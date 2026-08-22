using System.Diagnostics;

namespace Ahtola.Core.Storage;

/// <summary>
/// Acquires byte-range locks in SQLite's WAL shared-memory file. The component
/// uses the file only as a lock carrier: it neither maps nor writes the
/// WAL-index. Physical pagers separately coordinate multi-engine coexistence with
/// Stage 6 main-file SHARED ownership.
/// </summary>
/// <remarks>
/// SQLite reserves bytes 120 through 127 of <c>database-shm</c> for the WAL
/// write, checkpoint, recovery, and five reader locks. A checkpoint locks the
/// complete range exclusively; readers occupy one reader byte per process in
/// <b>shared</b> mode so concurrent engines (stock SQLite, Turso, other managed
/// pagers) can hold overlapping read marks. Writer and recovery ranges remain
/// exclusive. The lock file is deliberately retained on close because another
/// process can still be using it. See
/// <c>docs/wal-interoperability-contract.md</c> for the normative lock-byte map.
/// </remarks>
internal sealed class SqliteWalSharedMemoryLocks :
    ISqlitePagerLockCoordinator,
    IAsyncSqlitePagerLockCoordinator
{
    private const long WriteLockOffset = 120;
    private const long RecoveryLockOffset = 122;
    private const long FirstReaderLockOffset = 123;
    private const int ReaderLockCount = 5;
    private const long LockRangeLength = 8;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly SqliteWalByteRangeLock _byteRangeLocks;
    private Exception? _failure;
    private long? _readerLockOffset;
    private SqliteWalByteRangeLockLease? _readerLease;
    private int _readerReferenceCount;
    private int _activeRangeCount;

    internal SqliteWalSharedMemoryLocks(string databasePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        _path = string.Concat(Path.GetFullPath(databasePath), "-shm");
        _byteRangeLocks = new SqliteWalByteRangeLock(_path);
    }

    public IDisposable Acquire(SqlitePagerLockOperation operation, TimeSpan timeout)
        => Acquire(operation, timeout, pagerReadOnly: true);

    /// <summary>
    /// Acquires a range on behalf of one pager. A read-write pager may create the
    /// missing lock carrier on demand exactly like a native read-write connection
    /// does; a read-only pager must never create it (see the interoperability
    /// contract), so its reader locks fail while the carrier is absent.
    /// </summary>
    internal IDisposable Acquire(SqlitePagerLockOperation operation, TimeSpan timeout, bool pagerReadOnly)
        => operation switch
        {
            SqlitePagerLockOperation.Reader => AcquireReader(timeout, pagerReadOnly),
            SqlitePagerLockOperation.Writer => AcquireExclusiveRange(
                operation,
                WriteLockOffset,
                length: 1,
                timeout,
                allowCreate: true),
            SqlitePagerLockOperation.Checkpoint => AcquireExclusiveRange(
                operation,
                WriteLockOffset,
                LockRangeLength,
                timeout,
                allowCreate: true),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown SQLite WAL lock operation."),
        };

    public IDisposable AcquireRecovery(TimeSpan timeout)
        => AcquireExclusiveRange(
            SqlitePagerLockOperation.Writer,
            RecoveryLockOffset,
            length: 1,
            timeout,
            allowCreate: true);

    public ValueTask<IDisposable> AcquireAsync(
        SqlitePagerLockOperation operation,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => AcquireAsync(operation, timeout, pagerReadOnly: true, cancellationToken);

    internal ValueTask<IDisposable> AcquireAsync(
        SqlitePagerLockOperation operation,
        TimeSpan timeout,
        bool pagerReadOnly,
        CancellationToken cancellationToken = default)
        => operation switch
        {
            SqlitePagerLockOperation.Reader => AcquireReaderAsync(timeout, pagerReadOnly, cancellationToken),
            SqlitePagerLockOperation.Writer => AcquireExclusiveRangeAsync(
                operation,
                WriteLockOffset,
                length: 1,
                timeout,
                allowCreate: true,
                cancellationToken),
            SqlitePagerLockOperation.Checkpoint => AcquireExclusiveRangeAsync(
                operation,
                WriteLockOffset,
                LockRangeLength,
                timeout,
                allowCreate: true,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown SQLite WAL lock operation."),
        };

    public ValueTask<IDisposable> AcquireRecoveryAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => AcquireExclusiveRangeAsync(
            SqlitePagerLockOperation.Writer,
            RecoveryLockOffset,
            length: 1,
            timeout,
            allowCreate: true,
            cancellationToken);

    private IDisposable AcquireReader(TimeSpan timeout, bool pagerReadOnly)
    {
        EnsureCarrierExists(allowCreate: !pagerReadOnly);
        var stopwatch = StartTimeout(timeout);
        while (true)
        {
            Exception? lastContention = null;
            lock (_gate)
            {
                ThrowIfFailed();
                if (_readerReferenceCount != 0)
                {
                    _readerReferenceCount++;
                    return new ReaderLease(this);
                }

                for (var slot = 0; slot < ReaderLockCount; slot++)
                {
                    if (_byteRangeLocks.TryAcquireShared(
                            FirstReaderLockOffset + slot,
                            length: 1,
                            out var lease)
                        && lease is not null)
                    {
                        _readerLockOffset = FirstReaderLockOffset + slot;
                        _readerLease = lease;
                        _readerReferenceCount = 1;
                        _activeRangeCount++;
                        return new ReaderLease(this);
                    }

                    lastContention = null;
                }
            }

            if (!WaitForRetry(timeout, stopwatch))
                throw CreateBusyException(SqlitePagerLockOperation.Reader, timeout, lastContention as IOException);
        }
    }

    private IDisposable AcquireExclusiveRange(
        SqlitePagerLockOperation operation,
        long offset,
        long length,
        TimeSpan timeout,
        bool allowCreate)
    {
        EnsureCarrierExists(allowCreate);
        var stopwatch = StartTimeout(timeout);
        while (true)
        {
            Exception? contention = null;
            lock (_gate)
            {
                ThrowIfFailed();
                try
                {
                    if (_byteRangeLocks.TryAcquireExclusive(offset, length, out var lease)
                        && lease is not null)
                    {
                        _activeRangeCount++;
                        return new RangeLease(this, lease);
                    }

                }
                catch (IOException exception)
                {
                    contention = exception;
                }
                catch (UnauthorizedAccessException exception)
                {
                    contention = exception;
                }
            }

            if (!WaitForRetry(timeout, stopwatch))
                throw CreateBusyException(operation, timeout, contention as IOException);
        }
    }

    private async ValueTask<IDisposable> AcquireReaderAsync(
        TimeSpan timeout,
        bool pagerReadOnly,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureCarrierExists(allowCreate: !pagerReadOnly);
        var stopwatch = StartTimeout(timeout);
        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ThrowIfFailed();
                if (_readerReferenceCount != 0)
                {
                    _readerReferenceCount++;
                    return new ReaderLease(this);
                }

                for (var slot = 0; slot < ReaderLockCount; slot++)
                {
                    if (_byteRangeLocks.TryAcquireShared(
                            FirstReaderLockOffset + slot,
                            length: 1,
                            out var lease)
                        && lease is not null)
                    {
                        _readerLockOffset = FirstReaderLockOffset + slot;
                        _readerLease = lease;
                        _readerReferenceCount = 1;
                        _activeRangeCount++;
                        return new ReaderLease(this);
                    }
                }
            }

            if (!await SqliteBusyBackoff
                    .WaitAsync(attempt++, timeout, stopwatch, cancellationToken)
                    .ConfigureAwait(false))
            {
                throw CreateBusyException(SqlitePagerLockOperation.Reader, timeout, innerException: null);
            }
        }
    }

    private async ValueTask<IDisposable> AcquireExclusiveRangeAsync(
        SqlitePagerLockOperation operation,
        long offset,
        long length,
        TimeSpan timeout,
        bool allowCreate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureCarrierExists(allowCreate);
        var stopwatch = StartTimeout(timeout);
        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Exception? contention = null;
            lock (_gate)
            {
                ThrowIfFailed();
                try
                {
                    if (_byteRangeLocks.TryAcquireExclusive(offset, length, out var lease)
                        && lease is not null)
                    {
                        _activeRangeCount++;
                        return new RangeLease(this, lease);
                    }
                }
                catch (IOException exception)
                {
                    contention = exception;
                }
                catch (UnauthorizedAccessException exception)
                {
                    contention = exception;
                }
            }

            if (!await SqliteBusyBackoff
                    .WaitAsync(attempt++, timeout, stopwatch, cancellationToken)
                    .ConfigureAwait(false))
            {
                throw CreateBusyException(operation, timeout, contention as IOException);
            }
        }
    }

    private void EnsureCarrierExists(bool allowCreate)
    {
        if (File.Exists(_path))
            return;

        if (!allowCreate)
        {
            throw new InvalidOperationException(
                "Cannot safely open the managed database read-only because its WAL lock file is missing. "
                + "Creating that file would mutate storage.");
        }

        using var created = new FileStream(
            _path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1,
            FileOptions.None);
    }

    private void ReleaseReader()
    {
        lock (_gate)
        {
            if (_readerReferenceCount == 0)
                throw new InvalidOperationException("SQLite WAL reader lock reference count underflow.");

            if (_readerReferenceCount != 1)
            {
                _readerReferenceCount--;
                return;
            }

            var lease = _readerLease
                ?? throw new InvalidOperationException("SQLite WAL reader lock ownership is inconsistent.");
            ReleaseLease(lease);
            _readerReferenceCount = 0;
            _readerLockOffset = null;
            _readerLease = null;
        }
    }

    private void ReleaseRange(SqliteWalByteRangeLockLease lease)
    {
        lock (_gate)
            ReleaseLease(lease);
    }

    private void ReleaseLease(SqliteWalByteRangeLockLease lease)
    {
        if (_activeRangeCount == 0)
            throw new InvalidOperationException("SQLite WAL shared-memory lock ownership is inconsistent.");

        try
        {
            lease.Dispose();
        }
        catch (IOException exception)
        {
            _failure = exception;
            throw;
        }

        _activeRangeCount--;
    }

    private void ThrowIfFailed()
    {
        if (_failure is not null)
        {
            throw new InvalidOperationException(
                "SQLite WAL shared-memory lock release failed; refusing later lock acquisitions.",
                _failure);
        }
    }

    private static SqlitePagerBusyException CreateBusyException(
        SqlitePagerLockOperation operation,
        TimeSpan timeout,
        IOException? innerException)
        => new SqlitePagerBusyException(operation, timeout, innerException);

    private static Stopwatch? StartTimeout(TimeSpan timeout)
        => timeout == Timeout.InfiniteTimeSpan ? null : Stopwatch.StartNew();

    private static bool WaitForRetry(TimeSpan timeout, Stopwatch? stopwatch)
        => SqliteBusyBackoff.Wait(timeout, stopwatch);

    private sealed class ReaderLease : IDisposable
    {
        private SqliteWalSharedMemoryLocks? _owner;

        internal ReaderLease(SqliteWalSharedMemoryLocks owner) => _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.ReleaseReader();
        }
    }

    private sealed class RangeLease : IDisposable
    {
        private SqliteWalSharedMemoryLocks? _owner;
        private SqliteWalByteRangeLockLease? _lease;

        internal RangeLease(SqliteWalSharedMemoryLocks owner, SqliteWalByteRangeLockLease lease)
        {
            _owner = owner;
            _lease = lease;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            var lease = Interlocked.Exchange(ref _lease, null);
            if (owner is null || lease is null)
                return;

            owner.ReleaseRange(lease);
        }
    }
}
