namespace ClipShare.Windows.Vault;

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

public enum CryptoPurpose
{
    FolderMetadata,
    ItemMetadata,
    ItemPayload,
    FileKeyWrap,
    FileManifest,
    FileChunk,
    EventBody,
}

public static class CryptoPurposeExtensions
{
    public static string ToWireValue(this CryptoPurpose purpose) => purpose switch
    {
        CryptoPurpose.FolderMetadata => "folder-metadata",
        CryptoPurpose.ItemMetadata => "item-metadata",
        CryptoPurpose.ItemPayload => "item-payload",
        CryptoPurpose.FileKeyWrap => "file-key-wrap",
        CryptoPurpose.FileManifest => "file-manifest",
        CryptoPurpose.FileChunk => "file-chunk",
        CryptoPurpose.EventBody => "event-body",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
    };

    public static CryptoPurpose FromWireValue(string value) => value switch
    {
        "folder-metadata" => CryptoPurpose.FolderMetadata,
        "item-metadata" => CryptoPurpose.ItemMetadata,
        "item-payload" => CryptoPurpose.ItemPayload,
        "file-key-wrap" => CryptoPurpose.FileKeyWrap,
        "file-manifest" => CryptoPurpose.FileManifest,
        "file-chunk" => CryptoPurpose.FileChunk,
        "event-body" => CryptoPurpose.EventBody,
        _ => throw new InvalidDataException("Unknown Vault purpose."),
    };
}

public sealed record NonceAllocation
{
    public NonceAllocation(ReadOnlySpan<byte> prefix, long counter)
    {
        if (prefix.Length != 4)
        {
            throw new ArgumentException("Nonce prefixes are exactly four bytes.", nameof(prefix));
        }

        if (counter < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(counter), "Nonce counters cannot be negative.");
        }

        Prefix = prefix.ToArray();
        Counter = counter;
    }

    public byte[] Prefix { get; }

    public long Counter { get; }

    public byte[] GetNonce()
    {
        var result = new byte[12];
        Prefix.CopyTo(result, 0);
        BinaryPrimitives.WriteInt64BigEndian(result.AsSpan(4), Counter);
        return result;
    }
}

public sealed record VaultCryptoContext(
    CryptoPurpose Purpose,
    VaultId VaultId,
    DeviceId OriginDeviceId,
    string EntityId,
    string Field,
    long KeyEpoch)
{
    public VaultCryptoContext Validate()
    {
        _ = Purpose.ToWireValue();
        _ = VaultUuid.Validate(VaultId.Value);
        _ = VaultUuid.Validate(OriginDeviceId.Value);
        _ = VaultUuid.Validate(EntityId);
        ArgumentNullException.ThrowIfNull(Field);
        if (Field.Length is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(Field), "Vault field names contain one to 32 characters.");
        }

        if (!IsLowerAsciiLetter(Field[0]) || Field.Skip(1).Any(character =>
            !IsLowerAsciiLetter(character) && !char.IsAsciiDigit(character) && character != '-'))
        {
            throw new ArgumentException("Invalid Vault field name.", nameof(Field));
        }

        if (KeyEpoch <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(KeyEpoch), "Vault key epochs are positive.");
        }

        return this;
    }

    private static bool IsLowerAsciiLetter(char value) => value is >= 'a' and <= 'z';
}

public sealed record VaultCipherEnvelope(
    string Purpose,
    string VaultId,
    string OriginDeviceId,
    string EntityId,
    string Field,
    long KeyEpoch,
    long NonceCounter,
    string Nonce,
    long PaddedPlaintextBytes,
    string CipherAndTag);

public static class VaultCryptography
{
    private const int PaddingBucket = 4096;
    private static readonly byte[] KeyPrefix = Encoding.ASCII.GetBytes("clipshare:vault:key:v1\0");
    private static readonly byte[] AadPrefix = Encoding.ASCII.GetBytes("clipshare:vault:aad:v1\0");

    public static byte[] DeriveKey(ReadOnlySpan<byte> epochSecret, VaultCryptoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Validate();
        if (epochSecret.Length != EpochSecret.Size)
        {
            throw new ArgumentException("Epoch secrets are exactly 32 bytes.", nameof(epochSecret));
        }

        if (context.Purpose == CryptoPurpose.FileChunk)
        {
            throw new ArgumentException("File chunks use an independent File DEK.", nameof(context));
        }

        var salt = UuidBytes(context.VaultId.Value);
        var prk = HMACSHA256.HashData(salt, epochSecret);
        try
        {
            var info = Join(KeyPrefix, LengthPrefixed(context.Purpose.ToWireValue()), UuidBytes(context.OriginDeviceId.Value));
            return HMACSHA256.HashData(prk, Join(info, new byte[] { 1 }));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(prk);
        }
    }

    public static VaultCipherEnvelope EncryptRecord(
        ReadOnlySpan<byte> epochSecret,
        VaultCryptoContext context,
        NonceAllocation allocation,
        ReadOnlySpan<byte> payload)
    {
        if (context.Purpose == CryptoPurpose.FileChunk)
        {
            throw new ArgumentException("Use EncryptFileChunk for file bytes.", nameof(context));
        }

        if (allocation.Counter <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(allocation), "Record nonce counters start at one.");
        }

        var plaintext = Frame(payload);
        var key = DeriveKey(epochSecret, context);
        try
        {
            return Encrypt(key, context, allocation, plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static byte[] DecryptRecord(ReadOnlySpan<byte> epochSecret, VaultCipherEnvelope envelope)
    {
        var context = ContextFrom(envelope);
        if (context.Purpose == CryptoPurpose.FileChunk)
        {
            throw new ArgumentException("Use DecryptFileChunk for file bytes.", nameof(envelope));
        }

        if (envelope.NonceCounter <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(envelope), "Record nonce counters start at one.");
        }

        var key = DeriveKey(epochSecret, context);
        try
        {
            return Unframe(Decrypt(key, context, envelope));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static VaultCipherEnvelope EncryptFileChunk(
        ReadOnlySpan<byte> fileDek,
        VaultCryptoContext context,
        NonceAllocation allocation,
        ReadOnlySpan<byte> plaintext)
    {
        if (fileDek.Length != 32 || context.Purpose != CryptoPurpose.FileChunk)
        {
            throw new ArgumentException("A file chunk requires a 32-byte File DEK and file-chunk purpose.");
        }

        if (plaintext.Length > FileEncryptionMaterial.MaximumPlaintextChunkBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(plaintext), "File chunks are bounded to one MiB.");
        }

        return Encrypt(fileDek, context, allocation, plaintext);
    }

    public static byte[] DecryptFileChunk(ReadOnlySpan<byte> fileDek, VaultCipherEnvelope envelope)
    {
        var context = ContextFrom(envelope);
        if (fileDek.Length != 32 || context.Purpose != CryptoPurpose.FileChunk)
        {
            throw new ArgumentException("A file chunk requires a 32-byte File DEK and file-chunk purpose.");
        }

        return Decrypt(fileDek, context, envelope);
    }

    public static byte[] BuildAad(VaultCryptoContext context, long counter, long paddedLength)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Validate();
        return Join(
            AadPrefix,
            U16(1),
            LengthPrefixed(context.Purpose.ToWireValue()),
            UuidBytes(context.VaultId.Value),
            UuidBytes(context.OriginDeviceId.Value),
            UuidBytes(context.EntityId),
            LengthPrefixed(context.Field),
            U64(context.KeyEpoch),
            U64(counter),
            U64(paddedLength));
    }

    private static VaultCipherEnvelope Encrypt(
        ReadOnlySpan<byte> key,
        VaultCryptoContext context,
        NonceAllocation allocation,
        ReadOnlySpan<byte> plaintext)
    {
        var nonce = allocation.GetNonce();
        var aad = BuildAad(context, allocation.Counter, plaintext.Length);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        return new VaultCipherEnvelope(
            context.Purpose.ToWireValue(),
            context.VaultId.Value,
            context.OriginDeviceId.Value,
            context.EntityId,
            context.Field,
            context.KeyEpoch,
            allocation.Counter,
            Base64Url(nonce),
            plaintext.Length,
            Base64Url(Join(ciphertext, tag)));
    }

    private static byte[] Decrypt(
        ReadOnlySpan<byte> key,
        VaultCryptoContext context,
        VaultCipherEnvelope envelope)
    {
        var nonce = DecodeBase64Url(envelope.Nonce);
        var combined = DecodeBase64Url(envelope.CipherAndTag);
        if (nonce.Length != 12 || combined.Length < 16)
        {
            throw new InvalidDataException("Invalid Vault ciphertext shape.");
        }

        var ciphertext = combined.AsSpan(0, combined.Length - 16);
        if (envelope.PaddedPlaintextBytes != ciphertext.Length)
        {
            throw new InvalidDataException("Declared plaintext length does not match the ciphertext.");
        }

        if (context.Purpose == CryptoPurpose.FileChunk)
        {
            if (ciphertext.Length > FileEncryptionMaterial.MaximumPlaintextChunkBytes)
            {
                throw new InvalidDataException("File chunks are bounded to one MiB.");
            }
        }
        else if (ciphertext.Length < PaddingBucket || ciphertext.Length % PaddingBucket != 0)
        {
            throw new InvalidDataException("Invalid padded record length.");
        }

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(
            nonce,
            ciphertext,
            combined.AsSpan(combined.Length - 16),
            plaintext,
            BuildAad(context, envelope.NonceCounter, envelope.PaddedPlaintextBytes));
        return plaintext;
    }

    private static VaultCryptoContext ContextFrom(VaultCipherEnvelope envelope) => new(
        CryptoPurposeExtensions.FromWireValue(envelope.Purpose),
        VaultId.Parse(envelope.VaultId),
        DeviceId.Parse(envelope.OriginDeviceId),
        VaultUuid.Validate(envelope.EntityId),
        envelope.Field,
        envelope.KeyEpoch);

    private static byte[] Frame(ReadOnlySpan<byte> payload)
    {
        var required = checked(8 + payload.Length);
        var buckets = checked(required + PaddingBucket - 1) / PaddingBucket;
        var length = Math.Max(PaddingBucket, checked(buckets * PaddingBucket));
        var framed = new byte[length];
        BinaryPrimitives.WriteInt64BigEndian(framed, payload.Length);
        payload.CopyTo(framed.AsSpan(8));
        return framed;
    }

    private static byte[] Unframe(byte[] value)
    {
        try
        {
            if (value.Length < PaddingBucket || value.Length % PaddingBucket != 0)
            {
                throw new InvalidDataException("Invalid padded plaintext.");
            }

            var length = BinaryPrimitives.ReadInt64BigEndian(value);
            if (length < 0 || length > value.Length - 8L || length > int.MaxValue)
            {
                throw new InvalidDataException("Invalid framed plaintext length.");
            }

            if (value.AsSpan(8 + (int)length).IndexOfAnyExcept((byte)0) >= 0)
            {
                throw new InvalidDataException("Invalid non-zero padding.");
            }

            return value.AsSpan(8, (int)length).ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    private static byte[] UuidBytes(string value) => Convert.FromHexString(VaultUuid.Validate(value).Replace("-", string.Empty, StringComparison.Ordinal));

    private static byte[] LengthPrefixed(string value)
    {
        var encoded = new UTF8Encoding(false, true).GetBytes(value);
        if (encoded.Length > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "AAD string too long.");
        }

        return Join(U16(encoded.Length), encoded);
    }

    private static byte[] U16(int value)
    {
        var result = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(result, checked((ushort)value));
        return result;
    }

    private static byte[] U64(long value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "AAD integers are non-negative.");
        }

        var result = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(result, value);
        return result;
    }

    private static byte[] Join(params ReadOnlyMemory<byte>[] parts)
    {
        var result = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.Span.CopyTo(result.AsSpan(offset));
            offset += part.Length;
        }

        return result;
    }

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] DecodeBase64Url(string value)
    {
        if (value.Length == 0 || value.Length % 4 == 1 || value.Any(character =>
            !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
        {
            throw new InvalidDataException("Invalid Base64URL.");
        }

        var padded = value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '=');
        byte[] result;
        try
        {
            result = Convert.FromBase64String(padded);
        }
        catch (FormatException error)
        {
            throw new InvalidDataException("Invalid Base64URL.", error);
        }

        if (!string.Equals(Base64Url(result), value, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Non-canonical Base64URL.");
        }

        return result;
    }
}
