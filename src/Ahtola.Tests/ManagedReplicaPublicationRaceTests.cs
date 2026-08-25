using System.Diagnostics;
using System.Text;
using AwesomeAssertions;
using NativeSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

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

    [Test]
    public async Task MainFileReplacementKeepsTheNewGenerationBehindOrdinarySqliteWriters()
    {
        var path = NewReplicaPath("replace-sqlite-publication-lock");
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
                           "sqlite-write");
                       contender.WaitForBlockedProbe();
                   }))
            {
                var result = await connection.SyncAsync(
                    new AhtolaSyncOptions(),
                    CancellationToken.None);
                result.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);
            }

            contender.Should().NotBeNull();
            contender!.ReleaseBlockedProbe();
            contender!.WaitForCompletion();
            ReadBootstrapMarker(connection).Should().Be(84);
        }
        finally
        {
            contender?.Dispose();
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task WindowsReplacementHandoffsRemainContentionSafeForOrdinarySqliteWriters()
    {
        Assume.That(OperatingSystem.IsWindows(), Is.True);
        var path = NewReplicaPath("replace-windows-handoff");
        var initialImage = CreateDatabaseImageWithMarker(path + ".initial", 42);
        var updatedImage = CreateDatabaseImageWithMarker(path + ".updated", 84);
        var handler = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", initialImage),
            CreatePullResponse("revision-43", updatedImage),
        ]);
        CrossProcessReplicaRaceWorker? sourceGapWriter = null;
        CrossProcessReplicaRaceWorker? publishedGapWriter = null;
        Task? releasePublishedWriter = null;

        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            using (ManagedReplicaFaultInjection.Push(boundary =>
                   {
                       if (boundary == ManagedReplicaDurableBoundary.MainFileReplacementSourceLeaseReleased
                           && sourceGapWriter is null)
                       {
                           sourceGapWriter = new CrossProcessReplicaRaceWorker(
                               TestContext.CurrentContext.WorkDirectory,
                               path,
                               "sqlite-write");
                           sourceGapWriter.WaitForBlockedProbe();
                       }
                       else if (boundary == ManagedReplicaDurableBoundary.MainFileReplacementPublishedBeforeLease
                                && publishedGapWriter is null)
                       {
                           publishedGapWriter = new CrossProcessReplicaRaceWorker(
                               TestContext.CurrentContext.WorkDirectory,
                               path,
                               "sqlite-hold");
                           publishedGapWriter.WaitForProbeState("acquired");
                           releasePublishedWriter = Task.Run(async () =>
                           {
                               await Task.Delay(500).ConfigureAwait(false);
                               publishedGapWriter.ReleaseBlockedProbe();
                           });
                       }
                   }))
            {
                var result = await connection.SyncAsync(
                    new AhtolaSyncOptions(),
                    CancellationToken.None);
                result.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);
            }

            await releasePublishedWriter!.WaitAsync(TimeSpan.FromSeconds(30));
            publishedGapWriter!.WaitForCompletion();
            sourceGapWriter!.ReleaseBlockedProbe();
            sourceGapWriter.WaitForCompletion();
            ReadBootstrapMarker(connection).Should().Be(84);
        }
        finally
        {
            sourceGapWriter?.Dispose();
            publishedGapWriter?.Dispose();
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task WindowsRollbackHandoffPreservesTheRecoverableBackupWhenInterrupted()
    {
        Assume.That(OperatingSystem.IsWindows(), Is.True);
        var path = NewReplicaPath("rollback-windows-handoff");
        var initialImage = CreateDatabaseImageWithMarker(path + ".initial", 42);
        var updatedImage = CreateDatabaseImageWithMarker(path + ".updated", 84);
        var handler = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", initialImage),
            CreatePullResponse("revision-43", updatedImage),
        ]);
        CrossProcessReplicaRaceWorker? rollbackWriter = null;

        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            using (ManagedReplicaFaultInjection.Push(boundary =>
                   {
                       if (boundary == ManagedReplicaDurableBoundary.MainFileReplacementPublishedBeforeLease)
                       {
                           throw new IOException("Injected replacement reacquisition failure.");
                       }

                       if (boundary == ManagedReplicaDurableBoundary.MainFileRollbackLeasesReleased)
                       {
                           rollbackWriter = new CrossProcessReplicaRaceWorker(
                               TestContext.CurrentContext.WorkDirectory,
                               path,
                               "sqlite-hold");
                           rollbackWriter.WaitForProbeState("acquired");
                           rollbackWriter.ReleaseBlockedProbe();
                           rollbackWriter.WaitForCompletion();
                           throw new IOException("Injected rollback handoff interruption.");
                       }
                   }))
            {
                var exception = Assert.ThrowsAsync<IOException>(
                    () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
                exception!.Message.Should().Be("Injected rollback handoff interruption.");
            }

            rollbackWriter.Should().NotBeNull();
            Directory.GetFiles(
                    Path.GetDirectoryName(path)!,
                    $".{Path.GetFileName(path)}.apply-*.bak")
                .Should().ContainSingle(
                    "an interrupted rollback must retain the old database image for recovery");
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.Revision.Should().Be("revision-42");
        }
        finally
        {
            rollbackWriter?.Dispose();
            foreach (var backupPath in Directory.GetFiles(
                         Path.GetDirectoryName(path)!,
                         $".{Path.GetFileName(path)}.apply-*.bak"))
            {
                File.Delete(backupPath);
            }
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task ReplacementPhysicalCarriersBlockAHardLinkAliasNoOpPullAfterPublication()
    {
        var path = NewReplicaPath("replace-physical-alias");
        var aliasPath = path + ".alias";
        var initialImage = CreateDatabaseImageWithMarker(path + ".initial", 42);
        var updatedImage = CreateDatabaseImageWithMarker(path + ".updated", 84);
        var handler = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", initialImage),
            CreatePullResponse("revision-43", updatedImage),
        ]);
        CrossProcessReplicaRaceWorker? aliasPull = null;

        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            using (ManagedReplicaFaultInjection.Push(boundary =>
                   {
                       if (boundary == ManagedReplicaDurableBoundary.IncrementalApplyStagedDatabase)
                       {
                           var directory = Path.GetDirectoryName(path)!;
                           var stagingPath = Directory.GetFiles(
                                   directory,
                                   $".{Path.GetFileName(path)}.apply-*.tmp")
                               .Single();
                           ManagedReplicaJournalAndLockCarrierRegressionTests.RequireHardLink(
                               aliasPath,
                               stagingPath,
                               verifyPhysicalIdentity: false);
                           using var stream = new FileStream(
                               stagingPath,
                               FileMode.Open,
                               FileAccess.Read,
                               FileShare.ReadWrite | FileShare.Delete);
                           var fingerprint = Convert.ToHexString(
                               System.Security.Cryptography.SHA256.HashData(stream));
                           var aliasMetadataPath =
                               aliasPath + ManagedReplicaBootstrapper.MetadataSuffix;
                           File.Copy(
                               path + ManagedReplicaBootstrapper.MetadataSuffix,
                               aliasMetadataPath);
                           var aliasMetadataStagingPath =
                               aliasMetadataPath + $".{Guid.NewGuid():N}.tmp";
                           ManagedReplicaBootstrapper.WriteMetadata(
                               aliasMetadataStagingPath,
                               aliasMetadataPath,
                               ManagedReplicaBootstrapper.LoadMetadata(path)!.Value with
                               {
                                   Revision = "revision-43",
                                   DatabaseSha256 = fingerprint,
                               });
                       }
                       else if (boundary == ManagedReplicaDurableBoundary.IncrementalApplyDatabasePublished)
                       {
                           aliasPull = new CrossProcessReplicaRaceWorker(
                               TestContext.CurrentContext.WorkDirectory,
                               aliasPath,
                               "alias-noop-pull");
                           aliasPull.WaitForBlockedProbe();
                       }
                   }))
            {
                var result = await connection.SyncAsync(
                    new AhtolaSyncOptions(),
                    CancellationToken.None);
                result.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);
            }

            aliasPull.Should().NotBeNull();
            aliasPull!.ReleaseBlockedProbe();
            aliasPull.WaitForCompletion();
            ReadBootstrapMarker(connection).Should().Be(84);
        }
        finally
        {
            aliasPull?.Dispose();
            DeleteReplicaFiles(aliasPath);
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    [NonParallelizable]
    public void RepeatedMainFileReplacementDoesNotCreateGuidStagingCarriers()
    {
        var path = NewReplicaPath("replace-carrier-growth");
        var lockDirectory = path + ".locks";
        var previousLockDirectory = Environment.GetEnvironmentVariable(
            ManagedReplicaLockCarrier.DirectoryVariable);
        File.WriteAllBytes(path, CreateDatabaseImageWithMarker(path + ".source", 42));

        try
        {
            Environment.SetEnvironmentVariable(
                ManagedReplicaLockCarrier.DirectoryVariable,
                lockDirectory);
            for (var index = 0; index < 32; index++)
            {
                var stagingPath = path + $".replacement-{Guid.NewGuid():N}.tmp";
                var backupPath = path + $".backup-{Guid.NewGuid():N}.tmp";
                File.Copy(path, stagingPath);
                using (var replacementLock = ManagedReplicaApplyLock.AcquireMainFileReplacementLock(
                           path, stagingPath, CancellationToken.None))
                {
                    ManagedReplicaApplyLock.ReplaceMainFile(
                        replacementLock,
                        stagingPath,
                        path,
                        backupPath,
                        static () => { });
                }
                File.Delete(backupPath);
            }

            var retainedFiles = Directory.Exists(lockDirectory)
                ? Directory.GetFiles(lockDirectory)
                : [];
            retainedFiles.Should().ContainSingle(
                path => Path.GetFileName(path) == "physical-carrier-registry.lock",
                "replacement generations reclaim their physical carriers and holder registrations");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                ManagedReplicaLockCarrier.DirectoryVariable,
                previousLockDirectory);
            DeleteReplicaFiles(path);
            if (Directory.Exists(lockDirectory))
                Directory.Delete(lockDirectory, recursive: true);
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
        var releasePath = ReadWorkerValue("AHTOLA_REPLICA_RACE_RELEASE");
        File.WriteAllText(startedPath, string.Empty);

        if (mode == "sqlite-hold")
        {
            using var connection = new NativeSqliteConnection(
                $"Data Source={databasePath};Mode=ReadWrite;Default Timeout=30;Pooling=False");
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE bootstrap_marker SET value = value;";
            command.ExecuteNonQuery().Should().Be(1);
            File.WriteAllText(blockedPath, "acquired");
            WaitForWorkerFile(
                releasePath,
                TimeSpan.FromSeconds(30),
                "The ordinary SQLite writer was not released from the handoff probe.");
            transaction.Rollback();
            File.WriteAllText(completedPath, mode);
            return;
        }

        if (mode == "alias-noop-pull")
        {
            var metadata = ManagedReplicaBootstrapper.LoadMetadata(databasePath)!.Value;
            using var probeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await ManagedReplicaBootstrapper.CheckForUpdatesAsync(
                        CreateOptions(
                            databasePath,
                            new PullUpdatesHandler(
                                CreatePullResponse(
                                    metadata.Revision,
                                    [],
                                    declaredPages: 1))),
                        metadata,
                        new AhtolaSyncOptions(),
                        pendingLocalChanges: [],
                        acknowledgedLocalChanges: [],
                        probeTimeout.Token)
                    .ConfigureAwait(false);
                File.WriteAllText(completedPath, "alias-noop-pull-acquired-without-blocking");
                return;
            }
            catch (OperationCanceledException) when (probeTimeout.IsCancellationRequested)
            {
                File.WriteAllText(blockedPath, "blocked");
            }

            WaitForWorkerFile(
                releasePath,
                TimeSpan.FromSeconds(30),
                "The alias no-op pull was not released after publication.");
            File.WriteAllText(completedPath, mode);
            return;
        }

        if (mode == "sqlite-write")
        {
            try
            {
                ProbeOrdinarySqliteWrite(databasePath, timeoutSeconds: 1);
                File.WriteAllText(completedPath, "sqlite-write-acquired-without-blocking");
                return;
            }

            catch (Microsoft.Data.Sqlite.SqliteException exception)
                when (exception.SqliteErrorCode is 5 or 6)
            {
                File.WriteAllText(blockedPath, "blocked");
            }

            WaitForWorkerFile(
                releasePath,
                TimeSpan.FromSeconds(30),
                "The ordinary SQLite writer was not released after publication.");
            ProbeOrdinarySqliteWrite(databasePath, timeoutSeconds: 30);
            File.WriteAllText(completedPath, mode);
            return;
        }

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

    private static void ProbeOrdinarySqliteWrite(string databasePath, int timeoutSeconds)
    {
        using var connection = new NativeSqliteConnection(
            $"Data Source={databasePath};Mode=ReadWrite;Default Timeout={timeoutSeconds};Pooling=False");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE bootstrap_marker SET value = value;";
        command.ExecuteNonQuery().Should().Be(1);
        transaction.Rollback();
    }

    private sealed class CrossProcessReplicaRaceWorker : IDisposable
    {
        private readonly Process _worker;
        private readonly string _startedPath;
        private readonly string _blockedPath;
        private readonly string _completedPath;
        private readonly string _releasePath;
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
            _startedPath = Path.Combine(workDirectory, $"replica-race-started-{token}");
            _blockedPath = Path.Combine(workDirectory, $"replica-race-blocked-{token}");
            _completedPath = Path.Combine(workDirectory, $"replica-race-completed-{token}");
            _releasePath = Path.Combine(workDirectory, $"replica-race-release-{token}");
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
            startInfo.Environment["AHTOLA_REPLICA_RACE_STARTED"] = _startedPath;
            startInfo.Environment["AHTOLA_REPLICA_RACE_BLOCKED"] = _blockedPath;
            startInfo.Environment["AHTOLA_REPLICA_RACE_COMPLETED"] = _completedPath;
            startInfo.Environment["AHTOLA_REPLICA_RACE_RELEASE"] = _releasePath;

            _worker = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start the replica race worker.");
            _worker.OutputDataReceived += AppendOutput;
            _worker.ErrorDataReceived += AppendOutput;
            _worker.BeginOutputReadLine();
            _worker.BeginErrorReadLine();
            WaitForWorkerFile(
                _startedPath,
                TimeSpan.FromSeconds(60),
                "The replica race worker did not report readiness.",
                _worker);
        }

        internal void WaitForBlockedProbe()
            => WaitForProbeState("blocked");

        internal void WaitForProbeState(string expected)
        {
            WaitForWorkerFile(
                _blockedPath,
                TimeSpan.FromSeconds(30),
                "The replacement-generation publication contender acquired without blocking.",
                _worker);
            File.ReadAllText(_blockedPath).Should().Be(expected);
        }

        internal void ReleaseBlockedProbe()
            => File.WriteAllText(_releasePath, string.Empty);

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
                File.Delete(_startedPath);
                File.Delete(_blockedPath);
                File.Delete(_completedPath);
                File.Delete(_releasePath);
            }
        }

        private void AppendOutput(object sender, DataReceivedEventArgs args)
        {
            if (args.Data is { } line)
                _output.AppendLine(line);
        }
    }
}
