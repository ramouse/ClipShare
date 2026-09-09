namespace ClipShare.Windows.C2.Tests;

using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using ClipShare.Windows.Platform;
using ClipShare.Windows.Vault;

public sealed class VaultPlatformTests
{
    [Fact]
    public void MemoryGuardClearsOnLockExitAndTenMinuteBackgroundLimit()
    {
        Assert.Throws<ArgumentNullException>(() => new VaultMemoryGuard(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VaultMemoryGuard(() => { }, TimeSpan.FromTicks(-1)));

        var clears = 0;
        var guard = new VaultMemoryGuard(() => clears++);
        DateTimeOffset start = new(2026, 9, 2, 0, 0, 0, TimeSpan.Zero);
        guard.OnForeground(start);
        guard.OnBackground(start);
        guard.OnForeground(start.AddMinutes(9));
        Assert.Equal(0, clears);
        guard.OnBackground(start);
        guard.OnForeground(start.AddMinutes(10));
        guard.OnSystemLocked();
        guard.OnProcessExit();
        Assert.Equal(3, clears);
    }

    [Fact]
    public void WindowsSessionGuardRoutesLifecycleEventsIntoTheMemoryGuard()
    {
        Assert.Throws<ArgumentNullException>(() => new WindowsVaultSessionGuard(null!));

        var clears = 0;
        var sessionGuard = new WindowsVaultSessionGuard(new VaultMemoryGuard(() => clears++));
        DateTimeOffset start = new(2026, 9, 2, 0, 0, 0, TimeSpan.Zero);
        sessionGuard.OnAppSuspending(start);
        sessionGuard.OnAppResuming(start.AddMinutes(10));
        sessionGuard.OnSessionLocked();
        sessionGuard.OnProcessExit();
        Assert.Equal(3, clears);
    }

    [Fact]
    public async Task WrapperStoreWritesAtomicallyAndStatePromotionKeepsStableDigest()
    {
        string directory = CaseDirectory("wrapper");
        try
        {
            var store = new WindowsPlatformWrapperStore(directory);
            WindowsPlatformWrapper staged = Wrapper("STAGED");
            string digest = WindowsPlatformWrapperStore.Digest(staged);
            Assert.Throws<InvalidDataException>(() => WindowsPlatformWrapperStore.Digest(
                staged with { InitializationId = "00000000-0000-0000-0000-000000000000" }));
            await store.WriteAtomicallyAsync(staged, TestContext.Current.CancellationToken);
            WrapperObservation? observation = await store.ObserveAsync(
                VaultId.Parse(staged.VaultId),
                staged.KeyEpoch,
                TestContext.Current.CancellationToken);
            Assert.Equal(InitializationPhase.Staged, Assert.IsType<WrapperObservation>(observation).Phase);
            Assert.Equal(digest, observation.Digest);

            WindowsPlatformWrapper ready = staged with { State = "READY" };
            await store.WriteAtomicallyAsync(ready, TestContext.Current.CancellationToken);
            Assert.Equal(digest, WindowsPlatformWrapperStore.Digest(ready));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.WriteAtomicallyAsync(staged, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAtomicallyAsync(
                ready with { ProtectedSecret = ready.ProtectedSecret + "AA" },
                TestContext.Current.CancellationToken));

            string stale = Path.Combine(
                directory,
                $".{staged.VaultId}.{staged.KeyEpoch}.wrapper.json.{Guid.NewGuid():D}.tmp");
            await File.WriteAllTextAsync(stale, "stale", TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.WriteAtomicallyAsync(ready, TestContext.Current.CancellationToken));
            File.Delete(stale);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BlobStoreIsBoundedImmutableAtomicAndDigestChecked()
    {
        string directory = CaseDirectory("blob");
        try
        {
            var store = new WindowsEncryptedBlobStore(directory);
            byte[] ciphertext = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
            StoredEncryptedChunk stored = await store.WriteChunkAtomicallyAsync(
                "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
                "bbbbbbbb-cccc-4ddd-8eee-ffffffffffff",
                0,
                ciphertext,
                TestContext.Current.CancellationToken);
            Assert.Equal(ciphertext, await store.ReadChunkAsync(stored, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadChunkAsync(
                stored with { FileId = "cccccccc-dddd-4eee-8fff-000000000001" },
                TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadChunkAsync(
                stored with { ChunkIndex = -1 },
                TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<IOException>(() => store.WriteChunkAtomicallyAsync(
                stored.FileId,
                stored.GenerationId,
                stored.ChunkIndex,
                ciphertext,
                TestContext.Current.CancellationToken));

            string path = Path.Combine(directory, stored.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            byte[] changed = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            changed[0] ^= 1;
            await File.WriteAllBytesAsync(path, changed, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.ReadChunkAsync(stored, TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FileMaterialUsesGenerationScopedPrefixAndCounterAndIsBounded()
    {
        using FileEncryptionMaterial material = FileEncryptionMaterial.Create();
        NonceAllocation first = material.AllocateChunk(0);
        NonceAllocation last = material.AllocateChunk(long.MaxValue);
        Assert.Equal(first.Prefix, last.Prefix);
        Assert.Equal(0, first.Counter);
        Assert.Equal(long.MaxValue, last.Counter);
        Assert.Throws<ArgumentOutOfRangeException>(() => material.AllocateChunk(-1));

        var context = new VaultCryptoContext(
            CryptoPurpose.FileChunk,
            VaultId.Parse("00112233-4455-4677-8899-aabbccddeeff"),
            DeviceId.Parse("11111111-2222-4333-8444-555555555555"),
            "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
            "chunk",
            1);
        byte[] dek = material.CopyFileDek();
        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => VaultCryptography.EncryptFileChunk(
                dek,
                context,
                first,
                new byte[FileEncryptionMaterial.MaximumPlaintextChunkBytes + 1]));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void DpapiCurrentUserRoundTripsOnlyOnWindowsTemporaryVm()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var secret = Enumerable.Range(0, EpochSecret.Size).Select(value => (byte)value).ToArray();
        VaultId vaultId = VaultId.Parse("00112233-4455-4677-8899-aabbccddeeff");
        const string initializationId = "9a000000-0000-4000-8000-000000000001";
        Assert.Throws<ArgumentException>(() =>
            WindowsDpapiVaultKeyProtector.Protect(vaultId, initializationId, 1, secret.AsSpan(0, EpochSecret.Size - 1)));
        Assert.Throws<ArgumentException>(() =>
            WindowsDpapiVaultKeyProtector.Protect(vaultId, initializationId.ToUpperInvariant(), 1, secret));
        Assert.Throws<ArgumentException>(() => WindowsDpapiVaultKeyProtector.Protect(
            vaultId,
            "00000000-0000-0000-0000-000000000000",
            1,
            secret));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowsDpapiVaultKeyProtector.Protect(vaultId, initializationId, 0, secret));

        WindowsProtectedSecret protectedSecret = WindowsDpapiVaultKeyProtector.Protect(vaultId, initializationId, 1, secret);
        Assert.NotEqual(Convert.ToBase64String(secret), protectedSecret.ProtectedSecret);
        Assert.Equal(secret, WindowsDpapiVaultKeyProtector.Unprotect(vaultId, initializationId, 1, protectedSecret));
        Assert.Throws<ArgumentNullException>(() =>
            WindowsDpapiVaultKeyProtector.Unprotect(vaultId, initializationId, 1, null!));
        Assert.Throws<ArgumentException>(() =>
            WindowsDpapiVaultKeyProtector.Unprotect(vaultId, initializationId, 1, new WindowsProtectedSecret(string.Empty)));
        Assert.Throws<InvalidDataException>(() =>
            WindowsDpapiVaultKeyProtector.Unprotect(vaultId, initializationId, 1, new WindowsProtectedSecret("*")));
        Assert.Throws<InvalidDataException>(() =>
            WindowsDpapiVaultKeyProtector.Unprotect(vaultId, initializationId, 1, new WindowsProtectedSecret("A")));
        Assert.Throws<InvalidDataException>(() =>
            WindowsDpapiVaultKeyProtector.Unprotect(vaultId, initializationId, 1, new WindowsProtectedSecret("AB")));
        Assert.Throws<CryptographicException>(() => WindowsDpapiVaultKeyProtector.Unprotect(
            vaultId,
            initializationId,
            1,
            new WindowsProtectedSecret(Convert.ToBase64String(new byte[64]).TrimEnd('=').Replace('+', '-').Replace('/', '_'))));
        Assert.Throws<CryptographicException>(() =>
            WindowsDpapiVaultKeyProtector.Unprotect(vaultId, initializationId, 2, protectedSecret));
    }

    private static WindowsPlatformWrapper Wrapper(string state) => new(
        1,
        "WINDOWS",
        "DPAPI-CURRENT-USER",
        "00112233-4455-4677-8899-aabbccddeeff",
        "90000000-0000-4000-8000-000000000001",
        1,
        state,
        null,
        null,
        Convert.ToBase64String(Encoding.UTF8.GetBytes("protected-material")).TrimEnd('=').Replace('+', '-').Replace('/', '_'));

    private static string CaseDirectory(string name)
    {
        string? root = Environment.GetEnvironmentVariable("CLIPSHARE_TEST_OUTPUT");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            throw new InvalidOperationException("CLIPSHARE_TEST_OUTPUT must point to an existing sandbox directory.");
        }

        string directory = Path.Combine(root, $"c2-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
