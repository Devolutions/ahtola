using AwesomeAssertions;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class AsyncSqliteWalTests
{
    private const int PageSize = 512;

    [Test]
    public async Task AsyncWalMatchesSyncBytesAndSuspendsAtEveryIoBoundary()
    {
        var header = CreateHeader();
        var pages = CreatePages(4);
        var syncFileSystem = new InMemoryFileSystem();
        var asyncStorage = new InMemoryFileSystem();
        var controller = new DeterministicAsyncIoController(forceYield: true);
        var asyncFileSystem = DeterministicAsyncFileSystem.Create(
            AsyncFileSystemAdapter.Create(asyncStorage),
            controller);

        using (var syncWal = SqliteWalFile.Create(syncFileSystem, "sync.db-wal", header))
        {
            syncWal.AppendFrames(new FrameSource(pages), commitDatabaseSizeInPages: 4);
            syncWal.Flush();
        }

        var asyncWal = await CompleteAsync(
            SqliteWalFile.CreateAsync(asyncFileSystem, "async.db-wal", header),
            controller);
        (await CompleteAsync(
            asyncWal.AppendFramesAsync(new FrameSource(pages), commitDatabaseSizeInPages: 4),
            controller)).Should().Be(4);
        await CompleteAsync(asyncWal.FlushAsync(), controller);
        (await CompleteAsync(asyncWal.GetLengthAsync(), controller))
            .Should().Be(SqliteWalHeader.Size + 4L * (SqliteWalFrameHeader.Size + PageSize));
        (await CompleteAsync(asyncWal.ReadDurableHeaderAsync(), controller))
            .Should().BeEquivalentTo(header);

        var frames = await CompleteAsync(asyncWal.ReadFrameRangeAsync(1, 4), controller);
        frames.Should().HaveCount(4);
        frames.Select(static frame => frame.Header.PageNumber).Should().Equal(1U, 2U, 3U, 4U);
        frames[^1].Header.DatabaseSizeInPages.Should().Be(4);
        (await CompleteAsync(asyncWal.ScanRecoveryAsync(), controller))
            .LastCommittedFrameNumber.Should().Be(4);
        await CompleteAsync(asyncWal.DisposeAsync(), controller);

        ReadAllBytes(asyncStorage, "async.db-wal")
            .Should().Equal(ReadAllBytes(syncFileSystem, "sync.db-wal"));
        controller.GetOperationCount(FileSystemOperation.Open).Should().BeGreaterThan(0);
        controller.GetOperationCount(FileSystemOperation.Read).Should().BeGreaterThan(0);
        controller.GetOperationCount(FileSystemOperation.Write).Should().BeGreaterThan(0);
        controller.GetOperationCount(FileSystemOperation.GetLength).Should().BeGreaterThan(0);
        controller.GetOperationCount(FileSystemOperation.FlushToDisk).Should().BeGreaterThan(0);
        controller.GetOperationCount(FileSystemOperation.Dispose).Should().Be(1);
    }

    [Test]
    public async Task AsyncWalTreatsShortFrameReadsAsTruncation()
    {
        var storage = new InMemoryFileSystem();
        var shortReads = new ShortReadController();
        var fileSystem = new ShortReadAsyncFileSystem(
            AsyncFileSystemAdapter.Create(storage),
            shortReads);
        await using var wal = await SqliteWalFile.CreateAsync(fileSystem, "short.db-wal", CreateHeader());
        await wal.AppendFrameAsync(1, CreatePage(0x51), databaseSizeInPages: 1);

        shortReads.ShortenNextRead();
        var readException = Assert.ThrowsAsync<InvalidDataException>(
            async () => await wal.ReadFrameAsync(1));
        readException!.Message.Should().Contain("Short read on SQLite WAL frame");

        shortReads.ShortenNextRead();
        var recovery = await wal.ScanRecoveryAsync();
        recovery.LastValidFrameNumber.Should().Be(0);
        recovery.StopReason.Should().Be(SqliteWalRecoveryStopReason.PartialFrame);
    }

    [Test]
    public async Task AsyncAppendFaultRestoresFrameAlignmentAndPreservesException()
    {
        var storage = new InMemoryFileSystem();
        var controller = new DeterministicAsyncIoController(forceYield: false);
        var fileSystem = DeterministicAsyncFileSystem.Create(
            AsyncFileSystemAdapter.Create(storage),
            controller);
        await using var wal = await SqliteWalFile.CreateAsync(fileSystem, "fault.db-wal", CreateHeader());
        var expected = new IOException("injected async WAL write failure");
        controller.FailNext(FileSystemOperation.Write, expected);

        var exception = Assert.ThrowsAsync<IOException>(
            async () => await wal.AppendFrameAsync(1, CreatePage(0x61), databaseSizeInPages: 1));
        exception.Should().BeSameAs(expected);
        (await wal.GetLengthAsync()).Should().Be(SqliteWalHeader.Size);
        (await wal.ScanRecoveryAsync()).StopReason.Should().Be(SqliteWalRecoveryStopReason.EndOfFile);

        (await wal.AppendFrameAsync(1, CreatePage(0x62), databaseSizeInPages: 1)).Should().Be(1);
        (await wal.ScanRecoveryAsync()).LastCommittedFrameNumber.Should().Be(1);
    }

    [Test]
    public async Task AsyncWalCancellationPropagatesTheOriginalTokenWhileSuspended()
    {
        var storage = new InMemoryFileSystem();
        var controller = new DeterministicAsyncIoController(forceYield: true);
        var fileSystem = DeterministicAsyncFileSystem.Create(
            AsyncFileSystemAdapter.Create(storage),
            controller);
        var wal = await CompleteAsync(
            SqliteWalFile.CreateAsync(fileSystem, "cancel.db-wal", CreateHeader()),
            controller);
        using var cancellation = new CancellationTokenSource();

        var pending = wal.ScanRecoveryAsync(cancellation.Token);
        pending.IsCompleted.Should().BeFalse();
        controller.PendingYieldCount.Should().Be(1);
        cancellation.Cancel();

        var exception = Assert.CatchAsync<OperationCanceledException>(
            async () => await pending);
        exception!.CancellationToken.Should().Be(cancellation.Token);
        controller.ReleaseNext();
        await CompleteAsync(wal.DisposeAsync(), controller);
    }

    [Test]
    public async Task AsyncRecoveryTruncatesPartialAndUncommittedTailsAtLastCommit()
    {
        var storage = new InMemoryFileSystem();
        var fileSystem = AsyncFileSystemAdapter.Create(storage);
        await using var wal = await SqliteWalFile.CreateAsync(fileSystem, "recover.db-wal", CreateHeader());
        await wal.AppendFrameAsync(1, CreatePage(0x71), databaseSizeInPages: 1);
        await wal.AppendFrameAsync(2, CreatePage(0x72));
        var committedLength = SqliteWalHeader.Size + wal.FrameSize;

        await using (var raw = await fileSystem.OpenFileAsync(
            "recover.db-wal",
            FileOpenMode.OpenExisting))
        {
            await raw.WriteAsync(await raw.GetLengthAsync(), new byte[] { 0xAA, 0xBB, 0xCC });
        }

        var scan = await wal.ScanRecoveryAsync();
        scan.LastValidFrameNumber.Should().Be(2);
        scan.LastCommittedFrameNumber.Should().Be(1);
        scan.StopReason.Should().Be(SqliteWalRecoveryStopReason.PartialFrame);

        (await wal.RecoverToLastCommittedFrameAsync()).Should().Be(scan);
        (await wal.GetLengthAsync()).Should().Be(committedLength);
        (await wal.ScanRecoveryAsync()).StopReason.Should().Be(SqliteWalRecoveryStopReason.EndOfFile);
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await wal.ReadFrameAsync(2));
    }

    [Test]
    public async Task AsyncReadOnlyResetAndTruncatePreserveWalLifecycleSemantics()
    {
        var storage = new InMemoryFileSystem();
        var fileSystem = AsyncFileSystemAdapter.Create(storage);
        var wal = await SqliteWalFile.CreateAsync(fileSystem, "lifecycle.db-wal", CreateHeader());
        await wal.AppendFrameAsync(1, CreatePage(0x81), databaseSizeInPages: 1);
        var originalSalt = wal.Header.Salt1;

        await wal.ResetAfterDurableCheckpointAsync(publishCheckpointedRecoveryMarker: true);
        (await wal.GetLengthAsync()).Should().Be(SqliteWalHeader.Size);
        wal.Header.Salt1.Should().Be(unchecked(originalSalt + 1));
        wal.HasCheckpointedRecoveryMarker.Should().BeTrue();

        await wal.AppendFrameAsync(2, CreatePage(0x82), databaseSizeInPages: 2);
        await wal.TruncateAfterDurableCheckpointAsync();
        (await wal.GetLengthAsync()).Should().Be(0);
        await wal.AppendFrameAsync(3, CreatePage(0x83), databaseSizeInPages: 3);
        (await wal.ScanRecoveryAsync()).LastCommittedDatabaseSizeInPages.Should().Be(3);
        Assert.Throws<InvalidOperationException>(() => wal.ScanRecovery());
        await wal.DisposeAsync();

        await using var readOnly = await SqliteWalFile.OpenAsync(
            fileSystem,
            "lifecycle.db-wal",
            readOnly: true);
        readOnly.IsReadOnly.Should().BeTrue();
        (await readOnly.ReadFrameAsync(1)).PageData.Should().Equal(CreatePage(0x83));
        Assert.ThrowsAsync<InvalidOperationException>(
            async () => await readOnly.AppendFrameAsync(4, CreatePage(0x84), databaseSizeInPages: 4));
        await readOnly.FlushAsync();
    }

    private static SqliteWalHeader CreateHeader()
        => SqliteWalHeader.Create(
            PageSize,
            salt1: 0x1234_5678,
            salt2: 0x9ABC_DEF0,
            checkpointSequence: 7,
            checksumByteOrder: SqliteWalChecksumByteOrder.BigEndian);

    private static byte[] CreatePage(byte fill)
    {
        var page = new byte[PageSize];
        Array.Fill(page, fill);
        return page;
    }

    private static List<WalPage> CreatePages(int count)
    {
        var pages = new List<WalPage>(count);
        for (var index = 0; index < count; index++)
            pages.Add(new WalPage((uint)(index + 1), CreatePage(unchecked((byte)(0x20 + index)))));
        return pages;
    }

    private static byte[] ReadAllBytes(IFileSystem fileSystem, string path)
    {
        using var file = fileSystem.OpenFile(path, FileOpenMode.OpenExisting, readOnly: true);
        var contents = new byte[checked((int)file.Length)];
        file.Read(0, contents).Should().Be(contents.Length);
        return contents;
    }

    private static async Task CompleteAsync(
        ValueTask operation,
        DeterministicAsyncIoController controller)
    {
        var task = operation.AsTask();
        await ReleaseUntilCompleteAsync(task, controller);
        await task;
    }

    private static async Task<T> CompleteAsync<T>(
        ValueTask<T> operation,
        DeterministicAsyncIoController controller)
    {
        var task = operation.AsTask();
        await ReleaseUntilCompleteAsync(task, controller);
        return await task;
    }

    private static async Task ReleaseUntilCompleteAsync(
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
    }

    private sealed record WalPage(uint PageNumber, byte[] Image);

    private sealed class FrameSource(IReadOnlyList<WalPage> pages) : ISqliteWalFrameSource
    {
        public int Count => pages.Count;

        public uint GetPageNumber(int index) => pages[index].PageNumber;

        public ReadOnlySpan<byte> GetPageImage(int index) => pages[index].Image;
    }

    private sealed class ShortReadController
    {
        private int _remaining;

        internal void ShortenNextRead() => Interlocked.Exchange(ref _remaining, 1);

        internal bool Consume() => Interlocked.Exchange(ref _remaining, 0) != 0;
    }

    private sealed class ShortReadAsyncFileSystem(
        IAsyncFileSystem inner,
        ShortReadController controller) : IAsyncFileSystem
    {
        public ValueTask<bool> FileExistsAsync(
            string path,
            CancellationToken cancellationToken = default)
            => inner.FileExistsAsync(path, cancellationToken);

        public async ValueTask<IAsyncFile> OpenFileAsync(
            string path,
            FileOpenMode mode,
            bool readOnly = false,
            CancellationToken cancellationToken = default)
            => new ShortReadAsyncFile(
                await inner.OpenFileAsync(path, mode, readOnly, cancellationToken),
                controller);

        public ValueTask DeleteFileAsync(
            string path,
            CancellationToken cancellationToken = default)
            => inner.DeleteFileAsync(path, cancellationToken);

        public ValueTask<FileWriteStamp?> GetWriteStampAsync(
            string path,
            CancellationToken cancellationToken = default)
            => inner.GetWriteStampAsync(path, cancellationToken);
    }

    private sealed class ShortReadAsyncFile(
        IAsyncFile inner,
        ShortReadController controller) : IAsyncFile
    {
        public bool IsReadOnly => inner.IsReadOnly;

        public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken = default)
            => inner.GetLengthAsync(cancellationToken);

        public ValueTask<int> ReadAsync(
            long position,
            Memory<byte> destination,
            CancellationToken cancellationToken = default)
            => inner.ReadAsync(
                position,
                controller.Consume() && destination.Length > 0 ? destination[..^1] : destination,
                cancellationToken);

        public ValueTask WriteAsync(
            long position,
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken = default)
            => inner.WriteAsync(position, source, cancellationToken);

        public ValueTask SetLengthAsync(
            long length,
            CancellationToken cancellationToken = default)
            => inner.SetLengthAsync(length, cancellationToken);

        public ValueTask FlushToDiskAsync(CancellationToken cancellationToken = default)
            => inner.FlushToDiskAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
