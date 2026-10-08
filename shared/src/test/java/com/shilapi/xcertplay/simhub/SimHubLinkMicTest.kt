package com.shilapi.xcertplay.simhub

import java.net.InetAddress
import java.util.Random
import java.util.concurrent.TimeUnit
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/** The link side of the PC microphone (#34, §6.13): feature `mic`, `state.mic`, `micStart`/`micStop`. */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [29], manifest = Config.NONE)
class SimHubLinkMicTest {
    private val server = FakeSimHubServer().start()
    private var link: SimHubLink? = null
    @Volatile private var userChosePc = true

    @After fun tearDown() {
        link?.stop()
        server.stop()
    }

    private fun connect(tabletFeatures: Set<String>, micEnabled: Boolean?): SimHubLinkMicTransport {
        server.welcomeFeatures = listOf(SimHubProtocol.FEATURE_TELEMETRY, SimHubProtocol.FEATURE_MIC)
        server.stateAfterPairing = server.stateAfterPairing.copy(mic = micEnabled?.let { MicSettings(it) })
        val next = SimHubLink(IDENTITY, object : SimHubLink.Listener {}, features = tabletFeatures, timing = FAST, random = Random(1), log = {})
        link = next
        next.start(SimHubLink.Target("127.0.0.1", server.port, FakeSimHubServer.HOST_ID, FakeSimHubServer.TOKEN))
        val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5)
        while (System.nanoTime() < deadline && next.state.audio == null) Thread.sleep(10)
        assertNotNull("paired with a state", next.state.audio)
        return SimHubLinkMicTransport(next) { userChosePc }
    }

    @Test fun thePcMicrophoneIsOfferedWithTheFeatureAndStateMicAndTheUsersChoice() {
        val transport = connect(setOf(SimHubProtocol.FEATURE_MIC), micEnabled = true)
        val link = link!!
        assertTrue(link.state.hasFeature(SimHubProtocol.FEATURE_MIC))
        assertEquals(MicSettings(true), link.state.mic)
        assertTrue(transport.micAvailable)
        assertEquals(InetAddress.getByName("127.0.0.1"), transport.pairedHost)
        assertEquals(1L, transport.micEpoch)

        assertTrue(transport.micStart(16_000, 23_713))
        assertEquals(SimHubMessage.MicStart(sampleRate = 16_000, port = 23_713), server.await<SimHubMessage.MicStart>())
        assertTrue(transport.micStop())
        assertEquals(SimHubMessage.MicStop(), server.await<SimHubMessage.MicStop>())

        userChosePc = false
        assertFalse(transport.micAvailable)

        // The plugin switched it off: state.mic.enabled false, micStart is not sent.
        userChosePc = true
        server.send(server.stateAfterPairing.copy(mic = MicSettings(false)))
        val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(3)
        while (System.nanoTime() < deadline && link.state.mic?.enabled != false) Thread.sleep(10)
        assertFalse(transport.micAvailable)
        assertFalse(link.sendMicStart(16_000))
    }

    @Test fun withoutTheFeatureOrStateMicTheTabletMicrophoneIsUsed() {
        val transport = connect(setOf(SimHubProtocol.FEATURE_TELEMETRY), micEnabled = true)
        assertFalse(link!!.state.hasFeature(SimHubProtocol.FEATURE_MIC))
        assertFalse(transport.micAvailable)
        assertFalse(transport.micStart(16_000, 23_713))
        assertFalse(transport.micStop())
        assertNull(server.await<SimHubMessage.MicStart>(timeoutMs = 300))
    }

    @Test fun anAbsentStateMicMeansUnavailable() {
        val transport = connect(setOf(SimHubProtocol.FEATURE_MIC), micEnabled = null)
        assertTrue(link!!.state.hasFeature(SimHubProtocol.FEATURE_MIC))
        assertFalse(transport.micAvailable)
    }

    private companion object {
        val IDENTITY = SimHubLink.Identity(tabletId = "tablet-mic-test", name = "Tab", appVersion = "0.3.0")
        val FAST = SimHubLink.Timing(
            heartbeatIntervalMs = 100,
            linkLossTimeoutMs = 600,
            reconnectInitialDelayMs = 100,
            reconnectMaxDelayMs = 400,
            reconnectJitter = 0.0,
            connectTimeoutMs = 1_000,
            statusMinIntervalMs = 50,
            errorReplyMinIntervalMs = 0,
        )
    }
}
