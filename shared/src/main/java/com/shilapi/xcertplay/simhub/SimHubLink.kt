package com.shilapi.xcertplay.simhub

import android.util.Log
import java.io.BufferedOutputStream
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException
import java.util.Random
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit

/**
 * The tablet's end of the control channel (`docs/protocol.md` §5–§9): one object that knows whether
 * the plugin is reachable and exposes its state and commands.
 *
 * While started, the link connects to [Target], sends `hello`, checks `welcome`, pairs (Resume with
 * the stored token when [Target.hostId] matches `welcome.hostId`, otherwise Start once the caller
 * asked for it with [requestPairing], then Submit PIN through [pair]), sends `heartbeat` every
 * [Timing.heartbeatIntervalMs], declares the link lost when nothing arrives for
 * [Timing.linkLossTimeoutMs], and reconnects with exponential back-off (1, 2, 4, 8, then 10 s,
 * ±20 %, §9) until [stop].
 *
 * Threading: all [Listener] callbacks run on the link thread (`SimHubLink`), one at a time, never on
 * the caller's thread. A callback must not block for long: the watchdog runs on the same thread.
 * Writes happen on a per-session writer thread, so the public send methods never block on the
 * network. Every public method is safe to call from any thread, including from a callback.
 * After [stop] returns (when not called from a callback), no further callbacks are made.
 */
class SimHubLink(
    private val identity: Identity,
    private val listener: Listener,
    private val features: Set<String> = setOf(SimHubProtocol.FEATURE_IDLE_DASHBOARD),
    private val timing: Timing = Timing(),
    private val random: Random = Random(),
    private val log: (String) -> Unit = { Log.i(TAG, it) },
) {
    /** What `hello` says about this tablet (§6.1). */
    data class Identity(val tabletId: String, val name: String, val appVersion: String)

    /**
     * Where to connect, and the credentials for that host. [token] is only ever sent after a `welcome`
     * whose `hostId` equals [hostId] (§15); with no [hostId] the token is ignored.
     */
    data class Target(
        val host: String,
        val port: Int = SimHubProtocol.CONTROL_PORT,
        val hostId: String? = null,
        val token: String? = null,
    ) {
        override fun toString(): String =
            "Target(host=$host, port=$port, hostId=$hostId, token=${token?.let(SimHubProtocol::redactToken)})"
    }

    /** Spec defaults (§9); tests shorten them. */
    data class Timing(
        val heartbeatIntervalMs: Long = SimHubProtocol.HEARTBEAT_INTERVAL_MS,
        val linkLossTimeoutMs: Long = SimHubProtocol.LINK_LOSS_TIMEOUT_MS,
        val reconnectInitialDelayMs: Long = SimHubProtocol.RECONNECT_INITIAL_DELAY_MS,
        val reconnectMaxDelayMs: Long = SimHubProtocol.RECONNECT_MAX_DELAY_MS,
        val reconnectJitter: Double = SimHubProtocol.RECONNECT_JITTER,
        val connectTimeoutMs: Long = 5_000L,
        val statusMinIntervalMs: Long = SimHubProtocol.STATUS_MIN_INTERVAL_MS,
        val errorReplyMinIntervalMs: Long = 1_000L,
    ) {
        /** Granularity of the watchdog: at most 1/20 of the loss timeout, at most 250 ms. */
        internal val watchdogTickMs: Int get() = (linkLossTimeoutMs / 20).coerceIn(10L, 250L).toInt()
    }

    enum class LossReason {
        /** TCP connect failed or timed out. */
        CONNECT_FAILED,

        /** No `welcome` within the loss timeout. */
        HANDSHAKE_TIMEOUT,

        /** Nothing received for [Timing.linkLossTimeoutMs] (§9). */
        HEARTBEAT_TIMEOUT,

        /** TCP EOF. */
        CLOSED_BY_PEER,

        /** Socket error. */
        IO_ERROR,

        /** Peer sent `error shutdown`: a deliberate close (§9). Reconnection still applies. */
        PEER_SHUTDOWN,

        /** Peer sent another fatal `error`; [LinkLoss.errorCode] says which. */
        PEER_FATAL_ERROR,

        /** `unsupportedProtocol` or a `welcome` outside our version range; no automatic retry. */
        INCOMPATIBLE,

        /** `welcome.hostId` is not the paired host; the token was not sent. */
        HOST_MISMATCH,

        /** The peer sent a line longer than 65536 bytes; we sent `lineTooLong`. */
        LINE_TOO_LONG,
    }

    /**
     * Why a session ended. [retryInMs] is the delay before the next attempt, or `null` when the link
     * will not retry on its own ([LossReason.INCOMPATIBLE]).
     */
    data class LinkLoss(
        val reason: LossReason,
        val detail: String? = null,
        val errorCode: String? = null,
        val retryInMs: Long? = null,
    )

    /** Callbacks, all on the link thread. Every method has an empty default. */
    interface Listener {
        /** Any change of [state]: phase, session details, or a new `state` from the plugin. */
        fun onStateChanged(state: SimHubState) {}

        /** `pairResult ok`: the link is up (§1). The tablet may start CarPlay (#29). */
        fun onLinkUp(state: SimHubState) {}

        /** A link that was up went down. Stop audio streams; send `audioStart` again after the next [onLinkUp]. */
        fun onLinkLost(loss: LinkLoss) {}

        /** A connection attempt or a session that never reached Paired ended. */
        fun onAttemptFailed(loss: LinkLoss) {}

        /** Every `pairResult`, for the onboarding UI. */
        fun onPairResult(result: SimHubMessage.PairResult) {}

        /** Store [token] keyed by [hostId] (§8). Always store the value received. */
        fun onPaired(hostId: String, token: String) {}

        /** `tokenInvalid` or `forgotten`: delete the stored token for [hostId] and return to onboarding (§8). */
        fun onTokenRevoked(hostId: String, code: String) {}

        fun onCommand(command: SimHubCommand) {}

        fun onTelemetry(telemetry: SimHubMessage.Telemetry) {}

        /** Every `error` from the plugin, fatal or not. */
        fun onError(error: SimHubMessage.Error) {}
    }

    private val lifecycleLock = Any()
    private val wakeLock = Object()

    @Volatile private var generation = 0
    private var thread: Thread? = null
    private var session: Session? = null
    private var connectingSocket: Socket? = null

    @Volatile private var target: Target? = null
    @Volatile private var latestStatus: SimHubMessage.Status? = null
    @Volatile private var pairingRequested = false
    private var wakeRequested = false

    /** The plugin's version range from the last `unsupportedProtocol`, while halted. */
    @Volatile private var incompatibleRange: IntRange? = null

    @Volatile
    var state: SimHubState = SimHubState.STOPPED
        private set

    /** The current target including credentials learned from pairing, or `null` when never started. */
    val currentTarget: Target? get() = target

    /**
     * Number of sessions that reached Paired since this link was created. A change means the plugin
     * forgot every audio stream (§10.1), so they must be announced again.
     */
    @Volatile
    var pairedSessions: Long = 0L
        private set

    /**
     * Starts connecting to [target]. Starting again with the same target is a no-op; a different
     * target restarts the link.
     */
    fun start(target: Target) {
        synchronized(lifecycleLock) {
            if (thread != null && this.target == target) return
        }
        stop()
        synchronized(lifecycleLock) {
            if (thread != null) return
            this.target = target
            incompatibleRange = null
            val gen = ++generation
            state = SimHubState(phase = SimHubState.Phase.CONNECTING, host = target.host, controlPort = target.port)
            thread = Thread({ runLoop(gen) }, "SimHubLink").apply {
                isDaemon = true
                start()
            }
        }
        log("link started $target")
    }

    /** Sends `error shutdown` on an open session, closes it and stops reconnecting. */
    fun stop() {
        val worker: Thread
        val open: Session?
        val connecting: Socket?
        synchronized(lifecycleLock) {
            worker = thread ?: return
            thread = null
            open = session
            session = null
            // Queue `shutdown` before the link thread can notice the stop and close the session itself.
            open?.shutdown()
            generation++
            connecting = connectingSocket
            connectingSocket = null
        }
        wake()
        connecting?.closeQuietly()
        if (worker !== Thread.currentThread()) {
            worker.join(STOP_JOIN_MS)
            open?.closeNow()
        }
        state = SimHubState.STOPPED
        log("link stopped")
    }

    /**
     * Connects at once instead of waiting for the back-off, and resets it (§9). Also resumes a link
     * halted by [LossReason.INCOMPATIBLE] (the user chose to retry). No effect while connected.
     */
    fun reconnectNow() {
        incompatibleRange = null
        wake()
    }

    /**
     * Feeds a beacon from [SimHubDiscovery]. While disconnected, a beacon of the paired host makes the
     * link connect at once to the beacon's address and port and resets the back-off (§4.2, §9). The
     * new address is visible in [state] and [currentTarget] so the caller can store it.
     */
    fun onBeacon(host: DiscoveredHost) {
        val current = target ?: return
        if (current.hostId == null || current.hostId != host.hostId || !host.compatible) return
        val phase = state.phase
        if (phase != SimHubState.Phase.WAITING && phase != SimHubState.Phase.INCOMPATIBLE) return
        val halted = incompatibleRange
        if (halted != null && halted == host.minProtocol..host.protocol) return
        val address = host.address.hostAddress ?: return
        synchronized(lifecycleLock) {
            if (thread == null) return
            target = current.copy(host = address, port = host.controlPort)
        }
        incompatibleRange = null
        wake()
    }

    /**
     * Asks the plugin to show a PIN (`pairRequest` Start, §8 step 2), now if Unpaired or right after
     * the next `welcome`. Call again to retry after `pinExpired`, `tooManyAttempts` or `denied`.
     *
     * Without a token and without this call the session stays Unpaired; the plugin closes it after
     * 10 s (§9) and the link reconnects with back-off, so onboarding should call this promptly.
     */
    fun requestPairing() {
        pairingRequested = true
        val open = currentSession() ?: return
        if (open.phase == SimHubState.Phase.UNPAIRED) open.enqueue(SimHubMessage.PairRequest.START)
    }

    /** Submits the PIN the user typed (§8 step 3). Returns false unless a session is Unpaired. */
    fun pair(pin: String): Boolean {
        require(pin.length == 6 && pin.all { it in '0'..'9' }) { "pin must be 6 digits" }
        val open = currentSession() ?: return false
        if (open.phase != SimHubState.Phase.UNPAIRED) return false
        return open.enqueue(SimHubMessage.PairRequest.pin(pin))
    }

    /**
     * Sets the tablet's status (§6.7). The latest value is sent right after pairing and then on
     * change, at most once per [Timing.statusMinIntervalMs], trailing edge. Before the first call
     * the link reports [SimHubMessage.Status.IDLE].
     */
    fun send(status: SimHubMessage.Status) {
        latestStatus = status
        currentSession()?.statusChanged()
    }

    /** `audioStart` (§6.11). Returns false unless the link is up and `state.audio.enabled`. */
    fun sendAudioStart(
        stream: AudioStream,
        sampleRate: Int,
        channels: Int,
        format: AudioFormat = AudioFormat.PCM_S16LE,
    ): Boolean {
        require(sampleRate in 8_000..48_000 && sampleRate % 100 == 0) { "sampleRate must be a multiple of 100 in 8000..48000" }
        require(format != AudioFormat.OPUS || sampleRate in AudioFormat.OPUS_SAMPLE_RATES) { "opus takes 8, 12, 16, 24 or 48 kHz" }
        require(channels == 1 || channels == 2) { "channels must be 1 or 2" }
        require(stream != AudioStream.MIC) { "mic is not an audioStart stream; use sendMicStart" }
        if (!state.audioEnabled) return false
        return sendPaired(SimHubMessage.AudioStart(stream, format, sampleRate, channels))
    }

    /** `audioStop` (§6.12). Returns false unless the link is up. */
    fun sendAudioStop(stream: AudioStream): Boolean = sendPaired(SimHubMessage.AudioStop(stream))

    /**
     * `micStart` (§6.13): asks the plugin for the PC microphone, mono [sampleRate] Hz, to UDP [port] on this tablet.
     * Returns false unless [SimHubState.micAvailable] (link up, feature `mic`, `state.mic.enabled`).
     */
    fun sendMicStart(sampleRate: Int, port: Int = SimHubProtocol.MIC_PORT): Boolean {
        require(sampleRate in 8_000..48_000 && sampleRate % 100 == 0) { "sampleRate must be a multiple of 100 in 8000..48000" }
        require(port in 1..65_535) { "port must be 1..65535" }
        if (!state.micAvailable) return false
        return sendPaired(SimHubMessage.MicStart(sampleRate = sampleRate, port = port))
    }

    /** `micStop` (§6.13). Returns false unless the link is up with feature `mic`. */
    fun sendMicStop(): Boolean {
        if (!state.paired || !state.hasFeature(SimHubProtocol.FEATURE_MIC)) return false
        return sendPaired(SimHubMessage.MicStop())
    }

    /**
     * `artwork` (#47): the now-playing artwork as base64 [SimHubProtocol.MIME_JPEG] by default. Returns
     * false unless the link is up, or when the line would exceed [SimHubProtocol.MAX_LINE_BYTES].
     */
    fun sendArtwork(base64: String, mime: String = SimHubProtocol.MIME_JPEG): Boolean {
        val message = SimHubMessage.Artwork(mime, base64)
        if (SimHubProtocol.encodeLine(message).size > SimHubProtocol.MAX_LINE_BYTES) return false
        return sendPaired(message)
    }

    /** Answers a [SimHubCommand] that cannot be carried out now with `commandUnavailable` (§6.8). */
    fun sendCommandUnavailable(message: String? = null): Boolean {
        val open = currentSession() ?: return false
        if (open.phase != SimHubState.Phase.PAIRED) return false
        return open.sendErrorReply(SimHubProtocol.ERROR_COMMAND_UNAVAILABLE, message, SimHubProtocol.TYPE_COMMAND)
    }

    private fun sendPaired(message: SimHubMessage): Boolean {
        val open = currentSession() ?: return false
        if (open.phase != SimHubState.Phase.PAIRED) return false
        return open.enqueue(message)
    }

    private fun currentSession(): Session? = synchronized(lifecycleLock) { session }

    private fun isCurrent(gen: Int) = generation == gen

    private fun wake() {
        synchronized(wakeLock) {
            wakeRequested = true
            wakeLock.notifyAll()
        }
    }

    /** Waits up to [ms] (forever when `null`); true when woken by [wake] rather than by the timeout. */
    private fun sleep(gen: Int, ms: Long?): Boolean {
        val deadline = ms?.let { System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(it) }
        synchronized(wakeLock) {
            while (!wakeRequested && isCurrent(gen)) {
                if (deadline == null) {
                    wakeLock.wait()
                } else {
                    val left = TimeUnit.NANOSECONDS.toMillis(deadline - System.nanoTime())
                    if (left <= 0) return false
                    wakeLock.wait(left)
                }
            }
            val woken = wakeRequested
            wakeRequested = false
            return woken
        }
    }

    private fun backoffDelay(failures: Int): Long {
        val base = timing.reconnectInitialDelayMs.toDouble() * (1L shl failures.coerceAtMost(20))
        val capped = minOf(base, timing.reconnectMaxDelayMs.toDouble())
        val jitter = 1.0 + timing.reconnectJitter * (2 * random.nextDouble() - 1)
        return (capped * jitter).toLong().coerceAtLeast(0)
    }

    private fun publish(gen: Int, next: SimHubState) {
        if (!isCurrent(gen) || next == state) return
        state = next
        dispatch(gen) { it.onStateChanged(next) }
    }

    private inline fun dispatch(gen: Int, block: (Listener) -> Unit) {
        if (!isCurrent(gen)) return
        try {
            block(listener)
        } catch (error: RuntimeException) {
            log("listener failed: $error")
        }
    }

    // --- link thread ---------------------------------------------------------------------------

    private fun runLoop(gen: Int) {
        var failures = 0
        try {
            while (isCurrent(gen)) {
                synchronized(wakeLock) { wakeRequested = false }
                val result = runSession(gen) ?: break
                if (!isCurrent(gen)) break
                if (result.wasPaired) failures = 0
                val halt = result.loss.reason == LossReason.INCOMPATIBLE
                val delay = if (halt) null else backoffDelay(failures++)
                val loss = result.loss.copy(retryInMs = delay)
                log("session ended ${loss.reason} ${loss.detail ?: ""} retry=${delay ?: "none"}")
                val current = target
                publish(
                    gen,
                    SimHubState(
                        phase = if (halt) SimHubState.Phase.INCOMPATIBLE else SimHubState.Phase.WAITING,
                        host = current?.host,
                        controlPort = current?.port,
                        hostId = current?.hostId,
                    ),
                )
                if (result.wasPaired) dispatch(gen) { it.onLinkLost(loss) } else dispatch(gen) { it.onAttemptFailed(loss) }
                if (sleep(gen, delay)) failures = 0
            }
        } catch (_: InterruptedException) {
            // stop()
        }
    }

    private class SessionResult(val loss: LinkLoss, val wasPaired: Boolean)

    /** Runs one connection to completion. Returns `null` when stopped. */
    private fun runSession(gen: Int): SessionResult? {
        val target = target ?: return null
        publish(gen, SimHubState(phase = SimHubState.Phase.CONNECTING, host = target.host, controlPort = target.port, hostId = target.hostId))
        val socket = Socket()
        synchronized(lifecycleLock) {
            if (!isCurrent(gen)) return null
            connectingSocket = socket
        }
        try {
            socket.tcpNoDelay = true
            socket.connect(InetSocketAddress(target.host, target.port), timing.connectTimeoutMs.toInt())
            socket.soTimeout = timing.watchdogTickMs
        } catch (error: IOException) {
            socket.closeQuietly()
            if (!isCurrent(gen)) return null
            return SessionResult(LinkLoss(LossReason.CONNECT_FAILED, error.toString()), wasPaired = false)
        } finally {
            synchronized(lifecycleLock) { if (connectingSocket === socket) connectingSocket = null }
        }
        val open = Session(gen, socket, target)
        synchronized(lifecycleLock) {
            if (!isCurrent(gen)) {
                socket.closeQuietly()
                return null
            }
            session = open
        }
        return try {
            open.run()
        } finally {
            synchronized(lifecycleLock) { if (session === open) session = null }
            open.closeNow()
        }
    }

    /** One TCP session. [run] executes on the link thread; the writer has its own thread. */
    private inner class Session(private val gen: Int, private val socket: Socket, private val target: Target) {
        private val writer = Writer(
            output = socket.getOutputStream(),
            isPaired = { phase == SimHubState.Phase.PAIRED },
            onFinished = { graceful ->
                // FIN after the last line rather than close(): closing with unread input sends RST,
                // which makes the peer discard our final line (e.g. `shutdown`) before reading it.
                if (graceful) socket.shutdownOutputQuietly() else socket.closeQuietly()
            },
        )
        private val reader = LineReader(socket.getInputStream(), SimHubProtocol.MAX_LINE_BYTES)

        @Volatile var phase: SimHubState.Phase = SimHubState.Phase.CONNECTING
            private set

        private var runner: Thread? = null

        private var welcome: SimHubMessage.Welcome? = null
        private var negotiatedFeatures: Set<String> = emptySet()
        private var lastErrorReplyAt = Long.MIN_VALUE / 2
        private val errorReplyLock = Any()

        fun enqueue(message: SimHubMessage): Boolean = writer.offer(SimHubProtocol.encodeLine(message))

        fun statusChanged() = writer.statusChanged(immediate = false)

        fun shutdown() {
            writer.close(SimHubProtocol.encodeLine(SimHubMessage.Error(SimHubProtocol.ERROR_SHUTDOWN, "rigPlay is closing", fatal = true)))
        }

        /**
         * Lets the writer flush what is queued (a fatal error line) and send FIN, reads until the peer
         * closes (briefly, on the link thread only, so the close is not a RST), then closes.
         */
        fun closeNow() {
            writer.close(null)
            if (Thread.currentThread() !== writer) writer.join(WRITER_FLUSH_MS)
            if (Thread.currentThread() === runner && !socket.isClosed) reader.drainUntilEof(CLOSE_LINGER_MS)
            socket.closeQuietly()
        }

        /** Non-fatal error reply, at most one per [Timing.errorReplyMinIntervalMs] (§14.2). */
        fun sendErrorReply(code: String, message: String?, refType: String?): Boolean {
            synchronized(errorReplyLock) {
                val now = nowMs()
                if (now - lastErrorReplyAt < timing.errorReplyMinIntervalMs) return false
                lastErrorReplyAt = now
            }
            return enqueue(SimHubMessage.Error(code, message, refType = refType))
        }

        fun run(): SessionResult? {
            runner = Thread.currentThread()
            writer.start()
            enqueue(
                SimHubMessage.Hello(
                    tabletId = identity.tabletId,
                    name = identity.name,
                    appVersion = identity.appVersion,
                    protocol = SimHubProtocol.PROTOCOL_VERSION,
                    minProtocol = SimHubProtocol.MIN_PROTOCOL_VERSION,
                    features = features.toList(),
                ),
            )
            var lastLineAt = nowMs()
            var stoppedAt = 0L
            while (true) {
                val line = try {
                    reader.readLine()
                } catch (_: SocketTimeoutException) {
                    if (!isCurrent(gen)) {
                        // stop(): `shutdown` was queued; give the peer a moment to close first.
                        if (stoppedAt == 0L) stoppedAt = nowMs()
                        if (nowMs() - stoppedAt >= CLOSE_LINGER_MS) return null
                    }
                    if (nowMs() - lastLineAt >= timing.linkLossTimeoutMs) {
                        val reason = if (welcome == null) LossReason.HANDSHAKE_TIMEOUT else LossReason.HEARTBEAT_TIMEOUT
                        return end(LinkLoss(reason, "no line for ${timing.linkLossTimeoutMs} ms"))
                    }
                    continue
                } catch (_: LineTooLongException) {
                    writer.close(
                        SimHubProtocol.encodeLine(
                            SimHubMessage.Error(SimHubProtocol.ERROR_LINE_TOO_LONG, "line exceeds 65536 bytes", fatal = true),
                        ),
                    )
                    return end(LinkLoss(LossReason.LINE_TOO_LONG))
                } catch (error: IOException) {
                    return end(LinkLoss(LossReason.IO_ERROR, error.toString()))
                } ?: return end(LinkLoss(LossReason.CLOSED_BY_PEER))
                lastLineAt = nowMs()
                if (!isCurrent(gen)) return null
                if (line.isEmpty()) continue
                handle(line)?.let { return end(it) }
            }
        }

        private fun end(loss: LinkLoss): SessionResult? {
            if (!isCurrent(gen)) return null
            return SessionResult(loss, wasPaired = phase == SimHubState.Phase.PAIRED)
        }

        /** Returns a loss when the line ends the session. */
        private fun handle(line: String): LinkLoss? {
            val message = when (val parsed = SimHubProtocol.parse(line)) {
                is SimHubParseResult.Ok -> parsed.message
                is SimHubParseResult.Unknown -> {
                    log("ignored unknown type ${parsed.type}")
                    return null
                }
                is SimHubParseResult.Malformed -> {
                    log("ignored malformed line (${parsed.refType ?: "no type"}): ${parsed.reason}")
                    val refType = parsed.refType
                    if (refType != null && refType != SimHubProtocol.TYPE_ERROR) {
                        sendErrorReply(SimHubProtocol.ERROR_BAD_MESSAGE, parsed.reason, refType)
                    }
                    return null
                }
            }
            return when (message) {
                is SimHubMessage.Welcome -> onWelcome(message)
                is SimHubMessage.PairResult -> {
                    onPairResult(message)
                    null
                }
                is SimHubMessage.Heartbeat -> null
                is SimHubMessage.State -> {
                    if (phase == SimHubState.Phase.PAIRED) {
                        publish(
                            gen,
                            state.copy(
                                dashboardUrl = message.dashboardUrl,
                                idleDashboardUrl = message.idleDashboardUrl,
                                dashboardServer = message.dashboardServer,
                                audio = message.audio,
                                mic = message.mic,
                            ),
                        )
                    }
                    null
                }
                is SimHubMessage.Command -> {
                    if (phase == SimHubState.Phase.PAIRED) dispatch(gen) { it.onCommand(message.command) }
                    null
                }
                is SimHubMessage.Telemetry -> {
                    if (phase == SimHubState.Phase.PAIRED) dispatch(gen) { it.onTelemetry(message) }
                    null
                }
                is SimHubMessage.Error -> onError(message)
                is SimHubMessage.Beacon,
                is SimHubMessage.Hello,
                is SimHubMessage.PairRequest,
                is SimHubMessage.Status,
                is SimHubMessage.AudioStart,
                is SimHubMessage.AudioStop,
                is SimHubMessage.MicStart,
                is SimHubMessage.MicStop,
                is SimHubMessage.Artwork -> {
                    sendErrorReply(SimHubProtocol.ERROR_UNEXPECTED_MESSAGE, "${message.type} is tablet to plugin", message.type)
                    null
                }
            }
        }

        private fun onWelcome(message: SimHubMessage.Welcome): LinkLoss? {
            if (welcome != null) {
                sendErrorReply(SimHubProtocol.ERROR_UNEXPECTED_MESSAGE, "second welcome", message.type)
                return null
            }
            if (message.protocol !in SimHubProtocol.MIN_PROTOCOL_VERSION..SimHubProtocol.PROTOCOL_VERSION) {
                incompatibleRange = message.protocol..message.protocol
                return LinkLoss(LossReason.INCOMPATIBLE, "welcome protocol ${message.protocol}")
            }
            if (target.hostId != null && target.hostId != message.hostId) {
                return LinkLoss(LossReason.HOST_MISMATCH, "expected ${target.hostId}, got ${message.hostId}")
            }
            welcome = message
            negotiatedFeatures = message.features.filterTo(LinkedHashSet()) { it in features }
            phase = SimHubState.Phase.UNPAIRED
            writer.startHeartbeats()
            publish(gen, sessionState(SimHubState.Phase.UNPAIRED))
            val token = this@SimHubLink.target?.takeIf { it.hostId == message.hostId }?.token
            when {
                token != null -> enqueue(SimHubMessage.PairRequest.resume(token))
                pairingRequested -> enqueue(SimHubMessage.PairRequest.START)
                else -> log("connected to ${message.name}; waiting for requestPairing()")
            }
            return null
        }

        private fun onPairResult(result: SimHubMessage.PairResult) {
            val accepted = welcome
            if (accepted == null || phase != SimHubState.Phase.UNPAIRED) {
                log("ignored pairResult in $phase")
                return
            }
            log("pairResult $result")
            if (result.ok) {
                val token = result.token ?: return
                phase = SimHubState.Phase.PAIRED
                pairingRequested = false
                pairedSessions++
                updateTarget { it.copy(hostId = accepted.hostId, token = token) }
                writer.statusChanged(immediate = true)
                publish(gen, sessionState(SimHubState.Phase.PAIRED))
                dispatch(gen) { it.onPairResult(result) }
                dispatch(gen) { it.onPaired(accepted.hostId, token) }
                val up = state
                dispatch(gen) { it.onLinkUp(up) }
            } else {
                if (result.reason == PairFailure.TOKEN_INVALID) {
                    updateTarget { it.copy(token = null) }
                    dispatch(gen) { it.onTokenRevoked(accepted.hostId, PairFailure.TOKEN_INVALID.wire) }
                }
                publish(gen, state.copy(lastPairResult = result))
                dispatch(gen) { it.onPairResult(result) }
            }
        }

        private fun onError(error: SimHubMessage.Error): LinkLoss? {
            log("peer error ${error.code} fatal=${error.isFatal} ${error.message ?: ""}")
            dispatch(gen) { it.onError(error) }
            val fatal = error.isFatal || error.code in FATAL_CODES
            if (!fatal) return null
            return when (error.code) {
                SimHubProtocol.ERROR_SHUTDOWN -> LinkLoss(LossReason.PEER_SHUTDOWN, error.message, error.code)
                SimHubProtocol.ERROR_UNSUPPORTED_PROTOCOL -> {
                    incompatibleRange = (error.minProtocol ?: 0)..(error.maxProtocol ?: 0)
                    LinkLoss(LossReason.INCOMPATIBLE, error.message, error.code)
                }
                SimHubProtocol.ERROR_FORGOTTEN -> {
                    val hostId = welcome?.hostId ?: target.hostId
                    updateTarget { it.copy(token = null) }
                    if (hostId != null) dispatch(gen) { it.onTokenRevoked(hostId, SimHubProtocol.ERROR_FORGOTTEN) }
                    LinkLoss(LossReason.PEER_FATAL_ERROR, error.message, error.code)
                }
                else -> LinkLoss(LossReason.PEER_FATAL_ERROR, error.message, error.code)
            }
        }

        private fun updateTarget(change: (Target) -> Target) {
            synchronized(lifecycleLock) {
                if (!isCurrent(gen)) return
                this@SimHubLink.target = this@SimHubLink.target?.let(change)
            }
        }

        private fun sessionState(phase: SimHubState.Phase): SimHubState {
            val accepted = welcome
            return SimHubState(
                phase = phase,
                host = target.host,
                controlPort = target.port,
                hostId = accepted?.hostId,
                hostName = accepted?.name,
                hostVersion = accepted?.version,
                simhubVersion = accepted?.simhubVersion,
                protocol = accepted?.protocol,
                features = negotiatedFeatures,
            )
        }
    }

    /** Writes queued lines, heartbeats and coalesced status on its own thread. */
    private inner class Writer(
        output: OutputStream,
        private val isPaired: () -> Boolean,
        private val onFinished: (graceful: Boolean) -> Unit,
    ) : Thread("SimHubLink-writer") {
        private val out = BufferedOutputStream(output)
        private val queue = LinkedBlockingQueue<Any>()
        private val signal = Any()

        @Volatile private var closed = false
        @Volatile private var heartbeats = false
        @Volatile private var statusPending = false
        @Volatile private var statusImmediate = false
        private var nextHeartbeatAt = 0L
        private var lastStatusAt = Long.MIN_VALUE / 2
        private var seq = 0L

        init {
            isDaemon = true
        }

        fun offer(line: ByteArray): Boolean = !closed && queue.offer(line)

        fun startHeartbeats() {
            heartbeats = true
            queue.offer(signal)
        }

        /** [immediate]: right after `pairResult ok`, ignoring the coalescing interval. */
        fun statusChanged(immediate: Boolean) {
            if (immediate) statusImmediate = true
            statusPending = true
            queue.offer(signal)
        }

        /** Writes what is queued, then [finalLine] (if any), then shuts the output down (FIN). */
        fun close(finalLine: ByteArray?) {
            if (closed) return
            closed = true
            queue.offer(Close(finalLine))
        }

        override fun run() {
            var graceful = false
            try {
                while (true) {
                    val now = nowMs()
                    var waitMs = Long.MAX_VALUE
                    if (heartbeats) {
                        if (nextHeartbeatAt == 0L) nextHeartbeatAt = now
                        waitMs = minOf(waitMs, nextHeartbeatAt - now)
                    }
                    val sendStatus = statusPending && isPaired()
                    if (sendStatus) {
                        val due = if (statusImmediate) now else lastStatusAt + timing.statusMinIntervalMs
                        waitMs = minOf(waitMs, due - now)
                    }
                    val item = if (waitMs <= 0) queue.poll() else queue.poll(waitMs.coerceAtMost(60_000L), TimeUnit.MILLISECONDS)
                    when (item) {
                        is ByteArray -> out.write(item)
                        is Close -> {
                            drainLines()
                            item.line?.let(out::write)
                            out.flush()
                            graceful = true
                            break
                        }
                    }
                    val after = nowMs()
                    if (heartbeats && after >= nextHeartbeatAt) {
                        out.write(SimHubProtocol.encodeLine(SimHubMessage.Heartbeat(seq)))
                        seq = (seq + 1) and 0xFFFF_FFFFL
                        nextHeartbeatAt = maxOf(nextHeartbeatAt + timing.heartbeatIntervalMs, after)
                    }
                    if (statusPending && isPaired() &&
                        (statusImmediate || after - lastStatusAt >= timing.statusMinIntervalMs)
                    ) {
                        statusPending = false
                        statusImmediate = false
                        lastStatusAt = after
                        out.write(SimHubProtocol.encodeLine(latestStatus ?: SimHubMessage.Status.IDLE))
                    }
                    if (queue.isEmpty()) out.flush()
                }
            } catch (_: IOException) {
                // The reader sees the closed socket and reports the loss.
            } catch (_: InterruptedException) {
            } finally {
                closed = true
                try {
                    out.flush()
                } catch (_: IOException) {
                }
                onFinished(graceful)
            }
        }

        private fun drainLines() {
            while (true) {
                val next = queue.poll() ?: return
                if (next is ByteArray) out.write(next)
            }
        }
    }

    private class Close(val line: ByteArray?)

    private fun nowMs(): Long = System.nanoTime() / 1_000_000L

    companion object {
        const val TAG = "rigplay-simhub-link"
        private const val STOP_JOIN_MS = 1_000L
        private const val WRITER_FLUSH_MS = 500L
        private const val CLOSE_LINGER_MS = 300L

        /** Codes §14.1 marks fatal; treated as fatal even when the `fatal` member is missing. */
        private val FATAL_CODES = setOf(
            SimHubProtocol.ERROR_UNSUPPORTED_PROTOCOL,
            SimHubProtocol.ERROR_HELLO_REQUIRED,
            SimHubProtocol.ERROR_LINE_TOO_LONG,
            SimHubProtocol.ERROR_REPLACED,
            SimHubProtocol.ERROR_FORGOTTEN,
            SimHubProtocol.ERROR_SHUTDOWN,
        )
    }
}

internal class LineTooLongException : IOException("line too long")

/**
 * Reads `\n`-terminated UTF-8 lines with a length limit (§5.1). A read timeout leaves the partial
 * line buffered, so the caller can poll with `SO_TIMEOUT` without losing bytes.
 */
internal class LineReader(private val input: InputStream, private val maxBytes: Int) {
    private val buffer = ByteArray(8192)
    private var position = 0
    private var limit = 0
    private val line = ByteArrayOutputStream()

    /** The next line without terminator (one `\r` stripped), or `null` at end of stream. */
    fun readLine(): String? {
        while (true) {
            if (position == limit) {
                val count = input.read(buffer)
                if (count < 0) return null
                position = 0
                limit = count
            }
            var index = position
            while (index < limit && buffer[index] != '\n'.code.toByte()) index++
            line.write(buffer, position, index - position)
            if (index < limit) {
                position = index + 1
                var bytes = line.toByteArray()
                line.reset()
                if (bytes.isNotEmpty() && bytes.last() == '\r'.code.toByte()) bytes = bytes.copyOf(bytes.size - 1)
                if (bytes.size > maxBytes) throw LineTooLongException()
                return String(bytes, Charsets.UTF_8)
            }
            position = limit
            if (line.size() > maxBytes + 1) throw LineTooLongException()
        }
    }

    /** Discards input until end of stream, an error, or [timeoutMs]. */
    fun drainUntilEof(timeoutMs: Long) {
        val deadline = System.nanoTime() + timeoutMs * 1_000_000L
        while (System.nanoTime() < deadline) {
            try {
                if (input.read(buffer) < 0) return
            } catch (_: SocketTimeoutException) {
            } catch (_: IOException) {
                return
            }
        }
    }
}

private fun Socket.shutdownOutputQuietly() {
    try {
        if (!isClosed && !isOutputShutdown) shutdownOutput()
    } catch (_: IOException) {
    }
}

private fun Socket.closeQuietly() {
    try {
        close()
    } catch (_: IOException) {
    }
}
