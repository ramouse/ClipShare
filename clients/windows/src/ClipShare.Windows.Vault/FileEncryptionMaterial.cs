namespace ClipShare.Windows.Vault;

using System.Security.Cryptography;

public sealed class FileEncryptionMaterial : IDisposable
{
    public const int MaximumPlaintextChunkBytes = 1_048_576;
    private readonly byte[] fileDek;
    private readonly byte[] noncePrefix;
    private bool disposed;

    private FileEncryptionMaterial(byte[] fileDek, byte[] noncePrefix)
    {
        this.fileDek = fileDek;
        this.noncePrefix = noncePrefix;
    }

    public static FileEncryptionMaterial Create() => new(
        RandomNumberGenerator.GetBytes(32),
        RandomNumberGenerator.GetBytes(4));

    public byte[] CopyFileDek()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return fileDek.ToArray();
    }

    public NonceAllocation AllocateChunk(long chunkIndex)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(chunkIndex);

        return new NonceAllocation(noncePrefix, chunkIndex);
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(fileDek);
        CryptographicOperations.ZeroMemory(noncePrefix);
        disposed = true;
    }
}
