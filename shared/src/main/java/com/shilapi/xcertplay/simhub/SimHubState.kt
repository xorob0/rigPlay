package com.shilapi.xcertplay.simhub

/**
 * Immutable snapshot of what [SimHubLink] knows about the plugin. A new instance replaces the old
 * one on every change; the plugin-side members ([dashboardUrl] … [audio]) are replaced as a whole on
 * every `state` message (§6.6) and cleared when the session ends.
 */
data class SimHubState(
    val phase: Phase = Phase.STOPPED,
    /** Address and control port the link connects (or will connect) to. */
    val host: String? = null,
    val controlPort: Int? = null,
    /** From `welcome` (§6.2). */
    val hostId: String? = null,
    val hostName: String? = null,
    val hostVersion: String? = null,
    val simhubVersion: String? = null,
    /** Negotiated protocol version and features in effect for this session. */
    val protocol: Int? = null,
    val features: Set<String> = emptySet(),
    /** From the latest `state` (§6.6). */
    val dashboardUrl: String? = null,
    val idleDashboardUrl: String? = null,
    val dashboardServer: DashboardServer? = null,
    val audio: AudioSettings? = null,
    /** From the latest `state.mic` (§6.6); `null` when absent. */
    val mic: MicSettings? = null,
    /** Last pairing answer while Unpaired, for the onboarding UI (§8). */
    val lastPairResult: SimHubMessage.PairResult? = null,
) {
    enum class Phase {
        /** [SimHubLink.start] not called, or [SimHubLink.stop] called. */
        STOPPED,

        /** Opening TCP or waiting for `welcome`. */
        CONNECTING,

        /** `welcome` received; no `pairResult ok` yet (§5.2 Unpaired). */
        UNPAIRED,

        /** `pairResult ok` received: the link is up (§1). */
        PAIRED,

        /** Session ended; waiting for the next reconnect attempt. */
        WAITING,

        /** `unsupportedProtocol`: no automatic retry until a new beacon range or [SimHubLink.reconnectNow]. */
        INCOMPATIBLE,
    }

    /** A TCP session is open and `welcome` was received. */
    val connected: Boolean get() = phase == Phase.UNPAIRED || phase == Phase.PAIRED

    /** The link is up: a Paired session is open. */
    val paired: Boolean get() = phase == Phase.PAIRED

    /** `state.dashboardServer` absent means unknown; the tablet then assumes reachable (§6.6). */
    val dashboardServerReachable: Boolean get() = dashboardServer?.reachable ?: true

    /** The tablet may send audio only while this is true (§6.6, §6.11). */
    val audioEnabled: Boolean get() = paired && audio?.enabled == true

    fun hasFeature(feature: String): Boolean = feature in features

    /**
     * The PC can supply the phone's microphone (§6.13): the link is up, the session has feature `mic` and the
     * latest `state.mic.enabled` is true.
     */
    val micAvailable: Boolean get() = paired && hasFeature(SimHubProtocol.FEATURE_MIC) && mic?.enabled == true

    companion object {
        val STOPPED = SimHubState()
    }
}
