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
    public async Task ReplacementIntentIsCapturedUnderOrdinarySqliteWriteExclusion()
    {
        var path = NewReplicaPath("replace-intent-sqlite-lock");
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
                       if (boundary != ManagedReplicaDurableBoundary.MainFileReplacementIntentPublished
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
            contender.WaitForCompletion();
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
        var rollbackInterrupted = false;

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

                       if (boundary == ManagedReplicaDurableBoundary.MainFileRollbackDisplacedPreserved
                           && rollbackWriter is null)
                       {
                           rollbackWriter = new CrossProcessReplicaRaceWorker(
                               TestContext.CurrentContext.WorkDirectory,
                               path,
                               "sqlite-write");
                           rollbackWriter.WaitForBlockedProbe();
                           rollbackInterrupted = true;
                           throw new IOException("Injected rollback handoff interruption.");
                       }

                       if (rollbackInterrupted
                           && boundary == ManagedReplicaDurableBoundary.MainFileReplacementRecoveryStarted)
                       {
                           throw new IOException("Injected automatic recovery interruption.");
                       }
                   }))
            {
                var syncException = Assert.ThrowsAsync<IOException>(
                   () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
                syncException!.Message.Should().Be("Injected automatic recovery interruption.");
            }

            rollbackWriter.Should().NotBeNull();
            File.Exists(ManagedReplicaReplacementState.GetBackupPath(path)).Should().BeTrue(
                "an interrupted rollback must retain the deterministic old-image backup");
            File.Exists(path + ManagedReplicaReplacementState.IntentSuffix).Should().BeTrue(
                "the backup must remain discoverable through durable replacement intent");
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.Revision.Should().Be("revision-42");

            connection.Dispose();
            using var recovered = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            recovered.Open();

            rollbackWriter!.ReleaseBlockedProbe();
            rollbackWriter.WaitForCompletion();
            ReadBootstrapMarker(recovered).Should().Be(42);
            recovered.Dispose();
            File.ReadAllBytes(path).Should().Equal(initialImage);
            ManagedReplicaReplacementState.HasArtifacts(path).Should().BeFalse();
        }
        finally
        {
            rollbackWriter?.Dispose();
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task ColdOpenRollbackDiscardsCommittedReplacementGenerationWal()
    {
        Assume.That(OperatingSystem.IsWindows(), Is.True);
        var path = NewReplicaPath("rollback-replacement-wal");
        var initialImage = CreateDatabaseImageWithMarker(path + ".initial", 42);
        var updatedImage = CreateDatabaseImageWithMarker(path + ".updated", 84);
        var handler = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", initialImage),
            CreatePullResponse("revision-43", updatedImage),
        ]);
        CrossProcessReplicaRaceWorker? walWriter = null;
        var publicationFailed = false;
        var rollbackRestored = false;

        try
        {
            using (var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler)))
            {
                connection.Open();
                using (ManagedReplicaFaultInjection.Push(boundary =>
                       {
                           if (boundary == ManagedReplicaDurableBoundary.MainFileReplacementPublishedBeforeLease
                               && walWriter is null)
                           {
                               walWriter = new CrossProcessReplicaRaceWorker(
                                   TestContext.CurrentContext.WorkDirectory,
                                   path,
                                   "sqlite-wal-crash");
                               walWriter.WaitForProbeState("committed");
                               walWriter.WaitForCrashCompletion();
                               File.Exists(path + "-wal").Should().BeTrue();
                           }
                           else if (boundary == ManagedReplicaDurableBoundary.IncrementalApplyDatabasePublished)
                           {
                               publicationFailed = true;
                               throw new IOException("Injected publication failure after replacement WAL commit.");
                           }
                           else if (publicationFailed
                                    && boundary == ManagedReplicaDurableBoundary.MainFileRollbackSidecarsRestored)
                           {
                               rollbackRestored = true;
                               throw new IOException("Injected crash after restoring the original database generation.");
                           }
                           else if (rollbackRestored
                                    && boundary == ManagedReplicaDurableBoundary.MainFileReplacementRecoveryStarted)
                           {
                               throw new IOException("Injected automatic recovery interruption.");
                           }
                       }))
                {
                    var syncException = Assert.ThrowsAsync<IOException>(
                        () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
                    syncException!.Message.Should().Be("Injected automatic recovery interruption.");
                }
            }

            File.Exists(path + "-wal").Should().BeTrue(
                "the committed replacement-generation WAL must survive until cold recovery reconciles it");
            File.Exists(ManagedReplicaReplacementState.GetDisplacedPath(path)).Should().BeTrue();
            ManagedReplicaReplacementState.HasArtifacts(path).Should().BeTrue();

            using var recovered = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            recovered.Open();

            ReadBootstrapMarker(recovered).Should().Be(42);
            ManagedReplicaReplacementState.HasArtifacts(path).Should().BeFalse();
        }
        finally
        {
            walWriter?.Dispose();
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task WindowsRollbackBlocksMainMutatingWritersUntilRecoveryCompletes()
    {
        Assume.That(OperatingSystem.IsWindows(), Is.True);
        var path = NewReplicaPath("rollback-main-writer-guard");
        var initialImage = CreateDatabaseImageWithMarker(path + ".initial", 42);
        var updatedImage = CreateDatabaseImageWithMarker(path + ".updated", 84);
        var handler = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", initialImage),
            CreatePullResponse("revision-43", updatedImage),
        ]);
        CrossProcessReplicaRaceWorker? deleteWriter = null;
        CrossProcessReplicaRaceWorker? checkpointWriter = null;

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
                       if (boundary == ManagedReplicaDurableBoundary.MainFileRollbackRestoredUnderLease
                           && deleteWriter is null)
                       {
                           deleteWriter = new CrossProcessReplicaRaceWorker(
                               TestContext.CurrentContext.WorkDirectory,
                               path,
                               "sqlite-delete-commit");
                           checkpointWriter = new CrossProcessReplicaRaceWorker(
                               TestContext.CurrentContext.WorkDirectory,
                               path,
                               "sqlite-wal-checkpoint-commit");
                           deleteWriter.WaitForBlockedProbe();
                           checkpointWriter.WaitForBlockedProbe();
                       }
                   }))
            {
                var exception = Assert.ThrowsAsync<IOException>(
                    () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
                exception!.Message.Should().Be("Injected replacement reacquisition failure.");
            }

            deleteWriter.Should().NotBeNull();
            checkpointWriter.Should().NotBeNull();
            ManagedReplicaReplacementState.HasArtifacts(path).Should().BeFalse(
                "rollback recovery must complete before either writer is released");

            connection.Dispose();
            deleteWriter!.ReleaseBlockedProbe();
            deleteWriter.WaitForCompletion();
            checkpointWriter!.ReleaseBlockedProbe();
            checkpointWriter.WaitForCompletion();
            using var recovered = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            recovered.Open();

            using (var command = recovered.CreateCommand())
            {
                command.CommandText = "SELECT value FROM rollback_delete_writer";
                Convert.ToInt64(command.ExecuteScalar()).Should().Be(701);
                command.CommandText = "SELECT value FROM rollback_wal_checkpoint_writer";
                Convert.ToInt64(command.ExecuteScalar()).Should().Be(702);
            }
        }
        finally
        {
            deleteWriter?.ReleaseBlockedProbe();
            checkpointWriter?.ReleaseBlockedProbe();
            deleteWriter?.Dispose();
            checkpointWriter?.Dispose();
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task ColdOpenRollbackDiscardsReplacementWalRecreatedAfterQuarantineCrash()
    {
        Assume.That(OperatingSystem.IsWindows(), Is.True);
        var path = NewReplicaPath("rollback-recreated-replacement-wal");
        var initialImage = CreateDatabaseImageWithMarker(path + ".initial", 42);
        var updatedImage = CreateDatabaseImageWithMarker(path + ".updated", 84);
        var handler = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", initialImage),
            CreatePullResponse("revision-43", updatedImage),
        ]);
        var rollbackInterrupted = false;
        CrossProcessReplicaRaceWorker? preQuarantineWriter = null;
        CrossProcessReplicaRaceWorker? walWriter = null;

        try
        {
            using (var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler)))
            {
                connection.Open();
                using (ManagedReplicaFaultInjection.Push(boundary =>
                       {
                           if (boundary == ManagedReplicaDurableBoundary.MainFileReplacementPublishedBeforeLease)
                           {
                               preQuarantineWriter = new CrossProcessReplicaRaceWorker(
                                   TestContext.CurrentContext.WorkDirectory,
                                   path,
                                   "sqlite-wal-crash");
                               preQuarantineWriter.WaitForProbeState("committed");
                               preQuarantineWriter.WaitForCrashCompletion();
                               throw new IOException("Injected replacement reacquisition failure.");
                           }
                           if (!rollbackInterrupted
                               && boundary == ManagedReplicaDurableBoundary.MainFileRollbackSidecarsQuarantined)
                           {
                               rollbackInterrupted = true;
                               throw new IOException("Injected crash after replacement sidecar quarantine.");
                           }
                           if (rollbackInterrupted
                               && boundary == ManagedReplicaDurableBoundary.MainFileReplacementRecoveryStarted)
                           {
                               throw new IOException("Injected automatic recovery interruption.");
                           }
                       }))
                {
                    var syncException = Assert.ThrowsAsync<IOException>(
                        () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
                    syncException!.Message.Should().Be("Injected automatic recovery interruption.");
                }
            }

            walWriter = new CrossProcessReplicaRaceWorker(
                TestContext.CurrentContext.WorkDirectory,
                path,
                "sqlite-wal-crash");
            walWriter.WaitForProbeState("committed");
            walWriter.WaitForCrashCompletion();
            File.Exists(path + "-wal").Should().BeTrue();
            File.Exists(path + ManagedReplicaReplacementState.ReplacementWalSuffix).Should().BeTrue();

            using var recovered = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            recovered.Open();

            ReadBootstrapMarker(recovered).Should().Be(42);
            ManagedReplicaReplacementState.HasArtifacts(path).Should().BeFalse();
        }
        finally
        {
            if (walWriter is not null)
                walWriter.WaitForCrashCompletion();
            preQuarantineWriter?.Dispose();
            walWriter?.Dispose();
            DeleteReplicaFiles(path);
        }
    }

    [TestCase(nameof(ManagedReplicaDurableBoundary.MainFileReplacementOriginalWalCaptured), 42)]
    [TestCase(nameof(ManagedReplicaDurableBoundary.MainFileReplacementIntentPublished), 42)]
    [TestCase(nameof(ManagedReplicaDurableBoundary.MainFileReplacementSourceLeaseReleased), 42)]
    [TestCase(nameof(ManagedReplicaDurableBoundary.MainFileReplacementPublishedBeforeLease), 42)]
    [TestCase(nameof(ManagedReplicaDurableBoundary.IncrementalApplyDatabasePublished), 42)]
    [TestCase(nameof(ManagedReplicaDurableBoundary.IncrementalApplyMetadataPublished), 84)]
    [TestCase(nameof(ManagedReplicaDurableBoundary.MainFileReplacementBackupRetired), 84)]
    [TestCase(nameof(ManagedReplicaDurableBoundary.MainFileReplacementIntentRetired), 84)]
    public async Task ColdOpenRecoversEveryWindowsReplacementPublicationPhase(
        string interruptedBoundaryName,
        long expectedMarker)
    {
        Assume.That(OperatingSystem.IsWindows(), Is.True);
        var interruptedBoundary = Enum.Parse<ManagedReplicaDurableBoundary>(interruptedBoundaryName);
        var path = NewReplicaPath($"replace-cold-open-{interruptedBoundary}");
        var initialImage = CreateDatabaseImageWithMarker(path + ".initial", 42);
        var updatedImage = CreateDatabaseImageWithMarker(path + ".updated", 84);
        var handler = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", initialImage),
            CreatePullResponse("revision-43", updatedImage),
        ]);
        var phaseInterrupted = false;
        var automaticRecoveryInterrupted = false;

        try
        {
            using (var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler)))
            {
                connection.Open();
                using (ManagedReplicaFaultInjection.Push(boundary =>
                       {
                           if (!phaseInterrupted && boundary == interruptedBoundary)
                           {
                               phaseInterrupted = true;
                               throw new IOException($"Interrupted at {boundary}.");
                           }
                           if (phaseInterrupted
                               && boundary == ManagedReplicaDurableBoundary.MainFileReplacementRecoveryStarted)
                           {
                               automaticRecoveryInterrupted = true;
                               throw new IOException("Interrupted automatic replacement recovery.");
                           }
                       }))
                {
                    var exception = Assert.ThrowsAsync<IOException>(
                        () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
                    exception!.Message.Should().Be(
                        automaticRecoveryInterrupted
                            ? "Interrupted automatic replacement recovery."
                            : $"Interrupted at {interruptedBoundary}.");
                }
            }

            using var recovered = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            recovered.Open();

            ReadBootstrapMarker(recovered).Should().Be(expectedMarker);
            ManagedReplicaReplacementState.HasArtifacts(path).Should().BeFalse();
            var expectedRevision = expectedMarker == 42 ? "revision-42" : "revision-43";
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.Revision.Should().Be(expectedRevision);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [TestCase(nameof(ManagedReplicaDurableBoundary.MainFileRollbackDisplacedPreserved))]
    [TestCase(nameof(ManagedReplicaDurableBoundary.MainFileRollbackSidecarsQuarantined))]
    [TestCase(nameof(ManagedReplicaDurableBoundary.MainFileRollbackRestoreStarted))]
    [TestCase(nameof(ManagedReplicaDurableBoundary.MainFileRollbackRestoredUnderLease))]
    [TestCase(nameof(ManagedReplicaDurableBoundary.MainFileRollbackDatabaseRestored))]
    [TestCase(nameof(ManagedReplicaDurableBoundary.MainFileRollbackSidecarsRestored))]
    [TestCase(nameof(ManagedReplicaDurableBoundary.MainFileRollbackIntentRetired))]
    public async Task ColdOpenRecoversEveryWindowsReplacementRollbackPhase(
        string interruptedBoundaryName)
    {
        Assume.That(OperatingSystem.IsWindows(), Is.True);
        var interruptedBoundary = Enum.Parse<ManagedReplicaDurableBoundary>(interruptedBoundaryName);
        var path = NewReplicaPath($"rollback-cold-open-{interruptedBoundary}");
        var initialImage = CreateDatabaseImageWithMarker(path + ".initial", 42);
        var updatedImage = CreateDatabaseImageWithMarker(path + ".updated", 84);
        var handler = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", initialImage),
            CreatePullResponse("revision-43", updatedImage),
        ]);
        var forwardInterrupted = false;
        var rollbackInterrupted = false;
        var automaticRecoveryInterrupted = false;

        try
        {
            using (var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler)))
            {
                connection.Open();
                using (ManagedReplicaFaultInjection.Push(boundary =>
                       {
                           if (!forwardInterrupted
                               && boundary == ManagedReplicaDurableBoundary.MainFileReplacementPublishedBeforeLease)
                           {
                               forwardInterrupted = true;
                               throw new IOException("Injected forward handoff interruption.");
                           }
                           if (forwardInterrupted
                               && !rollbackInterrupted
                               && boundary == interruptedBoundary)
                           {
                               rollbackInterrupted = true;
                               throw new IOException($"Interrupted at {boundary}.");
                           }
                           if (rollbackInterrupted
                               && boundary == ManagedReplicaDurableBoundary.MainFileReplacementRecoveryStarted)
                           {
                               automaticRecoveryInterrupted = true;
                               throw new IOException("Interrupted automatic replacement recovery.");
                           }
                       }))
                {
                    var exception = Assert.ThrowsAsync<IOException>(
                        () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
                    exception!.Message.Should().Be(
                        automaticRecoveryInterrupted
                            ? "Interrupted automatic replacement recovery."
                            : $"Interrupted at {interruptedBoundary}.");
                }
            }

            using var recovered = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            recovered.Open();

            ReadBootstrapMarker(recovered).Should().Be(42);
            recovered.Dispose();
            File.ReadAllBytes(path).Should().Equal(initialImage);
            ManagedReplicaReplacementState.HasArtifacts(path).Should().BeFalse();
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.Revision.Should().Be("revision-42");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [TestCase("sqlite-delete-commit")]
    [TestCase("sqlite-wal-checkpoint-commit")]
    public async Task ColdOpenRestoresRollbackWhoseDisplacedGenerationWasDurablyRecorded(
        string writerMode)
    {
        Assume.That(OperatingSystem.IsWindows(), Is.True);
        var path = NewReplicaPath($"rollback-recorded-displaced-{writerMode}");
        var initialImage = CreateDatabaseImageWithMarker(path + ".initial", 42);
        var updatedImage = CreateDatabaseImageWithMarker(path + ".updated", 84);
        var handler = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", initialImage),
            CreatePullResponse("revision-43", updatedImage),
        ]);

        try
        {
            await InterruptRollbackAfterPreservingMutatedReplacementAsync(
                path,
                handler,
                writerMode);

            File.ReadAllBytes(path).Should().NotEqual(updatedImage);
            File.Exists(ManagedReplicaReplacementState.GetDisplacedPath(path)).Should().BeTrue();
            File.Exists(path + ManagedReplicaReplacementState.DisplacedSha256Suffix).Should().BeTrue();

            using var recovered = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            recovered.Open();

            ReadBootstrapMarker(recovered).Should().Be(42);
            recovered.Dispose();
            File.ReadAllBytes(path).Should().Equal(initialImage);
            ManagedReplicaReplacementState.HasArtifacts(path).Should().BeFalse();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task ColdOpenRejectsMutationAfterDisplacedGenerationWasDurablyRecorded()
    {
        Assume.That(OperatingSystem.IsWindows(), Is.True);
        var path = NewReplicaPath("rollback-recorded-displaced-mutation");
        var initialImage = CreateDatabaseImageWithMarker(path + ".initial", 42);
        var updatedImage = CreateDatabaseImageWithMarker(path + ".updated", 84);
        var handler = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", initialImage),
            CreatePullResponse("revision-43", updatedImage),
        ]);

        try
        {
            await InterruptRollbackAfterPreservingMutatedReplacementAsync(
                path,
                handler,
                "sqlite-wal-checkpoint-commit");

            using (var writer = new NativeSqliteConnection(
                       $"Data Source={path};Mode=ReadWriteCreate;Pooling=False"))
            {
                writer.Open();
                using var command = writer.CreateCommand();
                command.CommandText =
                    "CREATE TABLE unrelated_displaced_writer(value INTEGER);"
                    + " INSERT INTO unrelated_displaced_writer VALUES (127);";
                command.ExecuteNonQuery();
            }

            using var recovered = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            var exception = Assert.Throws<InvalidDataException>(() => recovered.Open());
            exception!.Message.Should().Contain("unrecognized database image");
            ManagedReplicaReplacementState.HasArtifacts(path).Should().BeTrue();
            File.ReadAllBytes(ManagedReplicaReplacementState.GetBackupPath(path))
                .Should().Equal(initialImage);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task ColdOpenFailsClosedForACommitAfterInterruptedInPlaceRollback()
    {
        Assume.That(OperatingSystem.IsWindows(), Is.True);
        var path = NewReplicaPath("rollback-interrupted-external-commit");
        var initialImage = CreateDatabaseImageWithMarker(path + ".initial", 42);
        var updatedImage = CreateDatabaseImageWithMarker(path + ".updated", 84);
        var handler = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", initialImage),
            CreatePullResponse("revision-43", updatedImage),
        ]);
        var forwardInterrupted = false;
        var rollbackInterrupted = false;
        var externalWriterCommitted = false;

        try
        {
            using (var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler)))
            {
                connection.Open();
                using (ManagedReplicaFaultInjection.Push(boundary =>
                       {
                           if (!forwardInterrupted
                               && boundary == ManagedReplicaDurableBoundary.MainFileReplacementPublishedBeforeLease)
                           {
                               forwardInterrupted = true;
                               throw new IOException("Injected forward handoff interruption.");
                           }
                           if (forwardInterrupted
                               && !rollbackInterrupted
                               && boundary == ManagedReplicaDurableBoundary.MainFileRollbackRestoreStarted)
                           {
                               rollbackInterrupted = true;
                               throw new IOException("Injected interrupted in-place rollback.");
                           }
                           if (rollbackInterrupted
                               && boundary == ManagedReplicaDurableBoundary.MainFileReplacementRecoveryStarted)
                           {
                               throw new IOException("Injected automatic recovery interruption.");
                           }
                       }))
                {
                    var exception = Assert.ThrowsAsync<IOException>(
                        () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
                    exception!.Message.Should().Be("Injected automatic recovery interruption.");
                }
            }

            using var recovered = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            using (ManagedReplicaFaultInjection.Push(boundary =>
                   {
                       if (boundary
                               != ManagedReplicaDurableBoundary.MainFileReplacementRecoveryClassifiedBeforeLease
                           || externalWriterCommitted)
                       {
                           return;
                       }

                       using var writer = new NativeSqliteConnection(
                           $"Data Source={path};Mode=ReadWriteCreate;Pooling=False");
                       writer.Open();
                       using var command = writer.CreateCommand();
                       command.CommandText =
                           "CREATE TABLE external_rollback_writer(value INTEGER);"
                           + " INSERT INTO external_rollback_writer VALUES (126);";
                       command.ExecuteNonQuery();
                       externalWriterCommitted = true;
                   }))
            {
                var recoveryException = Assert.Throws<InvalidDataException>(() => recovered.Open());
                recoveryException!.Message.Should().Contain("unrecognized database image");
            }

            externalWriterCommitted.Should().BeTrue();
            ManagedReplicaReplacementState.HasArtifacts(path).Should().BeTrue();
            File.ReadAllBytes(ManagedReplicaReplacementState.GetBackupPath(path))
                .Should().Equal(initialImage);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task SameProcessRetryRecoversAnInterruptedWindowsRollbackWithoutReenteringTheMainFileLease()
    {
        Assume.That(OperatingSystem.IsWindows(), Is.True);
        var path = NewReplicaPath("rollback-same-process-retry");
        var initialImage = CreateDatabaseImageWithMarker(path + ".initial", 42);
        var updatedImage = CreateDatabaseImageWithMarker(path + ".updated", 84);
        var handler = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", initialImage),
            CreatePullResponse("revision-43", updatedImage),
            CreatePullResponse("revision-43", updatedImage),
        ]);
        var forwardInterrupted = false;
        var rollbackInterrupted = false;

        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            using (ManagedReplicaFaultInjection.Push(boundary =>
                   {
                       if (!forwardInterrupted
                           && boundary == ManagedReplicaDurableBoundary.MainFileReplacementPublishedBeforeLease)
                       {
                           forwardInterrupted = true;
                           throw new IOException("Injected forward handoff interruption.");
                       }
                       if (!rollbackInterrupted
                           && boundary == ManagedReplicaDurableBoundary.MainFileRollbackDisplacedPreserved)
                       {
                           rollbackInterrupted = true;
                           throw new IOException("Injected rollback interruption.");
                       }
                   }))
            {
                Assert.ThrowsAsync<IOException>(
                    () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            }

            var result = await connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(30));

            result.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);
            ReadBootstrapMarker(connection).Should().Be(84);
            ManagedReplicaReplacementState.HasArtifacts(path).Should().BeFalse();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task FailedPublicationReopenRecoversAPreSwapReplacementIntent()
    {
        var path = NewReplicaPath("replace-reopen-recovery");
        var initialImage = CreateDatabaseImageWithMarker(path + ".initial", 42);
        var updatedImage = CreateDatabaseImageWithMarker(path + ".updated", 84);
        var handler = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", initialImage),
            CreatePullResponse("revision-43", updatedImage),
        ]);

        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            using (ManagedReplicaFaultInjection.Push(boundary =>
                   {
                       if (boundary == ManagedReplicaDurableBoundary.MainFileReplacementIntentPublished)
                           throw new IOException("Injected pre-swap publication interruption.");
                   }))
            {
                var exception = Assert.ThrowsAsync<IOException>(
                    () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
                exception!.Message.Should().Be("Injected pre-swap publication interruption.");
            }

            ManagedReplicaReplacementState.HasArtifacts(path).Should().BeFalse(
                "failed publication reopen must recover replacement state before exposing the database");
            ReadBootstrapMarker(connection).Should().Be(42);
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE post_recovery(value INTEGER)";
            command.ExecuteNonQuery();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void ColdOpenFailsClosedForAReplacementBackupWithoutIntent()
    {
        var path = NewReplicaPath("replacement-backup-without-intent");
        var image = CreateDatabaseImageWithMarker(path + ".initial", 42);
        var handler = new PullUpdatesHandler(CreatePullResponse("revision-42", image));

        try
        {
            using (var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler)))
                connection.Open();
            File.Copy(path, ManagedReplicaReplacementState.GetBackupPath(path));

            Action reopen = () =>
            {
                using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
                connection.Open();
            };

            reopen.Should().Throw<InvalidDataException>()
                .WithMessage("*backup without its durable intent*");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void ColdOpenFailsClosedForACorruptReplacementIntent()
    {
        var path = NewReplicaPath("corrupt-replacement-intent");
        var image = CreateDatabaseImageWithMarker(path + ".initial", 42);
        var handler = new PullUpdatesHandler(CreatePullResponse("revision-42", image));

        try
        {
            using (var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler)))
                connection.Open();
            File.WriteAllText(
                path + ManagedReplicaReplacementState.IntentSuffix,
                "not-a-valid-intent");

            Action reopen = () =>
            {
                using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
                connection.Open();
            };

            reopen.Should().Throw<InvalidDataException>()
                .WithMessage("*intent is invalid or corrupt*");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ColdOpenFailsClosedForAMissingOrCorruptReplacementBackup(bool corruptBackup)
    {
        var path = NewReplicaPath(corruptBackup
            ? "corrupt-replacement-backup"
            : "missing-replacement-backup");
        var initialImage = CreateDatabaseImageWithMarker(path + ".initial", 42);
        var updatedImage = CreateDatabaseImageWithMarker(path + ".updated", 84);
        var handler = new PullUpdatesHandler(CreatePullResponse("revision-42", initialImage));
        var stagingPath = path + ".replacement.tmp";
        var backupPath = ManagedReplicaReplacementState.GetBackupPath(path);

        try
        {
            using (var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler)))
                connection.Open();
            File.WriteAllBytes(stagingPath, updatedImage);
            ManagedReplicaReplacementState.Prepare(path, stagingPath);
            File.Replace(stagingPath, path, backupPath);
            if (corruptBackup)
                File.WriteAllText(backupPath, "corrupt");
            else
                File.Delete(backupPath);

            Action reopen = () =>
            {
                using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
                connection.Open();
            };

            reopen.Should().Throw<InvalidDataException>()
                .WithMessage(corruptBackup
                    ? "*backup is missing or corrupt*"
                    : "*backup is missing*");
        }
        finally
        {
            DeleteReplicaFiles(path);
            if (File.Exists(stagingPath))
                File.Delete(stagingPath);
        }
    }

    [Test]
    public void ChangedParseableMetadataCannotRetireAReplacementBackup()
    {
        var path = NewReplicaPath("replacement-unrelated-metadata");
        var initialImage = CreateDatabaseImageWithMarker(path + ".initial", 42);
        var updatedImage = CreateDatabaseImageWithMarker(path + ".updated", 84);
        var handler = new PullUpdatesHandler(CreatePullResponse("revision-42", initialImage));
        var stagingPath = path + ".replacement";
        var metadataStagingPath = path + ".unrelated-metadata";
        IDisposable? replacementLock = null;

        try
        {
            using (var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler)))
                connection.Open();
            var metadata = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            var replacementSha256 = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(updatedImage));
            var expectedReplacementMetadata = metadata with
            {
                Revision = "revision-43",
                DatabaseSha256 = replacementSha256,
            };
            File.WriteAllBytes(stagingPath, updatedImage);
            replacementLock = ManagedReplicaApplyLock.AcquireMainFileReplacementLock(
                path,
                stagingPath,
                CancellationToken.None);
            ManagedReplicaReplacementState.Prepare(
                path,
                stagingPath,
                ManagedReplicaBootstrapper.ComputeMetadataSha256(expectedReplacementMetadata));
            ManagedReplicaApplyLock.ReplaceMainFile(
                replacementLock,
                stagingPath,
                path,
                ManagedReplicaReplacementState.GetBackupPath(path),
                static () => { });
            replacementLock!.Dispose();
            replacementLock = null;

            ManagedReplicaBootstrapper.WriteMetadata(
                metadataStagingPath,
                path + ManagedReplicaBootstrapper.MetadataSuffix,
                expectedReplacementMetadata with { Revision = "unrelated-revision" });

            Action reopen = () =>
            {
                using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
                connection.Open();
            };

            reopen.Should().Throw<InvalidDataException>()
                .WithMessage("*metadata does not match the expected published generation*");
            File.Exists(ManagedReplicaReplacementState.GetBackupPath(path)).Should().BeTrue();
            File.Exists(path + ManagedReplicaReplacementState.IntentSuffix).Should().BeTrue();
        }
        finally
        {
            replacementLock?.Dispose();
            DeleteReplicaFiles(path);
            if (File.Exists(stagingPath))
                File.Delete(stagingPath);
            if (File.Exists(metadataStagingPath))
                File.Delete(metadataStagingPath);
        }
    }

    [Test]
    public void LocalArtifactEnumerationIncludesReplacementRecoveryState()
    {
        var path = NewReplicaPath("replacement-artifact-enumeration");

        ManagedReplicaBootstrapper.GetLocalArtifactPaths(path).Should().Contain(
            ManagedReplicaReplacementState.GetArtifactPaths(path));
    }

    [Test]
    public void CaseInsensitiveFileSystemsAcceptEquivalentReplacementPathCasing()
    {
        Assume.That(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), Is.True);
        var path = NewReplicaPath("replacement-path-case");
        var image = CreateDatabaseImageWithMarker(path + ".initial", 42);
        var handler = new PullUpdatesHandler(CreatePullResponse("revision-42", image));
        var stagingPath = path + ".replacement";

        try
        {
            using (var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler)))
                connection.Open();
            File.Copy(path, stagingPath);
            ManagedReplicaReplacementState.Prepare(path, stagingPath);

            var equivalentPath = path.ToUpperInvariant();
            ManagedReplicaReplacementState.HasArtifacts(equivalentPath).Should().BeTrue();
            ManagedReplicaReplacementState.Recover(equivalentPath);

            ManagedReplicaReplacementState.HasArtifacts(path).Should().BeFalse();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void WindowsReplacementIntentSupportsLongUnicodeFilenames()
    {
        Assume.That(OperatingSystem.IsWindows(), Is.True);
        var path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"{new string('\u754c', 130)}-{Guid.NewGuid():N}.db");
        var image = CreateDatabaseImageWithMarker(path + ".initial", 42);
        var handler = new PullUpdatesHandler(CreatePullResponse("revision-42", image));
        var stagingPath = path + ".replacement";

        try
        {
            using (var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler)))
                connection.Open();
            File.Copy(path, stagingPath);
            ManagedReplicaReplacementState.Prepare(path, stagingPath);

            ManagedReplicaReplacementState.Recover(path);

            ManagedReplicaReplacementState.HasArtifacts(path).Should().BeFalse();
        }
        finally
        {
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

        if (mode == "sqlite-wal-crash")
        {
            using var connection = new NativeSqliteConnection(
                $"Data Source={databasePath};Mode=ReadWrite;Default Timeout=30;Pooling=False");
            connection.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;";
                command.ExecuteNonQuery();
            }
            using (var transaction = connection.BeginTransaction())
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE bootstrap_marker SET value = 126;";
                command.ExecuteNonQuery().Should().Be(1);
                transaction.Commit();
            }
            File.Exists(databasePath + "-wal").Should().BeTrue();
            File.WriteAllText(blockedPath, "committed");
            Process.GetCurrentProcess().Kill(entireProcessTree: false);
            Thread.Sleep(Timeout.Infinite);
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
                when (exception.SqliteErrorCode is 5 or 6 or 8)
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

        if (mode is "sqlite-delete-commit" or "sqlite-wal-checkpoint-commit")
        {
            try
            {
                CommitOrdinarySqliteMainMutation(databasePath, mode, timeoutSeconds: 1);
                File.WriteAllText(completedPath, $"{mode}-acquired-without-blocking");
                return;
            }
            catch (Microsoft.Data.Sqlite.SqliteException exception)
                when (exception.SqliteErrorCode is 5 or 6 or 8)
            {
                File.WriteAllText(blockedPath, "blocked");
            }

            WaitForWorkerFile(
                releasePath,
                TimeSpan.FromSeconds(30),
                "The main-mutating SQLite writer was not released after rollback recovery.");
            CommitOrdinarySqliteMainMutation(databasePath, mode, timeoutSeconds: 30);
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

    private static void CommitOrdinarySqliteMainMutation(
        string databasePath,
        string mode,
        int timeoutSeconds)
    {
        using var connection = new NativeSqliteConnection(
            $"Data Source={databasePath};Mode=ReadWrite;Default Timeout={timeoutSeconds};Pooling=False");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = mode == "sqlite-delete-commit"
                ? "PRAGMA journal_mode=DELETE;"
                : "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=1;";
            command.ExecuteNonQuery();
        }
        using var transaction = connection.BeginTransaction();
        using var mutation = connection.CreateCommand();
        mutation.Transaction = transaction;
        mutation.CommandText = mode == "sqlite-delete-commit"
            ? "CREATE TABLE rollback_delete_writer(value INTEGER);"
              + " INSERT INTO rollback_delete_writer VALUES (701);"
            : "CREATE TABLE rollback_wal_checkpoint_writer(value INTEGER);"
              + " INSERT INTO rollback_wal_checkpoint_writer VALUES (702);";
        mutation.ExecuteNonQuery();
        transaction.Commit();
    }

    private static async Task InterruptRollbackAfterPreservingMutatedReplacementAsync(
        string path,
        PullUpdatesHandler handler,
        string writerMode)
    {
        CrossProcessReplicaRaceWorker? writer = null;
        var replacementMutated = false;
        var rollbackInterrupted = false;

        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            using (ManagedReplicaFaultInjection.Push(boundary =>
                   {
                       if (boundary
                               == ManagedReplicaDurableBoundary.MainFileReplacementPublishedBeforeLease
                           && writer is null)
                       {
                           writer = new CrossProcessReplicaRaceWorker(
                               TestContext.CurrentContext.WorkDirectory,
                               path,
                               writerMode);
                           writer.WaitForUnblockedCompletion();
                           replacementMutated = true;
                       }
                       else if (replacementMutated
                                && !rollbackInterrupted
                                && boundary
                                    == ManagedReplicaDurableBoundary.MainFileRollbackDisplacedPreserved)
                       {
                           rollbackInterrupted = true;
                           throw new IOException("Injected crash after preserving the displaced database.");
                       }
                       else if (rollbackInterrupted
                                && boundary
                                    == ManagedReplicaDurableBoundary.MainFileReplacementRecoveryStarted)
                       {
                           throw new IOException("Injected automatic recovery interruption.");
                       }
                   }))
            {
                var exception = Assert.ThrowsAsync<IOException>(
                    () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
                exception!.Message.Should().Be("Injected automatic recovery interruption.");
            }
        }
        finally
        {
            writer?.Dispose();
        }
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
            => WaitForCompletion(_mode);

        internal void WaitForUnblockedCompletion()
            => WaitForCompletion($"{_mode}-acquired-without-blocking");

        private void WaitForCompletion(string expectedResult)
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
            File.ReadAllText(_completedPath).Should().Be(expectedResult);
        }

        internal void WaitForCrashCompletion()
        {
            if (_completed)
                return;
            _worker.WaitForExit(TimeSpan.FromSeconds(30)).Should().BeTrue(
                "the committed WAL writer must terminate without SQLite cleanup");
            _worker.WaitForExit();
            _completed = true;
            _worker.ExitCode.Should().NotBe(0);
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
