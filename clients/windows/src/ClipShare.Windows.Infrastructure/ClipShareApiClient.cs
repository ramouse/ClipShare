using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClipShare.Windows.Application;

namespace ClipShare.Windows.Infrastructure;

/// <summary>API v1 HTTP 适配器；仅对明确的非消费响应做有限重试，不跟随重定向。</summary>
public sealed class ClipShareApiClient(
    HttpClient httpClient,
    EndpointPolicy endpointPolicy,
    Func<TimeSpan, CancellationToken, Task>? retryDelayAsync = null) : IClipShareApi
{
    private const int MaxProblemBytes = 65_536;
    private const int MaxSmallSuccessBytes = 262_144;
    // 100,000 个 UTF-16 code unit 全部以 JSON \\uXXXX 转义时需要 600,000 bytes，
    // 再为固定元数据和 JSON 结构保留余量；反序列化后仍会重新验证字符上限。
    private const int MaxTextShareSuccessBytes = 1_048_576;
    private const int CopyBufferBytes = 65_536;
    private const int MaxNonConsumingAttempts = 3;
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public async Task<CreatedShare> CreateTextShareAsync(
        PreparedTextShareCreate request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using HttpResponseMessage response = await SendNonConsumingWithRetryAsync(
            _ => ValueTask.FromResult(NewTextCreateRequest(request)),
            cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, HttpStatusCode.Created, cancellationToken).ConfigureAwait(false);
        ShareCreatedBody body = await ReadJsonAsync<ShareCreatedBody>(
            response,
            MaxSmallSuccessBytes,
            cancellationToken)
            .ConfigureAwait(false);
        string code = RequiredCode(body.Code);
        return new CreatedShare(
            code,
            endpointPolicy.ValidateCreatedShareUri(body.Url, code),
            ParseOptionalTimestamp(body.ExpiresAt),
            body.MaxViews,
            ParseTimestamp(body.CreatedAt),
            IsReplayed(response));
    }

    public async Task<ReceivedShare> ReadTextShareAsync(
        string code,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage message = NewRequest(
            HttpMethod.Get,
            $"/api/v1/shares/{RequiredCode(code)}");
        try
        {
            using HttpResponseMessage response = await SendConsumingAsync(message, cancellationToken)
                .ConfigureAwait(false);
            await EnsureSuccessAsync(response, HttpStatusCode.OK, cancellationToken).ConfigureAwait(false);
            ShareReadBody body = await ReadJsonAsync<ShareReadBody>(
                response,
                MaxTextShareSuccessBytes,
                cancellationToken)
                .ConfigureAwait(false);
            string content = body.Content ?? throw ContractError("content");
            if (content.Length is < 1 or > ClipShareValidation.MaxTextCharacters)
            {
                throw ContractError("content");
            }

            return new ReceivedShare(
                RequiredCode(body.Code),
                content,
                ParseOptionalTimestamp(body.ExpiresAt),
                body.RemainingViews,
                ParseTimestamp(body.CreatedAt));
        }
        catch (ClipShareException exception) when (exception is not ConsumptionOutcomeUnknownException
            && exception.Code == "invalid_response")
        {
            throw new ConsumptionOutcomeUnknownException(exception);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or IOException
            or OperationCanceledException)
        {
            throw new ConsumptionOutcomeUnknownException(exception);
        }
    }

    public async Task<CreatedFile> UploadFileAsync(
        PreparedFileUpload request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ClipShareValidation.ValidateUpload(request.Command);
        using HttpResponseMessage response = await SendNonConsumingWithRetryAsync(
            token => NewUploadRequestAsync(request, token),
            cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, HttpStatusCode.Created, cancellationToken).ConfigureAwait(false);
        FileCreatedBody body = await ReadJsonAsync<FileCreatedBody>(
            response,
            MaxSmallSuccessBytes,
            cancellationToken)
            .ConfigureAwait(false);
        string code = RequiredCode(body.Code);
        return new CreatedFile(
            code,
            endpointPolicy.ValidateCreatedFileUri(body.Url, code),
            body.OriginalName ?? throw ContractError("original_name"),
            body.SizeBytes,
            body.Encrypted,
            ParseOptionalTimestamp(body.ExpiresAt),
            body.MaxViews,
            ParseTimestamp(body.CreatedAt),
            IsReplayed(response));
    }

    public async Task<FileMetadata> GetFileMetadataAsync(
        string code,
        CancellationToken cancellationToken)
    {
        string requiredCode = RequiredCode(code);
        using HttpResponseMessage response = await SendNonConsumingWithRetryAsync(
            _ => ValueTask.FromResult(NewRequest(
                HttpMethod.Get,
                $"/api/v1/files/{requiredCode}")),
            cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, HttpStatusCode.OK, cancellationToken).ConfigureAwait(false);
        FileReadBody body = await ReadJsonAsync<FileReadBody>(
            response,
            MaxSmallSuccessBytes,
            cancellationToken)
            .ConfigureAwait(false);
        return new FileMetadata(
            RequiredCode(body.Code),
            body.OriginalName ?? throw ContractError("original_name"),
            body.SizeBytes,
            body.Encrypted,
            body.ContentType ?? throw ContractError("content_type"),
            body.PreviewAvailable,
            ParseOptionalTimestamp(body.ExpiresAt),
            body.RemainingViews,
            ParseTimestamp(body.CreatedAt));
    }

    public async Task DownloadFileAsync(
        string code,
        IDownloadTarget target,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        using HttpRequestMessage message = NewRequest(
            HttpMethod.Get,
            $"/api/v1/files/{RequiredCode(code)}/download");
        try
        {
            using HttpResponseMessage response = await SendConsumingAsync(message, cancellationToken)
                .ConfigureAwait(false);
            await EnsureSuccessAsync(response, HttpStatusCode.OK, cancellationToken).ConfigureAwait(false);
            long? declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength > ClipShareValidation.MaxDownloadBytes)
            {
                throw new ClipShareException(
                    "download_size_limit_exceeded",
                    "下载响应超过客户端允许的大小上限。");
            }

            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using Stream destination = await target.OpenWriteAsync(cancellationToken)
                .ConfigureAwait(false);
            byte[] buffer = new byte[CopyBufferBytes];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (total > ClipShareValidation.MaxDownloadBytes - read)
                {
                    throw new InvalidDataException("下载流超过客户端允许的大小上限。");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                total += read;
                progress?.Report(total);
            }

            if (declaredLength is not null && total != declaredLength.Value)
            {
                throw new InvalidDataException("下载响应的 Content-Length 与实际字节数不一致。");
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            await target.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ClipShareException exception) when (exception is not ConsumptionOutcomeUnknownException
            && exception.Code != "api_error")
        {
            await AbortIgnoringFailureAsync(target).ConfigureAwait(false);
            throw new ConsumptionOutcomeUnknownException(exception);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or IOException
            or InvalidDataException
            or UnauthorizedAccessException
            or OperationCanceledException)
        {
            await AbortIgnoringFailureAsync(target).ConfigureAwait(false);
            throw new ConsumptionOutcomeUnknownException(exception);
        }
    }

    public static HttpClient CreateHttpClient(TimeSpan timeout, IWebProxy? proxy = null)
    {
        SocketsHttpHandler handler = new()
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            UseProxy = true,
            Proxy = proxy,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string relativePath)
    {
        Uri uri = endpointPolicy.BuildApiUri(relativePath);
        endpointPolicy.ValidateRequestUri(uri);
        HttpRequestMessage message = new(method, uri);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return message;
    }

    private HttpRequestMessage NewTextCreateRequest(PreparedTextShareCreate request)
    {
        HttpRequestMessage message = NewRequest(HttpMethod.Post, "/api/v1/shares");
        message.Headers.Add("Idempotency-Key", request.IdempotencyKey);
        message.Content = JsonContent.Create(
            new ShareCreateBody(
                request.Command.Content,
                ClipShareValidation.ToWireValue(request.Command.Expiry),
                request.Command.MaxViews),
            options: JsonOptions);
        return message;
    }

    private async ValueTask<HttpRequestMessage> NewUploadRequestAsync(
        PreparedFileUpload request,
        CancellationToken cancellationToken)
    {
        Stream source = await request.Command.File.OpenReadAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            BoundedReadStream bounded = new(source, ClipShareValidation.MaxUploadBytes);
            StreamContent fileContent = new(bounded, CopyBufferBytes);
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(request.Command.File.ContentType);
            MultipartFormDataContent form = new();
            form.Add(fileContent, "file", request.Command.File.DisplayName);
            form.Add(new StringContent(ClipShareValidation.ToWireValue(request.Command.Expiry)), "expiry");
            form.Add(
                new StringContent(request.Command.MaxViews?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                "max_views");
            form.Add(new StringContent(request.Command.Encrypted ? "true" : "false"), "encrypted");

            HttpRequestMessage message = NewRequest(HttpMethod.Post, "/api/v1/files");
            message.Headers.Add("Idempotency-Key", request.IdempotencyKey);
            message.Content = form;
            return message;
        }
        catch
        {
            await source.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendNonConsumingWithRetryAsync(
        Func<CancellationToken, ValueTask<HttpRequestMessage>> requestFactory,
        CancellationToken cancellationToken)
    {
        int attempt = 0;
        while (true)
        {
            attempt++;
            using HttpRequestMessage message = await requestFactory(cancellationToken).ConfigureAwait(false);
            HttpResponseMessage response = await SendNonConsumingAsync(message, cancellationToken)
                .ConfigureAwait(false);
            if (attempt == MaxNonConsumingAttempts || !IsRetryableStatus(response.StatusCode))
            {
                return response;
            }

            try
            {
                EnsureNoStore(response);
                TimeSpan delay = GetRetryDelay(response, attempt);
                await (retryDelayAsync ?? Task.Delay)(delay, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                response.Dispose();
            }
        }
    }

    private static bool IsRetryableStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int completedAttempt)
    {
        TimeSpan? serverDelay = response.Headers.RetryAfter?.Delta;
        if (serverDelay is null && response.Headers.RetryAfter?.Date is DateTimeOffset retryDate)
        {
            serverDelay = retryDate - DateTimeOffset.UtcNow;
        }

        TimeSpan delay = serverDelay
            ?? TimeSpan.FromMilliseconds(250 * (1 << (completedAttempt - 1)));
        if (delay < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return delay > MaximumRetryDelay ? MaximumRetryDelay : delay;
    }

    private async Task<HttpResponseMessage> SendNonConsumingAsync(
        HttpRequestMessage message,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException
            or IOException
            or OperationCanceledException)
        {
            throw new ClipShareException(
                "network_result_unknown",
                "网络结果未知；若输入未改变，请复用当前操作重试。",
                exception);
        }
    }

    private async Task<HttpResponseMessage> SendConsumingAsync(
        HttpRequestMessage message,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or IOException
            or OperationCanceledException)
        {
            throw new ConsumptionOutcomeUnknownException(exception);
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        HttpStatusCode expected,
        CancellationToken cancellationToken)
    {
        EnsureNoStore(response);
        if ((int)response.StatusCode is >= 300 and <= 399)
        {
            throw new ClipShareException("redirect_forbidden", "API 重定向已被安全策略拒绝。");
        }

        if (response.StatusCode == expected)
        {
            return;
        }

        ProblemBody? problem = await TryReadProblemAsync(response, cancellationToken).ConfigureAwait(false);
        string code = problem?.Type ?? "http_error";
        string message = problem?.Detail ?? $"服务器返回错误（HTTP {(int)response.StatusCode}）。";
        throw new ClipShareException("api_error", $"{code}: {message}");
    }

    private static void EnsureNoStore(HttpResponseMessage response)
    {
        if (response.Headers.CacheControl?.NoStore != true)
        {
            throw new ClipShareException("invalid_response", "API 响应缺少 Cache-Control: no-store。");
        }
    }

    private static async Task<T> ReadJsonAsync<T>(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType != "application/json")
        {
            throw new ClipShareException("invalid_response", "API 成功响应媒体类型无效。");
        }

        if (response.Content.Headers.ContentLength is long declaredLength
            && declaredLength > maximumBytes)
        {
            throw new ClipShareException("invalid_response", "API 成功响应超过客户端允许的大小上限。");
        }

        try
        {
            byte[] payload = await ReadBoundedAsync(response.Content, maximumBytes, cancellationToken)
                .ConfigureAwait(false);
            T? result = JsonSerializer.Deserialize<T>(payload, JsonOptions);
            return result ?? throw new JsonException("响应体为空。");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new ClipShareException("invalid_response", "API 响应不符合冻结契约。", exception);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream bounded = new(capacity: Math.Min(maximumBytes, 65_536));
        byte[] buffer = new byte[8_192];
        int total = 0;
        while (total <= maximumBytes)
        {
            int read = await stream.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, maximumBytes + 1 - total)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
        }

        if (total > maximumBytes)
        {
            throw new ClipShareException("invalid_response", "API 成功响应超过客户端允许的大小上限。");
        }

        if (content.Headers.ContentLength is long declaredLength && total != declaredLength)
        {
            throw new ClipShareException("invalid_response", "API 成功响应长度与 Content-Length 不一致。");
        }

        return bounded.ToArray();
    }

    private static async Task<ProblemBody?> TryReadProblemAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentType?.MediaType != "application/problem+json")
        {
            return null;
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using MemoryStream bounded = new();
        byte[] buffer = new byte[4096];
        int total = 0;
        while (total <= MaxProblemBytes)
        {
            int read = await stream.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, MaxProblemBytes + 1 - total)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
        }

        if (total > MaxProblemBytes)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ProblemBody>(bounded.ToArray(), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string RequiredCode(string? code)
    {
        string value = code ?? throw ContractError("code");
        return ClipShareValidation.ParseCode(value);
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        if (!DateTimeOffset.TryParseExact(
            value,
            "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed))
        {
            throw ContractError("timestamp");
        }

        return parsed;
    }

    private static DateTimeOffset? ParseOptionalTimestamp(string? value) =>
        value is null ? null : ParseTimestamp(value);

    private static bool IsReplayed(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Idempotency-Replayed", out IEnumerable<string>? values)
        && values.SingleOrDefault() == "true";

    private static ClipShareException ContractError(string field) =>
        new("invalid_response", $"API 响应字段 {field} 不符合冻结契约。");

    private static async ValueTask AbortIgnoringFailureAsync(IDownloadTarget target)
    {
        try
        {
            await target.AbortAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 原始领取结果未知错误优先；平台适配器应另行记录脱敏清理失败事件。
        }
    }
}
