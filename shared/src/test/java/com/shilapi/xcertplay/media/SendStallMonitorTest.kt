package com.shilapi.xcertplay.media

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class SendStallMonitorTest {
    private val ms = 1_000_000L

    /** Sends one datagram at [at] ms taking [took] ms. */
    private fun SendStallMonitor.send(at: Long, took: Long = 0) {
        beforeSend(at * ms)
        afterSend((at + took) * ms)
    }

    @Test fun aQuietWindowReportsNothing() {
        val monitor = SendStallMonitor("media", windowMillis = 1_000, thresholdMillis = 60)
        for (t in 0 until 1_000 step 5) monitor.send(t.toLong(), took = 1)
        assertNull(monitor.report(999 * ms)) // the window is not over yet
        assertNull(monitor.report(1_000 * ms))
        assertEquals(1L, monitor.longestSendEverMillis)
        assertEquals(4L, monitor.longestGapEverMillis)
    }

    @Test fun aBlockingSendIsBlamedOnTheWifi() {
        val monitor = SendStallMonitor("media", windowMillis = 1_000, thresholdMillis = 60)
        monitor.send(0)
        monitor.send(5, took = 320) // socket.send blocked: the driver's queue was full
        for (t in 330 until 1_000 step 5) monitor.send(t.toLong())
        val line = monitor.report(1_000 * ms)
        assertEquals(
            "PC audio: media stalled in the last 1 s: longest socket.send 320 ms, longest gap between datagrams 5 ms (136 sent); the Wi-Fi held the datagrams back",
            line,
        )
        assertEquals(320L, monitor.longestSendEverMillis)
        // The next window starts clean.
        for (t in 1_000 until 2_000 step 5) monitor.send(t.toLong())
        assertNull(monitor.report(2_000 * ms))
        assertEquals(320L, monitor.longestSendEverMillis)
    }

    @Test fun aLongGapBetweenQuickSendsIsBlamedOnTheTablet() {
        val monitor = SendStallMonitor("alt", windowMillis = 1_000, thresholdMillis = 60)
        monitor.send(0, took = 1)
        monitor.send(410, took = 1) // nothing left the sender for 409 ms, then it sent at once
        val line = monitor.report(1_000 * ms)!!
        assertTrue(line, line.startsWith("PC audio: alt stalled in the last 1 s: longest socket.send 1 ms, longest gap between datagrams 409 ms (2 sent)"))
        assertTrue(line, line.endsWith("the tablet produced them late"))
        assertEquals(409L, monitor.longestGapEverMillis)
    }

    @Test fun theSummaryFoldsTheOpenWindowIn() {
        val monitor = SendStallMonitor("media", windowMillis = 10_000)
        monitor.send(0, took = 250)
        monitor.send(900, took = 1)
        assertEquals("longestSend=250ms longestGap=650ms", monitor.summary(1_000 * ms))
        assertNull(monitor.report(11_000 * ms)) // the folded window is not reported again
    }

    @Test fun theWindowOnlyEndsOnceItIsFull() {
        val monitor = SendStallMonitor("media", windowMillis = 10_000)
        monitor.send(0, took = 500)
        assertNull(monitor.report(9_999 * ms))
        assertTrue(monitor.report(10_000 * ms)!!.contains("longest socket.send 500 ms"))
    }
}
