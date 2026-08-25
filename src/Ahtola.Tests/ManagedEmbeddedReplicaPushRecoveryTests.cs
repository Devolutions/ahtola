using System.Buffers.Binary;
using System.Diagnostics;
using AwesomeAssertions;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ahtola.Tests;

public sealed partial class ManagedEmbeddedReplicaConnectionTests
{
    [Test]
    public async Task OrdinaryAmbiguousPushUsesRemoteWatermarkWithoutReplayingSql()
    {
        var path = NewReplicaPath("managed-replica-ordinary-ambiguous-push");
        var image = CreateJournalDatabaseImage(path + ".source");
        var replayCount = 0;
        var watermarkReadCount = 0;
        var handler = new ReplicaPushHandler(
            [
                CreatePullResponse("revision-42", image),
                CreatePullResponse("revision-42", [], declaredPages: 1),
            ],
            (request, _) =>
            {
                if (IsReplicaPushBatch(request))
                {
                    replayCount++;
                    return Task.FromException<HttpResponseMessage>(
                        new HttpRequestException("Response was lost after the remote commit."));
                }

                watermarkReadCount++;
                return Task.FromResult(ReplicaPushHandler.WatermarkResponse(0, 1));
            });

        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");

            Assert.ThrowsAsync<HttpRequestException>(
                () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            var interrupted = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            interrupted.RevertState.Should().BeNull();
            interrupted.PushState.Should().Be(
                new ManagedReplicaBootstrapper.ManagedReplicaPushState(0, 1, 2));
            connection.ReadManagedReplicaLocalChanges(10).Changes.Should().ContainSingle();

            var result = await connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);

            result.Statistics.CdcOperations.Should().Be(1);
            replayCount.Should().Be(1, "the remote watermark proves the first replay committed");
            watermarkReadCount.Should().Be(1);
            connection.ReadManagedReplicaLocalChanges(10).Changes.Should().BeEmpty();
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.PushState.Should().BeNull();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task OrdinaryPushIntentInterruptionQueriesThenSendsTheBatchOnce()
    {
        var path = NewReplicaPath("managed-replica-ordinary-push-intent");
        var image = CreateJournalDatabaseImage(path + ".source");
        var replayCount = 0;
        var watermarkReadCount = 0;
        var handler = new ReplicaPushHandler(
            [
                CreatePullResponse("revision-42", image),
                CreatePullResponse("revision-42", [], declaredPages: 1),
            ],
            (request, _) =>
            {
                if (IsReplicaPushBatch(request))
                {
                    replayCount++;
                    return Task.FromResult(ReplicaPushHandler.SuccessfulBatchResponse(5));
                }

                watermarkReadCount++;
                return Task.FromResult(ReplicaPushHandler.EmptyWatermarkResponse());
            });

        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
            using (ManagedReplicaFaultInjection.Push(boundary =>
                   {
                       if (boundary == ManagedReplicaDurableBoundary.ReplicaPushIntentPublished)
                           throw new InvalidOperationException("Injected push-intent interruption.");
                   }))
            {
                Assert.ThrowsAsync<InvalidOperationException>(
                    () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            }

            handler.PushCallCount.Should().Be(0, "intent publication precedes every remote request");
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.PushState.Should().NotBeNull();

            var result = await connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);

            result.Statistics.CdcOperations.Should().Be(1);
            watermarkReadCount.Should().Be(1);
            replayCount.Should().Be(1);
            connection.ReadManagedReplicaLocalChanges(10).Changes.Should().BeEmpty();
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.PushState.Should().BeNull();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task CrashAfterRemoteCommitObservationRecoversWithoutReplayingSql()
    {
        var path = NewReplicaPath("managed-replica-remote-commit-observed");
        var image = CreateJournalDatabaseImage(path + ".source");
        var replayCount = 0;
        var handler = new ReplicaPushHandler(
            [
                CreatePullResponse("revision-42", image),
                CreatePullResponse("revision-42", [], declaredPages: 1),
            ],
            (request, _) =>
            {
                if (IsReplicaPushBatch(request))
                {
                    replayCount++;
                    return Task.FromResult(ReplicaPushHandler.SuccessfulBatchResponse(5));
                }

                return Task.FromResult(ReplicaPushHandler.WatermarkResponse(0, 1));
            });

        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
            using (ManagedReplicaFaultInjection.Push(boundary =>
                   {
                       if (boundary == ManagedReplicaDurableBoundary.ReplicaPushRemoteCommitObserved)
                           throw new InvalidOperationException("Injected post-commit interruption.");
                   }))
            {
                Assert.ThrowsAsync<InvalidOperationException>(
                    () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            }

            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.PushState.Should().NotBeNull();
            var result = await connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);

            result.Statistics.CdcOperations.Should().Be(1);
            replayCount.Should().Be(1);
            connection.ReadManagedReplicaLocalChanges(10).Changes.Should().BeEmpty();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task JournalAppendDuringRemoteCommitRecoversTheProtectedBatchWithoutLosingTheAppend()
    {
        var path = NewReplicaPath("managed-replica-concurrent-append-during-push");
        var image = CreateJournalDatabaseImage(path + ".source");
        var replayCount = 0;
        var watermarkReadCount = 0;
        var handler = new ReplicaPushHandler(
            [CreatePullResponse("revision-42", image)],
            (request, _) =>
            {
                if (IsReplicaPushBatch(request))
                {
                    replayCount++;
                    ManagedReplicaChangeJournal.Open(path).AppendCommitted(
                    [
                        ReplicaLocalChange.Schema(
                            "CREATE TABLE concurrently_journaled(value INTEGER);"),
                    ]);
                    return Task.FromResult(ReplicaPushHandler.SuccessfulBatchResponse(5));
                }

                watermarkReadCount++;
                return Task.FromResult(ReplicaPushHandler.WatermarkResponse(0, 1));
            });

        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");

            var interrupted = Assert.ThrowsAsync<AhtolaException>(
                () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            interrupted!.ReplicaPushFailureKind.Should().Be(AhtolaReplicaPushFailureKind.InvalidLocalState);
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.PushState.Should().NotBeNull();
            ManagedReplicaChangeJournal.Open(path).ReadBatch(int.MaxValue).Changes
                .Select(change => change.Sequence).Should().Equal(1L, 2L);

            using (ManagedReplicaFaultInjection.Push(boundary =>
                   {
                       if (boundary == ManagedReplicaDurableBoundary.ReplicaPushIntentRetired)
                           throw new InvalidOperationException("Injected intent-retirement interruption.");
                   }))
            {
                Assert.ThrowsAsync<InvalidOperationException>(
                    () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            }

            replayCount.Should().Be(1, "the remote watermark covers the protected first batch");
            watermarkReadCount.Should().Be(1);
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.PushState.Should().BeNull();
            var durable = ManagedReplicaChangeJournal.Open(path);
            durable.AcknowledgedWatermark.Should().Be(2);
            durable.ReadBatch(int.MaxValue).Changes.Select(change => change.Sequence).Should().Equal(2L);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task CrashAfterJournalAcknowledgementCompletesIntentRetirementOnRetry()
    {
        var path = NewReplicaPath("managed-replica-push-ack-interruption");
        var image = CreateJournalDatabaseImage(path + ".source");
        var replayCount = 0;
        var handler = new ReplicaPushHandler(
            [
                CreatePullResponse("revision-42", image),
                CreatePullResponse("revision-42", [], declaredPages: 1),
            ],
            (request, _) =>
            {
                if (IsReplicaPushBatch(request))
                {
                    replayCount++;
                    return Task.FromResult(ReplicaPushHandler.SuccessfulBatchResponse(5));
                }

                return Task.FromResult(ReplicaPushHandler.WatermarkResponse(0, 1));
            });

        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
            using (ManagedReplicaFaultInjection.Push(boundary =>
                   {
                       if (boundary == ManagedReplicaDurableBoundary.JournalAcknowledgementPersisted)
                           throw new InvalidOperationException("Injected acknowledgement interruption.");
                   }))
            {
                Assert.ThrowsAsync<InvalidOperationException>(
                    () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            }

            ManagedReplicaChangeJournal.Open(path).AcknowledgedWatermark.Should().Be(2);
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.PushState.Should().NotBeNull();

            var result = await connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);

            result.Statistics.CdcOperations.Should().Be(1);
            replayCount.Should().Be(1);
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.PushState.Should().BeNull();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [TestCase(0, 1, "splits")]
    [TestCase(1, 0, "ahead")]
    public void InvalidRemoteWatermarkFailsClosedWithoutReplayingSql(
        long pullGeneration,
        long changeId,
        string expectedMessage)
    {
        var path = NewReplicaPath("managed-replica-invalid-push-watermark");
        var image = CreateJournalDatabaseImage(path + ".source");
        var replayCount = 0;
        var handler = new ReplicaPushHandler(
            [CreatePullResponse("revision-42", image)],
            (request, _) =>
            {
                if (IsReplicaPushBatch(request))
                    replayCount++;
                return Task.FromResult(
                    ReplicaPushHandler.WatermarkResponse(pullGeneration, changeId));
            });

        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (20);");
            using (ManagedReplicaFaultInjection.Push(boundary =>
                   {
                       if (boundary == ManagedReplicaDurableBoundary.ReplicaPushIntentPublished)
                           throw new InvalidOperationException("Injected push-intent interruption.");
                   }))
            {
                Assert.ThrowsAsync<InvalidOperationException>(
                    () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            }

            var exception = Assert.ThrowsAsync<AhtolaException>(
                () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));

            exception!.ReplicaPushFailureKind.Should().Be(AhtolaReplicaPushFailureKind.InvalidLocalState);
            exception.Message.Should().Contain(expectedMessage);
            replayCount.Should().Be(0);
            connection.ReadManagedReplicaLocalChanges(10).Changes.Should().HaveCount(2);
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.PushState.Should().NotBeNull();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [TestCase("bad-base64")]
    [TestCase("checksum")]
    [TestCase("truncated")]
    [TestCase("trailing")]
    [TestCase("version")]
    [TestCase("negative-generation")]
    [TestCase("zero-first-sequence")]
    [TestCase("invalid-watermark")]
    public void MalformedPushIntentFailsClosedBeforeNetworkAccess(string corruption)
    {
        var path = NewReplicaPath("managed-replica-corrupt-push-intent");
        var image = CreateJournalDatabaseImage(path + ".source");
        var handler = new ReplicaPushHandler(
            [CreatePullResponse("revision-42", image)],
            _ => ReplicaPushHandler.SuccessfulBatchResponse(5));

        try
        {
            var options = CreateOptions(path, handler);
            using (var connection = AhtolaConnection.CreateReplica(options))
            {
                connection.Open();
                connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
                using (ManagedReplicaFaultInjection.Push(boundary =>
                       {
                           if (boundary == ManagedReplicaDurableBoundary.ReplicaPushIntentPublished)
                               throw new InvalidOperationException("Injected push-intent interruption.");
                       }))
                {
                    Assert.ThrowsAsync<InvalidOperationException>(
                        () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
                }
            }

            CorruptPushState(path + ManagedReplicaBootstrapper.MetadataSuffix, corruption);

            using var reopened = AhtolaConnection.CreateReplica(options);
            Assert.Throws<InvalidDataException>(() => reopened.Open());
            handler.PushCallCount.Should().Be(0);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task CancellationWhileWaitingForPushFlightLeavesNoIntent()
    {
        var path = NewReplicaPath("managed-replica-push-lock-cancellation");
        var image = CreateJournalDatabaseImage(path + ".source");
        var handler = new ReplicaPushHandler(
            [CreatePullResponse("revision-42", image)],
            _ => ReplicaPushHandler.SuccessfulBatchResponse(5));

        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
            await using var held = await ManagedReplicaPushLock
                .AcquireExclusiveAsync(path, CancellationToken.None)
                .ConfigureAwait(false);
            using var cancellation = new CancellationTokenSource();
            var sync = connection.SyncAsync(new AhtolaSyncOptions(), cancellation.Token);
            await Task.Delay(100);
            cancellation.Cancel();

            Assert.CatchAsync<OperationCanceledException>(() => sync);
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.PushState.Should().BeNull();
            connection.ReadManagedReplicaLocalChanges(10).Changes.Should().ContainSingle();
            handler.PushCallCount.Should().Be(0);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void CancellationAfterRemoteCommitStillPublishesAcknowledgement()
    {
        var path = NewReplicaPath("managed-replica-post-commit-cancellation");
        var image = CreateJournalDatabaseImage(path + ".source");
        var handler = new ReplicaPushHandler(
            [CreatePullResponse("revision-42", image)],
            _ => ReplicaPushHandler.SuccessfulBatchResponse(5));

        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
            using var cancellation = new CancellationTokenSource();
            using (ManagedReplicaFaultInjection.Push(boundary =>
                   {
                       if (boundary == ManagedReplicaDurableBoundary.ReplicaPushRemoteCommitObserved)
                           cancellation.Cancel();
                   }))
            {
                Assert.CatchAsync<OperationCanceledException>(
                    () => connection.SyncAsync(new AhtolaSyncOptions(), cancellation.Token));
            }

            ManagedReplicaChangeJournal.Open(path).AcknowledgedWatermark.Should().Be(2);
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.PushState.Should().BeNull();
            connection.ReadManagedReplicaLocalChanges(10).Changes.Should().BeEmpty();
            handler.PushCallCount.Should().Be(1);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task LegacyCheckpointBoundPushOutcomeRecoversThroughIndependentIntent()
    {
        var path = NewReplicaPath("managed-replica-legacy-v6-push-intent");
        try
        {
            var scenario = PreparePendingCheckpointPush(
                path,
                static (_, _) => Task.FromResult(ReplicaPushHandler.WatermarkResponse(0, 2)));
            using var connection = AhtolaConnection.CreateReplica(scenario.Options);
            connection.Open();
            using (ManagedReplicaFaultInjection.Push(boundary =>
                   {
                       if (boundary == ManagedReplicaDurableBoundary.ReplicaPushIntentPublished)
                           throw new InvalidOperationException("Injected push-intent interruption.");
                   }))
            {
                Assert.ThrowsAsync<InvalidOperationException>(
                    () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            }

            var metadata = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            var push = metadata.PushState!.Value;
            var legacy = metadata with
            {
                PushState = null,
                RevertState = metadata.RevertState!.Value with
                {
                    Phase = ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.PushOutcomeUnknown,
                    AttemptedFirstSequence = push.FirstSequence,
                    AttemptedWatermark = push.Watermark,
                },
            };
            var metadataPath = path + ManagedReplicaBootstrapper.MetadataSuffix;
            var inconsistentStagingPath = metadataPath + $".inconsistent-{Guid.NewGuid():N}.tmp";
            ManagedReplicaBootstrapper.WriteMetadata(
                inconsistentStagingPath,
                metadataPath,
                legacy with { PushState = push with { Watermark = push.Watermark + 1 } });
            File.ReadAllText(metadataPath).Should().StartWith("version=8\n");
            Assert.Throws<InvalidDataException>(() => ManagedReplicaBootstrapper.LoadMetadata(path));

            var stagingPath = metadataPath + $".legacy-{Guid.NewGuid():N}.tmp";
            ManagedReplicaBootstrapper.WriteMetadata(stagingPath, metadataPath, legacy);
            File.ReadAllText(metadataPath).Should().StartWith("version=6\n");

            var loaded = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            loaded.PushState.Should().Be(push);
            loaded.RevertState!.Value.Phase.Should()
                .Be(ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.PushOutcomeUnknown);

            var result = await connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);

            result.Statistics.CdcOperations.Should().Be(1);
            connection.ReadManagedReplicaLocalChanges(10).Changes.Should().BeEmpty();
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.PushState.Should().BeNull();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task CrossProcessAliasPullCannotEraseOrReplayACommittedPushIntent()
    {
        var realDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"push-publication-real-{Guid.NewGuid():N}");
        var aliasDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"push-publication-alias-{Guid.NewGuid():N}");
        Directory.CreateDirectory(realDirectory);
        var path = Path.Combine(realDirectory, "replica.db");
        var aliasPath = Path.Combine(aliasDirectory, "replica.db");
        var image = CreateJournalDatabaseImage(path + ".source");
        var bootstrapHandler = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", image, protocol: 2),
            CreateLogicalPullResponse("revision-42", body: []),
        ]);

        try
        {
            try
            {
                Directory.CreateSymbolicLink(aliasDirectory, realDirectory);
            }
            catch (UnauthorizedAccessException)
            {
                Assert.Ignore("Creating symbolic links is not permitted on this host.");
            }
            catch (PlatformNotSupportedException)
            {
                Assert.Ignore("Symbolic links are not supported on this host.");
            }

            using (var setup = AhtolaConnection.CreateReplica(CreateOptions(path, bootstrapHandler)))
            {
                setup.Open();
                setup.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
            }

            var requestMetadata = ManagedReplicaBootstrapper.LoadMetadata(aliasPath)!.Value;
            var requestJournal = ManagedReplicaChangeJournal.Open(aliasPath);
            var requestPending = requestJournal.ReadBatch(int.MaxValue).Changes;
            var stalePullHandler = new DelayedPullHandler(
                CreateLogicalPullResponse("revision-43", body: []));
            var stalePull = ManagedReplicaBootstrapper.CheckForUpdatesAsync(
                CreateOptions(aliasPath, stalePullHandler),
                requestMetadata,
                new AhtolaSyncOptions(),
                requestPending,
                acknowledgedLocalChanges: [],
                CancellationToken.None);
            await stalePullHandler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

            using var worker = new CrossProcessCommittedPushWorker(
                TestContext.CurrentContext.WorkDirectory,
                path);
            stalePullHandler.Release();
            await Task.Delay(TimeSpan.FromMilliseconds(250));

            stalePull.IsCompleted.Should().BeFalse(
                "pull publication through an alias must queue behind the process holding the "
                + "physical push-flight lease");
            ManagedReplicaBootstrapper.LoadMetadata(aliasPath)!.Value.PushState.Should().Be(
                new ManagedReplicaBootstrapper.ManagedReplicaPushState(0, 1, 2),
                "the stale pull must not erase the durable evidence of the remote commit");

            worker.Release();
            var rejected = Assert.ThrowsAsync<InvalidOperationException>(() => stalePull);
            rejected!.Message.Should().Contain("pending push outcome");
            var interrupted = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            interrupted.Revision.Should().Be("revision-42");
            interrupted.PushState.Should().Be(
                new ManagedReplicaBootstrapper.ManagedReplicaPushState(0, 1, 2));

            var replayCount = 0;
            var watermarkReadCount = 0;
            var recoveryHandler = new ReplicaPushHandler(
                [CreateLogicalPullResponse("revision-42", body: [])],
                request =>
                {
                    if (IsReplicaPushBatch(request))
                    {
                        replayCount++;
                        return ReplicaPushHandler.SuccessfulBatchResponse(1);
                    }

                    watermarkReadCount++;
                    return ReplicaPushHandler.WatermarkResponse(0, 1);
                });
            using var recovery = AhtolaConnection.CreateReplica(CreateOptions(path, recoveryHandler));
            recovery.Open();
            var result = await recovery.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);

            result.Statistics.CdcOperations.Should().Be(1);
            replayCount.Should().Be(0, "the remote watermark proves the protected batch already committed");
            watermarkReadCount.Should().Be(1);
            recovery.ReadManagedReplicaLocalChanges(10).Changes.Should().BeEmpty();
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.PushState.Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(aliasDirectory))
                Directory.Delete(aliasDirectory);
            DeleteReplicaFiles(path);
            if (Directory.Exists(realDirectory))
                Directory.Delete(realDirectory, recursive: true);
        }
    }

    [Test]
    [Category("ProcessWorker")]
    [NonParallelizable]
    public async Task CrossProcessCommittedPushIntentWorker()
    {
        var databasePath = Environment.GetEnvironmentVariable("AHTOLA_PUSH_INTENT_WORKER_DATABASE");
        if (string.IsNullOrEmpty(databasePath))
            return;

        var readyPath = ReadWorkerValue("AHTOLA_PUSH_INTENT_WORKER_READY");
        var releasePath = ReadWorkerValue("AHTOLA_PUSH_INTENT_WORKER_RELEASE");
        var resultPath = ReadWorkerValue("AHTOLA_PUSH_INTENT_WORKER_RESULT");
        await using var pushLease = await ManagedReplicaPushLock
            .AcquireExclusiveAsync(databasePath, CancellationToken.None)
            .ConfigureAwait(false);
        await using (await ManagedReplicaApplyLock
                         .AcquireExclusiveAsync(databasePath, CancellationToken.None)
                         .ConfigureAwait(false))
        {
            var metadata = ManagedReplicaBootstrapper.LoadMetadata(databasePath)!.Value;
            var batch = ManagedReplicaChangeJournal.Open(databasePath).ReadBatch(int.MaxValue);
            _ = ManagedReplicaRevertWal.MarkPushStarted(
                databasePath,
                metadata,
                batch,
                sourcePullGeneration: 0);
        }

        File.WriteAllText(resultPath, "committed");
        File.WriteAllText(readyPath, string.Empty);
        WaitForWorkerFile(releasePath, TimeSpan.FromSeconds(30), "The push-intent worker was not released.");
    }

    private static bool IsReplicaPushBatch(HttpRequestMessage request)
    {
        using var document = JsonDocument.Parse(
            request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        return document.RootElement.GetProperty("requests")[0].TryGetProperty("batch", out _);
    }

    private static string ReadWorkerValue(string name)
        => Environment.GetEnvironmentVariable(name)
           ?? throw new InvalidOperationException($"The push-intent worker is missing {name}.");

    private static void WaitForWorkerFile(string path, TimeSpan timeout, string message, Process? worker = null)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (worker is { HasExited: true })
                Assert.Fail($"{message} The worker exited with code {worker.ExitCode}.");
            if (stopwatch.Elapsed >= timeout)
                Assert.Fail(message);
            Thread.Sleep(25);
        }
    }

    private sealed class DelayedPullHandler(byte[] response) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource RequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(response),
            };
        }
    }

    private sealed class CrossProcessCommittedPushWorker : IDisposable
    {
        private readonly Process _worker;
        private readonly string _readyPath;
        private readonly string _releasePath;
        private readonly string _resultPath;
        private readonly StringBuilder _output = new();
        private bool _released;

        internal CrossProcessCommittedPushWorker(string workDirectory, string databasePath)
        {
            var token = Guid.NewGuid().ToString("N");
            _readyPath = Path.Combine(workDirectory, $"push-intent-ready-{token}");
            _releasePath = Path.Combine(workDirectory, $"push-intent-release-{token}");
            _resultPath = Path.Combine(workDirectory, $"push-intent-result-{token}");
            var testDirectory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            var startInfo = new ProcessStartInfo(
                Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = testDirectory.FullName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("vstest");
            startInfo.ArgumentList.Add(Path.Combine(testDirectory.FullName, "Ahtola.Tests.dll"));
            startInfo.ArgumentList.Add(
                "--TestCaseFilter:FullyQualifiedName=Ahtola.Tests.ManagedEmbeddedReplicaConnectionTests."
                + nameof(CrossProcessCommittedPushIntentWorker));
            startInfo.Environment["AHTOLA_PUSH_INTENT_WORKER_DATABASE"] = databasePath;
            startInfo.Environment["AHTOLA_PUSH_INTENT_WORKER_READY"] = _readyPath;
            startInfo.Environment["AHTOLA_PUSH_INTENT_WORKER_RELEASE"] = _releasePath;
            startInfo.Environment["AHTOLA_PUSH_INTENT_WORKER_RESULT"] = _resultPath;

            _worker = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start the committed-push worker.");
            _worker.OutputDataReceived += AppendOutput;
            _worker.ErrorDataReceived += AppendOutput;
            _worker.BeginOutputReadLine();
            _worker.BeginErrorReadLine();
            WaitForWorkerFile(
                _readyPath,
                TimeSpan.FromSeconds(60),
                "The committed-push worker did not report readiness.",
                _worker);
        }

        internal void Release()
        {
            if (_released)
                return;
            _released = true;
            File.WriteAllText(_releasePath, string.Empty);
            if (!_worker.WaitForExit(TimeSpan.FromSeconds(60)))
            {
                _worker.Kill(entireProcessTree: true);
                Assert.Fail(
                    "The committed-push worker did not exit within 60 seconds:"
                    + Environment.NewLine
                    + _output);
            }

            _worker.WaitForExit();
            _worker.ExitCode.Should().Be(0, $"worker output:{Environment.NewLine}{_output}");
            File.ReadAllText(_resultPath).Should().Be("committed");
        }

        public void Dispose()
        {
            try
            {
                Release();
            }
            finally
            {
                _worker.Dispose();
                DeleteIfExists(_readyPath);
                DeleteIfExists(_releasePath);
                DeleteIfExists(_resultPath);
            }
        }

        private void AppendOutput(object sender, DataReceivedEventArgs args)
        {
            if (args.Data is { } line)
                _output.AppendLine(line);
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static void CorruptPushState(string metadataPath, string corruption)
    {
        const string field = "push_state_base64=";
        const int payloadLength = 1 + (3 * sizeof(long));
        var text = File.ReadAllText(metadataPath);
        var valueOffset = text.IndexOf(field, StringComparison.Ordinal) + field.Length;
        valueOffset.Should().BeGreaterThanOrEqualTo(field.Length);
        var valueEnd = text.IndexOf('\n', valueOffset);
        valueEnd.Should().BeGreaterThan(valueOffset);
        var encoded = text[valueOffset..valueEnd];
        string replacement;
        if (corruption == "bad-base64")
        {
            replacement = "***";
        }
        else
        {
            var bytes = Convert.FromBase64String(encoded);
            bytes = corruption switch
            {
                "checksum" => CorruptChecksum(bytes),
                "truncated" => bytes[..^1],
                "trailing" => [.. bytes, 0],
                "version" => RewriteAndRehash(bytes, static value => value[0] = 2),
                "negative-generation" => RewriteAndRehash(
                    bytes,
                    static value => BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(1), -1)),
                "zero-first-sequence" => RewriteAndRehash(
                    bytes,
                    static value => BinaryPrimitives.WriteInt64LittleEndian(
                        value.AsSpan(1 + sizeof(long)),
                        0)),
                "invalid-watermark" => RewriteAndRehash(
                    bytes,
                    static value =>
                    {
                        var firstSequence = BinaryPrimitives.ReadInt64LittleEndian(
                            value.AsSpan(1 + sizeof(long)));
                        BinaryPrimitives.WriteInt64LittleEndian(
                            value.AsSpan(1 + (2 * sizeof(long))),
                            firstSequence);
                    }),
                _ => throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null),
            };
            replacement = Convert.ToBase64String(bytes);
        }

        File.WriteAllText(metadataPath, text[..valueOffset] + replacement + text[valueEnd..]);
        return;

        static byte[] CorruptChecksum(byte[] bytes)
        {
            bytes[^1] ^= 0xff;
            return bytes;
        }

        static byte[] RewriteAndRehash(byte[] bytes, Action<byte[]> rewrite)
        {
            rewrite(bytes);
            SHA256.HashData(bytes.AsSpan(0, payloadLength)).CopyTo(bytes.AsSpan(payloadLength));
            return bytes;
        }
    }
}
