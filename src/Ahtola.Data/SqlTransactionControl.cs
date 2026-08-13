namespace Ahtola;

internal enum SqlTransactionCompletion
{
    None,
    Commit,
    Rollback,
}

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
