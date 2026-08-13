using System.Globalization;
using AwesomeAssertions;
using Ahtola.Core;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

// Focused coverage for the managed engine's DML RETURNING clause and aggregate window
// functions. Behavioural cases are cross-checked against a real SQLite build so the
// bounded subset stays byte-for-byte compatible where it claims support, and the
// rejection cases pin the exact boundaries of that subset.
public class WindowAndReturningTests
{
    [Test]
    public void InsertReturningProjectsColumnsAndExpressions()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER, price INTEGER);");

        var rows = ReadRows(connection, "INSERT INTO items(id, price) VALUES (1, 10), (2, 20) RETURNING id, price, price * 2 AS doubled;");

        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(10), SqlValue.Integer(20));
        rows[1].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(20), SqlValue.Integer(40));
    }

    [Test]
    public void InsertReturningReportsRowsAffectedAndColumnMetadata()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER, price INTEGER);");

        using var statement = connection.Prepare("INSERT INTO items(id, price) VALUES (1, 10) RETURNING id, price AS cost;");
        statement.GetColumnName(0).Should().Be("id");
        statement.GetColumnName(1).Should().Be("cost");
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(1));
        statement.GetValue(1).Should().Be(SqlValue.Integer(10));
        statement.Step().Should().Be(StatementStepResult.Done);
        statement.RowsAffected.Should().Be(1);
    }

    [Test]
    public void UpdateReturningReflectsNewValues()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER, price INTEGER);");
        Execute(connection, "INSERT INTO items VALUES (1, 10), (2, 20), (3, 30);");

        var rows = ReadRows(connection, "UPDATE items SET price = price + 5 WHERE id <= 2 RETURNING id, price;");

        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(15));
        rows[1].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(25));
    }

    [Test]
    public void DeleteReturningReflectsRemovedRows()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER, price INTEGER);");
        Execute(connection, "INSERT INTO items VALUES (1, 10), (2, 20), (3, 30);");

        var rows = ReadRows(connection, "DELETE FROM items WHERE price >= 20 RETURNING *;");

        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(20));
        rows[1].Should().Equal(SqlValue.Integer(3), SqlValue.Integer(30));

        ReadRows(connection, "SELECT id FROM items;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(1));
    }

    [Test]
    public void ReturningStarUsesQualifiedTableName()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER, price INTEGER);");

        var rows = ReadRows(connection, "INSERT INTO items VALUES (7, 70) RETURNING items.*;");

        rows.Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(7), SqlValue.Integer(70));
    }

    [Test]
    public void ReturningRejectsUnknownQualifiedStar()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER, price INTEGER);");

        using var statement = connection.Prepare("INSERT INTO items VALUES (1, 10) RETURNING other.*;");
        Assert.Throws<EmbeddedSqlException>(() => statement.Step());
    }

    [Test]
    public void ReturningRejectsAggregateFunctions()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER, price INTEGER);");

        using var statement = connection.Prepare("INSERT INTO items VALUES (1, 10) RETURNING sum(price);");
        Assert.Throws<EmbeddedSqlException>(() => statement.Step());
    }

    [Test]
    public void ReturningRejectsWindowFunctions()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER, price INTEGER);");

        using var statement = connection.Prepare("INSERT INTO items VALUES (1, 10) RETURNING sum(price) OVER ();");
        Assert.Throws<EmbeddedSqlException>(() => statement.Step());
    }

    [Test]
    public void ReturningIsRejectedInsideTriggerBodies()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER);");
        Execute(connection, "CREATE TABLE log(id INTEGER);");

        Assert.Throws<EmbeddedSqlException>(() => connection.Prepare(
            "CREATE TRIGGER t AFTER INSERT ON items BEGIN " +
            "INSERT INTO log VALUES (new.id) RETURNING id; END;"));
    }

    [Test]
    public void FailedInsertWithReturningIsStatementAtomic()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, price INTEGER);");
        Execute(connection, "INSERT INTO items VALUES (1, 10);");

        using (var statement = connection.Prepare("INSERT INTO items VALUES (2, 20), (1, 99) RETURNING id, price;"))
        {
            Assert.Throws<EmbeddedSqlException>(() => statement.Step());
        }

        // The duplicate key must roll back the whole statement, including the row that
        // would otherwise have been returned.
        var rows = ReadRows(connection, "SELECT id, price FROM items ORDER BY id;");
        rows.Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(1), SqlValue.Integer(10));
    }

    [Test]
    public void ReturningWorksInsideExplicitTransactionAndHonoursRollback()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER, price INTEGER);");

        Execute(connection, "BEGIN;");
        var rows = ReadRows(connection, "INSERT INTO items VALUES (1, 10), (2, 20) RETURNING id, price;");
        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(10));
        Execute(connection, "ROLLBACK;");

        // RETURNING surfaced the rows while the transaction was open, but the rollback
        // must still discard them.
        ReadRows(connection, "SELECT id FROM items;").Should().BeEmpty();
    }

    [Test]
    public void UpdateReturningRespectsUniqueIndexAtomicity()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER, code INTEGER);");
        Execute(connection, "CREATE UNIQUE INDEX items_code ON items(code);");
        Execute(connection, "INSERT INTO items VALUES (1, 100), (2, 200);");

        using (var statement = connection.Prepare("UPDATE items SET code = 200 WHERE id = 1 RETURNING id, code;"))
        {
            Assert.Throws<EmbeddedSqlException>(() => statement.Step());
        }

        // The unique-index violation rolls the statement back entirely; no row is changed
        // and nothing is returned.
        var rows = ReadRows(connection, "SELECT id, code FROM items ORDER BY id;");
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(100));
        rows[1].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(200));
    }

    [Test]
    public void InsertReturningFiresTriggersAfterCapturingRows()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER);");
        Execute(connection, "CREATE TABLE audit(marker INTEGER);");
        Execute(connection, "CREATE TRIGGER log_insert AFTER INSERT ON items BEGIN INSERT INTO audit VALUES (1); END;");

        var rows = ReadRows(connection, "INSERT INTO items VALUES (5) RETURNING id;");
        rows.Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(5));

        // Trigger side effects are still applied atomically with the base statement.
        ReadRows(connection, "SELECT marker FROM audit;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(1));
    }

    [Test]
    public void ReturningTargetSubqueriesObserveEachMutation()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE updated(id INTEGER PRIMARY KEY, value INTEGER);");
        Execute(connection, "INSERT INTO updated VALUES (1, 10), (2, 20), (3, 30);");
        var updatedRows = ReadRows(
            connection,
            """
            UPDATE updated SET value = value + 1 WHERE id < 3
            RETURNING id, (SELECT count(*) FROM updated WHERE value >= 21);
            """);
        updatedRows.Should().HaveCount(2);
        updatedRows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(1));
        updatedRows[1].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(2));

        Execute(connection, "CREATE TABLE deleted(id INTEGER PRIMARY KEY);");
        Execute(connection, "INSERT INTO deleted VALUES (1), (2), (3);");
        var deletedRows = ReadRows(
            connection,
            "DELETE FROM deleted WHERE id < 3 RETURNING id, (SELECT count(*) FROM deleted);");
        deletedRows.Should().HaveCount(2);
        deletedRows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));
        deletedRows[1].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(1));

        Execute(connection, "CREATE TABLE inserted(id INTEGER);");
        Execute(connection, "INSERT INTO inserted VALUES (1);");
        var insertedRows = ReadRows(
            connection,
            "INSERT INTO inserted VALUES (2), (3) RETURNING id, (SELECT count(*) FROM inserted);");
        insertedRows.Should().HaveCount(2);
        insertedRows[0].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(2));
        insertedRows[1].Should().Equal(SqlValue.Integer(3), SqlValue.Integer(3));
    }

    [Test]
    public void UpdateReturningObservesSameRowAfterTriggerWrites()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, value INTEGER);");
        Execute(connection, "INSERT INTO items VALUES (1, 10);");
        Execute(
            connection,
            """
            CREATE TRIGGER adjust AFTER UPDATE ON items BEGIN
                UPDATE items SET value = value + 100 WHERE id = NEW.id;
            END;
            """);

        ReadRows(
                connection,
                """
                UPDATE items SET value = 20 WHERE id = 1
                RETURNING value, (SELECT value FROM items AS current WHERE current.id = items.id);
                """)
            .Should()
            .ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(120), SqlValue.Integer(120));
    }

    [Test]
    public void RunningSumMatchesSqlite()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(id INTEGER, value INTEGER);",
                "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30), (4, 40);",
            ],
            "SELECT id, sum(value) OVER (ORDER BY id) AS running FROM t ORDER BY id;");
    }

    [Test]
    public void PartitionedAggregatesResetPerPartitionLikeSqlite()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE sales(region TEXT, amount INTEGER);",
                "INSERT INTO sales VALUES ('west', 10), ('west', 20), ('east', 5), ('east', 15), ('east', 25);",
            ],
            "SELECT region, amount, " +
            "sum(amount) OVER (PARTITION BY region ORDER BY amount) AS running, " +
            "count(*) OVER (PARTITION BY region) AS region_count " +
            "FROM sales ORDER BY region, amount;");
    }

    [Test]
    public void RowsFrameBetweenPrecedingAndFollowingMatchesSqlite()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(id INTEGER, value INTEGER);",
                "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30), (4, 40), (5, 50);",
            ],
            "SELECT id, sum(value) OVER (ORDER BY id ROWS BETWEEN 1 PRECEDING AND 1 FOLLOWING) AS windowed FROM t ORDER BY id;");
    }

    [Test]
    public void RowsUnboundedPrecedingToCurrentRowMatchesSqlite()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(id INTEGER, value INTEGER);",
                "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30), (4, 40);",
            ],
            "SELECT id, avg(value) OVER (ORDER BY id ROWS UNBOUNDED PRECEDING) AS avg_running FROM t ORDER BY id;");
    }

    [Test]
    public void RowsBetweenUnboundedPrecedingAndUnboundedFollowingMatchesSqlite()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(id INTEGER, value INTEGER);",
                "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30);",
            ],
            "SELECT id, min(value) OVER (ORDER BY id ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING) AS lo, " +
            "max(value) OVER (ORDER BY id ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING) AS hi FROM t ORDER BY id;");
    }

    [Test]
    public void DefaultFrameIsPeerInclusiveLikeSqlite()
    {
        // Duplicate ORDER BY keys must share the same running total: the default frame is
        // RANGE UNBOUNDED PRECEDING AND CURRENT ROW, which includes following peers.
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(id INTEGER, grp INTEGER, value INTEGER);",
                "INSERT INTO t VALUES (1, 1, 10), (2, 1, 20), (3, 2, 30), (4, 2, 40), (5, 2, 5);",
            ],
            "SELECT id, sum(value) OVER (ORDER BY grp) AS running FROM t ORDER BY id;");
    }

    [Test]
    public void WholePartitionWhenNoOrderByMatchesSqlite()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(id INTEGER, grp INTEGER, value INTEGER);",
                "INSERT INTO t VALUES (1, 1, 10), (2, 1, 20), (3, 2, 30), (4, 2, 40);",
            ],
            "SELECT id, sum(value) OVER (PARTITION BY grp) AS grp_total FROM t ORDER BY id;");
    }

    [Test]
    public void RowsFollowingFrameMatchesSqlite()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(id INTEGER, value INTEGER);",
                "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30), (4, 40);",
            ],
            "SELECT id, sum(value) OVER (ORDER BY id ROWS BETWEEN CURRENT ROW AND 2 FOLLOWING) AS ahead FROM t ORDER BY id;");
    }

    [Test]
    public void WindowFilterClauseMatchesSqlite()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(id INTEGER, value INTEGER);",
                "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30), (4, 40);",
            ],
            "SELECT id, sum(value) FILTER (WHERE value > 15) OVER (ORDER BY id) AS filtered FROM t ORDER BY id;");
    }

    [Test]
    public void GroupConcatWindowMatchesSqlite()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(id INTEGER, label TEXT);",
                "INSERT INTO t VALUES (1, 'a'), (2, 'b'), (3, 'c');",
            ],
            "SELECT id, group_concat(label, '|') OVER (ORDER BY id ROWS UNBOUNDED PRECEDING) AS acc FROM t ORDER BY id;");
    }

    [Test]
    public void OrderByOnWindowValueMatchesSqlite()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(id INTEGER, value INTEGER);",
                "INSERT INTO t VALUES (1, 30), (2, 10), (3, 20);",
            ],
            "SELECT id, sum(value) OVER (PARTITION BY id) AS total FROM t ORDER BY total DESC, id;");
    }

    [Test]
    public void RangeFrameMatchesSqlite()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(id INTEGER, value INTEGER);",
                "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30);",
            ],
            "SELECT sum(value) OVER (ORDER BY id RANGE BETWEEN 1 PRECEDING AND CURRENT ROW) FROM t;");
    }

    [Test]
    public void GroupsFrameMatchesSqlite()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(id INTEGER, value INTEGER);",
                "INSERT INTO t VALUES (1, 10), (1, 20), (2, 30);",
            ],
            "SELECT sum(value) OVER (ORDER BY id GROUPS BETWEEN 1 PRECEDING AND CURRENT ROW) FROM t;");
    }

    [Test]
    public void ExcludeClauseMatchesSqlite()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(id INTEGER, value INTEGER);",
                "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30);",
            ],
            "SELECT sum(value) OVER (ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW EXCLUDE CURRENT ROW) FROM t;");
    }

    [Test]
    public void MissingNamedWindowReferenceIsRejected()
    {
        AssertRejected("SELECT sum(value) OVER w FROM t;");
    }

    [Test]
    public void WindowRejectsDistinctArgument()
    {
        AssertRejected("SELECT sum(DISTINCT value) OVER (ORDER BY id) FROM t;");
    }

    [Test]
    public void RankingFunctionsMatchSqlite()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(id INTEGER, value INTEGER);",
                "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30);",
            ],
            "SELECT row_number() OVER (ORDER BY id), rank() OVER (ORDER BY value) FROM t;");
    }

    [Test]
    public void LagFunctionMatchesSqlite()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(id INTEGER, value INTEGER);",
                "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30);",
            ],
            "SELECT lag(value) OVER (ORDER BY id), lead(value, 1, -1) OVER (ORDER BY id) FROM t;");
    }

    [Test]
    public void WindowRejectedInWhereClause()
    {
        AssertRejected("SELECT id FROM t WHERE sum(value) OVER (ORDER BY id) > 10;");
    }

    [Test]
    public void WindowRejectsNegativeFrameOffset()
    {
        AssertRejected("SELECT sum(value) OVER (ORDER BY id ROWS -1 PRECEDING) FROM t;");
    }

    [Test]
    public void WindowCombinedWithGroupByMatchesSqlite()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(id INTEGER, value INTEGER);",
                "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30);",
            ],
            "SELECT sum(value) OVER (ORDER BY id), count(*) FROM t GROUP BY id;");
    }

    private static void AssertRejected(string query)
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30);");

        Assert.Throws<EmbeddedSqlException>(() =>
        {
            using var statement = connection.Prepare(query);
            while (statement.Step() == StatementStepResult.Row)
            {
            }
        });
    }

    private static void AssertMatchesSqlite(IReadOnlyList<string> setup, string query)
    {
        var managed = RunManaged(setup, query);
        var reference = RunSqlite(setup, query);

        managed.Should().HaveCount(reference.Count);
        for (var row = 0; row < reference.Count; row++)
        {
            managed[row].Should().HaveCount(reference[row].Length, "row {0} width should match SQLite", row);
            for (var column = 0; column < reference[row].Length; column++)
                CellsShouldMatch(managed[row][column], reference[row][column], row, column);
        }
    }

    private static List<SqlValue[]> RunManaged(IReadOnlyList<string> setup, string query)
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        return ReadRows(connection, query);
    }

    private static List<object?[]> RunSqlite(IReadOnlyList<string> setup, string query)
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
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var values = new object?[reader.FieldCount];
            for (var column = 0; column < values.Length; column++)
                values[column] = reader.IsDBNull(column) ? null : reader.GetValue(column);

            rows.Add(values);
        }

        return rows;
    }

    private static void CellsShouldMatch(SqlValue managed, object? reference, int row, int column)
    {
        var because = $"cell ({row},{column}) should match SQLite";
        if (reference is null)
        {
            managed.Kind.Should().Be(SqlValueKind.Null, because);
            return;
        }

        switch (reference)
        {
            case long integer:
                ToDouble(managed).Should().Be(integer, because);
                break;
            case double real:
                ToDouble(managed).Should().BeApproximately(real, 1e-9, because);
                break;
            case string text:
                managed.Kind.Should().Be(SqlValueKind.Text, because);
                managed.AsText().Should().Be(text, because);
                break;
            default:
                managed.ToString().Should().Be(reference.ToString(), because);
                break;
        }
    }

    private static double ToDouble(SqlValue value)
        => value.Kind switch
        {
            SqlValueKind.Integer => value.AsInteger(),
            SqlValueKind.Real => value.AsReal(),
            SqlValueKind.Text => double.Parse(value.AsText(), CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException($"Value {value.Kind} is not numeric."),
        };

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
            var values = new SqlValue[statement.ColumnCount];
            for (var ordinal = 0; ordinal < values.Length; ordinal++)
                values[ordinal] = statement.GetValue(ordinal);

            rows.Add(values);
        }

        return rows;
    }
}
