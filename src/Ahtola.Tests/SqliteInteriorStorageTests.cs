using System.Buffers.Binary;
using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public class SqliteInteriorStorageTests
{
    [Test]
    public void TableInteriorCodecPreservesChildAndRowidOrder()
    {
        const int pageSize = SqlitePageSize.Minimum;
        const int usableSpace = pageSize - 16;
        var builder = new SqliteTableInteriorPageBuilder(pageSize, usableSpace, rightMostChildPage: 13);
        builder.Append(SqliteTableInteriorCell.Create(7, -10));
        builder.Append(SqliteTableInteriorCell.Create(8, 0));
        builder.Append(SqliteTableInteriorCell.Create(9, 100));

        var page = new byte[pageSize];
        page.AsSpan(usableSpace).Fill(0xE1);
        builder.WriteTo(page);

        var view = SqliteTableInteriorPageView.Parse(page, usableSpace);
        view.Header.PageType.Should().Be(SqliteBtreePageType.TableInterior);
        view.Header.RightMostChildPage.Should().Be(13);
        view.Cells.Select(cell => cell.Cell.LeftChildPage).Should().Equal(7, 8, 9);
        view.Cells.Select(cell => cell.Cell.RowId).Should().Equal(-10, 0, 100);
        BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(view.CellPointers[0])).Should().Be(7);
        BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(8)).Should().Be(13);
        view.CellPointers[0].Should().BeLessThan(view.CellPointers[1]);
        view.CellPointers[1].Should().BeLessThan(view.CellPointers[2]);
        page.AsSpan(usableSpace).ToArray().Should().OnlyContain(value => value == 0xE1);

        view.SearchChild(-10).Should().Be(new SqliteBtreeChildSearchResult(0, 7, true));
        view.SearchChild(1).Should().Be(new SqliteBtreeChildSearchResult(2, 9, false));
        view.SearchChild(101).Should().Be(new SqliteBtreeChildSearchResult(3, 13, false));
    }

    [Test]
    public void IndexInteriorCodecPreservesChildAndRecordOrder()
    {
        const int pageSize = SqlitePageSize.Minimum;
        const int usableSpace = pageSize - 16;
        var first = Record(SqlValue.Integer(1));
        var second = Record(SqlValue.Integer(5));
        var third = Record(SqlValue.Integer(9));
        var builder = new SqliteIndexInteriorPageBuilder(pageSize, usableSpace, rightMostChildPage: 30);
        builder.Append(SqliteIndexInteriorCell.Create(10, first, usableSpace));
        builder.Append(SqliteIndexInteriorCell.Create(20, second, usableSpace));
        builder.Append(SqliteIndexInteriorCell.Create(25, third, usableSpace));

        var page = new byte[pageSize];
        page.AsSpan(usableSpace).Fill(0xD1);
        builder.WriteTo(page);

        var view = SqliteIndexInteriorPageView.Parse(page, usableSpace);
        view.HasVerifiedRecordOrdering.Should().BeTrue();
        view.Cells.Select(cell => cell.Cell.LeftChildPage).Should().Equal(10, 20, 25);
        view.GetRecord(0).Should().Equal(first);
        view.GetRecord(1).Should().Equal(second);
        view.GetRecord(2).Should().Equal(third);
        BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(view.CellPointers[0])).Should().Be(10);
        BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(8)).Should().Be(30);
        page.AsSpan(usableSpace).ToArray().Should().OnlyContain(value => value == 0xD1);

        view.SearchChild(first).Should().Be(new SqliteBtreeChildSearchResult(0, 10, true));
        view.SearchChild(Record(SqlValue.Integer(6)))
            .Should()
            .Be(new SqliteBtreeChildSearchResult(2, 25, false));
        view.SearchChild(Record(SqlValue.Integer(10)))
            .Should()
            .Be(new SqliteBtreeChildSearchResult(3, 30, false));
    }

    [Test]
    public void IndexInteriorCellUsesIndexPayloadCodecAndRefusesPartialKeySearch()
    {
        const int usableSpace = SqlitePageSize.Minimum - 16;
        var record = Record(
            SqlValue.Integer(10),
            SqlValue.Blob(Enumerable.Range(0, 1_000).Select(value => unchecked((byte)value)).ToArray()));
        var layout = SqlitePayloadLayout.Calculate(
            SqliteBtreePageType.IndexInterior,
            (ulong)record.Length,
            usableSpace);
        var cell = SqliteIndexInteriorCell.Create(
            5,
            (ulong)record.Length,
            record.AsSpan(..layout.LocalPayloadLength),
            firstOverflowPage: 6,
            usableSpace);
        var decoded = SqliteIndexInteriorCell.Decode(cell.ToArray(), usableSpace);

        decoded.LeftChildPage.Should().Be(5);
        decoded.Key.PayloadLength.Should().Be((ulong)record.Length);
        decoded.Key.LocalPayload.ToArray().Should().Equal(record[..layout.LocalPayloadLength]);
        decoded.Key.FirstOverflowPage.Should().Be(6);
        cell.ToArray()[..sizeof(uint)].Should().Equal(0, 0, 0, 5);

        var builder = new SqliteIndexInteriorPageBuilder(SqlitePageSize.Minimum, usableSpace, 7);
        builder.Append(SqliteIndexInteriorCell.Create(4, Record(SqlValue.Integer(1)), usableSpace));
        builder.Append(cell, record);
        var view = SqliteIndexInteriorPageView.Parse(builder.Build(), usableSpace);
        view.HasVerifiedRecordOrdering.Should().BeFalse();
        Assert.Throws<InvalidOperationException>(() => view.SearchChild(record));
    }

    [Test]
    public void InteriorViewsRejectCorruptChildPointersAndOrdering()
    {
        const int usableSpace = SqlitePageSize.Minimum;
        var tableBuilder = new SqliteTableInteriorPageBuilder(SqlitePageSize.Minimum, usableSpace, 4);
        tableBuilder.Append(SqliteTableInteriorCell.Create(2, 10));
        tableBuilder.Append(SqliteTableInteriorCell.Create(3, 20));
        var tablePage = tableBuilder.Build();

        BinaryPrimitives.WriteUInt32BigEndian(tablePage.AsSpan(8), 0);
        Assert.Throws<InvalidDataException>(() => SqliteTableInteriorPageView.Parse(tablePage, usableSpace));

        tablePage = tableBuilder.Build();
        BinaryPrimitives.WriteUInt32BigEndian(tablePage.AsSpan(8), 2);
        Assert.Throws<InvalidDataException>(() => SqliteTableInteriorPageView.Parse(tablePage, usableSpace));

        tablePage = tableBuilder.Build();
        var tableHeader = SqliteBtreePageHeader.Parse(tablePage);
        var firstTablePointer = BinaryPrimitives.ReadUInt16BigEndian(tablePage.AsSpan(tableHeader.CellPointerArrayOffset));
        var secondTablePointer = BinaryPrimitives.ReadUInt16BigEndian(
            tablePage.AsSpan(tableHeader.CellPointerArrayOffset + sizeof(ushort)));
        BinaryPrimitives.WriteUInt16BigEndian(tablePage.AsSpan(tableHeader.CellPointerArrayOffset), secondTablePointer);
        BinaryPrimitives.WriteUInt16BigEndian(
            tablePage.AsSpan(tableHeader.CellPointerArrayOffset + sizeof(ushort)),
            firstTablePointer);
        Assert.Throws<InvalidDataException>(() => SqliteTableInteriorPageView.Parse(tablePage, usableSpace));

        var indexBuilder = new SqliteIndexInteriorPageBuilder(SqlitePageSize.Minimum, usableSpace, 4);
        indexBuilder.Append(SqliteIndexInteriorCell.Create(2, Record(SqlValue.Integer(1)), usableSpace));
        indexBuilder.Append(SqliteIndexInteriorCell.Create(3, Record(SqlValue.Integer(2)), usableSpace));
        var indexPage = indexBuilder.Build();
        var indexHeader = SqliteBtreePageHeader.Parse(indexPage);
        BinaryPrimitives.WriteUInt16BigEndian(
            indexPage.AsSpan(indexHeader.CellPointerArrayOffset),
            (ushort)usableSpace);
        Assert.Throws<InvalidDataException>(() => SqliteIndexInteriorPageView.Parse(indexPage, usableSpace));
    }

    [Test]
    public void BoundedTableLeafSearchAndSplitProduceParentSeparator()
    {
        const int usableSpace = SqlitePageSize.Minimum - 16;
        var leafBuilder = new SqliteTableLeafPageBuilder(SqlitePageSize.Minimum, usableSpace);
        foreach (var rowId in new long[] { 1, 3, 5, 7 })
            leafBuilder.Append(SqliteTableLeafCell.Create(rowId, [(byte)rowId], usableSpace));
        var sourcePage = leafBuilder.Build();
        var source = SqliteTableLeafPageView.Parse(sourcePage, usableSpace);
        var sourceBeforeFailedSplit = sourcePage.ToArray();

        source.Search(5).Should().Be(new SqliteBtreeSearchResult(2, true));
        source.Search(4).Should().Be(new SqliteBtreeSearchResult(2, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => SqliteTableLeafSplit.Create(source, 0));
        sourcePage.Should().Equal(sourceBeforeFailedSplit);

        var split = SqliteTableLeafSplit.Create(source, 2);
        split.SeparatorRowId.Should().Be(3);
        SqliteTableLeafPageView.Parse(split.LeftPage.Span, usableSpace)
            .Cells
            .Select(cell => cell.Cell.RowId)
            .Should()
            .Equal(1, 3);
        SqliteTableLeafPageView.Parse(split.RightPage.Span, usableSpace)
            .Cells
            .Select(cell => cell.Cell.RowId)
            .Should()
            .Equal(5, 7);

        var parent = new SqliteTableInteriorPageBuilder(SqlitePageSize.Minimum, usableSpace, 12);
        parent.Append(SqliteTableInteriorCell.Create(11, split.SeparatorRowId));
        SqliteTableInteriorPageView.Parse(parent.Build(), usableSpace)
            .SearchChild(4)
            .Should()
            .Be(new SqliteBtreeChildSearchResult(1, 12, false));

        var rootBuilder = new SqliteTableLeafPageBuilder(SqlitePageSize.Minimum, usableSpace, isFirstPage: true);
        rootBuilder.Append(SqliteTableLeafCell.Create(1, [1], usableSpace));
        rootBuilder.Append(SqliteTableLeafCell.Create(2, [2], usableSpace));
        var root = SqliteTableLeafPageView.Parse(rootBuilder.Build(), usableSpace, isFirstPage: true);
        var rootSplit = SqliteTableLeafSplit.Create(root, 1);
        rootSplit.SeparatorRowId.Should().Be(1);
        SqliteTableLeafPageView.Parse(rootSplit.LeftPage.Span, usableSpace)
            .Cells
            .Single()
            .Cell
            .RowId
            .Should()
            .Be(1);
    }

    [Test]
    public void BoundedIndexLeafSearchAndSplitRequireCompleteRecords()
    {
        const int usableSpace = SqlitePageSize.Minimum - 16;
        var records = new[]
        {
            Record(SqlValue.Integer(1)),
            Record(SqlValue.Integer(3)),
            Record(SqlValue.Integer(5)),
            Record(SqlValue.Integer(7)),
        };
        var leafBuilder = new SqliteIndexLeafPageBuilder(SqlitePageSize.Minimum, usableSpace);
        foreach (var record in records)
            leafBuilder.Append(SqliteIndexLeafCell.Create(record, usableSpace));
        var source = SqliteIndexLeafPageView.Parse(leafBuilder.Build(), usableSpace);

        source.Search(records[2]).Should().Be(new SqliteBtreeSearchResult(2, true));
        source.Search(Record(SqlValue.Integer(4))).Should().Be(new SqliteBtreeSearchResult(2, false));
        var split = SqliteIndexLeafSplit.Create(source, 2);
        split.GetSeparatorRecord().Should().Equal(records[1]);
        SqliteIndexLeafPageView.Parse(split.LeftPage.Span, usableSpace)
            .GetRecord(1)
            .Should()
            .Equal(records[1]);
        SqliteIndexLeafPageView.Parse(split.RightPage.Span, usableSpace)
            .GetRecord(0)
            .Should()
            .Equal(records[2]);

        var parent = new SqliteIndexInteriorPageBuilder(SqlitePageSize.Minimum, usableSpace, 22);
        parent.Append(SqliteIndexInteriorCell.Create(21, split.GetSeparatorRecord(), usableSpace));
        SqliteIndexInteriorPageView.Parse(parent.Build(), usableSpace)
            .SearchChild(Record(SqlValue.Integer(4)))
            .Should()
            .Be(new SqliteBtreeChildSearchResult(1, 22, false));

        var overflowRecord = Record(
            SqlValue.Integer(10),
            SqlValue.Blob(Enumerable.Range(0, 1_000).Select(value => unchecked((byte)value)).ToArray()));
        var layout = SqlitePayloadLayout.Calculate(
            SqliteBtreePageType.IndexLeaf,
            (ulong)overflowRecord.Length,
            usableSpace);
        var overflowBuilder = new SqliteIndexLeafPageBuilder(SqlitePageSize.Minimum, usableSpace);
        overflowBuilder.Append(SqliteIndexLeafCell.Create(records[0], usableSpace));
        overflowBuilder.Append(
            SqliteIndexLeafCell.Create(
                (ulong)overflowRecord.Length,
                overflowRecord.AsSpan(..layout.LocalPayloadLength),
                firstOverflowPage: 2,
                usableSpace),
            overflowRecord);
        var incompleteView = SqliteIndexLeafPageView.Parse(overflowBuilder.Build(), usableSpace);
        incompleteView.HasVerifiedRecordOrdering.Should().BeFalse();
        Assert.Throws<InvalidOperationException>(() => incompleteView.Search(overflowRecord));
        Assert.Throws<InvalidOperationException>(() => SqliteIndexLeafSplit.Create(incompleteView, 1));
    }

    [Test]
    public void InteriorBuilderFailuresDoNotMutateExistingStateOrDestination()
    {
        const int pageSize = SqlitePageSize.Minimum;
        const int usableSpace = pageSize - 16;
        var builder = new SqliteTableInteriorPageBuilder(pageSize, usableSpace, rightMostChildPage: 3);
        builder.Append(SqliteTableInteriorCell.Create(1, 10));

        Assert.Throws<ArgumentException>(() => builder.Append(SqliteTableInteriorCell.Create(1, 20)));
        builder.Cells.Should().ContainSingle();
        SqliteTableInteriorPageView.Parse(builder.Build(), usableSpace)
            .Cells
            .Single()
            .Cell
            .RowId
            .Should()
            .Be(10);

        var truncatedDestination = Enumerable.Repeat((byte)0xA5, pageSize - 1).ToArray();
        Assert.Throws<ArgumentException>(() => builder.WriteTo(truncatedDestination));
        truncatedDestination.Should().OnlyContain(value => value == 0xA5);
    }

    private static byte[] Record(params SqlValue[] values) => SqliteRecordCodec.Encode(values);
}
