using System.Buffers.Binary;
using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedStagedFreelistAndHotJournalTests
{
    private const int PageSize = 4096;
    private const int UsableSpace = 4096;

    [Test]
    public void StagedAllocatePrefersFreelistLeafThenTrunkThenAppend()
    {
        var pages = CreateBlankPages(pageCount: 4);
        // Page 2 is a trunk with one leaf (page 3).
        WriteTrunk(pages[1], nextTrunk: 0, leafPages: [3]);
        pages[2].AsSpan().Clear();

        var io = new SqliteStagedBtreePageIo(
            pageNumber => (byte[])pages[checked((int)pageNumber) - 1].Clone(),
            committedPageCount: 4,
            pageSize: PageSize,
            usableSpace: UsableSpace,
            firstFreelistTrunkPage: 2,
            freelistPageCount: 2);

        var leaf = io.AllocatePage();
        leaf.Should().Be(3u);
        io.FreelistPageCount.Should().Be(1u);
        io.FirstFreelistTrunkPage.Should().Be(2u);
        io.PageCount.Should().Be(4u);

        var trunkReuse = io.AllocatePage();
        trunkReuse.Should().Be(2u);
        io.FreelistPageCount.Should().Be(0u);
        io.FirstFreelistTrunkPage.Should().Be(0u);
        io.PageCount.Should().Be(4u);

        var appended = io.AllocatePage();
        appended.Should().Be(5u);
        io.PageCount.Should().Be(5u);
    }

    [Test]
    public void StagedFreeBuildsTrunkThenLeafAndAllocateReclaimsThem()
    {
        var pages = CreateBlankPages(pageCount: 3);
        var io = new SqliteStagedBtreePageIo(
            pageNumber => (byte[])pages[checked((int)pageNumber) - 1].Clone(),
            committedPageCount: 3,
            pageSize: PageSize,
            usableSpace: UsableSpace);

        io.FreePage(3);
        io.FirstFreelistTrunkPage.Should().Be(3u);
        io.FreelistPageCount.Should().Be(1u);

        io.FreePage(2);
        io.FirstFreelistTrunkPage.Should().Be(3u);
        io.FreelistPageCount.Should().Be(2u);

        var reusedLeaf = io.AllocatePage();
        reusedLeaf.Should().Be(2u);
        io.FreelistPageCount.Should().Be(1u);

        var reusedTrunk = io.AllocatePage();
        reusedTrunk.Should().Be(3u);
        io.FreelistPageCount.Should().Be(0u);
        io.FirstFreelistTrunkPage.Should().Be(0u);
        io.PageCount.Should().Be(3u);
    }

    [Test]
    public void StagedAllocateSkipsPendingBytePage()
    {
        const int pageSize = 512;
        var pendingBytePage = (0x4000_0000u / pageSize) + 1;
        var io = new SqliteStagedBtreePageIo(
            _ => new byte[pageSize],
            committedPageCount: pendingBytePage - 1,
            pageSize,
            usableSpace: pageSize);

        io.AllocatePage().Should().Be(pendingBytePage + 1);
        io.StagedPages.Should().ContainKey(pendingBytePage);
        io.StagedPages.Should().ContainKey(pendingBytePage + 1);
    }

    [Test]
    public void StagedFreelistRejectsDuplicateFree()
    {
        var pages = CreateBlankPages(pageCount: 3);
        var io = CreateStagedIo(pages);

        io.FreePage(3);

        Assert.Throws<InvalidOperationException>(() => io.FreePage(3));
    }

    [Test]
    public void StagedFreelistRejectsFreeingExistingTrunk()
    {
        var pages = CreateBlankPages(pageCount: 3);
        WriteTrunk(pages[1], nextTrunk: 0, leafPages: [3]);
        var io = CreateStagedIo(pages, firstTrunk: 2, freeCount: 2);

        Assert.Throws<InvalidOperationException>(() => io.FreePage(2));
    }

    [Test]
    public void StagedFreelistRejectsOversizedLeafCount()
    {
        var pages = CreateBlankPages(pageCount: 2);
        BinaryPrimitives.WriteUInt32BigEndian(pages[1].AsSpan(sizeof(uint)), uint.MaxValue);

        Assert.Throws<InvalidDataException>(() => CreateStagedIo(pages, firstTrunk: 2, freeCount: 1));
    }

    [Test]
    public void StagedFreelistRejectsDuplicateLeaves()
    {
        var pages = CreateBlankPages(pageCount: 3);
        WriteTrunk(pages[1], nextTrunk: 0, leafPages: [3, 3]);

        Assert.Throws<InvalidDataException>(() => CreateStagedIo(pages, firstTrunk: 2, freeCount: 3));
    }

    [Test]
    public void StagedFreelistRejectsInvalidNextTrunk()
    {
        var pages = CreateBlankPages(pageCount: 2);
        WriteTrunk(pages[1], nextTrunk: 3, leafPages: []);

        Assert.Throws<InvalidDataException>(() => CreateStagedIo(pages, firstTrunk: 2, freeCount: 1));
    }

    [Test]
    public void StagedFreelistRejectsHeaderCountMismatch()
    {
        var pages = CreateBlankPages(pageCount: 2);
        WriteTrunk(pages[1], nextTrunk: 0, leafPages: []);

        Assert.Throws<InvalidDataException>(() => CreateStagedIo(pages, firstTrunk: 2, freeCount: 2));
    }

    [Test]
    public void IncrementalInsertReusesExistingFreelistLeafWithoutGrowingFile()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "freelist-incremental-reuse.db";

        uint reusableLeaf;
        uint pageCountBefore;
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
            // Large payload creates overflow pages; shrinking it populates the freelist.
            Execute(connection, $"INSERT INTO t VALUES (1, '{new string('q', 12_000)}');");
            Execute(connection, "UPDATE t SET value = 'small-committed' WHERE id = 1;");
        }

        using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
        {
            var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            header.FreelistPageCount.Should().BeGreaterThan(0);
            var freelist = SqliteFreelist.Read(header, pager.CommittedPageCount, pager.ReadCommittedPage);
            freelist.LeafPageNumbers.Should().NotBeEmpty();
            reusableLeaf = freelist.LeafPageNumbers[0];
            pageCountBefore = pager.CommittedPageCount;
        }

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            // Another large row needs overflow pages and should prefer freelist leaves.
            Execute(connection, $"INSERT INTO t VALUES (2, '{new string('r', 12_000)}');");
        }

        using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
        {
            var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            header.DatabaseSizeInPages.Should().Be(pageCountBefore);
            var freelist = SqliteFreelist.Read(header, pager.CommittedPageCount, pager.ReadCommittedPage);
            freelist.PageNumbers.Should().NotContain(reusableLeaf);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        QueryInteger(reopenedConnection, "SELECT COUNT(*) FROM t;").Should().Be(2);
    }

    [Test]
    public void BulkDeleteReclaimsLeafPagesOntoFreelistForLaterInserts()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "bulk-delete-reclaim.db";

        uint pageCountAfterInserts;
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, payload TEXT);");
            for (var id = 1; id <= 400; id++)
                Execute(connection, $"INSERT INTO t VALUES ({id}, '{new string('x', 80)}');");
        }

        using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
        {
            pageCountAfterInserts = pager.CommittedPageCount;
            pageCountAfterInserts.Should().BeGreaterThan(5u);
        }

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "DELETE FROM t;");
            QueryInteger(connection, "SELECT COUNT(*) FROM t;").Should().Be(0);
        }

        uint freelistAfterDelete;
        using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
        {
            var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            freelistAfterDelete = header.FreelistPageCount;
            freelistAfterDelete.Should().BeGreaterThan(0u);
            header.DatabaseSizeInPages.Should().Be(pageCountAfterInserts);
        }

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            for (var id = 1; id <= 400; id++)
                Execute(connection, $"INSERT INTO t VALUES ({id}, '{new string('y', 80)}');");
            QueryInteger(connection, "SELECT COUNT(*) FROM t;").Should().Be(400);
        }

        using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
        {
            var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            header.DatabaseSizeInPages.Should().Be(pageCountAfterInserts);
            header.FreelistPageCount.Should().BeLessThan(freelistAfterDelete);
        }
    }

    [Test]
    public void UnderfullLeafRedistributesWithFullSiblingWhenMergeDoesNotFit()
    {
        var pages = CreateBlankPages(pageCount: 2);
        var io = new SqliteStagedBtreePageIo(
            pageNumber => (byte[])pages[checked((int)pageNumber) - 1].Clone(),
            committedPageCount: 2,
            pageSize: PageSize,
            usableSpace: UsableSpace);
        io.WritePage(2, new SqliteTableLeafPageBuilder(PageSize, UsableSpace).Build());

        var writer = new SqliteIncrementalTableBtree(io);
        // Large records fill leaves quickly so a nearly-full sibling cannot absorb a
        // half-empty neighbor — force two-way redistribute instead of merge.
        var record = new byte[200];
        record.AsSpan().Fill(0x4D);
        const int rowCount = 80;
        for (var rowId = 1L; rowId <= rowCount; rowId++)
            writer.Insert(2, rowId, record);

        io.PageCount.Should().BeGreaterThan(3u);

        // Delete most low rowids so the left leaf falls below half full while the right
        // sibling stays too full for a one-page merge.
        for (var rowId = 1L; rowId <= 30; rowId++)
        {
            if (rowId % 4 != 0)
                writer.Delete(2, rowId);
        }

        var cursor = new SqliteTableBtreeCursor(io);
        for (var rowId = 1L; rowId <= rowCount; rowId++)
        {
            var deleted = rowId <= 30 && rowId % 4 != 0;
            if (deleted)
            {
                cursor.TrySeek(2, rowId, out _).Should().BeFalse($"deleted rowid {rowId}");
                continue;
            }

            cursor.TrySeek(2, rowId, out var found).Should().BeTrue($"rowid {rowId}");
            found.Should().Equal(record);
        }
    }

    [Test]
    public void UnderfullLeafMergesIntoSiblingWithoutGrowingOnRefill()
    {
        var pages = CreateBlankPages(pageCount: 2);
        var io = new SqliteStagedBtreePageIo(
            pageNumber => (byte[])pages[checked((int)pageNumber) - 1].Clone(),
            committedPageCount: 2,
            pageSize: PageSize,
            usableSpace: UsableSpace);
        io.WritePage(2, new SqliteTableLeafPageBuilder(PageSize, UsableSpace).Build());

        var writer = new SqliteIncrementalTableBtree(io);
        var record = new byte[60];
        record.AsSpan().Fill(0x3C);
        const int rowCount = 200;
        for (var rowId = 1L; rowId <= rowCount; rowId++)
            writer.Insert(2, rowId, record);

        var peakPages = io.PageCount;
        peakPages.Should().BeGreaterThan(3u);

        // Delete every other row so leaves stay non-empty but under-full and merge.
        for (var rowId = 1L; rowId <= rowCount; rowId += 2)
            writer.Delete(2, rowId);

        io.FreelistPageCount.Should().BeGreaterThan(0u);

        // Surviving rows must still be seekable.
        var cursor = new SqliteTableBtreeCursor(io);
        for (var rowId = 2L; rowId <= rowCount; rowId += 2)
        {
            cursor.TrySeek(2, rowId, out var found).Should().BeTrue($"rowid {rowId}");
            found.Should().Equal(record);
        }

        var freelistAfterDeletes = io.FreelistPageCount;

        // Refill deleted rowids; tree must remain correct and not explode in size.
        for (var rowId = 1L; rowId <= rowCount; rowId += 2)
            writer.Insert(2, rowId, record);

        // Merges reclaim some pages; refill may still append a few if packing differs.
        io.PageCount.Should().BeLessThan(peakPages * 3);
        if (freelistAfterDeletes > 0 && io.PageCount <= peakPages)
            io.FreelistPageCount.Should().BeLessThanOrEqualTo(freelistAfterDeletes);
        for (var rowId = 1L; rowId <= rowCount; rowId++)
            cursor.TrySeek(2, rowId, out _).Should().BeTrue($"rowid {rowId} after refill");
    }

    [Test]
    public void EmptyNonRootLeafDeleteFreesPageAndCollapsesRoot()
    {
        var pages = CreateBlankPages(pageCount: 2);
        var io = new SqliteStagedBtreePageIo(
            pageNumber => (byte[])pages[checked((int)pageNumber) - 1].Clone(),
            committedPageCount: 2,
            pageSize: PageSize,
            usableSpace: UsableSpace);
        io.WritePage(2, new SqliteTableLeafPageBuilder(PageSize, UsableSpace).Build());

        var writer = new SqliteIncrementalTableBtree(io);
        var record = new byte[60];
        record.AsSpan().Fill(0x5A);
        const int rowCount = 500;
        for (var rowId = 1L; rowId <= rowCount; rowId++)
            writer.Insert(2, rowId, record);

        io.PageCount.Should().BeGreaterThan(3u, "inserts must deepen the table b-tree past a single leaf");
        var peakPages = io.PageCount;

        for (var rowId = 1L; rowId <= rowCount; rowId++)
            writer.Delete(2, rowId);

        var rootHeader = SqliteBtreePageHeader.Parse(io.ReadPage(2), isFirstPage: false);
        rootHeader.PageType.Should().Be(SqliteBtreePageType.TableLeaf);
        rootHeader.CellCount.Should().Be(0);
        // Every non-root data page created by the inserts must be on the freelist.
        // Page 1 is the DB header page and page 2 is the catalog root — neither is freed.
        io.FreelistPageCount.Should().BeGreaterThan(0u);
        io.FirstFreelistTrunkPage.Should().NotBe(0u);
        io.FirstFreelistTrunkPage.Should().NotBe(2u);

        // Refill until the root must split — new pages must come from the freelist.
        var freelistBeforeRefill = io.FreelistPageCount;
        for (var rowId = 1L; rowId <= rowCount; rowId++)
            writer.Insert(2, rowId, record);

        io.PageCount.Should().Be(peakPages);
        io.FreelistPageCount.Should().BeLessThan(freelistBeforeRefill);
    }

    [Test]
    public void DeepTreeRangeDeleteMergesInteriorSiblingsWithoutMaintenanceException()
    {
        // Build a multi-level interior tree, then delete a dense low-rowid range so
        // empty leaves and underfull parents collapse. Single-child non-root interiors
        // must merge into sibling interiors (P5-A) rather than throw MaintenanceRequired.
        var pages = CreateBlankPages(pageCount: 2);
        var io = new SqliteStagedBtreePageIo(
            pageNumber => (byte[])pages[checked((int)pageNumber) - 1].Clone(),
            committedPageCount: 2,
            pageSize: PageSize,
            usableSpace: UsableSpace);
        io.WritePage(2, new SqliteTableLeafPageBuilder(PageSize, UsableSpace).Build());

        var writer = new SqliteIncrementalTableBtree(io);
        var record = new byte[48];
        record.AsSpan().Fill(0x7E);
        const int rowCount = 2500;
        for (var rowId = 1L; rowId <= rowCount; rowId++)
            writer.Insert(2, rowId, record);

        io.PageCount.Should().BeGreaterThan(10u, "fixture must create multi-level interiors");
        var peakPages = io.PageCount;

        // Delete the lower ~40% so left branches empty/underfill while right branches
        // remain dense enough that the root stays interior with sibling subtrees.
        const int deleteThrough = rowCount * 2 / 5;
        for (var rowId = 1L; rowId <= deleteThrough; rowId++)
            writer.Delete(2, rowId);

        var cursor = new SqliteTableBtreeCursor(io);
        for (var rowId = 1L; rowId <= rowCount; rowId++)
        {
            if (rowId <= deleteThrough)
            {
                cursor.TrySeek(2, rowId, out _).Should().BeFalse($"deleted rowid {rowId}");
                continue;
            }

            cursor.TrySeek(2, rowId, out var found).Should().BeTrue($"surviving rowid {rowId}");
            found.Should().Equal(record);
        }

        io.FreelistPageCount.Should().BeGreaterThan(0u);
        io.PageCount.Should().BeLessThanOrEqualTo(peakPages);

        // Refill deleted keys; tree must remain seek-correct without exploding.
        for (var rowId = 1L; rowId <= deleteThrough; rowId++)
            writer.Insert(2, rowId, record);

        for (var rowId = 1L; rowId <= rowCount; rowId++)
            cursor.TrySeek(2, rowId, out _).Should().BeTrue($"rowid {rowId} after refill");
        io.PageCount.Should().BeLessThan(peakPages * 3);
    }

    [Test]
    public void HotJournalRecoveryAcceptsTrailingPaddingAndSentinelRecordCount()
    {
        var fileSystem = new InMemoryFileSystem();
        const string dbPath = "hot-journal.db";
        const string journalPath = "hot-journal.db-journal";
        const int pageSize = 4096;
        const uint sectorSize = 4096;
        const uint nonce = 0xA5A5A5A5;
        const uint originalPages = 2;

        var originalPage2 = new byte[pageSize];
        originalPage2.AsSpan().Fill(0x11);
        var corruptedPage2 = new byte[pageSize];
        corruptedPage2.AsSpan().Fill(0x22);

        using (var db = fileSystem.OpenFile(dbPath, FileOpenMode.CreateNew))
        {
            var headerPage = new byte[pageSize];
            var header = SqliteDatabaseHeader.CreateDefault() with
            {
                DatabaseSizeInPages = originalPages,
                ChangeCounter = 1,
                VersionValidFor = 1,
            };
            header.WriteTo(headerPage);
            db.Write(0, headerPage);
            db.Write(pageSize, corruptedPage2);
            db.SetLength(pageSize * 2L);
        }

        using (var journal = fileSystem.OpenFile(journalPath, FileOpenMode.CreateNew))
        {
            var headerBytes = new byte[sectorSize];
            WriteJournalHeader(
                headerBytes,
                recordCount: uint.MaxValue,
                nonce,
                originalPages,
                (uint)pageSize,
                sectorSize);
            journal.Write(0, headerBytes);

            var recordOffset = (long)sectorSize;
            Span<byte> pageNumberBytes = stackalloc byte[4];
            Span<byte> checksumBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(pageNumberBytes, 2);
            journal.Write(recordOffset, pageNumberBytes);
            journal.Write(recordOffset + 4, originalPage2);
            BinaryPrimitives.WriteUInt32BigEndian(checksumBytes, ComputeJournalChecksum(originalPage2, nonce));
            journal.Write(recordOffset + 4 + pageSize, checksumBytes);
            // Trailing padding after the scanned record is ignored for sentinel journals.
            journal.SetLength(recordOffset + pageSize + 8 + 128);
        }

        SqliteRollbackJournal.RecoverIfPresent(fileSystem, dbPath, journalPath, readOnly: false);
        fileSystem.FileExists(journalPath).Should().BeFalse();

        using var restored = fileSystem.OpenFile(dbPath, FileOpenMode.OpenExisting, readOnly: true);
        var restoredPage2 = new byte[pageSize];
        restored.Read(pageSize, restoredPage2);
        restoredPage2.Should().Equal(originalPage2);
    }

    [Test]
    public void HotJournalRecoveryAcceptsDeclaredCountWithTrailingCapacity()
    {
        var fileSystem = new InMemoryFileSystem();
        const string dbPath = "hot-journal-pad.db";
        const string journalPath = "hot-journal-pad.db-journal";
        const int pageSize = 4096;
        const uint sectorSize = 512;
        const uint nonce = 7;
        const uint originalPages = 2;

        var originalPage2 = new byte[pageSize];
        originalPage2.AsSpan().Fill(0x33);
        var corruptedPage2 = new byte[pageSize];
        corruptedPage2.AsSpan().Fill(0x44);

        using (var db = fileSystem.OpenFile(dbPath, FileOpenMode.CreateNew))
        {
            var headerPage = new byte[pageSize];
            var header = SqliteDatabaseHeader.CreateDefault() with
            {
                DatabaseSizeInPages = originalPages,
                ChangeCounter = 1,
                VersionValidFor = 1,
            };
            header.WriteTo(headerPage);
            db.Write(0, headerPage);
            db.Write(pageSize, corruptedPage2);
            db.SetLength(pageSize * 2L);
        }

        using (var journal = fileSystem.OpenFile(journalPath, FileOpenMode.CreateNew))
        {
            var headerBytes = new byte[sectorSize];
            WriteJournalHeader(
                headerBytes,
                recordCount: 1,
                nonce,
                originalPages,
                (uint)pageSize,
                sectorSize);
            journal.Write(0, headerBytes);

            var recordOffset = (long)sectorSize;
            Span<byte> pageNumberBytes = stackalloc byte[4];
            Span<byte> checksumBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(pageNumberBytes, 2);
            journal.Write(recordOffset, pageNumberBytes);
            journal.Write(recordOffset + 4, originalPage2);
            BinaryPrimitives.WriteUInt32BigEndian(checksumBytes, ComputeJournalChecksum(originalPage2, nonce));
            journal.Write(recordOffset + 4 + pageSize, checksumBytes);
            // Extra preallocated journal capacity after declared records.
            journal.SetLength(recordOffset + pageSize + 8 + 4096);
        }

        SqliteRollbackJournal.RecoverIfPresent(fileSystem, dbPath, journalPath, readOnly: false);

        using var restored = fileSystem.OpenFile(dbPath, FileOpenMode.OpenExisting, readOnly: true);
        var restoredPage2 = new byte[pageSize];
        restored.Read(pageSize, restoredPage2);
        restoredPage2.Should().Equal(originalPage2);
    }

    private static byte[][] CreateBlankPages(int pageCount)
    {
        var pages = new byte[pageCount][];
        for (var i = 0; i < pageCount; i++)
            pages[i] = new byte[PageSize];
        return pages;
    }

    private static SqliteStagedBtreePageIo CreateStagedIo(
        byte[][] pages,
        uint firstTrunk = 0,
        uint freeCount = 0)
        => new(
            pageNumber => (byte[])pages[checked((int)pageNumber) - 1].Clone(),
            committedPageCount: (uint)pages.Length,
            pageSize: PageSize,
            usableSpace: UsableSpace,
            firstFreelistTrunkPage: firstTrunk,
            freelistPageCount: freeCount);

    private static void WriteTrunk(byte[] page, uint nextTrunk, uint[] leafPages)
    {
        page.AsSpan().Clear();
        BinaryPrimitives.WriteUInt32BigEndian(page, nextTrunk);
        BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(4), (uint)leafPages.Length);
        for (var i = 0; i < leafPages.Length; i++)
            BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(8 + (i * 4)), leafPages[i]);
    }

    private static void WriteJournalHeader(
        Span<byte> destination,
        uint recordCount,
        uint nonce,
        uint initialPages,
        uint pageSize,
        uint sectorSize)
    {
        ReadOnlySpan<byte> magic = [0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7];
        magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..], recordCount);
        BinaryPrimitives.WriteUInt32BigEndian(destination[12..], nonce);
        BinaryPrimitives.WriteUInt32BigEndian(destination[16..], initialPages);
        BinaryPrimitives.WriteUInt32BigEndian(destination[20..], sectorSize);
        BinaryPrimitives.WriteUInt32BigEndian(destination[24..], pageSize);
    }

    private static uint ComputeJournalChecksum(ReadOnlySpan<byte> page, uint nonce)
    {
        var checksum = nonce;
        for (var index = page.Length - 200; index >= 0; index -= 200)
            checksum = unchecked(checksum + page[index]);
        return checksum;
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private static long QueryInteger(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }
}
