package com.shilapi.xcertplay.simhub

import com.shilapi.xcertplay.transport.Iap2LocationMessages
import com.shilapi.xcertplay.transport.PascdEncoder
import com.shilapi.xcertplay.transport.VehicleGear
import com.shilapi.xcertplay.transport.VehicleSpeedLocationProvider
import com.shilapi.xcertplay.transport.VehicleSpeedSample
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class SimHubTelemetryTest {
    private var now = 10_000L
    private val store = SimHubTelemetryStore(nowMillis = { now })

    private val nordschleife = SimHubMessage.Telemetry(
        speedMps = 41.7,
        gear = Gear.D,
        heading = 274.5,
        lat = 50.3356,
        lon = 6.9475,
        alt = 617.0,
    )

    @Test fun knownFixEncodesGgaAndRmcWithChecksumKnotsAndUtc() {
        store.update(nordschleife)
        // 1791043200123 ms = 2026-10-03 16:00:00.123 UTC.
        val provider = SimHubLocationProvider(store, wallClockMillis = { 1_791_043_200_123L })

        assertEquals(
            "\$GPGGA,160000.00,5020.1360,N,00656.8500,E,1,08,1.0,617.0,M,0.0,M,,*59\r\n" +
                "\$GPRMC,160000.00,A,5020.1360,N,00656.8500,E,81.06,274.50,031026,,*02\r\n",
            provider.latestNmea(),
        )
        val fix = provider.currentFix()!!
        assertEquals(5.0, fix.accuracyMeters!!, 0.0)
        assertEquals(274.5, fix.bearingDegrees!!, 0.0)
        assertEquals(41.7, fix.speedMetersPerSecond!!, 0.0)
        assertEquals(1_791_043_200_123L, fix.timestampMillis)
        for (sentence in provider.latestNmea()!!.trimEnd().lines()) {
            val star = sentence.lastIndexOf('*')
            assertEquals(sentence, checksum(sentence.substring(1, star)), sentence.substring(star + 1))
        }
    }

    @Test fun southWestPositionWithoutHeadingOrAltitude() {
        store.update(SimHubMessage.Telemetry(lat = -33.5, lon = -70.25))
        val nmea = SimHubLocationProvider(store, wallClockMillis = { 1L }).latestNmea()!!

        assertTrue(nmea, nmea.contains(",3330.0000,S,07015.0000,W,1,08,1.0,0.0,M,"))
        // No heading: the RMC course stays empty instead of claiming north; no speed: 0 knots.
        assertTrue(nmea, nmea.contains(",W,0.00,,010170,,*"))
    }

    @Test fun noFixWithoutPositionOrWhenStale() {
        val provider = SimHubLocationProvider(store)
        assertNull("nothing received yet", provider.latestNmea())

        store.update(SimHubMessage.Telemetry(speedMps = 20.0, lon = 6.9))
        assertNull("no latitude", provider.latestNmea())

        store.update(nordschleife)
        assertNotNull(provider.latestNmea())
        now += SimHubTelemetryStore.STALE_AFTER_MILLIS - 1
        assertNotNull("still fresh just before 3 s", provider.latestNmea())
        now += 1
        assertNull("stale after 3 s without telemetry", provider.latestNmea())
        assertTrue("starting never fails: telemetry may come later", provider.start())
    }

    @Test fun gearMapping() {
        assertEquals(VehicleGear.PARK, SimHubVehicleSpeedSource.gearOf(Gear.P))
        assertEquals(VehicleGear.REVERSE, SimHubVehicleSpeedSource.gearOf(Gear.R))
        assertEquals(VehicleGear.NEUTRAL, SimHubVehicleSpeedSource.gearOf(Gear.N))
        assertEquals(VehicleGear.DRIVE, SimHubVehicleSpeedSource.gearOf(Gear.D))
        assertNull(SimHubVehicleSpeedSource.gearOf(null))
    }

    @Test fun speedSamplesFollowTelemetryAndProducePascd() {
        val speed = SimHubVehicleSpeedSource(store)
        store.update(nordschleife)
        speed.start()
        assertNull("nothing since start", speed.drain())

        now = 12_000L
        store.update(SimHubMessage.Telemetry(speedMps = 1.5, gear = Gear.R))
        now = 12_100L
        // No gear in this sample: the last known gear stays.
        store.update(SimHubMessage.Telemetry(speedMps = 2.0))
        now = 12_150L
        store.update(SimHubMessage.Telemetry(rpm = 900.0)) // no speed: no sample

        val reading = speed.drain()!!
        assertEquals(VehicleGear.REVERSE, reading.gear)
        assertEquals(listOf(VehicleSpeedSample(12_000L, 1.5), VehicleSpeedSample(12_100L, 2.0)), reading.samples)
        assertEquals("\$PASCD,12.000,C,R,0,2,0.00,1.500,0.10,2.000*60\r\n", PascdEncoder.encode(reading))
        assertNull("drained", speed.drain())
    }

    @Test fun staleSpeedIsDropped() {
        val speed = SimHubVehicleSpeedSource(store)
        speed.start()
        store.update(SimHubMessage.Telemetry(speedMps = 30.0))
        now += SimHubTelemetryStore.STALE_AFTER_MILLIS
        assertNull("stale samples are not reported", speed.drain())
        now += 100
        store.update(SimHubMessage.Telemetry(speedMps = 31.0))
        assertEquals(listOf(VehicleSpeedSample(now, 31.0)), speed.drain()!!.samples)
    }

    @Test fun stoppedSourceIgnoresTelemetryAndBufferIsBounded() {
        val speed = SimHubVehicleSpeedSource(store)
        store.update(SimHubMessage.Telemetry(speedMps = 5.0))
        speed.start()
        repeat(SimHubVehicleSpeedSource.MAX_BUFFERED_SAMPLES + 10) {
            now += 1
            store.update(SimHubMessage.Telemetry(speedMps = it.toDouble()))
        }
        val samples = speed.drain()!!.samples
        assertEquals(SimHubVehicleSpeedSource.MAX_BUFFERED_SAMPLES, samples.size)
        assertEquals(59.0, samples.last().metersPerSecond, 0.0)

        speed.stop()
        store.update(SimHubMessage.Telemetry(speedMps = 5.0))
        assertNull(speed.drain())
    }

    @Test fun vehicleSpeedLocationProviderAddsPascdToTheSimHubFix() {
        val provider = VehicleSpeedLocationProvider(SimHubLocationProvider(store), SimHubVehicleSpeedSource(store))
        provider.onRequested(setOf(0, 1, Iap2LocationMessages.VEHICLE_SPEED_DATA))
        assertTrue(provider.start())
        store.update(nordschleife)

        val nmea = provider.latestNmea()!!
        assertTrue(nmea, nmea.startsWith("\$GPGGA,"))
        assertTrue(nmea, nmea.contains("\r\n\$GPRMC,"))
        assertTrue(nmea, nmea.contains("\r\n\$PASCD,10.000,C,D,0,1,0.00,41.700*"))
        provider.stop()
    }

    @Test fun lastFuelSurvivesStalenessAndPartialSamples() {
        assertNull(store.lastFuel())
        store.update(SimHubMessage.Telemetry(fuelPercent = 63.2, rangeKm = 148.0))
        store.update(SimHubMessage.Telemetry(speedMps = 10.0))
        now += 60_000
        assertNull(store.fresh())
        assertEquals(SimHubTelemetryStore.FuelReading(63.2, 148.0), store.lastFuel())
    }

    private fun checksum(body: String): String {
        var value = 0
        for (character in body) value = value xor character.code
        return "%02X".format(value)
    }
}
