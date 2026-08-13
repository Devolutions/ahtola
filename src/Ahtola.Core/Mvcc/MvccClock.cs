namespace Ahtola.Core.Mvcc;

/// <summary>
/// Logical clock used by the MVCC store. Mirrors Turso
/// <c>turso-src/core/mvcc/clock.rs::LogicalClock</c>.
/// </summary>
internal interface ILogicalClock
{
    /// <summary>
    /// Generates the next timestamp, invokes <paramref name="publish"/> while
    /// the clock lock is held, then returns the timestamp.
    /// </summary>
    ulong GetTimestamp(Action<ulong> publish);

    void Reset(ulong timestamp);
}

/// <summary>
/// Mutex-guarded monotonic clock. Commit timestamps must be published as
/// <c>Preparing(ts)</c> inside <see cref="GetTimestamp"/> so begin timestamps
/// cannot interleave and break snapshot isolation (Turso clock.rs).
/// </summary>
internal sealed class MvccClock : ILogicalClock
{
    private readonly object _gate = new();
    private ulong _value;

    public ulong GetBeginTimestamp() => GetTimestamp(static _ => { });

    public ulong GetCommitTimestamp(Action<ulong> publish) => GetTimestamp(publish);

    public ulong GetTimestamp(Action<ulong> publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        lock (_gate)
        {
            var ts = _value;
            _value = checked(ts + 1);
            publish(ts);
            return ts;
        }
    }

    public void Reset(ulong timestamp)
    {
        lock (_gate)
            _value = timestamp;
    }
}
