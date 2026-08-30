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
    {
        var bound = ResolveBoundFtsIndex(function, row, context);
        return ManagedFtsFunctions.Match(
            arguments,
            CollectArgumentColumnNames(function),
            ResolveBoundFtsScalarOptions(bound));
    }

    /// <summary>
    /// Routes SQL's <c>col MATCH query</c> and <c>(col1,col2) MATCH query</c> spellings to the
    /// Turso-method scalar only when those columns resolve to an FTS method index. FTS5 MATCH remains
    /// a virtual-table constraint and a non-FTS MATCH continues to fail closed.
    /// </summary>
    private SqlValue EvaluateFtsMatchOperator(
        FunctionExpression function,
        SqlValue[] parameters,
        SourceRow? row,
        QueryContext context)
    {
        if (function.Arguments.Count != 2)
            throw new EmbeddedSqlException("unable to use function MATCH in the requested context");
        if (row is null)
            throw new EmbeddedSqlException("no such function: MATCH");

        var columnExpressions = function.Arguments[1] is RowValueExpression tuple
            ? tuple.Values
            : [function.Arguments[1]];
        if (columnExpressions.Any(static expression =>
                expression is not ColumnExpression { BooleanKeyword: null }))
        {
            throw new EmbeddedSqlException("no such function: MATCH");
        }
        var rewrittenArguments = new Expression[columnExpressions.Count + 1];
        for (var index = 0; index < columnExpressions.Count; index++)
            rewrittenArguments[index] = columnExpressions[index];
        rewrittenArguments[^1] = function.Arguments[0];

        var rewritten = new FunctionExpression(
            ManagedFtsPlannerAdapter.MatchFunction,
            rewrittenArguments,
            CountStar: false);
        if (ResolveBoundFtsIndex(rewritten, row, context) is null
            && !CanRouteFtsMatchOperator(columnExpressions, row, context))
        {
            throw new EmbeddedSqlException("unable to use function MATCH in the requested context");
        }

        var values = rewrittenArguments
            .Select(argument => Evaluate(argument, parameters, row, context))
            .ToArray();
        return EvaluateFtsMatch(rewritten, values, row, context);
    }

    private static bool CanRouteFtsMatchOperator(
        IReadOnlyList<Expression> columnExpressions,
        SourceRow? row,
        QueryContext context)
    {
        if (row is null || columnExpressions.Count == 0)
            return false;

        var names = new string[columnExpressions.Count];
        string? qualifier = null;
        for (var index = 0; index < columnExpressions.Count; index++)
        {
            if (columnExpressions[index] is not ColumnExpression { BooleanKeyword: null } column)
                return false;
            names[index] = column.UnqualifiedName ?? column.Name;
            if (column.Qualifier is null)
                continue;
            if (qualifier is not null
                && !string.Equals(qualifier, column.Qualifier, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            qualifier = column.Qualifier;
        }

        EmbeddedTable table;
        if (qualifier is not null)
        {
            if (!TryResolveSourceTable(qualifier, row, context, out _, out table)
                && !TryResolveQualifiedFtsSource(qualifier, row, context, names, out _, out table))
            {
                return false;
            }
        }
        else if (!TryResolveUnqualifiedFtsSource(row, context, names, out _, out table))
        {
            return false;
        }

        return table.Indexes.Any(static index =>
            index.IsMethodIndex
            && string.Equals(index.Method, "fts", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Evaluates <c>fts_score</c>. When a method index on the row's own source covers the exact
    /// column list, the score comes from that index's corpus so ranking is stable across access
    /// paths; otherwise it returns Turso's REAL <c>0.0</c> fallback.
    /// </summary>
    private SqlValue EvaluateFtsScore(
        FunctionExpression function,
        IReadOnlyList<SqlValue> arguments,
        SourceRow? row,
        QueryContext context)
    {
        if (context.IndexExpression || context.SchemaValidation)
        {
            return ManagedFtsFunctions.Score(
                arguments,
                CollectArgumentColumnNames(function));
        }

        var bound = ResolveBoundFtsIndex(function, row, context);
        if (bound is not null && arguments.Count >= 2)
        {
            if (arguments[^1].Kind == SqlValueKind.Null)
                return SqlValue.Real(0.0);
            if (arguments[^1].Kind != SqlValueKind.Text)
                throw new EmbeddedSqlException("fts_score() requires a text query");
            if (bound.RowId is not { } rowId)
                return SqlValue.Real(0.0);

            var binding = context.MethodIndexCache.GetOrOpen(bound.TableName, bound.Table, bound.Index);
            if (binding.Attachment.Definition.TryFindPattern(
                    ManagedIndexPatternShape.Score,
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
    /// The tokenizer the Ahtola-only <c>fts_snippet</c> extension uses for its text argument.
    /// Pinned <c>fts_highlight</c> always uses Turso's standalone default analyzer.
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
        ManagedFtsTokenizerOptions? boundTokenizer = null;
        foreach (var candidate in table.Indexes)
        {
            if (!candidate.IsMethodIndex
                || !string.Equals(candidate.Method, "fts", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var indexPosition = -1;
            for (var position = 0; position < candidate.Columns.Count; position++)
            {
                if (string.Equals(candidate.Columns[position].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    indexPosition = position;
                    break;
                }
            }

            if (indexPosition < 0)
                continue;

            var attachment = ManagedIndexMethodSemantics.GetAttachment(tableName, table, candidate)
                as ManagedFtsIndexAttachment;
            if (attachment is null)
                continue;

            // Two indexes with different tokenizers covering the same column make the choice
            // arbitrary; stay with the default rather than pick one silently.
            var tokenizer = attachment.Options.ColumnTokenizers[indexPosition];
            if (bound is not null && boundTokenizer != tokenizer)
                return null;

            bound = attachment;
            boundTokenizer = tokenizer;
        }

        return boundTokenizer;
    }

    private static ManagedFtsScalarOptions? ResolveBoundFtsScalarOptions(BoundFtsIndex? bound)
    {
        if (bound is null
            || ManagedIndexMethodSemantics.GetAttachment(bound.TableName, bound.Table, bound.Index)
                is not ManagedFtsIndexAttachment attachment)
        {
            return null;
        }

        var tokenizers = new ManagedFtsTokenizerOptions[bound.ArgumentToIndexColumn.Count];
        for (var argument = 0; argument < tokenizers.Length; argument++)
            tokenizers[argument] = attachment.Options.ColumnTokenizers[bound.ArgumentToIndexColumn[argument]];

        return new ManagedFtsScalarOptions(
            attachment.Options.Tokenizer,
            attachment.Options.Detail,
            attachment.Options.ColumnSize,
            tokenizers);
    }

    /// <summary>The method index a scalar <c>fts_*</c> call is bound to, plus the row it applies to.</summary>
    private sealed record BoundFtsIndex(
        string TableName,
        EmbeddedTable Table,
        EmbeddedIndex Index,
        long? RowId,
        IReadOnlyList<int> ArgumentToIndexColumn);

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
        string tableName;
        EmbeddedTable table;
        if (qualifier is not null)
        {
            if (!TryResolveSourceTable(qualifier, row, context, out tableName, out table)
                && !TryResolveQualifiedFtsSource(qualifier, row, context, names, out tableName, out table))
            {
                return null;
            }
        }
        else if (!TryResolveUnqualifiedFtsSource(row, context, names, out tableName, out table))
        {
            return null;
        }

        var rowId = qualifier is null ? row.RowId : row.GetRowIdForQualifier(qualifier);
        if (!table.HasMethodIndexes)
            return null;

        EmbeddedIndex? bound = null;
        ManagedFtsIndexAttachment? boundAttachment = null;
        int[]? boundMapping = null;
        foreach (var candidate in table.Indexes)
        {
            if (!candidate.IsMethodIndex
                || !string.Equals(candidate.Method, "fts", StringComparison.OrdinalIgnoreCase)
                || candidate.Columns.Count != columnCount)
            {
                continue;
            }

            var mapping = new int[columnCount];
            var seen = new bool[columnCount];
            var equal = true;
            for (var argument = 0; argument < columnCount; argument++)
            {
                var indexPosition = -1;
                for (var position = 0; position < candidate.Columns.Count; position++)
                {
                    if (string.Equals(candidate.Columns[position].Name, names[argument], StringComparison.OrdinalIgnoreCase))
                    {
                        indexPosition = position;
                        break;
                    }
                }

                if (indexPosition < 0 || seen[indexPosition])
                {
                    equal = false;
                    break;
                }

                seen[indexPosition] = true;
                mapping[argument] = indexPosition;
            }

            if (!equal)
                continue;

            var attachment = ManagedIndexMethodSemantics.GetAttachment(tableName, table, candidate)
                as ManagedFtsIndexAttachment;
            if (attachment is null)
                continue;

            // Equal configurations are interchangeable. Differing tokenizers, field weights or
            // detail options are observably different, so neither scalar binding nor prefiltering
            // may pick one by catalog order.
            if (boundAttachment is not null
                && !boundAttachment.HasEquivalentQuerySemantics(attachment))
            {
                return null;
            }

            if (bound is null
                || string.Compare(candidate.Name, bound.Name, StringComparison.OrdinalIgnoreCase) < 0)
            {
                bound = candidate;
                boundAttachment = attachment;
                boundMapping = mapping;
            }
        }

        return bound is null
            ? null
            : new BoundFtsIndex(tableName, table, bound, rowId, boundMapping!);
    }

    private static bool TryResolveUnqualifiedFtsSource(
        SourceRow row,
        QueryContext context,
        IReadOnlyList<string> names,
        out string tableName,
        out EmbeddedTable table)
    {
        tableName = string.Empty;
        table = null!;
        foreach (var (candidateName, candidate) in context.Tables)
        {
            if (!candidate.HasMethodIndexes
                || candidate.Columns.Length != row.Columns.Length
                || !candidate.Columns.SequenceEqual(row.Columns, StringComparer.OrdinalIgnoreCase)
                || names.Any(name => !candidate.Columns.Contains(name, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (table is not null)
                return false;
            tableName = candidateName;
            table = candidate;
        }

        return table is not null;
    }

    private static bool TryResolveQualifiedFtsSource(
        string qualifier,
        SourceRow row,
        QueryContext context,
        IReadOnlyList<string> names,
        out string tableName,
        out EmbeddedTable table)
    {
        tableName = string.Empty;
        table = null!;
        if (row.QualifiedColumns is null
            || names.Any(name => !row.QualifiedColumns.ContainsKey(qualifier + "." + name)))
        {
            return false;
        }

        foreach (var (candidateName, candidate) in context.Tables)
        {
            if (!candidate.HasMethodIndexes
                || names.Any(name => !candidate.Columns.Contains(name, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (table is not null)
                return false;
            tableName = candidateName;
            table = candidate;
        }

        return table is not null;
    }
}
