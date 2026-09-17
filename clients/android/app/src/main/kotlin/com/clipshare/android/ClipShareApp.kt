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
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
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
import androidx.compose.runtime.remember
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
import com.clipshare.feature.vault.CreateVaultFolderCommand
import com.clipshare.feature.vault.UpdateVaultTextCommand
import com.clipshare.feature.vault.RenameVaultFolderCommand
import com.clipshare.feature.vault.VaultController
import com.clipshare.feature.vault.VaultEntityKind
import com.clipshare.feature.vault.VaultEntitySelection
import com.clipshare.feature.vault.VaultFeatureState
import com.clipshare.feature.vault.VaultItemView
import com.clipshare.feature.vault.VaultPolicyCommand
import com.clipshare.feature.vault.VaultSection
import com.clipshare.core.vault.SyncPolicy
import com.clipshare.core.vault.VaultContentType
import kotlinx.coroutines.launch

private enum class Screen(val title: String) {
    VAULT("内容库"),
    SEND("发送"),
    RECEIVE("接收"),
    SETTINGS("设置"),
}

private const val SINGLE_VIEW_CHOICE = 1
private const val FIVE_VIEW_CHOICE = 5

@Suppress("FunctionNaming", "LongMethod")
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ClipShareApp(
    sendController: SendController,
    receiveController: ReceiveController,
    settingsController: SettingsController,
    vaultController: VaultController,
    defaultBaseUrl: String,
    onPickFile: () -> Unit,
    onCreateDownload: (String) -> Unit,
    onImportVaultFile: (String, String) -> Unit,
    onExportVaultFile: (String, String) -> Unit,
) {
    val sendState by sendController.state.collectAsStateWithLifecycle()
    val receiveState by receiveController.state.collectAsStateWithLifecycle()
    val settings by settingsController.settings.collectAsStateWithLifecycle(ClientSettings())
    val vaultState by vaultController.state.collectAsStateWithLifecycle()
    var screen by rememberSaveable { mutableStateOf(Screen.VAULT) }
    val coroutineScope = rememberCoroutineScope()

    MaterialTheme {
        Surface(modifier = Modifier.fillMaxSize()) {
            Scaffold(
                topBar = { TopAppBar(title = { Text("ClipShare v0.3-W2/A2") }) },
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
                        Screen.VAULT -> VaultScreen(
                            state = vaultState,
                            controller = vaultController,
                            onImportFile = onImportVaultFile,
                            onExportFile = onExportVaultFile,
                        )

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

@Suppress("FunctionNaming", "LongMethod", "CyclomaticComplexMethod")
@Composable
private fun VaultScreen(
    state: VaultFeatureState,
    controller: VaultController,
    onImportFile: (String, String) -> Unit,
    onExportFile: (String, String) -> Unit,
) {
    val scope = rememberCoroutineScope()
    val runVault: (suspend () -> Unit) -> Unit = { action ->
        scope.launch { runCatching { action() } }
    }
    var search by remember(state.sensitiveGeneration) { mutableStateOf("") }
    var folderName by remember(state.sensitiveGeneration) { mutableStateOf("") }
    var selectedFolderId by remember { mutableStateOf<String?>(null) }
    val activeFolders = state.snapshot.folders.filterNot { it.deleted }
    LaunchedEffect(activeFolders.map { it.id }) {
        if (selectedFolderId !in activeFolders.map { it.id }) selectedFolderId = activeFolders.firstOrNull()?.id
    }
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(20.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text("本地加密内容库", style = MaterialTheme.typography.titleMedium)
        Text("内容使用 Room 密文记录、Keystore 平台包装和加密文件 blob；“密码”与其他文件夹完全相同。")
        Row(
            modifier = Modifier.horizontalScroll(rememberScrollState()),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            VaultSection.entries.forEach { section ->
                FilterChip(
                    selected = state.section == section,
                    onClick = { runVault { controller.navigate(section, search) } },
                    label = { Text(section.label()) },
                )
            }
        }
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedTextField(
                value = search,
                onValueChange = { search = it },
                label = { Text("搜索（仅内存）") },
                modifier = Modifier.weight(1f).testTag("vault_search"),
                singleLine = true,
            )
            Button(
                onClick = { runVault { controller.navigate(state.section, search) } },
                enabled = !state.busy,
            ) { Text("搜索") }
        }
        HorizontalDivider()
        Text("新建普通文件夹", style = MaterialTheme.typography.titleMedium)
        OutlinedTextField(
            value = folderName,
            onValueChange = { folderName = it },
            label = { Text("文件夹名称") },
            modifier = Modifier.fillMaxWidth().testTag("vault_folder_name"),
            singleLine = true,
        )
        Text("父文件夹：${activeFolders.firstOrNull { it.id == selectedFolderId }?.name ?: "顶层"}")
        Row(
            modifier = Modifier.horizontalScroll(rememberScrollState()),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            FilterChip(
                selected = selectedFolderId == null,
                onClick = { selectedFolderId = null },
                label = { Text("顶层") },
            )
            activeFolders.forEach { folder ->
                FilterChip(
                    selected = selectedFolderId == folder.id,
                    onClick = { selectedFolderId = folder.id },
                    label = { Text(folder.name) },
                )
            }
        }
        Button(
            onClick = {
                runVault {
                    controller.createFolder(CreateVaultFolderCommand(folderName, selectedFolderId))
                    folderName = ""
                }
            },
            enabled = !state.busy && folderName.isNotBlank(),
        ) { Text("创建文件夹") }
        val selectedFolder = state.snapshot.folders.singleOrNull { folder ->
            VaultEntitySelection(VaultEntityKind.FOLDER, folder.id) in state.selection
        }
        Button(
            onClick = {
                selectedFolder?.let { folder ->
                    runVault { controller.renameFolder(RenameVaultFolderCommand(folder.id, folderName)) }
                }
            },
            enabled = !state.busy && selectedFolder != null && folderName.isNotBlank(),
        ) { Text("重命名所选文件夹") }
        HorizontalDivider()
        Text("显式保存", style = MaterialTheme.typography.titleMedium)
        Text("剪贴板监测和系统分享只填入候选；必须点击保存才进入 Vault。")
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            listOf(VaultContentType.TEXT, VaultContentType.URL).forEach { type ->
                FilterChip(
                    selected = state.draftContentType == type,
                    onClick = { controller.updateDraft(state.draftTitle, state.draftText, type) },
                    label = { Text(if (type == VaultContentType.TEXT) "文本" else "URL") },
                )
            }
        }
        OutlinedTextField(
            value = state.draftTitle,
            onValueChange = { controller.updateDraft(it, state.draftText, state.draftContentType) },
            label = { Text("标题") },
            modifier = Modifier.fillMaxWidth().testTag("vault_title"),
            singleLine = true,
        )
        OutlinedTextField(
            value = state.draftText,
            onValueChange = { controller.updateDraft(state.draftTitle, it, state.draftContentType) },
            label = { Text("正文或 HTTP(S) URL") },
            modifier = Modifier.fillMaxWidth().testTag("vault_body"),
            minLines = 4,
        )
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Button(
                onClick = {
                    selectedFolderId?.let { folder -> runVault { controller.saveDraft(folder) } }
                },
                enabled = !state.busy && selectedFolderId != null && state.draftText.isNotEmpty(),
            ) { Text("加密保存") }
            TextButton(
                onClick = {
                    selectedFolderId?.let { folder ->
                        onImportFile(folder, state.draftTitle.ifBlank { "导入文件" })
                    }
                },
                enabled = !state.busy && selectedFolderId != null,
            ) { Text("导入文件…") }
        }
        HorizontalDivider()
        Text("文件夹与条目", style = MaterialTheme.typography.titleMedium)
        state.snapshot.folders.forEach { folder ->
            Row(verticalAlignment = Alignment.CenterVertically) {
                val entity = VaultEntitySelection(VaultEntityKind.FOLDER, folder.id)
                Checkbox(
                    checked = entity in state.selection,
                    onCheckedChange = { controller.setSelected(entity, it) },
                )
                Text("文件夹 · ${folder.name} · ${folder.effectivePolicy.label()}")
            }
        }
        state.snapshot.items.forEach { item ->
            Row(verticalAlignment = Alignment.CenterVertically) {
                val entity = VaultEntitySelection(VaultEntityKind.ITEM, item.id)
                Checkbox(
                    checked = entity in state.selection,
                    onCheckedChange = { controller.setSelected(entity, it) },
                )
                Text("${item.contentType.label()} · ${item.title} · ${item.effectivePolicy.label()}")
                if (item.contentType != VaultContentType.FILE) {
                    TextButton(
                        onClick = {
                            controller.updateDraft(item.title, item.text.orEmpty(), item.contentType)
                        },
                    ) {
                        Text("载入编辑")
                    }
                }
            }
        }
        val editable = state.snapshot.items.singleOrNull { item ->
            VaultEntitySelection(VaultEntityKind.ITEM, item.id) in state.selection &&
                item.contentType != VaultContentType.FILE
        }
        val selectedFile = state.snapshot.items.singleOrNull { item ->
            VaultEntitySelection(VaultEntityKind.ITEM, item.id) in state.selection &&
                item.contentType == VaultContentType.FILE
        }
        Row(
            modifier = Modifier.horizontalScroll(rememberScrollState()),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Button(
                onClick = {
                    editable?.let { item ->
                        runVault {
                            controller.updateText(UpdateVaultTextCommand(item.id, state.draftTitle, state.draftText))
                        }
                    }
                },
                enabled = !state.busy && editable != null,
            ) { Text("更新所选") }
            Button(
                onClick = {
                    selectedFile?.let { item -> onExportFile(item.id, item.fileName ?: "导出文件") }
                },
                enabled = !state.busy && selectedFile != null,
            ) { Text("导出所选文件") }
            Button(
                onClick = { selectedFolderId?.let { runVault { controller.moveSelection(it) } } },
                enabled = !state.busy && state.selection.isNotEmpty() && selectedFolderId != null,
            ) { Text("移动") }
            Button(
                onClick = { runVault { controller.deleteSelection(includeFolderContents = true) } },
                enabled = !state.busy && state.selection.isNotEmpty(),
            ) { Text("回收") }
            Button(
                onClick = { runVault { controller.restoreSelection() } },
                enabled = !state.busy && state.section == VaultSection.TRASH && state.selection.isNotEmpty(),
            ) { Text("恢复") }
            Button(
                onClick = { runVault { controller.purgeSelection() } },
                enabled = !state.busy && state.section == VaultSection.TRASH && state.selection.isNotEmpty(),
            ) { Text("永久删除") }
        }
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Button(
                onClick = {
                    runVault { controller.setSelectionPolicy(VaultPolicyCommand(SyncPolicy.LOCAL_ONLY)) }
                },
                enabled = !state.busy && state.selection.isNotEmpty(),
            ) { Text("设为仅本机") }
            Button(
                onClick = {
                    runVault { controller.setSelectionPolicy(VaultPolicyCommand(SyncPolicy.ALL_PAIRED_DEVICES)) }
                },
                enabled = !state.busy && state.selection.isNotEmpty(),
            ) { Text("同步到全部已配对设备") }
        }
        state.status?.let { Text(it, modifier = Modifier.testTag("vault_status")) }
        state.snapshot.notice?.let { Text(it) }
    }
}

private fun VaultSection.label(): String = when (this) {
    VaultSection.INBOX -> "收件箱"
    VaultSection.ALL -> "全部"
    VaultSection.FOLDERS -> "文件夹"
    VaultSection.RECENT -> "最近"
    VaultSection.SYNCED -> "已同步"
    VaultSection.LOCAL_ONLY -> "仅本机"
    VaultSection.TRASH -> "回收站"
    VaultSection.DEVICES -> "设备"
}

private fun SyncPolicy.label(): String = when (this) {
    SyncPolicy.LOCAL_ONLY -> "仅本机"
    SyncPolicy.SELECTED_DEVICES -> "指定设备"
    SyncPolicy.ALL_PAIRED_DEVICES -> "全部设备"
}

private fun VaultContentType.label(): String = when (this) {
    VaultContentType.TEXT -> "文本"
    VaultContentType.URL -> "URL"
    VaultContentType.FILE -> "文件"
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
        Text("匿名服务器分享不使用 Vault 加密；内容会以明文交给所配置服务器，请勿发送敏感信息。")
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
        Text("W2/A2 只独立保存该偏好。尚无配对设备或同步通道，因此开关不会触发任何网络发送。")
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
    enabled: Boolean = true,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .selectable(
                selected = checked,
                onClick = { onCheckedChange(!checked) },
                role = Role.Switch,
                enabled = enabled,
            ),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(label, modifier = Modifier.weight(1f))
        Spacer(Modifier.width(12.dp))
        Switch(
            checked = checked,
            onCheckedChange = onCheckedChange,
            modifier = Modifier.testTag(testTag),
            enabled = enabled,
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
