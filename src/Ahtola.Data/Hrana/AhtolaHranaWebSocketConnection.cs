using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace Ahtola;

/// <summary>
/// One Hrana WebSocket "generation": exactly one <see cref="WebSocket"/>, one serialized
/// send path, one continuous receive loop, and one request-id correlation table.
/// </summary>
/// <remarks>
/// <para>
/// A generation is never resumable. When it faults (protocol violation, oversize message,
/// wrong frame opcode, socket error, or peer close) every pending request fails and every
/// <c>stream_id</c>/<c>cursor_id</c>/<c>sql_id</c> minted on it dies with it. Recovery is
/// connection replacement, never frame replay — replaying in-flight writes could
/// double-execute non-idempotent statements.
/// </para>
/// <para>
/// The <see cref="WebSocket"/> contract allows at most one outstanding send and one
/// outstanding receive. This type upholds that by construction: only
/// <see cref="RunSendLoopAsync"/> ever calls <c>SendAsync</c> and only
/// <see cref="RunReceiveLoopAsync"/> ever calls <c>ReceiveAsync</c>. Keeping a receive
/// always outstanding is also what lets the runtime process keep-alive pongs.
/// <c>CloseOutputAsync</c> is itself a send, so it is only ever issued after the send loop
/// has been <em>observed</em> to terminate; if the loop is wedged on a dead peer the socket
/// is aborted instead and the close frame is skipped entirely.
/// </para>
/// </remarks>
internal sealed class AhtolaHranaWebSocketConnection : IAsyncDisposable, IDisposable
{
    private const int ReceiveChunkBytes = 16 * 1024;
    private const int SendChunkBytes = 64 * 1024;

    private readonly WebSocket _socket;
    private readonly AhtolaHranaWebSocketOptions _options;
    private readonly Channel<OutboundFrame> _outbound;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<int, PendingRequest> _pending = new();
    private readonly object _cancelledGate = new();
    private readonly HashSet<int> _cancelledIds = [];
    private readonly object _disposalGate = new();
    private readonly TaskCompletionSource<bool> _hello =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task _sendLoop = Task.CompletedTask;
    private Task _receiveLoop = Task.CompletedTask;
    private Task _watchdog = Task.CompletedTask;
    private Task? _disposal;
    private long _lastReceiveTicks = Environment.TickCount64;
    private int _nextRequestId;
    private int _nextStreamId;
    private int _nextCursorId;
    private int _nextSqlId;
    private Exception? _fault;
    private int _helloSeen;
    private int _closeIssued;
    private int _disposed;

    private AhtolaHranaWebSocketConnection(WebSocket socket, int version, long generation, AhtolaHranaWebSocketOptions options)
    {
        _socket = socket;
        Version = version;
        Generation = generation;
        _options = options;
        _outbound = Channel.CreateBounded<OutboundFrame>(new BoundedChannelOptions(options.SendQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <summary>Negotiated Hrana version (1, 2 or 3).</summary>
    public int Version { get; }

    /// <summary>Monotonic generation number; every reconnect increments it.</summary>
    public long Generation { get; }

    /// <summary>The failure that terminated this generation, if any.</summary>
    public Exception? Fault => Volatile.Read(ref _fault);

    public bool IsAlive => Volatile.Read(ref _fault) is null && Volatile.Read(ref _disposed) == 0;

    /// <summary>Correlation slots still awaiting a server response (diagnostic/test hook).</summary>
    internal int OutstandingRequestCount => _pending.Count;

    /// <summary>Cancelled-request tombstones retained for this generation (diagnostic/test hook).</summary>
    internal int CancelledRequestCount
    {
        get
        {
            lock (_cancelledGate)
                return _cancelledIds.Count;
        }
    }

    /// <summary>
    /// Connects, negotiates a JSON subprotocol, starts both loops and completes the
    /// <c>hello</c> handshake. The returned connection is ready for requests.
    /// </summary>
    public static async Task<AhtolaHranaWebSocketConnection> ConnectAsync(
        Uri endpoint,
        string? authToken,
        IAhtolaWebSocketConnector connector,
        AhtolaHranaWebSocketOptions options,
        long generation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(options);

        using var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCancellation.CancelAfter(options.ConnectTimeout);

        var socket = await connector
            .ConnectAsync(endpoint, AhtolaHranaWireProtocol.JsonSubProtocols, options, connectCancellation.Token)
            .ConfigureAwait(false);

        AhtolaHranaWebSocketConnection connection;
        try
        {
            var version = AhtolaHranaWireProtocol.NegotiateVersion(socket.SubProtocol);
            connection = new AhtolaHranaWebSocketConnection(socket, version, generation, options);
        }
        catch
        {
            socket.Abort();
            socket.Dispose();
            throw;
        }

        try
        {
            connection.Start();
            await connection.HandshakeAsync(authToken, connectCancellation.Token).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public int AllocateStreamId() => Interlocked.Increment(ref _nextStreamId);

    public int AllocateCursorId() => Interlocked.Increment(ref _nextCursorId);

    public int AllocateSqlId() => Interlocked.Increment(ref _nextSqlId);

    /// <summary>Throws when the negotiated version cannot serve <paramref name="requestType"/>.</summary>
    internal void EnsureVersionSupports(string requestType)
    {
        var minimum = HranaRequest.MinimumVersion(requestType);
        if (minimum > Version)
        {
            throw new AhtolaException(
                $"The Hrana server negotiated protocol version {Version.ToString(System.Globalization.CultureInfo.InvariantCulture)}, "
                + $"which does not support '{requestType}' (requires version {minimum.ToString(System.Globalization.CultureInfo.InvariantCulture)}).");
        }
    }

    /// <summary>
    /// Sends one request and awaits its correlated response.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cancellation (caller token or command timeout) only abandons the caller's wait: the
    /// socket is untouched and a late response for the abandoned id is discarded rather
    /// than treated as a protocol violation.
    /// </para>
    /// <para>
    /// <paramref name="orphanCompensation"/> makes that safe for requests that <em>mint a
    /// server-side handle</em> (<c>open_stream</c>, <c>open_cursor</c>). Those cannot simply
    /// be forgotten: the server may already be creating the handle, so abandoning the wait
    /// would leak it for the lifetime of the connection. When supplied, the correlation slot
    /// survives the caller's cancellation and the compensation runs if — and only if — the
    /// request later succeeds. A compensation that cannot complete retires the generation,
    /// because a leaked handle is worse than a dropped connection.
    /// </para>
    /// <para>
    /// The same compensation also runs when the request is answered with a
    /// <c>response_error</c>: a rejection does not prove the handle was never minted, so the
    /// reserved id is released explicitly rather than assumed dead.
    /// </para>
    /// </remarks>
    public async Task<HranaResponse> SendRequestAsync(
        HranaRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Func<Task>? orphanCompensation = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureVersionSupports(request.Type);
        ThrowIfFaulted();

        var requestId = Interlocked.Increment(ref _nextRequestId);
        var pending = new PendingRequest(request.Type);
        if (!_pending.TryAdd(requestId, pending))
            throw new AhtolaHranaProtocolException($"Duplicate Hrana request id {requestId}.");

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout > TimeSpan.Zero)
            wait.CancelAfter(timeout);

        // "Answered" means the server produced a real reply (ok or error) that this caller
        // consumed. Anything else leaves the request provisional from the server's point of
        // view, which is exactly when a lifecycle compensation has to run.
        var answered = false;
        var reachedTheWire = false;
        try
        {
            var message = new HranaRequestMsg { RequestId = requestId, Request = request };
            await SendMessageAsync(
                    JsonSerializer.SerializeToUtf8Bytes(message, AhtolaHranaJsonContext.Default.HranaRequestMsg),
                    wait.Token,
                    frame => reachedTheWire = frame)
                .ConfigureAwait(false);

            var response = await pending.Completion.Task.WaitAsync(wait.Token).ConfigureAwait(false);
            answered = true;
            return response;
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          && pending.Completion.Task.IsFaulted)
        {
            // A response_error answers the request, but it does not prove that no handle was
            // minted: the rejection may have been raised after the stream or cursor already
            // existed server-side, and the id is never reusable either way. Requests that mint
            // a handle are therefore compensated on rejection too.
            answered = true;
            if (orphanCompensation is not null)
                await CompensateRejectedHandleAsync(requestId, orphanCompensation).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (answered)
            {
                _pending.TryRemove(requestId, out _);
            }
            else
            {
                // A request that never reached the outbound queue cannot have created a
                // server-side handle, so it takes the plain tombstone path. Keeping its slot
                // open would pin a correlation entry (and its compensation closure) for the
                // life of the generation waiting for a reply that can never arrive.
                AbandonRequest(requestId, pending, reachedTheWire ? orphanCompensation : null);
            }
        }
    }

    public void Dispose() => EnsureDisposal().GetAwaiter().GetResult();

    public ValueTask DisposeAsync() => new(EnsureDisposal());

    /// <summary>
    /// Retires the generation because a server-side handle could not be released. Callers use
    /// this when a lifecycle compensation fails: dropping the connection is the only way to
    /// guarantee the orphaned <c>stream_id</c>/<c>cursor_id</c> is reclaimed.
    /// </summary>
    internal void RetireForOrphanedHandle(string description)
        => FaultGeneration(new AhtolaException(
            $"The Hrana WebSocket generation was retired because {description}. Server-side handles are "
            + "released when the connection ends; nothing was replayed."));

    /// <summary>
    /// Faults the generation for a protocol violation observed above the transport (for
    /// example an unparsable typed result), so a malformed payload never degrades into an
    /// ordinary application error.
    /// </summary>
    internal AhtolaHranaProtocolException FaultProtocol(string message)
    {
        var violation = new AhtolaHranaProtocolException(message);
        FaultGeneration(violation);
        return violation;
    }

    /// <summary>
    /// One idempotent disposal task. Both <see cref="Dispose"/> and <see cref="DisposeAsync"/>
    /// converge on it, so a synchronous dispose can never return while the socket or either
    /// loop is still live.
    /// </summary>
    private Task EnsureDisposal()
    {
        lock (_disposalGate)
        {
            // Task.Run keeps the body off the caller's synchronization context so the blocking
            // Dispose() path cannot deadlock on it.
            return _disposal ??= Task.Run(DisposeCoreAsync);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);

        var deadline = Environment.TickCount64 + (long)_options.CloseTimeout.TotalMilliseconds;

        // 1. Stop accepting new outbound frames and let the send loop drain what is queued.
        _outbound.Writer.TryComplete();
        var sendLoopStopped = await WaitBoundedAsync(_sendLoop, Remaining(deadline)).ConfigureAwait(false);

        // 2. Best-effort close handshake, but ONLY once the send loop has actually stopped.
        //    CloseOutputAsync is a send; issuing it while the loop is wedged inside SendAsync
        //    would break the "one outstanding send" contract and corrupt the frame stream.
        if (sendLoopStopped)
        {
            await TryCloseOutputAsync(WebSocketCloseStatus.NormalClosure, Remaining(deadline)).ConfigureAwait(false);
            await WaitBoundedAsync(_receiveLoop, Remaining(deadline)).ConfigureAwait(false);
        }

        // 3. Abort is the last resort. After it both loops are guaranteed to unblock, so the
        //    waits below are unbounded on purpose: returning before they finish would leave a
        //    live send/receive racing the socket disposal.
        _lifetime.Cancel();
        try
        {
            _socket.Abort();
        }
        catch
        {
            // Aborting an already-dead socket is not actionable.
        }

        await AwaitLoopAsync(_sendLoop).ConfigureAwait(false);
        await AwaitLoopAsync(_receiveLoop).ConfigureAwait(false);
        await AwaitLoopAsync(_watchdog).ConfigureAwait(false);

        DrainQueuedFrames(new ObjectDisposedException(nameof(AhtolaHranaWebSocketConnection)));
        FailPending(Volatile.Read(ref _fault) ?? new ObjectDisposedException(nameof(AhtolaHranaWebSocketConnection)));
        _hello.TrySetException(new ObjectDisposedException(nameof(AhtolaHranaWebSocketConnection)));
        _ = _hello.Task.Exception;

        _socket.Dispose();
        _lifetime.Dispose();
    }

    private static TimeSpan Remaining(long deadlineTicks)
    {
        var remaining = deadlineTicks - Environment.TickCount64;
        return remaining <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(remaining);
    }

    private void Start()
    {
        // The send loop is assigned first so a receive-loop protocol failure can always
        // drain it before writing a close frame (never two concurrent sends).
        _sendLoop = Task.Run(RunSendLoopAsync);
        _receiveLoop = Task.Run(RunReceiveLoopAsync);
        _watchdog = StartWatchdog();
    }

    private async Task HandshakeAsync(string? authToken, CancellationToken cancellationToken)
    {
        var hello = new HranaHelloMsg { Jwt = string.IsNullOrWhiteSpace(authToken) ? null : authToken };
        await SendMessageAsync(
                JsonSerializer.SerializeToUtf8Bytes(hello, AhtolaHranaJsonContext.Default.HranaHelloMsg),
                cancellationToken)
            .ConfigureAwait(false);
        await _hello.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ThrowIfFaulted()
    {
        if (Volatile.Read(ref _fault) is { } fault)
            throw new AhtolaException($"The Hrana WebSocket connection is no longer usable: {fault.Message}");
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private void AbandonRequest(int requestId, PendingRequest pending, Func<Task>? orphanCompensation)
    {
        if (orphanCompensation is not null)
        {
            // Keep the correlation slot: the response is still owed and the handle it creates
            // must be closed. The slot is released by the continuation, or by generation
            // teardown, whichever happens first.
            pending.CompensateWhenAnswered(this, requestId, orphanCompensation);
            return;
        }

        // Tombstone before removing: the receive loop may be completing this very id, and a
        // tombstone for an id that was in fact answered is harmless (ids are never reused).
        // The reverse order would race — the receive loop could miss the slot and then miss
        // the tombstone, and abort a healthy generation as "unknown request id".
        MarkCancelled(requestId);
        _pending.TryRemove(requestId, out _);
        if (!pending.Completion.TrySetCanceled())
        {
            // Something already completed the slot (for example MarkCancelled crossing the
            // tombstone ceiling and faulting the generation). Observe any exception so an
            // abandoned request never surfaces as an unobserved task exception.
            _ = pending.Completion.Task.Exception;
        }
    }

    /// <summary>
    /// Records that a late response for <paramref name="requestId"/> must be discarded rather
    /// than treated as a multiplexing failure.
    /// </summary>
    /// <remarks>
    /// The set is scoped to the generation and is never evicted while the generation lives.
    /// Evicting the oldest entries — the obvious way to bound it — is unsound: an abandoned
    /// request can be answered arbitrarily late, and dropping its tombstone first turns a
    /// perfectly valid reply into an "unknown request id" abort that kills healthy
    /// connections. When the set grows past the configured ceiling the generation is retired
    /// instead, so the bound is enforced by closing the connection, never by forgetting.
    /// </remarks>
    private void MarkCancelled(int requestId)
    {
        bool exhausted;
        lock (_cancelledGate)
        {
            if (!_cancelledIds.Add(requestId))
                return;
            exhausted = _cancelledIds.Count > _options.MaxCancelledRequestTombstones;
        }

        if (exhausted)
        {
            FaultGeneration(new AhtolaException(
                "The Hrana WebSocket generation abandoned more than "
                + $"{_options.MaxCancelledRequestTombstones.ToString(System.Globalization.CultureInfo.InvariantCulture)} "
                + "requests without the server answering them, so it was retired rather than risk "
                + "misreading a late reply as a protocol violation."));
        }
    }

    private bool WasCancelled(int requestId)
    {
        lock (_cancelledGate)
            return _cancelledIds.Contains(requestId);
    }

    private async Task SendMessageAsync(
        byte[] payload,
        CancellationToken cancellationToken,
        Action<bool>? reportQueued = null)
    {
        if (payload.Length > _options.MaxMessageBytes)
        {
            throw new AhtolaException(
                $"The Hrana request message is {payload.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)} bytes, "
                + $"which exceeds the configured Ws Max Message Bytes of {_options.MaxMessageBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");
        }

        var frame = new OutboundFrame(payload);
        try
        {
            await _outbound.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new AhtolaException(
                $"The Hrana WebSocket connection is no longer usable: {Volatile.Read(ref _fault)?.Message ?? "the connection was closed."}");
        }

        // Only past this point can the send loop pick the frame up, so only past this point
        // can the request reach the server.
        reportQueued?.Invoke(true);
        await frame.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunSendLoopAsync()
    {
        try
        {
            while (await _outbound.Reader.WaitToReadAsync(_lifetime.Token).ConfigureAwait(false))
            {
                while (_outbound.Reader.TryRead(out var frame))
                {
                    try
                    {
                        await SendFrameAsync(frame).ConfigureAwait(false);
                        frame.Completion.TrySetResult();
                    }
                    catch (Exception exception)
                    {
                        frame.Fail(exception);
                        throw;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            DrainQueuedFrames(new ObjectDisposedException(nameof(AhtolaHranaWebSocketConnection)));
        }
        catch (ChannelClosedException)
        {
        }
        catch (Exception exception)
        {
            FaultGeneration(exception);
        }
    }

    private async Task SendFrameAsync(OutboundFrame frame)
    {
        var payload = frame.Payload;
        var offset = 0;
        do
        {
            var count = Math.Min(SendChunkBytes, payload.Length - offset);
            var endOfMessage = offset + count >= payload.Length;
            await _socket
                .SendAsync(
                    payload.AsMemory(offset, count),
                    WebSocketMessageType.Text,
                    endOfMessage,
                    _lifetime.Token)
                .ConfigureAwait(false);
            offset += count;
        }
        while (offset < payload.Length);
    }

    private async Task RunReceiveLoopAsync()
    {
        var chunk = ArrayPool<byte>.Shared.Rent(ReceiveChunkBytes);
        byte[]? assembly = null;
        var assembled = 0;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var result = await _socket
                    .ReceiveAsync(chunk.AsMemory(), _lifetime.Token)
                    .ConfigureAwait(false);

                // Any inbound frame — data, fragment or close — proves the peer is alive.
                Volatile.Write(ref _lastReceiveTicks, Environment.TickCount64);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    FaultGeneration(new AhtolaException(
                        $"The Hrana WebSocket connection was closed by the server ({_socket.CloseStatus?.ToString() ?? "no status"}"
                        + $"{(string.IsNullOrEmpty(_socket.CloseStatusDescription) ? string.Empty : ": " + _socket.CloseStatusDescription)})."));
                    return;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    await FailProtocolAsync(
                            WebSocketCloseStatus.InvalidMessageType,
                            "The Hrana server sent a binary frame on a JSON subprotocol; JSON encodings must use text frames.")
                        .ConfigureAwait(false);
                    return;
                }

                if (assembled + result.Count > _options.MaxMessageBytes)
                {
                    await FailProtocolAsync(
                            WebSocketCloseStatus.MessageTooBig,
                            $"A Hrana server message exceeded the configured Ws Max Message Bytes of "
                            + $"{_options.MaxMessageBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)}.")
                        .ConfigureAwait(false);
                    return;
                }

                if (!result.EndOfMessage || assembled > 0)
                {
                    Append(ref assembly, ref assembled, chunk.AsSpan(0, result.Count));
                    if (!result.EndOfMessage)
                        continue;
                }

                var payload = assembled > 0
                    ? assembly.AsMemory(0, assembled)
                    : chunk.AsMemory(0, result.Count);
                var dispatched = await DispatchAsync(payload).ConfigureAwait(false);
                assembled = 0;
                if (!dispatched)
                    return;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            FaultGeneration(exception);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
            if (assembly is not null)
                ArrayPool<byte>.Shared.Return(assembly);
        }
    }

    /// <summary>
    /// Application-level half-open detection, armed only by
    /// <see cref="AhtolaHranaWebSocketOptions.HalfOpenTimeout"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On net8.0 <c>ClientWebSocket</c> can send unsolicited keep-alive frames but has no
    /// <c>KeepAliveTimeout</c>, so a peer that stops responding without closing the TCP
    /// connection is invisible: the receive loop simply waits forever and every outstanding
    /// request hangs until its own timeout. This watchdog closes that hole without inventing
    /// protocol traffic — it never sends a ping or any Hrana message. It only observes that
    /// (a) requests are outstanding and (b) nothing at all has arrived for the configured
    /// budget, and then aborts the generation.
    /// </para>
    /// <para>
    /// It is opt-in because the signal is inherently ambiguous. Without ping/pong, "the peer
    /// has sent nothing" cannot distinguish a dead socket from a server that is simply busy —
    /// Hrana sends nothing between a request and its response — so any budget also caps how
    /// long one request may take. That is a deployment decision, not a default to impose, and
    /// it is deliberately <em>not</em> derived from the keep-alive settings: the runtime
    /// keep-alive on .NET 9+ is a ping/pong a busy server keeps answering, which is a
    /// fundamentally stronger signal than silence.
    /// </para>
    /// </remarks>
    private Task StartWatchdog()
    {
        if (_options.HalfOpenTimeout <= TimeSpan.Zero)
            return Task.CompletedTask;

        return Task.Run(RunWatchdogAsync);
    }

    private async Task RunWatchdogAsync()
    {
        var budgetMilliseconds = (long)_options.HalfOpenTimeout.TotalMilliseconds;
        var period = TimeSpan.FromMilliseconds(Math.Max(25d, budgetMilliseconds / 4d));

        try
        {
            using var timer = new PeriodicTimer(period);
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (Volatile.Read(ref _fault) is not null || Volatile.Read(ref _disposed) != 0)
                    return;

                // Two conditions, both necessary. Silence alone is not evidence: an idle
                // connection is silent by definition, and the request it sends next has had
                // no time to be answered. A long-waiting request alone is not evidence
                // either: the server may be answering other requests on other streams.
                var now = Environment.TickCount64;
                if (now - Volatile.Read(ref _lastReceiveTicks) < budgetMilliseconds)
                    continue;

                var oldestOutstanding = long.MaxValue;
                var outstanding = 0;
                foreach (var pending in _pending.Values)
                {
                    outstanding++;
                    if (pending.CreatedTicks < oldestOutstanding)
                        oldestOutstanding = pending.CreatedTicks;
                }

                if (outstanding == 0)
                    continue;

                var waiting = now - oldestOutstanding;
                if (waiting < budgetMilliseconds)
                    continue;

                FaultGeneration(new AhtolaException(
                    $"The Hrana WebSocket peer sent nothing for {waiting.ToString(System.Globalization.CultureInfo.InvariantCulture)}ms "
                    + $"while {outstanding.ToString(System.Globalization.CultureInfo.InvariantCulture)} request(s) were "
                    + "outstanding, which exceeds the configured Ws Half Open Timeout of "
                    + $"{budgetMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}ms. The connection was "
                    + "treated as half-open and aborted; nothing was replayed."));
                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void Append(ref byte[]? assembly, ref int assembled, ReadOnlySpan<byte> source)
    {
        var required = assembled + source.Length;
        if (assembly is null || assembly.Length < required)
        {
            var grown = ArrayPool<byte>.Shared.Rent(Math.Max(required, ReceiveChunkBytes * 2));
            if (assembly is not null)
            {
                assembly.AsSpan(0, assembled).CopyTo(grown);
                ArrayPool<byte>.Shared.Return(assembly);
            }
            assembly = grown;
        }

        source.CopyTo(assembly.AsSpan(assembled));
        assembled = required;
    }

    /// <summary>Returns false when the generation was terminated while dispatching.</summary>
    private async Task<bool> DispatchAsync(ReadOnlyMemory<byte> payload)
    {
        HranaServerMsg? message;
        try
        {
            message = JsonSerializer.Deserialize(payload.Span, AhtolaHranaJsonContext.Default.HranaServerMsg);
        }
        catch (JsonException exception)
        {
            await FailProtocolAsync(
                    WebSocketCloseStatus.ProtocolError,
                    $"Unable to parse a Hrana server message: {exception.Message}")
                .ConfigureAwait(false);
            return false;
        }

        if (message is null)
        {
            await FailProtocolAsync(WebSocketCloseStatus.ProtocolError, "The Hrana server sent an empty message.")
                .ConfigureAwait(false);
            return false;
        }

        switch (message.Type)
        {
            case AhtolaHranaWireProtocol.HelloOkType:
                if (Interlocked.Exchange(ref _helloSeen, 1) != 0)
                {
                    await FailProtocolAsync(
                            WebSocketCloseStatus.ProtocolError,
                            "The Hrana server sent more than one hello response.")
                        .ConfigureAwait(false);
                    return false;
                }
                _hello.TrySetResult(true);
                return true;

            case AhtolaHranaWireProtocol.HelloErrorType:
                {
                    if (AhtolaHranaResponseContract.ValidateError(message.Error, "hello_error") is { } helloViolation)
                    {
                        await FailProtocolAsync(WebSocketCloseStatus.ProtocolError, helloViolation).ConfigureAwait(false);
                        return false;
                    }

                    Interlocked.Exchange(ref _helloSeen, 1);
                    var error = AhtolaRemoteClient.CreateHranaError(message.Error);
                    _hello.TrySetException(error);
                    FaultGeneration(error);
                    return false;
                }

            case AhtolaHranaWireProtocol.ResponseOkType:
                {
                    if (RequireRequestId(message, AhtolaHranaWireProtocol.ResponseOkType) is { } okViolation)
                    {
                        await FailProtocolAsync(WebSocketCloseStatus.ProtocolError, okViolation).ConfigureAwait(false);
                        return false;
                    }

                    var okId = message.RequestId!.Value;
                    if (message.Response is not { } response)
                    {
                        await FailProtocolAsync(
                                WebSocketCloseStatus.ProtocolError,
                                $"The Hrana response_ok for request {okId.ToString(System.Globalization.CultureInfo.InvariantCulture)} did not include a response.")
                            .ConfigureAwait(false);
                        return false;
                    }

                    return await CompleteOkAsync(okId, response).ConfigureAwait(false);
                }

            case AhtolaHranaWireProtocol.ResponseErrorType:
                {
                    if (RequireRequestId(message, AhtolaHranaWireProtocol.ResponseErrorType) is { } errorViolation)
                    {
                        await FailProtocolAsync(WebSocketCloseStatus.ProtocolError, errorViolation).ConfigureAwait(false);
                        return false;
                    }

                    var errorId = message.RequestId!.Value;
                    if (AhtolaHranaResponseContract.ValidateError(
                            message.Error,
                            $"response_error for request {errorId.ToString(System.Globalization.CultureInfo.InvariantCulture)}")
                        is { } payloadViolation)
                    {
                        await FailProtocolAsync(WebSocketCloseStatus.ProtocolError, payloadViolation).ConfigureAwait(false);
                        return false;
                    }

                    var error = AhtolaRemoteClient.CreateHranaError(message.Error);
                    return await CompleteAsync(errorId, pending => pending.Completion.TrySetException(error))
                        .ConfigureAwait(false);
                }

            default:
                await FailProtocolAsync(
                        WebSocketCloseStatus.ProtocolError,
                        $"The Hrana server sent an unknown message type '{message.Type}'.")
                    .ConfigureAwait(false);
                return false;
        }
    }

    private static string? RequireRequestId(HranaServerMsg message, string messageType)
    {
        if (message.RequestId is not { } requestId)
            return $"A Hrana {messageType} message did not include a request_id.";
        if (requestId < 0)
        {
            return $"A Hrana {messageType} message carried the negative request_id "
                + $"{requestId.ToString(System.Globalization.CultureInfo.InvariantCulture)}.";
        }

        return null;
    }

    private async Task<bool> CompleteOkAsync(int requestId, HranaResponse response)
    {
        if (_pending.TryRemove(requestId, out var pending))
        {
            if (AhtolaHranaResponseContract.Validate(pending.RequestType, response) is { } violation)
            {
                // The caller must not read a malformed payload as data, and the generation
                // must not continue: a server that broke the response contract once cannot be
                // trusted to keep the id correlation straight either.
                pending.Completion.TrySetException(new AhtolaHranaProtocolException(violation));
                await FailProtocolAsync(WebSocketCloseStatus.ProtocolError, violation).ConfigureAwait(false);
                return false;
            }

            pending.Completion.TrySetResult(response);
            return true;
        }

        // A response for a request the caller abandoned is expected and intentionally
        // discarded; a response for an id that was never issued means multiplexing broke.
        if (WasCancelled(requestId))
            return true;

        await FailProtocolAsync(
                WebSocketCloseStatus.ProtocolError,
                $"The Hrana server answered unknown request id {requestId.ToString(System.Globalization.CultureInfo.InvariantCulture)}.")
            .ConfigureAwait(false);
        return false;
    }

    private async Task<bool> CompleteAsync(int requestId, Action<PendingRequest> complete)
    {
        if (_pending.TryRemove(requestId, out var pending))
        {
            complete(pending);
            return true;
        }

        if (WasCancelled(requestId))
            return true;

        await FailProtocolAsync(
                WebSocketCloseStatus.ProtocolError,
                $"The Hrana server answered unknown request id {requestId.ToString(System.Globalization.CultureInfo.InvariantCulture)}.")
            .ConfigureAwait(false);
        return false;
    }

    private async Task FailProtocolAsync(WebSocketCloseStatus status, string message)
    {
        var violation = new AhtolaHranaProtocolException(message);
        try
        {
            // Stop the send loop first: CloseOutputAsync is a send, and the WebSocket
            // contract allows only one outstanding send at a time. If the loop does not
            // actually stop (a wedged SendAsync against a dead peer) the close frame is
            // skipped entirely and FaultGeneration() aborts the socket instead.
            _outbound.Writer.TryComplete();
            var sendLoopStopped = await WaitBoundedAsync(_sendLoop, _options.CloseTimeout).ConfigureAwait(false);
            if (sendLoopStopped)
                await TryCloseOutputAsync(status, _options.CloseTimeout).ConfigureAwait(false);
        }
        catch
        {
            // Reporting the violation to the caller matters more than a clean close frame.
        }

        FaultGeneration(violation);
    }

    /// <summary>
    /// Issues the courtesy close frame at most once per generation.
    /// </summary>
    /// <remarks>
    /// A receive-loop protocol violation and a caller-initiated dispose can both reach this
    /// point concurrently. Both would have observed the send loop stopped, so without this
    /// guard two <c>CloseOutputAsync</c> calls could overlap — which is still two concurrent
    /// sends, and the runtime answers it by aborting the socket. The first caller wins and
    /// the second simply skips the frame.
    /// </remarks>
    private async Task TryCloseOutputAsync(WebSocketCloseStatus status, TimeSpan budget)
    {
        if (Interlocked.Exchange(ref _closeIssued, 1) != 0)
            return;

        try
        {
            if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
                return;

            using var closeCancellation = new CancellationTokenSource(budget <= TimeSpan.Zero ? TimeSpan.Zero : budget);
            await _socket
                .CloseOutputAsync(status, statusDescription: null, closeCancellation.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            // A failed close is never fatal: the caller aborts the socket next.
        }
    }

    private void FaultGeneration(Exception exception)
    {
        if (Interlocked.CompareExchange(ref _fault, exception, null) is not null)
            return;

        _outbound.Writer.TryComplete();
        _hello.TrySetException(exception);
        _ = _hello.Task.Exception;
        FailPending(exception);
        DrainQueuedFrames(exception);
        try
        {
            _socket.Abort();
        }
        catch
        {
            // The socket is already unusable; nothing further to do.
        }
    }

    private void FailPending(Exception exception)
    {
        foreach (var requestId in _pending.Keys)
        {
            if (_pending.TryRemove(requestId, out var pending))
                pending.Completion.TrySetException(exception);
        }
    }

    private void DrainQueuedFrames(Exception exception)
    {
        while (_outbound.Reader.TryRead(out var frame))
            frame.Fail(exception);
    }

    private async Task RunCompensationAsync(int requestId, Func<Task> compensation)
    {
        if (!IsAlive)
            return;

        try
        {
            await compensation().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (IsAlive)
            {
                RetireForOrphanedHandle(
                    "the handle created by abandoned request "
                    + $"{requestId.ToString(System.Globalization.CultureInfo.InvariantCulture)} could not be closed "
                    + $"({exception.Message})");
            }
        }
    }

    /// <summary>
    /// Releases the server-side handle that a rejected <c>open_stream</c>/<c>open_cursor</c>
    /// may still have created.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>response_error</c> answers the request but says nothing about whether the handle
    /// was minted before the failure was raised, so the reserved id cannot simply be dropped.
    /// Any answer to the compensating close — including an application error such as "no such
    /// stream" — proves the server no longer holds the handle, so it counts as compensated.
    /// </para>
    /// <para>
    /// Only a close that cannot be answered leaves the handle in doubt; then the generation is
    /// retired, because the server reclaims every handle when the connection ends.
    /// </para>
    /// </remarks>
    private async Task CompensateRejectedHandleAsync(int requestId, Func<Task> compensation)
    {
        if (!IsAlive)
            return;

        try
        {
            await compensation().ConfigureAwait(false);
        }
        catch (AhtolaHranaProtocolException)
        {
            // A protocol violation already faulted the generation, which reclaims the handle.
        }
        catch (Exception exception) when (exception is AhtolaException && IsAlive)
        {
            // The server answered the close with an application error, which is proof enough
            // that it is not holding the handle the rejected request reserved.
        }
        catch (Exception exception)
        {
            if (IsAlive)
            {
                RetireForOrphanedHandle(
                    "the handle possibly created by rejected request "
                    + $"{requestId.ToString(System.Globalization.CultureInfo.InvariantCulture)} could not be closed "
                    + $"({exception.Message})");
            }
        }
    }

    /// <summary>
    /// Waits for a loop, reporting whether it actually finished inside the budget. Callers
    /// must treat <c>false</c> as "the loop is still running" and never touch the socket
    /// until it has been aborted.
    /// </summary>
    private static async Task<bool> WaitBoundedAsync(Task task, TimeSpan budget)
    {
        if (task.IsCompleted)
            return true;
        if (budget <= TimeSpan.Zero)
            return false;

        try
        {
            await task.WaitAsync(budget).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch
        {
            // Loop failures are already reflected in the generation fault; the loop is done.
            return true;
        }
    }

    private static async Task AwaitLoopAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // Loop failures are already reflected in the generation fault.
        }
    }

    private sealed class PendingRequest(string requestType)
    {
        /// <summary>The request type this correlation slot expects; drives response validation.</summary>
        public string RequestType { get; } = requestType;

        /// <summary>
        /// When this request started waiting. The half-open watchdog needs it: silence only
        /// counts against a request that has actually been outstanding for the whole budget,
        /// otherwise an idle connection would fail the very next request it sends.
        /// </summary>
        public long CreatedTicks { get; } = Environment.TickCount64;

        public TaskCompletionSource<HranaResponse> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Keeps the slot alive after the caller walked away and releases the server-side
        /// handle if the request turns out to have succeeded.
        /// </summary>
        public void CompensateWhenAnswered(AhtolaHranaWebSocketConnection owner, int requestId, Func<Task> compensation)
        {
            _ = Completion.Task.ContinueWith(
                static (task, state) =>
                {
                    var (owner, requestId, compensation) = ((AhtolaHranaWebSocketConnection, int, Func<Task>))state!;
                    owner._pending.TryRemove(requestId, out _);
                    owner.MarkCancelled(requestId);

                    if (!task.IsCompletedSuccessfully)
                    {
                        // The request failed or the generation died: no handle exists, and the
                        // exception is observed here so it never surfaces as unobserved.
                        _ = task.Exception;
                        return Task.CompletedTask;
                    }

                    return owner.RunCompensationAsync(requestId, compensation);
                },
                (owner, requestId, compensation),
                CancellationToken.None,
                TaskContinuationOptions.DenyChildAttach,
                TaskScheduler.Default);
        }
    }

    private sealed class OutboundFrame(byte[] payload)
    {
        public byte[] Payload { get; } = payload;

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Fails the frame and observes the exception so an abandoned send (the caller was
        /// cancelled while the frame was still queued) never surfaces as an unobserved
        /// task exception.
        /// </summary>
        public void Fail(Exception exception)
        {
            Completion.TrySetException(exception);
            _ = Completion.Task.Exception;
        }
    }
}
