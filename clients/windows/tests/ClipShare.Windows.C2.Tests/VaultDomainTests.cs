namespace ClipShare.Windows.C2.Tests;

using ClipShare.Windows.Vault;

public sealed class VaultDomainTests
{
    [Fact]
    public void IdentifiersRejectUppercaseAndMalformedValues()
    {
        Assert.Throws<ArgumentException>(() => FolderId.Parse("AAAAAAAA-bbbb-4ccc-8ddd-eeeeeeeeeeee"));
        Assert.Throws<ArgumentException>(() => FolderId.Parse("not-a-uuid"));
        Assert.Throws<ArgumentNullException>(() => VaultId.Parse(null!));
    }

    [Fact]
    public void IdentifiersStateAndItemModelsRoundTripAndValidate()
    {
        const string value = "00112233-4455-4677-8899-aabbccddeeff";
        Assert.Equal(value, VaultId.Parse(value).ToString());
        Assert.Equal(value, FolderId.Parse(value).ToString());
        Assert.Equal(value, ItemId.Parse(value).ToString());
        Assert.Equal(value, VersionId.Parse(value).ToString());
        Assert.Equal(value, DeviceId.Parse(value).ToString());
        Assert.Equal(value, EventId.Parse(value).ToString());

        VaultState state = new(VaultId.Parse(value), 1, 1, true);
        Assert.Same(state, state.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (state with { FormatVersion = 2 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (state with { CurrentWriteEpoch = 0 }).Validate());

        DeviceId selectedDevice = DeviceId.Parse("11111111-2222-4333-8444-555555555555");
        var selected = new SyncPolicySetting(
            SyncPolicy.SelectedDevices,
            new HashSet<DeviceId> { selectedDevice });
        Assert.Equal(new HashSet<DeviceId> { selectedDevice }, selected.SelectedDevices);

        VaultFolder folder = Folder(1);
        VaultItem inheritedItem = new(
            ItemId.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
            folder.Id,
            VaultContentType.Text,
            "title",
            "payload"u8.ToArray(),
            new SyncPolicySetting(null),
            Version(2),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            null);
        var tree = new VaultTree([folder]);
        Assert.Equal(SyncPolicy.LocalOnly, tree.EffectivePolicy(folder.Id).Override);
        Assert.Equal(SyncPolicy.LocalOnly, tree.EffectivePolicy(inheritedItem).Override);
        Assert.Same(selected, tree.EffectivePolicy(inheritedItem with { SyncPolicy = selected }));
    }

    [Fact]
    public void TreePermitsDuplicateNamesAndExactlyEightLevels()
    {
        var folders = Chain(8, "密码");
        var tree = new VaultTree(folders);
        Assert.Equal(8, tree.Depth(folders[^1].Id));
        Assert.Equal(8, folders.Count(folder => folder.Name == "密码"));
        Assert.Throws<InvalidOperationException>(() => new VaultTree(Chain(9, "same")));
    }

    [Fact]
    public void TreeRejectsCyclesAndMovesBelowDescendants()
    {
        Assert.Throws<InvalidOperationException>(() => new VaultTree(
        [
            Folder(1, Id(2)),
            Folder(2, Id(1)),
        ]));

        var folders = Chain(3, "ordinary");
        var tree = new VaultTree(folders);
        Assert.Throws<InvalidOperationException>(() =>
            tree.ValidateMove(new HashSet<FolderId> { folders[0].Id }, folders[^1].Id));
    }

    [Fact]
    public void TreeRejectsInvalidMoveShapesAndAcceptsBoundedBatches()
    {
        Assert.Throws<ArgumentException>(() => new VaultTree([Folder(1), Folder(1)]));
        Assert.Throws<InvalidOperationException>(() => new VaultTree([Folder(1, Id(99))]));

        VaultFolder[] chain = Chain(8, "ordinary");
        VaultFolder detached = Folder(9);
        var tree = new VaultTree(chain.Append(detached));
        Assert.Throws<ArgumentException>(() => tree.ValidateMove(new HashSet<FolderId>(), null));
        Assert.Throws<InvalidOperationException>(() =>
            tree.ValidateMove(new HashSet<FolderId> { chain[0].Id }, chain[0].Id));
        Assert.Throws<InvalidOperationException>(() =>
            tree.ValidateMove(new HashSet<FolderId> { detached.Id }, chain[^1].Id));
        Assert.Throws<InvalidOperationException>(() =>
            tree.ValidateMove(new HashSet<FolderId> { Id(99) }, null));

        tree.ValidateMove(new HashSet<FolderId> { detached.Id }, null);
        tree.ValidateMove(new HashSet<FolderId> { chain[0].Id, chain[1].Id }, null);
    }

    [Fact]
    public void PolicyInheritanceAndSelectedDeviceRulesAreExplicit()
    {
        var root = Folder(1, policy: new SyncPolicySetting(SyncPolicy.AllPairedDevices));
        var child = Folder(2, root.Id);
        var tree = new VaultTree([root, child]);
        Assert.Equal(SyncPolicy.AllPairedDevices, tree.EffectivePolicy(child.Id).Override);
        Assert.Throws<ArgumentException>(() => new SyncPolicySetting(SyncPolicy.SelectedDevices));
        Assert.Throws<ArgumentException>(() => new SyncPolicySetting(
            SyncPolicy.LocalOnly,
            new HashSet<DeviceId> { DeviceId.Parse("11111111-2222-4333-8444-555555555555") }));
    }

    [Fact]
    public void PasswordIsOrdinaryAndDefaultsAreCreatedOnlyOnce()
    {
        var next = 20;
        var defaults = DefaultFolderInitializer.Create(
            false,
            [],
            () => Id(next++),
            () => Version(next++));
        Assert.Equal(DefaultFolderInitializer.Names, defaults.Select(folder => folder.Name));
        Assert.Empty(DefaultFolderInitializer.Create(true, defaults, () => Id(90), () => Version(90)));
        Assert.Throws<InvalidOperationException>(() =>
            DefaultFolderInitializer.Create(false, defaults, () => Id(90), () => Version(90)));

        var password = Assert.Single(defaults, folder => folder.Name == "密码");
        var renamed = password with { Name = "renamed" };
        Assert.Equal(password.SyncPolicy, renamed.SyncPolicy);
        Assert.Equal(password.ParentId, renamed.ParentId);
    }

    private static VaultFolder[] Chain(int count, string name) =>
        Enumerable.Range(1, count)
            .Select(value => Folder(value, value == 1 ? null : Id(value - 1), name))
            .ToArray();

    private static VaultFolder Folder(
        int value,
        FolderId? parent = null,
        string? name = null,
        SyncPolicySetting? policy = null) => new(
            Id(value),
            parent,
            name ?? $"folder-{value}",
            null,
            value,
            policy ?? new SyncPolicySetting(null),
            Version(value),
            null);

    private static FolderId Id(int value) =>
        FolderId.Parse($"00000000-0000-4000-8000-{value:000000000000}");

    private static VersionId Version(int value) =>
        VersionId.Parse($"10000000-0000-4000-8000-{value:000000000000}");
}
