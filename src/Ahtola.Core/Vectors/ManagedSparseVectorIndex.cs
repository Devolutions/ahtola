using Ahtola.Core.Indexing;

namespace Ahtola.Core.Vectors;

/// <summary>
/// Exact sparse Jaccard index. Positive finite rows use deterministic component postings;
/// negative rows are always reranked because an overlap certificate is not valid for them.
/// </summary>
internal sealed class ManagedSparseVectorIndex
{
    private readonly int _dimensions;
    private readonly int _candidateLimit;
    private readonly SortedDictionary<int, SortedSet<long>> _postings = [];
    private readonly Dictionary<long, int[]> _placements = [];
    private readonly SortedSet<long> _allRowIds = [];
    private readonly SortedSet<long> _alwaysRerank = [];
    private readonly HashSet<long> _unindexable = [];

    public ManagedSparseVectorIndex(
        int dimensions,
        int candidateLimit = ManagedVectorIndexLimits.MaxCandidateRows)
    {
        if (candidateLimit is < 1 or > ManagedVectorIndexLimits.MaxCandidateRows)
            throw new ArgumentOutOfRangeException(nameof(candidateLimit));

        _dimensions = dimensions;
        _candidateLimit = candidateLimit;
    }

    public long IndexedRows => _allRowIds.Count;
    public long UnindexableRows => _unindexable.Count;
    public int ComponentCount => _postings.Count;
    public long RerankedRows { get; private set; }
    public long SearchCount { get; private set; }

    public void Clear()
    {
        _postings.Clear();
        _placements.Clear();
        _allRowIds.Clear();
        _alwaysRerank.Clear();
        _unindexable.Clear();
        RerankedRows = 0;
        SearchCount = 0;
    }

    public bool Upsert(long rowId, SqlValue value)
    {
        Remove(rowId);
        if (!SqliteVectorFunctions.TryDecodeSparseVector(value, _dimensions, out var vector)
            || !vector.IsFinite)
        {
            _unindexable.Add(rowId);
            return false;
        }

        _placements[rowId] = vector.Indices;
        _allRowIds.Add(rowId);
        if (!vector.IsNonNegative)
        {
            _alwaysRerank.Add(rowId);
            return true;
        }

        foreach (var component in vector.Indices)
        {
            if (!_postings.TryGetValue(component, out var rows))
            {
                rows = [];
                _postings.Add(component, rows);
            }

            rows.Add(rowId);
        }

        return true;
    }

    public void Remove(long rowId)
    {
        _unindexable.Remove(rowId);
        if (!_placements.Remove(rowId, out var components))
            return;

        _allRowIds.Remove(rowId);
        if (_alwaysRerank.Remove(rowId))
            return;

        foreach (var component in components)
        {
            if (!_postings.TryGetValue(component, out var rows))
                continue;
            rows.Remove(rowId);
            if (rows.Count == 0)
                _postings.Remove(component);
        }
    }

    public ManagedVectorSearchResult Search(
        SqlValue queryValue,
        in DecodedSparseVector query,
        int limit,
        IManagedIndexSource source,
        int columnIndex)
    {
        SearchCount++;
        if (limit <= 0 || _allRowIds.Count == 0)
            return new ManagedVectorSearchResult([], query.Indices.Length, 0, true);
        if (!query.IsFinite || !query.IsNonNegative || query.Values.Length == 0 || UnindexableRows != 0)
            return ExhaustiveFallback(source);

        var candidateRowIds = new SortedSet<long>();
        foreach (var rowId in _alwaysRerank)
        {
            if (candidateRowIds.Count >= _candidateLimit)
                return Abandon(candidateRowIds, candidates: null, source);
            candidateRowIds.Add(rowId);
        }

        foreach (var component in query.Indices)
        {
            if (!_postings.TryGetValue(component, out var rows))
                continue;

            foreach (var rowId in rows)
            {
                if (candidateRowIds.Contains(rowId))
                    continue;
                if (candidateRowIds.Count >= _candidateLimit)
                    return Abandon(candidateRowIds, candidates: null, source);
                candidateRowIds.Add(rowId);
            }
        }

        var candidates = new List<ManagedVectorCandidate>(candidateRowIds.Count);
        var top = new ManagedVectorTopK(limit);
        foreach (var rowId in candidateRowIds)
        {
            if (!TryScore(rowId, queryValue, source, columnIndex, out var candidate))
                return Abandon(candidateRowIds, candidates, source);
            candidates.Add(candidate);
            top.Offer(candidate);
        }

        // Every omitted finite nonnegative row has an empty component intersection and therefore
        // distance exactly 1. A strict sub-1 cutoff is a complete exact certificate.
        if (!top.IsFull || top.Worst >= 1.0)
        {
            foreach (var rowId in _allRowIds)
            {
                if (candidateRowIds.Contains(rowId))
                    continue;
                if (candidates.Count >= _candidateLimit)
                    return Abandon(candidateRowIds, candidates, source);
                if (!TryScore(rowId, queryValue, source, columnIndex, out var candidate))
                    return Abandon(candidateRowIds, candidates, source);
                candidates.Add(candidate);
            }
        }

        RerankedRows += candidates.Count;
        return new ManagedVectorSearchResult(
            SelectSuperset(candidates, limit),
            query.Indices.Length,
            candidates.Count,
            candidates.Count == _allRowIds.Count);
    }

    private bool TryScore(
        long rowId,
        SqlValue query,
        IManagedIndexSource source,
        int columnIndex,
        out ManagedVectorCandidate candidate)
    {
        candidate = default;
        if (!source.TryGetPosition(rowId, out var position))
            return false;
        var values = source.GetRow(position);
        var value = columnIndex >= 0 && columnIndex < values.Length ? values[columnIndex] : SqlValue.Null;
        var distance = SqliteVectorFunctions.DistanceExact(value, query, VectorDistanceKind.Jaccard);
        if (!double.IsFinite(distance))
            return false;

        candidate = new ManagedVectorCandidate(position, rowId, distance);
        return true;
    }

    private ManagedVectorSearchResult ExhaustiveFallback(IManagedIndexSource source)
    {
        RerankedRows += source.RowCount;
        var rows = new List<ManagedVectorCandidate>(source.RowCount);
        for (var position = 0; position < source.RowCount; position++)
            rows.Add(new ManagedVectorCandidate(position, source.GetRowId(position), double.NaN));
        rows.Sort(static (left, right) => left.RowId.CompareTo(right.RowId));
        return new ManagedVectorSearchResult(rows, _postings.Count, source.RowCount, true);
    }

    private ManagedVectorSearchResult Abandon(
        SortedSet<long> candidateRowIds,
        List<ManagedVectorCandidate>? candidates,
        IManagedIndexSource source)
    {
        candidateRowIds.Clear();
        if (candidates is not null)
        {
            candidates.Clear();
            candidates.TrimExcess();
        }

        return ExhaustiveFallback(source);
    }

    private static IReadOnlyList<ManagedVectorCandidate> SelectSuperset(
        List<ManagedVectorCandidate> candidates,
        int limit)
    {
        if (candidates.Count == 0)
            return [];

        candidates.Sort(static (left, right) =>
        {
            var comparison = left.Distance.CompareTo(right.Distance);
            return comparison != 0 ? comparison : left.RowId.CompareTo(right.RowId);
        });
        var take = Math.Min(limit, candidates.Count);
        var cutoff = candidates[take - 1].Distance;
        while (take < candidates.Count && candidates[take].Distance.Equals(cutoff))
            take++;
        candidates.RemoveRange(take, candidates.Count - take);
        candidates.Sort(static (left, right) => left.RowId.CompareTo(right.RowId));
        return candidates;
    }
}
