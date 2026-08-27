using ClipShare.Windows.Application;
using ClipShare.Windows.Platform;
using Microsoft.Windows.Storage.Pickers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace ClipShare.Windows.App;

public sealed partial class MainWindow : Window, IDisposable
{
    private readonly MainViewModel _viewModel = new(new LocalFilePort());
    private readonly LocatorBoundState<FileMetadata> _downloadMetadata = new();
    private readonly Dictionary<Control, bool> _workflowControlStates = new();
    private CancellationTokenSource? _currentOperation;
    private bool _disposed;

    public MainWindow()
    {
        InitializeComponent();
        WorkflowNavigationView.SelectedItem = WorkflowNavigationView.MenuItems[0];
        BaseUrlTextBox.TextChanged += (_, _) => ResetEndpointDerivedState();
        ShareContentTextBox.TextChanged += (_, _) => ResetTextShareOutput();
        TextExpiryComboBox.SelectionChanged += (_, _) => ResetTextShareOutput();
        TextViewsComboBox.SelectionChanged += (_, _) => ResetTextShareOutput();
        FileExpiryComboBox.SelectionChanged += (_, _) => ResetFileShareOutput();
        FileViewsComboBox.SelectionChanged += (_, _) => ResetFileShareOutput();
        ReceiveTextLocatorTextBox.TextChanged += (_, _) => ReceivedTextBox.Text = string.Empty;
        DownloadLocatorTextBox.TextChanged += (_, _) => ResetDownloadPreparation();
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
        _viewModel.Dispose();
        GC.SuppressFinalize(this);
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
        string? destination = (args.SelectedItem as NavigationViewItem)?.Tag?.ToString();
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
            CaptureAndDisableControls(TextWorkflowGrid);
            CaptureAndDisableControls(FileWorkflowGrid);
        }

        CancelOperationButton.IsEnabled = !enabled;
    }

    private void CaptureAndDisableControls(DependencyObject parent)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is Control control)
            {
                _workflowControlStates.TryAdd(control, control.IsEnabled);
                control.IsEnabled = false;
            }

            CaptureAndDisableControls(child);
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
