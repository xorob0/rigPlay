package com.shilapi.xcertplay.simhub

import com.shilapi.xcertplay.iap2.wire.Iap2ParameterList
import com.shilapi.xcertplay.transport.Iap2IdentificationConfig
import com.shilapi.xcertplay.transport.Iap2VehicleStatus
import com.shilapi.xcertplay.transport.VehicleStatusSnapshot
import com.shilapi.xcertplay.transport.withVehicleStatusFrom
import java.nio.ByteBuffer
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class SimHubVehicleStatusProviderTest {
    private var now = 0L
    private val store = SimHubTelemetryStore(nowMillis = { now })
    private val provider = SimHubVehicleStatusProvider(store)

    @Test fun fuelAndRangeBecomeTheVehicleStatus() {
        store.update(SimHubMessage.Telemetry(fuelPercent = 63.2, rangeKm = 148.0))

        assertEquals(
            VehicleStatusSnapshot(
                rangeKm = 148,
                rangeWarning = false,
                batteryPercent = 63.2,
                currentChargeWh = null,
                maxChargeWh = null,
                maxRangeKm = 234, // 148 km at 63.2 % extrapolated to a full tank
                charging = false,
            ),
            provider.snapshot(),
        )
        val update = Iap2ParameterList.parse(Iap2VehicleStatus.update(provider.snapshot()!!).payload)
        assertEquals("percent x 1000", 63_200, ByteBuffer.wrap(update.first(24)!!.payload).int)
        assertEquals(148, ByteBuffer.wrap(update.first(3)!!.payload).short.toInt())
        assertEquals("not charging", 0, update.first(25)!!.payload.single().toInt())
        for (id in 21..23) assertNull("no Wh for a fuel tank", update.first(id))
    }

    @Test fun lowFuelWarnsAndAnEmptyTankDoesNotDivideByZero() {
        store.update(SimHubMessage.Telemetry(fuelPercent = 8.0, rangeKm = 20.0))
        assertTrue(provider.snapshot()!!.rangeWarning)
        assertEquals(250, provider.snapshot()!!.maxRangeKm)

        store.update(SimHubMessage.Telemetry(fuelPercent = 0.0, rangeKm = 0.0))
        assertEquals(0, provider.snapshot()!!.maxRangeKm)
    }

    @Test fun noReadingWithoutFuelAndRange() {
        assertNull("nothing received", provider.snapshot())
        store.update(SimHubMessage.Telemetry(speedMps = 30.0))
        assertNull("no fuel member", provider.snapshot())
        store.update(SimHubMessage.Telemetry(fuelPercent = 50.0))
        assertNull("fuel without range", provider.snapshot())
    }

    @Test fun theLastReadingOutlivesTheStream() {
        store.update(SimHubMessage.Telemetry(fuelPercent = 40.0, rangeKm = 90.0))
        now += 10 * 60_000L
        assertNull(store.fresh())
        assertEquals(90, provider.snapshot()!!.rangeKm)
    }

    @Test fun declaredAtIdentificationOnlyOnceAReadingExists() {
        val config = Iap2IdentificationConfig(
            name = "rigPlay",
            modelIdentifier = "rig",
            manufacturer = "rigPlay",
            serialNumber = "RIGPLAY-1",
            firmwareVersion = "1",
            hardwareVersion = "1.0",
            carPlayUsbInterfaceNumber = 3,
            vehicleStatusEnabled = true,
        )
        assertFalse(config.withVehicleStatusFrom(provider).vehicleStatusEnabled)
        store.update(SimHubMessage.Telemetry(fuelPercent = 40.0, rangeKm = 90.0))
        assertTrue(config.withVehicleStatusFrom(provider).vehicleStatusEnabled)
    }
}
