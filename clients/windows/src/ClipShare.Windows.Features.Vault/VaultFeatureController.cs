namespace ClipShare.Windows.Features.Vault;

using System.Text;
using ClipShare.Windows.Vault;

public sealed class VaultFeatureController : IAsyncDisposable
{
    public const int MaximumTitleCharacters = 512;
    public const int MaximumTextBytes = 256 * 1024;
    private readonly IVaultWorkspace workspace;
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private readonly HashSet<VaultEntitySelection> selection = [];
    private VaultFeatureState state = VaultFeatureState.Empty;
    private bool disposed;

    public VaultFeatureController(IVaultWorkspace workspace) =>
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));

    public VaultFeatureState State => state;

    public event EventHandler<VaultFeatureState>? StateChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(async token =>
        {
            await workspace.InitializeAsync(token).ConfigureAwait(false);
            await ReloadCoreAsync(state.Section, null, token).ConfigureAwait(false);
            return "加密内容库已就绪。";
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task NavigateAsync(
        VaultSection section,
        string? query = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(async token =>
        {
            selection.Clear();
            await ReloadCoreAsync(section, NormalizeQuery(query), token).ConfigureAwait(false);
            return state.Snapshot.Notice;
        }, cancellationToken);

    public void SetSelected(VaultEntitySelection entity, bool selected)
    {
        ThrowIfDisposed();
        ValidateSelection(entity);
        if (selected)
        {
            _ = selection.Add(entity);
        }
        else
        {
            _ = selection.Remove(entity);
        }

        Publish(state with { Selection = selection.ToHashSet() });
    }

    public Task CreateFolderAsync(
        CreateVaultFolderCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateName(command.Name, nameof(command));
        return MutateAsync(
            token => workspace.CreateFolderAsync(command with { Name = command.Name.Trim() }, token),
            "文件夹已创建。",
            cancellationToken);
    }

    public Task RenameFolderAsync(
        RenameVaultFolderCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateIdentifier(command.FolderId, nameof(command));
        ValidateName(command.Name, nameof(command));
        return MutateAsync(
            token => workspace.RenameFolderAsync(command with { Name = command.Name.Trim() }, token),
            "文件夹已重命名。",
            cancellationToken);
    }

    public Task SaveTextAsync(
        SaveVaultTextCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateTitle(command.Title);
        ValidateText(command.Text, command.ContentType);
        return MutateAsync(
            token => workspace.SaveTextAsync(command with { Title = command.Title.Trim() }, token),
            "内容已加密保存。",
            cancellationToken);
    }

    public Task ImportFileAsync(
        ImportVaultFileCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateTitle(command.Title);
        if (string.IsNullOrWhiteSpace(command.UserSelectedPath))
        {
            throw new ArgumentException("必须使用系统文件选择器明确选择文件。", nameof(command));
        }

        return MutateAsync(
            token => workspace.ImportFileAsync(command with { Title = command.Title.Trim() }, token),
            "文件已流式加密保存。",
            cancellationToken);
    }

    public async Task ExportFileAsync(
        string itemId,
        string userSelectedPath,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(itemId, nameof(itemId));
        if (string.IsNullOrWhiteSpace(userSelectedPath))
        {
            throw new ArgumentException("必须使用系统文件选择器明确选择导出位置。", nameof(userSelectedPath));
        }

        await ExecuteAsync(async token =>
        {
            await workspace.ExportFileAsync(itemId, userSelectedPath, token).ConfigureAwait(false);
            return "文件已解密导出到用户明确选择的位置。";
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task UpdateTextAsync(
        UpdateVaultTextCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateIdentifier(command.ItemId, nameof(command));
        ValidateTitle(command.Title);
        ValidateText(command.Text, VaultContentType.Text);
        return MutateAsync(
            token => workspace.UpdateTextAsync(command with { Title = command.Title.Trim() }, token),
            "内容已更新。",
            cancellationToken);
    }

    public Task MoveSelectionAsync(string targetFolderId, CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(targetFolderId, nameof(targetFolderId));
        IReadOnlySet<VaultEntitySelection> current = RequireSelection();
        return MutateAsync(
            token => workspace.MoveAsync(current, targetFolderId, token),
            "所选内容已移动。",
            cancellationToken);
    }

    public Task DeleteSelectionAsync(
        bool includeFolderContents,
        CancellationToken cancellationToken = default)
    {
        IReadOnlySet<VaultEntitySelection> current = RequireSelection();
        return MutateAsync(
            token => workspace.MoveToTrashAsync(current, includeFolderContents, token),
            "所选内容已移入回收站。",
            cancellationToken);
    }

    public Task RestoreSelectionAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlySet<VaultEntitySelection> current = RequireSelection();
        return MutateAsync(
            token => workspace.RestoreAsync(current, token),
            "所选内容已恢复。",
            cancellationToken);
    }

    public Task PurgeSelectionAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlySet<VaultEntitySelection> current = RequireSelection();
        return MutateAsync(
            token => workspace.PurgeAsync(current, token),
            "所选密文记录已永久删除；不承诺底层介质物理擦除。",
            cancellationToken);
    }

    public Task SetSelectionPolicyAsync(
        VaultPolicyCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Policy == SyncPolicy.SelectedDevices && command.SelectedDeviceIds.Count == 0)
        {
            throw new ArgumentException("指定设备策略至少需要一台设备。", nameof(command));
        }

        if (command.Policy != SyncPolicy.SelectedDevices && command.SelectedDeviceIds.Count != 0)
        {
            throw new ArgumentException("只有指定设备策略可以携带设备列表。", nameof(command));
        }

        foreach (string deviceId in command.SelectedDeviceIds)
        {
            ValidateIdentifier(deviceId, nameof(command));
        }

        IReadOnlySet<VaultEntitySelection> current = RequireSelection();
        return MutateAsync(
            token => workspace.SetPolicyAsync(current, command, token),
            "同步策略已更新；它控制分发，不是逐条目密码学 ACL。",
            cancellationToken);
    }

    public void ClearSensitiveState()
    {
        ThrowIfDisposed();
        workspace.ClearSensitiveState();
        selection.Clear();
        Publish(VaultFeatureState.Empty with { Status = "已清除内存中的 Vault 明文状态。" });
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        workspace.ClearSensitiveState();
        await workspace.DisposeAsync().ConfigureAwait(false);
        operationLock.Dispose();
    }

    private async Task MutateAsync(
        Func<CancellationToken, Task> mutation,
        string success,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(async token =>
        {
            await mutation(token).ConfigureAwait(false);
            selection.Clear();
            await ReloadCoreAsync(state.Section, null, token).ConfigureAwait(false);
            return success;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteAsync(
        Func<CancellationToken, Task<string?>> action,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!await operationLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("已有 Vault 操作正在执行。请等待完成。 ");
        }

        Publish(state with { IsBusy = true, Status = null, IsFailure = false });
        try
        {
            string? status = await action(cancellationToken).ConfigureAwait(false);
            Publish(state with { IsBusy = false, Status = status, IsFailure = false });
        }
        catch (OperationCanceledException)
        {
            Publish(state with { IsBusy = false, Status = "操作已取消。", IsFailure = true });
            throw;
        }
        catch (Exception)
        {
            Publish(state with { IsBusy = false, Status = "Vault 操作失败；现有密文未被替换。", IsFailure = true });
            throw;
        }
        finally
        {
            _ = operationLock.Release();
        }
    }

    private async Task ReloadCoreAsync(
        VaultSection section,
        string? query,
        CancellationToken cancellationToken)
    {
        VaultSnapshot snapshot = await workspace.LoadAsync(section, query, cancellationToken).ConfigureAwait(false);
        state = state with
        {
            Section = section,
            Snapshot = snapshot,
            Selection = selection.ToHashSet(),
        };
    }

    private HashSet<VaultEntitySelection> RequireSelection()
    {
        ThrowIfDisposed();
        if (selection.Count == 0)
        {
            throw new InvalidOperationException("请至少选择一个条目或文件夹。 ");
        }

        return selection.ToHashSet();
    }

    private static void ValidateSelection(VaultEntitySelection entity) =>
        ValidateIdentifier(entity.Id, nameof(entity));

    private static void ValidateIdentifier(string value, string argumentName)
    {
        if (!Guid.TryParseExact(value, "D", out Guid parsed) ||
            !string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal))
        {
            throw new ArgumentException("Vault 标识必须是小写规范 UUID。", argumentName);
        }
    }

    private static void ValidateName(string name, string argumentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Trim().Length > MaximumTitleCharacters)
        {
            throw new ArgumentOutOfRangeException(argumentName, "文件夹名称不能超过 512 个字符。 ");
        }
    }

    private static void ValidateTitle(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (title.Trim().Length > MaximumTitleCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(title), "标题不能超过 512 个字符。 ");
        }
    }

    private static void ValidateText(string text, VaultContentType contentType)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (contentType == VaultContentType.File)
        {
            throw new ArgumentException("文件必须通过显式文件导入入口保存。", nameof(contentType));
        }

        if (Encoding.UTF8.GetByteCount(text) > MaximumTextBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(text), "文本或 URL 不能超过 256 KiB UTF-8。 ");
        }

        if (contentType == VaultContentType.Url &&
            (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https")))
        {
            throw new ArgumentException("URL 必须使用 HTTP(S)。", nameof(text));
        }
    }

    private static string? NormalizeQuery(string? value)
    {
        string? result = value?.Trim();
        return string.IsNullOrEmpty(result) ? null : result;
    }

    private void Publish(VaultFeatureState next)
    {
        state = next;
        StateChanged?.Invoke(this, next);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
}
