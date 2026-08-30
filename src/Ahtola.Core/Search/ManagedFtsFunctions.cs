using System.Text;

namespace Ahtola.Core.Search;

/// <summary>
/// The configuration a scalar <c>fts_*</c> call inherits from the method index that covers its
/// columns, so index and scalar evaluation can never disagree about tokenization or about which
/// query constructs the index is able to answer.
/// </summary>
internal sealed record ManagedFtsScalarOptions(
    ManagedFtsTokenizerOptions Tokenizer,
    ManagedFtsDetailLevel Detail = ManagedFtsDetailLevel.Full,
    bool ColumnSize = true,
    IReadOnlyList<ManagedFtsTokenizerOptions>? ColumnTokenizers = null)
{
    public static ManagedFtsScalarOptions Default { get; } = new(ManagedFtsTokenizerOptions.Default);

    public IReadOnlyList<ManagedFtsTokenizerOptions> ResolveColumnTokenizers(int columnCount)
    {
        if (ColumnTokenizers is { } configured)
        {
            if (configured.Count != columnCount)
                throw new ArgumentException("One tokenizer per FTS column is required.");
            return configured;
        }

        return Enumerable.Repeat(Tokenizer, columnCount).ToArray();
    }
}

/// <summary>
/// The <c>fts_*</c> SQL surface. <c>fts_match</c>, <c>fts_highlight</c> and <c>fts_snippet</c> are
/// pure scalar functions in Turso's registry. A planned <c>fts_score</c> may additionally read the
/// corpus statistics of its unambiguous covering method index.
/// </summary>
internal static class ManagedFtsFunctions
{
    /// <summary>
    /// <c>fts_match(col…, query)</c>: true when the row's indexed text satisfies the query.
    /// Evaluated row-locally so the answer never depends on which access path the planner chose.
    /// </summary>
    public static SqlValue Match(
        IReadOnlyList<SqlValue> arguments,
        IReadOnlyList<string?> columnNames,
        ManagedFtsScalarOptions? options = null)
    {
        var (columns, query) = Split("fts_match", arguments);
        if (query is null)
            return SqlValue.Integer(0);

        var resolved = options ?? ManagedFtsScalarOptions.Default;
        var index = BuildSingleDocumentIndex(columns, columnNames, resolved);
        var node = ParseMethodQuery(query, columnNames, resolved);
        return SqlValue.Integer(index.Matches(node, 0) ? 1 : 0);
    }

    /// <summary>
    /// <c>fts_score(col…, query)</c> evaluated without a bound index: Turso's REAL <c>0.0</c>
    /// fallback. The corpus-aware form is produced by the engine when a method index covers the call.
    /// </summary>
    public static SqlValue Score(
        IReadOnlyList<SqlValue> arguments,
        IReadOnlyList<string?> columnNames,
        ManagedFtsScalarOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        _ = columnNames;
        _ = options;
        return SqlValue.Real(0.0);
    }

    /// <summary>
    /// <c>fts_highlight(text..., before, after, query)</c>: wraps every matching token occurrence.
    /// Offsets come from the tokenizer, so the untouched source text is reproduced exactly.
    /// </summary>
    public static SqlValue Highlight(
        IReadOnlyList<SqlValue> arguments,
        ManagedFtsTokenizerOptions? tokenizer = null)
    {
        if (arguments.Count < 4)
            throw new EmbeddedSqlException("wrong number of arguments to function fts_highlight()");
        var textCount = arguments.Count - 3;
        if (arguments[textCount].Kind == SqlValueKind.Null
            || arguments[textCount + 1].Kind == SqlValueKind.Null
            || arguments[textCount + 2].Kind == SqlValueKind.Null)
        {
            return SqlValue.Null;
        }

        var combined = new StringBuilder();
        for (var index = 0; index < textCount; index++)
        {
            if (arguments[index].Kind == SqlValueKind.Null)
                continue;
            if (combined.Length > 0)
                combined.Append(' ');
            combined.Append(ManagedFtsSearchIndex.ReadText(arguments[index]));
        }

        var text = combined.ToString();
        var before = ManagedFtsSearchIndex.ReadText(arguments[textCount]);
        var after = ManagedFtsSearchIndex.ReadText(arguments[textCount + 1]);
        var query = ManagedFtsSearchIndex.ReadText(arguments[textCount + 2]);
        if (text.Length == 0 || query.Length == 0)
            return SqlValue.Text(text);

        var options = tokenizer ?? ManagedFtsTokenizerOptions.Default;
        var tokens = ManagedFtsTokenization.Tokenize(text, options);
        var spans = CollectMatchedSpans(tokens, query, options);
        if (spans.Count == 0)
            return SqlValue.Text(text);

        var builder = new StringBuilder(text.Length + (spans.Count * (before.Length + after.Length)));
        AppendRange(builder, text, 0, text.Length, spans, before, after);
        return SqlValue.Text(builder.ToString());
    }

    /// <summary>Compatibility spelling for Ahtola's former four-argument ordering.</summary>
    public static SqlValue HighlightLegacy(
        IReadOnlyList<SqlValue> arguments,
        ManagedFtsTokenizerOptions? tokenizer = null)
    {
        if (arguments.Count != 4)
            throw new EmbeddedSqlException("wrong number of arguments to function fts_highlight_legacy()");

        return Highlight(
        [
            arguments[0],
            arguments[2],
            arguments[3],
            arguments[1],
        ], tokenizer);
    }

    internal static SqlValue HighlightFts5(
        SqlValue value,
        ManagedFtsNode? query,
        string columnName,
        string before,
        string after,
        ManagedFtsTokenizerOptions tokenizer)
    {
        if (value.Kind == SqlValueKind.Null)
            return SqlValue.Null;

        var text = ManagedFtsSearchIndex.ReadText(value);
        if (query is null)
            return SqlValue.Text(text);

        var tokens = ManagedFtsTokenization.Tokenize(text, tokenizer);
        var spans = CollectMatchedSpans(tokens, query, columnName);
        if (spans.Count == 0)
            return SqlValue.Text(text);

        var builder = new StringBuilder(text.Length + (spans.Count * (before.Length + after.Length)));
        AppendRange(builder, text, 0, text.Length, spans, before, after);
        return SqlValue.Text(builder.ToString());
    }

    /// <summary>
    /// <c>fts_snippet(text, query, before, after, ellipsis, tokens)</c>: the densest window of
    /// <c>tokens</c> tokens containing query matches, with matches wrapped.
    /// </summary>
    public static SqlValue Snippet(
        IReadOnlyList<SqlValue> arguments,
        ManagedFtsTokenizerOptions? tokenizer = null)
    {
        if (arguments.Count != 6)
            throw new EmbeddedSqlException("wrong number of arguments to function fts_snippet()");
        if (arguments[0].Kind == SqlValueKind.Null || arguments[1].Kind == SqlValueKind.Null)
            return SqlValue.Null;

        var text = ManagedFtsSearchIndex.ReadText(arguments[0]);
        var query = ManagedFtsSearchIndex.ReadText(arguments[1]);
        var before = ManagedFtsSearchIndex.ReadText(arguments[2]);
        var after = ManagedFtsSearchIndex.ReadText(arguments[3]);
        var ellipsis = ManagedFtsSearchIndex.ReadText(arguments[4]);
        var window = arguments[5].Kind == SqlValueKind.Integer ? (int)arguments[5].AsInteger() : 15;
        if (window <= 0 || window > 4096)
            throw new EmbeddedSqlException("fts_snippet() token count must be between 1 and 4096");

        var options = tokenizer ?? ManagedFtsTokenizerOptions.Default;
        var tokens = ManagedFtsTokenization.Tokenize(text, options);
        if (tokens.Count == 0)
            return SqlValue.Text(text);

        var spans = CollectMatchedSpans(tokens, query, options);
        var matchedPositions = MarkMatchedTokenPositions(tokens, spans);

        var start = SelectWindow(tokens, window, matchedPositions).Start;
        var end = Math.Min(start + window, tokens.Count);

        // Clip to the source span the window covers. When the window reaches an edge of the
        // document, extend to that edge so a snippet that covers everything reproduces the source
        // exactly instead of dropping the text outside the first and last token.
        var rangeStart = start == 0 ? 0 : tokens[start].Offset;
        var rangeEnd = end >= tokens.Count ? text.Length : MaxEnd(tokens, start, end);

        var builder = new StringBuilder();
        if (rangeStart > 0)
            builder.Append(ellipsis);

        AppendRange(builder, text, rangeStart, rangeEnd, spans, before, after);

        if (rangeEnd < text.Length)
            builder.Append(ellipsis);

        return SqlValue.Text(builder.ToString());
    }

    internal static SqlValue SnippetFts5(
        SqlValue value,
        ManagedFtsNode? query,
        string columnName,
        string before,
        string after,
        string ellipsis,
        int window,
        ManagedFtsTokenizerOptions tokenizer)
    {
        if (value.Kind == SqlValueKind.Null)
            return SqlValue.Null;
        if (window <= 0 || window > 4096)
            throw new EmbeddedSqlException("snippet() token count must be between 1 and 4096");

        var text = ManagedFtsSearchIndex.ReadText(value);
        var tokens = ManagedFtsTokenization.Tokenize(text, tokenizer);
        if (tokens.Count == 0 || query is null)
            return SqlValue.Text(text);

        var spans = CollectMatchedSpans(tokens, query, columnName);
        var matchedPositions = MarkMatchedTokenPositions(tokens, spans);
        var start = SelectWindow(tokens, window, matchedPositions).Start;
        var end = Math.Min(start + window, tokens.Count);
        var rangeStart = start == 0 ? 0 : tokens[start].Offset;
        var rangeEnd = end >= tokens.Count ? text.Length : MaxEnd(tokens, start, end);

        var builder = new StringBuilder();
        if (rangeStart > 0)
            builder.Append(ellipsis);
        AppendRange(builder, text, rangeStart, rangeEnd, spans, before, after);
        if (rangeEnd < text.Length)
            builder.Append(ellipsis);
        return SqlValue.Text(builder.ToString());
    }

    internal static int ScoreFts5Snippet(
        SqlValue value,
        ManagedFtsNode query,
        string columnName,
        int window,
        ManagedFtsTokenizerOptions tokenizer)
    {
        if (value.Kind == SqlValueKind.Null)
            return 0;

        var text = ManagedFtsSearchIndex.ReadText(value);
        var tokens = ManagedFtsTokenization.Tokenize(text, tokenizer);
        var spans = CollectMatchedSpans(tokens, query, columnName);
        return SelectWindow(tokens, window, MarkMatchedTokenPositions(tokens, spans)).Count;
    }

    /// <summary>
    /// Marks the token positions covered by a matched span, in one forward sweep.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The spans are sorted, merged and therefore disjoint, so a token can only ever be inside the
    /// first span whose end is past that token's offset: one cursor over the spans is enough, and
    /// the previous token × span cross product — quadratic on a document made of one repeated term,
    /// where nearly every token is also a span — becomes a linear two-pointer sweep.
    /// </para>
    /// <para>
    /// The cursor is rewound whenever the token offsets go backwards. A gram tokenizer emits one
    /// full pass per gram size, each restarting at the beginning of the document, so "ascending"
    /// holds within a pass rather than across the whole list. The number of passes is a small
    /// constant (<c>max_gram - min_gram + 1</c>), so the sweep stays linear.
    /// </para>
    /// </remarks>
    private static HashSet<int> MarkMatchedTokenPositions(
        IReadOnlyList<ManagedFtsToken> tokens,
        List<MatchSpan> spans)
    {
        var matchedPositions = new HashSet<int>();
        if (spans.Count == 0)
            return matchedPositions;

        var spanIndex = 0;
        var previousOffset = -1;
        foreach (var token in tokens)
        {
            if (token.Offset < previousOffset)
                spanIndex = 0;
            previousOffset = token.Offset;

            while (spanIndex < spans.Count && spans[spanIndex].End <= token.Offset)
                spanIndex++;
            if (spanIndex == spans.Count)
                continue;

            var span = spans[spanIndex];
            if (token.Offset >= span.Start && token.Offset + token.Length <= span.End)
                matchedPositions.Add(token.Position);
        }

        return matchedPositions;
    }

    private static int MaxEnd(IReadOnlyList<ManagedFtsToken> tokens, int start, int end)
    {
        var maximum = tokens[start].Offset;
        for (var index = start; index < end; index++)
            maximum = Math.Max(maximum, tokens[index].Offset + tokens[index].Length);

        return maximum;
    }

    /// <summary>
    /// Emits <paramref name="text"/> between two source offsets, wrapping every matched span that
    /// overlaps the range. Spans are pre-merged, so overlapping n-grams produce one wrapper instead
    /// of an interleaved mess, and no source character is ever duplicated or dropped.
    /// </summary>
    private static void AppendRange(
        StringBuilder builder,
        string text,
        int rangeStart,
        int rangeEnd,
        List<MatchSpan> spans,
        string before,
        string after)
    {
        var cursor = rangeStart;
        foreach (var span in spans)
        {
            var start = Math.Max(span.Start, rangeStart);
            var end = Math.Min(span.End, rangeEnd);
            if (end <= start || start < cursor)
                continue;

            builder.Append(text, cursor, start - cursor);
            builder.Append(before);
            builder.Append(text, start, end - start);
            builder.Append(after);
            cursor = end;
        }

        if (cursor < rangeEnd)
            builder.Append(text, cursor, rangeEnd - cursor);
    }

    /// <summary>
    /// Finds the densest window of <paramref name="window"/> tokens, in one pass.
    /// </summary>
    /// <remarks>
    /// The count slides: each step drops the token leaving the window and adds the one entering it,
    /// so the whole scan is linear in the token count instead of recounting the window at every
    /// start. Ties still resolve to the earliest window, exactly as the recount did.
    /// </remarks>
    private static (int Start, int Count) SelectWindow(
        IReadOnlyList<ManagedFtsToken> tokens,
        int window,
        HashSet<int> matchedPositions)
    {
        if (matchedPositions.Count == 0 || tokens.Count == 0)
            return (0, 0);

        var count = 0;
        var initialEnd = Math.Min(window, tokens.Count);
        for (var index = 0; index < initialEnd; index++)
        {
            if (matchedPositions.Contains(tokens[index].Position))
                count++;
        }
        if (tokens.Count <= window)
            return (0, count);

        var bestStart = 0;
        var bestCount = count;
        for (var start = 1; start + window <= tokens.Count; start++)
        {
            if (matchedPositions.Contains(tokens[start - 1].Position))
                count--;
            if (matchedPositions.Contains(tokens[start + window - 1].Position))
                count++;

            if (count > bestCount)
            {
                bestCount = count;
                bestStart = start;
            }
        }

        return (bestStart, bestCount);
    }

    /// <summary>A merged source span covered by at least one matching token.</summary>
    private readonly record struct MatchSpan(int Start, int End);

    private static List<MatchSpan> CollectMatchedSpans(
        IReadOnlyList<ManagedFtsToken> tokens,
        string query,
        ManagedFtsTokenizerOptions options)
    {
        var node = ManagedFtsQueryLanguage.Parse(
            query,
            options,
            static _ => true,
            ManagedFtsQuerySyntax.TursoMethod);
        return CollectMatchedSpans(tokens, node, columnName: null);
    }

    private static List<MatchSpan> CollectMatchedSpans(
        IReadOnlyList<ManagedFtsToken> tokens,
        ManagedFtsNode node,
        string? columnName)
    {
        var matched = new List<MatchSpan>();
        var lookup = new Fts5TokenLookup(tokens);
        var prefixTerms = 0;
        CollectMatchedSpans(tokens, lookup, node, columnName, matched, ref prefixTerms);

        if (matched.Count <= 1)
            return matched;

        matched.Sort(static (left, right)
            => left.Start == right.Start ? left.End.CompareTo(right.End) : left.Start.CompareTo(right.Start));

        // Overlapping matches (every gram of a gram tokenizer overlaps its neighbours) have to be
        // merged before any text is emitted, or the wrappers would nest and the source characters
        // between them would be written more than once.
        var merged = new List<MatchSpan> { matched[0] };
        for (var index = 1; index < matched.Count; index++)
        {
            var current = matched[index];
            var last = merged[^1];
            if (current.Start <= last.End)
                merged[^1] = new MatchSpan(last.Start, Math.Max(last.End, current.End));
            else
                merged.Add(current);
        }

        return merged;
    }

    private static void CollectMatchedSpans(
        IReadOnlyList<ManagedFtsToken> tokens,
        Fts5TokenLookup lookup,
        ManagedFtsNode node,
        string? columnName,
        List<MatchSpan> destination,
        ref int prefixTerms)
    {
        switch (node)
        {
            case ManagedFtsNoMatchNode:
                return;
            case ManagedFtsTermNode term:
                if (!AppliesToColumn(term.Column, columnName))
                    return;
                if (term.IsPrefix && ++prefixTerms > ManagedFtsLimits.MaxHighlightPrefixTerms)
                {
                    throw new EmbeddedSqlException(
                        $"fts highlight query uses more than {ManagedFtsLimits.MaxHighlightPrefixTerms} prefix terms");
                }

                var occurrences = term.IsPrefix
                    ? tokens.Where(token => token.Text.StartsWith(term.Text, StringComparison.Ordinal))
                    : lookup.Get(term.Text);
                foreach (var token in occurrences)
                {
                    if (!term.AnchoredAtStart || token.Position == 0)
                        AddMatchSpan(destination, token.Offset, token.Offset + token.Length);
                }
                return;
            case ManagedFtsPhraseNode phrase:
                prefixTerms += phrase.Terms.Count(static term => term.IsPrefix);
                if (prefixTerms > ManagedFtsLimits.MaxHighlightPrefixTerms)
                {
                    throw new EmbeddedSqlException(
                        $"fts highlight query uses more than {ManagedFtsLimits.MaxHighlightPrefixTerms} prefix terms");
                }
                if (AppliesToColumn(phrase.Column, columnName))
                    CollectPhraseSpans(lookup, phrase, destination);
                return;
            case ManagedFtsNearNode near:
                foreach (var phrase in near.Phrases)
                {
                    prefixTerms += phrase.Terms.Count(static term => term.IsPrefix);
                    if (prefixTerms > ManagedFtsLimits.MaxHighlightPrefixTerms)
                    {
                        throw new EmbeddedSqlException(
                            $"fts highlight query uses more than {ManagedFtsLimits.MaxHighlightPrefixTerms} prefix terms");
                    }
                }
                if (AppliesToColumn(near.Column, columnName))
                    CollectNearSpans(lookup, near, destination);
                return;
            case ManagedFtsAndNode and:
                CollectMatchedSpans(tokens, lookup, and.Left, columnName, destination, ref prefixTerms);
                CollectMatchedSpans(tokens, lookup, and.Right, columnName, destination, ref prefixTerms);
                return;
            case ManagedFtsOrNode or:
                CollectMatchedSpans(tokens, lookup, or.Left, columnName, destination, ref prefixTerms);
                CollectMatchedSpans(tokens, lookup, or.Right, columnName, destination, ref prefixTerms);
                return;
            case ManagedFtsNotNode:
                return;
            case ManagedFtsBoostNode boost:
                CollectMatchedSpans(tokens, lookup, boost.Operand, columnName, destination, ref prefixTerms);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(node));
        }
    }

    private static void CollectPhraseSpans(
        Fts5TokenLookup lookup,
        ManagedFtsPhraseNode phrase,
        List<MatchSpan> destination)
    {
        if (phrase.Terms.Count == 0)
            return;

        foreach (var match in FindPhraseMatches(
                     lookup,
                     new ManagedFtsNearPhrase(phrase.Terms),
                     phrase.AnchoredAtStart))
        {
            AddMatchSpan(destination, match.Span.Start, match.Span.End);
        }
    }

    private static List<PhraseMatch> FindPhraseMatches(
        Fts5TokenLookup lookup,
        ManagedFtsNearPhrase phrase,
        bool anchoredAtStart)
    {
        if (phrase.Terms.Count == 0)
            return [];

        var matches = new List<PhraseMatch>();
        var firstTerm = phrase.Terms[0];
        var firstOccurrences = firstTerm.IsPrefix
            ? lookup.GetPrefix(firstTerm.Text)
            : lookup.Get(firstTerm.Text);
        foreach (var first in firstOccurrences)
        {
            if (anchoredAtStart && first.Position != 0)
                continue;

            var end = first.Offset + first.Length;
            var matched = true;
            for (var index = 1; index < phrase.Terms.Count; index++)
            {
                var position = first.Position + index;
                var phraseTerm = phrase.Terms[index];
                var found = phraseTerm.IsPrefix
                    ? lookup.TryGetPrefixAtPosition(phraseTerm.Text, position, out var token)
                    : lookup.TryGetAtPosition(phraseTerm.Text, position, out token);
                if (!found)
                {
                    matched = false;
                    break;
                }

                end = Math.Max(end, token.Offset + token.Length);
            }

            if (matched)
            {
                matches.Add(new PhraseMatch(
                    first.Position,
                    checked(first.Position + phrase.Terms.Count - 1),
                    new MatchSpan(first.Offset, end)));
            }
        }

        return matches;
    }

    private static void CollectNearSpans(
        Fts5TokenLookup lookup,
        ManagedFtsNearNode near,
        List<MatchSpan> destination)
    {
        if (near.Phrases.Count == 0)
            return;

        var phraseMatches = near.Phrases
            .Select(phrase => FindPhraseMatches(lookup, phrase, anchoredAtStart: false))
            .ToArray();
        if (phraseMatches.Any(static matches => matches.Count == 0))
            return;

        if (!near.SqliteDistance)
        {
            foreach (var anchor in phraseMatches[0])
            {
                var occurrences = new PhraseMatch[near.Phrases.Count];
                occurrences[0] = anchor;
                var matched = true;
                for (var index = 1; index < phraseMatches.Length; index++)
                {
                    var found = phraseMatches[index].FirstOrDefault(
                        candidate => Math.Abs(candidate.EndPosition - anchor.EndPosition) <= near.Distance);
                    if (found == default)
                    {
                        matched = false;
                        break;
                    }

                    occurrences[index] = found;
                }

                if (!matched)
                    continue;
                foreach (var occurrence in occurrences)
                    AddMatchSpan(destination, occurrence.Span.Start, occurrence.Span.End);
            }

            return;
        }

        var maximumStarts = phraseMatches
            .SelectMany(static matches => matches)
            .Select(static match => match.StartPosition)
            .Distinct()
            .Order()
            .ToArray();
        var participatingRanges = Enumerable
            .Range(0, phraseMatches.Length)
            .Select(static _ => new List<PhraseMatchRange>())
            .ToArray();
        foreach (var maximumStart in maximumStarts)
        {
            var ranges = new PhraseMatchRange[phraseMatches.Length];
            var matches = true;
            for (var phraseIndex = 0; phraseIndex < phraseMatches.Length; phraseIndex++)
            {
                var minimumStart = Math.Max(
                    0L,
                    (long)maximumStart - near.Distance - near.Phrases[phraseIndex].Terms.Count);
                var first = LowerBoundByStart(phraseMatches[phraseIndex], minimumStart);
                var end = UpperBoundByStart(phraseMatches[phraseIndex], maximumStart);
                if (first == end)
                {
                    matches = false;
                    break;
                }

                ranges[phraseIndex] = new PhraseMatchRange(first, end);
            }

            if (!matches)
                continue;

            for (var phraseIndex = 0; phraseIndex < phraseMatches.Length; phraseIndex++)
                AddPhraseMatchRange(participatingRanges[phraseIndex], ranges[phraseIndex]);
        }

        for (var phraseIndex = 0; phraseIndex < phraseMatches.Length; phraseIndex++)
        {
            foreach (var range in participatingRanges[phraseIndex])
            {
                for (var index = range.Start; index < range.End; index++)
                {
                    var match = phraseMatches[phraseIndex][index];
                    AddMatchSpan(destination, match.Span.Start, match.Span.End);
                }
            }
        }
    }

    private static int LowerBoundByStart(IReadOnlyList<PhraseMatch> matches, long target)
    {
        var low = 0;
        var high = matches.Count;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (matches[middle].StartPosition < target)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    private static int UpperBoundByStart(IReadOnlyList<PhraseMatch> matches, int target)
    {
        var low = 0;
        var high = matches.Count;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (matches[middle].StartPosition <= target)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    private static void AddPhraseMatchRange(
        List<PhraseMatchRange> ranges,
        PhraseMatchRange next)
    {
        if (ranges.Count == 0 || next.Start > ranges[^1].End)
        {
            ranges.Add(next);
            return;
        }

        var previous = ranges[^1];
        ranges[^1] = new PhraseMatchRange(previous.Start, Math.Max(previous.End, next.End));
    }

    private readonly record struct PhraseMatch(
        int StartPosition,
        int EndPosition,
        MatchSpan Span);

    private readonly record struct PhraseMatchRange(int Start, int End);

    private static void AddMatchSpan(List<MatchSpan> destination, int start, int end)
    {
        if (destination.Count == ManagedFtsLimits.MaxHighlightSpans)
        {
            throw new EmbeddedSqlException(
                $"fts highlight matched more than {ManagedFtsLimits.MaxHighlightSpans} spans in one value");
        }

        destination.Add(new MatchSpan(start, end));
    }

    private static bool AppliesToColumn(string? constrainedColumn, string? requestedColumn)
        => requestedColumn is null
            || constrainedColumn is null
            || string.Equals(constrainedColumn, requestedColumn, StringComparison.OrdinalIgnoreCase);

    private sealed class Fts5TokenLookup
    {
        private readonly Dictionary<string, List<ManagedFtsToken>> _byTerm = new(StringComparer.Ordinal);
        private readonly Dictionary<int, ManagedFtsToken> _byPosition = [];

        public Fts5TokenLookup(IReadOnlyList<ManagedFtsToken> tokens)
        {
            foreach (var token in tokens)
            {
                _byPosition.TryAdd(token.Position, token);
                if (!_byTerm.TryGetValue(token.Text, out var occurrences))
                {
                    occurrences = [];
                    _byTerm.Add(token.Text, occurrences);
                }

                occurrences.Add(token);
            }

            foreach (var occurrences in _byTerm.Values)
            {
                var ordered = true;
                for (var index = 1; index < occurrences.Count; index++)
                {
                    if (occurrences[index - 1].Position > occurrences[index].Position)
                    {
                        ordered = false;
                        break;
                    }
                }

                if (!ordered)
                {
                    occurrences.Sort(static (left, right)
                        => left.Position == right.Position
                            ? left.Offset.CompareTo(right.Offset)
                            : left.Position.CompareTo(right.Position));
                }
            }
        }

        public IReadOnlyList<ManagedFtsToken> Get(string term)
            => _byTerm.TryGetValue(term, out var occurrences) ? occurrences : [];

        public IReadOnlyList<ManagedFtsToken> GetPrefix(string prefix)
            => _byPosition.Values
                .Where(token => token.Text.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(static token => token.Position)
                .ThenBy(static token => token.Offset)
                .ToArray();

        public bool TryGetAtPosition(string term, int position, out ManagedFtsToken token)
        {
            if (!_byTerm.TryGetValue(term, out var occurrences))
            {
                token = null!;
                return false;
            }

            var low = 0;
            var high = occurrences.Count;
            while (low < high)
            {
                var middle = low + ((high - low) >> 1);
                if (occurrences[middle].Position < position)
                    low = middle + 1;
                else
                    high = middle;
            }

            if (low < occurrences.Count && occurrences[low].Position == position)
            {
                token = occurrences[low];
                return true;
            }

            token = null!;
            return false;
        }

        public bool TryGetPrefixAtPosition(string prefix, int position, out ManagedFtsToken token)
        {
            if (_byPosition.TryGetValue(position, out token!)
                && token.Text.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }

            token = null!;
            return false;
        }

    }

    private static ManagedFtsSearchIndex BuildSingleDocumentIndex(
        IReadOnlyList<SqlValue> columns,
        IReadOnlyList<string?> columnNames,
        ManagedFtsScalarOptions options)
    {
        var weights = new double[columns.Count];
        Array.Fill(weights, 1.0);
        var tokenizers = options.ResolveColumnTokenizers(columns.Count);

        // The single-document index mirrors the covering index's detail and columnsize settings, so
        // a construct the real index cannot answer (a phrase against detail = 'columns', a column
        // filter against detail = 'none') fails on the scalar path too. Without this, the same SQL
        // would succeed or fail depending on which access path the planner picked.
        var index = new ManagedFtsSearchIndex(
            columns.Count,
            tokenizers,
            weights,
            options.Detail,
            options.ColumnSize)
        {
            ColumnIndexResolver = name => ResolveName(columnNames, name),
        };
        index.Upsert(0, [], columns.ToArray());
        return index;
    }

    private static ManagedFtsNode ParseMethodQuery(
        string query,
        IReadOnlyList<string?> columnNames,
        ManagedFtsScalarOptions options)
    {
        var tokenizers = options.ResolveColumnTokenizers(columnNames.Count);
        if (columnNames.All(static name => name is not null))
        {
            return ManagedFtsQueryLanguage.ParseMethod(
                query,
                columnNames.Select(static name => name!).ToArray(),
                tokenizers);
        }

        return ManagedFtsQueryLanguage.Parse(
            query,
            options.Tokenizer,
            name => ResolveName(columnNames, name) is not null,
            ManagedFtsQuerySyntax.TursoMethod);
    }

    private static int? ResolveName(IReadOnlyList<string?> columnNames, string name)
    {
        for (var index = 0; index < columnNames.Count; index++)
        {
            if (string.Equals(columnNames[index], name, StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return null;
    }

    private static (IReadOnlyList<SqlValue> Columns, string? Query) Split(string function, IReadOnlyList<SqlValue> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count < 2)
            throw new EmbeddedSqlException($"wrong number of arguments to function {function}()");
        if (arguments.Count - 1 > Indexing.ManagedIndexMethodLimits.MaxIndexedColumns)
        {
            throw new EmbeddedSqlException(
                $"{function}() accepts at most {Indexing.ManagedIndexMethodLimits.MaxIndexedColumns} columns");
        }

        var query = arguments[^1];
        if (query.Kind == SqlValueKind.Null)
            return (arguments.Take(arguments.Count - 1).ToArray(), null);
        if (query.Kind != SqlValueKind.Text)
            throw new EmbeddedSqlException($"{function}() requires a text query");

        return (arguments.Take(arguments.Count - 1).ToArray(), query.AsText());
    }
}
