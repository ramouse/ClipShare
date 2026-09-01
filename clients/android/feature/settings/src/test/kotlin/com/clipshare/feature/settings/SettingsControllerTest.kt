package com.clipshare.feature.settings

import com.clipshare.core.model.ClientSettings
import com.clipshare.core.model.SettingsRepository
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertFalse
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class SettingsControllerTest {
    @Test
    fun switchesDefaultOffAndPersistIndependently() = runTest {
        val repository = FakeSettingsRepository()
        val controller = SettingsController(repository)

        assertFalse(repository.value.monitorClipboard)
        assertFalse(repository.value.autoSyncPairedDevices)
        controller.setAutoSyncPairedDevices(true)

        assertFalse(repository.value.monitorClipboard)
        assertTrue(repository.value.autoSyncPairedDevices)
        assertFalse(repository.networkTouched)
    }

    @Test
    fun monitorAndEndpointSettingsAreForwardedWithoutNetworkEffects() = runTest {
        val repository = FakeSettingsRepository()
        val controller = SettingsController(repository)

        controller.setMonitorClipboard(true)
        controller.setServerBaseUrl("  https://example.test/base  ")

        assertTrue(repository.value.monitorClipboard)
        assertEquals("https://example.test/base", repository.value.serverBaseUrl)
        assertFalse(repository.networkTouched)
    }

    private class FakeSettingsRepository : SettingsRepository {
        private val flow = MutableStateFlow(ClientSettings())
        override val settings = flow
        val value: ClientSettings get() = flow.value
        var networkTouched = false

        override suspend fun setMonitorClipboard(enabled: Boolean) {
            flow.value = flow.value.copy(monitorClipboard = enabled)
        }

        override suspend fun setAutoSyncPairedDevices(enabled: Boolean) {
            flow.value = flow.value.copy(autoSyncPairedDevices = enabled)
        }

        override suspend fun setServerBaseUrl(value: String) {
            flow.value = flow.value.copy(serverBaseUrl = value)
        }
    }
}
