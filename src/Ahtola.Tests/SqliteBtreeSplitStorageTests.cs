using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public class SqliteBtreeSplitStorageTests
{
    [Test]
    public void TableRootSplitRoutesEveryKeyAndSurvivesCheckpoint()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var pager = CreatePager(fileSystem, "table-root"))
        {
            SeedTableRoot(pager, 1, 3, 5, 7);

            var mutation = new SqliteBtreeSplitWriter(
                pager,
                new SqliteAppendOnlyPageAllocator(pager.CommittedPageCount))
                .PrepareTableLeafSplit(leafPageNumber: 1, leftCellCount: 2);

            mutation.WriteImages.Select(image => image.PageNumber).Should().Equal(2, 3, 1);
            mutation.CommitTo(pager);

            pager.CommittedPageCount.Should().Be(3);
            SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1)).DatabaseSizeInPages.Should().Be(3);
            var root = SqliteTableInteriorPageView.Parse(
                pager.ReadCommittedPage(1),
                SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1)).UsableSpace,
                isFirstPage: true);
            root.Cells.Select(cell => cell.Cell.LeftChildPage).Should().Equal(2);
            root.Cells.Select(cell => cell.Cell.RowId).Should().Equal(3);
            root.Header.RightMostChildPage.Should().Be(3);
            root.SearchChild(1).ChildPage.Should().Be(2);
            root.SearchChild(3).ChildPage.Should().Be(2);
            root.SearchChild(4).ChildPage.Should().Be(3);
            root.SearchChild(7).ChildPage.Should().Be(3);
            ReadLeafRowIds(pager, 2).Should().Equal(1, 3);
            ReadLeafRowIds(pager, 3).Should().Equal(5, 7);

            pager.CheckpointToMainStore().DatabaseSizeInPages.Should().Be(3);
        }

        using var store = SqlitePageStore.Open(fileSystem, "table-root.db", readOnly: true);
        store.PageCount.Should().Be(3);
        var rootAfterCheckpoint = SqliteTableInteriorPageView.Parse(
            store.ReadPage(1),
            store.Header.UsableSpace,
            isFirstPage: true);
        rootAfterCheckpoint.SearchChild(3).ChildPage.Should().Be(2);
        rootAfterCheckpoint.SearchChild(4).ChildPage.Should().Be(3);
    }

    [Test]
    public void TableParentSplitInstallsChildBeforeParentAndPreservesRoutes()
    {
        var fileSystem = new InMemoryFileSystem();
        using var pager = CreatePager(fileSystem, "table-parent");
        SeedTableInteriorRoot(pager);

        var mutation = new SqliteBtreeSplitWriter(
            pager,
            new SqliteAppendOnlyPageAllocator(pager.CommittedPageCount))
            .PrepareTableLeafSplit(leafPageNumber: 2, leftCellCount: 1, parentPageNumber: 1);

        mutation.WriteImages.Select(image => image.PageNumber).Should().Equal(4, 2, 1);
        mutation.CommitTo(pager);

        pager.CommittedPageCount.Should().Be(4);
        var root = SqliteTableInteriorPageView.Parse(
            pager.ReadCommittedPage(1),
            SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1)).UsableSpace,
            isFirstPage: true);
        root.Cells.Select(cell => (cell.Cell.LeftChildPage, cell.Cell.RowId))
            .Should()
            .Equal((2U, 1L), (4U, 3L));
        root.Header.RightMostChildPage.Should().Be(3);
        root.SearchChild(1).ChildPage.Should().Be(2);
        root.SearchChild(2).ChildPage.Should().Be(4);
        root.SearchChild(3).ChildPage.Should().Be(4);
        root.SearchChild(4).ChildPage.Should().Be(3);
        ReadLeafRowIds(pager, 2).Should().Equal(1);
        ReadLeafRowIds(pager, 4).Should().Equal(3);
        ReadLeafRowIds(pager, 3).Should().Equal(5, 7);
    }

    [Test]
    public void FullTableInteriorRootPromotionReopensAndPassesExternalSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath("interior-root-promotion");
        try
        {
            int expectedRowCount;
            using (var pager = CreatePagerAtPath(PhysicalFileSystem.Instance, path))
            {
                var seed = SeedFullTableInteriorRoot(pager);
                expectedRowCount = seed.RowCount + 1;
                var mutation = PrepareTableInteriorRootPromotion(pager, seed);

                mutation.WriteImages.Select(image => image.PageNumber).Should().Equal(
                    seed.SourcePageCount + 1,
                    seed.SourcePageCount + 2,
                    seed.SourcePageCount + 3,
                    seed.RightMostLeafPage,
                    seed.RootPage,
                    1);
                mutation.CommitTo(pager);
                AssertPromotedTable(pager, seed.RootPage, expectedRowCount);
                pager.CheckpointToMainStore();
            }

            using (var reopened = SqlitePager.Open(
                       PhysicalFileSystem.Instance,
                       path,
                       path + "-wal",
                       readOnly: true))
            {
                AssertPromotedTable(reopened, rootPage: 2, expectedRowCount);
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path}");
            sqlite.Open();
            using (var integrity = sqlite.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check;";
                integrity.ExecuteScalar().Should().Be("ok");
            }

            using var count = sqlite.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM t;";
            Convert.ToInt32(count.ExecuteScalar()).Should().Be(expectedRowCount);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void EveryInterruptedFullTableInteriorRootPromotionRecoversThePriorTree()
    {
        for (var failedFrame = 1; failedFrame <= 6; failedFrame++)
        {
            var faults = new DeterministicFaultInjector();
            var fileSystem = new InMemoryFileSystem(faults);
            var path = $"full-interior-root-promotion-{failedFrame}.db";
            int expectedRowCount;
            using (var pager = CreatePagerAtPath(fileSystem, path))
            {
                var seed = SeedFullTableInteriorRoot(pager);
                expectedRowCount = seed.RowCount;
                var mutation = PrepareTableInteriorRootPromotion(pager, seed);
                faults.FailOnOccurrence(
                    FileSystemOperation.Write,
                    faults.GetOperationCount(FileSystemOperation.Write) + failedFrame);

                Assert.Throws<IOException>(() => mutation.CommitTo(pager));
                pager.State.Should().Be(SqlitePagerState.Faulted);
            }

            using var recovered = SqlitePager.Open(fileSystem, path, path + "-wal");
            recovered.CommittedPageCount.Should().BeLessThan(uint.MaxValue);
            AssertUnpromotedTable(recovered, rootPage: 2, expectedRowCount);
        }
    }

    [Test]
    public void EncryptedFullTableInteriorRootPromotionReopensReadOnly()
    {
        using var encryption = AhtolaEncryptionOptions.FromHex(
            AhtolaEncryptionCipher.Aes256Gcm,
            "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
        var fileSystem = new AhtolaEncryptionFileSystem(new InMemoryFileSystem(), encryption);
        const string path = "encrypted-full-interior-root-promotion.db";

        int expectedRowCount;
        using (var pager = CreatePagerAtPath(fileSystem, path))
        {
            var seed = SeedFullTableInteriorRoot(pager);
            expectedRowCount = seed.RowCount + 1;
            PrepareTableInteriorRootPromotion(pager, seed).CommitTo(pager);
        }

        using var reopened = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        AssertPromotedTable(reopened, rootPage: 2, expectedRowCount);
    }

    [Test]
    public void FullTableInteriorRootPromotionCannotBypassReadOnlyPager()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "readonly-full-interior-root-promotion.db";
        int expectedRowCount;
        using (var pager = CreatePagerAtPath(fileSystem, path))
        {
            var seed = SeedFullTableInteriorRoot(pager);
            expectedRowCount = seed.RowCount;
        }

        var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
        using (var readOnly = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
        {
            var seed = ReadFullTableInteriorRootSeed(readOnly);
            var mutation = PrepareTableInteriorRootPromotion(readOnly, seed);
            Assert.Throws<InvalidOperationException>(() => mutation.CommitTo(readOnly));
        }

        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBefore);
        using var reopened = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        AssertUnpromotedTable(reopened, rootPage: 2, expectedRowCount);
    }

    [Test]
    public void CorruptFullTableInteriorRootIsRejectedBeforePromotionWrites()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "corrupt-full-interior-root-promotion.db";
        SqliteDatabaseHeader header;
        using (var pager = CreatePagerAtPath(fileSystem, path))
        {
            _ = SeedFullTableInteriorRoot(pager);
            header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            pager.CheckpointToMainStore();
        }

        using (var store = SqlitePageStore.Open(fileSystem, path))
        {
            var rootPage = store.ReadPage(2);
            var root = SqliteTableInteriorPageView.Parse(rootPage, header.UsableSpace);
            rootPage[root.CellPointers[0] + sizeof(uint)] = 0;
            store.WritePage(2, rootPage);
            store.Flush();
        }

        fileSystem.DeleteFile(path + "-wal");
        using (SqliteWalFile.Create(
                   fileSystem,
                   path + "-wal",
                   SqliteWalHeader.Create(header.PageSize, salt1: 31, salt2: 37)))
        {
        }

        var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
        using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal"))
        {
            var finalPageOne = CreateFinalPageOne(pager);
            Assert.Throws<InvalidDataException>(() =>
                new SqliteBtreeSplitWriter(
                    pager,
                    new SqliteAppendOnlyPageAllocator(pager.CommittedPageCount))
                .PrepareTableInteriorRootRightmostLeafSplit(
                    rootPageNumber: 2,
                    SqliteTableLeafCell.Create(999_999, DataRecord(), header.UsableSpace),
                    finalPageOne));
        }

        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBefore);
    }

    [Test]
    public void IndexRootThenParentSplitRoutesEveryCompleteRecord()
    {
        var fileSystem = new InMemoryFileSystem();
        using var pager = CreatePager(fileSystem, "index");
        var records = new[]
        {
            Record(SqlValue.Integer(1)),
            Record(SqlValue.Integer(3)),
            Record(SqlValue.Integer(5)),
            Record(SqlValue.Integer(7)),
        };
        SeedIndexRoot(pager, records);

        var rootSplit = new SqliteBtreeSplitWriter(
            pager,
            new SqliteAppendOnlyPageAllocator(pager.CommittedPageCount))
            .PrepareIndexLeafSplit(leafPageNumber: 2, leftCellCount: 2);
        rootSplit.WriteImages.Select(image => image.PageNumber).Should().Equal(3, 4, 2);
        rootSplit.CommitTo(pager);

        var parentSplit = new SqliteBtreeSplitWriter(
            pager,
            new SqliteAppendOnlyPageAllocator(pager.CommittedPageCount))
            .PrepareIndexLeafSplit(leafPageNumber: 3, leftCellCount: 1, parentPageNumber: 2);
        parentSplit.WriteImages.Select(image => image.PageNumber).Should().Equal(5, 3, 2);
        parentSplit.CommitTo(pager);

        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var root = SqliteIndexInteriorPageView.Parse(
            pager.ReadCommittedPage(2),
            header.UsableSpace,
            header.TextEncoding);
        root.Cells.Select(cell => cell.Cell.LeftChildPage).Should().Equal(3, 5);
        root.GetRecord(0).Should().Equal(records[0]);
        root.GetRecord(1).Should().Equal(records[1]);
        root.Header.RightMostChildPage.Should().Be(4);
        root.SearchChild(records[0]).ChildPage.Should().Be(3);
        root.SearchChild(Record(SqlValue.Integer(2))).ChildPage.Should().Be(5);
        root.SearchChild(records[1]).ChildPage.Should().Be(5);
        root.SearchChild(Record(SqlValue.Integer(4))).ChildPage.Should().Be(4);
        ReadIndexRecords(pager, 3, header).Should().ContainSingle().Which.Should().Equal(records[0]);
        ReadIndexRecords(pager, 5, header).Should().ContainSingle().Which.Should().Equal(records[1]);
        var rightLeafRecords = ReadIndexRecords(pager, 4, header);
        rightLeafRecords.Should().HaveCount(2);
        rightLeafRecords[0].Should().Equal(records[2]);
        rightLeafRecords[1].Should().Equal(records[3]);
    }

    [Test]
    public void EveryInterruptedRootSplitFrameRecoversThePriorTree()
    {
        for (var failedFrame = 1; failedFrame <= 3; failedFrame++)
        {
            var faults = new DeterministicFaultInjector();
            var fileSystem = new InMemoryFileSystem(faults);
            using (var pager = CreatePager(fileSystem, $"failure-{failedFrame}"))
            {
                SeedTableRoot(pager, 1, 3, 5, 7);
                var mutation = new SqliteBtreeSplitWriter(
                    pager,
                    new SqliteAppendOnlyPageAllocator(pager.CommittedPageCount))
                    .PrepareTableLeafSplit(leafPageNumber: 1, leftCellCount: 2);
                faults.FailOnOccurrence(
                    FileSystemOperation.Write,
                    faults.GetOperationCount(FileSystemOperation.Write) + failedFrame);

                Assert.Throws<IOException>(() => mutation.CommitTo(pager));
                pager.State.Should().Be(SqlitePagerState.Faulted);
            }

            using var recovered = SqlitePager.Open(
                fileSystem,
                $"failure-{failedFrame}.db",
                $"failure-{failedFrame}.db-wal");
            recovered.CommittedPageCount.Should().Be(1);
            recovered.RecoveryInfo.LastCommittedFrameNumber.Should().Be(1);
            var root = SqliteTableLeafPageView.Parse(
                recovered.ReadCommittedPage(1),
                SqliteDatabaseHeader.Parse(recovered.ReadCommittedPage(1)).UsableSpace,
                isFirstPage: true);
            root.Cells.Select(cell => cell.Cell.RowId).Should().Equal(1, 3, 5, 7);
        }
    }

    [Test]
    public void EveryInterruptedParentPropagationFrameRecoversThePriorRouting()
    {
        for (var failedFrame = 1; failedFrame <= 3; failedFrame++)
        {
            var faults = new DeterministicFaultInjector();
            var fileSystem = new InMemoryFileSystem(faults);
            using (var pager = CreatePager(fileSystem, $"parent-failure-{failedFrame}"))
            {
                SeedTableInteriorRoot(pager);
                var mutation = new SqliteBtreeSplitWriter(
                    pager,
                    new SqliteAppendOnlyPageAllocator(pager.CommittedPageCount))
                    .PrepareTableLeafSplit(leafPageNumber: 2, leftCellCount: 1, parentPageNumber: 1);
                faults.FailOnOccurrence(
                    FileSystemOperation.Write,
                    faults.GetOperationCount(FileSystemOperation.Write) + failedFrame);

                Assert.Throws<IOException>(() => mutation.CommitTo(pager));
                pager.State.Should().Be(SqlitePagerState.Faulted);
            }

            using var recovered = SqlitePager.Open(
                fileSystem,
                $"parent-failure-{failedFrame}.db",
                $"parent-failure-{failedFrame}.db-wal");
            recovered.CommittedPageCount.Should().Be(3);
            recovered.RecoveryInfo.LastCommittedFrameNumber.Should().Be(3);
            var header = SqliteDatabaseHeader.Parse(recovered.ReadCommittedPage(1));
            var root = SqliteTableInteriorPageView.Parse(
                recovered.ReadCommittedPage(1),
                header.UsableSpace,
                isFirstPage: true);
            root.Cells.Select(cell => (cell.Cell.LeftChildPage, cell.Cell.RowId))
                .Should()
                .Equal((2U, 3L));
            root.Header.RightMostChildPage.Should().Be(3);
            root.SearchChild(3).ChildPage.Should().Be(2);
            root.SearchChild(4).ChildPage.Should().Be(3);
            ReadLeafRowIds(recovered, 2).Should().Equal(1, 3);
        }
    }

    private static SqlitePager CreatePager(IFileSystem fileSystem, string name)
        => CreatePagerAtPath(fileSystem, $"{name}.db");

    private static SqlitePager CreatePagerAtPath(IFileSystem fileSystem, string databasePath)
        => SqlitePager.Create(
            fileSystem,
            databasePath,
            databasePath + "-wal",
            SqliteWalHeader.Create(
                SqlitePageSize.Minimum,
                salt1: 0x1020_3040,
                salt2: 0x5060_7080,
                checkpointSequence: 1),
            SqliteDatabaseHeader.CreateDefault() with { PageSize = SqlitePageSize.Minimum });

    private static BoundedTableRootSeed SeedFullTableInteriorRoot(SqlitePager pager)
    {
        const uint rootPage = 2;
        var pageOne = pager.ReadCommittedPage(1);
        var header = SqliteDatabaseHeader.Parse(pageOne);
        var childCount = FindMaximumTableInteriorChildCount(pager.PageSize, header.UsableSpace);
        childCount.Should().BeGreaterThanOrEqualTo(4);

        var rightLeafCells = new List<SqliteTableLeafCell>();
        var nextRowId = childCount;
        SqliteTableLeafCell appendedCell;
        while (true)
        {
            var candidate = SqliteTableLeafCell.Create(nextRowId, DataRecord(), header.UsableSpace);
            if (CanBuildTableLeaf(rightLeafCells.Append(candidate), pager.PageSize, header.UsableSpace))
            {
                rightLeafCells.Add(candidate);
                nextRowId++;
                continue;
            }

            appendedCell = candidate;
            break;
        }

        rightLeafCells.Should().HaveCountGreaterThan(1);
        var rightMostLeafPage = checked(rootPage + (uint)childCount);
        var root = new SqliteTableInteriorPageBuilder(
            pager.PageSize,
            header.UsableSpace,
            rightMostLeafPage);
        var pages = new List<SqlitePageImage>(childCount + 1);
        for (var childIndex = 0; childIndex < childCount - 1; childIndex++)
        {
            var rowId = childIndex + 1L;
            var pageNumber = checked(3U + (uint)childIndex);
            root.Append(SqliteTableInteriorCell.Create(pageNumber, rowId));
            pages.Add(new SqlitePageImage(
                pageNumber,
                BuildTableLeaf(
                    pager.PageSize,
                    header.UsableSpace,
                    [SqliteTableLeafCell.Create(rowId, DataRecord(), header.UsableSpace)])));
        }

        pages.Add(new SqlitePageImage(
            rightMostLeafPage,
            BuildTableLeaf(pager.PageSize, header.UsableSpace, rightLeafCells)));
        pages.Add(new SqlitePageImage(rootPage, root.Build()));

        var sourcePageCount = rightMostLeafPage;
        var seededHeader = header with
        {
            DatabaseSizeInPages = sourcePageCount,
            VersionValidFor = header.ChangeCounter,
        };
        seededHeader.WriteTo(pageOne);
        var schema = new SqliteTableLeafPageBuilder(
            pager.PageSize,
            header.UsableSpace,
            isFirstPage: true);
        schema.Append(SqliteTableLeafCell.Create(
            rowId: 1,
            Record(
                SqlValue.Text("table"),
                SqlValue.Text("t"),
                SqlValue.Text("t"),
                SqlValue.Integer(rootPage),
                SqlValue.Text("CREATE TABLE t(value BLOB)")),
            header.UsableSpace));
        schema.WriteTo(pageOne);
        pages.Add(new SqlitePageImage(1, pageOne));
        CommitPages(pager, sourcePageCount, pages);

        return new BoundedTableRootSeed(
            RootPage: rootPage,
            SourcePageCount: sourcePageCount,
            RightMostLeafPage: rightMostLeafPage,
            AppendedCell: appendedCell,
            RowCount: (childCount - 1) + rightLeafCells.Count);
    }

    private static BoundedTableRootSeed ReadFullTableInteriorRootSeed(SqlitePager pager)
    {
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        const uint rootPage = 2;
        var root = SqliteTableInteriorPageView.Parse(
            pager.ReadCommittedPage(rootPage),
            header.UsableSpace);
        var rightLeaf = SqliteTableLeafPageView.Parse(
            pager.ReadCommittedPage(root.Header.RightMostChildPage),
            header.UsableSpace);
        var rowIds = ReadTableRowIds(pager, rootPage);
        return new BoundedTableRootSeed(
            RootPage: rootPage,
            SourcePageCount: pager.CommittedPageCount,
            RightMostLeafPage: root.Header.RightMostChildPage,
            AppendedCell: SqliteTableLeafCell.Create(
                rightLeaf.Cells[^1].Cell.RowId + 1,
                DataRecord(),
                header.UsableSpace),
            RowCount: rowIds.Length);
    }

    private static SqliteBtreeSplitMutation PrepareTableInteriorRootPromotion(
        SqlitePager pager,
        BoundedTableRootSeed seed)
        => new SqliteBtreeSplitWriter(
                pager,
                new SqliteAppendOnlyPageAllocator(pager.CommittedPageCount))
            .PrepareTableInteriorRootRightmostLeafSplit(
                seed.RootPage,
                seed.AppendedCell,
                CreateFinalPageOne(pager));

    private static byte[] CreateFinalPageOne(SqlitePager pager)
    {
        var pageOne = pager.ReadCommittedPage(1);
        var header = SqliteDatabaseHeader.Parse(pageOne);
        var newChangeCounter = header.ChangeCounter + 1;
        (header with
        {
            ChangeCounter = newChangeCounter,
            DatabaseSizeInPages = checked(pager.CommittedPageCount + 3),
            VersionValidFor = newChangeCounter,
        }).WriteTo(pageOne);
        return pageOne;
    }

    private static int FindMaximumTableInteriorChildCount(int pageSize, int usableSpace)
    {
        var childCount = 2;
        while (true)
        {
            try
            {
                var builder = new SqliteTableInteriorPageBuilder(
                    pageSize,
                    usableSpace,
                    checked(2U + (uint)childCount));
                for (var childIndex = 0; childIndex < childCount - 1; childIndex++)
                {
                    builder.Append(SqliteTableInteriorCell.Create(
                        checked(3U + (uint)childIndex),
                        childIndex + 1));
                }

                _ = builder.Build();
                childCount++;
            }
            catch (InvalidOperationException)
            {
                return childCount - 1;
            }
        }
    }

    private static bool CanBuildTableLeaf(
        IEnumerable<SqliteTableLeafCell> cells,
        int pageSize,
        int usableSpace)
    {
        try
        {
            var builder = new SqliteTableLeafPageBuilder(pageSize, usableSpace);
            foreach (var cell in cells)
                builder.Append(cell);
            _ = builder.Build();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static byte[] BuildTableLeaf(
        int pageSize,
        int usableSpace,
        IReadOnlyList<SqliteTableLeafCell> cells)
    {
        var builder = new SqliteTableLeafPageBuilder(pageSize, usableSpace);
        foreach (var cell in cells)
            builder.Append(cell);
        return builder.Build();
    }

    private static void AssertPromotedTable(
        SqlitePager pager,
        uint rootPage,
        int expectedRowCount)
    {
        ReadTableHeight(pager, rootPage).Should().Be(3);
        ReadTableRowIds(pager, rootPage)
            .Should()
            .Equal(Enumerable.Range(1, expectedRowCount).Select(value => (long)value));
    }

    private static void AssertUnpromotedTable(
        SqlitePager pager,
        uint rootPage,
        int expectedRowCount)
    {
        ReadTableHeight(pager, rootPage).Should().Be(2);
        ReadTableRowIds(pager, rootPage)
            .Should()
            .Equal(Enumerable.Range(1, expectedRowCount).Select(value => (long)value));
    }

    private static int ReadTableHeight(SqlitePager pager, uint pageNumber)
    {
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var page = pager.ReadCommittedPage(pageNumber);
        return SqliteBtreePageHeader.Parse(page).PageType switch
        {
            SqliteBtreePageType.TableLeaf => 1,
            SqliteBtreePageType.TableInterior => ReadTableInteriorHeight(
                pager,
                SqliteTableInteriorPageView.Parse(page, header.UsableSpace)),
            var pageType => throw new InvalidDataException(
                $"Expected a SQLite table b-tree page but found {pageType}."),
        };
    }

    private static int ReadTableInteriorHeight(
        SqlitePager pager,
        SqliteTableInteriorPageView interior)
    {
        var childHeights = interior.Cells
            .Select(cell => ReadTableHeight(pager, cell.Cell.LeftChildPage))
            .Append(ReadTableHeight(pager, interior.Header.RightMostChildPage))
            .ToArray();
        childHeights.Should().OnlyContain(height => height == childHeights[0]);
        return childHeights[0] + 1;
    }

    private static long[] ReadTableRowIds(SqlitePager pager, uint rootPage)
    {
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var rowIds = new List<long>();
        AppendTableRowIds(pager, rootPage, header.UsableSpace, rowIds);
        return rowIds.ToArray();
    }

    private static void AppendTableRowIds(
        SqlitePager pager,
        uint pageNumber,
        int usableSpace,
        ICollection<long> rowIds)
    {
        var page = pager.ReadCommittedPage(pageNumber);
        switch (SqliteBtreePageHeader.Parse(page).PageType)
        {
            case SqliteBtreePageType.TableLeaf:
                foreach (var cell in SqliteTableLeafPageView.Parse(page, usableSpace).Cells)
                    rowIds.Add(cell.Cell.RowId);
                return;
            case SqliteBtreePageType.TableInterior:
                {
                    var interior = SqliteTableInteriorPageView.Parse(page, usableSpace);
                    foreach (var cell in interior.Cells)
                        AppendTableRowIds(pager, cell.Cell.LeftChildPage, usableSpace, rowIds);
                    AppendTableRowIds(pager, interior.Header.RightMostChildPage, usableSpace, rowIds);
                    return;
                }
            default:
                throw new InvalidDataException("Expected a SQLite table b-tree page.");
        }
    }

    private static byte[] DataRecord()
        => Record(SqlValue.Blob(Enumerable.Repeat((byte)0xA5, 72).ToArray()));

    private static void SeedTableRoot(SqlitePager pager, params long[] rowIds)
    {
        var page = pager.ReadCommittedPage(1);
        var header = SqliteDatabaseHeader.Parse(page);
        var builder = new SqliteTableLeafPageBuilder(pager.PageSize, header.UsableSpace, isFirstPage: true);
        foreach (var rowId in rowIds)
            builder.Append(SqliteTableLeafCell.Create(rowId, [(byte)rowId], header.UsableSpace));
        builder.WriteTo(page);
        CommitPages(pager, targetPageCount: 1, [new SqlitePageImage(1, page)]);
    }

    private static void SeedTableInteriorRoot(SqlitePager pager)
    {
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var leftLeaf = BuildTableLeaf(pager.PageSize, header.UsableSpace, 1, 3);
        var rightLeaf = BuildTableLeaf(pager.PageSize, header.UsableSpace, 5, 7);
        var root = pager.ReadCommittedPage(1);
        var rootBuilder = new SqliteTableInteriorPageBuilder(
            pager.PageSize,
            header.UsableSpace,
            rightMostChildPage: 3,
            isFirstPage: true);
        rootBuilder.Append(SqliteTableInteriorCell.Create(2, 3));
        rootBuilder.WriteTo(root);
        WritePageOneCount(root, 3);

        CommitPages(
            pager,
            targetPageCount: 3,
            [
                new SqlitePageImage(2, leftLeaf),
                new SqlitePageImage(3, rightLeaf),
                new SqlitePageImage(1, root),
            ]);
    }

    private static void SeedIndexRoot(SqlitePager pager, IReadOnlyList<byte[]> records)
    {
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var root = BuildIndexLeaf(pager.PageSize, header.UsableSpace, records);
        CommitPages(pager, targetPageCount: 2, [new SqlitePageImage(2, root)]);
    }

    private static byte[] BuildTableLeaf(int pageSize, int usableSpace, params long[] rowIds)
    {
        var builder = new SqliteTableLeafPageBuilder(pageSize, usableSpace);
        foreach (var rowId in rowIds)
            builder.Append(SqliteTableLeafCell.Create(rowId, [(byte)rowId], usableSpace));
        return builder.Build();
    }

    private static byte[] BuildIndexLeaf(int pageSize, int usableSpace, IReadOnlyList<byte[]> records)
    {
        var builder = new SqliteIndexLeafPageBuilder(pageSize, usableSpace);
        foreach (var record in records)
            builder.Append(SqliteIndexLeafCell.Create(record, usableSpace));
        return builder.Build();
    }

    private static void CommitPages(
        SqlitePager pager,
        uint targetPageCount,
        IReadOnlyList<SqlitePageImage> images)
    {
        using var transaction = pager.BeginTransaction(targetPageCount);
        foreach (var image in images)
            transaction.WritePage(image.PageNumber, image.Page.Span);
        transaction.Commit();
    }

    private static long[] ReadLeafRowIds(SqlitePager pager, uint pageNumber)
    {
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        return SqliteTableLeafPageView.Parse(pager.ReadCommittedPage(pageNumber), header.UsableSpace)
            .Cells
            .Select(cell => cell.Cell.RowId)
            .ToArray();
    }

    private static byte[][] ReadIndexRecords(
        SqlitePager pager,
        uint pageNumber,
        SqliteDatabaseHeader header)
    {
        var view = SqliteIndexLeafPageView.Parse(
            pager.ReadCommittedPage(pageNumber),
            header.UsableSpace,
            header.TextEncoding);
        return Enumerable.Range(0, view.Cells.Count).Select(view.GetRecord).ToArray();
    }

    private static void WritePageOneCount(byte[] page, uint pageCount)
    {
        var header = SqliteDatabaseHeader.Parse(page);
        (header with
        {
            DatabaseSizeInPages = pageCount,
            VersionValidFor = header.ChangeCounter,
        }).WriteTo(page);
    }

    private static string CreateDatabasePath(string name)
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "sqlite-btree-split-storage-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{name}-{Guid.NewGuid():N}.db");
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

    private sealed record BoundedTableRootSeed(
        uint RootPage,
        uint SourcePageCount,
        uint RightMostLeafPage,
        SqliteTableLeafCell AppendedCell,
        int RowCount);

    private static byte[] Record(params SqlValue[] values) => SqliteRecordCodec.Encode(values);
}
