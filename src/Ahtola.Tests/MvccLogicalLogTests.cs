using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Mvcc;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class MvccLogicalLogTests
{
    [Test]
    public void CommitFramesSurviveReopenAndReplay()
    {
        var fs = new InMemoryFileSystem();
        const string dbPath = "mvcc-log.db";
        long table;

        using (var log = MvccLogicalLog.CreateOrOpen(fs, dbPath))
        {
            var store = new MvStore(logicalLog: log);
            table = store.GetOrCreateTableId("t");
            var tx = store.BeginTransaction();
            store.Insert(tx.Id, new MvccRowId(table, 1), [SqlValue.Text("hello")]);
            store.Insert(tx.Id, new MvccRowId(table, 2), [SqlValue.Integer(7)]);
            store.Commit(tx.Id);
        }

        using var reopened = MvccLogicalLog.CreateOrOpen(fs, dbPath);
        var recovered = new MvStore();
        reopened.ReplayInto(recovered);
        recovered.GetOrCreateTableId("t").Should().Be(table);

        var reader = recovered.BeginTransaction();
        // Table name→id map is not durable yet; scan by row id from recovered chains.
        var rows = recovered.ScanVisible(reader.Id);
        rows.Should().HaveCount(2);
        rows.Select(r => r.RowId.RowId).OrderBy(x => x).Should().Equal(1L, 2L);
        rows.Single(r => r.RowId.RowId == 1).Cells[0].Should().Be(SqlValue.Text("hello"));
        rows.Single(r => r.RowId.RowId == 2).Cells[0].Should().Be(SqlValue.Integer(7));
    }

    [Test]
    public void TruncateAfterCheckpointDropsFrames()
    {
        var fs = new InMemoryFileSystem();
        const string dbPath = "mvcc-ckpt.db";

        using (var log = MvccLogicalLog.CreateOrOpen(fs, dbPath))
        {
            var store = new MvStore(logicalLog: log);
            var table = store.GetOrCreateTableId("t");
            var tx = store.BeginTransaction();
            store.Insert(tx.Id, new MvccRowId(table, 1), [SqlValue.Integer(1)]);
            store.Commit(tx.Id);
            log.Offset.Should().BeGreaterThan(56);
            log.TruncateAfterCheckpoint();
            log.Offset.Should().Be(56);
        }

        using var reopened = MvccLogicalLog.CreateOrOpen(fs, dbPath);
        var recovered = new MvStore();
        reopened.ReplayInto(recovered);
        var reader = recovered.BeginTransaction();
        recovered.ScanVisible(reader.Id).Should().BeEmpty();
    }

    [Test]
    public void DeleteOpsReplayAsTombstones()
    {
        var fs = new InMemoryFileSystem();
        const string dbPath = "mvcc-del.db";

        long tableId;
        using (var log = MvccLogicalLog.CreateOrOpen(fs, dbPath))
        {
            var store = new MvStore(logicalLog: log);
            tableId = store.GetOrCreateTableId("t");
            var seed = store.BeginTransaction();
            store.Insert(seed.Id, new MvccRowId(tableId, 1), [SqlValue.Integer(1)]);
            store.Commit(seed.Id);

            var del = store.BeginTransaction();
            store.Delete(del.Id, new MvccRowId(tableId, 1)).Should().BeTrue();
            store.Commit(del.Id);
        }

        using var reopened = MvccLogicalLog.CreateOrOpen(fs, dbPath);
        var recovered = new MvStore();
        reopened.ReplayInto(recovered);
        var reader = recovered.BeginTransaction();
        recovered.ScanVisible(reader.Id).Should().BeEmpty();
    }

    [Test]
    public void TypedPrimaryKeyFramesSurviveReopenAndReplay()
    {
        var fs = new InMemoryFileSystem();
        const string dbPath = "mvcc-typed-log.db";
        var key = MvccKey.FromRecord(SqliteRecordCodec.Encode(
            [SqlValue.Text("tenant"), SqlValue.Integer(7)]));

        using (var log = MvccLogicalLog.CreateOrOpen(fs, dbPath))
        {
            var store = new MvStore(logicalLog: log);
            var table = store.GetOrCreateTableId("items");
            var tx = store.BeginTransaction();
            store.Insert(tx.Id, new MvccRowId(table, key), [SqlValue.Text("value")]);
            store.Commit(tx.Id);
        }

        using var reopened = MvccLogicalLog.CreateOrOpen(fs, dbPath);
        var recovered = new MvStore();
        reopened.ReplayInto(recovered);

        var reader = recovered.BeginTransaction();
        recovered.TryRead(reader.Id, new MvccRowId(-2, key), out var cells).Should().BeTrue();
        cells!.Should().Equal(SqlValue.Text("value"));
    }

    [Test]
    public void LegacyHeaderUpgradesOnlyAfterTheLogIsHeaderOnly()
    {
        var fs = new InMemoryFileSystem();
        const string dbPath = "mvcc-v3-upgrade.db";
        var logPath = MvccLogicalLog.LogPathForDatabase(dbPath);
        var header = new byte[56];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header, 0x4C4D4C32);
        header[4] = 3;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)header.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), 123UL);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(52), Crc32C.Compute(header));
        using (var file = fs.OpenFile(logPath, FileOpenMode.CreateNew))
        {
            file.Write(0, header);
            file.FlushToDisk();
        }

        using var log = MvccLogicalLog.CreateOrOpen(fs, dbPath);
        log.RequiresVersion4Upgrade.Should().BeTrue();
        log.TruncateAfterCheckpoint();
        log.UpgradeToVersion4AfterCheckpoint();
        log.RequiresVersion4Upgrade.Should().BeFalse();
    }

    [Test]
    public void RecoveryTruncatesATornTailBeforeAcceptingAnotherCommit()
    {
        var fs = new InMemoryFileSystem();
        const string dbPath = "mvcc-torn-tail.db";
        long table;

        using (var log = MvccLogicalLog.CreateOrOpen(fs, dbPath))
        {
            var store = new MvStore(logicalLog: log);
            table = store.GetOrCreateTableId("items");
            var tx = store.BeginTransaction();
            store.Insert(tx.Id, new MvccRowId(table, 1), [SqlValue.Text("first")]);
            store.Commit(tx.Id);
        }

        var logPath = MvccLogicalLog.LogPathForDatabase(dbPath);
        using (var file = fs.OpenFile(logPath, FileOpenMode.OpenExisting))
        {
            file.Write(file.Length, [0x4D, 0x56, 0x54]);
            file.FlushToDisk();
        }

        using (var recoveredLog = MvccLogicalLog.CreateOrOpen(fs, dbPath))
        {
            var recovered = new MvStore(logicalLog: recoveredLog);
            recoveredLog.ReplayInto(recovered);
            recovered.GetOrCreateTableId("items").Should().Be(table);
            var tx = recovered.BeginTransaction();
            recovered.Insert(tx.Id, new MvccRowId(table, 2), [SqlValue.Text("second")]);
            recovered.Commit(tx.Id);
        }

        using var reopenedLog = MvccLogicalLog.CreateOrOpen(fs, dbPath);
        var reopened = new MvStore();
        reopenedLog.ReplayInto(reopened);
        var reader = reopened.BeginTransaction();
        reopened.ScanVisible(reader.Id)
            .Select(row => row.Cells[0].AsText())
            .Should()
            .BeEquivalentTo(["first", "second"]);
    }

    [Test]
    public void FlushFailurePoisonsTheLiveStoreButRecoveryHonorsTheWrittenFrame()
    {
        var faults = new DeterministicFaultInjector();
        var fs = new InMemoryFileSystem(faults);
        const string dbPath = "mvcc-indeterminate-commit.db";
        long table;

        using (var log = MvccLogicalLog.CreateOrOpen(fs, dbPath))
        {
            var store = new MvStore(logicalLog: log);
            table = store.GetOrCreateTableId("items");
            var tx = store.BeginTransaction();
            store.Insert(tx.Id, new MvccRowId(table, 1), [SqlValue.Text("durable")]);
            faults.FailNext(FileSystemOperation.FlushToDisk);

            var commit = () => store.Commit(tx.Id);
            commit.Should().Throw<MvccLogicalLogCommitIndeterminateException>();
            var begin = () => store.BeginTransaction();
            begin.Should().Throw<EmbeddedSqlException>()
                .WithMessage("*indeterminate logical-log commit*");
            faults.ClearScheduled();
        }

        using var reopenedLog = MvccLogicalLog.CreateOrOpen(fs, dbPath);
        var reopened = new MvStore();
        reopenedLog.ReplayInto(reopened);
        var reader = reopened.BeginTransaction();
        reopened.TryRead(reader.Id, new MvccRowId(table, 1), out var cells).Should().BeTrue();
        cells!.Should().Equal(SqlValue.Text("durable"));
    }

    [Test]
    public void ReplayPreservesABaseOnlyDeleteAsATombstone()
    {
        var fs = new InMemoryFileSystem();
        const string dbPath = "mvcc-base-tombstone.db";
        long table;

        using (var log = MvccLogicalLog.CreateOrOpen(fs, dbPath))
        {
            var store = new MvStore(logicalLog: log);
            table = store.GetOrCreateTableId("items");
            var tx = store.BeginTransaction();
            store.DeleteOrTombstoneBase(tx.Id, new MvccRowId(table, 1));
            store.Commit(tx.Id);
        }

        using var reopenedLog = MvccLogicalLog.CreateOrOpen(fs, dbPath);
        var reopened = new MvStore();
        reopenedLog.ReplayInto(reopened);
        var reader = reopened.BeginTransaction();
        reopened.IsBaseRowInvalidated(reader.Id, new MvccRowId(table, 1)).Should().BeTrue();
    }
}
