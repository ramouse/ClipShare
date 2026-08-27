using ClipShare.Windows.Application;

namespace ClipShare.Windows.Platform;

/// <summary>同目录临时文件下载目标；成功后原子替换，失败删除半成品。</summary>
public sealed class AtomicDownloadTarget : IDownloadTarget, IAsyncDisposable
{
    private readonly string _targetPath;
    private readonly string _temporaryPath;
    private FileStream? _stream;
    private bool _committed;

    public AtomicDownloadTarget(string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        _targetPath = Path.GetFullPath(targetPath);
        string? parent = Path.GetDirectoryName(_targetPath);
        if (parent is null || !Directory.Exists(parent))
        {
            throw new ClipShareException("download_directory_missing", "下载目标目录不存在。");
        }

        _temporaryPath = Path.Combine(
            parent,
            $".{Path.GetFileName(_targetPath)}.{Guid.NewGuid():N}.clipshare-part");
    }

    public ValueTask<Stream> OpenWriteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_stream is not null)
        {
            throw new InvalidOperationException("下载目标已经打开。");
        }

        _stream = new FileStream(
            _temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 65_536,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        return ValueTask.FromResult<Stream>(_stream);
    }

    public async ValueTask CommitAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_stream is null)
        {
            throw new InvalidOperationException("下载目标尚未打开。");
        }

        await _stream.DisposeAsync().ConfigureAwait(false);
        _stream = null;
        File.Move(_temporaryPath, _targetPath, overwrite: true);
        _committed = true;
    }

    public async ValueTask AbortAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }

        if (!_committed)
        {
            File.Delete(_temporaryPath);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await AbortAsync(CancellationToken.None).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
