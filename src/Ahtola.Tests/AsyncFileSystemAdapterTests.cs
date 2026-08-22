using System.Runtime.ExceptionServices;
using AwesomeAssertions;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class AsyncFileSystemAdapterTests
{
    [Test]
    public async Task AdapterCompletesInlineAndPreservesFileSemantics()
    {
        var faults = new DeterministicFaultInjector();
        var synchronous = new InMemoryFileSystem(faults);
        var fileSystem = AsyncFileSystemAdapter.Create(synchronous);

        var exists = fileSystem.FileExistsAsync("inline.db");
        exists.IsCompletedSuccessfully.Should().BeTrue();
        (await exists).Should().BeFalse();

        var open = fileSystem.OpenFileAsync("inline.db", FileOpenMode.CreateNew);
        open.IsCompletedSuccessfully.Should().BeTrue();
        await using var file = await open;

        var write = file.WriteAsync(0, new byte[] { 1, 2, 3, 4, 5 });
        write.IsCompletedSuccessfully.Should().BeTrue();
        await write;

        var flush = file.FlushToDiskAsync();
        flush.IsCompletedSuccessfully.Should().BeTrue();
        await flush;

        var length = file.GetLengthAsync();
        length.IsCompletedSuccessfully.Should().BeTrue();
        (await length).Should().Be(5);

        var destination = Enumerable.Repeat((byte)0xCC, 8).ToArray();
        var read = file.ReadAsync(0, destination);
        read.IsCompletedSuccessfully.Should().BeTrue();
        (await read).Should().Be(5);
        destination.Should().Equal(1, 2, 3, 4, 5, 0xCC, 0xCC, 0xCC);

        var truncate = file.SetLengthAsync(2);
        truncate.IsCompletedSuccessfully.Should().BeTrue();
        await truncate;
        (await file.GetLengthAsync()).Should().Be(2);

        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(1);
        faults.GetOperationCount(FileSystemOperation.Read).Should().Be(1);
        faults.GetOperationCount(FileSystemOperation.FlushToDisk).Should().Be(1);
        faults.GetOperationCount(FileSystemOperation.SetLength).Should().Be(1);

        var delete = fileSystem.DeleteFileAsync("inline.db");
        delete.IsCompletedSuccessfully.Should().BeTrue();
        await delete;
        synchronous.FileExists("inline.db").Should().BeFalse();
    }

    [Test]
    public async Task AdapterPreservesAtomicReplacementCapabilityAndSemantics()
    {
        var synchronous = new InMemoryFileSystem();
        using (var source = synchronous.OpenFile("source.db", FileOpenMode.CreateNew))
        {
            source.Write(0, new byte[] { 4, 2 });
        }

        using (synchronous.OpenFile("destination.db", FileOpenMode.CreateNew))
        {
        }

        var fileSystem = AsyncFileSystemAdapter.Create(synchronous);
        var atomic = fileSystem.Should().BeAssignableTo<IAsyncAtomicFileSystem>().Subject;
        var replace = atomic.ReplaceFileAtomicallyAsync(
            "source.db",
            "destination.db",
            replaceEmptyDestination: true);

        replace.IsCompletedSuccessfully.Should().BeTrue();
        await replace;
        synchronous.FileExists("source.db").Should().BeFalse();

        await using var destination = await fileSystem.OpenFileAsync(
            "destination.db",
            FileOpenMode.OpenExisting);
        var bytes = new byte[2];
        (await destination.ReadAsync(0, bytes)).Should().Be(2);
        bytes.Should().Equal(4, 2);
    }

    [Test]
    public async Task AdapterPreservesTemporaryFileDeleteOnCloseWhereSupported()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ahtola-async-{Guid.NewGuid():N}.tmp");
        var fileSystem = AsyncFileSystemAdapter.Create(PhysicalFileSystem.Instance);
        var temporary = fileSystem.Should().BeAssignableTo<IAsyncTemporaryFileSystem>().Subject;

        try
        {
            var open = temporary.OpenTemporaryFileAsync(path);
            open.IsCompletedSuccessfully.Should().BeTrue();
            var file = await open;
            await file.WriteAsync(0, new byte[] { 9 });
            var dispose = file.DisposeAsync();
            dispose.IsCompletedSuccessfully.Should().BeTrue();
            await dispose;

            File.Exists(path).Should().BeFalse();
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task AdapterPreservesPageMaterializationCapability()
    {
        var synchronous = new MaterializingFile();
        var file = AsyncFileAdapter.Create(synchronous);
        var materializing = file.Should().BeAssignableTo<IAsyncPageMaterializingFile>().Subject;

        var operation = materializing.EnsureMaterializedAsync(4096, 1024);

        operation.IsCompletedSuccessfully.Should().BeTrue();
        await operation;
        synchronous.MaterializedRange.Should().Be((4096L, 1024));
    }

    [Test]
    public void AdapterHonorsCancellationBeforeCallingSynchronousStorage()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = AsyncFileSystemAdapter.Create(new InMemoryFileSystem(faults));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = Assert.Throws<OperationCanceledException>(
            () => fileSystem.OpenFileAsync(
                "cancelled.db",
                FileOpenMode.OpenOrCreate,
                cancellationToken: cancellation.Token));

        exception.CancellationToken.Should().Be(cancellation.Token);
        faults.GetOperationCount(FileSystemOperation.Open).Should().Be(0);
    }

    [Test]
    public void AdapterPropagatesExactExceptionsWithoutWrapping()
    {
        var expectedDelete = new IOException("delete failed");
        var fileSystem = AsyncFileSystemAdapter.Create(new ThrowingFileSystem(expectedDelete));
        fileSystem.Should().NotBeAssignableTo<IStoragePathResolver>();
        fileSystem.Should().NotBeAssignableTo<IAsyncAtomicFileSystem>();
        fileSystem.Should().NotBeAssignableTo<IAsyncTemporaryFileSystem>();

        var delete = Assert.Throws<IOException>(() => fileSystem.DeleteFileAsync("fault.db"));
        delete.Should().BeSameAs(expectedDelete);

        var expectedWrite = new InvalidDataException("write failed");
        var file = AsyncFileAdapter.Create(new ThrowingFile(expectedWrite, disposeException: null));
        var write = Assert.Throws<InvalidDataException>(
            () => file.WriteAsync(0, new byte[] { 1 }));
        write.Should().BeSameAs(expectedWrite);

        var expectedDispose = new IOException("dispose failed");
        var disposable = AsyncFileAdapter.Create(new ThrowingFile(writeException: null, expectedDispose));
        var dispose = Assert.Throws<IOException>(() => disposable.DisposeAsync());
        dispose.Should().BeSameAs(expectedDispose);
    }

    [Test]
    public void AdapterPreservesCanonicalPathCapability()
    {
        var logical = AsyncFileSystemAdapter.Create(new InMemoryFileSystem())
            .Should().BeAssignableTo<IStoragePathResolver>().Subject;
        logical.GetCanonicalPath("folder/database.db").Should().Be("folder/database.db");
        logical.PathComparer.Should().BeSameAs(StringComparer.Ordinal);

        var physical = AsyncFileSystemAdapter.Create(PhysicalFileSystem.Instance)
            .Should().BeAssignableTo<IStoragePathResolver>().Subject;
        physical.GetCanonicalPath("database.db").Should().Be(Path.GetFullPath("database.db"));
        physical.PathComparer.Equals("A", "a").Should().Be(
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());
    }

    private sealed class ThrowingFileSystem(Exception deleteException) : IFileSystem
    {
        public bool FileExists(string path) => false;

        public IFile OpenFile(string path, FileOpenMode mode, bool readOnly = false)
            => throw new NotSupportedException();

        public void DeleteFile(string path) => ExceptionDispatchInfo.Capture(deleteException).Throw();
    }

    private sealed class ThrowingFile(
        Exception? writeException,
        Exception? disposeException) : IFile
    {
        public long Length => 0;

        public bool IsReadOnly => false;

        public int Read(long position, Span<byte> destination) => 0;

        public void Write(long position, ReadOnlySpan<byte> source)
        {
            if (writeException is not null)
                ExceptionDispatchInfo.Capture(writeException).Throw();
        }

        public void SetLength(long length)
        {
        }

        public void FlushToDisk()
        {
        }

        public void Dispose()
        {
            if (disposeException is not null)
                ExceptionDispatchInfo.Capture(disposeException).Throw();
        }
    }

    internal sealed class MaterializingFile : IFile, IPageMaterializingFile
    {
        internal (long Position, int Length)? MaterializedRange { get; private set; }

        public long Length => 0;

        public bool IsReadOnly => false;

        public int Read(long position, Span<byte> destination) => 0;

        public void Write(long position, ReadOnlySpan<byte> source)
        {
        }

        public void SetLength(long length)
        {
        }

        public void FlushToDisk()
        {
        }

        public void EnsureMaterialized(long position, int length)
            => MaterializedRange = (position, length);

        public void Dispose()
        {
        }
    }
}

public sealed class DeterministicAsyncFileSystemTests
{
    [Test]
    public async Task ForcedCompletionsSuspendEveryCoreFileOperation()
    {
        var controller = new DeterministicAsyncIoController(forceYield: true);
        var fileSystem = DeterministicAsyncFileSystem.Create(
            AsyncFileSystemAdapter.Create(new InMemoryFileSystem()),
            controller);

        (await CompleteYieldAsync(
            fileSystem.FileExistsAsync("yield.db"),
            controller)).Should().BeFalse();
        (await CompleteYieldAsync(
            fileSystem.GetWriteStampAsync("yield.db"),
            controller)).Should().BeNull();

        var file = await CompleteYieldAsync(
            fileSystem.OpenFileAsync("yield.db", FileOpenMode.CreateNew),
            controller);
        fileSystem.Should().NotBeAssignableTo<IAsyncTemporaryFileSystem>();
        file.Should().NotBeAssignableTo<IAsyncPageMaterializingFile>();
        await CompleteYieldAsync(file.WriteAsync(0, new byte[] { 1, 2, 3 }), controller);
        (await CompleteYieldAsync(file.GetLengthAsync(), controller)).Should().Be(3);

        var bytes = new byte[4];
        (await CompleteYieldAsync(file.ReadAsync(0, bytes), controller)).Should().Be(3);
        await CompleteYieldAsync(file.SetLengthAsync(1), controller);
        await CompleteYieldAsync(file.FlushToDiskAsync(), controller);
        await CompleteYieldAsync(file.DisposeAsync(), controller);
        await CompleteYieldAsync(fileSystem.DeleteFileAsync("yield.db"), controller);

        controller.GetOperationCount(FileSystemOperation.FileExists).Should().Be(1);
        controller.GetOperationCount(FileSystemOperation.GetWriteStamp).Should().Be(1);
        controller.GetOperationCount(FileSystemOperation.Open).Should().Be(1);
        controller.GetOperationCount(FileSystemOperation.Write).Should().Be(1);
        controller.GetOperationCount(FileSystemOperation.GetLength).Should().Be(1);
        controller.GetOperationCount(FileSystemOperation.Read).Should().Be(1);
        controller.GetOperationCount(FileSystemOperation.SetLength).Should().Be(1);
        controller.GetOperationCount(FileSystemOperation.FlushToDisk).Should().Be(1);
        controller.GetOperationCount(FileSystemOperation.Dispose).Should().Be(1);
        controller.GetOperationCount(FileSystemOperation.Delete).Should().Be(1);
    }

    [Test]
    public async Task ForcedCompletionHonorsCancellationWhileSuspended()
    {
        var controller = new DeterministicAsyncIoController(forceYield: true);
        var fileSystem = DeterministicAsyncFileSystem.Create(
            AsyncFileSystemAdapter.Create(new InMemoryFileSystem()),
            controller);
        using var cancellation = new CancellationTokenSource();

        var pending = fileSystem.FileExistsAsync("cancel.db", cancellation.Token);
        pending.IsCompleted.Should().BeFalse();
        controller.PendingYieldCount.Should().Be(1);
        cancellation.Cancel();

        var exception = Assert.CatchAsync<OperationCanceledException>(
            async () => await pending);
        exception.CancellationToken.Should().Be(cancellation.Token);
        controller.ReleaseNext();
    }

    [Test]
    public async Task TargetedFailureIsRaisedAfterYieldAndPreservesExactException()
    {
        var expected = new IOException("targeted write failure");
        var controller = new DeterministicAsyncIoController(forceYield: true);
        controller.FailNext(FileSystemOperation.Write, expected);
        var fileSystem = DeterministicAsyncFileSystem.Create(
            AsyncFileSystemAdapter.Create(new InMemoryFileSystem()),
            controller);
        var file = await CompleteYieldAsync(
            fileSystem.OpenFileAsync("fault.db", FileOpenMode.CreateNew),
            controller);

        var pending = file.WriteAsync(0, new byte[] { 7 });
        pending.IsCompleted.Should().BeFalse();
        controller.ReleaseNext();

        var exception = Assert.ThrowsAsync<IOException>(async () => await pending);
        exception.Should().BeSameAs(expected);
        (await CompleteYieldAsync(file.GetLengthAsync(), controller)).Should().Be(0);
    }

    [Test]
    public async Task ForcedCompletionsCoverAtomicTemporaryAndMaterializationCapabilities()
    {
        var memoryController = new DeterministicAsyncIoController(forceYield: true);
        var memory = new InMemoryFileSystem();
        using (memory.OpenFile("source.db", FileOpenMode.CreateNew))
        {
        }

        var memoryAsync = DeterministicAsyncFileSystem.Create(
            AsyncFileSystemAdapter.Create(memory),
            memoryController);
        var atomic = memoryAsync.Should().BeAssignableTo<IAsyncAtomicFileSystem>().Subject;
        await CompleteYieldAsync(
            atomic.ReplaceFileAtomicallyAsync(
                "source.db",
                "destination.db",
                replaceEmptyDestination: false),
            memoryController);
        memory.FileExists("destination.db").Should().BeTrue();

        var materializationController = new DeterministicAsyncIoController(forceYield: true);
        var materializingFile = DeterministicAsyncFile.Create(
            AsyncFileAdapter.Create(new AsyncFileSystemAdapterTests.MaterializingFile()),
            materializationController);
        var materializing = materializingFile
            .Should().BeAssignableTo<IAsyncPageMaterializingFile>().Subject;
        await CompleteYieldAsync(
            materializing.EnsureMaterializedAsync(0, 4096),
            materializationController);

        var path = Path.Combine(Path.GetTempPath(), $"ahtola-async-yield-{Guid.NewGuid():N}.tmp");
        var physicalController = new DeterministicAsyncIoController(forceYield: true);
        var physical = DeterministicAsyncFileSystem.Create(
            AsyncFileSystemAdapter.Create(PhysicalFileSystem.Instance),
            physicalController);
        var temporaryFileSystem = physical
            .Should().BeAssignableTo<IAsyncTemporaryFileSystem>().Subject;
        try
        {
            var temporary = await CompleteYieldAsync(
                temporaryFileSystem.OpenTemporaryFileAsync(path),
                physicalController);
            await CompleteYieldAsync(temporary.DisposeAsync(), physicalController);
            File.Exists(path).Should().BeFalse();
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        memoryController.GetOperationCount(FileSystemOperation.AtomicReplace).Should().Be(1);
        materializationController.GetOperationCount(FileSystemOperation.EnsureMaterialized).Should().Be(1);
        physicalController.GetOperationCount(FileSystemOperation.OpenTemporary).Should().Be(1);
    }

    private static async Task CompleteYieldAsync(
        ValueTask operation,
        DeterministicAsyncIoController controller)
    {
        operation.IsCompleted.Should().BeFalse();
        controller.PendingYieldCount.Should().Be(1);
        controller.ReleaseNext();
        await operation;
    }

    private static async Task<T> CompleteYieldAsync<T>(
        ValueTask<T> operation,
        DeterministicAsyncIoController controller)
    {
        operation.IsCompleted.Should().BeFalse();
        controller.PendingYieldCount.Should().Be(1);
        controller.ReleaseNext();
        return await operation;
    }
}

internal sealed class DeterministicAsyncIoController(bool forceYield)
{
    private readonly object _gate = new();
    private readonly Dictionary<FileSystemOperation, long> _counts = new();
    private readonly Dictionary<(FileSystemOperation Operation, long Occurrence), Exception> _faults = new();
    private readonly Queue<TaskCompletionSource> _pendingYields = new();

    internal int PendingYieldCount
    {
        get
        {
            lock (_gate)
                return _pendingYields.Count;
        }
    }

    internal void FailNext(FileSystemOperation operation, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_gate)
        {
            var occurrence = _counts.GetValueOrDefault(operation) + 1;
            _faults[(operation, occurrence)] = exception;
        }
    }

    internal long GetOperationCount(FileSystemOperation operation)
    {
        lock (_gate)
            return _counts.GetValueOrDefault(operation);
    }

    internal void ReleaseNext()
    {
        TaskCompletionSource completion;
        lock (_gate)
            completion = _pendingYields.Dequeue();
        completion.SetResult();
    }

    internal async ValueTask BeforeOperationAsync(
        FileSystemOperation operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        TaskCompletionSource? completion = null;
        Exception? fault;
        lock (_gate)
        {
            var occurrence = _counts.GetValueOrDefault(operation) + 1;
            _counts[operation] = occurrence;
            _faults.Remove((operation, occurrence), out fault);
            if (forceYield)
            {
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingYields.Enqueue(completion);
            }
        }

        if (completion is not null)
            await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        if (fault is not null)
            ExceptionDispatchInfo.Capture(fault).Throw();
    }
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
            cancellationToken).ConfigureAwait(false);
        var file = await Inner.OpenFileAsync(path, mode, readOnly, cancellationToken).ConfigureAwait(false);
        return DeterministicAsyncFile.Create(file, Controller);
    }

    public async ValueTask DeleteFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await Controller.BeforeOperationAsync(
            FileSystemOperation.Delete,
            cancellationToken).ConfigureAwait(false);
        await Inner.DeleteFileAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<FileWriteStamp?> GetWriteStampAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await Controller.BeforeOperationAsync(
            FileSystemOperation.GetWriteStamp,
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
                cancellationToken).ConfigureAwait(false);
            var file = await ((IAsyncTemporaryFileSystem)Inner)
                .OpenTemporaryFileAsync(path, cancellationToken).ConfigureAwait(false);
            return DeterministicAsyncFile.Create(file, Controller);
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
                cancellationToken).ConfigureAwait(false);
            var file = await ((IAsyncTemporaryFileSystem)Inner)
                .OpenTemporaryFileAsync(path, cancellationToken).ConfigureAwait(false);
            return DeterministicAsyncFile.Create(file, Controller);
        }
    }
}

internal class DeterministicAsyncFile : IAsyncFile
{
    protected DeterministicAsyncFile(
        IAsyncFile inner,
        DeterministicAsyncIoController controller)
    {
        Inner = inner;
        Controller = controller;
    }

    protected IAsyncFile Inner { get; }

    protected DeterministicAsyncIoController Controller { get; }

    internal static IAsyncFile Create(
        IAsyncFile inner,
        DeterministicAsyncIoController controller)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(controller);
        return inner is IAsyncPageMaterializingFile
            ? new MaterializingFile(inner, controller)
            : new DeterministicAsyncFile(inner, controller);
    }

    public bool IsReadOnly => Inner.IsReadOnly;

    public async ValueTask<long> GetLengthAsync(CancellationToken cancellationToken = default)
    {
        await Controller.BeforeOperationAsync(
            FileSystemOperation.GetLength,
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
            cancellationToken).ConfigureAwait(false);
        await Inner.WriteAsync(position, source, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetLengthAsync(
        long length,
        CancellationToken cancellationToken = default)
    {
        await Controller.BeforeOperationAsync(
            FileSystemOperation.SetLength,
            cancellationToken).ConfigureAwait(false);
        await Inner.SetLengthAsync(length, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask FlushToDiskAsync(CancellationToken cancellationToken = default)
    {
        await Controller.BeforeOperationAsync(
            FileSystemOperation.FlushToDisk,
            cancellationToken).ConfigureAwait(false);
        await Inner.FlushToDiskAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await Controller.BeforeOperationAsync(
            FileSystemOperation.Dispose,
            CancellationToken.None).ConfigureAwait(false);
        await Inner.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class MaterializingFile(
        IAsyncFile inner,
        DeterministicAsyncIoController controller) :
        DeterministicAsyncFile(inner, controller),
        IAsyncPageMaterializingFile
    {
        public async ValueTask EnsureMaterializedAsync(
            long position,
            int length,
            CancellationToken cancellationToken = default)
        {
            await Controller.BeforeOperationAsync(
                FileSystemOperation.EnsureMaterialized,
                cancellationToken).ConfigureAwait(false);
            await ((IAsyncPageMaterializingFile)Inner).EnsureMaterializedAsync(
                position,
                length,
                cancellationToken).ConfigureAwait(false);
        }
    }
}
