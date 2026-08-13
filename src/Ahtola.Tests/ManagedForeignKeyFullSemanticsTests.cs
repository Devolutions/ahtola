using AwesomeAssertions;
using Ahtola.Core;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

public sealed class ManagedForeignKeyFullSemanticsTests
{
    [Test]
    public void CompositeKeysAffinityCollationNullsAndOmittedParentColumnsMatchSqlite()
    {
        AssertMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "CREATE TABLE parent(a TEXT COLLATE NOCASE, b TEXT COLLATE RTRIM, PRIMARY KEY(a, b))",
                "CREATE TABLE child(a TEXT, b TEXT, FOREIGN KEY(a, b) REFERENCES parent)",
                "INSERT INTO parent VALUES ('alpha', 'value')",
                "INSERT INTO child VALUES ('ALPHA', 'value   '), (NULL, 'missing'), ('missing', NULL)",
            ],
            "SELECT a, b FROM child ORDER BY rowid");
    }

    [Test]
    public void CompositeChildViolationIsStatementAtomicAndMatchesSqlite()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "CREATE TABLE parent(a INTEGER, b INTEGER, PRIMARY KEY(a, b))",
                "CREATE TABLE child(id INTEGER PRIMARY KEY, a INTEGER, b INTEGER, FOREIGN KEY(a, b) REFERENCES parent(a, b))",
                "INSERT INTO parent VALUES (1, 2)",
                "INSERT INTO child VALUES (1, 1, 2)",
            ],
            "INSERT INTO child VALUES (2, 1, 2), (3, 9, 9)",
            "SELECT id, a, b FROM child ORDER BY id");
    }

    [Test]
    public void RepeatedChildColumnsInCompositeForeignKeysMatchSqlite()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "CREATE TABLE parent(a INTEGER, b INTEGER, UNIQUE(a, b))",
                "CREATE TABLE child(value INTEGER, FOREIGN KEY(value, value) REFERENCES parent(a, b))",
                "INSERT INTO parent VALUES (1, 1)",
                "INSERT INTO child VALUES (1)",
            ],
            "INSERT INTO child VALUES (2)",
            "SELECT value FROM child");
    }

    [Test]
    public void CompositeCascadeSetNullAndSetDefaultActionsMatchSqlite()
    {
        AssertMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "CREATE TABLE parent(a INTEGER, b INTEGER, PRIMARY KEY(a, b))",
                "CREATE TABLE cascaded(id INTEGER PRIMARY KEY, a INTEGER, b INTEGER, "
                    + "FOREIGN KEY(a, b) REFERENCES parent(a, b) ON UPDATE CASCADE ON DELETE CASCADE)",
                "CREATE TABLE nulled(id INTEGER PRIMARY KEY, a INTEGER, b INTEGER, "
                    + "FOREIGN KEY(a, b) REFERENCES parent(a, b) ON UPDATE CASCADE ON DELETE SET NULL)",
                "CREATE TABLE defaulted(id INTEGER PRIMARY KEY, a INTEGER DEFAULT 7, b INTEGER DEFAULT 8, "
                    + "FOREIGN KEY(a, b) REFERENCES parent(a, b) ON UPDATE CASCADE ON DELETE SET DEFAULT)",
                "INSERT INTO parent VALUES (1, 2), (7, 8)",
                "INSERT INTO cascaded VALUES (1, 1, 2)",
                "INSERT INTO nulled VALUES (1, 1, 2)",
                "INSERT INTO defaulted VALUES (1, 1, 2)",
                "UPDATE parent SET a = 3, b = 4 WHERE a = 1 AND b = 2",
                "UPDATE nulled SET a = 3, b = 4",
                "UPDATE defaulted SET a = 3, b = 4",
                "DELETE FROM parent WHERE a = 3 AND b = 4",
            ],
            "SELECT 'cascade', id, a, b FROM cascaded "
                + "UNION ALL SELECT 'null', id, a, b FROM nulled "
                + "UNION ALL SELECT 'default', id, a, b FROM defaulted ORDER BY 1");
    }

    [Test]
    public void LimitedParentDmlUsesExplicitNullOrderingForActionsAndDeferral()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "PRAGMA foreign_keys = ON");
        Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY, priority INTEGER)");
        Execute(
            connection,
            "CREATE TABLE child(parent_id INTEGER REFERENCES parent(id) "
                + "ON UPDATE CASCADE ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED)");
        Execute(connection, "INSERT INTO parent VALUES (1, NULL), (2, 10), (3, 20)");
        Execute(connection, "INSERT INTO child VALUES (1), (2), (3)");

        Execute(
            connection,
            "UPDATE parent SET id = id + 100 "
                + "ORDER BY priority ASC NULLS LAST LIMIT 1");
        Execute(
            connection,
            "DELETE FROM parent "
                + "ORDER BY priority ASC NULLS FIRST LIMIT 1");
        ReadRows(connection, "SELECT parent_id FROM child ORDER BY parent_id")
            .Select(row => row[0].AsInteger())
            .Should().Equal(3, 102);

        Execute(
            connection,
            "CREATE TABLE guarded_child(parent_id INTEGER "
                + "REFERENCES parent(id) DEFERRABLE INITIALLY DEFERRED)");
        Execute(connection, "INSERT INTO guarded_child VALUES (3)");
        Execute(connection, "BEGIN");
        Execute(connection, "DELETE FROM parent ORDER BY priority DESC NULLS LAST LIMIT 1");
        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "COMMIT"))!
            .Message.Should().Be("FOREIGN KEY constraint failed");
        Execute(connection, "ROLLBACK");
        ReadRows(connection, "SELECT id FROM parent ORDER BY id")
            .Select(row => row[0].AsInteger())
            .Should().Equal(3, 102);
    }

    [Test]
    public void SetDefaultFailureRollsBackParentAndChildrenLikeSqlite()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "CREATE TABLE parent(id INTEGER PRIMARY KEY)",
                "CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER DEFAULT 2 "
                    + "REFERENCES parent(id) ON DELETE SET DEFAULT)",
                "INSERT INTO parent VALUES (1)",
                "INSERT INTO child VALUES (10, 1)",
            ],
            "DELETE FROM parent WHERE id = 1",
            "SELECT 'parent', id, NULL FROM parent UNION ALL SELECT 'child', id, parent_id FROM child ORDER BY 1");
    }

    [Test]
    public void DeferredConstraintsCommitRepairAndSavepointsMatchSqlite()
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();

        ExecuteBoth(
            managed,
            sqlite,
            "PRAGMA foreign_keys = ON",
            "CREATE TABLE parent(id INTEGER PRIMARY KEY)",
            "CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER "
                + "REFERENCES parent(id) DEFERRABLE INITIALLY DEFERRED)",
            "BEGIN",
            "SAVEPOINT nested",
            "INSERT INTO child VALUES (1, 1)",
            "ROLLBACK TO nested",
            "RELEASE nested",
            "INSERT INTO child VALUES (2, 2)");

        AssertSameError(managed, sqlite, "COMMIT");
        AssertQueriesMatch(managed, sqlite, "SELECT id, parent_id FROM child ORDER BY id");

        ExecuteBoth(managed, sqlite, "INSERT INTO parent VALUES (2)", "COMMIT");
        AssertQueriesMatch(managed, sqlite, "SELECT id, parent_id FROM child ORDER BY id");
    }

    [Test]
    public void DeferForeignKeysPragmaDefersImmediateConstraintsAndResetsAtTransactionEnd()
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();

        ExecuteBoth(
            managed,
            sqlite,
            "PRAGMA foreign_keys = ON",
            "CREATE TABLE parent(id INTEGER PRIMARY KEY)",
            "CREATE TABLE child(parent_id INTEGER REFERENCES parent(id))",
            "BEGIN",
            "PRAGMA defer_foreign_keys = ON",
            "INSERT INTO child VALUES (1)");

        AssertQueriesMatch(managed, sqlite, "PRAGMA defer_foreign_keys");
        AssertSameError(managed, sqlite, "COMMIT");
        ExecuteBoth(managed, sqlite, "INSERT INTO parent VALUES (1)", "COMMIT");
        AssertQueriesMatch(managed, sqlite, "PRAGMA defer_foreign_keys");
    }

    [Test]
    public void DeferredSavepointRollbackAndLegacyViolationBaselineMatchSqlite()
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();

        ExecuteBoth(
            managed,
            sqlite,
            "PRAGMA foreign_keys = OFF",
            "CREATE TABLE parent(id INTEGER PRIMARY KEY)",
            "CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER "
                + "REFERENCES parent(id) DEFERRABLE INITIALLY DEFERRED)",
            "INSERT INTO child VALUES (1, 99)",
            "PRAGMA foreign_keys = ON",
            "BEGIN",
            "SAVEPOINT pending",
            "INSERT INTO child VALUES (2, 2)",
            "ROLLBACK TO pending",
            "RELEASE pending",
            "CREATE TABLE unrelated(value INTEGER)",
            "COMMIT");

        AssertQueriesMatch(managed, sqlite, "SELECT id, parent_id FROM child ORDER BY id");

        ExecuteBoth(
            managed,
            sqlite,
            "BEGIN",
            "INSERT INTO parent VALUES (99)",
            "DELETE FROM parent WHERE id = 99");
        AssertSameError(managed, sqlite, "COMMIT");
        ExecuteBoth(managed, sqlite, "ROLLBACK");
    }

    [Test]
    public void DisablingDeferForeignKeysClearsPendingViolationsLikeMicrosoftDataSqlite()
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();

        ExecuteBoth(
            managed,
            sqlite,
            "PRAGMA foreign_keys = ON",
            "CREATE TABLE parent(id INTEGER PRIMARY KEY)",
            "CREATE TABLE immediate_child(parent_id INTEGER REFERENCES parent(id))",
            "BEGIN",
            "PRAGMA defer_foreign_keys = ON",
            "INSERT INTO immediate_child VALUES (1)",
            "PRAGMA defer_foreign_keys = OFF",
            "COMMIT");
        AssertQueriesMatch(managed, sqlite, "SELECT parent_id FROM immediate_child");

        ExecuteBoth(
            managed,
            sqlite,
            "CREATE TABLE deferred_child(parent_id INTEGER "
                + "REFERENCES parent(id) DEFERRABLE INITIALLY DEFERRED)",
            "BEGIN",
            "INSERT INTO deferred_child VALUES (2)",
            "PRAGMA defer_foreign_keys = OFF",
            "COMMIT");
        AssertQueriesMatch(managed, sqlite, "SELECT parent_id FROM deferred_child");
    }

    [Test]
    public void DeferredRestrictStillFailsAtTheParentMutation()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "CREATE TABLE parent(id INTEGER PRIMARY KEY)",
                "CREATE TABLE child(parent_id INTEGER REFERENCES parent(id) "
                    + "ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED)",
                "INSERT INTO parent VALUES (1)",
                "INSERT INTO child VALUES (1)",
                "BEGIN",
            ],
            "DELETE FROM parent",
            "SELECT id FROM parent");
    }

    [Test]
    public void CascadeActionsRunChildTriggersBeforeParentTriggers()
    {
        AssertMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "CREATE TABLE log(value TEXT)",
                "CREATE TABLE parent(id INTEGER PRIMARY KEY)",
                "CREATE TABLE child(parent_id INTEGER REFERENCES parent(id) ON DELETE CASCADE)",
                "CREATE TRIGGER child_after AFTER DELETE ON child BEGIN INSERT INTO log VALUES ('child'); END",
                "CREATE TRIGGER parent_after AFTER DELETE ON parent BEGIN INSERT INTO log VALUES ('parent'); END",
                "INSERT INTO parent VALUES (1)",
                "INSERT INTO child VALUES (1)",
                "DELETE FROM parent",
            ],
            "SELECT rowid, value FROM log ORDER BY rowid");
    }

    [Test]
    public void AfterTriggersCanRepairNoActionViolationsBeforeStatementEnd()
    {
        AssertMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "CREATE TABLE parent(id INTEGER PRIMARY KEY)",
                "CREATE TABLE child(parent_id INTEGER REFERENCES parent(id))",
                "CREATE TRIGGER create_parent AFTER INSERT ON child BEGIN INSERT INTO parent VALUES (1); END",
                "INSERT INTO child VALUES (1)",
                "CREATE TRIGGER remove_children AFTER DELETE ON parent BEGIN DELETE FROM child; END",
                "DELETE FROM parent",
            ],
            "SELECT (SELECT COUNT(*) FROM parent), (SELECT COUNT(*) FROM child)");
    }

    [Test]
    public void SelfReferentialCascadeIsBoundedAndMatchesSqlite()
    {
        AssertMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "CREATE TABLE node(id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES node(id) ON DELETE CASCADE)",
                "INSERT INTO node VALUES (1, NULL), (2, 1), (3, 2), (4, 3)",
                "DELETE FROM node WHERE id = 1",
            ],
            "SELECT id, parent_id FROM node ORDER BY id");
    }

    [Test]
    public void SelfReferentialSetNullOnDeleteMatchesSqlite()
    {
        AssertMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "CREATE TABLE node(id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES node(id) ON DELETE SET NULL)",
                "INSERT INTO node VALUES (1, NULL), (2, 1), (3, 2)",
                "DELETE FROM node WHERE id = 1",
            ],
            "SELECT id, parent_id FROM node ORDER BY id");
    }

    [Test]
    public void CrossTableCascadeOnDeleteMatchesSqlite()
    {
        AssertMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "CREATE TABLE parent(id INTEGER PRIMARY KEY)",
                "CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id) ON DELETE CASCADE)",
                "INSERT INTO parent VALUES (1), (2)",
                "INSERT INTO child VALUES (10, 1), (11, 1), (20, 2)",
                "DELETE FROM parent WHERE id = 1",
            ],
            "SELECT id, parent_id FROM child ORDER BY id");
    }

    [Test]
    public void SelfReferentialCascadeOnUpdateMatchesSqlite()
    {
        AssertMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "CREATE TABLE node(id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES node(id) ON UPDATE CASCADE)",
                "INSERT INTO node VALUES (1, NULL), (2, 1), (3, 2)",
                "UPDATE node SET id = 10 WHERE id = 1",
            ],
            "SELECT id, parent_id FROM node ORDER BY id");
    }

    [Test]
    public void SelfReferentialSetNullOnUpdateMatchesSqlite()
    {
        AssertMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "CREATE TABLE node(id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES node(id) ON UPDATE SET NULL)",
                "INSERT INTO node VALUES (1, NULL), (2, 1), (3, 2)",
                "UPDATE node SET id = 10 WHERE id = 1",
            ],
            "SELECT id, parent_id FROM node ORDER BY id");
    }

    [Test]
    public void DeferredCascadeCyclesResolveWithoutRecursiveReentry()
    {
        AssertMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "CREATE TABLE first(id INTEGER PRIMARY KEY, second_id INTEGER "
                    + "REFERENCES second(id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED)",
                "CREATE TABLE second(id INTEGER PRIMARY KEY, first_id INTEGER "
                    + "REFERENCES first(id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED)",
                "BEGIN",
                "INSERT INTO first VALUES (1, 1)",
                "INSERT INTO second VALUES (1, 1)",
                "COMMIT",
                "DELETE FROM first",
            ],
            "SELECT (SELECT COUNT(*) FROM first), (SELECT COUNT(*) FROM second)");
    }

    [Test]
    public void DropTableRunsForeignKeyActionsWithoutParentTriggers()
    {
        AssertMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "CREATE TABLE log(value TEXT)",
                "CREATE TABLE cascade_parent(id INTEGER PRIMARY KEY)",
                "CREATE TABLE cascade_child(parent_id INTEGER "
                    + "REFERENCES cascade_parent(id) ON DELETE CASCADE)",
                "CREATE TRIGGER cascade_child_deleted AFTER DELETE ON cascade_child "
                    + "BEGIN INSERT INTO log VALUES ('child'); END",
                "CREATE TRIGGER cascade_parent_deleted AFTER DELETE ON cascade_parent "
                    + "BEGIN INSERT INTO log VALUES ('parent'); END",
                "INSERT INTO cascade_parent VALUES (1)",
                "INSERT INTO cascade_child VALUES (1)",
                "DROP TABLE cascade_parent",
                "CREATE TABLE null_parent(id INTEGER PRIMARY KEY)",
                "CREATE TABLE null_child(parent_id INTEGER REFERENCES null_parent(id) ON DELETE SET NULL)",
                "INSERT INTO null_parent VALUES (2)",
                "INSERT INTO null_child VALUES (2)",
                "DROP TABLE null_parent",
            ],
            "SELECT 'child', parent_id FROM null_child "
                + "UNION ALL SELECT 'log', value FROM log ORDER BY 1");
    }

    [Test]
    public void ForeignKeysSupportGeneratedColumnsAndWithoutRowidTables()
    {
        AssertMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "CREATE TABLE generated_parent(seed INTEGER, key_value INTEGER AS (seed + 1) VIRTUAL UNIQUE)",
                "CREATE TABLE generated_child(seed INTEGER, key_value INTEGER AS (seed + 1) VIRTUAL "
                    + "REFERENCES generated_parent(key_value))",
                "INSERT INTO generated_parent(seed) VALUES (10)",
                "INSERT INTO generated_child(seed) VALUES (10)",
                "CREATE TABLE parent(a INTEGER, b INTEGER, PRIMARY KEY(a, b)) WITHOUT ROWID",
                "CREATE TABLE child(a INTEGER, b INTEGER, PRIMARY KEY(a, b), "
                    + "FOREIGN KEY(a, b) REFERENCES parent(a, b) ON UPDATE CASCADE) WITHOUT ROWID",
                "INSERT INTO parent VALUES (1, 2)",
                "INSERT INTO child VALUES (1, 2)",
                "UPDATE parent SET a = 3, b = 4",
            ],
            "SELECT 'generated', seed, key_value FROM generated_child "
                + "UNION ALL SELECT 'without-rowid', a, b FROM child ORDER BY 1");
    }

    [Test]
    public void MultirowWithoutRowidParentUpdatesCascadeByOriginalKey()
    {
        AssertMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "CREATE TABLE parent(a INTEGER, b INTEGER, PRIMARY KEY(a, b)) WITHOUT ROWID",
                "CREATE TABLE child(a INTEGER, b INTEGER, PRIMARY KEY(a, b), "
                    + "FOREIGN KEY(a, b) REFERENCES parent ON UPDATE CASCADE) WITHOUT ROWID",
                "INSERT INTO parent VALUES (1, 10), (2, 20)",
                "INSERT INTO child VALUES (1, 10), (2, 20)",
                "UPDATE parent SET a = a + 10",
            ],
            "SELECT a, b FROM child ORDER BY a");
    }

    [Test]
    public void ForeignKeyErrorsIgnoreInsertConflictAlgorithmsAndReplaceRunsDeleteActions()
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();

        ExecuteBoth(
            managed,
            sqlite,
            "PRAGMA foreign_keys = ON",
            "CREATE TABLE parent(id INTEGER PRIMARY KEY, alternate INTEGER UNIQUE)",
            "CREATE TABLE child(parent_id INTEGER REFERENCES parent(id) ON DELETE CASCADE)",
            "INSERT INTO parent VALUES (1, 10)",
            "INSERT INTO child VALUES (1)");

        AssertSameError(managed, sqlite, "INSERT OR IGNORE INTO child VALUES (999)");
        ExecuteBoth(managed, sqlite, "INSERT OR REPLACE INTO parent VALUES (1, 20)");
        AssertQueriesMatch(managed, sqlite, "SELECT COUNT(*) FROM child");
    }

    [Test]
    public void ForeignKeyErrorsUseAbortTimingAndPreserveConstraintPrecedence()
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();

        ExecuteBoth(
            managed,
            sqlite,
            "PRAGMA foreign_keys = ON",
            "CREATE TABLE parent(id INTEGER PRIMARY KEY)",
            "CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id))",
            "INSERT INTO parent VALUES (1)",
            "INSERT INTO child VALUES (1, 1)",
            "BEGIN");

        AssertSameError(managed, sqlite, "INSERT OR ROLLBACK INTO child VALUES (2, 999)");
        ExecuteBoth(managed, sqlite, "INSERT INTO child VALUES (2, 1)", "ROLLBACK");
        AssertQueriesMatch(managed, sqlite, "SELECT id, parent_id FROM child ORDER BY id");

        AssertSameError(managed, sqlite, "INSERT INTO child VALUES (1, 999)");
        AssertQueriesMatch(managed, sqlite, "SELECT id, parent_id FROM child ORDER BY id");
    }

    [Test]
    public void ForeignKeysPreserveFailPartialPublicationUnlessItLeavesAnImmediateViolation()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "CREATE TABLE items(id INTEGER PRIMARY KEY)",
            ],
            "INSERT OR FAIL INTO items VALUES (1), (2), (1)",
            "SELECT id FROM items ORDER BY id");

        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "CREATE TABLE configured(id INTEGER PRIMARY KEY ON CONFLICT FAIL)",
            ],
            "INSERT INTO configured VALUES (1), (2), (1)",
            "SELECT id FROM configured ORDER BY id");

        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "CREATE TABLE parent(id INTEGER PRIMARY KEY)",
                "CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id))",
            ],
            "INSERT OR FAIL INTO child VALUES (1, 999), (2, 999), (1, 999)",
            "SELECT id, parent_id FROM child ORDER BY id");

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(
            managed,
            sqlite,
            "PRAGMA foreign_keys = ON",
            "CREATE TABLE transactional(id INTEGER PRIMARY KEY)",
            "BEGIN");
        AssertSameError(
            managed,
            sqlite,
            "INSERT OR FAIL INTO transactional VALUES (1), (2), (1)");
        AssertQueriesMatch(managed, sqlite, "SELECT id FROM transactional ORDER BY id");
        ExecuteBoth(managed, sqlite, "COMMIT");
        AssertQueriesMatch(managed, sqlite, "SELECT id FROM transactional ORDER BY id");
    }

    [Test]
    public void MatchClausesRemainSimpleAndMultipleInlineReferencesAreIndependent()
    {
        AssertMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "CREATE TABLE first_parent(id INTEGER PRIMARY KEY)",
                "CREATE TABLE second_parent(id INTEGER PRIMARY KEY)",
                "CREATE TABLE composite_parent(a INTEGER, b INTEGER, PRIMARY KEY(a, b))",
                "CREATE TABLE child(value INTEGER REFERENCES first_parent(id) REFERENCES second_parent(id), "
                    + "a INTEGER, b INTEGER, FOREIGN KEY(a, b) REFERENCES composite_parent(a, b) MATCH FULL)",
                "INSERT INTO first_parent VALUES (1)",
                "INSERT INTO second_parent VALUES (1)",
                "INSERT INTO child VALUES (1, 99, NULL)",
            ],
            "SELECT value, a, b FROM child");
    }

    [Test]
    public void RepeatedForeignKeyClausesUseTheLastActionLikeSqlite()
    {
        AssertMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "CREATE TABLE parent(id INTEGER PRIMARY KEY)",
                "CREATE TABLE child(parent_id INTEGER REFERENCES parent(id) "
                    + "ON DELETE CASCADE ON DELETE SET NULL MATCH first MATCH second)",
                "INSERT INTO parent VALUES (1)",
                "INSERT INTO child VALUES (1)",
                "DELETE FROM parent",
            ],
            "SELECT parent_id FROM child");
    }

    [Test]
    public void MultipleForeignKeysRunReferentialActionsInSqliteOrder()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "CREATE TABLE parent(id INTEGER PRIMARY KEY)",
                "CREATE TABLE child(parent_id INTEGER "
                    + "REFERENCES parent(id) ON DELETE CASCADE "
                    + "REFERENCES parent(id) ON DELETE RESTRICT)",
                "INSERT INTO parent VALUES (1)",
                "INSERT INTO child VALUES (1)",
            ],
            "DELETE FROM parent",
            "SELECT (SELECT COUNT(*) FROM parent), (SELECT COUNT(*) FROM child)");
    }

    [Test]
    public void ForeignKeyListAndCheckPragmasMatchSqlite()
    {
        var setup = new[]
        {
            "PRAGMA foreign_keys = OFF",
            "CREATE TABLE parent(a INTEGER, b INTEGER, PRIMARY KEY(a, b)) WITHOUT ROWID",
            "CREATE TABLE child(id INTEGER PRIMARY KEY, a INTEGER, b INTEGER, "
                + "FOREIGN KEY(a, b) REFERENCES parent ON UPDATE CASCADE ON DELETE SET NULL MATCH FULL)",
            "CREATE TABLE without_rowid_child(a INTEGER, b INTEGER, PRIMARY KEY(a, b), "
                + "FOREIGN KEY(a, b) REFERENCES parent) WITHOUT ROWID",
            "INSERT INTO child VALUES (1, 9, 9)",
            "INSERT INTO without_rowid_child VALUES (8, 8)",
        };

        AssertMatchesSqlite(setup, "PRAGMA foreign_key_list(child)");
        AssertMatchesSqlite(setup, "PRAGMA foreign_key_check");
        AssertMatchesSqlite(setup, "PRAGMA foreign_key_check(child)");
    }

    [Test]
    public void SchemaQualifiedParentReferencesAreRejectedLikeSqlite()
    {
        const string sql = "CREATE TABLE child(parent_id INTEGER REFERENCES main.parent(id))";
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();

        Assert.Throws<EmbeddedSqlException>(() => Execute(managed, sql));
        Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, sql));
    }

    private static void AssertMatchesSqlite(IReadOnlyList<string> setup, string query)
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        foreach (var statement in setup)
            ExecuteBoth(managed, sqlite, statement);

        AssertQueriesMatch(managed, sqlite, query);
    }

    private static void AssertErrorAndStateMatchesSqlite(
        IReadOnlyList<string> setup,
        string failingSql,
        string stateQuery)
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        foreach (var statement in setup)
            ExecuteBoth(managed, sqlite, statement);

        AssertSameError(managed, sqlite, failingSql);
        AssertQueriesMatch(managed, sqlite, stateQuery);
    }

    private static void AssertErrorMatchesSqlite(IReadOnlyList<string> setup, string failingSql)
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        foreach (var statement in setup)
            ExecuteBoth(managed, sqlite, statement);

        AssertSameError(managed, sqlite, failingSql);
    }

    private static void ExecuteBoth(
        EmbeddedConnection managed,
        MsData.SqliteConnection sqlite,
        params string[] statements)
    {
        foreach (var statement in statements)
        {
            Execute(managed, statement);
            Execute(sqlite, statement);
        }
    }

    private static void AssertSameError(
        EmbeddedConnection managed,
        MsData.SqliteConnection sqlite,
        string sql)
    {
        var managedError = Assert.Throws<EmbeddedSqlException>(() => Execute(managed, sql));
        var sqliteError = Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, sql));
        sqliteError!.Message.Should().Contain(managedError!.Message);
    }

    private static void AssertQueriesMatch(
        EmbeddedConnection managed,
        MsData.SqliteConnection sqlite,
        string query)
    {
        var managedRows = ReadRows(managed, query);
        var sqliteRows = ReadRows(sqlite, query);
        managedRows.Should().HaveCount(
            sqliteRows.Count,
            "managed rows {0} should match SQLite rows {1}",
            FormatRows(managedRows),
            FormatRows(sqliteRows));
        for (var row = 0; row < sqliteRows.Count; row++)
        {
            managedRows[row].Should().HaveCount(sqliteRows[row].Length);
            for (var column = 0; column < sqliteRows[row].Length; column++)
                CellShouldMatch(managedRows[row][column], sqliteRows[row][column]);
        }
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
                managed.Kind.Should().Be(SqlValueKind.Integer);
                managed.AsInteger().Should().Be(integer);
                break;
            case double real:
                managed.Kind.Should().Be(SqlValueKind.Real);
                managed.AsReal().Should().BeApproximately(real, 1e-9);
                break;
            case string text:
                managed.Kind.Should().Be(SqlValueKind.Text);
                managed.AsText().Should().Be(text);
                break;
            case byte[] blob:
                managed.Kind.Should().Be(SqlValueKind.Blob);
                managed.AsBlob().ToArray().Should().Equal(blob);
                break;
            default:
                throw new AssertionException($"Unsupported SQLite value type {sqlite.GetType().Name}.");
        }
    }

    private static string FormatRows<T>(IReadOnlyList<T[]> rows)
        => string.Join(
            "; ",
            rows.Select(row => "[" + string.Join(", ", row.Select(value => value?.ToString() ?? "NULL")) + "]"));
}
