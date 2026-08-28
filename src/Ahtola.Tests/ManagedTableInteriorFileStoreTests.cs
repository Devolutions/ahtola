using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public class ManagedTableInteriorFileStoreTests
{
    [Test]
    public void PersistsOneLevelTableInteriorAcrossReopenAndRealSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
                Execute(connection, BuildInsert(1, 120));
            }

            using (var pager = SqlitePager.Open(
                       PhysicalFileSystem.Instance,
                       path,
                       path + "-wal",
                       readOnly: true))
            {
                pager.RecoveryInfo.LastCommittedFrameNumber.Should().BeGreaterThan(0);
                var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
                var schema = SqliteTableLeafPageView.Parse(
                    pager.ReadCommittedPage(1),
                    header.UsableSpace,
                    isFirstPage: true);
                var rootPage = checked((uint)schema.Cells
                    .Select(cell => SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding))
                    .Single(values => values[0].AsText() == "table" && values[1].AsText() == "t")[3]
                    .AsInteger());

                var root = SqliteTableInteriorPageView.Parse(
                    pager.ReadCommittedPage(rootPage),
                    header.UsableSpace);
                root.Cells.Should().NotBeEmpty();
                var childPages = root.Cells
                    .Select(cell => cell.Cell.LeftChildPage)
                    .Append(root.Header.RightMostChildPage)
                    .ToArray();
                childPages.Should().OnlyHaveUniqueItems();

                var rowIds = new List<long>();
                for (var childIndex = 0; childIndex < childPages.Length; childIndex++)
                {
                    var leaf = SqliteTableLeafPageView.Parse(
                        pager.ReadCommittedPage(childPages[childIndex]),
                        header.UsableSpace);
                    leaf.Cells.Should().NotBeEmpty();
                    if (childIndex < root.Cells.Count)
                    {
                        leaf.Cells[^1].Cell.RowId.Should().Be(root.Cells[childIndex].Cell.RowId);
                    }

                    rowIds.AddRange(leaf.Cells.Select(cell => cell.Cell.RowId));
                }

                rowIds.Should().Equal(Enumerable.Range(1, 120).Select(value => (long)value));
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                var rows = Query(connection, "SELECT id, value FROM t ORDER BY id;");
                rows.Select(row => row[0].AsInteger())
                    .Should()
                    .Equal(Enumerable.Range(1, 120).Select(value => (long)value));
                rows[^1][1].AsText().Should().Contain("row-120");
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

                using var count = sqlite.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM t;";
                Convert.ToInt64(count.ExecuteScalar()).Should().Be(120);
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
    public void InterruptedTableInteriorWriteRecoversThePriorCommittedLeaf()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        using (var database = EmbeddedDatabase.OpenFile("interior-recovery.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, "INSERT INTO t VALUES (1, 'committed');");

            faults.FailOnOccurrence(
                FileSystemOperation.Write,
                faults.GetOperationCount(FileSystemOperation.Write) + 3);
            Assert.Throws<IOException>(() => Execute(connection, BuildInsert(2, 120)));
        }

        using var recovered = EmbeddedDatabase.OpenFile("interior-recovery.db", fileSystem);
        using var recoveredConnection = recovered.Connect();
        var rows = Query(recoveredConnection, "SELECT id, value FROM t ORDER BY id;");
        rows.Should().ContainSingle();
        rows[0][0].AsInteger().Should().Be(1);
        rows[0][1].AsText().Should().Be("committed");
    }

    [Test]
        public void ReopenRejectsInteriorWhoseSeparatorIsBelowItsLeafMaximum()
    {
        var fileSystem = new InMemoryFileSystem();
        SqliteDatabaseHeader header;
        uint rootPage;
        using (var database = EmbeddedDatabase.OpenFile("interior-corrupt.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, BuildInsert(1, 120));
        }

        using (var store = SqlitePageStore.Open(fileSystem, "interior-corrupt.db"))
        {
            header = store.Header;
            var schema = SqliteTableLeafPageView.Parse(
                store.ReadPage(1),
                header.UsableSpace,
                isFirstPage: true);
            rootPage = checked((uint)schema.Cells
                .Select(cell => SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding))
                .Single(values => values[0].AsText() == "table" && values[1].AsText() == "t")[3]
                .AsInteger());

            var root = SqliteTableInteriorPageView.Parse(
                store.ReadPage(rootPage),
                header.UsableSpace);
            root.Cells.Should().NotBeEmpty();

                // Force separator rowid to 0 so the left child's live max exceeds it.
                var rootImage = store.ReadPage(rootPage);
                rootImage[root.CellPointers[0] + sizeof(uint)] = 0;
                store.WritePage(rootPage, rootImage);
                store.Flush();
            }

            fileSystem.DeleteFile("interior-corrupt.db-wal");
            using (SqliteWalFile.Create(
                       fileSystem,
                       "interior-corrupt.db-wal",
                       SqliteWalHeader.Create(header.PageSize, salt1: 1, salt2: 2)))
            {
            }

            var reopen = () => EmbeddedDatabase.OpenFile("interior-corrupt.db", fileSystem);
            reopen.Should().Throw<EmbeddedSqlException>().WithMessage("*separator*below maximum rowid*");
        }

        [Test]
        public void ReopenRejectsInteriorWhoseSeparatorOverlapsFollowingLeaf()
        {
            var fileSystem = new InMemoryFileSystem();
            const string path = "interior-overlapping-separator.db";
            SqliteDatabaseHeader header;
            uint rootPage;
            using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
                Execute(connection, BuildInsert(1, 120));
            }

            using (var store = SqlitePageStore.Open(fileSystem, path))
            {
                header = store.Header;
                var schema = SqliteTableLeafPageView.Parse(
                    store.ReadPage(1),
                    header.UsableSpace,
                    isFirstPage: true);
                rootPage = checked((uint)schema.Cells
                    .Select(cell => SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding))
                    .Single(values => values[0].AsText() == "table" && values[1].AsText() == "t")[3]
                    .AsInteger());

                var rootImage = store.ReadPage(rootPage);
                var root = SqliteTableInteriorPageView.Parse(rootImage, header.UsableSpace);
                root.Cells.Should().NotBeEmpty();
                var nextChildPage = root.Cells.Count == 1
                    ? root.Header.RightMostChildPage
                    : root.Cells[1].Cell.LeftChildPage;
                var nextChild = SqliteTableLeafPageView.Parse(
                    store.ReadPage(nextChildPage),
                    header.UsableSpace);
                nextChild.Cells.Should().NotBeEmpty();

                var separator = root.Cells[0];
                var nextMinimumRowId = nextChild.Cells[0].Cell.RowId;
                SqliteVarint.GetLength(unchecked((ulong)nextMinimumRowId))
                    .Should()
                    .Be(separator.Cell.EncodedLength - sizeof(uint));
                SqliteVarint.Write(
                    unchecked((ulong)nextMinimumRowId),
                    rootImage.AsSpan(separator.Offset + sizeof(uint)));
                store.WritePage(rootPage, rootImage);
                store.Flush();
            }

            fileSystem.DeleteFile(path + "-wal");
            using (SqliteWalFile.Create(
                       fileSystem,
                       path + "-wal",
                       SqliteWalHeader.Create(header.PageSize, salt1: 3, salt2: 5)))
            {
            }

            var reopen = () => EmbeddedDatabase.OpenFile(path, fileSystem);
            reopen.Should().Throw<EmbeddedSqlException>().WithMessage("*separator*is not below minimum rowid*");
        }

        [Test]
        public void ReopenRejectsNestedSeparatorThatOverlapsFollowingSubtree()
        {
            var path = CreateDatabasePath();
            try
            {
                using (var sqlite = new MsData.SqliteConnection($"Data Source={path}"))
                {
                    sqlite.Open();
                    using var command = sqlite.CreateCommand();
                    command.CommandText =
                        """
                        PRAGMA journal_mode=DELETE;
                        PRAGMA page_size=512;
                        VACUUM;
                        CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT NOT NULL);
                        """;
                    command.ExecuteNonQuery();
                    command.CommandText = BuildInsert(1, 2_000);
                    command.ExecuteNonQuery();
                }

                MsData.SqliteConnection.ClearAllPools();
                using (var store = SqlitePageStore.Open(PhysicalFileSystem.Instance, path))
                {
                    var header = store.Header;
                    var schema = SqliteTableLeafPageView.Parse(
                        store.ReadPage(1),
                        header.UsableSpace,
                        isFirstPage: true);
                    var rootPage = checked((uint)schema.Cells
                        .Select(cell => SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding))
                        .Single(values => values[0].AsText() == "table" && values[1].AsText() == "t")[3]
                        .AsInteger());

                    var rootImage = store.ReadPage(rootPage);
                    var root = SqliteTableInteriorPageView.Parse(rootImage, header.UsableSpace);
                    root.Cells.Should().NotBeEmpty();
                    var nextChildPage = root.Cells.Count == 1
                        ? root.Header.RightMostChildPage
                        : root.Cells[1].Cell.LeftChildPage;
                    SqliteBtreePageHeader.Parse(store.ReadPage(nextChildPage)).PageType
                        .Should()
                        .Be(SqliteBtreePageType.TableInterior);

                    var nextMinimumRowId = ReadSubtreeMinimumRowId(
                        store,
                        header,
                        nextChildPage);
                    var separator = root.Cells[0];
                    SqliteVarint.GetLength(unchecked((ulong)nextMinimumRowId))
                        .Should()
                        .Be(separator.Cell.EncodedLength - sizeof(uint));
                    SqliteVarint.Write(
                        unchecked((ulong)nextMinimumRowId),
                        rootImage.AsSpan(separator.Offset + sizeof(uint)));
                    store.WritePage(rootPage, rootImage);
                    store.Flush();
                }

                using (var sqlite = new MsData.SqliteConnection($"Data Source={path}"))
                {
                    sqlite.Open();
                    using var quickCheck = sqlite.CreateCommand();
                    quickCheck.CommandText = "PRAGMA quick_check;";
                    Convert.ToString(quickCheck.ExecuteScalar()).Should().NotBe("ok");
                }

                MsData.SqliteConnection.ClearAllPools();
                var reopen = () => EmbeddedDatabase.OpenFile(path);
                reopen.Should().Throw<EmbeddedSqlException>().WithMessage("*separator*is not below minimum rowid*");
            }
            finally
            {
                MsData.SqliteConnection.ClearAllPools();
                DeleteDatabase(path);
            }
        }

        [Test]
        public void OpensExternalSqliteTableInteriorWithStaleSeparatorsAfterDeletes()
        {
            // Real SQLite leaves parent separators unchanged when the left child's
            // maximum rowid is deleted. RDM Connections.db hits this shape; managed
            // open must accept separator >= left-child max, not require equality.
            var path = CreateDatabasePath();
            try
            {
                CreateExternalTableWithStaleSeparators(path);
                MsData.SqliteConnection.ClearAllPools();

                _ = FindStaleSeparatorRowId(path);

                using (var database = EmbeddedDatabase.OpenFile(path))
                using (var connection = database.Connect())
                {
                    var rows = Query(connection, "SELECT id FROM t ORDER BY id;");
                    rows.Should().NotBeEmpty();
                    rows.Select(row => row[0].AsInteger())
                        .Should()
                        .Equal(LoadSqliteRowIds(path));
                }

                using var facade = new Ahtola.Data.Sqlite.SqliteConnection(
                    $"Data Source={path};Mode=ReadOnly;Pooling=False");
                facade.Open();
                using var select = facade.CreateCommand();
                select.CommandText = "SELECT COUNT(*) FROM t;";
                Convert.ToInt64(select.ExecuteScalar()).Should().Be(LoadSqliteRowIds(path).Count);
            }
            finally
            {
                MsData.SqliteConnection.ClearAllPools();
                DeleteDatabase(path);
            }
        }

        [Test]
        public void InsertsAtExternalStaleSeparatorAndRemainsSqliteCompatible()
        {
            var path = CreateDatabasePath();
            try
            {
                CreateExternalTableWithStaleSeparators(path);
                MsData.SqliteConnection.ClearAllPools();
                var staleSeparatorRowId = FindStaleSeparatorRowId(path);

                using (var database = EmbeddedDatabase.OpenFile(path))
                using (var connection = database.Connect())
                {
                    Execute(
                        connection,
                        $"INSERT INTO t VALUES ({staleSeparatorRowId}, 'restored-stale-separator');");
                    Query(connection, $"SELECT value FROM t WHERE id = {staleSeparatorRowId};")
                        .Should()
                        .ContainSingle()
                        .Which[0]
                        .AsText()
                        .Should()
                        .Be("restored-stale-separator");
                }

                using var sqlite = new MsData.SqliteConnection($"Data Source={path}");
                sqlite.Open();
                using (var quickCheck = sqlite.CreateCommand())
                {
                    quickCheck.CommandText = "PRAGMA quick_check;";
                    quickCheck.ExecuteScalar().Should().Be("ok");
                }

                using var select = sqlite.CreateCommand();
                select.CommandText = $"SELECT value FROM t WHERE id = {staleSeparatorRowId};";
                select.ExecuteScalar().Should().Be("restored-stale-separator");
            }
            finally
            {
                MsData.SqliteConnection.ClearAllPools();
                DeleteDatabase(path);
            }
        }

        private static void CreateExternalTableWithStaleSeparators(string path)
        {
            using var sqlite = new MsData.SqliteConnection($"Data Source={path}");
            sqlite.Open();
            using var command = sqlite.CreateCommand();
            command.CommandText =
                """
                PRAGMA journal_mode=DELETE;
                CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT NOT NULL);
                """;
            command.ExecuteNonQuery();

            command.CommandText = BuildInsert(1, 200);
            command.ExecuteNonQuery();

            // Do not VACUUM: rebuild would tighten separators. Stale parent keys
            // after deleting left-child maxima are what production RDM files keep.
            command.CommandText =
                """
                DELETE FROM t WHERE id % 5 = 0;
                DELETE FROM t WHERE id IN (1, 2, 3, 50, 51, 99, 100, 150, 199, 200);
                """;
            command.ExecuteNonQuery();
        }

        private static long FindStaleSeparatorRowId(string path)
        {
            using var store = SqlitePageStore.Open(PhysicalFileSystem.Instance, path, readOnly: true);
            var header = store.Header;
            var schema = SqliteTableLeafPageView.Parse(
                store.ReadPage(1),
                header.UsableSpace,
                isFirstPage: true);
            var rootPage = checked((uint)schema.Cells
                .Select(cell => SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding))
                .Single(values => values[0].AsText() == "table" && values[1].AsText() == "t")[3]
                .AsInteger());
            var rootHeader = SqliteBtreePageHeader.Parse(store.ReadPage(rootPage));
            rootHeader.PageType.Should().Be(SqliteBtreePageType.TableInterior);

            long? staleSeparatorRowId;
            SubtreeMaximumRowId(
                    store,
                    header,
                    rootPage,
                    out staleSeparatorRowId)
                .Should()
                .NotBeNull();
            staleSeparatorRowId.Should().NotBeNull(
                "the fixture must keep at least one stale interior separator");
            return staleSeparatorRowId.GetValueOrDefault();
        }

        private static long? SubtreeMaximumRowId(
            SqlitePageStore store,
            SqliteDatabaseHeader header,
            uint pageNumber,
            out long? staleSeparatorRowId)
        {
            staleSeparatorRowId = null;
            var page = store.ReadPage(pageNumber);
            var pageHeader = SqliteBtreePageHeader.Parse(page);
            if (pageHeader.PageType == SqliteBtreePageType.TableLeaf)
            {
                var leaf = SqliteTableLeafPageView.Parse(page, header.UsableSpace);
                return leaf.Cells.Count == 0 ? null : leaf.Cells[^1].Cell.RowId;
            }

            pageHeader.PageType.Should().Be(SqliteBtreePageType.TableInterior);
            var interior = SqliteTableInteriorPageView.Parse(page, header.UsableSpace);
            long? maximum = null;
            for (var index = 0; index <= interior.Cells.Count; index++)
            {
                var childPage = index == interior.Cells.Count
                    ? interior.Header.RightMostChildPage
                    : interior.Cells[index].Cell.LeftChildPage;
                var childMax = SubtreeMaximumRowId(
                    store,
                    header,
                    childPage,
                    out var childStaleSeparatorRowId);
                staleSeparatorRowId ??= childStaleSeparatorRowId;
                if (childMax is { } value)
                    maximum = value;
                if (index < interior.Cells.Count
                    && childMax is { } leftMax
                    && leftMax < interior.Cells[index].Cell.RowId)
                {
                    staleSeparatorRowId ??= interior.Cells[index].Cell.RowId;
                }
            }

            return maximum;
        }

        private static long ReadSubtreeMinimumRowId(
            SqlitePageStore store,
            SqliteDatabaseHeader header,
            uint pageNumber)
        {
            var page = store.ReadPage(pageNumber);
            return SqliteBtreePageHeader.Parse(page).PageType switch
            {
                SqliteBtreePageType.TableLeaf => SqliteTableLeafPageView
                    .Parse(page, header.UsableSpace)
                    .Cells[0]
                    .Cell
                    .RowId,
                SqliteBtreePageType.TableInterior => ReadSubtreeMinimumRowId(
                    store,
                    header,
                    SqliteTableInteriorPageView
                        .Parse(page, header.UsableSpace)
                        .Cells[0]
                        .Cell
                        .LeftChildPage),
                var pageType => throw new InvalidDataException(
                    $"Expected a table b-tree page, found {pageType}."),
            };
        }

        private static List<long> LoadSqliteRowIds(string path)
        {
            using var sqlite = new MsData.SqliteConnection($"Data Source={path}");
            sqlite.Open();
            using var command = sqlite.CreateCommand();
            command.CommandText = "SELECT id FROM t ORDER BY id;";
            using var reader = command.ExecuteReader();
            var ids = new List<long>();
            while (reader.Read())
                ids.Add(reader.GetInt64(0));
            return ids;
        }

    private static string BuildInsert(int firstId, int lastId)
    {
        var rows = Enumerable.Range(firstId, lastId - firstId + 1)
            .Select(id => $"({id}, 'row-{id:D3}-{new string('x', 96)}')");
        return $"INSERT INTO t VALUES {string.Join(", ", rows)};";
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
        var directory = Path.Combine(AppContext.BaseDirectory, "managed-table-interior-file-store-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"table-{Guid.NewGuid():N}.db");
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
