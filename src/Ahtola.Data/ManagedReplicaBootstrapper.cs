using System.Buffers.Binary;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola;

internal static class ManagedReplicaBootstrapper
{
    internal const int PageSize = 4096;
    private const int MaxHeaderLength = 64 * 1024;
    private const int MaxPageMessageLength = PageSize + 1024;
    internal const string MetadataSuffix = ".ahtola-replica-meta";
    internal const string BootstrapStateSuffix = ".ahtola-replica-bootstrap";
    internal const string BaseSnapshotSuffix = ".ahtola-replica-base";
    private const string BaseSnapshotPreviousSuffix = ".ahtola-replica-base.previous";
    private const int MaxMetadataFileLength = 1024 * 1024;
    private const int MaxTableMapEntries = 100_000;
    private const int MaxStringBytes = 64 * 1024;
    private const int MaxLogicalBodyLength = 256 * 1024 * 1024;
    private static readonly byte[] SqliteHeader = "SQLite format 3\0"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly IReadOnlyDictionary<ulong, string> EmptyTableMap = new Dictionary<ulong, string>();

    /// <summary>
    /// Deletes a bootstrapped replica's durable artifacts: the main database file, its v3
    /// metadata sidecar, and any WAL/SHM/journal sidecars. Used to roll a bootstrap fully back
    /// when the mandatory post-bootstrap logical catch-up fails (see
    /// <see cref="ManagedReplicaConnectionHost"/>'s combined bootstrap+catch-up publication unit),
    /// so a subsequent open retries a clean bootstrap rather than observing a replica that is
    /// durably "bootstrapped" but has permanently skipped catch-up.
    /// </summary>
    internal static void DeleteBootstrappedReplicaFiles(string path)
    {
        DeletePublishedReplicaFiles(path);
        DeleteIfExists(path + BootstrapStateSuffix);
    }

    private static void DeletePublishedReplicaFiles(string path)
    {
        DeleteIfExists(path + MetadataSuffix);
        DeleteIfExists(path + ManagedReplicaPageMaterializingFileSystem.StateSuffix);
        DeleteIfExists(path + ManagedReplicaPageMaterializingFileSystem.OwnershipLockSuffix);
        DeleteIfExists(path + BaseSnapshotSuffix);
        DeleteIfExists(path + BaseSnapshotPreviousSuffix);
        ManagedReplicaRevertWal.DeleteArtifacts(path);
        DeleteStagingSidecars(path);
        // Keep the main file present until every sidecar is gone. Apply-lock aliases resolve an
        // existing target by physical identity; deleting it first would let a missing-path key
        // race the remainder of this cleanup while the original lease was still held.
        DeleteIfExists(path);
    }

    public static async Task BootstrapAsync(AhtolaReplicaOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        ManagedReplicaSupportMatrix.ValidateOptions(options);

        if (File.Exists(options.Path) && !File.Exists(options.Path + BootstrapStateSuffix))
        {
            throw new NotSupportedException(
                "Managed embedded replica bootstrap only installs a database at a missing replica path.");
        }

        await using var bootstrapLease = await ManagedReplicaApplyLock
            .AcquireExclusiveAsync(options.Path, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        using var publication = BootstrapPublication.Acquire(options.Path);
        if (publication.RequiresRecovery)
            DeletePublishedReplicaFiles(options.Path);

        if (File.Exists(options.Path))
        {
            throw new NotSupportedException(
                "Managed embedded replica bootstrap only installs a database at a missing replica path.");
        }

        if (!options.BootstrapIfEmpty)
        {
            throw new NotSupportedException(
                "Managed embedded replica bootstrap is disabled and the replica path does not contain an initialized managed database.");
        }

        var metadataPath = options.Path + MetadataSuffix;

        // Fast, unlocked pre-check: reject an obviously-unusable bootstrap target before paying
        // for a network round trip. Re-checked below, authoritatively, once the apply lease is
        // held -- two concurrent bootstraps of the same missing path could otherwise both pass
        // this check before either installs.
        EnsureBootstrapTargetIsMissing(options, metadataPath);

        publication.MarkInProgress();

        var directory = Path.GetDirectoryName(Path.GetFullPath(options.Path))!;
        var stagingPath = Path.Combine(
            directory,
            $".{Path.GetFileName(options.Path)}.bootstrap-{Guid.NewGuid():N}.tmp");
        var metadataStagingPath = Path.Combine(
            directory,
            $".{Path.GetFileName(metadataPath)}.bootstrap-{Guid.NewGuid():N}.tmp");
        var metadataWritePath = metadataStagingPath + ".write";

        var databaseInstalled = false;
        var metadataInstalled = false;
        var stateInstalled = false;
        try
        {
            var download = await DownloadDatabaseAsync(options, stagingPath, cancellationToken).ConfigureAwait(false);
            ValidateStagedDatabase(stagingPath, options.RemoteEncryption, download.IsPartial);

            IReadOnlyDictionary<ulong, string> tableMap;
            if (download.IsPartial)
            {
                var partialBootstrap = options.PartialBootstrap
                    ?? throw new InvalidOperationException("Partial bootstrap options are unavailable.");
                ManagedReplicaPageMaterializingFileSystem.InitializeState(
                    PhysicalFileSystem.Instance,
                    stagingPath,
                    download.Revision,
                    download.DatabasePages,
                    PageSize,
                    partialBootstrap.SegmentSize
                        ?? ManagedReplicaPageMaterializingFileSystem.DefaultSegmentSize,
                    download.MaterializedPageIds);
                using var materializingFileSystem = new ManagedReplicaPageMaterializingFileSystem(
                    PhysicalFileSystem.Instance,
                    stagingPath,
                    download.Revision,
                    new ManagedReplicaPullPageSource(options),
                    partialBootstrap.Prefetch);
                tableMap = RebuildTableMapFromSchema(stagingPath, materializingFileSystem);
            }
            else
            {
                tableMap = RebuildTableMapFromSchema(stagingPath, options.RemoteEncryption);
            }
            var remoteBaseSha256 = PublishInitialRemoteBaseSnapshot(options.Path, stagingPath);

            await WriteMetadataAsync(
                metadataWritePath,
                metadataStagingPath,
                download.Revision,
                ComputeDatabaseFingerprint(stagingPath),
                download.Protocol,
                tableMap,
                cancellationToken,
                remoteBaseSha256: remoteBaseSha256).ConfigureAwait(false);

            ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.BootstrapStagedDatabase);
            cancellationToken.ThrowIfCancellationRequested();
            ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.ReplicaApplyLockAcquired);
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                EnsureBootstrapTargetIsMissing(options, metadataPath);
                var stagedStatePath = stagingPath + ManagedReplicaPageMaterializingFileSystem.StateSuffix;
                if (download.IsPartial)
                {
                    File.Move(
                        stagedStatePath,
                        options.Path + ManagedReplicaPageMaterializingFileSystem.StateSuffix,
                        overwrite: false);
                    stateInstalled = true;
                }

                File.Move(metadataStagingPath, metadataPath, overwrite: false);
                metadataInstalled = true;
                ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.BootstrapSafetyStatePublished);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(stagingPath, options.Path, overwrite: false);
                databaseInstalled = true;

                ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.BootstrapDatabasePublished);
                cancellationToken.ThrowIfCancellationRequested();
                publication.MarkComplete(download.IsPartial);
            }
            catch
            {
                if (databaseInstalled || metadataInstalled || stateInstalled)
                    DeletePublishedReplicaFiles(options.Path);
                throw;
            }
        }
        finally
        {
            DeleteIfExists(stagingPath);
            DeleteStagingSidecars(stagingPath);
            DeleteIfExists(stagingPath + ManagedReplicaPageMaterializingFileSystem.StateSuffix);
            DeleteIfExists(stagingPath + ManagedReplicaPageMaterializingFileSystem.OwnershipLockSuffix);
            DeleteIfExists(metadataStagingPath);
            DeleteIfExists(metadataWritePath);
        }
    }

    /// <summary>
    /// Guards a bootstrap-install target: the path must be missing, bootstrap-on-empty must be
    /// enabled, and no orphaned metadata sidecar can already exist. Called once, unlocked, as a
    /// fast pre-check before paying for the network download, and again immediately after the
    /// apply lease is acquired as the authoritative, race-closing check (see
    /// <see cref="BootstrapAsync"/>).
    /// </summary>
    private static void EnsureBootstrapTargetIsMissing(AhtolaReplicaOptions options, string metadataPath)
    {
        if (File.Exists(options.Path))
        {
            throw new NotSupportedException(
                "Managed embedded replica bootstrap only installs a database at a missing replica path.");
        }

        if (!options.BootstrapIfEmpty)
        {
            throw new NotSupportedException(
                "Managed embedded replica bootstrap is disabled and the replica path does not contain an initialized managed database.");
        }

        if (File.Exists(metadataPath))
        {
            throw new InvalidOperationException(
                "Managed embedded replica metadata exists while the replica database is missing.");
        }
    }

    /// <summary>
    /// Rebuilds the portable logical-replay table-id-to-name map from the current local schema,
    /// keyed by each table's b-tree rootpage (the same stable, structurally reconstructible
    /// identifier Turso's <c>read_logical_replay_table_map</c> uses). Used after any page-based
    /// apply path (bootstrap, incremental pages, replace-base), where no logical schema identity
    /// operations are decoded but a future logical pull may depend on an existing map.
    /// </summary>
    private static IReadOnlyDictionary<ulong, string> RebuildTableMapFromSchema(
        string databasePath,
        AhtolaRemoteEncryptionOptions? remoteEncryption = null)
    {
        using var opened = ManagedReplicaEncryption.OpenDatabase(databasePath, remoteEncryption);
        return ReadTableMap(opened.Database.Connect());
    }

    private static IReadOnlyDictionary<ulong, string> RebuildTableMapFromSchema(
        string databasePath,
        IFileSystem fileSystem)
    {
        using var database = ManagedDatabaseAdapter.OpenFile(databasePath, fileSystem, readOnly: false);
        return ReadTableMap(database.Connect());
    }

    private static IReadOnlyDictionary<ulong, string> ReadTableMap(IManagedConnectionAdapter connection)
    {
        using var statement = connection.Prepare(
            "SELECT rootpage, name FROM sqlite_schema WHERE type = 'table' AND rootpage != 0");
        var map = new Dictionary<ulong, string>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var rootpage = statement.GetValue(0).AsInteger();
            if (rootpage <= 0)
                continue;
            map[unchecked((ulong)rootpage)] = statement.GetValue(1).AsText();
        }

        return map;
    }

    public static ManagedReplicaMetadata? LoadMetadata(string databasePath)
    {
        var path = databasePath + MetadataSuffix;
        if (!File.Exists(path))
            return null;
        if (new FileInfo(path).Length is <= 0 or > MaxMetadataFileLength)
            throw new InvalidDataException("Managed embedded replica metadata has an invalid size.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in StrictUtf8.GetString(File.ReadAllBytes(path)).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0 || !values.TryAdd(line[..separator], line[(separator + 1)..]))
                throw new InvalidDataException("Managed embedded replica metadata is malformed.");
        }

        if (!values.TryGetValue("version", out var version))
            throw new InvalidDataException("Managed embedded replica metadata is incomplete.");

        return version switch
        {
            "2" => LoadV2Metadata(values),
            "3" => LoadV3Metadata(values),
            "4" => LoadV4Metadata(values),
            "5" => LoadV5Metadata(values),
            "6" => LoadV6Metadata(values),
            _ => throw new InvalidDataException($"Managed embedded replica metadata has an unsupported version '{version}'."),
        };
    }

    internal static ManagedReplicaMetadata EnsureLegacyRemoteBaseSnapshot(
        string databasePath,
        ManagedReplicaMetadata metadata)
    {
        var snapshotPath = databasePath + BaseSnapshotSuffix;
        if (File.Exists(snapshotPath))
            return metadata;

        var fingerprint = ComputeDatabaseFingerprint(databasePath);
        var expected = string.IsNullOrEmpty(metadata.RemoteBaseSha256)
            ? metadata.DatabaseSha256
            : metadata.RemoteBaseSha256;
        if (!string.Equals(fingerprint, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Legacy managed replica metadata has no remote-base snapshot and the main file "
                + "has already diverged; a fresh bootstrap is required.");
        }

        CopyFileDurably(databasePath, snapshotPath);
        return metadata with { RemoteBaseSha256 = fingerprint };
    }

    private static ManagedReplicaMetadata LoadV2Metadata(Dictionary<string, string> values)
    {
        if (!values.TryGetValue("server_revision_base64", out var encodedRevision)
            || !values.TryGetValue("database_sha256", out var fingerprint)
            || !values.TryGetValue("client_id", out var clientId) || values.Count != 4)
            throw new InvalidDataException("Managed embedded replica metadata is incomplete.");

        var (revision, validatedFingerprint) = DecodeCommonFields(encodedRevision, fingerprint, clientId);
        // A v2 file always carries a synced revision: it has already talked to a page-protocol
        // remote (MVCC logical sync never shipped without the v3 protocol field), so it is pinned
        // to Pages rather than left Unknown, matching Turso's DatabaseMetadata::load back-compat rule.
        return new ManagedReplicaMetadata(
            revision,
            validatedFingerprint,
            clientId,
            RemotePullProtocol.Pages,
            EmptyTableMap)
        {
            RemoteBaseSha256 = validatedFingerprint,
        };
    }

    private static ManagedReplicaMetadata LoadV3Metadata(Dictionary<string, string> values)
        => LoadCurrentMetadata(
            values,
            expectedFieldCount: 6,
            revertState: null,
            journalBaseWatermark: 1,
            remoteBaseSha256: null);

    private static ManagedReplicaMetadata LoadV4Metadata(Dictionary<string, string> values)
    {
        if (!values.TryGetValue("revert_state_base64", out var revertStateEncoded))
            throw new InvalidDataException("Managed embedded replica metadata is incomplete.");

        ManagedReplicaRevertState revertState;
        try
        {
            revertState = DecodeRevertState(Convert.FromBase64String(revertStateEncoded));
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Managed embedded replica metadata is invalid.", exception);
        }

        return LoadCurrentMetadata(
            values,
            expectedFieldCount: 8,
            revertState,
            journalBaseWatermark: 1,
            remoteBaseSha256: null);
    }

    private static ManagedReplicaMetadata LoadV5Metadata(Dictionary<string, string> values)
        => LoadCurrentMetadata(
            values,
            expectedFieldCount: 8,
            revertState: null,
            DecodeJournalBaseWatermark(values),
            DecodeRemoteBaseSha256(values));

    private static ManagedReplicaMetadata LoadV6Metadata(Dictionary<string, string> values)
    {
        if (!values.TryGetValue("revert_state_base64", out var revertStateEncoded))
            throw new InvalidDataException("Managed embedded replica metadata is incomplete.");

        ManagedReplicaRevertState revertState;
        try
        {
            revertState = DecodeRevertState(Convert.FromBase64String(revertStateEncoded));
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Managed embedded replica metadata is invalid.", exception);
        }

        return LoadCurrentMetadata(
            values,
            expectedFieldCount: 9,
            revertState,
            DecodeJournalBaseWatermark(values),
            DecodeRemoteBaseSha256(values));
    }

    private static ManagedReplicaMetadata LoadCurrentMetadata(
        Dictionary<string, string> values,
        int expectedFieldCount,
        ManagedReplicaRevertState? revertState,
        long journalBaseWatermark,
        string? remoteBaseSha256)
    {
        if (!values.TryGetValue("server_revision_base64", out var encodedRevision)
            || !values.TryGetValue("database_sha256", out var fingerprint)
            || !values.TryGetValue("client_id", out var clientId)
            || !values.TryGetValue("protocol", out var protocolText)
            || !values.TryGetValue("table_map_base64", out var tableMapEncoded)
            || values.Count != expectedFieldCount)
        {
            throw new InvalidDataException("Managed embedded replica metadata is incomplete.");
        }

        var (revision, validatedFingerprint) = DecodeCommonFields(encodedRevision, fingerprint, clientId);
        var protocol = protocolText switch
        {
            "unknown" => RemotePullProtocol.Unknown,
            "pages" => RemotePullProtocol.Pages,
            "mvcc_logical" => RemotePullProtocol.MvccLogical,
            _ => throw new InvalidDataException("Managed embedded replica metadata has an unsupported protocol value."),
        };

        IReadOnlyDictionary<ulong, string> tableMap;
        try
        {
            tableMap = DecodeTableMap(Convert.FromBase64String(tableMapEncoded));
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Managed embedded replica metadata is invalid.", exception);
        }

        return new ManagedReplicaMetadata(
            revision,
            validatedFingerprint,
            clientId,
            protocol,
            tableMap)
        {
            RevertState = revertState,
            JournalBaseWatermark = journalBaseWatermark,
            RemoteBaseSha256 = remoteBaseSha256 ?? validatedFingerprint,
        };
    }

    private static long DecodeJournalBaseWatermark(IReadOnlyDictionary<string, string> values)
    {
        if (!values.TryGetValue("journal_base_watermark", out var text)
            || !long.TryParse(text, CultureInfo.InvariantCulture, out var watermark)
            || watermark <= 0)
        {
            throw new InvalidDataException("Managed embedded replica metadata has an invalid journal base watermark.");
        }

        return watermark;
    }

    private static string DecodeRemoteBaseSha256(IReadOnlyDictionary<string, string> values)
    {
        if (!values.TryGetValue("remote_base_sha256", out var fingerprint)
            || !IsSha256Hex(fingerprint))
        {
            throw new InvalidDataException("Managed embedded replica metadata has an invalid remote base fingerprint.");
        }

        return fingerprint;
    }

    private static (string Revision, string Fingerprint) DecodeCommonFields(string encodedRevision, string fingerprint, string clientId)
    {
        try
        {
            var revision = StrictUtf8.GetString(Convert.FromBase64String(encodedRevision));
            if (revision.Length == 0 || !Guid.TryParseExact(clientId, "N", out _))
                throw new InvalidDataException("Managed embedded replica metadata is invalid.");
            if (!IsSha256Hex(fingerprint))
                throw new InvalidDataException("Managed embedded replica metadata is invalid.");
            return (revision, fingerprint);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Managed embedded replica metadata is invalid.", exception);
        }
    }

    /// <summary>
    /// Encodes the stable table-id-to-name map as a small deterministic binary blob (never
    /// text), avoiding any escaping ambiguity for table names: a 4-byte LE entry count, followed
    /// by, per entry, an 8-byte LE stable id, a 4-byte LE UTF-8 byte length, and the UTF-8 bytes.
    /// </summary>
    private static byte[] EncodeTableMap(IReadOnlyDictionary<ulong, string> tableNamesByStableId)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        writer.Write(tableNamesByStableId.Count);
        foreach (var (stableId, name) in tableNamesByStableId.OrderBy(pair => pair.Key))
        {
            var nameBytes = StrictUtf8.GetBytes(name);
            writer.Write(stableId);
            writer.Write(nameBytes.Length);
            writer.Write(nameBytes);
        }

        return buffer.ToArray();
    }

    private static IReadOnlyDictionary<ulong, string> DecodeTableMap(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        int count;
        try
        {
            count = reader.ReadInt32();
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("Managed embedded replica table map is truncated.", exception);
        }

        if (count < 0 || count > MaxTableMapEntries)
            throw new InvalidDataException("Managed embedded replica table map has an invalid entry count.");

        var map = new Dictionary<ulong, string>(count);
        for (var i = 0; i < count; i++)
        {
            ulong stableId;
            int nameLength;
            try
            {
                stableId = reader.ReadUInt64();
                nameLength = reader.ReadInt32();
            }
            catch (EndOfStreamException exception)
            {
                throw new InvalidDataException("Managed embedded replica table map is truncated.", exception);
            }

            if (nameLength < 0 || nameLength > MaxStringBytes)
                throw new InvalidDataException("Managed embedded replica table map contains an invalid name length.");

            var nameBytes = reader.ReadBytes(nameLength);
            if (nameBytes.Length != nameLength)
                throw new InvalidDataException("Managed embedded replica table map is truncated.");

            string name;
            try
            {
                name = StrictUtf8.GetString(nameBytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("Managed embedded replica table map contains invalid UTF-8.", exception);
            }

            if (!map.TryAdd(stableId, name))
                throw new InvalidDataException("Managed embedded replica table map contains a duplicate stable table id.");
        }

        if (stream.Position != stream.Length)
            throw new InvalidDataException("Managed embedded replica table map has trailing bytes.");

        return map;
    }

    private static byte[] EncodeRevertState(ManagedReplicaRevertState state)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);
        writer.Write((byte)4);
        writer.Write((byte)state.Phase);
        writer.Write(state.SourceWalWatermark);
        writer.Write(state.AttemptedFirstSequence);
        writer.Write(state.AttemptedWatermark);
        writer.Write(state.SourceDatabaseSizeInPages);
        writer.Write(state.SourceWalCheckpointSequence);
        writer.Write(state.SourceWalSalt1);
        writer.Write(state.SourceWalSalt2);
        writer.Write(state.SourceWalChecksum1);
        writer.Write(state.SourceWalChecksum2);
        writer.Write(state.OriginalDatabaseSizeInPages);
        writer.Write(state.OriginalRevertWalFrameCount);
        writer.Write(state.CommittedDatabaseSizeInPages);
        writer.Write(state.CommittedRevertWalFrameCount);
        writer.Write(Convert.FromHexString(state.OriginalDatabaseSha256));
        writer.Write(Convert.FromHexString(state.CommittedDatabaseSha256));
        writer.Write(Convert.FromHexString(state.RevertWalSha256));
        writer.Flush();
        writer.Write(SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length))));
        return buffer.ToArray();
    }

    private static ManagedReplicaRevertState DecodeRevertState(byte[] bytes)
    {
        const int payloadLength = 2 + (3 * sizeof(long)) + (10 * sizeof(uint)) + 96;
        const int encodedLength = payloadLength + 32;
        if (bytes.Length != encodedLength || bytes[0] != 4)
            throw new InvalidDataException("Managed embedded replica revert state is malformed.");
        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(bytes.AsSpan(0, payloadLength)),
                bytes.AsSpan(payloadLength)))
        {
            throw new InvalidDataException("Managed embedded replica revert state failed its integrity check.");
        }

        var phase = (ManagedReplicaRevertPhase)bytes[1];
        var sourceWalWatermark = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(2));
        var offset = 2 + sizeof(long);
        var attemptedFirstSequence = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
        offset += sizeof(long);
        var attemptedWatermark = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
        offset += sizeof(long);
        var sourceDatabaseSizeInPages = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
        offset += sizeof(uint);
        var sourceWalCheckpointSequence = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
        offset += sizeof(uint);
        var sourceWalSalt1 = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
        offset += sizeof(uint);
        var sourceWalSalt2 = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
        offset += sizeof(uint);
        var sourceWalChecksum1 = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
        offset += sizeof(uint);
        var sourceWalChecksum2 = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
        offset += sizeof(uint);
        var originalDatabaseSizeInPages = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
        offset += sizeof(uint);
        var originalRevertWalFrameCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
        offset += sizeof(uint);
        var committedDatabaseSizeInPages = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
        offset += sizeof(uint);
        var committedRevertWalFrameCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
        offset += sizeof(uint);
        var originalDatabaseSha256 = Convert.ToHexString(bytes.AsSpan(offset, 32));
        offset += 32;
        var committedDatabaseSha256 = Convert.ToHexString(bytes.AsSpan(offset, 32));
        offset += 32;
        var revertWalSha256 = Convert.ToHexString(bytes.AsSpan(offset, 32));
        var hasAttemptedBatch = attemptedFirstSequence > 0 && attemptedWatermark > attemptedFirstSequence;
        if (!Enum.IsDefined(phase)
            || phase == ManagedReplicaRevertPhase.None
            || sourceWalWatermark < 0
            || sourceWalWatermark == 0 != (sourceDatabaseSizeInPages == 0)
            || phase == ManagedReplicaRevertPhase.Captured && sourceWalWatermark == 0
            || phase == ManagedReplicaRevertPhase.PushOutcomeUnknown != hasAttemptedBatch
            || attemptedFirstSequence < 0
            || attemptedWatermark < 0
            || originalDatabaseSizeInPages == 0
            || originalRevertWalFrameCount != originalDatabaseSizeInPages
            || originalRevertWalFrameCount > int.MaxValue
            || committedDatabaseSizeInPages == 0
            || committedRevertWalFrameCount != committedDatabaseSizeInPages
            || committedRevertWalFrameCount > int.MaxValue)
        {
            throw new InvalidDataException("Managed embedded replica revert state is invalid.");
        }

        return new ManagedReplicaRevertState(
            phase,
            sourceWalWatermark,
            attemptedFirstSequence,
            attemptedWatermark,
            sourceDatabaseSizeInPages,
            sourceWalCheckpointSequence,
            sourceWalSalt1,
            sourceWalSalt2,
            sourceWalChecksum1,
            sourceWalChecksum2,
            originalDatabaseSizeInPages,
            originalRevertWalFrameCount,
            committedDatabaseSizeInPages,
            committedRevertWalFrameCount,
            originalDatabaseSha256,
            committedDatabaseSha256,
            revertWalSha256);
    }

    public static Task<AhtolaSyncResult> CheckForUpdatesAsync(
        AhtolaReplicaOptions options, ManagedReplicaMetadata metadata, AhtolaSyncOptions syncOptions,
        CancellationToken cancellationToken)
        => CheckForUpdatesAsync(options, metadata, syncOptions, [], [], cancellationToken);

    /// <summary>
    /// Pulls and applies remote changes. <paramref name="pendingLocalChanges"/> is the set of
    /// local changes still awaiting push at the moment this call starts (e.g. left over after a
    /// push batch capped by <see cref="AhtolaReplicaOptions.PushOperationsThreshold"/>, or simply
    /// because no push has run yet this cycle); for the MVCC logical protocol these are
    /// precollected and reapplied on top of the newly pulled base so they are not silently lost
    /// (see <see cref="ManagedReplicaLogicalReplayer.CapturePendingLocalRowChanges"/>). Ignored
    /// for the page protocol, which has no mechanism to reconcile local writes across a pull.
    /// </summary>
    public static async Task<AhtolaSyncResult> CheckForUpdatesAsync(
        AhtolaReplicaOptions options,
        ManagedReplicaMetadata metadata,
        AhtolaSyncOptions syncOptions,
        IReadOnlyList<ReplicaLocalChange> pendingLocalChanges,
        CancellationToken cancellationToken)
        => await CheckForUpdatesAsync(
                options,
                metadata,
                syncOptions,
                pendingLocalChanges,
                [],
                cancellationToken)
            .ConfigureAwait(false);

    public static async Task<AhtolaSyncResult> CheckForUpdatesAsync(
        AhtolaReplicaOptions options, ManagedReplicaMetadata metadata, AhtolaSyncOptions syncOptions,
        IReadOnlyList<ReplicaLocalChange> pendingLocalChanges,
        IReadOnlyList<ReplicaLocalChange> acknowledgedLocalChanges,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pendingLocalChanges);
        ArgumentNullException.ThrowIfNull(acknowledgedLocalChanges);
        ManagedReplicaSupportMatrix.ValidateOptions(options);
        ManagedReplicaRevertWal.EnsureSynchronizationReady(options.Path, metadata);
        // The whole pull-and-apply cycle below is retried, in place, whenever the apply lease
        // reveals that local state already moved past the snapshot (metadata, pending local
        // changes) this iteration's request/response were negotiated against -- see
        // TryUseCurrentLocalStateAsPullBase. That can only happen when a concurrent
        // CheckForUpdatesAsync call (another sync/bootstrap-catch-up racing through the very same
        // per-path apply lease, or -- today, before the cross-process OS lock lands -- a second
        // process) commits its own apply while this call was waiting on the network or the lease,
        // both of which are deliberately outside the lease's scope (see ManagedReplicaApplyLock).
        // Retrying here, before any apply, means a stale response can never be applied on top of a
        // base it was not actually built from: doing so could silently regress metadata (rewind
        // Revision) or discard an intervening local change instead of reconciling it.
        while (true)
        {
            var requestLogical = metadata.Protocol == RemotePullProtocol.MvccLogical;
            if (requestLogical && options.RemoteEncryption is not null)
            {
                throw new NotSupportedException(
                    "Managed embedded replica synchronization does not support the MVCC logical pull "
                    + "protocol combined with remote encryption.");
            }

            if (!requestLogical)
            {
                // A non-logical (page) protocol client can only ever receive a Pages response for
                // any given pull, so this is known upfront and the guard can (and should) run before
                // spending a network round-trip on a request that would have to be rejected anyway.
                // This is the ORIGINAL, unconditional file-level check only (no pendingLocalChanges
                // rejection): a page-protocol replica has never reconciled local writes via the pull
                // path, so it has always tolerated unrelated journal-tracked pending push entries as
                // long as the file itself has not diverged, and that historical behavior is
                // preserved exactly here. A logical-protocol client cannot be checked here: its
                // response might turn out to be a Pages stream too (see the equivalent, STRICTER
                // check further down, after the actual stream kind of THIS response is known), or it
                // might be a logical stream that needs no such check at all.
                EnsureNoLocalDivergence(options.Path, metadata);
            }

            syncOptions.Progress?.Report(new AhtolaSyncProgress(AhtolaSyncProgressStage.Pulling));
            var payload = CreatePullRequest(metadata.Revision, options.LongPollTimeout, requestLogical);
            using var timeout = CreateTimeout(options.HttpPolicy.RequestTimeout, cancellationToken);
            using var scope = options.EnterApplicationHttpScope();
            using var client = options.HttpPolicy.CreateHttpClient(options.RemoteEncryption is not null);
            client.Timeout = Timeout.InfiniteTimeSpan;
            var token = string.IsNullOrWhiteSpace(options.AuthToken) ? null : options.AuthToken;
            var effectiveToken = timeout?.Token ?? cancellationToken;
            using var response = await AhtolaRemoteTransportSecurity
                .SendAsync(
                    client,
                    CreatePullUpdatesUri(options.RemoteUri),
                    requestUri => CreatePullUpdatesHttpRequest(
                        requestUri,
                        payload,
                        token,
                        options.RemoteEncryption),
                    token,
                    remoteEncryptionConfigured: options.RemoteEncryption is not null,
                    HttpCompletionOption.ResponseHeadersRead,
                    effectiveToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(effectiveToken).ConfigureAwait(false);
            var reader = new DelimitedProtobufReader(stream);
            var message = await reader.ReadAsync(MaxHeaderLength, effectiveToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The pull-updates response did not contain a protobuf header.");
            var header = ParseHeader(message);

            if (header.StreamKind == PullStreamKind.MvccLogicalLog)
            {
                if (header.ApplyMode == PullApplyMode.ReplaceBase)
                {
                    throw new InvalidDataException(
                        "The pull-updates response returned replace_base apply mode with an MVCC logical-log stream.");
                }
                if (!requestLogical)
                {
                    throw new InvalidDataException(
                        "The pull-updates response returned an MVCC logical-log stream, but a logical pull was not requested.");
                }

                var body = await ReadRemainingBytesAsync(stream, effectiveToken).ConfigureAwait(false);

                // One exclusive apply lease spans the whole logical apply (below): commit, checkpoint,
                // and metadata publication, or the callee's own rollback on failure. See
                // ManagedReplicaApplyLock for why this seam is deliberately narrow (acquired only for
                // the local apply, never for the network round trip above).
                await using var logicalApplyLease = await ManagedReplicaApplyLock.AcquireExclusiveAsync(options.Path, effectiveToken)
                    .ConfigureAwait(false);
                ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.ReplicaApplyLockAcquired);
                effectiveToken.ThrowIfCancellationRequested();

                // The request above (and this response) were negotiated against `metadata` and
                // `pendingLocalChanges` as they stood BEFORE the network round trip and the wait
                // for this lease -- both entirely outside its scope by design. Re-validate that
                // base is still current now that the lease is actually held: if another caller
                // already advanced local state in the meantime, this response is stale relative to
                // it and must never be applied; retry the whole pull against the now-current base
                // instead of silently regressing metadata or discarding an intervening local change.
                if (!TryUseCurrentLocalStateAsPullBase(
                        options.Path, metadata, pendingLocalChanges,
                        out var freshLogicalMetadata, out var freshLogicalPendingLocalChanges))
                {
                    metadata = freshLogicalMetadata;
                    pendingLocalChanges = freshLogicalPendingLocalChanges;
                    continue;
                }

                var (outcome, statistics) = await ApplyLogicalUpdatesAsync(
                    options, metadata, header, body, syncOptions, pendingLocalChanges, acknowledgedLocalChanges,
                    payload.Length, reader.BytesRead + body.Length, effectiveToken)
                    .ConfigureAwait(false);
                return new AhtolaSyncResult(outcome, statistics);
            }

            // Pages stream (Incremental or ReplaceBase for a page-protocol remote, or a protocol-2
            // remote using Pages+ReplaceBase for a validated full atomic replacement).
            var pages = new List<PullPage>();
            while (await reader.ReadAsync(MaxPageMessageLength, effectiveToken).ConfigureAwait(false) is { } page)
            {
                pages.Add(ParsePage(page));
            }
            if (pages.Count == 0 && string.Equals(header.Revision, metadata.Revision, StringComparison.Ordinal))
            {
                syncOptions.Progress?.Report(new AhtolaSyncProgress(AhtolaSyncProgressStage.Completed));
                return new AhtolaSyncResult(AhtolaSyncOutcome.UpToDate,
                    new AhtolaSyncStatistics(0, 0, 0, DateTimeOffset.UtcNow, null, payload.Length, reader.BytesRead, metadata.Revision));
            }

            if (pages.Count == 0)
                throw new InvalidDataException("The pull-updates response changed revision without returning page data.");
            if (string.Equals(header.Revision, metadata.Revision, StringComparison.Ordinal))
                throw new InvalidDataException("The pull-updates response returned page data without changing revision.");
            if (header.ApplyMode == PullApplyMode.ReplaceBase && (ulong)pages.Count != header.DatabasePages)
            {
                throw new InvalidDataException(
                    "The pull-updates response used replace_base apply mode without returning every database page exactly once.");
            }

            // One exclusive apply lease spans the sidecar/fingerprint re-check below through the
            // page-based apply and its metadata publication (or rollback). Acquired here -- after the
            // network round trip, before any sidecar inspection -- so a concurrent local write can
            // never land between "checked clean" and "applied" the way it could when the historical
            // page-protocol path relied solely on the pre-network EnsureNoLocalDivergence check.
            await using var pagesApplyLease = await ManagedReplicaApplyLock.AcquireExclusiveAsync(options.Path, effectiveToken)
                .ConfigureAwait(false);
            ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.ReplicaApplyLockAcquired);
            effectiveToken.ThrowIfCancellationRequested();

            // The response turned out to be a Pages stream (Incremental or ReplaceBase) even though
            // the remembered protocol is MvccLogical (a protocol-2 remote can still answer any given
            // pull this way, e.g. after its logical log was garbage-collected). Unlike the ordinary,
            // historical page-protocol path above, a logical-protocol replica's local writes are
            // expected to keep living in the WAL between syncs (tracked by the change journal,
            // reconciled by precollect/reapply for LOGICAL responses) -- but a raw page-based apply
            // has no such reconciliation mechanism at all, so this surprise combination needs its
            // OWN, apply-mode-aware guard: Incremental still rejects pending changes because a
            // partial page patch cannot rebase journaled SQL. ReplaceBase installs every page, so
            // pending statements can be replayed onto the new snapshot before metadata publication.
            //
            // The ordinary, historical page-protocol path (requestLogical == false) re-validates here
            // too: EnsureNoLocalDivergence already ran once before the network call above, but only
            // this post-network, lock-held check is authoritative -- a local write landing during the
            // round trip must still be caught before the apply below mutates anything. Always the
            // strict fingerprint/sidecar check here (never the checkpoint-and-discard behavior
            // EnsurePagesApplyIsSafe uses for ReplaceBase): a page-protocol replica has no push
            // mechanism, so there is no way to know local WAL content is already reflected upstream.
            if (requestLogical)
            {
                // Same staleness hazard as the logical-log branch above, and for the same reason:
                // this response (Incremental or ReplaceBase) was negotiated against `metadata` as
                // it stood before the network round trip and the wait for this lease.
                // CheckpointAndCleanSidecarsBeforePagesApply (the ReplaceBase sub-case below) has no
                // fingerprint to compare against, because a logical-protocol replica's WAL is
                // expected to keep evolving between syncs -- without this reload-and-compare, a
                // stale ReplaceBase response could overwrite local state that a concurrent caller
                // already advanced past it. Retry against the current base instead of applying it.
                if (!TryUseCurrentLocalStateAsPullBase(
                        options.Path, metadata, pendingLocalChanges,
                        out var freshPagesMetadata, out var freshPagesPendingLocalChanges))
                {
                    metadata = freshPagesMetadata;
                    pendingLocalChanges = freshPagesPendingLocalChanges;
                    continue;
                }

                metadata = EnsurePagesApplyIsSafe(
                    options.Path,
                    metadata,
                    pendingLocalChanges,
                    header.ApplyMode,
                    effectiveToken);
            }
            else
            {
                CheckFileDivergence(options.Path, metadata);
            }

            await ApplyIncrementalPagesAsync(
                    options,
                    header,
                    pages,
                    metadata,
                    pendingLocalChanges,
                    acknowledgedLocalChanges,
                    effectiveToken)
                .ConfigureAwait(false);
            syncOptions.Progress?.Report(new AhtolaSyncProgress(AhtolaSyncProgressStage.Applying));
            syncOptions.Progress?.Report(new AhtolaSyncProgress(AhtolaSyncProgressStage.Completed));
            return new AhtolaSyncResult(AhtolaSyncOutcome.RemoteChangesApplied,
                new AhtolaSyncStatistics(0, 0, 0, DateTimeOffset.UtcNow, null, payload.Length, reader.BytesRead, header.Revision));
        }
    }

    /// <summary>
    /// Reloads the durable local state (metadata revision and pending-push change journal) from
    /// disk while the apply lease for this pull is held, and compares it against the snapshot
    /// (<paramref name="requestBaseMetadata"/>, <paramref name="requestBasePendingLocalChanges"/>)
    /// the in-flight request and response were actually built from -- both captured before the
    /// network round trip and the wait for the lease, entirely outside its scope by design (see
    /// <see cref="ManagedReplicaApplyLock"/>). Returns false when local state already moved past
    /// that base: another writer (a concurrent sync/bootstrap-catch-up racing through this same
    /// lock, or -- today, before the cross-process OS lock lands -- another process) already
    /// committed its own apply, or a new local change was journaled or acknowledged, while this
    /// call waited. Applying a response negotiated against a base that no longer exists would
    /// silently regress metadata (rewind <see cref="ManagedReplicaMetadata.Revision"/>) or discard
    /// the intervening local change instead of reconciling it, so callers must discard the
    /// in-flight response and retry the whole pull against <paramref name="freshMetadata"/> and
    /// <paramref name="freshPendingLocalChanges"/> instead.
    /// </summary>
    private static bool TryUseCurrentLocalStateAsPullBase(
        string databasePath,
        ManagedReplicaMetadata requestBaseMetadata,
        IReadOnlyList<ReplicaLocalChange> requestBasePendingLocalChanges,
        out ManagedReplicaMetadata freshMetadata,
        out IReadOnlyList<ReplicaLocalChange> freshPendingLocalChanges)
    {
        freshMetadata = LoadMetadata(databasePath)
            ?? throw new NotSupportedException(
                "Managed embedded replica metadata was removed while an update was in flight.");
        freshPendingLocalChanges = ManagedReplicaChangeJournal.Open(databasePath).ReadBatch(int.MaxValue).Changes;

        return string.Equals(freshMetadata.Revision, requestBaseMetadata.Revision, StringComparison.Ordinal)
            && HaveSamePendingLocalChangeSequence(requestBasePendingLocalChanges, freshPendingLocalChanges);
    }

    /// <summary>
    /// Compares two pending-local-change snapshots by their journal <see
    /// cref="ReplicaLocalChange.Sequence"/> numbers only, in order, rather than by full record
    /// equality: a given sequence's content is immutable once committed (the journal only ever
    /// appends new entries with new sequence numbers, or removes acknowledged ones), but each
    /// reload deserializes fresh byte arrays for <c>BeforeRecord</c> that would never
    /// reference-equal an earlier snapshot's arrays even when nothing actually changed --
    /// comparing the auto-generated record-struct equality directly would report every reload as
    /// "changed" and make the retry loop above never converge.
    /// </summary>
    private static bool HaveSamePendingLocalChangeSequence(
        IReadOnlyList<ReplicaLocalChange> a, IReadOnlyList<ReplicaLocalChange> b)
    {
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Sequence != b[i].Sequence)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Decodes and applies an MVCC logical-log stream. The complete body is decoded and validated
    /// (<see cref="ManagedReplicaLml3Decoder.Decode"/>) before anything is mutated. A non-empty
    /// transaction set is replayed under one <c>BEGIN IMMEDIATE</c>/<c>COMMIT</c> against a
    /// dedicated connection (never the caller's live connection, so the local push change journal
    /// never captures this replay); the database, fingerprint, table map, protocol, and revision
    /// only advance together after that commit succeeds, and are left untouched on any failure.
    /// When the remote apply is non-empty and <paramref name="pendingLocalChanges"/> is non-empty,
    /// the current local state for every row touched by a pending change is precollected before
    /// the remote apply begins and reapplied in the same transaction, so local writes the server
    /// has not yet seen survive the pull instead of being silently overwritten.
    /// </summary>
    /// <remarks>
    /// Metadata (revision/fingerprint/table map) is published whenever <paramref name="header"/>
    /// carries a new revision, even when the decoded transaction set is empty (e.g. every
    /// transaction in the response was excluded as this client's own echo). Skipping metadata
    /// publication in that case would leave metadata pinned to the OLD revision forever: the next
    /// pull would resend the identical already-acknowledged range, decode to zero transactions
    /// again, and never converge to <see cref="AhtolaSyncOutcome.UpToDate"/>. Nothing was mutated
    /// on disk in that case, so the previously recorded fingerprint/table map remain valid as-is
    /// and no compensation is needed.
    /// </remarks>
    private static async Task<(AhtolaSyncOutcome Outcome, AhtolaSyncStatistics Statistics)> ApplyLogicalUpdatesAsync(
        AhtolaReplicaOptions options,
        ManagedReplicaMetadata metadata,
        PullHeader header,
        byte[] body,
        AhtolaSyncOptions syncOptions,
        IReadOnlyList<ReplicaLocalChange> pendingLocalChanges,
        IReadOnlyList<ReplicaLocalChange> acknowledgedLocalChanges,
        long networkSentBytes,
        long networkReceivedBytes,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ManagedReplicaLogicalTxn> transactions;
        if (header.LogicalMetadata is { } logicalMetadata)
        {
            transactions = ManagedReplicaLml3Decoder.Decode(logicalMetadata.Ranges, body, cancellationToken);
        }
        else if (body.Length == 0)
        {
            transactions = [];
        }
        else
        {
            throw new InvalidDataException(
                "The pull-updates response is missing MVCC logical-log metadata for a non-empty logical stream.");
        }

        if (transactions.Count == 0 && string.Equals(header.Revision, metadata.Revision, StringComparison.Ordinal))
        {
            syncOptions.Progress?.Report(new AhtolaSyncProgress(AhtolaSyncProgressStage.Completed));
            return (AhtolaSyncOutcome.UpToDate,
                new AhtolaSyncStatistics(0, 0, 0, DateTimeOffset.UtcNow, null, networkSentBytes, networkReceivedBytes, metadata.Revision));
        }
        if (pendingLocalChanges.Count != 0)
        {
            return ApplyLogicalUpdatesWithProtectedPending(
                options,
                metadata,
                header,
                transactions,
                syncOptions,
                pendingLocalChanges,
                acknowledgedLocalChanges,
                networkSentBytes,
                networkReceivedBytes,
                cancellationToken);
        }

        var metadataPath = options.Path + MetadataSuffix;
        var directory = Path.GetDirectoryName(Path.GetFullPath(options.Path))!;
        var metadataStagingPath = Path.Combine(directory, $".{Path.GetFileName(metadataPath)}.logical-{Guid.NewGuid():N}.tmp");

        long operationCount = 0;
        var journalBaseWatermark = AdvanceJournalBaseWatermark(metadata, acknowledgedLocalChanges);
        var remoteBaseSha256 = metadata.RemoteBaseSha256;
        var remoteBasePublished = false;
        var tableNamesByStableId = metadata.TableNamesByStableId;
        string fingerprint;
        DatabaseArtifactBackup? artifactBackup = null;
        var metadataPublished = false;
        try
        {
            if (transactions.Count != 0)
            {
                RejectIfLocalSchemaChangesConflictWithRemoteChanges(pendingLocalChanges);
                var pendingAddColumns = ManagedReplicaLogicalReplayer.CollectPendingAddColumns(pendingLocalChanges);

                // The SQL transaction below mutates the live database file in place (unlike the
                // page-based path, which stages a whole replacement file before ever touching the
                // original). Durability compensation for that in-place mutation therefore needs
                // its own full-artifact snapshot, taken before the transaction starts, covering
                // every durable file the engine may touch: the main database file plus its
                // WAL/SHM/journal sidecars. If anything fails after COMMIT but before metadata is
                // durably published (including a failed checkpoint, which must never be
                // swallowed), every one of those artifacts is restored to its pre-apply state so
                // the old revision/fingerprint pair in metadata remains valid and the next sync
                // safely retries from the old revision.
                artifactBackup = BackupDatabaseArtifacts(options.Path, directory, Guid.NewGuid().ToString("N"));
                using (var database = ManagedDatabaseAdapter.Open(options.Path))
                {
                    var connection = database.Connect();

                    // Precollect BEFORE the remote apply begins: capture is a plain read against
                    // the current (pre-pull) committed state, on the same dedicated connection
                    // that is about to apply the remote transactions.
                    var capturedLocalChanges = pendingLocalChanges.Count == 0
                        ? []
                        : ManagedReplicaLogicalReplayer.CapturePendingLocalRowChanges(connection, pendingLocalChanges);

                    ExecuteNonQuery(connection, "BEGIN IMMEDIATE");
                    try
                    {
                        var applied = ManagedReplicaLogicalReplayer.Apply(
                            connection,
                            transactions,
                            metadata.TableNamesByStableId,
                            metadata.ClientId,
                            cancellationToken,
                            pendingAddColumns);
                        operationCount = applied.OperationCount;
                        tableNamesByStableId = applied.TableNamesByStableId;

                        // Rebase still-unpushed local schema first so captured row values that
                        // include a locally-added column have a home, then rebase row writes.
                        // Both stay in the change journal: the server has not seen them yet.
                        if (pendingAddColumns.Count != 0)
                        {
                            ManagedReplicaLogicalReplayer.ReplayPendingLocalAddColumns(
                                connection, pendingAddColumns, cancellationToken);
                        }

                        if (capturedLocalChanges.Count != 0)
                        {
                            ManagedReplicaLogicalReplayer.ReplayPendingLocalRowChanges(
                                connection, capturedLocalChanges, cancellationToken);
                        }

                        ExecuteNonQuery(connection, "COMMIT");
                    }
                    catch
                    {
                        TryExecuteNonQuery(connection, "ROLLBACK");
                        throw;
                    }

                    ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.LogicalApplyCommitted);
                    cancellationToken.ThrowIfCancellationRequested();

                    // Force a WAL (if any) to checkpoint into the main file so the fingerprint
                    // hashed below, and any later plain-file-byte divergence check, observe the
                    // committed data. This must not be a best-effort/swallowed call: the
                    // transaction above is already durably committed, so a checkpoint failure
                    // here is compensated (restored) below like any other failure in this block,
                    // rather than silently leaving the file inconsistent with what publication is
                    // about to record.
                    ExecuteNonQuery(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
                    ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.LogicalApplyCheckpointed);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                syncOptions.Progress?.Report(new AhtolaSyncProgress(AhtolaSyncProgressStage.Applying));
                fingerprint = ComputeDatabaseFingerprint(options.Path);
            }
            else if (acknowledgedLocalChanges.Count != 0)
            {
                using var database = ManagedDatabaseAdapter.Open(options.Path);
                var connection = database.Connect();
                ExecuteNonQuery(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
                ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.LogicalApplyCheckpointed);
                cancellationToken.ThrowIfCancellationRequested();
                fingerprint = ComputeDatabaseFingerprint(options.Path);
            }
            else
            {
                // Revision advanced but every wire transaction decoded to nothing applied (e.g.
                // all were excluded as this client's own echo): nothing on disk changed, so the
                // previously recorded fingerprint and table map remain valid as-is. Only the
                // revision needs to move forward.
                fingerprint = metadata.DatabaseSha256;
            }

            if (transactions.Count != 0 || acknowledgedLocalChanges.Count != 0)
            {
                remoteBaseSha256 = PublishRemoteBaseSnapshot(options.Path, metadata, options.Path);
                remoteBasePublished = true;
            }

            try
            {
                await WriteMetadataAsync(
                        metadataStagingPath,
                        metadataPath,
                        header.Revision,
                        fingerprint,
                        header.Protocol,
                        tableNamesByStableId,
                        cancellationToken,
                        replaceExisting: true,
                        clientId: metadata.ClientId,
                        journalBaseWatermark: journalBaseWatermark,
                        remoteBaseSha256: remoteBaseSha256)
                    .ConfigureAwait(false);
            }
            finally
            {
                DeleteIfExists(metadataStagingPath);
            }

            // From this point on, metadata already durably fingerprints the just-installed
            // database image: a later interruption (e.g. cancellation observed immediately
            // below) must preserve that matched (database, metadata) pair rather than restore
            // only the database and leave metadata pointing at a revision the file no longer
            // (or, in the zero-transaction case, does not yet) reflect.
            metadataPublished = true;
            if (remoteBasePublished)
                CompleteRemoteBaseSnapshotPublication(options.Path);
            ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.LogicalApplyMetadataPublished);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            if (!metadataPublished && artifactBackup is { } backup)
                RestoreDatabaseArtifacts(options.Path, backup);
            throw;
        }
        finally
        {
            if (artifactBackup is { } backupToDelete)
                DeleteBackupArtifacts(backupToDelete);
        }

        syncOptions.Progress?.Report(new AhtolaSyncProgress(AhtolaSyncProgressStage.Completed));
        return (AhtolaSyncOutcome.RemoteChangesApplied,
            new AhtolaSyncStatistics(operationCount, 0, 0, DateTimeOffset.UtcNow, null, networkSentBytes, networkReceivedBytes, header.Revision));
    }

    private static (AhtolaSyncOutcome Outcome, AhtolaSyncStatistics Statistics)
        ApplyLogicalUpdatesWithProtectedPending(
            AhtolaReplicaOptions options,
            ManagedReplicaMetadata metadata,
            PullHeader header,
            IReadOnlyList<ManagedReplicaLogicalTxn> transactions,
            AhtolaSyncOptions syncOptions,
            IReadOnlyList<ReplicaLocalChange> pendingLocalChanges,
            IReadOnlyList<ReplicaLocalChange> acknowledgedLocalChanges,
            long networkSentBytes,
            long networkReceivedBytes,
            CancellationToken cancellationToken)
    {
        RejectIfLocalSchemaChangesConflictWithRemoteChanges(pendingLocalChanges);
        var pendingAddColumns = ManagedReplicaLogicalReplayer.CollectPendingAddColumns(pendingLocalChanges);
        IReadOnlyList<ManagedReplicaCapturedLocalRowChange> capturedLocalChanges;
        using (var database = ManagedDatabaseAdapter.Open(options.Path))
        {
            capturedLocalChanges = ManagedReplicaLogicalReplayer.CapturePendingLocalRowChanges(
                database.Connect(),
                pendingLocalChanges);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(options.Path))!;
        var token = Guid.NewGuid().ToString("N");
        var originalPath = Path.Combine(directory, $".{Path.GetFileName(options.Path)}.logical-base-{token}.tmp");
        var committedPath = Path.Combine(directory, $".{Path.GetFileName(options.Path)}.logical-local-{token}.tmp");
        long operationCount;
        IReadOnlyDictionary<ulong, string> tableNamesByStableId;
        try
        {
            File.Copy(
                ResolveRemoteBaseSnapshot(options.Path, metadata),
                originalPath,
                overwrite: false);
            using (var database = ManagedDatabaseAdapter.Open(originalPath))
            {
                var connection = database.Connect();
                ExecuteNonQuery(connection, "BEGIN IMMEDIATE");
                try
                {
                    ManagedReplicaLogicalReplayer.ReplayPendingLocalStatements(
                        connection,
                        acknowledgedLocalChanges,
                        cancellationToken);
                    ManagedReplicaLogicalReplayer.ReplayPendingLocalStatements(
                        connection,
                        pendingLocalChanges,
                        cancellationToken);
                    var applied = ManagedReplicaLogicalReplayer.Apply(
                        connection,
                        transactions,
                        metadata.TableNamesByStableId,
                        metadata.ClientId,
                        cancellationToken,
                        pendingAddColumns);
                    operationCount = applied.OperationCount;
                    tableNamesByStableId = applied.TableNamesByStableId;
                    ExecuteNonQuery(connection, "COMMIT");
                }
                catch
                {
                    TryExecuteNonQuery(connection, "ROLLBACK");
                    throw;
                }

                ExecuteNonQuery(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
            }
            ValidateStagedDatabase(originalPath, options.RemoteEncryption);

            File.Copy(originalPath, committedPath, overwrite: false);
            using (var database = ManagedDatabaseAdapter.Open(committedPath))
            {
                var connection = database.Connect();
                ExecuteNonQuery(connection, "BEGIN IMMEDIATE");
                try
                {
                    ManagedReplicaLogicalReplayer.ReplayPendingLocalSchemaChanges(
                        connection,
                        pendingLocalChanges,
                        cancellationToken);
                    ManagedReplicaLogicalReplayer.ReplayPendingLocalRowChanges(
                        connection,
                        capturedLocalChanges,
                        cancellationToken);
                    ExecuteNonQuery(connection, "COMMIT");
                }
                catch
                {
                    TryExecuteNonQuery(connection, "ROLLBACK");
                    throw;
                }

                ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.LogicalApplyCommitted);
                cancellationToken.ThrowIfCancellationRequested();
                ExecuteNonQuery(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
                ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.LogicalApplyCheckpointed);
                cancellationToken.ThrowIfCancellationRequested();
            }
            ValidateStagedDatabase(committedPath, options.RemoteEncryption);

            var protectedMetadata = metadata with
            {
                Revision = header.Revision,
                DatabaseSha256 = ComputeDatabaseFingerprint(committedPath),
                Protocol = header.Protocol,
                TableNamesByStableId = tableNamesByStableId,
                RevertState = null,
                JournalBaseWatermark = AdvanceJournalBaseWatermark(metadata, acknowledgedLocalChanges),
                RemoteBaseSha256 = ComputeDatabaseFingerprint(originalPath),
            };
            var publishedRemoteBase = PublishRemoteBaseSnapshot(options.Path, metadata, originalPath);
            protectedMetadata = protectedMetadata with { RemoteBaseSha256 = publishedRemoteBase };
            _ = ManagedReplicaRevertWal.PublishProtectedSnapshots(
                options.Path,
                protectedMetadata,
                originalPath,
                committedPath,
                cancellationToken);
            CompleteRemoteBaseSnapshotPublication(options.Path);
        }
        finally
        {
            DeleteIfExists(originalPath);
            DeleteStagingSidecars(originalPath);
            DeleteIfExists(committedPath);
            DeleteStagingSidecars(committedPath);
        }

        syncOptions.Progress?.Report(new AhtolaSyncProgress(AhtolaSyncProgressStage.Applying));
        syncOptions.Progress?.Report(new AhtolaSyncProgress(AhtolaSyncProgressStage.Completed));
        return (
            AhtolaSyncOutcome.RemoteChangesApplied,
            new AhtolaSyncStatistics(
                operationCount,
                0,
                0,
                DateTimeOffset.UtcNow,
                null,
                networkSentBytes,
                networkReceivedBytes,
                header.Revision));
    }

    /// <summary>
    /// The file suffixes for a SQLite/Ahtola database's durability-relevant sidecar files,
    /// beyond the main database file itself: the WAL, its shared-memory index, and the legacy
    /// rollback journal. A logical-apply compensation snapshot must cover all of these, not just
    /// the main file, or a restore could leave a WAL that references pages the restored main file
    /// no longer has (or vice versa).
    /// </summary>
    private static readonly string[] DatabaseSidecarSuffixes = ["-wal", "-shm", "-journal"];

    private readonly record struct DatabaseArtifactBackup(
        string DatabasePath,
        string MainBackupPath,
        bool MainExisted,
        IReadOnlyList<(string SidecarPath, string BackupPath, bool Existed)> Sidecars);

    /// <summary>
    /// Snapshots the main database file and every present sidecar to sibling <c>.bak</c> files so
    /// <see cref="RestoreDatabaseArtifacts"/> can put every durable artifact back exactly as it
    /// was, including artifacts that did not exist before the apply (which are deleted, not just
    /// left as leftover empty files, on restore).
    /// </summary>
    private static DatabaseArtifactBackup BackupDatabaseArtifacts(string databasePath, string directory, string token)
    {
        var mainBackupPath = Path.Combine(directory, $".{Path.GetFileName(databasePath)}.logical-{token}.bak");
        var mainExisted = File.Exists(databasePath);
        if (mainExisted)
            File.Copy(databasePath, mainBackupPath, overwrite: true);

        var sidecars = new List<(string, string, bool)>(DatabaseSidecarSuffixes.Length);
        foreach (var suffix in DatabaseSidecarSuffixes)
        {
            var sidecarPath = databasePath + suffix;
            var sidecarBackupPath = Path.Combine(directory, $".{Path.GetFileName(databasePath)}{suffix}.logical-{token}.bak");
            var existed = File.Exists(sidecarPath);
            if (existed)
                File.Copy(sidecarPath, sidecarBackupPath, overwrite: true);
            sidecars.Add((sidecarPath, sidecarBackupPath, existed));
        }

        return new DatabaseArtifactBackup(databasePath, mainBackupPath, mainExisted, sidecars);
    }

    private static void RestoreDatabaseArtifacts(string databasePath, DatabaseArtifactBackup backup)
    {
        if (backup.MainExisted)
            File.Copy(backup.MainBackupPath, databasePath, overwrite: true);
        else
            DeleteIfExists(databasePath);

        foreach (var (sidecarPath, sidecarBackupPath, existed) in backup.Sidecars)
        {
            if (existed)
                File.Copy(sidecarBackupPath, sidecarPath, overwrite: true);
            else
                DeleteIfExists(sidecarPath);
        }
    }

    private static void DeleteBackupArtifacts(DatabaseArtifactBackup backup)
    {
        DeleteIfExists(backup.MainBackupPath);
        foreach (var (_, sidecarBackupPath, _) in backup.Sidecars)
            DeleteIfExists(sidecarBackupPath);
    }

    private static async Task<byte[]> ReadRemainingBytesAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxLogicalBodyLength)
                throw new InvalidDataException("The MVCC logical-log stream exceeds the supported size.");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static void ExecuteNonQuery(IManagedConnectionAdapter connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private static void TryExecuteNonQuery(IManagedConnectionAdapter connection, string sql)
    {
        try
        {
            ExecuteNonQuery(connection, sql);
        }
        catch
        {
            // Best effort: a failed ROLLBACK/checkpoint must not mask the original failure, and a
            // checkpoint pragma that the engine does not support for the current journal mode is
            // harmless (the commit already made the main file consistent in that mode).
        }
    }


    /// <summary>
    /// Records the local image that was just acknowledged by a committed remote push. This
    /// preserves the client identity, protocol, and table map, and lets the subsequent pull
    /// retain its divergence guard.
    /// </summary>
    public static async Task<ManagedReplicaMetadata> RecordLocalPushAsync(
        AhtolaReplicaOptions options,
        ManagedReplicaMetadata metadata,
        ManagedReplicaPageMaterializingFileSystem? retainedMaterializer,
        CancellationToken cancellationToken)
    {
        var publication = GetBootstrapPublicationInfo(options.Path);
        var statePath = options.Path + ManagedReplicaPageMaterializingFileSystem.StateSuffix;
        if (publication.RequiresPageState && !File.Exists(statePath))
        {
            throw new InvalidDataException(
                "Managed replica bootstrap state requires lazy-page state that is missing.");
        }

        metadata = ManagedReplicaRevertWal.PrepareSynchronization(options.Path, metadata);
        var pendingRevert = metadata.RevertState.HasValue;
        var metadataPath = options.Path + MetadataSuffix;
        var directory = Path.GetDirectoryName(Path.GetFullPath(metadataPath))!;
        var stagingPath = Path.Combine(
            directory,
            $".{Path.GetFileName(metadataPath)}.push-{Guid.NewGuid():N}.tmp");
        try
        {
            var fingerprint = ComputeDatabaseFingerprint(options.Path);
            if (File.Exists(statePath))
            {
                if (retainedMaterializer is not null)
                {
                    retainedMaterializer.MarkLocalMutationsPushed();
                }
                else
                {
                    using var fileSystem = new ManagedReplicaPageMaterializingFileSystem(
                        PhysicalFileSystem.Instance,
                        options.Path,
                        metadata.Revision,
                        new ManagedReplicaPullPageSource(options),
                        prefetchSegments: false);
                    fileSystem.MarkLocalMutationsPushed();
                }
            }

            await WriteMetadataAsync(
                    stagingPath,
                    metadataPath,
                    metadata.Revision,
                    fingerprint,
                    metadata.Protocol,
                    metadata.TableNamesByStableId,
                    cancellationToken,
                    replaceExisting: true,
                    clientId: metadata.ClientId,
                    journalBaseWatermark: metadata.JournalBaseWatermark,
                    remoteBaseSha256: metadata.RemoteBaseSha256)
                .ConfigureAwait(false);
            var updated = metadata with
            {
                DatabaseSha256 = fingerprint,
                RevertState = null,
            };
            if (pendingRevert)
            {
                ManagedReplicaRevertWal.Retire(options.Path);
                ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.RevertRetired);
            }
            return updated;
        }
        finally
        {
            DeleteIfExists(stagingPath);
        }
    }

    internal static async Task<ManagedReplicaMetadata> CompletePartialReplicaAsync(
        AhtolaReplicaOptions options,
        ManagedReplicaMetadata metadata,
        bool allowTrackedLocalMutations,
        ManagedReplicaPageMaterializingFileSystem? retainedMaterializer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var statePath = options.Path + ManagedReplicaPageMaterializingFileSystem.StateSuffix;
        var publication = GetBootstrapPublicationInfo(options.Path);
        if (publication.RequiresPageState && !File.Exists(statePath))
        {
            throw new InvalidDataException(
                "Managed replica bootstrap state requires lazy-page state that is missing.");
        }
        if (!File.Exists(statePath))
            return metadata;

        using var ownedFileSystem = retainedMaterializer is null
            ? new ManagedReplicaPageMaterializingFileSystem(
                PhysicalFileSystem.Instance,
                options.Path,
                metadata.Revision,
                new ManagedReplicaPullPageSource(options),
                prefetchSegments: false)
            : null;
        var fileSystem = retainedMaterializer ?? ownedFileSystem!;
        var hasLocalDivergence = fileSystem.HasLocalMutations
            || (File.Exists(options.Path + "-wal")
                && new FileInfo(options.Path + "-wal").Length > 32)
            || (File.Exists(options.Path + "-journal")
                && new FileInfo(options.Path + "-journal").Length > 0);
        if (metadata.Protocol == RemotePullProtocol.Pages
            && hasLocalDivergence
            && !allowTrackedLocalMutations
            && !fileSystem.LocalMutationsPushed)
        {
            throw new NotSupportedException(
                "Managed embedded replica local divergence was detected; incremental pull cannot replace local changes.");
        }

        ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.PartialImageCompletionStarted);
        await fileSystem.MaterializeAllAsync(cancellationToken).ConfigureAwait(false);

        var fingerprint = fileSystem.ComputeDatabaseFingerprint();
        var metadataPath = options.Path + MetadataSuffix;
        var directory = Path.GetDirectoryName(Path.GetFullPath(metadataPath))!;
        var stagingPath = Path.Combine(
            directory,
            $".{Path.GetFileName(metadataPath)}.materialized-{Guid.NewGuid():N}.tmp");
        try
        {
            await WriteMetadataAsync(
                    stagingPath,
                    metadataPath,
                    metadata.Revision,
                    fingerprint,
                    metadata.Protocol,
                    metadata.TableNamesByStableId,
                    cancellationToken,
                    replaceExisting: true,
                    clientId: metadata.ClientId)
                .ConfigureAwait(false);
            MarkBootstrapPublicationFull(options.Path);
            DeleteIfExists(statePath);
            return metadata with { DatabaseSha256 = fingerprint };
        }
        finally
        {
            DeleteIfExists(stagingPath);
        }
    }

    private static async Task ApplyIncrementalPagesAsync(
        AhtolaReplicaOptions options,
        PullHeader header,
        IReadOnlyList<PullPage> pages,
        ManagedReplicaMetadata metadata,
        IReadOnlyList<ReplicaLocalChange> pendingLocalChanges,
        IReadOnlyList<ReplicaLocalChange> acknowledgedLocalChanges,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(options.Path))!;
        var stagingPath = Path.Combine(directory, $".{Path.GetFileName(options.Path)}.apply-{Guid.NewGuid():N}.tmp");
        var backupPath = Path.Combine(directory, $".{Path.GetFileName(options.Path)}.apply-{Guid.NewGuid():N}.bak");
        var metadataPath = options.Path + MetadataSuffix;
        var metadataStagingPath = Path.Combine(
            directory,
            $".{Path.GetFileName(metadataPath)}.apply-{Guid.NewGuid():N}.tmp");
        var protectedStagingPath = Path.Combine(
            directory,
            $".{Path.GetFileName(options.Path)}.protected-{Guid.NewGuid():N}.tmp");
        var databaseInstalled = false;
        var metadataInstalled = false;
        IDisposable? mainFileReplacementLock = null;
        try
        {
            File.Copy(options.Path, stagingPath, overwrite: false);
            var pageIds = new HashSet<ulong>();
            await using (var staging = new FileStream(
                stagingPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: PageSize,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                staging.SetLength(checked((long)header.DatabasePages * PageSize));
                foreach (var page in pages)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (page.PageId >= header.DatabasePages || !pageIds.Add(page.PageId))
                        throw new InvalidDataException("The pull-updates response contains an invalid incremental page set.");

                    staging.Position = checked((long)page.PageId * PageSize);
                    await staging.WriteAsync(page.Data, cancellationToken).ConfigureAwait(false);
                }

                await staging.FlushAsync(cancellationToken).ConfigureAwait(false);
                staging.Flush(flushToDisk: true);
            }

            ValidateStagedDatabase(stagingPath, options.RemoteEncryption);
            ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.IncrementalApplyStagedDatabase);
            cancellationToken.ThrowIfCancellationRequested();
            if (header.ApplyMode == PullApplyMode.ReplaceBase && pendingLocalChanges.Count > 0)
            {
                File.Copy(stagingPath, protectedStagingPath, overwrite: false);
                using (var opened = ManagedReplicaEncryption.OpenDatabase(
                           protectedStagingPath,
                           options.RemoteEncryption))
                {
                    var connection = opened.Database.Connect();
                    ExecuteNonQuery(connection, "BEGIN IMMEDIATE");
                    try
                    {
                        ManagedReplicaLogicalReplayer.ReplayPendingLocalStatements(
                            connection,
                            pendingLocalChanges,
                            cancellationToken);
                        ExecuteNonQuery(connection, "COMMIT");
                    }
                    catch
                    {
                        TryExecuteNonQuery(connection, "ROLLBACK");
                        throw;
                    }

                    ExecuteNonQuery(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
                }
                ValidateStagedDatabase(protectedStagingPath, options.RemoteEncryption);
                var protectedMetadata = metadata with
                {
                    Revision = header.Revision,
                    DatabaseSha256 = ComputeDatabaseFingerprint(protectedStagingPath),
                    Protocol = header.Protocol,
                    TableNamesByStableId = RebuildTableMapFromSchema(
                        protectedStagingPath,
                        options.RemoteEncryption),
                    RevertState = null,
                    JournalBaseWatermark = AdvanceJournalBaseWatermark(metadata, acknowledgedLocalChanges),
                    RemoteBaseSha256 = ComputeDatabaseFingerprint(stagingPath),
                };
                var publishedRemoteBase = PublishRemoteBaseSnapshot(options.Path, metadata, stagingPath);
                protectedMetadata = protectedMetadata with { RemoteBaseSha256 = publishedRemoteBase };
                _ = ManagedReplicaRevertWal.PublishProtectedSnapshots(
                    options.Path,
                    protectedMetadata,
                    stagingPath,
                    protectedStagingPath,
                    cancellationToken);
                CompleteRemoteBaseSnapshotPublication(options.Path);
                return;
            }
            if (metadata.RevertState.HasValue)
                metadata = ManagedReplicaRevertWal.MarkRemoteApplyStarted(options.Path, metadata);
            mainFileReplacementLock = ManagedReplicaApplyLock.AcquireMainFileReplacementLock(
                options.Path,
                cancellationToken);
            File.Replace(stagingPath, options.Path, backupPath, ignoreMetadataErrors: false);
            mainFileReplacementLock?.Dispose();
            mainFileReplacementLock = null;
            databaseInstalled = true;
            ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.IncrementalApplyDatabasePublished);
            cancellationToken.ThrowIfCancellationRequested();

            if (header.ApplyMode == PullApplyMode.ReplaceBase && pendingLocalChanges.Count > 0)
            {
                using var opened = ManagedReplicaEncryption.OpenDatabase(options.Path, options.RemoteEncryption);
                var connection = opened.Database.Connect();
                ExecuteNonQuery(connection, "BEGIN IMMEDIATE");
                try
                {
                    ManagedReplicaLogicalReplayer.ReplayPendingLocalStatements(
                        connection, pendingLocalChanges, cancellationToken);
                    ExecuteNonQuery(connection, "COMMIT");
                }
                catch
                {
                    TryExecuteNonQuery(connection, "ROLLBACK");
                    throw;
                }

                ExecuteNonQuery(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
            }

            // A page-based apply never decodes logical schema identity operations, so the table
            // map is rebuilt fresh from the newly-installed schema (self-healing), matching
            // Turso's read_logical_replay_table_map usage after page-based apply paths. The
            // freshly detected protocol is recorded too, so a protocol-2 remote that answered this
            // particular pull with Pages (e.g. Pages+ReplaceBase) still enables a logical request
            // on the next pull rather than sticking to pages forever.
            var remoteBaseSha256 = PublishRemoteBaseSnapshot(options.Path, metadata, options.Path);
            var tableMap = RebuildTableMapFromSchema(options.Path, options.RemoteEncryption);
            var installedFingerprint = ComputeDatabaseFingerprint(options.Path);
            mainFileReplacementLock = ManagedReplicaApplyLock.AcquireMainFileReplacementLock(
                options.Path,
                cancellationToken);
            if (!string.Equals(
                    ComputeDatabaseFingerprint(options.Path),
                    installedFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Managed embedded replica changed while its replacement metadata was being published.");
            }
            await WriteMetadataAsync(
                    metadataStagingPath,
                    metadataPath,
                    header.Revision,
                    installedFingerprint,
                    header.Protocol,
                    tableMap,
                    cancellationToken,
                    replaceExisting: true,
                    clientId: metadata.ClientId,
                    journalBaseWatermark: AdvanceJournalBaseWatermark(metadata, acknowledgedLocalChanges),
                    remoteBaseSha256: remoteBaseSha256)
                .ConfigureAwait(false);
            metadataInstalled = true;
            CompleteRemoteBaseSnapshotPublication(options.Path);
            if (File.Exists(options.Path + ManagedReplicaRevertWal.Suffix))
            {
                ManagedReplicaRevertWal.Retire(options.Path);
                ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.RevertRetired);
            }
            ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.IncrementalApplyMetadataPublished);
            cancellationToken.ThrowIfCancellationRequested();
            DeleteIfExists(backupPath);
        }
        catch
        {
            // Once metadata is durable it fingerprints the installed image. A later
            // interruption must preserve that matched pair rather than restore only DB.
            if (databaseInstalled && !metadataInstalled && File.Exists(backupPath))
            {
                mainFileReplacementLock?.Dispose();
                mainFileReplacementLock = null;
                File.Replace(backupPath, options.Path, destinationBackupFileName: null, ignoreMetadataErrors: false);
            }
            throw;
        }
        finally
        {
            DeleteIfExists(stagingPath);
            DeleteStagingSidecars(stagingPath);
            DeleteIfExists(metadataStagingPath);
            DeleteIfExists(protectedStagingPath);
            DeleteStagingSidecars(protectedStagingPath);
            DeleteIfExists(backupPath);
            mainFileReplacementLock?.Dispose();
        }
    }

    private static async Task<InitialDownload> DownloadDatabaseAsync(
        AhtolaReplicaOptions options,
        string stagingPath,
        CancellationToken cancellationToken)
    {
        using var scope = options.EnterApplicationHttpScope();
        using var client = options.HttpPolicy.CreateHttpClient(options.RemoteEncryption is not null);
        client.Timeout = Timeout.InfiniteTimeSpan;
        var authToken = string.IsNullOrWhiteSpace(options.AuthToken) ? null : options.AuthToken;
        var chunkPages = options.PullBytesThreshold is { } threshold
            ? Math.Min(checked((ulong)((threshold - 1) / PageSize + 1)), uint.MaxValue)
            : (ulong?)null;
        var prefixPageCount = GetPrefixPageCount(options.PartialBootstrap);
        var firstRequestPages = chunkPages;
        if (prefixPageCount is { } prefixPages)
        {
            firstRequestPages = firstRequestPages is { } boundedPages
                ? Math.Min(boundedPages, prefixPages)
                : prefixPages;
        }

        var materializedPageIds = new HashSet<ulong>();
        await using (var staging = new FileStream(
            stagingPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: PageSize,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            var firstSelector = firstRequestPages is { } requestedPages
                ? CreatePageRangeSelector(0, checked((uint)requestedPages))
                : [];
            var header = await PullBootstrapChunkAsync(
                    client,
                    options,
                    authToken,
                    staging,
                    CreateBootstrapPullRequest(
                        serverRevision: null,
                        options.LongPollTimeout,
                        firstSelector),
                    expectedHeader: null,
                    requestedStart: 0,
                    requestedEnd: firstRequestPages,
                    materializedPageIds: materializedPageIds,
                    cancellationToken)
                .ConfigureAwait(false);
            var selectedPageCount = Math.Min(prefixPageCount ?? header.DatabasePages, header.DatabasePages);

            if (chunkPages is { } pagesPerChunk)
            {
                if (header.DatabasePages > uint.MaxValue)
                    throw new InvalidDataException("The remote database is too large for chunked page selection.");

                var start = firstRequestPages ?? pagesPerChunk;
                while (start < selectedPageCount)
                {
                    var end = Math.Min(start + pagesPerChunk, selectedPageCount);
                    var selector = CreatePageRangeSelector(checked((uint)start), checked((uint)end));
                    _ = await PullBootstrapChunkAsync(
                            client,
                            options,
                            authToken,
                            staging,
                            CreateBootstrapPullRequest(
                                header.Revision,
                                longPollTimeout: null,
                                selector),
                            header,
                            start,
                            end,
                            materializedPageIds,
                            cancellationToken)
                        .ConfigureAwait(false);
                    start = end;
                }
            }

            await staging.FlushAsync(cancellationToken).ConfigureAwait(false);
            staging.Flush(flushToDisk: true);
            return new InitialDownload(
                header.Revision,
                header.Protocol,
                header.DatabasePages,
                materializedPageIds.Order().ToArray(),
                selectedPageCount < header.DatabasePages);
        }
    }

    private static async Task<PullHeader> PullBootstrapChunkAsync(
        HttpClient client,
        AhtolaReplicaOptions options,
        string? authToken,
        FileStream staging,
        byte[] requestPayload,
        PullHeader? expectedHeader,
        ulong requestedStart,
        ulong? requestedEnd,
        HashSet<ulong> materializedPageIds,
        CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeout(options.HttpPolicy.RequestTimeout, cancellationToken);
        var effectiveCancellationToken = timeout?.Token ?? cancellationToken;
        using var response = await AhtolaRemoteTransportSecurity
            .SendAsync(
                client,
                CreatePullUpdatesUri(options.RemoteUri),
                requestUri => CreatePullUpdatesHttpRequest(
                    requestUri,
                    requestPayload,
                    authToken,
                    options.RemoteEncryption),
                authToken,
                remoteEncryptionConfigured: options.RemoteEncryption is not null,
                HttpCompletionOption.ResponseHeadersRead,
                effectiveCancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(effectiveCancellationToken).ConfigureAwait(false);
        var reader = new DelimitedProtobufReader(stream);
        var headerPayload = await reader.ReadAsync(MaxHeaderLength, effectiveCancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The pull-updates response did not contain a protobuf header.");
        var header = ParseHeader(headerPayload);
        if (header.StreamKind != PullStreamKind.Pages)
        {
            throw new InvalidDataException(
                "Managed embedded replica bootstrap requires a raw page stream; the server returned an MVCC logical-log stream.");
        }
        if (options.RemoteEncryption is not null && header.Protocol == RemotePullProtocol.MvccLogical)
        {
            // Fail closed as soon as the remote's advertised protocol is known, before ever
            // installing a bootstrap image that would require an unsupported MVCC-logical
            // catch-up pull later (see CheckForUpdatesAsync's matching guard). Mirrors Turso's
            // ensure_logical_mvcc_pull_supported: MVCC logical pull and remote encryption are not
            // a supported combination.
            throw new NotSupportedException(
                "Managed embedded replica bootstrap does not support a remote that advertises the "
                + "MVCC logical pull protocol combined with remote encryption.");
        }

        if (expectedHeader is { } expected
            && (header.Revision != expected.Revision
                || header.DatabasePages != expected.DatabasePages
                || header.ApplyMode != expected.ApplyMode
                || header.Protocol != expected.Protocol))
        {
            throw new InvalidDataException(
                "A chunked bootstrap response did not match the initial database revision and shape.");
        }

        if (expectedHeader is null)
            staging.SetLength(checked((long)header.DatabasePages * PageSize));

        var expectedEnd = Math.Min(requestedEnd ?? header.DatabasePages, header.DatabasePages);
        var receivedPages = new HashSet<ulong>();
        while (await reader.ReadAsync(MaxPageMessageLength, effectiveCancellationToken).ConfigureAwait(false) is { } pagePayload)
        {
            var page = ParsePage(pagePayload);
            if (page.PageId < requestedStart || page.PageId >= expectedEnd)
                throw new InvalidDataException("The pull-updates response contains a page outside the requested bootstrap range.");
            if (!receivedPages.Add(page.PageId))
                throw new InvalidDataException("The pull-updates response contains a duplicate page.");
            materializedPageIds.Add(page.PageId);

            staging.Position = checked((long)page.PageId * PageSize);
            await staging.WriteAsync(page.Data, effectiveCancellationToken).ConfigureAwait(false);
        }

        if ((ulong)receivedPages.Count != expectedEnd - requestedStart)
        {
            throw new InvalidDataException(
                "The pull-updates response did not contain every requested database page exactly once.");
        }

        return header;
    }

    private static byte[] CreatePageRangeSelector(uint startInclusive, uint endExclusive)
    {
        if (startInclusive >= endExclusive)
            throw new ArgumentOutOfRangeException(nameof(endExclusive), endExclusive, "The page range must not be empty.");

        const uint serialCookie = 12347;
        var firstKey = startInclusive >> 16;
        var lastKey = (endExclusive - 1) >> 16;
        var containerCount = checked((int)(lastKey - firstKey + 1));
        var runBitmapLength = (containerCount + 7) / 8;
        var hasOffsets = containerCount >= 4;
        var headerLength = checked(
            sizeof(uint)
            + runBitmapLength
            + containerCount * (sizeof(ushort) * 2)
            + (hasOffsets ? containerCount * sizeof(uint) : 0));
        var result = new byte[checked(headerLength + containerCount * (sizeof(ushort) * 3))];
        BinaryPrimitives.WriteUInt32LittleEndian(
            result,
            serialCookie | checked((uint)(containerCount - 1) << 16));

        for (var index = 0; index < containerCount; index++)
            result[sizeof(uint) + index / 8] |= checked((byte)(1 << (index % 8)));

        var descriptionsOffset = sizeof(uint) + runBitmapLength;
        for (var index = 0; index < containerCount; index++)
        {
            var key = checked((ushort)(firstKey + (uint)index));
            var runStart = index == 0 ? checked((ushort)(startInclusive & ushort.MaxValue)) : (ushort)0;
            var runEnd = index == containerCount - 1
                ? checked((ushort)((endExclusive - 1) & ushort.MaxValue))
                : ushort.MaxValue;
            var descriptionOffset = descriptionsOffset + index * (sizeof(ushort) * 2);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(descriptionOffset), key);
            BinaryPrimitives.WriteUInt16LittleEndian(
                result.AsSpan(descriptionOffset + sizeof(ushort)),
                checked((ushort)(runEnd - runStart)));
        }

        var containersOffset = headerLength;
        if (hasOffsets)
        {
            var offsetsOffset = descriptionsOffset + containerCount * (sizeof(ushort) * 2);
            for (var index = 0; index < containerCount; index++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    result.AsSpan(offsetsOffset + index * sizeof(uint)),
                    checked((uint)(containersOffset + index * (sizeof(ushort) * 3))));
            }
        }

        for (var index = 0; index < containerCount; index++)
        {
            var runStart = index == 0 ? checked((ushort)(startInclusive & ushort.MaxValue)) : (ushort)0;
            var runEnd = index == containerCount - 1
                ? checked((ushort)((endExclusive - 1) & ushort.MaxValue))
                : ushort.MaxValue;
            var containerOffset = containersOffset + index * (sizeof(ushort) * 3);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(containerOffset), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(containerOffset + sizeof(ushort)), runStart);
            BinaryPrimitives.WriteUInt16LittleEndian(
                result.AsSpan(containerOffset + sizeof(ushort) * 2),
                checked((ushort)(runEnd - runStart)));
        }

        return result;
    }

    internal static async Task<ManagedReplicaPageBatch> FetchPagesAsync(
        AhtolaReplicaOptions options,
        string revision,
        IReadOnlyList<ulong> pageIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        ArgumentNullException.ThrowIfNull(pageIds);
        if (pageIds.Count == 0)
            throw new ArgumentException("At least one page must be requested.", nameof(pageIds));

        var requestedPageIds = pageIds.OrderBy(static pageId => pageId).ToArray();
        for (var index = 0; index < requestedPageIds.Length; index++)
        {
            if (requestedPageIds[index] > uint.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(pageIds), "A requested page exceeds the wire page-id limit.");
            if (index > 0 && requestedPageIds[index] == requestedPageIds[index - 1])
                throw new ArgumentException("Targeted page requests cannot contain duplicates.", nameof(pageIds));
        }

        var payload = CreateTargetedPagePullRequest(revision, requestedPageIds);
        using var timeout = CreateTimeout(options.HttpPolicy.RequestTimeout, cancellationToken);
        var effectiveCancellationToken = timeout?.Token ?? cancellationToken;
        using var scope = options.EnterApplicationHttpScope();
        using var client = options.HttpPolicy.CreateHttpClient(options.RemoteEncryption is not null);
        client.Timeout = Timeout.InfiniteTimeSpan;
        var authToken = string.IsNullOrWhiteSpace(options.AuthToken) ? null : options.AuthToken;
        using var response = await AhtolaRemoteTransportSecurity
            .SendAsync(
                client,
                CreatePullUpdatesUri(options.RemoteUri),
                requestUri => CreatePullUpdatesHttpRequest(
                    requestUri,
                    payload,
                    authToken,
                    options.RemoteEncryption),
                authToken,
                remoteEncryptionConfigured: options.RemoteEncryption is not null,
                HttpCompletionOption.ResponseHeadersRead,
                effectiveCancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content
            .ReadAsStreamAsync(effectiveCancellationToken)
            .ConfigureAwait(false);
        var reader = new DelimitedProtobufReader(stream);
        var headerPayload = await reader
            .ReadAsync(MaxHeaderLength, effectiveCancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("The targeted page response did not contain a protobuf header.");
        var header = ParseHeader(headerPayload);
        if (header.StreamKind != PullStreamKind.Pages)
            throw new InvalidDataException("The targeted page response returned a logical-log stream.");
        if (!string.Equals(header.Revision, revision, StringComparison.Ordinal))
            throw new InvalidDataException("The targeted page response belongs to a different server revision.");

        var requested = new HashSet<ulong>(requestedPageIds);
        var received = new HashSet<ulong>();
        var pages = new List<ManagedReplicaFetchedPage>(requested.Count);
        while (await reader.ReadAsync(MaxPageMessageLength, effectiveCancellationToken).ConfigureAwait(false) is { } pagePayload)
        {
            var page = ParsePage(pagePayload);
            if (page.PageId >= header.DatabasePages)
                throw new InvalidDataException("The targeted page response contains a page outside the declared database.");
            if (!requested.Contains(page.PageId))
                throw new InvalidDataException("The targeted page response contains a page that was not requested.");
            if (!received.Add(page.PageId))
                throw new InvalidDataException("The targeted page response contains a duplicate page.");
            pages.Add(new ManagedReplicaFetchedPage(page.PageId, page.Data));
        }

        foreach (var pageId in requestedPageIds)
        {
            if (pageId >= header.DatabasePages || !received.Contains(pageId))
                throw new InvalidDataException("The targeted page response omitted a requested page.");
        }

        return new ManagedReplicaPageBatch(header.Revision, header.DatabasePages, pages);
    }


    private static void ValidateStagedDatabase(
        string stagingPath,
        AhtolaRemoteEncryptionOptions? remoteEncryption,
        bool allowMissingPages = false)
    {
        if (remoteEncryption is null)
        {
            using var stream = new FileStream(stagingPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[SqliteHeader.Length];
            stream.ReadExactly(header);
            if (!header.SequenceEqual(SqliteHeader))
                throw new InvalidDataException("The bootstrapped page stream does not contain a SQLite database header.");
        }

        if (allowMissingPages)
            return;

        // For an encrypted stream, page 1 begins with the Ahtola encrypted-page magic rather than
        // the plaintext SQLite header, so the plaintext pre-check above is skipped: opening below
        // exercises the storage layer's own encrypted-header/reserved-byte validation instead
        // (see SqlitePageStore.OpenCore/OpenWithCodec), which fails closed on any mismatch.
        using var opened = ManagedReplicaEncryption.OpenDatabase(stagingPath, remoteEncryption);
        _ = opened.Database.Connect();
    }

    private static async Task WriteMetadataAsync(
        string stagingPath,
        string metadataPath,
        string revision,
        string fingerprint,
        RemotePullProtocol protocol,
        IReadOnlyDictionary<ulong, string> tableNamesByStableId,
        CancellationToken cancellationToken,
        bool replaceExisting = false,
        string? clientId = null,
        ManagedReplicaRevertState? revertState = null,
        long journalBaseWatermark = 1,
        string? remoteBaseSha256 = null)
    {
        var metadata = CreateMetadataBytes(
            revision,
            fingerprint,
            protocol,
            tableNamesByStableId,
            clientId ?? Guid.NewGuid().ToString("N"),
            revertState,
            journalBaseWatermark,
            remoteBaseSha256 ?? fingerprint);
        await using (var stream = new FileStream(
            stagingPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(metadata, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        if (replaceExisting && File.Exists(metadataPath))
            File.Replace(stagingPath, metadataPath, destinationBackupFileName: null, ignoreMetadataErrors: false);
        else
            File.Move(stagingPath, metadataPath, overwrite: false);
    }

    internal static void WriteMetadata(
        string stagingPath,
        string metadataPath,
        ManagedReplicaMetadata metadata,
        ManagedReplicaRevertState? revertState)
    {
        var bytes = CreateMetadataBytes(
            metadata.Revision,
            metadata.DatabaseSha256,
            metadata.Protocol,
            metadata.TableNamesByStableId,
            metadata.ClientId,
            revertState,
            metadata.JournalBaseWatermark,
            metadata.RemoteBaseSha256);
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

        File.Replace(
            stagingPath,
            metadataPath,
            destinationBackupFileName: null,
            ignoreMetadataErrors: false);
    }

    private static byte[] CreateMetadataBytes(
        string revision,
        string fingerprint,
        RemotePullProtocol protocol,
        IReadOnlyDictionary<ulong, string> tableNamesByStableId,
        string clientId,
        ManagedReplicaRevertState? revertState,
        long journalBaseWatermark,
        string remoteBaseSha256)
    {
        var protocolText = protocol switch
        {
            RemotePullProtocol.Unknown => "unknown",
            RemotePullProtocol.Pages => "pages",
            RemotePullProtocol.MvccLogical => "mvcc_logical",
            _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, "Unknown remote pull protocol."),
        };
        var metadata = string.Concat(
            revertState is null ? "version=5\n" : "version=6\n",
            "server_revision_base64=", Convert.ToBase64String(StrictUtf8.GetBytes(revision)), "\n",
            "database_sha256=", fingerprint, "\n",
            "client_id=", clientId, "\n",
            "protocol=", protocolText, "\n",
            "table_map_base64=", Convert.ToBase64String(EncodeTableMap(tableNamesByStableId)), "\n",
            "journal_base_watermark=", journalBaseWatermark.ToString(CultureInfo.InvariantCulture), "\n",
            "remote_base_sha256=", remoteBaseSha256, "\n",
            revertState is { } value
                ? string.Concat("revert_state_base64=", Convert.ToBase64String(EncodeRevertState(value)), "\n")
                : string.Empty);
        return Encoding.UTF8.GetBytes(metadata);
    }

    private static PullHeader ParseHeader(byte[] payload)
    {
        var reader = new ProtobufFieldReader(payload);
        string? revision = null;
        ulong? databasePages = null;
        var rawEncoding = false;
        var zstdEncoding = false;
        ulong? streamKind = null;
        ulong? applyMode = null;
        ulong? protocol = null;
        ManagedReplicaLogicalLogMetadata? logicalMetadata = null;

        while (reader.TryReadField(out var field, out var wireType))
        {
            switch (field)
            {
                case 1:
                    revision = ReadSingleString(ref reader, wireType, revision, "server revision");
                    break;
                case 2:
                    databasePages = ReadSingleVarint(ref reader, wireType, databasePages, "database size");
                    break;
                case 3:
                    ReadEmptyMessage(ref reader, wireType, "raw encoding");
                    if (rawEncoding)
                        throw new InvalidDataException("The pull-updates header contains raw encoding more than once.");
                    rawEncoding = true;
                    break;
                case 4:
                    reader.SkipField(wireType);
                    zstdEncoding = true;
                    break;
                case 5:
                    streamKind = ReadSingleVarint(ref reader, wireType, streamKind, "stream kind");
                    break;
                case 6:
                    applyMode = ReadSingleVarint(ref reader, wireType, applyMode, "apply mode");
                    break;
                case 7:
                    if (logicalMetadata is not null)
                        throw new InvalidDataException("The pull-updates response contains MVCC logical-log metadata more than once.");
                    logicalMetadata = ParseLogicalLogMetadata(reader.ReadLengthDelimited(wireType, "MVCC logical-log metadata"));
                    break;
                case 8:
                    protocol = ReadSingleVarint(ref reader, wireType, protocol, "protocol");
                    break;
                default:
                    reader.SkipField(wireType);
                    break;
            }
        }

        if (!rawEncoding || zstdEncoding)
            throw new InvalidDataException("Managed embedded replica bootstrap requires a raw, non-zstd page stream.");
        if (revision is null || revision.Length == 0)
            throw new InvalidDataException("The pull-updates response did not provide a server revision.");
        if (databasePages is not { } pageCount || pageCount == 0 || pageCount > (ulong)(long.MaxValue / PageSize))
            throw new InvalidDataException("The pull-updates response has an invalid database size.");
        if (streamKind is > 1)
            throw new InvalidDataException("The pull-updates response has an unsupported stream kind.");
        if (applyMode is > 1)
            throw new InvalidDataException("The pull-updates response has an unsupported apply mode.");

        var resolvedStreamKind = streamKind == 1 ? PullStreamKind.MvccLogicalLog : PullStreamKind.Pages;
        var resolvedApplyMode = applyMode == 1 ? PullApplyMode.ReplaceBase : PullApplyMode.Incremental;
        // A server predating the protocol field, or reporting a future unknown value, is treated
        // as page-only: MVCC databases only exist behind servers that advertise protocol=2.
        var resolvedProtocol = protocol == 2 ? RemotePullProtocol.MvccLogical : RemotePullProtocol.Pages;

        // Metadata (tag 7) is intentionally optional even for a logical stream: a genuinely empty
        // logical response (nothing new) may omit it entirely, with an empty body. The caller
        // validates that combination against the body length, matching Turso's
        // decode_raw_mvcc_logical_log_to_file (missing metadata + empty body => zero transactions).
        return new PullHeader(revision, pageCount, resolvedStreamKind, resolvedApplyMode, resolvedProtocol, logicalMetadata);
    }

    private static ManagedReplicaLogicalLogMetadata ParseLogicalLogMetadata(byte[] payload)
    {
        var reader = new ProtobufFieldReader(payload);
        string? format = null;
        var checkpointTransition = false;
        var ranges = new List<ManagedReplicaLogicalLogRange>();

        while (reader.TryReadField(out var field, out var wireType))
        {
            switch (field)
            {
                case 1:
                    if (format is not null)
                        throw new InvalidDataException("The MVCC logical-log metadata contains a format more than once.");
                    format = StrictUtf8.GetString(reader.ReadLengthDelimited(wireType, "MVCC logical-log format"));
                    break;
                case 2:
                    checkpointTransition = reader.ReadVarint(wireType, "MVCC logical-log checkpoint transition") != 0;
                    break;
                case 3:
                    ranges.Add(ParseLogicalLogRange(reader.ReadLengthDelimited(wireType, "MVCC logical-log range")));
                    break;
                default:
                    reader.SkipField(wireType);
                    break;
            }
        }

        if (string.IsNullOrEmpty(format))
            throw new InvalidDataException("The MVCC logical-log metadata is missing its format.");
        if (!string.Equals(format, ManagedReplicaLml3Decoder.ExpectedFormat, StringComparison.Ordinal))
            throw new InvalidDataException($"The MVCC logical-log metadata has an unsupported format '{format}'.");

        return new ManagedReplicaLogicalLogMetadata(format, checkpointTransition, ranges);
    }

    private static ManagedReplicaLogicalLogRange ParseLogicalLogRange(byte[] payload)
    {
        var reader = new ProtobufFieldReader(payload);
        ulong? generation = null;
        ulong? startOffset = null;
        ulong? endOffset = null;
        var startsWithHeader = false;
        byte[]? crcSeed = null;

        while (reader.TryReadField(out var field, out var wireType))
        {
            switch (field)
            {
                case 1:
                    generation = ReadSingleVarint(ref reader, wireType, generation, "range generation");
                    break;
                case 2:
                    startOffset = ReadSingleVarint(ref reader, wireType, startOffset, "range start offset");
                    break;
                case 3:
                    endOffset = ReadSingleVarint(ref reader, wireType, endOffset, "range end offset");
                    break;
                case 4:
                    startsWithHeader = reader.ReadVarint(wireType, "range starts_with_header") != 0;
                    break;
                case 5:
                    if (crcSeed is not null)
                        throw new InvalidDataException("The MVCC logical-log range contains a CRC seed more than once.");
                    crcSeed = reader.ReadLengthDelimited(wireType, "range CRC seed");
                    break;
                default:
                    reader.SkipField(wireType);
                    break;
            }
        }

        // generation, start_offset and end_offset are non-optional proto3 scalars, so the server omits them at
        // zero. A first range starts at offset 0 and carries no tag 2 at all.
        return new ManagedReplicaLogicalLogRange(
            generation ?? 0,
            startOffset ?? 0,
            endOffset ?? 0,
            startsWithHeader,
            crcSeed);
    }

    private static PullPage ParsePage(byte[] payload)
    {
        var reader = new ProtobufFieldReader(payload);
        ulong? pageId = null;
        byte[]? pageData = null;

        while (reader.TryReadField(out var field, out var wireType))
        {
            switch (field)
            {
                case 1:
                    pageId = ReadSingleVarint(ref reader, wireType, pageId, "page id");
                    break;
                case 2:
                    if (pageData is not null)
                        throw new InvalidDataException("A page message contains page data more than once.");
                    pageData = reader.ReadLengthDelimited(wireType, "page data");
                    break;
                default:
                    reader.SkipField(wireType);
                    break;
            }
        }

        if (pageData is null || pageData.Length != PageSize)
            throw new InvalidDataException("The pull-updates response contains an invalid raw database page.");

        // Protobuf omits scalar fields at their default value. Turso's page-zero
        // messages therefore carry only encoded_page (server_proto.rs::PageData).
        return new PullPage(pageId ?? 0, pageData);
    }

    private static string ReadSingleString(
        ref ProtobufFieldReader reader,
        int wireType,
        string? currentValue,
        string name)
    {
        if (currentValue is not null)
            throw new InvalidDataException($"The pull-updates response contains {name} more than once.");
        return StrictUtf8.GetString(reader.ReadLengthDelimited(wireType, name));
    }

    private static ulong ReadSingleVarint(
        ref ProtobufFieldReader reader,
        int wireType,
        ulong? currentValue,
        string name)
    {
        if (currentValue is not null)
            throw new InvalidDataException($"The pull-updates response contains {name} more than once.");
        return reader.ReadVarint(wireType, name);
    }

    private static void ReadEmptyMessage(ref ProtobufFieldReader reader, int wireType, string name)
    {
        if (reader.ReadLengthDelimited(wireType, name).Length != 0)
            throw new InvalidDataException($"The pull-updates response has an unsupported {name} payload.");
    }

    private static byte[] CreateBootstrapPullRequest(
        string? serverRevision,
        TimeSpan? longPollTimeout,
        byte[] serverPagesSelector)
        => CreatePullRequest(
            serverRevision,
            clientRevision: null,
            longPollTimeout,
            requestLogicalProtocol: false,
            serverPagesSelector);

    /// <summary>
    /// Builds a <c>PullUpdatesReqProtoBody</c> request. Tag 1 (<c>encoding</c>) is explicitly
    /// emitted as <c>PageUpdatesEncodingReq.Raw</c> (0), even though that is the proto3 default,
    /// so the wire request negotiates the only page encoding this managed client supports.
    /// <paramref name="clientRevision"/> is
    /// always emitted (tag 3) whenever it is non-empty, independent of whether a long-poll
    /// timeout is configured: the server cannot compute an incremental diff without it. Setting
    /// <paramref name="requestLogicalProtocol"/> encodes tag 8 (<c>stream_kind</c>) as
    /// <c>MvccLogicalLog</c> (1) instead of leaving it at its <c>Pages</c> (0) default.
    /// </summary>
    private static byte[] CreatePullRequest(
        string? clientRevision,
        TimeSpan? longPollTimeout,
        bool requestLogicalProtocol)
    {
        return CreatePullRequest(
            serverRevision: null,
            clientRevision,
            longPollTimeout,
            requestLogicalProtocol,
            serverPagesSelector: []);
    }

    private static byte[] CreatePullRequest(
        string? serverRevision,
        string? clientRevision,
        TimeSpan? longPollTimeout,
        bool requestLogicalProtocol,
        byte[] serverPagesSelector)
    {
        var request = new List<byte>(
            (serverRevision?.Length ?? 0)
            + (clientRevision?.Length ?? 0)
            + serverPagesSelector.Length
            + 18);
        WriteVarint(request, 1u << 3);
        WriteVarint(request, 0); // PageUpdatesEncodingReq::Raw
        if (!string.IsNullOrEmpty(serverRevision))
        {
            var revision = StrictUtf8.GetBytes(serverRevision);
            WriteVarint(request, 2u << 3 | 2);
            WriteVarint(request, checked((ulong)revision.Length));
            request.AddRange(revision);
        }
        if (!string.IsNullOrEmpty(clientRevision))
        {
            var revision = StrictUtf8.GetBytes(clientRevision);
            WriteVarint(request, 3u << 3 | 2);
            WriteVarint(request, checked((ulong)revision.Length));
            request.AddRange(revision);
        }
        if (longPollTimeout is { } timeout)
        {
            WriteVarint(request, 4u << 3);
            WriteVarint(request, checked((ulong)timeout.TotalMilliseconds));
        }
        if (serverPagesSelector.Length != 0)
        {
            WriteVarint(request, 5u << 3 | 2);
            WriteVarint(request, checked((ulong)serverPagesSelector.Length));
            request.AddRange(serverPagesSelector);
        }
        if (requestLogicalProtocol)
        {
            WriteVarint(request, 8u << 3);
            WriteVarint(request, 1); // PullUpdatesStreamKind::MvccLogicalLog
        }
        return request.ToArray();
    }

    private static uint? GetPrefixPageCount(AhtolaPartialBootstrapOptions? partialBootstrap)
        => partialBootstrap?.Kind == AhtolaPartialBootstrapKind.Prefix
            ? checked((uint)(partialBootstrap.PrefixLength / PageSize))
            : null;

    private static byte[] CreateTargetedPagePullRequest(
        string revision,
        IReadOnlyList<ulong> pageIds)
    {
        var revisionBytes = StrictUtf8.GetBytes(revision);
        var selector = SerializeRoaringPageSelector(pageIds);
        var request = new List<byte>(checked(revisionBytes.Length + selector.Length + 16));
        WriteVarint(request, 2u << 3 | 2);
        WriteVarint(request, checked((ulong)revisionBytes.Length));
        request.AddRange(revisionBytes);
        WriteVarint(request, 5u << 3 | 2);
        WriteVarint(request, checked((ulong)selector.Length));
        request.AddRange(selector);
        return request.ToArray();
    }

    /// <summary>
    /// Writes the portable RoaringBitmap format used by Turso's
    /// <c>server_pages_selector</c>. Run containers are intentionally omitted;
    /// array and bitmap containers cover every page set without another package.
    /// </summary>
    private static byte[] SerializeRoaringPageSelector(IReadOnlyList<ulong> pageIds)
    {
        var containers = new SortedDictionary<ushort, List<ushort>>();
        foreach (var pageId in pageIds)
        {
            var value = checked((uint)pageId);
            var key = checked((ushort)(value >> 16));
            var low = checked((ushort)(value & ushort.MaxValue));
            if (!containers.TryGetValue(key, out var lows))
            {
                lows = [];
                containers.Add(key, lows);
            }
            lows.Add(low);
        }

        var serializedContainers = new RoaringContainer[containers.Count];
        var containerIndex = 0;
        foreach (var (key, lows) in containers)
        {
            lows.Sort();
            serializedContainers[containerIndex++] = new RoaringContainer(
                key,
                lows.Count,
                lows.ToArray());
        }

        return SerializeRoaringPageSelector(serializedContainers);
    }

    private static byte[] SerializeRoaringPageSelector(
        IReadOnlyList<RoaringContainer> containers)
    {
        const uint noRunContainerCookie = 12346;
        const int maximumArrayCardinality = 4096;
        const int bitmapContainerSize = 8192;
        if (containers.Count == 0)
            throw new ArgumentException("At least one RoaringBitmap container is required.", nameof(containers));

        var headerLength = checked(8 + containers.Count * 8);
        var dataLength = 0;
        foreach (var container in containers)
        {
            if (container.Cardinality is < 1 or > 65536
                || (container.Values is not null
                    && container.Values.Length != container.Cardinality))
            {
                throw new InvalidDataException("A RoaringBitmap container has an invalid cardinality.");
            }

            dataLength = checked(dataLength + (container.Cardinality <= maximumArrayCardinality
                ? container.Cardinality * sizeof(ushort)
                : bitmapContainerSize));
        }

        var bytes = new byte[checked(headerLength + dataLength)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, noRunContainerCookie);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), checked((uint)containers.Count));
        var dataOffset = headerLength;
        for (var containerIndex = 0; containerIndex < containers.Count; containerIndex++)
        {
            var container = containers[containerIndex];
            var descriptorOffset = checked(8 + containerIndex * 4);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(descriptorOffset), container.Key);
            BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan(descriptorOffset + 2),
                checked((ushort)(container.Cardinality - 1)));
            var offsetOffset = checked(8 + containers.Count * 4 + containerIndex * 4);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offsetOffset), checked((uint)dataOffset));

            if (container.Cardinality <= maximumArrayCardinality)
            {
                if (container.Values is null)
                {
                    for (var low = 0; low < container.Cardinality; low++)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(
                            bytes.AsSpan(dataOffset),
                            checked((ushort)low));
                        dataOffset += sizeof(ushort);
                    }
                }
                else
                {
                    foreach (var low in container.Values)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(dataOffset), low);
                        dataOffset += sizeof(ushort);
                    }
                }
            }
            else if (container.Values is null)
            {
                for (var word = 0; word < 1024; word++)
                {
                    var remainingBits = container.Cardinality - word * 64;
                    var value = remainingBits >= 64
                        ? ulong.MaxValue
                        : remainingBits <= 0
                            ? 0
                            : (1UL << remainingBits) - 1;
                    BinaryPrimitives.WriteUInt64LittleEndian(
                        bytes.AsSpan(dataOffset + word * sizeof(ulong)),
                        value);
                }
                dataOffset += bitmapContainerSize;
            }
            else
            {
                foreach (var low in container.Values)
                    bytes[dataOffset + low / 8] |= checked((byte)(1 << (low & 7)));
                dataOffset += bitmapContainerSize;
            }
        }

        return bytes;
    }

    private static Uri CreatePullUpdatesUri(Uri endpoint)
    {
        var builder = new UriBuilder(endpoint)
        {
            Query = string.Empty,
            Fragment = string.Empty,
        };
        var path = builder.Path;
        builder.Path = string.IsNullOrEmpty(path) || path == "/"
            ? "/pull-updates"
            : path.TrimEnd('/').EndsWith("/pull-updates", StringComparison.OrdinalIgnoreCase)
                ? path
                : path.TrimEnd('/') + "/pull-updates";
        return builder.Uri;
    }

    private static CancellationTokenSource? CreateTimeout(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
            return null;

        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }

    private static HttpRequestMessage CreatePullUpdatesHttpRequest(
        Uri requestUri,
        byte[] payload,
        string? authToken,
        AhtolaRemoteEncryptionOptions? remoteEncryption)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new ByteArrayContent(payload),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/protobuf");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "application/protobuf");
        if (authToken is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
        if (remoteEncryption is not null)
        {
            request.Headers.TryAddWithoutValidation(
                AhtolaRemoteClient.EncryptionKeyHeaderName,
                remoteEncryption.Base64Key);
        }

        return request;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    internal static BootstrapPublicationInfo GetBootstrapPublicationInfo(string databasePath)
    {
        var path = databasePath + BootstrapStateSuffix;
        if (!File.Exists(path))
            return new BootstrapPublicationInfo(
                MarkerExists: false,
                IsComplete: true,
                RequiresPageState: false);

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var status = BootstrapPublication.ReadStatus(stream);
            return new BootstrapPublicationInfo(
                MarkerExists: true,
                IsComplete: status is BootstrapPublicationStatus.Complete
                    or BootstrapPublicationStatus.CompletePartial,
                RequiresPageState: status == BootstrapPublicationStatus.CompletePartial);
        }
        catch (IOException)
        {
            // The writer holds the bootstrap range lock, or publication was interrupted while
            // the marker was being replaced. In either case no database may be opened yet.
            return new BootstrapPublicationInfo(
                MarkerExists: true,
                IsComplete: false,
                RequiresPageState: false);
        }
    }

    internal static bool IsBootstrapPublicationComplete(string databasePath)
        => GetBootstrapPublicationInfo(databasePath).IsComplete;

    private static void MarkBootstrapPublicationFull(string databasePath)
    {
        using var publication = BootstrapPublication.Acquire(databasePath);
        if (publication.RequiresRecovery)
        {
            throw new InvalidDataException(
                "Managed replica bootstrap state became inconsistent while completing its partial image.");
        }

        publication.MarkComplete(isPartial: false);
    }

    private static void DeleteStagingSidecars(string path)
    {
        DeleteIfExists(path + "-wal");
        DeleteIfExists(path + "-shm");
        DeleteIfExists(path + "-journal");
    }

    private static void WriteVarint(List<byte> destination, ulong value)
    {
        while (value >= 0x80)
        {
            destination.Add((byte)((value & 0x7f) | 0x80));
            value >>= 7;
        }
        destination.Add((byte)value);
    }

    /// <summary>
    /// Rejects a replica whose file bytes drifted from what the last sync recorded, without a
    /// managed avenue back to reconciled state.
    /// </summary>
    /// <remarks>
    /// The page protocol supports only a raw whole/partial-page replacement apply: it has no way
    /// to reconcile local writes made between syncs, so any local WAL/journal activity or main-
    /// file byte drift there is necessarily unmanaged and must be rejected.
    /// </remarks>
    /// <remarks>
    /// The MVCC logical protocol is different: it is explicitly designed to keep serving local
    /// writes between syncs (they are captured by the local change journal, pushed on a later
    /// sync, and reconciled around each pull by precollecting and replaying still-unpushed
    /// changes on top of the new remote base — see
    /// <see cref="ManagedReplicaLogicalReplayer.CapturePendingLocalRowChanges"/>). An evolving WAL
    /// and a main-file fingerprint that drifts from the last recorded one are therefore the
    /// expected steady state for this protocol, not evidence of an unmanaged, out-of-band
    /// modification, so this convenience overload (context-free about which stream kind a
    /// specific response actually returned) skips the check entirely based on the remembered
    /// protocol. <see cref="CheckForUpdatesAsync(AhtolaReplicaOptions,ManagedReplicaMetadata,AhtolaSyncOptions,IReadOnlyList{ReplicaLocalChange},CancellationToken)"/>
    /// does NOT rely on this overload's protocol-based skip for its own gating, precisely because
    /// a protocol-2 (MvccLogical) remote can still answer a given pull with a Pages stream (see
    /// <see cref="EnsurePagesApplyIsSafe"/>).
    /// </remarks>
    public static void EnsureNoLocalDivergence(string databasePath, ManagedReplicaMetadata metadata)
    {
        if (metadata.Protocol == RemotePullProtocol.MvccLogical)
            return;

        CheckFileDivergence(databasePath, metadata);
    }

    /// <summary>
    /// Guards a PAGES-stream apply (Incremental or ReplaceBase). This stream kind may be returned
    /// even when the remembered protocol is MvccLogical: a protocol-2 remote can still answer any
    /// given pull with raw pages (e.g. after its logical log has been garbage-collected, or for a
    /// ReplaceBase). A raw page-based apply has no mechanism to reconcile local writes the way the
    /// logical path's precollect/reapply does, so both modes reject outright when local changes are
    /// still pending push. Incremental still rejects those entries because a partial page patch
    /// cannot rebase journaled SQL. ReplaceBase installs a complete snapshot, so pending statements
    /// are replayed onto the new image before metadata publication. After that their remaining
    /// safety requirements differ: ReplaceBase may checkpoint and discard fully-pushed local WAL
    /// state before replacement; Incremental patches only selected pages, so any non-empty WAL
    /// already proves that its main-file base is stale and must be rejected without
    /// checkpointing/mutating that base.
    /// </summary>
    private static ManagedReplicaMetadata EnsurePagesApplyIsSafe(
        string databasePath,
        ManagedReplicaMetadata metadata,
        IReadOnlyList<ReplicaLocalChange> pendingLocalChanges,
        PullApplyMode applyMode,
        CancellationToken cancellationToken)
    {
        if (pendingLocalChanges.Count > 0 && applyMode == PullApplyMode.Incremental)
        {
            throw new NotSupportedException(
                "Managed embedded replica has local changes pending push; an incremental page-based update "
                + "has no way to reconcile them and was rejected. Push the pending local changes, then retry.");
        }

        if (applyMode == PullApplyMode.Incremental)
        {
            CheckFileDivergence(databasePath, metadata);
            DeleteStagingSidecars(databasePath);
            return metadata;
        }
        if (pendingLocalChanges.Count != 0)
            return metadata;

        return CheckpointAndCleanSidecarsBeforePagesApply(databasePath, metadata, cancellationToken);
    }

    private static ManagedReplicaMetadata CheckpointAndCleanSidecarsBeforePagesApply(
        string databasePath,
        ManagedReplicaMetadata metadata,
        CancellationToken cancellationToken)
    {
        if (!DatabaseSidecarSuffixes.Any(suffix => File.Exists(databasePath + suffix)))
            return metadata;

        using (var pager = Ahtola.Core.Storage.SqlitePager.Open(
                   Ahtola.Core.Storage.PhysicalFileSystem.Instance,
                   databasePath,
                   databasePath + "-wal"))
        {
            var checkpoint = ManagedReplicaRevertWal.CaptureAndCheckpoint(
                databasePath,
                metadata,
                pager,
                cancellationToken);
            if (checkpoint.RetainedCommittedFrameCount != 0)
            {
                throw new NotSupportedException(
                    "Managed embedded replica could not reset all fully-pushed WAL frames before "
                    + "applying a page-based replacement.");
            }
        }
        if (File.Exists(databasePath + ManagedReplicaRevertWal.Suffix))
        {
            ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.RevertCheckpointed);
            cancellationToken.ThrowIfCancellationRequested();
        }

        // A successful durable checkpoint-and-reset leaves no frames beyond the 32-byte WAL
        // header and no rollback journal content. Anything else means the base is still carrying
        // live local state and must not be replaced or patched. Once proven clean, delete every
        // sidecar while publication still holds exclusive ownership so the newly installed
        // database cannot be paired with stale WAL-index state.
        if ((File.Exists(databasePath + "-wal") && new FileInfo(databasePath + "-wal").Length > 32)
            || (File.Exists(databasePath + "-journal") && new FileInfo(databasePath + "-journal").Length > 0))
        {
            throw new NotSupportedException(
                "Managed embedded replica could not checkpoint all fully-pushed local state before "
                + "applying a page-based update; retry after readers and transactions are quiescent.");
        }

        DeleteStagingSidecars(databasePath);
        return LoadMetadata(databasePath)
               ?? throw new InvalidDataException(
                   "Managed embedded replica checkpoint recovery metadata is missing after capture.");
    }

    private static void CheckFileDivergence(string databasePath, ManagedReplicaMetadata metadata)
    {
        // Opening the managed pager may create an empty 32-byte WAL header; frames
        // beyond that header are local state and cannot be replaced safely.
        if ((File.Exists(databasePath + "-wal") && new FileInfo(databasePath + "-wal").Length > 32)
            || (File.Exists(databasePath + "-journal") && new FileInfo(databasePath + "-journal").Length > 0))
            throw new NotSupportedException("Managed embedded replica local divergence was detected; incremental pull cannot replace local changes.");
        if (!string.Equals(ComputeDatabaseFingerprint(databasePath), metadata.DatabaseSha256, StringComparison.Ordinal))
            throw new NotSupportedException("Managed embedded replica local divergence was detected; incremental pull cannot replace local changes.");
    }

    /// <summary>Determines whether a bootstrapped managed embedded replica is present at
    /// <paramref name="databasePath"/> using only local filesystem state (the database file,
    /// metadata sidecar, and optional lazy-page state) — never opens a connection or contacts the remote endpoint. Opening
    /// an embedded-replica connection when the local database is absent triggers a full remote
    /// bootstrap/download as a side effect, which existence checks must never do.</summary>
    internal static ManagedReplicaLocalState GetLocalState(string databasePath)
    {
        var databaseExists = File.Exists(databasePath);
        var metadataExists = File.Exists(databasePath + MetadataSuffix);
        var pageStateExists = File.Exists(
            databasePath + ManagedReplicaPageMaterializingFileSystem.StateSuffix);
        var publication = GetBootstrapPublicationInfo(databasePath);
        if (!publication.IsComplete
            || publication.MarkerExists && !metadataExists
            || publication.RequiresPageState && !pageStateExists)
            return ManagedReplicaLocalState.Inconsistent;
        if (pageStateExists && (!databaseExists || !metadataExists))
            return ManagedReplicaLocalState.Inconsistent;
        if (databaseExists == metadataExists)
            return databaseExists ? ManagedReplicaLocalState.Present : ManagedReplicaLocalState.Absent;

        // BootstrapAsync itself refuses to proceed when exactly one of the pair exists (see its
        // own metadata-without-database check above); existence checks must surface the same
        // inconsistency rather than silently reporting true/false or repairing it implicitly.
        return ManagedReplicaLocalState.Inconsistent;
    }

    /// <summary>Every local filesystem artifact a managed embedded replica may have written
    /// alongside <paramref name="databasePath"/>: the database file itself, SQLite's own
    /// -wal/-shm/-journal siblings, the bootstrap/sync metadata sidecar, and the local change
    /// journal used to buffer writes between pushes. Callers that need to fully remove a
    /// replica's local footprint (e.g. EF's DatabaseCreator.Delete) must delete this whole set,
    /// not just the primary database file, or a later bootstrap will find a stale, inconsistent
    /// partial state.</summary>
    internal static IReadOnlyList<string> GetLocalArtifactPaths(string databasePath) =>
    [
        databasePath,
        databasePath + "-wal",
        databasePath + "-shm",
        databasePath + "-journal",
        .. ManagedReplicaRevertWal.GetArtifactPaths(databasePath),
        databasePath + BaseSnapshotSuffix,
        databasePath + BaseSnapshotPreviousSuffix,
        databasePath + MetadataSuffix,
        databasePath + BootstrapStateSuffix,
        databasePath + ManagedReplicaPageMaterializingFileSystem.StateSuffix,
        databasePath + ManagedReplicaPageMaterializingFileSystem.OwnershipLockSuffix,
        databasePath + ManagedReplicaChangeJournal.Suffix,
        databasePath + ManagedReplicaApplyLock.CarrierSuffix,
    ];

    /// <summary>
    /// Rejects a non-empty logical apply while a pending local schema change cannot be rebased.
    /// Additive <c>ALTER TABLE ... ADD COLUMN</c> is always allowed: extra local columns are
    /// ignored during remote table refresh and reapplied afterward. <c>CREATE</c> remains
    /// allowed because it introduces an object the server cannot yet know.
    /// </summary>
    private static void RejectIfLocalSchemaChangesConflictWithRemoteChanges(
        IReadOnlyList<ReplicaLocalChange> pendingLocalChanges)
    {
        foreach (var change in pendingLocalChanges)
        {
            if (change.Kind != ReplicaLocalChangeKind.Schema)
                continue;
            if (SqlTransactionControl.GetFirstKeyword(change.Sql) is { } keyword
                && keyword.Equals("CREATE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ManagedReplicaSchemaDdlText.TryParseAlterTableAddColumn(change.Sql) is not null)
                continue;

            throw new NotSupportedException(
                "Managed embedded replica has a local schema change pending push; a logical pull with "
                + "remote changes to apply cannot safely proceed while it is unpushed, since a remote table "
                + "refresh or drop is generated against a schema state that does not yet reflect it. Push "
                + "the pending local changes, then retry.");
        }
    }

    private static string ComputeDatabaseFingerprint(string path)
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

    private static string PublishInitialRemoteBaseSnapshot(string databasePath, string sourcePath)
    {
        var fingerprint = ComputeDatabaseFingerprint(sourcePath);
        CopyFileDurably(sourcePath, databasePath + BaseSnapshotSuffix);
        return fingerprint;
    }

    private static string PublishRemoteBaseSnapshot(
        string databasePath,
        ManagedReplicaMetadata metadata,
        string sourcePath)
    {
        NormalizeRemoteBaseSnapshot(databasePath, metadata);
        var destinationPath = databasePath + BaseSnapshotSuffix;
        var previousPath = databasePath + BaseSnapshotPreviousSuffix;
        var stagingPath = string.Concat(destinationPath, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            CopyFileDurably(sourcePath, stagingPath);
            File.Replace(stagingPath, destinationPath, previousPath, ignoreMetadataErrors: false);
            return ComputeDatabaseFingerprint(destinationPath);
        }
        finally
        {
            DeleteIfExists(stagingPath);
        }
    }

    private static string ResolveRemoteBaseSnapshot(
        string databasePath,
        ManagedReplicaMetadata metadata)
    {
        var destinationPath = databasePath + BaseSnapshotSuffix;
        if (MatchesFingerprint(destinationPath, metadata.RemoteBaseSha256))
            return destinationPath;
        var previousPath = databasePath + BaseSnapshotPreviousSuffix;
        if (MatchesFingerprint(previousPath, metadata.RemoteBaseSha256))
            return previousPath;
        throw new InvalidDataException(
            "Managed embedded replica remote-base snapshot is missing or failed its integrity check.");
    }

    private static void NormalizeRemoteBaseSnapshot(
        string databasePath,
        ManagedReplicaMetadata metadata)
    {
        var activePath = ResolveRemoteBaseSnapshot(databasePath, metadata);
        var destinationPath = databasePath + BaseSnapshotSuffix;
        var previousPath = databasePath + BaseSnapshotPreviousSuffix;
        if (string.Equals(activePath, previousPath, StringComparison.Ordinal))
        {
            DeleteIfExists(destinationPath);
            File.Move(previousPath, destinationPath, overwrite: false);
        }
        else
        {
            DeleteIfExists(previousPath);
        }
    }

    private static void CompleteRemoteBaseSnapshotPublication(string databasePath)
        => DeleteIfExists(databasePath + BaseSnapshotPreviousSuffix);

    private static bool MatchesFingerprint(string path, string expected)
        => File.Exists(path)
           && string.Equals(ComputeDatabaseFingerprint(path), expected, StringComparison.Ordinal);

    private static void CopyFileDurably(string sourcePath, string destinationPath)
    {
        using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920,
            FileOptions.SequentialScan);
        using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.WriteThrough);
        source.CopyTo(destination);
        destination.Flush(flushToDisk: true);
    }

    private static long AdvanceJournalBaseWatermark(
        ManagedReplicaMetadata metadata,
        IReadOnlyList<ReplicaLocalChange> acknowledgedLocalChanges)
    {
        if (acknowledgedLocalChanges.Count == 0)
            return metadata.JournalBaseWatermark;
        var first = acknowledgedLocalChanges[0].Sequence;
        var last = acknowledgedLocalChanges[^1].Sequence;
        if (first != metadata.JournalBaseWatermark
            || last < first
            || acknowledgedLocalChanges.Count != last - first + 1)
        {
            throw new InvalidDataException(
                "Managed replica acknowledged journal history is not contiguous with its recorded base.");
        }
        return checked(last + 1);
    }

    private static bool IsSha256Hex(string value)
        => value.Length == 64 && value.All(static c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    /// <summary>
    /// Managed embedded-replica metadata. <see cref="Revision"/> is an opaque, exact UTF-8
    /// resume token echoed back verbatim on the next pull request; it is never parsed or
    /// interpreted. <see cref="Protocol"/> is the detected remote sync capability, and
    /// <see cref="TableNamesByStableId"/> is the persisted portable table-id-to-name map used to
    /// resolve logical row operations that omit an explicit table name. Version 4 metadata also
    /// carries an internal pending checkpoint-revert marker until publication completes.
    /// </summary>
    public readonly record struct ManagedReplicaMetadata(
        string Revision,
        string DatabaseSha256,
        string ClientId,
        RemotePullProtocol Protocol,
        IReadOnlyDictionary<ulong, string> TableNamesByStableId)
    {
        internal ManagedReplicaRevertState? RevertState { get; init; }

        internal long JournalBaseWatermark { get; init; } = 1;

        internal string RemoteBaseSha256 { get; init; } = string.Empty;
    }

    internal readonly record struct BootstrapPublicationInfo(
        bool MarkerExists,
        bool IsComplete,
        bool RequiresPageState);

    private enum BootstrapPublicationStatus
    {
        Empty,
        InProgress,
        Complete,
        CompletePartial,
    }

    private sealed class BootstrapPublication : IDisposable
    {
        private const int RecordLength = 48;
        private static readonly byte[] Magic = "AHTLBP01"u8.ToArray();

        private readonly FileStream _stream;
        private int _disposed;

        private BootstrapPublication(
            FileStream stream,
            BootstrapPublicationStatus status,
            bool requiresRecovery)
        {
            _stream = stream;
            Status = status;
            RequiresRecovery = requiresRecovery;
        }

        internal BootstrapPublicationStatus Status { get; private set; }

        internal bool RequiresRecovery { get; }

        internal static BootstrapPublication Acquire(string databasePath)
        {
            var path = databasePath + BootstrapStateSuffix;
            FileStream? stream = null;
            try
            {
                stream = new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
                var status = ReadStatus(stream);
                var completeLength = stream.Length / RecordLength * RecordLength;
                if (stream.Length != completeLength)
                {
                    stream.SetLength(completeLength);
                    stream.Flush(flushToDisk: true);
                }
                var databaseExists = File.Exists(databasePath);
                var metadataExists = File.Exists(databasePath + MetadataSuffix);
                var pageStateExists = File.Exists(
                    databasePath + ManagedReplicaPageMaterializingFileSystem.StateSuffix);
                var requiresRecovery = status == BootstrapPublicationStatus.InProgress
                    || status == BootstrapPublicationStatus.Complete
                    && (!databaseExists || !metadataExists)
                    || status == BootstrapPublicationStatus.CompletePartial
                    && (!databaseExists || !metadataExists || !pageStateExists)
                    || status == BootstrapPublicationStatus.Empty
                    && (databaseExists || metadataExists || pageStateExists);
                var publication = new BootstrapPublication(stream, status, requiresRecovery);
                stream = null;
                return publication;
            }
            catch (IOException exception)
            {
                throw new IOException(
                    "Managed embedded replica bootstrap is already being published by another process.",
                    exception);
            }
            finally
            {
                stream?.Dispose();
            }
        }

        internal void MarkInProgress()
        {
            ThrowIfDisposed();
            _stream.SetLength(0);
            WriteStatus(BootstrapPublicationStatus.InProgress);
        }

        internal void MarkComplete(bool isPartial)
        {
            ThrowIfDisposed();
            WriteStatus(
                isPartial
                    ? BootstrapPublicationStatus.CompletePartial
                    : BootstrapPublicationStatus.Complete);
        }

        internal static BootstrapPublicationStatus ReadStatus(Stream stream)
        {
            if (stream.Length == 0)
                return BootstrapPublicationStatus.Empty;

            var completeRecords = stream.Length / RecordLength;
            if (completeRecords == 0)
                return BootstrapPublicationStatus.InProgress;

            var record = new byte[RecordLength];
            Span<byte> expected = stackalloc byte[32];
            var status = BootstrapPublicationStatus.Empty;
            stream.Position = 0;
            for (long index = 0; index < completeRecords; index++)
            {
                stream.ReadExactly(record);
                if (!record.AsSpan(0, Magic.Length).SequenceEqual(Magic))
                    throw new InvalidDataException("Managed replica bootstrap state has an invalid record.");
                var parsed = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(8));
                if (parsed is not ((int)BootstrapPublicationStatus.InProgress)
                    and not ((int)BootstrapPublicationStatus.Complete)
                    and not ((int)BootstrapPublicationStatus.CompletePartial))
                {
                    throw new InvalidDataException("Managed replica bootstrap state has an invalid status.");
                }

                SHA256.HashData(record.AsSpan(0, 16), expected);
                if (!CryptographicOperations.FixedTimeEquals(expected, record.AsSpan(16, 32)))
                {
                    throw new InvalidDataException(
                        "Managed replica bootstrap state failed its integrity check.");
                }

                status = (BootstrapPublicationStatus)parsed;
            }

            return status;
        }

        private void WriteStatus(BootstrapPublicationStatus status)
        {
            var record = new byte[RecordLength];
            Magic.CopyTo(record, 0);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(8), (int)status);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(12), RecordLength);
            SHA256.HashData(record.AsSpan(0, 16), record.AsSpan(16, 32));
            _stream.Position = _stream.Length;
            _stream.Write(record);
            _stream.Flush(flushToDisk: true);
            Status = status;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _stream.Dispose();
        }

        private void ThrowIfDisposed()
            => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    internal enum ManagedReplicaRevertPhase : byte
    {
        None = 0,
        Captured = 1,
        RestoreCommitted = 2,
        CommittedReady = 3,
        PushOutcomeUnknown = 4,
        RestoreOriginal = 5,
    }

    internal readonly record struct ManagedReplicaRevertState(
        ManagedReplicaRevertPhase Phase,
        long SourceWalWatermark,
        long AttemptedFirstSequence,
        long AttemptedWatermark,
        uint SourceDatabaseSizeInPages,
        uint SourceWalCheckpointSequence,
        uint SourceWalSalt1,
        uint SourceWalSalt2,
        uint SourceWalChecksum1,
        uint SourceWalChecksum2,
        uint OriginalDatabaseSizeInPages,
        uint OriginalRevertWalFrameCount,
        uint CommittedDatabaseSizeInPages,
        uint CommittedRevertWalFrameCount,
        string OriginalDatabaseSha256,
        string CommittedDatabaseSha256,
        string RevertWalSha256);
    private enum PullStreamKind
    {
        Pages = 0,
        MvccLogicalLog = 1,
    }

    private enum PullApplyMode
    {
        Incremental = 0,
        ReplaceBase = 1,
    }

    private readonly record struct ManagedReplicaLogicalLogMetadata(
        string Format,
        bool CheckpointTransition,
        IReadOnlyList<ManagedReplicaLogicalLogRange> Ranges);

    private readonly record struct InitialDownload(
        string Revision,
        RemotePullProtocol Protocol,
        ulong DatabasePages,
        IReadOnlyList<ulong> MaterializedPageIds,
        bool IsPartial);

    private readonly record struct PullHeader(
        string Revision,
        ulong DatabasePages,
        PullStreamKind StreamKind,
        PullApplyMode ApplyMode,
        RemotePullProtocol Protocol,
        ManagedReplicaLogicalLogMetadata? LogicalMetadata);
    private readonly record struct PullPage(ulong PageId, byte[] Data);
    private readonly record struct RoaringContainer(
        ushort Key,
        int Cardinality,
        ushort[]? Values);

    private sealed class DelimitedProtobufReader(Stream stream)
    {
        private readonly byte[] _singleByte = new byte[1];
        public long BytesRead { get; private set; }

        public async Task<byte[]?> ReadAsync(int maximumLength, CancellationToken cancellationToken)
        {
            var next = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (next < 0)
                return null;

            ulong length = 0;
            for (var byteIndex = 0; byteIndex < 10; byteIndex++)
            {
                if (byteIndex == 9 && (next & 0xfe) != 0)
                    throw new InvalidDataException("The protobuf message length is invalid.");
                length |= (ulong)(next & 0x7f) << (byteIndex * 7);
                if ((next & 0x80) == 0)
                    break;
                if (byteIndex == 9)
                    throw new InvalidDataException("The protobuf message length is invalid.");
                next = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                if (next < 0)
                    throw new InvalidDataException("The protobuf message ended inside its length prefix.");
            }

            if (length > (ulong)maximumLength)
                throw new InvalidDataException("The protobuf message exceeds the supported size.");

            var payload = new byte[(int)length];
            var offset = 0;
            while (offset < payload.Length)
            {
                var read = await stream.ReadAsync(payload.AsMemory(offset), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    throw new InvalidDataException("The protobuf message ended before its declared length.");
                offset += read;
                BytesRead += read;
            }
            return payload;
        }

        private async Task<int> ReadByteAsync(CancellationToken cancellationToken)
        {
            var read = await stream.ReadAsync(_singleByte.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read != 0)
                BytesRead++;
            return read == 0 ? -1 : _singleByte[0];
        }
    }

    private ref struct ProtobufFieldReader(ReadOnlySpan<byte> payload)
    {
        private ReadOnlySpan<byte> _payload = payload;
        private int _offset;

        public bool TryReadField(out int fieldNumber, out int wireType)
        {
            if (_offset == _payload.Length)
            {
                fieldNumber = 0;
                wireType = 0;
                return false;
            }

            var key = ReadRawVarint();
            fieldNumber = checked((int)(key >> 3));
            wireType = checked((int)(key & 7));
            if (fieldNumber == 0)
                throw new InvalidDataException("The protobuf message contains an invalid field number.");
            return true;
        }

        public ulong ReadVarint(int wireType, string name)
        {
            if (wireType != 0)
                throw new InvalidDataException($"The protobuf {name} field has an invalid wire type.");
            return ReadRawVarint();
        }

        public byte[] ReadLengthDelimited(int wireType, string name)
        {
            if (wireType != 2)
                throw new InvalidDataException($"The protobuf {name} field has an invalid wire type.");
            var length = ReadRawVarint();
            if (length > (ulong)(_payload.Length - _offset))
                throw new InvalidDataException($"The protobuf {name} field exceeds its message.");
            var value = _payload.Slice(_offset, (int)length).ToArray();
            _offset += value.Length;
            return value;
        }

        public void SkipField(int wireType)
        {
            switch (wireType)
            {
                case 0:
                    _ = ReadRawVarint();
                    break;
                case 1:
                    Skip(8);
                    break;
                case 2:
                    var length = ReadRawVarint();
                    if (length > int.MaxValue)
                        throw new InvalidDataException("The protobuf field is too large.");
                    Skip((int)length);
                    break;
                case 5:
                    Skip(4);
                    break;
                default:
                    throw new InvalidDataException("The protobuf message contains an unsupported wire type.");
            }
        }

        private ulong ReadRawVarint()
        {
            ulong value = 0;
            for (var byteIndex = 0; byteIndex < 10; byteIndex++)
            {
                if (_offset == _payload.Length)
                    throw new InvalidDataException("The protobuf message ended inside a varint.");
                var next = _payload[_offset++];
                if (byteIndex == 9 && (next & 0xfe) != 0)
                    throw new InvalidDataException("The protobuf message contains an invalid varint.");
                value |= (ulong)(next & 0x7f) << (byteIndex * 7);
                if ((next & 0x80) == 0)
                    return value;
            }
            throw new InvalidDataException("The protobuf message contains an invalid varint.");
        }

        private void Skip(int length)
        {
            if (length < 0 || length > _payload.Length - _offset)
                throw new InvalidDataException("The protobuf field exceeds its message.");
            _offset += length;
        }
    }
}

/// <summary>The local on-disk state of a managed embedded replica, as determined purely from
/// filesystem checks (see <see cref="ManagedReplicaBootstrapper.GetLocalState"/>) without opening
/// a connection or contacting the remote endpoint.</summary>
internal enum ManagedReplicaLocalState
{
    /// <summary>Neither the database file nor its metadata sidecar exist locally: no replica has
    /// been bootstrapped at this path yet.</summary>
    Absent,

    /// <summary>Both the database file and its metadata sidecar exist locally: a bootstrapped
    /// replica is present.</summary>
    Present,

    /// <summary>Exactly one of the database file or metadata sidecar exists locally. This is the
    /// same inconsistency <see cref="ManagedReplicaBootstrapper.BootstrapAsync"/> itself refuses
    /// to resolve automatically; callers should surface it as an error rather than silently
    /// treating it as present, absent, or repairing it implicitly.</summary>
    Inconsistent,
}
