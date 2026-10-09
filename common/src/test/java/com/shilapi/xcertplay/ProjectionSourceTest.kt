package com.shilapi.xcertplay

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [32])
class ProjectionSourceTest {
    private val context get() = RuntimeEnvironment.getApplication()

    @Test
    fun carPlayIsTheDefault() {
        assertEquals(ProjectionSource.CARPLAY, ProjectionSourceStore.load(context))
        assertEquals(ProjectionSource.CARPLAY, ProjectionSource.fromStored(null))
        assertEquals(ProjectionSource.CARPLAY, ProjectionSource.fromStored("MIRRORLINK"))
    }

    @Test
    fun selectionRoundTrips() {
        ProjectionSourceStore.save(context, ProjectionSource.ANDROID_AUTO)
        assertEquals(ProjectionSource.ANDROID_AUTO, ProjectionSourceStore.load(context))
        ProjectionSourceStore.save(context, ProjectionSource.CARPLAY)
        assertEquals(ProjectionSource.CARPLAY, ProjectionSourceStore.load(context))
    }

    @Test
    fun receiverIntentsStayInsideThisPackage() {
        val home = AndroidAutoReceiver.homeIntent(context).component!!
        assertEquals(context.packageName, home.packageName)
        assertEquals(AndroidAutoReceiver.HOME_ACTIVITY, home.className)
        val usb = AndroidAutoReceiver.connectUsbIntent(context)
        assertEquals(AndroidAutoReceiver.ACTION_CONNECT, usb.action)
        assertEquals(AndroidAutoReceiver.AUTOMATION_ACTIVITY, usb.component!!.className)
    }

    @Test
    fun receiverIsAbsentFromModulesThatDoNotPackageIt() {
        // common itself never declares the DiAuto activities; only the mobile APK does.
        assertFalse(AndroidAutoReceiver.isPackaged(context))
    }
}
