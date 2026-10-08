package com.shilapi.xcertplay.media

import com.shilapi.xcertplay.airplay.AudioCodecKind
import com.shilapi.xcertplay.airplay.MicrophoneConfig
import com.shilapi.xcertplay.simhub.AudioHeader
import com.shilapi.xcertplay.simhub.AudioStream
import com.shilapi.xcertplay.simhub.SimHubAudioCodec
import com.shilapi.xcertplay.simhub.SimHubEndpoints
import com.shilapi.xcertplay.simhub.SimHubMicTransport
import com.shilapi.xcertplay.simhub.SimHubProtocol
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.CopyOnWriteArrayList
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/** A paired PC as the microphone source sees it; records the messages it was asked to send. */
internal class FakeMicTransport(
    @Volatile var host: InetAddress? = InetAddress.getLoopbackAddress(),
    @Volatile override var micAvailable: Boolean = true,
) : SimHubMicTransport {
    val events = CopyOnWriteArrayList<String>()
    @Volatile override var micEpoch: Long = 1
    @Volatile var accept = true

    override val pairedHost: InetAddress? get() = host

    override fun micStart(sampleRate: Int, port: Int): Boolean {
        events += "micStart $sampleRate $port"
        return accept
    }

    override fun micStop(): Boolean {
        events += "micStop"
        return accept
    }
}

/** The plugin's side of §10.4: 5 ms mono datagrams with seq / timestamp from 0 and the start flag. */
internal class FakeMicSender(private val sampleRate: Int) {
    private var seq = 0
    private var timestamp = 0L
    val framesPerDatagram = sampleRate / 200

    fun next(value: (Int) -> Short = { 1000 }): ByteArray {
        val header = AudioHeader(seq, AudioStream.MIC, seq == 0, timestamp, sampleRate, 1)
        val samples = ShortArray(framesPerDatagram) { value((timestamp + it).toInt()) }
        seq = (seq + 1) and 0xFFFF
        timestamp += framesPerDatagram
        return SimHubAudioCodec.encodeSamples(header, samples)
    }
}

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [29], manifest = Config.NONE)
class NetworkMicrophoneSourceTest {
    private val transport = FakeMicTransport()
    private val lan: InetAddress = InetAddress.getByName("192.168.1.66")
    private var now = 0L
    private val sleeps = mutableListOf<Long>()
    private val sources = mutableListOf<NetworkMicrophoneSource>()

    @After fun tearDown() {
        sources.forEach { it.close() }
        SimHubEndpoints.microphone = null
    }

    private fun source(rate: Int = 16_000, channels: Int = 1, port: Int = 0) = NetworkMicrophoneSource(
        transport, rate, channels, port,
        nanoTime = { now },
        sleepNanos = { sleeps += it; now += it },
        log = {},
    ).also { sources += it }

    private fun samplesOf(bytes: ByteArray, count: Int): ShortArray {
        val shorts = ByteBuffer.wrap(bytes, 0, count).order(ByteOrder.LITTLE_ENDIAN).asShortBuffer()
        return ShortArray(shorts.remaining()).also { shorts.get(it) }
    }

    @Test fun startSendsMicStartWithTheBoundPortAndCloseSendsMicStop() {
        val mic = source(rate = 24_000)
        assertTrue(mic.start())
        assertTrue(mic.boundPort > 0)
        assertEquals(listOf("micStart 24000 ${mic.boundPort}"), transport.events)
        mic.close()
        assertEquals("micStop", transport.events.last())
        assertEquals(-1, mic.read(ByteArray(160), 0, 160))
        mic.close()
        assertEquals(2, transport.events.size)
    }

    @Test fun aRefusedMicStartFallsBackWithoutMicStop() {
        transport.accept = false
        val mic = source()
        assertFalse(mic.start())
        mic.close()
        assertEquals(listOf("micStart 16000 ${mic.boundPort}"), transport.events)
    }

    @Test fun datagramsAreTakenOnlyFromThePairedHostAndInTheRightFormat() {
        val mic = source()
        assertTrue(mic.start())
        val sender = FakeMicSender(16_000)
        val good = sender.next()
        mic.onDatagram(good, good.size, lan)
        assertEquals(1L, mic.rejected)
        mic.onDatagram(good, good.size, null)
        assertEquals(2L, mic.rejected)

        // CarPlay audio never flows PC → tablet; another rate or stereo does not match the micStart.
        val media = SimHubAudioCodec.encodeSamples(AudioHeader(0, AudioStream.MEDIA, true, 0, 16_000, 1), ShortArray(80))
        mic.onDatagram(media, media.size, transport.host)
        val wrongRate = SimHubAudioCodec.encodeSamples(AudioHeader(0, AudioStream.MIC, true, 0, 24_000, 1), ShortArray(120))
        mic.onDatagram(wrongRate, wrongRate.size, transport.host)
        val stereo = SimHubAudioCodec.encodeSamples(AudioHeader(0, AudioStream.MIC, true, 0, 16_000, 2), ShortArray(160))
        mic.onDatagram(stereo, stereo.size, transport.host)
        assertEquals(5L, mic.rejected)
        assertEquals(0L, mic.accepted)

        mic.onDatagram(good, good.size, transport.host)
        assertEquals(1L, mic.accepted)

        // Link down: no paired host, nothing accepted.
        transport.host = null
        val next = sender.next()
        mic.onDatagram(next, next.size, InetAddress.getLoopbackAddress())
        assertEquals(6L, mic.rejected)
    }

    @Test fun readDeliversTheStreamAtTheSampleClockWithSilenceOnUnderrun() {
        val mic = source()
        assertTrue(mic.start())
        val sender = FakeMicSender(16_000)
        val frame = ByteArray(320) // 10 ms at 16 kHz mono

        // Nothing received yet: silence, paced at 10 ms per read.
        assertEquals(320, mic.read(frame, 0, frame.size))
        assertArrayEquals(ShortArray(160), samplesOf(frame, 320))
        assertEquals(10_000_000L, now)

        repeat(10) { val d = sender.next { it.toShort() }; mic.onDatagram(d, d.size, transport.host) } // 50 ms
        assertEquals(320, mic.read(frame, 0, frame.size))
        assertArrayEquals(ShortArray(160) { it.toShort() }, samplesOf(frame, 320))
        assertEquals(20_000_000L, now)
        assertEquals(listOf(10_000_000L, 10_000_000L), sleeps)

        // A slow reader is not made to race: past the deadline, read returns at once.
        now += 15_000_000L
        mic.read(frame, 0, frame.size)
        assertEquals(2, sleeps.size)

        repeat(4) { mic.read(frame, 0, frame.size) }
        assertArrayEquals("drained: silence", ShortArray(160), samplesOf(frame, 320))
        assertTrue(mic.buffer.underruns >= 1)
    }

    @Test fun stereoUplinksGetTheMonoChannelTwice() {
        val mic = source(channels = 2)
        assertTrue(mic.start())
        val sender = FakeMicSender(16_000)
        repeat(8) { val d = sender.next { (it + 7).toShort() }; mic.onDatagram(d, d.size, transport.host) }
        val frame = ByteArray(16)
        assertEquals(16, mic.read(frame, 0, frame.size))
        assertArrayEquals(shortArrayOf(7, 7, 8, 8, 9, 9, 10, 10), samplesOf(frame, 16))
    }

    @Test fun aNewPairedSessionAsksForTheMicrophoneAgain() {
        val mic = source()
        assertTrue(mic.start())
        mic.read(ByteArray(160), 0, 160)
        assertEquals(1, transport.events.size)
        transport.micEpoch = 2
        mic.read(ByteArray(160), 0, 160)
        assertEquals(listOf("micStart 16000 ${mic.boundPort}", "micStart 16000 ${mic.boundPort}"), transport.events)
        mic.read(ByteArray(160), 0, 160)
        assertEquals(2, transport.events.size)
    }

    @Test fun realUdpDatagramsFromThePairedHostReachTheReader() {
        val mic = source()
        assertTrue(mic.start())
        val sender = FakeMicSender(16_000)
        DatagramSocket().use { udp ->
            repeat(10) {
                val d = sender.next { 1234 }
                udp.send(DatagramPacket(d, d.size, InetSocketAddress(InetAddress.getLoopbackAddress(), mic.boundPort)))
            }
            val deadline = System.currentTimeMillis() + 3_000
            while (mic.accepted < 10 && System.currentTimeMillis() < deadline) Thread.sleep(10)
        }
        assertEquals(10L, mic.accepted)
        val frame = ByteArray(160)
        mic.read(frame, 0, frame.size)
        assertArrayEquals(ShortArray(80) { 1234 }, samplesOf(frame, 160))
    }

    @Test fun theDefaultPortIs23713AndABusyPortFallsBack() {
        assertEquals(23_713, SimHubProtocol.MIC_PORT)
        DatagramSocket(null).use { busy ->
            busy.bind(InetSocketAddress(0))
            val mic = source(port = busy.localPort)
            assertTrue(mic.start())
            assertTrue(mic.boundPort != busy.localPort)
        }
    }

    @Test fun theUplinkUsesThePcOnlyWhenTheLinkOffersItAndTheRateFits() {
        val config = MicrophoneConfig(
            audioType = "speechrecognition", sampleRate = 24_000, channels = 1, payloadType = 100, frameMillis = 20,
            host = InetAddress.getLoopbackAddress(), port = 1, key = ByteArray(32), codec = AudioCodecKind.LPCM,
        )
        assertNull("no link owner", NetworkMicrophoneSource.forUplink(config))
        SimHubEndpoints.microphone = transport
        transport.micAvailable = false
        assertNull("the user chose the tablet, or the PC does not offer it", NetworkMicrophoneSource.forUplink(config))
        transport.micAvailable = true
        assertNull("11025 Hz cannot be carried", NetworkMicrophoneSource.forUplink(config.copy(sampleRate = 11_025)))
        assertNotNull(NetworkMicrophoneSource.forUplink(config))
    }

    @Test fun theSettingDefaultsToTheTablet() {
        assertEquals(MicrophoneSource.TABLET, MicrophoneSource.fromKey(null))
        assertEquals(MicrophoneSource.PC, MicrophoneSource.fromKey("pc"))
        assertEquals(MicrophoneSource.TABLET, MicrophoneSource.fromKey("bogus"))
    }
}
