package com.shilapi.xcertplay.media

import java.nio.ByteBuffer
import java.nio.ByteOrder
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Test

class PcmConversionTest {
    private fun s16le(vararg samples: Int): ByteArray =
        ByteBuffer.allocate(samples.size * 2).order(ByteOrder.LITTLE_ENDIAN).apply { samples.forEach { putShort(it.toShort()) } }.array()

    private fun samples(pcm: ByteArray): List<Int> {
        val buffer = ByteBuffer.wrap(pcm).order(ByteOrder.LITTLE_ENDIAN).asShortBuffer()
        return List(buffer.remaining()) { buffer.get(it).toInt() }
    }

    @Test fun bigEndianLpcmIsByteSwapped() {
        val be = byteArrayOf(0x12, 0x34, 0x80.toByte(), 0x01, 0x7f)
        assertArrayEquals(byteArrayOf(0x34, 0x12, 0x01, 0x80.toByte()), PcmConversion.toS16le(be, 0, be.size, PcmEncoding.S16BE))
    }

    @Test fun littleEndianIsCopiedAndAnOddTrailingByteDropped() {
        val le = byteArrayOf(9, 1, 2, 3, 4, 5)
        assertArrayEquals(byteArrayOf(1, 2, 3, 4), PcmConversion.toS16le(le, 1, 5, PcmEncoding.S16LE))
    }

    @Test fun floatIsScaledAndClipped() {
        val floats = ByteBuffer.allocate(20).order(ByteOrder.LITTLE_ENDIAN)
            .putFloat(0f).putFloat(1f).putFloat(-1f).putFloat(0.5f).putFloat(3f).array()
        assertEquals(listOf(0, 32767, -32767, 16384, 32767), samples(PcmConversion.toS16le(floats, 0, floats.size, PcmEncoding.FLOAT32LE)))
    }

    @Test fun moreThanTwoChannelsKeepTheFrontPair() {
        val sixChannels = s16le(1, 2, 3, 4, 5, 6, 11, 12, 13, 14, 15, 16)
        assertEquals(listOf(1, 2, 11, 12), samples(PcmConversion.toAtMostStereo(sixChannels, 6)))
        val stereo = s16le(1, 2)
        assertSame(stereo, PcmConversion.toAtMostStereo(stereo, 2))
    }

    @Test fun ratesThatAreNotWholeHundredsAreRaisedToAStandardRate() {
        assertEquals(48_000, PcmConversion.wireRate(48_000))
        assertEquals(44_100, PcmConversion.wireRate(44_100))
        assertEquals(24_000, PcmConversion.wireRate(24_000))
        assertEquals(16_000, PcmConversion.wireRate(11_025))
        assertEquals(24_000, PcmConversion.wireRate(22_050))
        assertEquals(48_000, PcmConversion.wireRate(96_000))
        assertEquals(8_000, PcmConversion.wireRate(4_000))
    }

    @Test fun resamplerKeepsDurationAndInterpolatesLinearly() {
        val resampler = LinearResampler(22_050, 24_000, 1)
        val input = s16le(*IntArray(2205) { it * 10 })
        val output = samples(resampler.process(input))
        // 100 ms in, ~100 ms out (one frame is held back for the next chunk).
        assertTrue("got ${output.size}", output.size in 2398..2400)
        assertEquals(0, output[0])
        // A ramp stays a ramp: step 10 * 22050 / 24000 per output frame.
        for (i in 1 until output.size) assertEquals(9.1875, (output[i] - output[i - 1]).toDouble(), 1.01)
    }

    @Test fun resamplerJoinsChunksSeamlessly() {
        val input = s16le(*IntArray(2000) { (it * 13) % 30000 - 15000 })
        val whole = LinearResampler(11_025, 16_000, 2).process(input)
        val chunked = LinearResampler(11_025, 16_000, 2).run {
            process(input, 0, 1000) + process(input, 1000, 1236) + process(input, 2236, input.size - 2236)
        }
        assertEquals(whole.size, chunked.size)
        val a = samples(whole)
        val b = samples(chunked)
        for (i in a.indices) assertTrue("sample $i: ${a[i]} vs ${b[i]}", kotlin.math.abs(a[i] - b[i]) <= 1)
    }
}
