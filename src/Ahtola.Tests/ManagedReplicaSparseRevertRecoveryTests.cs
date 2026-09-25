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
            pending.RevertState.Value.CommittedRevertWalFrameCount.Should()
                .BeLessThan(pending.RevertState.Value.CommittedDatabaseSizeInPages);
            File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(committed));

            var reopened = ManagedReplicaRevertWal.PrepareSynchronization(path, pending);
            reopened = ManagedReplicaRevertWal.CompletePreparedCheckpoint(path, reopened);
            reopened.RevertState.Should().BeNull();
            File.Exists(path + ManagedReplicaRevertWal.Suffix).Should().BeFalse();
            File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(committed));

            // A second generation still takes a fresh protected capture; this does
            // not claim cross-generation history retention.
            var next = CreateNextImage(path);
            _ = ManagedReplicaRevertWal.PublishProtectedSnapshots(
                path, reopened, committed, next, CancellationToken.None);
            var second = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            second.RevertState!.Value.FormatVersion.Should().Be(5);
            ManagedReplicaRevertWal.RestorePendingCheckpoint(path, second);
            File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(committed));
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
                         path + ".small", path + ".large", path + ".meta-stage"]))
        {
            if (File.Exists(artifact))
                File.Delete(artifact);
        }
    }
}
