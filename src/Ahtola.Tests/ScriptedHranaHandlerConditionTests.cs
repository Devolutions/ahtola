using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Direct unit tests of <see cref="ScriptedHranaHandler"/>'s batch-condition evaluator and
/// autocommit tracking — the test infrastructure every other RemoteEf* test implicitly relies on
/// for correct batch-guard behavior (e.g. <c>SqliteHistoryRepository</c>'s lock-acquisition
/// batches, and a real embedded-replica push's "only apply the journal inside an open
/// transaction" guard). These exercise <see cref="ScriptedHranaHandler.EvaluateBatchCondition"/>
/// in isolation, independent of any HTTP/EF plumbing, plus one end-to-end test proving the
/// handler's own <c>BEGIN</c>/<c>COMMIT</c>/<c>ROLLBACK</c> tracking feeds that evaluator
/// correctly.
/// </summary>
public sealed class ScriptedHranaHandlerConditionTests
{
    private static readonly IReadOnlyList<ScriptedHranaHandler.BatchStepOutcome> NoSteps = [];

    [Test]
    public void Ok_IsTrue_OnlyWhenTheReferencedStepSucceeded()
    {
        var steps = new[]
        {
            ScriptedHranaHandler.BatchStepOutcome.Succeeded,
            ScriptedHranaHandler.BatchStepOutcome.Failed,
            ScriptedHranaHandler.BatchStepOutcome.Skipped,
        };

        Evaluate(StepCondition("ok", 0), steps).Should().BeTrue();
        Evaluate(StepCondition("ok", 1), steps).Should().BeFalse();
        Evaluate(StepCondition("ok", 2), steps).Should().BeFalse();
    }

    [Test]
    public void Error_IsTrue_OnlyWhenTheReferencedStepActuallyFailed_NotWhenItWasSkipped()
    {
        // This is the tri-state fix itself: Hrana's "error" condition means "this step actually
        // ran and failed" — a step that never ran because an earlier guard skipped it is neither
        // ok nor error. Collapsing "skipped" into "not succeeded" (a boolean model) would make
        // this assertion wrongly true for the skipped step.
        var steps = new[]
        {
            ScriptedHranaHandler.BatchStepOutcome.Succeeded,
            ScriptedHranaHandler.BatchStepOutcome.Failed,
            ScriptedHranaHandler.BatchStepOutcome.Skipped,
        };

        Evaluate(StepCondition("error", 0), steps).Should().BeFalse();
        Evaluate(StepCondition("error", 1), steps).Should().BeTrue();
        Evaluate(StepCondition("error", 2), steps).Should().BeFalse("a skipped step never actually errored");
    }

    [Test]
    public void Ok_And_Error_AreBothFalse_ForAStepIndexThatHasNotHappenedYet()
    {
        Evaluate(StepCondition("ok", 5), NoSteps).Should().BeFalse();
        Evaluate(StepCondition("error", 5), NoSteps).Should().BeFalse();
    }

    [Test]
    public void Not_NegatesItsInnerCondition()
    {
        var steps = new[] { ScriptedHranaHandler.BatchStepOutcome.Succeeded };

        Evaluate(Not(StepCondition("ok", 0)), steps).Should().BeFalse();
        Evaluate(Not(StepCondition("error", 0)), steps).Should().BeTrue();
    }

    [Test]
    public void And_IsTrue_OnlyWhenEveryOperandIsTrue()
    {
        var steps = new[] { ScriptedHranaHandler.BatchStepOutcome.Succeeded, ScriptedHranaHandler.BatchStepOutcome.Failed };

        Evaluate(And(StepCondition("ok", 0), StepCondition("ok", 0)), steps).Should().BeTrue();
        Evaluate(And(StepCondition("ok", 0), StepCondition("ok", 1)), steps).Should().BeFalse();
    }

    [Test]
    public void Or_IsTrue_WhenAtLeastOneOperandIsTrue()
    {
        var steps = new[] { ScriptedHranaHandler.BatchStepOutcome.Succeeded, ScriptedHranaHandler.BatchStepOutcome.Failed };

        Evaluate(Or(StepCondition("ok", 1), StepCondition("error", 1)), steps).Should().BeTrue();
        Evaluate(Or(StepCondition("ok", 1), StepCondition("ok", 0)), steps).Should().BeTrue();
        Evaluate(Or(StepCondition("error", 0), StepCondition("ok", 1)), steps).Should().BeFalse();
    }

    [Test]
    public void IsAutocommit_ReflectsTheSuppliedFlagDirectly()
    {
        Evaluate(IsAutocommit(), NoSteps, isAutocommit: true).Should().BeTrue();
        Evaluate(IsAutocommit(), NoSteps, isAutocommit: false).Should().BeFalse();
    }

    [Test]
    public void NotIsAutocommit_GuardedReplicaPushShape_MatchesRealConditionSerialization()
    {
        // Mirrors a real embedded-replica push's likely guard shape: "the setup step
        // succeeded, AND we are inside an open transaction" — built with the actual
        // AhtolaRemoteBatchCondition factory methods (not hand-rolled JSON), converted to the
        // exact wire shape AhtolaRemoteClient sends, and evaluated both while autocommit and
        // while inside a transaction.
        var condition = AhtolaRemoteBatchCondition.And(
            AhtolaRemoteBatchCondition.StepSucceeded(0),
            AhtolaRemoteBatchCondition.Not(AhtolaRemoteBatchCondition.IsAutocommit));
        var steps = new[] { ScriptedHranaHandler.BatchStepOutcome.Succeeded };

        ScriptedHranaHandler.EvaluateBatchCondition(ToJsonElement(condition), steps, isAutocommit: false)
            .Should().BeTrue("step 0 succeeded and a transaction is open (not autocommit)");
        ScriptedHranaHandler.EvaluateBatchCondition(ToJsonElement(condition), steps, isAutocommit: true)
            .Should().BeFalse("autocommit mode means no transaction is open, so the guard must block the step");
    }

    [Test]
    public async Task Handler_TracksAutocommitThroughBeginCommitRollback_GatingALaterGuardedStep()
    {
        // End-to-end proof (not just the isolated evaluator above): the handler's own
        // BEGIN/COMMIT tracking must feed EvaluateBatchCondition correctly. Batch shape:
        //   0: BEGIN                                            (autocommit -> false)
        //   1: INSERT ... guarded by Not(IsAutocommit)           -> must run (false -> true)
        //   2: COMMIT                                            (autocommit -> true)
        //   3: INSERT ... guarded by Not(IsAutocommit)           -> must be skipped
        using var handler = new ScriptedHranaHandler();
        using var httpClient = new HttpClient(handler);
        using var client = new AhtolaRemoteClient(httpClient, new Uri("https://database.example"), authToken: null);

        var commands = new AhtolaBatchCommand[]
        {
            new("BEGIN"),
            new("INSERT INTO t VALUES (1)") { RemoteCondition = AhtolaRemoteBatchCondition.Not(AhtolaRemoteBatchCondition.IsAutocommit) },
            new("COMMIT"),
            new("INSERT INTO t VALUES (2)") { RemoteCondition = AhtolaRemoteBatchCondition.Not(AhtolaRemoteBatchCondition.IsAutocommit) },
        };

        await client.ExecuteBatchAsync(commands, commandTimeout: 30, wantRows: false, closeAfter: true, CancellationToken.None);

        handler.SqlLog.Should().Equal("BEGIN", "INSERT INTO t VALUES (1)", "COMMIT");
        handler.SqlLog.Should().NotContain("INSERT INTO t VALUES (2)", "autocommit was true again after COMMIT, so the guard must block this step");
        handler.IsAutocommit.Should().BeTrue("the batch ended after a COMMIT with no further BEGIN");
    }

    private static bool Evaluate(
        JsonElement condition,
        IReadOnlyList<ScriptedHranaHandler.BatchStepOutcome> stepOutcomes,
        bool isAutocommit = true)
        => ScriptedHranaHandler.EvaluateBatchCondition(condition, stepOutcomes, isAutocommit);

    private static JsonElement StepCondition(string type, int step)
        => Parse($$"""{"type":"{{type}}","step":{{step}}}""");

    private static JsonElement Not(JsonElement inner)
        => Parse($$"""{"type":"not","cond":{{inner.GetRawText()}}}""");

    private static JsonElement And(params JsonElement[] operands)
        => Parse($$"""{"type":"and","conds":[{{string.Join(",", operands.Select(static o => o.GetRawText()))}}]}""");

    private static JsonElement Or(params JsonElement[] operands)
        => Parse($$"""{"type":"or","conds":[{{string.Join(",", operands.Select(static o => o.GetRawText()))}}]}""");

    private static JsonElement IsAutocommit()
        => Parse("""{"type":"is_autocommit"}""");

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    /// <summary>Converts a real <see cref="AhtolaRemoteBatchCondition"/> into the exact JSON
    /// shape <c>AhtolaRemoteClient</c> sends over the wire (see
    /// <see cref="RemoteBatchConditionTests"/> for the confirmed wire shapes), so this test
    /// exercises the evaluator against production serialization rather than a hand-rolled
    /// approximation of it.</summary>
    private static JsonElement ToJsonElement(AhtolaRemoteBatchCondition condition)
        => JsonDocument.Parse(ToJsonNode(condition).ToJsonString()).RootElement;

    private static JsonNode ToJsonNode(AhtolaRemoteBatchCondition condition)
    {
        var node = new JsonObject { ["type"] = condition.Type };
        if (condition.Step is { } step)
            node["step"] = step;
        if (condition.Operand is { } operand)
            node["cond"] = ToJsonNode(operand);
        if (condition.Operands is { } operands)
            node["conds"] = new JsonArray(operands.Select(ToJsonNode).ToArray());

        return node;
    }
}
