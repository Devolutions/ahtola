using Ahtola.Core.Execution;

namespace Ahtola.Core;

/// <summary>
/// The <c>WHERE</c> clause a <c>ParseSchema</c> instruction carries, restricted to the shapes a DDL
/// program actually emits.
/// </summary>
/// <remarks>
/// <para>
/// Turso passes a real SQL string that its own query engine runs over <c>sqlite_schema</c>. Ahtola's
/// <c>ParseSchema</c> runs against the transaction-local row set rather than a query, so it needs its own
/// matcher. Rather than pretend to support arbitrary SQL, this parser accepts exactly the grammar the
/// upstream translators produce — <c>AND</c>-joined equality/inequality comparisons of <c>type</c>,
/// <c>name</c> or <c>tbl_name</c> against a string literal — and rejects everything else with a
/// diagnosable error instead of silently matching nothing.
/// </para>
/// <para>Examples that parse:</para>
/// <code>
/// tbl_name = 'orders' AND type != 'trigger'
/// type = 'trigger' AND tbl_name = 'orders'
/// name = 'idx_orders_id'
/// </code>
/// </remarks>
internal sealed class ManagedSchemaRowFilter
{
    private enum Column
    {
        Type,
        Name,
        TableName,
    }

    private readonly record struct Term(Column Column, bool Negated, string Literal);

    private readonly List<Term> _terms;

    private ManagedSchemaRowFilter(List<Term> terms) => _terms = terms;

    /// <summary>A filter that matches every row, used when <c>ParseSchema</c> carries no clause.</summary>
    public static ManagedSchemaRowFilter MatchAll { get; } = new([]);

    public static ManagedSchemaRowFilter Parse(string? whereClause)
    {
        if (whereClause is null)
            return MatchAll;

        var text = whereClause.Trim();
        if (text.Length == 0)
            throw Invalid(whereClause, "it is empty");

        // The upstream translators sometimes wrap the whole clause in parentheses; nothing else in the
        // accepted grammar uses them, so a balanced outer pair is simply peeled off.
        while (text.Length > 1 && text[0] == '(' && text[^1] == ')' && IsBalancedOuterPair(text))
            text = text[1..^1].Trim();

        var terms = new List<Term>();
        foreach (var conjunct in SplitConjuncts(text, whereClause))
            terms.Add(ParseTerm(conjunct, whereClause));

        return new ManagedSchemaRowFilter(terms);
    }

    public bool Matches(ManagedSchemaRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        foreach (var term in _terms)
        {
            var value = term.Column switch
            {
                Column.Type => row.Type,
                Column.Name => row.Name,
                _ => row.TableName,
            };
            var comparison = term.Column == Column.Type
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            if (string.Equals(value, term.Literal, comparison) == term.Negated)
                return false;
        }

        return true;
    }

    /// <summary>The matching rows, in schema order.</summary>
    public IEnumerable<ManagedSchemaRow> Apply(ManagedSchemaRowSet rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var row in rows.Rows)
        {
            if (Matches(row))
                yield return row;
        }
    }

    private static bool IsBalancedOuterPair(string text)
    {
        var depth = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\'')
            {
                index = SkipLiteral(text, index);
                continue;
            }
            if (text[index] == '(')
                depth++;
            else if (text[index] == ')' && --depth == 0)
                return index == text.Length - 1;
        }

        return false;
    }

    private static int SkipLiteral(string text, int openingQuote)
    {
        for (var index = openingQuote + 1; index < text.Length; index++)
        {
            if (text[index] != '\'')
                continue;
            if (index + 1 < text.Length && text[index + 1] == '\'')
            {
                index++;
                continue;
            }

            return index;
        }

        return text.Length - 1;
    }

    private static List<string> SplitConjuncts(string text, string original)
    {
        var conjuncts = new List<string>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\'')
            {
                index = SkipLiteral(text, index);
                continue;
            }

            // A disjunction is rejected by name rather than left to the term parser, which would only
            // notice it as a malformed literal and report a misleading quoting error.
            if (IsKeywordAt(text, index, "OR"))
                throw Invalid(original, "the accepted grammar joins terms with AND only");

            if (!IsKeywordAt(text, index, "AND"))
                continue;

            conjuncts.Add(text[start..index]);
            index += 2;
            start = index + 1;
        }

        conjuncts.Add(text[start..]);
        foreach (var conjunct in conjuncts)
        {
            if (conjunct.Trim().Length == 0)
                throw Invalid(original, "it has an empty conjunct");
        }

        return conjuncts;
    }

    private static bool IsKeywordAt(string text, int index, string keyword)
    {
        if (index + keyword.Length > text.Length)
            return false;
        if (!text.AsSpan(index, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase))
            return false;
        if (index > 0 && !char.IsWhiteSpace(text[index - 1]) && text[index - 1] != ')')
            return false;

        var after = index + keyword.Length;
        return after == text.Length || char.IsWhiteSpace(text[after]) || text[after] == '(';
    }

    private static Term ParseTerm(string conjunct, string original)
    {
        var text = conjunct.Trim();
        var operatorIndex = text.IndexOfAny(['=', '!', '<']);
        if (operatorIndex <= 0)
            throw Invalid(original, $"the term '{text}' has no supported comparison operator");

        var column = ParseColumn(text[..operatorIndex].Trim(), original);
        var remainder = text[operatorIndex..];
        bool negated;
        int operatorLength;
        if (remainder.StartsWith("!=", StringComparison.Ordinal) || remainder.StartsWith("<>", StringComparison.Ordinal))
        {
            negated = true;
            operatorLength = 2;
        }
        else if (remainder.StartsWith("==", StringComparison.Ordinal))
        {
            negated = false;
            operatorLength = 2;
        }
        else if (remainder.StartsWith('='))
        {
            negated = false;
            operatorLength = 1;
        }
        else
        {
            throw Invalid(original, $"the term '{text}' has no supported comparison operator");
        }

        return new Term(column, negated, ParseLiteral(remainder[operatorLength..].Trim(), original));
    }

    private static Column ParseColumn(string name, string original) => name.ToUpperInvariant() switch
    {
        "TYPE" => Column.Type,
        "NAME" => Column.Name,
        "TBL_NAME" => Column.TableName,
        _ => throw Invalid(original, $"'{name}' is not a filterable sqlite_schema column"),
    };

    private static string ParseLiteral(string text, string original)
    {
        if (text.Length < 2 || text[0] != '\'' || text[^1] != '\'')
            throw Invalid(original, $"'{text}' is not a single-quoted string literal");

        var body = text[1..^1];
        if (body.Contains('\'', StringComparison.Ordinal)
            && body.Replace("''", string.Empty, StringComparison.Ordinal).Contains('\'', StringComparison.Ordinal))
        {
            throw Invalid(original, $"'{text}' has an unbalanced quote");
        }

        return body.Replace("''", "'", StringComparison.Ordinal);
    }

    private static VdbeSchemaExecutionException Invalid(string clause, string reason)
        => new($"ParseSchema cannot use the clause \"{clause}\" because {reason}.");
}
