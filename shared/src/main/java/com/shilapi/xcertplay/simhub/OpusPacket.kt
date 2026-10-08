package com.shilapi.xcertplay.simhub

/**
 * What an Opus packet's first byte, the TOC (RFC 6716 §3.1), says about it, so a datagram can be checked and
 * placed on the sample clock (`docs/protocol.md` §10.4) without a decoder on either side.
 */
object OpusPacket {
    /** Frame duration in microseconds for each of the 32 TOC configurations. */
    private val FRAME_MICROS = intArrayOf(
        10_000, 20_000, 40_000, 60_000, // 0-3   SILK NB
        10_000, 20_000, 40_000, 60_000, // 4-7   SILK MB
        10_000, 20_000, 40_000, 60_000, // 8-11  SILK WB
        10_000, 20_000, // 12-13 hybrid SWB
        10_000, 20_000, // 14-15 hybrid FB
        2_500, 5_000, 10_000, 20_000, // 16-19 CELT NB
        2_500, 5_000, 10_000, 20_000, // 20-23 CELT WB
        2_500, 5_000, 10_000, 20_000, // 24-27 CELT SWB
        2_500, 5_000, 10_000, 20_000, // 28-31 CELT FB
    )

    /** The most audio one packet may carry (RFC 6716 §3.2.5). */
    private const val MAX_PACKET_MICROS = 120_000L

    /** Opus frames in the packet: 1, 2, or the count byte of a code 3 packet; 0 when malformed. */
    fun frameCount(packet: ByteArray, offset: Int = 0, length: Int = packet.size - offset): Int {
        if (length < 1) return 0
        return when (packet[offset].toInt() and 0x03) {
            0 -> 1
            1, 2 -> 2
            else -> if (length < 2) 0 else packet[offset + 1].toInt() and 0x3f
        }
    }

    /** Sample frames per Opus frame of the packet at [sampleRate]; 0 for an empty packet. */
    fun samplesPerFrame(packet: ByteArray, offset: Int, length: Int, sampleRate: Int): Int {
        if (length < 1) return 0
        val config = (packet[offset].toInt() shr 3) and 0x1f
        return (FRAME_MICROS[config].toLong() * sampleRate / 1_000_000L).toInt()
    }

    /**
     * Sample frames the packet decodes to at [sampleRate], or 0 when it is malformed: no TOC, a code 3 packet
     * without its count byte or with a count of 0, or more than 120 ms of audio.
     */
    fun frames(packet: ByteArray, offset: Int = 0, length: Int = packet.size - offset, sampleRate: Int): Int {
        val count = frameCount(packet, offset, length)
        if (count <= 0) return 0
        val config = (packet[offset].toInt() shr 3) and 0x1f
        if (FRAME_MICROS[config].toLong() * count > MAX_PACKET_MICROS) return 0
        return samplesPerFrame(packet, offset, length, sampleRate) * count
    }
}
