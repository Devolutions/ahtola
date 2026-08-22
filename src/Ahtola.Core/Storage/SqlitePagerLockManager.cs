using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Ahtola.Core.Storage;

/// <summary>The current process-local lock state for a SQLite pager pair.</summary>
public enum SqlitePagerLockState
{
    Unlocked,
    Readers,
    Writer,
    WriterAndReaders,
    Checkpoint,
}

/// <summary>The operation represented by a <see cref="SqlitePagerLockLease"/>.</summary>
public enum SqlitePagerLockOperation
{
    Reader,
    Writer,
    Checkpoint,
}

/// <summary>
/// Stage 4 busy taxonomy aligned with SQLite's <c>SQLITE_BUSY</c>,
/// <c>SQLITE_BUSY_SNAPSHOT</c>, and <c>SQLITE_BUSY_RECOVERY</c> extended result
/// codes.
/// </summary>
public enum SqlitePagerBusyReason
{
    /// <summary>Ordinary lock contention (<c>SQLITE_BUSY</c>).</summary>
    Busy,

    /// <summary>
    /// The reader's WAL snapshot/mark is no longer valid
    /// (<c>SQLITE_BUSY_SNAPSHOT</c>).
    /// </summary>
    Snapshot,

    /// <summary>
    /// Recovery/checkpoint recovery locks could not be obtained
    /// (<c>SQLITE_BUSY_RECOVERY</c>).
    /// </summary>
    Recovery,
}

/// <summary>
/// Raised when a SQLite pager lock cannot be acquired before its configured
/// busy timeout expires.
/// </summary>
public sealed class SqlitePagerBusyException : InvalidOperationException
{
    public SqlitePagerBusyException(
        SqlitePagerLockOperation operation,
        TimeSpan timeout,
        Exception? innerException = null)
        : this(operation, SqlitePagerBusyReason.Busy, timeout, innerException)
    {
    }

    public SqlitePagerBusyException(
        SqlitePagerLockOperation operation,
        SqlitePagerBusyReason reason,
        TimeSpan timeout,
        Exception? innerException = null)
        : base(CreateMessage(operation, reason, timeout), innerException)
    {
        Operation = operation;
        Reason = reason;
        Timeout = timeout;
    }

    /// <summary>The requested lock operation.</summary>
    public SqlitePagerLockOperation Operation { get; }

    /// <summary>Stage 4 busy class for SQLite extended-result mapping.</summary>
    public SqlitePagerBusyReason Reason { get; }

    /// <summary>
    /// The requested busy timeout. Shared-memory byte-range locks report
    /// external contention immediately, so the pager retries lock acquisition
    /// until this timeout expires.
    /// </summary>
    public TimeSpan Timeout { get; }

    private static string CreateMessage(
        SqlitePagerLockOperation operation,
        SqlitePagerBusyReason reason,
        TimeSpan timeout)
        => reason switch
        {
            SqlitePagerBusyReason.Snapshot =>
                $"SQLite pager snapshot is busy; {operation} could not retain a valid WAL read mark within {timeout}.",
            SqlitePagerBusyReason.Recovery =>
                $"SQLite pager recovery is busy; {operation} recovery lock could not be acquired within {timeout}.",
            _ =>
                $"SQLite pager is busy; {operation} lock could not be acquired within {timeout}.",
        };
}

/// <summary>
/// Acquires and releases the external boundary for managed SQLite pager locks.
/// </summary>
/// <remarks>
/// An implementation must atomically own the requested role before returning a
/// lease, or throw. It must never report success without ownership, and disposing
/// the returned lease must release exactly that ownership. The coordinator's
/// timeout is the remaining portion of the pager's configured busy timeout.
/// Implementations are managed-pager coordination only; implementing this
/// interface does not make the pager interoperable with SQLite clients.
/// </remarks>
public interface ISqlitePagerLockCoordinator
{
    /// <summary>Acquires the external lock for a reader, writer, or checkpoint.</summary>
    IDisposable Acquire(SqlitePagerLockOperation operation, TimeSpan timeout);

    /// <summary>Acquires the external recovery lock for a writable pager open.</summary>
    IDisposable AcquireRecovery(TimeSpan timeout);
}

/// <summary>
/// Optional nonblocking counterpart for pager coordinators that can retry
/// external ownership without sleeping a worker thread.
/// </summary>
public interface IAsyncSqlitePagerLockCoordinator
{
    ValueTask<IDisposable> AcquireAsync(
        SqlitePagerLockOperation operation,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    ValueTask<IDisposable> AcquireRecoveryAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Coordinates readers, the single WAL writer, and checkpointing for SQLite
/// pagers. Readers may coexist with a writer; a checkpoint waits for all
/// readers and the writer, then excludes every role.
/// </summary>
/// <remarks>
/// The default manager for a physical filesystem uses the <c>-shm</c> lock-byte
/// range for managed writer, checkpoint, recovery, and reader coordination on
/// Windows and Linux. The pager separately holds exclusive process ownership of
/// SQLite's main-file lock-byte range for its lifetime because this manager does
/// not maintain SQLite's WAL-index. Explicitly supplied managers and
/// non-physical filesystems coordinate only the roles represented here.
/// </remarks>
public sealed class SqlitePagerLockManager
{
    private static readonly TimeSpan MaximumMonitorTimeout = TimeSpan.FromMilliseconds(int.MaxValue);
    private readonly object _gate = new();
    private readonly AsyncConditionPulse _stateChanged = new();
    private readonly ISqlitePagerLockCoordinator? _coordinator;
    private int _readerCount;
    private int _checkpointWaiterCount;
    private bool _writerActive;
    private bool _writerExclusivePending;
    private bool _checkpointActive;
    private long _generation;
    private long _journalModeGeneration;
    private long _walPublicationBoundary = long.MaxValue;
    private IDisposable? _sharedReaderLock;

    /// <summary>Creates a process-local SQLite pager lock manager.</summary>
    public SqlitePagerLockManager()
    {
    }

    /// <summary>
    /// Creates a lock manager with an external managed-pager coordinator.
    /// </summary>
    public SqlitePagerLockManager(ISqlitePagerLockCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _coordinator = coordinator;
    }

    /// <summary>The current lock state.</summary>
    public SqlitePagerLockState State
    {
        get
        {
            lock (_gate)
                return GetState();
        }
    }

    /// <summary>The number of active read snapshots.</summary>
    public int ActiveReaderCount
    {
        get
        {
            lock (_gate)
                return _readerCount;
        }
    }

    /// <summary>
    /// The number of checkpoints waiting for existing readers or a writer.
    /// New readers and writers yield to a waiting checkpoint so it cannot starve.
    /// </summary>
    public int WaitingCheckpointCount
    {
        get
        {
            lock (_gate)
                return _checkpointWaiterCount;
        }
    }

    /// <summary>
    /// A monotonically increasing value published after a WAL commit or
    /// checkpoint changes the shared storage view.
    /// </summary>
    public long Generation
    {
        get
        {
            lock (_gate)
                return _generation;
        }
    }

    /// <summary>
    /// The storage generation of the most recent journal-mode transition.
    /// Pagers opened before this generation must reopen even if the durable
    /// header has since made a complete round trip back to their original mode.
    /// </summary>
    internal long JournalModeGeneration
    {
        get
        {
            lock (_gate)
                return _journalModeGeneration;
        }
    }

    /// <summary>
    /// Whether this manager also coordinates ownership beyond the local process.
    /// </summary>
    internal bool UsesFileBackedWalLocks => _coordinator is not null;

    /// <summary>Acquires a read-snapshot lock.</summary>
    public SqlitePagerLockLease EnterReader(TimeSpan? busyTimeout = null)
        => Enter(SqlitePagerLockOperation.Reader, NormalizeTimeout(busyTimeout), pagerReadOnly: true);

    /// <summary>Asynchronously acquires a read-snapshot lock.</summary>
    public ValueTask<SqlitePagerLockLease> EnterReaderAsync(
        TimeSpan? busyTimeout = null,
        CancellationToken cancellationToken = default)
        => EnterAsync(
            SqlitePagerLockOperation.Reader,
            NormalizeTimeout(busyTimeout),
            pagerReadOnly: true,
            useExternalCoordinator: true,
            cancellationToken);

    /// <summary>
    /// Acquires a read-snapshot lock for a pager whose read-only state is known.
    /// A read-write pager may recreate a missing shared-memory lock carrier on
    /// demand like a native read-write connection; a read-only pager must not.
    /// </summary>
    internal SqlitePagerLockLease EnterReader(TimeSpan? busyTimeout, bool pagerReadOnly)
        => Enter(
            SqlitePagerLockOperation.Reader,
            NormalizeTimeout(busyTimeout),
            pagerReadOnly,
            useExternalCoordinator: true);

    internal SqlitePagerLockLease EnterReader(
        TimeSpan? busyTimeout,
        bool pagerReadOnly,
        bool useExternalCoordinator)
        => Enter(
            SqlitePagerLockOperation.Reader,
            NormalizeTimeout(busyTimeout),
            pagerReadOnly,
            useExternalCoordinator);

    /// <summary>Acquires the single WAL writer lock.</summary>
    public SqlitePagerLockLease EnterWriter(TimeSpan? busyTimeout = null)
        => Enter(SqlitePagerLockOperation.Writer, NormalizeTimeout(busyTimeout));

    /// <summary>Asynchronously acquires the single WAL writer lock.</summary>
    public ValueTask<SqlitePagerLockLease> EnterWriterAsync(
        TimeSpan? busyTimeout = null,
        CancellationToken cancellationToken = default)
        => EnterAsync(
            SqlitePagerLockOperation.Writer,
            NormalizeTimeout(busyTimeout),
            pagerReadOnly: false,
            useExternalCoordinator: true,
            cancellationToken);

    internal SqlitePagerLockLease EnterWriter(
        TimeSpan? busyTimeout,
        bool useExternalCoordinator)
        => Enter(
            SqlitePagerLockOperation.Writer,
            NormalizeTimeout(busyTimeout),
            pagerReadOnly: false,
            useExternalCoordinator);

    /// <summary>Acquires an exclusive checkpoint lock.</summary>
    public SqlitePagerLockLease EnterCheckpoint(TimeSpan? busyTimeout = null)
        => Enter(SqlitePagerLockOperation.Checkpoint, NormalizeTimeout(busyTimeout));

    /// <summary>Asynchronously acquires an exclusive checkpoint lock.</summary>
    public ValueTask<SqlitePagerLockLease> EnterCheckpointAsync(
        TimeSpan? busyTimeout = null,
        CancellationToken cancellationToken = default)
        => EnterAsync(
            SqlitePagerLockOperation.Checkpoint,
            NormalizeTimeout(busyTimeout),
            pagerReadOnly: false,
            useExternalCoordinator: true,
            cancellationToken);

    internal SqlitePagerLockLease EnterCheckpoint(
        TimeSpan? busyTimeout,
        bool useExternalCoordinator)
        => Enter(
            SqlitePagerLockOperation.Checkpoint,
            NormalizeTimeout(busyTimeout),
            pagerReadOnly: false,
            useExternalCoordinator);

    /// <summary>
    /// Acquires SQLite's WAL recovery lock for writable open and recovery.
    /// Process-local writer ownership must already prevent a second local
    /// recovery; this only adds the physical cross-process boundary.
    /// </summary>
    internal IDisposable? EnterRecoveryLock(TimeSpan timeout, TimeSpan configuredTimeout)
    {
        if (_coordinator is null)
            return null;
        if (timeout == TimeSpan.Zero && configuredTimeout != TimeSpan.Zero)
        {
            throw new SqlitePagerBusyException(
                SqlitePagerLockOperation.Writer,
                SqlitePagerBusyReason.Recovery,
                configuredTimeout);
        }

        ReleaseIdleSharedReaderLock();

        try
        {
            return _coordinator.AcquireRecovery(timeout)
                   ?? throw new InvalidOperationException("SQLite pager lock coordinator returned no recovery lease.");
        }
        catch (SqlitePagerBusyException exception)
        {
            throw new SqlitePagerBusyException(
                SqlitePagerLockOperation.Writer,
                SqlitePagerBusyReason.Recovery,
                configuredTimeout,
                exception);
        }
    }

    internal async ValueTask<IDisposable?> EnterRecoveryLockAsync(
        TimeSpan timeout,
        TimeSpan configuredTimeout,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_coordinator is null)
            return null;
        if (timeout == TimeSpan.Zero && configuredTimeout != TimeSpan.Zero)
        {
            throw new SqlitePagerBusyException(
                SqlitePagerLockOperation.Writer,
                SqlitePagerBusyReason.Recovery,
                configuredTimeout);
        }
        if (_coordinator is not IAsyncSqlitePagerLockCoordinator asyncCoordinator)
        {
            throw new NotSupportedException(
                "The SQLite pager lock coordinator does not support asynchronous acquisition.");
        }

        ReleaseIdleSharedReaderLock();
        try
        {
            return await asyncCoordinator
                       .AcquireRecoveryAsync(timeout, cancellationToken)
                       .ConfigureAwait(false)
                   ?? throw new InvalidOperationException("SQLite pager lock coordinator returned no recovery lease.");
        }
        catch (SqlitePagerBusyException exception)
        {
            throw new SqlitePagerBusyException(
                SqlitePagerLockOperation.Writer,
                SqlitePagerBusyReason.Recovery,
                configuredTimeout,
                exception);
        }
    }

    private SqlitePagerLockLease Enter(
        SqlitePagerLockOperation operation,
        TimeSpan timeout,
        bool pagerReadOnly = true,
        bool useExternalCoordinator = true)
    {
        var stopwatch = timeout == Timeout.InfiniteTimeSpan ? null : Stopwatch.StartNew();
        lock (_gate)
        {
            var checkpointWaiterRegistered = false;
            if (operation == SqlitePagerLockOperation.Checkpoint)
            {
                _checkpointWaiterCount++;
                checkpointWaiterRegistered = true;
            }

            try
            {
                while (!CanEnter(operation))
                {
                    var remaining = RemainingTimeout(timeout, stopwatch);
                    if (remaining == TimeSpan.Zero)
                        throw new SqlitePagerBusyException(operation, timeout);

                    Monitor.Wait(_gate, remaining);
                }

                Acquire(operation);
            }
            finally
            {
                if (checkpointWaiterRegistered)
                {
                    _checkpointWaiterCount--;
                    Monitor.PulseAll(_gate);
                    _stateChanged.PulseAll();
                }
            }

        }

        IDisposable? externalLock = null;
        var leaseHandedOff = false;
        try
        {
            if (_coordinator is not null && useExternalCoordinator)
            {
                var coordinatorTimeout = RemainingFileLockTimeout(timeout, stopwatch);
                if (coordinatorTimeout == TimeSpan.Zero && timeout != TimeSpan.Zero)
                    throw new SqlitePagerBusyException(operation, timeout);

                if (operation == SqlitePagerLockOperation.Reader)
                {
                    AcquireSharedReaderLock(coordinatorTimeout, pagerReadOnly);
                }
                else
                {
                    // A writer coexists with readers, but a checkpoint or recovery must
                    // exclude them, and this manager just proved no local reader is active.
                    if (operation != SqlitePagerLockOperation.Writer)
                        ReleaseIdleSharedReaderLock();

                    externalLock = _coordinator.Acquire(operation, coordinatorTimeout)
                        ?? throw new InvalidOperationException("SQLite pager lock coordinator returned no lease.");
                }
            }

            var lease = new SqlitePagerLockLease(this, operation, externalLock);
            leaseHandedOff = true;
            return lease;
        }
        catch (SqlitePagerBusyException exception)
        {
            throw new SqlitePagerBusyException(operation, timeout, exception);
        }
        finally
        {
            if (!leaseHandedOff)
            {
                externalLock?.Dispose();
                ExitLocal(operation);
            }
        }
    }

    private async ValueTask<SqlitePagerLockLease> EnterAsync(
        SqlitePagerLockOperation operation,
        TimeSpan timeout,
        bool pagerReadOnly,
        bool useExternalCoordinator,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = timeout == Timeout.InfiniteTimeSpan ? null : Stopwatch.StartNew();
        var checkpointWaiterRegistered = false;
        var localAcquired = false;
        try
        {
            while (true)
            {
                Task stateChanged;
                TimeSpan remaining;
                lock (_gate)
                {
                    if (!checkpointWaiterRegistered
                        && operation == SqlitePagerLockOperation.Checkpoint)
                    {
                        _checkpointWaiterCount++;
                        checkpointWaiterRegistered = true;
                        _stateChanged.PulseAll();
                    }

                    if (CanEnter(operation))
                    {
                        Acquire(operation);
                        localAcquired = true;
                        break;
                    }

                    remaining = RemainingTimeout(timeout, stopwatch);
                    if (remaining == TimeSpan.Zero)
                        throw new SqlitePagerBusyException(operation, timeout);
                    stateChanged = _stateChanged.Capture();
                }

                try
                {
                    if (remaining == Timeout.InfiniteTimeSpan)
                        await stateChanged.WaitAsync(cancellationToken).ConfigureAwait(false);
                    else
                        await stateChanged.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    throw new SqlitePagerBusyException(operation, timeout);
                }
            }
        }
        finally
        {
            if (checkpointWaiterRegistered)
            {
                lock (_gate)
                {
                    _checkpointWaiterCount--;
                    Monitor.PulseAll(_gate);
                    _stateChanged.PulseAll();
                }
            }
        }

        IDisposable? externalLock = null;
        var leaseHandedOff = false;
        try
        {
            if (_coordinator is not null && useExternalCoordinator)
            {
                var coordinatorTimeout = RemainingFileLockTimeout(timeout, stopwatch);
                if (coordinatorTimeout == TimeSpan.Zero && timeout != TimeSpan.Zero)
                    throw new SqlitePagerBusyException(operation, timeout);
                if (_coordinator is not IAsyncSqlitePagerLockCoordinator asyncCoordinator)
                {
                    throw new NotSupportedException(
                        "The SQLite pager lock coordinator does not support asynchronous acquisition.");
                }

                if (operation == SqlitePagerLockOperation.Reader)
                {
                    await AcquireSharedReaderLockAsync(
                            asyncCoordinator,
                            coordinatorTimeout,
                            pagerReadOnly,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    if (operation != SqlitePagerLockOperation.Writer)
                        ReleaseIdleSharedReaderLock();

                    externalLock = await asyncCoordinator
                                           .AcquireAsync(operation, coordinatorTimeout, cancellationToken)
                                           .ConfigureAwait(false)
                                       ?? throw new InvalidOperationException(
                                           "SQLite pager lock coordinator returned no lease.");
                }
            }

            var lease = new SqlitePagerLockLease(this, operation, externalLock);
            leaseHandedOff = true;
            return lease;
        }
        catch (SqlitePagerBusyException exception)
        {
            throw new SqlitePagerBusyException(operation, timeout, exception);
        }
        finally
        {
            if (!leaseHandedOff && localAcquired)
            {
                externalLock?.Dispose();
                ExitLocal(operation);
            }
        }
    }

    internal long PublishStorageChange(SqlitePagerLockOperation operation)
    {
        if (operation is not SqlitePagerLockOperation.Writer and not SqlitePagerLockOperation.Checkpoint)
            throw new InvalidOperationException("Only writer and checkpoint leases can publish a SQLite storage change.");

        lock (_gate)
        {
            if (operation == SqlitePagerLockOperation.Writer && !_writerActive)
                throw new InvalidOperationException("The SQLite writer lease is no longer active.");
            if (operation == SqlitePagerLockOperation.Checkpoint && !_checkpointActive)
                throw new InvalidOperationException("The SQLite checkpoint lease is no longer active.");

            return checked(++_generation);
        }
    }

    internal void UpgradeWriterToExclusive(TimeSpan timeout)
    {
        var stopwatch = timeout == Timeout.InfiniteTimeSpan ? null : Stopwatch.StartNew();
        lock (_gate)
        {
            if (!_writerActive)
                throw new InvalidOperationException("The SQLite writer lease is no longer active.");

            _writerExclusivePending = true;
            while (_readerCount != 0)
            {
                var remaining = RemainingTimeout(timeout, stopwatch);
                if (remaining == TimeSpan.Zero)
                    throw new SqlitePagerBusyException(SqlitePagerLockOperation.Writer, timeout);
                Monitor.Wait(_gate, remaining);
            }
        }
    }

    /// <summary>
    /// Asynchronously upgrades the active writer lease to EXCLUSIVE without
    /// blocking a thread while existing readers drain.
    /// </summary>
    /// <remarks>
    /// New readers are excluded the moment the upgrade is requested, so a steady
    /// stream of arrivals cannot starve the writer. A failed upgrade drops the
    /// pending flag again: the caller falls back to RESERVED, which SQLite does
    /// not let block readers.
    /// </remarks>
    internal async ValueTask UpgradeWriterToExclusiveAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = timeout == Timeout.InfiniteTimeSpan ? null : Stopwatch.StartNew();
        var upgraded = false;
        var pendingRegistered = false;
        try
        {
            while (true)
            {
                Task stateChanged;
                TimeSpan remaining;
                lock (_gate)
                {
                    if (!_writerActive)
                        throw new InvalidOperationException("The SQLite writer lease is no longer active.");

                    if (!pendingRegistered)
                    {
                        _writerExclusivePending = true;
                        pendingRegistered = true;
                    }

                    if (_readerCount == 0)
                    {
                        upgraded = true;
                        return;
                    }

                    remaining = RemainingTimeout(timeout, stopwatch);
                    if (remaining == TimeSpan.Zero)
                        throw new SqlitePagerBusyException(SqlitePagerLockOperation.Writer, timeout);
                    stateChanged = _stateChanged.Capture();
                }

                try
                {
                    if (remaining == Timeout.InfiniteTimeSpan)
                        await stateChanged.WaitAsync(cancellationToken).ConfigureAwait(false);
                    else
                        await stateChanged.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    throw new SqlitePagerBusyException(SqlitePagerLockOperation.Writer, timeout);
                }
            }
        }
        finally
        {
            if (!upgraded && pendingRegistered)
            {
                lock (_gate)
                {
                    if (_writerActive)
                        _writerExclusivePending = false;
                    Monitor.PulseAll(_gate);
                    _stateChanged.PulseAll();
                }
            }
        }
    }

    /// <summary>
    /// The highest WAL frame this process has published to readers, or
    /// <see cref="long.MaxValue"/> when the physical WAL is authoritative.
    /// </summary>
    /// <remarks>
    /// A commit-bearing frame reaches the WAL file before its durable flush
    /// succeeds. Without a boundary, a concurrent reader that rescans the physical
    /// WAL would adopt a transaction that is still in flight and may yet fail. The
    /// committing writer lowers this boundary to the last published frame before
    /// appending and raises it again only once the flush and post-flush validation
    /// have both succeeded. The boundary is deliberately process-local and resets
    /// to unbounded on open, checkpoint, and WAL tail recovery, so crash recovery
    /// across a process or open boundary still derives visibility from the file.
    /// </remarks>
    internal long WalPublicationBoundary
    {
        get
        {
            lock (_gate)
                return _walPublicationBoundary;
        }
    }

    /// <summary>Hides WAL frames past <paramref name="lastPublishedFrameNumber"/> from readers.</summary>
    internal void BeginWalPublication(long lastPublishedFrameNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lastPublishedFrameNumber);
        lock (_gate)
        {
            if (!_writerActive)
                throw new InvalidOperationException("Only an active SQLite writer lease can publish WAL frames.");

            _walPublicationBoundary = lastPublishedFrameNumber;
        }
    }

    /// <summary>Republishes the physical WAL after a durable commit or recovery.</summary>
    internal void PublishAllWalFrames()
    {
        lock (_gate)
        {
            _walPublicationBoundary = long.MaxValue;
            Monitor.PulseAll(_gate);
            _stateChanged.PulseAll();
        }
    }

    internal long PublishJournalModeChange(SqlitePagerLockOperation operation)
    {
        lock (_gate)
        {
            var generation = PublishStorageChange(operation);
            _journalModeGeneration = generation;
            return generation;
        }
    }

    internal void Exit(SqlitePagerLockOperation operation, IDisposable? fileLock)
    {
        try
        {
            fileLock?.Dispose();
        }
        finally
        {
            ExitLocal(operation);
        }
    }

    private void ExitLocal(SqlitePagerLockOperation operation)
    {
        lock (_gate)
        {
            switch (operation)
            {
                case SqlitePagerLockOperation.Reader:
                    if (_readerCount == 0)
                        throw new InvalidOperationException("SQLite reader lock count underflow.");
                    _readerCount--;
                    break;
                case SqlitePagerLockOperation.Writer:
                    if (!_writerActive)
                        throw new InvalidOperationException("SQLite writer lock is not active.");
                    _writerExclusivePending = false;
                    _writerActive = false;
                    break;
                case SqlitePagerLockOperation.Checkpoint:
                    if (!_checkpointActive)
                        throw new InvalidOperationException("SQLite checkpoint lock is not active.");
                    _checkpointActive = false;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown SQLite lock operation.");
            }

            Monitor.PulseAll(_gate);
            _stateChanged.PulseAll();
        }
    }

    /// <summary>
    /// Acquires the coordinator's shared reader range once and keeps it while any
    /// local reader is active. A per-page acquisition would otherwise cost an
    /// operating-system lock round trip for every committed page read.
    /// </summary>
    private void AcquireSharedReaderLock(TimeSpan timeout, bool pagerReadOnly)
    {
        lock (_gate)
        {
            if (_sharedReaderLock is not null)
                return;
        }

        var acquired = (_coordinator is SqliteWalSharedMemoryLocks sharedMemoryLocks
                ? sharedMemoryLocks.Acquire(SqlitePagerLockOperation.Reader, timeout, pagerReadOnly)
                : _coordinator!.Acquire(SqlitePagerLockOperation.Reader, timeout))
            ?? throw new InvalidOperationException("SQLite pager lock coordinator returned no lease.");

        var redundant = false;
        lock (_gate)
        {
            if (_sharedReaderLock is null)
                _sharedReaderLock = acquired;
            else
                redundant = true;
        }

        if (redundant)
            acquired.Dispose();
    }

    private async ValueTask AcquireSharedReaderLockAsync(
        IAsyncSqlitePagerLockCoordinator asyncCoordinator,
        TimeSpan timeout,
        bool pagerReadOnly,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_sharedReaderLock is not null)
                return;
        }

        var acquired = _coordinator is SqliteWalSharedMemoryLocks sharedMemoryLocks
            ? await sharedMemoryLocks
                .AcquireAsync(
                    SqlitePagerLockOperation.Reader,
                    timeout,
                    pagerReadOnly,
                    cancellationToken)
                .ConfigureAwait(false)
            : await asyncCoordinator
                .AcquireAsync(SqlitePagerLockOperation.Reader, timeout, cancellationToken)
                .ConfigureAwait(false);
        if (acquired is null)
            throw new InvalidOperationException("SQLite pager lock coordinator returned no lease.");

        var redundant = false;
        lock (_gate)
        {
            if (_sharedReaderLock is null)
                _sharedReaderLock = acquired;
            else
                redundant = true;
        }

        if (redundant)
            acquired.Dispose();
    }

    /// <summary>
    /// Releases the retained shared reader range so an exclusive role can take it.
    /// Callers must already have established that no local reader is active.
    /// </summary>
    private void ReleaseIdleSharedReaderLock()
    {
        IDisposable? retained;
        lock (_gate)
        {
            if (_readerCount != 0)
                return;

            retained = _sharedReaderLock;
            _sharedReaderLock = null;
        }

        retained?.Dispose();
    }

    /// <summary>
    /// Releases the retained shared reader range during pager teardown, which ends
    /// every reader this manager could still be holding it for.
    /// </summary>
    internal void ReleaseRetainedSharedReaderLock()
    {
        IDisposable? retained;
        lock (_gate)
        {
            retained = _sharedReaderLock;
            _sharedReaderLock = null;
        }

        retained?.Dispose();
    }

    private bool CanEnter(SqlitePagerLockOperation operation)
        => operation switch
        {
            SqlitePagerLockOperation.Reader =>
                !_checkpointActive
                && !_writerExclusivePending
                && _checkpointWaiterCount == 0,
            SqlitePagerLockOperation.Writer => !_writerActive && !_checkpointActive && _checkpointWaiterCount == 0,
            SqlitePagerLockOperation.Checkpoint => !_writerActive && !_checkpointActive && _readerCount == 0,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown SQLite lock operation."),
        };

    private void Acquire(SqlitePagerLockOperation operation)
    {
        switch (operation)
        {
            case SqlitePagerLockOperation.Reader:
                _readerCount++;
                break;
            case SqlitePagerLockOperation.Writer:
                _writerActive = true;
                break;
            case SqlitePagerLockOperation.Checkpoint:
                _checkpointActive = true;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown SQLite lock operation.");
        }
    }

    private SqlitePagerLockState GetState()
    {
        if (_checkpointActive)
            return SqlitePagerLockState.Checkpoint;
        if (_writerActive)
            return _readerCount == 0 ? SqlitePagerLockState.Writer : SqlitePagerLockState.WriterAndReaders;
        return _readerCount == 0 ? SqlitePagerLockState.Unlocked : SqlitePagerLockState.Readers;
    }

    private static TimeSpan NormalizeTimeout(TimeSpan? busyTimeout)
    {
        var timeout = busyTimeout ?? TimeSpan.Zero;
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(busyTimeout), "Busy timeout must be non-negative or infinite.");

        return timeout;
    }

    private static TimeSpan RemainingTimeout(TimeSpan timeout, Stopwatch? stopwatch)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
            return Timeout.InfiniteTimeSpan;

        var remaining = timeout - stopwatch!.Elapsed;
        if (remaining <= TimeSpan.Zero)
            return TimeSpan.Zero;

        return remaining > MaximumMonitorTimeout ? MaximumMonitorTimeout : remaining;
    }

    internal static TimeSpan RemainingFileLockTimeout(TimeSpan timeout, Stopwatch? stopwatch)
    {
        if (timeout == TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
            return timeout;

        var remaining = timeout - stopwatch!.Elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
}

/// <summary>
/// A process-local SQLite pager lock lease. Dispose it exactly once to release
/// its role; writer and checkpoint leases publish durable changes explicitly.
/// </summary>
public sealed class SqlitePagerLockLease : IDisposable
{
    private SqlitePagerLockManager? _manager;
    private IDisposable? _fileLock;

    internal SqlitePagerLockLease(
        SqlitePagerLockManager manager,
        SqlitePagerLockOperation operation,
        IDisposable? fileLock)
    {
        _manager = manager;
        _fileLock = fileLock;
        Operation = operation;
    }

    /// <summary>The operation this lease protects.</summary>
    public SqlitePagerLockOperation Operation { get; }

    /// <summary>Whether this lease still holds its lock.</summary>
    public bool IsActive => Volatile.Read(ref _manager) is not null;

    internal long PublishStorageChange()
    {
        var manager = Volatile.Read(ref _manager)
            ?? throw new ObjectDisposedException(nameof(SqlitePagerLockLease));
        return manager.PublishStorageChange(Operation);
    }

    internal long PublishJournalModeChange()
    {
        var manager = Volatile.Read(ref _manager)
            ?? throw new ObjectDisposedException(nameof(SqlitePagerLockLease));
        return manager.PublishJournalModeChange(Operation);
    }

    internal void UpgradeToExclusive(TimeSpan timeout)
    {
        var manager = Volatile.Read(ref _manager)
            ?? throw new ObjectDisposedException(nameof(SqlitePagerLockLease));
        if (Operation != SqlitePagerLockOperation.Writer)
            throw new InvalidOperationException("Only a SQLite writer lease can upgrade to EXCLUSIVE.");
        manager.UpgradeWriterToExclusive(timeout);
    }

    internal ValueTask UpgradeToExclusiveAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var manager = Volatile.Read(ref _manager)
            ?? throw new ObjectDisposedException(nameof(SqlitePagerLockLease));
        if (Operation != SqlitePagerLockOperation.Writer)
            throw new InvalidOperationException("Only a SQLite writer lease can upgrade to EXCLUSIVE.");
        return manager.UpgradeWriterToExclusiveAsync(timeout, cancellationToken);
    }

    internal void BeginWalPublication(long lastPublishedFrameNumber)
    {
        var manager = Volatile.Read(ref _manager)
            ?? throw new ObjectDisposedException(nameof(SqlitePagerLockLease));
        manager.BeginWalPublication(lastPublishedFrameNumber);
    }

    internal void PublishWalFrames()
    {
        var manager = Volatile.Read(ref _manager)
            ?? throw new ObjectDisposedException(nameof(SqlitePagerLockLease));
        manager.PublishAllWalFrames();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        var manager = Interlocked.Exchange(ref _manager, null);
        var fileLock = Interlocked.Exchange(ref _fileLock, null);
        manager?.Exit(Operation, fileLock);
    }
}

internal static class SqlitePagerLockRegistry
{
    private sealed class LockScope
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, SqlitePagerLockManager> _locks = new(StringComparer.Ordinal);

        internal SqlitePagerLockManager GetOrAdd(string key, Func<SqlitePagerLockManager> create)
        {
            lock (_gate)
            {
                if (!_locks.TryGetValue(key, out var manager))
                {
                    manager = create();
                    _locks.Add(key, manager);
                }

                return manager;
            }
        }
    }

    private static readonly ConditionalWeakTable<IFileSystem, LockScope> FileSystemScopes = new();
    private static readonly LockScope PhysicalFileSystemScope = new();

    internal static SqlitePagerLockManager Get(IFileSystem fileSystem, string databasePath, string walPath)
    {
        fileSystem = AhtolaEncryptionFileSystem.Unwrap(fileSystem);
        var key = CreateKey(fileSystem, databasePath, walPath);
        var scope = fileSystem is PhysicalFileSystem
            ? PhysicalFileSystemScope
            : FileSystemScopes.GetValue(fileSystem, static _ => new LockScope());
        return scope.GetOrAdd(
            key,
            () => fileSystem is PhysicalFileSystem
                ? new SqlitePagerLockManager(new SqliteWalSharedMemoryLocks(databasePath))
                : new SqlitePagerLockManager());
    }

    /// <summary>
    /// A process-local lock scope for foreign read-only pagers. These never touch
    /// the shared-memory file, so their coordinator only serializes other foreign
    /// readers (and their rescan state) inside this process. The key is prefixed so
    /// a foreign pager never shares its coordinator with an owned pager on the same
    /// database: an owned pager may legitimately write the file while foreign
    /// readers rescan it.
    /// </summary>
    internal static SqlitePagerLockManager GetProcessLocal(IFileSystem fileSystem, string databasePath, string walPath)
    {
        fileSystem = AhtolaEncryptionFileSystem.Unwrap(fileSystem);
        var key = "foreign\0" + CreateKey(fileSystem, databasePath, walPath);
        var scope = fileSystem is PhysicalFileSystem
            ? PhysicalFileSystemScope
            : FileSystemScopes.GetValue(fileSystem, static _ => new LockScope());
        return scope.GetOrAdd(key, static () => new SqlitePagerLockManager());
    }

    private static string CreateKey(IFileSystem fileSystem, string databasePath, string walPath)
    {
        if (fileSystem is PhysicalFileSystem)
        {
            databasePath = Path.GetFullPath(databasePath);
            walPath = Path.GetFullPath(walPath);
            var key = string.Concat(databasePath, "\0", walPath);
            return OperatingSystem.IsWindows() ? key.ToUpperInvariant() : key;
        }

        return string.Concat(databasePath, "\0", walPath);
    }
}
