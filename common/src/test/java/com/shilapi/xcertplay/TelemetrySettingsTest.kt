package com.shilapi.xcertplay

import android.content.Context
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [33], manifest = Config.NONE)
class TelemetrySettingsTest {
    private val context get() = RuntimeEnvironment.getApplication()
    private val prefs get() = context.getSharedPreferences("xcertplay_airplay", Context.MODE_PRIVATE)

    @Before fun clear() { prefs.edit().clear().commit() }

    @Test fun locationSourceFollowsTheOlderSwitchUntilChosen() {
        assertEquals(LocationSource.NONE, AirPlayPersistence.loadLocationSource(context))
        AirPlayPersistence.saveLocationReportingEnabled(context, true)
        assertEquals(LocationSource.TABLET, AirPlayPersistence.loadLocationSource(context))
    }

    @Test fun locationSourceRoundTripsAndKeepsTheTabletFlagInStep() {
        AirPlayPersistence.saveLocationSource(context, LocationSource.SIMHUB)
        assertEquals(LocationSource.SIMHUB, AirPlayPersistence.loadLocationSource(context))
        assertEquals("simhub", prefs.getString("location_source", null))
        assertFalse("SimHub needs no tablet GPS", AirPlayPersistence.loadLocationReportingEnabled(context))

        AirPlayPersistence.saveLocationSource(context, LocationSource.TABLET)
        assertEquals(LocationSource.TABLET, AirPlayPersistence.loadLocationSource(context))
        assertTrue(AirPlayPersistence.loadLocationReportingEnabled(context))

        AirPlayPersistence.saveLocationSource(context, LocationSource.NONE)
        assertEquals(LocationSource.NONE, AirPlayPersistence.loadLocationSource(context))
    }

    @Test fun nightFromSimHubDefaultsToTheLocationSourceUntilChosen() {
        assertFalse(AirPlayPersistence.loadNightFromSimHub(context))
        AirPlayPersistence.saveLocationSource(context, LocationSource.SIMHUB)
        assertTrue(AirPlayPersistence.loadNightFromSimHub(context))
        AirPlayPersistence.saveNightFromSimHub(context, false)
        assertFalse(AirPlayPersistence.loadNightFromSimHub(context))
        AirPlayPersistence.saveLocationSource(context, LocationSource.NONE)
        AirPlayPersistence.saveNightFromSimHub(context, true)
        assertTrue(AirPlayPersistence.loadNightFromSimHub(context))
    }

    @Test fun vehicleStatusIsOptIn() {
        AirPlayPersistence.saveLocationSource(context, LocationSource.SIMHUB)
        assertFalse("declares an EV, so never on by default", AirPlayPersistence.loadSimHubVehicleStatus(context))
        AirPlayPersistence.saveSimHubVehicleStatus(context, true)
        assertTrue(AirPlayPersistence.loadSimHubVehicleStatus(context))
    }

    @Test fun deniedPreciseLocationTurnsTheTabletSourceOff() {
        AirPlayPersistence.saveLocationSource(context, LocationSource.TABLET)
        // What the permission callbacks do when precise location is refused.
        AirPlayPersistence.saveLocationReportingEnabled(context, false)
        assertEquals(LocationSource.NONE, AirPlayPersistence.loadLocationSource(context))
    }
}
