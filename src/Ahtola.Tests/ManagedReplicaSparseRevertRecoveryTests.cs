using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;

namespace Ahtola.Tests;

public sealed class ManagedReplicaSparseRevertRecoveryTests
{
    [Test]
    public void SparseCommittedSegmentSurvivesReopenAndRestoresBothProtectedImages()
    {
        var path = NewPath();
        try
        {
            var (original, committed, metadata) = CreateProtectedImages(path);
            SeedMetadata(path, metadata);

            _ = ManagedReplicaRevertWal.PublishProtectedSnapshots(
                path, metadata, original, committed, CancellationToken.None);

            var pending = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            pending.RevertState!.Value.FormatVersion.Should().Be(5);
            var firstGenerationBytes = new FileInfo(path + ManagedReplicaRevertWal.Suffix).Length;
            pending.RevertState.Value.CommittedRevertWalFrameCount.Should()
                .BeLessThan(pending.RevertState.Value.CommittedDatabaseSizeInPages);
            File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(committed));

            var reopened = ManagedReplicaRevertWal.PrepareSynchronization(path, pending);
            reopened = ManagedReplicaRevertWal.CompletePreparedCheckpoint(path, reopened);
            reopened.RevertState.Should().BeNull();
            File.Exists(path + ManagedReplicaRevertWal.Suffix).Should().BeFalse();
            File.Exists(path + ManagedReplicaRevertWal.HistorySuffix).Should().BeTrue();
            reopened.HistorySha256.Should().NotBeNull();
            File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(committed));

            var next = CreateNextImage(path);
            _ = ManagedReplicaRevertWal.PublishProtectedSnapshots(
                path, reopened, committed, next, CancellationToken.None);
            var second = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            second.RevertState!.Value.FormatVersion.Should().Be(6);
            second.RevertState.Value.ParentSha256.Should().Be(reopened.HistorySha256);
            second.RevertState.Value.OriginalRevertWalFrameCount.Should()
                .BeLessThan(second.RevertState.Value.OriginalDatabaseSizeInPages);
            new FileInfo(path + ManagedReplicaRevertWal.Suffix).Length
                .Should().BeLessThan(firstGenerationBytes / 2);
            File.Exists(path + ManagedReplicaRevertWal.HistorySuffix).Should().BeTrue();
            ManagedReplicaRevertWal.RestorePendingCheckpoint(path, second);
            File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(committed));
            var restored = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            restored.HistorySha256.Should().Be(reopened.HistorySha256);
            ManagedReplicaRevertWal.EnsureSynchronizationReady(path, restored);
        }
        finally
        {
            Delete(path);
        }
    }

    [Test]
    public void InterruptedSparseIntentReopensAtCommittedImageThenRestoresOriginal()
    {
        var path = NewPath();
        try
        {
            var (original, committed, metadata) = CreateProtectedImages(path);
            SeedMetadata(path, metadata);
            using (ManagedReplicaFaultInjection.Push(boundary =>
                   {
                       if (boundary == ManagedReplicaDurableBoundary.RevertRemoteApplyIntentPublished)
                           throw new InvalidOperationException("Interrupted after sparse intent.");
                   }))
            {
                Assert.Throws<InvalidOperationException>(() =>
                    ManagedReplicaRevertWal.PublishProtectedSnapshots(
                        path, metadata, original, committed, CancellationToken.None));
            }

            File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(original));
            var pending = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            pending.RevertState!.Value.FormatVersion.Should().Be(5);
            var recovered = ManagedReplicaRevertWal.PrepareSynchronization(path, pending);
            File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(committed));
            ManagedReplicaRevertWal.RestorePendingCheckpoint(path, recovered);
            File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(original));
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.RevertState.Should().BeNull();
            File.Exists(path + ManagedReplicaRevertWal.Suffix).Should().BeFalse();
        }
        finally
        {
            Delete(path);
        }
    }

    [Test]
    public void OrphanedSparseWalWithoutPublishedMetadataCannotReplaceTheDatabase()
    {
        var path = NewPath();
        try
        {
            var (original, committed, metadata) = CreateProtectedImages(path);
            SeedMetadata(path, metadata);
            using (ManagedReplicaFaultInjection.Push(boundary =>
                   {
                       if (boundary == ManagedReplicaDurableBoundary.RevertWalPublished)
                           throw new InvalidOperationException("Interrupted before intent.");
                   }))
            {
                Assert.Throws<InvalidOperationException>(() =>
                    ManagedReplicaRevertWal.PublishProtectedSnapshots(
                        path, metadata, original, committed, CancellationToken.None));
            }

            File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(original));
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.RevertState.Should().BeNull();
            ManagedReplicaRevertWal.EnsureSynchronizationReady(path, metadata);
            File.Exists(path + ManagedReplicaRevertWal.Suffix).Should().BeFalse();
            File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(original));
        }
        finally
        {
            Delete(path);
        }
    }

    [Test]
    public void InterruptedCommittedPublicationKeepsSparseRecoveryMetadataAndRestoresOnReopen()
    {
        var path = NewPath();
        try
        {
            var (original, committed, metadata) = CreateProtectedImages(path);
            SeedMetadata(path, metadata);
            using (ManagedReplicaFaultInjection.Push(boundary =>
                   {
                       if (boundary == ManagedReplicaDurableBoundary.RevertCommittedRestoreDatabasePublished)
                           throw new InvalidOperationException("Interrupted committed publication.");
                   }))
            {
                Assert.Throws<InvalidOperationException>(() =>
                    ManagedReplicaRevertWal.PublishProtectedSnapshots(
                        path, metadata, original, committed, CancellationToken.None));
            }

            var pending = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            pending.RevertState!.Value.FormatVersion.Should().Be(5);
            var reopened = ManagedReplicaRevertWal.PrepareSynchronization(path, pending);
            File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(committed));
            ManagedReplicaRevertWal.RestorePendingCheckpoint(path, reopened);
            File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(original));
        }
        finally
        {
            Delete(path);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SparseSegmentReconstructsResizedSnapshotsWithoutBorrowingUncommittedBytes(bool shrink)
    {
        var path = NewPath();
        try
        {
            CreateDatabase(path,
                "CREATE TABLE items(value TEXT);"
                + "CREATE TABLE untouched(value TEXT);"
                + string.Concat(Enumerable.Range(1, 12)
                    .Select(i => $"INSERT INTO untouched VALUES ('{new string((char)('a' + i), 1200)}');")));
            var small = path + ".small";
            File.Copy(path, small);
            using (var connection = new AhtolaConnection($"Data Source={path};Local Provider=Managed"))
            {
                connection.Open();
                for (var i = 0; i < 12; i++)
                    connection.ExecuteNonQuery(
                        $"INSERT INTO items VALUES ('{new string((char)('a' + i), 1200)}');");
            }
            var large = path + ".large";
            File.Copy(path, large);
            new FileInfo(large).Length.Should().BeGreaterThan(new FileInfo(small).Length);
            var original = shrink ? large : small;
            var committed = shrink ? small : large;
            File.Copy(original, path, overwrite: true);
            var metadata = Metadata(original);
            SeedMetadata(path, metadata);

            _ = ManagedReplicaRevertWal.PublishProtectedSnapshots(
                path, metadata, original, committed, CancellationToken.None);
            var pending = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            pending.RevertState!.Value.FormatVersion.Should().Be(5);
            File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(committed));
            ManagedReplicaRevertWal.RestorePendingCheckpoint(path, pending);
            File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(original));
        }
        finally
        {
            Delete(path);
        }
    }

    [Test]
    public void SparseRecoveryRejectsAValidlyEncodedStateWithAnUnreconstructableCommittedHash()
    {
        var path = NewPath();
        try
        {
            var (original, committed, metadata) = CreateProtectedImages(path);
            SeedMetadata(path, metadata);
            _ = ManagedReplicaRevertWal.PublishProtectedSnapshots(
                path, metadata, original, committed, CancellationToken.None);

            var pending = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            pending.RevertState!.Value.FormatVersion.Should().Be(5);
            var corrupt = pending with
            {
                RevertState = pending.RevertState.Value with
                {
                    CommittedDatabaseSha256 = new string('0', 64),
                },
            };
            ManagedReplicaBootstrapper.WriteMetadata(
                path + ".meta-stage",
                path + ManagedReplicaBootstrapper.MetadataSuffix,
                corrupt);

            var reopened = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            Assert.Throws<InvalidDataException>(() =>
                ManagedReplicaRevertWal.PrepareSynchronization(path, reopened));
            File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(committed));
            File.Exists(path + ManagedReplicaRevertWal.Suffix).Should().BeTrue();
        }
        finally
        {
            Delete(path);
        }
    }

    [Test]
    public void IdenticalProtectedImagesStillWriteACommittedSparseBoundary()
    {
        var path = NewPath();
        try
        {
            var (original, _, metadata) = CreateProtectedImages(path);
            SeedMetadata(path, metadata);
            _ = ManagedReplicaRevertWal.PublishProtectedSnapshots(
                path, metadata, original, original, CancellationToken.None);

            var pending = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            pending.RevertState!.Value.FormatVersion.Should().Be(5);
            pending.RevertState.Value.CommittedRevertWalFrameCount.Should().Be(1);
            var recovered = ManagedReplicaRevertWal.PrepareSynchronization(path, pending);
            recovered = ManagedReplicaRevertWal.CompletePreparedCheckpoint(path, recovered);
            File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(original));
            recovered.RevertState.Should().BeNull();
        }
        finally
        {
            Delete(path);
        }
    }

    [Test]
    public void FullV4SegmentStillDecodesAndRecoversAfterInterruption()
    {
        var path = NewPath();
        try
        {
            CreateDatabase(path, "PRAGMA user_version=7;");
            var original = path + ".original";
            var committed = path + ".committed";
            File.Copy(path, original);
            using (var connection = new AhtolaConnection($"Data Source={path};Local Provider=Managed"))
            {
                connection.Open();
                connection.ExecuteNonQuery("PRAGMA user_version=8;");
            }

            File.Copy(path, committed);
            new FileInfo(original).Length.Should().Be(4096);
            var metadata = Metadata(original);
            File.Copy(original, path, overwrite: true);
            SeedMetadata(path, metadata);
            using (ManagedReplicaFaultInjection.Push(boundary =>
                   {
                       if (boundary == ManagedReplicaDurableBoundary.RevertRemoteApplyIntentPublished)
                           throw new InvalidOperationException("Interrupted v4 capture.");
                   }))
            {
                Assert.Throws<InvalidOperationException>(() =>
                    ManagedReplicaRevertWal.PublishProtectedSnapshots(
                        path, metadata, original, committed, CancellationToken.None));
            }

            var pending = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            pending.RevertState!.Value.FormatVersion.Should().Be(4);
            pending.RevertState.Value.CommittedRevertWalFrameCount.Should()
                .Be(pending.RevertState.Value.CommittedDatabaseSizeInPages);
            ManagedReplicaBootstrapper.WriteMetadata(
                path + ".meta-stage",
                path + ManagedReplicaBootstrapper.MetadataSuffix,
                pending with { HistorySha256 = null });
            pending = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            pending.HistorySha256.Should().BeNull();
            var recovered = ManagedReplicaRevertWal.PrepareSynchronization(path, pending);
            File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(committed));
            ManagedReplicaRevertWal.RestorePendingCheckpoint(path, recovered);
            File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(original));
        }
        finally
        {
            Delete(path);
        }
    }

    [TestCase((int)ManagedReplicaDurableBoundary.RevertWalStaged)]
    [TestCase((int)ManagedReplicaDurableBoundary.RevertWalPublished)]
    [TestCase((int)ManagedReplicaDurableBoundary.RevertRemoteApplyIntentPublished)]
    [TestCase((int)ManagedReplicaDurableBoundary.RevertCommittedRestoreStagedDatabase)]
    [TestCase((int)ManagedReplicaDurableBoundary.RevertCommittedRestoreDatabasePublished)]
    [TestCase((int)ManagedReplicaDurableBoundary.RevertCommittedReadyMetadataPublished)]
    public void SecondGenerationHistorySurvivesEveryPublicationBoundary(int boundary)
    {
        var path = NewPath();
        try
        {
            var (original, committed, metadata) = CreateProtectedImages(path);
            SeedMetadata(path, metadata);
            var first = CompleteFirstGeneration(path, metadata, original, committed);
            var next = CreateNextImage(path);
            using (ManagedReplicaFaultInjection.Push(hit =>
                   {
                       if ((int)hit == boundary)
                           throw new InvalidOperationException("Simulated process exit.");
                   }))
            {
                Assert.Throws<InvalidOperationException>(() =>
                    ManagedReplicaRevertWal.PublishProtectedSnapshots(
                        path, first, committed, next, CancellationToken.None));
            }

            var reopened = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            reopened.HistorySha256.Should().Be(first.HistorySha256);
            if (reopened.RevertState is null)
            {
                ManagedReplicaRevertWal.EnsureSynchronizationReady(path, reopened);
                File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(committed));
            }
            else
            {
                reopened.RevertState.Value.FormatVersion.Should().Be(6);
                reopened = ManagedReplicaRevertWal.PrepareSynchronization(path, reopened);
                File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(next));
                ManagedReplicaRevertWal.RestorePendingCheckpoint(path, reopened);
                File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(committed));
            }
            File.Exists(path + ManagedReplicaRevertWal.HistorySuffix).Should().BeTrue();
        }
        finally
        {
            Delete(path);
        }
    }

    [Test]
    public void MissingOrCorruptHistoryFailsBeforePublishingAnotherCheckpoint()
    {
        var path = NewPath();
        try
        {
            var (original, committed, metadata) = CreateProtectedImages(path);
            SeedMetadata(path, metadata);
            var first = CompleteFirstGeneration(path, metadata, original, committed);
            var next = CreateNextImage(path);
            var historyPath = path + ManagedReplicaRevertWal.HistorySuffix;
            var historyBytes = File.ReadAllBytes(historyPath);
            historyBytes[^1] ^= 1;
            File.WriteAllBytes(historyPath, historyBytes);
            Assert.Throws<InvalidDataException>(() =>
                ManagedReplicaRevertWal.PublishProtectedSnapshots(
                    path, first, committed, next, CancellationToken.None));
            File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(committed));
            File.Exists(path + ManagedReplicaRevertWal.Suffix).Should().BeFalse();
            File.Delete(historyPath);
            Assert.Throws<InvalidDataException>(() =>
                ManagedReplicaRevertWal.PrepareSynchronization(
                    path, ManagedReplicaBootstrapper.LoadMetadata(path)!.Value));
        }
        finally
        {
            Delete(path);
        }
    }

    [TestCase((int)ManagedReplicaDurableBoundary.RevertHistoryStaged)]
    [TestCase((int)ManagedReplicaDurableBoundary.RevertHistoryPublished)]
    public void InterruptedFirstHistoryPublicationLeavesNoUntrackedParent(int boundary)
    {
        var path = NewPath();
        try
        {
            var (original, committed, metadata) = CreateProtectedImages(path);
            SeedMetadata(path, metadata);
            using (ManagedReplicaFaultInjection.Push(hit =>
                   {
                       if ((int)hit == boundary)
                           throw new InvalidOperationException("Interrupted history publication.");
                   }))
            {
                Assert.Throws<InvalidOperationException>(() =>
                    ManagedReplicaRevertWal.PublishProtectedSnapshots(
                        path, metadata, original, committed, CancellationToken.None));
            }

            var old = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            old.HistorySha256.Should().BeNull();
            ManagedReplicaRevertWal.EnsureSynchronizationReady(path, old);
            File.Exists(path + ManagedReplicaRevertWal.HistorySuffix).Should().BeFalse();
            File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(original));
            _ = ManagedReplicaRevertWal.PublishProtectedSnapshots(
                path, old, original, committed, CancellationToken.None);
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.HistorySha256.Should().NotBeNull();
        }
        finally
        {
            Delete(path);
        }
    }

    [Test]
    public void CancelledSecondGenerationPreservesParentUntilRecovery()
    {
        var path = NewPath();
        try
        {
            var (original, committed, metadata) = CreateProtectedImages(path);
            SeedMetadata(path, metadata);
            var first = CompleteFirstGeneration(path, metadata, original, committed);
            var next = CreateNextImage(path);
            using var cancellation = new CancellationTokenSource();
            using (ManagedReplicaFaultInjection.Push(hit =>
                   {
                       if (hit == ManagedReplicaDurableBoundary.RevertRemoteApplyIntentPublished)
                           cancellation.Cancel();
                   }))
            {
                Assert.Throws<OperationCanceledException>(() =>
                    ManagedReplicaRevertWal.PublishProtectedSnapshots(
                        path, first, committed, next, cancellation.Token));
            }
            var pending = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            pending.HistorySha256.Should().Be(first.HistorySha256);
            pending.RevertState!.Value.FormatVersion.Should().Be(6);
            pending = ManagedReplicaRevertWal.PrepareSynchronization(path, pending);
            ManagedReplicaRevertWal.RestorePendingCheckpoint(path, pending);
            File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(committed));
            File.Exists(path + ManagedReplicaRevertWal.HistorySuffix).Should().BeTrue();
        }
        finally
        {
            Delete(path);
        }
    }

    [Test]
    public void ReopenedSecondGenerationRestoresInAnotherProcess()
    {
        var path = NewPath();
        try
        {
            var (original, committed, metadata) = CreateProtectedImages(path);
            SeedMetadata(path, metadata);
            var first = CompleteFirstGeneration(path, metadata, original, committed);
            var next = CreateNextImage(path);
            _ = ManagedReplicaRevertWal.PublishProtectedSnapshots(
                path, first, committed, next, CancellationToken.None);

            var testDirectory = TestContext.CurrentContext.TestDirectory;
            var start = new ProcessStartInfo(
                Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = testDirectory,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("vstest");
            start.ArgumentList.Add(Path.Combine(testDirectory, "Ahtola.Tests.dll"));
            start.ArgumentList.Add(
                "--TestCaseFilter:FullyQualifiedName=Ahtola.Tests.ManagedReplicaSparseRevertRecoveryTests."
                + nameof(ReopenedSecondGenerationWorker));
            start.Environment["AHTOLA_HISTORY_RECOVERY_PATH"] = path;
            using var worker = Process.Start(start)
                ?? throw new InvalidOperationException("Failed to start history recovery worker.");
            if (!worker.WaitForExit(TimeSpan.FromSeconds(60)))
            {
                worker.Kill(entireProcessTree: true);
                Assert.Fail("History recovery worker timed out.");
            }
            worker.ExitCode.Should().Be(0);
            File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(committed));
            var recovered = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            recovered.RevertState.Should().BeNull();
            recovered.HistorySha256.Should().Be(first.HistorySha256);
        }
        finally
        {
            Delete(path);
        }
    }

    [Test]
    public void ReopenedSecondGenerationWorker()
    {
        var path = Environment.GetEnvironmentVariable("AHTOLA_HISTORY_RECOVERY_PATH");
        if (path is null)
            return;
        var pending = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
        pending.RevertState!.Value.FormatVersion.Should().Be(6);
        pending = ManagedReplicaRevertWal.PrepareSynchronization(path, pending);
        ManagedReplicaRevertWal.RestorePendingCheckpoint(path, pending);
    }

    [Test]
    public void RetainedRootSupportsThreeGenerationsAndStaysPinnedThroughPushIntent()
    {
        var path = NewPath();
        try
        {
            var (original, committed, metadata) = CreateProtectedImages(path);
            SeedMetadata(path, metadata);
            _ = ManagedReplicaRevertWal.PublishProtectedSnapshots(
                path, metadata, original, committed, CancellationToken.None);
            var firstBytes = new FileInfo(path + ManagedReplicaRevertWal.Suffix).Length;
            var first = ManagedReplicaRevertWal.CompletePreparedCheckpoint(
                path, ManagedReplicaBootstrapper.LoadMetadata(path)!.Value);
            var next = CreateNextImage(path);
            _ = ManagedReplicaRevertWal.PublishProtectedSnapshots(
                path, first, committed, next, CancellationToken.None);
            var second = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            second.RevertState!.Value.FormatVersion.Should().Be(6);
            new FileInfo(path + ManagedReplicaRevertWal.Suffix).Length
                .Should().BeLessThan(firstBytes / 2);
            second = ManagedReplicaRevertWal.CompletePreparedCheckpoint(path, second);

            var third = path + ".third";
            File.Copy(path, third);
            using (var connection = new AhtolaConnection($"Data Source={third};Local Provider=Managed"))
            {
                connection.Open();
                connection.ExecuteNonQuery("UPDATE items SET value='third' WHERE id=1;");
            }
            _ = ManagedReplicaRevertWal.PublishProtectedSnapshots(
                path, second, next, third, CancellationToken.None);
            var pending = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            pending.RevertState!.Value.FormatVersion.Should().Be(6);
            pending.RevertState.Value.ParentSha256.Should().Be(first.HistorySha256);
            new FileInfo(path + ManagedReplicaRevertWal.Suffix).Length
                .Should().BeLessThan(firstBytes / 2);
            var batch = new ReplicaLocalChangeBatch(
                1, 2, [ReplicaLocalChange.Schema("CREATE TABLE pending(value TEXT)")]);
            pending = ManagedReplicaRevertWal.MarkPushStarted(path, pending, batch, 0);
            var reopened = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            reopened.PushState.Should().NotBeNull();
            reopened.HistorySha256.Should().Be(first.HistorySha256);
            reopened = ManagedReplicaRevertWal.PrepareSynchronization(path, reopened);
            ManagedReplicaRevertWal.RestorePendingCheckpoint(path, reopened);
            var restored = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            restored.PushState.Should().NotBeNull();
            restored.HistorySha256.Should().Be(first.HistorySha256);
            File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(next));
            restored = ManagedReplicaRevertWal.ClearPushIntent(path, restored);
            ManagedReplicaRevertWal.EnsureSynchronizationReady(path, restored);
            File.Exists(path + ManagedReplicaRevertWal.HistorySuffix).Should().BeTrue();
        }
        finally
        {
            Delete(path);
        }
    }

    [Test]
    public void MismatchedV6ParentReferenceFailsClosedWithoutChangingDatabase()
    {
        var path = NewPath();
        try
        {
            var (original, committed, metadata) = CreateProtectedImages(path);
            SeedMetadata(path, metadata);
            var first = CompleteFirstGeneration(path, metadata, original, committed);
            var next = CreateNextImage(path);
            _ = ManagedReplicaRevertWal.PublishProtectedSnapshots(
                path, first, committed, next, CancellationToken.None);
            var pending = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            pending.RevertState!.Value.FormatVersion.Should().Be(6);
            ManagedReplicaBootstrapper.WriteMetadata(
                path + ".meta-stage",
                path + ManagedReplicaBootstrapper.MetadataSuffix,
                pending with
                {
                    RevertState = pending.RevertState.Value with
                    {
                        ParentSha256 = new string('0', 64),
                    },
                });
            var corrupt = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            Assert.Throws<InvalidDataException>(() =>
                ManagedReplicaRevertWal.PrepareSynchronization(path, corrupt));
            File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(next));
            File.Exists(path + ManagedReplicaRevertWal.Suffix).Should().BeTrue();
        }
        finally
        {
            Delete(path);
        }
    }

    [Test]
    public void ExistingV5RevertStateWithoutHistoryStillRecovers()
    {
        var path = NewPath();
        try
        {
            var (original, committed, metadata) = CreateProtectedImages(path);
            SeedMetadata(path, metadata);
            _ = ManagedReplicaRevertWal.PublishProtectedSnapshots(
                path, metadata, original, committed, CancellationToken.None);
            var pending = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            pending.RevertState!.Value.FormatVersion.Should().Be(5);
            ManagedReplicaBootstrapper.WriteMetadata(
                path + ".meta-stage",
                path + ManagedReplicaBootstrapper.MetadataSuffix,
                pending with { HistorySha256 = null });
            var legacy = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            legacy.HistorySha256.Should().BeNull();
            legacy = ManagedReplicaRevertWal.PrepareSynchronization(path, legacy);
            File.Exists(path + ManagedReplicaRevertWal.HistorySuffix).Should().BeFalse();
            ManagedReplicaRevertWal.RestorePendingCheckpoint(path, legacy);
            File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(original));
        }
        finally
        {
            Delete(path);
        }
    }

    [Test]
    public void PagerCheckpointReusesVerifiedHistoryAcrossWalGenerations()
    {
        var path = NewPath();
        try
        {
            var (original, committed, metadata) = CreateProtectedImages(path);
            SeedMetadata(path, metadata);
            StageWalImage(path, committed);
            using (var pager = Ahtola.Core.Storage.SqlitePager.Open(
                       Ahtola.Core.Storage.PhysicalFileSystem.Instance, path, path + "-wal"))
            {
                ManagedReplicaRevertWal.CaptureAndCheckpoint(
                    path, metadata, pager, CancellationToken.None)
                    .RetainedCommittedFrameCount.Should().Be(0);
            }
            var firstPending = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            firstPending.RevertState!.Value.FormatVersion.Should().Be(4);
            var firstBytes = new FileInfo(path + ManagedReplicaRevertWal.Suffix).Length;
            var first = ManagedReplicaRevertWal.PrepareSynchronization(path, firstPending);
            first = ManagedReplicaRevertWal.CompletePreparedCheckpoint(path, first);
            File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(committed));

            var next = CreateNextImage(path);
            StageWalImage(path, next);
            using (var pager = Ahtola.Core.Storage.SqlitePager.Open(
                       Ahtola.Core.Storage.PhysicalFileSystem.Instance, path, path + "-wal"))
            {
                ManagedReplicaRevertWal.CaptureAndCheckpoint(
                    path, first, pager, CancellationToken.None)
                    .RetainedCommittedFrameCount.Should().Be(0);
            }
            var second = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            second.RevertState!.Value.FormatVersion.Should().Be(6);
            second.RevertState.Value.ParentSha256.Should().Be(first.HistorySha256);
            new FileInfo(path + ManagedReplicaRevertWal.Suffix).Length
                .Should().BeLessThan(firstBytes / 2);
            second = ManagedReplicaRevertWal.PrepareSynchronization(path, second);
            File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(next));
            ManagedReplicaRevertWal.RestorePendingCheckpoint(path, second);
            File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(committed));
            File.Exists(path + ManagedReplicaRevertWal.HistorySuffix).Should().BeTrue();
        }
        finally
        {
            Delete(path);
        }
    }

    [Test]
    public void HistoryIsRetiredOnlyAfterItsMetadataReferenceIsCleared()
    {
        var path = NewPath();
        try
        {
            var (original, committed, metadata) = CreateProtectedImages(path);
            SeedMetadata(path, metadata);
            var first = CompleteFirstGeneration(path, metadata, original, committed);
            var historyPath = path + ManagedReplicaRevertWal.HistorySuffix;
            ManagedReplicaRevertWal.EnsureSynchronizationReady(path, first);
            File.Exists(historyPath).Should().BeTrue();

            ManagedReplicaBootstrapper.WriteMetadata(
                path + ".meta-stage",
                path + ManagedReplicaBootstrapper.MetadataSuffix,
                first with { HistorySha256 = null });
            File.Exists(historyPath).Should().BeTrue();
            var released = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            released.HistorySha256.Should().BeNull();
            ManagedReplicaRevertWal.EnsureSynchronizationReady(path, released);
            File.Exists(historyPath).Should().BeFalse();
        }
        finally
        {
            Delete(path);
        }
    }

    private static void StageWalImage(string path, string committedPath)
    {
        var baseImage = File.ReadAllBytes(path);
        var committedImage = File.ReadAllBytes(committedPath);
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            if (File.Exists(path + suffix))
                File.Delete(path + suffix);
        }
        using var pager = Ahtola.Core.Storage.SqlitePager.Open(
            Ahtola.Core.Storage.PhysicalFileSystem.Instance, path, path + "-wal");
        using var transaction = pager.BeginTransaction(
            targetDatabaseSizeInPages: checked((uint)(committedImage.Length / 4096)));
        for (var offset = 0; offset < committedImage.Length; offset += 4096)
        {
            var page = committedImage.AsSpan(offset, 4096);
            var prior = offset < baseImage.Length
                ? baseImage.AsSpan(offset, 4096)
                : ReadOnlySpan<byte>.Empty;
            if (!page.SequenceEqual(prior))
                transaction.WritePage(checked((uint)(offset / 4096 + 1)), page);
        }
        transaction.Commit();
    }

    private static ManagedReplicaBootstrapper.ManagedReplicaMetadata CompleteFirstGeneration(
        string path,
        ManagedReplicaBootstrapper.ManagedReplicaMetadata metadata,
        string original,
        string committed)
    {
        _ = ManagedReplicaRevertWal.PublishProtectedSnapshots(
            path, metadata, original, committed, CancellationToken.None);
        var pending = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
        return ManagedReplicaRevertWal.CompletePreparedCheckpoint(path, pending);
    }

    private static (string Original, string Committed, ManagedReplicaBootstrapper.ManagedReplicaMetadata Metadata)
        CreateProtectedImages(string path)
    {
        CreateDatabase(path,
            "CREATE TABLE items(id INTEGER PRIMARY KEY, value TEXT);"
            + "CREATE TABLE untouched(value TEXT);"
            + string.Concat(Enumerable.Range(1, 12)
                .Select(i => $"INSERT INTO untouched VALUES ('{new string((char)('a' + i), 1200)}');"))
            + "INSERT INTO items VALUES (1, 'before');");
        var original = path + ".original";
        var committed = path + ".committed";
        File.Copy(path, original);
        using (var connection = new AhtolaConnection($"Data Source={path};Local Provider=Managed"))
        {
            connection.Open();
            connection.ExecuteNonQuery("UPDATE items SET value='after' WHERE id=1;");
        }
        File.Copy(path, committed);
        new FileInfo(original).Length.Should().BeGreaterThan(4 * 4096);
        File.Copy(original, path, overwrite: true);
        return (original, committed, Metadata(original));
    }

    private static string CreateNextImage(string path)
    {
        var next = path + ".next";
        File.Copy(path, next);
        using (var connection = new AhtolaConnection($"Data Source={next};Local Provider=Managed"))
        {
            connection.Open();
            connection.ExecuteNonQuery("UPDATE items SET value='next' WHERE id=1;");
        }
        return next;
    }

    private static void CreateDatabase(string path, string sql)
    {
        using var connection = new AhtolaConnection($"Data Source={path};Local Provider=Managed");
        connection.Open();
        foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries))
            connection.ExecuteNonQuery(statement);
    }

    private static ManagedReplicaBootstrapper.ManagedReplicaMetadata Metadata(string path)
        => new(
            "revision-one",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
            Guid.NewGuid().ToString("N"),
            RemotePullProtocol.Pages,
            new Dictionary<ulong, string>())
        {
            RemoteBaseSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
        };

    private static void SeedMetadata(
        string path,
        ManagedReplicaBootstrapper.ManagedReplicaMetadata metadata)
    {
        File.WriteAllText(
            path + ManagedReplicaBootstrapper.MetadataSuffix,
            "version=5\n"
            + $"server_revision_base64={Convert.ToBase64String(Encoding.UTF8.GetBytes(metadata.Revision))}\n"
            + $"database_sha256={metadata.DatabaseSha256}\n"
            + $"client_id={metadata.ClientId}\n"
            + "protocol=pages\n"
            + "table_map_base64=AAAAAA==\n"
            + "journal_base_watermark=1\n"
            + $"remote_base_sha256={metadata.RemoteBaseSha256}\n");
    }

    private static string NewPath()
        => Path.Combine(TestContext.CurrentContext.WorkDirectory, $"replica-sparse-{Guid.NewGuid():N}.db");

    private static void Delete(string path)
    {
        foreach (var artifact in ManagedReplicaBootstrapper.GetLocalArtifactPaths(path)
                     .Concat([path + ".original", path + ".committed", path + ".next",
                         path + ".small", path + ".large", path + ".third", path + ".meta-stage"]))
        {
            if (File.Exists(artifact))
                File.Delete(artifact);
        }
    }
}
