package com.shilapi.xcertplay.simhub

import java.io.BufferedReader
import java.io.Closeable
import java.io.IOException
import java.io.InputStreamReader
import java.io.OutputStream
import java.net.BindException
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

/**
 * A scripted stand-in for the SimHub plugin's control listener (§5), on 127.0.0.1.
 *
 * By default it answers `hello` with `welcome`, sends heartbeats every [heartbeatIntervalMs], and runs
 * the pairing rules of §8 against [validToken] and [expectedPin], sending [stateAfterPairing] after
 * `pairResult ok`. [stop]/[restart] simulate the PC going off and on (same port); [silent] simulates a
 * PC that vanished without closing the socket (no heartbeats, no replies).
 *
 * [stop] returns only once the listening socket is gone (the accept thread has left `accept()`), and
 * [start] retries the bind for a while, so a [restart] on the same port cannot fail with
 * `BindException` on a busy machine.
 */
class FakeSimHubServer(
    val hostId: String = HOST_ID,
    val hostName: String = "RIG-PC",
    @Volatile var validToken: String = TOKEN,
    @Volatile var expectedPin: String = PIN,
    @Volatile var issuedToken: String = ISSUED_TOKEN,
    private val heartbeatIntervalMs: Long = 100,
) : Closeable {
    /** Every message received from tablets, in order (heartbeats included). */
    val received = LinkedBlockingQueue<SimHubMessage>()

    /** Lines that did not decode to a known message. */
    val receivedUnparsed = LinkedBlockingQueue<String>()

    val accepted = AtomicInteger()

    @Volatile var silent = false
    @Volatile var autoWelcome = true
    @Volatile var welcomeFeatures: List<String> = listOf(SimHubProtocol.FEATURE_TELEMETRY, SimHubProtocol.FEATURE_IDLE_DASHBOARD)

    /** Replaces the default `hello` handling: returns the lines to send, e.g. an `unsupportedProtocol` error. */
    @Volatile var helloResponder: ((SimHubMessage.Hello) -> List<SimHubMessage>?)? = null

    @Volatile var stateAfterPairing: SimHubMessage.State = SimHubMessage.State(
        dashboardUrl = "http://127.0.0.1:8888/Dash#Pit%20Board",
        idleDashboardUrl = "http://127.0.0.1:8888/Dash#Rig%20Clock",
        dashboardServer = DashboardServer(reachable = true, port = 8888),
        audio = AudioSettings(enabled = true, port = SimHubProtocol.AUDIO_PORT, formats = listOf(SimHubProtocol.FORMAT_PCM_S16LE)),
    )

    private val connections = CopyOnWriteArrayList<Connection>()
    @Volatile private var server: ServerSocket? = null
    @Volatile private var acceptThread: Thread? = null

    var port: Int = 0
        private set

    val openConnections: Int get() = connections.count { !it.socket.isClosed }

    fun start(): FakeSimHubServer {
        val socket = bind(port)
        port = socket.localPort
        server = socket
        acceptThread = Thread({ acceptLoop(socket) }, "FakeSimHubServer-accept").apply {
            isDaemon = true
            start()
        }
        return this
    }

    /**
     * Binds 127.0.0.1:[requested] (0: any free port). SO_REUSEADDR lets a restart rebind over the old
     * connections in TIME_WAIT; the retry (up to [BIND_RETRY_MS]) covers a listener the kernel has not
     * released yet.
     */
    private fun bind(requested: Int): ServerSocket {
        val deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(BIND_RETRY_MS)
        while (true) {
            val socket = ServerSocket()
            try {
                socket.reuseAddress = true
                socket.bind(InetSocketAddress(InetAddress.getLoopbackAddress(), requested))
                return socket
            } catch (e: BindException) {
                socket.close()
                if (requested == 0 || System.nanoTime() >= deadline) throw e
                Thread.sleep(20)
            }
        }
    }

    /**
     * PC off: closes the listener and every connection (FIN, no `shutdown` error). Closing a
     * `ServerSocket` only signals a thread blocked in `accept()`; the kernel keeps the socket listening
     * until that thread has left the call, so wait for it before the port counts as free.
     */
    fun stop() {
        server?.close()
        server = null
        acceptThread?.let { if (it !== Thread.currentThread()) it.join(10_000) }
        acceptThread = null
        connections.forEach { it.close() }
        connections.clear()
    }

    /** PC on again, same port. */
    fun restart(): FakeSimHubServer {
        stop()
        silent = false
        return start()
    }

    override fun close() = stop()

    fun send(message: SimHubMessage) = sendLine(SimHubProtocol.encode(message))

    /** Sends [line] (a `\n` is appended) on every open connection. */
    fun sendLine(line: String) {
        connections.forEach { it.write(line) }
    }

    fun closeConnections() {
        connections.forEach { it.close() }
        connections.clear()
    }

    /** The next received message of type [T] (others are skipped), or `null` after [timeoutMs]. */
    inline fun <reified T : SimHubMessage> await(timeoutMs: Long = 10_000, noinline match: (T) -> Boolean = { true }): T? {
        val deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(timeoutMs)
        while (true) {
            val left = TimeUnit.NANOSECONDS.toMillis(deadline - System.nanoTime())
            if (left <= 0) return null
            val next = received.poll(left, TimeUnit.MILLISECONDS) ?: return null
            if (next is T && match(next)) return next
        }
    }

    private fun acceptLoop(socket: ServerSocket) {
        try {
            while (!socket.isClosed) {
                val client = socket.accept()
                if (socket.isClosed) {
                    client.close()
                    break
                }
                client.tcpNoDelay = true
                accepted.incrementAndGet()
                val connection = Connection(client)
                connections += connection
                Thread({ connection.readLoop() }, "FakeSimHubServer-conn").apply { isDaemon = true }.start()
            }
        } catch (_: IOException) {
        }
    }

    private inner class Connection(val socket: Socket) {
        private val out: OutputStream = socket.getOutputStream()
        @Volatile private var welcomed = false
        @Volatile private var paired = false

        fun write(line: String) {
            synchronized(this) {
                try {
                    out.write((line + "\n").toByteArray(Charsets.UTF_8))
                    out.flush()
                } catch (_: IOException) {
                }
            }
        }

        fun send(message: SimHubMessage) = write(SimHubProtocol.encode(message))

        fun close() {
            try {
                socket.close()
            } catch (_: IOException) {
            }
        }

        fun readLoop() {
            val reader = BufferedReader(InputStreamReader(socket.getInputStream(), Charsets.UTF_8))
            try {
                while (true) {
                    val line = reader.readLine() ?: break
                    val message = SimHubProtocol.parseOrNull(line)
                    if (message == null) {
                        receivedUnparsed += line
                        continue
                    }
                    received += message
                    if (!silent) respond(message)
                }
            } catch (_: IOException) {
            } finally {
                close()
            }
        }

        private fun respond(message: SimHubMessage) {
            when (message) {
                is SimHubMessage.Hello -> {
                    val custom = helloResponder?.invoke(message)
                    if (custom != null) {
                        custom.forEach(::send)
                        return
                    }
                    if (!autoWelcome) return
                    send(
                        SimHubMessage.Welcome(
                            hostId = hostId,
                            name = hostName,
                            version = "0.1.0",
                            simhubVersion = "9.12.6",
                            protocol = 1,
                            features = welcomeFeatures.filter { it in message.effectiveFeatures },
                        ),
                    )
                    welcomed = true
                    startHeartbeats()
                }
                is SimHubMessage.PairRequest -> {
                    val answer = pairAnswer(message)
                    send(answer)
                    // State follows pairResult ok (§6.6).
                    if (answer.ok) send(stateAfterPairing)
                }
                else -> Unit
            }
        }

        private fun pairAnswer(request: SimHubMessage.PairRequest): SimHubMessage.PairResult {
            val token = request.token
            val pin = request.pin
            val result = when {
                token != null && token == validToken -> SimHubMessage.PairResult(ok = true, token = token)
                token != null -> SimHubMessage.PairResult(ok = false, reason = PairFailure.TOKEN_INVALID)
                pin != null && pin == expectedPin -> SimHubMessage.PairResult(ok = true, token = issuedToken)
                pin != null -> SimHubMessage.PairResult(ok = false, reason = PairFailure.WRONG_PIN, attemptsLeft = 2)
                else -> SimHubMessage.PairResult(ok = false, reason = PairFailure.PIN_REQUIRED, pinExpiresInSec = 120)
            }
            if (result.ok) paired = true
            return result
        }

        private fun startHeartbeats() {
            Thread({
                var seq = 0L
                try {
                    while (!socket.isClosed) {
                        if (!silent) send(SimHubMessage.Heartbeat(seq++))
                        Thread.sleep(heartbeatIntervalMs)
                    }
                } catch (_: InterruptedException) {
                }
            }, "FakeSimHubServer-heartbeat").apply { isDaemon = true }.start()
        }
    }

    companion object {
        const val HOST_ID = "3f6c2a4e-8d1b-4c7a-9e55-0b2d7f1a6c90"
        const val TOKEN = "q3Z2b0x9V1mN8pR4sT6uW7yA5cE1gH3jK2lM0nO9pQ8"
        const val ISSUED_TOKEN = "Zr8VtYqPl0kMnBv3Cx7Hs2Jd9Fg4Aw6Ee1Ru5Ti0Op3"
        const val PIN = "048291"
        private const val BIND_RETRY_MS = 2_000L
    }
}
