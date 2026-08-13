using System.Buffers.Binary;
using AwesomeAssertions;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public class SqliteTableLeafMutationTests
{
    [Test]
    public void TableLeafPageBuilderPacksCellsAndPreservesReservedBytes()
    {
        const int pageSize = SqlitePageSize.Minimum;
        const int reservedSpace = 16;
        var usableSpace = pageSize - reservedSpace;
        var builder = new SqliteTableLeafPageBuilder(pageSize, usableSpace);
        builder.Append(SqliteTableLeafCell.Create(1, [0x11, 0x12], usableSpace));
        builder.Append(SqliteTableLeafCell.Create(2, ReadOnlySpan<byte>.Empty, usableSpace));
        builder.Append(SqliteTableLeafCell.Create(9, [0x91], usableSpace));

        var page = new byte[pageSize];
        page.AsSpan(usableSpace).Fill(0xE1);
        builder.WriteTo(page);

        var view = SqliteTableLeafPageView.Parse(page, usableSpace);
        view.Cells.Select(cell => cell.Cell.RowId).Should().Equal(1, 2, 9);
        view.Header.FirstFreeblockOffset.Should().Be(0);
        view.Header.FragmentedFreeBytes.Should().Be(0);
        view.CellPointers[0].Should().BeLessThan(view.CellPointers[1]);
        view.CellPointers[1].Should().BeLessThan(view.CellPointers[2]);
        page.AsSpan(
                view.Header.CellPointerArrayOffset + (view.Cells.Count * sizeof(ushort)),
                view.Header.CellContentAreaOffset - (view.Header.CellPointerArrayOffset + (view.Cells.Count * sizeof(ushort))))
            .ToArray()
            .Should()
            .OnlyContain(value => value == 0);
        page.AsSpan(usableSpace).ToArray().Should().OnlyContain(value => value == 0xE1);

        Assert.Throws<ArgumentException>(() =>
            builder.Append(SqliteTableLeafCell.Create(9, [0x92], usableSpace)));
    }

    [Test]
    public void MutationWriterCreatesWalCommittedLeafAndReadsBackOverflowPayload()
    {
        var fileSystem = new InMemoryFileSystem();
        var header = SqliteDatabaseHeader.CreateDefault() with
        {
            PageSize = SqlitePageSize.Minimum,
            ReservedSpace = 16,
        };
        using var store = SqlitePageStore.Create(fileSystem, "created.db", header);
        var allocator = new SqliteAppendOnlyPageAllocator(store);
        var writer = new SqliteTableLeafMutationWriter(store, allocator);
        var smallPayload = new byte[] { 1, 2, 3 };
        var largePayload = Enumerable.Range(0, 1_300).Select(value => unchecked((byte)value)).ToArray();

        var mutation = writer.CreatePage(
        [
            new SqliteTableLeafCellInput(3, smallPayload),
            new SqliteTableLeafCellInput(8, largePayload),
        ]);

        mutation.TableLeafPageNumber.Should().Be(2);
        mutation.OverflowPages.Should().NotBeEmpty();
        mutation.TargetDatabaseSizeInPages.Should().Be((uint)(2 + mutation.OverflowPages.Count));

        var walHeader = SqliteWalHeader.Create(
            store.PageSize,
            salt1: 0x0102_0304,
            salt2: 0x0506_0708,
            checkpointSequence: 1);
        using var wal = SqliteWalFile.Create(fileSystem, "created.db-wal", walHeader);
        mutation.AppendToWal(wal).Should().Be(mutation.OverflowPages.Count + 1);
        var recovery = wal.ScanRecovery();
        recovery.LastCommittedFrameNumber.Should().Be(mutation.OverflowPages.Count + 1);
        recovery.LastCommittedDatabaseSizeInPages.Should().Be(mutation.TargetDatabaseSizeInPages);
        var committedLeaf = wal.ReadFrame(recovery.LastCommittedFrameNumber);
        committedLeaf.Header.PageNumber.Should().Be(mutation.TableLeafPageNumber);
        var committedView = SqliteTableLeafPageView.Parse(committedLeaf.PageData, store.Header.UsableSpace);
        committedView.Cells.Select(cell => cell.Cell.RowId).Should().Equal(3, 8);

        mutation.ApplyTo(store);
        store.PageCount.Should().Be(mutation.TargetDatabaseSizeInPages);
        var installedView = SqliteTableLeafPageView.Parse(
            store.ReadPage(mutation.TableLeafPageNumber),
            store.Header.UsableSpace);
        installedView.Cells.Select(cell => cell.Cell.RowId).Should().Equal(3, 8);
        var overflowReader = new SqliteOverflowChainReader(store);
        overflowReader.ReadPayload(installedView.Cells[0].Cell).Should().Equal(smallPayload);
        overflowReader.ReadPayload(installedView.Cells[1].Cell).Should().Equal(largePayload);
    }

    [Test]
    public void MutationWriterStoresPartialOverflowChunkOnlyOnTheFinalPage()
    {
        var fileSystem = new InMemoryFileSystem();
        var header = SqliteDatabaseHeader.CreateDefault() with { PageSize = SqlitePageSize.Minimum };
        using var store = SqlitePageStore.Create(fileSystem, "ordered-overflow.db", header);
        var payload = Enumerable.Range(0, 997).Select(value => unchecked((byte)value)).ToArray();
        var writer = new SqliteTableLeafMutationWriter(store, new SqliteAppendOnlyPageAllocator(store));

        var mutation = writer.CreatePage([new SqliteTableLeafCellInput(1, payload)]);

        mutation.OverflowPages.Should().HaveCountGreaterThan(1);
        var firstOverflowPage = SqliteOverflowPageView.Parse(
            mutation.OverflowPages[0].Page.Span,
            store.Header.UsableSpace);
        firstOverflowPage.NextPageNumber.Should().Be(mutation.OverflowPages[1].PageNumber);
        mutation.ApplyTo(store);

        var cell = SqliteTableLeafPageView.Parse(
            store.ReadPage(mutation.TableLeafPageNumber),
            store.Header.UsableSpace).Cells.Single().Cell;
        new SqliteOverflowChainReader(store).ReadPayload(cell).Should().Equal(payload);
    }

    [Test]
    public void MutationWriterRewritesRootLeafWithoutChangingItsDatabaseHeader()
    {
        var fileSystem = new InMemoryFileSystem();
        using var store = SqlitePageStore.Create(fileSystem, "root.db");
        var writer = new SqliteTableLeafMutationWriter(store, new SqliteAppendOnlyPageAllocator(store));
        var expectedHeader = store.Header;

        var mutation = writer.RewritePage(
            1,
        [
            new SqliteTableLeafCellInput(4, [0x44]),
            new SqliteTableLeafCellInput(7, [0x77, 0x78]),
        ]);
        mutation.TargetDatabaseSizeInPages.Should().Be(1);
        mutation.ApplyTo(store);

        var page = store.ReadPage(1);
        SqliteDatabaseHeader.Parse(page).Should().Be(expectedHeader);
        var view = SqliteTableLeafPageView.Parse(page, store.Header.UsableSpace, isFirstPage: true);
        view.Cells.Select(cell => cell.Cell.RowId).Should().Equal(4, 7);
        view.Cells[0].Cell.LocalPayload.ToArray().Should().Equal(0x44);
        view.Cells[1].Cell.LocalPayload.ToArray().Should().Equal(0x77, 0x78);
    }

    [Test]
    public void MutationWriterRejectsCorruptLeafBeforeAllocatingAnyNewPages()
    {
        var fileSystem = new InMemoryFileSystem();
        using var store = SqlitePageStore.Create(fileSystem, "corrupt.db");
        var allocator = new SqliteAppendOnlyPageAllocator(store);
        var writer = new SqliteTableLeafMutationWriter(store, allocator);
        var created = writer.CreatePage([new SqliteTableLeafCellInput(1, [0x01])]);
        created.ApplyTo(store);

        var corrupt = store.ReadPage(created.TableLeafPageNumber);
        BinaryPrimitives.WriteUInt16BigEndian(corrupt.AsSpan(SqliteBtreePageHeader.LeafHeaderSize), (ushort)store.Header.UsableSpace);
        store.WritePage(created.TableLeafPageNumber, corrupt);
        allocator.NextPageNumber.Should().Be(3);

        Assert.Throws<InvalidDataException>(() =>
            writer.RewritePage(created.TableLeafPageNumber, [new SqliteTableLeafCellInput(2, [0x02])]));
        allocator.NextPageNumber.Should().Be(3);
    }

    [Test]
    public void MutationWriterRejectsPageOneDataAllocations()
    {
        var fileSystem = new InMemoryFileSystem();
        using var store = SqlitePageStore.Create(fileSystem, "page-one.db");
        var writer = new SqliteTableLeafMutationWriter(store, new PageOneAllocator());

        Assert.Throws<InvalidOperationException>(() =>
            writer.CreatePage([new SqliteTableLeafCellInput(1, [0x01])]));
    }

    [Test]
    public void MutationWriterFaultsLeaveThePageStoreUntouchedAndWalRecoverable()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        var databaseHeader = SqliteDatabaseHeader.CreateDefault() with { PageSize = SqlitePageSize.Minimum };
        using var store = SqlitePageStore.Create(fileSystem, "fault.db", databaseHeader);
        var writer = new SqliteTableLeafMutationWriter(store, new SqliteAppendOnlyPageAllocator(store));
        var payload = Enumerable.Range(0, 1_300).Select(value => unchecked((byte)value)).ToArray();
        var mutation = writer.CreatePage([new SqliteTableLeafCellInput(1, payload)]);
        mutation.OverflowPages.Count.Should().BeGreaterThan(1);

        var walHeader = SqliteWalHeader.Create(
            store.PageSize,
            salt1: 0x1111_2222,
            salt2: 0x3333_4444,
            checkpointSequence: 1);
        using var wal = SqliteWalFile.Create(fileSystem, "fault.db-wal", walHeader);
        // The store and WAL headers use writes one and two; fail after one overflow frame.
        faults.FailOnOccurrence(FileSystemOperation.Write, 4);

        Assert.Throws<IOException>(() => mutation.AppendToWal(wal));
        store.PageCount.Should().Be(1);
        var recovery = wal.ScanRecovery();
        recovery.LastValidFrameNumber.Should().Be(1);
        recovery.LastCommittedFrameNumber.Should().Be(0);
        wal.RecoverToLastCommittedFrame().LastCommittedFrameNumber.Should().Be(0);

        faults.FailNext(FileSystemOperation.Write);
        Assert.Throws<IOException>(() => mutation.ApplyTo(store));
        store.PageCount.Should().Be(1);

        mutation.ApplyTo(store);
        store.PageCount.Should().Be(mutation.TargetDatabaseSizeInPages);
        var view = SqliteTableLeafPageView.Parse(
            store.ReadPage(mutation.TableLeafPageNumber),
            store.Header.UsableSpace);
        new SqliteOverflowChainReader(store).ReadPayload(view.Cells.Single().Cell).Should().Equal(payload);
    }

    private sealed class PageOneAllocator : ISqlitePageAllocator
    {
        public SqlitePageAllocation Allocate() => new(1, 1);
    }
}
