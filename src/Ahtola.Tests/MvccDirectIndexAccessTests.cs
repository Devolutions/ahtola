using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

/// <summary>
/// Focused coverage for the MVCC (BEGIN CONCURRENT) page-native direct-index phase: an eligible
/// join index seek or single-table index scan must serve rows directly from the transaction's own
/// pinned durable b-tree snapshot (<see cref="QueryContext.TransactionPinnedSnapshot"/>, captured at
/// BEGIN CONCURRENT), merged lazily with this transaction's visible <c>MvStore</c> effects via the
/// same two-peek primitive the classic (non-MVCC) transaction overlay uses (<see
/// cref="MvccDualCursor"/>), so every scenario below asserts both correctness (read your own
/// writes, suppress base rows shadowed by a visible overlay effect, respect snapshot isolation
/// against peers) and the zero-materialization contract (<see
/// cref="VdbeJoinIndexSeekMetrics.DurableCursorPlans"/> greater than zero, <see
/// cref="VdbeJoinIndexSeekMetrics.IndexRowsMaterialized"/> equal to zero).
/// </summary>
public sealed class MvccDirectIndexAccessTests
{
    [Test]
    public void OrdinaryIndexJoinUsesTheDurableSnapshotWithoutMaterializingTheBaseIndex()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("mvcc-ordinary-index-join.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_items(k INTEGER);");
        Execute(connection, "CREATE TABLE inner_items(k INTEGER, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_items_k ON inner_items(k);");
        Execute(connection, "INSERT INTO outer_items VALUES (1), (2);");
        Execute(connection, "INSERT INTO inner_items VALUES (1, 'one'), (2, 'two');");
        Execute(connection, "PRAGMA journal_mode=mvcc;");
        Execute(connection, "ANALYZE;");

        Execute(connection, "BEGIN CONCURRENT;");

        const string sql =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_k ON outer_items.k = inner_items.k
            ORDER BY outer_items.k;
            """;
        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, sql).Select(row => row[0].AsText()).Should().Equal("one", "two");
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().BeGreaterThan(0);
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);

        Execute(connection, "COMMIT;");
    }

    [Test]
    public void LocalInsertUpdateDeleteAndKeyMoveAreVisibleThroughTheMvccMerge()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("mvcc-local-mutations.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_items(k INTEGER);");
        Execute(connection, "CREATE TABLE inner_items(id INTEGER PRIMARY KEY, k INTEGER, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_items_k ON inner_items(k);");
        Execute(connection, "INSERT INTO outer_items VALUES (1), (2), (3), (4), (5);");
        Execute(
            connection,
            "INSERT INTO inner_items VALUES "
                + "(1, 1, 'stays'), (2, 2, 'will-update'), (3, 3, 'will-delete'), (4, 40, 'will-move');");
        Execute(connection, "PRAGMA journal_mode=mvcc;");
        Execute(connection, "ANALYZE;");

        Execute(connection, "BEGIN CONCURRENT;");
        // Local insert: a brand new row this transaction alone can see.
        Execute(connection, "INSERT INTO inner_items VALUES (5, 5, 'new-in-txn');");
        // Local update of a non-indexed column: the old key stays valid, payload changes.
        Execute(connection, "UPDATE inner_items SET payload = 'updated' WHERE id = 2;");
        // Local delete: must be suppressed from the merged stream.
        Execute(connection, "DELETE FROM inner_items WHERE id = 3;");
        // Local key-changing update: the old key (40) must vanish, the new key (4) must appear.
        Execute(connection, "UPDATE inner_items SET k = 4 WHERE id = 4;");

        const string sql =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_k ON outer_items.k = inner_items.k
            ORDER BY outer_items.k;
            """;
        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, sql).Select(row => row[0].AsText())
            .Should().Equal("stays", "updated", "will-move", "new-in-txn");
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().BeGreaterThan(0);
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);

        Execute(connection, "ROLLBACK;");
    }

    [Test]
    public void PartialIndexMembershipChangesAreReflectedThroughTheMvccMerge()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("mvcc-partial-membership.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_items(k INTEGER);");
        Execute(connection, "CREATE TABLE inner_items(id INTEGER PRIMARY KEY, k INTEGER, gate INTEGER, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_items_partial ON inner_items(k) WHERE gate > 0;");
        Execute(connection, "INSERT INTO outer_items VALUES (1), (2);");
        // id=1 starts outside the partial predicate (gate <= 0); id=2 starts inside it.
        Execute(connection, "INSERT INTO inner_items VALUES (1, 1, -1, 'joins-in-txn');");
        Execute(connection, "INSERT INTO inner_items VALUES (2, 2, 1, 'leaves-in-txn');");
        Execute(connection, "PRAGMA journal_mode=mvcc;");
        Execute(connection, "ANALYZE;");

        Execute(connection, "BEGIN CONCURRENT;");
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
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().BeGreaterThan(0);
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);

        Execute(connection, "ROLLBACK;");
    }

    [Test]
    public void ExpressionIndexKeyChangesAreReflectedThroughTheMvccMerge()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("mvcc-expression-key.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_items(k);");
        Execute(connection, "CREATE TABLE inner_items(id INTEGER PRIMARY KEY, k TEXT, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_items_lower_k ON inner_items(lower(k));");
        Execute(connection, "INSERT INTO outer_items VALUES ('old'), ('new');");
        Execute(connection, "INSERT INTO inner_items VALUES (1, 'OLD', 'p-old');");
        Execute(connection, "PRAGMA journal_mode=mvcc;");
        Execute(connection, "ANALYZE;");

        Execute(connection, "BEGIN CONCURRENT;");
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
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().BeGreaterThan(0);
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);

        Execute(connection, "ROLLBACK;");
    }

    [Test]
    public void WithoutRowidPrimaryKeyAndSecondaryIndexSeeksReflectMvccMutations()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("mvcc-without-rowid.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_codes(code TEXT);");
        Execute(connection, "CREATE TABLE outer_tags(tag TEXT);");
        Execute(connection, "CREATE TABLE entry(code TEXT PRIMARY KEY, tag TEXT) WITHOUT ROWID;");
        Execute(connection, "CREATE INDEX entry_tag ON entry(tag);");
        Execute(connection, "INSERT INTO outer_codes VALUES ('k1'), ('k2');");
        Execute(connection, "INSERT INTO outer_tags VALUES ('t1'), ('t9');");
        Execute(connection, "INSERT INTO entry VALUES ('k1', 't1');");
        Execute(connection, "PRAGMA journal_mode=mvcc;");
        Execute(connection, "ANALYZE;");

        Execute(connection, "BEGIN CONCURRENT;");
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
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);

        Execute(connection, "ROLLBACK;");
    }

    [Test]
    public void DuplicateIndexKeysAcrossBaseAndOverlayAreAllEmittedExactlyOnce()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("mvcc-duplicate-prefix.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_items(k INTEGER);");
        Execute(connection, "CREATE TABLE inner_items(id INTEGER PRIMARY KEY, k INTEGER, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_items_k ON inner_items(k);");
        Execute(connection, "INSERT INTO outer_items VALUES (7);");
        // Two base rows already share the duplicate key.
        Execute(connection, "INSERT INTO inner_items VALUES (1, 7, 'base-a'), (2, 7, 'base-b');");
        Execute(connection, "PRAGMA journal_mode=mvcc;");
        Execute(connection, "ANALYZE;");

        Execute(connection, "BEGIN CONCURRENT;");
        // A third row joins the same duplicate key purely through this transaction's overlay.
        Execute(connection, "INSERT INTO inner_items VALUES (3, 7, 'overlay-c');");

        const string sql =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_k ON outer_items.k = inner_items.k
            ORDER BY inner_items.payload;
            """;
        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, sql).Select(row => row[0].AsText())
            .Should().Equal("base-a", "base-b", "overlay-c");
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().BeGreaterThan(0);
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);

        Execute(connection, "ROLLBACK;");
    }

    [Test]
    public void LimitShortCircuitsTheMergedDurableStreamWithoutMaterializingTheWholeIndex()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("mvcc-limit.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, k INTEGER, payload TEXT);");
        Execute(connection, "CREATE INDEX items_k ON items(k);");
        Execute(
            connection,
            "INSERT INTO items VALUES "
                + string.Join(", ", Enumerable.Range(1, 500).Select(value => $"({value}, {value}, 'p{value}')"))
                + ";");
        Execute(connection, "PRAGMA journal_mode=mvcc;");
        Execute(connection, "ANALYZE;");

        Execute(connection, "BEGIN CONCURRENT;");
        Execute(connection, "INSERT INTO items VALUES (501, 0, 'overlay-first');");

        const string sql =
            """
            SELECT items.payload
            FROM items INDEXED BY items_k
            WHERE items.k >= 0
            ORDER BY items.k
            LIMIT 2;
            """;
        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, sql).Select(row => row[0].AsText()).Should().Equal("overlay-first", "p1");
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);

        Execute(connection, "ROLLBACK;");
    }

    [Test]
    public void SavepointRollbackRestoresTheMvccMergeToItsCheckpointedState()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("mvcc-savepoint-rollback.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_items(k INTEGER);");
        Execute(connection, "CREATE TABLE inner_items(k INTEGER, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_items_k ON inner_items(k);");
        Execute(connection, "INSERT INTO outer_items VALUES (10), (11);");
        Execute(connection, "PRAGMA journal_mode=mvcc;");
        Execute(connection, "ANALYZE;");

        const string sql =
            """
            SELECT outer_items.k, inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_k ON outer_items.k = inner_items.k
            ORDER BY outer_items.k;
            """;

        Execute(connection, "BEGIN CONCURRENT;");
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
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);

        Execute(connection, "ROLLBACK;");
    }

    [Test]
    public void PeerCommitAfterBeginConcurrentStaysInvisibleUntilANewTransactionBegins()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("mvcc-peer-commit.db", fileSystem);
        using var writer = database.Connect();
        using var reader = database.Connect();
        Execute(writer, "CREATE TABLE outer_items(k INTEGER);");
        Execute(writer, "CREATE TABLE inner_items(k INTEGER, payload TEXT);");
        Execute(writer, "CREATE INDEX inner_items_k ON inner_items(k);");
        Execute(writer, "INSERT INTO outer_items VALUES (2), (3);");
        Execute(writer, "INSERT INTO inner_items VALUES (2, 'two');");
        Execute(writer, "PRAGMA journal_mode=mvcc;");
        Execute(writer, "ANALYZE;");

        Execute(reader, "BEGIN CONCURRENT;");

        Execute(writer, "BEGIN CONCURRENT;");
        Execute(writer, "INSERT INTO inner_items VALUES (3, 'three-peer');");
        Execute(writer, "COMMIT;");

        const string sql =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_k ON outer_items.k = inner_items.k
            ORDER BY outer_items.k;
            """;
        database.ResetJoinOrderDiagnostics();
        // Snapshot isolation: the peer's post-BEGIN commit must stay invisible for the whole
        // reader transaction.
        ReadRows(reader, sql).Select(row => row[0].AsText()).Should().Equal("two");
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().BeGreaterThan(0);
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);
        Execute(reader, "ROLLBACK;");

        // A fresh transaction re-pins its own snapshot and now sees the peer's committed row.
        Execute(reader, "BEGIN CONCURRENT;");
        database.ResetJoinOrderDiagnostics();
        ReadRows(reader, sql).Select(row => row[0].AsText()).Should().Equal("two", "three-peer");
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);
        Execute(reader, "ROLLBACK;");
    }

    [Test]
    public void CheckpointAndReopenPreserveTheDurableMvccIndexJoinPath()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "mvcc-checkpoint-reopen.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE outer_items(k INTEGER);");
            Execute(connection, "CREATE TABLE inner_items(id INTEGER PRIMARY KEY, k INTEGER, payload TEXT);");
            Execute(connection, "CREATE INDEX inner_items_k ON inner_items(k);");
            Execute(connection, "INSERT INTO outer_items VALUES (1), (2), (3), (4);");
            Execute(connection, "INSERT INTO inner_items VALUES (1, 1, 'stays'), (2, 2, 'will-move'), (3, 3, 'will-delete');");
            Execute(connection, "PRAGMA journal_mode=mvcc;");
            Execute(connection, "ANALYZE;");

            Execute(connection, "BEGIN CONCURRENT;");
            Execute(connection, "UPDATE inner_items SET k = 4 WHERE id = 2;");
            Execute(connection, "DELETE FROM inner_items WHERE id = 3;");
            Execute(connection, "INSERT INTO inner_items VALUES (4, 4, 'new-in-txn');");
            Execute(connection, "COMMIT;");

            database.RunMvccCheckpoint("TRUNCATE");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Execute(reopenedConnection, "BEGIN CONCURRENT;");

        const string sql =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_k ON outer_items.k = inner_items.k
            ORDER BY inner_items.payload;
            """;
        reopened.ResetJoinOrderDiagnostics();
        ReadRows(reopenedConnection, sql).Select(row => row[0].AsText())
            .Should().Equal("new-in-txn", "stays", "will-move");
        reopened.JoinIndexSeekMetrics.DurableCursorPlans.Should().BeGreaterThan(0);
        reopened.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);

        Execute(reopenedConnection, "ROLLBACK;");
    }

    [Test]
    public void OldestReaderKeepsItsSnapshotConsistentAcrossACheckpointWhileUsingTheDurableIndexJoin()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "mvcc-oldest-reader-index-join.db";
        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var writer = database.Connect();
        using var reader = database.Connect();
        Execute(writer, "CREATE TABLE outer_items(k INTEGER);");
        Execute(writer, "CREATE TABLE inner_items(id INTEGER PRIMARY KEY, k INTEGER, payload TEXT);");
        Execute(writer, "CREATE INDEX inner_items_k ON inner_items(k);");
        Execute(writer, "INSERT INTO outer_items VALUES (9), (91);");
        Execute(writer, "INSERT INTO inner_items VALUES (1, 9, 'base');");
        Execute(writer, "PRAGMA journal_mode=mvcc;");
        Execute(writer, "ANALYZE;");

        // The reader pins its snapshot (and version-store floor) before the writer's later commit.
        Execute(reader, "BEGIN CONCURRENT;");

        Execute(writer, "BEGIN CONCURRENT;");
        Execute(writer, "INSERT INTO inner_items VALUES (2, 91, 'from-writer');");
        Execute(writer, "COMMIT;");

        // The reader's still-open BEGIN CONCURRENT pins a real durable pager read snapshot (the
        // page-native base cursor this phase adds), so TRUNCATE correctly declines to reset the
        // WAL while that reader might still need pre-checkpoint frames — exactly like a classic
        // reader transaction. The reader's row visibility and the zero-materialization join
        // metrics below are what this test actually verifies.
        var checkpoint = database.RunMvccCheckpoint("TRUNCATE");
        checkpoint.Busy.Should().BeTrue();

        const string sql =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_k ON outer_items.k = inner_items.k
            ORDER BY outer_items.k;
            """;
        database.ResetJoinOrderDiagnostics();
        // The oldest reader's snapshot must stay consistent across the checkpoint: it still sees
        // only what was visible when it began, even though the writer's row has since been
        // materialized into the durable catalog and the version chain garbage-collected up to the
        // reader's floor.
        ReadRows(reader, sql).Select(row => row[0].AsText()).Should().Equal("base");
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().BeGreaterThan(0);
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);
        Execute(reader, "ROLLBACK;");

        // Once the reader closes, a fresh transaction sees both rows through the same durable
        // index-join path.
        Execute(reader, "BEGIN CONCURRENT;");
        database.ResetJoinOrderDiagnostics();
        ReadRows(reader, sql).Select(row => row[0].AsText()).Should().Equal("base", "from-writer");
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);
        Execute(reader, "ROLLBACK;");
    }

    [Test]
    public void SingleTableMvccIndexScanUsesTheDurableFullScanAccessorWithoutMaterialization()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("mvcc-single-table-scan.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, k INTEGER, payload TEXT);");
        Execute(connection, "CREATE INDEX items_k ON items(k);");
        Execute(connection, "INSERT INTO items VALUES (1, 3, 'c'), (2, 1, 'a'), (3, 2, 'b');");
        Execute(connection, "PRAGMA journal_mode=mvcc;");
        Execute(connection, "ANALYZE;");

        Execute(connection, "BEGIN CONCURRENT;");
        Execute(connection, "INSERT INTO items VALUES (4, 4, 'd');");
        Execute(connection, "DELETE FROM items WHERE id = 2;");

        const string sql =
            """
            SELECT items.payload FROM items INDEXED BY items_k ORDER BY items.k;
            """;
        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, sql).Select(row => row[0].AsText()).Should().Equal("b", "c", "d");
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);

        Execute(connection, "ROLLBACK;");
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        _ = statement.Step();
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
}
