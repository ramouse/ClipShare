namespace ClipShare.Windows.Features.Vault;

using ClipShare.Windows.Platform;
using ClipShare.Windows.Vault;

public interface IWindowsVaultSecretProtector
{
    WindowsProtectedSecret Protect(
        VaultId vaultId,
        string initializationId,
        long keyEpoch,
        ReadOnlySpan<byte> epochSecret);

    byte[] Unprotect(
        VaultId vaultId,
        string initializationId,
        long keyEpoch,
        WindowsProtectedSecret protectedSecret);
}

public sealed class DpapiWindowsVaultSecretProtector : IWindowsVaultSecretProtector
{
    public WindowsProtectedSecret Protect(
        VaultId vaultId,
        string initializationId,
        long keyEpoch,
        ReadOnlySpan<byte> epochSecret) =>
        OperatingSystem.IsWindows()
            ? WindowsDpapiVaultKeyProtector.Protect(vaultId, initializationId, keyEpoch, epochSecret)
            : throw new PlatformNotSupportedException("Windows Vault DPAPI is only available on Windows.");

    public byte[] Unprotect(
        VaultId vaultId,
        string initializationId,
        long keyEpoch,
        WindowsProtectedSecret protectedSecret) =>
        OperatingSystem.IsWindows()
            ? WindowsDpapiVaultKeyProtector.Unprotect(vaultId, initializationId, keyEpoch, protectedSecret)
            : throw new PlatformNotSupportedException("Windows Vault DPAPI is only available on Windows.");
}
