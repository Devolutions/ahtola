using AwesomeAssertions;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public class SqliteOverflowStorageTests
{
    [Test]
    public void OverflowPageViewUsesSqliteBigEndianLayoutAndPreservesReservedBytes()
    {
        const int pageSize = SqlitePageSize.Minimum;
        const int reservedSpace = 16;
        var usableSpace = pageSize - reservedSpace;
        var payload = new byte[] { 0xA1, 0xB2, 0xC3 };
        var encoded = SqliteOverflowPageView
            .Create(pageSize, usableSpace, 0x01020304, payload)
            .ToArray();
        encoded.AsSpan(usableSpace).Fill(0xEE);

        var view = SqliteOverflowPageView.Parse(encoded, usableSpace);

        view.NextPageNumber.Should().Be(0x01020304U);
        view.PayloadCapacity.Should().Be(usableSpace - SqliteOverflowPageView.HeaderLength);
        view.Payload.Span[..payload.Length].ToArray().Should().Equal(payload);
        view.Payload.Span[payload.Length..].ToArray().Should().OnlyContain(value => value == 0);
        view.ToArray().Should().Equal(encoded);

        var destination = new byte[pageSize];
        view.WriteTo(destination);
        destination.Should().Equal(encoded);
    }

    [Test]
    public void OverflowPageViewRejectsNonSqliteGeometryAndOversizedPayloads()
    {
        const int pageSize = SqlitePageSize.Minimum;
        const int usableSpace = pageSize - 16;

        Assert.Throws<InvalidDataException>(() => SqliteOverflowPageView.Parse(new byte[pageSize - 1], usableSpace));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SqliteOverflowPageView.Create(pageSize, pageSize - byte.MaxValue - 1, 0, []));
        Assert.Throws<ArgumentException>(() => SqliteOverflowPageView.Create(
            pageSize,
            usableSpace,
            0,
            new byte[usableSpace - SqliteOverflowPageView.HeaderLength + 1]));

        var view = SqliteOverflowPageView.Create(pageSize, usableSpace, 0, []);
        Assert.Throws<ArgumentException>(() => view.WriteTo(new byte[pageSize - 1]));
    }

    [Test]
    public void OverflowChainReaderTraversesAndReconstructsTableLeafPayload()
    {
        var fileSystem = new InMemoryFileSystem();
        using var store = CreateReservedStore(fileSystem, "main.db");
        var reader = new SqliteOverflowChainReader(store);
        var layout = SqlitePayloadLayout.Calculate(
            SqliteBtreePageType.TableLeaf,
            payloadLength: 1_000,
            store.Header.UsableSpace);
        var payload = Enumerable.Range(0, 1_000).Select(value => unchecked((byte)value)).ToArray();
        var overflowPayload = payload.AsSpan(layout.LocalPayloadLength).ToArray();
        WriteChain(store, firstPageNumber: 2, overflowPayload);
        var cell = SqliteTableLeafCell.Create(
            rowId: 42,
            payloadLength: (ulong)payload.Length,
            localPayload: payload.AsSpan(..layout.LocalPayloadLength),
            firstOverflowPage: 2,
            usableSpace: store.Header.UsableSpace);

        reader.Traverse(2, (ulong)overflowPayload.Length).Should().Equal(2U, 3U);
        reader.Read(2, overflowPayload.Length).Should().Equal(overflowPayload);
        reader.ReadPayload(cell).Should().Equal(payload);
    }

    [Test]
    public void OverflowChainReaderRejectsTruncatedAndOverlongChains()
    {
        var fileSystem = new InMemoryFileSystem();
        using var store = CreateReservedStore(fileSystem, "main.db");
        var reader = new SqliteOverflowChainReader(store);
        var payloadCapacity = reader.PayloadCapacity;

        WriteOverflowPage(store, pageNumber: 2, nextPageNumber: 0, new byte[payloadCapacity]);
        Assert.Throws<InvalidDataException>(() => reader.Read(2, payloadCapacity + 1));

        WriteOverflowPage(store, pageNumber: 2, nextPageNumber: 3, new byte[payloadCapacity]);
        Assert.Throws<InvalidDataException>(() => reader.Read(2, payloadCapacity));
    }

    [Test]
    public void OverflowChainReaderRejectsCyclesAndOutOfRangeReferences()
    {
        var fileSystem = new InMemoryFileSystem();
        using var store = CreateReservedStore(fileSystem, "cycle.db");
        var reader = new SqliteOverflowChainReader(store);
        var payloadCapacity = reader.PayloadCapacity;

        WriteOverflowPage(store, pageNumber: 2, nextPageNumber: 3, new byte[payloadCapacity]);
        WriteOverflowPage(store, pageNumber: 3, nextPageNumber: 2, new byte[payloadCapacity]);
        Assert.Throws<InvalidDataException>(() => reader.Read(2, (payloadCapacity * 2) + 1));
        Assert.Throws<InvalidDataException>(() => reader.Read(1, 1));

        using var outOfRangeStore = CreateReservedStore(fileSystem, "out-of-range.db");
        var outOfRangeReader = new SqliteOverflowChainReader(outOfRangeStore);
        WriteOverflowPage(outOfRangeStore, pageNumber: 2, nextPageNumber: 3, new byte[outOfRangeReader.PayloadCapacity]);
        Assert.Throws<InvalidDataException>(() =>
            outOfRangeReader.Read(2, outOfRangeReader.PayloadCapacity + 1));
    }

    [Test]
    public void OverflowRangeReadValidatesTheRequiredNextPointerBeforeReturning()
    {
        var fileSystem = new InMemoryFileSystem();
        using var store = CreateReservedStore(fileSystem, "range-corrupt.db");
        var reader = new SqliteOverflowChainReader(store);
        var logicalLength = checked((ulong)reader.PayloadCapacity + 1);

        WriteOverflowPage(store, pageNumber: 2, nextPageNumber: 2, new byte[reader.PayloadCapacity]);
        Assert.Throws<InvalidDataException>(() =>
            reader.ReadRange(2, logicalLength, offset: 0, new byte[1]));

        WriteOverflowPage(store, pageNumber: 2, nextPageNumber: 3, new byte[reader.PayloadCapacity]);
        Assert.Throws<InvalidDataException>(() =>
            reader.ReadRange(2, logicalLength, offset: 0, new byte[1]));

        var cellPayloadLength = checked((ulong)reader.PayloadCapacity + 100);
        var localLength = SqlitePayloadLayout.Calculate(
            SqliteBtreePageType.TableLeaf,
            cellPayloadLength,
            store.Header.UsableSpace).LocalPayloadLength;
        var invalidCell = SqliteTableLeafCell.Create(
            rowId: 1,
            cellPayloadLength,
            new byte[localLength],
            firstOverflowPage: 3,
            store.Header.UsableSpace);
        Assert.Throws<InvalidDataException>(() =>
            reader.ReadPayloadRange(invalidCell, offset: 0, new byte[1]));
    }

    [Test]
    public void OverflowChainReaderSurfacesDeterministicReadFaultsWithoutMutation()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        using var store = CreateReservedStore(fileSystem, "main.db");
        var reader = new SqliteOverflowChainReader(store);
        var payload = new byte[] { 1, 2, 3, 4 };
        WriteOverflowPage(store, pageNumber: 2, nextPageNumber: 0, payload);

        faults.FailNext(FileSystemOperation.Read);
        Assert.Throws<IOException>(() => reader.Read(2, payload.Length));
        reader.Read(2, payload.Length).Should().Equal(payload);
    }

    private static SqlitePageStore CreateReservedStore(InMemoryFileSystem fileSystem, string path)
    {
        var header = SqliteDatabaseHeader.CreateDefault() with
        {
            PageSize = SqlitePageSize.Minimum,
            ReservedSpace = 16,
        };
        return SqlitePageStore.Create(fileSystem, path, header);
    }

    private static void WriteChain(SqlitePageStore store, uint firstPageNumber, ReadOnlySpan<byte> payload)
    {
        var payloadCapacity = store.Header.UsableSpace - SqliteOverflowPageView.HeaderLength;
        var offset = 0;
        var pageNumber = firstPageNumber;
        while (offset < payload.Length)
        {
            var count = Math.Min(payloadCapacity, payload.Length - offset);
            var nextPageNumber = offset + count == payload.Length ? 0U : pageNumber + 1;
            WriteOverflowPage(store, pageNumber, nextPageNumber, payload.Slice(offset, count));
            offset += count;
            pageNumber++;
        }
    }

    private static void WriteOverflowPage(
        SqlitePageStore store,
        uint pageNumber,
        uint nextPageNumber,
        ReadOnlySpan<byte> payload)
    {
        var page = SqliteOverflowPageView.Create(
            store.PageSize,
            store.Header.UsableSpace,
            nextPageNumber,
            payload);
        store.WritePage(pageNumber, page.ToArray());
    }
}
