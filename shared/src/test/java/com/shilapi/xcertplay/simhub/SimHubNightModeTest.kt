package com.shilapi.xcertplay.simhub

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class SimHubNightModeTest {
    private val mode = SimHubNightMode(debounceMillis = 2_000)

    @Test fun offFollowsTheTabletOnly() {
        assertNull(mode.update(enabled = false, night = true, androidNight = false, nowMillis = 0))
        assertFalse(mode.effective(androidNight = false))
        assertTrue(mode.effective(androidNight = true))
    }

    @Test fun firstSimHubValueOverridesTheTabletAtOnce() {
        assertEquals(true, mode.update(enabled = true, night = true, androidNight = false, nowMillis = 0))
        assertTrue(mode.effective(androidNight = false))
        assertNull("same value again: no change to send", mode.update(true, true, false, 100))
    }

    @Test fun changesAreDebounced() {
        mode.update(true, false, androidNight = true, nowMillis = 0)
        assertFalse(mode.effective(androidNight = true))

        assertNull(mode.update(true, true, true, 1_000))
        assertNull("not held long enough", mode.update(true, true, true, 2_999))
        assertEquals(true, mode.update(true, true, true, 3_000))
        assertNull(mode.update(true, true, true, 3_100))
    }

    @Test fun aFlickerShorterThanTheDebounceIsIgnored() {
        mode.update(true, false, false, 0)
        assertNull(mode.update(true, true, false, 500))
        assertNull("back to day: the pending change is dropped", mode.update(true, false, false, 900))
        assertNull("night again starts a new wait", mode.update(true, true, false, 2_600))
        assertNull(mode.update(true, true, false, 4_599))
        assertEquals(true, mode.update(true, true, false, 4_600))
    }

    @Test fun staleOrMissingValueFallsBackToTheTablet() {
        mode.update(true, true, androidNight = false, nowMillis = 0)
        assertEquals("stale stream: tablet day mode again", false, mode.update(true, null, false, 10_000))
        assertNull("tablet is already night: nothing changes", run {
            mode.update(true, true, true, 20_000)
            mode.update(true, null, true, 30_000)
        })
    }

    @Test fun turningTheSettingOffOrOnSendsTheDifference() {
        mode.update(true, true, androidNight = false, nowMillis = 0)
        assertEquals(false, mode.update(enabled = false, night = true, androidNight = false, nowMillis = 100))
        assertEquals(true, mode.update(enabled = true, night = true, androidNight = false, nowMillis = 200))
    }
}
