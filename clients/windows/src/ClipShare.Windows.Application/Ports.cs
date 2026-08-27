namespace ClipShare.Windows.Application;

/// <summary>可重复打开的上传源；实现不得把文件整体读入内存。</summary>
public interface IUploadFile
{
    string DisplayName { get; }

    string ContentType { get; }

    long Length { get; }

    ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken);
}

/// <summary>支持失败清理和原子提交的下载目标。</summary>
public interface IDownloadTarget
{
    ValueTask<Stream> OpenWriteAsync(CancellationToken cancellationToken);

    ValueTask CommitAsync(CancellationToken cancellationToken);

    ValueTask AbortAsync(CancellationToken cancellationToken);
}

/// <summary>冻结的 API v1 客户端端口。</summary>
public interface IClipShareApi
{
    Task<CreatedShare> CreateTextShareAsync(
        PreparedTextShareCreate request,
        CancellationToken cancellationToken);

    Task<ReceivedShare> ReadTextShareAsync(string code, CancellationToken cancellationToken);

    Task<CreatedFile> UploadFileAsync(
        PreparedFileUpload request,
        CancellationToken cancellationToken);

    Task<FileMetadata> GetFileMetadataAsync(string code, CancellationToken cancellationToken);

    Task DownloadFileAsync(
        string code,
        IDownloadTarget target,
        IProgress<long>? progress,
        CancellationToken cancellationToken);
}

/// <summary>高熵幂等键来源。</summary>
public interface IIdempotencyKeyFactory
{
    string Create();
}

/// <summary>本地文件系统适配端口；UI 仅传递用户明确选择的路径字符串。</summary>
public interface ILocalFilePort
{
    IUploadFile OpenUpload(string userSelectedPath);

    IDownloadTarget CreateDownloadTarget(string userSelectedPath);
}
