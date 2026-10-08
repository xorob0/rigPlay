package com.shilapi.xcertplay.media

import java.util.ArrayDeque
import java.util.concurrent.TimeUnit
import java.util.concurrent.locks.ReentrantLock
import kotlin.concurrent.withLock

/**
 * A bounded FIFO that never blocks the producer: when full, [offer] discards the oldest element so the
 * consumer stays close to live, and counts it in [dropped].
 */
class DropOldestQueue<T : Any>(val capacity: Int) {
    init {
        require(capacity >= 1) { "capacity must be positive" }
    }

    private val lock = ReentrantLock()
    private val notEmpty = lock.newCondition()
    private val items = ArrayDeque<T>(capacity)

    @Volatile
    var dropped: Long = 0L
        private set

    val size: Int get() = lock.withLock { items.size }

    fun isEmpty(): Boolean = lock.withLock { items.isEmpty() }

    /** Adds [item]; returns the element discarded to make room, or `null`. */
    fun offer(item: T): T? = lock.withLock {
        val discarded = if (items.size >= capacity) items.pollFirst().also { dropped++ } else null
        items.addLast(item)
        notEmpty.signal()
        discarded
    }

    /** The oldest element, waiting up to [timeoutMs] for one; `null` on timeout. */
    fun poll(timeoutMs: Long): T? = lock.withLock {
        var nanos = TimeUnit.MILLISECONDS.toNanos(timeoutMs)
        while (items.isEmpty()) {
            if (nanos <= 0L) return null
            nanos = notEmpty.awaitNanos(nanos)
        }
        items.pollFirst()
    }

    fun clear() = lock.withLock { items.clear() }
}
