using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class EmbeddedCatalogConflictAndIndexCollationTests
{
    [Test]
    public void StaleFileCatalogAutocommitRefreshesAndPreservesTheCommittedRowAfterReopen()
    {
        var path = CreateDatabasePath();
        try
        {
            CreateEntriesTable(path);

            using (var firstDatabase = EmbeddedDatabase.OpenFile(path))
            using (var staleDatabase = EmbeddedDatabase.OpenFile(path))
            using (var first = firstDatabase.Connect())
            using (var stale = staleDatabase.Connect())
            {
                Execute(first, "INSERT INTO entries VALUES ('first');");

                // Native SQLite keeps no persistent autocommit snapshot: once the peer
                // has committed (releasing the write lock), a fresh autocommit write
                // reads the latest committed view and succeeds. The managed engine
                // refreshes the stale catalog at statement start to match. (Verified
                // against Microsoft.Data.Sqlite/e_sqlite3: the write succeeds and both
                // rows persist.) Contrast with the explicit-transaction case below,
                // which keeps its open snapshot and is rejected at commit.
                Execute(stale, "INSERT INTO entries VALUES ('stale');");
            }

            using var reopenedDatabase = EmbeddedDatabase.OpenFile(path);
            using var reopened = reopenedDatabase.Connect();
            QueryText(reopened, "SELECT value FROM entries ORDER BY value;")
                .Should()
                .Equal("first", "stale");
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void StaleFileCatalogTransactionCommitIsRejectedWithoutLosingTheCommittedRowAfterReopen()
    {
        var path = CreateDatabasePath();
        try
        {
            CreateEntriesTable(path);

            using (var firstDatabase = EmbeddedDatabase.OpenFile(path))
            using (var staleDatabase = EmbeddedDatabase.OpenFile(path))
            using (var first = firstDatabase.Connect())
            using (var stale = staleDatabase.Connect())
            {
                Execute(stale, "BEGIN;");
                // The competing write has to land before the stale transaction takes
                // its write lock at its own first write; SQLite refuses an autocommit
                // write while another connection holds a write transaction.
                Execute(first, "INSERT INTO entries VALUES ('first');");
                Execute(stale, "INSERT INTO entries VALUES ('stale');");

                Action staleCommit = () => Execute(stale, "COMMIT;");
                staleCommit.Should().Throw<EmbeddedBusyException>()
                    .WithMessage("database is locked");
                Execute(stale, "ROLLBACK;");
            }

            using var reopenedDatabase = EmbeddedDatabase.OpenFile(path);
            using var reopened = reopenedDatabase.Connect();
            QueryText(reopened, "SELECT value FROM entries ORDER BY value;")
                .Should()
                .Equal("first");
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void OmittedUniqueIndexCollationInheritsNoCaseForDirectExecution()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE entries(value TEXT COLLATE NOCASE);");
        Execute(connection, "CREATE UNIQUE INDEX entries_value ON entries(value);");
        Execute(connection, "INSERT INTO entries VALUES ('a');");

        Action duplicate = () => Execute(connection, "INSERT INTO entries VALUES ('A');");
        duplicate.Should().Throw<EmbeddedSqlException>()
            .WithMessage("UNIQUE constraint failed: entries.value");
        QueryText(connection, "SELECT value FROM entries;").Should().Equal("a");
    }

    [Test]
    public void FileIndexInheritedNoCasePersistsAndEnforcesUniqueSemantics()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE entries(value TEXT COLLATE NOCASE);");
                Execute(connection, "CREATE UNIQUE INDEX entries_value ON entries(value);");
                Execute(connection, "INSERT INTO entries VALUES ('a');");
                Action duplicate = () => Execute(connection, "INSERT INTO entries VALUES ('A');");
                duplicate.Should().Throw<EmbeddedSqlException>()
                    .WithMessage("UNIQUE constraint failed: entries.value");
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path}");
            sqlite.Open();
            using var integrity = sqlite.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check;";
            integrity.ExecuteScalar().Should().Be("ok");

            using var indexes = sqlite.CreateCommand();
            indexes.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'index' AND name = 'entries_value';";
            indexes.ExecuteScalar().Should().Be(1L);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    private static void CreateEntriesTable(string path)
    {
        using var database = EmbeddedDatabase.OpenFile(path);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE entries(value TEXT);");
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private static IReadOnlyList<string> QueryText(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var values = new List<string>();
        while (statement.Step() == StatementStepResult.Row)
            values.Add(statement.GetValue(0).AsText());
        return values;
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "embedded-catalog-conflict-and-index-collation-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():N}.db");
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
