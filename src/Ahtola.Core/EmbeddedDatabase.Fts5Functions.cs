using Ahtola.Core.Parsing;
using Ahtola.Core.Search;

namespace Ahtola.Core;

public sealed partial class EmbeddedDatabase
{
    private static ManagedFts5SourceBinding ResolveFts5Source(
        FunctionExpression function,
        SourceRow? row)
    {
        if (row is null
            || function.Arguments.Count == 0
            || function.Arguments[0] is not ColumnExpression { BooleanKeyword: null } column)
        {
            throw new EmbeddedSqlException(
                $"unable to use function {function.Name.ToLowerInvariant()} in the requested context");
        }

        for (var scope = row; scope is not null; scope = scope.Parent)
        {
            if (!TryResolveColumnLocally(scope, column))
                continue;

            var localScope = scope.Parent is null ? scope : scope with { Parent = null };
            var binding = column.Qualifier is { } qualifier
                ? localScope.GetFts5SourceForQualifier(qualifier)
                : localScope.GetFts5SourceForTable(column.UnqualifiedName ?? column.Name);
            if (binding is not null)
                return binding;

            break;
        }

        throw new EmbeddedSqlException(
            $"unable to use function {function.Name.ToLowerInvariant()} in the requested context");
    }

    private static SqlValue EvaluateFts5Bm25(
        FunctionExpression function,
        IReadOnlyList<SqlValue> arguments,
        SourceRow? row)
    {
        if (arguments.Count == 0)
            throw new EmbeddedSqlException("wrong number of arguments to function bm25()");

        var binding = ResolveFts5Source(function, row);
        if (arguments.Count == 1)
            return SqlValue.Real(binding.Rank ?? 0.0);

        var weights = new double[binding.Table.ColumnNames.Count];
        Array.Fill(weights, 1.0);
        for (var index = 1; index < arguments.Count && index <= weights.Length; index++)
            weights[index - 1] = ReadFts5Double(arguments[index]);

        return SqlValue.Real(binding.ScoreCache?.Score(binding.RowId, weights)
            ?? binding.Table.Score(binding.Query, binding.RowId, weights));
    }

    private static SqlValue EvaluateFts5Highlight(
        FunctionExpression function,
        IReadOnlyList<SqlValue> arguments,
        SourceRow? row)
    {
        RequireArgumentCount("highlight", arguments, 4);
        var binding = ResolveFts5Source(function, row);
        var column = ReadFts5ColumnIndex("highlight", arguments[1], binding.Table.ColumnNames.Count, allowAutomatic: false);
        return ManagedFtsFunctions.HighlightFts5(
            binding.Table.GetColumnValue(binding.RowId, column),
            binding.Table.IsIndexedColumn(column) ? binding.Query : null,
            binding.Table.ColumnNames[column],
            ManagedFtsSearchIndex.ReadText(arguments[2]),
            ManagedFtsSearchIndex.ReadText(arguments[3]),
            binding.Table.Tokenizer);
    }

    private static SqlValue EvaluateFts5Snippet(
        FunctionExpression function,
        IReadOnlyList<SqlValue> arguments,
        SourceRow? row)
    {
        RequireArgumentCount("snippet", arguments, 6);
        var binding = ResolveFts5Source(function, row);
        var column = ReadFts5ColumnIndex("snippet", arguments[1], binding.Table.ColumnNames.Count, allowAutomatic: true);
        var requestedWindow = ReadFts5Integer(arguments[5]);
        if (requestedWindow is < 1 or > 4096)
            throw new EmbeddedSqlException("snippet() token count must be between 1 and 4096");
        var window = (int)requestedWindow;
        if (column < 0)
            column = binding.Table.SelectSnippetColumn(binding.Query, binding.RowId, window);
        return ManagedFtsFunctions.SnippetFts5(
            binding.Table.GetColumnValue(binding.RowId, column),
            binding.Table.IsIndexedColumn(column) ? binding.Query : null,
            binding.Table.ColumnNames[column],
            ManagedFtsSearchIndex.ReadText(arguments[2]),
            ManagedFtsSearchIndex.ReadText(arguments[3]),
            ManagedFtsSearchIndex.ReadText(arguments[4]),
            window,
            binding.Table.Tokenizer);
    }

    private static int ReadFts5ColumnIndex(
        string function,
        SqlValue value,
        int columnCount,
        bool allowAutomatic)
    {
        var column = ReadFts5Integer(value);
        var minimum = allowAutomatic ? -1 : 0;
        if (column < minimum || column >= columnCount)
            throw new EmbeddedSqlException($"{function}() column index is out of range");

        return checked((int)column);
    }

    private static double ReadFts5Double(SqlValue value)
    {
        var numeric = ApplyNumericAffinity(value);
        return numeric.Kind == SqlValueKind.Integer ? numeric.AsInteger() : numeric.AsReal();
    }

    private static long ReadFts5Integer(SqlValue value)
    {
        var numeric = ApplyNumericAffinity(value);
        return numeric.Kind == SqlValueKind.Integer
            ? numeric.AsInteger()
            : ToSqliteInteger(numeric.AsReal());
    }
}
