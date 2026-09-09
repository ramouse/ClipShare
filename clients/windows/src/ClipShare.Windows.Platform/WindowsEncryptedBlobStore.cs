namespace ClipShare.Windows.Platform;

using System.Security.Cryptography;

public sealed record StoredEncryptedChunk(
    string FileId,
    string GenerationId,
    long ChunkIndex,
    long CiphertextBytes,
    string CiphertextSha256,
    string RelativePath);

public sealed class WindowsEncryptedBlobStore
{
    private const int MaximumCiphertextChunkBytes = 1_048_576 + 16;
    private readonly string root;
    private readonly string rootPrefix;

    public WindowsEncryptedBlobStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        this.root = Path.GetFullPath(root);
        rootPrefix = this.root.EndsWith(Path.DirectorySeparatorChar)
            ? this.root
            : this.root + Path.DirectorySeparatorChar;
    }

    public async Task<StoredEncryptedChunk> WriteChunkAtomicallyAsync(
        string fileId,
        string generationId,
        long chunkIndex,
        ReadOnlyMemory<byte> ciphertextAndTag,
        CancellationToken cancellationToken = default)
    {
        ValidateUuid(fileId, nameof(fileId));
        ValidateUuid(generationId, nameof(generationId));
        ArgumentOutOfRangeException.ThrowIfNegative(chunkIndex);

        if (ciphertextAndTag.Length is < 16 or > MaximumCiphertextChunkBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(ciphertextAndTag), "Encrypted chunks must contain a tag and be bounded to one MiB plaintext.");
        }

        string relative = RelativePath(fileId, generationId, chunkIndex);
        string destination = ResolveRelative(relative);
        string? parent = Path.GetDirectoryName(destination);
        if (parent is null)
        {
            throw new InvalidOperationException("Encrypted chunk has no parent directory.");
        }

        Directory.CreateDirectory(parent);
        string temporary = Path.Combine(parent, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                65_536,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(ciphertextAndTag, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination, overwrite: false);
            return new StoredEncryptedChunk(
                fileId,
                generationId,
                chunkIndex,
                ciphertextAndTag.Length,
                Convert.ToHexStringLower(SHA256.HashData(ciphertextAndTag.Span)),
                relative);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }

    public async Task<byte[]> ReadChunkAsync(
        StoredEncryptedChunk chunk,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ValidateUuid(chunk.FileId, nameof(chunk));
        ValidateUuid(chunk.GenerationId, nameof(chunk));
        if (chunk.ChunkIndex < 0)
        {
            throw new InvalidDataException("Encrypted chunk index is outside the allowed bounds.");
        }

        if (chunk.CiphertextBytes is < 16 or > MaximumCiphertextChunkBytes)
        {
            throw new InvalidDataException("Encrypted chunk length is outside the allowed bounds.");
        }

        if (!string.Equals(
            chunk.RelativePath,
            RelativePath(chunk.FileId, chunk.GenerationId, chunk.ChunkIndex),
            StringComparison.Ordinal))
        {
            throw new InvalidDataException("Encrypted chunk path does not match its manifest identity.");
        }

        string path = ResolveRelative(chunk.RelativePath);
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            65_536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != chunk.CiphertextBytes)
        {
            throw new InvalidDataException("Encrypted chunk length does not match its manifest.");
        }

        var result = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(result, cancellationToken).ConfigureAwait(false);
        string digest = Convert.ToHexStringLower(SHA256.HashData(result));
        if (!string.Equals(digest, chunk.CiphertextSha256, StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(result);
            throw new InvalidDataException("Encrypted chunk digest does not match its manifest.");
        }

        return result;
    }

    private string ResolveRelative(string relative)
    {
        if (Path.IsPathRooted(relative))
        {
            throw new InvalidDataException("Encrypted chunk path must be relative.");
        }

        string candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Encrypted chunk path escapes the Vault blob root.");
        }

        return candidate;
    }

    private static void ValidateUuid(string value, string parameterName)
    {
        if (!Guid.TryParseExact(value, "D", out Guid parsed) ||
            !string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal))
        {
            throw new ArgumentException("Blob identifiers must be lowercase UUIDs.", parameterName);
        }
    }

    private static string RelativePath(string fileId, string generationId, long chunkIndex) =>
        $"{fileId}/{generationId}/chunk-{chunkIndex:D19}.bin";
}
