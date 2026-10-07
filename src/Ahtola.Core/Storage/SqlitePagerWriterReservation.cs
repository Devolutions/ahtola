namespace Ahtola.Core.Storage;

/// <summary>
/// The write lock an explicit write transaction holds from <c>BEGIN IMMEDIATE</c> (or a
/// deferred transaction's first write) until it commits or rolls back: SQLite's RESERVED
/// main-file lock in rollback-journal mode, or the WAL write lock (<c>-shm</c> byte 120) in WAL
/// mode, plus the process-local writer role. Holding it is what keeps other connections and
/// other processes from writing while the transaction is open, so its commit cannot lose the
/// lock.
/// </summary>
/// <remarks>
/// The commit's pager transaction borrows the leases (<see cref="TryLend"/>). A successful
/// commit consumes them; a commit that fails (for example busy on the EXCLUSIVE upgrade) hands
/// them back, so the transaction keeps its lock and a retried <c>COMMIT</c> can succeed, as in
/// SQLite.
/// </remarks>
internal sealed class SqlitePagerWriterReservation : IDisposable
{
    private readonly object _gate = new();
    private SqlitePagerLockLease? _writerLock;
    private SqliteMainFileLockLease? _mainFileLock;
    private bool _lent;
    private bool _disposed;

    internal SqlitePagerWriterReservation(
        SqlitePagerLockManager lockManager,
        SqlitePagerLockLease writerLock,
        SqliteMainFileLockLease? mainFileLock,
        bool deleteMode,
        bool persistentExclusive)
    {
        LockManager = lockManager;
        _writerLock = writerLock;
        _mainFileLock = mainFileLock;
        DeleteMode = deleteMode;
        PersistentExclusive = persistentExclusive;
    }

    internal SqlitePagerLockManager LockManager { get; }

    internal bool DeleteMode { get; }

    internal bool PersistentExclusive { get; }

    /// <summary>Whether the reservation still holds its locks (lent or not).</summary>
    internal bool IsHeld
    {
        get
        {
            lock (_gate)
                return !_disposed && (_lent || _writerLock is { IsActive: true });
        }
    }

    /// <summary>Hands the held leases to a pager transaction.</summary>
    internal bool TryLend(out SqlitePagerLockLease writerLock, out SqliteMainFileLockLease? mainFileLock)
    {
        lock (_gate)
        {
            if (_disposed || _lent || _writerLock is not { IsActive: true } held)
            {
                writerLock = null!;
                mainFileLock = null;
                return false;
            }

            writerLock = held;
            mainFileLock = _mainFileLock;
            _writerLock = null;
            _mainFileLock = null;
            _lent = true;
            return true;
        }
    }

    /// <summary>
    /// Takes back leases a pager transaction borrowed but did not commit. When the
    /// reservation was disposed meanwhile, the leases are released instead.
    /// </summary>
    internal void Return(SqlitePagerLockLease? writerLock, SqliteMainFileLockLease? mainFileLock)
    {
        var release = false;
        lock (_gate)
        {
            _lent = false;
            if (_disposed || writerLock is not { IsActive: true })
            {
                release = true;
            }
            else
            {
                _writerLock = writerLock;
                _mainFileLock = mainFileLock;
            }
        }

        if (release)
            ReleaseLeases(writerLock, mainFileLock);
    }

    /// <summary>Records that a committed pager transaction released the borrowed leases.</summary>
    internal void Consume()
    {
        lock (_gate)
        {
            _lent = false;
            _disposed = true;
        }
    }

    public void Dispose()
    {
        SqlitePagerLockLease? writerLock;
        SqliteMainFileLockLease? mainFileLock;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_lent)
                return;

            writerLock = _writerLock;
            mainFileLock = _mainFileLock;
            _writerLock = null;
            _mainFileLock = null;
        }

        ReleaseLeases(writerLock, mainFileLock);
    }

    private static void ReleaseLeases(SqlitePagerLockLease? writerLock, SqliteMainFileLockLease? mainFileLock)
    {
        try
        {
            mainFileLock?.Dispose();
        }
        finally
        {
            writerLock?.Dispose();
        }
    }
}
