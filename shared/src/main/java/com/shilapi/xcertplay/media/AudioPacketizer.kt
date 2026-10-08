package com.shilapi.xcertplay.media

import java.io.Closeable

/**
 * Cuts one stream's s16le PCM into audio datagrams (`docs/protocol.md` §10.2) in one wire format:
 * [PcmPacketizer] sends the PCM as it is, [OpusPacketizer] encodes it first (§10.4). A new packetizer
 * belongs to each `audioStart`. [emit] receives a buffer and its length; the buffer is valid only during
 * the call.
 */
interface AudioPacketizer : Closeable {
    val sampleRate: Int
    val channels: Int

    /** Datagrams emitted so far. */
    val datagrams: Long

    /** Appends PCM and emits every datagram it completes. */
    fun push(pcm: ByteArray, offset: Int = 0, length: Int = pcm.size - offset, emit: (ByteArray, Int) -> Unit)

    /** Sends what is buffered (end of stream, or before a format change). */
    fun flush(emit: (ByteArray, Int) -> Unit)

    /** Sends datagrams that became ready since the last [push] without waiting (an encoder working in the background). */
    fun poll(emit: (ByteArray, Int) -> Unit) {}

    /**
     * [frames] of audio are missing between what was pushed so far and what comes next (lost upstream or
     * dropped here): the timestamp jumps so the PC plays silence there (§10.2). Before the first datagram
     * there is nothing to place the gap after, so it is ignored.
     */
    fun skip(frames: Long, emit: (ByteArray, Int) -> Unit)

    override fun close() {}
}
