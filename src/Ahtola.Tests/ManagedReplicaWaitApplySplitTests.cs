using System.Net;
using System.Net.Http.Headers;
using Ahtola.Core.Storage;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Covers the managed replica sync engine's staged wait/apply lifecycle (mirrors Turso's
/// <c>wait_changes_from_remote</c> -&gt; opaque staged changes -&gt;
/// <c>apply_changes_from_remote</c> split; see
/// turso-src/sync/engine/src/database_sync_engine.rs): that
/// <see cref="AhtolaConnection.SyncAsync(AhtolaSyncOptions, CancellationToken)"/> no longer holds
/// the per-path host publication gate during the network long-poll, and that
/// <see cref="ManagedReplicaBootstrapper.ManagedReplicaStagedChanges"/> fails closed against
/// cross-replica, duplicate-apply, disposed-result, and stale-revision misuse.
/// </summary>
public sealed partial class ManagedEmbeddedReplicaConnectionTests
{
    [Test]
    public async Task SyncAsyncDoesNotBlockSiblingReadsDuringTheNetworkWait()
    {
        var path = NewReplicaPath("managed-replica-wait-split-read-unblocked");
        var image = CreateDatabaseImage(path + ".source");
        var handler = new BlockingPullUpdatesHandler(
            CreatePullResponse("revision-42", image),
            CreatePullResponse("revision-42", [], declaredPages: 1));
        try
        {
            using var local = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            using var synchronizer = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            local.Open();
            synchronizer.Open();

            var sync = synchronizer.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
            await handler.SyncStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // The sync's pull-updates request is now blocked inside the network long-poll. Before
            // the wait/apply split, the per-path publication gate was held for this entire staged
            // operation (push, then the fused wait+apply), so a sibling connection's database
            // adapter would already be closed and this read would either block on
            // EnterLocalOperationAsync or fail outright. It must instead complete immediately: the
            // wait for remote changes no longer holds any publication gate (see
            // ManagedReplicaBootstrapper.WaitForRemoteChangesAsync).
            var readTask = Task.Run(() => local.ExecuteNonQuery("SELECT value FROM bootstrap_marker;"));
            (await readTask.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(0);
            sync.IsCompleted.Should().BeFalse();

            // A brand-new read-only transaction started while the wait is in flight is not
            // blocked either.
            using (var transaction = local.BeginTransaction())
            {
                using var command = local.CreateCommand();
                command.CommandText = "SELECT value FROM bootstrap_marker;";
                command.Transaction = transaction;
                _ = command.ExecuteScalar();
                transaction.Commit();
            }
            sync.IsCompleted.Should().BeFalse();

            handler.Release();
            (await sync.WaitAsync(TimeSpan.FromSeconds(5))).Outcome.Should().Be(AhtolaSyncOutcome.UpToDate);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task WaitForRemoteChangesAsyncStagesAPagesResponseWithoutMutatingLocalState()
    {
        var path = NewReplicaPath("managed-replica-wait-split-stage-pages");
        var image = CreateDatabaseImage(path + ".source");
        var updatedImage = CreateDatabaseImageWithMarker(path + ".updated", 84);
        var handler = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", image),
            CreatePullResponse("revision-43", updatedImage),
        ]);
        var options = CreateOptions(path, handler);
        try
        {
            using (var setup = AhtolaConnection.CreateReplica(options))
                setup.Open();

            var metadata = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            using var staged = await ManagedReplicaBootstrapper.WaitForRemoteChangesAsync(
                    options, metadata, new AhtolaSyncOptions(), pendingLocalChanges: [], acknowledgedLocalChanges: [],
                    CancellationToken.None)
                .ConfigureAwait(false);

            // Staging never touches the durable database: the revision on disk is unchanged, and
            // the response is not yet applied.
            staged.IsEmpty.Should().BeFalse();
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.Revision.Should().Be("revision-42");

            var applied = await ManagedReplicaBootstrapper.ApplyRemoteChangesAsync(
                    options, staged, new AhtolaSyncOptions(), expectedConflictState: null, CancellationToken.None)
                .ConfigureAwait(false);
            applied.Result.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.Revision.Should().Be("revision-43");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task WaitForRemoteChangesAsyncStagesANoOpResponseThatAppliesToUpToDate()
    {
        var path = NewReplicaPath("managed-replica-wait-split-stage-noop");
        var image = CreateDatabaseImage(path + ".source");
        var handler = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", image),
            CreatePullResponse("revision-42", [], declaredPages: 1),
        ]);
        var options = CreateOptions(path, handler);
        try
        {
            using (var setup = AhtolaConnection.CreateReplica(options))
                setup.Open();

            var metadata = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            using var staged = await ManagedReplicaBootstrapper.WaitForRemoteChangesAsync(
                    options, metadata, new AhtolaSyncOptions(), pendingLocalChanges: [], acknowledgedLocalChanges: [],
                    CancellationToken.None)
                .ConfigureAwait(false);

            staged.IsEmpty.Should().BeTrue();

            var applied = await ManagedReplicaBootstrapper.ApplyRemoteChangesAsync(
                    options, staged, new AhtolaSyncOptions(), expectedConflictState: null, CancellationToken.None)
                .ConfigureAwait(false);
            applied.Result.Outcome.Should().Be(AhtolaSyncOutcome.UpToDate);
            applied.ReplayedLocalChangeCount.Should().Be(0);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task ApplyRemoteChangesAsyncThrowsWhenLocalStateAdvancedPastTheStagedSnapshot()
    {
        var path = NewReplicaPath("managed-replica-wait-split-stale-revision");
        var image = CreateDatabaseImage(path + ".source");
        var handler = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", image),
            CreatePullResponse("revision-43", CreateDatabaseImageWithMarker(path + ".updated-a", 100)),
            CreatePullResponse("revision-44", CreateDatabaseImageWithMarker(path + ".updated-b", 200)),
        ]);
        var options = CreateOptions(path, handler);
        try
        {
            using (var setup = AhtolaConnection.CreateReplica(options))
                setup.Open();

            var metadata = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;

            // Two independent waits negotiated against the SAME (revision-42) base -- exactly the
            // situation a concurrent racer (another sync, another process) creates for the second
            // one once the first has published.
            using var stagedFirst = await ManagedReplicaBootstrapper.WaitForRemoteChangesAsync(
                    options, metadata, new AhtolaSyncOptions(), pendingLocalChanges: [], acknowledgedLocalChanges: [],
                    CancellationToken.None)
                .ConfigureAwait(false);
            using var stagedSecond = await ManagedReplicaBootstrapper.WaitForRemoteChangesAsync(
                    options, metadata, new AhtolaSyncOptions(), pendingLocalChanges: [], acknowledgedLocalChanges: [],
                    CancellationToken.None)
                .ConfigureAwait(false);

            var appliedFirst = await ManagedReplicaBootstrapper.ApplyRemoteChangesAsync(
                    options, stagedFirst, new AhtolaSyncOptions(), expectedConflictState: null, CancellationToken.None)
                .ConfigureAwait(false);
            appliedFirst.Result.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.Revision.Should().Be("revision-43");

            // The second staged response was negotiated against revision-42, which the durable
            // metadata has since moved past -- applying it must never silently regress metadata or
            // discard the first apply; it must fail closed with the fresh snapshot instead.
            var stale = Assert.ThrowsAsync<ManagedReplicaStaleChangesException>(() =>
                ManagedReplicaBootstrapper.ApplyRemoteChangesAsync(
                    options, stagedSecond, new AhtolaSyncOptions(), expectedConflictState: null, CancellationToken.None));
            stale!.FreshMetadata.Revision.Should().Be("revision-43");
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.Revision.Should().Be("revision-43");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task ApplyRemoteChangesAsyncRejectsChangesStagedForADifferentReplicaPath()
    {
        var pathA = NewReplicaPath("managed-replica-wait-split-cross-replica-a");
        var pathB = NewReplicaPath("managed-replica-wait-split-cross-replica-b");
        var imageA = CreateDatabaseImage(pathA + ".source");
        var imageB = CreateDatabaseImage(pathB + ".source");
        var handlerA = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", imageA),
            CreatePullResponse("revision-43", CreateDatabaseImageWithMarker(pathA + ".updated", 7)),
        ]);
        var handlerB = new PullUpdatesHandler([CreatePullResponse("revision-42", imageB)]);
        var optionsA = CreateOptions(pathA, handlerA);
        var optionsB = CreateOptions(pathB, handlerB);
        try
        {
            using (var setupA = AhtolaConnection.CreateReplica(optionsA))
                setupA.Open();
            using (var setupB = AhtolaConnection.CreateReplica(optionsB))
                setupB.Open();

            var metadataA = ManagedReplicaBootstrapper.LoadMetadata(pathA)!.Value;
            using var staged = await ManagedReplicaBootstrapper.WaitForRemoteChangesAsync(
                    optionsA, metadataA, new AhtolaSyncOptions(), pendingLocalChanges: [], acknowledgedLocalChanges: [],
                    CancellationToken.None)
                .ConfigureAwait(false);

            var exception = Assert.ThrowsAsync<InvalidOperationException>(() =>
                ManagedReplicaBootstrapper.ApplyRemoteChangesAsync(
                    optionsB, staged, new AhtolaSyncOptions(), expectedConflictState: null, CancellationToken.None));
            exception!.Message.Should().Contain("different replica path");

            // Misuse against the wrong path must not have consumed the staged changes: applying
            // them against the CORRECT path afterward still succeeds.
            var applied = await ManagedReplicaBootstrapper.ApplyRemoteChangesAsync(
                    optionsA, staged, new AhtolaSyncOptions(), expectedConflictState: null, CancellationToken.None)
                .ConfigureAwait(false);
            applied.Result.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);
        }
        finally
        {
            DeleteReplicaFiles(pathA);
            DeleteReplicaFiles(pathB);
        }
    }

    [Test]
    public async Task ApplyRemoteChangesAsyncRejectsReapplyingTheSameStagedChanges()
    {
        var path = NewReplicaPath("managed-replica-wait-split-duplicate-apply");
        var image = CreateDatabaseImage(path + ".source");
        var handler = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", image),
            CreatePullResponse("revision-43", CreateDatabaseImageWithMarker(path + ".updated", 7)),
        ]);
        var options = CreateOptions(path, handler);
        try
        {
            using (var setup = AhtolaConnection.CreateReplica(options))
                setup.Open();

            var metadata = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            using var staged = await ManagedReplicaBootstrapper.WaitForRemoteChangesAsync(
                    options, metadata, new AhtolaSyncOptions(), pendingLocalChanges: [], acknowledgedLocalChanges: [],
                    CancellationToken.None)
                .ConfigureAwait(false);

            var applied = await ManagedReplicaBootstrapper.ApplyRemoteChangesAsync(
                    options, staged, new AhtolaSyncOptions(), expectedConflictState: null, CancellationToken.None)
                .ConfigureAwait(false);
            applied.Result.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);

            var exception = Assert.ThrowsAsync<InvalidOperationException>(() =>
                ManagedReplicaBootstrapper.ApplyRemoteChangesAsync(
                    options, staged, new AhtolaSyncOptions(), expectedConflictState: null, CancellationToken.None));
            exception!.Message.Should().Contain("already applied or discarded");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task ApplyRemoteChangesAsyncRejectsChangesAlreadyDiscarded()
    {
        var path = NewReplicaPath("managed-replica-wait-split-disposed-result");
        var image = CreateDatabaseImage(path + ".source");
        var handler = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", image),
            CreatePullResponse("revision-43", CreateDatabaseImageWithMarker(path + ".updated", 7)),
        ]);
        var options = CreateOptions(path, handler);
        try
        {
            using (var setup = AhtolaConnection.CreateReplica(options))
                setup.Open();

            var metadata = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            var staged = await ManagedReplicaBootstrapper.WaitForRemoteChangesAsync(
                    options, metadata, new AhtolaSyncOptions(), pendingLocalChanges: [], acknowledgedLocalChanges: [],
                    CancellationToken.None)
                .ConfigureAwait(false);

            // Discarding a staged response that was never applied must be a safe no-op: nothing
            // durable or shared was ever touched while it was staged.
            staged.Dispose();
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.Revision.Should().Be("revision-42");

            var exception = Assert.ThrowsAsync<InvalidOperationException>(() =>
                ManagedReplicaBootstrapper.ApplyRemoteChangesAsync(
                    options, staged, new AhtolaSyncOptions(), expectedConflictState: null, CancellationToken.None));
            exception!.Message.Should().Contain("already applied or discarded");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task WaitForRemoteChangesAsyncObservesCancellationDuringTheLongPollWithoutMutatingLocalState()
    {
        var path = NewReplicaPath("managed-replica-wait-split-cancel");
        var image = CreateDatabaseImage(path + ".source");
        var bootstrapHandler = new PullUpdatesHandler([CreatePullResponse("revision-42", image)]);
        var options = CreateOptions(path, bootstrapHandler);
        try
        {
            using (var setup = AhtolaConnection.CreateReplica(options))
                setup.Open();

            var metadata = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            var delayed = new DelayedPullHandler(CreatePullResponse("revision-43", image));
            var delayedOptions = CreateOptions(path, delayed);
            using var cancellation = new CancellationTokenSource();

            var wait = ManagedReplicaBootstrapper.WaitForRemoteChangesAsync(
                delayedOptions, metadata, new AhtolaSyncOptions(), pendingLocalChanges: [], acknowledgedLocalChanges: [],
                cancellation.Token);
            await delayed.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            Assert.CatchAsync<OperationCanceledException>(() => wait);
            // A cancelled wait never reached any lock or local mutation: the durable revision is
            // exactly as it was before the call.
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.Revision.Should().Be("revision-42");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task SyncAsyncCalledImmediatelyAfterAPriorSyncCompletesNeverJoinsItsAlreadyCompletedTask()
    {
        var path = NewReplicaPath("managed-replica-wait-split-reentrant-sync");
        var image = CreateDatabaseImage(path + ".source");
        var handler = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", image),
            CreatePullResponse("revision-42", [], declaredPages: 1),
            CreatePullResponse("revision-43", CreateDatabaseImageWithMarker(path + ".updated", 7)),
        ]);
        var options = CreateOptions(path, handler);
        try
        {
            using var connection = AhtolaConnection.CreateReplica(options);
            connection.Open();

            var first = connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);

            // Chained via ContinueWith rather than a sequential await: this schedules the second
            // call to run as soon as `first` becomes observably complete -- exactly the
            // reentrancy window ManagedReplicaSyncRegistry.Entry.CompleteSyncAsync's fix closes.
            // Clearing _inFlightSync now happens-before completing first's TaskCompletionSource,
            // so any continuation of `first` (including this one) is guaranteed to observe
            // _inFlightSync already cleared and must start a brand-new sync rather than silently
            // joining the first call's already-completed cached result.
            var second = first
                .ContinueWith(_ => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None))
                .Unwrap();

            var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(5));
            var secondResult = await second.WaitAsync(TimeSpan.FromSeconds(5));

            firstResult.Outcome.Should().Be(AhtolaSyncOutcome.UpToDate);
            secondResult.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);
            // Bootstrap consumed the first queued response; each SyncAsync call above must have
            // performed its own real network poll rather than the second one silently reusing the
            // first's already-completed result, so exactly three /pull-updates calls were made.
            handler.CallCount.Should().Be(3);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task DisposingTheLastHostWhileASyncIsWaitingForRemoteChangesDoesNotRetireTheEntry()
    {
        var path = NewReplicaPath("managed-replica-wait-split-dispose-during-wait");
        var image = CreateDatabaseImage(path + ".source");
        var handler = new BlockingPullUpdatesHandler(
            CreatePullResponse("revision-42", image),
            CreatePullResponse("revision-42", [], declaredPages: 1));
        var options = CreateOptions(path, handler);
        try
        {
            var connection = AhtolaConnection.CreateReplica(options);
            connection.Open();

            var entryBeforeDispose = ManagedReplicaSyncRegistry.Acquire(path);
            entryBeforeDispose.ReleaseReference();

            var sync = connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
            await handler.SyncStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // The sync's pull-updates request is now blocked inside the network long-poll, which
            // -- per the wait/apply split -- runs without holding the per-path publication gate.
            // Disposing the only host registered for this path while that wait is still in flight
            // must not let RetireIfUnusedNoLock remove the coordinating Entry from the registry: a
            // subsequently opened host for the same path would otherwise get a brand-new,
            // unrelated Entry with no in-process coordination against this orphaned sync's still-
            // pending apply.
            connection.Dispose();

            var entryAfterDispose = ManagedReplicaSyncRegistry.Acquire(path);
            try
            {
                entryAfterDispose.Should().BeSameAs(entryBeforeDispose);
            }
            finally
            {
                entryAfterDispose.ReleaseReference();
            }

            handler.Release();
            var result = await sync.WaitAsync(TimeSpan.FromSeconds(5));
            result.Outcome.Should().Be(AhtolaSyncOutcome.UpToDate);

            // The orphaned sync's apply (a no-op here, since the response carried no changes)
            // still completed successfully with no host left registered to observe it. A fresh
            // connection afterward must see correct, undamaged data, proving the pinned Entry was
            // reused correctly and released cleanly once the sync actually finished.
            using var reopened = AhtolaConnection.CreateReplica(options);
            reopened.Open();
            ReadBootstrapMarker(reopened).Should().Be(42);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task PreparePushReadsTheMaterializationLeaseFreshAfterAConcurrentPublicationDisposesIt()
    {
        var path = NewReplicaPath("managed-replica-wait-split-materializer-turnover");
        var databaseImage = CreateDatabaseImage(path + ".source");
        databaseImage.Length.Should().BeGreaterThan(4096);
        var handler = new LazyPagePullHandler("revision-prefix", databaseImage);
        var options = CreateOptions(
            path,
            handler,
            partialBootstrap: AhtolaPartialBootstrapOptions.Prefix(4096));

        try
        {
            using (var connection = AhtolaConnection.CreateReplica(options))
            {
                connection.Open();
                ReadBootstrapMarker(connection).Should().Be(42);
            }

            File.Exists(path + ManagedReplicaPageMaterializingFileSystem.StateSuffix)
                .Should().BeTrue("the first connection's own diagnostics assumed a genuinely partial bootstrap");

            using var connectionA = AhtolaConnection.CreateReplica(options);
            connectionA.Open();

            File.Exists(path + ManagedReplicaPageMaterializingFileSystem.StateSuffix)
                .Should().BeTrue("opening a second connection onto a still-partial replica must not itself complete it");

            // Entry.SynchronizeAsync coalesces every SyncAsync caller for the SAME path into one
            // shared in-flight task (see ManagedReplicaSyncRegistry.Entry), so a second connection
            // racing host A's own SyncAsync would just join it rather than run as its own,
            // separate publication. Entry.PublishExclusiveAsync deliberately does not coalesce
            // (see its own doc comment), so driving it directly here -- exactly as bootstrap
            // catch-up or another connection's own apply phase would -- reproduces the race
            // without that coalescing hiding it: a publication that has nothing to do with host
            // A's own sync still closes and reopens every host registered on this path, host A
            // included (see ManagedReplicaSyncRegistry.PublishAsync).
            var entry = ManagedReplicaSyncRegistry.Acquire(path);
            try
            {
                var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using (ManagedReplicaFaultInjection.Push(boundary =>
                       {
                           if (boundary != ManagedReplicaDurableBoundary.PartialImageCompletionStarted)
                               return;

                           paused.TrySetResult();
                           release.Task.GetAwaiter().GetResult();
                       }))
                {
                    var metadata = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;

                    // The competing publication reuses the SAME reference-counted registry entry
                    // host A's own retained lease already holds (see
                    // ManagedReplicaPageMaterializationRegistry.Acquire): the underlying
                    // ManagedReplicaPageMaterializingFileSystem takes an exclusive OS-level
                    // ownership lock on the lazy-page state, so a second, independent instance
                    // (retainedMaterializer: null) would fail outright while host A's own lease is
                    // still alive, rather than reproducing the race this test targets.
                    using var materializerLease = ManagedReplicaPageMaterializationRegistry.Acquire(
                        PhysicalFileSystem.Instance,
                        path,
                        metadata.Revision,
                        new ManagedReplicaPullPageSource(options),
                        prefetchSegments: false);

                    // The competing publication acquires the gate first -- closing every host,
                    // host A included -- and pauses there, still holding it, immediately before it
                    // would materialize and complete the partial image (deleting the sidecar).
                    var completingPublication = Task.Run(() => entry.PublishExclusiveAsync(
                        token => ManagedReplicaBootstrapper.CompletePartialReplicaAsync(
                            options,
                            metadata,
                            allowTrackedLocalMutations: false,
                            retainedMaterializer: materializerLease.FileSystem,
                            token),
                        CancellationToken.None));
                    await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));

                    // Host A's own sync now queues behind that held gate. With the fix, host A's
                    // gated PreparePushAndPartialReplicaAsync has not run yet -- and will not
                    // until the competing publication (and its reopen pass across every host) has
                    // fully completed -- so it has not read _materializationLease at all yet.
                    var syncA = connectionA.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
                    await Task.Delay(TimeSpan.FromMilliseconds(250));
                    syncA.IsCompleted.Should().BeFalse();

                    release.TrySetResult();
                    await completingPublication.WaitAsync(TimeSpan.FromSeconds(5));

                    // The competing publication has now completed the partial image and reopened
                    // every host, including host A, which disposed host A's OWN retained lease
                    // (the sidecar is gone). Host A's own gated PreparePushAndPartialReplicaAsync
                    // runs next and must read _materializationLease fresh -- now null -- rather
                    // than reuse whatever it captured before it ever requested the gate; the old,
                    // reverted ordering would hand a disposed
                    // ManagedReplicaPageMaterializingFileSystem to PushLocalChangesAsync/
                    // CompletePartialReplicaAsync here and throw ObjectDisposedException.
                    var resultA = await syncA.WaitAsync(TimeSpan.FromSeconds(5));
                    resultA.Outcome.Should().Be(AhtolaSyncOutcome.UpToDate);
                }
            }
            finally
            {
                entry.ReleaseReference();
            }

            File.Exists(path + ManagedReplicaPageMaterializingFileSystem.StateSuffix).Should().BeFalse();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }
}
