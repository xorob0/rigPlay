package com.shilapi.xcertplay.media

import android.util.Log
import com.shilapi.xcertplay.airplay.MicrophoneConfig
import com.shilapi.xcertplay.simhub.AudioDirection
import com.shilapi.xcertplay.simhub.AudioFormat
import com.shilapi.xcertplay.simhub.AudioStream
import com.shilapi.xcertplay.simhub.SimHubAudioCodec
import com.shilapi.xcertplay.simhub.SimHubEndpoints
import com.shilapi.xcertplay.simhub.SimHubMicJitterBuffer
import com.shilapi.xcertplay.simhub.SimHubMicTransport
import com.shilapi.xcertplay.simhub.SimHubProtocol
import java.io.Closeable
import java.net.BindException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.SocketException
import java.util.concurrent.atomic.AtomicBoolean

/** Where the phone's microphone comes from (setting "Microphone", persisted as `mic_source`). */
enum class MicrophoneSource(val key: String) {
    /** The PC's microphone through the SimHub plugin, while the link offers it; the tablet's otherwise. */
    PC("pc"),

    /** Always the tablet's own microphone. */
    TABLET("tablet"),
    ;

    companion object {
        val DEFAULT = TABLET

        fun fromKey(key: String?): MicrophoneSource = values().firstOrNull { it.key == key } ?: DEFAULT
    }
}

/** A PCM source for [MicrophoneUplink] in place of `AudioRecord`. */
internal interface MicrophonePcmSource : Closeable {
    /** For logs, e.g. "pc 192.168.1.20". */
    val description: String

    /** Starts the source; false when it cannot run (the uplink then records the tablet's microphone). */
    fun start(): Boolean

    /**
     * Like `AudioRecord.read(…, READ_BLOCKING)`: fills [length] bytes of little-endian s16 PCM at the uplink's rate
     * and channel count, paced in real time. Returns [length], or -1 once closed.
     */
    fun read(buffer: ByteArray, offset: Int, length: Int): Int
}

/**
 * The PC microphone as the phone's microphone (`docs/protocol.md` §6.13, §10.4). [start] binds UDP [port]
 * (23713; any free port if it is taken) and sends `micStart` with the uplink's [sampleRate]; a receiver thread takes
 * datagrams only from the paired host's address, decodes them in the plugin → tablet direction (`streamType` 4,
 * mono, the requested rate) and feeds a [SimHubMicJitterBuffer]. [read] hands the uplink exactly what it asks for,
 * at the pace of the sample clock, with silence on underrun, duplicated to stereo when the phone wants two
 * channels. [close] sends `micStop`. When the link comes back with a new Paired session while the phone still
 * listens, `micStart` is sent again.
 */
internal class NetworkMicrophoneSource(
    private val transport: SimHubMicTransport,
    private val sampleRate: Int,
    private val channels: Int,
    private val port: Int = SimHubProtocol.MIC_PORT,
    private val nanoTime: () -> Long = System::nanoTime,
    private val sleepNanos: (Long) -> Unit = ::sleepQuietly,
    private val openSocket: (Int) -> DatagramSocket = ::bindSocket,
    private val log: (String) -> Unit = { Log.i(TAG, it) },
) : MicrophonePcmSource {
    init {
        require(isValidRate(sampleRate)) { "sampleRate must be a multiple of 100 in 8000..48000" }
        require(channels == 1 || channels == 2) { "channels must be 1 or 2" }
    }

    private val running = AtomicBoolean(false)
    private val closed = AtomicBoolean(false)
    private val jitter = SimHubMicJitterBuffer(sampleRate)
    @Volatile private var socket: DatagramSocket? = null
    private var thread: Thread? = null
    @Volatile private var announcedEpoch = -1L
    private var startNs = 0L
    private var framesDelivered = 0L
    private var mono = ShortArray(0)

    /** Datagrams dropped: wrong source, invalid header, wrong format, or before the stream started. */
    @Volatile var rejected = 0L
        private set
    @Volatile var accepted = 0L
        private set

    /** The UDP port actually bound, once started. */
    @Volatile var boundPort = 0
        private set

    val buffer: SimHubMicJitterBuffer get() = jitter

    override val description: String get() = "pc ${transport.pairedHost?.hostAddress ?: "?"}:$boundPort"

    override fun start(): Boolean {
        if (closed.get() || !running.compareAndSet(false, true)) return running.get()
        val next = try {
            try {
                openSocket(port)
            } catch (busy: BindException) {
                log("Microphone: UDP $port is busy, using a free port")
                openSocket(0)
            }
        } catch (error: Exception) {
            log("Microphone: no UDP socket for the PC microphone (${error.javaClass.simpleName})")
            running.set(false)
            return false
        }
        socket = next
        boundPort = next.localPort
        thread = Thread({ receive(next) }, "simhub-mic").apply {
            isDaemon = true
            start()
        }
        announcedEpoch = transport.micEpoch
        if (!transport.micStart(sampleRate, boundPort)) {
            log("Microphone: micStart could not be sent; using the tablet microphone")
            shutdown(sendStop = false)
            return false
        }
        log("Microphone: from the PC, $sampleRate Hz mono on UDP $boundPort")
        return true
    }

    override fun read(buffer: ByteArray, offset: Int, length: Int): Int {
        if (closed.get()) return -1
        val frameBytes = 2 * channels
        val frames = length / frameBytes
        if (frames <= 0) return 0
        pace(frames)
        if (closed.get()) return -1
        reannounceIfNeeded()
        if (mono.size < frames) mono = ShortArray(frames)
        jitter.pull(mono, frames)
        var at = offset
        for (index in 0 until frames) {
            val sample = mono[index].toInt()
            repeat(channels) {
                buffer[at] = sample.toByte()
                buffer[at + 1] = (sample shr 8).toByte()
                at += 2
            }
        }
        return frames * frameBytes
    }

    /** Waits until the sample clock reaches the end of the next [frames], as a blocking recorder would. */
    private fun pace(frames: Int) {
        val now = nanoTime()
        if (framesDelivered == 0L) startNs = now
        val due = startNs + (framesDelivered + frames) * NANOS_PER_SECOND / sampleRate
        val wait = due - now
        if (wait > 0) {
            sleepNanos(wait)
        } else if (-wait > RESYNC_NANOS) {
            // The reader stalled (a GC, a slow encoder): restart the clock rather than racing to catch up.
            startNs = now - (framesDelivered + frames) * NANOS_PER_SECOND / sampleRate
        }
        framesDelivered += frames
    }

    /** A new Paired session: the plugin forgot the stream on link loss (§6.13), so ask again. */
    private fun reannounceIfNeeded() {
        val epoch = transport.micEpoch
        if (epoch == announcedEpoch || transport.pairedHost == null) return
        announcedEpoch = epoch
        jitter.reset()
        val sent = transport.micStart(sampleRate, boundPort)
        log("Microphone: link back, micStart ${if (sent) "sent again" else "not sent"}")
    }

    private fun receive(socket: DatagramSocket) {
        val data = ByteArray(MAX_DATAGRAM)
        val packet = DatagramPacket(data, data.size)
        while (running.get()) {
            try {
                packet.setData(data, 0, data.size)
                socket.receive(packet)
                onDatagram(data, packet.length, packet.address)
            } catch (_: SocketException) {
                break
            } catch (error: Exception) {
                if (running.get()) log("Microphone: receive failed (${error.javaClass.simpleName})")
                break
            }
        }
    }

    /** One datagram from [source]: §10.4 filtering, then the jitter buffer. */
    internal fun onDatagram(data: ByteArray, length: Int, source: InetAddress?) {
        val host = transport.pairedHost
        if (host == null || source == null || source != host) {
            rejected++
            return
        }
        val datagram = SimHubAudioCodec.decode(data, AudioDirection.PC_TO_TABLET, 0, length)
        if (datagram == null || datagram.header.stream != AudioStream.MIC || datagram.header.sampleRateHz != sampleRate ||
            datagram.header.channels != 1 || datagram.header.format != AudioFormat.PCM_S16LE
        ) {
            rejected++
            return
        }
        accepted++
        jitter.push(datagram.header.timestamp, datagram.header.start, datagram.samples())
    }

    override fun close() = shutdown(sendStop = true)

    private fun shutdown(sendStop: Boolean) {
        if (!closed.compareAndSet(false, true)) return
        val wasRunning = running.getAndSet(false)
        if (sendStop && wasRunning) {
            val sent = runCatching { transport.micStop() }.getOrDefault(false)
            log("Microphone: micStop ${if (sent) "sent" else "not sent (link down)"}; accepted=$accepted rejected=$rejected " +
                "late=${jitter.late} underruns=${jitter.underruns} skipped=${jitter.skipped}")
        }
        runCatching { socket?.close() }
        socket = null
        thread?.let { worker ->
            if (worker !== Thread.currentThread()) runCatching { worker.join(JOIN_MILLIS) }
        }
        thread = null
    }

    companion object {
        const val TAG = "rigPlay-Mic"
        private const val NANOS_PER_SECOND = 1_000_000_000L
        private const val RESYNC_NANOS = 200_000_000L
        private const val MAX_DATAGRAM = 12 + 8_192
        private const val JOIN_MILLIS = 500L

        fun isValidRate(rate: Int): Boolean = rate in 8_000..48_000 && rate % 100 == 0

        /**
         * The source for one uplink, or `null` to record the tablet's microphone: no SimHub link owner, the user chose
         * the tablet, the link is down or does not offer the microphone, or the phone wants a rate the protocol
         * cannot carry.
         */
        fun forUplink(config: MicrophoneConfig): MicrophonePcmSource? {
            val transport = SimHubEndpoints.microphone ?: return null
            if (!transport.micAvailable) return null
            if (!isValidRate(config.sampleRate) || config.channels !in 1..2) {
                Log.w(TAG, "Microphone: the phone wants ${config.sampleRate} Hz x${config.channels}; using the tablet microphone")
                return null
            }
            return NetworkMicrophoneSource(transport, config.sampleRate, config.channels)
        }

        private fun bindSocket(port: Int): DatagramSocket = DatagramSocket(null).apply {
            bind(InetSocketAddress(port))
        }

        private fun sleepQuietly(nanos: Long) {
            try {
                Thread.sleep(nanos / 1_000_000L, (nanos % 1_000_000L).toInt())
            } catch (_: InterruptedException) {
                Thread.currentThread().interrupt()
            }
        }
    }
}
