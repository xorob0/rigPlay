package com.shilapi.xcertplay.simhub

import java.net.InetAddress

/**
 * Where the phone's microphone comes from when the PC supplies it (`docs/protocol.md` §6.13, §10.4): the
 * control-channel messages that frame the stream and what the receiver needs to filter datagrams.
 *
 * [com.shilapi.xcertplay.media.NetworkMicrophoneSource] uses it; [SimHubLinkMicTransport] is the implementation over
 * a [SimHubLink], which the owner of the link plugs into [SimHubEndpoints.microphone]. Every member may be called
 * from any thread.
 */
interface SimHubMicTransport {
    /**
     * True when a new microphone stream should come from the PC: the user chose "PC via SimHub", the link is up,
     * the session has feature `mic` and the latest `state.mic.enabled` is true. Checked when the phone opens its
     * microphone; otherwise the tablet's own microphone is used.
     */
    val micAvailable: Boolean

    /**
     * The paired PC's address while a Paired session is open, else `null`. Microphone datagrams are accepted only
     * from it (§10.4). Read for every datagram, so it must be cheap.
     */
    val pairedHost: InetAddress?

    /** Changes whenever a new Paired session begins: link loss stopped the plugin's stream, so it is asked again. */
    val micEpoch: Long

    /** `micStart` (§6.13): mono [sampleRate] Hz to UDP [port] on this tablet. False when it could not be queued. */
    fun micStart(sampleRate: Int, port: Int): Boolean

    /** `micStop` (§6.13). False when it could not be queued (link down). */
    fun micStop(): Boolean
}

/** [SimHubMicTransport] over a [SimHubLink]; [enabled] is the user's choice ("Microphone: PC via SimHub"). */
class SimHubLinkMicTransport(
    private val link: SimHubLink,
    private val enabled: () -> Boolean,
) : SimHubMicTransport {
    private class Resolved(val host: String, val address: InetAddress)

    @Volatile private var resolved: Resolved? = null

    override val micAvailable: Boolean
        get() = link.state.micAvailable && runCatching(enabled).getOrDefault(false)

    override val pairedHost: InetAddress?
        get() {
            val state = link.state
            if (!state.paired) return null
            val host = state.host ?: return null
            resolved?.let { if (it.host == host) return it.address }
            // The host is the beacon's (or the stored) IP literal, so this does not hit DNS in practice.
            val address = runCatching { InetAddress.getByName(host) }.getOrNull() ?: return null
            resolved = Resolved(host, address)
            return address
        }

    override val micEpoch: Long get() = link.pairedSessions

    override fun micStart(sampleRate: Int, port: Int): Boolean = link.sendMicStart(sampleRate, port)

    override fun micStop(): Boolean = link.sendMicStop()
}
