package com.shilapi.xcertplay

/** Settings → Location → "Location source" (#41), persisted as `location_source`. */
enum class LocationSource(val key: String) {
    /** The game car's position and speed from SimHub `telemetry`; also declares wheel speed (`$PASCD`). */
    SIMHUB("simhub"),

    /** The tablet's own GPS (needs precise location permission). */
    TABLET("tablet"),

    /** No location to the iPhone; it uses its own GPS. */
    NONE("none"),
    ;

    companion object {
        fun fromKey(key: String?): LocationSource? = values().firstOrNull { it.key == key }
    }
}
