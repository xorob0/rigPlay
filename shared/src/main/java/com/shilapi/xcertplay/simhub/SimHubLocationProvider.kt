package com.shilapi.xcertplay.simhub

import com.shilapi.xcertplay.transport.CarPlayLocationFix
import com.shilapi.xcertplay.transport.Iap2LocationProvider
import com.shilapi.xcertplay.transport.NmeaLocationEncoder

/**
 * iAP2 LocationInformation from SimHub (#41): the game car's position from `telemetry` (lat, lon,
 * alt, heading, speed) as the `$GPGGA` + `$GPRMC` pair, in place of the tablet's GPS. While the
 * stream is stale (no sample for 3 s) or the sample has no position, nothing is sent and the phone falls
 * back to its own GPS.
 */
class SimHubLocationProvider(
    private val store: SimHubTelemetryStore,
    private val wallClockMillis: () -> Long = System::currentTimeMillis,
) : Iap2LocationProvider {
    /** Always succeeds: positions arrive with telemetry, which may start after the phone asked. */
    override fun start(): Boolean = true

    override fun stop() = Unit

    override fun latestNmea(): String? = currentFix()?.let(NmeaLocationEncoder::encode)

    /** The fix to send now, or `null` without a fresh sample carrying lat and lon. */
    fun currentFix(): CarPlayLocationFix? = store.fresh()?.let { fixOf(it, wallClockMillis()) }

    companion object {
        /** A telemetry position is exact; 5 m gives the phone a confident, ordinary GPS accuracy (HDOP 1). */
        const val ACCURACY_METERS = 5.0

        fun fixOf(telemetry: SimHubMessage.Telemetry, nowMillis: Long): CarPlayLocationFix? {
            val lat = telemetry.lat ?: return null
            val lon = telemetry.lon ?: return null
            return CarPlayLocationFix(
                latitudeDegrees = lat,
                longitudeDegrees = lon,
                altitudeMeters = telemetry.alt,
                bearingDegrees = telemetry.heading,
                speedMetersPerSecond = telemetry.speedMps,
                accuracyMeters = ACCURACY_METERS,
                timestampMillis = nowMillis,
            )
        }
    }
}
