using AwesomeAssertions;
using Ahtola.Core;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

public class QualifiedStarSqlRoutingTests
{
    [Test]
    public void AliasedSingleTableQualifiedStarRoutesAndMatchesSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE sales(id INTEGER, amount INTEGER, note TEXT);",
            "INSERT INTO sales VALUES (1, 10, 'first'), (2, 5, 'skip'), (3, 20, 'last');",
        ];
        const string query = "SELECT s.* FROM sales AS s WHERE s.amount >= 10;";

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        var managed = ReadRows(connection, query);
        managed.Should().HaveCount(2);
        managed[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(10), SqlValue.Text("first"));
        managed[1].Should().Equal(SqlValue.Integer(3), SqlValue.Integer(20), SqlValue.Text("last"));
        AssertMatchesSqlite(managed, setup, query);

        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN " + query)).ToList();
        opcodes.Should().Contain("OpenReadCursor")
            .And.Contain("Rewind")
            .And.Contain("JumpIfNotTrue")
            .And.Contain("ResultRow")
            .And.Contain("Next")
            .And.Contain("CloseCursor")
            .And.Contain("Halt");
        // Three projected columns plus the one the predicate loads for its comparison.
        opcodes.Count(opcode => opcode == "Column").Should().Be(4);
    }

    [Test]
    public void QualifiedStarOnlyRoutesForTheResolvedSingleTableQualifier()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE sales(id INTEGER, amount INTEGER);");
        Execute(connection, "INSERT INTO sales VALUES (2, 20), (1, 10);");

        // A resolved qualified star expands to the base table's declared columns before sorting.
        var ordered = ReadRows(connection, "SELECT s.* FROM sales AS s ORDER BY s.id;");
        ordered.Should().HaveCount(2);
        ordered[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(10));
        ordered[1].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(20));
        Opcodes(ReadRows(connection, "EXPLAIN SELECT s.* FROM sales AS s ORDER BY s.id;"))
            .Should().Contain("OpenSorter").And.Contain("SorterSort");

        // Once an alias is present, SQLite does not permit the underlying table name as a qualifier.
        // Declining lets the evaluator retain that exact diagnostic.
        var error = Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "SELECT sales.* FROM sales AS s;"))!;
        error.Message.Should().Be("no such table: sales");
        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT sales.* FROM sales AS s;"));
    }

    [Test]
    public void OrderedBoundedQualifiedStarRoutesThroughSorterAndMatchesSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE sales(id INTEGER, amount INTEGER, note TEXT);",
            "INSERT INTO sales VALUES (1, 20, 'first'), (2, 10, 'second'), (3, 20, 'last'), (4, 5, 'skip');",
        ];
        const string query =
            "SELECT s.* FROM sales AS s WHERE s.amount >= 10 ORDER BY s.amount DESC, s.id LIMIT 2 OFFSET 1;";

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        var managed = ReadRows(connection, query);
        managed.Should().HaveCount(2);
        managed[0].Should().Equal(SqlValue.Integer(3), SqlValue.Integer(20), SqlValue.Text("last"));
        managed[1].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(10), SqlValue.Text("second"));
        AssertMatchesSqlite(managed, setup, query);

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should().Equal(
            "LoadConstant", "LoadConstant",
            "OpenReadCursor", "OpenSorter", "Rewind", "Filter",
            "Column", "Column", "Column", "RowId", "SorterInsert", "Next", "CloseCursor",
            "SorterSort", "SorterData", "Copy", "Copy", "Copy",
            "OffsetGate", "LimitGate", "ResultRow", "SorterNext", "CloseSorter", "Halt");
    }

    [Test]
    public void OrderedQualifiedStarWithUnresolvedQualifierFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE sales(id INTEGER, amount INTEGER);");
        Execute(connection, "INSERT INTO sales VALUES (1, 10);");

        const string query = "SELECT sales.* FROM sales AS s ORDER BY s.id LIMIT 1;";

        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + query));
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, query))!
            .Message.Should().Be("no such table: sales");
    }

    private static void AssertMatchesSqlite(
        IReadOnlyList<SqlValue[]> managed,
        IReadOnlyList<string> setup,
        string query)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var statement in setup)
        {
            using var setupCommand = connection.CreateCommand();
            setupCommand.CommandText = statement;
            setupCommand.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = query;
        using var reader = command.ExecuteReader();
        var row = 0;
        while (reader.Read())
        {
            managed.Should().HaveCountGreaterThan(row);
            managed[row].Should().HaveCount(reader.FieldCount);
            for (var column = 0; column < reader.FieldCount; column++)
            {
                var reference = reader.IsDBNull(column) ? null : reader.GetValue(column);
                CellsShouldMatch(managed[row][column], reference);
            }

            row++;
        }

        managed.Should().HaveCount(row);
    }

    private static void CellsShouldMatch(SqlValue managed, object? reference)
    {
        switch (reference)
        {
            case null:
                managed.Kind.Should().Be(SqlValueKind.Null);
                break;
            case long integer:
                managed.Should().Be(SqlValue.Integer(integer));
                break;
            case string text:
                managed.Should().Be(SqlValue.Text(text));
                break;
            default:
                Assert.Fail($"Unexpected SQLite value type {reference.GetType().Name}.");
                break;
        }
    }

    private static IEnumerable<string> Opcodes(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[1].AsText());

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
            var row = new SqlValue[statement.GetColumnCount()];
            for (var ordinal = 0; ordinal < row.Length; ordinal++)
                row[ordinal] = statement.GetValue(ordinal);

            rows.Add(row);
        }

        return rows;
    }
}
