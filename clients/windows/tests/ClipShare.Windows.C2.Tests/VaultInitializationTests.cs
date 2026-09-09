namespace ClipShare.Windows.C2.Tests;

using ClipShare.Windows.Vault;

public sealed class VaultInitializationTests
{
    [Fact]
    public async Task CoordinatorCreatesOnlyFromAbsentAndReachesReady()
    {
        var operations = new FakeOperations();
        var result = await new VaultInitializationCoordinator(operations)
            .OpenOrCreateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, operations.CreateCalls);
        Assert.Equal(InitializationPhase.Ready, result.Wrapper.Phase);
        Assert.Equal(InitializationPhase.Ready, result.Database.Phase);
        Assert.True(result.Database.ContainsCiphertext);
    }

    [Fact]
    public async Task CoordinatorResumesWrapperOnlyWithoutGeneratingReplacementSecret()
    {
        var operations = new FakeOperations { Wrapper = Wrapper(InitializationPhase.Staged) };
        _ = await new VaultInitializationCoordinator(operations)
            .OpenOrCreateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, operations.CreateCalls);
        Assert.Equal(1, operations.StageDatabaseCalls);
    }

    [Fact]
    public async Task CoordinatorFailsClosedForDatabaseOnlyWithoutCreatingSecret()
    {
        var operations = new FakeOperations { Database = Database() };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new VaultInitializationCoordinator(operations).OpenOrCreateAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, operations.CreateCalls);
    }

    [Fact]
    public async Task CoordinatorCoversEveryMonotonicResumePathAndRejectsNoProgress()
    {
        Assert.Throws<ArgumentNullException>(() => new VaultInitializationCoordinator(null!));

        var databasePromotion = new FakeOperations
        {
            Wrapper = Wrapper(InitializationPhase.Ready),
            Database = Database(InitializationPhase.Staged),
        };
        var promotedDatabase = await new VaultInitializationCoordinator(databasePromotion)
            .OpenOrCreateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(InitializationPhase.Ready, promotedDatabase.Database.Phase);

        var stagedResume = new FakeOperations
        {
            Wrapper = Wrapper(InitializationPhase.Staged),
            Database = Database(InitializationPhase.Staged),
        };
        var resumed = await new VaultInitializationCoordinator(stagedResume)
            .OpenOrCreateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(InitializationPhase.Ready, resumed.Wrapper.Phase);

        var alreadyReady = new FakeOperations
        {
            Wrapper = Wrapper(InitializationPhase.Ready),
            Database = Database(),
        };
        _ = await new VaultInitializationCoordinator(alreadyReady)
            .OpenOrCreateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, alreadyReady.CreateCalls);

        var noProgress = new FakeOperations { IgnoreMutations = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new VaultInitializationCoordinator(noProgress).OpenOrCreateAsync(TestContext.Current.CancellationToken));
        Assert.Equal(6, noProgress.CreateCalls);
    }

    [Fact]
    public void OnlyBothAbsentCanCreateNewSecret()
    {
        Assert.IsType<InitializationAction.InitializeFreshVault>(VaultInitializationStateMachine.Decide(null, null));
        Assert.IsType<InitializationAction.FailClosed>(VaultInitializationStateMachine.Decide(null, Database()));
        Assert.IsType<InitializationAction.FailClosed>(
            VaultInitializationStateMachine.Decide(Wrapper(InitializationPhase.Ready), null));
    }

    [Fact]
    public void StagedStatesResumeExistingMaterialAndPromoteMonotonically()
    {
        Assert.IsType<InitializationAction.ResumeFromStagedWrapper>(
            VaultInitializationStateMachine.Decide(Wrapper(InitializationPhase.Staged), null));
        Assert.IsType<InitializationAction.ResumeStagedDatabase>(
            VaultInitializationStateMachine.Decide(Wrapper(InitializationPhase.Staged), Database(InitializationPhase.Staged)));
        Assert.IsType<InitializationAction.PromoteWrapperReady>(
            VaultInitializationStateMachine.Decide(Wrapper(InitializationPhase.Staged), Database()));
    }

    [Fact]
    public void IdentityOrDigestMismatchFailsClosed()
    {
        Assert.IsType<InitializationAction.FailClosed>(VaultInitializationStateMachine.Decide(
            Wrapper(InitializationPhase.Ready),
            Database() with { WrapperDigest = new string('1', 64), ContainsCiphertext = true }));
    }

    [Fact]
    public void StateMachineCoversPopulationPromotionReadyAndEveryIdentityMismatch()
    {
        WrapperObservation stagedWrapper = Wrapper(InitializationPhase.Staged);
        DatabaseObservation stagedEmpty = Database(InitializationPhase.Staged) with { ContainsCiphertext = false };
        Assert.IsType<InitializationAction.PopulateStagedDatabase>(
            VaultInitializationStateMachine.Decide(stagedWrapper, stagedEmpty));
        Assert.IsType<InitializationAction.PromoteDatabaseReady>(VaultInitializationStateMachine.Decide(
            Wrapper(InitializationPhase.Ready),
            Database(InitializationPhase.Staged)));
        Assert.IsType<InitializationAction.OpenReady>(VaultInitializationStateMachine.Decide(
            Wrapper(InitializationPhase.Ready),
            Database()));
        Assert.IsType<InitializationAction.FailClosed>(VaultInitializationStateMachine.Decide(
            Wrapper(InitializationPhase.Ready),
            Database() with { ContainsCiphertext = false }));

        Assert.IsType<InitializationAction.FailClosed>(VaultInitializationStateMachine.Decide(
            stagedWrapper,
            stagedEmpty with { VaultId = VaultId.Parse("11111111-2222-4333-8444-555555555555") }));
        Assert.IsType<InitializationAction.FailClosed>(VaultInitializationStateMachine.Decide(
            stagedWrapper,
            stagedEmpty with { InitializationId = "90000000-0000-4000-8000-000000000002" }));
        Assert.IsType<InitializationAction.FailClosed>(VaultInitializationStateMachine.Decide(
            stagedWrapper,
            stagedEmpty with { KeyEpoch = 2 }));

        InitializationPhase unknown = (InitializationPhase)99;
        Assert.Throws<InvalidOperationException>(() => VaultInitializationStateMachine.Decide(
            Wrapper(unknown),
            Database(unknown)));
    }

    private static WrapperObservation Wrapper(InitializationPhase phase) => new(
        VaultId.Parse("00112233-4455-4677-8899-aabbccddeeff"),
        "90000000-0000-4000-8000-000000000001",
        1,
        new string('0', 64),
        phase);

    private static DatabaseObservation Database(InitializationPhase phase = InitializationPhase.Ready) => new(
        VaultId.Parse("00112233-4455-4677-8899-aabbccddeeff"),
        "90000000-0000-4000-8000-000000000001",
        1,
        new string('0', 64),
        phase,
        true);

    private sealed class FakeOperations : IVaultInitializationOperations
    {
        public bool IgnoreMutations { get; init; }

        public WrapperObservation? Wrapper { get; set; }

        public DatabaseObservation? Database { get; set; }

        public int CreateCalls { get; private set; }

        public int StageDatabaseCalls { get; private set; }

        public Task<WrapperObservation?> ObserveWrapperAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Wrapper);

        public Task<DatabaseObservation?> ObserveDatabaseAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Database);

        public Task CreateStagedWrapperAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Null(Wrapper);
            Assert.Null(Database);
            CreateCalls++;
            if (!IgnoreMutations)
            {
                Wrapper = VaultInitializationTests.Wrapper(InitializationPhase.Staged);
            }

            return Task.CompletedTask;
        }

        public Task StageDatabaseFromWrapperAsync(
            WrapperObservation wrapper,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StageDatabaseCalls++;
            Database = VaultInitializationTests.Database(InitializationPhase.Staged) with { ContainsCiphertext = false };
            return Task.CompletedTask;
        }

        public Task PopulateStagedDatabaseAsync(
            WrapperObservation wrapper,
            DatabaseObservation database,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Database = database with { ContainsCiphertext = true };
            return Task.CompletedTask;
        }

        public Task VerifyExistingAsync(
            WrapperObservation wrapper,
            DatabaseObservation database,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(wrapper.Digest, database.WrapperDigest);
            Assert.True(database.ContainsCiphertext);
            return Task.CompletedTask;
        }

        public Task PromoteDatabaseReadyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Database = Assert.IsType<DatabaseObservation>(Database) with { Phase = InitializationPhase.Ready };
            return Task.CompletedTask;
        }

        public Task PromoteWrapperReadyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Wrapper = Assert.IsType<WrapperObservation>(Wrapper) with { Phase = InitializationPhase.Ready };
            return Task.CompletedTask;
        }
    }
}
