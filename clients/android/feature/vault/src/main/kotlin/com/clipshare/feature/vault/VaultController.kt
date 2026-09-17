package com.clipshare.feature.vault

import com.clipshare.core.vault.SyncPolicy
import com.clipshare.core.vault.VaultContentType
import java.net.URI
import java.nio.charset.StandardCharsets
import java.util.UUID
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Mutex

@Suppress("TooManyFunctions")
class VaultController(private val workspace: VaultWorkspace) : AutoCloseable {
    private val operationMutex = Mutex()
    private val selected = mutableSetOf<VaultEntitySelection>()
    private val mutableState = MutableStateFlow(VaultFeatureState())
    private var closed = false

    val state: StateFlow<VaultFeatureState> = mutableState.asStateFlow()

    suspend fun initialize() = execute {
        workspace.initialize()
        reload(mutableState.value.section, null)
        "加密内容库已就绪。"
    }

    suspend fun navigate(section: VaultSection, query: String? = null) = execute {
        selected.clear()
        reload(section, query?.trim()?.ifEmpty { null })
        mutableState.value.snapshot.notice
    }

    fun setSelected(entity: VaultEntitySelection, isSelected: Boolean) {
        checkOpen()
        validateUuid(entity.id)
        if (isSelected) selected += entity else selected -= entity
        mutableState.value = mutableState.value.copy(selection = selected.toSet())
    }

    fun updateDraft(title: String, text: String, contentType: VaultContentType) {
        checkOpen()
        mutableState.value = mutableState.value.copy(
            draftTitle = title.take(MAXIMUM_TITLE_CHARACTERS),
            draftText = text,
            draftContentType = contentType,
        )
    }

    fun acceptExternalText(text: String, source: String) {
        validateText(text, VaultContentType.TEXT)
        updateDraft(source, text, VaultContentType.TEXT)
    }

    suspend fun saveDraft(folderId: String) {
        val current = mutableState.value
        saveText(
            SaveVaultTextCommand(
                folderId,
                current.draftTitle.ifBlank { "未命名内容" },
                current.draftText,
                current.draftContentType,
            ),
        )
        mutableState.value = mutableState.value.copy(draftTitle = "", draftText = "")
    }

    suspend fun createFolder(command: CreateVaultFolderCommand) {
        validateName(command.name)
        mutate("文件夹已创建。") { workspace.createFolder(command.copy(name = command.name.trim())) }
    }

    suspend fun renameFolder(command: RenameVaultFolderCommand) {
        validateUuid(command.folderId)
        validateName(command.name)
        mutate("文件夹已重命名。") { workspace.renameFolder(command.copy(name = command.name.trim())) }
    }

    suspend fun saveText(command: SaveVaultTextCommand) {
        validateTitle(command.title)
        validateText(command.text, command.contentType)
        mutate("内容已加密保存。") { workspace.saveText(command.copy(title = command.title.trim())) }
    }

    suspend fun importFile(command: ImportVaultFileCommand) {
        validateTitle(command.title)
        require(command.uri.startsWith("content://")) { "文件必须来自系统 SAF content URI。" }
        mutate("文件已流式加密保存。") { workspace.importFile(command.copy(title = command.title.trim())) }
    }

    suspend fun exportFile(command: ExportVaultFileCommand) {
        validateUuid(command.itemId)
        require(command.uri.startsWith("content://")) { "导出位置必须来自系统 SAF content URI。" }
        execute {
            workspace.exportFile(command)
            "文件已解密导出到用户明确选择的位置。"
        }
    }

    suspend fun updateText(command: UpdateVaultTextCommand) {
        validateUuid(command.itemId)
        validateTitle(command.title)
        validateText(command.text, VaultContentType.TEXT)
        mutate("内容已更新。") { workspace.updateText(command.copy(title = command.title.trim())) }
    }

    suspend fun moveSelection(targetFolderId: String) {
        validateUuid(targetFolderId)
        val current = requireSelection()
        mutate("所选内容已移动。") { workspace.move(current, targetFolderId) }
    }

    suspend fun deleteSelection(includeFolderContents: Boolean) {
        val current = requireSelection()
        mutate("所选内容已移入回收站。") { workspace.moveToTrash(current, includeFolderContents) }
    }

    suspend fun restoreSelection() {
        val current = requireSelection()
        mutate("所选内容已恢复。") { workspace.restore(current) }
    }

    suspend fun purgeSelection() {
        val current = requireSelection()
        mutate("所选密文记录已永久删除；不承诺底层介质物理擦除。") { workspace.purge(current) }
    }

    suspend fun setSelectionPolicy(command: VaultPolicyCommand) {
        require(command.policy == SyncPolicy.SELECTED_DEVICES || command.selectedDeviceIds.isEmpty()) {
            "只有指定设备策略可以携带设备列表。"
        }
        require(command.policy != SyncPolicy.SELECTED_DEVICES || command.selectedDeviceIds.isNotEmpty()) {
            "指定设备策略至少需要一台设备。"
        }
        command.selectedDeviceIds.forEach(::validateUuid)
        val current = requireSelection()
        mutate("同步策略已更新；它控制分发，不是逐条目密码学 ACL。") {
            workspace.setPolicy(current, command)
        }
    }

    fun clearSensitiveState() {
        checkOpen()
        workspace.clearSensitiveState()
        selected.clear()
        mutableState.value = VaultFeatureState(
            status = "已清除内存中的 Vault 明文状态。",
            sensitiveGeneration = Math.addExact(mutableState.value.sensitiveGeneration, 1),
        )
    }

    override fun close() {
        if (closed) return
        closed = true
        workspace.clearSensitiveState()
        workspace.close()
    }

    private suspend fun mutate(success: String, action: suspend () -> Unit) = execute {
        action()
        selected.clear()
        reload(mutableState.value.section, null)
        success
    }

    @Suppress("TooGenericExceptionCaught")
    private suspend fun execute(action: suspend () -> String?) {
        checkOpen()
        check(operationMutex.tryLock()) { "已有 Vault 操作正在执行。请等待完成。" }
        mutableState.value = mutableState.value.copy(busy = true, status = null, failure = false)
        try {
            val status = action()
            mutableState.value = mutableState.value.copy(busy = false, status = status, failure = false)
        } catch (error: Exception) {
            mutableState.value = mutableState.value.copy(
                busy = false,
                status = "Vault 操作失败；现有密文未被替换。",
                failure = true,
            )
            throw error
        } finally {
            operationMutex.unlock()
        }
    }

    private suspend fun reload(section: VaultSection, query: String?) {
        mutableState.value = mutableState.value.copy(
            section = section,
            snapshot = workspace.load(section, query),
            selection = selected.toSet(),
        )
    }

    private fun requireSelection(): Set<VaultEntitySelection> {
        checkOpen()
        check(selected.isNotEmpty()) { "请至少选择一个条目或文件夹。" }
        return selected.toSet()
    }

    private fun checkOpen() = check(!closed) { "Vault controller is closed." }

    private companion object {
        const val MAXIMUM_TITLE_CHARACTERS = 512
        const val MAXIMUM_TEXT_BYTES = 256 * 1024

        fun validateName(value: String) {
            require(value.isNotBlank() && value.trim().length <= MAXIMUM_TITLE_CHARACTERS) {
                "文件夹名称必须为 1–512 个字符。"
            }
        }

        fun validateTitle(value: String) {
            require(value.isNotBlank() && value.trim().length <= MAXIMUM_TITLE_CHARACTERS) {
                "标题必须为 1–512 个字符。"
            }
        }

        fun validateText(value: String, contentType: VaultContentType) {
            require(contentType != VaultContentType.FILE) { "文件必须通过显式文件导入入口保存。" }
            require(value.toByteArray(StandardCharsets.UTF_8).size <= MAXIMUM_TEXT_BYTES) {
                "文本或 URL 不能超过 256 KiB UTF-8。"
            }
            if (contentType == VaultContentType.URL) {
                val uri = runCatching { URI(value) }.getOrNull()
                require(uri?.scheme == "http" || uri?.scheme == "https") { "URL 必须使用 HTTP(S)。" }
            }
        }

        fun validateUuid(value: String) {
            require(runCatching { UUID.fromString(value) }.getOrNull()?.toString() == value) {
                "Vault 标识必须是小写规范 UUID。"
            }
        }
    }
}
