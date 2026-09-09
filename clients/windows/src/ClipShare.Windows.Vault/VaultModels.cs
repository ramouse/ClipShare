namespace ClipShare.Windows.Vault;

using System.Globalization;
using System.Text.RegularExpressions;

internal static partial class VaultUuid
{
    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    internal static string Validate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Pattern().IsMatch(value))
        {
            throw new ArgumentException("Vault identifiers must be lowercase RFC 4122 UUIDs.", nameof(value));
        }

        return value;
    }

    internal static string TestValue(int prefix, int suffix) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{prefix:x8}-0000-4000-8000-{suffix:000000000000}");
}

public readonly record struct VaultId
{
    private VaultId(string value) => Value = value;

    public string Value { get; }

    public static VaultId Parse(string value) => new(VaultUuid.Validate(value));

    public override string ToString() => Value;
}

public readonly record struct FolderId
{
    private FolderId(string value) => Value = value;

    public string Value { get; }

    public static FolderId Parse(string value) => new(VaultUuid.Validate(value));

    public override string ToString() => Value;
}

public readonly record struct ItemId
{
    private ItemId(string value) => Value = value;

    public string Value { get; }

    public static ItemId Parse(string value) => new(VaultUuid.Validate(value));

    public override string ToString() => Value;
}

public readonly record struct VersionId
{
    private VersionId(string value) => Value = value;

    public string Value { get; }

    public static VersionId Parse(string value) => new(VaultUuid.Validate(value));

    public override string ToString() => Value;
}

public readonly record struct DeviceId
{
    private DeviceId(string value) => Value = value;

    public string Value { get; }

    public static DeviceId Parse(string value) => new(VaultUuid.Validate(value));

    public override string ToString() => Value;
}

public readonly record struct EventId
{
    private EventId(string value) => Value = value;

    public string Value { get; }

    public static EventId Parse(string value) => new(VaultUuid.Validate(value));

    public override string ToString() => Value;
}

public enum VaultContentType
{
    Text,
    Url,
    File,
}

public enum SyncPolicy
{
    LocalOnly,
    SelectedDevices,
    AllPairedDevices,
}

public sealed record SyncPolicySetting
{
    public SyncPolicySetting(SyncPolicy? policyOverride, IReadOnlySet<DeviceId>? selectedDevices = null)
    {
        Override = policyOverride;
        SelectedDevices = selectedDevices ?? new HashSet<DeviceId>();
        if (Override != SyncPolicy.SelectedDevices && SelectedDevices.Count != 0)
        {
            throw new ArgumentException("Selected devices are only valid for SelectedDevices.", nameof(selectedDevices));
        }

        if (Override == SyncPolicy.SelectedDevices && SelectedDevices.Count == 0)
        {
            throw new ArgumentException("SelectedDevices requires at least one device.", nameof(selectedDevices));
        }
    }

    public SyncPolicy? Override { get; }

    public IReadOnlySet<DeviceId> SelectedDevices { get; }
}

public sealed record VaultState(VaultId Id, int FormatVersion, long CurrentWriteEpoch, bool Initialized)
{
    public VaultState Validate()
    {
        if (FormatVersion != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(FormatVersion), "Unsupported Vault format version.");
        }

        if (CurrentWriteEpoch <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(CurrentWriteEpoch), "The write epoch must be positive.");
        }

        return this;
    }
}

public sealed record VaultFolder(
    FolderId Id,
    FolderId? ParentId,
    string Name,
    string? Description,
    long SortOrder,
    SyncPolicySetting SyncPolicy,
    VersionId CurrentVersionId,
    DateTimeOffset? DeletedAt);

public sealed record VaultItem(
    ItemId Id,
    FolderId FolderId,
    VaultContentType ContentType,
    string Title,
    ReadOnlyMemory<byte> Payload,
    SyncPolicySetting SyncPolicy,
    VersionId CurrentVersionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DeletedAt);
