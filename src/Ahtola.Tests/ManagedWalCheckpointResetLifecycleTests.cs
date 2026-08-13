using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public class ManagedWalCheckpointResetLifecycleTests
{
    [Test]
    public void ManagedCatalogRewritesKeepWalBoundedAndReopenFromMainStore()
    {
        var fileSystem = new InMemoryFileSystem();
        const string databasePath = "managed-wal-bounded.db";

        using (var database = EmbeddedDatabase.OpenFile(databasePath, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE entries(id INTEGER PRIMARY KEY, value TEXT);");
            AssertEmptyWal(fileSystem, databasePath);

            for (var id = 1; id <= 12; id++)
            {
                Execute(connection, $"INSERT INTO entries VALUES ({id}, 'value-{id}');");
                AssertEmptyWal(fileSystem, databasePath);
            }
        }

        using var reopened = EmbeddedDatabase.OpenFile(databasePath, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Scalar(reopenedConnection, "SELECT COUNT(*) FROM entries;").Should().Be(12);
        Scalar(reopenedConnection, "SELECT id FROM entries WHERE value = 'value-12';").Should().Be(12);
    }

    [Test]
    public void ResetLeavesCommittedWalRecoverableWhenMainStoreFlushFails()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string databasePath = "main-store-flush-failure.db";
        const string walPath = databasePath + "-wal";
        var page = CreatePage(SqlitePageSize.Default, 0x41);
        long committedWalLength;

        using (var pager = CreatePager(fileSystem, databasePath, walPath))
        {
            CommitPageTwo(pager, page);
            committedWalLength = ReadFileLength(fileSystem, walPath);

            faults.FailNext(FileSystemOperation.FlushToDisk);
            Assert.Throws<IOException>(() => pager.CheckpointToMainStoreAndResetWal());

            pager.State.Should().Be(SqlitePagerState.Faulted);
            ReadFileLength(fileSystem, walPath).Should().Be(committedWalLength);
            using var wal = SqliteWalFile.Open(fileSystem, walPath, readOnly: true);
            wal.ScanRecovery().LastCommittedFrameNumber.Should().Be(1);
        }

        using var recovered = SqlitePager.Open(fileSystem, databasePath, walPath);
        recovered.ReadCommittedPage(2).Should().Equal(page);
    }

    [Test]
    public void ResetFailureAtWalTruncationKeepsCommittedFramesAndData()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string databasePath = "wal-truncation-failure.db";
        const string walPath = databasePath + "-wal";
        var page = CreatePage(SqlitePageSize.Default, 0x51);
        long committedWalLength;

        using (var pager = CreatePager(fileSystem, databasePath, walPath))
        {
            CommitPageTwo(pager, page);
            committedWalLength = ReadFileLength(fileSystem, walPath);

            faults.FailNext(FileSystemOperation.SetLength);
            Assert.Throws<IOException>(() => pager.CheckpointToMainStoreAndResetWal());

            pager.State.Should().Be(SqlitePagerState.Faulted);
            ReadFileLength(fileSystem, walPath).Should().Be(committedWalLength);
            using var wal = SqliteWalFile.Open(fileSystem, walPath, readOnly: true);
            wal.ScanRecovery().LastCommittedFrameNumber.Should().Be(1);
        }

        using var recovered = SqlitePager.Open(fileSystem, databasePath, walPath);
        recovered.ReadCommittedPage(2).Should().Equal(page);
    }

    [Test]
    public void ResetRejectsCorruptWalWithoutTruncatingItsFrames()
    {
        var fileSystem = new InMemoryFileSystem();
        const string databasePath = "corrupt-reset.db";
        const string walPath = databasePath + "-wal";

        using var pager = CreatePager(fileSystem, databasePath, walPath);
        CommitPageTwo(pager, CreatePage(pager.PageSize, 0x61));
        var walLength = ReadFileLength(fileSystem, walPath);
        using (var rawWal = fileSystem.OpenFile(walPath, FileOpenMode.OpenExisting))
            rawWal.Write(SqliteWalHeader.Size + SqliteWalFrameHeader.Size, [0xFF]);

        Assert.Throws<InvalidDataException>(() => pager.CheckpointToMainStoreAndResetWal());

        pager.State.Should().Be(SqlitePagerState.Faulted);
        ReadFileLength(fileSystem, walPath).Should().Be(walLength);
        using var wal = SqliteWalFile.Open(fileSystem, walPath, readOnly: true);
        wal.ScanRecovery().StopReason.Should().Be(SqliteWalRecoveryStopReason.InvalidFrame);
    }

    [Test]
    public void ResetUsesExclusiveCheckpointLockBeforeReclaimingFrames()
    {
        var fileSystem = new InMemoryFileSystem();
        const string databasePath = "checkpoint-lock.db";
        const string walPath = databasePath + "-wal";

        using var pager = CreatePager(fileSystem, databasePath, walPath);
        CommitPageTwo(pager, CreatePage(pager.PageSize, 0x71));
        var walLength = ReadFileLength(fileSystem, walPath);
        using var snapshot = pager.BeginReadTransaction();

        Assert.Throws<SqlitePagerBusyException>(() => pager.CheckpointToMainStoreAndResetWal());

        pager.State.Should().Be(SqlitePagerState.Ready);
        ReadFileLength(fileSystem, walPath).Should().Be(walLength);
        using var wal = SqliteWalFile.Open(fileSystem, walPath, readOnly: true);
        wal.ScanRecovery().LastCommittedFrameNumber.Should().Be(1);
    }

    [Test]
    public void ResetReclaimsEncryptedWalAndReopensItsDurableMainStore()
    {
        var fileSystem = new InMemoryFileSystem();
        const string databasePath = "encrypted-reset.db";
        const string walPath = databasePath + "-wal";
        var page = CreatePage(SqlitePageSize.Default, 0x81, AhtolaPageEncryptionMetadataSize);
        using var encryption = new AhtolaEncryptionOptions(
            AhtolaEncryptionCipher.Aes256Gcm,
            Convert.FromHexString("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F"));

        using (var pager = SqlitePager.Create(
                   fileSystem,
                   databasePath,
                   walPath,
                   CreateWalHeader(),
                   encryption: encryption))
        {
            CommitPageTwo(pager, page);

            var checkpoint = pager.CheckpointToMainStoreAndResetWal();
            checkpoint.RetainedCommittedFrameCount.Should().Be(0);
            pager.RecoveryInfo.LastCommittedFrameNumber.Should().Be(0);
            ReadFileLength(fileSystem, walPath).Should().Be(SqliteWalHeader.Size);
        }

        using var reopened = SqlitePager.Open(fileSystem, databasePath, walPath, encryption: encryption);
        reopened.ReadCommittedPage(2).Should().Equal(page);
        reopened.RecoveryInfo.LastCommittedFrameNumber.Should().Be(0);
    }

    private static void AssertEmptyWal(IFileSystem fileSystem, string databasePath)
    {
        var walPath = databasePath + "-wal";
        ReadFileLength(fileSystem, walPath).Should().Be(SqliteWalHeader.Size);
        using var wal = SqliteWalFile.Open(fileSystem, walPath, readOnly: true);
        wal.ScanRecovery().Should().Be(new SqliteWalRecoveryInfo(
            LastValidFrameNumber: 0,
            LastCommittedFrameNumber: 0,
            LastCommittedDatabaseSizeInPages: 0,
            LastCommittedByteLength: SqliteWalHeader.Size,
            StopReason: SqliteWalRecoveryStopReason.EndOfFile));
    }

    private static SqlitePager CreatePager(IFileSystem fileSystem, string databasePath, string walPath)
        => SqlitePager.Create(fileSystem, databasePath, walPath, CreateWalHeader());

    private static SqliteWalHeader CreateWalHeader()
        => SqliteWalHeader.Create(
            SqlitePageSize.Default,
            salt1: 0x1020_3040,
            salt2: 0x5060_7080,
            checkpointSequence: 11);

    private static void CommitPageTwo(SqlitePager pager, byte[] page)
    {
        using var transaction = pager.BeginTransaction(targetDatabaseSizeInPages: 2);
        transaction.WritePage(2, page);
        transaction.Commit();
    }

    private static byte[] CreatePage(int pageSize, byte fill, int reservedSpace = 0)
    {
        var page = new byte[pageSize];
        page.AsSpan(0, pageSize - reservedSpace).Fill(fill);
        return page;
    }

    private static long ReadFileLength(IFileSystem fileSystem, string path)
    {
        using var file = fileSystem.OpenFile(path, FileOpenMode.OpenExisting, readOnly: true);
        return file.Length;
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private static long Scalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private const int AhtolaPageEncryptionMetadataSize = 28;
}
