package com.clipshare.android

import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsEnabled
import androidx.compose.ui.test.assertIsOff
import androidx.compose.ui.test.assertTextContains
import androidx.compose.ui.test.ExperimentalTestApi
import androidx.compose.ui.test.hasTestTag
import androidx.compose.ui.test.junit4.v2.createEmptyComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTextInput
import androidx.compose.ui.test.waitUntilAtLeastOneExists
import androidx.test.core.app.ActivityScenario
import org.junit.Rule
import org.junit.Test

@OptIn(ExperimentalTestApi::class)
class MainActivitySmokeTest {
    @get:Rule
    val composeRule = createEmptyComposeRule()

    @Test
    fun launchesWithEncryptedVaultSurface() {
        val scenario = ActivityScenario.launch(MainActivity::class.java)

        try {
            scenario.prepareForForeground()
            awaitNodeWithTag("vault_search")
            composeRule.onNodeWithText("本地加密内容库").assertIsDisplayed()
            composeRule.onNodeWithText("内容库").assertIsDisplayed()
            composeRule.onNodeWithText("发送").assertIsDisplayed()
            composeRule.onNodeWithText("接收").assertIsDisplayed()
            composeRule.onNodeWithText("设置").assertIsDisplayed()
        } finally {
            scenario.close()
        }
    }

    @Test
    fun clipboardAndAutoSyncSwitchesDefaultOff() {
        val scenario = ActivityScenario.launch(MainActivity::class.java)

        try {
            scenario.prepareForForeground()
            awaitNodeWithTag("vault_search")
            composeRule.onNodeWithText("设置").performClick()

            composeRule.onNodeWithTag("monitor_clipboard_switch").assertIsOff()
            composeRule.onNodeWithTag("auto_sync_switch").assertIsOff().assertIsEnabled()
        } finally {
            scenario.close()
        }
    }

    @Test
    fun draftAndReceiveInputSurviveActivityRecreation() {
        val scenario = ActivityScenario.launch(MainActivity::class.java)

        try {
            scenario.prepareForForeground()
            awaitNodeWithTag("vault_search")
            composeRule.onNodeWithText("发送").performClick()
            awaitNodeWithTag("send_draft_input")
            composeRule.onNodeWithTag("send_draft_input").performTextInput("draft-before-recreate")
            composeRule.onNodeWithText("接收").performClick()
            composeRule.onNodeWithTag("receive_input").performTextInput("AbC123")

            scenario.recreate()

            scenario.prepareForForeground()
            awaitNodeWithTag("receive_input")
            composeRule.onNodeWithTag("receive_input").assertTextContains("AbC123")
            composeRule.onNodeWithText("发送").performClick()
            composeRule.onNodeWithTag("send_draft_input").assertTextContains("draft-before-recreate")
        } finally {
            scenario.close()
        }
    }

    private fun awaitNodeWithTag(testTag: String) {
        composeRule.waitUntilAtLeastOneExists(
            matcher = hasTestTag(testTag),
            timeoutMillis = 30_000,
        )
    }
}
