package com.shilapi.xcertplay

import android.content.Context
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [33], manifest = Config.NONE)
class SimHubPersistenceTest {
    private val context get() = RuntimeEnvironment.getApplication()
    private val prefs get() = context.getSharedPreferences("xcertplay_airplay", Context.MODE_PRIVATE)
    private val pairing = SimHubPairing("host-1", "192.168.1.20", 23711, "RIG-PC", "q3Z2b0x9V1mN8pR4sT6uW7yA5cE1gH3jK2lM0nO9pQ8")

    @Before fun clear() { prefs.edit().clear().commit() }

    @Test fun freshInstallIsUnpairedWithSpecPorts() {
        assertNull(AirPlayPersistence.loadSimHubPairing(context))
        assertEquals(23711, AirPlayPersistence.loadSimHubControlPort(context))
        assertEquals(23710, AirPlayPersistence.loadSimHubDiscoveryPort(context))
    }

    @Test fun pairingRoundTripsUnderSimHubKeys() {
        AirPlayPersistence.saveSimHubPairing(context, pairing)
        assertEquals(pairing, AirPlayPersistence.loadSimHubPairing(context))
        assertEquals("host-1", prefs.getString("simhub_host_id", null))
        assertEquals(pairing.token, prefs.getString("simhub_token", null))
    }

    @Test fun missingTokenMeansUnpaired() {
        AirPlayPersistence.saveSimHubPairing(context, pairing)
        prefs.edit().remove("simhub_token").commit()
        assertNull(AirPlayPersistence.loadSimHubPairing(context))
    }

    @Test fun beaconAddressUpdateKeepsCredentials() {
        AirPlayPersistence.saveSimHubPairing(context, pairing)
        AirPlayPersistence.saveSimHubAddress(context, "192.168.1.42", 24000)
        assertEquals(pairing.copy(host = "192.168.1.42", port = 24000), AirPlayPersistence.loadSimHubPairing(context))
    }

    @Test fun forgetClearsThePairingButKeepsTabletIdAndPorts() {
        val tabletId = AirPlayPersistence.loadSimHubTabletId(context)
        AirPlayPersistence.saveSimHubControlPort(context, 24711)
        AirPlayPersistence.saveSimHubPairing(context, pairing)
        AirPlayPersistence.clearSimHubPairing(context)
        assertNull(AirPlayPersistence.loadSimHubPairing(context))
        assertNull(prefs.getString("simhub_token", null))
        assertEquals(tabletId, AirPlayPersistence.loadSimHubTabletId(context))
        assertEquals(24711, AirPlayPersistence.loadSimHubControlPort(context))
    }

    @Test fun tabletIdIsStable() {
        val first = AirPlayPersistence.loadSimHubTabletId(context)
        assertNotNull(first)
        assertEquals(first, AirPlayPersistence.loadSimHubTabletId(context))
    }

    @Test fun invalidPortsFallBackToDefaults() {
        AirPlayPersistence.saveSimHubDiscoveryPort(context, 70000)
        assertEquals(23710, AirPlayPersistence.loadSimHubDiscoveryPort(context))
        prefs.edit().putInt("simhub_control_port", 0).commit()
        assertEquals(23711, AirPlayPersistence.loadSimHubControlPort(context))
    }
}
