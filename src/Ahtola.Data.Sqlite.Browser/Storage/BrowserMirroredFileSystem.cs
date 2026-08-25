using Ahtola.Core.Storage;

namespace Ahtola.Data.Sqlite.Browser.Storage;

/// <summary>
/// Presents a browser persistent store as synchronous in-memory storage to the
/// managed engine and replays its exact mutations asynchronously at statement
/// boundaries, optionally encrypting them on the way out.
/// </summary>
internal sealed class BrowserMirroredFileSystem :
    IFileSystem,
    IAtomicFileSystem,
    ITemporaryFileSystem,
    IStoragePathResolver,
    ISnapshotFileIdentity,
    IPageCodecSource,
    IMvccJournalModePolicy,
    IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly InMemoryFileSystem _memory = new();
    private readonly CountingBrowserPersistentStore _persistent;
    private readonly BrowserEncryptedPersistence? _encryption;
    private readonly AhtolaBrowserReservedSpaceCodec? _reservedSpaceCodec;
    private readonly string _rootDirectory;
    private readonly bool _ownsPersistent;
    private List<Operation> _pending = [];
    private int _flushesInFlight;
    private int _operationsInFlight;
    private int _disposed;

    private BrowserMirroredFileSystem(
        IBrowserPersistentStore persistent,
        string rootDirectory,
        bool ownsPersistent,
        BrowserEncryptedPersistence? encryption)
    {
        _persistent = new CountingBrowserPersistentStore(persistent);
        _rootDirectory = Normalize(rootDirectory);
        _ownsPersistent = ownsPersistent;
        _encryption = encryption;
        _reservedSpaceCodec = encryption is null ? null : new AhtolaBrowserReservedSpaceCodec(encryption.Cipher);
        encryption?.SetBasePathProbe(_memory.FileExists);
    }

    public StringComparer PathComparer => StringComparer.Ordinal;

    /// <summary>
    /// Forces the managed engine to reserve the bytes AHTLA encryption metadata
    /// needs while it keeps reading and writing plaintext pages. Encryption itself
    /// happens asynchronously on the way to OPFS.
    /// </summary>
    IPageCodec? IPageCodecSource.PageCodec => _reservedSpaceCodec;

    /// <summary>
    /// Refuses <c>PRAGMA journal_mode=mvcc</c> for a database whose cipher cannot
    /// frame an <c>MVTX</c> logical-log chunk.
    /// </summary>
    /// <remarks>
    /// The browser encrypts on its way to the persistent store rather than
    /// through <see cref="AhtolaEncryptionFileSystem"/>, so the core's own
    /// logical-log encryption check cannot see this cipher. Declaring it here
    /// makes the pragma fail before header 255 is persisted and before any
    /// commit produces a frame the flush path could not encrypt — matching the
    /// desktop engine, which refuses the same ciphers when it builds its
    /// logical-log AEAD.
    /// </remarks>
    string? IMvccJournalModePolicy.DescribeMvccUnsupportedReason()
        => _encryption is null
            ? null
            : Core.Mvcc.MvccLogicalLog.DescribeMvccUnsupportedCipher(_encryption.Cipher);

    internal bool HasPendingMutations
    {
        get
        {
            lock (_gate)
                return _pending.Count != 0;
        }
    }

    /// <summary>
    /// Whether the durable store is still owed work, either queued or already
    /// handed to an in-flight flush. Synchronous teardown and the read-only flush
    /// fast path both key on this rather than on the queue alone, so a concurrent
    /// flush is never mistaken for a settled mirror.
    /// </summary>
    internal bool HasUnflushedWork
    {
        get
        {
            lock (_gate)
                return HasUnflushedWorkLocked;
        }
    }

    private bool HasUnflushedWorkLocked => _pending.Count != 0 || _flushesInFlight != 0;

    /// <summary>Whether this mirror has been disposed.</summary>
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// The number of operations this mirror has issued to the durable store, which
    /// in a browser is the number of OPFS worker round trips. Supported synchronous
    /// reads never change it because they are served from managed memory.
    /// </summary>
    internal long PersistentOperationCount => _persistent.OperationCount;

    /// <summary>Snapshots the mirror's durable-transport counters.</summary>
    /// <remarks>
    /// The owed-work count deliberately includes operations already handed to a running flush.
    /// Reporting only the queue would say "nothing pending" for the entire duration of a flush —
    /// exactly the window in which the durable store is owed the most — and would contradict
    /// <see cref="HasUnflushedWork"/>, which synchronous teardown fails closed on.
    /// </remarks>
    internal BrowserMirrorMetrics GetMetrics()
    {
        lock (_gate)
        {
            return new BrowserMirrorMetrics(
                _persistent.OperationCount,
                _pending.Count,
                _operationsInFlight,
                HasUnflushedWorkLocked);
        }
    }

    internal static async ValueTask<BrowserMirroredFileSystem> CreateAsync(
        IBrowserPersistentStore persistent,
        string rootDirectory,
        bool ownsPersistent = false,
        BrowserEncryptedPersistence? encryption = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persistent);
        var fileSystem = new BrowserMirroredFileSystem(
            persistent,
            rootDirectory,
            ownsPersistent,
            encryption);
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

    bool ISnapshotFileIdentity.CanProveDistinctFile(
        string path,
        IFileSystem otherFileSystem,
        string otherPath)
        => otherFileSystem is BrowserMirroredFileSystem other
           && !PathComparer.Equals(
               GetCanonicalPath(path),
               other.GetCanonicalPath(otherPath));

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

    internal ValueTask FlushPendingAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // A read-only workload owes the durable store nothing, so it must not pay
        // for an asynchronous state machine, the flush semaphore, or a worker call.
        // In-flight flushes count as owed work so a concurrent flush is never
        // reported as settled.
        if (!HasUnflushedWork)
        {
            return cancellationToken.IsCancellationRequested
                ? ValueTask.FromCanceled(cancellationToken)
                : ValueTask.CompletedTask;
        }

        return FlushPendingCoreAsync(cancellationToken);
    }

    private async ValueTask FlushPendingCoreAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
            _flushesInFlight++;
        try
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
        finally
        {
            lock (_gate)
                _flushesInFlight--;
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

            // Operations leave the queue but not the ledger: until they are written they are
            // still owed to the durable store, and GetMetrics must keep reporting them.
            _operationsInFlight += operations.Count;
        }

        var handles = new Dictionary<string, IAsyncFile>(StringComparer.Ordinal);
        var index = 0;
        Exception? closeError = null;
        var requeued = false;
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
                            _encryption?.NotifyCreated(operation.Path);
                            break;
                        }
                    case OperationKind.Write:
                        {
                            var file = await GetWritableFileAsync(
                                handles,
                                operation.Path,
                                cancellationToken).ConfigureAwait(false);
                            if (_encryption is null || operation.Capture is null)
                            {
                                await file.WriteAsync(
                                    operation.Position,
                                    operation.Bytes!,
                                    cancellationToken).ConfigureAwait(false);
                                break;
                            }

                            var prepared = await _encryption
                                .PrepareAsync(operation.Path, operation.Capture, cancellationToken)
                                .ConfigureAwait(false);
                            if (prepared.Bytes.Length == 0)
                                break;
                            await file.WriteAsync(
                                prepared.Position,
                                prepared.Bytes,
                                cancellationToken).ConfigureAwait(false);
                            if (prepared.PersistedLength is { } persistedLength)
                            {
                                await file.SetLengthAsync(
                                    persistedLength,
                                    cancellationToken).ConfigureAwait(false);
                            }
                            _encryption.CommitWrite(prepared);
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
                            _encryption?.NotifyLengthSet(operation.Path, operation.Position);
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
                        _encryption?.NotifyDeleted(operation.Path);
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
                        _encryption?.NotifyReplaced(operation.Path, operation.DestinationPath!);
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unknown mirrored storage operation {operation.Kind}.");
                }
            }
        }
        catch
        {
            // Move the unwritten tail back to the queue and out of flight in one step, so the
            // owed-work count never double-counts or momentarily forgets an operation.
            lock (_gate)
            {
                _pending.InsertRange(0, operations.GetRange(index, operations.Count - index));
                _operationsInFlight -= operations.Count;
                requeued = true;
            }

            throw;
        }
        finally
        {
            if (!requeued)
            {
                lock (_gate)
                    _operationsInFlight -= operations.Count;
            }

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
        if (_encryption is null)
        {
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Materialize(path, await ReadPersistentImageAsync(path, cancellationToken).ConfigureAwait(false));
            }

            return;
        }

        // Abandoned VACUUM and page-migration temporaries must never be decrypted:
        // they can be preallocated or half-written, and failing on one would block
        // opening an otherwise healthy database.
        var probes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BrowserPersistedFileRoles.IsTransientArtifact(path))
                continue;
            probes[path] = await ReadPersistentHeaderAsync(path, cancellationToken).ConfigureAwait(false);
        }

        var plan = _encryption.PlanLoad(
            paths,
            path => probes.TryGetValue(path, out var probe) ? probe : []);
        foreach (var artifact in plan.TransientArtifacts)
        {
            await _persistent
                .DeleteFileAsync(artifact, cancellationToken)
                .ConfigureAwait(false);
        }

        var images = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (path, _) in plan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            images[path] = await ReadPersistentImageAsync(path, cancellationToken).ConfigureAwait(false);
        }

        var plaintext = await _encryption
            .DecryptLoadedImagesAsync(plan, images, cancellationToken)
            .ConfigureAwait(false);
        foreach (var (path, _) in plan.Files)
            Materialize(path, plaintext[path]);
    }

    private void Materialize(string path, byte[] image)
    {
        using var destination = _memory.OpenFile(path, FileOpenMode.CreateNew);
        destination.SetLength(image.LongLength);
        if (image.Length != 0)
            destination.Write(0, image);
    }

    private async ValueTask<byte[]> ReadPersistentHeaderAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var source = await _persistent
            .OpenFileAsync(path, FileOpenMode.OpenExisting, readOnly: true, cancellationToken)
            .ConfigureAwait(false);
        var length = await source.GetLengthAsync(cancellationToken).ConfigureAwait(false);
        if (length == 0)
            return [];

        var probe = new byte[Math.Min(length, 16)];
        var read = await source.ReadAsync(0, probe, cancellationToken).ConfigureAwait(false);
        return read == probe.Length ? probe : probe.AsSpan(0, Math.Max(read, 0)).ToArray();
    }

    private async ValueTask<byte[]> ReadPersistentImageAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var source = await _persistent
            .OpenFileAsync(
                path,
                FileOpenMode.OpenExisting,
                readOnly: true,
                cancellationToken)
            .ConfigureAwait(false);
        var length = await source.GetLengthAsync(cancellationToken).ConfigureAwait(false);
        var image = new byte[checked((int)length)];
        var position = 0;
        while (position < image.Length)
        {
            var read = await source
                .ReadAsync(position, image.AsMemory(position), cancellationToken)
                .ConfigureAwait(false);
            if (read <= 0)
            {
                throw new InvalidDataException(
                    $"OPFS file '{path}' was truncated while loading the managed database.");
            }
            position += read;
        }

        return image;
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
        bool ReplaceEmptyDestination = false,
        BrowserPlaintextCapture? Capture = null)
    {
        public static Operation Create(string path) => new(OperationKind.Create, path);

        public static Operation Write(string path, long position, ReadOnlySpan<byte> source)
            => new(OperationKind.Write, path, position, source.ToArray());

        public static Operation CapturedWrite(string path, BrowserPlaintextCapture capture)
            => new(OperationKind.Write, path, capture.Position, Capture: capture);

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
            if (!persistMutations)
                return;
            owner.Enqueue(owner._encryption is { } encryption
                ? Operation.CapturedWrite(
                    path,
                    encryption.Capture(path, position, source.Length, inner))
                : Operation.Write(path, position, source));
        }

        public void SetLength(long length)
        {
            ThrowIfDisposed();
            inner.SetLength(length);
            if (persistMutations)
            {
                var persistedLength = owner._encryption is { } encryption
                    ? encryption.MapPersistedLength(path, length, inner)
                    : length;
                owner.Enqueue(Operation.SetLength(path, persistedLength));
            }
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
