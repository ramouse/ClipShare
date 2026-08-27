namespace ClipShare.Windows.Application;

/// <summary>把非消费型预读结果绑定到产生它的用户输入，避免输入变化后误用陈旧状态。</summary>
public sealed class LocatorBoundState<TValue>
    where TValue : class
{
    private string? _locator;
    private TValue? _value;

    public bool HasValue => _value is not null;

    public void Set(string locator, TValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locator);
        ArgumentNullException.ThrowIfNull(value);
        _locator = locator.Trim();
        _value = value;
    }

    public TValue? Get(string locator)
    {
        if (_value is null || string.IsNullOrWhiteSpace(locator))
        {
            return null;
        }

        return string.Equals(_locator, locator.Trim(), StringComparison.Ordinal)
            ? _value
            : null;
    }

    public void Reset()
    {
        _locator = null;
        _value = null;
    }
}
