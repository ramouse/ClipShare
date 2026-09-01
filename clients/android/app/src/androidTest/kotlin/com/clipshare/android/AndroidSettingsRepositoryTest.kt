package com.clipshare.android

import androidx.test.core.app.ApplicationProvider
import com.clipshare.platform.android.AndroidSettingsRepository
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class AndroidSettingsRepositoryTest {
    @Test
    fun persistsSwitchesIndependently() = runBlocking {
        val repository = AndroidSettingsRepository(ApplicationProvider.getApplicationContext())
        try {
            repository.setMonitorClipboard(true)
            repository.setAutoSyncPairedDevices(false)
            var settings = repository.settings.first()
            assertTrue(settings.monitorClipboard)
            assertFalse(settings.autoSyncPairedDevices)

            repository.setAutoSyncPairedDevices(true)
            settings = repository.settings.first()
            assertTrue(settings.monitorClipboard)
            assertTrue(settings.autoSyncPairedDevices)
        } finally {
            repository.setMonitorClipboard(false)
            repository.setAutoSyncPairedDevices(false)
        }
    }
}
