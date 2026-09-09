package com.clipshare.core.vault

import java.util.Base64
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

private val BASE64URL_PATTERN = Regex("^[A-Za-z0-9_-]+$")
private val FIELD_PATTERN = Regex("^[a-z][a-z0-9-]{0,31}$")
private val PURPOSES = setOf(
    "folder-metadata",
    "item-metadata",
    "item-payload",
    "file-key-wrap",
    "file-manifest",
    "file-chunk",
    "event-body",
    "epoch-wrapper",
)
private const val SCHEMA_VERSION = 1
private const val GCM_NONCE_BYTES = 12
private const val GCM_TAG_BYTES = 16
private const val MAXIMUM_FILE_CHUNK_BYTES = 1_048_576L
private const val PADDING_BUCKET_BYTES = 4096L
private const val BASE64_QUANTUM = 4

@Serializable
data class VaultEnvelopeContract(
    val schemaVersion: Int,
    val algorithm: String,
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

object VaultContractCodec {
    private val json = Json {
        ignoreUnknownKeys = false
        isLenient = false
        explicitNulls = true
    }

    fun decodeEnvelope(value: String): VaultEnvelopeContract {
        rejectDuplicateTopLevelProperties(value)
        val envelope = json.decodeFromString<VaultEnvelopeContract>(value)
        require(envelope.schemaVersion == SCHEMA_VERSION) { "Unsupported Vault envelope version." }
        require(envelope.algorithm == "A256GCM") { "Unsupported Vault algorithm." }
        require(envelope.purpose in PURPOSES) { "Unknown Vault purpose." }
        VaultId.parse(envelope.vaultId)
        DeviceId.parse(envelope.originDeviceId)
        validateUuid(envelope.entityId)
        require(FIELD_PATTERN.matches(envelope.field)) { "Invalid field name." }
        require(envelope.keyEpoch > 0) { "Invalid key epoch." }
        require(envelope.nonceCounter >= 0) { "Invalid nonce counter." }
        if (envelope.purpose != "file-chunk" && envelope.purpose != "epoch-wrapper") {
            require(envelope.nonceCounter > 0) { "Record nonce counters start at one." }
        }
        require(decodeCanonicalBase64Url(envelope.nonce).size == GCM_NONCE_BYTES) { "Invalid nonce." }
        val encrypted = decodeCanonicalBase64Url(envelope.cipherAndTag)
        require(encrypted.size >= GCM_TAG_BYTES) { "Ciphertext must include a 16-byte tag." }
        val plaintextBytes = encrypted.size.toLong() - GCM_TAG_BYTES
        require(envelope.paddedPlaintextBytes == plaintextBytes) {
            "Declared plaintext length does not match the ciphertext."
        }
        if (envelope.purpose == "file-chunk") {
            require(envelope.paddedPlaintextBytes in 0..MAXIMUM_FILE_CHUNK_BYTES) { "Invalid chunk length." }
        } else {
            require(
                envelope.paddedPlaintextBytes >= PADDING_BUCKET_BYTES &&
                    envelope.paddedPlaintextBytes % PADDING_BUCKET_BYTES == 0L,
            ) { "Vault record plaintext must use 4 KiB buckets." }
        }
        return envelope
    }

    @Suppress("CyclomaticComplexMethod")
    private fun rejectDuplicateTopLevelProperties(value: String) {
        val seen = mutableSetOf<String>()
        var objectDepth = 0
        var arrayDepth = 0
        var stringStart = -1
        var escaped = false
        var propertyName = false
        var expectingProperty = false
        value.forEachIndexed { index, character ->
            if (stringStart >= 0) {
                when {
                    escaped -> escaped = false
                    character == '\\' -> escaped = true
                    character == '"' -> {
                        if (propertyName) {
                            val decoded = json.decodeFromString<String>(value.substring(stringStart, index + 1))
                            require(seen.add(decoded)) { "Duplicate Vault envelope property." }
                            expectingProperty = false
                        }
                        stringStart = -1
                    }
                }
            } else {
                when (character) {
                    '{' -> {
                        objectDepth += 1
                        if (objectDepth == 1 && arrayDepth == 0) expectingProperty = true
                    }
                    '}' -> objectDepth -= 1
                    '[' -> arrayDepth += 1
                    ']' -> arrayDepth -= 1
                    ',' -> if (objectDepth == 1 && arrayDepth == 0) expectingProperty = true
                    '"' -> {
                        stringStart = index
                        propertyName = objectDepth == 1 && arrayDepth == 0 && expectingProperty
                    }
                }
            }
        }
    }

    private fun decodeCanonicalBase64Url(value: String): ByteArray {
        require(BASE64URL_PATTERN.matches(value)) { "Invalid Base64URL." }
        require(value.length % BASE64_QUANTUM != 1) { "Invalid Base64URL length." }
        val decoded = Base64.getUrlDecoder().decode(value)
        require(Base64.getUrlEncoder().withoutPadding().encodeToString(decoded) == value) {
            "Non-canonical Base64URL."
        }
        return decoded
    }
}
