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
    internal const string StagingSuffix = ".ahtola-replica-replacement.tmp";

    private const int MaximumIntentLength = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string GetBackupPath(string databasePath) => databasePath + BackupSuffix;

    internal static string GetDisplacedPath(string databasePath) => databasePath + DisplacedSuffix;

    internal static bool HasArtifacts(string databasePath)
        => GetArtifactPaths(databasePath).Any(File.Exists);

    internal static IReadOnlyList<string> GetArtifactPaths(string databasePath) =>
    [
        databasePath + IntentSuffix,
        databasePath + StagingSuffix,
        GetBackupPath(databasePath),
        GetDisplacedPath(databasePath),
    ];

    internal static void DeleteArtifacts(string databasePath)
    {
        foreach (var path in GetArtifactPaths(databasePath))
            DeleteIfExists(path);
    }

    internal static void Prepare(string databasePath, string replacementPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacementPath);

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

        var intent = new ReplacementIntent(
            ComputeSha256(databasePath),
            ComputeSha256(replacementPath),
            ComputeSha256(databasePath + ManagedReplicaBootstrapper.MetadataSuffix));
        var intentPath = databasePath + IntentSuffix;
        var stagingPath = databasePath + StagingSuffix;
        var bytes = StrictUtf8.GetBytes(
            "version=1\n"
            + $"backup={Path.GetFileName(GetBackupPath(databasePath))}\n"
            + $"displaced={Path.GetFileName(GetDisplacedPath(databasePath))}\n"
            + $"original_sha256={intent.OriginalDatabaseSha256}\n"
            + $"replacement_sha256={intent.ReplacementDatabaseSha256}\n"
            + $"metadata_sha256={intent.OriginalMetadataSha256}\n");

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
    }

    internal static void CompletePublication(string databasePath)
    {
        var intent = Read(databasePath);
        _ = ManagedReplicaBootstrapper.LoadMetadata(databasePath)
            ?? throw new InvalidDataException(
                "Managed embedded replica replacement publication has no metadata.");
        ValidateCurrentDatabase(databasePath, intent.ReplacementDatabaseSha256);
        if (string.Equals(
                ComputeSha256(databasePath + ManagedReplicaBootstrapper.MetadataSuffix),
                intent.OriginalMetadataSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Managed embedded replica replacement metadata does not describe the published database.");
        }

        DeleteIfExists(GetBackupPath(databasePath));
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
        if (string.Equals(
                ComputeSha256(databasePath + ManagedReplicaBootstrapper.MetadataSuffix),
                intent.OriginalMetadataSha256,
                StringComparison.Ordinal))
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
        DeleteIfExists(GetBackupPath(databasePath));
        DeleteIfExists(GetDisplacedPath(databasePath));
        DeleteIfExists(databasePath + StagingSuffix);
        DeleteIfExists(databasePath + IntentSuffix);
        ManagedReplicaFaultInjection.Hit(
            ManagedReplicaDurableBoundary.MainFileRollbackIntentRetired);
    }

    internal static void Recover(string databasePath)
    {
        var intentPath = databasePath + IntentSuffix;
        var stagingPath = databasePath + StagingSuffix;
        var backupPath = GetBackupPath(databasePath);
        var displacedPath = GetDisplacedPath(databasePath);
        if (!File.Exists(intentPath))
        {
            if (File.Exists(backupPath) || File.Exists(displacedPath))
            {
                throw new InvalidDataException(
                    "Managed embedded replica replacement recovery found a backup without its durable intent.");
            }
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
            CompleteRollback(databasePath);
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
            ManagedReplicaApplyLock.ReplaceMainFile(
                replacementLock,
                backupPath,
                databasePath,
                displacedPath,
                static () => { });
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
        if (lines.Length != 7
            || lines[0] != "version=1"
            || !string.Equals(
                lines[1],
                $"backup={Path.GetFileName(GetBackupPath(databasePath))}",
                filenameComparison)
            || !string.Equals(
                lines[2],
                $"displaced={Path.GetFileName(GetDisplacedPath(databasePath))}",
                filenameComparison)
            || !lines[3].StartsWith("original_sha256=", StringComparison.Ordinal)
            || !lines[4].StartsWith("replacement_sha256=", StringComparison.Ordinal)
            || !lines[5].StartsWith("metadata_sha256=", StringComparison.Ordinal)
            || lines[6].Length != 0)
        {
            throw InvalidIntent();
        }

        return new ReplacementIntent(
            ParseSha256(lines[3]["original_sha256=".Length..]),
            ParseSha256(lines[4]["replacement_sha256=".Length..]),
            ParseSha256(lines[5]["metadata_sha256=".Length..]));
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

    private static void ValidateCurrentDatabase(string databasePath, string expectedSha256)
        => ValidateFile(databasePath, expectedSha256, "database");

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
        string OriginalMetadataSha256);
}
