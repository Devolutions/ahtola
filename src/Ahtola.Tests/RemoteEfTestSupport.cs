using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Ahtola.Tests;

/// <summary>
/// Shared entity model for RemoteEf* tests exercising <c>UseAhtola</c> over a direct remote
/// Hrana or embedded-replica connection.
/// </summary>
internal sealed class RemoteWidgetContext(DbContextOptions<RemoteWidgetContext> options) : DbContext(options)
{
    public DbSet<RemoteWidget> Widgets => Set<RemoteWidget>();
}

internal sealed class RemoteWidget
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public decimal Price { get; set; }

    public int Quantity { get; set; }

    public List<string> Tags { get; set; } = [];
}

/// <summary>
/// A fake Hrana-over-HTTP endpoint for EF Core remote (RemoteHrana) tests. Records every SQL
/// statement sent — both single "execute" requests and batch steps — in arrival order so tests
/// can assert on the exact SQL/request sequence, and returns tailored per-statement responses
/// rather than one canned value for everything: RETURNING clauses get a generated-id row,
/// <c>sqlite_master</c>/<c>COUNT(*)</c> schema probes default to "no tables yet", generic
/// SELECTs get a scalar value, and everything else is a generic success/1-row-affected
/// acknowledgement. Tests needing an exact response (or error) for a specific statement can
/// script it with <see cref="At"/>/<see cref="ErrorAt"/>, keyed by the 0-based position of that
/// statement in the overall sequence (single executes and batch steps share one counter).
/// </summary>
internal sealed class ScriptedHranaHandler : HttpMessageHandler
{
    private readonly List<string> _sqlLog = [];
    private readonly Dictionary<int, JsonObject> _scriptedResults = [];
    private readonly Dictionary<int, JsonObject> _scriptedErrors = [];
    private readonly Dictionary<int, (HttpStatusCode Status, string? Body)> _scriptedHttpErrors = [];
    private int _statementIndex;
    private long _nextAutoId = 1;
    private bool _isAutocommit = true;

    /// <summary>Every SQL statement sent so far, in arrival order (batch steps are flattened
    /// into the same sequence as single executes).</summary>
    public IReadOnlyList<string> SqlLog => _sqlLog;

    /// <summary>The <c>Authorization</c> header of the most recent request, if any.</summary>
    public string? Authorization { get; private set; }

    public int RequestCount { get; private set; }

    /// <summary>Whether the fake's tracked connection state is currently in autocommit mode
    /// (true before any BEGIN or after COMMIT/ROLLBACK; false while a transaction is open),
    /// mirroring what a real Hrana server reports for an <c>is_autocommit</c> condition.</summary>
    public bool IsAutocommit => _isAutocommit;

    public ScriptedHranaHandler At(int statementIndex, JsonObject result)
    {
        _scriptedResults[statementIndex] = result;
        return this;
    }

    public ScriptedHranaHandler ErrorAt(int statementIndex, string message, string code = "SQLITE_ERROR")
    {
        _scriptedErrors[statementIndex] = new JsonObject { ["message"] = message, ["code"] = code };
        return this;
    }

    /// <summary>Scripts a raw transport-level HTTP failure (as opposed to a Hrana-JSON-level
    /// <c>{"type":"error",...}</c> body returned with HTTP 200) for the statement at
    /// <paramref name="statementIndex"/>, so tests can prove callers correctly distinguish e.g.
    /// HTTP 404 (not-found) from HTTP 401/403 (auth) or HTTP 5xx/408/429 (transient).</summary>
    public ScriptedHranaHandler HttpErrorAt(int statementIndex, HttpStatusCode status, string? body = null)
    {
        _scriptedHttpErrors[statementIndex] = (status, body);
        return this;
    }

    public static JsonObject Ok(long affectedRowCount = 1) => new()
    {
        ["cols"] = new JsonArray(),
        ["rows"] = new JsonArray(),
        ["affected_row_count"] = affectedRowCount,
    };

    public static JsonObject Rows(IReadOnlyList<string> columnNames, IReadOnlyList<IReadOnlyList<JsonObject>> rows, long affectedRowCount = 0)
    {
        var cols = new JsonArray();
        foreach (var name in columnNames)
            cols.Add(new JsonObject { ["name"] = name });

        var rowArray = new JsonArray();
        foreach (var row in rows)
        {
            var rowNode = new JsonArray();
            foreach (var value in row)
                rowNode.Add(value.DeepClone());

            rowArray.Add(rowNode);
        }

        return new JsonObject
        {
            ["cols"] = cols,
            ["rows"] = rowArray,
            ["affected_row_count"] = affectedRowCount,
        };
    }

    public static JsonObject Integer(long value) => new() { ["type"] = "integer", ["value"] = value.ToString(CultureInfo.InvariantCulture) };

    public static JsonObject Text(string value) => new() { ["type"] = "text", ["value"] = value };

    public static JsonObject Null() => new() { ["type"] = "null" };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestCount++;
        Authorization = request.Headers.Authorization?.ToString();

        if (_scriptedHttpErrors.TryGetValue(_statementIndex, out var httpError))
        {
            return new HttpResponseMessage(httpError.Status)
            {
                Content = new StringContent(httpError.Body ?? string.Empty, Encoding.UTF8, "text/plain"),
            };
        }

        using var document = JsonDocument.Parse(
            await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (request.RequestUri!.AbsolutePath.EndsWith("/v3/cursor", StringComparison.Ordinal))
            return RespondToCursor(document.RootElement.GetProperty("batch"));

        var requestEntry = document.RootElement.GetProperty("requests")[0];

        var responseRoot = new JsonObject();
        if (requestEntry.TryGetProperty("batch", out var batch))
        {
            var stepResults = new JsonArray();
            var stepErrors = new JsonArray();
            var stepOutcomes = new List<BatchStepOutcome>();
            foreach (var step in batch.GetProperty("steps").EnumerateArray())
            {
                var sql = step.GetProperty("stmt").GetProperty("sql").GetString()!;
                var shouldRun = !step.TryGetProperty("condition", out var condition)
                    || EvaluateBatchCondition(condition, stepOutcomes, _isAutocommit);
                if (!shouldRun)
                {
                    // A condition (e.g. "ok"/"error" gated on an earlier step) that evaluates
                    // to false means this step must NOT execute at all: Hrana returns null for
                    // both the result and the error, and the SQL must not be recorded/run —
                    // this is what lets a test prove a later destructive statement never fires
                    // after an earlier failure. A skipped step is its own outcome (neither
                    // succeeded nor failed), matching real Hrana semantics: a later "error"
                    // condition referencing it must not evaluate to true.
                    stepResults.Add(null);
                    stepErrors.Add(null);
                    stepOutcomes.Add(BatchStepOutcome.Skipped);
                    continue;
                }

                var (result, error) = RespondTo(sql);
                stepResults.Add(result?.DeepClone());
                stepErrors.Add(error?.DeepClone());
                stepOutcomes.Add(error is null ? BatchStepOutcome.Succeeded : BatchStepOutcome.Failed);
            }

            var batchResult = new JsonObject { ["step_results"] = stepResults, ["step_errors"] = stepErrors };
            responseRoot["results"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "ok",
                    ["response"] = new JsonObject { ["type"] = "batch", ["result"] = batchResult },
                },
            };
        }
        else if (requestEntry.GetProperty("type").GetString() == "close")
        {
            responseRoot["baton"] = null;
            responseRoot["results"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "ok",
                    ["response"] = new JsonObject { ["type"] = "close" },
                },
            };
        }
        else
        {
            var sql = requestEntry.GetProperty("stmt").GetProperty("sql").GetString()!;
            var (result, error) = RespondTo(sql);
            responseRoot["results"] = new JsonArray
            {
                error is not null
                    ? new JsonObject { ["type"] = "error", ["error"] = error.DeepClone() }
                    : new JsonObject
                    {
                        ["type"] = "ok",
                        ["response"] = new JsonObject { ["type"] = "execute", ["result"] = result!.DeepClone() },
                    },
            };
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseRoot.ToJsonString(), Encoding.UTF8, "application/json"),
        };
    }

    private HttpResponseMessage RespondToCursor(JsonElement batch)
    {
        var lines = new StringBuilder();
        lines.AppendLine("""{"baton":"scripted-cursor-baton","base_url":null}""");
        var stepOutcomes = new List<BatchStepOutcome>();
        var stepIndex = 0;
        foreach (var step in batch.GetProperty("steps").EnumerateArray())
        {
            var shouldRun = !step.TryGetProperty("condition", out var condition)
                || EvaluateBatchCondition(condition, stepOutcomes, _isAutocommit);
            if (!shouldRun)
            {
                stepOutcomes.Add(BatchStepOutcome.Skipped);
                stepIndex++;
                continue;
            }

            var sql = step.GetProperty("stmt").GetProperty("sql").GetString()!;
            var (result, error) = RespondTo(sql);
            if (error is not null)
            {
                lines.AppendLine(new JsonObject
                {
                    ["type"] = "step_error",
                    ["step"] = stepIndex,
                    ["error"] = error.DeepClone(),
                }.ToJsonString());
                stepOutcomes.Add(BatchStepOutcome.Failed);
                stepIndex++;
                continue;
            }

            lines.AppendLine(new JsonObject
            {
                ["type"] = "step_begin",
                ["step"] = stepIndex,
                ["cols"] = result!["cols"]?.DeepClone() ?? new JsonArray(),
            }.ToJsonString());
            foreach (var row in result["rows"]?.AsArray() ?? [])
            {
                lines.AppendLine(new JsonObject
                {
                    ["type"] = "row",
                    ["row"] = row?.DeepClone() ?? new JsonArray(),
                }.ToJsonString());
            }
            lines.AppendLine(new JsonObject
            {
                ["type"] = "step_end",
                ["affected_row_count"] = result["affected_row_count"]?.DeepClone() ?? 0,
                ["last_insert_rowid"] = result["last_insert_rowid"]?.DeepClone(),
            }.ToJsonString());
            stepOutcomes.Add(BatchStepOutcome.Succeeded);
            stepIndex++;
        }

        lines.AppendLine("""{"type":"replication_index","replication_index":null}""");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(lines.ToString(), Encoding.UTF8, "application/x-ndjson"),
        };
    }

    /// <summary>The outcome of a batch step once processed: whether it actually ran and
    /// succeeded, actually ran and failed, or was never run because an earlier guard condition
    /// evaluated to false. Real Hrana batch conditions distinguish all three — in particular, an
    /// "error" condition referencing a step is true only if that step actually failed, not if it
    /// was merely skipped.</summary>
    internal enum BatchStepOutcome
    {
        Succeeded,
        Failed,
        Skipped,
    }

    /// <summary>Evaluates a Hrana batch step "condition" object (as produced by
    /// <see cref="AhtolaRemoteBatchCondition"/>: "ok"/"error" referencing a specific earlier
    /// step index, "not"/"and"/"or" composition, and "is_autocommit") against the per-step
    /// outcome record built up so far and the connection's current autocommit state. Exposed
    /// internally so tests can exercise the evaluator directly, independent of any HTTP/JSON
    /// transport.</summary>
    internal static bool EvaluateBatchCondition(
        JsonElement condition,
        IReadOnlyList<BatchStepOutcome> stepOutcomes,
        bool isAutocommit)
    {
        var type = condition.GetProperty("type").GetString();
        return type switch
        {
            "ok" => GetStepOutcome(condition, stepOutcomes) == BatchStepOutcome.Succeeded,
            "error" => GetStepOutcome(condition, stepOutcomes) == BatchStepOutcome.Failed,
            "not" => !EvaluateBatchCondition(condition.GetProperty("cond"), stepOutcomes, isAutocommit),
            "and" => condition.GetProperty("conds").EnumerateArray()
                .All(operand => EvaluateBatchCondition(operand, stepOutcomes, isAutocommit)),
            "or" => condition.GetProperty("conds").EnumerateArray()
                .Any(operand => EvaluateBatchCondition(operand, stepOutcomes, isAutocommit)),
            "is_autocommit" => isAutocommit,
            _ => true,
        };

        static BatchStepOutcome GetStepOutcome(JsonElement condition, IReadOnlyList<BatchStepOutcome> stepOutcomes)
        {
            var step = condition.GetProperty("step").GetInt32();
            // A step index that hasn't happened yet (out of range) is treated the same as a
            // skipped step: neither "ok" nor "error" can be true for something that never ran.
            return step >= 0 && step < stepOutcomes.Count ? stepOutcomes[step] : BatchStepOutcome.Skipped;
        }
    }

    private (JsonObject? Result, JsonObject? Error) RespondTo(string sql)
    {
        var index = _statementIndex++;
        _sqlLog.Add(sql);
        UpdateAutocommitState(sql);

        if (_scriptedErrors.TryGetValue(index, out var scriptedError))
            return (null, scriptedError);
        if (_scriptedResults.TryGetValue(index, out var scriptedResult))
            return (scriptedResult, null);

        return (DefaultResult(sql), null);
    }

    /// <summary>Tracks the fake's autocommit state through explicit transaction-control
    /// statements: a <c>BEGIN</c> leaves autocommit mode (a transaction is now open), while a
    /// <c>COMMIT</c> or <c>ROLLBACK</c> returns to it (matching real SQLite/Hrana semantics for
    /// the <c>is_autocommit</c> batch condition — see <see cref="EvaluateBatchCondition"/>).</summary>
    private void UpdateAutocommitState(string sql)
    {
        var trimmed = sql.TrimStart();
        if (trimmed.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase))
            _isAutocommit = false;
        else if (trimmed.StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase)
                 || trimmed.StartsWith("ROLLBACK", StringComparison.OrdinalIgnoreCase))
            _isAutocommit = true;
    }

    private JsonObject DefaultResult(string sql)
    {
        var returningColumns = ExtractReturningColumns(sql);
        if (returningColumns.Count > 0)
        {
            var id = _nextAutoId++;
            return Rows(returningColumns, [returningColumns.Select(_ => Integer(id)).ToArray()], affectedRowCount: 1);
        }

        var trimmed = sql.TrimStart();
        if (trimmed.Contains("sqlite_master", StringComparison.OrdinalIgnoreCase)
            && trimmed.Contains("COUNT(*)", StringComparison.OrdinalIgnoreCase))
        {
            // A freshly-scripted fake database has no tables until a test says otherwise.
            return Rows(["value"], [[Integer(0)]]);
        }

        if (trimmed.Contains("changes()", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("last_insert_rowid()", StringComparison.OrdinalIgnoreCase))
        {
            // "SELECT changes();"-style affected-row-count checks (used by the non-RETURNING
            // insert/update/delete fallback path, and by lock-acquisition ExecuteScalar checks
            // like SqliteHistoryRepository's) expect exactly 1 for "the single row matched".
            return Rows(["value"], [[Integer(1)]]);
        }

        if (trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            return Rows(["value"], [[Integer(42)]]);

        // A non-SELECT, non-RETURNING statement (INSERT/UPDATE/DELETE) never produces columns
        // or rows on a real server, regardless of whether the caller requested rows back —
        // fabricating a row here previously masked the reader-positioning bug this fake now
        // exercises honestly (see SqliteDataReader's delegated-reader skip-to-columns logic).
        return Ok();
    }

    private static List<string> ExtractReturningColumns(string sql)
    {
        const string marker = "RETURNING";
        var index = sql.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return [];

        var tail = sql[(index + marker.Length)..].Trim();
        return tail.Split(',')
            .Select(part => part.Trim().Trim('"'))
            .Where(part => part.Length > 0)
            .ToList();
    }
}

/// <summary>
/// A fake pull-updates endpoint for embedded-replica bootstrap tests: responds to any request
/// with a single delimited-protobuf pull-updates response encoding a fixed database image as raw
/// pages, matching <c>ManagedReplicaBootstrapper</c>'s wire protocol (see its
/// <c>DownloadDatabaseAsync</c>/<c>ParseHeader</c>/<c>ParsePage</c>) — just enough of it to
/// bootstrap one fixed image deterministically, not the full flexibility that engine's own tests
/// exercise (compression, incremental multi-response streams, etc.).
/// </summary>
internal sealed class ScriptedBootstrapHandler(byte[] databaseImage, string revision = "bootstrap-1") : HttpMessageHandler
{
    private const int PageSize = 4096;

    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestCount++;
        var message = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encode()),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/protobuf");
        return Task.FromResult(message);
    }

    private byte[] Encode()
    {
        if (databaseImage.Length % PageSize != 0)
        {
            throw new InvalidOperationException(
                "The fixture database image must be a whole number of 4096-byte pages.");
        }

        // Header: field 1 = revision (required, non-empty), field 2 = database page count
        // (required, >0), field 3 = raw-encoding marker (required, empty payload). Fields 5/6/8
        // (stream kind, apply mode, protocol) are all optional/defaulted and can be omitted.
        var header = new List<byte>();
        WriteLengthDelimitedField(header, 1, Encoding.UTF8.GetBytes(revision));
        WriteVarintField(header, 2, (ulong)(databaseImage.Length / PageSize));
        WriteLengthDelimitedField(header, 3, []);

        var response = new List<byte>();
        WriteDelimitedMessage(response, header);
        for (var offset = 0; offset < databaseImage.Length; offset += PageSize)
        {
            var page = new List<byte>();
            var pageId = (ulong)(offset / PageSize);
            // Page 0's id is protobuf's default value and may be omitted entirely — the parser
            // treats an absent field 1 as page 0 (see ParsePage's "pageId ?? 0").
            if (pageId != 0)
                WriteVarintField(page, 1, pageId);
            WriteLengthDelimitedField(page, 2, databaseImage.AsSpan(offset, PageSize));
            WriteDelimitedMessage(response, page);
        }

        return response.ToArray();
    }

    private static void WriteVarintField(List<byte> destination, int fieldNumber, ulong value)
    {
        WriteVarint(destination, (ulong)((fieldNumber << 3) | 0));
        WriteVarint(destination, value);
    }

    private static void WriteLengthDelimitedField(List<byte> destination, int fieldNumber, ReadOnlySpan<byte> payload)
    {
        WriteVarint(destination, (ulong)((fieldNumber << 3) | 2));
        WriteVarint(destination, (ulong)payload.Length);
        destination.AddRange(payload.ToArray());
    }

    private static void WriteDelimitedMessage(List<byte> destination, List<byte> message)
    {
        WriteVarint(destination, (ulong)message.Count);
        destination.AddRange(message);
    }

    private static void WriteVarint(List<byte> destination, ulong value)
    {
        while (value >= 0x80)
        {
            destination.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }

        destination.Add((byte)value);
    }
}
