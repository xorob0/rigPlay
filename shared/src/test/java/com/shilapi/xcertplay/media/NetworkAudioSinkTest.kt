package com.shilapi.xcertplay.media

import com.shilapi.xcertplay.airplay.AudioCodecKind
import com.shilapi.xcertplay.airplay.AudioFormat
import com.shilapi.xcertplay.airplay.AudioStreamId
import com.shilapi.xcertplay.media.FakeAudioTransport.Event
import com.shilapi.xcertplay.simhub.AudioFormat as WireFormat
import com.shilapi.xcertplay.simhub.AudioStream
import com.shilapi.xcertplay.simhub.SimHubDiscovery
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [29], manifest = Config.NONE)
class NetworkAudioSinkTest {
    private val transport = FakeAudioTransport()
    private var sink = NetworkAudioSink(transport = { transport }, log = {})

    @After fun tearDown() = sink.close()

    private val music = AudioStreamId(100, "media")
    private val musicFormat = AudioFormat(AudioCodecKind.LPCM, 48_000, 2, 100, "media")

    /** One RTP packet of big-endian LPCM whose samples count up from [first]. */
    private fun rtp(frames: Int, channels: Int, first: Int = 0): ByteArray {
        val out = ByteArray(12 + frames * channels * 2)
        for (i in 0 until frames * channels) {
            val value = first + i
            out[12 + 2 * i] = (value shr 8).toByte()
            out[12 + 2 * i + 1] = value.toByte()
        }
        return out
    }

    private fun littleEndian(rtp: ByteArray): ByteArray =
        PcmConversion.toS16le(rtp, 12, rtp.size - 12, PcmEncoding.S16BE)

    @Test fun lpcmMusicIsAnnouncedPacketizedAndStopped() {
        sink.onAudioStarted(music, musicFormat, 0)
        val first = rtp(352, 2)
        val second = rtp(352, 2, first = 704)
        sink.onAudioRtp(music, musicFormat, first, 0)
        sink.onAudioRtp(music, musicFormat, second, 352)
        sink.onAudioStopped(music)

        val events = transport.drain()
        assertEquals(Event.Start(AudioStream.MEDIA, 48_000, 2), events.first())
        assertEquals(Event.Stop(AudioStream.MEDIA), events.last())
        val datagrams = events.filterIsInstance<Event.Datagram>().map { it.datagram }
        // 704 frames: two full 240-frame datagrams and a 224-frame tail flushed at the stop.
        assertEquals(listOf(960, 960, 896), datagrams.map { it.payload.size })
        assertEquals(listOf(0, 1, 2), datagrams.map { it.header.seq })
        assertEquals(listOf(0L, 240L, 480L), datagrams.map { it.header.timestamp })
        assertEquals(listOf(true, false, false), datagrams.map { it.header.start })
        assertArrayEquals(littleEndian(first) + littleEndian(second), datagrams.fold(ByteArray(0)) { acc, d -> acc + d.payload })
        assertEquals(3L, sink.stats.datagramsSent)
    }

    @Test fun streamsMapToTheProtocolStreams() {
        assertEquals(AudioStream.TELEPHONY, NetworkAudioSink.preferredStream(AudioStreamId(100, "telephony")))
        assertEquals(AudioStream.ALT, NetworkAudioSink.preferredStream(AudioStreamId(100, "speechrecognition")))
        assertEquals(AudioStream.ALT, NetworkAudioSink.preferredStream(AudioStreamId(101, "default")))
        assertEquals(AudioStream.ALT, NetworkAudioSink.preferredStream(AudioStreamId(100, "alert")))
        assertEquals(AudioStream.MEDIA, NetworkAudioSink.preferredStream(AudioStreamId(100, "media")))
        assertEquals(AudioStream.MEDIA, NetworkAudioSink.preferredStream(AudioStreamId(102, "unknown")))
    }

    @Test fun siriAtSixteenKiloHertzMonoGoesOnAlt() {
        val siri = AudioStreamId(100, "speechrecognition")
        val format = AudioFormat(AudioCodecKind.LPCM, 16_000, 1, 100, "speechrecognition")
        sink.onAudioRtp(siri, format, rtp(160, 1), 0)
        assertEquals(Event.Start(AudioStream.ALT, 16_000, 1), transport.await<Event.Start>())
        val datagram = transport.await<Event.Datagram>().datagram
        assertEquals(160, datagram.payload.size)
        assertEquals(AudioStream.ALT, datagram.header.stream)
    }

    @Test fun aBusyProtocolStreamIsBorrowedFromAFreeOne() {
        val guidance = AudioStreamId(101, "default")
        val siri = AudioStreamId(100, "speechrecognition")
        sink.onAudioStarted(guidance, AudioFormat(AudioCodecKind.LPCM, 24_000, 1, 101, "default"), 0)
        sink.onAudioStarted(siri, AudioFormat(AudioCodecKind.LPCM, 24_000, 1, 100, "speechrecognition"), 0)
        assertEquals(mapOf(guidance to AudioStream.ALT, siri to AudioStream.MEDIA), sink.assignments)
    }

    @Test fun oddRatesAreResampledToAWireRate() {
        val format = AudioFormat(AudioCodecKind.LPCM, 22_050, 1, 100, "telephony")
        val call = AudioStreamId(100, "telephony")
        sink.onAudioRtp(call, format, rtp(2205, 1), 0)
        assertEquals(Event.Start(AudioStream.TELEPHONY, 24_000, 1), transport.await<Event.Start>())
        assertEquals(24_000, transport.await<Event.Datagram>().datagram.header.sampleRateHz)
    }

    @Test fun formatChangeFlushesAndAnnouncesAgain() {
        val decoder = ScriptedDecoder()
        sink = NetworkAudioSink(transport = { transport }, decoderFactory = { decoder }, log = {})
        decoder.next = PcmChunkFormat(44_100, 2)
        sink.onAudioRtp(music, musicFormat, ByteArray(12 + 300 * 4), 0)
        assertEquals(Event.Start(AudioStream.MEDIA, 44_100, 2), transport.await<Event.Start>())
        // 300 frames at 44.1 kHz: one 220-frame datagram now, the 80-frame tail before the restart.
        assertEquals(880, transport.await<Event.Datagram>().datagram.payload.size)
        decoder.next = PcmChunkFormat(48_000, 2)
        sink.onAudioRtp(music, musicFormat, ByteArray(12 + 240 * 4), 0)

        val events = transport.drain()
        assertEquals(320, (events[0] as Event.Datagram).datagram.payload.size)
        assertEquals(Event.Start(AudioStream.MEDIA, 48_000, 2), events[1])
        val restarted = (events[2] as Event.Datagram).datagram.header
        assertEquals(0, restarted.seq)
        assertEquals(0L, restarted.timestamp)
        assertTrue(restarted.start)
    }

    @Test fun aReconnectAnnouncesTheStreamAgainFromZero() {
        sink.onAudioRtp(music, musicFormat, rtp(480, 2), 0)
        transport.await<Event.Start>()
        transport.drain()
        transport.epoch = 2
        sink.onAudioRtp(music, musicFormat, rtp(240, 2), 480)
        assertEquals(Event.Start(AudioStream.MEDIA, 48_000, 2), transport.await<Event.Start>())
        val header = transport.await<Event.Datagram>().datagram.header
        assertEquals(0, header.seq)
        assertTrue(header.start)
    }

    @Test fun withoutATargetNothingIsSentAndDropsAreCounted() {
        transport.target = null
        sink.onAudioRtp(music, musicFormat, rtp(480, 2), 0)
        sink.onAudioStopped(music)
        assertEquals(emptyList<Event>(), transport.drain())
        assertTrue(sink.stats.noTargetDrops >= 1)
    }

    @Test fun anIdleStreamIsStoppedAndRestartsWithTheNextPacket() {
        sink = NetworkAudioSink(transport = { transport }, idleStopMillis = 100, log = {})
        sink.onAudioRtp(music, musicFormat, rtp(240, 2), 0)
        transport.await<Event.Start>()
        transport.await<Event.Stop>()
        sink.onAudioRtp(music, musicFormat, rtp(240, 2), 240)
        assertEquals(Event.Start(AudioStream.MEDIA, 48_000, 2), transport.await<Event.Start>())
        assertTrue(transport.await<Event.Datagram>().datagram.header.start)
    }

    @Test fun queueOverflowDropsTheOldestPackets() {
        val gate = CountDownLatch(1)
        val decoder = ScriptedDecoder(gate)
        sink = NetworkAudioSink(transport = { transport }, decoderFactory = { decoder }, queueCapacity = 4, log = {})
        fun packet(index: Int) = sink.onAudioRtp(music, musicFormat, ByteArray(12 + 240 * 4) { index.toByte() }, index * 240)
        // The first packet blocks the sender in the decoder; ten more find a queue of four.
        packet(0)
        assertTrue(decoder.entered.await(3, TimeUnit.SECONDS))
        for (index in 1..10) packet(index)
        assertEquals(6L, sink.stats.queueDrops)
        gate.countDown()
        val datagrams = List(5) { transport.await<Event.Datagram>() }
        // The blocked packet and the four newest arrive, in order.
        assertEquals(listOf(0, 7, 8, 9, 10), datagrams.map { it.datagram.payload[0].toInt() })
        assertEquals(listOf(0, 1, 2, 3, 4), datagrams.map { it.datagram.header.seq })
    }

    @Test fun stoppingAStreamWhileAnotherTakesItsProtocolStreamKeepsStopBeforeStart() {
        val alt = AudioStreamId(101, "default")
        val format = AudioFormat(AudioCodecKind.LPCM, 24_000, 1, 101, "default")
        sink.onAudioRtp(alt, format, rtp(240, 1), 0)
        transport.await<Event.Datagram>()
        sink.onAudioStopped(alt)
        val siri = AudioStreamId(100, "speechrecognition")
        sink.onAudioRtp(siri, AudioFormat(AudioCodecKind.LPCM, 16_000, 1, 100, "speechrecognition"), rtp(160, 1), 0)
        val events = transport.drain().filter { it !is Event.Datagram }
        assertEquals(listOf(Event.Stop(AudioStream.ALT), Event.Start(AudioStream.ALT, 16_000, 1)), events)
    }

    @Test fun aGapInTheSourceClockBecomesATimestampJumpNotShorterAudio() {
        sink.onAudioStarted(music, musicFormat, 0)
        sink.onAudioRtp(music, musicFormat, rtp(352, 2), 0)
        // The packet at sample 352 never arrived (lost between phone and tablet, or dropped from a full queue).
        sink.onAudioRtp(music, musicFormat, rtp(352, 2, first = 1408), 704)
        sink.onAudioStopped(music)

        val datagrams = transport.drain().filterIsInstance<Event.Datagram>().map { it.datagram }
        // The 112-frame tail of the first packet goes out before the gap; the second packet starts at 704.
        assertEquals(listOf(240, 112, 240, 112), datagrams.map { it.frames })
        assertEquals(listOf(0L, 240L, 704L, 944L), datagrams.map { it.header.timestamp })
        assertEquals(listOf(0, 1, 2, 3), datagrams.map { it.header.seq })
        assertEquals(1L, sink.stats.timestampSkips)
    }

    @Test fun reorderedAndRepeatedPacketsAreNotGaps() {
        sink.onAudioRtp(music, musicFormat, rtp(240, 2), 480)
        sink.onAudioRtp(music, musicFormat, rtp(240, 2), 240) // late
        sink.onAudioRtp(music, musicFormat, rtp(240, 2), 240) // duplicate
        sink.onAudioRtp(music, musicFormat, rtp(240, 2), 480)
        sink.onAudioStopped(music)
        val datagrams = transport.drain().filterIsInstance<Event.Datagram>().map { it.datagram }
        assertEquals(listOf(0L, 240L, 480L, 720L), datagrams.map { it.header.timestamp })
        assertEquals(0L, sink.stats.timestampSkips)
    }

    @Test fun aPacketTheDecoderRefusesIsSentAsSilenceOnThePc() {
        val decoder = ScriptedDecoder().apply { refuse = setOf(1) }
        sink = NetworkAudioSink(transport = { transport }, decoderFactory = { decoder }, log = {})
        val aac = AudioFormat(AudioCodecKind.AAC_LC, 48_000, 2, 96, "media")
        // Three AAC packets of 1024 frames; the decoder refuses the second one.
        for (index in 0 until 3) sink.onAudioRtp(music, aac, ByteArray(12 + 1024 * 4) { index.toByte() }, index * 1024)
        sink.onAudioStopped(music)

        val datagrams = transport.drain().filterIsInstance<Event.Datagram>().map { it.datagram }
        assertEquals(listOf(0L, 240L, 480L, 720L, 960L, 2048L, 2288L, 2528L, 2768L, 3008L), datagrams.map { it.header.timestamp })
        assertEquals(listOf(240, 240, 240, 240, 64, 240, 240, 240, 240, 64), datagrams.map { it.frames })
        assertEquals(1L, sink.stats.decoderDrops)
        assertEquals(1L, sink.stats.timestampSkips)
    }

    @Test fun opusIsSentWhenThePluginPrefersIt() {
        val encoders = CopyOnWriteArrayList<FakeOpusEncoder>()
        transport.format = WireFormat.OPUS
        sink = NetworkAudioSink(transport = { transport }, opusEncoderFactory = { rate, ch -> FakeOpusEncoder(rate, ch).also { encoders += it } }, log = {})
        assertEquals(WireFormat.OPUS, sink.wireFormat)
        sink.onAudioRtp(music, musicFormat, rtp(960, 2), 0)
        sink.onAudioRtp(music, musicFormat, rtp(960, 2, first = 1920), 960)
        sink.onAudioStopped(music)

        val events = transport.drain()
        assertEquals(Event.Start(AudioStream.MEDIA, 48_000, 2, WireFormat.OPUS), events.first())
        assertEquals(Event.Stop(AudioStream.MEDIA), events.last())
        val datagrams = events.filterIsInstance<Event.Datagram>().map { it.datagram }
        assertEquals(2, datagrams.size)
        assertTrue(datagrams.all { it.header.format == WireFormat.OPUS })
        assertEquals(listOf(0L, 960L), datagrams.map { it.header.timestamp })
        assertEquals(listOf(960, 960), datagrams.map { it.frames })
        assertEquals(1, encoders.size)
        assertEquals(48_000, encoders[0].sampleRate)
        assertTrue(encoders[0].closed)
        assertEquals(0L, sink.stats.opusFallbacks)
    }

    @Test fun fortyFourKiloHertzIsResampledToFortyEightForOpus() {
        transport.format = WireFormat.OPUS
        sink = NetworkAudioSink(transport = { transport }, opusEncoderFactory = { rate, ch -> FakeOpusEncoder(rate, ch) }, log = {})
        val cd = AudioFormat(AudioCodecKind.LPCM, 44_100, 2, 100, "media")
        sink.onAudioRtp(music, cd, rtp(882, 2), 0)
        sink.onAudioRtp(music, cd, rtp(882, 2), 882)
        assertEquals(Event.Start(AudioStream.MEDIA, 48_000, 2, WireFormat.OPUS), transport.await<Event.Start>())
        assertEquals(48_000, transport.await<Event.Datagram>().datagram.header.sampleRateHz)

        // Mono 16 kHz is a rate Opus takes: it stays as it is.
        val siri = AudioStreamId(100, "speechrecognition")
        sink.onAudioRtp(siri, AudioFormat(AudioCodecKind.LPCM, 16_000, 1, 100, "speechrecognition"), rtp(640, 1), 0)
        assertEquals(Event.Start(AudioStream.ALT, 16_000, 1, WireFormat.OPUS), transport.await<Event.Start>())
        assertEquals(320, transport.await<Event.Datagram>().datagram.frames)
    }

    @Test fun withoutAnOpusEncoderTheSinkFallsBackToPcm() {
        transport.format = WireFormat.OPUS
        sink = NetworkAudioSink(transport = { transport }, opusEncoderFactory = { _, _ -> throw IllegalStateException("no codec") }, log = {})
        val cd = AudioFormat(AudioCodecKind.LPCM, 44_100, 2, 100, "media")
        sink.onAudioRtp(music, cd, rtp(441, 2), 0) // this chunk is lost to the failed encoder
        sink.onAudioRtp(music, cd, rtp(441, 2), 441)
        sink.onAudioRtp(music, cd, rtp(441, 2), 882)
        sink.onAudioStopped(music)

        val events = transport.drain()
        // PCM at the source rate, not the Opus rate: 44.1 kHz needs no resampling for PCM.
        assertEquals(listOf(Event.Start(AudioStream.MEDIA, 44_100, 2, WireFormat.PCM_S16LE)), events.filterIsInstance<Event.Start>())
        assertTrue(events.filterIsInstance<Event.Datagram>().all { it.datagram.header.format == WireFormat.PCM_S16LE })
        assertEquals(1L, sink.stats.opusFallbacks)
        assertEquals(WireFormat.PCM_S16LE, sink.wireFormat)
    }

    @Test fun aChangedPreferenceRestartsTheStreamInTheNewFormat() {
        sink = NetworkAudioSink(transport = { transport }, opusEncoderFactory = { rate, ch -> FakeOpusEncoder(rate, ch) }, log = {})
        sink.onAudioRtp(music, musicFormat, rtp(240, 2), 0)
        assertEquals(Event.Start(AudioStream.MEDIA, 48_000, 2, WireFormat.PCM_S16LE), transport.await<Event.Start>())
        assertEquals(WireFormat.PCM_S16LE, transport.await<Event.Datagram>().datagram.header.format)

        transport.format = WireFormat.OPUS // the plugin's Opus setting went on: a new state
        sink.onAudioRtp(music, musicFormat, rtp(1000, 2), 240)
        assertEquals(Event.Start(AudioStream.MEDIA, 48_000, 2, WireFormat.OPUS), transport.await<Event.Start>())
        val opus = transport.await<Event.Datagram>().datagram
        assertEquals(WireFormat.OPUS, opus.header.format)
        assertTrue(opus.header.start)
        assertEquals(0L, opus.header.timestamp)
    }

    @Test fun theWifiLockIsHeldWhileAStreamIsAnnounced() {
        val events = CopyOnWriteArrayList<String>()
        val lock = object : SimHubDiscovery.NetworkLock {
            override fun acquire() { events += "acquire" }
            override fun release() { events += "release" }
        }
        // A long idle stop: on a loaded CI box the 3 s default can end the one-datagram music stream before the
        // second stream starts, which releases and re-acquires the lock and is not what this test is about.
        sink = NetworkAudioSink(transport = { transport }, wifiLock = lock, idleStopMillis = 60_000L, log = {})
        sink.onAudioStarted(music, musicFormat, 0)
        assertEquals(emptyList<String>(), events) // nothing announced before the first PCM
        sink.onAudioRtp(music, musicFormat, rtp(240, 2), 0)
        transport.await<Event.Start>()
        awaitUntil { events.size == 1 }
        assertEquals(listOf("acquire"), events)
        assertTrue(sink.streaming)

        // A second stream shares the lock; it is released when the last one ends.
        val siri = AudioStreamId(100, "speechrecognition")
        sink.onAudioRtp(siri, AudioFormat(AudioCodecKind.LPCM, 16_000, 1, 100, "speechrecognition"), rtp(160, 1), 0)
        transport.await<Event.Start> { it.stream == AudioStream.ALT }
        sink.onAudioStopped(music)
        transport.await<Event.Stop> { it.stream == AudioStream.MEDIA }
        assertEquals(listOf("acquire"), events)
        sink.onAudioStopped(siri)
        transport.await<Event.Stop> { it.stream == AudioStream.ALT }
        awaitUntil { events.size == 2 }
        assertEquals(listOf("acquire", "release"), events)
        assertFalse(sink.streaming)
    }

    private fun awaitUntil(timeoutMs: Long = 3_000, condition: () -> Boolean) {
        val deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(timeoutMs)
        while (!condition()) {
            if (System.nanoTime() > deadline) throw AssertionError("condition not met within $timeoutMs ms")
            Thread.sleep(5)
        }
    }

    /**
     * Echoes each packet's payload as s16le PCM in the [next] format; the first decode waits for [gate], and
     * the packets whose index is in [refuse] are refused.
     */
    private class ScriptedDecoder(private val gate: CountDownLatch? = null) : PcmDecoder {
        @Volatile var next = PcmChunkFormat(48_000, 2)
        @Volatile var refuse = emptySet<Int>()
        val entered = CountDownLatch(1)
        private var first = true
        private var index = 0

        override fun decode(rtp: ByteArray, sample: Int, out: PcmSink): Boolean {
            if (first) {
                first = false
                entered.countDown()
                gate?.await(5, TimeUnit.SECONDS)
            }
            if (index++ in refuse) return false
            out.onPcm(rtp, 12, rtp.size - 12, next)
            return true
        }
    }
}
