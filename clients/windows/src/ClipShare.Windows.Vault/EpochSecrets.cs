namespace ClipShare.Windows.Vault;

using System.Security.Cryptography;

public interface IEpochSecretGenerator
{
    byte[] Generate(int size);
}

public sealed class RandomEpochSecretGenerator : IEpochSecretGenerator
{
    public static RandomEpochSecretGenerator Instance { get; } = new();

    private RandomEpochSecretGenerator()
    {
    }

    public byte[] Generate(int size) => RandomNumberGenerator.GetBytes(size);
}

public sealed class EpochSecret : IDisposable
{
    public const int Size = 32;
    private readonly byte[] value;
    private bool disposed;

    private EpochSecret(byte[] value) => this.value = value;

    public static EpochSecret FromBytes(ReadOnlySpan<byte> value)
    {
        if (value.Length != Size)
        {
            throw new ArgumentException("Epoch secrets are exactly 32 bytes.", nameof(value));
        }

        return new EpochSecret(value.ToArray());
    }

    public byte[] CopyBytes()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return value.ToArray();
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(value);
        disposed = true;
    }
}

public sealed class EpochKeyRing : IDisposable
{
    private const int MaximumGenerationAttempts = 8;
    private readonly Dictionary<long, EpochSecret> secrets;

    public EpochKeyRing(long initialEpoch, EpochSecret initialSecret)
    {
        ArgumentNullException.ThrowIfNull(initialSecret);
        if (initialEpoch <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialEpoch), "Epoch numbers are positive.");
        }

        CurrentWriteEpoch = initialEpoch;
        secrets = new Dictionary<long, EpochSecret> { [initialEpoch] = initialSecret };
    }

    public long CurrentWriteEpoch { get; private set; }

    public EpochSecret GetSecret(long epoch) =>
        secrets.TryGetValue(epoch, out var secret)
            ? secret
            : throw new KeyNotFoundException("The requested epoch secret is unavailable.");

    public long Rotate(IEpochSecretGenerator? generator = null)
    {
        if (CurrentWriteEpoch == long.MaxValue)
        {
            throw new InvalidOperationException("Epoch number exhausted.");
        }

        generator ??= RandomEpochSecretGenerator.Instance;
        for (var attempt = 0; attempt < MaximumGenerationAttempts; attempt++)
        {
            byte[] candidate = generator.Generate(EpochSecret.Size) ??
                throw new InvalidOperationException("Secret generator returned null.");
            try
            {
                if (candidate.Length != EpochSecret.Size)
                {
                    throw new InvalidOperationException("Secret generator returned the wrong length.");
                }

                if (!MatchesExistingSecret(candidate))
                {
                    CurrentWriteEpoch++;
                    secrets[CurrentWriteEpoch] = EpochSecret.FromBytes(candidate);
                    return CurrentWriteEpoch;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(candidate);
            }
        }

        throw new CryptographicException("Secret generator repeated a retained epoch secret.");
    }

    public void RemoveHistoricalEpoch(long epoch, long referenceCount)
    {
        if (referenceCount != 0)
        {
            throw new InvalidOperationException("Referenced epoch secrets cannot be removed.");
        }

        if (epoch == CurrentWriteEpoch)
        {
            throw new InvalidOperationException("The active write epoch cannot be removed.");
        }

        if (!secrets.Remove(epoch, out var secret))
        {
            throw new KeyNotFoundException("The epoch secret is unavailable.");
        }

        secret.Dispose();
    }

    public void Dispose()
    {
        foreach (var secret in secrets.Values)
        {
            secret.Dispose();
        }

        secrets.Clear();
    }

    private bool MatchesExistingSecret(ReadOnlySpan<byte> candidate)
    {
        foreach (EpochSecret secret in secrets.Values)
        {
            byte[] existing = secret.CopyBytes();
            try
            {
                if (CryptographicOperations.FixedTimeEquals(existing, candidate))
                {
                    return true;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(existing);
            }
        }

        return false;
    }
}
