using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;

namespace Ahtola.Tests;

public class DistinctScanSqlRoutingTests
{
    [Test]
    public void DirectColumnDistinctMatchesSqliteValuesAndTypes()
    {
        string[] setup =
        [
            "CREATE TABLE t(i, r, text_value, blob_value)",
            "INSERT INTO t VALUES (1, 1.5, 'one', x'01'), (1, 1.5, 'one', x'01'), (NULL, NULL, NULL, NULL), (2, 2.5, 'two', x'02')",
        ];

        AssertMatchesSqlite(setup, "SELECT DISTINCT i, r, text_value, blob_value FROM t");
    }

    [Test]
    public void FilteredDirectColumnDistinctMatchesSqliteValuesAndTypes()
    {
        string[] setup =
        [
            "CREATE TABLE t(i, r, text_value TEXT COLLATE NOCASE, blob_value, keep)",
            "INSERT INTO t VALUES (1, 1.5, 'one', x'01', 1), (1, 1.5, 'ONE', x'01', 2), (NULL, NULL, NULL, NULL, 2), (NULL, NULL, NULL, NULL, 3), (2, 2.5, 'two', x'02', 2)",
        ];

        AssertMatchesSqlite(setup, "SELECT DISTINCT i, r, text_value, blob_value FROM t WHERE keep >= 2");
    }

    [Test]
    public void FilteredDirectColumnDistinctGatesTheRowBeforeDistinctResultRow()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(a, b, keep)");
        Execute(connection, "INSERT INTO t VALUES (1, 'x', 1), (1, 'x', 2), (NULL, 'x', 2), (NULL, 'x', 3), (2, 'y', 2)");

        var rows = ReadRows(connection, "SELECT DISTINCT a, b FROM t WHERE keep >= ?1", SqlValue.Integer(2));
        rows.Should().HaveCount(3);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("x"));
        rows[1].Should().Equal(SqlValue.Null, SqlValue.Text("x"));
        rows[2].Should().Equal(SqlValue.Integer(2), SqlValue.Text("y"));

        ReadRows(connection, "EXPLAIN SELECT DISTINCT a, b FROM t WHERE keep >= ?1", SqlValue.Integer(2))
            .Select(row => row[1].AsText())
            .Should().Equal(
                "OpenReadCursor", "Rewind", "Column", "LoadParameter", "Compare", "JumpIfNotTrue", "Column",
                "Column", "DistinctResultRow", "Next", "CloseCursor", "Halt");
    }

    [Test]
    public void ResetClearsDistinctSetAndReadsAppendedRows()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(a)");
        Execute(connection, "INSERT INTO t VALUES (1), (1), (2)");

        using var statement = connection.Prepare("SELECT DISTINCT a FROM t");
        Drain(statement).Select(row => row[0]).Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));

        Execute(connection, "INSERT INTO t VALUES (2), (3)");
        statement.Reset();

        Drain(statement).Select(row => row[0]).Should().Equal(
            SqlValue.Integer(1), SqlValue.Integer(2), SqlValue.Integer(3));
    }

    [Test]
    public void FilteredDistinctResetRebindsThePreflightedParameter()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value, keep)");
        Execute(connection, "INSERT INTO t VALUES (1, 1), (1, 2), (2, 2), (3, 3)");

        using var statement = connection.Prepare("SELECT DISTINCT value FROM t WHERE keep >= ?1");
        statement.Bind(1, SqlValue.Integer(2));
        Drain(statement).Select(row => row[0])
            .Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2), SqlValue.Integer(3));

        statement.Reset();
        statement.Bind(1, SqlValue.Integer(3));
        Drain(statement).Select(row => row[0]).Should().Equal(SqlValue.Integer(3));

        ReadRows(connection, "EXPLAIN SELECT DISTINCT value FROM t WHERE keep >= ?1", SqlValue.Integer(3))
            .Select(row => row[1].AsText())
            .Should().Contain("JumpIfNotTrue").And.Contain("DistinctResultRow");
    }

    [Test]
    public void DistinctUsesDeclaredCollationsForDirectAndStarProjections()
    {
        string[] setup =
        [
            "CREATE TABLE t(a TEXT COLLATE NOCASE, b)",
            "INSERT INTO t VALUES ('x', 1), ('X', 1), ('x', 2)",
        ];

        AssertMatchesSqlite(setup, "SELECT DISTINCT a FROM t");
        AssertMatchesSqlite(setup, "SELECT DISTINCT * FROM t");
        AssertMatchesSqlite(setup, "SELECT DISTINCT t.* FROM t");

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var sql in setup)
            Execute(connection, sql);

        ReadRows(connection, "EXPLAIN SELECT DISTINCT a FROM t")
            .Select(row => row[1].AsText())
            .Should().Contain("DistinctResultRow");
    }

    [Test]
    public void CustomCollatedDistinctFallsBackBeforeStreamingRows()
    {
        var database = new EmbeddedDatabase();
        database.RegisterCollation("explode", (_, _) => throw new InvalidOperationException("collation failure"));
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a TEXT COLLATE explode, keep)");
        Execute(connection, "INSERT INTO t VALUES ('x', 1), ('x', 1)");

        using var statement = connection.Prepare("SELECT DISTINCT a FROM t WHERE keep = 1");
        Assert.Throws<InvalidOperationException>(() => statement.Step())!
            .Message.Should().Contain("collation failure");
        ExplainRefused(connection, "EXPLAIN SELECT DISTINCT a FROM t WHERE keep = 1");
    }

    [Test]
    public void DistinctPropagatesDeclaredCollationThroughViewsDerivedTablesAndCtes()
    {
        string[] setup =
        [
            "CREATE TABLE t(a TEXT COLLATE NOCASE)",
            "INSERT INTO t VALUES ('x'), ('X')",
            "CREATE VIEW v AS SELECT a AS x FROM t",
        ];

        AssertMatchesSqlite(setup, "SELECT DISTINCT x FROM v");
        AssertMatchesSqlite(setup, "SELECT DISTINCT x FROM (SELECT a AS x FROM t)");
        AssertMatchesSqlite(setup, "WITH c AS (SELECT a AS x FROM t) SELECT DISTINCT x FROM c");
        AssertMatchesSqlite(setup, "SELECT DISTINCT x FROM (WITH c AS (SELECT a AS x FROM t) SELECT x FROM c)");
        AssertMatchesSqlite(
            setup,
            "WITH c AS (SELECT a FROM t), d AS (WITH c AS (SELECT a FROM t) SELECT a FROM c) SELECT DISTINCT a FROM d");
    }

    [Test]
    public void DistinctKeepsPositionSpecificCollationsForDuplicateDerivedAndCteOutputNames()
    {
        string[] setup =
        [
            "CREATE TABLE t(a TEXT COLLATE NOCASE, b TEXT)",
            "INSERT INTO t VALUES ('x', 'x'), ('X', 'X')",
        ];

        AssertValuesMatchSqlite(setup, "SELECT DISTINCT d.* FROM (SELECT a AS x, b AS x FROM t) d");
        AssertValuesMatchSqlite(setup, "WITH c AS (SELECT a AS x, b AS x FROM t) SELECT DISTINCT c.* FROM c");
    }

    [Test]
    public void DistinctUsesCollationsForCteShadowingRecursiveCtesAndValues()
    {
        AssertMatchesSqlite(
            ["CREATE TABLE t(a TEXT)", "INSERT INTO t VALUES ('table')"],
            "WITH t(a) AS (SELECT 'x' COLLATE NOCASE UNION ALL SELECT 'X') SELECT DISTINCT a FROM t");

        string[] recursiveSetup =
        [
            "CREATE TABLE t(a TEXT COLLATE NOCASE)",
            "INSERT INTO t VALUES ('x'), ('X')",
        ];
        AssertMatchesSqlite(
            recursiveSetup,
            "WITH RECURSIVE c(x) AS (SELECT a FROM t UNION ALL SELECT x FROM c WHERE 0) SELECT DISTINCT x FROM c");

        AssertMatchesSqlite(
            [],
            "SELECT DISTINCT * FROM (VALUES ('x' COLLATE NOCASE), ('X'))");
        AssertMatchesSqlite(
            [],
            "WITH c(x) AS (VALUES ('x' COLLATE NOCASE), ('X')) SELECT DISTINCT x FROM c");
    }

    [Test]
    public void ComputedCollatedStarRowidAndComplexFilteredDistinctFallBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(a TEXT, b)");
        Execute(connection, "INSERT INTO t VALUES ('x', 1), ('X', 1), ('x', 2)");

        ReadRows(connection, "SELECT DISTINCT a COLLATE NOCASE FROM t")[0]
            .Should().Equal(SqlValue.Text("x"));
        ReadRows(connection, "SELECT DISTINCT a + 1 FROM t")[0][0]
            .AsInteger().Should().Be(1);
        ReadRows(connection, "SELECT DISTINCT * FROM t").Should().HaveCount(3);
        ReadRows(connection, "SELECT DISTINCT rowid FROM t").Should().HaveCount(3);
        ReadRows(connection, "SELECT DISTINCT a FROM t WHERE b + 0 = 1")
            .Select(row => row[0])
            .Should().Equal(SqlValue.Text("x"), SqlValue.Text("X"));
        using var missingFunctionStatement =
            connection.Prepare("SELECT DISTINCT a FROM t WHERE no_such_function(b)");
        var missingFunction = Assert.Throws<EmbeddedSqlException>(() => missingFunctionStatement.Step())!;
        missingFunction.Message.Should().Contain("no such function");

        ExplainRefused(connection, "EXPLAIN SELECT DISTINCT a COLLATE NOCASE FROM t");
        ExplainRefused(connection, "EXPLAIN SELECT DISTINCT a + 1 FROM t");
        ExplainRefused(connection, "EXPLAIN SELECT DISTINCT * FROM t");
        ExplainRefused(connection, "EXPLAIN SELECT DISTINCT rowid FROM t");
        ExplainRefused(connection, "EXPLAIN SELECT DISTINCT a FROM t WHERE b + 0 = 1");
        ExplainRefused(connection, "EXPLAIN SELECT DISTINCT a FROM t WHERE no_such_function(b)");
    }

    private static void AssertMatchesSqlite(IReadOnlyList<string> setup, string query)
    {
        var managed = RunManaged(setup, query);
        var sqlite = RunSqlite(setup, query);

        managed.Columns.Should().Equal(sqlite.Columns);
        AssertRowValuesMatchSqlite(managed.Rows, sqlite.Rows);
    }

    private static void AssertValuesMatchSqlite(IReadOnlyList<string> setup, string query)
    {
        var managed = RunManaged(setup, query);
        var sqlite = RunSqlite(setup, query);

        AssertRowValuesMatchSqlite(managed.Rows, sqlite.Rows);
    }

    private static void AssertRowValuesMatchSqlite(
        IReadOnlyList<SqlValue[]> managedRows,
        IReadOnlyList<object?[]> sqliteRows)
    {
        managedRows.Should().HaveCount(sqliteRows.Count);
        for (var row = 0; row < sqliteRows.Count; row++)
        {
            managedRows[row].Should().HaveCount(sqliteRows[row].Length);
            for (var column = 0; column < sqliteRows[row].Length; column++)
                CellShouldMatch(managedRows[row][column], sqliteRows[row][column]);
        }
    }

    private static (string[] Columns, List<SqlValue[]> Rows) RunManaged(IReadOnlyList<string> setup, string query)
    {
        using var connection = new EmbeddedDatabase().Connect();
        foreach (var sql in setup)
            Execute(connection, sql);

        using var statement = connection.Prepare(query);
        var columns = Enumerable.Range(0, statement.GetColumnCount()).Select(statement.GetColumnName).ToArray();
        return (columns, Drain(statement));
    }

    private static (string[] Columns, List<object?[]> Rows) RunSqlite(IReadOnlyList<string> setup, string query)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var sql in setup)
        {
            using var setupCommand = connection.CreateCommand();
            setupCommand.CommandText = sql;
            setupCommand.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = query;
        using var reader = command.ExecuteReader();
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var column = 0; column < row.Length; column++)
                row[column] = reader.IsDBNull(column) ? null : reader.GetValue(column);

            rows.Add(row);
        }

        return (columns, rows);
    }

    private static void CellShouldMatch(SqlValue managed, object? sqlite)
    {
        switch (sqlite)
        {
            case null:
                managed.Kind.Should().Be(SqlValueKind.Null);
                break;
            case long integer:
                managed.Should().Be(SqlValue.Integer(integer));
                break;
            case double real:
                managed.Should().Be(SqlValue.Real(real));
                break;
            case string text:
                managed.Should().Be(SqlValue.Text(text));
                break;
            case byte[] blob:
                managed.Kind.Should().Be(SqlValueKind.Blob);
                managed.AsBlob().ToArray().Should().Equal(blob);
                break;
            default:
                Assert.Fail($"Unexpected SQLite value type {sqlite.GetType().Name}.");
                break;
        }
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static List<SqlValue[]> ReadRows(
        EmbeddedConnection connection,
        string sql,
        params SqlValue[] parameters)
    {
        using var statement = connection.Prepare(sql);
        for (var index = 0; index < parameters.Length; index++)
            statement.Bind(index + 1, parameters[index]);
        return Drain(statement);
    }

    private static void ExplainRefused(EmbeddedConnection connection, string sql)
    {
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, sql))!
            .Message.Should().Contain("EXPLAIN is only supported");
    }

    private static List<SqlValue[]> Drain(EmbeddedStatement statement)
    {
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var row = new SqlValue[statement.GetColumnCount()];
            for (var column = 0; column < row.Length; column++)
                row[column] = statement.GetValue(column);

            rows.Add(row);
        }

        return rows;
    }
}
