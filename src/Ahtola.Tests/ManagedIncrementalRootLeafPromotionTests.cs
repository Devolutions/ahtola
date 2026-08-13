using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedIncrementalRootLeafPromotionTests
{
    private const int InitialRowCount = 20;
    private const int PayloadLength = 180;
    private const string EncryptionKey = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    [Test]
    public void RootLeafPromotionPreservesCatalogRootReopensAndPassesSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath("integrity");
        try
        {
            uint rootPage;
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                SeedLeafRoot(connection);
                rootPage = FindRootPage(PhysicalFileSystem.Instance, path, "table", "target");
                RootPageType(PhysicalFileSystem.Instance, path, rootPage)
                    .Should()
                    .Be(SqliteBtreePageType.TableLeaf);

                Execute(connection, InsertStatement(InitialRowCount + 1));
            }

            AssertPromotedRoot(
                PhysicalFileSystem.Instance,
                path,
                rootPage,
                expectedRowCount: InitialRowCount + 1);

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                QueryCount(connection).Should().Be(InitialRowCount + 1);
                QueryPayload(connection, InitialRowCount + 1).Should().HaveLength(PayloadLength);
            }

            var verificationPath = path + ".verify.db";
            File.Copy(path, verificationPath, overwrite: true);
            try
            {
                using var sqlite = new MsData.SqliteConnection($"Data Source={verificationPath}");
                sqlite.Open();
                using var integrity = sqlite.CreateCommand();
                integrity.CommandText = "PRAGMA integrity_check;";
                integrity.ExecuteScalar().Should().Be("ok");
            }
            finally
            {
                MsData.SqliteConnection.ClearAllPools();
                DeleteDatabase(verificationPath);
            }
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void EveryInterruptedRootLeafPromotionFrameRecoversThePriorLeaf()
    {
        for (var failedFrame = 1; failedFrame <= 4; failedFrame++)
        {
            var faults = new DeterministicFaultInjector();
            var fileSystem = new InMemoryFileSystem(faults);
            var path = $"root-leaf-promotion-wal-{failedFrame}.db";
            using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = database.Connect())
            {
                SeedLeafRoot(connection);
                faults.FailOnOccurrence(
                    FileSystemOperation.Write,
                    faults.GetOperationCount(FileSystemOperation.Write) + failedFrame);
                Assert.Throws<IOException>(() => Execute(connection, InsertStatement(InitialRowCount + 1)));
            }

            using (var recovered = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = recovered.Connect())
            {
                QueryCount(connection).Should().Be(InitialRowCount);
                QueryPayload(connection, InitialRowCount).Should().HaveLength(PayloadLength);
            }

            var rootPage = FindRootPage(fileSystem, path, "table", "target");
            RootPageType(fileSystem, path, rootPage).Should().Be(SqliteBtreePageType.TableLeaf);
        }
    }

    [Test]
    public void EncryptedRootLeafPromotionReopensReadOnly()
    {
        using var encryption = AhtolaEncryptionOptions.FromHex(
            AhtolaEncryptionCipher.Aes256Gcm,
            EncryptionKey);
        var fileSystem = new AhtolaEncryptionFileSystem(new InMemoryFileSystem(), encryption);
        const string path = "encrypted-root-leaf-promotion.db";

        uint rootPage;
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            SeedLeafRoot(connection);
            rootPage = FindRootPage(fileSystem, path, "table", "target");
            Execute(connection, InsertStatement(InitialRowCount + 1));
        }

        AssertPromotedRoot(fileSystem, path, rootPage, InitialRowCount + 1);
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var readOnlyConnection = reopened.Connect();
        QueryCount(readOnlyConnection).Should().Be(InitialRowCount + 1);
        QueryPayload(readOnlyConnection, InitialRowCount + 1).Should().HaveLength(PayloadLength);
    }

    [Test]
    public void RootLeafPromotionCannotBypassReadOnlyPager()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "root-leaf-promotion-read-only.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
            SeedLeafRoot(connection);

        var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
        using (var readOnly = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true))
        using (var connection = readOnly.Connect())
        {
            Assert.Throws<EmbeddedSqlException>(() =>
                Execute(connection, InsertStatement(InitialRowCount + 1)));
        }

        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBefore);
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        QueryCount(reopenedConnection).Should().Be(InitialRowCount);
    }

    [Test]
    public void CorruptRootLeafIsRejectedBeforePromotionWrites()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "root-leaf-promotion-corrupt.db";
        uint rootPage;
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            SeedLeafRoot(connection);
            rootPage = FindRootPage(fileSystem, path, "table", "target");
        }

        using (var store = SqlitePageStore.Open(fileSystem, path))
        {
            var page = store.ReadPage(rootPage);
            page[0] = (byte)SqliteBtreePageType.IndexLeaf;
            store.WritePage(rootPage, page);
            store.Flush();
        }

        var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
        Assert.Throws<EmbeddedSqlException>(() => EmbeddedDatabase.OpenFile(path, fileSystem));
        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBefore);
    }

    [Test]
    public void IndexedRootLeafOutgrowthFallsBackWithoutBreakingUniqueOrdering()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "indexed-root-leaf-promotion-fallback.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            SeedLeafRoot(connection);
            Execute(connection, "CREATE UNIQUE INDEX target_code ON target(code);");
            Execute(connection, InsertStatement(InitialRowCount + 1));

            QueryIdByCode(connection, InitialRowCount + 1).Should().Be(InitialRowCount + 1);
            Assert.Throws<EmbeddedSqlException>(() =>
                Execute(connection, $"INSERT INTO target VALUES (99, 'code-{InitialRowCount + 1:D3}', 'duplicate');"));
        }

        var rootPage = FindRootPage(fileSystem, path, "table", "target");
        RootPageType(fileSystem, path, rootPage).Should().Be(SqliteBtreePageType.TableInterior);
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        QueryCount(reopenedConnection).Should().Be(InitialRowCount + 1);
        QueryIdByCode(reopenedConnection, InitialRowCount + 1).Should().Be(InitialRowCount + 1);
    }

    private static void SeedLeafRoot(EmbeddedConnection connection)
    {
        Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, code TEXT, payload TEXT);");
        var rows = Enumerable.Range(1, InitialRowCount).Select(InsertValues);
        Execute(connection, $"INSERT INTO target VALUES {string.Join(", ", rows)};");
    }

    private static string InsertStatement(int id) => $"INSERT INTO target VALUES {InsertValues(id)};";

    private static string InsertValues(int id)
        => $"({id}, 'code-{id:D3}', '{new string('x', PayloadLength)}')";

    private static void AssertPromotedRoot(
        IFileSystem fileSystem,
        string path,
        uint expectedRootPage,
        int expectedRowCount)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        header.DatabaseSizeInPages.Should().Be(expectedRootPage + 2);
        FindRootPage(pager, header, "table", "target").Should().Be(expectedRootPage);
        var root = SqliteTableInteriorPageView.Parse(
            pager.ReadCommittedPage(expectedRootPage),
            header.UsableSpace);
        root.Cells.Should().ContainSingle();

        var left = SqliteTableLeafPageView.Parse(
            pager.ReadCommittedPage(root.Cells[0].Cell.LeftChildPage),
            header.UsableSpace);
        var right = SqliteTableLeafPageView.Parse(
            pager.ReadCommittedPage(root.Header.RightMostChildPage),
            header.UsableSpace);
        left.Cells.Should().NotBeEmpty();
        right.Cells.Should().NotBeEmpty();
        left.Cells[^1].Cell.RowId.Should().Be(root.Cells[0].Cell.RowId);
        left.Cells.Select(cell => cell.Cell.RowId)
            .Concat(right.Cells.Select(cell => cell.Cell.RowId))
            .Should()
            .Equal(Enumerable.Range(1, expectedRowCount).Select(id => (long)id));
    }

    private static uint FindRootPage(IFileSystem fileSystem, string path, string type, string name)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        return FindRootPage(
            pager,
            SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1)),
            type,
            name);
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

    private static SqliteBtreePageType RootPageType(IFileSystem fileSystem, string path, uint rootPage)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        return SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(rootPage)).PageType;
    }

    private static long QueryCount(EmbeddedConnection connection)
    {
        using var statement = connection.Prepare("SELECT COUNT(*) FROM target;");
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static string QueryPayload(EmbeddedConnection connection, int id)
    {
        using var statement = connection.Prepare($"SELECT payload FROM target WHERE id = {id};");
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsText();
    }

    private static long QueryIdByCode(EmbeddedConnection connection, int id)
    {
        using var statement = connection.Prepare($"SELECT id FROM target WHERE code = 'code-{id:D3}';");
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
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
            "managed-incremental-root-leaf-promotion-tests");
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
}
