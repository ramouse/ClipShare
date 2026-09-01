package com.clipshare.core.network

import com.clipshare.core.model.ClipShareGateway
import com.clipshare.core.model.ConsumingOperation
import com.clipshare.core.model.CreatedFile
import com.clipshare.core.model.CreatedShare
import com.clipshare.core.model.DownloadReceipt
import com.clipshare.core.model.GatewayResult
import com.clipshare.core.model.MAX_FILE_BYTES
import com.clipshare.core.model.MAX_RESPONSE_BYTES
import com.clipshare.core.model.ReadableDocument
import com.clipshare.core.model.ReceivedShare
import com.clipshare.core.model.RemoteFileMetadata
import com.clipshare.core.model.RemoteProblem
import com.clipshare.core.model.ShareOptions
import com.clipshare.core.model.WritableDocument
import java.io.ByteArrayOutputStream
import java.io.IOException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext
import kotlinx.serialization.SerializationException
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import okhttp3.HttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.MediaType.Companion.toMediaTypeOrNull
import okhttp3.MultipartBody
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okio.BufferedSink

private const val MAX_CREATE_ATTEMPTS = 3
private const val MAX_RETRY_AFTER_SECONDS = 5L
private const val HTTP_OK = 200
private const val HTTP_CLIENT_ERROR_MIN = 400
private const val HTTP_SERVER_ERROR_MAX = 599
private const val MILLISECONDS_PER_SECOND = 1000L
private val JSON_MEDIA_TYPE = "application/json; charset=utf-8".toMediaType()
private val OCTET_STREAM = "application/octet-stream".toMediaType()

fun interface RetrySleeper {
    suspend fun sleep(milliseconds: Long)
}

object ClipShareHttpClient {
    fun create(): OkHttpClient = OkHttpClient.Builder()
        .followRedirects(false)
        .followSslRedirects(false)
        .retryOnConnectionFailure(false)
        .cookieJar(okhttp3.CookieJar.NO_COOKIES)
        .build()
}

@Suppress("TooManyFunctions")
class OkHttpClipShareGateway(
    private val baseUrl: HttpUrl,
    private val client: OkHttpClient,
    private val retrySleeper: RetrySleeper = RetrySleeper { delay(it) },
) : ClipShareGateway {
    private val json = Json {
        ignoreUnknownKeys = false
        isLenient = false
        explicitNulls = true
        encodeDefaults = true
    }

    override suspend fun createText(
        content: String,
        options: ShareOptions,
        idempotencyKey: String,
    ): GatewayResult<CreatedShare> {
        if (!IDEMPOTENCY_KEY_PATTERN.matches(idempotencyKey)) {
            return GatewayResult.ProtocolFailure("幂等键格式无效")
        }
        val payload = json.encodeToString(
            ShareCreateRequestDto(content, options.expiry.wireValue, options.maxViews),
        )
        return executeCreate(idempotencyKey) {
            Request.Builder()
                .url(apiUrl("api/v1/shares"))
                .header("Idempotency-Key", idempotencyKey)
                .header("Accept", "application/json")
                .post(payload.toRequestBody(JSON_MEDIA_TYPE))
                .build()
        }.mapSuccess { responseBytes ->
            val dto = decode<ShareCreatedResponseDto>(responseBytes)
            require(dto.code.matches(CODE_PATTERN)) { "响应短码格式错误" }
            CreatedShare(dto.code, dto.url, dto.expiresAt, dto.maxViews, dto.createdAt)
        }
    }

    override suspend fun uploadFile(
        document: ReadableDocument,
        options: ShareOptions,
        idempotencyKey: String,
    ): GatewayResult<CreatedFile> {
        val documentSize = document.sizeBytes
        val preflightFailure = when {
            !IDEMPOTENCY_KEY_PATTERN.matches(idempotencyKey) -> GatewayResult.ProtocolFailure("幂等键格式无效")
            documentSize != null && documentSize > MAX_FILE_BYTES ->
                GatewayResult.ProtocolFailure("文件超过 100 MiB 限制")
            else -> null
        }
        return preflightFailure ?: executeCreate(idempotencyKey) {
            val body = MultipartBody.Builder()
                .setType(MultipartBody.FORM)
                .addFormDataPart(
                    "file",
                    document.displayName.ifBlank { "unnamed.bin" },
                    BoundedDocumentRequestBody(document),
                )
                .addFormDataPart("expiry", options.expiry.wireValue)
                .addFormDataPart("max_views", options.maxViews?.toString().orEmpty())
                .addFormDataPart("encrypted", "false")
                .build()
            Request.Builder()
                .url(apiUrl("api/v1/files"))
                .header("Idempotency-Key", idempotencyKey)
                .header("Accept", "application/json")
                .post(body)
                .build()
        }.mapSuccess { responseBytes ->
            val dto = decode<FileCreatedResponseDto>(responseBytes)
            require(dto.code.matches(CODE_PATTERN)) { "响应短码格式错误" }
            require(!dto.encrypted) { "A1 不接受服务端返回加密文件标记" }
            require(dto.sizeBytes in 0..MAX_FILE_BYTES) { "响应文件大小越界" }
            CreatedFile(
                dto.code,
                dto.url,
                dto.originalName,
                dto.sizeBytes,
                dto.expiresAt,
                dto.maxViews,
                dto.createdAt,
            )
        }
    }

    override suspend fun receiveText(code: String): GatewayResult<ReceivedShare> {
        if (!CODE_PATTERN.matches(code)) return GatewayResult.ProtocolFailure("短码格式无效")
        return withContext(Dispatchers.IO) {
            val request = Request.Builder()
                .url(apiUrl("api/v1/shares/$code"))
                .header("Accept", "application/json")
                .get()
                .build()
            try {
                client.newCall(request).execute().use { response ->
                    parseResponse(response, expectedStatus = 200).mapSuccess { responseBytes ->
                        val dto = decode<ShareReadResponseDto>(responseBytes)
                        require(dto.code == code) { "响应短码与请求不一致" }
                        ReceivedShare(dto.code, dto.content, dto.expiresAt, dto.remainingViews, dto.createdAt)
                    }
                }
            } catch (ignored: IOException) {
                GatewayResult.OutcomeUnknown(
                    ConsumingOperation.READ_TEXT,
                    "连接中断；本次读取可能已经消耗一次查看次数，请勿自动重试",
                )
            } catch (ignored: SerializationException) {
                GatewayResult.ProtocolFailure("服务端文本响应不符合冻结契约")
            } catch (exception: IllegalArgumentException) {
                GatewayResult.ProtocolFailure(exception.message ?: "服务端文本响应无效")
            }
        }
    }

    override suspend fun getFileMetadata(code: String): GatewayResult<RemoteFileMetadata> {
        if (!CODE_PATTERN.matches(code)) return GatewayResult.ProtocolFailure("短码格式无效")
        return executeSafe {
            Request.Builder()
                .url(apiUrl("api/v1/files/$code"))
                .header("Accept", "application/json")
                .get()
                .build()
        }.mapSuccess { responseBytes ->
            val dto = decode<FileReadResponseDto>(responseBytes)
            require(dto.code == code) { "响应短码与请求不一致" }
            require(dto.kind == "file") { "文件元数据 kind 无效" }
            require(!dto.encrypted) { "A1 不读取端到端加密文件；该能力属于 C2" }
            require(dto.sizeBytes in 0..MAX_FILE_BYTES) { "文件超过 100 MiB 限制" }
            RemoteFileMetadata(
                dto.code,
                dto.originalName,
                dto.sizeBytes,
                dto.contentType,
                dto.expiresAt,
                dto.remainingViews,
                dto.createdAt,
            )
        }
    }

    override suspend fun downloadFile(
        code: String,
        destination: WritableDocument,
    ): GatewayResult<DownloadReceipt> {
        if (!CODE_PATTERN.matches(code)) return GatewayResult.ProtocolFailure("短码格式无效")
        return withContext(Dispatchers.IO) {
            val request = Request.Builder()
                .url(apiUrl("api/v1/files/$code/download"))
                .header("Accept", "application/octet-stream")
                .get()
                .build()
            try {
                client.newCall(request).execute().use { response ->
                    validateNoStore(response)
                    if (response.code != HTTP_OK) {
                        return@use parseProblem(response)
                    }
                    val body = response.body
                    if (body.contentLength() > MAX_FILE_BYTES) {
                        return@use GatewayResult.ProtocolFailure("下载文件超过 100 MiB 限制")
                    }
                    val contentType = body.contentType()?.toString() ?: "application/octet-stream"
                    val bytesWritten = destination.outputStreamFactory.open().use { output ->
                        body.byteStream().use { input ->
                            copyBounded(input, output)
                        }
                    }
                    GatewayResult.Success(DownloadReceipt(bytesWritten, contentType))
                }
            } catch (ignored: IOException) {
                GatewayResult.OutcomeUnknown(
                    ConsumingOperation.DOWNLOAD_FILE,
                    "下载中断；次数可能已经消耗，目标文件也可能不完整，请勿自动重试",
                )
            } catch (ignored: SecurityException) {
                GatewayResult.OutcomeUnknown(
                    ConsumingOperation.DOWNLOAD_FILE,
                    "目标文档权限失效；下载次数可能已经消耗",
                )
            } catch (exception: ProtocolException) {
                GatewayResult.ProtocolFailure(exception.message ?: "下载响应违反冻结契约")
            }
        }
    }

    private suspend fun executeCreate(
        idempotencyKey: String,
        requestFactory: () -> Request,
    ): GatewayResult<ByteArray> = withContext(Dispatchers.IO) {
        var attempt = 1
        while (true) {
            try {
                client.newCall(requestFactory()).execute().use { response ->
                    if (response.code in RETRYABLE_STATUSES && attempt < MAX_CREATE_ATTEMPTS) {
                        validateNoStore(response)
                        retrySleeper.sleep(retryDelayMilliseconds(response))
                        attempt += 1
                    } else {
                        return@withContext parseResponse(response, expectedStatus = 201)
                    }
                }
            } catch (exception: ProtocolException) {
                return@withContext GatewayResult.ProtocolFailure(exception.message ?: "服务端响应违反冻结契约")
            } catch (ignored: DocumentTooLargeException) {
                return@withContext GatewayResult.ProtocolFailure("文件超过 100 MiB 限制")
            } catch (ignored: IOException) {
                return@withContext GatewayResult.TransportFailure(
                    "连接中断，创建结果未确认；手动重试必须复用同一幂等键",
                    mayHaveReachedServer = true,
                    idempotencyKey = idempotencyKey,
                )
            } catch (ignored: SecurityException) {
                return@withContext GatewayResult.TransportFailure(
                    "无法读取所选文档",
                    mayHaveReachedServer = false,
                    idempotencyKey = idempotencyKey,
                )
            }
        }
        @Suppress("UNREACHABLE_CODE")
        GatewayResult.ProtocolFailure("不可达状态")
    }

    private suspend fun executeSafe(requestFactory: () -> Request): GatewayResult<ByteArray> =
        withContext(Dispatchers.IO) {
            var attempt = 1
            while (true) {
                try {
                    client.newCall(requestFactory()).execute().use { response ->
                        if (response.code in RETRYABLE_STATUSES && attempt < MAX_CREATE_ATTEMPTS) {
                            validateNoStore(response)
                            retrySleeper.sleep(retryDelayMilliseconds(response))
                            attempt += 1
                        } else {
                            return@withContext parseResponse(response, expectedStatus = 200)
                        }
                    }
                } catch (exception: ProtocolException) {
                    return@withContext GatewayResult.ProtocolFailure(
                        exception.message ?: "服务端响应违反冻结契约",
                    )
                } catch (ignored: IOException) {
                    return@withContext GatewayResult.TransportFailure(
                        "网络连接失败",
                        mayHaveReachedServer = false,
                    )
                }
            }
            @Suppress("UNREACHABLE_CODE")
            GatewayResult.ProtocolFailure("不可达状态")
        }

    private fun parseResponse(response: Response, expectedStatus: Int): GatewayResult<ByteArray> {
        return try {
            validateNoStore(response)
            if (response.code == expectedStatus) {
                GatewayResult.Success(readBounded(response))
            } else {
                parseProblem(response)
            }
        } catch (exception: IOException) {
            throw exception
        } catch (exception: ProtocolException) {
            GatewayResult.ProtocolFailure(exception.message ?: "服务端响应违反冻结契约")
        }
    }

    private fun parseProblem(response: Response): GatewayResult.Rejected {
        val body = readBounded(response)
        val dto = try {
            decode<ProblemDetailDto>(body)
        } catch (exception: SerializationException) {
            throw ProtocolException("服务端错误响应不符合 ProblemDetail 冻结契约", exception)
        }
        if (dto.status != response.code || dto.status !in HTTP_CLIENT_ERROR_MIN..HTTP_SERVER_ERROR_MAX) {
            throw ProtocolException("ProblemDetail 状态码与 HTTP 状态不一致")
        }
        return GatewayResult.Rejected(RemoteProblem(dto.type, dto.title, dto.status, dto.detail))
    }

    private fun readBounded(response: Response): ByteArray {
        val body = response.body
        if (body.contentLength() > MAX_RESPONSE_BYTES) {
            throw ProtocolException("JSON 响应超过 1 MiB 上限")
        }
        val output = ByteArrayOutputStream()
        body.byteStream().use { input ->
            val buffer = ByteArray(DEFAULT_BUFFER_SIZE)
            var total = 0L
            while (true) {
                val read = input.read(buffer)
                if (read < 0) break
                total += read
                if (total > MAX_RESPONSE_BYTES) {
                    throw ProtocolException("JSON 响应超过 1 MiB 上限")
                }
                output.write(buffer, 0, read)
            }
        }
        return output.toByteArray()
    }

    private fun copyBounded(input: java.io.InputStream, output: java.io.OutputStream): Long {
        val buffer = ByteArray(DEFAULT_BUFFER_SIZE)
        var total = 0L
        while (true) {
            val read = input.read(buffer)
            if (read < 0) break
            total += read
            if (total > MAX_FILE_BYTES) {
                throw ProtocolException("下载文件超过 100 MiB 限制")
            }
            output.write(buffer, 0, read)
        }
        output.flush()
        return total
    }

    private fun validateNoStore(response: Response) {
        if (response.header("Cache-Control") != "no-store") {
            throw ProtocolException("API 响应缺少固定 Cache-Control: no-store")
        }
    }

    private fun retryDelayMilliseconds(response: Response): Long {
        val seconds = response.header("Retry-After")?.toLongOrNull()?.coerceIn(1, MAX_RETRY_AFTER_SECONDS) ?: 1
        return seconds * MILLISECONDS_PER_SECOND
    }

    private fun apiUrl(path: String): HttpUrl = baseUrl.newBuilder().addPathSegments(path).build()

    private inline fun <reified T> decode(bytes: ByteArray): T =
        json.decodeFromString(bytes.toString(Charsets.UTF_8))

    private inline fun <T, R> GatewayResult<T>.mapSuccess(transform: (T) -> R): GatewayResult<R> =
        when (this) {
            is GatewayResult.Success -> try {
                GatewayResult.Success(transform(value))
            } catch (ignored: SerializationException) {
                GatewayResult.ProtocolFailure("服务端 JSON 不符合冻结契约")
            } catch (exception: IllegalArgumentException) {
                GatewayResult.ProtocolFailure(exception.message ?: "服务端响应字段无效")
            }

            is GatewayResult.OutcomeUnknown -> this
            is GatewayResult.ProtocolFailure -> this
            is GatewayResult.Rejected -> this
            is GatewayResult.TransportFailure -> this
        }

    private class BoundedDocumentRequestBody(
        private val document: ReadableDocument,
    ) : RequestBody() {
        override fun contentType() = document.contentType.toMediaTypeOrNull() ?: OCTET_STREAM

        override fun contentLength(): Long = document.sizeBytes?.takeIf { it in 0..MAX_FILE_BYTES } ?: -1

        override fun writeTo(sink: BufferedSink) {
            document.inputStreamFactory.open().use { input ->
                val buffer = ByteArray(DEFAULT_BUFFER_SIZE)
                var total = 0L
                while (true) {
                    val read = input.read(buffer)
                    if (read < 0) break
                    total += read
                    if (total > MAX_FILE_BYTES) throw DocumentTooLargeException()
                    sink.write(buffer, 0, read)
                }
            }
        }
    }

    private class DocumentTooLargeException : IOException("document exceeds A1 limit")

    private class ProtocolException(message: String, cause: Throwable? = null) : RuntimeException(message, cause)

    private companion object {
        val CODE_PATTERN = Regex("^[A-Za-z0-9]{1,8}$")
        val IDEMPOTENCY_KEY_PATTERN = Regex("^[A-Za-z0-9._~:/+\\-]{8,128}$")
        val RETRYABLE_STATUSES = setOf(429, 503)
    }
}
