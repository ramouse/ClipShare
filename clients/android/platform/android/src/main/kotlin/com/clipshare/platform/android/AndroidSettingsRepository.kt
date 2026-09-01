package com.clipshare.platform.android

import android.content.Context
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.emptyPreferences
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import com.clipshare.core.model.ClientSettings
import com.clipshare.core.model.SettingsRepository
import java.io.IOException
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.catch
import kotlinx.coroutines.flow.map

private val Context.clipShareSettings by preferencesDataStore(name = "clipshare_a1_settings")

class AndroidSettingsRepository(context: Context) : SettingsRepository {
    private val dataStore = context.applicationContext.clipShareSettings

    override val settings: Flow<ClientSettings> = dataStore.data
        .catch { exception ->
            if (exception is IOException) emit(emptyPreferences()) else throw exception
        }
        .map { preferences ->
            ClientSettings(
                monitorClipboard = preferences[MONITOR_CLIPBOARD] ?: false,
                autoSyncPairedDevices = preferences[AUTO_SYNC_PAIRED] ?: false,
                serverBaseUrl = preferences[SERVER_BASE_URL].orEmpty(),
            )
        }

    override suspend fun setMonitorClipboard(enabled: Boolean) {
        dataStore.edit { it[MONITOR_CLIPBOARD] = enabled }
    }

    override suspend fun setAutoSyncPairedDevices(enabled: Boolean) {
        dataStore.edit { it[AUTO_SYNC_PAIRED] = enabled }
    }

    override suspend fun setServerBaseUrl(value: String) {
        dataStore.edit { it[SERVER_BASE_URL] = value }
    }

    private companion object {
        val MONITOR_CLIPBOARD = booleanPreferencesKey("monitor_clipboard")
        val AUTO_SYNC_PAIRED = booleanPreferencesKey("auto_sync_paired_devices")
        val SERVER_BASE_URL = stringPreferencesKey("server_base_url")
    }
}
