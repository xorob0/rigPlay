package com.shilapi.xcertplay.media

import com.shilapi.xcertplay.airplay.MicrophoneConfig
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.util.concurrent.atomic.AtomicInteger
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/** [MicrophoneUplink] fed by a PC source instead of `AudioRecord` (#34). */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [29], manifest = Config.NONE)
class MicrophoneUplinkPcSourceTest {
    private val phone = DatagramSocket(InetSocketAddress(InetAddress.getLoopbackAddress(), 0)).apply { soTimeout = 3_000 }
    private var uplink: MicrophoneUplink? = null

    @After fun tearDown() {
        uplink?.close()
        phone.close()
    }

    private class FakeSource(private val startOk: Boolean = true) : MicrophonePcmSource {
        val reads = AtomicInteger()
        val readSizes = mutableListOf<Int>()
        @Volatile var started = false
        @Volatile var closed = false

        override val description = "pc test"

        override fun start(): Boolean {
            started = true
            return startOk
        }

        override fun read(buffer: ByteArray, offset: Int, length: Int): Int {
            if (closed) return -1
            synchronized(readSizes) { readSizes += length }
            reads.incrementAndGet()
            Thread.sleep(5)
            return length
        }

        override fun close() {
            closed = true
        }
    }

    private fun config() = MicrophoneConfig(
        audioType = "speechrecognition", sampleRate = 16_000, channels = 1, payloadType = 100, frameMillis = 20,
        host = InetAddress.getLoopbackAddress(), port = phone.localPort, key = ByteArray(32),
    )

    @Test fun thePcSourceFeedsThePhoneOneFrameAtATimeAndIsClosedWithTheUplink() {
        val source = FakeSource()
        val next = MicrophoneUplink(config(), pcSource = { source }).also { uplink = it }
        assertTrue(next.start())
        assertTrue(source.started)

        val packet = DatagramPacket(ByteArray(2048), 2048)
        phone.receive(packet)
        // 20 ms of 16 kHz mono PCM, sealed: RTP header + body + Poly1305 tag + nonce.
        assertEquals(12 + 640 + 16 + 8, packet.length)
        assertEquals(640, synchronized(source.readSizes) { source.readSizes.first() })

        next.close()
        assertTrue(source.closed)
    }

    @Test fun aPcSourceThatCannotStartFallsBackToTheTabletMicrophone() {
        val source = FakeSource(startOk = false)
        val next = MicrophoneUplink(config(), pcSource = { source }).also { uplink = it }
        next.start()
        assertTrue(source.started)
        assertTrue(source.closed)
        assertEquals(0, source.reads.get())
    }
}
