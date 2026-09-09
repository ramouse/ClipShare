package com.clipshare.core.vault

import java.time.Instant

private val UUID_PATTERN = Regex(
    "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
)

internal fun validateUuid(value: String): String {
    require(UUID_PATTERN.matches(value)) { "Vault identifiers must be lowercase RFC 4122 UUIDs." }
    return value
}

@JvmInline
value class VaultId private constructor(val value: String) {
    companion object {
        fun parse(value: String) = VaultId(validateUuid(value))
    }
}

@JvmInline
value class FolderId private constructor(val value: String) {
    companion object {
        fun parse(value: String) = FolderId(validateUuid(value))
    }
}

@JvmInline
value class ItemId private constructor(val value: String) {
    companion object {
        fun parse(value: String) = ItemId(validateUuid(value))
    }
}

@JvmInline
value class VersionId private constructor(val value: String) {
    companion object {
        fun parse(value: String) = VersionId(validateUuid(value))
    }
}

@JvmInline
value class EventId private constructor(val value: String) {
    companion object {
        fun parse(value: String) = EventId(validateUuid(value))
    }
}

@JvmInline
value class DeviceId private constructor(val value: String) {
    companion object {
        fun parse(value: String) = DeviceId(validateUuid(value))
    }
}

enum class VaultContentType {
    TEXT,
    URL,
    FILE,
}

enum class SyncPolicy {
    LOCAL_ONLY,
    SELECTED_DEVICES,
    ALL_PAIRED_DEVICES,
}

data class SyncPolicySetting(
    val override: SyncPolicy?,
    val selectedDevices: Set<DeviceId> = emptySet(),
) {
    init {
        require(override == SyncPolicy.SELECTED_DEVICES || selectedDevices.isEmpty()) {
            "Selected devices are only valid for SELECTED_DEVICES."
        }
        require(override != SyncPolicy.SELECTED_DEVICES || selectedDevices.isNotEmpty()) {
            "SELECTED_DEVICES requires at least one device."
        }
    }
}

data class VaultState(
    val id: VaultId,
    val formatVersion: Int,
    val currentWriteEpoch: Long,
    val initialized: Boolean,
) {
    init {
        require(formatVersion == 1) { "Unsupported Vault format version." }
        require(currentWriteEpoch > 0) { "The write epoch must be positive." }
    }
}

data class VaultFolder(
    val id: FolderId,
    val parentId: FolderId?,
    val name: String,
    val description: String?,
    val sortOrder: Long,
    val syncPolicy: SyncPolicySetting,
    val currentVersionId: VersionId,
    val deletedAt: Instant?,
)

data class VaultItem(
    val id: ItemId,
    val folderId: FolderId,
    val contentType: VaultContentType,
    val title: String,
    val payload: ByteArray,
    val syncPolicy: SyncPolicySetting,
    val currentVersionId: VersionId,
    val createdAt: Instant,
    val updatedAt: Instant,
    val deletedAt: Instant?,
) {
    override fun equals(other: Any?): Boolean =
        other is VaultItem &&
            id == other.id &&
            folderId == other.folderId &&
            contentType == other.contentType &&
            title == other.title &&
            payload.contentEquals(other.payload) &&
            syncPolicy == other.syncPolicy &&
            currentVersionId == other.currentVersionId &&
            createdAt == other.createdAt &&
            updatedAt == other.updatedAt &&
            deletedAt == other.deletedAt

    override fun hashCode(): Int {
        var result = id.hashCode()
        result = 31 * result + folderId.hashCode()
        result = 31 * result + contentType.hashCode()
        result = 31 * result + title.hashCode()
        result = 31 * result + payload.contentHashCode()
        result = 31 * result + syncPolicy.hashCode()
        result = 31 * result + currentVersionId.hashCode()
        result = 31 * result + createdAt.hashCode()
        result = 31 * result + updatedAt.hashCode()
        result = 31 * result + (deletedAt?.hashCode() ?: 0)
        return result
    }
}
