using System.Collections.Concurrent;

namespace Ahtola;

/// <summary>
/// Narrow seam for the single exclusive lease that must be held across a managed embedded
/// replica's apply sequence: from the moment WAL/journal sidecar state is validated, through
/// checkpoint, main-file swap, and metadata publication (or rollback/cleanup on failure). Holding
/// one lease across that whole span closes the check-then-act window that would otherwise exist
/// between "sidecars look clean" and "the file was actually replaced".
/// </summary>
/// <remarks>
/// <see cref="ManagedReplicaBootstrapper"/> acquires and releases this lease internally around
/// its own apply methods; callers -- including <see cref="ManagedReplicaConnectionHost"/> and any
/// direct caller such as a test -- never acquire it themselves, so the invariant holds regardless
/// of how the bootstrapper is invoked.
///
/// The only implementation today, <see cref="InProcessManagedReplicaApplyLockCoordinator"/>, only
/// coordinates within this process. It is a placeholder for the forthcoming cross-process
/// DELETE-mode SQLite OS lock (SHARED/RESERVED/PENDING/EXCLUSIVE main-file locking): when that
/// lands, only <see cref="ManagedReplicaApplyLock.Current"/> needs to be repointed at the new
/// implementation -- no bootstrapper call site, and no caller, should need to change.
/// </remarks>
internal interface IManagedReplicaApplyLockCoordinator
{
    /// <summary>
    /// Acquires the exclusive apply lease for the database at <paramref name="path"/>, waiting
    /// for any other holder of the same (normalized) path to release first. The returned lease
    /// must be released -- via <c>await using</c> -- only after every sidecar validation,
    /// checkpoint, file swap, and metadata publication for this apply has completed, or after any
    /// rollback/cleanup on a failed apply has completed.
    /// </summary>
    ValueTask<IAsyncDisposable> AcquireExclusiveAsync(string path, CancellationToken cancellationToken);
}

/// <summary>
/// Holds the <see cref="IManagedReplicaApplyLockCoordinator"/> used by
/// <see cref="ManagedReplicaBootstrapper"/>. Defaults to the in-process gate. This indirection is
/// the entire seam gap #9 (the cross-process DELETE-mode OS lock) needs: swap
/// <see cref="Current"/> in one place once that lock exists, without touching any bootstrapper
/// call site.
/// </summary>
internal static class ManagedReplicaApplyLock
{
    private static IManagedReplicaApplyLockCoordinator _current = InProcessManagedReplicaApplyLockCoordinator.Instance;

    internal static IManagedReplicaApplyLockCoordinator Current
    {
        get => _current;
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }

    internal static ValueTask<IAsyncDisposable> AcquireExclusiveAsync(string path, CancellationToken cancellationToken)
        => Current.AcquireExclusiveAsync(path, cancellationToken);
}

/// <summary>
/// In-process-only interim implementation of <see cref="IManagedReplicaApplyLockCoordinator"/>: a
/// per-normalized-path FIFO exclusive async gate backed by <see cref="SemaphoreSlim"/>. Provides
/// mutual exclusion within this process alone and intentionally makes no attempt at cross-process
/// coordination -- that gap is exactly what the forthcoming DELETE-mode OS lock closes.
/// </summary>
/// <remarks>
/// Gates are created lazily per normalized path and never removed. A real replica process only
/// ever touches a small, effectively static set of distinct database paths over its lifetime, so
/// this keeps the implementation simple and lock-free on the hot (uncontended) path rather than
/// adding reference-counted teardown for a resource (one <see cref="SemaphoreSlim"/> per distinct
/// path) that is not worth the extra bookkeeping.
/// </remarks>
internal sealed class InProcessManagedReplicaApplyLockCoordinator : IManagedReplicaApplyLockCoordinator
{
    internal static readonly InProcessManagedReplicaApplyLockCoordinator Instance = new();

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(PathComparer);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public async ValueTask<IAsyncDisposable> AcquireExclusiveAsync(string path, CancellationToken cancellationToken)
    {
        var normalizedPath = NormalizePath(path);
        var gate = Gates.GetOrAdd(normalizedPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(gate);
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
