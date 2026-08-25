using System.Diagnostics;
using System.Text;
using AwesomeAssertions;

namespace Ahtola.Tests;

public sealed partial class ManagedEmbeddedReplicaConnectionTests
{
    [Test]
    public async Task MainFileReplacementKeepsTheNewGenerationBehindExistingPublicationLeases()
    {
        var path = NewReplicaPath("replace-stable-publication-lock");
        var initialImage = CreateDatabaseImageWithMarker(path + ".initial", 42);
        var updatedImage = CreateDatabaseImageWithMarker(path + ".updated", 84);
        var handler = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", initialImage),
            CreatePullResponse("revision-43", updatedImage),
        ]);
        CrossProcessReplicaRaceWorker? contender = null;

        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            using (ManagedReplicaFaultInjection.Push(boundary =>
                   {
                       if (boundary != ManagedReplicaDurableBoundary.IncrementalApplyDatabasePublished
                           || contender is not null)
                       {
                           return;
                       }

                       contender = new CrossProcessReplicaRaceWorker(
                           TestContext.CurrentContext.WorkDirectory,
                           path,
                           "publication");
                       contender.WaitForBlockedProbe();
                   }))
            {
                var result = await connection.SyncAsync(
                    new AhtolaSyncOptions(),
                    CancellationToken.None);
                result.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);
            }

            contender.Should().NotBeNull();
            contender!.WaitForCompletion();
            ReadBootstrapMarker(connection).Should().Be(84);
        }
        finally
        {
            contender?.Dispose();
            DeleteReplicaFiles(path);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task OrdinaryPullRejectsAConflictRecordedCrossProcessDuringTheNetworkWait(
        bool sameRevisionNoOp)
    {
        var path = NewReplicaPath("pull-conflict-publication-race");
        var initialImage = CreateDatabaseImageWithMarker(path + ".initial", 42);
        var updatedImage = CreateDatabaseImageWithMarker(path + ".updated", 84);

        try
        {
            using (var setup = AhtolaConnection.CreateReplica(
                       CreateOptions(
                           path,
                           new PullUpdatesHandler(CreatePullResponse("revision-42", initialImage)))))
            {
                setup.Open();
            }

            var metadata = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            var delayed = new DelayedPullHandler(
                sameRevisionNoOp
                    ? CreatePullResponse("revision-42", [], declaredPages: 1)
                    : CreatePullResponse("revision-43", updatedImage));
            var pull = ManagedReplicaBootstrapper.CheckForUpdatesAsync(
                CreateOptions(path, delayed),
                metadata,
                new AhtolaSyncOptions(),
                pendingLocalChanges: [],
                acknowledgedLocalChanges: [],
                CancellationToken.None);
            await delayed.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

            using (var writer = new CrossProcessReplicaRaceWorker(
                       TestContext.CurrentContext.WorkDirectory,
                       path,
                       "record-conflict"))
            {
                writer.WaitForCompletion();
            }

            delayed.Release();
            var pending = Assert.ThrowsAsync<AhtolaReplicaConflictPendingException>(() => pull);
            pending!.ConflictKind.Should().Be(AhtolaReplicaConflictKind.RowWrite);
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.Revision.Should().Be("revision-42");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task ConflictRebasePullRejectsAMarkerChangedCrossProcessDuringTheNetworkWait()
    {
        var path = NewReplicaPath("rebase-conflict-publication-race");
        var initialImage = CreateDatabaseImageWithMarker(path + ".initial", 42);
        var expected = new ManagedReplicaConflictState(
            AhtolaReplicaConflictKind.RowWrite,
            "initial",
            ConflictingSequence: 1,
            BatchFirstSequence: 1,
            BatchWatermark: 2,
            UnresolvedSequences: [1]);

        try
        {
            using (var setup = AhtolaConnection.CreateReplica(
                       CreateOptions(
                           path,
                           new PullUpdatesHandler(
                           [
                               CreatePullResponse("revision-42", initialImage, protocol: 2),
                               CreateLogicalPullResponse("revision-42", body: []),
                           ]))))
            {
                setup.Open();
            }

            ManagedReplicaConflictState.Write(path, expected);

            var metadata = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            var delayed = new DelayedPullHandler(
                CreateLogicalPullResponse("revision-43", body: []));
            var pull = ManagedReplicaBootstrapper.CheckForUpdatesAsync(
                CreateOptions(path, delayed),
                metadata,
                new AhtolaSyncOptions(),
                pendingLocalChanges: [],
                acknowledgedLocalChanges: [],
                expected,
                CancellationToken.None);
            await delayed.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

            using (var writer = new CrossProcessReplicaRaceWorker(
                       TestContext.CurrentContext.WorkDirectory,
                       path,
                       "change-conflict"))
            {
                writer.WaitForCompletion();
            }

            delayed.Release();
            Assert.ThrowsAsync<InvalidDataException>(() => pull);
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.Revision.Should().Be("revision-42");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task PartialImageCompletionRejectsAConflictRecordedBeforePublication()
    {
        var path = NewReplicaPath("materialization-conflict-publication-race");
        var image = CreateMultiPageDatabaseImage(path + ".source");
        var handler = new QueryBootstrapPullHandler("revision-query", image, bootstrapPages: [0u]);
        var options = CreateOptions(
            path,
            handler,
            partialBootstrap: AhtolaPartialBootstrapOptions.QueryPages(BootstrapQuery));
        CrossProcessReplicaRaceWorker? writer = null;

        try
        {
            await ManagedReplicaBootstrapper.BootstrapAsync(options, CancellationToken.None);
            var metadata = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;

            using (ManagedReplicaFaultInjection.Push(boundary =>
                   {
                       if (boundary != ManagedReplicaDurableBoundary.PartialImagePublicationLockWaiting
                           || writer is not null)
                       {
                           return;
                       }

                       writer = new CrossProcessReplicaRaceWorker(
                           TestContext.CurrentContext.WorkDirectory,
                           path,
                           "record-conflict");
                       writer.WaitForCompletion();
                   }))
            {
                var pending = Assert.ThrowsAsync<AhtolaReplicaConflictPendingException>(
                    () => ManagedReplicaBootstrapper.CompletePartialReplicaAsync(
                        options,
                        metadata,
                        allowTrackedLocalMutations: false,
                        retainedMaterializer: null,
                        CancellationToken.None));
                pending!.ConflictKind.Should().Be(AhtolaReplicaConflictKind.RowWrite);
            }

            writer.Should().NotBeNull();
            File.Exists(path + ManagedReplicaPageMaterializingFileSystem.StateSuffix).Should().BeTrue();
            var current = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            current.Revision.Should().Be(metadata.Revision);
            current.DatabaseSha256.Should().Be(metadata.DatabaseSha256);
            current.RemoteBaseSha256.Should().Be(metadata.RemoteBaseSha256);
        }
        finally
        {
            writer?.Dispose();
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    [Category("ProcessWorker")]
    [NonParallelizable]
    public async Task CrossProcessReplicaRaceWorkerEntryPoint()
    {
        var mode = Environment.GetEnvironmentVariable("AHTOLA_REPLICA_RACE_MODE");
        if (string.IsNullOrEmpty(mode))
            return;

        var databasePath = ReadWorkerValue("AHTOLA_REPLICA_RACE_DATABASE");
        var startedPath = ReadWorkerValue("AHTOLA_REPLICA_RACE_STARTED");
        var blockedPath = ReadWorkerValue("AHTOLA_REPLICA_RACE_BLOCKED");
        var completedPath = ReadWorkerValue("AHTOLA_REPLICA_RACE_COMPLETED");
        File.WriteAllText(startedPath, string.Empty);

        if (mode == "publication")
        {
            using var probeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            IAsyncDisposable? probePushLease = null;
            IAsyncDisposable? probeApplyLease = null;
            try
            {
                probePushLease = await ManagedReplicaPushLock
                    .AcquireExclusiveAsync(databasePath, probeTimeout.Token)
                    .ConfigureAwait(false);
                probeApplyLease = await ManagedReplicaApplyLock
                    .AcquireExclusiveAsync(databasePath, probeTimeout.Token)
                    .ConfigureAwait(false);
                File.WriteAllText(completedPath, "publication-acquired-without-blocking");
                return;
            }
            catch (OperationCanceledException) when (probeTimeout.IsCancellationRequested)
            {
                File.WriteAllText(blockedPath, "blocked");
            }
            finally
            {
                if (probeApplyLease is not null)
                    await probeApplyLease.DisposeAsync().ConfigureAwait(false);
                if (probePushLease is not null)
                    await probePushLease.DisposeAsync().ConfigureAwait(false);
            }
        }

        await using var pushLease = await ManagedReplicaPushLock
            .AcquireExclusiveAsync(databasePath, CancellationToken.None)
            .ConfigureAwait(false);
        await using var applyLease = await ManagedReplicaApplyLock
            .AcquireExclusiveAsync(databasePath, CancellationToken.None)
            .ConfigureAwait(false);

        if (mode is "record-conflict" or "change-conflict")
        {
            ManagedReplicaConflictState.Write(
                databasePath,
                new ManagedReplicaConflictState(
                    mode == "record-conflict"
                        ? AhtolaReplicaConflictKind.RowWrite
                        : AhtolaReplicaConflictKind.SchemaChange,
                    mode,
                    ConflictingSequence: 1,
                    BatchFirstSequence: 1,
                    BatchWatermark: 2,
                    UnresolvedSequences: [1]));
        }
        else if (mode != "publication")
        {
            throw new InvalidOperationException($"Unknown replica race worker mode '{mode}'.");
        }

        File.WriteAllText(completedPath, mode);
    }

    private sealed class CrossProcessReplicaRaceWorker : IDisposable
    {
        private readonly Process _worker;
        private readonly string _blockedPath;
        private readonly string _completedPath;
        private readonly string _mode;
        private readonly StringBuilder _output = new();
        private bool _completed;

        internal CrossProcessReplicaRaceWorker(
            string workDirectory,
            string databasePath,
            string mode)
        {
            _mode = mode;
            var token = Guid.NewGuid().ToString("N");
            var startedPath = Path.Combine(workDirectory, $"replica-race-started-{token}");
            _blockedPath = Path.Combine(workDirectory, $"replica-race-blocked-{token}");
            _completedPath = Path.Combine(workDirectory, $"replica-race-completed-{token}");
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
                + nameof(CrossProcessReplicaRaceWorkerEntryPoint));
            startInfo.Environment["AHTOLA_REPLICA_RACE_MODE"] = mode;
            startInfo.Environment["AHTOLA_REPLICA_RACE_DATABASE"] = databasePath;
            startInfo.Environment["AHTOLA_REPLICA_RACE_STARTED"] = startedPath;
            startInfo.Environment["AHTOLA_REPLICA_RACE_BLOCKED"] = _blockedPath;
            startInfo.Environment["AHTOLA_REPLICA_RACE_COMPLETED"] = _completedPath;

            _worker = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start the replica race worker.");
            _worker.OutputDataReceived += AppendOutput;
            _worker.ErrorDataReceived += AppendOutput;
            _worker.BeginOutputReadLine();
            _worker.BeginErrorReadLine();
            WaitForWorkerFile(
                startedPath,
                TimeSpan.FromSeconds(60),
                "The replica race worker did not report readiness.",
                _worker);
        }

        internal void WaitForBlockedProbe()
        {
            WaitForWorkerFile(
                _blockedPath,
                TimeSpan.FromSeconds(30),
                "The replacement-generation publication contender acquired without blocking.",
                _worker);
            File.ReadAllText(_blockedPath).Should().Be("blocked");
        }

        internal void WaitForCompletion()
        {
            if (_completed)
                return;
            _completed = true;
            if (!_worker.WaitForExit(TimeSpan.FromSeconds(60)))
            {
                _worker.Kill(entireProcessTree: true);
                Assert.Fail(
                    "The replica race worker did not exit within 60 seconds:"
                    + Environment.NewLine
                    + _output);
            }

            _worker.WaitForExit();
            _worker.ExitCode.Should().Be(0, $"worker output:{Environment.NewLine}{_output}");
            File.ReadAllText(_completedPath).Should().Be(_mode);
        }

        public void Dispose()
        {
            try
            {
                WaitForCompletion();
            }
            finally
            {
                _worker.Dispose();
            }
        }

        private void AppendOutput(object sender, DataReceivedEventArgs args)
        {
            if (args.Data is { } line)
                _output.AppendLine(line);
        }
    }
}
