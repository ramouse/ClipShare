namespace ClipShare.Windows.C2.Tests;

using ClipShare.Windows.Vault;

public sealed class VaultEventsTests
{
    [Fact]
    public void LinearEventsApplyAndExactReplaysAreIdempotent()
    {
        var vaultEvent = Event(1, Version(1), Version(2));
        var ledger = new VaultEventLedger(new HashSet<long> { 1 });
        Assert.IsType<EventDecision.ApplyLinear>(ledger.Accept(vaultEvent, new EntitySnapshot(Version(1), false)));
        Assert.IsType<EventDecision.Duplicate>(ledger.Accept(vaultEvent, new EntitySnapshot(Version(2), false)));
    }

    [Fact]
    public void SourceSequenceReuseAndRevokedDevicesFailClosed()
    {
        var source = Device(1);
        var first = Event(1, source: source);
        var ledger = new VaultEventLedger(new HashSet<long> { 1 });
        _ = ledger.Accept(first, new EntitySnapshot(null, false));
        Assert.Throws<EventRejectedException>(() =>
            ledger.Accept(Event(2, source: source, sequence: 1), new EntitySnapshot(null, false)));
        Assert.Throws<EventRejectedException>(() =>
            new VaultEventLedger(new HashSet<long> { 1 }, new HashSet<DeviceId> { source })
                .Accept(first, new EntitySnapshot(null, false)));
    }

    [Fact]
    public void EventsAndLedgerRejectMalformedUnavailableAndSubstitutedInputs()
    {
        VaultEvent valid = Event(1);
        Assert.Same(valid, valid.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (valid with { SourceSequence = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (valid with { LogicalTime = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (valid with { KeyEpoch = 0 }).Validate());
        Assert.Throws<ArgumentException>(() => (valid with { EncryptedBodyDigest = new string('0', 63) }).Validate());
        Assert.Throws<ArgumentException>(() => (valid with { EncryptedBodyDigest = new string('A', 64) }).Validate());
        Assert.Throws<ArgumentException>(() => (valid with { EncryptedBodyDigest = new string('g', 64) }).Validate());
        Assert.Throws<ArgumentException>(() => (valid with { EncryptedBodyDigest = null! }).Validate());
        Assert.Throws<ArgumentException>(() => (valid with { EntityId = "not-a-uuid" }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (valid with { EntityKind = (EntityKind)999 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (valid with { Operation = (VaultOperation)999 }).Validate());
        Assert.Throws<ArgumentNullException>(() => (valid with { EventId = default }).Validate());
        Assert.Throws<ArgumentNullException>(() => (valid with { SourceDeviceId = default }).Validate());
        Assert.Throws<ArgumentNullException>(() => (valid with { NewVersionId = default }).Validate());
        Assert.Throws<ArgumentNullException>(() => (valid with { BaseVersionId = default(VersionId) }).Validate());

        Assert.Throws<ArgumentNullException>(() => new VaultEventLedger(null!));
        Assert.Throws<ArgumentNullException>(() => new VaultEventLedger(new HashSet<long> { 1 }).Accept(null!, new EntitySnapshot(null, false)));
        Assert.Throws<ArgumentNullException>(() => new VaultEventLedger(new HashSet<long> { 1 }).Accept(valid, null!));

        var ledger = new VaultEventLedger(new HashSet<long> { 1 });
        _ = ledger.Accept(valid, new EntitySnapshot(null, false));
        Assert.Throws<EventRejectedException>(() =>
            ledger.Accept(valid with { LogicalTime = 2 }, new EntitySnapshot(null, false)));
        Assert.Throws<EventRejectedException>(() =>
            new VaultEventLedger(new HashSet<long> { 2 }).Accept(valid, new EntitySnapshot(null, false)));

        var orderedLedger = new VaultEventLedger(new HashSet<long> { 1 });
        _ = orderedLedger.Accept(Event(3, sequence: 3), new EntitySnapshot(null, false));
        Assert.Throws<EventRejectedException>(() =>
            orderedLedger.Accept(Event(4, sequence: 2), new EntitySnapshot(null, false)));
    }

    [Fact]
    public void ConflictsPreserveEditsTombstonesAndCycles()
    {
        var concurrent = Event(7, Version(1), Version(3));
        var conflict = Assert.IsType<EventDecision.CreateConflictCopy>(
            new VaultEventLedger(new HashSet<long> { 1 }).Accept(concurrent, new EntitySnapshot(Version(2), false)));
        Assert.Equal(VaultEventLedger.ConflictEntityId(concurrent.EntityId, concurrent.EventId), conflict.ConflictEntityId);
        Assert.Equal('8', conflict.ConflictEntityId[14]);

        Assert.IsType<EventDecision.PreserveTombstoneAndConflict>(
            new VaultEventLedger(new HashSet<long> { 1 }).Accept(Event(8, Version(1)), new EntitySnapshot(Version(2), true)));
        Assert.IsType<EventDecision.QuarantineCycle>(
            new VaultEventLedger(new HashSet<long> { 1 }).Accept(
                Event(9, Version(1), kind: EntityKind.Folder, operation: VaultOperation.Move),
                new EntitySnapshot(Version(2), false, true)));
        Assert.IsType<EventDecision.PreserveTombstoneAndConflict>(
            new VaultEventLedger(new HashSet<long> { 1 }).Accept(
                Event(10, Version(1), operation: VaultOperation.Trash),
                new EntitySnapshot(Version(2), false)));
        Assert.IsType<EventDecision.CreateConflictCopy>(
            new VaultEventLedger(new HashSet<long> { 1 }).Accept(
                Event(11, Version(1), kind: EntityKind.Folder, operation: VaultOperation.Update),
                new EntitySnapshot(Version(2), false)));
        Assert.IsType<EventDecision.CreateConflictCopy>(
            new VaultEventLedger(new HashSet<long> { 1 }).Accept(
                Event(12, Version(1), kind: EntityKind.Folder, operation: VaultOperation.Move),
                new EntitySnapshot(Version(2), false, false)));
    }

    [Fact]
    public void TrashExpiresAtExactThirtyDayBoundary()
    {
        var deleted = DateTimeOffset.Parse("2026-09-02T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        Assert.False(TrashRetention.IsExpired(deleted, deleted + TrashRetention.Duration - TimeSpan.FromTicks(1)));
        Assert.True(TrashRetention.IsExpired(deleted, deleted + TrashRetention.Duration));
    }

    [Fact]
    public void HistoricalReencryptionCannotRemoveReferencedOldEpoch()
    {
        var job = new HistoricalReencryptionJob(1, 2, 2)
            .Start()
            .Copied(1)
            .Pause()
            .Resume()
            .Copied(1)
            .Verify()
            .Commit();
        Assert.Throws<InvalidOperationException>(() => job.Complete(1));
        Assert.Equal(ReencryptionState.Complete, job.Complete(0).State);
    }

    [Fact]
    public void HistoricalReencryptionRejectsInvalidPlansProgressAndTransitions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HistoricalReencryptionJob(0, 2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HistoricalReencryptionJob(1, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HistoricalReencryptionJob(1, 2, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HistoricalReencryptionJob(1, 2, 1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HistoricalReencryptionJob(1, 2, 1, 2));

        var plan = new HistoricalReencryptionJob(1, 2, 2);
        Assert.Throws<InvalidOperationException>(() => plan.Copied(1));
        Assert.Throws<InvalidOperationException>(() => plan.Pause());
        Assert.Throws<InvalidOperationException>(() => plan.Resume());
        Assert.Throws<InvalidOperationException>(() => plan.Verify());
        Assert.Throws<InvalidOperationException>(() => plan.Commit());
        Assert.Throws<InvalidOperationException>(() => plan.Complete(0));

        HistoricalReencryptionJob copying = plan.Start();
        Assert.Throws<InvalidOperationException>(() => copying.Start());
        Assert.Throws<InvalidOperationException>(() => copying.Copied(0));
        Assert.Throws<InvalidOperationException>(() => copying.Copied(3));
        Assert.Throws<InvalidOperationException>(() => copying.Verify());
        HistoricalReencryptionJob failed = copying.FailRollback();
        Assert.Equal(ReencryptionState.FailedRollback, failed.State);
        Assert.Throws<InvalidOperationException>(() => failed.FailRollback());

        HistoricalReencryptionJob complete = copying.Copied(2).Verify().Commit().Complete(0);
        Assert.Throws<InvalidOperationException>(() => complete.FailRollback());
    }

    [Fact]
    public void NewEpochWrappersTargetExactlyNonRevokedDevices()
    {
        DeviceId active = Device(1);
        DeviceId revoked = Device(2);
        EpochDistributionPlan plan = EpochDistributionPlan.Create(
            1,
            new[] { new PairedDeviceState(active, false), new PairedDeviceState(revoked, true) });
        Assert.Equal(2, plan.NewEpoch);
        Assert.Equal(new HashSet<DeviceId> { active }, plan.Recipients);
        plan.ValidateGeneratedWrappers(new HashSet<DeviceId> { active });
        Assert.Throws<InvalidOperationException>(() =>
            plan.ValidateGeneratedWrappers(new HashSet<DeviceId> { active, revoked }));
    }

    [Fact]
    public void EpochDistributionRejectsExhaustionDuplicatesAndNullRecipientSets()
    {
        DeviceId active = Device(1);
        Assert.Throws<ArgumentNullException>(() => EpochDistributionPlan.Create(1, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => EpochDistributionPlan.Create(0, []));
        Assert.Throws<InvalidOperationException>(() => EpochDistributionPlan.Create(long.MaxValue, []));
        Assert.Throws<ArgumentException>(() => EpochDistributionPlan.Create(
            1,
            new[] { new PairedDeviceState(active, false), new PairedDeviceState(active, true) }));

        EpochDistributionPlan plan = EpochDistributionPlan.Create(
            1,
            new[] { new PairedDeviceState(active, true) });
        Assert.Empty(plan.Recipients);
        Assert.Throws<ArgumentNullException>(() => plan.ValidateGeneratedWrappers(null!));
    }

    private static VaultEvent Event(
        int value,
        VersionId? baseVersion = null,
        VersionId? newVersion = null,
        DeviceId? source = null,
        long? sequence = null,
        EntityKind kind = EntityKind.Item,
        VaultOperation operation = VaultOperation.Update) => new VaultEvent(
            EventId.Parse($"20000000-0000-4000-8000-{value:000000000000}"),
            source ?? Device(1),
            sequence ?? value,
            value,
            kind,
            "30000000-0000-4000-8000-000000000001",
            operation,
            baseVersion,
            newVersion ?? Version(value + 10),
            1,
            new string('0', 64));

    private static DeviceId Device(int value) =>
        DeviceId.Parse($"10000000-0000-4000-8000-{value:000000000000}");

    private static VersionId Version(int value) =>
        VersionId.Parse($"40000000-0000-4000-8000-{value:000000000000}");
}
