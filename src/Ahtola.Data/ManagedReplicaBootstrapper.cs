using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Ahtola.Core;

namespace Ahtola;

internal static class ManagedReplicaBootstrapper
{
    private const int PageSize = 4096;
    private const int MaxHeaderLength = 64 * 1024;
    private const int MaxPageMessageLength = PageSize + 1024;
    private const string MetadataSuffix = ".ahtola-replica-meta";
    private static readonly byte[] SqliteHeader = "SQLite format 3\0"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task BootstrapAsync(AhtolaReplicaOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        ManagedReplicaSupportMatrix.ValidateOptions(options);

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
        if (File.Exists(metadataPath))
        {
            throw new InvalidOperationException(
                "Managed embedded replica metadata exists while the replica database is missing.");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(options.Path))!;
        var stagingPath = Path.Combine(
            directory,
            $".{Path.GetFileName(options.Path)}.bootstrap-{Guid.NewGuid():N}.tmp");
        var metadataStagingPath = Path.Combine(
            directory,
            $".{Path.GetFileName(metadataPath)}.bootstrap-{Guid.NewGuid():N}.tmp");

        var databaseInstalled = false;
        try
        {
            var revision = await DownloadDatabaseAsync(options, stagingPath, cancellationToken).ConfigureAwait(false);
            ValidateStagedDatabase(stagingPath);

            ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.BootstrapStagedDatabase);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagingPath, options.Path, overwrite: false);
            databaseInstalled = true;

            ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.BootstrapDatabasePublished);
            cancellationToken.ThrowIfCancellationRequested();
            await WriteMetadataAsync(
                metadataStagingPath,
                metadataPath,
                revision,
                ComputeDatabaseFingerprint(options.Path),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A bootstrap image is not usable as a replica until its matching revision
            // metadata is durable. Roll it back so the next open can bootstrap cleanly.
            if (databaseInstalled && !File.Exists(metadataPath))
                DeleteIfExists(options.Path);
            throw;
        }
        finally
        {
            DeleteIfExists(stagingPath);
            DeleteStagingSidecars(stagingPath);
            DeleteIfExists(metadataStagingPath);
        }
    }

    public static ManagedReplicaMetadata? LoadMetadata(string databasePath)
    {
        var path = databasePath + MetadataSuffix;
        if (!File.Exists(path))
            return null;
        if (new FileInfo(path).Length is <= 0 or > 8192)
            throw new InvalidDataException("Managed embedded replica metadata has an invalid size.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in StrictUtf8.GetString(File.ReadAllBytes(path)).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0 || !values.TryAdd(line[..separator], line[(separator + 1)..]))
                throw new InvalidDataException("Managed embedded replica metadata is malformed.");
        }

        if (!values.TryGetValue("version", out var version) || version != "2"
            || !values.TryGetValue("server_revision_base64", out var encodedRevision)
            || !values.TryGetValue("database_sha256", out var fingerprint)
            || !values.TryGetValue("client_id", out var clientId) || values.Count != 4)
            throw new InvalidDataException("Managed embedded replica metadata is incomplete.");
        try
        {
            var revision = StrictUtf8.GetString(Convert.FromBase64String(encodedRevision));
            if (revision.Length == 0 || !Guid.TryParseExact(clientId, "N", out _))
                throw new InvalidDataException("Managed embedded replica metadata is invalid.");
            if (!IsSha256Hex(fingerprint))
                throw new InvalidDataException("Managed embedded replica metadata is invalid.");
            return new ManagedReplicaMetadata(revision, fingerprint, clientId);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Managed embedded replica metadata is invalid.", exception);
        }
    }

    public static async Task<AhtolaSyncResult> CheckForUpdatesAsync(
        AhtolaReplicaOptions options, ManagedReplicaMetadata metadata, AhtolaSyncOptions syncOptions,
        CancellationToken cancellationToken)
    {
        ManagedReplicaSupportMatrix.ValidateOptions(options);
        EnsureNoLocalDivergence(options.Path, metadata);
        syncOptions.Progress?.Report(new AhtolaSyncProgress(AhtolaSyncProgressStage.Pulling));
        var payload = CreatePullRequest(metadata.Revision, options.LongPollTimeout);
        using var timeout = CreateTimeout(options.HttpPolicy.RequestTimeout, cancellationToken);
        using var scope = options.EnterApplicationHttpScope();
        using var client = options.HttpPolicy.MessageHandler is { } handler ? new HttpClient(handler, false) : new HttpClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var request = new HttpRequestMessage(HttpMethod.Post, CreatePullUpdatesUri(options.RemoteUri))
        {
            Content = new ByteArrayContent(payload),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/protobuf");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "application/protobuf");
        var token = string.IsNullOrWhiteSpace(options.AuthToken) ? null : options.AuthToken;
        ValidateAuthTokenTransport(request.RequestUri!, token);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var effectiveToken = timeout?.Token ?? cancellationToken;
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, effectiveToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(effectiveToken).ConfigureAwait(false);
        var reader = new DelimitedProtobufReader(stream);
        var message = await reader.ReadAsync(MaxHeaderLength, effectiveToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The pull-updates response did not contain a protobuf header.");
        var header = ParseHeader(message);
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

        await ApplyIncrementalPagesAsync(options, header, pages, metadata.ClientId, effectiveToken).ConfigureAwait(false);
        syncOptions.Progress?.Report(new AhtolaSyncProgress(AhtolaSyncProgressStage.Applying));
        syncOptions.Progress?.Report(new AhtolaSyncProgress(AhtolaSyncProgressStage.Completed));
        return new AhtolaSyncResult(AhtolaSyncOutcome.RemoteChangesApplied,
            new AhtolaSyncStatistics(0, 0, 0, DateTimeOffset.UtcNow, null, payload.Length, reader.BytesRead, header.Revision));
    }

    /// <summary>
    /// Records the local image that was just acknowledged by a committed remote push. This
    /// preserves the client identity and lets the subsequent pull retain its divergence guard.
    /// </summary>
    public static async Task<ManagedReplicaMetadata> RecordLocalPushAsync(
        AhtolaReplicaOptions options,
        ManagedReplicaMetadata metadata,
        CancellationToken cancellationToken)
    {
        var metadataPath = options.Path + MetadataSuffix;
        var directory = Path.GetDirectoryName(Path.GetFullPath(metadataPath))!;
        var stagingPath = Path.Combine(
            directory,
            $".{Path.GetFileName(metadataPath)}.push-{Guid.NewGuid():N}.tmp");
        try
        {
            var fingerprint = ComputeDatabaseFingerprint(options.Path);
            await WriteMetadataAsync(
                    stagingPath,
                    metadataPath,
                    metadata.Revision,
                    fingerprint,
                    cancellationToken,
                    replaceExisting: true,
                    clientId: metadata.ClientId)
                .ConfigureAwait(false);
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
        string clientId,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(options.Path))!;
        var stagingPath = Path.Combine(directory, $".{Path.GetFileName(options.Path)}.apply-{Guid.NewGuid():N}.tmp");
        var backupPath = Path.Combine(directory, $".{Path.GetFileName(options.Path)}.apply-{Guid.NewGuid():N}.bak");
        var metadataPath = options.Path + MetadataSuffix;
        var metadataStagingPath = Path.Combine(
            directory,
            $".{Path.GetFileName(metadataPath)}.apply-{Guid.NewGuid():N}.tmp");
        var databaseInstalled = false;
        var metadataInstalled = false;
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

            ValidateStagedDatabase(stagingPath);
            ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.IncrementalApplyStagedDatabase);
            cancellationToken.ThrowIfCancellationRequested();
            File.Replace(stagingPath, options.Path, backupPath, ignoreMetadataErrors: false);
            databaseInstalled = true;
            ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.IncrementalApplyDatabasePublished);
            cancellationToken.ThrowIfCancellationRequested();
            await WriteMetadataAsync(
                    metadataStagingPath,
                    metadataPath,
                    header.Revision,
                    ComputeDatabaseFingerprint(options.Path),
                    cancellationToken,
                    replaceExisting: true,
                    clientId: clientId)
                .ConfigureAwait(false);
            metadataInstalled = true;
            ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.IncrementalApplyMetadataPublished);
            cancellationToken.ThrowIfCancellationRequested();
            DeleteIfExists(backupPath);
        }
        catch
        {
            // Once metadata is durable it fingerprints the installed image. A later
            // interruption must preserve that matched pair rather than restore only DB.
            if (databaseInstalled && !metadataInstalled && File.Exists(backupPath))
                File.Replace(backupPath, options.Path, destinationBackupFileName: null, ignoreMetadataErrors: false);
            throw;
        }
        finally
        {
            DeleteIfExists(stagingPath);
            DeleteStagingSidecars(stagingPath);
            DeleteIfExists(metadataStagingPath);
            DeleteIfExists(backupPath);
        }
    }

    private static async Task<string> DownloadDatabaseAsync(
        AhtolaReplicaOptions options,
        string stagingPath,
        CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeout(options.HttpPolicy.RequestTimeout, cancellationToken);
        var effectiveCancellationToken = timeout?.Token ?? cancellationToken;
        using var scope = options.EnterApplicationHttpScope();
        using var client = options.HttpPolicy.MessageHandler is { } handler
            ? new HttpClient(handler, disposeHandler: false)
            : new HttpClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var request = new HttpRequestMessage(HttpMethod.Post, CreatePullUpdatesUri(options.RemoteUri));
        request.Content = new ByteArrayContent(CreateInitialPullRequest(options.LongPollTimeout));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/protobuf");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "application/protobuf");

        var authToken = string.IsNullOrWhiteSpace(options.AuthToken) ? null : options.AuthToken;
        ValidateAuthTokenTransport(request.RequestUri!, authToken);
        if (authToken is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            effectiveCancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(effectiveCancellationToken).ConfigureAwait(false);
        var reader = new DelimitedProtobufReader(stream);
        var headerPayload = await reader.ReadAsync(MaxHeaderLength, effectiveCancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The pull-updates response did not contain a protobuf header.");
        var header = ParseHeader(headerPayload);

        var databaseLength = checked((long)header.DatabasePages * PageSize);
        var receivedPages = new HashSet<ulong>();
        await using (var staging = new FileStream(
            stagingPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: PageSize,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            staging.SetLength(databaseLength);
            while (await reader.ReadAsync(MaxPageMessageLength, effectiveCancellationToken).ConfigureAwait(false) is { } pagePayload)
            {
                var page = ParsePage(pagePayload);
                if (page.PageId >= header.DatabasePages)
                    throw new InvalidDataException("The pull-updates response contains a page outside the declared database size.");
                if (!receivedPages.Add(page.PageId))
                    throw new InvalidDataException("The pull-updates response contains a duplicate page.");

                staging.Position = checked((long)page.PageId * PageSize);
                await staging.WriteAsync(page.Data, effectiveCancellationToken).ConfigureAwait(false);
            }

            if ((ulong)receivedPages.Count != header.DatabasePages)
                throw new InvalidDataException("The pull-updates response did not contain every database page exactly once.");

            await staging.FlushAsync(effectiveCancellationToken).ConfigureAwait(false);
            staging.Flush(flushToDisk: true);
        }

        return header.Revision;
    }

    private static void ValidateStagedDatabase(string stagingPath)
    {
        using (var stream = new FileStream(stagingPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Span<byte> header = stackalloc byte[SqliteHeader.Length];
            stream.ReadExactly(header);
            if (!header.SequenceEqual(SqliteHeader))
                throw new InvalidDataException("The bootstrapped page stream does not contain a SQLite database header.");
        }

        using var database = ManagedDatabaseAdapter.Open(stagingPath);
        _ = database.Connect();
    }

    private static async Task WriteMetadataAsync(
        string stagingPath,
        string metadataPath,
        string revision,
        string fingerprint,
        CancellationToken cancellationToken,
        bool replaceExisting = false,
        string? clientId = null)
    {
        var metadata = string.Concat(
            "version=2\n",
            "server_revision_base64=", Convert.ToBase64String(StrictUtf8.GetBytes(revision)), "\n",
            "database_sha256=", fingerprint, "\n",
            "client_id=", clientId ?? Guid.NewGuid().ToString("N"), "\n");
        await using (var stream = new FileStream(
            stagingPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(metadata), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        if (replaceExisting && File.Exists(metadataPath))
            File.Replace(stagingPath, metadataPath, destinationBackupFileName: null, ignoreMetadataErrors: false);
        else
            File.Move(stagingPath, metadataPath, overwrite: false);
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
                    throw new InvalidDataException("Managed embedded replica bootstrap does not support logical update streams.");
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
        if (streamKind is > 0)
            throw new InvalidDataException("Managed embedded replica bootstrap supports only page streams.");
        if (applyMode is > 1)
            throw new InvalidDataException("The pull-updates response has an unsupported apply mode.");
        if (protocol is > 1)
            throw new InvalidDataException("Managed embedded replica bootstrap does not support logical pull protocols.");

        return new PullHeader(revision, pageCount);
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

    private static byte[] CreateInitialPullRequest(TimeSpan? longPollTimeout)
        => CreatePullRequest(clientRevision: null, longPollTimeout);

    private static byte[] CreatePullRequest(string? clientRevision, TimeSpan? longPollTimeout)
    {
        // Raw, Pages, and empty revisions are Prost defaults. A configured
        // timeout alone is non-default and uses PullUpdatesReqProtoBody tag 4.
        if (longPollTimeout is null)
            return [];

        var request = new List<byte>(clientRevision is null ? 6 : clientRevision.Length + 12);
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
        return request.ToArray();
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

    private static void ValidateAuthTokenTransport(Uri endpoint, string? authToken)
    {
        if (authToken is null
            || endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || endpoint.IsLoopback)
        {
            return;
        }

        throw new InvalidOperationException("Auth Token requires an HTTPS remote Ahtola URL unless the host is localhost or loopback.");
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
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

    public static void EnsureNoLocalDivergence(string databasePath, ManagedReplicaMetadata metadata)
    {
        // Opening the managed pager may create an empty 32-byte WAL header; frames
        // beyond that header are local state and cannot be replaced safely.
        if ((File.Exists(databasePath + "-wal") && new FileInfo(databasePath + "-wal").Length > 32)
            || (File.Exists(databasePath + "-journal") && new FileInfo(databasePath + "-journal").Length > 0))
            throw new NotSupportedException("Managed embedded replica local divergence was detected; incremental pull cannot replace local changes.");
        if (!string.Equals(ComputeDatabaseFingerprint(databasePath), metadata.DatabaseSha256, StringComparison.Ordinal))
            throw new NotSupportedException("Managed embedded replica local divergence was detected; incremental pull cannot replace local changes.");
    }

    private static string ComputeDatabaseFingerprint(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static bool IsSha256Hex(string value)
        => value.Length == 64 && value.All(static c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    public readonly record struct ManagedReplicaMetadata(string Revision, string DatabaseSha256, string ClientId);
    private readonly record struct PullHeader(string Revision, ulong DatabasePages);
    private readonly record struct PullPage(ulong PageId, byte[] Data);

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
