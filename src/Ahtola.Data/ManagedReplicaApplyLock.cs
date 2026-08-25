using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ahtola.Core.Storage;

namespace Ahtola;

/// <summary>
/// Resolves the single operating-system lock carrier that every alias of one physical replica
/// database must share.
/// </summary>
/// <remarks>
/// <para>
/// A carrier derived by concatenating a suffix onto <see cref="Path.GetFullPath(string)"/> is a
/// <em>textual</em> derivation, so two names for the same physical file -- a hard link, a symbolic
/// link, a junction/mount point, or a Windows short (8.3) name -- each produce their own carrier
/// file and therefore their own, mutually invisible operating-system lock. Two processes holding
/// what they each believe is the exclusive apply lease would then run concurrently over one
/// database.
/// </para>
/// <para>
/// The carrier is instead named from the file's <em>physical</em> identity (volume/device plus
/// file/inode id) inside one stable directory, so every alias resolves to the same carrier
/// regardless of how it was spelled or which process resolved it. The directory is deliberately
/// not a sibling of the database: two hard links to one file may live in different directories,
/// and a per-directory carrier would split them again.
/// </para>
/// <para>
/// When a replica path does not exist yet (its very first bootstrap) there is no file identity to
/// read, and a file that does not exist can have no hard links either; the carrier then falls back
/// to the physical identity of the parent directory plus the file name, which is alias-safe for
/// exactly that case. When neither can be proven -- a platform without file identity, or a missing
/// parent directory -- resolution fails closed rather than silently handing back a carrier that
/// cannot guarantee exclusion.
/// </para>
/// </remarks>
internal static class ManagedReplicaLockCarrier
{
    /// <summary>
    /// Overrides the stable lock directory. The default is per-user, so a deployment that shares
    /// one replica file between operating-system users points every user at one shared directory.
    /// </summary>
    internal const string DirectoryVariable = "AHTOLA_REPLICA_LOCK_DIRECTORY";

    /// <summary>Carrier kind for the exclusive apply/publication lease.</summary>
    internal const string ApplyKind = "apply";

    /// <summary>Carrier kind for the change-journal append/persist lease.</summary>
    internal const string JournalKind = "journal";

    /// <summary>Carrier kind for the cross-process remote push flight.</summary>
    internal const string PushKind = "push";

    /// <summary>
    /// Resolves and creates the carrier file for <paramref name="databasePath"/>, failing closed
    /// when the physical identity behind the path cannot be proven.
    /// </summary>
    internal static string Ensure(string databasePath, string kind)
    {
        var carrierPath = Resolve(databasePath, kind);
        Directory.CreateDirectory(Path.GetDirectoryName(carrierPath)!);

        // Opened and closed purely to create the carrier: the lease below takes its own handle.
        using (File.Open(
                   carrierPath,
                   FileMode.OpenOrCreate,
                   FileAccess.ReadWrite,
                   FileShare.ReadWrite | FileShare.Delete))
        {
        }

        return carrierPath;
    }

    /// <summary>
    /// Resolves the carrier path without creating anything, or returns <see langword="null"/> when
    /// the physical identity cannot be proven. Used by artifact enumeration, which must describe
    /// what a replica owns without ever failing.
    /// </summary>
    internal static string? TryResolve(string databasePath, string kind)
    {
        try
        {
            return Resolve(databasePath, kind);
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or PlatformNotSupportedException
                                              or ArgumentException)
        {
            return null;
        }
    }

    private static string Resolve(string databasePath, string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(databasePath));

        if (TryReadFileIdentity(fullPath) is { } fileIdentity)
            return Compose(kind, 'f', fileIdentity, name: null);

        var parentDirectory = Path.GetDirectoryName(fullPath);
        var fileName = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(parentDirectory) || string.IsNullOrEmpty(fileName))
            throw FailClosed(fullPath);

        try
        {
            var parentIdentity = SqliteWalSharedMemoryCarrierIdentity.FromDirectoryPath(parentDirectory);
            return Compose(kind, 'd', parentIdentity, OperatingSystem.IsWindows() ? fileName.ToUpperInvariant() : fileName);
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException
                                              or FileNotFoundException
                                              or PlatformNotSupportedException)
        {
            throw FailClosed(fullPath, exception);
        }
    }

    private static SqliteWalSharedMemoryCarrierIdentity? TryReadFileIdentity(string fullPath)
    {
        try
        {
            return SqliteWalSharedMemoryCarrierIdentity.FromPath(fullPath);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static string Compose(
        string kind,
        char scope,
        SqliteWalSharedMemoryCarrierIdentity identity,
        string? name)
    {
        var builder = new StringBuilder(kind)
            .Append('-')
            .Append(scope)
            .Append(identity.Device.ToString("x16", CultureInfo.InvariantCulture))
            .Append(identity.File.ToString("x16", CultureInfo.InvariantCulture));
        if (name is not null)
        {
            // The name only participates in the directory-identity fallback, where two different
            // files legitimately share one parent identity. Hashing keeps the carrier name a fixed,
            // path-separator-free, length-bounded token on every file system.
            builder
                .Append('-')
                .Append(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))).AsSpan(0, 32));
        }

        return Path.Combine(ResolveDirectory(), builder.Append(".lock").ToString());
    }

    private static string ResolveDirectory()
    {
        if (Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } configured)
            return Path.GetFullPath(configured);

        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        if (!string.IsNullOrEmpty(localApplicationData))
            return Path.Combine(localApplicationData, "Ahtola", "replica-locks");

        var userProfile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.Create);
        if (!string.IsNullOrEmpty(userProfile))
            return Path.Combine(userProfile, ".ahtola", "replica-locks");

        throw new IOException(
            "Managed embedded replica could not resolve a stable lock directory for its cross-process "
            + $"apply lease. Set the {DirectoryVariable} environment variable to a writable directory "
            + "shared by every process that opens this replica.");
    }

    private static PlatformNotSupportedException FailClosed(string fullPath, Exception? inner = null)
        => new(
            $"Managed embedded replica could not prove the physical identity of '{fullPath}', so it "
            + "cannot guarantee that every alias of this database shares one cross-process apply "
            + "lease. The replica refuses to proceed rather than run two writers concurrently.",
            inner);
}

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
/// The default implementation, <see cref="CrossProcessManagedReplicaApplyLockCoordinator"/>,
/// composes an in-process FIFO gate with a genuine operating-system byte-range lock over a carrier
/// named from the database's physical identity, so the lease holds across processes and across
/// every alias of the same file. <see cref="ManagedReplicaApplyLock.Current"/> is the only seam a
/// test needs to redirect; no bootstrapper call site, and no caller, ever changes.
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
/// <see cref="ManagedReplicaBootstrapper"/>.
/// </summary>
internal static class ManagedReplicaApplyLock
{
    /// <summary>
    /// Legacy sibling carrier name. Builds before the physical-identity carrier put the lock file
    /// next to the database, which split hard-link and other path aliases into separate locks. The
    /// constant is retained so artifact enumeration still cleans up a file an older build left
    /// behind; nothing acquires it any more.
    /// </summary>
    internal const string CarrierSuffix = ".ahtola-replica-apply-lock";
    private const long PendingByte = 0x4000_0000;
    private const long SQLiteExclusiveRangeLength = 512;
    private static IManagedReplicaApplyLockCoordinator _current = CrossProcessManagedReplicaApplyLockCoordinator.Instance;

    internal static IManagedReplicaApplyLockCoordinator Current
    {
        get => _current;
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }

    internal static ValueTask<IAsyncDisposable> AcquireExclusiveAsync(string path, CancellationToken cancellationToken)
        => Current.AcquireExclusiveAsync(path, cancellationToken);

    internal static IDisposable? AcquireMainFileReplacementLock(
        string path,
        CancellationToken cancellationToken)
        => File.Exists(path)
            ? new SqliteWalByteRangeLock(path).AcquireExclusive(
                PendingByte,
                SQLiteExclusiveRangeLength,
                Timeout.InfiniteTimeSpan,
                cancellationToken)
            : null;
}

/// <summary>
/// The exclusive lease that serializes <see cref="ManagedReplicaChangeJournal"/> append/persist
/// across every instance, alias, and process that shares one physical replica database.
/// </summary>
/// <remarks>
/// <para>
/// The journal publishes by rewriting the <em>whole</em> file, so two writers that each hold their
/// own in-memory copy do not merge: whichever replaces last wins, and the other writer's durably
/// appended, acknowledged, or discarded entries are silently gone. An in-object monitor cannot see
/// a second <see cref="ManagedReplicaChangeJournal"/> instance, let alone a second process, so the
/// serialization has to be an operating-system lock over the physical database identity.
/// </para>
/// <para>
/// This is deliberately a <em>separate</em> carrier from the apply lease rather than a second byte
/// range on the same one: on macOS the byte-range locks are process-associated POSIX locks, where
/// closing any descriptor for a file drops every lock the process holds on it. Two independent
/// carriers keep the two leases from interfering.
/// </para>
/// <para>
/// Lock order is always apply lease first, journal lease second (the journal lease is only ever
/// taken as a leaf, for the duration of one persist), so the two can never deadlock.
/// </para>
/// </remarks>
internal static class ManagedReplicaJournalLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.Ordinal);

    private static Func<string, IDisposable>? _override;

    /// <summary>Test seam: replaces the physical lease with an in-process stand-in.</summary>
    internal static Func<string, IDisposable>? Override
    {
        get => _override;
        set => _override = value;
    }

    internal static IDisposable AcquireExclusive(string databasePath)
    {
        if (_override is { } factory)
            return factory(databasePath);

        var carrierPath = ManagedReplicaLockCarrier.Ensure(databasePath, ManagedReplicaLockCarrier.JournalKind);

        // The in-process gate is taken first so threads of this process queue on a cheap semaphore
        // instead of spinning on the operating-system lock, and so a reentrant-looking second
        // acquisition inside one process is a deadlock we own rather than an OS-level lock upgrade
        // whose behaviour differs per platform.
        var gate = Gates.GetOrAdd(carrierPath, static _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            var lease = new SqliteWalByteRangeLock(carrierPath).AcquireExclusive(
                offset: 0,
                length: 1,
                Timeout.InfiniteTimeSpan,
                CancellationToken.None);
            return new Lease(gate, lease);
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    private sealed class Lease(SemaphoreSlim gate, IDisposable carrierLease) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        private IDisposable? _carrierLease = carrierLease;

        public void Dispose()
        {
            Interlocked.Exchange(ref _carrierLease, null)?.Dispose();
            Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }
}

/// <summary>
/// Serializes remote push flights for one physical replica across aliases and processes.
/// </summary>
/// <remarks>
/// The apply and journal leases remain short and are never held across network I/O. This separate
/// lease spans watermark verification and SQL replay so two processes cannot both observe the same
/// pre-batch watermark and replay one non-idempotent batch concurrently. Any operation that can
/// publish pull metadata or replace the main file takes this lease before the apply lease. That
/// push-to-apply order prevents a file replacement from changing the physical identity used for
/// this carrier while an older push carrier is still held.
/// </remarks>
internal static class ManagedReplicaPushLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.Ordinal);

    internal static async ValueTask<IAsyncDisposable> AcquireExclusiveAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var carrierPath = ManagedReplicaLockCarrier.Ensure(
            databasePath,
            ManagedReplicaLockCarrier.PushKind);
        var gate = Gates.GetOrAdd(carrierPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        SqliteWalByteRangeLockLease? carrierLease = null;
        try
        {
            carrierLease = new SqliteWalByteRangeLock(carrierPath).AcquireExclusive(
                offset: 0,
                length: 1,
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            return new Lease(gate, carrierLease);
        }
        catch
        {
            carrierLease?.Dispose();
            gate.Release();
            throw;
        }
    }

    private sealed class Lease(
        SemaphoreSlim gate,
        SqliteWalByteRangeLockLease carrierLease) : IAsyncDisposable
    {
        private SemaphoreSlim? _gate = gate;
        private SqliteWalByteRangeLockLease? _carrierLease = carrierLease;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _carrierLease, null)?.Dispose();
            Interlocked.Exchange(ref _gate, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class CrossProcessManagedReplicaApplyLockCoordinator : IManagedReplicaApplyLockCoordinator
{
    internal static readonly CrossProcessManagedReplicaApplyLockCoordinator Instance = new();

    public async ValueTask<IAsyncDisposable> AcquireExclusiveAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var localLease = await InProcessManagedReplicaApplyLockCoordinator.Instance
            .AcquireExclusiveAsync(path, cancellationToken)
            .ConfigureAwait(false);
        SqliteWalByteRangeLockLease? carrierLease = null;
        try
        {
            // Named from the database's physical identity, so a hard link or any other alias of the
            // same file resolves to this very carrier instead of minting a private one.
            var carrierPath = ManagedReplicaLockCarrier.Ensure(path, ManagedReplicaLockCarrier.ApplyKind);
            carrierLease = new SqliteWalByteRangeLock(carrierPath).AcquireExclusive(
                offset: 0,
                length: 1,
                Timeout.InfiniteTimeSpan,
                cancellationToken);

            return new Lease(localLease, carrierLease);
        }
        catch
        {
            carrierLease?.Dispose();
            await localLease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Lease(
        IAsyncDisposable localLease,
        SqliteWalByteRangeLockLease carrierLease) : IAsyncDisposable
    {
        private IAsyncDisposable? _localLease = localLease;
        private SqliteWalByteRangeLockLease? _carrierLease = carrierLease;

        public async ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _carrierLease, null)?.Dispose();
            var local = Interlocked.Exchange(ref _localLease, null);
            if (local is not null)
                await local.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// The in-process half of the apply lease: a per-canonicalized-path FIFO exclusive async gate
/// backed by <see cref="SemaphoreSlim"/>. It is composed with the physical carrier lock by
/// <see cref="CrossProcessManagedReplicaApplyLockCoordinator"/> so threads of one process queue on
/// a cheap semaphore rather than contending for the operating-system lock, and can also be
/// selected on its own by a test that only needs in-process serialization.
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
