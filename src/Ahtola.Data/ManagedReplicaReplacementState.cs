using System.Security.Cryptography;
using System.Text;

namespace Ahtola;

/// <summary>
/// Durable intent for an atomic managed-replica main-file replacement.
/// </summary>
internal static class ManagedReplicaReplacementState
{
    internal const string IntentSuffix = ".ahtola-replica-replacement";
    internal const string BackupSuffix = ".ahtola-replica-replacement.bak";
    internal const string DisplacedSuffix = ".ahtola-replica-replacement.displaced";
    internal const string OriginalWalSuffix = ".ahtola-replica-replacement.original-wal";
    internal const string RollbackSidecarsPreparedSuffix = ".ahtola-replica-replacement.rollback-sidecars";
    internal const string ReplacementWalSuffix = ".ahtola-replica-replacement.replacement-wal";
    internal const string ReplacementShmSuffix = ".ahtola-replica-replacement.replacement-shm";
    internal const string ReplacementJournalSuffix = ".ahtola-replica-replacement.replacement-journal";
    internal const string StagingSuffix = ".ahtola-replica-replacement.tmp";

    private const int MaximumIntentLength = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string GetBackupPath(string databasePath) => databasePath + BackupSuffix;

    internal static string GetDisplacedPath(string databasePath) => databasePath + DisplacedSuffix;

    internal static string GetOriginalWalPath(string databasePath) => databasePath + OriginalWalSuffix;

    internal static bool HasArtifacts(string databasePath)
        => GetArtifactPaths(databasePath).Any(File.Exists);

    internal static IReadOnlyList<string> GetArtifactPaths(string databasePath) =>
    [
        databasePath + IntentSuffix,
        databasePath + StagingSuffix,
        GetBackupPath(databasePath),
        GetDisplacedPath(databasePath),
        GetOriginalWalPath(databasePath),
        databasePath + RollbackSidecarsPreparedSuffix,
        databasePath + ReplacementWalSuffix,
        databasePath + ReplacementShmSuffix,
        databasePath + ReplacementJournalSuffix,
    ];

    internal static void DeleteArtifacts(string databasePath)
    {
        foreach (var path in GetArtifactPaths(databasePath))
            DeleteIfExists(path);
    }

    internal static void Prepare(string databasePath, string replacementPath)
        => Prepare(
            databasePath,
            replacementPath,
            ComputeSha256(databasePath + ManagedReplicaBootstrapper.MetadataSuffix));

    internal static void Prepare(
        string databasePath,
        string replacementPath,
        string replacementMetadataSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacementPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacementMetadataSha256);

        if (HasArtifacts(databasePath))
        {
            throw new InvalidDataException(
                "Managed embedded replica replacement intent cannot be prepared while recovery artifacts remain.");
        }
        if (!File.Exists(databasePath)
            || !File.Exists(replacementPath)
            || !File.Exists(databasePath + ManagedReplicaBootstrapper.MetadataSuffix))
        {
            throw new InvalidDataException(
                "Managed embedded replica replacement intent requires the database, replacement, and metadata files.");
        }
        if (File.Exists(databasePath + "-journal"))
        {
            throw new InvalidDataException(
                "Managed embedded replica replacement cannot capture a database with an active rollback journal.");
        }

        var originalWalPath = databasePath + "-wal";
        var originalWalBackupPath = GetOriginalWalPath(databasePath);
        string? originalWalSha256 = null;
        if (File.Exists(originalWalPath))
        {
            CopyFileDurably(originalWalPath, originalWalBackupPath);
            originalWalSha256 = ComputeSha256(originalWalBackupPath);
            ManagedReplicaFaultInjection.Hit(
                ManagedReplicaDurableBoundary.MainFileReplacementOriginalWalCaptured);
        }
        var intent = new ReplacementIntent(
            ComputeSha256(databasePath),
            ComputeSha256(replacementPath),
            ComputeSha256(databasePath + ManagedReplicaBootstrapper.MetadataSuffix),
            ParseSha256(replacementMetadataSha256),
            originalWalSha256);
        var intentPath = databasePath + IntentSuffix;
        var stagingPath = databasePath + StagingSuffix;
        var bytes = StrictUtf8.GetBytes(
            "version=2\n"
            + $"backup={Path.GetFileName(GetBackupPath(databasePath))}\n"
            + $"displaced={Path.GetFileName(GetDisplacedPath(databasePath))}\n"
            + $"original_wal_sha256={intent.OriginalWalSha256 ?? "absent"}\n"
            + $"original_sha256={intent.OriginalDatabaseSha256}\n"
            + $"replacement_sha256={intent.ReplacementDatabaseSha256}\n"
            + $"original_metadata_sha256={intent.OriginalMetadataSha256}\n"
            + $"replacement_metadata_sha256={intent.ReplacementMetadataSha256}\n");

        try
        {
            using (var stream = new FileStream(
                       stagingPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(stagingPath, intentPath, overwrite: false);
        }
        finally
        {
            DeleteIfExists(stagingPath);
        }

        ManagedReplicaFaultInjection.Hit(
            ManagedReplicaDurableBoundary.MainFileReplacementIntentPublished);
        DeleteIfExists(databasePath + "-wal");
        DeleteIfExists(databasePath + "-shm");
    }

    internal static void CompletePublication(string databasePath)
    {
        var intent = Read(databasePath);
        var metadata = ManagedReplicaBootstrapper.LoadMetadata(databasePath)
            ?? throw new InvalidDataException(
                "Managed embedded replica replacement publication has no metadata.");
        ValidateCurrentDatabase(databasePath, intent.ReplacementDatabaseSha256);
        ValidateReplacementMetadata(databasePath, metadata, intent);

        DeleteIfExists(GetBackupPath(databasePath));
        DeleteIfExists(GetOriginalWalPath(databasePath));
        DeleteRollbackSidecarArtifacts(databasePath, includePreparedMarker: true);
        ManagedReplicaFaultInjection.Hit(
            ManagedReplicaDurableBoundary.MainFileReplacementBackupRetired);
        DeleteIfExists(GetDisplacedPath(databasePath));
        DeleteIfExists(databasePath + StagingSuffix);
        DeleteIfExists(databasePath + IntentSuffix);
        ManagedReplicaFaultInjection.Hit(
            ManagedReplicaDurableBoundary.MainFileReplacementIntentRetired);
    }

    internal static bool TryCompletePublication(string databasePath)
    {
        var intent = Read(databasePath);
        ValidateCurrentDatabase(databasePath, intent.ReplacementDatabaseSha256);
        var metadataSha256 = ComputeSha256(
            databasePath + ManagedReplicaBootstrapper.MetadataSuffix);
        if (string.Equals(metadataSha256, intent.OriginalMetadataSha256, StringComparison.Ordinal))
        {
            return false;
        }

        CompletePublication(databasePath);
        return true;
    }

    internal static void CompleteRollback(string databasePath)
    {
        var intent = Read(databasePath);
        ValidateCurrentDatabase(databasePath, intent.OriginalDatabaseSha256);
        ReconcileRollbackSidecars(databasePath, intent);
        ManagedReplicaFaultInjection.Hit(
            ManagedReplicaDurableBoundary.MainFileRollbackSidecarsRestored);
        DeleteIfExists(GetBackupPath(databasePath));
        DeleteIfExists(GetDisplacedPath(databasePath));
        DeleteIfExists(databasePath + StagingSuffix);
        DeleteIfExists(databasePath + IntentSuffix);
        DeleteIfExists(databasePath + RollbackSidecarsPreparedSuffix);
        ManagedReplicaFaultInjection.Hit(
            ManagedReplicaDurableBoundary.MainFileRollbackIntentRetired);
    }

    internal static void PrepareRollbackSidecars(string databasePath)
    {
        var liveSidecars = GetSqliteSidecarPaths(databasePath);
        var quarantinedSidecars = GetReplacementSidecarPaths(databasePath);
        for (var index = 0; index < liveSidecars.Count; index++)
        {
            var livePath = liveSidecars[index];
            if (!File.Exists(livePath))
                continue;

            var quarantinePath = quarantinedSidecars[index];
            if (File.Exists(quarantinePath))
            {
                // Re-entry can follow a crash after quarantine but before the main-file swap.
                // The caller holds the still-published replacement inode, so a recreated live
                // sidecar belongs to that same generation and is discarded with it.
                DeleteIfExists(livePath);
                continue;
            }
            File.Move(livePath, quarantinePath, overwrite: false);
        }

        var preparedPath = databasePath + RollbackSidecarsPreparedSuffix;
        if (!File.Exists(preparedPath))
            WriteDurableMarker(preparedPath);
        ManagedReplicaFaultInjection.Hit(
            ManagedReplicaDurableBoundary.MainFileRollbackSidecarsQuarantined);

        var intent = Read(databasePath);
        if (intent.OriginalWalSha256 is not { } expectedOriginalWal)
            return;

        var originalWalBackupPath = GetOriginalWalPath(databasePath);
        ValidateFile(originalWalBackupPath, expectedOriginalWal, "original WAL backup");
        CopyFileDurably(originalWalBackupPath, databasePath + "-wal");
    }

    internal static void Recover(string databasePath)
    {
        var intentPath = databasePath + IntentSuffix;
        var stagingPath = databasePath + StagingSuffix;
        var backupPath = GetBackupPath(databasePath);
        var displacedPath = GetDisplacedPath(databasePath);
        if (!File.Exists(intentPath))
        {
            if (File.Exists(backupPath)
                || File.Exists(displacedPath))
            {
                throw new InvalidDataException(
                    "Managed embedded replica replacement recovery found a backup without its durable intent.");
            }
            DeleteIfExists(GetOriginalWalPath(databasePath));
            DeleteRollbackSidecarArtifacts(databasePath, includePreparedMarker: true);
            DeleteIfExists(stagingPath);
            return;
        }

        ManagedReplicaFaultInjection.Hit(
            ManagedReplicaDurableBoundary.MainFileReplacementRecoveryStarted);
        var intent = Read(databasePath);
        var metadataPath = databasePath + ManagedReplicaBootstrapper.MetadataSuffix;
        _ = ManagedReplicaBootstrapper.LoadMetadata(databasePath)
            ?? throw new InvalidDataException(
                "Managed embedded replica replacement recovery metadata is missing.");
        if (!File.Exists(databasePath))
        {
            throw new InvalidDataException(
                "Managed embedded replica replacement recovery database is missing.");
        }
        if (File.Exists(backupPath))
            ValidateFile(backupPath, intent.OriginalDatabaseSha256, "backup");
        if (File.Exists(displacedPath))
            ValidateFile(displacedPath, intent.ReplacementDatabaseSha256, "displaced database");

        var currentSha256 = ComputeSha256(databasePath);
        if (string.Equals(currentSha256, intent.OriginalDatabaseSha256, StringComparison.Ordinal))
        {
            ValidateOriginalWalState(databasePath, intent, originalDatabaseInstalled: true);
            IDisposable? rollbackLock = null;
            try
            {
                rollbackLock = File.Exists(displacedPath)
                    ? ManagedReplicaApplyLock.AcquireMainFileReplacementLock(
                        databasePath,
                        displacedPath,
                        CancellationToken.None)
                    : ManagedReplicaApplyLock.AcquireMainFileReplacementLock(
                        databasePath,
                        CancellationToken.None);
                CompleteRollback(databasePath);
            }
            finally
            {
                rollbackLock?.Dispose();
            }
            return;
        }
        if (!string.Equals(currentSha256, intent.ReplacementDatabaseSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Managed embedded replica replacement recovery found an unrecognized database image.");
        }

        var metadataChanged = !string.Equals(
            ComputeSha256(metadataPath),
            intent.OriginalMetadataSha256,
            StringComparison.Ordinal);
        if (metadataChanged)
        {
            CompletePublication(databasePath);
            return;
        }
        if (!File.Exists(backupPath))
        {
            throw new InvalidDataException(
                "Managed embedded replica replacement recovery cannot restore the original database because its backup is missing.");
        }
        IDisposable? replacementLock = null;
        try
        {
            replacementLock = ManagedReplicaApplyLock.AcquireMainFileReplacementLock(
                databasePath,
                backupPath,
                CancellationToken.None);
            ValidateOriginalWalState(databasePath, intent, originalDatabaseInstalled: false);
            ManagedReplicaApplyLock.RollBackMainFile(
                replacementLock,
                backupPath,
                databasePath,
                displacedPath,
                () => PrepareRollbackSidecars(databasePath));
            ManagedReplicaFaultInjection.Hit(
                ManagedReplicaDurableBoundary.MainFileRollbackDatabaseRestored);
            CompleteRollback(databasePath);
        }
        finally
        {
            replacementLock?.Dispose();
        }
    }

    private static ReplacementIntent Read(string databasePath)
    {
        var intentPath = databasePath + IntentSuffix;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(intentPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                "Managed embedded replica replacement intent could not be read.",
                exception);
        }
        if (bytes.Length == 0 || bytes.Length > MaximumIntentLength)
            throw InvalidIntent();

        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "Managed embedded replica replacement intent is not valid UTF-8.",
                exception);
        }

        var lines = text.Split('\n');
        var filenameComparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (lines.Length != 9
            || lines[0] != "version=2"
            || !string.Equals(
                lines[1],
                $"backup={Path.GetFileName(GetBackupPath(databasePath))}",
                filenameComparison)
            || !string.Equals(
                lines[2],
                $"displaced={Path.GetFileName(GetDisplacedPath(databasePath))}",
                filenameComparison)
            || !lines[3].StartsWith("original_wal_sha256=", StringComparison.Ordinal)
            || !lines[4].StartsWith("original_sha256=", StringComparison.Ordinal)
            || !lines[5].StartsWith("replacement_sha256=", StringComparison.Ordinal)
            || !lines[6].StartsWith("original_metadata_sha256=", StringComparison.Ordinal)
            || !lines[7].StartsWith("replacement_metadata_sha256=", StringComparison.Ordinal)
            || lines[8].Length != 0)
        {
            throw InvalidIntent();
        }

        return new ReplacementIntent(
            ParseSha256(lines[4]["original_sha256=".Length..]),
            ParseSha256(lines[5]["replacement_sha256=".Length..]),
            ParseSha256(lines[6]["original_metadata_sha256=".Length..]),
            ParseSha256(lines[7]["replacement_metadata_sha256=".Length..]),
            ParseOptionalSha256(lines[3]["original_wal_sha256=".Length..]));
    }

    private static string ParseSha256(string value)
    {
        if (value.Length != 64)
            throw InvalidIntent();
        try
        {
            _ = Convert.FromHexString(value);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                "Managed embedded replica replacement intent contains an invalid SHA-256 value.",
                exception);
        }
        return value;
    }

    private static string? ParseOptionalSha256(string value)
        => string.Equals(value, "absent", StringComparison.Ordinal)
            ? null
            : ParseSha256(value);

    private static void ValidateCurrentDatabase(string databasePath, string expectedSha256)
        => ValidateFile(databasePath, expectedSha256, "database");

    private static void ValidateReplacementMetadata(
        string databasePath,
        ManagedReplicaBootstrapper.ManagedReplicaMetadata metadata,
        ReplacementIntent intent)
    {
        if (!string.Equals(
                ComputeSha256(databasePath + ManagedReplicaBootstrapper.MetadataSuffix),
                intent.ReplacementMetadataSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Managed embedded replica replacement metadata does not match the expected published generation.");
        }
    }

    private static void ValidateOriginalWalState(
        string databasePath,
        ReplacementIntent intent,
        bool originalDatabaseInstalled)
    {
        var backupPath = GetOriginalWalPath(databasePath);
        var walPath = databasePath + "-wal";
        if (originalDatabaseInstalled
            && File.Exists(databasePath + RollbackSidecarsPreparedSuffix)
            && File.Exists(walPath))
        {
            ValidateLiveRollbackSidecars(databasePath);
            return;
        }
        if (intent.OriginalWalSha256 is not { } expected)
        {
            if (File.Exists(backupPath))
                throw new InvalidDataException(
                    "Managed embedded replica replacement recovery found an unexpected original WAL backup.");
            return;
        }

        if (File.Exists(backupPath))
        {
            ValidateFile(backupPath, expected, "original WAL backup");
            return;
        }
        ValidateFile(walPath, expected, "restored original WAL");
    }

    private static void ReconcileRollbackSidecars(
        string databasePath,
        ReplacementIntent intent)
    {
        var displacedExists = File.Exists(GetDisplacedPath(databasePath));
        var walPath = databasePath + "-wal";
        var originalWalBackupPath = GetOriginalWalPath(databasePath);
        var sidecarsPrepared = File.Exists(databasePath + RollbackSidecarsPreparedSuffix);
        if (sidecarsPrepared)
        {
            ValidateLiveRollbackSidecars(databasePath);
            if (File.Exists(walPath))
            {
                DeleteIfExists(originalWalBackupPath);
            }
            else if (intent.OriginalWalSha256 is not null)
            {
                if (!File.Exists(originalWalBackupPath))
                {
                    throw new InvalidDataException(
                        "Managed embedded replica replacement rollback cannot restore the original WAL because its backup is missing.");
                }
                File.Move(originalWalBackupPath, walPath, overwrite: false);
            }
            else
            {
                DeleteIfExists(originalWalBackupPath);
            }

            DeleteRollbackSidecarArtifacts(databasePath, includePreparedMarker: false);
            return;
        }

        var sidecars = GetSqliteSidecarPaths(databasePath);
        var originalWalAlreadyRestored =
            intent.OriginalWalSha256 is { } expectedOriginalWal
            && !File.Exists(originalWalBackupPath)
            && File.Exists(walPath)
            && string.Equals(ComputeSha256(walPath), expectedOriginalWal, StringComparison.Ordinal);
        if (!displacedExists && File.Exists(databasePath + "-journal"))
        {
            throw new InvalidDataException(
                "Managed embedded replica replacement rollback found a rollback journal without the displaced replacement database.");
        }
        if (!displacedExists && File.Exists(walPath))
        {
            if (intent.OriginalWalSha256 is not { } expected
                || !string.Equals(ComputeSha256(walPath), expected, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Managed embedded replica replacement rollback found an unrecognized WAL without the displaced replacement database.");
            }
        }

        foreach (var path in sidecars)
        {
            if (!originalWalAlreadyRestored || !string.Equals(path, walPath, StringComparison.Ordinal))
                DeleteIfExists(path);
        }
        if (intent.OriginalWalSha256 is not null)
        {
            if (originalWalAlreadyRestored)
                return;
            if (!File.Exists(originalWalBackupPath))
            {
                throw new InvalidDataException(
                    "Managed embedded replica replacement rollback cannot restore the original WAL because its backup is missing.");
            }
            File.Move(originalWalBackupPath, walPath, overwrite: true);
        }
        else
        {
            DeleteIfExists(originalWalBackupPath);
        }
    }

    private static void ValidateLiveRollbackSidecars(string databasePath)
    {
        var walExists = File.Exists(databasePath + "-wal");
        var shmExists = File.Exists(databasePath + "-shm");
        var journalExists = File.Exists(databasePath + "-journal");
        if ((shmExists && !walExists) || (journalExists && (walExists || shmExists)))
        {
            throw new InvalidDataException(
                "Managed embedded replica replacement rollback found an incoherent restored-database sidecar set.");
        }
    }

    private static IReadOnlyList<string> GetSqliteSidecarPaths(string databasePath) =>
    [
        databasePath + "-wal",
        databasePath + "-shm",
        databasePath + "-journal",
    ];

    private static IReadOnlyList<string> GetReplacementSidecarPaths(string databasePath) =>
    [
        databasePath + ReplacementWalSuffix,
        databasePath + ReplacementShmSuffix,
        databasePath + ReplacementJournalSuffix,
    ];

    private static void DeleteRollbackSidecarArtifacts(string databasePath, bool includePreparedMarker)
    {
        foreach (var path in GetReplacementSidecarPaths(databasePath))
            DeleteIfExists(path);
        if (includePreparedMarker)
            DeleteIfExists(databasePath + RollbackSidecarsPreparedSuffix);
    }

    private static void ValidateFile(string path, string expectedSha256, string role)
    {
        if (!File.Exists(path)
            || !string.Equals(ComputeSha256(path), expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Managed embedded replica replacement recovery {role} is missing or corrupt.");
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void CopyFileDurably(string sourcePath, string destinationPath)
    {
        using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.WriteThrough);
        source.CopyTo(destination);
        destination.Flush(flushToDisk: true);
    }

    private static void WriteDurableMarker(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.WriteThrough);
        stream.Flush(flushToDisk: true);
    }

    private static InvalidDataException InvalidIntent()
        => new("Managed embedded replica replacement intent is invalid or corrupt.");

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private readonly record struct ReplacementIntent(
        string OriginalDatabaseSha256,
        string ReplacementDatabaseSha256,
        string OriginalMetadataSha256,
        string ReplacementMetadataSha256,
        string? OriginalWalSha256);
}
