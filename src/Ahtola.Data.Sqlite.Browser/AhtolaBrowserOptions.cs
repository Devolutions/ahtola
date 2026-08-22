using Ahtola.Data.Sqlite;

namespace Ahtola.Data.Sqlite.Browser;

/// <summary>
/// Configures an Ahtola database stored in an application-owned OPFS directory.
/// </summary>
public sealed class AhtolaBrowserOptions
{
    /// <summary>
    /// The default shared transfer buffer size used by the OPFS worker.
    /// </summary>
    public const int DefaultSharedBufferSize = 1024 * 1024;

    /// <summary>
    /// Creates immutable browser database options.
    /// </summary>
    /// <param name="databasePath">
    /// A relative OPFS database path located below <paramref name="ownedDirectory"/>.
    /// </param>
    /// <param name="ownedDirectory">The relative OPFS directory exclusively owned by this data source.</param>
    /// <param name="sharedBufferSize">The OPFS worker transfer buffer size, in bytes.</param>
    /// <param name="readOnly">Whether connections reject database mutations.</param>
    public AhtolaBrowserOptions(
        string databasePath,
        string ownedDirectory,
        int sharedBufferSize = DefaultSharedBufferSize,
        bool readOnly = false)
    {
        OwnedDirectory = NormalizePath(ownedDirectory, nameof(ownedDirectory));
        DatabasePath = NormalizePath(databasePath, nameof(databasePath));
        if (!DatabasePath.StartsWith(OwnedDirectory + "/", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Browser database path '{DatabasePath}' must be below owned OPFS directory '{OwnedDirectory}'.",
                nameof(databasePath));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(sharedBufferSize, 64 * 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sharedBufferSize, 64 * 1024 * 1024);
        SharedBufferSize = sharedBufferSize;
        IsReadOnly = readOnly;

        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
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
        bool readOnly = false)
        : this(
            databasePath,
            GetParentDirectory(databasePath),
            sharedBufferSize,
            readOnly)
    {
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
