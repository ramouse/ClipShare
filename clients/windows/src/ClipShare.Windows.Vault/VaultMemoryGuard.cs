namespace ClipShare.Windows.Vault;

public sealed class VaultMemoryGuard
{
    private static readonly TimeSpan DefaultBackgroundLimit = TimeSpan.FromMinutes(10);
    private readonly Action clearSensitiveState;
    private readonly TimeSpan backgroundLimit;
    private DateTimeOffset? backgroundSince;

    public VaultMemoryGuard(Action clearSensitiveState, TimeSpan? backgroundLimit = null)
    {
        ArgumentNullException.ThrowIfNull(clearSensitiveState);
        this.clearSensitiveState = clearSensitiveState;
        this.backgroundLimit = backgroundLimit ?? DefaultBackgroundLimit;
        if (this.backgroundLimit < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(backgroundLimit));
        }
    }

    public void OnBackground(DateTimeOffset now) => backgroundSince = now;

    public void OnForeground(DateTimeOffset now)
    {
        DateTimeOffset? started = backgroundSince;
        backgroundSince = null;
        if (started is not null && now >= started.Value + backgroundLimit)
        {
            clearSensitiveState();
        }
    }

    public void OnSystemLocked()
    {
        backgroundSince = null;
        clearSensitiveState();
    }

    public void OnProcessExit()
    {
        backgroundSince = null;
        clearSensitiveState();
    }
}
