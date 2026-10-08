package com.shilapi.xcertplay.simhub

import java.io.Closeable
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress

/**
 * Where CarPlay audio goes when it plays on the PC (`docs/protocol.md` §6.11, §6.12, §10): the
 * control-channel messages that frame a stream and the UDP datagrams that carry it.
 *
 * [com.shilapi.xcertplay.media.NetworkAudioSink] streams through this seam and
 * [com.shilapi.xcertplay.media.SwitchingMediaSink] uses [audioTarget] to decide between the PC and the
 * tablet speaker. [SimHubLinkAudioTransport] is the implementation over a [SimHubLink]; the owner of the
 * link plugs it into [SimHubEndpoints.audioTransport].
 *
 * Every member may be called from any thread.
 */
interface SimHubAudioTransport {
    /**
     * The plugin's audio receiver (the control connection's address and `state.audio.port`), or `null`
     * when audio must play on the tablet: link down or unpaired, `state.audio.enabled` false, or no
     * format the tablet can send. Read for every packet, so it must be cheap.
     */
    val audioTarget: InetSocketAddress?

    /**
     * The format to send in: the first of `state.audio.formats` the tablet knows (§6.6, §10.4), so the
     * plugin's preference decides. `null` when there is none (and [audioTarget] is then `null` too).
     */
    val audioFormat: AudioFormat? get() = AudioFormat.PCM_S16LE

    /**
     * Changes whenever a new Paired session begins. Link loss stops every stream on the plugin (§10.1),
     * so a stream announced in an older epoch is announced again before more datagrams are sent.
     */
    val audioEpoch: Long get() = 0L

    /** `audioStart` (§6.11). Returns false when it could not be queued (link down, audio disabled). */
    fun audioStart(stream: AudioStream, sampleRate: Int, channels: Int, format: AudioFormat = AudioFormat.PCM_S16LE): Boolean

    /** `audioStop` (§6.12). Returns false when it could not be queued. */
    fun audioStop(stream: AudioStream): Boolean

    /**
     * Sends the first [length] bytes of [bytes], one complete datagram (§10.2 header plus payload), to
     * [audioTarget]. Called on a sender thread; [bytes] may be reused once this returns.
     */
    fun sendDatagram(bytes: ByteArray, length: Int): Boolean
}

/**
 * [SimHubAudioTransport] over a [SimHubLink]: control messages go through the link, datagrams through
 * one unconnected [DatagramSocket] to the address of the link's current host and `state.audio.port`.
 * The datagrams are marked Expedited Forwarding (DSCP 46), which Wi-Fi maps to its voice queue: on a
 * tablet whose Wi-Fi also carries the dashboard and whatever else is on the network, that is the
 * difference between 5 ms datagrams leaving on time and leaving in bursts.
 */
class SimHubLinkAudioTransport(
    private val link: SimHubLink,
    private val socket: DatagramSocket = DatagramSocket(),
) : SimHubAudioTransport, Closeable {
    private class Resolved(val host: String, val port: Int, val address: InetSocketAddress)

    @Volatile private var resolved: Resolved? = null

    init {
        // Best effort: a platform that refuses it still sends, in the default queue.
        runCatching { socket.trafficClass = EXPEDITED_FORWARDING_TOS }
    }

    override val audioFormat: AudioFormat?
        get() {
            val state = link.state
            if (!state.audioEnabled) return null
            return preferredFormat(state.audio?.formats ?: return null)
        }

    override val audioTarget: InetSocketAddress?
        get() {
            val state = link.state
            if (!state.audioEnabled) return null
            val audio = state.audio ?: return null
            if (preferredFormat(audio.formats) == null) return null
            val host = state.host ?: return null
            resolved?.let { if (it.host == host && it.port == audio.port) return it.address }
            // The host is the beacon's (or the stored) IP literal, so this does not hit DNS in practice.
            val address = InetSocketAddress(host, audio.port).takeUnless { it.isUnresolved } ?: return null
            resolved = Resolved(host, audio.port, address)
            return address
        }

    override val audioEpoch: Long get() = link.pairedSessions

    override fun audioStart(stream: AudioStream, sampleRate: Int, channels: Int, format: AudioFormat): Boolean =
        link.sendAudioStart(stream, sampleRate, channels, format)

    override fun audioStop(stream: AudioStream): Boolean = link.sendAudioStop(stream)

    override fun sendDatagram(bytes: ByteArray, length: Int): Boolean {
        val target = audioTarget ?: return false
        return try {
            socket.send(DatagramPacket(bytes, 0, length, target))
            true
        } catch (_: IOException) {
            false
        }
    }

    override fun close() = socket.close()

    companion object {
        /** IP TOS byte for DSCP EF (46 << 2): WMM access category voice. */
        const val EXPEDITED_FORWARDING_TOS = 0xB8

        /** The first format of `state.audio.formats` this tablet can send; names it does not know are skipped. */
        fun preferredFormat(formats: List<String>): AudioFormat? = formats.firstNotNullOfOrNull(AudioFormat::fromWire)
    }
}
