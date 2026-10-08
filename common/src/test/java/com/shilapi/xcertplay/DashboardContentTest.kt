package com.shilapi.xcertplay

import com.shilapi.xcertplay.simhub.DashboardServer
import com.shilapi.xcertplay.simhub.SimHubState
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class DashboardContentTest {
    private val url = "http://172.30.0.2:8888/dashboard/Pit%20Board"
    private val up = SimHubState(phase = SimHubState.Phase.PAIRED, host = "192.168.1.20", controlPort = 23711, dashboardUrl = url)

    @Test fun notPairedAndDisconnected() {
        assertEquals(DashboardContent.NotPaired, DashboardContent.resolve(up, paired = false))
        assertEquals(DashboardContent.Disconnected, DashboardContent.resolve(up.copy(phase = SimHubState.Phase.WAITING), paired = true))
    }

    @Test fun noDashboardSelected() =
        assertEquals(DashboardContent.NoDashboard, DashboardContent.resolve(up.copy(dashboardUrl = null), paired = true))

    @Test fun serverOff() = assertEquals(
        DashboardContent.ServerOff,
        DashboardContent.resolve(up.copy(dashboardServer = DashboardServer(reachable = false, port = 8888)), paired = true),
    )

    @Test fun unknownServerStateIsAssumedReachable() = assertEquals(
        DashboardContent.Load(url, "http://192.168.1.20:8888/dashboard/Pit%20Board"),
        DashboardContent.resolve(up, paired = true),
    )

    @Test fun noFallbackWhenTheHostsMatch() {
        val same = up.copy(dashboardUrl = "http://192.168.1.20:8888/dashboard/Rig")
        assertEquals(DashboardContent.Load("http://192.168.1.20:8888/dashboard/Rig", null), DashboardContent.resolve(same, paired = true))
    }

    @Test fun rewriteKeepsPortPathAndQuery() {
        assertEquals("http://10.0.0.5:8888/dashboard/Pit%20Board?x=1#top",
            DashboardUrls.withHost("http://172.30.0.2:8888/dashboard/Pit%20Board?x=1#top", "10.0.0.5"))
        assertEquals("http://10.0.0.5/dashboard/A", DashboardUrls.withHost("http://172.30.0.2/dashboard/A", "10.0.0.5"))
    }

    @Test fun rewriteHandlesIpv6AndHostNames() {
        assertEquals("http://[fe80::1]:8888/d", DashboardUrls.withHost("http://172.30.0.2:8888/d", "fe80::1"))
        assertEquals("http://rig-pc.local:8888/d", DashboardUrls.withHost("http://[fe80::1]:8888/d", "rig-pc.local"))
        assertNull(DashboardUrls.withHost("http://[fe80::1]:8888/d", "[FE80::1]"))
    }

    @Test fun rewriteRejectsGarbage() {
        assertNull(DashboardUrls.withHost("not a url", "10.0.0.5"))
        assertNull(DashboardUrls.withHost("http://172.30.0.2:8888/d", "172.30.0.2"))
    }

    @Test fun endpoint() {
        assertEquals("172.30.0.2" to 8888, DashboardUrls.endpoint("http://172.30.0.2:8888/d"))
        assertEquals("rig" to 80, DashboardUrls.endpoint("http://rig/d"))
        assertEquals("fe80::1" to 8888, DashboardUrls.endpoint("http://[fe80::1]:8888/d"))
    }

    // --- idle dashboard fallback chain (#39) ---

    private val idleUrl = "http://172.30.0.2:8888/Dash#Rig%20Clock"

    // SimHub's toolbar is off on the idle dashboard too (#50).
    @Test fun idleUsesTheIdleDashboardFirst() = assertEquals(
        DashboardContent.Load("$idleUrl|nocontrols", "http://192.168.1.20:8888/Dash#Rig%20Clock|nocontrols"),
        DashboardContent.resolveIdle(up.copy(idleDashboardUrl = idleUrl), paired = true),
    )

    @Test fun idleFallsBackToTheMainDashboard() {
        assertEquals(DashboardContent.resolve(up, paired = true), DashboardContent.resolveIdle(up, paired = true))
        assertTrue(DashboardContent.idleDashboardAvailable(up, paired = true))
    }

    @Test fun idleWithoutAnyDashboardMeansTheRigPlayScreen() {
        val none = up.copy(dashboardUrl = null, idleDashboardUrl = null)
        assertEquals(DashboardContent.NoDashboard, DashboardContent.resolveIdle(none, paired = true))
        assertFalse(DashboardContent.idleDashboardAvailable(none, paired = true))
    }

    @Test fun idleNeedsTheLinkAndTheWebServer() {
        val withIdle = up.copy(idleDashboardUrl = idleUrl)
        val off = withIdle.copy(dashboardServer = DashboardServer(reachable = false, port = 8888))
        assertEquals(DashboardContent.ServerOff, DashboardContent.resolveIdle(off, paired = true))
        assertFalse(DashboardContent.idleDashboardAvailable(off, paired = true))
        assertFalse(DashboardContent.idleDashboardAvailable(withIdle.copy(phase = SimHubState.Phase.WAITING), paired = true))
        assertFalse(DashboardContent.idleDashboardAvailable(withIdle, paired = false))
    }
}
