using System.Diagnostics;

namespace Ahtola.Core.Storage;

/// <summary>
/// Raised when every SQLite WAL read-mark slot remains unavailable while a
/// detached read snapshot is being established.
/// </summary>
public sealed class SqliteWalReadSnapshotBusyException : InvalidOperationException
{
    internal SqliteWalReadSnapshotBusyException(TimeSpan timeout)
        : base($"SQLite WAL read-mark locks could not establish a read snapshot within {timeout}.")
    {
        Timeout = timeout;
    }

    /// <summary>The requested bounded acquisition timeout.</summary>
    public TimeSpan Timeout { get; }
}

/// <summary>
/// Raised when a held WAL read mark is reset or rewritten while a snapshot is
/// still active (Stage 4 <c>SQLITE_BUSY_SNAPSHOT</c>).
/// </summary>
public sealed class SqliteWalReadSnapshotInvalidatedException : InvalidOperationException
{
    internal SqliteWalReadSnapshotInvalidatedException(
        int readMarkIndex,
        uint expectedMaximumFrame,
        uint actualMaximumFrame)
        : base(
            $"SQLite WAL read mark {readMarkIndex} changed from frame {expectedMaximumFrame} to {actualMaximumFrame} while its shared lock was held.")
    {
        ReadMarkIndex = readMarkIndex;
        ExpectedMaximumFrame = expectedMaximumFrame;
        ActualMaximumFrame = actualMaximumFrame;
    }

    /// <summary>The read-mark slot that was invalidated.</summary>
    public int ReadMarkIndex { get; }

    /// <summary>The frame boundary pinned when the snapshot began.</summary>
    public uint ExpectedMaximumFrame { get; }

    /// <summary>The frame boundary observed after the mark changed.</summary>
    public uint ActualMaximumFrame { get; }
}

/// <summary>
/// Establishes detached, SQLite-compatible WAL read snapshots over existing
/// physical WAL and shared-memory artifacts.
/// </summary>
/// <remarks>
/// The physical <see cref="SqlitePager"/> may compose this coordinator for
/// Stage 2 read-mark pinning under Stage 0 ownership. Detached construction
/// (over SQLite-produced artifacts without a pager) remains supported for
/// protocol tests. This type still does not relax main-file ownership, perform
/// recovery/checkpoint, or establish stock-SQLite concurrent interoperability
/// by itself — Stages 3–6 remain separate.
/// </remarks>
public sealed class SqliteWalReadSnapshotCoordinator : IDisposable
{
    private const long FirstReadMarkLockOffset = SqliteWalIndexCheckpointInfo.LockOffset + 3;
    private const int ReadMarkCount = SqliteWalIndexCheckpointInfo.ReadMarkCount;

    private readonly object _gate = new();
    private readonly SqliteWalFile _wal;
    private readonly SqliteWalIndexSharedMemory _index;
    private readonly SqliteWalByteRangeLock _locks;
    private readonly IDisposable? _ownedMapping;
    private readonly bool _ownsWal;
    private readonly HashSet<SqliteWalReadSnapshot> _snapshots = [];
    private bool _disposed;

    /// <summary>
    /// Creates a detached coordinator over caller-owned WAL, WAL-index, and lock
    /// primitives.
    /// </summary>
    public SqliteWalReadSnapshotCoordinator(
        SqliteWalFile wal,
        SqliteWalIndexSharedMemory index,
        SqliteWalByteRangeLock locks)
    {
        ArgumentNullException.ThrowIfNull(wal);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(locks);
        _wal = wal;
        _index = index;
        _locks = locks;
    }

    private SqliteWalReadSnapshotCoordinator(
        SqliteWalFile wal,
        SqliteWalIndexSharedMemory index,
        SqliteWalByteRangeLock locks,
        IDisposable ownedMapping)
        : this(wal, index, locks)
    {
        _ownedMapping = ownedMapping;
        _ownsWal = true;
    }

    /// <summary>
    /// Opens the existing physical WAL and shared-memory artifacts required by a
    /// detached read snapshot without creating any companion file.
    /// </summary>
    public static SqliteWalReadSnapshotCoordinator Open(string databasePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);

        var canonicalDatabasePath = Path.GetFullPath(databasePath);
        var sharedFileSystem = new SqlitePagerPhysicalFileSystem(PhysicalFileSystem.Instance);
        var wal = SqliteWalFile.Open(
            sharedFileSystem,
            string.Concat(canonicalDatabasePath, "-wal"),
            readOnly: true);
        ISqliteWalSharedMemoryMapping? mapping = null;
        try
        {
            mapping = ((ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance).OpenSharedMemory(
                string.Concat(canonicalDatabasePath, "-shm"),
                FileOpenMode.OpenExisting);
            var index = new SqliteWalIndexSharedMemory(mapping);
            var locks = new SqliteWalByteRangeLock(string.Concat(canonicalDatabasePath, "-shm"));
            return new SqliteWalReadSnapshotCoordinator(wal, index, locks, mapping);
        }
        catch
        {
            mapping?.Dispose();
            wal.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Acquires a shared SQLite WAL read-mark lock and pins the matching committed
    /// WAL frame boundary until the returned snapshot is reset or disposed.
    /// </summary>
    public SqliteWalReadSnapshot BeginRead(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeout);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        SqliteWalReadSnapshot? snapshot = null;
        try
        {
            snapshot = BeginReadCore(timeout, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ThrowIfDisposed();
                _snapshots.Add(snapshot);
            }

            return snapshot;
        }
        catch
        {
            snapshot?.Reset();
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        SqliteWalReadSnapshot[] snapshots;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            snapshots = [.. _snapshots];
            _snapshots.Clear();
        }

        List<Exception>? failures = null;
        foreach (var snapshot in snapshots)
        {
            try
            {
                snapshot.Reset();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (_ownedMapping is not null)
        {
            try
            {
                _ownedMapping.Dispose();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }
        if (_ownsWal)
        {
            try
            {
                _wal.Dispose();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is { Count: > 0 })
            throw new AggregateException("Failed to release one or more SQLite WAL read-snapshot resources.", failures);
    }

    internal void RemoveSnapshot(SqliteWalReadSnapshot snapshot)
    {
        lock (_gate)
            _snapshots.Remove(snapshot);
    }

    private SqliteWalReadSnapshot BeginReadCore(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = timeout == Timeout.InfiniteTimeSpan ? null : Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var region = _index.ReadValidatedHeader(_wal);
            if (IsDatabaseOnlySnapshot(region))
            {
                if (_locks.TryAcquireShared(FirstReadMarkLockOffset, length: 1, out var readMarkLease))
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var confirmation = _index.ReadValidatedHeader(_wal);
                        if (IsDatabaseOnlySnapshot(confirmation))
                            return new SqliteWalReadSnapshot(
                                this,
                                _wal,
                                _index,
                                readMarkLease
                                    ?? throw new InvalidOperationException(
                                        "SQLite WAL read-mark locking reported success without returning a lease."),
                                readMarkIndex: 0,
                                maximumFrame: 0,
                                confirmation.Header.DatabasePageCount);
                    }
                    catch
                    {
                        readMarkLease?.Dispose();
                        throw;
                    }

                    readMarkLease?.Dispose();
                }
            }
            else
            {
                SqliteWalReadSnapshot snapshot;
                foreach (var candidate in SelectExistingReadMarks(region))
                {
                    if (candidate.Frame != region.Header.MaximumFrame)
                        break;

                    if (TryAcquireExistingReadMark(
                            candidate.Index,
                            candidate.Frame,
                            cancellationToken,
                            out snapshot))
                    {
                        return snapshot;
                    }
                }

                if (TryAdvanceReadMark(cancellationToken, out snapshot))
                    return snapshot;

                foreach (var candidate in SelectExistingReadMarks(region))
                {
                    if (TryAcquireExistingReadMark(
                            candidate.Index,
                            candidate.Frame,
                            cancellationToken,
                            out snapshot))
                    {
                        return snapshot;
                    }
                }

            }

            WaitForRetry(timeout, stopwatch, cancellationToken);
        }
    }

    private bool TryAcquireExistingReadMark(
        int readMarkIndex,
        uint maximumFrame,
        CancellationToken cancellationToken,
        out SqliteWalReadSnapshot snapshot)
    {
        snapshot = null!;
        if (!_locks.TryAcquireShared(GetReadMarkLockOffset(readMarkIndex), length: 1, out var readMarkLease))
            return false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var confirmation = _index.ReadValidatedHeader(_wal);
            if (IsDatabaseOnlySnapshot(confirmation)
                || confirmation.Header.MaximumFrame < maximumFrame
                || confirmation.CheckpointInfo.GetReadMark(readMarkIndex) != maximumFrame)
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            snapshot = CreateWalSnapshot(
                readMarkLease
                    ?? throw new InvalidOperationException(
                        "SQLite WAL read-mark locking reported success without returning a lease."),
                readMarkIndex,
                maximumFrame);
            readMarkLease = null;
            return true;
        }
        finally
        {
            readMarkLease?.Dispose();
        }
    }

    private bool TryAdvanceReadMark(
        CancellationToken cancellationToken,
        out SqliteWalReadSnapshot snapshot)
    {
        snapshot = null!;
        for (var readMarkIndex = 1; readMarkIndex < ReadMarkCount; readMarkIndex++)
        {
            if (!_locks.TryAcquireExclusive(GetReadMarkLockOffset(readMarkIndex), length: 1, out var exclusiveLease))
                continue;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var region = _index.ReadValidatedHeader(_wal);
                if (IsDatabaseOnlySnapshot(region))
                    return false;

                _index.PublishReadMark(readMarkIndex, region.Header.MaximumFrame);
                var maximumFrame = region.Header.MaximumFrame;
                (exclusiveLease
                    ?? throw new InvalidOperationException(
                        "SQLite WAL read-mark locking reported success without returning a lease."))
                    .Dispose();
                exclusiveLease = null;

                if (TryAcquireExistingReadMark(
                        readMarkIndex,
                        maximumFrame,
                        cancellationToken,
                        out snapshot))
                {
                    return true;
                }
            }
            finally
            {
                exclusiveLease?.Dispose();
            }
        }

        return false;
    }

    private SqliteWalReadSnapshot CreateWalSnapshot(
        SqliteWalByteRangeLockLease readMarkLease,
        int readMarkIndex,
        uint maximumFrame)
    {
        try
        {
            var committedFrame = _wal.ReadFrame(maximumFrame);
            if (!committedFrame.Header.IsCommit)
            {
                throw new InvalidDataException(
                    $"SQLite WAL read mark {readMarkIndex} names frame {maximumFrame}, which is not a commit frame.");
            }

            return new SqliteWalReadSnapshot(
                this,
                _wal,
                _index,
                readMarkLease,
                readMarkIndex,
                maximumFrame,
                committedFrame.Header.DatabaseSizeInPages);
        }
        catch
        {
            readMarkLease.Dispose();
            throw;
        }
    }

    private static bool IsDatabaseOnlySnapshot(SqliteWalIndexHeaderRegion region)
        => region.Header.MaximumFrame == region.CheckpointInfo.BackfilledFrameCount;

    private static List<(int Index, uint Frame)> SelectExistingReadMarks(SqliteWalIndexHeaderRegion region)
    {
        var candidates = new List<(int Index, uint Frame)>(ReadMarkCount - 1);
        for (var readMarkIndex = 1; readMarkIndex < ReadMarkCount; readMarkIndex++)
        {
            var candidate = region.CheckpointInfo.GetReadMark(readMarkIndex);
            if (candidate == 0
                || candidate == SqliteWalIndexCheckpointInfo.ReadMarkNotUsed
                || candidate > region.Header.MaximumFrame)
            {
                continue;
            }

            candidates.Add((readMarkIndex, candidate));
        }

        candidates.Sort(static (left, right) => right.Frame.CompareTo(left.Frame));
        return candidates;
    }

    private static long GetReadMarkLockOffset(int readMarkIndex)
    {
        if ((uint)readMarkIndex >= ReadMarkCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(readMarkIndex),
                readMarkIndex,
                $"SQLite WAL read-mark index must be between zero and {ReadMarkCount - 1}.");
        }

        return FirstReadMarkLockOffset + readMarkIndex;
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Read-snapshot timeout must be non-negative or infinite.");
    }

    private static void WaitForRetry(
        TimeSpan timeout,
        Stopwatch? stopwatch,
        CancellationToken cancellationToken)
    {
        if (!SqliteBusyBackoff.Wait(timeout, stopwatch, cancellationToken))
            throw new SqliteWalReadSnapshotBusyException(timeout);
    }

    private void ThrowIfDisposed()
    {
        lock (_gate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SqliteWalReadSnapshotCoordinator));
        }
    }
}

/// <summary>
/// A direct, uncached view of the committed WAL frames protected by one SQLite
/// shared read-mark lease.
/// </summary>
public sealed class SqliteWalReadSnapshot : IDisposable
{
    private readonly SqliteWalReadSnapshotCoordinator _owner;
    private readonly SqliteWalFile _wal;
    private readonly SqliteWalIndexSharedMemory _index;
    private SqliteWalByteRangeLockLease? _readMarkLease;
    private Exception? _fault;

    internal SqliteWalReadSnapshot(
        SqliteWalReadSnapshotCoordinator owner,
        SqliteWalFile wal,
        SqliteWalIndexSharedMemory index,
        SqliteWalByteRangeLockLease readMarkLease,
        int readMarkIndex,
        uint maximumFrame,
        uint databasePageCount)
    {
        _owner = owner;
        _wal = wal;
        _index = index;
        _readMarkLease = readMarkLease;
        ReadMarkIndex = readMarkIndex;
        MaximumFrame = maximumFrame;
        DatabasePageCount = databasePageCount;
    }

    /// <summary>The SQLite WAL read-mark slot held in shared mode.</summary>
    public int ReadMarkIndex { get; }

    /// <summary>The committed WAL frame boundary pinned by this snapshot.</summary>
    public uint MaximumFrame { get; }

    /// <summary>The database page count at <see cref="MaximumFrame"/>.</summary>
    public uint DatabasePageCount { get; }

    /// <summary>Whether the snapshot still owns its shared WAL read-mark lock.</summary>
    public bool IsActive => Volatile.Read(ref _readMarkLease) is { IsActive: true };

    /// <summary>
    /// Reads one frame at or below the pinned boundary without caching its page
    /// image.
    /// </summary>
    public SqliteWalFrame ReadFrame(uint frameNumber)
    {
        if (frameNumber == 0 || frameNumber > MaximumFrame)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameNumber),
                frameNumber,
                $"Snapshot frame numbers must be between one and {MaximumFrame}.");
        }

        try
        {
            ValidatePinnedBoundary();
            return _wal.ReadFrame(frameNumber);
        }
        catch (Exception exception)
        {
            Fault(exception);
            throw;
        }
    }

    /// <summary>
    /// Finds the newest frame for one page at or below the pinned boundary without
    /// consulting a live WAL-index hash table or retaining a page cache.
    /// </summary>
    public SqliteWalFrame? FindFrame(uint pageNumber)
    {
        if (pageNumber == 0)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), "SQLite database page numbers start at one.");

        try
        {
            ValidatePinnedBoundary();
            for (var frameNumber = MaximumFrame; frameNumber > 0; frameNumber--)
            {
                var frame = _wal.ReadFrame(frameNumber);
                if (frame.Header.PageNumber == pageNumber)
                    return frame;
            }

            return null;
        }
        catch (Exception exception)
        {
            Fault(exception);
            throw;
        }
    }

    /// <summary>Ends the read snapshot and releases its exact shared read-mark lock.</summary>
    public void Reset() => EndRead();

    /// <inheritdoc />
    public void Dispose() => EndRead();

    /// <summary>
    /// Re-checks the pinned mark/header for Stage 4 snapshot-invalidated busy.
    /// </summary>
    internal void EnsureStillValid()
    {
        try
        {
            ValidatePinnedBoundary();
        }
        catch (Exception exception)
        {
            Fault(exception);
            throw;
        }
    }

    private void ValidatePinnedBoundary()
    {
        ThrowIfUnavailable();
        var region = _index.ReadValidatedHeader(_wal);
        if (region.Header.MaximumFrame < MaximumFrame)
        {
            throw new InvalidDataException(
                $"SQLite WAL committed boundary moved from at least {MaximumFrame} to {region.Header.MaximumFrame} while the read mark was held.");
        }
        if (ReadMarkIndex != 0
            && region.CheckpointInfo.GetReadMark(ReadMarkIndex) != MaximumFrame)
        {
            throw new SqliteWalReadSnapshotInvalidatedException(
                ReadMarkIndex,
                MaximumFrame,
                region.CheckpointInfo.GetReadMark(ReadMarkIndex));
        }
    }

    private void ThrowIfUnavailable()
    {
        if (_fault is not null)
            throw new InvalidOperationException("The SQLite WAL read snapshot is faulted.", _fault);
        if (!IsActive)
            throw new ObjectDisposedException(nameof(SqliteWalReadSnapshot));
    }

    private void Fault(Exception exception)
    {
        _fault ??= exception;
        EndRead();
    }

    private void EndRead()
    {
        var readMarkLease = Interlocked.Exchange(ref _readMarkLease, null);
        if (readMarkLease is null)
            return;

        try
        {
            readMarkLease.Dispose();
        }
        finally
        {
            _owner.RemoveSnapshot(this);
        }
    }
}
