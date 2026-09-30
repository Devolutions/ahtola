using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AwesomeAssertions;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// Covers the explicit pull-only, push-only, checkpoint and statistics operations of a managed
/// embedded replica (the counterparts of Turso's <c>TursoSyncDatabase.Pull/Push/Checkpoint/GetStats</c>),
/// the automatic-synchronization status surface, and per-request auth-token providers.
/// </summary>
public sealed partial class ManagedEmbeddedReplicaConnectionTests
{
    [Test]
    public async Task PullAppliesRemoteChangesWithoutPushingPendingLocalChanges()
    {
        var path = NewReplicaPath("managed-replica-explicit-pull");
        var image = CreateJournalDatabaseImage(path + ".source");
        var handler = new ExplicitSyncHandler(
        [
            CreatePullResponse("revision-42", image, protocol: 2),
            CreateLogicalPullResponse("revision-42", body: []),
        ]);
        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (1);");
            var pullsBefore = handler.PullCallCount;

            var result = await connection.PullAsync();

            result.Outcome.Should().Be(AhtolaSyncOutcome.UpToDate);
            handler.PullCallCount.Should().Be(pullsBefore + 1);
            handler.PushCallCount.Should().Be(0, "a pull-only operation never pushes");
            var statistics = connection.GetSyncStatistics();
            statistics.CdcOperations.Should().Be(1, "the unpushed local change is still pending");
            statistics.LastPull.Should().NotBeNull();
            statistics.LastPush.Should().BeNull();
            statistics.Revision.Should().Be("revision-42");
            statistics.NetworkReceivedBytes.Should().BePositive();
            ReadJournalEventValues(connection).Should().Equal(1);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task PushSendsEveryPendingBatchWithoutPulling()
    {
        var path = NewReplicaPath("managed-replica-explicit-push");
        var image = CreateJournalDatabaseImage(path + ".source");
        var handler = new ExplicitSyncHandler(
        [
            CreatePullResponse("revision-42", image, protocol: 2),
            CreateLogicalPullResponse("revision-42", body: []),
        ]);
        try
        {
            using var connection = AhtolaConnection.CreateReplica(
                CreateOptions(path, handler, pushOperationsThreshold: 1));
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (1);");
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (2);");
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (3);");
            connection.GetSyncStatistics().CdcOperations.Should().Be(3);
            var pullsBefore = handler.PullCallCount;

            var result = await connection.PushAsync();

            result.Outcome.Should().Be(AhtolaSyncOutcome.UpToDate);
            result.Statistics.CdcOperations.Should().Be(3);
            result.Statistics.LastPush.Should().NotBeNull();
            handler.PushCallCount.Should().Be(3, "the threshold of one change caps every batch");
            handler.PullCallCount.Should().Be(pullsBefore, "a push-only operation never pulls");
            var statistics = connection.GetSyncStatistics();
            statistics.CdcOperations.Should().Be(0);
            statistics.LastPush.Should().NotBeNull();

            // A later push with nothing pending is a no-op, and a normal sync still works.
            connection.Push().Statistics.CdcOperations.Should().Be(0);
            handler.PushCallCount.Should().Be(3);
            (await connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None))
                .Outcome.Should().Be(AhtolaSyncOutcome.UpToDate);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [TestCase(1UL)]
    [TestCase(2UL)]
    public async Task CheckpointFoldsTheLocalWalAndKeepsPendingChangesPushable(ulong protocol)
    {
        var path = NewReplicaPath("managed-replica-explicit-checkpoint");
        var image = CreateJournalDatabaseImage(path + ".source");
        byte[][] pulls = protocol == 2
            ?
            [
                CreatePullResponse("revision-42", image, protocol: 2),
                CreateLogicalPullResponse("revision-42", body: []),
            ]
            :
            [
                CreatePullResponse("revision-42", image),
                CreatePullResponse("revision-42", [], declaredPages: 1),
            ];
        var handler = new ExplicitSyncHandler(pulls);
        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (7);");

            await connection.CheckpointAsync();

            var statistics = connection.GetSyncStatistics();
            statistics.MainWalSize.Should().BeLessThanOrEqualTo(32, "the WAL was checkpointed and truncated");
            statistics.RevertWalSize.Should().Be(0);
            statistics.CdcOperations.Should().Be(1, "a checkpoint never discards unpushed local changes");
            ReadJournalEventValues(connection).Should().Equal(7);
            handler.PushCallCount.Should().Be(0);

            var result = await connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
            result.Outcome.Should().Be(AhtolaSyncOutcome.UpToDate);
            handler.PushCallCount.Should().Be(1);
            connection.GetSyncStatistics().CdcOperations.Should().Be(0);
            ReadJournalEventValues(connection).Should().Equal(7);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void ExplicitSyncOperationsRequireAnEmbeddedReplica()
    {
        using var local = new AhtolaConnection("Data Source=:memory:;Local Provider=Managed");
        local.Open();

        Assert.Throws<NotSupportedException>(() => local.Pull());
        Assert.Throws<NotSupportedException>(() => local.Push());
        Assert.Throws<NotSupportedException>(() => local.Checkpoint());
        Assert.Throws<NotSupportedException>(() => local.GetSyncStatistics());
        local.AutomaticSyncStatus.Should().Be(AhtolaAutomaticSyncStatus.Stopped);

        using var closed = new AhtolaConnection();
        Assert.Throws<InvalidOperationException>(() => closed.Pull());
    }

    [Test]
    public async Task AutomaticPullOnlySyncPublishesStatusAndNeverPushes()
    {
        var path = NewReplicaPath("managed-replica-automatic-pull-only");
        var image = CreateJournalDatabaseImage(path + ".source");
        var handler = new ExplicitSyncHandler(
        [
            CreatePullResponse("revision-42", image, protocol: 2),
            CreateLogicalPullResponse("revision-42", body: []),
        ]);
        var baseOptions = CreateOptions(path, handler);
        var options = new AhtolaReplicaOptions(path, baseOptions.RemoteUri, baseOptions.AuthToken)
        {
            SyncInterval = 1,
            AutomaticSyncMode = AhtolaAutomaticSyncMode.PullOnly,
            HttpPolicy = baseOptions.HttpPolicy,
        };
        var observed = new ConcurrentQueue<AhtolaAutomaticSyncStatus>();
        var succeeded = new TaskCompletionSource<AhtolaAutomaticSyncStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var connection = AhtolaConnection.CreateReplica(options);
            connection.AutomaticSyncStatusChanged += (sender, args) =>
            {
                sender.Should().BeSameAs(connection);
                observed.Enqueue(args.Status);
                if (args.Status is { State: AhtolaAutomaticSyncState.Waiting, LastSuccess: not null })
                    succeeded.TrySetResult(args.Status);
            };
            connection.Open();
            connection.AutomaticSyncStatus.State.Should().BeOneOf(
                AhtolaAutomaticSyncState.Waiting,
                AhtolaAutomaticSyncState.Running);
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (1);");

            var success = await succeeded.Task.WaitAsync(TimeSpan.FromSeconds(10));

            success.LastPullAppliedChanges.Should().BeFalse();
            success.LastException.Should().BeNull();
            success.NextAttempt.Should().NotBeNull();
            observed.Select(status => status.State).Should().Contain(AhtolaAutomaticSyncState.Running);
            handler.PushCallCount.Should().Be(0, "pull-only automatic sync never pushes");
            connection.GetSyncStatistics().CdcOperations.Should().Be(1);

            connection.Close();
            connection.AutomaticSyncStatus.State.Should().Be(AhtolaAutomaticSyncState.Stopped);
            connection.AutomaticSyncStatus.LastSuccess.Should().NotBeNull();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task AutomaticSyncStatusExposesAFaultedBackgroundLoopBeforeClose()
    {
        var path = NewReplicaPath("managed-replica-automatic-faulted");
        var image = CreateJournalDatabaseImage(path + ".source");
        var handler = new ExplicitSyncHandler(
            [CreatePullResponse("revision-42", image)],
            _ => ReplicaPushHandler.BatchErrorResponse(5, 2, "conflicting local change", "SQLITE_CONSTRAINT"));
        var faulted = new TaskCompletionSource<AhtolaAutomaticSyncStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler, syncInterval: 1));
        try
        {
            connection.AutomaticSyncStatusChanged += (_, args) =>
            {
                if (args.Status.State == AhtolaAutomaticSyncState.Faulted)
                    faulted.TrySetResult(args.Status);
            };
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");

            var status = await faulted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            status.LastException.Should().BeOfType<AhtolaReplicaConflictException>();
            connection.AutomaticSyncStatus.State.Should().Be(AhtolaAutomaticSyncState.Faulted);
            Assert.Throws<AhtolaReplicaConflictException>(() => connection.Close());
            connection.AutomaticSyncStatus.State.Should().Be(AhtolaAutomaticSyncState.Stopped);
            connection.AutomaticSyncStatus.LastException.Should().BeOfType<AhtolaReplicaConflictException>();
        }
        finally
        {
            connection.Dispose();
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task ReplicaAuthTokenProviderIsConsultedForEverySyncRequest()
    {
        var path = NewReplicaPath("managed-replica-token-provider");
        var image = CreateJournalDatabaseImage(path + ".source");
        var handler = new ExplicitSyncHandler(
        [
            CreatePullResponse("revision-42", image, protocol: 2),
            CreateLogicalPullResponse("revision-42", body: []),
        ]);
        var baseOptions = CreateOptions(path, handler);
        var issued = 0;
        var options = new AhtolaReplicaOptions(path, baseOptions.RemoteUri, authToken: "static-token")
        {
            AuthTokenProvider = _ => ValueTask.FromResult<string?>(
                "rotated-" + Interlocked.Increment(ref issued).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            HttpPolicy = baseOptions.HttpPolicy,
        };
        try
        {
            using var connection = AhtolaConnection.CreateReplica(options);
            connection.AuthTokenProvider.Should().BeSameAs(options.AuthTokenProvider);
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (1);");
            await connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);

            var headers = handler.AuthorizationHeaders.ToArray();
            headers.Length.Should().Be(handler.PullCallCount + handler.PushCallCount);
            handler.PushCallCount.Should().Be(1);
            headers.Should().OnlyHaveUniqueItems();
            headers.Should().AllSatisfy(header => header.Should().StartWith("rotated-"));
            headers.Should().NotContain("static-token");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void ReplicaClientNamePrefixesTheBootstrappedSyncClientIdentifier()
    {
        var path = NewReplicaPath("managed-replica-client-name");
        var image = CreateJournalDatabaseImage(path + ".source");
        var handler = new ExplicitSyncHandler([CreatePullResponse("revision-42", image)]);
        var baseOptions = CreateOptions(path, handler);
        var options = new AhtolaReplicaOptions(path, baseOptions.RemoteUri, baseOptions.AuthToken)
        {
            ClientName = "ahtola.tests_app",
            HttpPolicy = baseOptions.HttpPolicy,
        };
        try
        {
            using (var connection = AhtolaConnection.CreateReplica(options))
                connection.Open();

            var clientId = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.ClientId;
            clientId.Should().StartWith("ahtola.tests_app-");
            Guid.TryParseExact(clientId["ahtola.tests_app-".Length..], "N", out _).Should().BeTrue();

            // The persisted identifier is accepted when the replica is reopened.
            using var reopened = AhtolaConnection.CreateReplica(options);
            reopened.Open();
            reopened.GetSyncStatistics().Revision.Should().Be("revision-42");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }

        Assert.Throws<ArgumentException>(() => AhtolaConnection.CreateReplica(
            new AhtolaReplicaOptions(path, new Uri("https://example.test"), authToken: null)
            {
                ClientName = "has space",
            }));
    }

    [Test]
    public async Task SqliteFacadeExposesExplicitReplicaOperationsFromTheConnectionString()
    {
        var path = NewReplicaPath("sqlite-facade-explicit-sync");
        var image = CreateJournalDatabaseImage(path + ".source");
        var handler = new ExplicitSyncHandler(
        [
            CreatePullResponse("revision-42", image, protocol: 2),
            CreateLogicalPullResponse("revision-42", body: []),
        ]);
        var priorFactory = Ahtola.Data.Sqlite.SqliteConnection.RemoteMessageHandlerFactory;
        Ahtola.Data.Sqlite.SqliteConnection.RemoteMessageHandlerFactory = () => handler;
        var tokens = 0;
        try
        {
            using var replica = new Ahtola.Data.Sqlite.SqliteConnection(
                $"Data Source=https://example.test/cluster;Replica Path={path};Local Provider=Managed;Pooling=False;"
                + "Sync Client Name=facade;Sync Long Poll Timeout=250;Push Operations Threshold=1;"
                + "Automatic Sync Mode=PullOnly;Sync Experimental Features=views,strict")
            {
                AuthTokenProvider = _ => ValueTask.FromResult<string?>(
                    "facade-" + Interlocked.Increment(ref tokens).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            };
            await replica.OpenAsync();
            using (var insert = replica.CreateCommand())
            {
                insert.CommandText = "INSERT INTO journal_events VALUES (1); INSERT INTO journal_events VALUES (2);";
                insert.ExecuteNonQuery();
            }

            (await replica.PullAsync()).Outcome.Should().Be(AhtolaSyncOutcome.UpToDate);
            handler.PushCallCount.Should().Be(0);
            replica.GetSyncStatistics().CdcOperations.Should().Be(2);
            replica.Checkpoint();
            (await replica.PushAsync()).Statistics.CdcOperations.Should().Be(2);
            handler.PushCallCount.Should().Be(2);
            (await replica.GetSyncStatisticsAsync()).CdcOperations.Should().Be(0);
            replica.AutomaticSyncStatus.State.Should().Be(AhtolaAutomaticSyncState.Stopped);
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.ClientId.Should().StartWith("facade-");
            handler.AuthorizationHeaders.Should().AllSatisfy(header => header.Should().StartWith("facade-"));
        }
        finally
        {
            Ahtola.Data.Sqlite.SqliteConnection.RemoteMessageHandlerFactory = priorFactory;
            DeleteReplicaFiles(path);
        }
    }

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

    /// <summary>
    /// Serves queued pull-updates responses (repeating the last one once the queue is drained, so
    /// it must be a no-op), acknowledges every Hrana push batch with the exact step count it
    /// carried unless a custom response is supplied, and records every bearer token it receives.
    /// </summary>
    private sealed class ExplicitSyncHandler(
        IEnumerable<byte[]> pullResponses,
        Func<HttpRequestMessage, HttpResponseMessage>? pushResponse = null) : HttpMessageHandler
    {
        private readonly Queue<byte[]> _pullResponses = new(pullResponses);
        private byte[]? _lastPullResponse;
        private int _pullCallCount;
        private int _pushCallCount;

        public int PullCallCount => Volatile.Read(ref _pullCallCount);

        public int PushCallCount => Volatile.Read(ref _pushCallCount);

        public ConcurrentQueue<string?> AuthorizationHeaders { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            AuthorizationHeaders.Enqueue(request.Headers.Authorization?.Parameter);
            if (request.RequestUri!.AbsolutePath.EndsWith("/pull-updates", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _pullCallCount);
                byte[] payload;
                lock (_pullResponses)
                {
                    payload = _pullResponses.Count != 0
                        ? _lastPullResponse = _pullResponses.Dequeue()
                        : _lastPullResponse!;
                }

                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload),
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/protobuf");
                return response;
            }

            Interlocked.Increment(ref _pushCallCount);
            if (pushResponse is not null)
                return pushResponse(request);

            using var document = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var steps = document.RootElement.GetProperty("requests")[0].GetProperty("batch").GetProperty("steps");
            return ReplicaPushHandler.SuccessfulBatchResponse(steps.GetArrayLength());
        }
    }
}
