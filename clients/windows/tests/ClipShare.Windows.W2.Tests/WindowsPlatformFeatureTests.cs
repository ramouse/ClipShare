namespace ClipShare.Windows.W2.Tests;

using ClipShare.Windows.Application;
using ClipShare.Windows.Features.Vault;
using ClipShare.Windows.Platform;
using ClipShare.Windows.Vault;

public sealed class WindowsPlatformFeatureTests
{
    [Fact]
    public async Task NativeWindowsWorkspaceUsesDpapiCurrentUserAndPersistsEncryptedData()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string root = NewSandboxDirectory();
        const string sentinel = "W2_NATIVE_DPAPI_SENTINEL_2d56a0b7";
        try
        {
            string itemId;
            await using (var workspace = new WindowsEncryptedVaultWorkspace(root, new NoFilePort()))
            {
                await workspace.InitializeAsync(TestContext.Current.CancellationToken);
                VaultSnapshot initial = await workspace.LoadAsync(
                    VaultSection.All,
                    null,
                    TestContext.Current.CancellationToken);
                string inbox = initial.Folders.Single(folder => folder.Name == "收件箱").Id;
                await workspace.SaveTextAsync(
                    new SaveVaultTextCommand(inbox, "原生 DPAPI", sentinel, VaultContentType.Text),
                    TestContext.Current.CancellationToken);
                itemId = (await workspace.LoadAsync(
                    VaultSection.All,
                    null,
                    TestContext.Current.CancellationToken)).Items.Single().Id;
            }

            await using (var reopened = new WindowsEncryptedVaultWorkspace(root, new NoFilePort()))
            {
                await reopened.InitializeAsync(TestContext.Current.CancellationToken);
                VaultItemView item = (await reopened.LoadAsync(
                    VaultSection.All,
                    null,
                    TestContext.Current.CancellationToken)).Items.Single(candidate => candidate.Id == itemId);
                Assert.Equal(sentinel, item.Text);
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
        }
    }

    [Fact]
    public async Task SettingsDefaultOffAndPersistAtomically()
    {
        string root = NewSandboxDirectory();
        try
        {
            var store = new WindowsClientSettingsStore(root);
            Assert.Equal(new WindowsClientSettings(), await store.ReadAsync(TestContext.Current.CancellationToken));
            var autoSyncOnly = new WindowsClientSettings(MonitorClipboard: false, AutoSyncPairedDevices: true);
            await store.WriteAsync(autoSyncOnly, TestContext.Current.CancellationToken);
            Assert.Equal(autoSyncOnly, await store.ReadAsync(TestContext.Current.CancellationToken));
            var monitorOnly = new WindowsClientSettings(MonitorClipboard: true, AutoSyncPairedDevices: false);
            await store.WriteAsync(monitorOnly, TestContext.Current.CancellationToken);
            Assert.Equal(monitorOnly, await store.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DeviceIdentityIsStableAndNeverSilentlyRepairsCorruption()
    {
        string root = NewSandboxDirectory();
        try
        {
            var store = new WindowsLocalDeviceIdentityStore(root);
            string first = (await store.ReadOrCreateAsync(TestContext.Current.CancellationToken)).Value;
            Assert.Equal(first, (await store.ReadOrCreateAsync(TestContext.Current.CancellationToken)).Value);
            await File.WriteAllTextAsync(
                Path.Combine(root, "device.id"),
                "corrupt",
                TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<ArgumentException>(() =>
                store.ReadOrCreateAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WindowsInteractionSourceUsesEventsAndHasSymmetricCleanup()
    {
        string source = File.ReadAllText(AssetPath("WindowsInteractionHost.cs"));
        Assert.Contains("AddClipboardFormatListener", source, StringComparison.Ordinal);
        Assert.Contains("RemoveClipboardFormatListener", source, StringComparison.Ordinal);
        Assert.Contains("RegisterHotKey", source, StringComparison.Ordinal);
        Assert.Contains("UnregisterHotKey", source, StringComparison.Ordinal);
        Assert.Contains("ShellNotifyIcon(NotifyDelete", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatcherTimer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetClipboardData", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WinUiSourceExposesVaultAndDoesNotCallStorageOrCryptoDirectly()
    {
        string xaml = File.ReadAllText(AssetPath("MainWindow.xaml"));
        string code = File.ReadAllText(AssetPath("MainWindow.xaml.cs"));
        foreach (string marker in new[]
        {
            "VaultWorkspaceGrid",
            "VaultSearchTextBox",
            "VaultCreateFolderButton",
            "VaultImportFileButton",
            "MonitorClipboardToggleSwitch",
            "AutoSyncToggleSwitch",
        })
        {
            Assert.Contains(marker, xaml, StringComparison.Ordinal);
        }

        Assert.Contains("VaultFeatureController", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.Data.Sqlite", code, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsDpapiVaultKeyProtector", code, StringComparison.Ordinal);
        Assert.DoesNotContain("VaultCryptography", code, StringComparison.Ordinal);
        Assert.DoesNotContain("File.ReadAllText", code, StringComparison.Ordinal);
    }

    private static string NewSandboxDirectory()
    {
        string output = Environment.GetEnvironmentVariable("CLIPSHARE_TEST_OUTPUT")
            ?? throw new InvalidOperationException("CLIPSHARE_TEST_OUTPUT is required.");
        string root = Path.Combine(output, "w2-platform", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string AssetPath(string fileName) => Path.Combine(AppContext.BaseDirectory, "TestAssets", fileName);

    private sealed class NoFilePort : ILocalFilePort
    {
        public IUploadFile OpenUpload(string userSelectedPath) => throw new NotSupportedException();

        public IUploadFile OpenVaultImport(string userSelectedPath) => throw new NotSupportedException();

        public IDownloadTarget CreateDownloadTarget(string userSelectedPath) => throw new NotSupportedException();
    }
}
