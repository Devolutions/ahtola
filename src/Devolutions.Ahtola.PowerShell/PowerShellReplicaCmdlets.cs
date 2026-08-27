using System.Collections.Concurrent;
using System.Management.Automation;
using Ahtola;

namespace Ahtola.PSSqlite;

public abstract class PSSqliteReplicaCmdlet : PSSqliteCmdlet
{
    private readonly CancellationTokenSource _cancellation = new();

    protected T AwaitReplicaOperation<T>(
        string activity,
        Func<IProgress<AhtolaSyncProgress>, CancellationToken, Task<T>> operation)
    {
        var progress = new BufferedReplicaProgress();
        var task = operation(progress, _cancellation.Token);
        while (!task.IsCompleted)
        {
            DrainProgress(activity, progress);
            Thread.Sleep(25);
        }

        DrainProgress(activity, progress);
        return task.GetAwaiter().GetResult();
    }

    protected override void StopProcessing() => _cancellation.Cancel();

    protected CancellationToken CancellationToken => _cancellation.Token;

    private void DrainProgress(string activity, BufferedReplicaProgress progress)
    {
        while (progress.TryRead(out var update))
        {
            var completed = update.Stage == AhtolaSyncProgressStage.Completed;
            WriteProgress(new ProgressRecord(0, activity, update.Stage.ToString())
            {
                PercentComplete = update.Stage switch
                {
                    AhtolaSyncProgressStage.Pushing => 10,
                    AhtolaSyncProgressStage.Pulling => 40,
                    AhtolaSyncProgressStage.Applying => 75,
                    AhtolaSyncProgressStage.Completed => 100,
                    _ => -1,
                },
                RecordType = completed ? ProgressRecordType.Completed : ProgressRecordType.Processing,
            });
        }
    }

    private sealed class BufferedReplicaProgress : IProgress<AhtolaSyncProgress>
    {
        private readonly ConcurrentQueue<AhtolaSyncProgress> _updates = new();

        public void Report(AhtolaSyncProgress value) => _updates.Enqueue(value);

        public bool TryRead(out AhtolaSyncProgress value) => _updates.TryDequeue(out value!);
    }
}

[Cmdlet(VerbsLifecycle.Invoke, "AhtolaSqliteReplicaSync", SupportsShouldProcess = true)]
[OutputType(typeof(AhtolaSyncResult))]
public sealed class InvokePSSqliteReplicaSyncCommand : PSSqliteReplicaCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    [Alias("Connection")]
    public AhtolaCloudConnection ReplicaConnection { get; set; } = null!;

    protected override void ProcessRecord()
    {
        if (!ShouldProcess(ReplicaConnection.Endpoint, "Synchronize managed Turso Cloud replica"))
            return;

        WriteObject(AwaitReplicaOperation(
            "Synchronizing Ahtola SQLite replica",
            (progress, cancellationToken) => ReplicaConnection.SynchronizeAsync(
                new AhtolaSyncOptions(progress),
                cancellationToken)));
    }
}

[Cmdlet(VerbsCommon.Get, "AhtolaSqliteReplicaConflict")]
[OutputType(typeof(AhtolaReplicaConflictReport))]
public sealed class GetPSSqliteReplicaConflictCommand : PSSqliteReplicaCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    [Alias("Connection")]
    public AhtolaCloudConnection ReplicaConnection { get; set; } = null!;

    protected override void ProcessRecord()
    {
        var report = ReplicaConnection
            .InspectReplicaConflictAsync(CancellationToken)
            .GetAwaiter()
            .GetResult();
        if (report is not null)
            WriteObject(report);
    }
}

[Cmdlet(
    VerbsDiagnostic.Resolve,
    "AhtolaSqliteReplicaConflict",
    DefaultParameterSetName = "RebaseEligible",
    SupportsShouldProcess = true,
    ConfirmImpact = ConfirmImpact.High)]
[OutputType(typeof(AhtolaReplicaConflictResolutionResult))]
public sealed class ResolvePSSqliteReplicaConflictCommand : PSSqliteReplicaCmdlet
{
    private const string RebaseParameterSet = "RebaseEligible";
    private const string DiscardParameterSet = "DiscardUnresolved";

    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    [Alias("Connection")]
    public AhtolaCloudConnection ReplicaConnection { get; set; } = null!;

    [Parameter(ParameterSetName = RebaseParameterSet)]
    [Alias("ReplayEligible")]
    public SwitchParameter RebaseEligible { get; set; }

    [Parameter(Mandatory = true, ParameterSetName = DiscardParameterSet)]
    public SwitchParameter DiscardUnresolvedChanges { get; set; }

    [Parameter(Mandatory = true, ParameterSetName = DiscardParameterSet)]
    public SwitchParameter AcknowledgeDataLoss { get; set; }

    protected override void ProcessRecord()
    {
        var discardParameterSet = ParameterSetName == DiscardParameterSet;
        if (discardParameterSet && !DiscardUnresolvedChanges.IsPresent)
        {
            ThrowTerminatingError(new ErrorRecord(
                new PSArgumentException(
                    "DiscardUnresolvedChanges must be explicitly enabled for destructive conflict resolution.",
                    nameof(DiscardUnresolvedChanges)),
                "DiscardUnresolvedChangesRequired",
                ErrorCategory.InvalidArgument,
                DiscardUnresolvedChanges));
            return;
        }

        var discard = discardParameterSet && DiscardUnresolvedChanges.IsPresent;
        var action = discard
            ? "Permanently discard unresolved local replica changes"
            : "Pull the remote base and replay eligible local replica changes";
        if (!ShouldProcess(ReplicaConnection.ReplicaPath ?? ReplicaConnection.Endpoint, action))
            return;

        var resolution = discard
            ? AhtolaReplicaConflictResolution.DiscardUnresolvedChanges
            : AhtolaReplicaConflictResolution.PullAndRebaseEligible;
        WriteObject(AwaitReplicaOperation(
            "Resolving Ahtola SQLite replica conflict",
            (progress, cancellationToken) => ReplicaConnection.ResolveReplicaConflictAsync(
                resolution,
                new AhtolaReplicaConflictResolutionOptions
                {
                    AcknowledgeDataLoss = AcknowledgeDataLoss.IsPresent,
                    Progress = progress,
                },
                cancellationToken)));
    }
}

[Cmdlet(VerbsCommon.Get, "AhtolaSqliteReplicaChangeCapture")]
[OutputType(typeof(AhtolaReplicaChangeCaptureBatch))]
public sealed class GetPSSqliteReplicaChangeCaptureCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    [Alias("Connection")]
    public AhtolaCloudConnection ReplicaConnection { get; set; } = null!;

    protected override void ProcessRecord()
        => WriteObject(ReplicaConnection.PeekPendingChangeCapture());
}
