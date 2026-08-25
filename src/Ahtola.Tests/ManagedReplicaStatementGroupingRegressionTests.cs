using Ahtola.Core;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Regressions for the managed replica push review: a batch's acknowledgement watermark may only
/// ever retire rows the push actually transmitted.
/// </summary>
/// <remarks>
/// One SQL statement can invoke the update hook for many rows, and only the first of those journal
/// entries carries the statement text. That relationship used to be implied by adjacency, so a
/// conflict discard, a prune, or a batch boundary could separate a row from the statement that would
/// have transmitted it — and the next batch's watermark then retired the orphan as if the remote had
/// received it. The entries now name their statement explicitly, and every path that could break the
/// grouping proves it first.
/// </remarks>
public sealed class ManagedReplicaStatementGroupingRegressionTests
{
    [Test]
    public void AMultiRowStatementRecordsOneStatementIdentityForEveryRowItTouched()
    {
        var path = NewReplicaPath(nameof(AMultiRowStatementRecordsOneStatementIdentityForEveryRowItTouched));
        try
        {
            var journal = ManagedReplicaChangeJournal.Open(path);
            journal.AppendCommitted(MultiRowUpdate());

            var changes = journal.ReadBatch(int.MaxValue).Changes;
            changes.Select(static change => change.Sequence).Should().Equal(1L, 2L, 3L);
            changes.Select(static change => change.StatementSequence).Should().Equal(1L, 1L, 1L);
            changes[0].CarriesStatementSql.Should().BeTrue();
            changes[1].CarriesStatementSql.Should().BeFalse();

            // The grouping is durable, not an in-memory artifact of the append.
            var reopened = ManagedReplicaChangeJournal.Open(path);
            reopened.ReadBatch(int.MaxValue).Changes
                .Select(static change => change.StatementSequence)
                .Should().Equal(1L, 1L, 1L);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void ABatchLimitNeverEndsInsideAMultiRowStatement()
    {
        var path = NewReplicaPath(nameof(ABatchLimitNeverEndsInsideAMultiRowStatement));
        try
        {
            var journal = ManagedReplicaChangeJournal.Open(path);
            journal.AppendCommitted(MultiRowUpdate());
            journal.AppendCommitted([Row("items", 9, "INSERT INTO items(id) VALUES (9)")]);

            // A limit of two lands in the middle of the three-row statement. Cutting there would
            // leave rows 2 and 3 behind with no SQL of their own, and the next batch would retire
            // them without transmitting anything.
            var batch = journal.ReadBatch(2);
            batch.Changes.Select(static change => change.Sequence).Should().Equal(1L, 2L, 3L);
            batch.Watermark.Should().Be(4);

            journal.ValidateBatchIsFullyReplayable(batch);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void DiscardingPartOfAMultiRowStatementFailsClosed()
    {
        var path = NewReplicaPath(nameof(DiscardingPartOfAMultiRowStatementFailsClosed));
        try
        {
            var journal = ManagedReplicaChangeJournal.Open(path);
            journal.AppendCommitted(MultiRowUpdate());

            // Discarding only the entry that carries the SQL would strand rows 2 and 3: no push
            // could ever transmit them, and the next batch's watermark would retire them anyway.
            var partial = () => journal.DiscardUnacknowledged([1L]);
            partial.Should().Throw<InvalidOperationException>().WithMessage("*part of a statement*");

            // The trailing rows are no more discardable on their own.
            var trailing = () => journal.DiscardUnacknowledged([2L, 3L]);
            trailing.Should().Throw<InvalidOperationException>().WithMessage("*part of a statement*");

            journal.ReadBatch(int.MaxValue).Changes.Should().HaveCount(3, "nothing was written");
            journal.DiscardedSequences.Should().BeEmpty();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void DiscardingAWholeMultiRowStatementIsStillAllowedAndStaysGapAware()
    {
        var path = NewReplicaPath(nameof(DiscardingAWholeMultiRowStatementIsStillAllowedAndStaysGapAware));
        try
        {
            var journal = ManagedReplicaChangeJournal.Open(path);
            journal.AppendCommitted(MultiRowUpdate());
            journal.AppendCommitted([Row("items", 9, "INSERT INTO items(id) VALUES (9)")]);

            journal.DiscardUnacknowledged([1L, 2L, 3L]).Should().Be(3);

            // The discards stay durable evidence that the gap is intentional, which is what lets an
            // interrupted discard complete idempotently.
            journal.DiscardedSequences.Should().Equal(1L, 2L, 3L);
            journal.AssignedSequence.Should().Be(4, "the high-water mark never moves backwards");

            var reopened = ManagedReplicaChangeJournal.Open(path);
            reopened.DiscardedSequences.Should().Equal(1L, 2L, 3L);
            var batch = reopened.ReadBatch(int.MaxValue);
            batch.Changes.Select(static change => change.Sequence).Should().Equal(4L);
            reopened.ValidateBatchIsFullyReplayable(batch);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void ARowOrphanedFromItsStatementIsNeverAcknowledgedByALaterPush()
    {
        var path = NewReplicaPath(nameof(ARowOrphanedFromItsStatementIsNeverAcknowledgedByALaterPush));
        try
        {
            // A journal written before statement identity existed: three rows of one UPDATE, of
            // which only the first carries SQL, followed by an unrelated statement. Simulating the
            // pre-fix conflict discard by removing the SQL-carrying entry through the legacy path is
            // exactly the shape the review flagged.
            var journal = ManagedReplicaChangeJournal.Open(path);
            journal.AppendCommitted(MultiRowUpdate());
            journal.AppendCommitted([Row("items", 9, "INSERT INTO items(id) VALUES (9)")]);

            // Discard the whole statement, then verify the remaining batch is self-contained.
            long[] wholeStatement = [1L, 2L, 3L];
            journal.DiscardUnacknowledged(wholeStatement).Should().Be(3);

            var batch = journal.ReadBatch(int.MaxValue);
            batch.Changes.Select(static change => change.Sequence).Should().Equal(4L);

            // The surviving statement is self-contained, so the batch is replayable and its
            // watermark retires only sequence 4 — the discarded rows are already accounted for.
            journal.ValidateBatchIsFullyReplayable(batch);
            batch.Watermark.Should().Be(5);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void ABatchContainingAnOrphanedRowFailsClosedInsteadOfAcknowledgingIt()
    {
        var path = NewReplicaPath(nameof(ABatchContainingAnOrphanedRowFailsClosedInsteadOfAcknowledgingIt));
        try
        {
            var journal = ManagedReplicaChangeJournal.Open(path);

            // An entry whose statement is unknown is exactly what a pre-format-8 journal yields when
            // its leading SQL entry is gone. Acknowledging a batch that contains one would retire a
            // change the remote never received.
            journal.AppendCommitted(
            [
                ReplicaLocalChange.Row(SqliteChangeOperation.Update, "main", "items", 2),
                Row("items", 9, "INSERT INTO items(id) VALUES (9)"),
            ]);

            var batch = journal.ReadBatch(int.MaxValue);
            batch.Changes[0].StatementSequence.Should().Be(0, "no replayable statement is known");

            var validate = () => journal.ValidateBatchIsFullyReplayable(batch);
            validate.Should().Throw<InvalidDataException>().WithMessage("*never transmitted*");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void AnAlreadyAcknowledgedStatementStillCoversItsTrailingRows()
    {
        var path = NewReplicaPath(nameof(AnAlreadyAcknowledgedStatementStillCoversItsTrailingRows));
        try
        {
            var journal = ManagedReplicaChangeJournal.Open(path);
            journal.AppendCommitted(MultiRowUpdate());

            // A legacy journal could split a statement across two pushes. The leading entry was
            // transmitted and confirmed, so its trailing rows were applied remotely with it and are
            // safe to retire — unlike a statement that was discarded.
            journal.Acknowledge(2);

            var batch = journal.ReadBatch(int.MaxValue);
            batch.Changes.Select(static change => change.Sequence).Should().Equal(2L, 3L);
            journal.ValidateBatchIsFullyReplayable(batch);

            // The push path must reach the same verdict as the journal: a batch the journal
            // certified must never be rejected as unsent, or synchronization would wedge for good
            // on every upgraded replica whose watermark split a statement.
            AhtolaRemoteClient.ValidateReplicaPushCoverageForTesting(batch);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void ADiscardedStatementNeverCoversItsTrailingRows()
    {
        var path = NewReplicaPath(nameof(ADiscardedStatementNeverCoversItsTrailingRows));
        try
        {
            var journal = ManagedReplicaChangeJournal.Open(path);
            journal.AppendCommitted(MultiRowUpdate());

            // Hand-build the state the old discard could reach: sequence 1 recorded as discarded
            // while its trailing rows are still retained and still pending.
            var forced = ForceDiscardLeadingEntry(path, journal);

            var batch = forced.ReadBatch(int.MaxValue);
            batch.Changes.Select(static change => change.Sequence).Should().Equal(2L, 3L);

            var validate = () => forced.ValidateBatchIsFullyReplayable(batch);
            validate.Should().Throw<InvalidDataException>().WithMessage("*never transmitted*");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    /// <summary>
    /// Rewrites the journal so the leading entry of the multi-row statement is discarded while its
    /// trailing rows stay retained, reproducing the pre-fix conflict-discard outcome that a
    /// group-closed discard now refuses to create.
    /// </summary>
    private static ManagedReplicaChangeJournal ForceDiscardLeadingEntry(
        string path,
        ManagedReplicaChangeJournal journal)
    {
        var retained = journal.ReadBatch(int.MaxValue).Changes
            .Where(static change => change.Sequence != 1)
            .ToArray();

        var rebuilt = ManagedReplicaChangeJournal.OpenForTesting(
            path,
            assignedSequence: journal.AssignedSequence,
            acknowledgedWatermark: journal.AcknowledgedWatermark,
            changes: retained,
            discarded: [1L]);
        return rebuilt;
    }

    private static IReadOnlyList<ReplicaLocalChange> MultiRowUpdate()
        =>
        [
            ReplicaLocalChange.Row(SqliteChangeOperation.Update, "main", "items", 1) with
            {
                Sql = "UPDATE items SET v = v + 1 WHERE id IN (1, 2, 3)",
            },
            ReplicaLocalChange.Row(SqliteChangeOperation.Update, "main", "items", 2),
            ReplicaLocalChange.Row(SqliteChangeOperation.Update, "main", "items", 3),
        ];

    private static ReplicaLocalChange Row(string table, long rowId, string sql)
        => ReplicaLocalChange.Row(SqliteChangeOperation.Insert, "main", table, rowId) with { Sql = sql };

    private static string NewReplicaPath(string prefix)
        => Path.Combine(TestContext.CurrentContext.WorkDirectory, $"{prefix}-{Guid.NewGuid():N}.db");

    private static void DeleteReplicaFiles(string path)
    {
        foreach (var file in ManagedReplicaBootstrapper.GetLocalArtifactPaths(path))
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }
}
