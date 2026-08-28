using AwesomeAssertions;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class StorageReliabilityTests
{
    [Test]
    public void CommittedWalSurvivesPowerLossBeforeFirstCheckpoint()
    {
        var fileSystem = new LiveDurableFileSystem();
        const string databasePath = "before-first-checkpoint.db";
        const string walPath = databasePath + "-wal";
        var committedPage = CreatePage(0x42);

        using (var pager = CreatePager(fileSystem, databasePath, walPath))
        {
            CommitPage(pager, 2, committedPage, SqliteSynchronousMode.Full);
            pager.ReadCommittedPage(2).Should().Equal(committedPage);
            var durable = fileSystem.CaptureDurableSnapshot();
            durable.Files[databasePath].Length.Should().BeGreaterThanOrEqualTo(SqlitePageSize.Default);
            durable.Files[walPath].Length.Should().BeGreaterThan(SqliteWalHeader.Size);
        }

        fileSystem.RestoreAfterPowerLoss();

        using var recovered = SqlitePager.Open(fileSystem, databasePath, walPath);
        recovered.ReadCommittedPage(2).Should().Equal(committedPage);
        recovered.RecoveryInfo.LastCommittedFrameNumber.Should().BeGreaterThan(0);
    }

    [Test]
    public void UnsyncedCommitAndDurableUncommittedFrameNeverBecomeVisible()
    {
        var fileSystem = new LiveDurableFileSystem();
        const string databasePath = "volatile-tail.db";
        const string walPath = databasePath + "-wal";
        var durablePage = CreatePage(0x31);
        var volatilePage = CreatePage(0x72);
        var uncommittedPage = CreatePage(0x93);

        using (var pager = CreatePager(fileSystem, databasePath, walPath))
        {
            CommitPage(pager, 2, durablePage, SqliteSynchronousMode.Full);
            pager.CheckpointToMainStoreAndResetWal();
            CommitPage(pager, 2, volatilePage, SqliteSynchronousMode.Off);
            pager.ReadCommittedPage(2).Should().Equal(volatilePage);
        }

        fileSystem.RestoreAfterPowerLoss();
        using (var recovered = SqlitePager.Open(fileSystem, databasePath, walPath))
        {
            recovered.ReadCommittedPage(2).Should().Equal(durablePage);
            recovered.AppendUncommittedWalFrameForTesting(2, uncommittedPage);
        }

        fileSystem.RestoreAfterPowerLoss();
        using var final = SqlitePager.Open(fileSystem, databasePath, walPath);
        final.ReadCommittedPage(2).Should().Equal(durablePage);
        final.RecoveryInfo.LastCommittedFrameNumber.Should().Be(0);
    }

    [Test]
    public void TornCheckpointRecoversOneCommittedImageWithoutMixedPages()
    {
        var fileSystem = new LiveDurableFileSystem();
        const string databasePath = "torn-checkpoint.db";
        const string walPath = databasePath + "-wal";
        var oldPages = Enumerable.Range(2, 4)
            .ToDictionary(page => (uint)page, page => CreatePage((byte)(0x10 + page)));
        var newPages = Enumerable.Range(2, 4)
            .ToDictionary(page => (uint)page, page => CreatePage((byte)(0x70 + page)));

        using (var pager = CreatePager(fileSystem, databasePath, walPath))
        {
            CommitPages(pager, oldPages, SqliteSynchronousMode.Full);
            pager.CheckpointToMainStoreAndResetWal();
            fileSystem.MarkAllDurable();

            CommitPages(pager, newPages, SqliteSynchronousMode.Full);
            fileSystem.ArmTornFlush(databasePath, 1, 3);
            pager.CheckpointToMainStore(synchronousMode: SqliteSynchronousMode.Full);
        }

        var crash = fileSystem.TakeTornFlushSnapshot();
        crash.SelectedMutationCount.Should().BeGreaterThan(0);
        crash.DroppedMutationCount.Should().BeGreaterThan(0);
        crash.Files.Should().ContainKey(databasePath).WhoseValue.Should().NotBeEmpty();
        crash.Files.Should().ContainKey(walPath).WhoseValue.Length
            .Should().BeGreaterThan(SqliteWalHeader.Size);
        var tornMain = crash.Files[databasePath];
        oldPages.Any(pair => ContainsPageImage(tornMain, pair.Key, pair.Value)).Should().BeTrue();
        newPages.Any(pair => ContainsPageImage(tornMain, pair.Key, pair.Value)).Should().BeTrue();

        fileSystem.RestoreAfterPowerLoss(crash);
        using var recovered = SqlitePager.Open(fileSystem, databasePath, walPath);
        var recoveredPages = newPages.Keys.ToDictionary(
            pageNumber => pageNumber,
            recovered.ReadCommittedPage);
        var isPriorImage = oldPages.All(pair => recoveredPages[pair.Key].SequenceEqual(pair.Value));
        var isCommittedImage = newPages.All(pair => recoveredPages[pair.Key].SequenceEqual(pair.Value));
        (isPriorImage || isCommittedImage).Should().BeTrue(
            "checkpoint recovery must select a complete committed image, never torn page generations");
    }

    [Test]
    public void PowerLossPreservesSidecarsAndOnlyFlushedTruncation()
    {
        var fileSystem = new LiveDurableFileSystem();
        using (var database = fileSystem.OpenFile("state.db", FileOpenMode.CreateNew))
        {
            database.Write(0, new byte[] { 1, 2, 3, 4 });
            database.FlushToDisk();
            database.SetLength(2);
        }
        using (var wal = fileSystem.OpenFile("state.db-wal", FileOpenMode.CreateNew))
        {
            wal.Write(0, new byte[] { 5, 6 });
            wal.FlushToDisk();
        }
        using (var sharedMemory = fileSystem.OpenFile("state.db-shm", FileOpenMode.CreateNew))
        {
            sharedMemory.Write(0, new byte[] { 7, 8 });
            sharedMemory.FlushToDisk();
        }

        fileSystem.DeleteFile("state.db-wal");
        fileSystem.RestoreAfterPowerLoss();

        ReadAll(fileSystem, "state.db").Should().Equal(1, 2, 3, 4);
        fileSystem.FileExists("state.db-wal").Should().BeFalse();
        ReadAll(fileSystem, "state.db-shm").Should().Equal(7, 8);

        using (var database = fileSystem.OpenFile("state.db", FileOpenMode.OpenExisting))
        {
            database.SetLength(2);
            database.FlushToDisk();
        }
        fileSystem.RestoreAfterPowerLoss();
        ReadAll(fileSystem, "state.db").Should().Equal(1, 2);
    }

    private static SqlitePager CreatePager(
        IFileSystem fileSystem,
        string databasePath,
        string walPath)
        => SqlitePager.Create(
            fileSystem,
            databasePath,
            walPath,
            SqliteWalHeader.Create(
                SqlitePageSize.Default,
                salt1: 0x1020_3040,
                salt2: 0x5060_7080,
                checkpointSequence: 17));

    private static void CommitPage(
        SqlitePager pager,
        uint pageNumber,
        byte[] page,
        SqliteSynchronousMode synchronousMode)
        => CommitPages(pager, new Dictionary<uint, byte[]> { [pageNumber] = page }, synchronousMode);

    private static void CommitPages(
        SqlitePager pager,
        IReadOnlyDictionary<uint, byte[]> pages,
        SqliteSynchronousMode synchronousMode)
    {
        using var transaction = pager.BeginTransaction(pages.Keys.Max());
        foreach (var (pageNumber, page) in pages)
            transaction.WritePage(pageNumber, page);
        transaction.Commit(synchronousMode);
    }

    private static byte[] CreatePage(byte fill)
    {
        var page = new byte[SqlitePageSize.Default];
        Array.Fill(page, fill);
        return page;
    }

    private static byte[] ReadAll(IFileSystem fileSystem, string path)
    {
        using var file = fileSystem.OpenFile(path, FileOpenMode.OpenExisting, readOnly: true);
        var bytes = new byte[checked((int)file.Length)];
        file.Read(0, bytes).Should().Be(bytes.Length);
        return bytes;
    }

    private static bool ContainsPageImage(byte[] database, uint pageNumber, byte[] expected)
    {
        var offset = checked((int)(pageNumber - 1) * SqlitePageSize.Default);
        return database.AsSpan(offset, SqlitePageSize.Default).SequenceEqual(expected);
    }
}
