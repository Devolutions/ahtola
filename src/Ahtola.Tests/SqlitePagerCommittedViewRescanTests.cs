using AwesomeAssertions;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

/// <summary>
/// A physical pager holds SQLite's complete main-file lock-byte range for its
/// whole lifetime, so no ordinary SQLite client and no other managed process can
/// change durable storage while it is open. Rebuilding the committed view on
/// every committed page read therefore repeated a full WAL recovery scan for
/// state that provably could not have changed, which dominated file-backed
/// writes because one statement reads many pages.
/// </summary>
[NonParallelizable]
public sealed class SqlitePagerCommittedViewRescanTests
{
    // The user-version field is a header value the pager does not interpret.
    private const int SchemaCookieOffset = 60;

    [Test]
    public void RepeatedPhysicalReadsReuseTheCommittedViewWithoutRescanning()
    {
        RunWithPhysicalPager(pager =>
        {
            pager.ReadCommittedPage(1);
            var afterFirstRead = pager.CommittedViewRescanCount;

            for (var i = 0; i < 50; i++)
                pager.ReadCommittedPage(1);

            pager.CommittedViewRescanCount.Should().Be(afterFirstRead);
        });
    }

    [Test]
    public void CommittingRepublishesTheViewExactlyOnceForFollowingReads()
    {
        RunWithPhysicalPager(pager =>
        {
            pager.ReadCommittedPage(1);
            var before = pager.CommittedViewRescanCount;

            using (var transaction = pager.BeginTransaction(pager.CommittedPageCount))
            {
                var page = transaction.ReadPage(1);
                page[SchemaCookieOffset] ^= 0x01;
                transaction.WritePage(1, page);
                transaction.Commit();
            }

            for (var i = 0; i < 20; i++)
                pager.ReadCommittedPage(1);

            // The pager publishes its own commit, so no read has to rediscover it.
            pager.CommittedViewRescanCount.Should().Be(before);
        });
    }

    [Test]
    public void ASecondPagerInThisProcessForcesARescanAfterItCommits()
    {
        var directory = CreateDirectory();
        try
        {
            var path = Path.Combine(directory, "shared.db");
            using var reader = CreatePager(path);
            using var writer = SqlitePager.Open(
                PhysicalFileSystem.Instance,
                path,
                path + "-wal",
                busyTimeout: TimeSpan.FromSeconds(5));

            reader.ReadCommittedPage(1);
            var before = reader.CommittedViewRescanCount;

            using (var transaction = writer.BeginTransaction(writer.CommittedPageCount))
            {
                var page = transaction.ReadPage(1);
                page[SchemaCookieOffset] ^= 0x01;
                transaction.WritePage(1, page);
                transaction.Commit();
            }

            reader.ReadCommittedPage(1);
            reader.CommittedViewRescanCount.Should().BeGreaterThan(before);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void RunWithPhysicalPager(Action<SqlitePager> body)
    {
        var directory = CreateDirectory();
        try
        {
            using var pager = CreatePager(Path.Combine(directory, "rescan.db"));
            body(pager);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static SqlitePager CreatePager(string path)
    {
        if (File.Exists(path))
        {
            return SqlitePager.Open(
                PhysicalFileSystem.Instance,
                path,
                path + "-wal",
                busyTimeout: TimeSpan.FromSeconds(5));
        }

        return SqlitePager.Create(
            PhysicalFileSystem.Instance,
            path,
            path + "-wal",
            SqliteWalHeader.Create(SqlitePageSize.Default, 1, 2),
            busyTimeout: TimeSpan.FromSeconds(5));
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Ahtola-rescan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}

