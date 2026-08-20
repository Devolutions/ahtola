namespace Ahtola.Core.Parsing;

public sealed class SqlParameterMap
{
    public const int MaximumParameterCount = 250_000;

    private readonly string?[] _names;
    private readonly bool[] _referenced;
    private readonly Dictionary<string, int> _indices;

    private SqlParameterMap(string?[] names, bool[] referenced, Dictionary<string, int> indices)
    {
        _names = names;
        _referenced = referenced;
        _indices = indices;
    }

    public int Count => _names.Length - 1;

    public string? GetName(int index)
    {
        if (index < 1 || index > Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        return _names[index];
    }

    public bool IsReferenced(int index)
    {
        if (index < 1 || index > Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        return _referenced[index];
    }

    public bool TryGetIndex(string name, out int index)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _indices.TryGetValue(name, out index);
    }

    public static SqlParameterMap Parse(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var names = new List<string?> { null };
        var referenced = new List<bool> { false };
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var cursor = 0;

        while (cursor < sql.Length)
        {
            switch (sql[cursor])
            {
                case '\'':
                    cursor = SkipQuoted(sql, cursor, '\'', '\'');
                    continue;
                case '"':
                    cursor = SkipQuoted(sql, cursor, '"', '"');
                    continue;
                case '`':
                    cursor = SkipQuoted(sql, cursor, '`', '`');
                    continue;
                case '[':
                    cursor = SkipBracketQuoted(sql, cursor);
                    continue;
                case '-' when cursor + 1 < sql.Length && sql[cursor + 1] == '-':
                    cursor = SkipLineComment(sql, cursor + 2);
                    continue;
                case '/' when cursor + 1 < sql.Length && sql[cursor + 1] == '*':
                    cursor = SkipBlockComment(sql, cursor + 2);
                    continue;
                case '?':
                    cursor = AddQuestionParameter(sql, cursor, names, referenced, indices);
                    continue;
                case ':' or '@' or '$':
                    cursor = AddNamedParameter(sql, cursor, names, referenced, indices);
                    continue;
                default:
                    cursor++;
                    continue;
            }
        }

        return new SqlParameterMap(names.ToArray(), referenced.ToArray(), indices);
    }

    private static int AddQuestionParameter(
        string sql,
        int cursor,
        List<string?> names,
        List<bool> referenced,
        Dictionary<string, int> indices)
    {
        var end = cursor + 1;
        while (end < sql.Length && char.IsAsciiDigit(sql[end]))
            end++;

        if (end == cursor + 1)
        {
            EnsureParameterLimit(names.Count);
            names.Add(null);
            referenced.Add(true);
            return end;
        }

        if (!int.TryParse(sql.AsSpan(cursor + 1, end - cursor - 1), out var index) || index < 1)
            throw new FormatException($"Invalid numbered parameter at offset {cursor}.");

        EnsureParameterLimit(index);
        EnsureSlot(names, referenced, index);
        var name = sql[cursor..end];
        names[index] ??= name;
        referenced[index] = true;
        indices.TryAdd(name, index);
        return end;
    }

    private static int AddNamedParameter(
        string sql,
        int cursor,
        List<string?> names,
        List<bool> referenced,
        Dictionary<string, int> indices)
    {
        var end = ScanNamedParameterEnd(sql, cursor);

        if (end == cursor + 1)
            return end;

        var name = sql[cursor..end];
        if (!indices.ContainsKey(name))
        {
            EnsureParameterLimit(names.Count);
            var index = names.Count;
            names.Add(name);
            referenced.Add(true);
            indices.Add(name, index);
        }

        return end;
    }

    private static int ScanNamedParameterEnd(string sql, int cursor)
    {
        var end = cursor + 1;
        while (end < sql.Length && IsParameterIdentifierCharacter(sql[end]))
            end++;

        if (sql[cursor] != '$')
            return end;

        while (end + 1 < sql.Length && sql[end] == ':' && sql[end + 1] == ':')
        {
            end += 2;
            while (end < sql.Length && IsParameterIdentifierCharacter(sql[end]))
                end++;
        }

        if (end < sql.Length && sql[end] == '(')
        {
            end++;
            while (end < sql.Length && IsParameterIdentifierCharacter(sql[end]))
                end++;
            if (end < sql.Length && sql[end] == ')')
                end++;
        }

        return end;
    }

    private static void EnsureSlot(List<string?> names, List<bool> referenced, int index)
    {
        while (names.Count <= index)
        {
            names.Add(null);
            referenced.Add(false);
        }
    }

    private static void EnsureParameterLimit(int count)
    {
        if (count > MaximumParameterCount)
            throw new FormatException($"SQLite parameter index {count} exceeds the maximum of {MaximumParameterCount}.");
    }

    private static bool IsParameterIdentifierCharacter(char value)
        => char.IsAsciiLetterOrDigit(value) || value is '_' or '$';

    private static int SkipQuoted(string sql, int cursor, char delimiter, char escapedDelimiter)
    {
        cursor++;
        while (cursor < sql.Length)
        {
            if (sql[cursor] != delimiter)
            {
                cursor++;
                continue;
            }

            if (cursor + 1 < sql.Length && sql[cursor + 1] == escapedDelimiter)
            {
                cursor += 2;
                continue;
            }

            return cursor + 1;
        }

        return cursor;
    }

    private static int SkipBracketQuoted(string sql, int cursor)
    {
        cursor++;
        while (cursor < sql.Length && sql[cursor] != ']')
            cursor++;

        return cursor < sql.Length ? cursor + 1 : cursor;
    }

    private static int SkipLineComment(string sql, int cursor)
    {
        while (cursor < sql.Length && sql[cursor] is not '\r' and not '\n')
            cursor++;

        return cursor;
    }

    private static int SkipBlockComment(string sql, int cursor)
    {
        while (cursor + 1 < sql.Length)
        {
            if (sql[cursor] == '*' && sql[cursor + 1] == '/')
                return cursor + 2;

            cursor++;
        }

        return sql.Length;
    }
}
