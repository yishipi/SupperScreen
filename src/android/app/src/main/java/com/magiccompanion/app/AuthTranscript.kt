package com.magiccompanion.app

internal fun authenticationTranscript(
    hostId: String,
    linkId: String,
    nonce: String,
    deviceId: String,
): ByteArray = "1\n$hostId\n$linkId\n$nonce\n$deviceId".toByteArray(Charsets.UTF_8)
