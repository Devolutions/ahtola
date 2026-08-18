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
    CollectRows = 2,
    MaterializeCatalog = 3,
    PersistCatalog = 4,
    BackfillMainStore = 5,
    TruncateLogicalLog = 6,
    ResetWal = 7,
    GarbageCollect = 8,
    Finalize = 9,
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
