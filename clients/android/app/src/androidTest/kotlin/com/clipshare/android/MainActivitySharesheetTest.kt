package com.clipshare.android

import android.content.Context
import android.content.Intent
import androidx.compose.ui.test.ExperimentalTestApi
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertTextContains
import androidx.compose.ui.test.hasTestTag
import androidx.compose.ui.test.junit4.v2.createEmptyComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performScrollTo
import androidx.compose.ui.test.waitUntilAtLeastOneExists
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import org.junit.Rule
import org.junit.Test

@OptIn(ExperimentalTestApi::class)
class MainActivitySharesheetTest {
    @get:Rule
    val composeRule = createEmptyComposeRule()

    @Test
    fun actionSendOnlyFillsDraft() {
        val sharedText = "https://example.test/from-sharesheet"
        val context = ApplicationProvider.getApplicationContext<Context>()
        val intent = Intent(context, MainActivity::class.java).apply {
            action = Intent.ACTION_SEND
            type = "text/plain"
            putExtra(Intent.EXTRA_TEXT, sharedText)
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        val scenario = ActivityScenario.launch<MainActivity>(intent)

        try {
            composeRule.waitUntilAtLeastOneExists(
                matcher = hasTestTag("send_draft_input"),
                timeoutMillis = 30_000,
            )
            composeRule.onNodeWithTag("send_draft_input").assertTextContains(sharedText)
            composeRule.onNodeWithText("系统分享 已填入草稿，尚未发送")
                .performScrollTo()
                .assertIsDisplayed()
        } finally {
            scenario.close()
        }
    }
}
