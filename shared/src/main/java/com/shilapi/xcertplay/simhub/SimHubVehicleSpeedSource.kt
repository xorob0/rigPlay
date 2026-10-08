package com.shilapi.xcertplay.simhub

import com.shilapi.xcertplay.transport.VehicleGear
import com.shilapi.xcertplay.transport.VehicleSpeedReading
import com.shilapi.xcertplay.transport.VehicleSpeedSample
import com.shilapi.xcertplay.transport.VehicleSpeedSource

/**
 * Wheel speed and gear for `$PASCD` (#41) from SimHub `telemetry`: one sample per telemetry message that
 * carries `speedMps` (up to 10 Hz), drained by [com.shilapi.xcertplay.transport.VehicleSpeedLocationProvider]
 * about once a second. Nothing is reported while the stream is stale (no sample for 3 s).
 */
class SimHubVehicleSpeedSource(private val store: SimHubTelemetryStore) : VehicleSpeedSource {
    private val lock = Any()
    private val samples = ArrayList<VehicleSpeedSample>()
    private var gear = VehicleGear.DRIVE
    private var started = false
    private val listener: (SimHubTelemetryStore.Sample) -> Unit = ::onSample

    override fun start() {
        synchronized(lock) {
            if (started) return
            started = true
            samples.clear()
        }
        store.addListener(listener)
    }

    override fun stop() {
        store.removeListener(listener)
        synchronized(lock) {
            started = false
            samples.clear()
        }
    }

    override fun drain(): VehicleSpeedReading? {
        val fresh = store.latest()?.let(store::isFresh) == true
        synchronized(lock) {
            if (samples.isEmpty()) return null
            val drained = samples.toList()
            samples.clear()
            return if (fresh) VehicleSpeedReading(gear, drained) else null
        }
    }

    private fun onSample(sample: SimHubTelemetryStore.Sample) {
        val telemetry = sample.telemetry
        synchronized(lock) {
            if (!started) return
            gear = gearOf(telemetry.gear) ?: gear
            val speed = telemetry.speedMps ?: return
            samples += VehicleSpeedSample(sample.receivedAtMillis, speed)
            // Drained about once a second; a stalled consumer must not grow the buffer without bound.
            while (samples.size > MAX_BUFFERED_SAMPLES) samples.removeAt(0)
        }
    }

    companion object {
        const val MAX_BUFFERED_SAMPLES = 50

        /** `telemetry.gear` (§6.9) to the `$PASCD` gear letter; `null` keeps the last known gear. */
        fun gearOf(gear: Gear?): VehicleGear? = when (gear) {
            Gear.P -> VehicleGear.PARK
            Gear.R -> VehicleGear.REVERSE
            Gear.N -> VehicleGear.NEUTRAL
            Gear.D -> VehicleGear.DRIVE
            null -> null
        }
    }
}
