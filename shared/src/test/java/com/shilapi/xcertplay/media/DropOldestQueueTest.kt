package com.shilapi.xcertplay.media

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class DropOldestQueueTest {
    @Test fun overflowDropsTheOldestAndCountsIt() {
        val queue = DropOldestQueue<Int>(3)
        assertNull(queue.offer(1))
        assertNull(queue.offer(2))
        assertNull(queue.offer(3))
        assertEquals(1, queue.offer(4))
        assertEquals(2, queue.offer(5))
        assertEquals(2L, queue.dropped)
        assertEquals(3, queue.size)
        assertEquals(listOf(3, 4, 5), List(3) { queue.poll(0) })
        assertNull(queue.poll(10))
        assertEquals(2L, queue.dropped)
    }

    @Test fun pollWaitsForAProducer() {
        val queue = DropOldestQueue<String>(1)
        Thread { Thread.sleep(50); queue.offer("late") }.start()
        assertEquals("late", queue.poll(2_000))
    }
}
