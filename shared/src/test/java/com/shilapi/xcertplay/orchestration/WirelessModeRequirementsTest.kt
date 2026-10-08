package com.shilapi.xcertplay.orchestration

import com.shilapi.xcertplay.network.isServingInterfaceCandidate
import com.shilapi.xcertplay.transport.Iap2IdentificationConfig
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

/** #33: the two "the tablet owns the network" checks are lifted for the existing-network mode only. */
class WirelessModeRequirementsTest {
    @Test fun onlyTheTabletHotspotNeedsTethering() {
        assertTrue(WirelessModeRequirements.requiresTethering(WirelessHotspotMode.MANUAL))
        assertFalse(WirelessModeRequirements.requiresTethering(WirelessHotspotMode.WIFI_P2P))
        assertFalse(WirelessModeRequirements.requiresTethering(WirelessHotspotMode.LOCAL_ONLY_HOTSPOT))
        assertFalse(WirelessModeRequirements.requiresTethering(WirelessHotspotMode.EXISTING_NETWORK))
    }

    @Test fun ownNetworkModesStillSkipTheStationInterface() {
        for (mode in listOf(WirelessHotspotMode.MANUAL, WirelessHotspotMode.WIFI_P2P, WirelessHotspotMode.LOCAL_ONLY_HOTSPOT)) {
            assertFalse(mode.name, isServingInterfaceCandidate("wlan0", stationInterface = "wlan0", mode = mode))
            assertTrue(mode.name, isServingInterfaceCandidate("wlan1", stationInterface = "wlan0", mode = mode))
        }
    }

    @Test fun existingNetworkUsesOnlyTheStationInterface() {
        assertTrue(isServingInterfaceCandidate("wlan0", "wlan0", WirelessHotspotMode.EXISTING_NETWORK))
        assertFalse(isServingInterfaceCandidate("wlan1", "wlan0", WirelessHotspotMode.EXISTING_NETWORK))
        assertFalse(isServingInterfaceCandidate("wlan0", null, WirelessHotspotMode.EXISTING_NETWORK))
    }

    @Test fun onlyWifiDirectFallsBackOnOldAndroid() {
        assertEquals(WirelessHotspotMode.LOCAL_ONLY_HOTSPOT, WirelessModeRequirements.effectiveMode(WirelessHotspotMode.WIFI_P2P, 28))
        assertEquals(WirelessHotspotMode.WIFI_P2P, WirelessModeRequirements.effectiveMode(WirelessHotspotMode.WIFI_P2P, 29))
        assertEquals(WirelessHotspotMode.EXISTING_NETWORK, WirelessModeRequirements.effectiveMode(WirelessHotspotMode.EXISTING_NETWORK, 28))
        assertEquals(WirelessHotspotMode.MANUAL, WirelessModeRequirements.effectiveMode(WirelessHotspotMode.MANUAL, 28))
    }

    @Test fun savedCredentialsAreNeededForTheTwoModesTheUserTypesIn() {
        assertEquals(
            setOf(WirelessHotspotMode.MANUAL, WirelessHotspotMode.EXISTING_NETWORK),
            WirelessHotspotMode.entries.filter(WirelessModeRequirements::needsSavedCredentials).toSet(),
        )
    }

    @Test fun existingNetworkConfigNeedsNoTabletHotspotDetails() {
        val config = config(ssid = "", passphrase = "")
        assertEquals(WirelessHotspotMode.EXISTING_NETWORK, config.wirelessHotspotMode)
        config(ssid = "Home", passphrase = "home-password")
    }

    @Test fun existingNetworkConfigRejectsAnInvalidPassword() {
        assertThrows(IllegalArgumentException::class.java) { config(ssid = "Home", passphrase = "short") }
        assertThrows(IllegalArgumentException::class.java) { config(ssid = "Home\u0000", passphrase = "") }
    }

    private fun config(ssid: String, passphrase: String) = CarPlayRuntimeConfig(
        identification = Iap2IdentificationConfig(
            name = "test",
            modelIdentifier = "test",
            manufacturer = "test",
            serialNumber = "test",
            firmwareVersion = "1",
            hardwareVersion = "1",
            carPlayUsbInterfaceNumber = 3,
        ),
        transport = CarPlayTransport.WIRELESS,
        wirelessHotspotMode = WirelessHotspotMode.EXISTING_NETWORK,
        existingNetworkSsid = ssid,
        existingNetworkPassphrase = passphrase,
    )
}
