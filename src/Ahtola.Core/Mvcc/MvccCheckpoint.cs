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
    Collect = 2,
    Materialize = 3,
    PersistPageWal = 4,
    Backfill = 5,
    RetireLogicalLog = 6,
    ResetWal = 7,
    GarbageCollect = 8,
    Complete = 9,
}

/// <summary>Outcome of a managed MVCC checkpoint attempt.</summary>
internal readonly record struct MvccCheckpointResult(
    bool Busy,
    long LogFramesBefore,
    long CheckpointedFrames,
    MvccCheckpointPhase CompletedThrough);

/// <summary>
/// Stable committed input collected while the checkpoint admission lease is held.
/// The timestamp is an inclusive logical-log retirement boundary.
/// </summary>
internal sealed record MvccCheckpointSnapshot(
    IReadOnlyList<(MvccRowId RowId, SqlValue[] Cells)> LiveRows,
    IReadOnlyCollection<MvccRowId> DeletedRows,
    ulong DurableTimestamp);

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

/// <summary>Deterministic phase boundary hook used by crash/reopen tests.</summary>
internal static class MvccCheckpointFaultInjection
{
    [field: ThreadStatic]
    internal static Action<MvccCheckpointPhase>? AfterPhaseForTesting { get; set; }

    internal static void Hit(MvccCheckpointPhase phase)
        => AfterPhaseForTesting?.Invoke(phase);
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
