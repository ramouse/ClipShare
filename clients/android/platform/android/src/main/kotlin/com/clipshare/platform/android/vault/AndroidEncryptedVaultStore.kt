package com.clipshare.platform.android.vault

import com.clipshare.core.crypto.CryptoPurpose
import com.clipshare.core.crypto.NonceAllocation
import com.clipshare.core.vault.DeviceId
import com.clipshare.core.vault.VaultId
import java.security.SecureRandom

class AndroidEncryptedVaultStore private constructor(
    private val dao: VaultRoomDao,
    private val originDeviceId: DeviceId,
    private val random: SecureRandom,
) {
    constructor(dao: VaultRoomDao, originDeviceId: DeviceId) : this(dao, originDeviceId, SecureRandom())

    internal constructor(
        dao: VaultRoomDao,
        originDeviceId: DeviceId,
        testRandom: SecureRandom,
        @Suppress("UNUSED_PARAMETER") testOnly: TestNonceSource,
    ) : this(dao, originDeviceId, testRandom)

    suspend fun reserveRecordNonce(
        vaultId: VaultId,
        keyEpoch: Long,
        purpose: CryptoPurpose,
    ): NonceAllocation {
        require(keyEpoch > 0) { "Epoch numbers are positive." }
        require(purpose != CryptoPurpose.FILE_CHUNK) { "File chunks use generation-scoped counters." }
        val candidatePrefix = ByteArray(4).also(random::nextBytes)
        return try {
            dao.allocateNonce(
                vaultId.value,
                keyEpoch,
                purpose.wireValue,
                originDeviceId.value,
                candidatePrefix,
            )
        } finally {
            candidatePrefix.fill(0)
        }
    }

    suspend fun saveFolderAtomically(
        folder: FolderEntity,
        version: ItemVersionEntity,
        event: SyncEventEntity,
    ) = dao.insertFolderVersionAndEvent(folder, version, event)

    suspend fun saveItemAtomically(
        item: ItemEntity,
        version: ItemVersionEntity,
        event: SyncEventEntity,
    ) = dao.insertItemVersionAndEvent(item, version, event)

    internal object TestNonceSource
}
