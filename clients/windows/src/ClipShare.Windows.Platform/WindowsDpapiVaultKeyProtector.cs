namespace ClipShare.Windows.Platform;

using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using ClipShare.Windows.Vault;

public sealed record WindowsProtectedSecret(string ProtectedSecret);

[SupportedOSPlatform("windows")]
public sealed partial class WindowsDpapiVaultKeyProtector
{
    private const uint CryptProtectUiForbidden = 0x1;
    private static readonly byte[] EntropyPrefix = Encoding.ASCII.GetBytes("clipshare:vault:dpapi:v1\0");

    public static WindowsProtectedSecret Protect(
        VaultId vaultId,
        string initializationId,
        long keyEpoch,
        ReadOnlySpan<byte> epochSecret)
    {
        if (epochSecret.Length != EpochSecret.Size)
        {
            throw new ArgumentException("Epoch secrets are exactly 32 bytes.", nameof(epochSecret));
        }

        byte[] entropy = BuildEntropy(vaultId, initializationId, keyEpoch);
        try
        {
            return new WindowsProtectedSecret(Base64Url(Transform(epochSecret, entropy, protect: true)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    public static byte[] Unprotect(
        VaultId vaultId,
        string initializationId,
        long keyEpoch,
        WindowsProtectedSecret protectedSecret)
    {
        ArgumentNullException.ThrowIfNull(protectedSecret);
        byte[] entropy = BuildEntropy(vaultId, initializationId, keyEpoch);
        byte[] protectedBytes = DecodeBase64Url(protectedSecret.ProtectedSecret);
        try
        {
            byte[] result;
            try
            {
                result = Transform(protectedBytes, entropy, protect: false);
            }
            catch (Win32Exception error)
            {
                throw new CryptographicException("DPAPI could not unwrap the Vault epoch secret.", error);
            }
            if (result.Length != EpochSecret.Size)
            {
                CryptographicOperations.ZeroMemory(result);
                throw new CryptographicException("DPAPI wrapper contained an invalid epoch secret.");
            }

            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private static byte[] BuildEntropy(VaultId vaultId, string initializationId, long keyEpoch)
    {
        _ = VaultId.Parse(initializationId);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(keyEpoch);

        byte[] vaultBytes = Convert.FromHexString(vaultId.Value.Replace("-", string.Empty, StringComparison.Ordinal));
        byte[] initializationBytes = Convert.FromHexString(initializationId.Replace("-", string.Empty, StringComparison.Ordinal));
        byte[] entropy = new byte[EntropyPrefix.Length + 16 + 16 + sizeof(long)];
        EntropyPrefix.CopyTo(entropy, 0);
        vaultBytes.CopyTo(entropy, EntropyPrefix.Length);
        initializationBytes.CopyTo(entropy, EntropyPrefix.Length + 16);
        BinaryPrimitives.WriteInt64BigEndian(entropy.AsSpan(EntropyPrefix.Length + 32), keyEpoch);
        CryptographicOperations.ZeroMemory(vaultBytes);
        CryptographicOperations.ZeroMemory(initializationBytes);
        return entropy;
    }

    private static byte[] Transform(ReadOnlySpan<byte> input, ReadOnlySpan<byte> entropy, bool protect)
    {
        DataBlob inputBlob = Allocate(input);
        DataBlob entropyBlob = Allocate(entropy);
        DataBlob outputBlob = default;
        try
        {
            bool success = protect
                ? CryptProtectData(
                    in inputBlob,
                    null,
                    in entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outputBlob)
                : CryptUnprotectData(
                    in inputBlob,
                    IntPtr.Zero,
                    in entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outputBlob);
            if (!success)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DPAPI could not process the Vault epoch secret.");
            }

            var result = new byte[outputBlob.Length];
            Marshal.Copy(outputBlob.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            ZeroAndFreeHGlobal(inputBlob);
            ZeroAndFreeHGlobal(entropyBlob);
            ZeroAndLocalFree(outputBlob);
        }
    }

    private static DataBlob Allocate(ReadOnlySpan<byte> value)
    {
        IntPtr pointer = Marshal.AllocHGlobal(value.Length);
        byte[] copy = value.ToArray();
        try
        {
            Marshal.Copy(copy, 0, pointer, copy.Length);
            return new DataBlob(copy.Length, pointer);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    private static void ZeroAndFreeHGlobal(DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero)
        {
            return;
        }

        Marshal.Copy(new byte[blob.Length], 0, blob.Data, blob.Length);
        Marshal.FreeHGlobal(blob.Data);
    }

    private static void ZeroAndLocalFree(DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero)
        {
            return;
        }

        Marshal.Copy(new byte[blob.Length], 0, blob.Data, blob.Length);
        _ = LocalFree(blob.Data);
    }

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

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

        if (!string.Equals(Base64Url(result), value, StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(result);
            throw new InvalidDataException("Non-canonical Base64URL.");
        }

        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DataBlob(int length, IntPtr data)
    {
        public int Length { get; } = length;

        public IntPtr Data { get; } = data;
    }

    [LibraryImport("crypt32.dll", EntryPoint = "CryptProtectData", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(
        in DataBlob dataIn,
        string? description,
        in DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [LibraryImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(
        in DataBlob dataIn,
        IntPtr description,
        in DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [LibraryImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static partial IntPtr LocalFree(IntPtr memory);
}
