using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ahtola.Tests.Oracle;

internal sealed record ReplayOperation(
    int Index,
    string Sql,
    string Comparison,
    bool Ordered,
    int? Actor = null,
    string? Action = null,
    IReadOnlyList<int>? Dependencies = null);

internal sealed record ReplayScheduleStep(
    int Step,
    int Choice,
    int Actor,
    string ActorName,
    string YieldPoint,
    int YieldOrdinal,
    IReadOnlyList<int> EnabledActors,
    long ProgressCount);

internal sealed class ReplayTrace
{
    public string TestName { get; init; } = string.Empty;

    public ulong RootSeed { get; init; }

    public ulong StreamSeed { get; init; }

    public string StreamName { get; init; } = string.Empty;

    public string SeedDiagnostics { get; init; } = string.Empty;

    public List<ReplayOperation> Operations { get; init; } = [];

    public List<int> ScheduleChoices { get; init; } = [];

    public List<ReplayScheduleStep> Schedule { get; init; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Failure { get; set; }

    public static ReplayTrace Create(string testName, StableRandomStream stream)
        => new()
        {
            TestName = testName,
            RootSeed = stream.RootSeed,
            StreamSeed = stream.Seed,
            StreamName = stream.Name,
            SeedDiagnostics = stream.Diagnostics,
        };

    public void Add(
        string sql,
        string comparison = "ordered",
        bool ordered = true,
        int? actor = null,
        string? action = null,
        IReadOnlyList<int>? dependencies = null)
        => Operations.Add(
            new ReplayOperation(
                Operations.Count,
                sql,
                comparison,
                ordered,
                actor,
                action,
                dependencies is null ? null : [.. dependencies]));

    public void AddScheduleStep(CooperativeScheduleStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        ScheduleChoices.Add(step.Choice);
        Schedule.Add(new ReplayScheduleStep(
            step.Step,
            step.Choice,
            step.ActorId,
            step.ActorName,
            step.YieldPoint,
            step.YieldOrdinal,
            [.. step.EnabledActors],
            step.ProgressCount));
    }

    public string ToJson()
        => JsonSerializer.Serialize(this, SerializerOptions);

    public static ReplayTrace FromJson(string json)
        => JsonSerializer.Deserialize<ReplayTrace>(json, SerializerOptions)
            ?? throw new InvalidOperationException("The replay trace contained no value.");

    public string ToSql()
    {
        var writer = new StringWriter();
        writer.WriteLine($"-- {TestName}");
        writer.WriteLine($"-- {SeedDiagnostics}");
        if (ScheduleChoices.Count > 0)
        {
            writer.WriteLine($"-- schedule choices=[{string.Join(",", ScheduleChoices)}]");
            foreach (var step in Schedule)
            {
                writer.WriteLine(
                    $"-- schedule {step.Step}; choice={step.Choice}; actor={step.Actor}:{step.ActorName}; "
                    + $"yield={step.YieldPoint}#{step.YieldOrdinal}; "
                    + $"enabled=[{string.Join(",", step.EnabledActors)}]; progress={step.ProgressCount}");
            }
        }

        foreach (var operation in Operations)
        {
            writer.WriteLine();
            var actor = operation.Actor is { } actorId ? $"; actor={actorId}" : string.Empty;
            var action = operation.Action is { Length: > 0 } actionName ? $"; action={actionName}" : string.Empty;
            var dependencies = operation.Dependencies is { Count: > 0 } required
                ? $"; depends=[{string.Join(",", required)}]"
                : string.Empty;
            writer.WriteLine(
                $"-- operation {operation.Index}; comparison={operation.Comparison}; ordered={operation.Ordered}"
                + actor
                + action
                + dependencies);
            writer.WriteLine(operation.Sql.TrimEnd().EndsWith(';') ? operation.Sql.TrimEnd() : operation.Sql.TrimEnd() + ";");
        }

        return writer.ToString();
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

internal static class OracleFailureArtifacts
{
    public static void Run(ReplayTrace trace, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            Write(trace, exception);
            throw;
        }
    }

    private static void Write(ReplayTrace trace, Exception exception)
    {
        trace.Failure = exception.ToString();
        var safeName = string.Concat(trace.TestName.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "oracle-failures");
        Directory.CreateDirectory(directory);
        var stem = $"{safeName}-{trace.StreamSeed:x16}-{Guid.NewGuid():N}";
        var jsonPath = Path.Combine(directory, stem + ".json");
        var sqlPath = Path.Combine(directory, stem + ".sql");
        File.WriteAllText(jsonPath, trace.ToJson());
        File.WriteAllText(sqlPath, trace.ToSql());
        TestContext.AddTestAttachment(jsonPath, $"Oracle replay trace ({trace.SeedDiagnostics})");
        TestContext.AddTestAttachment(sqlPath, "Oracle replay SQL");
    }
}
