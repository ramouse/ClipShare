package com.clipshare.core.crypto

import com.clipshare.core.vault.DeviceId
import com.clipshare.core.vault.VaultId
import java.nio.file.Files
import java.nio.file.Path
import java.security.MessageDigest
import java.util.HexFormat
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class VaultCryptographyTest {
    @Test
    fun `file material uses a generation scoped prefix and bounded chunk counters`() {
        FileEncryptionMaterial.create().use { material ->
            val first = material.allocationFor(0)
            val last = material.allocationFor(Long.MAX_VALUE)
            assertArrayEquals(first.prefix, last.prefix)
            assertEquals(0, first.counter)
            assertEquals(Long.MAX_VALUE, last.counter)
            assertFails { material.allocationFor(-1) }

            val context = VaultCryptoContext(
                CryptoPurpose.FILE_CHUNK,
                VaultId.parse("00112233-4455-4677-8899-aabbccddeeff"),
                DeviceId.parse("11111111-2222-4333-8444-555555555555"),
                "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
                "chunk",
                1,
            )
            val dek = material.copyFileDek()
            try {
                assertFails {
                    VaultCryptography.encryptFileChunk(
                        dek,
                        context,
                        first,
                        ByteArray(FileEncryptionMaterial.MAXIMUM_PLAINTEXT_CHUNK_BYTES + 1),
                    )
                }
            } finally {
                dek.fill(0)
            }
        }

        val closed = FileEncryptionMaterial.create()
        closed.close()
        assertFails { closed.copyFileDek() }
        assertFails { closed.allocationFor(0) }
    }

    @Test
    fun `memory guard clears on lock exit and ten minute background limit`() {
        assertFails { VaultMemoryGuard({}, java.time.Duration.ofNanos(-1)) }
        var clears = 0
        val guard = VaultMemoryGuard(clearSensitiveState = { clears += 1 })
        val start = java.time.Instant.parse("2026-09-02T00:00:00Z")
        guard.onForeground(start)
        guard.onBackground(start)
        guard.onForeground(start.plus(java.time.Duration.ofMinutes(9)))
        assertEquals(0, clears)
        guard.onBackground(start)
        guard.onForeground(start.plus(java.time.Duration.ofMinutes(10)))
        guard.onSystemLocked()
        guard.onProcessExit()
        assertEquals(3, clears)
    }
    @Test
    fun `Kotlin provider matches the shared item and file vectors`() {
        val vectors = Json.parseToJsonElement(Files.readString(vectorPath())).jsonObject["vectors"]!!.jsonArray
        val item = vectors.single { it.jsonObject["id"]!!.jsonPrimitive.content == "epoch1-item-payload-4k" }.jsonObject
        val context = context(item)
        val secret = hex(item["epochSecretHex"]!!.jsonPrimitive.content)
        val key = VaultCryptography.deriveKey(secret, context)
        assertEquals(item["derivedKeyHex"]!!.jsonPrimitive.content, HexFormat.of().formatHex(key))
        val envelope = VaultCryptography.encryptRecord(
            secret,
            context,
            NonceAllocation(
                hex(item["noncePrefixHex"]!!.jsonPrimitive.content),
                item["nonceCounter"]!!.jsonPrimitive.content.toLong(),
            ),
            hex(item["payloadHex"]!!.jsonPrimitive.content),
        )
        val combined = java.util.Base64.getUrlDecoder().decode(envelope.cipherAndTag)
        assertEquals(item["cipherAndTagSha256Hex"]!!.jsonPrimitive.content, sha256(combined))
        assertArrayEquals(
            hex(item["payloadHex"]!!.jsonPrimitive.content),
            VaultCryptography.decryptRecord(secret, envelope),
        )

        val chunk = vectors.single {
            it.jsonObject["id"]!!.jsonPrimitive.content == "file-generation-chunk-3"
        }.jsonObject
        val chunkEnvelope = VaultCryptography.encryptFileChunk(
            hex(chunk["fileDekHex"]!!.jsonPrimitive.content),
            context(chunk),
            NonceAllocation(
                hex(chunk["noncePrefixHex"]!!.jsonPrimitive.content),
                chunk["nonceCounter"]!!.jsonPrimitive.content.toLong(),
            ),
            hex(chunk["payloadHex"]!!.jsonPrimitive.content),
        )
        assertEquals(chunk["cipherAndTagB64Url"]!!.jsonPrimitive.content, chunkEnvelope.cipherAndTag)
    }

    @Test
    fun `new epochs come from the generator and cannot repeat the prior secret`() {
        val first = EpochSecret.fromBytes(ByteArray(32) { it.toByte() })
        val ring = EpochKeyRing(1, first)
        var calls = 0
        val next = ring.rotate { size ->
            calls += 1
            if (calls == 1) ByteArray(size) { it.toByte() } else ByteArray(size) { (it + 1).toByte() }
        }
        assertEquals(2L, next)
        assertEquals(2, calls)
        assertFalse(ring.secret(1).copyBytes().contentEquals(ring.secret(2).copyBytes()))

        var historicalCollisionCalls = 0
        assertEquals(
            3L,
            ring.rotate { size ->
                historicalCollisionCalls += 1
                if (historicalCollisionCalls == 1) {
                    ByteArray(size) { it.toByte() }
                } else {
                    ByteArray(size) { (it + 2).toByte() }
                }
            },
        )
        assertEquals(2, historicalCollisionCalls)
        assertFalse(ring.secret(1).copyBytes().contentEquals(ring.secret(3).copyBytes()))
        assertFails { ring.removeHistoricalEpoch(1, 1) }
        ring.removeHistoricalEpoch(1, 0)
        ring.close()
    }

    @Test
    fun `epoch ring and secret lifecycle fail closed at every boundary`() {
        assertFails { EpochSecret.fromBytes(ByteArray(31)) }
        assertFails { EpochKeyRing(0, EpochSecret.fromBytes(ByteArray(32))) }

        val closed = EpochSecret.fromBytes(ByteArray(32))
        closed.close()
        assertFails { closed.copyBytes() }

        val wrongSizeRing = EpochKeyRing(1, EpochSecret.fromBytes(ByteArray(32)))
        lateinit var wrongSizeCandidate: ByteArray
        assertFails {
            wrongSizeRing.rotate {
                ByteArray(31) { 7 }.also { candidate -> wrongSizeCandidate = candidate }
            }
        }
        assertTrue(wrongSizeCandidate.all { it == 0.toByte() })
        wrongSizeRing.close()

        val repeatedRing = EpochKeyRing(1, EpochSecret.fromBytes(ByteArray(32)))
        assertFails { repeatedRing.rotate { ByteArray(it) } }
        repeatedRing.close()

        val maximumRing = EpochKeyRing(Long.MAX_VALUE, EpochSecret.fromBytes(ByteArray(32)))
        assertFails { maximumRing.rotate { ByteArray(it) { 1 } } }
        maximumRing.close()

        val missingRing = EpochKeyRing(1, EpochSecret.fromBytes(ByteArray(32)))
        assertFails { missingRing.secret(2) }
        assertFails { missingRing.removeHistoricalEpoch(1, 0) }
        assertFails { missingRing.removeHistoricalEpoch(2, 0) }
        missingRing.close()

        val generated = SecureRandomSecretGenerator.generate(EPOCH_SECRET_BYTES)
        assertEquals(EPOCH_SECRET_BYTES, generated.size)
        generated.fill(0)
    }

    @Test
    fun `AAD substitution fails authentication`() {
        val secret = ByteArray(32) { it.toByte() }
        val original = VaultCryptography.encryptRecord(
            secret,
            VaultCryptoContext(
                CryptoPurpose.ITEM_PAYLOAD,
                VaultId.parse("00112233-4455-4677-8899-aabbccddeeff"),
                DeviceId.parse("11111111-2222-4333-8444-555555555555"),
                "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
                "content",
                1,
            ),
            NonceAllocation(byteArrayOf(1, 2, 3, 4), 1),
            "secret".toByteArray(),
        )
        assertFails {
            VaultCryptography.decryptRecord(secret, original.copy(field = "title"))
        }
    }

    @Test
    fun `record nonce counters start at one`() {
        assertFails {
            VaultCryptography.encryptRecord(
                ByteArray(32),
                VaultCryptoContext(
                    CryptoPurpose.ITEM_PAYLOAD,
                    VaultId.parse("00112233-4455-4677-8899-aabbccddeeff"),
                    DeviceId.parse("11111111-2222-4333-8444-555555555555"),
                    "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
                    "content",
                    1,
                ),
                NonceAllocation(ByteArray(4), 0),
                ByteArray(0),
            )
        }
    }

    @Test
    fun `nonce allocation and derivation reject invalid key scopes`() {
        assertFails { NonceAllocation(ByteArray(3), 1) }
        assertFails { NonceAllocation(ByteArray(4), -1) }
        val first = NonceAllocation(byteArrayOf(1, 2, 3, 4), 1)
        assertTrue(first == NonceAllocation(byteArrayOf(1, 2, 3, 4), 1))
        assertFalse(first.equals("nonce"))
        assertFalse(first == NonceAllocation(byteArrayOf(4, 3, 2, 1), 1))
        assertFalse(first == NonceAllocation(byteArrayOf(1, 2, 3, 4), 2))
        assertEquals(first.hashCode(), NonceAllocation(byteArrayOf(1, 2, 3, 4), 1).hashCode())

        assertFails { VaultCryptography.deriveKey(ByteArray(31), recordContext()) }
        assertFails { VaultCryptography.deriveKey(ByteArray(32), fileContext()) }
        assertFails {
            VaultCryptography.aad(recordContext().copy(entityId = "not-a-uuid"), 1, 4096)
        }
        assertFails { recordContext().copy(field = "Title") }
        assertFails { recordContext().copy(field = "") }
        assertFails { recordContext().copy(field = "a".repeat(33)) }
        assertFails { recordContext().copy(keyEpoch = 0) }
        assertFails { VaultCryptography.aad(recordContext(), -1, 4096) }
        assertFails { VaultCryptography.aad(recordContext(), 1, -1) }
        assertFails {
            VaultCryptography.aad(recordContext().copy(field = "x".repeat(65_536)), 1, 4096)
        }
    }

    @Test
    fun `record and file APIs reject cross purpose malformed and unauthenticated envelopes`() {
        val secret = ByteArray(32) { it.toByte() }
        val record = VaultCryptography.encryptRecord(
            secret,
            recordContext(),
            NonceAllocation(byteArrayOf(1, 2, 3, 4), 1),
            byteArrayOf(9),
        )
        val fileDek = ByteArray(32) { (it + 1).toByte() }
        val chunk = VaultCryptography.encryptFileChunk(
            fileDek,
            fileContext(),
            NonceAllocation(byteArrayOf(4, 3, 2, 1), 0),
            byteArrayOf(1, 2, 3),
        )
        assertArrayEquals(byteArrayOf(1, 2, 3), VaultCryptography.decryptFileChunk(fileDek, chunk))

        assertFails {
            VaultCryptography.encryptRecord(
                secret,
                fileContext(),
                NonceAllocation(byteArrayOf(1, 2, 3, 4), 1),
                ByteArray(0),
            )
        }
        assertFails { VaultCryptography.decryptRecord(secret, chunk) }
        assertFails {
            VaultCryptography.encryptFileChunk(
                fileDek,
                recordContext(),
                NonceAllocation(ByteArray(4), 0),
                ByteArray(0),
            )
        }
        assertFails {
            VaultCryptography.encryptFileChunk(
                ByteArray(31),
                fileContext(),
                NonceAllocation(ByteArray(4), 0),
                ByteArray(0),
            )
        }
        assertFails { VaultCryptography.decryptFileChunk(ByteArray(31), chunk) }
        assertFails { VaultCryptography.decryptFileChunk(fileDek, record) }
        assertFails { VaultCryptography.decryptRecord(secret, record.copy(purpose = "unknown")) }
        assertFails { VaultCryptography.decryptRecord(secret, record.copy(nonceCounter = 0)) }
        assertFails { VaultCryptography.decryptRecord(secret, record.copy(nonce = "AA")) }
        assertFails { VaultCryptography.decryptRecord(secret, record.copy(nonce = "=")) }
        val shortCiphertext = java.util.Base64.getUrlEncoder().withoutPadding().encodeToString(ByteArray(15))
        assertFails {
            VaultCryptography.decryptRecord(
                secret,
                record.copy(cipherAndTag = shortCiphertext),
            )
        }
        assertFails { VaultCryptography.decryptRecord(ByteArray(32) { 7 }, record) }
    }

    @Test
    fun `declared plaintext lengths must match authenticated ciphertext shapes`() {
        val secret = ByteArray(32) { it.toByte() }
        val record = VaultCryptography.encryptRecord(
            secret,
            recordContext(),
            NonceAllocation(byteArrayOf(1, 2, 3, 4), 1),
            byteArrayOf(9),
        )
        assertFails {
            VaultCryptography.decryptRecord(
                secret,
                record.copy(paddedPlaintextBytes = record.paddedPlaintextBytes + 1),
            )
        }

        val chunk = VaultCryptography.encryptFileChunk(
            secret,
            fileContext(),
            NonceAllocation(byteArrayOf(4, 3, 2, 1), 0),
            byteArrayOf(1, 2, 3),
        )
        assertFails {
            VaultCryptography.decryptFileChunk(
                secret,
                chunk.copy(paddedPlaintextBytes = chunk.paddedPlaintextBytes + 1),
            )
        }
    }

    private fun context(value: kotlinx.serialization.json.JsonObject) = VaultCryptoContext(
        purpose = CryptoPurpose.entries.single { it.wireValue == value["purpose"]!!.jsonPrimitive.content },
        vaultId = VaultId.parse(value["vaultId"]!!.jsonPrimitive.content),
        originDeviceId = DeviceId.parse(value["originDeviceId"]!!.jsonPrimitive.content),
        entityId = value["entityId"]!!.jsonPrimitive.content,
        field = value["field"]!!.jsonPrimitive.content,
        keyEpoch = value["keyEpoch"]!!.jsonPrimitive.content.toLong(),
    )

    private fun recordContext() = VaultCryptoContext(
        CryptoPurpose.ITEM_PAYLOAD,
        VaultId.parse("00112233-4455-4677-8899-aabbccddeeff"),
        DeviceId.parse("11111111-2222-4333-8444-555555555555"),
        "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
        "content",
        1,
    )

    private fun fileContext() = recordContext().copy(purpose = CryptoPurpose.FILE_CHUNK, field = "chunk")

    private fun vectorPath(): Path = Path.of(requireNotNull(System.getProperty("clipshare.vault.vectors")))

    private fun hex(value: String): ByteArray = HexFormat.of().parseHex(value)

    private fun sha256(value: ByteArray): String =
        HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(value))

    private fun assertFails(operation: () -> Unit) {
        var failed = false
        try {
            operation()
        } catch (_: IllegalArgumentException) {
            failed = true
        } catch (_: IllegalStateException) {
            failed = true
        } catch (_: javax.crypto.AEADBadTagException) {
            failed = true
        }
        assertTrue("Expected a fail-closed result", failed)
    }
}
