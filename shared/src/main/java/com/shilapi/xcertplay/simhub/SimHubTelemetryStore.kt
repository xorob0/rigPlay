package com.shilapi.xcertplay.simhub

import java.util.concurrent.CopyOnWriteArrayList

/**
 * The latest `telemetry` sample from the paired PC (`docs/protocol.md` §6.9) and when it arrived, for
 * the CarPlay-side consumers ([SimHubLocationProvider], [SimHubVehicleSpeedSource], ...). The link
 * owner feeds it from `SimHubLink.Listener.onTelemetry`; the process-wide instance is
 * [SimHubEndpoints.telemetry].
 *
 * A sample is fresh for [staleAfterMillis] (3 s): the plugin sends up to 10 Hz while the phone is
 * connected, so a longer gap means the game, SimHub or the link went away and the phone must fall back
 * to its own GPS. Thread-safe; listeners run on the thread that called [update] (the link thread) and
 * must not block.
 */
class SimHubTelemetryStore(
    /** Monotonic milliseconds; the same clock stamps [Sample.receivedAtMillis] and speed samples. */
    val nowMillis: () -> Long = { System.nanoTime() / 1_000_000L },
    val staleAfterMillis: Long = STALE_AFTER_MILLIS,
) {
    data class Sample(val telemetry: SimHubMessage.Telemetry, val receivedAtMillis: Long)

    /** The last fuel reading, kept after the stream goes stale: fuel does not change while nobody drives. */
    data class FuelReading(val fuelPercent: Double, val rangeKm: Double?)

    @Volatile private var latest: Sample? = null
    @Volatile private var lastFuel: FuelReading? = null
    private val listeners = CopyOnWriteArrayList<(Sample) -> Unit>()

    fun update(telemetry: SimHubMessage.Telemetry) {
        val sample = Sample(telemetry, nowMillis())
        latest = sample
        telemetry.fuelPercent?.let { lastFuel = FuelReading(it, telemetry.rangeKm) }
        for (listener in listeners) listener(sample)
    }

    /** The last sample, however old. */
    fun latest(): Sample? = latest

    /** The last sample while it is fresh, else `null`. */
    fun fresh(): SimHubMessage.Telemetry? = latest?.takeIf(::isFresh)?.telemetry

    fun isFresh(sample: Sample): Boolean = nowMillis() - sample.receivedAtMillis < staleAfterMillis

    /** The last sample that carried `fuelPercent`, fresh or not; `null` until one arrived in this process. */
    fun lastFuel(): FuelReading? = lastFuel

    fun addListener(listener: (Sample) -> Unit) {
        listeners.add(listener)
    }

    fun removeListener(listener: (Sample) -> Unit) {
        listeners.remove(listener)
    }

    /** Forgets the stream (not [lastFuel]). */
    fun clear() {
        latest = null
    }

    companion object {
        const val STALE_AFTER_MILLIS = 3_000L
    }
}
