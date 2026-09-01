using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

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

    [Test]
    public void SuccessfulCoreDdlAdvancesSchemaVersionExactlyOncePerStatement()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        var schemaVersion = 0L;

        foreach (var sql in new[]
                 {
                     "CREATE TABLE base(id INTEGER PRIMARY KEY, value TEXT);",
                     "CREATE TABLE copied AS SELECT value FROM base;",
                     "CREATE INDEX base_value ON base(value);",
                     "CREATE VIEW base_view AS SELECT value FROM base;",
                     "CREATE TRIGGER base_trigger AFTER INSERT ON base BEGIN SELECT 1; END;",
                     "ALTER TABLE base ADD COLUMN extra INTEGER;",
                     "DROP TRIGGER base_trigger;",
                     "DROP VIEW base_view;",
                     "DROP INDEX base_value;",
                     "DROP TABLE copied;",
                     "DROP TABLE base;",
                 })
        {
            Execute(connection, sql);
            ReadCount(connection, "PRAGMA schema_version;").Should().Be(++schemaVersion);
        }
    }

    [Test]
    public void CoreDdlSavepointRollbackRestoresSchemaRowsAndCookie()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE stable(id INTEGER PRIMARY KEY);");
        var schemaVersion = ReadCount(connection, "PRAGMA schema_version;");

        Execute(connection, "BEGIN;");
        Execute(connection, "SAVEPOINT before_ddl;");
        Execute(connection, "CREATE TABLE transient(id INTEGER PRIMARY KEY, value TEXT);");
        Execute(connection, "CREATE INDEX transient_value ON transient(value);");
        Execute(connection, "CREATE VIEW transient_view AS SELECT value FROM transient;");
        Execute(connection, "CREATE TRIGGER transient_trigger AFTER INSERT ON transient BEGIN SELECT 1; END;");
        Execute(connection, "ALTER TABLE stable ADD COLUMN note TEXT;");
        Execute(connection, "ROLLBACK TO before_ddl;");
        Execute(connection, "RELEASE before_ddl;");

        ReadCount(
                connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('transient', 'transient_value', 'transient_view', 'transient_trigger');")
            .Should()
            .Be(0);
        ReadCount(connection, "SELECT COUNT(*) FROM pragma_table_info('stable') WHERE name = 'note';")
            .Should()
            .Be(0);
        ReadCount(connection, "PRAGMA schema_version;").Should().Be(schemaVersion);

        Execute(connection, "COMMIT;");
        ReadCount(connection, "PRAGMA schema_version;").Should().Be(schemaVersion);
    }

    [Test]
    public void FailedCoreDdlLeavesSchemaRowsAndCookieUntouched()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE stable(id INTEGER PRIMARY KEY, value TEXT);");
        var schemaVersion = ReadCount(connection, "PRAGMA schema_version;");
        var schemaRows = ReadSchemaRows(connection);

        Action createInvalidIndex = () => Execute(connection, "CREATE INDEX invalid_index ON stable(missing);");

        createInvalidIndex.Should().Throw<EmbeddedSqlException>().WithMessage("no such column: missing");
        ReadCount(connection, "PRAGMA schema_version;").Should().Be(schemaVersion);
        ReadSchemaRows(connection).Should().Equal(schemaRows);
    }

    [Test]
    public void RolledBackCoreDdlDoesNotSurviveFileReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "ddl-rollback-baseline.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE stable(id INTEGER PRIMARY KEY);");
            Execute(connection, "BEGIN;");
            Execute(connection, "CREATE TABLE transient(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, "CREATE INDEX transient_value ON transient(value);");
            Execute(connection, "CREATE VIEW transient_view AS SELECT value FROM transient;");
            Execute(connection, "CREATE TRIGGER transient_trigger AFTER INSERT ON transient BEGIN SELECT 1; END;");
            Execute(connection, "ROLLBACK;");
        }

        using var reopenedDatabase = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopened = reopenedDatabase.Connect();
        ReadSchemaRows(reopened).Should().Equal("table|stable|stable");
        ReadCount(reopened, "PRAGMA schema_version;").Should().Be(1);
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

    private static string[] ReadSchemaRows(EmbeddedConnection connection)
    {
        using var statement = connection.Prepare(
            "SELECT type, name, tbl_name FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name;");
        var rows = new List<string>();
        while (statement.Step() == StatementStepResult.Row)
        {
            rows.Add(string.Join("|", Enumerable.Range(0, 3).Select(index => statement.GetValue(index).AsText())));
        }

        return rows.ToArray();
    }
}
