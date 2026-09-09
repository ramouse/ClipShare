package com.clipshare.platform.android.vault

import androidx.room.Entity
import androidx.room.Index

@Entity(tableName = "vault_state", primaryKeys = ["singletonId"])
data class VaultStateEntity(
    val singletonId: Int = 1,
    val vaultId: String,
    val initializationId: String,
    val formatVersion: Int,
    val currentWriteEpoch: Long,
    val initializationState: String,
    val wrapperDigest: String,
)

@Entity(tableName = "folders", indices = [Index("parentId"), Index("currentVersionId", unique = true)])
data class FolderEntity(
    @androidx.room.PrimaryKey val folderId: String,
    val vaultId: String,
    val parentId: String?,
    val currentVersionId: String,
    val keyEpoch: Long,
    val tombstone: Boolean,
    val encryptedMetadata: String,
)

@Entity(tableName = "items", indices = [Index("folderId"), Index("currentVersionId", unique = true)])
data class ItemEntity(
    @androidx.room.PrimaryKey val itemId: String,
    val vaultId: String,
    val folderId: String,
    val contentType: String,
    val currentVersionId: String,
    val keyEpoch: Long,
    val tombstone: Boolean,
    val encryptedMetadata: String,
    val encryptedPayload: String?,
)

@Entity(tableName = "item_versions", indices = [Index("entityId"), Index("baseVersionId")])
data class ItemVersionEntity(
    @androidx.room.PrimaryKey val versionId: String,
    val entityId: String,
    val entityKind: String,
    val baseVersionId: String?,
    val keyEpoch: Long,
    val encryptedSnapshot: String,
)

@Entity(
    tableName = "encrypted_file_manifests",
    indices = [Index(value = ["fileId", "generationId"], unique = true), Index("itemId")],
)
data class FileManifestEntity(
    @androidx.room.PrimaryKey val manifestId: String,
    val fileId: String,
    val itemId: String,
    val generationId: String,
    val keyEpoch: Long,
    val chunkSize: Long,
    val chunkCount: Long,
    val encryptedManifest: String,
    val wrappedFileKey: String,
    val committed: Boolean,
)

@Entity(
    tableName = "encrypted_file_chunks",
    primaryKeys = ["fileId", "generationId", "chunkIndex"],
    indices = [Index(value = ["fileId", "generationId", "nonce"], unique = true)],
)
data class FileChunkEntity(
    val fileId: String,
    val generationId: String,
    val chunkIndex: Long,
    val nonce: String,
    val ciphertextBytes: Long,
    val ciphertextSha256: String,
    val relativePath: String,
)

@Entity(
    tableName = "sync_events",
    indices = [Index(value = ["sourceDeviceId", "sourceSequence"], unique = true), Index("entityId")],
)
data class SyncEventEntity(
    @androidx.room.PrimaryKey val eventId: String,
    val sourceDeviceId: String,
    val sourceSequence: Long,
    val logicalTime: Long,
    val entityId: String,
    val entityKind: String,
    val operation: String,
    val baseVersionId: String?,
    val newVersionId: String,
    val keyEpoch: Long,
    val encryptedBody: String,
    val bodyDigest: String,
    val disposition: String,
)

@Entity(tableName = "device_key_wrappers", primaryKeys = ["deviceId", "keyEpoch"])
data class DeviceKeyWrapperEntity(
    val deviceId: String,
    val keyEpoch: Long,
    val wrapper: String,
    val revoked: Boolean,
)

@Entity(tableName = "tombstones", indices = [Index("purgeAfterBucket")])
data class TombstoneEntity(
    @androidx.room.PrimaryKey val entityId: String,
    val entityKind: String,
    val versionId: String,
    val keyEpoch: Long,
    val encryptedDeletedAt: String,
    val purgeAfterBucket: Long,
)

@Entity(
    tableName = "nonce_state",
    primaryKeys = ["vaultId", "keyEpoch", "purpose", "originDeviceId"],
)
data class NonceStateEntity(
    val vaultId: String,
    val keyEpoch: Long,
    val purpose: String,
    val originDeviceId: String,
    val prefix: ByteArray,
    val lastCounter: Long,
) {
    override fun equals(other: Any?): Boolean =
        other is NonceStateEntity &&
            vaultId == other.vaultId &&
            keyEpoch == other.keyEpoch &&
            purpose == other.purpose &&
            originDeviceId == other.originDeviceId &&
            prefix.contentEquals(other.prefix) &&
            lastCounter == other.lastCounter

    override fun hashCode(): Int {
        var result = vaultId.hashCode()
        result = 31 * result + keyEpoch.hashCode()
        result = 31 * result + purpose.hashCode()
        result = 31 * result + originDeviceId.hashCode()
        result = 31 * result + prefix.contentHashCode()
        result = 31 * result + lastCounter.hashCode()
        return result
    }
}

@Entity(tableName = "migration_state")
data class MigrationStateEntity(
    @androidx.room.PrimaryKey val migrationId: String,
    val fromVersion: Int,
    val toVersion: Int,
    val state: String,
    val encryptedRollbackPath: String?,
)
