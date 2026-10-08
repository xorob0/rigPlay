package com.shilapi.xcertplay.media

import com.shilapi.xcertplay.simhub.AudioDatagram
import com.shilapi.xcertplay.simhub.AudioDirection
import com.shilapi.xcertplay.simhub.AudioFormat as WireFormat
import com.shilapi.xcertplay.simhub.AudioStream
import com.shilapi.xcertplay.simhub.SimHubAudioCodec
import com.shilapi.xcertplay.simhub.SimHubAudioTransport
import java.net.InetAddress
import java.net.InetSocketAddress
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit

/** Records what a sender does with the link, in order. */
class FakeAudioTransport : SimHubAudioTransport {
    sealed class Event {
        data class Start(val stream: AudioStream, val sampleRate: Int, val channels: Int, val format: WireFormat = WireFormat.PCM_S16LE) : Event()
        data class Stop(val stream: AudioStream) : Event()
        data class Datagram(val datagram: AudioDatagram) : Event()
    }

    @Volatile var target: InetSocketAddress? = InetSocketAddress(InetAddress.getLoopbackAddress(), 23712)
    @Volatile var epoch: Long = 1L
    /** What the plugin prefers (the first of its `state.audio.formats`). */
    @Volatile var format: WireFormat = WireFormat.PCM_S16LE
    val events = LinkedBlockingQueue<Event>()

    override val audioTarget: InetSocketAddress? get() = target
    override val audioFormat: WireFormat? get() = if (target == null) null else format
    override val audioEpoch: Long get() = epoch

    override fun audioStart(stream: AudioStream, sampleRate: Int, channels: Int, format: WireFormat): Boolean {
        if (target == null) return false
        events.add(Event.Start(stream, sampleRate, channels, format))
        return true
    }

    override fun audioStop(stream: AudioStream): Boolean {
        events.add(Event.Stop(stream))
        return target != null
    }

    override fun sendDatagram(bytes: ByteArray, length: Int): Boolean {
        val decoded = SimHubAudioCodec.decode(bytes, AudioDirection.TABLET_TO_PC, 0, length) ?: error("sender produced an invalid datagram")
        events.add(Event.Datagram(decoded))
        return true
    }

    inline fun <reified T : Event> await(timeoutMs: Long = 3_000, match: (T) -> Boolean = { true }): T {
        val deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(timeoutMs)
        while (true) {
            val left = TimeUnit.NANOSECONDS.toMillis(deadline - System.nanoTime())
            val event = if (left > 0) events.poll(left, TimeUnit.MILLISECONDS) else null
            if (event == null) throw AssertionError("no ${T::class.simpleName} within $timeoutMs ms")
            if (event is T && match(event)) return event
        }
    }

    /** Collects events until none arrives for [quietMs]. */
    fun drain(quietMs: Long = 300): List<Event> {
        val out = mutableListOf<Event>()
        while (true) out.add(events.poll(quietMs, TimeUnit.MILLISECONDS) ?: return out)
    }
}
