using System.Runtime.ExceptionServices;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

internal enum DeterministicAsyncIoEventKind
{
    Observed,
    Pending,
    Released,
    Canceled,
    Faulted,
}

internal readonly record struct DeterministicAsyncIoOperation(
    long Id,
    FileSystemOperation Type,
    string Path,
    long Occurrence,
    long PathOccurrence);

internal readonly record struct DeterministicAsyncIoEvent(
    DeterministicAsyncIoOperation Operation,
    DeterministicAsyncIoEventKind Kind);

internal sealed class DeterministicAsyncIoController(bool forceYield)
{
    private readonly object _gate = new();
    private readonly Dictionary<FileSystemOperation, long> _counts = new();
    private readonly Dictionary<(FileSystemOperation Operation, string Path), long> _pathCounts = new();
    private readonly Dictionary<(FileSystemOperation Operation, long Occurrence), Exception> _globalFaults = new();
    private readonly Dictionary<
        (FileSystemOperation Operation, string Path, long Occurrence),
        Exception> _pathFaults = new();
    private readonly List<PendingOperation> _pending = [];
    private readonly List<DeterministicAsyncIoEvent> _history = [];
    private long _nextId;

    internal int PendingYieldCount
    {
        get
        {
            lock (_gate)
                return _pending.Count;
        }
    }

    internal IReadOnlyList<DeterministicAsyncIoOperation> PendingOperations
    {
        get
        {
            lock (_gate)
                return [.. _pending.Select(static item => item.Operation)];
        }
    }

    internal IReadOnlyList<DeterministicAsyncIoEvent> History
    {
        get
        {
            lock (_gate)
                return [.. _history];
        }
    }

    internal void FailNext(FileSystemOperation operation, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_gate)
        {
            var occurrence = _counts.GetValueOrDefault(operation) + 1;
            _globalFaults[(operation, occurrence)] = exception;
        }
    }

    internal void FailOnOccurrence(
        FileSystemOperation operation,
        string path,
        long occurrence,
        Exception exception)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(occurrence, 1);
        ArgumentNullException.ThrowIfNull(exception);
        lock (_gate)
            _pathFaults[(operation, path, occurrence)] = exception;
    }

    internal long GetOperationCount(FileSystemOperation operation)
    {
        lock (_gate)
            return _counts.GetValueOrDefault(operation);
    }

    internal long GetOperationCount(FileSystemOperation operation, string path)
    {
        lock (_gate)
            return _pathCounts.GetValueOrDefault((operation, path));
    }

    internal long ReleaseNext()
    {
        long id;
        lock (_gate)
        {
            if (_pending.Count == 0)
                throw new InvalidOperationException("There are no pending asynchronous I/O operations.");
            id = _pending[0].Operation.Id;
        }

        Release(id);
        return id;
    }

    internal void Release(long operationId)
    {
        PendingOperation pending;
        lock (_gate)
        {
            var index = _pending.FindIndex(item => item.Operation.Id == operationId);
            if (index < 0)
                throw new InvalidOperationException($"Operation {operationId} is not pending.");
            pending = _pending[index];
            _pending.RemoveAt(index);
            _history.Add(new DeterministicAsyncIoEvent(
                pending.Operation,
                DeterministicAsyncIoEventKind.Released));
        }

        pending.Completion.SetResult();
    }

    internal async ValueTask BeforeOperationAsync(
        FileSystemOperation operation,
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        cancellationToken.ThrowIfCancellationRequested();

        DeterministicAsyncIoOperation scheduledOperation;
        TaskCompletionSource? completion = null;
        Exception? fault;
        lock (_gate)
        {
            var occurrence = _counts.GetValueOrDefault(operation) + 1;
            _counts[operation] = occurrence;
            var pathKey = (operation, path);
            var pathOccurrence = _pathCounts.GetValueOrDefault(pathKey) + 1;
            _pathCounts[pathKey] = pathOccurrence;
            scheduledOperation = new DeterministicAsyncIoOperation(
                ++_nextId,
                operation,
                path,
                occurrence,
                pathOccurrence);
            _history.Add(new DeterministicAsyncIoEvent(
                scheduledOperation,
                DeterministicAsyncIoEventKind.Observed));
            if (!_pathFaults.Remove((operation, path, pathOccurrence), out fault))
                _globalFaults.Remove((operation, occurrence), out fault);
            if (forceYield)
            {
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending.Add(new PendingOperation(scheduledOperation, completion));
                _history.Add(new DeterministicAsyncIoEvent(
                    scheduledOperation,
                    DeterministicAsyncIoEventKind.Pending));
            }
        }

        if (completion is not null)
        {
            try
            {
                await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lock (_gate)
                {
                    _history.Add(new DeterministicAsyncIoEvent(
                        scheduledOperation,
                        DeterministicAsyncIoEventKind.Canceled));
                }
                throw;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (fault is not null)
        {
            lock (_gate)
            {
                _history.Add(new DeterministicAsyncIoEvent(
                    scheduledOperation,
                    DeterministicAsyncIoEventKind.Faulted));
            }
            ExceptionDispatchInfo.Capture(fault).Throw();
        }
    }

    private sealed record PendingOperation(
        DeterministicAsyncIoOperation Operation,
        TaskCompletionSource Completion);
}

internal class DeterministicAsyncFileSystem : IAsyncFileSystem
{
    protected DeterministicAsyncFileSystem(
        IAsyncFileSystem inner,
        DeterministicAsyncIoController controller)
    {
        Inner = inner;
        Controller = controller;
    }

    protected IAsyncFileSystem Inner { get; }

    protected DeterministicAsyncIoController Controller { get; }

    internal static IAsyncFileSystem Create(
        IAsyncFileSystem inner,
        DeterministicAsyncIoController controller)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(controller);
        return (inner is IAsyncAtomicFileSystem, inner is IAsyncTemporaryFileSystem) switch
        {
            (true, true) => new AtomicTemporaryFileSystem(inner, controller),
            (true, false) => new AtomicFileSystem(inner, controller),
            (false, true) => new TemporaryFileSystem(inner, controller),
            _ => new DeterministicAsyncFileSystem(inner, controller),
        };
    }

    public async ValueTask<bool> FileExistsAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await Controller.BeforeOperationAsync(
            FileSystemOperation.FileExists,
            path,
            cancellationToken).ConfigureAwait(false);
        return await Inner.FileExistsAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IAsyncFile> OpenFileAsync(
        string path,
        FileOpenMode mode,
        bool readOnly = false,
        CancellationToken cancellationToken = default)
    {
        await Controller.BeforeOperationAsync(
            FileSystemOperation.Open,
            path,
            cancellationToken).ConfigureAwait(false);
        var file = await Inner.OpenFileAsync(path, mode, readOnly, cancellationToken).ConfigureAwait(false);
        return DeterministicAsyncFile.Create(file, Controller, path);
    }

    public async ValueTask DeleteFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await Controller.BeforeOperationAsync(
            FileSystemOperation.Delete,
            path,
            cancellationToken).ConfigureAwait(false);
        await Inner.DeleteFileAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<FileWriteStamp?> GetWriteStampAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await Controller.BeforeOperationAsync(
            FileSystemOperation.GetWriteStamp,
            path,
            cancellationToken).ConfigureAwait(false);
        return await Inner.GetWriteStampAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private sealed class AtomicFileSystem(
        IAsyncFileSystem inner,
        DeterministicAsyncIoController controller) :
        DeterministicAsyncFileSystem(inner, controller),
        IAsyncAtomicFileSystem
    {
        public async ValueTask ReplaceFileAtomicallyAsync(
            string sourcePath,
            string destinationPath,
            bool replaceEmptyDestination,
            CancellationToken cancellationToken = default)
        {
            await Controller.BeforeOperationAsync(
                FileSystemOperation.AtomicReplace,
                destinationPath,
                cancellationToken).ConfigureAwait(false);
            await ((IAsyncAtomicFileSystem)Inner).ReplaceFileAtomicallyAsync(
                sourcePath,
                destinationPath,
                replaceEmptyDestination,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class TemporaryFileSystem(
        IAsyncFileSystem inner,
        DeterministicAsyncIoController controller) :
        DeterministicAsyncFileSystem(inner, controller),
        IAsyncTemporaryFileSystem
    {
        public async ValueTask<IAsyncFile> OpenTemporaryFileAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            await Controller.BeforeOperationAsync(
                FileSystemOperation.OpenTemporary,
                path,
                cancellationToken).ConfigureAwait(false);
            var file = await ((IAsyncTemporaryFileSystem)Inner)
                .OpenTemporaryFileAsync(path, cancellationToken).ConfigureAwait(false);
            return DeterministicAsyncFile.Create(file, Controller, path);
        }
    }

    private sealed class AtomicTemporaryFileSystem(
        IAsyncFileSystem inner,
        DeterministicAsyncIoController controller) :
        DeterministicAsyncFileSystem(inner, controller),
        IAsyncAtomicFileSystem,
        IAsyncTemporaryFileSystem
    {
        public async ValueTask ReplaceFileAtomicallyAsync(
            string sourcePath,
            string destinationPath,
            bool replaceEmptyDestination,
            CancellationToken cancellationToken = default)
        {
            await Controller.BeforeOperationAsync(
                FileSystemOperation.AtomicReplace,
                destinationPath,
                cancellationToken).ConfigureAwait(false);
            await ((IAsyncAtomicFileSystem)Inner).ReplaceFileAtomicallyAsync(
                sourcePath,
                destinationPath,
                replaceEmptyDestination,
                cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<IAsyncFile> OpenTemporaryFileAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            await Controller.BeforeOperationAsync(
                FileSystemOperation.OpenTemporary,
                path,
                cancellationToken).ConfigureAwait(false);
            var file = await ((IAsyncTemporaryFileSystem)Inner)
                .OpenTemporaryFileAsync(path, cancellationToken).ConfigureAwait(false);
            return DeterministicAsyncFile.Create(file, Controller, path);
        }
    }
}

internal class DeterministicAsyncFile : IAsyncFile
{
    protected DeterministicAsyncFile(
        IAsyncFile inner,
        DeterministicAsyncIoController controller,
        string path)
    {
        Inner = inner;
        Controller = controller;
        Path = path;
    }

    protected IAsyncFile Inner { get; }

    protected DeterministicAsyncIoController Controller { get; }

    protected string Path { get; }

    internal static IAsyncFile Create(
        IAsyncFile inner,
        DeterministicAsyncIoController controller,
        string path = "<direct>")
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(controller);
        return inner is IAsyncPageMaterializingFile
            ? new MaterializingFile(inner, controller, path)
            : new DeterministicAsyncFile(inner, controller, path);
    }

    public bool IsReadOnly => Inner.IsReadOnly;

    public async ValueTask<long> GetLengthAsync(CancellationToken cancellationToken = default)
    {
        await Controller.BeforeOperationAsync(
            FileSystemOperation.GetLength,
            Path,
            cancellationToken).ConfigureAwait(false);
        return await Inner.GetLengthAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<int> ReadAsync(
        long position,
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        await Controller.BeforeOperationAsync(
            FileSystemOperation.Read,
            Path,
            cancellationToken).ConfigureAwait(false);
        return await Inner.ReadAsync(position, destination, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteAsync(
        long position,
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken = default)
    {
        await Controller.BeforeOperationAsync(
            FileSystemOperation.Write,
            Path,
            cancellationToken).ConfigureAwait(false);
        await Inner.WriteAsync(position, source, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetLengthAsync(
        long length,
        CancellationToken cancellationToken = default)
    {
        await Controller.BeforeOperationAsync(
            FileSystemOperation.SetLength,
            Path,
            cancellationToken).ConfigureAwait(false);
        await Inner.SetLengthAsync(length, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask FlushToDiskAsync(CancellationToken cancellationToken = default)
    {
        await Controller.BeforeOperationAsync(
            FileSystemOperation.FlushToDisk,
            Path,
            cancellationToken).ConfigureAwait(false);
        await Inner.FlushToDiskAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await Controller.BeforeOperationAsync(
            FileSystemOperation.Dispose,
            Path,
            CancellationToken.None).ConfigureAwait(false);
        await Inner.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class MaterializingFile(
        IAsyncFile inner,
        DeterministicAsyncIoController controller,
        string path) :
        DeterministicAsyncFile(inner, controller, path),
        IAsyncPageMaterializingFile
    {
        public async ValueTask EnsureMaterializedAsync(
            long position,
            int length,
            CancellationToken cancellationToken = default)
        {
            await Controller.BeforeOperationAsync(
                FileSystemOperation.EnsureMaterialized,
                Path,
                cancellationToken).ConfigureAwait(false);
            await ((IAsyncPageMaterializingFile)Inner).EnsureMaterializedAsync(
                position,
                length,
                cancellationToken).ConfigureAwait(false);
        }
    }
}
