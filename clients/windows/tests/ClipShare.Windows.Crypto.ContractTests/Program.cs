using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClipShare.Windows.Crypto.ContractTests;

internal static partial class Program
{
    private const string Prefix = "ENC1:";
    private const int KeyBytes = 32;
    private const int IvBytes = 12;
    private const int TagBytes = 16;
    private const int MaxMarkerChars = 16_777_216;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static int _passed;

    private sealed class Enc1ContractException(
        string code,
        string message,
        Exception? innerException = null) : Exception(message, innerException)
    {
        public string Code { get; } = code;
    }

    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine(
                "usage: contract-tests <positive-vectors.json> <negative-vectors.json>");
            return 2;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(args[0]));
        using JsonDocument negativeDocument = JsonDocument.Parse(File.ReadAllBytes(args[1]));
        JsonElement root = document.RootElement;
        Check(root.GetProperty("suite").GetString() == "clipshare-enc1", "suite name");
        Check(root.GetProperty("suite_version").GetInt32() == 1, "suite version");
        Check(root.GetProperty("spec_revision").GetString() == "1.0.0", "spec revision");
        Check(root.GetProperty("test_only").GetBoolean(), "fixtures are test-only");
        JsonElement algorithm = root.GetProperty("algorithm");
        Check(algorithm.GetProperty("name").GetString() == "AES-256-GCM", "algorithm name");
        Check(algorithm.GetProperty("key_bits").GetInt32() == 256, "AES-256 key bits");
        Check(algorithm.GetProperty("iv_bits").GetInt32() == 96, "96-bit IV");
        Check(algorithm.GetProperty("tag_bits").GetInt32() == 128, "128-bit tag");
        Check(algorithm.GetProperty("aad").GetString() == "empty", "empty AAD");
        Check(
            algorithm.GetProperty("output_layout").GetString() == "ciphertext||tag",
            "ciphertext and tag layout");
        Check(
            root.GetProperty("wire_format").GetString()
                == "ENC1:<iv_b64url>.<cipher_and_tag_b64url>",
            "wire format");
        Check(
            root.GetProperty("base64url_profile").GetString()
                == "RFC4648_URLSAFE_NO_PADDING_STRICT",
            "strict Base64URL profile");
        JsonElement limits = root.GetProperty("limits");
        Check(
            limits.GetProperty("max_encrypted_plaintext_bytes").GetInt32() == 10_485_760,
            "encrypted plaintext limit");
        Check(
            limits.GetProperty("max_marker_chars").GetInt32() == 16_777_216,
            "marker limit");
        string[] errorCodes = root.GetProperty("error_codes").EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .ToArray();
        Check(errorCodes.Contains("authentication_failed"), "authentication error code");
        Check(errorCodes.Contains("invalid_utf8"), "UTF-8 error code");

        JsonElement vectors = root.GetProperty("vectors");
        Check(vectors.GetArrayLength() >= 4, "zero and nonzero vector coverage");
        Check(
            vectors.EnumerateArray().Any(vector =>
                RequiredString(vector, "id") == "nonzero-unicode-nul-crlf-normalization"),
            "Unicode/NUL/CRLF vector coverage");
        Check(
            vectors.EnumerateArray().Any(vector =>
                RequiredString(vector, "id") == "nonzero-binary-boundaries"),
            "binary boundary vector coverage");
        foreach (JsonElement vector in vectors.EnumerateArray())
        {
            VerifyVector(vector);
        }

        VerifyNegativeVectors(vectors, negativeDocument.RootElement);
        Console.WriteLine($"ENC1 .NET contract: {_passed} passed, 0 failed");
        return 0;
    }

    private static void VerifyVector(JsonElement vector)
    {
        string id = RequiredString(vector, "id");
        string kind = RequiredString(vector, "kind");
        byte[] key = ParseHex(RequiredString(vector, "key_hex"));
        byte[] iv = ParseHex(RequiredString(vector, "iv_hex"));
        byte[] plaintext = ParseHex(RequiredString(vector, "plaintext_hex"));
        byte[] expectedCiphertext = ParseHex(RequiredString(vector, "ciphertext_hex"));
        byte[] expectedTag = ParseHex(RequiredString(vector, "tag_hex"));
        string expectedMarker = RequiredString(vector, "marker_ascii");

        Check(key.Length == KeyBytes, $"{id}: AES-256 key length");
        Check(iv.Length == IvBytes, $"{id}: 96-bit IV length");
        Check(Base64UrlEncode(key) == RequiredString(vector, "key_b64url"), $"{id}: key encoding");
        Check(Base64UrlEncode(iv) == RequiredString(vector, "iv_b64url"), $"{id}: IV encoding");

        if (kind == "text")
        {
            string text = RequiredString(vector, "plaintext_utf8");
            Check(StrictUtf8Encode(text).AsSpan().SequenceEqual(plaintext), $"{id}: strict UTF-8 bytes");
        }
        else
        {
            Check(kind == "bytes", $"{id}: supported vector kind");
        }

        (byte[] ciphertext, byte[] tag) = Encrypt(key, iv, plaintext);
        Check(ciphertext.AsSpan().SequenceEqual(expectedCiphertext), $"{id}: ciphertext KAT");
        Check(tag.AsSpan().SequenceEqual(expectedTag), $"{id}: authentication tag KAT");
        byte[] combined = [.. ciphertext, .. tag];
        Check(
            Base64UrlEncode(combined) == RequiredString(vector, "cipher_and_tag_b64url"),
            $"{id}: ciphertext and tag encoding");
        string marker = FormatMarker(iv, ciphertext, tag);
        Check(marker == expectedMarker, $"{id}: marker byte equality");

        (byte[] parsedIv, byte[] parsedCombined) = ParseMarker(expectedMarker);
        byte[] decrypted = Decrypt(key, parsedIv, parsedCombined);
        Check(decrypted.AsSpan().SequenceEqual(plaintext), $"{id}: fixed marker decryption");
        if (kind == "text")
        {
            Check(
                StrictUtf8Decode(decrypted) == RequiredString(vector, "plaintext_utf8"),
                $"{id}: strict UTF-8 text decryption");
        }
    }

    private static (byte[] Ciphertext, byte[] Tag) Encrypt(
        byte[] key,
        byte[] iv,
        byte[] plaintext)
    {
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagBytes];
        using AesGcm aes = new(key, TagBytes);
        aes.Encrypt(iv, plaintext, ciphertext, tag, ReadOnlySpan<byte>.Empty);
        return (ciphertext, tag);
    }

    private static byte[] Decrypt(byte[] key, byte[] iv, byte[] combined)
    {
        if (combined.Length < TagBytes)
        {
            throw new Enc1ContractException(
                "invalid_ciphertext_length", "ciphertext is shorter than the tag");
        }

        int ciphertextLength = combined.Length - TagBytes;
        byte[] plaintext = new byte[ciphertextLength];
        using AesGcm aes = new(key, TagBytes);
        try
        {
            aes.Decrypt(
                iv,
                combined.AsSpan(0, ciphertextLength),
                combined.AsSpan(ciphertextLength, TagBytes),
                plaintext,
                ReadOnlySpan<byte>.Empty);
        }
        catch (AuthenticationTagMismatchException exception)
        {
            throw new Enc1ContractException(
                "authentication_failed", "authentication failed", exception);
        }
        return plaintext;
    }

    private static string FormatMarker(byte[] iv, byte[] ciphertext, byte[] tag)
    {
        byte[] combined = new byte[ciphertext.Length + tag.Length];
        ciphertext.CopyTo(combined, 0);
        tag.CopyTo(combined, ciphertext.Length);
        return $"{Prefix}{Base64UrlEncode(iv)}.{Base64UrlEncode(combined)}";
    }

    private static (byte[] Iv, byte[] Combined) ParseMarker(string marker)
    {
        if (marker.Length > MaxMarkerChars)
        {
            throw new Enc1ContractException(
                "size_limit_exceeded", "ENC1 marker exceeds size limit");
        }
        if (!marker.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new Enc1ContractException("not_encrypted", "not an ENC1 marker");
        }

        string[] fields = marker[Prefix.Length..].Split('.');
        if (fields.Length != 2 || fields[0].Length == 0 || fields[1].Length == 0)
        {
            throw new Enc1ContractException("invalid_envelope", "invalid ENC1 envelope");
        }

        byte[] iv = Base64UrlDecode(fields[0]);
        byte[] combined = Base64UrlDecode(fields[1]);
        if (iv.Length != IvBytes)
        {
            throw new Enc1ContractException("invalid_iv_length", "invalid ENC1 IV length");
        }
        if (combined.Length < TagBytes)
        {
            throw new Enc1ContractException(
                "invalid_ciphertext_length", "ciphertext is shorter than the tag");
        }
        return (iv, combined);
    }

    private static void VerifyNegativeVectors(JsonElement vectors, JsonElement negative)
    {
        Check(RequiredString(negative, "suite") == "clipshare-enc1-negative", "negative suite name");
        Check(negative.GetProperty("suite_version").GetInt32() == 1, "negative suite version");
        Check(RequiredString(negative, "spec_revision") == "1.0.0", "negative spec revision");
        Check(negative.GetProperty("test_only").GetBoolean(), "negative fixtures test-only");

        foreach (JsonElement testCase in negative.GetProperty("marker_cases").EnumerateArray())
        {
            ExpectCode(
                () => ParseMarker(RequiredString(testCase, "value")),
                RequiredString(testCase, "expected_error"),
                $"{RequiredString(testCase, "id")}: marker rejection");
        }
        foreach (JsonElement testCase in negative.GetProperty("size_cases").EnumerateArray())
        {
            Check(RequiredString(testCase, "operation") == "marker-one-char-over-limit",
                $"{RequiredString(testCase, "id")}: supported size operation");
            ExpectCode(
                () => ParseMarker(
                    Prefix + new string('A', MaxMarkerChars - Prefix.Length + 1)),
                RequiredString(testCase, "expected_error"),
                $"{RequiredString(testCase, "id")}: rejection");
        }

        foreach (JsonElement testCase in negative.GetProperty("base64url_cases").EnumerateArray())
        {
            ExpectCode(() => Base64UrlDecode(RequiredString(testCase, "value")),
                RequiredString(testCase, "expected_error"),
                $"{RequiredString(testCase, "id")}: Base64URL rejection");
        }

        foreach (JsonElement testCase in negative.GetProperty("authentication_cases").EnumerateArray())
        {
            string sourceId = RequiredString(testCase, "source_vector_id");
            JsonElement vector = vectors.EnumerateArray().Single(
                item => RequiredString(item, "id") == sourceId);
            byte[] key = ParseHex(RequiredString(vector, "key_hex"));
            (byte[] iv, byte[] combined) = ParseMarker(RequiredString(vector, "marker_ascii"));
            switch (RequiredString(testCase, "mutation"))
            {
                case "flip-last-combined-bit":
                    byte[] tampered = (byte[])combined.Clone();
                    tampered[^1] ^= 0x01;
                    ExpectCode(() => Decrypt(key, iv, tampered),
                        RequiredString(testCase, "expected_error"),
                        $"{RequiredString(testCase, "id")}: rejection");
                    break;
                case "replace-key-a5":
                    byte[] wrongKey = Enumerable.Repeat((byte)0xA5, KeyBytes).ToArray();
                    ExpectCode(() => Decrypt(wrongKey, iv, combined),
                        RequiredString(testCase, "expected_error"),
                        $"{RequiredString(testCase, "id")}: rejection");
                    break;
                default:
                    throw new InvalidDataException("unsupported authentication mutation");
            }
        }

        foreach (JsonElement testCase in negative.GetProperty("unicode_cases").EnumerateArray())
        {
            string id = RequiredString(testCase, "id");
            switch (RequiredString(testCase, "operation"))
            {
                case "encode-isolated-high-surrogate":
                    ExpectCode(() => StrictUtf8Encode("\uD800"),
                        RequiredString(testCase, "expected_error"), $"{id}: rejection");
                    break;
                case "decode-ff":
                    ExpectCode(() => StrictUtf8Decode([0xFF]),
                        RequiredString(testCase, "expected_error"), $"{id}: rejection");
                    break;
                default:
                    throw new InvalidDataException("unsupported Unicode operation");
            }
        }
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        if (!Base64UrlRegex().IsMatch(value) || value.Length % 4 == 1)
        {
            throw new Enc1ContractException("invalid_base64url", "invalid Base64URL");
        }

        string padded = value.Replace('-', '+').Replace('_', '/')
            + new string('=', (4 - (value.Length % 4)) % 4);
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(padded);
        }
        catch (FormatException exception)
        {
            throw new Enc1ContractException(
                "invalid_base64url", "invalid Base64URL", exception);
        }
        if (Base64UrlEncode(decoded) != value)
        {
            throw new Enc1ContractException("invalid_base64url", "non-canonical Base64URL");
        }
        return decoded;
    }

    private static byte[] ParseHex(string value)
    {
        if (!HexRegex().IsMatch(value))
        {
            throw new FormatException("invalid lowercase hexadecimal fixture");
        }
        return Convert.FromHexString(value);
    }

    private static string RequiredString(JsonElement element, string propertyName) =>
        element.GetProperty(propertyName).GetString()
        ?? throw new InvalidDataException($"missing string property {propertyName}");

    private static byte[] StrictUtf8Encode(string value)
    {
        try
        {
            return StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new Enc1ContractException("invalid_unicode", "invalid Unicode", exception);
        }
    }

    private static string StrictUtf8Decode(byte[] value)
    {
        try
        {
            return StrictUtf8.GetString(value);
        }
        catch (DecoderFallbackException exception)
        {
            throw new Enc1ContractException("invalid_utf8", "invalid UTF-8", exception);
        }
    }

    private static void ExpectCode(Action action, string expectedCode, string name)
    {
        try
        {
            action();
        }
        catch (Enc1ContractException exception)
        {
            Check(exception.Code == expectedCode, $"{name} maps to {expectedCode}");
            return;
        }
        throw new InvalidOperationException($"FAIL: {name}");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"FAIL: {name}");
        }
        _passed++;
    }

    [GeneratedRegex("^(?:[0-9a-f]{2})*$", RegexOptions.CultureInvariant)]
    private static partial Regex HexRegex();

    [GeneratedRegex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Base64UrlRegex();
}
