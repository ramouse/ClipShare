namespace ClipShare.Windows.Features.Vault;

using ClipShare.Windows.Vault;

public enum VaultSection
{
    Inbox,
    All,
    Folders,
    Recent,
    Synced,
    LocalOnly,
    Trash,
    Devices,
}

public enum VaultEntityKind
{
    Folder,
    Item,
}

public readonly record struct VaultEntitySelection(VaultEntityKind Kind, string Id);

public sealed record VaultFolderView(
    string Id,
    string? ParentId,
    string Name,
    string? Description,
    long SortOrder,
    SyncPolicySetting Policy,
    SyncPolicy EffectivePolicy,
    bool Deleted);

public sealed record VaultItemView(
    string Id,
    string FolderId,
    VaultContentType ContentType,
    string Title,
    string? Text,
    string? FileName,
    long SizeBytes,
    SyncPolicySetting Policy,
    SyncPolicy EffectivePolicy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool Deleted);

public sealed record VaultDeviceView(string Id, string DisplayName, bool Revoked);

public sealed record VaultSnapshot(
    IReadOnlyList<VaultFolderView> Folders,
    IReadOnlyList<VaultItemView> Items,
    IReadOnlyList<VaultDeviceView> Devices,
    bool UsedSlowSearch,
    string? Notice);

public sealed record VaultFeatureState(
    VaultSection Section,
    VaultSnapshot Snapshot,
    IReadOnlySet<VaultEntitySelection> Selection,
    bool IsBusy,
    string? Status,
    bool IsFailure)
{
    public static VaultFeatureState Empty { get; } = new(
        VaultSection.Inbox,
        new VaultSnapshot([], [], [], false, null),
        new HashSet<VaultEntitySelection>(),
        false,
        null,
        false);
}

public sealed record CreateVaultFolderCommand(
    string Name,
    string? ParentId,
    string? Description = null);

public sealed record RenameVaultFolderCommand(string FolderId, string Name, string? Description = null);

public sealed record SaveVaultTextCommand(
    string FolderId,
    string Title,
    string Text,
    VaultContentType ContentType);

public sealed record ImportVaultFileCommand(
    string FolderId,
    string Title,
    string UserSelectedPath);

public sealed record UpdateVaultTextCommand(string ItemId, string Title, string Text);

public sealed record VaultPolicyCommand(
    SyncPolicy Policy,
    IReadOnlySet<string> SelectedDeviceIds,
    bool ClearDescendantOverrides);
