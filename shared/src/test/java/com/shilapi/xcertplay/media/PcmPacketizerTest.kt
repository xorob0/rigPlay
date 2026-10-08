package com.shilapi.xcertplay.media

import com.shilapi.xcertplay.simhub.AudioDatagram
import com.shilapi.xcertplay.simhub.AudioDirection
import com.shilapi.xcertplay.simhub.AudioHeader
import com.shilapi.xcertplay.simhub.AudioStream
import com.shilapi.xcertplay.simhub.SimHubAudioCodec
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class PcmPacketizerTest {
    private fun collect(packetizer: PcmPacketizer, block: (PcmPacketizer, (ByteArray, Int) -> Unit) -> Unit): List<AudioDatagram> {
        val out = mutableListOf<AudioDatagram>()
        block(packetizer) { bytes, length -> out.add(SimHubAudioCodec.decode(bytes, AudioDirection.TABLET_TO_PC, 0, length)!!) }
        return out
    }

    private fun ramp(frames: Int, channels: Int): ByteArray = ByteArray(frames * channels * 2) { (it * 7).toByte() }

    @Test fun fortyEightKiloHertzStereoUsesFiveMillisecondDatagramsOf960Bytes() {
        val packetizer = PcmPacketizer(AudioStream.MEDIA, 48_000, 2)
        assertEquals(240, packetizer.framesPerDatagram)
        val pcm = ramp(240 * 3 + 100, 2)
        val datagrams = collect(packetizer) { p, emit -> p.push(pcm, emit = emit) }

        assertEquals(3, datagrams.size)
        datagrams.forEachIndexed { index, datagram ->
            assertEquals(960, datagram.payload.size)
            assertEquals(index, datagram.header.seq)
            assertEquals(240L * index, datagram.header.timestamp)
            assertEquals(index == 0, datagram.header.start)
            assertEquals(AudioStream.MEDIA, datagram.header.stream)
            assertEquals(48_000, datagram.header.sampleRateHz)
            assertEquals(2, datagram.header.channels)
            assertArrayEquals(pcm.copyOfRange(index * 960, index * 960 + 960), datagram.payload)
        }
        assertEquals(400, packetizer.pendingBytes)
    }

    @Test fun datagramSizeFollowsRateAndChannels() {
        assertEquals(80, PcmPacketizer.defaultFramesPerDatagram(16_000, 1))
        assertEquals(120, PcmPacketizer.defaultFramesPerDatagram(24_000, 1))
        assertEquals(220, PcmPacketizer.defaultFramesPerDatagram(44_100, 2))
        val packetizer = PcmPacketizer(AudioStream.TELEPHONY, 16_000, 1)
        val datagrams = collect(packetizer) { p, emit -> p.push(ramp(160, 1), emit = emit) }
        assertEquals(listOf(160, 160), datagrams.map { it.payload.size })
        assertEquals(listOf(0L, 80L), datagrams.map { it.header.timestamp })
        datagrams.forEach { assertTrue(AudioHeader.SIZE + it.payload.size <= 1472) }
    }

    @Test fun pushesInPiecesProduceTheSameDatagramsAsOnePush() {
        val pcm = ramp(1000, 2)
        val whole = collect(PcmPacketizer(AudioStream.MEDIA, 48_000, 2)) { p, emit -> p.push(pcm, emit = emit) }
        val pieces = collect(PcmPacketizer(AudioStream.MEDIA, 48_000, 2)) { p, emit ->
            var offset = 0
            for (size in listOf(1, 3, 517, 961, 2, 1000)) {
                val take = minOf(size, pcm.size - offset)
                p.push(pcm, offset, take, emit)
                offset += take
            }
            p.push(pcm, offset, pcm.size - offset, emit)
        }
        assertEquals(whole, pieces)
    }

    @Test fun flushSendsWholeBufferedFramesAndDropsAPartialFrame() {
        val packetizer = PcmPacketizer(AudioStream.ALT, 48_000, 2)
        val datagrams = collect(packetizer) { p, emit ->
            p.push(ramp(250, 2), emit = emit)
            p.push(byteArrayOf(1, 2, 3), emit = emit)
            p.flush(emit)
        }
        assertEquals(2, datagrams.size)
        assertEquals(40, datagrams[1].payload.size)
        assertEquals(1, datagrams[1].header.seq)
        assertEquals(240L, datagrams[1].header.timestamp)
        assertFalse(datagrams[1].header.start)
        assertEquals(0, packetizer.pendingBytes)
        assertEquals(250L, packetizer.timestamp)
    }

    @Test fun skipFlushesTheTailThenJumpsTheTimestamp() {
        val packetizer = PcmPacketizer(AudioStream.MEDIA, 48_000, 2)
        val datagrams = collect(packetizer) { p, emit ->
            p.push(ramp(300, 2), emit = emit) // one datagram, 60 frames pending
            p.skip(1000, emit)
            p.push(ramp(240, 2), emit = emit)
        }
        assertEquals(listOf(240, 60, 240), datagrams.map { it.frames })
        assertEquals(listOf(0L, 240L, 1300L), datagrams.map { it.header.timestamp })
        assertEquals(listOf(0, 1, 2), datagrams.map { it.header.seq })
        assertEquals(1540L, packetizer.timestamp)
    }

    @Test fun skipBeforeAnyAudioIsIgnoredSoTheStreamStartsAtZero() {
        val packetizer = PcmPacketizer(AudioStream.MEDIA, 48_000, 2)
        val datagrams = collect(packetizer) { p, emit ->
            p.skip(1000, emit)
            p.push(byteArrayOf(1, 2, 3), emit = emit) // less than a frame: still nothing to place a gap after
            p.skip(1000, emit)
            p.push(ramp(240, 2), emit = emit)
        }
        assertEquals(1, datagrams.size)
        assertEquals(0L, datagrams[0].header.timestamp)
        assertTrue(datagrams[0].header.start)
    }

    @Test fun seqWrapsAfter65535() {
        val packetizer = PcmPacketizer(AudioStream.MEDIA, 8_000, 1, framesPerDatagram = 1)
        val frame = byteArrayOf(0, 0)
        var last: AudioDatagram? = null
        repeat(65_537) { packetizer.push(frame) { bytes, length -> last = SimHubAudioCodec.decode(bytes, AudioDirection.TABLET_TO_PC, 0, length) } }
        assertEquals(0, last!!.header.seq)
        assertEquals(65_536L, last!!.header.timestamp)
        assertEquals(1, packetizer.seq)
    }

    @Test(expected = IllegalArgumentException::class)
    fun rejectsRatesTheHeaderCannotCarry() {
        PcmPacketizer(AudioStream.MEDIA, 22_050, 2)
    }
}
