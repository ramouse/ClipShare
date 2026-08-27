using ClipShare.Windows.Application;
using ClipShare.Windows.Infrastructure;

namespace ClipShare.Windows.W1.Tests;

public sealed class ValidationTests
{
    [Theory]
    [InlineData(null, "clipshare-download.bin")]
    [InlineData("   ", "clipshare-download.bin")]
    [InlineData("report.txt", "report.txt")]
    [InlineData("../server/path/report.txt", "report.txt")]
    [InlineData("..\\server\\bad<name>.txt", "bad_name_.txt")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("...", "clipshare-download.bin")]
    [InlineData("line\nbreak.txt", "line_break.txt")]
    public void SuggestedFileNameTreatsServerNameAsUntrusted(string? input, string expected) =>
        Assert.Equal(expected, SuggestedFileName.FromUntrusted(input));

    [Fact]
    public void SuggestedFileNameIsBoundedForPickerDisplay()
    {
        string result = SuggestedFileName.FromUntrusted(new string('a', 200));

        Assert.Equal(128, result.Length);
    }

    [Theory]
    [InlineData("abc123", "abc123")]
    [InlineData(" https://clip.example/s/Ab19 ", "Ab19")]
    [InlineData("http://localhost:8000/s/z", "z")]
    public void ParseCodeAcceptsFrozenLocatorForms(string input, string expected)
    {
        Uri? trustedOrigin = Uri.TryCreate(input.Trim(), UriKind.Absolute, out Uri? locator)
            ? new Uri(locator.GetLeftPart(UriPartial.Authority) + "/")
            : null;

        Assert.Equal(expected, ClipShareValidation.ParseCode(input, trustedOrigin));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abcdefghi")]
    [InlineData("https://user@example.com/s/abc")]
    [InlineData("https://example.com/s/abc?code=other")]
    [InlineData("https://example.com/api/v1/shares/abc")]
    public void ParseCodeRejectsAmbiguousOrUnsafeLocators(string input)
    {
        Uri? trustedOrigin = input.StartsWith("https://example.com/", StringComparison.Ordinal)
            ? new Uri("https://example.com/")
            : null;
        ClipShareException exception = Assert.Throws<ClipShareException>(
            () => ClipShareValidation.ParseCode(input, trustedOrigin));
        Assert.Equal("invalid_share_locator", exception.Code);
    }

    [Theory]
    [InlineData("https://clip.example/s/Ab19#k=secret", "invalid_encryption_fragment")]
    [InlineData("https://clip.example/s/Ab19#k=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&x=1", "invalid_encryption_fragment")]
    [InlineData("https://clip.example/s/Ab19#k=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "encrypted_share_not_supported")]
    public void ParseCodeRejectsEncryptedLocatorBeforeConsumption(string input, string expectedCode)
    {
        ClipShareException exception = Assert.Throws<ClipShareException>(
            () => ClipShareValidation.ParseCode(input, new Uri("https://clip.example/")));
        Assert.Equal(expectedCode, exception.Code);
        Assert.Contains("未发起", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AbsoluteLocatorRequiresExplicitMatchingTrustedOrigin()
    {
        const string locator = "https://clip.example/s/Ab19";

        Assert.Equal(
            "share_origin_forbidden",
            Assert.Throws<ClipShareException>(() => ClipShareValidation.ParseCode(locator)).Code);
        Assert.Equal(
            "share_origin_forbidden",
            Assert.Throws<ClipShareException>(() => ClipShareValidation.ParseCode(
                locator,
                new Uri("https://other.example/"))).Code);
    }

    [Theory]
    [InlineData("https://clip.example/s/Ab19")]
    [InlineData("https://clip.example/api/v1/files/Ab19")]
    public void FileLocatorAcceptsFrozenPublicAndApiForms(string locator) =>
        Assert.Equal(
            "Ab19",
            ClipShareValidation.ParseCode(
                locator,
                new Uri("https://clip.example/"),
                ShareLocatorKind.File));

    [Fact]
    public void TextLocatorRejectsFileApiPath()
    {
        ClipShareException exception = Assert.Throws<ClipShareException>(() =>
            ClipShareValidation.ParseCode(
                "https://clip.example/api/v1/files/Ab19",
                new Uri("https://clip.example/"),
                ShareLocatorKind.Text));

        Assert.Equal("invalid_share_locator", exception.Code);
    }

    [Fact]
    public void CreateTextEnforcesFrozenLengthAndViewLimits()
    {
        Assert.Throws<ClipShareException>(() => ClipShareValidation.ValidateCreateText(
            new CreateTextShareCommand(string.Empty, ShareExpiry.OneDay, null)));
        Assert.Throws<ClipShareException>(() => ClipShareValidation.ValidateCreateText(
            new CreateTextShareCommand("content", ShareExpiry.OneDay, 2)));
        ClipShareValidation.ValidateCreateText(
            new CreateTextShareCommand("content", ShareExpiry.OneDay, 5));
    }

    [Fact]
    public void UploadEnforcesOneHundredMebibyteBoundary()
    {
        ClipShareValidation.ValidateUpload(new UploadFileCommand(
            new StubUploadFile(ClipShareValidation.MaxUploadBytes),
            ShareExpiry.OneDay,
            null));
        ClipShareException exception = Assert.Throws<ClipShareException>(() =>
            ClipShareValidation.ValidateUpload(new UploadFileCommand(
                new StubUploadFile(ClipShareValidation.MaxUploadBytes + 1),
                ShareExpiry.OneDay,
                null)));
        Assert.Equal("file_size_limit_exceeded", exception.Code);
    }

    [Fact]
    public void DownloadMetadataEnforcesUnencryptedBoundedContract()
    {
        FileMetadata valid = new(
            "abc123",
            "fixture.bin",
            ClipShareValidation.MaxDownloadBytes,
            false,
            "application/octet-stream",
            false,
            null,
            null,
            DateTimeOffset.UnixEpoch);
        ClipShareValidation.ValidateDownloadMetadata(valid);

        Assert.Throws<ArgumentNullException>(() => ClipShareValidation.ValidateDownloadMetadata(null!));
        Assert.Equal(
            "encrypted_share_not_supported",
            Assert.Throws<ClipShareException>(() =>
                ClipShareValidation.ValidateDownloadMetadata(valid with { Encrypted = true })).Code);
        Assert.Equal(
            "download_size_limit_exceeded",
            Assert.Throws<ClipShareException>(() =>
                ClipShareValidation.ValidateDownloadMetadata(valid with { SizeBytes = -1 })).Code);
        Assert.Equal(
            "invalid_response",
            Assert.Throws<ClipShareException>(() =>
                ClipShareValidation.ValidateDownloadMetadata(valid with { OriginalName = " " })).Code);
        Assert.Equal(
            "invalid_response",
            Assert.Throws<ClipShareException>(() =>
                ClipShareValidation.ValidateDownloadMetadata(valid with { ContentType = " " })).Code);
    }

    [Fact]
    public void TestEndpointAllowsOnlyHttpLoopback()
    {
        EndpointPolicy policy = EndpointPolicy.ForLoopbackTest(new Uri("http://127.0.0.1:43123/"));
        Assert.Equal(
            new Uri("http://127.0.0.1:43123/api/v1/shares"),
            policy.BuildApiUri("/api/v1/shares"));
        Assert.Throws<ClipShareException>(() =>
            EndpointPolicy.ForLoopbackTest(new Uri("https://example.com/")));
        Assert.Throws<ClipShareException>(() =>
            EndpointPolicy.ForLoopbackTest(new Uri("http://47.120.13.250/")));
    }

    [Fact]
    public void ReleaseEndpointRequiresHttpsAndRejectsCredentials()
    {
        EndpointPolicy policy = EndpointPolicy.ForRelease(
            new Uri("https://api.example/"),
            new Uri("https://clip.example/"));
        Assert.False(policy.IsTestOnly);
        Assert.Equal(new Uri("https://clip.example/"), policy.PublicBaseUri);
        Assert.Throws<ClipShareException>(() =>
            EndpointPolicy.ForRelease(new Uri("http://clip.example/")));
        Assert.Throws<ClipShareException>(() =>
            EndpointPolicy.ForRelease(new Uri("https://user:pass@clip.example/")));
        Assert.Equal(
            "https_required",
            Assert.Throws<ClipShareException>(() => EndpointPolicy.ForRelease(
                new Uri("https://api.example/"),
                new Uri("http://clip.example/"))).Code);
    }

    [Theory]
    [InlineData("https://CLIP.EXAMPLE", "https://clip.example/")]
    [InlineData("https://clip.example:443/", "https://clip.example/")]
    public void ReleaseEndpointCanonicalizesEquivalentOrigins(string input, string expected)
    {
        EndpointPolicy policy = EndpointPolicy.ForRelease(new Uri(input));

        Assert.Equal(new Uri(expected), policy.BaseUri);
        Assert.Equal(expected, policy.BaseUri.AbsoluteUri);
    }

    [Fact]
    public void LoopbackPolicyAllowsOnlyExplicitLoopbackPublicOrigin()
    {
        EndpointPolicy policy = EndpointPolicy.ForLoopbackTest(
            new Uri("http://127.0.0.1:43123/"),
            new Uri("http://localhost:43124/"));

        Assert.Equal(new Uri("http://localhost:43124/"), policy.PublicBaseUri);
        Assert.Equal(
            "test_endpoint_forbidden",
            Assert.Throws<ClipShareException>(() => EndpointPolicy.ForLoopbackTest(
                new Uri("http://127.0.0.1:43123/"),
                new Uri("https://localhost:43124/"))).Code);
        Assert.Equal(
            "test_endpoint_forbidden",
            Assert.Throws<ClipShareException>(() => EndpointPolicy.ForLoopbackTest(
                new Uri("http://127.0.0.1:43123/"),
                new Uri("http://example.com/"))).Code);
    }

    [Theory]
    [InlineData("https://clip.example/path")]
    [InlineData("https://clip.example/?query=1")]
    [InlineData("https://clip.example/#fragment")]
    [InlineData("ftp://clip.example/")]
    public void EndpointRejectsBaseUrlsOutsideFrozenOriginForm(string value)
    {
        ClipShareException exception = Assert.Throws<ClipShareException>(() =>
            EndpointPolicy.ForRelease(new Uri(value)));
        Assert.Equal("invalid_base_url", exception.Code);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("/api/v1/shares?x=1")]
    [InlineData("/api/v1/shares#x")]
    public void EndpointRejectsInvalidApiPaths(string path)
    {
        EndpointPolicy policy = EndpointPolicy.ForRelease(new Uri("https://clip.example/"));
        Assert.Throws<ClipShareException>(() => policy.BuildApiUri(path));
    }

    [Fact]
    public void EndpointRejectsCrossOriginRequests()
    {
        EndpointPolicy policy = EndpointPolicy.ForRelease(new Uri("https://clip.example/"));
        ClipShareException exception = Assert.Throws<ClipShareException>(() =>
            policy.ValidateRequestUri(new Uri("https://other.example/api/v1/shares")));
        Assert.Equal("request_origin_forbidden", exception.Code);
    }

    [Theory]
    [InlineData("relative", false)]
    [InlineData("https://user@clip.example/api/v1/shares", true)]
    [InlineData("https://clip.example/api/v1/shares#fragment", true)]
    [InlineData("http://clip.example/api/v1/shares", true)]
    [InlineData("https://clip.example:444/api/v1/shares", true)]
    public void EndpointRejectsEveryUnsafeRequestOriginForm(string value, bool absolute)
    {
        EndpointPolicy policy = EndpointPolicy.ForRelease(new Uri("https://clip.example/"));
        Uri requestUri = new(value, absolute ? UriKind.Absolute : UriKind.Relative);

        ClipShareException exception = Assert.Throws<ClipShareException>(() =>
            policy.ValidateRequestUri(requestUri));

        Assert.Equal("request_origin_forbidden", exception.Code);
    }

    [Fact]
    public void EndpointRejectsRelativeBaseUri()
    {
        ClipShareException exception = Assert.Throws<ClipShareException>(() =>
            EndpointPolicy.ForRelease(new Uri("relative", UriKind.Relative)));

        Assert.Equal("invalid_base_url", exception.Code);
    }

    [Fact]
    public void CreatedUrlsAcceptOnlyTheirFrozenResourcePaths()
    {
        EndpointPolicy policy = EndpointPolicy.ForRelease(
            new Uri("https://api.example/"),
            new Uri("https://clip.example/"));

        Assert.Equal(
            new Uri("https://clip.example/s/abc123"),
            policy.ValidateCreatedShareUri("https://clip.example/s/abc123", "abc123"));
        Assert.Equal(
            new Uri("https://clip.example/api/v1/files/abc123"),
            policy.ValidateCreatedFileUri(
                "https://clip.example/api/v1/files/abc123",
                "abc123"));
    }

    [Theory]
    [InlineData("https://clip.example/s/other")]
    [InlineData("https://clip.example/other/abc123")]
    [InlineData("https://clip.example/s/abc123?leak=1")]
    [InlineData("https://clip.example/s/abc123#fragment")]
    [InlineData("https://other.example/s/abc123")]
    [InlineData("http://clip.example/s/abc123")]
    [InlineData("https://user@clip.example/s/abc123")]
    [InlineData("relative")]
    public void CreatedTextUrlRejectsOriginStructureOrCodeMismatch(string value)
    {
        EndpointPolicy policy = EndpointPolicy.ForRelease(
            new Uri("https://api.example/"),
            new Uri("https://clip.example/"));

        ClipShareException exception = Assert.Throws<ClipShareException>(() =>
            policy.ValidateCreatedShareUri(value, "abc123"));

        Assert.Equal("invalid_response", exception.Code);
    }

    [Theory]
    [InlineData("https://clip.example/s/abc123")]
    [InlineData("https://clip.example/api/v1/files/other")]
    [InlineData("https://clip.example/api/v1/files/abc123?leak=1")]
    [InlineData("https://clip.example/api/v1/files/abc123#fragment")]
    [InlineData("https://other.example/api/v1/files/abc123")]
    public void CreatedFileUrlRejectsOriginStructureOrCodeMismatch(string value)
    {
        EndpointPolicy policy = EndpointPolicy.ForRelease(
            new Uri("https://api.example/"),
            new Uri("https://clip.example/"));

        ClipShareException exception = Assert.Throws<ClipShareException>(() =>
            policy.ValidateCreatedFileUri(value, "abc123"));

        Assert.Equal("invalid_response", exception.Code);
    }
}
