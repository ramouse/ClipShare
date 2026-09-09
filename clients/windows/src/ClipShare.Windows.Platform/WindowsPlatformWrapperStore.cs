namespace ClipShare.Windows.Platform;

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClipShare.Windows.Vault;

public sealed record WindowsPlatformWrapper(
    int SchemaVersion,
    string Platform,
    string Algorithm,
    string VaultId,
    string InitializationId,
    long KeyEpoch,
    string State,
    string? KeyReference,
    string? Nonce,
    string ProtectedSecret);

public sealed class WindowsPlatformWrapperStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
    };

    private readonly string directory;

    public WindowsPlatformWrapperStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = Path.GetFullPath(directory);
    }

    public async Task<WindowsPlatformWrapper?> ReadAsync(
        VaultId vaultId,
        long keyEpoch,
        CancellationToken cancellationToken = default)
    {
        string path = WrapperPath(vaultId, keyEpoch);
        if (!File.Exists(path))
        {
            return null;
        }

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16_384,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        WindowsPlatformWrapper wrapper = await JsonSerializer.DeserializeAsync<WindowsPlatformWrapper>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Platform wrapper is empty.");
        Validate(wrapper);
        return wrapper;
    }

    public async Task<WrapperObservation?> ObserveAsync(
        VaultId vaultId,
        long keyEpoch,
        CancellationToken cancellationToken = default)
    {
        WindowsPlatformWrapper? wrapper = await ReadAsync(vaultId, keyEpoch, cancellationToken).ConfigureAwait(false);
        if (wrapper is null)
        {
            return null;
        }

        return new WrapperObservation(
            VaultId.Parse(wrapper.VaultId),
            wrapper.InitializationId,
            wrapper.KeyEpoch,
            Digest(wrapper),
            wrapper.State == "STAGED" ? InitializationPhase.Staged : InitializationPhase.Ready);
    }

    public async Task WriteAtomicallyAsync(
        WindowsPlatformWrapper wrapper,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wrapper);
        Validate(wrapper);
        Directory.CreateDirectory(directory);
        VaultId vaultId = VaultId.Parse(wrapper.VaultId);
        string destination = WrapperPath(vaultId, wrapper.KeyEpoch);
        WindowsPlatformWrapper? existing = await ReadAsync(vaultId, wrapper.KeyEpoch, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if ((existing with { State = wrapper.State }) != wrapper)
            {
                throw new InvalidOperationException("An existing epoch wrapper cannot be replaced with different key material.");
            }

            if (existing.State == "READY" && wrapper.State != "READY")
            {
                throw new InvalidOperationException("A READY epoch wrapper cannot be downgraded.");
            }
        }

        string temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{wrapper.InitializationId}.tmp");
        string temporaryPattern = $".{Path.GetFileName(destination)}.*.tmp";
        if (Directory.EnumerateFiles(directory, temporaryPattern, SearchOption.TopDirectoryOnly).Any())
        {
            throw new InvalidOperationException("A stale wrapper temporary file requires recovery.");
        }

        try
        {
            await using (FileStream stream = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16_384,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, wrapper, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }

    public static string Digest(WindowsPlatformWrapper wrapper)
    {
        ArgumentNullException.ThrowIfNull(wrapper);
        Validate(wrapper);
        byte[] protectedBytes = DecodeBase64Url(wrapper.ProtectedSecret);
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(protectedBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private string WrapperPath(VaultId vaultId, long keyEpoch)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(keyEpoch);

        return Path.Combine(directory, $"{vaultId.Value}.{keyEpoch}.wrapper.json");
    }

    private static void Validate(WindowsPlatformWrapper wrapper)
    {
        if (wrapper.SchemaVersion != 1 ||
            !string.Equals(wrapper.Platform, "WINDOWS", StringComparison.Ordinal) ||
            !string.Equals(wrapper.Algorithm, "DPAPI-CURRENT-USER", StringComparison.Ordinal) ||
            wrapper.KeyReference is not null ||
            wrapper.Nonce is not null ||
            wrapper.KeyEpoch <= 0 ||
            wrapper.State is not ("STAGED" or "READY"))
        {
            throw new InvalidDataException("Unsupported Windows platform wrapper.");
        }

        _ = VaultId.Parse(wrapper.VaultId);
        try
        {
            _ = VaultId.Parse(wrapper.InitializationId);
        }
        catch (ArgumentException error)
        {
            throw new InvalidDataException("Invalid wrapper initialization ID.", error);
        }

        _ = DecodeBase64Url(wrapper.ProtectedSecret);
    }

    private static byte[] DecodeBase64Url(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        if (value.Length % 4 == 1 || value.Any(character =>
            !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
        {
            throw new InvalidDataException("Invalid Base64URL.");
        }

        string padded = value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '=');
        byte[] result;
        try
        {
            result = Convert.FromBase64String(padded);
        }
        catch (FormatException error)
        {
            throw new InvalidDataException("Invalid Base64URL.", error);
        }

        string canonical = Convert.ToBase64String(result).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (!string.Equals(canonical, value, StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(result);
            throw new InvalidDataException("Non-canonical Base64URL.");
        }

        return result;
    }
}
