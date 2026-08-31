namespace Ahtola.Core.Storage;

/// <summary>
/// A read cursor over a SQLite index b-tree that yields one contiguous equality prefix.
/// </summary>
/// <remarks>
/// Index interior separator cells are real index entries. The cursor therefore walks the
/// lower-bound child, emits matching separators, and continues through adjacent children
/// only while their separator prefixes can still equal the requested key.
/// </remarks>
public sealed class SqliteIndexBtreeCursor
{
    private const int MaximumDepth = 64;

    private readonly ISqliteBtreePageIo _io;
    private readonly SqliteIndexRecordComparer _comparer;
    private readonly SqliteOverflowChainReader _overflowReader;
    private readonly SqliteTextEncoding _textEncoding;

    /// <summary>Creates a cursor over one index's page and comparison semantics.</summary>
    public SqliteIndexBtreeCursor(
        ISqliteBtreePageIo pageIo,
        SqliteIndexRecordComparer comparer,
        SqliteTextEncoding textEncoding)
    {
        ArgumentNullException.ThrowIfNull(pageIo);
        ArgumentNullException.ThrowIfNull(comparer);
        _io = pageIo;
        _comparer = comparer;
        _textEncoding = textEncoding is SqliteTextEncoding.Unset
            ? SqliteTextEncoding.Utf8
            : textEncoding;
        _overflowReader = new SqliteOverflowChainReader(pageIo);
    }

    /// <summary>
    /// Enumerates complete index records whose first fields equal <paramref name="prefix"/>.
    /// </summary>
    public IEnumerable<byte[]> SeekPrefix(
        uint rootPage,
        IReadOnlyList<SqlValue> prefix,
        Action? pageRead = null,
        Action? keyCompared = null)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (prefix.Count == 0)
            throw new ArgumentException("An index seek prefix must contain at least one field.", nameof(prefix));
        if (rootPage == 0 || rootPage > _io.PageCount)
            throw new ArgumentOutOfRangeException(nameof(rootPage));

        var seek = prefix as SqlValue[] ?? prefix.ToArray();
        return EnumerateNode(rootPage, seek, depth: 0, pageRead, keyCompared);
    }

    /// <summary>
    /// Enumerates every complete index record in ascending SQLite key order, with no equality
    /// prefix filtering. Unlike <see cref="SeekPrefix"/>, this always descends into every child in
    /// left-to-right order rather than binary-searching for a matching separator, so it visits
    /// every leaf and interior separator exactly once — the natural in-order traversal a caller
    /// merging a full ORDER BY-elision scan against an overlay needs, without materializing and
    /// sorting the whole table in memory first.
    /// </summary>
    public IEnumerable<byte[]> ScanAscending(uint rootPage, Action? pageRead = null)
    {
        if (rootPage == 0 || rootPage > _io.PageCount)
            throw new ArgumentOutOfRangeException(nameof(rootPage));

        return EnumerateNodeAscending(rootPage, depth: 0, pageRead);
    }

    private IEnumerable<byte[]> EnumerateNodeAscending(uint pageNumber, int depth, Action? pageRead)
    {
        if (depth >= MaximumDepth)
        {
            throw new InvalidDataException(
                $"SQLite index b-tree rooted above page {pageNumber} is deeper than {MaximumDepth} levels.");
        }

        var image = _io.ReadPage(pageNumber);
        pageRead?.Invoke();
        switch (SqliteBtreePageHeader.Parse(image).PageType)
        {
            case SqliteBtreePageType.IndexLeaf:
                {
                    var leaf = SqliteIndexLeafPageView.Parse(
                        image,
                        _io.UsableSpace,
                        _textEncoding,
                        overflowReader: _overflowReader,
                        recordComparer: _comparer);
                    for (var index = 0; index < leaf.Cells.Count; index++)
                        yield return leaf.GetRecord(index);
                    yield break;
                }

            case SqliteBtreePageType.IndexInterior:
                {
                    var interior = SqliteIndexInteriorPageView.Parse(
                        image,
                        _io.UsableSpace,
                        _textEncoding,
                        overflowReader: _overflowReader,
                        recordComparer: _comparer);
                    for (var childIndex = 0; childIndex <= interior.Cells.Count; childIndex++)
                    {
                        var childPage = childIndex == interior.Cells.Count
                            ? interior.Header.RightMostChildPage
                            : interior.Cells[childIndex].Cell.LeftChildPage;
                        foreach (var record in EnumerateNodeAscending(childPage, depth + 1, pageRead))
                            yield return record;

                        if (childIndex < interior.Cells.Count)
                            yield return interior.GetRecord(childIndex);
                    }

                    yield break;
                }

            default:
                throw new InvalidDataException(
                    $"SQLite page {pageNumber} is not part of an index b-tree.");
        }
    }

    private IEnumerable<byte[]> EnumerateNode(
        uint pageNumber,
        SqlValue[] prefix,
        int depth,
        Action? pageRead,
        Action? keyCompared)
    {
        if (depth >= MaximumDepth)
        {
            throw new InvalidDataException(
                $"SQLite index b-tree rooted above page {pageNumber} is deeper than {MaximumDepth} levels.");
        }

        var image = _io.ReadPage(pageNumber);
        pageRead?.Invoke();
        switch (SqliteBtreePageHeader.Parse(image).PageType)
        {
            case SqliteBtreePageType.IndexLeaf:
                {
                    var leaf = SqliteIndexLeafPageView.Parse(
                        image,
                        _io.UsableSpace,
                        _textEncoding,
                        overflowReader: _overflowReader,
                        recordComparer: _comparer);
                    var low = 0;
                    var high = leaf.Cells.Count;
                    while (low < high)
                    {
                        var middle = low + ((high - low) / 2);
                        if (ComparePrefix(leaf.GetRecord(middle), prefix, keyCompared) < 0)
                            low = middle + 1;
                        else
                            high = middle;
                    }

                    for (var index = low; index < leaf.Cells.Count; index++)
                    {
                        var record = leaf.GetRecord(index);
                        var comparison = ComparePrefix(record, prefix, keyCompared);
                        if (comparison != 0)
                            yield break;
                        yield return record;
                    }

                    yield break;
                }

            case SqliteBtreePageType.IndexInterior:
                {
                    var interior = SqliteIndexInteriorPageView.Parse(
                        image,
                        _io.UsableSpace,
                        _textEncoding,
                        overflowReader: _overflowReader,
                        recordComparer: _comparer);
                    var low = 0;
                    var high = interior.Cells.Count;
                    while (low < high)
                    {
                        var middle = low + ((high - low) / 2);
                        if (ComparePrefix(interior.GetRecord(middle), prefix, keyCompared) < 0)
                            low = middle + 1;
                        else
                            high = middle;
                    }

                    for (var childIndex = low; childIndex <= interior.Cells.Count; childIndex++)
                    {
                        var childPage = childIndex == interior.Cells.Count
                            ? interior.Header.RightMostChildPage
                            : interior.Cells[childIndex].Cell.LeftChildPage;
                        foreach (var record in EnumerateNode(
                                     childPage,
                                     prefix,
                                     depth + 1,
                                     pageRead,
                                     keyCompared))
                        {
                            yield return record;
                        }

                        if (childIndex == interior.Cells.Count)
                            yield break;

                        var separator = interior.GetRecord(childIndex);
                        var comparison = ComparePrefix(separator, prefix, keyCompared);
                        if (comparison > 0)
                            yield break;
                        if (comparison == 0)
                            yield return separator;
                    }

                    yield break;
                }

            default:
                throw new InvalidDataException(
                    $"SQLite page {pageNumber} is not part of an index b-tree.");
        }
    }

    private int ComparePrefix(byte[] record, SqlValue[] prefix, Action? keyCompared)
    {
        keyCompared?.Invoke();
        var values = SqliteRecordCodec.Decode(record, _textEncoding);
        if (values.Length < prefix.Length)
            throw new InvalidDataException("SQLite index record is shorter than the requested seek prefix.");

        if (values.Length != prefix.Length)
            Array.Resize(ref values, prefix.Length);
        return _comparer.Compare(values, prefix);
    }
}
