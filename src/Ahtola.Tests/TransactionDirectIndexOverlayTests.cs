using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

/// <summary>
/// Focused coverage for the classic (non-MVCC) transaction direct-index-overlay phase: a
/// file-backed explicit transaction must serve eligible join index seeks directly from the pinned
/// durable pager snapshot taken at BEGIN, merged with this transaction's own
/// <c>TransactionMutationOverlay</c>, so every scenario below asserts both correctness (read your
/// own writes, suppress stale base entries, respect snapshot isolation against peers) and the
/// zero-materialization contract (<see cref="VdbeJoinIndexSeekMetrics.DurableCursorPlans"/> greater
/// than zero, <see cref="VdbeJoinIndexSeekMetrics.IndexRowsMaterialized"/> equal to zero).
/// </summary>
public sealed class TransactionDirectIndexOverlayTests
{
    [Test]
    public void PinnedSnapshotSurvivesALaterPeerCommitWhileTheOverlaySeesItsOwnWrites()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var setupDatabase = EmbeddedDatabase.OpenFile("overlay-peer-snapshot.db", fileSystem))
        using (var setupConnection = setupDatabase.Connect())
        {
            Execute(setupConnection, "CREATE TABLE outer_items(k INTEGER);");
            Execute(setupConnection, "CREATE TABLE inner_items(k INTEGER, payload TEXT);");
            Execute(setupConnection, "CREATE INDEX inner_items_k ON inner_items(k);");
            Execute(setupConnection, "INSERT INTO outer_items VALUES (2), (3);");
            Execute(setupConnection, "INSERT INTO inner_items VALUES (2, 'two');");
            Execute(setupConnection, "ANALYZE;");
        }

        using var databaseA = EmbeddedDatabase.OpenFile("overlay-peer-snapshot.db", fileSystem);
        using var connectionA = databaseA.Connect();
        // BEGIN pins the durable snapshot before this transaction takes any write lock, so a peer
        // can still commit its own write in between.
        Execute(connectionA, "BEGIN;");

        // A peer connection commits an unrelated row after A pinned its snapshot at BEGIN, while A
        // still holds no write lock of its own.
        using (var databaseB = EmbeddedDatabase.OpenFile("overlay-peer-snapshot.db", fileSystem))
        using (var connectionB = databaseB.Connect())
        {
            Execute(connectionB, "INSERT INTO inner_items VALUES (3, 'three-peer');");
        }

        // This transaction's own uncommitted write must be visible through the overlay, even
        // though the peer's commit above has already advanced the durable catalog underneath it.
        Execute(connectionA, "INSERT INTO inner_items VALUES (2, 'two-local');");

        const string sql =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_k ON outer_items.k = inner_items.k
            ORDER BY outer_items.k, inner_items.payload;
            """;

        databaseA.ResetJoinOrderDiagnostics();
        // Snapshot isolation: the peer's post-BEGIN commit (k=3) must stay invisible for the whole
        // transaction, while this transaction's own overlay row (k=2, 'two-local') is visible
        // alongside the row that was already committed when the snapshot was pinned.
        ReadRows(connectionA, sql).Select(row => row[0].AsText()).Should().Equal("two", "two-local");
        databaseA.JoinIndexSeekMetrics.DurableCursorPlans.Should().Be(1);
        databaseA.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);

        // This phase is classic (non-MVCC): a write transaction whose pinned snapshot has since
        // been superseded by a peer's commit cannot land its own write, so it must roll back
        // rather than silently rebase onto the newer snapshot.
        Execute(connectionA, "ROLLBACK;");

        using var connectionC = databaseA.Connect();
        ReadRows(connectionC, sql).Select(row => row[0].AsText())
            .Should().Equal("two", "three-peer");
    }

    [Test]
    public void UpdatedIndexedColumnSuppressesTheStaleKeyAndExposesTheNewKeyThroughTheOverlay()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("overlay-update-key.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_items(k INTEGER);");
        Execute(connection, "CREATE TABLE inner_items(id INTEGER PRIMARY KEY, k INTEGER, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_items_k ON inner_items(k);");
        Execute(connection, "INSERT INTO outer_items VALUES (1), (2);");
        Execute(connection, "INSERT INTO inner_items VALUES (1, 1, 'a');");
        Execute(connection, "ANALYZE;");

        Execute(connection, "BEGIN;");
        Execute(connection, "UPDATE inner_items SET k = 2 WHERE id = 1;");

        const string sql =
            """
            SELECT outer_items.k, inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_k ON outer_items.k = inner_items.k
            ORDER BY outer_items.k;
            """;
        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, sql).Select(row => (row[0].AsInteger(), row[1].AsText()))
            .Should().Equal((2L, "a"));
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().Be(1);
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);

        Execute(connection, "ROLLBACK;");
    }

    [Test]
    public void DeletedRowIsSuppressedFromTheDurableIndexSeekThroughTheOverlay()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("overlay-delete.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_items(k INTEGER);");
        Execute(connection, "CREATE TABLE inner_items(id INTEGER PRIMARY KEY, k INTEGER, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_items_k ON inner_items(k);");
        Execute(connection, "INSERT INTO outer_items VALUES (1);");
        Execute(connection, "INSERT INTO inner_items VALUES (1, 1, 'a');");
        Execute(connection, "ANALYZE;");

        Execute(connection, "BEGIN;");
        Execute(connection, "DELETE FROM inner_items WHERE id = 1;");

        const string sql =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_k ON outer_items.k = inner_items.k;
            """;
        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, sql).Should().BeEmpty();
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().Be(1);
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);

        Execute(connection, "ROLLBACK;");
    }

    [Test]
    public void RowidMoveIsVisibleThroughTheOverlayAndSuppressesTheOldIdentity()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("overlay-rowid-move.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_items(k INTEGER);");
        Execute(connection, "CREATE TABLE inner_items(id INTEGER PRIMARY KEY, k INTEGER, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_items_k ON inner_items(k);");
        Execute(connection, "INSERT INTO outer_items VALUES (7);");
        Execute(connection, "INSERT INTO inner_items VALUES (1, 7, 'a');");
        Execute(connection, "ANALYZE;");

        Execute(connection, "BEGIN;");
        // Changing the INTEGER PRIMARY KEY rowid alias moves the row's identity while its indexed
        // column (k) stays put -- the overlay must key the replacement by the *new* rowid.
        Execute(connection, "UPDATE inner_items SET id = 999 WHERE id = 1;");

        const string sql =
            """
            SELECT inner_items.id, inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_k ON outer_items.k = inner_items.k;
            """;
        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, sql).Select(row => (row[0].AsInteger(), row[1].AsText()))
            .Should().Equal((999L, "a"));
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().Be(1);
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);

        Execute(connection, "ROLLBACK;");
    }

    [Test]
    public void PartialIndexMembershipChangesAreReflectedThroughTheOverlay()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("overlay-partial-membership.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_items(k INTEGER);");
        Execute(connection, "CREATE TABLE inner_items(id INTEGER PRIMARY KEY, k INTEGER, gate INTEGER, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_items_partial ON inner_items(k) WHERE gate > 0;");
        Execute(connection, "INSERT INTO outer_items VALUES (1), (2);");
        // id=1 starts outside the partial predicate (gate <= 0); id=2 starts inside it.
        Execute(connection, "INSERT INTO inner_items VALUES (1, 1, -1, 'joins-in-txn');");
        Execute(connection, "INSERT INTO inner_items VALUES (2, 2, 1, 'leaves-in-txn');");
        Execute(connection, "ANALYZE;");

        Execute(connection, "BEGIN;");
        Execute(connection, "UPDATE inner_items SET gate = 1 WHERE id = 1;");
        Execute(connection, "UPDATE inner_items SET gate = -1 WHERE id = 2;");

        const string sql =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_partial
            ON outer_items.k = inner_items.k AND inner_items.gate > 0
            ORDER BY outer_items.k;
            """;
        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, sql).Select(row => row[0].AsText()).Should().Equal("joins-in-txn");
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().Be(1);
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);

        Execute(connection, "ROLLBACK;");
    }

    [Test]
    public void ExpressionIndexKeyChangesAreReflectedThroughTheOverlay()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("overlay-expression-key.db", fileSystem);
        using var connection = database.Connect();
        // The outer join column must stay untyped (no declared affinity) so the compiled
        // expression-index plan does not decline the seek for a possible affinity coercion --
        // see IndexSeekJoinDirectAccessTests.DurableReopenUsesPagerSeekForProvenExpressionIndex.
        Execute(connection, "CREATE TABLE outer_items(k);");
        Execute(connection, "CREATE TABLE inner_items(id INTEGER PRIMARY KEY, k TEXT, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_items_lower_k ON inner_items(lower(k));");
        Execute(connection, "INSERT INTO outer_items VALUES ('old'), ('new');");
        Execute(connection, "INSERT INTO inner_items VALUES (1, 'OLD', 'p-old');");
        Execute(
            connection,
            "INSERT INTO inner_items VALUES "
                + string.Join(", ", Enumerable.Range(2, 500).Select(value => $"({value}, 'noise{value}', 'n{value}')"))
                + ";");
        Execute(connection, "ANALYZE;");

        Execute(connection, "BEGIN;");
        Execute(connection, "UPDATE inner_items SET k = 'NEW' WHERE id = 1;");

        const string sql =
            """
            SELECT outer_items.k, inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_lower_k ON outer_items.k = lower(inner_items.k)
            ORDER BY outer_items.k;
            """;
        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, sql).Select(row => (row[0].AsText(), row[1].AsText()))
            .Should().Equal(("new", "p-old"));
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().Be(1);
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);

        Execute(connection, "ROLLBACK;");
    }

    [Test]
    public void WithoutRowidPrimaryKeyAndSecondaryIndexSeeksReflectOverlayMutations()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("overlay-without-rowid.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_codes(code TEXT);");
        Execute(connection, "CREATE TABLE outer_tags(tag TEXT);");
        Execute(connection, "CREATE TABLE entry(code TEXT PRIMARY KEY, tag TEXT) WITHOUT ROWID;");
        Execute(connection, "CREATE INDEX entry_tag ON entry(tag);");
        Execute(connection, "INSERT INTO outer_codes VALUES ('k1'), ('k2');");
        Execute(connection, "INSERT INTO outer_tags VALUES ('t1'), ('t9');");
        Execute(connection, "INSERT INTO entry VALUES ('k1', 't1');");
        // The planner only prefers the durable PK/secondary index seek over a small-table
        // automatic-covering-index materialization once the table has enough baseline rows for
        // ANALYZE to make that estimate; these noise rows never match outer_codes/outer_tags.
        Execute(
            connection,
            "INSERT INTO entry VALUES "
                + string.Join(", ", Enumerable.Range(1, 500).Select(value => $"('noise-{value:D5}', 'noise-tag-{value:D5}')"))
                + ";");
        Execute(connection, "ANALYZE;");

        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO entry VALUES ('k2', 't2');");
        Execute(connection, "UPDATE entry SET tag = 't9' WHERE code = 'k1';");

        const string primaryKeySql =
            """
            SELECT entry.code, entry.tag
            FROM outer_codes
            JOIN entry ON outer_codes.code = entry.code
            ORDER BY outer_codes.code;
            """;
        const string secondaryIndexSql =
            """
            SELECT entry.code
            FROM outer_tags
            JOIN entry INDEXED BY entry_tag ON outer_tags.tag = entry.tag
            ORDER BY outer_tags.tag;
            """;

        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, primaryKeySql).Select(row => (row[0].AsText(), row[1].AsText()))
            .Should().Equal(("k1", "t9"), ("k2", "t2"));
        ReadRows(connection, secondaryIndexSql).Select(row => row[0].AsText())
            .Should().Equal("k1");
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().Be(2);
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);

        Execute(connection, "ROLLBACK;");
    }

    [Test]
    public void RollbackToSavepointRestoresTheOverlayToItsCheckpointedState()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("overlay-savepoint-rollback.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_items(k INTEGER);");
        Execute(connection, "CREATE TABLE inner_items(k INTEGER, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_items_k ON inner_items(k);");
        Execute(connection, "INSERT INTO outer_items VALUES (10), (11);");
        // A non-empty baseline table is required for the planner to choose a compiled index-seek
        // plan instead of an empty-table scan shortcut; this row never matches outer_items.k.
        Execute(connection, "INSERT INTO inner_items VALUES (999, 'noise');");
        Execute(connection, "ANALYZE;");

        const string sql =
            """
            SELECT outer_items.k, inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_k ON outer_items.k = inner_items.k
            ORDER BY outer_items.k;
            """;

        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO inner_items VALUES (10, 'ten');");
        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, sql).Select(row => (row[0].AsInteger(), row[1].AsText()))
            .Should().Equal((10L, "ten"));

        Execute(connection, "SAVEPOINT sp1;");
        Execute(connection, "INSERT INTO inner_items VALUES (11, 'eleven');");
        Execute(connection, "DELETE FROM inner_items WHERE k = 10;");
        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, sql).Select(row => (row[0].AsInteger(), row[1].AsText()))
            .Should().Equal((11L, "eleven"));

        Execute(connection, "ROLLBACK TO sp1;");
        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, sql).Select(row => (row[0].AsInteger(), row[1].AsText()))
            .Should().Equal((10L, "ten"));
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().Be(1);
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);

        Execute(connection, "ROLLBACK;");
    }

    [Test]
    public void ReleasedSavepointKeepsItsOverlayMutationsVisibleThroughTheDurableSeek()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("overlay-savepoint-release.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_items(k INTEGER);");
        Execute(connection, "CREATE TABLE inner_items(k INTEGER, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_items_k ON inner_items(k);");
        Execute(connection, "INSERT INTO outer_items VALUES (20);");
        // A non-empty baseline table is required for the planner to choose a compiled index-seek
        // plan instead of an empty-table scan shortcut; this row never matches outer_items.k.
        Execute(connection, "INSERT INTO inner_items VALUES (999, 'noise');");
        Execute(connection, "ANALYZE;");

        Execute(connection, "BEGIN;");
        Execute(connection, "SAVEPOINT sp1;");
        Execute(connection, "INSERT INTO inner_items VALUES (20, 'twenty');");
        Execute(connection, "RELEASE sp1;");

        const string sql =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_k ON outer_items.k = inner_items.k;
            """;
        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, sql).Select(row => row[0].AsText()).Should().Equal("twenty");
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().Be(1);
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);

        Execute(connection, "COMMIT;");

        using var reopened = EmbeddedDatabase.OpenFile("overlay-savepoint-release.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadRows(reopenedConnection, sql).Select(row => row[0].AsText()).Should().Equal("twenty");
    }

    [Test]
    public void TriggerCascadedInsertIsVisibleThroughTheOverlayWithoutMaterializingTheIndex()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("overlay-trigger-cascade.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_items(k INTEGER);");
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY, k INTEGER);");
        Execute(connection, "CREATE TABLE order_log(k INTEGER, note TEXT);");
        Execute(connection, "CREATE INDEX order_log_k ON order_log(k);");
        Execute(
            connection,
            "CREATE TRIGGER orders_log AFTER INSERT ON orders "
                + "BEGIN INSERT INTO order_log VALUES (NEW.k, 'logged'); END;");
        Execute(connection, "INSERT INTO outer_items VALUES (77);");
        // A non-empty baseline table is required for the planner to choose a compiled index-seek
        // plan instead of an empty-table scan shortcut; this row never matches outer_items.k.
        Execute(connection, "INSERT INTO order_log VALUES (999, 'noise');");
        Execute(connection, "ANALYZE;");

        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO orders VALUES (1, 77);");

        const string sql =
            """
            SELECT order_log.note
            FROM outer_items
            JOIN order_log INDEXED BY order_log_k ON outer_items.k = order_log.k;
            """;
        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, sql).Select(row => row[0].AsText()).Should().Equal("logged");
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().Be(1);
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);

        Execute(connection, "ROLLBACK;");
    }

    [Test]
    public void ForeignKeyCascadeDeleteSuppressesDependentRowsThroughTheOverlay()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("overlay-fk-cascade.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "PRAGMA foreign_keys = ON;");
        Execute(connection, "CREATE TABLE outer_items(k INTEGER);");
        Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY);");
        Execute(
            connection,
            "CREATE TABLE child(id INTEGER PRIMARY KEY, "
                + "parent_id INTEGER REFERENCES parent(id) ON DELETE CASCADE, tag INTEGER);");
        Execute(connection, "CREATE INDEX child_tag ON child(tag);");
        Execute(connection, "INSERT INTO outer_items VALUES (55);");
        Execute(connection, "INSERT INTO parent VALUES (1);");
        Execute(connection, "INSERT INTO child VALUES (1, 1, 55);");
        Execute(connection, "ANALYZE;");

        Execute(connection, "BEGIN;");
        Execute(connection, "DELETE FROM parent WHERE id = 1;");

        const string sql =
            """
            SELECT child.id
            FROM outer_items
            JOIN child INDEXED BY child_tag ON outer_items.k = child.tag;
            """;
        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, sql).Should().BeEmpty();
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().Be(1);
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);

        Execute(connection, "ROLLBACK;");
    }

    [Test]
    public void RollbackDiscardsTheOverlaySoALaterTransactionOnTheSameConnectionStartsClean()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("overlay-rollback-cleanup.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_items(k INTEGER);");
        Execute(connection, "CREATE TABLE inner_items(k INTEGER, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_items_k ON inner_items(k);");
        Execute(connection, "INSERT INTO outer_items VALUES (30);");
        // A non-empty baseline table is required for the planner to choose a compiled index-seek
        // plan instead of an empty-table scan shortcut; this row never matches outer_items.k and
        // survives the ROLLBACK below since it was committed before BEGIN.
        Execute(connection, "INSERT INTO inner_items VALUES (999, 'noise');");
        Execute(connection, "ANALYZE;");

        const string sql =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_k ON outer_items.k = inner_items.k;
            """;

        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO inner_items VALUES (30, 'thirty');");
        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, sql).Select(row => row[0].AsText()).Should().Equal("thirty");
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().Be(1);
        Execute(connection, "ROLLBACK;");

        // A fresh explicit transaction on the very same connection must re-pin its own snapshot
        // and start with an empty overlay: no leaked state from the rolled-back transaction.
        Execute(connection, "BEGIN;");
        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, sql).Should().BeEmpty();
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().Be(1);
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);
        Execute(connection, "COMMIT;");
    }

    [Test]
    public void ConnectionDisposalDuringAnOpenTransactionReleasesThePinnedSnapshotWithoutCorruptingTheFile()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("overlay-connection-disposal.db", fileSystem))
        {
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, payload TEXT);");
                Execute(connection, "INSERT INTO items VALUES (1, 'committed');");
                Execute(connection, "BEGIN;");
                Execute(connection, "INSERT INTO items VALUES (2, 'uncommitted');");
                // Disposing the connection mid-transaction must dispose the pinned snapshot rather
                // than leak a reader that would wedge a later peer's checkpoint.
            }

            using var freshConnection = database.Connect();
            ReadRows(freshConnection, "SELECT payload FROM items ORDER BY id;")
                .Select(row => row[0].AsText())
                .Should().Equal("committed");
        }

        using var reopened = EmbeddedDatabase.OpenFile("overlay-connection-disposal.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadRows(reopenedConnection, "SELECT payload FROM items ORDER BY id;")
            .Select(row => row[0].AsText())
            .Should().Equal("committed");
    }

    [Test]
    public void DurableReopenAfterCommitSeesEveryOverlayMutationWithoutMaterialization()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("overlay-durable-reopen.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE outer_items(k INTEGER);");
            Execute(connection, "CREATE TABLE inner_items(id INTEGER PRIMARY KEY, k INTEGER, payload TEXT);");
            Execute(connection, "CREATE INDEX inner_items_k ON inner_items(k);");
            Execute(connection, "INSERT INTO outer_items VALUES (1), (2), (3);");
            Execute(connection, "INSERT INTO inner_items VALUES (1, 1, 'stays'), (2, 2, 'will-move'), (3, 3, 'will-delete');");
            Execute(connection, "ANALYZE;");

            Execute(connection, "BEGIN;");
            Execute(connection, "UPDATE inner_items SET k = 4 WHERE id = 2;");
            Execute(connection, "DELETE FROM inner_items WHERE id = 3;");
            Execute(connection, "INSERT INTO inner_items VALUES (4, 4, 'new-in-txn');");
            Execute(connection, "COMMIT;");
        }

        using var reopened = EmbeddedDatabase.OpenFile("overlay-durable-reopen.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        const string sql =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_k ON outer_items.k = inner_items.k
            ORDER BY inner_items.payload;
            """;
        reopened.ResetJoinOrderDiagnostics();
        ReadRows(reopenedConnection, sql).Select(row => row[0].AsText()).Should().Equal("stays");

        const string movedSql =
            """
            SELECT inner_items.payload
            FROM inner_items INDEXED BY inner_items_k
            WHERE inner_items.k = 4
            ORDER BY inner_items.payload;
            """;
        ReadRows(reopenedConnection, movedSql).Select(row => row[0].AsText())
            .Should().Equal("new-in-txn", "will-move");
        reopened.JoinIndexSeekMetrics.DurableCursorPlans.Should().Be(1);
        reopened.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);
    }

    // --- Finding #1: a failed AFTER-trigger statement must restore the overlay, not just the ---
    // --- statement's cloned catalog, so trigger-cascaded phantom rows never leak into a later ---
    // --- read on the still-open transaction. ---
    [Test]
    public void FailedAfterTriggerStatementRestoresTheOverlayAndSuppressesPhantomTriggerRowsButKeepsTheTransactionOpen()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("overlay-failed-after-trigger.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_items(k INTEGER);");
        Execute(connection, "CREATE TABLE source(id INTEGER PRIMARY KEY, k INTEGER, mode TEXT);");
        Execute(connection, "CREATE TABLE mirror(k INTEGER, note TEXT);");
        Execute(connection, "CREATE INDEX mirror_k ON mirror(k);");
        Execute(connection, "CREATE TABLE guard(id INTEGER PRIMARY KEY, k INTEGER, tag INTEGER);");
        Execute(connection, "CREATE INDEX guard_tag ON guard(tag);");
        Execute(
            connection,
            """
            CREATE TRIGGER source_after_insert AFTER INSERT ON source BEGIN
                INSERT INTO mirror VALUES (NEW.k, 'mirrored-' || NEW.id);
                DELETE FROM guard WHERE tag = NEW.k;
                SELECT CASE WHEN NEW.mode = 'abort' THEN RAISE(ABORT, 'guard tripped') END;
            END;
            """);
        Execute(connection, "INSERT INTO outer_items VALUES (77);");
        // A non-empty baseline table is required for the planner to choose a compiled index-seek
        // plan instead of an empty-table scan shortcut; these rows never match outer_items.k.
        Execute(connection, "INSERT INTO mirror VALUES (999, 'noise');");
        Execute(connection, "INSERT INTO guard VALUES (999, 999, 999);");
        Execute(connection, "ANALYZE;");

        const string mirrorSql =
            """
            SELECT mirror.note
            FROM outer_items
            JOIN mirror INDEXED BY mirror_k ON outer_items.k = mirror.k
            ORDER BY mirror.note;
            """;
        const string guardSql =
            """
            SELECT guard.id
            FROM outer_items
            JOIN guard INDEXED BY guard_tag ON outer_items.k = guard.tag;
            """;

        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO source VALUES (1, 77, 'ok');");
        Execute(connection, "INSERT INTO guard VALUES (2, 77, 77);");

        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, mirrorSql).Select(row => row[0].AsText()).Should().Equal("mirrored-1");
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().Be(1);
        ReadRows(connection, guardSql).Select(row => row[0].AsInteger()).Should().Equal(2L);

        var abortedInsert = () => Execute(connection, "INSERT INTO source VALUES (2, 77, 'abort');");
        abortedInsert.Should().Throw<EmbeddedSqlException>().WithMessage("*guard tripped*");

        // The failed AFTER-trigger statement must restore the overlay to exactly what it was
        // before this statement ran: the trigger's phantom mirror insert must vanish and the
        // trigger's phantom guard delete must be undone, even though both mutations came from a
        // trigger body rather than the top-level statement.
        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, mirrorSql).Select(row => row[0].AsText()).Should().Equal("mirrored-1");
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);
        ReadRows(connection, guardSql).Select(row => row[0].AsInteger()).Should().Equal(2L);

        // The transaction itself must still be open and usable after the failed statement: a
        // subsequent successful statement must see only its own effect layered on the restored
        // overlay, proving the restore did not discard unrelated prior mutations either.
        Execute(connection, "INSERT INTO source VALUES (3, 77, 'ok');");
        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, mirrorSql).Select(row => row[0].AsText()).Should().Equal("mirrored-1", "mirrored-3");
        ReadRows(connection, guardSql).Should().BeEmpty();

        Execute(connection, "COMMIT;");
    }

    // --- Finding #2: the per-file catalog/version lock must be held across the catalog refresh, ---
    // --- clone, and pager snapshot pin, so a peer's commit cannot land in that window and pin a ---
    // --- snapshot generation that no longer matches the cloned catalog. ---
    [Test]
    public void PeerCommitBlocksOnTheSharedFileCatalogLockUntilTheSnapshotIsFullyClonedAndPinned()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var setupDatabase = EmbeddedDatabase.OpenFile("overlay-peer-race.db", fileSystem))
        using (var setupConnection = setupDatabase.Connect())
        {
            Execute(setupConnection, "CREATE TABLE outer_items(k INTEGER);");
            Execute(setupConnection, "CREATE TABLE inner_items(k INTEGER, payload TEXT);");
            Execute(setupConnection, "CREATE INDEX inner_items_k ON inner_items(k);");
            Execute(setupConnection, "INSERT INTO outer_items VALUES (5);");
            // A non-empty baseline table is required for the planner to choose a compiled
            // index-seek plan instead of an empty-table scan shortcut; this row never matches.
            Execute(setupConnection, "INSERT INTO inner_items VALUES (999, 'noise');");
            Execute(setupConnection, "ANALYZE;");
        }

        using var databaseA = EmbeddedDatabase.OpenFile("overlay-peer-race.db", fileSystem);
        using var connectionA = databaseA.Connect();
        using var databaseB = EmbeddedDatabase.OpenFile("overlay-peer-race.db", fileSystem);
        using var connectionB = databaseB.Connect();

        using var peerStarted = new ManualResetEventSlim(false);
        var peerCommitted = false;
        Task? peerTask = null;

        EmbeddedDatabase.BeforePinningTransactionSnapshotForTesting = () =>
        {
            peerTask = Task.Run(() =>
            {
                peerStarted.Set();
                Execute(connectionB, "INSERT INTO inner_items VALUES (5, 'five-peer');");
                Volatile.Write(ref peerCommitted, true);
            });

            peerStarted.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue(
                "the peer task must start running while this hook still holds the shared file catalog lock");
            Thread.Sleep(100);
            Volatile.Read(ref peerCommitted).Should().BeFalse(
                "the peer's commit must stay blocked on the shared file-catalog lock for as long as this " +
                "hook — which runs between the catalog clone and the pager snapshot pin — has not returned");
        };
        try
        {
            Execute(connectionA, "BEGIN;");
        }
        finally
        {
            EmbeddedDatabase.BeforePinningTransactionSnapshotForTesting = null;
        }

        peerTask.Should().NotBeNull();
        peerTask!.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the peer task must finish once the lock is released");

        // The pinned snapshot taken for connectionA's BEGIN must be exactly what the catalog
        // clone observed while still holding the lock: the peer's now-committed row must never
        // appear for the rest of this transaction, proving the clone and the pin observed the
        // same generation.
        const string sql =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_k ON outer_items.k = inner_items.k;
            """;
        databaseA.ResetJoinOrderDiagnostics();
        ReadRows(connectionA, sql).Should().BeEmpty();
        databaseA.JoinIndexSeekMetrics.DurableCursorPlans.Should().Be(1);
        Execute(connectionA, "COMMIT;");

        using var verifyConnection = databaseA.Connect();
        ReadRows(verifyConnection, sql).Select(row => row[0].AsText()).Should().Equal("five-peer");
    }

    // --- Review fix: TryOpenIndexAccessor/TryOpenPrimaryKeyIndexAccessor must resolve table and ---
    // --- index root pages from the pinned snapshot's own immutable mapping, never from the file ---
    // --- store's live _tableRootPages/_indexRootPages, so a peer's drop/recreate (which reuses ---
    // --- the same name with a brand-new root page, possibly reusing a page number the old ---
    // --- object used to occupy) cannot redirect an already-pinned transaction's reads. ---
    [Test]
    public void PinnedTransactionResolvesIndexAndPrimaryKeyRootsFromItsOwnSnapshotDespitePeerDropRecreate()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var setupDatabase = EmbeddedDatabase.OpenFile("overlay-pinned-root-pages.db", fileSystem))
        using (var setupConnection = setupDatabase.Connect())
        {
            Execute(setupConnection, "CREATE TABLE outer_items(k INTEGER);");
            Execute(setupConnection, "CREATE TABLE inner_items(k INTEGER, payload TEXT);");
            Execute(setupConnection, "CREATE INDEX inner_items_k ON inner_items(k);");
            Execute(setupConnection, "CREATE TABLE pk_items(k INTEGER PRIMARY KEY, payload TEXT) WITHOUT ROWID;");
            Execute(setupConnection, "INSERT INTO outer_items VALUES (5);");
            Execute(setupConnection, "INSERT INTO inner_items VALUES (5, 'orig-index');");
            // pk_items needs enough rows that the planner's cost model prefers a genuine seek
            // through the table's own primary-key b-tree over building a one-shot in-memory
            // automatic index (which a single-row table would make artificially cheap and would
            // never exercise TryOpenTransactionIndexAccessor's pinned-snapshot resolution at all).
            Execute(
                setupConnection,
                "INSERT INTO pk_items VALUES "
                    + string.Join(
                        ", ",
                        Enumerable.Range(1, 500)
                            .Select(value => value == 5 ? "(5, 'orig-pk')" : $"({value}, 'row-{value}')"))
                    + ";");
            Execute(setupConnection, "ANALYZE;");
        }

        using var databaseA = EmbeddedDatabase.OpenFile("overlay-pinned-root-pages.db", fileSystem);
        using var connectionA = databaseA.Connect();
        using var databaseB = EmbeddedDatabase.OpenFile("overlay-pinned-root-pages.db", fileSystem);
        using var connectionB = databaseB.Connect();

        // BEGIN pins connectionA's durable snapshot — including its table/index root-page
        // mapping — before it makes any schema change of its own, so the pinned-snapshot direct
        // index accessor path stays eligible for the whole transaction below.
        Execute(connectionA, "BEGIN;");

        const string indexSql =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_k ON outer_items.k = inner_items.k;
            """;
        const string pkSql =
            """
            SELECT pk_items.payload
            FROM outer_items
            JOIN pk_items ON outer_items.k = pk_items.k;
            """;

        databaseA.ResetJoinOrderDiagnostics();
        ReadRows(connectionA, indexSql).Select(row => row[0].AsText()).Should().Equal("orig-index");
        ReadRows(connectionA, pkSql).Select(row => row[0].AsText()).Should().Equal("orig-pk");
        databaseA.JoinIndexSeekMetrics.DurableCursorPlans.Should().BeGreaterThan(0);
        databaseA.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);

        // A peer drops and recreates both tables (and the secondary index), reallocating fresh
        // root pages that may reuse the very page numbers connectionA's pinned snapshot captured
        // for the old objects, then churns the free list with an unrelated create/drop cycle so
        // the allocator has additional opportunities to reissue those pages before A reads again.
        Execute(connectionB, "DROP TABLE inner_items;");
        Execute(connectionB, "CREATE TABLE inner_items(k INTEGER, payload TEXT);");
        Execute(connectionB, "CREATE INDEX inner_items_k ON inner_items(k);");
        Execute(connectionB, "INSERT INTO inner_items VALUES (5, 'new-index');");
        Execute(connectionB, "DROP TABLE pk_items;");
        Execute(connectionB, "CREATE TABLE pk_items(k INTEGER PRIMARY KEY, payload TEXT) WITHOUT ROWID;");
        Execute(connectionB, "INSERT INTO pk_items VALUES (5, 'new-pk');");
        Execute(connectionB, "CREATE TABLE churn(v INTEGER);");
        Execute(connectionB, "DROP TABLE churn;");
        Execute(connectionB, "ANALYZE;");

        // connectionA's still-open transaction must keep resolving inner_items/pk_items (and the
        // secondary index) to the root pages its own pinned snapshot captured at BEGIN, not
        // whatever object the peer's drop/recreate now owns those same page numbers under: no
        // exception, and the transaction's original rows, never the peer's new ones.
        databaseA.ResetJoinOrderDiagnostics();
        ReadRows(connectionA, indexSql).Select(row => row[0].AsText()).Should().Equal("orig-index");
        ReadRows(connectionA, pkSql).Select(row => row[0].AsText()).Should().Equal("orig-pk");
        databaseA.JoinIndexSeekMetrics.DurableCursorPlans.Should().BeGreaterThan(0);
        databaseA.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);

        Execute(connectionA, "COMMIT;");

        // After commit, a fresh read must see the peer's committed data, proving A's isolation
        // was scoped to its own transaction rather than a permanent staleness bug.
        databaseA.ResetJoinOrderDiagnostics();
        ReadRows(connectionA, indexSql).Select(row => row[0].AsText()).Should().Equal("new-index");
        ReadRows(connectionA, pkSql).Select(row => row[0].AsText()).Should().Equal("new-pk");
    }

    // --- Finding #3: if the pager snapshot pin fails after the catalog clone and active- ---
    // --- transaction registration, BeginTransaction must clean up the currently-failing ---
    // --- database's cloned virtual tables and active-transaction count too, not just the ---
    // --- other databases already recorded in `states`. ---
    [Test]
    public void FailedSnapshotPinDuringBeginCleansUpSoTheConnectionStaysFullyUsableAfterward()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("overlay-begin-pin-failure.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1);");

        EmbeddedDatabase.BeforePinningTransactionSnapshotForTesting =
            () => throw new IOException("simulated pager snapshot-open failure");
        try
        {
            var beginWithInjectedFailure = () => Execute(connection, "BEGIN;");
            beginWithInjectedFailure.Should().Throw<IOException>()
                .WithMessage("*simulated pager snapshot-open failure*");
        }
        finally
        {
            EmbeddedDatabase.BeforePinningTransactionSnapshotForTesting = null;
        }

        // A failed pin must have cleaned up the cloned virtual tables and decremented the
        // active-transaction count for this database, and BeginTransaction's own cleanup must
        // have covered the currently-failing database too: a fresh explicit transaction on the
        // same connection must now work exactly as if the failed BEGIN had never happened.
        Execute(connection, "BEGIN;");
        ReadRows(connection, "SELECT v FROM t;").Select(row => row[0].AsInteger()).Should().Equal(1L);
        Execute(connection, "INSERT INTO t VALUES (2);");
        Execute(connection, "COMMIT;");
        ReadRows(connection, "SELECT v FROM t ORDER BY v;")
            .Select(row => row[0].AsInteger()).Should().Equal(1L, 2L);

        // VACUUM's own dispatch throws if the active-transaction count was left nonzero, so a
        // clean VACUUM here independently proves the failed BEGIN did not leak the
        // active-transaction bump that CloneTransactionSnapshotLocked applied before the
        // injected pin failure.
        Execute(connection, "VACUUM;");
    }

    // BeginConcurrentTransactionSnapshotLocked's own failure-cleanup catch block used to only
    // dispose a partially-opened pinned pager snapshot and roll back the MvStore transaction it
    // had just begun -- it never disconnected the catalog CloneTransactionSnapshotLocked had
    // already cloned, nor called EndTransaction to undo the active-transaction count that same
    // clone had already bumped, whenever the durable pager snapshot pin failed *after* the
    // clone succeeded. That left a stale active-transaction registration (and, for a database
    // with virtual tables, orphaned cloned virtual-table instances) behind every failed BEGIN
    // CONCURRENT pin, even though the MvStore transaction itself was already correctly rolled
    // back. This must never happen: a failed pin must unwind exactly as completely as a failed
    // pin on the classic (non-MVCC) BEGIN path above.
    [Test]
    public void FailedSnapshotPinDuringBeginConcurrentCleansUpSoTheConnectionStaysFullyUsableAfterward()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("overlay-begin-concurrent-pin-failure.db", fileSystem);
        using var connection = database.Connect();
        ReadRows(connection, "PRAGMA journal_mode=mvcc;");
        Execute(connection, "CREATE TABLE t(v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1);");

        EmbeddedDatabase.BeforePinningTransactionSnapshotForTesting =
            () => throw new IOException("simulated pager snapshot-open failure");
        try
        {
            var beginConcurrentWithInjectedFailure = () => Execute(connection, "BEGIN CONCURRENT;");
            beginConcurrentWithInjectedFailure.Should().Throw<IOException>()
                .WithMessage("*simulated pager snapshot-open failure*");
        }
        finally
        {
            EmbeddedDatabase.BeforePinningTransactionSnapshotForTesting = null;
        }

        // A failed pin must have rolled back the MvStore transaction
        // BeginConcurrentTransactionSnapshotLocked had already begun, disconnected the cloned
        // virtual tables, and decremented the active-transaction count: a fresh BEGIN CONCURRENT
        // on the same connection must now work exactly as if the failed one had never happened.
        Execute(connection, "BEGIN CONCURRENT;");
        ReadRows(connection, "SELECT v FROM t;").Select(row => row[0].AsInteger()).Should().Equal(1L);
        Execute(connection, "INSERT INTO t VALUES (2);");
        Execute(connection, "COMMIT;");
        ReadRows(connection, "SELECT v FROM t ORDER BY v;")
            .Select(row => row[0].AsInteger()).Should().Equal(1L, 2L);

        // VACUUM's own dispatch throws if the active-transaction count was left nonzero, so a
        // clean VACUUM here independently proves the failed BEGIN CONCURRENT did not leak the
        // active-transaction bump CloneTransactionSnapshotLocked applied, and the fresh
        // BEGIN CONCURRENT/COMMIT round-trip above independently proves the rolled-back MvStore
        // transaction left no dangling read mark or version-chain registration behind either.
        Execute(connection, "VACUUM;");
    }

    // --- Review fix: CreateTransactionSnapshotWithPin's cleanup must attempt every step (virtual ---
    // --- table disconnect, then EndTransaction) even when an earlier step also throws, and ---
    // --- SchemaCatalog.DisconnectOwnedVirtualTables must itself attempt every owned instance ---
    // --- instead of stopping at the first one whose Disconnect() callback throws. This combines ---
    // --- both: two virtual table instances are cloned into the failing pin's snapshot catalog, ---
    // --- only the first (by name) throws on Disconnect(), and the pin itself is also made to ---
    // --- fail, so BeginTransaction must surface both failures while still disconnecting the ---
    // --- second instance and unconditionally running EndTransaction. ---
    [Test]
    public void FailedSnapshotPinWithAThrowingVirtualTableDisconnectStillDisconnectsEveryInstanceAndEndsTheTransaction()
    {
        _ = MultiDisconnectModule.Instance;
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("overlay-multi-disconnect-pin-failure.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1);");
        Execute(connection, $"CREATE VIRTUAL TABLE v1 USING {MultiDisconnectModule.ModuleName}();");
        Execute(connection, $"CREATE VIRTUAL TABLE v2 USING {MultiDisconnectModule.ModuleName}();");

        MultiDisconnectTable.DisconnectedNames = [];
        MultiDisconnectTable.ThrowOnDisconnectForName = "v1";
        EmbeddedDatabase.BeforePinningTransactionSnapshotForTesting =
            () => throw new IOException("simulated pager snapshot-open failure");
        try
        {
            var beginWithInjectedFailure = () => Execute(connection, "BEGIN;");
            // Both the primary pin failure and v1's cleanup-time disconnect failure must be
            // preserved, not silently dropped in favor of one or the other.
            beginWithInjectedFailure.Should().Throw<AggregateException>()
                .Where(aggregate =>
                    aggregate.InnerExceptions.Any(inner => inner is IOException)
                    && aggregate.InnerExceptions.Any(inner => inner is InvalidOperationException));
        }
        finally
        {
            EmbeddedDatabase.BeforePinningTransactionSnapshotForTesting = null;
            MultiDisconnectTable.ThrowOnDisconnectForName = null;
        }

        // Every virtual table instance owned by the failed transaction's cloned catalog must
        // have gotten a disconnect attempt, not just the ones enumerated before v1's throwing
        // callback: a single misbehaving instance must never leak every later one in the same
        // catalog's disconnect loop.
        MultiDisconnectTable.DisconnectedNames.Should().Contain(["v1", "v2"]);
        MultiDisconnectTable.DisconnectedNames = null;

        // EndTransaction must still have run despite the disconnect failure: a fresh explicit
        // transaction on the same connection now works exactly as if the failed BEGIN had never
        // happened, and VACUUM (which throws if the active-transaction count was left nonzero)
        // independently proves the active-transaction count and any write reservation/lock this
        // method had already taken were not leaked.
        Execute(connection, "BEGIN;");
        ReadRows(connection, "SELECT v FROM t;").Select(row => row[0].AsInteger()).Should().Equal(1L);
        Execute(connection, "INSERT INTO t VALUES (2);");
        Execute(connection, "COMMIT;");
        ReadRows(connection, "SELECT v FROM t ORDER BY v;")
            .Select(row => row[0].AsInteger()).Should().Equal(1L, 2L);
        Execute(connection, "VACUUM;");
    }

    // --- Finding #4: TransactionMutationOverlay.RestoreCheckpoint must remove overlays for ---
    // --- tables absent from the checkpoint and rebuild an overlay under the checkpoint's own ---
    // --- shape when a table's rowid-vs-WITHOUT-ROWID identity changed since the checkpoint was ---
    // --- taken (dropped and recreated across a savepoint rollback). These are white-box tests ---
    // --- against the overlay types directly; they never call the buggy-by-design ---
    // --- TransactionMutationOverlay.GetOrCreate(database, table, tableName) (which looks up by ---
    // --- name only and cannot see a shape change), and instead populate `_tables` exclusively ---
    // --- through RestoreCheckpoint's own "no live entry / shape mismatch" branch. ---
    [Test]
    public void RestoreCheckpointRemovesOverlayEntriesForTablesAbsentFromTheCheckpoint()
    {
        var overlay = new TransactionMutationOverlay();
        var seedCheckpoint = new TransactionMutationOverlayCheckpoint(
            new Dictionary<string, TransactionTableOverlayCheckpoint>(StringComparer.OrdinalIgnoreCase)
            {
                ["ghost"] = new TransactionTableOverlayCheckpoint(
                    true,
                    new Dictionary<long, SqlValue[]?> { [1] = [SqlValue.Integer(1)] },
                    null),
            });
        overlay.RestoreCheckpoint(seedCheckpoint);
        overlay.TryGet("ghost", out var seeded).Should().BeTrue();
        seeded.TryGetByRowId(1, out var seededRow).Should().BeTrue();
        seededRow.Should().Equal(SqlValue.Integer(1));

        // Restoring a checkpoint predating "ghost"'s first touch (an empty checkpoint) must
        // remove the overlay outright, not merely clear it: leaving a stale-but-cleared entry
        // around would keep the table's overlay identity alive under the wrong assumption that
        // it had already been touched at checkpoint time.
        var emptyCheckpoint = new TransactionMutationOverlayCheckpoint(
            new Dictionary<string, TransactionTableOverlayCheckpoint>(StringComparer.OrdinalIgnoreCase));
        overlay.RestoreCheckpoint(emptyCheckpoint);

        overlay.TryGet("ghost", out _).Should().BeFalse(
            "a table overlay absent from the checkpoint must be removed outright");
    }

    [Test]
    public void RestoreCheckpointReconstructsAMismatchedShapeOverlayFromTheCheckpointRowidData()
    {
        var overlay = new TransactionMutationOverlay();

        // Seed "shifted" as a WITHOUT ROWID overlay: the shape in effect before the simulated
        // DROP/CREATE across the savepoint.
        var withoutRowidCheckpoint = new TransactionMutationOverlayCheckpoint(
            new Dictionary<string, TransactionTableOverlayCheckpoint>(StringComparer.OrdinalIgnoreCase)
            {
                ["shifted"] = new TransactionTableOverlayCheckpoint(
                    false,
                    null,
                    [([SqlValue.Integer(9)], [SqlValue.Integer(9), SqlValue.Text("stale-shape")])]),
            });
        overlay.RestoreCheckpoint(withoutRowidCheckpoint, _ => new SqliteIndexRecordComparer());
        overlay.TryGet("shifted", out var seeded).Should().BeTrue();
        seeded.HasRowid.Should().BeFalse();

        // Now restore a checkpoint that describes "shifted" as a ROWID table — as if it had been
        // dropped and recreated with a different shape after the checkpoint was taken but before
        // this ROLLBACK TO. No resolver is supplied: a ROWID checkpoint target must never need
        // one, since only a WITHOUT ROWID replacement overlay needs a primary-key comparer.
        var rowidCheckpoint = new TransactionMutationOverlayCheckpoint(
            new Dictionary<string, TransactionTableOverlayCheckpoint>(StringComparer.OrdinalIgnoreCase)
            {
                ["shifted"] = new TransactionTableOverlayCheckpoint(
                    true,
                    new Dictionary<long, SqlValue[]?> { [5] = [SqlValue.Text("recreated")] },
                    null),
            });
        overlay.RestoreCheckpoint(rowidCheckpoint);

        overlay.TryGet("shifted", out var reconstructed).Should().BeTrue();
        reconstructed.HasRowid.Should().BeTrue(
            "a shape mismatch between the live overlay and the checkpoint must replace the overlay " +
            "with a freshly shaped one rather than force mismatched data into the wrong storage");
        reconstructed.TryGetByRowId(5, out var recreatedRow).Should().BeTrue();
        recreatedRow.Should().Equal(SqlValue.Text("recreated"));
        // The old WITHOUT ROWID data is gone by construction: TryGetByPrimaryKey always returns
        // false on a ROWID-shaped overlay.
        reconstructed.TryGetByPrimaryKey([SqlValue.Integer(9)], out _).Should().BeFalse();
    }

    [Test]
    public void RestoreCheckpointThrowsWhenAWithoutRowidReconstructionNeedsAComparerButNoneWasSupplied()
    {
        var overlay = new TransactionMutationOverlay();
        var checkpoint = new TransactionMutationOverlayCheckpoint(
            new Dictionary<string, TransactionTableOverlayCheckpoint>(StringComparer.OrdinalIgnoreCase)
            {
                ["wr"] = new TransactionTableOverlayCheckpoint(
                    false,
                    null,
                    [([SqlValue.Integer(1)], [SqlValue.Integer(1)])]),
            });

        var restoreWithoutResolver = () => overlay.RestoreCheckpoint(checkpoint);

        restoreWithoutResolver.Should().Throw<InvalidOperationException>()
            .WithMessage("*primary-key comparer*");
    }

    [Test]
    public void DropAndRecreateWithDifferentRowIdentityDoesNotReuseAnIncompatibleOverlay()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("overlay-recreate-shape.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, payload TEXT);");

        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO t VALUES (1, 'rowid');");
        Execute(connection, "DROP TABLE t;");
        Execute(connection, "CREATE TABLE t(id TEXT PRIMARY KEY, payload TEXT) WITHOUT ROWID;");
        Execute(connection, "INSERT INTO t VALUES ('key', 'without-rowid');");
        ReadRows(connection, "SELECT payload FROM t;").Single()[0].AsText()
            .Should().Be("without-rowid");
        Execute(connection, "ROLLBACK;");

        ReadRows(connection, "SELECT count(*) FROM t;").Single()[0].AsInteger().Should().Be(0);
    }

    // --- Finding #5: ResetTransactionState must complete every cleanup step — catalog ---
    // --- disconnect, pinned-snapshot dispose, savepoint/overlay clearing, write-reservation ---
    // --- release, and EndTransaction — even when a virtual table's Rollback() callback throws, ---
    // --- surfacing that callback's exception only after all cleanup has run. ---
    [Test]
    public void ResetTransactionStateCompletesAllCleanupEvenWhenAVirtualTableRollbackCallbackThrows()
    {
        _ = RollbackFailureModule.Instance;
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("overlay-vtab-rollback-throws.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1);");
        Execute(connection, $"CREATE VIRTUAL TABLE side USING {RollbackFailureModule.ModuleName}();");

        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO t VALUES (2);");
        // Only a write against the virtual table registers it as a participant in this
        // transaction's ManagedVirtualTableTransaction, which is what makes ResetTransactionState
        // actually invoke its Rollback() callback below.
        Execute(connection, "INSERT INTO side VALUES (1);");

        RollbackFailureTable.ThrowOnRollback = true;
        var rollbackWithThrowingVirtualTable = () => Execute(connection, "ROLLBACK;");
        rollbackWithThrowingVirtualTable.Should().Throw<InvalidOperationException>()
            .WithMessage("*simulated virtual-table rollback failure*");

        // Despite the virtual table's Rollback() callback throwing, ResetTransactionState must
        // still have discarded the transaction (t.v = 2 never committed), disposed the pinned
        // snapshot, released the write reservation, and ended the transaction: a fresh
        // BEGIN/read/write/COMMIT on the very same connection must work exactly as if the failed
        // rollback callback had never happened, proving no leaked read mark or lock.
        Execute(connection, "BEGIN;");
        ReadRows(connection, "SELECT v FROM t;").Select(row => row[0].AsInteger()).Should().Equal(1L);
        Execute(connection, "INSERT INTO t VALUES (3);");
        Execute(connection, "COMMIT;");
        ReadRows(connection, "SELECT v FROM t ORDER BY v;")
            .Select(row => row[0].AsInteger()).Should().Equal(1L, 3L);

        // VACUUM's own dispatch throws if the active-transaction count was left nonzero, so a
        // clean VACUUM here independently proves EndTransaction() ran for this database despite
        // the earlier throwing rollback callback.
        Execute(connection, "VACUUM;");
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static List<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var values = new SqlValue[statement.GetColumnCount()];
            for (var ordinal = 0; ordinal < values.Length; ordinal++)
                values[ordinal] = statement.GetValue(ordinal);
            rows.Add(values);
        }

        return rows;
    }

    /// <summary>Minimal writable virtual-table module whose table simulates a throwing Rollback().</summary>
    private sealed class RollbackFailureModule : ManagedVirtualTableModule
    {
        public const string ModuleName = "overlay_rollback_failure_test";

        public static readonly RollbackFailureModule Instance = Register();

        public override string Name => ModuleName;

        public override ManagedVirtualTable Create(ManagedVirtualTableCreateContext context) => new RollbackFailureTable();

        public override ManagedVirtualTable Create(
            ManagedVirtualTableCreateContext context,
            ManagedVirtualTablePersistencePayload payload)
            => new RollbackFailureTable();

        private static RollbackFailureModule Register()
        {
            var module = new RollbackFailureModule();
            ManagedVirtualTableModuleRegistry.Register(module);
            return module;
        }
    }

    private sealed class RollbackFailureTable : ManagedVirtualTable
    {
        private static readonly ManagedVirtualTableSchema TableSchema =
            new([new ManagedVirtualTableColumn("v", ManagedVirtualTableAffinity.Integer)]);

        [ThreadStatic]
        public static bool ThrowOnRollback;

        public override ManagedVirtualTableSchema Schema => TableSchema;

        public override ManagedVirtualTablePlan BestIndex(
            IReadOnlyList<ManagedVirtualTableConstraint> constraints,
            IReadOnlyList<ManagedVirtualTableOrderBy> orderBy)
            => new(constraints.Select(static _ => ManagedVirtualTableConstraintUsage.Unused).ToArray());

        public override ManagedVirtualTableCursor Open() => new RollbackFailureCursor();

        public override ManagedVirtualTablePersistencePayload GetPersistencePayload() => new(1, []);

        public override long? Update(IReadOnlyList<SqlValue> arguments) => 1L;

        public override void Rollback()
        {
            if (!ThrowOnRollback)
                return;
            ThrowOnRollback = false;
            throw new InvalidOperationException("simulated virtual-table rollback failure");
        }

        private sealed class RollbackFailureCursor : ManagedVirtualTableCursor
        {
            public override bool Filter(ManagedVirtualTablePlan plan, IReadOnlyList<SqlValue> arguments) => true;

            public override void Next()
            {
            }

            public override bool Eof => true;

            public override SqlValue Column(int columnIndex)
                => throw new InvalidOperationException("this test-double virtual table never has rows");

            public override long RowId
                => throw new InvalidOperationException("this test-double virtual table never has rows");
        }
    }

    /// <summary>
    /// Minimal virtual-table module whose instances record every Disconnect() call by the name
    /// they were created with, and can be told to throw for one specific name — used to prove a
    /// catalog owning several instances attempts every instance's disconnect even when one of
    /// them fails.
    /// </summary>
    private sealed class MultiDisconnectModule : ManagedVirtualTableModule
    {
        public const string ModuleName = "overlay_multi_disconnect_test";

        public static readonly MultiDisconnectModule Instance = Register();

        public override string Name => ModuleName;

        public override ManagedVirtualTable Create(ManagedVirtualTableCreateContext context)
            => new MultiDisconnectTable(context.TableName);

        public override ManagedVirtualTable Create(
            ManagedVirtualTableCreateContext context,
            ManagedVirtualTablePersistencePayload payload)
            => new MultiDisconnectTable(context.TableName);

        private static MultiDisconnectModule Register()
        {
            var module = new MultiDisconnectModule();
            ManagedVirtualTableModuleRegistry.Register(module);
            return module;
        }
    }

    private sealed class MultiDisconnectTable(string name) : ManagedVirtualTable
    {
        private static readonly ManagedVirtualTableSchema TableSchema =
            new([new ManagedVirtualTableColumn("v", ManagedVirtualTableAffinity.Integer)]);

        [ThreadStatic]
        public static string? ThrowOnDisconnectForName;

        [ThreadStatic]
        public static List<string>? DisconnectedNames;

        public override ManagedVirtualTableSchema Schema => TableSchema;

        public override ManagedVirtualTablePlan BestIndex(
            IReadOnlyList<ManagedVirtualTableConstraint> constraints,
            IReadOnlyList<ManagedVirtualTableOrderBy> orderBy)
            => new(constraints.Select(static _ => ManagedVirtualTableConstraintUsage.Unused).ToArray());

        public override ManagedVirtualTableCursor Open() => new MultiDisconnectCursor();

        public override ManagedVirtualTablePersistencePayload GetPersistencePayload() => new(1, []);

        public override void Disconnect()
        {
            DisconnectedNames?.Add(name);
            if (string.Equals(ThrowOnDisconnectForName, name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"simulated disconnect failure for '{name}'");
        }

        private sealed class MultiDisconnectCursor : ManagedVirtualTableCursor
        {
            public override bool Filter(ManagedVirtualTablePlan plan, IReadOnlyList<SqlValue> arguments) => true;

            public override void Next()
            {
            }

            public override bool Eof => true;

            public override SqlValue Column(int columnIndex)
                => throw new InvalidOperationException("this test-double virtual table never has rows");

            public override long RowId
                => throw new InvalidOperationException("this test-double virtual table never has rows");
        }
    }
}
