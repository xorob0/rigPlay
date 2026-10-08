package com.shilapi.xcertplay.simhub

import android.util.Log
import com.shilapi.xcertplay.airplay.CarPlayMediaButton
import com.shilapi.xcertplay.media.CarPlayNowPlaying
import com.shilapi.xcertplay.orchestration.CarPlayController

/** The controls the bridge drives on a CarPlay session; [CarPlayController] provides them. */
interface CarPlayMediaRemote {
    /** One CarPlay media-button press ([CarPlayMediaButton] index); false when no session takes it. */
    fun sendMediaButton(index: Int): Boolean

    /** Opens Siri, as the car's voice key does; false when no session takes it. */
    fun requestSiri(): Boolean
}

/**
 * Connects the SimHub plugin to the running CarPlay session (`docs/protocol.md` §6.7, §6.8):
 *
 * - `command media` → [onCommand]: `playPause`, `next`, `previous` become CarPlay media-button presses,
 *   `siri` the Siri request the voice key makes.
 * - The iPhone's now-playing state → `status.nowPlaying` and `status.phoneConnected`/`phoneName` on the
 *   [SimHubStatusSink], on every change. Artwork-only changes are not sent.
 *
 * The iPhone reports the elapsed time only on play, pause and seek. The bridge remembers when each
 * report arrived and sends the position extrapolated to the moment it builds the message, with
 * `updatedAt`, so the plugin can keep extrapolating from there (§6.7).
 *
 * Thread-safe; callbacks from the controller may arrive on any thread.
 */
class SimHubMediaBridge(
    private val statusSink: () -> SimHubStatusSink?,
    private val clock: () -> Long = System::currentTimeMillis,
    private val log: (String) -> Unit = { Log.i(TAG, it) },
) {
    enum class Result {
        /** The press or Siri request was sent to the phone. */
        HANDLED,

        /** No phone takes it now; the caller answers `commandUnavailable` (§6.8). */
        UNAVAILABLE,

        /** Not a `media` command; the bridge did nothing. */
        NOT_MEDIA,
    }

    private var owner: Any? = null
    private var remote: CarPlayMediaRemote? = null
    private val subscriptions = mutableListOf<AutoCloseable>()
    private var latest: CarPlayNowPlaying? = null
    private var anchorPositionMs = 0L
    private var anchorAtMs = 0L
    private var phoneConnected = false
    private var phoneName: String? = null

    /** Binds the bridge to [controller] until [detach]; a previous controller is released. */
    fun attach(controller: CarPlayController) = attach(
        owner = controller,
        remote = object : CarPlayMediaRemote {
            override fun sendMediaButton(index: Int) = controller.sendMediaButton(index)
            override fun requestSiri() = controller.requestSiri()
        },
        subscribeNowPlaying = controller::addNowPlayingObserver,
        subscribePhone = controller::addPhoneObserver,
    )

    /** [attach] with explicit seams, for tests and other session types. */
    @Synchronized
    fun attach(
        owner: Any,
        remote: CarPlayMediaRemote,
        subscribeNowPlaying: ((CarPlayNowPlaying) -> Unit) -> AutoCloseable,
        subscribePhone: ((Boolean, String?) -> Unit) -> AutoCloseable = { AutoCloseable {} },
    ) {
        if (this.owner === owner) return
        releaseLocked()
        this.owner = owner
        this.remote = remote
        subscriptions += subscribePhone { connected, name -> onPhone(owner, connected, name) }
        subscriptions += subscribeNowPlaying { update -> onNowPlaying(owner, update) }
    }

    /** Releases [expected] if it is the bound controller; the plugin then sees no phone and no media. */
    @Synchronized
    fun detach(expected: Any?) {
        if (expected == null || owner !== expected) return
        releaseLocked()
        statusSink()?.let { sink ->
            sink.updateNowPlaying(null)
            sink.updatePhone(false, null)
        }
    }

    /** Carries out a `command` (§6.8). */
    fun onCommand(command: SimHubCommand): Result {
        val action = (command as? SimHubCommand.Media)?.action ?: return Result.NOT_MEDIA
        val target = synchronized(this) { remote }
        val sent = target != null && when (action) {
            MediaAction.PLAY_PAUSE -> target.sendMediaButton(CarPlayMediaButton.PLAY_PAUSE)
            MediaAction.NEXT -> target.sendMediaButton(CarPlayMediaButton.NEXT)
            MediaAction.PREVIOUS -> target.sendMediaButton(CarPlayMediaButton.PREVIOUS)
            MediaAction.SIRI -> target.requestSiri()
        }
        log("SimHub media ${action.wire} sent=$sent")
        return if (sent) Result.HANDLED else Result.UNAVAILABLE
    }

    /** Sends the current state again, e.g. to a status sink that was just plugged in. */
    @Synchronized
    fun republish() {
        val sink = statusSink() ?: return
        sink.updatePhone(phoneConnected, phoneName)
        sink.updateNowPlaying(latest?.let { build(it, clock()) })
    }

    @Synchronized
    private fun onPhone(expected: Any, connected: Boolean, name: String?) {
        if (owner !== expected) return
        val nextName = if (connected) name else null
        if (connected == phoneConnected && nextName == phoneName) return
        phoneConnected = connected
        phoneName = nextName
        statusSink()?.updatePhone(connected, nextName)
    }

    @Synchronized
    private fun onNowPlaying(expected: Any, update: CarPlayNowPlaying) {
        if (owner !== expected) return
        val now = clock()
        val previous = latest
        if (previous == null || update.elapsedMillis != previous.elapsedMillis) {
            anchorPositionMs = update.elapsedMillis ?: 0L
            anchorAtMs = now
        } else if (update.playing != previous.playing) {
            // Paused or resumed without a new elapsed time: continue from where playback got to.
            anchorPositionMs = positionMs(previous, now)
            anchorAtMs = now
        }
        latest = update
        if (previous != null && previous.copy(artworkTransferId = null) == update.copy(artworkTransferId = null)) return
        statusSink()?.updateNowPlaying(build(update, now))
    }

    private fun positionMs(info: CarPlayNowPlaying, now: Long): Long {
        val advanced = anchorPositionMs + if (info.playing) (now - anchorAtMs).coerceAtLeast(0L) else 0L
        val duration = info.durationMillis?.takeIf { it > 0L }
        return (if (duration != null) minOf(advanced, duration) else advanced).coerceAtLeast(0L)
    }

    private fun build(info: CarPlayNowPlaying, now: Long): NowPlaying? {
        val empty = info.title == null && info.artist == null && info.album == null && info.sourceApp == null &&
            info.durationMillis == null && info.elapsedMillis == null && !info.playing
        if (empty) return null
        return NowPlaying(
            title = info.title,
            artist = info.artist,
            album = info.album,
            app = info.sourceApp,
            playing = info.playing,
            position = positionMs(info, now) / 1000.0,
            duration = info.durationMillis?.takeIf { it > 0L }?.let { it / 1000.0 },
            updatedAt = now,
        )
    }

    private fun releaseLocked() {
        subscriptions.forEach { runCatching { it.close() } }
        subscriptions.clear()
        owner = null
        remote = null
        latest = null
        phoneConnected = false
        phoneName = null
    }

    companion object {
        const val TAG = "rigPlay-SimHubMedia"
    }
}
