package com.shilapi.xcertplay.orchestration

import com.shilapi.xcertplay.transport.Iap2IdentificationConfig
import com.shilapi.xcertplay.transport.UsbDeviceId
import com.shilapi.xcertplay.network.WifiP2pChannels
import java.net.Inet6Address
import java.net.InetAddress

enum class CarPlayTransport {
    WIRED,
    WIRELESS,
}

enum class WirelessHotspotMode {
    WIFI_P2P,
    LOCAL_ONLY_HOTSPOT,
    MANUAL,
    /**
     * Experimental (#33): the tablet stays a client of the home Wi-Fi and hands that network to the
     * iPhone instead of creating one. Untested with iOS.
     */
    EXISTING_NETWORK,
}

enum class ManualHotspotBand {
    AUTO,
    GHZ_2_4,
    GHZ_5,
}

enum class ManualHotspotSecurity {
    OPEN,
    WPA2,
    WPA3_TRANSITION,
    WPA3,
}

/**
 * Deployment-owned constants for one head unit. There are deliberately no built-in Apple
 * product IDs: the physical devices attached to the target must be identified first.
 */
class CarPlayRuntimeConfig(
    val iphoneDevices: List<UsbDeviceId> = emptyList(),
    val hostMac: ByteArray = DEFAULT_HOST_MAC,
    val linkLocal: String = "fe80::2",
    val identification: Iap2IdentificationConfig,
    val availableCurrentMilliAmps: Int = 2400,
    val label: String = "xcertplay",
    val hostName: String = "xcertplay",
    val transport: CarPlayTransport = CarPlayTransport.WIRED,
    val wirelessHotspotMode: WirelessHotspotMode = WirelessHotspotMode.WIFI_P2P,
    val manualHotspotSsid: String? = null,
    val manualHotspotPassphrase: String? = null,
    val manualHotspotBand: ManualHotspotBand = ManualHotspotBand.AUTO,
    val manualHotspotChannel: Int = 0,
    val manualHotspotSecurity: ManualHotspotSecurity = ManualHotspotSecurity.WPA2,
    /** Existing-network mode: the name the user entered; the live name read from Android wins when readable. */
    val existingNetworkSsid: String? = null,
    val existingNetworkPassphrase: String? = null,
    val wirelessBluetoothDeviceAddress: String? = null,
    val locationReportingEnabled: Boolean = false,
    val wifiP2pPreferredChannel: Int = WifiP2pChannels.AUTO,
) {
    init {
        require(iphoneDevices.all { it.vendorId == APPLE_VENDOR_ID }) {
            "iPhone USB identities must use Apple vendor ID 0x${APPLE_VENDOR_ID.toString(16)}"
        }
        require(hostMac.size == 6) { "hostMac must be 6 bytes" }
        require(isLinkLocalIpv6(linkLocal)) { "linkLocal must be a link-local IPv6 literal" }
        require(availableCurrentMilliAmps in 0..0xffff) {
            "availableCurrentMilliAmps must be in 0..65535"
        }
        require(label.isNotBlank()) { "label must not be blank" }
        require(hostName.isNotBlank()) { "hostName must not be blank" }
        // Only a wireless session starts the hotspot; a USB session must not fail on unused settings.
        if (transport == CarPlayTransport.WIRELESS && wirelessHotspotMode == WirelessHotspotMode.WIFI_P2P) {
            require(WifiP2pChannels.isValid(wifiP2pPreferredChannel)) {
                "Unsupported Wi-Fi Direct channel: $wifiP2pPreferredChannel"
            }
        }
        if (transport == CarPlayTransport.WIRELESS && wirelessHotspotMode == WirelessHotspotMode.MANUAL) {
            val ssid = manualHotspotSsid
            require(!ssid.isNullOrBlank()) {
                "manualHotspotSsid is required in manual hotspot mode"
            }
            require('\u0000' !in ssid) {
                "manualHotspotSsid must not contain U+0000"
            }
            val passphrase = manualHotspotPassphrase.orEmpty()
            require('\u0000' !in passphrase) {
                "manualHotspotPassphrase must not contain U+0000"
            }
            require(passphrase.isEmpty() || passphrase.length in 8..63) {
                "manualHotspotPassphrase must be empty or between 8 and 63 characters"
            }
            require(manualHotspotChannel in 0..196) {
                "manualHotspotChannel must be 0 or in 1..196"
            }
            require(
                manualHotspotSecurity == ManualHotspotSecurity.OPEN ||
                    passphrase.length in 8..63,
            ) {
                "A passphrase between 8 and 63 characters is required for secured manual hotspots"
            }
            require(
                manualHotspotChannel == 0 ||
                    isManualHotspotChannelCompatible(manualHotspotBand, manualHotspotChannel),
            ) {
                "manualHotspotChannel is not valid for the selected manual hotspot band"
            }
            require(
                manualHotspotSecurity == ManualHotspotSecurity.OPEN || passphrase.isNotEmpty(),
            ) {
                "manualHotspotPassphrase is required for secured manual hotspots"
            }
        }
        if (transport == CarPlayTransport.WIRELESS && wirelessHotspotMode == WirelessHotspotMode.EXISTING_NETWORK) {
            val ssid = existingNetworkSsid.orEmpty()
            val passphrase = existingNetworkPassphrase.orEmpty()
            require('\u0000' !in ssid && '\u0000' !in passphrase) {
                "existing network name and password must not contain U+0000"
            }
            require(ssid.encodeToByteArray().size <= 32) { "existingNetworkSsid must be at most 32 bytes" }
            require(passphrase.isEmpty() || passphrase.length in 8..63) {
                "existingNetworkPassphrase must be empty or between 8 and 63 characters"
            }
        }
    }

    companion object {
        const val APPLE_VENDOR_ID = 0x05ac
        val DEFAULT_HOST_MAC = byteArrayOf(0x02, 0x00, 0x00, 0x00, 0x00, 0x02)
        private fun isLinkLocalIpv6(value: String): Boolean {
            if (value.contains('%') || '\u0000' in value || !value.contains(':')) return false
            return try {
                val address = InetAddress.getByName(value)
                address is Inet6Address && address.isLinkLocalAddress
            } catch (_: Exception) {
                false
            }
        }
    }
}

fun isManualHotspotChannelCompatible(band: ManualHotspotBand, channel: Int): Boolean = when (band) {
    ManualHotspotBand.AUTO -> channel in 1..196
    ManualHotspotBand.GHZ_2_4 -> channel in 1..14
    ManualHotspotBand.GHZ_5 -> channel in 32..177
}

/** Which tablet-side prerequisites each wireless mode has (#33). */
object WirelessModeRequirements {
    /**
     * Only the tablet-hotspot mode attaches to Android tethering, so only it is blocked when
     * tethering is off ([com.shilapi.xcertplay.network.CarHotspotStatus]). The existing-network mode
     * never needs it: the network belongs to the home router.
     */
    fun requiresTethering(mode: WirelessHotspotMode): Boolean = mode == WirelessHotspotMode.MANUAL

    /**
     * Whether the iPhone reaches rigPlay on the station (default-route) Wi-Fi interface. The modes
     * that create a network must avoid that interface, which leads to the home router; the
     * existing-network mode must use exactly it.
     */
    fun servesOnStationInterface(mode: WirelessHotspotMode): Boolean =
        mode == WirelessHotspotMode.EXISTING_NETWORK

    /** Modes whose network name and password the user types into Settings. */
    fun needsSavedCredentials(mode: WirelessHotspotMode): Boolean =
        mode == WirelessHotspotMode.MANUAL || mode == WirelessHotspotMode.EXISTING_NETWORK

    /** Wi-Fi Direct needs Android 10; older tablets fall back to LocalOnlyHotspot. Other modes are kept. */
    fun effectiveMode(configured: WirelessHotspotMode, sdkInt: Int): WirelessHotspotMode =
        if (sdkInt < 29 && configured == WirelessHotspotMode.WIFI_P2P) {
            WirelessHotspotMode.LOCAL_ONLY_HOTSPOT
        } else {
            configured
        }
}
