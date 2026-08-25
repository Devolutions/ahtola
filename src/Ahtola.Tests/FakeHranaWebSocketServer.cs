using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Ahtola.Tests;

/// <summary>
/// An in-memory <see cref="WebSocket"/> that a test-owned server loop drives. Built by
/// subclassing the abstract BCL type, so no mocking framework or reflection is involved.
/// </summary>
internal sealed class FakeWebSocket : WebSocket
{
    private readonly Channel<InboundFrame> _inbound = Channel.CreateUnbounded<InboundFrame>();
    private readonly Func<FakeWebSocket, byte[], CancellationToken, Task> _onClientMessage;
    private readonly List<byte> _partialOutbound = [];
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly TaskCompletionSource _unblockSends = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private WebSocketState _state = WebSocketState.Open;
    private InboundFrame? _current;
    private int _currentOffset;

    public FakeWebSocket(string? subProtocol, Func<FakeWebSocket, byte[], CancellationToken, Task> onClientMessage)
    {
        SubProtocol = subProtocol;
        _onClientMessage = onClientMessage;
    }

    public override WebSocketCloseStatus? CloseStatus { get; }

    public override string? CloseStatusDescription { get; }

    public override string? SubProtocol { get; }

    public override WebSocketState State => _state;

    /// <summary>Set when more than one <c>SendAsync</c> was in flight (must never happen).</summary>
    public bool SawConcurrentSend { get; private set; }

    /// <summary>Set when more than one <c>ReceiveAsync</c> was in flight (must never happen).</summary>
    public bool SawConcurrentReceive { get; private set; }

    /// <summary>
    /// Set when <c>CloseOutputAsync</c> was issued while a <c>SendAsync</c> was still in
    /// flight. <c>CloseOutputAsync</c> is itself a send, so this is a WebSocket contract
    /// violation and must never happen even against a wedged peer.
    /// </summary>
    public bool SawCloseOutputDuringSend { get; private set; }

    /// <summary>
    /// Sends beyond this count block until the socket is aborted or disposed, simulating a
    /// half-open peer whose TCP window never opens again.
    /// </summary>
    public int BlockSendsAfter { get; set; } = int.MaxValue;

    private int _concurrentSends;
    private int _concurrentReceives;

    /// <summary>True while the client has a receive pending (required for keep-alive processing).</summary>
    public bool HasOutstandingReceive => Volatile.Read(ref _concurrentReceives) > 0;

    /// <summary>Number of <c>SendAsync</c> calls (fragments) the client issued.</summary>
    public int SendCallCount { get; private set; }

    public bool Aborted { get; private set; }

    public bool Disposed { get; private set; }

    public bool CloseOutputSent { get; private set; }

    public WebSocketCloseStatus? SentCloseStatus { get; private set; }

    /// <summary>Queues a complete text message from the server to the client.</summary>
    public void Push(string json, int fragmentSize = 0)
        => _inbound.Writer.TryWrite(new InboundFrame(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, fragmentSize));

    /// <summary>Queues a binary frame (a protocol violation for JSON subprotocols).</summary>
    public void PushBinary(byte[] payload)
        => _inbound.Writer.TryWrite(new InboundFrame(payload, WebSocketMessageType.Binary, 0));

    /// <summary>Queues a close frame from the server.</summary>
    public void PushClose()
        => _inbound.Writer.TryWrite(new InboundFrame([], WebSocketMessageType.Close, 0));

    public override void Abort()
    {
        Aborted = true;
        _state = WebSocketState.Aborted;
        _unblockSends.TrySetResult();
        _inbound.Writer.TryComplete();
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        => CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _concurrentSends) > 0)
            SawCloseOutputDuringSend = true;

        CloseOutputSent = true;
        SentCloseStatus = closeStatus;
        _state = WebSocketState.CloseSent;
        _inbound.Writer.TryWrite(new InboundFrame([], WebSocketMessageType.Close, 0));
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        Disposed = true;
        _state = WebSocketState.Closed;
        _unblockSends.TrySetResult();
        _inbound.Writer.TryComplete();
        _sendGate.Dispose();
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        var result = await ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        return new WebSocketReceiveResult(result.Count, result.MessageType, result.EndOfMessage);
    }

    public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var concurrent = Interlocked.Increment(ref _concurrentReceives);
        if (concurrent > 1)
            SawConcurrentReceive = true;
        try
        {
            while (_current is null)
            {
                if (!await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
                if (_inbound.Reader.TryRead(out var frame))
                {
                    _current = frame;
                    _currentOffset = 0;
                }
            }

            var current = _current;
            if (current.MessageType == WebSocketMessageType.Close)
            {
                _current = null;
                _state = WebSocketState.CloseReceived;
                return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }

            var remaining = current.Payload.Length - _currentOffset;
            var chunk = current.FragmentSize > 0
                ? Math.Min(current.FragmentSize, remaining)
                : remaining;
            chunk = Math.Min(chunk, buffer.Length);
            current.Payload.AsSpan(_currentOffset, chunk).CopyTo(buffer.Span);
            _currentOffset += chunk;
            var endOfMessage = _currentOffset >= current.Payload.Length;
            if (endOfMessage)
                _current = null;

            return new ValueWebSocketReceiveResult(chunk, current.MessageType, endOfMessage);
        }
        finally
        {
            Interlocked.Decrement(ref _concurrentReceives);
        }
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        => SendAsync(buffer.AsMemory(), messageType, endOfMessage, cancellationToken).AsTask();

    public override async ValueTask SendAsync(
        ReadOnlyMemory<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken)
    {
        var concurrent = Interlocked.Increment(ref _concurrentSends);
        if (concurrent > 1)
            SawConcurrentSend = true;
        SendCallCount++;
        try
        {
            if (SendCallCount > BlockSendsAfter)
            {
                // A half-open peer: the send never completes until the client gives up and
                // aborts the socket. Anything the client does to the socket while we are
                // parked here is a contract violation it must not commit.
                await _unblockSends.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
            }

            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            byte[]? message = null;
            try
            {
                _partialOutbound.AddRange(buffer.ToArray());
                if (!endOfMessage)
                    return;

                message = [.. _partialOutbound];
                _partialOutbound.Clear();
            }
            finally
            {
                _sendGate.Release();
            }

            await _onClientMessage(this, message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _concurrentSends);
        }
    }

    private sealed class InboundFrame(byte[] payload, WebSocketMessageType messageType, int fragmentSize)
    {
        public byte[] Payload { get; } = payload;

        public WebSocketMessageType MessageType { get; } = messageType;

        public int FragmentSize { get; } = fragmentSize;
    }
}

/// <summary>
/// A scripted Hrana WebSocket server: parses the client's JSON messages and answers with
/// spec-shaped replies. Behaviour is tuned through the mutable properties so each test can
/// drive one specific protocol scenario.
/// </summary>
internal sealed class FakeHranaServer : IAhtolaWebSocketConnector
{
    private readonly List<string> _received = [];
    private readonly object _sync = new();
    private int _connectAttempts;

    /// <summary>Subprotocol echoed back on the upgrade; null means "no header echoed" (v1).</summary>
    public string? NegotiatedSubProtocol { get; set; } = "hrana3";

    /// <summary>Fails the first N connection attempts before succeeding.</summary>
    public int FailConnectAttempts { get; set; }

    /// <summary>Answers <c>hello</c> with <c>hello_error</c> instead of <c>hello_ok</c>.</summary>
    public string? HelloError { get; set; }

    /// <summary>Rows returned by <c>execute</c> and cursor steps.</summary>
    public List<string> Rows { get; } = ["1"];

    /// <summary>Entries per <c>fetch_cursor</c> page.</summary>
    public int CursorPageLimit { get; set; } = 2;

    /// <summary>Server-side outbound fragment size in bytes (0 = one frame per message).</summary>
    public int FragmentSize { get; set; }

    /// <summary>Delays the response for these request types, releasing them on demand.</summary>
    public HashSet<string> HoldRequestTypes { get; } = [];

    /// <summary>Replies to the held requests in reverse arrival order when released.</summary>
    public bool ReplyToHeldRequestsInReverseOrder { get; set; }

    /// <summary>Emits a response for an id the client never issued.</summary>
    public bool AnswerUnknownRequestId { get; set; }

    /// <summary>Emits an unknown top-level message discriminator.</summary>
    public bool SendUnknownDiscriminator { get; set; }

    /// <summary>Answers with a binary frame instead of text.</summary>
    public bool SendBinaryFrame { get; set; }

    /// <summary>Pads execute responses so the message exceeds a configured cap.</summary>
    public int PaddingBytes { get; set; }

    /// <summary>Fails the given request types with a Hrana <c>response_error</c>.</summary>
    public Dictionary<string, (string Code, string Message)> RequestErrors { get; } = [];

    /// <summary>
    /// Replaces the whole server message for a request type. The literal <c>ID</c> is
    /// substituted with the client's request id, so a test can inject a payload that violates
    /// the response contract without teaching the fake about every malformed shape.
    /// </summary>
    public Dictionary<string, string> RawResponses { get; } = [];

    /// <summary>Request types the server silently never answers (a half-open peer).</summary>
    public HashSet<string> DropRequestTypes { get; } = [];

    /// <summary>Blocks every socket send past this count, simulating a wedged peer.</summary>
    public int BlockSendsAfter { get; set; } = int.MaxValue;

    /// <summary>Closes the socket when the given request type arrives.</summary>
    public string? CloseOnRequestType { get; set; }

    public string? ObservedJwt { get; private set; }

    public FakeWebSocket? Socket { get; private set; }

    public List<FakeWebSocket> Sockets { get; } = [];

    public List<string> RequestTypes { get; } = [];

    public List<int> OpenedStreamIds { get; } = [];

    public List<int> ClosedStreamIds { get; } = [];

    public List<int> OpenedCursorIds { get; } = [];

    public List<int> ClosedCursorIds { get; } = [];

    public List<string> ExecutedSql { get; } = [];

    public List<string> OfferedSubProtocols { get; } = [];

    public int ConnectAttempts => Volatile.Read(ref _connectAttempts);

    public int FetchCursorCalls { get; private set; }

    /// <summary>Every raw client message the server observed, in arrival order.</summary>
    public IReadOnlyList<string> ReceivedMessages
    {
        get
        {
            lock (_sync)
                return [.. _received];
        }
    }

    public AhtolaHranaWebSocketOptions? ObservedOptions { get; private set; }

    private readonly List<HeldRequest> _held = [];
    private readonly Dictionary<int, CursorState> _cursors = [];

    public Task<WebSocket> ConnectAsync(
        Uri endpoint,
        IReadOnlyList<string> subProtocols,
        AhtolaHranaWebSocketOptions options,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _connectAttempts);
        ObservedOptions = options;
        ConnectedEndpoints.Add(endpoint);
        lock (_sync)
        {
            OfferedSubProtocols.Clear();
            OfferedSubProtocols.AddRange(subProtocols);
        }

        if (ConnectAttempts <= FailConnectAttempts)
            throw new WebSocketException(WebSocketError.Faulted, "scripted connect failure");

        var socket = new FakeWebSocket(NegotiatedSubProtocol, HandleClientMessageAsync)
        {
            BlockSendsAfter = BlockSendsAfter,
        };
        Socket = socket;
        Sockets.Add(socket);
        return Task.FromResult<WebSocket>(socket);
    }

    public List<Uri> ConnectedEndpoints { get; } = [];

    /// <summary>Releases every held request, optionally reversing the reply order.</summary>
    public void ReleaseHeldRequests()
    {
        List<HeldRequest> held;
        lock (_sync)
        {
            held = [.. _held];
            _held.Clear();
        }

        if (ReplyToHeldRequestsInReverseOrder)
            held.Reverse();
        foreach (var request in held)
            Send(request.Socket, request.Response);
    }

    public int HeldRequestCount
    {
        get
        {
            lock (_sync)
                return _held.Count;
        }
    }

    private async Task HandleClientMessageAsync(FakeWebSocket socket, byte[] message, CancellationToken cancellationToken)
    {
        await Task.Yield();
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;
        var type = root.GetProperty("type").GetString();

        lock (_sync)
            _received.Add(Encoding.UTF8.GetString(message));

        if (type == "hello")
        {
            ObservedJwt = root.TryGetProperty("jwt", out var jwt) && jwt.ValueKind == JsonValueKind.String
                ? jwt.GetString()
                : null;
            Send(socket, HelloError is null
                ? """{"type":"hello_ok"}"""
                : """{"type":"hello_error","error":{"message":MSG,"code":"AUTH_FAILED"}}""".Replace("MSG", JsonSerializer.Serialize(HelloError), StringComparison.Ordinal));
            return;
        }

        if (type != "request")
            throw new InvalidOperationException($"Unexpected client message type '{type}'.");

        var requestId = root.GetProperty("request_id").GetInt32();
        var request = root.GetProperty("request");
        var requestType = request.GetProperty("type").GetString()!;
        lock (_sync)
            RequestTypes.Add(requestType);

        if (SendUnknownDiscriminator)
        {
            Send(socket, """{"type":"totally_unknown"}""");
            return;
        }

        if (SendBinaryFrame)
        {
            socket.PushBinary(Encoding.UTF8.GetBytes("""{"type":"hello_ok"}"""));
            return;
        }

        if (AnswerUnknownRequestId)
        {
            Send(socket, Envelope(requestId + 9999, """{"type":"TYPE"}""".Replace("TYPE", requestType, StringComparison.Ordinal)));
            return;
        }

        if (CloseOnRequestType == requestType)
        {
            socket.PushClose();
            return;
        }

        if (DropRequestTypes.Contains(requestType))
            return;

        if (RawResponses.TryGetValue(requestType, out var raw))
        {
            Send(
                socket,
                raw.Replace("ID", requestId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
            return;
        }

        if (RequestErrors.TryGetValue(requestType, out var error))
        {
            var payload = """{"type":"response_error","request_id":ID,"error":{"message":MSG,"code":CODE}}"""
                .Replace("ID", requestId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("MSG", JsonSerializer.Serialize(error.Message), StringComparison.Ordinal)
                .Replace("CODE", JsonSerializer.Serialize(error.Code), StringComparison.Ordinal);
            Send(socket, payload);
            return;
        }

        var response = BuildResponse(requestId, requestType, request);
        if (HoldRequestTypes.Contains(requestType))
        {
            lock (_sync)
                _held.Add(new HeldRequest(socket, response));
            return;
        }

        Send(socket, response);
    }

    private string BuildResponse(int requestId, string requestType, JsonElement request)
    {
        switch (requestType)
        {
            case "open_stream":
                lock (_sync)
                    OpenedStreamIds.Add(request.GetProperty("stream_id").GetInt32());
                return Envelope(requestId, """{"type":"open_stream"}""");

            case "close_stream":
                lock (_sync)
                    ClosedStreamIds.Add(request.GetProperty("stream_id").GetInt32());
                return Envelope(requestId, """{"type":"close_stream"}""");

            case "execute":
                {
                    var sql = request.GetProperty("stmt").GetProperty("sql").GetString()!;
                    var streamId = request.GetProperty("stream_id").GetInt32();
                    lock (_sync)
                        ExecutedSql.Add(sql);
                    return Envelope(
                        requestId,
                        """{"type":"execute","result":RESULT}"""
                            .Replace("RESULT", StatementResult(streamId), StringComparison.Ordinal));
                }

            case "batch":
                {
                    var steps = request.GetProperty("batch").GetProperty("steps");
                    var results = new List<string>();
                    var errors = new List<string>();
                    foreach (var step in steps.EnumerateArray())
                    {
                        var sql = step.GetProperty("stmt").GetProperty("sql").GetString()!;
                        lock (_sync)
                            ExecutedSql.Add(sql);
                        results.Add(StatementResult());
                        errors.Add("null");
                    }

                    return Envelope(
                        requestId,
                        """{"type":"batch","result":{"step_results":[RESULTS],"step_errors":[ERRORS]}}""".Replace("RESULTS", string.Join(",", results), StringComparison.Ordinal).Replace("ERRORS", string.Join(",", errors), StringComparison.Ordinal));
                }

            case "open_cursor":
                {
                    var cursorId = request.GetProperty("cursor_id").GetInt32();
                    var sql = request.GetProperty("batch").GetProperty("steps")[0].GetProperty("stmt").GetProperty("sql").GetString()!;
                    lock (_sync)
                    {
                        ExecutedSql.Add(sql);
                        OpenedCursorIds.Add(cursorId);
                        _cursors[cursorId] = new CursorState();
                    }
                    return Envelope(requestId, """{"type":"open_cursor"}""");
                }

            case "fetch_cursor":
                {
                    var cursorId = request.GetProperty("cursor_id").GetInt32();
                    var maxCount = request.GetProperty("max_count").GetInt64();
                    FetchCursorCalls++;
                    var state = _cursors[cursorId];
                    var entries = new List<string>();
                    var budget = Math.Min(maxCount, CursorPageLimit);
                    var done = false;
                    while (entries.Count < budget)
                    {
                        if (!state.StepBegun)
                        {
                            state.StepBegun = true;
                            entries.Add("""{"type":"step_begin","step":0,"cols":[{"name":"value","decltype":"INTEGER"}]}""");
                            continue;
                        }

                        if (state.RowIndex < Rows.Count)
                        {
                            entries.Add($$"""{"type":"row","row":[{"type":"integer","value":"{{Rows[state.RowIndex]}}"}]}""");
                            state.RowIndex++;
                            continue;
                        }

                        if (!state.StepEnded)
                        {
                            state.StepEnded = true;
                            entries.Add("""{"type":"step_end","affected_row_count":0,"last_insert_rowid":null}""");
                            continue;
                        }

                        break;
                    }

                    if (state.StepEnded && state.RowIndex >= Rows.Count)
                        done = true;

                    return Envelope(
                        requestId,
                        $$"""{"type":"fetch_cursor","entries":[{{string.Join(",", entries)}}],"done":{{(done ? "true" : "false")}}}""");
                }

            case "close_cursor":
                {
                    var cursorId = request.GetProperty("cursor_id").GetInt32();
                    lock (_sync)
                    {
                        _cursors.Remove(cursorId);
                        ClosedCursorIds.Add(cursorId);
                    }
                    return Envelope(requestId, """{"type":"close_cursor"}""");
                }

            case "store_sql":
                return Envelope(requestId, """{"type":"store_sql"}""");

            case "close_sql":
                return Envelope(requestId, """{"type":"close_sql"}""");

            case "sequence":
                return Envelope(requestId, """{"type":"sequence"}""");

            case "describe":
                return Envelope(
                    requestId,
                    """{"type":"describe","result":{"params":[{"name":":id"}],"cols":[{"name":"value","decltype":"INTEGER"}],"is_explain":false,"is_readonly":true}}""");

            case "get_autocommit":
                return Envelope(requestId, """{"type":"get_autocommit","is_autocommit":true}""");

            default:
                throw new InvalidOperationException($"Unhandled Hrana request type '{requestType}'.");
        }
    }

    private string StatementResult(int lastInsertRowId = 7)
    {
        var rows = string.Join(
            ",",
            Rows.Select(row => """[{"type":"integer","value":"ROW"}]""".Replace("ROW", row, StringComparison.Ordinal)));
        var padding = PaddingBytes > 0
            ? ",\"padding\":\"" + new string('x', PaddingBytes) + "\""
            : string.Empty;
        return """
            {"cols":[{"name":"value","decltype":"INTEGER"}],"rows":[ROWS],"affected_row_count":0,"last_insert_rowid":"ROWID"PADDING}
            """
            .Replace("ROWS", rows, StringComparison.Ordinal)
            .Replace("ROWID", lastInsertRowId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("PADDING", padding, StringComparison.Ordinal);
    }

    private static string Envelope(int requestId, string response)
        => $$"""{"type":"response_ok","request_id":{{requestId}},"response":{{response}}}""";

    private void Send(FakeWebSocket socket, string payload) => socket.Push(payload, FragmentSize);

    private sealed record HeldRequest(FakeWebSocket Socket, string Response);

    private sealed class CursorState
    {
        public bool StepBegun { get; set; }

        public int RowIndex { get; set; }

        public bool StepEnded { get; set; }
    }
}
