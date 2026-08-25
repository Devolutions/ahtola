using Ahtola.Core.Storage;

namespace Ahtola.Core.Execution;

/// <summary>
/// Supplies statement-local execution resources to a <see cref="ResumableStatement"/>.
/// </summary>
/// <remarks>
/// Spilled execution intermediates are transient artifacts. They use
/// <see cref="TemporaryFileSystem"/> exclusively and are never part of the SQLite
/// database, WAL, or catalog format.
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
    /// <param name="temporaryFileSystem">The backend that owns temporary execution spill files.</param>
    /// <param name="sorterMemoryLimitBytes">
    /// The retained-memory budget for spill-aware operators. The name is retained for source compatibility.
    /// </param>
    /// <param name="temporaryDirectory">The existing directory or logical path prefix for spill files.</param>
    /// <param name="sorterMergeFanIn">
    /// The upper bound on run heads retained by a merge pass. The runtime reduces it when
    /// the statement's available memory cannot hold that many managed rows and heap nodes.
    /// </param>
    /// <param name="allowTemporaryFileSpill">
    /// Whether an operator may exceed its in-memory share by writing to the temporary file system.
    /// Set this to <see langword="false"/> for <c>temp_store=MEMORY</c>, where writing the same
    /// payload to an in-memory file system would not bound the managed heap.
    /// </param>
    /// <param name="metrics">Optional execution metrics. A new instance is created when omitted.</param>
    public VdbeExecutionOptions(
        IFileSystem temporaryFileSystem,
        long sorterMemoryLimitBytes = DefaultSorterMemoryLimitBytes,
        string? temporaryDirectory = null,
        int sorterMergeFanIn = DefaultSorterMergeFanIn,
        bool allowTemporaryFileSpill = true,
        VdbeExecutionMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(temporaryFileSystem);
        ArgumentOutOfRangeException.ThrowIfLessThan(sorterMemoryLimitBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sorterMergeFanIn, 2);

        TemporaryFileSystem = temporaryFileSystem;
        SorterMemoryLimitBytes = sorterMemoryLimitBytes;
        SorterMergeFanIn = sorterMergeFanIn;
        AllowTemporaryFileSpill = allowTemporaryFileSpill;
        Metrics = metrics ?? new VdbeExecutionMetrics();
        TemporaryDirectory = string.IsNullOrWhiteSpace(temporaryDirectory)
            ? Path.GetTempPath()
            : temporaryDirectory;
    }

    /// <summary>The file-system abstraction that stores transient execution spill files.</summary>
    public IFileSystem TemporaryFileSystem { get; }

    /// <summary>
    /// The retained-memory budget for spill-aware operators. The property name is retained for compatibility.
    /// </summary>
    public long SorterMemoryLimitBytes { get; }

    /// <summary>
    /// The hard retained-memory budget shared by spill-aware execution intermediates in one statement.
    /// </summary>
    public long MemoryLimitBytes => SorterMemoryLimitBytes;

    /// <summary>
    /// The configured upper bound on run heads retained by a single merge pass.
    /// The effective fan-in is also bounded by the statement's available memory.
    /// </summary>
    public int SorterMergeFanIn { get; }

    /// <summary>Whether operators may use the temporary file system after exhausting memory.</summary>
    public bool AllowTemporaryFileSpill { get; }

    /// <summary>High-water and spill counters for executions using these options.</summary>
    public VdbeExecutionMetrics Metrics { get; }

    /// <summary>The existing directory or logical path prefix used for temporary spill names.</summary>
    public string TemporaryDirectory { get; }

    internal static VdbeExecutionOptions Default { get; } =
        new(PhysicalFileSystem.Instance);
}
