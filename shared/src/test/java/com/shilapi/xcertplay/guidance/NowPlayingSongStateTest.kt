package com.shilapi.xcertplay.guidance

import com.shilapi.xcertplay.iap2.message.Iap2Messages
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class NowPlayingSongStateTest {
    private fun update(block: com.shilapi.xcertplay.iap2.body.Iap2BodyBuilder.() -> Unit) =
        Iap2Messages.buildRaw(NowPlayingSongState.NOW_PLAYING_UPDATE, block)

    @Test
    fun followsTitleArtistAndPlaybackStatus() {
        val state = NowPlayingSongState()

        assertEquals(NowPlayingSong("Numb — Linkin Park", false),
            state.accept(update { group(0) { string(1, "Numb"); string(12, "Linkin Park") } }))
        assertEquals(NowPlayingSong("Numb — Linkin Park", true), state.accept(update { group(1) { u8(0, 1) } }))
        // Elapsed time alone changes nothing.
        assertNull(state.accept(update { group(1) { u32(1, 120_706L) } }))
        assertEquals(NowPlayingSong("Numb — Linkin Park", false), state.accept(update { group(1) { u8(0, 2) } }))
        // A title-only incremental update retains the last artist.
        assertEquals(NowPlayingSong("Podcast — Linkin Park", false), state.accept(update { group(0) { string(1, "Podcast") } }))
        assertEquals(NowPlayingSong("Podcast — Host", false), state.accept(update { group(0) { string(12, "Host") } }))
    }

    @Test
    fun nothingWithoutATitleOrForOtherMessages() {
        val state = NowPlayingSongState()
        assertNull(state.accept(update { group(1) { u8(0, 1) } }))
        assertNull(state.accept(Iap2Messages.buildRaw(0x5201) { group(0) { string(1, "Numb") } }))
        assertNull(state.current())

        state.accept(update { group(0) { string(1, "Numb") } })
        state.clear()
        assertNull(state.current())
    }

    @Test
    fun clearedTitlesForgetThePreviousSongUntilANewTitleArrives() {
        val state = NowPlayingSongState()
        state.accept(update { group(0) { string(1, "Previous song"); string(12, "Artist") } })
        state.accept(update { group(0) { string(1, "") } })
        assertNull(state.current())
        state.accept(update { group(1) { u8(0, 1) } })
        assertNull(state.current())
        assertEquals(NowPlayingSong("Next song", true),
            state.accept(update { group(0) { string(1, "Next song") } }))
        state.accept(update { group(0) { string(1, "  "); string(12, "Stale artist") } })
        assertNull(state.current())
        assertEquals(NowPlayingSong("After clear", true),
            state.accept(update { group(0) { string(1, "After clear") } }))
    }

    @Test
    fun titleOnlyUpdatesRetainArtistAndPlaybackWhenOtherFieldsAreOmitted() {
        val state = NowPlayingSongState()
        state.accept(update {
            group(0) { string(1, "Track"); string(12, "Artist") }
            group(1) { u8(0, 1) }
        })
        assertEquals(NowPlayingSong("Lyric line — Artist", true),
            state.accept(update { group(0) { string(1, "Lyric line") } }))
        assertNull(state.accept(update { group(0) { u32(4, 180_000L) } }))
        assertEquals(NowPlayingSong("Lyric line — Artist", true), state.current())
    }

    @Test
    fun explicitEmptyArtistClearsItWithoutChangingTitleOrPlayback() {
        val state = NowPlayingSongState()
        state.accept(update { group(0) { string(1, "Track"); string(12, "Artist") } })
        assertEquals(NowPlayingSong("Track", false),
            state.accept(update { group(0) { string(12, "") } }))
        assertEquals(NowPlayingSong("Next line", false),
            state.accept(update { group(0) { string(1, "Next line") } }))
    }

    @Test
    fun completeTrackUpdateReplacesBothTitleAndArtist() {
        val state = NowPlayingSongState()
        state.accept(update { group(0) { string(1, "First track"); string(12, "First artist") } })
        assertEquals(NowPlayingSong("Second track — Second artist", false),
            state.accept(update { group(0) { string(1, "Second track"); string(12, "Second artist") } }))
        assertEquals(NowPlayingSong("Third track", false),
            state.accept(update { group(0) { string(1, "Third track"); string(12, "") } }))
    }

    @Test
    fun textIsShortenedSafely() {
        assertNull(NowPlayingSongState.text("  ", "Artist"))
        assertEquals("Title", NowPlayingSongState.text(" Title ", ""))

        val long = NowPlayingSongState.text("Пісня".repeat(40), "Виконавець")!!
        assertTrue(long.toByteArray(Charsets.UTF_16LE).size <= NowPlayingSongState.MAX_TEXT_BYTES)
        assertEquals(127, long.length)

        // An emoji is never cut in half.
        val emoji = NowPlayingSongState.text("a" + "🎵".repeat(100), null)!!
        assertTrue(emoji.toByteArray(Charsets.UTF_16LE).size <= NowPlayingSongState.MAX_TEXT_BYTES)
        assertTrue(!Character.isHighSurrogate(emoji.last()))
    }
}
