using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedSecondaryIndexInteriorRootChildLeafSplitTests
{
    private const int PageSize = SqlitePageSize.Minimum;
    private const int InitialRowCount = 5;
    private const int CodePaddingLength = 12;
    private const string IndexName = "target_code_twice";
    private const string EncryptionKey = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    [Test]
    public void RightmostSecondaryIndexChildLeafSplitAppendsOnePageAndPassesSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath("pressure-integrity");
        try
        {
            var trigger = FindChildSplitTrigger();
            SeedThroughRow(PhysicalFileSystem.Instance, path, trigger - 1);
            var before = ReadTopology(PhysicalFileSystem.Instance, path);
            var sourceIndexRootPage = ReadPage(
                PhysicalFileSystem.Instance,
                path,
                before.IndexRootPage);

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
                Execute(connection, InsertStatement(trigger));

            var after = ReadTopology(PhysicalFileSystem.Instance, path);
            AssertChildSplit(
                PhysicalFileSystem.Instance,
                path,
                before,
                sourceIndexRootPage,
                after,
                trigger);
            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                Count(connection).Should().Be(trigger);
                IdByCode(connection, trigger).Should().Be(trigger);
            }

            VerifyWithSqlite(path, trigger);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void EveryInterruptedSecondaryIndexChildLeafSplitFrameRecoversThePriorCatalog()
    {
        var trigger = FindChildSplitTrigger();
        for (var failedFrame = 1; failedFrame <= 5; failedFrame++)
        {
            var faults = new DeterministicFaultInjector();
            var fileSystem = new InMemoryFileSystem(faults);
            var path = $"secondary-index-child-leaf-split-wal-{failedFrame}.db";
            SeedThroughRow(fileSystem, path, trigger - 1);
            var before = ReadTopology(fileSystem, path);

            using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = database.Connect())
            {
                faults.FailOnOccurrence(
                    FileSystemOperation.Write,
                    faults.GetOperationCount(FileSystemOperation.Write) + failedFrame);
                Assert.Throws<IOException>(() => Execute(connection, InsertStatement(trigger)));
            }

            using (var recovered = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = recovered.Connect())
            {
                Count(connection).Should().Be(trigger - 1);
                IdByCode(connection, trigger - 1).Should().Be(trigger - 1);
                CountByCode(connection, trigger).Should().Be(0);
            }

            ReadTopology(fileSystem, path).Should().Be(before);
        }
    }

    [Test]
    public void EncryptedSecondaryIndexChildLeafSplitReopensReadOnly()
    {
        using var encryption = AhtolaEncryptionOptions.FromHex(
            Ahtola.Core.Storage.AhtolaEncryptionCipher.Aes256Gcm,
            EncryptionKey);
        var fileSystem = new AhtolaEncryptionFileSystem(new InMemoryFileSystem(), encryption);
        const string path = "encrypted-secondary-index-child-leaf-split.db";
        var trigger = FindChildSplitTrigger();
        SeedThroughRow(fileSystem, path, trigger - 1);
        var before = ReadTopology(fileSystem, path);
        var sourceIndexRootPage = ReadPage(fileSystem, path, before.IndexRootPage);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
            Execute(connection, InsertStatement(trigger));

        var after = ReadTopology(fileSystem, path);
        AssertChildSplit(fileSystem, path, before, sourceIndexRootPage, after, trigger);
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var readOnlyConnection = reopened.Connect();
        Count(readOnlyConnection).Should().Be(trigger);
        IdByCode(readOnlyConnection, trigger).Should().Be(trigger);
    }

    [Test]
    public void SecondaryIndexChildLeafSplitCannotBypassReadOnlyPager()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "secondary-index-child-leaf-split-read-only.db";
        var trigger = FindChildSplitTrigger();
        SeedThroughRow(fileSystem, path, trigger - 1);
        var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true))
        using (var connection = database.Connect())
            Assert.Throws<EmbeddedSqlException>(() => Execute(connection, InsertStatement(trigger)));

        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBefore);
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Count(reopenedConnection).Should().Be(trigger - 1);
    }

    [Test]
    public void CorruptSecondaryIndexInteriorRootIsRejectedBeforeChildSplitWrites()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "secondary-index-child-leaf-split-corrupt.db";
        var trigger = FindChildSplitTrigger();
        SeedThroughRow(fileSystem, path, trigger - 1);
        var topology = ReadTopology(fileSystem, path);

        using (var store = SqlitePageStore.Open(fileSystem, path))
        {
            var page = store.ReadPage(topology.IndexRootPage);
            page[0] = (byte)SqliteBtreePageType.TableInterior;
            store.WritePage(topology.IndexRootPage, page);
            store.Flush();
        }

        var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
        Assert.Throws<EmbeddedSqlException>(() => EmbeddedDatabase.OpenFile(path, fileSystem));
        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBefore);
    }

    private static int FindChildSplitTrigger()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "find-secondary-index-child-leaf-split-trigger.db";
        SeedInitialRows(fileSystem, path);
        for (var id = InitialRowCount + 1; id <= 64; id++)
        {
            var before = ReadTopology(fileSystem, path);
            using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = database.Connect())
                Execute(connection, InsertStatement(id));
            var after = ReadTopology(fileSystem, path);
            if (before.TableRootType == SqliteBtreePageType.TableLeaf
                && after.TableRootType == SqliteBtreePageType.TableLeaf
                && before.IndexRootType == SqliteBtreePageType.IndexInterior
                && after.IndexRootType == SqliteBtreePageType.IndexInterior
                && after.TableRootPage == before.TableRootPage
                && after.IndexRootPage == before.IndexRootPage
                && after.PageCount == before.PageCount + 1
                && after.IndexRootCellCount == before.IndexRootCellCount + 1
                && after.IndexRightMostChildPage == after.PageCount)
            {
                return id;
            }
        }

        throw new InvalidOperationException("Unable to create a bounded secondary-index child-leaf split.");
    }

    private static void SeedThroughRow(IFileSystem fileSystem, string path, int rowCount)
    {
        SeedInitialRows(fileSystem, path);
        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var connection = database.Connect();
        for (var id = InitialRowCount + 1; id <= rowCount; id++)
            Execute(connection, InsertStatement(id));
    }

    private static void SeedInitialRows(IFileSystem fileSystem, string path)
    {
        CreateMinimumPageDatabase(fileSystem, path);
        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, code TEXT);");
        Execute(connection, string.Join(
            " ",
            "INSERT INTO target VALUES",
            string.Join(", ", Enumerable.Range(1, InitialRowCount).Select(InsertValues)) + ";"));
        Execute(connection, $"CREATE UNIQUE INDEX {IndexName} ON target(code, code, code, code);");
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

    private static Topology ReadTopology(IFileSystem fileSystem, string path)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var tableRootPage = FindRootPage(pager, header, "table", "target");
        var indexRootPage = FindRootPage(pager, header, "index", IndexName);
        var tableRootType = SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(tableRootPage)).PageType;
        var indexRoot = SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(indexRootPage));
        if (indexRoot.PageType != SqliteBtreePageType.IndexInterior)
        {
            return new Topology(
                header,
                pager.CommittedPageCount,
                tableRootPage,
                tableRootType,
                indexRootPage,
                indexRoot.PageType,
                0,
                0);
        }

        var parent = SqliteIndexInteriorPageView.Parse(
            pager.ReadCommittedPage(indexRootPage),
            header.UsableSpace,
            header.TextEncoding);
        return new Topology(
            header,
            pager.CommittedPageCount,
            tableRootPage,
            tableRootType,
            indexRootPage,
            indexRoot.PageType,
            parent.Cells.Count,
            parent.Header.RightMostChildPage);
    }

    private static void AssertChildSplit(
        IFileSystem fileSystem,
        string path,
        Topology before,
        ReadOnlySpan<byte> sourceIndexRootPage,
        Topology after,
        int expectedRowCount)
    {
        before.TableRootType.Should().Be(SqliteBtreePageType.TableLeaf);
        after.TableRootType.Should().Be(SqliteBtreePageType.TableLeaf);
        after.TableRootPage.Should().Be(before.TableRootPage);
        before.IndexRootType.Should().Be(SqliteBtreePageType.IndexInterior);
        after.IndexRootType.Should().Be(SqliteBtreePageType.IndexInterior);
        after.IndexRootPage.Should().Be(before.IndexRootPage);
        after.PageCount.Should().Be(before.PageCount + 1);
        after.Header.DatabaseSizeInPages.Should().Be(after.PageCount);
        after.Header.ChangeCounter.Should().Be(before.Header.ChangeCounter + 1);
        after.Header.VersionValidFor.Should().Be(after.Header.ChangeCounter);
        after.Header.SchemaCookie.Should().Be(before.Header.SchemaCookie);
        after.Header.FreelistPageCount.Should().Be(0);
        after.Header.FirstFreelistTrunkPage.Should().Be(0);
        after.IndexRootCellCount.Should().Be(before.IndexRootCellCount + 1);
        after.IndexRightMostChildPage.Should().Be(after.PageCount);

        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var parentBefore = SqliteIndexInteriorPageView.Parse(
            sourceIndexRootPage,
            before.Header.UsableSpace,
            before.Header.TextEncoding);
        var parentAfter = SqliteIndexInteriorPageView.Parse(
            pager.ReadCommittedPage(after.IndexRootPage),
            after.Header.UsableSpace,
            after.Header.TextEncoding);
        for (var cellIndex = 0; cellIndex < parentBefore.Cells.Count; cellIndex++)
        {
            parentAfter.Cells[cellIndex].Cell.LeftChildPage
                .Should()
                .Be(parentBefore.Cells[cellIndex].Cell.LeftChildPage);
            parentAfter.GetRecord(cellIndex).Should().Equal(parentBefore.GetRecord(cellIndex));
        }

        var newSeparator = parentAfter.Cells[^1].Cell;
        newSeparator.LeftChildPage.Should().Be(before.IndexRightMostChildPage);
        var left = SqliteIndexLeafPageView.Parse(
            pager.ReadCommittedPage(newSeparator.LeftChildPage),
            after.Header.UsableSpace,
            after.Header.TextEncoding);
        var right = SqliteIndexLeafPageView.Parse(
            pager.ReadCommittedPage(parentAfter.Header.RightMostChildPage),
            after.Header.UsableSpace,
            after.Header.TextEncoding);
        var comparer = new SqliteIndexRecordComparer(after.Header.TextEncoding);
        comparer.Compare(left.GetRecord(left.Cells.Count - 1), parentAfter.GetRecord(parentAfter.Cells.Count - 1))
            .Should()
            .BeLessThan(0);
        comparer.Compare(parentAfter.GetRecord(parentAfter.Cells.Count - 1), right.GetRecord(0))
            .Should()
            .BeLessThan(0);

        ReadIndexRecords(pager, after.Header, parentAfter)
            .Select(record => SqliteRecordCodec.Decode(record, after.Header.TextEncoding))
            .Select(record => record[^1].AsInteger())
            .Should()
            .Equal(Enumerable.Range(1, expectedRowCount).Select(id => (long)id));
    }

    private static IReadOnlyList<byte[]> ReadIndexRecords(
        SqlitePager pager,
        SqliteDatabaseHeader header,
        SqliteIndexInteriorPageView parent)
    {
        var records = new List<byte[]>();
        for (var childIndex = 0; childIndex <= parent.Cells.Count; childIndex++)
        {
            var childPage = childIndex == parent.Cells.Count
                ? parent.Header.RightMostChildPage
                : parent.Cells[childIndex].Cell.LeftChildPage;
            var leaf = SqliteIndexLeafPageView.Parse(
                pager.ReadCommittedPage(childPage),
                header.UsableSpace,
                header.TextEncoding);
            for (var recordIndex = 0; recordIndex < leaf.Cells.Count; recordIndex++)
                records.Add(leaf.GetRecord(recordIndex));
            if (childIndex < parent.Cells.Count)
                records.Add(parent.GetRecord(childIndex));
        }

        return records;
    }

    private static byte[] ReadPage(IFileSystem fileSystem, string path, uint pageNumber)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        return pager.ReadCommittedPage(pageNumber);
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

    private static void VerifyWithSqlite(string path, int expectedRowCount)
    {
        var verificationPath = path + ".verify.db";
        File.Copy(path, verificationPath, overwrite: true);
        try
        {
            // Avoid the global SQLite pool because parallel storage tests clear it while
            // their own temporary databases are being removed.
            using var sqlite = new MsData.SqliteConnection($"Data Source={verificationPath};Pooling=False");
            sqlite.Open();
            using (var integrity = sqlite.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check;";
                integrity.ExecuteScalar().Should().Be("ok");
            }

            using var lookup = sqlite.CreateCommand();
            lookup.CommandText =
                $"SELECT id FROM target INDEXED BY {IndexName} WHERE code = '{Code(expectedRowCount)}';";
            Convert.ToInt64(lookup.ExecuteScalar()).Should().Be(expectedRowCount);
        }
        finally
        {
            DeleteDatabase(verificationPath);
        }
    }

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

    private static long CountByCode(EmbeddedConnection connection, int id)
    {
        using var statement = connection.Prepare(
            $"SELECT COUNT(*) FROM target WHERE code = '{Code(id)}';");
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static long IdByCode(EmbeddedConnection connection, int id)
    {
        using var statement = connection.Prepare(
            $"SELECT id FROM target WHERE code = '{Code(id)}';");
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static string InsertStatement(int id) => "INSERT INTO target VALUES " + InsertValues(id) + ";";

    private static string InsertValues(int id) => $"({id}, '{Code(id)}')";

    private static string Code(int id) => $"code-{id:D3}-{new string('x', CodePaddingLength)}";

    private static string CreateDatabasePath(string name)
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "managed-secondary-index-interior-root-child-leaf-split-tests");
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

    private sealed record Topology(
        SqliteDatabaseHeader Header,
        uint PageCount,
        uint TableRootPage,
        SqliteBtreePageType TableRootType,
        uint IndexRootPage,
        SqliteBtreePageType IndexRootType,
        int IndexRootCellCount,
        uint IndexRightMostChildPage);
}
