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

        using (var log = MvccLogicalLog.CreateOrOpen(fs, dbPath))
        {
            var store = new MvStore(logicalLog: log);
            var table = store.GetOrCreateTableId("t");
            var tx = store.BeginTransaction();
            store.Insert(tx.Id, new MvccRowId(table, 1), [SqlValue.Text("hello")]);
            store.Insert(tx.Id, new MvccRowId(table, 2), [SqlValue.Integer(7)]);
            store.Commit(tx.Id);
        }

        using var reopened = MvccLogicalLog.CreateOrOpen(fs, dbPath);
        var recovered = new MvStore();
        reopened.ReplayInto(recovered);

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
}
