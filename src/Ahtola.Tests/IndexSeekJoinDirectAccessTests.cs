using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

/// <summary>
/// Focused coverage for the first direct-access phase: autocommit, committed file-backed
/// rowid-table joins that drive a safe partial index or expression index must use the durable
/// pager b-tree cursor (<see cref="VdbeJoinIndexSeekMetrics.DurableCursorPlans"/>, zero
/// <see cref="VdbeJoinIndexSeekMetrics.IndexRowsMaterialized"/>) instead of the materialized-index
/// fallback, but only when eligibility is actually proven.
/// </summary>
public sealed class IndexSeekJoinDirectAccessTests
{
    [Test]
    public void DurableReopenUsesPagerSeekForProvenPartialIndex()
    {
        string[] setup =
        [
            "CREATE TABLE outer_items(k INTEGER);",
            "CREATE TABLE inner_items(k INTEGER, gate INTEGER, payload TEXT);",
            "CREATE INDEX inner_items_partial ON inner_items(k) WHERE gate > 0;",
            "INSERT INTO outer_items VALUES (2), (40);",
            "INSERT INTO inner_items VALUES "
                + string.Join(", ", Enumerable.Range(1, 500).Select(value => $"({value}, 1, 'p{value}')"))
                + ";",
            "ANALYZE;",
        ];
        const string sql =
            """
            SELECT inner_items.payload
            FROM outer_items JOIN inner_items INDEXED BY inner_items_partial
            ON outer_items.k = inner_items.k AND inner_items.gate > 0
            ORDER BY outer_items.k;
            """;

        AssertMatchesSqlite(setup, sql);

        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("partial-index-join.db", fileSystem))
        using (var connection = database.Connect())
        {
            foreach (var statement in setup)
                Execute(connection, statement);
        }

        using var reopened = EmbeddedDatabase.OpenFile("partial-index-join.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadRows(reopenedConnection, "EXPLAIN QUERY PLAN " + sql).Single()[3].AsText()
            .Should().Be("SEARCH inner_items USING INDEX inner_items_partial (k=?)");

        reopened.ResetJoinOrderDiagnostics();
        ReadRows(reopenedConnection, sql).Select(row => row[0].AsText()).Should().Equal("p2", "p40");
        reopened.JoinIndexSeekMetrics.DurableCursorPlans.Should().BeGreaterThan(0);
        reopened.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);
    }

    [Test]
    public void UnprovenPartialIndexPredicateDeclinesAndKeepsRowsResidual()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("partial-index-unsafe.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_items(k INTEGER);");
        Execute(connection, "CREATE TABLE inner_items(k INTEGER, gate INTEGER, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_items_partial ON inner_items(k) WHERE gate > 0;");
        Execute(connection, "INSERT INTO outer_items VALUES (2);");
        Execute(connection, "INSERT INTO inner_items VALUES (2, 1, 'kept'), (2, -1, 'dropped');");
        Execute(connection, "ANALYZE;");

        // Nothing in the ON/WHERE clause proves inner_items.gate > 0, so the partial index must
        // stay an ineligible candidate: the join-order planner (unforced) must decline it rather
        // than ever seeking inner_items_partial, and every row -- including the one that fails
        // the index's own "gate > 0" predicate -- must still surface as residual output.
        const string sql =
            """
            SELECT inner_items.payload
            FROM outer_items JOIN inner_items
            ON outer_items.k = inner_items.k
            ORDER BY inner_items.payload;
            """;
        ReadRows(connection, "EXPLAIN QUERY PLAN " + sql).Single()[3].AsText()
            .Should().NotContain("inner_items_partial");

        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, sql).Select(row => row[0].AsText()).Should().Equal("dropped", "kept");
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().Be(0);
    }

    [Test]
    public void DurableReopenUsesPagerSeekForProvenExpressionIndex()
    {
        string[] setup =
        [
            "CREATE TABLE outer_items(k);",
            "CREATE TABLE inner_items(k TEXT, payload TEXT);",
            "CREATE INDEX inner_items_lower_k ON inner_items(lower(k));",
            "INSERT INTO outer_items VALUES ('b'), ('zz');",
            "INSERT INTO inner_items VALUES ('B', 'p-B'), ('ZZ', 'p-ZZ'), "
                + string.Join(
                    ", ",
                    Enumerable.Range(1, 500).Select(value => $"('noise{value}', 'n{value}')"))
                + ";",
            "ANALYZE;",
        ];
        const string sql =
            """
            SELECT inner_items.payload
            FROM outer_items JOIN inner_items INDEXED BY inner_items_lower_k
            ON outer_items.k = lower(inner_items.k)
            ORDER BY outer_items.k;
            """;

        AssertMatchesSqlite(setup, sql);

        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("expression-index-join.db", fileSystem))
        using (var connection = database.Connect())
        {
            foreach (var statement in setup)
                Execute(connection, statement);
        }

        using var reopened = EmbeddedDatabase.OpenFile("expression-index-join.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadRows(reopenedConnection, "EXPLAIN QUERY PLAN " + sql).Single()[3].AsText()
            .Should().Contain("inner_items_lower_k");

        reopened.ResetJoinOrderDiagnostics();
        ReadRows(reopenedConnection, sql).Select(row => row[0].AsText()).Should().Equal("p-B", "p-ZZ");
        reopened.JoinIndexSeekMetrics.DurableCursorPlans.Should().BeGreaterThan(0);
        reopened.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);
    }

    [TestCase(
        "CREATE INDEX inner_items_expr ON inner_items(lower(a));",
        "outer_items.a = upper(inner_items.a)",
        "X",
        TestName = "MismatchedIndexedExpressionDeclines")]
    [TestCase(
        "CREATE INDEX inner_items_expr ON inner_items(lower(a) COLLATE NOCASE);",
        "outer_items.a = lower(inner_items.a)",
        "x",
        TestName = "MismatchedExpressionIndexCollationDeclines")]
    public void MismatchedExpressionIndexJoinNeverAdvertisesSearch(string createIndex, string condition, string outerValue)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_items(a TEXT, b INTEGER);");
        Execute(connection, "CREATE TABLE inner_items(a TEXT, b INTEGER);");
        Execute(connection, createIndex);
        Execute(connection, $"INSERT INTO outer_items VALUES ('{outerValue}', 1);");
        Execute(connection, "INSERT INTO inner_items VALUES ('x', 1), ('y', 2), ('z', 3);");
        Execute(connection, "ANALYZE;");

        var detail = ReadRows(
            connection,
            $"EXPLAIN QUERY PLAN SELECT outer_items.a FROM outer_items JOIN inner_items ON {condition};");
        detail.Select(row => row[3].AsText())
            .Should().NotContain(value => value.StartsWith("SEARCH ", StringComparison.Ordinal));

        ReadRows(
                connection,
                $"SELECT outer_items.a FROM outer_items JOIN inner_items ON {condition};")
            .Should().ContainSingle();
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().Be(0);
    }

    [Test]
    public void ExpressionIndexSeekDeclinesWhenComparisonAffinityWouldTransformTheStoredExpression()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_items(k INTEGER);");
        Execute(connection, "CREATE TABLE inner_items(k TEXT, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_items_lower_k ON inner_items(lower(k));");
        Execute(connection, "INSERT INTO outer_items VALUES (1);");
        Execute(connection, "INSERT INTO inner_items VALUES ('1', 'hit');");
        Execute(connection, "ANALYZE;");

        const string sql =
            """
            SELECT inner_items.payload
            FROM outer_items JOIN inner_items ON outer_items.k = lower(inner_items.k);
            """;
        ReadRows(connection, "EXPLAIN QUERY PLAN " + sql)
            .Select(row => row[3].AsText())
            .Should().NotContain(value => value.StartsWith("SEARCH ", StringComparison.Ordinal));
        ReadRows(connection, sql).Single()[0].AsText().Should().Be("hit");
    }

    [Test]
    public void ExpressionIndexSeekDeclinesTextAffinityOuterProbeAgainstNumericExpression()
    {
        // The indexed expression "x + 0" always produces a NUMERIC-ish storage class (its
        // arithmetic result), but nothing declares that statically for an arbitrary expression
        // index, so the outer probe's own affinity is all that is known. A TEXT-affinity outer
        // probe ('1') is exactly SQLite's rule-2 case (datatype3.html §7.1): TEXT affinity would
        // need to be applied to the *indexed* side before comparing, which a seek keyed on the
        // raw probe value cannot honor (the b-tree stores '1 + 0' as INTEGER 1, sorted with
        // typed comparison, so a raw TEXT '1' seek key would search the wrong region and miss
        // the match entirely). The join must decline the index for this shape and fall back to a
        // residual scan, which evaluates the predicate row-by-row with the correct affinity
        // rules and matches real SQLite's result.
        string[] setup =
        [
            "CREATE TABLE outer_items(t TEXT);",
            "CREATE TABLE inner_items(x INTEGER, payload TEXT);",
            "CREATE INDEX inner_items_x_plus_zero ON inner_items(x + 0);",
            "INSERT INTO outer_items VALUES ('1');",
            "INSERT INTO inner_items VALUES (1, 'hit'), (2, 'miss');",
            "ANALYZE;",
        ];
        const string sql =
            """
            SELECT inner_items.payload
            FROM outer_items JOIN inner_items ON outer_items.t = inner_items.x + 0;
            """;

        AssertMatchesSqlite(setup, sql);

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        ReadRows(connection, "EXPLAIN QUERY PLAN " + sql)
            .Select(row => row[3].AsText())
            .Should().NotContain(value => value.StartsWith("SEARCH ", StringComparison.Ordinal));
        ReadRows(connection, sql).Single()[0].AsText().Should().Be("hit");
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().Be(0);
    }

    [Test]
    public void RegisteredFunctionOverrideCannotDriveAStaleExpressionIndex()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_items(k);");
        Execute(connection, "CREATE TABLE inner_items(k TEXT, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_items_lower_k ON inner_items(lower(k));");
        Execute(connection, "INSERT INTO outer_items VALUES ('ABC');");
        Execute(connection, "INSERT INTO inner_items VALUES ('ABC', 'hit');");
        Execute(connection, "ANALYZE;");
        database.RegisterScalarFunction("lower", 1, static values => values[0]);

        const string sql =
            """
            SELECT inner_items.payload
            FROM outer_items JOIN inner_items ON outer_items.k = lower(inner_items.k);
            """;
        ReadRows(connection, "EXPLAIN QUERY PLAN " + sql)
            .Select(row => row[3].AsText())
            .Should().NotContain(value => value.StartsWith("SEARCH ", StringComparison.Ordinal));
        ReadRows(connection, sql).Single()[0].AsText().Should().Be("hit");
    }

    [Test]
    public void ExpressionIndexSeekDeclinesWhenAnIndexedCastInheritsAnIncompatibleCollation()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_items(k BLOB);");
        Execute(connection, "CREATE TABLE inner_items(k TEXT COLLATE NOCASE, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_cast ON inner_items(CAST(k AS TEXT) COLLATE BINARY);");
        Execute(connection, "INSERT INTO outer_items VALUES ('a');");
        Execute(connection, "INSERT INTO inner_items VALUES ('A', 'hit');");
        Execute(connection, "ANALYZE;");

        const string sql =
            """
            SELECT inner_items.payload
            FROM outer_items JOIN inner_items ON CAST(inner_items.k AS TEXT) = outer_items.k;
            """;
        ReadRows(connection, "EXPLAIN QUERY PLAN " + sql)
            .Select(row => row[3].AsText())
            .Should().NotContain(value => value.StartsWith("SEARCH ", StringComparison.Ordinal));
        ReadRows(connection, sql).Single()[0].AsText().Should().Be("hit");
    }

    private static void AssertMatchesSqlite(IReadOnlyList<string> setup, string sql)
    {
        using var managed = OpenManaged(setup);
        var managedRows = ReadRows(managed, sql);
        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in setup)
        {
            using var command = sqlite.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }

        using var query = sqlite.CreateCommand();
        query.CommandText = sql;
        using var reader = query.ExecuteReader();
        var sqliteRows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var index = 0; index < row.Length; index++)
                row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            sqliteRows.Add(row);
        }

        managedRows.Should().HaveCount(sqliteRows.Count);
        for (var row = 0; row < sqliteRows.Count; row++)
        {
            for (var column = 0; column < sqliteRows[row].Length; column++)
                CellShouldMatch(managedRows[row][column], sqliteRows[row][column], row, column);
        }
    }

    private static EmbeddedConnection OpenManaged(IReadOnlyList<string> setup)
    {
        var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);
        return connection;
    }

    private static void CellShouldMatch(SqlValue managed, object? sqlite, int row, int column)
    {
        switch (sqlite)
        {
            case null:
                managed.Should().Be(SqlValue.Null, $"at row {row}, column {column}");
                break;
            case long integer:
                managed.Should().Be(SqlValue.Integer(integer), $"at row {row}, column {column}");
                break;
            case double real:
                managed.Should().Be(SqlValue.Real(real), $"at row {row}, column {column}");
                break;
            case string text:
                managed.Should().Be(SqlValue.Text(text), $"at row {row}, column {column}");
                break;
            case byte[] blob:
                managed.AsBlob().ToArray().Should().Equal(blob);
                break;
            default:
                throw new InvalidOperationException($"Unsupported SQLite value type {sqlite.GetType()}.");
        }
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static List<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var values = new SqlValue[statement.GetColumnCount()];
            for (var ordinal = 0; ordinal < values.Length; ordinal++)
                values[ordinal] = statement.GetValue(ordinal);
            rows.Add(values);
        }

        return rows;
    }
}
