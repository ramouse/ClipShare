namespace ClipShare.Windows.Vault;

public sealed record PairedDeviceState(DeviceId DeviceId, bool Revoked);

public sealed record EpochDistributionPlan
{
    private EpochDistributionPlan(long previousEpoch, long newEpoch, IReadOnlySet<DeviceId> recipients)
    {
        PreviousEpoch = previousEpoch;
        NewEpoch = newEpoch;
        Recipients = recipients;
    }

    public long PreviousEpoch { get; }

    public long NewEpoch { get; }

    public IReadOnlySet<DeviceId> Recipients { get; }

    public static EpochDistributionPlan Create(long previousEpoch, IReadOnlyCollection<PairedDeviceState> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(previousEpoch);
        if (previousEpoch == long.MaxValue)
        {
            throw new InvalidOperationException("The epoch number is exhausted.");
        }

        if (devices.Select(device => device.DeviceId).Distinct().Count() != devices.Count)
        {
            throw new ArgumentException("Paired devices must be unique.", nameof(devices));
        }

        return new EpochDistributionPlan(
            previousEpoch,
            previousEpoch + 1,
            devices.Where(device => !device.Revoked).Select(device => device.DeviceId).ToHashSet());
    }

    public void ValidateGeneratedWrappers(IReadOnlySet<DeviceId> wrapperRecipients)
    {
        ArgumentNullException.ThrowIfNull(wrapperRecipients);
        if (!Recipients.SetEquals(wrapperRecipients))
        {
            throw new InvalidOperationException("A new epoch must be wrapped exactly for the non-revoked device set.");
        }
    }
}
