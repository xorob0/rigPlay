package com.shilapi.xcertplay.simhub

import com.shilapi.xcertplay.transport.VehicleStatusProvider
import com.shilapi.xcertplay.transport.VehicleStatusSnapshot
import kotlin.math.roundToInt

/**
 * CarPlay vehicle status (#46) from SimHub `telemetry`: fuel left as the displayed "battery"
 * percentage and the estimated range, through the existing iAP2 vehicle plane
 * ([com.shilapi.xcertplay.transport.Iap2VehicleStatus]). That plane declares an electric vehicle (see
 * the EngineType note there), so this is opt-in.
 *
 * Uses the last reading with `fuelPercent` and `rangeKm` received in this process, even when the stream
 * is stale (fuel does not change while parked). Identification declares the vehicle only when a reading
 * exists ([com.shilapi.xcertplay.transport.withVehicleStatusFrom]); the plugin sends telemetry only
 * while a phone is connected, so the first connection after the app starts has none and a later
 * connection declares it.
 */
class SimHubVehicleStatusProvider(private val store: SimHubTelemetryStore) : VehicleStatusProvider {
    override fun snapshot(): VehicleStatusSnapshot? = store.lastFuel()?.let(::snapshotOf)

    companion object {
        /** Maps warns about range at or below this much fuel. */
        const val RANGE_WARNING_PERCENT = 10.0

        /**
         * `null` without a range: every update must carry Range, and 0 km would make Maps warn. Energy in
         * Wh is unknown for a fuel tank, so those fields are left out (the update omits unknown energy).
         */
        fun snapshotOf(reading: SimHubTelemetryStore.FuelReading): VehicleStatusSnapshot? {
            val range = reading.rangeKm?.takeIf { it.isFinite() && it >= 0 } ?: return null
            val percent = reading.fuelPercent.takeIf { it.isFinite() }?.coerceIn(0.0, 100.0) ?: return null
            val rangeKm = range.roundToInt().coerceIn(0, MAX_KM)
            // The range on a full tank, extrapolated; with (almost) nothing left there is nothing to scale.
            val maxRangeKm = if (percent >= 1.0) (range * 100.0 / percent).roundToInt().coerceIn(rangeKm, MAX_KM) else rangeKm
            return VehicleStatusSnapshot(
                rangeKm = rangeKm,
                rangeWarning = percent <= RANGE_WARNING_PERCENT,
                batteryPercent = percent,
                currentChargeWh = null,
                maxChargeWh = null,
                maxRangeKm = maxRangeKm,
                charging = false,
            )
        }

        private const val MAX_KM = 0xffff
    }
}
