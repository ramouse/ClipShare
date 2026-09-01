package com.clipshare.feature.receive

import com.clipshare.core.model.ClipShareGateway
import com.clipshare.core.model.CreatedFile
import com.clipshare.core.model.CreatedShare
import com.clipshare.core.model.DownloadReceipt
import com.clipshare.core.model.ConsumingOperation
import com.clipshare.core.model.GatewayAccess
import com.clipshare.core.model.GatewayProvider
import com.clipshare.core.model.GatewayResult
import com.clipshare.core.model.ReadableDocument
import com.clipshare.core.model.ReceivedShare
import com.clipshare.core.model.RemoteFileMetadata
import com.clipshare.core.model.RemoteProblem
import com.clipshare.core.model.ShareOptions
import com.clipshare.core.model.WritableDocument
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ReceiveControllerTest {
    @Test
    fun changingInputClearsMetadataFromPreviousCode() = runTest {
        val gateway = RecordingGateway()
        val controller = ReceiveController(
            GatewayProvider { GatewayAccess.Available(gateway) },
        )
        controller.updateInput("old123")
        controller.inspectFile()
        assertEquals("old123", controller.state.value.fileMetadata?.code)

        controller.updateInput("new456")

        assertNull(controller.state.value.fileMetadata)
    }

    @Test
    fun receivesTextOnlyAfterValidExplicitInput() = runTest {
        val gateway = RecordingGateway()
        val controller = controller(gateway)
        controller.updateInput("bad-code")
        controller.receiveText()
        assertEquals(0, gateway.receiveCalls)
        assertFalse(controller.state.value.inProgress)

        controller.updateInput("abc123")
        controller.receiveText()

        assertEquals(1, gateway.receiveCalls)
        assertEquals("received", controller.state.value.receivedText)
        assertEquals("读取成功", controller.state.value.statusMessage)
    }

    @Test
    fun invalidEndpointStopsReceiveBeforeGateway() = runTest {
        val gateway = RecordingGateway()
        val controller = ReceiveController(
            GatewayProvider { GatewayAccess.InvalidEndpoint("endpoint invalid") },
        )
        controller.updateInput("abc123")

        controller.receiveText()

        assertEquals("endpoint invalid", controller.state.value.statusMessage)
        assertEquals(0, gateway.receiveCalls)
    }

    @Test
    fun receiveMapsEveryFailureWithoutRetry() = runTest {
        val gateway = RecordingGateway()
        val controller = controller(gateway)
        controller.updateInput("abc123")

        gateway.receiveResult = GatewayResult.Rejected(RemoteProblem("type", "missing", 404, "gone"))
        controller.receiveText()
        assertTrue(controller.state.value.statusMessage.contains("404 missing"))

        gateway.receiveResult = GatewayResult.TransportFailure("offline", true)
        controller.receiveText()
        assertEquals("offline", controller.state.value.statusMessage)

        gateway.receiveResult = GatewayResult.OutcomeUnknown(ConsumingOperation.READ_TEXT, "maybe consumed")
        controller.receiveText()
        assertTrue(controller.state.value.statusMessage.contains("结果未知"))

        gateway.receiveResult = GatewayResult.ProtocolFailure("protocol")
        controller.receiveText()
        assertEquals("protocol", controller.state.value.statusMessage)
        assertEquals(4, gateway.receiveCalls)
    }

    @Test
    fun inspectFileMapsFailureAndReportMessage() = runTest {
        val gateway = RecordingGateway().apply {
            metadataResult = GatewayResult.ProtocolFailure("bad metadata")
        }
        val controller = controller(gateway)
        controller.reportMessage("notice")
        assertEquals("notice", controller.state.value.statusMessage)
        controller.updateInput("abc123")

        controller.inspectFile()

        assertEquals("bad metadata", controller.state.value.statusMessage)
        assertNull(controller.state.value.fileMetadata)
    }

    @Test
    fun downloadRequiresMetadataAndCurrentValidEndpoint() = runTest {
        val destination = WritableDocument { java.io.ByteArrayOutputStream() }
        val gateway = RecordingGateway()
        val access = arrayOf<GatewayAccess>(GatewayAccess.Available(gateway))
        val controller = ReceiveController(GatewayProvider { access[0] })

        controller.downloadInspectedFile(destination)
        assertTrue(controller.state.value.statusMessage.contains("先读取"))

        controller.updateInput("abc123")
        controller.inspectFile()
        access[0] = GatewayAccess.InvalidEndpoint("endpoint invalid")
        controller.downloadInspectedFile(destination)

        assertEquals("endpoint invalid", controller.state.value.statusMessage)
        assertEquals(0, gateway.downloadCalls)
    }

    @Test
    fun downloadMapsSuccessAndFailureResults() = runTest {
        val destination = WritableDocument { java.io.ByteArrayOutputStream() }
        val gateway = RecordingGateway()
        val controller = controller(gateway)
        controller.updateInput("abc123")
        controller.inspectFile()

        controller.downloadInspectedFile(destination)
        assertTrue(controller.state.value.statusMessage.contains("4 字节"))

        gateway.downloadResult = GatewayResult.OutcomeUnknown(
            ConsumingOperation.DOWNLOAD_FILE,
            "possibly consumed",
        )
        controller.downloadInspectedFile(destination)
        assertTrue(controller.state.value.statusMessage.contains("结果未知"))

        gateway.downloadResult = GatewayResult.Rejected(RemoteProblem("type", "gone", 410, "expired"))
        controller.downloadInspectedFile(destination)
        assertTrue(controller.state.value.statusMessage.contains("410 gone"))
        assertEquals(3, gateway.downloadCalls)
    }

    private fun controller(gateway: RecordingGateway) = ReceiveController(
        GatewayProvider { GatewayAccess.Available(gateway) },
    )

    private class RecordingGateway : ClipShareGateway {
        var receiveCalls = 0
        var downloadCalls = 0
        var receiveResult: GatewayResult<ReceivedShare> = GatewayResult.Success(
            ReceivedShare("abc123", "received", null, 1, "now"),
        )
        var metadataResult: GatewayResult<RemoteFileMetadata> = GatewayResult.Success(
            RemoteFileMetadata("abc123", "file.bin", 4, "application/octet-stream", null, 1, "now"),
        )
        var downloadResult: GatewayResult<DownloadReceipt> = GatewayResult.Success(
            DownloadReceipt(4, "application/octet-stream"),
        )

        override suspend fun createText(
            content: String,
            options: ShareOptions,
            idempotencyKey: String,
        ): GatewayResult<CreatedShare> = error("not used")

        override suspend fun uploadFile(
            document: ReadableDocument,
            options: ShareOptions,
            idempotencyKey: String,
        ): GatewayResult<CreatedFile> = error("not used")

        override suspend fun receiveText(code: String): GatewayResult<ReceivedShare> {
            receiveCalls += 1
            return receiveResult
        }

        override suspend fun getFileMetadata(code: String): GatewayResult<RemoteFileMetadata> =
            when (val result = metadataResult) {
                is GatewayResult.Success -> GatewayResult.Success(result.value.copy(code = code))
                else -> result
            }

        override suspend fun downloadFile(
            code: String,
            destination: WritableDocument,
        ): GatewayResult<DownloadReceipt> {
            downloadCalls += 1
            return downloadResult
        }
    }
}
