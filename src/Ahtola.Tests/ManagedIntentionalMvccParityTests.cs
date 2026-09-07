using Ahtola.Core;
using AwesomeAssertions;

namespace Ahtola.Tests;

public class ManagedIntentionalMvccParityTests
{
    [TestCase("CREATE TABLE created(value);",
        "SELECT count(*) FROM sqlite_schema WHERE name='created';", 1)]
    [TestCase("CREATE INDEX indexed_value ON existing(value);",
        "SELECT count(*) FROM sqlite_schema WHERE name='indexed_value';", 1)]
    [TestCase("DROP TABLE existing;",
        "SELECT count(*) FROM sqlite_schema WHERE name='existing';", 0)]
    [TestCase("ALTER TABLE existing ADD COLUMN extra;",
        "SELECT count(*) FROM pragma_table_info('existing') WHERE name='extra';", 1)]
    public void ConcurrentDdlCommitsWithoutAPeerSnapshot(string ddl, string query, long expected)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "PRAGMA journal_mode=mvcc; CREATE TABLE existing(value); BEGIN CONCURRENT;");
        Execute(connection, ddl);
        Execute(connection, "COMMIT;");

        ReadValue(connection, query).Should().Be(SqlValue.Integer(expected));
    }

    [Test]
    public void ConcurrentRollbackDoesNotBurnAutoincrementRowids()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, """
            PRAGMA journal_mode=mvcc;
            CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, value);
            BEGIN CONCURRENT;
            INSERT INTO t(value) VALUES('rolled back');
            ROLLBACK;
            INSERT INTO t(value) VALUES('retained');
            """);

        ReadValue(connection, "SELECT id FROM t;").Should().Be(SqlValue.Integer(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ConcurrentRollbackRestoresSequenceWatermarks(bool allocateInsideInsert)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, """
            PRAGMA journal_mode=mvcc;
            CREATE SEQUENCE s START WITH 100;
            CREATE TABLE t(value);
            BEGIN CONCURRENT;
            """);
        if (allocateInsideInsert)
        {
            Execute(connection, "INSERT INTO t VALUES(nextval('s')); INSERT INTO t VALUES(nextval('s'));");
        }
        else
        {
            ReadValue(connection, "SELECT nextval('s');").Should().Be(SqlValue.Integer(100));
            ReadValue(connection, "SELECT nextval('s');").Should().Be(SqlValue.Integer(101));
        }
        Execute(connection, "ROLLBACK;");

        ReadValue(connection, "SELECT count(*) FROM t;").Should().Be(SqlValue.Integer(0));
        ReadValue(connection, "SELECT nextval('s');").Should().Be(SqlValue.Integer(100));
    }

    [Test]
    public void InMemoryWalRequestsPreserveMvccAndAllocationWatermarks()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, """
            PRAGMA journal_mode=mvcc;
            CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, value);
            CREATE SEQUENCE s;
            INSERT INTO t(value) VALUES(nextval('s'));
            INSERT INTO t(value) VALUES(nextval('s'));
            INSERT INTO t(value) VALUES(nextval('s'));
            """);

        ReadValue(connection, "PRAGMA journal_mode=wal;").Should().Be(SqlValue.Text("mvcc"));
        ReadValue(connection, "SELECT seq FROM sqlite_sequence WHERE name='t';")
            .Should().Be(SqlValue.Integer(3));
        Execute(connection, "INSERT INTO t(value) VALUES(nextval('s'));");
        ReadValue(connection, "SELECT max(id) FROM t;").Should().Be(SqlValue.Integer(4));
        ReadValue(connection, "SELECT max(value) FROM t;").Should().Be(SqlValue.Integer(4));
        ReadValue(connection, "PRAGMA journal_mode=mvcc;").Should().Be(SqlValue.Text("mvcc"));
        Execute(connection, "INSERT INTO t(value) VALUES(nextval('s'));");
        ReadValue(connection, "SELECT max(id) FROM t;").Should().Be(SqlValue.Integer(5));
        ReadValue(connection, "SELECT max(value) FROM t;").Should().Be(SqlValue.Integer(5));
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
            {
                while (statement.Step() == StatementStepResult.Row)
                {
                }
            }
        }
    }

    private static SqlValue ReadValue(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0);
    }
}
