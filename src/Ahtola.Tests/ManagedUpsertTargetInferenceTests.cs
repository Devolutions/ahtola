using System.Globalization;
using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class ManagedUpsertTargetInferenceTests
{
    private static IEnumerable<TestCaseData> SuccessfulInferenceCases
    {
        get
        {
            yield return Case(
                "targetless-do-nothing",
                [
                    "CREATE TABLE t(id INTEGER PRIMARY KEY, code TEXT UNIQUE, value TEXT)",
                    "INSERT INTO t VALUES (1, 'one', 'seed')",
                    """
                    INSERT INTO t VALUES
                        (2, 'one', 'code-conflict'),
                        (1, 'two', 'primary-key-conflict'),
                        (3, 'three', 'inserted')
                    ON CONFLICT DO NOTHING
                    """,
                ],
                "SELECT id, code, value, last_insert_rowid() AS lir FROM t ORDER BY id");
            yield return Case(
                "targetless-do-nothing-reuses-generated-rowid",
                [
                    "CREATE TABLE t(id INTEGER PRIMARY KEY, code TEXT UNIQUE)",
                    "INSERT INTO t(code) VALUES ('one')",
                    "INSERT INTO t(code) VALUES ('one'), ('two') ON CONFLICT DO NOTHING",
                ],
                "SELECT id, code FROM t ORDER BY id");
            yield return Case(
                "targetless-do-nothing-burns-autoincrement-rowid",
                [
                    "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, code TEXT UNIQUE)",
                    "INSERT INTO t(code) VALUES ('one')",
                    "INSERT INTO t(code) VALUES ('one'), ('two') ON CONFLICT DO NOTHING",
                ],
                """
                SELECT id, code, (SELECT seq FROM sqlite_sequence WHERE name = 't') AS seq
                FROM t ORDER BY id
                """);
            yield return Case(
                "sort-order-is-not-an-inference-key",
                [
                    "CREATE TABLE t(code TEXT, value INTEGER)",
                    "CREATE UNIQUE INDEX t_code ON t(code ASC)",
                    "INSERT INTO t VALUES ('key', 1)",
                    """
                    INSERT INTO t VALUES ('key', 2)
                    ON CONFLICT(code DESC) DO UPDATE SET value = excluded.value
                    """,
                ],
                "SELECT code, value FROM t");
            yield return Case(
                "explicit-collation",
                [
                    "CREATE TABLE t(code TEXT, value INTEGER)",
                    "CREATE UNIQUE INDEX t_code ON t(code COLLATE NOCASE DESC)",
                    "INSERT INTO t VALUES ('key', 1)",
                    """
                    INSERT INTO t VALUES ('KEY', 2)
                    ON CONFLICT(code COLLATE nocase ASC) DO UPDATE SET value = excluded.value
                    """,
                ],
                "SELECT code, value FROM t");
            yield return Case(
                "partial-expression-index",
                [
                    "CREATE TABLE t(code TEXT, active INTEGER, region TEXT, value INTEGER)",
                    """
                    CREATE UNIQUE INDEX t_active_code
                    ON t(lower(code) COLLATE NOCASE DESC)
                    WHERE active = 1 AND region = 'east'
                    """,
                    "INSERT INTO t VALUES ('key', 1, 'east', 1)",
                    """
                    INSERT INTO t VALUES ('KEY', 1, 'east', 2)
                    ON CONFLICT(lower(code) COLLATE nocase ASC)
                    WHERE ((active = 01 AND region = 'east'))
                    DO UPDATE SET value = excluded.value
                    """,
                ],
                "SELECT code, active, region, value FROM t");
            yield return Case(
                "reordered-composite-target",
                [
                    "CREATE TABLE t(first_key TEXT, second_key TEXT, value INTEGER)",
                    """
                    CREATE UNIQUE INDEX t_composite
                    ON t(first_key COLLATE NOCASE, second_key)
                    """,
                    "INSERT INTO t VALUES ('first', 'second', 1)",
                    """
                    INSERT INTO t VALUES ('FIRST', 'second', 2)
                    ON CONFLICT(second_key, first_key COLLATE nocase)
                    DO UPDATE SET value = excluded.value
                    """,
                ],
                "SELECT first_key, second_key, value FROM t");
            yield return Case(
                "duplicate-exact-indexes",
                [
                    "CREATE TABLE t(code TEXT, value INTEGER)",
                    "CREATE UNIQUE INDEX t_code_first ON t(code)",
                    "CREATE UNIQUE INDEX t_code_final ON t(code)",
                    "INSERT INTO t VALUES ('key', 1)",
                    """
                    INSERT INTO t VALUES ('key', 2)
                    ON CONFLICT(code) DO UPDATE SET value = excluded.value
                    """,
                ],
                "SELECT code, value FROM t");
            yield return Case(
                "newest-omitted-collation-match",
                [
                    "CREATE TABLE t(code TEXT, value INTEGER)",
                    "CREATE UNIQUE INDEX t_code_binary ON t(code COLLATE BINARY)",
                    "CREATE UNIQUE INDEX t_code_nocase ON t(code COLLATE NOCASE)",
                    "INSERT INTO t VALUES ('key', 1)",
                    """
                    INSERT INTO t VALUES ('KEY', 2)
                    ON CONFLICT(code) DO UPDATE SET value = excluded.value
                    """,
                ],
                "SELECT code, value FROM t");
            yield return Case(
                "qualified-conflict-target",
                [
                    "CREATE TABLE t(code TEXT UNIQUE, value INTEGER)",
                    "INSERT INTO t VALUES ('key', 1)",
                    """
                    INSERT INTO t VALUES ('key', 2)
                    ON CONFLICT(t.code) DO UPDATE SET value = excluded.value
                    """,
                ],
                "SELECT code, value FROM t");
            yield return Case(
                "three-part-qualified-conflict-target",
                [
                    "CREATE TABLE t(code TEXT UNIQUE, value INTEGER)",
                    "INSERT INTO t VALUES ('key', 1)",
                    """
                    INSERT INTO t VALUES ('key', 2)
                    ON CONFLICT(main.t.code) DO UPDATE SET value = excluded.value
                    """,
                ],
                "SELECT code, value FROM t");
        }
    }

    private static IEnumerable<TestCaseData> RejectedInferenceCases
    {
        get
        {
            yield return RejectionCase(
                "explicit-collation-mismatch",
                [
                    "CREATE TABLE t(code TEXT, value INTEGER)",
                    "CREATE UNIQUE INDEX t_code ON t(code COLLATE NOCASE)",
                    "INSERT INTO t VALUES ('key', 1)",
                ],
                """
                INSERT INTO t VALUES ('KEY', 2)
                ON CONFLICT(code COLLATE BINARY) DO UPDATE SET value = excluded.value
                """,
                "ON CONFLICT clause does not match",
                "SELECT code, value FROM t");
            yield return RejectionCase(
                "partial-predicate-is-not-reordered",
                [
                    "CREATE TABLE t(code TEXT, active INTEGER, value INTEGER)",
                    "CREATE UNIQUE INDEX t_code ON t(code) WHERE active = 1",
                    "INSERT INTO t VALUES ('key', 1, 1)",
                ],
                """
                INSERT INTO t VALUES ('key', 1, 2)
                ON CONFLICT(code) WHERE 1 = active DO UPDATE SET value = excluded.value
                """,
                "ON CONFLICT clause does not match",
                "SELECT code, active, value FROM t");
            yield return RejectionCase(
                "expression-is-not-commuted",
                [
                    "CREATE TABLE t(left_value INTEGER, right_value INTEGER, value INTEGER)",
                    "CREATE UNIQUE INDEX t_sum ON t(left_value + right_value)",
                    "INSERT INTO t VALUES (1, 2, 1)",
                ],
                """
                INSERT INTO t VALUES (2, 1, 2)
                ON CONFLICT(right_value + left_value) DO UPDATE SET value = excluded.value
                """,
                "ON CONFLICT clause does not match",
                "SELECT left_value, right_value, value FROM t");
            yield return RejectionCase(
                "newest-omitted-collation-controls-conflict",
                [
                    "CREATE TABLE t(code TEXT, value INTEGER)",
                    "CREATE UNIQUE INDEX t_code_nocase ON t(code COLLATE NOCASE)",
                    "CREATE UNIQUE INDEX t_code_binary ON t(code COLLATE BINARY)",
                    "INSERT INTO t VALUES ('key', 1)",
                ],
                """
                INSERT INTO t VALUES ('KEY', 2)
                ON CONFLICT(code) DO UPDATE SET value = excluded.value
                """,
                "UNIQUE constraint failed",
                "SELECT code, value FROM t");
        }
    }

    [TestCaseSource(nameof(SuccessfulInferenceCases))]
    public void SuccessfulInferenceMatchesSQLite(DifferentialCase testCase)
    {
        AssertOutputsEqual(
            RunManaged(testCase.Statements, testCase.Query),
            RunSqlite(testCase.Statements, testCase.Query));
    }

    [TestCaseSource(nameof(RejectedInferenceCases))]
    public void RejectedInferenceMatchesSQLiteAndLeavesTheTableUnchanged(RejectionDifferentialCase testCase)
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in testCase.Setup)
        {
            Execute(managed, statement);
            Execute(sqlite, statement);
        }

        Assert.Throws<EmbeddedSqlException>(() => Execute(managed, testCase.Statement))!
            .Message.Should().Contain(testCase.ErrorFragment);
        Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, testCase.Statement))!
            .Message.Should().Contain(testCase.ErrorFragment);

        AssertOutputsEqual(
            QueryManaged(managed, testCase.Query),
            QuerySqlite(sqlite, testCase.Query));
    }

    [Test]
    public void TargetlessDoNothingReturningIncludesOnlyInsertedRowsInSourceOrder()
    {
        const string insert = """
            INSERT INTO t VALUES (1, 'primary'), (2, 'one'), (3, 'three'), (4, 'four')
            ON CONFLICT DO NOTHING
            RETURNING id, code
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        Execute(managed, "CREATE TABLE t(id INTEGER PRIMARY KEY, code TEXT UNIQUE);");
        Execute(managed, "INSERT INTO t VALUES (1, 'one');");
        var managedRows = QueryManaged(managed, insert);

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        Execute(sqlite, "CREATE TABLE t(id INTEGER PRIMARY KEY, code TEXT UNIQUE);");
        Execute(sqlite, "INSERT INTO t VALUES (1, 'one');");
        var sqliteRows = QuerySqlite(sqlite, insert);

        AssertOutputsEqual(managedRows, sqliteRows);
        managedRows.Rows.Should().Equal("I:3\u001fT:three", "I:4\u001fT:four");
    }

    [Test]
    public void UnreconstructableIndexExpressionsAreRejectedBeforeCatalogMutation()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(left_value INTEGER, right_value INTEGER);");
        connection.RegisterScalarFunction("custom_key", 1, values => values[0]);

        Assert.Throws<EmbeddedSqlException>(
                () => Execute(connection, "CREATE UNIQUE INDEX random_key ON t(random());"))!
            .Message.Should().Contain("non-deterministic functions are prohibited");
        Assert.Throws<EmbeddedSqlException>(
                () => Execute(connection, "CREATE UNIQUE INDEX custom_key_index ON t(custom_key(left_value));"))!
            .Message.Should().Contain("prohibited");
        Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "CREATE UNIQUE INDEX row_key ON t((left_value, right_value));"));

        QueryManaged(connection, "SELECT name FROM sqlite_schema WHERE type = 'index';")
            .Rows.Should().BeEmpty();
    }

    [TestCase("IGNORE")]
    [TestCase("REPLACE")]
    public void SuccessfulInsertOrAlgorithmsAroundUpsertMatchSQLite(string algorithm)
    {
        const string setup = "CREATE TABLE t(id INTEGER PRIMARY KEY, code TEXT UNIQUE);";
        const string seed = "INSERT INTO t VALUES (1, 'one');";
        var insert = $"""
            INSERT OR {algorithm} INTO t VALUES (2, 'two'), (3, 'one'), (4, 'four')
            ON CONFLICT(id) DO NOTHING
            RETURNING id, code
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        Execute(managed, setup);
        Execute(managed, seed);
        var managedReturning = QueryManaged(managed, insert);
        var managedState = QueryManaged(
            managed,
            "SELECT id, code, last_insert_rowid() AS lir FROM t ORDER BY id");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        Execute(sqlite, setup);
        Execute(sqlite, seed);
        var sqliteReturning = QuerySqlite(sqlite, insert);
        var sqliteState = QuerySqlite(
            sqlite,
            "SELECT id, code, last_insert_rowid() AS lir FROM t ORDER BY id");

        AssertOutputsEqual(managedReturning, sqliteReturning);
        AssertOutputsEqual(managedState, sqliteState);
    }

    [TestCase("ABORT")]
    [TestCase("FAIL")]
    [TestCase("ROLLBACK")]
    public void FailedInsertOrAlgorithmsAroundUpsertMatchSQLite(string algorithm)
    {
        const string setup = "CREATE TABLE t(id INTEGER PRIMARY KEY, code TEXT UNIQUE);";
        var insert = $"""
            INSERT OR {algorithm} INTO t VALUES (2, 'two'), (3, 'one'), (4, 'four')
            ON CONFLICT(id) DO NOTHING
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        Execute(managed, setup);
        Execute(managed, "INSERT INTO t VALUES (1, 'one');");
        Execute(managed, "BEGIN;");
        Execute(managed, "INSERT INTO t VALUES (9, 'pending');");
        Assert.Throws<EmbeddedSqlException>(() => Execute(managed, insert))!
            .Message.Should().Contain("UNIQUE constraint failed");
        var managedState = QueryManaged(
            managed,
            "SELECT id, code, last_insert_rowid() AS lir FROM t ORDER BY id");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        Execute(sqlite, setup);
        Execute(sqlite, "INSERT INTO t VALUES (1, 'one');");
        Execute(sqlite, "BEGIN;");
        Execute(sqlite, "INSERT INTO t VALUES (9, 'pending');");
        Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, insert))!
            .Message.Should().Contain("UNIQUE constraint failed");
        var sqliteState = QuerySqlite(
            sqlite,
            "SELECT id, code, last_insert_rowid() AS lir FROM t ORDER BY id");

        AssertOutputsEqual(managedState, sqliteState);
        if (algorithm == "ROLLBACK")
        {
            Assert.Throws<EmbeddedSqlException>(() => Execute(managed, "COMMIT;"));
            Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, "COMMIT;"));
        }
        else
        {
            Execute(managed, "COMMIT;");
            Execute(sqlite, "COMMIT;");
        }
    }

    [Test]
    public void InsertOrIgnoreAppliesNonUniqueConstraintsBeforeTheUpsertAction()
    {
        const string setup = "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT NOT NULL);";
        const string seed = "INSERT INTO t VALUES (1, 'seed');";
        const string insert = """
            INSERT OR IGNORE INTO t VALUES (1, NULL)
            ON CONFLICT(id) DO UPDATE SET value = excluded.value
            RETURNING id, value
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        Execute(managed, setup);
        Execute(managed, seed);
        var managedReturning = QueryManaged(managed, insert);
        var managedState = QueryManaged(managed, "SELECT id, value FROM t");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        Execute(sqlite, setup);
        Execute(sqlite, seed);
        var sqliteReturning = QuerySqlite(sqlite, insert);
        var sqliteState = QuerySqlite(sqlite, "SELECT id, value FROM t");

        AssertOutputsEqual(managedReturning, sqliteReturning);
        AssertOutputsEqual(managedState, sqliteState);
    }

    [Test]
    public void ConstraintOwnedIgnoreAppliesToConflictsOutsideTheUpsertTarget()
    {
        const string setup =
            "CREATE TABLE t(id INTEGER PRIMARY KEY, code TEXT UNIQUE ON CONFLICT IGNORE, value INTEGER);";
        const string insert = """
            INSERT INTO t VALUES (2, 'one', 2)
            ON CONFLICT(id) DO UPDATE SET value = excluded.value
            RETURNING id, code, value
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        Execute(managed, setup);
        Execute(managed, "INSERT INTO t VALUES (1, 'one', 1);");
        var managedReturning = QueryManaged(managed, insert);
        var managedState = QueryManaged(managed, "SELECT id, code, value FROM t");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        Execute(sqlite, setup);
        Execute(sqlite, "INSERT INTO t VALUES (1, 'one', 1);");
        var sqliteReturning = QuerySqlite(sqlite, insert);
        var sqliteState = QuerySqlite(sqlite, "SELECT id, code, value FROM t");

        AssertOutputsEqual(managedReturning, sqliteReturning);
        AssertOutputsEqual(managedState, sqliteState);
    }

    [TestCase("IGNORE")]
    [TestCase("FAIL")]
    [TestCase("REPLACE")]
    [TestCase("ROLLBACK")]
    public void DoUpdateSecondaryConflictsAlwaysAbortRegardlessOfInsertOrAlgorithm(string algorithm)
    {
        const string setup = "CREATE TABLE t(id INTEGER PRIMARY KEY, code TEXT UNIQUE);";
        var insert = $"""
            INSERT OR {algorithm} INTO t VALUES (1, 'two')
            ON CONFLICT(id) DO UPDATE SET code = excluded.code
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        Execute(managed, setup);
        Execute(managed, "INSERT INTO t VALUES (1, 'one'), (2, 'two');");
        Assert.Throws<EmbeddedSqlException>(() => Execute(managed, insert))!
            .Message.Should().Contain("UNIQUE constraint failed");
        var managedState = QueryManaged(managed, "SELECT id, code FROM t ORDER BY id");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        Execute(sqlite, setup);
        Execute(sqlite, "INSERT INTO t VALUES (1, 'one'), (2, 'two');");
        Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, insert))!
            .Message.Should().Contain("UNIQUE constraint failed");
        var sqliteState = QuerySqlite(sqlite, "SELECT id, code FROM t ORDER BY id");

        AssertOutputsEqual(managedState, sqliteState);
    }

    [Test]
    public void GeneratedStrictWithoutRowidForeignKeyContextMatchesSQLite()
    {
        string[] setup =
        [
            "PRAGMA foreign_keys = ON",
            """
            CREATE TABLE parent(
                tenant TEXT COLLATE NOCASE,
                code INT,
                label TEXT,
                normalized TEXT GENERATED ALWAYS AS (lower(label)) VIRTUAL,
                PRIMARY KEY(tenant DESC, code)
            ) WITHOUT ROWID, STRICT
            """,
            """
            CREATE UNIQUE INDEX parent_active_label
            ON parent(lower(label) COLLATE NOCASE DESC)
            WHERE code > 0
            """,
            """
            CREATE TABLE child(
                tenant TEXT COLLATE NOCASE,
                code INT,
                FOREIGN KEY(tenant, code) REFERENCES parent(tenant, code) ON UPDATE CASCADE
            ) STRICT
            """,
            "INSERT INTO parent(tenant, code, label) VALUES ('alpha', 1, 'One')",
            "INSERT INTO child VALUES ('alpha', 1)",
        ];
        const string upsert = """
            INSERT INTO parent(tenant, code, label) VALUES ('beta', 2, 'ONE')
            ON CONFLICT(lower(label) COLLATE nocase ASC)
            WHERE code > 0
            DO UPDATE SET
                tenant = excluded.tenant,
                code = excluded.code,
                label = excluded.label
            RETURNING tenant, code, label, normalized
            """;
        const string state = """
            SELECT parent.tenant, parent.code, parent.label, parent.normalized, child.tenant, child.code
            FROM parent JOIN child ON child.tenant = parent.tenant AND child.code = parent.code
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        foreach (var statement in setup)
            Execute(managed, statement);
        var managedReturning = QueryManaged(managed, upsert);
        var managedState = QueryManaged(managed, state);

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in setup)
            Execute(sqlite, statement);
        var sqliteReturning = QuerySqlite(sqlite, upsert);
        var sqliteState = QuerySqlite(sqlite, state);

        AssertOutputsEqual(managedReturning, sqliteReturning);
        AssertOutputsEqual(managedState, sqliteState);
    }

    [Test]
    public void TempAndAttachedCatalogsResolveRichUpsertTargets()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("upsert-main.db", fileSystem);
        using var connection = database.Connect();

        Execute(connection, "CREATE TEMP TABLE temp_items(code TEXT, active INT, value INT) STRICT;");
        Execute(
            connection,
            """
            CREATE UNIQUE INDEX temp.temp_items_code
            ON temp_items(lower(code) COLLATE NOCASE DESC)
            WHERE active = 1;
            """);
        Execute(connection, "INSERT INTO temp_items VALUES ('temp', 1, 1);");
        Execute(
            connection,
            """
            INSERT INTO temp_items VALUES ('TEMP', 1, 2)
            ON CONFLICT(lower(code) COLLATE nocase ASC) WHERE active = 1
            DO UPDATE SET value = excluded.value;
            """);

        Execute(connection, "ATTACH DATABASE 'upsert-aux.db' AS aux;");
        Execute(connection, "CREATE TABLE aux.items(code TEXT, active INT, value INT) STRICT;");
        Execute(
            connection,
            """
            CREATE UNIQUE INDEX aux.items_code
            ON items(lower(code) COLLATE NOCASE DESC)
            WHERE active = 1;
            """);
        Execute(connection, "INSERT INTO aux.items VALUES ('aux', 1, 3);");
        Execute(
            connection,
            """
            INSERT INTO aux.items VALUES ('AUX', 1, 4)
            ON CONFLICT(lower(code) COLLATE nocase ASC) WHERE active = 1
            DO UPDATE SET value = excluded.value;
            """);

        QueryManaged(connection, "SELECT value FROM temp_items;")
            .Rows.Should().Equal("I:2");
        QueryManaged(connection, "SELECT value FROM aux.items;")
            .Rows.Should().Equal("I:4");
    }

    [Test]
    public void TriggerCallbacksKeepSourceOrderAndFailuresRollBackTheWholeUpsert()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value INTEGER);");
        Execute(connection, "CREATE TABLE audit(event TEXT UNIQUE);");
        Execute(
            connection,
            "CREATE TRIGGER t_insert AFTER INSERT ON t BEGIN INSERT INTO audit VALUES ('insert'); END;");
        Execute(
            connection,
            "CREATE TRIGGER t_update AFTER UPDATE ON t BEGIN INSERT INTO audit VALUES ('update'); END;");
        Execute(connection, "INSERT INTO t VALUES (1, 1);");
        Execute(connection, "DELETE FROM audit;");

        Execute(
            connection,
            """
            INSERT INTO t VALUES (2, 2), (1, 10)
            ON CONFLICT(id) DO UPDATE SET value = excluded.value;
            """);
        QueryManaged(connection, "SELECT event FROM audit ORDER BY rowid;")
            .Rows.Should().Equal("T:insert", "T:update");

        Execute(connection, "DELETE FROM audit;");
        Execute(connection, "INSERT INTO audit VALUES ('update');");
        Assert.Throws<EmbeddedSqlException>(
                () => Execute(
                    connection,
                    """
                    INSERT INTO t VALUES (3, 3), (1, 11)
                    ON CONFLICT(id) DO UPDATE SET value = excluded.value;
                    """))!
            .Message.Should().Contain("UNIQUE constraint failed");

        QueryManaged(connection, "SELECT id, value FROM t ORDER BY id;")
            .Rows.Should().Equal("I:1\u001fI:10", "I:2\u001fI:2");
        QueryManaged(connection, "SELECT event FROM audit;")
            .Rows.Should().Equal("T:update");
    }

    [Test]
    public void ReturningAndLimitedDmlRemainOrderedAfterInferredUpserts()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, code TEXT UNIQUE, value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 'one', 1), (2, 'two', 2), (3, 'three', 3);");
        QueryManaged(
                connection,
                """
                INSERT INTO t VALUES (2, 'two', 20), (4, 'four', 4)
                ON CONFLICT(code) DO UPDATE SET value = excluded.value
                RETURNING id, value;
                """)
            .Rows.Should().Equal("I:2\u001fI:20", "I:4\u001fI:4");
        QueryManaged(
                connection,
                "UPDATE t SET value = value + 100 RETURNING id, value ORDER BY id DESC LIMIT 2;")
            .Rows.Should().Equal("I:3\u001fI:103", "I:4\u001fI:104");
        QueryManaged(connection, "DELETE FROM t RETURNING id ORDER BY id LIMIT 1;")
            .Rows.Should().Equal("I:1");
    }

    [Test]
    public void ExplainAndQueryPlanTruthfullyReportTheUpsertEvaluatorRoute()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 1);");

        QueryManaged(
                connection,
                "EXPLAIN QUERY PLAN INSERT INTO t VALUES (1, 2) ON CONFLICT DO NOTHING;")
            .Rows.Should().Equal("I:0\u001fI:0\u001fI:0\u001fT:MANAGED EVALUATOR FALLBACK");
        Assert.Throws<EmbeddedSqlException>(
                () => QueryManaged(
                    connection,
                    "EXPLAIN INSERT INTO t VALUES (1, 2) ON CONFLICT DO NOTHING;"))!
            .Message.Should().Contain("only supported for statements lowered to the bytecode compiler");
        QueryManaged(connection, "SELECT id, value FROM t;")
            .Rows.Should().Equal("I:1\u001fI:1");
    }

    [Test]
    public void DoUpdateMovesTheRowidAndLeavesNoStaleRow()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, a TEXT, u INTEGER UNIQUE);");
        Execute(connection, "INSERT INTO t VALUES (1,'old',10);");
        Execute(connection, "INSERT INTO t VALUES (2,'other',20);");

        Execute(
            connection,
            "INSERT INTO t VALUES (3,'new',10) ON CONFLICT(u) DO UPDATE SET id = excluded.id, a = excluded.a;");

        QueryManaged(connection, "SELECT id, a, u FROM t ORDER BY id;")
            .Rows.Should().Equal("I:2\u001fT:other\u001fI:20", "I:3\u001fT:new\u001fI:10");
        QueryManaged(connection, "SELECT rowid FROM t WHERE rowid = 1;").Rows.Should().BeEmpty();
    }

    [Test]
    public void DoUpdateAssigningRowidIsSkippedWhenItsWhereClauseIsFalse()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, a INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1,10);");
        Execute(connection, "INSERT INTO t VALUES (2,20);");

        Execute(connection, "INSERT INTO t(id,a) VALUES(1,99) ON CONFLICT(id) DO UPDATE SET id=2 WHERE a < '2';");

        QueryManaged(connection, "SELECT id, a FROM t ORDER BY id;")
            .Rows.Should().Equal("I:1\u001fI:10", "I:2\u001fI:20");
    }

    [Test]
    public void DoUpdateMovingTheRowidOntoAnExistingRowStillConflicts()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, u INTEGER UNIQUE);");
        Execute(connection, "INSERT INTO t VALUES (1,10);");
        Execute(connection, "INSERT INTO t VALUES (2,20);");

        Assert.Throws<EmbeddedSqlException>(
                () => Execute(connection, "INSERT INTO t VALUES (3,10) ON CONFLICT(u) DO UPDATE SET id = 2;"))!
            .Message.Should().Contain("UNIQUE constraint failed: t.id");
        QueryManaged(connection, "SELECT id, u FROM t ORDER BY id;")
            .Rows.Should().Equal("I:1\u001fI:10", "I:2\u001fI:20");
    }

    [Test]
    public void TargetlessDoUpdateResolvesAnyUniquenessConflict()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, code TEXT UNIQUE, value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 'one', 1);");

        Execute(connection, "INSERT INTO t VALUES (1, 'one', 2) ON CONFLICT DO UPDATE SET value = excluded.value;");
        QueryManaged(connection, "SELECT id, code, value FROM t;")
            .Rows.Should().Equal("I:1\u001fT:one\u001fI:2");

        Execute(connection, "INSERT INTO t VALUES (9, 'one', 3) ON CONFLICT DO UPDATE SET value = excluded.value;");
        QueryManaged(connection, "SELECT id, code, value FROM t;")
            .Rows.Should().Equal("I:1\u001fT:one\u001fI:3");
    }

    [Test]
    public void ReplaceConflictsFireDeleteAndInsertTriggersForEachSourceRow()
    {
        string[] setup =
        [
            "CREATE TABLE t(id INTEGER PRIMARY KEY, code TEXT UNIQUE)",
            "CREATE TABLE audit(event TEXT)",
            "INSERT INTO t VALUES (1, 'one'), (2, 'two')",
            "CREATE TRIGGER t_delete AFTER DELETE ON t BEGIN INSERT INTO audit VALUES ('delete'); END",
            "CREATE TRIGGER t_insert AFTER INSERT ON t BEGIN INSERT INTO audit VALUES ('insert'); END",
            "PRAGMA recursive_triggers = ON",
        ];
        const string replace = """
            INSERT OR REPLACE INTO t VALUES (3, 'one'), (4, 'two')
            ON CONFLICT(id) DO NOTHING
            RETURNING id, code
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        foreach (var statement in setup)
            Execute(managed, statement);
        var managedReturning = QueryManaged(managed, replace);
        var managedAudit = QueryManaged(managed, "SELECT event FROM audit ORDER BY rowid");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in setup)
            Execute(sqlite, statement);
        var sqliteReturning = QuerySqlite(sqlite, replace);
        var sqliteAudit = QuerySqlite(sqlite, "SELECT event FROM audit ORDER BY rowid");

        AssertOutputsEqual(managedReturning, sqliteReturning);
        AssertOutputsEqual(managedAudit, sqliteAudit);
        managedAudit.Rows.Should().Equal("T:delete", "T:insert", "T:delete", "T:insert");
    }

    [Test]
    public void SecondaryUpdateAbortPreservesTheLastAttemptedInsertRowid()
    {
        const string setup = "CREATE TABLE t(id INTEGER PRIMARY KEY, code TEXT UNIQUE);";
        const string upsert = """
            INSERT INTO t VALUES (3, 'three'), (1, 'two')
            ON CONFLICT(id) DO UPDATE SET code = excluded.code
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        Execute(managed, setup);
        Execute(managed, "INSERT INTO t VALUES (1, 'one'), (2, 'two');");
        Assert.Throws<EmbeddedSqlException>(() => Execute(managed, upsert))!
            .Message.Should().Contain("UNIQUE constraint failed");
        var managedState = QueryManaged(
            managed,
            "SELECT id, code, last_insert_rowid() AS lir FROM t ORDER BY id");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        Execute(sqlite, setup);
        Execute(sqlite, "INSERT INTO t VALUES (1, 'one'), (2, 'two');");
        Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, upsert))!
            .Message.Should().Contain("UNIQUE constraint failed");
        var sqliteState = QuerySqlite(
            sqlite,
            "SELECT id, code, last_insert_rowid() AS lir FROM t ORDER BY id");

        AssertOutputsEqual(managedState, sqliteState);
    }

    [Test]
    public void TriggerFailRollsBackTheOuterUpsert()
    {
        string[] setup =
        [
            "CREATE TABLE t(id INTEGER PRIMARY KEY, value INTEGER)",
            "CREATE TABLE audit(value INTEGER UNIQUE ON CONFLICT FAIL)",
            "INSERT INTO t VALUES (1, 1)",
            "INSERT INTO audit VALUES (1)",
            """
            CREATE TRIGGER t_update AFTER UPDATE ON t BEGIN
                INSERT INTO audit VALUES (2), (1);
            END
            """,
        ];
        const string upsert = """
            INSERT INTO t VALUES (1, 2)
            ON CONFLICT(id) DO UPDATE SET value = excluded.value
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        foreach (var statement in setup)
            Execute(managed, statement);
        Assert.Throws<EmbeddedSqlException>(() => Execute(managed, upsert))!
            .Message.Should().Contain("UNIQUE constraint failed");
        var managedState = QueryManaged(
            managed,
            "SELECT 't' AS source, id, value FROM t "
                + "UNION ALL SELECT 'audit', value, NULL FROM audit ORDER BY 1");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in setup)
            Execute(sqlite, statement);
        Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, upsert))!
            .Message.Should().Contain("UNIQUE constraint failed");
        var sqliteState = QuerySqlite(
            sqlite,
            "SELECT 't' AS source, id, value FROM t "
                + "UNION ALL SELECT 'audit', value, NULL FROM audit ORDER BY 1");

        AssertOutputsEqual(managedState, sqliteState);
    }

    [Test]
    public void ConstraintOwnedReplaceDoesNotOverrideAnIndependentAbortConflict()
    {
        const string setup = """
            CREATE TABLE t(
                id INTEGER PRIMARY KEY,
                replace_key TEXT UNIQUE ON CONFLICT REPLACE,
                abort_key TEXT UNIQUE
            )
            """;
        const string insert = """
            INSERT INTO t VALUES (3, 'replace-one', 'abort-two')
            ON CONFLICT(id) DO NOTHING
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        Execute(managed, setup);
        Execute(
            managed,
            "INSERT INTO t VALUES (1, 'replace-one', 'abort-one'), (2, 'replace-two', 'abort-two');");
        Assert.Throws<EmbeddedSqlException>(() => Execute(managed, insert))!
            .Message.Should().Contain("UNIQUE constraint failed: t.abort_key");
        var managedState = QueryManaged(managed, "SELECT * FROM t ORDER BY id");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        Execute(sqlite, setup);
        Execute(
            sqlite,
            "INSERT INTO t VALUES (1, 'replace-one', 'abort-one'), (2, 'replace-two', 'abort-two');");
        Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, insert))!
            .Message.Should().Contain("UNIQUE constraint failed: t.abort_key");
        var sqliteState = QuerySqlite(sqlite, "SELECT * FROM t ORDER BY id");

        AssertOutputsEqual(managedState, sqliteState);
    }

    [Test]
    public void WithoutRowidPrimaryKeyParticipatesInDeclarationOrderInference()
    {
        const string setup = """
            CREATE TABLE t(
                code TEXT,
                value INTEGER,
                UNIQUE(code COLLATE NOCASE),
                PRIMARY KEY(code COLLATE BINARY)
            ) WITHOUT ROWID
            """;
        const string upsert = """
            INSERT INTO t VALUES ('KEY', 2)
            ON CONFLICT(code) DO UPDATE SET value = excluded.value
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        Execute(managed, setup);
        Execute(managed, "INSERT INTO t VALUES ('key', 1);");
        Assert.Throws<EmbeddedSqlException>(() => Execute(managed, upsert))!
            .Message.Should().Contain("UNIQUE constraint failed");
        var managedState = QueryManaged(managed, "SELECT code, value FROM t");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        Execute(sqlite, setup);
        Execute(sqlite, "INSERT INTO t VALUES ('key', 1);");
        Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, upsert))!
            .Message.Should().Contain("UNIQUE constraint failed");
        var sqliteState = QuerySqlite(sqlite, "SELECT code, value FROM t");

        AssertOutputsEqual(managedState, sqliteState);
    }

    [Test]
    public void ThrowingUpdateCallbackPreservesTheLastAttemptedInsertRowid()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        connection.RegisterScalarFunction(
            "explode",
            1,
            _ => throw new InvalidOperationException("callback failed"));
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, code TEXT UNIQUE);");
        Execute(connection, "INSERT INTO t VALUES (1, 'one'), (2, 'two');");

        Assert.Throws<InvalidOperationException>(
                () => Execute(
                    connection,
                    """
                    INSERT INTO t VALUES (3, 'three'), (1, 'updated')
                    ON CONFLICT(id) DO UPDATE SET code = explode(excluded.code);
                    """))!
            .Message.Should().Be("callback failed");
        QueryManaged(
                connection,
                "SELECT id, code, last_insert_rowid() AS lir FROM t ORDER BY id")
            .Rows.Should().Equal("I:1\u001fT:one\u001fI:3", "I:2\u001fT:two\u001fI:3");
    }

    [Test]
    public void NewestConstraintPolicyControlsMixedConflictAtomicity()
    {
        const string setup = """
            CREATE TABLE t(
                id INTEGER PRIMARY KEY,
                abort_key TEXT UNIQUE ON CONFLICT ABORT,
                fail_key TEXT UNIQUE ON CONFLICT FAIL
            )
            """;
        const string insert = """
            INSERT INTO t VALUES
                (3, 'abort-three', 'fail-three'),
                (4, 'abort-one', 'fail-two')
            ON CONFLICT(id) DO NOTHING
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        Execute(managed, setup);
        Execute(
            managed,
            "INSERT INTO t VALUES (1, 'abort-one', 'fail-one'), (2, 'abort-two', 'fail-two');");
        Assert.Throws<EmbeddedSqlException>(() => Execute(managed, insert))!
            .Message.Should().Contain("UNIQUE constraint failed: t.fail_key");
        var managedState = QueryManaged(managed, "SELECT * FROM t ORDER BY id");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        Execute(sqlite, setup);
        Execute(
            sqlite,
            "INSERT INTO t VALUES (1, 'abort-one', 'fail-one'), (2, 'abort-two', 'fail-two');");
        Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, insert))!
            .Message.Should().Contain("UNIQUE constraint failed: t.fail_key");
        var sqliteState = QuerySqlite(sqlite, "SELECT * FROM t ORDER BY id");

        AssertOutputsEqual(managedState, sqliteState);
    }

    [Test]
    public void CascadedTriggerFailRollsBackTheOuterUpsert()
    {
        string[] setup =
        [
            "PRAGMA foreign_keys = ON",
            "CREATE TABLE parent(id TEXT PRIMARY KEY, token TEXT UNIQUE)",
            """
            CREATE TABLE child(
                parent_id TEXT REFERENCES parent(id) ON UPDATE CASCADE
            )
            """,
            "CREATE TABLE audit(value INTEGER UNIQUE ON CONFLICT FAIL)",
            "INSERT INTO parent VALUES ('old', 'token')",
            "INSERT INTO child VALUES ('old')",
            "INSERT INTO audit VALUES (1)",
            """
            CREATE TRIGGER child_update AFTER UPDATE ON child BEGIN
                INSERT INTO audit VALUES (2), (1);
            END
            """,
        ];
        const string upsert = """
            INSERT INTO parent VALUES ('new', 'token')
            ON CONFLICT(token) DO UPDATE SET id = excluded.id
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        foreach (var statement in setup)
            Execute(managed, statement);
        Assert.Throws<EmbeddedSqlException>(() => Execute(managed, upsert))!
            .Message.Should().Contain("UNIQUE constraint failed");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in setup)
            Execute(sqlite, statement);
        Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, upsert))!
            .Message.Should().Contain("UNIQUE constraint failed");

        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM parent"),
            QuerySqlite(sqlite, "SELECT * FROM parent"));
        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM child"),
            QuerySqlite(sqlite, "SELECT * FROM child"));
        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM audit"),
            QuerySqlite(sqlite, "SELECT * FROM audit"));
    }

    [Test]
    public void OuterInsertOrIgnorePolicyAppliesInsideUpsertTriggers()
    {
        string[] setup =
        [
            "CREATE TABLE t(id INTEGER PRIMARY KEY)",
            "CREATE TABLE audit(value INTEGER UNIQUE)",
            "INSERT INTO audit VALUES (1)",
            """
            CREATE TRIGGER t_insert AFTER INSERT ON t BEGIN
                INSERT INTO audit VALUES (1);
            END
            """,
        ];
        const string insert = """
            INSERT OR IGNORE INTO t VALUES (1)
            ON CONFLICT(id) DO NOTHING
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        foreach (var statement in setup)
            Execute(managed, statement);
        Execute(managed, insert);

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in setup)
            Execute(sqlite, statement);
        Execute(sqlite, insert);

        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM t"),
            QuerySqlite(sqlite, "SELECT * FROM t"));
        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM audit"),
            QuerySqlite(sqlite, "SELECT * FROM audit"));
    }

    [Test]
    public void ReplaceDeletesVictimsInConstraintCheckOrder()
    {
        string[] setup =
        [
            """
            CREATE TABLE t(
                id INTEGER PRIMARY KEY,
                first_key TEXT UNIQUE ON CONFLICT REPLACE,
                final_key TEXT UNIQUE ON CONFLICT REPLACE
            )
            """,
            "CREATE TABLE audit(remaining_id INTEGER)",
            "INSERT INTO t VALUES (1, 'first-one', 'final-one'), (2, 'first-two', 'final-two')",
            """
            CREATE TRIGGER t_delete AFTER DELETE ON t BEGIN
                INSERT INTO audit SELECT id FROM t ORDER BY id LIMIT 1;
            END
            """,
            "PRAGMA recursive_triggers = ON",
        ];
        const string replace = """
            INSERT INTO t VALUES (3, 'first-one', 'final-two')
            ON CONFLICT(id) DO NOTHING
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        foreach (var statement in setup)
            Execute(managed, statement);
        Execute(managed, replace);

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in setup)
            Execute(sqlite, statement);
        Execute(sqlite, replace);

        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM t"),
            QuerySqlite(sqlite, "SELECT * FROM t"));
        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM audit"),
            QuerySqlite(sqlite, "SELECT * FROM audit"));
    }

    [Test]
    public void ThrowingCandidateAndReturningCallbacksPreserveLastInsertRowid()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        connection.RegisterScalarFunction(
            "explode",
            1,
            _ => throw new InvalidOperationException("callback failed"));
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, code TEXT UNIQUE);");
        Execute(connection, "INSERT INTO t VALUES (1, 'one'), (2, 'two');");

        Assert.Throws<InvalidOperationException>(
                () => Execute(
                    connection,
                    """
                    INSERT INTO t VALUES (3, 'three'), (explode(4), 'four')
                    ON CONFLICT DO NOTHING;
                    """))!
            .Message.Should().Be("callback failed");
        QueryManaged(
                connection,
                "SELECT id, code, last_insert_rowid() AS lir FROM t ORDER BY id")
            .Rows.Should().Equal("I:1\u001fT:one\u001fI:3", "I:2\u001fT:two\u001fI:3");

        Assert.Throws<InvalidOperationException>(
                () => Execute(
                    connection,
                    """
                    INSERT INTO t VALUES (4, 'four'), (5, 'five')
                    ON CONFLICT DO NOTHING
                    RETURNING explode(id);
                    """))!
            .Message.Should().Be("callback failed");
        QueryManaged(
                connection,
                "SELECT id, code, last_insert_rowid() AS lir FROM t ORDER BY id")
            .Rows.Should().Equal("I:1\u001fT:one\u001fI:4", "I:2\u001fT:two\u001fI:4");
    }

    [Test]
    public void FailingReplaceDeleteTriggerDoesNotRecordTheCandidateRowid()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        connection.RegisterScalarFunction(
            "explode",
            1,
            _ => throw new InvalidOperationException("delete callback failed"));
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, code TEXT UNIQUE);");
        Execute(connection, "CREATE TABLE audit(value INTEGER PRIMARY KEY) WITHOUT ROWID;");
        Execute(connection, "INSERT INTO t VALUES (2, 'two');");
        Execute(connection, "INSERT INTO audit VALUES (10);");
        Execute(
            connection,
            """
            CREATE TRIGGER t_delete AFTER DELETE ON t BEGIN
                UPDATE audit SET value = explode(value);
            END;
            """);
        Execute(connection, "PRAGMA recursive_triggers = ON;");

        Assert.Throws<InvalidOperationException>(
                () => Execute(
                    connection,
                    """
                    INSERT OR REPLACE INTO t VALUES (3, 'two')
                    ON CONFLICT(id) DO NOTHING;
                    """))!
            .Message.Should().Be("delete callback failed");
        QueryManaged(
                connection,
                "SELECT id, code, last_insert_rowid() AS lir FROM t")
            .Rows.Should().Equal("I:2\u001fT:two\u001fI:2");
    }

    [Test]
    public void DefaultUpsertPreservesExplicitTriggerRollbackPolicy()
    {
        string[] setup =
        [
            "CREATE TABLE t(id INTEGER PRIMARY KEY)",
            "CREATE TABLE audit(value INTEGER UNIQUE)",
            "CREATE TABLE marker(value INTEGER)",
            "INSERT INTO audit VALUES (1)",
            """
            CREATE TRIGGER t_insert AFTER INSERT ON t BEGIN
                INSERT OR ROLLBACK INTO audit VALUES (1);
            END
            """,
            "BEGIN",
            "INSERT INTO marker VALUES (1)",
        ];
        const string insert = "INSERT INTO t VALUES (1) ON CONFLICT(id) DO NOTHING";

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        foreach (var statement in setup)
            Execute(managed, statement);
        Assert.Throws<EmbeddedSqlException>(() => Execute(managed, insert))!
            .Message.Should().Contain("UNIQUE constraint failed");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in setup)
            Execute(sqlite, statement);
        Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, insert))!
            .Message.Should().Contain("UNIQUE constraint failed");

        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM t"),
            QuerySqlite(sqlite, "SELECT * FROM t"));
        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM marker"),
            QuerySqlite(sqlite, "SELECT * FROM marker"));
    }

    [Test]
    public void OuterInsertOrIgnorePolicyAppliesToTriggerUpdates()
    {
        string[] setup =
        [
            "CREATE TABLE t(id INTEGER PRIMARY KEY)",
            "CREATE TABLE audit(id INTEGER PRIMARY KEY, code TEXT UNIQUE)",
            "INSERT INTO audit VALUES (1, 'one'), (2, 'two')",
            """
            CREATE TRIGGER t_insert AFTER INSERT ON t BEGIN
                UPDATE audit SET code = 'one' WHERE id = 2;
            END
            """,
        ];
        const string insert = """
            INSERT OR IGNORE INTO t VALUES (1)
            ON CONFLICT(id) DO NOTHING
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        foreach (var statement in setup)
            Execute(managed, statement);
        Execute(managed, insert);

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in setup)
            Execute(sqlite, statement);
        Execute(sqlite, insert);

        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM t"),
            QuerySqlite(sqlite, "SELECT * FROM t"));
        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM audit ORDER BY id"),
            QuerySqlite(sqlite, "SELECT * FROM audit ORDER BY id"));
    }

    [Test]
    public void ReplacementTriggersObservePhaseCorrectLastInsertRowid()
    {
        string[] setup =
        [
            "CREATE TABLE t(id INTEGER PRIMARY KEY, code TEXT UNIQUE)",
            "CREATE TABLE audit(event TEXT, observed INTEGER)",
            "INSERT INTO t VALUES (2, 'two')",
            """
            CREATE TRIGGER t_delete AFTER DELETE ON t BEGIN
                INSERT INTO audit VALUES ('delete', last_insert_rowid());
            END
            """,
            """
            CREATE TRIGGER t_insert AFTER INSERT ON t BEGIN
                INSERT INTO audit VALUES ('insert', last_insert_rowid());
            END
            """,
            "PRAGMA recursive_triggers = ON",
        ];
        const string replace = """
            INSERT OR REPLACE INTO t VALUES (3, 'two')
            ON CONFLICT(id) DO NOTHING
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        foreach (var statement in setup)
            Execute(managed, statement);
        Execute(managed, replace);

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in setup)
            Execute(sqlite, statement);
        Execute(sqlite, replace);

        AssertOutputsEqual(
            QueryManaged(managed, "SELECT event, observed FROM audit ORDER BY rowid"),
            QuerySqlite(sqlite, "SELECT event, observed FROM audit ORDER BY rowid"));
    }

    [Test]
    public void ThrowingConflictProbePreservesLastInsertRowid()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        connection.RegisterCollation(
            "explosive",
            (left, right) => left == "boom" || right == "boom"
                ? throw new InvalidOperationException("collation failed")
                : string.CompareOrdinal(left, right));
        Execute(
            connection,
            "CREATE TABLE t(id INTEGER PRIMARY KEY, code TEXT COLLATE explosive UNIQUE);");
        Execute(connection, "INSERT INTO t VALUES (1, 'one'), (2, 'two');");

        Assert.Throws<InvalidOperationException>(
                () => Execute(
                    connection,
                    """
                    INSERT INTO t VALUES (3, 'three'), (4, 'boom')
                    ON CONFLICT(code) DO NOTHING;
                    """))!
            .Message.Should().Be("collation failed");
        QueryManaged(
                connection,
                "SELECT id, code, last_insert_rowid() AS lir FROM t ORDER BY id")
            .Rows.Should().Equal("I:1\u001fT:one\u001fI:3", "I:2\u001fT:two\u001fI:3");
    }

    [Test]
    public void NonAliasColumnPrimaryKeyParticipatesInConflictPolicyOrder()
    {
        const string setup = """
            CREATE TABLE t(
                code TEXT UNIQUE ON CONFLICT IGNORE,
                id TEXT PRIMARY KEY,
                arbiter TEXT UNIQUE
            )
            """;
        const string insert = """
            INSERT INTO t VALUES
                ('code-three', 'id-three', 'arbiter-three'),
                ('code-one', 'id-two', 'arbiter-four')
            ON CONFLICT(arbiter) DO NOTHING
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        Execute(managed, setup);
        Execute(
            managed,
            "INSERT INTO t VALUES ('code-one', 'id-one', 'arbiter-one'), "
                + "('code-two', 'id-two', 'arbiter-two');");
        Assert.Throws<EmbeddedSqlException>(() => Execute(managed, insert))!
            .Message.Should().Contain("UNIQUE constraint failed: t.id");
        var managedState = QueryManaged(managed, "SELECT * FROM t ORDER BY id");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        Execute(sqlite, setup);
        Execute(
            sqlite,
            "INSERT INTO t VALUES ('code-one', 'id-one', 'arbiter-one'), "
                + "('code-two', 'id-two', 'arbiter-two');");
        Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, insert))!
            .Message.Should().Contain("UNIQUE constraint failed: t.id");
        var sqliteState = QuerySqlite(sqlite, "SELECT * FROM t ORDER BY id");

        AssertOutputsEqual(managedState, sqliteState);
    }

    [Test]
    public void TargetlessDoNothingStopsAtTheFirstMatchingArbiter()
    {
        const string setup = """
            CREATE TABLE t(
                first_key TEXT COLLATE explosive UNIQUE,
                final_key TEXT UNIQUE
            )
            """;
        const string insert = """
            INSERT INTO t VALUES ('first', 'new'), ('boom', 'duplicate')
            ON CONFLICT DO NOTHING
            """;
        static int Compare(string left, string right)
            => left == "boom" || right == "boom"
                ? throw new InvalidOperationException("collation should not run")
                : string.CompareOrdinal(left, right);

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        managed.RegisterCollation("explosive", Compare);
        Execute(managed, setup);
        Execute(managed, "INSERT INTO t VALUES ('safe', 'duplicate');");
        Execute(managed, insert);
        var managedState = QueryManaged(managed, "SELECT * FROM t ORDER BY final_key");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        sqlite.CreateCollation("explosive", Compare);
        Execute(sqlite, setup);
        Execute(sqlite, "INSERT INTO t VALUES ('safe', 'duplicate');");
        Execute(sqlite, insert);
        var sqliteState = QuerySqlite(sqlite, "SELECT * FROM t ORDER BY final_key");

        AssertOutputsEqual(managedState, sqliteState);
    }

    [Test]
    public void InheritedIgnoreDoesNotSuppressStrictDatatypeErrors()
    {
        string[] setup =
        [
            "CREATE TABLE t(id INTEGER PRIMARY KEY)",
            "CREATE TABLE audit(id INTEGER PRIMARY KEY, value INTEGER) STRICT",
            "INSERT INTO audit VALUES (1, 1)",
            """
            CREATE TRIGGER t_insert AFTER INSERT ON t BEGIN
                UPDATE audit SET value = 'text' WHERE id = 1;
            END
            """,
        ];
        const string insert = """
            INSERT OR IGNORE INTO t VALUES (1)
            ON CONFLICT(id) DO NOTHING
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        foreach (var statement in setup)
            Execute(managed, statement);
        Assert.Throws<EmbeddedSqlException>(() => Execute(managed, insert));

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in setup)
            Execute(sqlite, statement);
        Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, insert));

        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM t"),
            QuerySqlite(sqlite, "SELECT * FROM t"));
        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM audit"),
            QuerySqlite(sqlite, "SELECT * FROM audit"));
    }

    [Test]
    public void TriggerRollbackRecordsTheOuterCandidateRowid()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY);");
        Execute(connection, "CREATE TABLE audit(value INTEGER UNIQUE);");
        Execute(connection, "INSERT INTO t VALUES (2);");
        Execute(connection, "INSERT INTO audit VALUES (1);");
        Execute(
            connection,
            """
            CREATE TRIGGER t_insert AFTER INSERT ON t BEGIN
                INSERT OR ROLLBACK INTO audit VALUES (1);
            END;
            """);

        Assert.Throws<EmbeddedSqlException>(
                () => Execute(
                    connection,
                    "INSERT INTO t VALUES (10) ON CONFLICT(id) DO NOTHING;"))!
            .Message.Should().Contain("UNIQUE constraint failed");
        QueryManaged(connection, "SELECT id, last_insert_rowid() AS lir FROM t;")
            .Rows.Should().Equal("I:2\u001fI:10");
    }

    [Test]
    public void DescendingColumnPrimaryKeyKeepsDescendingAutoindexMetadata()
    {
        const string create = "CREATE TABLE t(id TEXT PRIMARY KEY DESC, value INTEGER)";

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        Execute(managed, create);
        var managedInfo = QueryManaged(managed, "PRAGMA index_xinfo(sqlite_autoindex_t_1)");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        Execute(sqlite, create);
        var sqliteInfo = QuerySqlite(sqlite, "PRAGMA index_xinfo(sqlite_autoindex_t_1)");

        AssertOutputsEqual(managedInfo, sqliteInfo);
    }

    [Test]
    public void DoUpdateTriggersAlwaysUseAbortConflictPolicy()
    {
        string[] setup =
        [
            "CREATE TABLE t(id INTEGER PRIMARY KEY, value INTEGER)",
            "CREATE TABLE audit(value INTEGER UNIQUE)",
            "INSERT INTO t VALUES (1, 1)",
            "INSERT INTO audit VALUES (1)",
            """
            CREATE TRIGGER t_update AFTER UPDATE ON t BEGIN
                INSERT OR IGNORE INTO audit VALUES (1);
            END
            """,
        ];
        const string upsert = """
            INSERT OR IGNORE INTO t VALUES (1, 2)
            ON CONFLICT(id) DO UPDATE SET value = excluded.value
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        foreach (var statement in setup)
            Execute(managed, statement);
        Assert.Throws<EmbeddedSqlException>(() => Execute(managed, upsert))!
            .Message.Should().Contain("UNIQUE constraint failed");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in setup)
            Execute(sqlite, statement);
        Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, upsert))!
            .Message.Should().Contain("UNIQUE constraint failed");

        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM t"),
            QuerySqlite(sqlite, "SELECT * FROM t"));
    }

    [Test]
    public void InsertOrFailRetainsOuterRowWhenInsertTriggerFailsImmediately()
    {
        string[] setup =
        [
            "CREATE TABLE t(id INTEGER PRIMARY KEY)",
            "CREATE TABLE audit(value INTEGER UNIQUE)",
            "INSERT INTO audit VALUES (1)",
            """
            CREATE TRIGGER t_insert AFTER INSERT ON t BEGIN
                INSERT INTO audit VALUES (1);
            END
            """,
        ];
        const string insert = """
            INSERT OR FAIL INTO t VALUES (1)
            ON CONFLICT(id) DO NOTHING
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        foreach (var statement in setup)
            Execute(managed, statement);
        Assert.Throws<EmbeddedSqlException>(() => Execute(managed, insert))!
            .Message.Should().Contain("UNIQUE constraint failed");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in setup)
            Execute(sqlite, statement);
        Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, insert))!
            .Message.Should().Contain("UNIQUE constraint failed");

        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM t"),
            QuerySqlite(sqlite, "SELECT * FROM t"));
    }

    [Test]
    public void UpsertExpressionsObserveTheCurrentLastInsertRowid()
    {
        const string setup = "CREATE TABLE t(id INTEGER PRIMARY KEY, value INTEGER)";
        const string upsert = """
            INSERT INTO t VALUES (3, 30), (1, 99)
            ON CONFLICT(id) DO UPDATE SET value = last_insert_rowid()
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        Execute(managed, setup);
        Execute(managed, "INSERT INTO t VALUES (1, 10), (2, 20)");
        Execute(managed, upsert);

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        Execute(sqlite, setup);
        Execute(sqlite, "INSERT INTO t VALUES (1, 10), (2, 20)");
        Execute(sqlite, upsert);

        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM t ORDER BY id"),
            QuerySqlite(sqlite, "SELECT * FROM t ORDER BY id"));

        using var returningDatabase = new EmbeddedDatabase();
        using var returningManaged = returningDatabase.Connect();
        Execute(returningManaged, setup);
        var managedReturning = QueryManaged(
            returningManaged,
            """
            INSERT INTO t VALUES (1, 10), (2, 20)
            ON CONFLICT(id) DO NOTHING
            RETURNING id, last_insert_rowid() AS lir
            """);
        using var returningSqlite = new MsData.SqliteConnection("Data Source=:memory:");
        returningSqlite.Open();
        Execute(returningSqlite, setup);
        var sqliteReturning = QuerySqlite(
            returningSqlite,
            """
            INSERT INTO t VALUES (1, 10), (2, 20)
            ON CONFLICT(id) DO NOTHING
            RETURNING id, last_insert_rowid() AS lir
            """);
        AssertOutputsEqual(managedReturning, sqliteReturning);

        using var updateFirstDatabase = new EmbeddedDatabase();
        using var updateFirstManaged = updateFirstDatabase.Connect();
        Execute(updateFirstManaged, setup);
        Execute(updateFirstManaged, "CREATE TABLE marker(id INTEGER PRIMARY KEY)");
        Execute(updateFirstManaged, "INSERT INTO t VALUES (1, 10)");
        Execute(updateFirstManaged, "INSERT INTO marker VALUES (9)");
        var managedUpdateFirst = QueryManaged(
            updateFirstManaged,
            """
            INSERT INTO t VALUES (1, 11), (2, 20)
            ON CONFLICT(id) DO UPDATE SET value = excluded.value
            RETURNING id, last_insert_rowid() AS lir
            """);
        using var updateFirstSqlite = new MsData.SqliteConnection("Data Source=:memory:");
        updateFirstSqlite.Open();
        Execute(updateFirstSqlite, setup);
        Execute(updateFirstSqlite, "CREATE TABLE marker(id INTEGER PRIMARY KEY)");
        Execute(updateFirstSqlite, "INSERT INTO t VALUES (1, 10)");
        Execute(updateFirstSqlite, "INSERT INTO marker VALUES (9)");
        var sqliteUpdateFirst = QuerySqlite(
            updateFirstSqlite,
            """
            INSERT INTO t VALUES (1, 11), (2, 20)
            ON CONFLICT(id) DO UPDATE SET value = excluded.value
            RETURNING id, last_insert_rowid() AS lir
            """);
        AssertOutputsEqual(managedUpdateFirst, sqliteUpdateFirst);
    }

    [Test]
    public void ReplacementFailurePreservesPriorSuccessfulSourceRowid()
    {
        string[] setup =
        [
            "CREATE TABLE t(id INTEGER PRIMARY KEY, code TEXT UNIQUE)",
            "INSERT INTO t VALUES (1, 'one'), (2, 'two')",
            """
            CREATE TRIGGER t_delete AFTER DELETE ON t BEGIN
                INSERT INTO t VALUES (99, 'two');
            END
            """,
            "PRAGMA recursive_triggers = ON",
        ];
        const string replace = """
            INSERT OR REPLACE INTO t VALUES (3, 'three'), (4, 'two')
            ON CONFLICT(id) DO NOTHING
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        foreach (var statement in setup)
            Execute(managed, statement);
        Assert.Throws<EmbeddedSqlException>(() => Execute(managed, replace))!
            .Message.Should().Contain("UNIQUE constraint failed");
        var managedState = QueryManaged(
            managed,
            "SELECT id, code, last_insert_rowid() AS lir FROM t ORDER BY id");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in setup)
            Execute(sqlite, statement);
        Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, replace))!
            .Message.Should().Contain("UNIQUE constraint failed");
        var sqliteState = QuerySqlite(
            sqlite,
            "SELECT id, code, last_insert_rowid() AS lir FROM t ORDER BY id");

        AssertOutputsEqual(managedState, sqliteState);
    }

    [Test]
    public void ExplicitCollationDoesNotInferARowidAliasPrimaryKey()
    {
        const string setup = "CREATE TABLE t(id INTEGER PRIMARY KEY, value INTEGER)";
        const string upsert = """
            INSERT INTO t VALUES (1, 2)
            ON CONFLICT(id COLLATE BINARY) DO UPDATE SET value = excluded.value
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        Execute(managed, setup);
        Execute(managed, "INSERT INTO t VALUES (1, 1)");
        Assert.Throws<EmbeddedSqlException>(() => Execute(managed, upsert))!
            .Message.Should().Contain("ON CONFLICT clause does not match");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        Execute(sqlite, setup);
        Execute(sqlite, "INSERT INTO t VALUES (1, 1)");
        Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, upsert))!
            .Message.Should().Contain("ON CONFLICT clause does not match");

        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM t"),
            QuerySqlite(sqlite, "SELECT * FROM t"));
    }

    [Test]
    public void DoUpdateCascadeTriggersUseAbortConflictPolicy()
    {
        string[] setup =
        [
            "PRAGMA foreign_keys = ON",
            "CREATE TABLE parent(id INTEGER PRIMARY KEY, parent_key TEXT UNIQUE)",
            """
            CREATE TABLE child(
                parent_key TEXT REFERENCES parent(parent_key) ON UPDATE CASCADE
            )
            """,
            "CREATE TABLE audit(value INTEGER UNIQUE)",
            "INSERT INTO parent VALUES (1, 'old')",
            "INSERT INTO child VALUES ('old')",
            "INSERT INTO audit VALUES (1)",
            """
            CREATE TRIGGER child_update AFTER UPDATE ON child BEGIN
                INSERT OR IGNORE INTO audit VALUES (1);
            END
            """,
        ];
        const string upsert = """
            INSERT OR IGNORE INTO parent VALUES (1, 'new')
            ON CONFLICT(id) DO UPDATE SET parent_key = excluded.parent_key
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        foreach (var statement in setup)
            Execute(managed, statement);
        Assert.Throws<EmbeddedSqlException>(() => Execute(managed, upsert))!
            .Message.Should().Contain("UNIQUE constraint failed");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in setup)
            Execute(sqlite, statement);
        Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, upsert))!
            .Message.Should().Contain("UNIQUE constraint failed");

        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM parent"),
            QuerySqlite(sqlite, "SELECT * FROM parent"));
        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM child"),
            QuerySqlite(sqlite, "SELECT * FROM child"));
    }

    [Test]
    public void TriggerLocalFailRetainsTheOuterInsertedRow()
    {
        string[] setup =
        [
            "CREATE TABLE t(id INTEGER PRIMARY KEY)",
            "CREATE TABLE audit(value INTEGER UNIQUE ON CONFLICT FAIL)",
            "INSERT INTO audit VALUES (1)",
            """
            CREATE TRIGGER t_insert AFTER INSERT ON t BEGIN
                INSERT INTO audit VALUES (1);
            END
            """,
        ];
        const string insert = "INSERT INTO t VALUES (10) ON CONFLICT(id) DO NOTHING";

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        foreach (var statement in setup)
            Execute(managed, statement);
        Assert.Throws<EmbeddedSqlException>(() => Execute(managed, insert))!
            .Message.Should().Contain("UNIQUE constraint failed");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in setup)
            Execute(sqlite, statement);
        Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, insert))!
            .Message.Should().Contain("UNIQUE constraint failed");

        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM t"),
            QuerySqlite(sqlite, "SELECT * FROM t"));
    }

    [Test]
    public void DuplicatePrimaryAndUniqueArbitersShareThePrimaryKeyPolicy()
    {
        const string setup = """
            CREATE TABLE t(
                key TEXT PRIMARY KEY ON CONFLICT IGNORE,
                value TEXT UNIQUE,
                UNIQUE(key)
            )
            """;
        const string insert = """
            INSERT INTO t VALUES ('key', 'new-value')
            ON CONFLICT(value) DO NOTHING
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        Execute(managed, setup);
        Execute(managed, "INSERT INTO t VALUES ('key', 'old-value')");
        Execute(managed, insert);

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        Execute(sqlite, setup);
        Execute(sqlite, "INSERT INTO t VALUES ('key', 'old-value')");
        Execute(sqlite, insert);

        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM t"),
            QuerySqlite(sqlite, "SELECT * FROM t"));
        AssertOutputsEqual(
            QueryManaged(managed, "PRAGMA index_list(t)"),
            QuerySqlite(sqlite, "PRAGMA index_list(t)"));
    }

    [Test]
    public void WithoutRowidDuplicateArbitersMergePoliciesAndRejectConflicts()
    {
        const string setup = """
            CREATE TABLE t(
                key TEXT PRIMARY KEY,
                value TEXT UNIQUE,
                UNIQUE(key) ON CONFLICT IGNORE
            ) WITHOUT ROWID
            """;
        const string insert = """
            INSERT INTO t VALUES ('key', 'new-value')
            ON CONFLICT(value) DO NOTHING
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        Execute(managed, setup);
        Execute(managed, "INSERT INTO t VALUES ('key', 'old-value')");
        Execute(managed, insert);

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        Execute(sqlite, setup);
        Execute(sqlite, "INSERT INTO t VALUES ('key', 'old-value')");
        Execute(sqlite, insert);

        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM t"),
            QuerySqlite(sqlite, "SELECT * FROM t"));

        const string conflicting = """
            CREATE TABLE invalid(
                key TEXT,
                PRIMARY KEY(key) ON CONFLICT IGNORE,
                UNIQUE(key) ON CONFLICT FAIL
            ) WITHOUT ROWID
            """;
        Assert.Throws<EmbeddedSqlException>(() => Execute(managed, conflicting))!
            .Message.Should().Contain("conflicting ON CONFLICT clauses");
        Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, conflicting))!
            .Message.Should().Contain("conflicting ON CONFLICT clauses");
    }

    [Test]
    public void CoalescedArbitersKeepTheFirstSortDescriptor()
    {
        const string rowidTable = "CREATE TABLE rowid_key(key TEXT UNIQUE PRIMARY KEY DESC)";

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        Execute(managed, rowidTable);
        var managedIndex = QueryManaged(
            managed,
            "PRAGMA index_xinfo(sqlite_autoindex_rowid_key_1)");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        Execute(sqlite, rowidTable);
        var sqliteIndex = QuerySqlite(
            sqlite,
            "PRAGMA index_xinfo(sqlite_autoindex_rowid_key_1)");
        AssertOutputsEqual(managedIndex, sqliteIndex);

        const string withoutRowid = """
            CREATE TABLE keyed(
                first_key TEXT,
                second_key TEXT,
                UNIQUE(first_key ASC, second_key DESC),
                PRIMARY KEY(first_key DESC, second_key ASC)
            ) WITHOUT ROWID
            """;
        Execute(managed, withoutRowid);
        Execute(sqlite, withoutRowid);
        Execute(
            managed,
            "INSERT INTO keyed VALUES ('b', 'one'), ('a', 'one'), ('a', 'two')");
        Execute(
            sqlite,
            "INSERT INTO keyed VALUES ('b', 'one'), ('a', 'one'), ('a', 'two')");
        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM keyed"),
            QuerySqlite(sqlite, "SELECT * FROM keyed"));
    }

    [Test]
    public void MergedPrimaryKeyPolicyControlsTriggeredDuplicates()
    {
        string[] setup =
        [
            "CREATE TABLE target(id INTEGER PRIMARY KEY)",
            "CREATE TABLE audit(key TEXT PRIMARY KEY, UNIQUE(key) ON CONFLICT IGNORE)",
            "INSERT INTO audit VALUES ('duplicate')",
            """
            CREATE TRIGGER target_insert AFTER INSERT ON target BEGIN
                INSERT INTO audit VALUES ('duplicate');
            END
            """,
        ];
        const string insert = "INSERT INTO target VALUES (1) ON CONFLICT(id) DO NOTHING";

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        foreach (var statement in setup)
            Execute(managed, statement);
        Execute(managed, insert);

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in setup)
            Execute(sqlite, statement);
        Execute(sqlite, insert);

        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM target"),
            QuerySqlite(sqlite, "SELECT * FROM target"));
        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM audit"),
            QuerySqlite(sqlite, "SELECT * FROM audit"));
    }

    [Test]
    public void TriggerBodyStatementsObserveInnerLastInsertRowid()
    {
        string[] setup =
        [
            "CREATE TABLE t(id INTEGER PRIMARY KEY)",
            "CREATE TABLE generated(id INTEGER PRIMARY KEY)",
            "CREATE TABLE audit(observed INTEGER)",
            """
            CREATE TRIGGER t_insert AFTER INSERT ON t BEGIN
                INSERT INTO generated VALUES (41);
                INSERT INTO audit VALUES (last_insert_rowid());
            END
            """,
        ];
        const string insert = "INSERT INTO t VALUES (7) ON CONFLICT(id) DO NOTHING";

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        foreach (var statement in setup)
            Execute(managed, statement);
        Execute(managed, insert);

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in setup)
            Execute(sqlite, statement);
        Execute(sqlite, insert);

        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM audit"),
            QuerySqlite(sqlite, "SELECT * FROM audit"));
        QueryManaged(managed, "SELECT last_insert_rowid() AS lir")
            .Rows.Should().Equal("I:7");
    }

    [Test]
    public void ForeignKeyCascadeTriggerFailRetainsReplacementPrefix()
    {
        string[] setup =
        [
            "PRAGMA foreign_keys = ON",
            """
            CREATE TABLE parent(
                id INTEGER PRIMARY KEY,
                code TEXT UNIQUE ON CONFLICT REPLACE
            )
            """,
            """
            CREATE TABLE child(
                parent_id INTEGER REFERENCES parent(id) ON DELETE CASCADE
            )
            """,
            "CREATE TABLE audit(value INTEGER UNIQUE)",
            "INSERT INTO parent VALUES (1, 'one')",
            "INSERT INTO child VALUES (1)",
            "INSERT INTO audit VALUES (100)",
            """
            CREATE TRIGGER child_delete AFTER DELETE ON child BEGIN
                INSERT OR FAIL INTO audit VALUES (200), (100);
            END
            """,
        ];
        const string insert = """
            INSERT INTO parent VALUES (2, 'one')
            ON CONFLICT(id) DO NOTHING
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        foreach (var statement in setup)
            Execute(managed, statement);
        Assert.Throws<EmbeddedSqlException>(() => Execute(managed, insert))!
            .Message.Should().Contain("UNIQUE constraint failed");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in setup)
            Execute(sqlite, statement);
        Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, insert))!
            .Message.Should().Contain("UNIQUE constraint failed");

        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM parent"),
            QuerySqlite(sqlite, "SELECT * FROM parent"));
        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM child"),
            QuerySqlite(sqlite, "SELECT * FROM child"));
        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM audit ORDER BY value"),
            QuerySqlite(sqlite, "SELECT * FROM audit ORDER BY value"));
    }

    [Test]
    public void ForeignKeySetNullTriggerFailAbortsReplacement()
    {
        string[] setup =
        [
            "PRAGMA foreign_keys = ON",
            """
            CREATE TABLE parent(
                id INTEGER PRIMARY KEY,
                code TEXT UNIQUE ON CONFLICT REPLACE
            )
            """,
            """
            CREATE TABLE child(
                parent_id INTEGER REFERENCES parent(id) ON DELETE SET NULL
            )
            """,
            "CREATE TABLE audit(value INTEGER UNIQUE)",
            "INSERT INTO parent VALUES (1, 'one')",
            "INSERT INTO child VALUES (1)",
            "INSERT INTO audit VALUES (100)",
            """
            CREATE TRIGGER child_update AFTER UPDATE ON child BEGIN
                INSERT OR FAIL INTO audit VALUES (200), (100);
            END
            """,
        ];
        const string insert = """
            INSERT INTO parent VALUES (2, 'one')
            ON CONFLICT(id) DO NOTHING
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        foreach (var statement in setup)
            Execute(managed, statement);
        Assert.Throws<EmbeddedSqlException>(() => Execute(managed, insert))!
            .Message.Should().Contain("UNIQUE constraint failed");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in setup)
            Execute(sqlite, statement);
        Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, insert))!
            .Message.Should().Contain("UNIQUE constraint failed");

        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM parent"),
            QuerySqlite(sqlite, "SELECT * FROM parent"));
        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM child"),
            QuerySqlite(sqlite, "SELECT * FROM child"));
        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM audit ORDER BY value"),
            QuerySqlite(sqlite, "SELECT * FROM audit ORDER BY value"));
    }

    [Test]
    public void ForeignKeySetNullNestedUpsertFailAbortsReplacement()
    {
        string[] setup =
        [
            "PRAGMA foreign_keys = ON",
            """
            CREATE TABLE parent(
                id INTEGER PRIMARY KEY,
                code TEXT UNIQUE ON CONFLICT REPLACE
            )
            """,
            """
            CREATE TABLE child(
                parent_id INTEGER REFERENCES parent(id) ON DELETE SET NULL
            )
            """,
            "CREATE TABLE audit(id INTEGER PRIMARY KEY, code TEXT UNIQUE)",
            "INSERT INTO parent VALUES (1, 'one')",
            "INSERT INTO child VALUES (1)",
            "INSERT INTO audit VALUES (1, 'duplicate')",
            """
            CREATE TRIGGER child_update AFTER UPDATE ON child BEGIN
                INSERT OR FAIL INTO audit VALUES (2, 'prefix'), (3, 'duplicate')
                ON CONFLICT(id) DO NOTHING;
            END
            """,
        ];
        const string insert = """
            INSERT INTO parent VALUES (2, 'one')
            ON CONFLICT(id) DO NOTHING
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        foreach (var statement in setup)
            Execute(managed, statement);
        Assert.Throws<EmbeddedSqlException>(() => Execute(managed, insert))!
            .Message.Should().Contain("UNIQUE constraint failed");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in setup)
            Execute(sqlite, statement);
        Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, insert))!
            .Message.Should().Contain("UNIQUE constraint failed");

        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM parent"),
            QuerySqlite(sqlite, "SELECT * FROM parent"));
        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM child"),
            QuerySqlite(sqlite, "SELECT * FROM child"));
        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM audit ORDER BY id"),
            QuerySqlite(sqlite, "SELECT * FROM audit ORDER BY id"));
    }

    [Test]
    public void OrdinaryTriggerNestedUpsertFailRetainsPrefixes()
    {
        string[] setup =
        [
            "CREATE TABLE target(id INTEGER PRIMARY KEY)",
            "CREATE TABLE audit(id INTEGER PRIMARY KEY, code TEXT UNIQUE)",
            "INSERT INTO audit VALUES (1, 'duplicate')",
            """
            CREATE TRIGGER target_insert AFTER INSERT ON target BEGIN
                INSERT OR FAIL INTO audit VALUES (2, 'prefix'), (3, 'duplicate')
                ON CONFLICT(id) DO NOTHING;
            END
            """,
        ];
        const string insert = "INSERT INTO target VALUES (10) ON CONFLICT(id) DO NOTHING";

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        foreach (var statement in setup)
            Execute(managed, statement);
        Assert.Throws<EmbeddedSqlException>(() => Execute(managed, insert))!
            .Message.Should().Contain("UNIQUE constraint failed");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in setup)
            Execute(sqlite, statement);
        Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, insert))!
            .Message.Should().Contain("UNIQUE constraint failed");

        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM target"),
            QuerySqlite(sqlite, "SELECT * FROM target"));
        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM audit ORDER BY id"),
            QuerySqlite(sqlite, "SELECT * FROM audit ORDER BY id"));
    }

    [Test]
    public void ForeignKeyCascadeImmediateNestedUpsertFailRetainsDeletionPrefix()
    {
        string[] setup =
        [
            "PRAGMA foreign_keys = ON",
            "CREATE TABLE parent(id INTEGER PRIMARY KEY, code TEXT UNIQUE ON CONFLICT REPLACE)",
            "CREATE TABLE child(parent_id INTEGER REFERENCES parent(id) ON DELETE CASCADE)",
            "CREATE TABLE audit(id INTEGER PRIMARY KEY, code TEXT UNIQUE)",
            "CREATE TABLE marker(id INTEGER PRIMARY KEY)",
            "INSERT INTO parent VALUES (1, 'one')",
            "INSERT INTO child VALUES (1)",
            "INSERT INTO audit VALUES (1, 'duplicate')",
            "INSERT INTO marker VALUES (99)",
            """
            CREATE TRIGGER child_delete AFTER DELETE ON child BEGIN
                INSERT INTO audit VALUES (41, 'inner');
                INSERT OR FAIL INTO audit VALUES (2, 'duplicate')
                ON CONFLICT(id) DO NOTHING;
            END
            """,
        ];
        const string insert = "INSERT INTO parent VALUES (2, 'one') ON CONFLICT(id) DO NOTHING";

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        foreach (var statement in setup)
            Execute(managed, statement);
        Assert.Throws<EmbeddedSqlException>(() => Execute(managed, insert))!
            .Message.Should().Contain("UNIQUE constraint failed");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in setup)
            Execute(sqlite, statement);
        Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, insert))!
            .Message.Should().Contain("UNIQUE constraint failed");

        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM parent"),
            QuerySqlite(sqlite, "SELECT * FROM parent"));
        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM child"),
            QuerySqlite(sqlite, "SELECT * FROM child"));
        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM audit"),
            QuerySqlite(sqlite, "SELECT * FROM audit"));
        AssertOutputsEqual(
            QueryManaged(managed, "SELECT last_insert_rowid() AS lir"),
            QuerySqlite(sqlite, "SELECT last_insert_rowid() AS lir"));
    }

    [Test]
    public void OuterReplaceDoesNotOverrideForeignKeyUpdateTriggerFail()
    {
        string[] setup =
        [
            "PRAGMA foreign_keys = ON",
            "CREATE TABLE parent(id INTEGER PRIMARY KEY, code TEXT UNIQUE)",
            "CREATE TABLE child(parent_id INTEGER REFERENCES parent(id) ON DELETE SET NULL)",
            "CREATE TABLE audit(value INTEGER UNIQUE)",
            "INSERT INTO parent VALUES (1, 'one')",
            "INSERT INTO child VALUES (1)",
            "INSERT INTO audit VALUES (100)",
            """
            CREATE TRIGGER child_update AFTER UPDATE ON child BEGIN
                INSERT OR FAIL INTO audit VALUES (200), (100);
            END
            """,
        ];
        const string insert = """
            INSERT OR REPLACE INTO parent VALUES (2, 'one')
            ON CONFLICT(id) DO NOTHING
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        foreach (var statement in setup)
            Execute(managed, statement);
        Assert.Throws<EmbeddedSqlException>(() => Execute(managed, insert))!
            .Message.Should().Contain("UNIQUE constraint failed");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in setup)
            Execute(sqlite, statement);
        Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, insert))!
            .Message.Should().Contain("UNIQUE constraint failed");

        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM parent"),
            QuerySqlite(sqlite, "SELECT * FROM parent"));
        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM child"),
            QuerySqlite(sqlite, "SELECT * FROM child"));
        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM audit ORDER BY value"),
            QuerySqlite(sqlite, "SELECT * FROM audit ORDER BY value"));
    }

    [Test]
    public void OuterFailCannotReclassifyAtomicForeignKeyUpdateFailure()
    {
        string[] setup =
        [
            "PRAGMA foreign_keys = ON",
            "CREATE TABLE driver(id INTEGER PRIMARY KEY)",
            "CREATE TABLE parent(id INTEGER PRIMARY KEY, parent_key TEXT UNIQUE)",
            "CREATE TABLE child(parent_key TEXT REFERENCES parent(parent_key) ON UPDATE CASCADE)",
            "CREATE TABLE audit(id INTEGER PRIMARY KEY, code TEXT UNIQUE)",
            "INSERT INTO parent VALUES (1, 'old')",
            "INSERT INTO child VALUES ('old')",
            "INSERT INTO audit VALUES (1, 'duplicate')",
            """
            CREATE TRIGGER child_update AFTER UPDATE ON child BEGIN
                INSERT OR FAIL INTO audit VALUES (2, 'prefix'), (3, 'duplicate')
                ON CONFLICT(id) DO NOTHING;
            END
            """,
            """
            CREATE TRIGGER driver_insert AFTER INSERT ON driver BEGIN
                UPDATE parent SET parent_key = 'new' WHERE id = 1;
            END
            """,
        ];
        const string insert = """
            INSERT OR FAIL INTO driver VALUES (1)
            ON CONFLICT(id) DO NOTHING
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        foreach (var statement in setup)
            Execute(managed, statement);
        Assert.Throws<EmbeddedSqlException>(() => Execute(managed, insert))!
            .Message.Should().Contain("UNIQUE constraint failed");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in setup)
            Execute(sqlite, statement);
        Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, insert))!
            .Message.Should().Contain("UNIQUE constraint failed");

        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM driver"),
            QuerySqlite(sqlite, "SELECT * FROM driver"));
        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM parent"),
            QuerySqlite(sqlite, "SELECT * FROM parent"));
        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM child"),
            QuerySqlite(sqlite, "SELECT * FROM child"));
        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM audit ORDER BY id"),
            QuerySqlite(sqlite, "SELECT * FROM audit ORDER BY id"));
    }

    [Test]
    public void ForeignKeyActionsDoNotAdvanceLaterParentRowsBeforeFail()
    {
        string[] setup =
        [
            "PRAGMA foreign_keys = ON",
            "CREATE TABLE parent(id INTEGER PRIMARY KEY, parent_key TEXT UNIQUE)",
            "CREATE TABLE first_child(parent_key TEXT REFERENCES parent(parent_key) ON UPDATE CASCADE)",
            "CREATE TABLE second_child(parent_key TEXT REFERENCES parent(parent_key) ON UPDATE CASCADE)",
            "CREATE TABLE audit(value TEXT UNIQUE)",
            "INSERT INTO parent VALUES (1, 'one'), (2, 'two')",
            "INSERT INTO first_child VALUES ('one'), ('two')",
            "INSERT INTO second_child VALUES ('one'), ('two')",
            "INSERT INTO audit VALUES ('duplicate')",
            """
            CREATE TRIGGER second_child_update AFTER UPDATE ON second_child
            WHEN NEW.parent_key = 'two-next'
            BEGIN
                INSERT OR FAIL INTO audit VALUES ('prefix'), ('duplicate');
            END
            """,
        ];
        const string update = "UPDATE parent SET parent_key = parent_key || '-next'";

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        foreach (var statement in setup)
            Execute(managed, statement);
        Assert.Throws<EmbeddedSqlException>(() => Execute(managed, update))!
            .Message.Should().Contain("UNIQUE constraint failed");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in setup)
            Execute(sqlite, statement);
        Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, update))!
            .Message.Should().Contain("UNIQUE constraint failed");

        foreach (var (table, columns) in new[]
                 {
                     ("parent", "id, parent_key"),
                     ("first_child", "parent_key"),
                     ("second_child", "parent_key"),
                     ("audit", "value"),
                 })
        {
            AssertOutputsEqual(
                QueryManaged(managed, $"SELECT {columns} FROM {table} ORDER BY 1"),
                QuerySqlite(sqlite, $"SELECT {columns} FROM {table} ORDER BY 1"));
        }
    }

    [Test]
    public void StandardInsertTriggerFailureRecordsTheOuterInsertRowid()
    {
        string[] setup =
        [
            "PRAGMA foreign_keys = ON",
            "CREATE TABLE driver(id INTEGER PRIMARY KEY)",
            "CREATE TABLE marker(id INTEGER PRIMARY KEY)",
            "CREATE TABLE parent(id INTEGER PRIMARY KEY, parent_key TEXT UNIQUE)",
            "CREATE TABLE child(parent_key TEXT REFERENCES parent(parent_key) ON UPDATE CASCADE)",
            "CREATE TABLE audit(id INTEGER PRIMARY KEY, code TEXT UNIQUE)",
            "INSERT INTO parent VALUES (1, 'old')",
            "INSERT INTO child VALUES ('old')",
            "INSERT INTO audit VALUES (1, 'duplicate')",
            "INSERT INTO marker VALUES (99)",
            """
            CREATE TRIGGER child_update AFTER UPDATE ON child BEGIN
                INSERT OR FAIL INTO audit VALUES (2, 'prefix'), (3, 'duplicate')
                ON CONFLICT(id) DO NOTHING;
            END
            """,
            """
            CREATE TRIGGER driver_insert AFTER INSERT ON driver BEGIN
                UPDATE parent SET parent_key = 'new' WHERE id = 1;
            END
            """,
        ];
        const string insert = "INSERT INTO driver VALUES (10)";

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        foreach (var statement in setup)
            Execute(managed, statement);
        Assert.Throws<EmbeddedSqlException>(() => Execute(managed, insert))!
            .Message.Should().Contain("UNIQUE constraint failed");

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in setup)
            Execute(sqlite, statement);
        Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, insert))!
            .Message.Should().Contain("UNIQUE constraint failed");

        AssertOutputsEqual(
            QueryManaged(managed, "SELECT * FROM driver"),
            QuerySqlite(sqlite, "SELECT * FROM driver"));
        AssertOutputsEqual(
            QueryManaged(managed, "SELECT last_insert_rowid() AS lir"),
            QuerySqlite(sqlite, "SELECT last_insert_rowid() AS lir"));
    }

    private static TestCaseData Case(string name, string[] statements, string query)
        => new(new DifferentialCase(statements, query)) { TestName = name };

    private static TestCaseData RejectionCase(
        string name,
        string[] setup,
        string statement,
        string errorFragment,
        string query)
        => new(new RejectionDifferentialCase(setup, statement, errorFragment, query)) { TestName = name };

    private static void AssertOutputsEqual(QueryOutput managed, QueryOutput sqlite)
    {
        managed.Columns.Should().Equal(sqlite.Columns);
        managed.Rows.Should().Equal(sqlite.Rows);
    }

    [Test]
    public void ChainedConflictClausesUseFirstMatchingActionForMultirowInsertsAndTriggers()
    {
        string[] setup =
        [
            "CREATE TABLE t(id INTEGER PRIMARY KEY, code TEXT UNIQUE, alternate TEXT UNIQUE, value TEXT)",
            "CREATE TABLE audit(id INTEGER, value TEXT)",
            "CREATE TRIGGER t_update AFTER UPDATE ON t BEGIN INSERT INTO audit VALUES (NEW.id, NEW.value); END",
            "INSERT INTO t VALUES (1, 'one', 'alpha', 'seed-one'), (2, 'two', 'beta', 'seed-two')",
        ];
        const string insert = """
            INSERT INTO t VALUES
                (3, 'one', 'new', 'ignored'),
                (4, 'new', 'beta', 'alternate'),
                (1, 'newer', 'newest', 'fallback')
            ON CONFLICT(code) DO NOTHING
            ON CONFLICT(alternate) DO UPDATE SET value = excluded.value || '-alternate'
            ON CONFLICT DO UPDATE SET value = excluded.value || '-fallback'
            """;

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in setup)
        {
            Execute(managed, statement);
            Execute(sqlite, statement);
        }

        Execute(managed, insert);
        Execute(sqlite, insert);

        AssertOutputsEqual(
            QueryManaged(managed, "SELECT id, code, alternate, value FROM t ORDER BY id"),
            QuerySqlite(sqlite, "SELECT id, code, alternate, value FROM t ORDER BY id"));
        AssertOutputsEqual(
            QueryManaged(managed, "SELECT id, value FROM audit ORDER BY rowid"),
            QuerySqlite(sqlite, "SELECT id, value FROM audit ORDER BY rowid"));
    }

    private static QueryOutput RunManaged(IReadOnlyList<string> statements, string query)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        foreach (var statement in statements)
            Execute(connection, statement);
        return QueryManaged(connection, query);
    }

    private static QueryOutput RunSqlite(IReadOnlyList<string> statements, string query)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var statement in statements)
            Execute(connection, statement);
        return QuerySqlite(connection, query);
    }

    private static QueryOutput QueryManaged(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var columns = Enumerable.Range(0, statement.GetColumnCount())
            .Select(statement.GetColumnName)
            .ToArray();
        var rows = new List<string>();
        while (statement.Step() == StatementStepResult.Row)
        {
            rows.Add(string.Join(
                '\u001f',
                Enumerable.Range(0, statement.GetColumnCount())
                    .Select(index => Format(statement.GetValue(index)))));
        }

        return new QueryOutput(columns, rows);
    }

    private static QueryOutput QuerySqlite(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(string.Join(
                '\u001f',
                Enumerable.Range(0, reader.FieldCount)
                    .Select(index => Format(reader.IsDBNull(index) ? null : reader.GetValue(index)))));
        }

        return new QueryOutput(columns, rows);
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

    private static string Format(SqlValue value)
        => value.Kind switch
        {
            SqlValueKind.Null => "N:",
            SqlValueKind.Integer => "I:" + value.AsInteger().ToString(CultureInfo.InvariantCulture),
            SqlValueKind.Real => "R:" + value.AsReal().ToString("R", CultureInfo.InvariantCulture),
            SqlValueKind.Text => "T:" + value.AsText(),
            SqlValueKind.Blob => "B:" + Convert.ToHexString(value.AsBlob().Span),
            _ => throw new InvalidOperationException($"Unknown SQL value kind {value.Kind}."),
        };

    private static string Format(object? value)
        => value switch
        {
            null => "N:",
            long integer => "I:" + integer.ToString(CultureInfo.InvariantCulture),
            double real => "R:" + real.ToString("R", CultureInfo.InvariantCulture),
            string text => "T:" + text,
            byte[] blob => "B:" + Convert.ToHexString(blob),
            _ => throw new InvalidOperationException($"Unknown SQLite value type {value.GetType().Name}."),
        };

    public sealed record DifferentialCase(string[] Statements, string Query);

    public sealed record RejectionDifferentialCase(
        string[] Setup,
        string Statement,
        string ErrorFragment,
        string Query);

    private sealed record QueryOutput(string[] Columns, IReadOnlyList<string> Rows);
}
