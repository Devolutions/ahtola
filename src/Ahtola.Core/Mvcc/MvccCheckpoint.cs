namespace Ahtola.Core.Mvcc;

/// <summary>
/// High-level phases of Turso's MVCC checkpoint state machine
/// (<c>checkpoint_state_machine.rs</c>). The managed engine runs these
/// synchronously rather than as a cooperative IO state machine.
/// </summary>
internal enum MvccCheckpointPhase : byte
{
    Prepare = 0,
    AcquireLock = 1,
    BuildSnapshot = 2,
    MaterializeRows = 3,
    CommitPager = 4,
    BackfillMainStore = 5,
    SyncMainStore = 6,
    RetireLogicalLog = 7,
    ResetWal = 8,
    GarbageCollect = 9,
    Complete = 10,
}

/// <summary>Outcome of a managed MVCC checkpoint attempt.</summary>
internal readonly record struct MvccCheckpointResult(
    bool Busy,
    long LogFramesBefore,
    long CheckpointedFrames,
    MvccCheckpointPhase CompletedThrough);

/// <summary>
/// Synchronous managed port of Turso's checkpoint durability sequence:
/// materialize into WAL pages, backfill and flush the main file, retire the
/// logical log, then reset the WAL last.
/// </summary>
internal static class MvccCheckpoint
{
    internal static bool ShouldTruncateLog(string? mode)
    {
        if (mode is null)
            return false;
        return mode.Equals("TRUNCATE", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("RESTART", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("FULL", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsPassive(string? mode)
        => mode is null
            || mode.Equals("PASSIVE", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Retained phase driver for the managed synchronous checkpoint. Unlike Turso's
/// cooperative driver, each entered phase completes synchronously before the
/// next phase is published.
/// </summary>
internal sealed class MvccCheckpointStateMachine
{
    internal MvccCheckpointPhase Phase { get; private set; } = MvccCheckpointPhase.Prepare;

    internal void Enter(MvccCheckpointPhase phase)
    {
        if (phase <= Phase)
            throw new InvalidOperationException(
                $"MVCC checkpoint cannot move from {Phase} to {phase}.");
        Phase = phase;
    }

    internal MvccCheckpointResult Result(
        bool busy,
        long logFramesBefore,
        long checkpointedFrames)
        => new(
            Busy: busy,
            LogFramesBefore: logFramesBefore,
            CheckpointedFrames: checkpointedFrames,
            CompletedThrough: Phase);
}
