package com.shilapi.xcertplay.simhub

import android.content.Context
import android.net.wifi.WifiManager
import android.util.Log
import java.io.Closeable
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.SocketException
import java.net.SocketTimeoutException
import java.nio.charset.StandardCharsets

/** One plugin seen through its beacon (§4). [address] is the datagram's source address (§4.1). */
data class DiscoveredHost(
    val hostId: String,
    val name: String,
    val address: InetAddress,
    val controlPort: Int,
    val audioPort: Int,
    val version: String,
    val simhubVersion: String?,
    val protocol: Int,
    val minProtocol: Int,
    /** [SimHubDiscovery]'s clock (monotonic milliseconds) when the last beacon arrived. */
    val lastSeen: Long,
) {
    /** False: list as "incompatible version" and do not connect (§4.2). */
    val compatible: Boolean get() = SimHubProtocol.isCompatible(minProtocol, protocol)
}

/**
 * Listens for plugin beacons on UDP [port] (23710, §4) and keeps the list of hosts heard from in the
 * last [expiryMs].
 *
 * [start] binds synchronously, so a busy port surfaces to the caller as an exception. Beacons are
 * received and [Listener] is called on a dedicated thread named `SimHubDiscovery`. Android drops
 * broadcasts unless a Wi-Fi multicast lock is held (§4.2); pass [wifiMulticastLock] as [networkLock].
 */
class SimHubDiscovery(
    private val listener: Listener,
    private val port: Int = SimHubProtocol.DISCOVERY_PORT,
    private val expiryMs: Long = SimHubProtocol.DISCOVERY_EXPIRY_MS,
    private val networkLock: NetworkLock? = null,
    private val clock: () -> Long = { System.nanoTime() / 1_000_000L },
    private val log: (String) -> Unit = { Log.i(TAG, it) },
) : Closeable {
    interface Listener {
        /** The list gained, lost or changed a host. Sorted by name. */
        fun onHostsChanged(hosts: List<DiscoveredHost>)

        /** Every valid beacon, including repeats; [SimHubLink.onBeacon] uses it to reconnect at once. */
        fun onBeacon(host: DiscoveredHost) {}
    }

    /** Acquired while listening. */
    interface NetworkLock {
        fun acquire()
        fun release()
    }

    private val lifecycleLock = Any()
    private var socket: DatagramSocket? = null
    private var thread: Thread? = null

    @Volatile
    private var snapshot: List<DiscoveredHost> = emptyList()

    /** Hosts currently listed. Safe from any thread. */
    val hosts: List<DiscoveredHost> get() = snapshot

    /** Port actually bound (useful when [port] is 0 in tests), or -1 when stopped. */
    val localPort: Int get() = synchronized(lifecycleLock) { socket?.localPort ?: -1 }

    /** Binds the wildcard address with `SO_REUSEADDR` and starts listening. No-op when started. */
    fun start() {
        synchronized(lifecycleLock) {
            if (socket != null) return
            val bound = DatagramSocket(null)
            try {
                bound.reuseAddress = true
                bound.broadcast = true
                bound.soTimeout = maxOf(50L, minOf(250L, expiryMs / 4)).toInt()
                bound.bind(InetSocketAddress(port))
            } catch (error: Throwable) {
                bound.close()
                throw error
            }
            networkLock?.runCatching { acquire() }?.onFailure { log("discovery lock not acquired: $it") }
            socket = bound
            thread = Thread({ receiveLoop(bound) }, "SimHubDiscovery").apply {
                isDaemon = true
                start()
            }
            log("discovery listening on UDP ${bound.localPort}")
        }
    }

    /** Stops listening and clears the list. Joins the thread unless called from a callback. */
    fun stop() {
        val (closing, worker) = synchronized(lifecycleLock) {
            val pair = socket to thread
            socket = null
            thread = null
            pair
        }
        if (closing == null) return
        closing.close()
        networkLock?.runCatching { release() }
        if (worker != null && worker !== Thread.currentThread()) worker.join(1_000)
        snapshot = emptyList()
    }

    override fun close() = stop()

    private fun receiveLoop(socket: DatagramSocket) {
        val buffer = ByteArray(SimHubProtocol.MAX_BEACON_BYTES * 2)
        val packet = DatagramPacket(buffer, buffer.size)
        val hostsById = LinkedHashMap<String, DiscoveredHost>()
        var rejected = 0
        try {
            while (!socket.isClosed) {
                var changed = false
                try {
                    packet.setData(buffer, 0, buffer.size)
                    socket.receive(packet)
                    val host = decode(packet)
                    if (host == null) {
                        if (rejected++ < 5) log("ignored datagram from ${packet.address} length=${packet.length}")
                    } else {
                        val previous = hostsById.put(host.hostId, host)
                        changed = previous == null || previous.copy(lastSeen = 0) != host.copy(lastSeen = 0)
                        if (changed) log("beacon ${host.name} ${host.address.hostAddress}:${host.controlPort} v${host.version}")
                        dispatch { listener.onBeacon(host) }
                    }
                } catch (_: SocketTimeoutException) {
                    // Expiry check below.
                }
                if (expire(hostsById)) changed = true
                if (changed && !socket.isClosed) publish(hostsById)
            }
        } catch (error: SocketException) {
            if (!socket.isClosed) log("discovery socket failed: $error")
        }
    }

    private fun decode(packet: DatagramPacket): DiscoveredHost? {
        if (packet.length > SimHubProtocol.MAX_BEACON_BYTES) return null
        val text = String(packet.data, packet.offset, packet.length, StandardCharsets.UTF_8)
        val beacon = SimHubProtocol.parseOrNull(text) as? SimHubMessage.Beacon ?: return null
        return DiscoveredHost(
            hostId = beacon.hostId,
            name = beacon.name,
            address = packet.address,
            controlPort = beacon.controlPort,
            audioPort = beacon.audioPort,
            version = beacon.version,
            simhubVersion = beacon.simhubVersion,
            protocol = beacon.protocol,
            minProtocol = beacon.effectiveMinProtocol,
            lastSeen = clock(),
        )
    }

    private fun expire(hostsById: MutableMap<String, DiscoveredHost>): Boolean {
        val now = clock()
        val iterator = hostsById.values.iterator()
        var removed = false
        while (iterator.hasNext()) {
            val host = iterator.next()
            if (now - host.lastSeen >= expiryMs) {
                iterator.remove()
                removed = true
                log("host ${host.name} expired")
            }
        }
        return removed
    }

    private fun publish(hostsById: Map<String, DiscoveredHost>) {
        val hosts = hostsById.values.sortedBy { it.name.lowercase() }
        snapshot = hosts
        dispatch { listener.onHostsChanged(hosts) }
    }

    private inline fun dispatch(block: () -> Unit) {
        try {
            block()
        } catch (error: RuntimeException) {
            log("discovery listener failed: $error")
        }
    }

    companion object {
        const val TAG = "rigplay-simhub-discovery"

        /** [NetworkLock] backed by a Wi-Fi multicast lock (needs `CHANGE_WIFI_MULTICAST_STATE`). */
        fun wifiMulticastLock(context: Context): NetworkLock {
            val wifi = (context.applicationContext ?: context).getSystemService(Context.WIFI_SERVICE) as WifiManager
            val lock = wifi.createMulticastLock("rigplay-simhub-discovery").apply { setReferenceCounted(false) }
            return object : NetworkLock {
                override fun acquire() = lock.acquire()
                override fun release() {
                    if (lock.isHeld) lock.release()
                }
            }
        }
    }
}
