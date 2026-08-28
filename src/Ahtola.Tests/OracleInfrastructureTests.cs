using AwesomeAssertions;
using Ahtola.Tests.Oracle;

namespace Ahtola.Tests;

public sealed class OracleInfrastructureTests
{
    [Test]
    public void StablePrngMatchesItsCrossRuntimeGoldenSequence()
    {
        var random = new StablePrng(0x0123456789abcdefUL);

        new[] { random.NextUInt64(), random.NextUInt64(), random.NextUInt64(), random.NextUInt64() }
            .Should().Equal(
                0x157a3807a48faa9dUL,
                0xd573529b34a1d093UL,
                0x2f90b72e996dccbeUL,
                0xa2d419334c4667ecUL);
    }

    [Test]
    public void ReplayTraceRoundTripsWithoutLosingSqlOrSeedDiagnostics()
    {
        var stream = StableTestSeed.Create(42).Derive("trace-round-trip");
        var trace = ReplayTrace.Create(TestContext.CurrentContext.Test.Name, stream);
        trace.Add("SELECT 1;", "typed ordered differential");
        trace.AddScheduleStep(new CooperativeScheduleStep(
            0,
            1,
            7,
            "reader",
            "reader.before-read",
            0,
            [2, 7],
            3));

        var restored = ReplayTrace.FromJson(trace.ToJson());

        restored.RootSeed.Should().Be(stream.RootSeed, because: stream.Diagnostics);
        restored.StreamSeed.Should().Be(stream.Seed, because: stream.Diagnostics);
        restored.SeedDiagnostics.Should().Be(stream.Diagnostics);
        restored.Operations.Should().Equal(trace.Operations);
        restored.ScheduleChoices.Should().Equal(1);
        restored.Schedule.Should().BeEquivalentTo(trace.Schedule, options => options.WithStrictOrdering());
        restored.ToSql().Should().Contain("SELECT 1;")
            .And.Contain("AHTOLA_TEST_SEED")
            .And.Contain("schedule choices=[1]")
            .And.Contain("yield=reader.before-read#0");
    }

    [Test]
    public void UnorderedComparisonDetectsMissingDuplicateRows()
    {
        var duplicate = new OracleRow([OracleValue.Integer(7), OracleValue.Text("same")]);
        var twoRows = OracleExecutionResult.Success(true, ["value", "label"], [duplicate, duplicate]);
        var oneRow = OracleExecutionResult.Success(true, ["value", "label"], [duplicate]);

        var error = Assert.Throws<AssertionException>(
            () => TypedSqliteOracle.AssertEquivalent(twoRows, oneRow, ordered: false, "multiplicity probe"));

        error!.Message.Should().Contain("row-bag mismatch").And.Contain("=> 1");
    }
}
