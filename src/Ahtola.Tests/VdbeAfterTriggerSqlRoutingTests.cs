using AwesomeAssertions;
using Ahtola.Core;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

// SQL-routing coverage for the first trigger family lowered through Program. EXPLAIN proves
// that each eligible AFTER INSERT trigger owns a child frame; SQLite differential assertions
// pin the evaluator leaf's established trigger semantics while unsupported shapes fall back.
public sealed class VdbeAfterTriggerSqlRoutingTests
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
    public void SimpleBeforeInsertTriggersRouteThroughProgramAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE data(id INTEGER PRIMARY KEY, value INTEGER)",
            "CREATE TABLE audit(kind TEXT, source_id INTEGER, value INTEGER)",
            "CREATE TRIGGER data_before BEFORE INSERT ON data BEGIN "
                + "INSERT INTO audit VALUES ('before', NEW.id, NEW.value); END",
            "CREATE TRIGGER data_after AFTER INSERT ON data BEGIN "
                + "INSERT INTO audit VALUES ('after', NEW.id, NEW.value); END",
        ];

        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(managed, sqlite, setup);

        var opcodes = ReadRows(managed, "EXPLAIN INSERT INTO data VALUES (1, 10)")
            .Select(row => row[1].AsText())
            .ToList();
        opcodes.Count(static opcode => opcode == "Program").Should().Be(2);
        opcodes.Should().Contain("ColumnRange").And.Contain("ResetOnce");

        Execute(managed, "INSERT INTO data VALUES (1, 10), (2, 20)");
        Execute(sqlite, "INSERT INTO data VALUES (1, 10), (2, 20)");
        AssertQueriesMatch(
            managed,
            sqlite,
            "SELECT kind, source_id, value FROM audit ORDER BY rowid");
    }

    [Test]
    public void BeforeInsertRaiseIgnoreSkipsTheInsertAndLaterTriggers()
    {
        string[] setup =
        [
            "CREATE TABLE data(id INTEGER PRIMARY KEY)",
            "CREATE TABLE audit(value TEXT)",
            "CREATE TRIGGER data_before BEFORE INSERT ON data BEGIN "
                + "INSERT INTO audit VALUES ('before-' || NEW.id); "
                + "SELECT CASE WHEN NEW.id = 2 THEN RAISE(IGNORE) END; END",
            "CREATE TRIGGER data_after AFTER INSERT ON data BEGIN "
                + "INSERT INTO audit VALUES ('after-' || NEW.id); END",
        ];

        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(managed, sqlite, setup);
        Execute(managed, "INSERT INTO data VALUES (1), (2), (3)");
        Execute(sqlite, "INSERT INTO data VALUES (1), (2), (3)");
        AssertQueriesMatch(
            managed,
            sqlite,
            "SELECT 'data', id FROM data UNION ALL SELECT 'audit', value FROM audit ORDER BY 1, 2");
    }

    [Test]
    public void UnsupportedReturningAndConflictShapesStayEvaluatorOwned()
    {
        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        Execute(managed, "CREATE TABLE data(id INTEGER PRIMARY KEY)");
        Execute(managed, "CREATE TABLE audit(id INTEGER)");
        Execute(
            managed,
            "CREATE TRIGGER data_after AFTER INSERT ON data BEGIN INSERT INTO audit VALUES (NEW.id); END");

        AssertExplainFallsBack(managed, "EXPLAIN INSERT INTO data VALUES (1) RETURNING id");
        AssertExplainFallsBack(managed, "EXPLAIN INSERT OR IGNORE INTO data VALUES (1)");
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

    [Test]
    public void UpdateAndDeleteProgramRoutesRetainUpdateHookOrder()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE updated(value INTEGER)");
        Execute(connection, "CREATE TABLE deleted(id INTEGER PRIMARY KEY)");
        Execute(connection, "CREATE TABLE audit(value INTEGER)");
        Execute(connection, "INSERT INTO updated VALUES (1)");
        Execute(connection, "INSERT INTO deleted VALUES (1)");
        Execute(
            connection,
            "CREATE TRIGGER updated_after AFTER UPDATE ON updated BEGIN "
                + "INSERT INTO audit VALUES (NEW.value); END");
        Execute(
            connection,
            "CREATE TRIGGER deleted_after AFTER DELETE ON deleted BEGIN "
                + "INSERT INTO audit VALUES (OLD.id); END");

        var changes = new List<(SqliteChangeOperation Operation, string Table, long RowId)>();
        connection.Hooks.UpdateHook = change => changes.Add((change.Operation, change.Table, change.RowId));
        Execute(connection, "UPDATE updated SET value = 2");
        Execute(connection, "DELETE FROM deleted");

        changes.Should().Equal(
            (SqliteChangeOperation.Update, "updated", 1),
            (SqliteChangeOperation.Insert, "audit", 1),
            (SqliteChangeOperation.Delete, "deleted", 1),
            (SqliteChangeOperation.Insert, "audit", 2));
    }

    [Test]
    public void SimpleBeforeUpdateTriggersRouteOldAndNewThroughProgramAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE data(value INTEGER)",
            "CREATE TABLE audit(kind TEXT, old_value INTEGER, new_value INTEGER)",
            "INSERT INTO data VALUES (1), (2)",
            "CREATE TRIGGER data_before BEFORE UPDATE ON data BEGIN "
                + "INSERT INTO audit VALUES ('before', OLD.value, NEW.value); END",
            "CREATE TRIGGER data_after AFTER UPDATE ON data BEGIN "
                + "INSERT INTO audit VALUES ('after', OLD.value, NEW.value); END",
        ];

        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(managed, sqlite, setup);

        var opcodes = ReadRows(managed, "EXPLAIN UPDATE data SET value = value + 10")
            .Select(row => row[1].AsText())
            .ToList();
        opcodes.Count(static opcode => opcode == "Program").Should().Be(2);
        opcodes.Should().Contain("ColumnRange").And.Contain("ResetOnce");

        Execute(managed, "UPDATE data SET value = value + 10");
        Execute(sqlite, "UPDATE data SET value = value + 10");
        AssertQueriesMatch(managed, sqlite, "SELECT value FROM data ORDER BY rowid");
        AssertQueriesMatch(
            managed,
            sqlite,
            "SELECT kind, old_value, new_value FROM audit ORDER BY rowid");
    }

    [Test]
    public void BeforeUpdateRaiseIgnoreSkipsTheUpdateAndLaterTriggers()
    {
        string[] setup =
        [
            "CREATE TABLE data(value INTEGER)",
            "CREATE TABLE audit(value TEXT)",
            "INSERT INTO data VALUES (1), (2), (3)",
            "CREATE TRIGGER data_before BEFORE UPDATE ON data BEGIN "
                + "INSERT INTO audit VALUES ('before-' || NEW.value); "
                + "SELECT CASE WHEN NEW.value = 12 THEN RAISE(IGNORE) END; END",
            "CREATE TRIGGER data_after AFTER UPDATE ON data BEGIN "
                + "INSERT INTO audit VALUES ('after-' || NEW.value); END",
        ];

        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(managed, sqlite, setup);
        Execute(managed, "UPDATE data SET value = value + 10");
        Execute(sqlite, "UPDATE data SET value = value + 10");
        AssertQueriesMatch(managed, sqlite, "SELECT value FROM data ORDER BY rowid");
        AssertQueriesMatch(managed, sqlite, "SELECT value FROM audit ORDER BY rowid");
    }

    [Test]
    public void StrictInsertEmitsTypeCheck()
    {
        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        Execute(managed, "CREATE TABLE data(id INTEGER PRIMARY KEY, value INTEGER) STRICT");
        ReadRows(managed, "EXPLAIN INSERT INTO data VALUES (1, 10)")
            .Select(row => row[1].AsText())
            .Should().Contain("TypeCheck");
    }

    [Test]
    public void SimpleAfterUpdateTriggersRouteOldAndNewThroughProgramAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE data(value INTEGER, note TEXT)",
            "CREATE TABLE audit(kind TEXT, old_rowid INTEGER, new_rowid INTEGER, old_value INTEGER, new_value INTEGER)",
            "INSERT INTO data VALUES (1, 'one'), (2, 'two'), (3, 'three')",
            "CREATE TRIGGER data_all AFTER UPDATE ON data BEGIN "
                + "INSERT INTO audit VALUES ('all', OLD.rowid, NEW.rowid, OLD.value, NEW.value); END",
            "CREATE TRIGGER data_large AFTER UPDATE OF value ON data WHEN NEW.value >= 12 BEGIN "
                + "INSERT INTO audit VALUES ('large', OLD.rowid, NEW.rowid, OLD.value, NEW.value); END",
        ];

        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(managed, sqlite, setup);

        var explain = ReadRows(managed, "EXPLAIN UPDATE data SET value = value + 10 WHERE value >= 2");
        AssertProgramTargets(explain, expectedCount: 2, expectedParameterCount: 6);

        Execute(managed, "UPDATE data SET value = value + 10 WHERE value >= 2");
        Execute(sqlite, "UPDATE data SET value = value + 10 WHERE value >= 2");
        AssertQueriesMatch(managed, sqlite, "SELECT rowid, value, note FROM data ORDER BY rowid");
        AssertQueriesMatch(
            managed,
            sqlite,
            "SELECT kind, old_rowid, new_rowid, old_value, new_value FROM audit ORDER BY rowid");
        AssertQueriesMatch(managed, sqlite, "SELECT changes(), total_changes(), last_insert_rowid()");
    }

    [Test]
    public void SimpleAfterDeleteTriggersRouteOldThroughProgramAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE data(id INTEGER PRIMARY KEY, value INTEGER)",
            "CREATE TABLE audit(kind TEXT, old_id INTEGER, old_value INTEGER)",
            "INSERT INTO data VALUES (1, 10), (2, 20), (3, 30)",
            "CREATE TRIGGER data_all AFTER DELETE ON data BEGIN "
                + "INSERT INTO audit VALUES ('all', OLD.id, OLD.value); END",
            "CREATE TRIGGER data_large AFTER DELETE ON data WHEN OLD.value >= 30 BEGIN "
                + "INSERT INTO audit VALUES ('large', OLD.id, OLD.value); END",
        ];

        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(managed, sqlite, setup);

        var explain = ReadRows(managed, "EXPLAIN DELETE FROM data WHERE value >= 20");
        AssertProgramTargets(explain, expectedCount: 2, expectedParameterCount: 3);

        Execute(managed, "DELETE FROM data WHERE value >= 20");
        Execute(sqlite, "DELETE FROM data WHERE value >= 20");
        AssertQueriesMatch(managed, sqlite, "SELECT id, value FROM data ORDER BY id");
        AssertQueriesMatch(managed, sqlite, "SELECT kind, old_id, old_value FROM audit ORDER BY rowid");
        AssertQueriesMatch(managed, sqlite, "SELECT changes(), total_changes(), last_insert_rowid()");
    }

    [Test]
    public void UpdateAndDeleteRaiseIgnoreJumpPastRemainingAfterPrograms()
    {
        string[] setup =
        [
            "CREATE TABLE updated(value INTEGER)",
            "CREATE TABLE deleted(id INTEGER PRIMARY KEY)",
            "CREATE TABLE trace(value TEXT)",
            "INSERT INTO updated VALUES (1), (2), (3)",
            "INSERT INTO deleted VALUES (1), (2), (3)",
            "CREATE TRIGGER update_later AFTER UPDATE ON updated BEGIN "
                + "INSERT INTO trace VALUES ('update-later-' || NEW.value); END",
            "CREATE TRIGGER update_ignore AFTER UPDATE ON updated BEGIN "
                + "INSERT INTO trace VALUES ('update-pre-' || NEW.value); "
                + "SELECT CASE WHEN NEW.value = 12 THEN RAISE(IGNORE) END; END",
            "CREATE TRIGGER delete_later AFTER DELETE ON deleted BEGIN "
                + "INSERT INTO trace VALUES ('delete-later-' || OLD.id); END",
            "CREATE TRIGGER delete_ignore AFTER DELETE ON deleted BEGIN "
                + "INSERT INTO trace VALUES ('delete-pre-' || OLD.id); "
                + "SELECT CASE WHEN OLD.id = 2 THEN RAISE(IGNORE) END; END",
        ];

        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(managed, sqlite, setup);
        AssertProgramTargets(
            ReadRows(managed, "EXPLAIN UPDATE updated SET value = value + 10"),
            expectedCount: 2,
            expectedParameterCount: 4);
        AssertProgramTargets(
            ReadRows(managed, "EXPLAIN DELETE FROM deleted"),
            expectedCount: 2,
            expectedParameterCount: 2);

        Execute(managed, "UPDATE updated SET value = value + 10");
        Execute(sqlite, "UPDATE updated SET value = value + 10");
        Execute(managed, "DELETE FROM deleted");
        Execute(sqlite, "DELETE FROM deleted");
        AssertQueriesMatch(managed, sqlite, "SELECT value FROM trace ORDER BY rowid");
        AssertQueriesMatch(managed, sqlite, "SELECT value FROM updated ORDER BY rowid");
        AssertQueriesMatch(managed, sqlite, "SELECT count(*) FROM deleted");
    }

    [Test]
    public void UpdateCheckAbortAndDeleteTriggerAbortRollBackProgramEffectsLikeSqlite()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "CREATE TABLE data(value INTEGER CHECK(value < 15))",
                "CREATE TABLE audit(value INTEGER)",
                "INSERT INTO data VALUES (1), (10)",
                "CREATE TRIGGER data_after AFTER UPDATE ON data BEGIN "
                    + "INSERT INTO audit VALUES (NEW.value); END",
            ],
            "UPDATE data SET value = value + 10",
            "SELECT 'data', value FROM data UNION ALL SELECT 'audit', value FROM audit ORDER BY 1, 2");

        AssertErrorAndStateMatchesSqlite(
            [
                "CREATE TABLE data(id INTEGER PRIMARY KEY)",
                "CREATE TABLE audit(value INTEGER)",
                "INSERT INTO data VALUES (1), (2), (3)",
                "CREATE TRIGGER data_after AFTER DELETE ON data BEGIN "
                    + "INSERT INTO audit VALUES (OLD.id); "
                    + "SELECT CASE WHEN OLD.id = 2 THEN RAISE(ABORT, 'delete-stop') END; END",
            ],
            "DELETE FROM data",
            "SELECT 'data', id FROM data UNION ALL SELECT 'audit', value FROM audit ORDER BY 1, 2");
    }

    [Test]
    public void StatefulWherePredicatesAreMaterializedBeforeAfterTriggerPrograms()
    {
        AssertStatefulPredicateMatchesSqlite(
            "UPDATE data SET value = value WHERE total_changes() = 3",
            "SELECT (SELECT count(*) FROM data), (SELECT count(*) FROM audit), changes(), total_changes()");
        AssertStatefulPredicateMatchesSqlite(
            "DELETE FROM data WHERE total_changes() = 3",
            "SELECT (SELECT count(*) FROM data), (SELECT count(*) FROM audit), changes(), total_changes()");
    }

    [Test]
    public void UnsupportedUpdateDeleteAndSelfMutatingTriggerShapesStayEvaluatorOwned()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE updated(value INTEGER)");
        Execute(connection, "CREATE TABLE deleted(id INTEGER PRIMARY KEY)");
        Execute(connection, "CREATE TABLE audit(value INTEGER)");
        Execute(
            connection,
            "CREATE TRIGGER update_before BEFORE UPDATE ON updated BEGIN "
                + "INSERT INTO audit VALUES (OLD.value); END");
        Execute(
            connection,
            "CREATE TRIGGER update_after AFTER UPDATE ON updated BEGIN "
                + "INSERT INTO audit VALUES (NEW.value); END");
        Execute(
            connection,
            "CREATE TRIGGER delete_before BEFORE DELETE ON deleted BEGIN "
                + "INSERT INTO audit VALUES (OLD.id); END");
        Execute(
            connection,
            "CREATE TRIGGER delete_after AFTER DELETE ON deleted BEGIN "
                + "INSERT INTO audit VALUES (OLD.id); END");

        ReadRows(connection, "EXPLAIN UPDATE updated SET value = 1")
            .Select(row => row[1].AsText())
            .Should().Contain("Program");
        ReadRows(connection, "EXPLAIN DELETE FROM deleted")
            .Select(row => row[1].AsText())
            .Should().Contain("Program");
        AssertExplainFallsBack(connection, "EXPLAIN UPDATE updated SET value = 1 RETURNING value");
        AssertExplainFallsBack(connection, "EXPLAIN DELETE FROM deleted RETURNING id");

        Execute(connection, "DROP TRIGGER update_before");
        Execute(connection, "DROP TRIGGER update_after");
        Execute(
            connection,
            "CREATE TRIGGER update_self AFTER UPDATE ON updated BEGIN "
                + "UPDATE updated SET value = NEW.value WHERE rowid = NEW.rowid; END");
        AssertExplainFallsBack(connection, "EXPLAIN UPDATE updated SET value = 2");
    }

    [Test]
    public void InheritedReplaceChainThatCanMutateTheSourceTableStaysEvaluatorOwned()
    {
        string[] setup =
        [
            "PRAGMA recursive_triggers = ON",
            "CREATE TABLE data(id INTEGER PRIMARY KEY, value INTEGER)",
            "CREATE TABLE middle(id INTEGER PRIMARY KEY)",
            "CREATE TABLE victim(id INTEGER PRIMARY KEY)",
            "INSERT INTO data VALUES (1, 10), (2, 20)",
            "INSERT INTO victim VALUES (1)",
            "CREATE TRIGGER data_after AFTER UPDATE ON data BEGIN "
                + "INSERT OR REPLACE INTO middle VALUES (NEW.id); END",
            "CREATE TRIGGER middle_after AFTER INSERT ON middle BEGIN "
                + "INSERT INTO victim VALUES (1); END",
            "CREATE TRIGGER victim_after AFTER DELETE ON victim BEGIN "
                + "DELETE FROM data WHERE id = 2; END",
        ];
        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(managed, sqlite, setup);

        AssertExplainFallsBack(managed, "EXPLAIN UPDATE data SET value = value + 1");
        Execute(managed, "UPDATE data SET value = value + 1");
        Execute(sqlite, "UPDATE data SET value = value + 1");
        AssertQueriesMatch(
            managed,
            sqlite,
            "SELECT 'data', id, value FROM data "
                + "UNION ALL SELECT 'middle', id, NULL FROM middle "
                + "UNION ALL SELECT 'victim', id, NULL FROM victim ORDER BY 1, 2");
    }

    [Test]
    public void InheritedReplaceUpdateThatCanMutateTheSourceTableStaysEvaluatorOwned()
    {
        string[] setup =
        [
            "PRAGMA recursive_triggers = ON",
            "CREATE TABLE data(id INTEGER PRIMARY KEY, value INTEGER)",
            "CREATE TABLE middle(id INTEGER PRIMARY KEY)",
            "CREATE TABLE victim(id INTEGER PRIMARY KEY, key INTEGER UNIQUE)",
            "INSERT INTO data VALUES (1, 10), (2, 20)",
            "INSERT INTO victim VALUES (1, 1), (2, 2)",
            "CREATE TRIGGER data_after AFTER UPDATE ON data BEGIN "
                + "INSERT OR REPLACE INTO middle VALUES (NEW.id); END",
            "CREATE TRIGGER middle_after AFTER INSERT ON middle BEGIN "
                + "UPDATE victim SET key = 1 WHERE id = 2; END",
            "CREATE TRIGGER victim_after AFTER DELETE ON victim BEGIN "
                + "DELETE FROM data WHERE id = 2; END",
        ];
        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(managed, sqlite, setup);

        AssertExplainFallsBack(managed, "EXPLAIN UPDATE data SET value = value + 1");
        Execute(managed, "UPDATE data SET value = value + 1");
        Execute(sqlite, "UPDATE data SET value = value + 1");
        AssertQueriesMatch(
            managed,
            sqlite,
            "SELECT 'data', id, value FROM data "
                + "UNION ALL SELECT 'middle', id, NULL FROM middle "
                + "UNION ALL SELECT 'victim', id, key FROM victim ORDER BY 1, 2");
    }

    private static void AssertProgramTargets(
        IReadOnlyList<SqlValue[]> explain,
        int expectedCount,
        int expectedParameterCount)
    {
        var nextAddress = explain.Single(row => row[1].AsText() == "Next")[0];
        explain.Where(row => row[1].AsText() == "Program")
            .Should().HaveCount(expectedCount)
            .And.OnlyContain(row =>
                row[2] == SqlValue.Integer(0)
                && row[3] == nextAddress
                && row[4] == SqlValue.Integer(expectedParameterCount));
    }

    private static void AssertErrorAndStateMatchesSqlite(
        IReadOnlyList<string> setup,
        string failingSql,
        string query)
    {
        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(managed, sqlite, setup);
        var managedError = Assert.Throws<EmbeddedSqlException>(() => Execute(managed, failingSql));
        var sqliteError = Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, failingSql));
        sqliteError!.Message.Should().Contain(managedError!.Message);
        AssertQueriesMatch(managed, sqlite, query);
    }

    private static void AssertStatefulPredicateMatchesSqlite(string statement, string query)
    {
        string[] setup =
        [
            "CREATE TABLE data(value INTEGER)",
            "CREATE TABLE audit(value INTEGER)",
            "INSERT INTO data VALUES (1), (2), (3)",
            statement.StartsWith("UPDATE", StringComparison.Ordinal)
                ? "CREATE TRIGGER data_after AFTER UPDATE ON data BEGIN INSERT INTO audit VALUES (NEW.value); END"
                : "CREATE TRIGGER data_after AFTER DELETE ON data BEGIN INSERT INTO audit VALUES (OLD.value); END",
        ];
        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(managed, sqlite, setup);
        ReadRows(managed, "EXPLAIN " + statement)
            .Select(row => row[1].AsText())
            .Should().Contain("Program");
        Execute(managed, statement);
        Execute(sqlite, statement);
        AssertQueriesMatch(managed, sqlite, query);
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
