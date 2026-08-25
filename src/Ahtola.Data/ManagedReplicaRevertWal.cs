using System.Buffers.Binary;
using System.Security.Cryptography;
using Ahtola.Core.Storage;

namespace Ahtola;

internal static class ManagedReplicaRevertWal
{
    internal const string Suffix = "-wal-revert";

    internal static SqliteCheckpointResult CaptureAndCheckpoint(
        string databasePath,
        ManagedReplicaBootstrapper.ManagedReplicaMetadata metadata,
        SqlitePager pager,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        ArgumentNullException.ThrowIfNull(pager);
        EnsureSynchronizationReady(databasePath, metadata);

        var stagingPath = CreateStagingPath(databasePath, "capture");
        try
        {
            return pager.CheckpointToMainStoreAndResetWal(
                capture =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var state = StageCapture(databasePath, stagingPath, capture);
                    ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.RevertWalStaged);
                    cancellationToken.ThrowIfCancellationRequested();

                    File.Move(stagingPath, databasePath + Suffix, overwrite: false);
                    ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.RevertWalPublished);
                    cancellationToken.ThrowIfCancellationRequested();

                    var metadataStagingPath = CreateStagingPath(databasePath, "metadata");
                    try
                    {
                        ManagedReplicaBootstrapper.WriteMetadata(
                            metadataStagingPath,
                            databasePath + ManagedReplicaBootstrapper.MetadataSuffix,
                            metadata with { RevertState = state });
                    }
                    finally
                    {
                        DeleteIfExists(metadataStagingPath);
                    }

                    ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.RevertMetadataPublished);
                    cancellationToken.ThrowIfCancellationRequested();
                });
        }
        finally
        {
            DeleteIfExists(stagingPath);
        }
    }

    internal static ManagedReplicaBootstrapper.ManagedReplicaMetadata PublishProtectedSnapshots(
        string databasePath,
        ManagedReplicaBootstrapper.ManagedReplicaMetadata metadata,
        string originalDatabasePath,
        string committedDatabasePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        ArgumentException.ThrowIfNullOrEmpty(originalDatabasePath);
        ArgumentException.ThrowIfNullOrEmpty(committedDatabasePath);
        EnsureSynchronizationReady(databasePath, metadata);

        var stagingPath = CreateStagingPath(databasePath, "capture");
        try
        {
            var state = StageFileCapture(stagingPath, originalDatabasePath, committedDatabasePath);
            ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.RevertWalStaged);
            cancellationToken.ThrowIfCancellationRequested();

            File.Move(stagingPath, databasePath + Suffix, overwrite: false);
            ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.RevertWalPublished);
            cancellationToken.ThrowIfCancellationRequested();

            var pending = metadata with
            {
                DatabaseSha256 = state.CommittedDatabaseSha256,
                RevertState = state,
            };
            WritePhaseMetadata(databasePath, pending);
            ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.RevertRemoteApplyIntentPublished);
            cancellationToken.ThrowIfCancellationRequested();

            var snapshots = ReadAndValidate(databasePath, state);
            using (PublishSnapshot(
                       databasePath,
                       state.CommittedDatabaseSizeInPages,
                       state.CommittedDatabaseSha256,
                       snapshots.CommittedPages,
                       snapshots.PageSize,
                       ManagedReplicaDurableBoundary.RevertCommittedRestoreStagedDatabase,
                       ManagedReplicaDurableBoundary.RevertCommittedRestoreDatabasePublished,
                       cancellationToken))
            {
                return TransitionPhase(
                    databasePath,
                    pending,
                    ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.CommittedReady,
                    ManagedReplicaDurableBoundary.RevertCommittedReadyMetadataPublished);
            }
        }
        finally
        {
            DeleteIfExists(stagingPath);
        }
    }

    /// <summary>
    /// Validates a pending recovery bundle and completes any interrupted checkpoint into the
    /// captured committed image. The conflict rollback image remains durable until the next push
    /// is either acknowledged or classified as a confirmed conflict.
    /// </summary>
    internal static ManagedReplicaBootstrapper.ManagedReplicaMetadata PrepareSynchronization(
        string databasePath,
        ManagedReplicaBootstrapper.ManagedReplicaMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        ManagedReplicaReplacementState.Recover(databasePath);
        CleanupTemporaryArtifacts(databasePath);
        if (metadata.RevertState is not { } state)
        {
            Retire(databasePath);
            return metadata;
        }

        var snapshots = ReadAndValidate(databasePath, state);
        switch (state.Phase)
        {
            case ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.Captured:
                ValidateCapturedSource(databasePath, state);
                metadata = TransitionPhase(
                    databasePath,
                    metadata,
                    ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.RestoreCommitted,
                    ManagedReplicaDurableBoundary.RevertCommittedRestoreIntentPublished);
                state = metadata.RevertState!.Value;
                break;
            case ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.RestoreOriginal:
                return FinishOriginalRestore(databasePath, metadata, state, snapshots, CancellationToken.None);
            case ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.CommittedReady:
            case ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.PushOutcomeUnknown:
                ValidateCommittedReady(databasePath, state);
                return metadata;
            case ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.RestoreCommitted:
                break;
            default:
                throw new InvalidDataException("Managed embedded replica checkpoint recovery phase is invalid.");
        }

        using (PublishSnapshot(
                   databasePath,
                   state.CommittedDatabaseSizeInPages,
                   state.CommittedDatabaseSha256,
                   snapshots.CommittedPages,
                   snapshots.PageSize,
                   ManagedReplicaDurableBoundary.RevertCommittedRestoreStagedDatabase,
                   ManagedReplicaDurableBoundary.RevertCommittedRestoreDatabasePublished,
                   CancellationToken.None))
        {
            return TransitionPhase(
                databasePath,
                metadata,
                ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.CommittedReady,
                ManagedReplicaDurableBoundary.RevertCommittedReadyMetadataPublished);
        }
    }

    internal static ManagedReplicaBootstrapper.ManagedReplicaMetadata MarkRemoteApplyStarted(
        string databasePath,
        ManagedReplicaBootstrapper.ManagedReplicaMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        if (metadata.RevertState is not { } state)
            return metadata;
        _ = ReadAndValidate(databasePath, state);
        if (state.Phase != ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.Captured)
        {
            throw new InvalidOperationException(
                "Managed embedded replica checkpoint recovery is not ready to publish a remote replacement.");
        }

        ValidateCapturedSource(databasePath, state);
        return TransitionPhase(
            databasePath,
            metadata,
            ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.RestoreCommitted,
            ManagedReplicaDurableBoundary.RevertRemoteApplyIntentPublished);
    }

    internal static ManagedReplicaBootstrapper.ManagedReplicaMetadata MarkPushStarted(
        string databasePath,
        ManagedReplicaBootstrapper.ManagedReplicaMetadata metadata,
        ReplicaLocalChangeBatch batch,
        long sourcePullGeneration)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        if (batch.Changes.Count == 0
            || batch.FirstSequence <= 0
            || batch.Watermark <= batch.FirstSequence
            || sourcePullGeneration < 0)
        {
            throw new ArgumentException("The protected replica push batch is empty or invalid.", nameof(batch));
        }

        var pushState = new ManagedReplicaBootstrapper.ManagedReplicaPushState(
            sourcePullGeneration,
            batch.FirstSequence,
            batch.Watermark);
        if (metadata.PushState is { } existing)
        {
            if (existing != pushState)
            {
                throw new InvalidDataException(
                    "Managed embedded replica push recovery references a different protected batch.");
            }

            if (metadata.RevertState is { } protectedState)
            {
                _ = ReadAndValidate(databasePath, protectedState);
                ValidateCommittedReady(databasePath, protectedState);
            }
            return metadata;
        }

        if (metadata.RevertState is { } state)
        {
            _ = ReadAndValidate(databasePath, state);
            if (state.Phase is not (
                    ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.CommittedReady
                    or ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.PushOutcomeUnknown))
            {
                throw new InvalidOperationException(
                    "Managed embedded replica checkpoint recovery is not ready to push local changes.");
            }
            ValidateCommittedReady(databasePath, state);
        }

        var updated = metadata with { PushState = pushState };
        WritePhaseMetadata(databasePath, updated);
        ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.ReplicaPushIntentPublished);
        return updated;
    }

    internal static ManagedReplicaBootstrapper.ManagedReplicaMetadata ClearPushIntent(
        string databasePath,
        ManagedReplicaBootstrapper.ManagedReplicaMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        if (metadata.PushState is null)
            return metadata;

        var updated = metadata with { PushState = null };
        WritePhaseMetadata(databasePath, updated);
        ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.ReplicaPushIntentRetired);
        return updated;
    }

    internal static void EnsureSynchronizationReady(
        string databasePath,
        ManagedReplicaBootstrapper.ManagedReplicaMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        CleanupTemporaryArtifacts(databasePath);
        EnsurePushRecoveryComplete(metadata);
        if (metadata.RevertState is not { } state)
        {
            Retire(databasePath);
            return;
        }

        _ = ReadAndValidate(databasePath, state);
        throw new InvalidOperationException(
            "Managed embedded replica has a pending checkpoint recovery bundle that must be "
            + "resolved before starting another pull or checkpoint.");
    }

    internal static void ValidateSynchronizationReady(
        string databasePath,
        ManagedReplicaBootstrapper.ManagedReplicaMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        EnsurePushRecoveryComplete(metadata);
        if (metadata.RevertState is not { } state)
            return;

        _ = ReadAndValidate(databasePath, state);
        throw new InvalidOperationException(
            "Managed embedded replica has a pending checkpoint recovery bundle that must be "
            + "resolved before starting another pull or checkpoint.");
    }

    internal static void EnsurePushRecoveryComplete(
        ManagedReplicaBootstrapper.ManagedReplicaMetadata metadata)
    {
        if (metadata.PushState.HasValue)
        {
            throw new InvalidOperationException(
                "Managed embedded replica has a pending push outcome that must be recovered "
                + "before publishing pulled or materialized state.");
        }
    }

    internal static ManagedReplicaBootstrapper.ManagedReplicaMetadata CompletePreparedCheckpoint(
        string databasePath,
        ManagedReplicaBootstrapper.ManagedReplicaMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        if (metadata.RevertState is not { } state)
            return metadata;

        _ = ReadAndValidate(databasePath, state);
        if (state.Phase is not (
                ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.CommittedReady
                or ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.PushOutcomeUnknown))
        {
            throw new InvalidOperationException(
                "Managed embedded replica checkpoint recovery is not ready to complete.");
        }
        ValidateCommittedReady(databasePath, state);
        if (!string.Equals(
                ComputeSha256(databasePath),
                state.CommittedDatabaseSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Managed embedded replica checkpoint completion does not match the captured committed image.");
        }

        var completed = metadata with
        {
            DatabaseSha256 = state.CommittedDatabaseSha256,
            RevertState = null,
        };
        ClearPendingMetadata(databasePath, completed, state.CommittedDatabaseSha256);
        ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.RevertRestoreMetadataPublished);
        Retire(databasePath);
        ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.RevertRetired);
        return completed;
    }

    /// <summary>
    /// Restores the exact database bytes protected by a pending checkpoint capture.
    /// The caller must hold exclusive managed-replica publication ownership.
    /// </summary>
    internal static void RestorePendingCheckpoint(
        string databasePath,
        ManagedReplicaBootstrapper.ManagedReplicaMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        CleanupTemporaryArtifacts(databasePath);
        if (metadata.RevertState is not { } state)
            throw new InvalidOperationException("Managed embedded replica metadata has no pending checkpoint revert capture.");

        var frames = ReadAndValidate(databasePath, state);
        cancellationToken.ThrowIfCancellationRequested();

        switch (state.Phase)
        {
            case ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.Captured:
                ValidateCapturedSource(databasePath, state);
                break;
            case ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.CommittedReady:
            case ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.PushOutcomeUnknown:
                ValidateCommittedReady(databasePath, state);
                break;
            case ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.RestoreCommitted:
                break;
            case ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.RestoreOriginal:
                _ = FinishOriginalRestore(databasePath, metadata, state, frames, cancellationToken);
                return;
            default:
                throw new InvalidOperationException(
                    "Managed embedded replica checkpoint recovery is not ready to restore the original image.");
        }

        metadata = TransitionPhase(
            databasePath,
            metadata,
            ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.RestoreOriginal,
            ManagedReplicaDurableBoundary.RevertConflictRestoreIntentPublished);
        _ = FinishOriginalRestore(
            databasePath,
            metadata,
            metadata.RevertState!.Value,
            frames,
            cancellationToken);
    }

    internal static void Retire(string databasePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        DeleteIfExists(databasePath + Suffix);
    }

    internal static void DeleteArtifacts(string databasePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        foreach (var path in GetArtifactPaths(databasePath))
            DeleteIfExists(path);
    }

    internal static IReadOnlyList<string> GetArtifactPaths(string databasePath) =>
    [
        databasePath + Suffix,
        CreateStagingPath(databasePath, "capture"),
        CreateStagingPath(databasePath, "metadata"),
        CreateStagingPath(databasePath, "restore"),
        CreateStagingPath(databasePath, "restore-backup"),
        CreateStagingPath(databasePath, "restore-metadata"),
        CreateStagingPath(databasePath, "phase-metadata"),
    ];

    private static ManagedReplicaBootstrapper.ManagedReplicaRevertState StageCapture(
        string databasePath,
        string stagingPath,
        SqliteCheckpointRevertCapture capture)
    {
        var expectedDatabaseLength = checked((long)capture.OriginalDatabaseSizeInPages * capture.PageSize);
        if (new FileInfo(databasePath).Length != expectedDatabaseLength)
        {
            throw new InvalidDataException(
                "Managed embedded replica database length does not match the checkpoint revert capture.");
        }

        var originalFileFingerprint = ComputeSha256(databasePath);
        Span<byte> saltBytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(saltBytes);
        var revertHeader = SqliteWalHeader.Create(
            capture.PageSize,
            BinaryPrimitives.ReadUInt32BigEndian(saltBytes),
            BinaryPrimitives.ReadUInt32BigEndian(saltBytes[4..]));
        using (var wal = SqliteWalFile.Create(PhysicalFileSystem.Instance, stagingPath, revertHeader))
        {
            using var originalSource = new RevertFrameSource(
                capture.OriginalDatabaseSizeInPages,
                capture.ReadOriginalPage);
            var originalLastFrame = wal.AppendFrames(
                originalSource,
                capture.OriginalDatabaseSizeInPages);
            var originalFingerprint = originalSource.CompleteFingerprint();
            if (originalLastFrame != originalSource.Count
                || !string.Equals(originalFingerprint, originalFileFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Managed embedded replica original checkpoint image changed during revert capture.");
            }

            using var committedSource = new RevertFrameSource(
                capture.CommittedDatabaseSizeInPages,
                capture.ReadCommittedPage);
            var committedLastFrame = wal.AppendFrames(
                committedSource,
                capture.CommittedDatabaseSizeInPages);
            var committedFingerprint = committedSource.CompleteFingerprint();
            var expectedLastFrame = checked((long)originalSource.Count + committedSource.Count);
            if (committedLastFrame != expectedLastFrame)
                throw new InvalidDataException("Managed embedded replica revert WAL frame count is invalid.");
            wal.Flush();

            var revertFingerprint = ComputeSha256(stagingPath);
            return new ManagedReplicaBootstrapper.ManagedReplicaRevertState(
                ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.Captured,
                capture.SourceWalWatermark,
                0,
                0,
                capture.SourceWatermarkFrame.DatabaseSizeInPages,
                capture.SourceWalHeader.CheckpointSequence,
                capture.SourceWalHeader.Salt1,
                capture.SourceWalHeader.Salt2,
                capture.SourceWatermarkFrame.Checksum1,
                capture.SourceWatermarkFrame.Checksum2,
                capture.OriginalDatabaseSizeInPages,
                checked((uint)originalSource.Count),
                capture.CommittedDatabaseSizeInPages,
                checked((uint)committedSource.Count),
                originalFingerprint,
                committedFingerprint,
                revertFingerprint);
        }
    }

    private static ManagedReplicaBootstrapper.ManagedReplicaRevertState StageFileCapture(
        string stagingPath,
        string originalDatabasePath,
        string committedDatabasePath)
    {
        var originalHeader = ReadDatabaseHeader(originalDatabasePath);
        var committedHeader = ReadDatabaseHeader(committedDatabasePath);
        if (originalHeader.PageSize != committedHeader.PageSize)
        {
            throw new InvalidDataException(
                "Managed embedded replica protected snapshots use different SQLite page sizes.");
        }

        var pageSize = originalHeader.PageSize;
        var originalPageCount = GetDatabasePageCount(originalDatabasePath, pageSize);
        var committedPageCount = GetDatabasePageCount(committedDatabasePath, pageSize);
        var originalFingerprint = ComputeSha256(originalDatabasePath);
        var committedFingerprint = ComputeSha256(committedDatabasePath);
        Span<byte> saltBytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(saltBytes);
        var revertHeader = SqliteWalHeader.Create(
            pageSize,
            BinaryPrimitives.ReadUInt32BigEndian(saltBytes),
            BinaryPrimitives.ReadUInt32BigEndian(saltBytes[4..]));
        using (var originalStream = OpenSnapshot(originalDatabasePath))
        using (var committedStream = OpenSnapshot(committedDatabasePath))
        using (var wal = SqliteWalFile.Create(PhysicalFileSystem.Instance, stagingPath, revertHeader))
        {
            using var originalSource = new RevertFrameSource(
                originalPageCount,
                pageNumber => ReadPage(originalStream, pageNumber, pageSize));
            var originalLastFrame = wal.AppendFrames(originalSource, originalPageCount);
            if (originalLastFrame != originalSource.Count
                || !string.Equals(
                    originalSource.CompleteFingerprint(),
                    originalFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Managed embedded replica original protected snapshot changed during capture.");
            }

            using var committedSource = new RevertFrameSource(
                committedPageCount,
                pageNumber => ReadPage(committedStream, pageNumber, pageSize));
            var committedLastFrame = wal.AppendFrames(committedSource, committedPageCount);
            if (committedLastFrame != checked((long)originalSource.Count + committedSource.Count)
                || !string.Equals(
                    committedSource.CompleteFingerprint(),
                    committedFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Managed embedded replica committed protected snapshot changed during capture.");
            }

            wal.Flush();
            return new ManagedReplicaBootstrapper.ManagedReplicaRevertState(
                ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.RestoreCommitted,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                originalPageCount,
                originalPageCount,
                committedPageCount,
                committedPageCount,
                originalFingerprint,
                committedFingerprint,
                ComputeSha256(stagingPath));
        }
    }

    private static SqliteDatabaseHeader ReadDatabaseHeader(string path)
    {
        Span<byte> bytes = stackalloc byte[SqliteDatabaseHeader.Size];
        using var stream = OpenSnapshot(path);
        stream.ReadExactly(bytes);
        return SqliteDatabaseHeader.Parse(bytes);
    }

    private static uint GetDatabasePageCount(string path, int pageSize)
    {
        var length = new FileInfo(path).Length;
        if (length <= 0 || length % pageSize != 0)
            throw new InvalidDataException("Managed embedded replica protected snapshot has an invalid length.");
        return checked((uint)(length / pageSize));
    }

    private static FileStream OpenSnapshot(string path)
        => new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: SqlitePageSize.Default,
            FileOptions.SequentialScan);

    private static ReadOnlyMemory<byte> ReadPage(FileStream stream, uint pageNumber, int pageSize)
    {
        var page = new byte[pageSize];
        stream.Position = checked((long)(pageNumber - 1) * pageSize);
        stream.ReadExactly(page);
        return page;
    }

    private static ValidatedRevertWal ReadAndValidate(
        string databasePath,
        ManagedReplicaBootstrapper.ManagedReplicaRevertState state)
    {
        var path = databasePath + Suffix;
        if (!File.Exists(path))
        {
            throw new InvalidDataException(
                "Managed embedded replica checkpoint revert metadata references a missing revert WAL sidecar.");
        }
        string fingerprint;
        try
        {
            fingerprint = ComputeSha256(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                "Managed embedded replica checkpoint revert WAL could not be read for validation.",
                exception);
        }
        if (!string.Equals(fingerprint, state.RevertWalSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Managed embedded replica checkpoint revert WAL failed its integrity check.");
        }

        try
        {
            using var wal = SqliteWalFile.Open(PhysicalFileSystem.Instance, path, readOnly: true);
            var recovery = wal.ScanRecovery();
            var totalFrameCount = checked(
                (long)state.OriginalRevertWalFrameCount
                + state.CommittedRevertWalFrameCount);
            if (!recovery.ReachedEndOfFile
                || recovery.LastValidFrameNumber != totalFrameCount
                || recovery.LastCommittedFrameNumber != totalFrameCount
                || recovery.LastCommittedDatabaseSizeInPages != state.CommittedDatabaseSizeInPages)
            {
                throw new InvalidDataException(
                    "Managed embedded replica checkpoint revert WAL has an invalid committed boundary.");
            }

            var originalPages = new List<SqliteCheckpointRevertPage>(
                checked((int)state.OriginalRevertWalFrameCount));
            var committedPages = new List<SqliteCheckpointRevertPage>(
                checked((int)state.CommittedRevertWalFrameCount));
            var originalPageNumbers = new HashSet<uint>();
            var committedPageNumbers = new HashSet<uint>();
            var frames = wal.ReadFrameRange(1, totalFrameCount);
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames[index];
                var originalImage = index < state.OriginalRevertWalFrameCount;
                var databaseSizeInPages = originalImage
                    ? state.OriginalDatabaseSizeInPages
                    : state.CommittedDatabaseSizeInPages;
                var pageNumbers = originalImage ? originalPageNumbers : committedPageNumbers;
                if (frame.Header.PageNumber > databaseSizeInPages
                    || !pageNumbers.Add(frame.Header.PageNumber))
                {
                    throw new InvalidDataException(
                        "Managed embedded replica checkpoint revert WAL contains an invalid or duplicate page.");
                }

                var expectedCommit = index == state.OriginalRevertWalFrameCount - 1
                                     || index == frames.Count - 1;
                if (frame.Header.IsCommit != expectedCommit
                    || frame.Header.IsCommit
                    && frame.Header.DatabaseSizeInPages != databaseSizeInPages)
                {
                    throw new InvalidDataException(
                        "Managed embedded replica checkpoint revert WAL contains an invalid snapshot boundary.");
                }

                var page = new SqliteCheckpointRevertPage(frame.Header.PageNumber, frame.PageData);
                if (originalImage)
                    originalPages.Add(page);
                else
                    committedPages.Add(page);
            }

            return new ValidatedRevertWal(wal.PageSize, originalPages, committedPages);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                "Managed embedded replica checkpoint revert WAL could not be validated.",
                exception);
        }
    }

    private static void ValidateCapturedSource(
        string databasePath,
        ManagedReplicaBootstrapper.ManagedReplicaRevertState state)
    {
        if (File.Exists(databasePath + "-journal")
            && new FileInfo(databasePath + "-journal").Length > 0)
        {
            throw new InvalidOperationException(
                "Managed embedded replica checkpoint revert was rejected because newer local database activity exists.");
        }
        if (File.Exists(databasePath + "-wal")
            && new FileInfo(databasePath + "-wal").Length > SqliteWalHeader.Size)
        {
            ValidateUncheckpointedSourceWal(databasePath, state);
            return;
        }

        var fingerprint = ComputeSha256(databasePath);
        if (!string.Equals(fingerprint, state.OriginalDatabaseSha256, StringComparison.Ordinal)
            && !string.Equals(fingerprint, state.CommittedDatabaseSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Managed embedded replica database no longer matches either captured checkpoint image.");
        }
    }

    private static void ValidateCommittedReady(
        string databasePath,
        ManagedReplicaBootstrapper.ManagedReplicaRevertState state)
    {
        if ((File.Exists(databasePath + "-wal")
             && new FileInfo(databasePath + "-wal").Length > SqliteWalHeader.Size)
            || (File.Exists(databasePath + "-journal")
                && new FileInfo(databasePath + "-journal").Length > 0))
        {
            throw new InvalidOperationException(
                "Managed embedded replica checkpoint recovery was rejected because newer local database activity exists.");
        }

        if (!string.Equals(
                ComputeSha256(databasePath),
                state.CommittedDatabaseSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Managed embedded replica checkpoint recovery was rejected because the committed database image changed.");
        }
    }

    private static void ValidateUncheckpointedSourceWal(
        string databasePath,
        ManagedReplicaBootstrapper.ManagedReplicaRevertState state)
    {
        var sourceWalPath = databasePath + "-wal";
        if (!File.Exists(sourceWalPath)
            || new FileInfo(sourceWalPath).Length <= SqliteWalHeader.Size)
        {
            return;
        }

        using var wal = SqliteWalFile.Open(PhysicalFileSystem.Instance, sourceWalPath, readOnly: true);
        if (wal.Header.CheckpointSequence != state.SourceWalCheckpointSequence
            || wal.Header.Salt1 != state.SourceWalSalt1
            || wal.Header.Salt2 != state.SourceWalSalt2)
        {
            throw new InvalidDataException(
                "Managed embedded replica source WAL no longer matches the captured checkpoint watermark epoch.");
        }

        var recovery = wal.ScanRecovery();
        if (recovery.LastValidFrameNumber > state.SourceWalWatermark
            || recovery.LastCommittedFrameNumber > state.SourceWalWatermark)
        {
            throw new InvalidOperationException(
                "Managed embedded replica checkpoint revert was rejected because newer local WAL frames exist.");
        }
        if (!recovery.ReachedEndOfFile
            || recovery.LastValidFrameNumber != state.SourceWalWatermark
            || recovery.LastCommittedFrameNumber != state.SourceWalWatermark
            || recovery.LastCommittedDatabaseSizeInPages != state.SourceDatabaseSizeInPages)
        {
            throw new InvalidDataException(
                "Managed embedded replica source WAL no longer matches the captured checkpoint watermark.");
        }

        var watermark = wal.ReadFrame(state.SourceWalWatermark).Header;
        if (watermark.Checksum1 != state.SourceWalChecksum1
            || watermark.Checksum2 != state.SourceWalChecksum2
            || watermark.DatabaseSizeInPages != state.SourceDatabaseSizeInPages)
        {
            throw new InvalidDataException(
                "Managed embedded replica source WAL checkpoint watermark failed its integrity check.");
        }
    }

    private static void ClearPendingMetadata(
        string databasePath,
        ManagedReplicaBootstrapper.ManagedReplicaMetadata metadata,
        string databaseSha256)
    {
        var metadataStagingPath = CreateStagingPath(databasePath, "restore-metadata");
        try
        {
            ManagedReplicaBootstrapper.WriteMetadata(
                metadataStagingPath,
                databasePath + ManagedReplicaBootstrapper.MetadataSuffix,
                metadata with { DatabaseSha256 = databaseSha256, RevertState = null });
        }
        finally
        {
            DeleteIfExists(metadataStagingPath);
        }
    }

    private static ManagedReplicaBootstrapper.ManagedReplicaMetadata TransitionPhase(
        string databasePath,
        ManagedReplicaBootstrapper.ManagedReplicaMetadata metadata,
        ManagedReplicaBootstrapper.ManagedReplicaRevertPhase phase,
        ManagedReplicaDurableBoundary boundary)
    {
        var state = metadata.RevertState
                    ?? throw new InvalidOperationException(
                        "Managed embedded replica metadata has no pending checkpoint revert capture.");
        var updated = metadata with
        {
            RevertState = state with
            {
                Phase = phase,
                AttemptedFirstSequence = phase == ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.PushOutcomeUnknown
                    ? state.AttemptedFirstSequence
                    : 0,
                AttemptedWatermark = phase == ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.PushOutcomeUnknown
                    ? state.AttemptedWatermark
                    : 0,
            },
        };
        WritePhaseMetadata(databasePath, updated);
        ManagedReplicaFaultInjection.Hit(boundary);
        return updated;
    }

    private static void WritePhaseMetadata(
        string databasePath,
        ManagedReplicaBootstrapper.ManagedReplicaMetadata updated)
    {
        var metadataStagingPath = CreateStagingPath(databasePath, "phase-metadata");
        try
        {
            ManagedReplicaBootstrapper.WriteMetadata(
                metadataStagingPath,
                databasePath + ManagedReplicaBootstrapper.MetadataSuffix,
                updated);
        }
        finally
        {
            DeleteIfExists(metadataStagingPath);
        }
    }

    private static ManagedReplicaBootstrapper.ManagedReplicaMetadata FinishOriginalRestore(
        string databasePath,
        ManagedReplicaBootstrapper.ManagedReplicaMetadata metadata,
        ManagedReplicaBootstrapper.ManagedReplicaRevertState state,
        ValidatedRevertWal frames,
        CancellationToken cancellationToken)
    {
        using (PublishSnapshot(
                   databasePath,
                   state.OriginalDatabaseSizeInPages,
                   state.OriginalDatabaseSha256,
                   frames.OriginalPages,
                   frames.PageSize,
                   ManagedReplicaDurableBoundary.RevertRestoreStagedDatabase,
                   ManagedReplicaDurableBoundary.RevertRestoreDatabasePublished,
                   cancellationToken))
        {
            var restored = metadata with
            {
                DatabaseSha256 = state.OriginalDatabaseSha256,
                RevertState = null,
            };
            ClearPendingMetadata(databasePath, restored, state.OriginalDatabaseSha256);
            ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.RevertRestoreMetadataPublished);
            cancellationToken.ThrowIfCancellationRequested();
            metadata = restored;
        }

        Retire(databasePath);
        ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.RevertRetired);
        return metadata;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string CreateStagingPath(string databasePath, string purpose)
        => string.Concat(databasePath, Suffix, ".", purpose, ".tmp");

    private static void CleanupTemporaryArtifacts(string databasePath)
    {
        foreach (var path in GetArtifactPaths(databasePath).Skip(1))
            DeleteIfExists(path);
    }

    private static void DeleteSqliteSidecars(string databasePath)
    {
        DeleteIfExists(databasePath + "-wal");
        DeleteIfExists(databasePath + "-shm");
        DeleteIfExists(databasePath + "-journal");
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static IDisposable PublishSnapshot(
        string databasePath,
        uint databaseSizeInPages,
        string expectedSha256,
        IReadOnlyList<SqliteCheckpointRevertPage> pages,
        int pageSize,
        ManagedReplicaDurableBoundary? stagedBoundary,
        ManagedReplicaDurableBoundary? publishedBoundary,
        CancellationToken cancellationToken)
    {
        var databaseStagingPath = CreateStagingPath(databasePath, "restore");
        var databaseBackupPath = ManagedReplicaReplacementState.GetBackupPath(databasePath);
        var displacedPath = ManagedReplicaReplacementState.GetDisplacedPath(databasePath);
        IDisposable? mainFileReplacementLock = null;
        var databaseInstalled = false;
        try
        {
            using (var stream = new FileStream(
                       databaseStagingPath,
                       FileMode.CreateNew,
                       FileAccess.ReadWrite,
                       FileShare.None,
                       bufferSize: pageSize,
                       FileOptions.WriteThrough))
            {
                stream.SetLength(checked((long)databaseSizeInPages * pageSize));
                foreach (var page in pages)
                {
                    stream.Position = checked((long)(page.PageNumber - 1) * pageSize);
                    stream.Write(page.PageData.Span);
                }
                stream.Flush(flushToDisk: true);
            }

            if (!string.Equals(
                    ComputeSha256(databaseStagingPath),
                    expectedSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Managed embedded replica checkpoint recovery did not reconstruct the exact captured database bytes.");
            }

            if (stagedBoundary is { } staged)
                ManagedReplicaFaultInjection.Hit(staged);
            cancellationToken.ThrowIfCancellationRequested();
            ManagedReplicaReplacementState.Recover(databasePath);
            mainFileReplacementLock = ManagedReplicaApplyLock.AcquireMainFileReplacementLock(
                databasePath,
                databaseStagingPath,
                cancellationToken);
            ManagedReplicaReplacementState.Prepare(databasePath, databaseStagingPath);
            DeleteSqliteSidecars(databasePath);
            ManagedReplicaApplyLock.ReplaceMainFile(
                mainFileReplacementLock,
                databaseStagingPath,
                databasePath,
                databaseBackupPath,
                () => databaseInstalled = true);
            if (OperatingSystem.IsWindows()
                && !string.Equals(
                    ComputeSha256(databasePath),
                    expectedSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Managed embedded replica checkpoint recovery changed during its Windows replacement lock handoff.");
            }
            if (publishedBoundary is { } published)
                ManagedReplicaFaultInjection.Hit(published);
            cancellationToken.ThrowIfCancellationRequested();
            return new SnapshotPublicationLease(
                mainFileReplacementLock,
                databaseStagingPath,
                databasePath);
        }
        catch
        {
            try
            {
                if (databaseInstalled && File.Exists(databaseBackupPath))
                {
                    ManagedReplicaApplyLock.RollBackMainFile(
                        mainFileReplacementLock,
                        databaseBackupPath,
                        databasePath,
                        displacedPath);
                    ManagedReplicaFaultInjection.Hit(
                        ManagedReplicaDurableBoundary.MainFileRollbackDatabaseRestored);
                    ManagedReplicaReplacementState.CompleteRollback(databasePath);
                }
            }
            finally
            {
                mainFileReplacementLock?.Dispose();
                DeleteIfExists(databaseStagingPath);
            }
            throw;
        }
    }

    private sealed class SnapshotPublicationLease(
        IDisposable? mainFileReplacementLock,
        string databaseStagingPath,
        string databasePath) : IDisposable
    {
        private IDisposable? _mainFileReplacementLock = mainFileReplacementLock;

        public void Dispose()
        {
            try
            {
                _ = ManagedReplicaReplacementState.TryCompletePublication(databasePath);
            }
            finally
            {
                Interlocked.Exchange(ref _mainFileReplacementLock, null)?.Dispose();
                DeleteIfExists(databaseStagingPath);
            }
        }
    }

    private sealed class RevertFrameSource(
        uint pageCount,
        Func<uint, ReadOnlyMemory<byte>> readPage)
        : ISqliteWalFrameSource, IDisposable
    {
        private IncrementalHash? _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private int _nextIndex;

        public int Count { get; } = checked((int)pageCount);

        public uint GetPageNumber(int index) => checked((uint)index + 1);

        public ReadOnlySpan<byte> GetPageImage(int index)
        {
            if (index != _nextIndex)
                throw new InvalidOperationException("Revert WAL pages must be consumed in order.");
            var page = readPage(GetPageNumber(index));
            _hash?.AppendData(page.Span);
            _nextIndex++;
            return page.Span;
        }

        public string CompleteFingerprint()
        {
            if (_nextIndex != Count || _hash is null)
                throw new InvalidOperationException("Revert WAL page capture is incomplete.");
            var fingerprint = Convert.ToHexString(_hash.GetHashAndReset());
            _hash.Dispose();
            _hash = null;
            return fingerprint;
        }

        public void Dispose()
        {
            _hash?.Dispose();
            _hash = null;
        }
    }

    private sealed record ValidatedRevertWal(
        int PageSize,
        IReadOnlyList<SqliteCheckpointRevertPage> OriginalPages,
        IReadOnlyList<SqliteCheckpointRevertPage> CommittedPages);
}
