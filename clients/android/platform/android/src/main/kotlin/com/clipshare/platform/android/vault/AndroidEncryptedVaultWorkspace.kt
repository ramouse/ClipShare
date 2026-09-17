package com.clipshare.platform.android.vault

import android.content.Context
import android.net.Uri
import android.provider.OpenableColumns
import androidx.room.Room
import androidx.core.net.toUri
import com.clipshare.core.crypto.CryptoPurpose
import com.clipshare.core.crypto.EPOCH_SECRET_BYTES
import com.clipshare.core.crypto.FileEncryptionMaterial
import com.clipshare.core.crypto.SecureRandomSecretGenerator
import com.clipshare.core.crypto.VaultCipherEnvelope
import com.clipshare.core.crypto.VaultCryptoContext
import com.clipshare.core.crypto.VaultCryptography
import com.clipshare.core.vault.DatabaseObservation
import com.clipshare.core.vault.DefaultFolderInitializer
import com.clipshare.core.vault.DeviceId
import com.clipshare.core.vault.FolderId
import com.clipshare.core.vault.InitializationAction
import com.clipshare.core.vault.InitializationPhase
import com.clipshare.core.vault.SyncPolicy
import com.clipshare.core.vault.SyncPolicySetting
import com.clipshare.core.vault.VaultContentType
import com.clipshare.core.vault.VaultContractCodec
import com.clipshare.core.vault.VaultEnvelopeContract
import com.clipshare.core.vault.VaultFolder
import com.clipshare.core.vault.VaultId
import com.clipshare.core.vault.VaultInitializationStateMachine
import com.clipshare.core.vault.VaultTree
import com.clipshare.core.vault.VersionId
import com.clipshare.feature.vault.CreateVaultFolderCommand
import com.clipshare.feature.vault.ExportVaultFileCommand
import com.clipshare.feature.vault.ImportVaultFileCommand
import com.clipshare.feature.vault.RenameVaultFolderCommand
import com.clipshare.feature.vault.SaveVaultTextCommand
import com.clipshare.feature.vault.UpdateVaultTextCommand
import com.clipshare.feature.vault.VaultDeviceView
import com.clipshare.feature.vault.VaultEntityKind
import com.clipshare.feature.vault.VaultEntitySelection
import com.clipshare.feature.vault.VaultFolderView
import com.clipshare.feature.vault.VaultItemView
import com.clipshare.feature.vault.VaultPolicyCommand
import com.clipshare.feature.vault.VaultSection
import com.clipshare.feature.vault.VaultSnapshot
import com.clipshare.feature.vault.VaultWorkspace
import java.io.File
import java.io.FileOutputStream
import java.nio.charset.StandardCharsets
import java.nio.file.FileAlreadyExistsException
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.security.MessageDigest
import java.util.Base64
import java.util.UUID
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json

@Suppress(
    "LargeClass",
    "TooManyFunctions",
    "LongMethod",
    "TooGenericExceptionCaught",
    "MaxLineLength",
)
class AndroidEncryptedVaultWorkspace(
    private val context: Context,
    private val nowMillis: () -> Long = System::currentTimeMillis,
) : VaultWorkspace {
    private val appContext = context.applicationContext
    private val root = File(appContext.noBackupFilesDir, "vault-v1")
    private val databaseFile = appContext.getDatabasePath(DATABASE_NAME)
    private val databaseExistedAtOpen = databaseFile.exists()
    private val database = Room.databaseBuilder(appContext, VaultRoomDatabase::class.java, DATABASE_NAME)
        .setJournalMode(androidx.room.RoomDatabase.JournalMode.WRITE_AHEAD_LOGGING)
        .build()
    private val dao = database.vaultDao()
    private val stateStore = AndroidRoomVaultStateStore(dao, databaseExistedAtOpen)
    private val wrapperStore = AndroidPlatformWrapperStore(File(root, "wrappers"))
    private val protector = AndroidVaultKeyProtector()
    private val blobStore = AndroidEncryptedBlobStore(File(root, "blobs"))
    private val json = Json { encodeDefaults = true; ignoreUnknownKeys = false; explicitNulls = true }
    private val mutationMutex = Mutex()
    private val epochSecrets = mutableMapOf<Long, ByteArray>()
    private var vaultId: VaultId? = null
    private var deviceId: DeviceId? = null
    private var sourceSequence = 0L
    private var initialized = false
    private var closed = false

    override suspend fun initialize() = mutationMutex.withLock {
        check(!closed) { "Vault workspace is closed." }
        if (initialized) return@withLock
        try {
            deviceId = readOrCreateDeviceId()
            val observed = stateStore.observe()
            if (observed == null) initializeWithoutDatabase() else resumeDatabase(observed)
            sourceSequence = dao.highestSourceSequence(requireDeviceId().value)
            initialized = true
        } catch (error: Exception) {
            clearSecrets()
            throw error
        }
    }

    override suspend fun load(section: VaultSection, query: String?): VaultSnapshot {
        ensureInitialized()
        val folders = readFolders()
        val items = readItems()
        val policies = effectiveFolderPolicies(folders)
        if (section == VaultSection.DEVICES) {
            return VaultSnapshot(notice = "设备配对与真实同步属于 P1；当前不会发起网络连接。")
        }
        var visibleFolders = if (section == VaultSection.TRASH) {
            folders.filter { it.record.tombstone }
        } else {
            folders.filterNot { it.record.tombstone }
        }
        var visibleItems = if (section == VaultSection.TRASH) {
            items.filter { it.record.tombstone }
        } else {
            items.filterNot { it.record.tombstone }
        }
        when (section) {
            VaultSection.INBOX -> {
                val inbox = folders.singleOrNull { !it.record.tombstone && it.metadata.templateRole == "inbox" }
                    ?.record?.folderId
                visibleFolders = visibleFolders.filter { it.record.folderId == inbox }
                visibleItems = visibleItems.filter { it.record.folderId == inbox }
            }
            VaultSection.FOLDERS -> visibleItems = emptyList()
            VaultSection.RECENT -> {
                visibleFolders = emptyList()
                visibleItems = visibleItems.sortedByDescending { it.metadata.updatedAtMillis }.take(RECENT_LIMIT)
            }
            VaultSection.SYNCED -> {
                visibleFolders = visibleFolders.filter { effectivePolicy(it, policies) != SyncPolicy.LOCAL_ONLY }
                visibleItems = visibleItems.filter { effectivePolicy(it, policies) != SyncPolicy.LOCAL_ONLY }
            }
            VaultSection.LOCAL_ONLY -> {
                visibleFolders = visibleFolders.filter { effectivePolicy(it, policies) == SyncPolicy.LOCAL_ONLY }
                visibleItems = visibleItems.filter { effectivePolicy(it, policies) == SyncPolicy.LOCAL_ONLY }
            }
            else -> Unit
        }
        val search = applySearch(visibleFolders, visibleItems, query)
        visibleFolders = search.folders
        visibleItems = search.items
        return VaultSnapshot(
            folders = visibleFolders.sortedWith(compareBy<FolderState> { it.metadata.sortOrder }.thenBy { it.metadata.name })
                .map { folder ->
                    VaultFolderView(
                        folder.record.folderId,
                        folder.record.parentId,
                        folder.metadata.name,
                        folder.metadata.description,
                        folder.metadata.sortOrder,
                        policySetting(folder.metadata.policy, folder.metadata.selectedDevices),
                        effectivePolicy(folder, policies),
                        folder.record.tombstone,
                    )
                },
            items = visibleItems.sortedByDescending { it.metadata.updatedAtMillis }.map { item ->
                VaultItemView(
                    item.record.itemId,
                    item.record.folderId,
                    contentType(item.record.contentType),
                    item.metadata.title,
                    item.text,
                    item.metadata.fileName,
                    item.metadata.sizeBytes,
                    policySetting(item.metadata.policy, item.metadata.selectedDevices),
                    effectivePolicy(item, policies),
                    java.time.Instant.ofEpochMilli(item.metadata.createdAtMillis),
                    java.time.Instant.ofEpochMilli(item.metadata.updatedAtMillis),
                    item.record.tombstone,
                )
            },
            devices = emptyList<VaultDeviceView>(),
            usedSlowSearch = search.slow,
            notice = if (search.slow) "搜索结果完整；已超过内存快速索引预算，使用较慢扫描。" else null,
        )
    }

    override suspend fun createFolder(command: CreateVaultFolderCommand) = mutate {
        val folders = readFolders()
        val parent = command.parentId?.let(FolderId::parse)
        parent?.let { parentId ->
            check(folders.any { it.record.folderId == parentId.value && !it.record.tombstone }) {
                "目标父文件夹不存在或已删除。"
            }
        }
        val id = newFolderId()
        VaultTree(
            folders.map(::domainFolder) + VaultFolder(
                id,
                parent,
                command.name,
                command.description,
                folders.size.toLong(),
                SyncPolicySetting(null),
                newVersionId(),
                null,
            ),
        ).depth(id)
        val metadata = FolderMetadata(
            command.name,
            command.description,
            folders.size.toLong(),
            null,
            emptyList(),
            null,
            nowMillis(),
            null,
        )
        dao.applyMutations(listOf(folderMutation(id.value, parent?.value, null, metadata, "CREATE", false)))
    }

    override suspend fun renameFolder(command: RenameVaultFolderCommand) = mutate {
        val folder = readFolders().singleOrNull {
            it.record.folderId == command.folderId && !it.record.tombstone
        } ?: error("文件夹不存在或已删除。")
        dao.applyMutations(
            listOf(
                folderMutation(
                    folder.record.folderId,
                    folder.record.parentId,
                    folder.record.currentVersionId,
                    folder.metadata.copy(
                        name = command.name,
                        description = command.description,
                        updatedAtMillis = nowMillis(),
                    ),
                    "UPDATE",
                    false,
                ),
            ),
        )
    }

    override suspend fun saveText(command: SaveVaultTextCommand) = mutate {
        requireActiveFolder(command.folderId)
        val now = nowMillis()
        val metadata = ItemMetadata(
            command.title,
            null,
            command.text.toByteArray(StandardCharsets.UTF_8).size.toLong(),
            null,
            emptyList(),
            now,
            now,
            null,
        )
        dao.applyMutations(
            listOf(
                itemMutation(
                    newItemId().value,
                    command.folderId,
                    wireContentType(command.contentType),
                    null,
                    metadata,
                    command.text,
                    "CREATE",
                    false,
                ),
            ),
        )
    }

    @Suppress("LongMethod")
    override suspend fun importFile(command: ImportVaultFileCommand) = mutate {
        requireActiveFolder(command.folderId)
        val uri = command.uri.toUri()
        require(uri.scheme == "content") { "Vault file imports require a content URI." }
        val descriptor = queryFile(uri)
        val itemId = newItemId()
        val fileId = newUuid()
        val generationId = newUuid()
        val manifestId = newUuid()
        val epoch = currentEpoch()
        val stored = mutableListOf<AndroidStoredEncryptedChunk>()
        val chunks = mutableListOf<FileChunkEntity>()
        FileEncryptionMaterial.create().use { material ->
            val fileDek = material.copyFileDek()
            try {
                var total = 0L
                appContext.contentResolver.openInputStream(uri).use { input ->
                    requireNotNull(input) { "Selected document cannot be opened." }
                    val buffer = ByteArray(FileEncryptionMaterial.MAXIMUM_PLAINTEXT_CHUNK_BYTES)
                    try {
                        var index = 0L
                        while (true) {
                            val count = readChunk(input, buffer)
                            if (count == 0) break
                            total = Math.addExact(total, count.toLong())
                            val plaintext = buffer.copyOf(count)
                            val envelope = try {
                                VaultCryptography.encryptFileChunk(
                                    fileDek,
                                    VaultCryptoContext(
                                        CryptoPurpose.FILE_CHUNK,
                                        requireVaultId(),
                                        requireDeviceId(),
                                        fileId,
                                        "chunk",
                                        epoch,
                                    ),
                                    material.allocationFor(index),
                                    plaintext,
                                )
                            } finally {
                                plaintext.fill(0)
                            }
                            val ciphertext = decodeB64(envelope.cipherAndTag)
                            try {
                                val saved = blobStore.writeChunkAtomically(fileId, generationId, index, ciphertext)
                                stored += saved
                                chunks += FileChunkEntity(
                                    fileId,
                                    generationId,
                                    index,
                                    envelope.nonce,
                                    saved.ciphertextBytes,
                                    saved.ciphertextSha256,
                                    saved.relativePath,
                                )
                            } finally {
                                ciphertext.fill(0)
                                buffer.fill(0, 0, count)
                            }
                            index = Math.addExact(index, 1)
                        }
                    } finally {
                        buffer.fill(0)
                    }
                }
                descriptor.size?.let { check(it == total) { "Selected document length changed during import." } }
                val wrappedKey = encryptBytes(CryptoPurpose.FILE_KEY_WRAP, fileId, "file-key", fileDek.copyOf(), epoch)
                val encryptedManifest = encryptJson(
                    CryptoPurpose.FILE_MANIFEST,
                    fileId,
                    "manifest",
                    FileManifestPayload(
                        fileId,
                        generationId,
                        requireDeviceId().value,
                        descriptor.name,
                        descriptor.contentType,
                        total,
                        FileEncryptionMaterial.MAXIMUM_PLAINTEXT_CHUNK_BYTES,
                        chunks.size,
                    ),
                    epoch,
                )
                val now = nowMillis()
                val metadata = ItemMetadata(
                    command.title,
                    descriptor.name,
                    total,
                    null,
                    emptyList(),
                    now,
                    now,
                    null,
                )
                val mutation = itemMutation(
                    itemId.value,
                    command.folderId,
                    "FILE",
                    null,
                    metadata,
                    null,
                    "CREATE",
                    false,
                )
                dao.applyFileMutation(
                    mutation,
                    FileManifestEntity(
                        manifestId,
                        fileId,
                        itemId.value,
                        generationId,
                        epoch,
                        FileEncryptionMaterial.MAXIMUM_PLAINTEXT_CHUNK_BYTES.toLong(),
                        chunks.size.toLong(),
                        encryptedManifest,
                        wrappedKey,
                        false,
                    ),
                    chunks,
                )
            } catch (error: Exception) {
                blobStore.deleteCommittedChunks(stored.map { it.relativePath })
                throw error
            } finally {
                fileDek.fill(0)
            }
        }
    }

    @Suppress("LongMethod")
    override suspend fun exportFile(command: ExportVaultFileCommand) {
        ensureInitialized()
        val item = requireItem(command.itemId, false)
        check(item.record.contentType == "FILE") { "只有文件条目可以导出。" }
        val records = dao.committedFileManifests(command.itemId)
        check(records.size == 1) { "A Vault file must have exactly one committed generation." }
        val record = records.single()
        val manifest = decryptJson(record.encryptedManifest) { json.decodeFromString<FileManifestPayload>(it) }
        check(
            manifest.fileId == record.fileId &&
                manifest.generationId == record.generationId &&
                manifest.sizeBytes == item.metadata.sizeBytes &&
                manifest.chunkCount.toLong() == record.chunkCount,
        ) { "Encrypted file manifest does not match its item record." }
        val fileDek = decryptBytes(record.wrappedFileKey)
        val destination = command.uri.toUri()
        try {
            val chunks = dao.fileChunks(record.fileId, record.generationId)
            check(chunks.size.toLong() == record.chunkCount) { "Encrypted file chunks are incomplete." }
            var total = 0L
            appContext.contentResolver.openOutputStream(destination, "wt").use { output ->
                requireNotNull(output) { "Selected export destination cannot be opened." }
                chunks.forEach { chunk ->
                    val ciphertext = blobStore.readChunk(
                        AndroidStoredEncryptedChunk(
                            chunk.fileId,
                            chunk.generationId,
                            chunk.chunkIndex,
                            chunk.ciphertextBytes,
                            chunk.ciphertextSha256,
                            chunk.relativePath,
                        ),
                    )
                    val plaintext = try {
                        VaultCryptography.decryptFileChunk(
                            fileDek,
                            VaultCipherEnvelope(
                                CryptoPurpose.FILE_CHUNK.wireValue,
                                requireVaultId().value,
                                manifest.originDeviceId,
                                record.fileId,
                                "chunk",
                                record.keyEpoch,
                                chunk.chunkIndex,
                                chunk.nonce,
                                chunk.ciphertextBytes - GCM_TAG_BYTES,
                                b64(ciphertext),
                            ),
                        )
                    } finally {
                        ciphertext.fill(0)
                    }
                    try {
                        output.write(plaintext)
                        total = Math.addExact(total, plaintext.size.toLong())
                    } finally {
                        plaintext.fill(0)
                    }
                }
                output.flush()
            }
            check(total == manifest.sizeBytes) { "Decrypted file length does not match its manifest." }
        } catch (error: Exception) {
            appContext.contentResolver.delete(destination, null, null)
            throw error
        } finally {
            fileDek.fill(0)
        }
    }

    override suspend fun updateText(command: UpdateVaultTextCommand) = mutate {
        val item = requireItem(command.itemId, false)
        check(item.record.contentType != "FILE") { "文件内容必须通过新 generation 替换。" }
        val metadata = item.metadata.copy(
            title = command.title,
            sizeBytes = command.text.toByteArray(StandardCharsets.UTF_8).size.toLong(),
            updatedAtMillis = nowMillis(),
        )
        dao.applyMutations(
            listOf(
                itemMutation(
                    item.record.itemId,
                    item.record.folderId,
                    item.record.contentType,
                    item.record.currentVersionId,
                    metadata,
                    command.text,
                    "UPDATE",
                    false,
                ),
            ),
        )
    }

    override suspend fun move(selection: Set<VaultEntitySelection>, targetFolderId: String) = mutate {
        val folders = readFolders()
        val items = readItems()
        check(folders.any { it.record.folderId == targetFolderId && !it.record.tombstone }) {
            "目标文件夹不存在或已删除。"
        }
        val moving = selection.filter { it.kind == VaultEntityKind.FOLDER }.map { FolderId.parse(it.id) }.toSet()
        if (moving.isNotEmpty()) {
            VaultTree(folders.filterNot { it.record.tombstone }.map(::domainFolder))
                .validateMove(moving, FolderId.parse(targetFolderId))
        }
        val mutations = selection.map { entity ->
            if (entity.kind == VaultEntityKind.FOLDER) {
                val folder = folders.single { it.record.folderId == entity.id && !it.record.tombstone }
                folderMutation(
                    entity.id,
                    targetFolderId,
                    folder.record.currentVersionId,
                    folder.metadata.copy(updatedAtMillis = nowMillis()),
                    "MOVE",
                    false,
                )
            } else {
                val item = items.single { it.record.itemId == entity.id && !it.record.tombstone }
                itemMutation(
                    entity.id,
                    targetFolderId,
                    item.record.contentType,
                    item.record.currentVersionId,
                    item.metadata.copy(updatedAtMillis = nowMillis()),
                    item.text,
                    "MOVE",
                    false,
                )
            }
        }
        dao.applyMutations(mutations)
    }

    override suspend fun moveToTrash(selection: Set<VaultEntitySelection>, includeFolderContents: Boolean) = mutate {
        val folders = readFolders()
        val items = readItems()
        val expanded = expandSelection(selection, folders, items, includeFolderContents)
        val deletedAt = nowMillis()
        dao.applyMutations(
            expanded.map { entity ->
                if (entity.kind == VaultEntityKind.FOLDER) {
                    val folder = folders.single { it.record.folderId == entity.id }
                    folderMutation(
                        entity.id,
                        folder.record.parentId,
                        folder.record.currentVersionId,
                        folder.metadata.copy(updatedAtMillis = deletedAt, deletedAtMillis = deletedAt),
                        "DELETE",
                        true,
                    )
                } else {
                    val item = items.single { it.record.itemId == entity.id }
                    itemMutation(
                        entity.id,
                        item.record.folderId,
                        item.record.contentType,
                        item.record.currentVersionId,
                        item.metadata.copy(updatedAtMillis = deletedAt, deletedAtMillis = deletedAt),
                        item.text,
                        "DELETE",
                        true,
                    )
                }
            },
        )
    }

    override suspend fun restore(selection: Set<VaultEntitySelection>) = mutate {
        val folders = readFolders()
        val items = readItems()
        val fallback = folders.filterNot { it.record.tombstone }.minByOrNull { it.metadata.sortOrder }
            ?.record?.folderId ?: error("没有可用于恢复的目标文件夹。")
        dao.applyMutations(
            selection.map { entity ->
                if (entity.kind == VaultEntityKind.FOLDER) {
                    val folder = folders.single { it.record.folderId == entity.id && it.record.tombstone }
                    val parent = folder.record.parentId?.takeIf { parentId ->
                        folders.any { it.record.folderId == parentId && !it.record.tombstone }
                    }
                    folderMutation(
                        entity.id,
                        parent,
                        folder.record.currentVersionId,
                        folder.metadata.copy(updatedAtMillis = nowMillis(), deletedAtMillis = null),
                        "RESTORE",
                        false,
                        true,
                    )
                } else {
                    val item = items.single { it.record.itemId == entity.id && it.record.tombstone }
                    val folder = item.record.folderId.takeIf { folderId ->
                        folders.any { it.record.folderId == folderId && !it.record.tombstone }
                    } ?: fallback
                    itemMutation(
                        entity.id,
                        folder,
                        item.record.contentType,
                        item.record.currentVersionId,
                        item.metadata.copy(updatedAtMillis = nowMillis(), deletedAtMillis = null),
                        item.text,
                        "RESTORE",
                        false,
                        true,
                    )
                }
            },
        )
    }

    override suspend fun purge(selection: Set<VaultEntitySelection>) = mutate {
        val folders = readFolders()
        val items = readItems()
        selection.forEach { entity ->
            val deleted = if (entity.kind == VaultEntityKind.FOLDER) {
                folders.any { it.record.folderId == entity.id && it.record.tombstone }
            } else {
                items.any { it.record.itemId == entity.id && it.record.tombstone }
            }
            check(deleted) { "只有回收站中的内容可以永久删除。" }
        }
        val paths = dao.purgeEntities(selection.map { entity ->
            (if (entity.kind == VaultEntityKind.FOLDER) "FOLDER" else "ITEM") to entity.id
        })
        blobStore.deleteCommittedChunks(paths)
    }

    override suspend fun setPolicy(selection: Set<VaultEntitySelection>, command: VaultPolicyCommand) = mutate {
        val folders = readFolders()
        val items = readItems()
        val expanded = if (command.clearDescendantOverrides) {
            expandSelection(selection, folders, items, true)
        } else {
            selection
        }
        val selectedDevices = command.selectedDeviceIds.sorted()
        dao.applyMutations(
            expanded.map { entity ->
                val inherited = command.clearDescendantOverrides && entity !in selection
                val policy = if (inherited) null else wirePolicy(command.policy)
                val devices = if (inherited) emptyList() else selectedDevices
                if (entity.kind == VaultEntityKind.FOLDER) {
                    val folder = folders.single { it.record.folderId == entity.id && !it.record.tombstone }
                    folderMutation(
                        entity.id,
                        folder.record.parentId,
                        folder.record.currentVersionId,
                        folder.metadata.copy(policy = policy, selectedDevices = devices, updatedAtMillis = nowMillis()),
                        "UPDATE",
                        false,
                    )
                } else {
                    val item = items.single { it.record.itemId == entity.id && !it.record.tombstone }
                    itemMutation(
                        entity.id,
                        item.record.folderId,
                        item.record.contentType,
                        item.record.currentVersionId,
                        item.metadata.copy(policy = policy, selectedDevices = devices, updatedAtMillis = nowMillis()),
                        item.text,
                        "UPDATE",
                        false,
                    )
                }
            },
        )
    }

    override fun clearSensitiveState() {
        if (closed) return
        clearSecrets()
        initialized = false
    }

    override fun close() {
        if (closed) return
        closed = true
        clearSecrets()
        database.close()
    }

    private suspend fun initializeWithoutDatabase() {
        val existing = wrapperStore.readSingle()
        check(existing == null || existing.state == InitializationPhase.STAGED.name) {
            "A READY wrapper without its Vault database cannot be recovered automatically."
        }
        val wrapper: AndroidPlatformWrapper
        val secret: ByteArray
        if (existing == null) {
            val createdVaultId = newVaultId()
            val initializationId = newUuid()
            val alias = "clipshare.vault.wrap.v1.${createdVaultId.value.replace("-", "")}"
            protector.createWrappingKey(alias)
            secret = SecureRandomSecretGenerator.generate(EPOCH_SECRET_BYTES)
            val protected = protector.protect(alias, secret, wrapperAad(createdVaultId, initializationId, INITIAL_EPOCH), emptySet())
            wrapper = AndroidPlatformWrapper(
                vaultId = createdVaultId.value,
                initializationId = initializationId,
                keyEpoch = INITIAL_EPOCH,
                state = InitializationPhase.STAGED.name,
                keyReference = protected.keyAlias,
                nonce = protected.nonce,
                protectedSecret = protected.cipherAndTag,
            )
            wrapperStore.writeAtomically(wrapper)
        } else {
            wrapper = existing
            secret = unprotect(wrapper)
        }
        vaultId = VaultId.parse(wrapper.vaultId)
        addSecret(wrapper.keyEpoch, secret)
        stateStore.createEmptyStaged(
            VaultStateEntity(
                vaultId = wrapper.vaultId,
                initializationId = wrapper.initializationId,
                formatVersion = 1,
                currentWriteEpoch = wrapper.keyEpoch,
                initializationState = InitializationPhase.STAGED.name,
                wrapperDigest = wrapperStore.digest(wrapper),
            ),
        )
        populateDefaults()
        stateStore.promoteReady()
        wrapperStore.writeAtomically(wrapper.copy(state = InitializationPhase.READY.name))
    }

    private suspend fun resumeDatabase(observation: DatabaseObservation) {
        vaultId = observation.vaultId
        val wrapper = requireNotNull(wrapperStore.read(observation.vaultId.value, observation.keyEpoch)) {
            "Vault database exists without its platform wrapper."
        }
        val wrapperObservation = wrapperStore.observe(observation.vaultId, observation.keyEpoch)
        val action = VaultInitializationStateMachine.decide(wrapperObservation, observation)
        if (action is InitializationAction.FailClosed) error(action.reason)
        addSecret(wrapper.keyEpoch, unprotect(wrapper))
        when (action) {
            InitializationAction.PopulateStagedDatabase -> {
                populateDefaults()
                stateStore.promoteReady()
                wrapperStore.writeAtomically(wrapper.copy(state = InitializationPhase.READY.name))
            }
            InitializationAction.ResumeStagedDatabase,
            InitializationAction.PromoteDatabaseReady,
            -> {
                stateStore.promoteReady()
                wrapperStore.writeAtomically(wrapper.copy(state = InitializationPhase.READY.name))
            }
            InitializationAction.PromoteWrapperReady ->
                wrapperStore.writeAtomically(wrapper.copy(state = InitializationPhase.READY.name))
            InitializationAction.OpenReady -> Unit
            else -> error("Unsupported Vault recovery state.")
        }
    }

    private suspend fun populateDefaults() {
        sourceSequence = 0
        val roles = listOf("inbox", "password", "work", "private", "other")
        val seeds = DefaultFolderInitializer.names.mapIndexed { index, name ->
            val mutation = folderMutation(
                newFolderId().value,
                null,
                null,
                FolderMetadata(name, null, index.toLong(), null, emptyList(), roles[index], nowMillis(), null),
                "CREATE",
                false,
            )
            VaultDefaultFolderSeed(checkNotNull(mutation.folder), mutation.version, mutation.event)
        }
        stateStore.populateDefaultFolders(seeds)
    }

    private suspend fun folderMutation(
        folderId: String,
        parentId: String?,
        baseVersionId: String?,
        metadata: FolderMetadata,
        operation: String,
        tombstone: Boolean,
        removeTombstone: Boolean = false,
    ): AndroidEncryptedVaultMutation {
        val versionId = newVersionId().value
        val epoch = currentEpoch()
        val encryptedMetadata = encryptJson(CryptoPurpose.FOLDER_METADATA, folderId, "metadata", metadata, epoch)
        val snapshot = encryptJson(CryptoPurpose.FOLDER_METADATA, folderId, "snapshot", metadata, epoch)
        val eventBody = encryptJson(
            CryptoPurpose.EVENT_BODY,
            folderId,
            "event",
            EventPayload(operation, "FOLDER", folderId, baseVersionId, versionId),
            epoch,
        )
        return AndroidEncryptedVaultMutation(
            FolderEntity(folderId, requireVaultId().value, parentId, versionId, epoch, tombstone, encryptedMetadata),
            null,
            ItemVersionEntity(versionId, folderId, "FOLDER", baseVersionId, epoch, snapshot),
            newEvent(folderId, "FOLDER", operation, baseVersionId, versionId, epoch, eventBody),
            if (tombstone) tombstone(folderId, "FOLDER", versionId, epoch, checkNotNull(metadata.deletedAtMillis)) else null,
            removeTombstone,
        )
    }

    private suspend fun itemMutation(
        itemId: String,
        folderId: String,
        contentType: String,
        baseVersionId: String?,
        metadata: ItemMetadata,
        text: String?,
        operation: String,
        tombstone: Boolean,
        removeTombstone: Boolean = false,
    ): AndroidEncryptedVaultMutation {
        val versionId = newVersionId().value
        val epoch = currentEpoch()
        val encryptedMetadata = encryptJson(CryptoPurpose.ITEM_METADATA, itemId, "metadata", metadata, epoch)
        val encryptedPayload = text?.let {
            encryptBytes(CryptoPurpose.ITEM_PAYLOAD, itemId, "payload", it.toByteArray(StandardCharsets.UTF_8), epoch)
        }
        val snapshot = encryptJson(CryptoPurpose.ITEM_METADATA, itemId, "snapshot", metadata, epoch)
        val eventBody = encryptJson(
            CryptoPurpose.EVENT_BODY,
            itemId,
            "event",
            EventPayload(operation, "ITEM", itemId, baseVersionId, versionId),
            epoch,
        )
        return AndroidEncryptedVaultMutation(
            null,
            ItemEntity(
                itemId,
                requireVaultId().value,
                folderId,
                contentType,
                versionId,
                epoch,
                tombstone,
                encryptedMetadata,
                encryptedPayload,
            ),
            ItemVersionEntity(versionId, itemId, "ITEM", baseVersionId, epoch, snapshot),
            newEvent(itemId, "ITEM", operation, baseVersionId, versionId, epoch, eventBody),
            if (tombstone) tombstone(itemId, "ITEM", versionId, epoch, checkNotNull(metadata.deletedAtMillis)) else null,
            removeTombstone,
        )
    }

    private suspend fun tombstone(
        entityId: String,
        kind: String,
        versionId: String,
        epoch: Long,
        deletedAtMillis: Long,
    ): TombstoneEntity {
        val encrypted = encryptJson(
            CryptoPurpose.EVENT_BODY,
            entityId,
            "deleted-at",
            DeletedAtPayload(deletedAtMillis),
            epoch,
        )
        val purgeBucket = (deletedAtMillis + TRASH_RETENTION_MILLIS) / DAY_MILLIS
        return TombstoneEntity(entityId, kind, versionId, epoch, encrypted, purgeBucket)
    }

    private fun newEvent(
        entityId: String,
        kind: String,
        operation: String,
        baseVersionId: String?,
        versionId: String,
        epoch: Long,
        body: String,
    ): SyncEventEntity {
        sourceSequence = Math.addExact(sourceSequence, 1)
        return SyncEventEntity(
            newUuid(),
            requireDeviceId().value,
            sourceSequence,
            nowMillis(),
            entityId,
            kind,
            operation,
            baseVersionId,
            versionId,
            epoch,
            body,
            sha256(body.toByteArray(StandardCharsets.UTF_8)),
            "APPLIED",
        )
    }

    private suspend inline fun <reified T> encryptJson(
        purpose: CryptoPurpose,
        entityId: String,
        field: String,
        value: T,
        epoch: Long,
    ): String = encryptBytes(purpose, entityId, field, json.encodeToString(value).toByteArray(), epoch)

    private suspend fun encryptBytes(
        purpose: CryptoPurpose,
        entityId: String,
        field: String,
        value: ByteArray,
        epoch: Long,
    ): String = try {
        val allocation = AndroidEncryptedVaultStore(dao, requireDeviceId())
            .reserveRecordNonce(requireVaultId(), epoch, purpose)
        serializeEnvelope(
            VaultCryptography.encryptRecord(
                requireSecret(epoch),
                VaultCryptoContext(purpose, requireVaultId(), requireDeviceId(), entityId, field, epoch),
                allocation,
                value,
            ),
        )
    } finally {
        value.fill(0)
    }

    private fun <T> decryptJson(value: String, deserializer: (String) -> T): T {
        val envelope = parseEnvelope(value)
        val plaintext = VaultCryptography.decryptRecord(requireSecret(envelope.keyEpoch), envelope)
        return try {
            deserializer(plaintext.toString(StandardCharsets.UTF_8))
        } finally {
            plaintext.fill(0)
        }
    }

    private fun decryptText(value: String): String {
        val envelope = parseEnvelope(value)
        val plaintext = VaultCryptography.decryptRecord(requireSecret(envelope.keyEpoch), envelope)
        return try {
            plaintext.toString(StandardCharsets.UTF_8)
        } finally {
            plaintext.fill(0)
        }
    }

    private fun decryptBytes(value: String): ByteArray {
        val envelope = parseEnvelope(value)
        return VaultCryptography.decryptRecord(requireSecret(envelope.keyEpoch), envelope)
    }

    private suspend fun readFolders(): List<FolderState> = dao.folders().map { record ->
        FolderState(record, decryptJson(record.encryptedMetadata) { json.decodeFromString<FolderMetadata>(it) })
    }

    private suspend fun readItems(): List<ItemState> = dao.items().map { record ->
        ItemState(
            record,
            decryptJson(record.encryptedMetadata) { json.decodeFromString<ItemMetadata>(it) },
            record.encryptedPayload?.let(::decryptText),
        )
    }

    private suspend fun requireActiveFolder(id: String): FolderState =
        readFolders().singleOrNull { it.record.folderId == id && !it.record.tombstone }
            ?: error("目标文件夹不存在或已删除。")

    private suspend fun requireItem(id: String, includeDeleted: Boolean): ItemState =
        readItems().singleOrNull { it.record.itemId == id && (includeDeleted || !it.record.tombstone) }
            ?: error("条目不存在或当前状态不允许该操作。")

    private suspend fun mutate(action: suspend () -> Unit) = mutationMutex.withLock {
        ensureInitialized()
        val prior = sourceSequence
        try {
            action()
        } catch (error: Exception) {
            sourceSequence = prior
            throw error
        }
    }

    private fun expandSelection(
        selection: Set<VaultEntitySelection>,
        folders: List<FolderState>,
        items: List<ItemState>,
        includeContents: Boolean,
    ): Set<VaultEntitySelection> {
        val result = selection.toMutableSet()
        selection.filter { it.kind == VaultEntityKind.FOLDER }.forEach { selected ->
            val descendants = descendants(selected.id, folders)
            val hasContents = descendants.isNotEmpty() || items.any {
                it.record.folderId == selected.id || it.record.folderId in descendants
            }
            check(!hasContents || includeContents) { "非空文件夹删除前必须明确包含其内容。" }
            if (includeContents) {
                result += descendants.map { VaultEntitySelection(VaultEntityKind.FOLDER, it) }
                result += items.filter {
                    it.record.folderId == selected.id || it.record.folderId in descendants
                }.map { VaultEntitySelection(VaultEntityKind.ITEM, it.record.itemId) }
            }
        }
        return result
    }

    private fun descendants(parentId: String, folders: List<FolderState>): Set<String> {
        val result = mutableSetOf<String>()
        val pending = ArrayDeque<String>()
        pending += parentId
        while (pending.isNotEmpty()) {
            val parent = pending.removeFirst()
            folders.filter { it.record.parentId == parent }.forEach { child ->
                if (result.add(child.record.folderId)) pending += child.record.folderId
            }
        }
        return result
    }

    private fun effectiveFolderPolicies(folders: List<FolderState>): Map<String, SyncPolicy> {
        val domain = folders.map(::domainFolder)
        val tree = VaultTree(domain)
        return domain.associate { it.id.value to (tree.effectivePolicy(it.id).override ?: SyncPolicy.LOCAL_ONLY) }
    }

    private fun effectivePolicy(folder: FolderState, policies: Map<String, SyncPolicy>): SyncPolicy =
        policies.getValue(folder.record.folderId)

    private fun effectivePolicy(item: ItemState, policies: Map<String, SyncPolicy>): SyncPolicy =
        item.metadata.policy?.let(::parsePolicy) ?: policies.getValue(item.record.folderId)

    private fun applySearch(folders: List<FolderState>, items: List<ItemState>, query: String?): SearchResult {
        val needle = query?.trim()?.takeIf(String::isNotEmpty) ?: return SearchResult(folders, items, false)
        val bodyBytes = items.sumOf { it.text?.toByteArray(StandardCharsets.UTF_8)?.size?.toLong() ?: 0L }
        val slow = folders.size + items.size > SEARCH_NAME_LIMIT ||
            items.count { it.text != null } > SEARCH_BODY_ITEM_LIMIT ||
            bodyBytes > SEARCH_BODY_BYTES_LIMIT ||
            items.any { (it.text?.toByteArray(StandardCharsets.UTF_8)?.size ?: 0) > SEARCH_SINGLE_BODY_BYTES_LIMIT }
        return SearchResult(
            folders.filter { contains(it.metadata.name, needle) || contains(it.metadata.description, needle) },
            items.filter {
                contains(it.metadata.title, needle) || contains(it.metadata.fileName, needle) || contains(it.text, needle)
            },
            slow,
        )
    }

    private fun queryFile(uri: Uri): FileDescriptor {
        var name: String? = null
        var size: Long? = null
        appContext.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)
            ?.use { cursor ->
                if (cursor.moveToFirst()) {
                    name = cursor.getString(cursor.getColumnIndexOrThrow(OpenableColumns.DISPLAY_NAME))
                    val sizeIndex = cursor.getColumnIndexOrThrow(OpenableColumns.SIZE)
                    if (!cursor.isNull(sizeIndex)) size = cursor.getLong(sizeIndex).takeIf { it >= 0 }
                }
            }
        val safeName = name?.substringAfterLast('/')?.substringAfterLast('\\')?.takeIf(String::isNotBlank)
            ?: "导入文件"
        return FileDescriptor(safeName, appContext.contentResolver.getType(uri) ?: "application/octet-stream", size)
    }

    private fun readOrCreateDeviceId(): DeviceId {
        val directory = File(root, "identity").apply { mkdirs() }
        val destination = File(directory, "device.id")
        if (destination.exists()) return DeviceId.parse(destination.readText().trim())
        val created = newDeviceId()
        val temporary = File(directory, ".device.id.${UUID.randomUUID()}.tmp")
        try {
            FileOutputStream(temporary).use { output ->
                output.write(created.value.toByteArray(StandardCharsets.US_ASCII))
                output.fd.sync()
            }
            return try {
                Files.move(temporary.toPath(), destination.toPath(), StandardCopyOption.ATOMIC_MOVE)
                created
            } catch (_: FileAlreadyExistsException) {
                DeviceId.parse(destination.readText().trim())
            }
        } finally {
            temporary.delete()
        }
    }

    private fun unprotect(wrapper: AndroidPlatformWrapper): ByteArray = protector.unprotect(
        AndroidProtectedSecret(wrapper.keyReference, wrapper.nonce, wrapper.protectedSecret),
        wrapperAad(VaultId.parse(wrapper.vaultId), wrapper.initializationId, wrapper.keyEpoch),
    )

    private fun wrapperAad(vaultId: VaultId, initializationId: String, epoch: Long): ByteArray =
        "clipshare:vault:android-wrapper:v1\u0000${vaultId.value}\u0000$initializationId\u0000$epoch"
            .toByteArray(StandardCharsets.US_ASCII)

    private fun serializeEnvelope(value: VaultCipherEnvelope): String = json.encodeToString(
        VaultEnvelopeContract(
            1,
            "A256GCM",
            value.purpose,
            value.vaultId,
            value.originDeviceId,
            value.entityId,
            value.field,
            value.keyEpoch,
            value.nonceCounter,
            value.nonce,
            value.paddedPlaintextBytes,
            value.cipherAndTag,
        ),
    )

    private fun parseEnvelope(value: String): VaultCipherEnvelope = VaultContractCodec.decodeEnvelope(value).let {
        VaultCipherEnvelope(
            it.purpose,
            it.vaultId,
            it.originDeviceId,
            it.entityId,
            it.field,
            it.keyEpoch,
            it.nonceCounter,
            it.nonce,
            it.paddedPlaintextBytes,
            it.cipherAndTag,
        )
    }

    private fun addSecret(epoch: Long, secret: ByteArray) {
        require(secret.size == EPOCH_SECRET_BYTES && epoch !in epochSecrets) { "Vault epoch secret state is invalid." }
        epochSecrets[epoch] = secret
    }

    private fun clearSecrets() {
        epochSecrets.values.forEach { it.fill(0) }
        epochSecrets.clear()
        vaultId = null
        deviceId = null
        sourceSequence = 0
    }

    private fun ensureInitialized() {
        check(!closed && initialized) { "Vault must be initialized before use." }
    }

    private suspend fun currentEpoch(): Long = dao.currentWriteEpoch()?.takeIf { it > 0 }
        ?: error("Vault database has no valid write epoch.")

    private fun requireVaultId(): VaultId = checkNotNull(vaultId) { "Vault is not initialized." }

    private fun requireDeviceId(): DeviceId = checkNotNull(deviceId) { "Device identity is not initialized." }

    private fun requireSecret(epoch: Long): ByteArray = checkNotNull(epochSecrets[epoch]) {
        "The required Vault epoch secret is not loaded."
    }

    private fun domainFolder(folder: FolderState): VaultFolder = VaultFolder(
        FolderId.parse(folder.record.folderId),
        folder.record.parentId?.let(FolderId::parse),
        folder.metadata.name,
        folder.metadata.description,
        folder.metadata.sortOrder,
        policySetting(folder.metadata.policy, folder.metadata.selectedDevices),
        VersionId.parse(folder.record.currentVersionId),
        folder.metadata.deletedAtMillis?.let(java.time.Instant::ofEpochMilli),
    )

    private fun policySetting(value: String?, selectedDevices: List<String>) = SyncPolicySetting(
        value?.let(::parsePolicy),
        selectedDevices.map(DeviceId::parse).toSet(),
    )

    private fun wireContentType(value: VaultContentType) = value.name

    private fun contentType(value: String) = VaultContentType.valueOf(value)

    private fun wirePolicy(value: SyncPolicy) = value.name

    private fun parsePolicy(value: String) = SyncPolicy.valueOf(value)

    private fun newUuid() = UUID.randomUUID().toString()

    private fun newVaultId() = VaultId.parse(newUuid())

    private fun newFolderId() = FolderId.parse(newUuid())

    private fun newItemId() = com.clipshare.core.vault.ItemId.parse(newUuid())

    private fun newVersionId() = VersionId.parse(newUuid())

    private fun newDeviceId() = DeviceId.parse(newUuid())

    private fun sha256(value: ByteArray) = MessageDigest.getInstance("SHA-256").digest(value).toLowerHex()

    private fun ByteArray.toLowerHex(): String = joinToString("") { "%02x".format(it) }

    private fun decodeB64(value: String): ByteArray = Base64.getUrlDecoder().decode(value)

    private fun b64(value: ByteArray): String = Base64.getUrlEncoder().withoutPadding().encodeToString(value)

    private fun contains(value: String?, query: String) = value?.contains(query, ignoreCase = true) == true

    private fun readChunk(input: java.io.InputStream, buffer: ByteArray): Int {
        var total = 0
        while (total < buffer.size) {
            val read = input.read(buffer, total, buffer.size - total)
            if (read < 0) break
            total += read
        }
        return total
    }

    @Serializable
    private data class FolderMetadata(
        val name: String,
        val description: String?,
        val sortOrder: Long,
        val policy: String?,
        val selectedDevices: List<String>,
        val templateRole: String?,
        val updatedAtMillis: Long,
        val deletedAtMillis: Long?,
    )

    @Serializable
    private data class ItemMetadata(
        val title: String,
        val fileName: String?,
        val sizeBytes: Long,
        val policy: String?,
        val selectedDevices: List<String>,
        val createdAtMillis: Long,
        val updatedAtMillis: Long,
        val deletedAtMillis: Long?,
    )

    @Serializable
    private data class EventPayload(
        val operation: String,
        val entityKind: String,
        val entityId: String,
        val baseVersionId: String?,
        val newVersionId: String,
    )

    @Serializable
    private data class DeletedAtPayload(val deletedAtMillis: Long)

    @Serializable
    private data class FileManifestPayload(
        val fileId: String,
        val generationId: String,
        val originDeviceId: String,
        val fileName: String,
        val contentType: String,
        val sizeBytes: Long,
        val chunkSize: Int,
        val chunkCount: Int,
    )

    private data class FolderState(val record: FolderEntity, val metadata: FolderMetadata)

    private data class ItemState(val record: ItemEntity, val metadata: ItemMetadata, val text: String?)

    private data class SearchResult(val folders: List<FolderState>, val items: List<ItemState>, val slow: Boolean)

    private data class FileDescriptor(val name: String, val contentType: String, val size: Long?)

    private companion object {
        const val DATABASE_NAME = "clipshare-vault.db"
        const val INITIAL_EPOCH = 1L
        const val RECENT_LIMIT = 100
        const val SEARCH_NAME_LIMIT = 50_000
        const val SEARCH_BODY_ITEM_LIMIT = 10_000
        const val SEARCH_SINGLE_BODY_BYTES_LIMIT = 256 * 1024
        const val SEARCH_BODY_BYTES_LIMIT = 64L * 1024 * 1024
        const val DAY_MILLIS = 86_400_000L
        const val TRASH_RETENTION_MILLIS = 30L * DAY_MILLIS
        const val GCM_TAG_BYTES = 16L
    }
}
