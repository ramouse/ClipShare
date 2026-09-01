package com.clipshare.core.model

import java.io.InputStream
import java.io.OutputStream
import kotlinx.coroutines.flow.Flow

fun interface InputStreamFactory {
    fun open(): InputStream
}

class ReadableDocument(
    val displayName: String,
    val contentType: String,
    val sizeBytes: Long?,
    val inputStreamFactory: InputStreamFactory,
)

fun interface OutputStreamFactory {
    fun open(): OutputStream
}

class WritableDocument(
    val outputStreamFactory: OutputStreamFactory,
)

interface ClipShareGateway {
    suspend fun createText(
        content: String,
        options: ShareOptions,
        idempotencyKey: String,
    ): GatewayResult<CreatedShare>

    suspend fun uploadFile(
        document: ReadableDocument,
        options: ShareOptions,
        idempotencyKey: String,
    ): GatewayResult<CreatedFile>

    suspend fun receiveText(code: String): GatewayResult<ReceivedShare>

    suspend fun getFileMetadata(code: String): GatewayResult<RemoteFileMetadata>

    suspend fun downloadFile(
        code: String,
        destination: WritableDocument,
    ): GatewayResult<DownloadReceipt>
}

fun interface GatewayProvider {
    suspend fun current(): GatewayAccess
}

fun interface IdempotencyKeyFactory {
    fun create(): String
}

data class ClientSettings(
    val monitorClipboard: Boolean = false,
    val autoSyncPairedDevices: Boolean = false,
    val serverBaseUrl: String = "",
)

interface SettingsRepository {
    val settings: Flow<ClientSettings>

    suspend fun setMonitorClipboard(enabled: Boolean)

    suspend fun setAutoSyncPairedDevices(enabled: Boolean)

    suspend fun setServerBaseUrl(value: String)
}

fun interface ClipboardSubscription {
    fun cancel()
}

fun interface ClipboardTextSource {
    fun subscribe(listener: (String?) -> Unit): ClipboardSubscription
}
