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
    TruncateLogicalLog = 5,
    GarbageCollect = 6,
    Finalize = 7,
}

/// <summary>Outcome of a managed MVCC checkpoint attempt.</summary>
internal readonly record struct MvccCheckpointResult(
    bool Busy,
    long LogFramesBefore,
    long CheckpointedFrames,
    MvccCheckpointPhase CompletedThrough);

/// <summary>
/// Skeleton port of Turso <c>CheckpointStateMachine</c>:
/// materialize committed version-store rows into the classic catalog, durably
/// persist, truncate the logical log (TRUNCATE/RESTART), then GC history past
/// the reader low-water mark. PASSIVE skips truncate when concurrent txs are
/// still open.
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
