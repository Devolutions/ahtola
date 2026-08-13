using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

public sealed class ManagedTriggerRowSemanticsTests
{
    [Test]
    public void DistinctTriggerChainsFireForInsertUpdateAndDelete()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE source(id INTEGER PRIMARY KEY, value TEXT)",
                "CREATE TABLE middle(id INTEGER PRIMARY KEY, value TEXT)",
                "CREATE TABLE audit(event TEXT, value TEXT)",
                "CREATE TRIGGER source_insert AFTER INSERT ON source BEGIN "
                    + "INSERT INTO middle VALUES (NEW.id, NEW.value); END",
                "CREATE TRIGGER middle_insert AFTER INSERT ON middle BEGIN "
                    + "INSERT INTO audit VALUES ('insert', NEW.value); END",
                "CREATE TRIGGER source_update AFTER UPDATE ON source BEGIN "
                    + "UPDATE middle SET value = NEW.value WHERE id = NEW.id; END",
                "CREATE TRIGGER middle_update AFTER UPDATE ON middle BEGIN "
                    + "INSERT INTO audit VALUES ('update', NEW.value); END",
                "CREATE TRIGGER source_delete AFTER DELETE ON source BEGIN "
                    + "DELETE FROM middle WHERE id = OLD.id; END",
                "CREATE TRIGGER middle_delete AFTER DELETE ON middle BEGIN "
                    + "INSERT INTO audit VALUES ('delete', OLD.value); END",
                "INSERT INTO source VALUES (1, 'one')",
                "UPDATE source SET value = 'two' WHERE id = 1",
                "DELETE FROM source WHERE id = 1",
            ],
            "SELECT event, value FROM audit ORDER BY rowid");
    }

    [Test]
    public void TimingWhenUpdateOfAndRowImagesMatchSqlite()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT)",
                "CREATE TABLE trace(phase TEXT, old_id, old_value, new_id, new_value)",
                "CREATE TRIGGER data_before_insert BEFORE INSERT ON data "
                    + "WHEN NEW.id > 0 BEGIN "
                    + "INSERT INTO trace VALUES ('BI', NULL, NULL, NEW.id, NEW.value); END",
                "CREATE TRIGGER data_after_insert AFTER INSERT ON data FOR EACH ROW "
                    + "BEGIN INSERT INTO trace VALUES ('AI', NULL, NULL, NEW.id, NEW.value); END",
                "CREATE TRIGGER data_before_update UPDATE OF value ON data "
                    + "BEGIN INSERT INTO trace VALUES ('BU', OLD.id, OLD.value, NEW.id, NEW.value); END",
                "CREATE TRIGGER data_after_update AFTER UPDATE OF value, ghost ON data "
                    + "WHEN NEW.value <> OLD.value BEGIN "
                    + "INSERT INTO trace VALUES ('AU', OLD.id, OLD.value, NEW.id, NEW.value); END",
                "CREATE TRIGGER data_after_delete AFTER DELETE ON data "
                    + "BEGIN INSERT INTO trace VALUES ('AD', OLD.id, OLD.value, NULL, NULL); END",
                "INSERT INTO data VALUES (1, 'one'), (2, 'two')",
                "UPDATE data SET id = id",
                "UPDATE data SET value = upper(value) WHERE id = 2",
                "DELETE FROM data WHERE id = 1",
            ],
            "SELECT phase, old_id, old_value, new_id, new_value FROM trace ORDER BY rowid");
    }

    [Test]
    public void InsteadOfViewTriggersExposeViewRowsLikeSqlite()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE base(id INTEGER PRIMARY KEY, value TEXT)",
                "CREATE TABLE trace(kind TEXT, old_id, old_value, new_id, new_value)",
                "CREATE VIEW projected AS SELECT id, value || '!' AS decorated FROM base",
                "CREATE TRIGGER projected_insert INSTEAD OF INSERT ON projected BEGIN "
                    + "INSERT INTO base VALUES (NEW.id, NEW.decorated); END",
                "CREATE TRIGGER projected_update INSTEAD OF UPDATE OF decorated ON projected BEGIN "
                    + "INSERT INTO trace VALUES ('U', OLD.id, OLD.decorated, NEW.id, NEW.decorated); "
                    + "UPDATE base SET value = NEW.decorated WHERE id = OLD.id; END",
                "CREATE TRIGGER projected_delete INSTEAD OF DELETE ON projected BEGIN "
                    + "INSERT INTO trace VALUES ('D', OLD.id, OLD.decorated, NULL, NULL); "
                    + "DELETE FROM base WHERE id = OLD.id; END",
                "INSERT INTO projected(id, decorated) VALUES (1, 'one')",
                "UPDATE projected SET decorated = 'two' WHERE id = 1",
                "DELETE FROM projected WHERE id = 1",
            ],
            "SELECT kind, old_id, old_value, new_id, new_value FROM trace ORDER BY rowid");
    }

    [Test]
    public void RaiseIgnoreAndFailPreserveTheSamePrefixesAsSqlite()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "CREATE TABLE data(id INTEGER PRIMARY KEY)",
                "CREATE TABLE trace(value TEXT)",
                "CREATE TRIGGER data_before BEFORE INSERT ON data BEGIN "
                    + "INSERT INTO trace VALUES ('pre' || NEW.id); "
                    + "SELECT CASE WHEN NEW.id = 2 THEN RAISE(FAIL, 'boom') END; "
                    + "INSERT INTO trace VALUES ('post' || NEW.id); END",
            ],
            "INSERT INTO data VALUES (1), (2), (3)",
            "SELECT 'data', id FROM data UNION ALL SELECT 'trace', value FROM trace ORDER BY 1, 2");

        AssertMatchesSqlite(
            [
                "CREATE TABLE data(id INTEGER PRIMARY KEY)",
                "CREATE TABLE trace(value TEXT)",
                "CREATE TRIGGER data_before BEFORE INSERT ON data BEGIN "
                    + "INSERT INTO trace VALUES ('pre' || NEW.id); "
                    + "SELECT CASE WHEN NEW.id = 2 THEN RAISE(IGNORE) END; "
                    + "INSERT INTO trace VALUES ('post' || NEW.id); END",
                "INSERT INTO data VALUES (1), (2), (3)",
            ],
            "SELECT 'data', id FROM data UNION ALL SELECT 'trace', value FROM trace ORDER BY 1, 2");
    }

    [Test]
    public void ForeignKeyActionsRunChildRowTriggersBeforeParentAfter()
    {
        AssertMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "CREATE TABLE trace(value TEXT)",
                "CREATE TABLE parent(id INTEGER PRIMARY KEY)",
                "CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER "
                    + "REFERENCES parent(id) ON DELETE CASCADE)",
                "CREATE TRIGGER parent_before BEFORE DELETE ON parent "
                    + "BEGIN INSERT INTO trace VALUES ('PB:' || OLD.id); END",
                "CREATE TRIGGER parent_after AFTER DELETE ON parent "
                    + "BEGIN INSERT INTO trace VALUES ('PA:' || OLD.id); END",
                "CREATE TRIGGER child_before BEFORE DELETE ON child "
                    + "BEGIN INSERT INTO trace VALUES ('CB:' || OLD.id); END",
                "CREATE TRIGGER child_after AFTER DELETE ON child "
                    + "BEGIN INSERT INTO trace VALUES ('CA:' || OLD.id); END",
                "INSERT INTO parent VALUES (1)",
                "INSERT INTO child VALUES (10, 1)",
                "DELETE FROM parent",
            ],
            "SELECT value FROM trace ORDER BY rowid");
    }

    [Test]
    public void ForeignKeyActionTriggerFailPreservesOnlyCompletedParentRows()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "CREATE TABLE parent(id INTEGER PRIMARY KEY)",
                "CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER "
                    + "REFERENCES parent(id) ON UPDATE CASCADE)",
                "INSERT INTO parent VALUES (1), (2)",
                "INSERT INTO child VALUES (10, 1), (20, 2)",
                "CREATE TRIGGER child_after AFTER UPDATE ON child "
                    + "WHEN OLD.parent_id = 1 BEGIN SELECT RAISE(FAIL, 'cascade-stop'); END",
            ],
            "UPDATE parent SET id = id + 10",
            "SELECT 'parent', id, NULL FROM parent "
                + "UNION ALL SELECT 'child', id, parent_id FROM child ORDER BY 1, 2");
    }

    [Test]
    public void UpsertGeneratedAndWithoutRowidImagesMatchSqlite()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE items(key TEXT PRIMARY KEY, seed INTEGER, "
                    + "doubled INTEGER AS (seed * 2) VIRTUAL) WITHOUT ROWID",
                "CREATE TABLE trace(value TEXT)",
                "CREATE TRIGGER items_before_insert BEFORE INSERT ON items BEGIN "
                    + "INSERT INTO trace VALUES ('BI:' || NEW.key || ':' || NEW.doubled); END",
                "CREATE TRIGGER items_after_insert AFTER INSERT ON items BEGIN "
                    + "INSERT INTO trace VALUES ('AI:' || NEW.key || ':' || NEW.doubled); END",
                "CREATE TRIGGER items_before_update BEFORE UPDATE OF seed ON items BEGIN "
                    + "INSERT INTO trace VALUES ('BU:' || OLD.doubled || ':' || NEW.doubled); END",
                "CREATE TRIGGER items_after_update AFTER UPDATE OF seed ON items BEGIN "
                    + "INSERT INTO trace VALUES ('AU:' || OLD.doubled || ':' || NEW.doubled); END",
                "INSERT INTO items(key, seed) VALUES ('a', 2)",
                "INSERT INTO items(key, seed) VALUES ('a', 3) "
                    + "ON CONFLICT(key) DO UPDATE SET seed = excluded.seed",
            ],
            "SELECT value FROM trace ORDER BY rowid");
    }

    [Test]
    public void AutomaticRowidIsFinalizedAfterBeforeTriggerWork()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT)",
                "CREATE TABLE trace(phase TEXT, value INTEGER)",
                "CREATE TRIGGER data_before BEFORE INSERT ON data WHEN NEW.value = 'outer' BEGIN "
                    + "INSERT INTO trace VALUES ('before-rowid', NEW.rowid); "
                    + "INSERT INTO data(value) VALUES ('inner'); END",
                "CREATE TRIGGER data_after AFTER INSERT ON data BEGIN "
                    + "INSERT INTO trace VALUES ('after-rowid', NEW.rowid); END",
                "INSERT INTO data(value) VALUES ('outer')",
            ],
            "SELECT phase, value FROM trace ORDER BY rowid");
    }

    [Test]
    public void AutomaticRowidTracksIgnoredAndAfterInsertedRows()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT)",
                "CREATE TRIGGER data_before BEFORE INSERT ON data WHEN NEW.value = 'skip' "
                    + "BEGIN SELECT RAISE(IGNORE); END",
                "CREATE TRIGGER data_after AFTER INSERT ON data WHEN NEW.value = 'first' "
                    + "BEGIN INSERT INTO data(value) VALUES ('nested'); END",
                "INSERT INTO data(value) VALUES ('skip'), ('first'), ('second')",
            ],
            "SELECT id, value FROM data ORDER BY id");
    }

    [Test]
    public void ReturningReflectsSameRowAfterTriggerChangesLikeTurso()
    {
        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        var setup = new[]
        {
            "CREATE TABLE data(id INTEGER PRIMARY KEY, value INTEGER)",
            "INSERT INTO data VALUES (1, 10)",
            "CREATE TRIGGER data_after AFTER UPDATE ON data WHEN NEW.value < 100 BEGIN "
                + "UPDATE data SET value = NEW.value + 100 WHERE id = NEW.id; END",
        };
        foreach (var sql in setup)
            Execute(managed, sql);

        ReadRows(managed, "UPDATE data SET value = 20 WHERE id = 1 RETURNING id, value")
            .Should()
            .ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(1), SqlValue.Integer(120));
        ReadRows(managed, "SELECT id, value FROM data")
            .Should()
            .ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(1), SqlValue.Integer(120));
    }

    [Test]
    public void UncorrelatedUpdateAssignmentSubqueryUsesThePreTriggerStatementSnapshot()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE data(id INTEGER PRIMARY KEY, value INTEGER)",
                "CREATE TABLE trace(value TEXT)",
                "CREATE TRIGGER data_before BEFORE UPDATE ON data "
                    + "BEGIN INSERT INTO trace VALUES ('before'); END",
                "INSERT INTO data VALUES (1, 100), (2, 200), (3, 300)",
                "UPDATE data SET value = (SELECT sum(value) FROM data) WHERE id <= 2",
            ],
            "SELECT id, value FROM data ORDER BY id");
    }

    [Test]
    public void ReplaceDeleteTriggersAndOuterConflictOverrideMatchSqlite()
    {
        AssertMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT UNIQUE)",
                "CREATE TABLE trace(value TEXT)",
                "CREATE TRIGGER data_deleted AFTER DELETE ON data BEGIN "
                    + "INSERT INTO trace VALUES ('D:' || OLD.id); END",
                "CREATE TRIGGER data_inserted AFTER INSERT ON data BEGIN "
                    + "INSERT INTO trace VALUES ('I:' || NEW.id); END",
                "INSERT INTO data VALUES (1, 'same')",
                "DELETE FROM trace",
                "INSERT OR REPLACE INTO data VALUES (2, 'same')",
            ],
            "SELECT value FROM trace ORDER BY rowid");

        AssertErrorAndStateMatchesSqlite(
            [
                "CREATE TABLE source(id INTEGER PRIMARY KEY)",
                "CREATE TABLE sink(id INTEGER PRIMARY KEY)",
                "INSERT INTO sink VALUES (1)",
                "CREATE TRIGGER source_inserted AFTER INSERT ON source BEGIN "
                    + "INSERT OR IGNORE INTO sink VALUES (1); END",
            ],
            "INSERT OR ABORT INTO source VALUES (1)",
            "SELECT (SELECT COUNT(*) FROM source), (SELECT COUNT(*) FROM sink)");
    }

    [Test]
    public void ReplaceDeleteTriggersFollowConstraintDiscoveryOrder()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE data(id INTEGER PRIMARY KEY, first_key TEXT UNIQUE, second_key TEXT UNIQUE)",
                "CREATE TABLE trace(id INTEGER)",
                "INSERT INTO data VALUES (10, 'A', 'X'), (1, 'B', 'Y')",
                "CREATE TRIGGER data_deleted AFTER DELETE ON data BEGIN "
                    + "INSERT INTO trace VALUES (OLD.id); "
                    + "SELECT CASE WHEN OLD.id = 1 THEN RAISE(FAIL, 'replace-stop') END; END",
            ],
            "INSERT OR REPLACE INTO data VALUES (3, 'A', 'Y')",
            "SELECT 'data', id FROM data "
                + "UNION ALL SELECT 'trace', id FROM trace ORDER BY 1, 2");

        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE data(pk INTEGER, first_key TEXT UNIQUE, second_key TEXT UNIQUE, "
                    + "PRIMARY KEY(pk)) WITHOUT ROWID",
                "CREATE TABLE trace(pk INTEGER)",
                "INSERT INTO data VALUES (1, 'A0', 'B0'), (2, 'A', 'B2'), (3, 'A3', 'B')",
                "CREATE TRIGGER data_deleted AFTER DELETE ON data BEGIN "
                    + "INSERT INTO trace VALUES (OLD.pk); "
                    + "SELECT CASE WHEN OLD.pk = 1 THEN RAISE(FAIL, 'replace-pk-stop') END; END",
            ],
            "INSERT OR REPLACE INTO data VALUES (1, 'A', 'B')",
            "SELECT 'data', pk FROM data "
                + "UNION ALL SELECT 'trace', pk FROM trace ORDER BY 1, 2");
    }

    [Test]
    public void NotNullReplaceDefaultsAreAppliedBetweenBeforeAndAfter()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT NOT NULL DEFAULT 'default')",
                "CREATE TABLE trace(phase TEXT, value TEXT)",
                "CREATE TRIGGER data_before BEFORE INSERT ON data BEGIN "
                    + "INSERT INTO trace VALUES ('before', NEW.value); END",
                "CREATE TRIGGER data_after AFTER INSERT ON data BEGIN "
                    + "INSERT INTO trace VALUES ('after', NEW.value); END",
                "INSERT OR REPLACE INTO data VALUES (1, NULL)",
            ],
            "SELECT phase, value FROM trace ORDER BY rowid");
    }

    [Test]
    public void ScalarCallbackOrderMatchesSqlite()
    {
        var managedCallbacks = new List<string>();
        var sqliteCallbacks = new List<string>();
        using var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "tap",
            2,
            values =>
            {
                managedCallbacks.Add(values[0].AsText());
                return values[1];
            });
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        sqlite.CreateFunction<string, long, long>(
            "tap",
            (phase, value) =>
            {
                sqliteCallbacks.Add(phase);
                return value;
            });
        var setup = new[]
        {
            "CREATE TABLE data(value INTEGER CHECK(value IS NOT NULL))",
            "CREATE TRIGGER data_before BEFORE INSERT ON data "
                + "WHEN tap('when', 1) BEGIN SELECT tap('before', 1); END",
            "CREATE TRIGGER data_after AFTER INSERT ON data "
                + "BEGIN SELECT tap('after', 1); END",
        };
        foreach (var sql in setup)
        {
            Execute(managed, sql);
            Execute(sqlite, sql);
        }

        AssertQueriesMatch(
            managed,
            sqlite,
            "INSERT INTO data VALUES (tap('assign', 7)) RETURNING tap('returning', value)");
        managedCallbacks.Should().Equal(sqliteCallbacks);
    }

    [Test]
    public void CancellationInsideTriggerRollsBackTheWriteTransaction()
    {
        using var cancellation = new CancellationTokenSource();
        using var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "cancel_write",
            1,
            values =>
            {
                cancellation.Cancel();
                return values[0];
            });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(id INTEGER)");
        Execute(
            connection,
            "CREATE TRIGGER data_after AFTER INSERT ON data "
                + "WHEN NEW.id = 2 BEGIN SELECT cancel_write(NEW.id); END");
        Execute(connection, "BEGIN");
        Execute(connection, "INSERT INTO data VALUES (99)");

        using (var statement = connection.Prepare("INSERT INTO data VALUES (1), (2), (3)"))
            Assert.Throws<OperationCanceledException>(() => statement.Step(cancellation.Token));

        ReadRows(connection, "SELECT id FROM data").Should().BeEmpty();
        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "ROLLBACK"))!
            .Message.Should().Be("cannot rollback - no transaction is active");
    }

    [Test]
    public void RecursiveCancellationRollsBackEveryNestedFrameAndTheTransaction()
    {
        using var cancellation = new CancellationTokenSource();
        var callbacks = new List<long>();
        using var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "cancel_at_three",
            1,
            values =>
            {
                var value = values[0].AsInteger();
                callbacks.Add(value);
                if (value == 3)
                    cancellation.Cancel();
                return values[0];
            });
        using var connection = database.Connect();
        Execute(connection, "PRAGMA recursive_triggers = ON");
        Execute(connection, "CREATE TABLE prior(id INTEGER)");
        Execute(connection, "CREATE TABLE data(id INTEGER)");
        Execute(
            connection,
            "CREATE TRIGGER data_inserted AFTER INSERT ON data WHEN NEW.id < 5 BEGIN "
                + "SELECT cancel_at_three(NEW.id); "
                + "INSERT INTO data VALUES (NEW.id + 1); END");
        Execute(connection, "BEGIN");
        Execute(connection, "INSERT INTO prior VALUES (99)");

        using (var statement = connection.Prepare("INSERT INTO data VALUES (1)"))
            Assert.Throws<OperationCanceledException>(() => statement.Step(cancellation.Token));

        callbacks.Should().Equal(1, 2, 3);
        ReadRows(connection, "SELECT id FROM prior").Should().BeEmpty();
        ReadRows(connection, "SELECT id FROM data").Should().BeEmpty();
        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "ROLLBACK"))!
            .Message.Should().Be("cannot rollback - no transaction is active");
    }

    [Test]
    public void FileTriggersPreserveRowSemanticsAndOrderAcrossReopenAndPageMigration()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("row-triggers.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT)");
            Execute(connection, "CREATE TABLE trace(value TEXT)");
            Execute(
                connection,
                "CREATE TRIGGER first AFTER INSERT ON data "
                    + "BEGIN INSERT INTO trace VALUES ('first:' || NEW.id); END");
            Execute(
                connection,
                "CREATE TRIGGER second AFTER INSERT ON data WHEN NEW.value IS NOT NULL "
                    + "BEGIN INSERT INTO trace VALUES ('second:' || NEW.value); END");
            Execute(connection, "INSERT INTO data VALUES (1, 'one')");
            ReadRows(connection, "SELECT value FROM trace ORDER BY rowid")
                .Select(row => row[0].AsText())
                .Should().Equal("second:one", "first:1");
        }

        using (var database = EmbeddedDatabase.OpenFile("row-triggers.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "DELETE FROM trace");
            Execute(connection, "INSERT INTO data VALUES (2, 'two')");
            Execute(connection, "PRAGMA page_size = 8192");
            Execute(connection, "VACUUM");
            Execute(connection, "INSERT INTO data VALUES (3, 'three')");
            ReadRows(connection, "SELECT value FROM trace ORDER BY rowid")
                .Select(row => row[0].AsText())
                .Should().Equal(
                    "second:two",
                    "first:2",
                    "second:three",
                    "first:3");
            ReadRows(
                    connection,
                    "SELECT sql FROM sqlite_schema WHERE type = 'trigger' AND name = 'second'")
                .Should().ContainSingle()
                .Which[0].AsText().Should().Contain("WHEN NEW.value IS NOT NULL");
        }
    }

    [Test]
    public void RecursiveFileTriggersSurviveReopenAndPageMigration()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("recursive-triggers.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE data(id INTEGER PRIMARY KEY)");
            Execute(connection, "CREATE TABLE trace(id INTEGER)");
            Execute(
                connection,
                "CREATE TRIGGER data_inserted AFTER INSERT ON data WHEN NEW.id < 3 BEGIN "
                    + "INSERT INTO trace VALUES (NEW.id); "
                    + "INSERT INTO data VALUES (NEW.id + 1); END");
            Execute(connection, "PRAGMA recursive_triggers = ON");
            Execute(connection, "INSERT INTO data VALUES (1)");
            Execute(connection, "PRAGMA journal_mode = DELETE");
            Execute(connection, "PRAGMA page_size = 8192");
            Execute(connection, "VACUUM");
        }

        using (var database = EmbeddedDatabase.OpenFile("recursive-triggers.db", fileSystem))
        using (var connection = database.Connect())
        {
            ReadRows(connection, "PRAGMA page_size").Should().ContainSingle()
                .Which[0].AsInteger().Should().Be(8192);
            Execute(connection, "DELETE FROM data");
            Execute(connection, "DELETE FROM trace");
            Execute(connection, "PRAGMA recursive_triggers = ON");
            Execute(connection, "INSERT INTO data VALUES (1)");
            ReadRows(connection, "SELECT id FROM data ORDER BY id")
                .Select(row => row[0].AsInteger())
                .Should().Equal(1, 2, 3);
            ReadRows(connection, "SELECT id FROM trace ORDER BY rowid")
                .Select(row => row[0].AsInteger())
                .Should().Equal(1, 2);
        }
    }

    [Test]
    public void RecursiveFileMutationRecoversAfterInjectedFlushFailure()
    {
        const string path = "recursive-trigger-failure.db";
        var inner = new InMemoryFileSystem();
        var fileSystem = new FlushFailingFileSystem(inner, path);
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "PRAGMA journal_mode = DELETE");
            Execute(connection, "CREATE TABLE data(id INTEGER PRIMARY KEY)");
            Execute(connection, "CREATE TABLE trace(id INTEGER)");
            Execute(
                connection,
                "CREATE TRIGGER data_inserted AFTER INSERT ON data WHEN NEW.id < 3 BEGIN "
                    + "INSERT INTO trace VALUES (NEW.id); "
                    + "INSERT INTO data VALUES (NEW.id + 1); END");
            Execute(connection, "PRAGMA recursive_triggers = ON");

            fileSystem.ArmFlushFailure();
            Assert.Throws<IOException>(() => Execute(connection, "INSERT INTO data VALUES (1)"));
        }

        fileSystem.Disarm();
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            ReadRows(connection, "SELECT id FROM data").Should().BeEmpty();
            ReadRows(connection, "SELECT id FROM trace").Should().BeEmpty();
            Execute(connection, "PRAGMA recursive_triggers = ON");
            Execute(connection, "INSERT INTO data VALUES (1)");
            ReadRows(connection, "SELECT id FROM data ORDER BY id")
                .Select(row => row[0].AsInteger())
                .Should().Equal(1, 2, 3);
        }
    }

    [Test]
    public void AttachedPersistentTriggersStayWithinTheirDatabase()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("trigger-main.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE main.data(id INTEGER PRIMARY KEY)");
        Execute(connection, "ATTACH DATABASE 'trigger-aux.db' AS aux");
        Execute(connection, "CREATE TABLE aux.data(id INTEGER PRIMARY KEY)");
        Execute(connection, "CREATE TABLE aux.trace(id INTEGER)");
        Execute(
            connection,
            "CREATE TRIGGER aux.data_after AFTER INSERT ON aux.data "
                + "BEGIN INSERT INTO trace VALUES (NEW.id); END");
        Execute(connection, "BEGIN");
        Execute(connection, "INSERT INTO aux.data VALUES (7)");
        Execute(connection, "COMMIT");

        ReadRows(connection, "SELECT id FROM aux.trace").Should().ContainSingle()
            .Which[0].Should().Be(SqlValue.Integer(7));
        var crossDatabase = () => Execute(
            connection,
            "CREATE TRIGGER main.cross_database AFTER INSERT ON main.data "
                + "BEGIN INSERT INTO aux.trace VALUES (NEW.id); END");
        crossDatabase.Should().Throw<EmbeddedSqlException>();
        Execute(connection, "INSERT INTO main.data VALUES (8)");
        ReadRows(connection, "SELECT id FROM aux.trace").Should().ContainSingle();
    }

    [Test]
    public void PersistentTriggerAllowsExplicitReferencesToItsOwnSchema()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(id INTEGER PRIMARY KEY)");
        Execute(
            connection,
            "CREATE TRIGGER data_after AFTER INSERT ON data "
                + "BEGIN SELECT main.data.id FROM main.data; END");

        Execute(connection, "INSERT INTO data VALUES (1)");
        ReadRows(connection, "SELECT id FROM data").Should().ContainSingle()
            .Which[0].Should().Be(SqlValue.Integer(1));
    }

    [Test]
    public void RecursiveAttachedAndTempTriggersRemainSchemaLocal()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("recursive-schema-main.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE main.data(id INTEGER PRIMARY KEY)");
        Execute(connection, "CREATE TABLE main.trace(id INTEGER)");
        Execute(connection, "ATTACH DATABASE 'recursive-schema-aux.db' AS aux");
        Execute(connection, "CREATE TABLE aux.data(id INTEGER PRIMARY KEY)");
        Execute(connection, "CREATE TABLE aux.trace(id INTEGER)");
        Execute(
            connection,
            "CREATE TRIGGER aux.data_inserted AFTER INSERT ON aux.data WHEN NEW.id < 3 BEGIN "
                + "INSERT INTO trace VALUES (NEW.id); "
                + "INSERT INTO data VALUES (NEW.id + 1); END");
        Execute(connection, "CREATE TEMP TABLE temp_data(id INTEGER PRIMARY KEY)");
        Execute(connection, "CREATE TEMP TABLE temp_trace(id INTEGER)");
        Execute(
            connection,
            "CREATE TRIGGER temp.temp_data_inserted AFTER INSERT ON temp.temp_data WHEN NEW.id < 3 BEGIN "
                + "INSERT INTO temp_trace VALUES (NEW.id); "
                + "INSERT INTO temp_data VALUES (NEW.id + 1); END");
        Execute(connection, "PRAGMA recursive_triggers = ON");
        Execute(connection, "INSERT INTO aux.data VALUES (1)");
        Execute(connection, "INSERT INTO temp.temp_data VALUES (1)");

        ReadRows(connection, "SELECT id FROM main.data").Should().BeEmpty();
        ReadRows(connection, "SELECT id FROM main.trace").Should().BeEmpty();
        ReadRows(connection, "SELECT id FROM aux.data ORDER BY id")
            .Select(row => row[0].AsInteger())
            .Should().Equal(1, 2, 3);
        ReadRows(connection, "SELECT id FROM aux.trace ORDER BY rowid")
            .Select(row => row[0].AsInteger())
            .Should().Equal(1, 2);
        ReadRows(connection, "SELECT id FROM temp.temp_data ORDER BY id")
            .Select(row => row[0].AsInteger())
            .Should().Equal(1, 2, 3);
        ReadRows(connection, "SELECT id FROM temp.temp_trace ORDER BY rowid")
            .Select(row => row[0].AsInteger())
            .Should().Equal(1, 2);
    }

    [Test]
    public void RaiseAbortAndRollbackUseSqliteTransactionScopes()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "CREATE TABLE prior(value INTEGER)",
                "CREATE TABLE data(id INTEGER)",
                "CREATE TABLE trace(value TEXT)",
                "CREATE TRIGGER data_abort AFTER INSERT ON data WHEN NEW.id = 2 BEGIN "
                    + "INSERT INTO trace VALUES ('seen'); SELECT RAISE(ABORT, 'abort-trigger'); END",
                "BEGIN",
                "INSERT INTO prior VALUES (1)",
            ],
            "INSERT INTO data VALUES (1), (2), (3)",
            "SELECT 'prior', value FROM prior "
                + "UNION ALL SELECT 'data', id FROM data "
                + "UNION ALL SELECT 'trace', value FROM trace ORDER BY 1, 2");

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE prior(value INTEGER)");
        Execute(connection, "CREATE TABLE data(id INTEGER)");
        Execute(
            connection,
            "CREATE TRIGGER data_rollback AFTER INSERT ON data WHEN NEW.id = 2 "
                + "BEGIN SELECT RAISE(ROLLBACK, 'rollback-trigger'); END");
        Execute(connection, "BEGIN");
        Execute(connection, "INSERT INTO prior VALUES (1)");
        Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "INSERT INTO data VALUES (1), (2), (3)"))!
            .Message.Should().Be("rollback-trigger");
        ReadRows(connection, "SELECT value FROM prior").Should().BeEmpty();
        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "COMMIT"))!
            .Message.Should().Be("cannot commit - no transaction is active");
    }

    [Test]
    public void TriggerLocalLastInsertRowidIsRestoredAfterFail()
    {
        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        var setup = new[]
        {
            "CREATE TABLE data(id INTEGER PRIMARY KEY)",
            "CREATE TABLE side(id INTEGER PRIMARY KEY, value TEXT)",
            "CREATE TABLE trace(value INTEGER)",
            "CREATE TRIGGER data_after AFTER INSERT ON data BEGIN "
                + "INSERT INTO side(value) VALUES ('nested'); "
                + "INSERT INTO trace VALUES (last_insert_rowid()); "
                + "SELECT RAISE(FAIL, 'failed-after'); END",
        };
        foreach (var sql in setup)
        {
            Execute(managed, sql);
            Execute(sqlite, sql);
        }

        Assert.Throws<EmbeddedSqlException>(() => Execute(managed, "INSERT INTO data VALUES (10)"));
        Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, "INSERT INTO data VALUES (10)"));
        AssertQueriesMatch(
            managed,
            sqlite,
            "SELECT (SELECT id FROM data), (SELECT id FROM side), "
                + "(SELECT value FROM trace), last_insert_rowid()");
    }

    [Test]
    public void NestedRaiseIgnoreReturnsToTheParentTrigger()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE parent(id INTEGER)",
                "CREATE TABLE child(id INTEGER)",
                "CREATE TABLE trace(value TEXT)",
                "CREATE TRIGGER child_before BEFORE INSERT ON child BEGIN "
                    + "INSERT INTO trace VALUES ('child-before'); SELECT RAISE(IGNORE); END",
                "CREATE TRIGGER parent_after AFTER INSERT ON parent BEGIN "
                    + "INSERT INTO child VALUES (NEW.id); "
                    + "INSERT INTO trace VALUES ('parent-resumed'); END",
                "INSERT INTO parent VALUES (1)",
            ],
            "SELECT value FROM trace ORDER BY rowid");
    }

    [Test]
    public void WithoutRowidCandidateIdentitySurvivesTriggerResorting()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE data(key TEXT PRIMARY KEY, value INTEGER) WITHOUT ROWID",
                "CREATE TABLE trace(value TEXT)",
                "INSERT INTO data VALUES ('a', 0), ('b', 0)",
                "CREATE TRIGGER data_after AFTER UPDATE ON data BEGIN "
                    + "INSERT OR IGNORE INTO data VALUES (NEW.key || 'x', 0); "
                    + "INSERT INTO trace VALUES (NEW.key); END",
                "UPDATE data SET value = value + 1 WHERE key IN ('a', 'b')",
            ],
            "SELECT key, value FROM data ORDER BY key");
    }

    [Test]
    public void RecursivePragmaControlsFiniteSelfRecursionAndDepthFirstOrder()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE data(id INTEGER PRIMARY KEY)",
                "CREATE TABLE trace(value TEXT)",
                "CREATE TRIGGER data_after AFTER INSERT ON data WHEN NEW.id < 3 BEGIN "
                    + "INSERT INTO trace VALUES ('enter:' || NEW.id); "
                    + "INSERT INTO data VALUES (NEW.id + 1); "
                    + "INSERT INTO trace VALUES ('exit:' || NEW.id); END",
                "INSERT INTO data VALUES (1)",
                "INSERT INTO trace VALUES ('off-count:' || (SELECT count(*) FROM data))",
                "DELETE FROM data",
                "PRAGMA recursive_triggers = ON",
                "INSERT INTO data VALUES (1)",
            ],
            "SELECT value FROM trace ORDER BY rowid");
    }

    [Test]
    public void RecursiveManagedCallbacksRemainOnTheCallingThread()
    {
        var callingThread = Environment.CurrentManagedThreadId;
        var callbackThreads = new List<int>();
        using var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "capture_thread",
            1,
            values =>
            {
                callbackThreads.Add(Environment.CurrentManagedThreadId);
                return values[0];
            });
        using var connection = database.Connect();
        Execute(connection, "PRAGMA recursive_triggers = ON");
        Execute(connection, "CREATE TABLE data(id INTEGER)");
        Execute(connection, "CREATE TABLE standalone(id INTEGER)");
        Execute(
            connection,
            "CREATE TRIGGER data_inserted AFTER INSERT ON data WHEN NEW.id < 3 BEGIN "
                + "SELECT capture_thread(NEW.id); "
                + "INSERT INTO data VALUES (NEW.id + 1); END");

        Execute(connection, "INSERT INTO data VALUES (1)");
        Execute(connection, "INSERT INTO standalone VALUES (capture_thread(99))");

        callbackThreads.Should().OnlyContain(thread => thread == callingThread);
        callbackThreads.Should().HaveCount(3);
    }

    [Test]
    public void RecursiveCallbackReentryFailsWithoutDeadlocking()
    {
        using var database = new EmbeddedDatabase();
        EmbeddedConnection? connection = null;
        database.RegisterScalarFunction(
            "reenter",
            1,
            values =>
            {
                using var statement = connection!.Prepare("SELECT count(*) FROM data");
                _ = statement.Step();
                return values[0];
            });
        connection = database.Connect();
        using (connection)
        {
            Execute(connection, "PRAGMA recursive_triggers = ON");
            Execute(connection, "CREATE TABLE data(id INTEGER)");
            Execute(
                connection,
                "CREATE TRIGGER data_inserted AFTER INSERT ON data BEGIN "
                    + "SELECT reenter(NEW.id); END");

            Assert.Throws<EmbeddedSqlException>(
                    () => Execute(connection, "INSERT INTO data VALUES (1)"))!
                .Message.Should().Contain("reentrant managed database use");
            ReadRows(connection, "SELECT id FROM data").Should().BeEmpty();

            database.RegisterScalarFunction(
                "reenter_task",
                1,
                values => Task.Run(() =>
                {
                    using var statement = connection.Prepare("SELECT count(*) FROM data");
                    _ = statement.Step();
                    return values[0];
                }).GetAwaiter().GetResult());
            Execute(connection, "DROP TRIGGER data_inserted");
            Execute(
                connection,
                "CREATE TRIGGER data_inserted AFTER INSERT ON data BEGIN "
                    + "SELECT reenter_task(NEW.id); END");
            Assert.Throws<EmbeddedSqlException>(
                    () => Execute(connection, "INSERT INTO data VALUES (2)"))!
                .Message.Should().Contain("reentrant managed database use");
            ReadRows(connection, "SELECT id FROM data").Should().BeEmpty();

            database.RegisterScalarFunction(
                "register_reentry",
                1,
                values =>
                {
                    connection.RegisterScalarFunction("leaked", 0, _ => SqlValue.Integer(1));
                    return values[0];
                });
            Execute(connection, "DROP TRIGGER data_inserted");
            Execute(
                connection,
                "CREATE TRIGGER data_inserted AFTER INSERT ON data BEGIN "
                    + "SELECT register_reentry(NEW.id); END");
            Assert.Throws<EmbeddedSqlException>(
                    () => Execute(connection, "INSERT INTO data VALUES (3)"))!
                .Message.Should().Contain("reentrant managed database use");
            Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "SELECT leaked()"));

            using var otherDatabase = new EmbeddedDatabase();
            using var otherConnection = otherDatabase.Connect();
            Execute(otherConnection, "CREATE TABLE other_data(id INTEGER)");
            database.RegisterScalarFunction(
                "write_other",
                1,
                values =>
                {
                    Execute(otherConnection, $"INSERT INTO other_data VALUES ({values[0].AsInteger()})");
                    return values[0];
                });
            Execute(connection, "DROP TRIGGER data_inserted");
            Execute(
                connection,
                "CREATE TRIGGER data_inserted AFTER INSERT ON data BEGIN "
                    + "SELECT write_other(NEW.id); END");
            Execute(connection, "INSERT INTO data VALUES (4)");
            ReadRows(otherConnection, "SELECT id FROM other_data").Should().ContainSingle()
                .Which[0].AsInteger().Should().Be(4);
        }
    }

    [Test]
    public void RecursiveIndirectTriggersPreserveNestedStatementOrder()
    {
        AssertMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE left_data(id INTEGER)",
                "CREATE TABLE right_data(id INTEGER)",
                "CREATE TABLE trace(value TEXT)",
                "CREATE TRIGGER left_after AFTER INSERT ON left_data WHEN NEW.id < 5 BEGIN "
                    + "INSERT INTO trace VALUES ('L+' || NEW.id); "
                    + "INSERT INTO right_data VALUES (NEW.id + 1); "
                    + "INSERT INTO trace VALUES ('L-' || NEW.id); END",
                "CREATE TRIGGER right_after AFTER INSERT ON right_data WHEN NEW.id < 5 BEGIN "
                    + "INSERT INTO trace VALUES ('R+' || NEW.id); "
                    + "INSERT INTO left_data VALUES (NEW.id + 1); "
                    + "INSERT INTO trace VALUES ('R-' || NEW.id); END",
                "INSERT INTO left_data VALUES (1)",
            ],
            "SELECT value FROM trace ORDER BY rowid");
    }

    [Test]
    public void RecursiveInsertSelectPredicateTerminatesLikeSqlite()
    {
        AssertMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE data(id INTEGER)",
                "CREATE TRIGGER data_inserted AFTER INSERT ON data BEGIN "
                    + "INSERT INTO data SELECT NEW.id + 1 WHERE NEW.id < 3; END",
                "INSERT INTO data VALUES (1)",
            ],
            "SELECT id FROM data ORDER BY id");
    }

    [Test]
    public void RecursiveIgnoreConflictTerminatesLikeSqlite()
    {
        AssertMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE data(id INTEGER PRIMARY KEY)",
                "CREATE TRIGGER data_inserted AFTER INSERT ON data BEGIN "
                    + "INSERT OR IGNORE INTO data VALUES (NEW.id); END",
                "INSERT INTO data VALUES (1)",
            ],
            "SELECT id FROM data");
    }

    [Test]
    public void RecursiveDepthLimitUsesSqliteErrorAndRollsBackAutocommit()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE data(id INTEGER)",
                "CREATE TRIGGER data_after AFTER INSERT ON data WHEN NEW.id < 2000 "
                    + "BEGIN INSERT INTO data VALUES (NEW.id + 1); END",
            ],
            "INSERT INTO data VALUES (1)",
            "SELECT count(*), last_insert_rowid() FROM data");
    }

    [Test]
    public void RecursiveDepthErrorPreservesTheSqliteTransactionPrefix()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE data(id INTEGER)",
                "CREATE TRIGGER data_after AFTER INSERT ON data WHEN NEW.id < 2000 "
                    + "BEGIN INSERT INTO data VALUES (NEW.id + 1); END",
                "BEGIN",
            ],
            "INSERT INTO data VALUES (1)",
            "SELECT count(*), min(id), max(id), last_insert_rowid() FROM data");
    }

    [Test]
    public void RecursiveDepthPrefixIgnoresUnrelatedForeignKeyMode()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE data(id INTEGER)",
                "CREATE TRIGGER data_after AFTER INSERT ON data WHEN NEW.id < 2000 "
                    + "BEGIN INSERT INTO data VALUES (NEW.id + 1); END",
                "BEGIN",
            ],
            "INSERT INTO data VALUES (1)",
            "SELECT count(*), min(id), max(id) FROM data");
    }

    [Test]
    public void RecursiveDepthErrorHonorsUnreachableRaiseAbortStatementJournal()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE data(id INTEGER)",
                "CREATE TRIGGER data_after AFTER INSERT ON data WHEN NEW.id < 2000 BEGIN "
                    + "SELECT CASE WHEN 0 THEN RAISE(ABORT, 'unreachable') END; "
                    + "INSERT INTO data VALUES (NEW.id + 1); END",
                "BEGIN",
            ],
            "INSERT INTO data VALUES (1)",
            "SELECT count(*) FROM data");
    }

    [Test]
    public void RecursiveDepthErrorPreflightsNestedAbortPrograms()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE data(id INTEGER)",
                "CREATE TABLE later(id INTEGER)",
                "CREATE TRIGGER later_inserted AFTER INSERT ON later BEGIN "
                    + "SELECT RAISE(ABORT, 'later-abort'); END",
                "CREATE TRIGGER data_after AFTER INSERT ON data WHEN NEW.id < 2000 BEGIN "
                    + "INSERT INTO data VALUES (NEW.id + 1); "
                    + "INSERT INTO later VALUES (NEW.id); END",
                "BEGIN",
            ],
            "INSERT INTO data VALUES (1)",
            "SELECT (SELECT count(*) FROM data), (SELECT count(*) FROM later)");

        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE data(id INTEGER)",
                "CREATE TRIGGER data_after AFTER INSERT ON data WHEN NEW.id < 2000 BEGIN "
                    + "INSERT INTO data VALUES (NEW.id + 1); "
                    + "SELECT abs(-9223372036854775808); END",
                "BEGIN",
            ],
            "INSERT INTO data VALUES (1)",
            "SELECT count(*) FROM data");

        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE data(id INTEGER)",
                "CREATE TRIGGER data_after AFTER INSERT ON data WHEN NEW.id < 2000 BEGIN "
                    + "INSERT INTO data VALUES (NEW.id + 1); "
                    + "SELECT CASE WHEN 0 THEN 'invalid' ->> '$' END; END",
                "BEGIN",
            ],
            "INSERT INTO data VALUES (1)",
            "SELECT count(*) FROM data");
    }

    [Test]
    public void RecursiveExpressionJournalClassificationMatchesSqlite()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE data(id INTEGER)",
                "CREATE TRIGGER data_after AFTER INSERT ON data WHEN NEW.id < 2000 BEGIN "
                    + "INSERT INTO data VALUES (NEW.id + 1); "
                    + "SELECT CURRENT_TIMESTAMP; END",
                "BEGIN",
            ],
            "INSERT INTO data VALUES (1)",
            "SELECT count(*) FROM data");

        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE data(id INTEGER)",
                "CREATE TRIGGER data_after AFTER INSERT ON data WHEN NEW.id < 2000 BEGIN "
                    + "INSERT INTO data VALUES (NEW.id + 1); "
                    + "SELECT coalesce(NEW.id, 0); END",
                "BEGIN",
            ],
            "INSERT INTO data VALUES (1)",
            "SELECT count(*) FROM data");
    }

    [Test]
    public void RecursiveDefaultsAndViewsParticipateInAbortPreflight()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE data(id INTEGER)",
                "CREATE TABLE target(id INTEGER, value INTEGER "
                    + "DEFAULT (abs(-9223372036854775808)))",
                "CREATE TRIGGER data_after AFTER INSERT ON data WHEN NEW.id < 2000 BEGIN "
                    + "INSERT INTO data VALUES (NEW.id + 1); "
                    + "INSERT INTO target(id) VALUES (NEW.id); END",
                "BEGIN",
            ],
            "INSERT INTO data VALUES (1)",
            "SELECT (SELECT count(*) FROM data), (SELECT count(*) FROM target)");

        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE data(id INTEGER)",
                "CREATE VIEW aborting_view AS SELECT abs(-9223372036854775808) AS value",
                "CREATE TRIGGER data_after AFTER INSERT ON data WHEN NEW.id < 2000 BEGIN "
                    + "INSERT INTO data VALUES (NEW.id + 1); "
                    + "SELECT value FROM aborting_view; END",
                "BEGIN",
            ],
            "INSERT INTO data VALUES (1)",
            "SELECT count(*) FROM data");
    }

    [Test]
    public void RecursiveBeforeTriggerDepthUsesOuterStatementJournal()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE outer_data(id INTEGER PRIMARY KEY)",
                "CREATE TABLE side_data(id INTEGER)",
                "CREATE TRIGGER side_inserted AFTER INSERT ON side_data WHEN NEW.id < 2000 "
                    + "BEGIN INSERT INTO side_data VALUES (NEW.id + 1); END",
                "CREATE TRIGGER outer_before BEFORE INSERT ON outer_data "
                    + "BEGIN INSERT INTO side_data VALUES (1); END",
                "BEGIN",
            ],
            "INSERT INTO outer_data VALUES (1)",
            "SELECT (SELECT count(*) FROM outer_data), (SELECT count(*) FROM side_data)");
    }

    [Test]
    public void RecursivePreflightUsesOuterConflictPolicy()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE source(id INTEGER)",
                "CREATE TABLE loop_data(id INTEGER PRIMARY KEY)",
                "CREATE TRIGGER loop_before BEFORE INSERT ON loop_data WHEN NEW.id < 2000 "
                    + "BEGIN INSERT OR IGNORE INTO loop_data VALUES (NEW.id + 1); END",
                "CREATE TRIGGER source_before BEFORE INSERT ON source "
                    + "BEGIN INSERT OR IGNORE INTO loop_data VALUES (1); END",
                "BEGIN",
            ],
            "INSERT OR ABORT INTO source VALUES (1)",
            "SELECT (SELECT count(*) FROM source), (SELECT count(*) FROM loop_data)");
    }

    [Test]
    public void RecursiveNestedTriggersRetainEffectiveIgnorePolicy()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE source(id INTEGER)",
                "CREATE TABLE loop_data(id INTEGER PRIMARY KEY)",
                "CREATE TRIGGER loop_inserted AFTER INSERT ON loop_data WHEN NEW.id < 2000 "
                    + "BEGIN INSERT INTO loop_data VALUES (NEW.id + 1); END",
                "CREATE TRIGGER source_inserted AFTER INSERT ON source "
                    + "BEGIN INSERT OR IGNORE INTO loop_data VALUES (1); END",
                "BEGIN",
            ],
            "INSERT INTO source VALUES (1)",
            "SELECT (SELECT count(*) FROM source), (SELECT count(*) FROM loop_data)");
    }

    [Test]
    public void RecursiveGeneratedAndForeignKeyProgramsOpenStatementJournal()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE data(id INTEGER, computed INTEGER AS (abs(id)) VIRTUAL)",
                "CREATE TRIGGER data_inserted AFTER INSERT ON data WHEN NEW.id < 2000 "
                    + "BEGIN INSERT INTO data(id) VALUES (NEW.id + 1); END",
                "BEGIN",
            ],
            "INSERT INTO data(id) VALUES (1)",
            "SELECT count(*) FROM data");

        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE parent(id INTEGER PRIMARY KEY)",
                "CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER NOT NULL "
                    + "REFERENCES parent(id) ON DELETE SET NULL)",
                "CREATE TABLE data(id INTEGER)",
                "INSERT INTO parent VALUES (1)",
                "INSERT INTO child VALUES (1, 1)",
                "CREATE TRIGGER data_inserted AFTER INSERT ON data WHEN NEW.id < 2000 BEGIN "
                    + "INSERT INTO data VALUES (NEW.id + 1); "
                    + "DELETE FROM parent WHERE id = 1; END",
                "BEGIN",
            ],
            "INSERT INTO data VALUES (1)",
            "SELECT (SELECT count(*) FROM data), "
                + "(SELECT count(*) FROM parent), (SELECT count(*) FROM child)");
    }

    [Test]
    public void RecursiveForeignKeyCascadeInterruptionRollsBackParentAndChild()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE parent(id INTEGER PRIMARY KEY)",
                "CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER "
                    + "REFERENCES parent(id) ON UPDATE CASCADE)",
                "CREATE TABLE side_data(id INTEGER)",
                "INSERT INTO parent VALUES (1)",
                "INSERT INTO child VALUES (1, 1)",
                "CREATE TRIGGER side_inserted AFTER INSERT ON side_data WHEN NEW.id < 2000 "
                    + "BEGIN INSERT INTO side_data VALUES (NEW.id + 1); END",
                "CREATE TRIGGER child_before BEFORE UPDATE ON child "
                    + "BEGIN INSERT INTO side_data VALUES (1); END",
                "BEGIN",
            ],
            "UPDATE parent SET id = 2 WHERE id = 1",
            "SELECT (SELECT id FROM parent), (SELECT parent_id FROM child), "
                + "(SELECT count(*) FROM side_data)");
    }

    [Test]
    public void RecursiveDepthErrorPreservesDeferredForeignKeyPrefix()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE parent(id INTEGER PRIMARY KEY)",
                "CREATE TABLE data(id INTEGER, parent_id INTEGER REFERENCES parent(id) "
                    + "DEFERRABLE INITIALLY DEFERRED)",
                "INSERT INTO parent VALUES (1)",
                "CREATE TRIGGER data_after AFTER INSERT ON data WHEN NEW.id < 2000 "
                    + "BEGIN INSERT INTO data VALUES (NEW.id + 1, 1); END",
                "BEGIN",
            ],
            "INSERT INTO data VALUES (1, 1)",
            "SELECT count(*), min(id), max(id) FROM data");
    }

    [Test]
    public void RecursiveNoOpDepthErrorDoesNotReserveATransactionWriteDatabase()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("recursive-noop-main.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "ATTACH DATABASE 'recursive-noop-aux.db' AS aux");
        Execute(connection, "CREATE TABLE aux.data(id INTEGER)");
        Execute(connection, "CREATE VIEW projected AS SELECT 1 AS id");
        Execute(
            connection,
            "CREATE TRIGGER projected_insert INSTEAD OF INSERT ON projected WHEN NEW.id < 2000 "
                + "BEGIN INSERT INTO projected VALUES (NEW.id + 1); END");
        Execute(connection, "PRAGMA recursive_triggers = ON");
        Execute(connection, "BEGIN");

        Assert.Throws<EmbeddedSqlException>(
                () => Execute(connection, "INSERT INTO projected VALUES (1)"))!
            .Message.Should().Be("too many levels of trigger recursion");
        Execute(connection, "INSERT INTO aux.data VALUES (7)");
        Execute(connection, "COMMIT");
        ReadRows(connection, "SELECT id FROM aux.data").Should().ContainSingle()
            .Which[0].AsInteger().Should().Be(7);
    }

    [Test]
    public void RecursiveDepthErrorRollsBackConstrainedTransactionStatement()
    {
        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        var setup = new[]
        {
            "PRAGMA recursive_triggers = ON",
            "CREATE TABLE data(id INTEGER PRIMARY KEY)",
            "CREATE TRIGGER data_after AFTER INSERT ON data WHEN NEW.id < 2000 "
                + "BEGIN INSERT INTO data VALUES (NEW.id + 1); END",
            "BEGIN",
        };
        foreach (var sql in setup)
        {
            Execute(managed, sql);
            Execute(sqlite, sql);
        }

        var managedError = Assert.Throws<EmbeddedSqlException>(
            () => Execute(managed, "INSERT INTO data VALUES (1)"));
        var sqliteError = Assert.Throws<MsData.SqliteException>(
            () => Execute(sqlite, "INSERT INTO data VALUES (1)"));
        sqliteError!.Message.Should().Contain(managedError!.Message);
        Execute(managed, "COMMIT");
        Execute(sqlite, "COMMIT");
        AssertQueriesMatch(managed, sqlite, "SELECT count(*), last_insert_rowid() FROM data");
    }

    [Test]
    public void RecursiveDeleteDepthErrorPreservesTheSqlitePrefix()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE data(id INTEGER PRIMARY KEY)",
                "WITH RECURSIVE numbers(id) AS ("
                    + "VALUES(1) UNION ALL SELECT id + 1 FROM numbers WHERE id < 1101"
                    + ") INSERT INTO data SELECT id FROM numbers",
                "CREATE TRIGGER data_deleted AFTER DELETE ON data WHEN OLD.id > 1 BEGIN "
                    + "DELETE FROM data WHERE id = OLD.id - 1; END",
            ],
            "DELETE FROM data WHERE id = 1101",
            "SELECT count(*), min(id), max(id) FROM data");
    }

    [Test]
    public void RecursiveIgnoreConstraintDepthErrorPreservesTheSqlitePrefix()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE data(id INTEGER PRIMARY KEY ON CONFLICT IGNORE)",
                "CREATE TRIGGER data_inserted AFTER INSERT ON data WHEN NEW.id < 2000 "
                    + "BEGIN INSERT INTO data VALUES (NEW.id + 1); END",
                "BEGIN",
            ],
            "INSERT INTO data VALUES (1)",
            "SELECT count(*), min(id), max(id) FROM data");
    }

    [Test]
    public void RecursiveRollbackConstraintDepthErrorPreservesTheSqlitePrefix()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE data(id INTEGER PRIMARY KEY ON CONFLICT ROLLBACK)",
                "CREATE TRIGGER data_inserted AFTER INSERT ON data WHEN NEW.id < 2000 "
                    + "BEGIN INSERT INTO data VALUES (NEW.id + 1); END",
                "BEGIN",
            ],
            "INSERT INTO data VALUES (1)",
            "SELECT count(*), min(id), max(id) FROM data");
    }

    [Test]
    public void RecursiveValidStrictDepthErrorPreservesTheSqlitePrefix()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE data(id INTEGER) STRICT",
                "CREATE TRIGGER data_inserted AFTER INSERT ON data WHEN NEW.id < 2000 "
                    + "BEGIN INSERT INTO data VALUES (NEW.id + 1); END",
                "BEGIN",
            ],
            "INSERT INTO data VALUES (1)",
            "SELECT count(*), min(id), max(id) FROM data");
    }

    [Test]
    public void RecursiveReplaceNotNullAndGeneratedUniqueUseStatementRollback()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE data(id INTEGER, required TEXT NOT NULL ON CONFLICT REPLACE DEFAULT 'value')",
                "CREATE TRIGGER data_inserted AFTER INSERT ON data WHEN NEW.id < 2000 "
                    + "BEGIN INSERT INTO data VALUES (NEW.id + 1, NULL); END",
                "BEGIN",
            ],
            "INSERT INTO data VALUES (1, NULL)",
            "SELECT count(*) FROM data");

        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE data(id INTEGER, seed INTEGER, computed INTEGER AS (seed) VIRTUAL, "
                    + "UNIQUE(computed))",
                "CREATE TRIGGER data_inserted AFTER INSERT ON data WHEN NEW.id < 2000 "
                    + "BEGIN INSERT INTO data(id, seed) VALUES (NEW.id + 1, NEW.seed + 1); END",
                "BEGIN",
            ],
            "INSERT INTO data(id, seed) VALUES (1, 1)",
            "SELECT count(*) FROM data");
    }

    [Test]
    public void RecursiveNonKeyUpdateDepthErrorPreservesTheSqlitePrefix()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE unrelated_parent(id INTEGER PRIMARY KEY)",
                "CREATE TABLE unrelated_child(id INTEGER PRIMARY KEY, parent_id INTEGER "
                    + "REFERENCES unrelated_parent(id))",
                "CREATE TABLE data(id INTEGER PRIMARY KEY, value INTEGER)",
                "INSERT INTO data VALUES (1, 0)",
                "CREATE TRIGGER data_updated AFTER UPDATE OF value ON data WHEN NEW.value < 2000 BEGIN "
                    + "UPDATE data SET value = NEW.value + 1 WHERE id = NEW.id; END",
                "BEGIN",
            ],
            "UPDATE data SET value = 1 WHERE id = 1",
            "SELECT id, value FROM data");
    }

    [Test]
    public void RecursivePartialUniquePredicateUsesStatementRollback()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE data(id INTEGER PRIMARY KEY, flag INTEGER)",
                "CREATE UNIQUE INDEX data_positive ON data(id) WHERE flag > 0",
                "INSERT INTO data VALUES (1, 0)",
                "CREATE TRIGGER data_updated AFTER UPDATE OF flag ON data WHEN NEW.flag < 2000 BEGIN "
                    + "UPDATE data SET flag = NEW.flag + 1 WHERE id = NEW.id; END",
                "BEGIN",
            ],
            "UPDATE data SET flag = 1 WHERE id = 1",
            "SELECT id, flag FROM data");
    }

    [Test]
    public void RecursiveUpsertSideEffectsParticipateInStatementRollback()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE source(id INTEGER)",
                "CREATE TABLE target(id INTEGER PRIMARY KEY, required TEXT NOT NULL)",
                "CREATE TRIGGER source_inserted AFTER INSERT ON source WHEN NEW.id < 2000 BEGIN "
                    + "INSERT INTO target VALUES (NEW.id, 'value') "
                    + "ON CONFLICT(id) DO UPDATE SET required = excluded.required; "
                    + "INSERT INTO source VALUES (NEW.id + 1); END",
                "BEGIN",
            ],
            "INSERT INTO source VALUES (1)",
            "SELECT (SELECT count(*) FROM source), (SELECT count(*) FROM target)");
    }

    [Test]
    public void RecursiveUnreachableUpsertUpdateUsesAbortPolicy()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE source(id INTEGER)",
                "CREATE TABLE target(id INTEGER PRIMARY KEY ON CONFLICT IGNORE, "
                    + "value INTEGER NOT NULL ON CONFLICT IGNORE)",
                "INSERT INTO target VALUES (1, 1)",
                "CREATE TRIGGER source_inserted AFTER INSERT ON source WHEN NEW.id < 2000 BEGIN "
                    + "INSERT INTO source VALUES (NEW.id + 1); "
                    + "INSERT OR IGNORE INTO target VALUES (1, 1) "
                    + "ON CONFLICT(id) DO UPDATE SET value = NULL; END",
                "BEGIN",
            ],
            "INSERT INTO source VALUES (1)",
            "SELECT (SELECT count(*) FROM source), (SELECT value FROM target)");
    }

    [Test]
    public void RecursiveCheckIgnorePreservesTheSqlitePrefix()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE data(id INTEGER CHECK(id > 0))",
                "CREATE TRIGGER data_inserted AFTER INSERT ON data WHEN NEW.id < 2000 "
                    + "BEGIN INSERT INTO data VALUES (NEW.id + 1); END",
                "BEGIN",
            ],
            "INSERT OR IGNORE INTO data VALUES (1)",
            "SELECT count(*), min(id), max(id) FROM data");
    }

    [Test]
    public void RecursiveRowImagesCoverGeneratedWithoutRowidAndViewRows()
    {
        AssertMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE trace(value TEXT)",
                "CREATE TABLE row_data(id INTEGER PRIMARY KEY, seed INTEGER, "
                    + "doubled INTEGER AS (seed * 2) VIRTUAL)",
                "CREATE TRIGGER row_inserted AFTER INSERT ON row_data WHEN NEW.seed < 3 BEGIN "
                    + "INSERT INTO trace VALUES ('I:' || NEW.rowid || ':' || NEW.seed || ':' || NEW.doubled); "
                    + "INSERT INTO row_data(seed) VALUES (NEW.seed + 1); END",
                "CREATE TRIGGER row_updated AFTER UPDATE OF seed ON row_data WHEN NEW.seed < 5 BEGIN "
                    + "INSERT INTO trace VALUES ('U:' || OLD.rowid || ':' || OLD.seed || ':' "
                    + "|| NEW.rowid || ':' || NEW.seed || ':' || NEW.doubled); "
                    + "UPDATE row_data SET seed = NEW.seed + 1 WHERE rowid = NEW.rowid; END",
                "INSERT INTO row_data(seed) VALUES (1)",
                "UPDATE row_data SET seed = 3 WHERE id = 1",
                "CREATE TABLE keyed_data(key TEXT PRIMARY KEY, seed INTEGER, "
                    + "doubled INTEGER AS (seed * 2) VIRTUAL) WITHOUT ROWID",
                "CREATE TRIGGER keyed_inserted AFTER INSERT ON keyed_data WHEN NEW.seed < 3 BEGIN "
                    + "INSERT INTO trace VALUES ('W:' || NEW.key || ':' || NEW.seed || ':' || NEW.doubled); "
                    + "INSERT INTO keyed_data(key, seed) VALUES (NEW.key || NEW.seed, NEW.seed + 1); END",
                "INSERT INTO keyed_data(key, seed) VALUES ('k', 1)",
                "CREATE VIEW projected AS SELECT key, seed, doubled FROM keyed_data",
                "CREATE TRIGGER projected_insert INSTEAD OF INSERT ON projected WHEN NEW.seed < 3 BEGIN "
                    + "INSERT INTO trace VALUES ('V:' || NEW.key || ':' || NEW.seed || ':' || NEW.doubled); "
                    + "INSERT INTO projected(key, seed) VALUES (NEW.key || 'v', NEW.seed + 1); END",
                "INSERT INTO projected(key, seed) VALUES ('v', 1)",
            ],
            "SELECT value FROM trace ORDER BY rowid");
    }

    [Test]
    public void RecursiveLastInsertRowidAndAutoIncrementSequenceMatchSqlite()
    {
        AssertMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE data(id INTEGER PRIMARY KEY AUTOINCREMENT, parent INTEGER)",
                "CREATE TABLE trace(depth INTEGER, seen INTEGER, last_id INTEGER)",
                "CREATE TRIGGER data_inserted AFTER INSERT ON data WHEN NEW.id < 3 BEGIN "
                    + "INSERT INTO trace VALUES (NEW.id, NEW.rowid, last_insert_rowid()); "
                    + "INSERT INTO data(parent) VALUES (NEW.id); "
                    + "INSERT INTO trace VALUES (-NEW.id, NEW.rowid, last_insert_rowid()); END",
                "INSERT INTO data(parent) VALUES (NULL)",
            ],
            "SELECT 'data', id, parent, NULL FROM data "
                + "UNION ALL SELECT 'log', depth, seen, last_id FROM trace "
                + "UNION ALL SELECT 'outer', last_insert_rowid(), NULL, NULL "
                + "UNION ALL SELECT 'sequence', seq, NULL, NULL FROM sqlite_sequence WHERE name = 'data' "
                + "ORDER BY 1, 2");
    }

    [Test]
    public void RecursiveRaiseFailPreservesTheSqliteMutationPrefix()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE data(id INTEGER PRIMARY KEY)",
                "CREATE TABLE trace(id INTEGER)",
                "CREATE TRIGGER data_inserted AFTER INSERT ON data WHEN NEW.id < 4 BEGIN "
                    + "INSERT INTO trace VALUES (NEW.id); "
                    + "SELECT CASE WHEN NEW.id = 3 THEN RAISE(FAIL, 'recursive-stop') END; "
                    + "INSERT INTO data VALUES (NEW.id + 1); END",
            ],
            "INSERT INTO data VALUES (1)",
            "SELECT 'data', id FROM data "
                + "UNION ALL SELECT 'trace', id FROM trace "
                + "UNION ALL SELECT 'last', last_insert_rowid() ORDER BY 1, 2");
    }

    [Test]
    public void RecursiveConflictOverrideAndReplaceDeleteProgramsMatchSqlite()
    {
        AssertMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE source(id INTEGER PRIMARY KEY)",
                "CREATE TABLE sink(id INTEGER PRIMARY KEY)",
                "INSERT INTO sink VALUES (2)",
                "CREATE TRIGGER source_inserted AFTER INSERT ON source WHEN NEW.id < 3 BEGIN "
                    + "INSERT INTO sink VALUES (NEW.id); "
                    + "INSERT INTO source VALUES (NEW.id + 1); END",
                "INSERT OR IGNORE INTO source VALUES (1)",
                "CREATE TABLE replace_data(id INTEGER PRIMARY KEY, value TEXT UNIQUE)",
                "CREATE TABLE replace_trace(id INTEGER)",
                "CREATE TRIGGER replace_deleted AFTER DELETE ON replace_data BEGIN "
                    + "INSERT INTO replace_trace VALUES (OLD.id); END",
                "CREATE TRIGGER replace_trace_inserted AFTER INSERT ON replace_trace WHEN NEW.id < 3 BEGIN "
                    + "INSERT INTO replace_trace VALUES (NEW.id + 1); END",
                "INSERT INTO replace_data VALUES (1, 'same')",
                "INSERT OR REPLACE INTO replace_data VALUES (9, 'same')",
            ],
            "SELECT 'source', id FROM source "
                + "UNION ALL SELECT 'sink', id FROM sink "
                + "UNION ALL SELECT 'replace', id FROM replace_data "
                + "UNION ALL SELECT 'deleted', id FROM replace_trace ORDER BY 1, 2");
    }

    [Test]
    public void RecursiveForeignKeyActionsAndDeferredChecksMatchSqlite()
    {
        AssertMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE parent(id INTEGER PRIMARY KEY)",
                "CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER "
                    + "REFERENCES parent(id) ON DELETE CASCADE)",
                "CREATE TABLE trace(value TEXT)",
                "CREATE TRIGGER child_deleted AFTER DELETE ON child WHEN OLD.id < 12 BEGIN "
                    + "INSERT INTO trace VALUES ('C:' || OLD.id); "
                    + "INSERT INTO parent VALUES (OLD.id + 1); "
                    + "INSERT INTO child VALUES (OLD.id + 10, OLD.id + 1); "
                    + "DELETE FROM parent WHERE id = OLD.id + 1; END",
                "INSERT INTO parent VALUES (1)",
                "INSERT INTO child VALUES (1, 1)",
                "DELETE FROM parent WHERE id = 1",
                "CREATE TABLE deferred_parent(id INTEGER PRIMARY KEY)",
                "CREATE TABLE deferred_child(id INTEGER PRIMARY KEY, parent_id INTEGER "
                    + "REFERENCES deferred_parent(id) DEFERRABLE INITIALLY DEFERRED)",
                "CREATE TRIGGER deferred_parent_inserted AFTER INSERT ON deferred_parent "
                    + "WHEN NEW.id < 3 BEGIN "
                    + "INSERT INTO deferred_child VALUES (NEW.id, NEW.id + 1); "
                    + "INSERT INTO deferred_parent VALUES (NEW.id + 1); END",
                "BEGIN",
                "INSERT INTO deferred_parent VALUES (1)",
                "COMMIT",
            ],
            "SELECT 'trace', value, NULL FROM trace "
                + "UNION ALL SELECT 'parent', id, NULL FROM parent "
                + "UNION ALL SELECT 'child', id, parent_id FROM child "
                + "UNION ALL SELECT 'deferred-parent', id, NULL FROM deferred_parent "
                + "UNION ALL SELECT 'deferred-child', id, parent_id FROM deferred_child ORDER BY 1, 2");
    }

    [Test]
    public void RecursiveAbortAndSavepointRollbackUseSqliteScopes()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE prior(id INTEGER)",
                "CREATE TABLE data(id INTEGER)",
                "CREATE TABLE trace(id INTEGER)",
                "CREATE TRIGGER data_inserted AFTER INSERT ON data WHEN NEW.id < 4 BEGIN "
                    + "INSERT INTO trace VALUES (NEW.id); "
                    + "SELECT CASE WHEN NEW.id = 3 THEN RAISE(ABORT, 'recursive-abort') END; "
                    + "INSERT INTO data VALUES (NEW.id + 1); END",
                "BEGIN",
                "INSERT INTO prior VALUES (99)",
            ],
            "INSERT INTO data VALUES (1)",
            "SELECT 'prior', id FROM prior "
                + "UNION ALL SELECT 'data', id FROM data "
                + "UNION ALL SELECT 'trace', id FROM trace ORDER BY 1, 2");

        AssertMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE prior(id INTEGER)",
                "CREATE TABLE data(id INTEGER)",
                "CREATE TRIGGER data_inserted AFTER INSERT ON data WHEN NEW.id < 3 "
                    + "BEGIN INSERT INTO data VALUES (NEW.id + 1); END",
                "BEGIN",
                "INSERT INTO prior VALUES (99)",
                "SAVEPOINT recursive_write",
                "INSERT INTO data VALUES (1)",
                "ROLLBACK TO recursive_write",
                "RELEASE recursive_write",
                "COMMIT",
            ],
            "SELECT 'prior', id FROM prior UNION ALL SELECT 'data', id FROM data ORDER BY 1, 2");

        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE prior(id INTEGER)",
                "CREATE TABLE data(id INTEGER)",
                "CREATE TRIGGER data_inserted AFTER INSERT ON data WHEN NEW.id < 4 BEGIN "
                    + "SELECT CASE WHEN NEW.id = 3 THEN RAISE(ROLLBACK, 'recursive-rollback') END; "
                    + "INSERT INTO data VALUES (NEW.id + 1); END",
                "BEGIN",
                "INSERT INTO prior VALUES (99)",
            ],
            "INSERT INTO data VALUES (1)",
            "SELECT 'prior', id FROM prior UNION ALL SELECT 'data', id FROM data ORDER BY 1, 2");
    }

    [Test]
    public void RecursiveCyclesAreRejectedBeforeCallbacksOrMutation()
    {
        var callbacks = 0;
        using var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "mark",
            1,
            values =>
            {
                callbacks++;
                return values[0];
            });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(id INTEGER)");
        Execute(
            connection,
            "CREATE TRIGGER data_after AFTER INSERT ON data BEGIN "
                + "SELECT mark(NEW.id); INSERT INTO data VALUES (NEW.id + 1); END");
        Execute(connection, "PRAGMA recursive_triggers = ON");

        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "INSERT INTO data VALUES (1)"))!
            .Message.Should().Be("too many levels of trigger recursion");
        callbacks.Should().Be(0);
        ReadRows(connection, "SELECT id FROM data").Should().BeEmpty();

        Execute(connection, "DROP TRIGGER data_after");
        Execute(
            connection,
            "CREATE TRIGGER data_after AFTER INSERT ON data WHEN 1 BEGIN "
                + "SELECT mark(NEW.id); INSERT INTO data VALUES (NEW.id + 1); END");
        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "INSERT INTO data VALUES (1)"))!
            .Message.Should().Be("too many levels of trigger recursion");
        callbacks.Should().Be(0);
        ReadRows(connection, "SELECT id FROM data").Should().BeEmpty();

        Execute(connection, "DROP TRIGGER data_after");
        Execute(
            connection,
            "CREATE TRIGGER data_after AFTER INSERT ON data WHEN 1 = 1 BEGIN "
                + "SELECT mark(NEW.id); INSERT INTO data VALUES (NEW.id + 1); END");
        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "INSERT INTO data VALUES (1)"))!
            .Message.Should().Be("too many levels of trigger recursion");
        callbacks.Should().Be(0);
        ReadRows(connection, "SELECT id FROM data").Should().BeEmpty();

        Execute(connection, "DROP TRIGGER data_after");
        Execute(
            connection,
            "CREATE TRIGGER data_after AFTER INSERT ON data BEGIN "
                + "SELECT mark(NEW.id); "
                + "INSERT INTO data SELECT NEW.id + 1 WHERE 1; END");
        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "INSERT INTO data VALUES (1)"))!
            .Message.Should().Be("too many levels of trigger recursion");
        callbacks.Should().Be(0);
        ReadRows(connection, "SELECT id FROM data").Should().BeEmpty();

        Execute(connection, "DROP TRIGGER data_after");
        Execute(
            connection,
            "CREATE TRIGGER data_after AFTER INSERT ON data WHEN (1 COLLATE BINARY) BEGIN "
                + "SELECT mark(NEW.id); INSERT INTO data VALUES (NEW.id + 1); END");
        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "INSERT INTO data VALUES (1)"))!
            .Message.Should().Be("too many levels of trigger recursion");
        callbacks.Should().Be(0);
        ReadRows(connection, "SELECT id FROM data").Should().BeEmpty();
    }

    [Test]
    public void BeforeUpdateCanMutateAnotherRowInTheTargetTable()
    {
        var callbacks = 0;
        using var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "mark",
            1,
            values =>
            {
                callbacks++;
                return values[0];
            });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(id INTEGER PRIMARY KEY, value INTEGER)");
        Execute(connection, "INSERT INTO data VALUES (1, 10), (2, 20)");
        Execute(
            connection,
            "CREATE TRIGGER data_before BEFORE UPDATE ON data BEGIN "
                + "SELECT mark(OLD.id); UPDATE data SET value = value + 100 WHERE id = 2; END");

        Execute(connection, "UPDATE data SET value = 11 WHERE id = 1");

        callbacks.Should().Be(1);
        ReadRows(connection, "SELECT id, value FROM data ORDER BY id")
            .Select(row => (row[0].AsInteger(), row[1].AsInteger()))
            .Should().Equal((1L, 11L), (2L, 120L));
    }

    [Test]
    public void BeforeForeignKeyActionTriggerCanMutateAnotherTargetRow()
    {
        var callbacks = 0;
        using var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "mark",
            1,
            values =>
            {
                callbacks++;
                return values[0];
            });
        using var connection = database.Connect();
        Execute(connection, "PRAGMA foreign_keys = ON");
        Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY)");
        Execute(
            connection,
            "CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER "
                + "REFERENCES parent(id) ON UPDATE CASCADE, note TEXT)");
        Execute(connection, "INSERT INTO parent VALUES (1)");
        Execute(connection, "INSERT INTO child VALUES (10, 1, 'old')");
        Execute(
            connection,
            "CREATE TRIGGER child_before BEFORE UPDATE ON child BEGIN "
                + "SELECT mark(OLD.id); UPDATE child SET note = 'trigger' WHERE id = 999; END");

        Execute(connection, "UPDATE parent SET id = 2 WHERE id = 1");
        callbacks.Should().Be(1);
        ReadRows(connection, "SELECT id FROM parent").Should().ContainSingle()
            .Which[0].Should().Be(SqlValue.Integer(2));
        ReadRows(connection, "SELECT parent_id, note FROM child").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(2), SqlValue.Text("old"));
    }

    [Test]
    public void TriggerBodyRestrictionsFailBeforeCatalogOrRowMutation()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(id INTEGER)");
        Execute(connection, "CREATE TABLE trace(id INTEGER)");
        var rejected = new[]
        {
            "CREATE TRIGGER bad AFTER INSERT ON data BEGIN INSERT INTO trace DEFAULT VALUES; END",
            "CREATE TRIGGER bad AFTER INSERT ON data BEGIN INSERT INTO main.trace VALUES (1); END",
            "CREATE TRIGGER bad AFTER INSERT ON data BEGIN INSERT INTO trace VALUES (1) RETURNING id; END",
            "CREATE TRIGGER bad AFTER INSERT ON data BEGIN UPDATE trace SET id = 1 LIMIT 1; END",
            "CREATE TRIGGER bad AFTER INSERT ON data BEGIN INSERT INTO trace VALUES (?); END",
            "CREATE TRIGGER bad AFTER INSERT ON data BEGIN CREATE TABLE nested(id); END",
            "CREATE TRIGGER bad AFTER INSERT ON data BEGIN PRAGMA foreign_keys; END",
        };
        foreach (var sql in rejected)
        {
            Assert.Throws<EmbeddedSqlException>(() => Execute(connection, sql));
            ReadRows(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'trigger'")
                .Should().ContainSingle().Which[0].Should().Be(SqlValue.Integer(0));
        }

        Execute(connection, "INSERT INTO data VALUES (1)");
        ReadRows(connection, "SELECT id FROM data").Should().ContainSingle();
        ReadRows(connection, "SELECT id FROM trace").Should().BeEmpty();
    }

    [Test]
    public void LazyTriggerProgramErrorsPreflightBeforeSourceCallbacks()
    {
        var callbacks = 0;
        using var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "mark",
            1,
            values =>
            {
                callbacks++;
                return values[0];
            });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(id INTEGER)");
        Execute(
            connection,
            "CREATE TRIGGER missing_body AFTER INSERT ON data "
                + "BEGIN INSERT INTO missing VALUES (NEW.id); END");

        Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "INSERT INTO data VALUES (mark(1))"))!
            .Message.Should().Contain("no such table: missing");
        callbacks.Should().Be(0);
        ReadRows(connection, "SELECT id FROM data").Should().BeEmpty();

        Execute(connection, "DROP TRIGGER missing_body");
        Execute(
            connection,
            "CREATE TRIGGER illegal_old AFTER INSERT ON data "
                + "BEGIN SELECT OLD.id; END");
        Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "INSERT INTO data VALUES (mark(2))"))!
            .Message.Should().Contain("no such column: OLD.id");
        callbacks.Should().Be(0);
        ReadRows(connection, "SELECT id FROM data").Should().BeEmpty();
    }

    [Test]
    public void UnqualifiedPseudoRowColumnsFailBeforeTriggerCallbacks()
    {
        var callbacks = 0;
        using var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "mark",
            1,
            values =>
            {
                callbacks++;
                return values[0];
            });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(value INTEGER)");
        Execute(
            connection,
            "CREATE TRIGGER data_after AFTER INSERT ON data BEGIN "
                + "SELECT mark(NEW.value); SELECT value; END");

        Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "INSERT INTO data VALUES (1)"))!
            .Message.Should().Contain("no such column: value");
        callbacks.Should().Be(0);
        ReadRows(connection, "SELECT value FROM data").Should().BeEmpty();
    }

    [Test]
    public void UnknownNamedWindowsFailBeforeTriggerCallbacks()
    {
        var callbacks = 0;
        using var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "mark",
            1,
            values =>
            {
                callbacks++;
                return values[0];
            });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(value INTEGER)");
        Execute(
            connection,
            "CREATE TRIGGER data_after AFTER INSERT ON data BEGIN "
                + "SELECT mark(NEW.value); SELECT row_number() OVER missing; END");

        Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "INSERT INTO data VALUES (1)"))!
            .Message.Should().Be("no such window: missing");
        callbacks.Should().Be(0);
        ReadRows(connection, "SELECT value FROM data").Should().BeEmpty();
    }

    [Test]
    public void TriggerChangesDoNotChangeTheOuterCandidateSet()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE data(id INTEGER PRIMARY KEY, value INTEGER)",
                "INSERT INTO data VALUES (1, 1), (2, 1)",
                "CREATE TRIGGER first_after AFTER UPDATE ON data WHEN NEW.id = 1 BEGIN "
                    + "UPDATE data SET value = 0 WHERE id = 2; END",
                "UPDATE data SET value = 2 WHERE value = 1",
            ],
            "SELECT id, value FROM data ORDER BY id");
    }

    [Test]
    public void UpsertUpdateOfAndReplaceDeleteOnlyTriggersMatchSqlite()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE data(key INTEGER PRIMARY KEY, value TEXT)",
                "CREATE TABLE trace(value TEXT)",
                "CREATE TRIGGER data_updated AFTER UPDATE OF value ON data BEGIN "
                    + "INSERT INTO trace VALUES ('U:' || NEW.value); END",
                "INSERT INTO data VALUES (1, 'old')",
                "INSERT INTO data VALUES (1, 'new') "
                    + "ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            ],
            "SELECT value FROM trace ORDER BY rowid");

        AssertMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT UNIQUE)",
                "CREATE TABLE trace(value TEXT)",
                "CREATE TRIGGER data_deleted AFTER DELETE ON data BEGIN "
                    + "INSERT INTO trace VALUES ('D:' || OLD.id); END",
                "INSERT INTO data VALUES (1, 'same')",
                "INSERT OR REPLACE INTO data VALUES (2, 'same')",
            ],
            "SELECT value FROM trace ORDER BY rowid");
    }

    [Test]
    public void CheckConflictsAndWithDmlFailKeepSqlitePrefixes()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE data(value INTEGER CHECK(value > 0))",
                "CREATE TABLE trace(value INTEGER)",
                "CREATE TRIGGER data_after AFTER INSERT ON data BEGIN "
                    + "INSERT INTO trace VALUES (NEW.value); END",
                "INSERT OR IGNORE INTO data VALUES (1), (-1), (2)",
            ],
            "SELECT 'data', value FROM data "
                + "UNION ALL SELECT 'trace', value FROM trace ORDER BY 1, 2");

        AssertErrorAndStateMatchesSqlite(
            [
                "CREATE TABLE data(value INTEGER)",
                "CREATE TABLE trace(value INTEGER)",
                "CREATE TRIGGER data_before BEFORE INSERT ON data BEGIN "
                    + "INSERT INTO trace VALUES (NEW.value); "
                    + "SELECT CASE WHEN NEW.value = 2 THEN RAISE(FAIL, 'cte-fail') END; END",
            ],
            "WITH input(value) AS (VALUES (1), (2), (3)) "
                + "INSERT INTO data SELECT value FROM input",
            "SELECT 'data', value FROM data "
                + "UNION ALL SELECT 'trace', value FROM trace ORDER BY 1, 2");
    }

    [Test]
    public void OuterConflictPolicyDoesNotEraseBodyUpsert()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE source(id INTEGER PRIMARY KEY)",
                "CREATE TABLE sink(id INTEGER PRIMARY KEY)",
                "INSERT INTO sink VALUES (1)",
                "CREATE TRIGGER source_after AFTER INSERT ON source BEGIN "
                    + "INSERT INTO sink VALUES (1) ON CONFLICT(id) DO NOTHING; END",
                "INSERT OR ABORT INTO source VALUES (1)",
            ],
            "SELECT (SELECT COUNT(*) FROM source), (SELECT COUNT(*) FROM sink)");
    }

    [Test]
    public void MultirowUpsertEvaluatesAllValuesBeforeRowTriggers()
    {
        var managedCallbacks = new List<string>();
        var sqliteCallbacks = new List<string>();
        using var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "tap",
            2,
            values =>
            {
                managedCallbacks.Add(values[0].AsText());
                return values[1];
            });
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        sqlite.CreateFunction<string, long, long>(
            "tap",
            (phase, value) =>
            {
                sqliteCallbacks.Add(phase);
                return value;
            });
        var setup = new[]
        {
            "CREATE TABLE data(id INTEGER PRIMARY KEY, value INTEGER)",
            "CREATE TRIGGER data_before BEFORE INSERT ON data "
                + "BEGIN SELECT tap('trigger-' || NEW.id, NEW.id); END",
        };
        foreach (var sql in setup)
        {
            Execute(managed, sql);
            Execute(sqlite, sql);
        }

        var statement = "INSERT INTO data VALUES "
            + "(1, tap('assign-1', 1)), (2, tap('assign-2', 2)), (3, tap('assign-3', 3)) "
            + "ON CONFLICT(id) DO UPDATE SET value = excluded.value";
        Execute(managed, statement);
        Execute(sqlite, statement);
        managedCallbacks.Should().Equal(sqliteCallbacks);
    }

    [Test]
    public void DuplicateViewInsertColumnsUseTheFirstValue()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE trace(value INTEGER)",
                "CREATE VIEW projected AS SELECT value FROM trace",
                "CREATE TRIGGER projected_insert INSTEAD OF INSERT ON projected BEGIN "
                    + "INSERT INTO trace VALUES (NEW.value); END",
                "INSERT INTO projected(value, value) VALUES (1, 2)",
            ],
            "SELECT value FROM trace");
    }

    [Test]
    public void TriggerDependentColumnAndTableRenamesRewriteBodies()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(id INTEGER)");
        Execute(connection, "CREATE TABLE trace(id INTEGER)");
        Execute(
            connection,
            "CREATE TRIGGER data_after AFTER INSERT ON data "
                + "BEGIN INSERT INTO trace VALUES (NEW.id); END");

        // RENAME TABLE rewrites trigger bodies, so it succeeds and keeps firing.
        Execute(connection, "ALTER TABLE data RENAME TO renamed");
        ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name='data_after'").Single()[0].AsText()
            .Should().Be(
                "CREATE TRIGGER data_after AFTER INSERT ON renamed "
                    + "BEGIN INSERT INTO trace VALUES (NEW.id); END");

        Execute(connection, "ALTER TABLE renamed RENAME COLUMN id TO value");
        ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name='data_after'").Single()[0].AsText()
            .Should().Be(
                "CREATE TRIGGER data_after AFTER INSERT ON renamed "
                    + "BEGIN INSERT INTO trace VALUES (NEW.value); END");

        Execute(connection, "INSERT INTO renamed VALUES (1)");
        ReadRows(connection, "SELECT id FROM trace").Should().ContainSingle()
            .Which[0].Should().Be(SqlValue.Integer(1));
    }

    [Test]
    public void FileTriggerDependencyScanCoversInsertSourcesAndCurrentTime()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("trigger-dependencies.db", fileSystem))
        {
            database.RegisterScalarFunction("custom_value", 1, values => values[0]);
            using var connection = database.Connect();
            Execute(connection, "CREATE TABLE data(id INTEGER)");
            Execute(connection, "CREATE TABLE trace(id INTEGER)");
            Assert.Throws<EmbeddedSqlException>(
                () => Execute(
                    connection,
                    "CREATE TRIGGER callback_after AFTER INSERT ON data BEGIN "
                        + "INSERT INTO trace SELECT custom_value(NEW.id); END"));
            Execute(
                connection,
                "CREATE TRIGGER time_after AFTER INSERT ON data "
                    + "WHEN CURRENT_TIMESTAMP IS NOT NULL "
                    + "BEGIN INSERT INTO trace VALUES (NEW.id); END");
        }

        using var reopened = EmbeddedDatabase.OpenFile("trigger-dependencies.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        Execute(reopenedConnection, "INSERT INTO data VALUES (1)");
        ReadRows(reopenedConnection, "SELECT id FROM trace").Should().ContainSingle()
            .Which[0].Should().Be(SqlValue.Integer(1));
    }

    [Test]
    public void NestedQueryAndReplaceDeleteProgramsPreflightBeforeMutation()
    {
        var callbacks = 0;
        using var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "mark",
            1,
            values =>
            {
                callbacks++;
                return values[0];
            });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE source(id INTEGER)");
        Execute(connection, "CREATE TABLE empty_target(value INTEGER)");
        Execute(
            connection,
            "CREATE TRIGGER source_after AFTER INSERT ON source BEGIN "
                + "UPDATE empty_target SET value = (SELECT value FROM missing); END");
        Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "INSERT INTO source VALUES (mark(1))"))!
            .Message.Should().Contain("no such table: missing");
        callbacks.Should().Be(0);
        ReadRows(connection, "SELECT id FROM source").Should().BeEmpty();

        Execute(connection, "DROP TRIGGER source_after");
        Execute(connection, "CREATE TABLE replacement(id INTEGER PRIMARY KEY, value TEXT UNIQUE)");
        Execute(connection, "INSERT INTO replacement VALUES (1, 'same')");
        Execute(
            connection,
            "CREATE TRIGGER replacement_delete BEFORE DELETE ON replacement "
                + "BEGIN SELECT OLD.missing; END");
        Execute(connection, "PRAGMA recursive_triggers = ON");
        Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "INSERT OR REPLACE INTO replacement VALUES (2, 'same')"))!
            .Message.Should().Contain("no such column: OLD.missing");
        ReadRows(connection, "SELECT id, value FROM replacement").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(1), SqlValue.Text("same"));
    }

    [Test]
    public void ForeignKeyActionTriggerFailureUsesForeignKeyValidation()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "PRAGMA foreign_keys = ON");
        Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY)");
        Execute(
            connection,
            "CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER "
                + "REFERENCES parent(id) ON UPDATE CASCADE ON DELETE CASCADE)");
        Execute(connection, "INSERT INTO parent VALUES (1)");
        Execute(connection, "INSERT INTO child VALUES (10, 1)");
        Execute(
            connection,
            "CREATE TRIGGER child_before BEFORE UPDATE ON child BEGIN "
                + "DELETE FROM parent WHERE id = NEW.parent_id; END");

        Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "UPDATE parent SET id = 2 WHERE id = 1"))!
            .Message.Should().Contain("FOREIGN KEY constraint failed");
        ReadRows(connection, "SELECT id FROM parent").Should().ContainSingle()
            .Which[0].Should().Be(SqlValue.Integer(1));
        ReadRows(connection, "SELECT id, parent_id FROM child").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(10), SqlValue.Integer(1));
    }

    [Test]
    public void AttachedSchemaNamedNewPreservesPseudoRowReferencesAfterReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var main = EmbeddedDatabase.OpenFile("named-new-main.db", fileSystem))
        using (var connection = main.Connect())
        {
            Execute(connection, "ATTACH DATABASE 'named-new-aux.db' AS new");
            Execute(connection, "CREATE TABLE new.data(id INTEGER)");
            Execute(connection, "CREATE TABLE new.trace(id INTEGER)");
            Execute(
                connection,
                "CREATE TRIGGER new.data_after AFTER INSERT ON new.data "
                    + "BEGIN INSERT INTO trace VALUES (NEW.id); END");
            Execute(connection, "DETACH new");
        }

        using var reopened = EmbeddedDatabase.OpenFile("named-new-aux.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        Execute(reopenedConnection, "INSERT INTO data VALUES (7)");
        ReadRows(reopenedConnection, "SELECT id FROM trace").Should().ContainSingle()
            .Which[0].Should().Be(SqlValue.Integer(7));
    }

    [Test]
    public void TriggerIndependentColumnRenameRemainsSupported()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(id INTEGER)");
        Execute(connection, "CREATE TABLE trace(value TEXT)");
        Execute(
            connection,
            "CREATE TRIGGER data_after AFTER INSERT ON data "
                + "BEGIN INSERT INTO trace VALUES ('inserted'); END");

        Execute(connection, "ALTER TABLE data RENAME COLUMN id TO value");
        Execute(connection, "INSERT INTO data VALUES (1)");
        ReadRows(connection, "SELECT value FROM trace").Should().ContainSingle()
            .Which[0].Should().Be(SqlValue.Text("inserted"));
    }

    [Test]
    public void OuterIgnoreAppliesToTriggerUpdateConflicts()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE source(id INTEGER PRIMARY KEY)",
                "CREATE TABLE sink(id INTEGER PRIMARY KEY, value INTEGER UNIQUE)",
                "INSERT INTO sink VALUES (1, 1), (2, 2)",
                "CREATE TRIGGER source_after AFTER INSERT ON source BEGIN "
                    + "UPDATE sink SET value = 2 WHERE id = 1; END",
                "INSERT OR IGNORE INTO source VALUES (1)",
            ],
            "SELECT (SELECT COUNT(*) FROM source), "
                + "(SELECT value FROM sink WHERE id = 1), (SELECT value FROM sink WHERE id = 2)");
    }

    [Test]
    public void InsertOrPolicyCanAccompanyTriggeredUpsert()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT)",
                "CREATE TABLE trace(value TEXT)",
                "CREATE TRIGGER data_update AFTER UPDATE OF value ON data BEGIN "
                    + "INSERT INTO trace VALUES (NEW.value); END",
                "INSERT INTO data VALUES (1, 'old')",
                "INSERT OR IGNORE INTO data VALUES (1, 'new') "
                    + "ON CONFLICT(id) DO UPDATE SET value = excluded.value",
            ],
            "SELECT value FROM trace");
    }

    [Test]
    public void RecreatedTriggerOrderPersistsWhenSchemaTextIsOtherwiseIdentical()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("trigger-order.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE data(id INTEGER)");
            Execute(connection, "CREATE TABLE trace(value TEXT)");
            Execute(
                connection,
                "CREATE TRIGGER first AFTER INSERT ON data "
                    + "BEGIN INSERT INTO trace VALUES ('first'); END");
            Execute(
                connection,
                "CREATE TRIGGER second AFTER INSERT ON data "
                    + "BEGIN INSERT INTO trace VALUES ('second'); END");
            Execute(connection, "DROP TRIGGER first");
            Execute(
                connection,
                "CREATE TRIGGER first AFTER INSERT ON data "
                    + "BEGIN INSERT INTO trace VALUES ('first'); END");
        }

        using var reopened = EmbeddedDatabase.OpenFile("trigger-order.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        Execute(reopenedConnection, "INSERT INTO data VALUES (1)");
        ReadRows(reopenedConnection, "SELECT value FROM trace ORDER BY rowid")
            .Select(row => row[0].AsText())
            .Should().Equal("first", "second");
    }

    [Test]
    public void FileTriggersRejectImplicitCustomCollationDependencies()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("trigger-collation.db", fileSystem);
        database.RegisterCollation("CUSTOM", string.CompareOrdinal);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(value TEXT COLLATE CUSTOM)");
        Execute(connection, "CREATE TABLE trace(value TEXT)");

        Assert.Throws<EmbeddedSqlException>(
            () => Execute(
                connection,
                "CREATE TRIGGER data_after AFTER INSERT ON data BEGIN "
                    + "INSERT INTO trace SELECT DISTINCT value FROM data; END"))!
            .Message.Should().Contain("custom collation 'CUSTOM'");
    }

    [Test]
    public void QualifiedDropTriggerAndQuotedRaiseFunctionAreSupported()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("drop-trigger-main.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "ATTACH DATABASE 'drop-trigger-aux.db' AS aux");
            Execute(connection, "CREATE TABLE aux.data(id INTEGER)");
            Execute(
                connection,
                "CREATE TRIGGER aux.data_after AFTER INSERT ON aux.data BEGIN SELECT NEW.id; END");
            Execute(connection, "DROP TRIGGER aux.data_after");
            ReadRows(
                    connection,
                    "SELECT COUNT(*) FROM aux.sqlite_schema WHERE type = 'trigger'")
                .Should().ContainSingle().Which[0].Should().Be(SqlValue.Integer(0));
        }

        var calls = 0;
        using var memory = new EmbeddedDatabase();
        memory.RegisterScalarFunction(
            "RAISE",
            1,
            values =>
            {
                calls++;
                return values[0];
            });
        using var memoryConnection = memory.Connect();
        Execute(memoryConnection, "CREATE TABLE data(id INTEGER)");
        Execute(
            memoryConnection,
            "CREATE TRIGGER data_after AFTER INSERT ON data BEGIN SELECT \"RAISE\"(NEW.id); END");
        Execute(memoryConnection, "INSERT INTO data VALUES (1)");
        calls.Should().Be(1);
    }

    [Test]
    public void UpsertNonTargetConflictsAndDoUpdateTriggersUseSqlitePolicies()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT UNIQUE)",
                "CREATE TABLE trace(id INTEGER)",
                "CREATE TRIGGER data_after AFTER INSERT ON data BEGIN "
                    + "INSERT INTO trace VALUES (NEW.id); END",
                "INSERT INTO data VALUES (1, 'existing')",
            ],
            "INSERT OR FAIL INTO data VALUES (2, 'new'), (3, 'existing') "
                + "ON CONFLICT(id) DO NOTHING",
            "SELECT 'data', id FROM data "
                + "UNION ALL SELECT 'trace', id FROM trace ORDER BY 1, 2");

        AssertErrorAndStateMatchesSqlite(
            [
                "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT)",
                "CREATE TABLE audit(id INTEGER PRIMARY KEY)",
                "INSERT INTO data VALUES (1, 'old')",
                "INSERT INTO audit VALUES (1)",
                "CREATE TRIGGER data_updated AFTER UPDATE ON data BEGIN "
                    + "INSERT INTO audit VALUES (1); END",
            ],
            "INSERT OR IGNORE INTO data VALUES (1, 'new') "
                + "ON CONFLICT(id) DO UPDATE SET value = excluded.value",
            "SELECT id, value FROM data");
    }

    [Test]
    public void OuterIgnoreDoesNotSuppressForeignKeyRestrict()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "CREATE TABLE parent(id INTEGER PRIMARY KEY)",
                "CREATE TABLE child(parent_id INTEGER REFERENCES parent(id) ON UPDATE RESTRICT)",
                "CREATE TABLE source(id INTEGER)",
                "INSERT INTO parent VALUES (1)",
                "INSERT INTO child VALUES (1)",
                "CREATE TRIGGER source_after AFTER INSERT ON source BEGIN "
                    + "UPDATE parent SET id = 2 WHERE id = 1; "
                    + "UPDATE child SET parent_id = 2; END",
            ],
            "INSERT OR IGNORE INTO source VALUES (1)",
            "SELECT (SELECT id FROM parent), (SELECT parent_id FROM child), "
                + "(SELECT COUNT(*) FROM source)");
    }

    [Test]
    public void ColumnRenameRewritesDependenciesFromOtherTriggerTargets()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE target(value INTEGER)");
        Execute(connection, "CREATE TABLE source(id INTEGER)");
        Execute(connection, "CREATE TABLE trace(value INTEGER)");
        Execute(
            connection,
            "CREATE TRIGGER source_after AFTER INSERT ON source BEGIN "
                + "INSERT INTO trace SELECT value FROM target; END");

        Execute(connection, "ALTER TABLE target RENAME COLUMN value TO renamed");
        ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name='source_after'").Single()[0].AsText()
            .Should().Be(
                "CREATE TRIGGER source_after AFTER INSERT ON source BEGIN "
                    + "INSERT INTO trace SELECT renamed FROM target; END");

        Execute(connection, "INSERT INTO target VALUES (7)");
        Execute(connection, "INSERT INTO source VALUES (1)");
        ReadRows(connection, "SELECT value FROM trace").Should().ContainSingle()
            .Which[0].Should().Be(SqlValue.Integer(7));
    }

    [Test]
    public void TransitiveForeignKeyCyclesPreflightBeforeParentCallbacks()
    {
        var callbacks = 0;
        using var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "mark",
            1,
            values =>
            {
                callbacks++;
                return values[0];
            });
        using var connection = database.Connect();
        Execute(connection, "PRAGMA foreign_keys = ON");
        Execute(connection, "PRAGMA recursive_triggers = ON");
        Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY)");
        Execute(
            connection,
            "CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER "
                + "REFERENCES parent(id) ON DELETE CASCADE)");
        Execute(
            connection,
            "CREATE TABLE grandchild(id INTEGER PRIMARY KEY, child_id INTEGER "
                + "REFERENCES child(id) ON DELETE CASCADE)");
        Execute(connection, "CREATE TRIGGER parent_before BEFORE DELETE ON parent BEGIN SELECT mark(OLD.id); END");
        Execute(
            connection,
            "CREATE TRIGGER grandchild_after AFTER DELETE ON grandchild BEGIN "
                + "DELETE FROM grandchild WHERE id = OLD.id; END");
        Execute(connection, "INSERT INTO parent VALUES (1)");
        Execute(connection, "INSERT INTO child VALUES (10, 1)");
        Execute(connection, "INSERT INTO grandchild VALUES (100, 10)");

        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "DELETE FROM parent"))!
            .Message.Should().Be("too many levels of trigger recursion");
        callbacks.Should().Be(0);
        ReadRows(connection, "SELECT id FROM parent").Should().ContainSingle();
    }

    [Test]
    public void InsteadOfCyclePreflightRunsBeforeViewProjectionCallbacks()
    {
        var callbacks = 0;
        using var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "mark",
            1,
            values =>
            {
                callbacks++;
                return values[0];
            });
        using var connection = database.Connect();
        Execute(connection, "PRAGMA recursive_triggers = ON");
        Execute(connection, "CREATE TABLE data(id INTEGER)");
        Execute(connection, "INSERT INTO data VALUES (1)");
        Execute(connection, "CREATE VIEW projected AS SELECT mark(id) AS id FROM data");
        Execute(
            connection,
            "CREATE TRIGGER projected_update INSTEAD OF UPDATE ON projected BEGIN "
                + "UPDATE projected SET id = NEW.id; END");

        Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "UPDATE projected SET id = 2"))!
            .Message.Should().Be("too many levels of trigger recursion");
        callbacks.Should().Be(0);
    }

    [Test]
    public void QuotedCurrentDateColumnRemainsAColumnInTriggerBodies()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE data(\"CURRENT_DATE\" TEXT)",
                "CREATE TABLE trace(value TEXT)",
                "CREATE TRIGGER data_after AFTER INSERT ON data BEGIN "
                    + "INSERT INTO trace SELECT \"CURRENT_DATE\" FROM data; END",
                "INSERT INTO data VALUES ('sentinel')",
            ],
            "SELECT value FROM trace");
    }

    [Test]
    public void QueryAliasesShadowOldAndNewPseudoRows()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT)",
                "CREATE TABLE other(value TEXT)",
                "CREATE TABLE trace(value TEXT)",
                "INSERT INTO data VALUES (1, 'trigger-old')",
                "INSERT INTO other VALUES ('query-old')",
                "CREATE TRIGGER data_after AFTER UPDATE ON data BEGIN "
                    + "INSERT INTO trace SELECT old.value FROM other AS old; END",
                "UPDATE data SET value = 'new' WHERE id = 1",
            ],
            "SELECT value FROM trace");
    }

    [Test]
    public void FileTriggerCollationScanIsNestedAndAllowsBuiltins()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("nested-collation.db", fileSystem))
        {
            database.RegisterCollation("CUSTOM", string.CompareOrdinal);
            using var connection = database.Connect();
            Execute(connection, "CREATE TABLE source(id INTEGER)");
            Execute(connection, "CREATE TABLE custom_values(value TEXT COLLATE CUSTOM)");
            Execute(connection, "CREATE TABLE trace(value TEXT)");
            Assert.Throws<EmbeddedSqlException>(
                () => Execute(
                    connection,
                    "CREATE TRIGGER source_after AFTER INSERT ON source BEGIN "
                        + "UPDATE trace SET value = (SELECT DISTINCT value FROM custom_values); END"))!
                .Message.Should().Contain("custom collation 'CUSTOM'");
            Execute(
                connection,
                "CREATE TRIGGER builtin_after AFTER INSERT ON source BEGIN "
                    + "INSERT INTO trace VALUES (NEW.id COLLATE NOCASE); END");
        }

        using var reopened = EmbeddedDatabase.OpenFile("nested-collation.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        Execute(reopenedConnection, "INSERT INTO source VALUES (1)");
        ReadRows(reopenedConnection, "SELECT value FROM trace").Should().ContainSingle()
            .Which[0].Should().Be(SqlValue.Text("1"));
    }

    [Test]
    public void AutoIncrementTriggerAttemptsAndRowidsMatchSqlite()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE data(id INTEGER PRIMARY KEY AUTOINCREMENT, value TEXT)",
                "CREATE TABLE trace(phase TEXT, id INTEGER)",
                "CREATE TRIGGER data_before BEFORE INSERT ON data WHEN NEW.value = 'outer' BEGIN "
                    + "INSERT INTO trace VALUES ('before', NEW.rowid); "
                    + "INSERT INTO data(value) VALUES ('inner'); END",
                "CREATE TRIGGER data_after AFTER INSERT ON data BEGIN "
                    + "INSERT INTO trace VALUES ('after', NEW.id); END",
                "INSERT INTO data(value) VALUES ('outer')",
            ],
            "SELECT 'data', id, value FROM data "
                + "UNION ALL SELECT 'trace-' || phase, id, NULL FROM trace "
                + "UNION ALL SELECT 'sequence', seq, NULL FROM sqlite_sequence ORDER BY 1, 2");

        AssertMatchesSqlite(
            [
                "CREATE TABLE data(id INTEGER PRIMARY KEY AUTOINCREMENT, value TEXT)",
                "CREATE TRIGGER data_before BEFORE INSERT ON data WHEN NEW.value = 'skip' "
                    + "BEGIN SELECT RAISE(IGNORE); END",
                "INSERT INTO data(value) VALUES ('skip'), ('kept')",
            ],
            "SELECT 'data', id FROM data "
                + "UNION ALL SELECT 'sequence', seq FROM sqlite_sequence ORDER BY 1");

        AssertMatchesSqlite(
            [
                "CREATE TABLE data(id INTEGER PRIMARY KEY AUTOINCREMENT, value TEXT)",
                "CREATE TRIGGER data_before BEFORE INSERT ON data WHEN NEW.value = 'outer' "
                    + "BEGIN INSERT INTO data(value) VALUES ('inner'); END",
                "INSERT INTO data VALUES (100, 'outer')",
            ],
            "SELECT 'data', id FROM data "
                + "UNION ALL SELECT 'sequence', seq FROM sqlite_sequence ORDER BY 1, 2");

        AssertErrorAndStateMatchesSqlite(
            [
                "CREATE TABLE data(id INTEGER PRIMARY KEY AUTOINCREMENT, value TEXT)",
                "CREATE TRIGGER data_before BEFORE INSERT ON data WHEN NEW.value = 'fail' "
                    + "BEGIN SELECT RAISE(FAIL, 'sequence-fail'); END",
            ],
            "INSERT INTO data(value) VALUES ('kept'), ('fail')",
            "SELECT 'data', id FROM data "
                + "UNION ALL SELECT 'sequence', seq FROM sqlite_sequence ORDER BY 1");
    }

    [Test]
    public void TriggerMutationsHonorPartialExpressionIndexes()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "CREATE TABLE source(id INTEGER PRIMARY KEY, value INTEGER)",
                "CREATE TABLE target(id INTEGER PRIMARY KEY, value INTEGER)",
                "CREATE UNIQUE INDEX target_expression ON target((value + 1)) WHERE value > 0",
                "CREATE TRIGGER source_after AFTER INSERT ON source BEGIN "
                    + "INSERT INTO target VALUES (NEW.id, NEW.value); END",
                "INSERT INTO source VALUES (1, 5)",
            ],
            "INSERT INTO source VALUES (2, 5)",
            "SELECT 'source', id, value FROM source "
                + "UNION ALL SELECT 'target', id, value FROM target ORDER BY 1, 2");
    }

    [Test]
    public void TriggerUpsertExpressionTargetsValidateQualifiersBeforeCallbacks()
    {
        var callbacks = 0;
        using var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "mark",
            1,
            values =>
            {
                callbacks++;
                return values[0];
            });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE source(id INTEGER)");
        Execute(connection, "CREATE TABLE target(value TEXT)");
        Execute(connection, "CREATE UNIQUE INDEX target_lower ON target(lower(value))");
        Execute(
            connection,
            "CREATE TRIGGER source_after AFTER INSERT ON source BEGIN "
                + "SELECT mark(NEW.id); "
                + "INSERT INTO target VALUES ('A') "
                + "ON CONFLICT(lower(nope.value)) DO UPDATE SET value = excluded.value; END");

        Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "INSERT INTO source VALUES (1)"))!
            .Message.Should().Contain("no such column: nope.value");
        callbacks.Should().Be(0);
        ReadRows(connection, "SELECT id FROM source").Should().BeEmpty();

        Execute(connection, "DROP TRIGGER source_after");
        Execute(
            connection,
            "CREATE TRIGGER source_after AFTER INSERT ON source BEGIN "
                + "INSERT INTO target VALUES ('A') "
                + "ON CONFLICT(lower(target.value)) DO UPDATE SET value = excluded.value; END");
        Execute(connection, "INSERT INTO source VALUES (1)");
        ReadRows(connection, "SELECT value FROM target").Should().ContainSingle()
            .Which[0].Should().Be(SqlValue.Text("A"));
    }

    [Test]
    public void TriggerBodiesComposeWindowsJoinsCompoundsAndOperators()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE source(id INTEGER)",
                "CREATE TABLE lhs(id INTEGER, value TEXT)",
                "CREATE TABLE rhs(id INTEGER, value TEXT)",
                "CREATE TABLE audit(sequence INTEGER, value TEXT)",
                "INSERT INTO lhs VALUES (1, 'left-1'), (2, 'left-2')",
                "INSERT INTO rhs VALUES (1, 'right-1'), (2, 'right-2')",
                "CREATE TRIGGER source_after AFTER INSERT ON source "
                    + "WHEN (NEW.id & 1) = 1 BEGIN "
                    + "INSERT INTO audit "
                    + "SELECT row_number() OVER (ORDER BY lhs.id), lhs.value || ':' || rhs.value "
                    + "FROM lhs JOIN rhs ON lhs.id = rhs.id; "
                    + "INSERT INTO audit "
                    + "SELECT 99, CAST(value AS TEXT) FROM ("
                    + "SELECT NEW.id << 1 AS value UNION ALL SELECT NEW.id + 10 EXCEPT SELECT -1"
                    + "); END",
                "INSERT INTO source VALUES (3)",
            ],
            "SELECT sequence, value FROM audit ORDER BY rowid");
    }

    [Test]
    public void TempSchemaTriggersAreConnectionLocalAndUseTempCatalog()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TEMP TABLE temp_source(id INTEGER)");
        Execute(connection, "CREATE TEMP TABLE temp_audit(id INTEGER)");
        Execute(
            connection,
            "CREATE TRIGGER temp.source_after AFTER INSERT ON temp.temp_source "
                + "BEGIN INSERT INTO temp_audit VALUES (NEW.id); END");
        Execute(connection, "INSERT INTO temp.temp_source VALUES (7)");

        ReadRows(connection, "SELECT id FROM temp.temp_audit").Should().ContainSingle()
            .Which[0].Should().Be(SqlValue.Integer(7));
        ReadRows(
                connection,
                "SELECT name FROM temp.sqlite_schema WHERE type = 'trigger'")
            .Should().ContainSingle().Which[0].Should().Be(SqlValue.Text("source_after"));
    }

    [Test]
    public void DropColumnValidatesFullRowTriggerDependencies()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(id INTEGER, removable TEXT)");
        Execute(connection, "CREATE TABLE audit(value TEXT)");
        Execute(
            connection,
            "CREATE TRIGGER independent AFTER INSERT ON data BEGIN "
                + "SELECT RAISE(IGNORE) WHERE NEW.id < 0; "
                + "INSERT INTO audit SELECT CAST(NEW.id AS TEXT); END");

        Execute(connection, "ALTER TABLE data DROP COLUMN removable");
        Execute(connection, "INSERT INTO data VALUES (1)");
        ReadRows(connection, "SELECT value FROM audit").Should().ContainSingle()
            .Which[0].Should().Be(SqlValue.Text("1"));

        Execute(connection, "CREATE TABLE dependent(id INTEGER, removable TEXT)");
        Execute(
            connection,
            "CREATE TRIGGER dependent_after AFTER INSERT ON dependent "
                + "WHEN NEW.removable IS NOT NULL BEGIN SELECT NEW.removable; END");
        Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "ALTER TABLE dependent DROP COLUMN removable"))!
            .Message.Should().Contain("error in trigger dependent_after after drop column");
        ReadRows(connection, "PRAGMA table_info(dependent)").Should().HaveCount(2);

        Execute(connection, "CREATE TABLE window_source(id INTEGER)");
        Execute(connection, "CREATE TABLE window_target(id INTEGER, removable INTEGER)");
        Execute(
            connection,
            "CREATE TRIGGER window_after AFTER INSERT ON window_source BEGIN "
                + "SELECT row_number() OVER named FROM window_target "
                + "WINDOW named AS (ORDER BY removable); END");
        Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "ALTER TABLE window_target DROP COLUMN removable"))!
            .Message.Should().Contain("error in trigger window_after after drop column");
        ReadRows(connection, "PRAGMA table_info(window_target)").Should().HaveCount(2);
    }

    [Test]
    public void TriggerBodyUpsertDoUpdateSetMayReferenceNewColumns()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE balances(id INTEGER PRIMARY KEY, amount INTEGER)",
                "CREATE TABLE transactions(id INTEGER PRIMARY KEY, account_id INTEGER, delta INTEGER)",
                "INSERT INTO balances VALUES (1, 100)",
                "CREATE TRIGGER apply_txn AFTER INSERT ON transactions BEGIN "
                    + "INSERT INTO balances VALUES (NEW.account_id, NEW.delta) "
                    + "ON CONFLICT(id) DO UPDATE SET amount = amount + NEW.delta; END",
                "INSERT INTO transactions VALUES (1, 1, 50)",
            ],
            "SELECT * FROM balances");
    }

    [Test]
    public void TriggerBodyUpsertDoUpdateWhereMayReferenceNewColumns()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE balances(id INTEGER PRIMARY KEY, amount INTEGER)",
                "CREATE TABLE transactions(id INTEGER PRIMARY KEY, account_id INTEGER, delta INTEGER)",
                "INSERT INTO balances VALUES (1, 100)",
                "CREATE TRIGGER apply_txn AFTER INSERT ON transactions BEGIN "
                    + "INSERT INTO balances VALUES (NEW.account_id, NEW.delta) "
                    + "ON CONFLICT(id) DO UPDATE SET amount = amount + NEW.delta "
                    + "WHERE NEW.delta > 0; END",
                "INSERT INTO transactions VALUES (1, 1, 50)",
            ],
            "SELECT * FROM balances");
    }

    [Test]
    public void TriggerBodyUpsertDoUpdateWhereFiltersOutNegativeDeltaLikeSqlite()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE balances(id INTEGER PRIMARY KEY, amount INTEGER)",
                "CREATE TABLE transactions(id INTEGER PRIMARY KEY, account_id INTEGER, delta INTEGER)",
                "INSERT INTO balances VALUES (1, 100)",
                "CREATE TRIGGER apply_txn AFTER INSERT ON transactions BEGIN "
                    + "INSERT INTO balances VALUES (NEW.account_id, NEW.delta) "
                    + "ON CONFLICT(id) DO UPDATE SET amount = amount + NEW.delta "
                    + "WHERE NEW.delta > 0; END",
                "INSERT INTO transactions VALUES (1, 1, -50)",
            ],
            "SELECT * FROM balances");
    }

    private static void AssertMatchesSqlite(IReadOnlyList<string> setup, string query)
    {
        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        foreach (var sql in setup)
        {
            Execute(managed, sql);
            Execute(sqlite, sql);
        }

        AssertQueriesMatch(managed, sqlite, query);
    }

    private static void AssertErrorAndStateMatchesSqlite(
        IReadOnlyList<string> setup,
        string failingSql,
        string query)
    {
        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        foreach (var sql in setup)
        {
            Execute(managed, sql);
            Execute(sqlite, sql);
        }

        var managedError = Assert.Throws<EmbeddedSqlException>(() => Execute(managed, failingSql));
        var sqliteError = Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, failingSql));
        sqliteError!.Message.Should().Contain(managedError!.Message);
        AssertQueriesMatch(managed, sqlite, query);
    }

    private static MsData.SqliteConnection OpenSqlite()
    {
        var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    private static void Execute(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void AssertQueriesMatch(
        EmbeddedConnection managed,
        MsData.SqliteConnection sqlite,
        string query)
    {
        var managedRows = ReadRows(managed, query);
        var sqliteRows = ReadRows(sqlite, query);
        managedRows.Should().HaveCount(sqliteRows.Count);
        for (var row = 0; row < sqliteRows.Count; row++)
        {
            managedRows[row].Should().HaveCount(sqliteRows[row].Length);
            for (var column = 0; column < sqliteRows[row].Length; column++)
                CellShouldMatch(managedRows[row][column], sqliteRows[row][column]);
        }
    }

    private static IReadOnlyList<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var values = new SqlValue[statement.GetColumnCount()];
            for (var column = 0; column < values.Length; column++)
                values[column] = statement.GetValue(column);
            rows.Add(values);
        }

        return rows;
    }

    private static IReadOnlyList<object?[]> ReadRows(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var values = new object?[reader.FieldCount];
            for (var column = 0; column < values.Length; column++)
                values[column] = reader.IsDBNull(column) ? null : reader.GetValue(column);
            rows.Add(values);
        }

        return rows;
    }

    private static void CellShouldMatch(SqlValue managed, object? sqlite)
    {
        switch (sqlite)
        {
            case null:
                managed.Kind.Should().Be(SqlValueKind.Null);
                break;
            case long integer:
                managed.AsInteger().Should().Be(integer);
                break;
            case double real:
                managed.AsReal().Should().BeApproximately(real, 1e-9);
                break;
            case string text:
                managed.AsText().Should().Be(text);
                break;
            case byte[] blob:
                managed.AsBlob().ToArray().Should().Equal(blob);
                break;
            default:
                throw new AssertionException($"Unsupported SQLite value type {sqlite.GetType().Name}.");
        }
    }

    private sealed class FlushFailingFileSystem(IFileSystem inner, string targetPath) : IFileSystem
    {
        private int _armed;

        public bool FileExists(string path) => inner.FileExists(path);

        public IFile OpenFile(string path, FileOpenMode mode, bool readOnly = false)
            => new FailureFile(this, inner.OpenFile(path, mode, readOnly), path == targetPath);

        public void DeleteFile(string path) => inner.DeleteFile(path);

        public void ArmFlushFailure() => Volatile.Write(ref _armed, 1);

        public void Disarm() => Volatile.Write(ref _armed, 0);

        private void FailIfArmed(bool isTarget)
        {
            if (isTarget && Interlocked.Exchange(ref _armed, 0) == 1)
                throw new IOException("Injected recursive trigger flush failure.");
        }

        private sealed class FailureFile(
            FlushFailingFileSystem owner,
            IFile innerFile,
            bool isTarget) : IFile
        {
            public long Length => innerFile.Length;

            public bool IsReadOnly => innerFile.IsReadOnly;

            public int Read(long position, Span<byte> destination) => innerFile.Read(position, destination);

            public void Write(long position, ReadOnlySpan<byte> source) => innerFile.Write(position, source);

            public void SetLength(long length) => innerFile.SetLength(length);

            public void FlushToDisk()
            {
                owner.FailIfArmed(isTarget);
                innerFile.FlushToDisk();
            }

            public void Dispose() => innerFile.Dispose();
        }
    }
}
