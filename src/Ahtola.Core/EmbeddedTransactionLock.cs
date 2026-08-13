using System.Runtime.CompilerServices;
using Ahtola.Core.Storage;

namespace Ahtola.Core;

/// <summary>
/// Raised when a managed statement cannot take the lock a SQLite connection
/// would need. This is the managed engine's <c>SQLITE_BUSY</c>.
/// </summary>
public class EmbeddedBusyException : EmbeddedSqlException
{
    /// <summary>Creates a busy failure carrying SQLite's message.</summary>
    public EmbeddedBusyException()
        : base("database is locked")
    {
    }

    /// <summary>Creates a busy failure with a more specific busy message.</summary>
    private protected EmbeddedBusyException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The write reservation a managed transaction holds on one database, modeling
/// SQLite's RESERVED and EXCLUSIVE locks for managed connections.
/// </summary>
/// <remarks>
/// This lock is process-local by design. A managed physical database is owned
/// exclusively by one process for its whole lifetime (see
/// <c>docs/wal-interoperability-contract.md</c>), so every connection
/// that can contend for a write is in this process. The lock is layered above
/// the pager and adds no cross-process boundary of its own, so it neither
/// relaxes nor duplicates that ownership guard.
///
/// A holder is identified by the owning object rather than by thread, because a
/// managed transaction is owned by a connection and can be advanced from
/// different threads across awaits.
/// </remarks>
internal sealed class EmbeddedTransactionLock
{
    private readonly object _gate = new();
    private readonly Queue<Waiter> _waiters = new();
    private object? _owner;
    private int _holds;
    private bool _excludesReaders;

    /// <summary>
    /// One queued acquisition attempt. Waiters are served strictly in arrival
    /// order so a contended write lock rotates fairly across connections instead
    /// of letting the most-recent releaser re-win (the EF migrations-lock convoy,
    /// ENGINE #17). Each waiter sleeps on its own monitor; the releaser hands off
    /// by pulsing only the head of the queue.
    /// </summary>
    private sealed class Waiter(object owner)
    {
        internal readonly object Owner = owner;
        internal bool Signaled;
        internal bool Removed;
    }

    /// <summary>Whether <paramref name="owner"/> currently holds this lock.</summary>
    internal bool IsHeldBy(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_gate)
            return ReferenceEquals(_owner, owner);
    }

    /// <summary>
    /// Takes the write reservation for <paramref name="owner"/>, waiting up to
    /// <paramref name="busyTimeout"/> when another owner holds it. SQLite maps
    /// <c>sqlite3_busy_timeout</c> onto the same retry: contention fails with busy
    /// only once the timeout has elapsed, and the default timeout of zero fails
    /// immediately.
    /// </summary>
    /// <param name="owner">The connection taking the reservation.</param>
    /// <param name="excludeReaders">
    /// Whether the reservation also excludes other owners' reads, which SQLite's
    /// EXCLUSIVE lock does only under a rollback journal.
    /// </param>
    /// <param name="busyTimeout">How long to wait for a competing holder to release.</param>
    internal void Enter(object owner, bool excludeReaders, TimeSpan busyTimeout)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var deadline = GetBusyDeadline(busyTimeout);
        lock (_gate)
        {
            // Re-entrant acquisition by the current owner never queues.
            if (ReferenceEquals(_owner, owner))
            {
                _holds = checked(_holds + 1);
                _excludesReaders |= excludeReaders;
                return;
            }

            // Uncontested fast path: no owner and no one waiting ahead.
            if (_owner is null && _waiters.Count == 0)
            {
                _owner = owner;
                _holds = 1;
                _excludesReaders = excludeReaders;
                return;
            }

            // Contended: queue behind existing waiters and sleep until the
            // releaser hands off to this waiter specifically (FIFO). The handoff
            // transfers ownership at signal time (in SignalHeadLocked), so this
            // waiter becomes the owner the moment it is dequeued.
            var waiter = new Waiter(owner);
            _waiters.Enqueue(waiter);
            var handedOff = false;
            try
            {
                while (!waiter.Signaled)
                    WaitForSignalOrThrow(waiter, deadline);

                handedOff = true;
                _holds = 1;
                _excludesReaders = excludeReaders;
            }
            finally
            {
                if (!handedOff)
                {
                    if (waiter.Removed)
                    {
                        // Dequeued as the handoff target: ownership already moved to
                        // this owner at signal time with _holds==0. Release it so the
                        // lock is not stranded in a dead owner. Use a raw release
                        // (not Exit) because _holds is 0 here.
                        if (ReferenceEquals(_owner, owner))
                        {
                            _owner = null;
                            _excludesReaders = false;
                            SignalHeadLocked();
                        }
                    }
                    else
                    {
                        // Never handed off: drop from wherever it sits in the queue.
                        waiter.Removed = true;
                        if (_waiters.Count > 0 && ReferenceEquals(_waiters.Peek(), waiter))
                        {
                            _waiters.Dequeue();
                            SignalHeadLocked();
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Takes the write reservation for one autocommit statement, waiting up to
    /// <paramref name="busyTimeout"/> while another owner holds it. Unlike
    /// <see cref="Enter"/>, contenders barge instead of queueing: SQLite has no
    /// FIFO between autocommit writers either — each statement is its own
    /// implicit write transaction and simply re-acquires the pager lock. A
    /// queue here would hand the lock to a waiting loser the moment the current
    /// owner commits, before that connection's next autocommit statement (e.g.
    /// the migrations-lock release DELETE) can run, reintroducing the EF
    /// migrations-lock convoy starvation (ENGINE #17). Barging lets the running
    /// owner's next statement win the race, exactly like the kernel lock does.
    /// Barging cannot jump the <see cref="Enter"/> queue: the FIFO handoff
    /// transfers ownership at signal time, so the lock is only ever observed
    /// free here when no explicit-transaction writer is queued.
    /// </summary>
    /// <remarks>
    /// Contenders poll with a sleep outside the gate rather than waiting on the
    /// gate monitor: a monitor wait would wake on every release pulse, letting
    /// a loser race the owner's very next statement for the lock and slipping
    /// into the commit-to-next-statement gap this reservation exists to close.
    /// </remarks>
    internal void EnterAutocommit(object owner, TimeSpan busyTimeout)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var deadline = GetBusyDeadline(busyTimeout);
        while (true)
        {
            lock (_gate)
            {
                // Re-entrant acquisition by the current owner never waits.
                if (ReferenceEquals(_owner, owner))
                {
                    _holds = checked(_holds + 1);
                    return;
                }

                if (_owner is null)
                {
                    _owner = owner;
                    _holds = 1;
                    _excludesReaders = false;
                    return;
                }

                if (deadline != long.MaxValue && Environment.TickCount64 >= deadline)
                    throw new EmbeddedBusyException();
            }

            Thread.Sleep(1);
        }
    }

    /// <summary>Releases one reservation taken by <paramref name="owner"/>.</summary>
    internal void Exit(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_gate)
        {
            if (!ReferenceEquals(_owner, owner))
                throw new InvalidOperationException("The managed transaction write reservation was lost.");

            _holds--;
            if (_holds != 0)
                return;

            _owner = null;
            _excludesReaders = false;
            SignalHeadLocked();
        }
    }

    private void SignalHeadLocked()
    {
        // Drop dead waiters off the head, then hand the lock to the first live
        // one. PulseAll wakes every sleeper, but only the signaled head proceeds;
        // the rest observe their own Signaled==false and re-wait.
        while (_waiters.Count > 0 && _waiters.Peek().Removed)
            _waiters.Dequeue();

        if (_waiters.Count == 0)
            return;

        var head = _waiters.Dequeue();
        head.Removed = true; // marks it dequeued so its own timeout cleanup no-ops
        head.Signaled = true;
        // Transfer ownership now, at signal time, so a woken waiter that times out
        // before claiming can still release on its own behalf (handedOff==false
        // path in Enter) instead of stranding the lock.
        _owner = head.Owner;
        _holds = 0; // the woken waiter sets this to 1 when it claims
        Monitor.PulseAll(_gate);
    }

    private void WaitForSignalOrThrow(Waiter waiter, long deadline)
    {
        if (deadline == long.MaxValue)
        {
            Monitor.Wait(_gate);
            return;
        }

        var remaining = deadline - Environment.TickCount64;
        if (remaining <= 0)
            throw new EmbeddedBusyException();

        Monitor.Wait(_gate, (int)Math.Min(remaining, int.MaxValue));
    }

    /// <summary>
    /// Throws busy when another owner holds a reservation that blocks writes,
    /// waiting up to <paramref name="busyTimeout"/> for it to be released first.
    /// Autocommit statements use this instead of taking the reservation, because
    /// they are already serialized by the owning database and hold no lock across
    /// statements.
    /// </summary>
    internal void ThrowIfWriteBlocked(object owner, TimeSpan busyTimeout)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var deadline = GetBusyDeadline(busyTimeout);
        lock (_gate)
        {
            // Blocked while another owner holds the reservation OR a queued writer
            // is ahead of this caller: barging past the FIFO would reintroduce the
            // starvation the queue exists to prevent.
            while ((_owner is not null && !ReferenceEquals(_owner, owner)) || _waiters.Count > 0)
                WaitForReleaseOrThrow(deadline);
        }
    }

    /// <summary>
    /// Throws busy when another owner holds a reader-excluding reservation,
    /// waiting up to <paramref name="busyTimeout"/> for it to be released first.
    /// </summary>
    internal void ThrowIfReadBlocked(object owner, TimeSpan busyTimeout)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var deadline = GetBusyDeadline(busyTimeout);
        lock (_gate)
        {
            while (_excludesReaders && _owner is not null && !ReferenceEquals(_owner, owner))
                WaitForReleaseOrThrow(deadline);
        }
    }

    private static long GetBusyDeadline(TimeSpan busyTimeout)
    {
        if (busyTimeout == Timeout.InfiniteTimeSpan)
            return long.MaxValue;

        var milliseconds = Math.Max(0, (long)Math.Ceiling(busyTimeout.TotalMilliseconds));
        return Environment.TickCount64 + milliseconds;
    }

    private void WaitForReleaseOrThrow(long deadline)
    {
        if (deadline == long.MaxValue)
        {
            Monitor.Wait(_gate);
            return;
        }

        var remaining = deadline - Environment.TickCount64;
        if (remaining <= 0)
            throw new EmbeddedBusyException();

        Monitor.Wait(_gate, (int)Math.Min(remaining, int.MaxValue));
    }
}

/// <summary>
/// Brokers one <see cref="EmbeddedTransactionLock"/> per file-backed database so
/// every managed connection opened on the same path contends for the same
/// reservation. In-memory databases own their lock directly, because the only
/// way two connections share one is by sharing the database instance itself.
/// </summary>
internal static class EmbeddedTransactionLockRegistry
{
    private sealed class LockScope
    {
        private readonly Dictionary<string, EmbeddedTransactionLock> _locks = new(StringComparer.Ordinal);

        internal EmbeddedTransactionLock Get(string key)
        {
            lock (_locks)
            {
                if (!_locks.TryGetValue(key, out var transactionLock))
                {
                    transactionLock = new EmbeddedTransactionLock();
                    _locks.Add(key, transactionLock);
                }

                return transactionLock;
            }
        }
    }

    private static readonly ConditionalWeakTable<IFileSystem, LockScope> FileSystemScopes = new();
    private static readonly LockScope PhysicalFileSystemScope = new();

    internal static EmbeddedTransactionLock Get(IFileSystem fileSystem, string databasePath)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrEmpty(databasePath);

        var unwrapped = AhtolaEncryptionFileSystem.Unwrap(fileSystem);
        if (unwrapped is not PhysicalFileSystem)
            return FileSystemScopes.GetValue(unwrapped, static _ => new LockScope()).Get(databasePath);

        var key = Path.GetFullPath(databasePath);
        return PhysicalFileSystemScope.Get(OperatingSystem.IsWindows() ? key.ToUpperInvariant() : key);
    }
}
