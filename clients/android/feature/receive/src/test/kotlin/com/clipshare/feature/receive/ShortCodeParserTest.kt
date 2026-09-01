package com.clipshare.feature.receive

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class ShortCodeParserTest {
    @Test
    fun acceptsShortCodeAndLink() {
        assertEquals(CodeParseResult.Accepted("aB12"), ShortCodeParser.parse("aB12"))
        assertEquals(
            CodeParseResult.Accepted("aB12"),
            ShortCodeParser.parse("https://clip.example/s/aB12/"),
        )
    }

    @Test
    fun rejectsEncryptedFragmentOwnedByC2() {
        val result = ShortCodeParser.parse("https://clip.example/s/aB12#enc1.payload")

        assertTrue(result is CodeParseResult.Rejected)
        assertTrue((result as CodeParseResult.Rejected).message.contains("C2"))
    }

    @Test
    fun rejectsMalformedNonHttpAndMissingPathCode() {
        for (value in listOf("", "not a code!", "ftp://clip.example/aB12", "https:///aB12")) {
            assertTrue(ShortCodeParser.parse(value) is CodeParseResult.Rejected)
        }
        assertTrue(ShortCodeParser.parse("https://clip.example/path/too-long-code") is CodeParseResult.Rejected)
    }

    @Test
    fun trimsShortCodesAndRejectsMoreThanEightCharacters() {
        assertEquals(CodeParseResult.Accepted("Ab12"), ShortCodeParser.parse("  Ab12  "))
        assertTrue(ShortCodeParser.parse("123456789") is CodeParseResult.Rejected)
    }
}
