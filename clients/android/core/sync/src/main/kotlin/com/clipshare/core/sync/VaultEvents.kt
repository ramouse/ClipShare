@file:Suppress("MagicNumber", "ReturnCount", "ThrowsCount")

package com.clipshare.core.sync

import com.clipshare.core.vault.DeviceId
import com.clipshare.core.vault.EventId
import com.clipshare.core.vault.VersionId
import java.security.MessageDigest
import java.time.Duration
import java.time.Instant

enum class EntityKind {
    FOLDER,
    ITEM,
}

enum class VaultOperation {
    CREATE,
    UPDATE,
    MOVE,
    TRASH,
    RESTORE,
    PURGE,
}

data class VaultEvent(
    val eventId: EventId,
    val sourceDeviceId: DeviceId,
    val sourceSequence: Long,
    val logicalTime: Long,
    val entityKind: EntityKind,
    val entityId: String,
    val operation: VaultOperation,
    val baseVersionId: VersionId?,
    val newVersionId: VersionId,
    val keyEpoch: Long,
    val encryptedBodyDigest: String,
) {
    init {
        require(sourceSequence > 0 && logicalTime > 0 && keyEpoch > 0) { "Event counters and epoch are positive." }
        require(Regex("^[0-9a-f]{64}$").matches(encryptedBodyDigest)) { "Event body digest must be SHA-256." }
        require(LOWERCASE_UUID.matches(entityId)) { "Event entity IDs must be lowercase UUIDs." }
    }
}

data class EntitySnapshot(
    val currentVersionId: VersionId?,
    val tombstone: Boolean,
    val moveWouldCreateCycle: Boolean = false,
)

sealed interface EventDecision {
    data object ApplyLinear : EventDecision

    data object Duplicate : EventDecision

    data class CreateConflictCopy(val conflictEntityId: String) : EventDecision

    data class PreserveTombstoneAndConflict(val conflictEntityId: String) : EventDecision

    data object QuarantineCycle : EventDecision
}

class EventRejectedException(message: String) : IllegalStateException(message)

class VaultEventLedger(
    private val acceptedEpochs: Set<Long>,
    private val revokedDevices: Set<DeviceId> = emptySet(),
) {
    private val byEventId = mutableMapOf<EventId, VaultEvent>()
    private val bySourceSequence = mutableMapOf<Pair<DeviceId, Long>, EventId>()
    private val highestSourceSequence = mutableMapOf<DeviceId, Long>()

    fun accept(event: VaultEvent, snapshot: EntitySnapshot): EventDecision {
        val existing = byEventId[event.eventId]
        if (existing != null) {
            if (existing == event) return EventDecision.Duplicate
            throw EventRejectedException("An event ID was reused with different content.")
        }
        if (event.sourceDeviceId in revokedDevices) {
            throw EventRejectedException("Events from revoked devices are rejected.")
        }
        if (event.keyEpoch !in acceptedEpochs) {
            throw EventRejectedException("Events for unavailable epochs are rejected.")
        }

        val sourceKey = event.sourceDeviceId to event.sourceSequence
        if (bySourceSequence.containsKey(sourceKey)) {
            throw EventRejectedException("A source sequence was reused by a different event.")
        }
        val highest = highestSourceSequence[event.sourceDeviceId]
        if (highest != null && event.sourceSequence <= highest) {
            throw EventRejectedException("An older source sequence cannot be accepted after a newer event.")
        }

        val decision = decide(event, snapshot)
        byEventId[event.eventId] = event
        bySourceSequence[sourceKey] = event.eventId
        highestSourceSequence[event.sourceDeviceId] = event.sourceSequence
        return decision
    }

    private fun decide(event: VaultEvent, snapshot: EntitySnapshot): EventDecision {
        if (event.baseVersionId == snapshot.currentVersionId) return EventDecision.ApplyLinear
        if (event.entityKind == EntityKind.FOLDER &&
            event.operation == VaultOperation.MOVE &&
            snapshot.moveWouldCreateCycle
        ) {
            return EventDecision.QuarantineCycle
        }
        val conflictId = conflictEntityId(event.entityId, event.eventId)
        return if (event.operation == VaultOperation.TRASH || snapshot.tombstone) {
            EventDecision.PreserveTombstoneAndConflict(conflictId)
        } else {
            EventDecision.CreateConflictCopy(conflictId)
        }
    }

    companion object {
        fun conflictEntityId(entityId: String, eventId: EventId): String {
            val digest = MessageDigest.getInstance("SHA-256")
            digest.update("clipshare:vault:conflict:v1\u0000".toByteArray(Charsets.US_ASCII))
            digest.update(uuidBytes(entityId))
            digest.update(uuidBytes(eventId.value))
            val bytes = digest.digest().copyOf(16)
            bytes[6] = ((bytes[6].toInt() and 0x0f) or 0x80).toByte()
            bytes[8] = ((bytes[8].toInt() and 0x3f) or 0x80).toByte()
            val hex = bytes.toLowerHex()
            return buildString {
                append(hex.substring(0, 8), '-')
                append(hex.substring(8, 12), '-')
                append(hex.substring(12, 16), '-')
                append(hex.substring(16, 20), '-')
                append(hex.substring(20))
            }
        }

        private fun uuidBytes(value: String): ByteArray {
            require(Regex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$").matches(value)) {
                "Invalid lowercase UUID."
            }
            return value.replace("-", "").chunked(2).map { it.toInt(16).toByte() }.toByteArray()
        }

        private fun ByteArray.toLowerHex(): String = joinToString("") { byte ->
            val value = byte.toInt() and 0xff
            "${HEX[value ushr 4]}${HEX[value and 0x0f]}"
        }

        private const val HEX = "0123456789abcdef"
    }
}

object TrashRetention {
    val duration: Duration = Duration.ofDays(30)

    fun isExpired(deletedAt: Instant, now: Instant): Boolean =
        !now.isBefore(deletedAt.plus(duration))
}

enum class ReencryptionState {
    PLAN,
    COPYING,
    PAUSED,
    VERIFYING,
    COMMITTING,
    COMPLETE,
    FAILED_ROLLBACK,
}

data class HistoricalReencryptionJob(
    val oldEpoch: Long,
    val newEpoch: Long,
    val totalRecords: Long,
    val processedRecords: Long = 0,
    val state: ReencryptionState = ReencryptionState.PLAN,
) {
    init {
        require(oldEpoch > 0 && newEpoch > oldEpoch) { "Reencryption must move to a newer independent epoch." }
        require(totalRecords >= 0 && processedRecords in 0..totalRecords) { "Invalid reencryption progress." }
    }

    fun start() = transition(ReencryptionState.PLAN, ReencryptionState.COPYING)

    fun copied(records: Long): HistoricalReencryptionJob {
        require(state == ReencryptionState.COPYING && records > 0) { "Copy progress is only valid while copying." }
        return copy(processedRecords = Math.addExact(processedRecords, records)).also {
            require(it.processedRecords <= totalRecords) { "Copy progress exceeds the plan." }
        }
    }

    fun pause() = transition(ReencryptionState.COPYING, ReencryptionState.PAUSED)

    fun resume() = transition(ReencryptionState.PAUSED, ReencryptionState.COPYING)

    fun verify(): HistoricalReencryptionJob {
        require(state == ReencryptionState.COPYING && processedRecords == totalRecords) {
            "All records must be copied first."
        }
        return copy(state = ReencryptionState.VERIFYING)
    }

    fun commit() = transition(ReencryptionState.VERIFYING, ReencryptionState.COMMITTING)

    fun complete(oldEpochReferenceCount: Long): HistoricalReencryptionJob {
        require(state == ReencryptionState.COMMITTING && oldEpochReferenceCount == 0L) {
            "Old epoch references must be zero before completion."
        }
        return copy(state = ReencryptionState.COMPLETE)
    }

    fun failRollback(): HistoricalReencryptionJob {
        require(state != ReencryptionState.COMPLETE && state != ReencryptionState.FAILED_ROLLBACK) {
            "Completed or already failed reencryption jobs are terminal."
        }
        return copy(state = ReencryptionState.FAILED_ROLLBACK)
    }

    private fun transition(from: ReencryptionState, to: ReencryptionState): HistoricalReencryptionJob {
        require(state == from) { "Invalid reencryption state transition." }
        return copy(state = to)
    }
}

private val LOWERCASE_UUID = Regex(
    "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
)
