package com.shilapi.xcertplay.media

import com.shilapi.xcertplay.simhub.AudioDatagram
import com.shilapi.xcertplay.simhub.AudioDirection
import com.shilapi.xcertplay.simhub.AudioFormat
import com.shilapi.xcertplay.simhub.AudioStream
import com.shilapi.xcertplay.simhub.SimHubAudioCodec
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class OpusPacketizerTest {
    private fun collect(packetizer: OpusPacketizer, block: (OpusPacketizer, (ByteArray, Int) -> Unit) -> Unit): List<AudioDatagram> {
        val out = mutableListOf<AudioDatagram>()
        block(packetizer) { bytes, length -> out.add(SimHubAudioCodec.decode(bytes, AudioDirection.TABLET_TO_PC, 0, length) ?: error("invalid opus datagram")) }
        return out
    }

    private fun ramp(frames: Int, channels: Int, first: Int = 0): ByteArray = ByteArray(frames * channels * 2) { ((first + it) * 7).toByte() }

    @Test fun twentyMillisecondFramesBecomeOneOpusDatagramEach() {
        val encoder = FakeOpusEncoder(48_000, 2)
        val packetizer = OpusPacketizer(AudioStream.MEDIA, encoder)
        val pcm = ramp(960 * 3 + 100, 2)
        val datagrams = collect(packetizer) { p, emit -> p.push(pcm, emit = emit) }

        assertEquals(3, datagrams.size)
        datagrams.forEachIndexed { index, datagram ->
            assertEquals(AudioFormat.OPUS, datagram.header.format)
            assertEquals(index, datagram.header.seq)
            assertEquals(960L * index, datagram.header.timestamp)
            assertEquals(index == 0, datagram.header.start)
            assertEquals(48_000, datagram.header.sampleRateHz)
            assertEquals(2, datagram.header.channels)
            assertEquals(960, datagram.frames)
            assertEquals(index, datagram.payload[1].toInt())
            assertArrayEquals(pcm.copyOfRange(index * 3840, index * 3840 + 6), datagram.payload.copyOfRange(2, 8))
        }
        assertEquals(400, packetizer.pendingBytes)
        assertEquals(3L, packetizer.datagrams)
        assertEquals(2880L, packetizer.timestamp)
    }

    @Test fun sixteenKiloHertzMonoUses320FrameDatagrams() {
        val packetizer = OpusPacketizer(AudioStream.ALT, FakeOpusEncoder(16_000, 1))
        val datagrams = collect(packetizer) { p, emit -> p.push(ramp(640, 1), emit = emit) }
        assertEquals(listOf(320, 320), datagrams.map { it.frames })
        assertEquals(listOf(0L, 320L), datagrams.map { it.header.timestamp })
    }

    @Test fun aLatePacketFromTheEncoderKeepsItsTimestamp() {
        val encoder = FakeOpusEncoder(48_000, 2, latency = 1)
        val packetizer = OpusPacketizer(AudioStream.MEDIA, encoder)
        val datagrams = collect(packetizer) { p, emit ->
            p.push(ramp(960, 2), emit = emit) // held by the encoder
            p.poll(emit)
            p.push(ramp(960, 2), emit = emit) // releases the first
            p.flush(emit) // drains the second
        }
        assertEquals(listOf(0L, 960L), datagrams.map { it.header.timestamp })
        assertEquals(listOf(0, 1), datagrams.map { it.payload[1].toInt() })
        assertEquals(0L, encoder.inFlight)
    }

    @Test fun flushPadsAPartialFrameWithSilence() {
        // A packet long enough to show the 40 real frames (160 bytes) and the start of the padding.
        val encoder = FakeOpusEncoder(48_000, 2, packetBytes = 200)
        val packetizer = OpusPacketizer(AudioStream.MEDIA, encoder)
        val datagrams = collect(packetizer) { p, emit ->
            p.push(ramp(1000, 2), emit = emit)
            p.flush(emit)
        }
        assertEquals(2, datagrams.size)
        // The second packet holds the 40 real frames, then zeros.
        val tail = datagrams[1].payload.copyOfRange(2, 200)
        assertArrayEquals(ramp(1000, 2).copyOfRange(3840, 4000), tail.copyOfRange(0, 160))
        assertTrue(tail.copyOfRange(160, tail.size).all { it == 0.toByte() })
        assertEquals(1920L, packetizer.timestamp)
        assertEquals(0, packetizer.pendingBytes)
    }

    @Test fun skipPadsTheTailAndJumpsByTheRestOfTheGap() {
        val packetizer = OpusPacketizer(AudioStream.MEDIA, FakeOpusEncoder(48_000, 2))
        val datagrams = collect(packetizer) { p, emit ->
            p.push(ramp(1200, 2), emit = emit) // one packet, 240 frames pending
            p.skip(1000, emit) // 720 frames of padding cover part of the gap; the clock jumps the other 280
            p.push(ramp(960, 2), emit = emit)
        }
        assertEquals(listOf(0L, 960L, 2200L), datagrams.map { it.header.timestamp })
        assertEquals(listOf(0, 1, 2), datagrams.map { it.header.seq })
        assertEquals(3160L, packetizer.timestamp)
    }

    @Test fun skipOnAFrameBoundaryJumpsTheWholeGap() {
        val packetizer = OpusPacketizer(AudioStream.MEDIA, FakeOpusEncoder(48_000, 2))
        val datagrams = collect(packetizer) { p, emit ->
            p.push(ramp(960, 2), emit = emit)
            p.skip(500, emit)
            p.push(ramp(960, 2), emit = emit)
        }
        assertEquals(listOf(0L, 1460L), datagrams.map { it.header.timestamp })
    }

    @Test fun skipBeforeAnyAudioIsIgnored() {
        val packetizer = OpusPacketizer(AudioStream.MEDIA, FakeOpusEncoder(48_000, 2))
        val datagrams = collect(packetizer) { p, emit ->
            p.skip(1000, emit)
            p.push(ramp(960, 2), emit = emit)
        }
        assertEquals(listOf(0L), datagrams.map { it.header.timestamp })
        assertTrue(datagrams[0].header.start)
    }

    @Test fun aPacketThatIsNotOpusIsDroppedButItsTimePasses() {
        val encoder = object : OpusFrameEncoder {
            override val sampleRate = 48_000
            override val channels = 2
            override val frameMillis = 20
            private var n = 0
            override fun encode(pcm: ByteArray, offset: Int, length: Int): List<ByteArray> =
                listOf(if (n++ == 0) ByteArray(0) else byteArrayOf(0xF8.toByte(), 1))
            override fun drain(waitMillis: Long): List<ByteArray> = emptyList()
            override fun close() {}
        }
        val packetizer = OpusPacketizer(AudioStream.MEDIA, encoder)
        val datagrams = collect(packetizer) { p, emit -> p.push(ramp(1920, 2), emit = emit) }
        assertEquals(1, datagrams.size)
        assertEquals(960L, datagrams[0].header.timestamp)
        assertEquals(1L, packetizer.badPackets)
    }

    @Test fun closeClosesTheEncoder() {
        val encoder = FakeOpusEncoder(48_000, 2)
        OpusPacketizer(AudioStream.MEDIA, encoder).close()
        assertTrue(encoder.closed)
    }
}
