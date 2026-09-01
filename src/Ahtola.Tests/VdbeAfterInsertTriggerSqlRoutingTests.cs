using AwesomeAssertions;
using Ahtola.Core;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

// SQL-routing coverage for the first trigger family lowered through Program. EXPLAIN proves
// that each eligible AFTER INSERT trigger owns a child frame; SQLite differential assertions
// pin the evaluator leaf's established trigger semantics while unsupported shapes fall back.
public sealed class VdbeAfterInsertTriggerSqlRoutingTests
{
    [Test]
    public void SimpleAfterInsertTriggersRouteThroughProgramAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE data(id INTEGER PRIMARY KEY, value INTEGER)",
            "CREATE TABLE audit(kind TEXT, source_id INTEGER, value INTEGER, seen_changes INTEGER, seen_rowid INTEGER)",
            "CREATE TRIGGER data_all AFTER INSERT ON data BEGIN "
                + "INSERT INTO audit VALUES ('all', NEW.id, NEW.value, changes(), last_insert_rowid()); END",
            "CREATE TRIGGER data_positive AFTER INSERT ON data WHEN NEW.value > 0 BEGIN "
                + "INSERT INTO audit VALUES ('positive', NEW.id, NEW.value, changes(), last_insert_rowid()); END",
        ];

        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(managed, sqlite, setup);

        var explain = ReadRows(
            managed,
            "EXPLAIN INSERT INTO data VALUES (1, 10), (2, -20)");
        var nextAddress = explain.Single(row => row[1].AsText() == "Next")[0];
        var programs = explain.Where(row => row[1].AsText() == "Program").ToArray();
        programs.Should().HaveCount(2);
        programs.Should().OnlyContain(row => row[3] == nextAddress);
        programs.Should().OnlyContain(row =>
            row[2] == SqlValue.Integer(0)
            && row[4] == SqlValue.Integer(3));

        Execute(managed, "INSERT INTO data VALUES (1, 10), (2, -20)");
        Execute(sqlite, "INSERT INTO data VALUES (1, 10), (2, -20)");

        AssertQueriesMatch(
            managed,
            sqlite,
            "SELECT kind, source_id, value, seen_changes, seen_rowid FROM audit ORDER BY rowid");
        AssertQueriesMatch(managed, sqlite, "SELECT changes(), total_changes(), last_insert_rowid()");
    }

    [Test]
    public void RaiseIgnoreUsesTheParentProgramTargetAndSkipsLaterAfterTriggers()
    {
        string[] setup =
        [
            "CREATE TABLE data(id INTEGER PRIMARY KEY)",
            "CREATE TABLE trace(value TEXT)",
            "CREATE TRIGGER data_later AFTER INSERT ON data BEGIN "
                + "INSERT INTO trace VALUES ('later-' || NEW.id); END",
            "CREATE TRIGGER data_ignore AFTER INSERT ON data BEGIN "
                + "INSERT INTO trace VALUES ('pre-' || NEW.id); "
                + "SELECT CASE WHEN NEW.id = 2 THEN RAISE(IGNORE) END; "
                + "INSERT INTO trace VALUES ('post-' || NEW.id); END",
        ];

        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(managed, sqlite, setup);

        var explain = ReadRows(managed, "EXPLAIN INSERT INTO data VALUES (1), (2), (3)");
        var nextAddress = explain.Single(row => row[1].AsText() == "Next")[0];
        explain.Where(row => row[1].AsText() == "Program")
            .Should().HaveCount(2)
            .And.OnlyContain(row => row[3] == nextAddress);

        Execute(managed, "INSERT INTO data VALUES (1), (2), (3)");
        Execute(sqlite, "INSERT INTO data VALUES (1), (2), (3)");
        AssertQueriesMatch(
            managed,
            sqlite,
            "SELECT 'data', id FROM data UNION ALL SELECT 'trace', value FROM trace ORDER BY 1, 2");
    }

    [Test]
    public void DefaultConstraintAbortRollsBackProgramTriggerEffectsLikeSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE data(id INTEGER PRIMARY KEY, value INTEGER UNIQUE)",
            "CREATE TABLE audit(value INTEGER)",
            "CREATE TRIGGER data_after AFTER INSERT ON data BEGIN "
                + "INSERT INTO audit VALUES (NEW.value); END",
        ];

        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(managed, sqlite, setup);
        ReadRows(managed, "EXPLAIN INSERT INTO data VALUES (1, 7), (2, 7)")
            .Select(row => row[1].AsText())
            .Should().Contain("Program");

        var managedError = Assert.Throws<EmbeddedSqlException>(
            () => Execute(managed, "INSERT INTO data VALUES (1, 7), (2, 7)"));
        var sqliteError = Assert.Throws<MsData.SqliteException>(
            () => Execute(sqlite, "INSERT INTO data VALUES (1, 7), (2, 7)"));
        sqliteError!.Message.Should().Contain(managedError!.Message);
        AssertQueriesMatch(
            managed,
            sqlite,
            "SELECT 'data', count(*) FROM data UNION ALL SELECT 'audit', count(*) FROM audit ORDER BY 1");
    }

    [Test]
    public void UnsupportedBeforeReturningAndConflictShapesStayEvaluatorOwned()
    {
        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        Execute(managed, "CREATE TABLE data(id INTEGER PRIMARY KEY)");
        Execute(managed, "CREATE TABLE audit(id INTEGER)");
        Execute(
            managed,
            "CREATE TRIGGER data_before BEFORE INSERT ON data BEGIN INSERT INTO audit VALUES (NEW.id); END");
        Execute(
            managed,
            "CREATE TRIGGER data_after AFTER INSERT ON data BEGIN INSERT INTO audit VALUES (NEW.id); END");

        AssertExplainFallsBack(managed, "EXPLAIN INSERT INTO data VALUES (1)");
        AssertExplainFallsBack(managed, "EXPLAIN INSERT INTO data VALUES (1) RETURNING id");
        AssertExplainFallsBack(managed, "EXPLAIN INSERT OR IGNORE INTO data VALUES (1)");

        using var sqlite = OpenSqlite();
        Execute(sqlite, "CREATE TABLE data(id INTEGER PRIMARY KEY)");
        Execute(sqlite, "CREATE TABLE audit(id INTEGER)");
        Execute(
            sqlite,
            "CREATE TRIGGER data_before BEFORE INSERT ON data BEGIN INSERT INTO audit VALUES (NEW.id); END");
        Execute(
            sqlite,
            "CREATE TRIGGER data_after AFTER INSERT ON data BEGIN INSERT INTO audit VALUES (NEW.id); END");
        Execute(managed, "INSERT INTO data VALUES (1)");
        Execute(sqlite, "INSERT INTO data VALUES (1)");
        AssertQueriesMatch(managed, sqlite, "SELECT id FROM audit ORDER BY rowid");
    }

    [Test]
    public void ProgramRoutedRowsAndTriggerWritesRetainUpdateHookOrder()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(id INTEGER PRIMARY KEY)");
        Execute(connection, "CREATE TABLE audit(id INTEGER PRIMARY KEY)");
        Execute(
            connection,
            "CREATE TRIGGER data_after AFTER INSERT ON data BEGIN INSERT INTO audit VALUES (NEW.id); END");
        ReadRows(connection, "EXPLAIN INSERT INTO data VALUES (1)")
            .Select(row => row[1].AsText())
            .Should().Contain("Program");

        var changes = new List<(SqliteChangeOperation Operation, string Table, long RowId)>();
        connection.Hooks.UpdateHook = change => changes.Add((change.Operation, change.Table, change.RowId));
        Execute(connection, "INSERT INTO data VALUES (1)");

        changes.Should().Equal(
            (SqliteChangeOperation.Insert, "data", 1),
            (SqliteChangeOperation.Insert, "audit", 1));
    }

    private static void AssertExplainFallsBack(EmbeddedConnection connection, string sql)
        => Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, sql))!
            .Message.Should().Contain("only supported for statements lowered to the bytecode compiler");

    private static MsData.SqliteConnection OpenSqlite()
    {
        var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private static void ExecuteBoth(
        EmbeddedConnection managed,
        MsData.SqliteConnection sqlite,
        IEnumerable<string> statements)
    {
        foreach (var statement in statements)
        {
            Execute(managed, statement);
            Execute(sqlite, statement);
        }
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

    private static void AssertQueriesMatch(
        EmbeddedConnection managed,
        MsData.SqliteConnection sqlite,
        string sql)
    {
        var managedRows = ReadRows(managed, sql);
        using var command = sqlite.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var sqliteRows = new List<SqlValue[]>();
        while (reader.Read())
        {
            var row = new SqlValue[reader.FieldCount];
            for (var index = 0; index < row.Length; index++)
                row[index] = FromSqlite(reader.GetValue(index));
            sqliteRows.Add(row);
        }

        managedRows.Should().Equal(sqliteRows, (left, right) => left.SequenceEqual(right));
    }

    private static List<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
            rows.Add(Enumerable.Range(0, statement.ColumnCount).Select(statement.GetValue).ToArray());
        return rows;
    }

    private static SqlValue FromSqlite(object value)
        => value switch
        {
            DBNull => SqlValue.Null,
            long integer => SqlValue.Integer(integer),
            double real => SqlValue.Real(real),
            string text => SqlValue.Text(text),
            byte[] blob => SqlValue.Blob(blob),
            _ => throw new InvalidOperationException($"Unsupported SQLite value {value.GetType().Name}."),
        };
}
