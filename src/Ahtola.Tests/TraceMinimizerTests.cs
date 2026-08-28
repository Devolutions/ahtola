using AwesomeAssertions;
using Ahtola.Tests.Oracle;

namespace Ahtola.Tests;

public sealed class TraceMinimizerTests
{
    [Test]
    public void DdminRemovesIrrelevantOperationsAndTheirDependents()
    {
        var operations = new[]
        {
            Operation(0, "setup", "CREATE TABLE t(id INTEGER PRIMARY KEY);"),
            Operation(1, "begin", "BEGIN;", dependencies: [0]),
            Operation(2, "observe", "SELECT * FROM t;", dependencies: [0]),
            Operation(3, "insert", "INSERT INTO t VALUES (13);", dependencies: [0, 1]),
            Operation(4, "savepoint", "SAVEPOINT irrelevant;", dependencies: [0, 1]),
            Operation(5, "update", "UPDATE t SET id = 99;", dependencies: [0, 1, 4]),
            Operation(6, "fault", "SELECT synthetic_failure FROM t;", dependencies: [0, 1, 3]),
            Operation(7, "commit", "COMMIT;", dependencies: [0, 1]),
        };

        var minimized = DependencyAwareTraceMinimizer.Minimize(operations, SyntheticFailure);

        minimized.Select(static operation => operation.Action)
            .Should().Equal("setup", "begin", "insert", "fault");
        DependencyAwareTraceMinimizer.ValidateDependencies(minimized);
        SyntheticFailure(minimized).Should().Be("SyntheticFailure:row=13");
    }

    [Test]
    public void ReplayTraceRoundTripsActorActionAndDependencies()
    {
        var stream = StableTestSeed.Create(91).Derive("dependency-round-trip");
        var trace = ReplayTrace.Create(TestContext.CurrentContext.Test.Name, stream);
        trace.Add("CREATE TABLE t(id INTEGER);", actor: 0, action: "setup");
        trace.Add("BEGIN;", actor: 1, action: "begin", dependencies: [0]);

        var restored = ReplayTrace.FromJson(trace.ToJson());

        restored.Operations[1].Actor.Should().Be(1);
        restored.Operations[1].Action.Should().Be("begin");
        restored.Operations[1].Dependencies.Should().Equal(0);
        restored.ToSql().Should().Contain("actor=1").And.Contain("depends=[0]");
    }

    [Test]
    public void FailureFingerprintNormalizesVolatileNumbersAndIdentifiers()
    {
        var first = new InvalidOperationException(
            "operation 19 failed at 0xCAFE for 70b8f95f-46a2-4af2-aacd-ab83dc78d1e1");
        var second = new InvalidOperationException(
            "operation 41 failed at 0xBEEF for 8b1a9953-c461-4f9a-827d-0d01bf54c2f4");

        DependencyAwareTraceMinimizer.NormalizeFailure(first)
            .Should().Be(DependencyAwareTraceMinimizer.NormalizeFailure(second));
    }

    [Test]
    [NonParallelizable]
    public void StableSeedHonorsHexEnvironmentOverride()
    {
        const string variable = "AHTOLA_TEST_SEED";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "0xdecafbad");
            var seed = StableTestSeed.Create(1);

            seed.RootSeed.Should().Be(0xdecafbadUL);
            seed.Source.Should().Be(variable);
            seed.Derive("override").Diagnostics.Should().Contain($"{variable}=3737844653");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    private static ReplayOperation Operation(
        int id,
        string action,
        string sql,
        int actor = 0,
        params int[] dependencies)
        => new(id, sql, "synthetic", true, actor, action, dependencies);

    private static string? SyntheticFailure(IReadOnlyList<ReplayOperation> operations)
    {
        var ids = operations.Select(static operation => operation.Index).ToHashSet();
        if (operations.Any(static operation => operation.Action == "fault")
            && operations.Any(static operation =>
                operation.Action == "insert" && operation.Sql.Contains("13", StringComparison.Ordinal))
            && operations.All(operation => (operation.Dependencies ?? []).All(ids.Contains)))
        {
            return "SyntheticFailure:row=13";
        }

        return null;
    }
}
