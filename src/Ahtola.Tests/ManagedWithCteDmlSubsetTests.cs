using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

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

    [Test]
    public void WritableCteExecutesOnStepMaterializesReturningAndRunsOnce()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, value INTEGER);");
        Execute(connection, "INSERT INTO target VALUES (1, 10), (2, 20), (3, 30);");

        using var statement = connection.Prepare("""
            WITH changed AS (
                UPDATE target
                SET value = value + 5
                WHERE id <= 2
                RETURNING id, value
            )
            SELECT a.id, a.value, b.value
            FROM changed AS a
            JOIN changed AS b ON b.id = a.id
            ORDER BY a.id;
            """);

        statement.GetColumnName(0).Should().Be("id");
        Scalar(connection, "SELECT sum(value) FROM target;").Should().Be(60);

        AssertRows(
            ReadRows(statement),
            [SqlValue.Integer(1), SqlValue.Integer(15), SqlValue.Integer(15)],
            [SqlValue.Integer(2), SqlValue.Integer(25), SqlValue.Integer(25)]);
        statement.RowsAffected.Should().Be(2);
        Scalar(connection, "SELECT sum(value) FROM target;").Should().Be(70);
        Scalar(connection, "SELECT changes();").Should().Be(2);
    }

    [Test]
    public void WritableCtesRunEagerlyAndRollbackTogetherOnFailure()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, value INTEGER);");
        Execute(connection, "INSERT INTO target VALUES (1, 10);");

        Execute(connection, """
            WITH unused AS (
                UPDATE target SET value = value + 1 RETURNING id
            )
            SELECT 1;
            """);
        Scalar(connection, "SELECT value FROM target;").Should().Be(11);

        using var failed = connection.Prepare("""
            WITH changed AS (
                UPDATE target SET value = value + 100 RETURNING id
            ),
            duplicate AS (
                INSERT INTO target VALUES (1, 999) RETURNING id
            )
            SELECT * FROM changed;
            """);
        Assert.Throws<EmbeddedSqlException>(() => failed.Step())!
            .Message.Should().Contain("UNIQUE constraint failed");
        Scalar(connection, "SELECT value FROM target;").Should().Be(11);
    }

    [Test]
    public void WritableCtesHonorTriggersForeignKeysAndSavepointRollback()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "PRAGMA foreign_keys = ON;");
        Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY, value INTEGER);");
        Execute(connection, "CREATE TABLE child(parent_id INTEGER REFERENCES parent(id));");
        Execute(connection, "CREATE TABLE audit(parent_id INTEGER, value INTEGER);");
        Execute(connection, "INSERT INTO parent VALUES (1, 10);");
        Execute(connection, "INSERT INTO child VALUES (1);");
        Execute(connection, """
            CREATE TRIGGER parent_audit AFTER UPDATE ON parent
            BEGIN
                INSERT INTO audit VALUES (NEW.id, NEW.value);
            END;
            """);

        Execute(connection, "BEGIN;");
        Execute(connection, "SAVEPOINT writable_cte;");
        using (var changed = connection.Prepare("""
                   WITH changed AS (
                       UPDATE parent SET value = value + 1 RETURNING id, value
                   )
                   SELECT id, value FROM changed;
                   """))
        {
            AssertRows(ReadRows(changed), [SqlValue.Integer(1), SqlValue.Integer(11)]);
            changed.RowsAffected.Should().Be(1);
        }
        Scalar(connection, "SELECT value FROM parent;").Should().Be(11);
        Scalar(connection, "SELECT count(*) FROM audit;").Should().Be(1);
        Execute(connection, "ROLLBACK TO writable_cte;");
        Execute(connection, "RELEASE writable_cte;");
        Execute(connection, "COMMIT;");
        Scalar(connection, "SELECT value FROM parent;").Should().Be(10);
        Scalar(connection, "SELECT count(*) FROM audit;").Should().Be(0);

        using var failed = connection.Prepare("""
            WITH changed AS (
                UPDATE parent SET value = value + 100 RETURNING id
            ),
            removed AS (
                DELETE FROM parent WHERE id = 1 RETURNING id
            )
            SELECT * FROM changed;
            """);
        Assert.Throws<EmbeddedSqlException>(() => failed.Step())!
            .Message.Should().Contain("FOREIGN KEY constraint failed");
        Scalar(connection, "SELECT value FROM parent;").Should().Be(10);
        Scalar(connection, "SELECT count(*) FROM audit;").Should().Be(0);
    }

    [Test]
    public void WritableCteNestedInDerivedTableDescribesBodyWithoutExecutingDuringPrepare()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, value INTEGER);");
        Execute(connection, "INSERT INTO target VALUES (1, 10);");

        using var statement = connection.Prepare("""
            SELECT id, value
            FROM (
                WITH changed AS (
                    UPDATE target SET value = value + 1 RETURNING id, value
                )
                SELECT id, value FROM changed
            );
            """);

        statement.GetColumnName(0).Should().Be("id");
        statement.GetColumnName(1).Should().Be("value");
        Scalar(connection, "SELECT value FROM target;").Should().Be(10);
        AssertRows(ReadRows(statement), [SqlValue.Integer(1), SqlValue.Integer(11)]);
        Scalar(connection, "SELECT value FROM target;").Should().Be(11);
    }

    [Test]
    public void WritableCteNestedInViewDescribesBodyWithoutExecutingDuringPrepare()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, value INTEGER);");
        Execute(connection, "INSERT INTO target VALUES (1, 10);");
        Execute(connection, """
            CREATE VIEW changed_target AS
            WITH changed AS (
                UPDATE target SET value = value + 1 RETURNING id, value
            )
            SELECT id, value FROM changed;
            """);

        using var statement = connection.Prepare("SELECT id, value FROM changed_target;");

        statement.GetColumnName(0).Should().Be("id");
        statement.GetColumnName(1).Should().Be("value");
        Scalar(connection, "SELECT value FROM target;").Should().Be(10);
        AssertRows(ReadRows(statement), [SqlValue.Integer(1), SqlValue.Integer(11)]);
        Scalar(connection, "SELECT value FROM target;").Should().Be(11);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NestedWritableCtePersistsAcrossFileReopen(bool throughView)
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "nested-writable-cte.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            CreateNestedWritableCteFixture(connection, throughView);
            using var statement = connection.Prepare(NestedWritableCteSql(throughView));
            AssertRows(ReadRows(statement), [SqlValue.Integer(1), SqlValue.Integer(11)]);
            statement.RowsAffected.Should().Be(1);
            Scalar(connection, "SELECT changes();").Should().Be(1);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var reopenedConnection = reopened.Connect();
        Scalar(reopenedConnection, "SELECT value FROM target;").Should().Be(11);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void QueryOnlyRejectsNestedWritableCte(bool throughView)
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        CreateNestedWritableCteFixture(connection, throughView);
        Execute(connection, "PRAGMA query_only = ON;");

        using var statement = connection.Prepare(NestedWritableCteSql(throughView));
        Assert.Throws<EmbeddedSqlException>(() => statement.Step())!
            .Message.Should().Contain("readonly database");
        Scalar(connection, "SELECT value FROM target;").Should().Be(10);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ReadOnlyDatabaseRejectsNestedWritableCte(bool throughView)
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "readonly-nested-writable-cte.db";
        using (var writable = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var setup = writable.Connect())
            CreateNestedWritableCteFixture(setup, throughView);

        using var database = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var connection = database.Connect();
        using var statement = connection.Prepare(NestedWritableCteSql(throughView));
        Assert.Throws<EmbeddedSqlException>(() => statement.Step())!
            .Message.Should().Contain("readonly database");
        Scalar(connection, "SELECT value FROM target;").Should().Be(10);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TransactionRollbackRestoresNestedWritableCte(bool throughView)
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        CreateNestedWritableCteFixture(connection, throughView);

        Execute(connection, "BEGIN;");
        using (var statement = connection.Prepare(NestedWritableCteSql(throughView)))
        {
            AssertRows(ReadRows(statement), [SqlValue.Integer(1), SqlValue.Integer(11)]);
            statement.RowsAffected.Should().Be(1);
        }
        Scalar(connection, "SELECT value FROM target;").Should().Be(11);
        Execute(connection, "ROLLBACK;");

        Scalar(connection, "SELECT value FROM target;").Should().Be(10);
    }

    private static void CreateNestedWritableCteFixture(
        EmbeddedConnection connection,
        bool throughView)
    {
        Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, value INTEGER);");
        Execute(connection, "INSERT INTO target VALUES (1, 10);");
        if (throughView)
        {
            Execute(connection, """
                CREATE VIEW changed_target AS
                WITH changed AS (
                    UPDATE target SET value = value + 1 RETURNING id, value
                )
                SELECT id, value FROM changed;
                """);
        }
    }

    private static string NestedWritableCteSql(bool throughView)
        => throughView
            ? "SELECT id, value FROM changed_target;"
            : """
                SELECT id, value
                FROM (
                    WITH changed AS (
                        UPDATE target SET value = value + 1 RETURNING id, value
                    )
                    SELECT id, value FROM changed
                );
                """;

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

    private static long Scalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static void AssertRows(IReadOnlyList<SqlValue[]> actual, params SqlValue[][] expected)
    {
        actual.Should().HaveCount(expected.Length);
        for (var index = 0; index < expected.Length; index++)
            actual[index].Should().Equal(expected[index]);
    }
}
