package com.clipshare.platform.android.vault

import android.content.Context
import android.net.Uri
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.clipshare.core.vault.SyncPolicy
import com.clipshare.core.vault.VaultContentType
import com.clipshare.feature.vault.SaveVaultTextCommand
import com.clipshare.feature.vault.ExportVaultFileCommand
import com.clipshare.feature.vault.ImportVaultFileCommand
import com.clipshare.feature.vault.VaultEntityKind
import com.clipshare.feature.vault.VaultEntitySelection
import com.clipshare.feature.vault.VaultPolicyCommand
import com.clipshare.feature.vault.VaultSection
import java.io.File
import java.security.KeyStore
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class AndroidVaultWorkspaceInstrumentedTest {
    private lateinit var context: Context
    private lateinit var keyAliasesBefore: Set<String>

    @Before
    fun setUp() {
        context = ApplicationProvider.getApplicationContext()
        context.deleteDatabase(DATABASE_NAME)
        File(context.noBackupFilesDir, VAULT_DIRECTORY).deleteRecursively()
        context.contentResolver.delete(documentUri("input"), null, null)
        context.contentResolver.delete(documentUri("output"), null, null)
        keyAliasesBefore = vaultKeyAliases()
    }

    @After
    fun tearDown() {
        context.deleteDatabase(DATABASE_NAME)
        File(context.noBackupFilesDir, VAULT_DIRECTORY).deleteRecursively()
        context.contentResolver.delete(documentUri("input"), null, null)
        context.contentResolver.delete(documentUri("output"), null, null)
        val keyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (vaultKeyAliases() - keyAliasesBefore).forEach(keyStore::deleteEntry)
    }

    @Test
    fun workspacePersistsEncryptedCrudPolicyTrashAndLifecycle() = runBlocking {
        val sentinel = "W2_ANDROID_SENTINEL_42a90f"
        val workspace = AndroidEncryptedVaultWorkspace(context)
        workspace.initialize()
        val initial = workspace.load(VaultSection.ALL, null)
        assertEquals(5, initial.folders.size)
        val inboxId = initial.folders.single { it.name == "收件箱" }.id

        workspace.saveText(SaveVaultTextCommand(inboxId, "敏感标题", sentinel, VaultContentType.TEXT))
        val item = workspace.load(VaultSection.INBOX, "SENTINEL").items.single()
        assertEquals(sentinel, item.text)
        workspace.importFile(ImportVaultFileCommand(inboxId, "文件", documentUri("input").toString()))
        val fileItem = workspace.load(VaultSection.ALL, null).items.single {
            it.contentType == VaultContentType.FILE
        }
        workspace.exportFile(ExportVaultFileCommand(fileItem.id, documentUri("output").toString()))
        val exported = context.contentResolver.openInputStream(documentUri("output")).use {
            requireNotNull(it).readBytes()
        }
        assertEquals("W2_ANDROID_FILE_SENTINEL_239ac4", exported.toString(Charsets.UTF_8))
        val selected = setOf(VaultEntitySelection(VaultEntityKind.ITEM, item.id))
        workspace.setPolicy(selected, VaultPolicyCommand(SyncPolicy.ALL_PAIRED_DEVICES))
        assertEquals(1, workspace.load(VaultSection.SYNCED, null).items.size)
        workspace.moveToTrash(selected, false)
        assertEquals(1, workspace.load(VaultSection.TRASH, null).items.size)
        workspace.restore(selected)
        assertTrue(workspace.load(VaultSection.TRASH, null).items.isEmpty())

        workspace.clearSensitiveState()
        assertTrue(runCatching { workspace.load(VaultSection.ALL, null) }.isFailure)
        workspace.initialize()
        assertEquals(
            sentinel,
            workspace.load(VaultSection.ALL, null).items.single { it.contentType == VaultContentType.TEXT }.text,
        )
        workspace.close()

        val artifacts = buildList {
            val database = context.getDatabasePath(DATABASE_NAME)
            add(database)
            add(File(database.path + "-wal"))
            add(File(database.path + "-shm"))
            addAll(File(context.noBackupFilesDir, VAULT_DIRECTORY).walkTopDown().filter(File::isFile))
        }.filter(File::exists)
        assertTrue(artifacts.isNotEmpty())
        artifacts.forEach { artifact ->
            assertFalse(artifact.readBytes().containsSubsequence(sentinel.toByteArray()))
        }

        val reopened = AndroidEncryptedVaultWorkspace(context)
        reopened.initialize()
        assertEquals(
            sentinel,
            reopened.load(VaultSection.ALL, null).items.single { it.contentType == VaultContentType.TEXT }.text,
        )
        reopened.moveToTrash(selected, false)
        reopened.purge(selected)
        assertTrue(reopened.load(VaultSection.TRASH, null).items.isEmpty())
        reopened.close()
    }

    private fun vaultKeyAliases(): Set<String> {
        val keyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        return keyStore.aliases().toList().filter { it.startsWith(KEY_ALIAS_PREFIX) }.toSet()
    }

    private fun documentUri(path: String): Uri = Uri.Builder()
        .scheme("content")
        .authority(DOCUMENT_AUTHORITY)
        .appendPath(path)
        .build()

    private fun ByteArray.containsSubsequence(needle: ByteArray): Boolean {
        if (needle.isEmpty() || size < needle.size) return false
        return (0..size - needle.size).any { start ->
            needle.indices.all { offset -> this[start + offset] == needle[offset] }
        }
    }

    private companion object {
        const val DATABASE_NAME = "clipshare-vault.db"
        const val VAULT_DIRECTORY = "vault-v1"
        const val KEY_ALIAS_PREFIX = "clipshare.vault.wrap.v1."
        const val DOCUMENT_AUTHORITY = "com.clipshare.platform.android.test.documents"
    }
}
