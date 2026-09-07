using Ahtola.Core;
using Ahtola.Core.Storage;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Regression coverage for a review-reported crash: <c>AsyncSchemaCatalogLoader.WalkAsync</c>
/// previously recursed once per child (per interior cell, plus the rightmost child) with no
/// depth or cycle guard, unlike the synchronous <see cref="SqliteTableBtreeCursor"/> and
/// <see cref="AsyncBoundedRowidTableScanCursor"/>, both of which already bound traversal depth.
/// A corrupt <c>sqlite_schema</c> b-tree containing a self-referencing (or long-cyclic) interior
/// page could drive the old recursive walk into unbounded recursion — a
/// <see cref="StackOverflowException"/>, which .NET cannot catch, crashing the process before
/// the classifier or any caller's <c>try</c>/<c>catch</c> ever runs. The fixed loader is
/// iterative with the same <c>MaximumDepth = 64</c> ceiling the other two traversals already
/// enforce, so a cycle is caught as an ordinary, catchable <see cref="InvalidDataException"/>.
/// </summary>
public sealed class AsyncSchemaCatalogLoaderCorruptionTests
{
    private const string DatabasePath = "corrupt-schema.db";
    private const string WalPath = "corrupt-schema.db-wal";

    [Test]
    public async Task ASelfReferencingSchemaInteriorPageThrowsInsteadOfCrashing()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile(DatabasePath, fileSystem))
        using (var connection = database.Connect())
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY);");

        CorruptPage1IntoASelfReferencingInteriorPage(fileSystem);

        await using var pager = await AsyncSqlitePager.OpenAsync(
            AsyncFileSystemAdapter.Create(fileSystem),
            DatabasePath,
            WalPath,
            readOnly: true);
        await using var readTransaction = await pager.BeginReadAsync();
        var pageCache = new BoundedAsyncPageCache(readTransaction, pager.UsableSpace, capacity: 128);

        // Bounded by construction (MaximumDepth = 64 iterations, not recursion depth): this call
        // must return (by throwing) rather than hang or crash the test process.
        var act = async () => await AsyncSchemaCatalogLoader.LoadAsync(pageCache, SqliteTextEncoding.Utf8);

        var assertion = await act.Should().ThrowAsync<InvalidDataException>();
        assertion.Which.Message.Should().ContainAny("deeper than", "cycle");
    }

    [Test]
    public async Task ALongCyclicSchemaInteriorChainThrowsInsteadOfCrashing()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile(DatabasePath, fileSystem))
        using (var connection = database.Connect())
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY);");

        CorruptIntoATwoPageInteriorCycle(fileSystem);

        await using var pager = await AsyncSqlitePager.OpenAsync(
            AsyncFileSystemAdapter.Create(fileSystem),
            DatabasePath,
            WalPath,
            readOnly: true);
        await using var readTransaction = await pager.BeginReadAsync();
        var pageCache = new BoundedAsyncPageCache(readTransaction, pager.UsableSpace, capacity: 128);

        var act = async () => await AsyncSchemaCatalogLoader.LoadAsync(pageCache, SqliteTextEncoding.Utf8);

        var assertion = await act.Should().ThrowAsync<InvalidDataException>();
        assertion.Which.Message.Should().ContainAny("deeper than", "cycle");
    }

    /// <summary>
    /// Rewrites page 1's b-tree portion (preserving the 100-byte database header) into a
    /// zero-cell table-interior page whose mandatory rightmost child points back at page 1
    /// itself — an interior page that is its own child.
    /// </summary>
    private static void CorruptPage1IntoASelfReferencingInteriorPage(InMemoryFileSystem fileSystem)
    {
        using var file = fileSystem.OpenFile(DatabasePath, FileOpenMode.OpenExisting);
        var page = new byte[SqlitePageSize.Default];
        file.Read(0, page);
        var header = SqliteDatabaseHeader.Parse(page);

        var builder = new SqliteTableInteriorPageBuilder(
            SqlitePageSize.Default,
            header.UsableSpace,
            rightMostChildPage: 1,
            isFirstPage: true);
        builder.WriteTo(page);
        file.Write(0, page);
    }

    /// <summary>
    /// Rewrites page 1 into a zero-cell interior page whose rightmost child is page 2, and
    /// writes page 2 as a zero-cell interior page whose rightmost child is page 1 — a two-page
    /// cycle that never reaches a leaf.
    /// </summary>
    private static void CorruptIntoATwoPageInteriorCycle(InMemoryFileSystem fileSystem)
    {
        using var file = fileSystem.OpenFile(DatabasePath, FileOpenMode.OpenExisting);
        var page1 = new byte[SqlitePageSize.Default];
        file.Read(0, page1);
        var header = SqliteDatabaseHeader.Parse(page1);

        var page1Builder = new SqliteTableInteriorPageBuilder(
            SqlitePageSize.Default,
            header.UsableSpace,
            rightMostChildPage: 2,
            isFirstPage: true);
        page1Builder.WriteTo(page1);
        file.Write(0, page1);

        var page2Builder = new SqliteTableInteriorPageBuilder(
            SqlitePageSize.Default,
            header.UsableSpace,
            rightMostChildPage: 1,
            isFirstPage: false);
        var page2 = page2Builder.Build();
        if (file.Length < SqlitePageSize.Default * 2L)
            file.SetLength(SqlitePageSize.Default * 2L);
        file.Write(SqlitePageSize.Default, page2);
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
