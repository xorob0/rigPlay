package com.shilapi.xcertplay.simhub

import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * The 12-byte header of an audio datagram (`docs/protocol.md` §10.2). Header fields are big-endian;
 * a PCM payload that follows is little-endian, an Opus payload (§10.4) is one Opus packet.
 *
 * @property seq u16 datagram counter per stream, wraps at 65535.
 * @property timestamp u32 sample-frame clock, wraps at 4294967295.
 * @property start flag bit 0: first datagram after `audioStart`. Reserved bits are written as 0 and
 *   ignored on decode.
 */
data class AudioHeader(
    val seq: Int,
    val stream: AudioStream,
    val start: Boolean,
    val timestamp: Long,
    val sampleRateHz: Int,
    val channels: Int,
    val format: AudioFormat = AudioFormat.PCM_S16LE,
) {
    init {
        require(seq in 0..0xFFFF) { "seq must fit in u16" }
        require(timestamp in 0..0xFFFF_FFFFL) { "timestamp must fit in u32" }
        require(sampleRateHz % 100 == 0 && sampleRateHz / 100 in SAMPLE_RATE_FIELD_RANGE) {
            "sampleRateHz must be a multiple of 100 within 8000..48000"
        }
        require(channels == 1 || channels == 2) { "channels must be 1 or 2" }
    }

    /** Bytes per sample frame of a PCM payload. */
    val frameBytes: Int get() = 2 * channels

    val isOpus: Boolean get() = format == AudioFormat.OPUS

    companion object {
        const val SIZE = 12
        const val FLAG_START = 0x01
        val SAMPLE_RATE_FIELD_RANGE = 80..480

        /** The longest Opus packet a datagram carries (§10.4). */
        const val MAX_OPUS_PACKET_BYTES = 1275

        /** Receivers accept payloads up to this size (§10.2). */
        const val MAX_PAYLOAD_BYTES = 8192
    }
}

/**
 * A decoded audio datagram: header plus the payload (little-endian PCM, or one Opus packet). [frames] is what
 * the payload stands for on the sample clock: whole PCM frames, or what the Opus packet's TOC says.
 */
class AudioDatagram(val header: AudioHeader, val payload: ByteArray) {
    val frames: Int = if (header.isOpus) OpusPacket.frames(payload, 0, payload.size, header.sampleRateHz) else payload.size / header.frameBytes

    /** The payload as interleaved signed 16-bit samples. */
    fun samples(): ShortArray {
        val buffer = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN).asShortBuffer()
        return ShortArray(buffer.remaining()).also { buffer.get(it) }
    }

    override fun equals(other: Any?): Boolean =
        other is AudioDatagram && other.header == header && other.payload.contentEquals(payload)

    override fun hashCode(): Int = 31 * header.hashCode() + payload.contentHashCode()

    override fun toString(): String = "AudioDatagram(header=$header, payloadBytes=${payload.size})"
}

/** Encoder and validating decoder for audio datagrams (§10.2). */
object SimHubAudioCodec {
    fun encodeHeader(header: AudioHeader, out: ByteArray = ByteArray(AudioHeader.SIZE), offset: Int = 0): ByteArray {
        require(out.size - offset >= AudioHeader.SIZE) { "buffer too small for the header" }
        ByteBuffer.wrap(out, offset, AudioHeader.SIZE).order(ByteOrder.BIG_ENDIAN)
            .putShort(header.seq.toShort())
            .put(header.stream.code.toByte())
            .put((if (header.start) AudioHeader.FLAG_START else 0).toByte())
            .putInt(header.timestamp.toInt())
            .putShort((header.sampleRateHz / 100).toShort())
            .put(header.channels.toByte())
            .put(header.format.code.toByte())
        return out
    }

    /** Header followed by [pcm] (already little-endian s16, as decoded from AirPlay), or by one Opus packet. */
    fun encode(header: AudioHeader, pcm: ByteArray, offset: Int = 0, length: Int = pcm.size - offset): ByteArray {
        if (header.isOpus) {
            require(length in 1..AudioHeader.MAX_OPUS_PACKET_BYTES && OpusPacket.frames(pcm, offset, length, header.sampleRateHz) > 0) {
                "payload must be one Opus packet"
            }
        } else {
            require(length > 0 && length % header.frameBytes == 0) {
                "payload must be a positive multiple of ${header.frameBytes} bytes"
            }
        }
        val out = ByteArray(AudioHeader.SIZE + length)
        encodeHeader(header, out)
        System.arraycopy(pcm, offset, out, AudioHeader.SIZE, length)
        return out
    }

    /** Header followed by [samples] written little-endian. */
    fun encodeSamples(header: AudioHeader, samples: ShortArray): ByteArray {
        val pcm = ByteBuffer.allocate(samples.size * 2).order(ByteOrder.LITTLE_ENDIAN)
        samples.forEach { pcm.putShort(it) }
        return encode(header, pcm.array())
    }

    /**
     * Decodes a datagram received in [direction], or returns `null` for anything §10.2 says to drop:
     * shorter than one complete frame after the header, an invalid stream type or one that does not flow
     * in [direction] (`mic` from a tablet, `media`/`alt`/`telephony` to a tablet), an invalid format
     * (Opus flows tablet → plugin only, §10.4; the microphone is PCM, §10.5), channel count or sample
     * rate, a PCM payload that is not a whole number of frames, or an Opus payload that is empty,
     * longer than one packet can be, or whose TOC describes no frame.
     */
    fun decode(data: ByteArray, direction: AudioDirection, offset: Int = 0, length: Int = data.size - offset): AudioDatagram? {
        if (length < AudioHeader.SIZE) return null
        val buffer = ByteBuffer.wrap(data, offset, length).order(ByteOrder.BIG_ENDIAN)
        val seq = buffer.short.toInt() and 0xFFFF
        val stream = AudioStream.fromCode(buffer.get().toInt() and 0xFF) ?: return null
        if (stream.direction != direction) return null
        val flags = buffer.get().toInt() and 0xFF
        val timestamp = buffer.int.toLong() and 0xFFFF_FFFFL
        val rateField = buffer.short.toInt() and 0xFFFF
        val channels = buffer.get().toInt() and 0xFF
        val format = AudioFormat.fromCode(buffer.get().toInt() and 0xFF) ?: return null
        if (format == AudioFormat.OPUS && direction == AudioDirection.PC_TO_TABLET) return null // the microphone is PCM (§10.5)
        if (rateField !in AudioHeader.SAMPLE_RATE_FIELD_RANGE) return null
        if (channels != 1 && channels != 2) return null
        val payloadLength = length - AudioHeader.SIZE
        if (payloadLength > AudioHeader.MAX_PAYLOAD_BYTES) return null // §10.2 high cap, mirrors C# AudioHeader.Validate
        if (format == AudioFormat.OPUS) {
            if (payloadLength !in 1..AudioHeader.MAX_OPUS_PACKET_BYTES) return null
            if (OpusPacket.frames(data, offset + AudioHeader.SIZE, payloadLength, rateField * 100) <= 0) return null
        } else if (payloadLength < 2 * channels || payloadLength % (2 * channels) != 0) {
            return null
        }
        val header = AudioHeader(
            seq = seq,
            stream = stream,
            start = flags and AudioHeader.FLAG_START != 0,
            timestamp = timestamp,
            sampleRateHz = rateField * 100,
            channels = channels,
            format = format,
        )
        val payload = data.copyOfRange(offset + AudioHeader.SIZE, offset + length)
        return AudioDatagram(header, payload)
    }
}
