using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedNestedSecondaryIndexLeafInsertionTests
{
    private const int InitialRowCount = 97;
    private const int RepeatedColumnCount = 64;

    [Test]
    public void NestedSecondaryIndexLeafInsertionPersistsOnlyTheRoutedLeafAndPassesExternalSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath("integrity");
        try
        {
            var target = SeedNestedTopology(PhysicalFileSystem.Instance, path);
            var before = ReadSnapshot(PhysicalFileSystem.Instance, path);

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
                Execute(connection, InsertStatement(target.RowId));

            var after = ReadSnapshot(PhysicalFileSystem.Instance, path);
            AssertBoundedMutation(before, after, target);

            using (var reopened = EmbeddedDatabase.OpenFile(path, readOnly: true))
            using (var connection = reopened.Connect())
            {
                Count(connection).Should().Be(InitialRowCount + 1);
                IdByCode(connection, target.RowId).Should().Be(target.RowId);
            }

            VerifyWithSqlite(path, target.RowId);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void EveryInterruptedNestedSecondaryIndexLeafInsertionFrameRecoversThePriorCatalog()
    {
        for (var failedFrame = 1; failedFrame <= 3; failedFrame++)
        {
            var faults = new DeterministicFaultInjector();
            var fileSystem = new InMemoryFileSystem(faults);
            var path = $"nested-secondary-index-insertion-{failedFrame}.db";
            var target = SeedNestedTopology(fileSystem, path);
            var before = ReadSnapshot(fileSystem, path);

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
                Count(connection).Should().Be(InitialRowCount);
                CountByCode(connection, target.RowId).Should().Be(0);
            }

            AssertUnchanged(before, ReadSnapshot(fileSystem, path));
        }
    }

    private static NestedInsertionTarget SeedNestedTopology(IFileSystem fileSystem, string path)
    {
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, code TEXT NOT NULL);");
            Execute(connection, InsertStatement(Enumerable.Range(1, InitialRowCount).Select(index => (index * 2) - 1)));
            Execute(connection, $"CREATE INDEX t_code_binary ON t({RepeatedBinaryIndexColumns()});");
        }
        AppendFreelistPage(fileSystem, path);

        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        header.FreelistPageCount.Should().Be(1);
        header.FirstFreelistTrunkPage.Should().Be(pager.CommittedPageCount);
        header.DatabaseSizeInPages.Should().Be(pager.CommittedPageCount);

        var tableRootPage = FindRootPage(pager, header, "table", "t");
        SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(tableRootPage)).PageType
            .Should()
            .Be(SqliteBtreePageType.TableLeaf);

        var indexRootPage = FindRootPage(pager, header, "index", "t_code_binary");
        for (var rowId = 2; rowId <= (InitialRowCount * 2); rowId += 2)
        {
            if (TryFindNestedLeaf(pager, header, indexRootPage, rowId, out var leafPage))
                return new NestedInsertionTarget(rowId, tableRootPage, indexRootPage, leafPage);
        }

        throw new InvalidOperationException("Unable to create a bounded nested secondary-index insertion topology.");
    }

    private static bool TryFindNestedLeaf(
        SqlitePager pager,
        SqliteDatabaseHeader header,
        uint rootPage,
        int rowId,
        out uint leafPage)
    {
        leafPage = 0;
        var record = BuildIndexRecord(rowId, header.TextEncoding);
        if (SqlitePayloadLayout.Calculate(
                SqliteBtreePageType.IndexLeaf,
                checked((ulong)record.Length),
                header.UsableSpace).UsesOverflow)
        {
            return false;
        }

        var rootImage = pager.ReadCommittedPage(rootPage);
        if (SqliteBtreePageHeader.Parse(rootImage).PageType != SqliteBtreePageType.IndexInterior)
            return false;

        var root = SqliteIndexInteriorPageView.Parse(
            rootImage,
            header.UsableSpace,
            header.TextEncoding);
        if (root.Cells.Count == 0)
            return false;

        var route = root.SearchChild(record);
        if (route.IsSeparatorKey || route.ChildPage == 0)
            return false;

        var interiorImage = pager.ReadCommittedPage(route.ChildPage);
        if (SqliteBtreePageHeader.Parse(interiorImage).PageType != SqliteBtreePageType.IndexInterior)
            return false;

        var interior = SqliteIndexInteriorPageView.Parse(
            interiorImage,
            header.UsableSpace,
            header.TextEncoding);
        if (interior.Cells.Count == 0)
            return false;

        route = interior.SearchChild(record);
        if (route.IsSeparatorKey || route.ChildPage == 0)
            return false;

        var leafImage = pager.ReadCommittedPage(route.ChildPage);
        if (SqliteBtreePageHeader.Parse(leafImage).PageType != SqliteBtreePageType.IndexLeaf)
            return false;

        var leaf = SqliteIndexLeafPageView.Parse(
            leafImage,
            header.UsableSpace,
            header.TextEncoding);
        if (leaf.Cells.Count == 0 || leaf.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null))
            return false;

        var comparer = new SqliteIndexRecordComparer(header.TextEncoding);
        var records = Enumerable.Range(0, leaf.Cells.Count).Select(leaf.GetRecord).ToList();
        var insertion = records.FindIndex(existing => comparer.Compare(record, existing) < 0);
        if (insertion < 0)
            insertion = records.Count;
        if ((insertion > 0 && comparer.Compare(records[insertion - 1], record) >= 0)
            || (insertion < records.Count && comparer.Compare(record, records[insertion]) >= 0))
        {
            return false;
        }

        records.Insert(insertion, record);
        if (!FitsIndexLeaf(records, header, comparer))
            return false;

        leafPage = route.ChildPage;
        return true;
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

    private static void AppendFreelistPage(IFileSystem fileSystem, string path)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal");
        var sourcePageCount = pager.CommittedPageCount;
        var targetPageCount = checked(sourcePageCount + 1);
        var schemaPage = pager.ReadCommittedPage(1);
        var header = SqliteDatabaseHeader.Parse(schemaPage);
        var newChangeCounter = checked(header.ChangeCounter + 1);
        var newHeader = header with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
            DatabaseSizeInPages = targetPageCount,
            FirstFreelistTrunkPage = targetPageCount,
            FreelistPageCount = 1,
        };
        newHeader.WriteTo(schemaPage);

        using var transaction = pager.BeginTransaction(targetPageCount);
        transaction.WritePage(targetPageCount, new byte[pager.PageSize]);
        transaction.WritePage(1, schemaPage);
        transaction.Commit();
    }

    private static Snapshot ReadSnapshot(IFileSystem fileSystem, string path)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var pages = Enumerable.Range(1, checked((int)pager.CommittedPageCount))
            .ToDictionary(
                pageNumber => checked((uint)pageNumber),
                pageNumber => pager.ReadCommittedPage(checked((uint)pageNumber)));
        return new Snapshot(header, pager.CommittedPageCount, pages);
    }

    private static void AssertBoundedMutation(
        Snapshot before,
        Snapshot after,
        NestedInsertionTarget target)
    {
        after.PageCount.Should().Be(before.PageCount);
        after.Header.DatabaseSizeInPages.Should().Be(before.Header.DatabaseSizeInPages);
        after.Header.ChangeCounter.Should().Be(before.Header.ChangeCounter + 1);
        after.Header.VersionValidFor.Should().Be(after.Header.ChangeCounter);
        after.Header.FreelistPageCount.Should().Be(before.Header.FreelistPageCount);
        after.Header.FirstFreelistTrunkPage.Should().Be(before.Header.FirstFreelistTrunkPage);

        var changedPages = after.Pages
            .Where(entry => !entry.Value.AsSpan().SequenceEqual(before.Pages[entry.Key]))
            .Select(entry => entry.Key)
            .Order()
            .ToArray();
        changedPages.Should().Equal(new uint[] { 1, target.TableRootPage, target.IndexLeafPage }.Order());
        after.Pages[target.IndexRootPage].Should().Equal(before.Pages[target.IndexRootPage]);
    }

    private static void AssertUnchanged(Snapshot before, Snapshot after)
    {
        after.Header.Should().Be(before.Header);
        after.PageCount.Should().Be(before.PageCount);
        after.Pages.Should().BeEquivalentTo(before.Pages);
    }

    private static uint FindRootPage(
        SqlitePager pager,
        SqliteDatabaseHeader header,
        string type,
        string name)
    {
        return checked((uint)ReadSchemaRecords(pager, header, 1, isFirstPage: true)
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
            Enumerable.Repeat(SqlValue.Text(Code(rowId)), RepeatedColumnCount)
                .Append(SqlValue.Integer(rowId))
                .ToArray(),
            textEncoding);

    private static string RepeatedBinaryIndexColumns()
        => string.Join(", ", Enumerable.Repeat("code COLLATE BINARY", RepeatedColumnCount));

    private static string InsertStatement(IEnumerable<int> rowIds)
        => $"INSERT INTO t VALUES {string.Join(", ", rowIds.Select(rowId => $"({rowId}, '{Code(rowId)}')"))};";

    private static string InsertStatement(int rowId) => InsertStatement([rowId]);

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private static long Count(EmbeddedConnection connection)
    {
        using var statement = connection.Prepare("SELECT COUNT(*) FROM t;");
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static long CountByCode(EmbeddedConnection connection, int rowId)
    {
        using var statement = connection.Prepare(
            $"SELECT COUNT(*) FROM t WHERE code = '{Code(rowId)}';");
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static long IdByCode(EmbeddedConnection connection, int rowId)
    {
        using var statement = connection.Prepare(
            $"SELECT id FROM t WHERE code = '{Code(rowId)}';");
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static void VerifyWithSqlite(string path, int rowId)
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
            lookup.CommandText =
                $"SELECT id FROM t INDEXED BY t_code_binary WHERE code = '{Code(rowId)}';";
            Convert.ToInt64(lookup.ExecuteScalar()).Should().Be(rowId);
        }
        finally
        {
            DeleteDatabase(verificationPath);
        }
    }

    private static string Code(int rowId) => $"v{rowId:D4}xxxxxxx";

    private static string CreateDatabasePath(string name)
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "managed-nested-secondary-index-leaf-insertion-tests");
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

    private sealed record NestedInsertionTarget(
        int RowId,
        uint TableRootPage,
        uint IndexRootPage,
        uint IndexLeafPage);

    private sealed record Snapshot(
        SqliteDatabaseHeader Header,
        uint PageCount,
        IReadOnlyDictionary<uint, byte[]> Pages);
}
