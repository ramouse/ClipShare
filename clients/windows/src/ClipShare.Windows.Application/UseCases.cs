namespace ClipShare.Windows.Application;

/// <summary>显式创建文本分享；调用方应保留 Prepared 请求以处理结果未知。</summary>
public sealed class CreateTextShareUseCase(IClipShareApi api, IIdempotencyKeyFactory keyFactory)
{
    public PreparedTextShareCreate Prepare(CreateTextShareCommand command)
    {
        ClipShareValidation.ValidateCreateText(command);
        return new PreparedTextShareCreate(command, keyFactory.Create());
    }

    public Task<CreatedShare> ExecuteAsync(
        PreparedTextShareCreate request,
        CancellationToken cancellationToken)
    {
        ClipShareValidation.ValidateCreateText(request.Command);
        return api.CreateTextShareAsync(request, cancellationToken);
    }
}

/// <summary>消费型文本领取；实现端必须把传输和解析不确定性映射为结果未知。</summary>
public sealed class ReceiveTextShareUseCase(IClipShareApi api, Uri trustedPublicBaseUri)
{
    public Task<ReceivedShare> ExecuteAsync(string codeOrUrl, CancellationToken cancellationToken) =>
        api.ReadTextShareAsync(
            ClipShareValidation.ParseCode(codeOrUrl, trustedPublicBaseUri),
            cancellationToken);
}

/// <summary>显式文件上传；Prepared 请求固定文件对象、字段和幂等键。</summary>
public sealed class UploadFileUseCase(IClipShareApi api, IIdempotencyKeyFactory keyFactory)
{
    public PreparedFileUpload Prepare(UploadFileCommand command)
    {
        ClipShareValidation.ValidateUpload(command);
        return new PreparedFileUpload(command, keyFactory.Create());
    }

    public Task<CreatedFile> ExecuteAsync(
        PreparedFileUpload request,
        CancellationToken cancellationToken)
    {
        ClipShareValidation.ValidateUpload(request.Command);
        return api.UploadFileAsync(request, cancellationToken);
    }
}

/// <summary>非消费型文件元数据读取。</summary>
public sealed class GetFileMetadataUseCase(IClipShareApi api, Uri trustedPublicBaseUri)
{
    public async Task<FileMetadata> ExecuteAsync(
        string codeOrUrl,
        CancellationToken cancellationToken)
    {
        FileMetadata metadata = await api.GetFileMetadataAsync(
            ClipShareValidation.ParseCode(
                codeOrUrl,
                trustedPublicBaseUri,
                ShareLocatorKind.File),
            cancellationToken).ConfigureAwait(false);
        ClipShareValidation.ValidateDownloadMetadata(metadata);
        return metadata;
    }
}

/// <summary>消费型文件下载；先执行非消费型元数据预检，失败时目标必须清理半成品。</summary>
public sealed class DownloadFileUseCase(IClipShareApi api, Uri trustedPublicBaseUri)
{
    public async Task ExecuteAsync(
        string codeOrUrl,
        IDownloadTarget target,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        string code = ClipShareValidation.ParseCode(
            codeOrUrl,
            trustedPublicBaseUri,
            ShareLocatorKind.File);
        FileMetadata metadata = await api.GetFileMetadataAsync(code, cancellationToken)
            .ConfigureAwait(false);
        ClipShareValidation.ValidateDownloadMetadata(metadata);
        await api.DownloadFileAsync(
            code,
            target,
            progress,
            cancellationToken).ConfigureAwait(false);
    }
}
