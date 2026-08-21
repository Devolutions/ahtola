using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Ahtola.Core.Storage;

namespace Ahtola;

internal readonly record struct ManagedReplicaFetchedPage(ulong PageId, byte[] Data);

internal readonly record struct ManagedReplicaPageBatch(
    string Revision,
    ulong DatabasePages,
    IReadOnlyList<ManagedReplicaFetchedPage> Pages);

internal interface IManagedReplicaPageSource
{
    Task<ManagedReplicaPageBatch> FetchPagesAsync(
        string revision,
        IReadOnlyList<ulong> pageIds,
        CancellationToken cancellationToken);
}

internal sealed class ManagedReplicaPullPageSource(AhtolaReplicaOptions options) : IManagedReplicaPageSource
{
    public Task<ManagedReplicaPageBatch> FetchPagesAsync(
        string revision,
        IReadOnlyList<ulong> pageIds,
        CancellationToken cancellationToken)
        => ManagedReplicaBootstrapper.FetchPagesAsync(options, revision, pageIds, cancellationToken);
}

/// <summary>
/// Decorates the main file of a partial replica so sparse pages are fetched and
/// durably published before the pager can observe their bytes.
/// </summary>
internal sealed class ManagedReplicaPageMaterializingFileSystem :
    IFileSystem,
    IFileSystemDecorator,
    IDisposable
{
    internal const string StateSuffix = ".ahtola-replica-pages";
    internal const int DefaultSegmentSize = 128 * 1024;

    private readonly IFileSystem _inner;
    private readonly IFileSystem _decoratedInner;
    private readonly string _databasePath;
    private readonly ManagedReplicaPageMaterializer _materializer;
    private int _disposed;

    internal ManagedReplicaPageMaterializingFileSystem(
        IFileSystem inner,
        string databasePath,
        string expectedRevision,
        IManagedReplicaPageSource pageSource,
        bool prefetchSegments)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRevision);
        ArgumentNullException.ThrowIfNull(pageSource);

        _decoratedInner = inner;
        _inner = CreateStorageFileSystem(inner);
        _databasePath = NormalizePath(databasePath);
        _materializer = new ManagedReplicaPageMaterializer(
            _inner,
            databasePath,
            expectedRevision,
            pageSource,
            prefetchSegments);
    }

    internal static void InitializeState(
        IFileSystem fileSystem,
        string databasePath,
        string revision,
        ulong databasePages,
        int pageSize,
        long segmentSize,
        IEnumerable<ulong> materializedPageIds)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ManagedReplicaPageMaterializer.InitializeState(
            CreateStorageFileSystem(fileSystem),
            databasePath,
            revision,
            databasePages,
            pageSize,
            segmentSize,
            materializedPageIds);
    }

    internal bool IsSegmentMaterialized(ulong pageId)
    {
        ThrowIfDisposed();
        return _materializer.IsSegmentMaterialized(pageId);
    }

    IFileSystem IFileSystemDecorator.InnerFileSystem => _decoratedInner;

    public bool FileExists(string path)
    {
        ThrowIfDisposed();
        return _inner.FileExists(path);
    }

    public FileWriteStamp? GetWriteStamp(string path)
    {
        ThrowIfDisposed();
        return _inner.GetWriteStamp(path);
    }

    public IFile OpenFile(string path, FileOpenMode mode, bool readOnly = false)
    {
        ThrowIfDisposed();
        var file = _inner.OpenFile(path, mode, readOnly);
        if (!PathComparer.Equals(NormalizePath(path), _databasePath))
            return file;

        try
        {
            return new MaterializingFile(file, _materializer);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public void DeleteFile(string path)
    {
        ThrowIfDisposed();
        _inner.DeleteFile(path);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _materializer.Dispose();
    }

    private static IFileSystem CreateStorageFileSystem(IFileSystem fileSystem)
        => fileSystem switch
        {
            AhtolaEncryptionFileSystem encrypted when encrypted.Inner is PhysicalFileSystem physical =>
                encrypted.WithInner(new SqlitePagerPhysicalFileSystem(physical)),
            AhtolaPageCodecFileSystem codec when codec.Inner is PhysicalFileSystem physical =>
                codec.WithInner(new SqlitePagerPhysicalFileSystem(physical)),
            PhysicalFileSystem physical => new SqlitePagerPhysicalFileSystem(physical),
            _ => fileSystem,
        };

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string NormalizePath(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed class MaterializingFile(
        IFile inner,
        ManagedReplicaPageMaterializer materializer) : IFile, IPageMaterializingFile
    {
        public long Length => inner.Length;

        public bool IsReadOnly => inner.IsReadOnly;

        public int Read(long position, Span<byte> destination)
        {
            materializer.EnsureMaterialized(position, destination.Length);
            return inner.Read(position, destination);
        }

        public void Write(long position, ReadOnlySpan<byte> source)
            => materializer.Write(inner, position, source);

        public void SetLength(long length)
            => materializer.SetLength(inner, length);

        public void FlushToDisk()
            => materializer.FlushToDisk(inner);

        public void EnsureMaterialized(long position, int length)
            => materializer.EnsureMaterialized(position, length);

        public void Dispose() => inner.Dispose();
    }
}

internal sealed class ManagedReplicaPageMaterializer : IDisposable
{
    private const int StateVersion = 1;
    private const int CommitVersion = 1;
    private const int HeaderPrefixLength = 36;
    private const int CommitPayloadPrefixLength = 24;
    private const int HashLength = 32;
    private const int MaximumRevisionLength = 64 * 1024;
    private const int MaximumCommitPayloadLength = 64 * 1024 * 1024;
    private const ulong NoTruncation = ulong.MaxValue;
    private static readonly byte[] HeaderMagic = "AHTLPM01"u8.ToArray();
    private static readonly byte[] CommitMagic = "MPRC"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _publicationGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly IFile _databaseFile;
    private readonly IFile _stateFile;
    private readonly IManagedReplicaPageSource _pageSource;
    private readonly Dictionary<ulong, LoadGroup> _loads = [];
    private readonly PageRangeSet _materialized;
    private readonly PageRangeSet _pendingMaterialized = new();
    private readonly string _revision;
    private readonly int _pageSize;
    private readonly ulong _remotePageCount;
    private readonly ulong _segmentPages;
    private readonly bool _prefetchSegments;
    private ulong _currentPageCount;
    private ulong? _pendingTruncateTo;
    private int _disposed;

    internal ManagedReplicaPageMaterializer(
        IFileSystem fileSystem,
        string databasePath,
        string expectedRevision,
        IManagedReplicaPageSource pageSource,
        bool prefetchSegments)
    {
        _pageSource = pageSource;
        _prefetchSegments = prefetchSegments;

        IFile? stateFile = null;
        IFile? databaseFile = null;
        try
        {
            stateFile = fileSystem.OpenFile(
                databasePath + ManagedReplicaPageMaterializingFileSystem.StateSuffix,
                FileOpenMode.OpenExisting);
            var loaded = LoadState(stateFile);
            if (!string.Equals(loaded.Revision, expectedRevision, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Managed replica page state belongs to a different server revision.");
            }

            databaseFile = fileSystem.OpenFile(databasePath, FileOpenMode.OpenExisting);
            var expectedLength = checked((long)loaded.CurrentPageCount * loaded.PageSize);
            if (databaseFile.Length != expectedLength)
            {
                throw new InvalidDataException(
                    "Managed replica page state disagrees with the local database file length.");
            }

            if (!loaded.Materialized.Contains(0))
            {
                throw new InvalidDataException(
                    "Managed replica page state does not mark the SQLite header page as materialized.");
            }

            _stateFile = stateFile;
            stateFile = null;
            _databaseFile = databaseFile;
            databaseFile = null;
            _materialized = loaded.Materialized;
            _revision = loaded.Revision;
            _pageSize = loaded.PageSize;
            _remotePageCount = loaded.RemotePageCount;
            _currentPageCount = loaded.CurrentPageCount;
            _segmentPages = loaded.SegmentSize / checked((ulong)loaded.PageSize);
        }
        finally
        {
            stateFile?.Dispose();
            databaseFile?.Dispose();
        }
    }

    internal static void InitializeState(
        IFileSystem fileSystem,
        string databasePath,
        string revision,
        ulong databasePages,
        int pageSize,
        long segmentSize,
        IEnumerable<ulong> materializedPageIds)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        ArgumentNullException.ThrowIfNull(materializedPageIds);
        ValidateLayout(databasePages, pageSize, segmentSize);

        var revisionBytes = StrictUtf8.GetBytes(revision);
        if (revisionBytes.Length > MaximumRevisionLength)
            throw new ArgumentException("The server revision is too long.", nameof(revision));

        var materialized = PageRangeSet.FromPageIds(materializedPageIds, databasePages);
        if (!materialized.Contains(0))
        {
            throw new InvalidDataException(
                "A partial managed replica must materialize SQLite page 1 during bootstrap.");
        }

        using (var database = fileSystem.OpenFile(databasePath, FileOpenMode.OpenExisting, readOnly: true))
        {
            var expectedLength = checked((long)databasePages * pageSize);
            if (database.Length != expectedLength)
            {
                throw new InvalidDataException(
                    "The partial replica database length does not match its declared page count.");
            }
        }

        var statePath = databasePath + ManagedReplicaPageMaterializingFileSystem.StateSuffix;
        IFile? state = null;
        var created = false;
        try
        {
            state = fileSystem.OpenFile(statePath, FileOpenMode.CreateNew);
            created = true;
            var header = BuildHeader(
                revisionBytes,
                databasePages,
                pageSize,
                checked((ulong)segmentSize));
            var commit = BuildCommit(
                databasePages,
                NoTruncation,
                materialized.Snapshot());
            state.Write(0, header);
            state.Write(header.Length, commit);
            state.FlushToDisk();
        }
        catch
        {
            state?.Dispose();
            state = null;
            if (created)
                fileSystem.DeleteFile(statePath);
            throw;
        }
        finally
        {
            state?.Dispose();
        }
    }

    internal void EnsureMaterialized(long position, int length)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length == 0)
            return;

        ulong currentPages;
        lock (_stateGate)
            currentPages = _currentPageCount;

        var fileLength = checked(currentPages * (ulong)_pageSize);
        var unsignedPosition = checked((ulong)position);
        if (unsignedPosition >= fileLength)
            return;

        var availableLength = Math.Min((ulong)length, fileLength - unsignedPosition);
        var endExclusive = checked(unsignedPosition + availableLength);
        var firstPage = unsignedPosition / (ulong)_pageSize;
        var lastPageExclusive = checked((endExclusive - 1) / (ulong)_pageSize + 1);
        EnsurePagesAsync(firstPage, lastPageExclusive, _shutdown.Token)
            .GetAwaiter()
            .GetResult();
    }

    internal bool IsSegmentMaterialized(ulong pageId)
    {
        lock (_stateGate)
        {
            if (pageId >= _currentPageCount)
                return false;

            var start = pageId / _segmentPages * _segmentPages;
            var end = Math.Min(checked(start + _segmentPages), _currentPageCount);
            for (var page = start; page < end; page++)
            {
                if (!IsMaterializedNoLock(page))
                    return false;
            }

            return true;
        }
    }

    internal void Write(IFile file, long position, ReadOnlySpan<byte> source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(file);
        ArgumentOutOfRangeException.ThrowIfNegative(position);

        _publicationGate.Wait();
        try
        {
            ThrowIfDisposed();
            if (source.IsEmpty)
            {
                file.Write(position, source);
                return;
            }

            var writeStart = checked((ulong)position);
            var writeEnd = checked(writeStart + (ulong)source.Length);
            var resultingLength = Math.Max(checked((ulong)file.Length), writeEnd);
            if (resultingLength % (ulong)_pageSize != 0)
            {
                throw new InvalidDataException(
                    "A managed replica database write would leave a partial trailing page.");
            }

            lock (_stateGate)
                ValidateWriteCoverageNoLock(writeStart, writeEnd);

            file.Write(position, source);
            var length = file.Length;
            if (length % _pageSize != 0)
            {
                throw new InvalidDataException(
                    "A managed replica database write left a partial trailing page.");
            }

            var currentPages = checked((ulong)(length / _pageSize));
            lock (_stateGate)
            {
                _currentPageCount = currentPages;
                var firstCompletePage = checked((writeStart + (ulong)_pageSize - 1) / (ulong)_pageSize);
                var lastCompletePageExclusive = writeEnd / (ulong)_pageSize;
                for (var page = firstCompletePage;
                     page < lastCompletePageExclusive && page < currentPages;
                     page++)
                {
                    if (!IsMaterializedNoLock(page))
                        _pendingMaterialized.Add(new PageRange(page, 1));
                }
            }
        }
        finally
        {
            _publicationGate.Release();
        }
    }

    private void ValidateWriteCoverageNoLock(ulong writeStart, ulong writeEnd)
    {
        var firstPage = writeStart / (ulong)_pageSize;
        var lastPage = (writeEnd - 1) / (ulong)_pageSize;
        for (var page = firstPage; page <= lastPage; page++)
        {
            if (IsMaterializedNoLock(page))
                continue;

            var pageStart = checked(page * (ulong)_pageSize);
            var pageEnd = checked(pageStart + (ulong)_pageSize);
            if (writeStart > pageStart || writeEnd < pageEnd)
            {
                throw new InvalidDataException(
                    $"A partial write cannot modify missing managed replica page {page + 1}.");
            }
        }
    }

    internal void SetLength(IFile file, long length)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(file);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length % _pageSize != 0)
        {
            throw new InvalidDataException(
                "A managed replica database length must be a whole number of pages.");
        }

        _publicationGate.Wait();
        try
        {
            ThrowIfDisposed();
            file.SetLength(length);
            var pageCount = checked((ulong)(length / _pageSize));
            lock (_stateGate)
            {
                if (pageCount < _currentPageCount)
                {
                    _materialized.Trim(pageCount);
                    _pendingMaterialized.Trim(pageCount);
                    _pendingTruncateTo = _pendingTruncateTo is { } prior
                        ? Math.Min(prior, pageCount)
                        : pageCount;
                }

                _currentPageCount = pageCount;
            }
        }
        finally
        {
            _publicationGate.Release();
        }
    }

    internal void FlushToDisk(IFile file)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(file);

        _publicationGate.Wait();
        try
        {
            ThrowIfDisposed();
            file.FlushToDisk();

            PageRange[] pending;
            ulong currentPages;
            ulong? truncateTo;
            lock (_stateGate)
            {
                pending = _pendingMaterialized.Snapshot();
                currentPages = _currentPageCount;
                truncateTo = _pendingTruncateTo;
            }

            if (pending.Length == 0 && truncateTo is null)
                return;

            AppendCommit(
                currentPages,
                truncateTo ?? NoTruncation,
                pending);
            lock (_stateGate)
            {
                if (truncateTo is { } truncation)
                    _materialized.Trim(truncation);
                _materialized.Trim(currentPages);
                _materialized.Add(pending);
                _pendingMaterialized.Clear();
                _pendingTruncateTo = null;
            }
        }
        finally
        {
            _publicationGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _shutdown.Cancel();
        _publicationGate.Wait();
        try
        {
            _databaseFile.Dispose();
            _stateFile.Dispose();
        }
        finally
        {
            _publicationGate.Release();
            _publicationGate.Dispose();
            _shutdown.Dispose();
        }
    }

    private async Task EnsurePagesAsync(
        ulong requiredStart,
        ulong requiredEndExclusive,
        CancellationToken cancellationToken)
    {
        LoadGroup? ownedGroup = null;
        HashSet<Task>? existingLoads = null;
        lock (_stateGate)
        {
            if (requiredStart >= requiredEndExclusive || requiredEndExclusive > _currentPageCount)
            {
                throw new InvalidDataException(
                    "A managed replica attempted to read outside its local database page range.");
            }
            if (IsRangeMaterializedNoLock(requiredStart, requiredEndExclusive))
                return;

            var candidateStart = requiredStart;
            var candidateEnd = requiredEndExclusive;
            if (_prefetchSegments)
            {
                candidateStart = requiredStart / _segmentPages * _segmentPages;
                candidateEnd = Math.Min(
                    checked(((requiredEndExclusive - 1) / _segmentPages + 1) * _segmentPages),
                    Math.Min(_currentPageCount, _remotePageCount));
            }

            List<ulong>? pagesToLoad = null;
            for (var page = candidateStart; page < candidateEnd; page++)
            {
                if (IsMaterializedNoLock(page))
                    continue;
                if (page >= _remotePageCount)
                {
                    throw new InvalidDataException(
                        $"Managed replica page {page + 1} is absent locally and outside the pinned remote database image.");
                }

                if (_loads.TryGetValue(page, out var existing))
                {
                    existingLoads ??= [];
                    existingLoads.Add(existing.Completion.Task);
                }
                else
                {
                    pagesToLoad ??= [];
                    pagesToLoad.Add(page);
                }
            }

            if (pagesToLoad is { Count: > 0 })
            {
                ownedGroup = new LoadGroup(pagesToLoad.ToArray());
                foreach (var page in ownedGroup.PageIds)
                    _loads.Add(page, ownedGroup);
            }
        }

        if (ownedGroup is not null)
        {
            _ = RunLoadGroupAsync(ownedGroup, cancellationToken);
            existingLoads ??= [];
            existingLoads.Add(ownedGroup.Completion.Task);
        }

        if (existingLoads is { Count: > 0 })
            await Task.WhenAll(existingLoads).ConfigureAwait(false);

        lock (_stateGate)
        {
            for (var page = requiredStart; page < requiredEndExclusive; page++)
            {
                if (!IsMaterializedNoLock(page))
                {
                    throw new InvalidDataException(
                        $"Managed replica page {page + 1} was not materialized by its remote fetch.");
                }
            }
        }
    }

    private bool IsRangeMaterializedNoLock(ulong start, ulong endExclusive)
    {
        for (var page = start; page < endExclusive; page++)
        {
            if (!IsMaterializedNoLock(page))
                return false;
        }

        return true;
    }

    private async Task RunLoadGroupAsync(LoadGroup group, CancellationToken cancellationToken)
    {
        try
        {
            await FetchAndPublishAsync(group.PageIds, cancellationToken).ConfigureAwait(false);
            group.Completion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            group.Completion.TrySetException(exception);
        }
        finally
        {
            lock (_stateGate)
            {
                foreach (var page in group.PageIds)
                {
                    if (_loads.TryGetValue(page, out var current) && ReferenceEquals(current, group))
                        _loads.Remove(page);
                }
            }
        }
    }

    private async Task FetchAndPublishAsync(
        ulong[] requestedPageIds,
        CancellationToken cancellationToken)
    {
        var batch = await _pageSource
            .FetchPagesAsync(_revision, requestedPageIds, cancellationToken)
            .ConfigureAwait(false);
        var pages = ValidateBatch(batch, requestedPageIds);

        await _publicationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var publishedIds = new List<ulong>(pages.Count);
            foreach (var pageId in requestedPageIds)
            {
                lock (_stateGate)
                {
                    if (IsMaterializedNoLock(pageId))
                        continue;
                    if (pageId >= _currentPageCount)
                    {
                        throw new InvalidDataException(
                            "The local replica was truncated while a missing page was being fetched.");
                    }
                }

                _databaseFile.Write(
                    checked((long)pageId * _pageSize),
                    pages[pageId]);
                publishedIds.Add(pageId);
            }

            if (publishedIds.Count == 0)
                return;

            _databaseFile.FlushToDisk();
            var ranges = PageRangeSet.FromPageIds(publishedIds, _currentPageCount).Snapshot();
            AppendCommit(_currentPageCount, NoTruncation, ranges);
            lock (_stateGate)
                _materialized.Add(ranges);
        }
        finally
        {
            _publicationGate.Release();
        }
    }

    private Dictionary<ulong, byte[]> ValidateBatch(
        ManagedReplicaPageBatch batch,
        IReadOnlyList<ulong> requestedPageIds)
    {
        if (!string.Equals(batch.Revision, _revision, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The targeted page response belongs to a different server revision.");
        }
        if (batch.DatabasePages != _remotePageCount)
        {
            throw new InvalidDataException(
                "The targeted page response reports a different database size.");
        }
        if (batch.Pages is null)
            throw new InvalidDataException("The targeted page response did not contain a page set.");

        var requested = new HashSet<ulong>(requestedPageIds);
        var pages = new Dictionary<ulong, byte[]>(requested.Count);
        foreach (var page in batch.Pages)
        {
            if (!requested.Contains(page.PageId))
            {
                throw new InvalidDataException(
                    $"The targeted page response returned unexpected page {page.PageId + 1}.");
            }
            if (!pages.TryAdd(page.PageId, page.Data))
                throw new InvalidDataException("The targeted page response contains a duplicate page.");
            if (page.Data is null || page.Data.Length != _pageSize)
            {
                throw new InvalidDataException(
                    $"The targeted page response returned an invalid image for page {page.PageId + 1}.");
            }
        }

        if (pages.Count != requested.Count)
            throw new InvalidDataException("The targeted page response omitted a requested page.");

        return pages;
    }

    private bool IsMaterializedNoLock(ulong pageId)
        => _materialized.Contains(pageId) || _pendingMaterialized.Contains(pageId);

    private void AppendCommit(
        ulong currentPageCount,
        ulong truncateTo,
        IReadOnlyList<PageRange> ranges)
    {
        var record = BuildCommit(currentPageCount, truncateTo, ranges);
        var originalLength = _stateFile.Length;
        try
        {
            _stateFile.Write(originalLength, record);
            _stateFile.FlushToDisk();
        }
        catch (Exception writeException)
        {
            try
            {
                _stateFile.SetLength(originalLength);
                _stateFile.FlushToDisk();
            }
            catch (Exception rollbackException)
            {
                throw new InvalidDataException(
                    "Managed replica page-state publication failed and its partial record could not be removed.",
                    new AggregateException(writeException, rollbackException));
            }

            throw;
        }
    }

    private static LoadedState LoadState(IFile stateFile)
    {
        if (stateFile.Length < HeaderPrefixLength + HashLength)
            throw new InvalidDataException("Managed replica page state is truncated.");

        var prefix = ReadExactly(stateFile, 0, HeaderPrefixLength);
        if (!prefix.AsSpan(0, HeaderMagic.Length).SequenceEqual(HeaderMagic))
            throw new InvalidDataException("Managed replica page state has an invalid header.");
        if (BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(8)) != StateVersion)
            throw new InvalidDataException("Managed replica page state has an unsupported version.");

        var pageSize = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(12));
        var remotePageCount = BinaryPrimitives.ReadUInt64LittleEndian(prefix.AsSpan(16));
        var segmentSize = BinaryPrimitives.ReadUInt64LittleEndian(prefix.AsSpan(24));
        var revisionLength = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(32));
        if (revisionLength <= 0 || revisionLength > MaximumRevisionLength)
            throw new InvalidDataException("Managed replica page state has an invalid revision length.");
        ValidateLayout(
            remotePageCount,
            pageSize,
            checked((long)segmentSize));

        var headerLength = checked(HeaderPrefixLength + revisionLength + HashLength);
        if (stateFile.Length < headerLength)
            throw new InvalidDataException("Managed replica page state header is truncated.");
        var header = ReadExactly(stateFile, 0, headerLength);
        ValidateHash(header, headerLength - HashLength, "Managed replica page state header");
        var revision = StrictUtf8.GetString(
            header,
            HeaderPrefixLength,
            revisionLength);

        var materialized = new PageRangeSet();
        var currentPageCount = remotePageCount;
        var offset = (long)headerLength;
        var validLength = offset;
        while (offset < stateFile.Length)
        {
            if (stateFile.Length - offset < 8)
                break;

            var recordPrefix = ReadExactly(stateFile, offset, 8);
            if (!recordPrefix.AsSpan(0, CommitMagic.Length).SequenceEqual(CommitMagic))
                throw new InvalidDataException("Managed replica page state contains an invalid record.");
            var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(recordPrefix.AsSpan(4));
            if (payloadLength < CommitPayloadPrefixLength
                || payloadLength > MaximumCommitPayloadLength)
            {
                throw new InvalidDataException(
                    "Managed replica page state contains an invalid record length.");
            }

            var recordLength = checked(8 + payloadLength + HashLength);
            if (stateFile.Length - offset < recordLength)
                break;

            var record = ReadExactly(stateFile, offset, recordLength);
            ValidateHash(record, recordLength - HashLength, "Managed replica page state record");
            var payload = record.AsSpan(8, payloadLength);
            if (BinaryPrimitives.ReadInt32LittleEndian(payload) != CommitVersion)
                throw new InvalidDataException("Managed replica page state contains an unsupported record.");

            var nextCurrentPageCount = BinaryPrimitives.ReadUInt64LittleEndian(payload[4..]);
            var truncateTo = BinaryPrimitives.ReadUInt64LittleEndian(payload[12..]);
            var rangeCount = BinaryPrimitives.ReadInt32LittleEndian(payload[20..]);
            if (rangeCount < 0
                || payloadLength != checked(CommitPayloadPrefixLength + rangeCount * 16))
            {
                throw new InvalidDataException("Managed replica page state contains invalid page ranges.");
            }
            if (nextCurrentPageCount > uint.MaxValue)
                throw new InvalidDataException("Managed replica page state exceeds the managed pager page limit.");

            if (truncateTo != NoTruncation)
                materialized.Trim(truncateTo);
            currentPageCount = nextCurrentPageCount;
            materialized.Trim(currentPageCount);

            var ranges = new PageRange[rangeCount];
            var rangeOffset = CommitPayloadPrefixLength;
            for (var index = 0; index < ranges.Length; index++)
            {
                var start = BinaryPrimitives.ReadUInt64LittleEndian(payload[rangeOffset..]);
                var count = BinaryPrimitives.ReadUInt64LittleEndian(payload[(rangeOffset + 8)..]);
                var range = new PageRange(start, count);
                if (count == 0 || range.EndExclusive > currentPageCount)
                {
                    throw new InvalidDataException(
                        "Managed replica page state contains a page range outside the local database.");
                }
                ranges[index] = range;
                rangeOffset += 16;
            }

            materialized.Add(ranges);
            offset += recordLength;
            validLength = offset;
        }

        if (validLength != stateFile.Length)
        {
            stateFile.SetLength(validLength);
            stateFile.FlushToDisk();
        }

        if (currentPageCount == 0)
            throw new InvalidDataException("Managed replica page state describes an empty database.");

        return new LoadedState(
            revision,
            pageSize,
            remotePageCount,
            currentPageCount,
            segmentSize,
            materialized);
    }

    private static byte[] BuildHeader(
        byte[] revision,
        ulong databasePages,
        int pageSize,
        ulong segmentSize)
    {
        var hashOffset = checked(HeaderPrefixLength + revision.Length);
        var header = new byte[checked(hashOffset + HashLength)];
        HeaderMagic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), StateVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), pageSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), databasePages);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24), segmentSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(32), revision.Length);
        revision.CopyTo(header, HeaderPrefixLength);
        SHA256.HashData(header.AsSpan(0, hashOffset), header.AsSpan(hashOffset, HashLength));
        return header;
    }

    private static byte[] BuildCommit(
        ulong currentPageCount,
        ulong truncateTo,
        IReadOnlyList<PageRange> ranges)
    {
        var payloadLength = checked(CommitPayloadPrefixLength + ranges.Count * 16);
        if (payloadLength > MaximumCommitPayloadLength)
            throw new InvalidDataException("Managed replica page-state update is too large.");

        var hashOffset = checked(8 + payloadLength);
        var record = new byte[checked(hashOffset + HashLength)];
        CommitMagic.CopyTo(record, 0);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), payloadLength);
        var payload = record.AsSpan(8, payloadLength);
        BinaryPrimitives.WriteInt32LittleEndian(payload, CommitVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(payload[4..], currentPageCount);
        BinaryPrimitives.WriteUInt64LittleEndian(payload[12..], truncateTo);
        BinaryPrimitives.WriteInt32LittleEndian(payload[20..], ranges.Count);
        var offset = CommitPayloadPrefixLength;
        foreach (var range in ranges)
        {
            if (range.Count == 0 || range.EndExclusive > currentPageCount)
                throw new InvalidDataException("Managed replica page-state update contains an invalid range.");
            BinaryPrimitives.WriteUInt64LittleEndian(payload[offset..], range.Start);
            BinaryPrimitives.WriteUInt64LittleEndian(payload[(offset + 8)..], range.Count);
            offset += 16;
        }

        SHA256.HashData(record.AsSpan(0, hashOffset), record.AsSpan(hashOffset, HashLength));
        return record;
    }

    private static byte[] ReadExactly(IFile file, long position, int length)
    {
        var bytes = new byte[length];
        if (file.Read(position, bytes) != length)
            throw new InvalidDataException("Managed replica page state was truncated while being read.");
        return bytes;
    }

    private static void ValidateHash(byte[] bytes, int hashOffset, string name)
    {
        Span<byte> expected = stackalloc byte[HashLength];
        SHA256.HashData(bytes.AsSpan(0, hashOffset), expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, bytes.AsSpan(hashOffset, HashLength)))
            throw new InvalidDataException($"{name} failed its integrity check.");
    }

    private static void ValidateLayout(ulong databasePages, int pageSize, long segmentSize)
    {
        if (databasePages == 0 || databasePages > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(databasePages), databasePages, "Database page count is invalid.");
        if (pageSize < 512 || pageSize > 65536 || (pageSize & (pageSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "SQLite page size is invalid.");
        if (segmentSize < pageSize || segmentSize % pageSize != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(segmentSize),
                segmentSize,
                "Segment size must be a positive whole number of database pages.");
        }
        _ = checked((long)databasePages * pageSize);
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed class LoadGroup(ulong[] pageIds)
    {
        internal ulong[] PageIds { get; } = pageIds;

        internal TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly record struct LoadedState(
        string Revision,
        int PageSize,
        ulong RemotePageCount,
        ulong CurrentPageCount,
        ulong SegmentSize,
        PageRangeSet Materialized);

    private readonly record struct PageRange(ulong Start, ulong Count)
    {
        internal ulong EndExclusive => checked(Start + Count);
    }

    private sealed class PageRangeSet
    {
        private readonly List<PageRange> _ranges = [];

        internal bool Contains(ulong pageId)
        {
            var low = 0;
            var high = _ranges.Count - 1;
            while (low <= high)
            {
                var middle = low + (high - low) / 2;
                var range = _ranges[middle];
                if (pageId < range.Start)
                {
                    high = middle - 1;
                }
                else if (pageId >= range.EndExclusive)
                {
                    low = middle + 1;
                }
                else
                {
                    return true;
                }
            }

            return false;
        }

        internal void Add(IEnumerable<PageRange> ranges)
        {
            foreach (var range in ranges)
                Add(range);
        }

        internal void Add(PageRange range)
        {
            if (range.Count == 0)
                return;

            var start = range.Start;
            var end = range.EndExclusive;
            var index = 0;
            while (index < _ranges.Count && _ranges[index].EndExclusive < start)
                index++;

            while (index < _ranges.Count && _ranges[index].Start <= end)
            {
                start = Math.Min(start, _ranges[index].Start);
                end = Math.Max(end, _ranges[index].EndExclusive);
                _ranges.RemoveAt(index);
            }

            _ranges.Insert(index, new PageRange(start, checked(end - start)));
        }

        internal void Trim(ulong pageCount)
        {
            for (var index = _ranges.Count - 1; index >= 0; index--)
            {
                var range = _ranges[index];
                if (range.Start >= pageCount)
                {
                    _ranges.RemoveAt(index);
                    continue;
                }
                if (range.EndExclusive > pageCount)
                    _ranges[index] = new PageRange(range.Start, pageCount - range.Start);
            }
        }

        internal PageRange[] Snapshot() => _ranges.ToArray();

        internal void Clear() => _ranges.Clear();

        internal static PageRangeSet FromPageIds(
            IEnumerable<ulong> pageIds,
            ulong pageCount)
        {
            var sorted = pageIds.Distinct().Order().ToArray();
            var ranges = new PageRangeSet();
            if (sorted.Length == 0)
                return ranges;

            var start = sorted[0];
            var end = checked(start + 1);
            if (start >= pageCount)
                throw new InvalidDataException("A materialized page lies outside the declared database.");
            for (var index = 1; index < sorted.Length; index++)
            {
                var page = sorted[index];
                if (page >= pageCount)
                    throw new InvalidDataException("A materialized page lies outside the declared database.");
                if (page == end)
                {
                    end++;
                    continue;
                }

                ranges.Add(new PageRange(start, end - start));
                start = page;
                end = checked(page + 1);
            }

            ranges.Add(new PageRange(start, end - start));
            return ranges;
        }
    }
}
