using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

public sealed class ManagedDdlBoundaryTests
{
    [Test]
    public void ManagedEngineAcceptsExplicitNullColumnConstraints()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE items(untyped NULL, typed TEXT NULL);");
        Execute(connection, "INSERT INTO items VALUES (NULL, NULL);");

        ReadCount(connection, "SELECT COUNT(*) FROM items WHERE untyped IS NULL AND typed IS NULL;")
            .Should()
            .Be(1);
    }

    [TestCase("CREATE TABLE items(value INTEGER CHECK (value > 0));")]
    [TestCase("CREATE TABLE items(value INTEGER, CONSTRAINT items_value_unique UNIQUE(value));")]
    [TestCase("CREATE TABLE items(value INTEGER NOT NULL ON CONFLICT IGNORE);")]
    [TestCase("CREATE TABLE items(value INTEGER UNIQUE ON CONFLICT REPLACE);")]
    [TestCase("CREATE TABLE items(value INTEGER PRIMARY KEY ON CONFLICT ABORT);")]
    public void ManagedEngineAcceptsConstraintDdl(string sql)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, sql);
        ReadCount(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';")
            .Should()
            .Be(1);
    }

    // A column declaring two inline PRIMARY KEY clauses (e.g. "a primary key primary key")
    // is rejected, matching SQLite/Turso: a table may have at most one primary key.
    [TestCase("CREATE TABLE t(a primary key primary key);")]
    [TestCase("CREATE TABLE t(a INTEGER PRIMARY KEY PRIMARY KEY);")]
    [TestCase("CREATE TABLE t(a primary key, b primary key);")]
    public void ManagedEngineRejectsDuplicatePrimaryKey(string sql)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        var error = Assert.Throws<EmbeddedSqlException>(() => Execute(connection, sql))!;
        error.Message.Should().Contain("more than one primary key");
    }

    // A column-level REFERENCES clause may name at most one parent column, matching
    // SQLite/Turso (turso-src/core/schema.rs: column-level FK columns.len() > 1 bail).
    [Test]
    public void ManagedEngineRejectsColumnLevelForeignKeyWithMultipleParentColumns()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE t(a, c);");

        var error = Assert.Throws<EmbeddedSqlException>(() =>
            Execute(connection, "CREATE TABLE s(a REFERENCES t(a, c));"))!;
        error.Message.Should().Contain("should reference only one column");
    }

    // RENAME COLUMN rewrites qualified references inside UPDATE...FROM trigger
    // bodies, matching SQLite/Turso (alter_table.sqltest::alter-rename-col-schema-update-cmd-from).
    [Test]
    public void ManagedEngineRewritesUpdateFromTriggerBodyOnRenameColumn()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE src (a INTEGER PRIMARY KEY, b);");
        Execute(connection, "CREATE TABLE aux (a INTEGER PRIMARY KEY, z);");
        Execute(connection, "CREATE TABLE dst (x);");
        Execute(connection,
            """
            CREATE TRIGGER trig1 AFTER INSERT ON dst BEGIN
                UPDATE aux SET z = src.b FROM src WHERE aux.a = src.a AND src.a = new.x;
            END
            """);

        Execute(connection, "ALTER TABLE src RENAME COLUMN b TO c;");

        var sql = ReadText(connection, "SELECT sql FROM sqlite_master WHERE type = 'trigger' AND name = 'trig1';");
        sql.Should().Contain("src.c");
        sql.Should().NotContain("src.b");
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static long ReadCount(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static string ReadText(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsText();
    }
}
