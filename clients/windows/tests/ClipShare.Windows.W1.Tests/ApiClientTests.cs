using System.Net;
using System.Text;
using System.Text.Json;
using ClipShare.Windows.Application;
using ClipShare.Windows.Infrastructure;

namespace ClipShare.Windows.W1.Tests;

public sealed class ApiClientTests
{
    private static readonly EndpointPolicy TestPolicy =
        EndpointPolicy.ForRelease(
            new Uri("https://api.example/"),
            new Uri("https://clip.example/"));

    [Fact]
    public async Task CreateShareSendsStableIdempotencyKeyAndFrozenJson()
    {
        RecordingHandler handler = new((request, _) => Task.FromResult(Responses.Json(
            HttpStatusCode.Created,
            $$"""{"code":"abc123","url":"https://clip.example/s/abc123","expires_at":null,"max_views":5,"created_at":"{{Responses.CreatedAt}}"}""")),
            captureContent: true);
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(httpClient, TestPolicy);
        PreparedTextShareCreate prepared = new(
            new CreateTextShareCommand("你好", ShareExpiry.OneDay, 5),
            "fixed-idempotency-key");

        CreatedShare result = await api.CreateTextShareAsync(prepared, TestContext.Current.CancellationToken);

        Assert.Equal("abc123", result.Code);
        Assert.Equal("fixed-idempotency-key", handler.LastRequest!.Headers.GetValues("Idempotency-Key").Single());
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        using JsonDocument body = JsonDocument.Parse(handler.LastContent!);
        Assert.Equal("你好", body.RootElement.GetProperty("content").GetString());
        Assert.Equal("24h", body.RootElement.GetProperty("expiry").GetString());
        Assert.Equal(5, body.RootElement.GetProperty("max_views").GetInt32());
    }

    [Fact]
    public async Task CreateSharePreservesPreparedOperationAcrossExplicitRetry()
    {
        int attempts = 0;
        RecordingHandler handler = new((_, _) =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new HttpRequestException("simulated disconnect");
            }

            HttpResponseMessage response = Responses.Json(
                HttpStatusCode.Created,
                $$"""{"code":"abc123","url":"https://clip.example/s/abc123","expires_at":null,"max_views":null,"created_at":"{{Responses.CreatedAt}}"}""");
            response.Headers.Add("Idempotency-Replayed", "true");
            return Task.FromResult(response);
        });
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(httpClient, TestPolicy);
        PreparedTextShareCreate prepared = new(
            new CreateTextShareCommand("same", ShareExpiry.Forever, null),
            "same-idempotency-key");

        ClipShareException unknown = await Assert.ThrowsAsync<ClipShareException>(() =>
            api.CreateTextShareAsync(prepared, TestContext.Current.CancellationToken));
        Assert.Equal("network_result_unknown", unknown.Code);
        CreatedShare replay = await api.CreateTextShareAsync(prepared, TestContext.Current.CancellationToken);
        Assert.True(replay.Replayed);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task NonConsumingCreateHonorsRetryAfterAndReusesIdempotencyKey()
    {
        List<string> keys = [];
        int attempts = 0;
        RecordingHandler handler = new((request, _) =>
        {
            attempts++;
            keys.Add(request.Headers.GetValues("Idempotency-Key").Single());
            if (attempts == 1)
            {
                HttpResponseMessage unavailable = Responses.Problem(
                    HttpStatusCode.ServiceUnavailable,
                    "temporarily_unavailable",
                    "稍后重试");
                unavailable.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                    TimeSpan.FromSeconds(2));
                return Task.FromResult(unavailable);
            }

            return Task.FromResult(Responses.Json(
                HttpStatusCode.Created,
                $$"""{"code":"abc123","url":"https://clip.example/s/abc123","expires_at":null,"max_views":null,"created_at":"{{Responses.CreatedAt}}"}"""));
        });
        List<TimeSpan> delays = [];
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(
            httpClient,
            TestPolicy,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });
        PreparedTextShareCreate prepared = new(
            new CreateTextShareCommand("same", ShareExpiry.Forever, null),
            "same-idempotency-key");

        CreatedShare created = await api.CreateTextShareAsync(prepared, TestContext.Current.CancellationToken);

        Assert.Equal("abc123", created.Code);
        Assert.Equal(2, handler.CallCount);
        Assert.Equal(["same-idempotency-key", "same-idempotency-key"], keys);
        Assert.Equal([TimeSpan.FromSeconds(2)], delays);
    }

    [Fact]
    public async Task NonConsumingMetadataUsesBoundedExponentialBackoff()
    {
        int attempts = 0;
        RecordingHandler handler = new((_, _) =>
        {
            attempts++;
            return Task.FromResult(attempts < 3
                ? Responses.Problem(HttpStatusCode.TooManyRequests, "busy", "稍后重试")
                : Responses.Json(
                    HttpStatusCode.OK,
                    $$"""{"code":"abc123","original_name":"file.bin","size_bytes":3,"encrypted":false,"content_type":"application/octet-stream","preview_available":false,"expires_at":null,"remaining_views":1,"created_at":"{{Responses.CreatedAt}}"}"""));
        });
        List<TimeSpan> delays = [];
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(
            httpClient,
            TestPolicy,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        FileMetadata metadata = await api.GetFileMetadataAsync(
            "abc123",
            TestContext.Current.CancellationToken);

        Assert.Equal("abc123", metadata.Code);
        Assert.Equal(3, handler.CallCount);
        Assert.Equal([TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500)], delays);
    }

    [Fact]
    public async Task NonConsumingRetryStopsAfterThreeAttempts()
    {
        RecordingHandler handler = new((_, _) => Task.FromResult(
            Responses.Problem(HttpStatusCode.ServiceUnavailable, "busy", "稍后重试")));
        List<TimeSpan> delays = [];
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(
            httpClient,
            TestPolicy,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        ClipShareException exception = await Assert.ThrowsAsync<ClipShareException>(() =>
            api.GetFileMetadataAsync("abc123", TestContext.Current.CancellationToken));

        Assert.Equal("api_error", exception.Code);
        Assert.Equal(3, handler.CallCount);
        Assert.Equal([TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500)], delays);
    }

    [Fact]
    public async Task RetryAfterDateIsClampedToSafeDelayRange()
    {
        int attempts = 0;
        RecordingHandler handler = new((_, _) =>
        {
            attempts++;
            if (attempts == 3)
            {
                return Task.FromResult(Responses.Json(
                    HttpStatusCode.OK,
                    $$"""{"code":"abc123","original_name":"file.bin","size_bytes":3,"encrypted":false,"content_type":"application/octet-stream","preview_available":false,"expires_at":null,"remaining_views":1,"created_at":"{{Responses.CreatedAt}}"}"""));
            }

            HttpResponseMessage response = Responses.Problem(
                HttpStatusCode.ServiceUnavailable,
                "busy",
                "稍后重试");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                attempts == 1
                    ? DateTimeOffset.UtcNow.AddMinutes(-1)
                    : DateTimeOffset.UtcNow.AddMinutes(5));
            return Task.FromResult(response);
        });
        List<TimeSpan> delays = [];
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(
            httpClient,
            TestPolicy,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        await api.GetFileMetadataAsync("abc123", TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.Zero, delays[0]);
        Assert.Equal(TimeSpan.FromSeconds(30), delays[1]);
    }

    [Fact]
    public async Task RetryableResponseWithoutNoStoreFailsClosedWithoutRetry()
    {
        RecordingHandler handler = new((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/problem+json"),
            }));
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(httpClient, TestPolicy, (_, _) => Task.CompletedTask);

        ClipShareException exception = await Assert.ThrowsAsync<ClipShareException>(() =>
            api.GetFileMetadataAsync("abc123", TestContext.Current.CancellationToken));

        Assert.Equal("invalid_response", exception.Code);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task RetryableUploadReopensBodyAndReusesIdempotencyKey()
    {
        int attempts = 0;
        List<string> keys = [];
        RecordingHandler handler = new((request, _) =>
        {
            attempts++;
            keys.Add(request.Headers.GetValues("Idempotency-Key").Single());
            return Task.FromResult(attempts == 1
                ? Responses.Problem(HttpStatusCode.ServiceUnavailable, "busy", "稍后重试")
                : Responses.Json(
                    HttpStatusCode.Created,
                    $$"""{"code":"abc123","url":"https://clip.example/api/v1/files/abc123","original_name":"fixture.bin","size_bytes":3,"encrypted":false,"expires_at":null,"max_views":1,"created_at":"{{Responses.CreatedAt}}"}"""));
        });
        CountingUploadFile upload = new([1, 2, 3]);
        PreparedFileUpload prepared = new(
            new UploadFileCommand(upload, ShareExpiry.OneDay, 1),
            "stable-upload-key");
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(httpClient, TestPolicy, (_, _) => Task.CompletedTask);

        CreatedFile created = await api.UploadFileAsync(prepared, TestContext.Current.CancellationToken);

        Assert.Equal("abc123", created.Code);
        Assert.Equal(2, upload.OpenCount);
        Assert.Equal(["stable-upload-key", "stable-upload-key"], keys);
    }

    [Fact]
    public async Task ConsumingReadDoesNotRetryRetryableStatus()
    {
        RecordingHandler handler = new((_, _) => Task.FromResult(
            Responses.Problem(HttpStatusCode.ServiceUnavailable, "busy", "稍后重试")));
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(httpClient, TestPolicy);

        ClipShareException exception = await Assert.ThrowsAsync<ClipShareException>(() =>
            api.ReadTextShareAsync("abc123", TestContext.Current.CancellationToken));

        Assert.Equal("api_error", exception.Code);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ConsumingReadTransportFailureIsOutcomeUnknownAndNotRetried()
    {
        RecordingHandler handler = new((_, _) => throw new HttpRequestException("offline"));
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(httpClient, TestPolicy);

        ConsumptionOutcomeUnknownException exception =
            await Assert.ThrowsAsync<ConsumptionOutcomeUnknownException>(() =>
                api.ReadTextShareAsync("abc123", TestContext.Current.CancellationToken));

        Assert.Equal("consumption_outcome_unknown", exception.Code);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ConsumingReadInvalidSuccessBodyIsOutcomeUnknown()
    {
        RecordingHandler handler = new((_, _) => Task.FromResult(
            Responses.Json(HttpStatusCode.OK, "{\"unexpected\":true}")));
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(httpClient, TestPolicy);

        await Assert.ThrowsAsync<ConsumptionOutcomeUnknownException>(() =>
            api.ReadTextShareAsync("abc123", TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task KnownProblemResponseRemainsClassifiedApiError()
    {
        RecordingHandler handler = new((_, _) => Task.FromResult(
            Responses.Problem(HttpStatusCode.Gone, "share_expired", "分享已过期")));
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(httpClient, TestPolicy);

        ClipShareException exception = await Assert.ThrowsAsync<ClipShareException>(() =>
            api.ReadTextShareAsync("abc123", TestContext.Current.CancellationToken));
        Assert.Equal("api_error", exception.Code);
        Assert.Contains("share_expired", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingNoStoreIsRejectedAsContractFailure()
    {
        RecordingHandler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        }));
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(httpClient, TestPolicy);

        await Assert.ThrowsAsync<ConsumptionOutcomeUnknownException>(() =>
            api.ReadTextShareAsync("abc123", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RedirectIsRejectedWithoutSecondRequest()
    {
        RecordingHandler handler = new((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://example.com/api/v1/shares/abc123");
            response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true };
            return Task.FromResult(response);
        });
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(httpClient, TestPolicy);

        ClipShareException exception = await Assert.ThrowsAsync<ClipShareException>(() =>
            api.GetFileMetadataAsync("abc123", TestContext.Current.CancellationToken));
        Assert.Equal("redirect_forbidden", exception.Code);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task DownloadStreamsAndCommitsWithoutBufferingWholeBody()
    {
        byte[] content = Enumerable.Range(0, 200_000).Select(value => (byte)value).ToArray();
        RecordingHandler handler = new((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(content, writable: false)),
            };
            response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true };
            return Task.FromResult(response);
        });
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(httpClient, TestPolicy);
        TrackingDownloadTarget target = new();

        await api.DownloadFileAsync("abc123", target, null, TestContext.Current.CancellationToken);

        Assert.True(target.Committed);
        Assert.False(target.Aborted);
        Assert.Equal(content, target.Stream.ToArray());
    }

    [Fact]
    public async Task InterruptedDownloadAbortsPartialAndReportsUnknown()
    {
        RecordingHandler handler = new((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ThrowingReadStream([1, 2, 3])),
            };
            response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true };
            return Task.FromResult(response);
        });
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(httpClient, TestPolicy);
        TrackingDownloadTarget target = new();

        await Assert.ThrowsAsync<ConsumptionOutcomeUnknownException>(() =>
            api.DownloadFileAsync("abc123", target, null, TestContext.Current.CancellationToken));
        Assert.True(target.Aborted);
        Assert.False(target.Committed);
    }

    [Fact]
    public async Task DownloadRejectsOversizedDeclaredLengthBeforeOpeningTarget()
    {
        RecordingHandler handler = new((_, _) =>
        {
            StreamContent content = new(new MemoryStream());
            content.Headers.ContentLength = ClipShareValidation.MaxDownloadBytes + 1;
            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = content };
            response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true };
            return Task.FromResult(response);
        });
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(httpClient, TestPolicy);
        TrackingDownloadTarget target = new();

        await Assert.ThrowsAsync<ConsumptionOutcomeUnknownException>(() =>
            api.DownloadFileAsync("abc123", target, null, TestContext.Current.CancellationToken));

        Assert.True(target.Aborted);
        Assert.False(target.Committed);
        Assert.Equal(0, target.Stream.Length);
    }

    [Fact]
    public async Task DownloadRejectsUnknownLengthStreamBeyondHardLimit()
    {
        RecordingHandler handler = new((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StreamContent(
                    new SyntheticReadStream(ClipShareValidation.MaxDownloadBytes + 1)),
            };
            response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true };
            return Task.FromResult(response);
        });
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(httpClient, TestPolicy);
        DiscardingDownloadTarget target = new();

        await Assert.ThrowsAsync<ConsumptionOutcomeUnknownException>(() =>
            api.DownloadFileAsync("abc123", target, null, TestContext.Current.CancellationToken));

        Assert.True(target.Aborted);
        Assert.False(target.Committed);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task DownloadRequiresDeclaredLengthToMatchActualBody(long declaredLength)
    {
        RecordingHandler handler = new((_, _) =>
        {
            StreamContent content = new(new MemoryStream([1, 2, 3], writable: false));
            content.Headers.ContentLength = declaredLength;
            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = content };
            response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true };
            return Task.FromResult(response);
        });
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(httpClient, TestPolicy);
        TrackingDownloadTarget target = new();

        if (declaredLength == 3)
        {
            await api.DownloadFileAsync("abc123", target, null, TestContext.Current.CancellationToken);
            Assert.True(target.Committed);
            Assert.False(target.Aborted);
            return;
        }

        await Assert.ThrowsAsync<ConsumptionOutcomeUnknownException>(() =>
            api.DownloadFileAsync("abc123", target, null, TestContext.Current.CancellationToken));
        Assert.True(target.Aborted);
        Assert.False(target.Committed);
    }

    [Fact]
    public async Task ReadTextParsesFrozenSuccessContract()
    {
        RecordingHandler handler = new((_, _) => Task.FromResult(Responses.Json(
            HttpStatusCode.OK,
            $$"""{"code":"abc123","content":"payload","expires_at":"{{Responses.CreatedAt}}","remaining_views":1,"created_at":"{{Responses.CreatedAt}}"}""")));
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(httpClient, TestPolicy);

        ReceivedShare result = await api.ReadTextShareAsync("abc123", TestContext.Current.CancellationToken);

        Assert.Equal("payload", result.Content);
        Assert.Equal(1, result.RemainingViews);
        Assert.Equal(TimeSpan.Zero, result.CreatedAt.Offset);
    }

    [Fact]
    public async Task MetadataParsesFrozenSuccessContractWithoutConsumption()
    {
        RecordingHandler handler = new((_, _) => Task.FromResult(Responses.Json(
            HttpStatusCode.OK,
            $$"""{"code":"abc123","original_name":"fixture.bin","size_bytes":3,"encrypted":false,"content_type":"application/octet-stream","preview_available":false,"expires_at":null,"remaining_views":5,"created_at":"{{Responses.CreatedAt}}"}""")));
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(httpClient, TestPolicy);

        FileMetadata result = await api.GetFileMetadataAsync("abc123", TestContext.Current.CancellationToken);

        Assert.Equal("fixture.bin", result.OriginalName);
        Assert.Equal(3, result.SizeBytes);
        Assert.Equal(5, result.RemainingViews);
        Assert.False(result.PreviewAvailable);
    }

    [Fact]
    public async Task UploadStreamsMultipartAndParsesFrozenSuccessContract()
    {
        RecordingHandler handler = new((_, _) => Task.FromResult(Responses.Json(
            HttpStatusCode.Created,
            $$"""{"code":"abc123","url":"https://clip.example/api/v1/files/abc123","original_name":"fixture.bin","size_bytes":3,"encrypted":false,"expires_at":null,"max_views":1,"created_at":"{{Responses.CreatedAt}}"}""")),
            captureContent: true);
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(httpClient, TestPolicy);
        PreparedFileUpload request = new(
            new UploadFileCommand(new StubUploadFile(3, [1, 2, 3]), ShareExpiry.OneDay, 1),
            "upload-key");

        CreatedFile result = await api.UploadFileAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("fixture.bin", result.OriginalName);
        Assert.Equal(3, result.SizeBytes);
        Assert.Contains("name=file", handler.LastContent!, StringComparison.Ordinal);
        Assert.Contains("name=expiry", handler.LastContent!, StringComparison.Ordinal);
        Assert.Equal("upload-key", handler.LastRequest!.Headers.GetValues("Idempotency-Key").Single());
    }

    [Fact]
    public async Task InvalidTimestampIsRejectedUsingExactSixDigitUtcContract()
    {
        RecordingHandler handler = new((_, _) => Task.FromResult(Responses.Json(
            HttpStatusCode.OK,
            "{\"code\":\"abc123\",\"content\":\"payload\",\"expires_at\":null,\"remaining_views\":null,\"created_at\":\"2026-08-24T01:02:03Z\"}")));
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(httpClient, TestPolicy);

        await Assert.ThrowsAsync<ConsumptionOutcomeUnknownException>(() =>
            api.ReadTextShareAsync("abc123", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WrongSuccessMediaTypeIsContractFailure()
    {
        RecordingHandler handler = new((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "text/plain"),
            };
            response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true };
            return Task.FromResult(response);
        });
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(httpClient, TestPolicy);

        ClipShareException exception = await Assert.ThrowsAsync<ClipShareException>(() =>
            api.GetFileMetadataAsync("abc123", TestContext.Current.CancellationToken));
        Assert.Equal("invalid_response", exception.Code);
    }

    [Fact]
    public void BoundedStreamRejectsExtraByteAndSupportsExactBoundary()
    {
        using BoundedReadStream exact = new(new MemoryStream([1, 2, 3]), 3);
        byte[] buffer = new byte[4];
        Assert.Equal(3, exact.Read(buffer, 0, buffer.Length));
        Assert.Equal(0, exact.Read(buffer, 0, buffer.Length));
        Assert.Equal(3, exact.Position);
        Assert.Throws<NotSupportedException>(() => exact.Position = 0);

        using BoundedReadStream oversized = new(new MemoryStream([1, 2, 3, 4]), 3);
        Assert.Equal(3, oversized.Read(buffer, 0, buffer.Length));
        Assert.Throws<InvalidDataException>(() => oversized.ReadByte());
    }

    [Fact]
    public async Task BoundedStreamAsyncRejectsExtraByte()
    {
        await using BoundedReadStream stream = new(new MemoryStream([1, 2]), 1);
        byte[] buffer = new byte[2];
        Assert.Equal(1, await stream.ReadAtLeastAsync(
            buffer,
            1,
            throwOnEndOfStream: false,
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await stream.ReadAtLeastAsync(
                buffer,
                1,
                throwOnEndOfStream: false,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void BoundedStreamExposesReadOnlyCapabilitiesAndByteReads()
    {
        using BoundedReadStream stream = new(new MemoryStream([9]), 1);
        Assert.True(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => _ = stream.Length);
        Assert.Equal(9, stream.ReadByte());
        Assert.Equal(-1, stream.ReadByte());
        Assert.Throws<NotSupportedException>(() => stream.Flush());
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
        Assert.Throws<NotSupportedException>(() => stream.Write([], 0, 0));
    }

    [Fact]
    public async Task DownloadContractFailureAbortsAndReportsOutcomeUnknown()
    {
        RecordingHandler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("body"),
        }));
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(httpClient, TestPolicy);
        TrackingDownloadTarget target = new();

        await Assert.ThrowsAsync<ConsumptionOutcomeUnknownException>(() =>
            api.DownloadFileAsync("abc123", target, null, TestContext.Current.CancellationToken));
        Assert.True(target.Aborted);
    }

    [Fact]
    public async Task DownloadKeepsOriginalUnknownResultWhenCleanupAlsoFails()
    {
        RecordingHandler handler = new((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ThrowingReadStream([1, 2, 3])),
            };
            response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true };
            return Task.FromResult(response);
        });
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(httpClient, TestPolicy);
        TrackingDownloadTarget target = new() { ThrowOnAbort = true };

        await Assert.ThrowsAsync<ConsumptionOutcomeUnknownException>(() =>
            api.DownloadFileAsync("abc123", target, null, TestContext.Current.CancellationToken));
        Assert.True(target.Aborted);
    }

    [Fact]
    public async Task DownloadTargetPermissionFailureAbortsAndReportsUnknown()
    {
        RecordingHandler handler = new((_, _) => Task.FromResult(Responses.Json(
            HttpStatusCode.OK,
            "{}")));
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(httpClient, TestPolicy);
        TrackingDownloadTarget target = new() { ThrowUnauthorizedOnOpen = true };

        await Assert.ThrowsAsync<ConsumptionOutcomeUnknownException>(() =>
            api.DownloadFileAsync("abc123", target, null, TestContext.Current.CancellationToken));

        Assert.True(target.Aborted);
        Assert.False(target.Committed);
    }

    [Theory]
    [InlineData("text/plain", "plain error")]
    [InlineData("application/problem+json", "not-json")]
    public async Task MalformedOrNonProblemErrorsUseGenericHttpClassification(
        string mediaType,
        string content)
    {
        RecordingHandler handler = new((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(content, Encoding.UTF8, mediaType),
            };
            response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true };
            return Task.FromResult(response);
        });
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(httpClient, TestPolicy);

        ClipShareException exception = await Assert.ThrowsAsync<ClipShareException>(() =>
            api.GetFileMetadataAsync("abc123", TestContext.Current.CancellationToken));
        Assert.Equal("api_error", exception.Code);
        Assert.Contains("http_error", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedProblemDetailsAreNotBufferedOrTrusted()
    {
        RecordingHandler handler = new((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(new string('x', 65_537), Encoding.UTF8, "application/problem+json"),
            };
            response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true };
            return Task.FromResult(response);
        });
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(httpClient, TestPolicy);

        ClipShareException exception = await Assert.ThrowsAsync<ClipShareException>(() =>
            api.GetFileMetadataAsync("abc123", TestContext.Current.CancellationToken));
        Assert.Contains("http_error", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidCreatedUrlIsRejectedByFrozenContract()
    {
        RecordingHandler handler = new((_, _) => Task.FromResult(Responses.Json(
            HttpStatusCode.Created,
            $$"""{"code":"abc123","url":"relative","expires_at":null,"max_views":null,"created_at":"{{Responses.CreatedAt}}"}""")));
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(httpClient, TestPolicy);
        PreparedTextShareCreate request = new(
            new CreateTextShareCommand("content", ShareExpiry.OneDay, null),
            "key");

        ClipShareException exception = await Assert.ThrowsAsync<ClipShareException>(() =>
            api.CreateTextShareAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal("invalid_response", exception.Code);
    }

    [Fact]
    public async Task NonConsumingSuccessRejectsOversizedDeclaredJsonBeforeReading()
    {
        RecordingHandler handler = new((_, _) =>
        {
            StreamContent content = new(new MemoryStream());
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            content.Headers.ContentLength = 262_145;
            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = content };
            response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true };
            return Task.FromResult(response);
        });
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(httpClient, TestPolicy);

        ClipShareException exception = await Assert.ThrowsAsync<ClipShareException>(() =>
            api.GetFileMetadataAsync("abc123", TestContext.Current.CancellationToken));

        Assert.Equal("invalid_response", exception.Code);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task NonConsumingSuccessRejectsOversizedUnknownLengthJson()
    {
        RecordingHandler handler = new((_, _) =>
        {
            StreamContent content = new(new SyntheticReadStream(262_145));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = content };
            response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true };
            return Task.FromResult(response);
        });
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(httpClient, TestPolicy);

        ClipShareException exception = await Assert.ThrowsAsync<ClipShareException>(() =>
            api.GetFileMetadataAsync("abc123", TestContext.Current.CancellationToken));

        Assert.Equal("invalid_response", exception.Code);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData(ClipShareValidation.MaxTextCharacters, true)]
    [InlineData(ClipShareValidation.MaxTextCharacters + 1, false)]
    public async Task ConsumingTextRevalidatesCharacterBoundary(int length, bool succeeds)
    {
        string content = new('a', length);
        RecordingHandler handler = new((_, _) => Task.FromResult(Responses.Json(
            HttpStatusCode.OK,
            $$"""{"code":"abc123","content":"{{content}}","expires_at":null,"remaining_views":1,"created_at":"{{Responses.CreatedAt}}"}""")));
        using HttpClient httpClient = new(handler);
        ClipShareApiClient api = new(httpClient, TestPolicy);

        if (succeeds)
        {
            ReceivedShare result = await api.ReadTextShareAsync(
                "abc123",
                TestContext.Current.CancellationToken);
            Assert.Equal(length, result.Content.Length);
        }
        else
        {
            await Assert.ThrowsAsync<ConsumptionOutcomeUnknownException>(() =>
                api.ReadTextShareAsync("abc123", TestContext.Current.CancellationToken));
        }

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public void HttpClientFactoryAppliesCallerTimeout()
    {
        using HttpClient client = ClipShareApiClient.CreateHttpClient(TimeSpan.FromSeconds(42));
        Assert.Equal(TimeSpan.FromSeconds(42), client.Timeout);
    }

    [Fact]
    public async Task OneHundredMebibyteBoundaryStreamsWithFixedBufferAndRejectsNextByte()
    {
        const long boundary = ClipShareValidation.MaxUploadBytes;
        byte[] buffer = new byte[65_536];
        await using BoundedReadStream exact = new(new SyntheticReadStream(boundary), boundary);
        long total = 0;
        int read;
        while ((read = await exact.ReadAsync(buffer, TestContext.Current.CancellationToken)) != 0)
        {
            total += read;
        }

        Assert.Equal(boundary, total);

        await using BoundedReadStream oversized = new(new SyntheticReadStream(boundary + 1), boundary);
        while (oversized.Position < boundary)
        {
            int chunk = await oversized.ReadAsync(buffer, TestContext.Current.CancellationToken);
            Assert.NotEqual(0, chunk);
        }

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await oversized.ReadAtLeastAsync(
                buffer,
                1,
                throwOnEndOfStream: false,
                TestContext.Current.CancellationToken));
    }
}
