using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Ahtola.Core.Storage;

/// <summary>SQLite checkpoint modes supported by the detached WAL coordinator.</summary>
public enum SqliteWalCheckpointMode
{
    Passive,
    Full,
    Restart,
    Truncate,
}

/// <summary>A page image to append as one frame of a detached WAL transaction.</summary>
public sealed record SqliteWalWritePage(uint PageNumber, ReadOnlyMemory<byte> PageData);

/// <summary>The durable WAL boundary published by a detached writer.</summary>
public sealed record SqliteWalWriteResult(uint MaximumFrame, uint DatabasePageCount);

/// <summary>The observable outcome of a detached WAL checkpoint attempt.</summary>
public sealed record SqliteWalCheckpointResult(
    SqliteWalCheckpointMode Mode,
    uint MaximumFrame,
    uint SafeFrame,
    uint BackfilledFrameCount,
    uint BackfillAttemptedFrameCount,
    bool IsBusy,
    bool ResetWal);

/// <summary>
/// Coordinates detached writer, recovery, and checkpoint operations over existing
/// SQLite WAL and <c>-shm</c> artifacts.
/// </summary>
/// <remarks>
/// This is a Stage 3 protocol component, not pager behavior. It neither acquires
/// nor relaxes the managed pager's Stage 0 main-file ownership lock, and callers
/// must not use it to claim managed-pager or stock-SQLite interoperability.
/// </remarks>
public sealed class SqliteWalWriterCheckpointCoordinator : IDisposable
{
    private const long WriteLockOffset = SqliteWalIndexCheckpointInfo.LockOffset;
    private const long CheckpointLockOffset = SqliteWalIndexCheckpointInfo.LockOffset + 1;
    private const long RecoveryLockOffset = SqliteWalIndexCheckpointInfo.LockOffset + 2;
    private const long FirstReadMarkLockOffset = SqliteWalIndexCheckpointInfo.LockOffset + 3;

    private readonly object _gate = new();
    private readonly ICheckpointStore _mainStore;
    private readonly SqliteWalFile _wal;
    private readonly SqliteWalIndexSharedMemory _index;
    private readonly SqliteWalByteRangeLock _locks;
    private readonly SqliteWalSharedMemoryCarrierIdentity? _carrierIdentity;
    private readonly ISqliteWalSharedMemoryLockCarrier? _recoveryCarrier;
    private readonly IDisposable? _ownedMapping;
    private readonly bool _ownsArtifacts;
    private bool _disposed;
    private Exception? _fault;

    [field: ThreadStatic]
    internal static Action? BeforeDetachedTailRepairForTesting { get; set; }

    [field: ThreadStatic]
    internal static Action? AfterDetachedWalFrameAppendForTesting { get; set; }

    [field: ThreadStatic]
    internal static Action? AfterDetachedBackfillAttemptPublicationForTesting { get; set; }

    /// <summary>
    /// Invoked on PASSIVE after read marks are released and before install/backfill
    /// publication. Tests use this to inject a concurrent <c>walRestartLog</c>-style wrap.
    /// </summary>
    [field: ThreadStatic]
    internal static Action? AfterDetachedPassiveReadMarksReleasedForTesting { get; set; }

    [field: ThreadStatic]
    internal static Action? AfterDetachedMainStoreBackfillForTesting { get; set; }

    /// <summary>
    /// Creates a coordinator over caller-owned storage, WAL, index, and lock
    /// primitives. All artifacts must describe the same SQLite database.
    /// </summary>
    public SqliteWalWriterCheckpointCoordinator(
        SqlitePageStore mainStore,
        SqliteWalFile wal,
        SqliteWalIndexSharedMemory index,
        SqliteWalByteRangeLock locks)
    {
        ArgumentNullException.ThrowIfNull(mainStore);
        ArgumentNullException.ThrowIfNull(wal);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(locks);
        if (mainStore.IsReadOnly || wal.IsReadOnly)
            throw new ArgumentException("A detached SQLite WAL writer/checkpointer requires writable main-store and WAL artifacts.");
        if (mainStore.PageSize != wal.PageSize)
            throw new InvalidDataException("SQLite main-store and WAL page sizes do not match.");

        _mainStore = new PagerCheckpointStore(mainStore);
        _wal = wal;
        _index = index;
        _locks = locks;
        _carrierIdentity = index.CarrierIdentity;
        _recoveryCarrier = index.LockCarrier;
    }

    private SqliteWalWriterCheckpointCoordinator(
        ICheckpointStore mainStore,
        SqliteWalFile wal,
        SqliteWalIndexSharedMemory index,
        SqliteWalByteRangeLock locks,
        IDisposable ownedMapping)
    {
        ArgumentNullException.ThrowIfNull(mainStore);
        ArgumentNullException.ThrowIfNull(wal);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(locks);
        if (mainStore.IsReadOnly || wal.IsReadOnly)
            throw new ArgumentException("A detached SQLite WAL writer/checkpointer requires writable main-store and WAL artifacts.");
        if (mainStore.PageSize != wal.PageSize)
            throw new InvalidDataException("SQLite main-store and WAL page sizes do not match.");

        _mainStore = mainStore;
        _wal = wal;
        _index = index;
        _locks = locks;
        _carrierIdentity = index.CarrierIdentity;
        _recoveryCarrier = index.LockCarrier;
        _ownedMapping = ownedMapping;
        _ownsArtifacts = true;
    }

    /// <summary>
    /// Opens existing physical SQLite WAL artifacts without creating a new
    /// database, WAL, or shared-memory file.
    /// </summary>
    public static SqliteWalWriterCheckpointCoordinator Open(string databasePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);

        var canonicalPath = Path.GetFullPath(databasePath);
        var walPath = string.Concat(canonicalPath, "-wal");
        var sharedMemoryPath = string.Concat(canonicalPath, "-shm");
        var sharedFileSystem = new SqlitePagerPhysicalFileSystem(PhysicalFileSystem.Instance);
        IFile? mainFile = null;
        ICheckpointStore? mainStore = null;
        SqliteWalFile? wal = null;
        ISqliteWalSharedMemoryMapping? mapping = null;
        try
        {
            mapping = PhysicalFileSystem.Instance.OpenSharedMemoryForRecovery(sharedMemoryPath);
            var index = new SqliteWalIndexSharedMemory(mapping);

            mainFile = sharedFileSystem.OpenFile(canonicalPath, FileOpenMode.OpenExisting);
            var mainPageSize = ReadMainDatabasePageSize(mainFile);
            mainStore = new RawCheckpointStore(mainFile, mainPageSize);
            mainFile = null;
            wal = SqliteWalFile.Open(
                sharedFileSystem,
                walPath,
                truncatedHeader: CreateTruncatedWalHeader(mainPageSize));
            var coordinator = new SqliteWalWriterCheckpointCoordinator(
                mainStore,
                wal,
                index,
                new SqliteWalByteRangeLock(sharedMemoryPath),
                mapping);

            // Checkpoint progress and hash arrays are transient shared-memory
            // state. Reconstruct them before exposing a coordinator so a stale
            // nBackfill value can never authorize a destructive WAL reset.
            coordinator.RebuildIndexFromWal();
            mainStore = null;
            return coordinator;
        }
        catch
        {
            mapping?.Dispose();
            wal?.Dispose();
            mainFile?.Dispose();
            mainStore?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Appends a complete transaction under SQLite's single writer lock and
    /// publishes it only after WAL bytes are flushed to durable storage.
    /// </summary>
    public SqliteWalWriteResult Commit(
        IReadOnlyList<SqliteWalWritePage> pages,
        uint databasePageCount,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ValidateTimeout(timeout);
        ValidateWrite(pages, databasePageCount);
        ValidateWritePageSizes(pages);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ThrowIfUnavailable();
            var writer = AcquireRecoveryLock(
                WriteLockOffset,
                length: 1,
                timeout,
                cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var prior = _index.ReadValidatedHeader(_wal);
                ValidateAppendableWal(prior.Header);
                var appended = new List<SqliteWalFrame>(pages.Count);
                try
                {
                    var firstFrameNumber = 0L;
                    var lastFrameNumber = 0L;
                    for (var index = 0; index < pages.Count; index++)
                    {
                        var page = pages[index];
                        lastFrameNumber = _wal.AppendFrame(
                            page.PageNumber,
                            page.PageData.Span,
                            index == pages.Count - 1 ? databasePageCount : 0);
                        if (index == 0)
                            firstFrameNumber = lastFrameNumber;
                        AfterDetachedWalFrameAppendForTesting?.Invoke();
                    }

                    // Materialize the appended run in one chain walk: ReadFrame
                    // revalidates the whole prefix per call, so reading them back
                    // one at a time is O(P^2).
                    appended.AddRange(_wal.ReadFrameRange(firstFrameNumber, lastFrameNumber));

                    // The header publication is the visibility point. Reaching it
                    // without a durable WAL commit would expose non-recoverable data.
                    _wal.Flush();
                    var committedFrame = appended[^1].Header;
                    var committedHeader = prior.Header.WithCommittedFrames(
                        checked(prior.Header.MaximumFrame + (uint)appended.Count),
                        databasePageCount,
                        committedFrame.Checksum1,
                        committedFrame.Checksum2);
                    _index.PublishCommittedFrames(prior.Header, appended, committedHeader, _wal);
                    return new SqliteWalWriteResult(committedHeader.MaximumFrame, databasePageCount);
                }
                catch (Exception exception)
                {
                    // Recovery takes checkpoint before writer. Release this lease
                    // before it obtains the complete ordered recovery lock set.
                    try
                    {
                        writer.Dispose();
                    }
                    catch (Exception releaseException)
                    {
                        _fault = new AggregateException(
                            "SQLite WAL writing failed and its writer lease could not be released for safe recovery.",
                            exception,
                            releaseException);
                        throw _fault;
                    }

                    FaultAfterWriteFailure(prior.Header.MaximumFrame, exception);
                    throw;
                }
            }
            finally
            {
                writer.Dispose();
            }
        }
    }

    /// <summary>
    /// Repairs only an uncommitted or invalid WAL tail while holding SQLite's
    /// checkpoint, writer, recovery, and every read-mark lock.
    /// </summary>
    public SqliteWalRecoveryInfo Recover(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeout);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ThrowIfUnavailable();
            return RecoverAndRebuild(timeout, cancellationToken);
        }
    }

    /// <summary>
    /// Backfills committed frames without exceeding any reader snapshot. FULL,
    /// RESTART, and TRUNCATE wait for every read mark; PASSIVE returns a partial
    /// result when a held mark prevents complete progress.
    /// </summary>
    public SqliteWalCheckpointResult Checkpoint(
        SqliteWalCheckpointMode mode,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ValidateMode(mode);
        ValidateTimeout(timeout);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ThrowIfUnavailable();
            SqliteWalByteRangeLockLease? passiveLease = null;
            if (mode == SqliteWalCheckpointMode.Passive
                && !_locks.TryAcquireExclusive(CheckpointLockOffset, length: 1, out passiveLease))
            {
                return new SqliteWalCheckpointResult(
                    mode,
                    MaximumFrame: 0,
                    SafeFrame: 0,
                    BackfilledFrameCount: 0,
                    BackfillAttemptedFrameCount: 0,
                    IsBusy: true,
                    ResetWal: false);
            }

            using var checkpoint = mode == SqliteWalCheckpointMode.Passive
                ? passiveLease
                    ?? throw new InvalidOperationException(
                        "SQLite checkpoint locking reported success without returning a lease.")
                : _locks.AcquireExclusive(CheckpointLockOffset, length: 1, timeout);
            IDisposable? writer = null;
            try
            {
                if (mode != SqliteWalCheckpointMode.Passive)
                    writer = _locks.AcquireExclusive(WriteLockOffset, length: 1, timeout);
                return CheckpointUnderLock(mode, timeout, cancellationToken);
            }
            finally
            {
                writer?.Dispose();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        List<Exception>? failures = null;
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
        if (_ownsArtifacts)
        {
            try
            {
                _wal.Dispose();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
            try
            {
                _mainStore.Dispose();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is { Count: > 0 })
        {
            throw new AggregateException(
                "Failed to release one or more detached SQLite WAL writer/checkpoint resources.",
                failures);
        }
    }

    private SqliteWalCheckpointResult CheckpointUnderLock(
        SqliteWalCheckpointMode mode,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = timeout == Timeout.InfiniteTimeSpan ? null : Stopwatch.StartNew();
        ReadMarkLeaseSet readMarks;
        SqliteWalIndexHeaderRegion region;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            region = ReadValidatedCheckpointHeader(mode, timeout, stopwatch, cancellationToken);
            readMarks = TryAcquireReadMarks(region);
            if (mode == SqliteWalCheckpointMode.Passive || readMarks.AllExclusive)
                break;

            readMarks.Dispose();
            WaitForRetry(timeout, stopwatch, cancellationToken);
        }

        using (readMarks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var maximumFrame = region.Header.MaximumFrame;
            var safeFrame = readMarks.SafeFrame;
            var allReadMarksExclusive = readMarks.AllExclusive;
            if (safeFrame < region.CheckpointInfo.BackfilledFrameCount)
            {
                throw new InvalidDataException(
                    "SQLite WAL read marks would move durable checkpoint progress backwards.");
            }

            // SQLite 3.51.3-style salt re-check while we still own the read-mark
            // set (PASSIVE may hold only a subset). Soft-skip if a peer wrapped.
            if (!_index.TryConfirmCheckpointIncarnation(region.Header, _wal, out var confirmedRegion))
                return SoftSkipCheckpoint(mode, confirmedRegion);

            region = confirmedRegion;
            maximumFrame = region.Header.MaximumFrame;
            if (safeFrame > maximumFrame)
                safeFrame = maximumFrame;

            var attemptedFrameCount = region.CheckpointInfo.BackfillAttemptedFrameCount;
            if (safeFrame > attemptedFrameCount)
            {
                try
                {
                    _index.PublishBackfillAttemptedFrameCount(region.Header, safeFrame, _wal);
                    attemptedFrameCount = safeFrame;
                    AfterDetachedBackfillAttemptPublicationForTesting?.Invoke();
                }
                catch (SqliteWalIncarnationChangedException)
                {
                    return SoftSkipCheckpoint(mode, _index.ReadStableHeaderRegion());
                }
            }

            var backfilledFrameCount = region.CheckpointInfo.BackfilledFrameCount;
            if (safeFrame > backfilledFrameCount)
            {
                if (mode == SqliteWalCheckpointMode.Passive)
                {
                    readMarks.Dispose();
                    AfterDetachedPassiveReadMarksReleasedForTesting?.Invoke();
                }

                // Re-check after releasing PASSIVE marks and any test hook that
                // simulates a concurrent walRestartLog.
                if (!_index.TryConfirmCheckpointIncarnation(region.Header, _wal, out var liveBeforeInstall))
                    return SoftSkipCheckpoint(mode, liveBeforeInstall);

                try
                {
                    // A checkpoint may make main-file pages visible only after the
                    // WAL recovery evidence for every copied frame is durable.
                    _wal.Flush();
                    InstallBackfill(safeFrame);
                    AfterDetachedMainStoreBackfillForTesting?.Invoke();
                    _index.PublishBackfilledFrameCount(region.Header, safeFrame, _wal);
                    backfilledFrameCount = safeFrame;
                }
                catch (SqliteWalIncarnationChangedException)
                {
                    return SoftSkipCheckpoint(mode, _index.ReadStableHeaderRegion());
                }
            }

            if (mode is SqliteWalCheckpointMode.Restart or SqliteWalCheckpointMode.Truncate)
            {
                if (!allReadMarksExclusive || backfilledFrameCount != maximumFrame)
                    throw new InvalidOperationException("SQLite WAL restart requires exclusive ownership of every reader mark.");

                cancellationToken.ThrowIfCancellationRequested();
                var confirmation = _index.ReadValidatedHeader(_wal);
                if (confirmation.Header.MaximumFrame != maximumFrame)
                {
                    // A writer could append while FULL waited for read marks. It
                    // cannot do so after this point, so restart only after its
                    // complete committed state is backfilled as well.
                    if (!_index.TryConfirmCheckpointIncarnation(
                            confirmation.Header,
                            _wal,
                            out confirmation))
                    {
                        throw new SqliteWalIncarnationChangedException(
                            "SQLite WAL changed incarnation while confirming a restart checkpoint boundary.");
                    }

                    if (confirmation.CheckpointInfo.BackfillAttemptedFrameCount < confirmation.Header.MaximumFrame)
                    {
                        _index.PublishBackfillAttemptedFrameCount(
                            confirmation.Header,
                            confirmation.Header.MaximumFrame,
                            _wal);
                        attemptedFrameCount = confirmation.Header.MaximumFrame;
                    }
                    _wal.Flush();
                    InstallBackfill(confirmation.Header.MaximumFrame);
                    _index.PublishBackfilledFrameCount(
                        confirmation.Header,
                        confirmation.Header.MaximumFrame,
                        _wal);
                    maximumFrame = confirmation.Header.MaximumFrame;
                    safeFrame = maximumFrame;
                    backfilledFrameCount = maximumFrame;
                }

                if (_mainStore.PageCount != confirmation.Header.DatabasePageCount)
                {
                    throw new InvalidDataException(
                        "SQLite WAL restart refuses to reset before the durable main file reaches the committed page count.");
                }

                _wal.ResetAfterDurableCheckpoint(publishCheckpointedRecoveryMarker: true);
                _index.ResetAfterDurableRestart(
                    confirmation.Header.WithRestartedWal(
                        _mainStore.PageCount,
                        _wal.Header.Salt1,
                        _wal.Header.Salt2));
                if (mode == SqliteWalCheckpointMode.Truncate)
                    _wal.TruncateAfterDurableCheckpoint();
                return new SqliteWalCheckpointResult(
                    mode,
                    maximumFrame,
                    safeFrame,
                    BackfilledFrameCount: 0,
                    BackfillAttemptedFrameCount: 0,
                    IsBusy: false,
                    ResetWal: true);
            }

            return new SqliteWalCheckpointResult(
                mode,
                maximumFrame,
                safeFrame,
                backfilledFrameCount,
                attemptedFrameCount,
                IsBusy: !allReadMarksExclusive && safeFrame < maximumFrame,
                ResetWal: false);
        }
    }

    private static SqliteWalCheckpointResult SoftSkipCheckpoint(
        SqliteWalCheckpointMode mode,
        SqliteWalIndexHeaderRegion liveRegion)
        => new(
            mode,
            liveRegion.Header.MaximumFrame,
            SafeFrame: liveRegion.CheckpointInfo.BackfilledFrameCount,
            liveRegion.CheckpointInfo.BackfilledFrameCount,
            liveRegion.CheckpointInfo.BackfillAttemptedFrameCount,
            IsBusy: false,
            ResetWal: false);

    private void InstallBackfill(uint safeFrame)
    {
        var finalFrame = _wal.ReadFrame(safeFrame);
        if (!finalFrame.Header.IsCommit)
        {
            throw new InvalidDataException(
                $"SQLite WAL safe checkpoint frame {safeFrame} is not a committed transaction boundary.");
        }

        var targetPageCount = finalFrame.Header.DatabaseSizeInPages;
        var latestFrames = new Dictionary<uint, SqliteWalFrame>();
        foreach (var frame in _wal.ReadFrameRange(1, safeFrame))
            latestFrames[frame.Header.PageNumber] = frame;

        var originalPageCount = _mainStore.PageCount;
        if (targetPageCount > originalPageCount)
        {
            for (var pageNumber = checked(originalPageCount + 1); pageNumber <= targetPageCount; pageNumber++)
            {
                if (!latestFrames.TryGetValue(pageNumber, out var page))
                {
                    throw new InvalidDataException(
                        $"SQLite WAL checkpoint is missing newly appended database page {pageNumber}.");
                }

                _mainStore.WritePage(pageNumber, page.PageData);
            }
        }

        foreach (var (pageNumber, frame) in latestFrames
                     .Where(entry => entry.Key <= targetPageCount)
                     .OrderBy(static entry => entry.Key))
        {
            if (pageNumber != 1 && pageNumber <= targetPageCount)
                _mainStore.WritePage(pageNumber, frame.PageData);
        }

        if (targetPageCount < originalPageCount)
        {
            if (!latestFrames.TryGetValue(1, out var pageOne))
            {
                throw new InvalidDataException(
                    "SQLite WAL shrink checkpoint is missing the authoritative first page.");
            }

            _mainStore.WriteShrinkCheckpointPageOne(pageOne.PageData);
            _mainStore.Flush();
            _mainStore.TruncateToPageCount(targetPageCount);
            _mainStore.Flush();
            return;
        }

        if (latestFrames.TryGetValue(1, out var updatedPageOne))
            _mainStore.WritePage(1, updatedPageOne.PageData);
        _mainStore.Flush();
    }

    private ReadMarkLeaseSet TryAcquireReadMarks(SqliteWalIndexHeaderRegion region)
    {
        var leases = new List<SqliteWalByteRangeLockLease>(SqliteWalIndexCheckpointInfo.ReadMarkCount);
        var safeFrame = region.Header.MaximumFrame;
        try
        {
            for (var readMarkIndex = 0; readMarkIndex < SqliteWalIndexCheckpointInfo.ReadMarkCount; readMarkIndex++)
            {
                if (_locks.TryAcquireExclusive(
                        FirstReadMarkLockOffset + readMarkIndex,
                        length: 1,
                        out var lease))
                {
                    leases.Add(lease
                        ?? throw new InvalidOperationException(
                            "SQLite read-mark locking reported success without returning a lease."));
                    continue;
                }

                var readMark = region.CheckpointInfo.GetReadMark(readMarkIndex);
                if (readMarkIndex == 0 || readMark == 0)
                {
                    safeFrame = Math.Min(safeFrame, region.CheckpointInfo.BackfilledFrameCount);
                    continue;
                }
                if (readMark == SqliteWalIndexCheckpointInfo.ReadMarkNotUsed
                    || readMark > region.Header.MaximumFrame)
                {
                    throw new InvalidDataException(
                        $"SQLite WAL read mark {readMarkIndex} is held but does not name a committed snapshot.");
                }

                safeFrame = Math.Min(safeFrame, readMark);
            }

            return new ReadMarkLeaseSet(leases, safeFrame);
        }
        catch
        {
            DisposeLeases(leases);
            throw;
        }
    }

    private ReadMarkLeaseSet AcquireAllReadMarks(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = timeout == Timeout.InfiniteTimeSpan ? null : Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var region = _index.ReadStableHeader();
            var leases = TryAcquireReadMarks(region);
            if (leases.AllExclusive)
                return leases;

            leases.Dispose();
            WaitForRetry(timeout, stopwatch, cancellationToken);
        }
    }

    private ReadMarkLeaseSet AcquireAllReadMarksWithoutIndex(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = timeout == Timeout.InfiniteTimeSpan ? null : Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var leases = new List<SqliteWalByteRangeLockLease>(SqliteWalIndexCheckpointInfo.ReadMarkCount);
            try
            {
                for (var readMarkIndex = 0; readMarkIndex < SqliteWalIndexCheckpointInfo.ReadMarkCount; readMarkIndex++)
                {
                    if (!TryAcquireRecoveryLock(
                            FirstReadMarkLockOffset + readMarkIndex,
                            length: 1,
                            out var lease))
                    {
                        DisposeLeases(leases);
                        leases = null!;
                        break;
                    }

                    leases.Add(lease
                        ?? throw new InvalidOperationException(
                            "SQLite read-mark locking reported success without returning a lease."));
                }

                if (leases is not null)
                    return new ReadMarkLeaseSet(leases, safeFrame: 0);
            }
            catch
            {
                if (leases is not null)
                    DisposeLeases(leases);
                throw;
            }

            WaitForRetry(timeout, stopwatch, cancellationToken);
        }
    }

    private void FaultAfterWriteFailure(uint priorMaximumFrame, Exception exception)
    {
        try
        {
            var recovery = _wal.ScanRecovery();
            if (recovery.LastCommittedFrameNumber == priorMaximumFrame)
            {
                var repaired = RecoverAndRebuild(TimeSpan.Zero, CancellationToken.None);
                if (repaired.LastCommittedFrameNumber == priorMaximumFrame)
                    return;
            }
        }
        catch (Exception recoveryException)
        {
            _fault = new AggregateException(
                "SQLite WAL write failed and its uncommitted tail could not be repaired.",
                exception,
                recoveryException);
            return;
        }

        _fault = exception;
    }

    private void ValidateAppendableWal(SqliteWalIndexHeader publishedHeader)
    {
        var recovery = _wal.ScanRecovery();
        if (recovery.StopReason != SqliteWalRecoveryStopReason.EndOfFile
            || recovery.LastValidFrameNumber != recovery.LastCommittedFrameNumber
            || recovery.LastCommittedFrameNumber != publishedHeader.MaximumFrame
            || (publishedHeader.MaximumFrame != 0
                && recovery.LastCommittedDatabaseSizeInPages != publishedHeader.DatabasePageCount))
        {
            throw new InvalidDataException(
                "Cannot append to a SQLite WAL whose valid frames do not exactly match the published committed boundary; recover it under the recovery lock set first.");
        }
    }

    private SqliteWalIndexHeaderRegion ReadValidatedCheckpointHeader(
        SqliteWalCheckpointMode mode,
        TimeSpan timeout,
        Stopwatch? stopwatch,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                return _index.ReadValidatedHeader(_wal);
            }
            catch (InvalidDataException) when (mode == SqliteWalCheckpointMode.Passive)
            {
                // A writer publishes WAL bytes before the new index header. If it
                // owns the writer byte, wait rather than treating that transient
                // mismatch as corruption or checkpointing an unstable boundary.
                if (!_locks.TryAcquireShared(WriteLockOffset, length: 1, out var writerProbe))
                {
                    WaitForRetry(timeout, stopwatch, cancellationToken);
                    continue;
                }

                using (writerProbe
                       ?? throw new InvalidOperationException(
                           "SQLite WAL writer probing reported success without returning a lease."))
                {
                    // With no exclusive writer, a persistent validation failure is
                    // corrupt state rather than a publication race and must escape.
                    return _index.ReadValidatedHeader(_wal);
                }
            }
        }
    }

    private SqliteWalRecoveryInfo RecoverAndRebuild(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var checkpoint = AcquireRecoveryLock(
            CheckpointLockOffset,
            length: 1,
            timeout,
            cancellationToken);
        using var writer = AcquireRecoveryLock(
            WriteLockOffset,
            length: 1,
            timeout,
            cancellationToken);
        using var recovery = AcquireRecoveryLock(
            RecoveryLockOffset,
            length: 1,
            timeout,
            cancellationToken);
        using var readMarks = AcquireAllReadMarksWithoutIndex(timeout, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRecoveryCarrier(checkpoint, writer, recovery, readMarks);

        SqliteWalIndexHeader? recoveryHeader = null;
        try
        {
            recoveryHeader = _index.ReadRecoverableHeader();
        }
        catch (InvalidDataException)
        {
        }

        var scan = _wal.ScanRecovery();
        if (scan.StopReason != SqliteWalRecoveryStopReason.EndOfFile
            || scan.LastValidFrameNumber != scan.LastCommittedFrameNumber)
        {
            // This validation is the final point at which a path carrier is
            // considered. Windows keeps that carrier unreplaceable for the rest
            // of recovery; platforms without that guarantee reject repair below.
            ValidateRecoveryCarrier(checkpoint, writer, recovery, readMarks);
            BeforeDetachedTailRepairForTesting?.Invoke();

            if (!CanRepairWalTail
                || recoveryHeader is null
                || !HasAuthenticatedTailRecoveryEvidence(recoveryHeader, scan))
            {
                throw new InvalidDataException(
                    "SQLite WAL corruption reaches before the last independently published committed boundary.");
            }

            scan = _wal.RecoverToLastCommittedFrame();
        }

        var repaired = scan;
        ValidateRecoveryCarrier(checkpoint, writer, recovery, readMarks);
        _index.RebuildFromWal(_wal, _mainStore.PageCount);
        return repaired;
    }

    private void RebuildIndexFromWal()
        => _ = RecoverAndRebuild(TimeSpan.Zero, CancellationToken.None);

    private bool CanRepairWalTail
        => _recoveryCarrier?.PreventsCarrierReplacement == true;

    private SqliteWalByteRangeLockLease AcquireRecoveryLock(
        long offset,
        long length,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => _recoveryCarrier is null
            ? _locks.AcquireExclusive(offset, length, timeout, cancellationToken)
            : _locks.AcquireExclusive(_recoveryCarrier, offset, length, timeout, cancellationToken);

    private bool TryAcquireRecoveryLock(
        long offset,
        long length,
        out SqliteWalByteRangeLockLease? lease)
        => _recoveryCarrier is null
            ? _locks.TryAcquireExclusive(offset, length, out lease)
            : _locks.TryAcquireExclusive(_recoveryCarrier, offset, length, out lease);

    private bool HasAuthenticatedTailRecoveryEvidence(
        SqliteWalIndexHeader recoveryHeader,
        SqliteWalRecoveryInfo scan)
    {
        var walHeader = _wal.Header;
        if (recoveryHeader.PageSize != walHeader.PageSize
            || recoveryHeader.WalChecksumByteOrder != walHeader.ChecksumByteOrder
            || recoveryHeader.Salt1 != walHeader.Salt1
            || recoveryHeader.Salt2 != walHeader.Salt2
            || recoveryHeader.MaximumFrame != scan.LastCommittedFrameNumber)
        {
            return false;
        }

        if (recoveryHeader.MaximumFrame == 0)
            return true;

        if (scan.LastCommittedDatabaseSizeInPages != recoveryHeader.DatabasePageCount)
            return false;

        var committedFrame = _wal.ReadFrame(recoveryHeader.MaximumFrame).Header;
        return committedFrame.IsCommit
            && committedFrame.DatabaseSizeInPages == recoveryHeader.DatabasePageCount
            && committedFrame.Checksum1 == recoveryHeader.FrameChecksum1
            && committedFrame.Checksum2 == recoveryHeader.FrameChecksum2;
    }

    private void ValidateRecoveryCarrier(
        SqliteWalByteRangeLockLease checkpoint,
        SqliteWalByteRangeLockLease writer,
        SqliteWalByteRangeLockLease recovery,
        ReadMarkLeaseSet readMarks)
    {
        if (_carrierIdentity is not { } carrierIdentity)
            return;

        if (checkpoint.CarrierIdentity != carrierIdentity
            || writer.CarrierIdentity != carrierIdentity
            || recovery.CarrierIdentity != carrierIdentity
            || SqliteWalSharedMemoryCarrierIdentity.FromPath(_locks.LockFilePath) != carrierIdentity)
        {
            throw new InvalidDataException(
                "SQLite WAL shared-memory carrier changed between mapping and recovery locking.");
        }

        readMarks.ValidateCarrierIdentity(carrierIdentity);
    }

    private static int ReadMainDatabasePageSize(IFile mainFile)
    {
        Span<byte> encodedPageSize = stackalloc byte[sizeof(ushort)];
        if (mainFile.Read(position: 16, encodedPageSize) != encodedPageSize.Length)
            throw new InvalidDataException("SQLite main database is too small to contain its page-size field.");
        return SqlitePageSize.Decode(BinaryPrimitives.ReadUInt16BigEndian(encodedPageSize));
    }

    private static SqliteWalHeader CreateTruncatedWalHeader(int pageSize)
    {
        Span<byte> salts = stackalloc byte[sizeof(uint) * 2];
        RandomNumberGenerator.Fill(salts);
        return SqliteWalHeader.Create(
            pageSize,
            BinaryPrimitives.ReadUInt32BigEndian(salts),
            BinaryPrimitives.ReadUInt32BigEndian(salts[sizeof(uint)..]));
    }

    private void ThrowIfUnavailable()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SqliteWalWriterCheckpointCoordinator));
        if (_fault is not null)
        {
            throw new InvalidOperationException(
                "The detached SQLite WAL writer/checkpoint coordinator is faulted and must not reuse its artifacts.",
                _fault);
        }
    }

    private static void ValidateWrite(IReadOnlyList<SqliteWalWritePage> pages, uint databasePageCount)    {
        if (pages.Count == 0)
            throw new ArgumentException("A SQLite WAL transaction requires at least one page.", nameof(pages));
        if (databasePageCount == 0)
            throw new ArgumentOutOfRangeException(
                nameof(databasePageCount),
                "A SQLite WAL transaction must declare a nonzero database page count.");

        var seen = new HashSet<uint>();
        foreach (var page in pages)
        {
            if (page is null)
                throw new ArgumentException("SQLite WAL transactions cannot contain null page entries.", nameof(pages));
            if (page.PageNumber == 0 || page.PageNumber > databasePageCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pages),
                    "SQLite WAL transaction pages must be within the committed database page count.");
            }
            if (!seen.Add(page.PageNumber))
            {
                throw new ArgumentException(
                    $"SQLite WAL transaction contains page {page.PageNumber} more than once.",
                    nameof(pages));
            }
        }
    }

    private void ValidateWritePageSizes(IReadOnlyList<SqliteWalWritePage> pages)
    {
        foreach (var page in pages)
        {
            if (page.PageData.Length != _wal.PageSize)
            {
                throw new ArgumentException(
                    $"SQLite WAL page {page.PageNumber} is {page.PageData.Length} bytes; expected {_wal.PageSize}.",
                    nameof(pages));
            }
        }
    }

    private static void ValidateMode(SqliteWalCheckpointMode mode)
    {
        if (mode is not SqliteWalCheckpointMode.Passive
            and not SqliteWalCheckpointMode.Full
            and not SqliteWalCheckpointMode.Restart
            and not SqliteWalCheckpointMode.Truncate)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown SQLite WAL checkpoint mode.");
        }
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), "SQLite WAL lock timeout must be non-negative or infinite.");
    }

    private static TimeSpan RemainingTimeout(TimeSpan timeout, Stopwatch? stopwatch)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
            return Timeout.InfiniteTimeSpan;
        if (timeout == TimeSpan.Zero)
            return TimeSpan.Zero;

        var remaining = timeout - stopwatch!.Elapsed;
        if (remaining <= TimeSpan.Zero)
            throw new SqliteWalByteRangeLockBusyException(
                lockFilePath: "SQLite WAL coordinator",
                offset: WriteLockOffset,
                length: 1,
                mode: SqliteWalByteRangeLockMode.Exclusive,
                timeout: timeout,
                innerException: null);
        return remaining;
    }

    private static void WaitForRetry(
        TimeSpan timeout,
        Stopwatch? stopwatch,
        CancellationToken cancellationToken)
    {
        if (!SqliteBusyBackoff.Wait(timeout, stopwatch, cancellationToken))
        {
            throw new SqliteWalByteRangeLockBusyException(
                lockFilePath: "SQLite WAL coordinator",
                offset: FirstReadMarkLockOffset,
                length: SqliteWalIndexCheckpointInfo.ReadMarkCount,
                mode: SqliteWalByteRangeLockMode.Exclusive,
                timeout: timeout,
                innerException: null);
        }
    }

    private static void DisposeLeases(List<SqliteWalByteRangeLockLease> leases)
    {
        Exception? failure = null;
        for (var index = leases.Count - 1; index >= 0; index--)
        {
            try
            {
                leases[index].Dispose();
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        if (failure is not null)
            throw new InvalidOperationException("Failed to release SQLite WAL read-mark locks.", failure);
    }

    private sealed class ReadMarkLeaseSet : IDisposable
    {
        private List<SqliteWalByteRangeLockLease>? _leases;

        internal ReadMarkLeaseSet(List<SqliteWalByteRangeLockLease> leases, uint safeFrame)
        {
            _leases = leases;
            SafeFrame = safeFrame;
        }

        internal uint SafeFrame { get; }

        internal bool AllExclusive => _leases?.Count == SqliteWalIndexCheckpointInfo.ReadMarkCount;

        internal void ValidateCarrierIdentity(SqliteWalSharedMemoryCarrierIdentity carrierIdentity)
        {
            var leases = _leases
                ?? throw new ObjectDisposedException(nameof(ReadMarkLeaseSet));
            if (leases.Any(lease => lease.CarrierIdentity != carrierIdentity))
            {
                throw new InvalidDataException(
                    "SQLite WAL read-mark leases do not share the mapped shared-memory carrier.");
            }
        }

        public void Dispose()
        {
            var leases = Interlocked.Exchange(ref _leases, null);
            if (leases is not null)
                DisposeLeases(leases);
        }
    }

    private interface ICheckpointStore : IDisposable
    {
        int PageSize { get; }

        uint PageCount { get; }

        bool IsReadOnly { get; }

        void WritePage(uint pageNumber, ReadOnlySpan<byte> source);

        void WriteShrinkCheckpointPageOne(ReadOnlySpan<byte> source);

        void TruncateToPageCount(uint pageCount);

        void Flush();
    }

    private sealed class PagerCheckpointStore(SqlitePageStore store) : ICheckpointStore
    {
        public int PageSize => store.PageSize;

        public uint PageCount => store.PageCount;

        public bool IsReadOnly => store.IsReadOnly;

        public void WritePage(uint pageNumber, ReadOnlySpan<byte> source) => store.WritePage(pageNumber, source);

        public void WriteShrinkCheckpointPageOne(ReadOnlySpan<byte> source)
            => store.WriteShrinkCheckpointPageOne(source);

        public void TruncateToPageCount(uint pageCount) => store.TruncateToPageCount(pageCount);

        public void Flush() => store.Flush();

        // The caller owns a SqlitePageStore supplied to the public constructor.
        public void Dispose()
        {
        }
    }

    private sealed class RawCheckpointStore : ICheckpointStore
    {
        private readonly IFile _file;
        private bool _disposed;

        internal RawCheckpointStore(IFile file, int pageSize)
        {
            ArgumentNullException.ThrowIfNull(file);
            if (pageSize < SqlitePageSize.Minimum || (pageSize & (pageSize - 1)) != 0)
                throw new ArgumentOutOfRangeException(nameof(pageSize), "SQLite WAL page size must be a valid power of two.");
            if (file.Length < pageSize || file.Length % pageSize != 0)
                throw new InvalidDataException("SQLite main database is not a whole number of WAL-sized pages.");

            _file = file;
            PageSize = pageSize;
        }

        public int PageSize { get; }

        public uint PageCount
        {
            get
            {
                ThrowIfDisposed();
                return checked((uint)(_file.Length / PageSize));
            }
        }

        public bool IsReadOnly => _file.IsReadOnly;

        public void WritePage(uint pageNumber, ReadOnlySpan<byte> source)
        {
            ThrowIfDisposed();
            if (source.Length != PageSize)
                throw new ArgumentException($"SQLite checkpoint page must be exactly {PageSize} bytes.", nameof(source));
            if (pageNumber == 0 || pageNumber > PageCount + 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pageNumber),
                    "SQLite checkpoint page writes must replace an existing page or append exactly one page.");
            }

            _file.Write(checked((long)(pageNumber - 1) * PageSize), source);
            if (_file.Length % PageSize != 0)
                throw new InvalidDataException("SQLite checkpoint write left the main database misaligned.");
        }

        public void WriteShrinkCheckpointPageOne(ReadOnlySpan<byte> source)
        {
            if (PageCount == 0)
                throw new InvalidDataException("SQLite checkpoint cannot install page one into an empty main database.");
            WritePage(pageNumber: 1, source);
        }

        public void TruncateToPageCount(uint pageCount)
        {
            ThrowIfDisposed();
            if (pageCount == 0 || pageCount > PageCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pageCount),
                    "SQLite checkpoint truncation must retain between one and the current number of pages.");
            }

            _file.SetLength(checked((long)pageCount * PageSize));
            if (PageCount != pageCount)
                throw new InvalidDataException("SQLite checkpoint truncation did not reach its requested page boundary.");
        }

        public void Flush()
        {
            ThrowIfDisposed();
            _file.FlushToDisk();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _file.Dispose();
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
