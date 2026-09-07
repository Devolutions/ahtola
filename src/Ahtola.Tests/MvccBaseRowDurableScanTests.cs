using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

/// <summary>
/// Focused coverage for wiring the bounded, page-native full-scan accessor (introduced in
/// c6a2235's <c>EmbeddedFileStore.TryOpenBaseTableFullScanAccessor</c>) into
/// <c>EmbeddedDatabase.GetNamedTableRows</c>'s concurrent-MVCC base-row path: an ordinary
/// <c>SELECT ... FROM t</c> with no index at all — the "ordinary MVCC table scans merge heap
/// catalog rows" gap named in the original PAGE analysis — must serve its base rows directly
/// from the transaction's own pinned durable b-tree snapshot instead of materializing
/// <c>EmbeddedTable.Rows</c> into memory first, exactly mirroring the zero-materialization
/// contract <see cref="MvccDirectIndexAccessTests"/> already proves for indexed MVCC scans (see
/// <see cref="VdbeJoinIndexSeekMetrics.DurableCursorPlans"/> greater than zero as the evidence a
/// bounded cursor plan was actually created).
/// </summary>
public sealed class MvccBaseRowDurableScanTests
{
    [Test]
    public void OrdinaryUnindexedRowidTableScanUsesTheDurableSnapshot()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("mvcc-base-rowid-scan.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
        // Large-enough values force a multi-level table-interior split at the default page
        // size, matching ManagedTableInteriorFileStoreTests.BuildInsert's approach — proving
        // this exercises the real recursive interior traversal, not just a single leaf.
        var rows = Enumerable.Range(1, 120)
            .Select(id => $"({id}, 'row-{id:D3}-{new string('x', 96)}')");
        Execute(connection, $"INSERT INTO t VALUES {string.Join(", ", rows)};");
        Execute(connection, "PRAGMA journal_mode=mvcc;");

        Execute(connection, "BEGIN CONCURRENT;");
        database.ResetJoinOrderDiagnostics();
        var results = ReadRows(connection, "SELECT id, value FROM t ORDER BY id;");
        results.Select(row => row[0].AsInteger()).Should().Equal(Enumerable.Range(1, 120).Select(id => (long)id));
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().BeGreaterThan(0);
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);

        Execute(connection, "COMMIT;");
    }

    [Test]
    public void OrdinaryUnindexedWithoutRowidTableScanUsesTheDurableSnapshot()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("mvcc-base-wr-scan.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(k TEXT PRIMARY KEY, v INTEGER) WITHOUT ROWID;");
        foreach (var i in Enumerable.Range(0, 30))
            Execute(connection, $"INSERT INTO t VALUES ('key-{i:D3}', {i});");
        Execute(connection, "PRAGMA journal_mode=mvcc;");

        Execute(connection, "BEGIN CONCURRENT;");
        database.ResetJoinOrderDiagnostics();
        var results = ReadRows(connection, "SELECT k, v FROM t ORDER BY k;");
        results.Select(row => row[1].AsInteger()).Should().Equal(Enumerable.Range(0, 30).Select(i => (long)i));
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().BeGreaterThan(0);
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);

        Execute(connection, "COMMIT;");
    }

    [Test]
    public void LocalMutationsAreStillMergedOntoTheDurableBaseScan()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("mvcc-base-scan-mutations.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
        Execute(
            connection,
            "INSERT INTO t VALUES (1, 'stays'), (2, 'will-update'), (3, 'will-delete');");
        Execute(connection, "PRAGMA journal_mode=mvcc;");

        Execute(connection, "BEGIN CONCURRENT;");
        Execute(connection, "INSERT INTO t VALUES (4, 'new-in-txn');");
        Execute(connection, "UPDATE t SET value = 'updated' WHERE id = 2;");
        Execute(connection, "DELETE FROM t WHERE id = 3;");

        database.ResetJoinOrderDiagnostics();
        var results = ReadRows(connection, "SELECT value FROM t ORDER BY id;");
        results.Select(row => row[0].AsText()).Should().Equal("stays", "updated", "new-in-txn");
        // The durable base scan still ran (for the unchanged rows); the transaction's own writes
        // are layered on top via the MvStore overlay, not by falling back to the heap.
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().BeGreaterThan(0);

        Execute(connection, "ROLLBACK;");
    }

    [Test]
    public void AConcurrentReadersUnindexedBaseScanStaysIsolatedFromAPeersLaterCommit()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("mvcc-base-scan-isolation.db", fileSystem);
        using var writer = database.Connect();
        using var reader = database.Connect();

        Execute(writer, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
        Execute(writer, "INSERT INTO t VALUES (1, 'a');");
        Execute(writer, "PRAGMA journal_mode=mvcc;");

        Execute(reader, "BEGIN CONCURRENT;");
        Execute(writer, "BEGIN CONCURRENT;");
        Execute(writer, "INSERT INTO t VALUES (2, 'b');");
        Execute(writer, "COMMIT;");

        database.ResetJoinOrderDiagnostics();
        var results = ReadRows(reader, "SELECT id FROM t ORDER BY id;");
        results.Select(row => row[0].AsInteger()).Should().Equal(1L);
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().BeGreaterThan(0);
        Execute(reader, "COMMIT;");

        ReadRows(reader, "SELECT id FROM t ORDER BY id;")
            .Select(row => row[0].AsInteger())
            .Should()
            .Equal(1L, 2L);
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
