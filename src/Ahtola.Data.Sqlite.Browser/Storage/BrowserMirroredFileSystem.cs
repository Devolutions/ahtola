using System.Runtime.Versioning;
using Ahtola.Core.Storage;

namespace Ahtola.Data.Sqlite.Browser.Storage;

/// <summary>
/// Presents browser OPFS as synchronous in-memory storage to the managed engine
/// and replays its exact mutations asynchronously at statement boundaries.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class BrowserMirroredFileSystem :
    IFileSystem,
    IAtomicFileSystem,
    ITemporaryFileSystem,
    IStoragePathResolver,
    IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly InMemoryFileSystem _memory = new();
    private readonly OpfsAsyncFileSystem _persistent;
    private readonly string _rootDirectory;
    private readonly bool _ownsPersistent;
    private List<Operation> _pending = [];
    private int _disposed;

    private BrowserMirroredFileSystem(
        OpfsAsyncFileSystem persistent,
        string rootDirectory,
        bool ownsPersistent)
    {
        _persistent = persistent;
        _rootDirectory = Normalize(rootDirectory);
        _ownsPersistent = ownsPersistent;
    }

    public StringComparer PathComparer => StringComparer.Ordinal;

    internal static async ValueTask<BrowserMirroredFileSystem> CreateAsync(
        OpfsAsyncFileSystem persistent,
        string rootDirectory,
        bool ownsPersistent = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persistent);
        var fileSystem = new BrowserMirroredFileSystem(
            persistent,
            rootDirectory,
            ownsPersistent);
        try
        {
            await fileSystem.LoadAsync(cancellationToken).ConfigureAwait(false);
            return fileSystem;
        }
        catch
        {
            await fileSystem.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public string GetCanonicalPath(string path) => ValidateOwnedPath(path);

    public bool FileExists(string path)
    {
        ThrowIfDisposed();
        return _memory.FileExists(ValidateOwnedPath(path));
    }

    public FileWriteStamp? GetWriteStamp(string path)
    {
        ThrowIfDisposed();
        return ((IFileSystem)_memory).GetWriteStamp(ValidateOwnedPath(path));
    }

    public IFile OpenFile(string path, FileOpenMode mode, bool readOnly = false)
    {
        ThrowIfDisposed();
        var canonicalPath = ValidateOwnedPath(path);
        var existed = _memory.FileExists(canonicalPath);
        var file = _memory.OpenFile(canonicalPath, mode, readOnly);
        if (!readOnly && !existed)
            Enqueue(Operation.Create(canonicalPath));
        return new MirroredFile(
            this,
            canonicalPath,
            file,
            persistMutations: true,
            deleteOnClose: false);
    }

    IFile ITemporaryFileSystem.OpenTemporaryFile(string path)
    {
        ThrowIfDisposed();
        var canonicalPath = ValidateOwnedPath(path);
        var file = _memory.OpenFile(canonicalPath, FileOpenMode.CreateNew);
        return new MirroredFile(
            this,
            canonicalPath,
            file,
            persistMutations: false,
            deleteOnClose: true);
    }

    public void DeleteFile(string path)
    {
        ThrowIfDisposed();
        var canonicalPath = ValidateOwnedPath(path);
        _memory.DeleteFile(canonicalPath);
        Enqueue(Operation.Delete(canonicalPath));
    }

    void IAtomicFileSystem.ReplaceFileAtomically(
        string sourcePath,
        string destinationPath,
        bool replaceEmptyDestination)
    {
        ThrowIfDisposed();
        var source = ValidateOwnedPath(sourcePath);
        var destination = ValidateOwnedPath(destinationPath);
        ((IAtomicFileSystem)_memory).ReplaceFileAtomically(
            source,
            destination,
            replaceEmptyDestination);
        Enqueue(Operation.Replace(source, destination, replaceEmptyDestination));
    }

    internal async ValueTask FlushPendingAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await FlushPendingCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask FlushPendingCoreAsync(CancellationToken cancellationToken)
    {
        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReplayPendingAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private async ValueTask ReplayPendingAsync(CancellationToken cancellationToken)
    {
        List<Operation> operations;
        lock (_gate)
        {
            if (_pending.Count == 0)
                return;
            operations = _pending;
            _pending = [];
        }

        var handles = new Dictionary<string, IAsyncFile>(StringComparer.Ordinal);
        var index = 0;
        Exception? closeError = null;
        try
        {
            for (; index < operations.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var operation = operations[index];
                switch (operation.Kind)
                {
                    case OperationKind.Create:
                    {
                        var file = await GetWritableFileAsync(
                            handles,
                            operation.Path,
                            cancellationToken).ConfigureAwait(false);
                        await file.SetLengthAsync(0, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    case OperationKind.Write:
                    {
                        var file = await GetWritableFileAsync(
                            handles,
                            operation.Path,
                            cancellationToken).ConfigureAwait(false);
                        await file.WriteAsync(
                            operation.Position,
                            operation.Bytes!,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    case OperationKind.SetLength:
                    {
                        var file = await GetWritableFileAsync(
                            handles,
                            operation.Path,
                            cancellationToken).ConfigureAwait(false);
                        await file.SetLengthAsync(
                            operation.Position,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    case OperationKind.Flush:
                    {
                        var file = await GetWritableFileAsync(
                            handles,
                            operation.Path,
                            cancellationToken).ConfigureAwait(false);
                        await file.FlushToDiskAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    case OperationKind.Delete:
                        await CloseHandleAsync(handles, operation.Path).ConfigureAwait(false);
                        await _persistent
                            .DeleteFileAsync(operation.Path, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case OperationKind.Replace:
                        await CloseHandleAsync(handles, operation.Path).ConfigureAwait(false);
                        await CloseHandleAsync(handles, operation.DestinationPath!).ConfigureAwait(false);
                        await _persistent
                            .ReplaceFileAtomicallyAsync(
                                operation.Path,
                                operation.DestinationPath!,
                                operation.ReplaceEmptyDestination,
                                cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unknown mirrored storage operation {operation.Kind}.");
                }
            }
        }
        catch
        {
            lock (_gate)
                _pending.InsertRange(0, operations.GetRange(index, operations.Count - index));
            throw;
        }
        finally
        {
            foreach (var file in handles.Values)
            {
                try
                {
                    await file.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    closeError ??= exception;
                }
            }
        }

        if (closeError is not null)
            throw closeError;
    }

    internal async ValueTask DeleteAllPersistentFilesAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await FlushPendingAsync(cancellationToken).ConfigureAwait(false);
        var paths = await _persistent
            .ListFilesAsync(_rootDirectory, cancellationToken)
            .ConfigureAwait(false);
        foreach (var path in paths)
        {
            await _persistent
                .DeleteFileAsync(path, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            await FlushPendingCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _flushGate.Dispose();
            if (_ownsPersistent)
                await _persistent.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask LoadAsync(CancellationToken cancellationToken)
    {
        var paths = await _persistent
            .ListFilesAsync(_rootDirectory, cancellationToken)
            .ConfigureAwait(false);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var source = await _persistent
                .OpenFileAsync(
                    path,
                    FileOpenMode.OpenExisting,
                    readOnly: true,
                    cancellationToken)
                .ConfigureAwait(false);
            var length = await source.GetLengthAsync(cancellationToken).ConfigureAwait(false);
            using var destination = _memory.OpenFile(path, FileOpenMode.CreateNew);
            destination.SetLength(length);
            var buffer = new byte[1024 * 1024];
            var position = 0L;
            while (position < length)
            {
                var count = checked((int)Math.Min(buffer.Length, length - position));
                var read = await source
                    .ReadAsync(position, buffer.AsMemory(0, count), cancellationToken)
                    .ConfigureAwait(false);
                if (read != count)
                {
                    throw new InvalidDataException(
                        $"OPFS file '{path}' was truncated while loading the managed database.");
                }
                destination.Write(position, buffer.AsSpan(0, count));
                position += count;
            }
        }
    }

    private async ValueTask<IAsyncFile> GetWritableFileAsync(
        Dictionary<string, IAsyncFile> handles,
        string path,
        CancellationToken cancellationToken)
    {
        if (handles.TryGetValue(path, out var file))
            return file;
        file = await _persistent
            .OpenFileAsync(
                path,
                FileOpenMode.OpenOrCreate,
                readOnly: false,
                cancellationToken)
            .ConfigureAwait(false);
        handles.Add(path, file);
        return file;
    }

    private static async ValueTask CloseHandleAsync(
        Dictionary<string, IAsyncFile> handles,
        string path)
    {
        if (!handles.Remove(path, out var file))
            return;
        await file.DisposeAsync().ConfigureAwait(false);
    }

    private void Enqueue(Operation operation)
    {
        lock (_gate)
            _pending.Add(operation);
    }

    private string ValidateOwnedPath(string path)
    {
        var normalized = Normalize(path);
        if (!normalized.StartsWith(_rootDirectory + "/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Browser database path '{normalized}' is outside owned directory '{_rootDirectory}'.");
        }
        return normalized;
    }

    private static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Replace('\\', '/').Trim('/');
        var segments = normalized.Split('/');
        if (segments.Any(static segment =>
                segment is "" or "." or ".."
                || segment.IndexOfAny(['\0', '\r', '\n']) >= 0))
        {
            throw new ArgumentException("Browser database paths must be relative and normalized.", nameof(path));
        }
        return string.Join('/', segments);
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private enum OperationKind
    {
        Create,
        Write,
        SetLength,
        Flush,
        Delete,
        Replace,
    }

    private sealed record Operation(
        OperationKind Kind,
        string Path,
        long Position = 0,
        byte[]? Bytes = null,
        string? DestinationPath = null,
        bool ReplaceEmptyDestination = false)
    {
        public static Operation Create(string path) => new(OperationKind.Create, path);

        public static Operation Write(string path, long position, ReadOnlySpan<byte> source)
            => new(OperationKind.Write, path, position, source.ToArray());

        public static Operation SetLength(string path, long length)
            => new(OperationKind.SetLength, path, length);

        public static Operation Flush(string path) => new(OperationKind.Flush, path);

        public static Operation Delete(string path) => new(OperationKind.Delete, path);

        public static Operation Replace(
            string sourcePath,
            string destinationPath,
            bool replaceEmptyDestination)
            => new(
                OperationKind.Replace,
                sourcePath,
                DestinationPath: destinationPath,
                ReplaceEmptyDestination: replaceEmptyDestination);
    }

    private sealed class MirroredFile(
        BrowserMirroredFileSystem owner,
        string path,
        IFile inner,
        bool persistMutations,
        bool deleteOnClose) : IFile
    {
        private int _disposed;

        public long Length => inner.Length;

        public bool IsReadOnly => inner.IsReadOnly;

        public int Read(long position, Span<byte> destination)
        {
            ThrowIfDisposed();
            return inner.Read(position, destination);
        }

        public void Write(long position, ReadOnlySpan<byte> source)
        {
            ThrowIfDisposed();
            inner.Write(position, source);
            if (persistMutations)
                owner.Enqueue(Operation.Write(path, position, source));
        }

        public void SetLength(long length)
        {
            ThrowIfDisposed();
            inner.SetLength(length);
            if (persistMutations)
                owner.Enqueue(Operation.SetLength(path, length));
        }

        public void FlushToDisk()
        {
            ThrowIfDisposed();
            inner.FlushToDisk();
            if (persistMutations)
                owner.Enqueue(Operation.Flush(path));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            inner.Dispose();
            if (deleteOnClose)
                owner._memory.DeleteFile(path);
        }

        private void ThrowIfDisposed()
            => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
