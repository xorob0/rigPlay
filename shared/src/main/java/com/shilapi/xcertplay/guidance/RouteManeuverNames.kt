package com.shilapi.xcertplay.guidance

/**
 * Apple's iAP2 RouteGuidanceManeuverType values (the [RouteManeuver.type] of 0x5202) as lowerCamel
 * names, the vocabulary of `status.nav.maneuver` (#47). The grouping matches the navigation widget's
 * arrows and the removed BYD cluster mapping: 28–46 are roundabout exits 1–19 (`type - 27`).
 */
object RouteManeuverNames {
    private val NAMES = arrayOf(
        "noTurn", // 0
        "leftTurn", // 1
        "rightTurn", // 2
        "straightAhead", // 3
        "uTurn", // 4
        "followRoad", // 5
        "enterRoundabout", // 6
        "exitRoundabout", // 7
        "offRamp", // 8
        "onRamp", // 9
        "arriveEndOfNavigation", // 10
        "startRoute", // 11
        "arriveAtDestination", // 12
        "keepLeft", // 13
        "keepRight", // 14
        "enterFerry", // 15
        "exitFerry", // 16
        "changeFerry", // 17
        "startRouteWithUTurn", // 18
        "uTurnAtRoundabout", // 19
        "leftTurnAtEnd", // 20
        "rightTurnAtEnd", // 21
        "highwayOffRampLeft", // 22
        "highwayOffRampRight", // 23
        "arriveAtDestinationLeft", // 24
        "arriveAtDestinationRight", // 25
        "uTurnWhenPossible", // 26
        "arriveEndOfDirections", // 27
    ) + Array(19) { "roundaboutExit${it + 1}" } + arrayOf( // 28–46
        "sharpLeftTurn", // 47
        "sharpRightTurn", // 48
        "slightLeftTurn", // 49
        "slightRightTurn", // 50
        "changeHighway", // 51
        "changeHighwayLeft", // 52
        "changeHighwayRight", // 53
    )

    /** Every name, in type order. */
    val ALL: List<String> = NAMES.toList()

    /** The name of [type]; a type newer than this table is `noTurn` (no specific arrow). */
    fun nameOf(type: Int): String = NAMES.getOrNull(type) ?: NAMES[0]
}
