using System.Buffers.Binary;
using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Mvcc;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

/// <summary>
/// Version 5 MVCC logical logs chain each frame's CRC32C from the previous one, seeded by
/// <c>crc32c(salt)</c>, like Turso's <c>logical_log.rs</c>. A V4 log (independent CRCs)
/// stays readable and appendable, and adopts chaining once a checkpoint truncates it.
/// </summary>
public sealed class MvccLogicalLogChainedCrcTests
{
    [Test]
    public void NewLogsChainFrameCrcsFromTheSalt()
    {
        var fs = new InMemoryFileSystem();
        const string dbPath = "chain-new.db";
        CommitRows(fs, dbPath, 1, 2, 3);

        var image = ReadAll(fs, dbPath);
        image[4].Should().Be(MvccLogicalLogFormat.ChainedVersion);
        var frames = Frames(image);
        frames.Should().HaveCount(3);
        var running = MvccLogTestCrc.InitialCrc(image);
        foreach (var (offset, length) in frames)
        {
            var trailer = offset + length - MvccLogicalLogFormat.TxTrailerSize;
            var stored = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(trailer));
            stored.Should().Be(Crc32C.Append(running, image.AsSpan(offset, trailer - offset)));
            stored.Should().NotBe(Crc32C.Compute(image.AsSpan(offset, trailer - offset)));
            running = stored;
        }

        Replay(fs, dbPath).Should().Equal(1L, 2L, 3L);
    }

    [Test]
    public void ReorderedFramesNoLongerValidate()
    {
        var fs = new InMemoryFileSystem();
        const string dbPath = "chain-reorder.db";
        CommitRows(fs, dbPath, 1, 2);

        // Two frames of the same length, swapped: each one keeps a CRC that is valid on its
        // own, which an unchained log would accept.
        var image = ReadAll(fs, dbPath);
        var frames = Frames(image);
        frames[0].Length.Should().Be(frames[1].Length);
        var first = image.AsSpan(frames[0].Offset, frames[0].Length).ToArray();
        image.AsSpan(frames[1].Offset, frames[1].Length).CopyTo(image.AsSpan(frames[0].Offset));
        first.CopyTo(image.AsSpan(frames[1].Offset));
        WriteAll(fs, dbPath, image);

        var replay = () => Replay(fs, dbPath);
        replay.Should().Throw<InvalidDataException>().WithMessage("*CRC mismatch*");
    }

    [Test]
    public void AppendingAfterAnOpenWithoutReplayContinuesTheChain()
    {
        var fs = new InMemoryFileSystem();
        const string dbPath = "chain-append.db";
        var table = CommitRows(fs, dbPath, 1);

        // Reopen and append without replaying first: the log finds the chain from the last
        // frame's stored CRC.
        using (var log = MvccLogicalLog.CreateOrOpen(fs, dbPath))
            log.AppendCommit(10, [MvccLogOp.Upsert(new MvccRowId(table, 2), [SqlValue.Integer(2)], "t")]);

        Replay(fs, dbPath).Should().Equal(1L, 2L);
    }

    [Test]
    public void VersionFourLogsKeepIndependentCrcsUntilACheckpointTruncatesThem()
    {
        var fs = new InMemoryFileSystem();
        const string dbPath = "chain-v4.db";
        var logPath = MvccLogicalLog.LogPathForDatabase(dbPath);
        using (var file = fs.OpenFile(logPath, FileOpenMode.CreateNew))
        {
            file.Write(0, BuildHeader(version: 4, salt: 99));
            file.FlushToDisk();
        }

        CommitRows(fs, dbPath, 1, 2);
        var image = ReadAll(fs, dbPath);
        image[4].Should().Be(4);
        foreach (var (offset, length) in Frames(image))
        {
            var trailer = offset + length - MvccLogicalLogFormat.TxTrailerSize;
            BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(trailer))
                .Should().Be(Crc32C.Compute(image.AsSpan(offset, trailer - offset)));
        }

        Replay(fs, dbPath).Should().Equal(1L, 2L);

        using (var log = MvccLogicalLog.CreateOrOpen(fs, dbPath))
        {
            log.ReplayInto(new MvStore());
            log.TruncateAfterCheckpoint();
        }

        ReadAll(fs, dbPath)[4].Should().Be(MvccLogicalLogFormat.ChainedVersion);
        CommitRows(fs, dbPath, 3);
        Replay(fs, dbPath).Should().Equal(3L);
    }

    [Test]
    public void CrcAppendMatchesComputingOverConcatenatedBytes()
    {
        var a = "turso"u8.ToArray();
        var b = "ahtola logical log"u8.ToArray();
        Crc32C.Append(Crc32C.Compute(a), b).Should().Be(Crc32C.Compute([.. a, .. b]));
    }

    private static long CommitRows(InMemoryFileSystem fs, string dbPath, params long[] rowIds)
    {
        using var log = MvccLogicalLog.CreateOrOpen(fs, dbPath);
        var store = new MvStore(logicalLog: log);
        log.ReplayInto(store);
        var table = store.GetOrCreateTableId("t");
        foreach (var rowId in rowIds)
        {
            var tx = store.BeginTransaction();
            store.Insert(tx.Id, new MvccRowId(table, rowId), [SqlValue.Integer(rowId)]);
            store.Commit(tx.Id);
        }

        return table;
    }

    private static long[] Replay(InMemoryFileSystem fs, string dbPath)
    {
        using var log = MvccLogicalLog.CreateOrOpen(fs, dbPath);
        var store = new MvStore();
        log.ReplayInto(store);
        var reader = store.BeginTransaction();
        return [.. store.ScanVisible(reader.Id).Select(static row => row.RowId.RowId).OrderBy(static id => id)];
    }

    private static List<(int Offset, int Length)> Frames(byte[] image)
    {
        var frames = new List<(int Offset, int Length)>();
        var position = MvccLogicalLogFormat.LogHeaderSize;
        while (position < image.Length)
        {
            var (payloadSize, _, _) = MvccLogicalLogFormat.ReadFrameHeader(
                image.AsSpan(position, MvccLogicalLogFormat.TxHeaderSize));
            var length = MvccLogicalLogFormat.TxHeaderSize + payloadSize + MvccLogicalLogFormat.TxTrailerSize;
            frames.Add((position, length));
            position += length;
        }

        return frames;
    }

    private static byte[] BuildHeader(byte version, ulong salt)
    {
        var header = new byte[MvccLogicalLogFormat.LogHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, MvccLogicalLogFormat.LogMagic);
        header[4] = version;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)header.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(MvccLogicalLogFormat.LogHeaderSaltStart), salt);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(MvccLogicalLogFormat.LogHeaderCrcStart),
            Crc32C.Compute(header));
        return header;
    }

    private static byte[] ReadAll(InMemoryFileSystem fs, string dbPath)
    {
        using var file = fs.OpenFile(MvccLogicalLog.LogPathForDatabase(dbPath), FileOpenMode.OpenExisting);
        var bytes = new byte[checked((int)file.Length)];
        file.Read(0, bytes).Should().Be(bytes.Length);
        return bytes;
    }

    private static void WriteAll(InMemoryFileSystem fs, string dbPath, byte[] bytes)
    {
        using var file = fs.OpenFile(MvccLogicalLog.LogPathForDatabase(dbPath), FileOpenMode.OpenExisting);
        file.Write(0, bytes);
        file.FlushToDisk();
    }
}
