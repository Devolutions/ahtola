using System.Globalization;

namespace Ahtola;

/// <summary>
/// Hrana WebSocket transport routing for <see cref="AhtolaRemoteClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>ws://</c> and <c>wss://</c> data sources use the persistent WebSocket transport;
/// <c>http</c>, <c>https</c>, <c>libsql</c> and <c>turso</c> keep using the stateless HTTP
/// pipeline exactly as before. The choice is made once when the client is constructed and
/// never changes at runtime: a WebSocket failure never silently downgrades to HTTP,
/// because that would change the delivery and ordering guarantees the caller opted into.
/// </para>
/// <para>
/// This transport targets the legacy libSQL/sqld Hrana WebSocket server. The pinned Turso
/// engine has no native Hrana WebSocket server and maps <c>ws</c>/<c>wss</c> onto its HTTP
/// pipeline endpoint instead.
/// </para>
/// </remarks>
internal sealed partial class AhtolaRemoteClient
{
    private readonly AhtolaHranaWebSocketTransport? _webSocketTransport;

    /// <summary>
    /// Creates a client bound to a persistent Hrana WebSocket connection.
    /// </summary>
    /// <remarks>
    /// Remote encryption is refused: the official <c>hello</c> message carries only a JWT,
    /// so there is no place to convey the <c>x-turso-encryption-key</c> value that the HTTP
    /// pipeline sends as a header. Failing closed is the only safe behaviour.
    /// </remarks>
    internal AhtolaRemoteClient(
        Uri webSocketEndpoint,
        string? authToken,
        AhtolaHranaWebSocketOptions webSocketOptions,
        AhtolaRemoteEncryptionOptions? remoteEncryption = null,
        IAhtolaWebSocketConnector? connector = null)
    {
        ArgumentNullException.ThrowIfNull(webSocketEndpoint);
        ArgumentNullException.ThrowIfNull(webSocketOptions);

        if (!IsWebSocketScheme(webSocketEndpoint.Scheme))
            throw new InvalidOperationException($"The Hrana WebSocket transport requires a ws or wss URL: {webSocketEndpoint}");
        if (remoteEncryption is not null)
        {
            throw new InvalidOperationException(
                "Remote encryption is not supported over ws/wss: the Hrana WebSocket hello message has no "
                + "encryption-key field, so the key cannot be conveyed. Use an https remote URL, which sends "
                + $"the {EncryptionKeyHeaderName} header on every pipeline request.");
        }

        AhtolaRemoteTransportSecurity.Validate(webSocketEndpoint, authToken, remoteEncryptionConfigured: false);

        _httpClient = null;
        _disposeHttpClient = false;
        _authToken = string.IsNullOrWhiteSpace(authToken) ? null : authToken;
        _remoteEncryptionKey = null;
        _pipelineUri = webSocketEndpoint;
        _cursorUri = webSocketEndpoint;
        _protocolVersion = RemoteProtocolVersion.V3;
        _allowV2Fallback = false;
        _webSocketTransport = new AhtolaHranaWebSocketTransport(
            webSocketEndpoint,
            _authToken,
            webSocketOptions,
            connector);
    }

    /// <summary>True when this client speaks Hrana over a persistent WebSocket.</summary>
    internal bool UsesWebSocketTransport => _webSocketTransport is not null;

    /// <summary>Negotiated Hrana version of the live WebSocket generation, when connected.</summary>
    internal int? NegotiatedWebSocketVersion => _webSocketTransport?.NegotiatedVersion;

    /// <summary>Physical WebSocket generation counter; increments on every connection attempt.</summary>
    internal long WebSocketGeneration => _webSocketTransport?.Generation ?? 0;

    /// <summary>
    /// Test seam: completes when the transport's most recent fire-and-forget
    /// <see cref="ResetSession"/> close has finished (see
    /// <see cref="AhtolaHranaWebSocketTransport.ResetSessionCompletion"/>).
    /// </summary>
    internal Task WebSocketResetCompletion => _webSocketTransport?.ResetSessionCompletion ?? Task.CompletedTask;

    internal static bool IsWebSocketScheme(string scheme)
        => scheme.Equals("ws", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("wss", StringComparison.OrdinalIgnoreCase);

    /// <summary>Maps a Hrana <c>Error</c> onto the shared remote SQL exception type.</summary>
    internal static AhtolaException CreateHranaError(RemoteError? error) => CreateRemoteError(error);

    private async Task<RemoteStatementResult> ExecuteOverWebSocketAsync(
        AhtolaHranaWebSocketTransport transport,
        RemoteStatement statement,
        int commandTimeout,
        bool closeAfter,
        CancellationToken cancellationToken)
    {
        EnterRequest();
        try
        {
            return await transport
                .ExecuteAsync(statement, commandTimeout, closeAfter, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ExitRequest();
        }
    }

    private async Task<RemoteBatchResult> ExecuteBatchOverWebSocketAsync(
        AhtolaHranaWebSocketTransport transport,
        RemoteBatch batch,
        int commandTimeout,
        bool closeAfter,
        CancellationToken cancellationToken)
    {
        EnterRequest();
        try
        {
            return await transport
                .ExecuteBatchAsync(batch, commandTimeout, closeAfter, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ExitRequest();
        }
    }

    private async Task<RemoteReaderExecution> ExecuteCursorOverWebSocketAsync(
        AhtolaHranaWebSocketTransport transport,
        string sql,
        AhtolaParameterCollection parameters,
        int commandTimeout,
        bool closeAfter,
        CancellationToken cancellationToken,
        Action<Exception>? failureCallback)
    {
        var batch = new RemoteBatch
        {
            Steps =
            [
                new RemoteBatchStep
                {
                    Statement = BuildStatement(sql, parameters, wantRows: true),
                },
            ],
            ReplicationIndex = _replicationIndex?.ToString(CultureInfo.InvariantCulture),
        };

        EnterRequest();
        var cursorOwnsRequest = false;
        try
        {
            var session = await transport
                .OpenCursorAsync(batch, commandTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (session is null)
            {
                // Hrana 1/2 have no cursors: fall back to a buffered execute on the same stream.
                return RemoteReaderExecution.FromBuffered(
                    await transport
                        .ExecuteAsync(
                            BuildStatement(sql, parameters, wantRows: true),
                            commandTimeout,
                            closeAfter,
                            cancellationToken)
                        .ConfigureAwait(false));
            }

            var cursor = new RemoteWebSocketCursor(
                this,
                session,
                commandTimeout,
                closeAfter,
                failureCallback);
            cursorOwnsRequest = true;
            try
            {
                await cursor.InitializeAsync(cancellationToken).ConfigureAwait(false);
                return RemoteReaderExecution.FromCursor(cursor);
            }
            catch
            {
                await cursor.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            if (!cursorOwnsRequest)
                ExitRequest();
        }
    }

    private async Task CloseOverWebSocketAsync(
        AhtolaHranaWebSocketTransport transport,
        int commandTimeout,
        CancellationToken cancellationToken)
    {
        if (!transport.HasOpenSession)
            return;

        EnterRequest();
        try
        {
            await transport.CloseSessionAsync(commandTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ExitRequest();
        }
    }

    private async ValueTask FinishWebSocketCursorAsync(
        AhtolaHranaCursorSession session,
        bool successful,
        bool closeAfter,
        int commandTimeout)
    {
        try
        {
            var closeTimeout = successful
                ? commandTimeout
                : commandTimeout <= 0
                    ? 1
                    : Math.Min(commandTimeout, 1);
            await session.CloseAsync(closeTimeout).ConfigureAwait(false);
            if (closeAfter || !successful)
                await session.CloseStreamAsync(closeTimeout).ConfigureAwait(false);
        }
        catch
        {
            ResetSession();
        }
        finally
        {
            ExitRequest();
        }
    }

    /// <summary>
    /// Hrana 3 cursor over the WebSocket transport. Entries arrive in <c>fetch_cursor</c>
    /// pages rather than as an NDJSON stream, but the ordering contract is identical:
    /// <c>step_begin</c>, then rows, then <c>step_end</c> or <c>step_error</c>, with a
    /// terminal <c>error</c> possible at any time.
    /// </summary>
    internal sealed class RemoteWebSocketCursor : RemoteCursor
    {
        private readonly AhtolaRemoteClient _owner;
        private readonly AhtolaHranaCursorSession _session;
        private readonly int _commandTimeout;
        private readonly bool _closeAfter;
        private readonly Action<Exception>? _failureCallback;
        private readonly Queue<RemoteCursorEntry> _entries = new();
        private bool _stepOpen;
        private bool _done;
        private bool _ownerFinished;
        private bool _failureReported;

        public RemoteWebSocketCursor(
            AhtolaRemoteClient owner,
            AhtolaHranaCursorSession session,
            int commandTimeout,
            bool closeAfter,
            Action<Exception>? failureCallback)
        {
            _owner = owner;
            _session = session;
            _commandTimeout = commandTimeout;
            _closeAfter = closeAfter;
            _failureCallback = failureCallback;
        }

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    var entry = await NextEntryAsync(cancellationToken).ConfigureAwait(false);
                    if (entry is null)
                        throw Malformed("cursor completed before step_begin");

                    switch (entry.Type)
                    {
                        case "step_begin":
                            if (entry.Step != 0)
                            {
                                throw Malformed(
                                    $"expected step 0 but received step {entry.Step?.ToString(CultureInfo.InvariantCulture) ?? "null"}");
                            }
                            _stepOpen = true;
                            Columns = entry.Columns ?? throw Malformed("step_begin did not include cols");
                            return;

                        case "step_error":
                            var stepError = CreateRemoteError(entry.Error);
                            await CompleteAfterStepErrorAsync(cancellationToken).ConfigureAwait(false);
                            throw stepError;

                        case "error":
                            throw CreateRemoteError(entry.Error);

                        case "row":
                        case "step_end":
                            throw Malformed($"{entry.Type} was received before step_begin");

                        default:
                            // Never skip an unknown entry: it may be carrying rows or ending a
                            // step, so ignoring it would silently truncate the result.
                            throw Malformed($"unknown cursor entry type '{entry.Type}'");
                    }
                }
            }
            catch (Exception exception)
            {
                if (!Terminated)
                    await HandleFailureAsync(exception).ConfigureAwait(false);
                throw;
            }
        }

        protected override async ValueTask<List<RemoteResponseValue>?> FetchRowAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    var entry = await NextEntryAsync(cancellationToken).ConfigureAwait(false);
                    if (entry is null)
                    {
                        if (_stepOpen)
                            throw Malformed("cursor terminated before step_end");
                        Terminated = true;
                        await FinishOwnerAsync(successful: true).ConfigureAwait(false);
                        return null;
                    }

                    switch (entry.Type)
                    {
                        case "row":
                            if (!_stepOpen)
                                throw Malformed("row was received outside a step");
                            var row = entry.Row ?? throw Malformed("row entry did not include row");
                            if (row.Count != Columns.Count)
                                throw Malformed($"row contained {row.Count} values for {Columns.Count} columns");
                            return row;

                        case "step_end":
                            if (!_stepOpen)
                                throw Malformed("step_end was received outside a step");
                            _stepOpen = false;
                            RecordsAffected = checked((int)(entry.AffectedRowCount ?? 0));
                            break;

                        case "step_error":
                            _stepOpen = false;
                            var stepError = CreateRemoteError(entry.Error);
                            await CompleteAfterStepErrorAsync(cancellationToken).ConfigureAwait(false);
                            throw stepError;

                        case "error":
                            throw CreateRemoteError(entry.Error);

                        case "step_begin":
                            throw Malformed("cursor returned more than one step");

                        default:
                            throw Malformed($"unknown cursor entry type '{entry.Type}'");
                    }
                }
            }
            catch (Exception exception)
            {
                if (!Terminated)
                    await HandleFailureAsync(exception).ConfigureAwait(false);
                throw;
            }
        }

        public override async ValueTask DisposeAsync()
        {
            if (Disposed)
                return;

            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (!Terminated)
                {
                    if (await ReadRowAsync(cleanup.Token).ConfigureAwait(false) is null)
                        break;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (AhtolaException)
            {
                // A drain failure must not mask the caller's disposal.
            }
            finally
            {
                Disposed = true;
                if (!_ownerFinished)
                    await FinishOwnerAsync(successful: Terminated).ConfigureAwait(false);
            }
        }

        private async Task<RemoteCursorEntry?> NextEntryAsync(CancellationToken cancellationToken)
        {
            while (_entries.Count == 0)
            {
                if (_done)
                    return null;

                var (entries, done) = await _session
                    .FetchAsync(_owner._webSocketTransport!.Options.CursorPageSize, _commandTimeout, cancellationToken)
                    .ConfigureAwait(false);
                _done = done;
                foreach (var entry in entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.Type))
                        throw Malformed("entry did not include type");
                    _entries.Enqueue(entry);
                }

                if (entries.Count == 0 && !done)
                {
                    throw Malformed("fetch_cursor returned no entries without reporting completion");
                }
            }

            return _entries.Dequeue();
        }

        private async Task CompleteAfterStepErrorAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var entry = await NextEntryAsync(cancellationToken).ConfigureAwait(false);
                if (entry is null)
                {
                    Terminated = true;
                    await FinishOwnerAsync(successful: true).ConfigureAwait(false);
                    return;
                }

                switch (entry.Type)
                {
                    case "error":
                        throw CreateRemoteError(entry.Error);

                    case "step_begin":
                    case "row":
                    case "step_end":
                    case "step_error":
                        throw Malformed($"unexpected {entry.Type} after step_error");

                    default:
                        throw Malformed($"unknown cursor entry type '{entry.Type}'");
                }
            }
        }

        private async Task HandleFailureAsync(Exception failure)
        {
            if (Disposed)
                return;

            Disposed = true;
            await FinishOwnerAsync(successful: false).ConfigureAwait(false);
            if (!_failureReported)
            {
                _failureReported = true;
                _failureCallback?.Invoke(failure);
            }
        }

        private async ValueTask FinishOwnerAsync(bool successful)
        {
            if (_ownerFinished)
                return;
            _ownerFinished = true;
            await _owner
                .FinishWebSocketCursorAsync(_session, successful, _closeAfter, _commandTimeout)
                .ConfigureAwait(false);
        }
    }
}
