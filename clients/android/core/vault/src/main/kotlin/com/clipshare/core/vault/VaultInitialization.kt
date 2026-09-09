package com.clipshare.core.vault

enum class InitializationPhase {
    STAGED,
    READY,
}

data class WrapperObservation(
    val vaultId: VaultId,
    val initializationId: String,
    val keyEpoch: Long,
    val digest: String,
    val phase: InitializationPhase,
)

data class DatabaseObservation(
    val vaultId: VaultId,
    val initializationId: String,
    val keyEpoch: Long,
    val wrapperDigest: String,
    val phase: InitializationPhase,
    val containsCiphertext: Boolean,
)

sealed interface InitializationAction {
    data object CreateNew : InitializationAction

    data object ResumeFromStagedWrapper : InitializationAction

    data object ResumeStagedDatabase : InitializationAction

    data object PopulateStagedDatabase : InitializationAction

    data object PromoteDatabaseReady : InitializationAction

    data object PromoteWrapperReady : InitializationAction

    data object OpenReady : InitializationAction

    data class FailClosed(val reason: String) : InitializationAction
}

object VaultInitializationStateMachine {
    @Suppress("CyclomaticComplexMethod", "ReturnCount")
    fun decide(wrapper: WrapperObservation?, database: DatabaseObservation?): InitializationAction {
        if (wrapper == null && database == null) return InitializationAction.CreateNew
        if (wrapper == null) return InitializationAction.FailClosed("Database exists without its platform wrapper.")
        if (database == null) {
            return if (wrapper.phase == InitializationPhase.STAGED) {
                InitializationAction.ResumeFromStagedWrapper
            } else {
                InitializationAction.FailClosed("A READY wrapper exists without its database.")
            }
        }
        if (!matches(wrapper, database)) {
            return InitializationAction.FailClosed("Wrapper and database identity, epoch, or digest mismatch.")
        }
        if (database.phase == InitializationPhase.READY && !database.containsCiphertext) {
            return InitializationAction.FailClosed("A READY Vault database has no encrypted initialization records.")
        }
        return when (wrapper.phase to database.phase) {
            InitializationPhase.STAGED to InitializationPhase.STAGED -> if (database.containsCiphertext) {
                InitializationAction.ResumeStagedDatabase
            } else {
                InitializationAction.PopulateStagedDatabase
            }
            InitializationPhase.READY to InitializationPhase.STAGED -> InitializationAction.PromoteDatabaseReady
            InitializationPhase.STAGED to InitializationPhase.READY -> InitializationAction.PromoteWrapperReady
            InitializationPhase.READY to InitializationPhase.READY -> InitializationAction.OpenReady
            else -> error("Unknown initialization state.")
        }
    }

    private fun matches(wrapper: WrapperObservation, database: DatabaseObservation): Boolean =
        wrapper.vaultId == database.vaultId &&
            wrapper.initializationId == database.initializationId &&
            wrapper.keyEpoch == database.keyEpoch &&
            wrapper.digest == database.wrapperDigest
}

interface VaultInitializationOperations {
    suspend fun observeWrapper(): WrapperObservation?

    suspend fun observeDatabase(): DatabaseObservation?

    suspend fun createStagedWrapper()

    suspend fun stageDatabaseFromWrapper(wrapper: WrapperObservation)

    suspend fun populateStagedDatabase(wrapper: WrapperObservation, database: DatabaseObservation)

    suspend fun verifyExisting(wrapper: WrapperObservation, database: DatabaseObservation)

    suspend fun promoteDatabaseReady()

    suspend fun promoteWrapperReady()
}

class VaultInitializationCoordinator(private val operations: VaultInitializationOperations) {
    suspend fun openOrCreate(): Pair<WrapperObservation, DatabaseObservation> {
        repeat(MAXIMUM_TRANSITIONS) {
            val wrapper = operations.observeWrapper()
            val database = operations.observeDatabase()
            when (val action = VaultInitializationStateMachine.decide(wrapper, database)) {
                InitializationAction.CreateNew -> operations.createStagedWrapper()
                InitializationAction.ResumeFromStagedWrapper -> {
                    operations.stageDatabaseFromWrapper(checkNotNull(wrapper))
                }
                InitializationAction.ResumeStagedDatabase,
                InitializationAction.PromoteDatabaseReady -> {
                    operations.verifyExisting(checkNotNull(wrapper), checkNotNull(database))
                    operations.promoteDatabaseReady()
                }
                InitializationAction.PopulateStagedDatabase -> {
                    operations.populateStagedDatabase(checkNotNull(wrapper), checkNotNull(database))
                }
                InitializationAction.PromoteWrapperReady -> {
                    operations.verifyExisting(checkNotNull(wrapper), checkNotNull(database))
                    operations.promoteWrapperReady()
                }
                InitializationAction.OpenReady -> {
                    operations.verifyExisting(checkNotNull(wrapper), checkNotNull(database))
                    return wrapper to database
                }
                is InitializationAction.FailClosed -> error(action.reason)
            }
        }
        error("Vault initialization did not make monotonic progress.")
    }

    private companion object {
        const val MAXIMUM_TRANSITIONS = 6
    }
}
