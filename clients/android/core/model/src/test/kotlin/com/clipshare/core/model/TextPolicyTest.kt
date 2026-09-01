package com.clipshare.core.model

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class TextPolicyTest {
    @Test
    fun rejectsEmptyContent() {
        assertTrue(TextPolicy.validate("  \n") is TextValidation.Rejected)
    }

    @Test
    fun enforcesUtf8ByteLimitInsteadOfCharacterCountOnly() {
        val result = TextPolicy.validate("中".repeat(MAX_TEXT_UTF8_BYTES / 3 + 1))

        assertTrue(result is TextValidation.Rejected)
    }

    @Test
    fun preservesAcceptedBytes() {
        assertEquals(
            TextValidation.Accepted("  https://example.test/a \n"),
            TextPolicy.validate("  https://example.test/a \n"),
        )
    }

    @Test
    fun enforcesContractCharacterLimitBeforeUtf8Limit() {
        val result = TextPolicy.validate("a".repeat(MAX_TEXT_CONTRACT_CHARS + 1))

        assertTrue(result is TextValidation.Rejected)
        assertTrue((result as TextValidation.Rejected).message.contains("100000"))
    }

    @Test
    fun acceptsExactContractAndUtf8Limits() {
        assertTrue(TextPolicy.validate("a".repeat(MAX_TEXT_CONTRACT_CHARS)) is TextValidation.Accepted)
        val exactUtf8Limit = "a".repeat(98_800) + "中".repeat(1_200)
        assertEquals(MAX_TEXT_UTF8_BYTES, exactUtf8Limit.toByteArray().size)
        assertTrue(TextPolicy.validate(exactUtf8Limit) is TextValidation.Accepted)
    }
}
