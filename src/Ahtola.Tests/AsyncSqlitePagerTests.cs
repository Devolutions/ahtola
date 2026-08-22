using AwesomeAssertions;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class AsyncSqlitePagerTests
{
    private const string DatabasePath = "main.db";
    private const string WalPath = "main.db-wal";

    [Test]
    public async Task AsyncWalCommitMatchesSynchronousDatabaseAndWalBytes()
    {
        var syncStorage = new InMemoryFileSystem();
        var asyncStorage = new InMemoryFileSystem();
        var header = CreateWalHeader();
        var page2 = CreatePage(SqlitePageSize.Default, 0xA1);

        using (var pager = SqlitePager.Create(
                   syncStorage,
                   DatabasePath,
                   WalPath,
                   header))
        {
            using var transaction = pager.BeginTransaction(2);
            transaction.WritePage(2, page2);
            transaction.Commit();
        }

        await using (var pager = await AsyncSqlitePager.CreateAsync(
                         AsyncFileSystemAdapter.Create(asyncStorage),
                         DatabasePath,
                         WalPath,
                         header))
        {
            await using var transaction = await pager.BeginTransactionAsync(2);
            await transaction.WritePageAsync(2, page2);
            await transaction.CommitAsync();
            pager.CommittedPageCount.Should().Be(2);
            pager.CommittedFrameCount.Should().Be(1);
        }

        ReadAll(asyncStorage, DatabasePath).Should().Equal(ReadAll(syncStorage, DatabasePath));
        ReadAll(asyncStorage, WalPath).Should().Equal(ReadAll(syncStorage, WalPath));
    }

    [Test]
    public async Task ReadSnapshotRemainsStableAcrossCommitAndRollback()
    {
        var storage = new InMemoryFileSystem();
        var fileSystem = AsyncFileSystemAdapter.Create(storage);
        await using var pager = await AsyncSqlitePager.CreateAsync(
            fileSystem,
            DatabasePath,
            WalPath,
            CreateWalHeader());
        await using var snapshot = await pager.BeginReadAsync();
        var originalPageOne = await snapshot.ReadPageAsync(1);
        var committedPage = CreatePage(pager.PageSize, 0xB2);

        await using (var transaction = await pager.BeginTransactionAsync(2))
        {
            await transaction.WritePageAsync(2, committedPage);
            (await transaction.ReadPageAsync(2)).Should().Equal(committedPage);
            await transaction.CommitAsync();
        }

        snapshot.PageCount.Should().Be(1);
        (await snapshot.ReadPageAsync(1)).Should().Equal(originalPageOne);
        (await pager.ReadPageAsync(2)).Should().Equal(committedPage);

        await using (var transaction = await pager.BeginTransactionAsync(2))
        {
            await transaction.WritePageAsync(2, CreatePage(pager.PageSize, 0xB3));
            await transaction.RollbackAsync();
        }
        (await pager.ReadPageAsync(2)).Should().Equal(committedPage);
    }

    [Test]
    public async Task WritableOpenRecoversUncommittedWalTailAndPreservesCommittedView()
    {
        var storage = new InMemoryFileSystem();
        var fileSystem = AsyncFileSystemAdapter.Create(storage);
        var committedPage = CreatePage(SqlitePageSize.Default, 0xC1);
        await using (var pager = await AsyncSqlitePager.CreateAsync(
                         fileSystem,
                         DatabasePath,
                         WalPath,
                         CreateWalHeader()))
        {
            await using var transaction = await pager.BeginTransactionAsync(2);
            await transaction.WritePageAsync(2, committedPage);
            await transaction.CommitAsync();
        }

        await using (var wal = await SqliteWalFile.OpenAsync(fileSystem, WalPath))
        {
            await wal.AppendFrameAsync(2, CreatePage(SqlitePageSize.Default, 0xC2));
            await wal.FlushAsync();
        }

        await using (var recovered = await AsyncSqlitePager.OpenAsync(
                         fileSystem,
                         DatabasePath,
                         WalPath))
        {
            recovered.RecoveryInfo.LastValidFrameNumber.Should().Be(2);
            recovered.RecoveryInfo.LastCommittedFrameNumber.Should().Be(1);
            recovered.CommittedFrameCount.Should().Be(1);
            (await recovered.ReadPageAsync(2)).Should().Equal(committedPage);
        }

        await using var repairedWal = await SqliteWalFile.OpenAsync(
            fileSystem,
            WalPath,
            readOnly: true);
        var recovery = await repairedWal.ScanRecoveryAsync();
        recovery.LastValidFrameNumber.Should().Be(1);
        recovery.LastCommittedFrameNumber.Should().Be(1);
        recovery.StopReason.Should().Be(SqliteWalRecoveryStopReason.EndOfFile);
    }

    [Test]
    public async Task CheckpointFlushesMainBeforeResetAndReopensFromCheckpointedView()
    {
        var storage = new InMemoryFileSystem();
        var fileSystem = AsyncFileSystemAdapter.Create(storage);
        var page2 = CreatePage(SqlitePageSize.Default, 0xD1);
        await using (var pager = await AsyncSqlitePager.CreateAsync(
                         fileSystem,
                         DatabasePath,
                         WalPath,
                         CreateWalHeader()))
        {
            await using (var transaction = await pager.BeginTransactionAsync(2))
            {
                await transaction.WritePageAsync(2, page2);
                await transaction.CommitAsync();
            }

            var retained = await pager.CheckpointToMainStoreAsync();
            retained.Should().Be(new SqliteCheckpointResult(2, 1, 1));
            var reset = await pager.CheckpointToMainStoreAndResetWalAsync();
            reset.Should().Be(new SqliteCheckpointResult(2, 1, 0));
            pager.CommittedFrameCount.Should().Be(0);
        }

        await using var store = await AsyncSqlitePageStore.OpenAsync(
            fileSystem,
            DatabasePath,
            readOnly: true);
        (await store.ReadPageAsync(2)).Should().Equal(page2);

        await using var reopened = await AsyncSqlitePager.OpenAsync(
            fileSystem,
            DatabasePath,
            WalPath,
            readOnly: true);
        reopened.CommittedPageCount.Should().Be(2);
        (await reopened.ReadPageAsync(2)).Should().Equal(page2);
    }

    [Test]
    public async Task DeleteModeCommitMatchesSynchronousPagerAndLeavesNoHotJournal()
    {
        var syncStorage = new InMemoryFileSystem();
        var asyncStorage = new InMemoryFileSystem();
        var legacyHeader = SqliteDatabaseHeader.CreateDefault() with
        {
            ReadVersion = SqliteFileFormatVersion.Legacy,
            WriteVersion = SqliteFileFormatVersion.Legacy,
        };
        var page2 = CreatePage(SqlitePageSize.Default, 0xE1);

        using (var pager = SqlitePager.CreateRollbackJournal(
                   syncStorage,
                   DatabasePath,
                   WalPath,
                   legacyHeader))
        {
            using var transaction = pager.BeginTransaction(2);
            transaction.WritePage(2, page2);
            transaction.Commit();
        }

        var asyncFileSystem = AsyncFileSystemAdapter.Create(asyncStorage);
        await using (var pager = await AsyncSqlitePager.CreateRollbackJournalAsync(
                         asyncFileSystem,
                         DatabasePath,
                         WalPath,
                         legacyHeader))
        {
            await using var transaction = await pager.BeginTransactionAsync(2);
            await transaction.WritePageAsync(2, page2);
            await transaction.CommitAsync();
        }

        ReadAll(asyncStorage, DatabasePath).Should().Equal(ReadAll(syncStorage, DatabasePath));
        asyncStorage.FileExists(DatabasePath + "-journal").Should().BeFalse();
        await using var reopened = await AsyncSqlitePager.OpenAsync(
            asyncFileSystem,
            DatabasePath,
            WalPath);
        reopened.JournalMode.Should().Be(SqliteJournalMode.Delete);
        (await reopened.ReadPageAsync(2)).Should().Equal(page2);
    }

    [Test]
    public async Task OpenRecoversHotRollbackJournalBeforePublishingDeleteModeView()
    {
        var storage = new InMemoryFileSystem();
        var legacyHeader = SqliteDatabaseHeader.CreateDefault() with
        {
            ReadVersion = SqliteFileFormatVersion.Legacy,
            WriteVersion = SqliteFileFormatVersion.Legacy,
        };
        using (var pager = SqlitePager.CreateRollbackJournal(
                   storage,
                   DatabasePath,
                   WalPath,
                   legacyHeader))
        {
        }

        byte[] originalPage;
        using (var store = SqlitePageStore.Open(storage, DatabasePath))
        {
            originalPage = store.ReadPage(1);
            var changedPage = originalPage.ToArray();
            changedPage[200] ^= 0x5A;
            Assert.Throws<IOException>(() => SqliteRollbackJournal.Commit(
                storage,
                DatabasePath + "-journal",
                store,
                [1],
                () =>
                {
                    store.WritePage(1, changedPage);
                    store.Flush();
                    throw new IOException("simulate a crash before journal deletion");
                }));
        }

        await using var recovered = await AsyncSqlitePager.OpenAsync(
            AsyncFileSystemAdapter.Create(storage),
            DatabasePath,
            WalPath);
        recovered.JournalMode.Should().Be(SqliteJournalMode.Delete);
        (await recovered.ReadPageAsync(1)).Should().Equal(originalPage);
        storage.FileExists(DatabasePath + "-journal").Should().BeFalse();
    }

    [Test]
    public async Task CancellationLeavesPagerReadableAndCommitFaultRequiresRecovery()
    {
        var storage = new InMemoryFileSystem();
        var controller = new DeterministicAsyncIoController(forceYield: true);
        var fileSystem = DeterministicAsyncFileSystem.Create(
            AsyncFileSystemAdapter.Create(storage),
            controller);
        var pager = await CompleteAsync(
            AsyncSqlitePager.CreateAsync(
                fileSystem,
                DatabasePath,
                WalPath,
                CreateWalHeader()),
            controller);
        using var cancellation = new CancellationTokenSource();

        var canceledRead = pager.ReadPageAsync(1, cancellation.Token).AsTask();
        await WaitForPendingYieldAsync(canceledRead, controller);
        cancellation.Cancel();
        var canceled = Assert.CatchAsync<OperationCanceledException>(async () => await canceledRead);
        canceled!.CancellationToken.Should().Be(cancellation.Token);
        controller.ReleaseNext();
        pager.State.Should().Be(SqlitePagerState.Ready);

        var transaction = await CompleteAsync(pager.BeginTransactionAsync(2), controller);
        await CompleteAsync(
            transaction.WritePageAsync(2, CreatePage(pager.PageSize, 0xF1)),
            controller);
        var expected = new IOException("injected async pager WAL failure");
        controller.FailNext(FileSystemOperation.Write, expected);
        var commit = transaction.CommitAsync().AsTask();
        await ReleaseUntilCompleteAsync(commit, controller);
        var failure = Assert.ThrowsAsync<IOException>(async () => await commit);
        failure.Should().BeSameAs(expected);
        transaction.State.Should().Be(SqlitePagerTransactionState.Faulted);
        pager.State.Should().Be(SqlitePagerState.Faulted);
        await CompleteAsync(transaction.DisposeAsync(), controller);
        await CompleteAsync(pager.DisposeAsync(), controller);

        await using var recovered = await AsyncSqlitePager.OpenAsync(
            AsyncFileSystemAdapter.Create(storage),
            DatabasePath,
            WalPath);
        recovered.CommittedPageCount.Should().Be(1);
    }

    private static SqliteWalHeader CreateWalHeader()
        => SqliteWalHeader.Create(
            SqlitePageSize.Default,
            salt1: 0x1122_3344,
            salt2: 0x5566_7788,
            checkpointSequence: 9);

    private static byte[] CreatePage(int pageSize, byte fill)
    {
        var page = new byte[pageSize];
        Array.Fill(page, fill);
        return page;
    }

    private static byte[] ReadAll(IFileSystem fileSystem, string path)
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

    private static async Task WaitForPendingYieldAsync(
        Task task,
        DeterministicAsyncIoController controller)
    {
        while (!task.IsCompleted && controller.PendingYieldCount == 0)
            await Task.Yield();
    }
}
