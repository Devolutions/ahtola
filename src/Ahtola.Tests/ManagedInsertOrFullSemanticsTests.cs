using System.Globalization;
using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite;
using ManagedSqliteConnection = Ahtola.Data.Sqlite.SqliteConnection;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedInsertOrFullSemanticsTests
{
    private static readonly string[] ConflictAlgorithms =
        ["ROLLBACK", "ABORT", "FAIL", "IGNORE", "REPLACE"];

    private static IEnumerable<TestCaseData> ConstraintAlgorithmCases()
    {
        foreach (var algorithm in ConflictAlgorithms)
        {
            yield return new TestCaseData(algorithm, "PRIMARY KEY")
                .SetName($"PrimaryKey_{algorithm}");
            yield return new TestCaseData(algorithm, "NOT NULL")
                .SetName($"NotNull_{algorithm}");
            yield return new TestCaseData(algorithm, "CHECK")
                .SetName($"Check_{algorithm}");
        }
    }

    [TestCaseSource(nameof(ConflictAlgorithms))]
    public void RowTriggersAndConflictPublicationMatchSqlite(string algorithm)
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(
            managed,
            sqlite,
            """
            CREATE TABLE items(id INTEGER PRIMARY KEY, value TEXT UNIQUE);
            CREATE TABLE audit(event TEXT);
            CREATE TRIGGER items_before BEFORE INSERT ON items
            BEGIN
                INSERT INTO audit VALUES(
                    'before:' || NEW.id || ':' || NEW.value || ':' || last_insert_rowid()
                );
            END;
            CREATE TRIGGER items_after AFTER INSERT ON items
            BEGIN
                INSERT INTO audit VALUES(
                    'after:' || NEW.id || ':' || NEW.value || ':' || last_insert_rowid()
                );
            END;
            INSERT INTO items VALUES(1, 'seed');
            DELETE FROM audit;
            BEGIN;
            """);

        AssertSameOutcome(
            managed,
            sqlite,
            $"INSERT OR {algorithm} INTO items VALUES(2, 'two'), (3, 'seed'), (4, 'four') "
            + "RETURNING id, value, last_insert_rowid()");
        AssertQueriesMatch(
            managed,
            sqlite,
            """
            SELECT 'item', id, value FROM items
            UNION ALL
            SELECT 'audit', rowid, event FROM audit
            ORDER BY 1, 2
            """);
        AssertQueriesMatch(managed, sqlite, "SELECT last_insert_rowid()");
        AssertSameOutcome(managed, sqlite, "COMMIT");
    }

    [Test]
    public void BeforeTriggerSeesUnallocatedRowidAndIgnoredAttemptDoesNotBurnSequence()
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(
            managed,
            sqlite,
            """
            CREATE TABLE items(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                value TEXT
            );
            CREATE TABLE audit(rowid_seen INTEGER, value TEXT);
            CREATE TRIGGER items_before BEFORE INSERT ON items
            BEGIN
                INSERT INTO audit VALUES(NEW.rowid, NEW.value);
                SELECT CASE
                    WHEN NEW.value = 'skip' THEN RAISE(IGNORE)
                END;
            END;
            """);

        AssertSameOutcome(
            managed,
            sqlite,
            "INSERT INTO items(value) VALUES('skip'), ('kept') RETURNING id, value");
        AssertQueriesMatch(managed, sqlite, "SELECT rowid_seen, value FROM audit ORDER BY rowid");
        AssertQueriesMatch(managed, sqlite, "SELECT id, value FROM items");
        AssertQueriesMatch(managed, sqlite, "SELECT seq FROM sqlite_sequence WHERE name='items'");
    }

    [Test]
    public void RaiseInWhenAndParameterPreflightMatchTriggerRules()
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(
            managed,
            sqlite,
            """
            CREATE TABLE items(id INTEGER PRIMARY KEY);
            CREATE TRIGGER ignore_all BEFORE INSERT ON items
            WHEN RAISE(IGNORE)
            BEGIN
                SELECT 1;
            END;
            """);

        AssertSameOutcome(managed, sqlite, "INSERT INTO items VALUES(1)");
        AssertQueriesMatch(managed, sqlite, "SELECT count(*) FROM items");
        Action parameterizedTrigger = () => managed.Prepare(
            """
            CREATE TRIGGER invalid BEFORE INSERT ON items
            WHEN ?
            BEGIN
                SELECT 1;
            END
            """);
        parameterizedTrigger.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*Bind parameters are not supported in trigger bodies*");
    }

    [Test]
    public void TriggerScriptSplitterDoesNotTreatQuotedOnColumnAsKeyword()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        var statements = connection.PrepareScript(
            """
            CREATE TABLE items([on] INTEGER, value INTEGER);
            CREATE TRIGGER item_update AFTER UPDATE OF [on], value ON items
            BEGIN
                SELECT 1;
            END;
            INSERT INTO items VALUES(1, 2);
            """);

        try
        {
            statements.Should().HaveCount(3);
        }
        finally
        {
            foreach (var statement in statements)
                statement.Dispose();
        }
    }

    [Test]
    public void DeleteTriggersExposeOldRowsAndRunPerRow()
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(
            managed,
            sqlite,
            """
            CREATE TABLE items(id INTEGER PRIMARY KEY);
            CREATE TABLE audit(event TEXT);
            CREATE TRIGGER items_before BEFORE DELETE ON items
            BEGIN
                INSERT INTO audit VALUES('before:' || OLD.id);
                SELECT CASE WHEN OLD.id = 2 THEN RAISE(IGNORE) END;
            END;
            CREATE TRIGGER items_after AFTER DELETE ON items
            BEGIN
                INSERT INTO audit VALUES('after:' || OLD.id);
            END;
            INSERT INTO items VALUES(1), (2), (3);
            """);

        AssertSameOutcome(managed, sqlite, "DELETE FROM items RETURNING id");
        AssertQueriesMatch(managed, sqlite, "SELECT id FROM items ORDER BY id");
        AssertQueriesMatch(managed, sqlite, "SELECT event FROM audit ORDER BY rowid");
    }

    [Test]
    public void UpdateTriggersExposeRowsAndHonorUpdateOfColumns()
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(
            managed,
            sqlite,
            """
            CREATE TABLE items(id INTEGER PRIMARY KEY, name TEXT, other INTEGER);
            CREATE TABLE audit(event TEXT);
            CREATE TRIGGER items_before BEFORE UPDATE OF name ON items
            BEGIN
                INSERT INTO audit VALUES(
                    'before:' || OLD.id || ':' || OLD.name || ':' || NEW.name
                );
                SELECT CASE WHEN OLD.id = 2 THEN RAISE(IGNORE) END;
            END;
            CREATE TRIGGER items_after AFTER UPDATE OF name ON items
            BEGIN
                INSERT INTO audit VALUES(
                    'after:' || OLD.id || ':' || OLD.name || ':' || NEW.name
                );
            END;
            INSERT INTO items VALUES(1, 'one', 0), (2, 'two', 0);
            """);

        AssertSameOutcome(managed, sqlite, "UPDATE items SET other=1");
        AssertQueriesMatch(managed, sqlite, "SELECT count(*) FROM audit");
        AssertSameOutcome(
            managed,
            sqlite,
            "UPDATE items SET name=name || '-updated' RETURNING id, name");
        AssertQueriesMatch(managed, sqlite, "SELECT id, name, other FROM items ORDER BY id");
        AssertQueriesMatch(managed, sqlite, "SELECT event FROM audit ORDER BY rowid");
    }

    [Test]
    public void RaiseIgnoreInTriggerWhenSkipsOnlyItsRow()
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(
            managed,
            sqlite,
            """
            CREATE TABLE items(id INTEGER PRIMARY KEY, value INTEGER);
            CREATE TRIGGER skip_second BEFORE UPDATE ON items
            WHEN OLD.id = 2 AND RAISE(IGNORE)
            BEGIN
                SELECT 1;
            END;
            INSERT INTO items VALUES(1, 0), (2, 0), (3, 0);
            """);

        AssertSameOutcome(managed, sqlite, "UPDATE items SET value=1 RETURNING id, value");
        AssertQueriesMatch(managed, sqlite, "SELECT id, value FROM items ORDER BY id");
    }

    [TestCase("FAIL")]
    [TestCase("ROLLBACK")]
    public void UpdateRowTriggersPreserveSchemaConflictBoundaries(string policy)
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(
            managed,
            sqlite,
            $"""
             CREATE TABLE items(
                 id INTEGER PRIMARY KEY,
                 value TEXT UNIQUE ON CONFLICT {policy}
             );
             CREATE TABLE marker(value INTEGER);
             CREATE TABLE audit(event TEXT);
             CREATE TRIGGER item_update AFTER UPDATE ON items
             BEGIN
                 INSERT INTO audit VALUES(NEW.id || ':' || NEW.value);
             END;
             INSERT INTO items VALUES(1, 'a'), (2, 'b'), (3, 'c');
             BEGIN;
             INSERT INTO marker VALUES(1);
             """);

        AssertSameOutcome(
            managed,
            sqlite,
            """
            UPDATE items
            SET value=CASE id WHEN 1 THEN 'x' WHEN 2 THEN 'c' END
            WHERE id <= 2
            """);
        AssertQueriesMatch(managed, sqlite, "SELECT id, value FROM items ORDER BY id");
        AssertQueriesMatch(managed, sqlite, "SELECT event FROM audit ORDER BY rowid");
        AssertSameOutcome(managed, sqlite, "COMMIT");
        AssertQueriesMatch(managed, sqlite, "SELECT value FROM marker");
    }

    [TestCase("BEFORE", "ROLLBACK")]
    [TestCase("BEFORE", "ABORT")]
    [TestCase("BEFORE", "FAIL")]
    [TestCase("BEFORE", "IGNORE")]
    [TestCase("AFTER", "ROLLBACK")]
    [TestCase("AFTER", "ABORT")]
    [TestCase("AFTER", "FAIL")]
    [TestCase("AFTER", "IGNORE")]
    public void RaiseAlgorithmsMatchSqlite(string timing, string algorithm)
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        var message = algorithm == "IGNORE" ? string.Empty : ", 'blocked row'";
        ExecuteBoth(
            managed,
            sqlite,
            $"""
             CREATE TABLE items(id INTEGER PRIMARY KEY);
             CREATE TRIGGER items_guard {timing} INSERT ON items
             WHEN NEW.id = 3
             BEGIN
                 SELECT RAISE({algorithm}{message});
             END;
             BEGIN;
             """);

        AssertSameOutcome(
            managed,
            sqlite,
            "INSERT INTO items VALUES(1), (2), (3), (4) RETURNING id");
        AssertQueriesMatch(managed, sqlite, "SELECT id FROM items ORDER BY id");
        AssertQueriesMatch(managed, sqlite, "SELECT last_insert_rowid()");
        AssertSameOutcome(managed, sqlite, "COMMIT");
    }

    [Test]
    public void RaiseFailDuringTriggerRowConstructionPreservesItsPrefix()
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(
            managed,
            sqlite,
            """
            CREATE TABLE base(id INTEGER PRIMARY KEY);
            CREATE TABLE audit(event TEXT);
            CREATE TRIGGER base_insert BEFORE INSERT ON base
            BEGIN
                INSERT OR ABORT INTO audit VALUES('first'), (RAISE(FAIL, 'stop'));
            END;
            """);

        AssertSameOutcome(managed, sqlite, "INSERT INTO base VALUES(1)");
        AssertQueriesMatch(managed, sqlite, "SELECT event FROM audit ORDER BY rowid");
        AssertQueriesMatch(managed, sqlite, "SELECT count(*) FROM base");
    }

    [Test]
    public void InsteadOfInsertPropagatesOuterPolicyAndReturningMatchesSqlite()
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(
            managed,
            sqlite,
            """
            CREATE TABLE items(id INTEGER PRIMARY KEY, value TEXT);
            CREATE TABLE audit(event TEXT);
            CREATE VIEW item_view AS SELECT id, value FROM items;
            CREATE TRIGGER item_view_insert INSTEAD OF INSERT ON item_view
            BEGIN
                INSERT OR FAIL INTO audit VALUES('attempt:' || NEW.id);
                INSERT OR FAIL INTO items VALUES(NEW.id, NEW.value);
            END;
            """);

        AssertSameOutcome(
            managed,
            sqlite,
            """
            INSERT OR IGNORE INTO item_view VALUES(1, 'first'), (1, 'ignored'), (2, 'second')
            RETURNING id, value
            """);
        AssertQueriesMatch(managed, sqlite, "SELECT id, value FROM items ORDER BY id");
        AssertQueriesMatch(managed, sqlite, "SELECT event FROM audit ORDER BY rowid");
        AssertQueriesMatch(managed, sqlite, "SELECT last_insert_rowid()");
    }

    [Test]
    public void InsteadOfTriggerDefersImmediateForeignKeysUntilTheOuterStatementEnds()
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(
            managed,
            sqlite,
            """
            PRAGMA foreign_keys=ON;
            CREATE TABLE parent(id INTEGER PRIMARY KEY);
            CREATE TABLE child(parent_id INTEGER REFERENCES parent(id));
            CREATE VIEW ingress AS SELECT id FROM parent;
            CREATE TRIGGER ingress_insert INSTEAD OF INSERT ON ingress
            BEGIN
                INSERT INTO child VALUES(NEW.id);
                INSERT INTO parent VALUES(NEW.id);
            END;
            """);

        AssertSameOutcome(managed, sqlite, "INSERT INTO ingress VALUES(1)");
        AssertQueriesMatch(managed, sqlite, "SELECT parent_id FROM child");
        AssertQueriesMatch(managed, sqlite, "SELECT id FROM parent");
    }

    [Test]
    public void ReturningObservesEachSuccessfulInsertRowid()
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(
            managed,
            sqlite,
            """
            CREATE TABLE items(id INTEGER PRIMARY KEY, value TEXT UNIQUE);
            INSERT INTO items VALUES(1, 'seed');
            """);

        AssertSameOutcome(
            managed,
            sqlite,
            """
            INSERT OR IGNORE INTO items VALUES(2, 'two'), (3, 'seed'), (4, 'four')
            RETURNING items.id, last_insert_rowid()
            """);
        AssertQueriesMatch(managed, sqlite, "SELECT last_insert_rowid()");
    }

    [Test]
    public void OrdinaryInsertReturningObservesEachInsertedRowid()
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(
            managed,
            sqlite,
            """
            CREATE TABLE items(id INTEGER PRIMARY KEY, value TEXT);
            CREATE TABLE auto_items(id INTEGER PRIMARY KEY AUTOINCREMENT);
            INSERT INTO items VALUES(10, 'seed');
            """);

        AssertSameOutcome(
            managed,
            sqlite,
            """
            INSERT INTO items VALUES(20, 'two'), (30, 'three')
            RETURNING id, last_insert_rowid()
            """);
        AssertSameOutcome(
            managed,
            sqlite,
            "INSERT INTO auto_items DEFAULT VALUES RETURNING id, last_insert_rowid()");
        AssertQueriesMatch(managed, sqlite, "SELECT last_insert_rowid()");
    }

    [TestCaseSource(nameof(ConstraintAlgorithmCases))]
    public void ConstraintAlgorithmsMatchSqlite(string algorithm, string constraint)
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(
            managed,
            sqlite,
            """
            CREATE TABLE items(
                id INTEGER PRIMARY KEY,
                required TEXT NOT NULL DEFAULT 'fallback',
                score INTEGER CHECK(score > 0)
            );
            CREATE TABLE audit(event TEXT);
            CREATE TRIGGER items_before BEFORE INSERT ON items
            BEGIN
                INSERT INTO audit VALUES(
                    'before:' || NEW.id || ':' || coalesce(NEW.required, 'NULL') || ':' || NEW.score
                );
            END;
            CREATE TRIGGER items_after AFTER INSERT ON items
            BEGIN
                INSERT INTO audit VALUES('after:' || NEW.id);
            END;
            INSERT INTO items VALUES(1, 'seed', 1);
            DELETE FROM audit;
            """);
        var conflictingRow = constraint switch
        {
            "PRIMARY KEY" => "(1, 'duplicate', 3)",
            "NOT NULL" => "(3, NULL, 3)",
            "CHECK" => "(3, 'invalid', -1)",
            _ => throw new InvalidOperationException($"Unknown constraint {constraint}."),
        };

        AssertSameOutcome(
            managed,
            sqlite,
            $"INSERT OR {algorithm} INTO items VALUES"
            + $"(2, 'two', 2), {conflictingRow}, (4, 'four', 4) "
            + "RETURNING id, required, score");
        AssertQueriesMatch(
            managed,
            sqlite,
            "SELECT id, required, score FROM items ORDER BY id");
        AssertQueriesMatch(managed, sqlite, "SELECT event FROM audit ORDER BY rowid");
        AssertQueriesMatch(managed, sqlite, "SELECT last_insert_rowid()");
    }

    [TestCase("FAIL")]
    [TestCase("REPLACE")]
    public void ConstraintLevelPoliciesPreserveRowTriggerSemantics(string policy)
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(
            managed,
            sqlite,
            $"""
             CREATE TABLE items(
                 id INTEGER PRIMARY KEY,
                 value TEXT UNIQUE ON CONFLICT {policy}
             );
             CREATE TABLE audit(event TEXT);
             CREATE TRIGGER items_before BEFORE INSERT ON items
             BEGIN
                 INSERT INTO audit VALUES('before:' || NEW.id);
             END;
             CREATE TRIGGER items_after AFTER INSERT ON items
             BEGIN
                 INSERT INTO audit VALUES('after:' || NEW.id);
             END;
             CREATE TRIGGER items_deleted_before BEFORE DELETE ON items
             BEGIN
                 INSERT INTO audit VALUES('delete-before:' || OLD.id);
             END;
             CREATE TRIGGER items_deleted_after AFTER DELETE ON items
             BEGIN
                 INSERT INTO audit VALUES('delete-after:' || OLD.id);
             END;
             PRAGMA recursive_triggers=ON;
             INSERT INTO items VALUES(1, 'seed');
             DELETE FROM audit;
             """);

        AssertSameOutcome(
            managed,
            sqlite,
            "INSERT INTO items VALUES(2, 'two'), (3, 'seed'), (4, 'four')");
        AssertQueriesMatch(
            managed,
            sqlite,
            """
            SELECT 'item', id, value FROM items
            UNION ALL
            SELECT 'audit', rowid, event FROM audit
            ORDER BY 1, 2
            """);
        AssertQueriesMatch(managed, sqlite, "SELECT last_insert_rowid()");
    }

    [Test]
    public void ReplaceMultipleConflictDeleteAndInsertOrderMatchesSqlite()
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(
            managed,
            sqlite,
            """
            CREATE TABLE items(
                id INTEGER PRIMARY KEY,
                left_value TEXT UNIQUE,
                right_value TEXT UNIQUE
            );
            CREATE TABLE audit(event TEXT);
            CREATE TRIGGER items_before_delete BEFORE DELETE ON items
            BEGIN
                INSERT INTO audit VALUES('before-delete:' || OLD.id);
            END;
            CREATE TRIGGER items_after_delete AFTER DELETE ON items
            BEGIN
                INSERT INTO audit VALUES('after-delete:' || OLD.id);
            END;
            CREATE TRIGGER items_before_insert BEFORE INSERT ON items
            BEGIN
                INSERT INTO audit VALUES('before-insert:' || NEW.id);
            END;
            CREATE TRIGGER items_after_insert AFTER INSERT ON items
            BEGIN
                INSERT INTO audit VALUES('after-insert:' || NEW.id);
            END;
            PRAGMA recursive_triggers=ON;
            INSERT INTO items VALUES(1, 'left-1', 'right-1'), (2, 'left-2', 'right-2');
            DELETE FROM audit;
            """);

        AssertSameOutcome(
            managed,
            sqlite,
            "INSERT OR REPLACE INTO items VALUES(3, 'left-1', 'right-2') RETURNING *");
        AssertQueriesMatch(managed, sqlite, "SELECT event FROM audit ORDER BY rowid");
        AssertQueriesMatch(managed, sqlite, "SELECT * FROM items ORDER BY id");
    }

    [Test]
    public void WithoutRowidReplacementDeletesPrimaryKeyConflictFirst()
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(
            managed,
            sqlite,
            """
            CREATE TABLE items(
                id INTEGER PRIMARY KEY,
                left_value TEXT UNIQUE,
                right_value TEXT UNIQUE
            ) WITHOUT ROWID;
            CREATE TABLE audit(id INTEGER);
            CREATE TRIGGER items_deleted BEFORE DELETE ON items
            BEGIN
                INSERT INTO audit VALUES(OLD.id);
            END;
            PRAGMA recursive_triggers=ON;
            INSERT INTO items VALUES
                (1, 'left-1', 'right-1'),
                (2, 'left-2', 'right-2'),
                (3, 'left-3', 'right-3');
            """);

        AssertSameOutcome(
            managed,
            sqlite,
            "INSERT OR REPLACE INTO items VALUES(1, 'left-2', 'right-3')");
        AssertQueriesMatch(managed, sqlite, "SELECT id FROM audit ORDER BY rowid");
        AssertQueriesMatch(managed, sqlite, "SELECT * FROM items ORDER BY id");
    }

    [Test]
    public void RaiseIgnoreBeforeReplacementDeleteLeavesTheConflictUnresolved()
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(
            managed,
            sqlite,
            """
            CREATE TABLE items(id INTEGER PRIMARY KEY, value TEXT UNIQUE);
            CREATE TRIGGER preserve_item BEFORE DELETE ON items
            BEGIN
                SELECT RAISE(IGNORE);
            END;
            PRAGMA recursive_triggers=ON;
            INSERT INTO items VALUES(1, 'seed');
            """);

        AssertSameOutcome(
            managed,
            sqlite,
            "INSERT OR REPLACE INTO items VALUES(2, 'seed')");
        AssertQueriesMatch(managed, sqlite, "SELECT id, value FROM items");
    }

    [Test]
    public void StandaloneReplaceAliasMatchesInsertOrReplace()
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(
            managed,
            sqlite,
            """
            CREATE TABLE items(id INTEGER PRIMARY KEY, value TEXT UNIQUE);
            INSERT INTO items VALUES(1, 'seed');
            """);

        AssertSameOutcome(
            managed,
            sqlite,
            "REPLACE INTO items VALUES(2, 'seed') RETURNING id, value, last_insert_rowid()");
        AssertQueriesMatch(managed, sqlite, "SELECT id, value FROM items");
    }

    [Test]
    public void DefaultGeneratedAndRowValueConstraintsMatchSqlite()
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(
            managed,
            sqlite,
            """
            CREATE TABLE items(
                id INTEGER PRIMARY KEY,
                left_value INTEGER DEFAULT 1,
                right_value INTEGER DEFAULT 2,
                pair TEXT AS (left_value || ':' || right_value) VIRTUAL UNIQUE,
                CHECK ((left_value, right_value) <> (0, 0))
            );
            INSERT INTO items DEFAULT VALUES;
            """);

        AssertSameOutcome(
            managed,
            sqlite,
            "INSERT OR REPLACE INTO items DEFAULT VALUES RETURNING id, left_value, right_value, pair");
        AssertSameOutcome(
            managed,
            sqlite,
            """
            INSERT OR IGNORE INTO items(id, left_value, right_value)
            VALUES(3, 0, 0), (4, 4, 5)
            RETURNING id, pair
            """);
        AssertQueriesMatch(
            managed,
            sqlite,
            "SELECT id, left_value, right_value, pair FROM items ORDER BY id");
        AssertQueriesMatch(managed, sqlite, "SELECT last_insert_rowid()");
    }

    [Test]
    public void TablePrimaryKeyUpsertTargetIsNotDoubleCounted()
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(
            managed,
            sqlite,
            """
            CREATE TABLE items(
                tenant TEXT,
                id INTEGER,
                value TEXT,
                PRIMARY KEY(tenant, id)
            );
            INSERT INTO items VALUES('a', 1, 'before');
            """);

        AssertSameOutcome(
            managed,
            sqlite,
            """
            INSERT INTO items VALUES('a', 1, 'after')
            ON CONFLICT(tenant, id) DO UPDATE SET value=excluded.value
            RETURNING tenant, id, value
            """);
        AssertQueriesMatch(managed, sqlite, "SELECT tenant, id, value FROM items");
    }

    [Test]
    public void UpsertTriggersRunPerMutationWithOldAndNewRows()
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(
            managed,
            sqlite,
            """
            CREATE TABLE items(id INTEGER PRIMARY KEY, value INTEGER);
            CREATE TABLE audit(event TEXT);
            CREATE TRIGGER before_insert BEFORE INSERT ON items
            BEGIN
                INSERT INTO audit VALUES('before-insert:' || NEW.id || ':' || NEW.value);
            END;
            CREATE TRIGGER after_insert AFTER INSERT ON items
            BEGIN
                INSERT INTO audit VALUES('after-insert:' || NEW.id || ':' || NEW.value);
            END;
            CREATE TRIGGER before_update BEFORE UPDATE OF value ON items
            BEGIN
                INSERT INTO audit VALUES(
                    'before-update:' || OLD.id || ':' || OLD.value || ':' || NEW.value
                );
            END;
            CREATE TRIGGER after_update AFTER UPDATE OF value ON items
            BEGIN
                INSERT INTO audit VALUES(
                    'after-update:' || OLD.id || ':' || OLD.value || ':' || NEW.value
                );
            END;
            INSERT INTO items VALUES(1, 10);
            DELETE FROM audit;
            """);

        AssertSameOutcome(
            managed,
            sqlite,
            """
            INSERT INTO items VALUES(1, 11), (2, 20), (1, 12)
            ON CONFLICT(id) DO UPDATE SET value=excluded.value
            RETURNING id, value, last_insert_rowid()
            """);
        AssertQueriesMatch(managed, sqlite, "SELECT id, value FROM items ORDER BY id");
        AssertQueriesMatch(managed, sqlite, "SELECT event FROM audit ORDER BY rowid");
    }

    [TestCaseSource(nameof(ConflictAlgorithms))]
    public void InsertOrAlgorithmsComposeWithUpsertTargets(string algorithm)
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(
            managed,
            sqlite,
            """
            CREATE TABLE items(
                id INTEGER PRIMARY KEY,
                code TEXT UNIQUE,
                value INTEGER
            );
            INSERT INTO items VALUES(1, 'seed', 1);
            """);

        AssertSameOutcome(
            managed,
            sqlite,
            $"""
             INSERT OR {algorithm} INTO items VALUES
                 (1, 'seed', 10),
                 (2, 'seed', 20),
                 (3, 'third', 30)
             ON CONFLICT(id) DO UPDATE SET value=excluded.value
             RETURNING id, code, value
             """);
        AssertQueriesMatch(managed, sqlite, "SELECT id, code, value FROM items ORDER BY id");
        AssertQueriesMatch(managed, sqlite, "SELECT last_insert_rowid()");
    }

    [Test]
    public void UpsertValidatesCandidateNotNullBeforeConflictAction()
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(
            managed,
            sqlite,
            """
            CREATE TABLE items(
                id INTEGER PRIMARY KEY,
                required TEXT NOT NULL,
                value INTEGER
            );
            INSERT INTO items VALUES(1, 'present', 0);
            """);

        AssertSameOutcome(
            managed,
            sqlite,
            """
            INSERT INTO items VALUES(1, NULL, 5)
            ON CONFLICT(id) DO UPDATE SET value=excluded.value
            """);
        AssertQueriesMatch(managed, sqlite, "SELECT id, required, value FROM items");
    }

    [Test]
    public void UpsertAbortRetainsLastSuccessfulAttemptedRowid()
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(
            managed,
            sqlite,
            """
            CREATE TABLE items(id INTEGER PRIMARY KEY, code TEXT UNIQUE);
            INSERT INTO items VALUES(1, 'seed');
            """);

        AssertSameOutcome(
            managed,
            sqlite,
            """
            INSERT INTO items VALUES(2, 'two'), (3, 'seed')
            ON CONFLICT(id) DO UPDATE SET code=excluded.code
            """);
        AssertQueriesMatch(managed, sqlite, "SELECT id, code FROM items ORDER BY id");
        AssertQueriesMatch(managed, sqlite, "SELECT last_insert_rowid()");
    }

    [TestCaseSource(nameof(ConflictAlgorithms))]
    public void CteGeneratedPartialExpressionAndWithoutRowidConflictsMatchSqlite(string algorithm)
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(
            managed,
            sqlite,
            """
            CREATE TABLE items(
                tenant TEXT,
                id INTEGER,
                value TEXT,
                normalized TEXT AS (lower(value)) VIRTUAL,
                active INTEGER,
                PRIMARY KEY(tenant, id)
            ) WITHOUT ROWID;
            CREATE UNIQUE INDEX active_value
                ON items(tenant, (normalized || ':' || active))
                WHERE active = 1;
            INSERT INTO items(tenant, id, value, active) VALUES('a', 1, 'seed', 1);
            """);

        AssertSameOutcome(
            managed,
            sqlite,
            """
            WITH source(tenant, id, value, active) AS (
                VALUES('a', 3, 'third', 1), ('a', 2, 'SEED', 1), ('a', 4, 'fourth', 1)
            )
            INSERT OR {{algorithm}} INTO items(tenant, id, value, active)
            SELECT tenant, id, value, active FROM source
            RETURNING tenant, id, normalized
            """.Replace("{{algorithm}}", algorithm, StringComparison.Ordinal));
        AssertQueriesMatch(
            managed,
            sqlite,
            "SELECT tenant, id, value, normalized, active FROM items ORDER BY tenant, id");
        AssertQueriesMatch(managed, sqlite, "SELECT last_insert_rowid()");
    }

    [TestCaseSource(nameof(ConflictAlgorithms))]
    public void StrictDatatypeFailureKeepsTheSamePrefixAsSqlite(string algorithm)
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(
            managed,
            sqlite,
            """
            CREATE TABLE items(id INTEGER PRIMARY KEY, value INT) STRICT;
            INSERT INTO items VALUES(1, 1);
            """);

        AssertSameOutcome(
            managed,
            sqlite,
            $"INSERT OR {algorithm} INTO items VALUES(2, 2), (3, 'invalid'), (4, 4)");
        AssertQueriesMatch(managed, sqlite, "SELECT id, value FROM items ORDER BY id");
        AssertQueriesMatch(managed, sqlite, "SELECT last_insert_rowid()");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ConflictPrefixAndReplacementSurviveJournalReopen(bool deleteJournal)
    {
        var fileSystem = new InMemoryFileSystem();
        var path = deleteJournal ? "insert-or-delete.db" : "insert-or-wal.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, value TEXT UNIQUE)");
            Execute(connection, "INSERT INTO items VALUES(1, 'seed')");
            if (deleteJournal)
                Execute(connection, "PRAGMA journal_mode=DELETE");

            Assert.Throws<EmbeddedSqlException>(
                () => Execute(
                    connection,
                    "INSERT OR FAIL INTO items VALUES(2, 'two'), (3, 'seed'), (4, 'four')"));
            Execute(connection, "INSERT OR REPLACE INTO items VALUES(5, 'seed')");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadRows(reopenedConnection, "SELECT id, value FROM items ORDER BY id")
            .Should().Equal(
                "I:2\u001fT:two",
                "I:5\u001fT:seed");
    }

    [TestCase(false, "IGNORE")]
    [TestCase(false, "FAIL")]
    [TestCase(false, "REPLACE")]
    [TestCase(true, "IGNORE")]
    [TestCase(true, "FAIL")]
    [TestCase(true, "REPLACE")]
    public void FailedConflictCommitRecoversThePreStatementImage(
        bool deleteJournal,
        string algorithm)
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        var path = $"insert-or-fault-{deleteJournal}-{algorithm}.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, value TEXT UNIQUE)");
            Execute(connection, "INSERT INTO items VALUES(1, 'seed')");
            if (deleteJournal)
                Execute(connection, "PRAGMA journal_mode=DELETE");

            faults.FailNext(FileSystemOperation.Write);
            Assert.Throws<IOException>(
                () => Execute(
                    connection,
                    $"INSERT OR {algorithm} INTO items VALUES(2, 'two'), (3, 'seed')"));
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadRows(reopenedConnection, "SELECT id, value FROM items ORDER BY id")
            .Should().Equal("I:1\u001fT:seed");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RaiseFailPrefixSurvivesTriggerReopen(bool deleteJournal)
    {
        var fileSystem = new InMemoryFileSystem();
        var path = deleteJournal ? "raise-fail-delete.db" : "raise-fail-wal.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY)");
            Execute(
                connection,
                """
                CREATE TRIGGER items_guard BEFORE INSERT ON items
                WHEN NEW.id = 2
                BEGIN
                    SELECT RAISE(FAIL, 'blocked');
                END
                """);
            if (deleteJournal)
                Execute(connection, "PRAGMA journal_mode=DELETE");
        }

        using (var reopened = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = reopened.Connect())
        {
            Assert.Throws<EmbeddedSqlException>(
                () => Execute(connection, "INSERT INTO items VALUES(1), (2), (3)"));
        }

        using var recovered = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var recoveredConnection = recovered.Connect();
        ReadRows(recoveredConnection, "SELECT id FROM items ORDER BY id")
            .Should().Equal("I:1");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void InsteadOfTriggerSurvivesReopen(bool deleteJournal)
    {
        var fileSystem = new InMemoryFileSystem();
        var path = deleteJournal ? "instead-delete.db" : "instead-wal.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, value TEXT)");
            Execute(connection, "CREATE VIEW item_view AS SELECT id, value FROM items");
            Execute(
                connection,
                """
                CREATE TRIGGER item_view_insert INSTEAD OF INSERT ON item_view
                BEGIN
                    INSERT INTO items VALUES(NEW.id, NEW.value);
                END
                """);
            if (deleteJournal)
                Execute(connection, "PRAGMA journal_mode=DELETE");
        }

        using (var reopened = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = reopened.Connect())
        {
            Execute(
                connection,
                "INSERT OR IGNORE INTO item_view VALUES(1, 'first'), (1, 'ignored'), (2, 'second')");
        }

        using var recovered = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var recoveredConnection = recovered.Connect();
        ReadRows(recoveredConnection, "SELECT id, value FROM items ORDER BY id")
            .Should().Equal("I:1\u001fT:first", "I:2\u001fT:second");
    }

    [Test]
    public void ListValuedTriggerWhenSurvivesReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "trigger-when-list.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY)");
            Execute(connection, "CREATE TABLE audit(id INTEGER)");
            Execute(
                connection,
                """
                CREATE TRIGGER selected_items AFTER INSERT ON items
                WHEN NEW.id IN (1, 2)
                BEGIN
                    INSERT INTO audit VALUES(NEW.id);
                END
                """);
        }

        using (var reopened = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = reopened.Connect())
        {
            Execute(connection, "INSERT INTO items VALUES(1), (3)");
        }

        using var recovered = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var recoveredConnection = recovered.Connect();
        ReadRows(recoveredConnection, "SELECT id FROM audit")
            .Should().Equal("I:1");
    }

    [Test]
    public void BackupPreservesConflictPrefixReplacementAndSequenceState()
    {
        using var source = OpenManagedSqlite();
        using var destination = OpenManagedSqlite();
        source.ExecuteNonQuery(
            "CREATE TABLE items(id INTEGER PRIMARY KEY AUTOINCREMENT, value TEXT UNIQUE)");
        source.ExecuteNonQuery("INSERT INTO items(value) VALUES('seed')");
        Assert.Throws<Ahtola.Data.Sqlite.SqliteException>(
            () => source.ExecuteNonQuery(
                "INSERT OR FAIL INTO items(value) VALUES('first'), ('seed'), ('last')"));
        source.ExecuteNonQuery("INSERT OR REPLACE INTO items(id, value) VALUES(10, 'seed')");

        source.BackupDatabase(destination);
        destination.ExecuteNonQuery("INSERT INTO items(value) VALUES('after-backup')");

        destination.ExecuteScalar<string>(
                "SELECT group_concat(id || ':' || value, ',') FROM items ORDER BY id")
            .Should().Be("2:first,10:seed,11:after-backup");
        destination.ExecuteScalar<long>(
                "SELECT seq FROM sqlite_sequence WHERE name='items'")
            .Should().Be(11);
    }

    [Test]
    public void AttachedSecondWriteIsRejectedBeforeInsertOrCallbacks()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("insert-or-attach-main.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "ATTACH DATABASE 'insert-or-attach-aux.db' AS aux");
        Execute(connection, "CREATE TABLE main.items(id INTEGER PRIMARY KEY)");
        Execute(connection, "CREATE TABLE aux.items(id INTEGER PRIMARY KEY)");
        var callbackCount = 0;
        connection.RegisterScalarFunction(
            "should_not_run",
            0,
            _ =>
            {
                callbackCount++;
                return SqlValue.Integer(2);
            });

        Execute(connection, "BEGIN");
        Execute(connection, "INSERT OR REPLACE INTO main.items VALUES(1)");
        Action secondWrite = () => Execute(
            connection,
            "INSERT OR REPLACE INTO aux.items VALUES(should_not_run())");
        secondWrite.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*cannot modify more than one database*atomically*");
        callbackCount.Should().Be(0);
        Execute(connection, "ROLLBACK");
        ReadRows(connection, "SELECT count(*) FROM main.items").Should().Equal("I:0");
        ReadRows(connection, "SELECT count(*) FROM aux.items").Should().Equal("I:0");
    }

    [Test]
    public void InvalidReturningIsRejectedBeforeSourceCallbacksOrMutation()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY)");
        var callbackCount = 0;
        connection.RegisterScalarFunction(
            "source_callback",
            0,
            _ =>
            {
                callbackCount++;
                return SqlValue.Integer(1);
            });

        Action invalid = () => Execute(
            connection,
            "INSERT OR IGNORE INTO items SELECT source_callback() RETURNING missing");
        invalid.Should().Throw<EmbeddedSqlException>().WithMessage("no such column: missing");
        callbackCount.Should().Be(0);
        ReadRows(connection, "SELECT count(*) FROM items").Should().Equal("I:0");
    }

    [Test]
    public void InvalidUpdateAndDeleteReturningRestoreRowTriggerMutations()
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        ExecuteBoth(
            managed,
            sqlite,
            """
            CREATE TABLE items(id INTEGER PRIMARY KEY, value INTEGER);
            CREATE TABLE audit(event TEXT);
            CREATE TRIGGER item_update AFTER UPDATE ON items
            BEGIN
                INSERT INTO audit VALUES('update:' || NEW.id);
            END;
            CREATE TRIGGER item_delete AFTER DELETE ON items
            BEGIN
                INSERT INTO audit VALUES('delete:' || OLD.id);
            END;
            INSERT INTO items VALUES(1, 1), (2, 2);
            """);

        AssertSameOutcome(managed, sqlite, "UPDATE items SET value=value+1 RETURNING missing");
        AssertQueriesMatch(managed, sqlite, "SELECT id, value FROM items ORDER BY id");
        AssertQueriesMatch(managed, sqlite, "SELECT count(*) FROM audit");
        AssertSameOutcome(managed, sqlite, "DELETE FROM items RETURNING missing");
        AssertQueriesMatch(managed, sqlite, "SELECT id, value FROM items ORDER BY id");
        AssertQueriesMatch(managed, sqlite, "SELECT count(*) FROM audit");
    }

    private static void AssertSameOutcome(
        EmbeddedConnection managed,
        MsData.SqliteConnection sqlite,
        string sql)
    {
        var managedOutcome = Capture(() => ReadRows(managed, sql));
        var sqliteOutcome = Capture(() => ReadRows(sqlite, sql));
        (managedOutcome.Error is null).Should().Be(
            sqliteOutcome.Error is null,
            "managed error was {0} and SQLite error was {1}",
            managedOutcome.Error,
            sqliteOutcome.Error);
        if (managedOutcome.Error is null)
        {
            managedOutcome.Rows.Should().Equal(sqliteOutcome.Rows);
            return;
        }

        sqliteOutcome.Error.Should().Contain(managedOutcome.Error!);
    }

    private static Outcome Capture(Func<IReadOnlyList<string>> operation)
    {
        try
        {
            return new Outcome(operation(), null);
        }
        catch (Exception exception)
        {
            return new Outcome([], NormalizeError(exception.Message));
        }
    }

    private static string NormalizeError(string message)
    {
        var quote = message.IndexOf('\'');
        return quote >= 0 && message.EndsWith("'.", StringComparison.Ordinal)
            ? message[(quote + 1)..^2]
            : message;
    }

    private static void ExecuteBoth(
        EmbeddedConnection managed,
        MsData.SqliteConnection sqlite,
        string script)
    {
        foreach (var statement in managed.PrepareScript(script))
        {
            using (statement)
            {
                while (statement.Step() == StatementStepResult.Row)
                {
                }
            }
        }

        using var command = sqlite.CreateCommand();
        command.CommandText = script;
        command.ExecuteNonQuery();
    }

    private static void AssertQueriesMatch(
        EmbeddedConnection managed,
        MsData.SqliteConnection sqlite,
        string sql)
        => ReadRows(managed, sql).Should().Equal(ReadRows(sqlite, sql));

    private static IReadOnlyList<string> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<string>();
        while (statement.Step() == StatementStepResult.Row)
        {
            rows.Add(string.Join(
                '\u001f',
                Enumerable.Range(0, statement.GetColumnCount())
                    .Select(index => Format(statement.GetValue(index)))));
        }

        return rows;
    }

    private static IReadOnlyList<string> ReadRows(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(string.Join(
                '\u001f',
                Enumerable.Range(0, reader.FieldCount)
                    .Select(index => Format(reader.IsDBNull(index) ? null : reader.GetValue(index)))));
        }

        return rows;
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
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
            null or DBNull => "N:",
            byte[] blob => "B:" + Convert.ToHexString(blob),
            double real => "R:" + real.ToString("R", CultureInfo.InvariantCulture),
            float real => "R:" + real.ToString("R", CultureInfo.InvariantCulture),
            _ when value is sbyte or byte or short or ushort or int or uint or long
                => "I:" + Convert.ToInt64(value, CultureInfo.InvariantCulture)
                    .ToString(CultureInfo.InvariantCulture),
            _ => "T:" + Convert.ToString(value, CultureInfo.InvariantCulture),
        };

    private static MsData.SqliteConnection OpenSqlite()
    {
        var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        connection.ExecuteNonQuery("PRAGMA recursive_triggers=ON");
        return connection;
    }

    private static ManagedSqliteConnection OpenManagedSqlite()
    {
        var connection = new ManagedSqliteConnection(
            "Data Source=:memory:;Local Provider=Managed;Pooling=False");
        connection.Open();
        return connection;
    }

    private sealed record Outcome(IReadOnlyList<string> Rows, string? Error);
}

internal static class MicrosoftSqliteTestExtensions
{
    public static void ExecuteNonQuery(this MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
