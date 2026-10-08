package com.shilapi.xcertplay.media

import com.shilapi.xcertplay.simhub.AudioFormat
import com.shilapi.xcertplay.simhub.AudioHeader
import com.shilapi.xcertplay.simhub.AudioStream
import com.shilapi.xcertplay.simhub.OpusPacket
import com.shilapi.xcertplay.simhub.SimHubAudioCodec

/**
 * [AudioPacketizer] for the Opus format (`docs/protocol.md` §10.4): gathers s16le PCM into the encoder's
 * frames (20 ms), encodes each, and sends every packet as one datagram with `format` 2, `seq` from 0 and
 * the timestamp advanced by the frames the packet's TOC says it holds (the encoder's frame unless it says
 * otherwise). The encoder may return a packet one call late; packets come in order, so the clock stays
 * right. A partial frame is padded with silence at [flush] and before a [skip], and the padding is taken
 * off the gap, so the PC's clock does not drift.
 */
class OpusPacketizer(
    val stream: AudioStream,
    private val encoder: OpusFrameEncoder,
) : AudioPacketizer {
    override val sampleRate: Int get() = encoder.sampleRate
    override val channels: Int get() = encoder.channels

    private val frameBytes = 2 * channels
    private val frameSize = encoder.frameSize
    private val inputBytes = frameSize * frameBytes
    private val input = ByteArray(inputBytes)
    private var pending = 0
    private val datagram = ByteArray(AudioHeader.SIZE + AudioHeader.MAX_OPUS_PACKET_BYTES)

    /** `seq` of the next datagram. */
    var seq: Int = 0
        private set

    /** `timestamp` of the next datagram: frames sent so far, modulo 2^32. */
    var timestamp: Long = 0L
        private set

    override var datagrams: Long = 0L
        private set

    /** Bytes buffered towards the next frame. */
    val pendingBytes: Int get() = pending

    /** Packets the encoder returned that were not one Opus packet (dropped; their time still passes). */
    var badPackets: Long = 0L
        private set

    override fun push(pcm: ByteArray, offset: Int, length: Int, emit: (ByteArray, Int) -> Unit) {
        var cursor = offset
        var left = length
        while (left > 0) {
            val take = minOf(left, inputBytes - pending)
            System.arraycopy(pcm, cursor, input, pending, take)
            pending += take
            cursor += take
            left -= take
            if (pending == inputBytes) {
                pending = 0
                deliver(encoder.encode(input, 0, inputBytes), emit)
            }
        }
    }

    override fun flush(emit: (ByteArray, Int) -> Unit) {
        padAndEncode(emit)
        deliver(encoder.drain(DRAIN_WAIT_MILLIS), emit)
    }

    override fun poll(emit: (ByteArray, Int) -> Unit) {
        if (encoder.inFlight > 0) deliver(encoder.drain(0), emit)
    }

    override fun skip(frames: Long, emit: (ByteArray, Int) -> Unit) {
        if (frames <= 0 || (datagrams == 0L && pending < frameBytes && encoder.inFlight == 0L)) return
        val padded = padAndEncode(emit)
        deliver(encoder.drain(DRAIN_WAIT_MILLIS), emit)
        val remaining = frames - padded
        if (remaining > 0) timestamp = (timestamp + remaining) and 0xFFFF_FFFFL
    }

    override fun close() = encoder.close()

    /** Encodes a partial frame padded with silence; returns the frames of padding added (0 when nothing was pending). */
    private fun padAndEncode(emit: (ByteArray, Int) -> Unit): Long {
        val whole = pending - pending % frameBytes
        if (whole <= 0) {
            pending = 0
            return 0L
        }
        val padding = (inputBytes - whole) / frameBytes
        input.fill(0, whole, inputBytes)
        pending = 0
        deliver(encoder.encode(input, 0, inputBytes), emit)
        return padding.toLong()
    }

    private fun deliver(packets: List<ByteArray>, emit: (ByteArray, Int) -> Unit) {
        for (packet in packets) {
            val frames = OpusPacket.frames(packet, 0, packet.size, sampleRate)
            if (packet.isEmpty() || packet.size > AudioHeader.MAX_OPUS_PACKET_BYTES || frames <= 0) {
                badPackets++
                timestamp = (timestamp + frameSize) and 0xFFFF_FFFFL
                continue
            }
            SimHubAudioCodec.encodeHeader(
                AudioHeader(
                    seq = seq,
                    stream = stream,
                    start = datagrams == 0L,
                    timestamp = timestamp,
                    sampleRateHz = sampleRate,
                    channels = channels,
                    format = AudioFormat.OPUS,
                ),
                datagram,
            )
            System.arraycopy(packet, 0, datagram, AudioHeader.SIZE, packet.size)
            seq = (seq + 1) and 0xFFFF
            timestamp = (timestamp + frames) and 0xFFFF_FFFFL
            datagrams++
            emit(datagram, AudioHeader.SIZE + packet.size)
        }
    }

    companion object {
        /** How long [flush] and [skip] wait for packets the encoder still owes: a few frames of a software encoder. */
        const val DRAIN_WAIT_MILLIS = 60L
    }
}
