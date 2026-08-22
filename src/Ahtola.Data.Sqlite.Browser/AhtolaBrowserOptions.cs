using Ahtola.Data.Sqlite;

namespace Ahtola.Data.Sqlite.Browser;

/// <summary>
/// Configures an Ahtola database stored in an application-owned OPFS directory.
/// </summary>
public sealed class AhtolaBrowserOptions : IDisposable
{
    /// <summary>
    /// The default shared transfer buffer size used by the OPFS worker.
    /// </summary>
    public const int DefaultSharedBufferSize = 1024 * 1024;

    private AhtolaBrowserEncryptionOptions? _encryption;

    /// <summary>
    /// Creates immutable browser database options.
    /// </summary>
    /// <param name="databasePath">
    /// A relative OPFS database path located below <paramref name="ownedDirectory"/>.
    /// </param>
    /// <param name="ownedDirectory">The relative OPFS directory exclusively owned by this data source.</param>
    /// <param name="sharedBufferSize">The OPFS worker transfer buffer size, in bytes.</param>
    /// <param name="readOnly">Whether connections reject database mutations.</param>
    /// <param name="encryption">
    /// Optional AHTLA page-encryption key material. It is copied, never placed in
    /// <see cref="ConnectionString"/>, and released when these options are disposed.
    /// </param>
    public AhtolaBrowserOptions(
        string databasePath,
        string ownedDirectory,
        int sharedBufferSize = DefaultSharedBufferSize,
        bool readOnly = false,
        AhtolaBrowserEncryptionOptions? encryption = null)
    {
        IsInMemory = string.Equals(databasePath, ":memory:", StringComparison.Ordinal);
        if (IsInMemory)
        {
            if (!string.Equals(ownedDirectory, ":memory:", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "An in-memory browser data source must use ':memory:' as its owned directory.",
                    nameof(ownedDirectory));
            }
            if (readOnly)
                throw new ArgumentException("An empty in-memory browser database cannot be opened read-only.", nameof(readOnly));
            if (encryption is not null)
                throw new NotSupportedException("Encryption is not applicable to an in-memory browser database.");

            OwnedDirectory = ":memory:";
            DatabasePath = ":memory:";
        }
        else
        {
            OwnedDirectory = NormalizePath(ownedDirectory, nameof(ownedDirectory));
            DatabasePath = NormalizePath(databasePath, nameof(databasePath));
            if (!DatabasePath.StartsWith(OwnedDirectory + "/", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Browser database path '{DatabasePath}' must be below owned OPFS directory '{OwnedDirectory}'.",
                    nameof(databasePath));
            }
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(sharedBufferSize, 64 * 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sharedBufferSize, 64 * 1024 * 1024);
        SharedBufferSize = sharedBufferSize;
        IsReadOnly = readOnly;
        _encryption = encryption?.CreateOwnedCopy();

        // Key material is deliberately absent here: a connection string is routinely
        // logged, cached, and compared, so it must never carry a passphrase or key.
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = IsInMemory
                ? SqliteOpenMode.Memory
                : readOnly
                    ? SqliteOpenMode.ReadOnly
                    : SqliteOpenMode.ReadWriteCreate,
            Cache = IsInMemory ? SqliteCacheMode.Shared : SqliteCacheMode.Default,
            LocalProvider = AhtolaLocalProvider.Managed,
            Pooling = false,
        }.ConnectionString;
    }

    /// <summary>
    /// Creates options whose owned directory is the database path's parent directory.
    /// </summary>
    public AhtolaBrowserOptions(
        string databasePath,
        int sharedBufferSize = DefaultSharedBufferSize,
        bool readOnly = false,
        AhtolaBrowserEncryptionOptions? encryption = null)
        : this(
            databasePath,
            GetParentDirectory(databasePath),
            sharedBufferSize,
            readOnly,
            encryption)
    {
    }

    /// <summary>
    /// Gets this instance's copy of the AHTLA encryption key material, or
    /// <see langword="null"/> when the database is stored unencrypted.
    /// </summary>
    public AhtolaBrowserEncryptionOptions? Encryption => _encryption;

    /// <summary>Whether OPFS content is encrypted with Ahtola's AHTLA page format.</summary>
    public bool IsEncrypted => _encryption is not null;

    /// <summary>Whether this data source is process-memory-only and does not initialize OPFS.</summary>
    public bool IsInMemory { get; }

    /// <summary>Zeros this instance's copy of the encryption key material.</summary>
    public void Dispose()
    {
        var encryption = Interlocked.Exchange(ref _encryption, null);
        encryption?.Dispose();
    }

    /// <summary>Gets the normalized database path relative to the OPFS root.</summary>
    public string DatabasePath { get; }

    /// <summary>Gets the normalized OPFS directory owned by this data source.</summary>
    public string OwnedDirectory { get; }

    /// <summary>Gets the normalized OPFS directory owned by this data source.</summary>
    public string OwnedDirectoryPath => OwnedDirectory;

    /// <summary>Gets the OPFS worker transfer buffer size, in bytes.</summary>
    public int SharedBufferSize { get; }

    /// <summary>Gets whether connections are read-only.</summary>
    public bool IsReadOnly { get; }

    /// <summary>Gets whether connections are read-only.</summary>
    public bool ReadOnly => IsReadOnly;

    /// <summary>Gets the managed-provider connection string exposed by created connections.</summary>
    public string ConnectionString { get; }

    private static string NormalizePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.EndsWith('/'))
        {
            throw new ArgumentException(
                "Browser OPFS paths must be normalized relative paths without leading or trailing separators.",
                parameterName);
        }

        var segments = normalized.Split('/');
        if (segments.Any(static segment =>
                segment is "" or "." or ".."
                || segment.IndexOfAny(['\0', '\r', '\n']) >= 0))
        {
            throw new ArgumentException(
                "Browser OPFS paths must be relative and cannot contain empty, current, or parent segments.",
                parameterName);
        }

        return string.Join('/', segments);
    }

    private static string GetParentDirectory(string databasePath)
    {
        if (string.Equals(databasePath, ":memory:", StringComparison.Ordinal))
            return ":memory:";

        var normalized = NormalizePath(databasePath, nameof(databasePath));
        var separator = normalized.LastIndexOf('/');
        if (separator <= 0)
        {
            throw new ArgumentException(
                "The browser database path must include an owned parent directory.",
                nameof(databasePath));
        }

        return normalized[..separator];
    }
}
