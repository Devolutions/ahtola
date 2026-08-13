using AwesomeAssertions;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class SqlitePagerBoundedReadCacheSliceTests
{
    [Test]
    public void BoundedReadCacheEvictsLeastRecentlyUsedCleanPageAndReloads()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        using var pager = CreatePager(fileSystem, "eviction", pageCacheCapacity: 2);
        var pages = new[]
        {
            CreatePage(pager.PageSize, 0x21),
            CreatePage(pager.PageSize, 0x32),
            CreatePage(pager.PageSize, 0x43),
        };
        MaterializeCleanPages(pager, pages);

        pager.PageCacheCapacity.Should().Be(2);
        pager.CachedPageCount.Should().Be(0);
        var readsBefore = faults.GetOperationCount(FileSystemOperation.Read);

        pager.ReadCommittedPage(2).Should().Equal(pages[0]);
        pager.ReadCommittedPage(3).Should().Equal(pages[1]);
        pager.ReadCommittedPage(2).Should().Equal(pages[0]);
        pager.ReadCommittedPage(4).Should().Equal(pages[2]);
        pager.ReadCommittedPage(3).Should().Equal(pages[1]);

        pager.CachedPageCount.Should().Be(2);
        faults.GetOperationCount(FileSystemOperation.Read).Should().Be(readsBefore + 4);
    }

    [Test]
    public void ReadCacheEvictsAnImageFromAnotherCommittedViewGeneration()
    {
        var cache = new SqlitePagerReadCache(capacity: 2);
        var page = CreatePage(SqlitePageSize.Default, 0x44);

        cache.Add(pageNumber: 2, viewGeneration: 7, page);
        cache.TryGetValue(pageNumber: 2, viewGeneration: 7, out var cached).Should().BeTrue();
        cached.Should().BeSameAs(page);

        cache.TryGetValue(pageNumber: 2, viewGeneration: 8, out _).Should().BeFalse();
        cache.Count.Should().Be(0);
    }

    [Test]
    public void SnapshotReusesOnlyItsCapturedCleanMainStoreGeneration()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        var original = CreatePage(SqlitePageSize.Default, 0x45);
        var replacement = CreatePage(SqlitePageSize.Default, 0x46);

        using (var seed = CreatePager(fileSystem, "snapshot-cache-generation", pageCacheCapacity: 2))
            MaterializeCleanPages(seed, original);

        using var snapshotPager = SqlitePager.Open(
            fileSystem,
            "snapshot-cache-generation.db",
            "snapshot-cache-generation.db-wal",
            pageCacheCapacity: 2);
        snapshotPager.ReadCommittedPage(2).Should().Equal(original);
        snapshotPager.CachedPageCount.Should().Be(1);

        using var snapshot = snapshotPager.BeginReadTransaction();
        using (var writer = SqlitePager.Open(
                   fileSystem,
                   "snapshot-cache-generation.db",
                   "snapshot-cache-generation.db-wal",
                   pageCacheCapacity: 2))
        {
            using var transaction = writer.BeginTransaction(targetDatabaseSizeInPages: 2);
            transaction.WritePage(2, replacement);
            transaction.Commit();
            writer.ReadCommittedPage(2).Should().Equal(replacement);
        }

        var readsBeforeSnapshot = faults.GetOperationCount(FileSystemOperation.Read);
        snapshot.ReadPage(2).Should().Equal(original);
        faults.GetOperationCount(FileSystemOperation.Read).Should().Be(readsBeforeSnapshot);
    }

    [Test]
    public void ReadCacheKeepsCommittedImageWhenTransactionRollsBack()
    {
        var fileSystem = new InMemoryFileSystem();
        using var pager = CreatePager(fileSystem, "rollback", pageCacheCapacity: 2);
        var committed = CreatePage(pager.PageSize, 0x51);
        MaterializeCleanPages(pager, committed);
        var staged = CreatePage(pager.PageSize, 0x52);

        pager.ReadCommittedPage(2).Should().Equal(committed);
        pager.CachedPageCount.Should().Be(1);

        using (var transaction = pager.BeginTransaction(targetDatabaseSizeInPages: 2))
        {
            transaction.WritePage(2, staged);
            transaction.ReadPage(2).Should().Equal(staged);
            transaction.Rollback();
        }

        pager.State.Should().Be(SqlitePagerState.Ready);
        pager.CachedPageCount.Should().Be(1);
        pager.ReadCommittedPage(2).Should().Equal(committed);
    }

    [Test]
    public void ReadCacheInvalidatesForWalRecoveryAndCheckpointReset()
    {
        var fileSystem = new InMemoryFileSystem();
        var committed = CreatePage(SqlitePageSize.Default, 0x61);
        var replacement = CreatePage(SqlitePageSize.Default, 0x62);

        using (var pager = CreatePager(fileSystem, "recovery-reset", pageCacheCapacity: 2))
        {
            MaterializeCleanPages(pager, committed);
            pager.ReadCommittedPage(2).Should().Equal(committed);
            pager.CachedPageCount.Should().Be(1);

            using (var transaction = pager.BeginTransaction(targetDatabaseSizeInPages: 2))
            {
                transaction.WritePage(2, replacement);
                transaction.Commit();
            }

            pager.CachedPageCount.Should().Be(0);
            pager.ReadCommittedPage(2).Should().Equal(replacement);
        }

        using (var recovered = SqlitePager.Open(
                   fileSystem,
                   "recovery-reset.db",
                   "recovery-reset.db-wal",
                   pageCacheCapacity: 2))
        {
            recovered.CachedPageCount.Should().Be(0);
            recovered.ReadCommittedPage(2).Should().Equal(replacement);

            recovered.CheckpointToMainStoreAndResetWal();
            recovered.CachedPageCount.Should().Be(0);
            recovered.ReadCommittedPage(2).Should().Equal(replacement);
            recovered.CachedPageCount.Should().Be(1);
        }

        using var reopenedAfterReset = SqlitePager.Open(
            fileSystem,
            "recovery-reset.db",
            "recovery-reset.db-wal",
            pageCacheCapacity: 2);
        reopenedAfterReset.CachedPageCount.Should().Be(0);
        reopenedAfterReset.ReadCommittedPage(2).Should().Equal(replacement);
    }

    [Test]
    public void ReadCacheInvalidatesWhenAnotherPagerPublishesAWalCommit()
    {
        var fileSystem = new InMemoryFileSystem();
        var original = CreatePage(SqlitePageSize.Default, 0x63);
        var replacement = CreatePage(SqlitePageSize.Default, 0x64);

        using (var seed = CreatePager(fileSystem, "shared-invalidation", pageCacheCapacity: 2))
            MaterializeCleanPages(seed, original);

        using var cachedPager = SqlitePager.Open(
            fileSystem,
            "shared-invalidation.db",
            "shared-invalidation.db-wal",
            pageCacheCapacity: 2);
        cachedPager.ReadCommittedPage(2).Should().Equal(original);
        cachedPager.CachedPageCount.Should().Be(1);

        using (var writerPager = SqlitePager.Open(
                   fileSystem,
                   "shared-invalidation.db",
                   "shared-invalidation.db-wal",
                   pageCacheCapacity: 2))
        {
            using var transaction = writerPager.BeginTransaction(targetDatabaseSizeInPages: 2);
            transaction.WritePage(2, replacement);
            transaction.Commit();
        }

        cachedPager.ReadCommittedPage(2).Should().Equal(replacement);
        cachedPager.CachedPageCount.Should().Be(0);
    }

    [Test]
    public void EncryptedReadOnlyPagerUsesBoundedCleanPageCache()
    {
        var fileSystem = new InMemoryFileSystem();
        using var encryption = new AhtolaEncryptionOptions(
            AhtolaEncryptionCipher.Aes256Gcm,
            Convert.FromHexString("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F"));
        var first = CreateEncryptedPage(SqlitePageSize.Default, 0x71);
        var second = CreateEncryptedPage(SqlitePageSize.Default, 0x72);

        using (var pager = CreatePager(fileSystem, "encrypted-read-only", pageCacheCapacity: 1, encryption))
        {
            MaterializeCleanPages(pager, first, second);
            pager.ReadCommittedPage(2).Should().Equal(first);
            pager.ReadCommittedPage(3).Should().Equal(second);
            pager.CachedPageCount.Should().Be(1);
        }

        using var readOnly = SqlitePager.Open(
            fileSystem,
            "encrypted-read-only.db",
            "encrypted-read-only.db-wal",
            readOnly: true,
            encryption: encryption,
            pageCacheCapacity: 1);
        readOnly.IsReadOnly.Should().BeTrue();
        readOnly.ReadCommittedPage(2).Should().Equal(first);
        readOnly.ReadCommittedPage(3).Should().Equal(second);
        readOnly.CachedPageCount.Should().Be(1);
        Assert.Throws<InvalidOperationException>(() => readOnly.BeginTransaction(targetDatabaseSizeInPages: 3));
        Assert.Throws<InvalidOperationException>(() => readOnly.CheckpointToMainStore());
    }

    [Test]
    public void MainStoreReadFaultFaultsClosedAndLeavesDurablePageForReopen()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        var expected = CreatePage(SqlitePageSize.Default, 0x81);

        using (var pager = CreatePager(fileSystem, "read-fault", pageCacheCapacity: 1))
        {
            MaterializeCleanPages(pager, expected);
            pager.CachedPageCount.Should().Be(0);

            faults.FailNext(FileSystemOperation.Read);
            Assert.Throws<IOException>(() => pager.ReadCommittedPage(2));
            pager.State.Should().Be(SqlitePagerState.Faulted);
            pager.CachedPageCount.Should().Be(0);
            Assert.Throws<InvalidOperationException>(() => pager.ReadCommittedPage(2));
        }

        using var reopened = SqlitePager.Open(
            fileSystem,
            "read-fault.db",
            "read-fault.db-wal",
            pageCacheCapacity: 1);
        reopened.ReadCommittedPage(2).Should().Equal(expected);
    }

    [Test]
    public void TransactionReadFaultCanRollBackAndReleaseItsWriterLease()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        using var pager = CreatePager(fileSystem, "transaction-read-fault", pageCacheCapacity: 1);
        MaterializeCleanPages(pager, CreatePage(pager.PageSize, 0x91));

        using (var transaction = pager.BeginTransaction(targetDatabaseSizeInPages: 2))
        {
            faults.FailNext(FileSystemOperation.Read);
            Assert.Throws<IOException>(() => transaction.ReadPage(2));
            pager.State.Should().Be(SqlitePagerState.Faulted);

            transaction.Rollback();
            transaction.State.Should().Be(SqlitePagerTransactionState.RolledBack);
        }

        pager.LockManager.State.Should().Be(SqlitePagerLockState.Unlocked);
    }

    private static SqlitePager CreatePager(
        InMemoryFileSystem fileSystem,
        string name,
        int pageCacheCapacity,
        AhtolaEncryptionOptions? encryption = null)
        => SqlitePager.Create(
            fileSystem,
            $"{name}.db",
            $"{name}.db-wal",
            SqliteWalHeader.Create(
                SqlitePageSize.Default,
                salt1: 0x0102_0304,
                salt2: 0x0506_0708,
                checkpointSequence: 3),
            encryption: encryption,
            pageCacheCapacity: pageCacheCapacity);

    private static byte[][] MaterializeCleanPages(SqlitePager pager, params byte[][] pages)
    {
        using (var transaction = pager.BeginTransaction(checked((uint)(pages.Length + 1))))
        {
            for (var index = 0; index < pages.Length; index++)
                transaction.WritePage(checked((uint)(index + 2)), pages[index]);
            transaction.Commit();
        }

        pager.CheckpointToMainStoreAndResetWal();
        return pages;
    }

    private static byte[] CreatePage(int pageSize, byte fill)
    {
        var page = new byte[pageSize];
        Array.Fill(page, fill);
        return page;
    }

    private static byte[] CreateEncryptedPage(int pageSize, byte fill)
    {
        var page = CreatePage(pageSize, fill);
        page.AsSpan(pageSize - 28).Clear();
        return page;
    }
}
