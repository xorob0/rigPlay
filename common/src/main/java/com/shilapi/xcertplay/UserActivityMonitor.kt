package com.shilapi.xcertplay

/**
 * When the user last touched rigPlay (#53). The home activities report touches and keys from
 * `onUserInteraction` ([RigSessionCoordinator.onUserInteraction]), and [RigSessionLifecycle] lets the
 * rigPlay idle screen take over the home page after "Go idle after" minutes without any.
 *
 * [clock] is monotonic milliseconds: `SystemClock.elapsedRealtime` in the app, a fake in tests. Not
 * thread-safe: call it on the main thread.
 */
class UserActivityMonitor(private val clock: () -> Long = { System.nanoTime() / 1_000_000L }) {
    /** [clock] time of the last touch or key, or of the monitor's creation. */
    var lastInteractionAt: Long = clock()
        private set

    fun now(): Long = clock()

    /** A touch or a key on a rigPlay screen, or the user arriving on one. */
    fun onUserInteraction() {
        lastInteractionAt = clock()
    }

    /** Milliseconds since the last interaction. */
    fun idleForMs(): Long = (clock() - lastInteractionAt).coerceAtLeast(0L)
}
