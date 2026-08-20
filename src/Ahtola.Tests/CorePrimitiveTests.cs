using System.Buffers.Binary;
using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Parsing;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public class CorePrimitiveTests
{
    [TestCase(0UL, new byte[] { 0x00 })]
    [TestCase(127UL, new byte[] { 0x7f })]
    [TestCase(128UL, new byte[] { 0x81, 0x00 })]
    [TestCase(16_383UL, new byte[] { 0xff, 0x7f })]
    [TestCase(16_384UL, new byte[] { 0x81, 0x80, 0x00 })]
    [TestCase(0x00ff_ffff_ffff_ffffUL, new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x7f })]
    [TestCase(ulong.MaxValue, new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff })]
    public void VarintRoundTripsCanonicalSqliteEncoding(ulong value, byte[] expected)
    {
        Span<byte> buffer = stackalloc byte[SqliteVarint.MaximumLength];

        var written = SqliteVarint.Write(value, buffer);

        buffer[..written].ToArray().Should().Equal(expected);
        SqliteVarint.TryRead(buffer[..written], out var decoded, out var consumed).Should().BeTrue();
        decoded.Should().Be(value);
        consumed.Should().Be(written);
    }

    [Test]
    public void VarintRejectsTruncatedInput()
    {
        SqliteVarint.TryRead([0x81], out _, out _).Should().BeFalse();
        SqliteVarint.TryRead([0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x00], out _, out _).Should().BeFalse();
        Assert.Throws<ArgumentException>(() => SqliteVarint.Write(128, new byte[1]));
    }

    [TestCase(512, (ushort)512)]
    [TestCase(4_096, (ushort)4_096)]
    [TestCase(65_536, (ushort)1)]
    public void PageSizeUsesSqliteHeaderEncoding(int size, ushort encoded)
    {
        SqlitePageSize.Encode(size).Should().Be(encoded);
        SqlitePageSize.Decode(encoded).Should().Be(size);
    }

    [Test]
    public void PageSizeRejectsInvalidHeaderValues()
    {
        Assert.Throws<InvalidDataException>(() => SqlitePageSize.Decode(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SqlitePageSize.Encode(1_024 + 512));
    }

    [Test]
    public void DatabaseHeaderRoundTripsTheSqliteFormat()
    {
        var header = SqliteDatabaseHeader.CreateDefault() with
        {
            DatabaseSizeInPages = 42,
            SchemaCookie = 3,
            UserVersion = 7,
            ApplicationId = 1234,
        };

        var decoded = SqliteDatabaseHeader.Parse(header.ToArray());

        decoded.Should().Be(header);
        decoded.UsableSpace.Should().Be(SqlitePageSize.Default);
    }

    [Test]
    public void DatabaseHeaderRejectsInvalidFixedFields()
    {
        var header = SqliteDatabaseHeader.CreateDefault().ToArray();
        header[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => SqliteDatabaseHeader.Parse(header));

        header = SqliteDatabaseHeader.CreateDefault().ToArray();
        header[21] = 63;
        Assert.Throws<InvalidDataException>(() => SqliteDatabaseHeader.Parse(header));
    }

    [Test]
    public void RecordCodecRoundTripsSqliteSerialTypes()
    {
        SqlValue[] values =
        [
            SqlValue.Null,
            SqlValue.Integer(-128),
            SqlValue.Integer(0),
            SqlValue.Integer(1),
            SqlValue.Integer(128),
            SqlValue.Integer(32_768),
            SqlValue.Integer(-8_388_608),
            SqlValue.Integer(1L << 40),
            SqlValue.Integer(long.MinValue),
            SqlValue.Real(1.5),
            SqlValue.Text("Ahtola"),
            SqlValue.Blob([0, 1, 2]),
        ];

        var encoded = SqliteRecordCodec.Encode(values);

        SqliteRecordCodec.Decode(encoded).Should().Equal(values);
    }

    [Test]
    public void RecordCodecUsesZeroAndOneSerialTypes()
    {
        var encoded = SqliteRecordCodec.Encode([SqlValue.Null, SqlValue.Integer(0), SqlValue.Integer(1)]);

        encoded.Should().Equal(4, 0, 8, 9);
    }

    [Test]
    public void RecordCodecUsesTheDatabaseTextEncoding()
    {
        var encoded = SqliteRecordCodec.Encode([SqlValue.Text("A")], SqliteTextEncoding.Utf16LittleEndian);

        encoded.Should().Equal(2, 17, 0x41, 0x00);
        SqliteRecordCodec.Decode(encoded, SqliteTextEncoding.Utf16LittleEndian).Should().Equal(SqlValue.Text("A"));
    }

    [Test]
    public void RecordCodecRejectsMalformedPayloads()
    {
        Assert.Throws<InvalidDataException>(() => SqliteRecordCodec.Decode([2, 1]));
        Assert.Throws<InvalidDataException>(() => SqliteRecordCodec.Decode([0]));
    }

    [Test]
    public void BtreePageHeaderRoundTripsLeafAndInteriorPages()
    {
        var leafPage = new byte[SqlitePageSize.Default];
        var leaf = SqliteBtreePageHeader.CreateEmpty(SqliteBtreePageType.TableLeaf, leafPage.Length);
        leaf.WriteTo(leafPage);
        SqliteBtreePageHeader.Parse(leafPage).Should().Be(leaf);

        var interiorPage = new byte[SqlitePageSize.Default];
        var interior = SqliteBtreePageHeader.CreateEmpty(SqliteBtreePageType.TableInterior, interiorPage.Length) with
        {
            RightMostChildPage = 42,
        };
        interior.WriteTo(interiorPage);
        SqliteBtreePageHeader.Parse(interiorPage).Should().Be(interior);
    }

    [Test]
    public void BtreePageHeaderValidatesCellLayout()
    {
        var page = new byte[SqlitePageSize.Default];
        page[0] = (byte)SqliteBtreePageType.TableLeaf;
        page[3] = 0;
        page[4] = 1;
        page[5] = 0;
        page[6] = 8;

        Assert.Throws<InvalidDataException>(() => SqliteBtreePageHeader.Parse(page));
    }

    [Test]
    public void BtreePageHeaderAcceptsFreeblocksInCellContent()
    {
        var page = new byte[SqlitePageSize.Default];
        var header = SqliteBtreePageHeader.CreateEmpty(SqliteBtreePageType.TableLeaf, page.Length) with
        {
            CellContentAreaOffset = 4_000,
            FirstFreeblockOffset = 4_000,
        };
        header.WriteTo(page);

        SqliteBtreePageHeader.Parse(page).Should().Be(header);
    }

    [Test]
    public void CellPointerArrayRoundTripsBigEndianOffsetsAndCopiesItsInput()
    {
        var page = new byte[SqlitePageSize.Minimum];
        var header = SqliteBtreePageHeader.CreateEmpty(SqliteBtreePageType.TableLeaf, page.Length) with
        {
            CellCount = 2,
            CellContentAreaOffset = 500,
        };
        header.WriteTo(page);

        SqliteCellPointerArray.WriteTo(page, header, new ushort[] { 500, 508 }, page.Length);

        page[header.CellPointerArrayOffset].Should().Be(0x01);
        page[header.CellPointerArrayOffset + 1].Should().Be(0xf4);
        var parsed = SqliteCellPointerArray.Parse(page, header, page.Length);
        parsed.Offsets.Should().Equal((ushort)500, (ushort)508);

        BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(header.CellPointerArrayOffset), 511);
        parsed.Offsets.Should().Equal((ushort)500, (ushort)508);
    }

    [Test]
    public void CellPointerArrayRejectsOffsetsOutsideCellContentAndDuplicates()
    {
        var page = new byte[SqlitePageSize.Minimum];
        var header = SqliteBtreePageHeader.CreateEmpty(SqliteBtreePageType.TableLeaf, page.Length) with
        {
            CellCount = 1,
            CellContentAreaOffset = 500,
        };
        header.WriteTo(page);

        BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(header.CellPointerArrayOffset), (ushort)page.Length);
        Assert.Throws<InvalidDataException>(() => SqliteCellPointerArray.Parse(page, header, page.Length));

        var duplicateHeader = header with { CellCount = 2 };
        duplicateHeader.WriteTo(page);
        BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(duplicateHeader.CellPointerArrayOffset), 500);
        BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(duplicateHeader.CellPointerArrayOffset + sizeof(ushort)), 500);
        Assert.Throws<InvalidDataException>(() => SqliteCellPointerArray.Parse(page, duplicateHeader, page.Length));
        Assert.Throws<ArgumentException>(() => SqliteCellPointerArray.WriteTo(
            page,
            duplicateHeader,
            new ushort[] { 500, 500 },
            page.Length));
    }

    [TestCase(0L)]
    [TestCase(127L)]
    [TestCase(128L)]
    [TestCase(-1L)]
    [TestCase(long.MaxValue)]
    [TestCase(long.MinValue)]
    public void TableLeafCellRoundTripsRowidVarintEdges(long rowId)
    {
        var cell = SqliteTableLeafCell.Create(rowId, new byte[] { 0xaa, 0xbb }, SqlitePageSize.Minimum);

        var decoded = SqliteTableLeafCell.Decode(cell.ToArray(), SqlitePageSize.Minimum);

        decoded.RowId.Should().Be(rowId);
        decoded.PayloadLength.Should().Be(2UL);
        decoded.LocalPayload.ToArray().Should().Equal(0xaa, 0xbb);
        decoded.FirstOverflowPage.Should().BeNull();
        decoded.EncodedLength.Should().Be(cell.EncodedLength);
    }

    [Test]
    public void TableLeafCellUsesPayloadLengthVarintsAndValidatesCellBounds()
    {
        var payload = new byte[128];
        Array.Fill(payload, (byte)0x5a);
        var cell = SqliteTableLeafCell.Create(127, payload, SqlitePageSize.Minimum);
        var encoded = cell.ToArray();

        encoded[..2].Should().Equal(0x81, 0x00);
        encoded[2].Should().Be(0x7f);
        SqliteTableLeafCell.Decode(encoded, SqlitePageSize.Minimum).LocalPayload.ToArray().Should().Equal(payload);
        Assert.Throws<InvalidDataException>(() => SqliteTableLeafCell.Decode(encoded[..^1], SqlitePageSize.Minimum));
    }

    [Test]
    public void TableLeafCellsUseSqlitesFourByteMinimumStorageLength()
    {
        var cell = SqliteTableLeafCell.Create(0, ReadOnlySpan<byte>.Empty, SqlitePageSize.Minimum);

        cell.EncodedLength.Should().Be(SqliteTableLeafCell.MinimumStorageLength);
        cell.ToArray().Should().Equal(0, 0, 0, 0);
        Assert.Throws<InvalidDataException>(() => SqliteTableLeafCell.Decode(new byte[] { 0, 0 }, SqlitePageSize.Minimum));
    }

    [Test]
    public void PayloadLayoutMatchesSqliteLocalPayloadThresholds()
    {
        var local = SqlitePayloadLayout.Calculate(SqliteBtreePageType.TableLeaf, 4_061, SqlitePageSize.Default);
        local.MaximumLocalPayloadLength.Should().Be(4_061);
        local.MinimumLocalPayloadLength.Should().Be(489);
        local.LocalPayloadLength.Should().Be(4_061);
        local.UsesOverflow.Should().BeFalse();

        var minimum = SqlitePayloadLayout.Calculate(SqliteBtreePageType.TableLeaf, 4_062, SqlitePageSize.Default);
        minimum.LocalPayloadLength.Should().Be(489);
        minimum.UsesOverflow.Should().BeTrue();
        minimum.StoredPayloadLength.Should().Be(493);

        var modulo = SqlitePayloadLayout.Calculate(SqliteBtreePageType.TableLeaf, 4_681, SqlitePageSize.Default);
        modulo.LocalPayloadLength.Should().Be(589);
        modulo.UsesOverflow.Should().BeTrue();

        var index = SqlitePayloadLayout.Calculate(SqliteBtreePageType.IndexLeaf, 1_003, SqlitePageSize.Default);
        index.MaximumLocalPayloadLength.Should().Be(1_002);
        index.LocalPayloadLength.Should().Be(489);
        index.UsesOverflow.Should().BeTrue();
    }

    [Test]
    public void TableLeafCellRoundTripsItsLocalOverflowPayloadAndPointer()
    {
        var layout = SqlitePayloadLayout.Calculate(SqliteBtreePageType.TableLeaf, 4_062, SqlitePageSize.Default);
        var localPayload = new byte[layout.LocalPayloadLength];
        Array.Fill(localPayload, (byte)0x42);
        var cell = SqliteTableLeafCell.Create(
            rowId: 42,
            payloadLength: 4_062,
            localPayload: localPayload,
            firstOverflowPage: 7,
            usableSpace: SqlitePageSize.Default);

        var encoded = cell.ToArray();
        encoded[^4..].Should().Equal(0, 0, 0, 7);
        var decoded = SqliteTableLeafCell.Decode(encoded, SqlitePageSize.Default);
        decoded.PayloadLength.Should().Be(4_062UL);
        decoded.LocalPayload.ToArray().Should().Equal(localPayload);
        decoded.FirstOverflowPage.Should().Be(7U);

        encoded[^1] = 0;
        Assert.Throws<InvalidDataException>(() => SqliteTableLeafCell.Decode(encoded, SqlitePageSize.Default));
    }

    [Test]
    public void TableLeafPageViewValidatesCellBoundariesAndReservedSpaceOnPageOne()
    {
        const int reservedSpace = 16;
        var page = new byte[SqlitePageSize.Minimum];
        var usableSpace = page.Length - reservedSpace;
        var record = SqliteRecordCodec.Encode([SqlValue.Integer(7)]);
        var cell = SqliteTableLeafCell.Create(128, record, usableSpace);
        var cellOffset = checked((ushort)(usableSpace - cell.EncodedLength));
        var header = SqliteBtreePageHeader.CreateEmpty(
            SqliteBtreePageType.TableLeaf,
            page.Length,
            isFirstPage: true,
            usableSpace: usableSpace) with
        {
            CellCount = 1,
            CellContentAreaOffset = cellOffset,
        };
        header.WriteTo(page);
        cell.WriteTo(page.AsSpan(cellOffset));
        SqliteCellPointerArray.WriteTo(page, header, new[] { cellOffset }, usableSpace);

        var view = SqliteTableLeafPageView.Parse(page, usableSpace, isFirstPage: true);
        view.Cells.Should().ContainSingle();
        view.Cells[0].Offset.Should().Be(cellOffset);
        view.Cells[0].Cell.RowId.Should().Be(128);
        SqliteRecordCodec.Decode(view.Cells[0].Cell.LocalPayload.Span).Should().Equal(SqlValue.Integer(7));

        page[cellOffset + cell.EncodedLength - 1] = 0;
        SqliteRecordCodec.Decode(view.Cells[0].Cell.LocalPayload.Span).Should().Equal(SqlValue.Integer(7));

        BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(header.CellPointerArrayOffset), (ushort)usableSpace);
        Assert.Throws<InvalidDataException>(() => SqliteTableLeafPageView.Parse(page, usableSpace, isFirstPage: true));
    }

    [Test]
    public void TableLeafPageViewRejectsTruncatedAndOverlappingCells()
    {
        var page = new byte[SqlitePageSize.Minimum];
        var truncatedHeader = SqliteBtreePageHeader.CreateEmpty(SqliteBtreePageType.TableLeaf, page.Length) with
        {
            CellCount = 1,
            CellContentAreaOffset = page.Length - 1,
        };
        truncatedHeader.WriteTo(page);
        page[^1] = 0;
        SqliteCellPointerArray.WriteTo(page, truncatedHeader, new ushort[] { (ushort)(page.Length - 1) }, page.Length);
        Assert.Throws<InvalidDataException>(() => SqliteTableLeafPageView.Parse(page, page.Length));

        page.AsSpan().Clear();
        var overlappingHeader = SqliteBtreePageHeader.CreateEmpty(SqliteBtreePageType.TableLeaf, page.Length) with
        {
            CellCount = 2,
            CellContentAreaOffset = 500,
        };
        overlappingHeader.WriteTo(page);
        SqliteTableLeafCell.Create(1, new byte[] { 0 }, page.Length).WriteTo(page.AsSpan(500));
        SqliteTableLeafCell.Create(0, ReadOnlySpan<byte>.Empty, page.Length).WriteTo(page.AsSpan(502));
        SqliteCellPointerArray.WriteTo(page, overlappingHeader, new ushort[] { 500, 502 }, page.Length);
        Assert.Throws<InvalidDataException>(() => SqliteTableLeafPageView.Parse(page, page.Length));
    }

    [Test]
    public void TableLeafPageViewRejectsOutOfOrderRowids()
    {
        var page = new byte[SqlitePageSize.Minimum];
        var header = SqliteBtreePageHeader.CreateEmpty(SqliteBtreePageType.TableLeaf, page.Length) with
        {
            CellCount = 2,
            CellContentAreaOffset = 490,
        };
        header.WriteTo(page);
        var first = SqliteTableLeafCell.Create(2, new byte[] { 1 }, page.Length);
        var second = SqliteTableLeafCell.Create(1, new byte[] { 2 }, page.Length);
        first.WriteTo(page.AsSpan(490));
        second.WriteTo(page.AsSpan(500));
        SqliteCellPointerArray.WriteTo(page, header, new ushort[] { 490, 500 }, page.Length);

        Assert.Throws<InvalidDataException>(() => SqliteTableLeafPageView.Parse(page, page.Length));
    }

    [Test]
    public void TableLeafPageViewRejectsCellsOverlappingFreeblocks()
    {
        var page = new byte[SqlitePageSize.Minimum];
        var header = SqliteBtreePageHeader.CreateEmpty(SqliteBtreePageType.TableLeaf, page.Length) with
        {
            CellCount = 1,
            CellContentAreaOffset = 500,
            FirstFreeblockOffset = 500,
        };
        header.WriteTo(page);
        SqliteTableLeafCell.Create(0, ReadOnlySpan<byte>.Empty, page.Length).WriteTo(page.AsSpan(500));
        page[503] = 4; // The zero-payload cell is also a syntactically valid 4-byte freeblock.
        SqliteCellPointerArray.WriteTo(page, header, new ushort[] { 500 }, page.Length);

        Assert.Throws<InvalidDataException>(() => SqliteTableLeafPageView.Parse(page, page.Length));
    }

    [Test]
    public void ParameterMapSupportsDollarInNamesAndBoundsParameterIndices()
    {
        var parameters = SqlParameterMap.Parse("SELECT :a$b, :a$b, $a::b(c), $a::b(c);");
        parameters.Count.Should().Be(2);
        parameters.GetName(1).Should().Be(":a$b");
        parameters.GetName(2).Should().Be("$a::b(c)");

        Assert.Throws<FormatException>(() => SqlParameterMap.Parse("?250001"));
    }

    [Test]
    public void ParameterMapSkipsQuotedAndCommentedParameters()
    {
        var parameters = SqlParameterMap.Parse(
            "SELECT '?1', \"@ignored\", ?2, :name, :name, @other -- $ignored\n, $value, /* ?9 */ ?");

        parameters.Count.Should().Be(6);
        parameters.GetName(1).Should().BeNull();
        parameters.GetName(2).Should().Be("?2");
        parameters.GetName(3).Should().Be(":name");
        parameters.GetName(4).Should().Be("@other");
        parameters.GetName(5).Should().Be("$value");
        parameters.GetName(6).Should().BeNull();
        parameters.IsReferenced(1).Should().BeFalse();
        parameters.IsReferenced(2).Should().BeTrue();
        parameters.IsReferenced(6).Should().BeTrue();
        parameters.TryGetIndex("?2", out var numbered).Should().BeTrue();
        numbered.Should().Be(2);
        parameters.TryGetIndex(":name", out var named).Should().BeTrue();
        named.Should().Be(3);
    }

    [Test]
    public void SqlValuesAreImmutableAndCompareByKindAndContent()
    {
        var source = new byte[] { 1, 2, 3 };
        var blob = SqlValue.Blob(source);
        source[0] = 42;

        blob.AsBlob().ToArray().Should().Equal(1, 2, 3);
        blob.Should().Be(SqlValue.Blob([1, 2, 3]));
        SqlValue.Integer(1).Should().NotBe(SqlValue.Real(1));
        SqlValue.Text("Ahtola").AsText().Should().Be("Ahtola");
        SqlValue.Null.Kind.Should().Be(SqlValueKind.Null);
        Assert.Throws<InvalidOperationException>(() => blob.AsText());
    }
}
