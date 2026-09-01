using System.Globalization;
using Ahtola.Core;
using Ahtola.Core.Execution;
using Ahtola.Core.Storage;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// End-to-end behavior of the compiled <c>ALTER TABLE</c> programs: what each variant changes, what it
/// leaves alone, what survives a rollback or a reopen, and what <c>EXPLAIN</c> reports without running any
/// of it.
/// </summary>
public sealed class AlterTableBytecodeTests
{
    private const string SchemaQuery =
        "SELECT type || '|' || name || '|' || tbl_name FROM sqlite_schema ORDER BY type, name;";

    // ---------------------------------------------------------------- explain

    [Test]
    public void ExplainAlterTableDescribesTheRowRewriteProgramWithoutMutatingAnything()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT);");
        Execute(connection, "CREATE INDEX t_a ON t(a);");
        Execute(connection, "INSERT INTO t VALUES (1, 'x');");
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();

        var opcodes = ExplainOpcodes(connection, "EXPLAIN ALTER TABLE t RENAME TO u;");

        opcodes.Should().Contain(
            ["OpenWriteCursor", "Rewind", "Column", "Compare", "Delete", "NewRowid", "MakeRecord", "Insert",
             "SetCookie", "RenameTable", "Halt"]);
        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
        ReadRows(connection, SchemaQuery).Should().Equal("index|t_a|t", "table|t|t");
        ReadScalar(connection, "SELECT COUNT(*) FROM t;").AsInteger().Should().Be(1);
    }

    [Test]
    public void ExplainAlterTableDescribesEveryVariantWithItsTypedOpcode()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT);");

        ExplainOpcodes(connection, "EXPLAIN ALTER TABLE t ADD COLUMN c BLOB;").Should().Contain("AddColumn");
        ExplainOpcodes(connection, "EXPLAIN ALTER TABLE t DROP COLUMN b;").Should().Contain("DropColumn");
        ExplainOpcodes(connection, "EXPLAIN ALTER TABLE t RENAME COLUMN b TO c;").Should().Contain("AlterColumn");
        ExplainOpcodes(connection, "EXPLAIN ALTER TABLE t ALTER COLUMN b TO b BLOB;").Should().Contain("AlterColumn");

        // Describing never lowers the dependent-schema validation the statement would run, and never runs
        // the program either: the table is untouched.
        ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name = 't';")
            .Should()
            .Equal("CREATE TABLE t(a INTEGER, b TEXT)");
    }

    [Test]
    public void ExplainAlterTableStillReportsAnIllegalAlterationAtDescribeTime()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY);");

        Action missingTable = () => ExplainOpcodes(connection, "EXPLAIN ALTER TABLE missing RENAME TO other;");
        Action lastColumn = () => ExplainOpcodes(connection, "EXPLAIN ALTER TABLE t DROP COLUMN a;");

        missingTable.Should().Throw<EmbeddedSqlException>().WithMessage("no such table: missing");
        lastColumn.Should().Throw<EmbeddedSqlException>()
            .WithMessage("cannot drop PRIMARY KEY column: \"a\"");
    }

    // ------------------------------------------------------------ happy paths

    [Test]
    public void RenameTableMovesTheTableItsIndexesAndItsRows()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT UNIQUE);");
        Execute(connection, "CREATE INDEX t_a ON t(a);");
        Execute(connection, "INSERT INTO t VALUES (1, 'x'), (2, 'y');");

        Execute(connection, "ALTER TABLE t RENAME TO u;");

        ReadRows(connection, SchemaQuery).Should().Equal(
            "index|sqlite_autoindex_u_1|u",
            "index|t_a|u",
            "table|u|u");
        ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE type = 'table';")
            .Should()
            .Equal("CREATE TABLE \"u\"(a INTEGER, b TEXT UNIQUE)");
        ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name = 't_a';")
            .Should()
            .Equal("CREATE INDEX t_a ON \"u\"(a)");
        ReadRows(connection, "SELECT a || '|' || b FROM u ORDER BY a;").Should().Equal("1|x", "2|y");
    }

    [Test]
    public void RenameTableFollowsForeignKeysViewsAndTriggers()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY);");
        Execute(connection, "CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id));");
        Execute(connection, "CREATE VIEW v AS SELECT id FROM parent;");
        Execute(connection, "CREATE TRIGGER tr AFTER INSERT ON parent BEGIN SELECT 1; END;");

        Execute(connection, "ALTER TABLE parent RENAME TO ancestor;");

        ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name = 'child';")
            .Should()
            .Equal("CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES \"ancestor\"(id))");
        ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name = 'v';")
            .Should()
            .Equal("CREATE VIEW v AS SELECT id FROM ancestor");
        ReadRows(connection, "SELECT tbl_name || '|' || sql FROM sqlite_schema WHERE name = 'tr';")
            .Should()
            .Equal("ancestor|CREATE TRIGGER tr AFTER INSERT ON ancestor BEGIN SELECT 1; END");

        // The rewritten foreign key still enforces: the parent metadata followed the rename.
        Execute(connection, "PRAGMA foreign_keys = ON;");
        Action orphan = () => Execute(connection, "INSERT INTO child VALUES (1, 99);");
        orphan.Should().Throw<EmbeddedSqlException>().WithMessage("*FOREIGN KEY*");
    }

    [Test]
    public void RenameTableCarriesTheAutoIncrementWatermarkAcross()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, v TEXT);");
        Execute(connection, "INSERT INTO t(v) VALUES ('a'), ('b');");

        Execute(connection, "ALTER TABLE t RENAME TO u;");

        ReadRows(connection, "SELECT name || '|' || seq FROM sqlite_sequence;").Should().Equal("u|2");
        Execute(connection, "INSERT INTO u(v) VALUES ('c');");
        ReadScalar(connection, "SELECT MAX(id) FROM u;").AsInteger().Should().Be(3);
    }

    [Test]
    public void AddColumnBackfillsExistingRowsAndExtendsTheStoredSql()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2);");

        Execute(connection, "ALTER TABLE t ADD COLUMN b TEXT DEFAULT 'n/a';");

        ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name = 't';")
            .Should()
            .Equal("CREATE TABLE t(a INTEGER, b TEXT DEFAULT 'n/a')");
        ReadRows(connection, "SELECT a || '|' || b FROM t ORDER BY a;").Should().Equal("1|n/a", "2|n/a");
    }

    [Test]
    public void DropColumnProjectsEveryStoredRow()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT, c BLOB);");
        Execute(connection, "INSERT INTO t VALUES (1, 'x', NULL), (2, 'y', NULL);");

        Execute(connection, "ALTER TABLE t DROP COLUMN b;");

        ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name = 't';")
            .Should()
            .Equal("CREATE TABLE t(a INTEGER, c BLOB)");
        ReadRows(connection, "SELECT a FROM t ORDER BY a;").Should().Equal("1", "2");
        Action gone = () => ReadRows(connection, "SELECT b FROM t;");
        gone.Should().Throw<EmbeddedSqlException>();
    }

    [Test]
    public void RenameColumnFollowsDependentViewsAndTriggers()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT);");
        Execute(connection, "CREATE VIEW v AS SELECT b FROM t;");
        Execute(connection, "CREATE TRIGGER tr AFTER UPDATE OF b ON t BEGIN SELECT 1; END;");
        Execute(connection, "INSERT INTO t VALUES (1, 'x');");

        Execute(connection, "ALTER TABLE t RENAME COLUMN b TO body;");

        ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name = 't';")
            .Should()
            .Equal("CREATE TABLE t(a INTEGER, body TEXT)");
        ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name = 'v';")
            .Should()
            .Equal("CREATE VIEW v AS SELECT body FROM t");
        ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name = 'tr';")
            .Should()
            .Equal("CREATE TRIGGER tr AFTER UPDATE OF body ON t BEGIN SELECT 1; END");
        ReadRows(connection, "SELECT body FROM t;").Should().Equal("x");
    }

    [Test]
    public void RenameColumnQuotesTheReplacementWhenTheStatementDid()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT);");
        Execute(connection, "CREATE VIEW v AS SELECT b FROM t;");

        Execute(connection, "ALTER TABLE t RENAME COLUMN b TO \"c d\";");

        ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name = 'v';")
            .Should()
            .Equal("CREATE VIEW v AS SELECT \"c d\" FROM t");
        ReadRows(connection, "SELECT \"c d\" FROM t;").Should().BeEmpty();
    }

    [Test]
    public void AlterColumnCoercesStoredValuesToTheReplacementAffinity()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT);");
        Execute(connection, "INSERT INTO t VALUES (1, '42');");

        Execute(connection, "ALTER TABLE t ALTER COLUMN b TO b INTEGER;");

        ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name = 't';")
            .Should()
            .Equal("CREATE TABLE t (a INTEGER, b INTEGER)");
        ReadRows(connection, "SELECT typeof(b) FROM t;").Should().Equal("integer");
    }

    [Test]
    public void AlterColumnRetiringAutoIncrementClearsTheWatermarkAndItsBackingTable()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, v TEXT);");
        Execute(connection, "INSERT INTO t(v) VALUES ('a'), ('b');");
        ReadScalar(connection, "SELECT seq FROM sqlite_sequence WHERE name = 't';").AsInteger().Should().Be(2);

        Execute(connection, "ALTER TABLE t ALTER COLUMN id TO id INTEGER;");

        ReadRows(connection, "SELECT name FROM sqlite_sequence;").Should().BeEmpty();
        ReadRows(connection, SchemaQuery).Should().Contain("table|t|t");
        ReadRows(connection, SchemaQuery)
            .Should()
            .NotContain(row => row.Contains(
                EmbeddedDatabase.GetAutoIncrementSequenceBackingTableName("t"),
                StringComparison.Ordinal));
    }

    [Test]
    public void AlterColumnDroppingAUniqueConstraintRetiresTheIndexItBacked()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT UNIQUE);");
        Execute(connection, "INSERT INTO t VALUES (1, 'x');");
        ReadRows(connection, SchemaQuery).Should().Equal("index|sqlite_autoindex_t_1|t", "table|t|t");

        Execute(connection, "ALTER TABLE t ALTER COLUMN b TO b TEXT;");

        // The constraint the implicit index came from is gone, so its row and its storage go with it.
        ReadRows(connection, SchemaQuery).Should().Equal("table|t|t");
        Execute(connection, "INSERT INTO t VALUES (2, 'x');");
        ReadRows(connection, "SELECT a FROM t ORDER BY a;").Should().Equal("1", "2");
    }

    // ------------------------------------------------------------------ names

    [Test]
    public void AlterTableResolvesTheTargetCaseInsensitivelyAndKeepsTheStoredSpelling()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE Orders(a INTEGER, b TEXT);");
        Execute(connection, "INSERT INTO Orders VALUES (1, 'x');");

        Execute(connection, "ALTER TABLE oRdErS ADD COLUMN c BLOB;");
        Execute(connection, "ALTER TABLE ORDERS DROP COLUMN b;");

        ReadRows(connection, SchemaQuery).Should().Equal("table|Orders|Orders");
        ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name = 'Orders';")
            .Should()
            .Equal("CREATE TABLE Orders(a INTEGER, c BLOB)");
        ReadRows(connection, "SELECT a FROM orders;").Should().Equal("1");
    }

    // ----------------------------------------------------------------- errors

    [Test]
    public void AnAlterationThatCannotRunLeavesTheSchemaExactlyAsItWas()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT);");
        Execute(connection, "CREATE TABLE other(a INTEGER);");
        Execute(connection, "CREATE VIEW v AS SELECT b FROM t;");
        Execute(connection, "INSERT INTO t VALUES (1, 'x');");
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();

        Action occupied = () => Execute(connection, "ALTER TABLE t RENAME TO other;");
        Action brokenView = () => Execute(connection, "ALTER TABLE t DROP COLUMN b;");
        Action duplicate = () => Execute(connection, "ALTER TABLE t ADD COLUMN a INTEGER;");
        Action missingColumn = () => Execute(connection, "ALTER TABLE t RENAME COLUMN missing TO other;");

        occupied.Should().Throw<EmbeddedSqlException>().WithMessage("table other already exists");
        brokenView.Should().Throw<EmbeddedSqlException>().WithMessage("error in view v after drop column:*");
        duplicate.Should().Throw<EmbeddedSqlException>().WithMessage("duplicate column name: a");
        missingColumn.Should().Throw<EmbeddedSqlException>().WithMessage("*missing*");

        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
        ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name = 't';")
            .Should()
            .Equal("CREATE TABLE t(a INTEGER, b TEXT)");
        ReadRows(connection, "SELECT a || '|' || b FROM t;").Should().Equal("1|x");
    }

    [Test]
    public void AddColumnStillRefusesTheDeclarationsSqliteForbids()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1);");

        Action stored = () => Execute(connection, "ALTER TABLE t ADD COLUMN b TEXT GENERATED ALWAYS AS (a) STORED;");
        Action unique = () => Execute(connection, "ALTER TABLE t ADD COLUMN b TEXT UNIQUE;");
        Action notNull = () => Execute(connection, "ALTER TABLE t ADD COLUMN b TEXT NOT NULL;");
        Action nonConstant = () => Execute(connection, "ALTER TABLE t ADD COLUMN b TEXT DEFAULT (a + 1);");

        stored.Should().Throw<EmbeddedSqlException>().WithMessage("cannot add a STORED column");
        unique.Should().Throw<EmbeddedSqlException>().WithMessage("Cannot add a PRIMARY KEY or UNIQUE column.");
        notNull.Should().Throw<EmbeddedSqlException>()
            .WithMessage("Cannot add a NOT NULL column without a default value.");
        nonConstant.Should().Throw<EmbeddedSqlException>()
            .WithMessage("default value of column [b] is not constant");
        ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name = 't';")
            .Should()
            .Equal("CREATE TABLE t(a INTEGER)");
    }

    [Test]
    public void DropColumnStillRefusesTheColumnsSqliteProtects()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, u TEXT UNIQUE, i INTEGER, plain TEXT);");
        Execute(connection, "CREATE INDEX t_i ON t(i);");

        Action primaryKey = () => Execute(connection, "ALTER TABLE t DROP COLUMN id;");
        Action unique = () => Execute(connection, "ALTER TABLE t DROP COLUMN u;");
        Action indexed = () => Execute(connection, "ALTER TABLE t DROP COLUMN i;");

        primaryKey.Should().Throw<EmbeddedSqlException>().WithMessage("cannot drop PRIMARY KEY column: \"id\"");
        unique.Should().Throw<EmbeddedSqlException>().WithMessage("cannot drop UNIQUE column: \"u\"");
        indexed.Should().Throw<EmbeddedSqlException>()
            .WithMessage("error in index t_i after drop column: no such column: i");
        ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name = 't';")
            .Should()
            .Equal("CREATE TABLE t(id INTEGER PRIMARY KEY, u TEXT UNIQUE, i INTEGER, plain TEXT)");
    }

    [Test]
    public void AlterTableRefusesToTouchTheInternalSequenceTableOrAVirtualTable()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT);");
        Execute(connection, "INSERT INTO t DEFAULT VALUES;");

        Action sequence = () => Execute(connection, "ALTER TABLE sqlite_sequence ADD COLUMN extra TEXT;");
        Action reserved = () => Execute(connection, "ALTER TABLE t RENAME TO sqlite_sequence;");

        sequence.Should().Throw<EmbeddedSqlException>()
            .WithMessage("table sqlite_sequence may not be altered");
        reserved.Should().Throw<EmbeddedSqlException>()
            .WithMessage("object name reserved for internal use: sqlite_sequence");
    }

    // --------------------------------------------------------------- rollback

    [Test]
    public void RollingBackAnAlterationRestoresTheSchemaAndTheRows()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT);");
        Execute(connection, "INSERT INTO t VALUES (1, 'x');");

        Execute(connection, "BEGIN;");
        Execute(connection, "ALTER TABLE t RENAME TO u;");
        Execute(connection, "ALTER TABLE u ADD COLUMN c BLOB;");
        ReadRows(connection, SchemaQuery).Should().Equal("table|u|u");
        Execute(connection, "ROLLBACK;");

        ReadRows(connection, SchemaQuery).Should().Equal("table|t|t");
        ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name = 't';")
            .Should()
            .Equal("CREATE TABLE t(a INTEGER, b TEXT)");
        ReadRows(connection, "SELECT a || '|' || b FROM t;").Should().Equal("1|x");
    }

    [Test]
    public void ASavepointRollbackUndoesOnlyTheAlterationItCovers()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT);");

        Execute(connection, "BEGIN;");
        Execute(connection, "ALTER TABLE t ADD COLUMN c BLOB;");
        Execute(connection, "SAVEPOINT s;");
        Execute(connection, "ALTER TABLE t DROP COLUMN b;");
        Execute(connection, "ROLLBACK TO s;");
        Execute(connection, "COMMIT;");

        ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name = 't';")
            .Should()
            .Equal("CREATE TABLE t(a INTEGER, b TEXT, c BLOB)");
    }

    [Test]
    public void AlterTableLeavesTheDmlCountersExactlyWhereTheLastDataStatementLeftThem()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT);");
        Execute(connection, "INSERT INTO t VALUES (7, 'x');");

        Execute(connection, "ALTER TABLE t ADD COLUMN c BLOB;");
        Execute(connection, "ALTER TABLE t RENAME TO u;");

        // A schema program's row writes are marked SkipLastRowid/SkipAllChangeCounts, so the counters still
        // report the INSERT that ran before the alterations rather than the schema rows they rewrote.
        ReadScalar(connection, "SELECT changes();").AsInteger().Should().Be(1);
        ReadScalar(connection, "SELECT last_insert_rowid();").AsInteger().Should().Be(7);
    }

    // ------------------------------------------------------------- durability

    [Test]
    public void EveryAlterationSurvivesAReopenWithItsRowsAndRoots()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "alter.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT, c BLOB);");
            Execute(connection, "CREATE INDEX t_a ON t(a);");
            Execute(connection, "INSERT INTO t VALUES (1, 'x', NULL), (2, 'y', NULL);");
            Execute(connection, "ALTER TABLE t DROP COLUMN c;");
            Execute(connection, "ALTER TABLE t ADD COLUMN d TEXT DEFAULT 'z';");
            Execute(connection, "ALTER TABLE t RENAME COLUMN b TO body;");
            Execute(connection, "ALTER TABLE t RENAME TO u;");
        }

        using (var reopened = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = reopened.Connect())
        {
            ReadRows(connection, SchemaQuery).Should().Equal("index|t_a|u", "table|u|u");
            ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name = 'u';")
                .Should()
                .Equal("CREATE TABLE \"u\"(a INTEGER, body TEXT, d TEXT DEFAULT 'z')");
            ReadRows(connection, "SELECT a || '|' || body || '|' || d FROM u ORDER BY a;")
                .Should()
                .Equal("1|x|z", "2|y|z");
            // The index still resolves against the renamed table, so a seek through it returns the row.
            ReadRows(connection, "SELECT body FROM u WHERE a = 2;").Should().Equal("y");
        }
    }

    [Test]
    public void AnAlterationRolledBackBeforeCommitLeavesNothingInTheFile()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "alter-rollback.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT);");
            Execute(connection, "BEGIN;");
            Execute(connection, "ALTER TABLE t RENAME TO renamed_away;");
            Execute(connection, "ROLLBACK;");
        }

        using (var reopened = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = reopened.Connect())
        {
            ReadRows(connection, SchemaQuery).Should().Equal("table|t|t");
        }
    }

    // ---------------------------------------------------------------- helpers

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    private static SqlValue ReadScalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0);
    }

    private static string[] ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<string>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var values = new List<string>();
            for (var column = 0; column < statement.ColumnCount; column++)
            {
                var value = statement.GetValue(column);
                values.Add(value.Kind switch
                {
                    SqlValueKind.Null => string.Empty,
                    SqlValueKind.Integer => value.AsInteger().ToString(CultureInfo.InvariantCulture),
                    SqlValueKind.Real => value.AsReal().ToString(CultureInfo.InvariantCulture),
                    _ => value.AsText(),
                });
            }

            rows.Add(string.Join("|", values));
        }

        return [.. rows];
    }

    private static string[] ExplainOpcodes(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var opcodes = new List<string>();
        while (statement.Step() == StatementStepResult.Row)
            opcodes.Add(statement.GetValue(1).AsText());

        return [.. opcodes];
    }
}
