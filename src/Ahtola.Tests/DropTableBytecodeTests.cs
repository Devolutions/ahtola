using System.Globalization;
using Ahtola.Core;
using Ahtola.Core.Compilation;
using Ahtola.Core.Execution;
using Ahtola.Core.Parsing;
using Ahtola.Core.Storage;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// End-to-end behavior of the compiled <c>DROP TABLE</c> program: what it removes, what it leaves alone,
/// what survives a rollback or a reopen, and what <c>EXPLAIN</c> reports without running any of it.
/// </summary>
public sealed class DropTableBytecodeTests
{
    // ---------------------------------------------------------------- explain

    [Test]
    public void ExplainDropTableDescribesTheScanDestroyProgramWithoutMutatingAnything()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT);");
        Execute(connection, "CREATE INDEX t_a ON t(a);");
        Execute(connection, "INSERT INTO t VALUES (1, 'x');");
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();

        var opcodes = ExplainOpcodes(connection, "EXPLAIN DROP TABLE t;");

        opcodes.Should().Contain(
            ["Rewind", "Column", "Compare", "Delete", "Next", "Destroy", "DropTable", "SetCookie", "Halt"]);
        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
        ReadRows(connection, SchemaQuery).Should().Equal("index|t_a|t", "table|t|t");
        ReadScalar(connection, "SELECT COUNT(*) FROM t;").AsInteger().Should().Be(1);
    }

    [Test]
    public void ExplainDropTableDescribesTheSequenceCleanupWithoutClearingTheWatermark()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, v TEXT);");
        Execute(connection, "INSERT INTO t(v) VALUES ('a'), ('b');");

        var opcodes = ExplainOpcodes(connection, "EXPLAIN DROP TABLE t;");

        opcodes.Should().Contain(["Last", "Prev", "Delete", "Destroy", "DropTable", "SetCookie"]);
        ReadScalar(connection, "SELECT seq FROM sqlite_sequence WHERE name = 't';").AsInteger().Should().Be(2);
    }

    // ------------------------------------------------------------ happy paths

    [Test]
    public void DropTableRemovesTheRowOfEveryObjectItTakesWithIt()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT UNIQUE);");
        Execute(connection, "CREATE INDEX t_a ON t(a);");
        Execute(connection, "CREATE TRIGGER t_after AFTER INSERT ON t BEGIN SELECT 1; END;");
        Execute(connection, "CREATE TABLE other(a INTEGER);");
        Execute(connection, "CREATE INDEX other_a ON other(a);");
        Execute(connection, "CREATE TRIGGER other_after AFTER INSERT ON other BEGIN SELECT 1; END;");

        Execute(connection, "DROP TABLE t;");

        ReadRows(connection, SchemaQuery).Should().Equal(
            "index|other_a|other",
            "table|other|other",
            "trigger|other_after|other");
    }

    [Test]
    public void DropTableBumpsTheSchemaCookieExactlyOnceHoweverManyObjectsItRemoves()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, v TEXT UNIQUE);");
        Execute(connection, "CREATE INDEX t_v ON t(v);");
        Execute(connection, "CREATE TRIGGER t_after AFTER INSERT ON t BEGIN SELECT 1; END;");
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();

        Execute(connection, "DROP TABLE t;");

        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion + 1);
    }

    [Test]
    public void DropTableClearsTheSequenceWatermarkSoAReusedNameStartsOver()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, v TEXT);");
        Execute(connection, "INSERT INTO t(v) VALUES ('a'), ('b');");
        ReadScalar(connection, "SELECT seq FROM sqlite_sequence WHERE name = 't';").AsInteger().Should().Be(2);

        Execute(connection, "DROP TABLE t;");

        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_sequence WHERE name = 't';")
            .AsInteger()
            .Should()
            .Be(0);
        // sqlite_sequence itself outlives the table whose watermark it held.
        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'sqlite_sequence';")
            .AsInteger()
            .Should()
            .Be(1);

        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, v TEXT);");
        Execute(connection, "INSERT INTO t(v) VALUES ('c');");
        ReadScalar(connection, "SELECT id FROM t;").AsInteger().Should().Be(1);
    }

    [Test]
    public void DropTableLeavesTheWatermarkOfEveryOtherSequenceAlone()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE kept(id INTEGER PRIMARY KEY AUTOINCREMENT, v TEXT);");
        Execute(connection, "CREATE TABLE dropped(id INTEGER PRIMARY KEY AUTOINCREMENT, v TEXT);");
        Execute(connection, "INSERT INTO kept(v) VALUES ('a'), ('b'), ('c');");
        Execute(connection, "INSERT INTO dropped(v) VALUES ('x');");

        Execute(connection, "DROP TABLE dropped;");

        ReadRows(connection, "SELECT name, seq FROM sqlite_sequence ORDER BY name;").Should().Equal("kept|3");
    }

    [Test]
    public void DropTableRemovesTheImplicitSequenceBackingTable()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, v TEXT);");
        var backingName = EmbeddedDatabase.GetAutoIncrementSequenceBackingTableName("t");
        ReadScalar(connection, $"SELECT COUNT(*) FROM sqlite_schema WHERE name = '{backingName}';")
            .AsInteger()
            .Should()
            .Be(1);

        Execute(connection, "DROP TABLE t;");

        ReadScalar(connection, $"SELECT COUNT(*) FROM sqlite_schema WHERE name = '{backingName}';")
            .AsInteger()
            .Should()
            .Be(0);
    }

    [Test]
    public void DropTableRemovesOnlyItsOwnChangeCaptureVersionEntry()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "PRAGMA capture_data_changes_conn('full');");
        Execute(connection, "CREATE TABLE other_cdc(change_id INTEGER PRIMARY KEY, payload BLOB);");
        Execute(connection, "INSERT INTO turso_cdc_version VALUES ('other_cdc', 'v2');");

        Execute(connection, "DROP TABLE turso_cdc;");

        ReadRows(connection, "SELECT table_name FROM turso_cdc_version ORDER BY table_name;")
            .Should()
            .Equal("other_cdc");
    }

    [Test]
    public void DropTableLeavesLastInsertRowidAndChangesUntouched()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE seed(id INTEGER PRIMARY KEY, v TEXT);");
        Execute(connection, "CREATE TABLE later(a INTEGER);");
        Execute(connection, "INSERT INTO later VALUES (1), (2);");
        Execute(connection, "INSERT INTO seed VALUES (42, 'x');");

        Execute(connection, "DROP TABLE later;");

        ReadScalar(connection, "SELECT last_insert_rowid();").AsInteger().Should().Be(42);
        ReadScalar(connection, "SELECT changes();").AsInteger().Should().Be(1);
    }

    [Test]
    public void DropTableResolvesTheStoredNameCaseInsensitively()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE MixedCase(a INTEGER);");
        Execute(connection, "CREATE INDEX MixedIndex ON MixedCase(a);");

        Execute(connection, "DROP TABLE mIxEdCaSe;");

        ReadRows(connection, SchemaQuery).Should().BeEmpty();
    }

    [Test]
    public void DropTableIfExistsOnAMissingTableChangesNothing()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();

        Execute(connection, "DROP TABLE IF EXISTS missing;");

        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
        ReadRows(connection, SchemaQuery).Should().Equal("table|t|t");
    }

    [TestCase("DROP TABLE missing;", "no such table: missing")]
    [TestCase("DROP TABLE v;", "use DROP VIEW to delete view v")]
    [TestCase("DROP TABLE sqlite_sequence;", "table sqlite_sequence may not be dropped")]
    public void RejectedDropTableLeavesTheSchemaExactlyAsItWas(string sql, string message)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, a INTEGER);");
        Execute(connection, "CREATE VIEW v AS SELECT a FROM t;");
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();
        var schemaRows = ReadRows(connection, SchemaQuery);

        Action drop = () => Execute(connection, sql);

        drop.Should().Throw<EmbeddedSqlException>().WithMessage(message);
        ReadRows(connection, SchemaQuery).Should().Equal(schemaRows);
        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
    }

    // -------------------------------------------------------- foreign keys

    [Test]
    public void DropTableFiresTheParentActionsItsChildrenDeclared()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "PRAGMA foreign_keys = ON;");
        Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY);");
        Execute(
            connection,
            "CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id) ON DELETE CASCADE);");
        Execute(connection, "INSERT INTO parent VALUES (1), (2);");
        Execute(connection, "INSERT INTO child VALUES (10, 1), (20, 2);");

        Execute(connection, "DROP TABLE parent;");

        ReadScalar(connection, "SELECT COUNT(*) FROM child;").AsInteger().Should().Be(0);
        ReadRows(connection, SchemaQuery).Should().Equal("table|child|child");
    }

    [Test]
    public void ARestrictedDropTableLeavesBothTheSchemaAndTheCascadeUnapplied()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "PRAGMA foreign_keys = ON;");
        Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY);");
        Execute(
            connection,
            "CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id) ON DELETE RESTRICT);");
        Execute(connection, "INSERT INTO parent VALUES (1);");
        Execute(connection, "INSERT INTO child VALUES (10, 1);");
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();

        Action drop = () => Execute(connection, "DROP TABLE parent;");

        drop.Should().Throw<EmbeddedSqlException>();
        ReadScalar(connection, "SELECT COUNT(*) FROM parent;").AsInteger().Should().Be(1);
        ReadScalar(connection, "SELECT COUNT(*) FROM child;").AsInteger().Should().Be(1);
        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
    }

    // ------------------------------------------------- atomicity and rollback

    [Test]
    public void AFailingDropTableProgramLeavesTheCatalogAndItsRowsExactlyAsTheyWere()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, v TEXT);");
        Execute(connection, "CREATE INDEX t_v ON t(v);");
        Execute(connection, "CREATE TRIGGER t_after AFTER INSERT ON t BEGIN SELECT 1; END;");
        Execute(connection, "INSERT INTO t(v) VALUES ('a'), ('b');");
        var schemaRows = ReadRows(connection, SchemaQuery);
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();

        // A program compiled against the live schema and then run against a stage that no longer holds
        // the trigger fails at its DropTrigger eviction — after the row deletes, the destroys and the
        // sequence scan have already been staged. Nothing it staged may reach the live catalog, whose
        // table instances the stage shares.
        var compiled = CompileDropTable(database, "DROP TABLE t;", schemaVersion);
        var stage = CreateStage(database, dropTriggersFromOverlay: true);
        var operations = new ManagedSchemaOperations(stage);
        compiled.Bind(operations);
        var (cursorSources, writeTargets) = compiled.CreateBindings(stage, CancellationToken.None);
        using var runtime = ResumableStatement.CreateWithSchemaContext(
            compiled.Program,
            new VdbeSchemaExecutionContext(operations),
            cursorSources,
            writeTargets);

        Action run = () => runtime.StepResumable(CancellationToken.None);

        run.Should().Throw<VdbeSchemaExecutionException>();
        stage.Discard();
        ReadRows(connection, SchemaQuery).Should().Equal(schemaRows);
        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
        ReadScalar(connection, "SELECT COUNT(*) FROM t;").AsInteger().Should().Be(2);
        ReadScalar(connection, "SELECT seq FROM sqlite_sequence WHERE name = 't';").AsInteger().Should().Be(2);
    }

    [Test]
    public void ADropTableProgramStagesEveryRootItRetiresRatherThanTouchingStorage()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT UNIQUE);");
        Execute(connection, "CREATE INDEX t_a ON t(a);");

        var compiled = CompileDropTable(database, "DROP TABLE t;");
        var stage = CreateStage(database);
        var expectedRoots = stage.Rows.Rows
            .Where(row => row.TableName == "t")
            .Select(row => row.RootPage)
            .ToArray();
        var operations = new ManagedSchemaOperations(stage);
        compiled.Bind(operations);
        var (cursorSources, writeTargets) = compiled.CreateBindings(stage, CancellationToken.None);
        using var runtime = ResumableStatement.CreateWithSchemaContext(
            compiled.Program,
            new VdbeSchemaExecutionContext(operations),
            cursorSources,
            writeTargets);

        runtime.StepResumable(CancellationToken.None).Should().Be(ResumableStatementStepResult.Done);

        // The table and both of its indexes: every root the schema rows carried is recorded as retired,
        // and the staged rows and catalog agree that they are gone.
        expectedRoots.Should().HaveCount(3);
        stage.RootPlan.DestroyedRoots.Should().BeEquivalentTo(expectedRoots);
        stage.Rows.Rows.Should().NotContain(row => row.TableName == "t");
        stage.Catalog.Tables.Should().NotContainKey("t");
        stage.ValidateRowsDescribeCatalog();
        stage.Discard();

        // Nothing the program staged reached the live catalog.
        ReadRows(connection, SchemaQuery).Should().Equal("index|sqlite_autoindex_t_1|t", "index|t_a|t", "table|t|t");
    }

    [Test]
    public void RollingBackATransactionRestoresADroppedTableAndItsRows()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, v TEXT);");
        Execute(connection, "CREATE INDEX t_v ON t(v);");
        Execute(connection, "CREATE TRIGGER t_after AFTER INSERT ON t BEGIN SELECT 1; END;");
        Execute(connection, "INSERT INTO t(v) VALUES ('a'), ('b');");
        var schemaRows = ReadRows(connection, SchemaQuery);
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();

        Execute(connection, "BEGIN;");
        Execute(connection, "DROP TABLE t;");
        Execute(connection, "ROLLBACK;");

        ReadRows(connection, SchemaQuery).Should().Equal(schemaRows);
        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
        ReadRows(connection, "SELECT id, v FROM t ORDER BY id;").Should().Equal("1|a", "2|b");
        ReadScalar(connection, "SELECT seq FROM sqlite_sequence WHERE name = 't';").AsInteger().Should().Be(2);
    }

    [Test]
    public void SavepointRollbackDiscardsADropTable()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE kept(a INTEGER);");
        Execute(connection, "CREATE TABLE transient(a INTEGER);");
        Execute(connection, "INSERT INTO transient VALUES (1);");

        Execute(connection, "BEGIN;");
        Execute(connection, "SAVEPOINT before_drop;");
        Execute(connection, "DROP TABLE transient;");
        Execute(connection, "ROLLBACK TO before_drop;");
        Execute(connection, "COMMIT;");

        ReadRows(connection, SchemaQuery).Should().Equal("table|kept|kept", "table|transient|transient");
        ReadScalar(connection, "SELECT COUNT(*) FROM transient;").AsInteger().Should().Be(1);
    }

    [Test]
    public void RollingBackAMethodIndexTableDropRestoresTheIndex()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, embedding BLOB);");
        Execute(connection, "INSERT INTO docs VALUES (1, vector32('[1,0,0,0]')), (2, vector32('[0,1,0,0]'));");
        Execute(connection, "CREATE INDEX docs_knn ON docs USING vector (embedding) WITH (dims = 4, min_rows = 0);");
        var schemaRows = ReadRows(connection, SchemaQuery);

        Execute(connection, "BEGIN;");
        Execute(connection, "DROP TABLE docs;");
        Execute(connection, "ROLLBACK;");

        ReadRows(connection, SchemaQuery).Should().Equal(schemaRows);
        ReadRows(
                connection,
                "SELECT id FROM docs ORDER BY vector_distance_l2(embedding, vector32('[0,1,0,0]')) LIMIT 1;")
            .Should()
            .Equal("2");
    }

    [Test]
    public void DroppingATableWithAMethodIndexRetiresBothAndFreesTheirNames()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, embedding BLOB);");
        Execute(connection, "INSERT INTO docs VALUES (1, vector32('[1,0,0,0]'));");
        Execute(connection, "CREATE INDEX docs_knn ON docs USING vector (embedding) WITH (dims = 4, min_rows = 0);");

        Execute(connection, "DROP TABLE docs;");

        ReadRows(connection, SchemaQuery).Should().BeEmpty();

        // Both names are free again, and the method index can be rebuilt from a clean slate.
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, embedding BLOB);");
        Execute(connection, "INSERT INTO docs VALUES (7, vector32('[0,1,0,0]'));");
        Execute(connection, "CREATE INDEX docs_knn ON docs USING vector (embedding) WITH (dims = 4, min_rows = 0);");
        ReadRows(
                connection,
                "SELECT id FROM docs ORDER BY vector_distance_l2(embedding, vector32('[0,1,0,0]')) LIMIT 1;")
            .Should()
            .Equal("7");
    }

    // --------------------------------------------------------------- durability

    [Test]
    public void ACommittedDropTableSurvivesAFileReopenAndReclaimsItsPages()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "drop-table-bytecode.db";
        const string payload = "padding that only the dropped table holds";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE kept(a INTEGER, b TEXT);");
            Execute(connection, "CREATE TABLE dropped(a INTEGER, b TEXT);");
            Execute(connection, "CREATE INDEX dropped_a ON dropped(a);");
            Execute(connection, "CREATE TRIGGER dropped_after AFTER INSERT ON dropped BEGIN SELECT 1; END;");
            Execute(connection, "INSERT INTO kept VALUES (1, 'x');");
            for (var value = 0; value < 256; value++)
                Execute(connection, $"INSERT INTO dropped VALUES ({value}, '{payload}');");
            FileContains(fileSystem, path, payload).Should().BeTrue();

            Execute(connection, "DROP TABLE dropped;");

            // The retired roots are reclaimed by the commit rewriting the database without them, so the
            // dropped table's pages hold none of its payload afterwards.
            FileContains(fileSystem, path, payload).Should().BeFalse();
        }

        using var reopenedDatabase = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopened = reopenedDatabase.Connect();
        ReadRows(reopened, SchemaQuery).Should().Equal("table|kept|kept");
        ReadRows(reopened, "SELECT a, b FROM kept;").Should().Equal("1|x");
    }

    [Test]
    public void ARolledBackFileBackedDropTableDoesNotSurviveAReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "drop-table-bytecode-rollback.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, v TEXT);");
            Execute(connection, "INSERT INTO t(v) VALUES ('a');");
            Execute(connection, "BEGIN;");
            Execute(connection, "DROP TABLE t;");
            Execute(connection, "ROLLBACK;");
        }

        using var reopenedDatabase = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopened = reopenedDatabase.Connect();
        ReadRows(reopened, "SELECT id, v FROM t;").Should().Equal("1|a");
        ReadScalar(reopened, "SELECT seq FROM sqlite_sequence WHERE name = 't';").AsInteger().Should().Be(1);
    }

    [Test]
    public void ACommittedAutoIncrementDropSurvivesAReopenWithoutItsWatermark()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "drop-table-bytecode-sequence.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, v TEXT);");
            Execute(connection, "CREATE TABLE kept(id INTEGER PRIMARY KEY AUTOINCREMENT, v TEXT);");
            Execute(connection, "INSERT INTO t(v) VALUES ('a'), ('b'), ('c');");
            Execute(connection, "INSERT INTO kept(v) VALUES ('k');");
            Execute(connection, "DROP TABLE t;");
        }

        using var reopenedDatabase = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopened = reopenedDatabase.Connect();
        ReadRows(reopened, "SELECT name, seq FROM sqlite_sequence ORDER BY name;").Should().Equal("kept|1");
        ReadRows(reopened, SchemaQuery)
            .Should()
            .NotContain(row => row.Contains("|t|", StringComparison.Ordinal));

        // The name is reusable and its sequence starts over.
        Execute(reopened, "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, v TEXT);");
        Execute(reopened, "INSERT INTO t(v) VALUES ('fresh');");
        ReadScalar(reopened, "SELECT id FROM t;").AsInteger().Should().Be(1);
    }

    [Test]
    public void DropTableInATemporarySchemaLeavesTheMainSchemaAlone()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE TEMP TABLE t(a INTEGER);");

        Execute(connection, "DROP TABLE temp.t;");

        ReadRows(connection, SchemaQuery).Should().Equal("table|t|t");
        ReadRows(connection, "SELECT type, name FROM temp.sqlite_schema;").Should().BeEmpty();
    }

    [Test]
    public void CancellingADropTableLeavesTheSchemaAndItsRowsAlone()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "PRAGMA foreign_keys = ON;");
        Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY);");
        Execute(
            connection,
            "CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id) ON DELETE CASCADE);");
        Execute(connection, "INSERT INTO parent VALUES (1);");
        Execute(connection, "INSERT INTO child VALUES (10, 1);");
        var schemaRows = ReadRows(connection, SchemaQuery);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Action drop = () =>
        {
            using var statement = connection.Prepare("DROP TABLE parent;");
            statement.Step(cancellation.Token);
        };

        drop.Should().Throw<OperationCanceledException>();
        // The cascade the preflight fired is undone with the program it was preparing for.
        ReadRows(connection, SchemaQuery).Should().Equal(schemaRows);
        ReadScalar(connection, "SELECT COUNT(*) FROM parent;").AsInteger().Should().Be(1);
        ReadScalar(connection, "SELECT COUNT(*) FROM child;").AsInteger().Should().Be(1);
    }

    private const string SchemaQuery =
        "SELECT type, name, tbl_name FROM sqlite_schema ORDER BY type, name;";

    private static CompiledSchemaProgram CompileDropTable(
        EmbeddedDatabase database,
        string sql,
        long schemaVersion = 0)
        => DdlStatementCompiler.CompileDropTable(
            (DropTableStatement)SqlParser.Parse(sql, SqlParameterMap.Parse(sql)),
            new DdlCompilationContext(database.LiveCatalog, schemaVersion, static _ => { }, static _ => { }));

    /// <summary>
    /// Builds the stage a schema program runs against exactly as the connection does: fresh dictionaries
    /// over the live catalog's own object instances, so an effect that mutated one in place instead of
    /// staging it would be visible to <paramref name="database"/> immediately.
    /// </summary>
    private static ManagedSchemaStage CreateStage(
        EmbeddedDatabase database,
        bool dropTriggersFromOverlay = false)
    {
        var live = database.LiveCatalog;
        return ManagedSchemaStage.Create(
            "main",
            () => new EmbeddedDatabase.SchemaCatalog(
                new Dictionary<string, EmbeddedTable>(live.Tables, StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, ViewDefinition>(live.Views, StringComparer.OrdinalIgnoreCase),
                dropTriggersFromOverlay
                    ? new Dictionary<string, TriggerDefinition>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, TriggerDefinition>(live.Triggers, StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, EmbeddedDatabase.VirtualTableDefinition>(
                    live.VirtualTables,
                    StringComparer.OrdinalIgnoreCase)),
            database.GetPragmaHeaderMetadata(),
            ManagedSchemaFixedCookies.Default);
    }

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

    /// <summary>Whether the persisted database still holds <paramref name="text"/> anywhere.</summary>
    private static bool FileContains(InMemoryFileSystem fileSystem, string path, string text)
    {
        using var file = fileSystem.OpenFile(path, FileOpenMode.OpenOrCreate, readOnly: true);
        var bytes = new byte[file.Length];
        file.Read(0, bytes);
        return bytes.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(text)) >= 0;
    }
}
