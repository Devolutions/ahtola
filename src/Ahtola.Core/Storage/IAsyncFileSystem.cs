namespace Ahtola.Core.Storage;

/// <summary>
/// Asynchronous positional storage that does not require a browser backend to
/// provide synchronous file APIs.
/// </summary>
public interface IAsyncFileSystem
{
    /// <summary>Returns whether a file exists at <paramref name="path"/>.</summary>
    ValueTask<bool> FileExistsAsync(
        string path,
        CancellationToken cancellationToken = default);

    /// <summary>Opens a file for positional asynchronous access.</summary>
    ValueTask<IAsyncFile> OpenFileAsync(
        string path,
        FileOpenMode mode,
        bool readOnly = false,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes the file at <paramref name="path"/> if it exists.</summary>
    ValueTask DeleteFileAsync(
        string path,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the current write stamp, or <see langword="null"/> when the file
    /// does not exist or the backend cannot observe write activity.
    /// </summary>
    ValueTask<FileWriteStamp?> GetWriteStampAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<FileWriteStamp?>(null);
    }
}

/// <summary>
/// Identifies an asynchronous file system that merely adapts a synchronous
/// backend. Pager locking keys on the adapted backend rather than on wrapper
/// identity so two adapters over one storage still share a single writer lock.
/// </summary>
internal interface IAsyncFileSystemBacking
{
    /// <summary>The synchronous storage this asynchronous facade forwards to.</summary>
    IFileSystem BackingFileSystem { get; }
}

/// <summary>
/// Optional capability for asynchronously publishing a fully written sibling
/// file without exposing a partial destination image.
/// </summary>
public interface IAsyncAtomicFileSystem
{
    ValueTask ReplaceFileAtomicallyAsync(
        string sourcePath,
        string destinationPath,
        bool replaceEmptyDestination,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional capability for opening a host-managed temporary file.
/// </summary>
public interface IAsyncTemporaryFileSystem
{
    ValueTask<IAsyncFile> OpenTemporaryFileAsync(
        string path,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A positionally addressed asynchronous file handle.
/// </summary>
public interface IAsyncFile : IAsyncDisposable
{
    /// <summary>Whether this handle was opened read-only.</summary>
    bool IsReadOnly { get; }

    /// <summary>Returns the current file length.</summary>
    ValueTask<long> GetLengthAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads into <paramref name="destination"/> at <paramref name="position"/>.
    /// A short result indicates end-of-file.
    /// </summary>
    ValueTask<int> ReadAsync(
        long position,
        Memory<byte> destination,
        CancellationToken cancellationToken = default);

    /// <summary>Writes all bytes from <paramref name="source"/>.</summary>
    ValueTask WriteAsync(
        long position,
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken = default);

    /// <summary>Truncates or zero-extends the file to <paramref name="length"/>.</summary>
    ValueTask SetLengthAsync(
        long length,
        CancellationToken cancellationToken = default);

    /// <summary>Flushes buffered data and metadata to durable storage.</summary>
    ValueTask FlushToDiskAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional capability for a sparse asynchronous file whose missing ranges
/// must be materialized before reading.
/// </summary>
public interface IAsyncPageMaterializingFile
{
    ValueTask EnsureMaterializedAsync(
        long position,
        int length,
        CancellationToken cancellationToken = default);
}
