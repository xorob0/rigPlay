package com.shilapi.xcertplay.media

import android.util.Log
import com.shilapi.xcertplay.airplay.AudioCodecKind
import com.shilapi.xcertplay.airplay.AudioFormat
import com.shilapi.xcertplay.airplay.AudioStreamId
import com.shilapi.xcertplay.airplay.MediaSink
import com.shilapi.xcertplay.simhub.AudioFormat as WireFormat
import com.shilapi.xcertplay.simhub.AudioStream as PcStream
import com.shilapi.xcertplay.simhub.SimHubAudioTransport
import com.shilapi.xcertplay.simhub.SimHubDiscovery
import com.shilapi.xcertplay.simhub.SimHubEndpoints
import java.io.Closeable
import java.net.InetSocketAddress
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong

/**
 * The audio half of a [MediaSink] that plays CarPlay audio on the PC through the SimHub plugin
 * (`docs/protocol.md` §10). Video, microphone and iAP2 callbacks are ignored: [SwitchingMediaSink]
 * sends those to the tablet's [AndroidMediaSink].
 *
 * Each CarPlay audio stream gets a sender with its own audio-priority thread. The engine's receive thread
 * only puts RTP packets into the sender's [DropOldestQueue] (the oldest is dropped and counted when the PC
 * side falls behind). The sender decodes them ([PcmDecoder]), converts the PCM to s16le at a header-legal
 * rate ([PcmConversion], [LinearResampler] for 11025/22050), cuts it into 5 ms datagrams
 * ([PcmPacketizer]) and hands them to the [SimHubAudioTransport]. It sends `audioStart` before the
 * first datagram, again after a format change or a reconnect (§10.1), and `audioStop` when CarPlay
 * stops the stream or nothing arrived for [idleStopMillis].
 *
 * The wire format is the plugin's choice ([SimHubAudioTransport.audioFormat], §6.6): PCM unless the plugin
 * lists `opus` first. For Opus (§10.4) the PCM is resampled to a rate the encoder takes (44.1 kHz → 48 kHz)
 * and goes through an [OpusPacketizer] over the device's encoder ([opusEncoderFactory]); when the device has
 * no Opus encoder the sink falls back to PCM for good and counts it. A plugin that changes its list
 * mid-stream gets a new `audioStart` in the new format.
 *
 * The datagram timestamp is the sample clock the PC plays by, so audio that never reaches the packetizer
 * must still advance it (§10.2): the sender follows the RTP timestamps and declares every gap (a packet
 * lost between phone and tablet, dropped from a full queue, or refused by the decoder) to the packetizer,
 * which jumps the clock. The PC then plays that much silence where the audio was, and its buffer keeps its
 * depth. Without this the stream arrives contiguous but short of real time, and the PC's buffer drains
 * into an underrun every few seconds. For AAC the decoder emits a packet's PCM about one packet late, so a
 * gap is placed up to one packet (21 ms) early; close enough for what it is.
 *
 * While any stream is announced the optional [wifiLock] is held (see `WifiLowLatencyLock`): Wi-Fi power
 * save and background scans otherwise hold datagrams back in bursts the PC hears as dropouts. A
 * [SendStallMonitor] per stream times every `socket.send` and the gaps between them and logs, every 10 s in
 * which something stalled, whether the radio held the datagrams back or the tablet produced them late: the
 * line to read next to the plugin page's underruns.
 *
 * CarPlay streams map to the protocol's three streams by audio type: telephony → `telephony`, Siri
 * and alternate audio (guidance, alerts) → `alt`, music → `media`. A stream whose protocol stream is
 * taken by another live CarPlay stream borrows a free one; with none free it is not sent.
 *
 * No Android audio is played, so no audio focus is requested.
 */
class NetworkAudioSink(
    private val transport: () -> SimHubAudioTransport? = { SimHubEndpoints.audioTransport },
    private val decoderFactory: (AudioFormat) -> PcmDecoder = PcmDecoder::forFormat,
    private val queueCapacity: Int = DEFAULT_QUEUE_PACKETS,
    private val idleStopMillis: Long = DEFAULT_IDLE_STOP_MILLIS,
    private val wifiLock: SimHubDiscovery.NetworkLock? = null,
    private val opusEncoderFactory: (sampleRate: Int, channels: Int) -> OpusFrameEncoder = { rate, channels -> MediaCodecOpusEncoder(rate, channels) },
    private val log: (String) -> Unit = { Log.i(TAG, it) },
) : MediaSink, Closeable {
    /** Totals since the sink was created. */
    data class Stats(
        val datagramsSent: Long,
        val queueDrops: Long,
        val sendFailures: Long,
        val noTargetDrops: Long,
        val decodeErrors: Long,
        val unassignedStreams: Long,
        /** Packets the decoder refused after waiting; their time went out as a timestamp jump. */
        val decoderDrops: Long = 0L,
        /** Timestamp jumps declared to the PC (§10.2): each one is audio that never reached the packetizer. */
        val timestampSkips: Long = 0L,
        /** Times the plugin asked for Opus and this device could not encode it, so PCM was sent instead (§10.4). */
        val opusFallbacks: Long = 0L,
    )

    private val datagramsSent = AtomicLong()
    private val queueDrops = AtomicLong()
    private val sendFailures = AtomicLong()
    private val noTargetDrops = AtomicLong()
    private val decodeErrors = AtomicLong()
    private val unassignedStreams = AtomicLong()
    private val decoderDrops = AtomicLong()
    private val timestampSkips = AtomicLong()
    private val opusFallbacks = AtomicLong()
    private val announcedStreams = AtomicInteger()

    /** Set once an Opus encoder could not be created on this device: every stream then sends PCM. */
    @Volatile private var opusUnavailable = false

    private val senders = ConcurrentHashMap<AudioStreamId, StreamSender>()
    private val assignmentLock = Any()
    private val holders = HashMap<PcStream, StreamSender>()
    private val lastHolders = HashMap<PcStream, StreamSender>()

    val stats: Stats
        get() = Stats(
            datagramsSent.get(), queueDrops.get(), sendFailures.get(),
            noTargetDrops.get(), decodeErrors.get(), unassignedStreams.get(),
            decoderDrops.get(), timestampSkips.get(), opusFallbacks.get(),
        )

    /** The format the next datagrams go out in: what the plugin prefers, PCM when it prefers Opus this device cannot encode. */
    val wireFormat: WireFormat
        get() {
            val preferred = transport()?.audioFormat ?: return WireFormat.PCM_S16LE
            return if (preferred == WireFormat.OPUS && opusUnavailable) WireFormat.PCM_S16LE else preferred
        }

    /** True while at least one stream is announced to the PC (and the Wi-Fi lock, if any, is held). */
    val streaming: Boolean get() = announcedStreams.get() > 0

    private fun streamAnnounced() {
        if (announcedStreams.incrementAndGet() != 1) return
        wifiLock?.runCatching { acquire() }?.onFailure { log("PC audio: Wi-Fi lock not acquired: $it") }
    }

    private fun streamEnded() {
        if (announcedStreams.decrementAndGet() != 0) return
        wifiLock?.runCatching { release() }?.onFailure { log("PC audio: Wi-Fi lock not released: $it") }
    }

    /** Protocol stream each live CarPlay stream is sent on. */
    val assignments: Map<AudioStreamId, PcStream>
        get() = senders.mapValues { it.value.stream }

    override fun onAudioStarted(id: AudioStreamId, format: AudioFormat, firstSample: Int) {
        sender(id, format)
    }

    override fun onAudioRtp(id: AudioStreamId, format: AudioFormat, rtp: ByteArray, sample: Int) {
        sender(id, format)?.submit(rtp, sample)
    }

    override fun onAudioStopped(id: AudioStreamId) {
        synchronized(assignmentLock) {
            val sender = senders.remove(id) ?: return
            holders.remove(sender.stream, sender)
            sender.stop()
        }
    }

    /** Ends every stream: buffered audio is sent and each started stream gets `audioStop`. */
    override fun close() {
        senders.keys.toList().forEach(::onAudioStopped)
    }

    private fun sender(id: AudioStreamId, format: AudioFormat): StreamSender? {
        senders[id]?.let { if (it.format == format) return it }
        synchronized(assignmentLock) {
            val existing = senders[id]
            if (existing != null) {
                if (existing.format == format) return existing
                senders.remove(id)
                holders.remove(existing.stream, existing)
                existing.stop()
            }
            val preferred = preferredStream(id)
            val stream = (listOf(preferred) + FALLBACK_ORDER).firstOrNull { it !in holders }
            if (stream == null) {
                if (unassignedStreams.getAndIncrement() == 0L) log("PC audio: no free stream for $id")
                return null
            }
            val predecessor = lastHolders[stream]?.takeUnless { it.finished }
            val sender = StreamSender(id, format, stream, predecessor)
            senders[id] = sender
            holders[stream] = sender
            lastHolders[stream] = sender
            if (stream != preferred) log("PC audio: $id sent as ${stream.wire}, ${preferred.wire} is busy")
            return sender
        }
    }

    private data class Announcement(
        val transport: SimHubAudioTransport,
        val epoch: Long,
        val target: InetSocketAddress,
        val sampleRate: Int,
        val channels: Int,
        val format: WireFormat,
    )

    private sealed class Item {
        class Packet(val rtp: ByteArray, val sample: Int) : Item()
        object Stop : Item()
    }

    private inner class StreamSender(
        val id: AudioStreamId,
        val format: AudioFormat,
        val stream: PcStream,
        private val predecessor: StreamSender?,
    ) {
        private val queue = DropOldestQueue<Item>(queueCapacity)
        private val workerLock = Any()
        private var worker: Thread? = null
        @Volatile private var stopping = false
        private val done = CountDownLatch(1)

        // Confined to the worker thread.
        private var predecessorAwaited = false
        private var decoder: PcmDecoder? = null
        private var resampler: LinearResampler? = null
        private var packetizer: AudioPacketizer? = null
        private var announced: Announcement? = null
        private var datagramsThisStream = 0L
        private val stallMonitor = SendStallMonitor(stream.wire)
        private val pcmSink = PcmSink { pcm, offset, length, chunk -> onPcm(pcm, offset, length, chunk) }

        // The source clock (RTP timestamps, in frames at format.sampleRate), to find audio that went missing.
        private var lastSample = -1L
        /** Frames the last packet carried when the payload says so (LPCM); the nominal step otherwise. */
        private var lastPacketFrames = 0L
        /** Smallest forward step between packets seen on this stream: the frames per packet of a compressed codec. */
        private var nominalStep = 0L
        /** Source frames lost since the last datagram, to declare to the packetizer with the next PCM. */
        private var missingSourceFrames = 0L
        private var lastPacketNanos = 0L

        val finished: Boolean get() = done.count == 0L

        fun submit(rtp: ByteArray, sample: Int) {
            if (stopping) return
            if (queue.offer(Item.Packet(rtp, sample)) != null) queueDrops.incrementAndGet()
            ensureWorker()
        }

        fun stop() {
            stopping = true
            if (queue.offer(Item.Stop) != null) queueDrops.incrementAndGet()
            ensureWorker()
        }

        fun awaitFinished(timeoutMs: Long) {
            try {
                done.await(timeoutMs, TimeUnit.MILLISECONDS)
            } catch (_: InterruptedException) {
                Thread.currentThread().interrupt()
            }
        }

        private fun ensureWorker() = synchronized(workerLock) {
            if (worker == null) {
                worker = Thread(::run, "rigplay-pc-audio-${stream.wire}").apply {
                    isDaemon = true
                    start()
                }
            }
        }

        private fun run() {
            var ended = false
            try {
                // The output clock is the PC's; this thread must not lose its turn to the video decoder or the UI.
                runCatching { android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_AUDIO) }
                if (!predecessorAwaited) {
                    // The previous holder's audioStop must reach the plugin before this stream's audioStart.
                    predecessor?.awaitFinished(PREDECESSOR_WAIT_MILLIS)
                    predecessorAwaited = true
                }
                lastPacketNanos = System.nanoTime()
                while (true) {
                    // A decoder finishes a packet after decode() returned, and an Opus encoder returns a packet a
                    // call late: poll briefly to send what is ready as soon as it is ready instead of with the next
                    // packet (or never, for the last packet before a pause).
                    val wait = if (decoder != null || packetizer != null) DRAIN_POLL_MILLIS else idleStopMillis
                    when (val item = queue.poll(wait)) {
                        null -> {
                            val idleMillis = (System.nanoTime() - lastPacketNanos) / 1_000_000L
                            if ((decoder != null || packetizer != null) && idleMillis < idleStopMillis) {
                                drainDecoder()
                                pollPacketizer()
                                continue
                            }
                            endStream("idle")
                            break
                        }
                        is Item.Stop -> {
                            endStream("stopped")
                            ended = true
                            break
                        }
                        is Item.Packet -> {
                            lastPacketNanos = System.nanoTime()
                            handle(item)
                        }
                    }
                }
            } catch (error: Throwable) {
                log("PC audio: sender ${stream.wire} failed ${error.javaClass.simpleName}: ${error.message}")
                runCatching { endStream("error") }
            } finally {
                if (ended) done.countDown()
                synchronized(workerLock) {
                    worker = null
                    if (!ended && !queue.isEmpty()) ensureWorker()
                }
            }
        }

        private fun handle(packet: Item.Packet) {
            noteSample(packet)
            val active = decoder ?: try {
                decoderFactory(format).also { decoder = it }
            } catch (error: Exception) {
                if (decodeErrors.getAndIncrement() == 0L) log("PC audio: no decoder for ${format.codec}: $error")
                return
            }
            val accepted = try {
                active.decode(packet.rtp, packet.sample, pcmSink)
            } catch (error: Exception) {
                decodeErrors.incrementAndGet()
                runCatching { active.close() }
                decoder = null
                false
            }
            if (!accepted) {
                // This packet's audio is gone: its time still has to pass on the PC.
                if (decoderDrops.getAndIncrement() == 0L) log("PC audio: decoder refused a ${format.codec} packet; its time is sent as silence")
                missingSourceFrames += lastPacketFrames
            }
        }

        private fun pollPacketizer() {
            val current = packetizer ?: return
            val link = announced?.transport ?: return
            current.poll { bytes, length -> deliver(link, bytes, length) }
        }

        private fun drainDecoder() {
            val active = decoder ?: return
            try {
                active.drain(pcmSink)
            } catch (error: Exception) {
                decodeErrors.incrementAndGet()
                runCatching { active.close() }
                decoder = null
            }
        }

        /**
         * Follows the RTP timestamp. A forward step larger than the frames the previous packet carried is audio
         * that never got here (lost upstream or dropped from the queue): it is declared to the packetizer with the
         * next PCM. A step back or a repeat (reorder, duplicate, the phone restarting its clock) is not a gap.
         * Steps beyond [MAX_GAP_SECONDS] are a new clock, not a pause: the PC would skip ahead anyway.
         */
        private fun noteSample(packet: Item.Packet) {
            val sample = packet.sample.toLong() and 0xffff_ffffL
            val carried = framesIn(packet.rtp)
            if (lastSample >= 0) {
                val step = (sample - lastSample) and 0xffff_ffffL
                if (step in 1 until MAX_GAP_SECONDS * format.sampleRate) {
                    if (carried == null && (nominalStep == 0L || step < nominalStep)) nominalStep = step
                    val expected = if (lastPacketFrames > 0) lastPacketFrames else nominalStep
                    if (expected > 0 && step > expected) missingSourceFrames += step - expected
                }
            }
            lastSample = sample
            lastPacketFrames = carried ?: nominalStep
        }

        /** Frames in a packet when the payload tells (LPCM); `null` for a compressed codec. */
        private fun framesIn(rtp: ByteArray): Long? =
            if (format.codec == AudioCodecKind.LPCM) ((rtp.size - RTP_HEADER_BYTES) / (2 * format.channels)).toLong().coerceAtLeast(0) else null

        private fun onPcm(pcm: ByteArray, offset: Int, length: Int, chunk: PcmChunkFormat) {
            if (chunk.channels < 1 || chunk.sampleRate <= 0) return
            var data = PcmConversion.toS16le(pcm, offset, length, chunk.encoding)
            val channels = minOf(chunk.channels, 2)
            data = PcmConversion.toAtMostStereo(data, chunk.channels)
            val format = wireFormat
            val rate = if (format == WireFormat.OPUS) MediaCodecOpusEncoder.wireRate(chunk.sampleRate) else PcmConversion.wireRate(chunk.sampleRate)
            if (rate != chunk.sampleRate) {
                val current = resampler?.takeIf {
                    it.inputRate == chunk.sampleRate && it.outputRate == rate && it.channels == channels
                } ?: LinearResampler(chunk.sampleRate, rate, channels).also { resampler = it }
                data = current.process(data)
            } else {
                resampler = null
            }
            if (data.isNotEmpty()) send(data, rate, channels, format)
        }

        private fun dropPacketizer() {
            packetizer?.let { runCatching { it.close() } }
            packetizer = null
        }

        private fun send(pcm: ByteArray, sampleRate: Int, channels: Int, wire: WireFormat) {
            val link = transport()
            val target = link?.audioTarget
            if (link == null || target == null) {
                // The plugin forgets the stream with the link (§10.1); announce it again when it is back.
                setAnnounced(null)
                dropPacketizer()
                noTargetDrops.incrementAndGet()
                return
            }
            val wanted = Announcement(link, link.audioEpoch, target, sampleRate, channels, wire)
            var current = packetizer
            if (announced != wanted || current == null) {
                val previous = announced
                if (previous != null && current != null && previous.copy(sampleRate = sampleRate, channels = channels, format = wire) == wanted) {
                    // Format change on the same session: the old format's tail goes out under its own header.
                    current.flush { bytes, length -> deliver(link, bytes, length) }
                }
                dropPacketizer()
                val next: AudioPacketizer = if (wire == WireFormat.OPUS) {
                    try {
                        OpusPacketizer(stream, opusEncoderFactory(sampleRate, channels))
                    } catch (error: Exception) {
                        // No Opus encoder on this device: PCM from the next chunk on, for every stream.
                        opusUnavailable = true
                        opusFallbacks.incrementAndGet()
                        log("PC audio: no Opus encoder (${error.javaClass.simpleName}: ${error.message}); sending PCM instead")
                        setAnnounced(null)
                        return
                    }
                } else {
                    PcmPacketizer(stream, sampleRate, channels)
                }
                if (!link.audioStart(stream, sampleRate, channels, wire)) {
                    setAnnounced(null)
                    runCatching { next.close() }
                    noTargetDrops.incrementAndGet()
                    return
                }
                log("PC audio: audioStart ${stream.wire} ${wire.wire} ${sampleRate}Hz x$channels for $id codec=${format.codec}")
                setAnnounced(wanted)
                current = next
                packetizer = current
                missingSourceFrames = 0L // a fresh clock: nothing to place a gap after
            }
            if (missingSourceFrames > 0) {
                val wireFrames = missingSourceFrames * sampleRate / format.sampleRate
                missingSourceFrames = 0L
                if (wireFrames > 0) {
                    current.skip(wireFrames) { bytes, length -> deliver(link, bytes, length) }
                    timestampSkips.incrementAndGet()
                }
            }
            current.push(pcm) { bytes, length -> deliver(link, bytes, length) }
        }

        private fun setAnnounced(next: Announcement?) {
            val previous = announced
            announced = next
            if (previous == null && next != null) streamAnnounced()
            if (previous != null && next == null) streamEnded()
        }

        private fun deliver(link: SimHubAudioTransport, bytes: ByteArray, length: Int) {
            stallMonitor.beforeSend(System.nanoTime())
            val sent = link.sendDatagram(bytes, length)
            val now = System.nanoTime()
            stallMonitor.afterSend(now)
            if (sent) {
                datagramsSent.incrementAndGet()
                datagramsThisStream++
            } else {
                sendFailures.incrementAndGet()
            }
            stallMonitor.report(now)?.let(log)
        }

        private fun endStream(reason: String) {
            val started = announced
            val link = transport()
            if (started != null && link != null && link === started.transport && link.audioEpoch == started.epoch) {
                if (link.audioTarget != null) {
                    drainDecoder()
                    packetizer?.flush { bytes, length -> deliver(link, bytes, length) }
                }
                link.audioStop(stream)
                log("PC audio: audioStop ${stream.wire} ($reason) datagrams=$datagramsThisStream drops=${queue.dropped} ${stallMonitor.summary(System.nanoTime())}")
            }
            setAnnounced(null)
            dropPacketizer()
            resampler = null
            datagramsThisStream = 0L
            lastSample = -1L
            lastPacketFrames = 0L
            nominalStep = 0L
            missingSourceFrames = 0L
            decoder?.let { runCatching { it.close() } }
            decoder = null
        }
    }

    companion object {
        const val TAG = "rigPlay-PcAudio"

        /** RTP packets buffered per stream: about 1.3 s of AAC (1024 frames per packet at 48 kHz). */
        const val DEFAULT_QUEUE_PACKETS = 64

        /** A stream that sends nothing for this long is stopped on the PC; it restarts with the next packet. */
        const val DEFAULT_IDLE_STOP_MILLIS = 3_000L

        private const val PREDECESSOR_WAIT_MILLIS = 500L

        /** How often an idle sender asks its decoder for PCM that became ready after the last packet. */
        private const val DRAIN_POLL_MILLIS = 20L

        /** A source-clock step longer than this is a new clock, not missing audio. */
        private const val MAX_GAP_SECONDS = 10L

        private const val RTP_HEADER_BYTES = 12

        private val FALLBACK_ORDER = listOf(PcStream.ALT, PcStream.MEDIA, PcStream.TELEPHONY)

        /** The protocol stream a CarPlay stream belongs on (§6.11). */
        fun preferredStream(id: AudioStreamId): PcStream = when (id.audioType.lowercase()) {
            "telephony" -> PcStream.TELEPHONY
            "speechrecognition" -> PcStream.ALT
            "media", "compatibility" -> PcStream.MEDIA
            "default", "alert" -> PcStream.ALT
            else -> if (id.type == AudioChannelMapper.STREAM_TYPE_MAIN_HIGH_AUDIO) PcStream.MEDIA else PcStream.ALT
        }
    }
}
