namespace Ahtola.Core.Search;

/// <summary>
/// Posting storage for <see cref="ManagedFtsSearchIndex"/>: immutable, compact segments shared by
/// reference across forks, plus one small mutable overlay for recent writes.
/// </summary>
/// <remarks>
/// <para>
/// Every engine write transaction and statement backup clones the table it touches, and the clone's
/// method attachment used to start empty, so the next query after any write rebuilt the whole index
/// from the base rows. The postings are therefore log structured, like Tantivy's segments: a fork
/// shares every immutable segment by reference and copies nothing until its first write, which
/// copies only what it touches — the overlay, or one segment's deleted-rowid set. A fork is O(number
/// of segments), and the working copy a transaction commits keeps the incrementally maintained
/// state instead of discarding it.
/// </para>
/// <para>
/// Invariant: a live rowid has exactly one live image, either in the overlay or in exactly one
/// segment that does not list it as deleted. Upsert and remove establish it by retiring the previous
/// image before publishing the next one.
/// </para>
/// <para>
/// A segment stores its postings column-wise: per term a contiguous run of (document ordinal,
/// frequency, column mask) entries, with positions and per-column frequencies packed into one shared
/// array each. That is roughly 20 bytes plus 8 per position for each posting, instead of a posting
/// struct, a positions array and a rowid dictionary entry per posting in the mutable overlay.
/// </para>
/// </remarks>
internal sealed partial class ManagedFtsSearchIndex
{
    /// <summary>Overlay postings (live and superseded) at which the overlay is frozen into a segment.</summary>
    internal const int OverlayFreezePostings = 16_384;

    /// <summary>Segments of one size tier merged together, and the size ratio between tiers.</summary>
    private const int MergeFactor = 4;

    /// <summary>Live postings below which every segment belongs to the smallest tier.</summary>
    private const long MinimumTierPostings = 16_384;

    private readonly object _sync = new();
    private readonly List<SegmentSlot> _segments = [];
    private Overlay _overlay = new();
    private bool _overlayShared;
    private int _documentCount;
    private bool _bulkLoad;
    private BulkBuilder? _bulk;

    /// <summary>Immutable segments currently holding postings; diagnostics and tests only.</summary>
    internal int SegmentCount => _segments.Count;

    /// <summary>Overlay size that triggers a freeze. Tests lower it to exercise segments cheaply.</summary>
    internal int OverlayFreezeThreshold { get; set; } = OverlayFreezePostings;

    /// <summary>Upper bound of the smallest merge tier. Tests lower it to exercise merges cheaply.</summary>
    internal long MergeTierBasePostings { get; set; } = MinimumTierPostings;

    /// <summary>
    /// A logically independent copy that shares every immutable segment with this index. Neither
    /// copy observes the other's later writes.
    /// </summary>
    public ManagedFtsSearchIndex Fork()
    {
        lock (_sync)
        {
            FlushBulkBuilder();
            var fork = new ManagedFtsSearchIndex(this)
            {
                OverlayFreezeThreshold = OverlayFreezeThreshold,
                MergeTierBasePostings = MergeTierBasePostings,
            };
            _overlayShared = true;
            fork._overlay = _overlay;
            fork._overlayShared = true;
            foreach (var slot in _segments)
                fork._segments.Add(slot.ShareForFork());

            fork._documentCount = _documentCount;
            Array.Copy(_columnTokenTotals, fork._columnTokenTotals, _columnTokenTotals.Length);
            return fork;
        }
    }

    /// <summary>
    /// Starts populating an index in one pass, so a rebuild produces one compact segment directly
    /// instead of a cascade of overlay freezes and merges. An empty index streams documents into a
    /// flat builder; a non-empty one only suspends freezing. The index must not be queried until
    /// <see cref="EndBulkLoad"/>.
    /// </summary>
    public void BeginBulkLoad()
    {
        lock (_sync)
        {
            _bulkLoad = true;
            if (_documentCount == 0 && _segments.Count == 0 && _overlay.TotalPostings == 0)
                _bulk = new BulkBuilder(this);
        }
    }

    /// <summary>Ends a bulk load by freezing everything it staged into a segment.</summary>
    public void EndBulkLoad()
    {
        lock (_sync)
        {
            _bulkLoad = false;
            FlushBulkBuilder();
            if (_overlay.TotalPostings > 0 || _overlay.Documents.Count > 0)
                FreezeOverlay();
            else
                MergeSegmentsIfNeeded();
        }
    }

    private void FlushBulkBuilder()
    {
        if (_bulk is not { } bulk)
            return;

        _bulk = null;
        var segment = bulk.Build();
        if (segment.DocumentCount > 0)
            _segments.Add(new SegmentSlot(segment));
    }

    private void AddDocumentStatistics(int[] columnLengths)
    {
        _documentCount++;
        for (var column = 0; column < _columnCount; column++)
            _columnTokenTotals[column] += columnLengths[column];
    }

    private void ClearStorage()
    {
        _bulk = null;
        _segments.Clear();
        _overlay = new Overlay();
        _overlayShared = false;
        _documentCount = 0;
        Array.Clear(_columnTokenTotals);
    }

    private long SegmentTombstonedPostings()
    {
        var total = 0L;
        foreach (var slot in _segments)
            total += slot.DeletedPostings;
        return total;
    }

    private long SegmentPostings()
    {
        var total = 0L;
        foreach (var slot in _segments)
            total += slot.Segment.PostingCount;
        return total;
    }

    private int CountDistinctTerms()
    {
        if (_segments.Count == 0)
            return _overlay.Postings.Count;
        if (_segments.Count == 1 && _overlay.Postings.Count == 0)
            return _segments[0].Segment.Terms.Length;

        var terms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in _segments)
            terms.UnionWith(slot.Segment.Terms);
        terms.UnionWith(_overlay.Postings.Keys);
        return terms.Count;
    }

    private bool TryGetDocument(long rowId, out Document document)
    {
        if (_overlay.Documents.TryGetValue(rowId, out document!))
            return true;

        for (var index = _segments.Count - 1; index >= 0; index--)
        {
            var slot = _segments[index];
            if (slot.Segment.TryGetOrdinal(rowId, out var ordinal) && !slot.IsDeleted(rowId))
            {
                document = slot.Segment.Documents[ordinal];
                return true;
            }
        }

        document = null!;
        return false;
    }

    private IEnumerable<long> EnumerateLiveRowIds()
    {
        var overlay = _overlay;
        foreach (var rowId in overlay.Documents.Keys)
            yield return rowId;

        foreach (var slot in _segments)
        {
            foreach (var rowId in slot.Segment.RowIds)
            {
                if (!slot.IsDeleted(rowId))
                    yield return rowId;
            }
        }
    }

    /// <summary>Every live posting of <paramref name="term"/>, in no particular order.</summary>
    private IEnumerable<Posting> EnumerateLivePostings(string term)
    {
        foreach (var slot in _segments)
        {
            var segment = slot.Segment;
            if (!segment.TermIds.TryGetValue(term, out var termId))
                continue;

            var end = segment.TermStarts[termId + 1];
            for (var index = segment.TermStarts[termId]; index < end; index++)
            {
                if (slot.Deleted.Count != 0 && slot.Deleted.Contains(segment.RowIds[segment.PostingDocuments[index]]))
                    continue;

                yield return segment.GetPosting(index);
            }
        }

        var overlay = _overlay;
        if (!overlay.Postings.TryGetValue(term, out var list))
            yield break;

        foreach (var posting in list.Entries)
        {
            if (overlay.IsLive(posting))
                yield return posting;
        }
    }

    private bool HasLivePosting(string term)
    {
        foreach (var slot in _segments)
        {
            var segment = slot.Segment;
            if (!segment.TermIds.TryGetValue(term, out var termId))
                continue;

            if (slot.Deleted.Count == 0)
                return true;

            var end = segment.TermStarts[termId + 1];
            for (var index = segment.TermStarts[termId]; index < end; index++)
            {
                if (!slot.Deleted.Contains(segment.RowIds[segment.PostingDocuments[index]]))
                    return true;
            }
        }

        var overlay = _overlay;
        if (overlay.Postings.TryGetValue(term, out var list))
        {
            foreach (var posting in list.Entries)
            {
                if (overlay.IsLive(posting))
                    return true;
            }
        }

        return false;
    }

    private bool TryGetLivePosting(string term, long rowId, out Posting posting)
    {
        var overlay = _overlay;
        if (overlay.Documents.ContainsKey(rowId))
        {
            if (overlay.Postings.TryGetValue(term, out var list)
                && list.TryGet(rowId, out posting)
                && overlay.IsLive(posting))
            {
                return true;
            }

            posting = default;
            return false;
        }

        for (var index = _segments.Count - 1; index >= 0; index--)
        {
            var slot = _segments[index];
            var segment = slot.Segment;
            if (!segment.TryGetOrdinal(rowId, out var ordinal) || slot.IsDeleted(rowId))
                continue;

            if (segment.TermIds.TryGetValue(term, out var termId)
                && segment.TryFindPosting(termId, ordinal, out var postingIndex))
            {
                posting = segment.GetPosting(postingIndex);
                return true;
            }

            break;
        }

        posting = default;
        return false;
    }

    /// <summary>
    /// Live terms starting with <paramref name="prefix"/>, in ordinal order. A term whose postings
    /// are all superseded is skipped without mutating anything, so prefix expansion can never be
    /// charged for stale terms and never writes shared state from a query.
    /// </summary>
    private IEnumerable<string> EnumerateLiveTermsWithPrefix(string prefix)
    {
        var sources = new List<string[]>(_segments.Count + 1);
        foreach (var slot in _segments)
            sources.Add(slot.Segment.Terms);
        sources.Add(_overlay.GetSortedTerms());

        var cursors = new int[sources.Count];
        for (var source = 0; source < sources.Count; source++)
        {
            var start = Array.BinarySearch(sources[source], prefix, StringComparer.Ordinal);
            cursors[source] = start < 0 ? ~start : start;
        }

        while (true)
        {
            string? next = null;
            for (var source = 0; source < sources.Count; source++)
            {
                var terms = sources[source];
                if (cursors[source] >= terms.Length
                    || !terms[cursors[source]].StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var candidate = terms[cursors[source]];
                if (next is null || string.CompareOrdinal(candidate, next) < 0)
                    next = candidate;
            }

            if (next is null)
                yield break;

            for (var source = 0; source < sources.Count; source++)
            {
                var terms = sources[source];
                if (cursors[source] < terms.Length && string.Equals(terms[cursors[source]], next, StringComparison.Ordinal))
                    cursors[source]++;
            }

            if (HasLivePosting(next))
                yield return next;
        }
    }

    private bool RemoveCore(long rowId)
    {
        FlushBulkBuilder();
        if (_overlay.Documents.TryGetValue(rowId, out var document))
        {
            EnsureOverlayWritable();
            _overlay.Documents.Remove(rowId);

            // Cheap delete: the overlay entries become tombstones, discovered by readers through the
            // generation check and dropped when the overlay is frozen or compacted.
            _overlay.TombstonedPostings += document.PostingCount;
            RetireDocumentStatistics(document);
            return true;
        }

        for (var index = _segments.Count - 1; index >= 0; index--)
        {
            var slot = _segments[index];
            if (!slot.Segment.TryGetOrdinal(rowId, out var ordinal) || slot.IsDeleted(rowId))
                continue;

            var retired = slot.Segment.Documents[ordinal];
            slot.MarkDeleted(rowId, retired.PostingCount);
            RetireDocumentStatistics(retired);
            return true;
        }

        return false;
    }

    private void RetireDocumentStatistics(Document document)
    {
        _documentCount--;
        for (var column = 0; column < _columnCount; column++)
            _columnTokenTotals[column] -= document.ColumnLengths[column];
    }

    private void EnsureOverlayWritable()
    {
        if (!_overlayShared)
            return;

        _overlay = _overlay.Clone();
        _overlayShared = false;
    }

    private void FreezeIfNeeded()
    {
        if (!_bulkLoad && _overlay.TotalPostings >= OverlayFreezeThreshold)
            FreezeOverlay();
    }

    private void FreezeOverlay()
    {
        var segment = BuildSegment([], _overlay);
        _overlay = new Overlay();
        _overlayShared = false;
        if (segment.DocumentCount > 0)
            _segments.Add(new SegmentSlot(segment));

        MergeSegmentsIfNeeded();
    }

    /// <summary>
    /// Tiered merging: a segment whose deletions dominate is rewritten on its own, and once
    /// <see cref="MergeFactor"/> segments share a size tier they are merged into one. Each posting is
    /// therefore rewritten O(log n) times over the life of the index, never on every write.
    /// </summary>
    private void MergeSegmentsIfNeeded()
    {
        while (true)
        {
            var rewrite = _segments.FindIndex(static slot =>
                slot.Segment.PostingCount > 0 && slot.DeletedPostings * 2 >= slot.Segment.PostingCount);
            if (rewrite >= 0)
            {
                ReplaceSegments([rewrite]);
                continue;
            }

            List<int>? tier = null;
            var tiers = new Dictionary<int, List<int>>();
            for (var index = 0; index < _segments.Count; index++)
            {
                var level = Tier(_segments[index].LivePostings);
                if (!tiers.TryGetValue(level, out var members))
                    tiers.Add(level, members = []);
                members.Add(index);
                if (members.Count >= MergeFactor && (tier is null || level < Tier(_segments[tier[0]].LivePostings)))
                    tier = members;
            }

            if (tier is null)
                return;

            ReplaceSegments(tier);
        }
    }

    private int Tier(long postings)
    {
        var tier = 0;
        var bound = Math.Max(MergeTierBasePostings, 1);
        while (postings >= bound)
        {
            tier++;
            bound *= MergeFactor;
        }

        return tier;
    }

    private void ReplaceSegments(IReadOnlyList<int> indices)
    {
        var selected = indices.Select(index => _segments[index]).ToArray();
        var merged = BuildSegment(selected, overlay: null);
        foreach (var index in indices.OrderByDescending(static index => index))
            _segments.RemoveAt(index);

        if (merged.DocumentCount > 0)
            _segments.Add(new SegmentSlot(merged));
    }

    /// <summary>Merges every segment and the overlay into at most one segment, purging all tombstones.</summary>
    private int CompactStorage()
    {
        var removed = _overlay.TombstonedPostings + SegmentTombstonedPostings();
        if (removed == 0 && _segments.Count <= 1 && _overlay.TotalPostings == 0)
            return 0;

        var merged = BuildSegment(_segments, _overlay);
        _segments.Clear();
        _overlay = new Overlay();
        _overlayShared = false;
        if (merged.DocumentCount > 0)
            _segments.Add(new SegmentSlot(merged));

        return (int)Math.Min(removed, int.MaxValue);
    }

    /// <summary>
    /// Builds one segment from the live documents of <paramref name="slots"/> and
    /// <paramref name="overlay"/>. Inputs are only read, so shared segments and a shared overlay are
    /// never modified.
    /// </summary>
    private Segment BuildSegment(IReadOnlyList<SegmentSlot> slots, Overlay? overlay)
    {
        var documents = new List<Document>();
        foreach (var slot in slots)
        {
            var segment = slot.Segment;
            for (var ordinal = 0; ordinal < segment.DocumentCount; ordinal++)
            {
                if (!slot.IsDeleted(segment.RowIds[ordinal]))
                    documents.Add(segment.Documents[ordinal]);
            }
        }

        if (overlay is not null)
            documents.AddRange(overlay.Documents.Values);

        documents.Sort(static (left, right) => left.RowId.CompareTo(right.RowId));
        var rowIds = new long[documents.Count];
        for (var index = 0; index < rowIds.Length; index++)
            rowIds[index] = documents[index].RowId;

        var remaps = new int[slots.Count][];
        var termSet = new HashSet<string>(StringComparer.Ordinal);
        for (var source = 0; source < slots.Count; source++)
        {
            var slot = slots[source];
            var segment = slot.Segment;
            var remap = new int[segment.DocumentCount];
            for (var ordinal = 0; ordinal < remap.Length; ordinal++)
            {
                remap[ordinal] = slot.IsDeleted(segment.RowIds[ordinal])
                    ? -1
                    : Array.BinarySearch(rowIds, segment.RowIds[ordinal]);
            }

            remaps[source] = remap;
            termSet.UnionWith(segment.Terms);
        }

        if (overlay is not null)
            termSet.UnionWith(overlay.Postings.Keys);

        var terms = termSet.ToArray();
        Array.Sort(terms, StringComparer.Ordinal);

        var recordPositions = _detail == ManagedFtsDetailLevel.Full;
        var recordColumnFrequencies = !recordPositions
            && (_detail == ManagedFtsDetailLevel.Columns || _scoringProfile == ManagedFtsScoringProfile.SqliteFts5);
        var keptTerms = new List<string>(terms.Length);
        var termStarts = new List<int>(terms.Length + 1) { 0 };
        var postingDocuments = new List<int>();
        var postingFrequencies = new List<int>();
        var postingMasks = new List<uint>();
        var positionStarts = recordPositions ? new List<int> { 0 } : null;
        var positions = new List<long>();
        var columnFrequencyStarts = recordColumnFrequencies ? new List<int> { 0 } : null;
        var columnFrequencies = new List<int>();
        var pending = new List<PendingPosting>();
        foreach (var term in terms)
        {
            pending.Clear();
            for (var source = 0; source < slots.Count; source++)
            {
                var segment = slots[source].Segment;
                if (!segment.TermIds.TryGetValue(term, out var termId))
                    continue;

                var remap = remaps[source];
                var end = segment.TermStarts[termId + 1];
                for (var index = segment.TermStarts[termId]; index < end; index++)
                {
                    var ordinal = remap[segment.PostingDocuments[index]];
                    if (ordinal >= 0)
                        pending.Add(new PendingPosting(ordinal, segment.GetPosting(index)));
                }
            }

            if (overlay is not null && overlay.Postings.TryGetValue(term, out var list))
            {
                foreach (var posting in list.Entries)
                {
                    if (overlay.IsLive(posting))
                        pending.Add(new PendingPosting(Array.BinarySearch(rowIds, posting.RowId), posting));
                }
            }

            if (pending.Count == 0)
                continue;

            pending.Sort(static (left, right) => left.Ordinal.CompareTo(right.Ordinal));
            foreach (var entry in pending)
            {
                postingDocuments.Add(entry.Ordinal);
                postingFrequencies.Add(entry.Posting.Frequency);
                postingMasks.Add(entry.Posting.ColumnMask);
                if (positionStarts is not null)
                {
                    positions.AddRange(entry.Posting.Positions.Span);
                    positionStarts.Add(positions.Count);
                }

                if (columnFrequencyStarts is not null)
                {
                    columnFrequencies.AddRange(entry.Posting.ColumnFrequencies.Span);
                    columnFrequencyStarts.Add(columnFrequencies.Count);
                }
            }

            keptTerms.Add(term);
            termStarts.Add(postingDocuments.Count);
        }

        return new Segment(
            rowIds,
            documents.ToArray(),
            keptTerms.ToArray(),
            termStarts.ToArray(),
            postingDocuments.ToArray(),
            postingFrequencies.ToArray(),
            postingMasks.ToArray(),
            positionStarts?.ToArray(),
            positions.ToArray(),
            columnFrequencyStarts?.ToArray(),
            columnFrequencies.ToArray());
    }

    private readonly record struct PendingPosting(int Ordinal, Posting Posting);

    /// <summary>
    /// Streams freshly staged documents straight into flat posting arrays, then orders them by term
    /// with one counting sort. Rows usually arrive in rowid order (the row store's order), in which
    /// case no document reordering happens at all.
    /// </summary>
    private sealed class BulkBuilder(ManagedFtsSearchIndex owner)
    {
        private readonly List<Document> _documents = [];
        private readonly HashSet<long> _rowIds = [];
        private readonly Dictionary<string, int> _termIds = new(StringComparer.Ordinal);
        private readonly List<string> _terms = [];
        private readonly List<int> _postingTerms = [];
        private readonly List<int> _postingDocuments = [];
        private readonly List<int> _postingFrequencies = [];
        private readonly List<uint> _postingMasks = [];
        private readonly List<int> _positionStarts = [0];
        private readonly List<long> _positions = [];
        private readonly List<int> _columnFrequencyStarts = [0];
        private readonly List<int> _columnFrequencies = [];
        private readonly bool _recordPositions = owner._detail == ManagedFtsDetailLevel.Full;
        private readonly bool _recordColumnFrequencies = owner._detail != ManagedFtsDetailLevel.Full
            && (owner._detail == ManagedFtsDetailLevel.Columns
                || owner._scoringProfile == ManagedFtsScoringProfile.SqliteFts5);
        private long _lastRowId = long.MinValue;
        private bool _inRowIdOrder = true;

        public bool TryAdd(long rowId, StagedDocument staged)
        {
            if (!_rowIds.Add(rowId))
                return false;

            if (rowId < _lastRowId)
                _inRowIdOrder = false;
            _lastRowId = Math.Max(_lastRowId, rowId);

            var arrival = _documents.Count;
            _documents.Add(new Document(rowId, staged.ColumnLengths, staged.Terms.Count, Generation: 0));
            foreach (var (term, occurrence) in staged.Terms)
            {
                if (!_termIds.TryGetValue(term, out var termId))
                {
                    termId = _terms.Count;
                    _termIds.Add(term, termId);
                    _terms.Add(term);
                }

                _postingTerms.Add(termId);
                _postingDocuments.Add(arrival);
                _postingFrequencies.Add(occurrence.Frequency);
                _postingMasks.Add(occurrence.ColumnMask);
                if (_recordPositions)
                {
                    occurrence.Positions.Sort();
                    _positions.AddRange(occurrence.Positions);
                    _positionStarts.Add(_positions.Count);
                }
                else if (_recordColumnFrequencies)
                {
                    for (var column = 0; column < owner._columnCount; column++)
                    {
                        if ((occurrence.ColumnMask & (1u << column)) != 0)
                            _columnFrequencies.Add(occurrence.ColumnFrequencies[column]);
                    }

                    _columnFrequencyStarts.Add(_columnFrequencies.Count);
                }
            }

            return true;
        }

        public Segment Build()
        {
            var documentCount = _documents.Count;
            var arrivalToOrdinal = new int[documentCount];
            var documents = _documents.ToArray();
            if (_inRowIdOrder)
            {
                for (var index = 0; index < documentCount; index++)
                    arrivalToOrdinal[index] = index;
            }
            else
            {
                var order = new int[documentCount];
                var keys = new long[documentCount];
                for (var index = 0; index < documentCount; index++)
                {
                    order[index] = index;
                    keys[index] = documents[index].RowId;
                }

                Array.Sort(keys, order);
                var sorted = new Document[documentCount];
                for (var ordinal = 0; ordinal < documentCount; ordinal++)
                {
                    arrivalToOrdinal[order[ordinal]] = ordinal;
                    sorted[ordinal] = documents[order[ordinal]];
                }

                documents = sorted;
            }

            var rowIds = new long[documentCount];
            for (var ordinal = 0; ordinal < documentCount; ordinal++)
                rowIds[ordinal] = documents[ordinal].RowId;

            // Term ids in ordinal term order, then a counting sort of the postings by that rank.
            var terms = _terms.ToArray();
            var termOrder = new int[terms.Length];
            for (var index = 0; index < termOrder.Length; index++)
                termOrder[index] = index;
            Array.Sort((string[])terms.Clone(), termOrder, StringComparer.Ordinal);
            var rank = new int[terms.Length];
            var sortedTerms = new string[terms.Length];
            for (var position = 0; position < termOrder.Length; position++)
            {
                rank[termOrder[position]] = position;
                sortedTerms[position] = terms[termOrder[position]];
            }

            var postingCount = _postingTerms.Count;
            var termStarts = new int[terms.Length + 1];
            for (var posting = 0; posting < postingCount; posting++)
                termStarts[rank[_postingTerms[posting]] + 1]++;
            for (var term = 0; term < terms.Length; term++)
                termStarts[term + 1] += termStarts[term];

            var placement = new int[postingCount];
            var next = (int[])termStarts.Clone();
            for (var posting = 0; posting < postingCount; posting++)
                placement[next[rank[_postingTerms[posting]]]++] = posting;

            var postingDocuments = new int[postingCount];
            for (var index = 0; index < postingCount; index++)
                postingDocuments[index] = arrivalToOrdinal[_postingDocuments[placement[index]]];

            if (!_inRowIdOrder)
            {
                for (var term = 0; term < terms.Length; term++)
                {
                    var start = termStarts[term];
                    Array.Sort(postingDocuments, placement, start, termStarts[term + 1] - start);
                }
            }

            var postingFrequencies = new int[postingCount];
            var postingMasks = new uint[postingCount];
            int[]? positionStarts = _recordPositions ? new int[postingCount + 1] : null;
            var positions = _recordPositions ? new long[_positions.Count] : [];
            int[]? columnFrequencyStarts = _recordColumnFrequencies ? new int[postingCount + 1] : null;
            var columnFrequencies = _recordColumnFrequencies ? new int[_columnFrequencies.Count] : [];
            var positionCursor = 0;
            var columnCursor = 0;
            for (var index = 0; index < postingCount; index++)
            {
                var source = placement[index];
                postingFrequencies[index] = _postingFrequencies[source];
                postingMasks[index] = _postingMasks[source];
                if (positionStarts is not null)
                {
                    var from = _positionStarts[source];
                    var length = _positionStarts[source + 1] - from;
                    for (var offset = 0; offset < length; offset++)
                        positions[positionCursor + offset] = _positions[from + offset];
                    positionCursor += length;
                    positionStarts[index + 1] = positionCursor;
                }

                if (columnFrequencyStarts is not null)
                {
                    var from = _columnFrequencyStarts[source];
                    var length = _columnFrequencyStarts[source + 1] - from;
                    for (var offset = 0; offset < length; offset++)
                        columnFrequencies[columnCursor + offset] = _columnFrequencies[from + offset];
                    columnCursor += length;
                    columnFrequencyStarts[index + 1] = columnCursor;
                }
            }

            return new Segment(
                rowIds,
                documents,
                sortedTerms,
                termStarts,
                postingDocuments,
                postingFrequencies,
                postingMasks,
                positionStarts,
                positions,
                columnFrequencyStarts,
                columnFrequencies);
        }
    }

    /// <summary>An immutable, column-wise posting segment. Never modified after construction.</summary>
    private sealed class Segment
    {
        public Segment(
            long[] rowIds,
            Document[] documents,
            string[] terms,
            int[] termStarts,
            int[] postingDocuments,
            int[] postingFrequencies,
            uint[] postingMasks,
            int[]? positionStarts,
            long[] positions,
            int[]? columnFrequencyStarts,
            int[] columnFrequencies)
        {
            RowIds = rowIds;
            Documents = documents;
            Terms = terms;
            TermStarts = termStarts;
            PostingDocuments = postingDocuments;
            PostingFrequencies = postingFrequencies;
            PostingMasks = postingMasks;
            PositionStarts = positionStarts;
            Positions = positions;
            ColumnFrequencyStarts = columnFrequencyStarts;
            ColumnFrequencies = columnFrequencies;
            TermIds = new Dictionary<string, int>(terms.Length, StringComparer.Ordinal);
            for (var index = 0; index < terms.Length; index++)
                TermIds.Add(terms[index], index);
        }

        /// <summary>Ascending; a document's ordinal is its index here.</summary>
        public long[] RowIds { get; }

        public Document[] Documents { get; }

        /// <summary>Ascending ordinal order; a term's id is its index here.</summary>
        public string[] Terms { get; }

        public Dictionary<string, int> TermIds { get; }

        /// <summary>Term id to its first posting; one extra trailing entry closes the last run.</summary>
        public int[] TermStarts { get; }

        /// <summary>Per posting, ascending within a term: the document ordinal.</summary>
        public int[] PostingDocuments { get; }

        public int[] PostingFrequencies { get; }

        public uint[] PostingMasks { get; }

        public int[]? PositionStarts { get; }

        public long[] Positions { get; }

        public int[]? ColumnFrequencyStarts { get; }

        public int[] ColumnFrequencies { get; }

        public int DocumentCount => RowIds.Length;

        public int PostingCount => PostingDocuments.Length;

        public bool TryGetOrdinal(long rowId, out int ordinal)
        {
            ordinal = Array.BinarySearch(RowIds, rowId);
            return ordinal >= 0;
        }

        public bool TryFindPosting(int termId, int ordinal, out int index)
        {
            var start = TermStarts[termId];
            var found = Array.BinarySearch(PostingDocuments, start, TermStarts[termId + 1] - start, ordinal);
            index = found;
            return found >= 0;
        }

        public Posting GetPosting(int index)
            => new(
                RowIds[PostingDocuments[index]],
                Generation: 0,
                PostingFrequencies[index],
                PostingMasks[index],
                PositionStarts is { } positionStarts
                    ? Positions.AsMemory(positionStarts[index], positionStarts[index + 1] - positionStarts[index])
                    : ReadOnlyMemory<long>.Empty,
                ColumnFrequencyStarts is { } columnStarts
                    ? ColumnFrequencies.AsMemory(columnStarts[index], columnStarts[index + 1] - columnStarts[index])
                    : ReadOnlyMemory<int>.Empty);
    }

    /// <summary>One index's view of a shared segment: the rowids this index has deleted from it.</summary>
    private sealed class SegmentSlot
    {
        private HashSet<long> _deleted;
        private bool _deletedShared;

        public SegmentSlot(Segment segment)
            : this(segment, [], deletedShared: false, deletedPostings: 0)
        {
        }

        private SegmentSlot(Segment segment, HashSet<long> deleted, bool deletedShared, long deletedPostings)
        {
            Segment = segment;
            _deleted = deleted;
            _deletedShared = deletedShared;
            DeletedPostings = deletedPostings;
        }

        public Segment Segment { get; }

        public IReadOnlySet<long> Deleted => _deleted;

        /// <summary>Postings belonging to deleted documents, still physically present.</summary>
        public long DeletedPostings { get; private set; }

        public long LivePostings => Segment.PostingCount - DeletedPostings;

        public bool IsDeleted(long rowId) => _deleted.Count != 0 && _deleted.Contains(rowId);

        public void MarkDeleted(long rowId, int postings)
        {
            if (_deletedShared)
            {
                _deleted = new HashSet<long>(_deleted);
                _deletedShared = false;
            }

            if (_deleted.Add(rowId))
                DeletedPostings += postings;
        }

        /// <summary>Both this slot and the returned one copy the deleted set before their next write.</summary>
        public SegmentSlot ShareForFork()
        {
            _deletedShared = true;
            return new SegmentSlot(Segment, _deleted, deletedShared: true, DeletedPostings);
        }
    }

    /// <summary>The mutable tail: recent writes, with generation-stamped tombstones.</summary>
    private sealed class Overlay
    {
        public Dictionary<long, Document> Documents { get; } = [];

        public Dictionary<string, PostingList> Postings { get; } = new(StringComparer.Ordinal);

        public long Generation { get; set; }

        public long TombstonedPostings { get; set; }

        public long TotalPostings { get; set; }

        private string[]? _sortedTerms;

        public void InvalidateSortedTerms() => _sortedTerms = null;

        public string[] GetSortedTerms()
        {
            if (_sortedTerms is { } cached)
                return cached;

            var terms = Postings.Keys.ToArray();
            Array.Sort(terms, StringComparer.Ordinal);
            _sortedTerms = terms;
            return terms;
        }

        public bool IsLive(in Posting posting)
            => Documents.TryGetValue(posting.RowId, out var document) && document.Generation == posting.Generation;

        public Overlay Clone()
        {
            var clone = new Overlay
            {
                Generation = Generation,
                TombstonedPostings = TombstonedPostings,
                TotalPostings = TotalPostings,
                _sortedTerms = _sortedTerms,
            };
            foreach (var (rowId, document) in Documents)
                clone.Documents.Add(rowId, document);
            foreach (var (term, list) in Postings)
                clone.Postings.Add(term, list.Clone());
            return clone;
        }
    }
}
