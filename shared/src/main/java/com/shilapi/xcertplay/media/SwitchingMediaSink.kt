package com.shilapi.xcertplay.media

import android.util.Log
import com.shilapi.xcertplay.airplay.AudioFormat
import com.shilapi.xcertplay.airplay.AudioStreamId
import com.shilapi.xcertplay.airplay.MediaSink
import com.shilapi.xcertplay.airplay.MicrophoneConfig
import com.shilapi.xcertplay.airplay.VideoCodec
import com.shilapi.xcertplay.simhub.SimHubAudioTransport
import com.shilapi.xcertplay.simhub.SimHubEndpoints

/** Where the user wants CarPlay audio to play (setting "Audio output"). */
enum class AudioOutputTarget(val key: String) {
    /** On the PC through the SimHub plugin, falling back to the tablet while the link is down. */
    PC("pc"),

    /** Always on the tablet. */
    TABLET("tablet"),
    ;

    companion object {
        val DEFAULT = PC

        fun fromKey(key: String?): AudioOutputTarget = values().firstOrNull { it.key == key } ?: DEFAULT
    }
}

/**
 * A [MediaSink] that plays CarPlay audio on the PC ([network]) while the SimHub link can take it
 * (paired, audio enabled: [SimHubAudioTransport.audioTarget] is not `null`) and on the tablet ([local])
 * otherwise. Video, microphone and iAP2 always go to [local].
 *
 * The route is checked for every RTP packet, so a stream moves live when the link comes or goes: the
 * old sink gets `onAudioStopped` (the network sink sends its buffered audio and `audioStop`), then the
 * new sink gets `onAudioStarted` and the packet. The engine never notices.
 *
 * [onMediaAudioChanged] reports whether any music stream runs, whichever sink plays it; give [local] a
 * no-op callback so the two do not disagree while a stream moves.
 */
class SwitchingMediaSink(
    private val local: MediaSink,
    private val network: NetworkAudioSink = NetworkAudioSink(),
    private val transport: () -> SimHubAudioTransport? = { SimHubEndpoints.audioTransport },
    private val onMediaAudioChanged: (Boolean) -> Unit = {},
    private val log: (String) -> Unit = { Log.i(NetworkAudioSink.TAG, it) },
) : MediaSink {
    enum class Route { LOCAL, PC }

    private class Active(var format: AudioFormat, var route: Route)

    private val lock = Any()
    private val active = HashMap<AudioStreamId, Active>()
    private var mediaAudioActive = false

    /** True while new audio goes to the PC. */
    val routesToPc: Boolean get() = transport()?.audioTarget != null

    /** The route of each running stream. */
    val routes: Map<AudioStreamId, Route> get() = synchronized(lock) { active.mapValues { it.value.route } }

    override fun onVideoCodec(type: Int, codec: VideoCodec) = local.onVideoCodec(type, codec)
    override fun onVideoConfig(type: Int, codecData: ByteArray) = local.onVideoConfig(type, codecData)
    override fun onVideoFrame(type: Int, naluBytes: ByteArray) = local.onVideoFrame(type, naluBytes)
    override fun setVideoRecoveryHandler(type: Int, handler: () -> Unit) = local.setVideoRecoveryHandler(type, handler)
    override fun setVideoDiagnosticHandler(type: Int, handler: (String) -> Unit) = local.setVideoDiagnosticHandler(type, handler)
    override fun onScreenStreamActive(type: Int, active: Boolean) = local.onScreenStreamActive(type, active)
    override fun onMicrophoneStarted(id: AudioStreamId, config: MicrophoneConfig) = local.onMicrophoneStarted(id, config)
    override fun onMicrophoneStopped(id: AudioStreamId) = local.onMicrophoneStopped(id)
    override fun onIapMessage(bytes: ByteArray) = local.onIapMessage(bytes)

    override fun onAudioStarted(id: AudioStreamId, format: AudioFormat, firstSample: Int) {
        val route = currentRoute()
        synchronized(lock) {
            val previous = active[id]
            if (previous != null && previous.route != route) sinkFor(previous.route).onAudioStopped(id)
            active[id] = Active(format, route)
            sinkFor(route).onAudioStarted(id, format, firstSample)
            publishMediaAudioLocked()
        }
    }

    override fun onAudioRtp(id: AudioStreamId, format: AudioFormat, rtp: ByteArray, sample: Int) {
        val route = currentRoute()
        synchronized(lock) {
            val entry = active[id]
            when {
                entry == null -> {
                    active[id] = Active(format, route)
                    sinkFor(route).onAudioStarted(id, format, sample)
                    publishMediaAudioLocked()
                }
                entry.route != route -> {
                    sinkFor(entry.route).onAudioStopped(id)
                    entry.route = route
                    entry.format = format
                    sinkFor(route).onAudioStarted(id, format, sample)
                    log("audio $id moved to ${if (route == Route.PC) "the PC" else "the tablet"}")
                }
                else -> entry.format = format
            }
            sinkFor(route).onAudioRtp(id, format, rtp, sample)
        }
    }

    override fun onAudioStopped(id: AudioStreamId) {
        synchronized(lock) {
            val entry = active.remove(id)
            if (entry != null) {
                sinkFor(entry.route).onAudioStopped(id)
            } else {
                // The engine also stops streams it never started; both sinks ignore unknown ones.
                local.onAudioStopped(id)
                network.onAudioStopped(id)
            }
            publishMediaAudioLocked()
        }
    }

    /** Ends the PC streams. [local] is closed by its owner. */
    fun close() {
        synchronized(lock) {
            active.entries.removeAll { (id, entry) ->
                if (entry.route == Route.PC) network.onAudioStopped(id)
                entry.route == Route.PC
            }
            publishMediaAudioLocked()
        }
        network.close()
    }

    private fun currentRoute(): Route = if (routesToPc) Route.PC else Route.LOCAL

    private fun sinkFor(route: Route): MediaSink = if (route == Route.PC) network else local

    private fun publishMediaAudioLocked() {
        val next = active.values.any { it.format.audioType == "media" }
        if (next == mediaAudioActive) return
        mediaAudioActive = next
        onMediaAudioChanged(next)
    }
}
