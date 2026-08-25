namespace Ahtola.Core.Storage;

/// <summary>
/// How a file should be opened by an <see cref="IFileSystem"/>.
/// </summary>
public enum FileOpenMode
{
    /// <summary>Open a file that must already exist.</summary>
    OpenExisting,

    /// <summary>Open the file if it exists, otherwise create it.</summary>
    OpenOrCreate,

    /// <summary>Create a new file, failing if one already exists.</summary>
    CreateNew,
}

/// <summary>
/// The kind of I/O a file is performing. Used by deterministic backends to
/// classify and, in tests, inject faults for a specific operation.
/// </summary>
public enum FileSystemOperation
{
    Read = 0,
    Write = 1,
    SetLength = 2,
    FlushToDisk = 3,
    AtomicReplace = 4,
    Open = 5,
    Delete = 6,
    FileExists = 7,
    GetWriteStamp = 8,
    GetLength = 9,
    OpenTemporary = 10,
    EnsureMaterialized = 11,
    Dispose = 12,
}

/// <summary>
/// A cheap content-activity signal for a file: its length plus the last time a
/// writer modified it. Foreign read-only pagers compare stamps across statement
/// boundaries to detect owner commits that leave no header metadata change
/// (a checkpoint that rewrites pages in place without touching the header).
/// </summary>
public readonly record struct FileWriteStamp(long Length, DateTimeOffset LastWriteTimeUtc);

/// <summary>
/// Optional capability that gives a storage backend authority over canonical
/// path identity. Host file systems can return absolute paths while browser or
/// in-memory stores can retain logical keys.
/// </summary>
public interface IStoragePathResolver
{
    /// <summary>Returns the stable identity used for <paramref name="path"/>.</summary>
    string GetCanonicalPath(string path);

    /// <summary>Compares canonical paths produced by this resolver.</summary>
    StringComparer PathComparer { get; }
}

/// <summary>
/// Minimal, correctness-first storage abstraction. Backends provide durable,
/// positional (offset addressed) access to files. This mirrors the split
/// between the Rust <c>IO</c> and <c>File</c> traits used by the core engine:
/// the file system opens named files and each <see cref="IFile"/> exposes
/// positional reads and writes that never rely on an implicit cursor.
/// </summary>
public interface IFileSystem
{
    /// <summary>Returns whether a file exists at <paramref name="path"/>.</summary>
    bool FileExists(string path);

    /// <summary>
    /// Opens a file for positional access. When <paramref name="readOnly"/> is
    /// <see langword="true"/> the returned handle rejects mutating operations.
    /// </summary>
    IFile OpenFile(string path, FileOpenMode mode, bool readOnly = false);

    /// <summary>Deletes the file at <paramref name="path"/> if it exists.</summary>
    void DeleteFile(string path);

    /// <summary>
    /// The current write stamp of a file, or <see langword="null"/> when the
    /// file does not exist or this backend cannot observe write activity.
    /// Foreign read-only change detection degrades to header metadata when a
    /// backend returns <see langword="null"/> here.
    /// </summary>
    FileWriteStamp? GetWriteStamp(string path) => null;
}

/// <summary>
/// Publishes a fully written sibling file at its final path without exposing a
/// partial destination image.
/// </summary>
internal interface IAtomicFileSystem
{
    void ReplaceFileAtomically(
        string sourcePath,
        string destinationPath,
        bool replaceEmptyDestination);
}

/// <summary>
/// Optional temporary-file capability. Implementations that can ask the host to
/// remove a file when its final handle closes expose it without leaking host I/O
/// APIs into execution code.
/// </summary>
internal interface ITemporaryFileSystem
{
    IFile OpenTemporaryFile(string path);
}

/// <summary>
/// A positionally addressed file handle. All reads and writes take an explicit
/// absolute byte offset so a single handle can be used concurrently without a
/// shared cursor, matching the <c>pread</c>/<c>pwrite</c> model the engine uses.
/// </summary>
public interface IFile : IDisposable
{
    /// <summary>Current length of the file in bytes.</summary>
    long Length { get; }

    /// <summary>Whether this handle was opened read-only.</summary>
    bool IsReadOnly { get; }

    /// <summary>
    /// Reads into <paramref name="destination"/> starting at <paramref name="position"/>,
    /// returning the number of bytes read. A return value shorter than the
    /// destination indicates end-of-file; callers that require a full read must
    /// treat a short read as truncation.
    /// </summary>
    int Read(long position, Span<byte> destination);

    /// <summary>
    /// Writes the whole of <paramref name="source"/> at <paramref name="position"/>,
    /// growing the file if the write extends past its current end.
    /// </summary>
    void Write(long position, ReadOnlySpan<byte> source);

    /// <summary>Sets the file length, truncating or zero-extending as needed.</summary>
    void SetLength(long length);

    /// <summary>Flushes buffered data and metadata to durable storage.</summary>
    void FlushToDisk();
}

/// <summary>
/// Optional capability for a sparse database file whose missing byte ranges
/// must be populated before they can be read.
/// </summary>
/// <remarks>
/// The pager probes this capability before entering its internal read locks.
/// The file still enforces the same check in <see cref="IFile.Read"/> so direct
/// page-store callers can never mistake a sparse hole for valid zero bytes.
/// </remarks>
internal interface IPageMaterializingFile
{
    void EnsureMaterialized(long position, int length);
}

/// <summary>
/// Identifies a file-system decorator whose underlying identity and optional
/// capabilities remain authoritative for pager locking and storage policy.
/// </summary>
internal interface IFileSystemDecorator
{
    IFileSystem InnerFileSystem { get; }
}

/// <summary>
/// Lets a file system advertise the page codec every database opened through it
/// must use, without being wrapped in <see cref="AhtolaPageCodecFileSystem"/>.
/// </summary>
/// <remarks>
/// Wrapping hides optional capabilities such as <see cref="IAtomicFileSystem"/>
/// and <see cref="ITemporaryFileSystem"/>, which storage adapters that implement
/// those interfaces themselves cannot afford to lose. Implementations must return
/// a stable instance so every pager, WAL, and journal opened from the same file
/// system agrees on the on-disk layout.
/// </remarks>
internal interface IPageCodecSource
{
    IPageCodec? PageCodec { get; }
}

/// <summary>
/// Optional capability that lets a storage backend refuse Turso MVCC before the
/// engine persists journal-mode header 255 or produces a single logical-log frame.
/// </summary>
/// <remarks>
/// Backends that encrypt out of band — the browser mirror encrypts on its way to
/// OPFS rather than through <see cref="AhtolaEncryptionFileSystem"/> — are
/// invisible to the core's own logical-log encryption check, so they declare the
/// restriction here instead. Implementations must be able to answer without any
/// I/O, because the engine asks at the <c>PRAGMA journal_mode</c> boundary.
/// </remarks>
internal interface IMvccJournalModePolicy
{
    /// <summary>
    /// <see langword="null"/> when this backend can host an MVCC logical log,
    /// otherwise the fail-closed reason reported to the caller.
    /// </summary>
    string? DescribeMvccUnsupportedReason();
}

/// <summary>
/// Optional capability for proving that two file-backed databases cannot
/// alias the same underlying file during a managed snapshot copy.
/// </summary>
internal interface ISnapshotFileIdentity
{
    bool CanProveDistinctFile(
        string path,
        IFileSystem otherFileSystem,
        string otherPath);
}
