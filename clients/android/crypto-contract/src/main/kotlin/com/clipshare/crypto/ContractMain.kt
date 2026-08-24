package com.clipshare.crypto

import java.nio.ByteBuffer
import java.nio.CharBuffer
import java.nio.charset.CharacterCodingException
import java.nio.charset.CodingErrorAction
import java.nio.charset.StandardCharsets
import java.nio.file.Files
import java.nio.file.Path
import java.util.Base64
import javax.crypto.AEADBadTagException
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive

private const val PREFIX = "ENC1:"
private const val KEY_BYTES = 32
private const val IV_BYTES = 12
private const val TAG_BYTES = 16
private const val MAX_MARKER_CHARS = 16_777_216
private val BASE64_URL = Regex("^[A-Za-z0-9_-]+$")
private val LOWER_HEX = Regex("^(?:[0-9a-f]{2})*$")
private var passed = 0

private class Enc1ContractException(
    val code: String,
    message: String,
    cause: Throwable? = null,
) : IllegalArgumentException(message, cause)

fun main(args: Array<String>) {
    require(args.size == 2) {
        "usage: contract-tests <positive-vectors.json> <negative-vectors.json>"
    }
    val root = Json.parseToJsonElement(Files.readString(Path.of(args[0]))).jsonObject
    val negative = Json.parseToJsonElement(Files.readString(Path.of(args[1]))).jsonObject
    checkTest(root.string("suite") == "clipshare-enc1", "suite name")
    checkTest(root.getValue("suite_version").jsonPrimitive.int == 1, "suite version")
    checkTest(root.string("spec_revision") == "1.0.0", "spec revision")
    checkTest(root.getValue("test_only").jsonPrimitive.boolean, "fixtures are test-only")
    val algorithm = root.objectValue("algorithm")
    checkTest(algorithm.string("name") == "AES-256-GCM", "algorithm name")
    checkTest(algorithm.getValue("key_bits").jsonPrimitive.int == 256, "AES-256 key bits")
    checkTest(algorithm.getValue("iv_bits").jsonPrimitive.int == 96, "96-bit IV")
    checkTest(algorithm.getValue("tag_bits").jsonPrimitive.int == 128, "128-bit tag")
    checkTest(algorithm.string("aad") == "empty", "empty AAD")
    checkTest(
        algorithm.string("output_layout") == "ciphertext||tag",
        "ciphertext and tag layout",
    )
    checkTest(
        root.string("wire_format") == "ENC1:<iv_b64url>.<cipher_and_tag_b64url>",
        "wire format",
    )
    checkTest(
        root.string("base64url_profile") == "RFC4648_URLSAFE_NO_PADDING_STRICT",
        "strict Base64URL profile",
    )
    val limits = root.objectValue("limits")
    checkTest(
        limits.getValue("max_encrypted_plaintext_bytes").jsonPrimitive.int == 10_485_760,
        "encrypted plaintext limit",
    )
    checkTest(
        limits.getValue("max_marker_chars").jsonPrimitive.int == 16_777_216,
        "marker limit",
    )
    val errorCodes = root.getValue("error_codes").jsonArray.map { it.jsonPrimitive.content }
    checkTest("authentication_failed" in errorCodes, "authentication error code")
    checkTest("invalid_utf8" in errorCodes, "UTF-8 error code")

    val vectors = root.getValue("vectors").jsonArray
    checkTest(vectors.size >= 4, "zero and nonzero vector coverage")
    checkTest(
        vectors.any { it.jsonObject.string("id") == "nonzero-unicode-nul-crlf-normalization" },
        "Unicode/NUL/CRLF vector coverage",
    )
    checkTest(
        vectors.any { it.jsonObject.string("id") == "nonzero-binary-boundaries" },
        "binary boundary vector coverage",
    )
    vectors.forEach { verifyVector(it.jsonObject) }
    verifyNegativeVectors(vectors.map { it.jsonObject }, negative)
    println("ENC1 Kotlin/JCE contract: $passed passed, 0 failed")
}

private fun verifyVector(vector: JsonObject) {
    val id = vector.string("id")
    val kind = vector.string("kind")
    val key = parseHex(vector.string("key_hex"))
    val iv = parseHex(vector.string("iv_hex"))
    val plaintext = parseHex(vector.string("plaintext_hex"))
    val expectedCiphertext = parseHex(vector.string("ciphertext_hex"))
    val expectedTag = parseHex(vector.string("tag_hex"))
    val expectedMarker = vector.string("marker_ascii")

    checkTest(key.size == KEY_BYTES, "$id: AES-256 key length")
    checkTest(iv.size == IV_BYTES, "$id: 96-bit IV length")
    checkTest(base64UrlEncode(key) == vector.string("key_b64url"), "$id: key encoding")
    checkTest(base64UrlEncode(iv) == vector.string("iv_b64url"), "$id: IV encoding")

    if (kind == "text") {
        val text = vector.string("plaintext_utf8")
        checkTest(strictUtf8Encode(text).contentEquals(plaintext), "$id: strict UTF-8 bytes")
    } else {
        checkTest(kind == "bytes", "$id: supported vector kind")
    }

    val combined = encrypt(key, iv, plaintext)
    val ciphertext = combined.copyOfRange(0, plaintext.size)
    val tag = combined.copyOfRange(plaintext.size, combined.size)
    checkTest(ciphertext.contentEquals(expectedCiphertext), "$id: ciphertext KAT")
    checkTest(tag.contentEquals(expectedTag), "$id: authentication tag KAT")
    checkTest(
        base64UrlEncode(combined) == vector.string("cipher_and_tag_b64url"),
        "$id: ciphertext and tag encoding",
    )
    val marker = "$PREFIX${base64UrlEncode(iv)}.${base64UrlEncode(combined)}"
    checkTest(marker == expectedMarker, "$id: marker byte equality")

    val parsed = parseMarker(expectedMarker)
    val decrypted = decrypt(key, parsed.first, parsed.second)
    checkTest(decrypted.contentEquals(plaintext), "$id: fixed marker decryption")
    if (kind == "text") {
        checkTest(strictUtf8(decrypted) == vector.string("plaintext_utf8"), "$id: strict UTF-8 text")
    }
}

private fun encrypt(key: ByteArray, iv: ByteArray, plaintext: ByteArray): ByteArray {
    val cipher = Cipher.getInstance("AES/GCM/NoPadding")
    cipher.init(Cipher.ENCRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(TAG_BYTES * 8, iv))
    cipher.updateAAD(ByteArray(0))
    return cipher.doFinal(plaintext)
}

private fun decrypt(key: ByteArray, iv: ByteArray, combined: ByteArray): ByteArray {
    if (combined.size < TAG_BYTES) {
        throw Enc1ContractException("invalid_ciphertext_length", "ciphertext is shorter than the tag")
    }
    val cipher = Cipher.getInstance("AES/GCM/NoPadding")
    cipher.init(Cipher.DECRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(TAG_BYTES * 8, iv))
    cipher.updateAAD(ByteArray(0))
    try {
        return cipher.doFinal(combined)
    } catch (error: AEADBadTagException) {
        throw Enc1ContractException("authentication_failed", "authentication failed", error)
    }
}

private fun parseMarker(marker: String): Pair<ByteArray, ByteArray> {
    if (marker.length > MAX_MARKER_CHARS) {
        throw Enc1ContractException("size_limit_exceeded", "ENC1 marker exceeds size limit")
    }
    if (!marker.startsWith(PREFIX)) {
        throw Enc1ContractException("not_encrypted", "not an ENC1 marker")
    }
    val fields = marker.removePrefix(PREFIX).split('.')
    if (fields.size != 2 || fields.any { it.isEmpty() }) {
        throw Enc1ContractException("invalid_envelope", "invalid ENC1 envelope")
    }
    val iv = base64UrlDecode(fields[0])
    val combined = base64UrlDecode(fields[1])
    if (iv.size != IV_BYTES) {
        throw Enc1ContractException("invalid_iv_length", "invalid ENC1 IV length")
    }
    if (combined.size < TAG_BYTES) {
        throw Enc1ContractException("invalid_ciphertext_length", "ciphertext is shorter than the tag")
    }
    return iv to combined
}

private fun verifyNegativeVectors(vectors: List<JsonObject>, negative: JsonObject) {
    checkTest(negative.string("suite") == "clipshare-enc1-negative", "negative suite name")
    checkTest(negative.getValue("suite_version").jsonPrimitive.int == 1, "negative suite version")
    checkTest(negative.string("spec_revision") == "1.0.0", "negative spec revision")
    checkTest(negative.getValue("test_only").jsonPrimitive.boolean, "negative fixtures test-only")

    negative.getValue("marker_cases").jsonArray.forEach { element ->
        val case = element.jsonObject
        expectCode(case.string("expected_error"), "${case.string("id")}: marker rejection") {
            parseMarker(case.string("value"))
        }
    }
    negative.getValue("size_cases").jsonArray.forEach { element ->
        val case = element.jsonObject
        checkTest(
            case.string("operation") == "marker-one-char-over-limit",
            "${case.string("id")}: supported size operation",
        )
        expectCode(case.string("expected_error"), "${case.string("id")}: rejection") {
            parseMarker(PREFIX + "A".repeat(MAX_MARKER_CHARS - PREFIX.length + 1))
        }
    }

    negative.getValue("base64url_cases").jsonArray.forEach { element ->
        val case = element.jsonObject
        expectCode(case.string("expected_error"), "${case.string("id")}: Base64URL rejection") {
            base64UrlDecode(case.string("value"))
        }
    }

    negative.getValue("authentication_cases").jsonArray.forEach { element ->
        val case = element.jsonObject
        val vector = vectors.single { it.string("id") == case.string("source_vector_id") }
        val originalKey = parseHex(vector.string("key_hex"))
        val (iv, originalCombined) = parseMarker(vector.string("marker_ascii"))
        when (case.string("mutation")) {
            "flip-last-combined-bit" -> {
                val tampered = originalCombined.clone().also {
                    it[it.lastIndex] = (it.last().toInt() xor 1).toByte()
                }
                expectCode(case.string("expected_error"), "${case.string("id")}: rejection") {
                    decrypt(originalKey, iv, tampered)
                }
            }
            "replace-key-a5" -> expectCode(case.string("expected_error"), "${case.string("id")}: rejection") {
                decrypt(ByteArray(KEY_BYTES) { 0xA5.toByte() }, iv, originalCombined)
            }
            else -> error("unsupported authentication mutation ${case.string("mutation")}")
        }
    }

    negative.getValue("unicode_cases").jsonArray.forEach { element ->
        val case = element.jsonObject
        when (case.string("operation")) {
            "encode-isolated-high-surrogate" -> {
                expectCode(case.string("expected_error"), "${case.string("id")}: rejection") {
                    strictUtf8Encode("\uD800")
                }
            }
            "decode-ff" -> {
                expectCode(case.string("expected_error"), "${case.string("id")}: rejection") {
                    strictUtf8(byteArrayOf(0xFF.toByte()))
                }
            }
            else -> error("unsupported Unicode operation ${case.string("operation")}")
        }
    }
}

private fun base64UrlEncode(value: ByteArray): String =
    Base64.getUrlEncoder().withoutPadding().encodeToString(value)

private fun base64UrlDecode(value: String): ByteArray {
    if (!BASE64_URL.matches(value) || value.length % 4 == 1) {
        throw Enc1ContractException("invalid_base64url", "invalid Base64URL")
    }
    val decoded = try {
        Base64.getUrlDecoder().decode(value)
    } catch (error: IllegalArgumentException) {
        throw Enc1ContractException("invalid_base64url", "invalid Base64URL", error)
    }
    if (base64UrlEncode(decoded) != value) {
        throw Enc1ContractException("invalid_base64url", "non-canonical Base64URL")
    }
    return decoded
}

private fun parseHex(value: String): ByteArray {
    require(LOWER_HEX.matches(value)) { "invalid lowercase hexadecimal fixture" }
    val result = ByteArray(value.length / 2)
    for (index in result.indices) {
        result[index] = value.substring(index * 2, index * 2 + 2).toInt(16).toByte()
    }
    return result
}

private fun strictUtf8(value: ByteArray): String {
    val decoder = StandardCharsets.UTF_8.newDecoder()
        .onMalformedInput(CodingErrorAction.REPORT)
        .onUnmappableCharacter(CodingErrorAction.REPORT)
    try {
        return decoder.decode(ByteBuffer.wrap(value)).toString()
    } catch (error: CharacterCodingException) {
        throw Enc1ContractException("invalid_utf8", "invalid UTF-8", error)
    }
}

private fun strictUtf8Encode(value: String): ByteArray {
    val encoder = StandardCharsets.UTF_8.newEncoder()
        .onMalformedInput(CodingErrorAction.REPORT)
        .onUnmappableCharacter(CodingErrorAction.REPORT)
    val encoded = try {
        encoder.encode(CharBuffer.wrap(value))
    } catch (error: CharacterCodingException) {
        throw Enc1ContractException("invalid_unicode", "invalid Unicode", error)
    }
    return ByteArray(encoded.remaining()).also { encoded.get(it) }
}

private fun expectCode(expectedCode: String, name: String, operation: () -> Unit) {
    try {
        operation()
    } catch (error: Enc1ContractException) {
        checkTest(error.code == expectedCode, "$name maps to $expectedCode")
        return
    }
    error("FAIL: $name")
}

private fun JsonObject.string(name: String): String =
    getValue(name).jsonPrimitive.contentOrNull ?: error("missing string property $name")

private fun JsonObject.objectValue(name: String): JsonObject = getValue(name).jsonObject

private fun checkTest(condition: Boolean, name: String) {
    check(condition) { "FAIL: $name" }
    passed += 1
}
