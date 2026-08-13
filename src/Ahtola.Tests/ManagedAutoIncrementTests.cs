using System.Globalization;
using AwesomeAssertions;
using Ahtola.Core;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

public sealed class ManagedAutoIncrementTests
{
    private static IEnumerable<TestCaseData> DifferentialCases
    {
        get
        {
            yield return Case(
                "delete-explicit-update",
                [
                    "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, value TEXT)",
                    "INSERT INTO t(value) VALUES ('first')",
                    "INSERT INTO t(id, value) VALUES (10, 'explicit')",
                    "DELETE FROM t WHERE id = 10",
                    "INSERT INTO t(value) VALUES ('after-delete')",
                    "UPDATE t SET id = 100 WHERE value = 'first'",
                    "INSERT INTO t(value) VALUES ('after-update')",
                ],
                "SELECT id, value, (SELECT seq FROM sqlite_sequence WHERE name = 't') AS seq, "
                + "last_insert_rowid() AS lir FROM t ORDER BY id");
            yield return Case(
                "ignore-burns-rowids",
                [
                    "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, value TEXT UNIQUE)",
                    "INSERT INTO t(value) VALUES ('seed')",
                    "INSERT OR IGNORE INTO t(value) VALUES ('seed'), ('kept')",
                ],
                "SELECT id, value, (SELECT seq FROM sqlite_sequence WHERE name = 't') AS seq, "
                + "last_insert_rowid() AS lir FROM t ORDER BY id");
            yield return Case(
                "ignored-only-statement-burns-rowid",
                [
                    "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, value TEXT UNIQUE)",
                    "INSERT INTO t(value) VALUES ('seed')",
                    "INSERT OR IGNORE INTO t(value) VALUES ('seed')",
                ],
                "SELECT id, value, (SELECT seq FROM sqlite_sequence WHERE name = 't') AS seq, "
                + "last_insert_rowid() AS lir FROM t ORDER BY id");
            yield return Case(
                "replace-burns-rowids",
                [
                    "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, value TEXT UNIQUE)",
                    "INSERT INTO t(value) VALUES ('seed')",
                    "INSERT OR REPLACE INTO t(value) VALUES ('other'), ('seed')",
                ],
                "SELECT id, value, (SELECT seq FROM sqlite_sequence WHERE name = 't') AS seq, "
                + "last_insert_rowid() AS lir FROM t ORDER BY id");
            yield return Case(
                "upsert-burns-rowids",
                [
                    "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, value TEXT UNIQUE)",
                    "INSERT INTO t(value) VALUES ('seed')",
                    "INSERT INTO t(value) VALUES ('seed'), ('kept') "
                    + "ON CONFLICT(value) DO UPDATE SET value = excluded.value",
                ],
                "SELECT id, value, (SELECT seq FROM sqlite_sequence WHERE name = 't') AS seq, "
                + "last_insert_rowid() AS lir FROM t ORDER BY id");
            yield return Case(
                "upsert-do-nothing-only-burns-rowid",
                [
                    "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, value TEXT UNIQUE)",
                    "INSERT INTO t(value) VALUES ('seed')",
                    "INSERT INTO t(value) VALUES ('seed') ON CONFLICT(value) DO NOTHING",
                ],
                "SELECT id, value, (SELECT seq FROM sqlite_sequence WHERE name = 't') AS seq, "
                + "last_insert_rowid() AS lir FROM t ORDER BY id");
            yield return Case(
                "manual-sequence-floor-and-repair",
                [
                    "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT)",
                    "INSERT INTO t DEFAULT VALUES",
                    "UPDATE sqlite_sequence SET seq = '100' WHERE name = 't'",
                    "INSERT INTO t DEFAULT VALUES",
                    "DELETE FROM sqlite_sequence WHERE name = 't'",
                    "INSERT INTO t DEFAULT VALUES",
                ],
                "SELECT id, (SELECT seq FROM sqlite_sequence WHERE name = 't') AS seq, "
                + "last_insert_rowid() AS lir FROM t ORDER BY id");
            yield return Case(
                "savepoint-restores-sequence",
                [
                    "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT)",
                    "INSERT INTO t DEFAULT VALUES",
                    "SAVEPOINT s",
                    "INSERT INTO t DEFAULT VALUES",
                    "ROLLBACK TO s",
                    "RELEASE s",
                    "INSERT INTO t DEFAULT VALUES",
                ],
                "SELECT id, (SELECT seq FROM sqlite_sequence WHERE name = 't') AS seq, "
                + "last_insert_rowid() AS lir FROM t ORDER BY id");
            yield return Case(
                "transaction-rollback-restores-sequence",
                [
                    "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT)",
                    "INSERT INTO t DEFAULT VALUES",
                    "BEGIN",
                    "INSERT INTO t DEFAULT VALUES",
                    "ROLLBACK",
                    "INSERT INTO t DEFAULT VALUES",
                ],
                "SELECT id, (SELECT seq FROM sqlite_sequence WHERE name = 't') AS seq, "
                + "last_insert_rowid() AS lir FROM t ORDER BY id");
            yield return Case(
                "table-primary-key-desc",
                [
                    "CREATE TABLE t(id INTEGER, value TEXT, PRIMARY KEY(id DESC AUTOINCREMENT))",
                    "INSERT INTO t(value) VALUES ('first'), ('second')",
                ],
                "SELECT id, rowid AS rid, value, (SELECT seq FROM sqlite_sequence WHERE name = 't') AS seq "
                + "FROM t ORDER BY id");
            yield return Case(
                "rename-updates-sequence",
                [
                    "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT)",
                    "INSERT INTO t DEFAULT VALUES",
                    "ALTER TABLE t RENAME TO renamed",
                    "INSERT INTO renamed DEFAULT VALUES",
                ],
                "SELECT id, (SELECT seq FROM sqlite_sequence WHERE name = 'renamed') AS seq "
                + "FROM renamed ORDER BY id");
            yield return Case(
                "explicit-negative-sequence-zero",
                [
                    "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT)",
                    "INSERT INTO t VALUES (-5)",
                ],
                "SELECT id, seq, typeof(seq) AS seq_type, last_insert_rowid() AS lir "
                + "FROM t JOIN sqlite_sequence ON name = 't'");
            yield return Case(
                "zero-row-insert-creates-sequence-row",
                [
                    "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT)",
                    "CREATE TABLE source(value INTEGER)",
                    "INSERT INTO t(id) SELECT value FROM source",
                ],
                "SELECT name, seq FROM sqlite_sequence");
            yield return Case(
                "sequence-text-uses-integer-prefix",
                [
                    "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT)",
                    "INSERT INTO t DEFAULT VALUES",
                    "DELETE FROM t",
                    "UPDATE sqlite_sequence SET seq = '1e2' WHERE name = 't'",
                    "INSERT INTO t DEFAULT VALUES",
                    "UPDATE sqlite_sequence SET seq = ' 12xyz' WHERE name = 't'",
                    "INSERT INTO t DEFAULT VALUES",
                ],
                "SELECT id, (SELECT seq FROM sqlite_sequence WHERE name = 't') AS seq "
                + "FROM t ORDER BY id");
            yield return Case(
                "sequence-overflowing-exponent-is-integer-prefix",
                [
                    "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT)",
                    "INSERT INTO t DEFAULT VALUES",
                    "DELETE FROM t",
                    "UPDATE sqlite_sequence SET seq = '1e999' WHERE name = 't'",
                    "INSERT INTO t DEFAULT VALUES",
                ],
                "SELECT id, seq FROM t JOIN sqlite_sequence ON name = 't'");
            yield return Case(
                "trigger-restores-captured-sequence-rowid",
                [
                    "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT)",
                    "CREATE TABLE b(id INTEGER PRIMARY KEY AUTOINCREMENT)",
                    "INSERT INTO t DEFAULT VALUES",
                    "INSERT INTO b DEFAULT VALUES",
                    "CREATE TRIGGER tr AFTER INSERT ON t BEGIN "
                    + "DELETE FROM sqlite_sequence WHERE name = 't'; "
                    + "INSERT INTO sqlite_sequence(name, seq) VALUES ('t', 100); END",
                    "INSERT INTO t DEFAULT VALUES",
                    "INSERT INTO t DEFAULT VALUES",
                ],
                "SELECT rowid, name, seq, (SELECT max(id) FROM t) AS max_id "
                + "FROM sqlite_sequence ORDER BY rowid");
        }
    }

    [TestCaseSource(nameof(DifferentialCases))]
    public void SuccessfulSemanticsMatchMicrosoftDataSqlite(DifferentialCase testCase)
    {
        var managed = RunManaged(testCase.Statements, testCase.Query);
        var sqlite = RunSqlite(testCase.Statements, testCase.Query);

        managed.Columns.Should().Equal(sqlite.Columns);
        managed.Rows.Should().Equal(sqlite.Rows);
    }

    [TestCase("ABORT")]
    [TestCase("FAIL")]
    [TestCase("ROLLBACK")]
    public void FailedConflictAlgorithmsMatchSqliteDataSequenceAndLastInsertRowid(string algorithm)
    {
        var begin = algorithm == "ROLLBACK" ? "BEGIN" : null;
        var managed = new EmbeddedDatabase();
        using var managedConnection = managed.Connect();
        Execute(managedConnection, "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, value TEXT UNIQUE)");
        Execute(managedConnection, "INSERT INTO t(value) VALUES ('seed')");
        if (begin is not null)
            Execute(managedConnection, begin);

        var managedError = Assert.Throws<EmbeddedSqlException>(
            () => Execute(
                managedConnection,
                $"INSERT OR {algorithm} INTO t(value) VALUES ('first'), ('seed'), ('last')"));
        var managedRows = QueryManaged(
            managedConnection,
            "SELECT id, value, (SELECT seq FROM sqlite_sequence WHERE name = 't') AS seq, "
            + "last_insert_rowid() AS lir FROM t ORDER BY id");

        using var sqliteConnection = new MsData.SqliteConnection("Data Source=:memory:");
        sqliteConnection.Open();
        Execute(sqliteConnection, "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, value TEXT UNIQUE)");
        Execute(sqliteConnection, "INSERT INTO t(value) VALUES ('seed')");
        if (begin is not null)
            Execute(sqliteConnection, begin);
        var sqliteError = Assert.Throws<MsData.SqliteException>(
            () => Execute(
                sqliteConnection,
                $"INSERT OR {algorithm} INTO t(value) VALUES ('first'), ('seed'), ('last')"));
        var sqliteRows = QuerySqlite(
            sqliteConnection,
            "SELECT id, value, (SELECT seq FROM sqlite_sequence WHERE name = 't') AS seq, "
            + "last_insert_rowid() AS lir FROM t ORDER BY id");

        sqliteError!.Message.Should().Contain(managedError!.Message);
        managedRows.Rows.Should().Equal(sqliteRows.Rows);
    }

    [TestCase(
        "CREATE TABLE t(id INT PRIMARY KEY AUTOINCREMENT)",
        "AUTOINCREMENT is only allowed on an INTEGER PRIMARY KEY")]
    [TestCase(
        "CREATE TABLE t(id INTEGER PRIMARY KEY DESC AUTOINCREMENT)",
        "AUTOINCREMENT is only allowed on an INTEGER PRIMARY KEY")]
    [TestCase(
        "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT) WITHOUT ROWID",
        "AUTOINCREMENT not allowed on WITHOUT ROWID tables")]
    [TestCase(
        "CREATE TABLE t(id INTEGER, other INTEGER, PRIMARY KEY(id AUTOINCREMENT, other))",
        "AUTOINCREMENT is only allowed on an INTEGER PRIMARY KEY")]
    [TestCase(
        "CREATE TABLE t(id INTEGER AUTOINCREMENT PRIMARY KEY)",
        "AUTOINCREMENT")]
    public void InvalidDeclarationsFailBeforeCatalogMutation(string sql, string expectedMessage)
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, sql))!
            .Message.Should().Contain(expectedMessage);
        QueryManaged(
                connection,
                "SELECT count(*) FROM sqlite_schema "
                + "WHERE name IN ('t', 'sqlite_sequence')")
            .Rows.Should().Equal(["I:0"]);
    }

    [Test]
    public void SequenceCatalogLifecycleMatchesSqlite()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, removed TEXT)");

        QueryManaged(
                connection,
                "SELECT name FROM sqlite_schema WHERE type = 'table' ORDER BY name")
            .Rows.Should().Equal(
            [
                "T:__turso_internal_seq___turso_internal_autoincrement_t",
                "T:sqlite_sequence",
                "T:t",
            ]);
        QueryManaged(
                connection,
                "SELECT value, is_called, start, inc, min, max, cycle "
                + "FROM __turso_internal_seq___turso_internal_autoincrement_t")
            .Rows.Should().Equal(["I:1\u001fI:0\u001fI:1\u001fI:1\u001fI:1\u001fI:9223372036854775807\u001fI:0"]);
        Execute(connection, "INSERT INTO t(removed) VALUES ('drop-me')");
        Execute(connection, "ALTER TABLE t DROP COLUMN removed");
        QueryManaged(connection, "SELECT name, seq FROM sqlite_sequence")
            .Rows.Should().Equal(["T:t\u001fI:1"]);
        QueryManaged(
                connection,
                "SELECT value, is_called FROM __turso_internal_seq___turso_internal_autoincrement_t")
            .Rows.Should().Equal(["I:1\u001fI:1"]);
        Execute(connection, "ALTER TABLE t RENAME TO renamed");
        QueryManaged(connection, "SELECT name, seq FROM sqlite_sequence")
            .Rows.Should().Equal(["T:renamed\u001fI:1"]);
        QueryManaged(
                connection,
                "SELECT name FROM sqlite_schema "
                + "WHERE name LIKE '__turso_internal_seq___turso_internal_autoincrement_%'")
            .Rows.Should().Equal(["T:__turso_internal_seq___turso_internal_autoincrement_renamed"]);
        Execute(connection, "DROP TABLE renamed");
        QueryManaged(connection, "SELECT count(*) FROM sqlite_sequence")
            .Rows.Should().Equal(["I:0"]);
        QueryManaged(
                connection,
                "SELECT count(*) FROM sqlite_schema "
                + "WHERE name LIKE '__turso_internal_seq___turso_internal_autoincrement_%'")
            .Rows.Should().Equal(["I:0"]);

        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "DROP TABLE sqlite_sequence"))!
            .Message.Should().Be("table sqlite_sequence may not be dropped");
        Assert.Throws<EmbeddedSqlException>(
                () => Execute(connection, "ALTER TABLE sqlite_sequence ADD COLUMN extra"))!
            .Message.Should().Be("table sqlite_sequence may not be altered");
        Assert.Throws<EmbeddedSqlException>(
                () => Execute(connection, "ALTER TABLE sqlite_sequence DROP COLUMN seq"))!
            .Message.Should().Be("table sqlite_sequence may not be altered");
        Assert.Throws<EmbeddedSqlException>(
                () => Execute(connection, "CREATE INDEX sequence_name ON sqlite_sequence(name)"))!
            .Message.Should().Be("table sqlite_sequence may not be indexed");
    }

    [Test]
    public void SequenceBackingTableNamePreservesTheTableName()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE MiXeD(id INTEGER PRIMARY KEY AUTOINCREMENT)");

        QueryManaged(
                connection,
                "SELECT name FROM sqlite_schema "
                + "WHERE name LIKE '__turso_internal_seq___turso_internal_autoincrement_%'")
            .Rows.Should().Equal(["T:__turso_internal_seq___turso_internal_autoincrement_MiXeD"]);
    }

    [Test]
    public void MaximumSequenceFailsLikeSqliteWithoutChangingState()
    {
        var statements = new[]
        {
            "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT)",
            "INSERT INTO t VALUES (9223372036854775807)",
        };
        var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        foreach (var statement in statements)
            Execute(managed, statement);
        var managedError = Assert.Throws<EmbeddedSqlException>(
            () => Execute(managed, "INSERT INTO t DEFAULT VALUES"));
        var managedRows = QueryManaged(
            managed,
            "SELECT id, seq, last_insert_rowid() AS lir FROM t JOIN sqlite_sequence ON name = 't'");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in statements)
            Execute(sqlite, statement);
        var sqliteError = Assert.Throws<MsData.SqliteException>(
            () => Execute(sqlite, "INSERT INTO t DEFAULT VALUES"));
        var sqliteRows = QuerySqlite(
            sqlite,
            "SELECT id, seq, last_insert_rowid() AS lir FROM t JOIN sqlite_sequence ON name = 't'");

        sqliteError!.Message.Should().Contain(managedError!.Message);
        managedRows.Rows.Should().Equal(sqliteRows.Rows);
    }

    [Test]
    public void NonAutoIncrementMaximumRowidUsesAnAvailableRandomPositiveRowid()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY)");
        Execute(connection, "INSERT INTO t VALUES (9223372036854775807)");
        Execute(connection, "INSERT INTO t DEFAULT VALUES");

        var generated = QueryManaged(connection, "SELECT id FROM t WHERE id <> 9223372036854775807").Rows;

        generated.Should().HaveCount(1);
        var rowId = long.Parse(generated[0]["I:".Length..], CultureInfo.InvariantCulture);
        rowId.Should().BeInRange(1, long.MaxValue - 1);
    }

    [Test]
    public void ExplainInsertDoesNotAllocateOrRequireRuntimeSequenceState()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, value TEXT)");

        QueryManaged(connection, "EXPLAIN INSERT INTO t(value) VALUES ('explained')")
            .Rows.Should().NotBeEmpty();
        QueryManaged(connection, "EXPLAIN QUERY PLAN INSERT INTO t(value) VALUES ('planned')")
            .Rows.Should().NotBeEmpty();
        QueryManaged(connection, "SELECT count(*) FROM t").Rows.Should().Equal(["I:0"]);
        QueryManaged(connection, "SELECT count(*) FROM sqlite_sequence").Rows.Should().Equal(["I:0"]);
    }

    private static TestCaseData Case(string name, string[] statements, string query)
        => new(new DifferentialCase(statements, query)) { TestName = name };

    private static QueryOutput RunManaged(IReadOnlyList<string> statements, string query)
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        foreach (var statement in statements)
            Execute(connection, statement);
        return QueryManaged(connection, query);
    }

    private static QueryOutput RunSqlite(IReadOnlyList<string> statements, string query)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var statement in statements)
            Execute(connection, statement);
        return QuerySqlite(connection, query);
    }

    private static QueryOutput QueryManaged(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var columns = Enumerable.Range(0, statement.GetColumnCount())
            .Select(statement.GetColumnName)
            .ToArray();
        var rows = new List<string>();
        while (statement.Step() == StatementStepResult.Row)
        {
            rows.Add(string.Join(
                '\u001f',
                Enumerable.Range(0, statement.GetColumnCount())
                    .Select(index => Format(statement.GetValue(index)))));
        }
        return new QueryOutput(columns, rows);
    }

    private static QueryOutput QuerySqlite(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(string.Join(
                '\u001f',
                Enumerable.Range(0, reader.FieldCount)
                    .Select(index => Format(reader.IsDBNull(index) ? null : reader.GetValue(index)))));
        }
        return new QueryOutput(columns, rows);
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    private static void Execute(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Format(SqlValue value)
    {
        return value.Kind switch
        {
            SqlValueKind.Null => "N:",
            SqlValueKind.Integer => "I:" + value.AsInteger().ToString(CultureInfo.InvariantCulture),
            SqlValueKind.Real => "R:" + value.AsReal().ToString("R", CultureInfo.InvariantCulture),
            SqlValueKind.Text => "T:" + value.AsText(),
            SqlValueKind.Blob => "B:" + Convert.ToHexString(value.AsBlob().Span),
            _ => throw new InvalidOperationException($"Unknown SQL value kind {value.Kind}."),
        };
    }

    private static string Format(object? value)
    {
        return value switch
        {
            null => "N:",
            long integer => "I:" + integer.ToString(CultureInfo.InvariantCulture),
            double real => "R:" + real.ToString("R", CultureInfo.InvariantCulture),
            string text => "T:" + text,
            byte[] blob => "B:" + Convert.ToHexString(blob),
            _ => throw new InvalidOperationException($"Unknown SQLite value type {value.GetType().Name}."),
        };
    }

    public sealed record DifferentialCase(string[] Statements, string Query);

    private sealed record QueryOutput(string[] Columns, IReadOnlyList<string> Rows);
}
