namespace ClipShare.Windows.Vault;

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

public sealed record VaultEnvelopeContract
{
    public required int SchemaVersion { get; init; }

    public required string Algorithm { get; init; }

    public required string Purpose { get; init; }

    public required string VaultId { get; init; }

    public required string OriginDeviceId { get; init; }

    public required string EntityId { get; init; }

    public required string Field { get; init; }

    public required long KeyEpoch { get; init; }

    public required long NonceCounter { get; init; }

    public required string Nonce { get; init; }

    public required long PaddedPlaintextBytes { get; init; }

    public required string CipherAndTag { get; init; }
}

public static partial class VaultContractCodec
{
    private static readonly HashSet<string> Purposes =
    [
        "folder-metadata",
        "item-metadata",
        "item-payload",
        "file-key-wrap",
        "file-manifest",
        "file-chunk",
        "event-body",
        "epoch-wrapper",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex FieldPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Base64UrlPattern();

    public static VaultEnvelopeContract DecodeEnvelope(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var envelope = JsonSerializer.Deserialize<VaultEnvelopeContract>(json, JsonOptions)
            ?? throw new JsonException("Vault envelope must be an object.");
        if (envelope.SchemaVersion != 1)
        {
            throw new InvalidDataException("Unsupported Vault envelope version.");
        }

        if (!string.Equals(envelope.Algorithm, "A256GCM", StringComparison.Ordinal) ||
            !Purposes.Contains(envelope.Purpose))
        {
            throw new InvalidDataException("Unsupported Vault encryption contract.");
        }

        _ = VaultId.Parse(envelope.VaultId);
        _ = DeviceId.Parse(envelope.OriginDeviceId);
        _ = VaultUuid.Validate(envelope.EntityId);
        if (!FieldPattern().IsMatch(envelope.Field) || envelope.KeyEpoch <= 0 || envelope.NonceCounter < 0)
        {
            throw new InvalidDataException("Invalid Vault envelope metadata.");
        }

        if (envelope.Purpose is not ("file-chunk" or "epoch-wrapper") && envelope.NonceCounter == 0)
        {
            throw new InvalidDataException("Record nonce counters start at one.");
        }

        byte[] encrypted = DecodeBase64Url(envelope.CipherAndTag);
        if (DecodeBase64Url(envelope.Nonce).Length != 12 || encrypted.Length < 16)
        {
            throw new InvalidDataException("Invalid Vault ciphertext shape.");
        }

        long plaintextBytes = encrypted.LongLength - 16;
        if (envelope.PaddedPlaintextBytes != plaintextBytes)
        {
            throw new InvalidDataException("Declared plaintext length does not match the ciphertext.");
        }

        if (envelope.Purpose != "file-chunk" &&
            (envelope.PaddedPlaintextBytes < 4096 || envelope.PaddedPlaintextBytes % 4096 != 0))
        {
            throw new InvalidDataException("Vault record plaintext must use 4 KiB buckets.");
        }

        if (envelope.Purpose == "file-chunk" && envelope.PaddedPlaintextBytes is < 0 or > 1_048_576)
        {
            throw new InvalidDataException("Invalid file chunk length.");
        }

        return envelope;
    }

    private static byte[] DecodeBase64Url(string value)
    {
        if (!Base64UrlPattern().IsMatch(value) || value.Length % 4 == 1)
        {
            throw new InvalidDataException("Invalid Base64URL.");
        }

        var padded = value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '=');
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(padded);
        }
        catch (FormatException error)
        {
            throw new InvalidDataException("Invalid Base64URL.", error);
        }

        var canonical = Convert.ToBase64String(decoded).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (!string.Equals(canonical, value, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Non-canonical Base64URL.");
        }

        return decoded;
    }
}
