using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedArbitraryDepthTableInteriorInsertionTests
{
    private const int PageSize = SqlitePageSize.Minimum;
    private const long FirstRowId = 9_000_000_000_000_000_000L;
    private const int RowCount = 17;

    [Test]
    public void DeepMiddleLeafInsertionPreservesTopologyReopensAndPassesExternalIntegrity()
    {
        var path = CreateDatabasePath();
        try
        {
            var target = CreatePreparedTopology(path, PhysicalFileSystem.Instance);
            DeleteTarget(path, PhysicalFileSystem.Instance, target);
            byte[] rootBefore;
            byte[] parentBefore;
            using (var pager = SqlitePager.Open(
                       PhysicalFileSystem.Instance,
                       path,
                       path + "-wal",
                       readOnly: true))
            {
                rootBefore = pager.ReadCommittedPage(target.RootPage);
                parentBefore = pager.ReadCommittedPage(target.ParentPage);
            }

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
                Execute(connection, $"INSERT INTO t VALUES ({target.RowId}, '{ValueFor(target.RowId)}');");

            AssertTopologyAndRows(path, PhysicalFileSystem.Instance, target, rootBefore, parentBefore);
            using (var reopened = EmbeddedDatabase.OpenFile(path, readOnly: true))
            using (var connection = reopened.Connect())
            {
                Scalar(connection, "SELECT COUNT(*) FROM t;").Should().Be(RowCount);
                Scalar(connection, $"SELECT id FROM t WHERE id = {target.RowId};").Should().Be(target.RowId);
            }

            VerifyWithSqlite(path, target.RowId);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void EveryDeepInsertionWalFrameInterruptionRetainsThePriorTopology()
    {
        // This path stages exactly the changed leaf and page one.  Fail both
        // frames: page one is deliberately last, so neither interruption commits.
        for (var failedFrame = 1; failedFrame <= 2; failedFrame++)
        {
            var faults = new DeterministicFaultInjector();
            var fileSystem = new InMemoryFileSystem(faults);
            var path = $"deep-middle-leaf-insertion-{failedFrame}.db";
            var target = CreatePreparedTopology(path, fileSystem);
            DeleteTarget(path, fileSystem, target);

            using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = database.Connect())
            {
                faults.FailOnOccurrence(
                    FileSystemOperation.Write,
                    faults.GetOperationCount(FileSystemOperation.Write) + failedFrame);
                Assert.Throws<IOException>(() =>
                    Execute(connection, $"INSERT INTO t VALUES ({target.RowId}, '{ValueFor(target.RowId)}');"));
            }

            using (var recovered = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = recovered.Connect())
            {
                Scalar(connection, "SELECT COUNT(*) FROM t;").Should().Be(RowCount - 1);
                QueryCount(connection, $"SELECT COUNT(*) FROM t WHERE id = {target.RowId};").Should().Be(0);
            }

            using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
            var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            var leaf = SqliteTableLeafPageView.Parse(
                pager.ReadCommittedPage(target.LeafPage),
                header.UsableSpace);
            leaf.Search(target.RowId).IsExact.Should().BeFalse();
            leaf.Cells.Should().ContainSingle();
        }
    }

    private static TopologyTarget CreatePreparedTopology(string path, IFileSystem fileSystem)
    {
        var header = SqliteDatabaseHeader.CreateDefault() with { PageSize = PageSize };
        using (SqlitePager.Create(
                   fileSystem,
                   path,
                   path + "-wal",
                   SqliteWalHeader.Create(PageSize, salt1: 0x1234_5678, salt2: 0x9ABC_DEF0),
                   header))
        {
        }

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, BuildInsert());
        }

        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: false);
        var sourceHeader = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var rootPage = FindTableRootPage(pager.ReadCommittedPage(1), sourceHeader);
        var sourceRootPage = pager.ReadCommittedPage(rootPage);
        var sourceRoot = SqliteTableLeafPageView.Parse(sourceRootPage, sourceHeader.UsableSpace);
        const int leafCount = 16;
        if (rootPage != 2
            || sourceHeader.DatabaseSizeInPages != rootPage
            || sourceRoot.Cells.Count != RowCount
            || sourceRoot.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null))
        {
            throw new InvalidOperationException("The deep insertion test requires one local table-root leaf.");
        }

        var pageImages = new List<SqlitePageImage>();
        var children = new List<TreeChild>(leafCount);
        var nextPageNumber = checked(sourceHeader.DatabaseSizeInPages + 1);
        var sourceCellIndex = 0;
        uint targetLeafPage = 0;
        long targetRowId = 0;
        for (var leafIndex = 0; leafIndex < leafCount; leafIndex++)
        {
            var cellCount = leafIndex == 7 ? 2 : 1;
            var cells = sourceRoot.Cells
                .Skip(sourceCellIndex)
                .Take(cellCount)
                .Select(cell => cell.Cell)
                .ToArray();
            sourceCellIndex += cellCount;

            var builder = new SqliteTableLeafPageBuilder(sourceHeader.PageSize, sourceHeader.UsableSpace);
            foreach (var cell in cells)
                builder.Append(cell);

            var pageNumber = nextPageNumber++;
            pageImages.Add(new SqlitePageImage(pageNumber, builder.Build()));
            children.Add(new TreeChild(pageNumber, cells[^1].RowId));
            if (leafIndex == 7)
            {
                targetLeafPage = pageNumber;
                targetRowId = cells[0].RowId;
            }
        }

        if (sourceCellIndex != sourceRoot.Cells.Count || targetLeafPage == 0)
            throw new InvalidOperationException("The deep insertion test did not create its target leaf.");

        uint targetParentPage = 0;
        for (var level = 1; level < 4; level++)
        {
            var parents = new List<TreeChild>(children.Count / 2);
            for (var childIndex = 0; childIndex < children.Count; childIndex += 2)
            {
                var left = children[childIndex];
                var right = children[childIndex + 1];
                var builder = new SqliteTableInteriorPageBuilder(
                    sourceHeader.PageSize,
                    sourceHeader.UsableSpace,
                    right.PageNumber);
                builder.Append(SqliteTableInteriorCell.Create(left.PageNumber, left.MaximumRowId));

                var pageNumber = nextPageNumber++;
                pageImages.Add(new SqlitePageImage(pageNumber, builder.Build()));
                parents.Add(new TreeChild(pageNumber, right.MaximumRowId));
                if (level == 1
                    && (left.PageNumber == targetLeafPage || right.PageNumber == targetLeafPage))
                    targetParentPage = pageNumber;
            }

            children = parents;
        }

        if (children.Count != 2 || targetParentPage == 0)
            throw new InvalidOperationException("The deep insertion test could not build a four-level topology.");

        var rootBuilder = new SqliteTableInteriorPageBuilder(
            sourceHeader.PageSize,
            sourceHeader.UsableSpace,
            children[1].PageNumber);
        rootBuilder.Append(SqliteTableInteriorCell.Create(children[0].PageNumber, children[0].MaximumRowId));
        var replacementRootPage = sourceRootPage.ToArray();
        rootBuilder.WriteTo(replacementRootPage);

        var targetPageCount = checked(nextPageNumber - 1);
        var replacementSchemaPage = pager.ReadCommittedPage(1);
        (sourceHeader with
        {
            ChangeCounter = sourceHeader.ChangeCounter + 1,
            DatabaseSizeInPages = targetPageCount,
            VersionValidFor = sourceHeader.ChangeCounter + 1,
        }).WriteTo(replacementSchemaPage);

        using (var transaction = pager.BeginTransaction(targetPageCount))
        {
            foreach (var image in pageImages)
                transaction.WritePage(image.PageNumber, image.Page.Span);
            transaction.WritePage(rootPage, replacementRootPage);
            transaction.WritePage(1, replacementSchemaPage);
            transaction.Commit();
        }

        return new TopologyTarget(rootPage, targetParentPage, targetLeafPage, targetRowId);
    }

    private static void DeleteTarget(string path, IFileSystem fileSystem, TopologyTarget target)
    {
        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var connection = database.Connect();
        Execute(connection, $"DELETE FROM t WHERE id = {target.RowId};");
        Scalar(connection, "SELECT COUNT(*) FROM t;").Should().Be(RowCount - 1);
    }

    private static void AssertTopologyAndRows(
        string path,
        IFileSystem fileSystem,
        TopologyTarget target,
        ReadOnlySpan<byte> rootBefore,
        ReadOnlySpan<byte> parentBefore)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        header.FreelistPageCount.Should().Be(0);
        header.FirstFreelistTrunkPage.Should().Be(0);
        pager.ReadCommittedPage(target.RootPage).Should().Equal(rootBefore.ToArray());
        pager.ReadCommittedPage(target.ParentPage).Should().Equal(parentBefore.ToArray());

        var leaf = SqliteTableLeafPageView.Parse(
            pager.ReadCommittedPage(target.LeafPage),
            header.UsableSpace);
        leaf.Cells.Select(cell => cell.Cell.RowId).Should().Equal(
            target.RowId,
            target.RowId + 1);
        ReadTableHeight(pager, header, target.RootPage, new HashSet<uint>()).Should().Be(5);
    }

    private static int ReadTableHeight(
        SqlitePager pager,
        SqliteDatabaseHeader header,
        uint pageNumber,
        ISet<uint> seenPages)
    {
        seenPages.Add(pageNumber).Should().BeTrue();
        var page = pager.ReadCommittedPage(pageNumber);
        if (SqliteBtreePageHeader.Parse(page).PageType == SqliteBtreePageType.TableLeaf)
            return 1;

        var interior = SqliteTableInteriorPageView.Parse(page, header.UsableSpace);
        var childHeights = interior.Cells
            .Select(cell => ReadTableHeight(pager, header, cell.Cell.LeftChildPage, seenPages))
            .Append(ReadTableHeight(pager, header, interior.Header.RightMostChildPage, seenPages))
            .ToArray();
        childHeights.Should().OnlyContain(height => height == childHeights[0]);
        return childHeights[0] + 1;
    }

    private static uint FindTableRootPage(ReadOnlySpan<byte> schemaPage, SqliteDatabaseHeader header)
    {
        var schema = SqliteTableLeafPageView.Parse(schemaPage, header.UsableSpace, isFirstPage: true);
        return checked((uint)schema.Cells
            .Select(cell => SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding))
            .Single(values => values[0].AsText() == "table" && values[1].AsText() == "t")[3]
            .AsInteger());
    }

    private static void VerifyWithSqlite(string path, long targetRowId)
    {
        using var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
        sqlite.Open();
        using (var integrity = sqlite.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            integrity.ExecuteScalar().Should().Be("ok");
        }

        using var count = sqlite.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) FROM t WHERE id = {targetRowId};";
        Convert.ToInt64(count.ExecuteScalar()).Should().Be(1);
    }

    private static string BuildInsert()
        => $"INSERT INTO t VALUES {string.Join(", ", Enumerable.Range(0, RowCount)
            .Select(index => $"({FirstRowId + index}, '{ValueFor(FirstRowId + index)}')"))};";

    private static string ValueFor(long rowId) => new('x', 10);

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private static long Scalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static long QueryCount(EmbeddedConnection connection, string sql) => Scalar(connection, sql);

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "managed-arbitrary-depth-table-interior-insertion-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():N}.db");
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }

    private sealed record TreeChild(uint PageNumber, long MaximumRowId);

    private sealed record TopologyTarget(uint RootPage, uint ParentPage, uint LeafPage, long RowId);
}
