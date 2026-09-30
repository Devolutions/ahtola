namespace Ahtola;

/// <summary>
/// Selects what the managed embedded-replica automatic synchronization loop
/// (<c>Sync Interval</c>) does on each tick.
/// </summary>
/// <remarks>
/// Ahtola's historical behavior, and still the default, is <see cref="PushAndPull"/>: every tick
/// runs a full <see cref="AhtolaConnection.SyncAsync(AhtolaSyncOptions, CancellationToken)"/>.
/// Turso's .NET binding runs a pull-only loop instead (<c>TursoAutomaticSyncCoordinator</c> calls
/// <c>PullAsync</c>); select <see cref="PullOnly"/> to match it and push local changes explicitly
/// with <see cref="AhtolaConnection.PushAsync(CancellationToken)"/>.
/// </remarks>
public enum AhtolaAutomaticSyncMode
{
    /// <summary>Push local changes, then pull and apply remote changes (the default).</summary>
    PushAndPull,

    /// <summary>Only pull and apply remote changes; local changes are pushed explicitly.</summary>
    PullOnly,
}

/// <summary>
/// Identifies the current phase of the managed embedded-replica automatic synchronization loop.
/// </summary>
public enum AhtolaAutomaticSyncState
{
    /// <summary>No automatic synchronization loop is running.</summary>
    Stopped,

    /// <summary>The loop is waiting for its next scheduled attempt.</summary>
    Waiting,

    /// <summary>A scheduled attempt is in flight.</summary>
    Running,

    /// <summary>A transient failure is being retried.</summary>
    Retrying,

    /// <summary>
    /// A non-retryable failure (or the last retry) stopped the loop. The failure is available in
    /// <see cref="AhtolaAutomaticSyncStatus.LastException"/> and is rethrown by
    /// <see cref="AhtolaConnection.Close"/>.
    /// </summary>
    Faulted,
}

/// <summary>
/// An immutable snapshot of the managed embedded-replica automatic synchronization loop.
/// </summary>
/// <param name="State">The current loop phase.</param>
/// <param name="Attempt">The 1-based attempt number of the current tick, or 0 while idle.</param>
/// <param name="LastAttempt">When the most recent attempt started.</param>
/// <param name="LastSuccess">When the most recent attempt completed successfully.</param>
/// <param name="LastPullAppliedChanges">
/// Whether the most recent successful attempt applied remote changes, or <see langword="null"/>
/// before the first success.
/// </param>
/// <param name="LastException">The failure of the most recent failed attempt, if any.</param>
/// <param name="NextAttempt">When the next attempt is scheduled, if one is.</param>
public sealed record AhtolaAutomaticSyncStatus(
    AhtolaAutomaticSyncState State,
    int Attempt,
    DateTimeOffset? LastAttempt,
    DateTimeOffset? LastSuccess,
    bool? LastPullAppliedChanges,
    Exception? LastException,
    DateTimeOffset? NextAttempt)
{
    /// <summary>The status of a connection that has no automatic synchronization loop.</summary>
    public static AhtolaAutomaticSyncStatus Stopped { get; } = new(
        AhtolaAutomaticSyncState.Stopped,
        Attempt: 0,
        LastAttempt: null,
        LastSuccess: null,
        LastPullAppliedChanges: null,
        LastException: null,
        NextAttempt: null);
}

/// <summary>
/// Carries the new status for <see cref="AhtolaConnection.AutomaticSyncStatusChanged"/>.
/// </summary>
public sealed class AhtolaAutomaticSyncStatusChangedEventArgs(AhtolaAutomaticSyncStatus status) : EventArgs
{
    /// <summary>Gets the status that was just published.</summary>
    public AhtolaAutomaticSyncStatus Status { get; } = status;
}

/// <summary>
/// Runs one connection's managed embedded-replica automatic synchronization loop and publishes
/// every phase change. Mirrors Turso's <c>TursoAutomaticSyncCoordinator</c>
/// (<c>turso-src/bindings/dotnet/src/Turso.Data/TursoAutomaticSyncCoordinator.cs</c>), with the
/// tick operation selected by <see cref="AhtolaAutomaticSyncMode"/> and Ahtola's own bounded
/// transient-retry policy.
/// </summary>
internal sealed class ManagedReplicaAutomaticSyncCoordinator
{
    private readonly Func<CancellationToken, Task<AhtolaSyncResult>> _operation;
    private readonly TimeSpan _interval;
    private readonly Action<AhtolaAutomaticSyncStatus> _publish;
    private readonly CancellationTokenSource _cancellation = new();
    private AhtolaAutomaticSyncStatus _status;
    private Task? _task;
    private int _stopped;

    public ManagedReplicaAutomaticSyncCoordinator(
        Func<CancellationToken, Task<AhtolaSyncResult>> operation,
        TimeSpan interval,
        AhtolaAutomaticSyncStatus initialStatus,
        Action<AhtolaAutomaticSyncStatus> publish)
    {
        _operation = operation;
        _interval = interval;
        _status = initialStatus;
        _publish = publish;
    }

    public AhtolaAutomaticSyncStatus Status => Volatile.Read(ref _status);

    public void Start()
    {
        Publish(Status with
        {
            State = AhtolaAutomaticSyncState.Waiting,
            Attempt = 0,
            NextAttempt = DateTimeOffset.UtcNow + _interval,
        });
        _task = RunAsync(_cancellation.Token);
    }

    /// <summary>
    /// Cancels the loop and waits for it. Returns the failure that stopped the loop, if any.
    /// </summary>
    public Exception? Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return null;
        if (_task is null)
        {
            _cancellation.Dispose();
            return null;
        }

        try
        {
            _cancellation.Cancel();
            _task.GetAwaiter().GetResult();
            return null;
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
        finally
        {
            _cancellation.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
            for (var attempt = 1; ; attempt++)
            {
                Publish(Status with
                {
                    State = attempt == 1 ? AhtolaAutomaticSyncState.Running : AhtolaAutomaticSyncState.Retrying,
                    Attempt = attempt,
                    LastAttempt = DateTimeOffset.UtcNow,
                    NextAttempt = null,
                });
                try
                {
                    var result = await _operation(cancellationToken).ConfigureAwait(false);
                    var success = DateTimeOffset.UtcNow;
                    Publish(Status with
                    {
                        State = AhtolaAutomaticSyncState.Waiting,
                        Attempt = 0,
                        LastSuccess = success,
                        LastPullAppliedChanges = result.Outcome == AhtolaSyncOutcome.RemoteChangesApplied,
                        LastException = null,
                        NextAttempt = success + _interval,
                    });
                    break;
                }
                catch (Exception exception) when (
                    attempt < AhtolaConnection.AutomaticSyncMaximumAttempts
                    && AhtolaConnection.IsTransientAutomaticSyncFailure(exception, cancellationToken))
                {
                    var delay = AhtolaConnection.GetAutomaticSyncRetryDelay(attempt - 1);
                    Publish(Status with
                    {
                        State = AhtolaAutomaticSyncState.Retrying,
                        Attempt = attempt,
                        LastException = exception,
                        NextAttempt = DateTimeOffset.UtcNow + delay,
                    });
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    Publish(Status with
                    {
                        State = AhtolaAutomaticSyncState.Faulted,
                        Attempt = attempt,
                        LastException = exception,
                        NextAttempt = null,
                    });
                    throw;
                }
            }
        }
    }

    private void Publish(AhtolaAutomaticSyncStatus status)
    {
        Volatile.Write(ref _status, status);
        _publish(status);
    }
}
