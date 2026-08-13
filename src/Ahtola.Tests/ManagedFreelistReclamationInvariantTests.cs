using System.Buffers.Binary;
using System.Text;
using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public class ManagedFreelistReclamationInvariantTests
{
    [Test]
    public void FreelistCodecRoundTripsEveryPageAcrossMultipleTrunks()
    {
        var template = SqliteDatabaseHeader.CreateDefault();
        var trunkLeafCapacity = (template.UsableSpace - (2 * sizeof(uint))) / sizeof(uint);
        var targetPageCount = checked((uint)(trunkLeafCapacity + 4));
        var freelist = SqliteFreelist.Create(
            usedPageCount: 1,
            targetPageCount: targetPageCount,
            pageSize: template.PageSize,
            usableSpace: template.UsableSpace);
        var header = template with
        {
            ChangeCounter = 1,
            VersionValidFor = 1,
            DatabaseSizeInPages = targetPageCount,
            FirstFreelistTrunkPage = freelist.FirstTrunkPage,
            FreelistPageCount = freelist.PageCount,
        };
        var images = freelist.PageImages.ToDictionary(image => image.PageNumber, image => image.ToArray());

        var parsed = SqliteFreelist.Read(header, targetPageCount, pageNumber => images[pageNumber]);

        parsed.PageNumbers.Should().Equal(freelist.PageNumbers);
        parsed.TrunkPageNumbers.Should().HaveCount(2);
        parsed.PageCount.Should().Be(targetPageCount - 1);
        parsed.PageImages.Should().BeEmpty();
        foreach (var leafPage in parsed.PageNumbers.Except(parsed.TrunkPageNumbers))
        {
            images[leafPage].AsSpan().IndexOfAnyExcept((byte)0)
                .Should()
                .Be(-1);
        }
    }

    [Test]
    public void FullRewriteReclaimsDeletedPagesReusesTheBoundedFileAndSurvivesReopen()
    {
        var path = CreateDatabasePath();
        var retiredPayload = "retired-" + new string('q', 240);
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, payload TEXT);");
                Execute(connection, "CREATE INDEX t_payload ON t(payload);");
                InsertRows(connection, 1, 100, retiredPayload);
            }

            var grownPageCount = ReadHeader(path).DatabaseSizeInPages;

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "DELETE FROM t;");
                QueryCount(connection, "SELECT COUNT(*) FROM t;").Should().Be(0);

                QueryCount(connection, "SELECT COUNT(*) FROM t;").Should().Be(0);

                InsertRows(connection, 1, 12, "replacement-" + new string('r', 32));
            }

            SqliteDatabaseHeader header;
            SqliteFreelist freelist;
            using (var pager = SqlitePager.Open(
                       PhysicalFileSystem.Instance,
                       path,
                       path + "-wal",
                       readOnly: true))
            {
                header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
                header.DatabaseSizeInPages.Should().Be(grownPageCount);
                freelist = SqliteFreelist.Read(
                    header,
                    pager.CommittedPageCount,
                    pager.ReadCommittedPage);
                freelist.PageCount.Should().Be(header.FreelistPageCount);
                freelist.PageNumbers.Should().NotBeEmpty();
                freelist.TrunkPageNumbers.Should().NotBeEmpty();
                foreach (var leafPage in freelist.PageNumbers.Except(freelist.TrunkPageNumbers))
                {
                    pager.ReadCommittedPage(leafPage).AsSpan().IndexOfAnyExcept((byte)0)
                        .Should()
                        .Be(-1);
                }
            }

            File.ReadAllBytes(path).AsSpan().IndexOf(Encoding.UTF8.GetBytes(retiredPayload))
                .Should()
                .Be(-1);

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                QueryCount(connection, "SELECT COUNT(*) FROM t;").Should().Be(12);
                Query(connection, "SELECT payload FROM t ORDER BY payload;")
                    .Should()
                    .HaveCount(12);
            }

            var verificationPath = path + ".verify.db";
            File.Copy(path, verificationPath, overwrite: true);
            try
            {
                using var sqlite = new MsData.SqliteConnection($"Data Source={verificationPath}");
                sqlite.Open();
                using var integrity = sqlite.CreateCommand();
                integrity.CommandText = "PRAGMA integrity_check;";
                integrity.ExecuteScalar().Should().Be("ok");

                using var freelistCount = sqlite.CreateCommand();
                freelistCount.CommandText = "PRAGMA freelist_count;";
                Convert.ToUInt32(freelistCount.ExecuteScalar()).Should().Be(header.FreelistPageCount);
            }
            finally
            {
                MsData.SqliteConnection.ClearAllPools();
                DeleteDatabase(verificationPath);
            }
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void FailedDeleteRewriteRecoversThePriorReachablePages()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);

        using (var database = EmbeddedDatabase.OpenFile("reclaim-recovery.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, payload TEXT);");
            InsertRows(connection, 1, 40, "committed-" + new string('c', 160));

            faults.FailOnOccurrence(
                FileSystemOperation.Write,
                faults.GetOperationCount(FileSystemOperation.Write) + 3);
            Assert.Throws<IOException>(() => Execute(connection, "DELETE FROM t;"));
        }

        using var recovered = EmbeddedDatabase.OpenFile("reclaim-recovery.db", fileSystem);
        using var recoveredConnection = recovered.Connect();
        QueryCount(recoveredConnection, "SELECT COUNT(*) FROM t;").Should().Be(40);
        Query(recoveredConnection, "SELECT payload FROM t WHERE id = 1;")
            .Single()[0]
            .AsText()
            .Should()
            .StartWith("committed-");
    }

    [Test]
    public void ReopenRejectsFreelistThatAliasesAReachableRootPage()
    {
        var fileSystem = new InMemoryFileSystem();
        SqliteDatabaseHeader header;
        SqliteFreelist freelist;
        using (var database = EmbeddedDatabase.OpenFile("reclaim-corrupt.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, payload TEXT);");
            Execute(connection, $"INSERT INTO t VALUES (1, '{new string('x', 10_000)}');");
            Execute(connection, "UPDATE t SET payload = 'small' WHERE id = 1;");
        }

        using (var pager = SqlitePager.Open(fileSystem, "reclaim-corrupt.db", "reclaim-corrupt.db-wal"))
        {
            header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            freelist = SqliteFreelist.Read(header, pager.CommittedPageCount, pager.ReadCommittedPage);
        }

        freelist.PageCount.Should().BeGreaterThan(1);
        var trunkPage = freelist.FirstTrunkPage;
        using (var store = SqlitePageStore.Open(fileSystem, "reclaim-corrupt.db"))
        {
            var trunk = store.ReadPage(trunkPage);
            BinaryPrimitives.ReadUInt32BigEndian(trunk.AsSpan(sizeof(uint))).Should().BeGreaterThan(0);
            BinaryPrimitives.WriteUInt32BigEndian(trunk.AsSpan(2 * sizeof(uint)), 2);
            store.WritePage(trunkPage, trunk);
            store.Flush();
        }

        fileSystem.DeleteFile("reclaim-corrupt.db-wal");
        using (SqliteWalFile.Create(
                   fileSystem,
                   "reclaim-corrupt.db-wal",
                   SqliteWalHeader.Create(header.PageSize, salt1: 11, salt2: 12)))
        {
        }

        var reopen = () => EmbeddedDatabase.OpenFile("reclaim-corrupt.db", fileSystem);
        reopen.Should().Throw<EmbeddedSqlException>().WithMessage("*allocation map*");
    }

    private static SqliteDatabaseHeader ReadHeader(string path)
    {
        using var pager = SqlitePager.Open(
            PhysicalFileSystem.Instance,
            path,
            path + "-wal",
            readOnly: true);
        return SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
    }

    private static void InsertRows(EmbeddedConnection connection, int firstId, int count, string payload)
    {
        var rows = Enumerable.Range(firstId, count)
            .Select(id => $"({id}, '{payload}{id:D3}')");
        Execute(connection, $"INSERT INTO t VALUES {string.Join(", ", rows)};");
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private static long QueryCount(EmbeddedConnection connection, string sql)
        => Query(connection, sql).Single()[0].AsInteger();

    private static List<SqlValue[]> Query(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var row = new SqlValue[statement.GetColumnCount()];
            for (var ordinal = 0; ordinal < row.Length; ordinal++)
                row[ordinal] = statement.GetValue(ordinal);
            rows.Add(row);
        }

        return rows;
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "managed-freelist-reclamation-invariant-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"reclaim-{Guid.NewGuid():N}.db");
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }
}
