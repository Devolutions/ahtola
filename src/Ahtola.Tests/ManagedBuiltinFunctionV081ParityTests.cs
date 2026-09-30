using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;

namespace Ahtola.Tests;

/// <summary>
/// Differential regressions for the built-in function behavior Turso v0.8.1 aligned with
/// SQLite: scalar min/max ties, BLOB date/time arguments, pre-epoch second rounding, and the
/// selectable rowid of json_each/json_tree. Each case is compared with the bundled SQLite.
/// </summary>
public sealed class ManagedBuiltinFunctionV081ParityTests
{
    // Upstream 78b1ac633: SQLite's minmaxFunc lets min() take the later of tied arguments
    // while max() keeps the earlier one, including under an argument collation.
    [TestCase("SELECT min(1, 1.0), max(1, 1.0), min(1.0, 1), max(1.0, 1)")]
    [TestCase("SELECT typeof(min(1, 1.0)), typeof(max(1, 1.0)), typeof(min(1.0, 1)), typeof(max(1.0, 1))")]
    [TestCase("SELECT min(2, 2.0, 2), min(2.0, 2, 2.0), max(2, 2.0, 2), max(2.0, 2, 2.0)")]
    [TestCase("SELECT min('a' COLLATE NOCASE, 'A'), max('A' COLLATE NOCASE, 'a')")]
    [TestCase("SELECT min('A', 'a' COLLATE NOCASE), max('a', 'A' COLLATE NOCASE)")]
    [TestCase("SELECT min('A' COLLATE NOCASE, 'a', 'B'), max('b' COLLATE NOCASE, 'B', 'a')")]
    // Upstream b2512eb71: a BLOB date/time value or modifier is read as its UTF-8 text.
    [TestCase("SELECT date(x'323032342D30312D3031'), unixepoch(x'323032342D30312D3031')")]
    [TestCase("SELECT date('2024-06-15', X'2B3120646179'), strftime(x'2559', '2024-01-01')")]
    [TestCase("SELECT julianday(x'323032342D30312D3031'), time(x'31323A30303A3030')")]
    [TestCase("SELECT datetime(x'323032342D30312D3031', x'2B31206D6F6E7468')")]
    [TestCase("SELECT timediff(X'323032342D30312D3032', X'323032342D30312D3031')")]
    [TestCase("SELECT date(x'FF'), date('2024-01-01', x'FF'), timediff(x'FF', '2024-01-01')")]
    // Upstream 535f684f1: whole Unix seconds round toward negative infinity.
    [TestCase("SELECT unixepoch('1969-12-31 23:59:59.999'), strftime('%s', '1969-12-31T23:59:59.999')")]
    [TestCase("SELECT unixepoch('1969-12-31 23:59:59.001'), unixepoch('1969-12-31 23:59:59.999', 'subsec')")]
    [TestCase("SELECT date(unixepoch('1969-12-31T23:59:59.999Z'), 'unixepoch')")]
    [TestCase("SELECT strftime('%s', '1969-12-31 23:59:58.500'), unixepoch('1969-12-31 23:59:58.500')")]
    [TestCase("SELECT strftime('%s', '0000-01-01'), unixepoch('-4713-11-24 12:00:00'), unixepoch('1970-01-01 00:00:00.999')")]
    // Upstream 16a02b139: json_each/json_tree number rows from zero and expose them as rowid.
    [TestCase("SELECT rowid, key FROM json_each('[5,6,7]')")]
    [TestCase("SELECT rowid, fullkey FROM json_tree('{\"a\":[1,2]}')")]
    [TestCase("SELECT rt.rowid, rt.oid, rt._rowid_ FROM json_tree('{\"a\":[1,2]}') AS rt WHERE rt.type NOT IN ('object','array')")]
    [TestCase("SELECT rowid FROM json_each('[1,2]') ORDER BY rowid DESC")]
    [TestCase("SELECT max(rowid), count(rowid) FROM json_tree('[1,[2,3]]')")]
    [TestCase("SELECT je.rowid, je.value FROM (SELECT '[1,2]' AS j UNION ALL SELECT '[3]') AS t, json_each(t.j) AS je")]
    [TestCase("SELECT je.rowid, je.value FROM json_each('[1,2]') AS je JOIN json_each('[9]') AS k ON 1")]
    [TestCase("SELECT (SELECT max(rowid) FROM json_each('[1,2,3]'))")]
    // Upstream 517ec809f and 5775d5337: NULL object labels are skipped, jsonb(NULL) is NULL.
    [TestCase("SELECT json_group_object(k, v) FROM (SELECT 'a' AS k, 1 AS v UNION ALL SELECT NULL, 2 UNION ALL SELECT 'b', 3)")]
    [TestCase("SELECT json_group_object(NULL, 1), json(jsonb_group_object(NULL, 1)), json_group_object('a', NULL)")]
    [TestCase("SELECT json_group_object(k, v) FROM (SELECT NULL AS k, x'00' AS v)")]
    [TestCase("SELECT typeof(jsonb(NULL)), jsonb(NULL) IS NULL, hex(jsonb('null'))")]
    public void MatchesSqlite(string sql)
    {
        RunManaged(sql).Should().Equal(RunSqlite(sql), because: sql);
    }

    [Test]
    public void JsonTreeRowIdCanBeStoredThroughInsertSelect()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE kv(n, key, val);");
        Execute(
            connection,
            "INSERT INTO kv SELECT rt.rowid, rt.fullkey, rt.atom FROM json_tree('{\"a\":[1,2]}') AS rt "
            + "WHERE rt.type NOT IN ('object','array');");

        RunManaged(connection, "SELECT n, key, val FROM kv ORDER BY key;")
            .Should().Equal("i:2|t:$.a[0]|i:1", "i:3|t:$.a[1]|i:2");
    }

    [Test]
    public void GenerateSeriesReportsEachValueAsItsRowId()
    {
        // SQLite's series extension (not bundled with the oracle build) and Turso's
        // core/series.rs both use the generated value as the rowid.
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        RunManaged(connection, "SELECT rowid, value FROM generate_series(3, 5);")
            .Should().Equal("i:3|i:3", "i:4|i:4", "i:5|i:5");
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static List<string> RunManaged(string sql)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        return RunManaged(connection, sql);
    }

    private static List<string> RunManaged(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<string>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var cells = new string[statement.ColumnCount];
            for (var index = 0; index < cells.Length; index++)
                cells[index] = Format(statement.GetValue(index));
            rows.Add(string.Join("|", cells));
        }

        return rows;
    }

    private static List<string> RunSqlite(string sql)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            var cells = new string[reader.FieldCount];
            for (var index = 0; index < cells.Length; index++)
            {
                cells[index] = reader.GetValue(index) switch
                {
                    DBNull => Format(SqlValue.Null),
                    long integer => Format(SqlValue.Integer(integer)),
                    double real => Format(SqlValue.Real(real)),
                    string text => Format(SqlValue.Text(text)),
                    byte[] blob => Format(SqlValue.Blob(blob)),
                    var other => throw new InvalidOperationException($"Unexpected SQLite value {other}."),
                };
            }

            rows.Add(string.Join("|", cells));
        }

        return rows;
    }

    private static string Format(SqlValue value)
        => value.Kind switch
        {
            SqlValueKind.Null => "null",
            SqlValueKind.Integer => "i:" + value.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture),
            SqlValueKind.Real => "r:" + value.AsReal().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            SqlValueKind.Text => "t:" + value.AsText(),
            SqlValueKind.Blob => "b:" + Convert.ToHexString(value.AsBlob().Span),
            _ => throw new InvalidOperationException($"Unknown value kind {value.Kind}."),
        };
}
