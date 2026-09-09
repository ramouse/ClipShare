namespace ClipShare.Windows.Platform;

using ClipShare.Windows.Vault;

/// <summary>Maps Windows lifecycle/session events into the platform-neutral Vault memory guard.</summary>
public sealed class WindowsVaultSessionGuard
{
    private readonly VaultMemoryGuard memoryGuard;

    public WindowsVaultSessionGuard(VaultMemoryGuard memoryGuard)
    {
        ArgumentNullException.ThrowIfNull(memoryGuard);
        this.memoryGuard = memoryGuard;
    }

    public void OnSessionLocked() => memoryGuard.OnSystemLocked();

    public void OnAppSuspending(DateTimeOffset now) => memoryGuard.OnBackground(now);

    public void OnAppResuming(DateTimeOffset now) => memoryGuard.OnForeground(now);

    public void OnProcessExit() => memoryGuard.OnProcessExit();
}
