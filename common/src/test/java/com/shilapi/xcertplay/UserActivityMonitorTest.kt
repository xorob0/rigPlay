package com.shilapi.xcertplay

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class UserActivityMonitorTest {
    private var now = 1_000L
    private val monitor = UserActivityMonitor { now }

    @Test fun startsActiveAtCreation() {
        assertEquals(1_000L, monitor.lastInteractionAt)
        assertEquals(0L, monitor.idleForMs())
    }

    @Test fun countsTimeSinceTheLastInteraction() {
        now += 90_000L
        assertEquals(90_000L, monitor.idleForMs())
        monitor.onUserInteraction()
        assertEquals(91_000L, monitor.lastInteractionAt)
        assertEquals(0L, monitor.idleForMs())
        now += 5_000L
        assertEquals(5_000L, monitor.idleForMs())
        assertEquals(96_000L, monitor.now())
    }

    @Test fun aClockGoingBackDoesNotGoNegative() {
        now -= 10_000L
        assertEquals(0L, monitor.idleForMs())
    }

    @Test fun defaultClockIsMonotonic() {
        val real = UserActivityMonitor()
        assertTrue(real.now() >= real.lastInteractionAt)
    }
}
