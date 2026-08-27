using ClipShare.Windows.Application;
using ClipShare.Windows.Infrastructure;

namespace ClipShare.Windows.App;

internal sealed class MainViewModel(ILocalFilePort localFiles) : IDisposable
{
    private ClientSession? _session;
    private Uri? _sessionBaseUri;
    private readonly PreparedOperationState<CreateTextShareCommand, PreparedTextShareCreate> _pendingText = new();
    private readonly PreparedOperationState<UploadAttemptKey, PreparedFileUpload> _pendingUpload = new();

    public async Task<string> CreateTextAsync(
        string baseUrl,
        string content,
        ShareExpiry expiry,
        int? maxViews,
        CancellationToken cancellationToken)
    {
        IClipShareApi api = GetApi(baseUrl);
        CreateTextShareUseCase useCase = new(api, new SecureIdempotencyKeyFactory());
        CreateTextShareCommand command = new(content, expiry, maxViews);
        PreparedTextShareCreate pending = _pendingText.GetOrPrepare(
            command,
            () => useCase.Prepare(command));

        CreatedShare result = await useCase.ExecuteAsync(pending, cancellationToken);
        _pendingText.Complete();
        return result.Url.AbsoluteUri;
    }

    public async Task<string> ReceiveTextAsync(
        string baseUrl,
        string locator,
        CancellationToken cancellationToken)
    {
        ClientSession session = GetSession(baseUrl);
        ReceivedShare result = await new ReceiveTextShareUseCase(session.Api, session.PublicBaseUri)
            .ExecuteAsync(locator, cancellationToken);
        return result.Content;
    }

    public async Task<string> UploadFileAsync(
        string baseUrl,
        string path,
        ShareExpiry expiry,
        int? maxViews,
        CancellationToken cancellationToken)
    {
        IClipShareApi api = GetApi(baseUrl);
        UploadFileUseCase useCase = new(api, new SecureIdempotencyKeyFactory());
        UploadAttemptKey key = new(Path.GetFullPath(path), expiry, maxViews);
        PreparedFileUpload pending = _pendingUpload.GetOrPrepare(key, () =>
        {
            UploadFileCommand command = new(localFiles.OpenUpload(path), expiry, maxViews);
            return useCase.Prepare(command);
        });

        CreatedFile result = await useCase.ExecuteAsync(pending, cancellationToken);
        _pendingUpload.Complete();
        return result.Url.AbsoluteUri;
    }

    public async Task<FileMetadata> GetFileMetadataAsync(
        string baseUrl,
        string locator,
        CancellationToken cancellationToken) =>
        await ExecuteGetFileMetadataAsync(baseUrl, locator, cancellationToken);

    public async Task DownloadFileAsync(
        string baseUrl,
        string locator,
        string targetPath,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        ClientSession session = GetSession(baseUrl);
        await new DownloadFileUseCase(session.Api, session.PublicBaseUri).ExecuteAsync(
            locator,
            localFiles.CreateDownloadTarget(targetPath),
            progress,
            cancellationToken);
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }

    private IClipShareApi GetApi(string baseUrl) => GetSession(baseUrl).Api;

    private async Task<FileMetadata> ExecuteGetFileMetadataAsync(
        string baseUrl,
        string locator,
        CancellationToken cancellationToken)
    {
        ClientSession session = GetSession(baseUrl);
        return await new GetFileMetadataUseCase(session.Api, session.PublicBaseUri)
            .ExecuteAsync(locator, cancellationToken);
    }

    private ClientSession GetSession(string baseUrl)
    {
        EndpointPolicy policy = ClientSession.CreateEndpointPolicy(baseUrl);
        if (_session is null || _sessionBaseUri != policy.BaseUri)
        {
            _session?.Dispose();
            _session = new ClientSession(policy);
            _sessionBaseUri = policy.BaseUri;
            _pendingText.Reset();
            _pendingUpload.Reset();
        }

        return _session;
    }

    private sealed record UploadAttemptKey(
        string Path,
        ShareExpiry Expiry,
        int? MaxViews);
}
