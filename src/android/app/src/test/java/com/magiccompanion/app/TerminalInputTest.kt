package com.magiccompanion.app

import org.junit.Assert.assertEquals
import org.junit.Test

class TerminalInputTest {
    @Test
    fun multilinePasteUsesTerminalReturnAndPreservesUnicode() {
        assertEquals("Get-Date\r中文🙂\r", terminalText(StringBuilder("Get-Date\r\n中文🙂\n")))
    }
}
