using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedSecondaryIndexInteriorRootMiddleLeafSplitTests
{
    private const int PageSize = SqlitePageSize.Minimum;
    private const int RepeatedColumnCount = 8;
    private const string IndexName = "target_id_repeated";

    [Test]
    public void InteriorRootMiddleChildLeafSplitPersistsReopensAndPassesSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath("integrity");
        try
        {
            var target = SeedMiddleLeafSplitTopology(PhysicalFileSystem.Instance, path);
            var before = ReadTopology(PhysicalFileSystem.Instance, path);

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
                Execute(connection, InsertStatement(target.RowId, target.Code));

            var after = ReadTopology(PhysicalFileSystem.Instance, path);
            AssertChildLeafSplit(before, after, target);
            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                Count(connection).Should().Be(target.RowCount + 1);
                CountById(connection, target.RowId).Should().Be(1);
            }

            VerifyWithSqlite(path, target);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void InteriorRootLeftmostChildLeafInsertionPersistsReopensAndPassesSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath("leftmost-integrity");
        try
        {
            var target = SeedLeftmostLeafInsertionTopology(PhysicalFileSystem.Instance, path);
            var before = ReadTopology(PhysicalFileSystem.Instance, path);

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
                Execute(connection, InsertStatement(target.RowId, target.Code));

            var after = ReadTopology(PhysicalFileSystem.Instance, path);
            AssertLeftmostChildLeafInsertion(before, after, target);
            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                Count(connection).Should().Be(target.RowCount + 1);
                CountById(connection, target.RowId).Should().Be(1);
            }

            VerifyWithSqlite(path, target);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void InteriorRootLeftmostChildLeafSplitPersistsReopensAndPassesSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath("leftmost-split-integrity");
        try
        {
            var target = SeedLeftmostLeafSplitTopology(PhysicalFileSystem.Instance, path);
            var before = ReadTopology(PhysicalFileSystem.Instance, path);

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
                Execute(connection, InsertStatement(target.RowId, target.Code));

            var after = ReadTopology(PhysicalFileSystem.Instance, path);
            AssertChildLeafSplit(before, after, target);
            target.ChildIndex.Should().Be(0);
            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                Count(connection).Should().Be(target.RowCount + 1);
                CountById(connection, target.RowId).Should().Be(1);
            }

            VerifyWithSqlite(path, target);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void EveryInterruptedInteriorRootMiddleChildLeafSplitFrameRecoversThePriorTree()
    {
        for (var failedFrame = 1; failedFrame <= 5; failedFrame++)
        {
            var faults = new DeterministicFaultInjector();
            var fileSystem = new InMemoryFileSystem(faults);
            var path = $"secondary-index-middle-child-leaf-split-wal-{failedFrame}.db";
            var target = SeedMiddleLeafSplitTopology(fileSystem, path);
            var before = ReadTopology(fileSystem, path);

            using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = database.Connect())
            {
                faults.FailOnOccurrence(
                    FileSystemOperation.Write,
                    faults.GetOperationCount(FileSystemOperation.Write) + failedFrame);
                Assert.Throws<IOException>(() => Execute(connection, InsertStatement(target.RowId, target.Code)));
            }

            using (var recovered = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = recovered.Connect())
            {
                Count(connection).Should().Be(target.RowCount);
                CountById(connection, target.RowId).Should().Be(0);
            }

            AssertUnchanged(before, ReadTopology(fileSystem, path));
        }
    }

    [Test]
    public void EveryInterruptedInteriorRootLeftmostChildLeafInsertionFrameRecoversThePriorTree()
    {
        for (var failedFrame = 1; failedFrame <= 3; failedFrame++)
        {
            var faults = new DeterministicFaultInjector();
            var fileSystem = new InMemoryFileSystem(faults);
            var path = $"secondary-index-leftmost-child-leaf-insertion-wal-{failedFrame}.db";
            var target = SeedLeftmostLeafInsertionTopology(fileSystem, path);
            var before = ReadTopology(fileSystem, path);

            using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = database.Connect())
            {
                faults.FailOnOccurrence(
                    FileSystemOperation.Write,
                    faults.GetOperationCount(FileSystemOperation.Write) + failedFrame);
                Assert.Throws<IOException>(() => Execute(connection, InsertStatement(target.RowId, target.Code)));
            }

            using (var recovered = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = recovered.Connect())
            {
                Count(connection).Should().Be(target.RowCount);
                CountById(connection, target.RowId).Should().Be(0);
            }

            AssertUnchanged(before, ReadTopology(fileSystem, path));
        }
    }

    [Test]
    public void EveryInterruptedInteriorRootLeftmostChildLeafSplitFrameRecoversThePriorTree()
    {
        for (var failedFrame = 1; failedFrame <= 5; failedFrame++)
        {
            var faults = new DeterministicFaultInjector();
            var fileSystem = new InMemoryFileSystem(faults);
            var path = $"secondary-index-leftmost-child-leaf-split-wal-{failedFrame}.db";
            var target = SeedLeftmostLeafSplitTopology(fileSystem, path);
            var before = ReadTopology(fileSystem, path);

            using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = database.Connect())
            {
                faults.FailOnOccurrence(
                    FileSystemOperation.Write,
                    faults.GetOperationCount(FileSystemOperation.Write) + failedFrame);
                Assert.Throws<IOException>(() => Execute(connection, InsertStatement(target.RowId, target.Code)));
            }

            using (var recovered = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = recovered.Connect())
            {
                Count(connection).Should().Be(target.RowCount);
                CountById(connection, target.RowId).Should().Be(0);
            }

            AssertUnchanged(before, ReadTopology(fileSystem, path));
        }
    }

    private static SplitTarget SeedMiddleLeafSplitTopology(IFileSystem fileSystem, string path)
        => SeedChildLeafTopology(fileSystem, path, ChildPosition.Middle, requireSplit: true);

    private static SplitTarget SeedLeftmostLeafInsertionTopology(IFileSystem fileSystem, string path)
        => SeedChildLeafTopology(fileSystem, path, ChildPosition.Leftmost, requireSplit: false);

    private static SplitTarget SeedLeftmostLeafSplitTopology(IFileSystem fileSystem, string path)
        => SeedChildLeafTopology(fileSystem, path, ChildPosition.Leftmost, requireSplit: true);

    private static SplitTarget SeedChildLeafTopology(
        IFileSystem fileSystem,
        string path,
        ChildPosition childPosition,
        bool requireSplit)
    {
        var initialRowCount = 5;
        CreateMinimumPageDatabase(fileSystem, path);
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, code INTEGER NOT NULL);");
            Execute(
                connection,
                InsertStatement(Enumerable.Range(1, initialRowCount).Select(id => (Id: id, Code: id * 100))));
            Execute(
                connection,
                $"CREATE UNIQUE INDEX {IndexName} ON target({RepeatedIndexColumns("code", RepeatedColumnCount)});");
        }

        var rowCount = initialRowCount;
        for (var rowId = initialRowCount + 1; rowId <= 96; rowId++)
        {
            if (TryFindChildSplitTarget(
                    fileSystem,
                    path,
                    rowCount,
                    rowId,
                    childPosition,
                    requireSplit,
                    out var target))
                return target;

            var filler = TryFindChildSplitTarget(
                fileSystem,
                path,
                rowCount,
                rowId,
                childPosition,
                requireSplit: false,
                out var candidate)
                ? candidate
                : new SplitTarget(rowId, rowId * 100, rowCount, ChildIndex: -1, ChildPage: 0);

            using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
            using var connection = database.Connect();
            Execute(connection, InsertStatement(filler.RowId, filler.Code));
            rowCount++;
        }

        throw new InvalidOperationException(
            $"Unable to create a bounded {childPosition} secondary-index leaf split topology.");
    }

    private static bool TryFindChildSplitTarget(
        IFileSystem fileSystem,
        string path,
        int rowCount,
        int rowId,
        ChildPosition childPosition,
        bool requireSplit,
        out SplitTarget target)
    {
        target = null!;
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        if (header.FreelistPageCount != 0
            || header.FirstFreelistTrunkPage != 0
            || header.DatabaseSizeInPages != pager.CommittedPageCount)
        {
            return false;
        }

        var tableRootPage = FindRootPage(pager, header, "table", "target");
        var indexRootPage = FindRootPage(pager, header, "index", IndexName);
        if (SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(tableRootPage)).PageType
                != SqliteBtreePageType.TableLeaf
            || SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(indexRootPage)).PageType
                != SqliteBtreePageType.IndexInterior)
        {
            return false;
        }

        var root = SqliteIndexInteriorPageView.Parse(
            pager.ReadCommittedPage(indexRootPage),
            header.UsableSpace,
            header.TextEncoding);
        if (root.Cells.Count < (childPosition == ChildPosition.Middle ? 2 : 1)
            || root.Cells.Any(cell => cell.Cell.Key.FirstOverflowPage is not null))
            return false;

        var comparer = new SqliteIndexRecordComparer(header.TextEncoding);
        var existingCodes = new HashSet<long>();
        var childPages = root.Cells
            .Select(cell => cell.Cell.LeftChildPage)
            .Append(root.Header.RightMostChildPage);
        for (var childIndex = 0; childIndex < root.Cells.Count; childIndex++)
            existingCodes.Add(SqliteRecordCodec.Decode(root.GetRecord(childIndex), header.TextEncoding)[0].AsInteger());
        foreach (var childPage in childPages)
        {
            var childPageImage = pager.ReadCommittedPage(childPage);
            if (SqliteBtreePageHeader.Parse(childPageImage).PageType != SqliteBtreePageType.IndexLeaf)
                return false;

            var child = SqliteIndexLeafPageView.Parse(
                childPageImage,
                header.UsableSpace,
                header.TextEncoding);
            foreach (var (_, recordIndex) in child.Cells.Select((cell, index) => (cell, index)))
                existingCodes.Add(SqliteRecordCodec.Decode(child.GetRecord(recordIndex), header.TextEncoding)[0].AsInteger());
        }

        for (var code = 1; code <= 2_400; code++)
        {
            if (code % 100 == 0 || existingCodes.Contains(code))
                continue;

            var record = BuildIndexRecord(code, rowId, header.TextEncoding);
            var route = root.SearchChild(record);
            if (route.IsSeparatorKey
                || route.ChildIndex < 0
                || route.ChildIndex >= root.Cells.Count
                || (childPosition == ChildPosition.Leftmost && route.ChildIndex != 0)
                || (childPosition == ChildPosition.Middle
                    && (route.ChildIndex == 0 || route.ChildIndex >= root.Cells.Count)))
            {
                continue;
            }

            var leafPage = pager.ReadCommittedPage(route.ChildPage);
            if (SqliteBtreePageHeader.Parse(leafPage).PageType != SqliteBtreePageType.IndexLeaf)
                return false;

            var leaf = SqliteIndexLeafPageView.Parse(
                leafPage,
                header.UsableSpace,
                header.TextEncoding);
            if (leaf.Cells.Count == 0 || leaf.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null))
                continue;

            var records = Enumerable.Range(0, leaf.Cells.Count).Select(leaf.GetRecord).ToList();
            var insertionIndex = records.FindIndex(existing => comparer.Compare(record, existing) < 0);
            if (insertionIndex < 0)
                insertionIndex = records.Count;
            if ((insertionIndex > 0 && comparer.Compare(records[insertionIndex - 1], record) >= 0)
                || (insertionIndex < records.Count && comparer.Compare(record, records[insertionIndex]) >= 0))
            {
                continue;
            }

            records.Insert(insertionIndex, record);
            var fitsLeaf = FitsIndexLeaf(records, header, comparer);
            if (fitsLeaf)
            {
                if (!requireSplit)
                {
                    target = new SplitTarget(rowId, code, rowCount, route.ChildIndex, route.ChildPage);
                    return true;
                }

                continue;
            }

            if (requireSplit
                && TrySplitIndexLeaf(records, header, comparer, out var separator)
                && FitsExpandedParent(root, route, separator, pager.CommittedPageCount, header, comparer))
            {
                target = new SplitTarget(rowId, code, rowCount, route.ChildIndex, route.ChildPage);
                return true;
            }
        }

        return false;
    }

    private static bool FitsIndexLeaf(
        IReadOnlyList<byte[]> records,
        SqliteDatabaseHeader header,
        SqliteIndexRecordComparer comparer)
    {
        try
        {
            var builder = new SqliteIndexLeafPageBuilder(
                header.PageSize,
                header.UsableSpace,
                comparer);
            foreach (var record in records)
                builder.Append(SqliteIndexLeafCell.Create(record, header.UsableSpace), record);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TrySplitIndexLeaf(
        IReadOnlyList<byte[]> records,
        SqliteDatabaseHeader header,
        SqliteIndexRecordComparer comparer,
        out byte[] separator)
    {
        separator = null!;
        for (var separatorIndex = 1; separatorIndex < records.Count - 1; separatorIndex++)
        {
            if (!FitsIndexLeaf(records.Take(separatorIndex).ToArray(), header, comparer)
                || !FitsIndexLeaf(records.Skip(separatorIndex + 1).ToArray(), header, comparer))
            {
                continue;
            }

            separator = records[separatorIndex];
            return true;
        }

        return false;
    }

    private static bool FitsExpandedParent(
        SqliteIndexInteriorPageView root,
        SqliteBtreeChildSearchResult route,
        ReadOnlySpan<byte> separator,
        uint sourcePageCount,
        SqliteDatabaseHeader header,
        SqliteIndexRecordComparer comparer)
    {
        try
        {
            var builder = new SqliteIndexInteriorPageBuilder(
                header.PageSize,
                header.UsableSpace,
                root.Header.RightMostChildPage,
                comparer);
            for (var cellIndex = 0; cellIndex < root.Cells.Count; cellIndex++)
            {
                if (cellIndex == route.ChildIndex)
                {
                    builder.Append(
                        SqliteIndexInteriorCell.Create(
                            route.ChildPage,
                            separator,
                            header.UsableSpace),
                        separator);
                    builder.Append(
                        SqliteIndexInteriorCell.Create(
                            sourcePageCount + 1,
                            root.GetRecord(cellIndex),
                            header.UsableSpace),
                        root.GetRecord(cellIndex));
                    continue;
                }

                builder.Append(root.Cells[cellIndex].Cell, root.GetRecord(cellIndex));
            }

            _ = builder.Build();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static Topology ReadTopology(IFileSystem fileSystem, string path)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var tableRootPage = FindRootPage(pager, header, "table", "target");
        var indexRootPage = FindRootPage(pager, header, "index", IndexName);
        var indexRootImage = pager.ReadCommittedPage(indexRootPage);
        var root = SqliteIndexInteriorPageView.Parse(
            indexRootImage,
            header.UsableSpace,
            header.TextEncoding);
        var children = root.Cells
            .Select(cell => cell.Cell.LeftChildPage)
            .Append(root.Header.RightMostChildPage)
            .Select(pageNumber =>
            {
                var leaf = SqliteIndexLeafPageView.Parse(
                    pager.ReadCommittedPage(pageNumber),
                    header.UsableSpace,
                    header.TextEncoding);
                return new LeafTopology(
                    pageNumber,
                    Enumerable.Range(0, leaf.Cells.Count)
                        .Select(leaf.GetRecord)
                        .Select(record => SqliteRecordCodec.Decode(record, header.TextEncoding)[^1].AsInteger())
                        .ToArray());
            })
            .ToArray();
        return new Topology(
            header,
            pager.CommittedPageCount,
            tableRootPage,
            indexRootPage,
            root.Cells
                .Select((cell, index) => new ParentCell(cell.Cell.LeftChildPage, root.GetRecord(index)))
                .ToArray(),
            root.Header.RightMostChildPage,
            indexRootImage,
            children,
            ReadIndexRowIds(root, children));
    }

    private static void AssertChildLeafSplit(Topology before, Topology after, SplitTarget target)
    {
        after.PageCount.Should().Be(before.PageCount + 1);
        after.Header.DatabaseSizeInPages.Should().Be(after.PageCount);
        after.Header.ChangeCounter.Should().Be(before.Header.ChangeCounter + 1);
        after.Header.VersionValidFor.Should().Be(after.Header.ChangeCounter);
        after.Header.SchemaCookie.Should().Be(before.Header.SchemaCookie);
        after.Header.FirstFreelistTrunkPage.Should().Be(0);
        after.Header.FreelistPageCount.Should().Be(0);
        after.TableRootPage.Should().Be(before.TableRootPage);
        after.IndexRootPage.Should().Be(before.IndexRootPage);
        after.ParentCells.Count.Should().Be(before.ParentCells.Count + 1);
        after.RightMostChildPage.Should().Be(before.RightMostChildPage);
        after.ParentCells[target.ChildIndex].LeftChildPage.Should().Be(target.ChildPage);
        after.ParentCells[target.ChildIndex + 1].LeftChildPage.Should().Be(after.PageCount);
        after.ParentCells[target.ChildIndex + 1].Record.Should()
            .Equal(before.ParentCells[target.ChildIndex].Record);
        after.Children[target.ChildIndex].PageNumber.Should().Be(target.ChildPage);
        after.Children[target.ChildIndex + 1].PageNumber.Should().Be(after.PageCount);
        after.IndexRowIds.Should().BeEquivalentTo(before.IndexRowIds.Append(target.RowId));
    }

    private static void AssertLeftmostChildLeafInsertion(Topology before, Topology after, SplitTarget target)
    {
        target.ChildIndex.Should().Be(0);
        after.PageCount.Should().Be(before.PageCount);
        after.Header.DatabaseSizeInPages.Should().Be(before.Header.DatabaseSizeInPages);
        after.Header.ChangeCounter.Should().Be(before.Header.ChangeCounter + 1);
        after.Header.VersionValidFor.Should().Be(after.Header.ChangeCounter);
        after.Header.SchemaCookie.Should().Be(before.Header.SchemaCookie);
        after.Header.FirstFreelistTrunkPage.Should().Be(0);
        after.Header.FreelistPageCount.Should().Be(0);
        after.TableRootPage.Should().Be(before.TableRootPage);
        after.IndexRootPage.Should().Be(before.IndexRootPage);
        after.ParentCells.Should().BeEquivalentTo(before.ParentCells, options => options.WithStrictOrdering());
        after.RightMostChildPage.Should().Be(before.RightMostChildPage);
        after.IndexRootImage.Should().Equal(before.IndexRootImage);
        after.Children.Select(child => child.PageNumber)
            .Should()
            .Equal(before.Children.Select(child => child.PageNumber));
        after.Children[target.ChildIndex].PageNumber.Should().Be(target.ChildPage);
        after.Children[target.ChildIndex].RowIds.Should()
            .BeEquivalentTo(before.Children[target.ChildIndex].RowIds.Append(target.RowId));
        after.Children.Where((_, index) => index != target.ChildIndex)
            .Should()
            .BeEquivalentTo(
                before.Children.Where((_, index) => index != target.ChildIndex),
                options => options.WithStrictOrdering());
        after.IndexRowIds.Should().BeEquivalentTo(before.IndexRowIds.Append(target.RowId));
    }

    private static void AssertUnchanged(Topology before, Topology after)
    {
        after.Header.Should().Be(before.Header);
        after.PageCount.Should().Be(before.PageCount);
        after.TableRootPage.Should().Be(before.TableRootPage);
        after.IndexRootPage.Should().Be(before.IndexRootPage);
        after.IndexRootImage.Should().Equal(before.IndexRootImage);
        after.ParentCells.Should().BeEquivalentTo(before.ParentCells, options => options.WithStrictOrdering());
        after.Children.Should().BeEquivalentTo(before.Children, options => options.WithStrictOrdering());
        after.IndexRowIds.Should().Equal(before.IndexRowIds);
    }

    private static IReadOnlyList<long> ReadIndexRowIds(
        SqliteIndexInteriorPageView root,
        IReadOnlyList<LeafTopology> children)
    {
        var rowIds = new List<long>();
        for (var childIndex = 0; childIndex < children.Count; childIndex++)
        {
            rowIds.AddRange(children[childIndex].RowIds);
            if (childIndex < root.Cells.Count)
            {
                rowIds.Add(SqliteRecordCodec.Decode(
                    root.GetRecord(childIndex),
                    root.RecordComparer.TextEncoding)[^1].AsInteger());
            }
        }

        return rowIds;
    }

    private static void CreateMinimumPageDatabase(IFileSystem fileSystem, string path)
    {
        var header = SqliteDatabaseHeader.CreateDefault() with { PageSize = PageSize };
        using var pager = SqlitePager.Create(
            fileSystem,
            path,
            path + "-wal",
            SqliteWalHeader.Create(PageSize, salt1: 0x1020_3040, salt2: 0x5060_7080),
            header);
    }

    private static uint FindRootPage(
        SqlitePager pager,
        SqliteDatabaseHeader header,
        string type,
        string name)
    {
        return checked((uint)ReadSchemaRecords(pager, header, pageNumber: 1, isFirstPage: true)
            .Single(values => values[0].AsText() == type && values[1].AsText() == name)[3]
            .AsInteger());
    }

    private static IEnumerable<SqlValue[]> ReadSchemaRecords(
        SqlitePager pager,
        SqliteDatabaseHeader header,
        uint pageNumber,
        bool isFirstPage)
    {
        var page = pager.ReadCommittedPage(pageNumber);
        return SqliteBtreePageHeader.Parse(page, isFirstPage).PageType switch
        {
            SqliteBtreePageType.TableLeaf => SqliteTableLeafPageView.Parse(
                    page,
                    header.UsableSpace,
                    isFirstPage)
                .Cells
                .Select(cell => SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding)),
            SqliteBtreePageType.TableInterior => SqliteTableInteriorPageView.Parse(
                    page,
                    header.UsableSpace,
                    isFirstPage)
                .Cells
                .Select(cell => cell.Cell.LeftChildPage)
                .Append(SqliteTableInteriorPageView.Parse(
                    page,
                    header.UsableSpace,
                    isFirstPage).Header.RightMostChildPage)
                .SelectMany(child => ReadSchemaRecords(pager, header, child, isFirstPage: false)),
            _ => throw new InvalidDataException("sqlite_schema has an unsupported b-tree page type."),
        };
    }

    private static byte[] BuildIndexRecord(int code, int rowId, SqliteTextEncoding textEncoding)
        => SqliteRecordCodec.Encode(
            Enumerable.Repeat(SqlValue.Integer(code), RepeatedColumnCount)
                .Append(SqlValue.Integer(rowId))
                .ToArray(),
            textEncoding);

    private static void VerifyWithSqlite(string path, SplitTarget target)
    {
        var verificationPath = path + ".verify.db";
        File.Copy(path, verificationPath, overwrite: true);
        try
        {
            // Avoid the global SQLite pool because other parallel storage tests clear it while
            // their own temporary databases are being removed. Writable open mode is required
            // for SQLite to initialize the copied database's WAL sidecar state.
            using var sqlite = new MsData.SqliteConnection($"Data Source={verificationPath};Pooling=False");
            sqlite.Open();
            using (var integrity = sqlite.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check;";
                integrity.ExecuteScalar().Should().Be("ok");
            }

            using var lookup = sqlite.CreateCommand();
            lookup.CommandText =
                $"SELECT id FROM target INDEXED BY {IndexName} WHERE code = {target.Code};";
            Convert.ToInt64(lookup.ExecuteScalar()).Should().Be(target.RowId);
        }
        finally
        {
            DeleteDatabase(verificationPath);
        }
    }

    private static string RepeatedIndexColumns(string column, int count)
        => string.Join(", ", Enumerable.Repeat(column, count));

    private static string InsertStatement(IEnumerable<(int Id, int Code)> rows)
        => $"INSERT INTO target VALUES {string.Join(", ", rows.Select(row => $"({row.Id}, {row.Code})"))};";

    private static string InsertStatement(int rowId, int code) => InsertStatement([(rowId, code)]);

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private static long Count(EmbeddedConnection connection)
    {
        using var statement = connection.Prepare("SELECT COUNT(*) FROM target;");
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static long CountById(EmbeddedConnection connection, int rowId)
    {
        using var statement = connection.Prepare($"SELECT COUNT(*) FROM target WHERE id = {rowId};");
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static string CreateDatabasePath(string name)
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "managed-secondary-index-interior-middle-leaf-split-tests");
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

    private sealed record SplitTarget(int RowId, int Code, int RowCount, int ChildIndex, uint ChildPage);

    private enum ChildPosition
    {
        Leftmost,
        Middle,
    }

    private sealed record ParentCell(uint LeftChildPage, byte[] Record);

    private sealed record LeafTopology(uint PageNumber, IReadOnlyList<long> RowIds);

    private sealed record Topology(
        SqliteDatabaseHeader Header,
        uint PageCount,
        uint TableRootPage,
        uint IndexRootPage,
        IReadOnlyList<ParentCell> ParentCells,
        uint RightMostChildPage,
        byte[] IndexRootImage,
        IReadOnlyList<LeafTopology> Children,
        IReadOnlyList<long> IndexRowIds);
}
