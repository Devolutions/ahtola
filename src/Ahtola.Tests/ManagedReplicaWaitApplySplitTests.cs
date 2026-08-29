using System.Net;
using System.Net.Http.Headers;
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
}
