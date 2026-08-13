using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Mvcc;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class MvccHeaderAndDualCursorTests
{
    [Test]
    public void FileBackedMvccPersistsHeaderVersionAcrossReopen()
    {
        const string path = "mvcc-header-255.db";
        var fs = new InMemoryFileSystem();

        using (var database = EmbeddedDatabase.OpenFile(path, fs))
        using (var connection = database.Connect())
        {
            ReadValue(connection, "PRAGMA journal_mode=mvcc;").Should().Be(SqlValue.Text("mvcc"));
            database.IsMvccEnabled.Should().BeTrue();
            database.GetJournalMode().Should().Be(SqliteJournalMode.Mvcc);
        }

        // Probe header after the writer connection is disposed so ownership is free.
        using (var probe = SqlitePager.Open(fs, path, path + "-wal", readOnly: true))
        {
            var header = SqliteDatabaseHeader.Parse(probe.ReadCommittedPage(1));
            header.WriteVersion.Should().Be(SqliteFileFormatVersion.Mvcc);
            header.ReadVersion.Should().Be(SqliteFileFormatVersion.Mvcc);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fs);
        reopened.GetJournalMode().Should().Be(SqliteJournalMode.Mvcc);
        reopened.IsMvccEnabled.Should().BeTrue();
    }

    [Test]
    public void DualCursorHidesBaseRowDeletedInStore()
    {
        var store = new MvStore();
        var table = store.GetOrCreateTableId("t");
        var seed = store.BeginTransaction();
        // Base table still has the row; store records a delete after "bootstrap".
        store.Insert(seed.Id, new MvccRowId(table, 1), [SqlValue.Text("base")]);
        store.Commit(seed.Id);

        var tx = store.BeginTransaction();
        store.Delete(tx.Id, new MvccRowId(table, 1)).Should().BeTrue();

        var merged = MvccDualCursor.MergeVisibleRows(
            store,
            tx.Id,
            table,
            baseRowIds: [1L],
            baseRows: [[SqlValue.Text("base")]]);
        merged.Should().BeEmpty();
    }

    [Test]
    public void DualCursorPrefersStoreUpdateOverBase()
    {
        var store = new MvStore();
        var table = store.GetOrCreateTableId("t");
        var seed = store.BeginTransaction();
        store.Insert(seed.Id, new MvccRowId(table, 1), [SqlValue.Text("old")]);
        store.Commit(seed.Id);

        var tx = store.BeginTransaction();
        store.Update(tx.Id, new MvccRowId(table, 1), [SqlValue.Text("new")]).Should().BeTrue();

        var merged = MvccDualCursor.MergeVisibleRows(
            store,
            tx.Id,
            table,
            baseRowIds: [1L],
            baseRows: [[SqlValue.Text("stale-base")]]);
        merged.Should().HaveCount(1);
        merged[0].Cells[0].Should().Be(SqlValue.Text("new"));
    }

    [Test]
    public void DualCursorIncludesStoreOnlyInserts()
    {
        var store = new MvStore();
        var table = store.GetOrCreateTableId("t");
        var tx = store.BeginTransaction();
        store.Insert(tx.Id, new MvccRowId(table, 99), [SqlValue.Integer(99)]);

        var merged = MvccDualCursor.MergeVisibleRows(
            store,
            tx.Id,
            table,
            baseRowIds: [1L],
            baseRows: [[SqlValue.Integer(1)]]);
        merged.Select(r => r.RowId).OrderBy(x => x).Should().Equal(1L, 99L);
    }

    private static SqlValue ReadValue(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0);
    }
}
