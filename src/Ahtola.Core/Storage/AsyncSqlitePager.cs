using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Ahtola.Core.Storage;

/// <summary>
/// A true-asynchronous SQLite pager over <see cref="IAsyncFileSystem"/>.
/// </summary>
/// <remarks>
/// The pager uses process-local reader, writer, and checkpoint coordination and
/// authenticates committed visibility by scanning the WAL. Physical SQLite
/// <c>-shm</c> interoperability remains the responsibility of
/// <see cref="SqlitePager"/>.
/// </remarks>
public sealed class AsyncSqlitePager : IAsyncDisposable
{
    private static readonly ConditionalWeakTable<IAsyncFileSystem, AsyncLockScope> LockScopes = new();

    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly IAsyncFileSystem _fileSystem;
    private readonly string _databasePath;
    private readonly string _walPath;
    private readonly string _journalPath;
    private readonly AsyncSqlitePageStore _pageStore;
    private readonly SqlitePagerLockManager _lockManager;
    private readonly AhtolaEncryptionOptions? _encryption;
    private readonly IPageCodec? _pageCodec;
    private readonly Dictionary<uint, byte[]> _walPageOverlay = [];
    private readonly HashSet<AsyncSqlitePagerReadTransaction> _activeReadTransactions = [];
    private SqliteWalFile? _wal;
    private AsyncSqlitePagerTransaction? _activeTransaction;
    private SqliteWalRecoveryInfo _recoveryInfo = CreateEmptyRecoveryInfo();
    private SqliteWalRecoveryInfo _visibleRecoveryInfo = CreateEmptyRecoveryInfo();
    private uint _committedPageCount;
    private long _committedFrameCount;
    private long _lockGeneration;
    private SqlitePagerState _state;
    private TimeSpan _busyTimeout;

    private AsyncSqlitePager(
        IAsyncFileSystem fileSystem,
        string databasePath,
        string walPath,
        AsyncSqlitePageStore pageStore,
        SqliteWalFile? wal,
        SqliteJournalMode journalMode,
        SqlitePagerLockManager lockManager,
        AhtolaEncryptionOptions? encryption,
        IPageCodec? pageCodec)
    {
        _fileSystem = fileSystem;
        _databasePath = databasePath;
        _walPath = walPath;
        _journalPath = databasePath + "-journal";
        _pageStore = pageStore;
        _wal = wal;
        JournalMode = journalMode;
        _lockManager = lockManager;
        _encryption = encryption;
        _pageCodec = pageCodec;
    }

    /// <summary>The fixed SQLite page size shared by the main store and WAL.</summary>
    public int PageSize
    {
        get
        {
            lock (_stateGate)
            {
                ThrowIfDisposed();
                return _pageStore.PageSize;
            }
        }
    }

    /// <summary>The durable transaction format used by this pager.</summary>
    public SqliteJournalMode JournalMode { get; }

    /// <summary>The database size represented by the committed view.</summary>
    public uint CommittedPageCount
    {
        get
        {
            lock (_stateGate)
            {
                ThrowIfDisposed();
                return _committedPageCount;
            }
        }
    }

    /// <summary>The final WAL frame in the committed view, or zero in DELETE mode.</summary>
    public long CommittedFrameCount
    {
        get
        {
            lock (_stateGate)
            {
                ThrowIfDisposed();
                return _committedFrameCount;
            }
        }
    }

    /// <summary>The recovery state that established the currently visible view.</summary>
    public SqliteWalRecoveryInfo RecoveryInfo
    {
        get
        {
            lock (_stateGate)
            {
                ThrowIfDisposed();
                return _visibleRecoveryInfo;
            }
        }
    }

    /// <summary>Whether the database or WAL handle is read-only.</summary>
    public bool IsReadOnly
    {
        get
        {
            lock (_stateGate)
            {
                ThrowIfDisposed();
                return _pageStore.IsReadOnly || (_wal?.IsReadOnly ?? false);
            }
        }
    }

    /// <summary>The pager lifecycle state.</summary>
    public SqlitePagerState State
    {
        get
        {
            lock (_stateGate)
                return _state;
        }
    }

    /// <summary>The process-local lock manager used by this pager.</summary>
    public SqlitePagerLockManager LockManager => _lockManager;

    /// <summary>The default wait for reader, writer, and checkpoint ownership.</summary>
    public TimeSpan BusyTimeout
    {
        get
        {
            lock (_stateGate)
                return _busyTimeout;
        }
        set
        {
            ValidateBusyTimeout(value, nameof(value));
            lock (_stateGate)
                _busyTimeout = value;
        }
    }

    /// <summary>Creates a fresh WAL-mode database and matching empty WAL.</summary>
    public static async ValueTask<AsyncSqlitePager> CreateAsync(
        IAsyncFileSystem fileSystem,
        string databasePath,
        string walPath,
        SqliteWalHeader walHeader,
        SqliteDatabaseHeader? databaseHeader = null,
        SqlitePagerLockManager? lockManager = null,
        TimeSpan? busyTimeout = null,
        AhtolaEncryptionOptions? encryption = null,
        IPageCodec? pageCodec = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        ArgumentException.ThrowIfNullOrEmpty(walPath);
        ArgumentNullException.ThrowIfNull(walHeader);
        ValidateBusyTimeout(busyTimeout, nameof(busyTimeout));
        PageCodecSupport.RejectCombinedTransforms(encryption, pageCodec);

        var header = databaseHeader ?? SqliteDatabaseHeader.CreateDefault();
        if (header.PageSize != walHeader.PageSize)
            throw new InvalidOperationException("SQLite database and WAL page sizes must match.");
        if (!IsWalCompatibleFormat(header.WriteVersion)
            || !IsWalCompatibleFormat(header.ReadVersion))
        {
            throw new InvalidOperationException(
                "A SQLite WAL overlay requires WAL/MVCC read and write format versions.");
        }

        var timeout = busyTimeout ?? TimeSpan.Zero;
        var manager = lockManager ?? GetLockManager(fileSystem, databasePath, walPath);
        await using var openingLock = await AsyncLockLease.AcquireCheckpointAsync(
            manager,
            timeout,
            cancellationToken).ConfigureAwait(false);

        AsyncSqlitePageStore? pageStore = null;
        SqliteWalFile? wal = null;
        var databaseCreated = false;
        var walCreated = false;
        try
        {
            pageStore = await AsyncSqlitePageStore.CreateAsync(
                fileSystem,
                databasePath,
                header,
                encryption: encryption,
                pageCodec: pageCodec,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            databaseCreated = true;
            wal = await SqliteWalFile.CreateAsync(
                fileSystem,
                walPath,
                walHeader,
                encryption,
                pageCodec,
                cancellationToken).ConfigureAwait(false);
            walCreated = true;

            var pager = new AsyncSqlitePager(
                fileSystem,
                databasePath,
                walPath,
                pageStore,
                wal,
                GetJournalMode(pageStore.Header),
                manager,
                encryption,
                pageCodec);
            await pager.InitializeCommittedViewAsync(
                await wal.ScanRecoveryAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
            pager._lockGeneration = openingLock.Lease.PublishStorageChange();
            pager._busyTimeout = timeout;
            pager._state = SqlitePagerState.Ready;
            return pager;
        }
        catch
        {
            if (wal is not null)
                await DisposeIgnoringFailureAsync(wal).ConfigureAwait(false);
            if (pageStore is not null)
                await DisposeIgnoringFailureAsync(pageStore).ConfigureAwait(false);
            if (walCreated)
                await DeleteIgnoringFailureAsync(fileSystem, walPath).ConfigureAwait(false);
            if (databaseCreated)
                await DeleteIgnoringFailureAsync(fileSystem, databasePath).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Creates a fresh DELETE-mode database without a WAL.</summary>
    public static async ValueTask<AsyncSqlitePager> CreateRollbackJournalAsync(
        IAsyncFileSystem fileSystem,
        string databasePath,
        string walPath,
        SqliteDatabaseHeader? databaseHeader = null,
        SqlitePagerLockManager? lockManager = null,
        TimeSpan? busyTimeout = null,
        AhtolaEncryptionOptions? encryption = null,
        IPageCodec? pageCodec = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        ArgumentException.ThrowIfNullOrEmpty(walPath);
        ValidateBusyTimeout(busyTimeout, nameof(busyTimeout));
        PageCodecSupport.RejectCombinedTransforms(encryption, pageCodec);

        var header = (databaseHeader ?? SqliteDatabaseHeader.CreateDefault()) with
        {
            ReadVersion = SqliteFileFormatVersion.Legacy,
            WriteVersion = SqliteFileFormatVersion.Legacy,
        };
        var timeout = busyTimeout ?? TimeSpan.Zero;
        var manager = lockManager ?? GetLockManager(fileSystem, databasePath, walPath);
        await using var openingLock = await AsyncLockLease.AcquireCheckpointAsync(
            manager,
            timeout,
            cancellationToken).ConfigureAwait(false);

        AsyncSqlitePageStore? pageStore = null;
        var databaseCreated = false;
        try
        {
            pageStore = await AsyncSqlitePageStore.CreateAsync(
                fileSystem,
                databasePath,
                header,
                encryption: encryption,
                pageCodec: pageCodec,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            databaseCreated = true;
            if (await fileSystem.FileExistsAsync(walPath, cancellationToken).ConfigureAwait(false))
                await fileSystem.DeleteFileAsync(walPath, cancellationToken).ConfigureAwait(false);

            var pager = new AsyncSqlitePager(
                fileSystem,
                databasePath,
                walPath,
                pageStore,
                wal: null,
                SqliteJournalMode.Delete,
                manager,
                encryption,
                pageCodec);
            await pager.InitializeRollbackViewAsync(cancellationToken).ConfigureAwait(false);
            pager._lockGeneration = openingLock.Lease.PublishStorageChange();
            pager._busyTimeout = timeout;
            pager._state = SqlitePagerState.Ready;
            return pager;
        }
        catch
        {
            if (pageStore is not null)
                await DisposeIgnoringFailureAsync(pageStore).ConfigureAwait(false);
            if (databaseCreated)
                await DeleteIgnoringFailureAsync(fileSystem, databasePath).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Opens an existing SQLite database and establishes its committed view.</summary>
    public static async ValueTask<AsyncSqlitePager> OpenAsync(
        IAsyncFileSystem fileSystem,
        string databasePath,
        string walPath,
        bool readOnly = false,
        SqlitePagerLockManager? lockManager = null,
        TimeSpan? busyTimeout = null,
        AhtolaEncryptionOptions? encryption = null,
        IPageCodec? pageCodec = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        ArgumentException.ThrowIfNullOrEmpty(walPath);
        ValidateBusyTimeout(busyTimeout, nameof(busyTimeout));
        PageCodecSupport.RejectCombinedTransforms(encryption, pageCodec);

        var timeout = busyTimeout ?? TimeSpan.Zero;
        var manager = lockManager ?? GetLockManager(fileSystem, databasePath, walPath);
        await using var openingLock = readOnly
            ? await AsyncLockLease.AcquireReaderAsync(manager, timeout, cancellationToken).ConfigureAwait(false)
            : await AsyncLockLease.AcquireCheckpointAsync(manager, timeout, cancellationToken).ConfigureAwait(false);

        AsyncSqlitePageStore? pageStore = null;
        SqliteWalFile? wal = null;
        try
        {
            await SqliteRollbackJournal.RecoverIfPresentAsync(
                fileSystem,
                databasePath,
                databasePath + "-journal",
                readOnly,
                cancellationToken).ConfigureAwait(false);
            pageStore = await AsyncSqlitePageStore.OpenForPagerAsync(
                fileSystem,
                databasePath,
                readOnly,
                encryption,
                pageCodec,
                cancellationToken).ConfigureAwait(false);
            var journalMode = GetJournalMode(pageStore.Header);

            if (UsesWalStorage(journalMode))
            {
                if (await fileSystem.FileExistsAsync(walPath, cancellationToken).ConfigureAwait(false))
                {
                    wal = await SqliteWalFile.OpenAsync(
                        fileSystem,
                        walPath,
                        readOnly,
                        encryption,
                        CreateTruncatedWalHeader(pageStore.PageSize),
                        pageCodec,
                        cancellationToken).ConfigureAwait(false);
                }
                else if (!readOnly)
                {
                    wal = await SqliteWalFile.CreateAsync(
                        fileSystem,
                        walPath,
                        CreateTruncatedWalHeader(pageStore.PageSize),
                        encryption,
                        pageCodec,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            else if (!readOnly
                     && await fileSystem.FileExistsAsync(walPath, cancellationToken).ConfigureAwait(false))
            {
                await fileSystem.DeleteFileAsync(walPath, cancellationToken).ConfigureAwait(false);
            }

            var pager = new AsyncSqlitePager(
                fileSystem,
                databasePath,
                walPath,
                pageStore,
                wal,
                journalMode,
                manager,
                encryption,
                pageCodec);
            if (UsesWalStorage(journalMode) && wal is not null)
            {
                var recovery = await wal.ScanRecoveryAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await pager.InitializeCommittedViewAsync(recovery, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException exception) when (readOnly)
                {
                    throw new InvalidDataException(
                        "Cannot safely open the SQLite database read-only because its WAL cannot establish a non-mutating committed snapshot. "
                        + "Open it writable to recover the WAL.",
                        exception);
                }

                if (!readOnly && HasUncommittedOrInvalidTail(recovery))
                {
                    var repairedFrom = await wal
                        .RecoverToLastCommittedFrameAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (repairedFrom != recovery)
                        throw new InvalidDataException("SQLite WAL changed between recovery scanning and tail repair.");

                    var repaired = await wal.ScanRecoveryAsync(cancellationToken).ConfigureAwait(false);
                    await pager.InitializeCommittedViewAsync(repaired, cancellationToken).ConfigureAwait(false);
                    lock (pager._stateGate)
                        pager._visibleRecoveryInfo = recovery;
                }
            }
            else if (journalMode == SqliteJournalMode.Delete)
            {
                await pager.InitializeRollbackViewAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await pager.InitializeCleanWalViewAsync(cancellationToken).ConfigureAwait(false);
            }

            pager._lockGeneration = readOnly
                ? manager.Generation
                : openingLock.Lease.PublishStorageChange();
            pager._busyTimeout = timeout;
            pager._state = SqlitePagerState.Ready;
            return pager;
        }
        catch
        {
            if (wal is not null)
                await DisposeIgnoringFailureAsync(wal).ConfigureAwait(false);
            if (pageStore is not null)
                await DisposeIgnoringFailureAsync(pageStore).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Reads a committed page using a short-lived stable snapshot.</summary>
    public async ValueTask<byte[]> ReadPageAsync(
        uint pageNumber,
        CancellationToken cancellationToken = default)
    {
        await using var read = await BeginReadAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return await read.ReadPageAsync(pageNumber, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a committed page into an exact page-sized destination.</summary>
    public async ValueTask ReadPageAsync(
        uint pageNumber,
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        if (destination.Length != PageSize)
            throw new ArgumentException($"Destination must be exactly {PageSize} bytes.", nameof(destination));
        var page = await ReadPageAsync(pageNumber, cancellationToken).ConfigureAwait(false);
        page.CopyTo(destination);
    }

    /// <summary>Begins a stable committed read snapshot.</summary>
    public async ValueTask<AsyncSqlitePagerReadTransaction> BeginReadAsync(
        TimeSpan? busyTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var timeout = ResolveBusyTimeout(busyTimeout);
        var readerLock = await _lockManager.EnterReaderAsync(timeout, cancellationToken)
            .ConfigureAwait(false);
        var handedOff = false;
        try
        {
            await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfNotReadable();
                await SynchronizeCommittedViewAsync(cancellationToken).ConfigureAwait(false);
                var transaction = new AsyncSqlitePagerReadTransaction(
                    this,
                    readerLock,
                    _committedPageCount,
                    CloneOverlay(_walPageOverlay));
                lock (_stateGate)
                    _activeReadTransactions.Add(transaction);
                handedOff = true;
                return transaction;
            }
            finally
            {
                _ioGate.Release();
            }
        }
        finally
        {
            if (!handedOff)
                readerLock.Dispose();
        }
    }

    /// <summary>Alias matching the synchronous pager transaction name.</summary>
    public ValueTask<AsyncSqlitePagerReadTransaction> BeginReadTransactionAsync(
        TimeSpan? busyTimeout = null,
        CancellationToken cancellationToken = default)
        => BeginReadAsync(busyTimeout, cancellationToken);

    /// <summary>Begins one single-writer page transaction.</summary>
    public async ValueTask<AsyncSqlitePagerTransaction> BeginTransactionAsync(
        uint targetDatabaseSizeInPages,
        TimeSpan? busyTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(targetDatabaseSizeInPages);
        var timeout = ResolveBusyTimeout(busyTimeout);
        var writerLock = await _lockManager.EnterWriterAsync(timeout, cancellationToken)
            .ConfigureAwait(false);
        var handedOff = false;
        try
        {
            await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                ThrowIfReadOnly();
                await SynchronizeCommittedViewAsync(cancellationToken).ConfigureAwait(false);
                if (UsesWalStorage(JournalMode) && HasUncommittedOrInvalidTail(_recoveryInfo))
                    await RecoverWalTailUnderWriterLockAsync(cancellationToken).ConfigureAwait(false);
                if (_state != SqlitePagerState.Ready)
                {
                    throw new InvalidOperationException(
                        $"Cannot begin a SQLite pager transaction while the pager is {_state}.");
                }

                var transaction = new AsyncSqlitePagerTransaction(
                    this,
                    writerLock,
                    targetDatabaseSizeInPages,
                    _committedPageCount,
                    CloneOverlay(_walPageOverlay));
                lock (_stateGate)
                {
                    _activeTransaction = transaction;
                    _state = SqlitePagerState.TransactionActive;
                }
                handedOff = true;
                return transaction;
            }
            finally
            {
                _ioGate.Release();
            }
        }
        finally
        {
            if (!handedOff)
                writerLock.Dispose();
        }
    }

    /// <summary>Stages a page in the active transaction.</summary>
    public ValueTask WritePageAsync(
        uint pageNumber,
        ReadOnlyMemory<byte> page,
        CancellationToken cancellationToken = default)
    {
        AsyncSqlitePagerTransaction transaction;
        lock (_stateGate)
        {
            ThrowIfDisposed();
            transaction = _activeTransaction
                ?? throw new InvalidOperationException("The SQLite pager has no active write transaction.");
        }

        return transaction.WritePageAsync(pageNumber, page, cancellationToken);
    }

    /// <summary>Commits the active transaction.</summary>
    public ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        AsyncSqlitePagerTransaction transaction;
        lock (_stateGate)
        {
            ThrowIfDisposed();
            transaction = _activeTransaction
                ?? throw new InvalidOperationException("The SQLite pager has no active write transaction.");
        }

        return transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Rolls back the active transaction without writing storage.</summary>
    public ValueTask RollbackAsync(CancellationToken cancellationToken = default)
    {
        AsyncSqlitePagerTransaction transaction;
        lock (_stateGate)
        {
            ThrowIfDisposed();
            transaction = _activeTransaction
                ?? throw new InvalidOperationException("The SQLite pager has no active write transaction.");
        }

        return transaction.RollbackAsync(cancellationToken);
    }

    /// <summary>Installs committed WAL pages into the main database and retains the WAL.</summary>
    public ValueTask<SqliteCheckpointResult> CheckpointToMainStoreAsync(
        TimeSpan? busyTimeout = null,
        CancellationToken cancellationToken = default)
        => CheckpointAsync(resetCommittedWal: false, busyTimeout, cancellationToken);

    /// <summary>Installs committed WAL pages, then durably resets the WAL.</summary>
    public ValueTask<SqliteCheckpointResult> CheckpointToMainStoreAndResetWalAsync(
        TimeSpan? busyTimeout = null,
        CancellationToken cancellationToken = default)
        => CheckpointAsync(resetCommittedWal: true, busyTimeout, cancellationToken);

    /// <summary>Repairs an uncommitted, partial, or invalid WAL tail.</summary>
    public async ValueTask RecoverUncommittedWalTailAsync(
        TimeSpan? busyTimeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!UsesWalStorage(JournalMode))
            throw new InvalidOperationException("Rollback-journal mode does not have a WAL tail to recover.");

        var timeout = ResolveBusyTimeout(busyTimeout);
        using var writerLock = await _lockManager.EnterWriterAsync(timeout, cancellationToken)
            .ConfigureAwait(false);
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ThrowIfReadOnly();
            if (_state != SqlitePagerState.Ready)
                throw new InvalidOperationException($"Cannot recover a SQLite WAL while the pager is {_state}.");
            try
            {
                await SynchronizeCommittedViewAsync(cancellationToken).ConfigureAwait(false);
                if (HasUncommittedOrInvalidTail(_recoveryInfo))
                    await RecoverWalTailUnderWriterLockAsync(cancellationToken).ConfigureAwait(false);
                _lockGeneration = writerLock.PublishStorageChange();
            }
            catch
            {
                TransitionToFaulted();
                throw;
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _ioGate.WaitAsync().ConfigureAwait(false);
        AsyncSqlitePagerTransaction? transaction;
        AsyncSqlitePagerReadTransaction[] readers;
        try
        {
            lock (_stateGate)
            {
                if (_state == SqlitePagerState.Disposed)
                    return;
                transaction = _activeTransaction;
                readers = [.. _activeReadTransactions];
                _activeTransaction = null;
                _activeReadTransactions.Clear();
                _state = SqlitePagerState.Disposed;
            }

            transaction?.AbortFromPagerDispose();
            foreach (var reader in readers)
                reader.InvalidateFromPagerDispose();

            try
            {
                if (_wal is not null)
                    await _wal.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await _pageStore.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    internal async ValueTask<byte[]> ReadSnapshotPageAsync(
        IReadOnlyDictionary<uint, byte[]> overlay,
        uint pageCount,
        uint pageNumber,
        CancellationToken cancellationToken)
    {
        if (pageNumber == 0 || pageNumber > pageCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageNumber),
                pageNumber,
                $"Page number is out of range for snapshot database size {pageCount}.");
        }
        if (overlay.TryGetValue(pageNumber, out var walPage))
            return [.. walPage];

        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfNotReadable();
            var physicalPageCount = await _pageStore.GetPageCountAsync(cancellationToken).ConfigureAwait(false);
            if (pageNumber > physicalPageCount)
            {
                throw new InvalidDataException(
                    $"Snapshot page {pageNumber} is absent from both the WAL overlay and main database file.");
            }

            return await _pageStore.ReadPageAsync(pageNumber, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    internal void EndReadTransaction(
        AsyncSqlitePagerReadTransaction transaction,
        SqlitePagerLockLease readerLock)
    {
        lock (_stateGate)
            _activeReadTransactions.Remove(transaction);
        readerLock.Dispose();
    }

    internal async ValueTask CommitTransactionAsync(
        AsyncSqlitePagerTransaction transaction,
        CancellationToken cancellationToken)
    {
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateGate)
            {
                ThrowIfDisposed();
                if (_state != SqlitePagerState.TransactionActive || _activeTransaction != transaction)
                    throw new InvalidOperationException("This SQLite pager transaction is not active.");
            }

            ValidateTransaction(transaction);
            try
            {
                if (JournalMode == SqliteJournalMode.Delete)
                    await CommitRollbackTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
                else
                    await CommitWalTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);

                lock (_stateGate)
                {
                    _activeTransaction = null;
                    _state = SqlitePagerState.Ready;
                }
                _lockGeneration = transaction.PublishStorageChange();
                transaction.ReleaseWriterLock();
            }
            catch
            {
                lock (_stateGate)
                    _activeTransaction = null;
                TransitionToFaulted();
                transaction.ReleaseWriterLock();
                throw;
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    internal void RollbackTransaction(AsyncSqlitePagerTransaction transaction)
    {
        lock (_stateGate)
        {
            if (_state == SqlitePagerState.TransactionActive && _activeTransaction == transaction)
            {
                _activeTransaction = null;
                _state = SqlitePagerState.Ready;
                transaction.ReleaseWriterLock();
            }
            else if (_state == SqlitePagerState.Faulted)
            {
                transaction.ReleaseWriterLock();
            }
        }
    }

    private async ValueTask<SqliteCheckpointResult> CheckpointAsync(
        bool resetCommittedWal,
        TimeSpan? busyTimeout,
        CancellationToken cancellationToken)
    {
        var timeout = ResolveBusyTimeout(busyTimeout);
        using var checkpointLock = await _lockManager.EnterCheckpointAsync(timeout, cancellationToken)
            .ConfigureAwait(false);
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ThrowIfReadOnly();
            await SynchronizeCommittedViewAsync(cancellationToken).ConfigureAwait(false);
            if (JournalMode == SqliteJournalMode.Delete)
            {
                if (_state != SqlitePagerState.Ready)
                    throw new InvalidOperationException($"Cannot checkpoint while the SQLite pager is {_state}.");
                return new SqliteCheckpointResult(_committedPageCount, 0, 0);
            }
            if (HasUncommittedOrInvalidTail(_recoveryInfo))
            {
                throw new InvalidOperationException(
                    "Cannot checkpoint a SQLite WAL with an uncommitted or invalid tail; recover it first.");
            }
            if (_state != SqlitePagerState.Ready)
                throw new InvalidOperationException($"Cannot checkpoint while the SQLite pager is {_state}.");

            lock (_stateGate)
                _state = SqlitePagerState.Checkpointing;
            try
            {
                await ValidateWalHasNotChangedAsync(cancellationToken).ConfigureAwait(false);
                await RequireWal().FlushAsync(cancellationToken).ConfigureAwait(false);
                var installed = await InstallCommittedOverlayIntoMainStoreAsync(cancellationToken)
                    .ConfigureAwait(false);
                var retainedFrames = _committedFrameCount;
                if (resetCommittedWal)
                {
                    if (await _pageStore.GetPageCountAsync(cancellationToken).ConfigureAwait(false)
                        != _committedPageCount)
                    {
                        throw new InvalidDataException(
                            "Cannot reset a SQLite WAL before the main database file reaches the committed page count.");
                    }

                    await ValidateWalHasNotChangedAsync(cancellationToken).ConfigureAwait(false);
                    await RequireWal().ResetAfterDurableCheckpointAsync(
                        CanPublishCheckpointedRecoveryMarker(),
                        cancellationToken).ConfigureAwait(false);
                    var emptyRecovery = CreateEmptyRecoveryInfo();
                    var visibleRecovery = await CreateRecoveryVisibleInfoAsync(
                        emptyRecovery,
                        cancellationToken).ConfigureAwait(false);
                    lock (_stateGate)
                    {
                        _walPageOverlay.Clear();
                        _committedFrameCount = 0;
                        _recoveryInfo = emptyRecovery;
                        _visibleRecoveryInfo = visibleRecovery;
                    }
                    retainedFrames = 0;
                }

                _lockGeneration = checkpointLock.PublishStorageChange();
                lock (_stateGate)
                    _state = SqlitePagerState.Ready;
                return new SqliteCheckpointResult(_committedPageCount, installed, retainedFrames);
            }
            catch
            {
                TransitionToFaulted();
                throw;
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private async ValueTask CommitWalTransactionAsync(
        AsyncSqlitePagerTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ValidateWalHasNotChangedAsync(cancellationToken).ConfigureAwait(false);
        var wal = RequireWal();
        await wal.AppendFramesAsync(
            new AsyncSqlitePagerTransaction.WalFrameSource(transaction),
            transaction.TargetDatabaseSizeInPages,
            cancellationToken).ConfigureAwait(false);
        await wal.FlushAsync(cancellationToken).ConfigureAwait(false);
        var recovery = await wal.ScanRecoveryAsync(cancellationToken).ConfigureAwait(false);
        if (recovery.StopReason != SqliteWalRecoveryStopReason.EndOfFile
            || recovery.LastCommittedFrameNumber != recovery.LastValidFrameNumber
            || recovery.LastCommittedDatabaseSizeInPages != transaction.TargetDatabaseSizeInPages)
        {
            throw new InvalidDataException("SQLite WAL did not preserve the transaction commit boundary.");
        }

        var committedOverlay = CloneOverlay(_walPageOverlay);
        foreach (var pageNumber in transaction.WriteOrder)
            committedOverlay[pageNumber] = transaction.GetPageImage(pageNumber).ToArray();
        foreach (var pageNumber in committedOverlay.Keys
                     .Where(pageNumber => pageNumber > transaction.TargetDatabaseSizeInPages)
                     .ToArray())
        {
            committedOverlay.Remove(pageNumber);
        }

        await ValidateVisiblePageSourcesAsync(
            transaction.TargetDatabaseSizeInPages,
            await _pageStore.GetPageCountAsync(cancellationToken).ConfigureAwait(false),
            committedOverlay,
            cancellationToken).ConfigureAwait(false);
        lock (_stateGate)
        {
            _walPageOverlay.Clear();
            foreach (var pair in committedOverlay)
                _walPageOverlay[pair.Key] = pair.Value;
            _committedPageCount = transaction.TargetDatabaseSizeInPages;
            _committedFrameCount = recovery.LastCommittedFrameNumber;
            _recoveryInfo = recovery;
            _visibleRecoveryInfo = recovery;
        }
    }

    private async ValueTask CommitRollbackTransactionAsync(
        AsyncSqlitePagerTransaction transaction,
        CancellationToken cancellationToken)
    {
        var originalPageCount = _committedPageCount;
        var pagesToJournal = new HashSet<uint>(
            transaction.WriteOrder.Where(pageNumber => pageNumber <= originalPageCount));
        if (transaction.TargetDatabaseSizeInPages > originalPageCount)
            pagesToJournal.Add(1);
        if (transaction.TargetDatabaseSizeInPages < originalPageCount)
        {
            for (var pageNumber = transaction.TargetDatabaseSizeInPages + 1;
                 pageNumber <= originalPageCount;
                 pageNumber++)
            {
                pagesToJournal.Add(pageNumber);
                if (pageNumber == uint.MaxValue)
                    break;
            }
        }

        await SqliteRollbackJournal.RecoverIfPresentAsync(
            _fileSystem,
            _databasePath,
            _journalPath,
            readOnly: false,
            cancellationToken).ConfigureAwait(false);
        var orderedPages = pagesToJournal
            .Where(pageNumber => pageNumber >= 1 && pageNumber <= originalPageCount)
            .OrderBy(static pageNumber => pageNumber)
            .ToArray();
        var writer = await SqliteRollbackJournal.BeginAsync(
            _fileSystem,
            _journalPath,
            orderedPages.Length,
            unchecked((uint)Random.Shared.NextInt64()),
            originalPageCount,
            PageSize,
            cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var pageNumber in orderedPages)
            {
                await writer.WritePageRecordAsync(
                    pageNumber,
                    await _pageStore.ReadRawPageAsync(pageNumber, cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
            }
            await writer.FinalizeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await writer.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var pageNumber in transaction.WriteOrder
                     .Where(pageNumber => pageNumber != 1 && pageNumber <= originalPageCount)
                     .OrderBy(static pageNumber => pageNumber))
        {
            await _pageStore.WritePageAsync(
                pageNumber,
                transaction.GetPageImage(pageNumber),
                cancellationToken).ConfigureAwait(false);
        }
        foreach (var pageNumber in transaction.WriteOrder
                     .Where(pageNumber => pageNumber > originalPageCount)
                     .OrderBy(static pageNumber => pageNumber))
        {
            await _pageStore.WritePageAsync(
                pageNumber,
                transaction.GetPageImage(pageNumber),
                cancellationToken).ConfigureAwait(false);
        }
        if (transaction.PageImages.TryGetValue(1, out var pageOne))
        {
            if (transaction.TargetDatabaseSizeInPages < originalPageCount)
            {
                await _pageStore.WriteShrinkCheckpointPageOneAsync(pageOne, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await _pageStore.WritePageAsync(1, pageOne, cancellationToken).ConfigureAwait(false);
            }
        }

        await _pageStore.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (transaction.TargetDatabaseSizeInPages < originalPageCount)
        {
            await _pageStore.TruncateToPageCountAsync(
                transaction.TargetDatabaseSizeInPages,
                cancellationToken).ConfigureAwait(false);
            await _pageStore.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        await SqliteRollbackJournal.DeleteAsync(_fileSystem, _journalPath, cancellationToken)
            .ConfigureAwait(false);

        var emptyRecovery = CreateEmptyRecoveryInfo();
        lock (_stateGate)
        {
            _committedPageCount = transaction.TargetDatabaseSizeInPages;
            _committedFrameCount = 0;
            _recoveryInfo = emptyRecovery;
            _visibleRecoveryInfo = emptyRecovery;
            _walPageOverlay.Clear();
        }
    }

    private async ValueTask<int> InstallCommittedOverlayIntoMainStoreAsync(
        CancellationToken cancellationToken)
    {
        var originalPageCount = await _pageStore.GetPageCountAsync(cancellationToken).ConfigureAwait(false);
        var installed = 0;
        for (var pageNumber = checked(originalPageCount + 1);
             pageNumber <= _committedPageCount;
             pageNumber++)
        {
            if (!_walPageOverlay.TryGetValue(pageNumber, out var page))
            {
                throw new InvalidDataException(
                    $"Committed WAL view is missing required appended page {pageNumber}.");
            }
            await _pageStore.WritePageAsync(pageNumber, page, cancellationToken).ConfigureAwait(false);
            installed++;
            if (pageNumber == uint.MaxValue)
                break;
        }

        foreach (var pageNumber in _walPageOverlay.Keys
                     .Where(pageNumber => pageNumber <= Math.Min(originalPageCount, _committedPageCount)
                                          && pageNumber != 1)
                     .OrderBy(static pageNumber => pageNumber))
        {
            await _pageStore.WritePageAsync(
                pageNumber,
                _walPageOverlay[pageNumber],
                cancellationToken).ConfigureAwait(false);
            installed++;
        }

        if (_committedPageCount < originalPageCount)
            ValidateShrinkCheckpointPageOne();
        if (_walPageOverlay.TryGetValue(1, out var firstPage))
        {
            if (_committedPageCount < originalPageCount)
            {
                await _pageStore.WriteShrinkCheckpointPageOneAsync(firstPage, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await _pageStore.WritePageAsync(1, firstPage, cancellationToken).ConfigureAwait(false);
            }
            installed++;
        }

        await _pageStore.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (_committedPageCount < originalPageCount)
        {
            await _pageStore.TruncateToPageCountAsync(_committedPageCount, cancellationToken)
                .ConfigureAwait(false);
            await _pageStore.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        return installed;
    }

    private async ValueTask SynchronizeCommittedViewAsync(CancellationToken cancellationToken)
    {
        if (JournalMode == SqliteJournalMode.Delete)
        {
            await _pageStore.RefreshHeaderAsync(cancellationToken).ConfigureAwait(false);
            await InitializeRollbackViewAsync(cancellationToken).ConfigureAwait(false);
            _lockGeneration = _lockManager.Generation;
            return;
        }

        if (_wal is null
            && await _fileSystem.FileExistsAsync(_walPath, cancellationToken).ConfigureAwait(false))
        {
            _wal = await SqliteWalFile.OpenAsync(
                _fileSystem,
                _walPath,
                _pageStore.IsReadOnly,
                _encryption,
                CreateTruncatedWalHeader(PageSize),
                _pageCodec,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        if (_wal is null)
        {
            await _pageStore.RefreshHeaderAsync(cancellationToken).ConfigureAwait(false);
            await InitializeCleanWalViewAsync(cancellationToken).ConfigureAwait(false);
            _lockGeneration = _lockManager.Generation;
            return;
        }

        var recovery = await _wal.ScanRecoveryAsync(cancellationToken).ConfigureAwait(false);
        if (recovery.LastCommittedFrameNumber == 0
            && recovery.StopReason == SqliteWalRecoveryStopReason.EndOfFile)
        {
            await _pageStore.RefreshHeaderAsync(cancellationToken).ConfigureAwait(false);
        }
        await InitializeCommittedViewAsync(recovery, cancellationToken).ConfigureAwait(false);
        _lockGeneration = _lockManager.Generation;
    }

    private async ValueTask RecoverWalTailUnderWriterLockAsync(CancellationToken cancellationToken)
    {
        var wal = RequireWal();
        var recovery = await wal.ScanRecoveryAsync(cancellationToken).ConfigureAwait(false);
        await InitializeCommittedViewAsync(recovery, cancellationToken).ConfigureAwait(false);
        if (!HasUncommittedOrInvalidTail(recovery))
            return;

        var repairedFrom = await wal.RecoverToLastCommittedFrameAsync(cancellationToken)
            .ConfigureAwait(false);
        if (repairedFrom != recovery)
            throw new InvalidDataException("SQLite WAL changed between recovery scanning and tail repair.");
        var repaired = await wal.ScanRecoveryAsync(cancellationToken).ConfigureAwait(false);
        await InitializeCommittedViewAsync(repaired, cancellationToken).ConfigureAwait(false);
        lock (_stateGate)
            _visibleRecoveryInfo = recovery;
    }

    private async ValueTask InitializeCommittedViewAsync(
        SqliteWalRecoveryInfo recovery,
        CancellationToken cancellationToken)
    {
        var wal = RequireWal();
        if (_pageStore.PageSize != wal.PageSize)
            throw new InvalidDataException("SQLite database and WAL page sizes do not match.");
        if (!IsWalCompatibleFormat(_pageStore.Header.WriteVersion)
            || !IsWalCompatibleFormat(_pageStore.Header.ReadVersion))
        {
            throw new InvalidDataException(
                "A SQLite WAL overlay requires WAL/MVCC read and write format versions.");
        }

        var mainPageCount = await _pageStore.GetPageCountAsync(cancellationToken).ConfigureAwait(false);
        var committedPageCount = mainPageCount;
        var overlay = new Dictionary<uint, byte[]>();
        var transactionPages = new Dictionary<uint, byte[]>();
        var finalTransactionHasPageOne = false;
        if (recovery.LastCommittedFrameNumber > 0)
        {
            var frameNumber = 0L;
            foreach (var frame in await wal.ReadFrameRangeAsync(
                         1,
                         recovery.LastCommittedFrameNumber,
                         cancellationToken).ConfigureAwait(false))
            {
                frameNumber++;
                transactionPages[frame.Header.PageNumber] = frame.PageData;
                if (!frame.Header.IsCommit)
                    continue;

                ValidateRecoveredTransaction(transactionPages, frame.Header.DatabaseSizeInPages);
                foreach (var pageNumber in overlay.Keys
                             .Where(pageNumber => pageNumber > frame.Header.DatabaseSizeInPages)
                             .ToArray())
                {
                    overlay.Remove(pageNumber);
                }
                foreach (var pair in transactionPages)
                    overlay[pair.Key] = pair.Value;
                committedPageCount = frame.Header.DatabaseSizeInPages;
                if (frameNumber == recovery.LastCommittedFrameNumber)
                    finalTransactionHasPageOne = transactionPages.ContainsKey(1);
                transactionPages.Clear();
            }
        }
        if (transactionPages.Count != 0)
            throw new InvalidDataException("SQLite WAL recovery stopped before a committed transaction boundary.");

        ValidateTrailingMainDatabasePages(
            recovery,
            mainPageCount,
            committedPageCount,
            finalTransactionHasPageOne,
            overlay);
        await ValidateVisiblePageSourcesAsync(
            committedPageCount,
            mainPageCount,
            overlay,
            cancellationToken).ConfigureAwait(false);

        var visibleRecovery = await CreateRecoveryVisibleInfoAsync(recovery, cancellationToken)
            .ConfigureAwait(false);
        lock (_stateGate)
        {
            _walPageOverlay.Clear();
            foreach (var pair in overlay)
                _walPageOverlay[pair.Key] = pair.Value;
            _committedPageCount = committedPageCount;
            _committedFrameCount = recovery.LastCommittedFrameNumber;
            _recoveryInfo = recovery;
            _visibleRecoveryInfo = visibleRecovery;
        }
    }

    private async ValueTask InitializeRollbackViewAsync(CancellationToken cancellationToken)
    {
        var header = _pageStore.Header;
        if (header.WriteVersion != SqliteFileFormatVersion.Legacy
            || header.ReadVersion != SqliteFileFormatVersion.Legacy)
        {
            throw new InvalidDataException(
                "A SQLite rollback-journal pager requires legacy read and write format versions.");
        }
        var pageCount = await _pageStore.GetPageCountAsync(cancellationToken).ConfigureAwait(false);
        if (header.VersionValidFor == header.ChangeCounter
            && header.DatabaseSizeInPages != 0
            && header.DatabaseSizeInPages != pageCount)
        {
            throw new InvalidDataException(
                "SQLite rollback-journal database header page count does not match the main file.");
        }

        var recovery = CreateEmptyRecoveryInfo();
        lock (_stateGate)
        {
            _committedPageCount = pageCount;
            _committedFrameCount = 0;
            _walPageOverlay.Clear();
            _recoveryInfo = recovery;
            _visibleRecoveryInfo = recovery;
        }
    }

    private async ValueTask InitializeCleanWalViewAsync(CancellationToken cancellationToken)
    {
        var header = _pageStore.Header;
        if (!IsWalCompatibleFormat(header.WriteVersion)
            || !IsWalCompatibleFormat(header.ReadVersion))
        {
            throw new InvalidDataException("A clean SQLite WAL view requires WAL/MVCC read and write format versions.");
        }
        var pageCount = await _pageStore.GetPageCountAsync(cancellationToken).ConfigureAwait(false);
        if (header.VersionValidFor != header.ChangeCounter
            || header.DatabaseSizeInPages != pageCount)
        {
            throw new InvalidDataException(
                "A SQLite WAL database without a WAL file must have an authoritative main-database header.");
        }

        var recovery = CreateEmptyRecoveryInfo();
        lock (_stateGate)
        {
            _committedPageCount = pageCount;
            _committedFrameCount = 0;
            _walPageOverlay.Clear();
            _recoveryInfo = recovery;
            _visibleRecoveryInfo = recovery;
        }
    }

    private async ValueTask<SqliteWalRecoveryInfo> CreateRecoveryVisibleInfoAsync(
        SqliteWalRecoveryInfo recovery,
        CancellationToken cancellationToken)
    {
        if (recovery.LastValidFrameNumber != 0
            || recovery.LastCommittedFrameNumber != 0
            || recovery.StopReason != SqliteWalRecoveryStopReason.EndOfFile
            || !RequireWal().HasCheckpointedRecoveryMarker)
        {
            return recovery;
        }

        var pageCount = await _pageStore.GetPageCountAsync(cancellationToken).ConfigureAwait(false);
        var header = _pageStore.Header;
        if (pageCount == 0
            || header.VersionValidFor != header.ChangeCounter
            || header.DatabaseSizeInPages != pageCount)
        {
            throw new InvalidDataException(
                "SQLite WAL checkpoint recovery marker does not have an authoritative durable main-database state.");
        }
        return new SqliteWalRecoveryInfo(
            LastValidFrameNumber: 0,
            LastCommittedFrameNumber: 1,
            LastCommittedDatabaseSizeInPages: pageCount,
            LastCommittedByteLength: SqliteWalHeader.Size,
            StopReason: SqliteWalRecoveryStopReason.EndOfFile);
    }

    private void ValidateTransaction(AsyncSqlitePagerTransaction transaction)
    {
        if (transaction.WriteOrder.Count == 0)
            throw new InvalidOperationException("A SQLite pager transaction must contain at least one complete page image.");
        foreach (var pageNumber in transaction.WriteOrder)
        {
            if (pageNumber == 0 || pageNumber > transaction.TargetDatabaseSizeInPages)
            {
                throw new InvalidOperationException(
                    $"SQLite pager transaction page {pageNumber} is outside its committed database size.");
            }
        }

        ValidatePageOneImage(transaction.PageImages, transaction.TargetDatabaseSizeInPages);
        if (transaction.TargetDatabaseSizeInPages < _committedPageCount)
            ValidateShrinkTransactionPageOne(transaction.PageImages, transaction.TargetDatabaseSizeInPages);
        if (transaction.TargetDatabaseSizeInPages <= _committedPageCount)
            return;

        var required = (ulong)transaction.TargetDatabaseSizeInPages - _committedPageCount;
        var provided = transaction.WriteOrder.Count(
            pageNumber => pageNumber > _committedPageCount
                          && pageNumber <= transaction.TargetDatabaseSizeInPages);
        if ((ulong)provided != required)
        {
            throw new InvalidOperationException(
                "Every newly committed SQLite page must have an explicit page image in the transaction.");
        }
    }

    private void ValidateRecoveredTransaction(
        IReadOnlyDictionary<uint, byte[]> transactionPages,
        uint targetPageCount)
    {
        if (targetPageCount == 0)
            throw new InvalidDataException("SQLite WAL commit frame has a zero database size.");
        foreach (var pageNumber in transactionPages.Keys)
        {
            if (pageNumber > targetPageCount)
            {
                throw new InvalidDataException(
                    $"SQLite WAL transaction writes page {pageNumber} beyond committed database size {targetPageCount}.");
            }
        }
        ValidatePageOneImage(transactionPages, targetPageCount);
    }

    private void ValidatePageOneImage(
        IReadOnlyDictionary<uint, byte[]> transactionPages,
        uint targetPageCount)
    {
        if (!transactionPages.TryGetValue(1, out var pageOne))
            return;
        var header = SqliteDatabaseHeader.Parse(pageOne);
        if (header.PageSize != _pageStore.PageSize)
            throw new InvalidDataException("SQLite transaction page 1 changes the database page size.");
        var expectedVersion = FormatVersionFor(JournalMode);
        if (header.WriteVersion != expectedVersion || header.ReadVersion != expectedVersion)
        {
            throw new InvalidDataException(
                $"SQLite transaction page 1 does not match the active {JournalMode} journal mode.");
        }
        if (header.VersionValidFor == header.ChangeCounter
            && header.DatabaseSizeInPages != 0
            && header.DatabaseSizeInPages != targetPageCount)
        {
            throw new InvalidDataException(
                "SQLite transaction page 1 has an authoritative page count different from its commit size.");
        }
    }

    private static void ValidateShrinkTransactionPageOne(
        IReadOnlyDictionary<uint, byte[]> transactionPages,
        uint targetPageCount)
    {
        if (!transactionPages.TryGetValue(1, out var pageOne))
        {
            throw new InvalidOperationException(
                "A database-shrinking SQLite transaction must rewrite page 1 with the new authoritative page count.");
        }
        var header = SqliteDatabaseHeader.Parse(pageOne);
        if (header.VersionValidFor != header.ChangeCounter
            || header.DatabaseSizeInPages != targetPageCount)
        {
            throw new InvalidDataException(
                "A database-shrinking SQLite transaction must make page 1's page count authoritative and equal to its commit size.");
        }
    }

    private void ValidateShrinkCheckpointPageOne()
    {
        if (!_walPageOverlay.TryGetValue(1, out var pageOne))
        {
            throw new InvalidDataException(
                "Cannot checkpoint a database-shrinking WAL view without its committed page 1 image.");
        }
        var header = SqliteDatabaseHeader.Parse(pageOne);
        if (header.VersionValidFor != header.ChangeCounter
            || header.DatabaseSizeInPages != _committedPageCount)
        {
            throw new InvalidDataException(
                "Cannot checkpoint a database-shrinking WAL view whose page 1 does not authoritatively declare the committed size.");
        }
    }

    private void ValidateTrailingMainDatabasePages(
        SqliteWalRecoveryInfo recovery,
        uint mainPageCount,
        uint committedPageCount,
        bool finalTransactionHasPageOne,
        IReadOnlyDictionary<uint, byte[]> overlay)
    {
        if (mainPageCount <= committedPageCount)
        {
            var header = _pageStore.Header;
            if (header.VersionValidFor == header.ChangeCounter
                && header.DatabaseSizeInPages != 0
                && header.DatabaseSizeInPages < mainPageCount)
            {
                throw new InvalidDataException(
                    "SQLite database header declares a smaller authoritative size without a recoverable shrinking WAL commit.");
            }
            return;
        }
        if (recovery.LastCommittedFrameNumber == 0
            || !finalTransactionHasPageOne
            || !overlay.TryGetValue(1, out var pageOne))
        {
            throw new InvalidDataException(
                "SQLite database has pages beyond its authoritative size without a recoverable shrinking WAL commit.");
        }

        var mainHeader = _pageStore.Header;
        var walHeader = SqliteDatabaseHeader.Parse(pageOne);
        if (walHeader.VersionValidFor != walHeader.ChangeCounter
            || walHeader.DatabaseSizeInPages != committedPageCount)
        {
            throw new InvalidDataException(
                "SQLite database has pages beyond its authoritative size, but its retained WAL does not contain the shrinking transaction's authoritative page 1.");
        }
        if (mainHeader.VersionValidFor == mainHeader.ChangeCounter
            && mainHeader.DatabaseSizeInPages == mainPageCount)
        {
            return;
        }
        if (mainHeader.DatabaseSizeInPages != committedPageCount || walHeader != mainHeader)
        {
            throw new InvalidDataException(
                "SQLite database has pages beyond its authoritative size, but its retained WAL does not prove a matching interrupted shrink checkpoint.");
        }
    }

    private async ValueTask ValidateVisiblePageSourcesAsync(CancellationToken cancellationToken)
        => await ValidateVisiblePageSourcesAsync(
            _committedPageCount,
            await _pageStore.GetPageCountAsync(cancellationToken).ConfigureAwait(false),
            _walPageOverlay,
            cancellationToken).ConfigureAwait(false);

    private static ValueTask ValidateVisiblePageSourcesAsync(
        uint committedPageCount,
        uint mainPageCount,
        IReadOnlyDictionary<uint, byte[]> overlay,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (committedPageCount <= mainPageCount)
            return ValueTask.CompletedTask;
        var required = (ulong)committedPageCount - mainPageCount;
        var available = overlay.Keys.LongCount(
            pageNumber => pageNumber > mainPageCount && pageNumber <= committedPageCount);
        if ((ulong)available != required)
        {
            throw new InvalidDataException(
                "SQLite WAL commit declares appended pages that are absent from both the WAL and main database file.");
        }
        return ValueTask.CompletedTask;
    }

    private async ValueTask ValidateWalHasNotChangedAsync(CancellationToken cancellationToken)
    {
        var recovery = await RequireWal().ScanRecoveryAsync(cancellationToken).ConfigureAwait(false);
        if (recovery.StopReason != SqliteWalRecoveryStopReason.EndOfFile
            || recovery.LastValidFrameNumber != _committedFrameCount
            || recovery.LastCommittedFrameNumber != _committedFrameCount
            || (recovery.LastCommittedFrameNumber != 0
                && recovery.LastCommittedDatabaseSizeInPages != _committedPageCount))
        {
            throw new InvalidDataException(
                "SQLite WAL changed outside this pager; begin a new transaction before writing.");
        }
    }

    private bool CanPublishCheckpointedRecoveryMarker()
    {
        var header = _pageStore.Header;
        return _walPageOverlay.ContainsKey(1)
               && header.VersionValidFor == header.ChangeCounter
               && header.DatabaseSizeInPages == _committedPageCount;
    }

    private TimeSpan ResolveBusyTimeout(TimeSpan? busyTimeout)
    {
        ValidateBusyTimeout(busyTimeout, nameof(busyTimeout));
        lock (_stateGate)
        {
            ThrowIfDisposed();
            return busyTimeout ?? _busyTimeout;
        }
    }

    private void ThrowIfNotReadable()
    {
        ThrowIfDisposed();
        if (_state is not SqlitePagerState.Ready and not SqlitePagerState.TransactionActive)
            throw new InvalidOperationException($"Cannot read from a SQLite pager while it is {_state}.");
    }

    private void ThrowIfReadOnly()
    {
        if (_pageStore.IsReadOnly || (_wal?.IsReadOnly ?? false))
            throw new InvalidOperationException("Cannot write through a read-only SQLite pager.");
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_state == SqlitePagerState.Disposed, this);

    private void TransitionToFaulted()
    {
        lock (_stateGate)
        {
            if (_state != SqlitePagerState.Disposed)
                _state = SqlitePagerState.Faulted;
        }
    }

    private SqliteWalFile RequireWal()
        => _wal ?? throw new InvalidOperationException("This SQLite pager has no WAL file.");

    private static SqliteWalHeader CreateTruncatedWalHeader(int pageSize)
        => SqliteWalHeader.Create(
            pageSize,
            unchecked((uint)Random.Shared.NextInt64()),
            unchecked((uint)Random.Shared.NextInt64()));

    private static SqliteWalRecoveryInfo CreateEmptyRecoveryInfo()
        => new(
            LastValidFrameNumber: 0,
            LastCommittedFrameNumber: 0,
            LastCommittedDatabaseSizeInPages: 0,
            LastCommittedByteLength: SqliteWalHeader.Size,
            StopReason: SqliteWalRecoveryStopReason.EndOfFile);

    private static bool HasUncommittedOrInvalidTail(SqliteWalRecoveryInfo recovery)
        => recovery.StopReason != SqliteWalRecoveryStopReason.EndOfFile
           || recovery.LastValidFrameNumber != recovery.LastCommittedFrameNumber;

    private static bool UsesWalStorage(SqliteJournalMode journalMode)
        => journalMode is SqliteJournalMode.Wal or SqliteJournalMode.Mvcc;

    private static bool IsWalCompatibleFormat(SqliteFileFormatVersion version)
        => version is SqliteFileFormatVersion.Wal or SqliteFileFormatVersion.Mvcc;

    private static SqliteFileFormatVersion FormatVersionFor(SqliteJournalMode journalMode)
        => journalMode switch
        {
            SqliteJournalMode.Wal => SqliteFileFormatVersion.Wal,
            SqliteJournalMode.Mvcc => SqliteFileFormatVersion.Mvcc,
            _ => SqliteFileFormatVersion.Legacy,
        };

    private static SqliteJournalMode GetJournalMode(SqliteDatabaseHeader header)
    {
        if (header.WriteVersion != header.ReadVersion)
        {
            throw new InvalidDataException(
                "SQLite database read and write format versions must match for managed storage.");
        }
        return header.WriteVersion switch
        {
            SqliteFileFormatVersion.Legacy => SqliteJournalMode.Delete,
            SqliteFileFormatVersion.Wal => SqliteJournalMode.Wal,
            SqliteFileFormatVersion.Mvcc => SqliteJournalMode.Mvcc,
            _ => throw new InvalidDataException(
                $"Managed storage does not support SQLite file format version {header.WriteVersion}."),
        };
    }

    private static Dictionary<uint, byte[]> CloneOverlay(
        IReadOnlyDictionary<uint, byte[]> overlay)
        => overlay.ToDictionary(static pair => pair.Key, static pair => pair.Value.ToArray());

    private static void ValidateBusyTimeout(TimeSpan? timeout, string parameterName)
    {
        if (timeout is not null)
            ValidateBusyTimeout(timeout.Value, parameterName);
    }

    private static void ValidateBusyTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(parameterName, timeout, "Busy timeout cannot be negative.");
    }

    private static SqlitePagerLockManager GetLockManager(
        IAsyncFileSystem fileSystem,
        string databasePath,
        string walPath)
    {
        var scope = LockScopes.GetValue(fileSystem, static _ => new AsyncLockScope());
        var key = CreateLockKey(fileSystem, databasePath, walPath);
        return scope.GetOrAdd(key);
    }

    private static string CreateLockKey(
        IAsyncFileSystem fileSystem,
        string databasePath,
        string walPath)
    {
        if (fileSystem is IStoragePathResolver resolver)
        {
            databasePath = resolver.GetCanonicalPath(databasePath);
            walPath = resolver.GetCanonicalPath(walPath);
        }
        return string.Concat(databasePath, "\0", walPath);
    }

    private static async ValueTask DisposeIgnoringFailureAsync(IAsyncDisposable disposable)
    {
        try
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static async ValueTask DeleteIgnoringFailureAsync(
        IAsyncFileSystem fileSystem,
        string path)
    {
        try
        {
            await fileSystem.DeleteFileAsync(path, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private sealed class AsyncLockScope
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, SqlitePagerLockManager> _locks = new(StringComparer.Ordinal);

        internal SqlitePagerLockManager GetOrAdd(string key)
        {
            lock (_gate)
            {
                if (!_locks.TryGetValue(key, out var manager))
                {
                    manager = new SqlitePagerLockManager();
                    _locks.Add(key, manager);
                }
                return manager;
            }
        }
    }

    private sealed class AsyncLockLease(SqlitePagerLockLease lease) : IAsyncDisposable
    {
        internal SqlitePagerLockLease Lease { get; } = lease;

        internal static async ValueTask<AsyncLockLease> AcquireReaderAsync(
            SqlitePagerLockManager manager,
            TimeSpan timeout,
            CancellationToken cancellationToken)
            => new(await manager.EnterReaderAsync(timeout, cancellationToken).ConfigureAwait(false));

        internal static async ValueTask<AsyncLockLease> AcquireCheckpointAsync(
            SqlitePagerLockManager manager,
            TimeSpan timeout,
            CancellationToken cancellationToken)
            => new(await manager.EnterCheckpointAsync(timeout, cancellationToken).ConfigureAwait(false));

        public ValueTask DisposeAsync()
        {
            Lease.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A stable committed snapshot owned by an <see cref="AsyncSqlitePager"/>.</summary>
public sealed class AsyncSqlitePagerReadTransaction : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AsyncSqlitePager _pager;
    private readonly IReadOnlyDictionary<uint, byte[]> _walPageOverlay;
    private SqlitePagerLockLease? _readerLock;

    internal AsyncSqlitePagerReadTransaction(
        AsyncSqlitePager pager,
        SqlitePagerLockLease readerLock,
        uint pageCount,
        IReadOnlyDictionary<uint, byte[]> walPageOverlay)
    {
        _pager = pager;
        _readerLock = readerLock;
        PageCount = pageCount;
        _walPageOverlay = walPageOverlay;
    }

    /// <summary>The database size captured at snapshot start.</summary>
    public uint PageCount { get; }

    /// <summary>Whether the snapshot still owns its reader lease.</summary>
    public bool IsActive => Volatile.Read(ref _readerLock) is not null;

    /// <summary>Reads one page from the stable snapshot.</summary>
    public async ValueTask<byte[]> ReadPageAsync(
        uint pageNumber,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_readerLock is null, this);
            return await _pager.ReadSnapshotPageAsync(
                _walPageOverlay,
                PageCount,
                pageNumber,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var readerLock = _readerLock;
            _readerLock = null;
            if (readerLock is not null)
                _pager.EndReadTransaction(this, readerLock);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal void InvalidateFromPagerDispose()
    {
        var readerLock = Interlocked.Exchange(ref _readerLock, null);
        readerLock?.Dispose();
    }
}

/// <summary>
/// In-memory page images that become visible only after their durable commit.
/// </summary>
public sealed class AsyncSqlitePagerTransaction : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AsyncSqlitePager _pager;
    private readonly uint _sourcePageCount;
    private readonly IReadOnlyDictionary<uint, byte[]> _sourceOverlay;
    private readonly Dictionary<uint, byte[]> _pageImages = [];
    private readonly List<uint> _writeOrder = [];
    private SqlitePagerLockLease? _writerLock;
    private SqlitePagerTransactionState _state = SqlitePagerTransactionState.Active;

    internal AsyncSqlitePagerTransaction(
        AsyncSqlitePager pager,
        SqlitePagerLockLease writerLock,
        uint targetDatabaseSizeInPages,
        uint sourcePageCount,
        IReadOnlyDictionary<uint, byte[]> sourceOverlay)
    {
        _pager = pager;
        _writerLock = writerLock;
        TargetDatabaseSizeInPages = targetDatabaseSizeInPages;
        _sourcePageCount = sourcePageCount;
        _sourceOverlay = sourceOverlay;
    }

    /// <summary>The database size written into this transaction's commit boundary.</summary>
    public uint TargetDatabaseSizeInPages { get; }

    /// <summary>The transaction lifecycle state.</summary>
    public SqlitePagerTransactionState State => _state;

    internal IReadOnlyDictionary<uint, byte[]> PageImages => _pageImages;
    internal IReadOnlyList<uint> WriteOrder => _writeOrder;

    /// <summary>Stages one complete page image.</summary>
    public async ValueTask WritePageAsync(
        uint pageNumber,
        ReadOnlyMemory<byte> page,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfNotActive();
            if (pageNumber == 0 || pageNumber > TargetDatabaseSizeInPages)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pageNumber),
                    pageNumber,
                    $"Page number must be between 1 and {TargetDatabaseSizeInPages}.");
            }
            if (page.Length != _pager.PageSize)
                throw new ArgumentException($"Page data must be exactly {_pager.PageSize} bytes.", nameof(page));
            if (!_pageImages.ContainsKey(pageNumber))
                _writeOrder.Add(pageNumber);
            _pageImages[pageNumber] = page.ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reads the latest staged image or the committed source snapshot.</summary>
    public async ValueTask<byte[]> ReadPageAsync(
        uint pageNumber,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfNotActive();
            if (_pageImages.TryGetValue(pageNumber, out var page))
                return [.. page];
            return await _pager.ReadSnapshotPageAsync(
                _sourceOverlay,
                _sourcePageCount,
                pageNumber,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Durably commits all staged pages.</summary>
    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfNotActive();
            try
            {
                await _pager.CommitTransactionAsync(this, cancellationToken).ConfigureAwait(false);
                _state = SqlitePagerTransactionState.Committed;
            }
            catch
            {
                if (_pager.State == SqlitePagerState.Faulted)
                {
                    _state = SqlitePagerTransactionState.Faulted;
                    ReleaseWriterLock();
                }
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Discards all staged pages without writing storage.</summary>
    public async ValueTask RollbackAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfNotActive();
            _pageImages.Clear();
            _writeOrder.Clear();
            _pager.RollbackTransaction(this);
            _state = SqlitePagerTransactionState.RolledBack;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_state == SqlitePagerTransactionState.Active)
            {
                _pageImages.Clear();
                _writeOrder.Clear();
                _pager.RollbackTransaction(this);
                _state = SqlitePagerTransactionState.RolledBack;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal byte[] GetPageImage(uint pageNumber)
        => _pageImages.TryGetValue(pageNumber, out var page)
            ? page
            : throw new InvalidOperationException($"SQLite pager transaction has no image for page {pageNumber}.");

    internal long PublishStorageChange()
        => (_writerLock
            ?? throw new InvalidOperationException("SQLite pager transaction no longer owns its writer lock."))
            .PublishStorageChange();

    internal void ReleaseWriterLock()
    {
        var writerLock = Interlocked.Exchange(ref _writerLock, null);
        writerLock?.Dispose();
    }

    internal void AbortFromPagerDispose()
    {
        if (_state == SqlitePagerTransactionState.Active)
        {
            _pageImages.Clear();
            _writeOrder.Clear();
            _state = SqlitePagerTransactionState.RolledBack;
        }
        ReleaseWriterLock();
    }

    private void ThrowIfNotActive()
    {
        if (_state != SqlitePagerTransactionState.Active)
            throw new InvalidOperationException($"SQLite pager transaction is {_state}.");
    }

    internal sealed class WalFrameSource(AsyncSqlitePagerTransaction transaction)
        : ISqliteWalFrameSource
    {
        public int Count => transaction.WriteOrder.Count;
        public uint GetPageNumber(int index) => transaction.WriteOrder[index];
        public ReadOnlySpan<byte> GetPageImage(int index)
            => transaction.GetPageImage(transaction.WriteOrder[index]);
    }
}
