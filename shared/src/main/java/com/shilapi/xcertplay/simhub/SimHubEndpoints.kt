package com.shilapi.xcertplay.simhub

/**
 * Process-wide plug points between the CarPlay session and whoever owns the [SimHubLink] (the rig
 * session coordinator, `RigSessionCoordinator`). The owner sets [audioTransport] when it creates the
 * link and clears (and closes) it when it stops the link, plugs a [statusSink] into its single `status`
 * sender, and forwards `command media` to [mediaBridge]:
 *
 * ```
 * SimHubEndpoints.audioTransport = SimHubLinkAudioTransport(link)
 * SimHubEndpoints.statusSink = sinkFeedingTheOneStatusSender
 * // on `command media`:
 * if (SimHubEndpoints.mediaBridge.onCommand(command) == SimHubMediaBridge.Result.UNAVAILABLE) {
 *     link.sendCommandUnavailable()
 * }
 * // on `telemetry` (any thread):
 * SimHubEndpoints.telemetry.update(telemetry)
 * ```
 */
object SimHubEndpoints {
    @Volatile var audioTransport: SimHubAudioTransport? = null

    /** The PC microphone (#34): set by the link owner with the user's "Microphone" choice; `null` without a link. */
    @Volatile var microphone: SimHubMicTransport? = null

    /** Setting it sends the current phone and now-playing state to the new sink. */
    @Volatile var statusSink: SimHubStatusSink? = null
        set(value) {
            field = value
            mediaBridge.republish()
        }

    /** Bound to the running CarPlay session by `CarPlayMediaKeys.attach`/`detach`. */
    val mediaBridge: SimHubMediaBridge = SimHubMediaBridge(statusSink = { statusSink })

    /** `telemetry` from the paired PC (#41); the link owner feeds it from `Listener.onTelemetry`. */
    val telemetry: SimHubTelemetryStore = SimHubTelemetryStore()
}
