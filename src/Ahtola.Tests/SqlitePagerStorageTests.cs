using AwesomeAssertions;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public class SqlitePagerStorageTests
{
    [Test]
    public void PagerPublishesOnlyCommittedPagesAndTransactionReadsItsOwnWrites()
    {
        var fileSystem = new InMemoryFileSystem();
        using var pager = CreatePager(fileSystem);
        var page2 = CreatePage(pager.PageSize, 0xA1);

        using (var transaction = pager.BeginTransaction(targetDatabaseSizeInPages: 2))
        {
            transaction.WritePage(2, page2);
            transaction.ReadPage(2).Should().Equal(page2);
            Assert.Throws<ArgumentOutOfRangeException>(() => pager.ReadCommittedPage(2));

            transaction.Commit();
            transaction.State.Should().Be(SqlitePagerTransactionState.Committed);
        }

        pager.State.Should().Be(SqlitePagerState.Ready);
        pager.CommittedPageCount.Should().Be(2);
        var firstRead = pager.ReadCommittedPage(2);
        firstRead.Should().Equal(page2);
        firstRead[0] = 0;
        pager.ReadCommittedPage(2).Should().Equal(page2);
    }

    [Test]
    public void PagerRejectsGrowthTransactionThatDoesNotMaterializeEveryNewPage()
    {
        var fileSystem = new InMemoryFileSystem();
        using var pager = CreatePager(fileSystem);
        using var transaction = pager.BeginTransaction(targetDatabaseSizeInPages: 3);

        transaction.WritePage(2, CreatePage(pager.PageSize, 0xB2));
        Assert.Throws<InvalidOperationException>(() => transaction.Commit());

        pager.State.Should().Be(SqlitePagerState.TransactionActive);
        transaction.State.Should().Be(SqlitePagerTransactionState.Active);
        transaction.Rollback();
        pager.State.Should().Be(SqlitePagerState.Ready);
    }

    [Test]
    public void PagerCommitsTableLeafMutationThroughItsWalOverlay()
    {
        var fileSystem = new InMemoryFileSystem();
        using var pager = CreatePager(fileSystem);
        SqliteTableLeafMutation mutation;
        using (var preparationStore = SqlitePageStore.Open(fileSystem, "main.db"))
        {
            var writer = new SqliteTableLeafMutationWriter(
                preparationStore,
                new SqliteAppendOnlyPageAllocator(preparationStore));
            mutation = writer.RewritePage(
                1,
            [
                new SqliteTableLeafCellInput(3, [0x31]),
                new SqliteTableLeafCellInput(9, [0x91, 0x92]),
            ]);
        }

        pager.CommitMutation(mutation);

        var view = SqliteTableLeafPageView.Parse(
            pager.ReadCommittedPage(1),
            pager.PageSize,
            isFirstPage: true);
        view.Cells.Select(cell => cell.Cell.RowId).Should().Equal(3, 9);
        pager.RecoveryInfo.LastCommittedFrameNumber.Should().Be(1);
    }

    [Test]
    public void PagerCheckpointInstallsPagesButRetainsWalForRecovery()
    {
        var fileSystem = new InMemoryFileSystem();
        var page2 = CreatePage(SqlitePageSize.Default, 0xC3);

        using (var pager = CreatePager(fileSystem))
        {
            using var transaction = pager.BeginTransaction(targetDatabaseSizeInPages: 2);
            transaction.WritePage(2, page2);
            transaction.Commit();

            var checkpoint = pager.CheckpointToMainStore();
            checkpoint.DatabaseSizeInPages.Should().Be(2);
            checkpoint.InstalledPageCount.Should().Be(1);
            checkpoint.RetainedCommittedFrameCount.Should().Be(1);
            pager.State.Should().Be(SqlitePagerState.Ready);
        }

        using (var store = SqlitePageStore.Open(fileSystem, "main.db", readOnly: true))
        {
            store.PageCount.Should().Be(2);
            store.ReadPage(2).Should().Equal(page2);
        }

        using var reopened = SqlitePager.Open(fileSystem, "main.db", "main.db-wal");
        reopened.ReadCommittedPage(2).Should().Equal(page2);
        reopened.RecoveryInfo.LastCommittedFrameNumber.Should().Be(1);
    }

    [Test]
    public void PagerRestartDiscardsValidUncommittedTransactionAtLastCommitBoundary()
    {
        var fileSystem = new InMemoryFileSystem();
        var committedPage = CreatePage(SqlitePageSize.Default, 0xC4);
        var uncommittedPage = CreatePage(SqlitePageSize.Default, 0xC5);

        using (var pager = CreatePager(fileSystem))
        {
            using var transaction = pager.BeginTransaction(targetDatabaseSizeInPages: 2);
            transaction.WritePage(2, committedPage);
            transaction.Commit();
        }

        using (var wal = SqliteWalFile.Open(fileSystem, "main.db-wal"))
        {
            wal.AppendFrame(2, uncommittedPage);
            wal.Flush();
        }

        using (var recovered = SqlitePager.Open(fileSystem, "main.db", "main.db-wal"))
        {
            recovered.RecoveryInfo.LastValidFrameNumber.Should().Be(2);
            recovered.RecoveryInfo.LastCommittedFrameNumber.Should().Be(1);
            recovered.CommittedPageCount.Should().Be(2);
            recovered.ReadCommittedPage(2).Should().Equal(committedPage);
        }

        using var repairedWal = SqliteWalFile.Open(fileSystem, "main.db-wal", readOnly: true);
        repairedWal.ScanRecovery().LastValidFrameNumber.Should().Be(1);
        repairedWal.ScanRecovery().LastCommittedFrameNumber.Should().Be(1);
    }

    [Test]
    public void PagerRestartDiscardsChecksumCorruptUncommittedTail()
    {
        var fileSystem = new InMemoryFileSystem();
        var committedPage = CreatePage(SqlitePageSize.Default, 0xD4);
        var uncommittedPage = CreatePage(SqlitePageSize.Default, 0xE5);

        using (var pager = CreatePager(fileSystem))
        {
            using var transaction = pager.BeginTransaction(targetDatabaseSizeInPages: 2);
            transaction.WritePage(2, committedPage);
            transaction.Commit();
        }

        long corruptFrameOffset;
        using (var wal = SqliteWalFile.Open(fileSystem, "main.db-wal"))
        {
            wal.AppendFrame(2, uncommittedPage);
            wal.Flush();
            corruptFrameOffset = SqliteWalHeader.Size + wal.FrameSize;
        }
        using (var rawWal = fileSystem.OpenFile("main.db-wal", FileOpenMode.OpenExisting))
            rawWal.Write(corruptFrameOffset + SqliteWalFrameHeader.Size, [0xFF]);

        using (var recovered = SqlitePager.Open(fileSystem, "main.db", "main.db-wal"))
        {
            recovered.RecoveryInfo.StopReason.Should().Be(SqliteWalRecoveryStopReason.InvalidFrame);
            recovered.CommittedPageCount.Should().Be(2);
            recovered.ReadCommittedPage(2).Should().Equal(committedPage);
        }

        using var repairedWal = SqliteWalFile.Open(fileSystem, "main.db-wal", readOnly: true);
        repairedWal.ScanRecovery().Should().Be(new SqliteWalRecoveryInfo(
            LastValidFrameNumber: 1,
            LastCommittedFrameNumber: 1,
            LastCommittedDatabaseSizeInPages: 2,
            LastCommittedByteLength: SqliteWalHeader.Size + repairedWal.FrameSize,
            StopReason: SqliteWalRecoveryStopReason.EndOfFile));
    }

    [Test]
    public void PagerFailureBeforeCommitRequiresRestartAndPreservesPriorView()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);

        using (var pager = CreatePager(fileSystem))
        {
            faults.FailNext(FileSystemOperation.Write);
            using var transaction = pager.BeginTransaction(targetDatabaseSizeInPages: 2);
            transaction.WritePage(2, CreatePage(pager.PageSize, 0xF6));

            Assert.Throws<IOException>(() => transaction.Commit());
            transaction.State.Should().Be(SqlitePagerTransactionState.Faulted);
            pager.State.Should().Be(SqlitePagerState.Faulted);
            Assert.Throws<InvalidOperationException>(() => pager.ReadCommittedPage(1));
        }

        using var recovered = SqlitePager.Open(fileSystem, "main.db", "main.db-wal");
        recovered.CommittedPageCount.Should().Be(1);
        recovered.RecoveryInfo.LastCommittedFrameNumber.Should().Be(0);
        recovered.ReadCommittedPage(1).Length.Should().Be(SqlitePageSize.Default);
    }

    [Test]
    public void PagerCheckpointFailureRetainsRecoverableWalView()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        var page2 = CreatePage(SqlitePageSize.Default, 0x77);

        using (var pager = CreatePager(fileSystem))
        {
            using var transaction = pager.BeginTransaction(targetDatabaseSizeInPages: 2);
            transaction.WritePage(2, page2);
            transaction.Commit();

            faults.FailNext(FileSystemOperation.Write);
            Assert.Throws<IOException>(() => pager.CheckpointToMainStore());
            pager.State.Should().Be(SqlitePagerState.Faulted);
        }

        using var recovered = SqlitePager.Open(fileSystem, "main.db", "main.db-wal");
        recovered.CommittedPageCount.Should().Be(2);
        recovered.ReadCommittedPage(2).Should().Equal(page2);
    }

    private static SqlitePager CreatePager(IFileSystem fileSystem)
        => SqlitePager.Create(
            fileSystem,
            "main.db",
            "main.db-wal",
            SqliteWalHeader.Create(
                SqlitePageSize.Default,
                salt1: 0x1122_3344,
                salt2: 0x5566_7788,
                checkpointSequence: 9));

    private static byte[] CreatePage(int pageSize, byte fill)
    {
        var page = new byte[pageSize];
        Array.Fill(page, fill);
        return page;
    }
}
