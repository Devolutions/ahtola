using AwesomeAssertions;
using Ahtola.Tests.Oracle;

namespace Ahtola.Tests;

public sealed class DeterministicCooperativeSchedulerTests
{
    [Test]
    public async Task ChoiceVectorReplaysTheSameNamedYieldTrace()
    {
        var random = new StablePrng(0x5cedUL);
        var first = await RunTwoActorProbeAsync((_, enabled) => random.NextInt32(enabled));
        first.EnsureSuccessful();

        var replay = await RunTwoActorProbeAsync(replayChoices: first.Choices);
        replay.EnsureSuccessful();

        replay.Steps.Select(Identity).Should().Equal(first.Steps.Select(Identity));
        replay.Choices.Should().Equal(first.Choices);

        var stream = StableTestSeed.Create(0x5cedUL).Derive("cooperative-replay");
        var trace = ReplayTrace.Create(TestContext.CurrentContext.Test.Name, stream);
        foreach (var step in first.Steps)
            trace.AddScheduleStep(step);

        var restored = ReplayTrace.FromJson(trace.ToJson());
        restored.ScheduleChoices.Should().Equal(first.Choices);
        restored.Schedule.Should().BeEquivalentTo(trace.Schedule, options => options.WithStrictOrdering());
        restored.ToSql().Should().Contain("schedule choices=[").And.Contain("yield=right.first#0");
    }

    [Test]
    public async Task DepthFirstExplorerEnumeratesEverySmallInterleaving()
    {
        var exploration = await CooperativeScheduleExplorer.ExploreDepthFirstAsync(
            prefix => RunTwoActorProbeAsync(replayChoices: prefix, allowReplayPrefix: true),
            maxSchedules: 8);

        exploration.Exhaustive.Should().BeTrue();
        exploration.Runs.Should().HaveCount(6, because: "two actors with two yield points have 4!/(2!*2!) schedules");
        exploration.Runs.Should().OnlyContain(static run => run.CompletedSuccessfully);
        exploration.Runs.Select(static run => string.Join(",", run.Steps.Select(step => step.ActorId)))
            .Should().OnlyHaveUniqueItems();
    }

    [Test]
    public async Task ObserverReceivesStartCrashAndFinalizationExactlyOnce()
    {
        var observer = new RecordingObserver();
        var scheduler = new DeterministicCooperativeScheduler(observers: observer);
        scheduler.AddActor("healthy", async actor =>
        {
            await actor.YieldAsync("healthy.ready");
            actor.NoteProgress();
        });
        scheduler.AddActor("crash", async actor =>
        {
            await actor.YieldAsync("crash.ready");
            throw new InvalidOperationException("synthetic actor crash");
        });

        var result = await scheduler.RunAsync();

        result.Failure.Should().BeOfType<InvalidOperationException>();
        observer.Started.Should().BeEquivalentTo(["healthy", "crash"]);
        observer.Finished.Should().ContainSingle().Which.Should().Be("healthy");
        observer.Crashed.Should().ContainSingle().Which.Should().Be("crash");
        observer.Finalized.Should().Be(1);
    }

    [Test]
    public async Task MaxStepBoundReportsCooperativeLivelock()
    {
        var scheduler = new DeterministicCooperativeScheduler(
            maxSteps: 4,
            maxStepsWithoutProgress: 4);
        scheduler.AddActor("spinner", async actor =>
        {
            while (true)
                await actor.YieldAsync("spinner.no-progress");
        });

        var result = await scheduler.RunAsync();

        result.Failure.Should().BeOfType<CooperativeLivelockException>();
        result.Steps.Should().HaveCount(4);
        result.Actors.Should().ContainSingle().Which.Completed.Should().BeFalse();
    }

    private static Task<CooperativeScheduleResult> RunTwoActorProbeAsync(
        Func<int, int, int>? choose = null,
        IReadOnlyList<int>? replayChoices = null,
        bool allowReplayPrefix = false)
    {
        var scheduler = new DeterministicCooperativeScheduler();
        scheduler.AddActor("left", async actor =>
        {
            await actor.YieldAsync("left.first");
            actor.NoteProgress();
            await actor.YieldAsync("left.second");
            actor.NoteProgress();
        });
        scheduler.AddActor("right", async actor =>
        {
            await actor.YieldAsync("right.first");
            actor.NoteProgress();
            await actor.YieldAsync("right.second");
            actor.NoteProgress();
        });
        return scheduler.RunAsync(replayChoices, choose, allowReplayPrefix);
    }

    private static string Identity(CooperativeScheduleStep step)
        => $"{step.ActorId}:{step.ActorName}:{step.YieldPoint}:{step.YieldOrdinal}";

    private sealed class RecordingObserver : CooperativeScheduleObserver
    {
        private readonly object _gate = new();

        internal List<string> Started { get; } = [];

        internal List<string> Finished { get; } = [];

        internal List<string> Crashed { get; } = [];

        internal int Finalized { get; private set; }

        public override void OnStart(CooperativeActorInfo actor)
        {
            lock (_gate)
                Started.Add(actor.Name);
        }

        public override void OnFinish(CooperativeActorResult actor)
        {
            lock (_gate)
                Finished.Add(actor.Actor.Name);
        }

        public override void OnCrash(CooperativeActorInfo actor, Exception exception)
        {
            lock (_gate)
                Crashed.Add(actor.Name);
        }

        public override void FinalizeRun(CooperativeScheduleResult result)
        {
            lock (_gate)
                Finalized++;
        }
    }
}
