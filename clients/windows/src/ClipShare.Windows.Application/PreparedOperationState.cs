namespace ClipShare.Windows.Application;

/// <summary>
/// 保存一次结果未知的幂等操作。相同输入重试时复用原准备结果，成功或端点切换后显式清除。
/// </summary>
public sealed class PreparedOperationState<TKey, TPrepared>
    where TKey : notnull
    where TPrepared : class
{
    private readonly IEqualityComparer<TKey> _comparer;
    private TKey? _key;
    private TPrepared? _prepared;

    public PreparedOperationState(IEqualityComparer<TKey>? comparer = null) =>
        _comparer = comparer ?? EqualityComparer<TKey>.Default;

    public bool HasPending => _prepared is not null;

    public TPrepared GetOrPrepare(TKey key, Func<TPrepared> prepare)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(prepare);

        if (_prepared is not null && _key is not null && _comparer.Equals(_key, key))
        {
            return _prepared;
        }

        TPrepared next = prepare()
            ?? throw new InvalidOperationException("准备操作不能返回 null。");
        _key = key;
        _prepared = next;
        return next;
    }

    public void Complete() => Reset();

    public void Reset()
    {
        _key = default;
        _prepared = null;
    }
}
