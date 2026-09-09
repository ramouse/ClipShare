package com.clipshare.platform.android.vault

import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import com.clipshare.core.crypto.EPOCH_SECRET_BYTES
import java.security.KeyStore
import java.util.Base64
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

data class AndroidProtectedSecret(
    val keyAlias: String,
    val nonce: String,
    val cipherAndTag: String,
)

class AndroidVaultKeyProtector(
    private val keyStore: KeyStore = KeyStore.getInstance(ANDROID_KEY_STORE).apply { load(null) },
) {
    fun createWrappingKey(alias: String) {
        require(alias.matches(ALIAS_PATTERN)) { "Invalid Vault Keystore alias." }
        check(!keyStore.containsAlias(alias)) { "A wrapping key already exists for this alias." }
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEY_STORE)
        val specification = KeyGenParameterSpec.Builder(
            alias,
            KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
        )
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
            .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
            .setKeySize(AES_KEY_BITS)
            .setRandomizedEncryptionRequired(true)
            .setUserAuthenticationRequired(false)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.VANILLA_ICE_CREAM) {
            specification.setUnlockedDeviceRequired(true)
        }
        generator.init(specification.build())
        generator.generateKey()
    }

    fun protect(
        alias: String,
        epochSecret: ByteArray,
        aad: ByteArray,
        usedNonces: Set<String>,
    ): AndroidProtectedSecret {
        require(epochSecret.size == EPOCH_SECRET_BYTES) { "Epoch secrets are exactly 32 bytes." }
        val key = requireKey(alias)
        repeat(MAX_NONCE_ATTEMPTS) {
            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(Cipher.ENCRYPT_MODE, key)
            require(cipher.iv.size == GCM_NONCE_BYTES) { "Keystore returned a non-standard GCM nonce." }
            val nonce = b64url(cipher.iv)
            if (nonce !in usedNonces) {
                cipher.updateAAD(aad)
                return AndroidProtectedSecret(alias, nonce, b64url(cipher.doFinal(epochSecret)))
            }
        }
        error("Keystore repeated a wrapper nonce too many times.")
    }

    fun unprotect(protectedSecret: AndroidProtectedSecret, aad: ByteArray): ByteArray {
        val cipher = Cipher.getInstance(TRANSFORMATION)
        val nonce = decodeB64url(protectedSecret.nonce)
        require(nonce.size == GCM_NONCE_BYTES) { "Invalid wrapper nonce." }
        cipher.init(
            Cipher.DECRYPT_MODE,
            requireKey(protectedSecret.keyAlias),
            GCMParameterSpec(GCM_TAG_BITS, nonce),
        )
        cipher.updateAAD(aad)
        return cipher.doFinal(decodeB64url(protectedSecret.cipherAndTag)).also {
            require(it.size == EPOCH_SECRET_BYTES) { "Platform wrapper contained an invalid epoch secret." }
        }
    }

    private fun requireKey(alias: String): SecretKey =
        (keyStore.getKey(alias, null) as? SecretKey) ?: error("Vault wrapping key is unavailable.")

    private fun b64url(value: ByteArray): String = Base64.getUrlEncoder().withoutPadding().encodeToString(value)

    private fun decodeB64url(value: String): ByteArray {
        require(value.matches(BASE64_PATTERN) && value.length % BASE64_QUANTUM != 1) {
            "Invalid wrapper Base64URL."
        }
        return Base64.getUrlDecoder().decode(value).also {
            require(b64url(it) == value) { "Non-canonical Base64URL." }
        }
    }

    private companion object {
        const val ANDROID_KEY_STORE = "AndroidKeyStore"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val MAX_NONCE_ATTEMPTS = 8
        const val AES_KEY_BITS = 256
        const val GCM_TAG_BITS = 128
        const val GCM_NONCE_BYTES = 12
        const val BASE64_QUANTUM = 4
        val ALIAS_PATTERN = Regex("^clipshare\\.vault\\.wrap\\.v1\\.[0-9a-f]{32}$")
        val BASE64_PATTERN = Regex("^[A-Za-z0-9_-]+$")
    }
}
