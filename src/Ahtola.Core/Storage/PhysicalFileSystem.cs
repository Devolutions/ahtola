using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ahtola.Core.Storage;

/// <summary>
/// Production <see cref="IFileSystem"/> backed by the host file system. Files
/// use an OS handle with positional <see cref="RandomAccess"/> I/O, which is
/// safe for concurrent offset-addressed reads and writes on a single handle.
/// </summary>
public sealed partial class PhysicalFileSystem :
    IFileSystem,
    IAtomicFileSystem,
    ITemporaryFileSystem,
    IStoragePathResolver,
    ISqliteWalSharedMemoryFileSystem
{
    private const uint ReplaceFileWriteThrough = 0x00000001;

    /// <summary>A shared, stateless instance.</summary>
    public static PhysicalFileSystem Instance { get; } = new();

    public StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    public string GetCanonicalPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Path.GetFullPath(path);
    }

    public bool FileExists(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return File.Exists(path);
    }

    public FileWriteStamp? GetWriteStamp(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var info = new FileInfo(path);
        return info.Exists
            ? new FileWriteStamp(info.Length, info.LastWriteTimeUtc)
            : null;
    }

    public IFile OpenFile(string path, FileOpenMode mode, bool readOnly = false)
        => OpenFile(path, mode, readOnly, FileShare.Read, pagerLockPath: null);

    IFile ITemporaryFileSystem.OpenTemporaryFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var handle = File.OpenHandle(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            FileOptions.DeleteOnClose);
        return new PhysicalFile(handle, readOnly: false);
    }

    internal IFile OpenPagerFile(string path, FileOpenMode mode, bool readOnly = false)
        => OpenFile(path, mode, readOnly, FileShare.ReadWrite | FileShare.Delete, path);

    private IFile OpenFile(
        string path,
        FileOpenMode mode,
        bool readOnly,
        FileShare share,
        string? pagerLockPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (readOnly && mode == FileOpenMode.CreateNew)
            throw new ArgumentException("A newly created file cannot be opened read-only.", nameof(readOnly));

        var fileMode = mode switch
        {
            FileOpenMode.OpenExisting => FileMode.Open,
            FileOpenMode.OpenOrCreate => FileMode.OpenOrCreate,
            FileOpenMode.CreateNew => FileMode.CreateNew,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported file open mode."),
        };

        var access = readOnly ? FileAccess.Read : FileAccess.ReadWrite;
        var handle = File.OpenHandle(path, fileMode, access, share, FileOptions.None);
        return new PhysicalFile(handle, readOnly, pagerLockPath);
    }

    public void DeleteFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        File.Delete(path);
    }

    void IAtomicFileSystem.ReplaceFileAtomically(
        string sourcePath,
        string destinationPath,
        bool replaceEmptyDestination)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        if (pathComparer.Equals(source, destination))
            throw new IOException("Atomic file replacement requires distinct source and destination paths.");
        if (!File.Exists(source))
            throw new FileNotFoundException("The atomic replacement source does not exist.", source);

        if (!File.Exists(destination))
        {
            File.Move(source, destination);
            return;
        }

        if (!replaceEmptyDestination)
            throw new IOException("output file already exists");
        using var destinationReservation = new FileStream(
            destination,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1,
            FileOptions.None);
        if (OperatingSystem.IsWindows())
        {
            destinationReservation.Lock(0, 1);
            try
            {
                if (destinationReservation.Length != 0)
                    throw new IOException("output file already exists");
                if (ReplaceFile(
                        destination,
                        source,
                        backupFileName: null,
                        ReplaceFileWriteThrough,
                        IntPtr.Zero,
                        IntPtr.Zero) == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            }
            finally
            {
                destinationReservation.Unlock(0, 1);
            }
            return;
        }
        if (OperatingSystem.IsLinux())
        {
            destinationReservation.Lock(0, 1);
            try
            {
                if (destinationReservation.Length != 0)
                    throw new IOException("output file already exists");
                File.Move(source, destination, overwrite: true);
            }
            finally
            {
                destinationReservation.Unlock(0, 1);
            }
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            // FileStream.Lock is not implemented on Darwin; use the WAL lock primitive.
            var locks = new SqliteWalByteRangeLock(destination);
            using (locks.AcquireExclusive(0, 1, TimeSpan.FromSeconds(30)))
            {
                if (destinationReservation.Length != 0)
                    throw new IOException("output file already exists");
                File.Move(source, destination, overwrite: true);
            }
            return;
        }

        throw new PlatformNotSupportedException(
            "Atomic replacement of an existing empty destination is supported only on Windows, Linux, and macOS.");
    }

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "ReplaceFileW",
        StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    private static partial int ReplaceFile(
        string replacedFileName,
        string replacementFileName,
        string? backupFileName,
        uint replaceFlags,
        IntPtr exclude,
        IntPtr reserved);
}

/// <summary>
/// Gives <see cref="SqlitePager"/> its required shared data handles without
/// weakening the default sharing policy for direct page-store users.
/// </summary>
internal sealed class SqlitePagerPhysicalFileSystem(PhysicalFileSystem fileSystem) :
    IFileSystem,
    IAtomicFileSystem,
    IStoragePathResolver
{
    public StringComparer PathComparer => fileSystem.PathComparer;

    public string GetCanonicalPath(string path) => fileSystem.GetCanonicalPath(path);

    public bool FileExists(string path) => fileSystem.FileExists(path);

    public FileWriteStamp? GetWriteStamp(string path) => fileSystem.GetWriteStamp(path);

    public IFile OpenFile(string path, FileOpenMode mode, bool readOnly = false)
        => fileSystem.OpenPagerFile(path, mode, readOnly);

    public void DeleteFile(string path) => fileSystem.DeleteFile(path);

    void IAtomicFileSystem.ReplaceFileAtomically(
        string sourcePath,
        string destinationPath,
        bool replaceEmptyDestination)
        => ((IAtomicFileSystem)fileSystem).ReplaceFileAtomically(
            sourcePath,
            destinationPath,
            replaceEmptyDestination);
}

/// <summary>
/// A host file handle exposing positional I/O over <see cref="RandomAccess"/>.
/// </summary>
public sealed class PhysicalFile : IFile
{
    private readonly SafeFileHandle _handle;
    private readonly string? _pagerLockPath;
    private int _disposed;

    internal PhysicalFile(SafeFileHandle handle, bool readOnly, string? pagerLockPath = null)
    {
        _handle = handle;
        _pagerLockPath = pagerLockPath;
        IsReadOnly = readOnly;
    }

    public bool IsReadOnly { get; }

    public long Length
    {
        get
        {
            ThrowIfDisposed();
            return RandomAccess.GetLength(_handle);
        }
    }

    public int Read(long position, Span<byte> destination)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(position);

        var total = 0;
        while (total < destination.Length)
        {
            var read = RandomAccess.Read(_handle, destination[total..], position + total);
            if (read == 0)
                break;

            total += read;
        }

        return total;
    }

    public void Write(long position, ReadOnlySpan<byte> source)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        if (IsReadOnly)
            throw new InvalidOperationException("Cannot write to a file opened read-only.");

        // The span overload of RandomAccess.Write writes the entire buffer or throws.
        RandomAccess.Write(_handle, source, position);
    }

    public void SetLength(long length)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (IsReadOnly)
            throw new InvalidOperationException("Cannot resize a file opened read-only.");

        RandomAccess.SetLength(_handle, length);
    }

    public void FlushToDisk()
    {
        ThrowIfDisposed();
        if (IsReadOnly)
            return;

        RandomAccess.FlushToDisk(_handle);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (_pagerLockPath is not null
            && SqliteManagedFileOwnershipRegistry.TryDeferPagerHandleClose(_pagerLockPath, _handle))
        {
            return;
        }

        _handle.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(PhysicalFile));
    }
}
