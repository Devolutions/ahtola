using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ahtola;

/// <summary>
/// Hrana <c>Stmt</c>. Shared verbatim by the HTTP pipeline transport and the
/// Hrana WebSocket transport so both speak the identical statement wire shape.
/// </summary>
internal sealed class RemoteStatement
{
    [JsonPropertyName("sql")]
    public string Sql { get; init; } = "";

    [JsonPropertyName("args")]
    public List<RemoteRequestValue> Args { get; } = [];

    [JsonPropertyName("named_args")]
    public List<RemoteNamedArg> NamedArgs { get; } = [];

    [JsonPropertyName("want_rows")]
    public bool WantRows { get; init; }
}

/// <summary>Hrana <c>Batch</c>.</summary>
internal sealed class RemoteBatch
{
    [JsonPropertyName("steps")]
    public List<RemoteBatchStep> Steps { get; init; } = [];

    [JsonPropertyName("replication_index")]
    public string? ReplicationIndex { get; init; }
}

/// <summary>Hrana <c>BatchStep</c>.</summary>
internal sealed class RemoteBatchStep
{
    [JsonPropertyName("condition")]
    public RemoteBatchCondition? Condition { get; init; }

    [JsonPropertyName("stmt")]
    public RemoteStatement Statement { get; init; } = new();
}

/// <summary>Hrana <c>BatchCond</c>.</summary>
internal sealed class RemoteBatchCondition
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("step")]
    public int? Step { get; init; }

    [JsonPropertyName("cond")]
    public RemoteBatchCondition? Condition { get; init; }

    [JsonPropertyName("conds")]
    public List<RemoteBatchCondition>? Conditions { get; set; }
}

/// <summary>Hrana <c>NamedArg</c>.</summary>
internal sealed class RemoteNamedArg
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("value")]
    public RemoteRequestValue Value { get; init; } = RemoteRequestValue.Null();
}

/// <summary>
/// Hrana <c>Value</c> in request position. Integers travel as strings so 64-bit
/// precision survives JSON, and blobs travel as base64.
/// </summary>
[JsonConverter(typeof(RemoteRequestValueJsonConverter))]
internal sealed class RemoteRequestValue
{
    public string Type { get; init; } = "";

    public string? StringValue { get; init; }

    public string? Base64 { get; init; }

    public double? FloatValue { get; init; }

    public static RemoteRequestValue Null()
    {
        return new RemoteRequestValue
        {
            Type = "null",
        };
    }

    public static RemoteRequestValue FromAhtolaValue(AhtolaValue value)
    {
        return value.ValueType switch
        {
            AhtolaValueType.Empty or AhtolaValueType.Null => Null(),
            AhtolaValueType.Integer => new RemoteRequestValue
            {
                Type = "integer",
                StringValue = value.IntValue.ToString(CultureInfo.InvariantCulture),
            },
            AhtolaValueType.Real => new RemoteRequestValue
            {
                Type = "float",
                FloatValue = value.RealValue,
            },
            AhtolaValueType.Text => new RemoteRequestValue
            {
                Type = "text",
                StringValue = value.StringValue ?? string.Empty,
            },
            AhtolaValueType.Blob => new RemoteRequestValue
            {
                Type = "blob",
                Base64 = Convert.ToBase64String(value.BlobValue ?? []),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(value), value.ValueType, null),
        };
    }
}

internal sealed class RemoteRequestValueJsonConverter : JsonConverter<RemoteRequestValue>
{
    public override RemoteRequestValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException("Remote request values are serialized only.");

    public override void Write(Utf8JsonWriter writer, RemoteRequestValue value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("type", value.Type);
        if (value.FloatValue is { } floatValue)
            writer.WriteNumber("value", floatValue);
        else if (value.StringValue is not null)
            writer.WriteString("value", value.StringValue);
        else if (value.Base64 is not null)
            writer.WriteString("base64", value.Base64);
        writer.WriteEndObject();
    }
}
