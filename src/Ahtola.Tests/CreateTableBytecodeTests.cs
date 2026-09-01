using Ahtola.Core;
using Ahtola.Core.Storage;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// End-to-end behavior of the compiled <c>CREATE TABLE</c>/<c>CREATE TABLE AS SELECT</c> path: what the
/// program does when it succeeds, what it leaves behind when it fails, what survives a reopen, and what
/// <c>EXPLAIN</c> reports without running it.
/// </summary>
public sealed class CreateTableBytecodeTests
{
    [Test]
    public void ExplainCreateTableDescribesTheSchemaProgramWithoutMutatingAnything()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();

        var opcodes = ExplainOpcodes(connection, "EXPLAIN CREATE TABLE t(a INTEGER, b TEXT);");

        opcodes.Should().Contain(["CreateBtree", "NewRowid", "MakeRecord", "Insert", "SetCookie", "ParseSchema", "Halt"]);
        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 't';").AsInteger().Should().Be(0);
    }

    [Test]
    public void ExplainCreateTableAsSelectDescribesThePopulationLoopWithoutRunningTheQuery()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE base(id INTEGER PRIMARY KEY, value TEXT);");
        Execute(connection, "INSERT INTO base VALUES (1, 'a'), (2, 'b');");

        var opcodes = ExplainOpcodes(connection, "EXPLAIN CREATE TABLE copied AS SELECT value FROM base;");

        opcodes.Should().Contain(["CreateBtree", "ParseSchema", "Rewind", "Column", "MakeRecord", "NewRowid", "Insert", "Next"]);
        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'copied';").AsInteger().Should().Be(0);
    }

    [Test]
    public void ExplainCoversEveryDdlFamilyNowThatAlterTableIsLowered()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE base(id INTEGER PRIMARY KEY, value TEXT);");

        // The last evaluator-owned DDL family was ALTER TABLE; every variant now describes a real program
        // instead of refusing, and describing one still mutates nothing.
        foreach (var ddl in new[]
                 {
                     "ALTER TABLE base ADD COLUMN extra INTEGER;",
                     "ALTER TABLE base RENAME TO renamed;",
                     "ALTER TABLE base RENAME COLUMN value TO body;",
                     "ALTER TABLE base ALTER COLUMN value TO value BLOB;",
                     "ALTER TABLE base DROP COLUMN value;",
                 })
        {
            ExplainOpcodes(connection, "EXPLAIN " + ddl)
                .Should()
                .Contain(["Rewind", "Delete", "MakeRecord", "Insert", "SetCookie", "Halt"], ddl);
        }

        ReadRows(connection, "SELECT name FROM sqlite_schema ORDER BY name;").Should().Equal("base");
        ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name = 'base';")
            .Should()
            .Equal("CREATE TABLE base(id INTEGER PRIMARY KEY, value TEXT)");
    }

    [Test]
    public void CreateTableWritesTheSchemaRowTheProgramBuilt()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT UNIQUE);");

        ReadRows(connection, "SELECT type, name, tbl_name, sql FROM sqlite_schema ORDER BY name;")
            .Should()
            .Equal(
                "index|sqlite_autoindex_t_1|t|",
                "table|t|t|CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT UNIQUE)");
    }

    [Test]
    public void CreateTableLeavesLastInsertRowidAndChangesUntouched()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE seed(id INTEGER PRIMARY KEY, v TEXT);");
        Execute(connection, "INSERT INTO seed VALUES (42, 'x');");

        Execute(connection, "CREATE TABLE later(a);");

        ReadScalar(connection, "SELECT last_insert_rowid();").AsInteger().Should().Be(42);
        ReadScalar(connection, "SELECT changes();").AsInteger().Should().Be(1);
    }

    [Test]
    public void CreateTableAsSelectPopulatesEveryRowWithSequentialRowids()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE base(id INTEGER PRIMARY KEY, value TEXT);");
        Execute(connection, "INSERT INTO base VALUES (7, 'a'), (9, 'b'), (11, 'c');");

        Execute(connection, "CREATE TABLE copied AS SELECT value FROM base;");

        ReadRows(connection, "SELECT rowid, value FROM copied ORDER BY rowid;")
            .Should()
            .Equal("1|a", "2|b", "3|c");
        ReadScalar(connection, "SELECT sql FROM sqlite_schema WHERE name = 'copied';")
            .AsText()
            .Should()
            .Be("CREATE TABLE copied(value TEXT)");
    }

    [Test]
    public void CreateTableAsSelectAppliesTheTargetColumnAffinities()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE base(v INTEGER);");
        Execute(connection, "INSERT INTO base VALUES (1), (2);");

        Execute(connection, "CREATE TABLE copied AS SELECT v FROM base;");

        ReadRows(connection, "SELECT typeof(v) FROM copied ORDER BY rowid;").Should().Equal("integer", "integer");
    }

    [Test]
    public void CreateTableWithAutoIncrementRegistersTheSequenceTablesAndTheirSeedRow()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, v TEXT);");

        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'sqlite_sequence';")
            .AsInteger()
            .Should()
            .Be(1);
        Execute(connection, "INSERT INTO t(v) VALUES ('a'), ('b');");
        Execute(connection, "DELETE FROM t;");
        Execute(connection, "INSERT INTO t(v) VALUES ('c');");
        ReadScalar(connection, "SELECT id FROM t;").AsInteger().Should().Be(3);
        ReadScalar(connection, "SELECT seq FROM sqlite_sequence WHERE name = 't';").AsInteger().Should().Be(3);
    }

    [Test]
    public void CreateTableIfNotExistsOnAnExistingTableChangesNothing()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();

        Execute(connection, "CREATE TABLE IF NOT EXISTS t(a TEXT, b TEXT);");

        ReadScalar(connection, "SELECT sql FROM sqlite_schema WHERE name = 't';")
            .AsText()
            .Should()
            .Be("CREATE TABLE t(a INTEGER)");
        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
    }

    [Test]
    public void TriggerNamesDoNotPoisonLaterCreateTablePrograms()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE VIEW v AS SELECT a FROM t;");
        Execute(connection, "CREATE INDEX ix ON t(a);");
        Execute(connection, "CREATE TRIGGER t AFTER INSERT ON t BEGIN SELECT 1; END;");
        Execute(connection, "CREATE TRIGGER v AFTER INSERT ON t BEGIN SELECT 1; END;");
        Execute(connection, "CREATE TRIGGER ix AFTER INSERT ON t BEGIN SELECT 1; END;");

        Execute(connection, "CREATE TABLE unrelated(value TEXT);");

        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'unrelated';")
            .AsInteger()
            .Should()
            .Be(1);
    }

    [Test]
    public void CreateTableMayUseAnExistingTriggerName()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE base(value INTEGER);");
        Execute(connection, "CREATE TRIGGER shared_name AFTER INSERT ON base BEGIN SELECT 1; END;");

        Execute(connection, "CREATE TABLE shared_name(value TEXT);");

        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'shared_name';")
            .AsInteger()
            .Should()
            .Be(2);
    }

    [Test]
    public void CreateTableIfNotExistsTreatsAnExistingVirtualTableAsATable()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE VIRTUAL TABLE v USING fts5(body);");
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();

        Execute(connection, "CREATE TABLE IF NOT EXISTS v(a TEXT);");

        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
        Execute(connection, "INSERT INTO v(body) VALUES ('still virtual');");
        ReadScalar(connection, "SELECT COUNT(*) FROM v;").AsInteger().Should().Be(1);
    }

    [Test]
    public void CreateTableAsSelectAllocatesLargeSequentialRowidRuns()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE source(value INTEGER);");
        Execute(
            connection,
            """
            WITH RECURSIVE values_to_copy(value) AS (
                VALUES(1)
                UNION ALL
                SELECT value + 1 FROM values_to_copy WHERE value < 8192
            )
            INSERT INTO source SELECT value FROM values_to_copy;
            """);

        Execute(connection, "CREATE TABLE copied AS SELECT value FROM source;");

        ReadScalar(connection, "SELECT COUNT(*) FROM copied;").AsInteger().Should().Be(8192);
        ReadScalar(connection, "SELECT MAX(rowid) FROM copied;").AsInteger().Should().Be(8192);
    }

    [TestCase("CREATE TABLE t(a);", "table t already exists")]
    [TestCase("CREATE TABLE v(a);", "there is already a view named v")]
    [TestCase("CREATE TABLE t_index(a);", "there is already an index named t_index")]
    [TestCase("CREATE TABLE sqlite_thing(a);", "object name reserved for internal use: sqlite_thing")]
    [TestCase("CREATE TABLE bad(a, b) WITHOUT ROWID;", "PRIMARY KEY missing on table bad")]
    public void RejectedCreateTableLeavesTheSchemaExactlyAsItWas(string sql, string message)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE INDEX t_index ON t(a);");
        Execute(connection, "CREATE VIEW v AS SELECT a FROM t;");
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();
        var schemaRows = ReadRows(connection, SchemaQuery);

        Action create = () => Execute(connection, sql);

        create.Should().Throw<EmbeddedSqlException>().WithMessage(message);
        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
        ReadRows(connection, SchemaQuery).Should().Equal(schemaRows);
    }

    [Test]
    public void AFailedCreateTableAsSelectLeavesNoTableSchemaRowOrCookieBump()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE base(v INTEGER);");
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();
        var schemaRows = ReadRows(connection, SchemaQuery);

        Action create = () => Execute(connection, "CREATE TABLE copied AS SELECT missing FROM base;");

        create.Should().Throw<EmbeddedSqlException>();
        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
        ReadRows(connection, SchemaQuery).Should().Equal(schemaRows);
        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'copied';").AsInteger().Should().Be(0);
    }

    [Test]
    public void SuccessfulCreateTableAdvancesTheSchemaCookieExactlyOnce()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE base(v INTEGER);");
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();

        Execute(connection, "CREATE TABLE plain(a);");
        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion + 1);

        Execute(connection, "CREATE TABLE copied AS SELECT v FROM base;");
        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion + 2);

        // AUTOINCREMENT creates three tables, but the statement is still one schema change.
        Execute(connection, "CREATE TABLE counted(id INTEGER PRIMARY KEY AUTOINCREMENT);");
        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion + 3);
    }

    [Test]
    public void RolledBackCreateTableIsInvisibleAndLeavesTheCookieAlone()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE base(v INTEGER);");
        Execute(connection, "INSERT INTO base VALUES (1), (2);");
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();

        Execute(connection, "BEGIN;");
        Execute(connection, "CREATE TABLE transient(a);");
        Execute(connection, "CREATE TABLE copied AS SELECT v FROM base;");
        Execute(connection, "ROLLBACK;");

        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('transient', 'copied');")
            .AsInteger()
            .Should()
            .Be(0);
        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
    }

    [Test]
    public void SavepointRollbackDiscardsACreateTableAsSelectAndItsRows()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE base(v INTEGER);");
        Execute(connection, "INSERT INTO base VALUES (1), (2), (3);");

        Execute(connection, "BEGIN;");
        Execute(connection, "SAVEPOINT before_ctas;");
        Execute(connection, "CREATE TABLE copied AS SELECT v FROM base;");
        ReadScalar(connection, "SELECT COUNT(*) FROM copied;").AsInteger().Should().Be(3);
        Execute(connection, "ROLLBACK TO before_ctas;");
        Execute(connection, "RELEASE before_ctas;");
        Execute(connection, "COMMIT;");

        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'copied';").AsInteger().Should().Be(0);
    }

    [Test]
    public void CommittedCreateTableAndCreateTableAsSelectSurviveAFileReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "create-table-bytecode.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE base(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, "INSERT INTO base VALUES (1, 'a'), (2, 'b');");
            Execute(connection, "CREATE TABLE copied AS SELECT value FROM base;");
            Execute(connection, "CREATE TABLE counted(id INTEGER PRIMARY KEY AUTOINCREMENT, v TEXT);");
            Execute(connection, "INSERT INTO counted(v) VALUES ('x');");
        }

        using var reopenedDatabase = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopened = reopenedDatabase.Connect();
        ReadRows(reopened, "SELECT value FROM copied ORDER BY rowid;").Should().Equal("a", "b");
        ReadScalar(reopened, "SELECT sql FROM sqlite_schema WHERE name = 'copied';")
            .AsText()
            .Should()
            .Be("CREATE TABLE copied(value TEXT)");
        ReadScalar(reopened, "SELECT seq FROM sqlite_sequence WHERE name = 'counted';").AsInteger().Should().Be(1);
    }

    [Test]
    public void ARolledBackFileBackedCreateTableDoesNotSurviveAReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "create-table-bytecode-rollback.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE stable(a);");
            Execute(connection, "BEGIN;");
            Execute(connection, "CREATE TABLE transient(a);");
            Execute(connection, "ROLLBACK;");
        }

        using var reopenedDatabase = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopened = reopenedDatabase.Connect();
        ReadRows(reopened, SchemaQuery).Should().Equal("table|stable|stable");
    }

    [Test]
    public void CreateTableIntoATemporarySchemaStaysOutOfTheMainSchema()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TEMP TABLE scratch(a INTEGER);");
        Execute(connection, "INSERT INTO scratch VALUES (5);");

        ReadScalar(connection, "SELECT a FROM scratch;").AsInteger().Should().Be(5);
        ReadScalar(connection, "SELECT COUNT(*) FROM main.sqlite_schema WHERE name = 'scratch';")
            .AsInteger()
            .Should()
            .Be(0);
    }

    [Test]
    public void CreateTableIsRejectedWhenItWouldExceedTheMaximumPageCount()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        // An in-memory database models max_page_count at the catalog level and only counts pages once its
        // header page exists, so materialize it before pinning the limit to the current size.
        Execute(connection, "PRAGMA user_version = 1;");
        Execute(connection, "CREATE TABLE base(a);");
        var pageCount = ReadScalar(connection, "PRAGMA page_count;").AsInteger();
        Execute(connection, $"PRAGMA max_page_count = {pageCount};");
        var schemaRows = ReadRows(connection, SchemaQuery);

        Action create = () => Execute(connection, "CREATE TABLE overflowing(a);");

        create.Should().Throw<EmbeddedSqlException>().WithMessage("database or disk is full");
        ReadRows(connection, SchemaQuery).Should().Equal(schemaRows);
    }

    [Test]
    public void CancellingACreateTableAsSelectLeavesNoPartiallyPopulatedTable()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE base(v INTEGER);");
        for (var value = 0; value < 64; value++)
            Execute(connection, $"INSERT INTO base VALUES ({value});");
        var schemaRows = ReadRows(connection, SchemaQuery);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Action create = () =>
        {
            using var statement = connection.Prepare("CREATE TABLE copied AS SELECT v FROM base;");
            statement.Step(cancellation.Token);
        };

        create.Should().Throw<OperationCanceledException>();
        ReadRows(connection, SchemaQuery).Should().Equal(schemaRows);
    }

    private const string SchemaQuery =
        "SELECT type, name, tbl_name FROM sqlite_schema ORDER BY type, name;";

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
                    SqlValueKind.Integer => value.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture),
                    SqlValueKind.Real => value.AsReal().ToString(System.Globalization.CultureInfo.InvariantCulture),
                    _ => value.AsText(),
                });
            }

            rows.Add(string.Join("|", values));
        }

        return rows.ToArray();
    }

    private static string[] ExplainOpcodes(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var opcodes = new List<string>();
        while (statement.Step() == StatementStepResult.Row)
            opcodes.Add(statement.GetValue(1).AsText());

        return opcodes.ToArray();
    }
}
