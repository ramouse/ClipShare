using ClipShare.Windows.Application;
using ClipShare.Windows.Platform;

namespace ClipShare.Windows.W1.Tests;

public sealed class PlatformFileTests
{
    [Fact]
    public async Task AtomicTargetCommitsOnlyCompletedContent()
    {
        string root = RequireSandboxRoot();
        string caseDirectory = Path.Combine(root, $"atomic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(caseDirectory);
        string destination = Path.Combine(caseDirectory, "download.bin");
        try
        {
            await using AtomicDownloadTarget target = new(destination);
            await using Stream stream = await target.OpenWriteAsync(TestContext.Current.CancellationToken);
            await stream.WriteAsync(new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken);
            await target.CommitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(caseDirectory, "*.clipshare-part"));
        }
        finally
        {
            Directory.Delete(caseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task AtomicTargetDeletesPartialOnAbort()
    {
        string root = RequireSandboxRoot();
        string caseDirectory = Path.Combine(root, $"abort-{Guid.NewGuid():N}");
        Directory.CreateDirectory(caseDirectory);
        string destination = Path.Combine(caseDirectory, "download.bin");
        try
        {
            await using AtomicDownloadTarget target = new(destination);
            await using Stream stream = await target.OpenWriteAsync(TestContext.Current.CancellationToken);
            await stream.WriteAsync(new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken);
            await target.AbortAsync(TestContext.Current.CancellationToken);
            Assert.False(File.Exists(destination));
            Assert.Empty(Directory.GetFiles(caseDirectory));
        }
        finally
        {
            Directory.Delete(caseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task UploadFileRejectsChangedSourceBeforeRetry()
    {
        string root = RequireSandboxRoot();
        string caseDirectory = Path.Combine(root, $"upload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(caseDirectory);
        string source = Path.Combine(caseDirectory, "source.bin");
        try
        {
            await File.WriteAllBytesAsync(source, new byte[] { 1 }, TestContext.Current.CancellationToken);
            LocalUploadFile upload = new(source);
            await File.WriteAllBytesAsync(source, new byte[] { 1, 2 }, TestContext.Current.CancellationToken);
            ClipShareException exception = await Assert.ThrowsAsync<ClipShareException>(async () =>
            {
                await upload.OpenReadAsync(TestContext.Current.CancellationToken);
            });
            Assert.Equal("file_changed", exception.Code);
        }
        finally
        {
            Directory.Delete(caseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task UploadFileRejectsSameLengthAndTimestampContentReplacementBeforeRetry()
    {
        string root = RequireSandboxRoot();
        string caseDirectory = Path.Combine(root, $"upload-digest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(caseDirectory);
        string source = Path.Combine(caseDirectory, "source.bin");
        try
        {
            await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3, 4 }, TestContext.Current.CancellationToken);
            LocalUploadFile upload = new(source);
            await using (Stream first = await upload.OpenReadAsync(TestContext.Current.CancellationToken))
            {
                Assert.Equal(1, first.ReadByte());
            }

            DateTime originalTimestamp = File.GetLastWriteTimeUtc(source);
            await File.WriteAllBytesAsync(source, new byte[] { 4, 3, 2, 1 }, TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(source, originalTimestamp);

            ClipShareException exception = await Assert.ThrowsAsync<ClipShareException>(async () =>
            {
                await upload.OpenReadAsync(TestContext.Current.CancellationToken);
            });
            Assert.Equal("file_changed", exception.Code);
        }
        finally
        {
            Directory.Delete(caseDirectory, recursive: true);
        }
    }

    [Fact]
    public void UploadAdapterRejectsOversizedFileBeforeHashing()
    {
        string root = RequireSandboxRoot();
        string source = Path.Combine(root, $"oversized-{Guid.NewGuid():N}.bin");
        try
        {
            using (FileStream stream = new(source, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(ClipShareValidation.MaxUploadBytes + 1);
            }

            ClipShareException exception = Assert.Throws<ClipShareException>(() => new LocalUploadFile(source));
            Assert.Equal("file_size_limit_exceeded", exception.Code);
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public async Task LocalFilePortOpensStableUploadAndCreatesTarget()
    {
        string root = RequireSandboxRoot();
        string caseDirectory = Path.Combine(root, $"port-{Guid.NewGuid():N}");
        Directory.CreateDirectory(caseDirectory);
        string source = Path.Combine(caseDirectory, "source.bin");
        string destination = Path.Combine(caseDirectory, "destination.bin");
        try
        {
            await File.WriteAllBytesAsync(source, [4, 5, 6], TestContext.Current.CancellationToken);
            LocalFilePort port = new();
            IUploadFile upload = port.OpenUpload(source);
            Assert.Equal("source.bin", upload.DisplayName);
            Assert.Equal("application/octet-stream", upload.ContentType);
            Assert.Equal(3, upload.Length);
            await using Stream input = await upload.OpenReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(4, input.ReadByte());

            await using AtomicDownloadTarget target = Assert.IsType<AtomicDownloadTarget>(
                port.CreateDownloadTarget(destination));
            await using Stream output = await target.OpenWriteAsync(TestContext.Current.CancellationToken);
            await output.WriteAsync(new byte[] { 7 }, TestContext.Current.CancellationToken);
            await target.CommitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(new byte[] { 7 }, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(caseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task PlatformAdaptersRejectMissingPathsAndInvalidLifecycle()
    {
        string root = RequireSandboxRoot();
        string missing = Path.Combine(root, $"missing-{Guid.NewGuid():N}", "file.bin");
        Assert.Throws<ClipShareException>(() => new LocalUploadFile(missing));
        Assert.Throws<ClipShareException>(() => new AtomicDownloadTarget(missing));

        string destination = Path.Combine(root, $"lifecycle-{Guid.NewGuid():N}.bin");
        AtomicDownloadTarget target = new(destination);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await target.CommitAsync(TestContext.Current.CancellationToken));
        await target.DisposeAsync();
    }

    [Fact]
    public async Task AtomicTargetCannotBeOpenedTwice()
    {
        string root = RequireSandboxRoot();
        string destination = Path.Combine(root, $"double-open-{Guid.NewGuid():N}.bin");
        await using AtomicDownloadTarget target = new(destination);
        await using Stream stream = await target.OpenWriteAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await target.OpenWriteAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void UploadAdapterRejectsDirectoryAsNonRegularFile()
    {
        string root = RequireSandboxRoot();
        ClipShareException exception = Assert.Throws<ClipShareException>(() =>
            new LocalUploadFile(root));
        Assert.Equal("file_not_regular", exception.Code);
    }

    private static string RequireSandboxRoot()
    {
        string? root = Environment.GetEnvironmentVariable("CLIPSHARE_TEST_OUTPUT");
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || !Directory.Exists(root))
        {
            throw new InvalidOperationException("CLIPSHARE_TEST_OUTPUT must point to an existing sandbox directory.");
        }

        return root;
    }
}
