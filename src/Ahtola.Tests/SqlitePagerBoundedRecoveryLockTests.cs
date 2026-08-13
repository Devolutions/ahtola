using AwesomeAssertions;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class SqlitePagerBoundedRecoveryLockTests
{
    [Test]
    public void RecoveryReportsWriterBusyThenTruncatesTailBeforeCheckpointReset()
    {
        var fileSystem = new InMemoryFileSystem();
        var locks = new SqlitePagerLockManager();
        const string databasePath = "bounded-recovery.db";
        const string walPath = databasePath + "-wal";
        var committedPage = CreatePage(SqlitePageSize.Default, 0xA1);

        using var writerPager = SqlitePager.Create(
            fileSystem,
            databasePath,
            walPath,
            CreateWalHeader(),
            lockManager: locks);
        CommitPageTwo(writerPager, committedPage);
        using var recoveringPager = SqlitePager.Open(
            fileSystem,
            databasePath,
            walPath,
            lockManager: locks);
        using (var blockingWriter = writerPager.BeginTransaction(targetDatabaseSizeInPages: 2))
        {
            using (var wal = SqliteWalFile.Open(fileSystem, walPath))
            {
                wal.AppendFrame(2, CreatePage(SqlitePageSize.Default, 0xB2));
                wal.Flush();
            }

            var busy = Assert.Throws<SqlitePagerBusyException>(
                () => recoveringPager.RecoverUncommittedWalTail(TimeSpan.Zero));

            busy!.Operation.Should().Be(SqlitePagerLockOperation.Writer);
            busy.Timeout.Should().Be(TimeSpan.Zero);
            recoveringPager.State.Should().Be(SqlitePagerState.Ready);
            locks.State.Should().Be(SqlitePagerLockState.Writer);
        }

        recoveringPager.RecoverUncommittedWalTail(TimeSpan.Zero);
        recoveringPager.RecoveryInfo.LastValidFrameNumber.Should().Be(2);
        recoveringPager.RecoveryInfo.LastCommittedFrameNumber.Should().Be(2);
        recoveringPager.ReadCommittedPage(2).Should().Equal(committedPage);
        locks.State.Should().Be(SqlitePagerLockState.Unlocked);

        recoveringPager.CheckpointToMainStoreAndResetWal().RetainedCommittedFrameCount.Should().Be(0);
        using var reopened = SqlitePager.Open(fileSystem, databasePath, walPath, lockManager: locks);
        reopened.RecoveryInfo.IsDurablyCheckpointedMainStore.Should().BeTrue();
        reopened.ReadCommittedPage(2).Should().Equal(committedPage);
    }

    [Test]
    public void RecoveryFlushFaultFaultsOnlyRecoveringPagerAndPreservesCommittedReopen()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        var locks = new SqlitePagerLockManager();
        const string databasePath = "bounded-recovery-fault.db";
        const string walPath = databasePath + "-wal";
        var committedPage = CreatePage(SqlitePageSize.Default, 0xC1);

        using var pager = SqlitePager.Create(
            fileSystem,
            databasePath,
            walPath,
            CreateWalHeader(),
            lockManager: locks);
        CommitPageTwo(pager, committedPage);
        using (var wal = SqliteWalFile.Open(fileSystem, walPath))
        {
            wal.AppendFrame(2, CreatePage(SqlitePageSize.Default, 0xC2));
            wal.Flush();
            wal.ScanRecovery().LastValidFrameNumber.Should().Be(3);
            wal.ScanRecovery().LastCommittedFrameNumber.Should().Be(2);
        }

        faults.FailNext(FileSystemOperation.FlushToDisk);

        Assert.Throws<IOException>(() => pager.RecoverUncommittedWalTail());
        pager.State.Should().Be(SqlitePagerState.Faulted);
        locks.State.Should().Be(SqlitePagerLockState.Unlocked);

        using var reopened = SqlitePager.Open(fileSystem, databasePath, walPath, lockManager: locks);
        reopened.RecoveryInfo.LastCommittedFrameNumber.Should().Be(2);
        reopened.ReadCommittedPage(2).Should().Equal(committedPage);
    }

    [Test]
    public void RecoveryBusyAtExternalRecoveryLockReleasesWriterAndPreservesPagerState()
    {
        var coordinator = new RecoveryBusyCoordinator();
        var locks = new SqlitePagerLockManager(coordinator);
        var fileSystem = new InMemoryFileSystem();
        const string databasePath = "bounded-recovery-external-busy.db";
        const string walPath = databasePath + "-wal";
        var committedPage = CreatePage(SqlitePageSize.Default, 0xD1);

        using var pager = SqlitePager.Create(
            fileSystem,
            databasePath,
            walPath,
            CreateWalHeader(),
            lockManager: locks);
        CommitPageTwo(pager, committedPage);
        using (var wal = SqliteWalFile.Open(fileSystem, walPath))
        {
            wal.AppendFrame(2, CreatePage(SqlitePageSize.Default, 0xD2));
            wal.Flush();
        }

        var timeout = TimeSpan.FromSeconds(1);
        var busy = Assert.Throws<SqlitePagerBusyException>(
            () => pager.RecoverUncommittedWalTail(timeout));

        busy!.Operation.Should().Be(SqlitePagerLockOperation.Writer);
        busy.Timeout.Should().Be(timeout);
        coordinator.LastRecoveryTimeout.Should().NotBeNull();
        coordinator.LastRecoveryTimeout!.Value.Should().BeGreaterThan(TimeSpan.Zero);
        coordinator.LastRecoveryTimeout!.Value.Should().BeLessThanOrEqualTo(timeout);
        pager.State.Should().Be(SqlitePagerState.Ready);
        locks.State.Should().Be(SqlitePagerLockState.Unlocked);

        coordinator.RejectRecovery = false;
        pager.RecoverUncommittedWalTail();
        pager.ReadCommittedPage(2).Should().Equal(committedPage);
    }

    private static void CommitPageTwo(SqlitePager pager, byte[] pageTwo)
    {
        var pageOne = pager.ReadCommittedPage(1);
        var header = SqliteDatabaseHeader.Parse(pageOne);
        (header with { DatabaseSizeInPages = 2 }).WriteTo(pageOne);

        using var transaction = pager.BeginTransaction(targetDatabaseSizeInPages: 2);
        transaction.WritePage(2, pageTwo);
        transaction.WritePage(1, pageOne);
        transaction.Commit();
    }

    private static SqliteWalHeader CreateWalHeader()
        => SqliteWalHeader.Create(
            SqlitePageSize.Default,
            salt1: 0x1020_3040,
            salt2: 0x5060_7080,
            checkpointSequence: 13);

    private static byte[] CreatePage(int pageSize, byte fill)
    {
        var page = new byte[pageSize];
        Array.Fill(page, fill);
        return page;
    }

    private sealed class RecoveryBusyCoordinator : ISqlitePagerLockCoordinator
    {
        internal bool RejectRecovery { get; set; } = true;

        internal TimeSpan? LastRecoveryTimeout { get; private set; }

        public IDisposable Acquire(SqlitePagerLockOperation operation, TimeSpan timeout) => new Lease();

        public IDisposable AcquireRecovery(TimeSpan timeout)
        {
            LastRecoveryTimeout = timeout;
            if (RejectRecovery)
                throw new SqlitePagerBusyException(SqlitePagerLockOperation.Writer, timeout);

            return new Lease();
        }

        private sealed class Lease : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
