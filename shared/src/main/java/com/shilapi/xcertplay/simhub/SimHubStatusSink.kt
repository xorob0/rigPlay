package com.shilapi.xcertplay.simhub

/**
 * Receives what the CarPlay session reports for `status` (`docs/protocol.md` §6.7), through
 * [SimHubMediaBridge]. The owner of the link plugs one into [SimHubEndpoints.statusSink] and must stay
 * the only sender of `status` lines: in the app that is `RigSessionLifecycle.publishStatus`, fed by the
 * coordinator's sink. Calls may come from any thread.
 */
interface SimHubStatusSink {
    /** The latest `status.nowPlaying`; `null` when nothing is known (no phone, no media). */
    fun updateNowPlaying(nowPlaying: NowPlaying?)

    /** `status.phoneConnected` and `status.phoneName`. */
    fun updatePhone(connected: Boolean, phoneName: String?) {}
}
