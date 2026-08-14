using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public class PersistentSecondaryIndexOneLevelFileStoreTests
{
    private const int RowCount = 120;

    [Test]
    public void PersistsOneLevelSecondaryIndexWithOrderedLeavesAcrossReopenAndIntegrityCheck()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
                Execute(connection, BuildInsert(1, RowCount));
                Execute(connection, "CREATE INDEX t_value_binary ON t(value);");
            }

            using (var pager = SqlitePager.Open(
                       PhysicalFileSystem.Instance,
                       path,
                       path + "-wal",
                       readOnly: true))
            {
                pager.RecoveryInfo.LastCommittedFrameNumber.Should().BeGreaterThan(0);
                var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
                var rootPage = ReadIndexRootPage(pager, header, "t_value_binary");
                var overflowReader = new SqliteOverflowChainReader(pager, header);
                var root = SqliteIndexInteriorPageView.Parse(
                    pager.ReadCommittedPage(rootPage),
                    header.UsableSpace,
                    header.TextEncoding,
                    overflowReader: overflowReader);
                root.Cells.Should().NotBeEmpty();

                var childPages = root.Cells
                    .Select(cell => cell.Cell.LeftChildPage)
                    .Append(root.Header.RightMostChildPage)
                    .ToArray();
                childPages.Should().OnlyHaveUniqueItems();
                childPages.Length.Should().BeGreaterThan(2);

                var records = new List<SqlValue[]>();
                var comparer = new SqliteIndexRecordComparer(header.TextEncoding);
                for (var childIndex = 0; childIndex < childPages.Length; childIndex++)
                {
                    var leaf = SqliteIndexLeafPageView.Parse(
                        pager.ReadCommittedPage(childPages[childIndex]),
                        header.UsableSpace,
                        header.TextEncoding,
                        overflowReader: overflowReader);
                    leaf.Cells.Should().NotBeEmpty();
                    if (childIndex < root.Cells.Count)
                    {
                        var separator = root.GetRecord(childIndex);
                        comparer.Compare(leaf.GetRecord(leaf.Cells.Count - 1), separator).Should().BeLessThan(0);
                    }

                    for (var recordIndex = 0; recordIndex < leaf.Cells.Count; recordIndex++)
                        records.Add(SqliteRecordCodec.Decode(leaf.GetRecord(recordIndex), header.TextEncoding));
                    if (childIndex < root.Cells.Count)
                        records.Add(SqliteRecordCodec.Decode(root.GetRecord(childIndex), header.TextEncoding));
                }

                records.Select(record => record[0].AsText())
                    .Should()
                    .Equal(Enumerable.Range(1, RowCount).Select(IndexValue));
                records.Select(record => record[1].AsInteger())
                    .Should()
                    .Equal(Enumerable.Range(1, RowCount).Select(id => (long)id));
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                Query(connection, "PRAGMA index_list(t);")
                    .Select(row => row[1].AsText())
                    .Should()
                    .Contain("t_value_binary");
                Query(connection, $"SELECT id FROM t WHERE value = '{IndexValue(73)}';")
                    .Single()[0]
                    .AsInteger()
                    .Should()
                    .Be(73);
            }

            var verificationPath = path + ".verify.db";
            File.Copy(path, verificationPath, overwrite: true);
            try
            {
                using var sqlite = new MsData.SqliteConnection($"Data Source={verificationPath}");
                sqlite.Open();

                using var indexedCount = sqlite.CreateCommand();
                indexedCount.CommandText = "SELECT COUNT(*) FROM t INDEXED BY t_value_binary;";
                Convert.ToInt64(indexedCount.ExecuteScalar()).Should().Be(RowCount);

                using var integrity = sqlite.CreateCommand();
                integrity.CommandText = "PRAGMA integrity_check;";
                integrity.ExecuteScalar().Should().Be("ok");

                using var plan = sqlite.CreateCommand();
                plan.CommandText =
                    $"EXPLAIN QUERY PLAN SELECT id FROM t INDEXED BY t_value_binary WHERE value = '{IndexValue(73)}';";
                using var reader = plan.ExecuteReader();
                reader.Read().Should().BeTrue();
                reader.GetString(3).Should().Contain("t_value_binary");
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
    public void InterruptedOneLevelIndexWriteRecoversOnlyThePriorCommittedIndex()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        using (var database = EmbeddedDatabase.OpenFile("one-level-index-recovery.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, $"INSERT INTO t VALUES (1, '{IndexValue(1)}');");
            Execute(connection, "CREATE INDEX t_value_binary ON t(value);");

            faults.FailOnOccurrence(
                FileSystemOperation.Write,
                faults.GetOperationCount(FileSystemOperation.Write) + 3);
            Assert.Throws<IOException>(() => Execute(connection, BuildInsert(2, RowCount)));
        }

        using var recovered = EmbeddedDatabase.OpenFile("one-level-index-recovery.db", fileSystem);
        using var recoveredConnection = recovered.Connect();
        var rows = Query(recoveredConnection, "SELECT id, value FROM t ORDER BY id;");
        rows.Should().ContainSingle();
        rows[0][0].AsInteger().Should().Be(1);
        rows[0][1].AsText().Should().Be(IndexValue(1));
        Query(recoveredConnection, "PRAGMA index_list(t);")
            .Select(row => row[1].AsText())
            .Should()
            .Contain("t_value_binary");
    }

    [Test]
    public void ReopensOneLevelIndexWithOverflowLeafAndSeparatorKeys()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("one-level-index-overflow.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, BuildOverflowInsert());
            Execute(connection, "CREATE INDEX t_value_binary ON t(value);");
        }

        using (var pager = SqlitePager.Open(
                   fileSystem,
                   "one-level-index-overflow.db",
                   "one-level-index-overflow.db-wal",
                   readOnly: true))
        {
            var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            var overflowReader = new SqliteOverflowChainReader(pager, header);
            var rootPage = ReadIndexRootPage(pager, header, "t_value_binary");
            var root = SqliteIndexInteriorPageView.Parse(
                pager.ReadCommittedPage(rootPage),
                header.UsableSpace,
                header.TextEncoding,
                overflowReader: overflowReader);
            root.Cells.Should().ContainSingle();
            root.Cells[0].Cell.Key.FirstOverflowPage.Should().NotBeNull();

            var childPages = root.Cells
                .Select(cell => cell.Cell.LeftChildPage)
                .Append(root.Header.RightMostChildPage);
            foreach (var childPage in childPages)
            {
                var leaf = SqliteIndexLeafPageView.Parse(
                    pager.ReadCommittedPage(childPage),
                    header.UsableSpace,
                    header.TextEncoding,
                    overflowReader: overflowReader);
                leaf.Cells.Should().NotBeEmpty();
                leaf.Cells.Should().Contain(cell => cell.Cell.FirstOverflowPage != null);
            }
        }

        using var reopened = EmbeddedDatabase.OpenFile("one-level-index-overflow.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        Query(reopenedConnection, "SELECT COUNT(*) FROM t;").Single()[0].AsInteger().Should().Be(9);
        Query(reopenedConnection, "PRAGMA index_list(t);")
            .Select(row => row[1].AsText())
            .Should()
            .Contain("t_value_binary");
    }

    [Test]
    public void PersistsIndexWithAtLeastThreeInteriorLevelsAcrossReopenAndSqliteIntegrityCheck()
    {
        const int deepRowCount = 4_000;
        var path = CreateDatabasePath();
        try
        {
            CreatePageSizeDatabase(path, pageSize: 512);
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
                Execute(connection, BuildInsert(1, deepRowCount));
                Execute(connection, "CREATE INDEX t_value_binary ON t(value);");
            }

            using (var pager = SqlitePager.Open(
                       PhysicalFileSystem.Instance,
                       path,
                       path + "-wal",
                       readOnly: true))
            {
                var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
                var rootPage = ReadIndexRootPage(pager, header, "t_value_binary");
                ReadIndexHeight(
                        pager,
                        header,
                        rootPage,
                        new SqliteOverflowChainReader(pager, header),
                        new HashSet<uint>())
                    .Should()
                    .BeGreaterThanOrEqualTo(4);
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                Query(connection, "SELECT COUNT(*) FROM t;").Single()[0].AsInteger().Should().Be(deepRowCount);
                Query(connection, $"SELECT id FROM t WHERE value = '{IndexValue(3_217)}';")
                    .Single()[0]
                    .AsInteger()
                    .Should()
                    .Be(3_217);
            }

            var verificationPath = path + ".verify.db";
            File.Copy(path, verificationPath, overwrite: true);
            try
            {
                using var sqlite = new MsData.SqliteConnection($"Data Source={verificationPath}");
                sqlite.Open();

                using var indexedCount = sqlite.CreateCommand();
                indexedCount.CommandText = "SELECT COUNT(*) FROM t INDEXED BY t_value_binary;";
                Convert.ToInt64(indexedCount.ExecuteScalar()).Should().Be(deepRowCount);

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
    public void ReopenRejectsOneLevelIndexWhoseSeparatorDoesNotMatchItsLeaf()
    {
        var fileSystem = new InMemoryFileSystem();
        SqliteDatabaseHeader header;
        uint rootPage;
        using (var database = EmbeddedDatabase.OpenFile("one-level-index-corrupt.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, BuildInsert(1, RowCount));
            Execute(connection, "CREATE INDEX t_value_binary ON t(value);");
        }

        using (var store = SqlitePageStore.Open(fileSystem, "one-level-index-corrupt.db"))
        {
            header = store.Header;
            using var pager = SqlitePager.Open(
                fileSystem,
                "one-level-index-corrupt.db",
                "one-level-index-corrupt.db-wal",
                readOnly: true);
            rootPage = ReadIndexRootPage(pager, header, "t_value_binary");
        }

        using (var store = SqlitePageStore.Open(fileSystem, "one-level-index-corrupt.db"))
        {
            var rootImage = store.ReadPage(rootPage);
            var root = SqliteIndexInteriorPageView.Parse(rootImage, header.UsableSpace, header.TextEncoding);
            var leftLeaf = SqliteIndexLeafPageView.Parse(
                store.ReadPage(root.Cells[0].Cell.LeftChildPage),
                header.UsableSpace,
                header.TextEncoding);
            var replacement = leftLeaf.GetRecord(leftLeaf.Cells.Count - 1);
            replacement.Length.Should().Be(root.GetRecord(0).Length);
            var cellOffset = root.CellPointers[0] + sizeof(uint);
            SqliteVarint.TryRead(rootImage.AsSpan(cellOffset), out _, out var payloadLengthBytes).Should().BeTrue();
            replacement.CopyTo(rootImage.AsSpan(cellOffset + payloadLengthBytes));
            store.WritePage(rootPage, rootImage);
            store.Flush();
        }

        fileSystem.DeleteFile("one-level-index-corrupt.db-wal");
        using (SqliteWalFile.Create(
                   fileSystem,
                   "one-level-index-corrupt.db-wal",
                   SqliteWalHeader.Create(header.PageSize, salt1: 1, salt2: 2)))
        {
        }

        var reopen = () => EmbeddedDatabase.OpenFile("one-level-index-corrupt.db", fileSystem);
        reopen.Should().Throw<EmbeddedSqlException>().WithMessage("*index*");
    }

    private static uint ReadIndexRootPage(SqlitePager pager, SqliteDatabaseHeader header, string indexName)
    {
        var schema = SqliteTableLeafPageView.Parse(
            pager.ReadCommittedPage(1),
            header.UsableSpace,
            isFirstPage: true);
        return checked((uint)schema.Cells
            .Select(cell => SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding))
            .Single(values => values[0].AsText() == "index" && values[1].AsText() == indexName)[3]
            .AsInteger());
    }

    private static int ReadIndexHeight(
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
            return 1;
        }

        pageHeader.PageType.Should().Be(SqliteBtreePageType.IndexInterior);
        var interior = SqliteIndexInteriorPageView.Parse(
            page,
            header.UsableSpace,
            header.TextEncoding,
            overflowReader: overflowReader);
        interior.Cells.Should().NotBeEmpty();
        var childHeights = interior.Cells
            .Select(cell => ReadIndexHeight(
                pager,
                header,
                cell.Cell.LeftChildPage,
                overflowReader,
                seenPages))
            .Append(ReadIndexHeight(
                pager,
                header,
                interior.Header.RightMostChildPage,
                overflowReader,
                seenPages))
            .ToArray();
        childHeights.Should().OnlyContain(height => height == childHeights[0]);
        return childHeights[0] + 1;
    }

    private static string BuildInsert(int firstId, int lastId)
    {
        var rows = Enumerable.Range(firstId, lastId - firstId + 1)
            .Select(id => $"({id}, '{IndexValue(id)}')");
        return $"INSERT INTO t VALUES {string.Join(", ", rows)};";
    }

    private static string BuildOverflowInsert()
    {
        var value = new string('z', 10_000);
        var rows = Enumerable.Range(1, 9)
            .Select(id => $"({id}, 'overflow-{id:D4}-{value}')");
        return $"INSERT INTO t VALUES {string.Join(", ", rows)};";
    }

    private static string IndexValue(int id) => $"value-{id:D4}-{new string('x', 96)}";

    private static void CreatePageSizeDatabase(string path, int pageSize)
    {
        var header = SqliteDatabaseHeader.CreateDefault() with { PageSize = pageSize };
        using (SqlitePager.Create(
                   PhysicalFileSystem.Instance,
                   path,
                   path + "-wal",
                   SqliteWalHeader.Create(pageSize, salt1: 101, salt2: 103),
                   header))
        {
        }
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

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

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "persistent-secondary-index-one-level-file-store-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"index-{Guid.NewGuid():N}.db");
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
