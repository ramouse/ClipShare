package com.clipshare.platform.android.vault

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import androidx.room.Transaction
import com.clipshare.core.crypto.NonceAllocation

@Dao
@Suppress("TooManyFunctions")
abstract class VaultRoomDao {
    @Query("SELECT * FROM vault_state WHERE singletonId = 1")
    abstract suspend fun vaultState(): VaultStateEntity?

    @Insert(onConflict = OnConflictStrategy.ABORT)
    abstract suspend fun insertVaultState(state: VaultStateEntity)

    @Query(
        "UPDATE vault_state SET initializationState = 'READY' " +
            "WHERE singletonId = 1 AND initializationState = 'STAGED'",
    )
    abstract suspend fun promoteInitializationReady(): Int

    @Query("SELECT COUNT(*) FROM folders")
    abstract suspend fun folderCount(): Long

    @Query("SELECT COUNT(*) FROM items")
    abstract suspend fun itemCount(): Long

    @Query(
        """
        SELECT
          (SELECT COUNT(*) FROM folders) +
          (SELECT COUNT(*) FROM items) +
          (SELECT COUNT(*) FROM item_versions) +
          (SELECT COUNT(*) FROM encrypted_file_manifests) +
          (SELECT COUNT(*) FROM sync_events) +
          (SELECT COUNT(*) FROM tombstones)
        """,
    )
    abstract suspend fun ciphertextCount(): Long

    @Insert(onConflict = OnConflictStrategy.IGNORE)
    protected abstract suspend fun insertNonceState(state: NonceStateEntity): Long

    @Query(
        """
        UPDATE nonce_state
        SET lastCounter = lastCounter + 1
        WHERE vaultId = :vaultId AND keyEpoch = :keyEpoch AND purpose = :purpose
          AND originDeviceId = :originDeviceId AND lastCounter < 9223372036854775807
        """,
    )
    protected abstract suspend fun incrementNonceCounter(
        vaultId: String,
        keyEpoch: Long,
        purpose: String,
        originDeviceId: String,
    ): Int

    @Query(
        """
        SELECT * FROM nonce_state
        WHERE vaultId = :vaultId AND keyEpoch = :keyEpoch AND purpose = :purpose
          AND originDeviceId = :originDeviceId
        """,
    )
    protected abstract suspend fun nonceState(
        vaultId: String,
        keyEpoch: Long,
        purpose: String,
        originDeviceId: String,
    ): NonceStateEntity?

    @Transaction
    open suspend fun allocateNonce(
        vaultId: String,
        keyEpoch: Long,
        purpose: String,
        originDeviceId: String,
        newPrefix: ByteArray,
    ): NonceAllocation {
        require(LOWERCASE_UUID.matches(vaultId) && LOWERCASE_UUID.matches(originDeviceId)) {
            "Nonce scopes require lowercase UUIDs."
        }
        require(keyEpoch > 0) { "Epoch numbers are positive." }
        require(purpose in RECORD_PURPOSES) { "Unknown or non-record nonce purpose." }
        require(newPrefix.size == NONCE_PREFIX_BYTES) { "Nonce prefixes are exactly four bytes." }
        insertNonceState(NonceStateEntity(vaultId, keyEpoch, purpose, originDeviceId, newPrefix, 0))
        check(incrementNonceCounter(vaultId, keyEpoch, purpose, originDeviceId) == 1) {
            "Nonce counter is exhausted or unavailable."
        }
        val current = checkNotNull(nonceState(vaultId, keyEpoch, purpose, originDeviceId))
        return NonceAllocation(current.prefix, current.lastCounter)
    }

    @Insert(onConflict = OnConflictStrategy.ABORT)
    protected abstract suspend fun insertFolder(folder: FolderEntity)

    @Insert(onConflict = OnConflictStrategy.ABORT)
    protected abstract suspend fun insertItem(item: ItemEntity)

    @Insert(onConflict = OnConflictStrategy.ABORT)
    protected abstract suspend fun insertVersion(version: ItemVersionEntity)

    @Insert(onConflict = OnConflictStrategy.ABORT)
    protected abstract suspend fun insertEvent(event: SyncEventEntity)

    @Query(
        """
        SELECT * FROM sync_events
        WHERE eventId = :eventId
           OR (sourceDeviceId = :sourceDeviceId AND sourceSequence = :sourceSequence)
        LIMIT 1
        """,
    )
    protected abstract suspend fun existingEvent(
        eventId: String,
        sourceDeviceId: String,
        sourceSequence: Long,
    ): SyncEventEntity?

    @Query("SELECT MAX(sourceSequence) FROM sync_events WHERE sourceDeviceId = :sourceDeviceId")
    protected abstract suspend fun highestSourceSequence(sourceDeviceId: String): Long?

    @Transaction
    open suspend fun populateDefaultFolders(defaults: List<VaultDefaultFolderSeed>) {
        require(defaults.size == DEFAULT_FOLDER_COUNT) { "Vault initialization requires exactly five default folders." }
        check(vaultState()?.initializationState == "STAGED") { "Only a STAGED Vault database can be populated." }
        check(ciphertextCount() == 0L) { "A staged Vault with existing ciphertext cannot be repopulated." }
        defaults.forEach { seed ->
            validateFolderTuple(seed.folder, seed.version, seed.event)
            insertFolder(seed.folder)
            insertVersion(seed.version)
            insertEvent(seed.event)
        }
    }

    @Transaction
    open suspend fun insertFolderVersionAndEvent(
        folder: FolderEntity,
        version: ItemVersionEntity,
        event: SyncEventEntity,
    ): Boolean {
        validateFolderTuple(folder, version, event)
        if (isDuplicateOrThrow(event)) return false
        insertFolder(folder)
        insertVersion(version)
        insertEvent(event)
        return true
    }

    @Transaction
    open suspend fun insertItemVersionAndEvent(
        item: ItemEntity,
        version: ItemVersionEntity,
        event: SyncEventEntity,
    ): Boolean {
        validateItemTuple(item, version, event)
        if (isDuplicateOrThrow(event)) return false
        insertItem(item)
        insertVersion(version)
        insertEvent(event)
        return true
    }

    private suspend fun isDuplicateOrThrow(event: SyncEventEntity): Boolean {
        val existing = existingEvent(event.eventId, event.sourceDeviceId, event.sourceSequence)
        if (existing == null) {
            val highest = highestSourceSequence(event.sourceDeviceId)
            check(highest == null || event.sourceSequence > highest) {
                "An older source sequence cannot be appended after a newer event."
            }
            return false
        }
        check(existing == event) {
            "Event ID or source sequence was replayed with different content."
        }
        return true
    }

    @Query("SELECT * FROM folders ORDER BY folderId")
    abstract suspend fun folders(): List<FolderEntity>

    @Query("SELECT * FROM items ORDER BY itemId")
    abstract suspend fun items(): List<ItemEntity>

    @Insert(onConflict = OnConflictStrategy.ABORT)
    abstract suspend fun insertFileManifest(manifest: FileManifestEntity)

    @Insert(onConflict = OnConflictStrategy.ABORT)
    abstract suspend fun insertFileChunks(chunks: List<FileChunkEntity>)

    @Query("UPDATE encrypted_file_manifests SET committed = 1 WHERE manifestId = :manifestId AND committed = 0")
    abstract suspend fun markManifestCommitted(manifestId: String): Int

    @Transaction
    open suspend fun commitFileGeneration(manifest: FileManifestEntity, chunks: List<FileChunkEntity>) {
        require(!manifest.committed) { "A file manifest must enter the transaction uncommitted." }
        require(
            listOf(manifest.manifestId, manifest.fileId, manifest.itemId, manifest.generationId)
                .all(LOWERCASE_UUID::matches),
        ) {
            "File generation identifiers must be lowercase UUIDs."
        }
        require(manifest.keyEpoch > 0) { "File manifest epochs are positive." }
        require(manifest.chunkSize in 1..MAXIMUM_FILE_CHUNK_BYTES) { "Invalid file chunk size." }
        require(manifest.encryptedManifest.isNotEmpty() && manifest.wrappedFileKey.isNotEmpty()) {
            "Encrypted file manifest fields cannot be empty."
        }
        require(manifest.chunkCount == chunks.size.toLong()) { "Manifest chunk count does not match chunk rows." }
        require(chunks.withIndex().all { (index, chunk) ->
            chunk.fileId == manifest.fileId &&
                chunk.generationId == manifest.generationId &&
                chunk.chunkIndex == index.toLong() &&
                FILE_NONCE.matches(chunk.nonce) &&
                chunk.ciphertextBytes in GCM_TAG_BYTES..MAXIMUM_CIPHERTEXT_CHUNK_BYTES &&
                LOWERCASE_SHA256.matches(chunk.ciphertextSha256) &&
                chunk.relativePath ==
                "${manifest.fileId}/${manifest.generationId}/" +
                "chunk-${index.toString().padStart(CHUNK_INDEX_WIDTH, '0')}.bin"
        }) {
            "File chunks must match the manifest, be complete, ordered, and structurally valid."
        }
        insertFileManifest(manifest)
        insertFileChunks(chunks)
        check(markManifestCommitted(manifest.manifestId) == 1) { "File manifest could not be committed." }
    }

    private fun validateFolderTuple(
        folder: FolderEntity,
        version: ItemVersionEntity,
        event: SyncEventEntity,
    ) {
        require(
            folder.currentVersionId == version.versionId &&
                folder.folderId == version.entityId &&
                version.entityKind == "FOLDER" &&
                event.entityId == folder.folderId &&
                event.entityKind == "FOLDER" &&
                event.newVersionId == version.versionId &&
                folder.keyEpoch == version.keyEpoch &&
                version.keyEpoch == event.keyEpoch,
        ) { "Folder, version, and event identities must form one atomic update." }
    }

    private fun validateItemTuple(
        item: ItemEntity,
        version: ItemVersionEntity,
        event: SyncEventEntity,
    ) {
        require(
            item.currentVersionId == version.versionId &&
                item.itemId == version.entityId &&
                version.entityKind == "ITEM" &&
                event.entityId == item.itemId &&
                event.entityKind == "ITEM" &&
                event.newVersionId == version.versionId &&
                item.keyEpoch == version.keyEpoch &&
                version.keyEpoch == event.keyEpoch,
        ) { "Item, version, and event identities must form one atomic update." }
    }
}

data class VaultDefaultFolderSeed(
    val folder: FolderEntity,
    val version: ItemVersionEntity,
    val event: SyncEventEntity,
)

private const val NONCE_PREFIX_BYTES = 4
private const val DEFAULT_FOLDER_COUNT = 5
private const val MAXIMUM_FILE_CHUNK_BYTES = 1_048_576L
private const val GCM_TAG_BYTES = 16L
private const val MAXIMUM_CIPHERTEXT_CHUNK_BYTES = MAXIMUM_FILE_CHUNK_BYTES + GCM_TAG_BYTES
private const val CHUNK_INDEX_WIDTH = 19
private val LOWERCASE_UUID = Regex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")
private val LOWERCASE_SHA256 = Regex("^[0-9a-f]{64}$")
private val FILE_NONCE = Regex("^[A-Za-z0-9_-]{16}$")
private val RECORD_PURPOSES = setOf(
    "folder-metadata",
    "item-metadata",
    "item-payload",
    "file-key-wrap",
    "file-manifest",
    "event-body",
)
