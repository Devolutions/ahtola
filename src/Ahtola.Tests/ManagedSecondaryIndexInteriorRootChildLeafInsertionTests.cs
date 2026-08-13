using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedSecondaryIndexInteriorRootChildLeafInsertionTests
{
    private const int PageSize = SqlitePageSize.Minimum;
    private const int RepeatedColumnCount = 48;
    private const string IndexName = "target_id_repeated";

    [Test]
    public void InteriorRootMiddleChildLeafInsertionPersistsReopensAndPassesSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath("integrity");
        try
        {
            var target = SeedInsertionTopology(PhysicalFileSystem.Instance, path);
            var before = ReadTopology(PhysicalFileSystem.Instance, path);

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
                Execute(connection, InsertStatement(target.RowId));

            var after = ReadTopology(PhysicalFileSystem.Instance, path);
            AssertBoundedInsertion(before, after, target);
            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                Count(connection).Should().Be(target.RowCount + 1);
                Id(connection, target.RowId).Should().Be(target.RowId);
            }

            VerifyWithSqlite(path, target);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void EveryInterruptedInteriorRootMiddleChildLeafInsertionFrameRecoversThePriorTree()
    {
        for (var failedFrame = 1; failedFrame <= 3; failedFrame++)
        {
            var faults = new DeterministicFaultInjector();
            var fileSystem = new InMemoryFileSystem(faults);
            var path = $"secondary-index-interior-child-insertion-wal-{failedFrame}.db";
            var target = SeedInsertionTopology(fileSystem, path);
            var before = ReadTopology(fileSystem, path);

            using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = database.Connect())
            {
                faults.FailOnOccurrence(
                    FileSystemOperation.Write,
                    faults.GetOperationCount(FileSystemOperation.Write) + failedFrame);
                Assert.Throws<IOException>(() => Execute(connection, InsertStatement(target.RowId)));
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

    private static InsertionTarget SeedInsertionTopology(IFileSystem fileSystem, string path)
    {
        CreateMinimumPageDatabase(fileSystem, path);
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY);");
            Execute(connection, InsertStatement(Enumerable.Range(1, 5).Select(value => (value * 2) - 1)));
            Execute(
                connection,
                $"CREATE UNIQUE INDEX {IndexName} ON target({RepeatedIndexColumns(RepeatedColumnCount)});");
        }

        var rowCount = 5;
        for (var rowId = 11; rowId <= 63; rowId += 2)
        {
            if (TryFindMiddleChildInsertionTarget(fileSystem, path, rowCount, out var target))
                return target;

            using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
            using var connection = database.Connect();
            Execute(connection, InsertStatement(rowId));
            rowCount++;
        }

        throw new InvalidOperationException(
            "Unable to create a bounded middle-child secondary-index insertion topology.");
    }

    private static bool TryFindMiddleChildInsertionTarget(
        IFileSystem fileSystem,
        string path,
        int rowCount,
        out InsertionTarget target)
    {
        target = null!;
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        if (header.FreelistPageCount != 0 || header.FirstFreelistTrunkPage != 0)
            return false;

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
        if (root.Cells.Count < 2 || root.Cells.Any(cell => cell.Cell.Key.FirstOverflowPage is not null))
            return false;

        var comparer = new SqliteIndexRecordComparer(header.TextEncoding);
        for (var rowId = 2; rowId < 64; rowId += 2)
        {
            var record = BuildIndexRecord(rowId, header.TextEncoding);
            var route = root.SearchChild(record);
            if (route.IsSeparatorKey
                || route.ChildIndex <= 0
                || route.ChildIndex >= root.Cells.Count)
            {
                continue;
            }

            var leaf = SqliteIndexLeafPageView.Parse(
                pager.ReadCommittedPage(route.ChildPage),
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
            try
            {
                var builder = new SqliteIndexLeafPageBuilder(
                    header.PageSize,
                    header.UsableSpace,
                    comparer);
                foreach (var candidate in records)
                    builder.Append(SqliteIndexLeafCell.Create(candidate, header.UsableSpace), candidate);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            target = new InsertionTarget(rowId, rowCount, route.ChildPage);
            return true;
        }

        return false;
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
            root.Cells.Count,
            indexRootImage,
            children);
    }

    private static void AssertBoundedInsertion(
        Topology before,
        Topology after,
        InsertionTarget target)
    {
        after.PageCount.Should().Be(before.PageCount);
        after.Header.DatabaseSizeInPages.Should().Be(before.Header.DatabaseSizeInPages);
        after.Header.ChangeCounter.Should().Be(before.Header.ChangeCounter + 1);
        after.Header.VersionValidFor.Should().Be(after.Header.ChangeCounter);
        after.Header.SchemaCookie.Should().Be(before.Header.SchemaCookie);
        after.Header.FirstFreelistTrunkPage.Should().Be(0);
        after.Header.FreelistPageCount.Should().Be(0);
        after.TableRootPage.Should().Be(before.TableRootPage);
        after.IndexRootPage.Should().Be(before.IndexRootPage);
        after.IndexRootCellCount.Should().Be(before.IndexRootCellCount);
        after.IndexRootImage.Should().Equal(before.IndexRootImage);
        after.Children.Select(child => child.PageNumber)
            .Should()
            .Equal(before.Children.Select(child => child.PageNumber));
        after.Children.Single(child => child.PageNumber == target.ChildPage).RowIds
            .Should()
            .Equal(before.Children.Single(child => child.PageNumber == target.ChildPage).RowIds
                .Append((long)target.RowId)
                .Order());
        after.Children.Where(child => child.PageNumber != target.ChildPage)
            .Should()
            .BeEquivalentTo(
                before.Children.Where(child => child.PageNumber != target.ChildPage),
                options => options.WithStrictOrdering());
    }

    private static void AssertUnchanged(Topology before, Topology after)
    {
        after.Header.Should().Be(before.Header);
        after.PageCount.Should().Be(before.PageCount);
        after.TableRootPage.Should().Be(before.TableRootPage);
        after.IndexRootPage.Should().Be(before.IndexRootPage);
        after.IndexRootCellCount.Should().Be(before.IndexRootCellCount);
        after.IndexRootImage.Should().Equal(before.IndexRootImage);
        after.Children.Should().BeEquivalentTo(before.Children, options => options.WithStrictOrdering());
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

    private static byte[] BuildIndexRecord(int rowId, SqliteTextEncoding textEncoding)
        => SqliteRecordCodec.Encode(
            Enumerable.Repeat(SqlValue.Integer(rowId), RepeatedColumnCount)
                .Append(SqlValue.Integer(rowId))
                .ToArray(),
            textEncoding);

    private static void VerifyWithSqlite(string path, InsertionTarget target)
    {
        var verificationPath = path + ".verify.db";
        File.Copy(path, verificationPath, overwrite: true);
        try
        {
            using var sqlite = new MsData.SqliteConnection($"Data Source={verificationPath}");
            sqlite.Open();
            using (var integrity = sqlite.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check;";
                integrity.ExecuteScalar().Should().Be("ok");
            }

            using var lookup = sqlite.CreateCommand();
            lookup.CommandText =
                $"SELECT id FROM target INDEXED BY {IndexName} WHERE id = {target.RowId};";
            Convert.ToInt64(lookup.ExecuteScalar()).Should().Be(target.RowId);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(verificationPath);
        }
    }

    private static string RepeatedIndexColumns(int count)
        => string.Join(", ", Enumerable.Repeat("id", count));

    private static string InsertStatement(IEnumerable<int> rowIds)
        => $"INSERT INTO target VALUES {string.Join(", ", rowIds.Select(rowId => $"({rowId})"))};";

    private static string InsertStatement(int rowId) => InsertStatement([rowId]);

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

    private static long Id(EmbeddedConnection connection, int rowId)
    {
        using var statement = connection.Prepare($"SELECT id FROM target WHERE id = {rowId};");
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static string CreateDatabasePath(string name)
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "managed-secondary-index-interior-child-insertion-tests");
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

    private sealed record InsertionTarget(int RowId, int RowCount, uint ChildPage);

    private sealed record LeafTopology(uint PageNumber, IReadOnlyList<long> RowIds);

    private sealed record Topology(
        SqliteDatabaseHeader Header,
        uint PageCount,
        uint TableRootPage,
        uint IndexRootPage,
        int IndexRootCellCount,
        byte[] IndexRootImage,
        IReadOnlyList<LeafTopology> Children);
}
