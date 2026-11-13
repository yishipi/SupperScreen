package com.magiccompanion.app

import android.Manifest
import android.annotation.SuppressLint
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothSocket
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import android.util.Log
import java.io.Closeable
import java.io.IOException
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.UUID
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

internal fun hasBluetoothPermission(context: Context): Boolean =
    Build.VERSION.SDK_INT < Build.VERSION_CODES.S ||
        context.checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED

@SuppressLint("MissingPermission")
internal fun bluetoothEnabled(context: Context): Boolean =
    hasBluetoothPermission(context) &&
        context.getSystemService(BluetoothManager::class.java)?.adapter?.isEnabled == true

// Pinned TLS and device authorization run inside this RFCOMM tunnel, including on unbonded radios.
internal class BluetoothRelay(context: Context, address: String, scope: CoroutineScope) : Closeable {
    private val device = requireNotNull(context.getSystemService(BluetoothManager::class.java)?.adapter)
        .getRemoteDevice(address)
    private val listener = ServerSocket(0, 1, InetAddress.getByName("127.0.0.1"))
    private var peer: BluetoothSocket? = null
    private var local: Socket? = null
    val url = "wss://127.0.0.1:${listener.localPort}/ws"

    init {
        scope.launch {
            while (isActive && !listener.isClosed) {
                try {
                    listener.accept().use { forward(it) }
                } catch (ex: IOException) {
                    if (!listener.isClosed) Log.d("SupperScreenBluetooth", "Bluetooth tunnel disconnected", ex)
                    // Closing either side lets the existing link backoff reconnect.
                } catch (_: SecurityException) {
                    // Android can revoke the Nearby devices permission while the app is running.
                }
            }
        }
    }

    @SuppressLint("MissingPermission")
    private suspend fun forward(client: Socket) = coroutineScope {
        val bluetooth = device.createInsecureRfcommSocketToServiceRecord(ServiceId)
        bluetooth.use {
            synchronized(this@BluetoothRelay) {
                if (listener.isClosed) return@coroutineScope
                local = client
                peer = bluetooth
            }
            try {
                bluetooth.connect()
                Log.d("SupperScreenBluetooth", "RFCOMM transport connected")
                val outgoing = launch {
                    try {
                        client.getInputStream().copyTo(bluetooth.outputStream)
                    } catch (_: IOException) {
                    } finally {
                        runCatching { bluetooth.close() }
                        runCatching { client.close() }
                    }
                }
                try {
                    bluetooth.inputStream.copyTo(client.getOutputStream())
                } finally {
                    runCatching { client.close() }
                    runCatching { bluetooth.close() }
                    outgoing.join()
                }
            } finally {
                synchronized(this@BluetoothRelay) {
                    local = null
                    peer = null
                }
            }
        }
    }

    @Synchronized
    override fun close() {
        runCatching { listener.close() }
        runCatching { local?.close() }
        runCatching { peer?.close() }
    }

    private companion object {
        val ServiceId: UUID = UUID.fromString("2d2b5b4a-f2e6-4d46-af54-d1ba12b60ef3")
    }
}
