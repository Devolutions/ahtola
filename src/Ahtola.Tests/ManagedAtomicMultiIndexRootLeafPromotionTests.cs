using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedAtomicMultiIndexRootLeafPromotionTests
{
    private const int InitialRowCount = 5;
    private const string EncryptionKey = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    private const int PromotedIndexCount = 2;
    private static readonly string[] IndexNames = ["target_code_twice_a", "target_code_twice_b", "target_code"];

    [Test]
    public void TwoUniqueIndexRootsPromoteTogetherAndPassSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath("integrity");
        try
        {
            var fileSystem = PhysicalFileSystem.Instance;
            SeedTarget(fileSystem, path);
            var before = ReadRoots(fileSystem, path);
            AssertLeafRoots(fileSystem, path, before);

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, InsertStatement(InitialRowCount + 1));
                Assert.Throws<EmbeddedSqlException>(() =>
                    Execute(connection, $"INSERT INTO target VALUES (99, '{Code(InitialRowCount + 1)}');"));
                Count(connection).Should().Be(InitialRowCount + 1);
            }

            AssertPromotedRoots(fileSystem, path, before, InitialRowCount + 1);
            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                IdByIndex(connection, IndexNames[0], InitialRowCount + 1).Should().Be(InitialRowCount + 1);
                IdByIndex(connection, IndexNames[1], 3).Should().Be(3);
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
    public void EveryInterruptedTwoIndexPromotionFrameRecoversThePriorCatalog()
    {
        const int promotionFrameCount = 9;
        for (var failedFrame = 1; failedFrame <= promotionFrameCount; failedFrame++)
        {
            var faults = new DeterministicFaultInjector();
            var fileSystem = new InMemoryFileSystem(faults);
            var path = $"atomic-multi-index-root-promotion-wal-{failedFrame}.db";
            SeedTarget(fileSystem, path);
            var before = ReadRoots(fileSystem, path);

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
                Count(connection).Should().Be(InitialRowCount);
                CountByCode(connection, InitialRowCount + 1).Should().Be(0);
                IdByIndex(connection, IndexNames[0], InitialRowCount).Should().Be(InitialRowCount);
                IdByIndex(connection, IndexNames[1], 1).Should().Be(1);
            }

            var recoveredRoots = ReadRoots(fileSystem, path);
            recoveredRoots.Header.DatabaseSizeInPages.Should().Be(before.Header.DatabaseSizeInPages);
            recoveredRoots.TableRoot.Should().Be(before.TableRoot);
            recoveredRoots.IndexRoots.Should().Equal(before.IndexRoots);
            AssertLeafRoots(fileSystem, path, recoveredRoots);
        }
    }

    [Test]
    public void EncryptedTwoIndexPromotionReopensReadOnly()
    {
        using var encryption = AhtolaEncryptionOptions.FromHex(
            AhtolaEncryptionCipher.Aes256Gcm,
            EncryptionKey);
        var fileSystem = new AhtolaEncryptionFileSystem(new InMemoryFileSystem(), encryption);
        const string path = "encrypted-atomic-multi-index-root-promotion.db";
        SeedTarget(fileSystem, path);
        var before = ReadRoots(fileSystem, path);
        AssertLeafRoots(fileSystem, path, before);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
            Execute(connection, InsertStatement(InitialRowCount + 1));

        AssertPromotedRoots(fileSystem, path, before, InitialRowCount + 1);
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var readOnlyConnection = reopened.Connect();
        IdByIndex(readOnlyConnection, IndexNames[0], InitialRowCount + 1).Should().Be(InitialRowCount + 1);
        IdByIndex(readOnlyConnection, IndexNames[1], 2).Should().Be(2);
    }

    [Test]
    public void TwoIndexPromotionCannotBypassReadOnlyPager()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "atomic-multi-index-root-promotion-read-only.db";
        SeedTarget(fileSystem, path);
        var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true))
        using (var connection = database.Connect())
        {
            Assert.Throws<EmbeddedSqlException>(() => Execute(connection, InsertStatement(InitialRowCount + 1)));
        }

        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBefore);
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Count(reopenedConnection).Should().Be(InitialRowCount);
    }

    [Test]
    public void CorruptSecondIndexRootIsRejectedBeforeMultiIndexPromotionWrites()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "atomic-multi-index-root-promotion-corruption.db";
        SeedTarget(fileSystem, path);
        var roots = ReadRoots(fileSystem, path);

        using (var store = SqlitePageStore.Open(fileSystem, path))
        {
            var page = store.ReadPage(roots.IndexRoots[1]);
            page[0] = (byte)SqliteBtreePageType.TableLeaf;
            store.WritePage(roots.IndexRoots[1], page);
            store.Flush();
        }

        var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
        Assert.Throws<EmbeddedSqlException>(() => EmbeddedDatabase.OpenFile(path, fileSystem));
        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBefore);
    }

    [Test]
    public void InteriorThirdIndexFallsBackInsteadOfPartiallyPromotingRoots()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "atomic-multi-index-root-promotion-fallback.db";
        SeedTarget(fileSystem, path);
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, InsertStatement(InitialRowCount + 1));
            Execute(connection, "CREATE UNIQUE INDEX target_code_twice_c ON target(code, code);");
        }

        var before = ReadRoots(fileSystem, path, [.. IndexNames, "target_code_twice_c"]);
        using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
        {
            SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(before.IndexRoots[^1])).PageType
                .Should()
                .Be(SqliteBtreePageType.IndexInterior);
        }
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, $"UPDATE target SET code = '{ChangedCode(1)}' WHERE id = 1;");
            Count(connection).Should().Be(InitialRowCount + 1);
            IdByIndex(connection, "target_code_twice_c", ChangedCode(1)).Should().Be(1);
        }

        var after = ReadRoots(fileSystem, path, [.. IndexNames, "target_code_twice_c"]);
        after.Header.ChangeCounter.Should().Be(before.Header.ChangeCounter + 1);
        after.IndexRoots.Should().HaveCount(before.IndexRoots.Count);
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        IdByIndex(reopenedConnection, IndexNames[0], InitialRowCount + 1).Should().Be(InitialRowCount + 1);
        IdByIndex(reopenedConnection, "target_code_twice_c", ChangedCode(1)).Should().Be(1);
    }

    private static void SeedTarget(IFileSystem fileSystem, string path)
    {
        CreateMinimumPageDatabase(fileSystem, path);
        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, code TEXT);");
        Execute(connection, string.Join(
            " ",
            "INSERT INTO target VALUES",
            string.Join(", ", Enumerable.Range(1, InitialRowCount).Select(InsertValues)) + ";"));
        Execute(connection, "CREATE UNIQUE INDEX target_code_twice_a ON target(code, code);");
        Execute(connection, "CREATE UNIQUE INDEX target_code_twice_b ON target(code, code);");
        Execute(connection, "CREATE UNIQUE INDEX target_code ON target(code);");
        Execute(connection, "CREATE VIEW target_schema_padding AS SELECT id, code FROM target;");
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

    private static RootSnapshot ReadRoots(
        IFileSystem fileSystem,
        string path,
        IReadOnlyList<string>? indexNames = null)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var names = indexNames ?? IndexNames;
        return new RootSnapshot(
            header,
            FindRootPage(pager, header, "table", "target"),
            names.Select(name => FindRootPage(pager, header, "index", name)).ToArray());
    }

    private static void AssertLeafRoots(IFileSystem fileSystem, string path, RootSnapshot roots)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        roots.IndexRoots.Append(roots.TableRoot).Should().OnlyHaveUniqueItems();
        SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(1), isFirstPage: true).PageType
            .Should()
            .Be(SqliteBtreePageType.TableInterior);
        SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(roots.TableRoot)).PageType
            .Should()
            .Be(SqliteBtreePageType.TableLeaf);
        foreach (var indexRoot in roots.IndexRoots)
        {
            SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(indexRoot)).PageType
                .Should()
                .Be(SqliteBtreePageType.IndexLeaf);
        }
    }

    private static void AssertPromotedRoots(
        IFileSystem fileSystem,
        string path,
        RootSnapshot before,
        int expectedRecordCount)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        header.ChangeCounter.Should().Be(before.Header.ChangeCounter + 1);
        header.VersionValidFor.Should().Be(header.ChangeCounter);
        header.DatabaseSizeInPages.Should().Be(before.Header.DatabaseSizeInPages + 4);
        FindRootPage(pager, header, "table", "target").Should().Be(before.TableRoot);
        SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(before.TableRoot)).PageType
            .Should()
            .Be(SqliteBtreePageType.TableLeaf);

        var appendedPages = new HashSet<uint>();
        for (var index = 0; index < before.IndexRoots.Count; index++)
        {
            var indexRoot = before.IndexRoots[index];
            FindRootPage(pager, header, "index", IndexNames[index])
                .Should()
                .Be(indexRoot);
            if (index >= PromotedIndexCount)
            {
                var leaf = SqliteIndexLeafPageView.Parse(
                    pager.ReadCommittedPage(indexRoot),
                    header.UsableSpace,
                    header.TextEncoding);
                leaf.Cells.Should().HaveCount(expectedRecordCount);
                Enumerable.Range(0, leaf.Cells.Count)
                    .Select(recordIndex => SqliteRecordCodec.Decode(
                        leaf.GetRecord(recordIndex),
                        header.TextEncoding)[1]
                    .AsInteger())
                    .Should()
                    .Equal(Enumerable.Range(1, expectedRecordCount).Select(id => (long)id));
                continue;
            }

            var root = SqliteIndexInteriorPageView.Parse(
                pager.ReadCommittedPage(indexRoot),
                header.UsableSpace,
                header.TextEncoding);
            root.Cells.Should().ContainSingle();
            var leftPageNumber = root.Cells[0].Cell.LeftChildPage;
            var rightPageNumber = root.Header.RightMostChildPage;
            appendedPages.Add(leftPageNumber).Should().BeTrue();
            appendedPages.Add(rightPageNumber).Should().BeTrue();

            var left = SqliteIndexLeafPageView.Parse(
                pager.ReadCommittedPage(leftPageNumber),
                header.UsableSpace,
                header.TextEncoding);
            var right = SqliteIndexLeafPageView.Parse(
                pager.ReadCommittedPage(rightPageNumber),
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
        }

        appendedPages.Should().BeEquivalentTo(Enumerable.Range(
                checked((int)before.Header.DatabaseSizeInPages + 1),
                4)
            .Select(pageNumber => (uint)pageNumber));
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
                .SelectMany(childPage => ReadSchemaRecords(pager, header, childPage, isFirstPage: false)),
            var pageType => throw new InvalidDataException(
                $"Unexpected sqlite_schema page type {pageType}."),
        };
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

            foreach (var index in IndexNames)
            {
                using var lookup = sqlite.CreateCommand();
                lookup.CommandText =
                    $"SELECT id FROM target INDEXED BY {index} WHERE code = '{Code(InitialRowCount + 1)}';";
                Convert.ToInt64(lookup.ExecuteScalar()).Should().Be(InitialRowCount + 1);
            }
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

    private static long CountByCode(EmbeddedConnection connection, int id)
    {
        using var statement = connection.Prepare($"SELECT COUNT(*) FROM target WHERE code = '{Code(id)}';");
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static long IdByIndex(EmbeddedConnection connection, string indexName, int id)
        => IdByIndex(connection, indexName, Code(id));

    private static long IdByIndex(EmbeddedConnection connection, string indexName, string code)
    {
        using var statement = connection.Prepare(
            $"SELECT id FROM target WHERE code = '{code}';");
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static string InsertStatement(int id) => "INSERT INTO target VALUES " + InsertValues(id) + ";";

    private static string InsertValues(int id) => $"({id}, '{Code(id)}')";

    private static string Code(int id) => $"code-{id:D3}-{new string('x', 33)}";

    private static string ChangedCode(int id) => $"next-{id:D3}-{new string('z', 33)}";

    private static string CreateDatabasePath(string name)
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "managed-atomic-multi-index-root-leaf-promotion-tests");
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

    private sealed record RootSnapshot(
        SqliteDatabaseHeader Header,
        uint TableRoot,
        IReadOnlyList<uint> IndexRoots);
}
