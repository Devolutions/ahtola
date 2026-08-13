using System.Buffers.Binary;
using System.Text;
using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedArbitraryDepthFullCatalogRewriteTests
{
    private const int PageSize = 512;
    private const int RowCount = 1_200;
    private const int PayloadLength = 969;
    private const int EncryptedPayloadLength = 913;
    private const long FirstRowId = 9_000_000_000_000_000_000L;
    private const string Aes256Key = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    [Test]
    public void FourthLevelTableAndIndexRewriteReopensReadOnlyUsesOverflowAndPassesSqliteIntegrity()
    {
        var path = CreateDatabasePath("integrity");
        try
        {
            CreatePageSizeDatabase(path, PhysicalFileSystem.Instance);
            CreateHighPressureCatalog(path, PhysicalFileSystem.Instance);

            using (var pager = SqlitePager.Open(
                       PhysicalFileSystem.Instance,
                       path,
                       path + "-wal",
                       readOnly: true))
            {
                AssertHighDepthCatalog(pager);
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                QueryCount(connection).Should().Be(RowCount);
                QueryValue(connection, FirstRowId + RowCount - 1).Should().Be(ValueFor(RowCount));
            }

            using (var readOnly = EmbeddedDatabase.OpenFile(path, readOnly: true))
            using (var connection = readOnly.Connect())
            {
                QueryCount(connection).Should().Be(RowCount);
                QueryValue(connection, FirstRowId).Should().Be(ValueFor(1));
            }

            VerifyWithSqlite(path);
            CorruptDeepIndexChildAndAssertReopenFails(path, PhysicalFileSystem.Instance);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void FourthLevelFullRewriteWalFailureRecoversThePriorCatalog()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "fourth-level-rewrite-wal-failure.db";
        CreatePageSizeDatabase(path, fileSystem);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, "CREATE INDEX t_value_binary ON t(value);");

            faults.FailOnOccurrence(
                FileSystemOperation.Write,
                faults.GetOperationCount(FileSystemOperation.Write) + 3);
            Assert.Throws<IOException>(() => Execute(connection, BuildInsert()));
        }

        using var recovered = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var recoveredConnection = recovered.Connect();
        QueryCount(recoveredConnection).Should().Be(0);
        Query(recoveredConnection, "PRAGMA index_list(t);")
            .Select(row => row[1].AsText())
            .Should()
            .Contain("t_value_binary");
    }

    [Test]
    public void EncryptedFourthLevelTableAndIndexReopenReadOnly()
    {
        using var encryption = AhtolaEncryptionOptions.FromHex(
            AhtolaEncryptionCipher.Aes256Gcm,
            Aes256Key);
        var fileSystem = new AhtolaEncryptionFileSystem(new InMemoryFileSystem(), encryption);
        const string path = "encrypted-fourth-level-full-rewrite.db";
        CreatePageSizeDatabase(path, fileSystem);
        CreateHighPressureCatalog(path, fileSystem, EncryptedPayloadLength);

        using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
        {
            AssertHighDepthCatalog(pager);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var connection = reopened.Connect();
        QueryCount(connection).Should().Be(RowCount);
        QueryValue(connection, FirstRowId + (RowCount / 2)).Should()
            .Be(ValueFor((RowCount / 2) + 1, EncryptedPayloadLength));
    }

    private static void CreateHighPressureCatalog(
        string path,
        IFileSystem fileSystem,
        int payloadLength = PayloadLength)
    {
        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
        Execute(connection, BuildInsert(payloadLength));
        Execute(connection, "CREATE INDEX t_value_binary ON t(value);");
    }

    private static void CreatePageSizeDatabase(string path, IFileSystem fileSystem)
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
    }

    private static void AssertHighDepthCatalog(SqlitePager pager)
    {
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var tableRoot = FindRootPage(pager, header, "table", "t");
        var indexRoot = FindRootPage(pager, header, "index", "t_value_binary");
        var table = ReadTableNode(pager, header, tableRoot, new HashSet<uint>());
        var index = ReadIndexNode(
            pager,
            header,
            indexRoot,
            new SqliteOverflowChainReader(pager, header),
            new HashSet<uint>());

        table.Height.Should().BeGreaterThanOrEqualTo(4);
        table.RowCount.Should().Be(RowCount);
        index.Height.Should().BeGreaterThanOrEqualTo(4);
        index.Records.Count.Should().Be(RowCount);
        index.HasOverflowSeparator.Should().BeTrue();
    }

    private static TableNodeInfo ReadTableNode(
        SqlitePager pager,
        SqliteDatabaseHeader header,
        uint pageNumber,
        ISet<uint> seenPages)
    {
        seenPages.Add(pageNumber).Should().BeTrue();
        var page = pager.ReadCommittedPage(pageNumber);
        var pageHeader = SqliteBtreePageHeader.Parse(page);
        if (pageHeader.PageType == SqliteBtreePageType.TableLeaf)
        {
            var leaf = SqliteTableLeafPageView.Parse(page, header.UsableSpace);
            leaf.Cells.Should().NotBeEmpty();
            return new TableNodeInfo(
                Height: 1,
                RowCount: leaf.Cells.Count,
                MaximumRowId: leaf.Cells[^1].Cell.RowId);
        }

        pageHeader.PageType.Should().Be(SqliteBtreePageType.TableInterior);
        var interior = SqliteTableInteriorPageView.Parse(page, header.UsableSpace);
        interior.Cells.Should().NotBeEmpty();
        TableNodeInfo? firstChild = null;
        var rowCount = 0;
        long maximumRowId = 0;
        for (var childIndex = 0; childIndex <= interior.Cells.Count; childIndex++)
        {
            var childPage = childIndex == interior.Cells.Count
                ? interior.Header.RightMostChildPage
                : interior.Cells[childIndex].Cell.LeftChildPage;
            var child = ReadTableNode(pager, header, childPage, seenPages);
            if (firstChild is { } expected)
                child.Height.Should().Be(expected.Height);
            else
                firstChild = child;

            if (childIndex < interior.Cells.Count)
                child.MaximumRowId.Should().Be(interior.Cells[childIndex].Cell.RowId);
            rowCount += child.RowCount;
            maximumRowId = child.MaximumRowId;
        }

        return new TableNodeInfo(firstChild!.Value.Height + 1, rowCount, maximumRowId);
    }

    private static IndexNodeInfo ReadIndexNode(
        SqlitePager pager,
        SqliteDatabaseHeader header,
        uint pageNumber,
        SqliteOverflowChainReader overflowReader,
        ISet<uint> seenPages)
    {
        seenPages.Add(pageNumber).Should().BeTrue();
        var page = pager.ReadCommittedPage(pageNumber);
        var pageHeader = SqliteBtreePageHeader.Parse(page);
        if (pageHeader.PageType == SqliteBtreePageType.IndexLeaf)
        {
            var leaf = SqliteIndexLeafPageView.Parse(
                page,
                header.UsableSpace,
                header.TextEncoding,
                overflowReader: overflowReader);
            leaf.Cells.Should().NotBeEmpty();
            return new IndexNodeInfo(
                Height: 1,
                Records: Enumerable.Range(0, leaf.Cells.Count).Select(leaf.GetRecord).ToArray(),
                HasOverflowSeparator: false);
        }

        pageHeader.PageType.Should().Be(SqliteBtreePageType.IndexInterior);
        var interior = SqliteIndexInteriorPageView.Parse(
            page,
            header.UsableSpace,
            header.TextEncoding,
            overflowReader: overflowReader);
        interior.Cells.Should().NotBeEmpty();
        var comparer = new SqliteIndexRecordComparer(header.TextEncoding);
        var records = new List<byte[]>();
        byte[]? previous = null;
        IndexNodeInfo? firstChild = null;
        var hasOverflowSeparator = interior.Cells.Any(cell => cell.Cell.Key.FirstOverflowPage is not null);
        for (var childIndex = 0; childIndex <= interior.Cells.Count; childIndex++)
        {
            var childPage = childIndex == interior.Cells.Count
                ? interior.Header.RightMostChildPage
                : interior.Cells[childIndex].Cell.LeftChildPage;
            var child = ReadIndexNode(pager, header, childPage, overflowReader, seenPages);
            if (firstChild is { } expected)
                child.Height.Should().Be(expected.Height);
            else
                firstChild = child;

            hasOverflowSeparator |= child.HasOverflowSeparator;
            AppendOrdered(records, child.Records, comparer, ref previous);
            if (childIndex < interior.Cells.Count)
                AppendOrdered(records, [interior.GetRecord(childIndex)], comparer, ref previous);
        }

        return new IndexNodeInfo(firstChild!.Value.Height + 1, records, hasOverflowSeparator);
    }

    private static void AppendOrdered(
        ICollection<byte[]> destination,
        IReadOnlyList<byte[]> source,
        SqliteIndexRecordComparer comparer,
        ref byte[]? previous)
    {
        foreach (var value in source)
        {
            if (previous is not null)
                comparer.Compare(previous, value).Should().BeLessThan(0);
            destination.Add(value);
            previous = value;
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

    private static void CorruptDeepIndexChildAndAssertReopenFails(string path, IFileSystem fileSystem)
    {
        SqliteDatabaseHeader header;
        using (var store = SqlitePageStore.Open(fileSystem, path))
        {
            header = store.Header;
            var schema = SqliteTableLeafPageView.Parse(
                store.ReadPage(1),
                header.UsableSpace,
                isFirstPage: true);
            var rootPage = checked((uint)schema.Cells
                .Select(cell => SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding))
                .Single(values => values[0].AsText() == "index" && values[1].AsText() == "t_value_binary")[3]
                .AsInteger());
            var root = SqliteIndexInteriorPageView.Parse(
                store.ReadPage(rootPage),
                header.UsableSpace,
                header.TextEncoding);
            var interiorPage = root.Cells[0].Cell.LeftChildPage;
            var interior = SqliteIndexInteriorPageView.Parse(
                store.ReadPage(interiorPage),
                header.UsableSpace,
                header.TextEncoding);
            interior.Cells.Should().NotBeEmpty();

            var image = store.ReadPage(interiorPage);
            BinaryPrimitives.WriteUInt32BigEndian(
                image.AsSpan(interior.CellPointers[0], sizeof(uint)),
                rootPage);
            store.WritePage(interiorPage, image);
            store.Flush();
        }

        ReplaceWalWithEmptyFile(fileSystem, path, header);
        Assert.Throws<EmbeddedSqlException>(() => EmbeddedDatabase.OpenFile(path, fileSystem));
    }

    private static void VerifyWithSqlite(string path)
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

            using var indexedCount = sqlite.CreateCommand();
            indexedCount.CommandText = "SELECT COUNT(*) FROM t INDEXED BY t_value_binary;";
            Convert.ToInt64(indexedCount.ExecuteScalar()).Should().Be(RowCount);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(verificationPath);
        }
    }

    private static string BuildInsert(int payloadLength = PayloadLength)
    {
        var builder = new StringBuilder("INSERT INTO t VALUES ");
        for (var index = 1; index <= RowCount; index++)
        {
            if (index > 1)
                builder.Append(", ");
            builder.Append('(')
                .Append(FirstRowId + index - 1)
                .Append(", '")
                .Append(ValueFor(index, payloadLength))
                .Append("')");
        }

        return builder.Append(';').ToString();
    }

    private static string ValueFor(int index, int payloadLength = PayloadLength)
        => $"value-{index:D5}-{new string('x', payloadLength)}";

    private static long QueryCount(EmbeddedConnection connection)
        => Query(connection, "SELECT COUNT(*) FROM t;").Single()[0].AsInteger();

    private static string QueryValue(EmbeddedConnection connection, long id)
        => Query(connection, $"SELECT value FROM t WHERE id = {id};").Single()[0].AsText();

    private static List<SqlValue[]> Query(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var row = new SqlValue[statement.GetColumnCount()];
            for (var ordinal = 0; ordinal < row.Length; ordinal++)
                row[ordinal] = statement.GetValue(ordinal);
            rows.Add(row);
        }

        return rows;
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private static string CreateDatabasePath(string name)
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "managed-arbitrary-depth-full-catalog-rewrite-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{name}-{Guid.NewGuid():N}.db");
    }

    private static void ReplaceWalWithEmptyFile(
        IFileSystem fileSystem,
        string path,
        SqliteDatabaseHeader header)
    {
        fileSystem.DeleteFile(path + "-wal");
        using (SqliteWalFile.Create(
                   fileSystem,
                   path + "-wal",
                   SqliteWalHeader.Create(header.PageSize, salt1: 107, salt2: 109)))
        {
        }
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

    private readonly record struct TableNodeInfo(int Height, int RowCount, long MaximumRowId);

    private readonly record struct IndexNodeInfo(
        int Height,
        IReadOnlyList<byte[]> Records,
        bool HasOverflowSeparator);
}
