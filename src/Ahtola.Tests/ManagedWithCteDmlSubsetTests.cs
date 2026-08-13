using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

public class ManagedWithCteDmlSubsetTests
{
    [Test]
    public void ManagedWithCteDmlInsertMaterializesRecursiveSourceAndReturnsRows()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, value INTEGER);");

        using var statement = connection.Prepare("""
            WITH RECURSIVE sequence(value) AS (
                SELECT ?1
                UNION ALL
                SELECT value + 1 FROM sequence WHERE value < ?2
            )
            INSERT INTO target(id, value)
            SELECT value, value * 10 FROM sequence
            RETURNING id, value;
            """);
        statement.Bind(1, SqlValue.Integer(1));
        statement.Bind(2, SqlValue.Integer(3));

        statement.GetColumnName(0).Should().Be("id");
        statement.GetColumnName(1).Should().Be("value");
        AssertRows(
            ReadRows(statement),
            [SqlValue.Integer(1), SqlValue.Integer(10)],
            [SqlValue.Integer(2), SqlValue.Integer(20)],
            [SqlValue.Integer(3), SqlValue.Integer(30)]);
        statement.RowsAffected.Should().Be(3);

        using var persisted = connection.Prepare("SELECT id, value FROM target ORDER BY id;");
        AssertRows(
            ReadRows(persisted),
            [SqlValue.Integer(1), SqlValue.Integer(10)],
            [SqlValue.Integer(2), SqlValue.Integer(20)],
            [SqlValue.Integer(3), SqlValue.Integer(30)]);
    }

    [Test]
    public void ManagedWithCteDmlUpdateUsesCtePredicateAndReturning()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, value INTEGER);");
        Execute(connection, "INSERT INTO target VALUES (1, 10), (2, 20), (3, 30);");

        using var statement = connection.Prepare("""
            WITH selected(id) AS (SELECT ?1)
            UPDATE target
            SET value = value + 100
            WHERE id IN (SELECT id FROM selected)
              AND id NOT IN (
                  WITH selected(id) AS (SELECT 1)
                  SELECT id FROM selected
              )
            RETURNING id, value;
            """);
        statement.Bind(1, SqlValue.Integer(2));

        AssertRows(ReadRows(statement), [SqlValue.Integer(2), SqlValue.Integer(120)]);
        statement.RowsAffected.Should().Be(1);

        using var persisted = connection.Prepare("SELECT value FROM target WHERE id = 2;");
        AssertRows(ReadRows(persisted), [SqlValue.Integer(120)]);
    }

    [Test]
    public void ManagedWithCteUpdateFromRematerializesReturningAfterTriggerMutation()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, value INTEGER);");
        Execute(connection, "CREATE TABLE source(id INTEGER PRIMARY KEY, bump INTEGER);");
        Execute(connection, "INSERT INTO target VALUES (1, 0), (2, 0), (3, 0);");
        Execute(connection, "INSERT INTO source VALUES (1, 1), (2, 2), (3, 3);");
        Execute(connection, """
            CREATE TRIGGER mutate_source BEFORE UPDATE ON target
            WHEN NEW.id = 1
            BEGIN
                UPDATE source SET bump = 100 WHERE id = 2;
            END;
            """);

        using var statement = connection.Prepare("""
            WITH c(id, bump) AS (SELECT id, bump FROM source)
            UPDATE target
            SET value = c.bump
            FROM c
            WHERE target.id = c.id
            RETURNING id, (SELECT bump FROM c WHERE c.id = target.id);
            """);

        AssertRows(
            ReadRows(statement),
            [SqlValue.Integer(1), SqlValue.Integer(1)],
            [SqlValue.Integer(2), SqlValue.Integer(100)],
            [SqlValue.Integer(3), SqlValue.Integer(3)]);
    }

    [Test]
    public void ManagedWithCteDmlDeleteMaterializesTargetRowsBeforeDeleting()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, value INTEGER);");
        Execute(connection, "INSERT INTO target VALUES (1, 10), (2, 20), (3, 30);");

        using var statement = connection.Prepare("""
            WITH doomed(id) AS (SELECT id FROM target WHERE value >= ?1)
            DELETE FROM target
            WHERE id IN (SELECT id FROM doomed)
            RETURNING id;
            """);
        statement.Bind(1, SqlValue.Integer(20));

        AssertRows(ReadRows(statement), [SqlValue.Integer(2)], [SqlValue.Integer(3)]);
        statement.RowsAffected.Should().Be(2);

        using var persisted = connection.Prepare("SELECT id FROM target;");
        AssertRows(ReadRows(persisted), [SqlValue.Integer(1)]);
    }

    [Test]
    public void ManagedWithCteDmlDoesNotLeakCtesIntoTriggerBodies()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY);");
        Execute(connection, "CREATE TABLE audit(id INTEGER PRIMARY KEY, mark INTEGER);");
        Execute(connection, "CREATE TABLE selected(id INTEGER PRIMARY KEY);");
        Execute(connection, "INSERT INTO audit VALUES (1, 0), (2, 0);");
        Execute(connection, "INSERT INTO selected VALUES (1);");
        Execute(connection, """
            CREATE TRIGGER record AFTER INSERT ON target BEGIN
                UPDATE audit SET mark = 1 WHERE id IN (SELECT id FROM selected);
            END;
            """);

        Execute(connection, """
            WITH selected(id) AS (SELECT 2)
            INSERT INTO target SELECT id FROM selected;
            """);

        using var audit = connection.Prepare("SELECT id, mark FROM audit ORDER BY id;");
        AssertRows(
            ReadRows(audit),
            [SqlValue.Integer(1), SqlValue.Integer(1)],
            [SqlValue.Integer(2), SqlValue.Integer(0)]);
    }

    [Test]
    public void ManagedWithCteDmlRollsBackFailuresKeepsCtesStatementLocalAndDefersUnusedCtes()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY);");
        Execute(connection, "INSERT INTO target VALUES (1);");

        using (var statement = connection.Prepare("""
            WITH attempted(id) AS (SELECT 2 UNION ALL SELECT 1)
            INSERT INTO target(id)
            SELECT id FROM attempted
            RETURNING id;
            """))
        {
            Assert.Throws<EmbeddedSqlException>(() => statement.Step())!
                .Message.Should().Contain("UNIQUE constraint failed");
        }

        using (var persisted = connection.Prepare("SELECT id FROM target;"))
            AssertRows(ReadRows(persisted), [SqlValue.Integer(1)]);

        using (var expired = connection.Prepare("SELECT id FROM attempted;"))
            Assert.Throws<EmbeddedSqlException>(() => expired.Step())!
                .Message.Should().Contain("no such table: attempted");

        Assert.Throws<EmbeddedSqlException>(() => connection.Prepare(
            "WITH attempted AS (INSERT INTO target VALUES (3) RETURNING id) SELECT * FROM attempted;"))!
            .Message.Should().Contain("writable CTEs are not supported");

        using var schemaQualified = connection.Prepare(
            "WITH attempted AS (SELECT id FROM main.target) INSERT INTO target SELECT id FROM attempted;");
        Assert.Throws<EmbeddedSqlException>(() => schemaQualified.Step())!
            .Message.Should().Contain("UNIQUE constraint failed");

        using (var unused = connection.Prepare(
                   "WITH unused(value) AS (VALUES (2, 4)) INSERT INTO target VALUES (3);"))
        {
            unused.Step().Should().Be(StatementStepResult.Done);
        }

        using var finalRows = connection.Prepare("SELECT id FROM target ORDER BY id;");
        AssertRows(ReadRows(finalRows), [SqlValue.Integer(1)], [SqlValue.Integer(3)]);
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    private static List<SqlValue[]> ReadRows(EmbeddedStatement statement)
    {
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
