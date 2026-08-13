using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedInteriorRootChildLeafSplitMutationTests
{
    private const int PageSize = SqlitePageSize.Minimum;
    private const int PayloadLength = 80;
    private const string EncryptionKey = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    [Test]
    public void RightmostChildLeafSplitAppendsOnePageReopensAndPassesSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath("pressure-integrity");
        try
        {
            var trigger = FindChildSplitTrigger();
            CreateMinimumPageDatabase(PhysicalFileSystem.Instance, path);
            SeedThroughRow(PhysicalFileSystem.Instance, path, trigger - 1);
            var before = ReadTopology(PhysicalFileSystem.Instance, path);

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
                Execute(connection, InsertStatement(trigger));

            var after = ReadTopology(PhysicalFileSystem.Instance, path);
            AssertChildSplit(PhysicalFileSystem.Instance, path, before, after);
            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
                Count(connection).Should().Be(trigger);

            VerifyWithSqlite(path, trigger);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void EveryInterruptedChildLeafSplitFrameRecoversThePriorInteriorRoot()
    {
        var trigger = FindChildSplitTrigger();
        for (var failedFrame = 1; failedFrame <= 4; failedFrame++)
        {
            var faults = new DeterministicFaultInjector();
            var fileSystem = new InMemoryFileSystem(faults);
            var path = $"interior-child-leaf-split-wal-{failedFrame}.db";
            CreateMinimumPageDatabase(fileSystem, path);
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
                Count(connection).Should().Be(trigger - 1);

            var recoveredTopology = ReadTopology(fileSystem, path);
            recoveredTopology.Should().Be(before);
        }
    }

    [Test]
    public void EncryptedChildLeafSplitReopensReadOnly()
    {
        using var encryption = AhtolaEncryptionOptions.FromHex(
            AhtolaEncryptionCipher.Aes256Gcm,
            EncryptionKey);
        var fileSystem = new AhtolaEncryptionFileSystem(new InMemoryFileSystem(), encryption);
        const string path = "encrypted-interior-child-leaf-split.db";
        var trigger = FindChildSplitTrigger();

        CreateMinimumPageDatabase(fileSystem, path);
        SeedThroughRow(fileSystem, path, trigger);
        var topology = ReadTopology(fileSystem, path);
        topology.RootCellCount.Should().BeGreaterThan(1);

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var connection = reopened.Connect();
        Count(connection).Should().Be(trigger);
        Payload(connection, trigger).Should().Be(PayloadValue(trigger));
    }

    [Test]
    public void ChildLeafSplitCannotBypassReadOnlyPager()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "interior-child-leaf-split-read-only.db";
        var trigger = FindChildSplitTrigger();
        CreateMinimumPageDatabase(fileSystem, path);
        SeedThroughRow(fileSystem, path, trigger - 1);
        var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true))
        using (var connection = database.Connect())
        {
            Assert.Throws<EmbeddedSqlException>(() => Execute(connection, InsertStatement(trigger)));
        }

        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBefore);
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Count(reopenedConnection).Should().Be(trigger - 1);
    }

    [Test]
    public void CorruptInteriorRootIsRejectedBeforeChildSplitWrites()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "interior-child-leaf-split-corrupt.db";
        var trigger = FindChildSplitTrigger();
        CreateMinimumPageDatabase(fileSystem, path);
        SeedThroughRow(fileSystem, path, trigger - 1);
        var topology = ReadTopology(fileSystem, path);

        using (var store = SqlitePageStore.Open(fileSystem, path))
        {
            var page = store.ReadPage(topology.RootPage);
            page[0] = (byte)SqliteBtreePageType.IndexInterior;
            store.WritePage(topology.RootPage, page);
            store.Flush();
        }

        var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
        Assert.Throws<EmbeddedSqlException>(() => EmbeddedDatabase.OpenFile(path, fileSystem));
        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBefore);
    }

    [Test]
    public void IndexedInteriorRootOutgrowthFallsBackWithoutPartialChildSplit()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "interior-child-leaf-split-index-fallback.db";
        var trigger = FindChildSplitTrigger();
        CreateMinimumPageDatabase(fileSystem, path);
        SeedThroughRow(fileSystem, path, trigger - 1);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
            Execute(connection, "CREATE INDEX target_payload ON target(payload);");

        var before = ReadTopology(fileSystem, path);
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, InsertStatement(trigger));
            Count(connection).Should().Be(trigger);
            IdByPayload(connection, trigger).Should().Be(trigger);
        }

        var after = ReadTopology(fileSystem, path);
        after.RootPage.Should().Be(before.RootPage);
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        IdByPayload(reopenedConnection, trigger).Should().Be(trigger);
    }

    private static int FindChildSplitTrigger()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "find-interior-child-leaf-split-trigger.db";
        CreateMinimumPageDatabase(fileSystem, path);
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
            Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, payload TEXT);");

        for (var id = 1; id <= 128; id++)
        {
            var before = ReadTopology(fileSystem, path);
            using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = database.Connect())
                Execute(connection, InsertStatement(id));
            var after = ReadTopology(fileSystem, path);
            if (before.RootType == SqliteBtreePageType.TableInterior
                && after.RootType == SqliteBtreePageType.TableInterior
                && after.RootPage == before.RootPage
                && after.RootCellCount == before.RootCellCount + 1
                && after.PageCount == before.PageCount + 1)
            {
                return id;
            }
        }

        throw new InvalidOperationException("Unable to create a bounded interior child-leaf split.");
    }

    private static void SeedThroughRow(IFileSystem fileSystem, string path, int rowCount)
    {
        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, payload TEXT);");
        for (var id = 1; id <= rowCount; id++)
            Execute(connection, InsertStatement(id));
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
        var rootPage = FindRootPage(pager, header);
        var root = SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(rootPage));
        if (root.PageType != SqliteBtreePageType.TableInterior)
            return new Topology(pager.CommittedPageCount, rootPage, root.PageType, 0, 0);

        var interior = SqliteTableInteriorPageView.Parse(
            pager.ReadCommittedPage(rootPage),
            header.UsableSpace);
        return new Topology(
            pager.CommittedPageCount,
            rootPage,
            root.PageType,
            interior.Cells.Count,
            interior.Header.RightMostChildPage);
    }

    private static uint FindRootPage(SqlitePager pager, SqliteDatabaseHeader header)
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

    private static void AssertChildSplit(
        IFileSystem fileSystem,
        string path,
        Topology before,
        Topology after)
    {
        before.RootType.Should().Be(SqliteBtreePageType.TableInterior);
        after.RootType.Should().Be(SqliteBtreePageType.TableInterior);
        after.RootPage.Should().Be(before.RootPage);
        after.PageCount.Should().Be(before.PageCount + 1);
        after.RootCellCount.Should().Be(before.RootCellCount + 1);
        after.RightMostChildPage.Should().Be(before.PageCount + 1);

        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var parent = SqliteTableInteriorPageView.Parse(
            pager.ReadCommittedPage(after.RootPage),
            header.UsableSpace);
        var newSeparator = parent.Cells[^1].Cell;
        newSeparator.LeftChildPage.Should().Be(before.RightMostChildPage);
        var left = SqliteTableLeafPageView.Parse(
            pager.ReadCommittedPage(newSeparator.LeftChildPage),
            header.UsableSpace);
        var right = SqliteTableLeafPageView.Parse(
            pager.ReadCommittedPage(parent.Header.RightMostChildPage),
            header.UsableSpace);
        left.Cells[^1].Cell.RowId.Should().Be(newSeparator.RowId);
        right.Cells[0].Cell.RowId.Should().BeGreaterThan(newSeparator.RowId);
    }

    private static void VerifyWithSqlite(string path, int expectedRowCount)
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

            using var count = sqlite.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM target;";
            Convert.ToInt32(count.ExecuteScalar()).Should().Be(expectedRowCount);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
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

    private static string Payload(EmbeddedConnection connection, int id)
    {
        using var statement = connection.Prepare($"SELECT payload FROM target WHERE id = {id};");
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsText();
    }

    private static long IdByPayload(EmbeddedConnection connection, int id)
    {
        using var statement = connection.Prepare(
            $"SELECT id FROM target WHERE payload = '{PayloadValue(id)}';");
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static string InsertStatement(int id)
        => $"INSERT INTO target VALUES ({id}, '{PayloadValue(id)}');";

    private static string PayloadValue(int id)
        => $"payload-{id:D3}-{new string('x', PayloadLength)}";

    private static string CreateDatabasePath(string name)
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "managed-interior-root-child-leaf-split-mutation-tests");
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
        uint PageCount,
        uint RootPage,
        SqliteBtreePageType RootType,
        int RootCellCount,
        uint RightMostChildPage);
}
