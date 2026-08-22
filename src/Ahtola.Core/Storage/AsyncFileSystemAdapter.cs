namespace Ahtola.Core.Storage;

/// <summary>
/// Adapts existing synchronous storage to the orthogonal asynchronous
/// contracts. Operations execute immediately and return completed
/// <see cref="ValueTask"/> instances.
/// </summary>
public static class AsyncFileSystemAdapter
{
    /// <summary>
    /// Wraps <paramref name="fileSystem"/>, preserving its optional atomic and
    /// temporary-file capabilities.
    /// </summary>
    public static IAsyncFileSystem Create(IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        return (
            fileSystem is IAtomicFileSystem,
            fileSystem is ITemporaryFileSystem,
            fileSystem is IStoragePathResolver) switch
        {
            (true, true, true) => new PathAtomicTemporaryAdapter(fileSystem),
            (true, true, false) => new AtomicTemporaryAdapter(fileSystem),
            (true, false, true) => new PathAtomicAdapter(fileSystem),
            (true, false, false) => new AtomicAdapter(fileSystem),
            (false, true, true) => new PathTemporaryAdapter(fileSystem),
            (false, true, false) => new TemporaryAdapter(fileSystem),
            (false, false, true) => new PathAdapter(fileSystem),
            _ => new FileSystemAdapter(fileSystem),
        };
    }

    private class FileSystemAdapter(IFileSystem inner) : IAsyncFileSystem, IAsyncFileSystemBacking
    {
        protected IFileSystem Inner { get; } = inner;

        public IFileSystem BackingFileSystem => Inner;

        public StringComparer PathComparer
            => ((IStoragePathResolver)Inner).PathComparer;

        public string GetCanonicalPath(string path)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            return ((IStoragePathResolver)Inner).GetCanonicalPath(path);
        }

        public ValueTask<bool> FileExistsAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Inner.FileExists(path));
        }

        public ValueTask<IAsyncFile> OpenFileAsync(
            string path,
            FileOpenMode mode,
            bool readOnly = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(AsyncFileAdapter.Create(Inner.OpenFile(path, mode, readOnly)));
        }

        public ValueTask DeleteFileAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Inner.DeleteFile(path);
            return ValueTask.CompletedTask;
        }

        public ValueTask<FileWriteStamp?> GetWriteStampAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Inner.GetWriteStamp(path));
        }
    }

    private class AtomicAdapter(IFileSystem inner) :
        FileSystemAdapter(inner),
        IAsyncAtomicFileSystem
    {
        public ValueTask ReplaceFileAtomicallyAsync(
            string sourcePath,
            string destinationPath,
            bool replaceEmptyDestination,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ((IAtomicFileSystem)Inner).ReplaceFileAtomically(
                sourcePath,
                destinationPath,
                replaceEmptyDestination);
            return ValueTask.CompletedTask;
        }
    }

    private class TemporaryAdapter(IFileSystem inner) :
        FileSystemAdapter(inner),
        IAsyncTemporaryFileSystem
    {
        public ValueTask<IAsyncFile> OpenTemporaryFileAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = ((ITemporaryFileSystem)Inner).OpenTemporaryFile(path);
            return ValueTask.FromResult(AsyncFileAdapter.Create(file));
        }
    }

    private class AtomicTemporaryAdapter(IFileSystem inner) :
        FileSystemAdapter(inner),
        IAsyncAtomicFileSystem,
        IAsyncTemporaryFileSystem
    {
        public ValueTask ReplaceFileAtomicallyAsync(
            string sourcePath,
            string destinationPath,
            bool replaceEmptyDestination,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ((IAtomicFileSystem)Inner).ReplaceFileAtomically(
                sourcePath,
                destinationPath,
                replaceEmptyDestination);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IAsyncFile> OpenTemporaryFileAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = ((ITemporaryFileSystem)Inner).OpenTemporaryFile(path);
            return ValueTask.FromResult(AsyncFileAdapter.Create(file));
        }
    }

    private sealed class PathAdapter(IFileSystem inner) :
        FileSystemAdapter(inner),
        IStoragePathResolver
    {
    }

    private sealed class PathAtomicAdapter(IFileSystem inner) :
        AtomicAdapter(inner),
        IStoragePathResolver
    {
    }

    private sealed class PathTemporaryAdapter(IFileSystem inner) :
        TemporaryAdapter(inner),
        IStoragePathResolver
    {
    }

    private sealed class PathAtomicTemporaryAdapter(IFileSystem inner) :
        AtomicTemporaryAdapter(inner),
        IStoragePathResolver
    {
    }
}

/// <summary>Adapts an existing synchronous positional file.</summary>
public static class AsyncFileAdapter
{
    /// <summary>
    /// Wraps <paramref name="file"/>, preserving its optional page
    /// materialization capability.
    /// </summary>
    public static IAsyncFile Create(IFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return file is IPageMaterializingFile
            ? new MaterializingFileAdapter(file)
            : new FileAdapter(file);
    }

    private class FileAdapter(IFile inner) : IAsyncFile
    {
        protected IFile Inner { get; } = inner;

        public bool IsReadOnly => Inner.IsReadOnly;

        public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Inner.Length);
        }

        public ValueTask<int> ReadAsync(
            long position,
            Memory<byte> destination,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Inner.Read(position, destination.Span));
        }

        public ValueTask WriteAsync(
            long position,
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Inner.Write(position, source.Span);
            return ValueTask.CompletedTask;
        }

        public ValueTask SetLengthAsync(
            long length,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Inner.SetLength(length);
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushToDiskAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Inner.FlushToDisk();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Inner.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MaterializingFileAdapter(IFile inner) :
        FileAdapter(inner),
        IAsyncPageMaterializingFile
    {
        public ValueTask EnsureMaterializedAsync(
            long position,
            int length,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ((IPageMaterializingFile)Inner).EnsureMaterialized(position, length);
            return ValueTask.CompletedTask;
        }
    }
}
