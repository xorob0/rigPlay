package com.shilapi.xcertplay.simhub

import java.util.TreeMap

/**
 * The tablet's jitter buffer for the PC microphone (`docs/protocol.md` §10.4): mono s16 chunks placed by their
 * datagram `timestamp`, played out frame by frame at the rate the phone consumes them.
 *
 * - A datagram with the `start` flag (or the first one) resets the buffer to its timestamp.
 * - Playback starts once [targetFrames] are buffered (about 40 ms), and again after an underrun; while it waits
 *   it plays silence without advancing, so the wait does not turn into latency.
 * - Gaps (lost datagrams) and underruns play silence. Datagrams that arrive after their play-out time are dropped.
 * - More than [maxFrames] buffered (the PC's clock runs faster than the tablet's, or a burst arrived) skips ahead
 *   to [targetFrames], so the delay stays bounded.
 *
 * Timestamps are u32 and wrap; they are unwrapped against the newest one seen. Thread-safe.
 */
class SimHubMicJitterBuffer(
    val sampleRate: Int,
    targetMs: Int = DEFAULT_TARGET_MS,
    maxMs: Int = DEFAULT_MAX_MS,
) {
    val targetFrames: Int = maxOf(1, sampleRate * targetMs / 1000)
    val maxFrames: Int = maxOf(targetFrames * 2, sampleRate * maxMs / 1000)

    private val chunks = TreeMap<Long, ShortArray>()
    private var started = false
    private var priming = true
    private var nextPlay = 0L
    private var newestAbs = 0L
    private var newestRaw = 0L

    /** Counters for diagnostics. */
    var received = 0L
        private set
    var late = 0L
        private set
    var skipped = 0L
        private set
    var underruns = 0L
        private set
    var concealedFrames = 0L
        private set
    var playedFrames = 0L
        private set

    /** Frames buffered ahead of the play position. */
    val bufferedFrames: Int
        @Synchronized get() = if (chunks.isEmpty()) 0 else (endOfLast() - maxOf(nextPlay, chunks.firstKey())).toInt().coerceAtLeast(0)

    /** Forgets everything buffered; the next datagram starts a new stream. */
    @Synchronized
    fun reset() {
        chunks.clear()
        started = false
        priming = true
    }

    /** Adds one datagram's samples (mono) at [timestamp] (u32). */
    @Synchronized
    fun push(timestamp: Long, start: Boolean, samples: ShortArray) {
        if (samples.isEmpty()) return
        received++
        val raw = timestamp and U32
        val abs: Long
        if (start || !started) {
            chunks.clear()
            started = true
            priming = true
            nextPlay = raw
            newestAbs = raw
            newestRaw = raw
            abs = raw
        } else {
            // Signed 32-bit distance from the newest timestamp: works across the wrap in both directions.
            abs = newestAbs + ((raw - newestRaw).toInt()).toLong()
        }
        val end = abs + samples.size
        if (end <= nextPlay) {
            late++
            return
        }
        if (abs > newestAbs) {
            newestAbs = abs
            newestRaw = raw
        }
        if (abs < nextPlay) {
            // Partly late: keep the part still ahead of the play position.
            chunks[nextPlay] = samples.copyOfRange((nextPlay - abs).toInt(), samples.size)
        } else if (!chunks.containsKey(abs)) {
            chunks[abs] = samples
        }
        if (endOfLast() - maxOf(nextPlay, chunks.firstKey()) > maxFrames) {
            skipped++
            dropBefore(endOfLast() - targetFrames)
        }
    }

    /**
     * Fills [out] with the next [frames] frames (silence where nothing is buffered) and advances the play position.
     * Returns true when any real audio was played.
     */
    @Synchronized
    fun pull(out: ShortArray, frames: Int = out.size): Boolean {
        out.fill(0, 0, frames)
        if (chunks.isEmpty()) {
            concealedFrames += frames
            return false
        }
        if (priming) {
            val first = chunks.firstKey()
            if (endOfLast() - maxOf(nextPlay, first) < targetFrames) {
                concealedFrames += frames
                return false
            }
            priming = false
            // Start at the first buffered frame: a gap in front (an underrun) is not played as silence.
            if (first > nextPlay) nextPlay = first
        }
        var real = 0L
        val windowEnd = nextPlay + frames
        val startKey = chunks.floorKey(nextPlay) ?: chunks.firstKey()
        for ((key, chunk) in chunks.tailMap(startKey, true)) {
            if (key >= windowEnd) break
            val from = maxOf(key, nextPlay)
            val to = minOf(key + chunk.size, windowEnd)
            if (to <= from) continue
            System.arraycopy(chunk, (from - key).toInt(), out, (from - nextPlay).toInt(), (to - from).toInt())
            real += to - from
        }
        playedFrames += real
        concealedFrames += frames - real
        nextPlay = windowEnd
        dropBefore(nextPlay)
        if (chunks.isEmpty()) {
            underruns++
            priming = true
        }
        return real > 0
    }

    private fun endOfLast(): Long {
        val last = chunks.lastEntry() ?: return nextPlay
        return last.key + last.value.size
    }

    /** Drops every frame before [position] and moves the play position there if it is behind. */
    private fun dropBefore(position: Long) {
        if (position > nextPlay) nextPlay = position
        while (chunks.isNotEmpty()) {
            val first = chunks.firstEntry()
            val end = first.key + first.value.size
            if (end <= nextPlay) {
                chunks.remove(first.key)
            } else {
                if (first.key < nextPlay) {
                    chunks.remove(first.key)
                    chunks[nextPlay] = first.value.copyOfRange((nextPlay - first.key).toInt(), first.value.size)
                }
                break
            }
        }
    }

    companion object {
        const val DEFAULT_TARGET_MS = 40
        const val DEFAULT_MAX_MS = 200
        private const val U32 = 0xFFFF_FFFFL
    }
}
