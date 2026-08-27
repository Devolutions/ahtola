using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ahtola.Core.Storage;

public sealed partial class PhysicalFileSystem
{
    /// <summary>
    /// Opens a mapped SQLite <c>-shm</c> file without connecting it to pager
    /// coordination or WAL-index publication.
    /// </summary>
    public ISqliteWalSharedMemoryMapping OpenSharedMemory(
        string path,
        FileOpenMode mode,
        bool readOnly = false)
        => OpenSharedMemoryCore(
            path,
            mode,
            readOnly,
            FileShare.ReadWrite | FileShare.Delete,
            preventsCarrierReplacement: false);

    internal ISqliteWalSharedMemoryMapping OpenSharedMemoryForRecovery(string path)
        => OpenSharedMemoryCore(
            path,
            FileOpenMode.OpenExisting,
            readOnly: false,
            FileShare.ReadWrite,
            preventsCarrierReplacement: true);

    private static ISqliteWalSharedMemoryMapping OpenSharedMemoryCore(
        string path,
        FileOpenMode mode,
        bool readOnly,
        FileShare share,
        bool preventsCarrierReplacement)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        PhysicalSqliteWalSharedMemoryMapping.ThrowIfPlatformUnsupported();

        if (readOnly && mode == FileOpenMode.CreateNew)
            throw new ArgumentException("A newly created shared-memory file cannot be opened read-only.", nameof(readOnly));

        var fileMode = mode switch
        {
            FileOpenMode.OpenExisting => FileMode.Open,
            // A read-only open may inspect an existing mapping but must never
            // create its companion file.
            FileOpenMode.OpenOrCreate when readOnly => FileMode.Open,
            FileOpenMode.OpenOrCreate => FileMode.OpenOrCreate,
            FileOpenMode.CreateNew => FileMode.CreateNew,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported file open mode."),
        };

        lock (PhysicalSqliteWalSharedMemoryMapping.SqliteWalSharedMemoryLifecycleRegistry.Gate)
        {
            var handle = mode == FileOpenMode.CreateNew
                ? File.OpenHandle(
                    path,
                    fileMode,
                    readOnly ? FileAccess.Read : FileAccess.ReadWrite,
                    share,
                    FileOptions.None)
                : PhysicalSqliteWalSharedMemoryMapping
                    .SqliteWalSharedMemoryLifecycleRegistry
                    .TryGetOrCreateBrokeredHandle(path, requireWritable: !readOnly)
                  ?? File.OpenHandle(
                      path,
                      fileMode,
                      readOnly ? FileAccess.Read : FileAccess.ReadWrite,
                      share,
                      FileOptions.None);
            try
            {
                return new PhysicalSqliteWalSharedMemoryMapping(
                    handle,
                    path,
                    readOnly,
                    preventsCarrierReplacement);
            }
            catch
            {
                if (!PhysicalSqliteWalSharedMemoryMapping
                        .SqliteWalSharedMemoryLifecycleRegistry
                        .OwnsBrokeredHandle(handle))
                {
                    handle.Dispose();
                }
                throw;
            }
        }
    }
}

/// <summary>
/// A physical, file-backed SQLite shared-memory mapping. Its mapped range follows
/// the file length and writable mappings grow it only through <see cref="Write"/>.
/// </summary>
internal sealed partial class PhysicalSqliteWalSharedMemoryMapping :
    ISqliteWalSharedMemoryMapping,
    ISqliteWalSharedMemoryLockCarrier
{
    private const uint PageReadOnly = 0x02;
    private const uint PageReadWrite = 0x04;
    private const uint FileMapWrite = 0x0002;
    private const uint FileMapRead = 0x0004;
    private const int ProtRead = 0x1;
    private const int ProtWrite = 0x2;
    private const int MapShared = 0x01;
    // Linux MS_SYNC=4; Darwin MS_SYNC=0x0010.
    private static int MsSyncFlag => OperatingSystem.IsMacOS() ? 0x0010 : 0x04;
    private const uint DuplicateSameAccess = 0x0000_0002;

    /// <summary>
    /// SQLite's Windows/Unix WAL-index dead-man switch lock byte. Equals
    /// <c>WIN_SHM_BASE + SQLITE_SHM_NLOCK</c> (120 + 8). While any engine holds a
    /// shared DMS lock, a newly attaching engine must not truncate <c>-shm</c>.
    /// </summary>
    internal const long DeadManSwitchLockOffset = 128;

    private readonly object _gate = new();
    private readonly SafeFileHandle _fileHandle;
    private readonly string _lockFilePath;
    private SafeWindowsFileMappingHandle? _windowsMapping;
    private SafeMappedViewHandle? _view;
    private long _length;
    private bool _registered;
    private bool _disposed;

    internal static Action? BeforeFinalDeleteForTesting { get; set; }

    internal PhysicalSqliteWalSharedMemoryMapping(
        SafeFileHandle fileHandle,
        string lockFilePath,
        bool readOnly,
        bool preventsCarrierReplacement)
    {
        _fileHandle = fileHandle;
        _lockFilePath = lockFilePath;
        CarrierIdentity = SqliteWalSharedMemoryCarrierIdentity.FromHandle(fileHandle);
        IsReadOnly = readOnly;
        PreventsCarrierReplacement = preventsCarrierReplacement && OperatingSystem.IsWindows();

        // Hold the DMS shared lock for the mapping lifetime so stock SQLite/Turso
        // do not treat this process as absent and truncate a live -shm mapping
        // (SQLite winLockSharedMemory / unixLockSharedMemory). One process-registry
        // lease per carrier also prevents a sibling mapping from closing the DMS
        // lease that represents all managed mappings on macOS.
        lock (SqliteWalSharedMemoryLifecycleRegistry.Gate)
        {
            SqliteWalSharedMemoryLifecycleRegistry.Register(
                CarrierIdentity,
                lockFilePath,
                fileHandle,
                writable: !readOnly,
                () => new SqliteWalByteRangeLock(lockFilePath).AcquireShared(
                    DeadManSwitchLockOffset,
                    length: 1,
                    timeout: TimeSpan.FromSeconds(5),
                    cancellationToken: CancellationToken.None));
            _registered = true;
            try
            {
                lock (_gate)
                {
                    MapCurrentFileLengthLocked(RandomAccess.GetLength(_fileHandle));
                }
            }
            catch
            {
                SqliteWalSharedMemoryLifecycleRegistry.Unregister(
                    CarrierIdentity);
                _registered = false;
                throw;
            }
        }
    }

    public bool IsReadOnly { get; }

    internal SqliteWalSharedMemoryCarrierIdentity CarrierIdentity { get; }

    SqliteWalSharedMemoryCarrierIdentity ISqliteWalSharedMemoryCarrierIdentity.CarrierIdentity
        => CarrierIdentity;

    internal bool PreventsCarrierReplacement { get; }

    bool ISqliteWalSharedMemoryLockCarrier.PreventsCarrierReplacement
        => PreventsCarrierReplacement;

    SafeFileHandle ISqliteWalSharedMemoryLockCarrier.DuplicateLockCarrierHandle()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (OperatingSystem.IsWindows())
            {
                var currentProcess = Native.GetCurrentProcess();
                if (Native.DuplicateHandle(
                        currentProcess,
                        _fileHandle,
                        currentProcess,
                        out var duplicate,
                        desiredAccess: 0,
                        inheritHandle: false,
                        DuplicateSameAccess) == 0)
                {
                    ThrowNativeIOException("DuplicateHandle", Marshal.GetLastPInvokeError());
                }

                return duplicate;
            }

            if ((OperatingSystem.IsLinux() && Environment.Is64BitProcess)
                || OperatingSystem.IsMacOS())
            {
                if (OperatingSystem.IsMacOS())
                {
                    return new SafeFileHandle(
                        _fileHandle.DangerousGetHandle(),
                        ownsHandle: false);
                }

                var descriptor = Native.Dup(_fileHandle);
                if (descriptor < 0)
                    ThrowNativeIOException("dup", Marshal.GetLastPInvokeError());

                return new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
            }

            ThrowIfPlatformUnsupported();
            throw new InvalidOperationException("The SQLite WAL shared-memory carrier platform selection is inconsistent.");
        }
    }

    public long Length
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                SynchronizeLengthLocked();
                return _length;
            }
        }
    }

    public void Read(long position, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);

        lock (_gate)
        {
            ThrowIfDisposed();
            SynchronizeLengthLocked();
            ValidateRange(position, destination.Length);
            if (destination.IsEmpty)
                return;

            unsafe
            {
                var source = new ReadOnlySpan<byte>(
                    (void*)((nint)_view!.DangerousGetHandle() + (nint)position),
                    destination.Length);
                source.CopyTo(destination);
            }
        }
    }

    public void Write(long position, ReadOnlySpan<byte> source)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);

        lock (_gate)
        {
            ThrowIfDisposed();
            if (IsReadOnly)
                throw new InvalidOperationException("Cannot write to a shared-memory mapping opened read-only.");

            SynchronizeLengthLocked();
            if (source.IsEmpty)
            {
                ValidateRange(position, 0);
                return;
            }
            if (source.Length > long.MaxValue - position)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(position),
                    "The shared-memory write extends beyond the supported file length.");
            }

            var requiredLength = position + source.Length;
            if (requiredLength > _length)
            {
                ValidateMappableLength(requiredLength);
                RandomAccess.SetLength(_fileHandle, requiredLength);
                MapCurrentFileLengthLocked(requiredLength);
            }

            unsafe
            {
                source.CopyTo(new Span<byte>(
                    (void*)((nint)_view!.DangerousGetHandle() + (nint)position),
                    source.Length));
            }
        }
    }

    public void MemoryBarrier()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            // MAP_SHARED/MapViewOfFile supplies shared physical pages; this full
            // fence orders prior writes before a dependent header publication.
            Thread.MemoryBarrier();
        }
    }

    public void Dispose()
        => DisposeCore(deleteIfLast: false);

    /// <summary>
    /// Releases this mapping and removes its transient carrier only when it is the
    /// final managed mapping and a non-blocking exclusive DMS probe proves that no
    /// stock SQLite/Turso peer remains.
    /// </summary>
    internal bool DisposeAndTryDeleteIfLast()
        => DisposeCore(deleteIfLast: true);

    private bool DisposeCore(bool deleteIfLast)
    {
        Exception? flushFailure = null;
        Exception? dmsFailure = null;
        var deleted = false;
        var fileHandleBrokered = OperatingSystem.IsMacOS();
        lock (SqliteWalSharedMemoryLifecycleRegistry.Gate)
        {
            lock (_gate)
            {
                if (_disposed)
                    return false;

                try
                {
                    FlushMappedViewLocked();
                }
                catch (Exception exception)
                {
                    flushFailure = exception;
                }
                finally
                {
                    DisposeMappedViewLocked();
                    try
                    {
                        var isFinalManagedMapping = !_registered
                            || SqliteWalSharedMemoryLifecycleRegistry.Unregister(CarrierIdentity);
                        _registered = false;
                        if (!isFinalManagedMapping)
                        {
                            deleteIfLast = false;
                        }
                        else if (deleteIfLast && !OperatingSystem.IsMacOS())
                        {
                            BeforeFinalDeleteForTesting?.Invoke();
                        }
                    }
                    catch (Exception exception)
                    {
                        dmsFailure = exception;
                        deleteIfLast = false;
                    }

                    SqliteWalByteRangeLockLease? exclusiveDms = null;
                    if (deleteIfLast
                        && !OperatingSystem.IsMacOS()
                        && !IsReadOnly
                        && flushFailure is null
                        && dmsFailure is null)
                    {
                        var locks = new SqliteWalByteRangeLock(_lockFilePath);
                        if (locks.TryAcquireExclusive(
                                this,
                                DeadManSwitchLockOffset,
                                length: 1,
                                out exclusiveDms))
                        {
                            try
                            {
                                if (File.Exists(_lockFilePath)
                                    && SqliteWalSharedMemoryCarrierIdentity.FromPath(_lockFilePath) == CarrierIdentity)
                                {
                                    File.Delete(_lockFilePath);
                                    deleted = true;
                                }
                            }
                            catch (IOException)
                            {
                                // The transient carrier is an optimization. A peer
                                // or platform policy may still prevent unlink.
                            }
                            catch (UnauthorizedAccessException)
                            {
                                // Best effort, matching SQLite's close-time unlink.
                            }
                        }
                    }

                    _disposed = true;
                    exclusiveDms?.Dispose();
                    if (!fileHandleBrokered)
                        _fileHandle.Dispose();
                }
            }
        }

        if (flushFailure is not null)
            throw new IOException("Failed to flush the SQLite shared-memory mapping during disposal.", flushFailure);
        if (dmsFailure is not null)
        {
            throw new IOException(
                "Failed to release the SQLite WAL-index dead-man switch lock during disposal.",
                dmsFailure);
        }

        return deleted;
    }

    internal static void ThrowIfPlatformUnsupported()
    {
        if (OperatingSystem.IsWindows())
            return;
        if (OperatingSystem.IsLinux() && Environment.Is64BitProcess)
            return;
        if (OperatingSystem.IsMacOS())
            return;

        throw new PlatformNotSupportedException(
            "Physical SQLite shared-memory mappings are supported only on Windows, 64-bit Linux, and macOS.");
    }

    private void SynchronizeLengthLocked()
    {
        var fileLength = RandomAccess.GetLength(_fileHandle);
        if (fileLength != _length)
            MapCurrentFileLengthLocked(fileLength);
    }

    private void MapCurrentFileLengthLocked(long length)
    {
        ValidateMappableLength(length);
        if (length == _length && (_view is not null || length == 0))
            return;

        FlushMappedViewLocked();
        DisposeMappedViewLocked();
        _length = 0;
        if (length == 0)
            return;

        if (OperatingSystem.IsWindows())
        {
            MapWindowsViewLocked(length);
        }
        else if ((OperatingSystem.IsLinux() && Environment.Is64BitProcess)
                 || OperatingSystem.IsMacOS())
        {
            MapUnixViewLocked(length);
        }
        else
        {
            ThrowIfPlatformUnsupported();
        }

        _length = length;
    }

    private void MapWindowsViewLocked(long length)
    {
        var mappingHandle = Native.CreateFileMapping(
            _fileHandle,
            IntPtr.Zero,
            IsReadOnly ? PageReadOnly : PageReadWrite,
            checked((uint)((ulong)length >> 32)),
            unchecked((uint)length),
            name: null);
        if (mappingHandle == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            ThrowNativeIOException("CreateFileMappingW", error);
        }

        var mapping = new SafeWindowsFileMappingHandle(mappingHandle);
        var address = Native.MapViewOfFile(
            mapping,
            IsReadOnly ? FileMapRead : FileMapWrite,
            fileOffsetHigh: 0,
            fileOffsetLow: 0,
            checked((nuint)length));
        if (address == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            mapping.Dispose();
            ThrowNativeIOException("MapViewOfFile", error);
        }

        _windowsMapping = mapping;
        _view = new SafeWindowsMappedViewHandle(address);
    }

    private void MapUnixViewLocked(long length)
    {
        var address = Native.Mmap(
            address: 0,
            checked((nuint)length),
            ProtRead | (IsReadOnly ? 0 : ProtWrite),
            MapShared,
            _fileHandle,
            offset: 0);
        if (address == -1)
            ThrowNativeIOException("mmap", Marshal.GetLastPInvokeError());

        _view = new SafeLinuxMappedViewHandle(address, checked((nuint)length));
    }

    private void FlushMappedViewLocked()
    {
        if (IsReadOnly || _view is null)
            return;

        var address = _view.DangerousGetHandle();
        if (OperatingSystem.IsWindows())
        {
            if (Native.FlushViewOfFile(address, checked((nuint)_length)) == 0)
                ThrowNativeIOException("FlushViewOfFile", Marshal.GetLastPInvokeError());
            if (Native.FlushFileBuffers(_fileHandle) == 0)
                ThrowNativeIOException("FlushFileBuffers", Marshal.GetLastPInvokeError());
            return;
        }

        if ((OperatingSystem.IsLinux() && Environment.Is64BitProcess)
            || OperatingSystem.IsMacOS())
        {
            if (Native.Msync(address, checked((nuint)_length), MsSyncFlag) != 0)
                ThrowNativeIOException("msync", Marshal.GetLastPInvokeError());
            RandomAccess.FlushToDisk(_fileHandle);
            return;
        }

        ThrowIfPlatformUnsupported();
    }

    private void DisposeMappedViewLocked()
    {
        _view?.Dispose();
        _view = null;
        _windowsMapping?.Dispose();
        _windowsMapping = null;
    }

    private void ValidateRange(long position, int byteCount)
    {
        if (position > _length || byteCount > _length - position)
        {
            throw new ArgumentOutOfRangeException(
                nameof(position),
                "The requested shared-memory range is outside the mapped length.");
        }
    }

    private static void ValidateMappableLength(long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (IntPtr.Size == sizeof(int) && length > int.MaxValue)
        {
            throw new IOException(
                "The SQLite shared-memory file is too large to map in this process.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PhysicalSqliteWalSharedMemoryMapping));
    }

    private static void ThrowNativeIOException(string operation, int error)
    {
        var nativeError = new Win32Exception(error);
        throw new IOException(
            $"{operation} failed with native error {error}: {nativeError.Message}.",
            nativeError);
    }

    internal static class SqliteWalSharedMemoryLifecycleRegistry
    {
        internal static object Gate { get; } = new();
        private static readonly Dictionary<SqliteWalSharedMemoryCarrierIdentity, Entry> Mappings = [];
        private static readonly Dictionary<string, Entry> MappingsByPath =
            new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        internal static void Register(
            SqliteWalSharedMemoryCarrierIdentity identity,
            string path,
            SafeFileHandle handle,
            bool writable,
            Func<SqliteWalByteRangeLockLease> acquireSharedDms)
        {
            if (Mappings.TryGetValue(identity, out var entry))
            {
                entry.MappingCount = checked(entry.MappingCount + 1);
                entry.AddPath(path);
                if (OperatingSystem.IsMacOS())
                    entry.AddBrokeredHandle(handle, writable);
                return;
            }

            var sharedDms = acquireSharedDms();
            if (sharedDms.CarrierIdentity != identity)
            {
                sharedDms.Dispose();
                throw new InvalidDataException(
                    "The SQLite WAL shared-memory path changed while its DMS lease was being acquired.");
            }

            entry = new Entry(identity, sharedDms);
            entry.AddPath(path);
            if (OperatingSystem.IsMacOS())
                entry.AddBrokeredHandle(handle, writable);
            Mappings.Add(identity, entry);
        }

        internal static bool Unregister(SqliteWalSharedMemoryCarrierIdentity identity)
        {
            if (!Mappings.TryGetValue(identity, out var entry) || entry.MappingCount <= 0)
                throw new InvalidOperationException("SQLite WAL shared-memory mapping registry underflow.");

            entry.MappingCount--;
            if (entry.MappingCount != 0 || entry.BorrowedLeaseCount != 0)
                return false;

            RemoveAndDisposeEntry(identity, entry);
            return true;
        }

        internal static SafeFileHandle? TryGetOrCreateBrokeredHandle(string path, bool requireWritable)
        {
            if (!OperatingSystem.IsMacOS())
                return null;

            lock (Gate)
            {
                var fullPath = Path.GetFullPath(path);
                if (!MappingsByPath.TryGetValue(fullPath, out var entry))
                    return null;
                try
                {
                    if (SqliteWalSharedMemoryCarrierIdentity.FromPath(path) != entry.Identity)
                    {
                        MappingsByPath.Remove(fullPath);
                        return null;
                    }
                }

                catch (FileNotFoundException)
                {
                    MappingsByPath.Remove(fullPath);
                    return null;
                }

                if (!requireWritable)
                    return entry.WritableHandle ?? entry.ReadOnlyHandle;
                if (entry.WritableHandle is not null)
                    return entry.WritableHandle;

                // F_WRLCK requires a writable descriptor on Darwin. Keep this
                // lazily opened descriptor in the carrier registry: closing a
                // per-lease descriptor would release every process-owned fcntl
                // lock for the inode, including DMS and unrelated read marks.
                var writableHandle = File.OpenHandle(
                    fullPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete,
                    FileOptions.None);
                try
                {
                    if (SqliteWalSharedMemoryCarrierIdentity.FromHandle(writableHandle) != entry.Identity)
                    {
                        throw new IOException(
                            "The SQLite WAL shared-memory path changed while opening its writable lock broker.");
                    }

                    entry.AddBrokeredHandle(writableHandle, writable: true);
                    return writableHandle;
                }
                catch
                {
                    writableHandle.Dispose();
                    throw;
                }
            }
        }

        internal static BrokeredHandleBorrow? TryBorrowBrokeredHandle(
            string path,
            bool requireWritable)
        {
            if (!OperatingSystem.IsMacOS())
                return null;

            lock (Gate)
            {
                var handle = TryGetOrCreateBrokeredHandle(path, requireWritable);
                if (handle is null)
                    return null;

                var identity = SqliteWalSharedMemoryCarrierIdentity.FromHandle(handle);
                if (!Mappings.TryGetValue(identity, out var entry))
                    throw new InvalidOperationException("SQLite WAL shared-memory broker entry disappeared.");

                entry.BorrowedLeaseCount = checked(entry.BorrowedLeaseCount + 1);
                return new BrokeredHandleBorrow(identity, handle);
            }
        }

        internal static bool OwnsBrokeredHandle(SafeFileHandle handle)
        {
            lock (Gate)
            {
                return Mappings.Values.Any(
                    entry => entry.BrokeredHandles.Any(
                        brokeredHandle => ReferenceEquals(brokeredHandle, handle)));
            }
        }

        internal static int GetBrokeredHandleCountForTesting(string path)
        {
            lock (Gate)
            {
                return MappingsByPath.TryGetValue(Path.GetFullPath(path), out var entry)
                    ? entry.BrokeredHandles.Count
                    : 0;
            }
        }

        private static void ReleaseBrokeredHandle(SqliteWalSharedMemoryCarrierIdentity identity)
        {
            lock (Gate)
            {
                if (!Mappings.TryGetValue(identity, out var entry)
                    || entry.BorrowedLeaseCount <= 0)
                {
                    throw new InvalidOperationException("SQLite WAL shared-memory broker lease underflow.");
                }

                entry.BorrowedLeaseCount--;
                if (entry.MappingCount == 0 && entry.BorrowedLeaseCount == 0)
                    RemoveAndDisposeEntry(identity, entry);
            }
        }

        private static void RemoveAndDisposeEntry(
            SqliteWalSharedMemoryCarrierIdentity identity,
            Entry entry)
        {
            Mappings.Remove(identity);
            foreach (var path in entry.Paths)
            {
                if (MappingsByPath.TryGetValue(path, out var mappedEntry)
                    && ReferenceEquals(mappedEntry, entry))
                {
                    MappingsByPath.Remove(path);
                }
            }

            try
            {
                entry.SharedDms.Dispose();
            }
            finally
            {
                foreach (var brokeredHandle in entry.BrokeredHandles)
                    brokeredHandle.Dispose();
            }
        }

        internal sealed class BrokeredHandleBorrow(
            SqliteWalSharedMemoryCarrierIdentity identity,
            SafeFileHandle handle) : IDisposable
        {
            private int _disposed;

            internal SafeFileHandle Handle { get; } = handle;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    ReleaseBrokeredHandle(identity);
            }
        }

        private sealed class Entry(
            SqliteWalSharedMemoryCarrierIdentity identity,
            SqliteWalByteRangeLockLease sharedDms)
        {
            internal SqliteWalSharedMemoryCarrierIdentity Identity { get; } = identity;
            internal int MappingCount { get; set; } = 1;
            internal int BorrowedLeaseCount { get; set; }
            internal SqliteWalByteRangeLockLease SharedDms { get; } = sharedDms;
            internal SafeFileHandle? ReadOnlyHandle { get; private set; }
            internal SafeFileHandle? WritableHandle { get; private set; }
            internal List<SafeFileHandle> BrokeredHandles { get; } = [];
            internal HashSet<string> Paths { get; } =
                new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            internal void AddPath(string path)
            {
                var fullPath = Path.GetFullPath(path);
                Paths.Add(fullPath);
                MappingsByPath[fullPath] = this;
            }

            internal void AddBrokeredHandle(SafeFileHandle handle, bool writable)
            {
                if (!BrokeredHandles.Any(candidate => ReferenceEquals(candidate, handle)))
                    BrokeredHandles.Add(handle);
                if (writable)
                    WritableHandle ??= handle;
                else
                    ReadOnlyHandle ??= handle;
            }
        }
    }

    private abstract class SafeMappedViewHandle : SafeHandle
    {
        protected SafeMappedViewHandle()
            : base(IntPtr.Zero, ownsHandle: true)
        {
        }

        protected SafeMappedViewHandle(nint address)
            : this()
        {
            SetHandle(address);
        }

        public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);
    }

    private sealed class SafeWindowsMappedViewHandle : SafeMappedViewHandle
    {
        internal SafeWindowsMappedViewHandle(nint address)
            : base(address)
        {
        }

        protected override bool ReleaseHandle() => Native.UnmapViewOfFile(handle) != 0;
    }

    private sealed class SafeLinuxMappedViewHandle : SafeMappedViewHandle
    {
        private readonly nuint _length;

        internal SafeLinuxMappedViewHandle(nint address, nuint length)
            : base(address)
        {
            _length = length;
        }

        protected override bool ReleaseHandle() => Native.Munmap(handle, _length) == 0;
    }

    private sealed class SafeWindowsFileMappingHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeWindowsFileMappingHandle(nint handle)
            : base(ownsHandle: true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle() => Native.CloseHandle(handle) != 0;
    }

    private static partial class Native
    {
        [DllImport(
            "kernel32.dll",
            EntryPoint = "CreateFileMappingW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        internal static extern nint CreateFileMapping(
            SafeFileHandle file,
            IntPtr attributes,
            uint protection,
            uint maximumSizeHigh,
            uint maximumSizeLow,
            string? name);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial nint MapViewOfFile(
            SafeWindowsFileMappingHandle fileMapping,
            uint desiredAccess,
            uint fileOffsetHigh,
            uint fileOffsetLow,
            nuint numberOfBytesToMap);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial int UnmapViewOfFile(nint baseAddress);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial int CloseHandle(nint handle);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial int FlushViewOfFile(nint baseAddress, nuint numberOfBytesToFlush);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial int FlushFileBuffers(SafeFileHandle file);

        [LibraryImport("kernel32.dll")]
        internal static partial nint GetCurrentProcess();

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial int DuplicateHandle(
            nint sourceProcess,
            SafeFileHandle sourceHandle,
            nint targetProcess,
            out SafeFileHandle targetHandle,
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint options);

        [LibraryImport("libc", EntryPoint = "mmap", SetLastError = true)]
        internal static partial nint Mmap(
            nint address,
            nuint length,
            int protection,
            int flags,
            SafeFileHandle file,
            long offset);

        [LibraryImport("libc", EntryPoint = "munmap", SetLastError = true)]
        internal static partial int Munmap(nint address, nuint length);

        [LibraryImport("libc", EntryPoint = "msync", SetLastError = true)]
        internal static partial int Msync(nint address, nuint length, int flags);

        [LibraryImport("libc", EntryPoint = "dup", SetLastError = true)]
        internal static partial int Dup(SafeFileHandle file);
    }
}
