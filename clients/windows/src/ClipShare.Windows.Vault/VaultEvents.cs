namespace ClipShare.Windows.Vault;

using System.Security.Cryptography;
using System.Text;

public enum EntityKind
{
    Folder,
    Item,
}

public enum VaultOperation
{
    Create,
    Update,
    Move,
    Trash,
    Restore,
    Purge,
}

public sealed record VaultEvent(
    EventId EventId,
    DeviceId SourceDeviceId,
    long SourceSequence,
    long LogicalTime,
    EntityKind EntityKind,
    string EntityId,
    VaultOperation Operation,
    VersionId? BaseVersionId,
    VersionId NewVersionId,
    long KeyEpoch,
    string EncryptedBodyDigest)
{
    public VaultEvent Validate()
    {
        if (SourceSequence <= 0 || LogicalTime <= 0 || KeyEpoch <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SourceSequence), "Event counters and epoch are positive.");
        }

        if (string.IsNullOrEmpty(EncryptedBodyDigest) || EncryptedBodyDigest.Length != 64 || EncryptedBodyDigest.Any(character =>
            !Uri.IsHexDigit(character) || character is >= 'A' and <= 'F'))
        {
            throw new ArgumentException("Event body digest must be lowercase SHA-256.", nameof(EncryptedBodyDigest));
        }

        if (!Enum.IsDefined(EntityKind) || !Enum.IsDefined(Operation))
        {
            throw new ArgumentOutOfRangeException(nameof(EntityKind), "Unknown Vault event kind or operation.");
        }

        _ = VaultUuid.Validate(EventId.Value);
        _ = VaultUuid.Validate(SourceDeviceId.Value);
        _ = VaultUuid.Validate(EntityId);
        _ = VaultUuid.Validate(NewVersionId.Value);
        if (BaseVersionId is { } baseVersionId)
        {
            _ = VaultUuid.Validate(baseVersionId.Value);
        }
        return this;
    }
}

public sealed record EntitySnapshot(VersionId? CurrentVersionId, bool Tombstone, bool MoveWouldCreateCycle = false);

public abstract record EventDecision
{
    private EventDecision()
    {
    }

    public sealed record ApplyLinear : EventDecision;

    public sealed record Duplicate : EventDecision;

    public sealed record CreateConflictCopy(string ConflictEntityId) : EventDecision;

    public sealed record PreserveTombstoneAndConflict(string ConflictEntityId) : EventDecision;

    public sealed record QuarantineCycle : EventDecision;
}

public sealed class EventRejectedException(string message) : InvalidOperationException(message);

public sealed class VaultEventLedger
{
    private readonly IReadOnlySet<long> acceptedEpochs;
    private readonly IReadOnlySet<DeviceId> revokedDevices;
    private readonly Dictionary<EventId, VaultEvent> byEventId = [];
    private readonly Dictionary<(DeviceId Device, long Sequence), EventId> bySourceSequence = [];
    private readonly Dictionary<DeviceId, long> highestSourceSequence = [];

    public VaultEventLedger(IReadOnlySet<long> acceptedEpochs, IReadOnlySet<DeviceId>? revokedDevices = null)
    {
        ArgumentNullException.ThrowIfNull(acceptedEpochs);
        this.acceptedEpochs = acceptedEpochs;
        this.revokedDevices = revokedDevices ?? new HashSet<DeviceId>();
    }

    public EventDecision Accept(VaultEvent vaultEvent, EntitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(vaultEvent);
        ArgumentNullException.ThrowIfNull(snapshot);
        vaultEvent.Validate();
        if (byEventId.TryGetValue(vaultEvent.EventId, out var existing))
        {
            return existing == vaultEvent
                ? new EventDecision.Duplicate()
                : throw new EventRejectedException("An event ID was reused with different content.");
        }

        if (revokedDevices.Contains(vaultEvent.SourceDeviceId))
        {
            throw new EventRejectedException("Events from revoked devices are rejected.");
        }

        if (!acceptedEpochs.Contains(vaultEvent.KeyEpoch))
        {
            throw new EventRejectedException("Events for unavailable epochs are rejected.");
        }

        var sourceKey = (vaultEvent.SourceDeviceId, vaultEvent.SourceSequence);
        if (bySourceSequence.ContainsKey(sourceKey))
        {
            throw new EventRejectedException("A source sequence was reused by a different event.");
        }

        if (highestSourceSequence.TryGetValue(vaultEvent.SourceDeviceId, out long highest) &&
            vaultEvent.SourceSequence <= highest)
        {
            throw new EventRejectedException("An older source sequence cannot be accepted after a newer event.");
        }

        var decision = Decide(vaultEvent, snapshot);
        byEventId[vaultEvent.EventId] = vaultEvent;
        bySourceSequence[sourceKey] = vaultEvent.EventId;
        highestSourceSequence[vaultEvent.SourceDeviceId] = vaultEvent.SourceSequence;
        return decision;
    }

    public static string ConflictEntityId(string entityId, EventId eventId)
    {
        var prefix = Encoding.ASCII.GetBytes("clipshare:vault:conflict:v1\0");
        var entity = Convert.FromHexString(VaultUuid.Validate(entityId).Replace("-", string.Empty, StringComparison.Ordinal));
        var eventBytes = Convert.FromHexString(eventId.Value.Replace("-", string.Empty, StringComparison.Ordinal));
        var digest = SHA256.HashData(prefix.Concat(entity).Concat(eventBytes).ToArray());
        var bytes = digest.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    private static EventDecision Decide(VaultEvent vaultEvent, EntitySnapshot snapshot)
    {
        if (vaultEvent.BaseVersionId == snapshot.CurrentVersionId)
        {
            return new EventDecision.ApplyLinear();
        }

        if (vaultEvent.EntityKind == EntityKind.Folder &&
            vaultEvent.Operation == VaultOperation.Move &&
            snapshot.MoveWouldCreateCycle)
        {
            return new EventDecision.QuarantineCycle();
        }

        var conflictId = ConflictEntityId(vaultEvent.EntityId, vaultEvent.EventId);
        return vaultEvent.Operation == VaultOperation.Trash || snapshot.Tombstone
            ? new EventDecision.PreserveTombstoneAndConflict(conflictId)
            : new EventDecision.CreateConflictCopy(conflictId);
    }
}

public static class TrashRetention
{
    public static TimeSpan Duration { get; } = TimeSpan.FromDays(30);

    public static bool IsExpired(DateTimeOffset deletedAt, DateTimeOffset now) => now >= deletedAt + Duration;
}

public enum ReencryptionState
{
    Plan,
    Copying,
    Paused,
    Verifying,
    Committing,
    Complete,
    FailedRollback,
}

public sealed record HistoricalReencryptionJob
{
    public HistoricalReencryptionJob(
        long oldEpoch,
        long newEpoch,
        long totalRecords,
        long processedRecords = 0,
        ReencryptionState state = ReencryptionState.Plan)
    {
        if (oldEpoch <= 0 || newEpoch <= oldEpoch || totalRecords < 0 || processedRecords < 0 || processedRecords > totalRecords)
        {
            throw new ArgumentOutOfRangeException(nameof(oldEpoch), "Invalid historical reencryption plan.");
        }

        OldEpoch = oldEpoch;
        NewEpoch = newEpoch;
        TotalRecords = totalRecords;
        ProcessedRecords = processedRecords;
        State = state;
    }

    public long OldEpoch { get; init; }

    public long NewEpoch { get; init; }

    public long TotalRecords { get; init; }

    public long ProcessedRecords { get; init; }

    public ReencryptionState State { get; init; }

    public HistoricalReencryptionJob Start() => Transition(ReencryptionState.Plan, ReencryptionState.Copying);

    public HistoricalReencryptionJob Copied(long records)
    {
        if (State != ReencryptionState.Copying || records <= 0 || ProcessedRecords > TotalRecords - records)
        {
            throw new InvalidOperationException("Invalid reencryption copy progress.");
        }

        return this with { ProcessedRecords = ProcessedRecords + records };
    }

    public HistoricalReencryptionJob Pause() => Transition(ReencryptionState.Copying, ReencryptionState.Paused);

    public HistoricalReencryptionJob Resume() => Transition(ReencryptionState.Paused, ReencryptionState.Copying);

    public HistoricalReencryptionJob Verify()
    {
        if (State != ReencryptionState.Copying || ProcessedRecords != TotalRecords)
        {
            throw new InvalidOperationException("All records must be copied before verification.");
        }

        return this with { State = ReencryptionState.Verifying };
    }

    public HistoricalReencryptionJob Commit() => Transition(ReencryptionState.Verifying, ReencryptionState.Committing);

    public HistoricalReencryptionJob Complete(long oldEpochReferenceCount)
    {
        if (State != ReencryptionState.Committing || oldEpochReferenceCount != 0)
        {
            throw new InvalidOperationException("Old epoch references must be zero before completion.");
        }

        return this with { State = ReencryptionState.Complete };
    }

    public HistoricalReencryptionJob FailRollback() =>
        State is not (ReencryptionState.Complete or ReencryptionState.FailedRollback)
            ? this with { State = ReencryptionState.FailedRollback }
            : throw new InvalidOperationException("Completed or already failed reencryption jobs are terminal.");

    private HistoricalReencryptionJob Transition(ReencryptionState from, ReencryptionState to) =>
        State == from
            ? this with { State = to }
            : throw new InvalidOperationException("Invalid reencryption state transition.");
}
