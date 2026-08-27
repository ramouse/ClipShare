using System.Net;
using ClipShare.Windows.Application;

namespace ClipShare.Windows.W1.Tests;

internal sealed class StubUploadFile(long length, byte[]? content = null) : IUploadFile
{
    private readonly byte[] _content = content ?? [];

    public string DisplayName => "fixture.bin";

    public string ContentType => "application/octet-stream";

    public long Length { get; } = length;

    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<Stream>(new MemoryStream(_content, writable: false));
    }
}

internal sealed class CountingUploadFile(byte[] content) : IUploadFile
{
    public int OpenCount { get; private set; }

    public string DisplayName => "fixture.bin";

    public string ContentType => "application/octet-stream";

    public long Length => content.Length;

    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OpenCount++;
        return ValueTask.FromResult<Stream>(new MemoryStream(content, writable: false));
    }
}

internal sealed class NamedStubUploadFile(string displayName, long length) : IUploadFile
{
    public string DisplayName { get; } = displayName;

    public string ContentType => "application/octet-stream";

    public long Length { get; } = length;

    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<Stream>(new MemoryStream());
}

internal sealed class FixedKeyFactory(string value) : IIdempotencyKeyFactory
{
    public string Create() => value;
}

internal sealed class FakeClipShareApi : IClipShareApi
{
    public PreparedTextShareCreate? LastTextCreate { get; private set; }

    public PreparedFileUpload? LastUpload { get; private set; }

    public IDownloadTarget? LastDownloadTarget { get; private set; }

    public FileMetadata Metadata { get; set; } = new(
        "abc123",
        "fixture.bin",
        3,
        false,
        "text/plain",
        true,
        null,
        1,
        DateTimeOffset.UnixEpoch);

    public List<string> ReadCodes { get; } = [];

    public Task<CreatedShare> CreateTextShareAsync(
        PreparedTextShareCreate request,
        CancellationToken cancellationToken)
    {
        LastTextCreate = request;
        return Task.FromResult(new CreatedShare(
            "abc123",
            new Uri("https://clip.example/s/abc123"),
            null,
            request.Command.MaxViews,
            DateTimeOffset.UnixEpoch,
            false));
    }

    public Task<ReceivedShare> ReadTextShareAsync(string code, CancellationToken cancellationToken)
    {
        ReadCodes.Add(code);
        return Task.FromResult(new ReceivedShare(
            code,
            "payload",
            null,
            1,
            DateTimeOffset.UnixEpoch));
    }

    public Task<CreatedFile> UploadFileAsync(
        PreparedFileUpload request,
        CancellationToken cancellationToken)
    {
        LastUpload = request;
        return Task.FromResult(new CreatedFile(
            "abc123",
            new Uri("https://clip.example/s/abc123"),
            request.Command.File.DisplayName,
            request.Command.File.Length,
            request.Command.Encrypted,
            null,
            request.Command.MaxViews,
            DateTimeOffset.UnixEpoch,
            false));
    }

    public Task<FileMetadata> GetFileMetadataAsync(string code, CancellationToken cancellationToken)
    {
        ReadCodes.Add(code);
        return Task.FromResult(Metadata with { Code = code });
    }

    public Task DownloadFileAsync(
        string code,
        IDownloadTarget target,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        ReadCodes.Add(code);
        LastDownloadTarget = target;
        return Task.CompletedTask;
    }
}

internal sealed class RecordingHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback,
    bool captureContent = false)
    : HttpMessageHandler
{
    public int CallCount { get; private set; }

    public HttpRequestMessage? LastRequest { get; private set; }

    public string? LastContent { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequest = request;
        if (captureContent && request.Content is not null)
        {
            LastContent = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        return await callback(request, cancellationToken);
    }
}

internal static class Responses
{
    public const string CreatedAt = "2026-08-24T01:02:03.123456Z";

    public static HttpResponseMessage Json(HttpStatusCode status, string json)
    {
        HttpResponseMessage response = new(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoStore = true,
        };
        return response;
    }

    public static HttpResponseMessage Problem(HttpStatusCode status, string type, string detail)
    {
        HttpResponseMessage response = new(status)
        {
            Content = new StringContent(
                $$"""{"type":"{{type}}","title":"错误","status":{{(int)status}},"detail":"{{detail}}"}""",
                System.Text.Encoding.UTF8,
                "application/problem+json"),
        };
        response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoStore = true,
        };
        return response;
    }
}

internal sealed class TrackingDownloadTarget : IDownloadTarget
{
    public MemoryStream Stream { get; } = new();

    public bool Committed { get; private set; }

    public bool Aborted { get; private set; }

    public bool ThrowOnAbort { get; init; }

    public bool ThrowUnauthorizedOnOpen { get; init; }

    public ValueTask<Stream> OpenWriteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ThrowUnauthorizedOnOpen)
        {
            throw new UnauthorizedAccessException("simulated target permission failure");
        }

        return ValueTask.FromResult<Stream>(Stream);
    }

    public ValueTask CommitAsync(CancellationToken cancellationToken)
    {
        Committed = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask AbortAsync(CancellationToken cancellationToken)
    {
        Aborted = true;
        if (ThrowOnAbort)
        {
            throw new IOException("simulated cleanup failure");
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class DiscardingDownloadTarget : IDownloadTarget
{
    public bool Committed { get; private set; }

    public bool Aborted { get; private set; }

    public ValueTask<Stream> OpenWriteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Stream.Null);
    }

    public ValueTask CommitAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Committed = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask AbortAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Aborted = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class ThrowingReadStream(byte[] prefix) : MemoryStream(prefix, writable: false)
{
    private bool _hasReturnedData;

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (_hasReturnedData)
        {
            throw new IOException("simulated transport interruption");
        }

        _hasReturnedData = true;
        return base.ReadAsync(buffer, cancellationToken);
    }
}

internal sealed class SyntheticReadStream : Stream
{
    private readonly long _length;
    private long _remaining;

    public SyntheticReadStream(long length) => (_length, _remaining) = (length, length);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _length - _remaining;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = (int)Math.Min(count, _remaining);
        Array.Clear(buffer, offset, read);
        _remaining -= read;
        return read;
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int read = (int)Math.Min(buffer.Length, _remaining);
        buffer.Span[..read].Clear();
        _remaining -= read;
        return ValueTask.FromResult(read);
    }

    public override int ReadByte()
    {
        if (_remaining == 0)
        {
            return -1;
        }

        _remaining--;
        return 0;
    }

    public override void Flush() => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
