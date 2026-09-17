namespace ClipShare.Windows.Features.Vault;

public interface IVaultWorkspace : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<VaultSnapshot> LoadAsync(
        VaultSection section,
        string? query,
        CancellationToken cancellationToken);

    Task CreateFolderAsync(CreateVaultFolderCommand command, CancellationToken cancellationToken);

    Task RenameFolderAsync(RenameVaultFolderCommand command, CancellationToken cancellationToken);

    Task SaveTextAsync(SaveVaultTextCommand command, CancellationToken cancellationToken);

    Task ImportFileAsync(ImportVaultFileCommand command, CancellationToken cancellationToken);

    Task ExportFileAsync(string itemId, string userSelectedPath, CancellationToken cancellationToken);

    Task UpdateTextAsync(UpdateVaultTextCommand command, CancellationToken cancellationToken);

    Task MoveAsync(
        IReadOnlySet<VaultEntitySelection> selection,
        string targetFolderId,
        CancellationToken cancellationToken);

    Task MoveToTrashAsync(
        IReadOnlySet<VaultEntitySelection> selection,
        bool includeFolderContents,
        CancellationToken cancellationToken);

    Task RestoreAsync(
        IReadOnlySet<VaultEntitySelection> selection,
        CancellationToken cancellationToken);

    Task PurgeAsync(
        IReadOnlySet<VaultEntitySelection> selection,
        CancellationToken cancellationToken);

    Task SetPolicyAsync(
        IReadOnlySet<VaultEntitySelection> selection,
        VaultPolicyCommand policy,
        CancellationToken cancellationToken);

    void ClearSensitiveState();
}
