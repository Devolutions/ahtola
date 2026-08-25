using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ahtola;

/// <summary>
/// Hrana WebSocket wire messages, transcribed from the authoritative
/// <c>tursodatabase/libsql</c> specs (<c>docs/HRANA_1_SPEC.md</c>,
/// <c>docs/HRANA_2_SPEC.md</c>, <c>docs/HRANA_3_SPEC.md</c>) and the reference
/// server implementation in <c>libsql-server/src/hrana/ws/</c>.
/// </summary>
/// <remarks>
/// <para>
/// These envelopes are deliberately distinct from the HTTP pipeline envelopes in
/// <see cref="AhtolaRemoteClient"/>: the HTTP variant threads state through an opaque
/// <c>baton</c>, while the WebSocket variant multiplexes client-assigned
/// <c>stream_id</c>/<c>cursor_id</c>/<c>sql_id</c> handles over one persistent
/// connection. Only the payload structures (<see cref="RemoteStatement"/>,
/// <see cref="RemoteBatch"/>, <see cref="RemoteStatementResult"/>,
/// <see cref="RemoteBatchResult"/>, <see cref="RemoteCursorEntry"/>,
/// <see cref="RemoteError"/>) are shared.
/// </para>
/// <para>
/// Unknown JSON fields are ignored (HRANA_3_SPEC "unknown fields must be ignored"),
/// while unknown <c>type</c> discriminators are protocol errors that terminate the
/// connection.
/// </para>
/// </remarks>
internal static class AhtolaHranaWireProtocol
{
    /// <summary>JSON subprotocols offered on the upgrade, highest preference first.</summary>
    /// <remarks>
    /// <c>hrana3-protobuf</c> is deliberately never offered: this client only speaks the
    /// JSON encoding, and accepting a Protobuf subprotocol would bind the connection to
    /// binary frames it cannot decode.
    /// </remarks>
    internal static readonly string[] JsonSubProtocols = ["hrana3", "hrana2", "hrana1"];

    internal const string HelloType = "hello";
    internal const string RequestType = "request";
    internal const string HelloOkType = "hello_ok";
    internal const string HelloErrorType = "hello_error";
    internal const string ResponseOkType = "response_ok";
    internal const string ResponseErrorType = "response_error";

    /// <summary>
    /// Maps a negotiated <c>Sec-WebSocket-Protocol</c> value to a Hrana version.
    /// An absent/empty value means the server did not echo a subprotocol, which per
    /// RFC 6455 and <c>handshake.rs</c> means it assumed Hrana 1.
    /// </summary>
    internal static int NegotiateVersion(string? subProtocol)
    {
        return subProtocol switch
        {
            null or "" => 1,
            "hrana1" => 1,
            "hrana2" => 2,
            "hrana3" => 3,
            _ => throw new AhtolaHranaProtocolException(
                $"The Hrana server negotiated an unsupported WebSocket subprotocol '{subProtocol}'. "
                + "This client offers only the JSON encodings hrana3, hrana2 and hrana1."),
        };
    }
}

/// <summary>A Hrana protocol violation. Always fatal to the WebSocket generation.</summary>
internal sealed class AhtolaHranaProtocolException : AhtolaException
{
    public AhtolaHranaProtocolException(string message)
        : base(message)
    {
    }
}

/// <summary><c>{"type":"hello","jwt":...}</c>.</summary>
internal sealed class HranaHelloMsg
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = AhtolaHranaWireProtocol.HelloType;

    /// <summary>
    /// Written even when null: the spec models the field as <c>string | null</c>, not as
    /// an optional field.
    /// </summary>
    [JsonPropertyName("jwt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Jwt { get; init; }
}

/// <summary><c>{"type":"request","request_id":N,"request":{...}}</c>.</summary>
internal sealed class HranaRequestMsg
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = AhtolaHranaWireProtocol.RequestType;

    [JsonPropertyName("request_id")]
    public int RequestId { get; init; }

    [JsonPropertyName("request")]
    public HranaRequest Request { get; init; } = new();
}

/// <summary>
/// The Hrana request union. Every variant is <c>{"type": ...}</c> plus the fields listed
/// in HRANA_3_SPEC "Requests"; absent fields are omitted from the JSON.
/// </summary>
internal sealed class HranaRequest
{
    public const string OpenStream = "open_stream";
    public const string CloseStream = "close_stream";
    public const string Execute = "execute";
    public const string BatchRequest = "batch";
    public const string StoreSql = "store_sql";
    public const string CloseSql = "close_sql";
    public const string Sequence = "sequence";
    public const string Describe = "describe";
    public const string OpenCursor = "open_cursor";
    public const string CloseCursor = "close_cursor";
    public const string FetchCursor = "fetch_cursor";
    public const string GetAutocommit = "get_autocommit";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("stream_id")]
    public int? StreamId { get; init; }

    [JsonPropertyName("cursor_id")]
    public int? CursorId { get; init; }

    [JsonPropertyName("sql_id")]
    public int? SqlId { get; init; }

    [JsonPropertyName("sql")]
    public string? Sql { get; init; }

    [JsonPropertyName("stmt")]
    public RemoteStatement? Statement { get; init; }

    [JsonPropertyName("batch")]
    public RemoteBatch? Batch { get; init; }

    [JsonPropertyName("max_count")]
    public long? MaxCount { get; init; }

    /// <summary>Minimum Hrana version that accepts the given request type.</summary>
    public static int MinimumVersion(string type)
        => type switch
        {
            OpenStream or CloseStream or Execute or BatchRequest => 1,
            StoreSql or CloseSql or Sequence or Describe => 2,
            OpenCursor or CloseCursor or FetchCursor or GetAutocommit => 3,
            _ => throw new AhtolaHranaProtocolException($"Unknown Hrana request type '{type}'."),
        };

    public static HranaRequest ForOpenStream(int streamId)
        => new() { Type = OpenStream, StreamId = streamId };

    public static HranaRequest ForCloseStream(int streamId)
        => new() { Type = CloseStream, StreamId = streamId };

    public static HranaRequest ForExecute(int streamId, RemoteStatement statement)
        => new() { Type = Execute, StreamId = streamId, Statement = statement };

    public static HranaRequest ForBatch(int streamId, RemoteBatch batch)
        => new() { Type = BatchRequest, StreamId = streamId, Batch = batch };

    public static HranaRequest ForStoreSql(int sqlId, string sql)
        => new() { Type = StoreSql, SqlId = sqlId, Sql = sql };

    public static HranaRequest ForCloseSql(int sqlId)
        => new() { Type = CloseSql, SqlId = sqlId };

    public static HranaRequest ForSequence(int streamId, string? sql, int? sqlId)
        => new() { Type = Sequence, StreamId = streamId, Sql = sql, SqlId = sqlId };

    public static HranaRequest ForDescribe(int streamId, string? sql, int? sqlId)
        => new() { Type = Describe, StreamId = streamId, Sql = sql, SqlId = sqlId };

    public static HranaRequest ForOpenCursor(int streamId, int cursorId, RemoteBatch batch)
        => new() { Type = OpenCursor, StreamId = streamId, CursorId = cursorId, Batch = batch };

    public static HranaRequest ForFetchCursor(int cursorId, long maxCount)
        => new() { Type = FetchCursor, CursorId = cursorId, MaxCount = maxCount };

    public static HranaRequest ForCloseCursor(int cursorId)
        => new() { Type = CloseCursor, CursorId = cursorId };

    public static HranaRequest ForGetAutocommit(int streamId)
        => new() { Type = GetAutocommit, StreamId = streamId };
}

/// <summary>
/// Minimum Hrana version required by a <c>BatchCond</c> tree.
/// </summary>
/// <remarks>
/// <para>
/// HRANA_1_SPEC/HRANA_2_SPEC define <c>ok</c>, <c>error</c>, <c>not</c>, <c>and</c> and
/// <c>or</c>; HRANA_3_SPEC added <c>is_autocommit</c>. A server negotiated at version 1 or
/// 2 has no way to evaluate <c>is_autocommit</c>, so sending it would either be rejected or
/// — worse — silently mis-evaluated, changing which steps run. The walk is recursive
/// because <c>is_autocommit</c> is almost always nested inside <c>not</c>/<c>and</c>/<c>or</c>
/// (the guarded replica-push shape is <c>not(is_autocommit)</c>).
/// </para>
/// <para>
/// This check runs before anything is written to the socket so a rejected batch never
/// leaves a half-opened stream behind.
/// </para>
/// </remarks>
internal static class HranaBatchContract
{
    internal const string IsAutocommitCondition = "is_autocommit";

    /// <summary>Highest minimum version required by any condition in the batch.</summary>
    public static int MinimumVersion(RemoteBatch? batch)
    {
        if (batch is null)
            return 1;

        var minimum = 1;
        foreach (var step in batch.Steps)
            minimum = Math.Max(minimum, MinimumVersion(step.Condition, depth: 0));

        return minimum;
    }

    /// <summary>
    /// Throws when the negotiated version cannot evaluate every condition in the batch.
    /// </summary>
    public static void EnsureVersionSupports(RemoteBatch? batch, int negotiatedVersion)
    {
        var minimum = MinimumVersion(batch);
        if (minimum <= negotiatedVersion)
            return;

        throw new AhtolaException(
            $"The Hrana server negotiated protocol version {negotiatedVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)}, "
            + $"which does not support the '{IsAutocommitCondition}' batch condition (requires version "
            + $"{minimum.ToString(System.Globalization.CultureInfo.InvariantCulture)}). Nothing was sent.");
    }

    private static int MinimumVersion(RemoteBatchCondition? condition, int depth)
    {
        if (condition is null)
            return 1;

        // A cyclic or absurdly deep condition tree is a client-side construction bug; refuse
        // it rather than recursing until the stack dies.
        if (depth > 64)
            throw new AhtolaException("A Hrana batch condition is nested more than 64 levels deep.");

        if (string.Equals(condition.Type, IsAutocommitCondition, StringComparison.Ordinal))
            return 3;

        var minimum = MinimumVersion(condition.Condition, depth + 1);
        if (condition.Conditions is { } operands)
        {
            foreach (var operand in operands)
                minimum = Math.Max(minimum, MinimumVersion(operand, depth + 1));
        }

        return minimum;
    }
}

/// <summary>
/// A server message: <c>hello_ok</c>, <c>hello_error</c>, <c>response_ok</c> or
/// <c>response_error</c>. Parsed permissively per field and strictly per discriminator.
/// </summary>
internal sealed class HranaServerMsg
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("request_id")]
    public int? RequestId { get; init; }

    [JsonPropertyName("response")]
    public HranaResponse? Response { get; init; }

    [JsonPropertyName("error")]
    public RemoteError? Error { get; init; }
}

/// <summary>The Hrana response union. <c>result</c> stays a raw element until the request type is known.</summary>
/// <remarks>
/// Every optional-looking field is modelled as nullable so an <em>absent</em> field is
/// distinguishable from a present default. The spec makes <c>entries</c>/<c>done</c>
/// (<c>fetch_cursor</c>) and <c>is_autocommit</c> (<c>get_autocommit</c>) mandatory, and
/// silently substituting <c>false</c>/<c>[]</c> for a missing field would turn a protocol
/// violation into a wrong answer. <see cref="AhtolaHranaResponseContract"/> enforces the
/// per-request-type requirements on the receive path so violations fault the generation.
/// </remarks>
internal sealed class HranaResponse
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("result")]
    public JsonElement Result { get; init; }

    [JsonPropertyName("entries")]
    public List<RemoteCursorEntry>? Entries { get; init; }

    [JsonPropertyName("done")]
    public bool? Done { get; init; }

    [JsonPropertyName("is_autocommit")]
    public bool? IsAutocommit { get; init; }
}

/// <summary>Hrana <c>DescribeResult</c> (v2+).</summary>
internal sealed class RemoteDescribeResult
{
    [JsonPropertyName("params")]
    public List<RemoteDescribeParam> Parameters { get; init; } = [];

    [JsonPropertyName("cols")]
    public List<RemoteDescribeColumn> Columns { get; init; } = [];

    [JsonPropertyName("is_explain")]
    public bool IsExplain { get; init; }

    [JsonPropertyName("is_readonly")]
    public bool IsReadOnly { get; init; }
}

/// <summary>Hrana <c>DescribeParam</c>.</summary>
internal sealed class RemoteDescribeParam
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

/// <summary>Hrana <c>DescribeCol</c>.</summary>
internal sealed class RemoteDescribeColumn
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("decltype")]
    public string? DeclType { get; init; }
}

/// <summary>
/// Source-generated metadata for the Hrana WebSocket envelopes. No reflection-based
/// serialization is used anywhere on this path so the transport stays NativeAOT- and
/// trim-safe.
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HranaHelloMsg))]
[JsonSerializable(typeof(HranaRequestMsg))]
[JsonSerializable(typeof(HranaServerMsg))]
[JsonSerializable(typeof(RemoteStatementResult))]
[JsonSerializable(typeof(RemoteBatchResult))]
[JsonSerializable(typeof(RemoteDescribeResult))]
internal sealed partial class AhtolaHranaJsonContext : JsonSerializerContext;
