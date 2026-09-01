package com.clipshare.android

import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import com.clipshare.core.model.MAX_TEXT_CONTRACT_CHARS
import com.clipshare.core.model.MAX_TEXT_UTF8_BYTES
import java.nio.charset.StandardCharsets
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.repeatOnLifecycle
import kotlinx.coroutines.launch

private const val SAVED_SEND_DRAFT = "a1.send-draft"
private const val SAVED_RECEIVE_INPUT = "a1.receive-input"

class MainActivity : ComponentActivity() {
    private lateinit var graph: AppGraph

    private val openDocument = registerForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri == null) {
            graph.sendController.reportMessage("未选择文件")
        } else {
            try {
                graph.sendController.selectDocument(graph.documentResolver.readable(uri))
            } catch (ignored: SecurityException) {
                graph.sendController.reportMessage("无法访问所选文件")
            }
        }
    }

    private val createDocument = registerForActivityResult(
        ActivityResultContracts.CreateDocument("application/octet-stream"),
    ) { uri ->
        if (uri == null) {
            graph.receiveController.reportMessage("未选择保存位置；没有发起下载")
        } else {
            lifecycleScope.launch {
                graph.receiveController.downloadInspectedFile(graph.documentResolver.writable(uri))
            }
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        graph = AppGraph(applicationContext)
        savedInstanceState?.getString(SAVED_SEND_DRAFT)?.let(graph.sendController::updateDraft)
        savedInstanceState?.getString(SAVED_RECEIVE_INPUT)?.let(graph.receiveController::updateInput)
        acceptSharedText(intent)
        lifecycleScope.launch {
            repeatOnLifecycle(Lifecycle.State.STARTED) {
                graph.settingsRepository.settings.collect { settings ->
                    graph.clipboardCoordinator.setMonitoringEnabled(settings.monitorClipboard)
                }
            }
        }
        setContent {
            ClipShareApp(
                sendController = graph.sendController,
                receiveController = graph.receiveController,
                settingsController = graph.settingsController,
                defaultBaseUrl = BuildConfig.DEFAULT_BASE_URL,
                onPickFile = { openDocument.launch(arrayOf("*/*")) },
                onCreateDownload = { fileName -> createDocument.launch(fileName) },
            )
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        acceptSharedText(intent)
    }

    override fun onStart() {
        super.onStart()
        graph.clipboardCoordinator.setVisible(true)
    }

    override fun onStop() {
        graph.clipboardCoordinator.setVisible(false)
        super.onStop()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (::graph.isInitialized) graph.clipboardCoordinator.setFocused(hasFocus)
    }

    override fun onDestroy() {
        if (::graph.isInitialized) graph.close()
        super.onDestroy()
    }

    override fun onSaveInstanceState(outState: Bundle) {
        if (::graph.isInitialized) {
            graph.sendController.state.value.draft.boundedSavedText()?.let {
                outState.putString(SAVED_SEND_DRAFT, it)
            }
            graph.receiveController.state.value.input.boundedSavedText()?.let {
                outState.putString(SAVED_RECEIVE_INPUT, it)
            }
        }
        super.onSaveInstanceState(outState)
    }

    private fun acceptSharedText(intent: Intent) {
        if (intent.action != Intent.ACTION_SEND || intent.type != "text/plain") return
        val text = intent.getCharSequenceExtra(Intent.EXTRA_TEXT)?.toString() ?: return
        graph.sendController.acceptExternalText(text, "系统分享")
    }
}

private fun String.boundedSavedText(): String? = takeIf {
    it.length <= MAX_TEXT_CONTRACT_CHARS &&
        it.toByteArray(StandardCharsets.UTF_8).size <= MAX_TEXT_UTF8_BYTES
}
