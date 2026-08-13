using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public class PersistentSecondaryIndexFileStoreTests
{
    [Test]
    public void PersistsBinarySecondaryIndexAsLinkedSqliteLeafAndRealSqliteAcceptsIt()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE person(id INTEGER PRIMARY KEY, name TEXT);");
                Execute(connection, "INSERT INTO person VALUES (1, 'zoe');");
                Execute(connection, "INSERT INTO person VALUES (2, 'ada');");
                Execute(connection, "INSERT INTO person VALUES (3, 'zoe');");
                Execute(connection, "CREATE INDEX person_name_binary ON person(name);");
            }

            using (var pager = SqlitePager.Open(
                       PhysicalFileSystem.Instance,
                       path,
                       path + "-wal",
                       readOnly: true))
            {
                var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
                var schema = SqliteTableLeafPageView.Parse(
                    pager.ReadCommittedPage(1),
                    header.UsableSpace,
                    isFirstPage: true);
                var indexEntry = schema.Cells
                    .Select(cell => SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding))
                    .Single(values => values[0].AsText() == "index"
                                      && values[1].AsText() == "person_name_binary");
                indexEntry[2].AsText().Should().Be("person");
                var rootPage = checked((uint)indexEntry[3].AsInteger());
                rootPage.Should().BeGreaterThanOrEqualTo(2);

                var index = SqliteIndexLeafPageView.Parse(
                    pager.ReadCommittedPage(rootPage),
                    header.UsableSpace,
                    header.TextEncoding,
                    overflowReader: new SqliteOverflowChainReader(pager, header));
                index.Header.PageType.Should().Be(SqliteBtreePageType.IndexLeaf);
                index.HasVerifiedRecordOrdering.Should().BeTrue();
                index.Cells.Should().HaveCount(3);
                index.GetRecord(0).Should().Equal(Record(SqlValue.Text("ada"), SqlValue.Integer(2)));
                index.GetRecord(1).Should().Equal(Record(SqlValue.Text("zoe"), SqlValue.Integer(1)));
                index.GetRecord(2).Should().Equal(Record(SqlValue.Text("zoe"), SqlValue.Integer(3)));
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

                using var plan = sqlite.CreateCommand();
                plan.CommandText = "EXPLAIN QUERY PLAN SELECT id FROM person INDEXED BY person_name_binary WHERE name = 'zoe';";
                using var reader = plan.ExecuteReader();
                reader.Read().Should().BeTrue();
                reader.GetString(3).Should().Contain("person_name_binary");
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
    public void ReopenRecoversAndReconstructsBoundedSecondaryIndexMetadata()
    {
        var fileSystem = new InMemoryFileSystem();
        var crashed = EmbeddedDatabase.OpenFile("secondary-index-recovery.db", fileSystem);
        var crashedConnection = crashed.Connect();
        Execute(crashedConnection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
        Execute(crashedConnection, "INSERT INTO t VALUES (1, 'one');");
        Execute(crashedConnection, "CREATE UNIQUE INDEX t_value_binary ON t(value);");

        using var recovered = EmbeddedDatabase.OpenFile("secondary-index-recovery.db", fileSystem);
        using var connection = recovered.Connect();
        var indexList = Query(connection, "PRAGMA index_list(t);");
        indexList.Select(row => row[1].AsText()).Should().Contain("t_value_binary");
        Query(connection, "SELECT value FROM t WHERE id = 1;").Single()[0].AsText().Should().Be("one");
    }

    [Test]
    public void PersistsAndReopensOverflowIndexRecord()
    {
        var fileSystem = new InMemoryFileSystem();
        var payload = new string('x', 10_000);
        using (var database = EmbeddedDatabase.OpenFile("secondary-index-overflow.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, $"INSERT INTO t VALUES (1, '{payload}');");
            Execute(connection, "CREATE INDEX t_value_binary ON t(value);");
        }

        using (var pager = SqlitePager.Open(
                   fileSystem,
                   "secondary-index-overflow.db",
                   "secondary-index-overflow.db-wal",
                   readOnly: true))
        {
            var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            var schema = SqliteTableLeafPageView.Parse(
                pager.ReadCommittedPage(1),
                header.UsableSpace,
                isFirstPage: true);
            var rootPage = checked((uint)schema.Cells
                .Select(cell => SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding))
                .Single(values => values[1].AsText() == "t_value_binary")[3]
                .AsInteger());

            SqliteIndexLeafPageView.Parse(
                    pager.ReadCommittedPage(rootPage),
                    header.UsableSpace,
                    header.TextEncoding)
                .HasVerifiedRecordOrdering
                .Should()
                .BeFalse();

            var index = SqliteIndexLeafPageView.Parse(
                pager.ReadCommittedPage(rootPage),
                header.UsableSpace,
                header.TextEncoding,
                overflowReader: new SqliteOverflowChainReader(pager, header));
            index.Cells.Should().ContainSingle();
            index.Cells[0].Cell.FirstOverflowPage.Should().NotBeNull();
            index.GetRecord(0).Should().Equal(Record(SqlValue.Text(payload), SqlValue.Integer(1)));
        }

        using var reopened = EmbeddedDatabase.OpenFile("secondary-index-overflow.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        Query(reopenedConnection, "PRAGMA index_list(t);")
            .Select(row => row[1].AsText())
            .Should()
            .Contain("t_value_binary");
    }

    [Test]
    public void PersistsDescendingIndexAndRejectsCorruptCommittedSecondaryIndex()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("secondary-index-corrupt.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, "INSERT INTO t VALUES (1, 'one');");
            Execute(connection, "CREATE INDEX t_value_binary ON t(value);");

            Execute(connection, "CREATE INDEX t_value_desc ON t(value DESC);");
        }

        uint rootPage;
        SqliteDatabaseHeader header;
        using (var pager = SqlitePager.Open(
                   fileSystem,
                   "secondary-index-corrupt.db",
                   "secondary-index-corrupt.db-wal",
                   readOnly: true))
        {
            header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            var schema = SqliteTableLeafPageView.Parse(
                pager.ReadCommittedPage(1),
                header.UsableSpace,
                isFirstPage: true);
            rootPage = checked((uint)schema.Cells
                .Select(cell => SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding))
                .Single(values => values[1].AsText() == "t_value_binary")[3]
                .AsInteger());
        }

        fileSystem.DeleteFile("secondary-index-corrupt.db-wal");
        using (var wal = SqliteWalFile.Create(
                   fileSystem,
                   "secondary-index-corrupt.db-wal",
                   SqliteWalHeader.Create(header.PageSize, salt1: 1, salt2: 2)))
        {
        }
        using (var file = fileSystem.OpenFile("secondary-index-corrupt.db", FileOpenMode.OpenExisting))
        {
            var page = new byte[header.PageSize];
            file.Read((rootPage - 1L) * header.PageSize, page).Should().Be(page.Length);
            page[0] = (byte)SqliteBtreePageType.TableLeaf;
            file.Write((rootPage - 1L) * header.PageSize, page);
            file.FlushToDisk();
        }

        var reopen = () => EmbeddedDatabase.OpenFile("secondary-index-corrupt.db", fileSystem);
        reopen.Should().Throw<EmbeddedSqlException>().WithMessage("*index*");
    }

    private static byte[] Record(params SqlValue[] values) => SqliteRecordCodec.Encode(values);

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

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
        var directory = Path.Combine(AppContext.BaseDirectory, "persistent-secondary-index-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"index-{Guid.NewGuid():N}.db");
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
