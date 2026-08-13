using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public class IndexedTableDurableMutationPressureTests
{
    [Test]
    public void RebuildsSupportedIndexesAfterPressureMutationsAndReopen()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE work(id INTEGER PRIMARY KEY, category TEXT, payload TEXT);");
                Execute(connection, BuildInsertRows(1, 180));
                Execute(connection, "CREATE UNIQUE INDEX work_payload ON work(payload);");
                Execute(connection, "CREATE INDEX work_category_payload ON work(category, payload);");

                Execute(connection,
                    "UPDATE work SET id = id + 1000, category = 'hot', payload = 'updated-' || id WHERE id <= 40;");
                var duplicate = () => Execute(connection, "UPDATE work SET payload = 'updated-17' WHERE id = 1040;");
                duplicate.Should().Throw<EmbeddedSqlException>().WithMessage("*UNIQUE constraint failed*");
                Execute(connection, "DELETE FROM work WHERE id >= 81 AND id <= 110;");

                Scalar(connection, "SELECT COUNT(*) FROM work;").Should().Be(150);
                Scalar(connection, "SELECT id FROM work WHERE payload = 'updated-17';").Should().Be(1017);
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                Scalar(connection, "SELECT COUNT(*) FROM work;").Should().Be(150);
                Scalar(connection, "SELECT id FROM work WHERE payload = 'updated-17';").Should().Be(1017);
                Query(connection, "SELECT id FROM work WHERE category = 'hot' ORDER BY id;")
                    .Select(row => row[0].AsInteger())
                    .Should()
                    .Equal(Enumerable.Range(1001, 40).Select(id => (long)id));
                Query(connection, "PRAGMA index_list(work);")
                    .Select(row => row[1].AsText())
                    .Should()
                    .BeEquivalentTo(["work_category_payload", "work_payload"]);
            }

            VerifyWithSqlite(path);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void FailedIndexedDeleteRecoveryRetainsPriorIndexesThenCommitsReplacement()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        using (var database = EmbeddedDatabase.OpenFile("indexed-delete-recovery.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE work(id INTEGER PRIMARY KEY, category TEXT, payload TEXT);");
            Execute(connection, BuildInsertRows(1, 120));
            Execute(connection, "CREATE UNIQUE INDEX work_payload ON work(payload);");
            Execute(connection, "CREATE INDEX work_category_payload ON work(category, payload);");

            faults.FailOnOccurrence(
                FileSystemOperation.Write,
                faults.GetOperationCount(FileSystemOperation.Write) + 3);
            Assert.Throws<IOException>(() => Execute(connection, "DELETE FROM work WHERE id <= 20;"));
        }

        using (var recovered = EmbeddedDatabase.OpenFile("indexed-delete-recovery.db", fileSystem))
        using (var connection = recovered.Connect())
        {
            Scalar(connection, "SELECT COUNT(*) FROM work;").Should().Be(120);
            Scalar(connection, "SELECT id FROM work WHERE id = 17;").Should().Be(17);
            Query(connection, "PRAGMA index_list(work);")
                .Select(row => row[1].AsText())
                .Should()
                .BeEquivalentTo(["work_category_payload", "work_payload"]);

            Execute(connection, "DELETE FROM work WHERE id <= 20;");
        }

        using var reopened = EmbeddedDatabase.OpenFile("indexed-delete-recovery.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        Scalar(reopenedConnection, "SELECT COUNT(*) FROM work;").Should().Be(100);
        Scalar(reopenedConnection, "SELECT COUNT(*) FROM work WHERE id = 17;").Should().Be(0);
        Scalar(reopenedConnection, "SELECT id FROM work WHERE id = 21;").Should().Be(21);
    }

    [Test]
    public void PersistsRichIndexMutationsAndRejectsCorruptRebuiltIndexOnReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        SqliteDatabaseHeader header;
        uint indexRoot;
        using (var database = EmbeddedDatabase.OpenFile("indexed-mutation-corruption.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE work(id INTEGER PRIMARY KEY, category TEXT, payload TEXT);");
            Execute(connection, BuildInsertRows(1, 60));
            Execute(connection, "CREATE INDEX work_category_payload ON work(category, payload);");
            Execute(connection, "UPDATE work SET category = 'reindexed' WHERE id <= 30;");
            Execute(connection, "DELETE FROM work WHERE id > 50;");

            Execute(connection, "CREATE INDEX work_desc ON work(payload DESC);");
            Execute(connection, "CREATE INDEX work_nocase ON work(payload COLLATE NOCASE);");
            Query(connection, "PRAGMA index_list(work);")
                .Select(row => row[1].AsText())
                .Should()
                .BeEquivalentTo(["work_category_payload", "work_desc", "work_nocase"]);
        }

        using (var pager = SqlitePager.Open(
                   fileSystem,
                   "indexed-mutation-corruption.db",
                   "indexed-mutation-corruption.db-wal",
                   readOnly: true))
        {
            header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            indexRoot = ReadIndexRoot(pager, header, "work_category_payload");
        }

        fileSystem.DeleteFile("indexed-mutation-corruption.db-wal");
        using (SqliteWalFile.Create(
                   fileSystem,
                   "indexed-mutation-corruption.db-wal",
                   SqliteWalHeader.Create(header.PageSize, salt1: 17, salt2: 18)))
        {
        }
        using (var store = SqlitePageStore.Open(fileSystem, "indexed-mutation-corruption.db"))
        {
            var indexPage = store.ReadPage(indexRoot);
            indexPage[0] = (byte)SqliteBtreePageType.TableLeaf;
            store.WritePage(indexRoot, indexPage);
            store.Flush();
        }

        var reopen = () => EmbeddedDatabase.OpenFile("indexed-mutation-corruption.db", fileSystem);
        reopen.Should().Throw<EmbeddedSqlException>().WithMessage("*index*");
    }

    private static void VerifyWithSqlite(string path)
    {
        var verificationPath = path + ".verify.db";
        File.Copy(path, verificationPath, overwrite: true);
        try
        {
            using var sqlite = new MsData.SqliteConnection($"Data Source={verificationPath}");
            sqlite.Open();

            using (var integrity = sqlite.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check;";
                integrity.ExecuteScalar().Should().Be("ok");
            }

            using (var uniqueLookup = sqlite.CreateCommand())
            {
                uniqueLookup.CommandText =
                    "SELECT id FROM work INDEXED BY work_payload WHERE payload = 'updated-17';";
                Convert.ToInt64(uniqueLookup.ExecuteScalar()).Should().Be(1017);
            }

            using (var indexedRows = sqlite.CreateCommand())
            {
                indexedRows.CommandText =
                    "SELECT id FROM work INDEXED BY work_category_payload WHERE category = 'hot' ORDER BY id;";
                using var reader = indexedRows.ExecuteReader();
                var ids = new List<long>();
                while (reader.Read())
                    ids.Add(reader.GetInt64(0));

                ids.Should().Equal(Enumerable.Range(1001, 40).Select(id => (long)id));
            }
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(verificationPath);
        }
    }

    private static uint ReadIndexRoot(SqlitePager pager, SqliteDatabaseHeader header, string indexName)
    {
        var schema = SqliteTableLeafPageView.Parse(
            pager.ReadCommittedPage(1),
            header.UsableSpace,
            isFirstPage: true);
        return checked((uint)schema.Cells
            .Select(cell => SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding))
            .Single(values => values[0].AsText() == "index" && values[1].AsText() == indexName)[3]
            .AsInteger());
    }

    private static string BuildInsertRows(int firstId, int count)
    {
        var rows = Enumerable.Range(firstId, count)
            .Select(id => $"({id}, 'category-{id % 7:D2}', 'payload-{id:D5}-{new string('x', 96)}')");
        return $"INSERT INTO work VALUES {string.Join(", ", rows)};";
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private static long Scalar(EmbeddedConnection connection, string sql)
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
        var directory = Path.Combine(AppContext.BaseDirectory, "indexed-table-durable-mutation-pressure-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"indexed-mutations-{Guid.NewGuid():N}.db");
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
