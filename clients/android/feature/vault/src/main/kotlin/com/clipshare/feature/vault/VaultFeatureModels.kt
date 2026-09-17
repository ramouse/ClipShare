package com.clipshare.feature.vault

import com.clipshare.core.vault.SyncPolicy
import com.clipshare.core.vault.SyncPolicySetting
import com.clipshare.core.vault.VaultContentType
import java.time.Instant

enum class VaultSection {
    INBOX,
    ALL,
    FOLDERS,
    RECENT,
    SYNCED,
    LOCAL_ONLY,
    TRASH,
    DEVICES,
}

enum class VaultEntityKind {
    FOLDER,
    ITEM,
}

data class VaultEntitySelection(val kind: VaultEntityKind, val id: String)

data class VaultFolderView(
    val id: String,
    val parentId: String?,
    val name: String,
    val description: String?,
    val sortOrder: Long,
    val policy: SyncPolicySetting,
    val effectivePolicy: SyncPolicy,
    val deleted: Boolean,
)

data class VaultItemView(
    val id: String,
    val folderId: String,
    val contentType: VaultContentType,
    val title: String,
    val text: String?,
    val fileName: String?,
    val sizeBytes: Long,
    val policy: SyncPolicySetting,
    val effectivePolicy: SyncPolicy,
    val createdAt: Instant,
    val updatedAt: Instant,
    val deleted: Boolean,
)

data class VaultDeviceView(val id: String, val displayName: String, val revoked: Boolean)

data class VaultSnapshot(
    val folders: List<VaultFolderView> = emptyList(),
    val items: List<VaultItemView> = emptyList(),
    val devices: List<VaultDeviceView> = emptyList(),
    val usedSlowSearch: Boolean = false,
    val notice: String? = null,
)

data class VaultFeatureState(
    val section: VaultSection = VaultSection.INBOX,
    val snapshot: VaultSnapshot = VaultSnapshot(),
    val selection: Set<VaultEntitySelection> = emptySet(),
    val busy: Boolean = false,
    val status: String? = null,
    val failure: Boolean = false,
    val draftTitle: String = "",
    val draftText: String = "",
    val draftContentType: VaultContentType = VaultContentType.TEXT,
    val sensitiveGeneration: Long = 0,
)

data class CreateVaultFolderCommand(val name: String, val parentId: String?, val description: String? = null)

data class RenameVaultFolderCommand(val folderId: String, val name: String, val description: String? = null)

data class SaveVaultTextCommand(
    val folderId: String,
    val title: String,
    val text: String,
    val contentType: VaultContentType,
)

data class ImportVaultFileCommand(val folderId: String, val title: String, val uri: String)

data class ExportVaultFileCommand(val itemId: String, val uri: String)

data class UpdateVaultTextCommand(val itemId: String, val title: String, val text: String)

data class VaultPolicyCommand(
    val policy: SyncPolicy,
    val selectedDeviceIds: Set<String> = emptySet(),
    val clearDescendantOverrides: Boolean = false,
)
