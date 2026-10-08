package com.shilapi.xcertplay.network

import java.net.InetAddress
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class AirPlayPeerTest {
    private val listening = InetAddress.getByName("192.168.49.1")

    @Test fun theTabletConnectingToItselfIsNotTheIphone() {
        assertTrue(isOwnAddress(InetAddress.getLoopbackAddress(), listening))
        assertTrue(isOwnAddress(InetAddress.getByName("192.168.49.1"), listening))
    }

    @Test fun anotherHostOnTheNetworkIsExternal() {
        // A documentation address (RFC 5737) no interface of the test machine has.
        assertFalse(isOwnAddress(InetAddress.getByName("192.0.2.45"), listening))
    }
}
