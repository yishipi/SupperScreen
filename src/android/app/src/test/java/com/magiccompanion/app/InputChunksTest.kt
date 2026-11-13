package com.magiccompanion.app

import org.junit.Assert.*
import org.junit.Test

class InputChunksTest {
    @Test
    fun unicodeTextIsNeverSplitBetweenSurrogates() {
        val text = "a".repeat(1023) + "📚" + "中文".repeat(1500)
        val pieces = committedChunks(text)
        assertEquals(text, pieces.joinToString(""))
        assertTrue(
            pieces.all {
                it.length <= 1024 &&
                    !Character.isHighSurrogate(it.last()) &&
                    !Character.isLowSurrogate(it.first())
            }
        )
    }

    @Test
    fun emptyCompositionDoesNotEmitText() {
        assertTrue(committedChunks("").isEmpty())
    }

    @Test
    fun committedChineseTextIsPreserved() {
        assertEquals(listOf("实时中文\n"), committedChunks("实时中文\n"))
    }
}
