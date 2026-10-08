package com.shilapi.xcertplay.network

import android.content.Context
import android.net.wifi.WifiManager
import android.os.Build
import com.shilapi.xcertplay.orchestration.WirelessHotspotMode
import java.io.Closeable
import java.net.Inet4Address
import java.net.Inet6Address
import java.net.InetAddress
import java.net.NetworkInterface
import java.util.Collections
import java.util.concurrent.atomic.AtomicBoolean

/** Observes startup without changing connection deadlines, address selection or retry behavior. */
internal class WirelessStartupDiagnostics(
    private val sample: () -> String,
    private val log: (String) -> Unit,
    private val intervalMillis: Long = 10_000,
    /** Fixed per-session facts (mode, interface, channel) repeated on every line; see [wirelessStartupContext]. */
    private val context: String = "",
    private val nowNs: () -> Long = System::nanoTime,
) : Closeable {
    private val closed = AtomicBoolean(false)
    private val startedNanos = nowNs()
    private var authenticated = false
    private var wifiConfigs = 0
    private var startRequests = 0
    private var tcpAccepted = 0
    private var sessionActive = false
    private var firstStartRequestNs: Long? = null
    private var firstTcpAfterStartMs: Long? = null
    @Volatile private var lastSnapshot = ""
    private val worker = Thread(::observe, "rigplay-wireless-diagnostics").apply { isDaemon = true }

    init { require(intervalMillis > 0) }

    @Synchronized fun start() {
        if (!closed.get() && worker.state == Thread.State.NEW) worker.start()
    }

    @Synchronized fun controlProgress(message: String) {
        when (message) {
            "iap2 authentication accepted" -> authenticated = true
            "iap2 tx=0x5703 accessory-wifi-configuration",
            "iap2 tx=0x5703 post-transport accessory-wifi-configuration" -> wifiConfigs++
            "iap2 tx=0x4301 carplay-start-session" -> {
                startRequests++
                if (firstStartRequestNs == null) firstStartRequestNs = nowNs()
            }
        }
    }

    @Synchronized fun connectionAccepted() {
        tcpAccepted++
        if (firstTcpAfterStartMs == null) {
            firstStartRequestNs?.let { firstTcpAfterStartMs = elapsedMillis(it) }
        }
    }
    @Synchronized fun sessionActive() { sessionActive = true }

    @Synchronized fun summary(): String {
        val waitingFor = when {
            sessionActive -> "none"
            tcpAccepted > 0 -> "AirPlay_protocol"
            startRequests > 0 -> "WiFi_discovery_or_AirPlay_TCP"
            authenticated -> "WiFi_configuration_or_start_request"
            else -> "Bluetooth_iAP2_authentication"
        }
        return "wireless startup elapsedMs=${elapsedMillis(startedNanos)} " +
            "authenticated=$authenticated wifiConfigs=$wifiConfigs startRequests=$startRequests " +
            "tcpAccepted=$tcpAccepted sessionActive=$sessionActive waitingFor=$waitingFor " +
            "startRequestAgeMs=${firstStartRequestNs?.let(::elapsedMillis) ?: "none"} " +
            "firstTcpAfterStartMs=${firstTcpAfterStartMs ?: "none"}"
    }

    private fun elapsedMillis(since: Long): Long = (nowNs() - since).coerceAtLeast(0) / 1_000_000

    private fun observe() {
        try {
            while (!closed.get()) {
                val snapshot = try { sample() } catch (error: Exception) {
                    "sampling=unavailable failureClass=${error.javaClass.simpleName}"
                }
                if (closed.get()) return
                lastSnapshot = snapshot
                emit(listOf(summary(), context).filter { it.isNotEmpty() }.joinToString(" "))
                emitSnapshot(snapshot)
                Thread.sleep(intervalMillis)
            }
        } catch (_: InterruptedException) {
            // Teardown interrupts sleep or an in-flight Android callback wait.
        }
    }

    @Synchronized override fun close() {
        if (!closed.compareAndSet(false, true)) return
        worker.interrupt()
        emit(listOf(summary(), context, "observation=ended").filter { it.isNotEmpty() }.joinToString(" "))
        emitSnapshot(lastSnapshot, cached = true)
    }

    private fun emitSnapshot(snapshot: String, cached: Boolean = false) {
        // The report redactor caps each log line at 700 characters. Never append the kernel
        // counters to the already detailed startup/interface/P2P/Bonjour state line.
        snapshot.lineSequence().filter { it.isNotBlank() }.take(4).forEach { line ->
            emit("wireless snapshot${if (cached) " cached=true" else ""} $line")
        }
    }

    private fun emit(message: String) {
        try { log(message) } catch (_: RuntimeException) {
            // An observer cannot fail startup or teardown.
        }
    }
}

/**
 * The wireless mode and the network it handed to the iPhone, for exported reports (#33). Contains no
 * SSID, password or hardware address, and avoids the words the report redactor drops lines for
 * ("ssid", "pass"); `networkNameReadable` is `not_applicable` when rigPlay owns the network.
 */
fun wirelessStartupContext(
    mode: WirelessHotspotMode,
    info: WirelessHotspotInfo,
): String = "mode=${mode.name} iface=${info.interfaceName ?: "unknown"} channel=${info.channel} " +
    "frequency=${info.frequencyMHz?.let { "${it}MHz" } ?: "unknown"} " +
    "networkNameReadable=${info.ssidReadable?.toString() ?: "not_applicable"}"

/**
 * What the Wi-Fi chip reports it can run at once, logged at each wireless start for the tablet spike
 * (#33). `unknown` where the Android version lacks the API or the firmware throws.
 */
fun wifiConcurrencySummary(context: Context): String {
    val wifi = context.applicationContext.getSystemService(WifiManager::class.java)
        ?: return "wifiRadio=unavailable"
    fun read(value: () -> Boolean): String = runCatching(value).map(Boolean::toString).getOrDefault("unknown")
    val staAp = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
        read { wifi.isStaApConcurrencySupported }
    } else {
        "unknown"
    }
    val staLocalOnly = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
        read { wifi.isStaConcurrencyForLocalOnlyConnectionsSupported }
    } else {
        "unknown"
    }
    return "p2pSupported=${read { wifi.isP2pSupported }} staApConcurrency=$staAp " +
        "staLocalOnlyConcurrency=$staLocalOnly band5GHz=${read { wifi.is5GHzBandSupported }}"
}

/** Counts are useful in exported reports; literals, hardware identifiers and names are not. */
internal object WirelessInterfaceDiagnostics {
    fun snapshot(interfaceName: String?): String {
        if (interfaceName == null) return "interfaceState=unknown"
        return try {
            val network = NetworkInterface.getByName(interfaceName)
                ?: return "interfaceState=missing"
            "interfaceState=${if (network.isUp) "up" else "down"} multicast=${network.supportsMulticast()} " +
                addressSummary(Collections.list(network.inetAddresses))
        } catch (error: Exception) {
            "interfaceState=unavailable failureClass=${error.javaClass.simpleName}"
        }
    }

    fun addressSummary(addresses: List<InetAddress>): String =
        "ipv4Usable=${addresses.count { it is Inet4Address && !it.isLoopbackAddress && !it.isAnyLocalAddress && !it.isLinkLocalAddress && !it.isMulticastAddress }} " +
            "ipv6LinkLocal=${addresses.count { it is Inet6Address && it.isLinkLocalAddress }} " +
            "ipv6Scoped=${addresses.count { it is Inet6Address && it.isLinkLocalAddress && it.scopeId > 0 }}"
}
