package com.clipshare.platform.android.vault

import com.clipshare.core.vault.DatabaseObservation
import com.clipshare.core.vault.InitializationPhase
import com.clipshare.core.vault.VaultId

class AndroidRoomVaultStateStore(
    private val dao: VaultRoomDao,
    private val databaseFileExistedAtOpen: Boolean,
) {
    suspend fun observe(): DatabaseObservation? {
        val state = dao.vaultState()
        if (state == null) {
            check(!databaseFileExistedAtOpen) { "Vault database exists without initialization state." }
            return null
        }
        return DatabaseObservation(
            vaultId = VaultId.parse(state.vaultId),
            initializationId = state.initializationId,
            keyEpoch = state.currentWriteEpoch,
            wrapperDigest = state.wrapperDigest,
            phase = InitializationPhase.valueOf(state.initializationState),
            containsCiphertext = dao.ciphertextCount() > 0,
        )
    }

    suspend fun createEmptyStaged(state: VaultStateEntity) {
        check(!databaseFileExistedAtOpen) { "An existing database may not be replaced during Vault initialization." }
        require(state.initializationState == InitializationPhase.STAGED.name) { "Database must be created STAGED." }
        dao.insertVaultState(state)
    }

    suspend fun populateDefaultFolders(defaults: List<VaultDefaultFolderSeed>) {
        dao.populateDefaultFolders(defaults)
    }

    suspend fun promoteReady() {
        check(dao.promoteInitializationReady() == 1) { "Vault database cannot be promoted from its current state." }
    }
}
