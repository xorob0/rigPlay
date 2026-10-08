package com.shilapi.xcertplay.media

/**
 * Watches the pace at which one stream's datagrams leave the tablet, to tell where a stall the PC heard came
 * from. [NetworkAudioSink] calls [beforeSend] and [afterSend] around every `socket.send`; every [windowMillis]
 * [report] answers with one log line when something was slow:
 *
 * - the longest `socket.send` call: a send that blocks for hundreds of milliseconds is the Wi-Fi driver's queue
 *   full behind a stalled radio (power save, a scan, roaming, interference); the plugin receives the queue as a
 *   burst afterwards;
 * - the longest gap between two sends: with short sends, a long gap is the tablet itself (decoder starved, the
 *   phone's packets held up on USB, the thread not scheduled), not the network.
 *
 * Pure Kotlin with an injected clock, so it is unit-tested; the sink feeds it `System.nanoTime()`.
 */
class SendStallMonitor(
    private val streamName: String,
    private val windowMillis: Long = DEFAULT_WINDOW_MILLIS,
    private val thresholdMillis: Long = DEFAULT_THRESHOLD_MILLIS,
) {
    private var windowStartNanos = Long.MIN_VALUE
    private var sendStartNanos = Long.MIN_VALUE
    private var lastSendEndNanos = Long.MIN_VALUE
    private var longestSendNanos = 0L
    private var longestGapNanos = 0L
    private var sends = 0L

    /** The longest `socket.send` seen since the stream started, in ms (for the stop line). */
    var longestSendEverMillis: Long = 0L
        private set

    /** The longest gap between two sends seen since the stream started, in ms (for the stop line). */
    var longestGapEverMillis: Long = 0L
        private set

    fun beforeSend(nowNanos: Long) {
        if (windowStartNanos == Long.MIN_VALUE) windowStartNanos = nowNanos
        if (lastSendEndNanos != Long.MIN_VALUE) {
            val gap = nowNanos - lastSendEndNanos
            if (gap > longestGapNanos) longestGapNanos = gap
        }
        sendStartNanos = nowNanos
    }

    fun afterSend(nowNanos: Long) {
        if (sendStartNanos != Long.MIN_VALUE) {
            val took = nowNanos - sendStartNanos
            if (took > longestSendNanos) longestSendNanos = took
        }
        lastSendEndNanos = nowNanos
        sends++
    }

    /**
     * At the end of a window: a line describing it when the longest send or gap passed [thresholdMillis], else
     * `null`. Either way the window starts over.
     */
    fun report(nowNanos: Long): String? {
        if (windowStartNanos == Long.MIN_VALUE || nowNanos - windowStartNanos < windowMillis * 1_000_000L) return null
        val seconds = (nowNanos - windowStartNanos) / 1_000_000_000L
        val count = sends
        val sendMs = longestSendNanos / 1_000_000L
        val gapMs = longestGapNanos / 1_000_000L
        closeWindow(nowNanos)
        if (sendMs < thresholdMillis && gapMs < thresholdMillis) return null
        val culprit = when {
            sendMs >= thresholdMillis && sendMs >= gapMs / 2 -> "the Wi-Fi held the datagrams back"
            else -> "the tablet produced them late"
        }
        return "PC audio: $streamName stalled in the last $seconds s: longest socket.send $sendMs ms, " +
            "longest gap between datagrams $gapMs ms ($count sent); $culprit"
    }

    /** Folds the current window into the stream totals (at `audioStop`) and returns them for the stop line. */
    fun summary(nowNanos: Long): String {
        closeWindow(nowNanos)
        return "longestSend=${longestSendEverMillis}ms longestGap=${longestGapEverMillis}ms"
    }

    private fun closeWindow(nowNanos: Long) {
        val sendMs = longestSendNanos / 1_000_000L
        val gapMs = longestGapNanos / 1_000_000L
        if (sendMs > longestSendEverMillis) longestSendEverMillis = sendMs
        if (gapMs > longestGapEverMillis) longestGapEverMillis = gapMs
        windowStartNanos = nowNanos
        longestSendNanos = 0L
        longestGapNanos = 0L
        sends = 0L
    }

    companion object {
        const val DEFAULT_WINDOW_MILLIS = 10_000L

        /** A send or a gap this long is a stall the plugin's buffer has to cover. */
        const val DEFAULT_THRESHOLD_MILLIS = 60L
    }
}
