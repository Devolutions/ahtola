using System.Buffers.Binary;
using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class WithoutRowidBinaryPrimaryKeyRecursiveStorageTests
{
    private const int DeepOverflowRowCount = 80;
    private const int DeepOverflowKeyLength = 5_000;
    private const int DeepBinaryRowCount = 1_600;
    private const string Aes256Key = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    [Test]
    public void DeepOverflowBinaryPrimaryKeyTreeReopensAfterUpdateAndDeleteAndPassesSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath("deep-overflow");
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE entry(payload TEXT, code TEXT PRIMARY KEY) WITHOUT ROWID;");
                Execute(connection, BuildOverflowInsert(1, DeepOverflowRowCount));
                Assert.Throws<EmbeddedSqlException>(
                    () => Execute(connection, $"INSERT INTO entry VALUES ('duplicate', '{OverflowKey(20)}');"));
                Execute(connection, $"UPDATE entry SET payload = 'updated' WHERE code = '{OverflowKey(20)}';");
                Execute(connection, $"DELETE FROM entry WHERE code = '{OverflowKey(40)}';");
                Execute(connection, $"INSERT INTO entry VALUES ('replacement', '{OverflowKey(DeepOverflowRowCount + 1)}');");
            }

            using (var pager = SqlitePager.Open(
                       PhysicalFileSystem.Instance,
                       path,
                       path + "-wal",
                       readOnly: true))
            {
                var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
                var rootPage = FindTableRootPage(pager.ReadCommittedPage(1), header, "entry");
                var overflowReader = new SqliteOverflowChainReader(pager, header);

                GetIndexTreeHeight(pager, header, rootPage, overflowReader).Should().BeGreaterThanOrEqualTo(2);
                var records = ReadIndexTreeRecords(pager, header, rootPage, overflowReader);
                records.Should().HaveCount(DeepOverflowRowCount);
                records
                    .Select(record => SqliteRecordCodec.Decode(record, header.TextEncoding)[0].AsText())
                    .Should()
                    .Equal(ExpectedOverflowKeys());
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                Scalar(connection, "SELECT COUNT(*) FROM entry;").AsInteger().Should().Be(DeepOverflowRowCount);
                Scalar(connection, $"SELECT payload FROM entry WHERE code = '{OverflowKey(20)}';")
                    .AsText()
                    .Should()
                    .Be("updated");
                Scalar(connection, $"SELECT COUNT(*) FROM entry WHERE code = '{OverflowKey(40)}';")
                    .AsInteger()
                    .Should()
                    .Be(0);
                Scalar(connection, $"SELECT payload FROM entry WHERE code = '{OverflowKey(DeepOverflowRowCount + 1)}';")
                    .AsText()
                    .Should()
                    .Be("replacement");
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
    public void CompositeCollatedTableAndSecondaryIndexesSplitAndPassSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath("composite-index-pressure");
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, """
                    CREATE TABLE entry(
                        tenant TEXT,
                        sequence INTEGER,
                        value TEXT,
                        payload TEXT,
                        computed INTEGER GENERATED ALWAYS AS (sequence % 17) VIRTUAL,
                        PRIMARY KEY(tenant COLLATE NOCASE, sequence DESC),
                        UNIQUE(value)
                    ) WITHOUT ROWID;
                    """);
                Execute(connection, "CREATE INDEX entry_payload ON entry(payload DESC, tenant COLLATE BINARY);");
                Execute(connection, "CREATE INDEX entry_computed ON entry(computed DESC);");
                Execute(connection, BuildCompositeInsert(1, DeepBinaryRowCount));
            }

            using (var pager = SqlitePager.Open(
                       PhysicalFileSystem.Instance,
                       path,
                       path + "-wal",
                       readOnly: true))
            {
                var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
                foreach (var (type, name) in new[]
                         {
                             ("table", "entry"),
                             ("index", "sqlite_autoindex_entry_2"),
                             ("index", "entry_payload"),
                             ("index", "entry_computed"),
                         })
                {
                    var root = FindRootPage(pager.ReadCommittedPage(1), header, type, name);
                    SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(root)).PageType
                        .Should().Be(SqliteBtreePageType.IndexInterior);
                }
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                Scalar(connection, "SELECT COUNT(*) FROM entry;").AsInteger().Should().Be(DeepBinaryRowCount);
                Scalar(connection, "SELECT computed FROM entry WHERE value = 'value-01600';")
                    .AsInteger().Should().Be(DeepBinaryRowCount % 17);
            }

            VerifyWithSqlite(path, DeepBinaryRowCount);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void EncryptedReadOnlyReopenLoadsDeepBinaryPrimaryKeyTree()
    {
        using var encryption = AhtolaEncryptionOptions.FromHex(AhtolaEncryptionCipher.Aes256Gcm, Aes256Key);
        var fileSystem = new AhtolaEncryptionFileSystem(new InMemoryFileSystem(), encryption);
        const string path = "without-rowid-recursive-encrypted.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE entry(payload TEXT, code TEXT PRIMARY KEY) WITHOUT ROWID;");
            Execute(connection, BuildBinaryInsert(1, DeepBinaryRowCount));
        }

        using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
        {
            var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            var rootPage = FindTableRootPage(pager.ReadCommittedPage(1), header, "entry");
            GetIndexTreeHeight(pager, header, rootPage, new SqliteOverflowChainReader(pager, header))
                .Should()
                .BeGreaterThanOrEqualTo(2);
        }

        using (var readOnly = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true))
        using (var connection = readOnly.Connect())
        {
            Scalar(connection, "SELECT COUNT(*) FROM entry;").AsInteger().Should().Be(DeepBinaryRowCount);
            Scalar(connection, $"SELECT payload FROM entry WHERE code = '{BinaryKey(DeepBinaryRowCount)}';")
                .AsText()
                .Should()
                .Be(BinaryPayload(DeepBinaryRowCount));
            Assert.Throws<EmbeddedSqlException>(
                () => Execute(connection, $"INSERT INTO entry VALUES ('blocked', '{BinaryKey(DeepBinaryRowCount + 1)}');"))!
                .Message.Should().Be("attempt to write a readonly database");
        }
    }

    [Test]
    public void InterruptedRecursiveBinaryPrimaryKeyRewriteRecoversOnlyPriorCommit()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "without-rowid-recursive-wal-failure.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE entry(payload TEXT, code TEXT PRIMARY KEY) WITHOUT ROWID;");
            Execute(connection, $"INSERT INTO entry VALUES ('committed', '{OverflowKey(1)}');");

            faults.FailOnOccurrence(
                FileSystemOperation.Write,
                faults.GetOperationCount(FileSystemOperation.Write) + 3);
            Assert.Throws<IOException>(() => Execute(connection, BuildOverflowInsert(2, DeepOverflowRowCount - 1)));
        }

        faults.ClearScheduled();
        using var recovered = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var recoveredConnection = recovered.Connect();
        Scalar(recoveredConnection, $"SELECT payload FROM entry WHERE code = '{OverflowKey(1)}';")
            .AsText()
            .Should()
            .Be("committed");
        Scalar(recoveredConnection, "SELECT COUNT(*) FROM entry;").AsInteger().Should().Be(1);
    }

    [Test]
    public void RecursiveBinaryPrimaryKeyTreeWithAliasedChildIsRejectedOnReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "without-rowid-recursive-corrupt.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE entry(payload TEXT, code TEXT PRIMARY KEY) WITHOUT ROWID;");
            Execute(connection, BuildOverflowInsert(1, DeepOverflowRowCount));
        }

        using (var store = SqlitePageStore.Open(fileSystem, path))
        {
            var header = store.Header;
            var rootPage = FindTableRootPage(store.ReadPage(1), header, "entry");
            var root = SqliteIndexInteriorPageView.Parse(
                store.ReadPage(rootPage),
                header.UsableSpace,
                header.TextEncoding);
            root.Cells.Should().NotBeEmpty();

            var rootImage = store.ReadPage(rootPage);
            BinaryPrimitives.WriteUInt32BigEndian(
                rootImage.AsSpan(root.CellPointers[0], sizeof(uint)),
                rootPage);
            store.WritePage(rootPage, rootImage);
            store.Flush();
        }

        var reopen = () => EmbeddedDatabase.OpenFile(path, fileSystem);
        reopen.Should().Throw<EmbeddedSqlException>().WithMessage("*WITHOUT ROWID table*");
    }

    private static int GetIndexTreeHeight(
        SqlitePager pager,
        SqliteDatabaseHeader header,
        uint pageNumber,
        SqliteOverflowChainReader overflowReader)
    {
        var page = pager.ReadCommittedPage(pageNumber);
        var pageHeader = SqliteBtreePageHeader.Parse(page);
        return pageHeader.PageType switch
        {
            SqliteBtreePageType.IndexLeaf => 0,
            SqliteBtreePageType.IndexInterior => GetInteriorHeight(
                SqliteIndexInteriorPageView.Parse(
                    page,
                    header.UsableSpace,
                    header.TextEncoding,
                    overflowReader: overflowReader),
                pager,
                header,
                overflowReader),
            _ => throw new InvalidDataException($"Unexpected page type {pageHeader.PageType} in WITHOUT ROWID tree."),
        };
    }

    private static int GetInteriorHeight(
        SqliteIndexInteriorPageView interior,
        SqlitePager pager,
        SqliteDatabaseHeader header,
        SqliteOverflowChainReader overflowReader)
    {
        var heights = interior.Cells
            .Select(cell => GetIndexTreeHeight(pager, header, cell.Cell.LeftChildPage, overflowReader))
            .Append(GetIndexTreeHeight(pager, header, interior.Header.RightMostChildPage, overflowReader))
            .ToArray();
        heights.Should().OnlyContain(height => height == heights[0]);
        return checked(heights[0] + 1);
    }

    private static List<byte[]> ReadIndexTreeRecords(
        SqlitePager pager,
        SqliteDatabaseHeader header,
        uint pageNumber,
        SqliteOverflowChainReader overflowReader)
    {
        var page = pager.ReadCommittedPage(pageNumber);
        var pageHeader = SqliteBtreePageHeader.Parse(page);
        switch (pageHeader.PageType)
        {
            case SqliteBtreePageType.IndexLeaf:
                {
                    var leaf = SqliteIndexLeafPageView.Parse(
                        page,
                        header.UsableSpace,
                        header.TextEncoding,
                        overflowReader: overflowReader);
                    return Enumerable.Range(0, leaf.Cells.Count).Select(leaf.GetRecord).ToList();
                }
            case SqliteBtreePageType.IndexInterior:
                {
                    var interior = SqliteIndexInteriorPageView.Parse(
                        page,
                        header.UsableSpace,
                        header.TextEncoding,
                        overflowReader: overflowReader);
                    var records = new List<byte[]>();
                    for (var childIndex = 0; childIndex <= interior.Cells.Count; childIndex++)
                    {
                        var childPage = childIndex == interior.Cells.Count
                            ? interior.Header.RightMostChildPage
                            : interior.Cells[childIndex].Cell.LeftChildPage;
                        records.AddRange(ReadIndexTreeRecords(pager, header, childPage, overflowReader));
                        if (childIndex < interior.Cells.Count)
                            records.Add(interior.GetRecord(childIndex));
                    }

                    return records;
                }
            default:
                throw new InvalidDataException($"Unexpected page type {pageHeader.PageType} in WITHOUT ROWID tree.");
        }
    }

    private static uint FindTableRootPage(ReadOnlySpan<byte> schemaPage, SqliteDatabaseHeader header, string name)
        => FindRootPage(schemaPage, header, "table", name);

    private static uint FindRootPage(
        ReadOnlySpan<byte> schemaPage,
        SqliteDatabaseHeader header,
        string type,
        string name)
    {
        var schema = SqliteTableLeafPageView.Parse(schemaPage, header.UsableSpace, isFirstPage: true);
        return checked((uint)schema.Cells
            .Select(cell => SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding))
            .Single(values => values[0].AsText() == type && values[1].AsText() == name)[3]
            .AsInteger());
    }

    private static IEnumerable<string> ExpectedOverflowKeys()
    {
        foreach (var index in Enumerable.Range(1, DeepOverflowRowCount + 1))
        {
            if (index != 40)
                yield return OverflowKey(index);
        }
    }

    private static string BuildOverflowInsert(int firstIndex, int count)
        => $"INSERT INTO entry VALUES {string.Join(", ", Enumerable.Range(firstIndex, count)
            .Select(index => $"('payload-{index}', '{OverflowKey(index)}')"))};";

    private static string BuildBinaryInsert(int firstIndex, int count)
        => $"INSERT INTO entry VALUES {string.Join(", ", Enumerable.Range(firstIndex, count)
            .Select(index => $"('{BinaryPayload(index)}', '{BinaryKey(index)}')"))};";

    private static string BuildCompositeInsert(int firstIndex, int count)
        => $"INSERT INTO entry(tenant, sequence, value, payload) VALUES {string.Join(", ", Enumerable.Range(firstIndex, count)
            .Reverse()
            .Select(index =>
                $"('tenant-{index % 32:D2}', {index}, 'value-{index:D5}', 'payload-{index % 101:D3}-{new string('p', 96)}')"))};";

    private static string OverflowKey(int index)
        => $"key-{index:D5}-{new string('z', DeepOverflowKeyLength)}";

    private static string BinaryKey(int index)
        => $"key-{index:D5}-{new string('b', 96)}";

    private static string BinaryPayload(int index) => $"payload-{index:D5}";

    private static void VerifyWithSqlite(string path, int expectedRowCount = DeepOverflowRowCount)
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
            count.CommandText = "SELECT COUNT(*) FROM entry;";
            Convert.ToInt64(count.ExecuteScalar()).Should().Be(expectedRowCount);
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

    private static SqlValue Scalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0);
    }

    private static string CreateDatabasePath(string name)
    {
        var directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "without-rowid-binary-primary-key-recursive-storage-tests");
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
