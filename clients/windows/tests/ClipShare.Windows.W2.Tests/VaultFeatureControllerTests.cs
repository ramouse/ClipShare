namespace ClipShare.Windows.W2.Tests;

using ClipShare.Windows.Features.Vault;
using ClipShare.Windows.Application;
using ClipShare.Windows.Platform;
using ClipShare.Windows.Vault;

public sealed class VaultFeatureControllerTests
{
    private const string FolderId = "10000000-0000-4000-8000-000000000001";
    private const string ItemId = "20000000-0000-4000-8000-000000000001";
    private const string DeviceId = "30000000-0000-4000-8000-000000000001";

    [Fact]
    public async Task InitializeNavigateAndSelectionPublishState()
    {
        await using var workspace = new FakeWorkspace();
        await using var controller = new VaultFeatureController(workspace);
        var observed = new List<VaultFeatureState>();
        controller.StateChanged += (_, state) => observed.Add(state);

        await controller.InitializeAsync(TestContext.Current.CancellationToken);
        await controller.NavigateAsync(VaultSection.All, "  hello  ", TestContext.Current.CancellationToken);
        controller.SetSelected(new VaultEntitySelection(VaultEntityKind.Item, ItemId), true);

        Assert.True(workspace.Initialized);
        Assert.Equal("hello", workspace.LastQuery);
        Assert.Equal(VaultSection.All, controller.State.Section);
        Assert.Single(controller.State.Selection);
        Assert.Contains(observed, state => state.IsBusy);
    }

    [Fact]
    public async Task MutationsValidateAndReloadWithoutKeepingSelection()
    {
        await using var workspace = new FakeWorkspace();
        await using var controller = new VaultFeatureController(workspace);
        await controller.InitializeAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(() => controller.SaveTextAsync(
            new SaveVaultTextCommand(FolderId, "URL", "ftp://example.test", VaultContentType.Url),
            TestContext.Current.CancellationToken));
        await controller.SaveTextAsync(
            new SaveVaultTextCommand(FolderId, "标题", "正文", VaultContentType.Text),
            TestContext.Current.CancellationToken);
        await controller.RenameFolderAsync(
            new RenameVaultFolderCommand(FolderId, "新名称"),
            TestContext.Current.CancellationToken);
        await controller.UpdateTextAsync(
            new UpdateVaultTextCommand(ItemId, "新标题", "新正文"),
            TestContext.Current.CancellationToken);
        controller.SetSelected(new VaultEntitySelection(VaultEntityKind.Item, ItemId), true);
        await controller.MoveSelectionAsync(FolderId, TestContext.Current.CancellationToken);
        controller.SetSelected(new VaultEntitySelection(VaultEntityKind.Item, ItemId), true);
        await controller.DeleteSelectionAsync(false, TestContext.Current.CancellationToken);
        controller.SetSelected(new VaultEntitySelection(VaultEntityKind.Item, ItemId), true);
        await controller.RestoreSelectionAsync(TestContext.Current.CancellationToken);
        controller.SetSelected(new VaultEntitySelection(VaultEntityKind.Item, ItemId), true);
        await controller.PurgeSelectionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, workspace.SaveTextCalls);
        Assert.Equal(1, workspace.MoveCalls);
        Assert.Equal(1, workspace.RenameCalls);
        Assert.Equal(1, workspace.UpdateCalls);
        Assert.Equal(1, workspace.DeleteCalls);
        Assert.Equal(1, workspace.RestoreCalls);
        Assert.Equal(1, workspace.PurgeCalls);
        Assert.Empty(controller.State.Selection);
    }

    [Fact]
    public async Task PolicyRequiresDevicesOnlyForSelectedDevices()
    {
        await using var workspace = new FakeWorkspace();
        await using var controller = new VaultFeatureController(workspace);
        await controller.InitializeAsync(TestContext.Current.CancellationToken);
        controller.SetSelected(new VaultEntitySelection(VaultEntityKind.Item, ItemId), true);

        await Assert.ThrowsAsync<ArgumentException>(() => controller.SetSelectionPolicyAsync(
            new VaultPolicyCommand(SyncPolicy.SelectedDevices, new HashSet<string>(), false),
            TestContext.Current.CancellationToken));
        await controller.SetSelectionPolicyAsync(
            new VaultPolicyCommand(SyncPolicy.SelectedDevices, new HashSet<string> { DeviceId }, false),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, workspace.PolicyCalls);
    }

    [Fact]
    public async Task FailureIsFailClosedAndClearingDropsPlaintextState()
    {
        await using var workspace = new FakeWorkspace { FailCreate = true };
        await using var controller = new VaultFeatureController(workspace);
        await controller.InitializeAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.CreateFolderAsync(
            new CreateVaultFolderCommand("工作", null),
            TestContext.Current.CancellationToken));
        Assert.True(controller.State.IsFailure);

        controller.ClearSensitiveState();
        Assert.True(workspace.Cleared);
        Assert.Empty(controller.State.Snapshot.Items);
    }

    [Fact]
    public async Task ControllerValidatesOptionalPathsPoliciesAndDisposal()
    {
        Assert.Throws<ArgumentNullException>(() => new VaultFeatureController(null!));
        await using var workspace = new FakeWorkspace();
        var controller = new VaultFeatureController(workspace);
        await controller.InitializeAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.MoveSelectionAsync(FolderId, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => controller.ImportFileAsync(
            new ImportVaultFileCommand(FolderId, "文件", " "),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            controller.ExportFileAsync(ItemId, " ", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => controller.SaveTextAsync(
            new SaveVaultTextCommand(FolderId, "文件", "x", VaultContentType.File),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => controller.SaveTextAsync(
            new SaveVaultTextCommand(
                FolderId,
                "大文本",
                new string('x', VaultFeatureController.MaximumTextBytes + 1),
                VaultContentType.Text),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => controller.SaveTextAsync(
            new SaveVaultTextCommand(FolderId, "URL", "/relative", VaultContentType.Url),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => controller.RenameFolderAsync(
            new RenameVaultFolderCommand("AAAAAAAA-BBBB-4CCC-8DDD-EEEEEEEEEEEE", "名称"),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => controller.CreateFolderAsync(
            new CreateVaultFolderCommand(
                new string('x', VaultFeatureController.MaximumTitleCharacters + 1),
                null),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => controller.SaveTextAsync(
            new SaveVaultTextCommand(
                FolderId,
                new string('x', VaultFeatureController.MaximumTitleCharacters + 1),
                "正文",
                VaultContentType.Text),
            TestContext.Current.CancellationToken));

        await controller.ImportFileAsync(
            new ImportVaultFileCommand(FolderId, "文件", "selected.bin"),
            TestContext.Current.CancellationToken);
        await controller.ExportFileAsync(ItemId, "exported.bin", TestContext.Current.CancellationToken);
        await controller.SaveTextAsync(
            new SaveVaultTextCommand(FolderId, "URL", "https://example.test", VaultContentType.Url),
            TestContext.Current.CancellationToken);
        await controller.NavigateAsync(VaultSection.All, "   ", TestContext.Current.CancellationToken);
        Assert.Null(workspace.LastQuery);

        var selected = new VaultEntitySelection(VaultEntityKind.Item, ItemId);
        controller.SetSelected(selected, true);
        controller.SetSelected(selected, false);
        Assert.Empty(controller.State.Selection);
        controller.SetSelected(selected, true);
        await Assert.ThrowsAsync<ArgumentException>(() => controller.SetSelectionPolicyAsync(
            new VaultPolicyCommand(SyncPolicy.LocalOnly, new HashSet<string> { DeviceId }, false),
            TestContext.Current.CancellationToken));

        workspace.CancelCreate = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.CreateFolderAsync(
            new CreateVaultFolderCommand("取消", null),
            TestContext.Current.CancellationToken));
        Assert.Equal("操作已取消。", controller.State.Status);

        await controller.DisposeAsync();
        await controller.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => controller.ClearSensitiveState());
    }

    [Fact]
    public async Task EncryptedWorkspacePersistsCrudPolicyTrashAndNoPlaintextSentinel()
    {
        string root = NewSandboxDirectory();
        const string sentinel = "W2_WINDOWS_SENTINEL_7d8cce1b";
        string sourceFile = Path.Combine(Path.GetDirectoryName(root)!, $"source-{Guid.NewGuid():N}.bin");
        string exportedFile = sourceFile + ".exported";
        try
        {
            await File.WriteAllTextAsync(sourceFile, sentinel + "-file", TestContext.Current.CancellationToken);
            var protector = new TestSecretProtector();
            string inboxId;
            string itemId;
            await using (var workspace = new WindowsEncryptedVaultWorkspace(root, new NoFilePort(), protector))
            {
                await workspace.InitializeAsync(TestContext.Current.CancellationToken);
                VaultSnapshot initial = await workspace.LoadAsync(
                    VaultSection.All,
                    null,
                    TestContext.Current.CancellationToken);
                Assert.Equal(5, initial.Folders.Count);
                inboxId = initial.Folders.Single(folder => folder.Name == "收件箱").Id;

                await workspace.SaveTextAsync(
                    new SaveVaultTextCommand(inboxId, "敏感标题", sentinel, VaultContentType.Text),
                    TestContext.Current.CancellationToken);
                VaultSnapshot saved = await workspace.LoadAsync(
                    VaultSection.Inbox,
                    "SENTINEL",
                    TestContext.Current.CancellationToken);
                VaultItemView item = Assert.Single(saved.Items);
                itemId = item.Id;
                Assert.Equal(sentinel, item.Text);
                await workspace.ImportFileAsync(
                    new ImportVaultFileCommand(inboxId, "文件", sourceFile),
                    TestContext.Current.CancellationToken);
                VaultItemView fileItem = (await workspace.LoadAsync(
                    VaultSection.All,
                    null,
                    TestContext.Current.CancellationToken)).Items.Single(candidate => candidate.ContentType == VaultContentType.File);
                Assert.Equal(Path.GetFileName(sourceFile), fileItem.FileName);
                Assert.Null(fileItem.Text);
                await workspace.ExportFileAsync(fileItem.Id, exportedFile, TestContext.Current.CancellationToken);
                Assert.Equal(
                    await File.ReadAllBytesAsync(sourceFile, TestContext.Current.CancellationToken),
                    await File.ReadAllBytesAsync(exportedFile, TestContext.Current.CancellationToken));

                var selected = new HashSet<VaultEntitySelection>
                {
                    new(VaultEntityKind.Item, itemId),
                };
                await workspace.SetPolicyAsync(
                    selected,
                    new VaultPolicyCommand(SyncPolicy.AllPairedDevices, new HashSet<string>(), false),
                    TestContext.Current.CancellationToken);
                Assert.Single((await workspace.LoadAsync(VaultSection.Synced, null, TestContext.Current.CancellationToken)).Items);
                await workspace.MoveToTrashAsync(selected, false, TestContext.Current.CancellationToken);
                Assert.Single((await workspace.LoadAsync(VaultSection.Trash, null, TestContext.Current.CancellationToken)).Items);
                await workspace.RestoreAsync(selected, TestContext.Current.CancellationToken);
                Assert.Empty((await workspace.LoadAsync(VaultSection.Trash, null, TestContext.Current.CancellationToken)).Items);
            }

            await using (var reopened = new WindowsEncryptedVaultWorkspace(root, new NoFilePort(), protector))
            {
                await reopened.InitializeAsync(TestContext.Current.CancellationToken);
                VaultItemView persisted = (await reopened.LoadAsync(
                    VaultSection.All,
                    null,
                    TestContext.Current.CancellationToken)).Items.Single(
                    candidate => candidate.ContentType == VaultContentType.Text);
                Assert.Equal(itemId, persisted.Id);
                Assert.Equal(sentinel, persisted.Text);
                await reopened.MoveToTrashAsync(
                    new HashSet<VaultEntitySelection> { new(VaultEntityKind.Item, itemId) },
                    false,
                    TestContext.Current.CancellationToken);
                await reopened.PurgeAsync(
                    new HashSet<VaultEntitySelection> { new(VaultEntityKind.Item, itemId) },
                    TestContext.Current.CancellationToken);
                Assert.Empty((await reopened.LoadAsync(VaultSection.Trash, null, TestContext.Current.CancellationToken)).Items);
            }

            byte[] sentinelBytes = System.Text.Encoding.UTF8.GetBytes(sentinel);
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                byte[] contents = await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken);
                Assert.Equal(-1, contents.AsSpan().IndexOf(sentinelBytes));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            File.Delete(sourceFile);
            File.Delete(exportedFile);
        }
    }

    [Fact]
    public async Task EncryptedWorkspaceEnforcesTreeBatchAndRecoveryBoundaries()
    {
        string root = NewSandboxDirectory();
        try
        {
            var protector = new TestSecretProtector();
            await using var workspace = new WindowsEncryptedVaultWorkspace(root, new NoFilePort(), protector);
            await workspace.InitializeAsync(TestContext.Current.CancellationToken);
            VaultSnapshot initial = await workspace.LoadAsync(VaultSection.All, null, TestContext.Current.CancellationToken);
            string inbox = initial.Folders.Single(folder => folder.Name == "收件箱").Id;

            await workspace.CreateFolderAsync(
                new CreateVaultFolderCommand("父级", null),
                TestContext.Current.CancellationToken);
            string parent = (await workspace.LoadAsync(VaultSection.All, null, TestContext.Current.CancellationToken))
                .Folders.Single(folder => folder.Name == "父级").Id;
            await workspace.CreateFolderAsync(
                new CreateVaultFolderCommand("子级", parent),
                TestContext.Current.CancellationToken);
            string child = (await workspace.LoadAsync(VaultSection.All, null, TestContext.Current.CancellationToken))
                .Folders.Single(folder => folder.Name == "子级").Id;
            await workspace.SaveTextAsync(
                new SaveVaultTextCommand(child, "旧标题", "旧正文", VaultContentType.Text),
                TestContext.Current.CancellationToken);
            string item = (await workspace.LoadAsync(VaultSection.All, null, TestContext.Current.CancellationToken))
                .Items.Single().Id;
            await workspace.UpdateTextAsync(
                new UpdateVaultTextCommand(item, "新标题", "新正文"),
                TestContext.Current.CancellationToken);
            Assert.Equal("新正文", (await workspace.LoadAsync(VaultSection.All, "新标题", TestContext.Current.CancellationToken)).Items.Single().Text);

            await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.MoveAsync(
                new HashSet<VaultEntitySelection> { new(VaultEntityKind.Folder, parent) },
                child,
                TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.MoveToTrashAsync(
                new HashSet<VaultEntitySelection> { new(VaultEntityKind.Folder, parent) },
                false,
                TestContext.Current.CancellationToken));

            await workspace.SetPolicyAsync(
                new HashSet<VaultEntitySelection> { new(VaultEntityKind.Folder, parent) },
                new VaultPolicyCommand(SyncPolicy.AllPairedDevices, new HashSet<string>(), true),
                TestContext.Current.CancellationToken);
            VaultSnapshot synced = await workspace.LoadAsync(VaultSection.Synced, null, TestContext.Current.CancellationToken);
            Assert.Contains(synced.Folders, folder => folder.Id == child);
            Assert.Contains(synced.Items, candidate => candidate.Id == item);

            await workspace.MoveAsync(
                new HashSet<VaultEntitySelection>
                {
                    new(VaultEntityKind.Folder, child),
                    new(VaultEntityKind.Item, item),
                },
                inbox,
                TestContext.Current.CancellationToken);
            VaultSnapshot moved = await workspace.LoadAsync(VaultSection.All, null, TestContext.Current.CancellationToken);
            Assert.Equal(inbox, moved.Folders.Single(folder => folder.Id == child).ParentId);
            Assert.Equal(inbox, moved.Items.Single(candidate => candidate.Id == item).FolderId);

            await workspace.MoveToTrashAsync(
                new HashSet<VaultEntitySelection> { new(VaultEntityKind.Folder, parent) },
                false,
                TestContext.Current.CancellationToken);
            await workspace.PurgeAsync(
                new HashSet<VaultEntitySelection> { new(VaultEntityKind.Folder, parent) },
                TestContext.Current.CancellationToken);
            Assert.DoesNotContain(
                (await workspace.LoadAsync(VaultSection.Trash, null, TestContext.Current.CancellationToken)).Folders,
                folder => folder.Id == parent);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExistingCiphertextWithoutWrapperFailsClosedWithoutReplacement()
    {
        string root = NewSandboxDirectory();
        try
        {
            var protector = new TestSecretProtector();
            await using (var workspace = new WindowsEncryptedVaultWorkspace(root, new NoFilePort(), protector))
            {
                await workspace.InitializeAsync(TestContext.Current.CancellationToken);
                string inbox = (await workspace.LoadAsync(VaultSection.All, null, TestContext.Current.CancellationToken))
                    .Folders.Single(folder => folder.Name == "收件箱").Id;
                await workspace.SaveTextAsync(
                    new SaveVaultTextCommand(inbox, "标题", "正文", VaultContentType.Text),
                    TestContext.Current.CancellationToken);
            }

            string wrapper = Directory.EnumerateFiles(Path.Combine(root, "wrappers"), "*.wrapper.json").Single();
            File.Delete(wrapper);
            await using var reopened = new WindowsEncryptedVaultWorkspace(root, new NoFilePort(), protector);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                reopened.InitializeAsync(TestContext.Current.CancellationToken));
            Assert.True(File.Exists(Path.Combine(root, "vault.db")));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "wrappers"), "*.wrapper.json"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewSandboxDirectory()
    {
        string output = Environment.GetEnvironmentVariable("CLIPSHARE_TEST_OUTPUT")
            ?? throw new InvalidOperationException("CLIPSHARE_TEST_OUTPUT is required.");
        string root = Path.Combine(output, "w2-workspace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class TestSecretProtector : IWindowsVaultSecretProtector
    {
        public WindowsProtectedSecret Protect(
            VaultId vaultId,
            string initializationId,
            long keyEpoch,
            ReadOnlySpan<byte> epochSecret) =>
            new(Convert.ToBase64String(epochSecret).TrimEnd('=').Replace('+', '-').Replace('/', '_'));

        public byte[] Unprotect(
            VaultId vaultId,
            string initializationId,
            long keyEpoch,
            WindowsProtectedSecret protectedSecret)
        {
            string padded = protectedSecret.ProtectedSecret.Replace('-', '+').Replace('_', '/')
                .PadRight((protectedSecret.ProtectedSecret.Length + 3) / 4 * 4, '=');
            return Convert.FromBase64String(padded);
        }
    }

    private sealed class NoFilePort : ILocalFilePort
    {
        public IUploadFile OpenUpload(string userSelectedPath) => throw new NotSupportedException();

        public IUploadFile OpenVaultImport(string userSelectedPath) =>
            new LocalUploadFile(userSelectedPath, maximumLength: long.MaxValue);

        public IDownloadTarget CreateDownloadTarget(string userSelectedPath) => new AtomicDownloadTarget(userSelectedPath);
    }

    private sealed class FakeWorkspace : IVaultWorkspace
    {
        public bool Initialized { get; private set; }

        public bool Cleared { get; private set; }

        public bool FailCreate { get; init; }

        public bool CancelCreate { get; set; }

        public int SaveTextCalls { get; private set; }

        public int MoveCalls { get; private set; }

        public int PolicyCalls { get; private set; }

        public int RenameCalls { get; private set; }

        public int ImportCalls { get; private set; }

        public int ExportCalls { get; private set; }

        public int UpdateCalls { get; private set; }

        public int DeleteCalls { get; private set; }

        public int RestoreCalls { get; private set; }

        public int PurgeCalls { get; private set; }

        public string? LastQuery { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Initialized = true;
            return Task.CompletedTask;
        }

        public Task<VaultSnapshot> LoadAsync(
            VaultSection section,
            string? query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastQuery = query;
            return Task.FromResult(new VaultSnapshot(
                [new VaultFolderView(FolderId, null, "收件箱", null, 0, new SyncPolicySetting(null), SyncPolicy.LocalOnly, false)],
                [new VaultItemView(ItemId, FolderId, VaultContentType.Text, "标题", "正文", null, 6, new SyncPolicySetting(null), SyncPolicy.LocalOnly, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false)],
                [new VaultDeviceView(DeviceId, "测试设备", false)],
                false,
                section == VaultSection.Devices ? "设备配对属于 P1。" : null));
        }

        public Task CreateFolderAsync(CreateVaultFolderCommand command, CancellationToken cancellationToken)
        {
            if (CancelCreate)
            {
                return Task.FromCanceled(new CancellationToken(canceled: true));
            }

            return FailCreate
                ? Task.FromException(new InvalidOperationException("fail closed"))
                : Task.CompletedTask;
        }

        public Task RenameFolderAsync(RenameVaultFolderCommand command, CancellationToken cancellationToken)
        {
            RenameCalls++;
            return Task.CompletedTask;
        }

        public Task SaveTextAsync(SaveVaultTextCommand command, CancellationToken cancellationToken)
        {
            SaveTextCalls++;
            return Task.CompletedTask;
        }

        public Task ImportFileAsync(ImportVaultFileCommand command, CancellationToken cancellationToken)
        {
            ImportCalls++;
            return Task.CompletedTask;
        }

        public Task ExportFileAsync(string itemId, string userSelectedPath, CancellationToken cancellationToken)
        {
            ExportCalls++;
            return Task.CompletedTask;
        }

        public Task UpdateTextAsync(UpdateVaultTextCommand command, CancellationToken cancellationToken)
        {
            UpdateCalls++;
            return Task.CompletedTask;
        }

        public Task MoveAsync(IReadOnlySet<VaultEntitySelection> current, string targetFolderId, CancellationToken cancellationToken)
        {
            MoveCalls++;
            return Task.CompletedTask;
        }

        public Task MoveToTrashAsync(IReadOnlySet<VaultEntitySelection> current, bool includeFolderContents, CancellationToken cancellationToken)
        {
            DeleteCalls++;
            return Task.CompletedTask;
        }

        public Task RestoreAsync(IReadOnlySet<VaultEntitySelection> current, CancellationToken cancellationToken)
        {
            RestoreCalls++;
            return Task.CompletedTask;
        }

        public Task PurgeAsync(IReadOnlySet<VaultEntitySelection> current, CancellationToken cancellationToken)
        {
            PurgeCalls++;
            return Task.CompletedTask;
        }

        public Task SetPolicyAsync(IReadOnlySet<VaultEntitySelection> current, VaultPolicyCommand policy, CancellationToken cancellationToken)
        {
            PolicyCalls++;
            return Task.CompletedTask;
        }

        public void ClearSensitiveState() => Cleared = true;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
