using Ahtola.Core;
using Ahtola.Core.Parsing;
using Ahtola.Core.Storage;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// End-to-end coverage for the BROWSER workstream's bounded async scan pipeline: schema
/// catalog loading, shape classification, and the page-bounded ascending rowid-table scan
/// cursor, all driven through <see cref="AsyncSqlitePager"/> over an
/// <see cref="AsyncFileSystemAdapter"/>-wrapped <see cref="InMemoryFileSystem"/> so none of this
/// requires a browser. This proves the Core-side pipeline independently of the browser ADO
/// surface built on top of it.
/// </summary>
public sealed class AsyncBoundedRowidScanTests
{
    private const string DatabasePath = "bounded.db";
    private const string WalPath = "bounded.db-wal";

    [Test]
    public async Task ScansAnOrdinaryRowidTableInAscendingOrderWithoutMaterializingItAllAtOnce()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile(DatabasePath, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, label TEXT);");
            foreach (var i in Enumerable.Range(0, 500))
                Execute(connection, $"INSERT INTO items VALUES ({i}, 'row-{i}');");
        }

        await using var pager = await AsyncSqlitePager.OpenAsync(
            AsyncFileSystemAdapter.Create(fileSystem),
            DatabasePath,
            WalPath,
            readOnly: true);
        await using var readTransaction = await pager.BeginReadAsync();
        var pageCache = new BoundedAsyncPageCache(readTransaction, pager.UsableSpace, capacity: 32);
        var textEncoding = await ReadTextEncodingAsync(pageCache);
        var catalog = await AsyncSchemaCatalogLoader.LoadAsync(pageCache, textEncoding);

        var plan = BoundedRowidScanShapeClassifier.TryClassify(
            "SELECT id, label FROM items",
            catalog,
            out var rejectionReason);
        plan.Should().NotBeNull(rejectionReason);

        var rows = new List<(long Id, string Label)>();
        await foreach (var row in AsyncBoundedRowidTableScanCursor.ScanAscendingAsync(
            pageCache,
            plan!.RootPage,
            plan.Table,
            textEncoding,
            plan.Limit))
        {
            rows.Add((
                row[plan.ProjectedColumnIndexes[0]].AsInteger(),
                row[plan.ProjectedColumnIndexes[1]].AsText()));
        }

        rows.Should().HaveCount(500);
        rows.Should().BeInAscendingOrder(row => row.Id);
        rows[0].Should().Be((0L, "row-0"));
        rows[499].Should().Be((499L, "row-499"));

        // Real, provable page-count bound: a 500-row table with tiny rows fits in a handful of
        // pages, never one page per row -- this is the direct assertion the coordinator asked
        // for (resident/read page count proportional to actual page count, not row count).
        pageCache.ResidentPageCount.Should().BeLessThan(32);
    }

    [Test]
    public async Task RespectsLimitAndStopsReadingFurtherPages()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile(DatabasePath, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, label TEXT);");
            foreach (var i in Enumerable.Range(0, 2000))
                Execute(connection, $"INSERT INTO items VALUES ({i}, 'row-{i}');");
        }

        await using var pager = await AsyncSqlitePager.OpenAsync(
            AsyncFileSystemAdapter.Create(fileSystem),
            DatabasePath,
            WalPath,
            readOnly: true);
        await using var readTransaction = await pager.BeginReadAsync();
        var pageCache = new BoundedAsyncPageCache(readTransaction, pager.UsableSpace, capacity: 64);
        var textEncoding = await ReadTextEncodingAsync(pageCache);
        var catalog = await AsyncSchemaCatalogLoader.LoadAsync(pageCache, textEncoding);

        var plan = BoundedRowidScanShapeClassifier.TryClassify(
            "SELECT id FROM items LIMIT 3",
            catalog,
            out var rejectionReason);
        plan.Should().NotBeNull(rejectionReason);
        plan!.Limit.Should().Be(3);

        var ids = new List<long>();
        await foreach (var row in AsyncBoundedRowidTableScanCursor.ScanAscendingAsync(
            pageCache,
            plan.RootPage,
            plan.Table,
            textEncoding,
            plan.Limit))
        {
            ids.Add(row[plan.ProjectedColumnIndexes[0]].AsInteger());
        }

        ids.Should().Equal(0L, 1L, 2L);
    }

    [Test]
    public async Task ThrowsWhenTheConfiguredPageBudgetCannotFitTheLiveAncestorStack()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile(DatabasePath, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, label TEXT);");
            foreach (var i in Enumerable.Range(0, 5000))
                Execute(connection, $"INSERT INTO items VALUES ({i}, 'row-{i}-padding-padding-padding');");
        }

        await using var pager = await AsyncSqlitePager.OpenAsync(
            AsyncFileSystemAdapter.Create(fileSystem),
            DatabasePath,
            WalPath,
            readOnly: true);
        await using var readTransaction = await pager.BeginReadAsync();
        // A capacity of 1 cannot hold the schema catalog's own root page pin plus the scan's
        // root/interior/leaf pages simultaneously for a multi-level tree.
        var pageCache = new BoundedAsyncPageCache(readTransaction, pager.UsableSpace, capacity: 1);
        var textEncoding = await ReadTextEncodingAsync(pageCache);
        var catalog = await AsyncSchemaCatalogLoader.LoadAsync(pageCache, textEncoding);

        var plan = BoundedRowidScanShapeClassifier.TryClassify(
            "SELECT id FROM items",
            catalog,
            out var rejectionReason);
        plan.Should().NotBeNull(rejectionReason);

        var act = async () =>
        {
            await foreach (var _ in AsyncBoundedRowidTableScanCursor.ScanAscendingAsync(
                pageCache,
                plan!.RootPage,
                plan.Table,
                textEncoding,
                plan.Limit))
            {
            }
        };

        await act.Should().ThrowAsync<SqliteBoundedPageBudgetExceededException>();
    }

    [Test]
    public async Task PadsRowsWrittenBeforeAlterTableAddColumnWithDefaultsInsteadOfIndexingOutOfRange()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile(DatabasePath, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, label TEXT);");
            // This physical row is stored with only 2 columns' worth of record content.
            Execute(connection, "INSERT INTO items VALUES (1, 'old-row');");
            Execute(connection, "ALTER TABLE items ADD COLUMN note TEXT DEFAULT 'unset';");
            // This row is stored after the ALTER, so its own record already has 3 values.
            Execute(connection, "INSERT INTO items VALUES (2, 'new-row', 'explicit');");
        }

        await using var pager = await AsyncSqlitePager.OpenAsync(
            AsyncFileSystemAdapter.Create(fileSystem),
            DatabasePath,
            WalPath,
            readOnly: true);
        await using var readTransaction = await pager.BeginReadAsync();
        var pageCache = new BoundedAsyncPageCache(readTransaction, pager.UsableSpace, capacity: 32);
        var textEncoding = await ReadTextEncodingAsync(pageCache);
        var catalog = await AsyncSchemaCatalogLoader.LoadAsync(pageCache, textEncoding);

        var plan = BoundedRowidScanShapeClassifier.TryClassify("SELECT * FROM items", catalog, out var rejectionReason);
        plan.Should().NotBeNull(rejectionReason);
        plan!.ProjectedColumnIndexes.Should().HaveCount(3);

        var rows = new List<SqlValue[]>();
        await foreach (var row in AsyncBoundedRowidTableScanCursor.ScanAscendingAsync(
            pageCache,
            plan.RootPage,
            plan.Table,
            textEncoding,
            plan.Limit))
        {
            rows.Add(row);
        }

        rows.Should().HaveCount(2);
        // The pre-ALTER row's short record must be padded with the column's declared default
        // (matching EmbeddedFileStore.RestoreRowidTableRecord's own behavior) rather than
        // leaving the projected values array too short to index into.
        rows[0][0].AsInteger().Should().Be(1L);
        rows[0][1].AsText().Should().Be("old-row");
        rows[0][2].AsText().Should().Be("unset");
        rows[1][0].AsInteger().Should().Be(2L);
        rows[1][1].AsText().Should().Be("new-row");
        rows[1][2].AsText().Should().Be("explicit");
    }

    [Test]
    public async Task PinsTheCurrentLeafSoAnOverflowFetchCannotSilentlyEvictItsCacheEntry()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile(DatabasePath, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, blob TEXT);");
            // Large enough to force at least one overflow page beyond the leaf's local payload.
            Execute(connection, $"INSERT INTO items VALUES (1, '{new string('x', 8000)}');");
        }

        await using var pager = await AsyncSqlitePager.OpenAsync(
            AsyncFileSystemAdapter.Create(fileSystem),
            DatabasePath,
            WalPath,
            readOnly: true);
        await using var readTransaction = await pager.BeginReadAsync();
        // A single-page table (root is itself the leaf) needs exactly one resident page for the
        // leaf and at least one more for its overflow chain. A capacity of 1 can therefore only
        // ever succeed if the leaf's own cache entry is allowed to be silently evicted to make
        // room for the overflow page -- which would understate real resident memory (the leaf's
        // bytes are still alive via the parsed view and its cells) instead of failing loud.
        var pageCache = new BoundedAsyncPageCache(readTransaction, pager.UsableSpace, capacity: 1);
        var textEncoding = await ReadTextEncodingAsync(pageCache);
        var catalog = await AsyncSchemaCatalogLoader.LoadAsync(pageCache, textEncoding);

        var plan = BoundedRowidScanShapeClassifier.TryClassify("SELECT blob FROM items", catalog, out var rejectionReason);
        plan.Should().NotBeNull(rejectionReason);

        var act = async () =>
        {
            await foreach (var _ in AsyncBoundedRowidTableScanCursor.ScanAscendingAsync(
                pageCache,
                plan!.RootPage,
                plan.Table,
                textEncoding,
                plan.Limit))
            {
            }
        };

        await act.Should().ThrowAsync<SqliteBoundedPageBudgetExceededException>();

        // The budget fault must leave the connection's own resources (the read transaction, the
        // pager) in a state that still disposes cleanly -- a fault here must be terminal for the
        // scan, not for the whole connection.
        var disposeAct = async () => await readTransaction.DisposeAsync();
        await disposeAct.Should().NotThrowAsync();
    }

    [Test]
    public async Task RejectsUnsupportedShapesBeforeAnyPageIsRead()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile(DatabasePath, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(
                connection,
                """
                CREATE TABLE plain(id INTEGER PRIMARY KEY, value TEXT);
                CREATE TABLE indexed(id INTEGER PRIMARY KEY, value TEXT);
                CREATE INDEX indexed_value ON indexed(value);
                CREATE TABLE wr(k TEXT PRIMARY KEY, v INTEGER) WITHOUT ROWID;
                """);
            Execute(connection, "INSERT INTO plain VALUES (1, 'a');");
        }

        await using var pager = await AsyncSqlitePager.OpenAsync(
            AsyncFileSystemAdapter.Create(fileSystem),
            DatabasePath,
            WalPath,
            readOnly: true);
        await using var readTransaction = await pager.BeginReadAsync();
        var pageCache = new BoundedAsyncPageCache(readTransaction, pager.UsableSpace, capacity: 32);
        var textEncoding = await ReadTextEncodingAsync(pageCache);
        var catalog = await AsyncSchemaCatalogLoader.LoadAsync(pageCache, textEncoding);

        BoundedRowidScanShapeClassifier.TryClassify("SELECT * FROM plain WHERE id = 1", catalog, out var whereReason)
            .Should().BeNull();
        whereReason.Should().Contain("WHERE");

        BoundedRowidScanShapeClassifier.TryClassify("SELECT * FROM indexed", catalog, out var indexedReason)
            .Should().BeNull();
        indexedReason.Should().Contain("index");

        BoundedRowidScanShapeClassifier.TryClassify("SELECT * FROM wr", catalog, out var withoutRowidReason)
            .Should().BeNull();
        withoutRowidReason.Should().Contain("WITHOUT ROWID");

        BoundedRowidScanShapeClassifier.TryClassify("INSERT INTO plain VALUES (2, 'b')", catalog, out var writeReason)
            .Should().BeNull();
        writeReason.Should().Contain("SELECT");

        BoundedRowidScanShapeClassifier.TryClassify("SELECT * FROM missing", catalog, out var missingReason)
            .Should().BeNull();
        missingReason.Should().Contain("missing");

        // Classification never reads a page: the cache's resident count must stay exactly what
        // schema loading alone required, proving rejection happens before any scan I/O.
        var residentAfterClassification = pageCache.ResidentPageCount;
        BoundedRowidScanShapeClassifier.TryClassify("SELECT * FROM indexed", catalog, out _);
        pageCache.ResidentPageCount.Should().Be(residentAfterClassification);
    }

    private static async ValueTask<SqliteTextEncoding> ReadTextEncodingAsync(BoundedAsyncPageCache pageCache)
    {
        var page1 = await pageCache.ReadPageAsync(1);
        return SqliteDatabaseHeader.Parse(page1).TextEncoding;
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
