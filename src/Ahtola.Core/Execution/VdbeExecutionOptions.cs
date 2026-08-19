using Ahtola.Core.Storage;

namespace Ahtola.Core.Execution;

/// <summary>
/// Supplies statement-local execution resources to a <see cref="ResumableStatement"/>.
/// </summary>
/// <remarks>
/// Sorter runs are transient execution artifacts. They use <see cref="TemporaryFileSystem"/>
/// exclusively and are never part of the SQLite database, WAL, or catalog format.
/// </remarks>
public sealed class VdbeExecutionOptions
{
    /// <summary>
    /// The default sorter memory budget, matching SQLite's default
    /// <c>cache_size=-2000</c> (2 MiB).
    /// </summary>
    public const long DefaultSorterMemoryLimitBytes = 2L * 1024 * 1024;
    public const int DefaultSorterMergeFanIn = 32;

    /// <summary>
    /// Creates execution resources for one or more statements.
    /// </summary>
    /// <param name="temporaryFileSystem">The backend that owns temporary sorter runs.</param>
    /// <param name="sorterMemoryLimitBytes">The maximum buffered sorter payload before spilling.</param>
    /// <param name="temporaryDirectory">The existing directory or logical path prefix for run files.</param>
    /// <param name="sorterMergeFanIn">The maximum run heads retained by a merge pass.</param>
    public VdbeExecutionOptions(
        IFileSystem temporaryFileSystem,
        long sorterMemoryLimitBytes = DefaultSorterMemoryLimitBytes,
        string? temporaryDirectory = null,
        int sorterMergeFanIn = DefaultSorterMergeFanIn)
    {
        ArgumentNullException.ThrowIfNull(temporaryFileSystem);
        ArgumentOutOfRangeException.ThrowIfLessThan(sorterMemoryLimitBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sorterMergeFanIn, 2);

        TemporaryFileSystem = temporaryFileSystem;
        SorterMemoryLimitBytes = sorterMemoryLimitBytes;
        SorterMergeFanIn = sorterMergeFanIn;
        TemporaryDirectory = string.IsNullOrWhiteSpace(temporaryDirectory)
            ? Path.GetTempPath()
            : temporaryDirectory;
    }

    /// <summary>The file-system abstraction that stores transient sorter runs.</summary>
    public IFileSystem TemporaryFileSystem { get; }

    /// <summary>The maximum buffered sorter payload before an external run is written.</summary>
    public long SorterMemoryLimitBytes { get; }

    /// <summary>The maximum run heads retained by a single merge pass.</summary>
    public int SorterMergeFanIn { get; }

    /// <summary>The existing directory or logical path prefix used for temporary run names.</summary>
    public string TemporaryDirectory { get; }

    internal static VdbeExecutionOptions Default { get; } =
        new(PhysicalFileSystem.Instance);
}
