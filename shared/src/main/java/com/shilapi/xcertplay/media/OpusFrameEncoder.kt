package com.shilapi.xcertplay.media

import android.media.MediaCodec
import android.media.MediaFormat
import java.io.Closeable

/**
 * Encodes fixed frames of s16le PCM into Opus packets for the PC audio stream (`docs/protocol.md` §10.4).
 * [MediaCodecOpusEncoder] is the Android implementation; tests plug in a fake.
 */
interface OpusFrameEncoder : Closeable {
    val sampleRate: Int
    val channels: Int

    /** Milliseconds of PCM per [encode] call and per packet out. */
    val frameMillis: Int

    /** Sample frames per packet. */
    val frameSize: Int get() = sampleRate * frameMillis / 1000

    /**
     * Queues one frame ([frameSize] frames of PCM at [offset]) and returns the packets that are ready: usually
     * the one for the previous frame, sometimes none yet, each exactly one frame of audio, in order.
     */
    fun encode(pcm: ByteArray, offset: Int, length: Int): List<ByteArray>

    /** Packets that became ready since the last call; waits up to [waitMillis] for ones still in flight. */
    fun drain(waitMillis: Long): List<ByteArray>

    /** Frames queued and not yet returned as packets. */
    val inFlight: Long get() = 0L
}

/**
 * [OpusFrameEncoder] on Android's Opus encoder (`c2.android.opus.encoder`, API 29+ on every device, software).
 * Takes 8, 12, 16, 24 or 48 kHz, 1 or 2 channels. One 20 ms frame per input buffer, so each output buffer is
 * one packet; the codec config buffer (OpusHead) that comes first is dropped, the wire needs none. Throws from
 * the constructor when the device has no encoder; the sender then falls back to PCM.
 */
class MediaCodecOpusEncoder(
    override val sampleRate: Int,
    override val channels: Int,
    bitrate: Int = defaultBitrate(channels),
) : OpusFrameEncoder {
    override val frameMillis: Int get() = FRAME_MILLIS

    private val codec: MediaCodec
    private val info = MediaCodec.BufferInfo()
    private var presentationUs = 0L
    private var queued = 0L
    private var produced = 0L
    private var closed = false

    init {
        require(sampleRate in OPUS_RATES) { "Opus takes 8, 12, 16, 24 or 48 kHz, not $sampleRate" }
        require(channels == 1 || channels == 2) { "channels must be 1 or 2" }
        val format = MediaFormat.createAudioFormat(MediaFormat.MIMETYPE_AUDIO_OPUS, sampleRate, channels).apply {
            setInteger(MediaFormat.KEY_BIT_RATE, bitrate)
            setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, frameSize * channels * 2)
            // A hint the software encoder honours; the default (10) is more CPU than a tablet needs to spend.
            setInteger(MediaFormat.KEY_COMPLEXITY, COMPLEXITY)
        }
        val created = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_AUDIO_OPUS)
        try {
            created.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
            created.start()
        } catch (error: Exception) {
            created.release()
            throw error
        }
        codec = created
    }

    override val inFlight: Long get() = queued - produced

    override fun encode(pcm: ByteArray, offset: Int, length: Int): List<ByteArray> {
        if (closed) return emptyList()
        require(length == frameSize * channels * 2) { "one frame is ${frameSize * channels * 2} bytes, not $length" }
        var index = codec.dequeueInputBuffer(INPUT_TIMEOUT_US)
        var retries = 0
        val out = ArrayList<ByteArray>(2)
        while (index < 0 && retries++ < INPUT_RETRIES) {
            // The encoder is behind: take its output so it can take more input, then ask again.
            out += drain(0)
            index = codec.dequeueInputBuffer(INPUT_TIMEOUT_US)
        }
        if (index < 0) throw IllegalStateException("the Opus encoder is not taking input")
        val input = codec.getInputBuffer(index) ?: throw IllegalStateException("no input buffer")
        input.clear()
        input.put(pcm, offset, length)
        codec.queueInputBuffer(index, 0, length, presentationUs, 0)
        presentationUs += frameMillis * 1_000L
        queued++
        out += drain(0)
        return out
    }

    override fun drain(waitMillis: Long): List<ByteArray> {
        if (closed) return emptyList()
        val out = ArrayList<ByteArray>(2)
        val deadline = System.nanoTime() + waitMillis * 1_000_000L
        while (true) {
            val wait = if (inFlight > 0) maxOf(0L, (deadline - System.nanoTime()) / 1_000L) else 0L
            val index = codec.dequeueOutputBuffer(info, wait)
            when {
                index == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> continue
                index >= 0 -> {
                    val config = info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0
                    if (!config && info.size > 0) {
                        val buffer = codec.getOutputBuffer(index)
                        if (buffer != null) {
                            val packet = ByteArray(info.size)
                            buffer.position(info.offset)
                            buffer.limit(info.offset + info.size)
                            buffer.get(packet)
                            out += packet
                            produced++
                        }
                    }
                    codec.releaseOutputBuffer(index, false)
                }
                else -> return out
            }
        }
    }

    override fun close() {
        if (closed) return
        closed = true
        runCatching { codec.stop() }
        runCatching { codec.release() }
    }

    companion object {
        /** The encoder's native frame (§10.4): 50 packets per second. */
        const val FRAME_MILLIS = 20

        /** About transparent for music in stereo; speech in mono needs less (§10.4). */
        const val STEREO_BITRATE = 96_000
        const val MONO_BITRATE = 48_000

        private const val COMPLEXITY = 5
        private const val INPUT_TIMEOUT_US = 10_000L
        private const val INPUT_RETRIES = 10

        val OPUS_RATES = listOf(8_000, 12_000, 16_000, 24_000, 48_000)

        fun defaultBitrate(channels: Int): Int = if (channels >= 2) STEREO_BITRATE else MONO_BITRATE

        /** The rate to encode a source at [rate] at: itself when Opus takes it, otherwise 48 kHz. */
        fun wireRate(rate: Int): Int = if (rate in OPUS_RATES) rate else 48_000
    }
}
