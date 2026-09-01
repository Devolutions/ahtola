using System.Globalization;
using Ahtola.Core;
using Ahtola.Core.Storage;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// End-to-end behavior of the compiled view, trigger and virtual-table schema programs: what they do when
/// they succeed, what they leave behind when they fail or roll back, what survives a reopen, how many
/// times a module's lifecycle hooks run, and what <c>EXPLAIN</c> reports without running any of it.
/// </summary>
public sealed class SchemaObjectBytecodeTests
{
    // ---------------------------------------------------------------- explain

    [Test]
    public void ExplainCreateViewDescribesTheSchemaProgramWithoutMutatingAnything()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();

        var opcodes = ExplainOpcodes(connection, "EXPLAIN CREATE VIEW v AS SELECT a FROM t;");

        opcodes.Should().Contain(["NewRowid", "MakeRecord", "Insert", "ParseSchema", "SetCookie", "Halt"]);
        opcodes.Should().NotContain("CreateBtree");
        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'v';").AsInteger().Should().Be(0);
    }

    [Test]
    public void ExplainDropViewDescribesTheScanWithoutMutatingAnything()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE VIEW v AS SELECT a FROM t;");
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();

        var opcodes = ExplainOpcodes(connection, "EXPLAIN DROP VIEW v;");

        opcodes.Should().Contain(["Rewind", "Column", "Compare", "Delete", "Next", "SetCookie", "DropView", "Halt"]);
        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'v';").AsInteger().Should().Be(1);
    }

    [Test]
    public void ExplainCreateTriggerDescribesItsProgramWithoutRunningItsBody()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE TABLE audit(a INTEGER);");
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();

        var opcodes = ExplainOpcodes(
            connection,
            "EXPLAIN CREATE TRIGGER tr AFTER INSERT ON t BEGIN INSERT INTO audit VALUES (1); END;");

        opcodes.Should().Contain(["NewRowid", "MakeRecord", "Insert", "SetCookie", "ParseSchema", "Halt"]);
        // Describing a trigger must not execute the body it stores.
        ReadScalar(connection, "SELECT COUNT(*) FROM audit;").AsInteger().Should().Be(0);
        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'tr';").AsInteger().Should().Be(0);
        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
    }

    [Test]
    public void ExplainDropTriggerDescribesTheScanWithoutMutatingAnything()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE TRIGGER tr AFTER INSERT ON t BEGIN SELECT 1; END;");
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();

        var opcodes = ExplainOpcodes(connection, "EXPLAIN DROP TRIGGER tr;");

        opcodes.Should().Contain(["Rewind", "Delete", "SetCookie", "DropTrigger", "Halt"]);
        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'tr';").AsInteger().Should().Be(1);
        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
    }

    [Test]
    public void ExplainVirtualTableLifecycleNeverInvokesTheModule()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE VIRTUAL TABLE docs USING fts5(body);");
        Execute(connection, "INSERT INTO docs(body) VALUES ('hello world');");
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();

        ExplainOpcodes(connection, "EXPLAIN CREATE VIRTUAL TABLE more USING fts5(body);")
            .Should()
            .Contain(["VCreate", "MakeRecord", "Insert", "SetCookie", "ParseSchema", "Halt"]);
        ExplainOpcodes(connection, "EXPLAIN DROP TABLE docs;")
            .Should()
            .Contain(["Rewind", "Delete", "SetCookie", "VDestroy", "DropTable", "Halt"]);
        ExplainOpcodes(connection, "EXPLAIN ALTER TABLE docs RENAME TO papers;")
            .Should()
            .Contain(["VRename", "Delete", "MakeRecord", "Insert", "SetCookie", "RenameTable", "Halt"]);

        // None of the three descriptions may create, destroy, or rename a module instance.
        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
        ReadScalar(connection, "SELECT COUNT(*) FROM docs;").AsInteger().Should().Be(1);
        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'more';").AsInteger().Should().Be(0);
    }

    // ----------------------------------------------------------- happy paths

    [Test]
    public void SchemaObjectDdlAdvancesTheSchemaCookieExactlyOncePerStatement()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        var schemaVersion = 0L;

        foreach (var sql in new[]
                 {
                     "CREATE TABLE t(a INTEGER);",
                     "CREATE VIEW v AS SELECT a FROM t;",
                     "CREATE TRIGGER tr AFTER INSERT ON t BEGIN SELECT 1; END;",
                     "CREATE VIRTUAL TABLE docs USING fts5(body);",
                     "ALTER TABLE docs RENAME TO papers;",
                     "DROP TABLE papers;",
                     "DROP TRIGGER tr;",
                     "DROP VIEW v;",
                 })
        {
            Execute(connection, sql);
            ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(++schemaVersion);
        }
    }

    [Test]
    public void SchemaObjectDdlLeavesLastInsertRowidAndChangesUntouched()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);");
        Execute(connection, "INSERT INTO t VALUES (42, 'x');");

        Execute(connection, "CREATE VIEW view_t AS SELECT v FROM t;");
        Execute(connection, "CREATE TRIGGER tr AFTER UPDATE ON t BEGIN SELECT 1; END;");
        Execute(connection, "CREATE VIRTUAL TABLE docs USING fts5(body);");
        ReadScalar(connection, "SELECT last_insert_rowid();").AsInteger().Should().Be(42);
        ReadScalar(connection, "SELECT changes();").AsInteger().Should().Be(1);

        Execute(connection, "DROP TABLE docs;");
        Execute(connection, "DROP TRIGGER tr;");
        Execute(connection, "DROP VIEW view_t;");
        ReadScalar(connection, "SELECT last_insert_rowid();").AsInteger().Should().Be(42);
        ReadScalar(connection, "SELECT changes();").AsInteger().Should().Be(1);
    }

    [Test]
    public void AViewCreatedByBytecodeIsImmediatelyQueryable()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT);");
        Execute(connection, "INSERT INTO t VALUES (1, 'x'), (2, 'y');");

        Execute(connection, "CREATE VIEW v (label) AS SELECT b FROM t WHERE a = 2;");

        ReadRows(connection, "SELECT label FROM v;").Should().Equal("y");
        ReadScalar(connection, "SELECT sql FROM sqlite_schema WHERE name = 'v';")
            .AsText()
            .Should()
            .Be("CREATE VIEW v (label) AS SELECT b FROM t WHERE a = 2");
    }

    [Test]
    public void AViewOverAnApplicationDefinedFunctionStaysLegalInMemory()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        connection.RegisterScalarFunction("shout", 1, static arguments =>
            SqlValue.Text(arguments[0].AsText().ToUpperInvariant()));
        Execute(connection, "CREATE TABLE t(a TEXT);");
        Execute(connection, "INSERT INTO t VALUES ('x');");

        // Lowering CREATE VIEW must not promote the file store's persistence-only check into a create-time
        // rejection: an in-memory catalog may hold a callback-dependent view forever.
        Execute(connection, "CREATE VIEW v AS SELECT shout(a) AS loud FROM t;");

        ReadRows(connection, "SELECT loud FROM v;").Should().Equal("X");
    }

    [Test]
    public void TriggersCreatedByBytecodeKeepTheirDeclarationOrderWhenTheyFire()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE TABLE audit(step TEXT);");
        Execute(connection, "CREATE TRIGGER first AFTER INSERT ON t BEGIN INSERT INTO audit VALUES ('first'); END;");
        Execute(connection, "CREATE TRIGGER second AFTER INSERT ON t BEGIN INSERT INTO audit VALUES ('second'); END;");

        Execute(connection, "INSERT INTO t VALUES (1);");

        // SQLite fires row triggers in reverse creation order, so the declaration order the program
        // recorded is what decides which one runs first.
        ReadRows(connection, "SELECT step FROM audit;").Should().Equal("second", "first");
    }

    [Test]
    public void ATriggerHonorsItsWhenClauseAndUpdateOfColumnList()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b INTEGER);");
        Execute(connection, "CREATE TABLE audit(note TEXT);");
        Execute(connection, "INSERT INTO t VALUES (1, 1);");
        Execute(
            connection,
            "CREATE TRIGGER tr AFTER UPDATE OF a ON t WHEN NEW.a > 5 BEGIN INSERT INTO audit VALUES ('fired'); END;");

        Execute(connection, "UPDATE t SET b = 9;");
        ReadScalar(connection, "SELECT COUNT(*) FROM audit;").AsInteger().Should().Be(0);

        Execute(connection, "UPDATE t SET a = 2;");
        ReadScalar(connection, "SELECT COUNT(*) FROM audit;").AsInteger().Should().Be(0);

        Execute(connection, "UPDATE t SET a = 9;");
        ReadRows(connection, "SELECT note FROM audit;").Should().Equal("fired");
    }

    [Test]
    public void AnInsteadOfTriggerOnAViewStillReceivesItsWrites()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE VIEW v AS SELECT a FROM t;");
        Execute(connection, "CREATE TRIGGER tr INSTEAD OF INSERT ON v BEGIN INSERT INTO t VALUES (NEW.a); END;");

        Execute(connection, "INSERT INTO v VALUES (7);");

        ReadRows(connection, "SELECT a FROM t;").Should().Equal("7");
    }

    [Test]
    public void ARecursiveTriggerStillRecursesWhenTheProgramCreatedIt()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "PRAGMA recursive_triggers = ON;");
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(
            connection,
            "CREATE TRIGGER tr AFTER INSERT ON t WHEN NEW.a < 3 BEGIN INSERT INTO t VALUES (NEW.a + 1); END;");

        Execute(connection, "INSERT INTO t VALUES (1);");

        ReadRows(connection, "SELECT a FROM t ORDER BY a;").Should().Equal("1", "2", "3");
    }

    [Test]
    public void DroppingAViewRetiresTheTriggersThatWatchedIt()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE VIEW v AS SELECT a FROM t;");
        Execute(connection, "CREATE TRIGGER on_view INSTEAD OF INSERT ON v BEGIN INSERT INTO t VALUES (NEW.a); END;");
        Execute(connection, "CREATE TRIGGER on_table AFTER INSERT ON t BEGIN SELECT 1; END;");

        Execute(connection, "DROP VIEW v;");

        ReadRows(connection, SchemaQuery).Should().Equal("table|t|t", "trigger|on_table|t");
    }

    [Test]
    public void TriggersAndOtherObjectsKeepSeparateNamespaces()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE VIEW v AS SELECT a FROM t;");

        // A trigger may reuse a table's or a view's name; SQLite only rejects trigger-vs-trigger.
        Execute(connection, "CREATE TRIGGER t AFTER INSERT ON t BEGIN SELECT 1; END;");
        Execute(connection, "CREATE TRIGGER v AFTER INSERT ON t BEGIN SELECT 1; END;");

        Action duplicate = () => Execute(connection, "CREATE TRIGGER t AFTER UPDATE ON t BEGIN SELECT 1; END;");
        duplicate.Should().Throw<EmbeddedSqlException>().WithMessage("trigger t already exists");

        Action viewOverTrigger = () => Execute(connection, "CREATE VIEW t AS SELECT a FROM t;");
        viewOverTrigger.Should().Throw<EmbeddedSqlException>().WithMessage("there is already a table named t");
    }

    [Test]
    public void IfNotExistsAndIfExistsChangeNothingForSchemaObjects()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE VIEW v AS SELECT a FROM t;");
        Execute(connection, "CREATE TRIGGER tr AFTER INSERT ON t BEGIN SELECT 1; END;");
        Execute(connection, "CREATE VIRTUAL TABLE docs USING fts5(body);");
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();
        var rows = ReadRows(connection, SchemaQuery);

        Execute(connection, "CREATE VIEW IF NOT EXISTS v AS SELECT a FROM t;");
        Execute(connection, "CREATE TRIGGER IF NOT EXISTS tr AFTER INSERT ON t BEGIN SELECT 2; END;");
        Execute(connection, "CREATE VIRTUAL TABLE IF NOT EXISTS docs USING fts5(body);");
        Execute(connection, "DROP VIEW IF EXISTS missing_view;");
        Execute(connection, "DROP TRIGGER IF EXISTS missing_trigger;");
        Execute(connection, "DROP TABLE IF EXISTS missing_table;");

        ReadRows(connection, SchemaQuery).Should().Equal(rows);
        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
    }

    [Test]
    public void SchemaObjectNamesResolveCaseInsensitivelyAndKeepTheirStoredCase()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE VIEW MixedView AS SELECT a FROM t;");
        Execute(connection, "CREATE TRIGGER MixedTrigger AFTER INSERT ON t BEGIN SELECT 1; END;");
        Execute(connection, "CREATE VIRTUAL TABLE MixedDocs USING fts5(body);");

        ReadRows(connection, SchemaQuery).Should().Equal(
            "table|t|t",
            "trigger|MixedTrigger|t",
            "view|MixedView|MixedView");

        Execute(connection, "DROP TRIGGER mixedtrigger;");
        Execute(connection, "DROP VIEW mixedview;");
        Execute(connection, "DROP TABLE mixeddocs;");

        ReadRows(connection, SchemaQuery).Should().Equal("table|t|t");
    }

    // ---------------------------------------------------------------- failures

    [Test]
    public void AFailedSchemaObjectStatementLeavesRowsAndCookieUntouched()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE VIEW v AS SELECT a FROM t;");
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();
        var rows = ReadRows(connection, SchemaQuery);

        Action duplicateView = () => Execute(connection, "CREATE VIEW v AS SELECT a FROM t;");
        duplicateView.Should().Throw<EmbeddedSqlException>().WithMessage("view v already exists");

        Action triggerOnMissingTable =
            () => Execute(connection, "CREATE TRIGGER tr AFTER INSERT ON missing BEGIN SELECT 1; END;");
        triggerOnMissingTable.Should().Throw<EmbeddedSqlException>().WithMessage("no such table: missing");

        Action dropMissingTrigger = () => Execute(connection, "DROP TRIGGER missing;");
        dropMissingTrigger.Should().Throw<EmbeddedSqlException>().WithMessage("no such trigger: missing");

        ReadRows(connection, SchemaQuery).Should().Equal(rows);
        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
    }

    [Test]
    public void ATriggerCannotBeCreatedOnAVirtualTable()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE VIRTUAL TABLE docs USING fts5(body);");
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();

        Action create = () => Execute(connection, "CREATE TRIGGER tr AFTER INSERT ON docs BEGIN SELECT 1; END;");

        create.Should().Throw<EmbeddedSqlException>().WithMessage("cannot create triggers on virtual tables");
        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
    }

    [Test]
    public void AVirtualTableRenameOntoAnOccupiedNameLeavesTheModuleAlone()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE taken(a INTEGER);");
        Execute(connection, "CREATE VIRTUAL TABLE docs USING fts5(body);");
        Execute(connection, "INSERT INTO docs(body) VALUES ('hello world');");
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();

        Action rename = () => Execute(connection, "ALTER TABLE docs RENAME TO taken;");

        rename.Should().Throw<EmbeddedSqlException>().WithMessage("there is already an object named taken");
        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
        ReadScalar(connection, "SELECT COUNT(*) FROM docs;").AsInteger().Should().Be(1);
    }

    // ------------------------------------------------------- rollback / durability

    [Test]
    public void RollingBackATransactionDiscardsEverySchemaObjectDirection()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE VIEW stable_view AS SELECT a FROM t;");
        Execute(connection, "CREATE TRIGGER stable_trigger AFTER INSERT ON t BEGIN SELECT 1; END;");
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();
        var rows = ReadRows(connection, SchemaQuery);

        Execute(connection, "BEGIN;");
        Execute(connection, "CREATE VIEW transient_view AS SELECT a FROM t;");
        Execute(connection, "CREATE TRIGGER transient_trigger AFTER UPDATE ON t BEGIN SELECT 1; END;");
        Execute(connection, "DROP VIEW stable_view;");
        Execute(connection, "DROP TRIGGER stable_trigger;");
        Execute(connection, "ROLLBACK;");

        ReadRows(connection, SchemaQuery).Should().Equal(rows);
        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
    }

    [Test]
    public void SavepointRollbackDiscardsSchemaObjectDdl()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");

        Execute(connection, "BEGIN;");
        Execute(connection, "SAVEPOINT before_objects;");
        Execute(connection, "CREATE VIEW v AS SELECT a FROM t;");
        Execute(connection, "CREATE TRIGGER tr AFTER INSERT ON t BEGIN SELECT 1; END;");
        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('v', 'tr');")
            .AsInteger()
            .Should()
            .Be(2);
        Execute(connection, "ROLLBACK TO before_objects;");
        Execute(connection, "RELEASE before_objects;");
        Execute(connection, "COMMIT;");

        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('v', 'tr');")
            .AsInteger()
            .Should()
            .Be(0);
    }

    [Test]
    public void CommittedSchemaObjectDdlSurvivesAFileReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "schema-object-bytecode.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT);");
            Execute(connection, "INSERT INTO t VALUES (1, 'x');");
            Execute(connection, "CREATE VIEW keep_view AS SELECT b FROM t;");
            Execute(connection, "CREATE VIEW gone_view AS SELECT a FROM t;");
            Execute(connection, "CREATE TRIGGER keep_trigger AFTER INSERT ON t BEGIN SELECT 1; END;");
            Execute(connection, "CREATE TRIGGER gone_trigger AFTER UPDATE ON t BEGIN SELECT 1; END;");
            Execute(connection, "DROP VIEW gone_view;");
            Execute(connection, "DROP TRIGGER gone_trigger;");
        }

        using var reopenedDatabase = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopened = reopenedDatabase.Connect();
        ReadRows(reopened, SchemaQuery).Should().Equal(
            "table|t|t",
            "trigger|keep_trigger|t",
            "view|keep_view|keep_view");
        ReadRows(reopened, "SELECT b FROM keep_view;").Should().Equal("x");
    }

    [Test]
    public void ARolledBackFileBackedSchemaObjectDoesNotSurviveAReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "schema-object-bytecode-rollback.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(a INTEGER);");
            Execute(connection, "CREATE VIEW stable_view AS SELECT a FROM t;");
            Execute(connection, "BEGIN;");
            Execute(connection, "CREATE VIEW transient_view AS SELECT a FROM t;");
            Execute(connection, "CREATE TRIGGER transient_trigger AFTER INSERT ON t BEGIN SELECT 1; END;");
            Execute(connection, "DROP VIEW stable_view;");
            Execute(connection, "ROLLBACK;");
        }

        using var reopenedDatabase = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopened = reopenedDatabase.Connect();
        ReadRows(reopened, SchemaQuery).Should().Equal("table|t|t", "view|stable_view|stable_view");
    }

    // ------------------------------------------------- virtual resource lifetime

    [Test]
    public void ACreatedVirtualTableIsUsableAndRetainsItsModuleState()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE VIRTUAL TABLE docs USING fts5(body);");
        Execute(connection, "INSERT INTO docs(body) VALUES ('hello world'), ('goodbye world');");

        ReadScalar(connection, "SELECT COUNT(*) FROM docs WHERE docs MATCH 'hello';").AsInteger().Should().Be(1);
    }

    [Test]
    public void ARolledBackCreateVirtualTableLeavesNoTableAndNoLiveInstance()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();

        Execute(connection, "BEGIN;");
        Execute(connection, "CREATE VIRTUAL TABLE docs USING fts5(body);");
        Execute(connection, "INSERT INTO docs(body) VALUES ('hello');");
        Execute(connection, "ROLLBACK;");

        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
        Action query = () => Execute(connection, "SELECT COUNT(*) FROM docs;");
        query.Should().Throw<EmbeddedSqlException>();

        // The name is free again, and creating it a second time works from a clean module state.
        Execute(connection, "CREATE VIRTUAL TABLE docs USING fts5(body);");
        ReadScalar(connection, "SELECT COUNT(*) FROM docs;").AsInteger().Should().Be(0);
    }

    [Test]
    public void ADroppedVirtualTableIsGoneFromTheSchemaAndItsNameIsReusable()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE VIRTUAL TABLE docs USING fts5(body);");
        Execute(connection, "INSERT INTO docs(body) VALUES ('hello world');");

        Execute(connection, "DROP TABLE docs;");

        ReadRows(connection, SchemaQuery).Should().BeEmpty();
        Execute(connection, "CREATE VIRTUAL TABLE docs USING fts5(body);");
        ReadScalar(connection, "SELECT COUNT(*) FROM docs;").AsInteger().Should().Be(0);
    }

    [Test]
    public void ARenamedVirtualTableKeepsItsContentAndItsDependentView()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE VIRTUAL TABLE docs USING fts5(body);");
        Execute(connection, "INSERT INTO docs(body) VALUES ('hello world');");
        Execute(connection, "CREATE VIEW docs_view AS SELECT body FROM docs;");

        Execute(connection, "ALTER TABLE docs RENAME TO papers;");

        ReadRows(connection, "SELECT body FROM papers;").Should().Equal("hello world");
        ReadRows(connection, "SELECT body FROM docs_view;").Should().Equal("hello world");
        ReadScalar(connection, "SELECT sql FROM sqlite_schema WHERE name = 'docs_view';")
            .AsText()
            .Should()
            .Be("CREATE VIEW docs_view AS SELECT body FROM papers");
        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'docs';").AsInteger().Should().Be(0);
    }

    [Test]
    public void AVirtualTableSurvivesRenameAndReopenWithItsPersistedState()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "schema-object-bytecode-vtab.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE VIRTUAL TABLE docs USING fts5(body);");
            Execute(connection, "INSERT INTO docs(body) VALUES ('hello world');");
            Execute(connection, "ALTER TABLE docs RENAME TO papers;");
        }

        using var reopenedDatabase = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopened = reopenedDatabase.Connect();
        ReadRows(reopened, "SELECT body FROM papers;").Should().Equal("hello world");
        ReadScalar(reopened, "SELECT COUNT(*) FROM papers WHERE papers MATCH 'hello';").AsInteger().Should().Be(1);
    }

    [Test]
    public void ARolledBackVirtualTableDropRestoresTheTableAndItsRows()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "schema-object-bytecode-vtab-rollback.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE VIRTUAL TABLE docs USING fts5(body);");
            Execute(connection, "INSERT INTO docs(body) VALUES ('hello world');");
            Execute(connection, "BEGIN;");
            Execute(connection, "DROP TABLE docs;");
            Execute(connection, "ROLLBACK;");
        }

        using var reopenedDatabase = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopened = reopenedDatabase.Connect();
        ReadRows(reopened, "SELECT body FROM docs;").Should().Equal("hello world");
    }

    [Test]
    public void TheVirtualTableLifecycleRunsEachHookExactlyOnceAcrossCreateRenameAndDrop()
    {
        _ = LifecycleModule;
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, $"CREATE VIRTUAL TABLE tracked USING {LifecycleModuleName};");
        LifecycleModule.InstancesFor("tracked").Should().NotBeEmpty();
        LifecycleModule.InstancesFor("tracked").Sum(instance => instance.DestroyCalls).Should().Be(0);

        // VRename renames the live instance in place rather than the catalog building a replacement, so
        // exactly one instance ever sees the rename and it is neither destroyed nor released by it.
        Execute(connection, "ALTER TABLE tracked RENAME TO renamed;");
        var renamed = LifecycleModule.InstancesFor("tracked")
            .Should()
            .ContainSingle(instance => instance.RenameCalls == 1)
            .Subject;
        renamed.RenamedTo.Should().Be("renamed");
        renamed.DisconnectCalls.Should().Be(0);
        renamed.DestroyCalls.Should().Be(0);

        Execute(connection, "DROP TABLE renamed;");
        // VDestroy is the only hook that retires module state, and DropTable evicts the catalog entry
        // without releasing the instance a second time. The catalog may have snapshotted the table in
        // between, so the invariant is over every instance the module ever handed out for it.
        var lifetime = LifecycleModule.InstancesFor("tracked")
            .Concat(LifecycleModule.InstancesFor("renamed"))
            .ToArray();
        lifetime.Sum(instance => instance.DestroyCalls).Should().Be(1);
        lifetime.Sum(instance => instance.RenameCalls).Should().Be(1);
        lifetime.Should().AllSatisfy(static instance =>
            (instance.DestroyCalls + instance.DisconnectCalls).Should().BeLessThanOrEqualTo(1));
    }

    [Test]
    public void ARolledBackCreateDisconnectsTheInstanceItProducedExactlyOnce()
    {
        _ = LifecycleModule;
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE anchor(a INTEGER);");

        Execute(connection, "BEGIN;");
        Execute(connection, $"CREATE VIRTUAL TABLE rolled_back USING {LifecycleModuleName};");
        Execute(connection, "ROLLBACK;");

        var created = LifecycleModule.InstancesFor("rolled_back").Should().ContainSingle().Subject;
        created.DisconnectCalls.Should().Be(1);
        created.DestroyCalls.Should().Be(0);
        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'rolled_back';")
            .AsInteger()
            .Should()
            .Be(0);
    }

    [Test]
    public void ACancelledSchemaObjectStatementPublishesNothing()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        using var statement = connection.Prepare("CREATE VIEW v AS SELECT a FROM t;");
        Action step = () => statement.Step(cancellation.Token);

        step.Should().Throw<OperationCanceledException>();
        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'v';").AsInteger().Should().Be(0);
    }

    private const string SchemaQuery =
        "SELECT type, name, tbl_name FROM sqlite_schema ORDER BY type, name;";

    private const string LifecycleModuleName = "schema_object_lifecycle_probe";

    private static readonly LifecycleProbeModule LifecycleModule = RegisterLifecycleModule();

    private static LifecycleProbeModule RegisterLifecycleModule()
    {
        var module = new LifecycleProbeModule();
        ManagedVirtualTableModuleRegistry.Register(module);
        return module;
    }

    /// <summary>
    /// A module whose only job is to count the lifecycle callbacks the schema programs invoke, so a
    /// create/rename/drop sequence can be proven to reach each hook exactly once.
    /// </summary>
    private sealed class LifecycleProbeModule : ManagedVirtualTableModule
    {
        public override string Name => LifecycleModuleName;

        public List<LifecycleProbeTable> Instances { get; } = [];

        public override ManagedVirtualTable Create(ManagedVirtualTableCreateContext context)
            => Track(context.TableName);

        public override ManagedVirtualTable Create(
            ManagedVirtualTableCreateContext context,
            ManagedVirtualTablePersistencePayload payload)
            => Track(context.TableName);

        /// <summary>Every instance created for <paramref name="tableName"/>, in creation order.</summary>
        public LifecycleProbeTable[] InstancesFor(string tableName)
        {
            lock (Instances)
            {
                return [.. Instances.Where(instance =>
                    string.Equals(instance.CreatedAs, tableName, StringComparison.OrdinalIgnoreCase))];
            }
        }

        private LifecycleProbeTable Track(string tableName)
        {
            var table = new LifecycleProbeTable(tableName);
            lock (Instances)
                Instances.Add(table);
            return table;
        }
    }

    private sealed class LifecycleProbeTable(string createdAs) : ManagedVirtualTable
    {
        private static readonly ManagedVirtualTableSchema ProbeSchema = new(
            [new ManagedVirtualTableColumn("value", ManagedVirtualTableAffinity.Integer)]);

        public string CreatedAs { get; } = createdAs;
        public int RenameCalls { get; private set; }
        public string? RenamedTo { get; private set; }
        public int DisconnectCalls { get; private set; }
        public int DestroyCalls { get; private set; }

        public override ManagedVirtualTableSchema Schema => ProbeSchema;

        public override ManagedVirtualTablePlan BestIndex(
            IReadOnlyList<ManagedVirtualTableConstraint> constraints,
            IReadOnlyList<ManagedVirtualTableOrderBy> orderBy) => new([]);

        public override ManagedVirtualTableCursor Open() => new LifecycleProbeCursor();

        public override ManagedVirtualTablePersistencePayload GetPersistencePayload() => new(1, []);

        public override void Rename(string newName)
        {
            RenameCalls++;
            RenamedTo = newName;
        }

        public override void Disconnect() => DisconnectCalls++;

        public override void Destroy() => DestroyCalls++;
    }

    private sealed class LifecycleProbeCursor : ManagedVirtualTableCursor
    {
        public override bool Eof => true;

        public override long RowId => 0;

        public override bool Filter(ManagedVirtualTablePlan plan, IReadOnlyList<SqlValue> arguments) => false;

        public override void Next()
        {
        }

        public override SqlValue Column(int columnIndex) => SqlValue.Null;
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

    private static string[] ExplainOpcodes(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var opcodes = new List<string>();
        while (statement.Step() == StatementStepResult.Row)
            opcodes.Add(statement.GetValue(1).AsText());

        return [.. opcodes];
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
}
