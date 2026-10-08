package com.shilapi.xcertplay

/**
 * Settings → "When no iPhone is connected" (#39): what the tablet shows while SimHub is up and no
 * phone is connected. With SimHub down the rigPlay idle screen is shown either way, since the PC's
 * web server cannot serve a dashboard then.
 */
enum class IdleMode(val key: String) {
    /** SimHub's idle dashboard (`state.idleDashboardUrl`), else its main dashboard, else the rigPlay screen. */
    DASHBOARD("dashboard"),

    /** The built-in rigPlay idle screen (clock, PC name). */
    RIGPLAY_SCREEN("screen");

    companion object {
        val DEFAULT = DASHBOARD

        fun fromKey(key: String?): IdleMode = values().firstOrNull { it.key == key } ?: DEFAULT
    }
}

/** "Turn the screen off" on the rigPlay idle screen: minutes, 0 = never. */
object IdleScreenOff {
    val CHOICES = listOf(0, 1, 5, 15, 30)
    const val DEFAULT = 0

    fun sanitize(minutes: Int): Int = if (minutes in CHOICES) minutes else DEFAULT
}

/**
 * "Go idle after" (#53): minutes without a touch before the rigPlay idle screen takes over the home
 * page or settings while no phone is connected. 0 = immediately: the moment the PC goes away (or the
 * phone does), without a timer afterwards.
 */
object IdleAfter {
    val CHOICES = listOf(0, 1, 3, 5, 10)
    const val DEFAULT = 3

    fun sanitize(minutes: Int): Int = if (minutes in CHOICES) minutes else DEFAULT
}
