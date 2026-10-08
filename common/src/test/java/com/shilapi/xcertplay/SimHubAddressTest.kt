package com.shilapi.xcertplay

import com.shilapi.xcertplay.SimHubAddress.Parsed
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class SimHubAddressTest {
    private fun parse(text: String) = SimHubAddress.parse(text, 23711)

    @Test fun hostAlone() = assertEquals(Parsed("192.168.1.20", 23711), parse(" 192.168.1.20 "))
    @Test fun hostAndPort() = assertEquals(Parsed("192.168.1.20", 24000), parse("192.168.1.20:24000"))
    @Test fun hostName() = assertEquals(Parsed("rig-pc.local", 23711), parse("rig-pc.local"))
    @Test fun bracketedIpv6() = assertEquals(Parsed("fe80::1", 23711), parse("[fe80::1]"))
    @Test fun bracketedIpv6WithPort() = assertEquals(Parsed("fe80::1", 24000), parse("[fe80::1]:24000"))
    @Test fun bareIpv6() = assertEquals(Parsed("fe80::1", 23711), parse("fe80::1"))

    @Test fun rejectsGarbage() {
        assertNull(parse(""))
        assertNull(parse("192.168.1.20:"))
        assertNull(parse("192.168.1.20:0"))
        assertNull(parse("192.168.1.20:70000"))
        assertNull(parse(":23711"))
        assertNull(parse("http://192.168.1.20"))
        assertNull(parse("192.168 .1.20"))
        assertNull(parse("[fe80::1"))
    }
}
