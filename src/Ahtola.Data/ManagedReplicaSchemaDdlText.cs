namespace Ahtola;

/// <summary>
/// Minimal, quote/comment-aware SQL text scanning used only to make schema DDL replay
/// idempotent (diffing a remote <c>CREATE TABLE</c>'s column list against the local table, and
/// recognizing an <c>ALTER TABLE ... ADD COLUMN</c> shape). This is deliberately not a general
/// SQL parser: it only locates paren nesting, top-level commas, and leading identifiers well
/// enough to reproduce Turso's <c>execute_ddl_idempotent</c> column-diffing behavior.
/// </summary>
internal static class ManagedReplicaSchemaDdlText
{
    /// <summary>
    /// Structural shape of a <c>CREATE TABLE</c> statement: its column definitions (name plus
    /// full definition text), table-level constraints (verbatim text, e.g. a table-level
    /// <c>PRIMARY KEY</c>/<c>UNIQUE</c>/<c>CHECK</c>/<c>FOREIGN KEY</c> clause), and the
    /// presence of the <c>STRICT</c>/<c>WITHOUT ROWID</c> table options. Used to detect whether a
    /// remote schema refresh is purely additive (safe to apply via <c>ALTER TABLE ADD COLUMN</c>)
    /// or requires a full table rebuild that this replay engine does not implement.
    /// </summary>
    public readonly record struct TableShape(
        IReadOnlyList<(string Name, string Definition)> Columns,
        IReadOnlyList<string> TableConstraints,
        bool Strict,
        bool WithoutRowId);

    /// <summary>
    /// Splits the column/constraint list of a <c>CREATE TABLE</c> statement into individual
    /// column definitions, skipping table-level constraints (<c>PRIMARY KEY</c>, <c>UNIQUE</c>,
    /// <c>CHECK</c>, <c>FOREIGN KEY</c>, <c>CONSTRAINT</c>). Returns <see langword="null"/> when
    /// the statement's column-list parentheses cannot be located (e.g. <c>CREATE TABLE ... AS
    /// SELECT</c>).
    /// </summary>
    public static IReadOnlyList<(string Name, string Definition)>? SplitCreateTableColumns(string createTableSql)
        => TryGetCreateTableShape(createTableSql)?.Columns;

    /// <summary>
    /// Parses a <c>CREATE TABLE</c> statement's full structural shape (columns, table-level
    /// constraints, and <c>STRICT</c>/<c>WITHOUT ROWID</c> options). Returns <see langword="null"/>
    /// when the statement's column-list parentheses cannot be located (e.g. <c>CREATE TABLE ... AS
    /// SELECT</c>), in which case the statement cannot be safely diffed and must be replayed
    /// directly.
    /// </summary>
    public static TableShape? TryGetCreateTableShape(string createTableSql)
    {
        ArgumentNullException.ThrowIfNull(createTableSql);
        var index = SkipTrivia(createTableSql, 0);
        if (!MatchKeyword(createTableSql, ref index, "CREATE"))
            return null;
        _ = MatchKeyword(createTableSql, ref index, "TEMP") || MatchKeyword(createTableSql, ref index, "TEMPORARY");
        if (!MatchKeyword(createTableSql, ref index, "TABLE"))
            return null;
        if (MatchKeyword(createTableSql, ref index, "IF"))
        {
            if (!MatchKeyword(createTableSql, ref index, "NOT") || !MatchKeyword(createTableSql, ref index, "EXISTS"))
                return null;
        }

        // Table name: identifier, optionally schema-qualified.
        if (!TryReadIdentifier(createTableSql, index, out _, out index))
            return null;
        index = SkipTrivia(createTableSql, index);
        if (index < createTableSql.Length && createTableSql[index] == '.')
        {
            index++;
            if (!TryReadIdentifier(createTableSql, index, out _, out index))
                return null;
        }

        index = SkipTrivia(createTableSql, index);
        if (index >= createTableSql.Length || createTableSql[index] != '(')
            return null;

        var closeParen = FindMatchingParen(createTableSql, index);
        if (closeParen < 0)
            return null;

        var columns = new List<(string, string)>();
        var tableConstraints = new List<string>();
        foreach (var span in SplitTopLevelByComma(createTableSql, index + 1, closeParen))
        {
            var trimmed = TrimTrivia(createTableSql, span.Start, span.End);
            if (trimmed.Start >= trimmed.End)
                continue;

            var segmentText = createTableSql[trimmed.Start..trimmed.End];
            if (IsTableLevelConstraint(createTableSql, trimmed.Start))
            {
                tableConstraints.Add(segmentText);
                continue;
            }

            if (!TryReadIdentifier(createTableSql, trimmed.Start, out var name, out _))
                continue;

            columns.Add((name, segmentText));
        }

        var tail = createTableSql[(closeParen + 1)..];
        var strict = ContainsTopLevelKeyword(tail, "STRICT");
        var withoutRowId = ContainsTopLevelKeyword(tail, "WITHOUT") && ContainsTopLevelKeyword(tail, "ROWID");

        return new TableShape(columns, tableConstraints, strict, withoutRowId);
    }

    /// <summary>
    /// Rewrites a <c>CREATE TABLE</c> statement to include <c>IF NOT EXISTS</c> when not already
    /// present, mirroring Turso's <c>execute_ddl_idempotent</c> (which unconditionally sets
    /// <c>if_not_exists = true</c> on the parsed AST before executing). This keeps a bare
    /// <c>CREATE TABLE</c> replay idempotent under retry even when the table already exists.
    /// Returns the original text unchanged when it cannot be parsed as a recognized
    /// <c>CREATE TABLE</c> shape.
    /// </summary>
    public static string EnsureCreateTableIfNotExists(string createTableSql)
    {
        ArgumentNullException.ThrowIfNull(createTableSql);
        var index = SkipTrivia(createTableSql, 0);
        if (!MatchKeyword(createTableSql, ref index, "CREATE"))
            return createTableSql;
        _ = MatchKeyword(createTableSql, ref index, "TEMP") || MatchKeyword(createTableSql, ref index, "TEMPORARY");
        if (!MatchKeyword(createTableSql, ref index, "TABLE"))
            return createTableSql;

        var afterTable = index;
        var probe = index;
        if (MatchKeyword(createTableSql, ref probe, "IF"))
        {
            // Already has (or attempts) an IF [NOT EXISTS] clause; leave it exactly as-is either
            // way (a malformed "IF" without "NOT EXISTS" should surface its own parse error).
            return createTableSql;
        }

        return string.Concat(createTableSql.AsSpan(0, afterTable), " IF NOT EXISTS", createTableSql.AsSpan(afterTable));
    }

    /// <summary>
    /// Recognizes an <c>ALTER TABLE &lt;name&gt; ADD [COLUMN] &lt;definition&gt;</c> statement and
    /// returns its table name plus the new column's name and full definition text. Returns
    /// <see langword="null"/> for any other <c>ALTER TABLE</c> form (rename, drop column, etc.),
    /// which must be replayed directly rather than diffed for idempotency.
    /// </summary>
    public static (string TableName, string ColumnName, string ColumnDefinition)? TryParseAlterTableAddColumn(
        string alterSql)
    {
        ArgumentNullException.ThrowIfNull(alterSql);
        var index = SkipTrivia(alterSql, 0);
        if (!MatchKeyword(alterSql, ref index, "ALTER") || !MatchKeyword(alterSql, ref index, "TABLE"))
            return null;

        if (!TryReadIdentifier(alterSql, index, out var tableName, out index))
            return null;
        index = SkipTrivia(alterSql, index);
        if (index < alterSql.Length && alterSql[index] == '.')
        {
            index++;
            if (!TryReadIdentifier(alterSql, index, out tableName, out index))
                return null;
        }

        if (!MatchKeyword(alterSql, ref index, "ADD"))
            return null;
        _ = MatchKeyword(alterSql, ref index, "COLUMN");

        index = SkipTrivia(alterSql, index);
        var end = alterSql.Length;
        while (end > index && (char.IsWhiteSpace(alterSql[end - 1]) || alterSql[end - 1] == ';'))
            end--;
        if (index >= end)
            return null;
        if (!TryReadIdentifier(alterSql, index, out var columnName, out _))
            return null;

        return (tableName, columnName, alterSql[index..end]);
    }

    private static bool IsTableLevelConstraint(string sql, int index)
        => MatchKeywordNoAdvance(sql, index, "CONSTRAINT")
           || MatchKeywordNoAdvance(sql, index, "PRIMARY")
           || MatchKeywordNoAdvance(sql, index, "UNIQUE")
           || MatchKeywordNoAdvance(sql, index, "CHECK")
           || MatchKeywordNoAdvance(sql, index, "FOREIGN");

    private static bool MatchKeywordNoAdvance(string sql, int index, string keyword)
    {
        var probe = index;
        return MatchKeyword(sql, ref probe, keyword);
    }

    /// <summary>
    /// Scans <paramref name="sql"/> for a standalone occurrence of <paramref name="keyword"/> at
    /// any position (word-boundary on both sides; quoted tokens/comments are skipped over
    /// whole). Used to detect the <c>STRICT</c>/<c>WITHOUT ROWID</c> table options in the text
    /// following a <c>CREATE TABLE</c> statement's closing column-list parenthesis.
    /// </summary>
    private static bool ContainsTopLevelKeyword(string sql, string keyword)
    {
        var index = 0;
        while (index < sql.Length)
        {
            var precededByWordChar = index > 0 && (char.IsLetterOrDigit(sql[index - 1]) || sql[index - 1] == '_');
            if (!precededByWordChar)
            {
                var probe = index;
                if (MatchKeyword(sql, ref probe, keyword))
                    return true;
            }

            var (consumed, _) = ClassifyAt(sql, index, sql.Length);
            index += Math.Max(1, consumed);
        }

        return false;
    }

    private static bool MatchKeyword(string sql, ref int index, string keyword)
    {
        var start = SkipTrivia(sql, index);
        if (start + keyword.Length > sql.Length)
            return false;
        if (string.Compare(sql, start, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
            return false;
        var end = start + keyword.Length;
        if (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] == '_'))
            return false;

        index = end;
        return true;
    }

    internal static bool TryReadIdentifier(string sql, int index, out string identifier, out int nextIndex)
    {
        index = SkipTrivia(sql, index);
        if (index >= sql.Length)
        {
            identifier = string.Empty;
            nextIndex = index;
            return false;
        }

        var quoteChar = sql[index] switch
        {
            '"' => '"',
            '`' => '`',
            '[' => ']',
            _ => '\0',
        };

        if (quoteChar != '\0')
        {
            var scan = index + 1;
            var text = new System.Text.StringBuilder();
            while (scan < sql.Length)
            {
                if (sql[scan] == quoteChar)
                {
                    if (quoteChar != ']' && scan + 1 < sql.Length && sql[scan + 1] == quoteChar)
                    {
                        text.Append(quoteChar);
                        scan += 2;
                        continue;
                    }

                    identifier = text.ToString();
                    nextIndex = scan + 1;
                    return true;
                }

                text.Append(sql[scan]);
                scan++;
            }

            identifier = string.Empty;
            nextIndex = index;
            return false;
        }

        if (!char.IsLetter(sql[index]) && sql[index] != '_')
        {
            identifier = string.Empty;
            nextIndex = index;
            return false;
        }

        var end = index;
        while (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] == '_' || sql[end] == '$'))
            end++;

        identifier = sql[index..end];
        nextIndex = end;
        return true;
    }

    private static (int Start, int End) TrimTrivia(string sql, int start, int end)
    {
        while (start < end)
        {
            var next = SkipTrivia(sql, start);
            if (next == start)
                break;
            start = next;
        }

        while (end > start && char.IsWhiteSpace(sql[end - 1]))
            end--;
        return (start, end);
    }

    private static IEnumerable<(int Start, int End)> SplitTopLevelByComma(string sql, int start, int end)
    {
        var depth = 0;
        var segmentStart = start;
        var index = start;
        while (index < end)
        {
            var (consumed, kind) = ClassifyAt(sql, index, end);
            if (kind != LexicalKind.Other)
            {
                // Whitespace/comments and quoted identifiers/string literals are consumed as one
                // atomic unit: their internal characters (including '(', ')', ',') must never be
                // individually re-examined for structural paren/comma tracking.
                index += consumed;
                continue;
            }

            var c = sql[index];
            if (c == '(')
            {
                depth++;
                index++;
            }
            else if (c == ')')
            {
                depth--;
                index++;
            }
            else if (c == ',' && depth == 0)
            {
                yield return (segmentStart, index);
                segmentStart = index + 1;
                index++;
            }
            else
            {
                index += consumed;
            }
        }

        yield return (segmentStart, end);
    }

    /// <summary>
    /// Finds the index of the ')' matching the '(' at <paramref name="openParenIndex"/>, skipping
    /// over quoted identifiers, string literals, and comments. Returns -1 when unmatched.
    /// </summary>
    private static int FindMatchingParen(string sql, int openParenIndex)
    {
        var depth = 0;
        var index = openParenIndex;
        while (index < sql.Length)
        {
            var (consumed, kind) = ClassifyAt(sql, index, sql.Length);
            if (kind != LexicalKind.Other)
            {
                index += consumed;
                continue;
            }

            var c = sql[index];
            if (c == '(')
            {
                depth++;
                index++;
            }
            else if (c == ')')
            {
                depth--;
                index++;
                if (depth == 0)
                    return index - 1;
            }
            else
            {
                index += consumed;
            }
        }

        return -1;
    }

    /// <summary>
    /// Lexical classification for one scanned unit. <see cref="Whitespace"/> covers actual
    /// whitespace and comments (safe for <see cref="SkipTrivia"/> to skip when searching for the
    /// start of the next significant token). <see cref="QuotedToken"/> covers string literals and
    /// quoted/bracketed identifiers: these are structurally significant (must NOT be skipped by
    /// <see cref="SkipTrivia"/>, or an identifier reader positioned at the opening quote would
    /// never see it) but must still be consumed as one atomic span by paren/comma scanners so
    /// punctuation inside the quotes cannot be misread as SQL structure.
    /// </summary>
    private enum LexicalKind
    {
        Other,
        Whitespace,
        QuotedToken,
    }

    /// <summary>
    /// Classifies one lexical unit starting at <paramref name="index"/>. Comments are consumed
    /// whole as <see cref="LexicalKind.Whitespace"/>; string literals and quoted identifiers are
    /// consumed whole as <see cref="LexicalKind.QuotedToken"/>; any other single character is
    /// returned with <c>consumed = 1</c> and <see cref="LexicalKind.Other"/>.
    /// </summary>
    private static (int Consumed, LexicalKind Kind) ClassifyAt(string sql, int index, int end)
    {
        var c = sql[index];
        if (c is ' ' or '\t' or '\r' or '\n')
            return (1, LexicalKind.Whitespace);

        if (c == '-' && index + 1 < end && sql[index + 1] == '-')
        {
            var scan = index + 2;
            while (scan < end && sql[scan] != '\n')
                scan++;
            return (scan - index, LexicalKind.Whitespace);
        }

        if (c == '/' && index + 1 < end && sql[index + 1] == '*')
        {
            var scan = index + 2;
            while (scan + 1 < end && !(sql[scan] == '*' && sql[scan + 1] == '/'))
                scan++;
            scan = Math.Min(scan + 2, end);
            return (scan - index, LexicalKind.Whitespace);
        }

        // String literals ('...') and quoted identifiers ("...", `...`) share the same doubled-
        // delimiter escape rule (e.g. "a""b" / 'a''b' / `a``b`).
        if (c is '\'' or '"' or '`')
        {
            var scan = index + 1;
            while (scan < end)
            {
                if (sql[scan] == c)
                {
                    if (scan + 1 < end && sql[scan + 1] == c)
                    {
                        scan += 2;
                        continue;
                    }

                    scan++;
                    break;
                }

                scan++;
            }

            return (scan - index, LexicalKind.QuotedToken);
        }

        // Bracketed identifiers ([...]) have no escape convention in SQLite; ']' always closes.
        if (c == '[')
        {
            var scan = index + 1;
            while (scan < end && sql[scan] != ']')
                scan++;
            scan = Math.Min(scan + 1, end);
            return (scan - index, LexicalKind.QuotedToken);
        }

        return (1, LexicalKind.Other);
    }

    private static int SkipTrivia(string sql, int index)
    {
        while (index < sql.Length)
        {
            var (consumed, kind) = ClassifyAt(sql, index, sql.Length);
            if (kind != LexicalKind.Whitespace)
                return index;
            index += Math.Max(1, consumed);
        }

        return index;
    }
}
