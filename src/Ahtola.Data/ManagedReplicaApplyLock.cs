using System.Collections.Concurrent;
using Ahtola.Core.Storage;

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
/// per-canonicalized-path FIFO exclusive async gate backed by <see cref="SemaphoreSlim"/>. Provides
/// mutual exclusion within this process alone and intentionally makes no attempt at cross-process
/// coordination -- that gap is exactly what the forthcoming DELETE-mode OS lock closes.
/// </summary>
/// <remarks>
/// Gates are created lazily per resolved <see cref="LockKey"/> and never removed. A real replica
/// process only ever touches a small, effectively static set of distinct database paths over its
/// lifetime, so this keeps the implementation simple and lock-free on the hot (uncontended) path
/// rather than adding reference-counted teardown for a resource (one <see cref="SemaphoreSlim"/>
/// per distinct key) that is not worth the extra bookkeeping.
/// </remarks>
internal sealed class InProcessManagedReplicaApplyLockCoordinator : IManagedReplicaApplyLockCoordinator
{
    internal static readonly InProcessManagedReplicaApplyLockCoordinator Instance = new();

    private static readonly ConcurrentDictionary<LockKey, SemaphoreSlim> Gates = new();

    public async ValueTask<IAsyncDisposable> AcquireExclusiveAsync(string path, CancellationToken cancellationToken)
    {
        var key = ResolveLockKey(path);
        var gate = Gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(gate);
    }

    /// <summary>
    /// Resolves the mutual-exclusion key for <paramref name="path"/> using true physical file (or,
    /// failing that, physical parent-directory) identity wherever the platform supports it, so a
    /// symbolic link, junction/mount point, hard link, or Windows short (8.3) name that aliases the
    /// same underlying file or directory resolves to the SAME key as the canonical path. A purely
    /// textual normalization (<see cref="Path.GetFullPath(string)"/> alone) cannot make that
    /// guarantee -- two textually different paths naming the same physical file would get
    /// different keys and could race each other through this coordinator instead of serializing.
    /// </summary>
    private static LockKey ResolveLockKey(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        try
        {
            // The common case: the database file already exists (every apply after the replica's
            // first bootstrap). Its own physical identity is the strongest available key and, by
            // construction, is immune to every aliasing vector the review called out. Name/TextPath
            // are deliberately left null: equality for this kind must depend on Identity alone, or
            // two differently-spelled aliases of the same physical file would wrongly get different
            // keys.
            return new LockKey(LockKeyKind.PhysicalFile, SqliteWalSharedMemoryCarrierIdentity.FromPath(fullPath), null, null);
        }
        catch (FileNotFoundException)
        {
            // The target does not exist yet (first-ever bootstrap for this path); fall through to
            // canonicalizing its parent directory instead.
        }
        catch (DirectoryNotFoundException)
        {
            // Same as above, but a directory component of the path is also missing.
        }
        catch (PlatformNotSupportedException)
        {
            return new LockKey(LockKeyKind.TextFallback, default, null, NormalizeForTextFallback(fullPath));
        }

        var parentDirectory = Path.GetDirectoryName(fullPath);
        var fileName = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(parentDirectory) || string.IsNullOrEmpty(fileName))
        {
            // No parent-directory component to canonicalize (a degenerate/rooted path); nothing
            // physical is left to resolve against, so fall back to the plain textual key.
            return new LockKey(LockKeyKind.TextFallback, default, null, NormalizeForTextFallback(fullPath));
        }

        try
        {
            var parentIdentity = SqliteWalSharedMemoryCarrierIdentity.FromDirectoryPath(parentDirectory);
            var normalizedFileName = OperatingSystem.IsWindows() ? fileName.ToUpperInvariant() : fileName;
            return new LockKey(LockKeyKind.PhysicalParentAndName, parentIdentity, normalizedFileName, null);
        }
        catch (DirectoryNotFoundException)
        {
            // The parent directory does not exist yet either (for example, the very first
            // bootstrap into a brand-new directory tree). There is nothing physical left to
            // canonicalize, so fall back to the textual key -- a narrower race window than before
            // this fix, since it now only applies while the parent directory itself is missing
            // rather than for every not-yet-bootstrapped path.
            return new LockKey(LockKeyKind.TextFallback, default, null, NormalizeForTextFallback(fullPath));
        }
        catch (PlatformNotSupportedException)
        {
            return new LockKey(LockKeyKind.TextFallback, default, null, NormalizeForTextFallback(fullPath));
        }
    }

    private static string NormalizeForTextFallback(string fullPath)
        => OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;

    private enum LockKeyKind
    {
        /// <summary>Keyed by the physical identity of the target file itself, which already exists.</summary>
        PhysicalFile,

        /// <summary>Keyed by the physical identity of the target's parent directory plus its (case-normalized) file name, because the target itself does not exist yet.</summary>
        PhysicalParentAndName,

        /// <summary>Keyed by a purely textual normalization; used only when neither physical resolution above is available on this platform or for this path.</summary>
        TextFallback,
    }

    /// <summary>
    /// Mutual-exclusion key for one database path. Equality is structural over all four fields, so
    /// two keys of different <see cref="LockKeyKind"/>s are never equal even if their unused fields
    /// happen to share default values.
    /// </summary>
    private readonly record struct LockKey(
        LockKeyKind Kind,
        SqliteWalSharedMemoryCarrierIdentity Identity,
        string? Name,
        string? TextPath);

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
