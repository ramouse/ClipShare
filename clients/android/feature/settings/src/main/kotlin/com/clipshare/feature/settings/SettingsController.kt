package com.clipshare.feature.settings

import com.clipshare.core.model.ClientSettings
import com.clipshare.core.model.SettingsRepository
import kotlinx.coroutines.flow.Flow

class SettingsController(private val repository: SettingsRepository) {
    val settings: Flow<ClientSettings> = repository.settings

    suspend fun setMonitorClipboard(enabled: Boolean) = repository.setMonitorClipboard(enabled)

    suspend fun setAutoSyncPairedDevices(enabled: Boolean) = repository.setAutoSyncPairedDevices(enabled)

    suspend fun setServerBaseUrl(value: String) = repository.setServerBaseUrl(value.trim())
}
