package com.shilapi.xcertplay.simhub

import org.junit.Assert.assertEquals
import org.junit.Test

class OpusPacketTest {
    private fun packet(vararg bytes: Int) = ByteArray(bytes.size) { bytes[it].toByte() }

    @Test fun framesFollowTheTocConfigurationAndCode() {
        assertEquals(960, OpusPacket.frames(packet(0xF8), sampleRate = 48_000)) // CELT FB 20 ms, code 0
        assertEquals(320, OpusPacket.frames(packet(0xF8), sampleRate = 16_000))
        assertEquals(120, OpusPacket.frames(packet(0xE0), sampleRate = 48_000)) // CELT FB 2.5 ms
        assertEquals(2880, OpusPacket.frames(packet(0x18), sampleRate = 48_000)) // SILK NB 60 ms
        assertEquals(1920, OpusPacket.frames(packet(0x79), sampleRate = 48_000)) // code 1: two frames
        assertEquals(1920, OpusPacket.frames(packet(0x7A), sampleRate = 48_000)) // code 2
        assertEquals(2880, OpusPacket.frames(packet(0x7B, 0x03), sampleRate = 48_000)) // code 3, three frames
        assertEquals(960, OpusPacket.frames(packet(0xFC, 0xFF, 0xFE), sampleRate = 48_000)) // what libopus emits for silence
    }

    @Test fun malformedPacketsHaveNoFrames() {
        assertEquals(0, OpusPacket.frames(ByteArray(0), sampleRate = 48_000))
        assertEquals(0, OpusPacket.frames(packet(0x7B), sampleRate = 48_000)) // code 3 without its count byte
        assertEquals(0, OpusPacket.frames(packet(0x7B, 0x00), sampleRate = 48_000)) // count 0
        assertEquals(0, OpusPacket.frames(packet(0x1B, 0x03), sampleRate = 48_000)) // 3 x 60 ms is over 120 ms
        assertEquals(0, OpusPacket.frameCount(packet(0x0B)))
    }

    @Test fun offsetsAreHonoured() {
        val buffer = packet(0x00, 0x00, 0xF8, 0x01, 0x02)
        assertEquals(960, OpusPacket.frames(buffer, offset = 2, length = 3, sampleRate = 48_000))
        assertEquals(0, OpusPacket.frames(buffer, offset = 2, length = 0, sampleRate = 48_000))
    }
}
