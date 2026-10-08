package com.shilapi.xcertplay.simhub

import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [29], manifest = Config.NONE)
class SimHubDiscoveryTest {
    private val changes = LinkedBlockingQueue<List<DiscoveredHost>>()
    private val beacons = LinkedBlockingQueue<DiscoveredHost>()
    private val acquired = AtomicInteger()
    private val released = AtomicInteger()
    private lateinit var discovery: SimHubDiscovery
    private lateinit var sender: DatagramSocket

    @Before fun setUp() {
        discovery = SimHubDiscovery(
            listener = object : SimHubDiscovery.Listener {
                override fun onHostsChanged(hosts: List<DiscoveredHost>) { changes += hosts }
                override fun onBeacon(host: DiscoveredHost) { beacons += host }
            },
            port = 0,
            expiryMs = EXPIRY_MS,
            networkLock = object : SimHubDiscovery.NetworkLock {
                override fun acquire() { acquired.incrementAndGet() }
                override fun release() { released.incrementAndGet() }
            },
            log = {},
        )
        discovery.start()
        sender = DatagramSocket()
    }

    @After fun tearDown() {
        discovery.stop()
        sender.close()
    }

    @Test fun beaconListsTheHostAtTheDatagramSourceAddress() {
        assertTrue(discovery.localPort > 0)
        assertEquals(1, acquired.get())
        send(fixtureBeacon())

        val hosts = changes.poll(2, TimeUnit.SECONDS)!!
        assertEquals(1, hosts.size)
        val host = hosts.single()
        assertEquals("3f6c2a4e-8d1b-4c7a-9e55-0b2d7f1a6c90", host.hostId)
        assertEquals("RIG-PC", host.name)
        assertEquals(InetAddress.getByName("127.0.0.1"), host.address)
        assertEquals(23711, host.controlPort)
        assertEquals(23712, host.audioPort)
        assertEquals("0.1.0", host.version)
        assertEquals("9.12.6", host.simhubVersion)
        assertEquals(1, host.protocol)
        assertEquals(1, host.minProtocol)
        assertTrue(host.compatible)
        assertEquals(hosts, discovery.hosts)
    }

    @Test fun repeatedBeaconsRefreshWithoutAChangeAndChangedFieldsNotify() {
        send(fixtureBeacon())
        changes.poll(2, TimeUnit.SECONDS)!!
        send(fixtureBeacon())
        assertTrue(beacons.poll(2, TimeUnit.SECONDS) != null)
        assertTrue(beacons.poll(2, TimeUnit.SECONDS) != null)
        assertNull("an identical beacon is not a change", changes.poll(150, TimeUnit.MILLISECONDS))

        send(fixtureBeacon().copy(controlPort = 24000))
        assertEquals(24000, changes.poll(2, TimeUnit.SECONDS)!!.single().controlPort)
    }

    @Test fun hostExpiresWhenBeaconsStop() {
        val sentAt = System.nanoTime()
        send(fixtureBeacon())
        assertEquals(1, changes.poll(2, TimeUnit.SECONDS)!!.size)
        val afterExpiry = changes.poll(2, TimeUnit.SECONDS)!!
        val elapsed = TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - sentAt)
        assertTrue(afterExpiry.isEmpty())
        assertTrue("expired after $elapsed ms", elapsed in EXPIRY_MS..EXPIRY_MS + 600)
        assertTrue(discovery.hosts.isEmpty())
    }

    @Test fun beaconsKeepTheHostListed() {
        send(fixtureBeacon())
        changes.poll(2, TimeUnit.SECONDS)!!
        repeat(6) {
            Thread.sleep(EXPIRY_MS / 3)
            send(fixtureBeacon())
        }
        assertNull(changes.poll(10, TimeUnit.MILLISECONDS))
        assertEquals(1, discovery.hosts.size)
    }

    @Test fun invalidDatagramsAreIgnored() {
        sendRaw("not json")
        sendRaw("""{"type":"beacon","name":"RIG-PC","hostId":"h","version":"0.1.0","audioPort":23712,"protocol":1}""")
        sendRaw(SimHubProtocol.encode(SimHubMessage.Heartbeat(1)))
        sendRaw("""{"type":"beacon","name":"${"x".repeat(1100)}","hostId":"h","version":"1","controlPort":1,"audioPort":2,"protocol":1}""")
        assertNull(changes.poll(300, TimeUnit.MILLISECONDS))
        assertTrue(discovery.hosts.isEmpty())
    }

    @Test fun incompatibleVersionsAreListedButMarked() {
        send(fixtureBeacon().copy(hostId = "aaaaaaaa-0000-4000-8000-000000000001", name = "NEW-PC", protocol = 3, minProtocol = 2))
        send(fixtureBeacon())
        var hosts = changes.poll(2, TimeUnit.SECONDS)!!
        if (hosts.size < 2) hosts = changes.poll(2, TimeUnit.SECONDS)!!
        assertEquals(listOf("NEW-PC", "RIG-PC"), hosts.map { it.name })
        assertFalse(hosts[0].compatible)
        assertTrue(hosts[1].compatible)
    }

    @Test fun stopReleasesTheLockAndClearsTheList() {
        send(fixtureBeacon())
        changes.poll(2, TimeUnit.SECONDS)!!
        discovery.stop()
        assertEquals(1, released.get())
        assertEquals(-1, discovery.localPort)
        assertTrue(discovery.hosts.isEmpty())
        discovery.start()
        assertEquals(2, acquired.get())
    }

    private fun fixtureBeacon() = SimHubMessage.Beacon(
        name = "RIG-PC",
        hostId = "3f6c2a4e-8d1b-4c7a-9e55-0b2d7f1a6c90",
        version = "0.1.0",
        simhubVersion = "9.12.6",
        controlPort = 23711,
        audioPort = 23712,
        protocol = 1,
        minProtocol = 1,
    )

    private fun send(beacon: SimHubMessage.Beacon) = sendRaw(SimHubProtocol.encode(beacon))

    private fun sendRaw(payload: String) {
        val bytes = payload.toByteArray(Charsets.UTF_8)
        sender.send(DatagramPacket(bytes, bytes.size, InetAddress.getByName("127.0.0.1"), discovery.localPort))
    }

    companion object {
        private const val EXPIRY_MS = 400L
    }
}
