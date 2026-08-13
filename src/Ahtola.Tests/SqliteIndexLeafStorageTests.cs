using System.Buffers.Binary;
using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public class SqliteIndexLeafStorageTests
{
    [Test]
    public void IndexLeafCellUsesSqliteIndexPayloadLayoutAndRoundTripsOverflowPointer()
    {
        const int usableSpace = SqlitePageSize.Minimum - 16;
        var payload = Enumerable.Range(0, 1_000).Select(value => unchecked((byte)value)).ToArray();
        var layout = SqlitePayloadLayout.Calculate(SqliteBtreePageType.IndexLeaf, (ulong)payload.Length, usableSpace);

        layout.UsesOverflow.Should().BeTrue();
        var cell = SqliteIndexLeafCell.Create(
            (ulong)payload.Length,
            payload.AsSpan(..layout.LocalPayloadLength),
            firstOverflowPage: 0x0102_0304,
            usableSpace);
        var decoded = SqliteIndexLeafCell.Decode(cell.ToArray(), usableSpace);

        decoded.PayloadLength.Should().Be((ulong)payload.Length);
        decoded.LocalPayload.ToArray().Should().Equal(payload[..layout.LocalPayloadLength]);
        decoded.FirstOverflowPage.Should().Be(0x0102_0304U);
        cell.ToArray()[^sizeof(uint)..].Should().Equal(1, 2, 3, 4);
        Assert.Throws<InvalidDataException>(() => SqliteIndexLeafCell.Decode(cell.ToArray()[..^1], usableSpace));
    }

    // SQLite has no NaN: sqlite3VdbeMemSetDouble refuses to store one and serialGet reads a stored
    // NaN back as NULL, so a record carrying a NaN payload sorts as NULL rather than being rejected.
    [Test]
    public void IndexRecordsCarryingANaNPayloadDecodeAsNullLikeSqlite()
    {
        var payload = BitConverter.GetBytes(double.NaN);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(payload);

        // A one-column record: a header of two bytes whose only serial type is 7 (IEEE 754 double).
        var record = new byte[] { 0x02, 0x07 }.Concat(payload).ToArray();

        var decoded = SqliteRecordCodec.Decode(record, SqliteTextEncoding.Utf8);
        decoded.Should().ContainSingle().Which.Kind.Should().Be(SqlValueKind.Null);

        var comparer = new SqliteIndexRecordComparer();
        comparer.Invoking(c => c.Validate(record)).Should().NotThrow();
        comparer.Compare(record, Record(SqlValue.Integer(-1))).Should().BeLessThan(0);
    }

    [Test]
    public void IndexRecordComparerUsesSqliteStorageClassAndBinaryOrdering()
    {
        var comparer = new SqliteIndexRecordComparer();

        comparer.Compare(Record(SqlValue.Null), Record(SqlValue.Integer(-1))).Should().BeLessThan(0);
        comparer.Compare(Record(SqlValue.Integer(2)), Record(SqlValue.Real(2))).Should().Be(0);
        comparer.Compare(Record(SqlValue.Real(-1.5)), Record(SqlValue.Integer(-1))).Should().BeLessThan(0);
        comparer.Compare(
            Record(SqlValue.Integer(long.MaxValue)),
            Record(SqlValue.Real((double)long.MaxValue))).Should().BeLessThan(0);
        comparer.Compare(Record(SqlValue.Text("B")), Record(SqlValue.Text("a"))).Should().BeLessThan(0);
        comparer.Compare(Record(SqlValue.Text("z")), Record(SqlValue.Blob([0]))).Should().BeLessThan(0);
        comparer.Compare(
            Record(SqlValue.Integer(1), SqlValue.Text("z")),
            Record(SqlValue.Integer(2))).Should().BeLessThan(0);
    }

    [Test]
    public void IndexRecordComparerAppliesBuiltInCollationsAndDirectionsPerTerm()
    {
        var comparer = new SqliteIndexRecordComparer(
            SqliteTextEncoding.Utf8,
            [true, false, false],
            ["NOCASE", "RTRIM", "BINARY"]);

        comparer.Compare(
            Record(SqlValue.Text("a"), SqlValue.Text("x "), SqlValue.Integer(1), SqlValue.Integer(5)),
            Record(SqlValue.Text("B"), SqlValue.Text("x"), SqlValue.Integer(1), SqlValue.Integer(2)))
            .Should().BeGreaterThan(0);
        comparer.Compare(
            Record(SqlValue.Text("A"), SqlValue.Text("x "), SqlValue.Integer(1), SqlValue.Integer(1)),
            Record(SqlValue.Text("a"), SqlValue.Text("x"), SqlValue.Integer(1), SqlValue.Integer(2)))
            .Should().BeLessThan(0);
        comparer.Compare(
            Record(SqlValue.Text("a\0c"), SqlValue.Text("x"), SqlValue.Integer(1), SqlValue.Integer(1)),
            Record(SqlValue.Text("A\0b"), SqlValue.Text("x"), SqlValue.Integer(1), SqlValue.Integer(2)))
            .Should().BeLessThan(0);
        comparer.Compare(
            Record(SqlValue.Text("Ä"), SqlValue.Text("x"), SqlValue.Integer(1)),
            Record(SqlValue.Text("ä"), SqlValue.Text("x"), SqlValue.Integer(1)))
            .Should().NotBe(0);

        var utf16RTrim = new SqliteIndexRecordComparer(
            SqliteTextEncoding.Utf16LittleEndian,
            [false],
            ["RTRIM"]);
        utf16RTrim.Compare(
            SqliteRecordCodec.Encode(
                [SqlValue.Text("ÿ")],
                SqliteTextEncoding.Utf16LittleEndian),
            SqliteRecordCodec.Encode(
                [SqlValue.Text("Ā")],
                SqliteTextEncoding.Utf16LittleEndian))
            .Should().BeLessThan(0);

        Assert.Throws<NotSupportedException>(() => new SqliteIndexRecordComparer(
            SqliteTextEncoding.Utf8,
            [false],
            ["custom"]));
    }

    [Test]
    public void IndexLeafPageBuilderPacksOrderedCellsAndPreservesReservedBytes()
    {
        const int pageSize = SqlitePageSize.Minimum;
        const int reservedSpace = 16;
        var usableSpace = pageSize - reservedSpace;
        var records = new[]
        {
            Record(SqlValue.Null),
            Record(SqlValue.Integer(1)),
            Record(SqlValue.Text("A")),
            Record(SqlValue.Blob([0x01])),
        };
        var builder = new SqliteIndexLeafPageBuilder(pageSize, usableSpace);
        foreach (var record in records)
            builder.Append(SqliteIndexLeafCell.Create(record, usableSpace));

        var page = new byte[pageSize];
        page.AsSpan(usableSpace).Fill(0xE1);
        builder.WriteTo(page);

        var view = SqliteIndexLeafPageView.Parse(page, usableSpace);
        view.Header.PageType.Should().Be(SqliteBtreePageType.IndexLeaf);
        view.HasVerifiedRecordOrdering.Should().BeTrue();
        view.Cells.Select(cell => cell.Cell.LocalPayload.ToArray()).Should().BeEquivalentTo(records, options => options.WithStrictOrdering());
        view.CellPointers[0].Should().BeLessThan(view.CellPointers[1]);
        page.AsSpan(usableSpace).ToArray().Should().OnlyContain(value => value == 0xE1);

        Assert.Throws<ArgumentException>(() =>
            builder.Append(SqliteIndexLeafCell.Create(Record(SqlValue.Integer(1)), usableSpace)));
        Assert.Throws<ArgumentException>(() =>
            new SqliteIndexLeafPageBuilder(pageSize, usableSpace, isFirstPage: true));
    }

    [Test]
    public void IndexLeafViewRejectsUnorderedAndTruncatedCells()
    {
        const int usableSpace = SqlitePageSize.Minimum;
        var first = Record(SqlValue.Integer(1));
        var second = Record(SqlValue.Integer(2));
        var builder = new SqliteIndexLeafPageBuilder(SqlitePageSize.Minimum, usableSpace);
        builder.Append(SqliteIndexLeafCell.Create(first, usableSpace));
        builder.Append(SqliteIndexLeafCell.Create(second, usableSpace));
        var page = builder.Build();
        var header = SqliteBtreePageHeader.Parse(page);
        var firstOffset = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(header.CellPointerArrayOffset));
        var secondOffset = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(header.CellPointerArrayOffset + sizeof(ushort)));

        BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(header.CellPointerArrayOffset), secondOffset);
        BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(header.CellPointerArrayOffset + sizeof(ushort)), firstOffset);
        Assert.Throws<InvalidDataException>(() => SqliteIndexLeafPageView.Parse(page, usableSpace));

        BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(header.CellPointerArrayOffset), (ushort)(usableSpace - 1));
        Assert.Throws<InvalidDataException>(() => SqliteIndexLeafPageView.Parse(page, usableSpace));
    }

    [Test]
    public void IndexLeafMutationWritesWalAndReadsBackOverflowRecord()
    {
        var fileSystem = new InMemoryFileSystem();
        var header = SqliteDatabaseHeader.CreateDefault() with
        {
            PageSize = SqlitePageSize.Minimum,
            ReservedSpace = 16,
        };
        using var store = SqlitePageStore.Create(fileSystem, "index.db", header);
        var writer = new SqliteIndexLeafMutationWriter(store, new SqliteAppendOnlyPageAllocator(store));
        var first = Record(SqlValue.Integer(1));
        var second = Record(
            SqlValue.Integer(2),
            SqlValue.Blob(Enumerable.Range(0, 1_000).Select(value => unchecked((byte)value)).ToArray()));

        var mutation = writer.CreatePage(
        [
            new SqliteIndexLeafCellInput(first),
            new SqliteIndexLeafCellInput(second),
        ]);

        mutation.IndexLeafPageNumber.Should().Be(2);
        mutation.OverflowPages.Should().NotBeEmpty();
        var walHeader = SqliteWalHeader.Create(
            store.PageSize,
            salt1: 0x0102_0304,
            salt2: 0x0506_0708,
            checkpointSequence: 1);
        using var wal = SqliteWalFile.Create(fileSystem, "index.db-wal", walHeader);
        mutation.AppendToWal(wal).Should().Be(mutation.OverflowPages.Count + 1);
        wal.ScanRecovery().LastCommittedDatabaseSizeInPages.Should().Be(mutation.TargetDatabaseSizeInPages);

        mutation.ApplyTo(store);
        var reader = new SqliteOverflowChainReader(store);
        SqliteIndexLeafPageView.Parse(
                store.ReadPage(mutation.IndexLeafPageNumber),
                store.Header.UsableSpace,
                store.Header.TextEncoding)
            .HasVerifiedRecordOrdering
            .Should()
            .BeFalse();
        var view = SqliteIndexLeafPageView.Parse(
            store.ReadPage(mutation.IndexLeafPageNumber),
            store.Header.UsableSpace,
            store.Header.TextEncoding,
            overflowReader: reader);
        view.HasVerifiedRecordOrdering.Should().BeTrue();
        reader.ReadPayload(view.Cells[0].Cell).Should().Equal(first);
        reader.ReadPayload(view.Cells[1].Cell).Should().Equal(second);
    }

    [Test]
    public void IndexLeafMutationRejectsUnorderedRecordsAndUnsafeOverflowRewriteBeforeAllocation()
    {
        var fileSystem = new InMemoryFileSystem();
        using var store = SqlitePageStore.Create(fileSystem, "index.db");
        var allocator = new SqliteAppendOnlyPageAllocator(store);
        var writer = new SqliteIndexLeafMutationWriter(store, allocator);

        Assert.Throws<ArgumentException>(() => writer.CreatePage(
        [
            new SqliteIndexLeafCellInput(Record(SqlValue.Integer(2))),
            new SqliteIndexLeafCellInput(Record(SqlValue.Integer(1))),
        ]));
        allocator.NextPageNumber.Should().Be(2);

        var large = Record(
            SqlValue.Integer(1),
            SqlValue.Blob(Enumerable.Range(0, 1_000).Select(value => unchecked((byte)value)).ToArray()));
        var created = writer.CreatePage([new SqliteIndexLeafCellInput(large)]);
        created.ApplyTo(store);
        var nextPageNumber = allocator.NextPageNumber;

        Assert.Throws<NotSupportedException>(() =>
            writer.RewritePage(created.IndexLeafPageNumber, [new SqliteIndexLeafCellInput(Record(SqlValue.Integer(3)))]));
        allocator.NextPageNumber.Should().Be(nextPageNumber);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            writer.RewritePage(1, [new SqliteIndexLeafCellInput(Record(SqlValue.Integer(3)))]));
    }

    [Test]
    public void IndexLeafMutationRewritesFullyLocalPageAndRejectsCorruptionBeforeAllocation()
    {
        var fileSystem = new InMemoryFileSystem();
        var header = SqliteDatabaseHeader.CreateDefault() with
        {
            PageSize = SqlitePageSize.Minimum,
            ReservedSpace = 16,
        };
        using var store = SqlitePageStore.Create(fileSystem, "rewrite.db", header);
        var allocator = new SqliteAppendOnlyPageAllocator(store);
        var writer = new SqliteIndexLeafMutationWriter(store, allocator);
        var created = writer.CreatePage([new SqliteIndexLeafCellInput(Record(SqlValue.Integer(1)))]);
        created.ApplyTo(store);
        var existing = store.ReadPage(created.IndexLeafPageNumber);
        existing.AsSpan(store.Header.UsableSpace).Fill(0xD1);
        store.WritePage(created.IndexLeafPageNumber, existing);

        var rewritten = writer.RewritePage(
            created.IndexLeafPageNumber,
            [new SqliteIndexLeafCellInput(Record(SqlValue.Integer(2)))]);
        rewritten.TargetDatabaseSizeInPages.Should().Be(store.PageCount);
        rewritten.ApplyTo(store);
        SqliteIndexLeafPageView.Parse(
                store.ReadPage(created.IndexLeafPageNumber),
                store.Header.UsableSpace,
                store.Header.TextEncoding)
            .Cells
            .Single()
            .Cell
            .LocalPayload
            .ToArray()
            .Should()
            .Equal(Record(SqlValue.Integer(2)));
        store.ReadPage(created.IndexLeafPageNumber)
            .AsSpan(store.Header.UsableSpace)
            .ToArray()
            .Should()
            .OnlyContain(value => value == 0xD1);

        var corrupt = store.ReadPage(created.IndexLeafPageNumber);
        var btreeHeader = SqliteBtreePageHeader.Parse(corrupt);
        BinaryPrimitives.WriteUInt16BigEndian(
            corrupt.AsSpan(btreeHeader.CellPointerArrayOffset),
            (ushort)store.Header.UsableSpace);
        store.WritePage(created.IndexLeafPageNumber, corrupt);
        var nextPageNumber = allocator.NextPageNumber;

        Assert.Throws<InvalidDataException>(() =>
            writer.RewritePage(created.IndexLeafPageNumber, [new SqliteIndexLeafCellInput(Record(SqlValue.Integer(3)))]));
        allocator.NextPageNumber.Should().Be(nextPageNumber);
    }

    [Test]
    public void IndexLeafMutationWalAndStoreFaultsLeaveNoCommittedMutationAndCanBeRetried()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        using var store = SqlitePageStore.Create(fileSystem, "fault.db");
        var writer = new SqliteIndexLeafMutationWriter(store, new SqliteAppendOnlyPageAllocator(store));
        var record = Record(
            SqlValue.Integer(1),
            SqlValue.Blob(Enumerable.Range(0, 1_000).Select(value => unchecked((byte)value)).ToArray()));
        var mutation = writer.CreatePage([new SqliteIndexLeafCellInput(record)]);

        var walHeader = SqliteWalHeader.Create(
            store.PageSize,
            salt1: 0x1111_2222,
            salt2: 0x3333_4444,
            checkpointSequence: 1);
        using var wal = SqliteWalFile.Create(fileSystem, "fault.db-wal", walHeader);
        faults.FailOnOccurrence(FileSystemOperation.Write, faults.GetOperationCount(FileSystemOperation.Write) + 2);
        Assert.Throws<IOException>(() => mutation.AppendToWal(wal));
        wal.ScanRecovery().LastCommittedFrameNumber.Should().Be(0);
        store.PageCount.Should().Be(1);

        faults.FailNext(FileSystemOperation.Write);
        Assert.Throws<IOException>(() => mutation.ApplyTo(store));
        store.PageCount.Should().Be(1);

        mutation.ApplyTo(store);
        var reader = new SqliteOverflowChainReader(store);
        var view = SqliteIndexLeafPageView.Parse(
            store.ReadPage(mutation.IndexLeafPageNumber),
            store.Header.UsableSpace,
            store.Header.TextEncoding,
            overflowReader: reader);
        reader.ReadPayload(view.Cells.Single().Cell).Should().Equal(record);
    }

    private static byte[] Record(params SqlValue[] values) => SqliteRecordCodec.Encode(values);
}
