package com.shilapi.xcertplay

import android.content.Context
import android.net.ConnectivityManager
import android.net.LinkAddress
import android.net.LinkProperties
import android.net.NetworkCapabilities
import android.net.NetworkInfo
import android.net.wifi.WifiInfo
import android.net.wifi.WifiManager
import com.shilapi.xcertplay.network.AndroidStationWifiReader
import com.shilapi.xcertplay.network.ExistingNetworkHotspotManager
import com.shilapi.xcertplay.network.StationWifiReader
import com.shilapi.xcertplay.network.WirelessHotspotBackend
import com.shilapi.xcertplay.network.WirelessHotspotInfo
import com.shilapi.xcertplay.orchestration.WirelessHotspotMode
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import org.robolectric.shadows.ShadowLog
import org.robolectric.shadows.ShadowNetwork
import org.robolectric.shadows.ShadowNetworkCapabilities
import org.robolectric.shadows.ShadowNetworkInfo
import org.robolectric.shadows.ShadowWifiInfo
import java.io.Closeable
import java.io.IOException
import java.net.InetAddress
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

/** #33: existing Wi-Fi network mode driven through fake WifiInfo / LinkProperties. */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [33], manifest = Config.NONE)
class ExistingNetworkHotspotManagerTest {
    private val context get() = RuntimeEnvironment.getApplication()

    private class FakeReader(var link: LinkProperties?, var info: WifiInfo?) : StationWifiReader {
        var watches = 0
        var released = 0
        var onLost: (() -> Unit)? = null
        override fun wifiLinkProperties() = link
        override fun wifiInfo() = info
        override fun watch(onLost: () -> Unit): Closeable {
            watches++
            this.onLost = onLost
            return Closeable { released++ }
        }
    }

    @Before fun clear() {
        ShadowLog.clear()
        context.getSharedPreferences("xcertplay_airplay", Context.MODE_PRIVATE).edit().clear().commit()
        context.getSharedPreferences("rigplay", Context.MODE_PRIVATE).edit().clear().commit()
    }

    private fun link(name: String = "wlan0", vararg addresses: String = arrayOf("192.168.1.20")) = LinkProperties().apply {
        interfaceName = name
        // LinkAddress(InetAddress, Int) and addLinkAddress are hidden in the public SDK stubs.
        val constructor = LinkAddress::class.java.getConstructor(InetAddress::class.java, Int::class.javaPrimitiveType)
        val add = LinkProperties::class.java.getMethod("addLinkAddress", LinkAddress::class.java)
        addresses.forEach { add.invoke(this, constructor.newInstance(InetAddress.getByName(it), if (':' in it) 64 else 24)) }
    }

    private fun wifiInfo(ssid: String? = "Home", bssid: String = "aa:bb:cc:dd:ee:ff", frequency: Int = 5180): WifiInfo =
        ShadowWifiInfo.newInstance().also { info ->
            val shadow = shadowOf(info)
            if (ssid != null) shadow.setSSID(ssid)
            shadow.setBSSID(bssid)
            shadow.setFrequency(frequency)
        }

    /** start() refuses the main thread, like the other managers. */
    private fun <T> background(block: () -> T): T {
        val worker = Executors.newSingleThreadExecutor()
        try {
            return worker.submit<T> { block() }.get(5, TimeUnit.SECONDS)
        } catch (failure: java.util.concurrent.ExecutionException) {
            throw failure.cause ?: failure
        } finally {
            worker.shutdownNow()
        }
    }

    @Test fun assemblesTheHotspotFromTheStationConnection() {
        val reader = FakeReader(link(), wifiInfo())
        val logs = mutableListOf<String>()
        val manager = ExistingNetworkHotspotManager(context, "", "home-password", logs::add, reader)
        val info = background { manager.start(1000) }
        assertEquals("Home", info.ssid)
        assertEquals(true, info.ssidReadable)
        assertEquals("home-password", info.passphrase)
        assertEquals("wlan0", info.interfaceName)
        assertEquals(InetAddress.getByName("192.168.1.20"), info.hostAddress)
        assertEquals(36, info.channel)
        assertEquals(WirelessHotspotBackend.EXISTING_NETWORK, info.backend)
        assertNull(info.bssid)
        assertTrue(logs.single().contains("networkNameReadable=true routerAddressReadable=true"))
        manager.close()
    }

    @Test fun hiddenNetworkNameFallsBackToSettings() {
        // Android 10+ without location permission: "<unknown ssid>" and a redacted BSSID.
        val reader = FakeReader(link(), wifiInfo(ssid = null, bssid = "02:00:00:00:00:00", frequency = 2437))
        val logs = mutableListOf<String>()
        val info = background { ExistingNetworkHotspotManager(context, "Home", "home-password", logs::add, reader).start(1000) }
        assertEquals("Home", info.ssid)
        assertEquals(false, info.ssidReadable)
        assertEquals(6, info.channel)
        assertTrue(logs.single().contains("networkNameReadable=false routerAddressReadable=false"))
    }

    @Test fun hiddenNameAndNothingSavedFails() {
        val reader = FakeReader(link(), wifiInfo(ssid = null))
        val failure = runCatching { background { ExistingNetworkHotspotManager(context, "", "home-password", {}, reader).start(1000) } }
        assertTrue(failure.exceptionOrNull() is IOException)
    }

    @Test fun notOnWifiTimesOut() {
        val reader = FakeReader(null, null)
        val failure = runCatching { background { ExistingNetworkHotspotManager(context, "Home", "", {}, reader).start(300) } }
        assertTrue(failure.exceptionOrNull() is IOException)
        assertEquals(0, reader.watches)
    }

    @Test fun closeOnlyReleasesTheCallback() {
        val reader = FakeReader(link(), wifiInfo())
        val manager = ExistingNetworkHotspotManager(context, "", "home-password", {}, reader)
        background { manager.start(1000) }
        assertEquals("association=not_exposed station=connected", manager.connectionDiagnosticSnapshot())
        reader.onLost!!.invoke()
        assertEquals("association=not_exposed station=lost", manager.connectionDiagnosticSnapshot())
        manager.close()
        manager.close()
        assertEquals(1, reader.watches)
        assertEquals(1, reader.released)
    }

    @Test fun passwordNeverReachesLogsReportsOrToString() {
        val logs = mutableListOf<String>()
        val manager = ExistingNetworkHotspotManager(context, "Home", "home-password", logs::add, FakeReader(link(), wifiInfo(ssid = null)))
        val info: WirelessHotspotInfo = background { manager.start(1000) }
        val startup = com.shilapi.xcertplay.network.wirelessStartupContext(WirelessHotspotMode.EXISTING_NETWORK, info)
        val everything = logs + ShadowLog.getLogs().map { it.msg } + manager.toString() + info.toString() + startup
        assertTrue(everything.none { it.contains("home-password") })
        // The lines must also survive the exported-report redactor, which drops any "ssid"/"pass" line.
        (logs + startup).forEach { assertNotNull(it, DiagnosticRedactor.redact(it)) }
        manager.close()
    }

    @Test fun androidReaderUsesTheWifiNetworkAndReleasesItsCallback() {
        val connectivity = context.getSystemService(ConnectivityManager::class.java)
        val shadow = shadowOf(connectivity)
        shadow.clearAllNetworks()
        val network = ShadowNetwork.newInstance(42)
        shadow.addNetwork(network, ShadowNetworkInfo.newInstance(
            NetworkInfo.DetailedState.CONNECTED, ConnectivityManager.TYPE_WIFI, 0, true, NetworkInfo.State.CONNECTED,
        ))
        shadow.setNetworkCapabilities(network, ShadowNetworkCapabilities.newInstance().also {
            shadowOf(it).addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
        })
        shadow.setLinkProperties(network, link("wlan0", "192.168.1.20", "fe80::1"))
        shadowOf(context.getSystemService(WifiManager::class.java)).setConnectionInfo(wifiInfo())

        val reader = AndroidStationWifiReader(context)
        assertEquals("wlan0", reader.wifiLinkProperties()?.interfaceName)
        assertEquals(5180, reader.wifiInfo()?.frequency)
        val handle = reader.watch {}
        assertEquals(1, shadow.networkCallbacks.size)
        handle.close()
        assertEquals(0, shadow.networkCallbacks.size)
    }

    @Test fun radioConcurrencySummarySurvivesTheReportRedactor() {
        val line = "wireless radio mode=WIFI_P2P " + com.shilapi.xcertplay.network.wifiConcurrencySummary(context)
        assertTrue(line, Regex("p2pSupported=\\w+ staApConcurrency=\\w+ staLocalOnlyConcurrency=\\w+ band5GHz=\\w+").containsMatchIn(line))
        assertEquals(line, DiagnosticRedactor.redact(line))
    }

    @Test fun autoStartSkipsTheTetheringCheckOnlyForTheExistingNetwork() {
        AirPlayPersistence.saveWirelessEnabled(context, true)
        RigPlayPreferences.savePhone(context, "AA:BB:CC:DD:EE:FF", "Tim's iPhone")
        val tetheringOff = { false }
        AirPlayPersistence.saveWirelessHotspotMode(context, WirelessHotspotMode.EXISTING_NETWORK)
        assertEquals("Wi-Fi network details missing", RigPhoneSession.autoStartBlocker(context, tetheringOff) { true })
        AirPlayPersistence.saveExistingNetworkSsid(context, "Home")
        AirPlayPersistence.saveExistingNetworkPassphrase(context, "home-password")
        assertEquals(null, RigPhoneSession.autoStartBlocker(context, tetheringOff) { true })

        // The tablet-hotspot mode keeps both of its checks, with its own saved details.
        AirPlayPersistence.saveWirelessHotspotMode(context, WirelessHotspotMode.MANUAL)
        assertEquals("hotspot details missing", RigPhoneSession.autoStartBlocker(context, tetheringOff) { true })
        AirPlayPersistence.saveManualHotspotSsid(context, "Tablet")
        AirPlayPersistence.saveManualHotspotPassphrase(context, "tablet-password")
        assertEquals("hotspot is off", RigPhoneSession.autoStartBlocker(context, tetheringOff) { true })
        assertEquals(null, RigPhoneSession.autoStartBlocker(context, { null }) { true })
    }

    @Test fun existingNetworkModeAndDetailsPersistApartFromTheTabletHotspot() {
        AirPlayPersistence.saveManualHotspotSsid(context, "Tablet")
        AirPlayPersistence.saveExistingNetworkSsid(context, "Home")
        AirPlayPersistence.saveWirelessHotspotMode(context, WirelessHotspotMode.EXISTING_NETWORK)
        assertEquals(WirelessHotspotMode.EXISTING_NETWORK, AirPlayPersistence.loadWirelessHotspotMode(context))
        assertEquals("Tablet", AirPlayPersistence.loadManualHotspotSsid(context))
        assertEquals("Home", AirPlayPersistence.loadExistingNetworkSsid(context))
        assertFalse(AirPlayPersistence.loadExistingNetworkPassphrase(context).isNotEmpty())
    }
}
