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

    [Test]
    public void DualCursorConsumesOnlyThePrefixNeededForFirstWinner()
    {
        var store = new MvStore();
        var table = store.GetOrCreateTableId("t");
        var tx = store.BeginTransaction();
        store.Insert(tx.Id, new MvccRowId(table, 50_000), [SqlValue.Integer(50_000)]);
        var visitedBaseRows = 0;

        IEnumerable<MvccDualCursor.Row> BaseRows()
        {
            for (var rowId = 1L; rowId <= 100_000; rowId++)
            {
                visitedBaseRows++;
                yield return new MvccDualCursor.Row(
                    MvccKey.FromInteger(rowId),
                    [SqlValue.Integer(rowId)]);
            }
        }

        var rows = MvccDualCursor.EnumerateVisibleRows(
            store,
            tx.Id,
            table,
            BaseRows(),
            MvccKeyComparer.Integer);
        visitedBaseRows.Should().Be(0);

        using var cursor = rows.GetEnumerator();
        cursor.MoveNext().Should().BeTrue();
        cursor.Current.Key.Integer.Should().Be(1);
        visitedBaseRows.Should().Be(1);
    }

    [Test]
    public void DualCursorUsesCompositePrimaryKeyCollationAndDirection()
    {
        var schema = new SqlitePrimaryKeySchema(
        [
            new SqlitePrimaryKeyTerm(
                0,
                "name",
                SqliteKeySortOrder.Ascending,
                SqliteKeyCollation.FromName("NOCASE")),
            new SqlitePrimaryKeyTerm(
                1,
                "rank",
                SqliteKeySortOrder.Descending,
                SqliteKeyCollation.Binary),
        ]);
        var recordComparer = new SqliteIndexRecordComparer(
            SqliteTextEncoding.Utf8,
            schema.Terms.Select(term =>
                new SqliteIndexComparisonTerm(term.SortOrder, term.Collation)).ToArray());
        var comparer = MvccKeyComparer.ForRecord(recordComparer);
        var store = new MvStore();
        var table = store.GetOrCreateTableId("t");
        var tx = store.BeginTransaction();
        var alphaTwo = MvccKey.FromPrimaryKey(
            schema,
            [SqlValue.Text("ALPHA"), SqlValue.Integer(2)],
            SqliteTextEncoding.Utf8);
        var betaOne = MvccKey.FromPrimaryKey(
            schema,
            [SqlValue.Text("beta"), SqlValue.Integer(1)],
            SqliteTextEncoding.Utf8);
        store.Insert(tx.Id, new MvccRowId(table, betaOne), [SqlValue.Text("beta-1")]);
        store.Insert(tx.Id, new MvccRowId(table, alphaTwo), [SqlValue.Text("alpha-2")]);

        var alphaThree = MvccKey.FromPrimaryKey(
            schema,
            [SqlValue.Text("alpha"), SqlValue.Integer(3)],
            SqliteTextEncoding.Utf8);
        var alphaOne = MvccKey.FromPrimaryKey(
            schema,
            [SqlValue.Text("alpha"), SqlValue.Integer(1)],
            SqliteTextEncoding.Utf8);
        var merged = MvccDualCursor.EnumerateVisibleRows(
                store,
                tx.Id,
                table,
                [
                    new MvccDualCursor.Row(alphaThree, [SqlValue.Text("alpha-3")]),
                    new MvccDualCursor.Row(alphaOne, [SqlValue.Text("alpha-1")]),
                ],
                comparer)
            .ToArray();

        merged.Select(row => row.Cells[0].AsText())
            .Should()
            .Equal("alpha-3", "alpha-2", "alpha-1", "beta-1");
    }

    [Test]
    public void OpenDualCursorKeepsItsTransactionSnapshotAsAPeerCommits()
    {
        var store = new MvStore();
        var reader = store.BeginTransaction();
        var writer = store.BeginTransaction();
        var table = store.GetOrCreateTableId(reader.Id, "t");
        var rows = MvccDualCursor.EnumerateVisibleRows(
            store,
            reader.Id,
            table,
            [
                new MvccDualCursor.Row(
                    MvccKey.FromInteger(1),
                    [SqlValue.Text("one")]),
                new MvccDualCursor.Row(
                    MvccKey.FromInteger(2),
                    [SqlValue.Text("two")]),
            ],
            MvccKeyComparer.Integer);

        using var cursor = rows.GetEnumerator();
        cursor.MoveNext().Should().BeTrue();
        cursor.Current.Key.Integer.Should().Be(1);

        store.UpdateIncludingBase(
            writer.Id,
            new MvccRowId(table, 2),
            [SqlValue.Text("updated")]);
        store.Insert(
            writer.Id,
            new MvccRowId(table, 3),
            [SqlValue.Text("three")]);
        store.Commit(writer.Id);

        cursor.MoveNext().Should().BeTrue();
        cursor.Current.Key.Integer.Should().Be(2);
        cursor.Current.Cells[0].Should().Be(SqlValue.Text("two"));
        cursor.MoveNext().Should().BeFalse();
        store.Rollback(reader.Id);
    }

    private static SqlValue ReadValue(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0);
    }
}
