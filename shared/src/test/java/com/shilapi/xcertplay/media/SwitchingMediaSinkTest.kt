package com.shilapi.xcertplay.media

import com.shilapi.xcertplay.airplay.AudioCodecKind
import com.shilapi.xcertplay.airplay.AudioFormat
import com.shilapi.xcertplay.airplay.AudioStreamId
import com.shilapi.xcertplay.airplay.MediaSink
import com.shilapi.xcertplay.airplay.MicrophoneConfig
import com.shilapi.xcertplay.media.FakeAudioTransport.Event
import com.shilapi.xcertplay.simhub.AudioStream
import java.net.InetAddress
import java.net.InetSocketAddress
import java.util.Collections
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [29], manifest = Config.NONE)
class SwitchingMediaSinkTest {
    private val transport = FakeAudioTransport()
    private val local = RecordingSink()
    private val mediaAudio = Collections.synchronizedList(mutableListOf<Boolean>())
    private val network = NetworkAudioSink(transport = { transport }, log = {})
    private val sink = SwitchingMediaSink(local, network, transport = { transport }, onMediaAudioChanged = { mediaAudio.add(it) }, log = {})

    private val music = AudioStreamId(100, "media")
    private val format = AudioFormat(AudioCodecKind.LPCM, 48_000, 2, 100, "media")
    private val packet = ByteArray(12 + 240 * 4)
    private val target = InetSocketAddress(InetAddress.getLoopbackAddress(), 23712)

    @After fun tearDown() = sink.close()

    @Test fun audioGoesToThePcWhileTheLinkTakesIt() {
        sink.onAudioStarted(music, format, 0)
        sink.onAudioRtp(music, format, packet, 0)
        assertEquals(Event.Start(AudioStream.MEDIA, 48_000, 2), transport.await<Event.Start>())
        transport.await<Event.Datagram>()
        assertEquals(emptyList<String>(), local.calls)
        assertEquals(mapOf(music to SwitchingMediaSink.Route.PC), sink.routes)
        assertTrue(sink.routesToPc)
    }

    @Test fun audioPlaysOnTheTabletWithoutALink() {
        transport.target = null
        sink.onAudioStarted(music, format, 7)
        sink.onAudioRtp(music, format, packet, 7)
        sink.onAudioStopped(music)
        assertEquals(listOf("start $music 7", "rtp $music 7", "stop $music"), local.calls)
        assertEquals(emptyList<Event>(), transport.drain(100))
    }

    @Test fun aStreamMovesToTheTabletWhenTheLinkDropsAndBackWhenItReturns() {
        sink.onAudioStarted(music, format, 0)
        sink.onAudioRtp(music, format, packet, 0)
        transport.await<Event.Datagram>()

        // Link lost: the plugin forgot the stream; the tablet takes over at the next packet.
        transport.target = null
        sink.onAudioRtp(music, format, packet, 240)
        assertEquals(listOf("start $music 240", "rtp $music 240"), local.calls)
        assertEquals(SwitchingMediaSink.Route.LOCAL, sink.routes[music])

        // Link back: the tablet stops, the PC gets a fresh audioStart.
        transport.target = target
        transport.epoch++
        transport.drain(100)
        sink.onAudioRtp(music, format, packet, 480)
        assertEquals("stop $music", local.calls.last())
        assertEquals(Event.Start(AudioStream.MEDIA, 48_000, 2), transport.await<Event.Start>())
        assertTrue(transport.await<Event.Datagram>().datagram.header.start)
    }

    @Test fun leavingThePcFlushesAndStopsTheStream() {
        var pcAllowed = true
        val switching = SwitchingMediaSink(local, network, transport = { transport.takeIf { pcAllowed } }, log = {})
        switching.onAudioStarted(music, format, 0)
        switching.onAudioRtp(music, format, ByteArray(12 + 300 * 4), 0)
        transport.await<Event.Datagram>()
        pcAllowed = false
        switching.onAudioRtp(music, format, packet, 300)
        // The 60 buffered frames go out before audioStop; the packet itself plays on the tablet.
        val tail = transport.drain().map { if (it is Event.Datagram) it.datagram.payload.size else it }
        assertEquals(listOf(240, Event.Stop(AudioStream.MEDIA)), tail)
        assertEquals(listOf("start $music 300", "rtp $music 300"), local.calls)
    }

    @Test fun musicActivityIsReportedOnceAcrossARouteChange() {
        sink.onAudioStarted(music, format, 0)
        transport.target = null
        sink.onAudioRtp(music, format, packet, 0)
        transport.target = target
        sink.onAudioRtp(music, format, packet, 240)
        val siri = AudioStreamId(100, "speechrecognition")
        sink.onAudioStarted(siri, AudioFormat(AudioCodecKind.LPCM, 16_000, 1, 100, "speechrecognition"), 0)
        sink.onAudioStopped(siri)
        sink.onAudioStopped(music)
        assertEquals(listOf(true, false), mediaAudio.toList())
    }

    @Test fun videoMicrophoneAndIapAlwaysGoToTheTablet() {
        sink.onScreenStreamActive(110, true)
        sink.onVideoFrame(110, byteArrayOf(1))
        sink.onIapMessage(byteArrayOf(2))
        assertEquals(listOf("screen 110 true", "frame 110", "iap"), local.calls)
        assertFalse(local.calls.any { it.startsWith("mic") })
    }

    private class RecordingSink : MediaSink {
        val calls: MutableList<String> = Collections.synchronizedList(mutableListOf())
        override fun onScreenStreamActive(type: Int, active: Boolean) { calls.add("screen $type $active") }
        override fun onVideoFrame(type: Int, naluBytes: ByteArray) { calls.add("frame $type") }
        override fun onIapMessage(bytes: ByteArray) { calls.add("iap") }
        override fun onAudioStarted(id: AudioStreamId, format: AudioFormat, firstSample: Int) { calls.add("start $id $firstSample") }
        override fun onAudioRtp(id: AudioStreamId, format: AudioFormat, rtp: ByteArray, sample: Int) { calls.add("rtp $id $sample") }
        override fun onAudioStopped(id: AudioStreamId) { calls.add("stop $id") }
        override fun onMicrophoneStarted(id: AudioStreamId, config: MicrophoneConfig) { calls.add("mic start $id") }
    }
}
