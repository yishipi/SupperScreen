package com.magiccompanion.app

import android.annotation.SuppressLint
import android.view.View
import android.webkit.JavascriptInterface
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebView
import android.webkit.WebViewClient
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import java.io.ByteArrayInputStream
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject

private val terminalBackground = Color(0xFF0C0C0C)
private val terminalForeground = Color(0xFFCCCCCC)
private val terminalWarning = Color(0xFFFFC857)

internal class TerminalBridge(
    private val input: (String) -> Unit,
    private val viewport: (Int, Int) -> Unit,
    private val firstOutput: () -> Unit,
) {
    @JavascriptInterface fun send(text: String) = input(text)
    @JavascriptInterface fun size(columns: Int, rows: Int) = viewport(columns, rows)
    @JavascriptInterface fun outputReady() = firstOutput()
}

@Composable
internal fun TerminalScreen(state: UiState, client: AgentClient, onClose: () -> Unit) {
    if ("terminal.control" in state.permissions) {
        TerminalSession(state, client, onClose)
    } else {
        Panel("PowerShell") {
            Text(if (state.online) "请在电脑「手机与权限」开启「PowerShell 终端」" else "电脑离线")
        }
    }
}

@SuppressLint("SetJavaScriptEnabled")
@Composable
private fun TerminalSession(state: UiState, client: AgentClient, onClose: () -> Unit) {
    val assets = LocalContext.current.assets
    var browser by remember { mutableStateOf<WebView?>(null) }
    var viewport by remember { mutableStateOf<Pair<Int, Int>?>(null) }
    var editor by remember { mutableStateOf<TerminalEditText?>(null) }
    var firstFrameReady by remember { mutableStateOf(false) }
    val html by produceState<String?>(null, assets) {
        val result = withContext(Dispatchers.IO) {
            runCatching {
                fun read(name: String) = assets.open("terminal/$name").bufferedReader().use { it.readText() }
                read("index.html").replace("__CSS__", read("xterm.css"))
                    .replace("__XTERM__", read("xterm.js")).replace("__FIT__", read("addon-fit.js"))
            }
        }
        result.onSuccess { value = it }.onFailure { client.report("无法加载 PowerShell 界面，请关闭后重新打开") }
    }
    val canType = firstFrameReady && state.online && state.terminalRunning && !state.paused
    val sendInput: (String) -> Unit = { text ->
        browser?.evaluateJavascript("terminal.scrollToBottom();", null)
        client.terminalWrite(text)
    }

    // Start the native session even if WebView is still initializing.
    LaunchedEffect(state.online, state.paused) {
        if (state.online && !state.paused && state.terminalId.isEmpty() && state.error.isEmpty())
            client.openTerminal(80, 24)
    }
    LaunchedEffect(state.terminalId, viewport) {
        viewport?.let { (columns, rows) ->
            if (state.terminalRunning) client.resizeTerminal(columns, rows)
        }
    }
    LaunchedEffect(browser, viewport != null) {
        val view = browser ?: return@LaunchedEffect
        if (viewport == null) return@LaunchedEffect
        client.terminalFrames.collect { frame ->
            view.evaluateJavascript("receiveTerminal(${JSONObject.quote(frame.data)},${frame.reset});", null)
        }
    }

    val status = when {
        state.error.isNotEmpty() -> state.error
        !state.online -> "电脑离线"
        state.paused -> "已暂停"
        state.terminalId.isEmpty() -> "正在连接 PowerShell…"
        !state.terminalRunning -> "终端已退出"
        !firstFrameReady -> "正在等待 PowerShell 输出…"
        else -> ""
    }
    val onRestart: (() -> Unit)? = if (state.online && !state.paused && !state.terminalRunning &&
        (state.terminalId.isNotEmpty() || state.error.isNotEmpty()) && html != null
    ) {
        {
            client.clearError()
            client.openTerminal(viewport?.first ?: 80, viewport?.second ?: 24)
        }
    } else null
    val background = if (firstFrameReady) terminalBackground else MaterialTheme.colorScheme.background
    Column(Modifier.fillMaxSize().background(background).padding(horizontal = 6.dp, vertical = 3.dp)) {
        if (firstFrameReady && status.isNotEmpty())
            Text(status, color = terminalWarning, fontSize = 12.sp, maxLines = 2)
        Box(Modifier.weight(1f).fillMaxWidth()) {
            html?.let { page ->
                AndroidView(
                    modifier = Modifier.fillMaxSize(),
                    factory = { context ->
                        WebView(context).apply {
                            alpha = 0f
                            setBackgroundColor(android.graphics.Color.rgb(12, 12, 12))
                            isFocusable = false
                            overScrollMode = View.OVER_SCROLL_NEVER
                            settings.javaScriptEnabled = true
                            settings.allowFileAccess = false
                            settings.allowContentAccess = false
                            settings.blockNetworkLoads = true
                            webViewClient = object : WebViewClient() {
                                override fun shouldOverrideUrlLoading(view: WebView?, request: WebResourceRequest?) = true
                                override fun shouldInterceptRequest(view: WebView?, request: WebResourceRequest?) =
                                    WebResourceResponse("text/plain", "utf-8", ByteArrayInputStream(byteArrayOf()))
                            }
                            val view = this
                            addJavascriptInterface(
                                TerminalBridge(
                                    input = client::terminalWrite,
                                    viewport = { columns, rows -> view.post { viewport = columns to rows } },
                                    firstOutput = {
                                        view.post {
                                            if (browser === view) view.postVisualStateCallback(
                                                0,
                                                object : WebView.VisualStateCallback() {
                                                    override fun onComplete(requestId: Long) {
                                                        if (browser === view) firstFrameReady = true
                                                    }
                                                },
                                            )
                                        }
                                    },
                                ),
                                "SupperTerminal",
                            )
                            browser = this
                            loadDataWithBaseURL("https://terminal.supperscreen.invalid/", page, "text/html", "utf-8", null)
                        }
                    },
                    update = { it.alpha = if (firstFrameReady) 1f else 0f },
                    onRelease = {
                        it.removeJavascriptInterface("SupperTerminal")
                        browser = null
                        it.destroy()
                    },
                )
            }
            // Keep a native IME target without taking space from terminal output.
            AndroidView(
                modifier = Modifier.align(Alignment.BottomStart).size(1.dp),
                factory = { context -> TerminalEditText(context).also { editor = it } },
                update = { view ->
                    view.isEnabled = canType
                    view.send = sendInput
                },
                onRelease = { editor = null },
            )
            if (!firstFrameReady) {
                Column(
                    Modifier.align(Alignment.Center).padding(16.dp),
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.spacedBy(10.dp),
                ) {
                    if (state.online && !state.paused && state.error.isEmpty() &&
                        (state.terminalId.isEmpty() || state.terminalRunning)
                    ) CircularProgressIndicator(Modifier.size(22.dp), strokeWidth = 2.dp)
                    Text(status, color = MaterialTheme.colorScheme.onSurfaceVariant, fontSize = 13.sp)
                    if (onRestart != null)
                        TextButton(onClick = onRestart) { Text("重新打开") }
                }
            }
        }
        if (firstFrameReady) TerminalToolbar(
            enabled = canType,
            send = sendInput,
            onKeyboard = { editor?.openKeyboard() },
            onRestart = onRestart,
            onClose = onClose,
        ) else Spacer(Modifier.height(36.dp))
    }
}

private val toolbarKeys = listOf(
    "Esc" to "\u001b", "Tab" to "\t", "↑" to "\u001b[A", "↓" to "\u001b[B",
    "←" to "\u001b[D", "→" to "\u001b[C", "Ctrl+C" to "\u0003", "↵" to "\r",
)

@Composable
private fun TerminalToolbar(
    enabled: Boolean,
    send: (String) -> Unit,
    onKeyboard: () -> Unit,
    onRestart: (() -> Unit)?,
    onClose: () -> Unit,
) {
    Row(
        Modifier.fillMaxWidth().height(36.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        TerminalButton("键盘", enabled, onKeyboard)
        toolbarKeys.forEach { (label, input) -> TerminalButton(label, enabled) { send(input) } }
        Spacer(Modifier.weight(1f))
        if (onRestart != null) TerminalButton("重新打开", true, onRestart)
        TerminalButton("关闭终端", true, onClose)
    }
}

@Composable
private fun TerminalButton(label: String, enabled: Boolean, onClick: () -> Unit) {
    TextButton(
        onClick = onClick,
        enabled = enabled,
        modifier = Modifier.height(36.dp),
        contentPadding = PaddingValues(horizontal = 6.dp),
        colors = ButtonDefaults.textButtonColors(contentColor = terminalForeground),
    ) { Text(label, fontSize = 12.sp, maxLines = 1) }
}
