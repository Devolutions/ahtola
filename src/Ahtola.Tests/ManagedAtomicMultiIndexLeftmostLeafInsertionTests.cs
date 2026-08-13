using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedAtomicMultiIndexLeftmostLeafInsertionTests
{
    private const int PageSize = SqlitePageSize.Minimum;
    private const int RepeatedColumnCount = 4;
    private const int InitialRowCount = 7;
    private const string CodePadding = "xxxxxxxxxxxx";
    private static readonly string[] IndexNames =
        ["target_code_binary_a", "target_code_binary_b", "target_code_binary_c"];

    [Test]
    public void MultiIndexLeftmostLeafInsertionPersistsReopensAndPassesSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath("integrity");
        try
        {
            var target = SeedLeftmostInsertionTopology(PhysicalFileSystem.Instance, path);
            var before = ReadTopology(PhysicalFileSystem.Instance, path);

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
                Execute(connection, InsertStatement(target.RowId));

            var after = ReadTopology(PhysicalFileSystem.Instance, path);
            AssertBoundedLeftmostInsertion(before, after, target);
            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                Count(connection).Should().Be(target.RowCount + 1);
                IdByCode(connection, target.RowId).Should().Be(target.RowId);
            }

            VerifyWithSqlite(path, target);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void MultiIndexLeftmostLeafInsertionCommitsOnlyTheTableAndThreeRoutedLeaves()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "multi-index-leftmost-leaf-insertion.db";
        var target = SeedLeftmostInsertionTopology(fileSystem, path);
        var before = ReadTopology(fileSystem, path);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
            Execute(connection, InsertStatement(target.RowId));
            (faults.GetOperationCount(FileSystemOperation.Write) - writesBefore).Should().Be(11);
        }

        AssertBoundedLeftmostInsertion(before, ReadTopology(fileSystem, path), target);
    }

    [Test]
    public void EveryInterruptedMultiIndexLeftmostLeafInsertionFrameRecoversThePriorCatalog()
    {
        for (var failedFrame = 1; failedFrame <= IndexNames.Length + 2; failedFrame++)
        {
            var faults = new DeterministicFaultInjector();
            var fileSystem = new InMemoryFileSystem(faults);
            var path = $"multi-index-leftmost-leaf-insertion-wal-{failedFrame}.db";
            var target = SeedLeftmostInsertionTopology(fileSystem, path);
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
                CountByCode(connection, target.RowId).Should().Be(0);
            }

            ReadTopology(fileSystem, path).Should().BeEquivalentTo(
                before,
                options => options.WithStrictOrdering());
        }
    }

    private static InsertionTarget SeedLeftmostInsertionTopology(IFileSystem fileSystem, string path)
    {
        CreateMinimumPageDatabase(fileSystem, path);
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, code TEXT NOT NULL);");
            Execute(connection, InsertStatement(Enumerable.Range(1, InitialRowCount).Select(value => (value * 2) - 1)));
            foreach (var indexName in IndexNames)
            {
                Execute(
                    connection,
                    $"CREATE UNIQUE INDEX {indexName} ON target({RepeatedBinaryIndexColumns()});");
            }
        }

        var rowCount = InitialRowCount;
        for (var nextOddRowId = (InitialRowCount * 2) + 1; nextOddRowId <= 95; nextOddRowId += 2)
        {
            if (TryFindLeftmostInsertionTarget(fileSystem, path, rowCount, out var target))
                return target;

            using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
            using var connection = database.Connect();
            Execute(connection, InsertStatement(nextOddRowId));
            rowCount++;
        }

        throw new InvalidOperationException(
            "Unable to create a bounded multi-index leftmost-child insertion topology.");
    }

    private static bool TryFindLeftmostInsertionTarget(
        IFileSystem fileSystem,
        string path,
        int rowCount,
        out InsertionTarget target)
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
        if (SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(tableRootPage)).PageType
            != SqliteBtreePageType.TableLeaf)
        {
            return false;
        }

        const int rowId = 0;
        var indexes = new List<IndexInsertionTarget>(IndexNames.Length);
        foreach (var indexName in IndexNames)
        {
            var rootPage = FindRootPage(pager, header, "index", indexName);
            var rootImage = pager.ReadCommittedPage(rootPage);
            if (SqliteBtreePageHeader.Parse(rootImage).PageType != SqliteBtreePageType.IndexInterior)
                return false;

            var root = SqliteIndexInteriorPageView.Parse(
                rootImage,
                header.UsableSpace,
                header.TextEncoding);
            if (root.Cells.Count == 0 || root.Cells.Any(cell => cell.Cell.Key.FirstOverflowPage is not null))
                return false;

            var record = BuildIndexRecord(rowId, header.TextEncoding);
            if (SqlitePayloadLayout.Calculate(
                    SqliteBtreePageType.IndexLeaf,
                    checked((ulong)record.Length),
                    header.UsableSpace).UsesOverflow)
            {
                return false;
            }

            var route = root.SearchChild(record);
            if (route.IsSeparatorKey || route.ChildIndex != 0)
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
            records.Insert(0, record);
            if (comparer.Compare(record, records[1]) >= 0 || !FitsIndexLeaf(records, header, comparer))
                return false;

            indexes.Add(new IndexInsertionTarget(indexName, rootPage, route.ChildPage));
        }

        target = new InsertionTarget(rowId, rowCount, indexes);
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

    private static Topology ReadTopology(IFileSystem fileSystem, string path)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var tableRootPage = FindRootPage(pager, header, "table", "target");
        var indexes = IndexNames.Select(indexName =>
        {
            var rootPage = FindRootPage(pager, header, "index", indexName);
            var rootImage = pager.ReadCommittedPage(rootPage);
            var root = SqliteIndexInteriorPageView.Parse(
                rootImage,
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
            return new IndexTopology(indexName, rootPage, rootImage, children);
        }).ToArray();

        return new Topology(header, pager.CommittedPageCount, tableRootPage, indexes);
    }

    private static void AssertBoundedLeftmostInsertion(
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

        foreach (var indexTarget in target.Indexes)
        {
            var beforeIndex = before.Indexes.Single(index => index.Name == indexTarget.Name);
            var afterIndex = after.Indexes.Single(index => index.Name == indexTarget.Name);
            afterIndex.RootPage.Should().Be(indexTarget.RootPage);
            afterIndex.RootImage.Should().Equal(beforeIndex.RootImage);
            afterIndex.Children.Select(child => child.PageNumber)
                .Should()
                .Equal(beforeIndex.Children.Select(child => child.PageNumber));
            indexTarget.LeafPage.Should().Be(beforeIndex.Children[0].PageNumber);
            afterIndex.Children[0].RowIds.Should()
                .Equal(beforeIndex.Children[0].RowIds.Append((long)target.RowId).Order());
            afterIndex.Children.Skip(1).Should().BeEquivalentTo(
                beforeIndex.Children.Skip(1),
                options => options.WithStrictOrdering());
        }
    }

    private static void VerifyWithSqlite(string path, InsertionTarget target)
    {
        var verificationPath = path + ".verify.db";
        File.Copy(path, verificationPath, overwrite: true);
        try
        {
            // Avoid the global SQLite pool while parallel fixtures remove their files.
            using var sqlite = new MsData.SqliteConnection($"Data Source={verificationPath};Pooling=False");
            sqlite.Open();
            using (var integrity = sqlite.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check;";
                integrity.ExecuteScalar().Should().Be("ok");
            }

            foreach (var indexName in IndexNames)
            {
                using var lookup = sqlite.CreateCommand();
                lookup.CommandText =
                    $"SELECT id FROM target INDEXED BY {indexName} WHERE code = '{Code(target.RowId)}';";
                Convert.ToInt64(lookup.ExecuteScalar()).Should().Be(target.RowId);
            }
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
            Enumerable.Repeat(SqlValue.Text(Code(rowId)), RepeatedColumnCount)
                .Append(SqlValue.Integer(rowId))
                .ToArray(),
            textEncoding);

    private static string RepeatedBinaryIndexColumns()
        => string.Join(", ", Enumerable.Repeat("code COLLATE BINARY", RepeatedColumnCount));

    private static string InsertStatement(IEnumerable<int> rowIds)
        => $"INSERT INTO target VALUES {string.Join(", ", rowIds.Select(rowId => $"({rowId}, '{Code(rowId)}')"))};";

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

    private static long CountByCode(EmbeddedConnection connection, int rowId)
    {
        using var statement = connection.Prepare(
            $"SELECT COUNT(*) FROM target WHERE code = '{Code(rowId)}';");
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static long IdByCode(EmbeddedConnection connection, int rowId)
    {
        using var statement = connection.Prepare(
            $"SELECT id FROM target WHERE code = '{Code(rowId)}';");
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static string Code(int rowId) => $"code-{rowId:D3}-{CodePadding}";

    private static string CreateDatabasePath(string name)
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "managed-atomic-multi-index-leftmost-leaf-insertion-tests");
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

    private sealed record IndexInsertionTarget(string Name, uint RootPage, uint LeafPage);

    private sealed record InsertionTarget(
        int RowId,
        int RowCount,
        IReadOnlyList<IndexInsertionTarget> Indexes);

    private sealed record LeafTopology(uint PageNumber, IReadOnlyList<long> RowIds);

    private sealed record IndexTopology(
        string Name,
        uint RootPage,
        byte[] RootImage,
        IReadOnlyList<LeafTopology> Children);

    private sealed record Topology(
        SqliteDatabaseHeader Header,
        uint PageCount,
        uint TableRootPage,
        IReadOnlyList<IndexTopology> Indexes);
}
