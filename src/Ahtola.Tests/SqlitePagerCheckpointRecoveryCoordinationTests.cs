using AwesomeAssertions;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class SqlitePagerCheckpointRecoveryCoordinationTests
{
    [Test]
    public void CheckpointRejectsAnUncommittedWriterTailUntilAnotherConnectionRecoversIt()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        var locks = new SqlitePagerLockManager(new NoOpExternalCoordinator());
        const string databasePath = "checkpoint-recovery-coordination.db";
        const string walPath = databasePath + "-wal";
        var abandonedPage = CreatePage(SqlitePageSize.Default, 0xA1);
        var recoveredPage = CreatePage(SqlitePageSize.Default, 0xB2);

        using var failedWriterPager = SqlitePager.Create(
            fileSystem,
            databasePath,
            walPath,
            CreateWalHeader(),
            lockManager: locks);
        using var recoveringPager = SqlitePager.Open(
            fileSystem,
            databasePath,
            walPath,
            lockManager: locks);

        using (var transaction = failedWriterPager.BeginTransaction(targetDatabaseSizeInPages: 2))
        {
            transaction.WritePage(2, abandonedPage);
            var pageOne = failedWriterPager.ReadCommittedPage(1);
            var pageOneHeader = SqliteDatabaseHeader.Parse(pageOne);
            (pageOneHeader with { DatabaseSizeInPages = 2 }).WriteTo(pageOne);
            transaction.WritePage(1, pageOne);
            faults.FailOnOccurrence(
                FileSystemOperation.Write,
                faults.GetOperationCount(FileSystemOperation.Write) + 2);

            Assert.Throws<IOException>(() => transaction.Commit());
            transaction.State.Should().Be(SqlitePagerTransactionState.Faulted);
        }

        failedWriterPager.State.Should().Be(SqlitePagerState.Faulted);
        locks.State.Should().Be(SqlitePagerLockState.Unlocked);
        recoveringPager.ReadCommittedPage(1).Length.Should().Be(SqlitePageSize.Default);
        recoveringPager.RecoveryInfo.LastValidFrameNumber.Should().Be(1);
        recoveringPager.RecoveryInfo.LastCommittedFrameNumber.Should().Be(0);

        var checkpointFailure = Assert.Throws<InvalidOperationException>(
            () => recoveringPager.CheckpointToMainStoreAndResetWal());
        checkpointFailure!.Message.Should().Contain("recover it under the writer lock");
        recoveringPager.State.Should().Be(SqlitePagerState.Ready);
        locks.State.Should().Be(SqlitePagerLockState.Unlocked);

        using (var recovery = recoveringPager.BeginTransaction(targetDatabaseSizeInPages: 2))
        {
            recovery.WritePage(2, recoveredPage);
            recovery.Commit();
        }

        recoveringPager.ReadCommittedPage(2).Should().Equal(recoveredPage);
        locks.State.Should().Be(SqlitePagerLockState.Unlocked);

        using var reopened = SqlitePager.Open(fileSystem, databasePath, walPath, lockManager: locks);
        reopened.ReadCommittedPage(2).Should().Equal(recoveredPage);
        reopened.RecoveryInfo.LastCommittedFrameNumber.Should().Be(1);
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

    private sealed class NoOpExternalCoordinator : ISqlitePagerLockCoordinator
    {
        public IDisposable Acquire(SqlitePagerLockOperation operation, TimeSpan timeout) => new Lease();

        public IDisposable AcquireRecovery(TimeSpan timeout) => new Lease();

        private sealed class Lease : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
