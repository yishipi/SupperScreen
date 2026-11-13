package com.magiccompanion.app

import android.app.Application
import android.bluetooth.BluetoothAdapter
import android.content.ContentValues
import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import androidx.lifecycle.AndroidViewModel
import java.nio.charset.StandardCharsets
import java.security.*
import java.security.cert.X509Certificate
import java.security.spec.ECGenParameterSpec
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec
import javax.net.ssl.SSLContext
import javax.net.ssl.X509TrustManager
import kotlinx.coroutines.*
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import okhttp3.*
import okhttp3.HttpUrl.Companion.toHttpUrl
import org.json.JSONArray
import org.json.JSONObject

internal fun json(vararg values: Pair<String, Any?>) =
    JSONObject().apply { values.forEach { (key, value) -> put(key, value ?: JSONObject.NULL) } }

internal fun JSONArray.objects(): List<JSONObject> = (0 until length()).map { getJSONObject(it) }

internal fun JSONArray.strings(): List<String> = (0 until length()).map { getString(it) }

internal fun b64(bytes: ByteArray): String = Base64.encodeToString(bytes, Base64.NO_WRAP)

internal fun digest(bytes: ByteArray) = MessageDigest.getInstance("SHA-256").digest(bytes)

private val todoHexColor = Regex("#[0-9a-fA-F]{6}")

data class Todo(val id: String, val title: String, val completed: Boolean, val color: String? = null, val textColor: String? = null, val backgroundColor: String? = null)

data class DiskCapacity(val name: String, val usedBytes: Long, val totalBytes: Long)

data class AllowedApp(
    val id: String,
    val name: String,
    val launchAvailable: Boolean,
    val icon: Bitmap? = null,
)

data class UiState(
    val paired: Boolean = false,
    val online: Boolean = false,
    val transport: String = "离线",
    val bluetoothReady: Boolean = false,
    val supportsApps: Boolean = true,
    val supportsAudio: Boolean = true,
    val host: String = "尚未配对电脑",
    val pairingCode: String = "",
    val permissions: Set<String> = emptySet(),
    val todos: List<Todo> = emptyList(),
    val todoTime: String = "",
    val todoRevision: Long = -1,
    val apps: List<AllowedApp> = emptyList(),
    val cpu: Double = 0.0,
    val ramUsed: Long = 0,
    val ramTotal: Long = 0,
    val diskFree: Long = 0,
    val disks: List<DiskCapacity> = emptyList(),
    val diskRead: Long? = null,
    val diskWrite: Long? = null,
    val networkReceive: Long? = null,
    val networkSend: Long? = null,
    val volume: Float = 0f,
    val muted: Boolean = false,
    val audioAvailable: Boolean = false,
    val error: String = "",
    val paused: Boolean = false,
    val locked: Boolean = false,
    val lockedAudio: Boolean = false,
    val busy: Boolean = false,
    val notice: String = "",
    val templates: List<JSONObject> = emptyList(),
    val jobs: Map<String, JSONObject> = emptyMap(),
    val terminalId: String = "",
    val terminalRunning: Boolean = false,
)

private class TodoCache(context: Context) : SQLiteOpenHelper(context, "todo-cache.db", null, 1) {
    override fun onCreate(db: SQLiteDatabase) {
        db.execSQL(
            "CREATE TABLE snapshots(host TEXT PRIMARY KEY,revision INTEGER NOT NULL,payload TEXT NOT NULL)"
        )
    }

    override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) = Unit

    @Synchronized
    fun load(host: String): JSONObject? =
        readableDatabase.rawQuery("SELECT payload FROM snapshots WHERE host=?", arrayOf(host)).use {
            if (it.moveToFirst()) JSONObject(it.getString(0)) else null
        }

    @Synchronized
    fun save(host: String, value: JSONObject) {
        val db = writableDatabase
        db.beginTransaction()
        try {
            val old = load(host)
            if (old == null || value.getLong("revision") >= old.getLong("revision"))
                db.insertWithOnConflict(
                    "snapshots",
                    null,
                    ContentValues().apply {
                        put("host", host)
                        put("revision", value.getLong("revision"))
                        put("payload", value.toString())
                    },
                    SQLiteDatabase.CONFLICT_REPLACE,
                )
            db.setTransactionSuccessful()
        } finally {
            db.endTransaction()
        }
    }

    @Synchronized
    fun clear() {
        writableDatabase.delete("snapshots", null, null)
    }
}

class AgentClient(application: Application) : AndroidViewModel(application) {
    private val prefs = application.getSharedPreferences("settings", Context.MODE_PRIVATE)
    private val cache = TodoCache(application)
    private var bluetoothRelay: BluetoothRelay? = null
    private val keys = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
    private val alias = "magic-companion-device-v1"
    private val publicKey: ByteArray
    private val deviceId: String
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val mutable = MutableStateFlow(UiState())
    private val capturingScreens = java.util.concurrent.atomic.AtomicBoolean()
    val state = mutable.asStateFlow()
    @Volatile
    private var config: JSONObject? =
        prefs.getString("host", null)?.let { runCatching { JSONObject(it) }.getOrNull() }

    private data class Link(
        val url: String,
        val kind: String,
        var socket: WebSocket? = null,
        @Volatile var session: String? = null,
        @Volatile var bluetoothCapabilities: Set<String>? = null,
    )

    private fun Link.supports(capability: String): Boolean =
        kind != "bluetooth" || bluetoothCapabilities?.contains(capability) == true

    private val links = ConcurrentHashMap<String, Link>()

    private data class Pending(val link: Link, val completion: CompletableDeferred<JSONObject>)
    private class RequestRejected(message: String) : java.io.IOException(message)

    private val pending = ConcurrentHashMap<String, Pending>()
    @Volatile private var selected: Link? = null
    @Volatile private var pairClaim: String? = null
    private var discovery: NsdManager.DiscoveryListener? = null
    private val nsd = application.getSystemService(Context.NSD_SERVICE) as NsdManager
    private var running = true
    @Volatile private var visible = true

    init {
        mutable.update { it.copy(bluetoothReady = bluetoothEnabled(getApplication())) }
        if (!keys.containsAlias(alias))
            KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore")
                .apply {
                    initialize(
                        KeyGenParameterSpec.Builder(
                                alias,
                                KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY,
                            )
                            .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
                            .setDigests(KeyProperties.DIGEST_SHA256)
                            .build()
                    )
                }
                .generateKeyPair()
        publicKey = keys.getCertificate(alias).publicKey.encoded
        deviceId = digest(publicKey).joinToString("") { "%02x".format(it.toInt() and 255) }
        config?.let { host ->
            mutable.update { it.copy(paired = true, host = host.optString("hostName")) }
            cache.load(host.getString("hostId"))?.let(::applyTodos)
            connectCandidates(host)
            startDiscovery()
        }
        scope.launch {
            var nextBluetoothLookup = 0L
            while (isActive) {
                delay(1000)
                if (visible) retryBluetooth()
                val active = selected
                if (active != null && !capturingScreens.get())
                    runCatching { requestOn(active, "session.ping", JSONObject(), 2500) }
                        .onFailure { active.socket?.cancel() }
                val now = android.os.SystemClock.elapsedRealtime()
                val host = config
                if (visible && mutable.value.bluetoothReady && active != null && host != null &&
                    !hasBluetoothAddress() && now >= nextBluetoothLookup
                ) {
                    nextBluetoothLookup = now + 10000
                    learnBluetoothAddress(active, host)
                }
            }
        }
    }

    fun clearError() {
        mutable.update { it.copy(error = "") }
    }

    fun report(message: String) {
        mutable.update { it.copy(error = message.take(300)) }
    }

    fun pair(raw: String) {
        runCatching {
                val host = JSONObject(raw)
                require(
                    host.getString("type") == "magic-companion-pair" &&
                        host.getInt("protocolVersion") == 1
                ) {
                    "二维码不是本应用的配对码"
                }
                require(
                    java.time.Instant.parse(host.getString("expiresAt"))
                        .isAfter(java.time.Instant.now())
                ) {
                    "二维码已过期，请在电脑重新生成"
                }
                require(host.getString("fingerprint").matches(Regex("sha256:[A-Za-z0-9_-]{43}"))) {
                    "电脑身份格式无效"
                }
                require(Base64.decode(host.getString("secret"), Base64.DEFAULT).size == 32)
                require(host.getJSONArray("candidates").length() in 1..8)
                host.getJSONArray("candidates").objects().forEach {
                    validateUrl(it.getString("url"))
                }
                disconnect()
                config = host
                pairClaim = null
                mutable.value = UiState(
                    host = host.optString("hostName"),
                    notice = "连接电脑后，请在电脑确认配对",
                    bluetoothReady = bluetoothEnabled(getApplication()),
                )
                connectCandidates(host)
            }
            .onFailure { report(it.message ?: "二维码无效") }
    }

    private fun validateUrl(url: String) {
        val parsed = url.replaceFirst("wss:", "https:").toHttpUrl()
        require(
            url.startsWith("wss://") &&
                parsed.username.isEmpty() &&
                parsed.password.isEmpty() &&
                parsed.encodedPath == "/ws" &&
                parsed.query == null &&
                parsed.fragment == null
        ) {
            "连接地址必须是 wss://地址:端口/ws"
        }
    }

    private fun connectCandidates(host: JSONObject) {
        host.getJSONArray("candidates").objects().forEach {
            addLink(it.getString("url"), it.getString("transport"))
        }
        connectBluetooth(host)
    }

    fun retryBluetooth() {
        val ready = bluetoothEnabled(getApplication())
        mutable.update { if (it.bluetoothReady == ready) it else it.copy(bluetoothReady = ready) }
        if (ready) config?.let(::connectBluetooth)
    }

    fun hasBluetoothAddress(): Boolean =
        BluetoothAdapter.checkBluetoothAddress(config?.optString("bluetoothAddress"))

    @Synchronized
    private fun connectBluetooth(host: JSONObject) {
        val address = host.optString("bluetoothAddress")
        if (host !== config || bluetoothRelay != null ||
            !BluetoothAdapter.checkBluetoothAddress(address) || !bluetoothEnabled(getApplication()))
            return
        runCatching {
            BluetoothRelay(getApplication(), address, scope).also { relay ->
                bluetoothRelay = relay
                addLink(relay.url, "bluetooth")
            }
        }.onFailure { report("蓝牙连接未就绪，请检查手机蓝牙后重试") }
    }

    @Synchronized
    private fun stopBluetooth() {
        bluetoothRelay?.let { relay ->
            links.remove(relay.url)?.socket?.cancel()
            relay.close()
        }
        bluetoothRelay = null
    }

    private suspend fun learnBluetoothAddress(link: Link, host: JSONObject) {
        if (link.kind == "bluetooth") return
        val response = runCatching { requestOn(link, "session.hello") }.getOrNull() ?: return
        val address = response.optJSONObject("result")?.optString("bluetoothAddress") ?: return
        if (host !== config || !BluetoothAdapter.checkBluetoothAddress(address)) return
        host.put("bluetoothAddress", address)
        prefs.edit().putString("host", host.toString()).apply()
        connectBluetooth(host)
    }

    private fun addLink(url: String, kind: String) {
        if (links.containsKey(url)) return
        if (links.size >= 8) {
            // DHCP may leave old QR candidates behind when a new LAN address is discovered.
            val stale = links.values.firstOrNull { it.kind == "lan" && it.session == null } ?: return
            if (links.remove(stale.url, stale)) stale.socket?.cancel()
        }
        val link = Link(url, kind)
        if (links.putIfAbsent(url, link) != null) return
        scope.launch {
            var backoff = 1000L
            while (isActive && running && links[url] === link) {
                val ended = CompletableDeferred<Unit>()
                open(link, ended)
                ended.await()
                if (links[url] !== link) break
                synchronized(this@AgentClient) {
                    if (pairClaim == url) pairClaim = null
                }
                link.session = null
                chooseLink()
                delay(backoff)
                backoff = if (mutable.value.online) 1000 else (backoff * 2).coerceAtMost(15000)
            }
        }
    }

    private fun httpClient(): OkHttpClient {
        val pin = config?.getString("fingerprint") ?: error("未设置电脑身份")
        val trust =
            object : X509TrustManager {
                override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()

                override fun checkClientTrusted(
                    chain: Array<out X509Certificate>?,
                    authType: String?,
                ) {
                    throw java.security.cert.CertificateException("不接受客户端 TLS 身份")
                }

                override fun checkServerTrusted(
                    chain: Array<out X509Certificate>?,
                    authType: String?,
                ) {
                    val cert =
                        chain?.firstOrNull()
                            ?: throw java.security.cert.CertificateException("缺少电脑证书")
                    cert.checkValidity()
                    val actual =
                        "sha256:" +
                            Base64.encodeToString(
                                digest(cert.publicKey.encoded),
                                Base64.URL_SAFE or Base64.NO_WRAP or Base64.NO_PADDING,
                            )
                    if (!MessageDigest.isEqual(pin.toByteArray(), actual.toByteArray()))
                        throw java.security.cert.CertificateException("电脑身份不匹配，请在电脑重新配对")
                }
            }
        val tls = SSLContext.getInstance("TLS").apply { init(null, arrayOf(trust), SecureRandom()) }
        return OkHttpClient.Builder()
            .sslSocketFactory(tls.socketFactory, trust)
            .connectTimeout(3, TimeUnit.SECONDS)
            .readTimeout(0, TimeUnit.SECONDS)
            .pingInterval(5, TimeUnit.SECONDS)
            .build()
    }

    private fun authentication(challenge: JSONObject, url: String, pairing: Boolean): JSONObject? {
        val host = config ?: error("未配对")
        require(
            challenge.getInt("protocolVersion") == 1 &&
                challenge.getString("hostId") == host.getString("hostId")
        ) {
            "电脑身份不匹配"
        }
        val canonical =
            authenticationTranscript(
                challenge.getString("hostId"),
                challenge.getString("linkId"),
                challenge.getString("nonce"),
                deviceId,
            )
        val signature =
            Signature.getInstance("SHA256withECDSA")
                .apply {
                    initSign(keys.getKey(alias, null) as PrivateKey)
                    update(canonical)
                }
                .sign()
        return json(
                "type" to "authenticate",
                "deviceId" to deviceId,
                "publicKey" to b64(publicKey),
                "signature" to b64(signature),
                "deviceName" to "${Build.MANUFACTURER} ${Build.MODEL}",
            )
            .apply {
                if (pairing && host.has("secret"))
                    synchronized(this@AgentClient) {
                        if (pairClaim != null && pairClaim != url) return null
                        run {
                            pairClaim = url
                            put("pairSessionId", host.getString("pairSessionId"))
                            put(
                                "pairProof",
                                b64(
                                    Mac.getInstance("HmacSHA256")
                                        .apply {
                                            init(
                                                SecretKeySpec(
                                                    Base64.decode(
                                                        host.getString("secret"),
                                                        Base64.DEFAULT,
                                                    ),
                                                    "HmacSHA256",
                                                )
                                            )
                                        }
                                        .doFinal(canonical)
                                ),
                            )
                            mutable.update {
                                it.copy(
                                    pairingCode =
                                        digest(canonical)
                                            .joinToString("") { b ->
                                                "%02X".format(b.toInt() and 255)
                                            }
                                            .take(6)
                                )
                            }
                        }
                    }
            }
    }

    private fun open(link: Link, ended: CompletableDeferred<Unit>) {
        val client =
            runCatching { httpClient() }
                .getOrElse {
                    ended.complete(Unit)
                    return
                }
        link.socket =
            client.newWebSocket(
                Request.Builder().url(link.url).build(),
                object : WebSocketListener() {
                    override fun onMessage(webSocket: WebSocket, text: String) {
                        if (links[link.url] !== link) return
                        if (text.toByteArray().size > 65536) {
                            webSocket.cancel()
                            return
                        }
                        runCatching {
                                val value = JSONObject(text)
                                when (value.optString("type")) {
                                    "challenge" -> {
                                        val auth = authentication(value, link.url, true)
                                        if (auth == null) webSocket.cancel()
                                        else webSocket.send(auth.toString())
                                    }
                                    "authenticated" -> {
                                        link.session = value.getString("sessionId")
                                        val host = config ?: return
                                        host.remove("secret")
                                        host.remove("pairSessionId")
                                        host.remove("expiresAt")
                                        prefs.edit().putString("host", host.toString()).apply()
                                        mutable.update {
                                            it.copy(
                                                paired = true,
                                                host = value.optString("hostName"),
                                                permissions =
                                                    value
                                                        .getJSONArray("permissions")
                                                        .strings()
                                                        .toSet(),
                                                pairingCode = "",
                                                notice = "",
                                                error = "",
                                            )
                                        }
                                        startDiscovery()
                                        chooseLink()
                                        scope.launch {
                                            learnBluetoothAddress(link, host)
                                        }
                                    }
                                    "auth.error" -> {
                                        if (value.optString("code") == "DEVICE_REVOKED") forget()
                                        else if (!mutable.value.online && (pairClaim == null || pairClaim == link.url))
                                            report(value.optString("message"))
                                        webSocket.close(1008, "认证结束")
                                    }
                                    else ->
                                        if (value.optString("messageType") == "response") {
                                            val id = value.optString("requestId")
                                            pending.remove(id)?.completion?.complete(value)
                                        } else if (value.optString("messageType") == "event")
                                            handleEvent(
                                                link,
                                                value.getString("eventType"),
                                                value.getJSONObject("payload"),
                                            )
                                }
                            }
                            .onFailure {
                                report("电脑消息格式或身份验证失败")
                                webSocket.cancel()
                            }
                    }

                    override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                        webSocket.close(code, reason)
                    }

                    override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                        finish()
                    }

                    override fun onFailure(
                        webSocket: WebSocket,
                        t: Throwable,
                        response: Response?,
                    ) {
                        finish()
                    }

                    private fun finish() {
                        link.session = null
                        link.bluetoothCapabilities = null
                        pending.entries
                            .filter { it.value.link === link }
                            .forEach { (id, value) ->
                                if (pending.remove(id, value))
                                    value.completion.completeExceptionally(
                                        java.io.IOException("连接已中断")
                                    )
                            }
                        chooseLink()
                        ended.complete(Unit)
                        client.connectionPool.evictAll()
                        client.dispatcher.executorService.shutdown()
                    }
                },
            )
    }

    @Synchronized
    private fun chooseLink() {
        val next =
            links.values
                .filter { it.session != null }
                .sortedBy {
                    when (it.kind) {
                        "bluetooth" -> 3
                        "usb" -> 1
                        else -> 2
                    }
                }
                .firstOrNull()
        if (next !== selected) {
            selected = next
            if (next != null) scope.launch { subscribe(next) }
        }
        mutable.update {
            it.copy(
                online = next != null,
                supportsApps = next?.supports("apps") ?: true,
                supportsAudio = next?.supports("audio") ?: true,
                transport =
                    when (next?.kind) {
                        "usb" -> "USB"
                        "lan" -> "Wi-Fi"
                        "bluetooth" -> "蓝牙"
                        else -> "离线"
                    },
            )
        }
    }

    private suspend fun requestOn(
        link: Link,
        action: String,
        payload: JSONObject = JSONObject(),
        timeout: Long = 6000,
        id: String = UUID.randomUUID().toString(),
    ): JSONObject {
        val session = link.session ?: throw java.io.IOException("电脑未连接")
        val answer = CompletableDeferred<JSONObject>()
        pending[id] = Pending(link, answer)
        try {
            val sent =
                link.socket?.send(
                    json(
                            "protocolVersion" to 1,
                            "messageType" to "request",
                            "sessionId" to session,
                            "deviceId" to deviceId,
                            "requestId" to id,
                            "action" to action,
                            "payload" to payload,
                        )
                        .toString()
                ) == true
            if (!sent) throw java.io.IOException("请求未发送")
            val response = withTimeoutOrNull(timeout) { answer.await() }
                ?: throw java.io.IOException("电脑响应超时，请检查连接")
            if (response.getString("status") != "ok")
                throw RequestRejected(response.getJSONObject("error").getString("message"))
            return response
        } finally {
            pending.remove(id)
        }
    }

    private suspend fun request(action: String, payload: JSONObject = JSONObject(), timeout: Long = 6000): JSONObject {
        val link = selected ?: throw java.io.IOException("电脑离线，暂时无法操作")
        val id = UUID.randomUUID().toString()
        try {
            return requestOn(link, action, payload, timeout = timeout, id = id)
        } catch (ex: Exception) {
            if (ex is CancellationException || ex is RequestRejected) throw ex
            if (action == "todo.change") {
                val current = selected ?: throw java.io.IOException("连接已中断，请重新连接后检查待办")
                return requestOn(current, action, payload, id = id)
            }
            if (action in setOf("app.launch", "task.run", "media.command", "screen.capture")) {
                val recovered =
                    runCatching {
                            selected?.let {
                                requestOn(it, "request.status", json("requestId" to id))
                                    .getJSONObject("result")
                                    .optJSONObject("result")
                            }
                        }
                        .getOrNull()
                if (recovered != null) {
                    if (recovered.optString("status") == "ok") return recovered
                    throw java.io.IOException(
                        recovered.optJSONObject("error")?.optString("message") ?: "操作失败"
                    )
                }
                throw java.io.IOException("操作结果未知，未自动重复执行；请检查电脑或任务状态")
            }
            throw ex
        }
    }

    private suspend fun subscribe(link: Link) {
        if (link.kind == "bluetooth" && link.bluetoothCapabilities == null) {
            val capabilities = runCatching {
                requestOn(link, "session.hello").getJSONObject("result")
                    .getJSONArray("capabilities").strings().toSet()
            }.getOrNull()
            if (capabilities != null) {
                link.bluetoothCapabilities = capabilities
                chooseLink()
            }
        }
        val permissions = mutable.value.permissions
        suspend fun get(action: String, apply: (JSONObject) -> Unit) {
            runCatching { requestOn(link, action).getJSONObject("result") }.onSuccess(apply)
        }
        if ("todos.read" in permissions)
            get("todo.subscribe") { applyTodos(it.getJSONObject("snapshot")) }
        if (visible && "system.read" in permissions) get("system.metrics.subscribe", ::applyMetrics)
        if ("tasks.read" in permissions) refreshTemplates()
        if (selected !== link) return
        if (link.supports("audio") && visible && "audio.control" in permissions)
            get("audio.state.subscribe", ::applyAudio)
        if (link.supports("apps") && "app.launch" in permissions && selected === link) refreshApps()
    }

    private fun handleEvent(link: Link, type: String, payload: JSONObject) {
        when (type) {
            "todo.snapshot" -> if ("todos.read" in mutable.value.permissions) applyTodos(payload)
            "system.metrics" -> applyMetrics(payload)
            "audio.state" -> if (link === selected && "audio.control" in mutable.value.permissions) applyAudio(payload)
            "app.catalog.changed" -> if (link === selected && "app.launch" in mutable.value.permissions)
                scope.launch { refreshApps() }
            "permission.changed" -> {
                val permissions = payload.getJSONArray("permissions").strings().toSet()
                if ("todos.read" !in permissions) {
                    cache.clear()
                    mutable.update {
                        it.copy(todos = emptyList(), todoTime = "", todoRevision = -1)
                    }
                }
                if ("terminal.control" !in permissions) stopTerminalPolling()
                mutable.update { it.copy(permissions = permissions) }
                selected?.let { scope.launch { subscribe(it) } }
            }
            "device.revoked" -> forget()
            "control.paused" -> mutable.update {
                it.copy(
                    paused = payload.optBoolean("paused"),
                    locked = payload.optBoolean("locked"),
                    lockedAudio = payload.optBoolean("lockedAudio"),
                )
            }
            "task.updated" -> applyJob(payload)
        }
    }

    @Synchronized
    private fun applyTodos(value: JSONObject) {
        if (!value.has("lastSyncedAt"))
            value.put("lastSyncedAt", java.time.Instant.now().toString())
        val revision = value.getLong("revision")
        if (revision < mutable.value.todoRevision) return
        val todos =
            value.getJSONArray("items").objects().map {
                fun optionalColor(key: String) = it.optString(key).takeIf(todoHexColor::matches)
                Todo(it.getString("id"), it.getString("title"), it.getBoolean("completed"), optionalColor("color"),
                    optionalColor("textColor"), optionalColor("backgroundColor"))
            }
        require(todos.size <= 100)
        config?.optString("hostId")?.let { cache.save(it, value) }
        mutable.update {
            it.copy(
                todos = todos,
                todoRevision = revision,
                todoTime = value.getString("lastSyncedAt"),
            )
        }
    }

    private fun applyMetrics(v: JSONObject) {
        val disks = v.optJSONArray("disks")?.objects()?.map { disk ->
            DiskCapacity(disk.getString("name"), disk.getLong("usedBytes"), disk.getLong("totalBytes"))
        }.orEmpty()
        val io = v.optJSONObject("diskIo")
        val network = v.optJSONObject("network")
        fun rate(section: JSONObject?, key: String): Long? =
            section?.takeUnless { it.isNull(key) }?.getLong(key)
        mutable.update {
            it.copy(
                cpu = v.getDouble("cpuPercent"),
                ramUsed = v.getLong("ramUsedBytes"),
                ramTotal = v.getLong("ramTotalBytes"),
                diskFree = v.getLong("diskFreeBytes"),
                disks = disks,
                diskRead = rate(io, "readBytesPerSecond"),
                diskWrite = rate(io, "writeBytesPerSecond"),
                networkReceive = rate(network, "receivedBytesPerSecond"),
                networkSend = rate(network, "sentBytesPerSecond"),
            )
        }
    }

    private fun applyAudio(v: JSONObject) {
        mutable.update {
            it.copy(
                volume = v.getDouble("level").toFloat(),
                muted = v.getBoolean("muted"),
                audioAvailable = v.getBoolean("available"),
            )
        }
    }

    private suspend fun refreshApps() {
        runCatching {
                request("app.catalog.get")
                    .getJSONObject("result")
                    .getJSONArray("items")
                    .objects()
                    .map {
                        AllowedApp(
                            it.getString("appId"),
                            it.getString("displayName"),
                            it.optBoolean("launchAvailable", true),
                            mutable.value.apps
                                .firstOrNull { app -> app.id == it.getString("appId") }
                                ?.icon,
                        )
                    }
            }
            .onSuccess { apps ->
                mutable.update { it.copy(apps = apps) }
                for (app in apps.filter { it.icon == null }) {
                    runCatching {
                            val raw =
                                request("app.icon.get", json("iconId" to app.id))
                                    .getJSONObject("result")
                                    .optString("dataBase64")
                            if (raw.isNotEmpty() && raw != "null") {
                                val bytes = Base64.decode(raw, Base64.DEFAULT)
                                BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
                            } else null
                        }
                        .getOrNull()
                        ?.let { bitmap ->
                            mutable.update {
                                it.copy(
                                    apps =
                                        it.apps.map { value ->
                                            if (value.id == app.id) value.copy(icon = bitmap)
                                            else value
                                        }
                                )
                            }
                        }
                }
            }
    }

    private suspend fun refreshTemplates() {
        runCatching { request("task.templates").getJSONObject("result") }
            .onSuccess { value ->
                mutable.update {
                    it.copy(templates = value.getJSONArray("items").objects())
                }
            }
    }

    private val activeActions = java.util.concurrent.atomic.AtomicInteger()

    private fun action(feedback: Boolean = true, block: suspend () -> Unit) {
        scope.launch {
            if (feedback) {
                activeActions.incrementAndGet()
                mutable.update { it.copy(busy = true, notice = "正在执行…") }
            }
            var succeeded = false
            try {
                block()
                succeeded = true
            } catch (ex: Exception) {
                if (ex is CancellationException) throw ex
                report(ex.message ?: "操作未完成")
            } finally {
                if (feedback && activeActions.decrementAndGet() == 0)
                    mutable.update {
                        it.copy(busy = false, notice = if (succeeded) "操作已完成" else "操作未完成")
                    }
            }
        }
    }

    fun refresh() {
        selected?.let { scope.launch { subscribe(it) } }
    }

    fun completeTodo(id: String) = action {
        val response = request(
            "todo.change",
            json("operation" to "setCompleted", "id" to id, "completed" to true),
        )
        applyTodos(response.getJSONObject("result"))
    }

    fun foreground(visible: Boolean) {
        this.visible = visible
        action(false) {
            links.values
                .filter { it.session != null }
                .forEach { requestOn(it, "session.visibility", json("foreground" to visible)) }
            if (visible) {
                retryBluetooth()
                startDiscovery()
                refresh()
            }
        }
    }

    fun volume(value: Float) = action {
        applyAudio(
            request("audio.volume.set", json("level" to value.coerceIn(0f, 1f)))
                .getJSONObject("result")
        )
    }

    fun mute(value: Boolean) = action {
        applyAudio(request("audio.mute.set", json("muted" to value)).getJSONObject("result"))
    }

    fun media(command: String) = action { request("media.command", json("command" to command)) }

    fun launch(app: AllowedApp) = action { request("app.launch", json("appId" to app.id)) }

    fun captureScreens(onSaved: (Int) -> Unit) = action {
        if ("screen.capture" !in mutable.value.permissions) {
            report("请在电脑「手机与权限」开启「保存电脑截屏」")
            return@action
        }
        if (!capturingScreens.compareAndSet(false, true)) return@action
        try {
            val count = request("screen.capture", timeout = 30_000).getJSONObject("result").getInt("count")
            withContext(Dispatchers.Main) { onSaved(count) }
        } finally {
            capturingScreens.set(false)
        }
    }

    data class TerminalFrame(val data: String, val reset: Boolean)
    private val terminalChannel = Channel<TerminalFrame>(16)
    val terminalFrames = terminalChannel.receiveAsFlow()
    private var terminalPolling: Job? = null
    private var terminalSequence = 0L
    private val terminalInput = Channel<String>(128)
    private val terminalGate = Mutex()

    init {
        scope.launch {
            for (first in terminalInput) terminalGate.withLock {
                val id = mutable.value.terminalId
                if (id.isEmpty()) return@withLock
                val text = buildString {
                    append(first)
                    while (length < 2000) append(terminalInput.tryReceive().getOrNull() ?: break)
                }
                try {
                    request("terminal.write", json("id" to id, "sequence" to ++terminalSequence, "text" to text))
                } catch (ex: Exception) {
                    if (ex is CancellationException) throw ex
                    report(ex.message ?: "终端输入失败，请关闭后重新打开")
                    stopTerminalPolling()
                }
            }
        }
    }

    fun openTerminal(columns: Int, rows: Int) = action(false) {
        terminalGate.withLock {
            stopTerminalPolling()
            while (terminalChannel.tryReceive().isSuccess) { }
            val result = request("terminal.open", json("columns" to columns.coerceIn(20, 300), "rows" to rows.coerceIn(5, 120))).getJSONObject("result")
            val id = result.getString("id")
            terminalSequence = result.optLong("inputSequence")
            mutable.update { it.copy(terminalId = id, terminalRunning = result.getBoolean("running")) }
            terminalPolling = scope.launch {
                var offset = 0L
                try {
                    while (isActive && mutable.value.terminalId == id) {
                        if (!mutable.value.online || !visible) { delay(400); continue }
                        val chunk = request("terminal.read", json("id" to id, "offset" to offset)).getJSONObject("result")
                        val data = chunk.getString("data")
                        if (data.isNotEmpty() || chunk.getBoolean("reset"))
                            terminalChannel.send(TerminalFrame(data, chunk.getBoolean("reset")))
                        offset = chunk.getLong("offset")
                        mutable.update { it.copy(terminalRunning = chunk.getBoolean("running")) }
                        if (!chunk.getBoolean("running") && data.isEmpty()) break
                        delay(if (data.isEmpty()) 250 else 80)
                    }
                } catch (ex: Exception) {
                    if (ex is CancellationException) throw ex
                    report(ex.message ?: "终端连接已断开，请关闭后重新打开")
                    mutable.update { it.copy(terminalRunning = false) }
                }
            }
        }
    }

    fun terminalWrite(text: String) {
        val state = mutable.value
        if (text.isEmpty() || !state.terminalRunning || !state.online || state.paused) return
        committedChunks(text, 2000).forEach {
            if (!terminalInput.trySend(it).isSuccess) report("终端输入繁忙，请稍后重试")
        }
    }

    fun resizeTerminal(columns: Int, rows: Int) = action(false) {
        val id = mutable.value.terminalId
        if (id.isNotEmpty()) request("terminal.resize", json("id" to id, "columns" to columns.coerceIn(20, 300), "rows" to rows.coerceIn(5, 120)))
    }

    fun closeTerminal() = action(false) {
        terminalGate.withLock {
            stopTerminalPolling()
            if (mutable.value.online && "terminal.control" in mutable.value.permissions) request("terminal.close")
        }
    }

    private fun stopTerminalPolling() {
        terminalPolling?.cancel(); terminalPolling = null
        while (terminalInput.tryReceive().isSuccess) { }
        mutable.update { it.copy(terminalId = "", terminalRunning = false) }
    }

    fun runTask(id: String, parameters: JSONObject) = action {
        applyJob(
            request("task.run", json("templateId" to id, "params" to parameters))
                .getJSONObject("result")
        )
    }

    fun refreshJob(id: String) = action {
        applyJob(request("task.status.get", json("jobId" to id)).getJSONObject("result"))
    }

    fun cancelJob(id: String) = action {
        applyJob(request("task.cancel", json("jobId" to id)).getJSONObject("result"))
    }

    private fun applyJob(job: JSONObject) {
        val templateId = job.getString("templateId")
        mutable.update { state ->
            val previous = state.jobs[templateId]
            if (previous != null) {
                if (previous.optString("startedAt") > job.optString("startedAt")) return@update state
                if (previous.optString("jobId") == job.optString("jobId") &&
                    previous.optString("state") != "running" && job.optString("state") == "running"
                ) return@update state
            }
            state.copy(jobs = state.jobs + (templateId to job))
        }
    }

    @Synchronized
    private fun startDiscovery() {
        if (discovery != null || !mutable.value.paired) return
        val listener =
            object : NsdManager.DiscoveryListener {
                override fun onDiscoveryStarted(type: String) = Unit

                override fun onServiceFound(service: NsdServiceInfo) {
                    if (service.serviceType == "_mcompanion._tcp.")
                        @Suppress("DEPRECATION")
                        nsd.resolveService(
                            service,
                            object : NsdManager.ResolveListener {
                                override fun onResolveFailed(info: NsdServiceInfo, code: Int) = Unit

                                override fun onServiceResolved(info: NsdServiceInfo) {
                                    val id = info.attributes["hostId"]?.toString(Charsets.UTF_8)
                                    @Suppress("DEPRECATION") val address = info.host?.hostAddress
                                    if (
                                        id == config?.optString("hostId") &&
                                            address != null &&
                                            !address.contains(":")
                                    )
                                        addLink("wss://$address:${info.port}/ws", "lan")
                                }
                            },
                        )
                }

                override fun onServiceLost(info: NsdServiceInfo) = Unit

                override fun onDiscoveryStopped(type: String) = Unit

                override fun onStartDiscoveryFailed(type: String, code: Int) {
                    discovery = null
                }

                override fun onStopDiscoveryFailed(type: String, code: Int) = Unit
            }
        discovery = listener
        runCatching {
            nsd.discoverServices("_mcompanion._tcp.", NsdManager.PROTOCOL_DNS_SD, listener)
        }.onFailure { discovery = null }
    }

    fun unpair() {
        val host = config ?: return
        val link = selected
        scope.launch {
            if (link != null) runCatching { requestOn(link, "session.unpair", timeout = 1500) }
            if (config === host) forget()
        }
    }

    private fun forget() {
        stopTerminalPolling()
        disconnect()
        config = null
        prefs.edit().remove("host").apply()
        cache.clear()
        mutable.value = UiState(
            notice = "配对已解除，请在电脑生成新二维码",
            bluetoothReady = bluetoothEnabled(getApplication()),
        )
    }

    private fun disconnect() {
        stopBluetooth()
        discovery?.let { runCatching { nsd.stopServiceDiscovery(it) } }
        discovery = null
        val previousLinks = links.values.toList()
        links.clear()
        pairClaim = null
        previousLinks.forEach { it.socket?.cancel() }
        selected = null
        pending.values.forEach { it.completion.cancel() }
        pending.clear()
    }

    override fun onCleared() {
        running = false
        disconnect()
        scope.cancel()
        cache.close()
        super.onCleared()
    }
}
