using System.Diagnostics;

namespace Ahtola.Core.Storage;

/// <summary>
/// SQLite-compatible busy retry delays from
/// <c>sqliteDefaultBusyCallback</c> (<c>delays[]</c> by attempt count).
/// </summary>
/// <remarks>
/// Stage 4 replaces the previous flat 10 ms poll so managed lock waiters track
/// stock SQLite backoff without requiring a custom busy handler yet.
/// </remarks>
public static class SqliteBusyBackoff
{
    // From sqlite3.c sqliteDefaultBusyCallback:
    // delays[] = { 1, 2, 5, 10, 15, 20, 25, 25, 25, 50, 50, 100 }
    private static readonly int[] DelayMilliseconds =
        [1, 2, 5, 10, 15, 20, 25, 25, 25, 50, 50, 100];

    /// <summary>
    /// Returns the sleep duration for the given zero-based retry attempt,
    /// capped by any remaining busy-timeout budget.
    /// </summary>
    public static TimeSpan DelayForAttempt(int attempt, TimeSpan elapsed, TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);

        if (timeout != Timeout.InfiniteTimeSpan && elapsed >= timeout)
            return TimeSpan.Zero;

        var index = attempt < DelayMilliseconds.Length
            ? attempt
            : DelayMilliseconds.Length - 1;
        var delay = TimeSpan.FromMilliseconds(DelayMilliseconds[index]);
        if (timeout == Timeout.InfiniteTimeSpan)
            return delay;

        var remaining = timeout - elapsed;
        return remaining < delay ? remaining : delay;
    }

    /// <summary>
    /// Sleeps for the next SQLite busy delay for <paramref name="attempt"/>,
    /// or returns <see langword="false"/> when the timeout has expired.
    /// </summary>
    public static bool Wait(int attempt, TimeSpan timeout, Stopwatch? stopwatch)
    {
        var elapsed = stopwatch?.Elapsed ?? TimeSpan.Zero;
        var delay = DelayForAttempt(attempt, elapsed, timeout);
        if (delay <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            return false;

        if (delay > TimeSpan.Zero)
            Thread.Sleep(delay);

        return timeout == Timeout.InfiniteTimeSpan
            || stopwatch is null
            || stopwatch.Elapsed < timeout;
    }

    /// <summary>
    /// Waits for the next SQLite busy delay or cancellation; returns
    /// <see langword="false"/> when the timeout has expired.
    /// </summary>
    public static bool Wait(
        int attempt,
        TimeSpan timeout,
        Stopwatch? stopwatch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var elapsed = stopwatch?.Elapsed ?? TimeSpan.Zero;
        var delay = DelayForAttempt(attempt, elapsed, timeout);
        if (delay <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            return false;

        if (delay > TimeSpan.Zero)
            cancellationToken.WaitHandle.WaitOne(delay);

        cancellationToken.ThrowIfCancellationRequested();
        return timeout == Timeout.InfiniteTimeSpan
            || stopwatch is null
            || stopwatch.Elapsed < timeout;
    }

    /// <summary>
    /// Asynchronously waits for the next SQLite busy delay or cancellation;
    /// returns <see langword="false"/> when the timeout has expired.
    /// </summary>
    public static async ValueTask<bool> WaitAsync(
        int attempt,
        TimeSpan timeout,
        Stopwatch? stopwatch,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var elapsed = stopwatch?.Elapsed ?? TimeSpan.Zero;
        var delay = DelayForAttempt(attempt, elapsed, timeout);
        if (delay <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            return false;

        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

        return timeout == Timeout.InfiniteTimeSpan
            || stopwatch is null
            || stopwatch.Elapsed < timeout;
    }

    /// <summary>
    /// Convenience overload that estimates the attempt from elapsed time for
    /// simple poll loops. Prefer attempt-indexed overloads at call sites that
    /// already count retries.
    /// </summary>
    public static bool Wait(TimeSpan timeout, Stopwatch? stopwatch)
        => Wait(attempt: EstimateAttempt(stopwatch?.Elapsed ?? TimeSpan.Zero), timeout, stopwatch);

    /// <summary>
    /// Cancellation-aware convenience overload pairing with
    /// <see cref="Wait(TimeSpan, Stopwatch?)"/>.
    /// </summary>
    public static bool Wait(
        TimeSpan timeout,
        Stopwatch? stopwatch,
        CancellationToken cancellationToken)
        => Wait(
            attempt: EstimateAttempt(stopwatch?.Elapsed ?? TimeSpan.Zero),
            timeout,
            stopwatch,
            cancellationToken);

    /// <summary>
    /// Asynchronous convenience overload pairing with
    /// <see cref="WaitAsync(int, TimeSpan, Stopwatch?, CancellationToken)"/>.
    /// </summary>
    public static ValueTask<bool> WaitAsync(
        TimeSpan timeout,
        Stopwatch? stopwatch,
        CancellationToken cancellationToken = default)
        => WaitAsync(
            attempt: EstimateAttempt(stopwatch?.Elapsed ?? TimeSpan.Zero),
            timeout,
            stopwatch,
            cancellationToken);

    private static int EstimateAttempt(TimeSpan elapsed)
    {
        // Map elapsed time onto the cumulative sqlite totals so convenience
        // callers without an attempt counter still climb the delay ladder.
        ReadOnlySpan<int> totals = [0, 1, 3, 8, 18, 33, 53, 78, 103, 153, 203, 303];
        var elapsedMs = elapsed <= TimeSpan.Zero
            ? 0
            : elapsed.TotalMilliseconds >= int.MaxValue
                ? int.MaxValue
                : (int)elapsed.TotalMilliseconds;
        for (var index = totals.Length - 1; index >= 0; index--)
        {
            if (elapsedMs >= totals[index])
                return index;
        }

        return 0;
    }
}
