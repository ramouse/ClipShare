package com.clipshare.platform.android.vault

import android.content.Context
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.clipshare.core.crypto.CryptoPurpose
import com.clipshare.core.crypto.NonceAllocation
import com.clipshare.core.crypto.VaultCryptoContext
import com.clipshare.core.crypto.VaultCryptography
import com.clipshare.core.vault.DeviceId
import com.clipshare.core.vault.InitializationPhase
import com.clipshare.core.vault.VaultId
import java.io.File
import java.security.KeyStore
import java.util.Base64
import java.util.UUID
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class VaultPlatformInstrumentedTest {
    private lateinit var context: Context
    private lateinit var database: VaultRoomDatabase
    private lateinit var databaseName: String

    @Before
    fun setUp() {
        context = ApplicationProvider.getApplicationContext()
        databaseName = "vault-c2-${UUID.randomUUID()}.db"
        database = Room.databaseBuilder(context, VaultRoomDatabase::class.java, databaseName)
            .setJournalMode(androidx.room.RoomDatabase.JournalMode.WRITE_AHEAD_LOGGING)
            .build()
    }

    @After
    fun tearDown() {
        database.close()
        context.deleteDatabase(databaseName)
    }

    @Test
    fun roomInitializationNonceAndEventReplayAreCrashConsistent() = runBlocking {
        val dao = database.vaultDao()
        val stateStore = AndroidRoomVaultStateStore(dao, databaseFileExistedAtOpen = false)
        assertEquals(null, stateStore.observe())
        stateStore.createEmptyStaged(stagedState())
        assertFalse(checkNotNull(stateStore.observe()).containsCiphertext)
        stateStore.populateDefaultFolders(defaults("cipher"))
        assertEquals(InitializationPhase.STAGED, stateStore.observe()?.phase)
        assertTrue(checkNotNull(stateStore.observe()).containsCiphertext)

        val encryptedStore = AndroidEncryptedVaultStore(dao, DeviceId.parse(DEVICE_ID))
        assertTrue(
            runCatching {
                encryptedStore.reserveRecordNonce(VaultId.parse(VAULT_ID), 0, CryptoPurpose.ITEM_PAYLOAD)
            }.isFailure,
        )
        val first = encryptedStore.reserveRecordNonce(VaultId.parse(VAULT_ID), 1, CryptoPurpose.ITEM_PAYLOAD)
        val second = encryptedStore.reserveRecordNonce(VaultId.parse(VAULT_ID), 1, CryptoPurpose.ITEM_PAYLOAD)
        assertEquals(1, first.counter)
        assertEquals(2, second.counter)
        assertArrayEquals(first.prefix, second.prefix)
        database.openHelper.writableDatabase.execSQL(
            "UPDATE nonce_state SET lastCounter = ? WHERE vaultId = ? AND keyEpoch = ? AND purpose = ? " +
                "AND originDeviceId = ?",
            arrayOf<Any>(Long.MAX_VALUE, VAULT_ID, 1L, "item-payload", DEVICE_ID),
        )
        assertTrue(
            runCatching {
                encryptedStore.reserveRecordNonce(VaultId.parse(VAULT_ID), 1, CryptoPurpose.ITEM_PAYLOAD)
            }.isFailure,
        )

        val item = ItemEntity(
            itemId = ITEM_ID,
            vaultId = VAULT_ID,
            folderId = FOLDER_IDS.first(),
            contentType = "TEXT",
            currentVersionId = ITEM_VERSION_ID,
            keyEpoch = 1,
            tombstone = false,
            encryptedMetadata = "cipher-metadata",
            encryptedPayload = "cipher-payload",
        )
        val version = ItemVersionEntity(ITEM_VERSION_ID, ITEM_ID, "ITEM", null, 1, "cipher-snapshot")
        val event = SyncEventEntity(
            eventId = ITEM_EVENT_ID,
            sourceDeviceId = DEVICE_ID,
            sourceSequence = 100,
            logicalTime = 100,
            entityId = ITEM_ID,
            entityKind = "ITEM",
            operation = "CREATE",
            baseVersionId = null,
            newVersionId = ITEM_VERSION_ID,
            keyEpoch = 1,
            encryptedBody = "cipher-event",
            bodyDigest = "aa".repeat(32),
            disposition = "APPLIED",
        )
        assertTrue(encryptedStore.saveItemAtomically(item, version, event))
        assertFalse(encryptedStore.saveItemAtomically(item, version, event))
        val substitution = event.copy(eventId = SUBSTITUTE_EVENT_ID, bodyDigest = "bb".repeat(32))
        assertTrue(runCatching { encryptedStore.saveItemAtomically(item, version, substitution) }.isFailure)
        assertTrue(
            runCatching {
                encryptedStore.saveItemAtomically(item, version, event.copy(logicalTime = 101))
            }.isFailure,
        )
        assertTrue(
            runCatching {
                encryptedStore.saveItemAtomically(
                    item,
                    version,
                    event.copy(eventId = SUBSTITUTE_EVENT_ID, sourceSequence = 99),
                )
            }.isFailure,
        )

        stateStore.promoteReady()
        assertEquals(InitializationPhase.READY, stateStore.observe()?.phase)
        assertTrue(runCatching { stateStore.promoteReady() }.isFailure)
    }

    @Test
    fun keystoreWrapperRoundTripsAndPersistsNonceUniqueness() {
        val alias = "clipshare.vault.wrap.v1.${UUID.randomUUID().toString().replace("-", "")}"
        val keyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        try {
            val protector = AndroidVaultKeyProtector(keyStore)
            protector.createWrappingKey(alias)
            assertEquals(null, keyStore.getKey(alias, null).encoded)
            val secret = ByteArray(32) { it.toByte() }
            val aad = "vault-wrapper-aad".toByteArray()
            val first = protector.protect(alias, secret, aad, emptySet())
            val second = protector.protect(alias, secret, aad, setOf(first.nonce))
            assertNotEquals(first.nonce, second.nonce)
            assertArrayEquals(secret, protector.unprotect(second, aad))
            assertTrue(runCatching { protector.unprotect(second, "other-aad".toByteArray()) }.isFailure)

            val directory = File(context.noBackupFilesDir, "vault-wrapper-${UUID.randomUUID()}")
            try {
                val store = AndroidPlatformWrapperStore(directory)
                val staged = AndroidPlatformWrapper(
                    vaultId = VAULT_ID,
                    initializationId = INITIALIZATION_ID,
                    keyEpoch = 1,
                    state = "STAGED",
                    keyReference = alias,
                    nonce = second.nonce,
                    protectedSecret = second.cipherAndTag,
                )
                store.writeAtomically(staged)
                assertFalse(first.nonce in store.usedNonces(alias))
                assertTrue(second.nonce in store.usedNonces(alias))
                val digest = store.digest(staged)
                store.writeAtomically(staged.copy(state = "READY"))
                assertEquals(digest, store.digest(staged.copy(state = "READY")))
                assertEquals(InitializationPhase.READY, store.observe(VaultId.parse(VAULT_ID), 1)?.phase)
                assertTrue(runCatching { store.writeAtomically(staged) }.isFailure)
                assertTrue(
                    runCatching {
                        store.writeAtomically(staged.copy(state = "READY", protectedSecret = first.cipherAndTag))
                    }.isFailure,
                )
                assertTrue(
                    runCatching {
                        store.writeAtomically(staged.copy(keyEpoch = 2, state = "STAGED"))
                    }.isFailure,
                )
                assertTrue(runCatching { store.read("../escape", 1) }.isFailure)
                assertTrue(runCatching { store.usedNonces("invalid-alias") }.isFailure)
                assertTrue(runCatching { store.writeAtomically(staged.copy(nonce = "AA")) }.isFailure)

                val stale = File(directory, ".$VAULT_ID.1.wrapper.json.${UUID.randomUUID()}.tmp")
                stale.writeText("stale")
                assertTrue(runCatching { store.writeAtomically(staged.copy(state = "READY")) }.isFailure)
                assertTrue(stale.delete())

                val corrupt = File(directory, "corrupt.tmp")
                corrupt.writeText("{")
                assertTrue(runCatching { store.usedNonces(alias) }.isFailure)
                assertTrue(corrupt.delete())

                val corruptReservation = File(directory, ".corrupt.reservation.json")
                corruptReservation.writeText("{")
                assertTrue(runCatching { store.usedNonces(alias) }.isFailure)
                assertTrue(corruptReservation.delete())
            } finally {
                directory.deleteRecursively()
            }
        } finally {
            if (keyStore.containsAlias(alias)) keyStore.deleteEntry(alias)
        }
    }

    @Test
    fun databaseWalShmAndBlobContainNoPlaintextSentinels() = runBlocking {
        val secret = ByteArray(32) { it.toByte() }
        val encryptedDefaults = DEFAULT_NAMES.mapIndexed { index, name ->
            val encrypted = encryptRecord(secret, FOLDER_IDS[index], index + 1L, name)
            default(index, encrypted)
        }
        val stateStore = AndroidRoomVaultStateStore(database.vaultDao(), databaseFileExistedAtOpen = false)
        stateStore.createEmptyStaged(stagedState())
        stateStore.populateDefaultFolders(encryptedDefaults)

        val sentinel = "CLIPSHARE-C2-PLAINTEXT-秘密-20260902"
        val fileEnvelope = VaultCryptography.encryptFileChunk(
            ByteArray(32) { 7 },
            VaultCryptoContext(
                purpose = CryptoPurpose.FILE_CHUNK,
                vaultId = VaultId.parse(VAULT_ID),
                originDeviceId = DeviceId.parse(DEVICE_ID),
                entityId = FILE_ID,
                field = "chunk",
                keyEpoch = 1,
            ),
            NonceAllocation(byteArrayOf(4, 3, 2, 1), 0),
            sentinel.toByteArray(),
        )
        val blobRoot = File(context.noBackupFilesDir, "vault-blob-${UUID.randomUUID()}")
        try {
            AndroidEncryptedBlobStore(blobRoot).writeChunkAtomically(
                FILE_ID,
                GENERATION_ID,
                0,
                Base64.getUrlDecoder().decode(fileEnvelope.cipherAndTag),
            )
            database.openHelper.writableDatabase.query("PRAGMA wal_checkpoint(PASSIVE)").use { cursor ->
                assertTrue(cursor.moveToFirst())
            }

            val artifacts = buildList {
                val databaseFile = context.getDatabasePath(databaseName)
                add(databaseFile)
                add(File(databaseFile.path + "-wal"))
                add(File(databaseFile.path + "-shm"))
                addAll(blobRoot.walkTopDown().filter(File::isFile))
            }.filter(File::exists)
            val forbidden = DEFAULT_NAMES + sentinel
            artifacts.forEach { artifact ->
                val bytes = artifact.readBytes()
                forbidden.forEach { value -> assertFalse(bytes.containsSubsequence(value.toByteArray())) }
            }
        } finally {
            blobRoot.deleteRecursively()
            secret.fill(0)
        }
    }

    @Test
    fun fileGenerationBlobIntegrityAndRoomGuardsAreEnforced() = runBlocking {
        val dao = database.vaultDao()
        val existingDatabaseState = AndroidRoomVaultStateStore(dao, databaseFileExistedAtOpen = true)
        assertTrue(runCatching { existingDatabaseState.observe() }.isFailure)
        assertTrue(runCatching { existingDatabaseState.createEmptyStaged(stagedState()) }.isFailure)

        val ciphertext = ByteArray(48) { index -> (index * 3).toByte() }
        val blobRoot = File(context.noBackupFilesDir, "vault-blob-integrity-${UUID.randomUUID()}")
        try {
            val blobStore = AndroidEncryptedBlobStore(blobRoot)
            val stored = blobStore.writeChunkAtomically(FILE_ID, GENERATION_ID, 0, ciphertext)
            assertArrayEquals(ciphertext, blobStore.readChunk(stored))
            assertTrue(runCatching { blobStore.writeChunkAtomically(FILE_ID, GENERATION_ID, 0, ciphertext) }.isFailure)
            assertEquals(ciphertext.size.toLong(), stored.ciphertextBytes)
            assertEquals(0, stored.chunkIndex)

            val manifest = FileManifestEntity(
                manifestId = FILE_MANIFEST_ID,
                fileId = FILE_ID,
                itemId = ITEM_ID,
                generationId = GENERATION_ID,
                keyEpoch = 1,
                chunkSize = 1_048_576,
                chunkCount = 1,
                encryptedManifest = "cipher-manifest",
                wrappedFileKey = "cipher-file-key",
                committed = false,
            )
            val chunk = FileChunkEntity(
                fileId = stored.fileId,
                generationId = stored.generationId,
                chunkIndex = stored.chunkIndex,
                nonce = "AQIDBAAAAAAAAAAA",
                ciphertextBytes = stored.ciphertextBytes,
                ciphertextSha256 = stored.ciphertextSha256,
                relativePath = stored.relativePath,
            )
            dao.commitFileGeneration(manifest, listOf(chunk))
            assertEquals(1L, dao.ciphertextCount())
            assertTrue(runCatching { dao.commitFileGeneration(manifest.copy(committed = true), listOf(chunk)) }.isFailure)
            assertTrue(runCatching { dao.commitFileGeneration(manifest.copy(chunkCount = 2), listOf(chunk)) }.isFailure)
            assertTrue(
                runCatching { dao.commitFileGeneration(manifest, listOf(chunk.copy(chunkIndex = 1))) }.isFailure,
            )
            assertTrue(
                runCatching {
                    dao.commitFileGeneration(manifest, listOf(chunk.copy(generationId = ITEM_EVENT_ID)))
                }.isFailure,
            )

            assertTrue(runCatching { blobStore.writeChunkAtomically(FILE_ID, GENERATION_ID, -1, ciphertext) }.isFailure)
            assertTrue(runCatching { blobStore.writeChunkAtomically("not-a-uuid", GENERATION_ID, 0, ciphertext) }.isFailure)
            assertTrue(runCatching { blobStore.writeChunkAtomically(FILE_ID, GENERATION_ID, 0, ByteArray(15)) }.isFailure)
            assertTrue(runCatching { blobStore.readChunk(stored.copy(relativePath = "../escape.bin")) }.isFailure)
            assertTrue(runCatching { blobStore.readChunk(stored.copy(fileId = ITEM_ID)) }.isFailure)
            assertTrue(runCatching { blobStore.readChunk(stored.copy(chunkIndex = -1)) }.isFailure)
            assertTrue(runCatching { blobStore.readChunk(stored.copy(ciphertextBytes = 47)) }.isFailure)

            val storedFile = File(blobRoot, stored.relativePath)
            val corrupted = storedFile.readBytes().also { it[0] = (it[0].toInt() xor 0xff).toByte() }
            storedFile.writeBytes(corrupted)
            assertTrue(runCatching { blobStore.readChunk(stored) }.isFailure)

            val nonceState = NonceStateEntity(VAULT_ID, 1, "ITEM_PAYLOAD", DEVICE_ID, byteArrayOf(1, 2, 3, 4), 7)
            val sameNonceState = nonceState.copy(prefix = nonceState.prefix.copyOf())
            assertEquals(nonceState, sameNonceState)
            assertEquals(nonceState.hashCode(), sameNonceState.hashCode())
            assertNotEquals(nonceState, sameNonceState.copy(lastCounter = 8))

            val deviceWrapper = DeviceKeyWrapperEntity(DEVICE_ID, 1, "cipher-wrapper", revoked = false)
            val tombstone = TombstoneEntity(ITEM_ID, "ITEM", ITEM_VERSION_ID, 1, "cipher-time", 2)
            val migration = MigrationStateEntity("migration-1", 1, 2, "STAGED", "cipher-rollback-path")
            assertEquals(listOf(DEVICE_ID, ITEM_ID, "migration-1"), listOf(deviceWrapper.deviceId, tombstone.entityId, migration.migrationId))
        } finally {
            blobRoot.deleteRecursively()
            ciphertext.fill(0)
        }
    }

    private fun stagedState() = VaultStateEntity(
        vaultId = VAULT_ID,
        initializationId = INITIALIZATION_ID,
        formatVersion = 1,
        currentWriteEpoch = 1,
        initializationState = "STAGED",
        wrapperDigest = "00".repeat(32),
    )

    private fun defaults(encrypted: String): List<VaultDefaultFolderSeed> =
        FOLDER_IDS.indices.map { default(it, encrypted) }

    private fun default(index: Int, encrypted: String): VaultDefaultFolderSeed {
        val versionId = VERSION_IDS[index]
        return VaultDefaultFolderSeed(
            FolderEntity(FOLDER_IDS[index], VAULT_ID, null, versionId, 1, false, encrypted),
            ItemVersionEntity(versionId, FOLDER_IDS[index], "FOLDER", null, 1, encrypted),
            SyncEventEntity(
                EVENT_IDS[index],
                DEVICE_ID,
                index + 1L,
                index + 1L,
                FOLDER_IDS[index],
                "FOLDER",
                "CREATE",
                null,
                versionId,
                1,
                encrypted,
                "aa".repeat(32),
                "APPLIED",
            ),
        )
    }

    private fun encryptRecord(secret: ByteArray, entityId: String, counter: Long, value: String): String =
        kotlinx.serialization.json.Json.encodeToString(
            com.clipshare.core.crypto.VaultCipherEnvelope.serializer(),
            VaultCryptography.encryptRecord(
                secret,
                VaultCryptoContext(
                    CryptoPurpose.FOLDER_METADATA,
                    VaultId.parse(VAULT_ID),
                    DeviceId.parse(DEVICE_ID),
                    entityId,
                    "metadata",
                    1,
                ),
                NonceAllocation(byteArrayOf(1, 2, 3, 4), counter),
                value.toByteArray(),
            ),
        )

    private fun ByteArray.containsSubsequence(needle: ByteArray): Boolean {
        if (needle.isEmpty() || size < needle.size) return false
        return (0..size - needle.size).any { start ->
            needle.indices.all { offset -> this[start + offset] == needle[offset] }
        }
    }

    private companion object {
        const val VAULT_ID = "00112233-4455-4677-8899-aabbccddeeff"
        const val DEVICE_ID = "11111111-2222-4333-8444-555555555555"
        const val INITIALIZATION_ID = "90000000-0000-4000-8000-000000000001"
        const val ITEM_ID = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"
        const val ITEM_VERSION_ID = "bbbbbbbb-cccc-4ddd-8eee-ffffffffffff"
        const val ITEM_EVENT_ID = "cccccccc-dddd-4eee-8fff-000000000001"
        const val SUBSTITUTE_EVENT_ID = "dddddddd-eeee-4fff-8000-000000000002"
        const val FILE_ID = "eeeeeeee-ffff-4000-8000-000000000003"
        const val GENERATION_ID = "ffffffff-0000-4000-8000-000000000004"
        const val FILE_MANIFEST_ID = "12345678-1234-4234-8234-123456789abc"
        val DEFAULT_NAMES = listOf("收件箱", "密码", "工作", "私人", "其他")
        val FOLDER_IDS = List(5) { index -> "0000000${index + 1}-0000-4000-8000-00000000000${index + 1}" }
        val VERSION_IDS = List(5) { index -> "0000001${index + 1}-0000-4000-8000-00000000000${index + 1}" }
        val EVENT_IDS = List(5) { index -> "0000002${index + 1}-0000-4000-8000-00000000000${index + 1}" }
    }
}
