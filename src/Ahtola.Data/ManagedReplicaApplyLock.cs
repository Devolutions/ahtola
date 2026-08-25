using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ahtola.Core.Storage;

namespace Ahtola;

/// <summary>
/// Resolves the operating-system lock carriers that every alias and every published generation of
/// one replica database must share.
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
/// Each lease holds two carriers: one named from the file's <em>physical</em> identity
/// (volume/device plus file/inode id), which unifies hard-link aliases, and one named from the
/// physical parent-directory identity plus file name, which remains stable when an atomic
/// <see cref="File.Replace(string,string,string?,bool)"/> publishes a new inode at that path.
/// Symbolic-link and junction aliases resolve their parent to the same physical directory.
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
        var carrierPaths = EnsureAll(databasePath, kind);
        return carrierPaths[0];
    }

    internal static IReadOnlyList<string> EnsureAll(string databasePath, string kind)
    {
        var carrierPaths = ResolveAll(databasePath, kind);
        foreach (var carrierPath in carrierPaths)
            EnsureCarrier(carrierPath);

        return carrierPaths;
    }

    internal static string EnsureStable(string databasePath, string kind)
    {
        var carrierPath = ResolveStable(databasePath, kind);
        EnsureCarrier(carrierPath);
        return carrierPath;
    }

    internal static string? EnsurePhysical(string databasePath, string kind)
    {
        var carrierPath = TryResolvePhysical(databasePath, kind);
        if (carrierPath is not null)
            EnsureCarrier(carrierPath);
        return carrierPath;
    }

    internal static string? TryResolvePhysical(string databasePath, string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(databasePath));
        return TryReadFileIdentity(fullPath) is { } fileIdentity
            ? Compose(kind, 'f', fileIdentity, name: null)
            : null;
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

    internal static IReadOnlyList<string> TryResolveAll(string databasePath, string kind)
    {
        try
        {
            return ResolveAll(databasePath, kind);
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or PlatformNotSupportedException
                                              or ArgumentException)
        {
            return [];
        }
    }

    private static string Resolve(string databasePath, string kind)
        => ResolveAll(databasePath, kind)[0];

    private static IReadOnlyList<string> ResolveAll(string databasePath, string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(databasePath));
        var pathCarrier = ResolveStable(fullPath, kind);
        var physicalCarrier = TryResolvePhysical(fullPath, kind);
        if (physicalCarrier is null)
            return [pathCarrier];

        return string.Equals(physicalCarrier, pathCarrier, StringComparison.Ordinal)
            ? [physicalCarrier]
            : [physicalCarrier, pathCarrier];
    }

    private static string ResolveStable(string databasePath, string kind)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(databasePath));
        var parentDirectory = Path.GetDirectoryName(fullPath);
        var fileName = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(parentDirectory) || string.IsNullOrEmpty(fileName))
            throw FailClosed(fullPath);

        string pathCarrier;
        try
        {
            var parentIdentity = SqliteWalSharedMemoryCarrierIdentity.FromDirectoryPath(parentDirectory);
            pathCarrier = Compose(
                kind,
                'd',
                parentIdentity,
                OperatingSystem.IsWindows() ? fileName.ToUpperInvariant() : fileName);
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException
                                              or FileNotFoundException
                                              or PlatformNotSupportedException)
        {
            throw FailClosed(fullPath, exception);
        }
        return pathCarrier;
    }

    internal static void EnsureCarrier(string carrierPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(carrierPath)!);

        // Opened and closed purely to create the carrier: the lease below takes its own handle.
        using (File.Open(
                   carrierPath,
                   FileMode.OpenOrCreate,
                   FileAccess.ReadWrite,
                   FileShare.ReadWrite | FileShare.Delete))
        {
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
/// Acquires only the physical-identity carrier for an existing database inode. Registrations make
/// the carrier safely reclaimable: every holder and waiter registers under one directory guard
/// before opening the carrier, and the last registration removes both files.
/// </summary>
internal sealed class ManagedReplicaPhysicalCarrierLease : IAsyncDisposable
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> ActiveRegistrations =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, object> RegistryGates =
        new(StringComparer.Ordinal);

    private SemaphoreSlim? _gate;
    private SqliteWalByteRangeLockLease? _carrierLease;
    private Registration? _registration;

    private ManagedReplicaPhysicalCarrierLease(
        SemaphoreSlim? gate,
        SqliteWalByteRangeLockLease? carrierLease,
        Registration? registration)
    {
        _gate = gate;
        _carrierLease = carrierLease;
        _registration = registration;
    }

    internal static async ValueTask<ManagedReplicaPhysicalCarrierLease> AcquireAsync(
        string databasePath,
        string kind,
        CancellationToken cancellationToken)
    {
        while (ManagedReplicaLockCarrier.TryResolvePhysical(databasePath, kind) is { } carrierPath)
        {
            Registration? registration = null;
            SemaphoreSlim? gate = null;
            SqliteWalByteRangeLockLease? carrierLease = null;
            var gateHeld = false;
            try
            {
                registration = Registration.Create(carrierPath);
                gate = Gates.GetOrAdd(carrierPath, static _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                gateHeld = true;
                carrierLease = new SqliteWalByteRangeLock(carrierPath).AcquireExclusive(
                    offset: 0,
                    length: 1,
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
                if (string.Equals(
                        ManagedReplicaLockCarrier.TryResolvePhysical(databasePath, kind),
                        carrierPath,
                        StringComparison.Ordinal))
                {
                    return new ManagedReplicaPhysicalCarrierLease(gate, carrierLease, registration);
                }
            }
            catch
            {
                carrierLease?.Dispose();
                if (gateHeld)
                    gate!.Release();
                registration?.Dispose();
                throw;
            }

            carrierLease.Dispose();
            gate.Release();
            registration.Dispose();
        }

        return new ManagedReplicaPhysicalCarrierLease(null, null, null);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _carrierLease, null)?.Dispose();
        Interlocked.Exchange(ref _gate, null)?.Release();
        Interlocked.Exchange(ref _registration, null)?.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class Registration : IDisposable
    {
        private readonly string _carrierPath;
        private readonly string _holderPath;
        private SqliteWalByteRangeLockLease? _holderLease;

        private Registration(
            string carrierPath,
            string holderPath,
            SqliteWalByteRangeLockLease holderLease)
        {
            _carrierPath = carrierPath;
            _holderPath = holderPath;
            _holderLease = holderLease;
        }

        internal static Registration Create(string carrierPath)
        {
            var directory = Path.GetDirectoryName(carrierPath)!;
            Directory.CreateDirectory(directory);
            var registryPath = Path.Combine(directory, "physical-carrier-registry.lock");
            ManagedReplicaLockCarrier.EnsureCarrier(registryPath);
            lock (RegistryGates.GetOrAdd(registryPath, static _ => new object()))
            {
                using var registryLease = new SqliteWalByteRangeLock(registryPath).AcquireExclusive(
                    offset: 0,
                    length: 1,
                    Timeout.InfiniteTimeSpan,
                    CancellationToken.None);

                CleanupStaleRegistrations(carrierPath);
                ManagedReplicaLockCarrier.EnsureCarrier(carrierPath);
                var holderPath = string.Concat(
                    carrierPath,
                    ".holder-",
                    Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                    "-",
                    Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
                ManagedReplicaLockCarrier.EnsureCarrier(holderPath);
                var holderLease = new SqliteWalByteRangeLock(holderPath).AcquireExclusive(
                    offset: 0,
                    length: 1,
                    Timeout.InfiniteTimeSpan,
                    CancellationToken.None);
                ActiveRegistrations.TryAdd(holderPath, 0);
                return new Registration(carrierPath, holderPath, holderLease);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _holderLease, null) is not { } holderLease)
                return;

            var directory = Path.GetDirectoryName(_carrierPath)!;
            var registryPath = Path.Combine(directory, "physical-carrier-registry.lock");
            lock (RegistryGates.GetOrAdd(registryPath, static _ => new object()))
            {
                using var registryLease = new SqliteWalByteRangeLock(registryPath).AcquireExclusive(
                    offset: 0,
                    length: 1,
                    Timeout.InfiniteTimeSpan,
                    CancellationToken.None);
                ActiveRegistrations.TryRemove(_holderPath, out _);
                holderLease.Dispose();
                File.Delete(_holderPath);
                CleanupStaleRegistrations(_carrierPath);
                if (!EnumerateHolderPaths(_carrierPath).Any())
                    File.Delete(_carrierPath);
            }
        }

        private static void CleanupStaleRegistrations(string carrierPath)
        {
            foreach (var holderPath in EnumerateHolderPaths(carrierPath))
            {
                if (ActiveRegistrations.ContainsKey(holderPath))
                    continue;
                var marker = new SqliteWalByteRangeLock(holderPath);
                if (!marker.TryAcquireExclusive(offset: 0, length: 1, out var staleLease))
                    continue;
                staleLease!.Dispose();
                File.Delete(holderPath);
            }
        }

        private static IEnumerable<string> EnumerateHolderPaths(string carrierPath)
        {
            var directory = Path.GetDirectoryName(carrierPath)!;
            var pattern = Path.GetFileName(carrierPath) + ".holder-*";
            return Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly);
        }
    }
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

    internal static IDisposable? AcquireMainFileReplacementLock(
        string path,
        string replacementPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;

        IDisposable? destinationMainFileLease = null;
        IDisposable? replacementMainFileLease = null;
        ManagedReplicaPhysicalCarrierLease? replacementPushCarrier = null;
        ManagedReplicaPhysicalCarrierLease? replacementApplyCarrier = null;
        try
        {
            // The caller already owns the final path's stable push/apply carriers. Lock the two
            // database inodes themselves as well. Unix retains both leases across rename. Windows
            // cannot rename a byte-locked source, so replacement releases that private staging
            // lease at the replace syscall and immediately reacquires it through the final path,
            // while the old destination remains locked. The GUID staging path deliberately gets no
            // named carrier of its own; creating one per replacement would leak permanent files
            // into the shared carrier directory.
            replacementPushCarrier = ManagedReplicaPhysicalCarrierLease.AcquireAsync(
                    replacementPath,
                    ManagedReplicaLockCarrier.PushKind,
                    cancellationToken)
                .AsTask().GetAwaiter().GetResult();
            replacementApplyCarrier = ManagedReplicaPhysicalCarrierLease.AcquireAsync(
                    replacementPath,
                    ManagedReplicaLockCarrier.ApplyKind,
                    cancellationToken)
                .AsTask().GetAwaiter().GetResult();
            destinationMainFileLease = new SqliteWalByteRangeLock(path).AcquireExclusive(
                PendingByte,
                SQLiteExclusiveRangeLength,
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            replacementMainFileLease = new SqliteWalByteRangeLock(replacementPath).AcquireExclusive(
                PendingByte,
                SQLiteExclusiveRangeLength,
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            return new MainFileReplacementLease(
                destinationMainFileLease,
                replacementMainFileLease,
                replacementPushCarrier,
                replacementApplyCarrier);
        }
        catch
        {
            replacementMainFileLease?.Dispose();
            destinationMainFileLease?.Dispose();
            replacementApplyCarrier?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            replacementPushCarrier?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    internal static void ReplaceMainFile(
        IDisposable? replacementLock,
        string replacementPath,
        string destinationPath,
        string? backupPath,
        Action replacementCompleted)
    {
        if (replacementLock is MainFileReplacementLease lease)
        {
            lease.Replace(replacementPath, destinationPath, backupPath, replacementCompleted);
            return;
        }

        File.Replace(replacementPath, destinationPath, backupPath, ignoreMetadataErrors: false);
        replacementCompleted();
    }

    internal static void RollBackMainFile(
        IDisposable? replacementLock,
        string backupPath,
        string destinationPath,
        string displacedPath)
    {
        if (replacementLock is MainFileReplacementLease lease)
        {
            lease.RollBack(backupPath, destinationPath, displacedPath);
            return;
        }

        File.Replace(backupPath, destinationPath, displacedPath, ignoreMetadataErrors: false);
    }

    private sealed class MainFileReplacementLease(
        IDisposable destinationMainFileLease,
        IDisposable replacementMainFileLease,
        ManagedReplicaPhysicalCarrierLease replacementPushCarrier,
        ManagedReplicaPhysicalCarrierLease replacementApplyCarrier) : IDisposable
    {
        private IDisposable? _destinationMainFileLease = destinationMainFileLease;
        private IDisposable? _replacementMainFileLease = replacementMainFileLease;
        private ManagedReplicaPhysicalCarrierLease? _replacementPushCarrier = replacementPushCarrier;
        private ManagedReplicaPhysicalCarrierLease? _replacementApplyCarrier = replacementApplyCarrier;

        internal void ReleaseMainFileLease()
            => Interlocked.Exchange(ref _destinationMainFileLease, null)?.Dispose();

        internal void Replace(
            string replacementPath,
            string destinationPath,
            string? backupPath,
            Action replacementCompleted)
        {
            if (OperatingSystem.IsWindows())
            {
                Interlocked.Exchange(ref _replacementMainFileLease, null)?.Dispose();
                ManagedReplicaFaultInjection.Hit(
                    ManagedReplicaDurableBoundary.MainFileReplacementSourceLeaseReleased);
            }

            File.Replace(replacementPath, destinationPath, backupPath, ignoreMetadataErrors: false);
            replacementCompleted();
            if (OperatingSystem.IsWindows())
            {
                ManagedReplicaFaultInjection.Hit(
                    ManagedReplicaDurableBoundary.MainFileReplacementPublishedBeforeLease);
                _replacementMainFileLease = AcquireSqliteMainFileLease(destinationPath);
            }
        }

        internal void RollBack(string backupPath, string destinationPath, string displacedPath)
        {
            if (OperatingSystem.IsWindows())
            {
                ReleaseMainFileLease();
                Interlocked.Exchange(ref _replacementMainFileLease, null)?.Dispose();
                ManagedReplicaFaultInjection.Hit(
                    ManagedReplicaDurableBoundary.MainFileRollbackLeasesReleased);
            }
            File.Replace(backupPath, destinationPath, displacedPath, ignoreMetadataErrors: false);
            if (OperatingSystem.IsWindows())
            {
                // A writer may have entered either inode while ReplaceFile required their leases
                // to be released. Reacquire both before rollback state or sidecars are reconciled.
                _destinationMainFileLease = AcquireSqliteMainFileLease(destinationPath);
                _replacementMainFileLease = AcquireSqliteMainFileLease(displacedPath);
            }
        }

        public void Dispose()
        {
            ReleaseMainFileLease();
            Interlocked.Exchange(ref _replacementMainFileLease, null)?.Dispose();
            Interlocked.Exchange(ref _replacementApplyCarrier, null)
                ?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Interlocked.Exchange(ref _replacementPushCarrier, null)
                ?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        private IDisposable AcquireSqliteMainFileLease(string path)
            => new SqliteWalByteRangeLock(path).AcquireExclusive(
                PendingByte,
                SQLiteExclusiveRangeLength,
                Timeout.InfiniteTimeSpan,
                CancellationToken.None);
    }
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

        var gates = new List<SemaphoreSlim>(2);
        var carrierLeases = new List<SqliteWalByteRangeLockLease>(2);
        try
        {
            var stableCarrier = ManagedReplicaLockCarrier.EnsureStable(
                databasePath,
                ManagedReplicaLockCarrier.JournalKind);
            var stableGate = Gates.GetOrAdd(stableCarrier, static _ => new SemaphoreSlim(1, 1));
            stableGate.Wait();
            gates.Add(stableGate);
            carrierLeases.Add(new SqliteWalByteRangeLock(stableCarrier).AcquireExclusive(
                offset: 0,
                length: 1,
                Timeout.InfiniteTimeSpan,
                CancellationToken.None));

            while (ManagedReplicaLockCarrier.EnsurePhysical(
                       databasePath,
                       ManagedReplicaLockCarrier.JournalKind) is { } physicalCarrier)
            {
                var physicalGate = Gates.GetOrAdd(physicalCarrier, static _ => new SemaphoreSlim(1, 1));
                physicalGate.Wait();
                gates.Add(physicalGate);
                var physicalLease = new SqliteWalByteRangeLock(physicalCarrier).AcquireExclusive(
                    offset: 0,
                    length: 1,
                    Timeout.InfiniteTimeSpan,
                    CancellationToken.None);
                if (string.Equals(
                        ManagedReplicaLockCarrier.TryResolvePhysical(
                            databasePath,
                            ManagedReplicaLockCarrier.JournalKind),
                        physicalCarrier,
                        StringComparison.Ordinal))
                {
                    carrierLeases.Add(physicalLease);
                    break;
                }

                physicalLease.Dispose();
                gates.RemoveAt(gates.Count - 1);
                physicalGate.Release();
            }

            return new Lease(gates, carrierLeases);
        }
        catch
        {
            for (var index = carrierLeases.Count - 1; index >= 0; index--)
                carrierLeases[index].Dispose();
            for (var index = gates.Count - 1; index >= 0; index--)
                gates[index].Release();
            throw;
        }
    }

    private sealed class Lease(
        IReadOnlyList<SemaphoreSlim> gates,
        IReadOnlyList<SqliteWalByteRangeLockLease> carrierLeases) : IDisposable
    {
        private IReadOnlyList<SemaphoreSlim>? _gates = gates;
        private IReadOnlyList<SqliteWalByteRangeLockLease>? _carrierLeases = carrierLeases;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _carrierLeases, null) is { } leases)
            {
                for (var index = leases.Count - 1; index >= 0; index--)
                    leases[index].Dispose();
            }
            if (Interlocked.Exchange(ref _gates, null) is { } heldGates)
            {
                for (var index = heldGates.Count - 1; index >= 0; index--)
                    heldGates[index].Release();
            }
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
        var gates = new List<SemaphoreSlim>(2);
        var carrierLeases = new List<SqliteWalByteRangeLockLease>(2);
        ManagedReplicaPhysicalCarrierLease? physicalLease = null;
        try
        {
            var stableCarrier = ManagedReplicaLockCarrier.EnsureStable(
                databasePath,
                ManagedReplicaLockCarrier.PushKind);
            var stableGate = Gates.GetOrAdd(stableCarrier, static _ => new SemaphoreSlim(1, 1));
            await stableGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gates.Add(stableGate);
            carrierLeases.Add(new SqliteWalByteRangeLock(stableCarrier).AcquireExclusive(
                offset: 0,
                length: 1,
                Timeout.InfiniteTimeSpan,
                cancellationToken));

            physicalLease = await ManagedReplicaPhysicalCarrierLease.AcquireAsync(
                    databasePath,
                    ManagedReplicaLockCarrier.PushKind,
                    cancellationToken)
                .ConfigureAwait(false);
            return new Lease(gates, carrierLeases, physicalLease);
        }
        catch
        {
            if (physicalLease is not null)
                await physicalLease.DisposeAsync().ConfigureAwait(false);
            for (var index = carrierLeases.Count - 1; index >= 0; index--)
                carrierLeases[index].Dispose();
            for (var index = gates.Count - 1; index >= 0; index--)
                gates[index].Release();
            throw;
        }
    }

    private sealed class Lease(
        IReadOnlyList<SemaphoreSlim> gates,
        IReadOnlyList<SqliteWalByteRangeLockLease> carrierLeases,
        ManagedReplicaPhysicalCarrierLease physicalLease) : IAsyncDisposable
    {
        private IReadOnlyList<SemaphoreSlim>? _gates = gates;
        private IReadOnlyList<SqliteWalByteRangeLockLease>? _carrierLeases = carrierLeases;
        private ManagedReplicaPhysicalCarrierLease? _physicalLease = physicalLease;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _physicalLease, null) is { } physical)
                await physical.DisposeAsync().ConfigureAwait(false);
            if (Interlocked.Exchange(ref _carrierLeases, null) is { } leases)
            {
                for (var index = leases.Count - 1; index >= 0; index--)
                    leases[index].Dispose();
            }
            if (Interlocked.Exchange(ref _gates, null) is { } heldGates)
            {
                for (var index = heldGates.Count - 1; index >= 0; index--)
                    heldGates[index].Release();
            }
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
        var localLeases = new List<IAsyncDisposable>(2);
        var carrierLeases = new List<SqliteWalByteRangeLockLease>(2);
        ManagedReplicaPhysicalCarrierLease? physicalLease = null;
        try
        {
            var stableCarrier = ManagedReplicaLockCarrier.EnsureStable(
                path,
                ManagedReplicaLockCarrier.ApplyKind);
            localLeases.Add(await InProcessManagedReplicaApplyLockCoordinator
                .AcquireCarrierAsync(stableCarrier, cancellationToken)
                .ConfigureAwait(false));
            carrierLeases.Add(new SqliteWalByteRangeLock(stableCarrier).AcquireExclusive(
                offset: 0,
                length: 1,
                Timeout.InfiniteTimeSpan,
                cancellationToken));

            physicalLease = await ManagedReplicaPhysicalCarrierLease.AcquireAsync(
                    path,
                    ManagedReplicaLockCarrier.ApplyKind,
                    cancellationToken)
                .ConfigureAwait(false);
            return new Lease(localLeases, carrierLeases, physicalLease);
        }
        catch
        {
            if (physicalLease is not null)
                await physicalLease.DisposeAsync().ConfigureAwait(false);
            for (var index = carrierLeases.Count - 1; index >= 0; index--)
                carrierLeases[index].Dispose();
            for (var index = localLeases.Count - 1; index >= 0; index--)
                await localLeases[index].DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Lease(
        IReadOnlyList<IAsyncDisposable> localLeases,
        IReadOnlyList<SqliteWalByteRangeLockLease> carrierLeases,
        ManagedReplicaPhysicalCarrierLease physicalLease) : IAsyncDisposable
    {
        private IReadOnlyList<IAsyncDisposable>? _localLeases = localLeases;
        private IReadOnlyList<SqliteWalByteRangeLockLease>? _carrierLeases = carrierLeases;
        private ManagedReplicaPhysicalCarrierLease? _physicalLease = physicalLease;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _physicalLease, null) is { } physical)
                await physical.DisposeAsync().ConfigureAwait(false);
            if (Interlocked.Exchange(ref _carrierLeases, null) is { } carrierLocks)
            {
                for (var index = carrierLocks.Count - 1; index >= 0; index--)
                    carrierLocks[index].Dispose();
            }
            if (Interlocked.Exchange(ref _localLeases, null) is { } localLocks)
            {
                for (var index = localLocks.Count - 1; index >= 0; index--)
                    await localLocks[index].DisposeAsync().ConfigureAwait(false);
            }
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
/// Gates are created lazily per resolved carrier path and never removed. A real replica
/// process only ever touches a small, effectively static set of distinct database paths over its
/// lifetime, so this keeps the implementation simple and lock-free on the hot (uncontended) path
/// rather than adding reference-counted teardown for a resource (one <see cref="SemaphoreSlim"/>
/// per distinct key) that is not worth the extra bookkeeping.
/// </remarks>
internal sealed class InProcessManagedReplicaApplyLockCoordinator : IManagedReplicaApplyLockCoordinator
{
    internal static readonly InProcessManagedReplicaApplyLockCoordinator Instance = new();

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.Ordinal);

    public async ValueTask<IAsyncDisposable> AcquireExclusiveAsync(string path, CancellationToken cancellationToken)
    {
        var leases = new List<IAsyncDisposable>(2);
        try
        {
            var stableCarrier = ManagedReplicaLockCarrier.EnsureStable(
                path,
                ManagedReplicaLockCarrier.ApplyKind);
            leases.Add(await AcquireCarrierAsync(stableCarrier, cancellationToken).ConfigureAwait(false));
            while (ManagedReplicaLockCarrier.TryResolvePhysical(
                       path,
                       ManagedReplicaLockCarrier.ApplyKind) is { } physicalCarrier)
            {
                var physicalLease = await AcquireCarrierAsync(physicalCarrier, cancellationToken)
                    .ConfigureAwait(false);
                leases.Add(physicalLease);
                if (string.Equals(
                        ManagedReplicaLockCarrier.TryResolvePhysical(
                            path,
                            ManagedReplicaLockCarrier.ApplyKind),
                        physicalCarrier,
                        StringComparison.Ordinal))
                {
                    break;
                }

                leases.RemoveAt(leases.Count - 1);
                await physicalLease.DisposeAsync().ConfigureAwait(false);
            }
            return new CompositeLease(leases);
        }
        catch
        {
            for (var index = leases.Count - 1; index >= 0; index--)
                await leases[index].DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static async ValueTask<IAsyncDisposable> AcquireCarrierAsync(
        string carrierPath,
        CancellationToken cancellationToken)
    {
        var gate = Gates.GetOrAdd(carrierPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(gate);
    }

    private sealed class CompositeLease(IReadOnlyList<IAsyncDisposable> leases) : IAsyncDisposable
    {
        private IReadOnlyList<IAsyncDisposable>? _leases = leases;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _leases, null) is { } heldLeases)
            {
                for (var index = heldLeases.Count - 1; index >= 0; index--)
                    await heldLeases[index].DisposeAsync().ConfigureAwait(false);
            }
        }
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
