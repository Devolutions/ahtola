using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

public sealed class ManagedBoundedUpsertRuntimeSliceTests
{
    [Test]
    public void UpsertInsertsAndDoesNothingOnPrimaryKeyConflict()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, label TEXT);");

        ReadRows(
                connection,
                "INSERT INTO items VALUES (1, 'first') ON CONFLICT(id) DO NOTHING RETURNING id, label;")
            .Should()
            .ContainSingle()
            .Which
            .Should()
            .Equal(SqlValue.Integer(1), SqlValue.Text("first"));
        connection.LastInsertRowId.Should().Be(1);

        using (var noOp = connection.Prepare(
                   "INSERT INTO items VALUES (1, 'ignored') ON CONFLICT(id) DO NOTHING RETURNING id;"))
        {
            noOp.Step().Should().Be(StatementStepResult.Done);
            noOp.RowsAffected.Should().Be(0);
        }

        connection.LastInsertRowId.Should().Be(1);
        ReadRows(connection, "SELECT id, label FROM items;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(1), SqlValue.Text("first"));
    }

    [Test]
    public void UpsertUpdateUsesTargetAndExcludedValues()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, quantity INTEGER, label TEXT);");
        Execute(connection, "INSERT INTO items VALUES (1, 3, 'old');");

        using var statement = connection.Prepare(
            """
            INSERT INTO items VALUES (1, 7, 'new')
            ON CONFLICT(id) DO UPDATE
            SET quantity = items.quantity + excluded.quantity, label = excluded.label
            RETURNING id, quantity, label;
            """);
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(1));
        statement.GetValue(1).Should().Be(SqlValue.Integer(10));
        statement.GetValue(2).Should().Be(SqlValue.Text("new"));
        statement.Step().Should().Be(StatementStepResult.Done);
        statement.RowsAffected.Should().Be(1);
        connection.LastInsertRowId.Should().Be(1);
    }

    [Test]
    public void UpsertFromSelectAndCteUsesTheSameConflictResolutionAsValues()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, value TEXT);");
        Execute(connection, "CREATE TABLE source(id INTEGER, value TEXT);");
        Execute(connection, "INSERT INTO items VALUES (1, 'old');");
        Execute(connection, "INSERT INTO source VALUES (1, 'updated'), (2, 'inserted');");

        AssertRows(
            ReadRows(
                connection,
                """
                INSERT INTO items SELECT id, value FROM source WHERE true
                ON CONFLICT(id) DO UPDATE SET value = excluded.value
                RETURNING id, value;
                """),
            [SqlValue.Integer(1), SqlValue.Text("updated")],
            [SqlValue.Integer(2), SqlValue.Text("inserted")]);

        AssertRows(
            ReadRows(
                connection,
                """
                WITH source_rows(id, value) AS (VALUES(1, 'cte-update'), (3, 'cte-insert'))
                INSERT INTO items SELECT id, value FROM source_rows WHERE true
                ON CONFLICT(id) DO UPDATE SET value = excluded.value
                RETURNING id, value;
                """),
            [SqlValue.Integer(1), SqlValue.Text("cte-update")],
            [SqlValue.Integer(3), SqlValue.Text("cte-insert")]);

        AssertRows(
            ReadRows(connection, "SELECT id, value FROM items ORDER BY id;"),
            [SqlValue.Integer(1), SqlValue.Text("cte-update")],
            [SqlValue.Integer(2), SqlValue.Text("inserted")],
            [SqlValue.Integer(3), SqlValue.Text("cte-insert")]);
    }

    [Test]
    public void UpsertUniqueConflictHonorsNoCaseAndNullsRemainDistinct()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE tokens(code TEXT COLLATE NOCASE, value TEXT);");
        Execute(connection, "CREATE UNIQUE INDEX tokens_code_unique ON tokens(code);");
        Execute(connection, "INSERT INTO tokens VALUES ('alpha', 'old');");

        ReadRows(
                connection,
                """
                INSERT INTO tokens VALUES ('ALPHA', 'new')
                ON CONFLICT(code) DO UPDATE SET value = excluded.value
                RETURNING code, value;
                """)
            .Should()
            .ContainSingle()
            .Which
            .Should()
            .Equal(SqlValue.Text("alpha"), SqlValue.Text("new"));

        Execute(connection, "INSERT INTO tokens VALUES (NULL, 'first-null') ON CONFLICT(code) DO NOTHING;");
        Execute(connection, "INSERT INTO tokens VALUES (NULL, 'second-null') ON CONFLICT(code) DO NOTHING;");
        AssertRows(
            ReadRows(connection, "SELECT value FROM tokens WHERE code IS NULL ORDER BY value;"),
            [SqlValue.Text("first-null")],
            [SqlValue.Text("second-null")]);
    }

    [Test]
    public void UpsertUpdateRecomputesGeneratedValuesFiresUpdateTriggerAndReturnsUpdatedRow()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, value INTEGER, doubled AS (value * 2));");
        Execute(connection, "CREATE TABLE audit(event TEXT);");
        Execute(
            connection,
            "CREATE TRIGGER item_update AFTER UPDATE ON items BEGIN INSERT INTO audit VALUES ('update'); END;");
        Execute(connection, "INSERT INTO items(id, value) VALUES (1, 3);");

        ReadRows(
                connection,
                """
                INSERT INTO items(id, value) VALUES (1, 10)
                ON CONFLICT(id) DO UPDATE SET value = excluded.value
                RETURNING value, doubled;
                """)
            .Should()
            .ContainSingle()
            .Which
            .Should()
            .Equal(SqlValue.Integer(10), SqlValue.Integer(20));
        AssertRows(ReadRows(connection, "SELECT event FROM audit;"), [SqlValue.Text("update")]);
    }

    [Test]
    public void ConditionalUpsertUpdateUsesTargetAndExcludedValuesAndSkipsFalseOrNullPredicates()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, value INTEGER);");
        Execute(connection, "CREATE TABLE audit(event TEXT);");
        Execute(
            connection,
            "CREATE TRIGGER item_update AFTER UPDATE ON items BEGIN INSERT INTO audit VALUES ('update'); END;");
        Execute(connection, "INSERT INTO items VALUES (1, 5);");

        ReadRows(
                connection,
                """
                INSERT INTO items VALUES (1, 10)
                ON CONFLICT(id) DO UPDATE SET value = excluded.value
                WHERE excluded.value > items.value
                RETURNING value;
                """)
            .Should()
            .ContainSingle()
            .Which
            .Should()
            .Equal(SqlValue.Integer(10));

        using (var skipped = connection.Prepare(
                   """
                   INSERT INTO items VALUES (1, 8)
                   ON CONFLICT(id) DO UPDATE SET value = excluded.value
                   WHERE excluded.value > items.value
                   RETURNING value;
                   """))
        {
            skipped.Step().Should().Be(StatementStepResult.Done);
            skipped.RowsAffected.Should().Be(0);
        }

        using (var skipped = connection.Prepare(
                   """
                   INSERT INTO items VALUES (1, 12)
                   ON CONFLICT(id) DO UPDATE SET value = excluded.value
                   WHERE NULL
                   RETURNING value;
                   """))
        {
            skipped.Step().Should().Be(StatementStepResult.Done);
            skipped.RowsAffected.Should().Be(0);
        }

        AssertRows(ReadRows(connection, "SELECT value FROM items;"), [SqlValue.Integer(10)]);
        AssertRows(ReadRows(connection, "SELECT event FROM audit;"), [SqlValue.Text("update")]);
    }

    [Test]
    public void MultiRowUpsertProcessesMixedActionsInOrderAndFiresEachStatementTriggerOnce()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(
            connection,
            "CREATE TABLE items(id INTEGER PRIMARY KEY, value INTEGER, doubled AS (value * 2));");
        Execute(connection, "CREATE TABLE audit(event TEXT);");
        Execute(
            connection,
            "CREATE TRIGGER item_insert AFTER INSERT ON items BEGIN INSERT INTO audit VALUES ('insert'); END;");
        Execute(
            connection,
            "CREATE TRIGGER item_update AFTER UPDATE ON items BEGIN INSERT INTO audit VALUES ('update'); END;");
        Execute(connection, "INSERT INTO items VALUES (1, 10);");
        Execute(connection, "DELETE FROM audit;");

        using var statement = connection.Prepare(
            """
            INSERT INTO items(id, value) VALUES (1, 11), (2, 20), (1, 8)
            ON CONFLICT(id) DO UPDATE SET value = excluded.value
            WHERE excluded.value > items.value
            RETURNING id, value, doubled;
            """);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            rows.Add(
            [
                statement.GetValue(0),
                statement.GetValue(1),
                statement.GetValue(2),
            ]);
        }

        AssertRows(
            rows,
            [SqlValue.Integer(1), SqlValue.Integer(11), SqlValue.Integer(22)],
            [SqlValue.Integer(2), SqlValue.Integer(20), SqlValue.Integer(40)]);
        statement.RowsAffected.Should().Be(2);
        connection.LastInsertRowId.Should().Be(2);
        AssertRows(
            ReadRows(connection, "SELECT id, value, doubled FROM items ORDER BY id;"),
            [SqlValue.Integer(1), SqlValue.Integer(11), SqlValue.Integer(22)],
            [SqlValue.Integer(2), SqlValue.Integer(20), SqlValue.Integer(40)]);
        AssertRows(
            ReadRows(connection, "SELECT event FROM audit;"),
            [SqlValue.Text("update")],
            [SqlValue.Text("insert")]);
    }

    [Test]
    public void MultiRowUpsertDoNothingSkipsConflictsAndRollsBackTheWholeStatementOnLaterFailure()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, code TEXT UNIQUE, value INTEGER);");
        Execute(connection, "INSERT INTO items VALUES (1, 'one', 1);");

        ReadRows(
                connection,
                """
                INSERT INTO items VALUES (1, 'ignored', 10), (2, 'two', 2)
                ON CONFLICT(id) DO NOTHING
                RETURNING id, code, value;
                """)
            .Should()
            .ContainSingle()
            .Which
            .Should()
            .Equal(SqlValue.Integer(2), SqlValue.Text("two"), SqlValue.Integer(2));

        Action conflict = () => Execute(
            connection,
            """
            INSERT INTO items VALUES (3, 'three', 3), (4, 'one', 4)
            ON CONFLICT(id) DO UPDATE SET value = excluded.value;
            """);
        conflict.Should().Throw<EmbeddedSqlException>().WithMessage("UNIQUE constraint failed: items.code");
        AssertRows(
            ReadRows(connection, "SELECT id, code, value FROM items ORDER BY id;"),
            [SqlValue.Integer(1), SqlValue.Text("one"), SqlValue.Integer(1)],
            [SqlValue.Integer(2), SqlValue.Text("two"), SqlValue.Integer(2)]);
    }

    [Test]
    public void UpsertConstraintFailureRollsBackTheWholeStatement()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, code TEXT UNIQUE, payload TEXT);");
        Execute(connection, "INSERT INTO items VALUES (1, 'one', 'original'), (2, 'two', 'other');");

        Action conflict = () => Execute(
            connection,
            """
            INSERT INTO items VALUES (1, 'two', 'changed')
            ON CONFLICT(id) DO UPDATE SET code = excluded.code, payload = excluded.payload;
            """);
        conflict.Should().Throw<EmbeddedSqlException>().WithMessage("UNIQUE constraint failed: items.code");

        AssertRows(
            ReadRows(connection, "SELECT id, code, payload FROM items ORDER BY id;"),
            [SqlValue.Integer(1), SqlValue.Text("one"), SqlValue.Text("original")],
            [SqlValue.Integer(2), SqlValue.Text("two"), SqlValue.Text("other")]);
    }

    [Test]
    public void TargetlessDoNothingAndDuplicateExactTargetsUseSQLiteInference()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, code TEXT UNIQUE, value INTEGER);");
        Execute(connection, "INSERT INTO items VALUES (1, 'one', 1);");

        Execute(connection, "INSERT INTO items VALUES (1, 'x', 2) ON CONFLICT DO NOTHING;");
        Execute(connection, "CREATE UNIQUE INDEX duplicate_code ON items(code);");
        Execute(
            connection,
            "INSERT INTO items VALUES (2, 'one', 2) ON CONFLICT(code) DO UPDATE SET value = excluded.value;");

        AssertRows(
            ReadRows(connection, "SELECT id, code, value FROM items;"),
            [SqlValue.Integer(1), SqlValue.Text("one"), SqlValue.Integer(2)]);
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static IReadOnlyList<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var row = new SqlValue[statement.ColumnCount];
            for (var index = 0; index < row.Length; index++)
                row[index] = statement.GetValue(index);
            rows.Add(row);
        }

        return rows;
    }

    private static void AssertRows(IReadOnlyList<SqlValue[]> actual, params SqlValue[][] expected)
    {
        actual.Should().HaveCount(expected.Length);
        for (var index = 0; index < expected.Length; index++)
            actual[index].Should().Equal(expected[index]);
    }
}
