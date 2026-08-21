namespace Ahtola;

internal enum SqlTransactionCompletion
{
    None,
    Commit,
    Rollback,
}

internal enum SqlSavepointAction
{
    None,
    Savepoint,
    Release,
    RollbackTo,
}

internal readonly record struct SqlSavepointCommand(SqlSavepointAction Action, string? Name);

internal static class SqlTransactionControl
{
    public static string? GetFirstKeyword(string sql)
    {
        var index = 0;
        SkipLeadingEmptyStatements(sql, ref index);
        var keyword = ReadKeyword(sql, ref index);
        return keyword.Length == 0 ? null : keyword;
    }

    public static SqlTransactionCompletion GetCompletion(string sql)
    {
        var index = 0;
        SkipLeadingEmptyStatements(sql, ref index);
        var command = ReadKeyword(sql, ref index);
        if (command.Equals("COMMIT", StringComparison.OrdinalIgnoreCase)
            || command.Equals("END", StringComparison.OrdinalIgnoreCase))
        {
            return SqlTransactionCompletion.Commit;
        }

        if (!command.Equals("ROLLBACK", StringComparison.OrdinalIgnoreCase))
            return SqlTransactionCompletion.None;

        var tail = ReadKeyword(sql, ref index);
        if (tail.Equals("TRANSACTION", StringComparison.OrdinalIgnoreCase))
            tail = ReadKeyword(sql, ref index);

        return tail.Equals("TO", StringComparison.OrdinalIgnoreCase)
            ? SqlTransactionCompletion.None
            : SqlTransactionCompletion.Rollback;
    }

    public static SqlSavepointCommand GetSavepointCommand(string sql)
    {
        var index = 0;
        SkipLeadingEmptyStatements(sql, ref index);
        var command = ReadKeyword(sql, ref index);
        if (command.Equals("SAVEPOINT", StringComparison.OrdinalIgnoreCase))
            return new SqlSavepointCommand(SqlSavepointAction.Savepoint, ReadIdentifier(sql, ref index));

        if (command.Equals("RELEASE", StringComparison.OrdinalIgnoreCase))
        {
            var name = ReadIdentifier(sql, ref index);
            if (name.Equals("SAVEPOINT", StringComparison.OrdinalIgnoreCase))
                name = ReadIdentifier(sql, ref index);
            return new SqlSavepointCommand(SqlSavepointAction.Release, name);
        }

        if (!command.Equals("ROLLBACK", StringComparison.OrdinalIgnoreCase))
            return default;

        var tail = ReadKeyword(sql, ref index);
        if (tail.Equals("TRANSACTION", StringComparison.OrdinalIgnoreCase))
            tail = ReadKeyword(sql, ref index);
        if (!tail.Equals("TO", StringComparison.OrdinalIgnoreCase))
            return default;

        var rollbackName = ReadIdentifier(sql, ref index);
        if (rollbackName.Equals("SAVEPOINT", StringComparison.OrdinalIgnoreCase))
            rollbackName = ReadIdentifier(sql, ref index);
        return new SqlSavepointCommand(SqlSavepointAction.RollbackTo, rollbackName);
    }

    private static string ReadIdentifier(string sql, ref int index)
    {
        SkipTrivia(sql, ref index);
        if (index >= sql.Length)
            return string.Empty;

        var quote = sql[index];
        var closingQuote = quote switch
        {
            '"' or '\'' or '`' => quote,
            '[' => ']',
            _ => '\0',
        };
        if (closingQuote == '\0')
            return ReadKeyword(sql, ref index);

        index++;
        var result = new System.Text.StringBuilder();
        while (index < sql.Length)
        {
            var current = sql[index++];
            if (current != closingQuote)
            {
                result.Append(current);
                continue;
            }

            if (index < sql.Length && sql[index] == closingQuote && closingQuote != ']')
            {
                result.Append(closingQuote);
                index++;
                continue;
            }

            return result.ToString();
        }

        return result.ToString();
    }

    private static string ReadKeyword(string sql, ref int index)
    {
        SkipTrivia(sql, ref index);
        var start = index;
        while (index < sql.Length
               && (char.IsLetterOrDigit(sql[index]) || sql[index] is '_' or '$'))
            index++;
        return sql[start..index];
    }

    private static void SkipTrivia(string sql, ref int index)
    {
        while (index < sql.Length)
        {
            if (char.IsWhiteSpace(sql[index]))
            {
                index++;
                continue;
            }

            if (index + 1 < sql.Length && sql[index] == '-' && sql[index + 1] == '-')
            {
                index += 2;
                while (index < sql.Length && sql[index] is not '\r' and not '\n')
                    index++;
                continue;
            }

            if (index + 1 < sql.Length && sql[index] == '/' && sql[index + 1] == '*')
            {
                var commentEnd = sql.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (commentEnd < 0)
                {
                    index = sql.Length;
                    return;
                }

                index = commentEnd + 2;
                continue;
            }

            return;
        }
    }

    private static void SkipLeadingEmptyStatements(string sql, ref int index)
    {
        while (index < sql.Length)
        {
            SkipTrivia(sql, ref index);
            if (index >= sql.Length || sql[index] != ';')
                return;

            index++;
        }
    }
}
