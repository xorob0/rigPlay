package com.shilapi.xcertplay

import android.graphics.Bitmap
import android.os.SystemClock
import android.util.Base64
import android.util.Log
import com.shilapi.xcertplay.simhub.SimHubArtworkThrottle
import com.shilapi.xcertplay.simhub.SimHubProtocol
import java.io.ByteArrayOutputStream
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.TimeUnit
import kotlin.math.roundToInt

/**
 * Now-playing artwork to the SimHub plugin (#47) as `{"type":"artwork","mime":"image/jpeg","base64":…}`:
 * downscaled to at most [MAX_SIDE] px, JPEG quality [QUALITY] (lower if the line would not fit in 64 KB),
 * sent only when it changes and at most once per 2 s ([SimHubArtworkThrottle]); the latest artwork is sent
 * again when the link comes back. Fed by [CarPlayMediaKeys] with the decoded artwork of the current track.
 */
internal object SimHubArtworkPublisher {
    private const val TAG = "rigplay-artwork"
    const val MAX_SIDE = 256
    const val QUALITY = 80
    private val FALLBACK_QUALITIES = intArrayOf(QUALITY, 60, 40)
    /** Room for `{"type":"artwork","mime":"image/jpeg","base64":""}` and the newline in one line (§5.1). */
    private const val MAX_BASE64_CHARS = SimHubProtocol.MAX_LINE_BYTES - 128

    private val worker = Executors.newSingleThreadScheduledExecutor { task ->
        Thread(task, "rigplay-simhub-artwork").apply { isDaemon = true }
    }
    private val throttle = SimHubArtworkThrottle()
    private var lastSource: Bitmap? = null // main thread
    private var pending: ScheduledFuture<*>? = null // worker thread

    /** The current track's artwork changed. Main thread; [artwork] stays owned (and recycled) by the caller. */
    fun onArtwork(artwork: Bitmap) {
        if (artwork === lastSource || artwork.isRecycled) return
        lastSource = artwork
        val scaled = try {
            scaledCopy(artwork)
        } catch (error: RuntimeException) {
            Log.w(TAG, "could not scale artwork", error)
            return
        }
        worker.execute {
            encode(scaled)?.let(throttle::offer)
            pump()
        }
    }

    /** The link is up again: send the latest artwork to the new session. Any thread. */
    fun resend() {
        worker.execute {
            throttle.resend()
            pump()
        }
    }

    /** Worker thread: sends what is due and schedules the trailing send. */
    private fun pump() {
        val now = SystemClock.elapsedRealtime()
        throttle.due(now)?.let { payload ->
            // Not sent while the link is down: resend() runs when it comes back.
            val sent = RigSessionCoordinator.simHubLink?.sendArtwork(payload) == true
            Log.i(TAG, "artwork ${payload.length} base64 chars sent=$sent")
        }
        val wait = throttle.waitMillis(now) ?: return
        if (pending?.isDone == false) return
        pending = worker.schedule(::pump, wait.coerceAtLeast(1L), TimeUnit.MILLISECONDS)
    }

    private fun scaledCopy(source: Bitmap): Bitmap {
        val scale = minOf(1f, MAX_SIDE.toFloat() / maxOf(source.width, source.height))
        val width = (source.width * scale).roundToInt().coerceAtLeast(1)
        val height = (source.height * scale).roundToInt().coerceAtLeast(1)
        val scaled = Bitmap.createScaledBitmap(source, width, height, true)
        // createScaledBitmap may hand back the source itself; the caller may recycle that at any time.
        return if (scaled === source) source.copy(source.config ?: Bitmap.Config.ARGB_8888, false) else scaled
    }

    /** Worker thread: JPEG then base64, or `null` if it does not fit in one control line. Recycles [bitmap]. */
    private fun encode(bitmap: Bitmap): String? = try {
        FALLBACK_QUALITIES.asSequence()
            .mapNotNull { quality ->
                val out = ByteArrayOutputStream()
                if (bitmap.compress(Bitmap.CompressFormat.JPEG, quality, out)) Base64.encodeToString(out.toByteArray(), Base64.NO_WRAP) else null
            }
            .firstOrNull { it.length <= MAX_BASE64_CHARS }
    } finally {
        bitmap.recycle()
    }
}
