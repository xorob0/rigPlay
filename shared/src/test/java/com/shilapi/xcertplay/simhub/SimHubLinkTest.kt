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
import org.junit.Assert.fail
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [29], manifest = Config.NONE)
class SimHubLinkTest {
    private lateinit var server: FakeSimHubServer
    private val events = RecordingListener()
    private var link: SimHubLink? = null

    @Before fun setUp() {
        server = FakeSimHubServer().start()
    }

    @After fun tearDown() {
        link?.stop()
        server.stop()
    }

    @Test fun connectSendsHelloAndReachesUnpairedOnWelcome() {
        val link = newLink()
        link.start(SimHubLink.Target("127.0.0.1", server.port))

        val hello = server.await<SimHubMessage.Hello>() ?: fail("no hello") as Nothing
        assertEquals(IDENTITY.tabletId, hello.tabletId)
        assertEquals(IDENTITY.name, hello.name)
        assertEquals(IDENTITY.appVersion, hello.appVersion)
        assertEquals(1, hello.protocol)
        assertEquals(1, hello.minProtocol)
        assertEquals(listOf(SimHubProtocol.FEATURE_IDLE_DASHBOARD), hello.features)

        val unpaired = events.awaitState { it.phase == SimHubState.Phase.UNPAIRED }
        assertEquals(FakeSimHubServer.HOST_ID, unpaired.hostId)
        assertEquals("RIG-PC", unpaired.hostName)
        assertEquals(1, unpaired.protocol)
        assertEquals(setOf(SimHubProtocol.FEATURE_IDLE_DASHBOARD), unpaired.features)
        assertTrue(unpaired.connected)
        assertFalse(unpaired.paired)

        // Heartbeats flow from the tablet once welcome arrived (§9); no pairRequest without a token or request.
        assertNotNull(server.await<SimHubMessage.Heartbeat>())
        assertNull(server.await<SimHubMessage.PairRequest>(timeoutMs = 300))
        assertEquals(1, server.accepted.get())
    }

    @Test fun storedTokenResumesPairingAndDeliversState() {
        val link = newLink()
        val status = SimHubMessage.Status(phoneConnected = true, phoneName = "Tim's iPhone", screen = Screen.CARPLAY, nowPlaying = null)
        link.send(status)
        link.start(pairedTarget())

        assertEquals(SimHubMessage.PairRequest(token = FakeSimHubServer.TOKEN), server.await<SimHubMessage.PairRequest>())
        val up = events.await<Event.LinkUp>()
        assertTrue(up.state.paired)
        assertEquals(Event.Paired(FakeSimHubServer.HOST_ID, FakeSimHubServer.TOKEN), events.await<Event.Paired>())

        val withState = events.awaitState { it.dashboardUrl != null }
        assertEquals("http://127.0.0.1:8888/Dash#Pit%20Board", withState.dashboardUrl)
        assertEquals("http://127.0.0.1:8888/Dash#Rig%20Clock", withState.idleDashboardUrl)
        assertTrue(withState.dashboardServerReachable)
        assertTrue(withState.audioEnabled)
        // Status is sent right after pairing (§6.7), with the value set before connecting.
        assertEquals(status, server.await<SimHubMessage.Status>())
    }

    @Test fun tokenIsNotSentToADifferentHost() {
        val link = newLink()
        link.start(SimHubLink.Target("127.0.0.1", server.port, hostId = "00000000-0000-4000-8000-000000000000", token = FakeSimHubServer.TOKEN))
        val failure = events.await<Event.AttemptFailed>()
        assertEquals(SimHubLink.LossReason.HOST_MISMATCH, failure.loss.reason)
        assertNull(server.await<SimHubMessage.PairRequest>(timeoutMs = 200))
    }

    @Test fun pinFlowPairsAndReportsTheNewToken() {
        val link = newLink()
        link.start(SimHubLink.Target("127.0.0.1", server.port))
        events.awaitState { it.phase == SimHubState.Phase.UNPAIRED }

        link.requestPairing()
        assertEquals(SimHubMessage.PairRequest.START, server.await<SimHubMessage.PairRequest> { it.pin == null })
        val required = events.await<Event.PairResultReceived>().result
        assertEquals(PairFailure.PIN_REQUIRED, required.reason)
        assertEquals(120, required.pinExpiresInSec)
        assertEquals(PairFailure.PIN_REQUIRED, events.awaitState { it.lastPairResult != null }.lastPairResult?.reason)

        assertTrue(link.pair("111111"))
        assertEquals(PairFailure.WRONG_PIN, events.await<Event.PairResultReceived>().result.reason)

        assertTrue(link.pair(FakeSimHubServer.PIN))
        assertEquals(SimHubMessage.PairRequest(pin = FakeSimHubServer.PIN), server.await<SimHubMessage.PairRequest> { it.pin == FakeSimHubServer.PIN })
        assertTrue(events.await<Event.PairResultReceived>().result.ok)
        assertEquals(Event.Paired(FakeSimHubServer.HOST_ID, FakeSimHubServer.ISSUED_TOKEN), events.await<Event.Paired>())
        events.await<Event.LinkUp>()
        val target = link.currentTarget!!
        assertEquals(FakeSimHubServer.HOST_ID, target.hostId)
        assertEquals(FakeSimHubServer.ISSUED_TOKEN, target.token)
        assertFalse("pair() is refused once paired", link.pair(FakeSimHubServer.PIN))
    }

    @Test fun pairingRequestedBeforeConnectingSendsStartAfterWelcome() {
        val link = newLink()
        link.requestPairing()
        link.start(SimHubLink.Target("127.0.0.1", server.port))
        assertEquals(SimHubMessage.PairRequest.START, server.await<SimHubMessage.PairRequest>())
    }

    @Test fun invalidTokenIsRevoked() {
        server.validToken = "something-else"
        val link = newLink()
        link.start(pairedTarget())
        assertEquals(Event.TokenRevoked(FakeSimHubServer.HOST_ID, "tokenInvalid"), events.await<Event.TokenRevoked>())
        assertNull(link.currentTarget!!.token)
        assertEquals(SimHubState.Phase.UNPAIRED, link.state.phase)
    }

    @Test fun heartbeatLossIsDetectedWithinTheTimeout() {
        val link = newLink()
        link.start(pairedTarget())
        events.await<Event.LinkUp>()

        val silentAt = System.nanoTime()
        server.silent = true
        val lost = events.await<Event.LinkLost>(timeoutMs = 3_000)
        val elapsed = TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - silentAt)
        assertEquals(SimHubLink.LossReason.HEARTBEAT_TIMEOUT, lost.loss.reason)
        val timeout = FAST.linkLossTimeoutMs
        assertTrue("lost after $elapsed ms", elapsed >= timeout - FAST.heartbeatIntervalMs - 50)
        assertTrue("lost after $elapsed ms", elapsed <= timeout + 500)
        assertEquals(FAST.reconnectInitialDelayMs, lost.loss.retryInMs)
        assertFalse(link.state.connected)
    }

    @Test fun heartbeatLossWithSpecTimingsFiresWithinFiveToSixSeconds() {
        val link = newLink(SimHubLink.Timing())
        link.start(pairedTarget())
        events.await<Event.LinkUp>()
        // The last line the tablet receives is at most one server heartbeat (100 ms) before silence.
        val silentAt = System.nanoTime()
        server.silent = true
        val lost = events.await<Event.LinkLost>(timeoutMs = 8_000)
        val elapsed = TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - silentAt)
        assertEquals(SimHubLink.LossReason.HEARTBEAT_TIMEOUT, lost.loss.reason)
        assertTrue("lost after $elapsed ms", elapsed in 4_850..6_000)
        val retry = lost.loss.retryInMs!!
        assertTrue("first retry $retry ms is 1 s ± 20 %", retry in 800..1_200)
    }

    @Test fun reconnectsWithBackoffAfterTheServerRestarts() {
        val link = newLink()
        link.start(pairedTarget())
        events.await<Event.LinkUp>()

        server.stop()
        val lost = events.await<Event.LinkLost>()
        assertTrue(lost.loss.reason in setOf(SimHubLink.LossReason.CLOSED_BY_PEER, SimHubLink.LossReason.IO_ERROR))

        // While the PC is off every attempt fails and the delay doubles up to the cap.
        val retries = (1..4).map { events.await<Event.AttemptFailed>().loss }
        retries.forEach { assertEquals(SimHubLink.LossReason.CONNECT_FAILED, it.reason) }
        assertEquals(listOf(200L, 400L, 400L, 400L), retries.map { it.retryInMs })

        server.restart()
        val up = events.await<Event.LinkUp>(timeoutMs = 10_000)
        assertTrue(up.state.paired)
        assertTrue(server.accepted.get() >= 2)
        assertEquals(SimHubMessage.PairRequest(token = FakeSimHubServer.TOKEN), server.await<SimHubMessage.PairRequest>())
    }

    @Test fun backoffResetsAfterASuccessfulPairing() {
        val link = newLink()
        link.start(pairedTarget())
        events.await<Event.LinkUp>()
        server.closeConnections()
        assertEquals(FAST.reconnectInitialDelayMs, events.await<Event.LinkLost>().loss.retryInMs)
        events.await<Event.LinkUp>()
        server.closeConnections()
        assertEquals(FAST.reconnectInitialDelayMs, events.await<Event.LinkLost>().loss.retryInMs)
    }

    @Test fun beaconOfThePairedHostReconnectsAtOnce() {
        val slowRetry = FAST.copy(reconnectInitialDelayMs = 20_000, reconnectMaxDelayMs = 20_000)
        val link = newLink(slowRetry)
        link.start(pairedTarget())
        events.await<Event.LinkUp>()
        server.stop()
        assertEquals(20_000L, events.await<Event.LinkLost>().loss.retryInMs)
        server.restart()

        val start = System.nanoTime()
        link.onBeacon(beacon(hostId = "someone-else"))
        assertNull(events.poll<Event.LinkUp>(300))
        link.onBeacon(beacon(hostId = FakeSimHubServer.HOST_ID))
        // "At once" means long before the 20 s retry would have fired; the bound leaves room for a busy runner.
        events.await<Event.LinkUp>(timeoutMs = 10_000)
        assertTrue(TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - start) < 10_000)
        assertEquals("127.0.0.1", link.currentTarget!!.host)
    }

    @Test fun mediaAndScreenCommandsAreDispatched() {
        val link = newLink()
        link.start(pairedTarget())
        events.await<Event.LinkUp>()

        server.sendLine("""{"type":"command","command":"media","action":"next"}""")
        assertEquals(SimHubCommand.Media(MediaAction.NEXT), events.await<Event.CommandReceived>().command)
        server.send(SimHubMessage.Command(SimHubCommand.Media(MediaAction.PLAY_PAUSE)))
        assertEquals(SimHubCommand.Media(MediaAction.PLAY_PAUSE), events.await<Event.CommandReceived>().command)
        server.send(SimHubMessage.Command(SimHubCommand.ShowDashboard))
        assertEquals(SimHubCommand.ShowDashboard, events.await<Event.CommandReceived>().command)

        assertTrue(link.sendCommandUnavailable("no phone"))
        val error = server.await<SimHubMessage.Error>() ?: fail("no error") as Nothing
        assertEquals(SimHubProtocol.ERROR_COMMAND_UNAVAILABLE, error.code)
        assertEquals("command", error.refType)
    }

    @Test fun commandsBeforePairingAreIgnored() {
        val link = newLink()
        link.start(SimHubLink.Target("127.0.0.1", server.port))
        events.awaitState { it.phase == SimHubState.Phase.UNPAIRED }
        server.send(SimHubMessage.Command(SimHubCommand.ShowCarPlay))
        assertNull(events.poll<Event.CommandReceived>(300))
    }

    @Test fun malformedLinesAreIgnoredWithoutDroppingTheLink() {
        val link = newLink()
        link.start(pairedTarget())
        events.await<Event.LinkUp>()

        server.sendLine("this is not json")
        server.sendLine("""["command"]""")
        server.sendLine("""{"type":"selfDestruct","delaySec":3}""")
        server.sendLine("""{"type":"command","command":"media","action":"shuffle"}""")
        server.sendLine("")
        server.sendLine("""{"type":"command","command":"showCarPlay"}""")

        assertEquals(SimHubCommand.ShowCarPlay, events.await<Event.CommandReceived>().command)
        // Known type with invalid content: answered with badMessage and refType (§14.2).
        val bad = server.await<SimHubMessage.Error>() ?: fail("no badMessage") as Nothing
        assertEquals(SimHubProtocol.ERROR_BAD_MESSAGE, bad.code)
        assertEquals("command", bad.refType)
        assertFalse(bad.isFatal)
        assertNull(events.poll<Event.LinkLost>(300))
        assertTrue(link.state.paired)
        assertEquals(1, server.accepted.get())
    }

    @Test fun shutdownErrorClosesWithoutAReconnectStorm() {
        val link = newLink()
        link.start(pairedTarget())
        events.await<Event.LinkUp>()

        server.send(SimHubMessage.Error(SimHubProtocol.ERROR_SHUTDOWN, "SimHub is closing", fatal = true))
        server.stop()
        assertEquals(SimHubProtocol.ERROR_SHUTDOWN, events.await<Event.ErrorReceived>().error.code)
        val lost = events.await<Event.LinkLost>()
        assertEquals(SimHubLink.LossReason.PEER_SHUTDOWN, lost.loss.reason)
        assertEquals(FAST.reconnectInitialDelayMs, lost.loss.retryInMs)

        // Back-off still applies: 100 + 200 + 400 + 400 … ms, so about five attempts in 1.5 s, not hundreds.
        Thread.sleep(1_500)
        val attempts = events.drain().filterIsInstance<Event.AttemptFailed>()
        assertTrue("attempts ${attempts.size}", attempts.size in 2..6)
        assertTrue(attempts.all { it.loss.reason == SimHubLink.LossReason.CONNECT_FAILED })
    }

    @Test fun unsupportedProtocolHaltsUntilRetried() {
        server.helloResponder = {
            listOf(SimHubMessage.Error(SimHubProtocol.ERROR_UNSUPPORTED_PROTOCOL, "plugin speaks 2-3", fatal = true, minProtocol = 2, maxProtocol = 3))
        }
        val link = newLink()
        link.start(pairedTarget())
        val failure = events.await<Event.AttemptFailed>()
        assertEquals(SimHubLink.LossReason.INCOMPATIBLE, failure.loss.reason)
        assertNull(failure.loss.retryInMs)
        assertEquals(SimHubState.Phase.INCOMPATIBLE, events.awaitState { it.phase == SimHubState.Phase.INCOMPATIBLE }.phase)
        assertNull(events.poll<Event.AttemptFailed>(500))
        assertEquals(1, server.accepted.get())

        // Same range in the beacon: still halted. User retry: connects again.
        link.onBeacon(beacon(FakeSimHubServer.HOST_ID, minProtocol = 2, protocol = 3))
        assertNull(events.poll<Event.AttemptFailed>(300))
        server.helloResponder = null
        link.reconnectNow()
        events.await<Event.LinkUp>()
    }

    @Test fun overlongLineIsAnsweredWithLineTooLong() {
        val link = newLink()
        link.start(pairedTarget())
        events.await<Event.LinkUp>()
        server.sendLine("x".repeat(SimHubProtocol.MAX_LINE_BYTES + 10))
        assertEquals(SimHubLink.LossReason.LINE_TOO_LONG, events.await<Event.LinkLost>().loss.reason)
        val error = server.await<SimHubMessage.Error>() ?: fail("no lineTooLong") as Nothing
        assertEquals(SimHubProtocol.ERROR_LINE_TOO_LONG, error.code)
        assertTrue(error.isFatal)
    }

    @Test fun statusIsCoalescedToTheLatestValue() {
        val link = newLink(FAST.copy(statusMinIntervalMs = 200))
        link.start(pairedTarget())
        events.await<Event.LinkUp>()
        assertEquals(SimHubMessage.Status.IDLE, server.await<SimHubMessage.Status>())

        val burst = (1..20).map { index ->
            SimHubMessage.Status(phoneConnected = true, phoneName = "phone $index", screen = Screen.CARPLAY, nowPlaying = null)
        }
        burst.forEach(link::send)
        Thread.sleep(600)
        val sent = server.received.filterIsInstance<SimHubMessage.Status>()
        assertTrue("sent ${sent.size} statuses for a burst of 20", sent.size in 1..3)
        assertEquals(burst.last(), sent.last())
    }

    @Test fun audioMessagesNeedAPairedLinkWithAudioEnabled() {
        val link = newLink()
        assertFalse(link.sendAudioStart(AudioStream.MEDIA, 48_000, 2))
        link.start(pairedTarget())
        events.awaitState { it.audio != null }
        assertTrue(link.sendAudioStart(AudioStream.MEDIA, 48_000, 2))
        assertEquals(
            SimHubMessage.AudioStart(AudioStream.MEDIA, AudioFormat.PCM_S16LE, 48_000, 2),
            server.await<SimHubMessage.AudioStart>(),
        )
        assertTrue(link.sendAudioStop(AudioStream.MEDIA))
        assertEquals(SimHubMessage.AudioStop(AudioStream.MEDIA), server.await<SimHubMessage.AudioStop>())

        server.send(server.stateAfterPairing.copy(audio = AudioSettings(enabled = false, port = 23712, formats = listOf("pcm_s16le"))))
        events.awaitState { it.audio?.enabled == false }
        assertFalse(link.sendAudioStart(AudioStream.MEDIA, 48_000, 2))
    }

    @Test fun stopSendsShutdownAndDoesNotReconnect() {
        val link = newLink()
        link.start(pairedTarget())
        events.await<Event.LinkUp>()
        link.stop()
        val error = server.await<SimHubMessage.Error>() ?: fail("no shutdown") as Nothing
        assertEquals(SimHubProtocol.ERROR_SHUTDOWN, error.code)
        assertTrue(error.isFatal)
        assertEquals(SimHubState.STOPPED, link.state)
        events.drain()
        Thread.sleep(400)
        assertTrue(events.drain().isEmpty())
        assertEquals(1, server.accepted.get())
    }

    @Test fun stopFromACallbackDoesNotDeadlock() {
        val stopper = object : SimHubLink.Listener {
            @Volatile var link: SimHubLink? = null
            override fun onLinkUp(state: SimHubState) {
                link?.stop()
                events.onLinkUp(state)
            }
        }
        val link = SimHubLink(IDENTITY, stopper, timing = FAST, random = Random(1), log = {})
        stopper.link = link
        this.link = link
        link.start(pairedTarget())
        events.await<Event.LinkUp>()
        assertNotNull(server.await<SimHubMessage.Error> { it.code == SimHubProtocol.ERROR_SHUTDOWN })
        assertEquals(SimHubState.Phase.STOPPED, link.state.phase)
    }

    private fun newLink(timing: SimHubLink.Timing = FAST): SimHubLink =
        SimHubLink(IDENTITY, events, timing = timing, random = Random(1), log = { println("LINK " + it) }).also { link = it }

    private fun pairedTarget() = SimHubLink.Target("127.0.0.1", server.port, FakeSimHubServer.HOST_ID, FakeSimHubServer.TOKEN)

    private fun beacon(hostId: String, minProtocol: Int = 1, protocol: Int = 1) = DiscoveredHost(
        hostId = hostId,
        name = "RIG-PC",
        address = InetAddress.getByName("127.0.0.1"),
        controlPort = server.port,
        audioPort = 23712,
        version = "0.1.0",
        simhubVersion = null,
        protocol = protocol,
        minProtocol = minProtocol,
        lastSeen = 0,
    )

    sealed class Event {
        data class StateChanged(val state: SimHubState) : Event()
        data class LinkUp(val state: SimHubState) : Event()
        data class LinkLost(val loss: SimHubLink.LinkLoss) : Event()
        data class AttemptFailed(val loss: SimHubLink.LinkLoss) : Event()
        data class PairResultReceived(val result: SimHubMessage.PairResult) : Event()
        data class Paired(val hostId: String, val token: String) : Event()
        data class TokenRevoked(val hostId: String, val code: String) : Event()
        data class CommandReceived(val command: SimHubCommand) : Event()
        data class ErrorReceived(val error: SimHubMessage.Error) : Event()
    }

    /**
     * Records callbacks. [await] and [poll] take the oldest not yet taken event of the requested type,
     * whatever came before it, so tests do not depend on the order of unrelated callbacks.
     */
    class RecordingListener : SimHubLink.Listener {
        private val lock = Object()
        private val pending = mutableListOf<Event>()

        private fun record(event: Event) = synchronized(lock) {
            pending += event
            lock.notifyAll()
        }

        override fun onStateChanged(state: SimHubState) = record(Event.StateChanged(state))
        override fun onLinkUp(state: SimHubState) = record(Event.LinkUp(state))
        override fun onLinkLost(loss: SimHubLink.LinkLoss) = record(Event.LinkLost(loss))
        override fun onAttemptFailed(loss: SimHubLink.LinkLoss) = record(Event.AttemptFailed(loss))
        override fun onPairResult(result: SimHubMessage.PairResult) = record(Event.PairResultReceived(result))
        override fun onPaired(hostId: String, token: String) = record(Event.Paired(hostId, token))
        override fun onTokenRevoked(hostId: String, code: String) = record(Event.TokenRevoked(hostId, code))
        override fun onCommand(command: SimHubCommand) = record(Event.CommandReceived(command))
        override fun onError(error: SimHubMessage.Error) = record(Event.ErrorReceived(error))

        fun take(timeoutMs: Long, match: (Event) -> Boolean): Event? {
            val deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(timeoutMs)
            synchronized(lock) {
                while (true) {
                    val index = pending.indexOfFirst(match)
                    if (index >= 0) return pending.removeAt(index)
                    val left = TimeUnit.NANOSECONDS.toMillis(deadline - System.nanoTime())
                    if (left <= 0) return null
                    lock.wait(left)
                }
            }
        }

        inline fun <reified T : Event> poll(timeoutMs: Long, crossinline match: (T) -> Boolean = { true }): T? =
            take(timeoutMs) { it is T && match(it) } as T?

        inline fun <reified T : Event> await(timeoutMs: Long = 10_000, crossinline match: (T) -> Boolean = { true }): T =
            poll(timeoutMs, match) ?: throw AssertionError("no ${T::class.java.simpleName} within $timeoutMs ms")

        fun awaitState(timeoutMs: Long = 10_000, match: (SimHubState) -> Boolean): SimHubState =
            await<Event.StateChanged>(timeoutMs) { match(it.state) }.state

        fun drain(): List<Event> = synchronized(lock) { pending.toList().also { pending.clear() } }
    }

    companion object {
        private val IDENTITY = SimHubLink.Identity(
            tabletId = "9b1e4d2c-5a7f-4e3b-8c61-2f0a9d4b7e18",
            name = "Lenovo Tab P11",
            appVersion = "0.3.0",
        )

        /** Short timings so tests run fast; jitter off so delays are exact. */
        private val FAST = SimHubLink.Timing(
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
