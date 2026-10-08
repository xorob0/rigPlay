package com.shilapi.xcertplay

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class SimHubDashPageTest {
    @Test fun dashPageGetsNoControls() {
        assertEquals("http://192.168.1.20:8888/Dash#Pit%20Board|nocontrols",
            SimHubDashPage.withoutChrome("http://192.168.1.20:8888/Dash#Pit%20Board"))
        assertEquals("http://[fe80::1]:8888/dash#A|nocontrols", SimHubDashPage.withoutChrome("http://[fe80::1]:8888/dash#A"))
    }

    @Test fun existingFlagsAreKept() {
        assertEquals("http://h:8888/Dash#A|nostartup|nocontrols", SimHubDashPage.withoutChrome("http://h:8888/Dash#A|nostartup"))
        assertEquals("http://h:8888/Dash#A|nocontrols", SimHubDashPage.withoutChrome("http://h:8888/Dash#A|nocontrols"))
        assertEquals("http://h:8888/Dash#A|nocontrols,1", SimHubDashPage.withoutChrome("http://h:8888/Dash#A|nocontrols,1"))
    }

    @Test fun aNameThatLooksLikeTheFlagIsNotAFlag() =
        assertEquals("http://h:8888/Dash#nocontrols|nocontrols", SimHubDashPage.withoutChrome("http://h:8888/Dash#nocontrols"))

    @Test fun otherPagesAreLeftAlone() {
        assertEquals("http://h:8888/dashboard/A", SimHubDashPage.withoutChrome("http://h:8888/dashboard/A"))
        assertEquals("http://h:8888/Dash", SimHubDashPage.withoutChrome("http://h:8888/Dash"))
        assertEquals("http://h:8888/Dash#", SimHubDashPage.withoutChrome("http://h:8888/Dash#"))
        assertEquals("http://h:8888/Dashboards#A", SimHubDashPage.withoutChrome("http://h:8888/Dashboards#A"))
        assertEquals("file:///Dash#A", SimHubDashPage.withoutChrome("file:///Dash#A"))
        assertEquals("not a url#A", SimHubDashPage.withoutChrome("not a url#A"))
    }

    @Test fun scriptOnlyForTheDashPage() {
        assertEquals(SimHubDashPage.HIDE_CHROME_SCRIPT, SimHubDashPage.scriptAfterLoad("http://h:8888/Dash#A|nocontrols"))
        assertNull(SimHubDashPage.scriptAfterLoad("about:blank"))
        assertNull(SimHubDashPage.scriptAfterLoad("http://h:8888/other"))
        assertNull(SimHubDashPage.scriptAfterLoad(null))
    }

    @Test fun scriptIsAnExpressionThatHidesTheToolbar() {
        val script = SimHubDashPage.HIDE_CHROME_SCRIPT
        assertTrue(script.startsWith("(function(){") && script.endsWith("})()"))
        assertTrue("#controls2" in script && ".mobilehelp" in script)
        assertFalse("\"" in script)
    }

    @Test fun fragmentOnlyChangeIsTheSameDocument() {
        assertTrue(SimHubDashPage.sameDocument("http://h:8888/Dash#A|nocontrols", "http://h:8888/Dash#B|nocontrols"))
        assertFalse(SimHubDashPage.sameDocument("http://h:8888/Dash#A", "http://h:8888/Dash#A"))
        assertFalse(SimHubDashPage.sameDocument("http://h:8888/Dash#A", "http://h2:8888/Dash#A"))
        assertFalse(SimHubDashPage.sameDocument(null, "http://h:8888/Dash#A"))
        assertFalse(SimHubDashPage.sameDocument("about:blank", "http://h:8888/Dash#A"))
    }

    @Test fun reloadScriptQuotesTheUrl() {
        assertEquals("location.replace('http://h:8888/Dash#B|nocontrols');location.reload();",
            SimHubDashPage.reloadScript("http://h:8888/Dash#B|nocontrols"))
        assertEquals("'a\\'b\\\\c\\u000a\\u003c'", SimHubDashPage.jsString("a'b\\c\n<"))
    }

    @Test fun resolvedDashboardHasNoControlsOnBothHosts() {
        val state = com.shilapi.xcertplay.simhub.SimHubState(
            phase = com.shilapi.xcertplay.simhub.SimHubState.Phase.PAIRED,
            host = "127.0.0.1",
            dashboardUrl = "http://172.30.0.2:8888/Dash#SimHub%20-%20FordGT",
        )
        assertEquals(
            DashboardContent.Load(
                "http://172.30.0.2:8888/Dash#SimHub%20-%20FordGT|nocontrols",
                "http://127.0.0.1:8888/Dash#SimHub%20-%20FordGT|nocontrols",
            ),
            DashboardContent.resolve(state, paired = true),
        )
    }
}
