using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace Ahtola.Core.Storage;

/// <summary>
/// Raised when a physical managed database cannot obtain its required main-file
/// SQLite SHARED lock.
/// </summary>
public sealed class SqlitePagerClientOwnershipException : InvalidOperationException
{
    internal SqlitePagerClientOwnershipException(
        string databasePath,
        TimeSpan timeout,
        Exception innerException)
        : base(
            $"Managed main-file SHARED lock for database '{databasePath}' could not be acquired within {timeout}. "
            + "Another client likely holds PENDING or EXCLUSIVE on the main database file.",
            innerException)
    {
        DatabasePath = databasePath;
        Timeout = timeout;
    }

    /// <summary>The fully qualified database path whose lock was rejected.</summary>
    public string DatabasePath { get; }

    /// <summary>The configured acquisition timeout.</summary>
    public TimeSpan Timeout { get; }
}

internal enum SqliteMainFileLockState
{
    Shared,
    Reserved,
    Pending,
    Exclusive,
    Disposed,
}

/// <summary>
/// Process-wide broker for SQLite's rollback-journal main-file lock bytes.
/// Logical locks from every managed handle are aggregated onto one stable OS
/// handle so Windows handle-scoped locks and Unix process-scoped locks expose
/// the same protocol.
/// </summary>
internal sealed class SqliteManagedFileOwnership
{
    internal const long PendingByte = 0x4000_0000;
    internal const long ReservedByte = PendingByte + 1;
    internal const long SharedFirstByte = PendingByte + 2;
    internal const long SharedSize = 510;

    private static readonly TimeSpan MaximumMonitorTimeout = TimeSpan.FromMilliseconds(int.MaxValue);
    private readonly object _gate = new();
    private string _databasePath;
    private readonly List<SafeFileHandle> _deferredPagerHandles = [];
    private readonly List<SqliteByteRangeLockHandle> _deferredLockHandles = [];
    private SqliteByteRangeLockHandle? _lockHandle;
    private SqliteMainFileLockLease? _writer;
    private int _clientCount;
    private int _blockingSharedCount;
    private int _sharedCount;
    private bool _acquiringShared;
    private bool _lockHandleWritable;
    private bool _acquiringClient;
    private Exception? _failure;

    internal SqliteManagedFileOwnership(string databasePath) => _databasePath = databasePath;

    internal SqliteManagedFileOwnershipClient AcquireClient(
        string databasePath,
        bool createNew,
        bool readOnly,
        TimeSpan timeout)
    {
        var stopwatch = timeout == Timeout.InfiniteTimeSpan ? null : Stopwatch.StartNew();
        while (true)
        {
            lock (_gate)
            {
                ThrowIfFailed();
                if (_clientCount != 0)
                {
                    if (createNew)
                        throw new IOException($"The managed database '{_databasePath}' already exists.");

                    _clientCount++;
                    return new SqliteManagedFileOwnershipClient(this, readOnly);
                }

                if (!_acquiringClient)
                {
                    _databasePath = databasePath;
                    _acquiringClient = true;
                    break;
                }
                if (createNew)
                    throw new IOException($"The managed database '{_databasePath}' is already being opened.");

                var remaining = RemainingTimeout(timeout, stopwatch);
                if (remaining == TimeSpan.Zero)
                    throw CreateOwnershipException(timeout);
                Monitor.Wait(_gate, remaining);
            }
        }

        EnsurePlatformSupported();
        FileStream? ensureStream = null;
        try
        {
            if (createNew || !File.Exists(_databasePath))
            {
                ensureStream = new FileStream(
                    _databasePath,
                    createNew ? FileMode.CreateNew : FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 1,
                    FileOptions.None);
            }
            else if (!readOnly)
            {
                ensureStream = new FileStream(
                    _databasePath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 1,
                    FileOptions.None);
            }

            ensureStream?.Dispose();
            ensureStream = null;

            lock (_gate)
            {
                if (!_acquiringClient || _clientCount != 0)
                    throw new InvalidOperationException("Managed SQLite client acquisition state is inconsistent.");

                _clientCount = 1;
                _acquiringClient = false;
                Monitor.PulseAll(_gate);
                return new SqliteManagedFileOwnershipClient(this, readOnly);
            }
        }
        catch
        {
            ensureStream?.Dispose();
            CompleteClientAcquisitionFailed();
            throw;
        }
    }

    internal SqliteMainFileLockLease AcquireShared(
        bool readOnly,
        bool blocksExclusive,
        SqlitePagerLockOperation operation,
        TimeSpan timeout)
    {
        var stopwatch = timeout == Timeout.InfiniteTimeSpan ? null : Stopwatch.StartNew();
        lock (_gate)
        {
            ThrowIfFailed();
            while (true)
            {
                while (_writer?.State is SqliteMainFileLockState.Pending or SqliteMainFileLockState.Exclusive)
                {
                    var remaining = RemainingTimeout(timeout, stopwatch);
                    if (remaining == TimeSpan.Zero)
                        throw new SqlitePagerBusyException(operation, timeout);
                    Monitor.Wait(_gate, remaining);
                }
                if (_sharedCount != 0 || !_acquiringShared)
                    break;

                var acquisitionWait = RemainingTimeout(timeout, stopwatch);
                if (acquisitionWait == TimeSpan.Zero)
                    throw new SqlitePagerBusyException(operation, timeout);
                Monitor.Wait(_gate, acquisitionWait);
            }

            EnsureLockHandle(writable: false, timeout, stopwatch, operation);
            if (_sharedCount == 0)
            {
                _acquiringShared = true;
                try
                {
                    AcquireOsShared(timeout, stopwatch, operation);
                }
                finally
                {
                    _acquiringShared = false;
                    Monitor.PulseAll(_gate);
                }
            }

            _sharedCount++;
            if (blocksExclusive)
                _blockingSharedCount++;
            return new SqliteMainFileLockLease(this, readOnly, blocksExclusive);
        }
    }

    internal void AcquireReserved(SqliteMainFileLockLease lease, TimeSpan timeout)
    {
        var stopwatch = timeout == Timeout.InfiniteTimeSpan ? null : Stopwatch.StartNew();
        lock (_gate)
        {
            ValidateActiveLease(lease, SqliteMainFileLockState.Shared);
            if (lease.IsReadOnly)
                throw new InvalidOperationException("A read-only main-file lock cannot become RESERVED.");

            while (_writer is not null)
            {
                var remaining = RemainingTimeout(timeout, stopwatch);
                if (remaining == TimeSpan.Zero)
                    throw new SqlitePagerBusyException(SqlitePagerLockOperation.Writer, timeout);
                Monitor.Wait(_gate, remaining);
            }

            EnsureLockHandle(writable: true, timeout, stopwatch, SqlitePagerLockOperation.Writer);
            _writer = lease;
            try
            {
                AcquireRange(
                    ReservedByte,
                    length: 1,
                    SqliteWalByteRangeLockMode.Exclusive,
                    timeout,
                    stopwatch,
                    SqlitePagerLockOperation.Writer);
                lease.HasReserved = true;
                lease.State = SqliteMainFileLockState.Reserved;
            }
            catch
            {
                _writer = null;
                Monitor.PulseAll(_gate);
                throw;
            }
        }
    }

    internal void AcquireExclusive(
        SqliteMainFileLockLease lease,
        bool requireReserved,
        TimeSpan timeout)
    {
        var stopwatch = timeout == Timeout.InfiniteTimeSpan ? null : Stopwatch.StartNew();
        lock (_gate)
        {
            ThrowIfFailed();
            if (lease.State == SqliteMainFileLockState.Exclusive)
                return;
            if (requireReserved && !lease.HasReserved)
                throw new InvalidOperationException("A rollback-journal writer requires RESERVED before EXCLUSIVE.");
            if (lease.State is not SqliteMainFileLockState.Shared
                and not SqliteMainFileLockState.Reserved
                and not SqliteMainFileLockState.Pending)
            {
                throw new InvalidOperationException(
                    $"Cannot upgrade a {lease.State} SQLite main-file lock to EXCLUSIVE.");
            }

            var claimedRecoveryWriter = false;
            if (_writer is null)
            {
                _writer = lease;
                claimedRecoveryWriter = true;
            }
            else if (!ReferenceEquals(_writer, lease))
            {
                throw new InvalidOperationException("A different managed writer owns the SQLite main-file lock.");
            }

            EnsureLockHandle(writable: true, timeout, stopwatch, SqlitePagerLockOperation.Writer);
            if (lease.State != SqliteMainFileLockState.Pending)
            {
                try
                {
                    AcquireRange(
                        PendingByte,
                        length: 1,
                        SqliteWalByteRangeLockMode.Exclusive,
                        timeout,
                        stopwatch,
                        SqlitePagerLockOperation.Writer);
                }
                catch
                {
                    if (claimedRecoveryWriter)
                    {
                        _writer = null;
                        Monitor.PulseAll(_gate);
                    }
                    throw;
                }

                lease.State = SqliteMainFileLockState.Pending;
            }

            var writerSharedCount = lease.BlocksExclusive ? 1 : 0;
            while (_blockingSharedCount != writerSharedCount)
            {
                var remaining = RemainingTimeout(timeout, stopwatch);
                if (remaining == TimeSpan.Zero)
                    throw new SqlitePagerBusyException(SqlitePagerLockOperation.Writer, timeout);
                Monitor.Wait(_gate, remaining);
            }

            UpgradeSharedRangeToExclusive(timeout, stopwatch);
            lease.State = SqliteMainFileLockState.Exclusive;
        }
    }

    internal void DowngradeToShared(SqliteMainFileLockLease lease)
    {
        lock (_gate)
        {
            ThrowIfFailed();
            ValidateLeaseOwner(lease);
            if (!ReferenceEquals(_writer, lease))
            {
                if (lease.State == SqliteMainFileLockState.Shared)
                    return;
                throw new InvalidOperationException("The main-file lock lease is not the managed writer.");
            }

            try
            {
                DowngradeWriterToShared(lease);
            }
            catch (IOException exception)
            {
                _failure = exception;
                throw;
            }
        }
    }

    internal bool IsReservedByAnotherProcess(SqliteMainFileLockLease lease)
    {
        lock (_gate)
        {
            ThrowIfFailed();
            ValidateLeaseOwner(lease);
            if (_writer is not null)
                return !ReferenceEquals(_writer, lease);

            return GetLockHandle().HasConflictingExclusiveLock(ReservedByte, length: 1);
        }
    }

    internal void Release(SqliteMainFileLockLease lease)
    {
        lock (_gate)
        {
            ValidateLeaseOwner(lease);
            try
            {
                if (ReferenceEquals(_writer, lease))
                    DowngradeWriterToShared(lease);
                else if (lease.State != SqliteMainFileLockState.Shared)
                    throw new InvalidOperationException($"Cannot release a {lease.State} SQLite main-file lock.");

                if (_sharedCount == 0)
                    throw new InvalidOperationException("SQLite main-file SHARED lock count underflow.");

                _sharedCount--;
                if (lease.BlocksExclusive)
                    _blockingSharedCount--;
                lease.State = SqliteMainFileLockState.Disposed;
                if (_sharedCount == 0)
                {
                    GetLockHandle().Unlock(SharedFirstByte, SharedSize);
                    CloseDeferredHandles();
                }
                Monitor.PulseAll(_gate);
            }
            catch (IOException exception)
            {
                _failure = exception;
                throw;
            }
        }
    }

    internal bool TryDeferPagerHandleClose(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsMacOS())
            return false;

        lock (_gate)
        {
            if (_sharedCount == 0)
                return false;

            _deferredPagerHandles.Add(handle);
            return true;
        }
    }

    internal void MarkAsPersistentWalShared(SqliteMainFileLockLease lease)
    {
        lock (_gate)
        {
            ValidateActiveLease(lease, SqliteMainFileLockState.Shared);
            if (!lease.BlocksExclusive)
                return;

            lease.BlocksExclusive = false;
            _blockingSharedCount--;
            Monitor.PulseAll(_gate);
        }
    }

    internal void ReleaseClient()
    {
        lock (_gate)
        {
            if (_clientCount == 0)
                throw new InvalidOperationException("Managed SQLite client reference count underflow.");

            _clientCount--;
            if (_clientCount != 0)
                return;
            if (_sharedCount != 0 || _writer is not null)
            {
                throw new InvalidOperationException(
                    "The final managed SQLite client was released while main-file locks remain active.");
            }

            CloseDeferredHandles();
            _lockHandle?.Dispose();
            _lockHandle = null;
            _lockHandleWritable = false;
        }
    }

    private void AcquireOsShared(
        TimeSpan timeout,
        Stopwatch? stopwatch,
        SqlitePagerLockOperation operation)
    {
        AcquireRange(
            PendingByte,
            length: 1,
            SqliteWalByteRangeLockMode.Shared,
            timeout,
            stopwatch,
            operation);
        try
        {
            AcquireRange(
                SharedFirstByte,
                SharedSize,
                SqliteWalByteRangeLockMode.Shared,
                timeout,
                stopwatch,
                operation);
        }
        finally
        {
            GetLockHandle().Unlock(PendingByte, length: 1);
        }
    }

    private void UpgradeSharedRangeToExclusive(TimeSpan timeout, Stopwatch? stopwatch)
    {
        var handle = GetLockHandle();
        IOException? contention;
        if (!OperatingSystem.IsWindows())
        {
            while (!handle.TryLock(
                       SharedFirstByte,
                       SharedSize,
                       SqliteWalByteRangeLockMode.Exclusive,
                       out contention))
            {
                if (!WaitForRetryWithGateReleased(timeout, stopwatch))
                {
                    throw new SqlitePagerBusyException(
                        SqlitePagerLockOperation.Writer,
                        timeout,
                        contention);
                }
            }
            return;
        }

        while (true)
        {
            handle.Unlock(SharedFirstByte, SharedSize);
            if (handle.TryLock(
                    SharedFirstByte,
                    SharedSize,
                    SqliteWalByteRangeLockMode.Exclusive,
                    out contention))
            {
                return;
            }

            if (!handle.TryLock(
                    SharedFirstByte,
                    SharedSize,
                    SqliteWalByteRangeLockMode.Shared,
                    out var reacquireFailure))
            {
                throw new IOException(
                    "SQLite main-file SHARED lock could not be restored after a failed EXCLUSIVE upgrade.",
                    reacquireFailure);
            }

            if (!WaitForRetryWithGateReleased(timeout, stopwatch))
            {
                throw new SqlitePagerBusyException(
                    SqlitePagerLockOperation.Writer,
                    timeout,
                    contention);
            }
        }
    }

    private void DowngradeWriterToShared(SqliteMainFileLockLease lease)
    {
        var handle = GetLockHandle();
        if (lease.State == SqliteMainFileLockState.Exclusive)
        {
            if (OperatingSystem.IsWindows())
            {
                handle.Unlock(SharedFirstByte, SharedSize);
                if (!handle.TryLock(
                        SharedFirstByte,
                        SharedSize,
                        SqliteWalByteRangeLockMode.Shared,
                        out var contention))
                {
                    throw new IOException(
                        "SQLite main-file EXCLUSIVE lock could not be downgraded to SHARED.",
                        contention);
                }
            }
            else if (!handle.TryLock(
                         SharedFirstByte,
                         SharedSize,
                         SqliteWalByteRangeLockMode.Shared,
                         out var contention))
            {
                throw new IOException(
                    "SQLite main-file EXCLUSIVE lock could not be downgraded to SHARED.",
                    contention);
            }
        }

        if (lease.HasReserved)
        {
            handle.Unlock(ReservedByte, length: 1);
            lease.HasReserved = false;
        }
        if (lease.State is SqliteMainFileLockState.Pending or SqliteMainFileLockState.Exclusive)
            handle.Unlock(PendingByte, length: 1);

        lease.State = SqliteMainFileLockState.Shared;
        _writer = null;
        Monitor.PulseAll(_gate);
    }

    private void AcquireRange(
        long offset,
        long length,
        SqliteWalByteRangeLockMode mode,
        TimeSpan timeout,
        Stopwatch? stopwatch,
        SqlitePagerLockOperation operation)
    {
        IOException? contention;
        while (!GetLockHandle().TryLock(offset, length, mode, out contention))
        {
            if (!WaitForRetryWithGateReleased(timeout, stopwatch))
                throw new SqlitePagerBusyException(operation, timeout, contention);
        }
    }

    private void EnsureLockHandle(
        bool writable,
        TimeSpan timeout,
        Stopwatch? stopwatch,
        SqlitePagerLockOperation operation)
    {
        if (_lockHandle is null)
        {
            try
            {
                _lockHandle = new SqliteByteRangeLockHandle(_databasePath, writable: true);
                _lockHandleWritable = true;
            }
            catch (UnauthorizedAccessException) when (!writable)
            {
                _lockHandle = new SqliteByteRangeLockHandle(_databasePath, writable: false);
                _lockHandleWritable = false;
            }
            return;
        }
        if (!writable || _lockHandleWritable)
            return;

        var replacement = new SqliteByteRangeLockHandle(_databasePath, writable: true);
        try
        {
            if (_sharedCount != 0)
            {
                IOException? contention;
                while (!replacement.TryLock(
                           SharedFirstByte,
                           SharedSize,
                           SqliteWalByteRangeLockMode.Shared,
                           out contention))
                {
                    if (!WaitForRetryWithGateReleased(timeout, stopwatch))
                        throw new SqlitePagerBusyException(operation, timeout, contention);
                }
            }

            var previous = _lockHandle;
            _lockHandle = replacement;
            _lockHandleWritable = true;
            replacement = null!;
            if (OperatingSystem.IsMacOS() && _sharedCount != 0)
            {
                _deferredLockHandles.Add(previous);
            }
            else
            {
                if (_sharedCount != 0)
                    previous.Unlock(SharedFirstByte, SharedSize);
                previous.Dispose();
            }
        }
        finally
        {
            replacement?.Dispose();
        }
    }

    private SqliteByteRangeLockHandle GetLockHandle()
        => _lockHandle
           ?? throw new InvalidOperationException("SQLite main-file lock handle is not open.");

    private void CloseDeferredHandles()
    {
        foreach (var handle in _deferredPagerHandles)
            handle.Dispose();
        _deferredPagerHandles.Clear();
        foreach (var handle in _deferredLockHandles)
            handle.Dispose();
        _deferredLockHandles.Clear();
    }

    private bool WaitForRetryWithGateReleased(TimeSpan timeout, Stopwatch? stopwatch)
    {
        Monitor.Exit(_gate);
        try
        {
            return SqliteBusyBackoff.Wait(timeout, stopwatch);
        }
        finally
        {
            Monitor.Enter(_gate);
        }
    }

    private void CompleteClientAcquisitionFailed()
    {
        lock (_gate)
        {
            if (!_acquiringClient)
                return;
            _acquiringClient = false;
            Monitor.PulseAll(_gate);
        }
    }

    private void ValidateActiveLease(
        SqliteMainFileLockLease lease,
        SqliteMainFileLockState requiredState)
    {
        ValidateLeaseOwner(lease);
        if (lease.State != requiredState)
        {
            throw new InvalidOperationException(
                $"SQLite main-file lock must be {requiredState}, not {lease.State}.");
        }
    }

    private static void ValidateLeaseOwner(SqliteMainFileLockLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.State == SqliteMainFileLockState.Disposed)
            throw new ObjectDisposedException(nameof(SqliteMainFileLockLease));
    }

    private void ThrowIfFailed()
    {
        if (_failure is not null)
        {
            throw new InvalidOperationException(
                "Managed SQLite main-file lock release failed; refusing later lock operations.",
                _failure);
        }
    }

    private static void EnsurePlatformSupported()
    {
        if (OperatingSystem.IsWindows()
            || (OperatingSystem.IsLinux() && Environment.Is64BitProcess)
            || OperatingSystem.IsMacOS())
        {
            return;
        }

        throw new PlatformNotSupportedException(
            "Managed physical databases require SQLite main-file byte-range locks, "
            + "which are supported here only on Windows, 64-bit Linux, and macOS.");
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

    private SqlitePagerClientOwnershipException CreateOwnershipException(
        TimeSpan timeout,
        Exception? innerException = null)
        => new(
            _databasePath,
            timeout,
            innerException ?? new TimeoutException("Another local caller is opening the managed SQLite database."));
}

internal sealed class SqliteManagedFileOwnershipClient : IDisposable
{
    private SqliteManagedFileOwnership? _owner;

    internal SqliteManagedFileOwnershipClient(SqliteManagedFileOwnership owner, bool readOnly)
    {
        _owner = owner;
        IsReadOnly = readOnly;
    }

    internal bool IsReadOnly { get; }

    internal SqliteMainFileLockLease AcquireShared(
        SqlitePagerLockOperation operation,
        TimeSpan timeout,
        bool blocksExclusive = true)
        => GetOwner().AcquireShared(IsReadOnly, blocksExclusive, operation, timeout);

    public void Dispose()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        owner?.ReleaseClient();
    }

    private SqliteManagedFileOwnership GetOwner()
        => Volatile.Read(ref _owner)
           ?? throw new ObjectDisposedException(nameof(SqliteManagedFileOwnershipClient));
}

internal sealed class SqliteMainFileLockLease : IDisposable
{
    private SqliteManagedFileOwnership? _owner;

    internal SqliteMainFileLockLease(
        SqliteManagedFileOwnership owner,
        bool readOnly,
        bool blocksExclusive)
    {
        _owner = owner;
        IsReadOnly = readOnly;
        BlocksExclusive = blocksExclusive;
        State = SqliteMainFileLockState.Shared;
    }

    internal bool IsReadOnly { get; }

    internal bool HasReserved { get; set; }

    internal bool BlocksExclusive { get; set; }

    internal SqliteMainFileLockState State { get; set; }

    internal void AcquireReserved(TimeSpan timeout) => GetOwner().AcquireReserved(this, timeout);

    internal void AcquireExclusive(bool requireReserved, TimeSpan timeout)
        => GetOwner().AcquireExclusive(this, requireReserved, timeout);

    internal void DowngradeToShared() => GetOwner().DowngradeToShared(this);

    internal bool IsReservedByAnotherProcess() => GetOwner().IsReservedByAnotherProcess(this);

    internal void MarkAsPersistentWalShared() => GetOwner().MarkAsPersistentWalShared(this);

    public void Dispose()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        owner?.Release(this);
    }

    private SqliteManagedFileOwnership GetOwner()
        => Volatile.Read(ref _owner)
           ?? throw new ObjectDisposedException(nameof(SqliteMainFileLockLease));
}

internal static class SqliteManagedFileOwnershipRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<RegistryKey, SqliteManagedFileOwnership> Owners = [];

    internal static SqliteManagedFileOwnershipClient? Acquire(
        IFileSystem fileSystem,
        string databasePath,
        bool createNew,
        bool readOnly,
        TimeSpan timeout)
    {
        fileSystem = AhtolaEncryptionFileSystem.Unwrap(fileSystem);
        if (fileSystem is not PhysicalFileSystem)
            return null;

        var canonicalPath = CanonicalizeDatabasePath(databasePath);
        var pathKey = RegistryKey.ForPath(CreatePathKey(canonicalPath));
        SqliteManagedFileOwnership owner;
        lock (Gate)
        {
            if (!Owners.TryGetValue(pathKey, out owner!))
            {
                var key = File.Exists(canonicalPath)
                    ? RegistryKey.ForIdentity(SqliteWalSharedMemoryCarrierIdentity.FromPath(canonicalPath))
                    : pathKey;
                if (!Owners.TryGetValue(key, out owner!))
                {
                    owner = new SqliteManagedFileOwnership(canonicalPath);
                    Owners.Add(key, owner);
                }
            }
        }

        var client = owner.AcquireClient(canonicalPath, createNew, readOnly, timeout);
        try
        {
            var identityKey = RegistryKey.ForIdentity(
                SqliteWalSharedMemoryCarrierIdentity.FromPath(canonicalPath));
            lock (Gate)
            {
                if (Owners.TryGetValue(identityKey, out var existing)
                    && !ReferenceEquals(existing, owner))
                {
                    throw new InvalidOperationException(
                        "The SQLite database was opened concurrently through two aliases before its file identity was registered.");
                }

                Owners[identityKey] = owner;
                if (Owners.TryGetValue(pathKey, out var pathOwner)
                    && ReferenceEquals(pathOwner, owner))
                {
                    Owners.Remove(pathKey);
                }
            }

            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    internal static bool TryDeferPagerHandleClose(string path, SafeFileHandle handle)
    {
        if (!OperatingSystem.IsMacOS())
            return false;

        SqliteManagedFileOwnership? owner;
        lock (Gate)
        {
            var identityKey = RegistryKey.ForIdentity(
                SqliteWalSharedMemoryCarrierIdentity.FromHandle(handle));
            if (!Owners.TryGetValue(identityKey, out owner))
                Owners.TryGetValue(RegistryKey.ForPath(CreatePathKey(path)), out owner);
        }
        return owner?.TryDeferPagerHandleClose(handle) == true;
    }

    private static string CreatePathKey(string databasePath)
    {
        var path = CanonicalizeDatabasePath(databasePath);
        return OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
    }

    private static string CanonicalizeDatabasePath(string databasePath)
    {
        var path = Path.GetFullPath(databasePath);
        try
        {
            if (File.Exists(path))
            {
                var target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null)
                    path = target.FullName;
            }
        }
        catch
        {
            // Fall back to the unresolved full path when the host cannot follow links.
        }

        return path;
    }

    private readonly record struct RegistryKey(
        SqliteWalSharedMemoryCarrierIdentity? Identity,
        string? Path)
    {
        internal static RegistryKey ForIdentity(SqliteWalSharedMemoryCarrierIdentity identity)
            => new(identity, Path: null);

        internal static RegistryKey ForPath(string path)
            => new(Identity: null, path);
    }
}
