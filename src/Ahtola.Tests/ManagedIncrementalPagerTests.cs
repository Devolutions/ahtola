using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

/// <summary>
/// Covers the incremental pager: a mutation must cost the pages it touches
/// rather than the size of the database, and a point read must cost the height
/// of the tree rather than the size of the database.
/// </summary>
[NonParallelizable]
public sealed class ManagedIncrementalPagerTests
{
    private const int PayloadLength = 120;

    [Test]
    public void SingleRowInsertPageCostDoesNotGrowWithTableSize()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "incremental-pager-write-cost.db";

        var reads = new List<long>();
        var writes = new List<long>();
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, payload TEXT);");
            for (var id = 1; id <= 400; id++)
            {
                var readsBefore = faults.GetOperationCount(FileSystemOperation.Read);
                var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
                Execute(connection, InsertStatement(id));
                reads.Add(faults.GetOperationCount(FileSystemOperation.Read) - readsBefore);
                writes.Add(faults.GetOperationCount(FileSystemOperation.Write) - writesBefore);
            }
        }

        var earlyReads = reads.Skip(20).Take(60).Average();
        var lateReads = reads.Skip(340).Take(60).Average();
        var earlyWrites = writes.Skip(20).Take(60).Average();
        var lateWrites = writes.Skip(340).Take(60).Average();
        TestContext.Out.WriteLine(
            $"reads/insert early(21-80)={earlyReads:F2} late(341-400)={lateReads:F2} max={reads.Max()}");
        TestContext.Out.WriteLine(
            $"writes/insert early(21-80)={earlyWrites:F2} late(341-400)={lateWrites:F2} max={writes.Max()}");

        // Rebuilding the catalog re-reads every page, so the read cost of one
        // insert used to grow with the table: 45 reads at row 50 against 2,069
        // at row 370. A cursor only reads its search path, so the cost has to
        // stay inside a small constant band.
        lateReads.Should().BeLessThan(earlyReads * 2);
        reads.Max().Should().BeLessThan(80);
        lateWrites.Should().BeLessThan(earlyWrites * 2);
        writes.Max().Should().BeLessThan(40);
    }

    [Test]
    public void SequentialInsertsPackPagesComparablyToSqlite()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "incremental-pager-page-count.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, payload TEXT);");
            for (var id = 1; id <= 400; id++)
                Execute(connection, InsertStatement(id));
        }

        var managedPages = PageCount(fileSystem, path);
        var sqlitePages = SqlitePageCountForSameRows(400);
        TestContext.Out.WriteLine($"managed pages={managedPages} sqlite pages={sqlitePages}");

        // Bounded in-place mutation never repacked, so these 400 rows used to
        // occupy 372 pages. Cursor-driven splitting has to stay close to what
        // SQLite itself produces for the same rows.
        managedPages.Should().BeLessThan(checked((uint)(3 * sqlitePages)));
    }

    [Test]
    public void PointLookupReadsOnlyTheSearchPath()
    {
        var pageIo = new CountingPageIo(pageSize: 4096, usableSpace: 4096, initialPageCount: 2);
        pageIo.WritePage(2u, SqliteTableLeafPageBuilderImage(pageIo));

        var writer = new SqliteIncrementalTableBtree(pageIo);
        var records = new Dictionary<long, byte[]>();
        for (var rowId = 1L; rowId <= 4000; rowId++)
        {
            var record = Record(rowId);
            records[rowId] = record;
            writer.Insert(2, rowId, record);
        }

        // A tree this large cannot be resident in any bounded cache, so a read
        // that only touches the search path is the property under test.
        pageIo.PageCount.Should().BeGreaterThan(100);

        var cursor = new SqliteTableBtreeCursor(pageIo);
        foreach (var rowId in new long[] { 1, 7, 1234, 2500, 4000 })
        {
            pageIo.ResetReadCount();
            cursor.TrySeek(2, rowId, out var record).Should().BeTrue();
            record.Should().Equal(records[rowId]);
            TestContext.Out.WriteLine($"rowid {rowId}: {pageIo.ReadCount} page reads of {pageIo.PageCount}");
            pageIo.ReadCount.Should().BeLessThanOrEqualTo(4);
        }

        pageIo.ResetReadCount();
        cursor.TrySeek(2, 99999, out _).Should().BeFalse();
        pageIo.ReadCount.Should().BeLessThanOrEqualTo(4);
    }

    [Test]
    public void PointLookupReadsBoundedPayloadRangesAcrossOverflowBoundaries()
    {
        const int payloadLength = 1024 * 1024;
        var pageIo = new CountingPageIo(pageSize: 4096, usableSpace: 4096, initialPageCount: 2);
        pageIo.WritePage(2u, SqliteTableLeafPageBuilderImage(pageIo));
        var payload = Enumerable.Range(0, payloadLength)
            .Select(static value => unchecked((byte)(value * 31)))
            .ToArray();
        new SqliteIncrementalTableBtree(pageIo).Insert(2, 7, payload);

        var layout = SqlitePayloadLayout.Calculate(
            SqliteBtreePageType.TableLeaf,
            (ulong)payload.Length,
            pageIo.UsableSpace);
        var overflowCapacity = pageIo.UsableSpace - SqliteOverflowPageView.HeaderLength;
        var cursor = new SqliteTableBtreeCursor(pageIo);
        pageIo.ResetReadCount();
        cursor.TryGetPayloadLength(2, 7, out var storedLength).Should().BeTrue();
        storedLength.Should().Be((ulong)payload.Length);
        pageIo.ReadCount.Should().Be(1, "payload length comes from the table-leaf cell header");

        AssertPayloadRange(
            cursor,
            pageIo,
            payload,
            checked((ulong)(layout.LocalPayloadLength - 17)),
            length: 64,
            maximumPageReads: 2);
        AssertPayloadRange(
            cursor,
            pageIo,
            payload,
            checked((ulong)(layout.LocalPayloadLength + overflowCapacity - 17)),
            length: 64,
            maximumPageReads: 3);
    }

    [Test]
    public void BoundedPayloadReadDoesNotAllocateTheCompleteRecord()
    {
        const int payloadLength = 1024 * 1024;
        var pageIo = new CountingPageIo(pageSize: 4096, usableSpace: 4096, initialPageCount: 2);
        pageIo.WritePage(2u, SqliteTableLeafPageBuilderImage(pageIo));
        var payload = new byte[payloadLength];
        payload.AsSpan().Fill(0x5A);
        new SqliteIncrementalTableBtree(pageIo).Insert(2, 11, payload);

        var localLength = SqlitePayloadLayout.Calculate(
            SqliteBtreePageType.TableLeaf,
            (ulong)payload.Length,
            pageIo.UsableSpace).LocalPayloadLength;
        var offset = checked((ulong)(localLength - 8));
        var destination = new byte[32];
        var cursor = new SqliteTableBtreeCursor(pageIo);

        cursor.TryReadPayload(2, 11, offset, destination).Should().BeTrue();
        pageIo.ResetReadCount();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        var found = cursor.TryReadPayload(2, 11, offset, destination);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        found.Should().BeTrue();
        destination.Should().OnlyContain(static value => value == 0x5A);
        pageIo.ReadCount.Should().BeLessThanOrEqualTo(2);
        allocated.Should().BeLessThan(
            payloadLength / 8,
            "a bounded range read must allocate for touched pages, not the complete record");
    }

    [Test]
    public void ColumnReadCrossesLocalAndOverflowPayloadBoundaries()
    {
        var pageIo = new CountingPageIo(pageSize: 512, usableSpace: 512, initialPageCount: 2);
        pageIo.WritePage(2u, SqliteTableLeafPageBuilderImage(pageIo));
        var target = Enumerable.Range(0, 2048)
            .Select(static value => unchecked((byte)(value * 17)))
            .ToArray();
        byte[]? record = null;
        var targetOffset = 0;
        SqlitePayloadLayout? layout = null;
        for (var prefixLength = 0; prefixLength < 2048; prefixLength++)
        {
            var candidate = SqliteRecordCodec.Encode(
                [SqlValue.Blob(new byte[prefixLength]), SqlValue.Blob(target)]);
            var headerSize = candidate.Length - prefixLength - target.Length;
            var candidateLayout = SqlitePayloadLayout.Calculate(
                SqliteBtreePageType.TableLeaf,
                (ulong)candidate.Length,
                pageIo.UsableSpace);
            var candidateTargetOffset = headerSize + prefixLength;
            if (candidateTargetOffset < candidateLayout.LocalPayloadLength
                && candidateTargetOffset + target.Length > candidateLayout.LocalPayloadLength)
            {
                record = candidate;
                targetOffset = candidateTargetOffset;
                layout = candidateLayout;
                break;
            }
        }

        record.Should().NotBeNull("the fixture must place the target across the local/overflow boundary");
        new SqliteIncrementalTableBtree(pageIo).Insert(2, 17, record!);
        var cursor = new SqliteTableBtreeCursor(pageIo);
        cursor.TryGetColumnLocation(2, 17, 1, out var location).Should().BeTrue();
        location.PayloadOffset.Should().Be((ulong)targetOffset);
        location.Length.Should().Be((ulong)target.Length);
        location.StorageClass.Should().Be(SqliteRecordStorageClass.Blob);

        var start = checked((ulong)(layout!.LocalPayloadLength - targetOffset - 16));
        var destination = new byte[64];
        pageIo.ResetReadCount();
        cursor.TryReadColumn(2, 17, 1, start, destination, out var bytesRead).Should().BeTrue();

        bytesRead.Should().Be(destination.Length);
        destination.Should().Equal(target.AsSpan(checked((int)start), destination.Length).ToArray());
        pageIo.ReadCount.Should().BeLessThanOrEqualTo(2);
    }

    [Test]
    public void ColumnLocatorReadsAHeaderThatSpillsOntoOverflowPages()
    {
        var pageIo = new CountingPageIo(pageSize: 512, usableSpace: 512, initialPageCount: 2);
        pageIo.WritePage(2u, SqliteTableLeafPageBuilderImage(pageIo));
        var values = Enumerable.Repeat(SqlValue.Blob([]), 1000).ToList();
        var target = Enumerable.Range(0, 4096).Select(static value => unchecked((byte)value)).ToArray();
        values.Add(SqlValue.Blob(target));
        var record = SqliteRecordCodec.Encode(values);
        SqliteVarint.TryRead(record, out var headerSize, out _).Should().BeTrue();
        var layout = SqlitePayloadLayout.Calculate(
            SqliteBtreePageType.TableLeaf,
            (ulong)record.Length,
            pageIo.UsableSpace);
        headerSize.Should().BeGreaterThan((ulong)layout.LocalPayloadLength);
        new SqliteIncrementalTableBtree(pageIo).Insert(2, 23, record);

        var cursor = new SqliteTableBtreeCursor(pageIo);
        pageIo.ResetReadCount();
        cursor.TryGetColumnLength(2, 23, values.Count - 1, out var length).Should().BeTrue();
        length.Should().Be((ulong)target.Length);
        pageIo.ReadCount.Should().BeLessThanOrEqualTo(3, "only the leaf and spilled header pages are needed");

        var destination = new byte[48];
        cursor.TryReadColumn(2, 23, values.Count - 1, 4070, destination, out var bytesRead).Should().BeTrue();
        bytesRead.Should().Be(26);
        destination[..bytesRead].Should().Equal(target.AsSpan(4070).ToArray());
    }

    [Test]
    public void ColumnLocatorDoesNotMaterializeLargePrecedingBodies()
    {
        const int largeColumnLength = 1024 * 1024;
        var pageIo = new CountingPageIo(pageSize: 4096, usableSpace: 4096, initialPageCount: 2);
        pageIo.WritePage(2u, SqliteTableLeafPageBuilderImage(pageIo));
        var record = SqliteRecordCodec.Encode(
            [SqlValue.Blob(new byte[largeColumnLength]), SqlValue.Blob([1, 2, 3, 4])]);
        new SqliteIncrementalTableBtree(pageIo).Insert(2, 29, record);
        var cursor = new SqliteTableBtreeCursor(pageIo);

        cursor.TryGetColumnLocation(2, 29, 1, out _).Should().BeTrue();
        pageIo.ResetReadCount();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        cursor.TryGetColumnLocation(2, 29, 1, out var location).Should().BeTrue();

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        location.PayloadOffset.Should().Be((ulong)(record.Length - 4));
        location.Length.Should().Be(4);
        pageIo.ReadCount.Should().Be(1, "the preceding body is skipped using serial-type lengths");
        allocated.Should().BeLessThan(
            largeColumnLength / 8,
            "locating a column must allocate for the leaf/header, not preceding value bodies");
    }

    [Test]
    public void ColumnReadsRejectNonByteValuesAndMalformedHeaders()
    {
        var pageIo = new CountingPageIo(pageSize: 4096, usableSpace: 4096, initialPageCount: 2);
        pageIo.WritePage(2u, SqliteTableLeafPageBuilderImage(pageIo));
        var writer = new SqliteIncrementalTableBtree(pageIo);
        writer.Insert(2, 31, SqliteRecordCodec.Encode([SqlValue.Integer(42)]));
        writer.Insert(2, 32, [2, 10]);
        writer.Insert(2, 33, [2, 0x81]);
        var cursor = new SqliteTableBtreeCursor(pageIo);

        Assert.Throws<InvalidOperationException>(
            () => cursor.TryGetColumnLength(2, 31, 0, out _));
        Assert.Throws<InvalidDataException>(
            () => cursor.TryGetColumnLocation(2, 32, 0, out _));
        Assert.Throws<InvalidDataException>(
            () => cursor.TryGetColumnLocation(2, 33, 0, out _));
        Assert.Throws<InvalidDataException>(
            () => cursor.TryGetColumnLocation(2, 31, 1, out _));
    }

    [Test]
    public void IncrementalMutationsMatchSqliteAcrossIndexesOverflowAndDeletes()
    {
        var path = CreateDatabasePath("differential");
        var sqlitePath = path + ".sqlite";
        try
        {
            var statements = BuildDifferentialWorkload();
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                foreach (var sql in statements)
                    Execute(connection, sql);
            }

            using (var sqlite = new MsData.SqliteConnection($"Data Source={sqlitePath}"))
            {
                sqlite.Open();
                foreach (var sql in statements)
                {
                    using var command = sqlite.CreateCommand();
                    command.CommandText = sql;
                    command.ExecuteNonQuery();
                }
            }

            var queries = new[]
            {
                "SELECT id, code, payload FROM t ORDER BY id;",
                "SELECT id, code FROM t ORDER BY code, id;",
                "SELECT COUNT(*), SUM(id), SUM(LENGTH(payload)) FROM t;",
                "SELECT id FROM t WHERE code >= 'code-0100' AND code < 'code-0200' ORDER BY code;",
            };

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                foreach (var sql in queries)
                    QueryRowDigests(connection, sql).Should().Equal(SqliteQueryRowDigests(sqlitePath, sql), sql);
            }

            AssertSqliteIntegrityCheck(path);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
            DeleteDatabase(sqlitePath);
        }
    }

    [Test]
    public void DeletingALeafMaximumRepairsSeparatorsAndSurvivesReopen()
    {
        var path = CreateDatabasePath("separator");
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, payload TEXT);");
                for (var id = 1; id <= 200; id++)
                    Execute(connection, InsertStatement(id));

                // Deleting the largest rowid of an interior child exercises
                // separator tightening in the incremental mutation path.
                for (var id = 30; id <= 200; id += 30)
                    Execute(connection, $"DELETE FROM t WHERE id = {id};");
            }

            var expected = Enumerable.Range(1, 200).Where(id => id % 30 != 0).Select(id => (long)id).ToArray();
            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                QueryRows(connection, "SELECT id FROM t ORDER BY id;")
                    .Select(row => (long)row[0]!)
                    .Should()
                    .Equal(expected);
            }

            AssertSqliteIntegrityCheck(path);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    private static List<string> BuildDifferentialWorkload()
    {
        var statements = new List<string>
        {
            "CREATE TABLE t(id INTEGER PRIMARY KEY, code TEXT, payload TEXT);",
            "CREATE UNIQUE INDEX t_code ON t(code);",
            "CREATE INDEX t_payload ON t(payload);",
        };

        for (var id = 1; id <= 300; id++)
            statements.Add($"INSERT INTO t VALUES ({id}, 'code-{id:D4}', '{Payload(id)}');");

        for (var id = 5; id <= 300; id += 25)
            statements.Add($"UPDATE t SET payload = '{new string('u', 40)}' WHERE id = {id};");

        for (var id = 11; id <= 300; id += 37)
            statements.Add($"DELETE FROM t WHERE id = {id};");

        // A payload well past the local-payload threshold forces an overflow
        // chain through the incremental writer.
        statements.Add($"INSERT INTO t VALUES (5000, 'code-5000', '{new string('o', 9000)}');");
        statements.Add("INSERT INTO t VALUES (5001, 'code-5001', 'tail');");
        return statements;
    }

    private static string Payload(int id) => id % 7 == 0
        ? new string('y', PayloadLength * 2)
        : new string('x', PayloadLength);

    private static string InsertStatement(int id)
        => $"INSERT INTO t VALUES ({id}, '{new string('x', PayloadLength)}');";

    private static int SqlitePageCountForSameRows(int rowCount)
    {
        var path = CreateDatabasePath("sqlite-page-count");
        try
        {
            using var sqlite = new MsData.SqliteConnection($"Data Source={path}");
            sqlite.Open();
            using (var pageSize = sqlite.CreateCommand())
            {
                pageSize.CommandText = "PRAGMA page_size = 4096;";
                pageSize.ExecuteNonQuery();
            }

            using (var create = sqlite.CreateCommand())
            {
                create.CommandText = "CREATE TABLE t(id INTEGER PRIMARY KEY, payload TEXT);";
                create.ExecuteNonQuery();
            }

            for (var id = 1; id <= rowCount; id++)
            {
                using var insert = sqlite.CreateCommand();
                insert.CommandText = InsertStatement(id);
                insert.ExecuteNonQuery();
            }

            using var count = sqlite.CreateCommand();
            count.CommandText = "PRAGMA page_count;";
            return Convert.ToInt32(count.ExecuteScalar());
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    private static uint PageCount(IFileSystem fileSystem, string path)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        return SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1)).DatabaseSizeInPages;
    }

    private static byte[] SqliteTableLeafPageBuilderImage(ISqliteBtreePageIo pageIo)
    {
        var image = new byte[pageIo.PageSize];
        new SqliteTableLeafPageBuilder(pageIo.PageSize, pageIo.UsableSpace).WriteTo(image);
        return image;
    }

    private static byte[] Record(long rowId)
    {
        var record = new byte[rowId % 11 == 0 ? 900 : 60];
        for (var index = 0; index < record.Length; index++)
            record[index] = unchecked((byte)(rowId + index));

        return record;
    }

    private static void AssertPayloadRange(
        SqliteTableBtreeCursor cursor,
        CountingPageIo pageIo,
        byte[] payload,
        ulong offset,
        int length,
        int maximumPageReads)
    {
        var destination = new byte[length];
        pageIo.ResetReadCount();

        cursor.TryReadPayload(2, 7, offset, destination).Should().BeTrue();

        destination.Should().Equal(payload.AsSpan(checked((int)offset), length).ToArray());
        pageIo.ReadCount.Should().BeLessThanOrEqualTo(maximumPageReads);
    }

    private static void AssertSqliteIntegrityCheck(string path)
    {
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

    private static List<object?[]> QueryRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<object?[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var row = new object?[statement.ColumnCount];
            for (var index = 0; index < row.Length; index++)
                row[index] = Normalize(statement.GetValue(index));

            rows.Add(row);
        }

        return rows;
    }

    private static List<string> QueryRowDigests(EmbeddedConnection connection, string sql)
        => QueryRows(connection, sql).Select(Digest).ToList();

    private static List<string> SqliteQueryRowDigests(string path, string sql)
        => SqliteQueryRows(path, sql).Select(Digest).ToList();

    /// <summary>
    /// Renders a row so a mismatch reports the differing column compactly
    /// instead of dumping kilobytes of payload text.
    /// </summary>
    private static string Digest(object?[] row) => string.Join(
        " | ",
        row.Select(value => value switch
        {
            null => "NULL",
            string text when text.Length > 16
                => $"text[{text.Length}]#{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..16]}",
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
        }));

    private static List<object?[]> SqliteQueryRows(string path, string sql)
    {
        using var sqlite = new MsData.SqliteConnection($"Data Source={path}");
        sqlite.Open();
        using var command = sqlite.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var index = 0; index < row.Length; index++)
                row[index] = NormalizeSqlite(reader.GetValue(index));

            rows.Add(row);
        }

        return rows;
    }

    private static object? Normalize(SqlValue value) => value.Kind switch
    {
        SqlValueKind.Null => null,
        SqlValueKind.Integer => value.AsInteger(),
        SqlValueKind.Real => value.AsReal(),
        SqlValueKind.Text => value.AsText(),
        _ => Convert.ToBase64String(value.AsBlob().ToArray()),
    };

    private static object? NormalizeSqlite(object value) => value switch
    {
        DBNull => null,
        long integer => integer,
        double real => real,
        string text => text,
        byte[] blob => Convert.ToBase64String(blob),
        _ => value,
    };

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    private static string CreateDatabasePath(string name)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "managed-incremental-pager-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{name}-{Guid.NewGuid():N}.db");
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }

    /// <summary>
    /// An <see cref="ISqliteBtreePageIo"/> that counts page reads. The same
    /// decorator shape is where synthetic short writes, fsync failures, and
    /// ENOSPC belong for b-tree level fault injection.
    /// </summary>
    private sealed class CountingPageIo : ISqliteBtreePageIo
    {
        private readonly List<byte[]> _pages = [];

        public CountingPageIo(int pageSize, int usableSpace, int initialPageCount)
        {
            PageSize = pageSize;
            UsableSpace = usableSpace;
            for (var index = 0; index < initialPageCount; index++)
                _pages.Add(new byte[pageSize]);
        }

        public int PageSize { get; }

        public int UsableSpace { get; }

        public uint PageCount => (uint)_pages.Count;

        public int ReadCount { get; private set; }

        public void ResetReadCount() => ReadCount = 0;

        public byte[] ReadPage(uint pageNumber)
        {
            ReadCount++;
            return (byte[])_pages[checked((int)pageNumber) - 1].Clone();
        }

        public void WritePage(uint pageNumber, ReadOnlySpan<byte> image)
            => _pages[checked((int)pageNumber) - 1] = image.ToArray();

        public uint AllocatePage()
        {
            _pages.Add(new byte[PageSize]);
            return (uint)_pages.Count;
        }

        public void FreePage(uint pageNumber)
        {
            // Counting fixture only exercises growth; free leaves a zeroed hole.
            if (pageNumber < 2 || pageNumber > PageCount)
                throw new ArgumentOutOfRangeException(nameof(pageNumber));
            _pages[checked((int)pageNumber) - 1] = new byte[PageSize];
        }
    }
}
