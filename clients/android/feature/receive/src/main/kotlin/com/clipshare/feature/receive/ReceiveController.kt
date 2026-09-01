package com.clipshare.feature.receive

import com.clipshare.core.model.GatewayAccess
import com.clipshare.core.model.GatewayProvider
import com.clipshare.core.model.GatewayResult
import com.clipshare.core.model.RemoteFileMetadata
import com.clipshare.core.model.WritableDocument
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

data class ReceiveUiState(
    val input: String = "",
    val inProgress: Boolean = false,
    val statusMessage: String = "",
    val receivedText: String? = null,
    val fileMetadata: RemoteFileMetadata? = null,
)

class ReceiveController(private val gatewayProvider: GatewayProvider) {
    private val mutableState = MutableStateFlow(ReceiveUiState())
    val state: StateFlow<ReceiveUiState> = mutableState.asStateFlow()

    fun updateInput(value: String) {
        mutableState.value = mutableState.value.copy(
            input = value,
            statusMessage = "",
            receivedText = null,
            fileMetadata = null,
        )
    }

    fun reportMessage(value: String) {
        mutableState.value = mutableState.value.copy(statusMessage = value)
    }

    suspend fun receiveText() {
        withGatewayAndCode { gateway, code ->
            mutableState.value = mutableState.value.copy(
                inProgress = true,
                statusMessage = "读取会消耗一次查看次数；请求中不会自动重试",
                receivedText = null,
            )
            val result = gateway.receiveText(code)
            mutableState.value = when (result) {
                is GatewayResult.Success -> mutableState.value.copy(
                    inProgress = false,
                    statusMessage = "读取成功",
                    receivedText = result.value.content,
                )

                else -> mutableState.value.fromFailure(result)
            }
        }
    }

    suspend fun inspectFile() {
        withGatewayAndCode { gateway, code ->
            mutableState.value = mutableState.value.copy(
                inProgress = true,
                statusMessage = "正在读取不消费次数的文件元数据",
                fileMetadata = null,
            )
            val result = gateway.getFileMetadata(code)
            mutableState.value = when (result) {
                is GatewayResult.Success -> mutableState.value.copy(
                    inProgress = false,
                    statusMessage = "文件元数据读取成功；只有确认下载后才会消费次数",
                    fileMetadata = result.value,
                )

                else -> mutableState.value.fromFailure(result)
            }
        }
    }

    suspend fun downloadInspectedFile(destination: WritableDocument) {
        val metadata = mutableState.value.fileMetadata
        if (metadata == null) {
            mutableState.value = mutableState.value.copy(statusMessage = "请先读取文件元数据")
            return
        }
        val access = gatewayProvider.current()
        if (access is GatewayAccess.InvalidEndpoint) {
            mutableState.value = mutableState.value.copy(statusMessage = access.message)
            return
        }
        access as GatewayAccess.Available
        mutableState.value = mutableState.value.copy(
            inProgress = true,
            statusMessage = "下载会消耗一次次数；连接中断不会自动重试",
        )
        val result = access.gateway.downloadFile(metadata.code, destination)
        mutableState.value = when (result) {
            is GatewayResult.Success -> mutableState.value.copy(
                inProgress = false,
                statusMessage = "已写入 ${result.value.bytesWritten} 字节",
            )

            else -> mutableState.value.fromFailure(result)
        }
    }

    private suspend fun withGatewayAndCode(
        block: suspend (com.clipshare.core.model.ClipShareGateway, String) -> Unit,
    ) {
        val parsed = ShortCodeParser.parse(mutableState.value.input)
        if (parsed is CodeParseResult.Rejected) {
            mutableState.value = mutableState.value.copy(statusMessage = parsed.message)
            return
        }
        parsed as CodeParseResult.Accepted
        val access = gatewayProvider.current()
        if (access is GatewayAccess.InvalidEndpoint) {
            mutableState.value = mutableState.value.copy(statusMessage = access.message)
            return
        }
        access as GatewayAccess.Available
        block(access.gateway, parsed.code)
    }

    private fun ReceiveUiState.fromFailure(result: GatewayResult<*>): ReceiveUiState = when (result) {
        is GatewayResult.Rejected -> copy(
            inProgress = false,
            statusMessage = "${result.problem.status} ${result.problem.title}：${result.problem.detail}",
        )

        is GatewayResult.TransportFailure -> copy(inProgress = false, statusMessage = result.message)
        is GatewayResult.OutcomeUnknown -> copy(inProgress = false, statusMessage = "结果未知：${result.message}")
        is GatewayResult.ProtocolFailure -> copy(inProgress = false, statusMessage = result.message)
        is GatewayResult.Success -> error("success must be reduced by the caller")
    }
}
