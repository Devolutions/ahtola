using System.Text;

namespace Ahtola;

/// <summary>
/// The durable, replica-private record of one open push conflict. Its presence is authoritative:
/// while the file exists, ordinary, manual, and automatic synchronization refuse to push, exactly
/// as <see cref="ManagedReplicaRevertWal.EnsureSynchronizationReady"/> refuses while a checkpoint
/// recovery bundle is pending.
/// </summary>
/// <remarks>
/// <para>
/// The marker is a sibling sidecar rather than a new phase inside
/// <see cref="ManagedReplicaBootstrapper.ManagedReplicaRevertPhase"/>: the revert-WAL state
/// machine's transition graph is deliberately left untouched, and a conflict is orthogonal to it
/// (the revert bundle has already been restored and retired by the time a marker is written).
/// </para>
/// <para>
/// It is written with the same durability idiom as
/// <see cref="ManagedReplicaChangeJournal"/>: stage into a sibling temp file opened with
/// <see cref="FileOptions.WriteThrough"/>, flush to disk, then atomically replace. A crash can
/// therefore only ever leave the previous content or the new content, never a partial record.
/// Any content that does not validate fails closed with <see cref="InvalidDataException"/>.
/// </para>
/// </remarks>
internal readonly record struct ManagedReplicaConflictState(
    AhtolaReplicaConflictKind ConflictKind,
    string? RemoteErrorCode,
    long? ConflictingSequence,
    long BatchFirstSequence,
    long BatchWatermark,
    IReadOnlyList<long> UnresolvedSequences)
{
    internal const string Suffix = ".ahtola-replica-conflict";
    internal const string StagingSuffix = Suffix + ".staging.tmp";

    private const ulong Magic = 0x54_4C_46_4E_4F_43_4F_54; // "TOCONFLT"
    private const int Version = 1;
    private const int MaxStringBytes = 4096;

    internal static string GetPath(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        return databasePath + Suffix;
    }

    internal static string GetStagingPath(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        return databasePath + StagingSuffix;
    }

    internal static bool Exists(string databasePath) => File.Exists(GetPath(databasePath));

    /// <summary>
    /// Reads the durable marker, or returns <see langword="null"/> when no conflict is open.
    /// Corrupt or self-inconsistent content is never silently ignored.
    /// </summary>
    internal static ManagedReplicaConflictState? TryRead(string databasePath)
    {
        var path = GetPath(databasePath);

        // A staging file only ever exists as a leftover from an interrupted publish: the durable
        // marker is installed by an atomic replace, so the staging content was never adopted.
        // Clearing it here keeps the replica's on-disk footprint exactly the declared artifact set.
        DeleteStagingArtifact(GetStagingPath(databasePath), throwOnFailure: false);
        if (!File.Exists(path))
            return null;

        try
        {
            return ReadCore(path);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("Managed replica conflict marker is truncated.", exception);
        }
    }

    private static ManagedReplicaConflictState ReadCore(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        if (stream.Length < 8 || reader.ReadUInt64() != Magic)
            throw new InvalidDataException("Managed replica conflict marker has an unsupported format.");
        if (reader.ReadInt32() != Version)
            throw new InvalidDataException("Managed replica conflict marker has an unsupported format.");

        var kindValue = reader.ReadByte();
        if (kindValue > (byte)AhtolaReplicaConflictKind.SchemaChange)
            throw new InvalidDataException("Managed replica conflict marker has an unknown conflict kind.");
        var remoteErrorCode = ReadOptionalString(reader);
        var conflictingSequence = reader.ReadInt64();
        var batchFirstSequence = reader.ReadInt64();
        var batchWatermark = reader.ReadInt64();
        var unresolvedCount = reader.ReadInt32();

        if (batchFirstSequence <= 0
            || batchWatermark <= batchFirstSequence
            || unresolvedCount <= 0
            || unresolvedCount > batchWatermark - batchFirstSequence)
        {
            throw new InvalidDataException("Managed replica conflict marker has invalid batch state.");
        }

        if (conflictingSequence != -1
            && (conflictingSequence < batchFirstSequence || conflictingSequence >= batchWatermark))
        {
            throw new InvalidDataException(
                "Managed replica conflict marker references a sequence outside its recorded batch.");
        }

        var unresolved = new long[unresolvedCount];
        var previous = batchFirstSequence - 1;
        for (var i = 0; i < unresolvedCount; i++)
        {
            var sequence = reader.ReadInt64();
            if (sequence <= previous || sequence >= batchWatermark)
                throw new InvalidDataException("Managed replica conflict marker is not ordered.");
            unresolved[i] = sequence;
            previous = sequence;
        }

        if (stream.Position != stream.Length)
            throw new InvalidDataException("Managed replica conflict marker is malformed.");

        return new ManagedReplicaConflictState(
            (AhtolaReplicaConflictKind)kindValue,
            remoteErrorCode,
            conflictingSequence == -1 ? null : conflictingSequence,
            batchFirstSequence,
            batchWatermark,
            unresolved);
    }

    /// <summary>
    /// Durably publishes <paramref name="state"/>. Replacing an existing marker is only ever done
    /// to record a narrower set of still-unresolved sequences; the conflict identity itself is
    /// never rewritten.
    /// </summary>
    internal static void Write(string databasePath, ManagedReplicaConflictState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (state.UnresolvedSequences.Count == 0)
        {
            throw new InvalidOperationException(
                "Managed replica conflict marker requires at least one unresolved change; delete it instead.");
        }

        var path = GetPath(databasePath);
        var stagingPath = GetStagingPath(databasePath);
        try
        {
            DeleteStagingArtifact(stagingPath, throwOnFailure: true);
            using (var stream = new FileStream(
                       stagingPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write((byte)state.ConflictKind);
                WriteOptionalString(writer, state.RemoteErrorCode);
                writer.Write(state.ConflictingSequence ?? -1);
                writer.Write(state.BatchFirstSequence);
                writer.Write(state.BatchWatermark);
                writer.Write(state.UnresolvedSequences.Count);
                foreach (var sequence in state.UnresolvedSequences)
                    writer.Write(sequence);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
                File.Replace(stagingPath, path, destinationBackupFileName: null, ignoreMetadataErrors: false);
            else
                File.Move(stagingPath, path, overwrite: false);
            ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.ConflictMarkerPublished);
        }
        finally
        {
            if (File.Exists(stagingPath))
                File.Delete(stagingPath);
        }
    }

    /// <summary>
    /// Removes the marker. Only ever called after the replacement durable state (the rebased
    /// database plus its published metadata, or the discarded journal) is itself durable, so a
    /// crash can never clear the block without the resolution having landed.
    /// </summary>
    internal static void Delete(string databasePath)
    {
        var path = GetPath(databasePath);
        DeleteStagingArtifact(GetStagingPath(databasePath), throwOnFailure: false);
        if (File.Exists(path))
            File.Delete(path);
        ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.ConflictMarkerRetired);
    }

    internal static IReadOnlyList<string> GetArtifactPaths(string databasePath) =>
    [
        GetPath(databasePath),
        GetStagingPath(databasePath),
    ];

    private static void DeleteStagingArtifact(string stagingPath, bool throwOnFailure)
    {
        try
        {
            if (File.Exists(stagingPath))
                File.Delete(stagingPath);
        }
        catch (Exception exception) when (!throwOnFailure
                                          && exception is IOException or UnauthorizedAccessException)
        {
            // Reading the marker must never fail because a leftover staging file is momentarily
            // locked: the durable marker is authoritative on its own. Publication still deletes
            // the leftover with throwOnFailure, so a genuinely stuck artifact surfaces before
            // anything depends on writing over it.
        }
    }

    private static void WriteOptionalString(BinaryWriter writer, string? value)
    {
        if (value is null)
        {
            writer.Write(-1);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > MaxStringBytes)
            throw new InvalidDataException("Managed replica conflict marker string is too large.");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string? ReadOptionalString(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length == -1)
            return null;
        if (length < 0 || length > MaxStringBytes)
            throw new InvalidDataException("Managed replica conflict marker contains an invalid string.");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException("Managed replica conflict marker is truncated.");
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Managed replica conflict marker contains invalid UTF-8.", exception);
        }
    }
}
