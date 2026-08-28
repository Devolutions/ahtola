namespace Ahtola.Tests;

internal sealed record CooperativeActorInfo(int Id, string Name);

internal sealed record CooperativeScheduleStep(
    int Step,
    int Choice,
    int ActorId,
    string ActorName,
    string YieldPoint,
    int YieldOrdinal,
    IReadOnlyList<int> EnabledActors,
    long ProgressCount);

internal sealed record CooperativeActorResult(
    CooperativeActorInfo Actor,
    bool Completed,
    int YieldCount,
    long ProgressCount,
    Exception? Crash);

internal sealed class CooperativeScheduleResult
{
    internal CooperativeScheduleResult(
        IReadOnlyList<CooperativeScheduleStep> steps,
        IReadOnlyList<CooperativeActorResult> actors,
        Exception? failure)
    {
        Steps = steps;
        Actors = actors;
        Failure = failure;
    }

    internal IReadOnlyList<CooperativeScheduleStep> Steps { get; }

    internal IReadOnlyList<CooperativeActorResult> Actors { get; }

    internal Exception? Failure { get; }

    internal IReadOnlyList<int> Choices => [.. Steps.Select(static step => step.Choice)];

    internal bool CompletedSuccessfully =>
        Failure is null
        && Actors.Count > 0
        && Actors.All(static actor => actor.Completed && actor.Crash is null);

    internal void EnsureSuccessful()
    {
        if (Failure is not null)
            throw new AssertionException($"Cooperative schedule failed: {Failure.Message}", Failure);
        if (!CompletedSuccessfully)
            throw new AssertionException("Cooperative schedule did not complete every actor exactly once.");
    }
}

internal interface ICooperativeScheduleObserver
{
    void OnStart(CooperativeActorInfo actor);

    void OnFinish(CooperativeActorResult actor);

    void OnCrash(CooperativeActorInfo actor, Exception exception);

    void FinalizeRun(CooperativeScheduleResult result);
}

internal abstract class CooperativeScheduleObserver : ICooperativeScheduleObserver
{
    public virtual void OnStart(CooperativeActorInfo actor)
    {
    }

    public virtual void OnFinish(CooperativeActorResult actor)
    {
    }

    public virtual void OnCrash(CooperativeActorInfo actor, Exception exception)
    {
    }

    public virtual void FinalizeRun(CooperativeScheduleResult result)
    {
    }
}

internal sealed class CooperativeActorContext
{
    private readonly DeterministicCooperativeScheduler _scheduler;
    private readonly DeterministicCooperativeScheduler.ActorState _state;

    internal CooperativeActorContext(
        DeterministicCooperativeScheduler scheduler,
        DeterministicCooperativeScheduler.ActorState state)
    {
        _scheduler = scheduler;
        _state = state;
    }

    internal int ActorId => _state.Info.Id;

    internal string ActorName => _state.Info.Name;

    internal CancellationToken CancellationToken => _scheduler.CancellationToken;

    internal ValueTask YieldAsync(string name)
        => _scheduler.YieldAsync(_state, name);

    internal void NoteProgress()
        => _scheduler.NoteProgress(_state);
}

/// <summary>
/// Test-only scheduler for async actors that cooperate at explicit, named yield
/// points. It intentionally does not attempt to intercept CLR synchronization.
/// </summary>
internal sealed class DeterministicCooperativeScheduler
{
    private static readonly TimeSpan StateChangeTimeout = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly int _maxSteps;
    private readonly int _maxStepsWithoutProgress;
    private readonly IReadOnlyList<ICooperativeScheduleObserver> _observers;
    private readonly List<ActorState> _actors = [];
    private readonly List<CooperativeScheduleStep> _steps = [];
    private readonly SemaphoreSlim _stateChanged = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private Exception? _observerFailure;
    private long _progressCount;
    private bool _started;

    internal DeterministicCooperativeScheduler(
        int maxSteps = 256,
        int maxStepsWithoutProgress = 64,
        params ICooperativeScheduleObserver[] observers)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSteps);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxStepsWithoutProgress);
        _maxSteps = maxSteps;
        _maxStepsWithoutProgress = maxStepsWithoutProgress;
        _observers = observers ?? throw new ArgumentNullException(nameof(observers));
    }

    internal CancellationToken CancellationToken => _shutdown.Token;

    internal void AddActor(string name, Func<CooperativeActorContext, Task> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(action);
        if (_started)
            throw new InvalidOperationException("Actors cannot be added after the schedule starts.");
        if (_actors.Any(actor => string.Equals(actor.Info.Name, name, StringComparison.Ordinal)))
            throw new ArgumentException($"An actor named '{name}' is already registered.", nameof(name));

        _actors.Add(new ActorState(new CooperativeActorInfo(_actors.Count, name), action));
    }

    internal async Task<CooperativeScheduleResult> RunAsync(
        IReadOnlyList<int>? replayChoices = null,
        Func<int, int, int>? choose = null,
        bool allowReplayPrefix = false)
    {
        if (_started)
            throw new InvalidOperationException("A cooperative scheduler can only run once.");
        if (_actors.Count == 0)
            throw new InvalidOperationException("At least one actor is required.");
        if (replayChoices is not null && choose is not null)
            throw new ArgumentException("A replay vector and a choice function are mutually exclusive.");

        _started = true;
        foreach (var actor in _actors)
            actor.Task = ExecuteActorAsync(actor);

        Exception? failure = null;
        var stepsWithoutProgress = 0;
        var lastProgress = 0L;
        try
        {
            while (true)
            {
                await WaitForQuiescenceAsync().ConfigureAwait(false);

                ActorState[] waiting;
                lock (_gate)
                {
                    failure = _observerFailure
                        ?? _actors.Select(static actor => actor.Crash).FirstOrDefault(static crash => crash is not null);
                    if (failure is not null)
                        break;
                    if (_actors.All(static actor => actor.Status == ActorStatus.Completed))
                    {
                        if (!allowReplayPrefix
                            && replayChoices is { } replay
                            && replay.Count != _steps.Count)
                        {
                            failure = new InvalidOperationException(
                                $"Replay supplied {replay.Count} choices but the schedule completed after {_steps.Count} steps.");
                        }
                        break;
                    }

                    waiting = [.. _actors
                        .Where(static actor => actor.Status == ActorStatus.Waiting)
                        .OrderBy(static actor => actor.Info.Id)];
                }

                if (_steps.Count >= _maxSteps)
                {
                    failure = new CooperativeLivelockException(
                        $"Schedule exceeded the {_maxSteps}-step bound without completing.");
                    break;
                }

                var choice = replayChoices is { } choices && _steps.Count < choices.Count
                    ? choices[_steps.Count]
                    : choose?.Invoke(_steps.Count, waiting.Length) ?? 0;
                if ((uint)choice >= (uint)waiting.Length)
                {
                    failure = new InvalidOperationException(
                        $"Schedule choice {choice} at step {_steps.Count} is outside the enabled range [0, {waiting.Length}).");
                    break;
                }

                var selected = waiting[choice];
                ResumeRequest request;
                lock (_gate)
                {
                    request = selected.Waiting
                        ?? throw new InvalidOperationException($"Actor {selected.Info.Name} is no longer waiting.");
                    selected.Waiting = null;
                    selected.Status = ActorStatus.Running;
                    _steps.Add(new CooperativeScheduleStep(
                        _steps.Count,
                        choice,
                        selected.Info.Id,
                        selected.Info.Name,
                        request.Name,
                        request.Ordinal,
                        [.. waiting.Select(static actor => actor.Info.Id)],
                        _progressCount));
                }

                request.Resume.TrySetResult();
                await WaitForActorTransitionAsync(selected).ConfigureAwait(false);

                var progress = Interlocked.Read(ref _progressCount);
                if (progress == lastProgress)
                {
                    stepsWithoutProgress++;
                    if (stepsWithoutProgress >= _maxStepsWithoutProgress)
                    {
                        failure = new CooperativeLivelockException(
                            $"Schedule made no reported progress for {stepsWithoutProgress} consecutive steps.");
                        break;
                    }
                }
                else
                {
                    lastProgress = progress;
                    stepsWithoutProgress = 0;
                }
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (failure is not null)
            await StopActorsAsync().ConfigureAwait(false);

        var result = CreateResult(failure ?? _observerFailure);
        Notify(static (observer, state) => observer.FinalizeRun(state), result);
        if (_observerFailure is not null && result.Failure is null)
            result = CreateResult(_observerFailure);
        return result;
    }

    internal ValueTask YieldAsync(ActorState actor, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _shutdown.Token.ThrowIfCancellationRequested();

        TaskCompletionSource resume;
        lock (_gate)
        {
            if (actor.Status != ActorStatus.Running)
                throw new InvalidOperationException($"Actor {actor.Info.Name} attempted to yield while {actor.Status}.");

            resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            actor.Waiting = new ResumeRequest(name, actor.YieldCount++, resume);
            actor.Status = ActorStatus.Waiting;
        }

        _stateChanged.Release();
        return new ValueTask(resume.Task.WaitAsync(_shutdown.Token));
    }

    internal void NoteProgress(ActorState actor)
    {
        lock (_gate)
        {
            if (actor.Status == ActorStatus.Completed || actor.Status == ActorStatus.Crashed)
                throw new InvalidOperationException($"Actor {actor.Info.Name} reported progress after completion.");
            actor.ProgressCount++;
            Interlocked.Increment(ref _progressCount);
        }
    }

    private async Task ExecuteActorAsync(ActorState actor)
    {
        Notify(static (observer, state) => observer.OnStart(state), actor.Info);
        try
        {
            await actor.Action(new CooperativeActorContext(this, actor)).ConfigureAwait(false);
            lock (_gate)
            {
                if (actor.Status == ActorStatus.Completed || actor.Status == ActorStatus.Crashed)
                    throw new InvalidOperationException($"Actor {actor.Info.Name} completed more than once.");
                actor.Status = ActorStatus.Completed;
                actor.CompletionCount++;
            }

            Notify(
                static (observer, state) => observer.OnFinish(state),
                CreateActorResult(actor));
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            lock (_gate)
                actor.Status = ActorStatus.Canceled;
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                actor.Status = ActorStatus.Crashed;
                actor.Crash = exception;
            }
            Notify(
                static (observer, state) => observer.OnCrash(state.Actor, state.Exception),
                (Actor: actor.Info, Exception: exception));
        }
        finally
        {
            _stateChanged.Release();
        }
    }

    private async Task WaitForQuiescenceAsync()
    {
        while (true)
        {
            lock (_gate)
            {
                if (_actors.All(static actor => actor.Status != ActorStatus.Running))
                    return;
            }

            if (!await _stateChanged.WaitAsync(StateChangeTimeout).ConfigureAwait(false))
            {
                throw new CooperativeLivelockException(
                    "Actors did not reach another named yield point or terminal state within the bounded wait.");
            }
        }
    }

    private async Task WaitForActorTransitionAsync(ActorState actor)
    {
        while (true)
        {
            lock (_gate)
            {
                if (actor.Status != ActorStatus.Running)
                    return;
            }

            if (!await _stateChanged.WaitAsync(StateChangeTimeout).ConfigureAwait(false))
            {
                throw new CooperativeLivelockException(
                    $"Actor {actor.Info.Name} did not reach another named yield point or terminal state.");
            }
        }
    }

    private async Task StopActorsAsync()
    {
        _shutdown.Cancel();
        TaskCompletionSource[] pending;
        lock (_gate)
        {
            pending = [.. _actors
                .Where(static actor => actor.Waiting is not null)
                .Select(static actor => actor.Waiting!.Resume)];
        }
        foreach (var completion in pending)
            completion.TrySetCanceled(_shutdown.Token);

        var tasks = _actors.Select(static actor => actor.Task!).ToArray();
        await Task.WhenAll(tasks).WaitAsync(StateChangeTimeout).ConfigureAwait(false);
    }

    private CooperativeScheduleResult CreateResult(Exception? failure)
        => new(
            [.. _steps],
            [.. _actors.Select(CreateActorResult)],
            failure);

    private static CooperativeActorResult CreateActorResult(ActorState actor)
        => new(
            actor.Info,
            actor.Status == ActorStatus.Completed && actor.CompletionCount == 1,
            actor.YieldCount,
            actor.ProgressCount,
            actor.Crash);

    private void Notify<TState>(
        Action<ICooperativeScheduleObserver, TState> callback,
        TState state)
    {
        foreach (var observer in _observers)
        {
            try
            {
                callback(observer, state);
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref _observerFailure, exception, null);
            }
        }
    }

    internal sealed class ActorState(
        CooperativeActorInfo info,
        Func<CooperativeActorContext, Task> action)
    {
        internal CooperativeActorInfo Info { get; } = info;

        internal Func<CooperativeActorContext, Task> Action { get; } = action;

        internal ActorStatus Status { get; set; } = ActorStatus.Running;

        internal ResumeRequest? Waiting { get; set; }

        internal Task? Task { get; set; }

        internal Exception? Crash { get; set; }

        internal int YieldCount { get; set; }

        internal long ProgressCount { get; set; }

        internal int CompletionCount { get; set; }
    }

    internal sealed record ResumeRequest(string Name, int Ordinal, TaskCompletionSource Resume);

    internal enum ActorStatus
    {
        Running,
        Waiting,
        Completed,
        Crashed,
        Canceled,
    }
}

internal sealed class CooperativeLivelockException(string message) : TimeoutException(message);

internal sealed record CooperativeExplorationResult(
    IReadOnlyList<CooperativeScheduleResult> Runs,
    bool Exhaustive);

internal static class CooperativeScheduleExplorer
{
    internal static async Task<CooperativeExplorationResult> ExploreDepthFirstAsync(
        Func<IReadOnlyList<int>, Task<CooperativeScheduleResult>> run,
        int maxSchedules)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSchedules);

        var pending = new Stack<int[]>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { string.Empty };
        var results = new List<CooperativeScheduleResult>();
        pending.Push([]);

        while (pending.Count > 0 && results.Count < maxSchedules)
        {
            var prefix = pending.Pop();
            var result = await run(prefix).ConfigureAwait(false);
            results.Add(result);
            result.EnsureSuccessful();

            for (var step = result.Steps.Count - 1; step >= prefix.Length; step--)
            {
                var decision = result.Steps[step];
                for (var alternative = decision.EnabledActors.Count - 1; alternative >= 1; alternative--)
                {
                    var candidate = result.Choices.Take(step).Append(alternative).ToArray();
                    var key = string.Join(",", candidate);
                    if (seen.Add(key))
                        pending.Push(candidate);
                }
            }
        }

        return new CooperativeExplorationResult(results, pending.Count == 0);
    }
}
