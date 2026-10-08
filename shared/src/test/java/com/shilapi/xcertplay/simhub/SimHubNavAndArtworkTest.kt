package com.shilapi.xcertplay.simhub

import com.shilapi.xcertplay.glance.CarPlayGlance
import com.shilapi.xcertplay.guidance.RouteManeuverNames
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class SimHubNavAndArtworkTest {
    @Test fun maneuverNamesFollowApplesTypes() {
        assertEquals(54, RouteManeuverNames.ALL.size)
        assertEquals(RouteManeuverNames.ALL.size, RouteManeuverNames.ALL.toSet().size)
        assertEquals("noTurn", RouteManeuverNames.nameOf(0))
        assertEquals("leftTurn", RouteManeuverNames.nameOf(1))
        assertEquals("rightTurn", RouteManeuverNames.nameOf(2))
        assertEquals("arriveAtDestination", RouteManeuverNames.nameOf(12))
        assertEquals("uTurnAtRoundabout", RouteManeuverNames.nameOf(19))
        assertEquals("arriveEndOfDirections", RouteManeuverNames.nameOf(27))
        assertEquals("roundaboutExit1", RouteManeuverNames.nameOf(28))
        assertEquals("roundaboutExit19", RouteManeuverNames.nameOf(46))
        assertEquals("sharpLeftTurn", RouteManeuverNames.nameOf(47))
        assertEquals("slightRightTurn", RouteManeuverNames.nameOf(50))
        assertEquals("changeHighwayRight", RouteManeuverNames.nameOf(53))
        assertEquals("unknown types have no arrow", "noTurn", RouteManeuverNames.nameOf(54))
        assertEquals("noTurn", RouteManeuverNames.nameOf(-1))
    }

    @Test fun glanceBecomesStatusNav() {
        val glance = CarPlayGlance.Snapshot(
            connected = true,
            maneuverType = 50,
            distanceMeters = 350,
            road = "B258",
            arrivalEpochSeconds = 1_791_044_100L,
        )
        assertEquals(NavStatus("slightRightTurn", 350, "B258", 1_791_044_100L), SimHubNav.of(glance))
        assertEquals(
            "blank road and unknown arrival are omitted",
            NavStatus("leftTurn", 0, null, null),
            SimHubNav.of(CarPlayGlance.Snapshot(connected = true, maneuverType = 1)),
        )
        assertNull("no route", SimHubNav.of(glance.copy(maneuverType = null)))
        assertNull("no phone", SimHubNav.of(glance.copy(connected = false)))
    }

    @Test fun artworkGoesOutOnChangeAtMostEveryTwoSeconds() {
        val throttle = SimHubArtworkThrottle(minIntervalMillis = 2_000)
        assertNull("nothing offered", throttle.due(0))
        assertNull(throttle.waitMillis(0))

        throttle.offer("A")
        assertEquals("first artwork at once", "A", throttle.due(100))
        throttle.offer("A")
        assertNull("unchanged", throttle.due(5_000))

        throttle.offer("B")
        throttle.offer("C")
        assertEquals("C waits for the interval", 0L, throttle.waitMillis(5_000))
        assertEquals("C", throttle.due(5_000))
        throttle.offer("D")
        assertNull("too soon", throttle.due(6_000))
        assertEquals(1_000L, throttle.waitMillis(6_000))
        assertEquals("trailing edge sends the latest", "D", throttle.due(7_000))
        assertNull(throttle.waitMillis(7_000))
    }

    @Test fun backToTheSentArtworkCancelsThePendingOneAndANewSessionResends() {
        val throttle = SimHubArtworkThrottle(minIntervalMillis = 2_000)
        throttle.offer("A")
        throttle.due(0)
        throttle.offer("B")
        throttle.offer("A")
        assertNull("A is what the plugin has", throttle.waitMillis(500))

        throttle.resend()
        assertEquals("a new link session gets the latest again", "A", throttle.due(3_000))
    }
}
