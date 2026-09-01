package com.clipshare.feature.send

import com.clipshare.core.model.CreatedFile
import com.clipshare.core.model.CreatedShare
import com.clipshare.core.model.GatewayAccess
import com.clipshare.core.model.GatewayProvider
import com.clipshare.core.model.GatewayResult
import com.clipshare.core.model.IdempotencyKeyFactory
import com.clipshare.core.model.MAX_FILE_BYTES
import com.clipshare.core.model.ReadableDocument
import com.clipshare.core.model.ShareOptions
import com.clipshare.core.model.TextPolicy
import com.clipshare.core.model.TextValidation
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

data class SendUiState(
    val draft: String = "",
    val selectedFileName: String? = null,
    val inProgress: Boolean = false,
    val statusMessage: String = "",
    val createdCode: String? = null,
    val createdUrl: String? = null,
    val canRetryLastCreate: Boolean = false,
)

class SendController(
    private val gatewayProvider: GatewayProvider,
    private val keyFactory: IdempotencyKeyFactory,
) {
    private val mutableState = MutableStateFlow(SendUiState())
    val state: StateFlow<SendUiState> = mutableState.asStateFlow()

    private var selectedDocument: ReadableDocument? = null
    private var pendingCreate: PendingCreate? = null

    fun updateDraft(value: String) {
        pendingCreate = null
        mutableState.value = mutableState.value.copy(
            draft = value,
            statusMessage = "",
            canRetryLastCreate = false,
        )
    }

    fun reportMessage(value: String) {
        mutableState.value = mutableState.value.copy(statusMessage = value)
    }

    fun acceptExternalText(value: String, sourceLabel: String) {
        when (val validation = TextPolicy.validate(value)) {
            is TextValidation.Accepted -> mutableState.value = mutableState.value.copy(
                draft = validation.text,
                statusMessage = "$sourceLabel 已填入草稿，尚未发送",
            )

            is TextValidation.Rejected -> mutableState.value = mutableState.value.copy(
                statusMessage = "$sourceLabel 未采用：${validation.message}",
            )
        }
    }

    fun selectDocument(document: ReadableDocument) {
        selectedDocument = document
        pendingCreate = null
        val sizeBytes = document.sizeBytes
        mutableState.value = mutableState.value.copy(
            selectedFileName = document.displayName,
            canRetryLastCreate = false,
            statusMessage = if (sizeBytes != null && sizeBytes > MAX_FILE_BYTES) {
                "文件超过 100 MiB，不能上传"
            } else {
                "已选择文件；只有点击上传后才会读取并发送"
            },
        )
    }

    suspend fun sendText(options: ShareOptions = ShareOptions()) {
        val validation = TextPolicy.validate(mutableState.value.draft)
        if (validation is TextValidation.Rejected) {
            mutableState.value = mutableState.value.copy(statusMessage = validation.message)
            return
        }
        validation as TextValidation.Accepted
        execute(PendingCreate.Text(validation.text, options, keyFactory.create()))
    }

    suspend fun sendSelectedFile(options: ShareOptions = ShareOptions()) {
        val document = selectedDocument
        if (document == null) {
            mutableState.value = mutableState.value.copy(statusMessage = "请先通过系统文件选择器选择文件")
            return
        }
        val sizeBytes = document.sizeBytes
        if (sizeBytes != null && sizeBytes > MAX_FILE_BYTES) {
            mutableState.value = mutableState.value.copy(statusMessage = "文件超过 100 MiB，不能上传")
            return
        }
        execute(PendingCreate.File(document, options, keyFactory.create()))
    }

    suspend fun retryLastCreate() {
        val pending = pendingCreate
        if (pending == null || !mutableState.value.canRetryLastCreate) {
            mutableState.value = mutableState.value.copy(statusMessage = "没有可安全复用幂等键的创建请求")
            return
        }
        execute(pending)
    }

    private suspend fun execute(pending: PendingCreate) {
        val access = gatewayProvider.current()
        if (access is GatewayAccess.InvalidEndpoint) {
            mutableState.value = mutableState.value.copy(statusMessage = access.message)
            return
        }
        access as GatewayAccess.Available
        pendingCreate = pending
        mutableState.value = mutableState.value.copy(
            inProgress = true,
            statusMessage = "",
            createdCode = null,
            createdUrl = null,
            canRetryLastCreate = false,
        )
        val result = when (pending) {
            is PendingCreate.Text -> access.gateway.createText(pending.content, pending.options, pending.key)
            is PendingCreate.File -> access.gateway.uploadFile(pending.document, pending.options, pending.key)
        }
        mutableState.value = reduce(result)
        if (result is GatewayResult.Success) pendingCreate = null
    }

    private fun reduce(result: GatewayResult<Any>): SendUiState {
        val current = mutableState.value
        return when (result) {
            is GatewayResult.Success -> when (val value = result.value) {
                is CreatedShare -> current.copy(
                    inProgress = false,
                    statusMessage = "文本已创建；请按需复制短码或链接",
                    createdCode = value.code,
                    createdUrl = value.url,
                    canRetryLastCreate = false,
                )

                is CreatedFile -> current.copy(
                    inProgress = false,
                    statusMessage = "文件已上传；请按需复制短码或链接",
                    createdCode = value.code,
                    createdUrl = value.url,
                    canRetryLastCreate = false,
                )

                else -> current.copy(inProgress = false, statusMessage = "未知创建响应")
            }

            is GatewayResult.Rejected -> current.copy(
                inProgress = false,
                statusMessage = "${result.problem.status} ${result.problem.title}：${result.problem.detail}",
                canRetryLastCreate = false,
            )

            is GatewayResult.TransportFailure -> current.copy(
                inProgress = false,
                statusMessage = result.message,
                canRetryLastCreate = result.idempotencyKey != null,
            )

            is GatewayResult.ProtocolFailure -> current.copy(
                inProgress = false,
                statusMessage = result.message,
                canRetryLastCreate = false,
            )

            is GatewayResult.OutcomeUnknown -> current.copy(
                inProgress = false,
                statusMessage = result.message,
                canRetryLastCreate = false,
            )
        }
    }

    private sealed interface PendingCreate {
        val key: String

        data class Text(
            val content: String,
            val options: ShareOptions,
            override val key: String,
        ) : PendingCreate

        data class File(
            val document: ReadableDocument,
            val options: ShareOptions,
            override val key: String,
        ) : PendingCreate
    }
}
