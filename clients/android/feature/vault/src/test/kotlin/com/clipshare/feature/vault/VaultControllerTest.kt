package com.clipshare.feature.vault

import com.clipshare.core.vault.SyncPolicy
import com.clipshare.core.vault.SyncPolicySetting
import com.clipshare.core.vault.VaultContentType
import java.time.Instant
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class VaultControllerTest {
    @Test
    fun initializeNavigateAndSelect() = runTest {
        val workspace = FakeWorkspace()
        val controller = VaultController(workspace)
        controller.initialize()
        controller.navigate(VaultSection.ALL, "  hello  ")
        controller.setSelected(VaultEntitySelection(VaultEntityKind.ITEM, ITEM_ID), true)

        assertTrue(workspace.initialized)
        assertEquals("hello", workspace.lastQuery)
        assertEquals(VaultSection.ALL, controller.state.value.section)
        assertEquals(1, controller.state.value.selection.size)
        controller.close()
    }

    @Test
    fun validatesMutatesAndClearsSelection() = runTest {
        val workspace = FakeWorkspace()
        val controller = VaultController(workspace)
        controller.initialize()
        assertTrue(
            runCatching {
                controller.saveText(SaveVaultTextCommand(FOLDER_ID, "URL", "ftp://example.test", VaultContentType.URL))
            }.isFailure,
        )
        controller.saveText(SaveVaultTextCommand(FOLDER_ID, "标题", "正文", VaultContentType.TEXT))
        controller.setSelected(VaultEntitySelection(VaultEntityKind.ITEM, ITEM_ID), true)
        controller.moveSelection(FOLDER_ID)

        assertEquals(1, workspace.saveCalls)
        assertEquals(1, workspace.moveCalls)
        assertTrue(controller.state.value.selection.isEmpty())
        controller.close()
    }

    @Test
    fun policyAndFailClosedState() = runTest {
        val workspace = FakeWorkspace(failCreate = true)
        val controller = VaultController(workspace)
        controller.initialize()
        controller.setSelected(VaultEntitySelection(VaultEntityKind.ITEM, ITEM_ID), true)
        assertTrue(
            runCatching {
                controller.setSelectionPolicy(VaultPolicyCommand(SyncPolicy.SELECTED_DEVICES))
            }.isFailure,
        )
        controller.setSelectionPolicy(
            VaultPolicyCommand(SyncPolicy.SELECTED_DEVICES, setOf(DEVICE_ID)),
        )
        assertEquals(1, workspace.policyCalls)
        assertTrue(runCatching { controller.createFolder(CreateVaultFolderCommand("工作", null)) }.isFailure)
        assertTrue(controller.state.value.failure)

        controller.clearSensitiveState()
        assertTrue(workspace.cleared)
        assertFalse(controller.state.value.failure)
        controller.close()
    }

    @Test
    @Suppress("LongMethod")
    fun coversDraftCrudFileTrashValidationAndCloseBoundaries() = runTest {
        val workspace = FakeWorkspace()
        val controller = VaultController(workspace)
        controller.initialize()

        controller.acceptExternalText("候选正文", "剪贴板候选")
        assertEquals("候选正文", controller.state.value.draftText)
        controller.saveDraft(FOLDER_ID)
        controller.updateDraft("", "无标题正文", VaultContentType.TEXT)
        controller.saveDraft(FOLDER_ID)
        controller.createFolder(CreateVaultFolderCommand("子级", FOLDER_ID))
        controller.renameFolder(RenameVaultFolderCommand(FOLDER_ID, "收件箱新名"))
        controller.importFile(ImportVaultFileCommand(FOLDER_ID, "文件", "content://test/input"))
        controller.exportFile(ExportVaultFileCommand(ITEM_ID, "content://test/output"))
        controller.updateText(UpdateVaultTextCommand(ITEM_ID, "新标题", "新正文"))

        val selection = VaultEntitySelection(VaultEntityKind.ITEM, ITEM_ID)
        controller.setSelected(selection, true)
        controller.deleteSelection(false)
        controller.setSelected(selection, true)
        controller.restoreSelection()
        controller.setSelected(selection, true)
        controller.purgeSelection()

        assertEquals(1, workspace.createCalls)
        assertEquals(1, workspace.renameCalls)
        assertEquals(1, workspace.importCalls)
        assertEquals(1, workspace.exportCalls)
        assertEquals(1, workspace.updateCalls)
        assertEquals(1, workspace.deleteCalls)
        assertEquals(1, workspace.restoreCalls)
        assertEquals(1, workspace.purgeCalls)

        assertTrue(runCatching { controller.moveSelection(FOLDER_ID) }.isFailure)
        assertTrue(runCatching { controller.createFolder(CreateVaultFolderCommand(" ", null)) }.isFailure)
        assertTrue(runCatching { controller.renameFolder(RenameVaultFolderCommand(UPPERCASE_ID, "名称")) }.isFailure)
        assertTrue(
            runCatching {
                controller.saveText(SaveVaultTextCommand(FOLDER_ID, "文件", "x", VaultContentType.FILE))
            }.isFailure,
        )
        assertTrue(
            runCatching {
                controller.saveText(
                    SaveVaultTextCommand(FOLDER_ID, "大文本", "x".repeat(256 * 1024 + 1), VaultContentType.TEXT),
                )
            }.isFailure,
        )
        assertTrue(
            runCatching {
                controller.saveText(SaveVaultTextCommand(FOLDER_ID, "URL", "/relative", VaultContentType.URL))
            }.isFailure,
        )
        assertTrue(
            runCatching {
                controller.saveText(SaveVaultTextCommand(FOLDER_ID, " ", "正文", VaultContentType.TEXT))
            }.isFailure,
        )
        assertTrue(
            runCatching {
                controller.saveText(
                    SaveVaultTextCommand(FOLDER_ID, "x".repeat(513), "正文", VaultContentType.TEXT),
                )
            }.isFailure,
        )
        assertTrue(
            runCatching {
                controller.renameFolder(RenameVaultFolderCommand("not-a-uuid", "名称"))
            }.isFailure,
        )
        controller.saveText(
            SaveVaultTextCommand(FOLDER_ID, "URL", "https://example.test", VaultContentType.URL),
        )
        assertTrue(
            runCatching { controller.importFile(ImportVaultFileCommand(FOLDER_ID, "文件", "file:///tmp/a")) }
                .isFailure,
        )
        assertTrue(
            runCatching { controller.exportFile(ExportVaultFileCommand(ITEM_ID, "file:///tmp/b")) }.isFailure,
        )

        controller.setSelected(selection, true)
        controller.setSelected(selection, false)
        assertTrue(controller.state.value.selection.isEmpty())
        controller.setSelected(selection, true)
        assertTrue(
            runCatching {
                controller.setSelectionPolicy(VaultPolicyCommand(SyncPolicy.LOCAL_ONLY, setOf(DEVICE_ID)))
            }.isFailure,
        )
        controller.navigate(VaultSection.ALL, "   ")
        assertEquals(null, workspace.lastQuery)
        controller.navigate(VaultSection.ALL)

        controller.clearSensitiveState()
        assertTrue(controller.state.value.sensitiveGeneration > 0)
        controller.close()
        controller.close()
        assertTrue(runCatching { controller.updateDraft("x", "y", VaultContentType.TEXT) }.isFailure)
    }

    private class FakeWorkspace(private val failCreate: Boolean = false) : VaultWorkspace {
        var initialized = false
        var cleared = false
        var saveCalls = 0
        var moveCalls = 0
        var policyCalls = 0
        var createCalls = 0
        var renameCalls = 0
        var importCalls = 0
        var exportCalls = 0
        var updateCalls = 0
        var deleteCalls = 0
        var restoreCalls = 0
        var purgeCalls = 0
        var lastQuery: String? = null

        override suspend fun initialize() {
            initialized = true
        }

        override suspend fun load(section: VaultSection, query: String?): VaultSnapshot {
            lastQuery = query
            return VaultSnapshot(
                folders = listOf(
                    VaultFolderView(
                        FOLDER_ID,
                        null,
                        "收件箱",
                        null,
                        0,
                        SyncPolicySetting(null),
                        SyncPolicy.LOCAL_ONLY,
                        false,
                    ),
                ),
                items = listOf(
                    VaultItemView(
                        ITEM_ID,
                        FOLDER_ID,
                        VaultContentType.TEXT,
                        "标题",
                        "正文",
                        null,
                        6,
                        SyncPolicySetting(null),
                        SyncPolicy.LOCAL_ONLY,
                        Instant.EPOCH,
                        Instant.EPOCH,
                        false,
                    ),
                ),
                devices = listOf(VaultDeviceView(DEVICE_ID, "测试设备", false)),
            )
        }

        override suspend fun createFolder(command: CreateVaultFolderCommand) {
            check(!failCreate) { "fail closed" }
            createCalls += 1
        }

        override suspend fun renameFolder(command: RenameVaultFolderCommand) {
            renameCalls += 1
        }

        override suspend fun saveText(command: SaveVaultTextCommand) {
            saveCalls += 1
        }

        override suspend fun importFile(command: ImportVaultFileCommand) {
            importCalls += 1
        }

        override suspend fun exportFile(command: ExportVaultFileCommand) {
            exportCalls += 1
        }

        override suspend fun updateText(command: UpdateVaultTextCommand) {
            updateCalls += 1
        }

        override suspend fun move(selection: Set<VaultEntitySelection>, targetFolderId: String) {
            moveCalls += 1
        }

        override suspend fun moveToTrash(selection: Set<VaultEntitySelection>, includeFolderContents: Boolean) {
            deleteCalls += 1
        }

        override suspend fun restore(selection: Set<VaultEntitySelection>) {
            restoreCalls += 1
        }

        override suspend fun purge(selection: Set<VaultEntitySelection>) {
            purgeCalls += 1
        }

        override suspend fun setPolicy(selection: Set<VaultEntitySelection>, command: VaultPolicyCommand) {
            policyCalls += 1
        }

        override fun clearSensitiveState() {
            cleared = true
        }

        override fun close() = Unit
    }

    private companion object {
        const val FOLDER_ID = "10000000-0000-4000-8000-000000000001"
        const val ITEM_ID = "20000000-0000-4000-8000-000000000001"
        const val DEVICE_ID = "30000000-0000-4000-8000-000000000001"
        const val UPPERCASE_ID = "AAAAAAAA-BBBB-4CCC-8DDD-EEEEEEEEEEEE"
    }
}
