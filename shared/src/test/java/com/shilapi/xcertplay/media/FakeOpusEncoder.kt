package com.shilapi.xcertplay.media

/**
 * An [OpusFrameEncoder] that does not encode: each frame becomes a packet of a CELT fullband 20 ms TOC byte
 * (0xF8) followed by the frame's index and its first bytes, so a test can tell packets apart. [latency] packets
 * are held back, as the Android encoder holds one, and come out on the next call or on [drain].
 */
class FakeOpusEncoder(
    override val sampleRate: Int,
    override val channels: Int,
    private val latency: Int = 0,
    private val packetBytes: Int = 8,
) : OpusFrameEncoder {
    override val frameMillis: Int get() = 20

    private val pending = ArrayDeque<ByteArray>()
    var frames = 0
        private set
    var closed = false
        private set

    override val inFlight: Long get() = pending.size.toLong()

    override fun encode(pcm: ByteArray, offset: Int, length: Int): List<ByteArray> {
        require(length == frameSize * channels * 2) { "a frame is ${frameSize * channels * 2} bytes" }
        val packet = ByteArray(packetBytes)
        packet[0] = 0xF8.toByte()
        packet[1] = frames.toByte()
        for (i in 2 until packetBytes) packet[i] = pcm[offset + i - 2]
        frames++
        pending.addLast(packet)
        return release(latency)
    }

    override fun drain(waitMillis: Long): List<ByteArray> = release(0)

    private fun release(keep: Int): List<ByteArray> {
        val out = ArrayList<ByteArray>()
        while (pending.size > keep) out += pending.removeFirst()
        return out
    }

    override fun close() {
        closed = true
    }
}
