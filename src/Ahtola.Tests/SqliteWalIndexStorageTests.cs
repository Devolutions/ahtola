using System.Buffers.Binary;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class SqliteWalIndexStorageTests
{
    [Test]
    public void WalIndexHeaderParsesNativeLayoutAndPreservesWalSaltByteOrder()
    {
        var bytes = CreateHeader(
            changeCounter: 0x1020_3040,
            pageSize: 4_096,
            maximumFrame: 12,
            databasePageCount: 3,
            frameChecksum1: 0x1122_3344,
            frameChecksum2: 0x5566_7788,
            salt1: 0x99AA_BBCC,
            salt2: 0xDDEE_FF00,
            walChecksumByteOrder: SqliteWalChecksumByteOrder.BigEndian);

        var header = SqliteWalIndexHeader.Parse(bytes);

        header.ChangeCounter.Should().Be(0x1020_3040);
        header.WalChecksumByteOrder.Should().Be(SqliteWalChecksumByteOrder.BigEndian);
        header.PageSize.Should().Be(4_096);
        header.MaximumFrame.Should().Be(12);
        header.DatabasePageCount.Should().Be(3);
        header.FrameChecksum1.Should().Be(0x1122_3344);
        header.FrameChecksum2.Should().Be(0x5566_7788);
        header.Salt1.Should().Be(0x99AA_BBCC);
        header.Salt2.Should().Be(0xDDEE_FF00);
    }

    [Test]
    public void WalIndexHeaderRejectsMalformedFieldsEvenWhenTheirChecksumsAreRecomputed()
    {
        var bytes = CreateHeader();

        bytes[40] ^= 0x01;
        Assert.Throws<InvalidDataException>(() => SqliteWalIndexHeader.Parse(bytes));

        bytes = CreateHeader();
        WriteUInt32Native(bytes, offset: 0, value: SqliteWalIndexHeader.CurrentFormatVersion + 1);
        RewriteHeaderChecksum(bytes);
        Assert.Throws<InvalidDataException>(() => SqliteWalIndexHeader.Parse(bytes));

        bytes = CreateHeader();
        WriteUInt32Native(bytes, offset: 4, value: 1);
        RewriteHeaderChecksum(bytes);
        Assert.Throws<InvalidDataException>(() => SqliteWalIndexHeader.Parse(bytes));

        bytes = CreateHeader();
        bytes[12] = 0;
        RewriteHeaderChecksum(bytes);
        Assert.Throws<InvalidDataException>(() => SqliteWalIndexHeader.Parse(bytes));

        bytes = CreateHeader();
        bytes[13] = 2;
        RewriteHeaderChecksum(bytes);
        Assert.Throws<InvalidDataException>(() => SqliteWalIndexHeader.Parse(bytes));

        bytes = CreateHeader();
        WriteUInt16Native(bytes, offset: 14, value: 513);
        RewriteHeaderChecksum(bytes);
        Assert.Throws<InvalidDataException>(() => SqliteWalIndexHeader.Parse(bytes));

        bytes = CreateHeader(maximumFrame: 1, databasePageCount: 0);
        Assert.Throws<InvalidDataException>(() => SqliteWalIndexHeader.Parse(bytes));
        Assert.Throws<InvalidDataException>(() => SqliteWalIndexHeader.Parse(new byte[SqliteWalIndexHeader.Size - 1]));
    }

    [Test]
    public void WalIndexHeaderRegionRejectsTornAndStalePublicationStates()
    {
        var current = CreateHeader(maximumFrame: 8, databasePageCount: 2);
        var region = CreateHeaderRegion(current, nBackfill: 4, nBackfillAttempted: 6);

        var parsed = SqliteWalIndexHeaderRegion.Parse(region);
        parsed.Header.MaximumFrame.Should().Be(8);
        parsed.CheckpointInfo.BackfilledFrameCount.Should().Be(4);
        parsed.CheckpointInfo.BackfillAttemptedFrameCount.Should().Be(6);

        var next = CreateHeader(changeCounter: 2, maximumFrame: 9, databasePageCount: 2);
        next.CopyTo(region, SqliteWalIndexHeader.Size);
        Assert.Throws<InvalidDataException>(() => SqliteWalIndexHeaderRegion.Parse(region));

        region = CreateHeaderRegion(current, nBackfill: 9, nBackfillAttempted: 9);
        Assert.Throws<InvalidDataException>(() => SqliteWalIndexHeaderRegion.Parse(region));

        region = CreateHeaderRegion(current, nBackfill: 4, nBackfillAttempted: 9);
        Assert.Throws<InvalidDataException>(() => SqliteWalIndexHeaderRegion.Parse(region));
    }

    [Test]
    public void WalIndexLayoutMapsFirstAndSubsequentBlocksWithoutOverlappingTheHeader()
    {
        SqliteWalIndexLayout.GetPageNumberOffset(1).Should().Be(SqliteWalIndexLayout.HeaderRegionSize);
        SqliteWalIndexLayout.GetPageNumberOffset(4_062).Should().Be(16_380);
        SqliteWalIndexLayout.GetHashSlotOffset(0, 0).Should().Be(16_384);
        SqliteWalIndexLayout.GetPageNumberOffset(4_063).Should().Be(SqliteWalIndexLayout.BlockSize);
        SqliteWalIndexLayout.GetHashSlotOffset(1, 0).Should().Be(49_152);
        SqliteWalIndexLayout.GetRequiredBlockCount(4_062).Should().Be(1);
        SqliteWalIndexLayout.GetRequiredBlockCount(4_063).Should().Be(2);

        Assert.Throws<ArgumentOutOfRangeException>(() => SqliteWalIndexLayout.GetPageNumberOffset(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SqliteWalIndexLayout.GetHashSlotOffset(0, 8_192));
    }

    [Test]
    [NonParallelizable]
    public void SqliteProducedWalIndexMatchesTheIndependentWalRecoveryScan()
    {
        var workDirectory = CreateWorkDirectory();
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            using var connection = new SqliteConnection(
                $"Data Source={databasePath};Mode=ReadWriteCreate;Pooling=False");
            connection.Open();
            Execute(connection, "PRAGMA journal_mode=WAL;");
            Execute(connection, "CREATE TABLE data(value TEXT NOT NULL);");
            Execute(connection, "INSERT INTO data VALUES ('one'), ('two');");

            var sharedMemory = ReadAllBytesSharingWithSqlite(databasePath + "-shm");
            var index = SqliteWalIndexHeaderRegion.Parse(sharedMemory);
            var walFileSystem = new InMemoryFileSystem();
            using (var walCopy = walFileSystem.OpenFile("main.db-wal", FileOpenMode.CreateNew))
                walCopy.Write(0, ReadAllBytesSharingWithSqlite(databasePath + "-wal"));
            using var wal = SqliteWalFile.Open(walFileSystem, "main.db-wal", readOnly: true);
            var recovery = wal.ScanRecovery();
            var lastCommittedFrame = wal.ReadFrame(recovery.LastCommittedFrameNumber).Header;

            index.Header.MaximumFrame.Should().Be(checked((uint)recovery.LastCommittedFrameNumber));
            index.Header.DatabasePageCount.Should().Be(recovery.LastCommittedDatabaseSizeInPages);
            index.Header.FrameChecksum1.Should().Be(lastCommittedFrame.Checksum1);
            index.Header.FrameChecksum2.Should().Be(lastCommittedFrame.Checksum2);
            index.Header.Salt1.Should().Be(wal.Header.Salt1);
            index.Header.Salt2.Should().Be(wal.Header.Salt2);
            index.CheckpointInfo.BackfilledFrameCount.Should().BeLessThanOrEqualTo(index.Header.MaximumFrame);
            index.CheckpointInfo.BackfillAttemptedFrameCount.Should().BeLessThanOrEqualTo(index.Header.MaximumFrame);
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    private static byte[] CreateHeader(
        uint changeCounter = 1,
        int pageSize = 4_096,
        uint maximumFrame = 1,
        uint databasePageCount = 1,
        uint frameChecksum1 = 0x0102_0304,
        uint frameChecksum2 = 0xA0B0_C0D0,
        uint salt1 = 0x1122_3344,
        uint salt2 = 0x5566_7788,
        SqliteWalChecksumByteOrder walChecksumByteOrder = SqliteWalChecksumByteOrder.LittleEndian)
    {
        var bytes = new byte[SqliteWalIndexHeader.Size];
        WriteUInt32Native(bytes, offset: 0, SqliteWalIndexHeader.CurrentFormatVersion);
        WriteUInt32Native(bytes, offset: 8, changeCounter);
        bytes[12] = 1;
        bytes[13] = walChecksumByteOrder == SqliteWalChecksumByteOrder.BigEndian ? (byte)1 : (byte)0;
        WriteUInt16Native(bytes, offset: 14, pageSize == SqlitePageSize.Maximum ? (ushort)1 : checked((ushort)pageSize));
        WriteUInt32Native(bytes, offset: 16, maximumFrame);
        WriteUInt32Native(bytes, offset: 20, databasePageCount);
        WriteUInt32Native(bytes, offset: 24, frameChecksum1);
        WriteUInt32Native(bytes, offset: 28, frameChecksum2);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(32, 4), salt1);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(36, 4), salt2);
        RewriteHeaderChecksum(bytes);
        return bytes;
    }

    private static byte[] CreateHeaderRegion(byte[] header, uint nBackfill, uint nBackfillAttempted)
    {
        var region = new byte[SqliteWalIndexLayout.HeaderRegionSize];
        header.CopyTo(region, 0);
        header.CopyTo(region, SqliteWalIndexHeader.Size);
        WriteUInt32Native(region, offset: 96, nBackfill);
        WriteUInt32Native(region, offset: 128, nBackfillAttempted);
        return region;
    }

    private static void RewriteHeaderChecksum(byte[] header)
    {
        var checksumByteOrder = SqliteWalIndexHeader.NativeByteOrder == SqliteWalIndexByteOrder.LittleEndian
            ? SqliteWalChecksumByteOrder.LittleEndian
            : SqliteWalChecksumByteOrder.BigEndian;
        var checksum = SqliteWalChecksum.Calculate(header.AsSpan(0, 40), checksumByteOrder);
        WriteUInt32Native(header, offset: 40, checksum.First);
        WriteUInt32Native(header, offset: 44, checksum.Second);
    }

    private static void WriteUInt32Native(byte[] destination, int offset, uint value)
    {
        if (SqliteWalIndexHeader.NativeByteOrder == SqliteWalIndexByteOrder.LittleEndian)
            BinaryPrimitives.WriteUInt32LittleEndian(destination.AsSpan(offset, sizeof(uint)), value);
        else
            BinaryPrimitives.WriteUInt32BigEndian(destination.AsSpan(offset, sizeof(uint)), value);
    }

    private static void WriteUInt16Native(byte[] destination, int offset, ushort value)
    {
        if (SqliteWalIndexHeader.NativeByteOrder == SqliteWalIndexByteOrder.LittleEndian)
            BinaryPrimitives.WriteUInt16LittleEndian(destination.AsSpan(offset, sizeof(ushort)), value);
        else
            BinaryPrimitives.WriteUInt16BigEndian(destination.AsSpan(offset, sizeof(ushort)), value);
    }

    private static void Execute(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static byte[] ReadAllBytesSharingWithSqlite(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > int.MaxValue)
            throw new InvalidDataException($"SQLite test artifact '{path}' is too large to snapshot.");

        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static string CreateWorkDirectory()
    {
        var directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "sqlite-wal-index",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteWorkDirectory(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
