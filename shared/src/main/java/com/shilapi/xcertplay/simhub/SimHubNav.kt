package com.shilapi.xcertplay.simhub

import com.shilapi.xcertplay.glance.CarPlayGlance
import com.shilapi.xcertplay.guidance.RouteManeuverNames

/** CarPlay route guidance as `status.nav` (#47). */
object SimHubNav {
    /** `null` without a phone or an active, fresh route (the plugin then clears `RigPlay.Nav.*`). */
    fun of(glance: CarPlayGlance.Snapshot): NavStatus? {
        if (!glance.connected) return null
        val type = glance.maneuverType ?: return null
        return NavStatus(
            maneuver = RouteManeuverNames.nameOf(type),
            distanceM = glance.distanceMeters.coerceAtLeast(0),
            road = glance.road.ifBlank { null },
            etaEpochS = glance.arrivalEpochSeconds?.takeIf { it > 0 },
        )
    }
}
