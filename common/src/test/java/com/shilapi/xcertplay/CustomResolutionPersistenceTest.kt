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
@Config(sdk = [29], manifest = Config.NONE)
class CustomResolutionPersistenceTest {
    private val app get() = RuntimeEnvironment.getApplication()

    @Before fun setUp() {
        app.getSharedPreferences("xcertplay_airplay", Context.MODE_PRIVATE).edit().clear().commit()
    }

    @Test fun defaultsTo100Percent() {
        assertEquals(100, AirPlayPersistence.loadDisplayScalePercent(app))
    }

    @Test fun aResolutionSavedAsTenthsByAnOlderBuildIsCarriedOver() {
        AirPlayPersistence.saveDisplayScaleTenths(app, 8)
        assertEquals(80, AirPlayPersistence.loadDisplayScalePercent(app))
    }

    @Test fun percentagesAreClampedTo30Through160() {
        AirPlayPersistence.saveDisplayScalePercent(app, 145)
        assertEquals(145, AirPlayPersistence.loadDisplayScalePercent(app))
        AirPlayPersistence.saveDisplayScalePercent(app, 400)
        assertEquals(160, AirPlayPersistence.loadDisplayScalePercent(app))
        AirPlayPersistence.saveDisplayScalePercent(app, 5)
        assertEquals(30, AirPlayPersistence.loadDisplayScalePercent(app))
    }
}
