using ClipShare.Windows.Application;
using ClipShare.Windows.Infrastructure;

namespace ClipShare.Windows.W1.Tests;

public sealed class UseCaseTests
{
    private static readonly string[] ExpectedReadCodes = ["abc123", "abc123", "abc123", "abc123"];
    private static readonly Uri TrustedPublicBaseUri = new("https://clip.example/");

    [Fact]
    public async Task TextCreatePreparesAndExecutesImmutableOperation()
    {
        FakeClipShareApi api = new();
        CreateTextShareUseCase useCase = new(api, new FixedKeyFactory("stable-key"));
        CreateTextShareCommand command = new("content", ShareExpiry.SevenDays, 1);

        PreparedTextShareCreate prepared = useCase.Prepare(command);
        CreatedShare result = await useCase.ExecuteAsync(prepared, TestContext.Current.CancellationToken);

        Assert.Same(prepared, api.LastTextCreate);
        Assert.Equal("stable-key", prepared.IdempotencyKey);
        Assert.Equal("abc123", result.Code);
        Assert.Equal("7d", ClipShareValidation.ToWireValue(command.Expiry));
    }

    [Fact]
    public async Task UploadPreparesAndExecutesImmutableOperation()
    {
        FakeClipShareApi api = new();
        UploadFileUseCase useCase = new(api, new FixedKeyFactory("upload-key"));
        UploadFileCommand command = new(new StubUploadFile(3, [1, 2, 3]), ShareExpiry.OneHour, 5);

        PreparedFileUpload prepared = useCase.Prepare(command);
        CreatedFile result = await useCase.ExecuteAsync(prepared, TestContext.Current.CancellationToken);

        Assert.Same(prepared, api.LastUpload);
        Assert.Equal("upload-key", prepared.IdempotencyKey);
        Assert.Equal("fixture.bin", result.OriginalName);
        Assert.False(result.Encrypted);
    }

    [Fact]
    public async Task ReadMetadataAndDownloadUseCasesParseCodeBeforeCallingApi()
    {
        FakeClipShareApi api = new();
        ReceivedShare received = await new ReceiveTextShareUseCase(api, TrustedPublicBaseUri).ExecuteAsync(
            "https://clip.example/s/abc123",
            TestContext.Current.CancellationToken);
        FileMetadata metadata = await new GetFileMetadataUseCase(api, TrustedPublicBaseUri).ExecuteAsync(
            "abc123",
            TestContext.Current.CancellationToken);
        TrackingDownloadTarget target = new();
        await new DownloadFileUseCase(api, TrustedPublicBaseUri).ExecuteAsync(
            "https://clip.example/api/v1/files/abc123",
            target,
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal("payload", received.Content);
        Assert.Equal("text/plain", metadata.ContentType);
        Assert.Equal(3, metadata.SizeBytes);
        Assert.Equal(ExpectedReadCodes, api.ReadCodes);
        Assert.Same(target, api.LastDownloadTarget);
    }

    [Fact]
    public async Task EncryptedLocatorIsRejectedBeforeConsumingApiCall()
    {
        FakeClipShareApi api = new();
        ReceiveTextShareUseCase useCase = new(api, TrustedPublicBaseUri);

        ClipShareException exception = await Assert.ThrowsAsync<ClipShareException>(() =>
            useCase.ExecuteAsync(
                "https://clip.example/s/abc123#k=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                TestContext.Current.CancellationToken));

        Assert.Equal("encrypted_share_not_supported", exception.Code);
        Assert.Empty(api.ReadCodes);
    }

    [Fact]
    public async Task DownloadPreflightRejectsEncryptedMetadataBeforeConsumption()
    {
        FakeClipShareApi api = new();
        api.Metadata = api.Metadata with { Encrypted = true };
        TrackingDownloadTarget target = new();

        ClipShareException exception = await Assert.ThrowsAsync<ClipShareException>(() =>
            new DownloadFileUseCase(api, TrustedPublicBaseUri).ExecuteAsync(
                "abc123",
                target,
                null,
                TestContext.Current.CancellationToken));

        Assert.Equal("encrypted_share_not_supported", exception.Code);
        Assert.Equal(["abc123"], api.ReadCodes);
        Assert.Null(api.LastDownloadTarget);
        Assert.False(target.Aborted);
    }

    [Fact]
    public async Task DownloadPreflightRejectsOversizedMetadataBeforeConsumption()
    {
        FakeClipShareApi api = new();
        api.Metadata = api.Metadata with { SizeBytes = ClipShareValidation.MaxDownloadBytes + 1 };

        ClipShareException exception = await Assert.ThrowsAsync<ClipShareException>(() =>
            new DownloadFileUseCase(api, TrustedPublicBaseUri).ExecuteAsync(
                "abc123",
                new TrackingDownloadTarget(),
                null,
                TestContext.Current.CancellationToken));

        Assert.Equal("download_size_limit_exceeded", exception.Code);
        Assert.Equal(["abc123"], api.ReadCodes);
        Assert.Null(api.LastDownloadTarget);
    }

    [Fact]
    public void ValidationCoversExpiryFileNameAndNullBoundaries()
    {
        Assert.Equal("1h", ClipShareValidation.ToWireValue(ShareExpiry.OneHour));
        Assert.Equal("24h", ClipShareValidation.ToWireValue(ShareExpiry.OneDay));
        Assert.Equal("forever", ClipShareValidation.ToWireValue(ShareExpiry.Forever));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ClipShareValidation.ToWireValue((ShareExpiry)999));
        Assert.Throws<ArgumentNullException>(() => ClipShareValidation.ValidateCreateText(null!));
        Assert.Throws<ArgumentNullException>(() => ClipShareValidation.ValidateUpload(null!));
        ClipShareException emptyName = Assert.Throws<ClipShareException>(() =>
            ClipShareValidation.ValidateUpload(new UploadFileCommand(
                new NamedStubUploadFile(" ", 0),
                ShareExpiry.OneDay,
                null)));
        Assert.Equal("invalid_file_name", emptyName.Code);
    }

    [Fact]
    public void SecureKeysAreIndependentBase64UrlEncoded256BitValues()
    {
        SecureIdempotencyKeyFactory factory = new();
        string first = factory.Create();
        string second = factory.Create();

        Assert.Matches("^[A-Za-z0-9_-]{43}$", first);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void PreparedOperationStateReusesOnlyEqualPendingInput()
    {
        PreparedOperationState<string, object> state = new(StringComparer.Ordinal);
        int prepareCount = 0;

        object first = state.GetOrPrepare("same", () =>
        {
            prepareCount++;
            return new object();
        });
        object retry = state.GetOrPrepare("same", () =>
        {
            prepareCount++;
            return new object();
        });
        object changed = state.GetOrPrepare("changed", () =>
        {
            prepareCount++;
            return new object();
        });

        Assert.True(state.HasPending);
        Assert.Same(first, retry);
        Assert.NotSame(first, changed);
        Assert.Equal(2, prepareCount);
    }

    [Fact]
    public void PreparedOperationStateClearsAfterSuccessOrEndpointReset()
    {
        PreparedOperationState<int, object> state = new();
        object first = state.GetOrPrepare(1, static () => new object());

        state.Complete();
        Assert.False(state.HasPending);
        object afterSuccess = state.GetOrPrepare(1, static () => new object());

        state.Reset();
        Assert.False(state.HasPending);
        object afterReset = state.GetOrPrepare(1, static () => new object());

        Assert.NotSame(first, afterSuccess);
        Assert.NotSame(afterSuccess, afterReset);
    }

    [Fact]
    public void PreparedOperationStateRejectsInvalidFactories()
    {
        PreparedOperationState<string, object> state = new();

        Assert.Throws<ArgumentNullException>(() => state.GetOrPrepare(null!, static () => new object()));
        Assert.Throws<ArgumentNullException>(() => state.GetOrPrepare("key", null!));
        Assert.Throws<InvalidOperationException>(() => state.GetOrPrepare("key", static () => null!));
        Assert.False(state.HasPending);
    }

    [Fact]
    public void LocatorBoundStateReturnsValueOnlyForCurrentTrimmedInput()
    {
        LocatorBoundState<object> state = new();
        object value = new();

        state.Set("  abc123  ", value);

        Assert.True(state.HasValue);
        Assert.Same(value, state.Get("abc123"));
        Assert.Null(state.Get("ABC123"));
        Assert.Null(state.Get("other"));
        Assert.Null(state.Get(" "));
    }

    [Fact]
    public void LocatorBoundStateResetsAndRejectsInvalidValues()
    {
        LocatorBoundState<object> state = new();

        Assert.Throws<ArgumentException>(() => state.Set(" ", new object()));
        Assert.Throws<ArgumentNullException>(() => state.Set("abc123", null!));
        state.Set("abc123", new object());
        state.Reset();

        Assert.False(state.HasValue);
        Assert.Null(state.Get("abc123"));
    }
}
