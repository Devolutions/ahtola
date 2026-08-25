using System.Globalization;
using System.Text.Json;

namespace Ahtola;

/// <summary>
/// Owns the Hrana WebSocket connection generations for one ADO.NET remote session and
/// maps ADO.NET operations onto Hrana stream/cursor lifecycles.
/// </summary>
/// <remarks>
/// <para>
/// A <c>stream_id</c> is the WebSocket analogue of the HTTP pipeline baton, but it is
/// scoped to a single physical connection: when a generation dies, its streams, cursors
/// and stored SQL die with it. Nothing is ever replayed. A later operation may establish a
/// brand-new connection (bounded by <see cref="AhtolaHranaWebSocketOptions.ConnectAttempts"/>)
/// and a brand-new stream, but if a session was open when the generation died the ADO.NET
/// remote session is invalidated and the failure surfaces to the caller instead.
/// </para>
/// <para>There is no silent WebSocket-to-HTTP fallback: <c>ws</c>/<c>wss</c> stays on WebSocket.</para>
/// </remarks>
internal sealed class AhtolaHranaWebSocketTransport : IAsyncDisposable, IDisposable
{
    private readonly Uri _endpoint;
    private readonly string? _authToken;
    private readonly IAhtolaWebSocketConnector _connector;

    /// <summary>
    /// Serializes connection/stream lifecycle. Deliberately never disposed: disposal can time
    /// out waiting for it, and disposing it out from under a concurrent operation would turn
    /// that operation's <c>finally { Release(); }</c> into an <see cref="ObjectDisposedException"/>
    /// that masks the real failure. A <see cref="SemaphoreSlim"/> whose
    /// <c>AvailableWaitHandle</c> is never touched holds no unmanaged resource.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>
    /// Guards the disposal decision. Everything that publishes or steals
    /// <see cref="_connection"/> relative to <see cref="_disposed"/> takes it, so a socket that
    /// finishes connecting after disposal started can never be published (and leaked) behind
    /// disposal's back.
    /// </summary>
    private readonly object _disposalGate = new();

    private AhtolaHranaWebSocketConnection? _connection;
    private int? _streamId;
    private int _sessionOpen;
    private long _generation;
    private string? _invalidationReason;
    private Task? _disposal;
    private volatile bool _disposed;

    public AhtolaHranaWebSocketTransport(
        Uri endpoint,
        string? authToken,
        AhtolaHranaWebSocketOptions options,
        IAhtolaWebSocketConnector? connector = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(options);

        _endpoint = endpoint;
        _authToken = string.IsNullOrWhiteSpace(authToken) ? null : authToken;
        Options = options.Validate();
        _connector = connector ?? AhtolaClientWebSocketConnector.Instance;
    }

    public AhtolaHranaWebSocketOptions Options { get; }

    /// <summary>True while a Hrana stream is open (the WebSocket analogue of an HTTP baton).</summary>
    /// <remarks>
    /// Read without taking the lifecycle gate so a caller can never block behind a
    /// connection attempt.
    /// </remarks>
    public bool HasOpenSession => Volatile.Read(ref _sessionOpen) != 0;

    private void SetStreamId(int? streamId)
    {
        _streamId = streamId;
        Volatile.Write(ref _sessionOpen, streamId is null ? 0 : 1);
    }

    /// <summary>Negotiated protocol version of the live generation, or null when not connected.</summary>
    public int? NegotiatedVersion => _connection is { IsAlive: true } connection ? connection.Version : null;

    /// <summary>Generation counter; incremented for every physical connection attempt.</summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>
    /// Drops the current stream without waiting: used by the ADO.NET layer when it decides
    /// the remote session is no longer usable. Several call sites invoke this from inside a
    /// <c>catch</c> block, so it must never block the caller and must never itself throw.
    /// It is a fire-and-forget wrapper over <see cref="ResetSessionAsync"/>, which does the
    /// real work (including the network close) without touching the calling thread.
    /// </summary>
    public void ResetSession()
    {
        ResetSessionCompletion = Task.Run(async () =>
        {
            try
            {
                await ResetSessionAsync().ConfigureAwait(false);
            }
            catch
            {
                // ResetSessionAsync funnels every close failure through the generation-
                // retiring policy before it can throw here; this catch exists only so a
                // fire-and-forget failure can never surface as an unobserved task exception
                // on the thread pool.
            }
        });
    }

    /// <summary>
    /// Test seam for <see cref="ResetSession"/>'s fire-and-forget work. The <see cref="Task"/>
    /// is created and stored synchronously inside <see cref="ResetSession"/> — before it
    /// returns to its caller — so awaiting this property afterwards deterministically observes
    /// the in-flight reset instead of requiring a poll or an arbitrary delay. Starts out
    /// already completed so awaiting it before any reset has happened is always safe.
    /// </summary>
    internal Task ResetSessionCompletion { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Does the work <see cref="ResetSession"/> used to do inline, but fully async and without
    /// swallowing failures: drops the transport's own reference to the stream immediately, then
    /// asks the server to close it. An unconfirmed close (error, drop, or timeout) retires the
    /// whole generation — the same policy every other handle close honours (see
    /// <see cref="RetireGenerationForOrphanedHandleAsync"/>) — because the transport has already
    /// forgotten the stream id, so dropping the socket is the only way left to make the server
    /// reclaim it. Internal rather than public: <see cref="ResetSession"/> is the only
    /// production call site, and it stays the public fire-and-forget entry point so existing
    /// synchronous callers are unaffected.
    /// </summary>
    internal async Task ResetSessionAsync()
    {
        int? streamId;
        AhtolaHranaWebSocketConnection? connection;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            streamId = _streamId;
            SetStreamId(null);
            _invalidationReason = null;
            connection = _connection;
        }
        finally
        {
            _gate.Release();
        }

        if (streamId is not { } id || connection is not { IsAlive: true })
            return;

        try
        {
            await connection
                .SendRequestAsync(HranaRequest.ForCloseStream(id), Options.CloseTimeout, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await RetireGenerationForOrphanedHandleAsync(
                    connection,
                    exception,
                    "stream " + id.ToString(CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
        }
    }

    public async Task<RemoteStatementResult> ExecuteAsync(
        RemoteStatement statement,
        int commandTimeout,
        bool closeAfter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statement);

        var lease = await AcquireStreamAsync(commandTimeout, cancellationToken).ConfigureAwait(false);
        try
        {
            var response = await lease.Connection
                .SendRequestAsync(
                    HranaRequest.ForExecute(lease.StreamId, statement),
                    CommandTimeout(commandTimeout),
                    cancellationToken)
                .ConfigureAwait(false);
            var result = ReadResult(lease, response, HranaRequest.Execute, AhtolaHranaJsonContext.Default.RemoteStatementResult);
            if (closeAfter)
                await ReleaseStreamAsync(lease, commandTimeout).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception)
        {
            await HandleOperationFailureAsync(lease, exception).ConfigureAwait(false);
            await ReleaseStreamAfterFailureAsync(lease, closeAfter, commandTimeout).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<RemoteBatchResult> ExecuteBatchAsync(
        RemoteBatch batch,
        int commandTimeout,
        bool closeAfter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        // Condition gating runs inside the acquire gate, before a stream is minted, so a
        // batch the negotiated version cannot evaluate never leaves a half-opened stream.
        var lease = await AcquireStreamAsync(
                commandTimeout,
                cancellationToken,
                connection => HranaBatchContract.EnsureVersionSupports(batch, connection.Version))
            .ConfigureAwait(false);
        try
        {
            var response = await lease.Connection
                .SendRequestAsync(
                    HranaRequest.ForBatch(lease.StreamId, batch),
                    CommandTimeout(commandTimeout),
                    cancellationToken)
                .ConfigureAwait(false);
            var result = ReadResult(lease, response, HranaRequest.BatchRequest, AhtolaHranaJsonContext.Default.RemoteBatchResult);
            if (closeAfter)
                await ReleaseStreamAsync(lease, commandTimeout).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception)
        {
            await HandleOperationFailureAsync(lease, exception).ConfigureAwait(false);
            await ReleaseStreamAfterFailureAsync(lease, closeAfter, commandTimeout).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Runs the multi-statement <c>sequence</c> request (Hrana 2+).
    /// </summary>
    public async Task RunSequenceAsync(
        string? sql,
        int? sqlId,
        int commandTimeout,
        bool closeAfter,
        CancellationToken cancellationToken)
    {
        var lease = await AcquireStreamAsync(
                commandTimeout,
                cancellationToken,
                static connection => connection.EnsureVersionSupports(HranaRequest.Sequence))
            .ConfigureAwait(false);
        try
        {
            await lease.Connection
                .SendRequestAsync(
                    HranaRequest.ForSequence(lease.StreamId, sql, sqlId),
                    CommandTimeout(commandTimeout),
                    cancellationToken)
                .ConfigureAwait(false);
            if (closeAfter)
                await ReleaseStreamAsync(lease, commandTimeout).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await HandleOperationFailureAsync(lease, exception).ConfigureAwait(false);
            await ReleaseStreamAfterFailureAsync(lease, closeAfter, commandTimeout).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Runs the <c>describe</c> request (Hrana 2+).</summary>
    public async Task<RemoteDescribeResult> DescribeAsync(
        string? sql,
        int? sqlId,
        int commandTimeout,
        bool closeAfter,
        CancellationToken cancellationToken)
    {
        var lease = await AcquireStreamAsync(
                commandTimeout,
                cancellationToken,
                static connection => connection.EnsureVersionSupports(HranaRequest.Describe))
            .ConfigureAwait(false);
        try
        {
            var response = await lease.Connection
                .SendRequestAsync(
                    HranaRequest.ForDescribe(lease.StreamId, sql, sqlId),
                    CommandTimeout(commandTimeout),
                    cancellationToken)
                .ConfigureAwait(false);
            var result = ReadResult(lease, response, HranaRequest.Describe, AhtolaHranaJsonContext.Default.RemoteDescribeResult);
            if (closeAfter)
                await ReleaseStreamAsync(lease, commandTimeout).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception)
        {
            await HandleOperationFailureAsync(lease, exception).ConfigureAwait(false);
            await ReleaseStreamAfterFailureAsync(lease, closeAfter, commandTimeout).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Stores SQL text connection-wide (Hrana 2+). Unlike the HTTP variant, stored SQL on a
    /// WebSocket belongs to the connection, so it dies with the generation.
    /// </summary>
    public async Task<int> StoreSqlAsync(string sql, int commandTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var connection = await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);
        var sqlId = connection.AllocateSqlId();
        await connection
            .SendRequestAsync(
                HranaRequest.ForStoreSql(sqlId, sql),
                CommandTimeout(commandTimeout),
                cancellationToken,
                // Stored SQL is a server-side handle like a stream or cursor: an abandoned
                // store_sql that the server later honours would pin the text for the life of
                // the connection.
                orphanCompensation: () => connection.SendRequestAsync(
                    HranaRequest.ForCloseSql(sqlId),
                    Options.CloseTimeout,
                    CancellationToken.None))
            .ConfigureAwait(false);
        return sqlId;
    }

    /// <summary>Releases SQL text stored with <see cref="StoreSqlAsync"/>.</summary>
    public async Task CloseSqlAsync(int sqlId, int commandTimeout, CancellationToken cancellationToken)
    {
        var connection = await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await connection
                .SendRequestAsync(HranaRequest.ForCloseSql(sqlId), CommandTimeout(commandTimeout), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Stored SQL is a server-side handle like a stream or cursor (see StoreSqlAsync):
            // the caller has already stopped tracking sqlId, so an unconfirmed close must
            // retire the generation rather than leave the text pinned for the life of the
            // connection.
            await RetireGenerationForOrphanedHandleAsync(
                    connection,
                    exception,
                    "stored sql " + sqlId.ToString(CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Reads the stream's autocommit state (Hrana 3).</summary>
    public async Task<bool> GetAutocommitAsync(int commandTimeout, CancellationToken cancellationToken)
    {
        var lease = await AcquireStreamAsync(
                commandTimeout,
                cancellationToken,
                static connection => connection.EnsureVersionSupports(HranaRequest.GetAutocommit))
            .ConfigureAwait(false);
        try
        {
            var response = await lease.Connection
                .SendRequestAsync(
                    HranaRequest.ForGetAutocommit(lease.StreamId),
                    CommandTimeout(commandTimeout),
                    cancellationToken)
                .ConfigureAwait(false);

            // The receive path already rejected an absent is_autocommit, so a null here would
            // mean the contract check was bypassed; fail loudly instead of guessing false.
            return response.IsAutocommit
                   ?? throw lease.Connection.FaultProtocol(
                       "The Hrana 'get_autocommit' response did not include the mandatory boolean "
                       + "'is_autocommit' field.");
        }
        catch (Exception exception)
        {
            await HandleOperationFailureAsync(lease, exception).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Opens a Hrana 3 cursor. Returns null when the negotiated version predates cursors so
    /// the caller can fall back to a buffered <c>execute</c>/<c>batch</c> on the same stream.
    /// </summary>
    public async Task<AhtolaHranaCursorSession?> OpenCursorAsync(
        RemoteBatch batch,
        int commandTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        // Batch conditions are gated before the stream is minted. Cursors themselves are not:
        // a pre-v3 server still gets a usable stream so the caller can fall back to execute.
        var lease = await AcquireStreamAsync(
                commandTimeout,
                cancellationToken,
                connection => HranaBatchContract.EnsureVersionSupports(batch, connection.Version))
            .ConfigureAwait(false);
        if (lease.Connection.Version < 3)
            return null;

        try
        {
            var connection = lease.Connection;
            var cursorId = connection.AllocateCursorId();
            await connection
                .SendRequestAsync(
                    HranaRequest.ForOpenCursor(lease.StreamId, cursorId, batch),
                    CommandTimeout(commandTimeout),
                    cancellationToken,
                    // An abandoned open_cursor may still create the cursor server-side, which
                    // would leave the stream permanently occupied. Close it on late success.
                    orphanCompensation: () => connection.SendRequestAsync(
                        HranaRequest.ForCloseCursor(cursorId),
                        Options.CloseTimeout,
                        CancellationToken.None))
                .ConfigureAwait(false);
            return new AhtolaHranaCursorSession(this, lease, cursorId);
        }
        catch (Exception exception)
        {
            await HandleOperationFailureAsync(lease, exception).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Closes the ADO.NET-visible stream, mirroring the HTTP pipeline's trailing close.</summary>
    public async Task CloseSessionAsync(int commandTimeout, CancellationToken cancellationToken)
    {
        AhtolaHranaWebSocketConnection? connection;
        int streamId;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_streamId is not { } id)
                return;
            connection = _connection;
            streamId = id;
            SetStreamId(null);
        }
        finally
        {
            _gate.Release();
        }

        if (connection is not { IsAlive: true })
            return;

        var lease = new StreamLease(connection, streamId, WasExistingSession: true);
        try
        {
            await connection
                .SendRequestAsync(
                    HranaRequest.ForCloseStream(streamId),
                    CommandTimeout(commandTimeout),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The stream id has already been dropped here, so an unconfirmed close would
            // strand it server-side with nothing left to retry it.
            await HandleHandleCloseFailureAsync(
                    lease,
                    exception,
                    "stream " + streamId.ToString(CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
            throw;
        }
    }

    public void Dispose() => EnsureDisposal().GetAwaiter().GetResult();

    public ValueTask DisposeAsync() => new(EnsureDisposal());

    /// <summary>
    /// One idempotent disposal task shared by <see cref="Dispose"/> and
    /// <see cref="DisposeAsync"/>, so a synchronous ADO.NET dispose never returns while the
    /// generation's socket or loops are still live. The graceful phase is bounded; once that
    /// budget is spent the connection aborts and disposal still waits for confirmed loop
    /// termination and socket disposal.
    /// </summary>
    private Task EnsureDisposal()
    {
        lock (_disposalGate)
            return _disposal ??= Task.Run(DisposeCoreAsync);
    }

    private async Task DisposeCoreAsync()
    {
        AhtolaHranaWebSocketConnection? connection;
        int? streamId;

        // The graceful phase is bounded: an operation wedged against a dead peer still holds
        // the lifecycle gate, and disposal must not wait on it forever. Failing to acquire it
        // only costs the courtesy close_stream — the connection disposal below still aborts
        // the socket and waits for confirmed loop termination.
        var acquired = await _gate.WaitAsync(Options.CloseTimeout).ConfigureAwait(false);
        try
        {
            // Flipping the flag and stealing the connection happen together, and under the same
            // lock the publication path uses. Doing them separately would let a connect that is
            // still in flight publish its socket after this snapshot was taken, leaving a live
            // socket and two loops nothing would ever dispose.
            lock (_disposalGate)
            {
                _disposed = true;
                connection = _connection;
                _connection = null;
            }

            streamId = acquired ? _streamId : null;
            if (acquired)
                SetStreamId(null);
        }
        finally
        {
            if (acquired)
                _gate.Release();
        }

        if (connection is null)
            return;

        if (streamId is { } id && connection.IsAlive)
        {
            try
            {
                using var closeCancellation = new CancellationTokenSource(Options.CloseTimeout);
                await connection
                    .SendRequestAsync(HranaRequest.ForCloseStream(id), Options.CloseTimeout, closeCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Best effort: the socket close below releases the stream server-side.
            }
        }

        await connection.DisposeAsync().ConfigureAwait(false);
    }

    internal TimeSpan CommandTimeout(int commandTimeout)
        => commandTimeout > 0 ? TimeSpan.FromSeconds(commandTimeout) : TimeSpan.Zero;

    /// <summary>
    /// Projects a validated response onto its typed result.
    /// </summary>
    /// <remarks>
    /// The receive path already enforced the structural contract, so anything that still
    /// fails here is a protocol violation the caller must never see as data: it faults the
    /// generation instead of surfacing as an ordinary application error.
    /// </remarks>
    internal static T ReadResult<T>(
        StreamLease lease,
        HranaResponse response,
        string expectedType,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        if (!string.Equals(response.Type, expectedType, StringComparison.Ordinal))
        {
            throw lease.Connection.FaultProtocol(
                $"Expected a '{expectedType}' response but received '{response.Type}'.");
        }

        if (response.Result.ValueKind is System.Text.Json.JsonValueKind.Undefined or System.Text.Json.JsonValueKind.Null)
            throw lease.Connection.FaultProtocol($"The Hrana '{expectedType}' response did not include a result.");

        try
        {
            return response.Result.Deserialize(typeInfo)
                   ?? throw lease.Connection.FaultProtocol($"The Hrana '{expectedType}' response returned an empty result.");
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw lease.Connection.FaultProtocol(
                $"Unable to parse the Hrana '{expectedType}' result: {exception.Message}");
        }
    }

    /// <summary>
    /// Reports an operation failure so a dead generation is retired. Application-level
    /// errors and caller cancellations leave the connection and stream untouched.
    /// </summary>
    internal async Task HandleOperationFailureAsync(StreamLease lease, Exception exception, bool sessionCritical = false)
    {
        if (exception is OperationCanceledException || lease.Connection.IsAlive)
            return;

        AhtolaHranaWebSocketConnection? retired = null;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_connection, lease.Connection))
            {
                retired = _connection;
                _connection = null;
            }

            SetStreamId(null);
            if (lease.WasExistingSession || sessionCritical)
            {
                _invalidationReason =
                    "The Hrana WebSocket connection was lost while a remote session was open, so the session "
                    + $"(and any open transaction or cursor) was invalidated: {lease.Connection.Fault?.Message ?? exception.Message} "
                    + "Nothing was replayed; reopen the connection and retry the work explicitly.";
            }
        }
        finally
        {
            _gate.Release();
        }

        if (retired is not null)
            await retired.DisposeAsync().ConfigureAwait(false);
    }

    internal async Task ReleaseStreamAsync(StreamLease lease, int commandTimeout)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_streamId == lease.StreamId)
                SetStreamId(null);
        }
        finally
        {
            _gate.Release();
        }

        if (!lease.Connection.IsAlive)
            return;

        try
        {
            await lease.Connection
                .SendRequestAsync(
                    HranaRequest.ForCloseStream(lease.StreamId),
                    CommandTimeout(commandTimeout),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await HandleHandleCloseFailureAsync(
                    lease,
                    exception,
                    "stream "
                    + lease.StreamId.ToString(CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
        }
    }

    /// <summary>Bridges a failed lease-scoped close onto the shared handle-close policy.</summary>
    private Task HandleHandleCloseFailureAsync(StreamLease lease, Exception exception, string handleDescription)
        => RetireGenerationForOrphanedHandleAsync(lease.Connection, exception, handleDescription);

    /// <summary>
    /// Handles a lifecycle close (<c>close_stream</c>/<c>close_cursor</c>/<c>close_sql</c>)
    /// that did not succeed, for whichever connection currently owns the handle.
    /// </summary>
    /// <remarks>
    /// The transport has already dropped its own reference to the handle by the time the close
    /// is issued, so a swallowed failure would forget a handle the server may still hold: it
    /// would occupy the connection until the generation eventually died on its own. When the
    /// generation is still healthy the only way to guarantee the handle is reclaimed is to end
    /// the generation, which releases every stream, cursor and stored SQL with the socket. No
    /// session invalidation reason is recorded, because the caller asked for this handle to be
    /// closed: a later operation simply reconnects.
    /// </remarks>
    private async Task RetireGenerationForOrphanedHandleAsync(
        AhtolaHranaWebSocketConnection connection,
        Exception exception,
        string handleDescription)
    {
        if (connection.IsAlive)
        {
            connection.RetireForOrphanedHandle(
                $"{handleDescription} could not be closed ({exception.Message})");
        }

        AhtolaHranaWebSocketConnection? retired = null;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_connection, connection))
            {
                retired = _connection;
                _connection = null;
                SetStreamId(null);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (retired is not null)
            await retired.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Honours <c>closeAfter</c> even when the operation failed.
    /// </summary>
    /// <remarks>
    /// A Hrana <c>response_error</c> is an ordinary application error: the request was
    /// rejected but the stream — and the whole generation — is still perfectly healthy. The
    /// caller asked for the stream to be closed afterwards, so skipping the close on that
    /// path would strand the stream server-side until the connection eventually dies, and
    /// would leave the transport reporting an open session the caller believes it closed.
    /// When the generation is already dead the stream died with it and there is nothing to
    /// send.
    /// </remarks>
    private async Task ReleaseStreamAfterFailureAsync(StreamLease lease, bool closeAfter, int commandTimeout)
    {
        if (!closeAfter || !lease.Connection.IsAlive)
            return;

        // Only when the transport still owns this lease's stream. If the trailing close
        // already ran on the success path, or the session was reset underneath us, a second
        // close_stream would be a duplicate for an id the server has already released.
        await _gate.WaitAsync().ConfigureAwait(false);
        bool owned;
        try
        {
            owned = _streamId == lease.StreamId;
        }
        finally
        {
            _gate.Release();
        }

        if (!owned)
            return;

        try
        {
            await ReleaseStreamAsync(lease, commandTimeout).ConfigureAwait(false);
        }
        catch
        {
            // The caller's original failure is the interesting one; a failed trailing close
            // has already retired the generation through HandleOperationFailureAsync.
        }
    }

    /// <summary>Bridges a failed <c>close_cursor</c> onto the shared handle-close policy.</summary>
    internal Task HandleCursorCloseFailureAsync(StreamLease lease, Exception exception, int cursorId)
        => HandleHandleCloseFailureAsync(
            lease,
            exception,
            "cursor " + cursorId.ToString(CultureInfo.InvariantCulture));

    private async Task<StreamLease> AcquireStreamAsync(
        int commandTimeout,
        CancellationToken cancellationToken,
        Action<AhtolaHranaWebSocketConnection>? validate = null)
    {
        // The whole acquire is serialized so concurrent callers share one stream instead of
        // racing to open several (the ADO.NET layer already serializes, but the transport
        // must not depend on that).
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AhtolaHranaWebSocketConnection? retired = null;
        try
        {
            var connection = await EnsureConnectionCoreAsync(cancellationToken, retire => retired = retire)
                .ConfigureAwait(false);

            // Version/shape gating happens here, after the connection is known but before any
            // stream is minted, so a request the negotiated version cannot serve never leaks
            // a stream and needs no compensating close.
            validate?.Invoke(connection);

            if (_streamId is { } streamId)
                return new StreamLease(connection, streamId, WasExistingSession: true);

            var openedId = connection.AllocateStreamId();
            await connection
                .SendRequestAsync(
                    HranaRequest.ForOpenStream(openedId),
                    CommandTimeout(commandTimeout),
                    cancellationToken,
                    // If the caller walks away mid-open the server may still create the
                    // stream. The connection keeps the correlation slot and closes the stream
                    // when the late reply lands, so an abandoned open never leaks a handle.
                    orphanCompensation: () => connection.SendRequestAsync(
                        HranaRequest.ForCloseStream(openedId),
                        Options.CloseTimeout,
                        CancellationToken.None))
                .ConfigureAwait(false);
            SetStreamId(openedId);
            return new StreamLease(connection, openedId, WasExistingSession: false);
        }
        finally
        {
            _gate.Release();
            if (retired is not null)
                await retired.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<AhtolaHranaWebSocketConnection> EnsureConnectionAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AhtolaHranaWebSocketConnection? retired = null;
        try
        {
            return await EnsureConnectionCoreAsync(cancellationToken, retire => retired = retire).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            if (retired is not null)
                await retired.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Requires <see cref="_gate"/> to be held by the caller.</summary>
    private async Task<AhtolaHranaWebSocketConnection> EnsureConnectionCoreAsync(
        CancellationToken cancellationToken,
        Action<AhtolaHranaWebSocketConnection?> retire)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_invalidationReason is { } reason)
            throw new AhtolaException(reason);

        if (_connection is { IsAlive: true } alive)
            return alive;

        if (_streamId is not null)
        {
            var detail = _connection?.Fault?.Message ?? "the connection was closed";
            retire(_connection);
            _connection = null;
            SetStreamId(null);
            _invalidationReason =
                $"The Hrana WebSocket connection was lost while a remote session was open ({detail}). "
                + "Streams, cursors and stored SQL do not survive a reconnect and nothing is replayed; "
                + "reopen the connection and retry the work explicitly.";
            throw new AhtolaException(_invalidationReason);
        }

        retire(_connection);
        _connection = null;
        var connection = await ConnectWithRetriesAsync(cancellationToken).ConfigureAwait(false);

        // Disposal can time out on the gate and complete while this connect is still in
        // flight. Publishing the new connection then would leak a live socket and two loops
        // that nothing ever disposes, so the disposal check and the publication are one
        // atomic step under the disposal lock; a refused publication hands the socket back.
        bool published;
        lock (_disposalGate)
        {
            published = !_disposed;
            if (published)
                _connection = connection;
        }

        if (!published)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(AhtolaHranaWebSocketTransport));
        }

        return connection;
    }

    private async Task<AhtolaHranaWebSocketConnection> ConnectWithRetriesAsync(CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= Options.ConnectAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var generation = Interlocked.Increment(ref _generation);
            try
            {
                return await AhtolaHranaWebSocketConnection
                    .ConnectAsync(_endpoint, _authToken, _connector, Options, generation, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (AhtolaHranaProtocolException)
            {
                // Negotiation/protocol failures are deterministic: retrying cannot help.
                throw;
            }
            catch (Exception exception)
            {
                lastFailure = exception;
                if (attempt == Options.ConnectAttempts)
                    break;

                var delay = TimeSpan.FromMilliseconds(
                    Options.ConnectRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        var summary =
            $"Unable to establish a Hrana WebSocket connection to {_endpoint} after "
            + $"{Options.ConnectAttempts.ToString(CultureInfo.InvariantCulture)} attempt(s): {lastFailure?.Message}";
        throw lastFailure is null
            ? new AhtolaException(summary)
            : new AhtolaException(summary, lastFailure);
    }

    /// <summary>A borrowed Hrana stream plus the connection generation that owns it.</summary>
    internal readonly record struct StreamLease(
        AhtolaHranaWebSocketConnection Connection,
        int StreamId,
        bool WasExistingSession);
}

/// <summary>
/// A Hrana 3 cursor opened over the WebSocket transport. Only one cursor may be open per
/// stream at a time, and no other request may be sent on that stream until it is closed.
/// </summary>
internal sealed class AhtolaHranaCursorSession(
    AhtolaHranaWebSocketTransport transport,
    AhtolaHranaWebSocketTransport.StreamLease lease,
    int cursorId)
{
    public int CursorId { get; } = cursorId;

    public AhtolaHranaWebSocketTransport.StreamLease Lease { get; } = lease;

    public bool IsAlive => Lease.Connection.IsAlive;

    /// <summary>Fetches the next page of cursor entries.</summary>
    public async Task<(List<RemoteCursorEntry> Entries, bool Done)> FetchAsync(
        int maxCount,
        int commandTimeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await Lease.Connection
                .SendRequestAsync(
                    HranaRequest.ForFetchCursor(CursorId, maxCount),
                    transport.CommandTimeout(commandTimeout),
                    cancellationToken)
                .ConfigureAwait(false);

            // The receive path rejects a fetch_cursor without entries/done, so a missing field
            // here would mean the contract check was bypassed: fault rather than invent a
            // "not done, no entries" answer that would spin the cursor forever.
            if (response.Entries is not { } entries || response.Done is not { } done)
            {
                throw Lease.Connection.FaultProtocol(
                    "The Hrana 'fetch_cursor' response did not include the mandatory 'entries' array and "
                    + "'done' flag.");
            }

            return (entries, done);
        }
        catch (Exception exception)
        {
            // A cursor spans several round trips: if the generation dies mid-cursor the
            // ADO.NET reader and its stream are unrecoverable, so the session is invalidated.
            await transport.HandleOperationFailureAsync(Lease, exception, sessionCritical: true).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Closes the cursor so the stream can accept requests again.</summary>
    /// <remarks>
    /// A close that is not confirmed cannot be swallowed: the cursor would keep occupying its
    /// stream server-side while this session forgets it exists. The transport retires the
    /// generation instead, which reclaims the cursor with the socket.
    /// </remarks>
    public async Task CloseAsync(int commandTimeout)
    {
        if (!Lease.Connection.IsAlive)
            return;

        try
        {
            await Lease.Connection
                .SendRequestAsync(
                    HranaRequest.ForCloseCursor(CursorId),
                    transport.CommandTimeout(commandTimeout),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await transport.HandleCursorCloseFailureAsync(Lease, exception, CursorId).ConfigureAwait(false);
        }
    }

    /// <summary>Closes the owning stream, mirroring the HTTP cursor's trailing close.</summary>
    public Task CloseStreamAsync(int commandTimeout)
        => transport.ReleaseStreamAsync(Lease, commandTimeout);
}
