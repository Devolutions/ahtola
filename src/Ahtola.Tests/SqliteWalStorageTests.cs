using System.Buffers.Binary;
using AwesomeAssertions;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public class SqliteWalStorageTests
{
    [Test]
    public void WalHeaderRoundTripsBigEndianFieldsAndChecksumOrders()
    {
        var header = SqliteWalHeader.Create(
            SqlitePageSize.Default,
            salt1: 0x1122_3344,
            salt2: 0x5566_7788,
            checkpointSequence: 0x99AA_BBCC,
            checksumByteOrder: SqliteWalChecksumByteOrder.BigEndian);

        var bytes = header.ToArray();
        bytes.AsSpan(0, 4).ToArray().Should().Equal(0x37, 0x7F, 0x06, 0x83);
        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4, 4)).Should().Be(SqliteWalHeader.CurrentFormatVersion);
        bytes.AsSpan(8, 4).ToArray().Should().Equal(0, 0, 0x10, 0);
        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)).Should().Be(0x1122_3344);
        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)).Should().Be(0x5566_7788);

        var parsed = SqliteWalHeader.Parse(bytes);
        parsed.PageSize.Should().Be(SqlitePageSize.Default);
        parsed.CheckpointSequence.Should().Be(0x99AA_BBCC);
        parsed.ChecksumByteOrder.Should().Be(SqliteWalChecksumByteOrder.BigEndian);
        parsed.Checksum1.Should().Be(header.Checksum1);
        parsed.Checksum2.Should().Be(header.Checksum2);

        bytes[31] ^= 0x01;
        Assert.Throws<InvalidDataException>(() => SqliteWalHeader.Parse(bytes));
        Assert.Throws<InvalidDataException>(() => SqliteWalHeader.Parse(new byte[SqliteWalHeader.Size - 1]));
    }

    [Test]
    public void WalCodecsRejectInvalidPageSizesAndFrameBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SqliteWalHeader.Create(513, salt1: 1, salt2: 2));

        var maximumSizeHeader = SqliteWalHeader.Create(SqlitePageSize.Maximum, salt1: 1, salt2: 2).ToArray();
        maximumSizeHeader.AsSpan(8, 4).ToArray().Should().Equal(0, 1, 0, 0);
        SqliteWalHeader.Parse(maximumSizeHeader).PageSize.Should().Be(SqlitePageSize.Maximum);

        var malformedHeader = CreateHeader().ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(malformedHeader.AsSpan(8, 4), 513);
        RewriteHeaderChecksum(malformedHeader);
        Assert.Throws<InvalidDataException>(() => SqliteWalHeader.Parse(malformedHeader));

        BinaryPrimitives.WriteUInt32BigEndian(malformedHeader.AsSpan(8, 4), 0);
        RewriteHeaderChecksum(malformedHeader);
        Assert.Throws<InvalidDataException>(() => SqliteWalHeader.Parse(malformedHeader));

        Assert.Throws<InvalidDataException>(() => SqliteWalFrameHeader.Parse(new byte[SqliteWalFrameHeader.Size - 1]));
        Assert.Throws<InvalidDataException>(() => SqliteWalFrameHeader.Parse(new byte[SqliteWalFrameHeader.Size]));
        var invalidFrameHeader = new SqliteWalFrameHeader(0, 0, 1, 2, 3, 4);
        Assert.Throws<ArgumentException>(() => invalidFrameHeader.WriteTo(new byte[SqliteWalFrameHeader.Size - 1]));
        Assert.Throws<InvalidOperationException>(() => invalidFrameHeader.WriteTo(new byte[SqliteWalFrameHeader.Size]));

        var fileSystem = new InMemoryFileSystem();
        var header = CreateHeader();
        using var wal = SqliteWalFile.Create(fileSystem, "main.db-wal", header);
        Assert.Throws<ArgumentOutOfRangeException>(() => wal.AppendFrame(0, CreatePage(header.PageSize, 0x01), 1));
        Assert.Throws<ArgumentException>(() => wal.AppendFrame(1, new byte[header.PageSize - 1], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => wal.ReadFrame(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => wal.ReadFrame(long.MaxValue));
    }

    [Test]
    public void WalFileAppendsReadsAndRecoversCommittedFrames()
    {
        var fileSystem = new InMemoryFileSystem();
        var header = CreateHeader();
        var firstPage = CreatePage(header.PageSize, 0xA1);
        var secondPage = CreatePage(header.PageSize, 0xB2);

        using (var wal = SqliteWalFile.Create(fileSystem, "main.db-wal", header))
        {
            wal.AppendFrame(pageNumber: 1, firstPage);
            wal.AppendFrame(pageNumber: 2, secondPage, databaseSizeInPages: 2);

            wal.Length.Should().Be(SqliteWalHeader.Size + 2 * wal.FrameSize);
            var frame = wal.ReadFrame(2);
            frame.Header.PageNumber.Should().Be(2);
            frame.Header.DatabaseSizeInPages.Should().Be(2);
            frame.Header.Salt1.Should().Be(header.Salt1);
            frame.Header.Salt2.Should().Be(header.Salt2);
            frame.PageData.Should().Equal(secondPage);

            var recovery = wal.ScanRecovery();
            recovery.LastValidFrameNumber.Should().Be(2);
            recovery.LastCommittedFrameNumber.Should().Be(2);
            recovery.LastCommittedDatabaseSizeInPages.Should().Be(2);
            recovery.LastCommittedByteLength.Should().Be(wal.Length);
            recovery.StopReason.Should().Be(SqliteWalRecoveryStopReason.EndOfFile);
        }

        using var reopened = SqliteWalFile.Open(fileSystem, "main.db-wal", readOnly: true);
        reopened.ReadFrame(1).PageData.Should().Equal(firstPage);
        reopened.ScanRecovery().LastCommittedFrameNumber.Should().Be(2);
        Assert.Throws<InvalidOperationException>(() => reopened.AppendFrame(3, secondPage, 2));
    }

    [Test]
    public void WalRecoveryTruncatesUncommittedAndPartialTailsAtLastCommit()
    {
        var fileSystem = new InMemoryFileSystem();
        var header = CreateHeader();
        using var wal = SqliteWalFile.Create(fileSystem, "main.db-wal", header);

        wal.AppendFrame(1, CreatePage(header.PageSize, 0x31), databaseSizeInPages: 1);
        wal.AppendFrame(2, CreatePage(header.PageSize, 0x32));
        var committedLength = wal.Length - wal.FrameSize;
        using (var rawFile = fileSystem.OpenFile("main.db-wal", FileOpenMode.OpenExisting))
            rawFile.Write(wal.Length, [0xAA, 0xBB, 0xCC]);

        var recovery = wal.ScanRecovery();
        recovery.LastValidFrameNumber.Should().Be(2);
        recovery.LastCommittedFrameNumber.Should().Be(1);
        recovery.LastCommittedByteLength.Should().Be(committedLength);
        recovery.StopReason.Should().Be(SqliteWalRecoveryStopReason.PartialFrame);

        wal.RecoverToLastCommittedFrame().Should().Be(recovery);
        wal.Length.Should().Be(committedLength);
        wal.ScanRecovery().StopReason.Should().Be(SqliteWalRecoveryStopReason.EndOfFile);
        Assert.Throws<ArgumentOutOfRangeException>(() => wal.ReadFrame(2));
    }

    [Test]
    public void WalFileRejectsCorruptFramesAndAppendsOnlyAtAlignedValidEnd()
    {
        var fileSystem = new InMemoryFileSystem();
        var header = CreateHeader();
        using var wal = SqliteWalFile.Create(fileSystem, "main.db-wal", header);

        wal.AppendFrame(1, CreatePage(header.PageSize, 0x41), databaseSizeInPages: 1);
        using (var rawFile = fileSystem.OpenFile("main.db-wal", FileOpenMode.OpenExisting))
            rawFile.Write(SqliteWalHeader.Size + SqliteWalFrameHeader.Size, [0xFF]);

        wal.ScanRecovery().StopReason.Should().Be(SqliteWalRecoveryStopReason.InvalidFrame);
        Assert.Throws<InvalidDataException>(() => wal.ReadFrame(1));
        Assert.Throws<InvalidDataException>(() => wal.AppendFrame(2, CreatePage(header.PageSize, 0x42), 2));
    }

    [Test]
    public void WalFileRejectsFrameSaltMismatchEvenWhenTheChecksumMatches()
    {
        var fileSystem = new InMemoryFileSystem();
        var header = CreateHeader();
        using var wal = SqliteWalFile.Create(fileSystem, "main.db-wal", header);

        wal.AppendFrame(1, CreatePage(header.PageSize, 0x43), databaseSizeInPages: 1);
        using (var rawFile = fileSystem.OpenFile("main.db-wal", FileOpenMode.OpenExisting))
        {
            var frame = new byte[checked((int)wal.FrameSize)];
            rawFile.Read(SqliteWalHeader.Size, frame).Should().Be(frame.Length);
            frame[8] ^= 0x01;
            RewriteFrameChecksum(frame, header);
            rawFile.Write(SqliteWalHeader.Size, frame);
        }

        wal.ScanRecovery().StopReason.Should().Be(SqliteWalRecoveryStopReason.InvalidFrame);
        Assert.Throws<InvalidDataException>(() => wal.ReadFrame(1));
    }

    [Test]
    public void WalFileRestoresAlignmentWhenInjectedAppendWriteFails()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        var header = CreateHeader();
        using var wal = SqliteWalFile.Create(fileSystem, "main.db-wal", header);

        faults.FailNext(FileSystemOperation.Write);
        Assert.Throws<IOException>(() => wal.AppendFrame(1, CreatePage(header.PageSize, 0x51), 1));

        wal.Length.Should().Be(SqliteWalHeader.Size);
        wal.ScanRecovery().Should().Be(new SqliteWalRecoveryInfo(
            LastValidFrameNumber: 0,
            LastCommittedFrameNumber: 0,
            LastCommittedDatabaseSizeInPages: 0,
            LastCommittedByteLength: SqliteWalHeader.Size,
            StopReason: SqliteWalRecoveryStopReason.EndOfFile));

        wal.AppendFrame(1, CreatePage(header.PageSize, 0x52), 1).Should().Be(1);
        wal.ScanRecovery().LastCommittedFrameNumber.Should().Be(1);
    }

    [Test]
    public void WalRecoverySurfacesInjectedTruncationFailureWithoutChangingTheFile()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        var header = CreateHeader();
        using var wal = SqliteWalFile.Create(fileSystem, "main.db-wal", header);

        wal.AppendFrame(1, CreatePage(header.PageSize, 0x61), databaseSizeInPages: 1);
        wal.AppendFrame(2, CreatePage(header.PageSize, 0x62));
        var originalLength = wal.Length;
        faults.FailNext(FileSystemOperation.SetLength);

        Assert.Throws<IOException>(() => wal.RecoverToLastCommittedFrame());
        wal.Length.Should().Be(originalLength);
        wal.ScanRecovery().LastCommittedFrameNumber.Should().Be(1);
        ((wal.Length - SqliteWalHeader.Size) % wal.FrameSize).Should().Be(0);
    }

    private static SqliteWalHeader CreateHeader()
        => SqliteWalHeader.Create(
            SqlitePageSize.Default,
            salt1: 0x0102_0304,
            salt2: 0xA0B0_C0D0,
            checkpointSequence: 7);

    private static byte[] CreatePage(int pageSize, byte fill)
    {
        var page = new byte[pageSize];
        Array.Fill(page, fill);
        return page;
    }

    private static void RewriteHeaderChecksum(byte[] header)
    {
        var checksumByteOrder = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4))
            == SqliteWalHeader.BigEndianChecksumMagic
            ? SqliteWalChecksumByteOrder.BigEndian
            : SqliteWalChecksumByteOrder.LittleEndian;
        var checksum = SqliteWalChecksum.Calculate(header.AsSpan(0, SqliteWalHeader.Size - 8), checksumByteOrder);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24, 4), checksum.First);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(28, 4), checksum.Second);
    }

    private static void RewriteFrameChecksum(byte[] frame, SqliteWalHeader header)
    {
        var afterFrameHeader = SqliteWalChecksum.Calculate(
            frame.AsSpan(0, 8),
            header.ChecksumByteOrder,
            header.Checksum1,
            header.Checksum2);
        var checksum = SqliteWalChecksum.Calculate(
            frame.AsSpan(SqliteWalFrameHeader.Size),
            header.ChecksumByteOrder,
            afterFrameHeader.First,
            afterFrameHeader.Second);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(16, 4), checksum.First);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(20, 4), checksum.Second);
    }
}
