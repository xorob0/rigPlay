package com.shilapi.xcertplay

import com.shilapi.xcertplay.simhub.DashboardServer
import com.shilapi.xcertplay.simhub.SimHubState
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

class SimHubIconCacheTest {
    @get:Rule val folder = TemporaryFolder()

    private val requested = mutableListOf<String>()
    private val answers = HashMap<String, ByteArray?>()
    private val pendingIo = ArrayDeque<Runnable>()
    private var now = 0L
    private val icons = mutableListOf<String>()
    private val dir: File by lazy { File(folder.root, SimHubIconCache.DIRECTORY) }
    private val cache by lazy {
        SimHubIconCache(
            dir = dir,
            http = { url -> requested += url; answers[url] },
            io = { pendingIo.addLast(it) },
            main = { it.run() },
            clock = { now },
        ) { icons += it }
    }

    private val up = SimHubState(
        phase = SimHubState.Phase.PAIRED,
        host = "192.168.1.20",
        controlPort = 23711,
        hostId = "pc-1",
        dashboardServer = DashboardServer(reachable = true, port = 8888),
    )
    private val android192 = "http://192.168.1.20:8888/favicons/android-icon-192x192.png"
    private val apple180 = "http://192.168.1.20:8888/favicons/apple-icon-180x180.png"

    private fun runIo() { while (pendingIo.isNotEmpty()) pendingIo.removeFirst().run() }

    @Test fun requestNeedsTheLinkUpAndTheServerReachable() {
        assertNull(SimHubIconCache.requestFor(up.copy(phase = SimHubState.Phase.WAITING)))
        assertNull(SimHubIconCache.requestFor(up.copy(dashboardServer = DashboardServer(reachable = false, port = 8888))))
        assertNull(SimHubIconCache.requestFor(up.copy(hostId = null)))
        assertNull(SimHubIconCache.requestFor(up.copy(host = null)))
        assertEquals(SimHubIconCache.Request("pc-1", listOf(android192, apple180)), SimHubIconCache.requestFor(up))
    }

    @Test fun requestUsesTheConnectedHostAndTheWebPort() {
        // The dashboard URL names the PC's NAT address; the link's host is the one that works.
        val natted = up.copy(host = "127.0.0.1", dashboardServer = DashboardServer(true, 9999), dashboardUrl = "http://172.30.0.2:9999/Dash#A")
        assertEquals("http://127.0.0.1:9999/favicons/android-icon-192x192.png", SimHubIconCache.requestFor(natted)!!.urls[0])
        val portFromUrl = up.copy(dashboardServer = null, dashboardUrl = "http://172.30.0.2:8080/Dash#A|nocontrols")
        assertEquals("http://192.168.1.20:8080/favicons/android-icon-192x192.png", SimHubIconCache.requestFor(portFromUrl)!!.urls[0])
        assertEquals("http://192.168.1.20:8888/favicons/android-icon-192x192.png",
            SimHubIconCache.requestFor(up.copy(dashboardServer = null))!!.urls[0])
        assertEquals("http://[fe80::1]:8888/favicons/android-icon-192x192.png",
            SimHubIconCache.requestFor(up.copy(host = "fe80::1"))!!.urls[0])
    }

    @Test fun fetchesOnceAndCaches() {
        answers[android192] = png(192, 192)
        cache.onState(up)
        cache.onState(up) // in flight: no second request
        runIo()
        assertEquals(listOf(android192), requested)
        assertEquals(listOf("pc-1"), icons)
        assertArrayEquals(png(192, 192), SimHubIconCache.file(dir, "pc-1")!!.readBytes())
        cache.onState(up)
        runIo()
        assertEquals(1, requested.size)
        assertFalse(File(dir, "pc-1.png.part").exists())
    }

    @Test fun fallsBackToTheAppleIcon() {
        answers[android192] = null
        answers[apple180] = png(180, 180)
        cache.onState(up)
        runIo()
        assertEquals(listOf(android192, apple180), requested)
        assertEquals(180, SimHubIconCache.file(dir, "pc-1")!!.readBytes()[19].toInt() and 0xFF)
    }

    @Test fun anHtmlErrorPageIsNotAnIcon() {
        answers[android192] = "<html>404</html>".toByteArray()
        answers[apple180] = png(180, 120)
        cache.onState(up)
        runIo()
        assertNull(SimHubIconCache.file(dir, "pc-1"))
        assertTrue(icons.isEmpty())
    }

    @Test fun failureIsRetriedOnlyAfterTenMinutes() {
        cache.onState(up)
        runIo()
        assertEquals(2, requested.size)
        now += SimHubIconCache.RETRY_AFTER_MS - 1
        cache.onState(up)
        runIo()
        assertEquals(2, requested.size)
        now += 1
        answers[android192] = png(192, 192)
        cache.onState(up)
        runIo()
        assertEquals(3, requested.size)
        assertEquals(listOf("pc-1"), icons)
    }

    @Test fun anotherHostGetsItsOwnIcon() {
        answers[android192] = png(192, 192)
        cache.onState(up)
        runIo()
        cache.onState(up.copy(hostId = "pc-2"))
        runIo()
        assertEquals(listOf("pc-1", "pc-2"), icons)
        assertTrue(SimHubIconCache.file(dir, "pc-2")!!.isFile)
    }

    @Test fun noFetchWhileTheLinkIsDown() {
        cache.onState(up.copy(phase = SimHubState.Phase.CONNECTING))
        runIo()
        assertTrue(requested.isEmpty())
    }

    @Test fun throwingSourceCountsAsAFailure() {
        val throwing = SimHubIconCache(dir, { error("boom") }, { it.run() }, { it.run() }, { now }) { icons += it }
        throwing.onState(up)
        assertTrue(icons.isEmpty())
        assertNull(SimHubIconCache.file(dir, "pc-1"))
    }

    @Test fun iconValidation() {
        assertTrue(SimHubIconCache.isUsableIcon(png(192, 192)))
        assertFalse(SimHubIconCache.isUsableIcon(png(192, 180)))
        assertFalse(SimHubIconCache.isUsableIcon(png(8, 8)))
        assertFalse(SimHubIconCache.isUsableIcon(png(2048, 2048)))
        assertFalse(SimHubIconCache.isUsableIcon(ByteArray(10)))
        assertFalse(SimHubIconCache.isUsableIcon(png(192, 192).also { it[0] = 0 }))
    }

    @Test fun fileNamesAreSafe() {
        assertEquals("pc-1.png", SimHubIconCache.fileName("pc-1"))
        assertEquals("3f2c9a8e-1b2c.png", SimHubIconCache.fileName("3f2c9a8e-1b2c"))
        val odd = SimHubIconCache.fileName("../x/y")
        assertFalse("/" in odd)
        assertFalse(odd.startsWith("."))
        assertNotEquals(SimHubIconCache.fileName("a/b"), SimHubIconCache.fileName("a:b"))
        assertNull(SimHubIconCache.file(dir, null))
        assertNull(SimHubIconCache.file(dir, "missing"))
    }

    /** A PNG header (signature + IHDR) with the given size; enough for the checks. */
    private fun png(width: Int, height: Int): ByteArray {
        val bytes = ByteArray(64)
        byteArrayOf(0x89.toByte(), 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A).copyInto(bytes)
        bytes[11] = 13
        "IHDR".toByteArray().copyInto(bytes, 12)
        for (i in 0..3) {
            bytes[16 + i] = (width ushr (24 - 8 * i)).toByte()
            bytes[20 + i] = (height ushr (24 - 8 * i)).toByte()
        }
        return bytes
    }
}
