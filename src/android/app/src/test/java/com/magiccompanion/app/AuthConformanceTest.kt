package com.magiccompanion.app

import java.security.KeyFactory
import java.security.MessageDigest
import java.security.Signature
import java.security.spec.X509EncodedKeySpec
import java.util.Base64
import java.util.Properties
import org.junit.Assert.*
import org.junit.Test

class AuthConformanceTest {
    @Test
    fun sharedDeviceSignatureMatchesActualAndroidTranscript() {
        val vector =
            Properties().apply {
                AuthConformanceTest::class
                    .java
                    .classLoader!!
                    .getResourceAsStream("auth-vector.properties")!!
                    .use(::load)
            }
        val publicKey = Base64.getDecoder().decode(vector.getProperty("publicKey"))
        val id =
            MessageDigest.getInstance("SHA-256").digest(publicKey).joinToString("") {
                "%02x".format(it.toInt() and 255)
            }
        assertEquals(vector.getProperty("deviceId"), id)
        val transcript =
            authenticationTranscript(
                vector.getProperty("hostId"),
                vector.getProperty("linkId"),
                vector.getProperty("nonce"),
                id,
            )
        assertTrue(
            Signature.getInstance("SHA256withECDSA")
                .apply {
                    initVerify(
                        KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(publicKey))
                    )
                    update(transcript)
                }
                .verify(Base64.getDecoder().decode(vector.getProperty("signature")))
        )
    }
}
