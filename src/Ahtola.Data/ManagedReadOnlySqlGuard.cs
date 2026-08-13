using System.Globalization;

namespace Ahtola;

internal static class ManagedReadOnlySqlGuard
{
    internal const string QueryOnlyDisabledMessage =
        "PRAGMA query_only cannot be disabled when Mode=ReadOnly and Local Provider=Managed.";

    internal static void ThrowIfQueryOnlyIsDisabled(string sql)
    {
        var statementStart = 0;
        while (statementStart < sql.Length)
        {
            var statementEnd = FindStatementEnd(sql, statementStart);
            ThrowIfStatementDisablesQueryOnly(sql.AsSpan(statementStart, statementEnd - statementStart));
            statementStart = statementEnd + 1;
        }
    }

    private static void ThrowIfStatementDisablesQueryOnly(ReadOnlySpan<char> statement)
    {
        var index = 0;
        SkipTrivia(statement, ref index);
        if (!TryReadIdentifier(statement, ref index, out var keyword)
            || !keyword.Equals("PRAGMA", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SkipTrivia(statement, ref index);
        if (!TryReadIdentifier(statement, ref index, out var name))
            return;

        SkipTrivia(statement, ref index);
        if (index < statement.Length && statement[index] == '.')
        {
            index++;
            SkipTrivia(statement, ref index);
            if (!TryReadIdentifier(statement, ref index, out name))
                return;
            SkipTrivia(statement, ref index);
        }

        if (!name.Equals("query_only", StringComparison.OrdinalIgnoreCase))
            return;

        var parenthesized = false;
        if (index < statement.Length && statement[index] == '=')
        {
            index++;
        }
        else if (index < statement.Length && statement[index] == '(')
        {
            parenthesized = true;
            index++;
        }
        else
        {
            return;
        }

        SkipTrivia(statement, ref index);
        if (!TryReadPragmaValue(statement, ref index, out var value, out var quoted)
            || !IsEnabledPragmaValue(value, quoted))
        {
            throw new InvalidOperationException(QueryOnlyDisabledMessage);
        }

        if (parenthesized)
        {
            SkipTrivia(statement, ref index);
            if (index >= statement.Length || statement[index] != ')')
                throw new InvalidOperationException(QueryOnlyDisabledMessage);
        }
    }

    private static bool IsEnabledPragmaValue(ReadOnlySpan<char> value, bool quoted)
    {
        if (!quoted)
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                return integer != 0;

            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var real))
                return double.IsFinite(real) && real != 0;
        }

        return value.Equals("ON", StringComparison.OrdinalIgnoreCase)
               || value.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
               || value.Equals("YES", StringComparison.OrdinalIgnoreCase)
               || value.SequenceEqual("1");
    }

    private static int FindStatementEnd(string sql, int index)
    {
        while (index < sql.Length)
        {
            if (sql[index] is '\'' or '"' or '`')
            {
                SkipQuoted(sql, ref index, sql[index]);
                continue;
            }

            if (sql[index] == '[')
            {
                SkipBracketed(sql, ref index);
                continue;
            }

            if (sql[index] == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                SkipLineComment(sql, ref index);
                continue;
            }

            if (sql[index] == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                SkipBlockComment(sql, ref index);
                continue;
            }

            if (sql[index] == ';')
                return index;

            index++;
        }

        return index;
    }

    private static void SkipTrivia(ReadOnlySpan<char> sql, ref int index)
    {
        while (true)
        {
            while (index < sql.Length && char.IsWhiteSpace(sql[index]))
                index++;

            if (index + 1 < sql.Length && sql[index] == '-' && sql[index + 1] == '-')
            {
                SkipLineComment(sql, ref index);
                continue;
            }

            if (index + 1 < sql.Length && sql[index] == '/' && sql[index + 1] == '*')
            {
                SkipBlockComment(sql, ref index);
                continue;
            }

            return;
        }
    }

    private static bool TryReadIdentifier(ReadOnlySpan<char> sql, ref int index, out ReadOnlySpan<char> identifier)
    {
        identifier = default;
        if (index >= sql.Length)
            return false;

        if (sql[index] is '\'' or '"' or '`')
            return TryReadQuoted(sql, ref index, sql[index], out identifier);
        if (sql[index] == '[')
            return TryReadBracketed(sql, ref index, out identifier);

        var start = index;
        while (index < sql.Length && IsIdentifierPart(sql[index]))
            index++;
        if (index == start)
            return false;

        identifier = sql[start..index];
        return true;
    }

    private static bool TryReadPragmaValue(
        ReadOnlySpan<char> sql,
        ref int index,
        out ReadOnlySpan<char> value,
        out bool quoted)
    {
        value = default;
        quoted = false;
        if (index >= sql.Length)
            return false;

        if (sql[index] is '\'' or '"' or '`')
        {
            quoted = true;
            return TryReadQuoted(sql, ref index, sql[index], out value);
        }

        var start = index;
        while (index < sql.Length
               && !char.IsWhiteSpace(sql[index])
               && sql[index] is not ')' and not ';')
        {
            if (sql[index] == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
                break;
            if (sql[index] == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
                break;

            index++;
        }

        if (index == start)
            return false;

        value = sql[start..index];
        return true;
    }

    private static bool TryReadQuoted(
        ReadOnlySpan<char> sql,
        ref int index,
        char quote,
        out ReadOnlySpan<char> value)
    {
        index++;
        var start = index;
        while (index < sql.Length)
        {
            if (sql[index] != quote)
            {
                index++;
                continue;
            }

            if (index + 1 < sql.Length && sql[index + 1] == quote)
            {
                index += 2;
                continue;
            }

            value = sql[start..index];
            index++;
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryReadBracketed(ReadOnlySpan<char> sql, ref int index, out ReadOnlySpan<char> value)
    {
        index++;
        var start = index;
        while (index < sql.Length && sql[index] != ']')
            index++;
        if (index == sql.Length)
        {
            value = default;
            return false;
        }

        value = sql[start..index];
        index++;
        return true;
    }

    private static void SkipQuoted(ReadOnlySpan<char> sql, ref int index, char quote)
    {
        _ = TryReadQuoted(sql, ref index, quote, out _);
    }

    private static void SkipBracketed(ReadOnlySpan<char> sql, ref int index)
    {
        _ = TryReadBracketed(sql, ref index, out _);
    }

    private static void SkipLineComment(ReadOnlySpan<char> sql, ref int index)
    {
        index += 2;
        while (index < sql.Length && sql[index] is not '\r' and not '\n')
            index++;
    }

    private static void SkipBlockComment(ReadOnlySpan<char> sql, ref int index)
    {
        index += 2;
        while (index + 1 < sql.Length && (sql[index] != '*' || sql[index + 1] != '/'))
            index++;
        index = Math.Min(index + 2, sql.Length);
    }

    private static bool IsIdentifierPart(char value)
        => char.IsLetterOrDigit(value) || value is '_' or '$';
}
