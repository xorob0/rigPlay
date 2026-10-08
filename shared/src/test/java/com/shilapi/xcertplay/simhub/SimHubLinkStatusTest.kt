package com.shilapi.xcertplay.simhub

import com.shilapi.xcertplay.airplay.CarPlayMediaButton
import com.shilapi.xcertplay.media.CarPlayNowPlaying
import java.util.Random
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/** The plugin's view: commands in, `status` out, through a real [SimHubLink]. */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [29], manifest = Config.NONE)
class SimHubLinkStatusTest {
    private lateinit var server: FakeSimHubServer
    private lateinit var link: SimHubLink
    private lateinit var status: SimHubLinkStatus
    private lateinit var bridge: SimHubMediaBridge
    private val presses = LinkedBlockingQueue<String>()
    private val observers = CopyOnWriteArrayList<(CarPlayNowPlaying) -> Unit>()
    private val phoneObservers = CopyOnWriteArrayList<(Boolean, String?) -> Unit>()

    @Before fun setUp() {
        server = FakeSimHubServer().start()
        val listener = object : SimHubLink.Listener {
            override fun onCommand(command: SimHubCommand) {
                if (bridge.onCommand(command) == SimHubMediaBridge.Result.UNAVAILABLE) link.sendCommandUnavailable()
            }
        }
        link = SimHubLink(IDENTITY, listener, timing = FAST, random = Random(1), log = {})
        status = SimHubLinkStatus(link)
        bridge = SimHubMediaBridge(statusSink = { status }, log = {})
        link.start(SimHubLink.Target("127.0.0.1", server.port, FakeSimHubServer.HOST_ID, FakeSimHubServer.TOKEN))
    }

    @After fun tearDown() {
        link.stop()
        server.stop()
    }

    private fun attachPhone() {
        val remote = object : CarPlayMediaRemote {
            override fun sendMediaButton(index: Int) = presses.add("button $index")
            override fun requestSiri() = presses.add("siri")
        }
        bridge.attach(
            owner = remote,
            remote = remote,
            subscribeNowPlaying = { observer -> observers += observer; observer(CarPlayNowPlaying()); AutoCloseable { observers -= observer } },
            subscribePhone = { observer -> phoneObservers += observer; AutoCloseable { phoneObservers -= observer } },
        )
    }

    @Test fun nextTrackFromTheWheelSkipsOnThePhoneAndTheTitleReachesThePlugin() {
        attachPhone()
        server.await<SimHubMessage.Status>() ?: throw AssertionError("no initial status")
        phoneObservers.forEach { it(true, "Tim's iPhone") }
        observers.forEach {
            it(CarPlayNowPlaying(title = "Teardrop", artist = "Massive Attack", album = "Mezzanine", sourceApp = "Spotify", durationMillis = 330_000, elapsedMillis = 83_400, playing = true))
        }

        val sent = server.await<SimHubMessage.Status> { it.nowPlaying?.title == "Teardrop" }
        assertNotNull(sent)
        assertEquals(true, sent!!.phoneConnected)
        assertEquals("Tim's iPhone", sent.phoneName)
        assertEquals(83.4, sent.nowPlaying!!.position, 0.5)
        assertEquals(330.0, sent.nowPlaying!!.duration!!, 0.0)

        server.send(SimHubMessage.Command(SimHubCommand.Media(MediaAction.NEXT)))
        assertEquals("button ${CarPlayMediaButton.NEXT}", presses.poll(3, TimeUnit.SECONDS))
        server.send(SimHubMessage.Command(SimHubCommand.Media(MediaAction.SIRI)))
        assertEquals("siri", presses.poll(3, TimeUnit.SECONDS))
    }

    @Test fun mediaCommandsWithoutAPhoneAreAnsweredCommandUnavailable() {
        server.await<SimHubMessage.Status>() ?: throw AssertionError("not paired")
        server.send(SimHubMessage.Command(SimHubCommand.Media(MediaAction.PLAY_PAUSE)))
        val error = server.await<SimHubMessage.Error>()
        assertEquals(SimHubProtocol.ERROR_COMMAND_UNAVAILABLE, error?.code)
    }

    @Test fun phoneDisconnectClearsNowPlaying() {
        attachPhone()
        phoneObservers.forEach { it(true, "Tim's iPhone") }
        observers.forEach { it(CarPlayNowPlaying(title = "Teardrop", playing = true)) }
        server.await<SimHubMessage.Status> { it.nowPlaying?.title == "Teardrop" } ?: throw AssertionError("no title")
        bridge.detach(Any())
        phoneObservers.forEach { it(false, null) }
        val idle = server.await<SimHubMessage.Status> { !it.phoneConnected }
        assertNotNull(idle)
        assertNull(idle!!.nowPlaying)
        assertNull(idle.phoneName)
    }

    private companion object {
        val IDENTITY = SimHubLink.Identity(tabletId = "tablet-status-test", name = "Tab", appVersion = "0.3.0")
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

/**
 * Test sink: keeps the whole `status` (§6.7) and hands every change to [SimHubLink.send], so the
 * bridge → link → plugin path can be checked end to end. Not used by the app, where
 * `RigSessionLifecycle.publishStatus` is the single `status` sender.
 */
private class SimHubLinkStatus(private val link: SimHubLink) : SimHubStatusSink {
    private var status: SimHubMessage.Status = SimHubMessage.Status.IDLE

    val current: SimHubMessage.Status get() = synchronized(this) { status }

    override fun updateNowPlaying(nowPlaying: NowPlaying?) = update { it.copy(nowPlaying = nowPlaying) }

    override fun updatePhone(connected: Boolean, phoneName: String?) = update {
        it.copy(
            phoneConnected = connected,
            phoneName = if (connected) phoneName else null,
            nowPlaying = if (connected) it.nowPlaying else null,
        )
    }

    fun updateScreen(screen: Screen) = update { it.copy(screen = screen) }

    private inline fun update(change: (SimHubMessage.Status) -> SimHubMessage.Status) {
        synchronized(this) {
            val next = change(status)
            if (next == status) return
            status = next
            link.send(next)
        }
    }
}
