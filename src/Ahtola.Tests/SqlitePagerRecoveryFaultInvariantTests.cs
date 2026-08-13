using AwesomeAssertions;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class SqlitePagerRecoveryFaultInvariantTests
{
    [Test]
    public void FailedInPlaceTailRecoveryFaultsPagerReleasesWriterAndPreservesCommittedReopen()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        var locks = new SqlitePagerLockManager(new NoOpExternalCoordinator());
        const string databasePath = "recovery-fault.db";
        const string walPath = databasePath + "-wal";
        var header = SqliteWalHeader.Create(SqlitePageSize.Default, salt1: 17, salt2: 23);
        var committedPage = CreatePage(header.PageSize, 0xA1);

        using (var pager = SqlitePager.Create(fileSystem, databasePath, walPath, header, lockManager: locks))
        {
            using (var transaction = pager.BeginTransaction(targetDatabaseSizeInPages: 2))
            {
                transaction.WritePage(2, committedPage);
                transaction.Commit();
            }

            using (var wal = SqliteWalFile.Open(fileSystem, walPath))
            {
                wal.AppendFrame(2, CreatePage(header.PageSize, 0xB2));
                wal.Flush();
            }

            faults.FailNext(FileSystemOperation.FlushToDisk);

            Assert.Throws<IOException>(() => pager.BeginTransaction(targetDatabaseSizeInPages: 2));

            pager.State.Should().Be(SqlitePagerState.Faulted);
            locks.State.Should().Be(SqlitePagerLockState.Unlocked);
            Assert.Throws<InvalidOperationException>(() => pager.BeginTransaction(targetDatabaseSizeInPages: 2));
        }

        using var reopened = SqlitePager.Open(fileSystem, databasePath, walPath, lockManager: locks);
        reopened.ReadCommittedPage(2).Should().Equal(committedPage);
        reopened.RecoveryInfo.LastCommittedFrameNumber.Should().Be(1);
    }

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
