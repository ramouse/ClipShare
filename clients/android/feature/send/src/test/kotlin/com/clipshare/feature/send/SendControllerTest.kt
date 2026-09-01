package com.clipshare.feature.send

import com.clipshare.core.model.ClipShareGateway
import com.clipshare.core.model.CreatedFile
import com.clipshare.core.model.CreatedShare
import com.clipshare.core.model.DownloadReceipt
import com.clipshare.core.model.GatewayAccess
import com.clipshare.core.model.GatewayProvider
import com.clipshare.core.model.GatewayResult
import com.clipshare.core.model.IdempotencyKeyFactory
import com.clipshare.core.model.InputStreamFactory
import com.clipshare.core.model.MAX_FILE_BYTES
import com.clipshare.core.model.ReadableDocument
import com.clipshare.core.model.ReceivedShare
import com.clipshare.core.model.RemoteFileMetadata
import com.clipshare.core.model.RemoteProblem
import com.clipshare.core.model.ShareOptions
import com.clipshare.core.model.WritableDocument
import java.io.ByteArrayInputStream
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class SendControllerTest {
    @Test
    fun externalTextOnlyFillsDraft() = runTest {
        val gateway = RecordingGateway()
        val controller = controller(gateway)

        controller.acceptExternalText("https://example.test", "系统分享")

        assertEquals("https://example.test", controller.state.value.draft)
        assertTrue(controller.state.value.statusMessage.contains("尚未发送"))
        assertEquals(0, gateway.createCalls)
    }

    @Test
    fun explicitRetryReusesIdempotencyKey() = runTest {
        val gateway = RecordingGateway().apply { failFirstCreate = true }
        val controller = controller(gateway)
        controller.updateDraft("hello")

        controller.sendText()
        assertTrue(controller.state.value.canRetryLastCreate)
        controller.retryLastCreate()

        assertEquals(listOf("a1-test-key", "a1-test-key"), gateway.keys)
        assertEquals("abc123", controller.state.value.createdCode)
        assertFalse(controller.state.value.canRetryLastCreate)
    }

    @Test
    fun editingDraftInvalidatesRetryForOriginalPayload() = runTest {
        val gateway = RecordingGateway().apply { failFirstCreate = true }
        val controller = controller(gateway)
        controller.updateDraft("original")
        controller.sendText()

        controller.updateDraft("replacement")
        controller.retryLastCreate()

        assertEquals(listOf("a1-test-key"), gateway.keys)
        assertFalse(controller.state.value.canRetryLastCreate)
        assertTrue(controller.state.value.statusMessage.contains("没有可安全复用"))
    }

    @Test
    fun rejectsInvalidExternalAndExplicitTextWithoutCallingGateway() = runTest {
        val gateway = RecordingGateway()
        val controller = controller(gateway)

        controller.acceptExternalText(" ", "系统分享")
        assertTrue(controller.state.value.statusMessage.contains("未采用"))
        controller.updateDraft("")
        controller.sendText()

        assertEquals(0, gateway.createCalls)
        assertTrue(controller.state.value.statusMessage.contains("不能为空"))
    }

    @Test
    fun invalidEndpointFailsBeforeCreate() = runTest {
        val gateway = RecordingGateway()
        val controller = SendController(
            GatewayProvider { GatewayAccess.InvalidEndpoint("invalid endpoint") },
            IdempotencyKeyFactory { "a1-test-key" },
        )
        controller.updateDraft("hello")

        controller.sendText()

        assertEquals("invalid endpoint", controller.state.value.statusMessage)
        assertEquals(0, gateway.createCalls)
    }

    @Test
    fun selectedFileRequiresExplicitUploadAndMapsSuccess() = runTest {
        val gateway = RecordingGateway()
        val controller = controller(gateway)
        val document = document("file-data")

        controller.selectDocument(document)
        assertEquals(0, gateway.uploadCalls)
        controller.sendSelectedFile()

        assertEquals(1, gateway.uploadCalls)
        assertEquals("file123", controller.state.value.createdCode)
        assertEquals("fixture.txt", controller.state.value.selectedFileName)
    }

    @Test
    fun missingAndKnownOversizeFilesNeverOpenGateway() = runTest {
        val gateway = RecordingGateway()
        val controller = controller(gateway)
        controller.sendSelectedFile()
        assertTrue(controller.state.value.statusMessage.contains("请先"))

        controller.selectDocument(
            ReadableDocument(
                "large.bin",
                "application/octet-stream",
                MAX_FILE_BYTES + 1,
                InputStreamFactory { error("must not open") },
            ),
        )
        assertTrue(controller.state.value.statusMessage.contains("100 MiB"))
        controller.sendSelectedFile()

        assertEquals(0, gateway.uploadCalls)
    }

    @Test
    fun mapsRejectedProtocolOutcomeUnknownAndNonRetryableTransport() = runTest {
        val gateway = RecordingGateway()
        val controller = controller(gateway)
        controller.updateDraft("hello")

        gateway.textResult = GatewayResult.Rejected(RemoteProblem("type", "bad", 400, "detail"))
        controller.sendText()
        assertTrue(controller.state.value.statusMessage.contains("400 bad"))
        assertFalse(controller.state.value.canRetryLastCreate)

        gateway.textResult = GatewayResult.ProtocolFailure("protocol")
        controller.sendText()
        assertEquals("protocol", controller.state.value.statusMessage)

        gateway.textResult = GatewayResult.OutcomeUnknown(
            com.clipshare.core.model.ConsumingOperation.READ_TEXT,
            "unknown",
        )
        controller.sendText()
        assertEquals("unknown", controller.state.value.statusMessage)

        gateway.textResult = GatewayResult.TransportFailure("offline", false)
        controller.sendText()
        assertEquals("offline", controller.state.value.statusMessage)
        assertFalse(controller.state.value.canRetryLastCreate)
    }

    @Test
    fun reportMessageAndSuccessfulTextExposeResult() = runTest {
        val controller = controller(RecordingGateway())
        controller.reportMessage("notice")
        assertEquals("notice", controller.state.value.statusMessage)
        controller.updateDraft("hello")

        controller.sendText()

        assertEquals("abc123", controller.state.value.createdCode)
        assertTrue(controller.state.value.statusMessage.contains("文本已创建"))
    }

    private fun controller(gateway: RecordingGateway) = SendController(
        GatewayProvider { GatewayAccess.Available(gateway) },
        IdempotencyKeyFactory { "a1-test-key" },
    )

    private fun document(content: String) = ReadableDocument(
        "fixture.txt",
        "text/plain",
        content.toByteArray().size.toLong(),
        InputStreamFactory { ByteArrayInputStream(content.toByteArray()) },
    )

    private class RecordingGateway : ClipShareGateway {
        var createCalls = 0
        var failFirstCreate = false
        var uploadCalls = 0
        val keys = mutableListOf<String>()
        var textResult: GatewayResult<CreatedShare>? = null

        override suspend fun createText(
            content: String,
            options: ShareOptions,
            idempotencyKey: String,
        ): GatewayResult<CreatedShare> {
            createCalls += 1
            keys += idempotencyKey
            if (failFirstCreate && createCalls == 1) {
                return GatewayResult.TransportFailure("unknown", true, idempotencyKey)
            }
            return textResult
                ?: GatewayResult.Success(CreatedShare("abc123", "https://invalid/s/abc123", null, null, "now"))
        }

        override suspend fun uploadFile(
            document: ReadableDocument,
            options: ShareOptions,
            idempotencyKey: String,
        ): GatewayResult<CreatedFile> {
            uploadCalls += 1
            keys += idempotencyKey
            return GatewayResult.Success(
                CreatedFile("file123", "https://invalid/f/file123", document.displayName, 9, null, null, "now"),
            )
        }

        override suspend fun receiveText(code: String): GatewayResult<ReceivedShare> = error("not used")

        override suspend fun getFileMetadata(code: String): GatewayResult<RemoteFileMetadata> = error("not used")

        override suspend fun downloadFile(
            code: String,
            destination: WritableDocument,
        ): GatewayResult<DownloadReceipt> = error("not used")
    }
}
