using System.Globalization;
using System.Text.Json;

namespace Ahtola;

/// <summary>
/// Structural validation of a Hrana <c>response_ok</c> payload against the request type it
/// answers.
/// </summary>
/// <remarks>
/// <para>
/// This runs on the receive loop, before the response is handed to the waiting caller, so a
/// malformed payload becomes a generation fault instead of a silent default or an
/// application-level error. Substituting a default for a mandatory field is the dangerous
/// failure mode: a missing <c>is_autocommit</c> read as <c>false</c> would tell the ADO.NET
/// layer a transaction is open, and a missing <c>done</c> read as <c>false</c> would spin a
/// cursor forever.
/// </para>
/// <para>
/// Unknown <em>nested</em> discriminators (cursor entry types, value types) are treated the
/// same way as an unknown top-level message type: they terminate the generation. Silently
/// ignoring them would let a newer server drop rows or steps on the floor without the client
/// ever noticing. Unknown <em>fields</em> remain ignored, per HRANA_3_SPEC.
/// </para>
/// </remarks>
internal static class AhtolaHranaResponseContract
{
    private const int MaxCursorValueTypeLength = 64;

    /// <summary>
    /// Returns null when the response satisfies the contract for <paramref name="expectedType"/>,
    /// otherwise a human-readable description of the violation.
    /// </summary>
    public static string? Validate(string expectedType, HranaResponse response)
    {
        if (response is null)
            return "The Hrana server sent an empty response object.";

        if (!string.Equals(response.Type, expectedType, StringComparison.Ordinal))
        {
            return $"The Hrana server answered a '{expectedType}' request with a "
                + $"'{Describe(response.Type)}' response.";
        }

        return expectedType switch
        {
            HranaRequest.OpenStream
                or HranaRequest.CloseStream
                or HranaRequest.StoreSql
                or HranaRequest.CloseSql
                or HranaRequest.Sequence
                or HranaRequest.OpenCursor
                or HranaRequest.CloseCursor => null,

            HranaRequest.Execute => ValidateStatementResult(response.Result, expectedType),
            HranaRequest.BatchRequest => ValidateBatchResult(response.Result, expectedType),
            HranaRequest.Describe => ValidateDescribeResult(response.Result, expectedType),

            HranaRequest.GetAutocommit => response.IsAutocommit is null
                ? "The Hrana 'get_autocommit' response did not include the mandatory boolean "
                    + "'is_autocommit' field."
                : null,

            HranaRequest.FetchCursor => ValidateFetchCursor(response),

            _ => $"The Hrana server answered an unknown request type '{Describe(expectedType)}'.",
        };
    }

    /// <summary>Validates the <c>error</c> object carried by a <c>response_error</c> message.</summary>
    public static string? ValidateError(RemoteError? error, string context)
    {
        if (error is null)
            return $"A Hrana {context} did not include the mandatory 'error' object.";
        if (error.Message is null)
            return $"A Hrana {context} error did not include the mandatory string 'message' field.";

        return null;
    }

    private static string? ValidateFetchCursor(HranaResponse response)
    {
        if (response.Done is null)
            return "The Hrana 'fetch_cursor' response did not include the mandatory boolean 'done' field.";
        if (response.Entries is not { } entries)
            return "The Hrana 'fetch_cursor' response did not include the mandatory 'entries' array.";

        for (var index = 0; index < entries.Count; index++)
        {
            if (ValidateCursorEntry(entries[index]) is { } violation)
            {
                return $"The Hrana 'fetch_cursor' entry at index "
                    + $"{index.ToString(CultureInfo.InvariantCulture)} is invalid: {violation}";
            }
        }

        return null;
    }

    private static string? ValidateCursorEntry(RemoteCursorEntry? entry)
    {
        if (entry is null)
            return "the entry was null";

        switch (entry.Type)
        {
            case "step_begin":
                if (entry.Step is not { } step)
                    return "step_begin did not include a numeric 'step' field";
                if (step < 0)
                    return $"step_begin reported a negative step {step.ToString(CultureInfo.InvariantCulture)}";
                return entry.Columns is null ? "step_begin did not include a 'cols' array" : null;

            case "step_end":
                return entry.AffectedRowCount is null
                    ? "step_end did not include a numeric 'affected_row_count' field"
                    : null;

            case "row":
                if (entry.Row is not { } row)
                    return "row did not include a 'row' array";
                for (var index = 0; index < row.Count; index++)
                {
                    if (ValidateValue(row[index]) is { } violation)
                    {
                        return $"the value at index {index.ToString(CultureInfo.InvariantCulture)} is invalid: "
                            + violation;
                    }
                }
                return null;

            case "step_error":
                return entry.Step is null
                    ? "step_error did not include a numeric 'step' field"
                    : ValidateError(entry.Error, "cursor step_error");

            case "error":
                return ValidateError(entry.Error, "cursor error");

            default:
                // A future entry type cannot be skipped: it may carry rows or terminate a step.
                return $"unknown cursor entry type '{Describe(entry.Type)}'";
        }
    }

    private static string? ValidateStatementResult(JsonElement result, string requestType)
    {
        if (result.ValueKind != JsonValueKind.Object)
            return $"The Hrana '{requestType}' response did not include a 'result' object.";

        return ValidateStatementResultObject(result, $"the '{requestType}' result");
    }

    private static string? ValidateStatementResultObject(JsonElement result, string context)
    {
        if (!result.TryGetProperty("cols", out var columns) || columns.ValueKind != JsonValueKind.Array)
            return $"{context} did not include the mandatory 'cols' array.";
        if (!result.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return $"{context} did not include the mandatory 'rows' array.";
        if (!result.TryGetProperty("affected_row_count", out var affected) || affected.ValueKind != JsonValueKind.Number)
            return $"{context} did not include the mandatory numeric 'affected_row_count' field.";
        if (!affected.TryGetUInt64(out _))
            return $"{context} reported an 'affected_row_count' outside the unsigned 64-bit range.";

        var columnCount = columns.GetArrayLength();
        var rowIndex = 0;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array)
            {
                return $"{context} row {rowIndex.ToString(CultureInfo.InvariantCulture)} is not an array.";
            }

            if (row.GetArrayLength() != columnCount)
            {
                return $"{context} row {rowIndex.ToString(CultureInfo.InvariantCulture)} contained "
                    + $"{row.GetArrayLength().ToString(CultureInfo.InvariantCulture)} values for "
                    + $"{columnCount.ToString(CultureInfo.InvariantCulture)} columns.";
            }

            var valueIndex = 0;
            foreach (var value in row.EnumerateArray())
            {
                if (ValidateValue(value) is { } violation)
                {
                    return $"{context} row {rowIndex.ToString(CultureInfo.InvariantCulture)} value "
                        + $"{valueIndex.ToString(CultureInfo.InvariantCulture)} is invalid: {violation}";
                }
                valueIndex++;
            }

            rowIndex++;
        }

        return null;
    }

    private static string? ValidateBatchResult(JsonElement result, string requestType)
    {
        if (result.ValueKind != JsonValueKind.Object)
            return $"The Hrana '{requestType}' response did not include a 'result' object.";
        if (!result.TryGetProperty("step_results", out var stepResults) || stepResults.ValueKind != JsonValueKind.Array)
            return $"The Hrana '{requestType}' result did not include the mandatory 'step_results' array.";
        if (!result.TryGetProperty("step_errors", out var stepErrors) || stepErrors.ValueKind != JsonValueKind.Array)
            return $"The Hrana '{requestType}' result did not include the mandatory 'step_errors' array.";
        if (stepResults.GetArrayLength() != stepErrors.GetArrayLength())
        {
            return $"The Hrana '{requestType}' result reported "
                + $"{stepResults.GetArrayLength().ToString(CultureInfo.InvariantCulture)} step results for "
                + $"{stepErrors.GetArrayLength().ToString(CultureInfo.InvariantCulture)} step errors.";
        }

        var index = 0;
        foreach (var stepResult in stepResults.EnumerateArray())
        {
            if (stepResult.ValueKind is JsonValueKind.Null)
            {
                index++;
                continue;
            }

            if (stepResult.ValueKind != JsonValueKind.Object)
            {
                return $"The Hrana '{requestType}' step result at index "
                    + $"{index.ToString(CultureInfo.InvariantCulture)} is neither null nor an object.";
            }

            if (ValidateStatementResultObject(
                    stepResult,
                    $"the '{requestType}' step result at index {index.ToString(CultureInfo.InvariantCulture)}")
                is { } violation)
            {
                return violation;
            }

            index++;
        }

        index = 0;
        foreach (var stepError in stepErrors.EnumerateArray())
        {
            if (stepError.ValueKind is JsonValueKind.Null)
            {
                index++;
                continue;
            }

            if (stepError.ValueKind != JsonValueKind.Object)
            {
                return $"The Hrana '{requestType}' step error at index "
                    + $"{index.ToString(CultureInfo.InvariantCulture)} is neither null nor an object.";
            }

            if (!stepError.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String)
            {
                return $"The Hrana '{requestType}' step error at index "
                    + $"{index.ToString(CultureInfo.InvariantCulture)} did not include a string 'message'.";
            }

            index++;
        }

        return null;
    }

    private static string? ValidateDescribeResult(JsonElement result, string requestType)
    {
        if (result.ValueKind != JsonValueKind.Object)
            return $"The Hrana '{requestType}' response did not include a 'result' object.";
        if (!result.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Array)
            return $"The Hrana '{requestType}' result did not include the mandatory 'params' array.";
        if (!result.TryGetProperty("cols", out var columns) || columns.ValueKind != JsonValueKind.Array)
            return $"The Hrana '{requestType}' result did not include the mandatory 'cols' array.";
        if (!result.TryGetProperty("is_explain", out var isExplain) || !IsBoolean(isExplain))
            return $"The Hrana '{requestType}' result did not include the mandatory boolean 'is_explain' field.";
        if (!result.TryGetProperty("is_readonly", out var isReadOnly) || !IsBoolean(isReadOnly))
            return $"The Hrana '{requestType}' result did not include the mandatory boolean 'is_readonly' field.";

        foreach (var column in columns.EnumerateArray())
        {
            if (column.ValueKind != JsonValueKind.Object)
                return $"The Hrana '{requestType}' result contained a non-object entry in 'cols'.";
            if (!column.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                return $"The Hrana '{requestType}' result contained a column without a string 'name'.";
        }

        return null;
    }

    private static string? ValidateValue(RemoteResponseValue? value)
    {
        if (value is null)
            return "the value was null";

        switch (value.Type)
        {
            case "null":
                return null;

            case "integer":
                if (value.Value.ValueKind == JsonValueKind.String)
                {
                    return long.TryParse(value.Value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                        ? null
                        : "an 'integer' value was not a signed 64-bit integer";
                }
                if (value.Value.ValueKind == JsonValueKind.Number)
                    return value.Value.TryGetInt64(out _) ? null : "an 'integer' value was outside the signed 64-bit range";
                return "an 'integer' value did not include a numeric or string 'value' field";

            case "float":
                if (value.Value.ValueKind == JsonValueKind.Number)
                    return value.Value.TryGetDouble(out _) ? null : "a 'float' value was outside the double range";
                if (value.Value.ValueKind == JsonValueKind.String)
                {
                    return double.TryParse(value.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                        ? null
                        : "a 'float' value was not a valid number";
                }
                return "a 'float' value did not include a numeric 'value' field";

            case "text":
                return value.Value.ValueKind == JsonValueKind.String
                    ? null
                    : "a 'text' value did not include a string 'value' field";

            case "blob":
                return value.Base64 is null
                    ? "a 'blob' value did not include a string 'base64' field"
                    : null;

            default:
                return $"unknown value type '{Describe(value.Type)}'";
        }
    }

    private static string? ValidateValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return "the value is not a JSON object";
        if (!value.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            return "the value did not include a string 'type' field";

        var name = type.GetString();
        switch (name)
        {
            case "null":
                return null;

            case "integer":
                if (!value.TryGetProperty("value", out var integer))
                    return "an 'integer' value did not include a 'value' field";
                if (integer.ValueKind == JsonValueKind.String)
                {
                    return long.TryParse(integer.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                        ? null
                        : "an 'integer' value was not a signed 64-bit integer";
                }
                return integer.ValueKind == JsonValueKind.Number && integer.TryGetInt64(out _)
                    ? null
                    : "an 'integer' value was outside the signed 64-bit range";

            case "float":
                if (!value.TryGetProperty("value", out var real))
                    return "a 'float' value did not include a 'value' field";
                if (real.ValueKind == JsonValueKind.String)
                {
                    return double.TryParse(real.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                        ? null
                        : "a 'float' value was not a valid number";
                }
                return real.ValueKind == JsonValueKind.Number && real.TryGetDouble(out _)
                    ? null
                    : "a 'float' value did not include a numeric 'value' field";

            case "text":
                return value.TryGetProperty("value", out var text) && text.ValueKind == JsonValueKind.String
                    ? null
                    : "a 'text' value did not include a string 'value' field";

            case "blob":
                return value.TryGetProperty("base64", out var base64) && base64.ValueKind == JsonValueKind.String
                    ? null
                    : "a 'blob' value did not include a string 'base64' field";

            default:
                return $"unknown value type '{Describe(name)}'";
        }
    }

    private static bool IsBoolean(JsonElement element)
        => element.ValueKind is JsonValueKind.True or JsonValueKind.False;

    /// <summary>Truncates a server-supplied discriminator so it cannot bloat the fault message.</summary>
    private static string Describe(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        return value.Length <= MaxCursorValueTypeLength
            ? value
            : string.Concat(value.AsSpan(0, MaxCursorValueTypeLength), "...");
    }
}
