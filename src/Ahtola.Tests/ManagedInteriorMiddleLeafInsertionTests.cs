using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedInteriorMiddleLeafInsertionTests
{
    private const int PageSize = SqlitePageSize.Minimum;
    private const int PayloadLength = 80;

    [Test]
    public void MiddleLeafInsertionPersistsReopensAndPassesSqliteIntegrityCheck()
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
            after.PageCount.Should().Be(before.PageCount);
            after.RootPage.Should().Be(before.RootPage);
            after.RootImage.Should().Equal(before.RootImage);
            after.ChildPages.Should().Equal(before.ChildPages);
            after.Header.ChangeCounter.Should().Be(before.Header.ChangeCounter + 1);
            after.Header.VersionValidFor.Should().Be(after.Header.ChangeCounter);
            after.LeafRows[target.ChildPage].Should().Contain(target.RowId);

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                Count(connection).Should().Be(target.RowCount + 1);
                Payload(connection, target.RowId).Should().Be(PayloadValue(target.RowId));
            }

            VerifyWithSqlite(path, target);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void InterruptedMiddleLeafInsertionRecoversThePriorTree()
    {
        for (var failedWrite = 1; failedWrite <= 2; failedWrite++)
        {
            var faults = new DeterministicFaultInjector();
            var fileSystem = new InMemoryFileSystem(faults);
            var path = $"interior-middle-leaf-insertion-wal-{failedWrite}.db";
            var target = SeedInsertionTopology(fileSystem, path);
            var before = ReadTopology(fileSystem, path);

            using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = database.Connect())
            {
                faults.FailOnOccurrence(
                    FileSystemOperation.Write,
                    faults.GetOperationCount(FileSystemOperation.Write) + failedWrite);
                Assert.Throws<IOException>(() => Execute(connection, InsertStatement(target.RowId)));
            }

            using (var recovered = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = recovered.Connect())
            {
                Count(connection).Should().Be(target.RowCount);
                CountById(connection, target.RowId).Should().Be(0);
            }

            ReadTopology(fileSystem, path).Should().BeEquivalentTo(
                before,
                options => options.WithStrictOrdering());
        }
    }

    private static InsertionTarget SeedInsertionTopology(IFileSystem fileSystem, string path)
    {
        CreateMinimumPageDatabase(fileSystem, path);
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, payload TEXT NOT NULL);");
            Execute(connection, Enumerable.Range(1, 5).Select(value => (value * 2) - 1));
        }

        var rowCount = 5;
        foreach (var rowId in SeedRowIds())
        {
            if (TryFindMiddleLeafInsertionTarget(fileSystem, path, rowCount, out var target))
                return target;

            using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
            using var connection = database.Connect();
            Execute(connection, InsertStatement(rowId));
            rowCount++;
        }

        throw new InvalidOperationException("Unable to create a bounded middle table-leaf insertion topology.");
    }

    /// <summary>
    /// Odd row ids 11..127 in two passes. The first pass only appends, which
    /// packs pages completely; the second fills the gaps it left, so those
    /// insertions split middle leaves and leave free space behind for the
    /// bounded insertion under test.
    /// </summary>
    private static IEnumerable<int> SeedRowIds()
    {
        for (var rowId = 11; rowId <= 127; rowId += 4)
            yield return rowId;

        for (var rowId = 13; rowId <= 127; rowId += 4)
            yield return rowId;
    }

    private static bool TryFindMiddleLeafInsertionTarget(
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

        var rootPage = FindTableRootPage(pager, header);
        if (SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(rootPage)).PageType
            != SqliteBtreePageType.TableInterior)
        {
            return false;
        }

        var root = SqliteTableInteriorPageView.Parse(
            pager.ReadCommittedPage(rootPage),
            header.UsableSpace);
        if (root.Cells.Count < 2)
            return false;

        for (var rowId = 2; rowId < 128; rowId += 2)
        {
            var route = root.SearchChild(rowId);
            if (route.IsSeparatorKey
                || route.ChildIndex <= 0
                || route.ChildIndex >= root.Cells.Count)
            {
                continue;
            }

            var leaf = SqliteTableLeafPageView.Parse(
                pager.ReadCommittedPage(route.ChildPage),
                header.UsableSpace);
            if (leaf.Cells.Count == 0 || leaf.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null))
                continue;

            var insertion = leaf.Search(rowId);
            if (insertion.IsExact)
                continue;

            var cells = leaf.Cells.Select(cell => cell.Cell).ToList();
            cells.Insert(
                insertion.Index,
                SqliteTableLeafCell.Create(rowId, RecordFor(rowId), header.UsableSpace));
            try
            {
                var builder = new SqliteTableLeafPageBuilder(header.PageSize, header.UsableSpace);
                foreach (var cell in cells)
                    builder.Append(cell);
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
        var rootPage = FindTableRootPage(pager, header);
        var rootImage = pager.ReadCommittedPage(rootPage);
        var root = SqliteTableInteriorPageView.Parse(rootImage, header.UsableSpace);
        var childPages = root.Cells
            .Select(cell => cell.Cell.LeftChildPage)
            .Append(root.Header.RightMostChildPage)
            .ToArray();
        var leafRows = childPages.ToDictionary(
            pageNumber => pageNumber,
            pageNumber => SqliteTableLeafPageView.Parse(
                    pager.ReadCommittedPage(pageNumber),
                    header.UsableSpace)
                .Cells
                .Select(cell => cell.Cell.RowId)
                .ToArray());
        return new Topology(header, pager.CommittedPageCount, rootPage, rootImage, childPages, leafRows);
    }

    private static uint FindTableRootPage(SqlitePager pager, SqliteDatabaseHeader header)
    {
        var schema = SqliteTableLeafPageView.Parse(
            pager.ReadCommittedPage(1),
            header.UsableSpace,
            isFirstPage: true);
        return checked((uint)schema.Cells
            .Select(cell => SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding))
            .Single(values => values[0].AsText() == "table" && values[1].AsText() == "target")[3]
            .AsInteger());
    }

    private static void VerifyWithSqlite(string path, InsertionTarget target)
    {
        var verificationPath = path + ".verify.db";
        File.Copy(path, verificationPath, overwrite: true);
        try
        {
            using var sqlite = new MsData.SqliteConnection($"Data Source={verificationPath};Pooling=False");
            sqlite.Open();
            using (var integrity = sqlite.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check;";
                integrity.ExecuteScalar().Should().Be("ok");
            }

            using var lookup = sqlite.CreateCommand();
            lookup.CommandText = $"SELECT payload FROM target WHERE id = {target.RowId};";
            lookup.ExecuteScalar().Should().Be(PayloadValue(target.RowId));
        }
        finally
        {
            DeleteDatabase(verificationPath);
        }
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

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private static void Execute(EmbeddedConnection connection, IEnumerable<int> rowIds)
    {
        foreach (var rowId in rowIds)
            Execute(connection, InsertStatement(rowId));
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

    private static string Payload(EmbeddedConnection connection, int rowId)
    {
        using var statement = connection.Prepare($"SELECT payload FROM target WHERE id = {rowId};");
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsText();
    }

    private static byte[] RecordFor(int rowId)
        => SqliteRecordCodec.Encode([SqlValue.Null, SqlValue.Text(PayloadValue(rowId))]);

    private static string InsertStatement(int rowId)
        => $"INSERT INTO target VALUES ({rowId}, '{PayloadValue(rowId)}');";

    private static string PayloadValue(int rowId)
        => $"payload-{rowId:D3}-{new string('m', PayloadLength)}";

    private static string CreateDatabasePath(string name)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "managed-interior-middle-leaf-insertion-tests");
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

    private sealed record Topology(
        SqliteDatabaseHeader Header,
        uint PageCount,
        uint RootPage,
        byte[] RootImage,
        uint[] ChildPages,
        IReadOnlyDictionary<uint, long[]> LeafRows);
}
