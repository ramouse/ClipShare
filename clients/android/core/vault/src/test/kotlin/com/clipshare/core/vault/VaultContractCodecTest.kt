package com.clipshare.core.vault

import java.util.Base64
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class VaultContractCodecTest {
    @Test
    fun `strict envelope accepts the frozen shape`() {
        val decoded = VaultContractCodec.decodeEnvelope(envelopeJson())
        assertEquals("item-payload", decoded.purpose)
        assertEquals(4096L, decoded.paddedPlaintextBytes)
    }

    @Test
    fun `strict envelope rejects unknown properties and invalid buckets`() {
        assertFails { VaultContractCodec.decodeEnvelope(envelopeJson().replace("}", ",\"extra\":true}")) }
        assertFails {
            VaultContractCodec.decodeEnvelope(
                envelopeJson().replace(
                    "\"algorithm\":\"A256GCM\"",
                    "\"algorithm\":\"A128GCM\",\"algorithm\":\"A256GCM\"",
                ),
            )
        }
        assertFails {
            VaultContractCodec.decodeEnvelope(
                envelopeJson().replace(
                    "\"algorithm\":\"A256GCM\"",
                    "\"\\u0061lgorithm\":\"A128GCM\",\"algorithm\":\"A256GCM\"",
                ),
            )
        }
        assertFails { VaultContractCodec.decodeEnvelope(envelopeJson().replace("4096", "4095")) }
        assertFails { VaultContractCodec.decodeEnvelope(envelopeJson().replace("4096", "0")) }
    }

    @Test
    fun `strict envelope rejects noncanonical base64url`() {
        val nonceProperty = "\"nonce\":\"${nonce()}\""
        assertFails {
            VaultContractCodec.decodeEnvelope(envelopeJson().replace(nonceProperty, "\"nonce\":\"AAAAAAAAAAAAAAA=\""))
        }
        assertFails { VaultContractCodec.decodeEnvelope(envelopeJson().replace(nonceProperty, "\"nonce\":\"A\"")) }
        assertFails { VaultContractCodec.decodeEnvelope(envelopeJson().replace(nonceProperty, "\"nonce\":\"AA\"")) }

        val canonicalCiphertext = ciphertext()
        val nonCanonicalCiphertext = canonicalCiphertext.dropLast(1) + "B"
        assertFails {
            VaultContractCodec.decodeEnvelope(envelopeJson().replace(canonicalCiphertext, nonCanonicalCiphertext))
        }
    }

    @Test
    fun `strict envelope rejects every security sensitive field boundary`() {
        val value = envelopeJson()
        assertFails { VaultContractCodec.decodeEnvelope(value.replace("\"schemaVersion\":1", "\"schemaVersion\":2")) }
        assertFails { VaultContractCodec.decodeEnvelope(value.replace("A256GCM", "A128GCM")) }
        assertFails { VaultContractCodec.decodeEnvelope(value.replace("item-payload", "unknown")) }
        assertFails { VaultContractCodec.decodeEnvelope(value.replace("00112233", "X0112233")) }
        assertFails { VaultContractCodec.decodeEnvelope(value.replace("11111111", "X1111111")) }
        assertFails { VaultContractCodec.decodeEnvelope(value.replace("aaaaaaaa", "Xaaaaaaa")) }
        assertFails {
            VaultContractCodec.decodeEnvelope(value.replace("\"field\":\"content\"", "\"field\":\"Content\""))
        }
        assertFails { VaultContractCodec.decodeEnvelope(value.replace("\"keyEpoch\":1", "\"keyEpoch\":0")) }
        assertFails { VaultContractCodec.decodeEnvelope(value.replace("\"nonceCounter\":7", "\"nonceCounter\":-1")) }
        assertFails { VaultContractCodec.decodeEnvelope(value.replace("\"nonceCounter\":7", "\"nonceCounter\":0")) }
        assertFails {
            VaultContractCodec.decodeEnvelope(
                value.replace(ciphertext(), Base64.getUrlEncoder().withoutPadding().encodeToString(ByteArray(15))),
            )
        }
    }

    @Test
    fun `file chunk and epoch wrapper allow their frozen zero counters`() {
        val chunk = envelopeJson()
            .replace("item-payload", "file-chunk")
            .replace("\"nonceCounter\":7", "\"nonceCounter\":0")
            .replace("\"paddedPlaintextBytes\":4096", "\"paddedPlaintextBytes\":0")
            .replace(ciphertext(), emptyCiphertext())
        assertEquals(0L, VaultContractCodec.decodeEnvelope(chunk).paddedPlaintextBytes)
        assertFails {
            VaultContractCodec.decodeEnvelope(
                chunk.replace("\"paddedPlaintextBytes\":0", "\"paddedPlaintextBytes\":1"),
            )
        }
        assertFails {
            VaultContractCodec.decodeEnvelope(
                chunk.replace("\"paddedPlaintextBytes\":0", "\"paddedPlaintextBytes\":-1"),
            )
        }

        val wrapper = envelopeJson()
            .replace("item-payload", "epoch-wrapper")
            .replace("\"nonceCounter\":7", "\"nonceCounter\":0")
        assertEquals(0L, VaultContractCodec.decodeEnvelope(wrapper).nonceCounter)
    }

    private fun envelopeJson(): String =
        """{"schemaVersion":1,"algorithm":"A256GCM","purpose":"item-payload",${
            "\"vaultId\":\"00112233-4455-4677-8899-aabbccddeeff\"," +
                "\"originDeviceId\":\"11111111-2222-4333-8444-555555555555\"," +
                "\"entityId\":\"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee\"," +
                "\"field\":\"content\",\"keyEpoch\":1,\"nonceCounter\":7," +
                "\"nonce\":\"${nonce()}\",\"paddedPlaintextBytes\":4096," +
                "\"cipherAndTag\":\"${ciphertext()}\""
        }}"""

    private fun nonce() = Base64.getUrlEncoder().withoutPadding().encodeToString(ByteArray(12))

    private fun ciphertext() = Base64.getUrlEncoder().withoutPadding().encodeToString(ByteArray(4096 + 16))

    private fun emptyCiphertext() = Base64.getUrlEncoder().withoutPadding().encodeToString(ByteArray(16))

    private fun assertFails(operation: () -> Unit) {
        var failed = false
        try {
            operation()
        } catch (_: IllegalArgumentException) {
            failed = true
        } catch (_: kotlinx.serialization.SerializationException) {
            failed = true
        }
        assertTrue("Expected strict contract rejection", failed)
    }
}
