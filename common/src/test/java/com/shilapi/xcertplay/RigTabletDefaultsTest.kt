package com.shilapi.xcertplay

import android.content.Context
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

/** #28: a fresh install opens after boot and connects on its own; both stay user-switchable. */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [33], manifest = Config.NONE)
class RigTabletDefaultsTest {
    private val context get() = RuntimeEnvironment.getApplication()

    @Before fun clear() {
        context.getSharedPreferences("xcertplay_airplay", Context.MODE_PRIVATE).edit().clear().commit()
        context.getSharedPreferences("rigplay", Context.MODE_PRIVATE).edit().clear().commit()
    }

    @Test fun freshInstallStartsOnBootAndConnectsAutomatically() {
        assertTrue(AirPlayPersistence.loadAutoStartOnBoot(context))
        assertTrue(RigPlayPreferences.autoConnect(context))
    }

    @Test fun userChoiceIsKept() {
        AirPlayPersistence.saveAutoStartOnBoot(context, false)
        RigPlayPreferences.saveAutoConnect(context, false)
        assertFalse(AirPlayPersistence.loadAutoStartOnBoot(context))
        assertFalse(RigPlayPreferences.autoConnect(context))
    }
}
