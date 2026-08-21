using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

/// <summary>
/// Covers <see cref="ManagedReplicaChangeCaptureProjector"/> and the public
/// <see cref="AhtolaConnection.PeekPendingChangeCapture"/> entry point: the read-only bridge that
/// projects a managed embedded replica's still-pending local change journal into Ahtola's public
/// change-data-capture row contract. These tests prove the bridge agrees with the real
/// <c>turso_cdc</c> table for the fields it can represent, documents where it intentionally
/// cannot (an update's before-image, multi-row transaction grouping), fails closed for
/// unrepresentable entries instead of guessing, and that peeking is side-effect-free: it can
/// never corrupt, or be corrupted by, a genuine push's acknowledgement.
/// </summary>
public sealed class ManagedReplicaChangeCaptureBridgeTests
{
    [Test]
    public void ProjectAgreesWithRealChangeDataCaptureForDistinctInsertUpdateDeleteRows()
    {
        using var database = ManagedDatabaseAdapter.Open(":memory:");
        var connection = database.Connect();

        Exec(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT)");
        Exec(connection, "PRAGMA capture_data_changes_conn('full')");

        // Pre-existing state the pending batch under test does NOT reference: only present so
        // the update/delete below have a row to act on. These generate their own real CDC rows,
        // so every comparison below targets its row by (change_type, id) rather than a blanket
        // "the next N rows" ordering assumption.
        Exec(connection, "INSERT INTO t VALUES (2, 'seed2')");
        Exec(connection, "INSERT INTO t VALUES (3, 'seed3')");

        // The three journal-tracked operations under test, each touching a distinct rowid
        // exactly once so none of them hits the "superseded touch" after-image degradation.
        Exec(connection, "INSERT INTO t VALUES (1, 'a')");
        Exec(connection, "UPDATE t SET value = 'c2' WHERE id = 2");
        var beforeDelete = SqliteRecordCodec.Encode(
            ManagedReplicaLogicalReplayer.TryCaptureCurrentRowValues(connection, "t", 3)!);
        Exec(connection, "DELETE FROM t WHERE id = 3");

        var batch = new ReplicaLocalChangeBatch(
            FirstSequence: 1,
            Watermark: 4,
            Changes:
            [
                ReplicaLocalChange.Row(SqliteChangeOperation.Insert, "main", "t", 1) with { Sequence = 1 },
                ReplicaLocalChange.Row(SqliteChangeOperation.Update, "main", "t", 2) with { Sequence = 2 },
                ReplicaLocalChange.Row(SqliteChangeOperation.Delete, "main", "t", 3, beforeDelete) with { Sequence = 3 },
            ]);

        var projected = ManagedReplicaChangeCaptureProjector.Project(connection, batch);

        projected.FirstChangeId.Should().Be(1);
        projected.AcknowledgementWatermark.Should().Be(4);
        projected.Rows.Should().HaveCount(3);

        var insertRow = projected.Rows[0];
        var insertCdc = SingleCdcRow(connection, changeType: 1, id: 1);
        insertRow.ChangeId.Should().Be(1);
        insertRow.ChangeTransactionId.Should().Be(insertRow.ChangeId);
        insertRow.ChangeType.Should().Be(AhtolaReplicaChangeType.Insert);
        insertRow.TableName.Should().Be(AsText(insertCdc[3]));
        insertRow.RowId.Should().Be(AsInteger(insertCdc[4]));
        insertRow.Before.Should().BeNull();
        insertCdc[5].Should().Be(SqlValue.Null, "a real insert row never has a before-image either");
        DecodeAfter(insertRow).Should().Equal(SqliteRecordCodec.Decode(AsBlob(insertCdc[6]).Span));

        var updateRow = projected.Rows[1];
        var updateCdc = SingleCdcRow(connection, changeType: 0, id: 2);
        updateRow.ChangeId.Should().Be(2);
        updateRow.ChangeTransactionId.Should().Be(updateRow.ChangeId);
        updateRow.ChangeType.Should().Be(AhtolaReplicaChangeType.Update);
        updateRow.TableName.Should().Be(AsText(updateCdc[3]));
        updateRow.RowId.Should().Be(AsInteger(updateCdc[4]));
        // Documented, deliberate divergence: the private replica journal never captures an
        // update's pre-image, while the real turso_cdc row does. Both sides are asserted so this
        // simplification stays a proven, intentional gap rather than an untested one.
        updateRow.Before.Should().BeNull();
        updateCdc[5].Should().NotBe(SqlValue.Null, "the real CDC row DOES capture the update's before-image");
        DecodeAfter(updateRow).Should().Equal(SqliteRecordCodec.Decode(AsBlob(updateCdc[6]).Span));

        var deleteRow = projected.Rows[2];
        var deleteCdc = SingleCdcRow(connection, changeType: -1, id: 3);
        deleteRow.ChangeId.Should().Be(3);
        deleteRow.ChangeTransactionId.Should().Be(deleteRow.ChangeId);
        deleteRow.ChangeType.Should().Be(AhtolaReplicaChangeType.Delete);
        deleteRow.TableName.Should().Be(AsText(deleteCdc[3]));
        deleteRow.RowId.Should().Be(AsInteger(deleteCdc[4]));
        deleteRow.After.Should().BeNull();
        deleteCdc[6].Should().Be(SqlValue.Null, "a real delete row never has an after-image either");
        SqliteRecordCodec.Decode(deleteRow.Before!).Should().Equal(SqliteRecordCodec.Decode(AsBlob(deleteCdc[5]).Span));
    }

    [Test]
    public void ProjectDegradesSupersededTouchAfterImageToNullInsteadOfStaleData()
    {
        using var database = ManagedDatabaseAdapter.Open(":memory:");
        var connection = database.Connect();

        Exec(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT)");
        // Only the FINAL live value is ever present; if the earlier touch's "after" were
        // reconstructed from live state it would silently show this final value instead, so
        // asserting it stays null is what actually proves the guard works.
        Exec(connection, "INSERT INTO t VALUES (5, 'final')");

        var batch = new ReplicaLocalChangeBatch(
            FirstSequence: 1,
            Watermark: 3,
            Changes:
            [
                ReplicaLocalChange.Row(SqliteChangeOperation.Insert, "main", "t", 5) with { Sequence = 1 },
                ReplicaLocalChange.Row(SqliteChangeOperation.Update, "main", "t", 5) with { Sequence = 2 },
            ]);

        var projected = ManagedReplicaChangeCaptureProjector.Project(connection, batch);

        projected.Rows.Should().HaveCount(2);
        projected.Rows[0].After.Should().BeNull("a later pending change in the same batch touches the same row again");
        projected.Rows[1].After.Should().NotBeNull("this is the last touch to the row, so live state safely reflects it");
        SqliteRecordCodec.Decode(projected.Rows[1].After!).Should()
            .Equal(SqlValue.Integer(5), SqlValue.Text("final"));
    }

    [Test]
    public void ProjectFailsClosedForADeleteWithNoCapturedBeforeImage()
    {
        using var database = ManagedDatabaseAdapter.Open(":memory:");
        var connection = database.Connect();

        Exec(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT)");
        Exec(connection, "INSERT INTO t VALUES (1, 'x')");
        Exec(connection, "DELETE FROM t WHERE id = 1");

        var batch = new ReplicaLocalChangeBatch(
            FirstSequence: 1,
            Watermark: 2,
            Changes: [ReplicaLocalChange.Row(SqliteChangeOperation.Delete, "main", "t", 1) with { Sequence = 1 }]);

        Action act = () => ManagedReplicaChangeCaptureProjector.Project(connection, batch);

        act.Should().Throw<AhtolaReplicaChangeCaptureException>()
            .WithMessage("*no captured before-image*");
    }

    [Test]
    public void ProjectFailsClosedWhenTheBatchContainsASchemaChangeAnywhereEvenAlongsideValidRowChanges()
    {
        using var database = ManagedDatabaseAdapter.Open(":memory:");
        var connection = database.Connect();

        Exec(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT)");
        Exec(connection, "INSERT INTO t VALUES (1, 'a')");

        // The schema entry is deliberately NOT first, proving the whole batch is scanned rather
        // than only its head.
        var batch = new ReplicaLocalChangeBatch(
            FirstSequence: 1,
            Watermark: 3,
            Changes:
            [
                ReplicaLocalChange.Row(SqliteChangeOperation.Insert, "main", "t", 1) with { Sequence = 1 },
                ReplicaLocalChange.Schema("CREATE TABLE u(v INTEGER)") with { Sequence = 2 },
            ]);

        Action act = () => ManagedReplicaChangeCaptureProjector.Project(connection, batch);

        act.Should().Throw<AhtolaReplicaChangeCaptureException>()
            .WithMessage("*schema*");
    }

    [Test]
    public void AcknowledgingAnEarlierWatermarkLeavesLaterPendingRowsUncorrupted()
    {
        using var database = ManagedDatabaseAdapter.Open(":memory:");
        var connection = database.Connect();
        Exec(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT)");
        // Final live state matching each pending change's own result (neither key is touched
        // twice, so both remain eligible for after-image reconstruction).
        Exec(connection, "INSERT INTO t VALUES (1, 'v2')");
        Exec(connection, "INSERT INTO t VALUES (2, 'v3')");

        var journalPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"cdc-bridge-ack-{Guid.NewGuid():N}.db");
        try
        {
            var journal = ManagedReplicaChangeJournal.Open(journalPath);
            journal.AppendCommitted(
            [
                ReplicaLocalChange.Row(SqliteChangeOperation.Update, "main", "t", 1),
                ReplicaLocalChange.Row(SqliteChangeOperation.Insert, "main", "t", 2),
            ]);

            var firstPeek = ManagedReplicaChangeCaptureProjector.Project(connection, journal.ReadBatch(int.MaxValue));
            firstPeek.FirstChangeId.Should().Be(1);
            firstPeek.AcknowledgementWatermark.Should().Be(3);
            firstPeek.Rows.Should().HaveCount(2);
            firstPeek.Rows[0].ChangeId.Should().Be(1);
            firstPeek.Rows[0].ChangeType.Should().Be(AhtolaReplicaChangeType.Update);
            SqliteRecordCodec.Decode(firstPeek.Rows[0].After!).Should().Equal(SqlValue.Integer(1), SqlValue.Text("v2"));
            firstPeek.Rows[1].ChangeId.Should().Be(2);
            firstPeek.Rows[1].ChangeType.Should().Be(AhtolaReplicaChangeType.Insert);
            SqliteRecordCodec.Decode(firstPeek.Rows[1].After!).Should().Equal(SqlValue.Integer(2), SqlValue.Text("v3"));

            // Peeking again before any acknowledgement must be perfectly repeatable: it is a
            // pure read, so it cannot itself be the thing that "uses up" or mutates the journal.
            // Compared structurally (not via record equality) since "after" images are freshly
            // re-encoded byte[] instances on every call and byte[] uses reference equality.
            var repeatPeek = ManagedReplicaChangeCaptureProjector.Project(connection, journal.ReadBatch(int.MaxValue));
            repeatPeek.Should().BeEquivalentTo(firstPeek);

            // A genuine push now acknowledges only the first (already-delivered) change.
            journal.Acknowledge(2);

            var secondPeek = ManagedReplicaChangeCaptureProjector.Project(connection, journal.ReadBatch(int.MaxValue));
            secondPeek.Rows.Should().ContainSingle();
            var remaining = secondPeek.Rows[0];
            // The surviving row keeps its original change id: acknowledging a prefix never
            // renumbers what is left, and the row's content is byte-for-byte identical to what
            // the pre-ack peek already reported for it.
            remaining.ChangeId.Should().Be(firstPeek.Rows[1].ChangeId);
            remaining.ChangeType.Should().Be(firstPeek.Rows[1].ChangeType);
            remaining.TableName.Should().Be(firstPeek.Rows[1].TableName);
            remaining.RowId.Should().Be(firstPeek.Rows[1].RowId);
            SqliteRecordCodec.Decode(remaining.After!).Should()
                .Equal(SqliteRecordCodec.Decode(firstPeek.Rows[1].After!));
            secondPeek.FirstChangeId.Should().Be(2);
            secondPeek.AcknowledgementWatermark.Should().Be(3, "no new changes were appended after the ack");
        }
        finally
        {
            var sidecar = journalPath + ManagedReplicaChangeJournal.Suffix;
            if (File.Exists(sidecar))
                File.Delete(sidecar);
        }
    }

    [Test]
    public void PublicPeekPendingChangeCaptureProjectsCommittedLocalMutationsEndToEnd()
    {
        var path = NewReplicaPath("cdc-bridge-peek");
        try
        {
            using (var setup = new AhtolaConnection($"Data Source={path};Local Provider=Managed"))
            {
                setup.Open();
                setup.ExecuteNonQuery("CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT)");
                setup.ExecuteNonQuery("INSERT INTO t VALUES (2, 'seed2')");
            }

            using var replica = AhtolaConnection.CreateReplica(
                new AhtolaReplicaOptions(path, new Uri("https://example.test"), authToken: null));
            replica.Open();

            replica.ExecuteNonQuery("INSERT INTO t VALUES (1, 'a')");
            replica.ExecuteNonQuery("UPDATE t SET value = 'c2' WHERE id = 2");

            var peek = replica.PeekPendingChangeCapture();

            peek.FirstChangeId.Should().Be(1);
            peek.AcknowledgementWatermark.Should().Be(3);
            peek.Rows.Should().HaveCount(2);

            var insertRow = peek.Rows[0];
            insertRow.ChangeId.Should().Be(1);
            insertRow.ChangeTransactionId.Should().Be(1);
            insertRow.ChangeType.Should().Be(AhtolaReplicaChangeType.Insert);
            insertRow.TableName.Should().Be("t");
            insertRow.RowId.Should().Be(1);
            insertRow.Before.Should().BeNull();
            SqliteRecordCodec.Decode(insertRow.After!).Should().Equal(SqlValue.Integer(1), SqlValue.Text("a"));

            var updateRow = peek.Rows[1];
            updateRow.ChangeId.Should().Be(2);
            updateRow.ChangeType.Should().Be(AhtolaReplicaChangeType.Update);
            updateRow.TableName.Should().Be("t");
            updateRow.RowId.Should().Be(2);
            updateRow.Before.Should().BeNull("the private replica journal never captures an update's pre-image");
            SqliteRecordCodec.Decode(updateRow.After!).Should().Equal(SqlValue.Integer(2), SqlValue.Text("c2"));

            // Peeking performs no network I/O and never advances the push watermark: reading it
            // again reports the exact same pending batch (structurally, not via record equality
            // — "after" images are freshly re-encoded byte[] instances on every call).
            replica.PeekPendingChangeCapture().Should().BeEquivalentTo(peek);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void PeekPendingChangeCaptureThrowsForNonReplicaConnections()
    {
        using var connection = new AhtolaConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        Action act = () => connection.PeekPendingChangeCapture();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*managed embedded replica connections*");
    }

    // --- helpers ---

    private static void Exec(IManagedConnectionAdapter connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private static SqlValue[] SingleCdcRow(IManagedConnectionAdapter connection, int changeType, long id)
    {
        using var statement = connection.Prepare(
            "SELECT change_id, change_txn_id, change_type, table_name, id, before, after "
            + "FROM turso_cdc WHERE change_type = ? AND id = ?");
        statement.Bind(1, SqlValue.Integer(changeType));
        statement.Bind(2, SqlValue.Integer(id));
        statement.Step().Should().Be(StatementStepResult.Row);
        var row = new SqlValue[statement.GetColumnCount()];
        for (var i = 0; i < row.Length; i++)
            row[i] = statement.GetValue(i);
        statement.Step().Should().Be(StatementStepResult.Done, "exactly one real CDC row should match this filter");
        return row;
    }

    private static string AsText(SqlValue value) => value.AsText();

    private static long AsInteger(SqlValue value) => value.AsInteger();

    private static ReadOnlyMemory<byte> AsBlob(SqlValue value) => value.AsBlob();

    private static SqlValue[] DecodeAfter(AhtolaReplicaChangeRow row) => SqliteRecordCodec.Decode(row.After!);

    private static string NewReplicaPath(string prefix)
        => Path.Combine(TestContext.CurrentContext.WorkDirectory, $"{prefix}-{Guid.NewGuid():N}.db");

    private static void DeleteReplicaFiles(string path)
    {
        foreach (var file in new[]
                 {
                     path,
                     path + "-wal",
                     path + "-shm",
                     path + "-journal",
                     path + ".ahtola-replica-meta",
                     path + ManagedReplicaChangeJournal.Suffix,
                 })
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }
}
