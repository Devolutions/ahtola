using Ahtola.Core.Indexing;
using Ahtola.Core.Parsing;
using Ahtola.Core.Search;

namespace Ahtola.Core;

/// <summary>
/// The FTS half of the scalar surface: how <c>fts_match</c>, <c>fts_score</c>, <c>fts_highlight</c>
/// and <c>fts_snippet</c> find the method index they belong to.
/// </summary>
/// <remarks>
/// This is deliberately separate from <c>EmbeddedDatabase.IndexMethods.cs</c>, which is method
/// generic. Every cast to an FTS attachment lives here, so a second index method can be added
/// without touching the planner or the execution path.
/// </remarks>
public sealed partial class EmbeddedDatabase
{
    /// <summary>
    /// Evaluates <c>fts_match</c>. Always row local, so the predicate answers identically whether or
    /// not the planner used a method index; when a method index covers the exact column list of the
    /// row's own source, its configured tokenizer is used so index and scalar tokenization can never
    /// diverge.
    /// </summary>
    private SqlValue EvaluateFtsMatch(
        FunctionExpression function,
        IReadOnlyList<SqlValue> arguments,
        SourceRow? row,
        QueryContext context)
        => ManagedFtsFunctions.Match(
            arguments,
            CollectArgumentColumnNames(function),
            ResolveBoundFtsScalarOptions(ResolveBoundFtsIndex(function, row, context)));

    /// <summary>
    /// Evaluates <c>fts_score</c>. When a method index on the row's own source covers the exact
    /// column list, the score comes from that index's corpus so ranking is stable across access
    /// paths; otherwise it degrades to a documented single-document BM25.
    /// </summary>
    private SqlValue EvaluateFtsScore(
        FunctionExpression function,
        IReadOnlyList<SqlValue> arguments,
        SourceRow? row,
        QueryContext context)
    {
        var bound = ResolveBoundFtsIndex(function, row, context);
        if (bound is not null
            && arguments.Count >= 2
            && arguments[^1].Kind == SqlValueKind.Text
            && bound.RowId is { } rowId)
        {
            var binding = context.MethodIndexCache.GetOrOpen(bound.TableName, bound.Table, bound.Index);
            if (binding.Attachment.Definition.TryFindPattern(
                    ManagedIndexPatternShape.ScoreOrdered,
                    out var patternIndex))
            {
                return binding.TryGetRank(patternIndex, arguments[^1], rowId, out var rank)
                    ? rank
                    : SqlValue.Real(0.0);
            }
        }

        return ManagedFtsFunctions.Score(
            arguments,
            CollectArgumentColumnNames(function),
            ResolveBoundFtsScalarOptions(bound));
    }

    /// <summary>
    /// The tokenizer <c>fts_highlight</c>/<c>fts_snippet</c> must use for a given text argument.
    /// Both take a single column, so they bind to whichever index on that column's own source
    /// covers it; without a binding they fall back to the documented default tokenizer.
    /// </summary>
    private ManagedFtsTokenizerOptions? ResolveBoundFtsTokenizer(
        FunctionExpression function,
        SourceRow? row,
        QueryContext context)
    {
        if (IsShadowedMethodFunction(function.Name))
            return null;
        if (row is null
            || function.Arguments.Count == 0
            || function.Arguments[0] is not ColumnExpression { BooleanKeyword: null } column)
        {
            return null;
        }

        var qualifier = column.Qualifier ?? row.RowIdQualifier;
        if (qualifier is null || !TryResolveSourceTable(qualifier, row, context, out var tableName, out var table))
            return null;
        if (!table.HasMethodIndexes)
            return null;

        var name = column.UnqualifiedName ?? column.Name;
        ManagedFtsIndexAttachment? bound = null;
        foreach (var candidate in table.Indexes)
        {
            if (!candidate.IsMethodIndex
                || !string.Equals(candidate.Method, "fts", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var covers = false;
            foreach (var indexColumn in candidate.Columns)
            {
                if (string.Equals(indexColumn.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    covers = true;
                    break;
                }
            }

            if (!covers)
                continue;

            var attachment = ManagedIndexMethodSemantics.GetAttachment(tableName, table, candidate)
                as ManagedFtsIndexAttachment;
            if (attachment is null)
                continue;

            // Two indexes with different tokenizers covering the same column make the choice
            // arbitrary; stay with the default rather than pick one silently.
            if (bound is not null && bound.Options.Tokenizer != attachment.Options.Tokenizer)
                return null;

            bound = attachment;
        }

        return bound?.Options.Tokenizer;
    }

    private ManagedFtsScalarOptions? ResolveBoundFtsScalarOptions(BoundFtsIndex? bound)
        => bound is null
            ? null
            : ManagedIndexMethodSemantics.GetAttachment(bound.TableName, bound.Table, bound.Index)
                as ManagedFtsIndexAttachment is { } attachment
                ? new ManagedFtsScalarOptions(
                    attachment.Options.Tokenizer,
                    attachment.Options.Detail,
                    attachment.Options.ColumnSize)
                : null;

    /// <summary>The method index a scalar <c>fts_*</c> call is bound to, plus the row it applies to.</summary>
    private sealed record BoundFtsIndex(
        string TableName,
        EmbeddedTable Table,
        EmbeddedIndex Index,
        long? RowId);

    /// <summary>
    /// Resolves the one method index a scalar <c>fts_*</c> call is bound to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Binding is by <em>source identity</em>, never by column-name similarity. The call's column
    /// arguments must all resolve to a single source: the qualifier they carry, or — when they are
    /// unqualified — the source the current row belongs to. Only indexes on that source's table are
    /// considered, and the rowid used for corpus scoring is that source's rowid.
    /// </para>
    /// <para>
    /// This is what stops an unrelated table's index from changing scalar behavior. Matching purely
    /// on column names meant that creating <c>docs_fts</c> on <c>docs(title, body)</c> silently
    /// changed the meaning of <c>fts_score(title, body, ?)</c> over a completely different table
    /// that happened to have columns with the same names, and in a join it scored the wrong row.
    /// </para>
    /// <para>
    /// A user scalar callback that shadows the function name suppresses binding entirely, so the
    /// callback's own semantics are never mixed with index-aware ones.
    /// </para>
    /// </remarks>
    private BoundFtsIndex? ResolveBoundFtsIndex(
        FunctionExpression function,
        SourceRow? row,
        QueryContext context)
    {
        if (IsShadowedMethodFunction(function.Name))
            return null;
        if (function.Arguments.Count < 2 || row is null)
            return null;

        var columnCount = function.Arguments.Count - 1;
        var names = new string[columnCount];
        string? qualifier = null;
        for (var position = 0; position < columnCount; position++)
        {
            if (function.Arguments[position] is not ColumnExpression { BooleanKeyword: null } column)
                return null;

            names[position] = column.UnqualifiedName ?? column.Name;
            if (column.Qualifier is not { } columnQualifier)
                continue;

            // Every column argument must come from the same source, or the call spans a join and no
            // single index can describe it.
            if (qualifier is not null && !string.Equals(qualifier, columnQualifier, StringComparison.OrdinalIgnoreCase))
                return null;

            qualifier = columnQualifier;
        }

        // Unqualified arguments belong to the source that produced this row.
        qualifier ??= row.RowIdQualifier;
        if (qualifier is null)
            return null;

        var rowId = row.GetRowIdForQualifier(qualifier);
        if (rowId is null)
            return null;

        if (!TryResolveSourceTable(qualifier, row, context, out var tableName, out var table))
            return null;
        if (!table.HasMethodIndexes)
            return null;

        EmbeddedIndex? bound = null;
        foreach (var candidate in table.Indexes)
        {
            if (!candidate.IsMethodIndex
                || !string.Equals(candidate.Method, "fts", StringComparison.OrdinalIgnoreCase)
                || candidate.Columns.Count != columnCount)
            {
                continue;
            }

            var equal = true;
            for (var position = 0; position < columnCount; position++)
            {
                if (!string.Equals(candidate.Columns[position].Name, names[position], StringComparison.OrdinalIgnoreCase))
                {
                    equal = false;
                    break;
                }
            }

            if (!equal)
                continue;

            // More than one covering index makes the choice arbitrary; stay with scalar semantics.
            if (bound is not null)
                return null;

            bound = candidate;
        }

        return bound is null ? null : new BoundFtsIndex(tableName, table, bound, rowId);
    }
}
