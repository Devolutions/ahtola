using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Mvcc;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class MvccStoreUnitTests
{
    [Test]
    public void ClockPublishesTimestampAtomicallyBeforeReturning()
    {
        var clock = new MvccClock();
        ulong? published = null;
        var ts = clock.GetCommitTimestamp(value => published = value);
        published.Should().Be(ts);
        clock.GetBeginTimestamp().Should().Be(ts + 1);
    }

    [Test]
    public void ConcurrentCommitsWithDisjointWritesSucceed()
    {
        var store = new MvStore();
        var table = store.GetOrCreateTableId("t");
        var a = store.BeginTransaction();
        var b = store.BeginTransaction();
        store.Insert(a.Id, new MvccRowId(table, 1), [SqlValue.Integer(1)]);
        store.Insert(b.Id, new MvccRowId(table, 2), [SqlValue.Integer(2)]);
        store.Commit(a.Id);
        store.Commit(b.Id);
    }

    [Test]
    public void ConcurrentDeletesOnSameRowRaiseWriteWriteConflict()
    {
        var store = new MvStore();
        var table = store.GetOrCreateTableId("t");
        var seed = store.BeginTransaction();
        store.Insert(seed.Id, new MvccRowId(table, 7), [SqlValue.Text("x")]);
        store.Commit(seed.Id);

        var a = store.BeginTransaction();
        var b = store.BeginTransaction();
        store.Delete(a.Id, new MvccRowId(table, 7)).Should().BeTrue();
        var act = () => store.Delete(b.Id, new MvccRowId(table, 7));
        act.Should().Throw<EmbeddedWriteWriteConflictException>();
    }

    [Test]
    public void ReaderDoesNotSeeUncommittedWritesFromPeer()
    {
        var store = new MvStore();
        var table = store.GetOrCreateTableId("t");
        var writer = store.BeginTransaction();
        store.Insert(writer.Id, new MvccRowId(table, 1), [SqlValue.Integer(42)]);

        var reader = store.BeginTransaction();
        store.TryRead(reader.Id, new MvccRowId(table, 1), out _).Should().BeFalse();

        store.Commit(writer.Id);

        // Reader began before writer committed — snapshot isolation keeps it dark.
        store.TryRead(reader.Id, new MvccRowId(table, 1), out _).Should().BeFalse();

        var later = store.BeginTransaction();
        store.TryRead(later.Id, new MvccRowId(table, 1), out var cells).Should().BeTrue();
        cells![0].Should().Be(SqlValue.Integer(42));
    }

    [Test]
    public void WriterSeesOwnUncommittedInserts()
    {
        var store = new MvStore();
        var table = store.GetOrCreateTableId("t");
        var tx = store.BeginTransaction();
        store.Insert(tx.Id, new MvccRowId(table, 3), [SqlValue.Text("mine")]);
        store.TryRead(tx.Id, new MvccRowId(table, 3), out var cells).Should().BeTrue();
        cells![0].Should().Be(SqlValue.Text("mine"));
    }

    [Test]
    public void UpdateReplacesVisibleVersion()
    {
        var store = new MvStore();
        var table = store.GetOrCreateTableId("t");
        var seed = store.BeginTransaction();
        store.Insert(seed.Id, new MvccRowId(table, 1), [SqlValue.Text("old")]);
        store.Commit(seed.Id);

        var tx = store.BeginTransaction();
        store.Update(tx.Id, new MvccRowId(table, 1), [SqlValue.Text("new")]).Should().BeTrue();
        store.TryRead(tx.Id, new MvccRowId(table, 1), out var cells).Should().BeTrue();
        cells![0].Should().Be(SqlValue.Text("new"));
        store.Commit(tx.Id);

        var reader = store.BeginTransaction();
        store.TryRead(reader.Id, new MvccRowId(table, 1), out cells).Should().BeTrue();
        cells![0].Should().Be(SqlValue.Text("new"));
    }

    [Test]
    public void RollbackDropsUncommittedVersions()
    {
        var store = new MvStore();
        var table = store.GetOrCreateTableId("t");
        var tx = store.BeginTransaction();
        store.Insert(tx.Id, new MvccRowId(table, 9), [SqlValue.Integer(9)]);
        store.Rollback(tx.Id);

        var reader = store.BeginTransaction();
        store.TryRead(reader.Id, new MvccRowId(table, 9), out _).Should().BeFalse();
        store.ScanVisible(reader.Id).Should().BeEmpty();
    }

    [Test]
    public void ExclusiveTransactionBlocksPeerBegin()
    {
        var store = new MvStore();
        _ = store.BeginExclusiveTransaction();
        var act = () => store.BeginTransaction();
        act.Should().Throw<EmbeddedBusyException>();
    }

    [Test]
    public void ScanVisibleReturnsCommittedRowsOnly()
    {
        var store = new MvStore();
        var table = store.GetOrCreateTableId("t");
        var a = store.BeginTransaction();
        store.Insert(a.Id, new MvccRowId(table, 1), [SqlValue.Integer(1)]);
        store.Insert(a.Id, new MvccRowId(table, 2), [SqlValue.Integer(2)]);
        store.Commit(a.Id);

        var b = store.BeginTransaction();
        store.Insert(b.Id, new MvccRowId(table, 3), [SqlValue.Integer(3)]);

        var reader = store.BeginTransaction();
        var visible = store.ScanVisible(reader.Id);
        visible.Select(row => row.RowId.RowId).OrderBy(id => id).Should().Equal(1L, 2L);
    }

    [Test]
    public void DeleteOrTombstoneBaseInvalidatesClassicBaseRow()
    {
        var store = new MvStore();
        var table = store.GetOrCreateTableId("t");
        var key = new MvccRowId(table, 5);
        var tx = store.BeginTransaction();

        store.DeleteOrTombstoneBase(tx.Id, key);
        store.IsBaseRowInvalidated(tx.Id, key).Should().BeTrue();
        store.TryRead(tx.Id, key, out _).Should().BeFalse();

        store.Commit(tx.Id);
        store.SnapshotCommittedDeletes().Should().Contain(key);

        var later = store.BeginTransaction();
        store.IsBaseRowInvalidated(later.Id, key).Should().BeTrue();
    }

    [Test]
    public void ConcurrentBaseTombstonesRaiseWriteWriteConflict()
    {
        var store = new MvStore();
        var table = store.GetOrCreateTableId("t");
        var key = new MvccRowId(table, 9);
        var a = store.BeginTransaction();
        var b = store.BeginTransaction();

        store.DeleteOrTombstoneBase(a.Id, key);
        var act = () => store.DeleteOrTombstoneBase(b.Id, key);
        act.Should().Throw<EmbeddedWriteWriteConflictException>();
    }

    [Test]
    public void UpdateIncludingBaseOverlaysClassicBaseRow()
    {
        var store = new MvStore();
        var table = store.GetOrCreateTableId("t");
        var key = new MvccRowId(table, 1);
        var tx = store.BeginTransaction();

        store.UpdateIncludingBase(tx.Id, key, [SqlValue.Text("new")]);
        store.TryRead(tx.Id, key, out var cells).Should().BeTrue();
        cells![0].Should().Be(SqlValue.Text("new"));
        store.IsBaseRowInvalidated(tx.Id, key).Should().BeTrue();
        store.Commit(tx.Id);

        store.SnapshotLiveCommittedRows().Should().ContainSingle(row =>
            row.RowId == key && row.Cells[0].Equals(SqlValue.Text("new")));
    }

    [Test]
    public void TypedPrimaryKeyCanonicalizationMatchesBuiltInSqliteCollations()
    {
        var noCase = new SqlitePrimaryKeySchema(
        [
            new SqlitePrimaryKeyTerm(
                0,
                "key",
                SqliteKeySortOrder.Ascending,
                SqliteKeyCollation.FromName("NOCASE")),
        ]);
        var rtrim = new SqlitePrimaryKeySchema(
        [
            new SqlitePrimaryKeyTerm(
                0,
                "key",
                SqliteKeySortOrder.Ascending,
                SqliteKeyCollation.FromName("RTRIM")),
        ]);

        MvccKey.FromPrimaryKey(noCase, [SqlValue.Text("Alpha")], SqliteTextEncoding.Utf8)
            .Should()
            .Be(MvccKey.FromPrimaryKey(noCase, [SqlValue.Text("alpha")], SqliteTextEncoding.Utf8));
        MvccKey.FromPrimaryKey(rtrim, [SqlValue.Text("key")], SqliteTextEncoding.Utf8)
            .Should()
            .Be(MvccKey.FromPrimaryKey(rtrim, [SqlValue.Text("key   ")], SqliteTextEncoding.Utf8));
    }

    [Test]
    public void LegacyRecoveryWithoutAnObjectNameFailsClosedForTheV4Upgrade()
    {
        var store = new MvStore();

        store.ApplyRecoveredCommit(
            1,
            [MvccLogOp.Upsert(new MvccRowId(-2, 1), [SqlValue.Text("legacy")])]);

        store.CanUpgradeLegacyLog.Should().BeFalse();
    }
}
