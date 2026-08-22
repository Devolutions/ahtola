using System.Buffers.Binary;
using AwesomeAssertions;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class AsyncSqliteRollbackJournalTests
{
    [Test]
    public async Task AsyncWriterMatchesSynchronousJournalBytesAcrossForcedYields()
    {
        var expected = CreateSynchronousHotJournal();
        var backing = new InMemoryFileSystem();
        var controller = new DeterministicAsyncIoController(forceYield: true);
        var fileSystem = DeterministicAsyncFileSystem.Create(
            AsyncFileSystemAdapter.Create(backing),
            controller);

        var writer = await CompleteAsync(
            SqliteRollbackJournal.BeginAsync(
                fileSystem,
                "async.db-journal",
                recordCount: 1,
                expected.ChecksumNonce,
                initialDatabasePageCount: 1,
                expected.PageSize),
            controller);
        try
        {
            await CompleteAsync(
                writer.WritePageRecordAsync(1, expected.OriginalPage),
                controller);
            await CompleteAsync(writer.FinalizeAsync(), controller);
        }
        finally
        {
            await CompleteAsync(writer.DisposeAsync(), controller);
        }

        ReadAll(backing, "async.db-journal").Should().Equal(expected.JournalBytes);
        controller.GetOperationCount(FileSystemOperation.Open).Should().Be(1);
        controller.GetOperationCount(FileSystemOperation.Write).Should().Be(5);
        controller.GetOperationCount(FileSystemOperation.SetLength).Should().Be(1);
        controller.GetOperationCount(FileSystemOperation.FlushToDisk).Should().Be(2);
        controller.GetOperationCount(FileSystemOperation.Dispose).Should().Be(1);
    }

    [Test]
    public async Task AsyncRecoveryRestoresHotJournalAndDeletesItAfterDurableDatabaseFlush()
    {
        var expected = CreateSynchronousHotJournal();
        var backing = new InMemoryFileSystem();
        WriteAll(backing, "recover.db", Enumerable.Repeat((byte)0xCC, expected.PageSize).ToArray());
        WriteAll(backing, "recover.db-journal", expected.JournalBytes);
        var controller = new DeterministicAsyncIoController(forceYield: true);
        var fileSystem = DeterministicAsyncFileSystem.Create(
            AsyncFileSystemAdapter.Create(backing),
            controller);

        await CompleteAsync(
            SqliteRollbackJournal.RecoverIfPresentAsync(
                fileSystem,
                "recover.db",
                "recover.db-journal",
                readOnly: false),
            controller);

        ReadAll(backing, "recover.db").Should().Equal(expected.OriginalPage);
        backing.FileExists("recover.db-journal").Should().BeFalse();
        controller.GetOperationCount(FileSystemOperation.FileExists).Should().Be(2);
        controller.GetOperationCount(FileSystemOperation.Write).Should().Be(2);
        controller.GetOperationCount(FileSystemOperation.SetLength).Should().Be(1);
        controller.GetOperationCount(FileSystemOperation.FlushToDisk).Should().Be(2);
        controller.GetOperationCount(FileSystemOperation.Delete).Should().Be(1);
    }

    [Test]
    public async Task AsyncRecoveryRejectsHotJournalForReadOnlyOpenWithoutMutation()
    {
        var expected = CreateSynchronousHotJournal();
        var backing = new InMemoryFileSystem();
        var changedPage = Enumerable.Repeat((byte)0xA7, expected.PageSize).ToArray();
        WriteAll(backing, "readonly.db", changedPage);
        WriteAll(backing, "readonly.db-journal", expected.JournalBytes);
        var controller = new DeterministicAsyncIoController(forceYield: true);
        var fileSystem = DeterministicAsyncFileSystem.Create(
            AsyncFileSystemAdapter.Create(backing),
            controller);

        var exception = Assert.ThrowsAsync<InvalidDataException>(
            async () => await CompleteAsync(
                SqliteRollbackJournal.RecoverIfPresentAsync(
                    fileSystem,
                    "readonly.db",
                    "readonly.db-journal",
                    readOnly: true),
                controller));

        exception.Message.Should().Contain("hot rollback journal");
        ReadAll(backing, "readonly.db").Should().Equal(changedPage);
        ReadAll(backing, "readonly.db-journal").Should().Equal(expected.JournalBytes);
        controller.GetOperationCount(FileSystemOperation.Delete).Should().Be(0);
    }

    [Test]
    public async Task AsyncHotDetectionReportsShortReadAfterObservedLengthChanges()
    {
        var backing = new InMemoryFileSystem();
        var bytes = new byte[9];
        new byte[] { 0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7 }.CopyTo(bytes, 0);
        WriteAll(backing, "short.db-journal", bytes);
        var controller = new DeterministicAsyncIoController(forceYield: true);
        var fileSystem = DeterministicAsyncFileSystem.Create(
            AsyncFileSystemAdapter.Create(backing),
            controller);

        var operation = SqliteRollbackJournal
            .IsHotAsync(fileSystem, "short.db-journal")
            .AsTask();
        await ReleaseNextAsync(operation, controller);
        await ReleaseNextAsync(operation, controller);
        await ReleaseNextAsync(operation, controller);
        await WaitForPendingYieldAsync(operation, controller);
        using (var journal = backing.OpenFile("short.db-journal", FileOpenMode.OpenExisting))
            journal.SetLength(4);
        controller.ReleaseNext();

        await ReleaseRemainingAsync(operation, controller);
        var exception = Assert.ThrowsAsync<InvalidDataException>(async () => await operation);
        exception.Message.Should().Be("SQLite rollback journal magic is truncated.");
    }

    [Test]
    public async Task AsyncHotDetectionPreservesCancellationTokenWhileSuspended()
    {
        var backing = new InMemoryFileSystem();
        var controller = new DeterministicAsyncIoController(forceYield: true);
        var fileSystem = DeterministicAsyncFileSystem.Create(
            AsyncFileSystemAdapter.Create(backing),
            controller);
        using var cancellation = new CancellationTokenSource();

        var operation = SqliteRollbackJournal
            .IsHotAsync(fileSystem, "cancel.db-journal", cancellation.Token)
            .AsTask();
        await WaitForPendingYieldAsync(operation, controller);
        cancellation.Cancel();

        var exception = Assert.CatchAsync<OperationCanceledException>(
            async () => await operation);
        exception.CancellationToken.Should().Be(cancellation.Token);
        controller.ReleaseNext();
        backing.FileExists("cancel.db-journal").Should().BeFalse();
    }

    [Test]
    public async Task AsyncFinalizePropagatesSecondFlushFaultAfterPublishingMagic()
    {
        var expected = CreateSynchronousHotJournal();
        var backing = new InMemoryFileSystem();
        var controller = new DeterministicAsyncIoController(forceYield: true);
        var fileSystem = DeterministicAsyncFileSystem.Create(
            AsyncFileSystemAdapter.Create(backing),
            controller);
        var writer = await CompleteAsync(
            SqliteRollbackJournal.BeginAsync(
                fileSystem,
                "fault.db-journal",
                recordCount: 1,
                expected.ChecksumNonce,
                initialDatabasePageCount: 1,
                expected.PageSize),
            controller);
        await CompleteAsync(writer.WritePageRecordAsync(1, expected.OriginalPage), controller);

        var finalize = writer.FinalizeAsync().AsTask();
        await ReleaseNextAsync(finalize, controller);
        await ReleaseNextAsync(finalize, controller);
        await WaitForPendingYieldAsync(finalize, controller);
        var expectedException = new IOException("second journal flush failed");
        controller.FailNext(FileSystemOperation.FlushToDisk, expectedException);
        controller.ReleaseNext();
        await ReleaseNextAsync(finalize, controller);

        var exception = Assert.ThrowsAsync<IOException>(async () => await finalize);
        exception.Should().BeSameAs(expectedException);
        SqliteRollbackJournal.IsHot(backing, "fault.db-journal").Should().BeTrue();
        await CompleteAsync(writer.DisposeAsync(), controller);
    }

    [Test]
    public async Task AsyncWriterRejectsDuplicatePagesBeforePublishingHotJournal()
    {
        var expected = CreateSynchronousHotJournal();
        var backing = new InMemoryFileSystem();
        var controller = new DeterministicAsyncIoController(forceYield: true);
        var fileSystem = DeterministicAsyncFileSystem.Create(
            AsyncFileSystemAdapter.Create(backing),
            controller);
        var writer = await CompleteAsync(
            SqliteRollbackJournal.BeginAsync(
                fileSystem,
                "duplicate.db-journal",
                recordCount: 2,
                expected.ChecksumNonce,
                initialDatabasePageCount: 1,
                expected.PageSize),
            controller);
        await CompleteAsync(writer.WritePageRecordAsync(1, expected.OriginalPage), controller);

        var exception = Assert.ThrowsAsync<InvalidOperationException>(
            async () => await writer.WritePageRecordAsync(1, expected.OriginalPage));

        exception.Message.Should().Contain("already contains page 1");
        controller.GetOperationCount(FileSystemOperation.Write).Should().Be(4);
        SqliteRollbackJournal.IsHot(backing, "duplicate.db-journal").Should().BeFalse();
        await CompleteAsync(writer.DisposeAsync(), controller);
    }

    private static HotJournalFixture CreateSynchronousHotJournal()
    {
        var fileSystem = new InMemoryFileSystem();
        const string databasePath = "sync.db";
        const string journalPath = "sync.db-journal";
        using var pageStore = SqlitePageStore.Create(fileSystem, databasePath);
        var originalPage = pageStore.ReadRawPage(1);

        Assert.Throws<InvalidOperationException>(
            () => SqliteRollbackJournal.Commit(
                fileSystem,
                journalPath,
                pageStore,
                [1],
                () => throw new InvalidOperationException("retain hot journal")));

        var journalBytes = ReadAll(fileSystem, journalPath);
        var checksumNonce = BinaryPrimitives.ReadUInt32BigEndian(journalBytes.AsSpan(12));
        return new HotJournalFixture(
            journalBytes,
            originalPage,
            checksumNonce,
            pageStore.PageSize);
    }

    private static byte[] ReadAll(IFileSystem fileSystem, string path)
    {
        using var file = fileSystem.OpenFile(path, FileOpenMode.OpenExisting, readOnly: true);
        var bytes = new byte[checked((int)file.Length)];
        file.Read(0, bytes).Should().Be(bytes.Length);
        return bytes;
    }

    private static void WriteAll(IFileSystem fileSystem, string path, ReadOnlySpan<byte> bytes)
    {
        using var file = fileSystem.OpenFile(path, FileOpenMode.CreateNew);
        file.Write(0, bytes);
        file.SetLength(bytes.Length);
    }

    private static async Task CompleteAsync(
        ValueTask operation,
        DeterministicAsyncIoController controller)
    {
        var task = operation.AsTask();
        await ReleaseRemainingAsync(task, controller);
        await task;
    }

    private static async Task<T> CompleteAsync<T>(
        ValueTask<T> operation,
        DeterministicAsyncIoController controller)
    {
        var task = operation.AsTask();
        await ReleaseRemainingAsync(task, controller);
        return await task;
    }

    private static async Task ReleaseRemainingAsync(
        Task operation,
        DeterministicAsyncIoController controller)
    {
        while (!operation.IsCompleted)
            await ReleaseNextAsync(operation, controller);
    }

    private static async Task ReleaseNextAsync(
        Task operation,
        DeterministicAsyncIoController controller)
    {
        await WaitForPendingYieldAsync(operation, controller);
        if (!operation.IsCompleted)
            controller.ReleaseNext();
    }

    private static async Task WaitForPendingYieldAsync(
        Task operation,
        DeterministicAsyncIoController controller)
    {
        while (!operation.IsCompleted && controller.PendingYieldCount == 0)
            await Task.Yield();
    }

    private sealed record HotJournalFixture(
        byte[] JournalBytes,
        byte[] OriginalPage,
        uint ChecksumNonce,
        int PageSize);
}
