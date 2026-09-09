package com.clipshare.core.crypto

import com.clipshare.core.vault.DeviceId
import com.clipshare.core.vault.VaultId
import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.Base64
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec
import kotlinx.serialization.Serializable

private val KEY_PREFIX = "clipshare:vault:key:v1\u0000".toByteArray(Charsets.US_ASCII)
private val AAD_PREFIX = "clipshare:vault:aad:v1\u0000".toByteArray(Charsets.US_ASCII)
private const val GCM_TAG_BITS = 128
private const val GCM_TAG_BYTES = GCM_TAG_BITS / Byte.SIZE_BITS
private const val NONCE_BYTES = 12
internal const val NONCE_PREFIX_BYTES = 4
internal const val FILE_DEK_BYTES = 32
private const val PADDING_BUCKET = 4096
private const val SCHEMA_VERSION = 1
private const val HKDF_FIRST_BLOCK = 1
private const val HEX_BYTE_CHARACTERS = 2
private const val BASE64_QUANTUM = 4
private val LOWERCASE_UUID_PATTERN =
    Regex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")
private val FIELD_PATTERN = Regex("^[a-z][a-z0-9-]{0,31}$")

enum class CryptoPurpose(val wireValue: String, val usesDerivedKey: Boolean = true) {
    FOLDER_METADATA("folder-metadata"),
    ITEM_METADATA("item-metadata"),
    ITEM_PAYLOAD("item-payload"),
    FILE_KEY_WRAP("file-key-wrap"),
    FILE_MANIFEST("file-manifest"),
    FILE_CHUNK("file-chunk", usesDerivedKey = false),
    EVENT_BODY("event-body"),
}

data class NonceAllocation(val prefix: ByteArray, val counter: Long) {
    init {
        require(prefix.size == NONCE_PREFIX_BYTES) { "Nonce prefixes are exactly four bytes." }
        require(counter >= 0) { "Nonce counters cannot be negative." }
    }

    fun nonce(): ByteArray = ByteBuffer.allocate(NONCE_BYTES)
        .order(ByteOrder.BIG_ENDIAN)
        .put(prefix)
        .putLong(counter)
        .array()

    override fun equals(other: Any?): Boolean =
        other is NonceAllocation && prefix.contentEquals(other.prefix) && counter == other.counter

    override fun hashCode(): Int = 31 * prefix.contentHashCode() + counter.hashCode()
}

data class VaultCryptoContext(
    val purpose: CryptoPurpose,
    val vaultId: VaultId,
    val originDeviceId: DeviceId,
    val entityId: String,
    val field: String,
    val keyEpoch: Long,
) {
    init {
        require(LOWERCASE_UUID_PATTERN.matches(entityId)) { "Invalid Vault entity ID." }
        require(FIELD_PATTERN.matches(field)) { "Invalid Vault field name." }
        require(keyEpoch > 0) { "Vault key epochs are positive." }
    }
}

@Serializable
data class VaultCipherEnvelope(
    val purpose: String,
    val vaultId: String,
    val originDeviceId: String,
    val entityId: String,
    val field: String,
    val keyEpoch: Long,
    val nonceCounter: Long,
    val nonce: String,
    val paddedPlaintextBytes: Long,
    val cipherAndTag: String,
)

@Suppress("TooManyFunctions")
object VaultCryptography {
    fun deriveKey(epochSecret: ByteArray, context: VaultCryptoContext): ByteArray {
        require(epochSecret.size == EPOCH_SECRET_BYTES) { "Epoch secrets are exactly 32 bytes." }
        require(context.purpose.usesDerivedKey) { "File chunks use an independent File DEK." }
        val salt = uuidBytes(context.vaultId.value)
        val extract = Mac.getInstance("HmacSHA256")
        extract.init(SecretKeySpec(salt, "HmacSHA256"))
        val prk = extract.doFinal(epochSecret)
        return try {
            val info = bytes(
                KEY_PREFIX,
                lengthPrefixed(context.purpose.wireValue),
                uuidBytes(context.originDeviceId.value),
            )
            val expand = Mac.getInstance("HmacSHA256")
            expand.init(SecretKeySpec(prk, "HmacSHA256"))
            expand.doFinal(bytes(info, byteArrayOf(HKDF_FIRST_BLOCK.toByte())))
        } finally {
            prk.fill(0)
        }
    }

    fun encryptRecord(
        epochSecret: ByteArray,
        context: VaultCryptoContext,
        allocation: NonceAllocation,
        payload: ByteArray,
    ): VaultCipherEnvelope {
        require(context.purpose != CryptoPurpose.FILE_CHUNK) { "Use encryptFileChunk for file bytes." }
        require(allocation.counter > 0) { "Record nonce counters start at one." }
        val plaintext = frame(payload)
        val key = deriveKey(epochSecret, context)
        try {
            return encrypt(key, context, allocation, plaintext)
        } finally {
            key.fill(0)
            plaintext.fill(0)
        }
    }

    fun decryptRecord(
        epochSecret: ByteArray,
        envelope: VaultCipherEnvelope,
    ): ByteArray {
        val context = contextFrom(envelope)
        require(context.purpose != CryptoPurpose.FILE_CHUNK) { "Use decryptFileChunk for file bytes." }
        require(envelope.nonceCounter > 0) { "Record nonce counters start at one." }
        val key = deriveKey(epochSecret, context)
        return try {
            unframe(decrypt(key, context, envelope))
        } finally {
            key.fill(0)
        }
    }

    fun encryptFileChunk(
        fileDek: ByteArray,
        context: VaultCryptoContext,
        allocation: NonceAllocation,
        plaintext: ByteArray,
    ): VaultCipherEnvelope {
        require(context.purpose == CryptoPurpose.FILE_CHUNK) { "File chunk purpose required." }
        require(fileDek.size == FILE_DEK_BYTES) { "File DEKs are exactly 32 bytes." }
        require(plaintext.size <= FileEncryptionMaterial.MAXIMUM_PLAINTEXT_CHUNK_BYTES) {
            "File chunks are bounded to one MiB."
        }
        return encrypt(fileDek, context, allocation, plaintext)
    }

    fun decryptFileChunk(fileDek: ByteArray, envelope: VaultCipherEnvelope): ByteArray {
        require(fileDek.size == FILE_DEK_BYTES) { "File DEKs are exactly 32 bytes." }
        val context = contextFrom(envelope)
        require(context.purpose == CryptoPurpose.FILE_CHUNK) { "File chunk purpose required." }
        return decrypt(fileDek, context, envelope)
    }

    fun aad(context: VaultCryptoContext, counter: Long, paddedLength: Long): ByteArray = bytes(
        AAD_PREFIX,
        u16(SCHEMA_VERSION),
        lengthPrefixed(context.purpose.wireValue),
        uuidBytes(context.vaultId.value),
        uuidBytes(context.originDeviceId.value),
        uuidBytes(context.entityId),
        lengthPrefixed(context.field),
        u64(context.keyEpoch),
        u64(counter),
        u64(paddedLength),
    )

    private fun encrypt(
        key: ByteArray,
        context: VaultCryptoContext,
        allocation: NonceAllocation,
        plaintext: ByteArray,
    ): VaultCipherEnvelope {
        val nonce = allocation.nonce()
        val aad = aad(context, allocation.counter, plaintext.size.toLong())
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(GCM_TAG_BITS, nonce))
        cipher.updateAAD(aad)
        val encrypted = cipher.doFinal(plaintext)
        return VaultCipherEnvelope(
            purpose = context.purpose.wireValue,
            vaultId = context.vaultId.value,
            originDeviceId = context.originDeviceId.value,
            entityId = context.entityId,
            field = context.field,
            keyEpoch = context.keyEpoch,
            nonceCounter = allocation.counter,
            nonce = b64url(nonce),
            paddedPlaintextBytes = plaintext.size.toLong(),
            cipherAndTag = b64url(encrypted),
        )
    }

    private fun decrypt(key: ByteArray, context: VaultCryptoContext, envelope: VaultCipherEnvelope): ByteArray {
        val nonce = decodeB64Url(envelope.nonce)
        require(nonce.size == NONCE_BYTES) { "Invalid nonce." }
        val encrypted = decodeB64Url(envelope.cipherAndTag)
        require(encrypted.size >= GCM_TAG_BYTES) { "Ciphertext must include a 16-byte tag." }
        val ciphertextBytes = encrypted.size - GCM_TAG_BYTES
        require(envelope.paddedPlaintextBytes == ciphertextBytes.toLong()) {
            "Declared plaintext length does not match the ciphertext."
        }
        if (context.purpose == CryptoPurpose.FILE_CHUNK) {
            require(ciphertextBytes <= FileEncryptionMaterial.MAXIMUM_PLAINTEXT_CHUNK_BYTES) {
                "File chunks are bounded to one MiB."
            }
        } else {
            require(ciphertextBytes >= PADDING_BUCKET && ciphertextBytes % PADDING_BUCKET == 0) {
                "Invalid padded record length."
            }
        }
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(GCM_TAG_BITS, nonce))
        cipher.updateAAD(aad(context, envelope.nonceCounter, envelope.paddedPlaintextBytes))
        return cipher.doFinal(encrypted)
    }

    private fun contextFrom(envelope: VaultCipherEnvelope): VaultCryptoContext = VaultCryptoContext(
        purpose = CryptoPurpose.entries.singleOrNull { it.wireValue == envelope.purpose }
            ?: error("Unknown Vault purpose."),
        vaultId = VaultId.parse(envelope.vaultId),
        originDeviceId = DeviceId.parse(envelope.originDeviceId),
        entityId = envelope.entityId,
        field = envelope.field,
        keyEpoch = envelope.keyEpoch,
    )

    private fun frame(payload: ByteArray): ByteArray {
        val required = Math.addExact(Long.SIZE_BYTES, payload.size)
        val buckets = Math.addExact(required, PADDING_BUCKET - 1) / PADDING_BUCKET
        val padded = maxOf(PADDING_BUCKET, Math.multiplyExact(buckets, PADDING_BUCKET))
        return ByteBuffer.allocate(padded)
            .order(ByteOrder.BIG_ENDIAN)
            .putLong(payload.size.toLong())
            .put(payload)
            .array()
    }

    private fun unframe(value: ByteArray): ByteArray {
        try {
            require(value.size >= PADDING_BUCKET && value.size % PADDING_BUCKET == 0) { "Invalid padded plaintext." }
            val buffer = ByteBuffer.wrap(value).order(ByteOrder.BIG_ENDIAN)
            val length = buffer.long
            require(length >= 0 && length <= value.size - Long.SIZE_BYTES) { "Invalid framed plaintext length." }
            val result = ByteArray(length.toInt())
            buffer.get(result)
            while (buffer.hasRemaining()) require(buffer.get() == 0.toByte()) { "Invalid non-zero padding." }
            return result
        } finally {
            value.fill(0)
        }
    }

    private fun uuidBytes(value: String): ByteArray {
        require(LOWERCASE_UUID_PATTERN.matches(value)) {
            "Invalid lowercase UUID."
        }
        return value.replace("-", "")
            .chunked(HEX_BYTE_CHARACTERS)
            .map { it.toInt(radix = 16).toByte() }
            .toByteArray()
    }

    private fun lengthPrefixed(value: String): ByteArray {
        val encoded = value.toByteArray(Charsets.UTF_8)
        require(encoded.size <= UShort.MAX_VALUE.toInt()) { "AAD string too long." }
        return bytes(u16(encoded.size), encoded)
    }

    private fun u16(value: Int): ByteArray = ByteBuffer.allocate(Short.SIZE_BYTES)
        .order(ByteOrder.BIG_ENDIAN)
        .putShort(value.toShort())
        .array()

    private fun u64(value: Long): ByteArray {
        require(value >= 0) { "AAD integers are non-negative." }
        return ByteBuffer.allocate(Long.SIZE_BYTES).order(ByteOrder.BIG_ENDIAN).putLong(value).array()
    }

    private fun bytes(vararg parts: ByteArray): ByteArray = ByteArrayOutputStream().use { output ->
        parts.forEach(output::write)
        output.toByteArray()
    }

    private fun b64url(value: ByteArray): String = Base64.getUrlEncoder().withoutPadding().encodeToString(value)

    private fun decodeB64Url(value: String): ByteArray {
        require(Regex("^[A-Za-z0-9_-]+$").matches(value) && value.length % BASE64_QUANTUM != 1) {
            "Invalid Base64URL."
        }
        val result = Base64.getUrlDecoder().decode(value)
        require(b64url(result) == value) { "Non-canonical Base64URL." }
        return result
    }
}
