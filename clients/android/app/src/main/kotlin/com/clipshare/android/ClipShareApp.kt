package com.clipshare.android

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.PrimaryTabRow
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Tab
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.clipshare.core.model.ClientSettings
import com.clipshare.core.model.Expiry
import com.clipshare.core.model.ShareOptions
import com.clipshare.feature.receive.ReceiveController
import com.clipshare.feature.receive.ReceiveUiState
import com.clipshare.feature.send.SendController
import com.clipshare.feature.send.SendUiState
import com.clipshare.feature.settings.SettingsController
import kotlinx.coroutines.launch

private enum class Screen(val title: String) {
    SEND("发送"),
    RECEIVE("接收"),
    SETTINGS("设置"),
}

private const val SINGLE_VIEW_CHOICE = 1
private const val FIVE_VIEW_CHOICE = 5

@Suppress("FunctionNaming")
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ClipShareApp(
    sendController: SendController,
    receiveController: ReceiveController,
    settingsController: SettingsController,
    defaultBaseUrl: String,
    onPickFile: () -> Unit,
    onCreateDownload: (String) -> Unit,
) {
    val sendState by sendController.state.collectAsStateWithLifecycle()
    val receiveState by receiveController.state.collectAsStateWithLifecycle()
    val settings by settingsController.settings.collectAsStateWithLifecycle(ClientSettings())
    var screen by rememberSaveable { mutableStateOf(Screen.SEND) }
    val coroutineScope = rememberCoroutineScope()

    MaterialTheme {
        Surface(modifier = Modifier.fillMaxSize()) {
            Scaffold(
                topBar = { TopAppBar(title = { Text("ClipShare v0.3-A1") }) },
            ) { padding ->
                Column(modifier = Modifier.padding(padding)) {
                    PrimaryTabRow(selectedTabIndex = screen.ordinal) {
                        Screen.entries.forEach { candidate ->
                            Tab(
                                selected = candidate == screen,
                                onClick = { screen = candidate },
                                text = { Text(candidate.title) },
                            )
                        }
                    }
                    when (screen) {
                        Screen.SEND -> SendScreen(
                            state = sendState,
                            onDraftChanged = sendController::updateDraft,
                            onSendText = { options ->
                                coroutineScope.launch { sendController.sendText(options) }
                            },
                            onPickFile = onPickFile,
                            onSendFile = { options ->
                                coroutineScope.launch { sendController.sendSelectedFile(options) }
                            },
                            onRetry = { coroutineScope.launch { sendController.retryLastCreate() } },
                        )

                        Screen.RECEIVE -> ReceiveScreen(
                            state = receiveState,
                            onInputChanged = receiveController::updateInput,
                            onReceiveText = { coroutineScope.launch { receiveController.receiveText() } },
                            onInspectFile = { coroutineScope.launch { receiveController.inspectFile() } },
                            onDownload = onCreateDownload,
                        )

                        Screen.SETTINGS -> SettingsScreen(
                            settings = settings,
                            defaultBaseUrl = defaultBaseUrl,
                            onMonitorChanged = {
                                coroutineScope.launch { settingsController.setMonitorClipboard(it) }
                            },
                            onAutoSyncChanged = {
                                coroutineScope.launch { settingsController.setAutoSyncPairedDevices(it) }
                            },
                            onSaveEndpoint = {
                                coroutineScope.launch { settingsController.setServerBaseUrl(it) }
                            },
                        )
                    }
                }
            }
        }
    }
}

@Suppress("FunctionNaming", "LongMethod")
@Composable
private fun SendScreen(
    state: SendUiState,
    onDraftChanged: (String) -> Unit,
    onSendText: (ShareOptions) -> Unit,
    onPickFile: () -> Unit,
    onSendFile: (ShareOptions) -> Unit,
    onRetry: () -> Unit,
) {
    var expiry by rememberSaveable { mutableStateOf(Expiry.ONE_DAY) }
    var maxViews by rememberSaveable { mutableStateOf<Int?>(null) }
    val options = ShareOptions(expiry, maxViews)
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(20.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text("显式发送文本或 URL", style = MaterialTheme.typography.titleMedium)
        Text("A1 尚未接入端到端加密；内容会以明文交给所配置服务器，请勿发送敏感信息。")
        Text("系统分享和前台剪贴板只会填入草稿；必须再次点击发送。")
        OutlinedTextField(
            value = state.draft,
            onValueChange = onDraftChanged,
            label = { Text("文本或 HTTP(S) URL") },
            minLines = 5,
            modifier = Modifier
                .fillMaxWidth()
                .testTag("send_draft_input"),
            enabled = !state.inProgress,
        )
        Text("有效期")
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Expiry.entries.forEach { candidate ->
                FilterChip(
                    selected = expiry == candidate,
                    onClick = { expiry = candidate },
                    label = { Text(candidate.label()) },
                )
            }
        }
        Text("最大查看/下载次数")
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            listOf<Int?>(null, SINGLE_VIEW_CHOICE, FIVE_VIEW_CHOICE).forEach { candidate ->
                FilterChip(
                    selected = maxViews == candidate,
                    onClick = { maxViews = candidate },
                    label = { Text(candidate?.toString() ?: "不限") },
                )
            }
        }
        Button(onClick = { onSendText(options) }, enabled = !state.inProgress) {
            Text("发送文本")
        }
        HorizontalDivider()
        Text("通过系统文件选择器上传", style = MaterialTheme.typography.titleMedium)
        Text("全程流式处理，客户端与服务端上限均为 100 MiB。")
        Text(state.selectedFileName ?: "尚未选择文件")
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            TextButton(onClick = onPickFile, enabled = !state.inProgress) { Text("选择文件") }
            Button(
                onClick = { onSendFile(options) },
                enabled = !state.inProgress && state.selectedFileName != null,
            ) { Text("上传文件") }
        }
        ResultBlock(state.statusMessage, state.createdCode, state.createdUrl)
        if (state.canRetryLastCreate) {
            Button(onClick = onRetry, enabled = !state.inProgress) {
                Text("复用原幂等键重试")
            }
        }
    }
}

private fun Expiry.label(): String = when (this) {
    Expiry.ONE_HOUR -> "1 小时"
    Expiry.ONE_DAY -> "24 小时"
    Expiry.SEVEN_DAYS -> "7 天"
    Expiry.FOREVER -> "永久"
}

@Suppress("FunctionNaming")
@Composable
private fun ReceiveScreen(
    state: ReceiveUiState,
    onInputChanged: (String) -> Unit,
    onReceiveText: () -> Unit,
    onInspectFile: () -> Unit,
    onDownload: (String) -> Unit,
) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(20.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text("通过短码或链接显式接收", style = MaterialTheme.typography.titleMedium)
        OutlinedTextField(
            value = state.input,
            onValueChange = onInputChanged,
            label = { Text("1–8 位短码或 HTTP(S) 链接") },
            singleLine = true,
            modifier = Modifier
                .fillMaxWidth()
                .testTag("receive_input"),
            enabled = !state.inProgress,
        )
        Text("读取文本会消费一次查看次数；网络中断后结果未知，客户端不会自动重试。")
        Button(onClick = onReceiveText, enabled = !state.inProgress) { Text("读取文本（消费一次）") }
        HorizontalDivider()
        Text("文件元数据不会消费次数。确认下载后才会消费一次下载次数。")
        TextButton(onClick = onInspectFile, enabled = !state.inProgress) { Text("查看文件信息") }
        state.fileMetadata?.let { metadata ->
            Text("${metadata.originalName} · ${metadata.sizeBytes} 字节 · ${metadata.contentType}")
            Button(
                onClick = { onDownload(metadata.originalName) },
                enabled = !state.inProgress,
            ) { Text("选择位置并下载（消费一次）") }
        }
        if (state.statusMessage.isNotBlank()) Text(state.statusMessage)
        state.receivedText?.let { content ->
            Text("接收内容", style = MaterialTheme.typography.titleMedium)
            SelectionContainer { Text(content) }
        }
    }
}

@Suppress("FunctionNaming")
@Composable
private fun SettingsScreen(
    settings: ClientSettings,
    defaultBaseUrl: String,
    onMonitorChanged: (Boolean) -> Unit,
    onAutoSyncChanged: (Boolean) -> Unit,
    onSaveEndpoint: (String) -> Unit,
) {
    var endpointDraft by rememberSaveable { mutableStateOf(settings.serverBaseUrl) }
    LaunchedEffect(settings.serverBaseUrl) { endpointDraft = settings.serverBaseUrl }
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(20.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp),
    ) {
        Text("剪贴板边界", style = MaterialTheme.typography.titleMedium)
        SettingSwitch(
            label = "监测剪贴板",
            testTag = "monitor_clipboard_switch",
            checked = settings.monitorClipboard,
            onCheckedChange = onMonitorChanged,
        )
        Text("仅在本界面可见且拥有窗口焦点时监听；离开、锁屏或失焦会立即注销监听。")
        SettingSwitch(
            label = "自动同步到已配对设备",
            testTag = "auto_sync_switch",
            checked = settings.autoSyncPairedDevices,
            onCheckedChange = onAutoSyncChanged,
        )
        Text("A1 只独立保存该偏好。尚无配对设备或同步通道，因此开关不会触发任何网络发送。")
        HorizontalDivider()
        Text("服务器", style = MaterialTheme.typography.titleMedium)
        OutlinedTextField(
            value = endpointDraft,
            onValueChange = { endpointDraft = it },
            label = { Text("服务器基础 URL") },
            placeholder = {
                Text(defaultBaseUrl.ifBlank { "https://your-server.example/" })
            },
            singleLine = true,
            modifier = Modifier.fillMaxWidth(),
        )
        if (settings.serverBaseUrl.isBlank() && defaultBaseUrl.isNotBlank()) {
            Text("当前未保存地址，Debug 将使用：$defaultBaseUrl")
        }
        Text("Release 只允许 HTTPS；Debug/Test 只允许 localhost、回环地址或 10.0.2.2。")
        Button(onClick = { onSaveEndpoint(endpointDraft) }) { Text("保存服务器地址") }
    }
}

@Suppress("FunctionNaming")
@Composable
private fun SettingSwitch(
    label: String,
    testTag: String,
    checked: Boolean,
    onCheckedChange: (Boolean) -> Unit,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .selectable(
                selected = checked,
                onClick = { onCheckedChange(!checked) },
                role = Role.Switch,
            ),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(label, modifier = Modifier.weight(1f))
        Spacer(Modifier.width(12.dp))
        Switch(
            checked = checked,
            onCheckedChange = onCheckedChange,
            modifier = Modifier.testTag(testTag),
        )
    }
}

@Suppress("FunctionNaming")
@Composable
private fun ResultBlock(message: String, code: String?, url: String?) {
    if (message.isNotBlank()) Text(message)
    if (code != null || url != null) {
        Spacer(Modifier.height(4.dp))
        SelectionContainer {
            Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                code?.let { Text("短码：$it") }
                url?.let { Text("链接：$it") }
            }
        }
    }
}
