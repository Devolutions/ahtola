using AwesomeAssertions;
using Ahtola.Core;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

// Coverage for the managed engine's SQLite rowid and INTEGER PRIMARY KEY semantics.
// Every behavioural case is cross-checked byte-for-byte against a real SQLite build
// (Microsoft.Data.Sqlite) so the managed hidden-rowid handling, alias autogeneration,
// coercion, conflict messages, last_insert_rowid(), and AUTOINCREMENT stay compatible.
public class RowidSemanticsTests
{
    [Test]
    public void HiddenRowidAutogeneratesSequentiallyFromOne()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(a TEXT)",
                "INSERT INTO t(a) VALUES ('x')",
                "INSERT INTO t(a) VALUES ('y')",
            ],
            "SELECT rowid, a FROM t ORDER BY rowid");
    }

    [Test]
    public void RowidAliasesResolveInterchangeably()
    {
        // rowid/_rowid_/oid all resolve to the same value. (SQLite titles every such bare
        // reference "rowid", so alias them to compare the resolved values, not the titles.)
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(a TEXT)",
                "INSERT INTO t(a) VALUES ('x')",
                "INSERT INTO t(a) VALUES ('y')",
            ],
            "SELECT rowid AS r0, _rowid_ AS r1, oid AS r2 FROM t ORDER BY rowid");
    }

    [Test]
    public void SelectStarExcludesTheHiddenRowid()
    {
        // AssertMatchesSqlite compares column names, so a mismatch here would mean the
        // managed engine surfaced the rowid as a declared column.
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(a TEXT, b INT)",
                "INSERT INTO t(a, b) VALUES ('x', 1)",
            ],
            "SELECT * FROM t");
    }

    [Test]
    public void ExplicitRowidAdvancesTheAutogenerationHighWaterMark()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(a TEXT)",
                "INSERT INTO t(a) VALUES ('first')",
                "INSERT INTO t(rowid, a) VALUES (100, 'explicit')",
                "INSERT INTO t(a) VALUES ('after')",
            ],
            "SELECT rowid, a FROM t ORDER BY rowid");
    }

    [Test]
    public void RowidTableScanFollowsBtreeKeyOrderWithoutAnOrderByClause()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(x, y INTEGER PRIMARY KEY, z)",
                "INSERT INTO t(z, x, y, rowid) VALUES (1, 2, 3, 4), (5, 6, 7, 8)",
                "INSERT INTO t(z, x, y, rowid) VALUES (9, 10, 11, 12)",
                "INSERT INTO t(z, x, rowid, y) VALUES (-1, -2, -3, -4), (-5, -6, -7, -8)",
                "INSERT INTO t(z, x, rowid, y) VALUES (-9, -10, -11, -12)",
            ],
            "SELECT rowid AS y, x, y, z FROM t");
    }

    [Test]
    public void NegativeRowidCountsTowardTheHighWaterMark()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(a TEXT)",
                "INSERT INTO t(rowid, a) VALUES (-5, 'neg')",
                "INSERT INTO t(a) VALUES ('auto')",
            ],
            "SELECT rowid, a FROM t ORDER BY rowid");
    }

    [Test]
    public void ZeroRowidIsStoredVerbatim()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(a TEXT)",
                "INSERT INTO t(rowid, a) VALUES (0, 'zero')",
            ],
            "SELECT rowid, a FROM t");
    }

    [Test]
    public void IntegerTextIsCoercedIntoTheRowid()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(a TEXT)",
                "INSERT INTO t(rowid, a) VALUES ('123', 'x')",
            ],
            "SELECT rowid, typeof(rowid) AS ty, a FROM t");
    }

    [Test]
    public void IntegralRealAndRealTextAreCoercedIntoTheRowid()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(a TEXT)",
                "INSERT INTO t(rowid, a) VALUES (5.0, 'real')",
                "INSERT INTO t(rowid, a) VALUES ('2.0', 'realtext')",
            ],
            "SELECT rowid, typeof(rowid) AS ty, a FROM t ORDER BY rowid");
    }

    [Test]
    public void IntegerPrimaryKeyAliasesTheRowidAndAutogenerates()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(id INTEGER PRIMARY KEY, a TEXT)",
                "INSERT INTO t(a) VALUES ('x')",
                "INSERT INTO t(id, a) VALUES (NULL, 'y')",
                "INSERT INTO t(a) VALUES ('z')",
            ],
            "SELECT id, rowid AS rid, a FROM t ORDER BY id");
    }

    [Test]
    public void IntegerPrimaryKeyAppliesIntegerAffinityToText()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(id INTEGER PRIMARY KEY, a TEXT)",
                "INSERT INTO t(id, a) VALUES ('5', 'five')",
            ],
            "SELECT id, typeof(id) AS ty, a FROM t");
    }

    [Test]
    public void IntegerPrimaryKeyDescendingIsNotARowidAlias()
    {
        // DESC disqualifies the column from aliasing the rowid: SQLite leaves it a normal
        // column (NULL when omitted) backed by a separate rowid.
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(id INTEGER PRIMARY KEY DESC, a TEXT)",
                "INSERT INTO t(a) VALUES ('x')",
            ],
            "SELECT id, rowid, typeof(id) AS ty, a FROM t");
    }

    [Test]
    public void RowidPseudoColumnOverridesTheAliasColumn()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(id INTEGER PRIMARY KEY, a TEXT)",
                "INSERT INTO t(id, rowid, a) VALUES (3, 6, 'x')",
            ],
            "SELECT id, rowid AS rid, a FROM t");
    }

    [Test]
    public void RealColumnNamedRowidShadowsThePseudoColumn()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(rowid TEXT, a TEXT)",
                "INSERT INTO t(rowid, a) VALUES ('label', 'x')",
            ],
            "SELECT rowid, a FROM t");
    }

    [Test]
    public void UpdatingTheAliasColumnMovesTheRowid()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(id INTEGER PRIMARY KEY, a TEXT)",
                "INSERT INTO t(id, a) VALUES (1, 'x')",
                "INSERT INTO t(id, a) VALUES (2, 'y')",
                "UPDATE t SET id = 50 WHERE a = 'x'",
            ],
            "SELECT id, rowid AS rid, a FROM t ORDER BY id");
    }

    [Test]
    public void UpdatingTheHiddenRowidMovesTheRow()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(a TEXT)",
                "INSERT INTO t(a) VALUES ('x')",
                "INSERT INTO t(a) VALUES ('y')",
                "UPDATE t SET rowid = 99 WHERE a = 'x'",
            ],
            "SELECT rowid, a FROM t ORDER BY rowid");
    }

    [Test]
    public void DeletingTheMaximumRowidThenReinsertingReusesTheGap()
    {
        // A plain rowid table (no AUTOINCREMENT) reuses max+1 after the top row is gone.
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(a TEXT)",
                "INSERT INTO t(a) VALUES ('a')",
                "INSERT INTO t(a) VALUES ('b')",
                "INSERT INTO t(a) VALUES ('c')",
                "DELETE FROM t WHERE rowid = 3",
                "INSERT INTO t(a) VALUES ('d')",
            ],
            "SELECT rowid, a FROM t ORDER BY rowid");
    }

    [Test]
    public void WhereOnRowidFiltersRowsOnTheEvaluator()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(a TEXT)",
                "INSERT INTO t(a) VALUES ('a')",
                "INSERT INTO t(a) VALUES ('b')",
                "INSERT INTO t(a) VALUES ('c')",
            ],
            "SELECT a FROM t WHERE rowid = 2");
    }

    [Test]
    public void RangeWhereOnRowidMatchesSqlite()
    {
        // Exercises the compiled-scan fallback: a WHERE over the unbacked rowid pseudo
        // column must run on the evaluator rather than the materialized-column scan.
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(a TEXT)",
                "INSERT INTO t(a) VALUES ('a')",
                "INSERT INTO t(a) VALUES ('b')",
                "INSERT INTO t(a) VALUES ('c')",
                "INSERT INTO t(a) VALUES ('d')",
            ],
            "SELECT rowid, a FROM t WHERE rowid > 1 AND rowid <= 3 ORDER BY rowid");
    }

    [Test]
    public void RowidRoundTripsThroughReturning()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(a TEXT)",
            ],
            "INSERT INTO t(a) VALUES ('x'), ('y') RETURNING rowid, a");
    }

    [Test]
    public void LastInsertRowidDefaultsToZeroBeforeAnyInsert()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(a TEXT)",
            ],
            "SELECT last_insert_rowid() AS lir");
    }

    [Test]
    public void LastInsertRowidReportsTheMostRecentInsert()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(a TEXT)",
                "INSERT INTO t(a) VALUES ('x')",
                "INSERT INTO t(rowid, a) VALUES (40, 'y')",
            ],
            "SELECT last_insert_rowid() AS lir");
    }

    [Test]
    public void LastInsertRowidIsUnchangedByUpdatesAndDeletes()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(a TEXT)",
                "INSERT INTO t(rowid, a) VALUES (7, 'x')",
                "UPDATE t SET a = 'z' WHERE rowid = 7",
                "DELETE FROM t WHERE rowid = 7",
            ],
            "SELECT last_insert_rowid() AS lir");
    }

    [Test]
    public void DuplicateAliasRowidReportsAQualifiedUniqueError()
    {
        AssertErrorMatchesSqlite(
            [
                "CREATE TABLE t(id INTEGER PRIMARY KEY, a TEXT)",
                "INSERT INTO t(id, a) VALUES (1, 'x')",
            ],
            "INSERT INTO t(id, a) VALUES (1, 'y')");
    }

    [Test]
    public void DuplicateHiddenRowidReportsAQualifiedUniqueError()
    {
        AssertErrorMatchesSqlite(
            [
                "CREATE TABLE t(a TEXT)",
                "INSERT INTO t(rowid, a) VALUES (1, 'x')",
            ],
            "INSERT INTO t(rowid, a) VALUES (1, 'y')");
    }

    [Test]
    public void NonNumericTextRowidReportsDatatypeMismatch()
    {
        AssertErrorMatchesSqlite(
            [
                "CREATE TABLE t(a TEXT)",
            ],
            "INSERT INTO t(rowid, a) VALUES ('abc', 'x')");
    }

    [Test]
    public void FractionalRealRowidReportsDatatypeMismatch()
    {
        AssertErrorMatchesSqlite(
            [
                "CREATE TABLE t(a TEXT)",
            ],
            "INSERT INTO t(rowid, a) VALUES (2.5, 'x')");
    }

    [Test]
    public void UpdatingTheAliasColumnToNullReportsDatatypeMismatch()
    {
        AssertErrorMatchesSqlite(
            [
                "CREATE TABLE t(id INTEGER PRIMARY KEY, a TEXT)",
                "INSERT INTO t(id, a) VALUES (1, 'x')",
            ],
            "UPDATE t SET id = NULL WHERE a = 'x'");
    }

    [Test]
    public void UpdatingARowidIntoAnExistingRowReportsAUniqueError()
    {
        AssertErrorMatchesSqlite(
            [
                "CREATE TABLE t(id INTEGER PRIMARY KEY, a TEXT)",
                "INSERT INTO t(id, a) VALUES (1, 'x')",
                "INSERT INTO t(id, a) VALUES (2, 'y')",
            ],
            "UPDATE t SET id = 1 WHERE id = 2");
    }

    [Test]
    public void AutoIncrementUsesDurableMonotonicSequenceSemantics()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, a TEXT)",
                "INSERT INTO t(a) VALUES ('first')",
                "INSERT INTO t(id, a) VALUES (10, 'explicit')",
                "DELETE FROM t WHERE id = 10",
                "INSERT INTO t(a) VALUES ('after-delete')",
            ],
            "SELECT id, a, (SELECT seq FROM sqlite_sequence WHERE name = 't') AS seq "
            + "FROM t ORDER BY id");
    }

    private static void AssertMatchesSqlite(IReadOnlyList<string> setup, string query)
    {
        var managed = RunManaged(setup, query);
        var reference = RunSqlite(setup, query);

        managed.Columns.Should().Equal(reference.Columns, "column names should match SQLite");
        managed.Rows.Should().HaveCount(reference.Rows.Count);
        for (var row = 0; row < reference.Rows.Count; row++)
        {
            managed.Rows[row].Should().HaveCount(reference.Rows[row].Length, "row {0} width should match SQLite", row);
            for (var column = 0; column < reference.Rows[row].Length; column++)
                CellsShouldMatch(managed.Rows[row][column], reference.Rows[row][column], row, column);
        }
    }

    private static void AssertErrorMatchesSqlite(IReadOnlyList<string> setup, string query)
    {
        var managedMessage = CaptureManagedError(setup, query);
        var sqliteMessage = CaptureSqliteError(setup, query);

        // Microsoft.Data.Sqlite wraps the core message as "SQLite Error NN: '<message>'.",
        // so the managed engine's message must appear verbatim inside SQLite's.
        sqliteMessage.Should().Contain(
            managedMessage,
            "the managed error should match the SQLite error text");
    }

    private static string CaptureManagedError(IReadOnlyList<string> setup, string query)
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        var exception = Assert.Throws<EmbeddedSqlException>(() =>
        {
            using var statement = connection.Prepare(query);
            while (statement.Step() == StatementStepResult.Row)
            {
            }
        });

        return exception!.Message;
    }

    private static string CaptureSqliteError(IReadOnlyList<string> setup, string query)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var statement in setup)
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }

        var exception = Assert.Throws<MsData.SqliteException>(() =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = query;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
            }
        });

        return exception!.Message;
    }

    private static QueryOutput RunManaged(IReadOnlyList<string> setup, string query)
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        using var command = connection.Prepare(query);
        var columns = new string[command.GetColumnCount()];
        for (var ordinal = 0; ordinal < columns.Length; ordinal++)
            columns[ordinal] = command.GetColumnName(ordinal);

        var rows = new List<SqlValue[]>();
        while (command.Step() == StatementStepResult.Row)
        {
            var values = new SqlValue[command.GetColumnCount()];
            for (var ordinal = 0; ordinal < values.Length; ordinal++)
                values[ordinal] = command.GetValue(ordinal);

            rows.Add(values);
        }

        return new QueryOutput(columns, rows);
    }

    private static ReferenceOutput RunSqlite(IReadOnlyList<string> setup, string query)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var statement in setup)
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }

        using var queryCommand = connection.CreateCommand();
        queryCommand.CommandText = query;
        using var reader = queryCommand.ExecuteReader();
        var columns = new string[reader.FieldCount];
        for (var column = 0; column < columns.Length; column++)
            columns[column] = reader.GetName(column);

        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var values = new object?[reader.FieldCount];
            for (var column = 0; column < values.Length; column++)
                values[column] = reader.IsDBNull(column) ? null : reader.GetValue(column);

            rows.Add(values);
        }

        return new ReferenceOutput(columns, rows);
    }

    private static void CellsShouldMatch(SqlValue managed, object? reference, int row, int column)
    {
        var because = $"cell ({row},{column}) should match SQLite";
        switch (reference)
        {
            case null:
                managed.Kind.Should().Be(SqlValueKind.Null, because);
                break;
            case long integer:
                managed.Kind.Should().Be(SqlValueKind.Integer, because);
                managed.AsInteger().Should().Be(integer, because);
                break;
            case double real:
                managed.Kind.Should().Be(SqlValueKind.Real, because);
                managed.AsReal().Should().BeApproximately(real, 1e-9, because);
                break;
            case string text:
                managed.Kind.Should().Be(SqlValueKind.Text, because);
                managed.AsText().Should().Be(text, because);
                break;
            case byte[] blob:
                managed.Kind.Should().Be(SqlValueKind.Blob, because);
                managed.AsBlob().ToArray().Should().Equal(blob, because);
                break;
            default:
                managed.ToString().Should().Be(reference.ToString(), because);
                break;
        }
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private sealed record QueryOutput(string[] Columns, IReadOnlyList<SqlValue[]> Rows);

    private sealed record ReferenceOutput(string[] Columns, IReadOnlyList<object?[]> Rows);
}
