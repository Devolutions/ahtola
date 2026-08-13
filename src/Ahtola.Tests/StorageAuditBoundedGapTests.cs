using System.Buffers.Binary;
using AwesomeAssertions;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class StorageAuditBoundedGapTests
{
    private static ReadOnlySpan<byte> RollbackJournalMagic
        => [0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7];

    [Test]
    public void EmptyDatabaseHeaderAcceptsSchemaFormatZero()
    {
        var empty = SqliteDatabaseHeader.CreateDefault() with
        {
            SchemaFormat = 0,
            TextEncoding = SqliteTextEncoding.Unset,
            VersionValidFor = 1,
        };

        SqliteDatabaseHeader.Parse(empty.ToArray()).Should().Be(empty);
        var singlePageEmpty = empty with { DatabaseSizeInPages = 1 };
        SqliteDatabaseHeader.Parse(singlePageEmpty.ToArray()).Should().Be(singlePageEmpty);

        var initialized = empty with { TextEncoding = SqliteTextEncoding.Utf8 };
        Assert.Throws<InvalidOperationException>(() => initialized.ToArray());
        Assert.Throws<InvalidOperationException>(
            () => (empty with { DatabaseSizeInPages = 2 }).ToArray());
        Assert.Throws<InvalidOperationException>(
            () => (empty with { VersionValidFor = empty.ChangeCounter + 1 }).ToArray());
        Assert.Throws<InvalidOperationException>(
            () => (empty with { SchemaCookie = 1 }).ToArray());
        var bytes = empty.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(56), (uint)SqliteTextEncoding.Utf8);
        Assert.Throws<InvalidDataException>(() => SqliteDatabaseHeader.Parse(bytes));
    }

    [Test]
    public void DatabaseAndBtreeHeaderSerializersRejectUnknownEnums()
    {
        var databaseHeader = SqliteDatabaseHeader.CreateDefault();
        Assert.Throws<InvalidOperationException>(
            () => (databaseHeader with { WriteVersion = (SqliteFileFormatVersion)3 }).ToArray());
        Assert.Throws<InvalidOperationException>(
            () => (databaseHeader with { ReadVersion = (SqliteFileFormatVersion)3 }).ToArray());
        Assert.Throws<InvalidOperationException>(
            () => (databaseHeader with { TextEncoding = (SqliteTextEncoding)4 }).ToArray());

        var page = new byte[SqlitePageSize.Minimum];
        var btreeHeader = SqliteBtreePageHeader.CreateEmpty(
            SqliteBtreePageType.TableLeaf,
            page.Length) with
        {
            PageType = (SqliteBtreePageType)0xff,
        };
        Assert.Throws<InvalidOperationException>(() => btreeHeader.WriteTo(page));
    }

    [Test]
    public void BtreePageValidatesFragmentedGapAccounting()
    {
        var page = new byte[SqlitePageSize.Minimum];
        var cell = SqliteTableLeafCell.Create(1, [0x2a], page.Length);
        var cellOffset = page.Length - cell.EncodedLength;
        var header = SqliteBtreePageHeader.CreateEmpty(
            SqliteBtreePageType.TableLeaf,
            page.Length) with
        {
            CellCount = 1,
            CellContentAreaOffset = cellOffset - 1,
            FragmentedFreeBytes = 1,
        };
        header.WriteTo(page);
        cell.WriteTo(page.AsSpan(cellOffset));
        SqliteCellPointerArray.WriteTo(page, header, [checked((ushort)cellOffset)], page.Length);

        SqliteTableLeafPageView.Parse(page, page.Length).Cells.Should().ContainSingle();

        (header with { FragmentedFreeBytes = 0 }).WriteTo(page);
        Assert.Throws<InvalidDataException>(() => SqliteTableLeafPageView.Parse(page, page.Length));

        page.AsSpan().Clear();
        var untrackedGapHeader = header with
        {
            CellContentAreaOffset = cellOffset - 4,
            FragmentedFreeBytes = 4,
        };
        untrackedGapHeader.WriteTo(page);
        cell.WriteTo(page.AsSpan(cellOffset));
        SqliteCellPointerArray.WriteTo(
            page,
            untrackedGapHeader,
            [checked((ushort)cellOffset)],
            page.Length);
        Assert.Throws<InvalidDataException>(() => SqliteTableLeafPageView.Parse(page, page.Length));
    }

    [Test]
    public void WalHeaderUsesLiteral64KPageSizeAndRejectsZero()
    {
        var bytes = SqliteWalHeader.Create(
            SqlitePageSize.Maximum,
            salt1: 1,
            salt2: 2).ToArray();

        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8)).Should().Be(65_536);
        SqliteWalHeader.Parse(bytes).PageSize.Should().Be(SqlitePageSize.Maximum);

        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 0);
        RewriteWalHeaderChecksum(bytes);
        Assert.Throws<InvalidDataException>(() => SqliteWalHeader.Parse(bytes));
    }

    [Test]
    public void WalRestartAdvancesSequenceAndSaltsUsingUpstreamRules()
    {
        var fileSystem = new InMemoryFileSystem();
        var original = SqliteWalHeader.Create(
            SqlitePageSize.Minimum,
            salt1: uint.MaxValue,
            salt2: 0x1020_3040,
            checkpointSequence: uint.MaxValue);
        using var wal = SqliteWalFile.Create(fileSystem, "restart.db-wal", original);
        wal.AppendFrame(1, new byte[original.PageSize], databaseSizeInPages: 1);

        wal.ResetAfterDurableCheckpoint(publishCheckpointedRecoveryMarker: false);

        wal.Header.CheckpointSequence.Should().Be(0);
        wal.Header.Salt1.Should().Be(0);
        wal.Length.Should().Be(SqliteWalHeader.Size);
        using var raw = fileSystem.OpenFile("restart.db-wal", FileOpenMode.OpenExisting, readOnly: true);
        var bytes = new byte[SqliteWalHeader.Size];
        raw.Read(0, bytes).Should().Be(bytes.Length);
        SqliteWalHeader.Parse(bytes).Should().BeEquivalentTo(wal.Header);
    }

    [Test]
    public void WalAllowsCommitFrameBeyondTruncatedDatabaseSize()
    {
        var header = SqliteWalHeader.Create(SqlitePageSize.Minimum, salt1: 1, salt2: 2);
        var fileSystem = new InMemoryFileSystem();
        using var wal = SqliteWalFile.Create(fileSystem, "commit.db-wal", header);
        wal.AppendFrame(2, new byte[header.PageSize], databaseSizeInPages: 1);

        var recovery = wal.ScanRecovery();
        recovery.StopReason.Should().Be(SqliteWalRecoveryStopReason.EndOfFile);
        recovery.LastCommittedFrameNumber.Should().Be(1);
        recovery.LastCommittedDatabaseSizeInPages.Should().Be(1);
        wal.ReadFrame(1).Header.PageNumber.Should().Be(2);
    }

    [Test]
    public void WalChecksumRejectsUnknownOrderEvenForEmptyInput()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SqliteWalChecksum.Calculate([], (SqliteWalChecksumByteOrder)99));
    }

    [Test]
    public void RollbackJournalIgnoresShortMagicOnlyArtifact()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "short.db-journal";
        using (var journal = fileSystem.OpenFile(path, FileOpenMode.CreateNew))
            journal.Write(0, RollbackJournalMagic);

        SqliteRollbackJournal.IsHot(fileSystem, path).Should().BeFalse();
        SqliteRollbackJournal.RecoverIfPresent(
            fileSystem,
            "short.db",
            path,
            readOnly: false);
        fileSystem.FileExists(path).Should().BeFalse();
    }

    [TestCase(0u)]
    [TestCase(256u)]
    [TestCase(65_537u)]
    [TestCase(uint.MaxValue)]
    public void RollbackJournalRejectsUnsafePageSizeBeforeAllocation(uint pageSize)
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "page-size.db-journal";
        var header = new byte[512];
        RollbackJournalMagic.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), 512);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24), pageSize);
        using (var journal = fileSystem.OpenFile(path, FileOpenMode.CreateNew))
            journal.Write(0, header);

        Assert.Throws<InvalidDataException>(
            () => SqliteRollbackJournal.RecoverIfPresent(
                fileSystem,
                "page-size.db",
                path,
                readOnly: false));
    }

    [Test]
    public void BtreeHeaderBoundsCellContentAreaAgainstUsableSpace()
    {
        const int pageSize = SqlitePageSize.Minimum;
        const int reserved = 32;
        const int usableSpace = pageSize - reserved;
        var page = new byte[pageSize];
        var header = SqliteBtreePageHeader.CreateEmpty(SqliteBtreePageType.TableLeaf, pageSize, usableSpace: usableSpace);
        header.WriteTo(page, usableSpace);

        SqliteBtreePageHeader.Parse(page, usableSpace: usableSpace).CellContentAreaOffset
            .Should().Be(usableSpace);

        // Byte 5..6 is the cell-content-area offset. Pointing it into the reserved
        // suffix is legal against the physical page but corrupt against usableSize.
        BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(5), checked((ushort)(usableSpace + 1)));
        SqliteBtreePageHeader.Parse(page).CellContentAreaOffset.Should().Be(usableSpace + 1);
        Assert.Throws<InvalidDataException>(
            () => SqliteBtreePageHeader.Parse(page, usableSpace: usableSpace));
        Assert.Throws<InvalidDataException>(
            () => SqliteTableLeafPageView.Parse(page, usableSpace));
    }

    [Test]
    public void BtreeHeaderBoundsFirstFreeblockAgainstUsableSpaceMinusFour()
    {
        const int pageSize = SqlitePageSize.Minimum;
        const int reserved = 32;
        const int usableSpace = pageSize - reserved;
        var page = new byte[pageSize];
        var header = SqliteBtreePageHeader.CreateEmpty(SqliteBtreePageType.TableLeaf, pageSize, usableSpace: usableSpace)
            with
            {
                CellContentAreaOffset = 16,
            };
        header.WriteTo(page, usableSpace);

        // Byte 1..2 is the first-freeblock offset; SQLite requires pc <= usableSize - 4.
        BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(1), checked((ushort)(usableSpace - 4)));
        SqliteBtreePageHeader.Parse(page, usableSpace: usableSpace).FirstFreeblockOffset
            .Should().Be((ushort)(usableSpace - 4));

        BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(1), checked((ushort)(usableSpace - 3)));
        SqliteBtreePageHeader.Parse(page).FirstFreeblockOffset.Should().Be((ushort)(usableSpace - 3));
        Assert.Throws<InvalidDataException>(
            () => SqliteBtreePageHeader.Parse(page, usableSpace: usableSpace));
    }

    [Test]
    public void AppendOnlyAllocatorSkipsPendingBytePage()
    {
        // 0x40000000 / 65536 + 1 == 16385, reachable without materializing a 1 GiB file.
        const int pageSize = SqlitePageSize.Maximum;
        var pendingBytePage = SqlitePageLimits.PendingBytePage(pageSize);
        pendingBytePage.Should().Be(16_385u);

        var allocator = new SqliteAppendOnlyPageAllocator(pendingBytePage - 2, pageSize);
        allocator.PendingBytePageNumber.Should().Be(pendingBytePage);
        allocator.Peek(0).Should().Be(pendingBytePage - 1);
        allocator.Peek(1).Should().Be(pendingBytePage + 1);

        allocator.Allocate().PageNumber.Should().Be(pendingBytePage - 1);
        allocator.Allocate().PageNumber.Should().Be(pendingBytePage + 1);
        allocator.Allocate().PageNumber.Should().Be(pendingBytePage + 2);

        var atPending = new SqliteAppendOnlyPageAllocator(pendingBytePage - 1, pageSize);
        atPending.Allocate().PageNumber.Should().Be(pendingBytePage + 1);
    }

    [Test]
    public void StagedPageIoAndFreelistNeverHandOutPendingBytePage()
    {
        const int pageSize = SqlitePageSize.Maximum;
        var pendingBytePage = SqlitePageLimits.PendingBytePage(pageSize);

        var io = new SqliteStagedBtreePageIo(
            _ => new byte[pageSize],
            committedPageCount: pendingBytePage - 1,
            pageSize,
            usableSpace: pageSize);
        io.AllocatePage().Should().Be(pendingBytePage + 1);
        io.StagedPages.Keys.Should().Contain(pendingBytePage);

        var freelist = SqliteFreelist.Create(
            usedPageCount: pendingBytePage - 1,
            targetPageCount: pendingBytePage + 2,
            pageSize,
            usableSpace: pageSize);
        freelist.PageNumbers.Should().NotContain(pendingBytePage);
        freelist.PageNumbers.Should().Contain(pendingBytePage + 1);

        Assert.Throws<ArgumentException>(
            () => SqliteFreelist.CreateFromFreePages(
                pendingBytePage + 2,
                [pendingBytePage],
                pageSize,
                usableSpace: pageSize));
    }

    [Test]
    public void SplitReservationsSkipPendingBytePage()
    {
        const int pageSize = SqlitePageSize.Maximum;
        var pendingBytePage = SqlitePageLimits.PendingBytePage(pageSize);
        var allocator = new SqliteAppendOnlyPageAllocator(pendingBytePage - 3, pageSize);

        // A three-page interior-root promotion straddling the pending byte must
        // reserve 16382, 16384, 16386 - never 16385.
        var reserved = new[] { allocator.Allocate(), allocator.Allocate(), allocator.Allocate() };
        reserved.Select(allocation => allocation.PageNumber)
            .Should()
            .Equal(pendingBytePage - 2, pendingBytePage - 1, pendingBytePage + 1);
    }

    [Test]
    public void AllocationPathsEnforceTheDatabaseGrowthCeiling()
    {
        SqlitePageLimits.DefaultMaximumPageCount.Should().Be(0xffff_fffeu);
        SqlitePageLimits.ClampMaximumPageCount(4, currentPageCount: 9).Should().Be(9u);
        SqlitePageLimits.ClampMaximumPageCount(0, currentPageCount: 1)
            .Should().Be(SqlitePageLimits.DefaultMaximumPageCount);

        var allocator = new SqliteAppendOnlyPageAllocator(
            sourceDatabaseSizeInPages: 4,
            SqlitePageSize.Minimum,
            maximumPageCount: 6);
        allocator.MaximumPageCount.Should().Be(6u);
        allocator.Allocate().PageNumber.Should().Be(5u);
        allocator.Allocate().PageNumber.Should().Be(6u);
        Assert.Throws<InvalidOperationException>(() => allocator.Allocate());

        var io = new SqliteStagedBtreePageIo(
            _ => new byte[SqlitePageSize.Minimum],
            committedPageCount: 4,
            SqlitePageSize.Minimum,
            usableSpace: SqlitePageSize.Minimum);
        io.SetMaximumPageCount(5).Should().Be(5u);
        io.AllocatePage().Should().Be(5u);
        Assert.Throws<InvalidOperationException>(() => io.AllocatePage());
        io.SetMaximumPageCount(1).Should().Be(5u);
    }

    [Test]
    public void RollbackJournalWithUnknownRecordCountStopsAtTornChecksumTail()
    {
        const int pageSize = 512;
        const uint initialPageCount = 4;
        var fileSystem = new InMemoryFileSystem();
        const string journalPath = "torn.db-journal";
        const string databasePath = "torn.db";
        const uint nonce = 0;

        using (var database = fileSystem.OpenFile(databasePath, FileOpenMode.CreateNew))
        {
            database.Write(0, new byte[pageSize * (int)initialPageCount]);
            database.FlushToDisk();
        }

        var goodPage = new byte[pageSize];
        goodPage.AsSpan().Fill(0x11);
        var tornPage = new byte[pageSize];
        tornPage.AsSpan().Fill(0x22);

        var journalBytes = new List<byte>();
        var header = new byte[pageSize];
        RollbackJournalMagic.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), uint.MaxValue);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), nonce);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), initialPageCount);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), pageSize);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24), pageSize);
        journalBytes.AddRange(header);
        journalBytes.AddRange(BuildJournalRecord(2, goodPage, nonce, corruptChecksum: false));
        journalBytes.AddRange(BuildJournalRecord(3, tornPage, nonce, corruptChecksum: true));

        using (var journal = fileSystem.OpenFile(journalPath, FileOpenMode.CreateNew))
        {
            journal.Write(0, journalBytes.ToArray());
            journal.FlushToDisk();
        }

        SqliteRollbackJournal.RecoverIfPresent(fileSystem, databasePath, journalPath, readOnly: false);

        using var restored = fileSystem.OpenFile(databasePath, FileOpenMode.OpenExisting, readOnly: true);
        var page2 = new byte[pageSize];
        var page3 = new byte[pageSize];
        restored.Read(pageSize, page2);
        restored.Read(pageSize * 2, page3);
        page2.Should().Equal(goodPage);
        page3.Should().NotEqual(tornPage);
    }

    [Test]
    public void SchemaFormatOneThroughFourRejectsUnsetTextEncoding()
    {
        var header = SqliteDatabaseHeader.CreateDefault();
        foreach (var schemaFormat in new uint[] { 1, 2, 3, 4 })
        {
            var candidate = header with
            {
                SchemaFormat = schemaFormat,
                TextEncoding = SqliteTextEncoding.Unset,
            };
            Assert.Throws<InvalidOperationException>(() => candidate.ToArray());

            var bytes = (header with { SchemaFormat = schemaFormat }).ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(56), (uint)SqliteTextEncoding.Unset);
            Assert.Throws<InvalidDataException>(() => SqliteDatabaseHeader.Parse(bytes));
        }
    }

    [Test]
    public void HeaderRejectsImpossibleAutoVacuumCombinations()
    {
        var header = SqliteDatabaseHeader.CreateDefault() with { DatabaseSizeInPages = 4 };
        header.LargestRootBtreePage.Should().Be(0u);
        header.IncrementalVacuumEnabled.Should().Be(0u);

        Assert.Throws<InvalidOperationException>(
            () => (header with { IncrementalVacuumEnabled = 1 }).ToArray());
        Assert.Throws<InvalidOperationException>(
            () => (header with { LargestRootBtreePage = 2, IncrementalVacuumEnabled = 2 }).ToArray());
        Assert.Throws<InvalidOperationException>(
            () => (header with { LargestRootBtreePage = 5 }).ToArray());

        var valid = header with { LargestRootBtreePage = 2, IncrementalVacuumEnabled = 1 };
        SqliteDatabaseHeader.Parse(valid.ToArray()).Should().Be(valid);

        var bytes = header.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(64), 1);
        Assert.Throws<InvalidDataException>(() => SqliteDatabaseHeader.Parse(bytes));

        bytes = valid.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(52), 9);
        Assert.Throws<InvalidDataException>(() => SqliteDatabaseHeader.Parse(bytes));
    }

    private static byte[] BuildJournalRecord(uint pageNumber, byte[] page, uint nonce, bool corruptChecksum)
    {
        var record = new byte[4 + page.Length + 4];
        BinaryPrimitives.WriteUInt32BigEndian(record, pageNumber);
        page.CopyTo(record.AsSpan(4));

        var checksum = nonce;
        for (var index = page.Length - 200; index >= 0; index -= 200)
            checksum = unchecked(checksum + page[index]);
        if (corruptChecksum)
            checksum = unchecked(checksum + 1);

        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(4 + page.Length), checksum);
        return record;
    }

    private static void RewriteWalHeaderChecksum(Span<byte> header)
    {
        var order = BinaryPrimitives.ReadUInt32BigEndian(header)
            == SqliteWalHeader.BigEndianChecksumMagic
            ? SqliteWalChecksumByteOrder.BigEndian
            : SqliteWalChecksumByteOrder.LittleEndian;
        var checksum = SqliteWalChecksum.Calculate(header[..24], order);
        BinaryPrimitives.WriteUInt32BigEndian(header[24..], checksum.First);
        BinaryPrimitives.WriteUInt32BigEndian(header[28..], checksum.Second);
    }

}
