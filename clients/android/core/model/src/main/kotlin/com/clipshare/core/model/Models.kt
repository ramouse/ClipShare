package com.clipshare.core.model

const val MAX_TEXT_UTF8_BYTES: Int = 100 * 1024
const val MAX_TEXT_CONTRACT_CHARS: Int = 100_000
const val MAX_FILE_BYTES: Long = 100L * 1024L * 1024L
const val MAX_RESPONSE_BYTES: Long = 1024L * 1024L

private const val SINGLE_VIEW: Int = 1
private const val FIVE_VIEWS: Int = 5

enum class Expiry(val wireValue: String) {
    ONE_HOUR("1h"),
    ONE_DAY("24h"),
    SEVEN_DAYS("7d"),
    FOREVER("forever"),
}

data class ShareOptions(
    val expiry: Expiry = Expiry.ONE_DAY,
    val maxViews: Int? = null,
) {
    init {
        require(maxViews == null || maxViews == SINGLE_VIEW || maxViews == FIVE_VIEWS) {
            "maxViews must be null, 1, or 5"
        }
    }
}

data class CreatedShare(
    val code: String,
    val url: String,
    val expiresAt: String?,
    val maxViews: Int?,
    val createdAt: String,
)

data class ReceivedShare(
    val code: String,
    val content: String,
    val expiresAt: String?,
    val remainingViews: Int?,
    val createdAt: String,
)

data class CreatedFile(
    val code: String,
    val url: String,
    val originalName: String,
    val sizeBytes: Long,
    val expiresAt: String?,
    val maxViews: Int?,
    val createdAt: String,
)

data class RemoteFileMetadata(
    val code: String,
    val originalName: String,
    val sizeBytes: Long,
    val contentType: String,
    val expiresAt: String?,
    val remainingViews: Int?,
    val createdAt: String,
)

data class DownloadReceipt(
    val bytesWritten: Long,
    val contentType: String,
)

data class RemoteProblem(
    val type: String,
    val title: String,
    val status: Int,
    val detail: String,
)

enum class ConsumingOperation {
    READ_TEXT,
    DOWNLOAD_FILE,
}

sealed interface GatewayResult<out T> {
    data class Success<T>(val value: T) : GatewayResult<T>

    data class Rejected(val problem: RemoteProblem) : GatewayResult<Nothing>

    data class TransportFailure(
        val message: String,
        val mayHaveReachedServer: Boolean,
        val idempotencyKey: String? = null,
    ) : GatewayResult<Nothing>

    data class OutcomeUnknown(
        val operation: ConsumingOperation,
        val message: String,
    ) : GatewayResult<Nothing>

    data class ProtocolFailure(val message: String) : GatewayResult<Nothing>
}

sealed interface GatewayAccess {
    data class Available(val gateway: ClipShareGateway) : GatewayAccess
    data class InvalidEndpoint(val message: String) : GatewayAccess
}

sealed interface TextValidation {
    data class Accepted(val text: String) : TextValidation
    data class Rejected(val message: String) : TextValidation
}
