using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// End-to-end coverage for the file-backed managed engine: durable persistence of
/// schema and rows as real SQLite pages, crash/reopen recovery, atomic rejection of
/// unsupported schema/data, and cross-engine readability by a real SQLite library.
/// </summary>
public class ManagedFileStorageTests
{
    [Test]
    public void CreatesInsertsAndReadsBackAfterGracefulReopenOnSharedStore()
    {
        var fileSystem = new InMemoryFileSystem();

        using (var database = EmbeddedDatabase.OpenFile("managed.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE person(id INTEGER PRIMARY KEY, name TEXT, score REAL);");
            Execute(connection, "INSERT INTO person VALUES (1, 'ada', 9.5);");
            Execute(connection, "INSERT INTO person VALUES (2, 'grace', 8.25);");
        }

        // Reopening the same backing store must reconstruct the catalog and rows
        // purely from the persisted SQLite pages.
        using (var reopened = EmbeddedDatabase.OpenFile("managed.db", fileSystem))
        using (var connection = reopened.Connect())
        {
            var rows = Query(connection, "SELECT id, name, score FROM person ORDER BY id;");
            rows.Should().HaveCount(2);
            rows[0][0].AsInteger().Should().Be(1);
            rows[0][1].AsText().Should().Be("ada");
            rows[0][2].AsReal().Should().Be(9.5);
            rows[1][0].AsInteger().Should().Be(2);
            rows[1][1].AsText().Should().Be("grace");
        }
    }

    [Test]
    public void SurvivesUngracefulCrashWithoutCloseOnSharedStore()
    {
        var fileSystem = new InMemoryFileSystem();

        // Open, write, and then abandon the database WITHOUT disposing it, modelling
        // a process that crashed after committing. The bytes persist in the store.
        var crashed = EmbeddedDatabase.OpenFile("crash.db", fileSystem);
        var crashedConnection = crashed.Connect();
        Execute(crashedConnection, "CREATE TABLE t(id INTEGER PRIMARY KEY, note TEXT);");
        Execute(crashedConnection, "INSERT INTO t VALUES (42, 'durable');");
        // Intentionally no Dispose(): simulate an abrupt termination.

        using var recovered = EmbeddedDatabase.OpenFile("crash.db", fileSystem);
        using var connection = recovered.Connect();
        var rows = Query(connection, "SELECT id, note FROM t;");
        rows.Should().ContainSingle();
        rows[0][0].AsInteger().Should().Be(42);
        rows[0][1].AsText().Should().Be("durable");
    }

    [Test]
    public void FailedCommitLeavesPriorPersistentStateIntact()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);

        var database = EmbeddedDatabase.OpenFile("atomic.db", fileSystem);
        var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, note TEXT);");
        Execute(connection, "INSERT INTO t VALUES (1, 'first');");

        // Inject a write fault so the next durable commit fails part-way.
        faults.FailNext(FileSystemOperation.Write);
        var act = () => Execute(connection, "INSERT INTO t VALUES (2, 'second');");
        act.Should().Throw<IOException>();

        // The faulted database is unusable, but a fresh reopen must recover exactly
        // the last successfully committed state: row 1 present, row 2 absent.
        using var recovered = EmbeddedDatabase.OpenFile("atomic.db", fileSystem);
        using var recoveredConnection = recovered.Connect();
        var rows = Query(recoveredConnection, "SELECT id, note FROM t ORDER BY id;");
        rows.Should().ContainSingle();
        rows[0][0].AsInteger().Should().Be(1);
        rows[0][1].AsText().Should().Be("first");
    }

    [Test]
    public void PersistsAscendingAndDescendingSecondaryIndexesAcrossReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("secondary-index.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT);");
            Execute(connection, "INSERT INTO t VALUES (1, 'z');");
            Execute(connection, "CREATE INDEX idx_name ON t(name);");
            Execute(connection, "INSERT INTO t VALUES (2, 'a');");

            Query(connection, "SELECT name FROM t ORDER BY name;")
                .Select(row => row[0].AsText())
                .Should().Equal("a", "z");

            Execute(connection, "CREATE INDEX idx_name_desc ON t(name DESC);");
            Query(connection, "PRAGMA index_list(t);")
                .Select(row => row[1].AsText())
                .Should().BeEquivalentTo(["idx_name", "idx_name_desc"]);
            Query(connection, "SELECT COUNT(*) FROM t;")[0][0].AsInteger().Should().Be(2);
        }

        using (var reopened = EmbeddedDatabase.OpenFile("secondary-index.db", fileSystem))
        using (var connection = reopened.Connect())
        {
            Query(connection, "SELECT name FROM t ORDER BY name;")
                .Select(row => row[0].AsText())
                .Should().Equal("a", "z");
            Query(connection, "PRAGMA index_list(t);")
                .Select(row => row[1].AsText())
                .Should().BeEquivalentTo(["idx_name", "idx_name_desc"]);
            Execute(connection, "INSERT INTO t VALUES (3, 'm');");
        }

        using var final = EmbeddedDatabase.OpenFile("secondary-index.db", fileSystem);
        using var finalConnection = final.Connect();
        Query(finalConnection, "SELECT name FROM t ORDER BY name;")
            .Select(row => row[0].AsText())
            .Should().Equal("a", "m", "z");
    }

    [Test]
    public void PersistsUniqueColumnAndEnforcesItAfterReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("unique-column.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER, code TEXT UNIQUE);");
            Execute(connection, "INSERT INTO t VALUES (1, 'one');");
        }

        using var reopened = EmbeddedDatabase.OpenFile("unique-column.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        var duplicate = () => Execute(reopenedConnection, "INSERT INTO t VALUES (2, 'one');");
        duplicate.Should().Throw<EmbeddedSqlException>()
            .WithMessage("UNIQUE constraint failed: t.code");
        Query(reopenedConnection, "SELECT id, code FROM t;").Should().HaveCount(1);
    }

    [Test]
    public void PersistsNonIntegerPrimaryKeyThroughImplicitIndex()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("persist-pk.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(name TEXT PRIMARY KEY);");
            Execute(connection, "INSERT INTO t VALUES ('alpha'), ('beta');");

            var duplicate = () => Execute(connection, "INSERT INTO t VALUES ('alpha');");
            duplicate.Should().Throw<EmbeddedSqlException>().WithMessage("*UNIQUE constraint failed*");
        }

        using var reopened = EmbeddedDatabase.OpenFile("persist-pk.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        Query(reopenedConnection, "SELECT name FROM t ORDER BY name;")
            .Select(row => row[0].AsText())
            .Should().Equal("alpha", "beta");
        var duplicateAfterReopen = () => Execute(reopenedConnection, "INSERT INTO t VALUES ('beta');");
        duplicateAfterReopen.Should().Throw<EmbeddedSqlException>().WithMessage("*UNIQUE constraint failed*");
    }

    [Test]
    public void PersistsOverflowRowsAcrossReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        var payload = new string('a', 10_000);

        using (var database = EmbeddedDatabase.OpenFile("overflow.db", fileSystem))
        using (var initialConnection = database.Connect())
        {
            Execute(initialConnection, "CREATE TABLE t(id INTEGER PRIMARY KEY, payload TEXT);");
            Execute(initialConnection, $"INSERT INTO t VALUES (1, '{payload}');");
        }

        using var reopened = EmbeddedDatabase.OpenFile("overflow.db", fileSystem);
        using var connection = reopened.Connect();
        Query(connection, "SELECT payload FROM t WHERE id = 1;")[0][0].AsText().Should().Be(payload);
    }

    [Test]
    public void FailedOverflowCommitLeavesPriorPersistentStateIntact()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        var payload = new string('a', 10_000);

        var database = EmbeddedDatabase.OpenFile("overflow-atomic.db", fileSystem);
        var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, payload TEXT);");
        Execute(connection, "INSERT INTO t VALUES (1, 'first');");

        // Let the WAL receive frames for the rewritten schema and root page, then
        // fail the first overflow frame before a commit marker can be written.
        faults.FailOnOccurrence(
            FileSystemOperation.Write,
            faults.GetOperationCount(FileSystemOperation.Write) + 3);
        var act = () => Execute(connection, $"INSERT INTO t VALUES (2, '{payload}');");
        act.Should().Throw<IOException>();

        using var recovered = EmbeddedDatabase.OpenFile("overflow-atomic.db", fileSystem);
        using var recoveredConnection = recovered.Connect();
        var rows = Query(recoveredConnection, "SELECT id, payload FROM t ORDER BY id;");
        rows.Should().ContainSingle();
        rows[0][0].AsInteger().Should().Be(1);
        rows[0][1].AsText().Should().Be("first");
    }

    [Test]
    public void PersistsViewsAndReloadsThemFromSchema()
    {
        var fileSystem = new InMemoryFileSystem();

        using (var database = EmbeddedDatabase.OpenFile("views.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT);");
            Execute(connection, "INSERT INTO t VALUES (1, 'alpha');");
            Execute(connection, "INSERT INTO t VALUES (2, 'beta');");
            Execute(connection, "CREATE VIEW v AS SELECT name FROM t WHERE id = 2;");
        }

        using (var reopened = EmbeddedDatabase.OpenFile("views.db", fileSystem))
        using (var connection = reopened.Connect())
        {
            Query(connection, "SELECT name FROM v;")[0][0].AsText().Should().Be("beta");
        }
    }

    [Test]
    public void StoresRowsAsGenuineSqlitePagesReadableByTheStandaloneParser()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("format.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT);");
            Execute(connection, "INSERT INTO t VALUES (1, 'one');");
            Execute(connection, "INSERT INTO t VALUES (2, 'two');");
        }

        // Re-parse the raw bytes with the independent SQLite storage primitives to
        // prove the file is genuine SQLite format, not a bespoke serialization.
        using var pager = SqlitePager.Open(fileSystem, "format.db", "format.db-wal", readOnly: false);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        header.PageSize.Should().Be(4096);
        var usableSpace = header.UsableSpace;

        var schema = SqliteTableLeafPageView.Parse(pager.ReadCommittedPage(1), usableSpace, isFirstPage: true);
        var schemaRow = schema.Cells
            .Select(cell => SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding))
            .Single(values => values[1].AsText() == "t");
        schemaRow[0].AsText().Should().Be("table");
        var rootPage = (uint)schemaRow[3].AsInteger();
        rootPage.Should().BeGreaterThanOrEqualTo(2);

        var table = SqliteTableLeafPageView.Parse(pager.ReadCommittedPage(rootPage), usableSpace, isFirstPage: false);
        table.Cells.Should().HaveCount(2);

        // The INTEGER PRIMARY KEY is a rowid alias: SQLite stores it as the cell
        // rowid with NULL in the record, exactly as we persist it.
        table.Cells[0].Cell.RowId.Should().Be(1);
        var firstRecord = SqliteRecordCodec.Decode(table.Cells[0].Cell.LocalPayload.Span, header.TextEncoding);
        firstRecord[0].Kind.Should().Be(SqlValueKind.Null);
        firstRecord[1].AsText().Should().Be("one");
    }

    [Test]
    public void ProducesAFileReadableByARealSqliteLibrary()
    {
        var path = CreatePhysicalDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT);");
                Execute(connection, "INSERT INTO t VALUES (1, 'ada');");
                Execute(connection, "INSERT INTO t VALUES (2, 'grace');");
            }

            // Because every commit is checkpointed into the main database file, the
            // .db alone is a self-contained, valid SQLite database. Copy just that
            // file and open it with the real SQLite engine to prove it.
            var verifyPath = path + ".verify.db";
            File.Copy(path, verifyPath, overwrite: true);
            try
            {
                using var real = new MsData.SqliteConnection($"Data Source={verifyPath}");
                real.Open();

                using var integrity = real.CreateCommand();
                integrity.CommandText = "PRAGMA integrity_check;";
                integrity.ExecuteScalar().Should().Be("ok");

                using var query = real.CreateCommand();
                query.CommandText = "SELECT name FROM t WHERE id = 2;";
                query.ExecuteScalar().Should().Be("grace");

                using var count = real.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM t;";
                Convert.ToInt64(count.ExecuteScalar()).Should().Be(2);
            }
            finally
            {
                MsData.SqliteConnection.ClearAllPools();
                DeletePhysicalDatabase(verifyPath);
            }
        }
        finally
        {
            DeletePhysicalDatabase(path);
        }
    }

    [Test]
    public void ProducesOverflowFileReadableByARealSqliteLibrary()
    {
        var path = CreatePhysicalDatabasePath();
        var payload = new string('z', 10_000);
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, payload TEXT);");
                Execute(connection, $"INSERT INTO t VALUES (1, '{payload}');");
            }

            var verifyPath = path + ".verify.db";
            File.Copy(path, verifyPath, overwrite: true);
            try
            {
                using var real = new MsData.SqliteConnection($"Data Source={verifyPath}");
                real.Open();

                using var integrity = real.CreateCommand();
                integrity.CommandText = "PRAGMA integrity_check;";
                integrity.ExecuteScalar().Should().Be("ok");

                using var query = real.CreateCommand();
                query.CommandText = "SELECT length(payload), payload FROM t WHERE id = 1;";
                using var reader = query.ExecuteReader();
                reader.Read().Should().BeTrue();
                reader.GetInt32(0).Should().Be(payload.Length);
                reader.GetString(1).Should().Be(payload);
            }
            finally
            {
                MsData.SqliteConnection.ClearAllPools();
                DeletePhysicalDatabase(verifyPath);
            }
        }
        finally
        {
            DeletePhysicalDatabase(path);
        }
    }

    [Test]
    public void ReclaimsObsoleteOverflowPagesAsASqliteFreelist()
    {
        var path = CreatePhysicalDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, payload TEXT);");
                Execute(connection, $"INSERT INTO t VALUES (1, '{new string('z', 10_000)}');");
                Execute(connection, "UPDATE t SET payload = 'small' WHERE id = 1;");
            }

            var verifyPath = path + ".verify.db";
            File.Copy(path, verifyPath, overwrite: true);
            try
            {
                using var real = new MsData.SqliteConnection($"Data Source={verifyPath}");
                real.Open();

                using var integrity = real.CreateCommand();
                integrity.CommandText = "PRAGMA integrity_check;";
                integrity.ExecuteScalar().Should().Be("ok");

                using var freelist = real.CreateCommand();
                freelist.CommandText = "PRAGMA freelist_count;";
                Convert.ToInt64(freelist.ExecuteScalar()).Should().BeGreaterThan(0);

                using var query = real.CreateCommand();
                query.CommandText = "SELECT payload FROM t WHERE id = 1;";
                query.ExecuteScalar().Should().Be("small");
            }
            finally
            {
                MsData.SqliteConnection.ClearAllPools();
                DeletePhysicalDatabase(verifyPath);
            }
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeletePhysicalDatabase(path);
        }
    }

    [Test]
    public void PersistsAcrossReopenOnThePhysicalFileSystem()
    {
        var path = CreatePhysicalDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT);");
                Execute(connection, "INSERT INTO t VALUES (7, 'seven');");
            }

            File.Exists(path).Should().BeTrue();

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                Query(connection, "SELECT name FROM t WHERE id = 7;")[0][0].AsText().Should().Be("seven");
            }
        }
        finally
        {
            DeletePhysicalDatabase(path);
        }
    }

    [Test]
    public void PreservesHiddenRowidsAcrossReopen()
    {
        var fileSystem = new InMemoryFileSystem();

        using (var database = EmbeddedDatabase.OpenFile("hidden-rowid.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(a TEXT);");
            Execute(connection, "INSERT INTO t(a) VALUES ('a');");
            Execute(connection, "INSERT INTO t(a) VALUES ('b');");
            Execute(connection, "INSERT INTO t(a) VALUES ('c');");
            // Delete the top row so the surviving rowids are non-contiguous (1, 2); a naive
            // re-sequencing on persist would renumber them and lose their identity.
            Execute(connection, "DELETE FROM t WHERE rowid = 3;");
            Execute(connection, "INSERT INTO t(rowid, a) VALUES (100, 'd');");
        }

        using (var reopened = EmbeddedDatabase.OpenFile("hidden-rowid.db", fileSystem))
        using (var connection = reopened.Connect())
        {
            var rows = Query(connection, "SELECT rowid, a FROM t ORDER BY rowid;");
            rows.Should().HaveCount(3);
            rows[0][0].AsInteger().Should().Be(1);
            rows[0][1].AsText().Should().Be("a");
            rows[1][0].AsInteger().Should().Be(2);
            rows[1][1].AsText().Should().Be("b");
            rows[2][0].AsInteger().Should().Be(100);
            rows[2][1].AsText().Should().Be("d");

            // The high-water mark is rebuilt from the persisted rowids, so the next
            // autogenerated rowid follows the largest surviving value.
            Execute(connection, "INSERT INTO t(a) VALUES ('e');");
            Query(connection, "SELECT rowid FROM t WHERE a = 'e';")[0][0].AsInteger().Should().Be(101);
        }
    }

    [Test]
    public void PreservesAliasRowidsAcrossReopen()
    {
        var fileSystem = new InMemoryFileSystem();

        using (var database = EmbeddedDatabase.OpenFile("alias-rowid.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, a TEXT);");
            Execute(connection, "INSERT INTO t(id, a) VALUES (5, 'five');");
            Execute(connection, "INSERT INTO t(a) VALUES ('auto');");
        }

        using (var reopened = EmbeddedDatabase.OpenFile("alias-rowid.db", fileSystem))
        using (var connection = reopened.Connect())
        {
            var rows = Query(connection, "SELECT id, rowid, a FROM t ORDER BY id;");
            rows.Should().HaveCount(2);
            rows[0][0].AsInteger().Should().Be(5);
            rows[0][1].AsInteger().Should().Be(5);
            rows[0][2].AsText().Should().Be("five");
            rows[1][0].AsInteger().Should().Be(6);
            rows[1][1].AsInteger().Should().Be(6);
            rows[1][2].AsText().Should().Be("auto");
        }
    }

    [Test]
    public void PersistsIntegerPrimaryKeyDescendingThroughImplicitIndex()
    {
        var fileSystem = new InMemoryFileSystem();

        // INTEGER PRIMARY KEY DESC is not a rowid alias in SQLite; it is backed by a
        // separate sqlite_autoindex unique index, which the file engine now persists.
        using (var database = EmbeddedDatabase.OpenFile("persist-desc-pk.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY DESC, a TEXT);");
            Execute(connection, "INSERT INTO t VALUES (3, 'c'), (1, 'a'), (2, 'b');");

            var duplicate = () => Execute(connection, "INSERT INTO t VALUES (2, 'dupe');");
            duplicate.Should().Throw<EmbeddedSqlException>().WithMessage("*UNIQUE constraint failed*");
        }

        using var reopened = EmbeddedDatabase.OpenFile("persist-desc-pk.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        Query(reopenedConnection, "SELECT a FROM t ORDER BY id DESC;")
            .Select(row => row[0].AsText())
            .Should().Equal("c", "b", "a");
    }

    [Test]
    public void ReopenAfterMainFileDeletionDiscardsOrphanedWalAndCreatesFresh()
    {
        var path = CreatePhysicalDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT);");
                Execute(connection, "INSERT INTO t VALUES (1, 'ada');");

                // Detach without disposing the pager so the -wal is not checkpointed
                // away, then delete only the main database file — exactly what EFCore's
                // EnsureDeleted does (File.Delete on the DataSource path).
                ((IDisposable)database).Dispose();
            }

            File.Delete(path);
            File.Exists(path).Should().BeFalse();
            File.Exists(path + "-wal").Should().BeTrue("the orphaned WAL outlives the main file");

            // Native SQLite creates a fresh database when the main file is missing;
            // the managed engine must not fault with "missing its main database file".
            using var reopened = EmbeddedDatabase.OpenFile(path);
            using var reopenedConnection = reopened.Connect();
            Query(reopenedConnection, "SELECT name FROM sqlite_schema WHERE type = 'table';")
                .Select(row => row[0].AsText())
                .Should().NotContain("t", "the orphaned WAL frames are discarded with the deleted database");
            Execute(reopenedConnection, "CREATE TABLE fresh(id INTEGER PRIMARY KEY);");
            Execute(reopenedConnection, "INSERT INTO fresh VALUES (1);");
            Query(reopenedConnection, "SELECT id FROM fresh;")[0][0].AsInteger().Should().Be(1);
        }
        finally
        {
            DeletePhysicalDatabase(path);
        }
    }

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

    private static string CreatePhysicalDatabasePath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "managed-file-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"managed-{Guid.NewGuid():N}.db");
    }

    private static void DeletePhysicalDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }
}
