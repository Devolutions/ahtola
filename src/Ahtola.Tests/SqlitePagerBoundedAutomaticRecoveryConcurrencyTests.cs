using AwesomeAssertions;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class SqlitePagerBoundedAutomaticRecoveryConcurrencyTests
{
    [Test]
    public void CompetingPagerReportsBoundedRecoveryBusyThenCheckpointsRecoveredCommit()
    {
        var fileSystem = new InMemoryFileSystem();
        var coordinator = new RecoveryGateCoordinator();
        var locks = new SqlitePagerLockManager(coordinator);
        const string databasePath = "automatic-recovery-busy.db";
        const string walPath = databasePath + "-wal";
        var firstPage = CreatePage(SqlitePageSize.Default, 0xA1);
        var replacementPage = CreatePage(SqlitePageSize.Default, 0xB2);

        using var writerPager = SqlitePager.Create(
            fileSystem,
            databasePath,
            walPath,
            CreateWalHeader(),
            lockManager: locks);
        CommitPageTwo(writerPager, firstPage);
        using var recoveringPager = SqlitePager.Open(
            fileSystem,
            databasePath,
            walPath,
            lockManager: locks);

        using (var wal = SqliteWalFile.Open(fileSystem, walPath))
        {
            wal.AppendFrame(2, CreatePage(SqlitePageSize.Default, 0xC3));
            wal.Flush();
        }

        var timeout = TimeSpan.FromMilliseconds(250);
        coordinator.BlockRecovery = true;
        var busy = Assert.Throws<SqlitePagerBusyException>(
            () => recoveringPager.BeginTransaction(targetDatabaseSizeInPages: 2, busyTimeout: timeout));

        busy!.Operation.Should().Be(SqlitePagerLockOperation.Writer);
        busy.Timeout.Should().Be(timeout);
        coordinator.LastRecoveryTimeout.Should().NotBeNull();
        coordinator.LastRecoveryTimeout!.Value.Should().BeGreaterThan(TimeSpan.Zero);
        coordinator.LastRecoveryTimeout!.Value.Should().BeLessThanOrEqualTo(timeout);
        recoveringPager.State.Should().Be(SqlitePagerState.Ready);
        locks.State.Should().Be(SqlitePagerLockState.Unlocked);
        recoveringPager.ReadCommittedPage(2).Should().Equal(firstPage);

        coordinator.BlockRecovery = false;
        CommitPageTwo(recoveringPager, replacementPage);
        recoveringPager.CheckpointToMainStoreAndResetWal().RetainedCommittedFrameCount.Should().Be(0);

        using var reopened = SqlitePager.Open(fileSystem, databasePath, walPath, lockManager: locks);
        reopened.RecoveryInfo.IsDurablyCheckpointedMainStore.Should().BeTrue();
        reopened.ReadCommittedPage(2).Should().Equal(replacementPage);
    }

    [Test]
    public void AutomaticRecoveryFlushFaultFaultsOnlyOnePagerAndLeavesPeerCheckpointable()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        var locks = new SqlitePagerLockManager(new RecoveryGateCoordinator());
        const string databasePath = "automatic-recovery-fault.db";
        const string walPath = databasePath + "-wal";
        var firstPage = CreatePage(SqlitePageSize.Default, 0xD4);
        var replacementPage = CreatePage(SqlitePageSize.Default, 0xE5);

        using var faultedPager = SqlitePager.Create(
            fileSystem,
            databasePath,
            walPath,
            CreateWalHeader(),
            lockManager: locks);
        CommitPageTwo(faultedPager, firstPage);
        using var peerPager = SqlitePager.Open(
            fileSystem,
            databasePath,
            walPath,
            lockManager: locks);
        using (var wal = SqliteWalFile.Open(fileSystem, walPath))
        {
            wal.AppendFrame(2, CreatePage(SqlitePageSize.Default, 0xF6));
            wal.Flush();
        }

        faults.FailNext(FileSystemOperation.FlushToDisk);

        Assert.Throws<IOException>(() => faultedPager.BeginTransaction(targetDatabaseSizeInPages: 2));
        faultedPager.State.Should().Be(SqlitePagerState.Faulted);
        locks.State.Should().Be(SqlitePagerLockState.Unlocked);

        CommitPageTwo(peerPager, replacementPage);
        peerPager.CheckpointToMainStoreAndResetWal().RetainedCommittedFrameCount.Should().Be(0);

        using var reopened = SqlitePager.Open(fileSystem, databasePath, walPath, lockManager: locks);
        reopened.RecoveryInfo.IsDurablyCheckpointedMainStore.Should().BeTrue();
        reopened.ReadCommittedPage(2).Should().Equal(replacementPage);
    }

    [Test]
    public void AutomaticRecoveryReleaseFaultFaultsOnlyOwnerAndReleasesPeerCheckpoint()
    {
        var fileSystem = new InMemoryFileSystem();
        var coordinator = new RecoveryReleaseFaultCoordinator();
        var locks = new SqlitePagerLockManager(coordinator);
        const string databasePath = "automatic-recovery-release-fault.db";
        const string walPath = databasePath + "-wal";
        var committedPage = CreatePage(SqlitePageSize.Default, 0x17);

        using var recoveringPager = SqlitePager.Create(
            fileSystem,
            databasePath,
            walPath,
            CreateWalHeader(),
            lockManager: locks);
        CommitPageTwo(recoveringPager, committedPage);
        using var checkpointPager = SqlitePager.Open(
            fileSystem,
            databasePath,
            walPath,
            lockManager: locks);
        var recoveryReleasesBeforeFault = coordinator.RecoveryLeaseReleaseCount;
        using (var wal = SqliteWalFile.Open(fileSystem, walPath))
        {
            wal.AppendFrame(2, CreatePage(SqlitePageSize.Default, 0x28));
            wal.Flush();
        }

        coordinator.FailNextRecoveryRelease = true;

        Assert.Throws<IOException>(
            () => recoveringPager.BeginTransaction(targetDatabaseSizeInPages: 2));

        recoveringPager.State.Should().Be(SqlitePagerState.Faulted);
        coordinator.RecoveryLeaseReleaseCount.Should().Be(recoveryReleasesBeforeFault + 1);
        locks.State.Should().Be(SqlitePagerLockState.Unlocked);

        checkpointPager.CheckpointToMainStoreAndResetWal().RetainedCommittedFrameCount.Should().Be(0);
        using var reopened = SqlitePager.Open(fileSystem, databasePath, walPath, lockManager: locks);
        reopened.RecoveryInfo.IsDurablyCheckpointedMainStore.Should().BeTrue();
        reopened.ReadCommittedPage(2).Should().Equal(committedPage);
    }

    [Test]
    public void ManualRecoveryReleaseFaultFaultsOnlyOwnerAndReleasesPeerCheckpoint()
    {
        var fileSystem = new InMemoryFileSystem();
        var coordinator = new RecoveryReleaseFaultCoordinator();
        var locks = new SqlitePagerLockManager(coordinator);
        const string databasePath = "manual-recovery-release-fault.db";
        const string walPath = databasePath + "-wal";
        var committedPage = CreatePage(SqlitePageSize.Default, 0x39);

        using var recoveringPager = SqlitePager.Create(
            fileSystem,
            databasePath,
            walPath,
            CreateWalHeader(),
            lockManager: locks);
        CommitPageTwo(recoveringPager, committedPage);
        using var checkpointPager = SqlitePager.Open(
            fileSystem,
            databasePath,
            walPath,
            lockManager: locks);
        var recoveryReleasesBeforeFault = coordinator.RecoveryLeaseReleaseCount;
        using (var wal = SqliteWalFile.Open(fileSystem, walPath))
        {
            wal.AppendFrame(2, CreatePage(SqlitePageSize.Default, 0x4A));
            wal.Flush();
        }

        coordinator.FailNextRecoveryRelease = true;

        Assert.Throws<IOException>(() => recoveringPager.RecoverUncommittedWalTail());

        recoveringPager.State.Should().Be(SqlitePagerState.Faulted);
        coordinator.RecoveryLeaseReleaseCount.Should().Be(recoveryReleasesBeforeFault + 1);
        locks.State.Should().Be(SqlitePagerLockState.Unlocked);

        checkpointPager.CheckpointToMainStoreAndResetWal().RetainedCommittedFrameCount.Should().Be(0);
        using var reopened = SqlitePager.Open(fileSystem, databasePath, walPath, lockManager: locks);
        reopened.RecoveryInfo.IsDurablyCheckpointedMainStore.Should().BeTrue();
        reopened.ReadCommittedPage(2).Should().Equal(committedPage);
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

    private sealed class RecoveryGateCoordinator : ISqlitePagerLockCoordinator
    {
        internal bool BlockRecovery { get; set; }

        internal TimeSpan? LastRecoveryTimeout { get; private set; }

        public IDisposable Acquire(SqlitePagerLockOperation operation, TimeSpan timeout) => new Lease();

        public IDisposable AcquireRecovery(TimeSpan timeout)
        {
            LastRecoveryTimeout = timeout;
            if (BlockRecovery)
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

    private sealed class RecoveryReleaseFaultCoordinator : ISqlitePagerLockCoordinator
    {
        private int _failNextRecoveryRelease;

        internal bool FailNextRecoveryRelease
        {
            set => Volatile.Write(ref _failNextRecoveryRelease, value ? 1 : 0);
        }

        internal int RecoveryLeaseReleaseCount { get; private set; }

        public IDisposable Acquire(SqlitePagerLockOperation operation, TimeSpan timeout) => new Lease();

        public IDisposable AcquireRecovery(TimeSpan timeout) => new RecoveryLease(this);

        private sealed class Lease : IDisposable
        {
            public void Dispose()
            {
            }
        }

        private sealed class RecoveryLease : IDisposable
        {
            private RecoveryReleaseFaultCoordinator? _owner;

            internal RecoveryLease(RecoveryReleaseFaultCoordinator owner) => _owner = owner;

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner is null)
                    return;

                owner.RecoveryLeaseReleaseCount++;
                if (Interlocked.Exchange(ref owner._failNextRecoveryRelease, 0) != 0)
                    throw new IOException("Injected recovery lease release failure.");
            }
        }
    }
}
