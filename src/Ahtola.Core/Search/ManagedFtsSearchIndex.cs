using System.Globalization;
using System.Text;

namespace Ahtola.Core.Search;

/// <summary>One scored search hit. Ordering is score descending, then rowid ascending.</summary>
internal readonly record struct ManagedFtsHit(long RowId, double Score);

internal enum ManagedFtsScoringProfile
{
    Managed,
    SqliteFts5,
}

/// <summary>
/// The managed inverted index behind a <c>USING fts</c> method index: term dictionary, per-document
/// postings with term frequency, column mask and token positions, deterministic Okapi BM25 scoring,
/// generation-stamped deletes, and explicit compaction.
/// </summary>
/// <remarks>
/// <para>
/// This is a native managed implementation, not a port of Turso's Tantivy-on-a-blob-directory FTS
/// (turso-src/core/index_method/fts.rs). The observable SQL surface and the merge thresholds are
/// aligned; the storage representation deliberately is not. See docs/managed-index-methods.md.
/// </para>
/// <para>
/// Positions are encoded as <c>(column &lt;&lt; 32) | position</c> so a single sorted
/// <see cref="long"/> array supports both phrase adjacency and NEAR proximity without a second
/// allocation per column.
/// </para>
/// <para>
/// Every posting carries the generation of the document image it was produced from. A rowid that is
/// deleted and re-inserted — an UPSERT, a REPLACE, or SQLite's rowid reuse after a delete — gets a
/// fresh generation, so the previous image's postings can never be mistaken for live terms of the
/// new image even before compaction reclaims them.
/// </para>
/// </remarks>
internal sealed class ManagedFtsSearchIndex
{
    /// <summary>Okapi BM25 term-frequency saturation parameter.</summary>
    private const double BM25K1 = 1.2;

    /// <summary>Okapi BM25 length-normalization parameter.</summary>
    private const double BM25B = 0.75;

    /// <summary>Compaction threshold ported from Turso's FTS_DELETED_DOCS_MERGE_THRESHOLD (fts.rs:76).</summary>
    public const double DeletedDocumentsCompactionThreshold = 0.30;

    /// <summary>Upper bound on documents compacted inline in a user statement (fts.rs:73-91).</summary>
    public const int MaxSynchronousCompactionDocuments = 64_000;

    private readonly int _columnCount;
    private readonly ManagedFtsTokenizerOptions _tokenizer;
    private readonly double[] _columnWeights;
    private readonly ManagedFtsDetailLevel _detail;
    private readonly bool _columnSize;
    private readonly ManagedFtsScoringProfile _scoringProfile;
    private readonly Dictionary<long, Document> _documents = [];
    private readonly Dictionary<string, PostingList> _postings = new(StringComparer.Ordinal);
    private readonly long[] _columnTokenTotals;
    private string[]? _sortedTerms;
    private long _tombstonedPostings;
    private long _generation;

    public ManagedFtsSearchIndex(
        int columnCount,
        ManagedFtsTokenizerOptions tokenizer,
        IReadOnlyList<double> columnWeights,
        ManagedFtsDetailLevel detail = ManagedFtsDetailLevel.Full,
        bool columnSize = true,
        ManagedFtsScoringProfile scoringProfile = ManagedFtsScoringProfile.Managed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columnCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columnCount, sizeof(uint) * 8);
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentNullException.ThrowIfNull(columnWeights);
        if (columnWeights.Count != columnCount)
            throw new ArgumentException("One weight per indexed column is required.", nameof(columnWeights));

        _columnCount = columnCount;
        _tokenizer = tokenizer;
        _columnWeights = columnWeights.ToArray();
        _detail = detail;
        _columnSize = columnSize;
        _scoringProfile = scoringProfile;
        _columnTokenTotals = new long[columnCount];
    }

    /// <summary>Live document count (tombstones excluded).</summary>
    public int DocumentCount => _documents.Count;

    /// <summary>Distinct indexed terms, including terms whose postings are entirely tombstoned.</summary>
    public int TermCount => _postings.Count;

    /// <summary>Posting entries that refer to deleted or superseded documents, awaiting compaction.</summary>
    public long TombstonedPostings => _tombstonedPostings;

    /// <summary>Total posting entries, live and tombstoned.</summary>
    public long TotalPostings { get; private set; }

    /// <summary>The detail level this index was configured with.</summary>
    public ManagedFtsDetailLevel Detail => _detail;

    /// <summary>True when per-column token lengths participate in BM25 normalization.</summary>
    public bool ColumnSizeEnabled => _columnSize;

    /// <summary>True when tombstones exceed the compaction threshold ported from Turso.</summary>
    public bool NeedsCompaction
        => TotalPostings > 0
            && (double)_tombstonedPostings / TotalPostings >= DeletedDocumentsCompactionThreshold;

    public bool ContainsDocument(long rowId) => _documents.ContainsKey(rowId);

    /// <summary>The base row this document was derived from, used for reference-identity refresh.</summary>
    public bool TryGetSource(long rowId, out SqlValue[] source)
    {
        if (_documents.TryGetValue(rowId, out var document))
        {
            source = document.Source;
            return true;
        }

        source = [];
        return false;
    }

    public IEnumerable<long> RowIds => _documents.Keys;

    public void Clear()
    {
        _documents.Clear();
        _postings.Clear();
        Array.Clear(_columnTokenTotals);
        _sortedTerms = null;
        _tombstonedPostings = 0;
        TotalPostings = 0;
        _generation = 0;
    }

    /// <summary>
    /// Indexes one base row. Column values outside TEXT affinity are read as text or skipped.
    /// </summary>
    /// <remarks>
    /// Tokenization runs to completion, and every limit is enforced, before a single byte of index
    /// state is mutated. A document that trips a limit therefore leaves the index exactly as it was
    /// rather than half-updated with the old image already removed.
    /// </remarks>
    public void Upsert(long rowId, SqlValue[] source, ReadOnlySpan<SqlValue> columnValues)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (columnValues.Length != _columnCount)
            throw new ArgumentException("Column value count does not match the index column count.", nameof(columnValues));

        var staged = Stage(columnValues);
        Publish(rowId, source, staged);
    }

    /// <summary>
    /// Tokenizes one row into a staged document image without touching index state. Splitting the
    /// work in two is what makes a failed insert or a failed statement leave no partial state.
    /// </summary>
    private StagedDocument Stage(ReadOnlySpan<SqlValue> columnValues)
    {
        var lengths = new int[_columnCount];
        var perTerm = new Dictionary<string, TermOccurrence>(StringComparer.Ordinal);
        var totalPositions = 0;
        for (var column = 0; column < _columnCount; column++)
        {
            var text = ReadText(columnValues[column]);
            if (text.Length == 0)
                continue;

            var tokens = ManagedFtsTokenization.Tokenize(text, _tokenizer);
            lengths[column] = tokens.Count;
            foreach (var token in tokens)
            {
                if (++totalPositions > ManagedFtsLimits.MaxPositionsPerDocument)
                {
                    throw new EmbeddedSqlException(
                        $"fts document exceeds {ManagedFtsLimits.MaxPositionsPerDocument} indexed positions");
                }

                if (!perTerm.TryGetValue(token.Text, out var occurrence))
                {
                    occurrence = new TermOccurrence();
                    if (_detail == ManagedFtsDetailLevel.Columns
                        || _scoringProfile == ManagedFtsScoringProfile.SqliteFts5)
                    {
                        occurrence.ColumnFrequencies = new int[_columnCount];
                    }
                    perTerm.Add(token.Text, occurrence);
                }

                occurrence.Frequency++;
                occurrence.ColumnMask |= 1u << column;
                if (_detail == ManagedFtsDetailLevel.Full)
                    occurrence.Positions.Add(EncodePosition(column, token.Position));
                else if (_detail == ManagedFtsDetailLevel.Columns
                    || _scoringProfile == ManagedFtsScoringProfile.SqliteFts5)
                {
                    occurrence.ColumnFrequencies[column]++;
                }
            }
        }

        return new StagedDocument(lengths, perTerm);
    }

    private void Publish(long rowId, SqlValue[] source, StagedDocument staged)
    {
        // Remove first so a re-inserted rowid retires its previous postings, then take the new
        // generation: readers compare a posting's generation against the live document's, so a
        // superseded posting is invisible immediately rather than only after compaction.
        Remove(rowId);

        var generation = ++_generation;
        for (var column = 0; column < _columnCount; column++)
            _columnTokenTotals[column] += staged.ColumnLengths[column];

        _documents.Add(
            rowId,
            new Document(rowId, source, staged.ColumnLengths, staged.Terms.Count, generation));
        foreach (var (term, occurrence) in staged.Terms)
        {
            if (!_postings.TryGetValue(term, out var list))
            {
                list = new PostingList();
                _postings.Add(term, list);
                _sortedTerms = null;
            }

            var positions = Array.Empty<long>();
            var columnFrequencies = Array.Empty<int>();
            if (_detail == ManagedFtsDetailLevel.Full)
            {
                occurrence.Positions.Sort();
                positions = occurrence.Positions.ToArray();
            }
            else if (_detail == ManagedFtsDetailLevel.Columns
                || _scoringProfile == ManagedFtsScoringProfile.SqliteFts5)
            {
                // Compact to just the columns the term actually occurs in, ascending. This is the
                // minimum metadata a column-filtered query or SQLite-compatible per-column BM25
                // weight needs once positions are not recorded. detail=none still refuses column
                // filters; the derived frequencies are retained only for observable ranking.
                columnFrequencies = new int[System.Numerics.BitOperations.PopCount(occurrence.ColumnMask)];
                var next = 0;
                for (var column = 0; column < _columnCount; column++)
                {
                    if ((occurrence.ColumnMask & (1u << column)) != 0)
                        columnFrequencies[next++] = occurrence.ColumnFrequencies[column];
                }
            }

            list.Add(new Posting(
                rowId,
                generation,
                occurrence.Frequency,
                occurrence.ColumnMask,
                positions,
                columnFrequencies));
            TotalPostings++;
        }
    }

    /// <summary>
    /// Removes one document. Postings are left in place as tombstones and skipped by every reader
    /// (through the generation stamp) until <see cref="Compact"/> physically purges them.
    /// </summary>
    public bool Remove(long rowId)
    {
        if (!_documents.Remove(rowId, out var document))
            return false;

        for (var column = 0; column < _columnCount; column++)
            _columnTokenTotals[column] -= document.ColumnLengths[column];

        // Cheap delete: the posting entries become tombstones, discovered by readers through the
        // generation check and reclaimed by compaction.
        _tombstonedPostings += document.PostingCount;
        return true;
    }

    /// <summary>Physically purges tombstoned postings and drops terms that became empty.</summary>
    public int Compact()
    {
        if (_tombstonedPostings == 0)
            return 0;

        var removed = 0;
        var emptyTerms = new List<string>();
        foreach (var (term, list) in _postings)
        {
            removed += list.Purge(_documents);
            if (list.Count == 0)
                emptyTerms.Add(term);
        }

        foreach (var term in emptyTerms)
            _postings.Remove(term);

        if (emptyTerms.Count > 0)
            _sortedTerms = null;

        TotalPostings -= removed;
        _tombstonedPostings = 0;
        return removed;
    }

    /// <summary>Evaluates a parsed query and returns hits ordered by score desc, rowid asc.</summary>
    public IReadOnlyList<ManagedFtsHit> Search(ManagedFtsNode query, int? limit = null)
        => Search(query, _columnWeights, limit);

    /// <summary>Evaluates a parsed query with call-specific BM25 column weights.</summary>
    public IReadOnlyList<ManagedFtsHit> Search(
        ManagedFtsNode query,
        IReadOnlyList<double> columnWeights,
        int? limit = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(columnWeights);
        if (columnWeights.Count != _columnCount)
            throw new ArgumentException("One weight per indexed column is required.", nameof(columnWeights));

        var accumulator = new Dictionary<long, double>();
        var matches = Evaluate(query, accumulator, columnWeights);
        if (matches.Count > ManagedFtsLimits.MaxMatchRows)
            throw new EmbeddedSqlException($"fts query matches more than {ManagedFtsLimits.MaxMatchRows} rows");

        var hits = new List<ManagedFtsHit>(matches.Count);
        foreach (var rowId in matches)
            hits.Add(new ManagedFtsHit(rowId, accumulator.TryGetValue(rowId, out var score) ? score : 0.0));

        hits.Sort(static (left, right)
            => left.Score == right.Score
                ? left.RowId.CompareTo(right.RowId)
                : right.Score.CompareTo(left.Score));

        if (limit is { } max && max >= 0 && hits.Count > max)
            hits.RemoveRange(max, hits.Count - max);

        return hits;
    }

    /// <summary>True when the document matches, without computing a score.</summary>
    public bool Matches(ManagedFtsNode query, long rowId)
    {
        ArgumentNullException.ThrowIfNull(query);
        return _documents.ContainsKey(rowId)
            && Evaluate(query, null, _columnWeights).Contains(rowId);
    }

    /// <summary>The BM25 score of one document for a query, or 0 when it does not match.</summary>
    public double Score(
        ManagedFtsNode query,
        long rowId,
        IReadOnlyList<double>? columnWeights = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        var weights = columnWeights ?? _columnWeights;
        if (weights.Count != _columnCount)
            throw new ArgumentException("One weight per indexed column is required.", nameof(columnWeights));
        var accumulator = new Dictionary<long, double>();
        return Evaluate(query, accumulator, weights).Contains(rowId)
            && accumulator.TryGetValue(rowId, out var score)
            ? score
            : 0.0;
    }

    private HashSet<long> Evaluate(
        ManagedFtsNode node,
        Dictionary<long, double>? accumulator,
        IReadOnlyList<double> columnWeights)
        => node switch
        {
            ManagedFtsNoMatchNode => [],
            ManagedFtsTermNode term => EvaluateTerm(term, accumulator, columnWeights),
            ManagedFtsPhraseNode phrase => EvaluatePhrase(phrase, accumulator, columnWeights),
            ManagedFtsNearNode near => EvaluateNear(near, accumulator, columnWeights),
            ManagedFtsAndNode and => Intersect(
                Evaluate(and.Left, accumulator, columnWeights),
                Evaluate(and.Right, accumulator, columnWeights)),
            ManagedFtsOrNode or => Union(
                Evaluate(or.Left, accumulator, columnWeights),
                Evaluate(or.Right, accumulator, columnWeights)),
            ManagedFtsNotNode not => Exclude(EvaluateExclusion(not.Operand, columnWeights)),
            _ => throw new ArgumentOutOfRangeException(nameof(node)),
        };

    /// <summary>
    /// Evaluates a negated branch for its row set alone, into a scratch accumulator that is thrown
    /// away with the call.
    /// </summary>
    /// <remarks>
    /// A NOT branch names rows to remove; it never says a surviving row is more relevant. Sharing
    /// the query's accumulator with it leaks the excluded branch's BM25 contribution onto any row
    /// that survives through another branch — <c>(NOT b) OR x</c> and <c>a NOT (b NOT c)</c> both
    /// keep rows the negated branch scored — so the exclusion is evaluated in isolation instead.
    /// </remarks>
    private HashSet<long> EvaluateExclusion(
        ManagedFtsNode node,
        IReadOnlyList<double> columnWeights)
        => Evaluate(node, new Dictionary<long, double>(), columnWeights);

    private HashSet<long> EvaluateTerm(
        ManagedFtsTermNode term,
        Dictionary<long, double>? accumulator,
        IReadOnlyList<double> columnWeights)
    {
        if (term.AnchoredAtStart)
        {
            // An anchored term asks "is this the first token of the column", which needs the token's
            // position. Without positions the index cannot answer it, and silently reporting no
            // match would be a wrong answer rather than a missing feature.
            RequirePositions("anchored term");
        }

        var columnMask = ResolveColumnMask(term.Column);
        if (term.IsPrefix && _scoringProfile == ManagedFtsScoringProfile.SqliteFts5)
            return EvaluateSqlitePrefixTerm(term, accumulator, columnWeights, columnMask);

        var matches = new HashSet<long>();
        foreach (var expanded in ExpandTerm(term))
        {
            if (!_postings.TryGetValue(expanded, out var list))
                continue;

            var candidates = new List<ScoreCandidate>();
            foreach (var posting in list.Entries)
            {
                if (!IsLive(posting) || (posting.ColumnMask & columnMask) == 0)
                    continue;

                var frequency = term.AnchoredAtStart
                    ? CountAnchored(posting.Positions, columnMask)
                    : CountInColumns(posting, columnMask);
                if (frequency == 0)
                    continue;

                candidates.Add(new ScoreCandidate(
                    posting.RowId,
                    frequency,
                    term.AnchoredAtStart
                        ? posting.Positions
                            .Where(encoded => (encoded & 0xFFFFFFFFL) == 0
                                && (columnMask & (1u << (int)(encoded >> 32))) != 0)
                            .ToArray()
                        : posting.Positions,
                    posting.ColumnFrequencies,
                    posting.ColumnMask));
                matches.Add(posting.RowId);
            }

            if (accumulator is not null)
                Accumulate(accumulator, candidates, columnMask, columnWeights);
        }

        return matches;
    }

    private HashSet<long> EvaluateSqlitePrefixTerm(
        ManagedFtsTermNode term,
        Dictionary<long, double>? accumulator,
        IReadOnlyList<double> columnWeights,
        uint columnMask)
    {
        var aggregates = new Dictionary<long, PrefixScoreCandidate>();
        foreach (var expanded in ExpandTerm(term))
        {
            if (!_postings.TryGetValue(expanded, out var list))
                continue;

            foreach (var posting in list.Entries)
            {
                if (!IsLive(posting) || (posting.ColumnMask & columnMask) == 0)
                    continue;

                var frequency = term.AnchoredAtStart
                    ? CountAnchored(posting.Positions, columnMask)
                    : CountInColumns(posting, columnMask);
                if (frequency == 0)
                    continue;

                if (!aggregates.TryGetValue(posting.RowId, out var aggregate))
                {
                    aggregate = new PrefixScoreCandidate(_columnCount);
                    aggregates.Add(posting.RowId, aggregate);
                }

                aggregate.Frequency += frequency;
                aggregate.ColumnMask |= posting.ColumnMask & columnMask;
                if (posting.Positions.Length != 0)
                {
                    foreach (var encoded in posting.Positions)
                    {
                        var bit = 1u << (int)(encoded >> 32);
                        if ((columnMask & bit) != 0
                            && (!term.AnchoredAtStart || (encoded & 0xFFFFFFFFL) == 0))
                        {
                            aggregate.Positions.Add(encoded);
                        }
                    }
                }
                else
                {
                    var next = 0;
                    for (var column = 0; column < _columnCount; column++)
                    {
                        var bit = 1u << column;
                        if ((posting.ColumnMask & bit) == 0)
                            continue;
                        if ((columnMask & bit) != 0)
                            aggregate.ColumnFrequencies[column] += posting.ColumnFrequencies[next];
                        next++;
                    }
                }
            }
        }

        var matches = new HashSet<long>(aggregates.Keys);
        if (accumulator is null || aggregates.Count == 0)
            return matches;

        var candidates = new List<ScoreCandidate>(aggregates.Count);
        foreach (var (rowId, aggregate) in aggregates)
        {
            aggregate.Positions.Sort();
            var compactFrequencies = new int[System.Numerics.BitOperations.PopCount(aggregate.ColumnMask)];
            var next = 0;
            for (var column = 0; column < _columnCount; column++)
            {
                if ((aggregate.ColumnMask & (1u << column)) != 0)
                    compactFrequencies[next++] = aggregate.ColumnFrequencies[column];
            }

            candidates.Add(new ScoreCandidate(
                rowId,
                aggregate.Frequency,
                aggregate.Positions.ToArray(),
                aggregate.Positions.Count == 0 ? compactFrequencies : [],
                aggregate.ColumnMask));
        }

        Accumulate(accumulator, candidates, columnMask, columnWeights);
        return matches;
    }

    private HashSet<long> EvaluatePhrase(
        ManagedFtsPhraseNode phrase,
        Dictionary<long, double>? accumulator,
        IReadOnlyList<double> columnWeights,
        IReadOnlyDictionary<long, int[]>? includedRowFrequencies = null)
    {
        RequirePositions("phrase");
        var columnMask = ResolveColumnMask(phrase.Column);
        if (phrase.IsPrefix)
        {
            return EvaluatePrefixPhrase(
                phrase,
                accumulator,
                columnWeights,
                columnMask,
                includedRowFrequencies);
        }

        var candidates = new List<ScoreCandidate>();
        var matches = new HashSet<long>();
        foreach (var rowId in IntersectTerms(phrase.Terms))
        {
            var frequencies = CountPhraseByColumn(
                rowId,
                phrase.Terms,
                isPrefix: false,
                columnMask,
                phrase.AnchoredAtStart);
            var candidate = CreateFrequencyCandidate(rowId, frequencies);
            if (candidate.Frequency == 0)
                continue;

            matches.Add(rowId);
            candidates.Add(candidate);
        }

        if (accumulator is not null)
            Accumulate(accumulator, candidates, columnMask, columnWeights, includedRowFrequencies);

        return matches;
    }

    private HashSet<long> EvaluatePrefixPhrase(
        ManagedFtsPhraseNode phrase,
        Dictionary<long, double>? accumulator,
        IReadOnlyList<double> columnWeights,
        uint columnMask,
        IReadOnlyDictionary<long, int[]>? includedRowFrequencies)
    {
        var candidates = new List<ScoreCandidate>();
        var matches = new HashSet<long>();
        var nearPhrase = new ManagedFtsNearPhrase(phrase.Terms, IsPrefix: true);
        foreach (var rowId in FindPhraseCandidates(nearPhrase))
        {
            var frequencies = CountPhraseByColumn(
                rowId,
                phrase.Terms,
                isPrefix: true,
                columnMask,
                phrase.AnchoredAtStart);
            var candidate = CreateFrequencyCandidate(rowId, frequencies);
            if (candidate.Frequency == 0)
                continue;

            matches.Add(rowId);
            candidates.Add(candidate);
        }

        if (accumulator is not null)
        {
            Accumulate(
                accumulator,
                candidates,
                columnMask,
                columnWeights,
                includedRowFrequencies);
        }

        return matches;
    }

    private HashSet<long> EvaluateNear(
        ManagedFtsNearNode near,
        Dictionary<long, double>? accumulator,
        IReadOnlyList<double> columnWeights)
    {
        RequirePositions("NEAR");
        var columnMask = ResolveColumnMask(near.Column);
        if (near.SqliteDistance)
            return EvaluateSqliteNear(near, accumulator, columnWeights, columnMask);

        var terms = near.Phrases.SelectMany(static phrase => phrase.Terms).ToArray();
        var candidates = new List<ScoreCandidate>();
        var matches = new HashSet<long>();
        foreach (var rowId in IntersectTerms(terms))
        {
            var frequency = CountNear(rowId, terms, near.Distance, columnMask);
            if (frequency == 0)
                continue;

            matches.Add(rowId);
            candidates.Add(new ScoreCandidate(rowId, frequency, [], [], columnMask));
        }

        if (accumulator is not null)
            Accumulate(accumulator, candidates, columnMask, columnWeights);

        return matches;
    }

    private HashSet<long> EvaluateSqliteNear(
        ManagedFtsNearNode near,
        Dictionary<long, double>? accumulator,
        IReadOnlyList<double> columnWeights,
        uint columnMask)
    {
        var matches = new HashSet<long>();
        var matchedPhraseFrequencies = Enumerable
            .Range(0, near.Phrases.Count)
            .Select(static _ => new Dictionary<long, int[]>())
            .ToArray();
        foreach (var rowId in IntersectPhrases(near.Phrases))
        {
            var nearMatch = FindSqliteNearMatches(
                rowId,
                near.Phrases,
                near.Distance,
                columnMask);
            var mask = GetFrequencyColumnMask(nearMatch.GroupFrequencies);
            if (mask == 0)
                continue;

            matches.Add(rowId);
            for (var phraseIndex = 0; phraseIndex < near.Phrases.Count; phraseIndex++)
            {
                matchedPhraseFrequencies[phraseIndex].Add(
                    rowId,
                    nearMatch.PhraseColumnFrequencies[phraseIndex]);
            }
        }

        if (accumulator is null || matches.Count == 0)
            return matches;

        // SQLite FTS5 scores every phrase in a NEAR group independently. The proximity
        // predicate only limits which rows and columns receive those phrase contributions.
        for (var phraseIndex = 0; phraseIndex < near.Phrases.Count; phraseIndex++)
        {
            var phrase = near.Phrases[phraseIndex];
            EvaluatePhrase(
                new ManagedFtsPhraseNode(
                    phrase.Terms,
                    phrase.IsPrefix,
                    near.Column,
                    AnchoredAtStart: false),
                accumulator,
                columnWeights,
                matchedPhraseFrequencies[phraseIndex]);
        }

        return matches;
    }

    private void RequirePositions(string construct)
    {
        if (_detail != ManagedFtsDetailLevel.Full)
        {
            throw new EmbeddedSqlException(
                $"fts index with detail = '{FormatDetail(_detail)}' does not record positions, so {construct} queries are unavailable");
        }
    }

    /// <summary>The canonical spelling of a detail level, used by diagnostics and the catalog text.</summary>
    public static string FormatDetail(ManagedFtsDetailLevel detail)
        => detail switch
        {
            ManagedFtsDetailLevel.Full => "full",
            ManagedFtsDetailLevel.Columns => "columns",
            ManagedFtsDetailLevel.None => "none",
            _ => throw new ArgumentOutOfRangeException(nameof(detail)),
        };

    /// <summary>Parses a detail level, failing closed on anything unrecognized.</summary>
    public static ManagedFtsDetailLevel ParseDetail(string value)
        => value.ToLowerInvariant() switch
        {
            "full" => ManagedFtsDetailLevel.Full,
            "columns" => ManagedFtsDetailLevel.Columns,
            "none" => ManagedFtsDetailLevel.None,
            _ => throw new EmbeddedSqlException($"unknown fts detail level: {value}"),
        };

    private bool IsLive(in Posting posting)
        => _documents.TryGetValue(posting.RowId, out var document) && document.Generation == posting.Generation;

    private IEnumerable<long> IntersectTerms(IReadOnlyList<string> terms)
    {
        HashSet<long>? candidates = null;
        foreach (var term in terms)
        {
            if (!_postings.TryGetValue(term, out var list))
                return [];

            var rowIds = new HashSet<long>();
            foreach (var posting in list.Entries)
            {
                if (IsLive(posting))
                    rowIds.Add(posting.RowId);
            }

            if (candidates is null)
                candidates = rowIds;
            else
                candidates.IntersectWith(rowIds);

            if (candidates.Count == 0)
                return [];
        }

        return candidates ?? [];
    }

    private IEnumerable<long> IntersectPhrases(IReadOnlyList<ManagedFtsNearPhrase> phrases)
    {
        HashSet<long>? candidates = null;
        foreach (var phrase in phrases)
        {
            var phraseCandidates = FindPhraseCandidates(phrase);
            if (candidates is null)
                candidates = phraseCandidates;
            else
                candidates.IntersectWith(phraseCandidates);

            if (candidates.Count == 0)
                return [];
        }

        return candidates ?? [];
    }

    private HashSet<long> FindPhraseCandidates(ManagedFtsNearPhrase phrase)
    {
        HashSet<long>? candidates = null;
        for (var index = 0; index < phrase.Terms.Count; index++)
        {
            var termRows = new HashSet<long>();
            var isPrefix = phrase.IsPrefix && index == phrase.Terms.Count - 1;
            var terms = isPrefix
                ? ExpandTerm(new ManagedFtsTermNode(
                    phrase.Terms[index],
                    IsPrefix: true,
                    Column: null,
                    AnchoredAtStart: false))
                : [phrase.Terms[index]];
            foreach (var term in terms)
            {
                if (!_postings.TryGetValue(term, out var list))
                    continue;

                foreach (var posting in list.Entries)
                {
                    if (IsLive(posting))
                        termRows.Add(posting.RowId);
                }
            }

            if (candidates is null)
                candidates = termRows;
            else
                candidates.IntersectWith(termRows);

            if (candidates.Count == 0)
                return [];
        }

        return candidates ?? [];
    }

    private List<PhraseOccurrence> GetPhraseOccurrences(
        long rowId,
        ManagedFtsNearPhrase phrase,
        uint columnMask,
        bool anchored)
    {
        if (phrase.Terms.Count == 0)
            return [];

        var streams = new long[phrase.Terms.Count][];
        for (var index = 0; index < phrase.Terms.Count; index++)
        {
            var isPrefix = phrase.IsPrefix && index == phrase.Terms.Count - 1;
            if (isPrefix)
            {
                streams[index] = GetPrefixPositions(phrase.Terms[index], rowId);
                if (streams[index].Length == 0)
                    return [];
            }
            else if (TryGetPositions(phrase.Terms[index], rowId, out var positions))
            {
                streams[index] = positions;
            }
            else
            {
                return [];
            }
        }

        var occurrences = new List<PhraseOccurrence>();
        foreach (var start in streams[0])
        {
            var column = (int)(start >> 32);
            var position = (int)(start & 0xFFFFFFFFL);
            if ((columnMask & (1u << column)) == 0 || (anchored && position != 0))
                continue;

            var matched = true;
            for (var index = 1; index < streams.Length; index++)
            {
                if (Array.BinarySearch(streams[index], start + index) < 0)
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                occurrences.Add(new PhraseOccurrence(
                    column,
                    position,
                    checked(position + streams.Length - 1)));
            }
        }

        return occurrences;
    }

    private long[] GetPrefixPositions(string prefix, long rowId)
    {
        var positions = new List<long>();
        foreach (var expanded in ExpandTerm(new ManagedFtsTermNode(
                     prefix,
                     IsPrefix: true,
                     Column: null,
                     AnchoredAtStart: false)))
        {
            if (TryGetPositions(expanded, rowId, out var expandedPositions))
                positions.AddRange(expandedPositions);
        }

        positions.Sort();
        return positions.ToArray();
    }

    private ScoreCandidate CreateFrequencyCandidate(long rowId, IReadOnlyList<int> frequencies)
    {
        var columnMask = GetFrequencyColumnMask(frequencies);
        var compact = new int[System.Numerics.BitOperations.PopCount(columnMask)];
        var next = 0;
        var total = 0;
        for (var column = 0; column < _columnCount; column++)
        {
            var frequency = frequencies[column];
            total += frequency;
            if (frequency != 0)
                compact[next++] = frequency;
        }

        return new ScoreCandidate(rowId, total, [], compact, columnMask);
    }

    private uint GetFrequencyColumnMask(IReadOnlyList<int> frequencies)
    {
        var mask = 0u;
        for (var column = 0; column < _columnCount; column++)
        {
            if (frequencies[column] != 0)
                mask |= 1u << column;
        }

        return mask;
    }

    private int[] CountPhraseByColumn(
        long rowId,
        IReadOnlyList<string> terms,
        bool isPrefix,
        uint columnMask,
        bool anchored)
    {
        var frequencies = new int[_columnCount];
        foreach (var occurrence in GetPhraseOccurrences(
                     rowId,
                     new ManagedFtsNearPhrase(terms, isPrefix),
                     columnMask,
                     anchored))
        {
            frequencies[occurrence.Column]++;
        }

        return frequencies;
    }

    private int CountNear(long rowId, IReadOnlyList<string> terms, int distance, uint columnMask)
    {
        var streams = new long[terms.Count][];
        for (var index = 0; index < terms.Count; index++)
        {
            if (!TryGetPositions(terms[index], rowId, out var positions))
                return 0;

            streams[index] = positions;
        }

        var count = 0;
        foreach (var anchor in streams[0])
        {
            var column = (int)(anchor >> 32);
            if ((columnMask & (1u << column)) == 0)
                continue;

            var anchorPosition = (int)(anchor & 0xFFFFFFFFL);
            var matched = true;
            for (var index = 1; index < streams.Length; index++)
            {
                if (!HasPositionWithin(streams[index], column, anchorPosition, distance))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
                count++;
        }

        return count;
    }

    private SqliteNearMatch FindSqliteNearMatches(
        long rowId,
        IReadOnlyList<ManagedFtsNearPhrase> phrases,
        int distance,
        uint columnMask)
    {
        var phraseOccurrences = new List<PhraseOccurrence>[phrases.Count];
        for (var index = 0; index < phrases.Count; index++)
        {
            phraseOccurrences[index] = GetPhraseOccurrences(
                rowId,
                phrases[index],
                columnMask,
                anchored: false);
            if (phraseOccurrences[index].Count == 0)
                return new SqliteNearMatch(phrases.Count, _columnCount);
        }

        var result = new SqliteNearMatch(phrases.Count, _columnCount);
        for (var column = 0; column < _columnCount; column++)
        {
            if ((columnMask & (1u << column)) == 0)
                continue;

            var columnOccurrences = new List<PhraseOccurrence>[phrases.Count];
            var maximumStarts = new List<int>();
            for (var phraseIndex = 0; phraseIndex < phraseOccurrences.Length; phraseIndex++)
            {
                columnOccurrences[phraseIndex] = phraseOccurrences[phraseIndex]
                    .Where(occurrence => occurrence.Column == column)
                    .ToList();
                if (columnOccurrences[phraseIndex].Count == 0)
                    goto NextColumn;

                maximumStarts.AddRange(
                    columnOccurrences[phraseIndex].Select(static occurrence => occurrence.StartPosition));
            }

            maximumStarts.Sort();
            var participatingRanges = Enumerable
                .Range(0, phrases.Count)
                .Select(static _ => new List<OccurrenceRange>())
                .ToArray();
            var previousMaximumStart = -1;
            foreach (var maximumStart in maximumStarts)
            {
                if (maximumStart == previousMaximumStart)
                    continue;
                previousMaximumStart = maximumStart;

                var ranges = new OccurrenceRange[phrases.Count];
                var matches = true;
                for (var phraseIndex = 0; phraseIndex < phrases.Count; phraseIndex++)
                {
                    var minimumStart = Math.Max(
                        0L,
                        (long)maximumStart - distance - phrases[phraseIndex].Terms.Count);
                    var occurrences = columnOccurrences[phraseIndex];
                    var first = LowerBoundByStart(occurrences, minimumStart);
                    var end = UpperBoundByStart(occurrences, maximumStart);
                    if (first == end)
                    {
                        matches = false;
                        break;
                    }

                    ranges[phraseIndex] = new OccurrenceRange(first, end);
                }

                if (!matches)
                    continue;

                result.GroupFrequencies[column]++;
                for (var phraseIndex = 0; phraseIndex < phrases.Count; phraseIndex++)
                    AddOccurrenceRange(participatingRanges[phraseIndex], ranges[phraseIndex]);
            }

            for (var phraseIndex = 0; phraseIndex < phrases.Count; phraseIndex++)
            {
                result.PhraseColumnFrequencies[phraseIndex][column] =
                    participatingRanges[phraseIndex].Sum(static range => range.End - range.Start);
            }

        NextColumn:
            continue;
        }

        return result;
    }

    private static int LowerBoundByStart(IReadOnlyList<PhraseOccurrence> occurrences, long target)
    {
        var low = 0;
        var high = occurrences.Count;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (occurrences[middle].StartPosition < target)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    private static int UpperBoundByStart(IReadOnlyList<PhraseOccurrence> occurrences, int target)
    {
        var low = 0;
        var high = occurrences.Count;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (occurrences[middle].StartPosition <= target)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    private static void AddOccurrenceRange(List<OccurrenceRange> ranges, OccurrenceRange next)
    {
        if (ranges.Count == 0 || next.Start > ranges[^1].End)
        {
            ranges.Add(next);
            return;
        }

        var previous = ranges[^1];
        ranges[^1] = new OccurrenceRange(previous.Start, Math.Max(previous.End, next.End));
    }

    private static bool HasPositionWithin(long[] positions, int column, int anchorPosition, int distance)
    {
        var low = EncodePosition(column, Math.Max(anchorPosition - distance, 0));
        var high = EncodePosition(column, anchorPosition + distance);
        var start = LowerBound(positions, low);
        return start < positions.Length && positions[start] <= high;
    }

    private static int LowerBound(long[] values, long target)
    {
        var low = 0;
        var high = values.Length;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (values[middle] < target)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    private bool TryGetPositions(string term, long rowId, out long[] positions)
    {
        if (_postings.TryGetValue(term, out var list)
            && list.TryGet(rowId, out var posting)
            && IsLive(posting))
        {
            positions = posting.Positions;
            return true;
        }

        positions = [];
        return false;
    }

    private IEnumerable<string> ExpandTerm(ManagedFtsTermNode term)
    {
        if (!term.IsPrefix)
        {
            yield return term.Text;
            yield break;
        }

        if (term.Text.Length == 0)
            throw new EmbeddedSqlException("fts prefix term cannot be empty");

        // Stale terms must not consume the expansion budget: a term whose only postings are
        // tombstones is not a live term and could otherwise make a legitimate prefix query fail.
        PurgeStaleTermsForExpansion();

        var sorted = GetSortedTerms();
        var start = Array.BinarySearch(sorted, term.Text, StringComparer.Ordinal);
        if (start < 0)
            start = ~start;

        var produced = 0;
        for (var index = start; index < sorted.Length; index++)
        {
            if (!sorted[index].StartsWith(term.Text, StringComparison.Ordinal))
                yield break;

            if (++produced > ManagedFtsLimits.MaxPrefixTerms)
            {
                throw new EmbeddedSqlException(
                    $"fts prefix term '{term.Text}*' expands to more than {ManagedFtsLimits.MaxPrefixTerms} terms");
            }

            yield return sorted[index];
        }
    }

    /// <summary>
    /// Drops terms whose postings are all tombstoned, so prefix expansion counts live terms only.
    /// This is the same physical purge <see cref="Compact"/> performs; it is run here regardless of
    /// the merge threshold because correctness of the limit, not throughput, is at stake.
    /// </summary>
    private void PurgeStaleTermsForExpansion()
    {
        if (_tombstonedPostings == 0)
            return;

        Compact();
    }

    private string[] GetSortedTerms()
    {
        if (_sortedTerms is { } cached)
            return cached;

        var terms = _postings.Keys.ToArray();
        Array.Sort(terms, StringComparer.Ordinal);
        _sortedTerms = terms;
        return terms;
    }

    private void Accumulate(
        Dictionary<long, double> accumulator,
        List<ScoreCandidate> candidates,
        uint columnMask,
        IReadOnlyList<double> columnWeights,
        IReadOnlyDictionary<long, int[]>? includedRowFrequencies = null)
    {
        if (candidates.Count == 0)
            return;

        var documentCount = _documents.Count;
        var idf = _scoringProfile == ManagedFtsScoringProfile.SqliteFts5
            ? Math.Max(
                0.000001,
                Math.Log((documentCount - candidates.Count + 0.5) / (candidates.Count + 0.5)))
            : Math.Log(1.0 + ((documentCount - candidates.Count + 0.5) / (candidates.Count + 0.5)));
        foreach (var originalCandidate in candidates)
        {
            if (!_documents.TryGetValue(originalCandidate.RowId, out var document))
                continue;

            var candidate = originalCandidate;
            var effectiveColumnMask = columnMask;
            if (includedRowFrequencies is not null)
            {
                if (!includedRowFrequencies.TryGetValue(candidate.RowId, out var frequencies))
                    continue;

                candidate = CreateFrequencyCandidate(candidate.RowId, frequencies);
                effectiveColumnMask &= candidate.PostingColumnMask;
                if (effectiveColumnMask == 0)
                    continue;
            }

            double score;
            if (_scoringProfile == ManagedFtsScoringProfile.SqliteFts5)
                score = ScoreSqliteFts5(
                    document,
                    candidate,
                    idf,
                    effectiveColumnMask,
                    columnWeights);
            else if (candidate.Positions.Length != 0)
            {
                score = ScorePerColumn(
                    document,
                    candidate.Positions,
                    idf,
                    effectiveColumnMask,
                    columnWeights);
            }
            else if (candidate.ColumnFrequencies.Length != 0)
            {
                score = ScorePerColumnFrequencies(
                    document,
                    candidate,
                    idf,
                    effectiveColumnMask,
                    columnWeights);
            }
            else
            {
                score = ScoreUniform(
                    document,
                    candidate.Frequency,
                    idf,
                    effectiveColumnMask,
                    columnWeights);
            }

            accumulator[candidate.RowId] = accumulator.TryGetValue(candidate.RowId, out var existing)
                ? existing + score
                : score;
        }
    }

    private double ScoreSqliteFts5(
        Document document,
        in ScoreCandidate candidate,
        double idf,
        uint columnMask,
        IReadOnlyList<double> columnWeights)
    {
        double weightedFrequency;
        if (candidate.Positions.Length != 0)
        {
            Span<int> perColumn = stackalloc int[_columnCount];
            foreach (var encoded in candidate.Positions)
            {
                var column = (int)(encoded >> 32);
                if ((columnMask & (1u << column)) != 0)
                    perColumn[column]++;
            }

            weightedFrequency = 0.0;
            for (var column = 0; column < _columnCount; column++)
                weightedFrequency += perColumn[column] * columnWeights[column];
        }
        else if (candidate.ColumnFrequencies.Length != 0)
        {
            weightedFrequency = 0.0;
            var next = 0;
            for (var column = 0; column < _columnCount; column++)
            {
                var bit = 1u << column;
                if ((candidate.PostingColumnMask & bit) == 0)
                    continue;

                var frequency = candidate.ColumnFrequencies[next++];
                if ((columnMask & bit) != 0)
                    weightedFrequency += frequency * columnWeights[column];
            }
        }
        else
        {
            // detail=none has no column attribution. Preserve its aggregate-frequency behavior;
            // phrase and NEAR candidates require detail=full and carry exact per-column counts.
            var weight = 0.0;
            for (var column = 0; column < _columnCount; column++)
            {
                if ((columnMask & (1u << column)) != 0)
                    weight = Math.Max(weight, columnWeights[column]);
            }

            weightedFrequency = candidate.Frequency * weight;
        }

        if (weightedFrequency <= 0.0)
            return 0.0;

        var documentLength = document.ColumnLengths.Sum();
        var totalTokens = 0L;
        foreach (var total in _columnTokenTotals)
            totalTokens += total;
        var averageLength = _documents.Count == 0 ? 0.0 : (double)totalTokens / _documents.Count;
        var normalization = averageLength <= 0.0
            ? 1.0
            : 1.0 - BM25B + (BM25B * documentLength / averageLength);
        return idf
            * weightedFrequency
            * (BM25K1 + 1.0)
            / (weightedFrequency + (BM25K1 * normalization));
    }

    /// <summary>
    /// Per-column BM25 for a <c>detail = 'columns'</c> posting, which knows how often the term
    /// occurs in each column but not where.
    /// </summary>
    /// <remarks>
    /// This is the same sum as <see cref="ScorePerColumn"/>: every selected column contributes its
    /// own weighted, length-normalized saturation. Falling back to the single-column attribution
    /// <see cref="ScoreUniform"/> uses would charge one column for occurrences that belong to
    /// several, which makes a column-weighted ranking wrong rather than merely approximate.
    /// </remarks>
    private double ScorePerColumnFrequencies(
        Document document,
        in ScoreCandidate candidate,
        double idf,
        uint columnMask,
        IReadOnlyList<double> columnWeights)
    {
        var score = 0.0;
        var next = 0;
        for (var column = 0; column < _columnCount; column++)
        {
            var bit = 1u << column;
            if ((candidate.PostingColumnMask & bit) == 0)
                continue;

            var frequency = candidate.ColumnFrequencies[next++];
            if ((columnMask & bit) == 0 || frequency == 0)
                continue;

            score += columnWeights[column] * idf * Saturate(frequency, document.ColumnLengths[column], column);
        }

        return score;
    }

    private double ScorePerColumn(
        Document document,
        long[] positions,
        double idf,
        uint columnMask,
        IReadOnlyList<double> columnWeights)
    {
        Span<int> perColumn = stackalloc int[_columnCount];
        foreach (var encoded in positions)
        {
            var column = (int)(encoded >> 32);
            if ((columnMask & (1u << column)) != 0)
                perColumn[column]++;
        }

        var score = 0.0;
        for (var column = 0; column < _columnCount; column++)
        {
            if (perColumn[column] == 0)
                continue;

            score += columnWeights[column] * idf * Saturate(perColumn[column], document.ColumnLengths[column], column);
        }

        return score;
    }

    private double ScoreUniform(
        Document document,
        int frequency,
        double idf,
        uint columnMask,
        IReadOnlyList<double> columnWeights)
    {
        // Phrase and NEAR match a contiguous window, so their frequency is not attributable to a
        // single column stream. Attribute it to the heaviest selected column, which keeps the score
        // deterministic and monotone in the configured weights.
        var bestWeight = 0.0;
        var bestColumn = -1;
        for (var column = 0; column < _columnCount; column++)
        {
            if ((columnMask & (1u << column)) == 0 || document.ColumnLengths[column] == 0)
                continue;

            if (bestColumn < 0 || columnWeights[column] > bestWeight)
            {
                bestWeight = columnWeights[column];
                bestColumn = column;
            }
        }

        return bestColumn < 0
            ? 0.0
            : bestWeight * idf * Saturate(frequency, document.ColumnLengths[bestColumn], bestColumn);
    }

    private double Saturate(int frequency, int documentLength, int column)
    {
        // columnsize = 0 disables length normalization outright, which is what SQLite's FTS5
        // columnsize option means: the index stops paying for per-column lengths and BM25 degrades
        // to the unnormalized saturation curve.
        if (!_columnSize)
            return frequency * (BM25K1 + 1.0) / (frequency + BM25K1);

        var averageLength = _documents.Count == 0
            ? 0.0
            : (double)_columnTokenTotals[column] / _documents.Count;
        var normalization = averageLength <= 0.0
            ? 1.0
            : 1.0 - BM25B + (BM25B * documentLength / averageLength);
        return frequency * (BM25K1 + 1.0) / (frequency + (BM25K1 * normalization));
    }

    private uint ResolveColumnMask(string? column)
    {
        if (column is null)
            return _columnCount >= 32 ? uint.MaxValue : (1u << _columnCount) - 1;

        if (_detail == ManagedFtsDetailLevel.None)
        {
            throw new EmbeddedSqlException(
                "fts index with detail = 'none' does not record column attribution, so column filters are unavailable");
        }

        var index = ColumnIndexResolver?.Invoke(column)
            ?? throw new EmbeddedSqlException($"no such fts column: {column}");
        if (index < 0 || index >= _columnCount)
            throw new EmbeddedSqlException($"no such fts column: {column}");

        return 1u << index;
    }

    /// <summary>Maps a column name to its position in this index, supplied by the owning attachment.</summary>
    public Func<string, int?>? ColumnIndexResolver { get; set; }

    private static int CountInColumns(in Posting posting, uint columnMask)
    {
        if ((posting.ColumnMask & ~columnMask) == 0)
            return posting.Frequency;

        if (posting.Positions.Length != 0)
        {
            var count = 0;
            foreach (var encoded in posting.Positions)
            {
                if ((columnMask & (1u << (int)(encoded >> 32))) != 0)
                    count++;
            }

            return count;
        }

        if (posting.ColumnFrequencies.Length != 0)
        {
            // detail = 'columns': positions are not recorded, but the per-column counts are, so a
            // term that occurs in both a selected and an unselected column still reports exactly
            // its occurrences in the selected ones instead of collapsing to "no match".
            var count = 0;
            var next = 0;
            for (var column = 0; column < 32; column++)
            {
                var bit = 1u << column;
                if ((posting.ColumnMask & bit) == 0)
                    continue;
                if ((columnMask & bit) != 0)
                    count += posting.ColumnFrequencies[next];
                next++;
            }

            return count;
        }

        // Reachable only for an index that records no column attribution at all, which
        // ResolveColumnMask already refuses to build a partial mask for. Fail closed rather than
        // guess a frequency.
        throw new EmbeddedSqlException(
            "fts index does not record column attribution, so a column-filtered term cannot be scored");
    }

    private static int CountAnchored(long[] positions, uint columnMask)
    {
        var count = 0;
        foreach (var encoded in positions)
        {
            if ((encoded & 0xFFFFFFFFL) == 0 && (columnMask & (1u << (int)(encoded >> 32))) != 0)
                count++;
        }

        return count;
    }

    private HashSet<long> Intersect(HashSet<long> left, HashSet<long> right)
    {
        left.IntersectWith(right);
        return left;
    }

    private HashSet<long> Union(HashSet<long> left, HashSet<long> right)
    {
        left.UnionWith(right);
        return left;
    }

    private HashSet<long> Exclude(HashSet<long> excluded)
    {
        var result = new HashSet<long>(_documents.Keys);
        result.ExceptWith(excluded);
        return result;
    }

    internal static long EncodePosition(int column, int position)
        => ((long)column << 32) | (uint)position;

    internal static string ReadText(SqlValue value)
        => value.Kind switch
        {
            SqlValueKind.Text => value.AsText(),
            SqlValueKind.Integer => value.AsInteger().ToString(CultureInfo.InvariantCulture),
            SqlValueKind.Real => value.AsReal().ToString("R", CultureInfo.InvariantCulture),
            SqlValueKind.Blob => Encoding.UTF8.GetString(value.AsBlob().Span),
            _ => string.Empty,
        };

    private sealed record Document(
        long RowId,
        SqlValue[] Source,
        int[] ColumnLengths,
        int PostingCount,
        long Generation);

    private sealed record StagedDocument(int[] ColumnLengths, Dictionary<string, TermOccurrence> Terms);

    private sealed class TermOccurrence
    {
        public int Frequency;
        public uint ColumnMask;
        public List<long> Positions { get; } = [];

        /// <summary>Per-column occurrence counts, indexed by column ordinal.</summary>
        public int[] ColumnFrequencies = [];
    }

    /// <summary>
    /// One document's entry in a term's posting list.
    /// </summary>
    /// <remarks>
    /// <c>ColumnFrequencies</c> holds compact per-column occurrence counts for
    /// <c>detail = 'columns'</c>: one entry per column set in <c>ColumnMask</c>, in ascending column
    /// order. It is empty for the other detail levels, where positions carry the same information
    /// (<c>full</c>) or no column-specific question can be asked (<c>none</c>).
    /// </remarks>
    private readonly record struct Posting(
        long RowId,
        long Generation,
        int Frequency,
        uint ColumnMask,
        long[] Positions,
        int[] ColumnFrequencies);

    private readonly record struct PhraseOccurrence(int Column, int StartPosition, int EndPosition);

    private readonly record struct OccurrenceRange(int Start, int End);

    private sealed class SqliteNearMatch(int phraseCount, int columnCount)
    {
        public int[] GroupFrequencies { get; } = new int[columnCount];

        public int[][] PhraseColumnFrequencies { get; } = Enumerable
            .Range(0, phraseCount)
            .Select(_ => new int[columnCount])
            .ToArray();
    }

    /// <summary>One document's contribution to a term's score, before BM25 is applied.</summary>
    private readonly record struct ScoreCandidate(
        long RowId,
        int Frequency,
        long[] Positions,
        int[] ColumnFrequencies,
        uint PostingColumnMask);

    private sealed class PrefixScoreCandidate(int columnCount)
    {
        public int Frequency;
        public uint ColumnMask;
        public List<long> Positions { get; } = [];
        public int[] ColumnFrequencies { get; } = new int[columnCount];
    }

    private sealed class PostingList
    {
        private readonly List<Posting> _entries = [];
        private readonly Dictionary<long, int> _byRowId = [];

        public IReadOnlyList<Posting> Entries => _entries;

        public int Count => _entries.Count;

        public void Add(in Posting posting)
        {
            _byRowId[posting.RowId] = _entries.Count;
            _entries.Add(posting);
        }

        public bool TryGet(long rowId, out Posting posting)
        {
            if (_byRowId.TryGetValue(rowId, out var index))
            {
                posting = _entries[index];
                return true;
            }

            posting = default;
            return false;
        }

        public int Purge(Dictionary<long, Document> liveDocuments)
        {
            var removed = 0;
            var write = 0;
            for (var read = 0; read < _entries.Count; read++)
            {
                var entry = _entries[read];
                if (!liveDocuments.TryGetValue(entry.RowId, out var document)
                    || document.Generation != entry.Generation)
                {
                    removed++;
                    continue;
                }

                _entries[write++] = entry;
            }

            if (removed == 0)
                return 0;

            _entries.RemoveRange(write, _entries.Count - write);
            _byRowId.Clear();
            for (var index = 0; index < _entries.Count; index++)
                _byRowId[_entries[index].RowId] = index;

            return removed;
        }
    }
}
