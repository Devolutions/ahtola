using AwesomeAssertions;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

/// <summary>
/// The pager acquires a reader lease for every committed page read. Taking a
/// fresh operating-system lock each time made every read a syscall round trip,
/// which dominated file-backed autocommit writes because a single statement
/// reads many pages. These tests pin the retained shared reader range and the
/// exclusive handoff that must still be able to take it away.
/// </summary>
public sealed class SqlitePagerSharedReaderLockTests
{
    [Test]
    public void RepeatedCommittedPageReadsReuseOneCoordinatorReaderRange()
    {
        var coordinator = new CountingCoordinator();
        var locks = new SqlitePagerLockManager(coordinator);
        var fileSystem = new InMemoryFileSystem(new DeterministicFaultInjector());
        using var pager = SqlitePager.Create(
            fileSystem,
            "shared-reader.db",
            "shared-reader.db-wal",
            CreateWalHeader(),
            lockManager: locks);

        for (var i = 0; i < 25; i++)
            pager.ReadCommittedPage(1).Length.Should().Be(SqlitePageSize.Default);

        coordinator.ReaderAcquisitions.Should().Be(1);
        coordinator.ReleasedLeases.Should().Be(0);
    }

    [Test]
    public void AnExclusiveRoleTakesBackTheRetainedReaderRange()
    {
        var coordinator = new CountingCoordinator();
        var locks = new SqlitePagerLockManager(coordinator);
        var fileSystem = new InMemoryFileSystem(new DeterministicFaultInjector());
        using var pager = SqlitePager.Create(
            fileSystem,
            "reader-handoff.db",
            "reader-handoff.db-wal",
            CreateWalHeader(),
            lockManager: locks);

        pager.ReadCommittedPage(1);
        coordinator.ReaderAcquisitions.Should().Be(1);

        // A checkpoint excludes readers, so the retained range must be released
        // before its own exclusive acquisition rather than deadlocking against it.
        pager.CheckpointToMainStoreAndResetWal();
        coordinator.ReleasedLeases.Should().BeGreaterThan(0);

        pager.ReadCommittedPage(1).Length.Should().Be(SqlitePageSize.Default);
        coordinator.ReaderAcquisitions.Should().Be(2);
    }

    [Test]
    public void DisposingThePagerReleasesTheRetainedReaderRange()
    {
        var coordinator = new CountingCoordinator();
        var locks = new SqlitePagerLockManager(coordinator);
        var fileSystem = new InMemoryFileSystem(new DeterministicFaultInjector());
        var pager = SqlitePager.Create(
            fileSystem,
            "reader-dispose.db",
            "reader-dispose.db-wal",
            CreateWalHeader(),
            lockManager: locks);

        pager.ReadCommittedPage(1);
        coordinator.LiveLeases.Should().Be(1);

        pager.Dispose();

        coordinator.LiveLeases.Should().Be(0);
    }

    private static SqliteWalHeader CreateWalHeader()
        => SqliteWalHeader.Create(
            SqlitePageSize.Default,
            salt1: 0x1020_3040,
            salt2: 0x5060_7080,
            checkpointSequence: 1);

    private sealed class CountingCoordinator : ISqlitePagerLockCoordinator
    {
        private int _readerAcquisitions;
        private int _releasedLeases;
        private int _liveLeases;

        public int ReaderAcquisitions => Volatile.Read(ref _readerAcquisitions);

        public int ReleasedLeases => Volatile.Read(ref _releasedLeases);

        public int LiveLeases => Volatile.Read(ref _liveLeases);

        public IDisposable Acquire(SqlitePagerLockOperation operation, TimeSpan timeout)
        {
            if (operation == SqlitePagerLockOperation.Reader)
            {
                Interlocked.Increment(ref _readerAcquisitions);
                Interlocked.Increment(ref _liveLeases);
                return new Lease(this);
            }

            return new Lease(null);
        }

        public IDisposable AcquireRecovery(TimeSpan timeout) => new Lease(null);

        private sealed class Lease(CountingCoordinator? owner) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (owner is null || Interlocked.Exchange(ref _disposed, 1) == 1)
                    return;

                Interlocked.Increment(ref owner._releasedLeases);
                Interlocked.Decrement(ref owner._liveLeases);
            }
        }
    }
}
