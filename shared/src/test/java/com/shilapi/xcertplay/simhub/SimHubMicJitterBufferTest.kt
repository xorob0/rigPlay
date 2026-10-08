package com.shilapi.xcertplay.simhub

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** The PC microphone's jitter buffer (`docs/protocol.md` §10.4): priming, gaps, reordering, late, skip, wrap. */
class SimHubMicJitterBufferTest {
    /** 16 kHz: 80-frame datagrams of 5 ms, target 40 ms (640 frames), max 200 ms (3200 frames). */
    private val buffer = SimHubMicJitterBuffer(16_000)

    private fun chunk(firstValue: Int, frames: Int = 80) = ShortArray(frames) { (firstValue + it).toShort() }

    private fun pull(frames: Int = 80): ShortArray = ShortArray(frames).also { buffer.pull(it, frames) }

    @Test fun waitsForTheTargetBeforePlayingAndDoesNotAdvanceWhileWaiting() {
        assertEquals(640, buffer.targetFrames)
        buffer.push(0, true, chunk(1))
        assertArrayEquals(ShortArray(80), pull())
        for (i in 1 until 8) buffer.push(80L * i, false, chunk(1 + 80 * i))
        // 640 frames buffered: playback starts at the first frame, nothing was skipped while waiting.
        assertArrayEquals(chunk(1), pull())
        assertArrayEquals(chunk(81), pull())
        assertEquals(480, buffer.bufferedFrames)
    }

    @Test fun reordersByTimestampAndFillsLostDatagramsWithSilence() {
        buffer.push(0, true, chunk(1))
        buffer.push(160, false, chunk(161)) // 80..159 arrives late
        buffer.push(80, false, chunk(81))
        buffer.push(320, false, chunk(321)) // 240..319 is lost
        for (i in 5 until 9) buffer.push(80L * i, false, chunk(1 + 80 * i))
        assertArrayEquals(chunk(1), pull())
        assertArrayEquals(chunk(81), pull())
        assertArrayEquals(chunk(161), pull())
        assertArrayEquals(ShortArray(80), pull())
        assertArrayEquals(chunk(321), pull())
        assertEquals(80L * 4, buffer.playedFrames)
    }

    @Test fun aDatagramPastItsPlayOutTimeIsDropped() {
        for (i in 0 until 8) buffer.push(80L * i, i == 0, chunk(1 + 80 * i))
        pull(); pull()
        buffer.push(80, false, chunk(81))
        assertEquals(1L, buffer.late)
        assertArrayEquals(chunk(161), pull())
    }

    @Test fun anUnderrunPlaysSilenceThenPrimesAgainWithoutPlayingTheGap() {
        for (i in 0 until 8) buffer.push(80L * i, i == 0, chunk(1 + 80 * i))
        repeat(8) { pull() }
        assertFalse(buffer.pull(ShortArray(80)))
        assertEquals(1L, buffer.underruns)
        // The PC kept its clock: the next audio is 1 s later. It plays from its first frame once primed.
        for (i in 0 until 8) buffer.push(16_000L + 80 * i, false, chunk(5000 + 80 * i))
        assertArrayEquals(chunk(5000), pull())
    }

    @Test fun tooMuchBufferedSkipsAheadToTheTarget() {
        for (i in 0 until 50) buffer.push(80L * i, i == 0, chunk(1 + 80 * i)) // 4000 frames > 3200
        assertTrue(buffer.skipped >= 1)
        assertTrue("buffered ${buffer.bufferedFrames}", buffer.bufferedFrames <= buffer.maxFrames)
        val out = pull()
        assertTrue(out[0] > 0)
    }

    @Test fun theStartFlagResetsTheStream() {
        for (i in 0 until 8) buffer.push(80L * i, i == 0, chunk(1 + 80 * i))
        pull()
        for (i in 0 until 8) buffer.push(80L * i, i == 0, chunk(9000 + 80 * i))
        assertArrayEquals(chunk(9000), pull())
    }

    @Test fun timestampsWrapAtU32() {
        val first = 0xFFFF_FFFFL - 159 // the third datagram wraps to 0
        for (i in 0 until 8) buffer.push((first + 80L * i) and 0xFFFF_FFFFL, i == 0, chunk(1 + 80 * i))
        for (i in 0 until 8) assertArrayEquals("datagram $i", chunk(1 + 80 * i), pull())
        assertEquals(0L, buffer.late)
    }

    @Test fun pullsOfAnySizeSpanDatagrams() {
        for (i in 0 until 8) buffer.push(80L * i, i == 0, chunk(1 + 80 * i))
        val out = pull(100)
        assertArrayEquals(ShortArray(100) { (1 + it).toShort() }, out)
        assertArrayEquals(ShortArray(30) { (101 + it).toShort() }, pull(30))
    }
}
