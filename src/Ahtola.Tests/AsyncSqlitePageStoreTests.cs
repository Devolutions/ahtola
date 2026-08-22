using AwesomeAssertions;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class AsyncSqlitePageStoreTests
{
    [Test]
    public async Task CreateWriteReadAndReopenMatchSynchronousStoreBytes()
    {
        var synchronousFileSystem = new InMemoryFileSystem();
        var asynchronousBacking = new InMemoryFileSystem();
        var asynchronousFileSystem = AsyncFileSystemAdapter.Create(asynchronousBacking);
        var page2 = CreatePage(SqlitePageSize.Default, 0xA5);

        using (var synchronous = SqlitePageStore.Create(synchronousFileSystem, "main.db"))
        {
            synchronous.WritePage(2, page2);
            synchronous.Flush();
        }

        await using (var asynchronous = await AsyncSqlitePageStore.CreateAsync(
            asynchronousFileSystem,
            "main.db"))
        {
            asynchronous.Path.Should().Be("main.db");
            asynchronous.PageSize.Should().Be(SqlitePageSize.Default);
            asynchronous.IsReadOnly.Should().BeFalse();
            (await asynchronous.GetPageCountAsync()).Should().Be(1);

            await asynchronous.WritePageAsync(2, page2);
            asynchronous.Header.DatabaseSizeInPages.Should().Be(2);
            (await asynchronous.GetPageCountAsync()).Should().Be(2);

            var destination = new byte[asynchronous.PageSize];
            await asynchronous.ReadPageAsync(2, destination);
            destination.Should().Equal(page2);
            (await asynchronous.ReadPageAsync(2)).Should().Equal(page2);
            await asynchronous.FlushAsync();
        }

        Snapshot(synchronousFileSystem, "main.db")
            .Should().Equal(Snapshot(asynchronousBacking, "main.db"));

        await using var reopened = await AsyncSqlitePageStore.OpenAsync(
            asynchronousFileSystem,
            "main.db",
            readOnly: true);
        reopened.IsReadOnly.Should().BeTrue();
        reopened.Header.DatabaseSizeInPages.Should().Be(2);
        (await reopened.GetPageCountAsync()).Should().Be(2);
        (await reopened.ReadRawPageAsync(2)).Should().Equal(page2);
    }

    [Test]
    public async Task ForcedYieldAppendRestoresLengthWhenHeaderWriteFails()
    {
        var faults = new DeterministicFaultInjector();
        var backing = new InMemoryFileSystem(faults);
        var controller = new DeterministicAsyncIoController(forceYield: true);
        var fileSystem = DeterministicAsyncFileSystem.Create(
            AsyncFileSystemAdapter.Create(backing),
            controller);
        var store = await CompleteAsync(
            AsyncSqlitePageStore.CreateAsync(fileSystem, "main.db"),
            controller);
        faults.FailOnOccurrence(FileSystemOperation.Write, 3);

        Assert.ThrowsAsync<IOException>(async () => await CompleteAsync(
            store.WritePageAsync(2, CreatePage(store.PageSize, 0xD1)),
            controller));

        (await CompleteAsync(store.GetPageCountAsync(), controller)).Should().Be(1);
        using (var reopened = SqlitePageStore.Open(backing, "main.db"))
            reopened.PageCount.Should().Be(1);

        await CompleteAsync(store.DisposeAsync(), controller);
    }

    [Test]
    public async Task ReadRejectsAnExactShortPage()
    {
        var backing = new InMemoryFileSystem();
        var controlled = new ControlledAsyncFileSystem(
            AsyncFileSystemAdapter.Create(backing));
        await using var store = await AsyncSqlitePageStore.CreateAsync(
            controlled,
            "main.db");
        controlled.ShortReads = true;

        var exception = Assert.ThrowsAsync<InvalidDataException>(
            async () => await store.ReadPageAsync(1));

        exception.Message.Should().Contain("Short read on page 1");
        exception.Message.Should().Contain($"expected {store.PageSize} bytes");
    }

    [Test]
    public async Task CancellationWhileBackendIsSuspendedPropagatesTheToken()
    {
        var controller = new DeterministicAsyncIoController(forceYield: true);
        var fileSystem = DeterministicAsyncFileSystem.Create(
            AsyncFileSystemAdapter.Create(new InMemoryFileSystem()),
            controller);
        var store = await CompleteAsync(
            AsyncSqlitePageStore.CreateAsync(fileSystem, "main.db"),
            controller);
        using var cancellation = new CancellationTokenSource();

        var pending = store.ReadPageAsync(1, cancellation.Token);
        pending.IsCompleted.Should().BeFalse();
        controller.PendingYieldCount.Should().Be(1);
        cancellation.Cancel();

        var exception = Assert.CatchAsync<OperationCanceledException>(
            async () => await pending);
        exception.CancellationToken.Should().Be(cancellation.Token);
        controller.ReleaseNext();
        await CompleteAsync(store.DisposeAsync(), controller);
    }

    [Test]
    public async Task ShrinkPageOneThenTruncateMatchesSynchronousStore()
    {
        var synchronousFileSystem = new InMemoryFileSystem();
        var asynchronousBacking = new InMemoryFileSystem();
        using var synchronous = SqlitePageStore.Create(synchronousFileSystem, "main.db");
        await using var asynchronous = await AsyncSqlitePageStore.CreateAsync(
            AsyncFileSystemAdapter.Create(asynchronousBacking),
            "main.db");
        var page2 = CreatePage(synchronous.PageSize, 0x22);
        var page3 = CreatePage(synchronous.PageSize, 0x33);

        synchronous.WritePage(2, page2);
        synchronous.WritePage(3, page3);
        await asynchronous.WritePageAsync(2, page2);
        await asynchronous.WritePageAsync(3, page3);

        var synchronousPageOne = synchronous.ReadPage(1);
        (synchronous.Header with { DatabaseSizeInPages = 1 }).WriteTo(synchronousPageOne);
        synchronous.WriteShrinkCheckpointPageOne(synchronousPageOne);
        synchronous.TruncateToPageCount(1);

        var asynchronousPageOne = await asynchronous.ReadPageAsync(1);
        (asynchronous.Header with { DatabaseSizeInPages = 1 }).WriteTo(asynchronousPageOne);
        await asynchronous.WriteShrinkCheckpointPageOneAsync(asynchronousPageOne);
        await asynchronous.TruncateToPageCountAsync(1);

        (await asynchronous.GetPageCountAsync()).Should().Be(1);
        asynchronous.Header.DatabaseSizeInPages.Should().Be(1);
        Snapshot(synchronousFileSystem, "main.db")
            .Should().Equal(Snapshot(asynchronousBacking, "main.db"));
    }

    [Test]
    public async Task UnpublishedImageAcceptsAsynchronousPageCallbacks()
    {
        var backing = new InMemoryFileSystem();
        await using var store = await AsyncSqlitePageStore.CreateAsync(
            AsyncFileSystemAdapter.Create(backing),
            "vacuum.db",
            flushOnCreate: false);
        var pageOne = await store.ReadPageAsync(1);
        (store.Header with { DatabaseSizeInPages = 2 }).WriteTo(pageOne);
        var pageTwo = CreatePage(store.PageSize, 0x74);
        var callbackCalls = new List<uint>();

        await store.WriteUnpublishedImageAsync(
            2,
            async (pageNumber, cancellationToken) =>
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                callbackCalls.Add(pageNumber);
                return pageNumber == 1 ? pageOne : pageTwo;
            });

        callbackCalls.Should().Equal(1U, 2U);
        (await store.GetPageCountAsync()).Should().Be(2);
        (await store.ReadPageAsync(2)).Should().Equal(pageTwo);
    }

    [Test]
    public async Task RawReplacementCanRefreshTheHeader()
    {
        var backing = new InMemoryFileSystem();
        using (var source = SqlitePageStore.Create(backing, "source.db"))
        {
            var pageOne = source.ReadPage(1);
            (source.Header with { UserVersion = 42 }).WriteTo(pageOne);
            source.WritePage(1, pageOne);
        }

        var fileSystem = AsyncFileSystemAdapter.Create(backing);
        await using var destination = await AsyncSqlitePageStore.CreateAsync(
            fileSystem,
            "destination.db");
        await using var sourceFile = await fileSystem.OpenFileAsync(
            "source.db",
            FileOpenMode.OpenExisting,
            readOnly: true);

        await destination.ReplaceRawContentAsync(sourceFile);
        await destination.RefreshHeaderAsync();

        destination.Header.UserVersion.Should().Be(42);
        Snapshot(backing, "destination.db").Should().Equal(Snapshot(backing, "source.db"));
    }

    [Test]
    public async Task ExternalSynchronousCodecRunsAcrossForcedAsyncIo()
    {
        var controller = new DeterministicAsyncIoController(forceYield: true);
        var fileSystem = DeterministicAsyncFileSystem.Create(
            AsyncFileSystemAdapter.Create(new InMemoryFileSystem()),
            controller);
        var codec = new RecordingPageCodec();
        var store = await CompleteAsync(
            AsyncSqlitePageStore.CreateAsync(fileSystem, "coded.db", pageCodec: codec),
            controller);
        var pageTwo = CreatePage(store.PageSize, 0x6C);

        await CompleteAsync(store.WritePageAsync(2, pageTwo), controller);
        (await CompleteAsync(store.ReadPageAsync(2), controller)).Should().Equal(pageTwo);
        codec.EncodeCount.Should().BeGreaterThanOrEqualTo(3);
        codec.DecodeCount.Should().BeGreaterThanOrEqualTo(2);
        await CompleteAsync(store.DisposeAsync(), controller);

        var reopened = await CompleteAsync(
            AsyncSqlitePageStore.OpenAsync(fileSystem, "coded.db", pageCodec: codec),
            controller);
        (await CompleteAsync(reopened.ReadPageAsync(2), controller)).Should().Equal(pageTwo);
        await CompleteAsync(reopened.DisposeAsync(), controller);
    }

    [Test]
    public async Task InvalidOpenAndFailedCreateCleanUpOwnedHandlesAndArtifacts()
    {
        var malformedBacking = new InMemoryFileSystem();
        using (var store = SqlitePageStore.Create(malformedBacking, "malformed.db"))
        {
        }

        using (var file = malformedBacking.OpenFile("malformed.db", FileOpenMode.OpenExisting))
            file.SetLength(file.Length + 1);

        var controlled = new ControlledAsyncFileSystem(
            AsyncFileSystemAdapter.Create(malformedBacking));
        Assert.ThrowsAsync<InvalidDataException>(
            async () => await AsyncSqlitePageStore.OpenAsync(controlled, "malformed.db"));
        controlled.DisposeCount.Should().Be(1);

        var faults = new DeterministicFaultInjector();
        var failedBacking = new InMemoryFileSystem(faults);
        faults.FailNext(FileSystemOperation.Write);
        Assert.ThrowsAsync<IOException>(async () => await AsyncSqlitePageStore.CreateAsync(
            AsyncFileSystemAdapter.Create(failedBacking),
            "failed.db"));
        failedBacking.FileExists("failed.db").Should().BeFalse();

        var existingBacking = new InMemoryFileSystem();
        using (SqlitePageStore.Create(existingBacking, "existing.db"))
        {
        }

        var original = Snapshot(existingBacking, "existing.db");
        Assert.ThrowsAsync<IOException>(async () => await AsyncSqlitePageStore.CreateAsync(
            AsyncFileSystemAdapter.Create(existingBacking),
            "existing.db"));
        Snapshot(existingBacking, "existing.db").Should().Equal(original);
    }

    [Test]
    public async Task DisposeIsAsynchronousIdempotentAndGuardsState()
    {
        var controlled = new ControlledAsyncFileSystem(
            AsyncFileSystemAdapter.Create(new InMemoryFileSystem()));
        var store = await AsyncSqlitePageStore.CreateAsync(controlled, "main.db");

        await store.DisposeAsync();
        await store.DisposeAsync();

        controlled.DisposeCount.Should().Be(1);
        Assert.Throws<ObjectDisposedException>(() => _ = store.Header);
        Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await store.GetPageCountAsync());
    }

    private static byte[] CreatePage(int pageSize, byte fill)
    {
        var page = new byte[pageSize];
        Array.Fill(page, fill);
        return page;
    }

    private static byte[] Snapshot(InMemoryFileSystem fileSystem, string path)
    {
        using var file = fileSystem.OpenFile(path, FileOpenMode.OpenExisting, readOnly: true);
        var bytes = new byte[checked((int)file.Length)];
        file.Read(0, bytes).Should().Be(bytes.Length);
        return bytes;
    }

    private static async Task CompleteAsync(
        ValueTask operation,
        DeterministicAsyncIoController controller)
    {
        var task = operation.AsTask();
        await DrainAsync(task, controller);
    }

    private static async Task<T> CompleteAsync<T>(
        ValueTask<T> operation,
        DeterministicAsyncIoController controller)
    {
        var task = operation.AsTask();
        await DrainAsync(task, controller);
        return await task;
    }

    private static async Task DrainAsync(
        Task task,
        DeterministicAsyncIoController controller)
    {
        while (!task.IsCompleted)
        {
            if (controller.PendingYieldCount > 0)
                controller.ReleaseNext();
            else
                await Task.Yield();
        }

        await task;
    }

    private sealed class ControlledAsyncFileSystem(IAsyncFileSystem inner) : IAsyncFileSystem
    {
        internal bool ShortReads { get; set; }

        internal int DisposeCount { get; private set; }

        public ValueTask<bool> FileExistsAsync(
            string path,
            CancellationToken cancellationToken = default)
            => inner.FileExistsAsync(path, cancellationToken);

        public async ValueTask<IAsyncFile> OpenFileAsync(
            string path,
            FileOpenMode mode,
            bool readOnly = false,
            CancellationToken cancellationToken = default)
        {
            var file = await inner.OpenFileAsync(
                path,
                mode,
                readOnly,
                cancellationToken);
            return new ControlledAsyncFile(this, file);
        }

        public ValueTask DeleteFileAsync(
            string path,
            CancellationToken cancellationToken = default)
            => inner.DeleteFileAsync(path, cancellationToken);

        public ValueTask<FileWriteStamp?> GetWriteStampAsync(
            string path,
            CancellationToken cancellationToken = default)
            => inner.GetWriteStampAsync(path, cancellationToken);

        private sealed class ControlledAsyncFile(
            ControlledAsyncFileSystem owner,
            IAsyncFile innerFile) : IAsyncFile
        {
            public bool IsReadOnly => innerFile.IsReadOnly;

            public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken = default)
                => innerFile.GetLengthAsync(cancellationToken);

            public ValueTask<int> ReadAsync(
                long position,
                Memory<byte> destination,
                CancellationToken cancellationToken = default)
                => innerFile.ReadAsync(
                    position,
                    owner.ShortReads && destination.Length > 0
                        ? destination[..^1]
                        : destination,
                    cancellationToken);

            public ValueTask WriteAsync(
                long position,
                ReadOnlyMemory<byte> source,
                CancellationToken cancellationToken = default)
                => innerFile.WriteAsync(position, source, cancellationToken);

            public ValueTask SetLengthAsync(
                long length,
                CancellationToken cancellationToken = default)
                => innerFile.SetLengthAsync(length, cancellationToken);

            public ValueTask FlushToDiskAsync(CancellationToken cancellationToken = default)
                => innerFile.FlushToDiskAsync(cancellationToken);

            public async ValueTask DisposeAsync()
            {
                owner.DisposeCount++;
                await innerFile.DisposeAsync();
            }
        }
    }

    private sealed class RecordingPageCodec : IPageCodec
    {
        private static readonly PageCodecId Id = new(
            new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });

        internal int EncodeCount { get; private set; }

        internal int DecodeCount { get; private set; }

        public PageCodecId CodecId => Id;

        public byte RequiredReservedBytes => 0;

        public void EncodePage(
            PageCodecContext context,
            ReadOnlySpan<byte> input,
            Span<byte> output)
        {
            context.Location.Should().Be(PageLocation.Database);
            EncodeCount++;
            input.CopyTo(output);
        }

        public void DecodePage(
            PageCodecContext context,
            ReadOnlySpan<byte> input,
            Span<byte> output)
        {
            context.Location.Should().Be(PageLocation.Database);
            DecodeCount++;
            input.CopyTo(output);
        }
    }
}
