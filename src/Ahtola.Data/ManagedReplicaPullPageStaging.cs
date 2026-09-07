using System.Buffers.Binary;

namespace Ahtola;

/// <summary>
/// Bounded, file-backed staging for the raw page set of a pull-updates response (Incremental or
/// ReplaceBase apply mode). Pages arrive off the network one at a time
/// (<see cref="ManagedReplicaBootstrapper.WaitForRemoteChangesAsync"/>) and are written straight to
/// a private temporary file as sequential (page id, page bytes) records instead of being
/// accumulated in an in-memory list: a ReplaceBase response for a large database can carry every
/// page the database has, and holding all of them as managed byte arrays at once would scale pull
/// memory with database size instead of with a single page's size. This mirrors the file-backed
/// page write the chunked bootstrap download already uses
/// (<see cref="ManagedReplicaBootstrapper.DownloadDatabaseAsync"/> and the chunk-fetch helper it
/// calls) -- this type simply extends that same bounded-staging pattern to the ordinary
/// wait-for-remote-changes pull path so both code paths hold at most one page in memory at a time.
/// </summary>
/// <remarks>
/// The record format is intentionally the simplest one that preserves existing observable
/// behavior: pages are stored in wire arrival order with no page-id indexing or deduplication at
/// write time, exactly mirroring what the in-memory <c>List&lt;PullPage&gt;</c> it replaces used to
/// do. Duplicate-page and out-of-range validation remain the apply step's responsibility (see
/// <see cref="ManagedReplicaBootstrapper.PullPage"/> consumers), unchanged from before this type
/// existed.
/// </remarks>
internal sealed class ManagedReplicaPullPageStaging : IDisposable
{
    private const int RecordHeaderLength = sizeof(ulong);

    private readonly string _filePath;
    private FileStream? _stream;
    private int _disposed;

    private ManagedReplicaPullPageStaging(string filePath, FileStream stream)
    {
        _filePath = filePath;
        _stream = stream;
    }

    /// <summary>Number of pages appended so far.</summary>
    internal int Count { get; private set; }

    /// <summary>
    /// Creates a new, empty staging file beside <paramref name="databasePath"/>, following the same
    /// hidden-dotfile-with-random-suffix convention every other replica staging path in
    /// <see cref="ManagedReplicaBootstrapper"/> uses.
    /// </summary>
    internal static ManagedReplicaPullPageStaging Create(string databasePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
        var filePath = Path.Combine(
            directory,
            $".{Path.GetFileName(databasePath)}.pull-pages-{Guid.NewGuid():N}.tmp");
        var stream = new FileStream(
            filePath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: ManagedReplicaBootstrapper.PageSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new ManagedReplicaPullPageStaging(filePath, stream);
    }

    /// <summary>Appends one page to the staging file, in wire arrival order.</summary>
    internal async Task AppendAsync(ulong pageId, byte[] data, CancellationToken cancellationToken)
    {
        if (data.Length != ManagedReplicaBootstrapper.PageSize)
            throw new InvalidDataException("The pull-updates response contains an invalid raw database page.");

        var stream = _stream ?? throw new ObjectDisposedException(nameof(ManagedReplicaPullPageStaging));
        var header = new byte[RecordHeaderLength];
        BinaryPrimitives.WriteUInt64LittleEndian(header, pageId);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        Count++;
    }

    /// <summary>Flushes buffered writes once every page has been appended.</summary>
    internal async Task CompleteAsync(CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new ObjectDisposedException(nameof(ManagedReplicaPullPageStaging));
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Enumerates the staged pages in the order they were appended, reading each record's fixed-
    /// size page payload one at a time so the caller never holds more than one page in memory at
    /// once. Must not be enumerated concurrently with <see cref="AppendAsync"/>/<see cref="CompleteAsync"/>
    /// or from more than one enumerator at a time (both would race the shared stream position).
    /// </summary>
    internal IEnumerable<ManagedReplicaBootstrapper.PullPage> ReadPages()
    {
        var stream = _stream ?? throw new ObjectDisposedException(nameof(ManagedReplicaPullPageStaging));
        stream.Position = 0;
        var header = new byte[RecordHeaderLength];
        for (var i = 0; i < Count; i++)
        {
            stream.ReadExactly(header);
            var pageId = BinaryPrimitives.ReadUInt64LittleEndian(header);
            var data = new byte[ManagedReplicaBootstrapper.PageSize];
            stream.ReadExactly(data);
            yield return new ManagedReplicaBootstrapper.PullPage(pageId, data);
        }
    }

    /// <summary>
    /// Idempotent: closes the underlying stream (if still open) and deletes the staging file (if it
    /// still exists). Safe to call more than once, and safe to call whether or not the staged pages
    /// were ever read -- a staged response discarded without being applied
    /// (<c>ManagedReplicaStagedChanges.Dispose</c>) must not leak its staging file, and one that was
    /// applied must not either once the apply step is done with it.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            return;

        _stream?.Dispose();
        _stream = null;
        try
        {
            File.Delete(_filePath);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a private temp file; a concurrent delete or a transient
            // sharing violation here must not surface as a pull failure.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
