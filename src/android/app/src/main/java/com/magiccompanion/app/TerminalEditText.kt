package com.magiccompanion.app

import android.content.Context
import android.text.InputType
import android.view.KeyEvent
import android.view.View
import android.view.inputmethod.BaseInputConnection
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputConnection
import android.view.inputmethod.InputConnectionWrapper
import android.view.inputmethod.InputMethodManager
import android.view.inputmethod.TextAttribute
import androidx.appcompat.widget.AppCompatEditText

internal fun terminalText(text: CharSequence): String = text.toString().replace("\r\n", "\r").replace('\n', '\r')

internal class TerminalEditText(context: Context) : AppCompatEditText(context) {
    var send: (String) -> Unit = {}

    init {
        alpha = 0f
        importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
        isCursorVisible = false
        setSingleLine(true)
        inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS
        imeOptions = EditorInfo.IME_ACTION_SEND or EditorInfo.IME_FLAG_NO_EXTRACT_UI or EditorInfo.IME_FLAG_NO_FULLSCREEN
        setOnEditorActionListener { _, action, _ ->
            if (action != EditorInfo.IME_ACTION_SEND) false else {
                sendPendingText()
                send("\r")
                true
            }
        }
    }

    fun openKeyboard() {
        if (!requestFocus()) return
        post {
            val ime = context.getSystemService(InputMethodManager::class.java)
            ime?.showSoftInput(this, InputMethodManager.SHOW_IMPLICIT)
        }
    }

    // The local editor holds IME candidates. Committed text goes directly to the remote terminal.
    override fun onCreateInputConnection(outAttrs: EditorInfo): InputConnection? {
        val target = super.onCreateInputConnection(outAttrs) ?: return null
        return object : InputConnectionWrapper(target, false) {
            override fun commitText(text: CharSequence, newCursorPosition: Int): Boolean = commit(text)

            override fun commitText(text: CharSequence, newCursorPosition: Int, textAttribute: TextAttribute?): Boolean = commit(text)

            override fun replaceText(start: Int, end: Int, text: CharSequence, newCursorPosition: Int, textAttribute: TextAttribute?): Boolean = commit(text)

            override fun finishComposingText(): Boolean {
                val accepted = super.finishComposingText()
                if (accepted) sendPendingText()
                return accepted
            }

            override fun deleteSurroundingText(beforeLength: Int, afterLength: Int): Boolean =
                if (editableText.isEmpty()) sendDelete(beforeLength, afterLength)
                else super.deleteSurroundingText(beforeLength, afterLength)

            override fun deleteSurroundingTextInCodePoints(beforeLength: Int, afterLength: Int): Boolean =
                if (editableText.isEmpty()) sendDelete(beforeLength, afterLength)
                else super.deleteSurroundingTextInCodePoints(beforeLength, afterLength)
        }
    }

    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean {
        if (editableText.isNotEmpty()) return super.onKeyDown(keyCode, event)
        val ctrl = keyCode - KeyEvent.KEYCODE_A + 1
        val sequence = when {
            event.isCtrlPressed && ctrl in 1..26 -> ctrl.toChar().toString()
            keyCode in terminalKeys -> terminalKeys.getValue(keyCode)
            else -> event.unicodeChar.takeIf { it >= 32 && Character.isValidCodePoint(it) }
                ?.let { String(Character.toChars(it)) }
        } ?: return super.onKeyDown(keyCode, event)
        send(sequence)
        return true
    }

    override fun onTextContextMenuItem(id: Int): Boolean {
        val handled = super.onTextContextMenuItem(id)
        if (handled && (id == android.R.id.paste || id == android.R.id.pasteAsPlainText)) sendPendingText()
        return handled
    }

    private fun commit(value: CharSequence): Boolean {
        val committed = terminalText(value)
        val buffer = editableText
        BaseInputConnection.removeComposingSpans(buffer)
        buffer.clear()
        if (committed.isNotEmpty()) send(committed)
        return true
    }

    private fun sendPendingText() {
        if (editableText.isNotEmpty()) commit(editableText)
    }

    private fun sendDelete(before: Int, after: Int): Boolean {
        val keys = "\u007f".repeat(before.coerceIn(0, 64)) + "\u001b[3~".repeat(after.coerceIn(0, 64))
        if (keys.isNotEmpty()) send(keys)
        return true
    }

    private companion object {
        val terminalKeys = mapOf(
            KeyEvent.KEYCODE_ESCAPE to "\u001b", KeyEvent.KEYCODE_TAB to "\t",
            KeyEvent.KEYCODE_ENTER to "\r", KeyEvent.KEYCODE_NUMPAD_ENTER to "\r",
            KeyEvent.KEYCODE_DEL to "\u007f", KeyEvent.KEYCODE_FORWARD_DEL to "\u001b[3~",
            KeyEvent.KEYCODE_DPAD_UP to "\u001b[A", KeyEvent.KEYCODE_DPAD_DOWN to "\u001b[B",
            KeyEvent.KEYCODE_DPAD_RIGHT to "\u001b[C", KeyEvent.KEYCODE_DPAD_LEFT to "\u001b[D",
            KeyEvent.KEYCODE_MOVE_HOME to "\u001b[H", KeyEvent.KEYCODE_MOVE_END to "\u001b[F",
        )
    }
}
