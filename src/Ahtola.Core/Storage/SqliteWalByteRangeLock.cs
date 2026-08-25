using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ahtola.Core.Storage;

/// <summary>The access mode requested for a SQLite WAL byte-range lock.</summary>
public enum SqliteWalByteRangeLockMode
{
    /// <summary>Allows other holders to acquire the same range in shared mode.</summary>
    Shared,

    /// <summary>Excludes every shared or exclusive holder from the requested range.</summary>
    Exclusive,
}

/// <summary>
/// Raised when a SQLite WAL byte-range lock remains unavailable for its requested
/// acquisition timeout.
/// </summary>
public sealed class SqliteWalByteRangeLockBusyException : InvalidOperationException
{
    internal SqliteWalByteRangeLockBusyException(
        string lockFilePath,
        long offset,
        long length,
        SqliteWalByteRangeLockMode mode,
        TimeSpan timeout,
        Exception? innerException)
        : base(
            $"SQLite WAL {mode.ToString().ToLowerInvariant()} byte-range lock [{offset}, {offset + length}) "
            + $"in '{lockFilePath}' could not be acquired within {timeout}.",
            innerException)
    {
        LockFilePath = lockFilePath;
        Offset = offset;
        Length = length;
        Mode = mode;
        Timeout = timeout;
    }

    /// <summary>The canonical path of the lock carrier file.</summary>
    public string LockFilePath { get; }

    /// <summary>The first byte of the unavailable range.</summary>
    public long Offset { get; }

    /// <summary>The nonzero number of bytes in the unavailable range.</summary>
    public long Length { get; }

    /// <summary>The lock mode that could not be acquired.</summary>
    public SqliteWalByteRangeLockMode Mode { get; }

    /// <summary>The requested busy timeout.</summary>
    public TimeSpan Timeout { get; }
}

/// <summary>
/// Acquires detached SQLite WAL byte-range locks over an existing physical file.
/// </summary>
/// <remarks>
/// This primitive does not create, map, or interpret a <c>-shm</c> file, and it
/// does not implement any pager, WAL-index, read-mark, writer, or checkpoint
/// protocol. Each returned lease owns a dedicated file descriptor until disposal,
/// so its operating-system lock lifetime cannot be shortened by an unrelated
/// lease. Windows uses <c>LockFileEx</c>; 64-bit Linux uses OFD locks so closing
/// an unrelated descriptor cannot release a lease; macOS uses POSIX
/// <c>fcntl(F_SETLK)</c> (process-associated, not OFD — same class of lock SQLite
/// uses on Darwin).
/// </remarks>
public sealed partial class SqliteWalByteRangeLock
{
    private const uint LockFileFailImmediately = 0x0000_0001;
    private const uint LockFileExclusiveLock = 0x0000_0002;
    private const int LinuxOfdGetLock = 36;
    private const int LinuxOfdSetLock = 37;
    private const short LinuxReadLock = 0;
    private const short LinuxWriteLock = 1;
    private const short LinuxUnlock = 2;
    private const short LinuxSeekSet = 0;
    private const int LinuxAccessDenied = 13;
    private const int LinuxResourceTemporarilyUnavailable = 11;
    private const int LinuxInvalidArgument = 22;
    // Darwin sys/fcntl.h: F_SETLK=8, F_RDLCK=1, F_UNLCK=2, F_WRLCK=3.
    private const int MacGetLock = 7;
    private const int MacSetLock = 8;
    private const short MacReadLock = 1;
    private const short MacWriteLock = 3;
    private const short MacUnlock = 2;
    private const short MacSeekSet = 0;
    private const int MacAccessDenied = 13;
    private const int MacResourceTemporarilyUnavailable = 35; // EAGAIN
    private const int WindowsLockViolation = 33;

    /// <summary>
    /// Creates a lock primitive for an existing file that will carry the requested SQLite WAL locks.
    /// </summary>
    /// <param name="lockFilePath">The existing physical file used only for locking.</param>
    /// <exception cref="PlatformNotSupportedException">
    /// The current platform cannot provide the required lock semantics.
    /// </exception>
    public SqliteWalByteRangeLock(string lockFilePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(lockFilePath);
        EnsurePlatformSupported();
        LockFilePath = Path.GetFullPath(lockFilePath);
    }

    /// <summary>The canonical path of the physical lock carrier file.</summary>
    public string LockFilePath { get; }

    /// <summary>
    /// Attempts to acquire a shared lock without waiting for an unavailable range.
    /// </summary>
    /// <returns><see langword="true"/> with a lease when acquired; otherwise <see langword="false"/>.</returns>
    public bool TryAcquireShared(long offset, long length, out SqliteWalByteRangeLockLease? lease)
        => TryAcquire(offset, length, SqliteWalByteRangeLockMode.Shared, out lease);

    /// <summary>
    /// Attempts to acquire an exclusive lock without waiting for an unavailable range.
    /// </summary>
    /// <returns><see langword="true"/> with a lease when acquired; otherwise <see langword="false"/>.</returns>
    public bool TryAcquireExclusive(long offset, long length, out SqliteWalByteRangeLockLease? lease)
        => TryAcquire(offset, length, SqliteWalByteRangeLockMode.Exclusive, out lease);

    /// <summary>
    /// Attempts to acquire a lock without waiting for an unavailable range.
    /// </summary>
    /// <returns><see langword="true"/> with a lease when acquired; otherwise <see langword="false"/>.</returns>
    public bool TryAcquire(
        long offset,
        long length,
        SqliteWalByteRangeLockMode mode,
        out SqliteWalByteRangeLockLease? lease)
    {
        ValidateRange(offset, length);
        ValidateMode(mode);
        return TryAcquireCore(offset, length, mode, carrier: null, out lease, out _);
    }

    /// <summary>
    /// Acquires a shared lock, retrying until the requested timeout expires.
    /// </summary>
    public SqliteWalByteRangeLockLease AcquireShared(long offset, long length, TimeSpan timeout)
        => Acquire(offset, length, SqliteWalByteRangeLockMode.Shared, timeout);

    /// <summary>
    /// Acquires a shared lock, retrying until it is available, the requested
    /// timeout expires, or cancellation is requested.
    /// </summary>
    public SqliteWalByteRangeLockLease AcquireShared(
        long offset,
        long length,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => Acquire(offset, length, SqliteWalByteRangeLockMode.Shared, timeout, cancellationToken);

    /// <summary>
    /// Acquires an exclusive lock, retrying until the requested timeout expires.
    /// </summary>
    public SqliteWalByteRangeLockLease AcquireExclusive(long offset, long length, TimeSpan timeout)
        => Acquire(offset, length, SqliteWalByteRangeLockMode.Exclusive, timeout);

    /// <summary>
    /// Acquires an exclusive lock, retrying until it is available, the requested
    /// timeout expires, or cancellation is requested.
    /// </summary>
    public SqliteWalByteRangeLockLease AcquireExclusive(
        long offset,
        long length,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => Acquire(offset, length, SqliteWalByteRangeLockMode.Exclusive, timeout, cancellationToken);

    /// <summary>
    /// Acquires a lock, retrying until the requested timeout expires.
    /// </summary>
    /// <exception cref="SqliteWalByteRangeLockBusyException">
    /// The requested range remained unavailable for <paramref name="timeout"/>.
    /// </exception>
    public SqliteWalByteRangeLockLease Acquire(
        long offset,
        long length,
        SqliteWalByteRangeLockMode mode,
        TimeSpan timeout)
        => AcquireCore(offset, length, mode, timeout, CancellationToken.None, carrier: null);

    /// <summary>
    /// Acquires a lock, retrying until it is available, the requested timeout
    /// expires, or cancellation is requested.
    /// </summary>
    /// <exception cref="SqliteWalByteRangeLockBusyException">
    /// The requested range remained unavailable for <paramref name="timeout"/>.
    /// </exception>
    public SqliteWalByteRangeLockLease Acquire(
        long offset,
        long length,
        SqliteWalByteRangeLockMode mode,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => AcquireCore(offset, length, mode, timeout, cancellationToken, carrier: null);

    internal bool TryAcquireExclusive(
        ISqliteWalSharedMemoryLockCarrier carrier,
        long offset,
        long length,
        out SqliteWalByteRangeLockLease? lease)
    {
        ArgumentNullException.ThrowIfNull(carrier);
        ValidateRange(offset, length);
        return TryAcquireCore(
            offset,
            length,
            SqliteWalByteRangeLockMode.Exclusive,
            carrier,
            out lease,
            out _);
    }

    internal SqliteWalByteRangeLockLease AcquireExclusive(
        ISqliteWalSharedMemoryLockCarrier carrier,
        long offset,
        long length,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(carrier);
        return AcquireCore(
            offset,
            length,
            SqliteWalByteRangeLockMode.Exclusive,
            timeout,
            cancellationToken,
            carrier);
    }

    private SqliteWalByteRangeLockLease AcquireCore(
        long offset,
        long length,
        SqliteWalByteRangeLockMode mode,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        ISqliteWalSharedMemoryLockCarrier? carrier)
    {
        ValidateRange(offset, length);
        ValidateMode(mode);
        ValidateTimeout(timeout);
        cancellationToken.ThrowIfCancellationRequested();

        var stopwatch = timeout == Timeout.InfiniteTimeSpan ? null : Stopwatch.StartNew();
        IOException? contention = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryAcquireCore(offset, length, mode, carrier, out var lease, out contention))
            {
                return lease
                    ?? throw new InvalidOperationException(
                        "SQLite WAL byte-range locking reported success without returning a lease.");
            }

            if (!WaitForRetry(timeout, stopwatch, cancellationToken))
            {
                throw new SqliteWalByteRangeLockBusyException(
                    LockFilePath,
                    offset,
                    length,
                    mode,
                    timeout,
                    contention);
            }
        }
    }

    private bool TryAcquireCore(
        long offset,
        long length,
        SqliteWalByteRangeLockMode mode,
        ISqliteWalSharedMemoryLockCarrier? carrier,
        out SqliteWalByteRangeLockLease? lease,
        out IOException? contention)
    {
        var handle = carrier is null
            ? OpenLeaseHandle(mode)
            : carrier.DuplicateLockCarrierHandle();
        try
        {
            if (!TryLock(handle, offset, length, mode, out contention))
            {
                handle.Dispose();
                lease = null;
                return false;
            }

            try
            {
                lease = new SqliteWalByteRangeLockLease(handle, offset, length, mode);
                handle = null!;
                return true;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private SafeFileHandle OpenLeaseHandle(SqliteWalByteRangeLockMode mode)
        => File.OpenHandle(
            LockFilePath,
            FileMode.Open,
            mode == SqliteWalByteRangeLockMode.Shared ? FileAccess.Read : FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.None);

    internal static bool TryLock(
        SafeFileHandle handle,
        long offset,
        long length,
        SqliteWalByteRangeLockMode mode,
        out IOException? contention)
    {
        if (OperatingSystem.IsWindows())
        {
            var flags = LockFileFailImmediately;
            if (mode == SqliteWalByteRangeLockMode.Exclusive)
                flags |= LockFileExclusiveLock;

            var overlapped = CreateWindowsOverlapped(offset);
            GetWindowsLengthParts(length, out var lowLength, out var highLength);
            if (Native.LockFileEx(handle, flags, reserved: 0, lowLength, highLength, ref overlapped))
            {
                contention = null;
                return true;
            }

            var error = Marshal.GetLastPInvokeError();
            if (error == WindowsLockViolation)
            {
                contention = CreateContentionException("LockFileEx", error);
                return false;
            }

            ThrowNativeIOException("LockFileEx", error);
        }

        if (OperatingSystem.IsLinux() && Environment.Is64BitProcess)
        {
            var fileLock = new LinuxFileLock(
                mode == SqliteWalByteRangeLockMode.Shared ? LinuxReadLock : LinuxWriteLock,
                LinuxSeekSet,
                offset,
                length);
            if (Native.FcntlLinux(handle, LinuxOfdSetLock, ref fileLock) == 0)
            {
                contention = null;
                return true;
            }

            var error = Marshal.GetLastPInvokeError();
            if (error is LinuxAccessDenied or LinuxResourceTemporarilyUnavailable)
            {
                contention = CreateContentionException("fcntl(F_OFD_SETLK)", error);
                return false;
            }
            if (error == LinuxInvalidArgument)
            {
                throw new PlatformNotSupportedException(
                    "The current Linux kernel does not support the required F_OFD_SETLK SQLite WAL locks.",
                    new Win32Exception(error));
            }

            ThrowNativeIOException("fcntl(F_OFD_SETLK)", error);
        }

        if (OperatingSystem.IsMacOS())
        {
            var fileLock = new MacFileLock(
                mode == SqliteWalByteRangeLockMode.Shared ? MacReadLock : MacWriteLock,
                MacSeekSet,
                offset,
                length);
            if (Native.FcntlMac(handle, MacSetLock, ref fileLock) == 0)
            {
                contention = null;
                return true;
            }

            var error = Marshal.GetLastPInvokeError();
            if (error is MacAccessDenied or MacResourceTemporarilyUnavailable)
            {
                contention = CreateContentionException("fcntl(F_SETLK)", error);
                return false;
            }

            ThrowNativeIOException("fcntl(F_SETLK)", error);
        }

        EnsurePlatformSupported();
        throw new InvalidOperationException("The SQLite WAL byte-range lock platform selection is inconsistent.");
    }

    internal static void Unlock(
        SafeFileHandle handle,
        long offset,
        long length)
    {
        if (OperatingSystem.IsWindows())
        {
            var overlapped = CreateWindowsOverlapped(offset);
            GetWindowsLengthParts(length, out var lowLength, out var highLength);
            if (!Native.UnlockFileEx(handle, reserved: 0, lowLength, highLength, ref overlapped))
                ThrowNativeIOException("UnlockFileEx", Marshal.GetLastPInvokeError());
            return;
        }

        if (OperatingSystem.IsLinux() && Environment.Is64BitProcess)
        {
            var fileLock = new LinuxFileLock(LinuxUnlock, LinuxSeekSet, offset, length);
            if (Native.FcntlLinux(handle, LinuxOfdSetLock, ref fileLock) != 0)
                ThrowNativeIOException("fcntl(F_OFD_SETLK unlock)", Marshal.GetLastPInvokeError());
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            var fileLock = new MacFileLock(MacUnlock, MacSeekSet, offset, length);
            if (Native.FcntlMac(handle, MacSetLock, ref fileLock) != 0)
                ThrowNativeIOException("fcntl(F_SETLK unlock)", Marshal.GetLastPInvokeError());
            return;
        }

        EnsurePlatformSupported();
        throw new InvalidOperationException("The SQLite WAL byte-range lock platform selection is inconsistent.");
    }

    internal static bool HasConflictingExclusiveLock(
        SafeFileHandle handle,
        long offset,
        long length)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!TryLock(
                    handle,
                    offset,
                    length,
                    SqliteWalByteRangeLockMode.Exclusive,
                    out _))
            {
                return true;
            }

            Unlock(handle, offset, length);
            return false;
        }

        if (OperatingSystem.IsLinux() && Environment.Is64BitProcess)
        {
            var fileLock = new LinuxFileLock(LinuxWriteLock, LinuxSeekSet, offset, length);
            if (Native.FcntlLinux(handle, LinuxOfdGetLock, ref fileLock) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == LinuxInvalidArgument)
                {
                    throw new PlatformNotSupportedException(
                        "The current Linux kernel does not support the required F_OFD_GETLK SQLite locks.",
                        new Win32Exception(error));
                }

                ThrowNativeIOException("fcntl(F_OFD_GETLK)", error);
            }

            return fileLock.Type != LinuxUnlock;
        }

        if (OperatingSystem.IsMacOS())
        {
            var fileLock = new MacFileLock(MacWriteLock, MacSeekSet, offset, length);
            if (Native.FcntlMac(handle, MacGetLock, ref fileLock) != 0)
                ThrowNativeIOException("fcntl(F_GETLK)", Marshal.GetLastPInvokeError());
            return fileLock.Type != MacUnlock;
        }

        EnsurePlatformSupported();
        throw new InvalidOperationException("The SQLite WAL byte-range lock platform selection is inconsistent.");
    }

    private static void EnsurePlatformSupported()
    {
        if (OperatingSystem.IsWindows())
            return;
        if (OperatingSystem.IsLinux() && Environment.Is64BitProcess && Marshal.SizeOf<LinuxFileLock>() == 32)
            return;
        if (OperatingSystem.IsMacOS() && Marshal.SizeOf<MacFileLock>() == 24)
            return;

        throw new PlatformNotSupportedException(
            "SQLite WAL byte-range locks are supported only on Windows, 64-bit Linux (OFD), and macOS (POSIX F_SETLK).");
    }

    private static void ValidateRange(long offset, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length), "SQLite WAL lock lengths must be greater than zero.");
        if (offset > long.MaxValue - length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                "The SQLite WAL lock range extends beyond the supported signed 64-bit file offset.");
        }
    }

    private static void ValidateMode(SqliteWalByteRangeLockMode mode)
    {
        if (mode is not SqliteWalByteRangeLockMode.Shared and not SqliteWalByteRangeLockMode.Exclusive)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mode),
                mode,
                "SQLite WAL lock mode must be shared or exclusive.");
        }
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Lock timeout must be non-negative or infinite.");
    }

    private static bool WaitForRetry(
        TimeSpan timeout,
        Stopwatch? stopwatch,
        CancellationToken cancellationToken)
        => SqliteBusyBackoff.Wait(timeout, stopwatch, cancellationToken);

    private static WindowsOverlapped CreateWindowsOverlapped(long offset)
    {
        var value = checked((ulong)offset);
        return new WindowsOverlapped
        {
            OffsetLow = unchecked((uint)value),
            OffsetHigh = checked((uint)(value >> 32)),
        };
    }

    private static void GetWindowsLengthParts(long length, out uint lowLength, out uint highLength)
    {
        var value = checked((ulong)length);
        lowLength = unchecked((uint)value);
        highLength = checked((uint)(value >> 32));
    }

    private static IOException CreateContentionException(string operation, int error)
        => new(
            $"{operation} could not acquire the requested SQLite WAL byte-range lock.",
            new Win32Exception(error));

    private static void ThrowNativeIOException(string operation, int error)
        => throw new IOException(
            $"{operation} failed with native error {error}: {new Win32Exception(error).Message}.",
            new Win32Exception(error));

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsOverlapped
    {
        internal nint Internal;
        internal nint InternalHigh;
        internal uint OffsetLow;
        internal uint OffsetHigh;
        internal nint Event;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct LinuxFileLock
    {
        internal LinuxFileLock(short type, short whence, long start, long length)
        {
            Type = type;
            Whence = whence;
            Start = start;
            Length = length;
            ProcessId = 0;
        }

        [FieldOffset(0)]
        internal short Type;

        [FieldOffset(2)]
        internal short Whence;

        [FieldOffset(8)]
        internal long Start;

        [FieldOffset(16)]
        internal long Length;

        [FieldOffset(24)]
        internal int ProcessId;
    }

    /// <summary>
    /// Darwin <c>struct flock</c> field order differs from Linux:
    /// <c>l_start, l_len, l_pid, l_type, l_whence</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 24)]
    private struct MacFileLock
    {
        internal MacFileLock(short type, short whence, long start, long length)
        {
            Start = start;
            Length = length;
            ProcessId = 0;
            Type = type;
            Whence = whence;
        }

        internal long Start;
        internal long Length;
        internal int ProcessId;
        internal short Type;
        internal short Whence;
    }

    private static partial class Native
    {
        [LibraryImport("kernel32.dll", EntryPoint = "LockFileEx", SetLastError = true)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool LockFileEx(
            SafeFileHandle file,
            uint flags,
            uint reserved,
            uint numberOfBytesToLockLow,
            uint numberOfBytesToLockHigh,
            ref WindowsOverlapped overlapped);

        [LibraryImport("kernel32.dll", EntryPoint = "UnlockFileEx", SetLastError = true)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool UnlockFileEx(
            SafeFileHandle file,
            uint reserved,
            uint numberOfBytesToUnlockLow,
            uint numberOfBytesToUnlockHigh,
            ref WindowsOverlapped overlapped);

        [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int FcntlLinux(
            SafeFileHandle fileDescriptor,
            int command,
            ref LinuxFileLock fileLock);

        [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int FcntlMac(
            SafeFileHandle fileDescriptor,
            int command,
            ref MacFileLock fileLock);
    }

}

/// <summary>
/// Keeps one physical file handle open while a higher-level SQLite protocol
/// changes the modes of several byte ranges on that same carrier.
/// </summary>
internal sealed class SqliteByteRangeLockHandle : IDisposable
{
    private SafeFileHandle? _handle;

    internal SqliteByteRangeLockHandle(string path, bool writable)
    {
        _ = new SqliteWalByteRangeLock(path);
        _handle = File.OpenHandle(
            Path.GetFullPath(path),
            FileMode.Open,
            writable ? FileAccess.ReadWrite : FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.None);
    }

    internal bool TryLock(
        long offset,
        long length,
        SqliteWalByteRangeLockMode mode,
        out IOException? contention)
        => SqliteWalByteRangeLock.TryLock(
            GetHandle(),
            offset,
            length,
            mode,
            out contention);

    internal void Unlock(long offset, long length)
        => SqliteWalByteRangeLock.Unlock(GetHandle(), offset, length);

    internal bool HasConflictingExclusiveLock(long offset, long length)
        => SqliteWalByteRangeLock.HasConflictingExclusiveLock(GetHandle(), offset, length);

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, null);
        handle?.Dispose();
    }

    private SafeFileHandle GetHandle()
        => Volatile.Read(ref _handle)
           ?? throw new ObjectDisposedException(nameof(SqliteByteRangeLockHandle));
}

/// <summary>Releases the exact SQLite WAL byte range acquired by its owner.</summary>
public sealed class SqliteWalByteRangeLockLease : IDisposable
{
    private SafeFileHandle? _handle;
    private readonly long _offset;
    private readonly long _length;

    internal SqliteWalByteRangeLockLease(
        SafeFileHandle handle,
        long offset,
        long length,
        SqliteWalByteRangeLockMode mode)
    {
        _handle = handle;
        CarrierIdentity = SqliteWalSharedMemoryCarrierIdentity.FromHandle(handle);
        _offset = offset;
        _length = length;
        Mode = mode;
    }

    /// <summary>The first byte protected by this lease.</summary>
    public long Offset => _offset;

    /// <summary>The nonzero number of bytes protected by this lease.</summary>
    public long Length => _length;

    /// <summary>The mode acquired for this lease.</summary>
    public SqliteWalByteRangeLockMode Mode { get; }

    /// <summary>Whether this lease still owns its operating-system lock.</summary>
    public bool IsActive => Volatile.Read(ref _handle) is not null;

    internal SqliteWalSharedMemoryCarrierIdentity CarrierIdentity { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, null);
        if (handle is null)
            return;

        try
        {
            SqliteWalByteRangeLock.Unlock(handle, _offset, _length);
        }
        finally
        {
            // Closing a dedicated carrier is a second release path if the
            // explicit native unlock itself reports an error.
            handle.Dispose();
        }
    }
}

/// <summary>Identifies one physical shared-memory carrier across independently opened handles.</summary>
internal readonly partial record struct SqliteWalSharedMemoryCarrierIdentity(ulong Device, ulong File)
{
    internal static SqliteWalSharedMemoryCarrierIdentity FromPath(string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            if (Native.StatMac(path, out var information) != 0)
                ThrowPathStatFailure(path, Marshal.GetLastPInvokeError());

            return new SqliteWalSharedMemoryCarrierIdentity(
                unchecked((ulong)(uint)information.Device),
                information.Inode);
        }

        using var handle = System.IO.File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.None);
        return FromHandle(handle);
    }

    /// <summary>
    /// Resolves the physical identity of a directory rather than a file. Used to canonicalize the
    /// containing directory of a path that does not exist yet (for example, a replica database
    /// file before its first bootstrap), so that two textually different paths naming the same
    /// physical directory -- via a symbolic link, junction/mount point, hard link, or Windows
    /// short (8.3) name alias -- resolve to the same identity.
    /// </summary>
    internal static SqliteWalSharedMemoryCarrierIdentity FromDirectoryPath(string directoryPath)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows' CreateFile denies read access to a directory unless the caller passes
            // FILE_FLAG_BACKUP_SEMANTICS, and File.OpenHandle never sets that flag, so a directory
            // must be opened through this dedicated native path instead of File.OpenHandle.
            using var handle = Native.OpenDirectoryHandleWindows(directoryPath);
            return FromHandle(handle);
        }

        // POSIX open() does succeed on a directory, but File.OpenHandle deliberately refuses to
        // hand one back: it fstats the descriptor it just opened and throws
        // UnauthorizedAccessException for anything that is a directory. A directory's identity on
        // Unix therefore has to come from a path-based stat rather than from a handle -- which is
        // also exactly how FromPath already resolves a file on macOS.
        if (OperatingSystem.IsMacOS())
        {
            if (Native.StatMac(directoryPath, out var macInformation) != 0)
                ThrowDirectoryStatFailure(directoryPath, Marshal.GetLastPInvokeError());

            return new SqliteWalSharedMemoryCarrierIdentity(
                unchecked((ulong)(uint)macInformation.Device),
                macInformation.Inode);
        }

        if (OperatingSystem.IsLinux() && Environment.Is64BitProcess)
        {
            if (Native.StatLinux(directoryPath, out var linuxInformation) != 0)
                ThrowDirectoryStatFailure(directoryPath, Marshal.GetLastPInvokeError());

            return new SqliteWalSharedMemoryCarrierIdentity(linuxInformation.Device, linuxInformation.Inode);
        }

        throw new PlatformNotSupportedException(
            "SQLite WAL shared-memory carrier identity is supported only on Windows, 64-bit Linux, and macOS.");
    }

    /// <summary>
    /// Reports a failed directory stat the way the managed file APIs would, so callers can keep
    /// distinguishing "this parent directory does not exist (yet)" -- an expected state during a
    /// first bootstrap -- from a genuine I/O failure.
    /// </summary>
    private static void ThrowDirectoryStatFailure(string directoryPath, int error)
    {
        if (error is NoSuchFileOrDirectory or NotADirectory)
            throw new DirectoryNotFoundException($"Could not find a part of the path '{directoryPath}'.");

        ThrowNativeIOException("stat", error);
    }

    /// <summary>
    /// The file-oriented counterpart of <see cref="ThrowDirectoryStatFailure"/>: a caller probing
    /// whether a database file exists yet must see the same <see cref="FileNotFoundException"/> the
    /// handle-based path on Linux and Windows raises, not a generic I/O failure.
    /// </summary>
    private static void ThrowPathStatFailure(string path, int error)
    {
        if (error == NoSuchFileOrDirectory)
            throw new FileNotFoundException($"Could not find file '{path}'.", path);
        if (error == NotADirectory)
            throw new DirectoryNotFoundException($"Could not find a part of the path '{path}'.");

        ThrowNativeIOException("stat", error);
    }

    private const int NoSuchFileOrDirectory = 2; // ENOENT, identical on Linux and macOS.
    private const int NotADirectory = 20; // ENOTDIR, identical on Linux and macOS.

    internal static SqliteWalSharedMemoryCarrierIdentity FromHandle(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (OperatingSystem.IsWindows())
        {
            if (!Native.GetFileInformationByHandle(handle, out var information))
                ThrowNativeIOException("GetFileInformationByHandle", Marshal.GetLastPInvokeError());

            return new SqliteWalSharedMemoryCarrierIdentity(
                information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
        }

        if (OperatingSystem.IsLinux() && Environment.Is64BitProcess)
        {
            if (Native.FstatLinux(handle, out var information) != 0)
                ThrowNativeIOException("fstat", Marshal.GetLastPInvokeError());

            return new SqliteWalSharedMemoryCarrierIdentity(information.Device, information.Inode);
        }

        if (OperatingSystem.IsMacOS())
        {
            if (Native.FstatMac(handle, out var information) != 0)
                ThrowNativeIOException("fstat", Marshal.GetLastPInvokeError());

            return new SqliteWalSharedMemoryCarrierIdentity(
                unchecked((ulong)(uint)information.Device),
                information.Inode);
        }

        throw new PlatformNotSupportedException(
            "SQLite WAL shared-memory carrier identity is supported only on Windows, 64-bit Linux, and macOS.");
    }

    private static void ThrowNativeIOException(string operation, int error)
        => throw new IOException(
            $"{operation} failed with native error {error}: {new Win32Exception(error).Message}.",
            new Win32Exception(error));

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileInformation
    {
        internal uint FileAttributes;
        internal uint CreationTimeLow;
        internal uint CreationTimeHigh;
        internal uint LastAccessTimeLow;
        internal uint LastAccessTimeHigh;
        internal uint LastWriteTimeLow;
        internal uint LastWriteTimeHigh;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential, Size = 144)]
    private struct LinuxFileStatus
    {
        internal ulong Device;
        internal ulong Inode;
        private ulong _linkCount;
        private uint _mode;
        private uint _userId;
        private uint _groupId;
        private int _padding;
        private ulong _deviceType;
        private long _size;
        private long _blockSize;
        private long _blockCount;
        private long _accessSeconds;
        private long _accessNanoseconds;
        private long _modificationSeconds;
        private long _modificationNanoseconds;
        private long _changeSeconds;
        private long _changeNanoseconds;
        private long _reserved1;
        private long _reserved2;
        private long _reserved3;
    }

    /// <summary>
    /// Darwin 64-bit-inode <c>struct stat</c> layout for carrier identity only.
    /// <c>st_dev</c> @0, <c>st_ino</c> @8 after mode/nlink.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct MacFileStatus
    {
        [FieldOffset(0)]
        internal int Device;

        [FieldOffset(8)]
        internal ulong Inode;
    }

    private static partial class Native
    {
        private const uint GenericRead = 0x8000_0000;
        private const uint FileShareRead = 0x0000_0001;
        private const uint FileShareWrite = 0x0000_0002;
        private const uint FileShareDelete = 0x0000_0004;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x0200_0000;

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetFileInformationByHandle(
            SafeFileHandle file,
            out WindowsFileInformation information);

        [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int FstatLinux(
            SafeFileHandle fileDescriptor,
            out LinuxFileStatus information);

        [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int FstatMac(
            SafeFileHandle fileDescriptor,
            out MacFileStatus information);

        [LibraryImport(
            "libc",
            EntryPoint = "stat",
            StringMarshalling = StringMarshalling.Utf8,
            SetLastError = true)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int StatMac(
            string path,
            out MacFileStatus information);

        /// <summary>
        /// Path-based <c>stat</c> for 64-bit Linux, used for directories: <c>fstat</c> needs a
        /// descriptor, and <see cref="System.IO.File.OpenHandle"/> refuses to produce one for a
        /// directory on Unix.
        /// </summary>
        [LibraryImport(
            "libc",
            EntryPoint = "stat",
            StringMarshalling = StringMarshalling.Utf8,
            SetLastError = true)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int StatLinux(
            string path,
            out LinuxFileStatus information);

        /// <summary>
        /// Opens a directory for metadata-only access on Windows. <see cref="System.IO.File.OpenHandle"/>
        /// always denies directories (it never sets <c>FILE_FLAG_BACKUP_SEMANTICS</c>), so directory
        /// identity resolution goes through this dedicated <c>CreateFile</c> call instead.
        /// </summary>
        [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        internal static SafeFileHandle OpenDirectoryHandleWindows(string directoryPath)
        {
            var handle = CreateFileW(
                directoryPath,
                GenericRead,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                if (error is 2 or 3)
                {
                    throw new DirectoryNotFoundException(
                        $"CreateFile failed because directory '{directoryPath}' does not exist.",
                        new Win32Exception(error));
                }
                ThrowNativeIOException("CreateFile", error);
            }
            return handle;
        }
    }
}
