package com.clipshare.core.vault

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlinx.coroutines.test.runTest

class VaultInitializationTest {
    @Test
    fun `coordinator creates only from absent and resumes wrapper without replacement secret`() = runTest {
        val fresh = FakeOperations()
        val ready = VaultInitializationCoordinator(fresh).openOrCreate()
        assertEquals(1, fresh.createCalls)
        assertEquals(InitializationPhase.READY, ready.first.phase)
        assertEquals(InitializationPhase.READY, ready.second.phase)

        val recovery = FakeOperations(wrapper = wrapper(InitializationPhase.STAGED))
        VaultInitializationCoordinator(recovery).openOrCreate()
        assertEquals(0, recovery.createCalls)
        assertEquals(1, recovery.stageDatabaseCalls)
    }

    @Test
    fun `coordinator fails closed for database only without creating secret`() = runTest {
        val operations = FakeOperations(database = database())
        val failure = runCatching { VaultInitializationCoordinator(operations).openOrCreate() }
        assertTrue(failure.isFailure)
        assertEquals(0, operations.createCalls)
    }

    @Test
    fun `only both absent can create a new epoch secret`() {
        assertEquals(InitializationAction.CreateNew, VaultInitializationStateMachine.decide(null, null))
        assertTrue(VaultInitializationStateMachine.decide(null, database()) is InitializationAction.FailClosed)
        assertTrue(
            VaultInitializationStateMachine.decide(
                wrapper(InitializationPhase.READY),
                null,
            ) is InitializationAction.FailClosed,
        )
    }

    @Test
    fun `staged states resume existing material and promote monotonically`() {
        assertEquals(
            InitializationAction.ResumeFromStagedWrapper,
            VaultInitializationStateMachine.decide(wrapper(InitializationPhase.STAGED), null),
        )
        assertEquals(
            InitializationAction.ResumeStagedDatabase,
            VaultInitializationStateMachine.decide(
                wrapper(InitializationPhase.STAGED),
                database(InitializationPhase.STAGED),
            ),
        )
        assertEquals(
            InitializationAction.PromoteWrapperReady,
            VaultInitializationStateMachine.decide(
                wrapper(InitializationPhase.STAGED),
                database(InitializationPhase.READY),
            ),
        )
        assertEquals(
            InitializationAction.PopulateStagedDatabase,
            VaultInitializationStateMachine.decide(
                wrapper(InitializationPhase.STAGED),
                database(InitializationPhase.STAGED).copy(containsCiphertext = false),
            ),
        )
        assertEquals(
            InitializationAction.PromoteDatabaseReady,
            VaultInitializationStateMachine.decide(
                wrapper(InitializationPhase.READY),
                database(InitializationPhase.STAGED),
            ),
        )
        assertEquals(
            InitializationAction.OpenReady,
            VaultInitializationStateMachine.decide(
                wrapper(InitializationPhase.READY),
                database(InitializationPhase.READY),
            ),
        )
    }

    @Test
    fun `identity or digest mismatch fails closed even with ciphertext`() {
        val wrapper = wrapper(InitializationPhase.READY)
        val mismatches = listOf(
            database().copy(vaultId = VaultId.parse("10112233-4455-4677-8899-aabbccddeeff")),
            database().copy(initializationId = "90000000-0000-4000-8000-000000000002"),
            database().copy(keyEpoch = 2),
            database().copy(wrapperDigest = "11".repeat(32)),
            database().copy(containsCiphertext = false),
        )
        mismatches.forEach { database ->
            assertTrue(VaultInitializationStateMachine.decide(wrapper, database) is InitializationAction.FailClosed)
        }
    }

    @Test
    fun `coordinator handles ready wrapper staged database and detects nonprogress`() = runTest {
        val recovery = FakeOperations(
            wrapper = wrapper(InitializationPhase.READY),
            database = database(InitializationPhase.STAGED),
        )
        val result = VaultInitializationCoordinator(recovery).openOrCreate()
        assertEquals(InitializationPhase.READY, result.second.phase)

        val stuck = object : VaultInitializationOperations {
            override suspend fun observeWrapper(): WrapperObservation? = null

            override suspend fun observeDatabase(): DatabaseObservation? = null

            override suspend fun createStagedWrapper() = Unit

            override suspend fun stageDatabaseFromWrapper(wrapper: WrapperObservation) = Unit

            override suspend fun populateStagedDatabase(
                wrapper: WrapperObservation,
                database: DatabaseObservation,
            ) = Unit

            override suspend fun verifyExisting(
                wrapper: WrapperObservation,
                database: DatabaseObservation,
            ) = Unit

            override suspend fun promoteDatabaseReady() = Unit

            override suspend fun promoteWrapperReady() = Unit
        }
        assertTrue(runCatching { VaultInitializationCoordinator(stuck).openOrCreate() }.isFailure)
    }

    private fun wrapper(phase: InitializationPhase) = WrapperObservation(
        vaultId = vaultId(),
        initializationId = "90000000-0000-4000-8000-000000000001",
        keyEpoch = 1,
        digest = "00".repeat(32),
        phase = phase,
    )

    private fun database(phase: InitializationPhase = InitializationPhase.READY) = DatabaseObservation(
        vaultId = vaultId(),
        initializationId = "90000000-0000-4000-8000-000000000001",
        keyEpoch = 1,
        wrapperDigest = "00".repeat(32),
        phase = phase,
        containsCiphertext = true,
    )

    private fun vaultId() = VaultId.parse("00112233-4455-4677-8899-aabbccddeeff")

    private class FakeOperations(
        var wrapper: WrapperObservation? = null,
        var database: DatabaseObservation? = null,
    ) : VaultInitializationOperations {
        var createCalls = 0
        var stageDatabaseCalls = 0

        override suspend fun observeWrapper(): WrapperObservation? = wrapper

        override suspend fun observeDatabase(): DatabaseObservation? = database

        override suspend fun createStagedWrapper() {
            check(wrapper == null && database == null)
            createCalls += 1
            wrapper = wrapper(InitializationPhase.STAGED)
        }

        override suspend fun stageDatabaseFromWrapper(wrapper: WrapperObservation) {
            stageDatabaseCalls += 1
            database = database(InitializationPhase.STAGED).copy(containsCiphertext = false)
        }

        override suspend fun populateStagedDatabase(wrapper: WrapperObservation, database: DatabaseObservation) {
            this.database = database.copy(containsCiphertext = true)
        }

        override suspend fun verifyExisting(wrapper: WrapperObservation, database: DatabaseObservation) {
            check(wrapper.digest == database.wrapperDigest && database.containsCiphertext)
        }

        override suspend fun promoteDatabaseReady() {
            database = checkNotNull(database).copy(phase = InitializationPhase.READY)
        }

        override suspend fun promoteWrapperReady() {
            wrapper = checkNotNull(wrapper).copy(phase = InitializationPhase.READY)
        }

        private fun wrapper(phase: InitializationPhase) = WrapperObservation(
            vaultId = VaultId.parse("00112233-4455-4677-8899-aabbccddeeff"),
            initializationId = "90000000-0000-4000-8000-000000000001",
            keyEpoch = 1,
            digest = "00".repeat(32),
            phase = phase,
        )

        private fun database(phase: InitializationPhase = InitializationPhase.READY) = DatabaseObservation(
            vaultId = VaultId.parse("00112233-4455-4677-8899-aabbccddeeff"),
            initializationId = "90000000-0000-4000-8000-000000000001",
            keyEpoch = 1,
            wrapperDigest = "00".repeat(32),
            phase = phase,
            containsCiphertext = true,
        )
    }
}
