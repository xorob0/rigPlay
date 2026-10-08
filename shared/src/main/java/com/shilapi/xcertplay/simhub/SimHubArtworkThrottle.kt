package com.shilapi.xcertplay.simhub

/**
 * When to send `artwork` (#47): only an artwork that differs from the one last sent in this link
 * session, at most once per [minIntervalMillis]; within the interval the latest offer wins and goes out
 * when the interval ends (trailing edge). Thread-safe.
 */
class SimHubArtworkThrottle(private val minIntervalMillis: Long = MIN_INTERVAL_MILLIS) {
    private var latest: String? = null
    private var sent: String? = null
    private var lastSentAt: Long? = null

    /** The newest encoded artwork. */
    @Synchronized
    fun offer(payload: String) {
        latest = payload
    }

    /** The payload to send now, marked as sent, or `null` (nothing new, or too soon). */
    @Synchronized
    fun due(nowMillis: Long): String? {
        if (waitMillis(nowMillis) != 0L) return null
        val next = latest ?: return null
        sent = next
        lastSentAt = nowMillis
        return next
    }

    /** Milliseconds until [due] returns something; `null` when nothing is pending. */
    @Synchronized
    fun waitMillis(nowMillis: Long): Long? {
        val next = latest ?: return null
        if (next == sent) return null
        val last = lastSentAt ?: return 0L
        return (minIntervalMillis - (nowMillis - last)).coerceAtLeast(0L)
    }

    /** A new link session: the plugin has no artwork, so the latest goes out again. */
    @Synchronized
    fun resend() {
        sent = null
    }

    companion object {
        const val MIN_INTERVAL_MILLIS = 2_000L
    }
}
