namespace ClipShare.Windows.C2.Tests;

using System.Text.Json;
using ClipShare.Windows.Vault;

public sealed class VaultContractCodecTests
{
    [Fact]
    public void StrictEnvelopeAcceptsFrozenShape()
    {
        var decoded = VaultContractCodec.DecodeEnvelope(EnvelopeJson());
        Assert.Equal("item-payload", decoded.Purpose);
        Assert.Equal(4096, decoded.PaddedPlaintextBytes);
    }

    [Fact]
    public void StrictEnvelopeRejectsUnknownPropertiesAndInvalidBuckets()
    {
        Assert.Throws<JsonException>(() =>
            VaultContractCodec.DecodeEnvelope(EnvelopeJson().Replace("}", ",\"extra\":true}", StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() => VaultContractCodec.DecodeEnvelope(EnvelopeJson().Replace(
            "\"algorithm\":\"A256GCM\"",
            "\"algorithm\":\"A128GCM\",\"algorithm\":\"A256GCM\"",
            StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() =>
            VaultContractCodec.DecodeEnvelope(EnvelopeJson().Replace("4096", "4095", StringComparison.Ordinal)));
    }

    [Fact]
    public void StrictEnvelopeRejectsNonCanonicalBase64Url()
    {
        Assert.Throws<InvalidDataException>(() =>
            VaultContractCodec.DecodeEnvelope(EnvelopeJson().Replace(Nonce, "AAAAAAAAAAAAAAA=", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() =>
            VaultContractCodec.DecodeEnvelope(EnvelopeJson().Replace(CipherAndTag, "AAAAAAAAAAAAAAAAAAAAAB", StringComparison.Ordinal)));
    }

    [Fact]
    public void StrictEnvelopeRejectsEveryMetadataAndCiphertextBoundary()
    {
        Assert.Throws<ArgumentNullException>(() => VaultContractCodec.DecodeEnvelope(null!));
        Assert.Throws<JsonException>(() => VaultContractCodec.DecodeEnvelope("null"));
        Assert.Throws<InvalidDataException>(() => DecodeWith("\"schemaVersion\":1", "\"schemaVersion\":2"));
        Assert.Throws<InvalidDataException>(() => DecodeWith("\"algorithm\":\"A256GCM\"", "\"algorithm\":\"other\""));
        Assert.Throws<InvalidDataException>(() => DecodeWith("\"purpose\":\"item-payload\"", "\"purpose\":\"other\""));
        Assert.Throws<InvalidDataException>(() => DecodeWith("\"field\":\"content\"", "\"field\":\"Bad\""));
        Assert.Throws<InvalidDataException>(() => DecodeWith("\"keyEpoch\":1", "\"keyEpoch\":0"));
        Assert.Throws<InvalidDataException>(() => DecodeWith("\"nonceCounter\":7", "\"nonceCounter\":-1"));
        Assert.Throws<InvalidDataException>(() => DecodeWith("\"nonceCounter\":7", "\"nonceCounter\":0"));
        Assert.Throws<InvalidDataException>(() => DecodeWith(Nonce, "AAAAAAAAAAAAAAA"));
        Assert.Throws<InvalidDataException>(() => DecodeWith(CipherAndTag, "AA"));
    }

    [Fact]
    public void FileChunkAndEpochWrapperAllowTheirFrozenCounterShapes()
    {
        string fileChunk = EnvelopeJson()
            .Replace("\"purpose\":\"item-payload\"", "\"purpose\":\"file-chunk\"", StringComparison.Ordinal)
            .Replace("\"nonceCounter\":7", "\"nonceCounter\":0", StringComparison.Ordinal)
            .Replace("\"paddedPlaintextBytes\":4096", "\"paddedPlaintextBytes\":0", StringComparison.Ordinal)
            .Replace(CipherAndTag, EmptyCipherAndTag, StringComparison.Ordinal);
        Assert.Equal("file-chunk", VaultContractCodec.DecodeEnvelope(fileChunk).Purpose);
        Assert.Throws<InvalidDataException>(() => VaultContractCodec.DecodeEnvelope(
            fileChunk.Replace("\"paddedPlaintextBytes\":0", "\"paddedPlaintextBytes\":1", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => VaultContractCodec.DecodeEnvelope(
            fileChunk.Replace("\"paddedPlaintextBytes\":0", "\"paddedPlaintextBytes\":-1", StringComparison.Ordinal)));

        string epochWrapper = EnvelopeJson()
            .Replace("\"purpose\":\"item-payload\"", "\"purpose\":\"epoch-wrapper\"", StringComparison.Ordinal)
            .Replace("\"nonceCounter\":7", "\"nonceCounter\":0", StringComparison.Ordinal);
        Assert.Equal("epoch-wrapper", VaultContractCodec.DecodeEnvelope(epochWrapper).Purpose);
    }

    private const string Nonce = "AAAAAAAAAAAAAAAA";
    private static readonly string CipherAndTag = Convert.ToBase64String(new byte[4096 + 16])
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static readonly string EmptyCipherAndTag = Convert.ToBase64String(new byte[16])
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static VaultEnvelopeContract DecodeWith(string oldValue, string newValue) =>
        VaultContractCodec.DecodeEnvelope(EnvelopeJson().Replace(oldValue, newValue, StringComparison.Ordinal));

    private static string EnvelopeJson() =>
        $$"""
        {"schemaVersion":1,"algorithm":"A256GCM","purpose":"item-payload","vaultId":"00112233-4455-4677-8899-aabbccddeeff","originDeviceId":"11111111-2222-4333-8444-555555555555","entityId":"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee","field":"content","keyEpoch":1,"nonceCounter":7,"nonce":"AAAAAAAAAAAAAAAA","paddedPlaintextBytes":4096,"cipherAndTag":"{{CipherAndTag}}"}
        """;
}
