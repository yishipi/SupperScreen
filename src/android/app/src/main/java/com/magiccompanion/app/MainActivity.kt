package com.magiccompanion.app

import android.Manifest
import android.app.Activity
import android.bluetooth.BluetoothAdapter
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Bundle
import android.os.Build
import android.view.WindowManager
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.foundation.*
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.google.mlkit.vision.barcode.BarcodeScanning
import com.google.mlkit.vision.common.InputImage
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        WindowCompat.setDecorFitsSystemWindows(window, false)
        window.attributes = window.attributes.apply {
            layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES
        }
        hideSystemBars()
        setContent {
            MaterialTheme(
                colorScheme =
                    lightColorScheme(
                        primary = Color(0xFF245BC2),
                        onPrimary = Color.White,
                        secondaryContainer = Color(0xFFE6EFFF),
                        onSecondaryContainer = Color(0xFF234878),
                        background = Color(0xFFF2F4F8),
                        surface = Color.White,
                        onSurface = Color(0xFF18243A),
                        onSurfaceVariant = Color(0xFF54637A),
                        outlineVariant = Color(0xFFDCE3EE),
                    )
            ) {
                Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
                    CompanionScreen()
                }
            }
        }
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) hideSystemBars()
    }

    private fun hideSystemBars() {
        WindowCompat.getInsetsController(window, window.decorView).apply {
            systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            hide(WindowInsetsCompat.Type.systemBars())
        }
    }
}

private enum class Page { Home, Terminal, Performance, Audio, Apps, Scripts, Settings }
private val headerHeight = 34.dp
private val screenVerticalPadding = 3.dp
private val pageGap = 4.dp

@Composable
private fun CompanionScreen(client: AgentClient = viewModel()) {
    val state by client.state.collectAsStateWithLifecycle()
    var scanning by remember { mutableStateOf(false) }
    var page by remember { mutableStateOf(Page.Home) }
    val context = LocalContext.current
    val lifecycle = LocalLifecycleOwner.current
    val cameraPermission =
        rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
            if (granted) scanning = true else client.report("需要相机权限才能扫描配对码")
        }
    val scan = {
        if (ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) ==
            PackageManager.PERMISSION_GRANTED) scanning = true
        else cameraPermission.launch(Manifest.permission.CAMERA)
    }
    val useBluetooth = {
        client.retryBluetooth()
        if (!state.paired || !client.hasBluetoothAddress()) scan()
    }
    val enableRadio =
        rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
            if (result.resultCode == Activity.RESULT_OK && bluetoothEnabled(context)) useBluetooth()
            else client.report("请打开手机蓝牙后重试")
        }
    val checkRadio = {
        if (bluetoothEnabled(context)) useBluetooth()
        else runCatching { enableRadio.launch(Intent(BluetoothAdapter.ACTION_REQUEST_ENABLE)) }
            .onFailure { client.report("请在手机系统设置中打开蓝牙") }
        Unit
    }
    val nearbyPermission =
        rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
            if (granted) checkRadio() else client.report("使用蓝牙需要「附近的设备」权限")
        }
    val selectBluetooth = {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S && !hasBluetoothPermission(context))
            nearbyPermission.launch(Manifest.permission.BLUETOOTH_CONNECT)
        else checkRadio()
    }
    DisposableEffect(lifecycle) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_STOP) client.foreground(false)
            else if (event == Lifecycle.Event.ON_START) client.foreground(true)
        }
        lifecycle.lifecycle.addObserver(observer)
        onDispose { lifecycle.lifecycle.removeObserver(observer) }
    }
    fun navigate(next: Page) {
        scanning = false
        if (next == page) return
        if (page == Page.Terminal) client.closeTerminal()
        client.clearError()
        page = next
    }
    val back = { if (scanning) scanning = false else navigate(Page.Home) }
    BackHandler(enabled = scanning || page != Page.Home, onBack = back)
    val density = LocalDensity.current
    val cutouts = LocalView.current.rootWindowInsets?.displayCutout?.boundingRects
    val verticalInsets = WindowInsets.safeDrawing.only(WindowInsetsSides.Vertical)
    val systemSides = WindowInsets.systemBars.union(WindowInsets.waterfall).only(WindowInsetsSides.Horizontal)
    val cutoutSides = WindowInsets.displayCutout.only(WindowInsetsSides.Horizontal)
    val headerTop = verticalInsets.getTop(density) + with(density) { screenVerticalPadding.roundToPx() }
    val headerHeightPx = with(density) { headerHeight.roundToPx() }
    val contentTop = headerTop + headerHeightPx + with(density) { pageGap.roundToPx() }
    // A side camera only needs padding for content bands at its vertical position.
    fun sidesFor(top: Int, height: Int) =
        if (cutouts == null || cutouts.any { it.top < top + height && it.bottom > top }) cutoutSides
        else WindowInsets(0)

    Column(
        Modifier.fillMaxSize().windowInsetsPadding(verticalInsets).windowInsetsPadding(systemSides)
            .padding(horizontal = 8.dp, vertical = screenVerticalPadding),
        verticalArrangement = Arrangement.spacedBy(pageGap),
    ) {
        Box(Modifier.windowInsetsPadding(sidesFor(headerTop, headerHeightPx))) {
            CompanionHeader(state, page, ::navigate) {
                client.captureScreens { count ->
                    Toast.makeText(context, "已保存 $count 张截图到电脑", Toast.LENGTH_SHORT).show()
                }
            }
        }
        BoxWithConstraints(Modifier.weight(1f).fillMaxWidth()) {
            val contentHeight = with(density) { maxHeight.roundToPx() }
            Box(Modifier.fillMaxSize().windowInsetsPadding(sidesFor(contentTop, contentHeight))) {
                when {
                    scanning -> Scanner(
                        onCode = { navigate(Page.Home); client.pair(it) },
                        onClose = { scanning = false },
                        onError = client::report,
                    )
                    page == Page.Terminal -> TerminalScreen(state, client) { navigate(Page.Home) }
                    page == Page.Settings -> SettingsScreen(state, scan, selectBluetooth) {
                        navigate(Page.Home)
                        client.unpair()
                    }
                    state.paired && state.pairingCode.isEmpty() -> Dashboard(state, client, page)
                    else -> Column(Modifier.verticalScroll(rememberScrollState())) {
                        Panel("连接电脑") {
                            if (state.pairingCode.isNotEmpty())
                                Text("请在电脑确认验证码：${state.pairingCode}", color = MaterialTheme.colorScheme.primary)
                            else {
                                Text("扫描电脑端的 SupperScreen 配对码。")
                                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                    Button(onClick = scan) { Text("扫码配对") }
                                    if (!state.bluetoothReady)
                                        OutlinedButton(onClick = selectBluetooth) { Text("启用蓝牙") }
                                }
                            }
                            if (state.notice.isNotEmpty()) Text(state.notice, fontSize = 12.sp)
                        }
                    }
                }
            }
        }
    }
    if ((page != Page.Terminal || scanning) && state.error.isNotEmpty())
        AlertDialog(
            onDismissRequest = client::clearError,
            text = { Text(state.error) },
            confirmButton = { TextButton(onClick = client::clearError) { Text("确定") } },
        )
}

@Composable
private fun CompanionHeader(state: UiState, page: Page, navigate: (Page) -> Unit, onCapture: () -> Unit) {
    BoxWithConstraints(Modifier.fillMaxWidth().height(headerHeight)) {
        val hostWidth = if (maxWidth < 580.dp) 84.dp else 148.dp
        Row(
            Modifier.fillMaxSize(),
            horizontalArrangement = Arrangement.spacedBy(3.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(
                state.host,
                Modifier.widthIn(max = hostWidth).padding(horizontal = 3.dp),
                style = MaterialTheme.typography.titleSmall,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            Row(
                Modifier.weight(1f).horizontalScroll(rememberScrollState()),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                HeaderButton("主页", page == Page.Home) { navigate(Page.Home) }
                HeaderButton("PowerShell", page == Page.Terminal, state.paired) { navigate(Page.Terminal) }
                HeaderButton("性能", page == Page.Performance, state.paired) { navigate(Page.Performance) }
                HeaderButton("音量", page == Page.Audio, state.paired && state.supportsAudio) { navigate(Page.Audio) }
                HeaderButton("应用", page == Page.Apps, state.paired && state.supportsApps) { navigate(Page.Apps) }
                HeaderButton("脚本", page == Page.Scripts, state.paired) { navigate(Page.Scripts) }
                HeaderButton("截屏", false, state.online && !state.paused && !state.busy, onCapture)
            }
            ConnectionBadge(state)
            HeaderButton("设置", page == Page.Settings) { navigate(Page.Settings) }
        }
    }
}

@Composable
private fun HeaderButton(label: String, selected: Boolean, enabled: Boolean = true, onClick: () -> Unit) {
    TextButton(
        onClick = onClick,
        enabled = enabled,
        modifier = Modifier.height(headerHeight),
        contentPadding = PaddingValues(horizontal = 7.dp, vertical = 0.dp),
        colors = ButtonDefaults.textButtonColors(
            containerColor = if (selected) MaterialTheme.colorScheme.secondaryContainer else Color.Transparent,
        ),
    ) { Text(label, fontSize = 12.sp, maxLines = 1) }
}

@Composable
private fun ConnectionBadge(state: UiState) {
    val colors = MaterialTheme.colorScheme
    val label = when {
        state.online && state.locked -> "${state.transport} · 锁屏"
        state.online && state.paused -> "${state.transport} · 已暂停"
        state.online -> "${state.transport} · 已连接"
        else -> "未连接"
    }
    Surface(
        shape = CircleShape,
        color = if (state.online) colors.secondaryContainer else colors.surface,
        contentColor = if (state.online) colors.onSecondaryContainer else colors.onSurfaceVariant,
    ) {
        Row(
            Modifier.padding(horizontal = 8.dp, vertical = 3.dp),
            horizontalArrangement = Arrangement.spacedBy(6.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Box(
                Modifier.size(6.dp).background(
                    if (state.online) colors.primary else colors.onSurfaceVariant,
                    CircleShape,
                )
            )
            Text(label, style = MaterialTheme.typography.labelMedium, lineHeight = 14.sp, maxLines = 1)
        }
    }
}

@Composable
private fun Dashboard(state: UiState, client: AgentClient, page: Page) {
    Row(Modifier.fillMaxSize(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        TodoCard(state, client, "待办事项", Modifier.weight(0.43f).fillMaxHeight())
        Box(Modifier.weight(0.57f).fillMaxHeight()) {
            when (page) {
                Page.Audio -> ToolPanel(state.supportsAudio, "音量与媒体") { AudioCard(state, client) }
                Page.Apps -> ToolPanel(state.supportsApps, "应用") { AppCard(state, client) }
                Page.Scripts -> ScriptsScreen(state, client)
                else -> SystemCard(state, "电脑状态", Modifier.fillMaxSize())
            }
        }
    }
}

@Composable
private fun ToolPanel(available: Boolean, title: String, content: @Composable () -> Unit) {
    if (available)
        Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) { content() }
    else
        Panel(title) { Text("当前连接暂不支持此功能") }
}

@Composable
internal fun Panel(
    title: String,
    modifier: Modifier = Modifier,
    headerAction: @Composable () -> Unit = {},
    content: @Composable ColumnScope.() -> Unit,
) {
    OutlinedCard(
        modifier.fillMaxWidth(),
        shape = RoundedCornerShape(12.dp),
        colors = CardDefaults.outlinedCardColors(containerColor = MaterialTheme.colorScheme.surface),
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outlineVariant),
    ) {
        Column(Modifier.padding(10.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Text(title, Modifier.weight(1f), fontSize = 14.sp, fontWeight = FontWeight.SemiBold)
                headerAction()
            }
            content()
        }
    }
}

@Composable
private fun TodoCard(state: UiState, client: AgentClient, title: String, modifier: Modifier) {
    val canComplete = state.online && "todos.write" in state.permissions && "todos.read" in state.permissions
    Panel(title, modifier, headerAction = {
        Text(
            "${state.todos.count { !it.completed }} 项未完成", fontSize = 11.sp,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }) {
        if (!state.online)
            Text("离线 · 最近同步 ${state.todoTime.take(19).replace('T', ' ')}", fontSize = 10.sp)
        else if (!canComplete)
            Text("请在电脑开启「手机完成待办」", fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
        LazyColumn(Modifier.weight(1f).fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(1.dp)) {
            if (state.todos.isEmpty()) item {
                Text("当前没有待办", fontSize = 13.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            items(state.todos, key = { it.id }) { item ->
                val accent = item.color?.let { Color(android.graphics.Color.parseColor(it)) }
                    ?: MaterialTheme.colorScheme.primary
                val enabled = canComplete && !state.busy && !item.completed
                val background = item.backgroundColor?.let { Color(android.graphics.Color.parseColor(it)) }
                    ?: if (item.color != null) accent.copy(alpha = if (item.completed) 0.04f else 0.08f) else Color.Transparent
                Row(
                    Modifier.fillMaxWidth().heightIn(min = 34.dp)
                        .clip(RoundedCornerShape(6.dp))
                        .background(background)
                        .toggleable(item.completed, enabled = enabled, role = Role.Checkbox) { checked ->
                            if (checked) client.completeTodo(item.id)
                        }
                        .padding(horizontal = 6.dp, vertical = 2.dp),
                    horizontalArrangement = Arrangement.spacedBy(6.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Checkbox(
                        checked = item.completed,
                        enabled = enabled,
                        onCheckedChange = null,
                        colors = CheckboxDefaults.colors(
                            checkedColor = accent,
                            uncheckedColor = accent,
                            disabledCheckedColor = accent.copy(alpha = 0.45f),
                        ),
                    )
                    Text(
                        item.title,
                        modifier = Modifier.weight(1f),
                        color = item.textColor?.let { Color(android.graphics.Color.parseColor(it)) }
                            ?: if (item.completed) MaterialTheme.colorScheme.onSurfaceVariant else MaterialTheme.colorScheme.onSurface,
                        textDecoration = if (item.completed) TextDecoration.LineThrough else null,
                        fontSize = 12.sp, lineHeight = 16.sp, maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
                HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.55f))
            }
        }
    }
}

@Composable
private fun AppCard(state: UiState, client: AgentClient) {
    Panel("应用") {
        if ("app.launch" !in state.permissions) {
            Text("在电脑「手机与权限」开启「启动允许的应用」", fontSize = 12.sp)
            return@Panel
        }
        if (state.apps.isEmpty())
            Text("在电脑「应用启动」中添加程序", fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
        state.apps.forEach { app ->
            Surface(
                Modifier.fillMaxWidth(),
                color = MaterialTheme.colorScheme.background,
                shape = RoundedCornerShape(8.dp),
            ) {
                Row(
                    Modifier.padding(horizontal = 8.dp, vertical = 4.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    if (app.icon != null) Image(app.icon.asImageBitmap(), null, Modifier.size(28.dp))
                    else Surface(
                        Modifier.size(28.dp), shape = CircleShape,
                        color = MaterialTheme.colorScheme.secondaryContainer,
                    ) {
                        Box(contentAlignment = Alignment.Center) {
                            Text(app.name.take(1), fontSize = 12.sp, color = MaterialTheme.colorScheme.primary)
                        }
                    }
                    Text(
                        app.name, Modifier.weight(1f), fontSize = 13.sp,
                        maxLines = 2, overflow = TextOverflow.Ellipsis,
                    )
                    TextButton(
                        onClick = { client.launch(app) },
                        enabled = app.launchAvailable && state.online && !state.paused && !state.busy,
                        contentPadding = PaddingValues(horizontal = 6.dp),
                    ) { Text(if (app.launchAvailable) "启动" else "不可用", fontSize = 12.sp) }
                }
            }
        }
    }
}

@Composable
private fun AudioCard(state: UiState, client: AgentClient) {
    if ("audio.control" !in state.permissions) {
        Panel("音量与媒体") { Text("在电脑「手机与权限」开启「音量和媒体」", fontSize = 12.sp) }
        return
    }
    var volume by remember(state.volume) { mutableFloatStateOf(state.volume) }
    val enabled = state.online && state.audioAvailable && (!state.paused || state.lockedAudio) &&
        !state.busy && "audio.control" in state.permissions
    Panel("音量与媒体", headerAction = {
        TextButton(onClick = { client.mute(!state.muted) }, enabled = enabled) {
            Text(if (state.muted) "取消静音" else "静音", fontSize = 12.sp)
        }
    }) {
        Text(
            if (state.audioAvailable) "${(volume * 100).toInt()}%" else "音频设备不可用",
            fontSize = if (state.audioAvailable) 24.sp else 13.sp,
            fontWeight = FontWeight.SemiBold,
            color = MaterialTheme.colorScheme.primary,
        )
        Slider(
            value = volume,
            onValueChange = { volume = it },
            onValueChangeFinished = { client.volume(volume) },
            enabled = enabled,
        )
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            OutlinedButton(
                onClick = { client.media("previous") }, enabled = enabled,
                modifier = Modifier.weight(1f), contentPadding = PaddingValues(horizontal = 4.dp),
            ) { Text("上一首", fontSize = 12.sp) }
            Button(
                onClick = { client.media("playPause") }, enabled = enabled,
                modifier = Modifier.weight(1.2f), contentPadding = PaddingValues(horizontal = 4.dp),
            ) { Text("播放 / 暂停", fontSize = 12.sp) }
            OutlinedButton(
                onClick = { client.media("next") }, enabled = enabled,
                modifier = Modifier.weight(1f), contentPadding = PaddingValues(horizontal = 4.dp),
            ) { Text("下一首", fontSize = 12.sp) }
        }
    }
}

private fun gib(value: Long) = "%.1f".format(value / 1073741824.0)

private fun transferRate(bytes: Long?): String = when {
    bytes == null -> "—"
    bytes >= 1048576 -> "%.1f MiB/s".format(bytes / 1048576.0)
    else -> "%.1f KiB/s".format(bytes / 1024.0)
}

@Composable
private fun SystemCard(state: UiState, title: String, modifier: Modifier = Modifier) {
    Panel(title, modifier, headerAction = {
        if (!state.online) Text("离线", fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }) {
        if (state.ramTotal == 0L) {
            Text("等待电脑性能数据", fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            return@Panel
        }
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            UsageMeter(
                "CPU", "${"%.0f".format(state.cpu)}%", (state.cpu / 100).toFloat(),
                Modifier.weight(1f),
            )
            UsageMeter(
                "内存", "${gib(state.ramUsed)} / ${gib(state.ramTotal)} GiB",
                (state.ramUsed.toDouble() / state.ramTotal.coerceAtLeast(1)).toFloat(),
                Modifier.weight(1f),
            )
        }
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            RateTile("磁盘 I/O", "读", state.diskRead, "写", state.diskWrite, Modifier.weight(1f))
            RateTile("网络吞吐", "↓", state.networkReceive, "↑", state.networkSend, Modifier.weight(1f))
        }
        Text("磁盘 · 已用 / 总量", fontSize = 11.sp, lineHeight = 15.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
        if (state.disks.isEmpty()) Text("可用 ${gib(state.diskFree)} GiB", fontSize = 12.sp)
        else {
            LazyColumn(
                Modifier.weight(1f).fillMaxWidth(),
                verticalArrangement = Arrangement.spacedBy(4.dp),
                contentPadding = PaddingValues(bottom = 2.dp),
            ) {
                items(state.disks, key = { it.name }) { disk ->
                    UsageMeter(
                        disk.name, "${gib(disk.usedBytes)} / ${gib(disk.totalBytes)} GiB",
                        (disk.usedBytes.toDouble() / disk.totalBytes.coerceAtLeast(1)).toFloat(),
                        Modifier.fillMaxWidth().heightIn(min = 22.dp),
                    )
                }
            }
        }
    }
}

@Composable
private fun UsageMeter(label: String, value: String, progress: Float, modifier: Modifier) {
    Column(modifier, verticalArrangement = Arrangement.spacedBy(2.dp)) {
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(4.dp)) {
            Text(label, fontSize = 11.sp, lineHeight = 15.sp)
            Text(
                value, Modifier.weight(1f), fontSize = 11.sp, lineHeight = 15.sp,
                textAlign = androidx.compose.ui.text.style.TextAlign.End,
                maxLines = 1, overflow = TextOverflow.Ellipsis,
            )
        }
        LinearProgressIndicator(
            progress = { progress.coerceIn(0f, 1f) },
            modifier = Modifier.fillMaxWidth().height(3.dp),
            gapSize = 0.dp, drawStopIndicator = {},
        )
    }
}

@Composable
private fun RateTile(
    title: String, first: String, firstRate: Long?, second: String, secondRate: Long?,
    modifier: Modifier,
) {
    Surface(modifier, shape = RoundedCornerShape(8.dp), color = MaterialTheme.colorScheme.background) {
        Column(Modifier.padding(horizontal = 8.dp, vertical = 5.dp), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(title, fontSize = 10.sp, lineHeight = 14.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                Text("$first ${transferRate(firstRate)}", Modifier.weight(1f), fontSize = 10.sp, lineHeight = 14.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text("$second ${transferRate(secondRate)}", Modifier.weight(1f), fontSize = 10.sp, lineHeight = 14.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
    }
}

@Composable
private fun SettingsScreen(
    state: UiState,
    onScan: () -> Unit,
    onBluetooth: () -> Unit,
    onUnpair: () -> Unit,
) {
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) {
        Panel("设备连接") {
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
                Button(onClick = onScan) { Text("扫描配对码", fontSize = 12.sp) }
                if (!state.bluetoothReady)
                    OutlinedButton(onClick = onBluetooth) { Text("启用蓝牙", fontSize = 12.sp) }
                if (state.paired)
                    TextButton(
                        onClick = onUnpair,
                        colors = ButtonDefaults.textButtonColors(contentColor = MaterialTheme.colorScheme.error),
                    ) { Text("解除配对", fontSize = 12.sp) }
            }
        }
    }
}

@androidx.annotation.OptIn(androidx.camera.core.ExperimentalGetImage::class)
@Composable
private fun Scanner(onCode: (String) -> Unit, onClose: () -> Unit, onError: (String) -> Unit) {
    val context = LocalContext.current
    val lifecycle = LocalLifecycleOwner.current
    val preview = remember { PreviewView(context) }
    val executor = remember { Executors.newSingleThreadExecutor() }
    val scanner = remember { BarcodeScanning.getClient() }
    val consumed = remember { AtomicBoolean(false) }
    DisposableEffect(Unit) {
        val future = ProcessCameraProvider.getInstance(context)
        var provider: ProcessCameraProvider? = null
        future.addListener(
            {
                runCatching {
                        provider = future.get()
                        val cameraPreview =
                            Preview.Builder().build().also {
                                it.surfaceProvider = preview.surfaceProvider
                            }
                        val analysis =
                            ImageAnalysis.Builder()
                                .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                                .build()
                        analysis.setAnalyzer(executor) { frame ->
                            @androidx.camera.core.ExperimentalGetImage val image = frame.image
                            if (image == null || consumed.get()) frame.close()
                            else
                                scanner
                                    .process(
                                        InputImage.fromMediaImage(
                                            image,
                                            frame.imageInfo.rotationDegrees,
                                        )
                                    )
                                    .addOnSuccessListener { barcodes ->
                                        barcodes
                                            .firstNotNullOfOrNull { it.rawValue }
                                            ?.let {
                                                if (consumed.compareAndSet(false, true)) onCode(it)
                                            }
                                    }
                                    .addOnFailureListener { onError("无法读取二维码，请对准电脑的配对码") }
                                    .addOnCompleteListener { frame.close() }
                        }
                        provider!!.bindToLifecycle(
                            lifecycle,
                            CameraSelector.DEFAULT_BACK_CAMERA,
                            cameraPreview,
                            analysis,
                        )
                    }
                    .onFailure {
                        onError("相机启动失败，请检查相机权限")
                        onClose()
                    }
            },
            ContextCompat.getMainExecutor(context),
        )
        onDispose {
            consumed.set(true)
            provider?.unbindAll()
            scanner.close()
            executor.shutdown()
        }
    }
    Box(Modifier.fillMaxSize()) {
        AndroidView(factory = { preview }, modifier = Modifier.fillMaxSize())
        Text(
            "扫描电脑控制台的配对二维码",
            color = Color.White,
            modifier = Modifier.align(Alignment.TopCenter).padding(24.dp),
        )
        Button(
            onClick = onClose,
            modifier = Modifier.align(Alignment.BottomCenter).padding(24.dp),
        ) {
            Text("取消")
        }
    }
}
