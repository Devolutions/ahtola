using System.Collections.Concurrent;
using System.Diagnostics;
using Ahtola.Core;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Regression coverage for two cross-process review findings against the managed embedded replica:
///
/// (A) <see cref="ManagedReplicaChangeJournal"/> publishes by rewriting the <em>whole</em> file, so
/// two instances -- two connections, two aliases of one file, or two processes -- used to silently
/// drop each other's durably appended, acknowledged, or discarded entries: whoever replaced last
/// won. Every mutation now runs under <see cref="ManagedReplicaJournalLock"/> and re-reads the
/// durable file first, so concurrent writers merge instead of clobbering.
///
/// (B) A lock carrier derived textually from a path gives every alias of one physical file its own,
/// mutually invisible operating-system lock. <see cref="ManagedReplicaLockCarrier"/> now names the
/// carrier from the file's physical identity, so a hard link -- the one alias no textual
/// normalization can ever collapse, because both names are equally real directory entries for one
/// inode -- shares a single carrier, and resolution fails closed when that identity cannot be
/// proven.
/// </summary>
public sealed class ManagedReplicaJournalAndLockCarrierRegressionTests
{
    private static readonly TimeSpan BlockedProbe = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(30);

    private static string NewReplicaPath(string prefix)
        => Path.Combine(TestContext.CurrentContext.WorkDirectory, $"{prefix}-{Guid.NewGuid():N}.db");

    private static void DeleteReplicaFiles(string path)
    {
        foreach (var file in ManagedReplicaBootstrapper.GetLocalArtifactPaths(path))
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    private static ReplicaLocalChange Row(string table, long rowId, string sql)
        => ReplicaLocalChange.Row(SqliteChangeOperation.Insert, "main", table, rowId) with { Sql = sql };

    private static IReadOnlyList<long> Sequences(ManagedReplicaChangeJournal journal)
        => journal.ReadBatch(int.MaxValue).Changes.Select(change => change.Sequence).ToArray();

    // ---- (A) journal append/persist serialization ---------------------------------------------

    [Test]
    public void TwoJournalInstancesOverOneFileMergeTheirAppendsInsteadOfOverwritingEachOther()
    {
        var path = NewReplicaPath("journal-two-instances");
        try
        {
            // Both instances are opened against the same empty file, so both start from an assigned
            // high-water mark of 0. Persisting from that stale in-memory snapshot is exactly the
            // lost update this regression is about: the second writer would rewrite the whole file
            // with only its own entry, again at sequence 1, and the first writer's durable entry
            // would simply be gone.
            var first = ManagedReplicaChangeJournal.Open(path);
            var second = ManagedReplicaChangeJournal.Open(path);

            first.AppendCommitted([Row("t", 1, "INSERT INTO t VALUES (1)")]);
            second.AppendCommitted([Row("t", 2, "INSERT INTO t VALUES (2)")]);

            var durable = ManagedReplicaChangeJournal.Open(path);
            Sequences(durable).Should().Equal(1L, 2L);
            durable.ReadBatch(int.MaxValue).Changes.Select(change => change.Sql)
                .Should().Equal("INSERT INTO t VALUES (1)", "INSERT INTO t VALUES (2)");

            // The second instance assigned against the durable high-water mark rather than the
            // snapshot it was opened with, so its own view now agrees with the file.
            second.AssignedSequence.Should().Be(2);
            Sequences(second).Should().Equal(1L, 2L);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void AnAcknowledgementPublishedByOneInstanceSurvivesAnotherInstancesLaterAppend()
    {
        var path = NewReplicaPath("journal-ack-vs-append");
        try
        {
            var writer = ManagedReplicaChangeJournal.Open(path);
            writer.AppendCommitted([Row("t", 1, "INSERT INTO t VALUES (1)")]);
            writer.AppendCommitted([Row("t", 2, "INSERT INTO t VALUES (2)")]);

            // Opened before the acknowledgement, so its in-memory watermark is stale by the time it
            // publishes. Writing that stale watermark back would resurrect an entry the remote has
            // already confirmed and push it a second time.
            var appender = ManagedReplicaChangeJournal.Open(path);
            writer.Acknowledge(2);
            appender.AppendCommitted([Row("t", 3, "INSERT INTO t VALUES (3)")]);

            var durable = ManagedReplicaChangeJournal.Open(path);
            durable.AcknowledgedWatermark.Should().Be(2);
            durable.AssignedSequence.Should().Be(3);
            Sequences(durable).Should().Equal(2L, 3L);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void ADiscardPublishedByOneInstanceSurvivesAnotherInstancesLaterAppend()
    {
        var path = NewReplicaPath("journal-discard-vs-append");
        try
        {
            var writer = ManagedReplicaChangeJournal.Open(path);
            writer.AppendCommitted([Row("t", 1, "INSERT INTO t VALUES (1)")]);
            writer.AppendCommitted([Row("t", 2, "INSERT INTO t VALUES (2)")]);

            var appender = ManagedReplicaChangeJournal.Open(path);
            writer.DiscardUnacknowledged([1]).Should().Be(1);
            appender.AppendCommitted([Row("t", 3, "INSERT INTO t VALUES (3)")]);

            // The discard record is the only durable evidence that the hole at sequence 1 is
            // intentional. An append computed from a pre-discard snapshot would erase it and make
            // the next reopen fail closed on an unexplained gap.
            var durable = ManagedReplicaChangeJournal.Open(path);
            durable.DiscardedSequences.Should().Equal(1L);
            durable.WasDiscarded(1).Should().BeTrue();
            Sequences(durable).Should().Equal(2L, 3L);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void ManyConcurrentJournalInstancesAssignEverySequenceExactlyOnce()
    {
        const int writers = 4;
        const int appendsPerWriter = 25;

        var path = NewReplicaPath("journal-concurrent-instances");
        try
        {
            // Every task drives its OWN instance, so nothing but the physical journal lease (plus
            // the durable re-read inside each mutation) keeps the whole-file rewrites from
            // clobbering one another. A lost update shows up as a missing sequence, a lock that did
            // not actually exclude shows up as a duplicate, and a torn write fails the reopen.
            using var start = new ManualResetEventSlim(false);
            var failures = new ConcurrentBag<Exception>();
            var tasks = new Task[writers];
            for (var writer = 0; writer < writers; writer++)
            {
                var id = writer;
                tasks[writer] = Task.Run(() =>
                {
                    try
                    {
                        var journal = ManagedReplicaChangeJournal.Open(path);
                        start.Wait(Settle);
                        for (var i = 0; i < appendsPerWriter; i++)
                            journal.AppendCommitted([Row("t", id, $"INSERT INTO t VALUES ({id}, {i})")]);
                    }
                    catch (Exception exception)
                    {
                        failures.Add(exception);
                    }
                });
            }

            start.Set();
            Task.WaitAll(tasks, TimeSpan.FromSeconds(120)).Should().BeTrue();
            failures.Should().BeEmpty();

            var durable = ManagedReplicaChangeJournal.Open(path);
            durable.AssignedSequence.Should().Be(writers * appendsPerWriter);
            Sequences(durable).Should().Equal(
                Enumerable.Range(1, writers * appendsPerWriter).Select(value => (long)value));
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void OpeningAJournalWaitsForAnInFlightPersistInsteadOfDeletingItsStagingFile()
    {
        var path = NewReplicaPath("journal-open-vs-persist");
        try
        {
            var journal = ManagedReplicaChangeJournal.Open(path);
            Task? opener = null;
            using var openerStarted = new ManualResetEventSlim(false);
            var blockedWhilePersisting = false;

            using (ManagedReplicaFaultInjection.Push(boundary =>
            {
                // Hit from inside the persist, while its lease is still held.
                if (boundary != ManagedReplicaDurableBoundary.JournalAppendPersisted || opener is not null)
                    return;

                opener = Task.Run(() =>
                {
                    openerStarted.Set();
                    _ = ManagedReplicaChangeJournal.Open(path);
                });

                openerStarted.Wait(Settle);
                blockedWhilePersisting = !opener.Wait(BlockedProbe);
            }))
            {
                journal.AppendCommitted([Row("t", 1, "INSERT INTO t VALUES (1)")]);
            }

            opener.Should().NotBeNull("the append must have reached its durable boundary");
            blockedWhilePersisting.Should().BeTrue(
                "opening a journal deletes any leftover staging file, and POSIX unlink succeeds on a "
                + "file another process still holds open, so that cleanup has to wait for an in-flight persist");
            opener!.Wait(Settle).Should().BeTrue();
            Sequences(ManagedReplicaChangeJournal.Open(path)).Should().Equal(1L);
            File.Exists(ManagedReplicaChangeJournal.GetStagingPath(path)).Should().BeFalse();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void TheJournalLeaseExcludesASecondHolderAndUsesItsOwnCarrier()
    {
        var path = NewReplicaPath("journal-lease-exclusive");
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            var carrier = ManagedReplicaLockCarrier.Ensure(path, ManagedReplicaLockCarrier.JournalKind);
            File.Exists(carrier).Should().BeTrue();

            // Deliberately a SEPARATE carrier from the apply lease rather than a second byte range
            // on the same one: on macOS these are process-associated POSIX locks, where closing any
            // descriptor for a file drops every lock the process holds on it, so one shared carrier
            // would let the two leases silently cancel each other.
            carrier.Should().NotBe(ManagedReplicaLockCarrier.Ensure(path, ManagedReplicaLockCarrier.ApplyKind));

            RunExclusionProbe(path, path, ManagedReplicaJournalLock.AcquireExclusive);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    // ---- (B) physical-identity lock carriers --------------------------------------------------

    [Test]
    public void HardLinkAliasesOfOneDatabaseResolveToOneApplyAndOneJournalCarrier()
    {
        var path = NewReplicaPath("carrier-hard-link-identity");
        var aliasPath = path + ".hardlink";
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            RequireHardLink(aliasPath, path);
            Path.GetFullPath(aliasPath).Should().NotBe(
                Path.GetFullPath(path),
                "the point of this case is that the two names stay textually distinct");

            foreach (var kind in new[] { ManagedReplicaLockCarrier.ApplyKind, ManagedReplicaLockCarrier.JournalKind })
            {
                ManagedReplicaLockCarrier.Ensure(aliasPath, kind)
                    .Should().Be(ManagedReplicaLockCarrier.Ensure(path, kind));
                ManagedReplicaLockCarrier.TryResolve(aliasPath, kind)
                    .Should().Be(ManagedReplicaLockCarrier.TryResolve(path, kind));
            }

            // The carrier deliberately lives in the shared lock directory, never beside the
            // database: two hard links to one file may live in different directories, and a
            // per-directory carrier would split them again.
            Path.GetDirectoryName(ManagedReplicaLockCarrier.Ensure(path, ManagedReplicaLockCarrier.ApplyKind))
                .Should().NotBe(Path.GetDirectoryName(Path.GetFullPath(path)));
        }
        finally
        {
            if (File.Exists(aliasPath))
                File.Delete(aliasPath);
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task ApplyLeaseSerializesAHardLinkAliasBehindItsRealPath()
    {
        var path = NewReplicaPath("apply-lease-hard-link");
        var aliasPath = path + ".hardlink";
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            RequireHardLink(aliasPath, path);

            var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var holder = Task.Run(async () =>
            {
                await using var lease = await ManagedReplicaApplyLock
                    .AcquireExclusiveAsync(path, CancellationToken.None)
                    .ConfigureAwait(false);
                acquired.TrySetResult();
                await release.Task.ConfigureAwait(false);
            });

            await acquired.Task.WaitAsync(Settle);

            var contenderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var contender = Task.Run(async () =>
            {
                contenderStarted.TrySetResult();
                await using var lease = await ManagedReplicaApplyLock
                    .AcquireExclusiveAsync(aliasPath, CancellationToken.None)
                    .ConfigureAwait(false);
            });

            await contenderStarted.Task.WaitAsync(Settle);
            await Task.Delay(BlockedProbe);
            contender.IsCompleted.Should().BeFalse(
                "a hard-link alias of the same physical file must queue behind the held apply lease "
                + "rather than acquire a second, independent one");

            release.TrySetResult();
            await holder.WaitAsync(Settle);
            await contender.WaitAsync(Settle);
        }
        finally
        {
            if (File.Exists(aliasPath))
                File.Delete(aliasPath);
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void JournalLeaseSerializesAHardLinkAliasBehindItsRealPath()
    {
        var path = NewReplicaPath("journal-lease-hard-link");
        var aliasPath = path + ".hardlink";
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            RequireHardLink(aliasPath, path);
            RunExclusionProbe(path, aliasPath, ManagedReplicaJournalLock.AcquireExclusive);
        }
        finally
        {
            if (File.Exists(aliasPath))
                File.Delete(aliasPath);
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void LockCarrierResolutionFailsClosedWhenPhysicalIdentityCannotBeProven()
    {
        // Neither the file nor its parent directory exists, so nothing physical can be
        // canonicalized and no carrier can be guaranteed to be shared by every alias. Handing back
        // a textually derived carrier here would let two writers each believe they hold the
        // exclusive lease, so resolution refuses instead of guessing.
        var missing = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"carrier-missing-parent-{Guid.NewGuid():N}",
            "replica.db");

        Assert.Throws<PlatformNotSupportedException>(
            () => ManagedReplicaLockCarrier.Ensure(missing, ManagedReplicaLockCarrier.ApplyKind));
        Assert.Throws<PlatformNotSupportedException>(
            () => ManagedReplicaLockCarrier.Ensure(missing, ManagedReplicaLockCarrier.JournalKind));

        // Artifact enumeration only describes a footprint, so it must never fail: the non-throwing
        // seam reports "unknown" and the enumeration simply omits the carrier.
        ManagedReplicaLockCarrier.TryResolve(missing, ManagedReplicaLockCarrier.ApplyKind).Should().BeNull();
        ManagedReplicaBootstrapper.GetLocalArtifactPaths(missing).Should().Contain(missing);
    }

    [Test]
    public void AMissingDatabaseStillResolvesADistinctCarrierFromItsParentDirectoryIdentity()
    {
        // A first-ever bootstrap has no file identity to read -- and a file that does not exist can
        // have no hard links either -- so the parent directory's physical identity plus the file
        // name is alias-safe for exactly that case and must still resolve rather than fail closed.
        var path = NewReplicaPath("carrier-missing-file");
        var sibling = NewReplicaPath("carrier-missing-file-sibling");
        try
        {
            File.Exists(path).Should().BeFalse();
            var carrier = ManagedReplicaLockCarrier.Ensure(path, ManagedReplicaLockCarrier.ApplyKind);
            File.Exists(carrier).Should().BeTrue();
            ManagedReplicaLockCarrier.Ensure(path, ManagedReplicaLockCarrier.ApplyKind).Should().Be(carrier);

            // Two different missing files share one parent identity, so the file name has to keep
            // them apart.
            ManagedReplicaLockCarrier.Ensure(sibling, ManagedReplicaLockCarrier.ApplyKind).Should().NotBe(carrier);
        }
        finally
        {
            DeleteReplicaFiles(sibling);
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void GetLocalArtifactPathsListsBothPhysicalIdentityCarriersSoCleanupRemovesThem()
    {
        var path = NewReplicaPath("carrier-artifact-enumeration");
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            var apply = ManagedReplicaLockCarrier.Ensure(path, ManagedReplicaLockCarrier.ApplyKind);
            var journal = ManagedReplicaLockCarrier.Ensure(path, ManagedReplicaLockCarrier.JournalKind);

            ManagedReplicaBootstrapper.GetLocalArtifactPaths(path).Should().Contain([apply, journal]);

            DeleteReplicaFiles(path);
            File.Exists(apply).Should().BeFalse();
            File.Exists(journal).Should().BeFalse();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    // ---- helpers ------------------------------------------------------------------------------

    /// <summary>
    /// Holds <paramref name="holderPath"/>'s lease, proves a second acquisition through
    /// <paramref name="contenderPath"/> blocks while it is held, and proves it completes once the
    /// lease is released (so the contender was queued, never failed outright).
    /// </summary>
    private static void RunExclusionProbe(
        string holderPath,
        string contenderPath,
        Func<string, IDisposable> acquire)
    {
        using var acquired = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var contenderStarted = new ManualResetEventSlim(false);

        var holder = Task.Run(() =>
        {
            using (acquire(holderPath))
            {
                acquired.Set();
                release.Wait(Settle);
            }
        });

        acquired.Wait(Settle).Should().BeTrue();
        var contender = Task.Run(() =>
        {
            contenderStarted.Set();
            acquire(contenderPath).Dispose();
        });

        contenderStarted.Wait(Settle).Should().BeTrue();
        contender.Wait(BlockedProbe).Should().BeFalse(
            "the lease must exclude a second holder for the same physical database, however it was spelled");

        release.Set();
        holder.Wait(Settle).Should().BeTrue();
        contender.Wait(Settle).Should().BeTrue();
    }

    /// <summary>
    /// Creates a hard link, or ignores the calling test when the host or file system cannot make
    /// one. .NET exposes no managed hard-link API (only symbolic links) and this repository does not
    /// add P/Invoke outside <c>Ahtola.Core/Storage</c>, so the platform tool is used instead.
    /// </summary>
    private static void RequireHardLink(string linkPath, string targetPath)
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("fsutil.exe", ["hardlink", "create", linkPath, targetPath])
            : new ProcessStartInfo("ln", [targetPath, linkPath]);
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("the hard-link tool did not start");
            var diagnostics = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(milliseconds: 30_000))
            {
                process.Kill(entireProcessTree: true);
                Assert.Ignore("Creating a hard link timed out on this host.");
            }

            if (process.ExitCode != 0)
                Assert.Ignore($"Creating a hard link is not supported on this host: {diagnostics.Trim()}");
        }
        catch (Exception exception) when (exception
                                              is System.ComponentModel.Win32Exception
                                              or PlatformNotSupportedException
                                              or InvalidOperationException)
        {
            Assert.Ignore($"Creating a hard link is not available on this host: {exception.Message}");
        }

        if (!File.Exists(linkPath))
            Assert.Ignore("The host reported success but produced no hard link.");

        // Prove the result really is one physical file under two names before relying on it: a tool
        // that silently copied would make every assertion in the caller vacuously pass.
        using (var target = new FileStream(targetPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            target.Seek(0, SeekOrigin.End);
            target.WriteByte(0x5a);
            target.Flush(flushToDisk: true);
        }

        if (new FileInfo(linkPath).Length != new FileInfo(targetPath).Length)
            Assert.Ignore("The host produced a copy rather than a hard link.");
    }
}
