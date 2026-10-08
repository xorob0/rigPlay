package com.shilapi.xcertplay.network

import com.shilapi.xcertplay.orchestration.WirelessHotspotMode
import com.shilapi.xcertplay.transport.Iap2WirelessSecurity
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.IOException
import java.net.Inet6Address
import java.net.InetAddress

/** #33: existing Wi-Fi network mode, assembled from the station connection without Android. */
class ExistingNetworkHotspotInfoTest {
    private val lan = InetAddress.getByName("192.168.1.20")
    private val linkLocal = InetAddress.getByName("fe80::1234")

    private fun station(
        ssid: String? = "Home",
        frequency: Int? = 5180,
        addresses: List<InetAddress> = listOf(linkLocal, lan),
        index: Int = 7,
    ) = StationWifiSnapshot("wlan0", addresses, index, ssid, "aa:bb:cc:dd:ee:ff", frequency)

    @Test fun channelComesFromTheStationFrequencyOnEveryBand() {
        mapOf(2412 to 1, 2437 to 6, 5180 to 36, 5745 to 149, 5955 to 1).forEach { (mhz, channel) ->
            assertEquals("$mhz MHz", channel, station(frequency = mhz).channel)
        }
        assertEquals("6 GHz", station(frequency = 5955).bandLabel)
        assertEquals(0, station(frequency = null).channel)
        assertEquals(0, station(frequency = 2413).channel)
    }

    @Test fun readableNetworkNameIsUsedAndMarkedReadable() {
        val info = existingNetworkHotspotInfo(station(), configuredSsid = "", passphrase = "home-password")
        assertEquals("Home", info.ssid)
        assertEquals(true, info.ssidReadable)
        assertEquals(36, info.channel)
        assertEquals(5180, info.frequencyMHz)
        assertEquals("5 GHz", info.bandLabel)
        assertEquals("wlan0", info.interfaceName)
        // The iPhone gets the LAN IPv4 address; both families are served, the IPv6 one scoped.
        assertEquals(lan, info.hostAddress)
        assertEquals(listOf(lan, linkLocal), info.hostAddresses)
        assertEquals(7, (info.hostAddresses[1] as Inet6Address).scopeId)
        assertEquals(Iap2WirelessSecurity.WPA_WPA2, info.security)
        assertEquals(WirelessHotspotBackend.EXISTING_NETWORK, info.backend)
        // The router BSSID must not become rigPlay's AirPlay device identifier.
        assertNull(info.bssid)
    }

    @Test fun hiddenNetworkNameFallsBackToTheSavedOne() {
        val info = existingNetworkHotspotInfo(station(ssid = null), configuredSsid = " Home ", passphrase = "home-password")
        assertEquals("Home", info.ssid)
        assertEquals(false, info.ssidReadable)
    }

    @Test fun hiddenNameWithNothingSavedFails() {
        assertThrows(IOException::class.java) {
            existingNetworkHotspotInfo(station(ssid = null), configuredSsid = "", passphrase = "home-password")
        }
    }

    @Test fun savedNameForAnotherNetworkFailsWithoutLeakingThePassword() {
        val error = assertThrows(IOException::class.java) {
            existingNetworkHotspotInfo(station(ssid = "Neighbour"), configuredSsid = "Home", passphrase = "home-password")
        }
        assertFalse(error.message!!.contains("home-password"))
    }

    @Test fun openNetworkHasNoSecurity() {
        assertEquals(Iap2WirelessSecurity.NONE, existingNetworkHotspotInfo(station(), "", "").security)
    }

    @Test fun ipv4IsPreferredAndAnUnscopedLinkLocalAddressIsNotUsed() {
        val v4 = existingNetworkHotspotInfo(station(index = 0), "", "")
        assertEquals(lan, v4.hostAddress)
        assertEquals(listOf(lan), v4.hostAddresses)
        assertEquals(lan, existingNetworkHotspotInfo(station(addresses = listOf(lan)), "", "").hostAddress)
        assertThrows(IOException::class.java) {
            existingNetworkHotspotInfo(station(addresses = listOf(linkLocal), index = 0), "", "")
        }
        assertNull(station(addresses = emptyList()).hostAddress)
    }

    @Test fun theRouterAddressIsOnlyAnApHint() {
        val info = existingNetworkHotspotInfo(station(), "", "")
        assertArrayEquals(byteArrayOf(0xaa.toByte(), 0xbb.toByte(), 0xcc.toByte(), 0xdd.toByte(), 0xee.toByte(), 0xff.toByte()),
            info.accessPointBssid)
        assertNull(info.bssid)
        listOf(null, "", "02:00:00:00:00:00", "00:00:00:00:00:00", "01:00:5e:00:00:01", "not-a-mac", "aa:bb:cc:dd:ee").forEach {
            assertNull(it, accessPointBssid(it))
        }
    }

    @Test fun openOrSecuredMismatchesAreReportedWhenAndroidKnowsTheSecurity() {
        assertTrue(securityMismatch(android.net.wifi.WifiInfo.SECURITY_TYPE_OPEN, "home-password")!!.contains("open"))
        assertTrue(securityMismatch(android.net.wifi.WifiInfo.SECURITY_TYPE_PSK, "")!!.contains("secured"))
        assertTrue(securityMismatch(android.net.wifi.WifiInfo.SECURITY_TYPE_SAE, "")!!.contains("secured"))
        assertNull(securityMismatch(android.net.wifi.WifiInfo.SECURITY_TYPE_OPEN, ""))
        assertNull(securityMismatch(android.net.wifi.WifiInfo.SECURITY_TYPE_PSK, "home-password"))
        assertNull(securityMismatch(android.net.wifi.WifiInfo.SECURITY_TYPE_UNKNOWN, ""))
    }

    @Test fun androidSsidFormsAreNormalised() {
        assertEquals("Home Wi-Fi", readableSsid("\"Home Wi-Fi\""))
        assertNull(readableSsid("<unknown ssid>"))
        assertNull(readableSsid(null))
        assertNull(readableSsid("\"\""))
        assertNull(readableSsid("486f6d65")) // Non-UTF-8 names come back as unquoted hex.
    }

    @Test fun passwordNeverAppearsInToString() {
        val info = existingNetworkHotspotInfo(station(), "", "home-password")
        assertFalse(info.toString().contains("home-password"))
        assertTrue(info.toString().contains("passphrase=<redacted>"))
    }

    @Test fun startupContextRecordsModeInterfaceChannelAndNameReadability() {
        val info = existingNetworkHotspotInfo(station(ssid = null), "Home", "home-password")
        val line = wirelessStartupContext(WirelessHotspotMode.EXISTING_NETWORK, info)
        assertEquals("mode=EXISTING_NETWORK iface=wlan0 channel=36 frequency=5180MHz networkNameReadable=false", line)
        // The exported-report redactor drops whole lines that mention these words.
        assertFalse(line.contains("ssid", ignoreCase = true) || line.contains("pass", ignoreCase = true))
        assertFalse(line.contains("Home"))
    }

    @Test fun ownNetworkModesReportNameReadabilityAsNotApplicable() {
        val info = WirelessHotspotInfo(
            "rigPlay", "secret-pass", Iap2WirelessSecurity.WPA_WPA2, 149, 5745, null, "p2p-wlan0-0",
            lan, "5 GHz", WirelessHotspotBackend.WIFI_P2P,
        )
        assertTrue(wirelessStartupContext(WirelessHotspotMode.WIFI_P2P, info).endsWith("networkNameReadable=not_applicable"))
    }

    @Test fun startupDiagnosticsRepeatTheContextOnEveryLine() {
        val logs = mutableListOf<String>()
        val diagnostics = WirelessStartupDiagnostics({ "interfaceState=up" }, { logs.add(it) }, context = "mode=EXISTING_NETWORK iface=wlan0")
        diagnostics.close()
        assertTrue(logs.single().contains("mode=EXISTING_NETWORK iface=wlan0 observation=ended"))
    }
}
