using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedSecondaryIndexRootLeafPromotionInvariantTests
{
    private const int InitialRowCount = 5;
    private const string EncryptionKey = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    [Test]
    public void SecondaryIndexRootLeafPromotionPreservesRowLinksUniqueKeysAndSqliteIntegrity()
    {
        var path = CreateDatabasePath("integrity");
        try
        {
            var fileSystem = PhysicalFileSystem.Instance;
            SeedTarget(fileSystem, path);
            var (tableRootPage, indexRootPage, pageCountBefore) = ReadTargetRoots(fileSystem, path);

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, InsertStatement(InitialRowCount + 1));

                Scalar(connection, "SELECT id FROM target WHERE code = '" + Code(InitialRowCount + 1) + "';")
                    .Should()
                    .Be(InitialRowCount + 1);
                Assert.Throws<EmbeddedSqlException>(() =>
                    Execute(connection, "INSERT INTO target VALUES (99, '" + Code(InitialRowCount + 1) + "');"));
                Scalar(connection, "SELECT COUNT(*) FROM target;").Should().Be(InitialRowCount + 1);
            }

            AssertPromotedIndexRoot(
                fileSystem,
                path,
                tableRootPage,
                indexRootPage,
                pageCountBefore + 2,
                InitialRowCount + 1);
            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                Scalar(connection, "SELECT id FROM target WHERE code = '" + Code(3) + "';").Should().Be(3);
                Scalar(connection, "SELECT COUNT(*) FROM target;").Should().Be(InitialRowCount + 1);
            }

            VerifyWithSqlite(path);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void EveryInterruptedSecondaryIndexRootLeafPromotionFrameRecoversThePriorLeaf()
    {
        for (var failedFrame = 1; failedFrame <= 5; failedFrame++)
        {
            var faults = new DeterministicFaultInjector();
            var fileSystem = new InMemoryFileSystem(faults);
            var path = $"secondary-index-root-promotion-wal-{failedFrame}.db";
            SeedTarget(fileSystem, path);
            var (_, indexRootPage, pageCountBefore) = ReadTargetRoots(fileSystem, path);

            using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = database.Connect())
            {
                faults.FailOnOccurrence(
                    FileSystemOperation.Write,
                    faults.GetOperationCount(FileSystemOperation.Write) + failedFrame);
                Assert.Throws<IOException>(() => Execute(connection, InsertStatement(InitialRowCount + 1)));
            }

            using (var recovered = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = recovered.Connect())
            {
                Scalar(connection, "SELECT COUNT(*) FROM target;").Should().Be(InitialRowCount);
                Scalar(connection, "SELECT COUNT(*) FROM target WHERE code = '" + Code(InitialRowCount + 1) + "';")
                    .Should()
                    .Be(0);
            }

            var (_, recoveredIndexRootPage, pageCount) = ReadTargetRoots(fileSystem, path);
            recoveredIndexRootPage.Should().Be(indexRootPage);
            pageCount.Should().Be(pageCountBefore);
            using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
            SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(indexRootPage)).PageType
                .Should()
                .Be(SqliteBtreePageType.IndexLeaf);
        }
    }

    [Test]
    public void EncryptedSecondaryIndexRootLeafPromotionReopensReadOnly()
    {
        using var encryption = AhtolaEncryptionOptions.FromHex(
            AhtolaEncryptionCipher.Aes256Gcm,
            EncryptionKey);
        var fileSystem = new AhtolaEncryptionFileSystem(new InMemoryFileSystem(), encryption);
        const string path = "encrypted-secondary-index-root-promotion.db";
        const int encryptedInitialRowCount = 4;
        SeedTarget(fileSystem, path, encryptedInitialRowCount);
        var (tableRootPage, indexRootPage, pageCountBefore) = ReadTargetRoots(fileSystem, path);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var writableConnection = database.Connect())
            Execute(writableConnection, InsertStatement(encryptedInitialRowCount + 1));

        AssertPromotedIndexRoot(
            fileSystem,
            path,
            tableRootPage,
            indexRootPage,
            pageCountBefore + 2,
            encryptedInitialRowCount + 1);
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var connection = reopened.Connect();
        Scalar(connection, "SELECT id FROM target WHERE code = '" + Code(encryptedInitialRowCount + 1) + "';")
            .Should()
            .Be(encryptedInitialRowCount + 1);
    }

    [Test]
    public void SecondaryIndexRootLeafPromotionCannotBypassReadOnlyPager()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "secondary-index-root-promotion-read-only.db";
        SeedTarget(fileSystem, path);
        var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);

        using (var readOnly = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true))
        using (var connection = readOnly.Connect())
        {
            Assert.Throws<EmbeddedSqlException>(() => Execute(connection, InsertStatement(InitialRowCount + 1)));
        }

        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBefore);
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Scalar(reopenedConnection, "SELECT COUNT(*) FROM target;").Should().Be(InitialRowCount);
    }

    [Test]
    public void CorruptSecondaryIndexRootLeafIsRejectedBeforePromotionWrites()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "secondary-index-root-promotion-corrupt.db";
        SeedTarget(fileSystem, path);
        var (_, indexRootPage, _) = ReadTargetRoots(fileSystem, path);

        using (var store = SqlitePageStore.Open(fileSystem, path))
        {
            var page = store.ReadPage(indexRootPage);
            page[0] = (byte)SqliteBtreePageType.TableLeaf;
            store.WritePage(indexRootPage, page);
            store.Flush();
        }

        var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
        Assert.Throws<EmbeddedSqlException>(() => EmbeddedDatabase.OpenFile(path, fileSystem));
        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBefore);
    }

    [Test]
    public void MultiIndexOutgrowthFallsBackToFullCatalogRewriteBeforeRootPromotion()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "secondary-index-root-promotion-multi-index-fallback.db";
        SeedTarget(fileSystem, path);
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE INDEX target_code_copy ON target(code);");
            Execute(connection, InsertStatement(InitialRowCount + 1));
        }

        var (_, rewrittenIndexRootPage, _) = ReadTargetRoots(fileSystem, path);
        using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
        {
            SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(rewrittenIndexRootPage)).PageType
                .Should()
                .Be(SqliteBtreePageType.IndexInterior);
        }
        using (var reopened = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = reopened.Connect())
        {
            Scalar(connection, "SELECT COUNT(*) FROM target;").Should().Be(InitialRowCount + 1);
            Scalar(connection, "SELECT id FROM target WHERE code = '" + Code(InitialRowCount + 1) + "';")
                .Should()
                .Be(InitialRowCount + 1);
        }
    }

    private static void SeedTarget(
        IFileSystem fileSystem,
        string path,
        int initialRowCount = InitialRowCount)
    {
        CreateMinimumPageDatabase(fileSystem, path);
        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, code TEXT);");
        Execute(connection, string.Join(
            " ",
            "INSERT INTO target VALUES",
            string.Join(", ", Enumerable.Range(1, initialRowCount).Select(InsertValues)) + ";"));
        Execute(connection, "CREATE UNIQUE INDEX target_code_twice ON target(code, code);");
    }

    private static void CreateMinimumPageDatabase(IFileSystem fileSystem, string path)
    {
        var header = SqliteDatabaseHeader.CreateDefault() with { PageSize = SqlitePageSize.Minimum };
        using var pager = SqlitePager.Create(
            fileSystem,
            path,
            path + "-wal",
            SqliteWalHeader.Create(SqlitePageSize.Minimum, salt1: 0x1020_3040, salt2: 0x5060_7080),
            header);
    }

    private static (uint TableRootPage, uint IndexRootPage, uint PageCount) ReadTargetRoots(
        IFileSystem fileSystem,
        string path)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        return (
            FindRootPage(pager, header, "table", "target"),
            FindRootPage(pager, header, "index", "target_code_twice"),
            pager.CommittedPageCount);
    }

    private static void AssertPromotedIndexRoot(
        IFileSystem fileSystem,
        string path,
        uint expectedTableRootPage,
        uint expectedIndexRootPage,
        uint expectedPageCount,
        int expectedRecordCount)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        pager.CommittedPageCount.Should().Be(expectedPageCount);
        header.DatabaseSizeInPages.Should().Be(expectedPageCount);
        FindRootPage(pager, header, "table", "target").Should().Be(expectedTableRootPage);
        FindRootPage(pager, header, "index", "target_code_twice").Should().Be(expectedIndexRootPage);
        SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(expectedTableRootPage)).PageType
            .Should()
            .Be(SqliteBtreePageType.TableLeaf);

        var root = SqliteIndexInteriorPageView.Parse(
            pager.ReadCommittedPage(expectedIndexRootPage),
            header.UsableSpace,
            header.TextEncoding);
        root.Cells.Should().ContainSingle();
        var left = SqliteIndexLeafPageView.Parse(
            pager.ReadCommittedPage(root.Cells[0].Cell.LeftChildPage),
            header.UsableSpace,
            header.TextEncoding);
        var right = SqliteIndexLeafPageView.Parse(
            pager.ReadCommittedPage(root.Header.RightMostChildPage),
            header.UsableSpace,
            header.TextEncoding);
        left.Cells.Should().NotBeEmpty();
        right.Cells.Should().NotBeEmpty();

        var comparer = new SqliteIndexRecordComparer(header.TextEncoding);
        comparer.Compare(left.GetRecord(left.Cells.Count - 1), root.GetRecord(0)).Should().BeLessThan(0);
        comparer.Compare(root.GetRecord(0), right.GetRecord(0)).Should().BeLessThan(0);
        var records = Enumerable.Range(0, left.Cells.Count)
            .Select(left.GetRecord)
            .Append(root.GetRecord(0))
            .Concat(Enumerable.Range(0, right.Cells.Count).Select(right.GetRecord))
            .Select(record => SqliteRecordCodec.Decode(record, header.TextEncoding))
            .ToArray();
        records.Should().HaveCount(expectedRecordCount);
        records.Select(record => record[2].AsInteger())
            .Should()
            .Equal(Enumerable.Range(1, expectedRecordCount).Select(id => (long)id));
        foreach (var record in records)
        {
            record[0].AsText().Should().Be(record[1].AsText());
            record[0].AsText().Should().Be(Code(checked((int)record[2].AsInteger())));
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

            using var indexedLookup = sqlite.CreateCommand();
            indexedLookup.CommandText =
                "SELECT id FROM target INDEXED BY target_code_twice WHERE code = '" + Code(4) + "';";
            Convert.ToInt64(indexedLookup.ExecuteScalar()).Should().Be(4);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(verificationPath);
        }
    }

    private static long Scalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private static string InsertStatement(int id) => "INSERT INTO target VALUES " + InsertValues(id) + ";";

    private static string InsertValues(int id) => $"({id}, '{Code(id)}')";

    private static string Code(int id) => $"code-{id:D3}-{new string('x', 35)}";

    private static string CreateDatabasePath(string name)
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "managed-secondary-index-root-leaf-promotion-invariant-tests");
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
