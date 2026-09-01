package com.clipshare.core.network

import com.clipshare.core.model.CreatedShare
import com.clipshare.core.model.CreatedFile
import com.clipshare.core.model.DownloadReceipt
import com.clipshare.core.model.GatewayResult
import com.clipshare.core.model.InputStreamFactory
import com.clipshare.core.model.MAX_FILE_BYTES
import com.clipshare.core.model.OutputStreamFactory
import com.clipshare.core.model.ReadableDocument
import com.clipshare.core.model.ReceivedShare
import com.clipshare.core.model.RemoteFileMetadata
import com.clipshare.core.model.ShareOptions
import com.clipshare.core.model.WritableDocument
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.IOException
import kotlinx.coroutines.test.runTest
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import okhttp3.OkHttpClient
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

class OkHttpClipShareGatewayTest {
    private lateinit var server: MockWebServer

    @Before
    fun startServer() {
        server = MockWebServer()
        server.start()
    }

    @After
    fun stopServer() {
        server.close()
    }

    @Test
    fun createRetriesOnlyExplicitTransientResponsesAndKeepsKey() = runTest {
        server.enqueue(problemResponse(503))
        server.enqueue(problemResponse(429, retryAfter = 1))
        server.enqueue(
            jsonResponse(
                201,
                """
                    {
                      "code": "aB12",
                      "url": "https://invalid/s/aB12",
                      "expires_at": null,
                      "max_views": null,
                      "created_at": "now"
                    }
                """.trimIndent(),
            ),
        )
        val gateway = gateway(sleeper = RetrySleeper { })

        val result = gateway.createText("hello", ShareOptions(), "fixed-a1-key")

        assertTrue(result is GatewayResult.Success<CreatedShare>)
        assertEquals(3, server.requestCount)
        repeat(3) {
            assertEquals("fixed-a1-key", server.takeRequest().headers["Idempotency-Key"])
        }
    }

    @Test
    fun consumingReadDoesNotRetry429() = runTest {
        server.enqueue(problemResponse(429))
        server.enqueue(
            jsonResponse(
                200,
                """{"code":"aB12","content":"late","expires_at":null,"remaining_views":1,"created_at":"now"}""",
            ),
        )

        val result = gateway(sleeper = RetrySleeper { }).receiveText("aB12")

        assertTrue(result is GatewayResult.Rejected)
        assertEquals(1, server.requestCount)
    }

    @Test
    fun consumingDisconnectReturnsOutcomeUnknown() = runTest {
        val disconnectingClient = OkHttpClient.Builder()
            .addInterceptor { throw IOException("simulated disconnect") }
            .retryOnConnectionFailure(false)
            .build()
        val gateway = OkHttpClipShareGateway(server.url("/"), disconnectingClient, RetrySleeper { })

        val result = gateway.receiveText("aB12")

        assertTrue(result is GatewayResult.OutcomeUnknown)
    }

    @Test
    fun rejectsSuccessfulApiResponseWithoutNoStore() = runTest {
        server.enqueue(
            MockResponse.Builder()
                .code(200)
                .addHeader("Content-Type", "application/json")
                .body(
                    """{"code":"aB12","content":"hello","expires_at":null,"remaining_views":1,"created_at":"now"}""",
                )
                .build(),
        )

        val result = gateway().receiveText("aB12")

        assertTrue(result is GatewayResult.ProtocolFailure)
    }

    @Test
    fun rejectsKnownOversizeFileBeforeOpeningOrSending() = runTest {
        var opened = false
        val document = ReadableDocument(
            "too-large.bin",
            "application/octet-stream",
            MAX_FILE_BYTES + 1,
            InputStreamFactory {
                opened = true
                error("must not open")
            },
        )

        val result = gateway().uploadFile(document, ShareOptions(), "fixed-a1-key")

        assertTrue(result is GatewayResult.ProtocolFailure)
        assertEquals(false, opened)
        assertEquals(0, server.requestCount)
    }

    @Test
    fun validatesIdempotencyKeysBeforeCreateRequests() = runTest {
        val gateway = gateway()

        assertTrue(gateway.createText("hello", ShareOptions(), "bad") is GatewayResult.ProtocolFailure)
        assertTrue(
            gateway.uploadFile(document("data"), ShareOptions(), "bad") is GatewayResult.ProtocolFailure,
        )
        assertEquals(0, server.requestCount)
    }

    @Test
    fun uploadsFileAsBoundedMultipartAndMapsResponse() = runTest {
        server.enqueue(
            jsonResponse(
                201,
                """
                    {
                      "code":"file12",
                      "url":"https://invalid/f/file12",
                      "original_name":"fixture.txt",
                      "size_bytes":4,
                      "encrypted":false,
                      "expires_at":null,
                      "max_views":1,
                      "created_at":"now"
                    }
                """.trimIndent(),
            ),
        )

        val result = gateway().uploadFile(document("data"), ShareOptions(maxViews = 1), "fixed-a1-key")

        assertTrue(result is GatewayResult.Success<CreatedFile>)
        result as GatewayResult.Success
        assertEquals("file12", result.value.code)
        assertEquals(4, result.value.sizeBytes)
        val request = server.takeRequest()
        assertEquals("fixed-a1-key", request.headers["Idempotency-Key"])
        assertTrue(request.body?.utf8()?.contains("data") == true)
    }

    @Test
    fun mapsCreateTransportAndProtocolFailuresWithoutUnsafeRetry() = runTest {
        val disconnecting = OkHttpClient.Builder()
            .addInterceptor { throw IOException("offline") }
            .retryOnConnectionFailure(false)
            .build()
        val transport = OkHttpClipShareGateway(server.url("/"), disconnecting, RetrySleeper { })
            .createText("hello", ShareOptions(), "fixed-a1-key")
        assertTrue(transport is GatewayResult.TransportFailure)
        assertEquals("fixed-a1-key", (transport as GatewayResult.TransportFailure).idempotencyKey)

        val invalidCreateResponse =
            """{"code":"invalid-code","url":"x","expires_at":null,"max_views":null,"created_at":"now"}"""
        server.enqueue(jsonResponse(201, invalidCreateResponse))
        assertTrue(
            gateway().createText("hello", ShareOptions(), "fixed-a1-key") is GatewayResult.ProtocolFailure,
        )
    }

    @Test
    fun readsTextAndRejectsInvalidOrMismatchedCodes() = runTest {
        val gateway = gateway()
        assertTrue(gateway.receiveText("invalid-code") is GatewayResult.ProtocolFailure)

        server.enqueue(
            jsonResponse(
                200,
                """{"code":"aB12","content":"hello","expires_at":null,"remaining_views":1,"created_at":"now"}""",
            ),
        )
        val success = gateway.receiveText("aB12")
        assertTrue(success is GatewayResult.Success<ReceivedShare>)
        assertEquals("hello", (success as GatewayResult.Success).value.content)

        server.enqueue(
            jsonResponse(
                200,
                """{"code":"other","content":"hello","expires_at":null,"remaining_views":1,"created_at":"now"}""",
            ),
        )
        assertTrue(gateway.receiveText("aB12") is GatewayResult.ProtocolFailure)
    }

    @Test
    fun readsFileMetadataWithSafeRetryAndFrozenFields() = runTest {
        server.enqueue(problemResponse(503, retryAfter = 99))
        server.enqueue(
            jsonResponse(
                200,
                """
                    {
                      "code":"aB12",
                      "original_name":"fixture.bin",
                      "size_bytes":4,
                      "encrypted":false,
                      "content_type":"application/octet-stream",
                      "preview_available":false,
                      "expires_at":null,
                      "remaining_views":1,
                      "created_at":"now",
                      "kind":"file"
                    }
                """.trimIndent(),
            ),
        )

        val result = gateway(sleeper = RetrySleeper { }).getFileMetadata("aB12")

        assertTrue(result is GatewayResult.Success<RemoteFileMetadata>)
        assertEquals("fixture.bin", (result as GatewayResult.Success).value.originalName)
        assertEquals(2, server.requestCount)
    }

    @Test
    fun metadataRejectsInvalidCodeEncryptedAndOversizeResponses() = runTest {
        val gateway = gateway()
        assertTrue(gateway.getFileMetadata("invalid-code") is GatewayResult.ProtocolFailure)

        server.enqueue(fileMetadataResponse(encrypted = true, sizeBytes = 4))
        assertTrue(gateway.getFileMetadata("aB12") is GatewayResult.ProtocolFailure)

        server.enqueue(fileMetadataResponse(encrypted = false, sizeBytes = MAX_FILE_BYTES + 1))
        assertTrue(gateway.getFileMetadata("aB12") is GatewayResult.ProtocolFailure)
    }

    @Test
    fun metadataTransportFailureIsSafeToRetry() = runTest {
        val client = OkHttpClient.Builder()
            .addInterceptor { throw IOException("offline") }
            .retryOnConnectionFailure(false)
            .build()

        val result = OkHttpClipShareGateway(server.url("/"), client, RetrySleeper { })
            .getFileMetadata("aB12")

        assertTrue(result is GatewayResult.TransportFailure)
        assertEquals(false, (result as GatewayResult.TransportFailure).mayHaveReachedServer)
    }

    @Test
    fun downloadsFileStreamingAndReportsWrittenBytes() = runTest {
        server.enqueue(
            MockResponse.Builder()
                .code(200)
                .addHeader("Cache-Control", "no-store")
                .addHeader("Content-Type", "application/octet-stream")
                .body("streamed")
                .build(),
        )
        val output = ByteArrayOutputStream()

        val result = gateway().downloadFile(
            "aB12",
            WritableDocument(OutputStreamFactory { output }),
        )

        assertTrue(result is GatewayResult.Success<DownloadReceipt>)
        assertEquals(8, (result as GatewayResult.Success).value.bytesWritten)
        assertEquals("streamed", output.toString(Charsets.UTF_8))
    }

    @Test
    fun downloadRejectsInvalidCodeProblemAndMissingNoStore() = runTest {
        val destination = WritableDocument(OutputStreamFactory { ByteArrayOutputStream() })
        val gateway = gateway()
        assertTrue(gateway.downloadFile("invalid-code", destination) is GatewayResult.ProtocolFailure)

        server.enqueue(problemResponse(404))
        assertTrue(gateway.downloadFile("aB12", destination) is GatewayResult.Rejected)

        server.enqueue(MockResponse.Builder().code(200).body("unsafe").build())
        assertTrue(gateway.downloadFile("aB12", destination) is GatewayResult.ProtocolFailure)
    }

    @Test
    fun downloadDisconnectAndDestinationPermissionAreOutcomeUnknown() = runTest {
        val destination = WritableDocument(OutputStreamFactory { ByteArrayOutputStream() })
        val disconnecting = OkHttpClient.Builder()
            .addInterceptor { throw IOException("offline") }
            .retryOnConnectionFailure(false)
            .build()
        val disconnected = OkHttpClipShareGateway(server.url("/"), disconnecting, RetrySleeper { })
            .downloadFile("aB12", destination)
        assertTrue(disconnected is GatewayResult.OutcomeUnknown)

        server.enqueue(
            MockResponse.Builder()
                .code(200)
                .addHeader("Cache-Control", "no-store")
                .body("content")
                .build(),
        )
        val denied = gateway().downloadFile(
            "aB12",
            WritableDocument(OutputStreamFactory { throw SecurityException("revoked") }),
        )
        assertTrue(denied is GatewayResult.OutcomeUnknown)
    }

    @Test
    fun malformedProblemAndOversizeJsonAreProtocolFailures() = runTest {
        server.enqueue(jsonResponse(400, "{}", "application/problem+json"))
        assertTrue(
            gateway().createText("hello", ShareOptions(), "fixed-a1-key") is GatewayResult.ProtocolFailure,
        )

        server.enqueue(
            MockResponse.Builder()
                .code(200)
                .addHeader("Cache-Control", "no-store")
                .addHeader("Content-Length", (1024 * 1024 + 1).toString())
                .body("small")
                .build(),
        )
        assertTrue(gateway().receiveText("aB12") is GatewayResult.ProtocolFailure)
    }

    @Test
    fun createStopsAfterMaximumTransientAttemptsAndMapsFinalProblem() = runTest {
        repeat(3) { server.enqueue(problemResponse(503)) }

        val result = gateway(sleeper = RetrySleeper { }).createText(
            "hello",
            ShareOptions(),
            "fixed-a1-key",
        )

        assertTrue(result is GatewayResult.Rejected)
        assertEquals(3, server.requestCount)
    }

    @Test
    fun uploadMapsRevokedDocumentPermissionWithoutOpeningTwice() = runTest {
        var opens = 0
        val document = ReadableDocument(
            "revoked.bin",
            "application/octet-stream",
            null,
            InputStreamFactory {
                opens += 1
                throw SecurityException("revoked")
            },
        )

        val result = gateway().uploadFile(document, ShareOptions(), "fixed-a1-key")

        assertTrue(result is GatewayResult.TransportFailure)
        assertEquals(false, (result as GatewayResult.TransportFailure).mayHaveReachedServer)
        assertEquals(1, opens)
    }

    @Test
    fun uploadsUnknownLengthAndInvalidMimeWithBoundedStreaming() = runTest {
        repeat(2) {
            server.enqueue(
                jsonResponse(
                    201,
                    """
                        {
                          "code":"file12",
                          "url":"https://invalid/f/file12",
                          "original_name":"fixture.txt",
                          "size_bytes":4,
                          "encrypted":false,
                          "expires_at":null,
                          "max_views":null,
                          "created_at":"now"
                        }
                    """.trimIndent(),
                ),
            )
        }
        val gateway = gateway()
        val unknownLength = ReadableDocument(
            "fixture.txt",
            "text/plain",
            null,
            InputStreamFactory { ByteArrayInputStream("data".toByteArray()) },
        )
        val invalidMime = ReadableDocument(
            "fixture.txt",
            "not a mime type",
            4,
            InputStreamFactory { ByteArrayInputStream("data".toByteArray()) },
        )

        assertTrue(
            gateway.uploadFile(unknownLength, ShareOptions(), "fixed-a1-key") is GatewayResult.Success,
        )
        assertTrue(
            gateway.uploadFile(invalidMime, ShareOptions(), "fixed-a1-key") is GatewayResult.Success,
        )
        assertEquals(2, server.requestCount)
    }

    @Test
    fun responseValidationRejectsMismatchedProblemsAndInvalidFileFields() = runTest {
        server.enqueue(
            jsonResponse(
                400,
                """{"type":"bad","title":"bad","status":404,"detail":"mismatch"}""",
                "application/problem+json",
            ),
        )
        assertTrue(
            gateway().createText("hello", ShareOptions(), "fixed-a1-key") is GatewayResult.ProtocolFailure,
        )

        server.enqueue(
            jsonResponse(
                201,
                """
                    {
                      "code":"file12","url":"https://invalid/f/file12","original_name":"fixture.txt",
                      "size_bytes":4,"encrypted":true,"expires_at":null,"max_views":null,"created_at":"now"
                    }
                """.trimIndent(),
            ),
        )
        assertTrue(
            gateway().uploadFile(document("data"), ShareOptions(), "fixed-a1-key") is
                GatewayResult.ProtocolFailure,
        )

        server.enqueue(
            jsonResponse(
                200,
                """
                    {
                      "code":"aB12","original_name":"fixture.bin","size_bytes":4,"encrypted":false,
                      "content_type":"application/octet-stream","preview_available":false,
                      "expires_at":null,"remaining_views":1,"created_at":"now","kind":"text"
                    }
                """.trimIndent(),
            ),
        )
        assertTrue(gateway().getFileMetadata("aB12") is GatewayResult.ProtocolFailure)
    }

    private fun gateway(sleeper: RetrySleeper = RetrySleeper { }) = OkHttpClipShareGateway(
        server.url("/"),
        ClipShareHttpClient.create(),
        sleeper,
    )

    private fun document(content: String) = ReadableDocument(
        displayName = "fixture.txt",
        contentType = "text/plain",
        sizeBytes = content.toByteArray().size.toLong(),
        inputStreamFactory = InputStreamFactory { ByteArrayInputStream(content.toByteArray()) },
    )

    private fun fileMetadataResponse(encrypted: Boolean, sizeBytes: Long): MockResponse = jsonResponse(
        200,
        """
            {
              "code":"aB12",
              "original_name":"fixture.bin",
              "size_bytes":$sizeBytes,
              "encrypted":$encrypted,
              "content_type":"application/octet-stream",
              "preview_available":false,
              "expires_at":null,
              "remaining_views":1,
              "created_at":"now",
              "kind":"file"
            }
        """.trimIndent(),
    )

    private fun problemResponse(status: Int, retryAfter: Int? = null): MockResponse {
        val builder = MockResponse.Builder()
            .code(status)
            .addHeader("Cache-Control", "no-store")
            .addHeader("Content-Type", "application/problem+json")
            .body("""{"type":"transient","title":"稍后重试","status":$status,"detail":"test"}""")
        if (retryAfter != null) builder.addHeader("Retry-After", retryAfter.toString())
        return builder.build()
    }

    private fun jsonResponse(
        status: Int,
        body: String,
        contentType: String = "application/json",
    ): MockResponse = MockResponse.Builder()
        .code(status)
        .addHeader("Cache-Control", "no-store")
        .addHeader("Content-Type", contentType)
        .body(body)
        .build()
}
