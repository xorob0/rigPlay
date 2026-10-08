package com.shilapi.xcertplay

import android.content.Context
import org.junit.Assert.assertEquals
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [33], manifest = Config.NONE)
class IdleSettingsPersistenceTest {
    private val context get() = RuntimeEnvironment.getApplication()
    private val prefs get() = context.getSharedPreferences("xcertplay_airplay", Context.MODE_PRIVATE)

    @Before fun clear() { prefs.edit().clear().commit() }

    @Test fun defaultsAreTheIdleDashboardAndNoScreenOff() {
        assertEquals(IdleMode.DASHBOARD, AirPlayPersistence.loadIdleMode(context))
        assertEquals(0, AirPlayPersistence.loadIdleScreenOffMinutes(context))
    }

    @Test fun settingsRoundTripUnderIdleKeys() {
        AirPlayPersistence.saveIdleMode(context, IdleMode.RIGPLAY_SCREEN)
        AirPlayPersistence.saveIdleScreenOffMinutes(context, 15)
        assertEquals(IdleMode.RIGPLAY_SCREEN, AirPlayPersistence.loadIdleMode(context))
        assertEquals(15, AirPlayPersistence.loadIdleScreenOffMinutes(context))
        assertEquals("screen", prefs.getString("idle_mode", null))
        assertEquals(15, prefs.getInt("idle_screen_off_minutes", -1))
    }

    @Test fun unknownValuesFallBackToTheDefaults() {
        prefs.edit().putString("idle_mode", "hologram").putInt("idle_screen_off_minutes", 7).commit()
        assertEquals(IdleMode.DASHBOARD, AirPlayPersistence.loadIdleMode(context))
        assertEquals(0, AirPlayPersistence.loadIdleScreenOffMinutes(context))
        AirPlayPersistence.saveIdleScreenOffMinutes(context, -3)
        assertEquals(0, prefs.getInt("idle_screen_off_minutes", -1))
    }
}
