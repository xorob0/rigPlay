package com.shilapi.xcertplay.media

import android.media.AudioFormat as AndroidAudioFormat
import android.media.MediaCodec
import android.media.MediaFormat
import com.shilapi.xcertplay.airplay.AudioCodecKind
import com.shilapi.xcertplay.airplay.AudioFormat
import java.io.Closeable
import java.nio.ByteBuffer
import java.nio.ByteOrder

/** Receives decoded PCM; [pcm] is only valid during the call. */
fun interface PcmSink {
    fun onPcm(pcm: ByteArray, offset: Int, length: Int, format: PcmChunkFormat)
}

/** Turns the RTP packets of one CarPlay audio stream (12-byte header plus payload) into PCM. */
interface PcmDecoder : Closeable {
    /**
     * Takes one packet; PCM comes out through [out], now or on a later call. Returns false when the packet's
     * audio was dropped (the decoder would not take it), so the caller can account for the missing time.
     */
    fun decode(rtp: ByteArray, sample: Int, out: PcmSink): Boolean

    /** Delivers output that became ready after the last [decode]. */
    fun drain(out: PcmSink) {}

    override fun close() {}

    companion object {
        /** The decoder CarPlay's [format] needs: LPCM is unpacked directly, AAC-LC and Opus use MediaCodec. */
        fun forFormat(format: AudioFormat): PcmDecoder = when (format.codec) {
            AudioCodecKind.LPCM -> LpcmPcmDecoder(format)
            AudioCodecKind.AAC_LC, AudioCodecKind.OPUS -> MediaCodecPcmDecoder(format)
        }
    }
}

/** CarPlay LPCM: the RTP payload is already PCM, signed 16-bit big-endian. */
class LpcmPcmDecoder(format: AudioFormat) : PcmDecoder {
    private val chunkFormat = PcmChunkFormat(format.sampleRate, format.channels, PcmEncoding.S16BE)

    override fun decode(rtp: ByteArray, sample: Int, out: PcmSink): Boolean {
        if (rtp.size > RTP_HEADER_BYTES) out.onPcm(rtp, RTP_HEADER_BYTES, rtp.size - RTP_HEADER_BYTES, chunkFormat)
        return true
    }
}

/** AAC-LC or Opus through a MediaCodec decoder, configured as the tablet renderer configures it. */
class MediaCodecPcmDecoder(private val format: AudioFormat) : PcmDecoder {
    private val codec: MediaCodec
    private val info = MediaCodec.BufferInfo()
    private var outputFormat = PcmChunkFormat(format.sampleRate, format.channels)
    private var buffer = ByteArray(16 * 1024)

    init {
        val mediaFormat = requireNotNull(CarPlayAudioCodecConfig.mediaFormat(format)) { "no MediaCodec for ${format.codec}" }
        val created = MediaCodec.createDecoderByType(mediaFormat.getString(MediaFormat.KEY_MIME)!!)
        try {
            created.configure(mediaFormat, null, null, 0)
            created.start()
        } catch (error: Exception) {
            created.release()
            throw error
        }
        codec = created
    }

    override fun decode(rtp: ByteArray, sample: Int, out: PcmSink): Boolean {
        val accessUnit = CarPlayAudioCodecConfig.accessUnit(format, rtp) ?: return true
        var index = codec.dequeueInputBuffer(INPUT_TIMEOUT_US)
        var retries = 0
        while (index < 0 && retries++ < INPUT_RETRIES) {
            // The decoder is behind (a busy tablet): free its output so it can take more input, then ask again.
            // Giving up here would silently shorten the audio by one frame, which the PC hears as a dropout later.
            drain(out)
            index = codec.dequeueInputBuffer(INPUT_TIMEOUT_US)
        }
        if (index < 0) return false
        val input = codec.getInputBuffer(index)
        val accepted = input != null && accessUnit.size <= input.capacity()
        if (accepted) {
            input!!.clear()
            input.put(accessUnit)
            val presentationUs = (sample.toLong() and 0xffff_ffffL) * 1_000_000L / format.sampleRate
            codec.queueInputBuffer(index, 0, accessUnit.size, presentationUs, 0)
        } else {
            codec.queueInputBuffer(index, 0, 0, 0, 0)
        }
        drain(out)
        return accepted
    }

    override fun drain(out: PcmSink) {
        while (true) {
            val index = codec.dequeueOutputBuffer(info, 0)
            when {
                index == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> outputFormat = readOutputFormat(codec.outputFormat)
                index >= 0 -> {
                    val size = info.size
                    val output = if (size > 0) codec.getOutputBuffer(index) else null
                    if (output != null) {
                        if (size > buffer.size) buffer = ByteArray(size)
                        output.position(info.offset)
                        output.limit(info.offset + size)
                        output.get(buffer, 0, size)
                        out.onPcm(buffer, 0, size, outputFormat)
                    }
                    codec.releaseOutputBuffer(index, false)
                }
                else -> return
            }
        }
    }

    private fun readOutputFormat(media: MediaFormat): PcmChunkFormat {
        val encoding = if (media.containsKey(MediaFormat.KEY_PCM_ENCODING) &&
            media.getInteger(MediaFormat.KEY_PCM_ENCODING) == AndroidAudioFormat.ENCODING_PCM_FLOAT
        ) PcmEncoding.FLOAT32LE else PcmEncoding.S16LE
        return PcmChunkFormat(
            sampleRate = media.intOr(MediaFormat.KEY_SAMPLE_RATE, outputFormat.sampleRate),
            channels = media.intOr(MediaFormat.KEY_CHANNEL_COUNT, outputFormat.channels),
            encoding = encoding,
        )
    }

    override fun close() {
        runCatching { codec.stop() }
        runCatching { codec.release() }
    }

    private fun MediaFormat.intOr(key: String, fallback: Int): Int =
        if (containsKey(key)) runCatching { getInteger(key) }.getOrDefault(fallback) else fallback

    private companion object {
        const val INPUT_TIMEOUT_US = 10_000L

        /** With [INPUT_TIMEOUT_US], up to about 200 ms of waiting before a packet is given up; the queue holds over a second. */
        const val INPUT_RETRIES = 20
    }
}

/** MediaCodec setup for CarPlay's compressed audio, shared by the tablet renderer and the PC sender. */
internal object CarPlayAudioCodecConfig {
    private const val AAC_OBJECT_TYPE_LC = 2
    private const val MIN_OPUS_PACKET_BYTES = 4
    private const val MAX_INPUT_BYTES = 64 * 1024
    private const val OPUS_CODEC_DELAY_NANOS = 6_500_000L
    private const val OPUS_SEEK_PRE_ROLL_NANOS = 80_000_000L

    /** The decoder format for [format], or `null` for LPCM, which needs none. */
    fun mediaFormat(format: AudioFormat): MediaFormat? {
        val mime = when (format.codec) {
            AudioCodecKind.AAC_LC -> MediaFormat.MIMETYPE_AUDIO_AAC
            AudioCodecKind.OPUS -> MediaFormat.MIMETYPE_AUDIO_OPUS
            AudioCodecKind.LPCM -> return null
        }
        return MediaFormat().apply {
            setString(MediaFormat.KEY_MIME, mime)
            setInteger(MediaFormat.KEY_SAMPLE_RATE, format.sampleRate)
            setInteger(MediaFormat.KEY_CHANNEL_COUNT, format.channels)
            setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, MAX_INPUT_BYTES)
            if (format.codec == AudioCodecKind.AAC_LC) {
                setInteger(MediaFormat.KEY_IS_ADTS, 1)
                setByteBuffer("csd-0", ByteBuffer.wrap(aacAudioSpecificConfig(format.sampleRate, format.channels)))
            } else {
                setByteBuffer("csd-0", ByteBuffer.wrap(opusHead(format.sampleRate, format.channels)))
                setByteBuffer("csd-1", ByteBuffer.wrap(littleEndianLong(OPUS_CODEC_DELAY_NANOS)))
                setByteBuffer("csd-2", ByteBuffer.wrap(littleEndianLong(OPUS_SEEK_PRE_ROLL_NANOS)))
            }
        }
    }

    /**
     * The decoder input for one RTP packet: an ADTS frame for AAC-LC, the raw packet for Opus, or
     * `null` when the packet carries nothing to decode (empty AAC, Opus shorter than a TOC).
     */
    fun accessUnit(format: AudioFormat, rtp: ByteArray): ByteArray? {
        if (rtp.size <= RTP_HEADER_BYTES) return null
        val payload = rtp.copyOfRange(RTP_HEADER_BYTES, rtp.size)
        return when (format.codec) {
            AudioCodecKind.AAC_LC -> MediaCodecSupport.adtsFrame(payload, format.sampleRate, format.channels)
            AudioCodecKind.OPUS -> payload.takeIf { it.size >= MIN_OPUS_PACKET_BYTES }
            AudioCodecKind.LPCM -> null
        }
    }

    fun aacAudioSpecificConfig(sampleRate: Int, channels: Int): ByteArray {
        val frequencyIndex = MediaCodecSupport.aacFrequencyIndex(sampleRate)
        val value = (AAC_OBJECT_TYPE_LC shl 11) or (frequencyIndex shl 7) or (channels.coerceIn(1, 7) shl 3)
        return byteArrayOf((value ushr 8).toByte(), value.toByte())
    }

    /** Minimal OpusHead CSD for the mono 48 kHz stream CarPlay negotiates. */
    fun opusHead(sampleRate: Int, channels: Int): ByteArray {
        val head = ByteArray(19)
        "OpusHead".toByteArray(Charsets.US_ASCII).copyInto(head, 0)
        head[8] = 1
        head[9] = channels.toByte()
        head[10] = 0x38
        head[11] = 0x01
        head[12] = sampleRate.toByte()
        head[13] = (sampleRate ushr 8).toByte()
        head[14] = (sampleRate ushr 16).toByte()
        head[15] = (sampleRate ushr 24).toByte()
        return head
    }

    private fun littleEndianLong(value: Long): ByteArray =
        ByteBuffer.allocate(8).order(ByteOrder.LITTLE_ENDIAN).putLong(value).array()
}

private const val RTP_HEADER_BYTES = 12
