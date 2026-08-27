using System.Security.Cryptography;
using ClipShare.Windows.Application;

namespace ClipShare.Windows.Platform;

/// <summary>只读本地上传源；重放前流式验证内容身份，避免同一幂等键对应不同正文。</summary>
public sealed class LocalUploadFile : IUploadFile
{
    private readonly string _path;
    private readonly DateTime _lastWriteTimeUtc;
    private readonly object _identityLock = new();
    private byte[]? _expectedSha256;

    public LocalUploadFile(string path, string contentType = "application/octet-stream")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        if (Directory.Exists(_path))
        {
            throw new ClipShareException("file_not_regular", "所选内容不是普通文件。");
        }

        FileInfo info = new(_path);
        if (!info.Exists)
        {
            throw new ClipShareException("file_not_found", "所选文件不存在。");
        }

        DisplayName = info.Name;
        ContentType = string.IsNullOrWhiteSpace(contentType)
            ? "application/octet-stream"
            : contentType;
        Length = info.Length;
        if (Length > ClipShareValidation.MaxUploadBytes)
        {
            throw new ClipShareException(
                "file_size_limit_exceeded",
                $"文件大小不能超过 {ClipShareValidation.MaxUploadBytes} 字节。");
        }

        _lastWriteTimeUtc = info.LastWriteTimeUtc;
    }

    public string DisplayName { get; }

    public string ContentType { get; }

    public long Length { get; }

    public async ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileInfo current = new(_path);
        if (!current.Exists || current.Length != Length || current.LastWriteTimeUtc != _lastWriteTimeUtc)
        {
            throw new ClipShareException(
                "file_changed",
                "所选文件在准备上传后发生变化，请重新选择并生成新的上传操作。");
        }

        FileStream stream = new(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 65_536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            byte[] currentSha256 = await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            lock (_identityLock)
            {
                if (_expectedSha256 is null)
                {
                    _expectedSha256 = currentSha256;
                }
                else if (!CryptographicOperations.FixedTimeEquals(_expectedSha256, currentSha256))
                {
                    throw ChangedFile();
                }
            }

            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static ClipShareException ChangedFile() =>
        new(
            "file_changed",
            "所选文件在准备上传后发生变化，请重新选择并生成新的上传操作。");
}
