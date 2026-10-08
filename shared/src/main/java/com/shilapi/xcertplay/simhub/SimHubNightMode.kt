package com.shilapi.xcertplay.simhub

/**
 * CarPlay night mode from SimHub (#45): while the setting "Night mode from SimHub" is on and fresh
 * `telemetry` carries `night`, that value wins over the tablet's own day/night (`uiMode`); otherwise the
 * tablet's value is used, as before.
 *
 * The SimHub value is debounced: a change counts once it has held for [debounceMillis], so a game that
 * flips headlights or crosses dusk does not toggle the iPhone's maps back and forth. The first value
 * after none counts at once, and losing the value (stale stream, field disabled) falls back at once.
 * Not thread-safe: call from one thread (the activity's main thread).
 */
class SimHubNightMode(private val debounceMillis: Long = DEBOUNCE_MILLIS) {
    private var enabled = false
    private var accepted: Boolean? = null
    private var candidate: Boolean? = null
    private var candidateSince = 0L

    /** The night mode to send, given the tablet's own [androidNight]. */
    fun effective(androidNight: Boolean): Boolean = if (enabled) accepted ?: androidNight else androidNight

    /**
     * Feeds the setting and the latest SimHub `night` (`null` when absent or stale). Returns the new
     * [effective] value when it changed, else `null`: only changes are sent to the iPhone.
     */
    fun update(enabled: Boolean, night: Boolean?, androidNight: Boolean, nowMillis: Long): Boolean? {
        val before = effective(androidNight)
        this.enabled = enabled
        accept(night, nowMillis)
        val after = effective(androidNight)
        return after.takeIf { it != before }
    }

    private fun accept(night: Boolean?, nowMillis: Long) {
        when {
            night == null -> {
                accepted = null
                candidate = null
            }
            accepted == null -> {
                accepted = night
                candidate = null
            }
            night == accepted -> candidate = null
            night != candidate -> {
                candidate = night
                candidateSince = nowMillis
            }
            nowMillis - candidateSince >= debounceMillis -> {
                accepted = night
                candidate = null
            }
        }
    }

    companion object {
        const val DEBOUNCE_MILLIS = 2_000L
    }
}
