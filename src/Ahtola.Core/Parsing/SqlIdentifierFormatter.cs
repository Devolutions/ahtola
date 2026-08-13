namespace Ahtola.Core.Parsing;

/// <summary>
/// Formats identifiers for regenerated schema SQL (the text stored in <c>sqlite_schema.sql</c>).
/// SQLite quotes an identifier only when it cannot stand bare — anything that is not a plain
/// ASCII identifier, or that collides with a keyword — so synthesized DDL round-trips with the
/// same shape a hand-written statement would have (<c>CREATE TABLE t (b INTEGER)</c>, not
/// <c>CREATE TABLE "t" ("b" INTEGER)</c>).
/// </summary>
internal static class SqlIdentifierFormatter
{
    /// <summary>SQLite's keyword list (keywordhash.h); a bare keyword must be quoted in DDL.</summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ABORT", "ACTION", "ADD", "AFTER", "ALL", "ALTER", "ALWAYS", "ANALYZE", "AND", "AS",
        "ASC", "ATTACH", "AUTOINCREMENT", "BEFORE", "BEGIN", "BETWEEN", "BY", "CASCADE", "CASE",
        "CAST", "CHECK", "COLLATE", "COLUMN", "COMMIT", "CONFLICT", "CONSTRAINT", "CREATE",
        "CROSS", "CURRENT", "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "DATABASE",
        "DEFAULT", "DEFERRABLE", "DEFERRED", "DELETE", "DESC", "DETACH", "DISTINCT", "DO",
        "DROP", "EACH", "ELSE", "END", "ESCAPE", "EXCEPT", "EXCLUDE", "EXCLUSIVE", "EXISTS",
        "EXPLAIN", "FAIL", "FILTER", "FIRST", "FOLLOWING", "FOR", "FOREIGN", "FROM", "FULL",
        "GENERATED", "GLOB", "GROUP", "GROUPS", "HAVING", "IF", "IGNORE", "IMMEDIATE", "IN",
        "INDEX", "INDEXED", "INITIALLY", "INNER", "INSERT", "INSTEAD", "INTERSECT", "INTO",
        "IS", "ISNULL", "JOIN", "KEY", "LAST", "LEFT", "LIKE", "LIMIT", "MATCH", "MATERIALIZED",
        "NATURAL", "NO", "NOT", "NOTHING", "NOTNULL", "NULL", "NULLS", "OF", "OFFSET", "ON",
        "OR", "ORDER", "OTHERS", "OUTER", "OVER", "PARTITION", "PLAN", "PRAGMA", "PRECEDING",
        "PRIMARY", "QUERY", "RAISE", "RANGE", "RECURSIVE", "REFERENCES", "REGEXP", "REINDEX",
        "RELEASE", "RENAME", "REPLACE", "RESTRICT", "RETURNING", "RIGHT", "ROLLBACK", "ROW",
        "ROWS", "SAVEPOINT", "SELECT", "SET", "TABLE", "TEMP", "TEMPORARY", "THEN", "TIES",
        "TO", "TRANSACTION", "TRIGGER", "UNBOUNDED", "UNION", "UNIQUE", "UPDATE", "USING",
        "VACUUM", "VALUES", "VIEW", "VIRTUAL", "WHEN", "WHERE", "WINDOW", "WITH", "WITHOUT",
    };

    public static bool IsSqlKeyword(string identifier) => Keywords.Contains(identifier);

    public static bool NeedsQuoting(string identifier)
    {
        if (identifier.Length == 0)
            return true;
        if (!char.IsAsciiLetter(identifier[0]) && identifier[0] != '_')
            return true;
        for (var index = 1; index < identifier.Length; index++)
        {
            if (!char.IsAsciiLetterOrDigit(identifier[index]) && identifier[index] != '_')
                return true;
        }
        return Keywords.Contains(identifier);
    }

    public static string Quote(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public static string QuoteIfNeeded(string identifier)
        => NeedsQuoting(identifier) ? Quote(identifier) : identifier;
}
