using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class SqliteWalWriterCheckpointCoordinatorTests
{
    private const long WriteLockOffset = SqliteWalIndexCheckpointInfo.LockOffset;
    private const long RecoveryLockOffset = SqliteWalIndexCheckpointInfo.LockOffset + 2;

    [Test]
    [NonParallelizable]
    public void DetachedWriterPublishesDurableFramesOnlyAfterUpdatingTheWalIndex()
    {
        RequireCoordinatorSupport();
        using var artifact = SqliteWalArtifact.Create();
        using var coordinator = SqliteWalWriterCheckpointCoordinator.Open(artifact.DatabasePath);
        using var sourceWal = OpenWalCopy(artifact.DatabasePath);
        using var sourceMapping = ((ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance).OpenSharedMemory(
            artifact.DatabasePath + "-shm",
            FileOpenMode.OpenExisting,
            readOnly: true);
        var sourceIndex = new SqliteWalIndexSharedMemory(sourceMapping);
        var prior = sourceIndex.ReadValidatedHeader(sourceWal);
        var sourceFrame = sourceWal.ReadFrame(prior.Header.MaximumFrame);
        var page = sourceFrame.PageData.ToArray();
        page[^1] ^= 0x5A;

        var written = coordinator.Commit(
            [new SqliteWalWritePage(sourceFrame.Header.PageNumber, page)],
            prior.Header.DatabasePageCount,
            TimeSpan.Zero);

        written.MaximumFrame.Should().Be(prior.Header.MaximumFrame + 1);
        using var committedWal = OpenWalCopy(artifact.DatabasePath);
        using var committedMapping = ((ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance).OpenSharedMemory(
            artifact.DatabasePath + "-shm",
            FileOpenMode.OpenExisting,
            readOnly: true);
        var committedIndex = new SqliteWalIndexSharedMemory(committedMapping);
        var committed = committedIndex.ReadValidatedHeader(committedWal);

        committed.Header.MaximumFrame.Should().Be(written.MaximumFrame);
        committedWal.ReadFrame(written.MaximumFrame).Header.IsCommit.Should().BeTrue();
        committedIndex.FindFrame(committedWal, sourceFrame.Header.PageNumber).Should().Be(written.MaximumFrame);
    }

    [Test]
    [NonParallelizable]
    public void PassiveCheckpointStopsAtAProcessIsolatedHeldSnapshot()
    {
        RequireCoordinatorSupport();
        using var artifact = SqliteWalArtifact.Create();
        using var coordinator = SqliteWalWriterCheckpointCoordinator.Open(artifact.DatabasePath);
        using var snapshots = SqliteWalReadSnapshotCoordinator.Open(artifact.DatabasePath);
        using var snapshot = snapshots.BeginRead(TimeSpan.Zero);
        RunSqliteWriterWorker(artifact.DatabasePath);

        var checkpoint = coordinator.Checkpoint(SqliteWalCheckpointMode.Passive, TimeSpan.Zero);

        checkpoint.IsBusy.Should().BeTrue();
        checkpoint.SafeFrame.Should().Be(snapshot.MaximumFrame);
        checkpoint.BackfilledFrameCount.Should().Be(snapshot.MaximumFrame);
        checkpoint.BackfillAttemptedFrameCount.Should().Be(snapshot.MaximumFrame);
        checkpoint.MaximumFrame.Should().BeGreaterThan(snapshot.MaximumFrame);
    }

    [Test]
    [NonParallelizable]
    public void PassiveCheckpointEvidenceBindsInclusiveWatermarkToWalIncarnation()
    {
        RequireCoordinatorSupport();
        using var artifact = SqliteWalArtifact.Create();
        using var coordinator = SqliteWalWriterCheckpointCoordinator.Open(artifact.DatabasePath);

        var checkpoint = coordinator.CheckpointPassiveValidated(TimeSpan.Zero);

        checkpoint.IsBusy.Should().BeFalse();
        checkpoint.HasValidatedWalIncarnation.Should().BeTrue();
        checkpoint.SyncedFrameInclusive.Should().Be(checkpoint.MaximumFrame);
        using var wal = OpenWalCopy(artifact.DatabasePath);
        using var mapping = ((ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance)
            .OpenSharedMemory(
                artifact.DatabasePath + "-shm",
                FileOpenMode.OpenExisting,
                readOnly: true);
        var header = new SqliteWalIndexSharedMemory(mapping).ReadValidatedHeader(wal).Header;
        checkpoint.WalIndexChangeCounter.Should().Be(header.ChangeCounter);
        checkpoint.WalSalt1.Should().Be(header.Salt1);
        checkpoint.WalSalt2.Should().Be(header.Salt2);
    }

    [Test]
    [NonParallelizable]
    public void FullCheckpointWaitsInsteadOfCheckpointingPastAHeldSnapshot()
    {
        RequireCoordinatorSupport();
        using var artifact = SqliteWalArtifact.Create();
        using var coordinator = SqliteWalWriterCheckpointCoordinator.Open(artifact.DatabasePath);
        using var snapshots = SqliteWalReadSnapshotCoordinator.Open(artifact.DatabasePath);
        using var snapshot = snapshots.BeginRead(TimeSpan.Zero);
        RunSqliteWriterWorker(artifact.DatabasePath);

        Assert.Throws<SqliteWalByteRangeLockBusyException>(
            () => coordinator.Checkpoint(SqliteWalCheckpointMode.Full, TimeSpan.FromMilliseconds(30)));

        snapshot.IsActive.Should().BeTrue();
    }

    [Test]
    [NonParallelizable]
    public void RestartAndTruncateResetOnlyAfterDurableBackfill()
    {
        RequireCoordinatorSupport();

        using (var restartArtifact = SqliteWalArtifact.Create())
        using (var restart = SqliteWalWriterCheckpointCoordinator.Open(restartArtifact.DatabasePath))
        {
            var result = restart.Checkpoint(SqliteWalCheckpointMode.Restart, TimeSpan.Zero);
            result.ResetWal.Should().BeTrue();
            result.BackfilledFrameCount.Should().Be(0);
            new FileInfo(restartArtifact.DatabasePath + "-wal").Length.Should().Be(SqliteWalHeader.Size);

            using var wal = OpenWalCopy(restartArtifact.DatabasePath);
            using var mapping = ((ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance).OpenSharedMemory(
                restartArtifact.DatabasePath + "-shm",
                FileOpenMode.OpenExisting,
                readOnly: true);
            var region = new SqliteWalIndexSharedMemory(mapping).ReadValidatedHeader(wal);
            var resetTail = new byte[8];
            mapping.Read(SqliteWalIndexHeader.Size * 2L + 32, resetTail);
            region.Header.MaximumFrame.Should().Be(0);
            region.CheckpointInfo.BackfilledFrameCount.Should().Be(0);
            region.CheckpointInfo.BackfillAttemptedFrameCount.Should().Be(0);
            resetTail.Should().Equal(new byte[8]);
            region.CheckpointInfo.GetReadMark(readMarkIndex: 0).Should().Be(0);
            for (var readMarkIndex = 1; readMarkIndex < SqliteWalIndexCheckpointInfo.ReadMarkCount; readMarkIndex++)
            {
                region.CheckpointInfo.GetReadMark(readMarkIndex)
                    .Should().Be(SqliteWalIndexCheckpointInfo.ReadMarkNotUsed);
            }
        }

        using (var truncateArtifact = SqliteWalArtifact.Create())
        {
            using (var truncate = SqliteWalWriterCheckpointCoordinator.Open(truncateArtifact.DatabasePath))
            {
                var result = truncate.Checkpoint(SqliteWalCheckpointMode.Truncate, TimeSpan.Zero);
                result.ResetWal.Should().BeTrue();
            }

            new FileInfo(truncateArtifact.DatabasePath + "-wal").Length.Should().Be(0);
            using var reopened = SqliteWalWriterCheckpointCoordinator.Open(truncateArtifact.DatabasePath);
            reopened.Checkpoint(SqliteWalCheckpointMode.Passive, TimeSpan.Zero).MaximumFrame.Should().Be(0);
            reopened.Recover(TimeSpan.Zero).LastCommittedFrameNumber.Should().Be(0);
            new FileInfo(truncateArtifact.DatabasePath + "-wal").Length.Should().Be(0);
        }
    }

    [TestCase("stale")]
    [TestCase("torn")]
    [NonParallelizable]
    public void ZeroLengthTruncateWalReopensDespiteStaleOrTornSharedMemory(string mutation)
    {
        RequireCoordinatorSupport();
        using var artifact = SqliteWalArtifact.Create();
        byte[] staleHeader;
        using (var mapping = ((ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance).OpenSharedMemory(
                   artifact.DatabasePath + "-shm",
                   FileOpenMode.OpenExisting,
                   readOnly: true))
        {
            staleHeader = new byte[SqliteWalIndexHeader.Size];
            mapping.Read(position: 0, staleHeader);
        }

        using (var coordinator = SqliteWalWriterCheckpointCoordinator.Open(artifact.DatabasePath))
            coordinator.Checkpoint(SqliteWalCheckpointMode.Truncate, TimeSpan.Zero).ResetWal.Should().BeTrue();

        using (var mapping = ((ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance).OpenSharedMemory(
                   artifact.DatabasePath + "-shm",
                   FileOpenMode.OpenExisting))
        {
            mapping.Write(position: 0, staleHeader);
            if (mutation == "stale")
            {
                mapping.Write(SqliteWalIndexHeader.Size, staleHeader);
            }
            else
            {
                var tornHeader = staleHeader.ToArray();
                tornHeader[40] ^= 0x01;
                mapping.Write(SqliteWalIndexHeader.Size, tornHeader);
            }
            mapping.MemoryBarrier();
        }

        new FileInfo(artifact.DatabasePath + "-wal").Length.Should().Be(0);
        using var reopened = SqliteWalWriterCheckpointCoordinator.Open(artifact.DatabasePath);
        reopened.Recover(TimeSpan.Zero).LastCommittedFrameNumber.Should().Be(0);
        reopened.Checkpoint(SqliteWalCheckpointMode.Passive, TimeSpan.Zero).MaximumFrame.Should().Be(0);
        new FileInfo(artifact.DatabasePath + "-wal").Length.Should().Be(0);
    }

    [Test]
    [NonParallelizable]
    public void CommitRefusesToPublishAValidUncommittedWalTail()
    {
        RequireCoordinatorSupport();
        using var artifact = SqliteWalArtifact.Create();
        using var coordinator = SqliteWalWriterCheckpointCoordinator.Open(artifact.DatabasePath);
        using var before = OpenWalCopy(artifact.DatabasePath);
        var beforeRecovery = before.ScanRecovery();
        var committedFrame = before.ReadFrame(beforeRecovery.LastCommittedFrameNumber);

        AppendValidUncommittedFrame(artifact.DatabasePath + "-wal", before, committedFrame);

        Assert.Throws<InvalidDataException>(
            () => coordinator.Commit(
                [new SqliteWalWritePage(committedFrame.Header.PageNumber, committedFrame.PageData)],
                beforeRecovery.LastCommittedDatabaseSizeInPages,
                TimeSpan.Zero));

        using (var tail = OpenWalCopy(artifact.DatabasePath))
        {
            var tailRecovery = tail.ScanRecovery();
            tailRecovery.LastValidFrameNumber.Should().Be(beforeRecovery.LastCommittedFrameNumber + 1);
            tailRecovery.LastCommittedFrameNumber.Should().Be(beforeRecovery.LastCommittedFrameNumber);
        }
        using (var mapping = ((ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance).OpenSharedMemory(
                   artifact.DatabasePath + "-shm",
                   FileOpenMode.OpenExisting,
                   readOnly: true))
        {
            var header = new byte[SqliteWalIndexHeader.Size];
            mapping.Read(position: 0, header);
            SqliteWalIndexHeader.Parse(header).MaximumFrame
                .Should().Be(checked((uint)beforeRecovery.LastCommittedFrameNumber));
        }

        if (!OperatingSystem.IsWindows())
        {
            Assert.Throws<InvalidDataException>(() => coordinator.Recover(TimeSpan.Zero));
            return;
        }

        coordinator.Recover(TimeSpan.Zero).LastCommittedFrameNumber
            .Should().Be(beforeRecovery.LastCommittedFrameNumber);
        coordinator.Commit(
                [new SqliteWalWritePage(committedFrame.Header.PageNumber, committedFrame.PageData)],
                beforeRecovery.LastCommittedDatabaseSizeInPages,
                TimeSpan.Zero)
            .MaximumFrame.Should().Be(checked((uint)beforeRecovery.LastCommittedFrameNumber + 1));
    }

    [Test]
    [NonParallelizable]
    public void CoordinatorRebuildsCheckpointProgressBeforeAllowingAWalReset()
    {
        RequireCoordinatorSupport();
        using var artifact = SqliteWalArtifact.Create();
        using (var wal = OpenWalCopy(artifact.DatabasePath))
        using (var mapping = ((ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance).OpenSharedMemory(
                   artifact.DatabasePath + "-shm",
                   FileOpenMode.OpenExisting))
        {
            var region = new SqliteWalIndexSharedMemory(mapping).ReadValidatedHeader(wal);
            WriteUInt32Native(mapping, position: 96, region.Header.MaximumFrame);
            WriteUInt32Native(mapping, position: 128, region.Header.MaximumFrame);
            mapping.MemoryBarrier();
        }

        using var coordinator = SqliteWalWriterCheckpointCoordinator.Open(artifact.DatabasePath);
        using var verifiedWal = OpenWalCopy(artifact.DatabasePath);
        using var verifiedMapping = ((ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance).OpenSharedMemory(
            artifact.DatabasePath + "-shm",
            FileOpenMode.OpenExisting,
            readOnly: true);
        var verified = new SqliteWalIndexSharedMemory(verifiedMapping).ReadValidatedHeader(verifiedWal);
        verified.CheckpointInfo.BackfilledFrameCount.Should().Be(0);
        verified.CheckpointInfo.BackfillAttemptedFrameCount.Should().Be(0);
    }

    [Test]
    [NonParallelizable]
    public void PassiveCheckpointSoftSkipsWhenWalIncarnationChangesAfterReadMarksRelease()
    {
        // SQLite 3.51.3 / Tailscale WAL-reset race: after PASSIVE releases read marks,
        // a peer may wrap the WAL (new salts, mxFrame=0). Soft-skip must not publish
        // the pre-wrap safeFrame into nBackfill.
        RequireCoordinatorSupport();
        using var artifact = SqliteWalArtifact.Create();
        uint preWrapMaximumFrame;
        using (var probeWal = OpenWalCopy(artifact.DatabasePath))
        {
            preWrapMaximumFrame = checked((uint)probeWal.ScanRecovery().LastCommittedFrameNumber);
            preWrapMaximumFrame.Should().BeGreaterThan(0);
        }

        try
        {
            SqliteWalWriterCheckpointCoordinator.AfterDetachedPassiveReadMarksReleasedForTesting =
                () => SimulatePeerWalWrap(artifact.DatabasePath);

            using var coordinator = SqliteWalWriterCheckpointCoordinator.Open(artifact.DatabasePath);
            var result = coordinator.Checkpoint(SqliteWalCheckpointMode.Passive, TimeSpan.Zero);

            result.ResetWal.Should().BeFalse();
            result.IsBusy.Should().BeFalse();
            result.BackfilledFrameCount.Should().Be(0);
            result.MaximumFrame.Should().Be(0);
            result.BackfilledFrameCount.Should().NotBe(preWrapMaximumFrame);
        }
        finally
        {
            SqliteWalWriterCheckpointCoordinator.AfterDetachedPassiveReadMarksReleasedForTesting = null;
        }

        using var verifiedWal = OpenWalCopy(artifact.DatabasePath);
        using var verifiedMapping = ((ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance).OpenSharedMemory(
            artifact.DatabasePath + "-shm",
            FileOpenMode.OpenExisting,
            readOnly: true);
        var verified = new SqliteWalIndexSharedMemory(verifiedMapping).ReadValidatedHeader(verifiedWal);
        verified.Header.MaximumFrame.Should().Be(0);
        verified.CheckpointInfo.BackfilledFrameCount.Should().Be(0);
        // Attempted may have been published under the pre-wrap incarnation; durable
        // nBackfill must never land at the stale safe frame.
        verified.CheckpointInfo.BackfilledFrameCount.Should().NotBe(preWrapMaximumFrame);
    }

    [Test]
    [NonParallelizable]
    public void CheckpointResetMarkerRepairsAStaleIndexOnReopen()
    {
        RequireCoordinatorSupport();
        using var artifact = SqliteWalArtifact.Create();
        byte[] staleHeader;
        using (var wal = OpenWalCopy(artifact.DatabasePath))
        using (var mapping = ((ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance).OpenSharedMemory(
                   artifact.DatabasePath + "-shm",
                   FileOpenMode.OpenExisting,
                   readOnly: true))
        {
            staleHeader = new SqliteWalIndexSharedMemory(mapping)
                .ReadValidatedHeader(wal)
                .Header
                .ToArray();
        }

        using (var coordinator = SqliteWalWriterCheckpointCoordinator.Open(artifact.DatabasePath))
        {
            coordinator.Checkpoint(SqliteWalCheckpointMode.Restart, TimeSpan.Zero).ResetWal.Should().BeTrue();
            using var mapping = ((ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance).OpenSharedMemory(
                artifact.DatabasePath + "-shm",
                FileOpenMode.OpenExisting);
            mapping.Write(position: 0, staleHeader);
            mapping.Write(SqliteWalIndexHeader.Size, staleHeader);
            mapping.MemoryBarrier();
        }

        using var reopened = SqliteWalWriterCheckpointCoordinator.Open(artifact.DatabasePath);
        reopened.Checkpoint(SqliteWalCheckpointMode.Passive, TimeSpan.Zero).MaximumFrame.Should().Be(0);
    }

    [Test]
    [NonParallelizable]
    public void CanceledCommitAndASeparateProcessWriterLeaseLeaveNoHeldWriteLock()
    {
        RequireCoordinatorSupport();
        using var artifact = SqliteWalArtifact.Create();
        using var coordinator = SqliteWalWriterCheckpointCoordinator.Open(artifact.DatabasePath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => coordinator.Commit(
                [new SqliteWalWritePage(1, new byte[512])],
                databasePageCount: 1,
                TimeSpan.Zero,
                cancellation.Token));

        using (var worker = new CrossProcessLockHandle(
                   artifact.WorkDirectory,
                   artifact.DatabasePath + "-shm",
                   WriteLockOffset,
                   length: 1))
        {
            worker.Result.Should().Be("acquired");
            Assert.Throws<SqliteWalByteRangeLockBusyException>(
                () => coordinator.Checkpoint(SqliteWalCheckpointMode.Restart, TimeSpan.Zero));
        }

        var lockProbe = new SqliteWalByteRangeLock(artifact.DatabasePath + "-shm");
        lockProbe.TryAcquireExclusive(WriteLockOffset, length: 1, out var released).Should().BeTrue();
        released!.Dispose();
    }

    [Test]
    [NonParallelizable]
    public void RecoveryReadsTailEvidenceOnlyAfterOwningTheFullRecoveryLockSet()
    {
        RequireCoordinatorSupport();
        using var artifact = SqliteWalArtifact.Create();
        using var coordinator = SqliteWalWriterCheckpointCoordinator.Open(artifact.DatabasePath);
        using var before = OpenWalCopy(artifact.DatabasePath);
        var beforeRecovery = before.ScanRecovery();
        var committedFrame = before.ReadFrame(beforeRecovery.LastCommittedFrameNumber);
        AppendValidUncommittedFrame(artifact.DatabasePath + "-wal", before, committedFrame);

        byte[] firstHeader;
        byte[] secondHeader;
        using (var mapping = ((ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance).OpenSharedMemory(
                   artifact.DatabasePath + "-shm",
                   FileOpenMode.OpenExisting))
        {
            firstHeader = new byte[SqliteWalIndexHeader.Size];
            secondHeader = new byte[SqliteWalIndexHeader.Size];
            mapping.Read(position: 0, firstHeader);
            mapping.Read(SqliteWalIndexHeader.Size, secondHeader);

            var invalidHeader = firstHeader.ToArray();
            invalidHeader[40] ^= 0x01;
            mapping.Write(position: 0, invalidHeader);
            mapping.Write(SqliteWalIndexHeader.Size, invalidHeader);
            mapping.MemoryBarrier();
        }

        using var recoveryBlocker = new CrossProcessLockHandle(
            artifact.WorkDirectory,
            artifact.DatabasePath + "-shm",
            RecoveryLockOffset,
            length: 1);
        var recovery = Task.Run(() => coordinator.Recover(TimeSpan.FromSeconds(5)));

        using (var writerProbe = new CrossProcessLockHandle(
                   artifact.WorkDirectory,
                   artifact.DatabasePath + "-shm",
                   WriteLockOffset,
                   length: 1,
                   holdLease: false))
        {
            writerProbe.Result.Should().Be("busy");
        }

        using (var mapping = ((ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance).OpenSharedMemory(
                   artifact.DatabasePath + "-shm",
                   FileOpenMode.OpenExisting))
        {
            mapping.Write(position: 0, firstHeader);
            mapping.Write(SqliteWalIndexHeader.Size, secondHeader);
            mapping.MemoryBarrier();
        }

        recoveryBlocker.Dispose();
        if (OperatingSystem.IsWindows())
        {
            recovery.GetAwaiter().GetResult().LastCommittedFrameNumber
                .Should().Be(beforeRecovery.LastCommittedFrameNumber);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => recovery.GetAwaiter().GetResult());
        }

        using var repairedWal = OpenWalCopy(artifact.DatabasePath);
        repairedWal.ScanRecovery().LastValidFrameNumber.Should().Be(
            OperatingSystem.IsWindows()
                ? beforeRecovery.LastCommittedFrameNumber
                : beforeRecovery.LastCommittedFrameNumber + 1);
    }

    [Test]
    [NonParallelizable]
    public void RecoveryRejectsDivergentZeroAndCommittedHeadersBeforeTruncatingACorruptWal()
    {
        RequireCoordinatorSupport();
        using var artifact = SqliteWalArtifact.Create();
        using var coordinator = SqliteWalWriterCheckpointCoordinator.Open(artifact.DatabasePath);
        using var wal = OpenWalCopy(artifact.DatabasePath);
        using var mapping = ((ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance).OpenSharedMemory(
            artifact.DatabasePath + "-shm",
            FileOpenMode.OpenExisting);
        var index = new SqliteWalIndexSharedMemory(mapping);
        var committedHeader = index.ReadValidatedHeader(wal).Header;
        var zeroHeader = committedHeader.WithRestartedWal(committedHeader.DatabasePageCount);
        mapping.Write(position: 0, zeroHeader.ToArray());
        mapping.Write(SqliteWalIndexHeader.Size, committedHeader.ToArray());
        mapping.MemoryBarrier();

        var walPath = artifact.DatabasePath + "-wal";
        var originalLength = new FileInfo(walPath).Length;
        using (var stream = new FileStream(
                   walPath,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            stream.Position = SqliteWalHeader.Size + 16;
            var checksum = stream.ReadByte();
            checksum.Should().NotBe(-1);
            stream.Position = SqliteWalHeader.Size + 16;
            stream.WriteByte(unchecked((byte)(checksum ^ 0x01)));
            stream.Flush(flushToDisk: true);
        }

        Assert.Throws<InvalidDataException>(() => coordinator.Recover(TimeSpan.Zero));
        new FileInfo(walPath).Length.Should().Be(originalLength);
        using var corruptWal = OpenWalCopy(artifact.DatabasePath);
        var recovery = corruptWal.ScanRecovery();
        recovery.LastCommittedFrameNumber.Should().Be(0);
        recovery.StopReason.Should().Be(SqliteWalRecoveryStopReason.InvalidFrame);
    }

    [Test]
    [NonParallelizable]
    public void RecoveryRequiresAuthenticatedDatabaseSizeAndFrameChecksumsBeforeTruncatingATail()
    {
        RequireCoordinatorSupport();
        using var artifact = SqliteWalArtifact.Create();
        using var coordinator = SqliteWalWriterCheckpointCoordinator.Open(artifact.DatabasePath);
        using var before = OpenWalCopy(artifact.DatabasePath);
        var beforeRecovery = before.ScanRecovery();
        AppendValidUncommittedFrame(
            artifact.DatabasePath + "-wal",
            before,
            before.ReadFrame(beforeRecovery.LastCommittedFrameNumber));
        var walLength = new FileInfo(artifact.DatabasePath + "-wal").Length;

        using (var mapping = ((ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance).OpenSharedMemory(
                   artifact.DatabasePath + "-shm",
                   FileOpenMode.OpenExisting))
        {
            var index = new SqliteWalIndexSharedMemory(mapping);
            var publishedHeader = index.ReadValidatedHeader(before).Header;
            var mismatchedHeaderBytes = publishedHeader.ToArray();
            WriteUInt32Native(
                mismatchedHeaderBytes,
                position: 20,
                checked(publishedHeader.DatabasePageCount + 1));
            RewriteIndexHeaderChecksum(mismatchedHeaderBytes);
            var mismatchedHeader = SqliteWalIndexHeader.Parse(mismatchedHeaderBytes);
            mapping.Write(position: 0, mismatchedHeader.ToArray());
            mapping.Write(SqliteWalIndexHeader.Size, mismatchedHeader.ToArray());
            mapping.MemoryBarrier();
        }

        Assert.Throws<InvalidDataException>(() => coordinator.Recover(TimeSpan.Zero));
        new FileInfo(artifact.DatabasePath + "-wal").Length.Should().Be(walLength);
        using var tail = OpenWalCopy(artifact.DatabasePath);
        tail.ScanRecovery().LastValidFrameNumber.Should().Be(beforeRecovery.LastCommittedFrameNumber + 1);
    }

    [Test]
    [NonParallelizable]
    public void RecoveryRejectsSharedMemoryCarrierReplacementBeforeTailTruncation()
    {
        RequireCoordinatorSupport();
        using var artifact = SqliteWalArtifact.Create();
        var databasePath = Path.Combine(artifact.WorkDirectory, "carrier-replacement.db");
        File.Copy(artifact.DatabasePath, databasePath);
        File.Copy(artifact.DatabasePath + "-wal", databasePath + "-wal");
        File.Copy(artifact.DatabasePath + "-shm", databasePath + "-shm");

        using var coordinator = SqliteWalWriterCheckpointCoordinator.Open(databasePath);
        using var before = OpenWalCopy(databasePath);
        var beforeRecovery = before.ScanRecovery();
        AppendValidUncommittedFrame(
            databasePath + "-wal",
            before,
            before.ReadFrame(beforeRecovery.LastCommittedFrameNumber));
        var walPath = databasePath + "-wal";
        var walLength = new FileInfo(walPath).Length;
        var sharedMemoryPath = databasePath + "-shm";
        var replacedCarrierPath = sharedMemoryPath + ".replaced";
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<IOException>(() => File.Move(sharedMemoryPath, replacedCarrierPath));
            coordinator.Recover(TimeSpan.Zero).LastCommittedFrameNumber
                .Should().Be(beforeRecovery.LastCommittedFrameNumber);
            return;
        }

        File.Move(sharedMemoryPath, replacedCarrierPath);
        File.Copy(replacedCarrierPath, sharedMemoryPath);

        Assert.Throws<InvalidDataException>(() => coordinator.Recover(TimeSpan.Zero));
        new FileInfo(walPath).Length.Should().Be(walLength);
        using var tail = OpenWalCopy(databasePath);
        tail.ScanRecovery().LastValidFrameNumber.Should().Be(beforeRecovery.LastCommittedFrameNumber + 1);
    }

    [Test]
    [NonParallelizable]
    public void RecoveryCannotRepairATailAfterAnAdversarialCarrierReplacementAttempt()
    {
        RequireCoordinatorSupport();
        using var artifact = SqliteWalArtifact.Create();
        var databasePath = Path.Combine(artifact.WorkDirectory, "late-carrier-replacement.db");
        File.Copy(artifact.DatabasePath, databasePath);
        File.Copy(artifact.DatabasePath + "-wal", databasePath + "-wal");
        File.Copy(artifact.DatabasePath + "-shm", databasePath + "-shm");

        using var coordinator = SqliteWalWriterCheckpointCoordinator.Open(databasePath);
        using var before = OpenWalCopy(databasePath);
        var beforeRecovery = before.ScanRecovery();
        AppendValidUncommittedFrame(
            databasePath + "-wal",
            before,
            before.ReadFrame(beforeRecovery.LastCommittedFrameNumber));
        var walPath = databasePath + "-wal";
        var walLength = new FileInfo(walPath).Length;
        var sharedMemoryPath = databasePath + "-shm";
        var replacedCarrierPath = sharedMemoryPath + ".late-replaced";
        IOException? replacementFailure = null;

        SqliteWalWriterCheckpointCoordinator.BeforeDetachedTailRepairForTesting = () =>
        {
            try
            {
                File.Move(sharedMemoryPath, replacedCarrierPath);
                File.Copy(replacedCarrierPath, sharedMemoryPath);
            }
            catch (IOException exception)
            {
                replacementFailure = exception;
            }
        };

        try
        {
            if (OperatingSystem.IsWindows())
            {
                coordinator.Recover(TimeSpan.Zero).LastCommittedFrameNumber
                    .Should().Be(beforeRecovery.LastCommittedFrameNumber);
                replacementFailure.Should().NotBeNull(
                    "the recovery mapping denies delete sharing until its carrier-bound recovery leases are released");
                using var repaired = OpenWalCopy(databasePath);
                repaired.ScanRecovery().LastValidFrameNumber.Should().Be(beforeRecovery.LastCommittedFrameNumber);
            }
            else
            {
                Assert.Throws<InvalidDataException>(() => coordinator.Recover(TimeSpan.Zero));
                replacementFailure.Should().BeNull();
                new FileInfo(walPath).Length.Should().Be(walLength);
                using var tail = OpenWalCopy(databasePath);
                tail.ScanRecovery().LastValidFrameNumber.Should().Be(beforeRecovery.LastCommittedFrameNumber + 1);
            }
        }
        finally
        {
            SqliteWalWriterCheckpointCoordinator.BeforeDetachedTailRepairForTesting = null;
        }
    }

    [Test]
    [NonParallelizable]
    public void FailedCommitAfterCarrierReplacementDoesNotRepairThroughTheReplacementCarrier()
    {
        RequireCoordinatorSupport();
        using var artifact = SqliteWalArtifact.Create();
        var databasePath = Path.Combine(artifact.WorkDirectory, "failed-commit-carrier-replacement.db");
        File.Copy(artifact.DatabasePath, databasePath);
        File.Copy(artifact.DatabasePath + "-wal", databasePath + "-wal");
        File.Copy(artifact.DatabasePath + "-shm", databasePath + "-shm");

        using var coordinator = SqliteWalWriterCheckpointCoordinator.Open(databasePath);
        using var before = OpenWalCopy(databasePath);
        var beforeRecovery = before.ScanRecovery();
        beforeRecovery.LastCommittedDatabaseSizeInPages.Should().BeGreaterThanOrEqualTo(2);
        var page = before.ReadFrame(beforeRecovery.LastCommittedFrameNumber).PageData;
        var walPath = databasePath + "-wal";
        var originalLength = new FileInfo(walPath).Length;
        var sharedMemoryPath = databasePath + "-shm";
        var replacementPath = sharedMemoryPath + ".failed-commit-replacement";
        var replacementSucceeded = false;

        SqliteWalWriterCheckpointCoordinator.AfterDetachedWalFrameAppendForTesting = () =>
        {
            File.Move(sharedMemoryPath, replacementPath);
            File.Copy(replacementPath, sharedMemoryPath);
            replacementSucceeded = true;
            throw new IOException("Injected failure after the first detached WAL frame append.");
        };

        try
        {
            Assert.Throws<IOException>(
                () => coordinator.Commit(
                    [
                        new SqliteWalWritePage(1, page),
                        new SqliteWalWritePage(2, page),
                    ],
                    beforeRecovery.LastCommittedDatabaseSizeInPages,
                    TimeSpan.Zero));
        }
        finally
        {
            SqliteWalWriterCheckpointCoordinator.AfterDetachedWalFrameAppendForTesting = null;
        }

        if (OperatingSystem.IsWindows())
        {
            replacementSucceeded.Should().BeFalse(
                "the recovery mapping denies delete sharing for the commit writer lock's full lifetime");
            using var repaired = OpenWalCopy(databasePath);
            repaired.ScanRecovery().LastValidFrameNumber.Should().Be(beforeRecovery.LastCommittedFrameNumber);
            return;
        }

        replacementSucceeded.Should().BeTrue();
        new FileInfo(walPath).Length.Should().Be(
            originalLength + SqliteWalFrameHeader.Size + before.PageSize);
        using (var tail = OpenWalCopy(databasePath))
        {
            var recovery = tail.ScanRecovery();
            recovery.LastValidFrameNumber.Should().Be(beforeRecovery.LastCommittedFrameNumber + 1);
            recovery.LastCommittedFrameNumber.Should().Be(beforeRecovery.LastCommittedFrameNumber);
        }

        Assert.Throws<InvalidOperationException>(
            () => coordinator.Commit(
                [new SqliteWalWritePage(1, page)],
                beforeRecovery.LastCommittedDatabaseSizeInPages,
                TimeSpan.Zero));
    }

    [Test]
    [NonParallelizable]
    public void CanceledRecoveryReleasesRoleLocksForCrossProcessTakeover()
    {
        RequireCoordinatorSupport();
        using var artifact = SqliteWalArtifact.Create();
        using var coordinator = SqliteWalWriterCheckpointCoordinator.Open(artifact.DatabasePath);
        using var before = OpenWalCopy(artifact.DatabasePath);
        var beforeRecovery = before.ScanRecovery();
        AppendValidUncommittedFrame(
            artifact.DatabasePath + "-wal",
            before,
            before.ReadFrame(beforeRecovery.LastCommittedFrameNumber));

        using var recoveryBlocker = new CrossProcessLockHandle(
            artifact.WorkDirectory,
            artifact.DatabasePath + "-shm",
            RecoveryLockOffset,
            length: 1);
        using var cancellation = new CancellationTokenSource();
        var recovery = Task.Run(
            () => coordinator.Recover(Timeout.InfiniteTimeSpan, cancellation.Token));

        using (var writerProbe = new CrossProcessLockHandle(
                   artifact.WorkDirectory,
                   artifact.DatabasePath + "-shm",
                   WriteLockOffset,
                   length: 1,
                   holdLease: false))
        {
            writerProbe.Result.Should().Be("busy");
        }

        cancellation.Cancel();
        SpinWait.SpinUntil(
            () => recovery.IsCompleted,
            TimeSpan.FromSeconds(5)).Should().BeTrue(
            "recovery cancellation must not wait for a held lock");
        Assert.Throws<OperationCanceledException>(() => recovery.GetAwaiter().GetResult());

        recoveryBlocker.Dispose();
        using (var takeover = new CrossProcessLockHandle(
                   artifact.WorkDirectory,
                   artifact.DatabasePath + "-shm",
                   WriteLockOffset,
                   length: 8))
        {
            takeover.Result.Should().Be("acquired");
        }

        if (OperatingSystem.IsWindows())
        {
            coordinator.Recover(TimeSpan.Zero).LastCommittedFrameNumber.Should().Be(beforeRecovery.LastCommittedFrameNumber);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => coordinator.Recover(TimeSpan.Zero));
        }
    }

    [TestCase("torn")]
    [TestCase("corrupt")]
    [NonParallelizable]
    public void TornAndCorruptWalIndexPublicationsAreRebuiltFromTheAuthenticatedWal(string mutation)
    {
        RequireCoordinatorSupport();
        using var artifact = SqliteWalArtifact.Create();
        using (var mapping = ((ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance).OpenSharedMemory(
                   artifact.DatabasePath + "-shm",
                   FileOpenMode.OpenExisting))
        {
            var header = new byte[SqliteWalIndexHeader.Size];
            mapping.Read(SqliteWalIndexHeader.Size, header);
            if (mutation == "torn")
            {
                header[8] ^= 0x01;
                mapping.Write(SqliteWalIndexHeader.Size, header);
            }
            else
            {
                header[40] ^= 0x01;
                mapping.Write(SqliteWalIndexHeader.Size, header);
                mapping.Write(position: 0, header);
            }
            mapping.MemoryBarrier();
        }

        using var coordinator = SqliteWalWriterCheckpointCoordinator.Open(artifact.DatabasePath);
        coordinator.Checkpoint(SqliteWalCheckpointMode.Passive, TimeSpan.Zero).MaximumFrame.Should().BeGreaterThan(0);
    }

    [Test]
    [NonParallelizable]
    public void CorruptionBeforeThePublishedCommittedBoundaryIsRejectedFailClosed()
    {
        RequireCoordinatorSupport();
        using var artifact = SqliteWalArtifact.Create();
        using (var stream = new FileStream(
                   artifact.DatabasePath + "-wal",
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            stream.Position = SqliteWalHeader.Size + 16;
            var checksum = stream.ReadByte();
            checksum.Should().NotBe(-1);
            stream.Position = SqliteWalHeader.Size + 16;
            stream.WriteByte(unchecked((byte)(checksum ^ 0x01)));
            stream.Flush(flushToDisk: true);
        }

        Assert.Throws<InvalidDataException>(
            () => SqliteWalWriterCheckpointCoordinator.Open(artifact.DatabasePath));
    }

    [Test]
    [Category("ProcessWorker")]
    [NonParallelizable]
    public void CrossProcessSqliteWriterWorker()
    {
        var databasePath = Environment.GetEnvironmentVariable("TURSO_WAL_WRITER_CHECKPOINT_DATABASE_PATH");
        if (string.IsNullOrEmpty(databasePath))
            return;

        using var connection = new SqliteConnection(
            $"Data Source={databasePath};Mode=ReadWrite;Pooling=False");
        connection.Open();
        Execute(connection, "PRAGMA wal_autocheckpoint=0;");
        Execute(connection, "INSERT INTO data(value) VALUES ('writer-process');");
    }

    [Test]
    [Category("ProcessWorker")]
    [NonParallelizable]
    public void CrossProcessWriteLockWorker()
    {
        var lockPath = Environment.GetEnvironmentVariable("TURSO_WAL_WRITER_CHECKPOINT_LOCK_PATH");
        if (string.IsNullOrEmpty(lockPath))
            return;

        var offset = long.Parse(
            ReadWorkerValue("TURSO_WAL_WRITER_CHECKPOINT_LOCK_OFFSET"),
            CultureInfo.InvariantCulture);
        var length = long.Parse(
            ReadWorkerValue("TURSO_WAL_WRITER_CHECKPOINT_LOCK_LENGTH"),
            CultureInfo.InvariantCulture);
        var holdLease = bool.Parse(ReadWorkerValue("TURSO_WAL_WRITER_CHECKPOINT_LOCK_HOLD_LEASE"));
        var readyPath = ReadWorkerValue("TURSO_WAL_WRITER_CHECKPOINT_LOCK_READY_PATH");
        var releasePath = ReadWorkerValue("TURSO_WAL_WRITER_CHECKPOINT_LOCK_RELEASE_PATH");
        var resultPath = ReadWorkerValue("TURSO_WAL_WRITER_CHECKPOINT_LOCK_RESULT_PATH");
        var locks = new SqliteWalByteRangeLock(lockPath);
        try
        {
            using var lease = locks.AcquireExclusive(offset, length, TimeSpan.Zero);
            File.WriteAllText(resultPath, "acquired");
            File.WriteAllText(readyPath, string.Empty);
            if (holdLease)
                WaitForFile(releasePath, TimeSpan.FromSeconds(60), "The writer-lock worker was not released.");
        }
        catch (SqliteWalByteRangeLockBusyException)
        {
            File.WriteAllText(resultPath, "busy");
            File.WriteAllText(readyPath, string.Empty);
        }
    }

    private static void RunSqliteWriterWorker(string databasePath)
    {
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
            "--TestCaseFilter:FullyQualifiedName=Ahtola.Tests.SqliteWalWriterCheckpointCoordinatorTests."
            + nameof(CrossProcessSqliteWriterWorker));
        startInfo.Environment["TURSO_WAL_WRITER_CHECKPOINT_DATABASE_PATH"] = databasePath;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the SQLite WAL writer worker.");
        var output = process.StandardOutput.ReadToEnd();
        output += process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, $"writer output:{Environment.NewLine}{output}");
    }

    private static void Execute(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static void SimulatePeerWalWrap(string databasePath)
    {
        var walPath = databasePath + "-wal";
        var shmPath = databasePath + "-shm";
        var fileSystem = new SqlitePagerPhysicalFileSystem(PhysicalFileSystem.Instance);
        using var wal = SqliteWalFile.Open(fileSystem, walPath);
        using var mapping = ((ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance).OpenSharedMemory(
            shmPath,
            FileOpenMode.OpenExisting);
        var index = new SqliteWalIndexSharedMemory(mapping);
        var region = index.ReadValidatedHeader(wal);
        region.Header.MaximumFrame.Should().BeGreaterThan(0);

        wal.ResetAfterDurableCheckpoint(publishCheckpointedRecoveryMarker: true);
        index.ResetAfterDurableRestart(
            region.Header.WithRestartedWal(
                region.Header.DatabasePageCount,
                wal.Header.Salt1,
                wal.Header.Salt2));
    }

    private static SqliteWalFile OpenWalCopy(string databasePath)
    {
        var fileSystem = new InMemoryFileSystem();
        using (var copy = fileSystem.OpenFile("main.db-wal", FileOpenMode.CreateNew))
            copy.Write(position: 0, ReadAllBytes(databasePath + "-wal"));
        return SqliteWalFile.Open(fileSystem, "main.db-wal", readOnly: true);
    }

    private static byte[] ReadAllBytes(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void AppendValidUncommittedFrame(
        string walPath,
        SqliteWalFile wal,
        SqliteWalFrame committedFrame)
    {
        var page = committedFrame.PageData.ToArray();
        page[^1] ^= 0x7B;
        var frame = new byte[SqliteWalFrameHeader.Size + wal.PageSize];
        var frameHeader = new SqliteWalFrameHeader(
            committedFrame.Header.PageNumber,
            0,
            wal.Header.Salt1,
            wal.Header.Salt2,
            0,
            0);
        frameHeader.WriteTo(frame.AsSpan(0, SqliteWalFrameHeader.Size));
        page.CopyTo(frame, SqliteWalFrameHeader.Size);
        var initialChecksum = SqliteWalChecksum.Calculate(
            frame.AsSpan(0, 8),
            wal.Header.ChecksumByteOrder,
            committedFrame.Header.Checksum1,
            committedFrame.Header.Checksum2);
        var checksum = SqliteWalChecksum.Calculate(
            frame.AsSpan(SqliteWalFrameHeader.Size),
            wal.Header.ChecksumByteOrder,
            initialChecksum.First,
            initialChecksum.Second);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(16, sizeof(uint)), checksum.First);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(20, sizeof(uint)), checksum.Second);

        using var stream = new FileStream(
            walPath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        stream.Position = stream.Length;
        stream.Write(frame);
        stream.Flush(flushToDisk: true);
    }

    private static void WriteUInt32Native(ISqliteWalSharedMemoryMapping mapping, long position, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        WriteUInt32Native(bytes, position: 0, value);
        mapping.Write(position, bytes);
    }

    private static void WriteUInt32Native(Span<byte> destination, int position, uint value)
    {
        if (SqliteWalIndexHeader.NativeByteOrder == SqliteWalIndexByteOrder.LittleEndian)
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(position, sizeof(uint)), value);
        else
            BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(position, sizeof(uint)), value);
    }

    private static void RewriteIndexHeaderChecksum(byte[] header)
    {
        var checksumByteOrder = SqliteWalIndexHeader.NativeByteOrder == SqliteWalIndexByteOrder.LittleEndian
            ? SqliteWalChecksumByteOrder.LittleEndian
            : SqliteWalChecksumByteOrder.BigEndian;
        var checksum = SqliteWalChecksum.Calculate(header.AsSpan(0, 40), checksumByteOrder);
        WriteUInt32Native(header, position: 40, checksum.First);
        WriteUInt32Native(header, position: 44, checksum.Second);
    }

    private static string ReadWorkerValue(string name)
        => Environment.GetEnvironmentVariable(name)
           ?? throw new InvalidOperationException($"The WAL writer/checkpoint worker is missing '{name}'.");

    private static void WaitForFile(string path, TimeSpan timeout, string failureMessage, Process? worker = null, Func<string>? output = null)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (worker?.HasExited == true)
            {
                worker.WaitForExit();
                Assert.Fail($"{failureMessage}{Environment.NewLine}{output?.Invoke()}");
            }
            if (stopwatch.Elapsed >= timeout)
            {
                worker?.Kill(entireProcessTree: true);
                Assert.Fail($"{failureMessage}{Environment.NewLine}{output?.Invoke()}");
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(10));
        }
    }

    private static bool SupportsCoordinator
        => OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && Environment.Is64BitProcess) || OperatingSystem.IsMacOS();

    private static void RequireCoordinatorSupport()
    {
        if (!SupportsCoordinator)
        {
            Assert.Ignore(
                "Detached SQLite WAL writer/checkpoint coordination is supported only on Windows, 64-bit Linux, and macOS.");
        }
    }

    private sealed class SqliteWalArtifact : IDisposable
    {
        private SqliteWalArtifact(string workDirectory, string databasePath, SqliteConnection connection)
        {
            WorkDirectory = workDirectory;
            DatabasePath = databasePath;
            Connection = connection;
        }

        internal string WorkDirectory { get; }

        internal string DatabasePath { get; }

        private SqliteConnection Connection { get; }

        internal static SqliteWalArtifact Create()
        {
            var workDirectory = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "sqlite-wal-writer-checkpoint",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDirectory);
            var databasePath = Path.Combine(workDirectory, "main.db");
            var connection = new SqliteConnection(
                $"Data Source={databasePath};Mode=ReadWriteCreate;Pooling=False");
            try
            {
                connection.Open();
                Execute(connection, "PRAGMA page_size=512;");
                Execute(connection, "VACUUM;");
                Execute(connection, "PRAGMA journal_mode=WAL;");
                Execute(connection, "PRAGMA wal_autocheckpoint=0;");
                Execute(connection, "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT NOT NULL);");
                Execute(connection, "INSERT INTO data(value) VALUES ('one'), ('two'), ('three');");
                Execute(connection, "UPDATE data SET value = 'two-updated' WHERE id = 2;");
                Execute(connection, "CREATE INDEX data_value ON data(value);");
                return new SqliteWalArtifact(workDirectory, databasePath, connection);
            }
            catch
            {
                connection.Dispose();
                if (Directory.Exists(workDirectory))
                    Directory.Delete(workDirectory, recursive: true);
                throw;
            }
        }

        public void Dispose()
        {
            Connection.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(WorkDirectory))
                Directory.Delete(WorkDirectory, recursive: true);
        }
    }

    private sealed class CrossProcessLockHandle : IDisposable
    {
        private readonly Process _worker;
        private readonly string _releasePath;
        private readonly string _resultPath;
        private readonly StringBuilder _output = new();
        private readonly bool _holdsLease;
        private bool _disposed;
        private bool _released;

        internal CrossProcessLockHandle(
            string workDirectory,
            string lockPath,
            long offset,
            long length,
            bool holdLease = true)
        {
            var token = Guid.NewGuid().ToString("N");
            var readyPath = Path.Combine(workDirectory, $"wal-write-ready-{token}");
            _releasePath = Path.Combine(workDirectory, $"wal-write-release-{token}");
            _resultPath = Path.Combine(workDirectory, $"wal-write-result-{token}");
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
                "--TestCaseFilter:FullyQualifiedName=Ahtola.Tests.SqliteWalWriterCheckpointCoordinatorTests."
                + nameof(CrossProcessWriteLockWorker));
            startInfo.Environment["TURSO_WAL_WRITER_CHECKPOINT_LOCK_PATH"] = lockPath;
            startInfo.Environment["TURSO_WAL_WRITER_CHECKPOINT_LOCK_OFFSET"] = offset.ToString(CultureInfo.InvariantCulture);
            startInfo.Environment["TURSO_WAL_WRITER_CHECKPOINT_LOCK_LENGTH"] = length.ToString(CultureInfo.InvariantCulture);
            startInfo.Environment["TURSO_WAL_WRITER_CHECKPOINT_LOCK_HOLD_LEASE"] = holdLease.ToString();
            startInfo.Environment["TURSO_WAL_WRITER_CHECKPOINT_LOCK_READY_PATH"] = readyPath;
            startInfo.Environment["TURSO_WAL_WRITER_CHECKPOINT_LOCK_RELEASE_PATH"] = _releasePath;
            startInfo.Environment["TURSO_WAL_WRITER_CHECKPOINT_LOCK_RESULT_PATH"] = _resultPath;
            _holdsLease = holdLease;

            _worker = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start the WAL writer-lock worker.");
            _worker.OutputDataReceived += AppendOutput;
            _worker.ErrorDataReceived += AppendOutput;
            _worker.BeginOutputReadLine();
            _worker.BeginErrorReadLine();
            WaitForFile(
                readyPath,
                TimeSpan.FromSeconds(60),
                "The WAL writer-lock worker did not report readiness.",
                _worker,
                DrainOutput);
        }

        internal string Result => File.ReadAllText(_resultPath);

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            try
            {
                if (_holdsLease && !_released)
                {
                    File.WriteAllText(_releasePath, string.Empty);
                    _released = true;
                }
                if (!_worker.WaitForExit(TimeSpan.FromSeconds(60)))
                {
                    _worker.Kill(entireProcessTree: true);
                    Assert.Fail(
                        "The WAL writer-lock worker did not exit within 60 seconds:"
                        + Environment.NewLine
                        + DrainOutput());
                }

                _worker.WaitForExit();
                _worker.ExitCode.Should().Be(0, $"worker output:{Environment.NewLine}{DrainOutput()}");
            }
            finally
            {
                _worker.Dispose();
            }
        }

        private void AppendOutput(object sender, DataReceivedEventArgs args)
        {
            if (args.Data is null)
                return;

            lock (_output)
                _output.AppendLine(args.Data);
        }

        private string DrainOutput()
        {
            lock (_output)
                return _output.ToString();
        }
    }
}
