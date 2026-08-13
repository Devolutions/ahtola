using System.Buffers.Binary;
using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedBoundedThreeLevelSecondaryIndexPersistenceTests
{
    private const int RowCount = 1_600;
    private const int OverflowRowCount = 80;
    private const int OverflowValueLength = 5_000;
    private const string Aes256Key = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    [Test]
    public void TwoInteriorLevelSecondaryIndexPersistsReopensReadOnlyAndPassesSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath("integrity");
        try
        {
            CreatePressureIndex(path, PhysicalFileSystem.Instance);

            using (var pager = SqlitePager.Open(
                       PhysicalFileSystem.Instance,
                       path,
                       path + "-wal",
                       readOnly: true))
            {
                AssertTwoInteriorLevelIndex(pager, RowCount, IndexValue);
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                QueryCount(connection).Should().Be(RowCount);
                QueryId(connection, RowCount).Should().Be(RowCount);
            }

            using (var readOnly = EmbeddedDatabase.OpenFile(path, readOnly: true))
            using (var connection = readOnly.Connect())
            {
                QueryCount(connection).Should().Be(RowCount);
                QueryId(connection, RowCount / 2).Should().Be(RowCount / 2);
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
    public void TwoInteriorLevelSecondaryIndexPreservesOverflowSeparatorsAcrossReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "two-interior-overflow-index.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, BuildOverflowInsert());
            Execute(connection, "CREATE INDEX t_value_binary ON t(value);");
        }

        using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
        {
            AssertTwoInteriorLevelIndex(pager, OverflowRowCount, OverflowIndexValue);
            var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            var rootPage = FindIndexRootPage(pager.ReadCommittedPage(1), header);
            var overflowReader = new SqliteOverflowChainReader(pager, header);
            var root = SqliteIndexInteriorPageView.Parse(
                pager.ReadCommittedPage(rootPage),
                header.UsableSpace,
                header.TextEncoding,
                overflowReader: overflowReader);
            root.Cells.Should().Contain(cell => cell.Cell.Key.FirstOverflowPage != null);

            var interior = SqliteIndexInteriorPageView.Parse(
                pager.ReadCommittedPage(root.Cells[0].Cell.LeftChildPage),
                header.UsableSpace,
                header.TextEncoding,
                overflowReader: overflowReader);
            interior.Cells.Should().Contain(cell => cell.Cell.Key.FirstOverflowPage != null);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        QueryCount(reopenedConnection).Should().Be(OverflowRowCount);
        QueryId(reopenedConnection, OverflowIndexValue(OverflowRowCount)).Should().Be(OverflowRowCount);
    }

    [Test]
    public void InterruptedTwoInteriorLevelIndexRewriteRecoversThePriorCommittedIndex()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "two-interior-index-wal-failure.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, BuildInsert(1, 1));
            Execute(connection, "CREATE INDEX t_value_binary ON t(value);");

            faults.FailOnOccurrence(
                FileSystemOperation.Write,
                faults.GetOperationCount(FileSystemOperation.Write) + 3);
            Assert.Throws<IOException>(() => Execute(connection, BuildInsert(2, RowCount - 1)));
        }

        using var recovered = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var recoveredConnection = recovered.Connect();
        QueryCount(recoveredConnection).Should().Be(1);
        QueryId(recoveredConnection, 1).Should().Be(1);
        Query(recoveredConnection, "PRAGMA index_list(t);")
            .Select(row => row[1].AsText())
            .Should()
            .Contain("t_value_binary");
    }

    [Test]
    public void EncryptedTwoInteriorLevelSecondaryIndexReopensReadOnly()
    {
        using var encryption = AhtolaEncryptionOptions.FromHex(
            AhtolaEncryptionCipher.Aes256Gcm,
            Aes256Key);
        var fileSystem = new AhtolaEncryptionFileSystem(new InMemoryFileSystem(), encryption);
        const string path = "two-interior-encrypted-index.db";

        CreatePressureIndex(path, fileSystem);

        using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
        {
            AssertTwoInteriorLevelIndex(pager, RowCount, IndexValue);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var connection = reopened.Connect();
        QueryCount(connection).Should().Be(RowCount);
        QueryId(connection, RowCount).Should().Be(RowCount);
    }

    [Test]
    public void ReopenRejectsTwoInteriorLevelIndexWithAliasedLeafChild()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "two-interior-index-corrupt.db";
        SqliteDatabaseHeader header;
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, BuildInsert(1, RowCount));
            Execute(connection, "CREATE INDEX t_value_binary ON t(value);");
        }

        using (var store = SqlitePageStore.Open(fileSystem, path))
        {
            header = store.Header;
            var rootPage = FindIndexRootPage(store.ReadPage(1), header);
            var root = SqliteIndexInteriorPageView.Parse(
                store.ReadPage(rootPage),
                header.UsableSpace,
                header.TextEncoding);
            root.Cells.Should().NotBeEmpty();

            var interiorPage = root.Cells[0].Cell.LeftChildPage;
            var interior = SqliteIndexInteriorPageView.Parse(
                store.ReadPage(interiorPage),
                header.UsableSpace,
                header.TextEncoding);
            interior.Cells.Should().NotBeEmpty();

            var interiorImage = store.ReadPage(interiorPage);
            BinaryPrimitives.WriteUInt32BigEndian(
                interiorImage.AsSpan(interior.CellPointers[0], sizeof(uint)),
                rootPage);
            store.WritePage(interiorPage, interiorImage);
            store.Flush();
        }

        ReplaceWalWithEmptyFile(fileSystem, path, header, salt1: 67, salt2: 71);

        var reopen = () => EmbeddedDatabase.OpenFile(path, fileSystem);
        reopen.Should().Throw<EmbeddedSqlException>().WithMessage("*index*");
    }

    private static void CreatePressureIndex(string path, IFileSystem fileSystem)
    {
        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
        Execute(connection, BuildInsert(1, RowCount));
        Execute(connection, "CREATE INDEX t_value_binary ON t(value);");
    }

    private static void AssertTwoInteriorLevelIndex(
        SqlitePager pager,
        int expectedRowCount,
        Func<int, string> expectedValue)
    {
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var rootPage = FindIndexRootPage(pager.ReadCommittedPage(1), header);
        var overflowReader = new SqliteOverflowChainReader(pager, header);
        var comparer = new SqliteIndexRecordComparer(header.TextEncoding);
        var root = SqliteIndexInteriorPageView.Parse(
            pager.ReadCommittedPage(rootPage),
            header.UsableSpace,
            header.TextEncoding,
            overflowReader: overflowReader);
        root.Cells.Should().NotBeEmpty();

        var seenPages = new HashSet<uint> { rootPage };
        var records = new List<byte[]>();
        byte[]? previousRecord = null;
        foreach (var (interiorPage, rootChildIndex) in root.Cells
                     .Select((cell, index) => (cell.Cell.LeftChildPage, index))
                     .Append((root.Header.RightMostChildPage, root.Cells.Count)))
        {
            seenPages.Add(interiorPage).Should().BeTrue();
            var interior = SqliteIndexInteriorPageView.Parse(
                pager.ReadCommittedPage(interiorPage),
                header.UsableSpace,
                header.TextEncoding,
                overflowReader: overflowReader);
            interior.Cells.Should().NotBeEmpty();

            var subtreeRecords = new List<byte[]>();
            foreach (var (leafPage, interiorChildIndex) in interior.Cells
                         .Select((cell, index) => (cell.Cell.LeftChildPage, index))
                         .Append((interior.Header.RightMostChildPage, interior.Cells.Count)))
            {
                seenPages.Add(leafPage).Should().BeTrue();
                var leaf = SqliteIndexLeafPageView.Parse(
                    pager.ReadCommittedPage(leafPage),
                    header.UsableSpace,
                    header.TextEncoding,
                    overflowReader: overflowReader);
                leaf.Cells.Should().NotBeEmpty();
                var leafRecords = Enumerable.Range(0, leaf.Cells.Count)
                    .Select(leaf.GetRecord)
                    .ToArray();
                AppendOrderedRecords(subtreeRecords, leafRecords, comparer);
                if (interiorChildIndex < interior.Cells.Count)
                {
                    var separator = interior.GetRecord(interiorChildIndex);
                    comparer.Compare(leafRecords[^1], separator).Should().BeLessThan(0);
                    AppendOrderedRecords(subtreeRecords, [separator], comparer);
                }
            }

            AppendOrderedRecords(records, subtreeRecords, comparer);
            if (rootChildIndex < root.Cells.Count)
            {
                var separator = root.GetRecord(rootChildIndex);
                comparer.Compare(subtreeRecords[^1], separator).Should().BeLessThan(0);
                AppendOrderedRecords(records, [separator], comparer);
            }
        }

        foreach (var record in records)
        {
            if (previousRecord is not null)
                comparer.Compare(previousRecord, record).Should().BeLessThan(0);
            previousRecord = record;
        }

        records.Select(record => SqliteRecordCodec.Decode(record, header.TextEncoding)[0].AsText())
            .Should()
            .Equal(Enumerable.Range(1, expectedRowCount).Select(expectedValue));
        records.Select(record => SqliteRecordCodec.Decode(record, header.TextEncoding)[1].AsInteger())
            .Should()
            .Equal(Enumerable.Range(1, expectedRowCount).Select(id => (long)id));
    }

    private static void AppendOrderedRecords(
        ICollection<byte[]> destination,
        IReadOnlyList<byte[]> source,
        SqliteIndexRecordComparer comparer)
    {
        foreach (var record in source)
        {
            if (destination.LastOrDefault() is { } previous)
                comparer.Compare(previous, record).Should().BeLessThan(0);
            destination.Add(record);
        }
    }

    private static uint FindIndexRootPage(ReadOnlySpan<byte> schemaPage, SqliteDatabaseHeader header)
    {
        var schema = SqliteTableLeafPageView.Parse(
            schemaPage,
            header.UsableSpace,
            isFirstPage: true);
        return checked((uint)schema.Cells
            .Select(cell => SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding))
            .Single(values => values[0].AsText() == "index" && values[1].AsText() == "t_value_binary")[3]
            .AsInteger());
    }

    private static string BuildInsert(int firstId, int count)
    {
        var rows = Enumerable.Range(firstId, count)
            .Select(id => $"({id}, '{IndexValue(id)}')");
        return $"INSERT INTO t VALUES {string.Join(", ", rows)};";
    }

    private static string IndexValue(int id) => $"value-{id:D5}-{new string('x', 96)}";

    private static string BuildOverflowInsert()
    {
        var rows = Enumerable.Range(1, OverflowRowCount)
            .Select(id => $"({id}, '{OverflowIndexValue(id)}')");
        return $"INSERT INTO t VALUES {string.Join(", ", rows)};";
    }

    private static string OverflowIndexValue(int id)
        => $"overflow-{id:D5}-{new string('z', OverflowValueLength)}";

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

    private static long QueryCount(EmbeddedConnection connection)
        => Query(connection, "SELECT COUNT(*) FROM t;").Single()[0].AsInteger();

    private static long QueryId(EmbeddedConnection connection, int id)
        => QueryId(connection, IndexValue(id));

    private static long QueryId(EmbeddedConnection connection, string value)
        => Query(connection, $"SELECT id FROM t WHERE value = '{value}';").Single()[0].AsInteger();

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
            "managed-bounded-three-level-secondary-index-persistence-tests");
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

    private static void ReplaceWalWithEmptyFile(
        IFileSystem fileSystem,
        string path,
        SqliteDatabaseHeader header,
        uint salt1,
        uint salt2)
    {
        fileSystem.DeleteFile(path + "-wal");
        using (SqliteWalFile.Create(
                   fileSystem,
                   path + "-wal",
                   SqliteWalHeader.Create(header.PageSize, salt1, salt2)))
        {
        }
    }
}
