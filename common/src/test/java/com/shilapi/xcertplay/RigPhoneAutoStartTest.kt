package com.shilapi.xcertplay

import android.content.Context
import com.shilapi.xcertplay.orchestration.WirelessHotspotMode
import org.junit.Assert.assertEquals
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

/** #29: SimHub-triggered starts only go ahead when "Connect phone" would not need to ask anything. */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [33], manifest = Config.NONE)
class RigPhoneAutoStartTest {
    private val context get() = RuntimeEnvironment.getApplication()

    @Before fun clear() {
        context.getSharedPreferences("xcertplay_airplay", Context.MODE_PRIVATE).edit().clear().commit()
        context.getSharedPreferences("rigplay", Context.MODE_PRIVATE).edit().clear().commit()
    }

    private fun blocker() = RigPhoneSession.autoStartBlocker(context) { true }

    @Test fun automaticConnectionOff() {
        RigPlayPreferences.saveAutoConnect(context, false)
        assertEquals("automatic connection is off", blocker())
    }

    @Test fun wirelessNeedsAChosenIphone() {
        AirPlayPersistence.saveWirelessEnabled(context, true)
        assertEquals("no iPhone chosen", blocker())
        RigPlayPreferences.savePhone(context, "AA:BB:CC:DD:EE:FF", "Tim's iPhone")
        AirPlayPersistence.saveWirelessHotspotMode(context, WirelessHotspotMode.MANUAL)
        assertEquals("hotspot details missing", blocker())
        AirPlayPersistence.saveWirelessHotspotMode(context, WirelessHotspotMode.WIFI_P2P)
        assertEquals(null, blocker())
    }

    @Test fun usbNeedsNothingMore() {
        AirPlayPersistence.saveWirelessEnabled(context, false)
        assertEquals(null, blocker())
    }

    @Test fun missingAuthenticationBlocks() {
        assertEquals("CarPlay authentication unavailable", RigPhoneSession.autoStartBlocker(context) { false })
    }
}
