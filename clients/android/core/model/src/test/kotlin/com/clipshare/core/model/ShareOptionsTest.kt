package com.clipshare.core.model

import org.junit.Assert.assertEquals
import org.junit.Assert.fail
import org.junit.Test

class ShareOptionsTest {
    @Test
    fun acceptsOnlyFrozenViewChoices() {
        assertEquals(null, ShareOptions(maxViews = null).maxViews)
        assertEquals(1, ShareOptions(maxViews = 1).maxViews)
        assertEquals(5, ShareOptions(maxViews = 5).maxViews)

        try {
            ShareOptions(maxViews = 2)
            fail("unsupported maxViews must be rejected")
        } catch (expected: IllegalArgumentException) {
            assertEquals("maxViews must be null, 1, or 5", expected.message)
        }
    }
}
