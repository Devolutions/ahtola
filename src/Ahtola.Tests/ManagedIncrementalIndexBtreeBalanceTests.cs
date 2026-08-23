using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedIncrementalIndexBtreeBalanceTests
{
    private const int PageSize = SqlitePageSize.Minimum;
    private const uint RootPage = 2;

    [Test]
    public void InteriorSeparatorDeletesMergePagesAndCollapseRootWithoutMaintenanceFallback()
    {
        var committed = new[]
        {
            new byte[PageSize],
            new SqliteIndexLeafPageBuilder(
                PageSize,
                PageSize,
                new SqliteIndexRecordComparer(SqliteTextEncoding.Utf8)).Build(),
        };
        var io = new SqliteStagedBtreePageIo(
            pageNumber => (byte[])committed[checked((int)pageNumber) - 1].Clone(),
            committedPageCount: 2,
            pageSize: PageSize,
            usableSpace: PageSize);
        var comparer = new SqliteIndexRecordComparer(SqliteTextEncoding.Utf8);
        var writer = new SqliteIncrementalIndexBtree(io, comparer, SqliteTextEncoding.Utf8);
        var records = Enumerable.Range(1, 30)
            .Select(BuildRecord)
            .ToList();

        foreach (var record in records)
            writer.Insert(RootPage, record);

        SqliteBtreePageHeader.Parse(io.ReadPage(RootPage)).PageType
            .Should().Be(SqliteBtreePageType.IndexInterior);
        var root = SqliteIndexInteriorPageView.Parse(
            io.ReadPage(RootPage),
            PageSize,
            SqliteTextEncoding.Utf8,
            overflowReader: new SqliteOverflowChainReader(io),
            recordComparer: comparer);
        var separator = root.GetRecord(root.Cells.Count / 2);

        writer.Delete(RootPage, separator);
        records.RemoveAll(record => record.AsSpan().SequenceEqual(separator));
        ReadTree(io, comparer)
            .Select(ReadRowId)
            .Should()
            .Equal(records.Select(ReadRowId));

        foreach (var record in records.ToArray())
            writer.Delete(RootPage, record);

        var collapsedRoot = SqliteBtreePageHeader.Parse(io.ReadPage(RootPage));
        collapsedRoot.PageType.Should().Be(SqliteBtreePageType.IndexLeaf);
        collapsedRoot.CellCount.Should().Be(0);
        io.FreelistPageCount.Should().BeGreaterThan(0u);
    }

    [Test]
    public void TableDeleteRebalancesThreeSiblingsWhenNeitherPairCanBePacked()
    {
        static SqliteTableLeafCell Cell(long rowId, int payloadLength)
            => SqliteTableLeafCell.Create(rowId, new byte[payloadLength], PageSize);

        var left = new[] { Cell(1, 95), Cell(2, 95), Cell(3, 95) };
        var middle = new[] { Cell(4, 325), Cell(5, 145) };
        var right = new[] { Cell(6, 195), Cell(7, 195) };
        var root = new SqliteTableInteriorPageBuilder(PageSize, PageSize, rightMostChildPage: 5);
        root.Append(SqliteTableInteriorCell.Create(leftChildPage: 3, rowId: 3));
        root.Append(SqliteTableInteriorCell.Create(leftChildPage: 4, rowId: 5));
        var committed = new[]
        {
            new byte[PageSize],
            root.Build(),
            BuildTableLeaf(left),
            BuildTableLeaf(middle),
            BuildTableLeaf(right),
        };
        var io = new SqliteStagedBtreePageIo(
            pageNumber => (byte[])committed[checked((int)pageNumber) - 1].Clone(),
            committedPageCount: 5,
            pageSize: PageSize,
            usableSpace: PageSize);

        new SqliteIncrementalTableBtree(io).Delete(RootPage, rowId: 1);

        io.StagedPages.Keys.Should().Contain([2u, 3u, 4u, 5u]);
        var cursor = new SqliteTableBtreeCursor(io);
        cursor.TrySeek(RootPage, 1, out _).Should().BeFalse();
        foreach (var rowId in Enumerable.Range(2, 6))
            cursor.TrySeek(RootPage, rowId, out _).Should().BeTrue($"rowid {rowId}");
    }

    [Test]
    public void DeepInteriorSeparatorDeleteWritesOnlyTheBalancedPaths()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "deep-index-separator-delete.db";
        CreateIndexedTarget(fileSystem, path);

        uint pageCount;
        long separatorRowId;
        using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
        {
            pageCount = pager.CommittedPageCount;
            separatorRowId = ReadRootSeparatorRowId(pager);
        }

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
            Execute(connection, $"DELETE FROM target WHERE id = {separatorRowId};");
            (faults.GetOperationCount(FileSystemOperation.Write) - writesBefore)
                .Should().BeLessThan(pageCount);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var reopenedConnection = reopened.Connect();
        QueryInteger(reopenedConnection, "SELECT COUNT(*) FROM target;").Should().Be(179);
        QueryInteger(
            reopenedConnection,
            $"SELECT COUNT(*) FROM target WHERE id = {separatorRowId};").Should().Be(0);
    }

    [Test]
    public void RepeatedDeepDeletesCollapseTheIndexRootIncrementally()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "deep-index-root-collapse.db";
        CreateIndexedTarget(fileSystem, path);

        uint pageCount;
        uint indexRoot;
        using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
        {
            pageCount = pager.CommittedPageCount;
            var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            indexRoot = FindRootPage(pager, header, "index", "target_value");
        }

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            for (var id = 1; id <= 180; id++)
            {
                var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
                Execute(connection, $"DELETE FROM target WHERE id = {id};");
                (faults.GetOperationCount(FileSystemOperation.Write) - writesBefore)
                    .Should().BeLessThan(pageCount, $"delete {id} must remain incremental");
            }
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var reopenedConnection = reopened.Connect();
        QueryInteger(reopenedConnection, "SELECT COUNT(*) FROM target;").Should().Be(0);
        using var pagerAfter = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var rootHeader = SqliteBtreePageHeader.Parse(pagerAfter.ReadCommittedPage(indexRoot));
        rootHeader.PageType.Should().Be(SqliteBtreePageType.IndexLeaf);
        rootHeader.CellCount.Should().Be(0);
        SqliteDatabaseHeader.Parse(pagerAfter.ReadCommittedPage(1))
            .FreelistPageCount.Should().BeGreaterThan(0u);
    }

    [Test]
    public void SeparatorDeleteReopensAndPassesExternalSqliteIntegrityCheck()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"ahtola-index-balance-{Guid.NewGuid():N}.db");
        try
        {
            CreateIndexedTarget(PhysicalFileSystem.Instance, path);

            long separatorRowId;
            using (var pager = SqlitePager.Open(
                       PhysicalFileSystem.Instance,
                       path,
                       path + "-wal",
                       readOnly: true))
            {
                separatorRowId = ReadRootSeparatorRowId(pager);
            }

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
                Execute(connection, $"DELETE FROM target WHERE id = {separatorRowId};");

            using (var reopened = EmbeddedDatabase.OpenFile(path, readOnly: true))
            using (var connection = reopened.Connect())
            {
                QueryInteger(connection, "SELECT COUNT(*) FROM target;").Should().Be(179);
                QueryInteger(
                    connection,
                    $"SELECT COUNT(*) FROM target WHERE id = {separatorRowId};").Should().Be(0);
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path}");
            sqlite.Open();
            using (var integrity = sqlite.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check;";
                integrity.ExecuteScalar().Should().Be("ok");
            }
            using var count = sqlite.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM target;";
            Convert.ToInt64(count.ExecuteScalar()).Should().Be(179);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            foreach (var candidate in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
            {
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
        }
    }

    private static byte[] BuildRecord(int id)
    {
        return SqliteRecordCodec.Encode(
            [SqlValue.Text($"value-{id:D4}-{new string('x', 44)}"), SqlValue.Integer(id)],
            SqliteTextEncoding.Utf8);
    }

    private static void CreateIndexedTarget(IFileSystem fileSystem, string path)
    {
        var header = SqliteDatabaseHeader.CreateDefault() with { PageSize = PageSize };
        using (SqlitePager.Create(
                   fileSystem,
                   path,
                   path + "-wal",
                   SqliteWalHeader.Create(PageSize, salt1: 101, salt2: 103),
                   header))
        {
        }

        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, value TEXT);");
        var rows = Enumerable.Range(1, 180)
            .Select(id => $"({id}, 'value-{id:D4}-{new string('x', 48)}')");
        Execute(connection, $"INSERT INTO target VALUES {string.Join(", ", rows)};");
        Execute(connection, "CREATE INDEX target_value ON target(value);");
    }

    private static long ReadRootSeparatorRowId(SqlitePager pager)
    {
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var indexRoot = FindRootPage(pager, header, "index", "target_value");
        var comparer = new SqliteIndexRecordComparer(header.TextEncoding);
        var root = SqliteIndexInteriorPageView.Parse(
            pager.ReadCommittedPage(indexRoot),
            header.UsableSpace,
            header.TextEncoding,
            overflowReader: new SqliteOverflowChainReader(pager, header),
            recordComparer: comparer);
        root.Cells.Should().NotBeEmpty();
        return SqliteRecordCodec.Decode(
            root.GetRecord(root.Cells.Count / 2),
            header.TextEncoding)[^1].AsInteger();
    }

    private static byte[] BuildTableLeaf(IEnumerable<SqliteTableLeafCell> cells)
    {
        var builder = new SqliteTableLeafPageBuilder(PageSize, PageSize);
        foreach (var cell in cells)
            builder.Append(cell);
        return builder.Build();
    }

    private static long ReadRowId(byte[] record)
        => SqliteRecordCodec.Decode(record, SqliteTextEncoding.Utf8)[^1].AsInteger();

    private static List<byte[]> ReadTree(
        ISqliteBtreePageIo io,
        SqliteIndexRecordComparer comparer)
    {
        var records = new List<byte[]>();
        ReadTreePage(io, comparer, RootPage, new HashSet<uint>(), records);
        return records;
    }

    private static void ReadTreePage(
        ISqliteBtreePageIo io,
        SqliteIndexRecordComparer comparer,
        uint pageNumber,
        HashSet<uint> visited,
        List<byte[]> records)
    {
        visited.Add(pageNumber).Should().BeTrue($"page {pageNumber} must not be visited twice");
        var image = io.ReadPage(pageNumber);
        var header = SqliteBtreePageHeader.Parse(image);
        var overflow = new SqliteOverflowChainReader(io);
        if (header.PageType == SqliteBtreePageType.IndexLeaf)
        {
            var leaf = SqliteIndexLeafPageView.Parse(
                image,
                io.UsableSpace,
                SqliteTextEncoding.Utf8,
                overflowReader: overflow,
                recordComparer: comparer);
            for (var index = 0; index < leaf.Cells.Count; index++)
                records.Add(leaf.GetRecord(index));
            return;
        }

        header.PageType.Should().Be(SqliteBtreePageType.IndexInterior);
        var interior = SqliteIndexInteriorPageView.Parse(
            image,
            io.UsableSpace,
            SqliteTextEncoding.Utf8,
            overflowReader: overflow,
            recordComparer: comparer);
        for (var index = 0; index <= interior.Cells.Count; index++)
        {
            var childPage = index == interior.Cells.Count
                ? interior.Header.RightMostChildPage
                : interior.Cells[index].Cell.LeftChildPage;
            ReadTreePage(io, comparer, childPage, visited, records);
            if (index < interior.Cells.Count)
                records.Add(interior.GetRecord(index));
        }
    }

    private static uint FindRootPage(
        SqlitePager pager,
        SqliteDatabaseHeader header,
        string type,
        string name)
    {
        var schema = SqliteTableLeafPageView.Parse(
            pager.ReadCommittedPage(1),
            header.UsableSpace,
            isFirstPage: true);
        return checked((uint)schema.Cells
            .Select(cell => SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding))
            .Single(values => values[0].AsText() == type && values[1].AsText() == name)[3]
            .AsInteger());
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private static long QueryInteger(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }
}
