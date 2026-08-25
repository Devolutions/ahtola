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
    bool ColumnSize = true)
{
    public static ManagedFtsScalarOptions Default { get; } = new(ManagedFtsTokenizerOptions.Default);
}

/// <summary>
/// The <c>fts_*</c> SQL surface. <c>fts_match</c>, <c>fts_highlight</c> and <c>fts_snippet</c> are
/// pure functions of their arguments. <c>fts_score</c> additionally depends on the corpus statistics
/// of the covering method index, which is why it is registered as non-deterministic and rejected in
/// schema expressions.
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
            return SqlValue.Null;

        var resolved = options ?? ManagedFtsScalarOptions.Default;
        var index = BuildSingleDocumentIndex(columns, columnNames, resolved);
        var node = ManagedFtsQueryLanguage.Parse(
            query,
            resolved.Tokenizer,
            name => ResolveName(columnNames, name) is not null);
        return SqlValue.Integer(index.Matches(node, 0) ? 1 : 0);
    }

    /// <summary>
    /// <c>fts_score(col…, query)</c> evaluated without a bound index: BM25 over a corpus of one.
    /// The corpus-aware form is produced by the engine when a method index covers the call.
    /// </summary>
    public static SqlValue Score(
        IReadOnlyList<SqlValue> arguments,
        IReadOnlyList<string?> columnNames,
        ManagedFtsScalarOptions? options = null)
    {
        var (columns, query) = Split("fts_score", arguments);
        if (query is null)
            return SqlValue.Null;

        var resolved = options ?? ManagedFtsScalarOptions.Default;
        var index = BuildSingleDocumentIndex(columns, columnNames, resolved);
        var node = ManagedFtsQueryLanguage.Parse(
            query,
            resolved.Tokenizer,
            name => ResolveName(columnNames, name) is not null);
        return SqlValue.Real(index.Score(node, 0));
    }

    /// <summary>
    /// <c>fts_highlight(text, query, before, after)</c>: wraps every matching token occurrence.
    /// Offsets come from the tokenizer, so the untouched source text is reproduced exactly.
    /// </summary>
    public static SqlValue Highlight(
        IReadOnlyList<SqlValue> arguments,
        ManagedFtsTokenizerOptions? tokenizer = null)
    {
        if (arguments.Count != 4)
            throw new EmbeddedSqlException("wrong number of arguments to function fts_highlight()");
        if (arguments[0].Kind == SqlValueKind.Null || arguments[1].Kind == SqlValueKind.Null)
            return SqlValue.Null;

        var text = ManagedFtsSearchIndex.ReadText(arguments[0]);
        var query = ManagedFtsSearchIndex.ReadText(arguments[1]);
        var before = ManagedFtsSearchIndex.ReadText(arguments[2]);
        var after = ManagedFtsSearchIndex.ReadText(arguments[3]);
        var options = tokenizer ?? ManagedFtsTokenizerOptions.Default;
        var tokens = ManagedFtsTokenization.Tokenize(text, options);
        var spans = CollectMatchedSpans(tokens, query, options);
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

        var start = SelectWindowStart(tokens, window, matchedPositions);
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
    private static int SelectWindowStart(
        IReadOnlyList<ManagedFtsToken> tokens,
        int window,
        HashSet<int> matchedPositions)
    {
        if (matchedPositions.Count == 0 || tokens.Count <= window)
            return 0;

        var count = 0;
        for (var index = 0; index < window; index++)
        {
            if (matchedPositions.Contains(tokens[index].Position))
                count++;
        }

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

        return bestStart;
    }

    /// <summary>A merged source span covered by at least one matching token.</summary>
    private readonly record struct MatchSpan(int Start, int End);

    private static List<MatchSpan> CollectMatchedSpans(
        IReadOnlyList<ManagedFtsToken> tokens,
        string query,
        ManagedFtsTokenizerOptions options)
    {
        var node = ManagedFtsQueryLanguage.Parse(query, options, static _ => true);
        var wanted = new List<(string Text, bool IsPrefix)>();
        CollectPositiveTerms(node, wanted);

        // Exact terms resolve in constant time per token; only prefix terms need a scan, and the
        // parser's own term budget plus this bound keep that scan a small constant.
        var exact = new HashSet<string>(StringComparer.Ordinal);
        var prefixes = new List<string>();
        foreach (var (text, isPrefix) in wanted)
        {
            if (!isPrefix)
            {
                exact.Add(text);
                continue;
            }

            if (prefixes.Count == ManagedFtsLimits.MaxHighlightPrefixTerms)
            {
                throw new EmbeddedSqlException(
                    $"fts highlight query uses more than {ManagedFtsLimits.MaxHighlightPrefixTerms} prefix terms");
            }

            prefixes.Add(text);
        }

        var matched = new List<MatchSpan>();
        foreach (var token in tokens)
        {
            if (!exact.Contains(token.Text) && !StartsWithAny(token.Text, prefixes))
                continue;

            if (matched.Count == ManagedFtsLimits.MaxHighlightSpans)
            {
                throw new EmbeddedSqlException(
                    $"fts highlight matched more than {ManagedFtsLimits.MaxHighlightSpans} spans in one value");
            }

            matched.Add(new MatchSpan(token.Offset, token.Offset + token.Length));
        }

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

    private static bool StartsWithAny(string text, List<string> prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static void CollectPositiveTerms(ManagedFtsNode node, List<(string Text, bool IsPrefix)> destination)
    {
        switch (node)
        {
            case ManagedFtsTermNode term:
                destination.Add((term.Text, term.IsPrefix));
                return;
            case ManagedFtsPhraseNode phrase:
                foreach (var term in phrase.Terms)
                    destination.Add((term, false));
                return;
            case ManagedFtsNearNode near:
                foreach (var term in near.Terms)
                    destination.Add((term, false));
                return;
            case ManagedFtsAndNode and:
                CollectPositiveTerms(and.Left, destination);
                CollectPositiveTerms(and.Right, destination);
                return;
            case ManagedFtsOrNode or:
                CollectPositiveTerms(or.Left, destination);
                CollectPositiveTerms(or.Right, destination);
                return;
            case ManagedFtsNotNode:
                // A negated branch never contributes highlighted text.
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(node));
        }
    }

    private static ManagedFtsSearchIndex BuildSingleDocumentIndex(
        IReadOnlyList<SqlValue> columns,
        IReadOnlyList<string?> columnNames,
        ManagedFtsScalarOptions options)
    {
        var weights = new double[columns.Count];
        Array.Fill(weights, 1.0);

        // The single-document index mirrors the covering index's detail and columnsize settings, so
        // a construct the real index cannot answer (a phrase against detail = 'columns', a column
        // filter against detail = 'none') fails on the scalar path too. Without this, the same SQL
        // would succeed or fail depending on which access path the planner picked.
        var index = new ManagedFtsSearchIndex(
            columns.Count,
            options.Tokenizer,
            weights,
            options.Detail,
            options.ColumnSize)
        {
            ColumnIndexResolver = name => ResolveName(columnNames, name),
        };
        index.Upsert(0, [], columns.ToArray());
        return index;
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
