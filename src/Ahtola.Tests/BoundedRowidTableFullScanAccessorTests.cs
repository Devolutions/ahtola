using Ahtola.Core;
using Ahtola.Core.Storage;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Verifies <c>EmbeddedFileStore.TryOpenBaseTableFullScanAccessor</c>/
/// <c>ScanCommittedRowidTableAscending</c>: a durable, page-native, ascending-rowid-order scan of
/// an ordinary rowid table's own committed b-tree that decodes rows one at a time instead of
/// materializing the whole table first. This is additive infrastructure only in this commit — it
/// has no live caller yet — so these tests exercise it directly against
/// <c>EmbeddedDatabase.FileStoreForTesting</c> rather than through the query engine.
/// </summary>
[NonParallelizable]
public sealed class BoundedRowidTableFullScanAccessorTests
{
    [Test]
    public void ScansASingleLeafTableInAscendingRowidOrderWithoutMaterializingRows()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "bounded-scan-single-leaf.db";

        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
            foreach (var id in Enumerable.Range(1, 20))
                Execute(connection, $"INSERT INTO t VALUES ({id}, 'row-{id}');");
        }

        var table = database.LiveCatalog.Tables["t"];
        var fileStore = database.FileStoreForTesting!;

        fileStore.TryOpenBaseTableFullScanAccessor(table, sharedSnapshot: null, out var accessor)
            .Should().BeTrue();
        using (accessor)
        {
            accessor.Open();
            var rows = accessor.Scan().ToList();
            rows.Select(row => row.RowId).Should().Equal(Enumerable.Range(1, 20).Select(id => (long?)id));
            rows.Select(row => row.Values[1].AsText())
                .Should()
                .Equal(Enumerable.Range(1, 20).Select(id => $"row-{id}"));
            rows.Select(row => row.Values[0].AsInteger())
                .Should()
                .Equal(Enumerable.Range(1, 20).Select(id => (long)id));
        }
    }

    [Test]
    public void ScansAMultiLevelTableInteriorInAscendingRowidOrder()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "bounded-scan-multi-level.db";

        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
            // Large-enough values force at least one table-interior split at the default page
            // size, matching ManagedTableInteriorFileStoreTests.BuildInsert's approach.
            var rows = Enumerable.Range(1, 120)
                .Select(id => $"({id}, 'row-{id:D3}-{new string('x', 96)}')");
            Execute(connection, $"INSERT INTO t VALUES {string.Join(", ", rows)};");
        }

        var table = database.LiveCatalog.Tables["t"];
        var fileStore = database.FileStoreForTesting!;

        fileStore.TryOpenBaseTableFullScanAccessor(table, sharedSnapshot: null, out var accessor)
            .Should().BeTrue();
        int pageReads = 0;
        using (accessor)
        {
            accessor.Open();
            var scanned = accessor.Scan(pageRead: () => pageReads++).ToList();
            scanned.Select(row => row.RowId).Should().Equal(Enumerable.Range(1, 120).Select(id => (long?)id));
            scanned.Select(row => row.Values[0].AsInteger())
                .Should()
                .Equal(Enumerable.Range(1, 120).Select(id => (long)id));
        }

        // Proves the scan actually walked multiple pages (root + at least two leaves), not a
        // single already-materialized in-memory blob.
        pageReads.Should().BeGreaterThan(2);
    }

    [Test]
    public void OpeningTheScanAccessorDoesNotMaterializeTheTablesInMemoryRows()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "bounded-scan-no-materialize.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
            foreach (var id in Enumerable.Range(1, 10))
                Execute(connection, $"INSERT INTO t VALUES ({id}, 'row-{id}');");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        var table = reopened.LiveCatalog.Tables["t"];
        table.HasPendingRowLoad.Should().BeTrue(
            "physical open must not have materialized this indexless table yet");

        var fileStore = reopened.FileStoreForTesting!;
        fileStore.TryOpenBaseTableFullScanAccessor(table, sharedSnapshot: null, out var accessor)
            .Should().BeTrue();
        using (accessor)
        {
            accessor.Open();
            accessor.Scan().Count().Should().Be(10);
        }

        table.HasPendingRowLoad.Should().BeTrue(
            "the bounded scan accessor must decode rows on demand without ever populating " +
            "EmbeddedTable.Rows/RowIds");
    }

    [Test]
    public void ScansRecomputeVirtualGeneratedColumns()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "bounded-scan-generated-columns.db";

        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using (var connection = database.Connect())
        {
            Execute(
                connection,
                "CREATE TABLE t(id INTEGER PRIMARY KEY, a INTEGER, b INTEGER AS (a * 2));");
            Execute(connection, "INSERT INTO t(id, a) VALUES (1, 5), (2, 7);");
        }

        var table = database.LiveCatalog.Tables["t"];
        var fileStore = database.FileStoreForTesting!;

        fileStore.TryOpenBaseTableFullScanAccessor(table, sharedSnapshot: null, out var accessor)
            .Should().BeTrue();
        using (accessor)
        {
            accessor.Open();
            var rows = accessor.Scan().ToList();
            rows.Select(row => row.Values[2].AsInteger()).Should().Equal(10L, 14L);
        }
    }

    [Test]
    public void ScansDecodeOverflowPayloads()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "bounded-scan-overflow.db";
        var large = new string('y', 8000);

        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
            using var statement = connection.Prepare("INSERT INTO t VALUES (1, ?);");
            statement.Bind(1, SqlValue.Text(large));
            statement.Step();
        }

        var table = database.LiveCatalog.Tables["t"];
        var fileStore = database.FileStoreForTesting!;

        fileStore.TryOpenBaseTableFullScanAccessor(table, sharedSnapshot: null, out var accessor)
            .Should().BeTrue();
        using (accessor)
        {
            accessor.Open();
            var rows = accessor.Scan().ToList();
            rows.Should().ContainSingle();
            rows[0].Values[1].AsText().Should().Be(large);
        }
    }

    [Test]
    public void CannotOpenTheAccessorForAWithoutRowidTable()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "bounded-scan-without-rowid.db";

        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(k TEXT PRIMARY KEY, v INTEGER) WITHOUT ROWID;");
            Execute(connection, "INSERT INTO t VALUES ('a', 1);");
        }

        var table = database.LiveCatalog.Tables["t"];
        var fileStore = database.FileStoreForTesting!;

        fileStore.TryOpenBaseTableFullScanAccessor(table, sharedSnapshot: null, out _)
            .Should().BeFalse(
                "an ordinary rowid-table scan accessor must reject a WITHOUT ROWID table; " +
                "its root page is an index b-tree, not a table b-tree");
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
            {
                while (statement.Step() == StatementStepResult.Row)
                {
                }
            }
        }
    }
}
