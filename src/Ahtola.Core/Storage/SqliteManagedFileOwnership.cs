using System.Diagnostics;

namespace Ahtola.Core.Storage;

/// <summary>
/// Raised when a physical managed database cannot obtain its required main-file
/// SQLite SHARED lock (Stage 6).
/// </summary>
public sealed class SqlitePagerClientOwnershipException : InvalidOperationException
{
    internal SqlitePagerClientOwnershipException(
        string databasePath,
        TimeSpan timeout,
        Exception innerException)
        : base(
            $"Managed main-file SHARED lock for database '{databasePath}' could not be acquired within {timeout}. "
            + "Another client likely holds PENDING/RESERVED/EXCLUSIVE on the main database file.",
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

/// <summary>
/// Stage 6 main-file lock broker. Every physical pager in this process shares one
/// SQLite SHARED lock on a stable byte in the 510-byte shared range. This coexists
/// with ordinary SQLite/Turso SHARED readers; exclusive PENDING/RESERVED/EXCLUSIVE
/// holders still block open. WAL write exclusion remains on <c>-shm</c> (Stages 1–5).
/// </summary>
internal sealed class SqliteManagedFileOwnership
{
    private const long PendingByte = 0x4000_0000;
    private const long SharedFirstByte = PendingByte + 2;
    private const long SharedSize = 510;

    private readonly object _gate = new();
    private readonly string _databasePath;
    private readonly long _sharedLockOffset;
    private SqliteWalByteRangeLock? _locks;
    private SqliteWalByteRangeLockLease? _sharedLease;
    private int _referenceCount;
    private bool _acquiring;
    private Exception? _failure;

    internal SqliteManagedFileOwnership(string databasePath)
    {
        _databasePath = databasePath;
        // Stable FNV-1a slot inside SQLite's SHARED range (not randomized hash codes).
        uint hash = 2166136261;
        foreach (var ch in databasePath)
            hash = (hash ^ ch) * 16777619;
        _sharedLockOffset = SharedFirstByte + (hash % SharedSize);
    }

    internal IDisposable Acquire(bool createNew, bool readOnly, TimeSpan timeout)
    {
        var stopwatch = timeout == Timeout.InfiniteTimeSpan ? null : Stopwatch.StartNew();
        while (true)
        {
            lock (_gate)
            {
                ThrowIfFailed();
                if (_referenceCount != 0)
                {
                    if (createNew)
                        throw new IOException($"The managed database '{_databasePath}' already exists.");

                    _referenceCount++;
                    return new Lease(this);
                }

                if (!_acquiring)
                {
                    _acquiring = true;
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

        if (!OperatingSystem.IsWindows()
            && !OperatingSystem.IsLinux()
            && !OperatingSystem.IsMacOS())
        {
            CompleteAcquisitionFailed();
            throw new PlatformNotSupportedException(
                "Managed physical databases require SQLite main-file byte-range locks, "
                + "which are supported here only on Windows, Linux, and macOS.");
        }

        FileStream? ensureStream = null;
        try
        {
            // Ensure the main file exists before locking it.
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

            var locks = new SqliteWalByteRangeLock(_databasePath);
            SqliteWalByteRangeLockLease sharedLease;
            try
            {
                sharedLease = locks.AcquireShared(
                    _sharedLockOffset,
                    length: 1,
                    RemainingTimeout(timeout, stopwatch));
            }
            catch (SqliteWalByteRangeLockBusyException exception)
            {
                throw CreateOwnershipException(timeout, exception);
            }

            lock (_gate)
            {
                if (!_acquiring || _referenceCount != 0 || _sharedLease is not null)
                    throw new InvalidOperationException("Managed SQLite client ownership acquisition state is inconsistent.");

                _locks = locks;
                _sharedLease = sharedLease;
                _referenceCount = 1;
                _acquiring = false;
                Monitor.PulseAll(_gate);
                return new Lease(this);
            }
        }
        catch
        {
            ensureStream?.Dispose();
            CompleteAcquisitionFailed();
            throw;
        }
    }

    private void CompleteAcquisitionFailed()
    {
        lock (_gate)
        {
            if (!_acquiring)
                return;
            _acquiring = false;
            Monitor.PulseAll(_gate);
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            if (_referenceCount == 0)
                throw new InvalidOperationException("Managed SQLite client ownership reference count underflow.");

            _referenceCount--;
            if (_referenceCount != 0)
                return;

            var sharedLease = _sharedLease
                ?? throw new InvalidOperationException("Managed SQLite client ownership shared lease is missing.");
            _sharedLease = null;
            _locks = null;
            try
            {
                sharedLease.Dispose();
            }
            catch (IOException exception)
            {
                _failure = exception;
                throw;
            }
        }
    }

    private void ThrowIfFailed()
    {
        if (_failure is not null)
        {
            throw new InvalidOperationException(
                "Managed SQLite client ownership release failed; refusing later database opens.",
                _failure);
        }
    }

    private static TimeSpan RemainingTimeout(TimeSpan timeout, Stopwatch? stopwatch)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
            return Timeout.InfiniteTimeSpan;

        var remaining = timeout - stopwatch!.Elapsed;
        if (remaining <= TimeSpan.Zero)
            return TimeSpan.Zero;

        return remaining > TimeSpan.FromMilliseconds(int.MaxValue)
            ? TimeSpan.FromMilliseconds(int.MaxValue)
            : remaining;
    }

    private SqlitePagerClientOwnershipException CreateOwnershipException(
        TimeSpan timeout,
        Exception? innerException = null)
        => new(
            _databasePath,
            timeout,
            innerException ?? new TimeoutException("Another local caller is acquiring managed SQLite ownership."));

    private sealed class Lease(SqliteManagedFileOwnership owner) : IDisposable
    {
        private SqliteManagedFileOwnership? _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release();
        }
    }
}

internal static class SqliteManagedFileOwnershipRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, SqliteManagedFileOwnership> Owners = new(StringComparer.Ordinal);

    internal static IDisposable? Acquire(
        IFileSystem fileSystem,
        string databasePath,
        bool createNew,
        bool readOnly,
        TimeSpan timeout)
    {
        fileSystem = AhtolaEncryptionFileSystem.Unwrap(fileSystem);
        if (fileSystem is not PhysicalFileSystem)
            return null;

        var path = CanonicalizeDatabasePath(databasePath);
        var key = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        SqliteManagedFileOwnership owner;
        lock (Gate)
        {
            if (!Owners.TryGetValue(key, out owner!))
            {
                owner = new SqliteManagedFileOwnership(path);
                Owners.Add(key, owner);
            }
        }

        return owner.Acquire(createNew, readOnly, timeout);
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
}
