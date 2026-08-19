using AwesomeAssertions;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

/// <summary>
/// Covers <see cref="SqliteWalFile.AppendFrames(ISqliteWalFrameSource, uint)"/>
/// and <see cref="SqliteWalFile.ReadFrameRange(long, long)"/>, which replace the
/// per-frame append/read loops that made a large commit O(P^2).
/// </summary>
public sealed class SqliteWalBatchAppendTests
{
    private const int PageSize = 512;

    [Test]
    public void BatchAppendProducesTheSameBytesAsSequentialSingleFrameAppends()
    {
        var batchFileSystem = new InMemoryFileSystem();
        var singleFileSystem = new InMemoryFileSystem();
        var header = CreateHeader();
        var pages = CreatePages(12);

        using (var batchWal = SqliteWalFile.Create(batchFileSystem, "batch.db-wal", header))
        {
            var last = batchWal.AppendFrames(new FrameSource(pages), commitDatabaseSizeInPages: 12);
            last.Should().Be(pages.Count);
            batchWal.Flush();
        }

        using (var singleWal = SqliteWalFile.Create(singleFileSystem, "single.db-wal", header))
        {
            for (var index = 0; index < pages.Count; index++)
            {
                singleWal.AppendFrame(
                    pages[index].PageNumber,
                    pages[index].Image,
                    index == pages.Count - 1 ? 12U : 0U);
            }

            singleWal.Flush();
        }

        // Byte-for-byte equality proves the carried checksum chain, salts, and
        // commit marker placement match the per-frame path exactly.
        ReadAllBytes(batchFileSystem, "batch.db-wal")
            .Should().Equal(ReadAllBytes(singleFileSystem, "single.db-wal"));
    }

    [Test]
    public void BatchAppendMarksOnlyTheFinalFrameAsCommitted()
    {
        var fileSystem = new InMemoryFileSystem();
        var pages = CreatePages(5);
        using var wal = SqliteWalFile.Create(fileSystem, "commit-marker.db-wal", CreateHeader());
        wal.AppendFrames(new FrameSource(pages), commitDatabaseSizeInPages: 7);
        wal.Flush();

        var frames = wal.ReadFrameRange(1, pages.Count);
        frames.Should().HaveCount(pages.Count);
        for (var index = 0; index < frames.Count - 1; index++)
            frames[index].Header.IsCommit.Should().BeFalse();
        frames[^1].Header.IsCommit.Should().BeTrue();
        frames[^1].Header.DatabaseSizeInPages.Should().Be(7);

        var recovery = wal.ScanRecovery();
        recovery.StopReason.Should().Be(SqliteWalRecoveryStopReason.EndOfFile);
        recovery.LastCommittedFrameNumber.Should().Be(pages.Count);
        recovery.LastCommittedDatabaseSizeInPages.Should().Be(7);
    }

    [Test]
    public void ReadFrameRangeMatchesPerFrameReadsAndValidatesTheChain()
    {
        var fileSystem = new InMemoryFileSystem();
        var pages = CreatePages(9);
        using var wal = SqliteWalFile.Create(fileSystem, "range.db-wal", CreateHeader());
        wal.AppendFrames(new FrameSource(pages), commitDatabaseSizeInPages: 9);
        wal.Flush();

        var range = wal.ReadFrameRange(3, 7);
        range.Should().HaveCount(5);
        for (var index = 0; index < range.Count; index++)
        {
            var expected = wal.ReadFrame(index + 3);
            range[index].Header.Should().Be(expected.Header);
            range[index].PageData.Should().Equal(expected.PageData);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => wal.ReadFrameRange(0, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => wal.ReadFrameRange(4, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => wal.ReadFrameRange(1, pages.Count + 1));
    }

    [Test]
    public void ReadFrameRangeRejectsACorruptedPrefixFrame()
    {
        var fileSystem = new InMemoryFileSystem();
        var pages = CreatePages(6);
        using (var wal = SqliteWalFile.Create(fileSystem, "corrupt.db-wal", CreateHeader()))
        {
            wal.AppendFrames(new FrameSource(pages), commitDatabaseSizeInPages: 6);
            wal.Flush();
        }

        // Corrupt frame 2's payload; reading frames 4..6 must still fail because
        // the chain is validated from the WAL header forward.
        using (var file = fileSystem.OpenFile("corrupt.db-wal", FileOpenMode.OpenExisting))
        {
            var frameOffset = SqliteWalHeader.Size + SqliteWalFrameHeader.Size + PageSize + SqliteWalFrameHeader.Size;
            file.Write(frameOffset + 4, [0xFF, 0xFF, 0xFF, 0xFF]);
        }

        using var reopened = SqliteWalFile.Open(fileSystem, "corrupt.db-wal");
        Assert.Throws<InvalidDataException>(() => reopened.ReadFrameRange(4, 6));
    }

    [Test]
    public void FailedBatchAppendLeavesOnlyAnUncommittedRecoverableTail()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        var pages = CreatePages(6);
        using var wal = SqliteWalFile.Create(fileSystem, "fault.db-wal", CreateHeader());
        var lengthBefore = wal.Length;

        // Fail the third frame's write; the batch never reaches its commit frame.
        faults.FailOnOccurrence(
            FileSystemOperation.Write,
            faults.GetOperationCount(FileSystemOperation.Write) + 3);
        Assert.Throws<IOException>(() => wal.AppendFrames(new FrameSource(pages), commitDatabaseSizeInPages: 6));
        faults.ClearScheduled();

        // The partial frame is gone and the file is frame-aligned.
        ((wal.Length - SqliteWalHeader.Size) % wal.FrameSize).Should().Be(0);
        wal.Length.Should().BeGreaterThan(lengthBefore);

        var recovery = wal.ScanRecovery();
        recovery.LastCommittedFrameNumber.Should().Be(0);
        recovery.LastValidFrameNumber.Should().BeGreaterThan(0);

        // Recovery removes the uncommitted tail, and a later batch then commits.
        wal.RecoverToLastCommittedFrame();
        wal.Length.Should().Be(lengthBefore);
        wal.AppendFrames(new FrameSource(pages), commitDatabaseSizeInPages: 6);
        wal.Flush();
        wal.ScanRecovery().LastCommittedFrameNumber.Should().Be(pages.Count);
    }

    [Test]
    public void BatchAppendRejectsAnEmptyBatchAndInvalidFrames()
    {
        var fileSystem = new InMemoryFileSystem();
        using var wal = SqliteWalFile.Create(fileSystem, "invalid.db-wal", CreateHeader());

        Assert.Throws<ArgumentException>(
            () => wal.AppendFrames(new FrameSource([]), commitDatabaseSizeInPages: 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => wal.AppendFrames(
                new FrameSource([new BatchPage(0, new byte[PageSize])]),
                commitDatabaseSizeInPages: 1));
        Assert.Throws<ArgumentException>(
            () => wal.AppendFrames(
                new FrameSource([new BatchPage(1, new byte[PageSize + 1])]),
                commitDatabaseSizeInPages: 1));

        // A rejected batch must not have advanced the WAL.
        wal.Length.Should().Be(SqliteWalHeader.Size);
    }

    [Test]
    public void BatchAppendChainsOntoFramesFromAnEarlierCommit()
    {
        var fileSystem = new InMemoryFileSystem();
        using var wal = SqliteWalFile.Create(fileSystem, "chained.db-wal", CreateHeader());
        var first = CreatePages(3);
        var second = CreatePages(4, startPage: 10, fill: 0x5A);

        wal.AppendFrames(new FrameSource(first), commitDatabaseSizeInPages: 3);
        wal.Flush();
        var lastFrame = wal.AppendFrames(new FrameSource(second), commitDatabaseSizeInPages: 14);
        wal.Flush();

        lastFrame.Should().Be(first.Count + second.Count);
        var recovery = wal.ScanRecovery();
        recovery.StopReason.Should().Be(SqliteWalRecoveryStopReason.EndOfFile);
        recovery.LastCommittedFrameNumber.Should().Be(first.Count + second.Count);
        recovery.LastCommittedDatabaseSizeInPages.Should().Be(14);

        var frames = wal.ReadFrameRange(1, recovery.LastCommittedFrameNumber);
        frames[first.Count - 1].Header.IsCommit.Should().BeTrue();
        frames[first.Count].Header.PageNumber.Should().Be(10);
    }

    private static SqliteWalHeader CreateHeader()
        => SqliteWalHeader.Create(PageSize, salt1: 0x1234_5678, salt2: 0x9ABC_DEF0);

    private static List<BatchPage> CreatePages(int count, uint startPage = 1, byte fill = 0x11)
    {
        var pages = new List<BatchPage>(count);
        for (var index = 0; index < count; index++)
        {
            var image = new byte[PageSize];
            Array.Fill(image, unchecked((byte)(fill + index)));
            pages.Add(new BatchPage(startPage + (uint)index, image));
        }

        return pages;
    }

    private static byte[] ReadAllBytes(IFileSystem fileSystem, string path)
    {
        using var file = fileSystem.OpenFile(path, FileOpenMode.OpenExisting, readOnly: true);
        var contents = new byte[checked((int)file.Length)];
        file.Read(0, contents).Should().Be(contents.Length);
        return contents;
    }

    private sealed record BatchPage(uint PageNumber, byte[] Image);

    private sealed class FrameSource(IReadOnlyList<BatchPage> pages) : ISqliteWalFrameSource
    {
        public int Count => pages.Count;

        public uint GetPageNumber(int index) => pages[index].PageNumber;

        public ReadOnlySpan<byte> GetPageImage(int index) => pages[index].Image;
    }
}
