using AwesomeAssertions;
using Ahtola.Core.Storage;
using System.Security.Cryptography;

namespace Ahtola.Tests;

/// <summary>
/// Crash-safety of the two publications that decide whether a managed embedded replica may be
/// exposed at all: the mandatory post-bootstrap MVCC logical catch-up, and the atomic replacement
/// of the remote-base snapshot when a partial replica becomes fully materialized.
/// </summary>
/// <remarks>
/// <para>
/// An MVCC-logical bootstrap ships a raw page image of the last durable generation base -- the
/// server deliberately never checkpoints for a bootstrap (see
/// <c>turso-src/sync/engine/src/database_sync_operations.rs</c>) -- so the image is stale by
/// construction and the replica owes an immediate logical catch-up. Recording that obligation
/// only in memory is not enough: a crash between publishing the base and running the catch-up
/// would leave a durable (database, metadata) pair that every later open accepts at face value,
/// silently serving pre-checkpoint data forever. The obligation is therefore part of the same
/// durable bootstrap publication, and the replica stays non-exposable until it is retired.
/// </para>
/// <para>
/// Every test here drives the fault-injection boundaries directly, so "crash" means the process
/// stops at that exact durable point: in-process compensation does not run, and what remains on
/// disk is what the next open must cope with.
/// </para>
/// </remarks>
public sealed partial class ManagedEmbeddedReplicaConnectionTests
{
    /// <summary>
    /// A page-protocol bootstrap owes no catch-up: it is complete and exposable the instant it is
    /// published, and must not acquire a new obligation that would force a pointless extra pull.
    /// </summary>
    [Test]
    public void PageProtocolBootstrapOwesNoCatchUpAndIsExposableImmediately()
    {
        var path = NewReplicaPath("managed-replica-pages-no-catchup");
        var image = CreateDatabaseImage(path + ".source");
        var handler = new PullUpdatesHandler([CreatePullResponse("revision-42", image, protocol: 1)]);
        var options = CreateOptions(path, handler);

        try
        {
            using var connection = AhtolaConnection.CreateReplica(options);
            connection.Open();
            ReadBootstrapMarker(connection).Should().Be(42);

            handler.CallCount.Should().Be(1, "a page-protocol bootstrap is already current");
            var publication = ManagedReplicaBootstrapper.GetBootstrapPublicationInfo(path);
            publication.RequiresCatchUp.Should().BeFalse();
            publication.IsComplete.Should().BeTrue();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    /// <summary>
    /// Crashing after the bootstrap is durably published but before the catch-up runs must leave a
    /// replica that is present but NOT exposable, and the next open must finish the owed catch-up
    /// against the already-installed base rather than re-downloading it.
    /// </summary>
    [Test]
    public void BootstrapCrashBeforeCatchUpLeavesANonExposableReplicaTheNextOpenCatchesUp()
    {
        var path = NewReplicaPath("managed-replica-catchup-crash-published");
        var image = CreateDatabaseImage(path + ".source");
        var handler = new CountingPullUpdatesHandler(
        [
            CreatePullResponse("revision-42", image, protocol: 2),
            CreateLogicalPullResponse("revision-42", body: []),
        ]);
        var options = CreateOptions(path, handler);

        try
        {
            InterruptOpenAt(options, ManagedReplicaDurableBoundary.BootstrapCatchUpRequirementPublished);

            // Durable, complete, and self-consistent -- but deliberately not exposable.
            File.Exists(path).Should().BeTrue();
            File.Exists(path + ManagedReplicaBootstrapper.MetadataSuffix).Should().BeTrue();
            var interrupted = ManagedReplicaBootstrapper.GetBootstrapPublicationInfo(path);
            interrupted.RequiresCatchUp.Should().BeTrue();
            interrupted.IsComplete.Should().BeFalse(
                "a bootstrap that still owes its catch-up must never satisfy the exposability gate");
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.Revision.Should().Be("revision-42");
            handler.BootstrapCallCount.Should().Be(1);
            handler.CatchUpCallCount.Should().Be(0);

            using var connection = AhtolaConnection.CreateReplica(options);
            connection.Open();
            ReadBootstrapMarker(connection).Should().Be(42);

            handler.BootstrapCallCount.Should().Be(
                1,
                "the installed base image is already on disk; resuming must not re-download it");
            handler.CatchUpCallCount.Should().Be(1);
            var resumed = ManagedReplicaBootstrapper.GetBootstrapPublicationInfo(path);
            resumed.RequiresCatchUp.Should().BeFalse();
            resumed.IsComplete.Should().BeTrue();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    /// <summary>
    /// A crash inside a resumed catch-up must not destroy the durable state it was resuming: that
    /// state predates the attempt and is exactly as safe (installed, non-exposable) as it was
    /// before it, so a transient failure must cost a retry, not a full re-download.
    /// </summary>
    [Test]
    public void CrashDuringAResumedCatchUpKeepsTheInstalledBaseForTheNextAttempt()
    {
        var path = NewReplicaPath("managed-replica-catchup-crash-apply");
        var image = CreateDatabaseImage(path + ".source");
        var handler = new CountingPullUpdatesHandler(
        [
            CreatePullResponse("revision-42", image, protocol: 2),
            CreateLogicalPullResponse("revision-42", body: []),
        ]);
        var options = CreateOptions(path, handler);

        try
        {
            InterruptOpenAt(options, ManagedReplicaDurableBoundary.BootstrapCatchUpRequirementPublished);
            var installedBytes = File.ReadAllBytes(path).Length;

            // Second attempt crashes inside the catch-up itself, before it does any work.
            InterruptOpenAt(options, ManagedReplicaDurableBoundary.BootstrapCatchUpStarted);

            File.Exists(path).Should().BeTrue(
                "resuming an already-published bootstrap must never discard it on a transient failure");
            File.ReadAllBytes(path).Length.Should().Be(installedBytes);
            ManagedReplicaBootstrapper.GetBootstrapPublicationInfo(path).RequiresCatchUp.Should().BeTrue();
            handler.BootstrapCallCount.Should().Be(1);

            using var connection = AhtolaConnection.CreateReplica(options);
            connection.Open();
            ReadBootstrapMarker(connection).Should().Be(42);

            handler.BootstrapCallCount.Should().Be(1);
            handler.CatchUpCallCount.Should().Be(1);
            ManagedReplicaBootstrapper.GetBootstrapPublicationInfo(path).IsComplete.Should().BeTrue();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    /// <summary>
    /// Crashing after the catch-up's own metadata is durable but before the completion marker is
    /// retired leaves a replica whose data is already current while the marker still asserts the
    /// obligation. The next open must repeat the catch-up from the advanced revision -- a no-op
    /// pull -- and retire the marker, never replaying the already-applied transaction twice.
    /// </summary>
    [Test]
    public async Task CrashAfterCatchUpMetadataPublishRetiresTheMarkerWithoutReplayingTheApply()
    {
        var path = NewReplicaPath("managed-replica-catchup-crash-metadata");
        var image = CreateDatabaseImage(path + ".source");
        var (logicalBody, rangeMessage) = BuildSimpleLogicalPullBody(
            tableName: "remote_items",
            rowId: 7,
            columnValue: "remote",
            schemaSql: "CREATE TABLE remote_items(id INTEGER PRIMARY KEY, x TEXT)",
            salt: 771UL);
        var handler = new CountingPullUpdatesHandler(
        [
            CreatePullResponse("revision-42", image, protocol: 2),
            CreateLogicalPullResponse("revision-43", logicalBody, rangeMessages: [rangeMessage]),
            CreateLogicalPullResponse("revision-43", body: []),
        ]);
        var options = CreateOptions(path, handler);

        try
        {
            InterruptOpenAt(options, ManagedReplicaDurableBoundary.LogicalApplyMetadataPublished);

            // The catch-up's apply is durable, so the on-disk revision already moved past the
            // bootstrap generation; the failed-catch-up rollback must recognize that and back off.
            File.Exists(path).Should().BeTrue();
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.Revision.Should().Be("revision-43");
            ManagedReplicaBootstrapper.GetBootstrapPublicationInfo(path).RequiresCatchUp.Should().BeTrue(
                "the completion marker is retired only after the catch-up returns, which it never did");

            using var connection = AhtolaConnection.CreateReplica(options);
            connection.Open();

            handler.BootstrapCallCount.Should().Be(1);
            handler.CatchUpCallCount.Should().Be(2, "the resumed catch-up re-pulls from the advanced revision");
            ManagedReplicaBootstrapper.GetBootstrapPublicationInfo(path).IsComplete.Should().BeTrue();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM remote_items;";
            Convert.ToInt64(await command.ExecuteScalarAsync()).Should().Be(
                1,
                "resuming must repeat only a no-op pull, never replay an already-applied transaction");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    /// <summary>
    /// Crashing in the window between a fully successful catch-up and the retirement of the
    /// completion marker keeps the replica non-exposable; the next open retires it.
    /// </summary>
    [Test]
    public void CrashBeforeCompletionMarkerRetirementLeavesTheReplicaNonExposableUntilTheNextOpen()
    {
        var path = NewReplicaPath("managed-replica-catchup-crash-retire");
        var image = CreateDatabaseImage(path + ".source");
        var handler = new CountingPullUpdatesHandler(
        [
            CreatePullResponse("revision-42", image, protocol: 2),
            CreateLogicalPullResponse("revision-42", body: []),
            CreateLogicalPullResponse("revision-42", body: []),
        ]);
        var options = CreateOptions(path, handler);

        try
        {
            InterruptOpenAt(options, ManagedReplicaDurableBoundary.BootstrapCatchUpPublished);

            handler.CatchUpCallCount.Should().Be(1, "the catch-up itself fully succeeded");
            ManagedReplicaBootstrapper.GetBootstrapPublicationInfo(path).IsComplete.Should().BeFalse(
                "an unretired marker must keep the replica closed even when its data is current");

            using var connection = AhtolaConnection.CreateReplica(options);
            connection.Open();
            ReadBootstrapMarker(connection).Should().Be(42);

            handler.BootstrapCallCount.Should().Be(1);
            ManagedReplicaBootstrapper.GetBootstrapPublicationInfo(path).IsComplete.Should().BeTrue();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    /// <summary>
    /// An interrupted rollback of a failed catch-up can leave the recorded obligation next to an
    /// already-dismantled replica: the rollback deletes the sidecars before the marker precisely so
    /// a crash never leaves a complete-looking pair with no marker. The resumed open must recognize
    /// that residue and re-bootstrap, not run a catch-up against artifacts that no longer exist --
    /// nothing else ever clears the marker, so treating the flag alone as proof would wedge the
    /// path permanently.
    /// </summary>
    [TestCase("metadata")]
    [TestCase("database")]
    public void MarkerWithoutItsArtifactsReBootstrapsInsteadOfResumingAnImpossibleCatchUp(string missing)
    {
        var path = NewReplicaPath($"managed-replica-catchup-residue-{missing}");
        var image = CreateDatabaseImage(path + ".source");
        var handler = new CountingPullUpdatesHandler(
        [
            CreatePullResponse("revision-42", image, protocol: 2),
            CreatePullResponse("revision-42", image, protocol: 2),
            CreateLogicalPullResponse("revision-42", body: []),
        ]);
        var options = CreateOptions(path, handler);

        try
        {
            InterruptOpenAt(options, ManagedReplicaDurableBoundary.BootstrapCatchUpRequirementPublished);
            ManagedReplicaBootstrapper.GetBootstrapPublicationInfo(path).RequiresCatchUp.Should().BeTrue();

            // Exactly what an interrupted DeleteBootstrappedReplicaFiles leaves behind: the marker
            // still asserts the obligation, but the artifacts a resume needs are already gone.
            File.Delete(missing == "metadata" ? path + ManagedReplicaBootstrapper.MetadataSuffix : path);
            ManagedReplicaBootstrapper.CanResumeRequiredCatchUp(path).Should().BeFalse();

            using var connection = AhtolaConnection.CreateReplica(options);
            connection.Open();
            ReadBootstrapMarker(connection).Should().Be(42);

            handler.BootstrapCallCount.Should().Be(
                2,
                "an unresumable obligation must be recovered by a clean re-bootstrap");
            handler.CatchUpCallCount.Should().Be(1);
            var recovered = ManagedReplicaBootstrapper.GetBootstrapPublicationInfo(path);
            recovered.IsComplete.Should().BeTrue();
            recovered.RequiresCatchUp.Should().BeFalse();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    /// <summary>
    /// The same residue for a sparse bootstrap: the lazy-page sidecar is gone while the marker still
    /// records both the obligation and the partial image, so a resume would fail closed on missing
    /// page state instead of recovering.
    /// </summary>
    [Test]
    public void PartialMarkerWithoutItsPageStateReBootstrapsInsteadOfResuming()
    {
        var path = NewReplicaPath("managed-replica-catchup-residue-pagestate");
        var image = CreateMultiPageDatabaseImage(path + ".source");
        var handler = new QueryBootstrapPullHandler(
            "revision-query",
            image,
            bootstrapPages: [0u],
            protocol: 2);
        var options = CreateOptions(
            path,
            handler,
            partialBootstrap: AhtolaPartialBootstrapOptions.QueryPages(BootstrapQuery));

        try
        {
            InterruptOpenAt(options, ManagedReplicaDurableBoundary.BootstrapCatchUpRequirementPublished);
            var interrupted = ManagedReplicaBootstrapper.GetBootstrapPublicationInfo(path);
            interrupted.RequiresCatchUp.Should().BeTrue();
            interrupted.RequiresPageState.Should().BeTrue();

            File.Delete(path + ManagedReplicaPageMaterializingFileSystem.StateSuffix);
            ManagedReplicaBootstrapper.CanResumeRequiredCatchUp(path).Should().BeFalse();

            var bootstrapsBeforeRecovery = handler.BootstrapCallCount;
            using var connection = AhtolaConnection.CreateReplica(options);
            connection.Open();
            ReadBootstrapMarker(connection).Should().Be(42);

            handler.BootstrapCallCount.Should().Be(
                bootstrapsBeforeRecovery + 1,
                "an unresumable partial obligation must be recovered by a clean re-bootstrap");
            handler.LogicalCatchUpCallCount.Should().Be(1);
            ManagedReplicaBootstrapper.GetBootstrapPublicationInfo(path).IsComplete.Should().BeTrue();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    /// <summary>
    /// Restart behavior for both sparse selection strategies. A partial MVCC bootstrap records the
    /// owed catch-up alongside its lazy-page state, and the resumed open must complete the sparse
    /// image and the catch-up without re-selecting the page set -- for a server-chosen query set
    /// (tag 7) exactly as for a client-chosen prefix range (tag 5).
    /// </summary>
    [TestCase(true)]
    [TestCase(false)]
    public void PartialBootstrapCrashBeforeCatchUpResumesWithoutReselectingThePageSet(bool query)
    {
        var path = NewReplicaPath($"managed-replica-partial-catchup-crash-{(query ? "query" : "prefix")}");
        var image = CreateMultiPageDatabaseImage(path + ".source");
        var handler = new QueryBootstrapPullHandler(
            "revision-query",
            image,
            bootstrapPages: [0u],
            protocol: 2);
        var options = CreateOptions(
            path,
            handler,
            partialBootstrap: query
                ? AhtolaPartialBootstrapOptions.QueryPages(BootstrapQuery)
                : AhtolaPartialBootstrapOptions.Prefix(4096));

        try
        {
            InterruptOpenAt(options, ManagedReplicaDurableBoundary.BootstrapCatchUpRequirementPublished);

            var interrupted = ManagedReplicaBootstrapper.GetBootstrapPublicationInfo(path);
            interrupted.RequiresCatchUp.Should().BeTrue();
            interrupted.RequiresPageState.Should().BeTrue("the sparse image is still lazily materialized");
            interrupted.IsComplete.Should().BeFalse();
            File.Exists(path + ManagedReplicaPageMaterializingFileSystem.StateSuffix).Should().BeTrue();
            var selectionsBeforeResume = query ? handler.BootstrapCallCount : handler.TargetedRequests.Count;
            handler.LogicalCatchUpCallCount.Should().Be(0);

            using var connection = AhtolaConnection.CreateReplica(options);
            connection.Open();
            ReadBootstrapMarker(connection).Should().Be(42);

            handler.LogicalCatchUpCallCount.Should().Be(1);
            if (query)
            {
                handler.BootstrapCallCount.Should().Be(
                    selectionsBeforeResume,
                    "resuming must never re-run the server-side query selection");
            }

            var resumed = ManagedReplicaBootstrapper.GetBootstrapPublicationInfo(path);
            resumed.IsComplete.Should().BeTrue();
            resumed.RequiresCatchUp.Should().BeFalse();
            resumed.RequiresPageState.Should().BeFalse("the catch-up completes the sparse image first");
            File.Exists(path + ManagedReplicaPageMaterializingFileSystem.StateSuffix).Should().BeFalse();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    /// <summary>
    /// Completing a sparse image must atomically replace the remote-base snapshot with the finished
    /// full image before metadata records its hash. The snapshot taken at bootstrap is a copy of the
    /// SPARSE file, so leaving it in place while metadata adopts the full-image hash would durably
    /// publish a base whose bytes and recorded hash disagree.
    /// </summary>
    [Test]
    public async Task CompletingASparseImageRepublishesTheRemoteBaseSnapshotItRecords()
    {
        var path = NewReplicaPath("managed-replica-complete-base-snapshot");
        var image = CreateMultiPageDatabaseImage(path + ".source");
        var handler = new QueryBootstrapPullHandler("revision-query", image, bootstrapPages: [0u]);
        var options = CreateOptions(
            path,
            handler,
            partialBootstrap: AhtolaPartialBootstrapOptions.QueryPages(BootstrapQuery));

        try
        {
            using (var connection = AhtolaConnection.CreateReplica(options))
            {
                connection.Open();
                var sparse = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
                ReadBaseSnapshotFingerprint(path).Should().Be(
                    sparse.RemoteBaseSha256,
                    "the bootstrap snapshot starts out matching the sparse image it copied");

                _ = await connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
            }

            File.Exists(path + ManagedReplicaPageMaterializingFileSystem.StateSuffix).Should().BeFalse();
            var completed = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            ReadBaseSnapshotFingerprint(path).Should().Be(
                completed.RemoteBaseSha256,
                "the recorded remote-base hash must always describe the snapshot bytes on disk");
            completed.RemoteBaseSha256.Should().Be(
                completed.DatabaseSha256,
                "a completed image and the base it publishes are the same bytes");
            File.Exists(path + ".ahtola-replica-base.previous").Should().BeFalse(
                "the superseded snapshot is retired once metadata names its replacement");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    /// <summary>
    /// The completion publishes in three ordered steps -- replace the snapshot, record its hash,
    /// retire the superseded copy -- so a crash at either boundary still leaves metadata's recorded
    /// hash matched by a retained snapshot copy, and a retry converges rather than failing closed.
    /// Driven against a locally diverged image so the sparse and completed bases really are
    /// different bytes: an image whose completion happens to be byte-identical could not tell a
    /// correct republication apart from no republication at all.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task InterruptedCompletionKeepsTheRemoteBaseSnapshotAndItsRecordedHashInAgreement(
        bool afterMetadata)
    {
        var boundary = afterMetadata
            ? ManagedReplicaDurableBoundary.PartialImageMetadataPublished
            : ManagedReplicaDurableBoundary.PartialImageBaseSnapshotPublished;
        var path = NewReplicaPath($"managed-replica-complete-crash-{boundary}");
        var image = CreateMultiPageDatabaseImage(path + ".source");
        var handler = new QueryBootstrapPullHandler("revision-query", image, bootstrapPages: [0u]);
        var options = CreateOptions(
            path,
            handler,
            partialBootstrap: AhtolaPartialBootstrapOptions.QueryPages(BootstrapQuery));

        try
        {
            await ManagedReplicaBootstrapper.BootstrapAsync(options, CancellationToken.None);
            var bootstrapped = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            MutateOnePageLocally(options, bootstrapped.Revision, pageIndex: 3, fill: 0xA7);

            using (ManagedReplicaFaultInjection.Push(point =>
                   {
                       if (point == boundary)
                           throw new InvalidOperationException("Injected completion interruption.");
                   }))
            {
                Assert.CatchAsync(() => ManagedReplicaBootstrapper.CompletePartialReplicaAsync(
                    options,
                    bootstrapped,
                    allowTrackedLocalMutations: true,
                    retainedMaterializer: null,
                    CancellationToken.None));
            }

            // Whatever the crash point, the hash metadata records must still be matched by a
            // snapshot copy that is actually on disk -- otherwise the next process to need the base
            // for a conflict rebase fails its integrity check with nothing to fall back on.
            var interrupted = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            interrupted.RemoteBaseSha256.Should().Be(
                afterMetadata ? interrupted.DatabaseSha256 : bootstrapped.RemoteBaseSha256,
                afterMetadata
                    ? "metadata is durable once the boundary after it is reached"
                    : "metadata still names the superseded base until it is republished");
            ResolvableBaseSnapshotFingerprints(path).Should().Contain(
                interrupted.RemoteBaseSha256,
                "a retained snapshot copy must always still match the recorded hash");
            File.Exists(path + ManagedReplicaPageMaterializingFileSystem.StateSuffix).Should().BeTrue(
                "the completion never reached the point where the lazy-page state is retired");

            // Recovery: retrying the completion from the durable metadata converges on a single
            // snapshot whose bytes the recorded hash describes.
            var recovering = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            _ = await ManagedReplicaBootstrapper.CompletePartialReplicaAsync(
                options,
                recovering,
                allowTrackedLocalMutations: true,
                retainedMaterializer: null,
                CancellationToken.None);

            var recovered = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            ReadBaseSnapshotFingerprint(path).Should().Be(recovered.RemoteBaseSha256);
            recovered.RemoteBaseSha256.Should().Be(recovered.DatabaseSha256);
            recovered.RemoteBaseSha256.Should().NotBe(bootstrapped.RemoteBaseSha256);
            File.Exists(path + ".ahtola-replica-base.previous").Should().BeFalse();
            File.Exists(path + ManagedReplicaPageMaterializingFileSystem.StateSuffix).Should().BeFalse();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    /// <summary>
    /// The end-to-end shape the completed snapshot exists for: a query that selects only the header
    /// page, completion into a full image, and then a revision-advancing logical pull carrying an
    /// unpushed local change. That last step rebuilds the local image from the remote-base snapshot
    /// (a conflict rebase), which can only work if completion republished the snapshot to match the
    /// hash metadata records.
    /// </summary>
    [Test]
    public async Task HeaderOnlyQueryImageCompletesThenRebasesARevisionAdvancingLogicalPull()
    {
        var path = NewReplicaPath("managed-replica-header-only-rebase");
        var image = CreateMultiPageDatabaseImage(path + ".source");
        var handler = new QueryBootstrapPullHandler(
            "revision-query",
            image,
            bootstrapPages: [0u],
            protocol: 2);
        var options = CreateOptions(
            path,
            handler,
            partialBootstrap: AhtolaPartialBootstrapOptions.QueryPages(BootstrapQuery));

        try
        {
            using (var connection = AhtolaConnection.CreateReplica(options))
            {
                // The mandatory post-bootstrap catch-up completes the header-only image first, so
                // the replica is already a full image by the time the connection opens.
                connection.Open();
                File.Exists(path + ManagedReplicaPageMaterializingFileSystem.StateSuffix).Should().BeFalse();
                ReadBootstrapMarker(connection).Should().Be(42);
            }

            var completed = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            ReadBaseSnapshotFingerprint(path).Should().Be(completed.RemoteBaseSha256);

            var (logicalBody, rangeMessage) = BuildSimpleLogicalPullBody(
                tableName: "remote_items",
                rowId: 3,
                columnValue: "remote",
                schemaSql: "CREATE TABLE remote_items(id INTEGER PRIMARY KEY, x TEXT)",
                salt: 613UL);
            var advancingHandler = new PullUpdatesHandler(
            [
                CreateLogicalPullResponse("revision-next", logicalBody, rangeMessages: [rangeMessage]),
            ]);
            var advancingOptions = CreateOptions(path, advancingHandler);

            IReadOnlyList<ReplicaLocalChange> pendingChanges;
            using (var connection = AhtolaConnection.CreateReplica(advancingOptions))
            {
                connection.Open();
                connection.ExecuteNonQuery("CREATE TABLE local_items(id INTEGER PRIMARY KEY, x TEXT);");
                connection.ExecuteNonQuery("INSERT INTO local_items(id, x) VALUES (1, 'local');");
                pendingChanges = ManagedReplicaChangeJournal.Open(path).ReadBatch(int.MaxValue).Changes;
                pendingChanges.Should().NotBeEmpty();
            }

            // Metadata is reloaded from disk, exactly as a fresh process would: the rebase resolves
            // the remote-base snapshot by the hash recorded there, so a completion that failed to
            // republish the snapshot fails closed here instead of rebasing.
            var onDisk = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            var result = await ManagedReplicaBootstrapper.CheckForUpdatesAsync(
                advancingOptions,
                onDisk,
                new AhtolaSyncOptions(),
                pendingChanges,
                CancellationToken.None);

            result.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);

            using var reopened = AhtolaConnection.CreateReplica(advancingOptions);
            reopened.Open();
            ReadBootstrapMarker(reopened).Should().Be(42);
            using var remote = reopened.CreateCommand();
            remote.CommandText = "SELECT x FROM remote_items WHERE id = 3;";
            remote.ExecuteScalar().Should().Be("remote");
            using var local = reopened.CreateCommand();
            local.CommandText = "SELECT x FROM local_items WHERE id = 1;";
            local.ExecuteScalar().Should().Be(
                "local",
                "the rebase replays the unpushed local change on top of the completed remote base");

            var rebased = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            ReadBaseSnapshotFingerprint(path).Should().Be(rebased.RemoteBaseSha256);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    /// <summary>
    /// A revision-advancing PAGE sync after completion. The page path re-checks the main file's
    /// bytes against the fingerprint metadata recorded, so it also pins that completion published a
    /// metadata record describing the real completed image rather than a partially-defaulted one.
    /// </summary>
    [Test]
    public async Task CompletedSparseImageAcceptsARevisionAdvancingPageSync()
    {
        var path = NewReplicaPath("managed-replica-complete-page-sync");
        var image = CreateMultiPageDatabaseImage(path + ".source");
        var handler = new QueryBootstrapPullHandler("revision-query", image, bootstrapPages: [0u]);
        var options = CreateOptions(
            path,
            handler,
            partialBootstrap: AhtolaPartialBootstrapOptions.QueryPages(BootstrapQuery));

        try
        {
            using (var connection = AhtolaConnection.CreateReplica(options))
            {
                connection.Open();
                _ = await connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
                File.Exists(path + ManagedReplicaPageMaterializingFileSystem.StateSuffix).Should().BeFalse();
            }

            var replacementHandler = new PullUpdatesHandler(
                [CreatePullResponse("revision-next", image, protocol: 1, applyMode: 1)]);
            var replacementOptions = CreateOptions(path, replacementHandler);
            using var reopened = AhtolaConnection.CreateReplica(replacementOptions);
            reopened.Open();

            var result = await reopened.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);

            result.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);
            ReadBootstrapMarker(reopened).Should().Be(42);
            var advanced = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            advanced.Revision.Should().Be("revision-next");
            ReadBaseSnapshotFingerprint(path).Should().Be(advanced.RemoteBaseSha256);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    /// <summary>
    /// The precise invariant finding the snapshot bug is about: once a sparse image whose bytes
    /// have drifted from the snapshot published at bootstrap is completed, the recorded
    /// <c>remote_base_sha256</c> must describe bytes that are actually on disk. Completing without
    /// republishing durably records a hash no retained snapshot copy matches, and every later
    /// process that needs the base for a conflict rebase fails its integrity check with no way
    /// back. The rest of the metadata record must survive the completion intact too.
    /// </summary>
    [Test]
    public async Task CompletingALocallyDivergedSparseImagePublishesABaseTheMetadataCanResolve()
    {
        var path = NewReplicaPath("managed-replica-complete-diverged-base");
        var image = CreateMultiPageDatabaseImage(path + ".source");
        var handler = new QueryBootstrapPullHandler("revision-query", image, bootstrapPages: [0u]);
        var options = CreateOptions(
            path,
            handler,
            partialBootstrap: AhtolaPartialBootstrapOptions.QueryPages(BootstrapQuery));

        try
        {
            await ManagedReplicaBootstrapper.BootstrapAsync(options, CancellationToken.None);
            var bootstrapped = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            ReadBaseSnapshotFingerprint(path).Should().Be(bootstrapped.RemoteBaseSha256);

            // A whole-page local replacement through the lazy-page file system, which is exactly how
            // a sparse replica records local writes. It drifts the main image away from the
            // snapshot the bootstrap copied, so completion can no longer reuse that snapshot.
            MutateOnePageLocally(options, bootstrapped.Revision, pageIndex: 3, fill: 0xA7);

            var diverged = bootstrapped with { JournalBaseWatermark = 9 };
            var completed = await ManagedReplicaBootstrapper.CompletePartialReplicaAsync(
                options,
                diverged,
                allowTrackedLocalMutations: true,
                retainedMaterializer: null,
                CancellationToken.None);

            File.Exists(path + ManagedReplicaPageMaterializingFileSystem.StateSuffix).Should().BeFalse();
            var onDisk = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            onDisk.RemoteBaseSha256.Should().NotBe(
                bootstrapped.RemoteBaseSha256,
                "the completed image is not the sparse image the bootstrap snapshot copied");
            ReadBaseSnapshotFingerprint(path).Should().Be(
                onDisk.RemoteBaseSha256,
                "the recorded remote-base hash must describe snapshot bytes that exist on disk");
            onDisk.RemoteBaseSha256.Should().Be(completed.RemoteBaseSha256);
            onDisk.DatabaseSha256.Should().Be(completed.DatabaseSha256);
            onDisk.JournalBaseWatermark.Should().Be(
                9,
                "completion republishes the metadata record; it must not reset unrelated fields");
            File.Exists(path + ".ahtola-replica-base.previous").Should().BeFalse();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    /// <summary>
    /// Runs an <c>Open()</c> that is interrupted at <paramref name="boundary"/>, asserting it threw.
    /// </summary>
    private static void InterruptOpenAt(
        AhtolaReplicaOptions options,
        ManagedReplicaDurableBoundary boundary)
    {
        using (ManagedReplicaFaultInjection.Push(point =>
               {
                   if (point == boundary)
                       throw new InvalidOperationException($"Injected interruption at {boundary}.");
               }))
        {
            Assert.Catch(() => AhtolaConnection.CreateReplica(options).Open());
        }
    }

    private static string ReadBaseSnapshotFingerprint(string path)
        => ComputeFileSha256(path + ManagedReplicaBootstrapper.BaseSnapshotSuffix);

    /// <summary>
    /// Replaces one whole page of a sparse replica through the lazy-page file system -- exactly how
    /// a sparse replica records a local write -- so the main image drifts away from the remote-base
    /// snapshot the bootstrap copied.
    /// </summary>
    private static void MutateOnePageLocally(
        AhtolaReplicaOptions options,
        string revision,
        int pageIndex,
        byte fill)
    {
        using var materializing = new ManagedReplicaPageMaterializingFileSystem(
            PhysicalFileSystem.Instance,
            options.Path,
            revision,
            new ManagedReplicaPullPageSource(options),
            prefetchSegments: false);
        using var file = materializing.OpenFile(options.Path, FileOpenMode.OpenExisting);
        var replacement = new byte[4096];
        replacement.AsSpan().Fill(fill);
        file.Write(pageIndex * 4096L, replacement);
        file.FlushToDisk();
    }

    private static IReadOnlyList<string> ResolvableBaseSnapshotFingerprints(string path)
        => new[] { ManagedReplicaBootstrapper.BaseSnapshotSuffix, ".ahtola-replica-base.previous" }
            .Select(suffix => path + suffix)
            .Where(File.Exists)
            .Select(ComputeFileSha256)
            .ToArray();

    private static string ComputeFileSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    /// A database big enough that bootstrapping only the header page leaves genuine holes: the
    /// schema walk a bootstrap performs faults in the schema pages, so a database small enough to
    /// be fully covered by that walk would be byte-identical sparse and complete, and could never
    /// exercise a base-snapshot replacement.
    /// </summary>
    private static byte[] CreateLargeMultiPageDatabaseImage(string path)
    {
        try
        {
            CreateInitializedDatabase(path);
            using (var connection = new AhtolaConnection($"Data Source={path};Local Provider=Managed"))
            {
                connection.Open();
                connection.ExecuteNonQuery("CREATE TABLE bootstrap_payload(value BLOB NOT NULL);");
                for (var row = 0; row < 24; row++)
                    connection.ExecuteNonQuery("INSERT INTO bootstrap_payload VALUES (zeroblob(12000));");
            }

            return File.ReadAllBytes(path);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    /// <summary>
    /// A queued-response pull server that separates the raw page bootstrap from the logical
    /// catch-up pulls that follow it, so a resumed open can be proven not to re-download the base.
    /// A bootstrap request never carries a client revision (tag 3); a catch-up always does.
    /// </summary>
    private sealed class CountingPullUpdatesHandler(IEnumerable<byte[]> responses) : HttpMessageHandler
    {
        private readonly Queue<byte[]> _responses = new(responses);
        private int _bootstrapCallCount;
        private int _catchUpCallCount;

        public int BootstrapCallCount => Volatile.Read(ref _bootstrapCallCount);

        public int CatchUpCallCount => Volatile.Read(ref _catchUpCallCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var payload = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            if (TryReadLengthDelimitedField(payload, 3, out _))
                Interlocked.Increment(ref _catchUpCallCount);
            else
                Interlocked.Increment(ref _bootstrapCallCount);

            var message = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_responses.Dequeue()),
            };
            message.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/protobuf");
            return message;
        }
    }
}
