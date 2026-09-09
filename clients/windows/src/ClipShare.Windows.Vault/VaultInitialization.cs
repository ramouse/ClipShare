namespace ClipShare.Windows.Vault;

public enum InitializationPhase
{
    Staged,
    Ready,
}

public sealed record WrapperObservation(
    VaultId VaultId,
    string InitializationId,
    long KeyEpoch,
    string Digest,
    InitializationPhase Phase);

public sealed record DatabaseObservation(
    VaultId VaultId,
    string InitializationId,
    long KeyEpoch,
    string WrapperDigest,
    InitializationPhase Phase,
    bool ContainsCiphertext);

public abstract record InitializationAction
{
    private InitializationAction()
    {
    }

    public sealed record InitializeFreshVault : InitializationAction;

    public sealed record ResumeFromStagedWrapper : InitializationAction;

    public sealed record ResumeStagedDatabase : InitializationAction;

    public sealed record PopulateStagedDatabase : InitializationAction;

    public sealed record PromoteDatabaseReady : InitializationAction;

    public sealed record PromoteWrapperReady : InitializationAction;

    public sealed record OpenReady : InitializationAction;

    public sealed record FailClosed(string Reason) : InitializationAction;
}

public static class VaultInitializationStateMachine
{
    public static InitializationAction Decide(WrapperObservation? wrapper, DatabaseObservation? database)
    {
        if (wrapper is null && database is null)
        {
            return new InitializationAction.InitializeFreshVault();
        }

        if (wrapper is null)
        {
            return new InitializationAction.FailClosed("Database exists without its platform wrapper.");
        }

        if (database is null)
        {
            return wrapper.Phase == InitializationPhase.Staged
                ? new InitializationAction.ResumeFromStagedWrapper()
                : new InitializationAction.FailClosed("A READY wrapper exists without its database.");
        }

        if (!Matches(wrapper, database))
        {
            return new InitializationAction.FailClosed("Wrapper and database identity, epoch, or digest mismatch.");
        }

        if (database.Phase == InitializationPhase.Ready && !database.ContainsCiphertext)
        {
            return new InitializationAction.FailClosed("A READY Vault database has no encrypted initialization records.");
        }

        return (wrapper.Phase, database.Phase) switch
        {
            (InitializationPhase.Staged, InitializationPhase.Staged) => database.ContainsCiphertext
                ? new InitializationAction.ResumeStagedDatabase()
                : new InitializationAction.PopulateStagedDatabase(),
            (InitializationPhase.Ready, InitializationPhase.Staged) => new InitializationAction.PromoteDatabaseReady(),
            (InitializationPhase.Staged, InitializationPhase.Ready) => new InitializationAction.PromoteWrapperReady(),
            (InitializationPhase.Ready, InitializationPhase.Ready) => new InitializationAction.OpenReady(),
            _ => throw new InvalidOperationException("Unknown initialization state."),
        };
    }

    private static bool Matches(WrapperObservation wrapper, DatabaseObservation database) =>
        wrapper.VaultId == database.VaultId &&
        string.Equals(wrapper.InitializationId, database.InitializationId, StringComparison.Ordinal) &&
        wrapper.KeyEpoch == database.KeyEpoch &&
        string.Equals(wrapper.Digest, database.WrapperDigest, StringComparison.Ordinal);
}

public interface IVaultInitializationOperations
{
    Task<WrapperObservation?> ObserveWrapperAsync(CancellationToken cancellationToken);

    Task<DatabaseObservation?> ObserveDatabaseAsync(CancellationToken cancellationToken);

    Task CreateStagedWrapperAsync(CancellationToken cancellationToken);

    Task StageDatabaseFromWrapperAsync(WrapperObservation wrapper, CancellationToken cancellationToken);

    Task PopulateStagedDatabaseAsync(
        WrapperObservation wrapper,
        DatabaseObservation database,
        CancellationToken cancellationToken);

    Task VerifyExistingAsync(
        WrapperObservation wrapper,
        DatabaseObservation database,
        CancellationToken cancellationToken);

    Task PromoteDatabaseReadyAsync(CancellationToken cancellationToken);

    Task PromoteWrapperReadyAsync(CancellationToken cancellationToken);
}

public sealed class VaultInitializationCoordinator
{
    private const int MaximumTransitions = 6;
    private readonly IVaultInitializationOperations operations;

    public VaultInitializationCoordinator(IVaultInitializationOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        this.operations = operations;
    }

    public async Task<(WrapperObservation Wrapper, DatabaseObservation Database)> OpenOrCreateAsync(
        CancellationToken cancellationToken = default)
    {
        for (var transition = 0; transition < MaximumTransitions; transition++)
        {
            WrapperObservation? wrapper = await operations.ObserveWrapperAsync(cancellationToken).ConfigureAwait(false);
            DatabaseObservation? database = await operations.ObserveDatabaseAsync(cancellationToken).ConfigureAwait(false);
            InitializationAction action = VaultInitializationStateMachine.Decide(wrapper, database);
            switch (action)
            {
                case InitializationAction.InitializeFreshVault:
                    await operations.CreateStagedWrapperAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case InitializationAction.ResumeFromStagedWrapper:
                    await operations.StageDatabaseFromWrapperAsync(wrapper!, cancellationToken).ConfigureAwait(false);
                    break;
                case InitializationAction.ResumeStagedDatabase:
                case InitializationAction.PromoteDatabaseReady:
                    await operations.VerifyExistingAsync(wrapper!, database!, cancellationToken).ConfigureAwait(false);
                    await operations.PromoteDatabaseReadyAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case InitializationAction.PopulateStagedDatabase:
                    await operations.PopulateStagedDatabaseAsync(wrapper!, database!, cancellationToken).ConfigureAwait(false);
                    break;
                case InitializationAction.PromoteWrapperReady:
                    await operations.VerifyExistingAsync(wrapper!, database!, cancellationToken).ConfigureAwait(false);
                    await operations.PromoteWrapperReadyAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case InitializationAction.OpenReady:
                    await operations.VerifyExistingAsync(wrapper!, database!, cancellationToken).ConfigureAwait(false);
                    return (wrapper!, database!);
                case InitializationAction.FailClosed failClosed:
                    throw new InvalidDataException(failClosed.Reason);
                default:
                    throw new InvalidOperationException("Unknown Vault initialization action.");
            }
        }

        throw new InvalidOperationException("Vault initialization did not make monotonic progress.");
    }
}
