using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Ahtola.Core;
using Ahtola.Core.Storage;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Covers the managed embedded replica's explicit push-conflict resolution workflow: durable
/// conflict recording, conservative classification, fail-closed synchronization while a conflict
/// is open, eligible-only logical rebase, explicit data-loss-acknowledged discard, and the
/// crash/cancellation/restart behavior of each.
/// </summary>
public sealed class ManagedReplicaConflictRebaseTests
{
    // ---------------------------------------------------------------------------------------
    // Classifier: pure, no I/O.
    // ---------------------------------------------------------------------------------------
    [Test]
    public void ClassifierMarksTheRejectedRowConflictingAndAnUnrelatedRowEligible()
    {
        var batch = new[]
        {
            Row(1, "items", 1, "INSERT INTO items(id) VALUES (1)"),
            Row(2, "items", 2, "INSERT INTO items(id) VALUES (2)"),
        };
        var entries = ManagedReplicaConflictClassifier.Classify(
            batch,
            AhtolaReplicaConflictKind.RowWrite,
            conflictingSequence: 2);
        entries.Select(entry => entry.Eligibility).Should().Equal(
            AhtolaReplicaChangeEligibility.Eligible,
            AhtolaReplicaChangeEligibility.Conflicting);
        entries[0].Kind.Should().Be(AhtolaReplicaChangeKind.RowWrite);
        entries[0].Table.Should().Be("items");
        entries[0].RowId.Should().Be(1);
    }

    [Test]
    public void ClassifierMarksAnotherWriteToTheSameRowAsManual()
    {
        var batch = new[]
        {
            Row(1, "items", 7, "UPDATE items SET x = 'a' WHERE id = 7"),
            Row(2, "items", 7, "UPDATE items SET x = 'b' WHERE id = 7"),
            Row(3, "other", 7, "INSERT INTO other(id) VALUES (7)"),
        };
        var entries = ManagedReplicaConflictClassifier.Classify(
            batch,
            AhtolaReplicaConflictKind.RowWrite,
            conflictingSequence: 1);
        entries.Select(entry => entry.Eligibility).Should().Equal(
            AhtolaReplicaChangeEligibility.Conflicting,
            AhtolaReplicaChangeEligibility.RequiresManualResolution,
            AhtolaReplicaChangeEligibility.Eligible);
    }

    [Test]
    public void ClassifierPropagatesChainedSameRowWritesToManualToAnyDepth()
    {
        var batch = new[]
        {
            Row(1, "items", 1, "INSERT INTO items(id) VALUES (1)"),
            Row(2, "items", 5, "UPDATE items SET x = 'a' WHERE id = 5"),
            Row(3, "items", 5, "UPDATE items SET x = 'b' WHERE id = 5"),
            Row(4, "items", 5, "DELETE FROM items WHERE id = 5"),
        };
        var entries = ManagedReplicaConflictClassifier.Classify(
            batch,
            AhtolaReplicaConflictKind.RowWrite,
            conflictingSequence: 2);
        entries.Select(entry => entry.Eligibility).Should().Equal(
            AhtolaReplicaChangeEligibility.Eligible,
            AhtolaReplicaChangeEligibility.Conflicting,
            AhtolaReplicaChangeEligibility.RequiresManualResolution,
            AhtolaReplicaChangeEligibility.RequiresManualResolution);
    }

    [Test]
    public void ClassifierMarksEveryEntryConflictingForAnUnknownConflictKind()
    {
        var batch = new[]
        {
            Row(1, "items", 1, "INSERT INTO items(id) VALUES (1)"),
            Schema(2, "CREATE TABLE other(id INTEGER PRIMARY KEY)"),
        };
        var entries = ManagedReplicaConflictClassifier.Classify(
            batch,
            AhtolaReplicaConflictKind.Unknown,
            conflictingSequence: 1);
        entries.Should().OnlyContain(entry => entry.Eligibility == AhtolaReplicaChangeEligibility.Conflicting);
    }

    [Test]
    public void ClassifierFailsClosedWhenTheReportedSequenceIsNotInTheBatch()
    {
        var batch = new[]
        {
            Row(4, "items", 1, "INSERT INTO items(id) VALUES (1)"),
            Row(5, "items", 2, "INSERT INTO items(id) VALUES (2)"),
        };
        var entries = ManagedReplicaConflictClassifier.Classify(
            batch,
            AhtolaReplicaConflictKind.RowWrite,
            conflictingSequence: 99);
        entries.Should().OnlyContain(entry => entry.Eligibility == AhtolaReplicaChangeEligibility.Conflicting);
    }

    [Test]
    public void ClassifierFailsClosedWhenNoSequenceWasReportedAtAll()
    {
        var batch = new[] { Row(1, "items", 1, "INSERT INTO items(id) VALUES (1)") };
        var entries = ManagedReplicaConflictClassifier.Classify(
            batch,
            AhtolaReplicaConflictKind.RowWrite,
            conflictingSequence: null);
        entries.Should().OnlyContain(entry => entry.Eligibility == AhtolaReplicaChangeEligibility.Conflicting);
    }

    [Test]
    public void ClassifierMarksEverySchemaEntryManualForASchemaConflict()
    {
        var batch = new[]
        {
            Schema(1, "CREATE TABLE a(id INTEGER PRIMARY KEY)"),
            Schema(2, "CREATE TABLE b(id INTEGER PRIMARY KEY)"),
            Row(3, "unrelated", 1, "INSERT INTO unrelated(id) VALUES (1)"),
        };
        var entries = ManagedReplicaConflictClassifier.Classify(
            batch,
            AhtolaReplicaConflictKind.SchemaChange,
            conflictingSequence: 1);
        entries.Select(entry => entry.Eligibility).Should().Equal(
            AhtolaReplicaChangeEligibility.Conflicting,
            AhtolaReplicaChangeEligibility.RequiresManualResolution,
            AhtolaReplicaChangeEligibility.Eligible);
        entries[1].Kind.Should().Be(AhtolaReplicaChangeKind.SchemaChange);
        entries[1].Table.Should().Be("b");
        entries[1].RowId.Should().BeNull();
    }

    [Test]
    public void ClassifierMarksRowWritesOnATableWithAnUndecidedSchemaChangeManual()
    {
        var batch = new[]
        {
            Schema(1, "ALTER TABLE b ADD COLUMN x TEXT"),
            Row(2, "b", 1, "INSERT INTO b(id) VALUES (1)"),
            Row(3, "c", 1, "INSERT INTO c(id) VALUES (1)"),
            Schema(4, "CREATE TABLE d(id INTEGER PRIMARY KEY)"),
        };
        var entries = ManagedReplicaConflictClassifier.Classify(
            batch,
            AhtolaReplicaConflictKind.SchemaChange,
            conflictingSequence: 4);
        entries.Select(entry => entry.Eligibility).Should().Equal(
            AhtolaReplicaChangeEligibility.RequiresManualResolution,
            AhtolaReplicaChangeEligibility.RequiresManualResolution,
            AhtolaReplicaChangeEligibility.Eligible,
            AhtolaReplicaChangeEligibility.Conflicting);
    }

    [Test]
    public void ClassifierMarksASchemaChangeOnTheConflictingRowsTableManual()
    {
        var batch = new[]
        {
            Row(1, "items", 3, "UPDATE items SET x = 'a' WHERE id = 3"),
            Schema(2, "ALTER TABLE items ADD COLUMN y TEXT"),
            Schema(3, "CREATE TABLE elsewhere(id INTEGER PRIMARY KEY)"),
        };
        var entries = ManagedReplicaConflictClassifier.Classify(
            batch,
            AhtolaReplicaConflictKind.RowWrite,
            conflictingSequence: 1);
        entries.Select(entry => entry.Eligibility).Should().Equal(
            AhtolaReplicaChangeEligibility.Conflicting,
            AhtolaReplicaChangeEligibility.RequiresManualResolution,
            AhtolaReplicaChangeEligibility.Eligible);
    }

    [Test]
    public void ClassifierTreatsAnUnparsableSchemaTargetAsTouchingEveryTable()
    {
        var batch = new[]
        {
            Row(1, "items", 3, "UPDATE items SET x = 'a' WHERE id = 3"),
            Schema(2, "CREATE TRIGGER t AFTER INSERT ON items BEGIN SELECT 1; END"),
        };
        var entries = ManagedReplicaConflictClassifier.Classify(
            batch,
            AhtolaReplicaConflictKind.RowWrite,
            conflictingSequence: 1);
        entries[1].Eligibility.Should().Be(AhtolaReplicaChangeEligibility.RequiresManualResolution);
        entries[1].Table.Should().BeEmpty();
    }

    [Test]
    public void SchemaStatementTargetResolvesTheTableForEveryRecognizedShape()
    {
        ManagedReplicaSchemaDdlText.TryGetSchemaStatementTarget("CREATE TABLE IF NOT EXISTS main.items(id INTEGER)")
            .Should().Be("items");
        ManagedReplicaSchemaDdlText.TryGetSchemaStatementTarget("ALTER TABLE items ADD COLUMN x TEXT")
            .Should().Be("items");
        ManagedReplicaSchemaDdlText.TryGetSchemaStatementTarget("DROP TABLE IF EXISTS items")
            .Should().Be("items");
        ManagedReplicaSchemaDdlText.TryGetSchemaStatementTarget("CREATE UNIQUE INDEX idx ON items(x)")
            .Should().Be("items");
        ManagedReplicaSchemaDdlText.TryGetSchemaStatementTarget("DROP INDEX idx").Should().BeNull();
        ManagedReplicaSchemaDdlText.TryGetSchemaStatementTarget("CREATE TRIGGER t AFTER INSERT ON items BEGIN END")
            .Should().BeNull();
        ManagedReplicaSchemaDdlText.TryGetSchemaStatementTarget("VACUUM").Should().BeNull();
    }

    // ---------------------------------------------------------------------------------------
    // Durable conflict marker.
    // ---------------------------------------------------------------------------------------
    [Test]
    public void ConflictMarkerRoundTripsEveryRecordedField()
    {
        var path = NewReplicaPath("conflict-marker-roundtrip");
        try
        {
            var state = new ManagedReplicaConflictState(
                AhtolaReplicaConflictKind.RowWrite,
                "SQLITE_CONSTRAINT",
                ConflictingSequence: 3,
                BatchFirstSequence: 1,
                BatchWatermark: 5,
                UnresolvedSequences: [3, 4]);
            ManagedReplicaConflictState.Write(path, state);
            ManagedReplicaConflictState.Exists(path).Should().BeTrue();
            var read = ManagedReplicaConflictState.TryRead(path)!.Value;
            read.ConflictKind.Should().Be(AhtolaReplicaConflictKind.RowWrite);
            read.RemoteErrorCode.Should().Be("SQLITE_CONSTRAINT");
            read.ConflictingSequence.Should().Be(3);
            read.BatchFirstSequence.Should().Be(1);
            read.BatchWatermark.Should().Be(5);
            read.UnresolvedSequences.Should().Equal(3L, 4L);
            ManagedReplicaConflictState.Delete(path);
            ManagedReplicaConflictState.TryRead(path).Should().BeNull();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void ConflictMarkerRoundTripsAMissingRemoteErrorCodeAndSequence()
    {
        var path = NewReplicaPath("conflict-marker-null-fields");
        try
        {
            ManagedReplicaConflictState.Write(
                path,
                new ManagedReplicaConflictState(
                    AhtolaReplicaConflictKind.Unknown,
                    RemoteErrorCode: null,
                    ConflictingSequence: null,
                    BatchFirstSequence: 2,
                    BatchWatermark: 3,
                    UnresolvedSequences: [2]));
            var read = ManagedReplicaConflictState.TryRead(path)!.Value;
            read.RemoteErrorCode.Should().BeNull();
            read.ConflictingSequence.Should().BeNull();
            read.ConflictKind.Should().Be(AhtolaReplicaConflictKind.Unknown);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [TestCase(ConflictMarkerCorruption.BadMagic)]
    [TestCase(ConflictMarkerCorruption.BadVersion)]
    [TestCase(ConflictMarkerCorruption.Truncated)]
    [TestCase(ConflictMarkerCorruption.TrailingBytes)]
    [TestCase(ConflictMarkerCorruption.UnknownConflictKind)]
    [TestCase(ConflictMarkerCorruption.EmptyUnresolvedSet)]
    [TestCase(ConflictMarkerCorruption.UnresolvedSequenceOutsideBatch)]
    [TestCase(ConflictMarkerCorruption.UnorderedUnresolvedSet)]
    [TestCase(ConflictMarkerCorruption.ConflictingSequenceOutsideBatch)]
    [TestCase(ConflictMarkerCorruption.InvertedBatchRange)]
    public void ConflictMarkerFailsClosedOnCorruptContent(ConflictMarkerCorruption corruption)
    {
        var path = NewReplicaPath("conflict-marker-corrupt");
        try
        {
            File.WriteAllBytes(
                ManagedReplicaConflictState.GetPath(path),
                BuildCorruptConflictMarker(corruption));
            Assert.Throws<InvalidDataException>(() => ManagedReplicaConflictState.TryRead(path));
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void ConflictMarkerRefusesToRecordAnEmptyUnresolvedSet()
    {
        var path = NewReplicaPath("conflict-marker-empty");
        try
        {
            Assert.Throws<InvalidOperationException>(() => ManagedReplicaConflictState.Write(
                path,
                new ManagedReplicaConflictState(
                    AhtolaReplicaConflictKind.RowWrite,
                    null,
                    1,
                    1,
                    2,
                    [])));
            File.Exists(ManagedReplicaConflictState.GetPath(path)).Should().BeFalse();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void GetLocalArtifactPathsIncludesTheConflictMarkerSidecar()
    {
        var path = NewReplicaPath("conflict-artifact-paths");
        ManagedReplicaBootstrapper.GetLocalArtifactPaths(path)
            .Should().Contain(path + ManagedReplicaConflictState.Suffix);
    }

    // ---------------------------------------------------------------------------------------
    // Journal discard.
    // ---------------------------------------------------------------------------------------
    [Test]
    public void JournalDiscardRemovesOnlyTheRequestedPendingChangesAndNeverMovesTheWatermark()
    {
        var path = NewReplicaPath("conflict-journal-discard");
        try
        {
            var journal = ManagedReplicaChangeJournal.Open(path);
            journal.AppendCommitted(
            [
                Row(0, "items", 1, "INSERT INTO items(id) VALUES (1)"),
                Row(0, "items", 2, "INSERT INTO items(id) VALUES (2)"),
                Row(0, "items", 3, "INSERT INTO items(id) VALUES (3)"),
            ]);
            journal.DiscardUnacknowledged([2, 3]).Should().Be(2);
            // The pending batch keeps its original first sequence (the watermark never moved), so
            // a discard can never be mistaken for a remote acknowledgement.
            var batch = journal.ReadBatch(int.MaxValue);
            batch.FirstSequence.Should().Be(1);
            batch.Changes.Select(change => change.Sequence).Should().Equal(1L);
            // Removing the tail is durable across reopen: the on-disk format allows the retained
            // set to end below the assigned high-water mark.
            var reopened = ManagedReplicaChangeJournal.Open(path);
            reopened.ReadBatch(int.MaxValue).Changes.Select(change => change.Sequence).Should().Equal(1L);
            // A later local write still gets a fresh, strictly higher sequence: monotonicity is
            // preserved even though sequences 2 and 3 no longer exist.
            reopened.AppendCommitted([Row(0, "items", 9, "INSERT INTO items(id) VALUES (9)")]);
            reopened.ReadBatch(int.MaxValue).Changes.Select(change => change.Sequence).Should().Equal(1L, 4L);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void JournalDiscardCanEmptyTheRetainedSetEntirely()
    {
        var path = NewReplicaPath("conflict-journal-discard-all");
        try
        {
            var journal = ManagedReplicaChangeJournal.Open(path);
            journal.AppendCommitted([Row(0, "items", 1, "INSERT INTO items(id) VALUES (1)")]);
            journal.DiscardUnacknowledged([1]).Should().Be(1);
            ManagedReplicaChangeJournal.Open(path).ReadBatch(int.MaxValue).Changes.Should().BeEmpty();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void JournalDiscardRejectsAnAlreadyAcknowledgedSequence()
    {
        var path = NewReplicaPath("conflict-journal-discard-acked");
        try
        {
            var journal = ManagedReplicaChangeJournal.Open(path);
            journal.AppendCommitted(
            [
                Row(0, "items", 1, "INSERT INTO items(id) VALUES (1)"),
                Row(0, "items", 2, "INSERT INTO items(id) VALUES (2)"),
            ]);
            journal.Acknowledge(2);
            Assert.Throws<InvalidOperationException>(() => journal.DiscardUnacknowledged([1]));
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void JournalDiscardFailsClosedOnASequenceItDoesNotRetain()
    {
        var path = NewReplicaPath("conflict-journal-discard-missing");
        try
        {
            var journal = ManagedReplicaChangeJournal.Open(path);
            journal.AppendCommitted([Row(0, "items", 1, "INSERT INTO items(id) VALUES (1)")]);
            Assert.Throws<InvalidDataException>(() => journal.DiscardUnacknowledged([1, 2]));
            journal.ReadBatch(int.MaxValue).Changes.Should().ContainSingle();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void JournalDiscardRejectsDuplicateSequences()
    {
        var path = NewReplicaPath("conflict-journal-discard-duplicate");
        try
        {
            var journal = ManagedReplicaChangeJournal.Open(path);
            journal.AppendCommitted([Row(0, "items", 1, "INSERT INTO items(id) VALUES (1)")]);
            Assert.Throws<ArgumentException>(() => journal.DiscardUnacknowledged([1, 1]));
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    // ---------------------------------------------------------------------------------------
    // End-to-end conflict lifecycle.
    // ---------------------------------------------------------------------------------------
    [Test]
    public void SyncRecordsADurableConflictMarkerAndThenRefusesToPushAgain()
    {
        var path = NewReplicaPath("conflict-blocks-sync");
        var image = CreateJournalDatabaseImage(path + ".source");
        var handler = ConflictHandler.RowConflict([CreatePagePullResponse("revision-42", image)]);
        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
            var conflict = Assert.ThrowsAsync<AhtolaReplicaConflictException>(
                () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            conflict!.ConflictKind.Should().Be(AhtolaReplicaConflictKind.RowWrite);
            File.Exists(ManagedReplicaConflictState.GetPath(path)).Should().BeTrue();
            var marker = ManagedReplicaConflictState.TryRead(path)!.Value;
            marker.BatchFirstSequence.Should().Be(1);
            marker.BatchWatermark.Should().Be(2);
            marker.ConflictingSequence.Should().Be(1);
            marker.UnresolvedSequences.Should().Equal(1L);
            // Every later synchronization attempt fails closed without a second push.
            var pending = Assert.ThrowsAsync<AhtolaReplicaConflictPendingException>(
                () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            pending!.UnresolvedChangeCount.Should().Be(1);
            pending.ConflictKind.Should().Be(AhtolaReplicaConflictKind.RowWrite);
            pending.ReplicaPushFailureKind.Should().Be(AhtolaReplicaPushFailureKind.Conflict);
            handler.PushCallCount.Should().Be(1);
            // The rejected change is still durably journaled: nothing was acknowledged.
            ManagedReplicaChangeJournal.Open(path).ReadBatch(int.MaxValue).Changes
                .Select(change => change.Sequence).Should().Equal(1L);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task ConflictMarkerStillBlocksSynchronizationAfterReopeningTheReplica()
    {
        var path = NewReplicaPath("conflict-restart-idempotent");
        var image = CreateJournalDatabaseImage(path + ".source");
        var handler = ConflictHandler.RowConflict([CreatePagePullResponse("revision-42", image)]);
        var options = CreateOptions(path, handler);
        try
        {
            using (var connection = AhtolaConnection.CreateReplica(options))
            {
                connection.Open();
                connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
                Assert.ThrowsAsync<AhtolaReplicaConflictException>(
                    () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            }
            using var reopened = AhtolaConnection.CreateReplica(options);
            reopened.Open();
            Assert.ThrowsAsync<AhtolaReplicaConflictPendingException>(
                () => reopened.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            var report = await reopened.InspectReplicaConflictAsync();
            report.Should().NotBeNull();
            report!.UnresolvedEntries.Select(entry => entry.Sequence).Should().Equal(1L);
            handler.PushCallCount.Should().Be(1);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task InspectReplicaConflictReturnsNullWhenNothingIsRecorded()
    {
        var path = NewReplicaPath("conflict-inspect-empty");
        var image = CreateJournalDatabaseImage(path + ".source");
        var handler = ConflictHandler.NoPush([CreatePagePullResponse("revision-42", image)]);
        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            (await connection.InspectReplicaConflictAsync()).Should().BeNull();
            File.Exists(ManagedReplicaConflictState.GetPath(path)).Should().BeFalse();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task InspectReplicaConflictClassifiesTheWholeRejectedBatch()
    {
        var path = NewReplicaPath("conflict-inspect-batch");
        var image = CreateJournalDatabaseImage(path + ".source");
        // Steps: BEGIN, CREATE TABLE IF NOT EXISTS, change 1, change 2, watermark, COMMIT.
        var handler = ConflictHandler.Conflict(
            [CreatePagePullResponse("revision-42", image)],
            stepCount: 6,
            errorStep: 3,
            code: "SQLITE_CONSTRAINT");
        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (20);");
            Assert.ThrowsAsync<AhtolaReplicaConflictException>(
                () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            var report = (await connection.InspectReplicaConflictAsync())!;
            report.ConflictKind.Should().Be(AhtolaReplicaConflictKind.RowWrite);
            report.RemoteErrorCode.Should().Be("SQLITE_CONSTRAINT");
            report.ConflictingSequence.Should().Be(2);
            report.BatchFirstSequence.Should().Be(1);
            report.BatchWatermark.Should().Be(3);
            report.Entries.Select(entry => entry.Sequence).Should().Equal(1L, 2L);
            report.EligibleEntries.Select(entry => entry.Sequence).Should().Equal(1L);
            report.UnresolvedEntries.Select(entry => entry.Sequence).Should().Equal(2L);
            report.Entries.Should().OnlyContain(entry => entry.Kind == AhtolaReplicaChangeKind.RowWrite);
            report.Entries[0].Table.Should().Be("journal_events");
            // Inspection is a pure read: it neither clears nor rewrites the durable marker.
            ManagedReplicaConflictState.TryRead(path)!.Value.UnresolvedSequences.Should().Equal(2L);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void InspectReplicaConflictFailsClosedWhenTheMarkerAndJournalDisagree()
    {
        var path = NewReplicaPath("conflict-inspect-stale");
        var image = CreateJournalDatabaseImage(path + ".source");
        var handler = ConflictHandler.Conflict(
            [CreatePagePullResponse("revision-42", image)],
            stepCount: 6,
            errorStep: 3,
            code: "SQLITE_CONSTRAINT");
        var options = CreateOptions(path, handler);
        try
        {
            using (var connection = AhtolaConnection.CreateReplica(options))
            {
                connection.Open();
                connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
                connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (20);");
                Assert.ThrowsAsync<AhtolaReplicaConflictException>(
                    () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            }
            // Rewrite the marker so it claims two unresolved sequences while the journal only
            // still retains one of them, and the other left retention WITHOUT a recorded discard:
            // a genuinely inconsistent pair whose missing entry has no durable explanation, which
            // must never be reinterpreted against whatever the journal happens to hold.
            var state = ManagedReplicaConflictState.TryRead(path)!.Value;
            ManagedReplicaConflictState.Write(path, state with { UnresolvedSequences = [1, 2] });
            ManagedReplicaChangeJournal.Open(path).Acknowledge(2);
            using var reopened = AhtolaConnection.CreateReplica(options);
            reopened.Open();
            Assert.ThrowsAsync<InvalidDataException>(() => reopened.InspectReplicaConflictAsync());
            Assert.ThrowsAsync<InvalidDataException>(
                () => reopened.ResolveReplicaConflictAsync(
                    AhtolaReplicaConflictResolution.DiscardUnresolvedChanges,
                    new AhtolaReplicaConflictResolutionOptions { AcknowledgeDataLoss = true }));
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task ResolveRebaseReplaysOnlyEligibleChangesAndKeepsTheConflictOpen()
    {
        var path = NewReplicaPath("conflict-rebase-eligible");
        var image = CreateLogicalSourceImage(path + ".source");
        var (logicalBody, rangeMessage) = BuildSimpleLogicalPullBody(
            tableName: "remote_items",
            rowId: 2,
            columnValue: "remote",
            schemaSql: "CREATE TABLE remote_items(id INTEGER PRIMARY KEY, x TEXT)",
            salt: 7100UL);
        var handler = ConflictHandler.Conflict(
            [
                CreatePagePullResponse("revision-42", image, protocol: 2),
                CreateLogicalPullResponse("revision-42", []),
                CreateLogicalPullResponse("revision-43", logicalBody, [rangeMessage]),
            ],
            stepCount: 6,
            errorStep: 3,
            code: "SQLITE_CONSTRAINT");
        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (20);");
            Assert.ThrowsAsync<AhtolaReplicaConflictException>(
                () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            var progress = new ProgressRecorder();
            var result = await connection.ResolveReplicaConflictAsync(
                AhtolaReplicaConflictResolution.PullAndRebaseEligible,
                new AhtolaReplicaConflictResolutionOptions { Progress = progress });
            result.Resolution.Should().Be(AhtolaReplicaConflictResolution.PullAndRebaseEligible);
            result.ConflictCleared.Should().BeFalse("unresolved entries are still quarantined");
            result.RebasedChangeCount.Should().Be(1);
            result.DiscardedChangeCount.Should().Be(0);
            result.SyncResult!.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);
            result.RemainingConflict!.UnresolvedEntries.Select(entry => entry.Sequence).Should().Equal(2L);
            progress.Stages.Should().Contain(AhtolaSyncProgressStage.Pulling);
            // The freshly pulled remote row is present, the eligible local row survived the
            // rebase, and the quarantined local row lost to the newly pulled base.
            ReadScalar(connection, "SELECT x FROM remote_items WHERE id = 2;").Should().Be("remote");
            ReadJournalEventValues(connection).Should().Equal(10);
            // The conflict is still open: the marker is retained and synchronization stays blocked.
            File.Exists(ManagedReplicaConflictState.GetPath(path)).Should().BeTrue();
            Assert.ThrowsAsync<AhtolaReplicaConflictPendingException>(
                () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            handler.PushCallCount.Should().Be(1);
            // Both changes are still journaled and unacknowledged; the rebase pushed nothing.
            ManagedReplicaChangeJournal.Open(path).ReadBatch(int.MaxValue).Changes
                .Select(change => change.Sequence).Should().Equal(1L, 2L);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task ResolveRebaseThenDiscardClearsTheConflictAndLetsTheEligibleChangePush()
    {
        var path = NewReplicaPath("conflict-rebase-then-discard");
        var image = CreateLogicalSourceImage(path + ".source");
        var (logicalBody, rangeMessage) = BuildSimpleLogicalPullBody(
            tableName: "remote_items",
            rowId: 2,
            columnValue: "remote",
            schemaSql: "CREATE TABLE remote_items(id INTEGER PRIMARY KEY, x TEXT)",
            salt: 7101UL);
        var handler = ConflictHandler.ConflictThenSuccess(
            [
                CreatePagePullResponse("revision-42", image, protocol: 2),
                CreateLogicalPullResponse("revision-42", []),
                CreateLogicalPullResponse("revision-43", logicalBody, [rangeMessage]),
                CreateLogicalPullResponse("revision-43", []),
            ],
            conflictStepCount: 6,
            conflictErrorStep: 3);
        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (20);");
            Assert.ThrowsAsync<AhtolaReplicaConflictException>(
                () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            _ = await connection.ResolveReplicaConflictAsync(
                AhtolaReplicaConflictResolution.PullAndRebaseEligible);
            // Discarding without acknowledging data loss is refused before any I/O.
            Assert.ThrowsAsync<InvalidOperationException>(
                () => connection.ResolveReplicaConflictAsync(
                    AhtolaReplicaConflictResolution.DiscardUnresolvedChanges));
            ManagedReplicaChangeJournal.Open(path).ReadBatch(int.MaxValue).Changes.Should().HaveCount(2);
            var discard = await connection.ResolveReplicaConflictAsync(
                AhtolaReplicaConflictResolution.DiscardUnresolvedChanges,
                new AhtolaReplicaConflictResolutionOptions { AcknowledgeDataLoss = true });
            discard.ConflictCleared.Should().BeTrue();
            discard.DiscardedChangeCount.Should().Be(1);
            discard.RemainingConflict.Should().BeNull();
            discard.SyncResult.Should().BeNull("a discard never contacts the remote endpoint");
            File.Exists(ManagedReplicaConflictState.GetPath(path)).Should().BeFalse();
            (await connection.InspectReplicaConflictAsync()).Should().BeNull();
            // The eligible change is still pending and now pushes normally.
            ManagedReplicaChangeJournal.Open(path).ReadBatch(int.MaxValue).Changes
                .Select(change => change.Sequence).Should().Equal(1L);
            _ = await connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
            handler.PushCallCount.Should().Be(2);
            handler.LastPushedStatements.Should().Contain(sql => sql.Contains("VALUES (10)", StringComparison.Ordinal));
            handler.LastPushedStatements.Should().NotContain(sql => sql.Contains("VALUES (20)", StringComparison.Ordinal));
            ManagedReplicaChangeJournal.Open(path).ReadBatch(int.MaxValue).Changes.Should().BeEmpty();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task ResolveDiscardWithoutRebaseClearsTheConflictWithoutContactingTheRemote()
    {
        var path = NewReplicaPath("conflict-discard-only");
        var image = CreateJournalDatabaseImage(path + ".source");
        var handler = ConflictHandler.RowConflict([CreatePagePullResponse("revision-42", image)]);
        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
            Assert.ThrowsAsync<AhtolaReplicaConflictException>(
                () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            var pullsBefore = handler.PullCallCount;
            var result = await connection.ResolveReplicaConflictAsync(
                AhtolaReplicaConflictResolution.DiscardUnresolvedChanges,
                new AhtolaReplicaConflictResolutionOptions { AcknowledgeDataLoss = true });
            result.ConflictCleared.Should().BeTrue();
            result.DiscardedChangeCount.Should().Be(1);
            handler.PullCallCount.Should().Be(pullsBefore);
            handler.PushCallCount.Should().Be(1);
            ManagedReplicaChangeJournal.Open(path).ReadBatch(int.MaxValue).Changes.Should().BeEmpty();
            // Resolving again has nothing to resolve and says so rather than silently succeeding.
            Assert.ThrowsAsync<InvalidOperationException>(
                () => connection.ResolveReplicaConflictAsync(
                    AhtolaReplicaConflictResolution.DiscardUnresolvedChanges,
                    new AhtolaReplicaConflictResolutionOptions { AcknowledgeDataLoss = true }));
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task ResolveCompletesADiscardThatCrashedBeforeTheMarkerWasRetired()
    {
        var path = NewReplicaPath("conflict-discard-crash");
        var image = CreateJournalDatabaseImage(path + ".source");
        var handler = ConflictHandler.RowConflict([CreatePagePullResponse("revision-42", image)]);
        var options = CreateOptions(path, handler);
        try
        {
            using (var connection = AhtolaConnection.CreateReplica(options))
            {
                connection.Open();
                connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
                Assert.ThrowsAsync<AhtolaReplicaConflictException>(
                    () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
                using var fault = ManagedReplicaFaultInjection.Push(boundary =>
                {
                    if (boundary == ManagedReplicaDurableBoundary.JournalDiscardPersisted)
                        throw new IOException("simulated crash after the journal discard was durable");
                });
                Assert.ThrowsAsync<IOException>(
                    () => connection.ResolveReplicaConflictAsync(
                        AhtolaReplicaConflictResolution.DiscardUnresolvedChanges,
                        new AhtolaReplicaConflictResolutionOptions { AcknowledgeDataLoss = true }));
            }
            // Durable state after the "crash": the journal discard landed, the marker did not.
            ManagedReplicaChangeJournal.Open(path).ReadBatch(int.MaxValue).Changes.Should().BeEmpty();
            File.Exists(ManagedReplicaConflictState.GetPath(path)).Should().BeTrue();
            using var reopened = AhtolaConnection.CreateReplica(options);
            reopened.Open();
            Assert.ThrowsAsync<AhtolaReplicaConflictPendingException>(
                () => reopened.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            var completion = await reopened.ResolveReplicaConflictAsync(
                AhtolaReplicaConflictResolution.DiscardUnresolvedChanges,
                new AhtolaReplicaConflictResolutionOptions { AcknowledgeDataLoss = true });
            completion.ConflictCleared.Should().BeTrue();
            completion.DiscardedChangeCount.Should().Be(0, "the discard itself already landed");
            File.Exists(ManagedReplicaConflictState.GetPath(path)).Should().BeFalse();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task RebaseThatCrashesBeforeMetadataPublicationRetainsTheMarkerAndRetriesCleanly()
    {
        var path = NewReplicaPath("conflict-rebase-crash");
        var image = CreateLogicalSourceImage(path + ".source");
        var (logicalBody, rangeMessage) = BuildSimpleLogicalPullBody(
            tableName: "remote_items",
            rowId: 2,
            columnValue: "remote",
            schemaSql: "CREATE TABLE remote_items(id INTEGER PRIMARY KEY, x TEXT)",
            salt: 7102UL);
        var handler = ConflictHandler.Conflict(
            [
                CreatePagePullResponse("revision-42", image, protocol: 2),
                CreateLogicalPullResponse("revision-42", []),
                CreateLogicalPullResponse("revision-43", logicalBody, [rangeMessage]),
                CreateLogicalPullResponse("revision-43", logicalBody, [rangeMessage]),
            ],
            stepCount: 6,
            errorStep: 3,
            code: "SQLITE_CONSTRAINT");
        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (20);");
            Assert.ThrowsAsync<AhtolaReplicaConflictException>(
                () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            var markerBefore = File.ReadAllBytes(ManagedReplicaConflictState.GetPath(path));
            var revisionBefore = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.Revision;
            using (ManagedReplicaFaultInjection.Push(boundary =>
            {
                if (boundary == ManagedReplicaDurableBoundary.LogicalApplyCommitted)
                    throw new IOException("simulated crash before metadata publication");
            }))
            {
                Assert.ThrowsAsync<IOException>(
                    () => connection.ResolveReplicaConflictAsync(
                        AhtolaReplicaConflictResolution.PullAndRebaseEligible));
            }
            // Nothing durable moved: metadata is still the pre-rebase revision, the journal is
            // untouched, and the marker is byte-identical, so a retry reclassifies identically.
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.Revision.Should().Be(revisionBefore);
            File.ReadAllBytes(ManagedReplicaConflictState.GetPath(path)).Should().Equal(markerBefore);
            ManagedReplicaChangeJournal.Open(path).ReadBatch(int.MaxValue).Changes
                .Select(change => change.Sequence).Should().Equal(1L, 2L);
            var retry = await connection.ResolveReplicaConflictAsync(
                AhtolaReplicaConflictResolution.PullAndRebaseEligible);
            retry.SyncResult!.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);
            retry.ConflictCleared.Should().BeFalse();
            ReadJournalEventValues(connection).Should().Equal(10);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void RebaseCancelledBeforePublicationLeavesTheMarkerAndJournalUntouched()
    {
        var path = NewReplicaPath("conflict-rebase-cancel");
        var image = CreateLogicalSourceImage(path + ".source");
        var (logicalBody, rangeMessage) = BuildSimpleLogicalPullBody(
            tableName: "remote_items",
            rowId: 2,
            columnValue: "remote",
            schemaSql: "CREATE TABLE remote_items(id INTEGER PRIMARY KEY, x TEXT)",
            salt: 7103UL);
        var handler = ConflictHandler.Conflict(
            [
                CreatePagePullResponse("revision-42", image, protocol: 2),
                CreateLogicalPullResponse("revision-42", []),
                CreateLogicalPullResponse("revision-43", logicalBody, [rangeMessage]),
            ],
            stepCount: 6,
            errorStep: 3,
            code: "SQLITE_CONSTRAINT");
        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (20);");
            Assert.ThrowsAsync<AhtolaReplicaConflictException>(
                () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            var markerBefore = File.ReadAllBytes(ManagedReplicaConflictState.GetPath(path));
            var revisionBefore = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.Revision;
            using var cancellation = new CancellationTokenSource();
            using (ManagedReplicaFaultInjection.Push(boundary =>
            {
                if (boundary == ManagedReplicaDurableBoundary.ReplicaApplyLockAcquired)
                    cancellation.Cancel();
            }))
            {
                Assert.CatchAsync<OperationCanceledException>(
                    () => connection.ResolveReplicaConflictAsync(
                        AhtolaReplicaConflictResolution.PullAndRebaseEligible,
                        options: null,
                        cancellation.Token));
            }
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.Revision.Should().Be(revisionBefore);
            File.ReadAllBytes(ManagedReplicaConflictState.GetPath(path)).Should().Equal(markerBefore);
            ManagedReplicaChangeJournal.Open(path).ReadBatch(int.MaxValue).Changes
                .Select(change => change.Sequence).Should().Equal(1L, 2L);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void RebaseFailsClosedForAPageProtocolReplica()
    {
        var path = NewReplicaPath("conflict-rebase-page-protocol");
        var image = CreateJournalDatabaseImage(path + ".source");
        var handler = ConflictHandler.RowConflict([CreatePagePullResponse("revision-42", image)]);
        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
            Assert.ThrowsAsync<AhtolaReplicaConflictException>(
                () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            var pullsBefore = handler.PullCallCount;
            Assert.ThrowsAsync<NotSupportedException>(
                () => connection.ResolveReplicaConflictAsync(
                    AhtolaReplicaConflictResolution.PullAndRebaseEligible));
            handler.PullCallCount.Should().Be(pullsBefore, "the rebase must fail before any request");
            File.Exists(ManagedReplicaConflictState.GetPath(path)).Should().BeTrue();
            ManagedReplicaChangeJournal.Open(path).ReadBatch(int.MaxValue).Changes.Should().ContainSingle();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void RebaseFailsClosedWhileAnUnresolvedSchemaChangeIsQuarantined()
    {
        var path = NewReplicaPath("conflict-rebase-schema");
        var image = CreateLogicalSourceImage(path + ".source");
        var handler = ConflictHandler.Conflict(
            [
                CreatePagePullResponse("revision-42", image, protocol: 2),
                CreateLogicalPullResponse("revision-42", []),
            ],
            stepCount: 5,
            errorStep: 2,
            code: "SQLITE_SCHEMA");
        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            connection.ExecuteNonQuery("CREATE TABLE local_only(value INTEGER NOT NULL);");
            var conflict = Assert.ThrowsAsync<AhtolaReplicaConflictException>(
                () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            conflict!.ConflictKind.Should().Be(AhtolaReplicaConflictKind.SchemaChange);
            var pullsBefore = handler.PullCallCount;
            Assert.ThrowsAsync<NotSupportedException>(
                () => connection.ResolveReplicaConflictAsync(
                    AhtolaReplicaConflictResolution.PullAndRebaseEligible));
            handler.PullCallCount.Should().Be(pullsBefore);
            File.Exists(ManagedReplicaConflictState.GetPath(path)).Should().BeTrue();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task AutomaticSyncStopsRetryingWhileAConflictIsPending()
    {
        AhtolaConnection
            .IsTransientAutomaticSyncFailure(
                new AhtolaReplicaConflictPendingException(
                    "pending",
                    AhtolaReplicaConflictKind.RowWrite,
                    unresolvedChangeCount: 1),
                CancellationToken.None)
            .Should().BeFalse();
        var path = NewReplicaPath("conflict-automatic-sync");
        var image = CreateJournalDatabaseImage(path + ".source");
        var handler = ConflictHandler.RowConflict([CreatePagePullResponse("revision-42", image)]);
        try
        {
            using var connection = AhtolaConnection.CreateReplica(
                CreateOptions(path, handler, syncInterval: 1));
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
            Assert.ThrowsAsync<AhtolaReplicaConflictException>(
                () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            var pushesAfterConflict = handler.PushCallCount;
            await Task.Delay(300);
            // The automatic sync loop keeps failing closed on the durable marker instead of
            // re-pushing the rejected batch.
            handler.PushCallCount.Should().Be(pushesAfterConflict);
            File.Exists(ManagedReplicaConflictState.GetPath(path)).Should().BeTrue();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void ConflictApisRequireAManagedReplicaConnection()
    {
        var path = NewReplicaPath("conflict-not-a-replica");
        try
        {
            using var connection = new AhtolaConnection($"Data Source={path};Local Provider=Managed");
            connection.Open();
            Assert.ThrowsAsync<NotSupportedException>(() => connection.InspectReplicaConflictAsync());
            Assert.ThrowsAsync<NotSupportedException>(
                () => connection.ResolveReplicaConflictAsync(
                    AhtolaReplicaConflictResolution.PullAndRebaseEligible));
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Conflict-rebase review regressions.
    // ---------------------------------------------------------------------------------------
    [Test]
    public void JournalRecordsEveryDiscardedSequenceAndStaysGapAwareThroughPushAcknowledgeAndReopen()
    {
        // The 1,2,3 / discard-2 / push / acknowledge / crash shape the review called out: an
        // interior discard leaves a hole, and every consumer of the journal must be gap-aware
        // instead of demanding a contiguous run.
        var path = NewReplicaPath("conflict-journal-gap-aware");
        try
        {
            var journal = ManagedReplicaChangeJournal.Open(path);
            journal.AppendCommitted(
            [
                Row(0, "items", 1, "INSERT INTO items(id) VALUES (1)"),
                Row(0, "items", 2, "INSERT INTO items(id) VALUES (2)"),
                Row(0, "items", 3, "INSERT INTO items(id) VALUES (3)"),
            ]);
            journal.DiscardUnacknowledged([2]).Should().Be(1);
            journal.DiscardedSequences.Should().Equal(2L);
            // A push reads across the hole and still spans it with its acknowledgement watermark.
            var batch = journal.ReadBatch(int.MaxValue);
            batch.FirstSequence.Should().Be(1);
            batch.Watermark.Should().Be(4);
            batch.Changes.Select(change => change.Sequence).Should().Equal(1L, 3L);
            // Protected-push recovery re-reads exactly the attempted range despite the hole,
            // because every missing sequence in it is a durably recorded discard.
            journal.ReadBatch(1, 4).Changes.Select(change => change.Sequence).Should().Equal(1L, 3L);
            journal.Acknowledge(4);
            journal.ReadAcknowledged(1).Select(change => change.Sequence).Should().Equal(1L, 3L);
            // "Crash" and reopen: the discard record, the hole, and the watermark are all durable.
            var reopened = ManagedReplicaChangeJournal.Open(path);
            reopened.DiscardedSequences.Should().Equal(2L);
            reopened.AcknowledgedWatermark.Should().Be(4);
            reopened.AssignedSequence.Should().Be(3);
            reopened.ReadAcknowledged(1).Select(change => change.Sequence).Should().Equal(1L, 3L);
            reopened.ReadBatch(int.MaxValue).Changes.Should().BeEmpty();
            // Pruning retires the retained history and the discard record together, so the record
            // stays bounded instead of growing forever.
            reopened.PruneAcknowledged(4);
            var pruned = ManagedReplicaChangeJournal.Open(path);
            pruned.DiscardedSequences.Should().BeEmpty();
            pruned.RetentionBase.Should().Be(4);
            // Monotonicity survives: the next local write never reuses a discarded sequence.
            pruned.AppendCommitted([Row(0, "items", 9, "INSERT INTO items(id) VALUES (9)")]);
            pruned.ReadBatch(int.MaxValue).Changes.Select(change => change.Sequence).Should().Equal(4L);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void JournalFailsClosedWhenAGapHasNoRecordedDiscard()
    {
        var path = NewReplicaPath("conflict-journal-unexplained-gap");
        try
        {
            File.WriteAllBytes(
                path + ManagedReplicaChangeJournal.Suffix,
                BuildJournalFile(version: 7, sequence: 3, watermark: 1, retained: [1, 3], discarded: []));
            Assert.Throws<InvalidDataException>(() => ManagedReplicaChangeJournal.Open(path));
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void JournalFailsClosedWhenASequenceIsBothRetainedAndDiscarded()
    {
        var path = NewReplicaPath("conflict-journal-double-recorded");
        try
        {
            File.WriteAllBytes(
                path + ManagedReplicaChangeJournal.Suffix,
                BuildJournalFile(version: 7, sequence: 2, watermark: 1, retained: [1, 2], discarded: [2]));
            Assert.Throws<InvalidDataException>(() => ManagedReplicaChangeJournal.Open(path));
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void JournalFailsClosedOnALegacyHeaderThatImpliesAnImplausibleDiscardSpan()
    {
        // `sequence` is not bounded by the file's own length, so a corrupt pre-format-7 header
        // must be rejected up front rather than driving an unbounded gap reconstruction.
        var path = NewReplicaPath("conflict-journal-implausible-span");
        try
        {
            File.WriteAllBytes(
                path + ManagedReplicaChangeJournal.Suffix,
                BuildJournalFile(version: 6, sequence: 1L << 40, watermark: 1, retained: [], discarded: []));
            Assert.Throws<InvalidDataException>(() => ManagedReplicaChangeJournal.Open(path));
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void JournalFailsClosedOnASaturatedAssignedSequence()
    {
        var path = NewReplicaPath("conflict-journal-saturated-sequence");
        try
        {
            File.WriteAllBytes(
                path + ManagedReplicaChangeJournal.Suffix,
                BuildJournalFile(version: 7, sequence: long.MaxValue, watermark: 1, retained: [], discarded: []));
            Assert.Throws<InvalidDataException>(() => ManagedReplicaChangeJournal.Open(path));
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void JournalAdoptsALegacyFormatSixGapAsARecordedDiscardAndUpgradesOnTheNextPersist()
    {
        // Format 6 allowed holes without recording why. The only interpretation consistent with
        // how the journal is written is "an explicit discard removed it", so an older file keeps
        // opening and becomes exact on its next persist.
        var path = NewReplicaPath("conflict-journal-legacy-six");
        try
        {
            File.WriteAllBytes(
                path + ManagedReplicaChangeJournal.Suffix,
                BuildJournalFile(version: 6, sequence: 3, watermark: 1, retained: [1, 3], discarded: []));
            var journal = ManagedReplicaChangeJournal.Open(path);
            journal.DiscardedSequences.Should().Equal(2L);
            journal.WasDiscarded(2).Should().BeTrue();
            journal.Acknowledge(4);
            ManagedReplicaChangeJournal.Open(path).DiscardedSequences.Should().Equal(2L);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task DiscardingAnInteriorSequenceStillPushesAcknowledgesAndAdvancesTheJournalBase()
    {
        // End-to-end 1,2,3 / discard-2: the conflict quarantines only the middle change, the
        // discard leaves an interior hole, and the next ordinary sync must still push the
        // surviving pair, acknowledge across the hole, and advance the recorded journal base.
        var path = NewReplicaPath("conflict-interior-discard-push");
        var image = CreateLogicalSourceImage(path + ".source");
        var (logicalBody, rangeMessage) = BuildSimpleLogicalPullBody(
            tableName: "remote_items",
            rowId: 2,
            columnValue: "remote",
            schemaSql: "CREATE TABLE remote_items(id INTEGER PRIMARY KEY, x TEXT)",
            salt: 7110UL);
        var handler = ConflictHandler.ConflictThenSuccess(
            [
                CreatePagePullResponse("revision-42", image, protocol: 2),
                CreateLogicalPullResponse("revision-42", []),
                CreateLogicalPullResponse("revision-43", logicalBody, [rangeMessage]),
            ],
            conflictStepCount: 7,
            conflictErrorStep: 3,
            successStepCount: 6);
        var options = CreateOptions(path, handler);
        try
        {
            using (var connection = AhtolaConnection.CreateReplica(options))
            {
                connection.Open();
                connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
                connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (20);");
                connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (30);");
                Assert.ThrowsAsync<AhtolaReplicaConflictException>(
                    () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
                var report = await connection.InspectReplicaConflictAsync();
                report!.UnresolvedEntries.Select(entry => entry.Sequence).Should().Equal(2L);
                var discard = await connection.ResolveReplicaConflictAsync(
                    AhtolaReplicaConflictResolution.DiscardUnresolvedChanges,
                    new AhtolaReplicaConflictResolutionOptions { AcknowledgeDataLoss = true });
                discard.DiscardedChangeCount.Should().Be(1);
                discard.ConflictCleared.Should().BeTrue();
                // The hole is durable and explained.
                var afterDiscard = ManagedReplicaChangeJournal.Open(path);
                afterDiscard.ReadBatch(int.MaxValue).Changes.Select(change => change.Sequence)
                    .Should().Equal(1L, 3L);
                afterDiscard.DiscardedSequences.Should().Equal(2L);
                // The next ordinary sync pushes across the hole, acknowledges it, and applies the
                // remote transaction, which is where the acknowledged history is rebased onto the
                // remote base and the recorded journal base advances past the hole.
                var result = await connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
                result.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);
                handler.PushCallCount.Should().Be(2);
                handler.LastPushedStatements.Should().Contain(sql => sql.Contains("VALUES (10)", StringComparison.Ordinal));
                handler.LastPushedStatements.Should().Contain(sql => sql.Contains("VALUES (30)", StringComparison.Ordinal));
                handler.LastPushedStatements.Should().NotContain(sql => sql.Contains("VALUES (20)", StringComparison.Ordinal));
                ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.JournalBaseWatermark.Should().Be(4);
                // The discard removed sequence 2 from the replication stream, not the local row:
                // discarding without first rebasing deliberately leaves local rows in place.
                ReadJournalEventValues(connection).Should().Equal(10, 20, 30);
                ReadScalar(connection, "SELECT x FROM remote_items WHERE id = 2;").Should().Be("remote");
            }
            // "Crash" and reopen: the durable journal is still internally complete.
            var reopenedJournal = ManagedReplicaChangeJournal.Open(path);
            reopenedJournal.ReadBatch(int.MaxValue).Changes.Should().BeEmpty();
            reopenedJournal.AcknowledgedWatermark.Should().Be(4);
            using var reopened = AhtolaConnection.CreateReplica(options);
            reopened.Open();
            ReadJournalEventValues(reopened).Should().Equal(10, 20, 30);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task RebaseAgainstAnUnchangedRemoteStillRollsOffQuarantinedWritesAndReportsTheReplayCount()
    {
        // The remote has nothing new to send and the revision does not move, but the quarantined
        // local write is still materialized locally. The rebase must rebuild from the remote base
        // anyway, and RebasedChangeCount must describe the replay that actually happened.
        var path = NewReplicaPath("conflict-rebase-same-revision");
        var image = CreateLogicalSourceImage(path + ".source");
        var handler = ConflictHandler.Conflict(
            [
                CreatePagePullResponse("revision-42", image, protocol: 2),
                CreateLogicalPullResponse("revision-42", []),
                CreateLogicalPullResponse("revision-42", []),
            ],
            stepCount: 6,
            errorStep: 3,
            code: "SQLITE_CONSTRAINT");
        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (20);");
            Assert.ThrowsAsync<AhtolaReplicaConflictException>(
                () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            ReadJournalEventValues(connection).Should().Equal(10, 20);
            var result = await connection.ResolveReplicaConflictAsync(
                AhtolaReplicaConflictResolution.PullAndRebaseEligible);
            result.SyncResult!.Outcome.Should().Be(
                AhtolaSyncOutcome.RemoteChangesApplied,
                "an empty same-revision pull must still execute the quarantine-aware protected replay");
            result.RebasedChangeCount.Should().Be(1);
            result.ConflictCleared.Should().BeFalse();
            // The quarantined write lost to the freshly rebuilt base; the eligible one survived.
            ReadJournalEventValues(connection).Should().Equal(10);
            ManagedReplicaChangeJournal.Open(path).ReadBatch(int.MaxValue).Changes
                .Select(change => change.Sequence).Should().Equal(1L, 2L);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task StalePullRetryRefreshesAcknowledgedHistorySoAcknowledgedWritesSurviveTheRebuild()
    {
        // Between the request and the apply, a concurrent participant acknowledges the first
        // journal entry. The staleness check must reload BOTH the pending and the acknowledged
        // history: refreshing only the pending set would leave the acknowledged entry in neither
        // replay list, and the protected rebuild would silently drop its row. Since the
        // acknowledge only advances local journal state -- metadata's revision, database hash,
        // revert/push state, and journal watermark are all untouched -- this is exactly the
        // benign case the apply lease rebases onto in place (see
        // ManagedReplicaBootstrapper.RebaseOntoCurrentLocalStateOrThrowAsync) rather than
        // discarding the response and re-fetching from the remote.
        var path = NewReplicaPath("conflict-stale-retry-acknowledged");
        var image = CreateLogicalSourceImage(path + ".source");
        var (logicalBody, rangeMessage) = BuildSimpleLogicalPullBody(
            tableName: "remote_items",
            rowId: 2,
            columnValue: "remote",
            schemaSql: "CREATE TABLE remote_items(id INTEGER PRIMARY KEY, x TEXT)",
            salt: 7111UL);
        var handler = ConflictHandler.NoPush(
        [
            CreatePagePullResponse("revision-42", image, protocol: 2),
            CreateLogicalPullResponse("revision-42", []),
            CreateLogicalPullResponse("revision-43", logicalBody, [rangeMessage]),
        ]);
        var options = CreateOptions(path, handler);
        try
        {
            IReadOnlyList<ReplicaLocalChange> pendingChanges;
            ManagedReplicaBootstrapper.ManagedReplicaMetadata metadata;
            using (var connection = AhtolaConnection.CreateReplica(options))
            {
                connection.Open();
                connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
                connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (20);");
                pendingChanges = ManagedReplicaChangeJournal.Open(path).ReadBatch(int.MaxValue).Changes;
                metadata = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            }
            pendingChanges.Select(change => change.Sequence).Should().Equal(1L, 2L);
            var interleaved = 0;
            using (ManagedReplicaFaultInjection.Push(boundary =>
            {
                if (boundary != ManagedReplicaDurableBoundary.ReplicaApplyLockAcquired
                    || Interlocked.Increment(ref interleaved) != 1)
                {
                    return;
                }
                // A concurrent participant acknowledges sequence 1 while this pull's response is
                // already in flight: the pending set shrinks and the acknowledged set grows.
                ManagedReplicaChangeJournal.Open(path).Acknowledge(2);
            }))
            {
                var result = await ManagedReplicaBootstrapper.CheckForUpdatesAsync(
                    options,
                    metadata,
                    new AhtolaSyncOptions(),
                    pendingChanges,
                    [],
                    CancellationToken.None);
                result.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);
            }
            interleaved.Should().Be(
                1,
                "the acknowledge only advances local journal state, so the apply lease rebases onto the "
                + "refreshed journal in place instead of discarding the response and re-pulling from the "
                + "remote");
            handler.PullCallCount.Should().Be(3);
            using var reopened = AhtolaConnection.CreateReplica(options);
            reopened.Open();
            ReadJournalEventValues(reopened).Should().Equal(
                [10, 20],
                "the acknowledged write must be replayed from the refreshed acknowledged history");
            ReadScalar(reopened, "SELECT x FROM remote_items WHERE id = 2;").Should().Be("remote");
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.JournalBaseWatermark.Should().Be(2);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task SiblingConnectionWriteBeforeTheApplyIsRebasedWithoutAnExtraPullOrHostChurn()
    {
        // The same benign race as the previous test, but end-to-end through a second, genuinely
        // independent AhtolaConnection instead of a raw journal acknowledge: a sibling connection
        // commits an ordinary local write after the pull request/response this call replays were
        // captured, but before CheckForUpdatesAsync's apply lease re-validates the local baseline
        // against them -- exactly the ordering a real concurrent sibling write racing the network
        // long-poll produces, sequenced deterministically here instead of via a timing-dependent
        // concurrent write. Only the pending local-change journal advances -- the remote-facing
        // identity the staged response depends on (revision, database hash, revert/push state,
        // journal watermark) does not -- so the apply lease must rebase onto the fresh journal in
        // place instead of discarding the response and re-pulling from the remote, bounding both
        // network traffic and host close/reopen churn to exactly what this one pull needs.
        var path = NewReplicaPath("conflict-stale-retry-sibling-write");
        var image = CreateLogicalSourceImage(path + ".source");
        var (logicalBody, rangeMessage) = BuildSimpleLogicalPullBody(
            tableName: "remote_items",
            rowId: 2,
            columnValue: "remote",
            schemaSql: "CREATE TABLE remote_items(id INTEGER PRIMARY KEY, x TEXT)",
            salt: 7222UL);
        var handler = ConflictHandler.NoPush(
        [
            CreatePagePullResponse("revision-42", image, protocol: 2),
            CreateLogicalPullResponse("revision-42", []),
            CreateLogicalPullResponse("revision-43", logicalBody, [rangeMessage]),
        ]);
        var options = CreateOptions(path, handler);
        try
        {
            IReadOnlyList<ReplicaLocalChange> pendingChanges;
            ManagedReplicaBootstrapper.ManagedReplicaMetadata metadata;
            using (var connection = AhtolaConnection.CreateReplica(options))
            {
                connection.Open();
                connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
                pendingChanges = ManagedReplicaChangeJournal.Open(path).ReadBatch(int.MaxValue).Changes;
                metadata = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            }
            pendingChanges.Select(change => change.Sequence).Should().Equal(1L);

            // A second, independent connection commits an ordinary write after pendingChanges/
            // metadata above were captured as this pull's request base, but before
            // CheckForUpdatesAsync (below) ever re-validates that base against fresh disk state.
            using (var sibling = AhtolaConnection.CreateReplica(options))
            {
                sibling.Open();
                sibling.ExecuteNonQuery("INSERT INTO journal_events VALUES (20);");
            }

            var applyLockAcquisitions = 0;
            using (ManagedReplicaFaultInjection.Push(boundary =>
            {
                if (boundary == ManagedReplicaDurableBoundary.ReplicaApplyLockAcquired)
                    Interlocked.Increment(ref applyLockAcquisitions);
            }))
            {
                var result = await ManagedReplicaBootstrapper.CheckForUpdatesAsync(
                    options, metadata, new AhtolaSyncOptions(), pendingChanges, [], CancellationToken.None);
                result.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);
            }

            applyLockAcquisitions.Should().Be(
                1,
                "the sibling connection's write only advances the local journal, so the apply lease "
                + "rebases onto it in place instead of discarding the response and re-pulling from "
                + "the remote");
            handler.PullCallCount.Should().Be(
                3,
                "bootstrap plus catch-up plus exactly one explicit pull -- no extra re-fetch for the "
                + "sibling connection's write");

            using var reopened = AhtolaConnection.CreateReplica(options);
            reopened.Open();
            ReadJournalEventValues(reopened).Should().Equal(
                [10, 20],
                "the sibling connection's write must survive the rebase, not be lost or overwritten");
            ReadScalar(reopened, "SELECT x FROM remote_items WHERE id = 2;").Should().Be("remote");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void ResolveKeepsTheMarkerWhenTheJournalHasNoEvidenceForItsUnresolvedSequences()
    {
        var path = NewReplicaPath("conflict-missing-evidence");
        var image = CreateJournalDatabaseImage(path + ".source");
        var handler = ConflictHandler.RowConflict([CreatePagePullResponse("revision-42", image)]);
        var options = CreateOptions(path, handler);
        try
        {
            using (var connection = AhtolaConnection.CreateReplica(options))
            {
                connection.Open();
                connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
                Assert.ThrowsAsync<AhtolaReplicaConflictException>(
                    () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            }
            // The marker's only unresolved sequence leaves retention with no discard recorded --
            // evidence loss, not a completed discard.
            ManagedReplicaChangeJournal.Open(path).Acknowledge(2);
            var markerBefore = File.ReadAllBytes(ManagedReplicaConflictState.GetPath(path));
            using var reopened = AhtolaConnection.CreateReplica(options);
            reopened.Open();
            Assert.ThrowsAsync<InvalidDataException>(
                () => reopened.ResolveReplicaConflictAsync(
                    AhtolaReplicaConflictResolution.DiscardUnresolvedChanges,
                    new AhtolaReplicaConflictResolutionOptions { AcknowledgeDataLoss = true }));
            // The marker is deliberately untouched, so synchronization stays blocked.
            File.ReadAllBytes(ManagedReplicaConflictState.GetPath(path)).Should().Equal(markerBefore);
            Assert.ThrowsAsync<AhtolaReplicaConflictPendingException>(
                () => reopened.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task ResolveCompletesAnInterruptedDiscardOnlyFromTheRecordedDiscardEvidence()
    {
        var path = NewReplicaPath("conflict-discard-evidence");
        var image = CreateJournalDatabaseImage(path + ".source");
        var handler = ConflictHandler.RowConflict([CreatePagePullResponse("revision-42", image)]);
        var options = CreateOptions(path, handler);
        try
        {
            using (var connection = AhtolaConnection.CreateReplica(options))
            {
                connection.Open();
                connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
                Assert.ThrowsAsync<AhtolaReplicaConflictException>(
                    () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
                using var fault = ManagedReplicaFaultInjection.Push(boundary =>
                {
                    if (boundary == ManagedReplicaDurableBoundary.JournalDiscardPersisted)
                        throw new IOException("simulated crash after the journal discard was durable");
                });
                Assert.ThrowsAsync<IOException>(
                    () => connection.ResolveReplicaConflictAsync(
                        AhtolaReplicaConflictResolution.DiscardUnresolvedChanges,
                        new AhtolaReplicaConflictResolutionOptions { AcknowledgeDataLoss = true }));
            }
            // The durable discard record -- not the mere absence of the entry -- is what proves the
            // discard landed.
            var journal = ManagedReplicaChangeJournal.Open(path);
            journal.ReadBatch(int.MaxValue).Changes.Should().BeEmpty();
            journal.DiscardedSequences.Should().Equal(1L);
            using var reopened = AhtolaConnection.CreateReplica(options);
            reopened.Open();
            var completion = await reopened.ResolveReplicaConflictAsync(
                AhtolaReplicaConflictResolution.PullAndRebaseEligible);
            completion.ConflictCleared.Should().BeTrue();
            completion.DiscardedChangeCount.Should().Be(0);
            completion.SyncResult.Should().BeNull("completing a landed discard never contacts the remote");
            File.Exists(ManagedReplicaConflictState.GetPath(path)).Should().BeFalse();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void PushPublicationHoldsThePhysicalApplyLeaseWhilePublishing()
    {
        var path = NewReplicaPath("conflict-push-lease");
        var image = CreateJournalDatabaseImage(path + ".source");
        var handler = ConflictHandler.RowConflict([CreatePagePullResponse("revision-42", image)]);
        var contended = false;
        Task? competitor = null;
        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
            using (ManagedReplicaFaultInjection.Push(boundary =>
            {
                if (boundary != ManagedReplicaDurableBoundary.ReplicaPushPublicationLockAcquired
                    || contended)
                {
                    return;
                }
                contended = true;
                competitor = Task.Run(async () =>
                {
                    await using var lease = await ManagedReplicaApplyLock
                        .AcquireExclusiveAsync(path, CancellationToken.None)
                        .ConfigureAwait(false);
                });
                competitor.Wait(TimeSpan.FromMilliseconds(250)).Should().BeFalse(
                    "push publication must hold the exclusive physical apply lease");
            }))
            {
                Assert.ThrowsAsync<AhtolaReplicaConflictException>(
                    () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            }
            contended.Should().BeTrue("the push publication boundary must have been reached");
            // Once publication released the lease the competitor completes, proving it was only
            // ever blocked by the lease rather than failing outright.
            competitor!.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
        }
        finally
        {
            competitor?.Wait(TimeSpan.FromSeconds(30));
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void PushFailsClosedWhenAnotherWriterAdvancesTheJournalBetweenSelectionAndIntent()
    {
        var path = NewReplicaPath("conflict-push-generation");
        var image = CreateJournalDatabaseImage(path + ".source");
        var handler = ConflictHandler.NoPush([CreatePagePullResponse("revision-42", image)]);
        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
            var hits = 0;
            using (ManagedReplicaFaultInjection.Push(boundary =>
            {
                if (boundary != ManagedReplicaDurableBoundary.ReplicaPushPublicationLockAcquired
                    || Interlocked.Increment(ref hits) != 2)
                {
                    return;
                }
                // Journal append uses its own leaf lease, so another participant can move it while
                // this call holds the apply lease. The generation selected under the first apply
                // acquisition must reject that movement under this second one, before intent or I/O.
                ManagedReplicaChangeJournal.Open(path).AppendCommitted(
                    [Row(0, "journal_events", 99, "INSERT INTO journal_events VALUES (99)")]);
            }))
            {
                var failure = Assert.ThrowsAsync<AhtolaException>(
                    () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
                failure!.ReplicaPushFailureKind.Should().Be(AhtolaReplicaPushFailureKind.InvalidLocalState);
            }
            handler.PushCallCount.Should().Be(0, "the push must fail closed before contacting the remote");
            File.Exists(ManagedReplicaConflictState.GetPath(path)).Should().BeFalse();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void PublicationCancelledAtOwnershipLeavesTheReplicaAndTheConnectionUntouched()
    {
        var path = NewReplicaPath("conflict-cancel-ownership");
        var image = CreateJournalDatabaseImage(path + ".source");
        var handler = ConflictHandler.RowConflict([CreatePagePullResponse("revision-42", image)]);
        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
            Assert.ThrowsAsync<AhtolaReplicaConflictException>(
                () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            var markerBefore = File.ReadAllBytes(ManagedReplicaConflictState.GetPath(path));
            using var cancellation = new CancellationTokenSource();
            using (ManagedReplicaFaultInjection.Push(boundary =>
            {
                if (boundary == ManagedReplicaDurableBoundary.ReplicaPublicationOwnershipAcquired)
                    cancellation.Cancel();
            }))
            {
                Assert.CatchAsync<OperationCanceledException>(
                    () => connection.ResolveReplicaConflictAsync(
                        AhtolaReplicaConflictResolution.DiscardUnresolvedChanges,
                        new AhtolaReplicaConflictResolutionOptions { AcknowledgeDataLoss = true },
                        cancellation.Token));
            }
            // Nothing durable moved and the connection was never closed for publication.
            File.ReadAllBytes(ManagedReplicaConflictState.GetPath(path)).Should().Equal(markerBefore);
            ManagedReplicaChangeJournal.Open(path).ReadBatch(int.MaxValue).Changes
                .Select(change => change.Sequence).Should().Equal(1L);
            connection.State.Should().Be(System.Data.ConnectionState.Open);
            ReadJournalEventValues(connection).Should().Equal(10);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task DiscardCompletesDeterministicallyWhenCancelledAfterTheJournalReplaceIsDurable()
    {
        var path = NewReplicaPath("conflict-cancel-after-discard");
        var image = CreateJournalDatabaseImage(path + ".source");
        var handler = ConflictHandler.RowConflict([CreatePagePullResponse("revision-42", image)]);
        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
            Assert.ThrowsAsync<AhtolaReplicaConflictException>(
                () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            using var cancellation = new CancellationTokenSource();
            AhtolaReplicaConflictResolutionResult result;
            using (ManagedReplicaFaultInjection.Push(boundary =>
            {
                if (boundary == ManagedReplicaDurableBoundary.JournalDiscardPersisted)
                    cancellation.Cancel();
            }))
            {
                result = await connection.ResolveReplicaConflictAsync(
                    AhtolaReplicaConflictResolution.DiscardUnresolvedChanges,
                    new AhtolaReplicaConflictResolutionOptions { AcknowledgeDataLoss = true },
                    cancellation.Token);
            }
            // Past the durable discard the resolution always completes: abandoning it would leave a
            // marker naming sequences that no longer exist.
            result.ConflictCleared.Should().BeTrue();
            result.DiscardedChangeCount.Should().Be(1);
            File.Exists(ManagedReplicaConflictState.GetPath(path)).Should().BeFalse();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void JournalAndConflictMarkerUseDeterministicStagingArtifactsAndCleanThemUp()
    {
        var path = NewReplicaPath("conflict-staging-artifacts");
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        try
        {
            ManagedReplicaBootstrapper.GetLocalArtifactPaths(path)
                .Should().Contain(path + ManagedReplicaChangeJournal.StagingSuffix)
                .And.Contain(path + ManagedReplicaConflictState.StagingSuffix);
            // Stale staging artifacts (an interrupted persist) are removed rather than accumulating.
            File.WriteAllBytes(ManagedReplicaChangeJournal.GetStagingPath(path), [1, 2, 3]);
            File.WriteAllBytes(ManagedReplicaConflictState.GetStagingPath(path), [1, 2, 3]);
            var journal = ManagedReplicaChangeJournal.Open(path);
            File.Exists(ManagedReplicaChangeJournal.GetStagingPath(path)).Should().BeFalse();
            _ = ManagedReplicaConflictState.TryRead(path);
            File.Exists(ManagedReplicaConflictState.GetStagingPath(path)).Should().BeFalse();
            journal.AppendCommitted([Row(0, "items", 1, "INSERT INTO items(id) VALUES (1)")]);
            ManagedReplicaConflictState.Write(
                path,
                new ManagedReplicaConflictState(
                    AhtolaReplicaConflictKind.RowWrite,
                    "SQLITE_CONSTRAINT",
                    1,
                    1,
                    2,
                    [1]));
            // No random, data-bearing leftovers anywhere beside the replica.
            Directory.GetFiles(directory, Path.GetFileName(path) + "*.tmp").Should().BeEmpty();
            Directory.GetFiles(directory, "." + Path.GetFileName(path) + "*.tmp").Should().BeEmpty();
        }
        finally
        {
            DeleteReplicaFiles(path);
            File.Exists(ManagedReplicaChangeJournal.GetStagingPath(path)).Should().BeFalse();
            File.Exists(ManagedReplicaConflictState.GetStagingPath(path)).Should().BeFalse();
        }
    }

    [Test]
    public void PublicationReopenFailureClosesTheConnectionInsteadOfLeavingItOpen()
    {
        var path = NewReplicaPath("conflict-reopen-failure");
        var image = CreateJournalDatabaseImage(path + ".source");
        var handler = ConflictHandler.RowConflict([CreatePagePullResponse("revision-42", image)]);
        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
            Assert.ThrowsAsync<AhtolaReplicaConflictException>(
                () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            using (ManagedReplicaFaultInjection.Push(boundary =>
            {
                if (boundary != ManagedReplicaDurableBoundary.ConflictMarkerRetired)
                    return;
                // Make the post-publication reopen impossible: metadata is gone while checkpoint
                // recovery artifacts claim otherwise, which ReopenAfterPublication rejects.
                File.Delete(path + ManagedReplicaBootstrapper.MetadataSuffix);
                File.WriteAllBytes(path + "-wal-revert", [1, 2, 3]);
            }))
            {
                Assert.ThrowsAsync<InvalidDataException>(
                    () => connection.ResolveReplicaConflictAsync(
                        AhtolaReplicaConflictResolution.DiscardUnresolvedChanges,
                        new AhtolaReplicaConflictResolutionOptions { AcknowledgeDataLoss = true }));
            }
            connection.State.Should().Be(
                System.Data.ConnectionState.Closed,
                "a connection whose database was disposed and could not be reopened must not report Open");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task TheRemoteBasePublishedByAProtectedApplyExcludesEveryPendingLocalChange()
    {
        // The published remote base is the image the SERVER would hold for this client: the previous
        // base advanced by what the server has acknowledged and by this pull's remote transactions,
        // and nothing else. It is also the image every later protected rebase copies before it
        // replays the journal, so a base that already carried the pending replay would apply the
        // same local statements again on the next rebase.
        var path = NewReplicaPath("protected-base-excludes-pending");
        var image = CreateUniqueJournalDatabaseImage(path + ".source");
        var handler = ConflictHandler.NoPush(
        [
            CreatePagePullResponse("revision-42", image, protocol: 2),
            CreateLogicalPullResponse("revision-42", []),
            CreateLogicalPullResponse("revision-43", []),
        ]);
        var options = CreateOptions(path, handler);
        try
        {
            var (pending, metadata) = SeedPendingLocalWrite(options, path, 10);
            var result = await ManagedReplicaBootstrapper.CheckForUpdatesAsync(
                options, metadata, new AhtolaSyncOptions(), pending, [], CancellationToken.None);
            result.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);
            // The live replica keeps the still-unpushed write materialized...
            ReadJournalEventValuesFrom(path).Should().Equal(10);
            // ...while the remote-base snapshot the metadata now fingerprints does not.
            var published = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            var basePath = path + ManagedReplicaBootstrapper.BaseSnapshotSuffix;
            File.Exists(basePath).Should().BeTrue();
            ComputeSha256(basePath).Should().Be(
                published.RemoteBaseSha256,
                "the recorded hash and the published snapshot must be produced from the same file");
            ReadJournalEventValuesFrom(basePath).Should().BeEmpty(
                "a remote base that already contained the pending replay would replay it again on the next rebase");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task TwoConsecutiveProtectedRebasesReplayEachPendingChangeExactlyOnce()
    {
        // The regression: the first protected apply published a remote base that already contained
        // the replayed pending statement, so the second one copied that base and replayed the very
        // same INSERT onto it. On any table with a uniqueness guarantee that turned an ordinary
        // re-sync into a constraint violation; without one it silently duplicated rows.
        var path = NewReplicaPath("protected-rebase-consecutive");
        var image = CreateUniqueJournalDatabaseImage(path + ".source");
        var handler = ConflictHandler.NoPush(
        [
            CreatePagePullResponse("revision-42", image, protocol: 2),
            CreateLogicalPullResponse("revision-42", []),
            CreateLogicalPullResponse("revision-43", []),
            CreateLogicalPullResponse("revision-44", []),
        ]);
        var options = CreateOptions(path, handler);
        try
        {
            var (pending, metadata) = SeedPendingLocalWrite(options, path, 10);
            var first = await ManagedReplicaBootstrapper.CheckForUpdatesAsync(
                options, metadata, new AhtolaSyncOptions(), pending, [], CancellationToken.None);
            first.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);
            first.Statistics.Revision.Should().Be("revision-43");
            // Deliberately the SAME pending set: nothing was pushed, so the journal still owes both
            // rebases the identical replay. Settling the recovery bundle the first protected apply
            // left behind is exactly what ManagedReplicaConnectionHost does before every pull.
            metadata = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            metadata = ManagedReplicaRevertWal.PrepareSynchronization(path, metadata);
            metadata = ManagedReplicaRevertWal.CompletePreparedCheckpoint(path, metadata);
            var second = await ManagedReplicaBootstrapper.CheckForUpdatesAsync(
                options, metadata, new AhtolaSyncOptions(), pending, [], CancellationToken.None);
            second.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);
            second.Statistics.Revision.Should().Be("revision-44");
            ReadJournalEventValuesFrom(path).Should().Equal(
                [10],
                "the pending write must survive both rebases exactly once, never be replayed twice");
            ReadJournalEventValuesFrom(path + ManagedReplicaBootstrapper.BaseSnapshotSuffix).Should().BeEmpty();
            // Nothing was acknowledged, so the journal is untouched by either rebase.
            var journal = ManagedReplicaChangeJournal.Open(path);
            journal.ReadBatch(int.MaxValue).Changes.Select(change => change.Sequence).Should().Equal(1L);
            journal.AcknowledgedWatermark.Should().Be(1);
            handler.PullCallCount.Should().Be(4);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task NoLocalWriterCanLandBetweenApplyRevalidationAndSnapshotPublication()
    {
        // One exclusive physical apply lease must span the whole sequence: the post-network
        // re-validation of the local base, the transactional replay, the WAL checkpoint, the
        // snapshot's sidecar deletion and main-file replace, and the metadata publication. A
        // second process (modelled here by a competitor that takes the very same lease and then
        // journals a local write) must not be able to interleave anywhere inside it.
        var watched = new[]
        {
            ManagedReplicaDurableBoundary.LogicalApplyCommitted,
            ManagedReplicaDurableBoundary.LogicalApplyCheckpointed,
            ManagedReplicaDurableBoundary.RevertWalPublished,
            ManagedReplicaDurableBoundary.RevertCommittedRestoreStagedDatabase,
            ManagedReplicaDurableBoundary.RevertCommittedRestoreDatabasePublished,
            ManagedReplicaDurableBoundary.RevertCommittedReadyMetadataPublished,
        };
        var path = NewReplicaPath("protected-apply-lease-window");
        var image = CreateUniqueJournalDatabaseImage(path + ".source");
        var handler = ConflictHandler.NoPush(
        [
            CreatePagePullResponse("revision-42", image, protocol: 2),
            CreateLogicalPullResponse("revision-42", []),
            CreateLogicalPullResponse("revision-43", []),
        ]);
        var options = CreateOptions(path, handler);
        var gate = new object();
        var observed = new List<ManagedReplicaDurableBoundary>();
        var interleaved = new List<ManagedReplicaDurableBoundary>();
        Task? competitor = null;
        try
        {
            var (pending, metadata) = SeedPendingLocalWrite(options, path, 10);
            var competitorStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ManagedReplicaFaultInjection.Push(boundary =>
            {
                if (boundary == ManagedReplicaDurableBoundary.ReplicaApplyLockAcquired && competitor is null)
                {
                    competitor = Task.Run(async () =>
                    {
                        competitorStarted.TrySetResult();
                        await using var lease = await ManagedReplicaApplyLock
                            .AcquireExclusiveAsync(path, CancellationToken.None)
                            .ConfigureAwait(false);
                        ManagedReplicaChangeJournal.Open(path).AppendCommitted(
                            [Row(0, "journal_events", 99, "INSERT INTO journal_events VALUES (99)")]);
                    });
                    competitorStarted.Task.Wait(TimeSpan.FromSeconds(30));
                    competitor.Wait(TimeSpan.FromMilliseconds(250)).Should().BeFalse(
                        "the apply lease is held from re-validation onward, so no other writer may proceed");
                    return;
                }
                if (!watched.Contains(boundary))
                    return;
                lock (gate)
                {
                    observed.Add(boundary);
                    if (competitor is { IsCompleted: true })
                        interleaved.Add(boundary);
                }
            }))
            {
                var result = await ManagedReplicaBootstrapper.CheckForUpdatesAsync(
                    options, metadata, new AhtolaSyncOptions(), pending, [], CancellationToken.None);
                result.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);
            }
            competitor.Should().NotBeNull("the apply must have re-validated under the lease");
            competitor!.Wait(TimeSpan.FromSeconds(60)).Should().BeTrue(
                "the competitor must have been queued behind the lease, never rejected");
            lock (gate)
            {
                observed.Should().Contain(watched, "every publication boundary must run under one lease");
                interleaved.Should().BeEmpty(
                    "a competing local writer must not acquire the lease at any point between "
                    + "re-validation and the snapshot replacement, WAL cleanup, and metadata publication");
            }
            // The competing write landed strictly after publication: the replica shows the applied
            // revision, and the journal now carries the competitor's entry after the original one.
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.Revision.Should().Be("revision-43");
            ManagedReplicaChangeJournal.Open(path).ReadBatch(int.MaxValue).Changes
                .Select(change => change.Sequence).Should().Equal(1L, 2L);
        }
        finally
        {
            competitor?.Wait(TimeSpan.FromSeconds(60));
            DeleteReplicaFiles(path);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Bootstraps the replica, commits one tracked local write, and returns the pending journal
    /// entries and metadata exactly as they are on disk afterwards -- the inputs a protected apply
    /// is negotiated against.
    /// </summary>
    private static (IReadOnlyList<ReplicaLocalChange> Pending, ManagedReplicaBootstrapper.ManagedReplicaMetadata Metadata)
        SeedPendingLocalWrite(AhtolaReplicaOptions options, string path, int value)
    {
        using (var connection = AhtolaConnection.CreateReplica(options))
        {
            connection.Open();
            connection.ExecuteNonQuery($"INSERT INTO journal_events VALUES ({value});");
        }
        var pending = ManagedReplicaChangeJournal.Open(path).ReadBatch(int.MaxValue).Changes;
        pending.Select(change => change.Sequence).Should().Equal(1L);
        return (pending, ManagedReplicaBootstrapper.LoadMetadata(path)!.Value);
    }

    /// <summary>
    /// The ordinary bootstrap image, but with a uniqueness guarantee on the journaled table so a
    /// replayed-twice pending statement fails loudly instead of silently duplicating a row.
    /// </summary>
    private static byte[] CreateUniqueJournalDatabaseImage(string path)
    {
        try
        {
            using (var connection = new AhtolaConnection($"Data Source={path};Local Provider=Managed"))
            {
                connection.Open();
                connection.ExecuteNonQuery("CREATE TABLE bootstrap_marker(value INTEGER NOT NULL);");
                connection.ExecuteNonQuery("INSERT INTO bootstrap_marker VALUES (42);");
                connection.ExecuteNonQuery("CREATE TABLE journal_events(value INTEGER NOT NULL UNIQUE);");
            }
            return File.ReadAllBytes(path);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    /// <summary>
    /// Reads <c>journal_events</c> straight out of a database file on disk (the live replica, or a
    /// remote-base snapshot) through a copy, so the original file and its sidecars are untouched.
    /// </summary>
    private static IReadOnlyList<int> ReadJournalEventValuesFrom(string databasePath)
    {
        var inspectionPath = databasePath + $".inspect-{Guid.NewGuid():N}.db";
        try
        {
            File.Copy(databasePath, inspectionPath, overwrite: false);
            using var connection = new AhtolaConnection($"Data Source={inspectionPath};Local Provider=Managed");
            connection.Open();
            return ReadJournalEventValues(connection);
        }
        finally
        {
            foreach (var artifact in ManagedReplicaBootstrapper.GetLocalArtifactPaths(inspectionPath))
            {
                if (File.Exists(artifact))
                    File.Delete(artifact);
            }
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    /// <summary>
    /// Writes a change-journal file by hand so validation can be exercised against shapes the
    /// production writer never produces (an unexplained gap, a doubly recorded sequence) and
    /// against the pre-format-7 layout.
    /// </summary>
    private static byte[] BuildJournalFile(
        int version,
        long sequence,
        long watermark,
        IReadOnlyList<long> retained,
        IReadOnlyList<long> discarded)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x4C_4E_52_4A_4C_4F_54_41UL);
            writer.Write(version);
            writer.Write(sequence);
            writer.Write(watermark);
            writer.Write(retained.Count);
            foreach (var entry in retained)
            {
                writer.Write(entry);
                writer.Write((byte)1); // ReplicaLocalChangeKind.Row
                writer.Write((int)SqliteChangeOperation.Insert);
                WriteJournalString(writer, "main");
                WriteJournalString(writer, "items");
                writer.Write(entry);
                WriteJournalString(writer, $"INSERT INTO items(id) VALUES ({entry})");
                writer.Write(-1); // no captured before-image
            }
            if (version >= 7)
            {
                writer.Write(discarded.Count);
                foreach (var entry in discarded)
                    writer.Write(entry);
            }
        }
        return buffer.ToArray();
    }

    private static void WriteJournalString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    public enum ConflictMarkerCorruption
    {
        BadMagic,
        BadVersion,
        Truncated,
        TrailingBytes,
        UnknownConflictKind,
        EmptyUnresolvedSet,
        UnresolvedSequenceOutsideBatch,
        UnorderedUnresolvedSet,
        ConflictingSequenceOutsideBatch,
        InvertedBatchRange,
    }

    private static byte[] BuildCorruptConflictMarker(ConflictMarkerCorruption corruption)
    {
        var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(corruption == ConflictMarkerCorruption.BadMagic
                ? 0xDEAD_BEEF_DEAD_BEEFUL
                : 0x54_4C_46_4E_4F_43_4F_54UL);
            writer.Write(corruption == ConflictMarkerCorruption.BadVersion ? 99 : 1);
            writer.Write((byte)(corruption == ConflictMarkerCorruption.UnknownConflictKind ? 42 : 1));
            writer.Write(-1); // remote error code: null
            writer.Write(corruption == ConflictMarkerCorruption.ConflictingSequenceOutsideBatch ? 99L : 1L);
            writer.Write(corruption == ConflictMarkerCorruption.InvertedBatchRange ? 9L : 1L);
            writer.Write(3L);
            switch (corruption)
            {
                case ConflictMarkerCorruption.EmptyUnresolvedSet:
                    writer.Write(0);
                    break;
                case ConflictMarkerCorruption.UnresolvedSequenceOutsideBatch:
                    writer.Write(1);
                    writer.Write(77L);
                    break;
                case ConflictMarkerCorruption.UnorderedUnresolvedSet:
                    writer.Write(2);
                    writer.Write(2L);
                    writer.Write(1L);
                    break;
                default:
                    writer.Write(1);
                    writer.Write(1L);
                    break;
            }
            if (corruption == ConflictMarkerCorruption.TrailingBytes)
                writer.Write(0x5A5A5A5AU);
        }
        var bytes = buffer.ToArray();
        return corruption == ConflictMarkerCorruption.Truncated ? bytes[..^3] : bytes;
    }

    private static ReplicaLocalChange Row(long sequence, string table, long rowId, string sql)
        => ReplicaLocalChange.Row(SqliteChangeOperation.Insert, "main", table, rowId) with
        {
            Sequence = sequence,
            Sql = sql,
        };
    private static ReplicaLocalChange Schema(long sequence, string sql)
        => ReplicaLocalChange.Schema(sql) with { Sequence = sequence };
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

    private static byte[] CreateJournalDatabaseImage(string path)
    {
        try
        {
            using (var connection = new AhtolaConnection($"Data Source={path};Local Provider=Managed"))
            {
                connection.Open();
                connection.ExecuteNonQuery("CREATE TABLE bootstrap_marker(value INTEGER NOT NULL);");
                connection.ExecuteNonQuery("INSERT INTO bootstrap_marker VALUES (42);");
                connection.ExecuteNonQuery("CREATE TABLE journal_events(value INTEGER NOT NULL);");
            }
            return File.ReadAllBytes(path);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    private static byte[] CreateLogicalSourceImage(string path) => CreateJournalDatabaseImage(path);
    private static IReadOnlyList<int> ReadJournalEventValues(AhtolaConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM journal_events ORDER BY value;";
        using var reader = command.ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add(checked((int)reader.GetInt64(0)));
        return values;
    }

    private static object? ReadScalar(AhtolaConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static AhtolaReplicaOptions CreateOptions(
        string path,
        HttpMessageHandler handler,
        int syncInterval = 0)
        => new(path, new Uri("https://example.test/cluster"), authToken: "token-42")
        {
            LongPollTimeout = TimeSpan.FromSeconds(3),
            SyncInterval = syncInterval,
            HttpPolicy = new AhtolaSyncHttpPolicy(handler)
            {
                MessageHandlerDisablesAutomaticRedirects = true,
            },
        };
    private sealed class ProgressRecorder : IProgress<AhtolaSyncProgress>
    {
        public List<AhtolaSyncProgressStage> Stages { get; } = [];
        public void Report(AhtolaSyncProgress value) => Stages.Add(value.Stage);
    }

    /// <summary>
    /// Serves a fixed queue of pull-updates responses and answers pushes with a scripted sequence
    /// of Hrana batch results, recording the statements each push actually replayed.
    /// </summary>
    private sealed class ConflictHandler : HttpMessageHandler
    {
        private readonly Queue<byte[]> _pullResponses;
        private readonly Func<int, HttpResponseMessage> _pushResponse;
        private readonly object _gate = new();
        private ConflictHandler(IEnumerable<byte[]> pullResponses, Func<int, HttpResponseMessage> pushResponse)
        {
            _pullResponses = new Queue<byte[]>(pullResponses);
            _pushResponse = pushResponse;
        }
        public int PullCallCount { get; private set; }
        public int PushCallCount { get; private set; }
        public IReadOnlyList<string> LastPushedStatements { get; private set; } = [];
        public static ConflictHandler NoPush(IEnumerable<byte[]> pullResponses)
            => new(pullResponses, _ => throw new InvalidOperationException("No push was expected."));
        public static ConflictHandler RowConflict(IEnumerable<byte[]> pullResponses)
            => Conflict(pullResponses, stepCount: 5, errorStep: 2, code: "SQLITE_CONSTRAINT");
        public static ConflictHandler Conflict(
            IEnumerable<byte[]> pullResponses,
            int stepCount,
            int errorStep,
            string code)
            => new(pullResponses, _ => BatchResponse(stepCount, errorStep, "conflicting local change", code));
        public static ConflictHandler ConflictThenSuccess(
            IEnumerable<byte[]> pullResponses,
            int conflictStepCount,
            int conflictErrorStep,
            int successStepCount = 5)
            => new(
                pullResponses,
                push => push == 1
                    ? BatchResponse(conflictStepCount, conflictErrorStep, "conflicting local change", "SQLITE_CONSTRAINT")
                    : BatchResponse(successStepCount, null, null, null));
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/pull-updates", StringComparison.Ordinal))
            {
                byte[] payload;
                lock (_gate)
                {
                    PullCallCount++;
                    payload = _pullResponses.Dequeue();
                }
                var pullResponse = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload),
                };
                pullResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/protobuf");
                return pullResponse;
            }
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            int push;
            lock (_gate)
            {
                PushCallCount++;
                push = PushCallCount;
                LastPushedStatements = ExtractStatements(body);
            }
            return _pushResponse(push);
        }
        /// <summary>Pulls every <c>"sql":"..."</c> value out of a serialized Hrana batch request.</summary>
        private static IReadOnlyList<string> ExtractStatements(string body)
        {
            var statements = new List<string>();
            const string marker = "\"sql\":\"";
            var index = body.IndexOf(marker, StringComparison.Ordinal);
            while (index >= 0)
            {
                var start = index + marker.Length;
                var end = start;
                while (end < body.Length && body[end] != '"')
                    end += body[end] == '\\' ? 2 : 1;
                statements.Add(body[start..Math.Min(end, body.Length)]);
                index = body.IndexOf(marker, Math.Min(end, body.Length), StringComparison.Ordinal);
            }
            return statements;
        }
        private static HttpResponseMessage BatchResponse(int stepCount, int? errorStep, string? message, string? code)
        {
            var results = string.Join(",", Enumerable.Range(0, stepCount)
                .Select(index => index == errorStep ? "null" : "{}"));
            var errors = string.Join(",", Enumerable.Range(0, stepCount)
                .Select(index => index == errorStep
                    ? $"{{\"message\":\"{message}\",\"code\":\"{code}\"}}"
                    : "null"));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"results\":[{\"type\":\"ok\",\"response\":{\"type\":\"batch\",\"result\":"
                    + $"{{\"step_results\":[{results}],\"step_errors\":[{errors}]}}}}}}]}}",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    // ---- protobuf fixtures ----------------------------------------------------------------
    private static byte[] CreatePagePullResponse(string revision, byte[] databaseImage, ulong protocol = 1)
    {
        var header = new List<byte>();
        WriteLengthDelimitedField(header, 1, Encoding.UTF8.GetBytes(revision));
        WriteVarintField(header, 2, checked((ulong)((databaseImage.Length + 4095) / 4096)));
        WriteLengthDelimitedField(header, 3, []);
        WriteVarintField(header, 5, 0);
        WriteVarintField(header, 6, 1);
        WriteVarintField(header, 8, protocol);
        var response = new List<byte>();
        WriteDelimitedMessage(response, header);
        for (var offset = 0; offset < databaseImage.Length; offset += 4096)
        {
            var page = new List<byte>();
            if (offset != 0)
                WriteVarintField(page, 1, checked((ulong)(offset / 4096)));
            WriteLengthDelimitedField(
                page,
                2,
                databaseImage.AsSpan(offset, Math.Min(4096, databaseImage.Length - offset)));
            WriteDelimitedMessage(response, page);
        }
        return response.ToArray();
    }

    private static byte[] CreateLogicalPullResponse(
        string revision,
        byte[] body,
        IReadOnlyList<byte[]>? rangeMessages = null)
    {
        var header = new List<byte>();
        WriteLengthDelimitedField(header, 1, Encoding.UTF8.GetBytes(revision));
        WriteVarintField(header, 2, 1);
        WriteLengthDelimitedField(header, 3, []);
        WriteVarintField(header, 5, 1); // stream_kind = MvccLogicalLog
        WriteVarintField(header, 6, 0);
        if (rangeMessages is not null)
        {
            var metadata = new List<byte>();
            WriteLengthDelimitedField(metadata, 1, Encoding.UTF8.GetBytes("lml3"));
            foreach (var range in rangeMessages)
                WriteLengthDelimitedField(metadata, 3, range);
            WriteLengthDelimitedField(header, 7, metadata.ToArray());
        }
        WriteVarintField(header, 8, 2);
        var response = new List<byte>();
        WriteDelimitedMessage(response, header);
        response.AddRange(body);
        return response.ToArray();
    }

    private static (byte[] Body, byte[] RangeMessage) BuildSimpleLogicalPullBody(
        string tableName,
        long rowId,
        string columnValue,
        string schemaSql,
        ulong salt)
    {
        var logHeader = Lml3TestBuilder.BuildHeader(salt);
        var crc = Lml3TestBuilder.HeaderSeedCrc(salt);
        var schemaRecord = Lml3TestBuilder.SchemaRecord("table", tableName, 5, schemaSql);
        var schemaOp = Lml3TestBuilder.BuildRecoveryOp(0, 0, -1, Lml3TestBuilder.UpsertTablePayload(1, schemaRecord));
        var rowRecord = SqliteRecordCodec.Encode([SqlValue.Null, SqlValue.Text(columnValue)]);
        var rowOp = Lml3TestBuilder.BuildRecoveryOp(0, 0, -2, Lml3TestBuilder.UpsertTablePayload(rowId, rowRecord));
        var recoveryPayload = schemaOp.Concat(rowOp).ToArray();
        var portableTxn = Lml3TestBuilder.BuildPortableLogicalTxn(1, 1, [tableName], [(-2, 0)]);
        var extRecord = Lml3TestBuilder.BuildExtensionRecord(
            Lml3TestBuilder.PortableChangesExtensionType,
            Lml3TestBuilder.Delimited(portableTxn));
        var frame = Lml3TestBuilder.BuildFrame(ref crc, recoveryPayload, opCount: 2, extensionBlock: extRecord);
        var logicalBody = logHeader.Concat(frame).ToArray();
        var range = new List<byte>();
        WriteVarintField(range, 1, 1);
        WriteVarintField(range, 2, 0);
        WriteVarintField(range, 3, checked((ulong)logicalBody.Length));
        WriteVarintField(range, 4, 1);
        return (logicalBody, range.ToArray());
    }

    private static void WriteDelimitedMessage(List<byte> destination, List<byte> message)
    {
        WriteVarint(destination, checked((ulong)message.Count));
        destination.AddRange(message);
    }

    private static void WriteLengthDelimitedField(List<byte> destination, int fieldNumber, ReadOnlySpan<byte> value)
    {
        WriteVarint(destination, checked((ulong)fieldNumber << 3 | 2));
        WriteVarint(destination, checked((ulong)value.Length));
        destination.AddRange(value.ToArray());
    }

    private static void WriteVarintField(List<byte> destination, int fieldNumber, ulong value)
    {
        WriteVarint(destination, checked((ulong)fieldNumber << 3));
        WriteVarint(destination, value);
    }

    private static void WriteVarint(List<byte> destination, ulong value)
    {
        while (value >= 0x80)
        {
            destination.Add((byte)((value & 0x7f) | 0x80));
            value >>= 7;
        }
        destination.Add((byte)value);
    }
}
