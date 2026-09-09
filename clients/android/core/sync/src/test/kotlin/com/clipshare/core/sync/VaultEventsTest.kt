package com.clipshare.core.sync

import com.clipshare.core.vault.DeviceId
import com.clipshare.core.vault.EventId
import com.clipshare.core.vault.VersionId
import java.time.Instant
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class VaultEventsTest {
    @Test
    fun `linear events apply and exact replays are idempotent`() {
        val event = event(1, base = version(1), next = version(2))
        val ledger = VaultEventLedger(setOf(1))
        assertEquals(EventDecision.ApplyLinear, ledger.accept(event, EntitySnapshot(version(1), false)))
        assertEquals(EventDecision.Duplicate, ledger.accept(event, EntitySnapshot(version(2), false)))
    }

    @Test
    fun `source sequence reuse and revoked devices fail closed`() {
        val device = device(1)
        val first = event(1, source = device)
        val ledger = VaultEventLedger(setOf(1))
        ledger.accept(first, EntitySnapshot(null, false))
        assertFails { ledger.accept(event(2, source = device, sequence = 1), EntitySnapshot(null, false)) }
        assertFails { VaultEventLedger(setOf(1), setOf(device)).accept(first, EntitySnapshot(null, false)) }
        assertFails { VaultEventLedger(setOf(2)).accept(first, EntitySnapshot(null, false)) }
        val orderedLedger = VaultEventLedger(setOf(1))
        orderedLedger.accept(event(3, source = device, sequence = 3), EntitySnapshot(null, false))
        assertFails { orderedLedger.accept(event(4, source = device, sequence = 2), EntitySnapshot(null, false)) }
        assertFails {
            val ledgerWithEvent = VaultEventLedger(setOf(1))
            ledgerWithEvent.accept(first, EntitySnapshot(null, false))
            ledgerWithEvent.accept(first.copy(encryptedBodyDigest = "11".repeat(32)), EntitySnapshot(null, false))
        }
    }

    @Test
    fun `concurrent edits are preserved and conflict IDs are deterministic`() {
        val concurrent = event(7, base = version(1), next = version(3))
        val decision = VaultEventLedger(setOf(1)).accept(concurrent, EntitySnapshot(version(2), false))
        val conflict = decision as EventDecision.CreateConflictCopy
        assertEquals(
            conflict.conflictEntityId,
            VaultEventLedger.conflictEntityId(concurrent.entityId, concurrent.eventId),
        )
        assertTrue(conflict.conflictEntityId.substring(14, 15) == "8")
    }

    @Test
    fun `delete edit conflicts preserve tombstones and cyclic moves quarantine`() {
        val edit = event(3, operation = VaultOperation.UPDATE, base = version(1))
        assertTrue(
            VaultEventLedger(setOf(1)).accept(edit, EntitySnapshot(version(2), true))
                is EventDecision.PreserveTombstoneAndConflict,
        )
        val move = event(4, kind = EntityKind.FOLDER, operation = VaultOperation.MOVE, base = version(1))
        assertEquals(
            EventDecision.QuarantineCycle,
            VaultEventLedger(setOf(1)).accept(move, EntitySnapshot(version(2), false, true)),
        )
        assertTrue(
            VaultEventLedger(setOf(1)).accept(
                event(5, operation = VaultOperation.TRASH, base = version(1)),
                EntitySnapshot(version(2), false),
            ) is EventDecision.PreserveTombstoneAndConflict,
        )
        assertTrue(
            VaultEventLedger(setOf(1)).accept(
                event(6, kind = EntityKind.ITEM, operation = VaultOperation.MOVE, base = version(1)),
                EntitySnapshot(version(2), false, true),
            ) is EventDecision.CreateConflictCopy,
        )
        assertTrue(
            VaultEventLedger(setOf(1)).accept(
                event(8, kind = EntityKind.FOLDER, operation = VaultOperation.UPDATE, base = version(1)),
                EntitySnapshot(version(2), false, true),
            ) is EventDecision.CreateConflictCopy,
        )
    }

    @Test
    fun `trash expires at the exact thirty day boundary`() {
        val deleted = Instant.parse("2026-09-02T00:00:00Z")
        assertFalse(TrashRetention.isExpired(deleted, deleted.plus(TrashRetention.duration).minusNanos(1)))
        assertTrue(TrashRetention.isExpired(deleted, deleted.plus(TrashRetention.duration)))
    }

    @Test
    fun `historical reencryption cannot remove a referenced old epoch`() {
        var job = HistoricalReencryptionJob(1, 2, 2).start()
        job = job.copied(1).pause().resume().copied(1).verify().commit()
        assertFails { job.complete(1) }
        assertEquals(ReencryptionState.COMPLETE, job.complete(0).state)
    }

    @Test
    fun `historical reencryption validates plan progress and every transition`() {
        assertFails { HistoricalReencryptionJob(0, 2, 1) }
        assertFails { HistoricalReencryptionJob(2, 2, 1) }
        assertFails { HistoricalReencryptionJob(2, 1, 1) }
        assertFails { HistoricalReencryptionJob(1, 2, -1) }
        assertFails { HistoricalReencryptionJob(1, 2, 1, -1) }
        assertFails { HistoricalReencryptionJob(1, 2, 1, 2) }

        val plan = HistoricalReencryptionJob(1, 2, 1)
        assertFails { plan.copied(1) }
        assertFails { plan.pause() }
        assertFails { plan.verify() }
        assertFails { plan.commit() }
        assertFails { plan.complete(0) }

        val copying = plan.start()
        assertFails { copying.start() }
        assertFails { copying.copied(0) }
        assertFails { copying.copied(2) }
        assertFails { copying.verify() }
        assertFails { copying.resume() }
        val paused = copying.pause()
        assertFails { paused.pause() }
        val verifying = paused.resume().copied(1).verify()
        assertFails { verifying.verify() }
        val committing = verifying.commit()
        assertFails { committing.commit() }
        val failed = committing.failRollback()
        assertEquals(ReencryptionState.FAILED_ROLLBACK, failed.state)
        assertFails { failed.failRollback() }
        assertFails { committing.complete(0).failRollback() }
    }

    @Test
    fun `new epoch wrappers target exactly non revoked devices`() {
        val active = device(1)
        val revoked = device(2)
        val plan = EpochDistributionPlan.create(
            1,
            listOf(PairedDeviceState(active, false), PairedDeviceState(revoked, true)),
        )
        assertEquals(2, plan.newEpoch)
        assertEquals(setOf(active), plan.recipients)
        plan.validateGeneratedWrappers(setOf(active))
        assertFails { plan.validateGeneratedWrappers(setOf(active, revoked)) }
        assertFails { EpochDistributionPlan.create(0, emptyList()) }
        assertFails { EpochDistributionPlan.create(Long.MAX_VALUE, emptyList()) }
        assertFails {
            EpochDistributionPlan.create(
                1,
                listOf(PairedDeviceState(active, false), PairedDeviceState(active, true)),
            )
        }
        val none = EpochDistributionPlan.create(1, listOf(PairedDeviceState(revoked, true)))
        assertEquals(emptySet<DeviceId>(), none.recipients)
    }

    @Test
    fun `event schema rejects each invalid counter digest and conflict identifier`() {
        assertFails { event(1, sequence = 0) }
        assertFails { event(1, logicalTime = 0) }
        assertFails { event(1, keyEpoch = 0) }
        assertFails { event(1, digest = "not-a-digest") }
        assertFails { event(1, entityId = "not-a-uuid") }
        assertFails {
            VaultEventLedger.conflictEntityId(
                "not-a-uuid",
                EventId.parse("20000000-0000-4000-8000-000000000001"),
            )
        }
    }

    private fun event(
        value: Int,
        source: DeviceId = device(1),
        sequence: Long = value.toLong(),
        kind: EntityKind = EntityKind.ITEM,
        operation: VaultOperation = VaultOperation.UPDATE,
        base: VersionId? = null,
        next: VersionId = version(value + 10),
        logicalTime: Long = value.toLong(),
        keyEpoch: Long = 1,
        digest: String = "00".repeat(32),
        entityId: String = "30000000-0000-4000-8000-000000000001",
    ) = VaultEvent(
        eventId = EventId.parse("20000000-0000-4000-8000-${value.toString().padStart(12, '0')}"),
        sourceDeviceId = source,
        sourceSequence = sequence,
        logicalTime = logicalTime,
        entityKind = kind,
        entityId = entityId,
        operation = operation,
        baseVersionId = base,
        newVersionId = next,
        keyEpoch = keyEpoch,
        encryptedBodyDigest = digest,
    )

    private fun device(value: Int) = DeviceId.parse("10000000-0000-4000-8000-${value.toString().padStart(12, '0')}")

    private fun version(value: Int) = VersionId.parse("40000000-0000-4000-8000-${value.toString().padStart(12, '0')}")

    private fun assertFails(operation: () -> Unit) {
        var failed = false
        try {
            operation()
        } catch (_: IllegalArgumentException) {
            failed = true
        } catch (_: IllegalStateException) {
            failed = true
        }
        assertTrue("Expected fail-closed behavior", failed)
    }
}
