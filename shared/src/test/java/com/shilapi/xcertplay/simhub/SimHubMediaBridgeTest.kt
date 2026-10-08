package com.shilapi.xcertplay.simhub

import com.shilapi.xcertplay.airplay.CarPlayMediaButton
import com.shilapi.xcertplay.media.CarPlayNowPlaying
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [29], manifest = Config.NONE)
class SimHubMediaBridgeTest {
    private var now = 1_791_043_200_000L
    private val sink = RecordingStatusSink()
    private val bridge = SimHubMediaBridge(statusSink = { sink }, clock = { now }, log = {})
    private val phone = FakePhone()

    private val teardrop = CarPlayNowPlaying(
        title = "Teardrop",
        album = "Mezzanine",
        artist = "Massive Attack",
        sourceApp = "Spotify",
        durationMillis = 330_000,
        elapsedMillis = 83_400,
        playing = true,
    )

    private fun attach() = bridge.attach(phone, phone, phone::subscribeNowPlaying, phone::subscribePhone)

    @Test fun mediaCommandsPressTheCarPlayButtonsAndSiriOpensSiri() {
        attach()
        assertEquals(SimHubMediaBridge.Result.HANDLED, bridge.onCommand(SimHubCommand.Media(MediaAction.PLAY_PAUSE)))
        assertEquals(SimHubMediaBridge.Result.HANDLED, bridge.onCommand(SimHubCommand.Media(MediaAction.NEXT)))
        assertEquals(SimHubMediaBridge.Result.HANDLED, bridge.onCommand(SimHubCommand.Media(MediaAction.PREVIOUS)))
        assertEquals(SimHubMediaBridge.Result.HANDLED, bridge.onCommand(SimHubCommand.Media(MediaAction.SIRI)))
        assertEquals(listOf("button ${CarPlayMediaButton.PLAY_PAUSE}", "button ${CarPlayMediaButton.NEXT}", "button ${CarPlayMediaButton.PREVIOUS}", "siri"), phone.calls)
    }

    @Test fun commandsWithoutAPhoneAreUnavailable() {
        assertEquals(SimHubMediaBridge.Result.UNAVAILABLE, bridge.onCommand(SimHubCommand.Media(MediaAction.NEXT)))
        attach()
        phone.accepts = false
        assertEquals(SimHubMediaBridge.Result.UNAVAILABLE, bridge.onCommand(SimHubCommand.Media(MediaAction.SIRI)))
        assertEquals(SimHubMediaBridge.Result.NOT_MEDIA, bridge.onCommand(SimHubCommand.ShowDashboard))
    }

    @Test fun nowPlayingIsSentWithAPositionThePluginCanExtrapolate() {
        attach()
        assertNull(sink.nowPlaying.single())
        phone.publish(teardrop)
        assertEquals(
            NowPlaying("Teardrop", "Massive Attack", "Mezzanine", "Spotify", playing = true, position = 83.4, duration = 330.0, updatedAt = now),
            sink.nowPlaying.last(),
        )

        // Title change 10 s later without a new elapsed time: the position moved on with playback.
        now += 10_000
        phone.publish(teardrop.copy(title = "Teardrop (Live)"))
        val moved = sink.nowPlaying.last()!!
        assertEquals(93.4, moved.position, 0.001)
        assertEquals(now, moved.updatedAt)

        // Pause 5 s later: the position freezes where playback got to.
        now += 5_000
        phone.publish(teardrop.copy(title = "Teardrop (Live)", playing = false))
        val paused = sink.nowPlaying.last()!!
        assertEquals(98.4, paused.position, 0.001)
        now += 60_000
        phone.publish(teardrop.copy(title = "Teardrop (Live)", playing = false, artworkTransferId = 3))
        assertEquals(paused, sink.nowPlaying.last())

        // Seek: the new elapsed time is the position.
        phone.publish(teardrop.copy(title = "Teardrop (Live)", playing = true, elapsedMillis = 10_000))
        assertEquals(10.0, sink.nowPlaying.last()!!.position, 0.001)
    }

    @Test fun artworkOnlyChangesAreNotSent() {
        attach()
        phone.publish(teardrop)
        val sent = sink.nowPlaying.size
        phone.publish(teardrop.copy(artworkTransferId = 7))
        assertEquals(sent, sink.nowPlaying.size)
    }

    @Test fun positionIsClampedToTheDurationAndLiveStreamsHaveNone() {
        attach()
        phone.publish(teardrop.copy(elapsedMillis = 329_000))
        now += 10_000
        phone.publish(teardrop.copy(elapsedMillis = 329_000, title = "x"))
        assertEquals(330.0, sink.nowPlaying.last()!!.position, 0.001)
        phone.publish(CarPlayNowPlaying(title = "Radio", durationMillis = 0, playing = true))
        assertNull(sink.nowPlaying.last()!!.duration)
    }

    @Test fun phoneConnectionAndEndOfSessionReachTheStatus() {
        attach()
        phone.connect(true, "Tim's iPhone")
        phone.publish(teardrop)
        assertEquals(true to "Tim's iPhone", sink.phone.last())
        // The controller clears the metadata when the session ends.
        phone.publish(CarPlayNowPlaying())
        phone.connect(false, null)
        assertNull(sink.nowPlaying.last())
        assertEquals(false to null, sink.phone.last())
    }

    @Test fun detachClearsTheStatusAndIgnoresTheOldController() {
        attach()
        phone.connect(true, "Tim's iPhone")
        phone.publish(teardrop)
        bridge.detach(phone)
        assertNull(sink.nowPlaying.last())
        assertEquals(false to null, sink.phone.last())
        assertTrue(phone.observers.isEmpty())
        assertEquals(SimHubMediaBridge.Result.UNAVAILABLE, bridge.onCommand(SimHubCommand.Media(MediaAction.NEXT)))
        bridge.detach(FakePhone())
    }

    @Test fun republishSendsTheCurrentStateToANewSink() {
        attach()
        phone.connect(true, "Tim's iPhone")
        phone.publish(teardrop)
        sink.nowPlaying.clear()
        sink.phone.clear()
        now += 2_000
        bridge.republish()
        assertEquals(85.4, sink.nowPlaying.single()!!.position, 0.001)
        assertEquals(listOf(true to "Tim's iPhone"), sink.phone)
    }

    private class RecordingStatusSink : SimHubStatusSink {
        val nowPlaying = mutableListOf<NowPlaying?>()
        val phone = mutableListOf<Pair<Boolean, String?>>()
        override fun updateNowPlaying(nowPlaying: NowPlaying?) { this.nowPlaying += nowPlaying }
        override fun updatePhone(connected: Boolean, phoneName: String?) { phone += connected to phoneName }
    }

    private class FakePhone : CarPlayMediaRemote {
        val calls = mutableListOf<String>()
        var accepts = true
        val observers = mutableListOf<(CarPlayNowPlaying) -> Unit>()
        private val phoneObservers = mutableListOf<(Boolean, String?) -> Unit>()
        private var current = CarPlayNowPlaying()

        override fun sendMediaButton(index: Int): Boolean { calls += "button $index"; return accepts }
        override fun requestSiri(): Boolean { calls += "siri"; return accepts }

        fun subscribeNowPlaying(observer: (CarPlayNowPlaying) -> Unit): AutoCloseable {
            observers += observer
            observer(current)
            return AutoCloseable { observers -= observer }
        }

        fun subscribePhone(observer: (Boolean, String?) -> Unit): AutoCloseable {
            phoneObservers += observer
            return AutoCloseable { phoneObservers -= observer }
        }

        fun publish(next: CarPlayNowPlaying) {
            current = next
            observers.toList().forEach { it(next) }
        }

        fun connect(connected: Boolean, name: String?) = phoneObservers.toList().forEach { it(connected, name) }
    }
}
