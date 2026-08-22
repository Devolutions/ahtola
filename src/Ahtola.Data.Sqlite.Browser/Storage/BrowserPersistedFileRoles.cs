using Ahtola.Core.Storage;

namespace Ahtola.Data.Sqlite.Browser.Storage;

/// <summary>The role a persisted OPFS file plays for AHTLA page encryption.</summary>
internal enum BrowserPersistedFileRole
{
    /// <summary>A page-structured SQLite database: main, attached, or a VACUUM target.</summary>
    Database,

    /// <summary>A write-ahead log whose frame bodies are encrypted.</summary>
    Wal,

    /// <summary>A DELETE-mode rollback journal whose page records are encrypted.</summary>
    Journal,

    /// <summary>The rebuildable WAL index, which carries no page content.</summary>
    SharedMemory,

    /// <summary>An MVCC logical log, which the engine writes outside the page codec.</summary>
    MvccLog,
}

/// <summary>
/// Resolves persisted OPFS paths to encryption roles by anchoring sidecars to
/// databases that are actually known, instead of trusting a filename suffix.
/// </summary>
/// <remarks>
/// <para>
/// A suffix test alone is unsafe: a perfectly legal database can be named
/// <c>notes-shm</c> or attached as <c>archive-wal</c>, and treating it as a
/// sidecar would either corrupt it or, for <c>-shm</c>, write its pages to OPFS
/// in the clear. A path is only a sidecar when the database it would belong to
/// is itself known, and a content probe vetoes the classification when the file
/// still looks like a database.
/// </para>
/// <para>
/// Databases are discovered in open order at run time (the engine always opens a
/// database before its sidecars) and in shortest-path-first order at load time,
/// which guarantees a base path is resolved before anything derived from it.
/// </para>
/// </remarks>
internal sealed class BrowserPersistedFileRoles
{
    // EmbeddedFileStore builds these names with Guid.NewGuid():N, so an exact
    // shape match proves the file is an engine temporary rather than user data.
    private static readonly string[] TransientPrefixes = [".vacuum-", ".page-size-"];
    private const string TransientSuffix = ".tmp";
    private const string MvccUpgradeSuffix = ".v4-upgrade";
    private const int GuidHexLength = 32;

    private static readonly (string Suffix, BrowserPersistedFileRole Role)[] SidecarSuffixes =
    [
        ("-journal", BrowserPersistedFileRole.Journal),
        ("-wal", BrowserPersistedFileRole.Wal),
        ("-shm", BrowserPersistedFileRole.SharedMemory),
        ("-log", BrowserPersistedFileRole.MvccLog),
    ];

    private readonly Dictionary<string, BrowserPersistedFileRole> _roles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _databases = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether <paramref name="path"/> has the exact shape of an engine temporary.
    /// </summary>
    /// <remarks>
    /// This is a <em>load-time</em> question only. A live VACUUM or page-size
    /// migration writes its rebuilt database through the persisted mirror at one
    /// of these paths and then publishes it atomically, so treating the name as
    /// "not real data" while the engine is writing would push a plaintext database
    /// to OPFS. During a load, by contrast, nothing holds these files and they are
    /// provably abandoned. <see cref="Classify"/> therefore never consults this.
    /// </remarks>
    internal static bool IsTransientArtifact(string path)
    {
        if (path.EndsWith(MvccUpgradeSuffix, StringComparison.Ordinal))
            return true;

        var candidate = path.AsSpan();
        foreach (var (suffix, _) in SidecarSuffixes)
        {
            if (candidate.EndsWith(suffix, StringComparison.Ordinal))
            {
                candidate = candidate[..^suffix.Length];
                break;
            }
        }

        if (!candidate.EndsWith(TransientSuffix, StringComparison.Ordinal))
            return false;

        candidate = candidate[..^TransientSuffix.Length];
        if (candidate.Length < GuidHexLength)
            return false;

        var guid = candidate[^GuidHexLength..];
        foreach (var character in guid)
        {
            if (!char.IsAsciiHexDigitLower(character))
                return false;
        }

        var head = candidate[..^GuidHexLength];
        foreach (var prefix in TransientPrefixes)
        {
            if (head.EndsWith(prefix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>Declares a path that is known to be a database up front.</summary>
    internal void RegisterDatabase(string path)
    {
        _databases.Add(path);
        _roles[path] = BrowserPersistedFileRole.Database;
    }

    /// <summary>Forgets a deleted path so a later file reusing the name is reclassified.</summary>
    internal void Forget(string path)
    {
        _roles.Remove(path);
        _databases.Remove(path);
    }

    /// <summary>Moves a role along with an atomically replaced file.</summary>
    internal void Rename(string sourcePath, string destinationPath)
    {
        var moved = _roles.Remove(sourcePath, out var role);
        _databases.Remove(sourcePath);
        if (!moved)
        {
            _roles.Remove(destinationPath);
            _databases.Remove(destinationPath);
            return;
        }

        _roles[destinationPath] = role;
        if (role == BrowserPersistedFileRole.Database)
            _databases.Add(destinationPath);
        else
            _databases.Remove(destinationPath);
    }

    /// <summary>
    /// Resolves the role of <paramref name="path"/>. <paramref name="probeHeader"/>
    /// supplies the first bytes of the file when they are available, so a database
    /// whose name merely looks like a sidecar is never demoted, and a genuine
    /// sidecar is still recognized when its base database is absent.
    /// </summary>
    internal BrowserPersistedFileRole Resolve(
        string path,
        ReadOnlySpan<byte> probeHeader,
        Func<string, bool>? basePathExists = null)
    {
        if (_roles.TryGetValue(path, out var known))
            return known;

        var role = Classify(path, probeHeader, basePathExists);
        _roles[path] = role;
        if (role == BrowserPersistedFileRole.Database)
            _databases.Add(path);
        return role;
    }

    private BrowserPersistedFileRole Classify(
        string path,
        ReadOnlySpan<byte> probeHeader,
        Func<string, bool>? basePathExists)
    {
        foreach (var (suffix, role) in SidecarSuffixes)
        {
            if (!path.EndsWith(suffix, StringComparison.Ordinal))
                continue;

            var basePath = path[..^suffix.Length];
            if (basePath.Length == 0)
                break;

            // A sidecar never begins with a database magic. If this one does, the
            // name collided with a real database and the sidecar role is wrong.
            if (LooksLikeDatabase(probeHeader))
                return BrowserPersistedFileRole.Database;

            // Positive identification first: a WAL and a finalized journal are
            // self-describing, so they stay recognizable even when their database
            // is missing. Otherwise the base database has to be known or present.
            if (role == BrowserPersistedFileRole.Wal && LooksLikeWal(probeHeader))
                return role;
            if (role == BrowserPersistedFileRole.Journal && LooksLikeJournal(probeHeader))
                return role;
            if (_databases.Contains(basePath) || basePathExists?.Invoke(basePath) == true)
                return role;

            break;
        }

        return BrowserPersistedFileRole.Database;
    }

    private static bool LooksLikeDatabase(ReadOnlySpan<byte> probeHeader)
        => AhtolaEncryptedPageFormat.IsAhtolaEncrypted(probeHeader)
           || (probeHeader.Length >= AhtolaEncryptedPageFormat.SqliteHeaderMagic.Length
               && probeHeader[..AhtolaEncryptedPageFormat.SqliteHeaderMagic.Length]
                   .SequenceEqual(AhtolaEncryptedPageFormat.SqliteHeaderMagic));

    private static bool LooksLikeWal(ReadOnlySpan<byte> probeHeader)
    {
        if (probeHeader.Length < 4)
            return false;
        var magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(probeHeader);
        return magic is SqliteWalHeader.LittleEndianChecksumMagic or SqliteWalHeader.BigEndianChecksumMagic;
    }

    private static bool LooksLikeJournal(ReadOnlySpan<byte> probeHeader)
        => SqliteRollbackJournalFormat.HasMagic(probeHeader);
}
