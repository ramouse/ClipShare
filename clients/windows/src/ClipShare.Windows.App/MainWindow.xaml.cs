using ClipShare.Windows.Application;
using ClipShare.Windows.Features.Vault;
using ClipShare.Windows.Platform;
using ClipShare.Windows.Vault;
using System.Text;
using Microsoft.Windows.Storage.Pickers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace ClipShare.Windows.App;

public sealed partial class MainWindow : Window, IDisposable
{
    private readonly MainViewModel _viewModel = new(new LocalFilePort());
    private readonly VaultFeatureController _vaultController;
    private readonly WindowsClientSettingsStore _settingsStore;
    private readonly WindowsInteractionHost _interactionHost = new();
    private readonly LocatorBoundState<FileMetadata> _downloadMetadata = new();
    private readonly Dictionary<Control, bool> _workflowControlStates = new();
    private readonly Dictionary<Control, bool> _vaultControlStates = new();
    private CancellationTokenSource? _currentOperation;
    private string? _selectedVaultFilePath;
    private DateTimeOffset? _deactivatedAt;
    private bool _vaultInitialized;
    private bool _rootLoaded;
    private bool _renderingVault;
    private bool _loadingSettings;
    private bool _disposed;

    public MainWindow()
    {
        string appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClipShare",
            "v0.3");
        var localFiles = new LocalFilePort();
        _vaultController = new VaultFeatureController(
            new WindowsEncryptedVaultWorkspace(Path.Combine(appData, "vault"), localFiles));
        _settingsStore = new WindowsClientSettingsStore(Path.Combine(appData, "settings"));
        InitializeComponent();
        AppNavigationView.SelectedItem = AppNavigationView.MenuItems[0];
        WorkflowNavigationView.SelectedItem = WorkflowNavigationView.MenuItems[0];
        BaseUrlTextBox.TextChanged += (_, _) => ResetEndpointDerivedState();
        ShareContentTextBox.TextChanged += (_, _) => ResetTextShareOutput();
        TextExpiryComboBox.SelectionChanged += (_, _) => ResetTextShareOutput();
        TextViewsComboBox.SelectionChanged += (_, _) => ResetTextShareOutput();
        FileExpiryComboBox.SelectionChanged += (_, _) => ResetFileShareOutput();
        FileViewsComboBox.SelectionChanged += (_, _) => ResetFileShareOutput();
        ReceiveTextLocatorTextBox.TextChanged += (_, _) => ReceivedTextBox.Text = string.Empty;
        DownloadLocatorTextBox.TextChanged += (_, _) => ResetDownloadPreparation();
        _vaultController.StateChanged += VaultController_StateChanged;
        _interactionHost.ClipboardChanged += InteractionHost_ClipboardChanged;
        _interactionHost.HotKeyPressed += InteractionHost_ActivateVault;
        _interactionHost.TrayActivated += InteractionHost_ActivateVault;
        _interactionHost.SessionLocked += InteractionHost_SessionLocked;
        Activated += MainWindow_Activated;
        Closed += (_, _) => Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancellationTokenSource? operation = _currentOperation;
        _currentOperation = null;
        operation?.Cancel();
        operation?.Dispose();
        ClearVaultUiPlaintext();
        _interactionHost.Dispose();
        _vaultController.ClearSensitiveState();
        _vaultController.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _viewModel.Dispose();
        GC.SuppressFinalize(this);
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_rootLoaded)
        {
            return;
        }

        _rootLoaded = true;
        try
        {
            WindowsClientSettings settings = await _settingsStore.ReadAsync();
            _loadingSettings = true;
            MonitorClipboardToggleSwitch.IsOn = settings.MonitorClipboard;
            AutoSyncToggleSwitch.IsOn = settings.AutoSyncPairedDevices;
            _loadingSettings = false;

            IntPtr windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            _interactionHost.Start(windowHandle);
            _interactionHost.SetClipboardMonitoring(settings.MonitorClipboard);
            HotKeyStatusTextBlock.Text = _interactionHost.HotKeyAvailable
                ? "快捷键 Ctrl+Shift+V 已注册；托盘入口已启用。"
                : "快捷键 Ctrl+Shift+V 已被其他应用占用；托盘入口仍可使用。";
            await EnsureVaultInitializedAsync();
        }
        catch (Exception)
        {
            _loadingSettings = false;
            ShowError("内容库或平台入口初始化失败；现有密文保持不变。", InfoBarSeverity.Error);
        }
    }

    private async Task EnsureVaultInitializedAsync()
    {
        if (_vaultInitialized || _disposed)
        {
            return;
        }

        await _vaultController.InitializeAsync();
        _vaultInitialized = true;
        RenderVault(_vaultController.State);
    }

    private void VaultController_StateChanged(object? sender, VaultFeatureState state)
    {
        _ = DispatcherQueue.TryEnqueue(() => RenderVault(state));
    }

    private void RenderVault(VaultFeatureState state)
    {
        if (_disposed)
        {
            return;
        }

        _renderingVault = true;
        try
        {
            VaultFolderView[] activeFolders = state.Snapshot.Folders.Where(folder => !folder.Deleted).ToArray();
            VaultFolderListView.ItemsSource = state.Snapshot.Folders;
            VaultItemListView.ItemsSource = state.Snapshot.Items;
            VaultParentFolderComboBox.ItemsSource = activeFolders;
            VaultTargetFolderComboBox.ItemsSource = activeFolders;
            VaultMoveTargetComboBox.ItemsSource = activeFolders;
            if (VaultTargetFolderComboBox.SelectedIndex < 0 && activeFolders.Length != 0)
            {
                VaultTargetFolderComboBox.SelectedIndex = 0;
            }

            VaultNoticeTextBlock.Text = state.Status ?? state.Snapshot.Notice ?? string.Empty;
            SetVaultInteractionEnabled(!state.IsBusy);
            if (state.IsFailure && !string.IsNullOrWhiteSpace(state.Status))
            {
                ShowError(state.Status, InfoBarSeverity.Error);
            }
        }
        finally
        {
            _renderingVault = false;
        }
    }

    private async void AppNavigationView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        NavigationViewItem? selectedItem = args.SelectedItemContainer as NavigationViewItem
            ?? args.SelectedItem as NavigationViewItem;
        string destination = selectedItem?.Tag?.ToString() ?? "vault";
        bool showVault = destination == "vault";
        bool showShare = destination == "share";
        VaultWorkspaceGrid.Visibility = showVault ? Visibility.Visible : Visibility.Collapsed;
        SettingsWorkspacePanel.Visibility = destination == "settings" ? Visibility.Visible : Visibility.Collapsed;
        WorkflowNavigationView.Visibility = showShare ? Visibility.Visible : Visibility.Collapsed;
        TextWorkflowGrid.Visibility = showShare &&
            string.Equals((WorkflowNavigationView.SelectedItem as NavigationViewItem)?.Tag?.ToString(), "text", StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;
        FileWorkflowGrid.Visibility = showShare &&
            string.Equals((WorkflowNavigationView.SelectedItem as NavigationViewItem)?.Tag?.ToString(), "file", StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (showVault)
        {
            await RunVaultOperationAsync(EnsureVaultInitializedAsync);
        }
    }

    private async void VaultSectionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_rootLoaded || !_vaultInitialized || _renderingVault)
        {
            return;
        }

        await RunVaultOperationAsync(() => _vaultController.NavigateAsync(SelectedVaultSection(), VaultSearchTextBox.Text));
    }

    private async void VaultRefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RunVaultOperationAsync(() => _vaultController.NavigateAsync(SelectedVaultSection(), VaultSearchTextBox.Text));

    private async void VaultCreateFolderButton_Click(object sender, RoutedEventArgs e)
    {
        string? parentId = VaultParentFolderComboBox.SelectedValue as string;
        bool succeeded = await RunVaultOperationAsync(() => _vaultController.CreateFolderAsync(
            new CreateVaultFolderCommand(VaultFolderNameTextBox.Text, parentId)));
        if (succeeded)
        {
            VaultFolderNameTextBox.Text = string.Empty;
        }
    }

    private async void VaultRenameFolderButton_Click(object sender, RoutedEventArgs e)
    {
        bool succeeded = await RunVaultOperationAsync(() =>
        {
            VaultFolderView folder = VaultFolderListView.SelectedItems.OfType<VaultFolderView>().SingleOrDefault()
                ?? throw new InvalidOperationException("请选择一个要重命名的文件夹。 ");
            return _vaultController.RenameFolderAsync(
                new RenameVaultFolderCommand(folder.Id, VaultFolderNameTextBox.Text));
        });
        if (succeeded)
        {
            VaultFolderNameTextBox.Text = string.Empty;
        }
    }

    private async void VaultSaveItemButton_Click(object sender, RoutedEventArgs e)
    {
        bool succeeded = await RunVaultOperationAsync(() =>
        {
            string folderId = RequireSelectedValue(VaultTargetFolderComboBox, "请先选择目标文件夹。 ");
            VaultContentType contentType = (VaultContentTypeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Url"
                ? VaultContentType.Url
                : VaultContentType.Text;
            return _vaultController.SaveTextAsync(
                new SaveVaultTextCommand(folderId, VaultItemTitleTextBox.Text, VaultItemBodyTextBox.Text, contentType));
        });
        if (succeeded)
        {
            VaultItemTitleTextBox.Text = string.Empty;
            VaultItemBodyTextBox.Text = string.Empty;
        }
    }

    private async void VaultUpdateItemButton_Click(object sender, RoutedEventArgs e)
    {
        await RunVaultOperationAsync(() =>
        {
            VaultItemView item = VaultItemListView.SelectedItems.OfType<VaultItemView>().SingleOrDefault()
                ?? throw new InvalidOperationException("请选择一个文本或 URL 条目。 ");
            return _vaultController.UpdateTextAsync(
                new UpdateVaultTextCommand(item.Id, VaultItemTitleTextBox.Text, VaultItemBodyTextBox.Text));
        });
    }

    private async void VaultImportFileButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FileOpenPicker picker = new(AppWindow.Id) { CommitButtonText = "加密导入" };
            PickFileResult? result = await picker.PickSingleFileAsync();
            if (result is null)
            {
                VaultSelectedFileTextBlock.Text = "未选择文件。";
                return;
            }

            _selectedVaultFilePath = result.Path;
            VaultSelectedFileTextBlock.Text = Path.GetFileName(result.Path);
            string folderId = RequireSelectedValue(VaultTargetFolderComboBox, "请先选择目标文件夹。 ");
            string title = string.IsNullOrWhiteSpace(VaultItemTitleTextBox.Text)
                ? Path.GetFileName(result.Path)
                : VaultItemTitleTextBox.Text;
            await RunVaultOperationAsync(() => _vaultController.ImportFileAsync(
                new ImportVaultFileCommand(folderId, title, _selectedVaultFilePath)));
            _selectedVaultFilePath = null;
            VaultSelectedFileTextBlock.Text = "尚未选择 Vault 文件";
            VaultItemTitleTextBox.Text = string.Empty;
        }
        catch (Exception)
        {
            _selectedVaultFilePath = null;
            ShowError("文件导入失败；未记录或显示完整本地路径，现有密文保持不变。", InfoBarSeverity.Error);
        }
    }

    private async void VaultExportFileButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            VaultItemView item = VaultItemListView.SelectedItems.OfType<VaultItemView>().SingleOrDefault()
                ?? throw new InvalidOperationException("请选择一个文件条目。 ");
            if (item.ContentType != VaultContentType.File)
            {
                throw new InvalidOperationException("只有文件条目可以导出。 ");
            }

            FileSavePicker picker = new(AppWindow.Id)
            {
                CommitButtonText = "解密导出",
                SuggestedFileName = SuggestedFileName.FromUntrusted(item.FileName),
            };
            PickFileResult? result = await picker.PickSaveFileAsync();
            if (result is not null)
            {
                await RunVaultOperationAsync(() => _vaultController.ExportFileAsync(item.Id, result.Path));
            }
        }
        catch (Exception)
        {
            ShowError("文件导出失败；未记录或显示完整本地路径。", InfoBarSeverity.Error);
        }
    }

    private void VaultEntityList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_renderingVault)
        {
            return;
        }

        foreach (VaultEntitySelection selected in _vaultController.State.Selection)
        {
            _vaultController.SetSelected(selected, false);
        }

        foreach (VaultFolderView folder in VaultFolderListView.SelectedItems.OfType<VaultFolderView>())
        {
            _vaultController.SetSelected(new VaultEntitySelection(VaultEntityKind.Folder, folder.Id), true);
        }

        foreach (VaultItemView item in VaultItemListView.SelectedItems.OfType<VaultItemView>())
        {
            _vaultController.SetSelected(new VaultEntitySelection(VaultEntityKind.Item, item.Id), true);
        }

        VaultItemView? editable = VaultItemListView.SelectedItems.OfType<VaultItemView>().SingleOrDefault();
        if (editable is not null && editable.ContentType != VaultContentType.File)
        {
            VaultItemTitleTextBox.Text = editable.Title;
            VaultItemBodyTextBox.Text = editable.Text ?? string.Empty;
            VaultContentTypeComboBox.SelectedIndex = editable.ContentType == VaultContentType.Url ? 1 : 0;
        }
    }

    private async void VaultMoveButton_Click(object sender, RoutedEventArgs e) =>
        await RunVaultOperationAsync(() => _vaultController.MoveSelectionAsync(
            RequireSelectedValue(VaultMoveTargetComboBox, "请选择批量移动目标。 ")));

    private async void VaultDeleteButton_Click(object sender, RoutedEventArgs e) =>
        await RunVaultOperationAsync(() => _vaultController.DeleteSelectionAsync(includeFolderContents: true));

    private async void VaultRestoreButton_Click(object sender, RoutedEventArgs e) =>
        await RunVaultOperationAsync(() => _vaultController.RestoreSelectionAsync());

    private async void VaultPurgeButton_Click(object sender, RoutedEventArgs e) =>
        await RunVaultOperationAsync(() => _vaultController.PurgeSelectionAsync());

    private async void VaultPolicyButton_Click(object sender, RoutedEventArgs e)
    {
        SyncPolicy policy = (VaultPolicyComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "AllPairedDevices"
            ? SyncPolicy.AllPairedDevices
            : SyncPolicy.LocalOnly;
        await RunVaultOperationAsync(() => _vaultController.SetSelectionPolicyAsync(
            new VaultPolicyCommand(policy, new HashSet<string>(), VaultClearOverridesCheckBox.IsChecked == true)));
    }

    private async void MonitorClipboardToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || !_rootLoaded)
        {
            return;
        }

        try
        {
            WindowsClientSettings current = await _settingsStore.ReadAsync();
            WindowsClientSettings updated = current with { MonitorClipboard = MonitorClipboardToggleSwitch.IsOn };
            await _settingsStore.WriteAsync(updated);
            _interactionHost.SetClipboardMonitoring(updated.MonitorClipboard);
        }
        catch (Exception)
        {
            ShowError("剪贴板设置保存失败。", InfoBarSeverity.Error);
        }
    }

    private async void AutoSyncToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || !_rootLoaded)
        {
            return;
        }

        try
        {
            WindowsClientSettings current = await _settingsStore.ReadAsync();
            await _settingsStore.WriteAsync(current with { AutoSyncPairedDevices = AutoSyncToggleSwitch.IsOn });
            if (AutoSyncToggleSwitch.IsOn)
            {
                ShowSuccess("自动同步偏好已保存；需要 P1 配对设备后才会执行。当前没有网络操作。 ");
            }
        }
        catch (Exception)
        {
            ShowError("自动同步设置保存失败。", InfoBarSeverity.Error);
        }
    }

    private async void InteractionHost_ClipboardChanged(object? sender, EventArgs e)
    {
        try
        {
            DataPackageView content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                return;
            }

            string text = await content.GetTextAsync();
            if (string.IsNullOrEmpty(text) || Encoding.UTF8.GetByteCount(text) > VaultFeatureController.MaximumTextBytes)
            {
                return;
            }

            VaultItemTitleTextBox.Text = "剪贴板候选";
            VaultItemBodyTextBox.Text = text;
            AppNavigationView.SelectedItem = AppNavigationView.MenuItems[0];
            VaultNoticeTextBlock.Text = "已读取前台剪贴板候选；只有点击“加密保存”才会写入 Vault。";
        }
        catch (Exception)
        {
            ShowError("剪贴板候选暂时不可用；没有保存或发送任何内容。", InfoBarSeverity.Warning);
        }
    }

    private void InteractionHost_ActivateVault(object? sender, EventArgs e)
    {
        AppNavigationView.SelectedItem = AppNavigationView.MenuItems[0];
        Activate();
    }

    private void InteractionHost_SessionLocked(object? sender, EventArgs e)
    {
        _vaultController.ClearSensitiveState();
        _vaultInitialized = false;
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            ClearVaultUiPlaintext();
            RenderVault(_vaultController.State);
        });
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            _deactivatedAt = DateTimeOffset.UtcNow;
            return;
        }

        if (_deactivatedAt is DateTimeOffset deactivated && DateTimeOffset.UtcNow - deactivated >= TimeSpan.FromMinutes(10))
        {
            _vaultController.ClearSensitiveState();
            _vaultInitialized = false;
            ClearVaultUiPlaintext();
        }

        _deactivatedAt = null;
        if (_rootLoaded && !_vaultInitialized)
        {
            await RunVaultOperationAsync(EnsureVaultInitializedAsync);
        }
    }

    private async Task<bool> RunVaultOperationAsync(Func<Task> operation)
    {
        try
        {
            await operation();
            return true;
        }
        catch (Exception)
        {
            ShowError("Vault 操作失败；现有密文保持不变。", InfoBarSeverity.Error);
            return false;
        }
    }

    private VaultSection SelectedVaultSection() =>
        Enum.TryParse((VaultSectionComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out VaultSection section)
            ? section
            : VaultSection.Inbox;

    private static string RequireSelectedValue(ComboBox comboBox, string message) =>
        comboBox.SelectedValue as string ?? throw new InvalidOperationException(message);

    private void ClearVaultUiPlaintext()
    {
        VaultSearchTextBox.Text = string.Empty;
        VaultFolderNameTextBox.Text = string.Empty;
        VaultItemTitleTextBox.Text = string.Empty;
        VaultItemBodyTextBox.Text = string.Empty;
        VaultSelectedFileTextBlock.Text = "尚未选择 Vault 文件";
        VaultNoticeTextBlock.Text = string.Empty;
        VaultFolderListView.ItemsSource = null;
        VaultItemListView.ItemsSource = null;
        _selectedVaultFilePath = null;
    }

    private async void CreateTextButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiOperationAsync(CreateTextButton, async cancellationToken =>
        {
            CreatedTextUrlTextBox.Text = string.Empty;
            CreatedTextUrlTextBox.Text = await _viewModel.CreateTextAsync(
                BaseUrlTextBox.Text,
                ShareContentTextBox.Text,
                SelectedExpiry(TextExpiryComboBox),
                SelectedMaxViews(TextViewsComboBox),
                cancellationToken);
            CopyTextUrlButton.IsEnabled = true;
            ShowSuccess("文本分享已创建；分享链接可能包含敏感能力信息，请谨慎复制。");
        });
    }

    private async void ReceiveTextButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiOperationAsync(ReceiveTextButton, async cancellationToken =>
        {
            ReceivedTextBox.Text = string.Empty;
            ReceivedTextBox.Text = await _viewModel.ReceiveTextAsync(
                BaseUrlTextBox.Text,
                ReceiveTextLocatorTextBox.Text,
                cancellationToken);
            ShowSuccess("领取完成；本次服务器授权已计入访问次数。");
        });
    }

    private async void UploadFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UploadPathTextBox.Text))
        {
            ShowError("请先通过系统文件选择器选择要上传的文件。", InfoBarSeverity.Error);
            return;
        }

        await RunUiOperationAsync(UploadFileButton, async cancellationToken =>
        {
            CreatedFileUrlTextBox.Text = string.Empty;
            CreatedFileUrlTextBox.Text = await _viewModel.UploadFileAsync(
                BaseUrlTextBox.Text,
                UploadPathTextBox.Text,
                SelectedExpiry(FileExpiryComboBox),
                SelectedMaxViews(FileViewsComboBox),
                cancellationToken);
            CopyFileUrlButton.IsEnabled = true;
            ShowSuccess("文件分享已创建；分享链接可能包含敏感能力信息，请谨慎复制。");
        });
    }

    private async void SelectUploadFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartUserSelection())
        {
            return;
        }

        SelectUploadFileButton.IsEnabled = false;
        try
        {
            FileOpenPicker picker = new(AppWindow.Id)
            {
                CommitButtonText = "选择",
            };
            PickFileResult? result = await picker.PickSingleFileAsync();
            if (result is not null)
            {
                UploadPathTextBox.Text = result.Path;
                ResetFileShareOutput();
            }
        }
        catch (Exception)
        {
            ShowError("无法打开文件选择器；未记录或显示本地路径。", InfoBarSeverity.Error);
        }
        finally
        {
            SelectUploadFileButton.IsEnabled = true;
        }
    }

    private async void ReadMetadataButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiOperationAsync(ReadMetadataButton, async cancellationToken =>
        {
            FileMetadataTextBlock.Text = string.Empty;
            _downloadMetadata.Reset();
            FileMetadata metadata = await _viewModel.GetFileMetadataAsync(
                BaseUrlTextBox.Text,
                DownloadLocatorTextBox.Text,
                cancellationToken);
            FileMetadataTextBlock.Text =
                $"{metadata.OriginalName} · {metadata.SizeBytes:N0} 字节 · 剩余次数：{metadata.RemainingViews?.ToString() ?? "不限"}";
            _downloadMetadata.Set(DownloadLocatorTextBox.Text, metadata);
            ShowSuccess("文件元数据已读取；此操作不消耗下载次数。");
        });
    }

    private async void SelectDownloadPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartUserSelection())
        {
            return;
        }

        SelectDownloadPathButton.IsEnabled = false;
        try
        {
            FileMetadata? metadata = _downloadMetadata.Get(DownloadLocatorTextBox.Text);
            FileSavePicker picker = new(AppWindow.Id)
            {
                CommitButtonText = "保存",
                SuggestedFileName = SuggestedFileName.FromUntrusted(metadata?.OriginalName),
            };
            PickFileResult? result = await picker.PickSaveFileAsync();
            if (result is not null)
            {
                DownloadPathTextBox.Text = result.Path;
            }
        }
        catch (Exception)
        {
            ShowError("无法打开保存位置选择器；未记录或显示本地路径。", InfoBarSeverity.Error);
        }
        finally
        {
            SelectDownloadPathButton.IsEnabled = true;
        }
    }

    private async void DownloadFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DownloadPathTextBox.Text))
        {
            ShowError("请先通过系统保存对话框选择保存位置。", InfoBarSeverity.Error);
            return;
        }

        await RunUiOperationAsync(DownloadFileButton, async cancellationToken =>
        {
            FileMetadata? metadata = _downloadMetadata.Get(DownloadLocatorTextBox.Text);
            long? expectedBytes = metadata?.SizeBytes;
            DownloadProgressBar.Value = 0;
            DownloadProgressBar.Maximum = Math.Max(1, expectedBytes ?? 1);
            DownloadProgressBar.IsIndeterminate = expectedBytes is null;
            DownloadProgressTextBlock.Text = expectedBytes is null
                ? "正在下载；未预读元数据，完成前总大小未知。"
                : $"0 / {expectedBytes.Value:N0} 字节";
            Progress<long> progress = new(bytes =>
            {
                if (expectedBytes is not null)
                {
                    DownloadProgressBar.IsIndeterminate = false;
                    DownloadProgressBar.Value = Math.Min(bytes, expectedBytes.Value);
                    DownloadProgressTextBlock.Text = $"{bytes:N0} / {expectedBytes.Value:N0} 字节";
                }
                else
                {
                    DownloadProgressTextBlock.Text = $"已接收 {bytes:N0} 字节";
                }
            });
            await _viewModel.DownloadFileAsync(
                BaseUrlTextBox.Text,
                DownloadLocatorTextBox.Text,
                DownloadPathTextBox.Text,
                progress,
                cancellationToken);
            DownloadProgressBar.IsIndeterminate = false;
            if (expectedBytes is not null)
            {
                DownloadProgressBar.Value = expectedBytes.Value;
            }

            DownloadProgressTextBlock.Text = "下载完成。";
            ShowSuccess("文件下载完成；本次服务器授权已计入下载次数。");
        });
    }

    private void WorkflowNavigationView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        NavigationViewItem? selectedItem = args.SelectedItemContainer as NavigationViewItem
            ?? args.SelectedItem as NavigationViewItem;
        string? destination = selectedItem?.Tag?.ToString();
        bool showFiles = string.Equals(destination, "file", StringComparison.Ordinal);
        TextWorkflowGrid.Visibility = showFiles ? Visibility.Collapsed : Visibility.Visible;
        FileWorkflowGrid.Visibility = showFiles ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RunUiOperationAsync(Button button, Func<CancellationToken, Task> operation)
    {
        if (_currentOperation is not null)
        {
            ShowError("已有操作正在进行；请等待完成或先取消。", InfoBarSeverity.Warning);
            return;
        }

        button.IsEnabled = false;
        SetWorkflowInteractionEnabled(false);
        StatusInfoBar.IsOpen = false;
        using CancellationTokenSource cancellation = new();
        _currentOperation = cancellation;
        try
        {
            await operation(cancellation.Token);
        }
        catch (Exception) when (_disposed)
        {
        }
        catch (ConsumptionOutcomeUnknownException exception)
        {
            ShowError(exception.Message, InfoBarSeverity.Warning);
        }
        catch (ClipShareException exception)
        {
            ShowError(exception.Message, InfoBarSeverity.Error);
        }
        catch (OperationCanceledException)
        {
            ShowError(
                "操作已取消。若这是领取或下载，服务器可能已经计入本次访问，客户端不会自动重试。",
                InfoBarSeverity.Warning);
        }
        catch (Exception)
        {
            ShowError("操作失败；未记录或显示剪贴板内容、短码、文件名或路径。", InfoBarSeverity.Error);
        }
        finally
        {
            if (ReferenceEquals(_currentOperation, cancellation))
            {
                _currentOperation = null;
            }

            if (!_disposed)
            {
                DownloadProgressBar.IsIndeterminate = false;
                SetWorkflowInteractionEnabled(true);
                button.IsEnabled = true;
            }
        }
    }

    private void CancelOperationButton_Click(object sender, RoutedEventArgs e) =>
        _currentOperation?.Cancel();

    private void CopyTextUrlButton_Click(object sender, RoutedEventArgs e) =>
        CopyShareLink(CreatedTextUrlTextBox.Text);

    private void CopyFileUrlButton_Click(object sender, RoutedEventArgs e) =>
        CopyShareLink(CreatedFileUrlTextBox.Text);

    private bool CanStartUserSelection()
    {
        if (_currentOperation is null)
        {
            return true;
        }

        ShowError("已有操作正在进行；请等待完成或先取消。", InfoBarSeverity.Warning);
        return false;
    }

    private void CopyShareLink(string link)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            ShowError("当前没有可复制的分享链接。", InfoBarSeverity.Error);
            return;
        }

        try
        {
            DataPackage package = new();
            package.SetText(link);
            ClipboardContentOptions options = new()
            {
                IsAllowedInHistory = false,
                IsRoamable = false,
            };

            if (Clipboard.SetContentWithOptions(package, options))
            {
                ShowSuccess("分享链接已复制；为降低能力链接泄露风险，未写入剪贴板历史且不跨设备同步。");
            }
            else
            {
                ShowError("剪贴板暂时不可用，请稍后重试。", InfoBarSeverity.Error);
            }
        }
        catch (Exception)
        {
            ShowError("剪贴板暂时不可用，请稍后重试。", InfoBarSeverity.Error);
        }
    }

    private void ResetEndpointDerivedState()
    {
        ResetTextShareOutput();
        ResetFileShareOutput();
        ReceivedTextBox.Text = string.Empty;
        ResetDownloadPreparation();
    }

    private void ResetTextShareOutput()
    {
        CreatedTextUrlTextBox.Text = string.Empty;
        CopyTextUrlButton.IsEnabled = false;
    }

    private void ResetFileShareOutput()
    {
        CreatedFileUrlTextBox.Text = string.Empty;
        CopyFileUrlButton.IsEnabled = false;
    }

    private void ResetDownloadPreparation()
    {
        _downloadMetadata.Reset();
        FileMetadataTextBlock.Text = string.Empty;
        DownloadPathTextBox.Text = string.Empty;
        DownloadProgressBar.IsIndeterminate = false;
        DownloadProgressBar.Value = 0;
        DownloadProgressBar.Maximum = 1;
        DownloadProgressTextBlock.Text = string.Empty;
    }

    private void SetWorkflowInteractionEnabled(bool enabled)
    {
        BaseUrlTextBox.IsEnabled = enabled;
        WorkflowNavigationView.IsEnabled = enabled;
        if (enabled)
        {
            foreach (KeyValuePair<Control, bool> state in _workflowControlStates)
            {
                state.Key.IsEnabled = state.Value;
            }

            _workflowControlStates.Clear();
        }
        else
        {
            _workflowControlStates.Clear();
            CaptureAndDisableControls(TextWorkflowGrid, _workflowControlStates);
            CaptureAndDisableControls(FileWorkflowGrid, _workflowControlStates);
        }

        CancelOperationButton.IsEnabled = !enabled;
    }

    private void SetVaultInteractionEnabled(bool enabled)
    {
        if (enabled)
        {
            foreach (KeyValuePair<Control, bool> state in _vaultControlStates)
            {
                state.Key.IsEnabled = state.Value;
            }

            _vaultControlStates.Clear();
        }
        else if (_vaultControlStates.Count == 0)
        {
            CaptureAndDisableControls(VaultWorkspaceGrid, _vaultControlStates);
        }
    }

    private static void CaptureAndDisableControls(
        DependencyObject parent,
        Dictionary<Control, bool> controlStates)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is Control control)
            {
                controlStates.TryAdd(control, control.IsEnabled);
                control.IsEnabled = false;
            }

            CaptureAndDisableControls(child, controlStates);
        }
    }

    private void ShowSuccess(string message)
    {
        StatusInfoBar.Severity = InfoBarSeverity.Success;
        StatusInfoBar.Title = "成功";
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }

    private void ShowError(string message, InfoBarSeverity severity)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Title = severity == InfoBarSeverity.Warning ? "结果未知" : "操作失败";
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }

    private static ShareExpiry SelectedExpiry(ComboBox comboBox) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "1h" => ShareExpiry.OneHour,
            "24h" => ShareExpiry.OneDay,
            "7d" => ShareExpiry.SevenDays,
            "forever" => ShareExpiry.Forever,
            _ => throw new ClipShareException("invalid_expiry", "请选择有效期。"),
        };

    private static int? SelectedMaxViews(ComboBox comboBox) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "0" => null,
            "1" => 1,
            "5" => 5,
            _ => throw new ClipShareException("invalid_max_views", "请选择访问次数。"),
        };
}
