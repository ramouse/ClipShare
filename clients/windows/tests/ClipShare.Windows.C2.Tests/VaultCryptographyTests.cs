namespace ClipShare.Windows.C2.Tests;

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClipShare.Windows.Vault;

public sealed class VaultCryptographyTests
{
    [Fact]
    public void DotNetProviderMatchesSharedItemAndFileVectors()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vectors", "positive-vectors.json")));
        var vectors = document.RootElement.GetProperty("vectors");
        var item = vectors.EnumerateArray().Single(vector => vector.GetProperty("id").GetString() == "epoch1-item-payload-4k");
        var itemContext = Context(item);
        var secret = Convert.FromHexString(item.GetProperty("epochSecretHex").GetString()!);
        var key = VaultCryptography.DeriveKey(secret, itemContext);
        Assert.Equal(item.GetProperty("derivedKeyHex").GetString(), Convert.ToHexString(key).ToLowerInvariant());
        var envelope = VaultCryptography.EncryptRecord(
            secret,
            itemContext,
            new NonceAllocation(
                Convert.FromHexString(item.GetProperty("noncePrefixHex").GetString()!),
                item.GetProperty("nonceCounter").GetInt64()),
            Convert.FromHexString(item.GetProperty("payloadHex").GetString()!));
        var combined = DecodeBase64Url(envelope.CipherAndTag);
        Assert.Equal(
            item.GetProperty("cipherAndTagSha256Hex").GetString(),
            Convert.ToHexString(SHA256.HashData(combined)).ToLowerInvariant());
        Assert.Equal(
            Convert.FromHexString(item.GetProperty("payloadHex").GetString()!),
            VaultCryptography.DecryptRecord(secret, envelope));

        var chunk = vectors.EnumerateArray().Single(vector => vector.GetProperty("id").GetString() == "file-generation-chunk-3");
        var chunkEnvelope = VaultCryptography.EncryptFileChunk(
            Convert.FromHexString(chunk.GetProperty("fileDekHex").GetString()!),
            Context(chunk),
            new NonceAllocation(
                Convert.FromHexString(chunk.GetProperty("noncePrefixHex").GetString()!),
                chunk.GetProperty("nonceCounter").GetInt64()),
            Convert.FromHexString(chunk.GetProperty("payloadHex").GetString()!));
        Assert.Equal(chunk.GetProperty("cipherAndTagB64Url").GetString(), chunkEnvelope.CipherAndTag);
    }

    [Fact]
    public void RotationUsesGeneratorOutputAndRejectsRepeatedSecret()
    {
        using var ring = new EpochKeyRing(1, EpochSecret.FromBytes(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray()));
        var generator = new RepeatingThenIndependentGenerator();
        Assert.Equal(2, ring.Rotate(generator));
        Assert.Equal(2, generator.Calls);
        Assert.NotEqual(Convert.ToHexString(ring.GetSecret(1).CopyBytes()), Convert.ToHexString(ring.GetSecret(2).CopyBytes()));

        var historicalCollision = new HistoricalCollisionThenIndependentGenerator();
        Assert.Equal(3, ring.Rotate(historicalCollision));
        Assert.Equal(2, historicalCollision.Calls);
        Assert.NotEqual(Convert.ToHexString(ring.GetSecret(1).CopyBytes()), Convert.ToHexString(ring.GetSecret(3).CopyBytes()));
        Assert.Throws<InvalidOperationException>(() => ring.RemoveHistoricalEpoch(1, 1));
        ring.RemoveHistoricalEpoch(1, 0);
    }

    [Fact]
    public void AadSubstitutionFailsAuthentication()
    {
        var secret = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var context = new VaultCryptoContext(
            CryptoPurpose.ItemPayload,
            VaultId.Parse("00112233-4455-4677-8899-aabbccddeeff"),
            DeviceId.Parse("11111111-2222-4333-8444-555555555555"),
            "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
            "content",
            1);
        var envelope = VaultCryptography.EncryptRecord(secret, context, new NonceAllocation([1, 2, 3, 4], 1), "secret"u8);
        Assert.Throws<AuthenticationTagMismatchException>(() =>
            VaultCryptography.DecryptRecord(secret, envelope with { Field = "title" }));
    }

    [Fact]
    public void RecordNonceCountersStartAtOne()
    {
        var context = new VaultCryptoContext(
            CryptoPurpose.ItemPayload,
            VaultId.Parse("00112233-4455-4677-8899-aabbccddeeff"),
            DeviceId.Parse("11111111-2222-4333-8444-555555555555"),
            "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
            "content",
            1);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VaultCryptography.EncryptRecord(new byte[32], context, new NonceAllocation(new byte[4], 0), []));
    }

    [Fact]
    public void PurposeWireValuesRoundTripAndRejectUnknownValues()
    {
        foreach (CryptoPurpose purpose in Enum.GetValues<CryptoPurpose>())
        {
            Assert.Equal(purpose, CryptoPurposeExtensions.FromWireValue(purpose.ToWireValue()));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => ((CryptoPurpose)999).ToWireValue());
        Assert.Throws<InvalidDataException>(() => CryptoPurposeExtensions.FromWireValue("unknown-purpose"));
    }

    [Fact]
    public void EpochSecretsAndKeyRingFailClosedAtGenerationBoundaries()
    {
        byte[] generated = RandomEpochSecretGenerator.Instance.Generate(EpochSecret.Size);
        try
        {
            Assert.Equal(EpochSecret.Size, generated.Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(generated);
        }

        Assert.Throws<ArgumentException>(() => EpochSecret.FromBytes(new byte[EpochSecret.Size - 1]));
        using (EpochSecret disposed = EpochSecret.FromBytes(new byte[EpochSecret.Size]))
        {
            disposed.Dispose();
            Assert.Throws<ObjectDisposedException>(() => disposed.CopyBytes());
        }

        using EpochSecret invalidInitial = EpochSecret.FromBytes(new byte[EpochSecret.Size]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new EpochKeyRing(0, invalidInitial));

        using var maximumRing = new EpochKeyRing(
            long.MaxValue,
            EpochSecret.FromBytes(new byte[EpochSecret.Size]));
        Assert.Throws<InvalidOperationException>(() => maximumRing.Rotate());

        byte[] initialBytes = Enumerable.Range(0, EpochSecret.Size).Select(value => (byte)value).ToArray();
        using var ring = new EpochKeyRing(1, EpochSecret.FromBytes(initialBytes));
        Assert.Throws<KeyNotFoundException>(() => ring.GetSecret(2));
        Assert.Throws<InvalidOperationException>(() => ring.Rotate(new WrongLengthGenerator()));
        Assert.Throws<CryptographicException>(() => ring.Rotate(new RepeatingGenerator(initialBytes)));
        Assert.Equal(2, ring.Rotate());
        Assert.Throws<InvalidOperationException>(() => ring.RemoveHistoricalEpoch(1, 1));
        Assert.Throws<InvalidOperationException>(() => ring.RemoveHistoricalEpoch(2, 0));
        Assert.Throws<KeyNotFoundException>(() => ring.RemoveHistoricalEpoch(99, 0));
        ring.RemoveHistoricalEpoch(1, 0);
    }

    [Fact]
    public void CryptoPrimitivesRejectInvalidKeysContextsAndNonceShapes()
    {
        byte[] secret = Enumerable.Range(0, EpochSecret.Size).Select(value => (byte)value).ToArray();
        VaultCryptoContext itemContext = ItemContext();
        VaultCryptoContext fileContext = ItemContext(CryptoPurpose.FileChunk);
        var allocation = new NonceAllocation([1, 2, 3, 4], 1);

        Assert.Throws<ArgumentException>(() => new NonceAllocation(new byte[3], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NonceAllocation(new byte[4], -1));
        Assert.Equal("010203040000000000000001", Convert.ToHexString(allocation.GetNonce()).ToLowerInvariant());
        Assert.Throws<ArgumentException>(() => VaultCryptography.DeriveKey(new byte[31], itemContext));
        Assert.Throws<ArgumentException>(() => VaultCryptography.DeriveKey(secret, fileContext));
        Assert.Throws<ArgumentException>(() => VaultCryptography.EncryptRecord(secret, fileContext, allocation, []));

        VaultCipherEnvelope record = VaultCryptography.EncryptRecord(secret, itemContext, allocation, "record"u8);
        VaultCipherEnvelope chunk = VaultCryptography.EncryptFileChunk(secret, fileContext, allocation, "chunk"u8);
        Assert.Equal("chunk"u8.ToArray(), VaultCryptography.DecryptFileChunk(secret, chunk));
        Assert.Throws<ArgumentException>(() => VaultCryptography.DecryptRecord(secret, chunk));
        Assert.Throws<ArgumentException>(() =>
            VaultCryptography.EncryptFileChunk(new byte[31], fileContext, allocation, []));
        Assert.Throws<ArgumentException>(() =>
            VaultCryptography.EncryptFileChunk(secret, itemContext, allocation, []));
        Assert.Throws<ArgumentException>(() => VaultCryptography.DecryptFileChunk(new byte[31], chunk));
        Assert.Throws<ArgumentException>(() => VaultCryptography.DecryptFileChunk(secret, record));

        Assert.Throws<InvalidDataException>(() =>
            VaultCryptography.DecryptRecord(secret, record with { Nonce = Base64Url(new byte[11]) }));
        Assert.Throws<InvalidDataException>(() =>
            VaultCryptography.DecryptRecord(secret, record with { CipherAndTag = Base64Url(new byte[15]) }));
        foreach (string malformed in new[] { string.Empty, "A", "*", "AB" })
        {
            Assert.Throws<InvalidDataException>(() =>
                VaultCryptography.DecryptRecord(secret, record with { Nonce = malformed }));
        }

        Assert.Throws<ArgumentException>(() =>
            VaultCryptography.DecryptRecord(secret, record with { EntityId = "not-a-uuid" }));
        Assert.Throws<InvalidDataException>(() =>
            VaultCryptography.DecryptRecord(secret, record with { Purpose = "unknown-purpose" }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VaultCryptography.DecryptRecord(secret, record with { NonceCounter = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VaultCryptography.BuildAad(itemContext with { Field = new string('a', ushort.MaxValue + 1) }, 1, 4096));
        Assert.Throws<ArgumentException>(() =>
            VaultCryptography.BuildAad(itemContext with { Field = "Title" }, 1, 4096));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VaultCryptography.BuildAad(itemContext with { Field = string.Empty }, 1, 4096));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VaultCryptography.BuildAad(itemContext with { Field = new string('a', 33) }, 1, 4096));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VaultCryptography.BuildAad(itemContext with { KeyEpoch = 0 }, 1, 4096));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VaultCryptography.BuildAad(itemContext with { KeyEpoch = -1 }, 1, 4096));
        Assert.Throws<ArgumentOutOfRangeException>(() => VaultCryptography.BuildAad(itemContext, -1, 4096));
        Assert.Throws<ArgumentOutOfRangeException>(() => VaultCryptography.BuildAad(itemContext, 1, -1));
        Assert.Throws<InvalidDataException>(() =>
            VaultCryptography.DecryptRecord(secret, record with { PaddedPlaintextBytes = record.PaddedPlaintextBytes + 1 }));
        Assert.Throws<InvalidDataException>(() =>
            VaultCryptography.DecryptFileChunk(secret, chunk with { PaddedPlaintextBytes = chunk.PaddedPlaintextBytes + 1 }));
    }

    [Fact]
    public void RecordFramingRejectsMalformedAuthenticatedPlaintext()
    {
        byte[] secret = Enumerable.Range(0, EpochSecret.Size).Select(value => (byte)value).ToArray();
        VaultCryptoContext context = ItemContext();
        var allocation = new NonceAllocation([4, 3, 2, 1], 9);

        Assert.Throws<InvalidDataException>(() =>
            VaultCryptography.DecryptRecord(secret, EncryptRawRecordFrame(secret, context, allocation, new byte[16])));

        var negativeLength = new byte[4096];
        BinaryPrimitives.WriteInt64BigEndian(negativeLength, -1);
        Assert.Throws<InvalidDataException>(() => VaultCryptography.DecryptRecord(
            secret,
            EncryptRawRecordFrame(secret, context, allocation, negativeLength)));

        var oversizedLength = new byte[4096];
        BinaryPrimitives.WriteInt64BigEndian(oversizedLength, 4090);
        Assert.Throws<InvalidDataException>(() => VaultCryptography.DecryptRecord(
            secret,
            EncryptRawRecordFrame(secret, context, allocation, oversizedLength)));

        var nonZeroPadding = new byte[4096];
        nonZeroPadding[8] = 1;
        Assert.Throws<InvalidDataException>(() => VaultCryptography.DecryptRecord(
            secret,
            EncryptRawRecordFrame(secret, context, allocation, nonZeroPadding)));
    }

    private static VaultCryptoContext Context(JsonElement value) => new(
        CryptoPurposeExtensions.FromWireValue(value.GetProperty("purpose").GetString()!),
        VaultId.Parse(value.GetProperty("vaultId").GetString()!),
        DeviceId.Parse(value.GetProperty("originDeviceId").GetString()!),
        value.GetProperty("entityId").GetString()!,
        value.GetProperty("field").GetString()!,
        value.GetProperty("keyEpoch").GetInt64());

    private static byte[] DecodeBase64Url(string value) =>
        Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static VaultCryptoContext ItemContext(
        CryptoPurpose purpose = CryptoPurpose.ItemPayload,
        string field = "content",
        long keyEpoch = 1) => new(
            purpose,
            VaultId.Parse("00112233-4455-4677-8899-aabbccddeeff"),
            DeviceId.Parse("11111111-2222-4333-8444-555555555555"),
            "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
            field,
            keyEpoch);

    private static VaultCipherEnvelope EncryptRawRecordFrame(
        byte[] epochSecret,
        VaultCryptoContext context,
        NonceAllocation allocation,
        byte[] framedPlaintext)
    {
        byte[] key = VaultCryptography.DeriveKey(epochSecret, context);
        byte[] nonce = allocation.GetNonce();
        byte[] ciphertext = new byte[framedPlaintext.Length];
        byte[] tag = new byte[16];
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(
                nonce,
                framedPlaintext,
                ciphertext,
                tag,
                VaultCryptography.BuildAad(context, allocation.Counter, framedPlaintext.Length));
            return new VaultCipherEnvelope(
                context.Purpose.ToWireValue(),
                context.VaultId.Value,
                context.OriginDeviceId.Value,
                context.EntityId,
                context.Field,
                context.KeyEpoch,
                allocation.Counter,
                Base64Url(nonce),
                framedPlaintext.Length,
                Base64Url(ciphertext.Concat(tag).ToArray()));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private sealed class RepeatingThenIndependentGenerator : IEpochSecretGenerator
    {
        public int Calls { get; private set; }

        public byte[] Generate(int size)
        {
            Calls++;
            return Enumerable.Range(0, size)
                .Select(value => (byte)(Calls == 1 ? value : value + 1))
                .ToArray();
        }
    }

    private sealed class WrongLengthGenerator : IEpochSecretGenerator
    {
        public byte[] Generate(int size) => new byte[size - 1];
    }

    private sealed class HistoricalCollisionThenIndependentGenerator : IEpochSecretGenerator
    {
        public int Calls { get; private set; }

        public byte[] Generate(int size)
        {
            Calls++;
            return Enumerable.Range(0, size)
                .Select(value => (byte)(Calls == 1 ? value : value + 2))
                .ToArray();
        }
    }

    private sealed class RepeatingGenerator(byte[] value) : IEpochSecretGenerator
    {
        public byte[] Generate(int size)
        {
            Assert.Equal(size, value.Length);
            return value.ToArray();
        }
    }
}
