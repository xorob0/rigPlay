package com.shilapi.xcertplay.media

import kotlin.math.floor
import kotlin.math.roundToInt

/** Sample layout of a decoded PCM chunk before it is normalised to s16le for the PC. */
enum class PcmEncoding {
    /** Signed 16-bit little-endian: MediaCodec's default output and the wire format of §10.2. */
    S16LE,

    /** Signed 16-bit big-endian: CarPlay's wired LPCM payload. */
    S16BE,

    /** 32-bit IEEE float little-endian: MediaCodec output with `KEY_PCM_ENCODING` = float. */
    FLOAT32LE,
}

/** Format of one decoded PCM chunk. */
data class PcmChunkFormat(val sampleRate: Int, val channels: Int, val encoding: PcmEncoding = PcmEncoding.S16LE)

/**
 * Converts decoded PCM into what an audio datagram carries (`docs/protocol.md` §10.2): interleaved
 * s16le, one or two channels, at a sample rate that is a multiple of 100 Hz within 8000..48000.
 */
object PcmConversion {
    const val MIN_RATE = 8_000
    const val MAX_RATE = 48_000

    /** Rates the PC is given when the input rate cannot go on the wire as it is. */
    private val STANDARD_RATES = intArrayOf(8_000, 16_000, 24_000, 32_000, 44_100, 48_000)

    /** True when [rate] fits the header's `sampleRate / 100` field (§10.2). */
    fun isWireRate(rate: Int): Boolean = rate in MIN_RATE..MAX_RATE && rate % 100 == 0

    /**
     * The rate to send [rate] at: [rate] itself when it fits the header, otherwise the next standard
     * rate up (11025 → 16000, 22050 → 24000), capped at 48000.
     */
    fun wireRate(rate: Int): Int {
        if (isWireRate(rate)) return rate
        return STANDARD_RATES.firstOrNull { it >= rate } ?: MAX_RATE
    }

    /** [length] bytes of [encoding] samples at [offset] as a new s16le array (a trailing partial sample is dropped). */
    fun toS16le(source: ByteArray, offset: Int, length: Int, encoding: PcmEncoding): ByteArray = when (encoding) {
        PcmEncoding.S16LE -> source.copyOfRange(offset, offset + (length and 1.inv()))
        PcmEncoding.S16BE -> {
            val count = length / 2
            ByteArray(count * 2).also { out ->
                for (i in 0 until count) {
                    out[2 * i] = source[offset + 2 * i + 1]
                    out[2 * i + 1] = source[offset + 2 * i]
                }
            }
        }
        PcmEncoding.FLOAT32LE -> {
            val count = length / 4
            ByteArray(count * 2).also { out ->
                for (i in 0 until count) {
                    val p = offset + 4 * i
                    val bits = (source[p].toInt() and 0xff) or
                        ((source[p + 1].toInt() and 0xff) shl 8) or
                        ((source[p + 2].toInt() and 0xff) shl 16) or
                        ((source[p + 3].toInt() and 0xff) shl 24)
                    val value = (Float.fromBits(bits).coerceIn(-1f, 1f) * 32767f).roundToInt()
                    out[2 * i] = value.toByte()
                    out[2 * i + 1] = (value shr 8).toByte()
                }
            }
        }
    }

    /** Keeps the first two channels of interleaved s16le with [channels] > 2; returns [pcm] otherwise. */
    fun toAtMostStereo(pcm: ByteArray, channels: Int): ByteArray {
        if (channels <= 2) return pcm
        val frames = pcm.size / (2 * channels)
        return ByteArray(frames * 4).also { out ->
            for (frame in 0 until frames) System.arraycopy(pcm, frame * 2 * channels, out, frame * 4, 4)
        }
    }
}

/**
 * Linear-interpolation resampler for interleaved s16le, stateful across chunks so consecutive calls
 * join without clicks. Only used for the odd rates the header cannot carry (11025, 22050), where
 * speech quality is enough.
 */
class LinearResampler(val inputRate: Int, val outputRate: Int, val channels: Int) {
    init {
        require(inputRate > 0 && outputRate > 0) { "rates must be positive" }
        require(channels >= 1) { "channels must be positive" }
    }

    private val step = inputRate.toDouble() / outputRate
    private val previous = ShortArray(channels)

    /** Position of the next output frame, in input frames relative to the current chunk; -1 is the previous chunk's last frame. */
    private var position = 0.0

    fun process(pcm: ByteArray, offset: Int = 0, length: Int = pcm.size - offset): ByteArray {
        val frameBytes = 2 * channels
        val frames = length / frameBytes
        if (frames == 0) return ByteArray(0)
        val estimate = ((frames + 1) / step).toInt() + 2
        var out = ByteArray(estimate * frameBytes)
        var written = 0
        while (true) {
            val index = floor(position).toInt()
            if (index + 1 >= frames) break
            val fraction = position - index
            if (written + frameBytes > out.size) out = out.copyOf(out.size * 2)
            for (channel in 0 until channels) {
                val s0 = if (index < 0) previous[channel].toInt() else sample(pcm, offset, index, channel)
                val s1 = sample(pcm, offset, index + 1, channel)
                val value = (s0 + (s1 - s0) * fraction).roundToInt()
                out[written++] = value.toByte()
                out[written++] = (value shr 8).toByte()
            }
            position += step
        }
        for (channel in 0 until channels) previous[channel] = sample(pcm, offset, frames - 1, channel).toShort()
        position -= frames
        return out.copyOf(written)
    }

    private fun sample(pcm: ByteArray, offset: Int, frame: Int, channel: Int): Int {
        val p = offset + (frame * channels + channel) * 2
        return (pcm[p].toInt() and 0xff) or (pcm[p + 1].toInt() shl 8)
    }
}
