namespace Ahtola.Core;

/// <summary>
/// The function names the managed engine implements itself, independent of connection-registered
/// callbacks. Persisted file schema (views, triggers) may reference these: the stored CREATE SQL
/// re-resolves to the same engine implementations after reopen. The names must mirror the
/// evaluator dispatch in <see cref="EmbeddedDatabase"/> (the scalar-function switch,
/// IsBuiltInAggregate, the managed percentile aggregates, and ValidateWindowFunction); tests pin
/// the two against each other.
/// </summary>
internal static class SqliteBuiltinFunctions
{
    private static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        // Built-in aggregates (IsBuiltInAggregate) and managed percentile aggregates.
        "COUNT", "SUM", "TOTAL", "AVG", "MIN", "MAX", "GROUP_CONCAT", "STRING_AGG", "ARRAY_AGG",
        "JSON_GROUP_ARRAY", "JSON_GROUP_OBJECT", "JSONB_GROUP_ARRAY", "JSONB_GROUP_OBJECT",
        "MEDIAN", "MODE", "PERCENTILE", "PERCENTILE_CONT", "PERCENTILE_DISC",
        // Built-in window functions (ValidateWindowFunction).
        "ROW_NUMBER", "RANK", "DENSE_RANK", "PERCENT_RANK", "CUME_DIST", "NTILE",
        "LAG", "LEAD", "FIRST_VALUE", "LAST_VALUE", "NTH_VALUE",
        // Built-in scalars (the EvaluateScalarFunction dispatch).
        "ABS", "CEIL", "CEILING", "FLOOR", "TRUNC", "ROUND", "LN", "LOG", "LOG2", "LOG10",
        "EXP", "SQRT", "POW", "POWER", "GCD", "LCM", "MOD", "SIGN", "PI", "DEGREES", "RADIANS",
        "SIN", "COS", "TAN", "ASIN", "ACOS", "ATAN", "ATAN2", "SINH", "COSH", "TANH",
        "ASINH", "ACOSH", "ATANH",
        "SUBSTR", "SUBSTRING", "REPLACE", "STRING_REVERSE", "REVERSE", "SOUNDEX", "REPEAT", "LPAD", "RPAD", "TRIM", "BTRIM", "LTRIM", "RTRIM", "QUOTE",
        "CHAR", "CHR", "UNICODE", "UNISTR", "UNISTR_QUOTE", "UNHEX", "ZEROBLOB", "RANDOMBLOB", "RANDOM", "CONCAT", "CONCAT_WS",
        "IF", "IIF", "LIKELY", "UNLIKELY", "LIKELIHOOD",
        "SQLITE_VERSION", "TURSO_VERSION", "SQLITE_SOURCE_ID", "CHANGES", "TOTAL_CHANGES", "TIMEDIFF",
        "TIME_DATE",
        "IS_AUTOCOMMIT",
        "COALESCE", "DATE", "DATETIME", "GLOB", "HEX", "IFNULL", "INSTR",
        "JSON", "JSONB", "JSON_ARRAY", "JSONB_ARRAY", "JSON_ARRAY_LENGTH", "JSON_ERROR_POSITION",
        "JSON_EXTRACT", "JSONB_EXTRACT", "JSON_INSERT", "JSONB_INSERT", "JSON_OBJECT", "JSONB_OBJECT",
        "JSON_PATCH", "JSONB_PATCH", "JSON_PRETTY", "JSON_QUOTE", "JSON_REMOVE", "JSONB_REMOVE",
        "JSON_REPLACE", "JSONB_REPLACE", "JSON_SET", "JSONB_SET", "JSON_TYPE", "JSON_VALID", "JULIANDAY",
        "LAST_INSERT_ROWID", "LENGTH", "CHAR_LENGTH", "CHARACTER_LENGTH", "LIKE", "LOWER", "NULLIF", "OCTET_LENGTH", "FORMAT", "PRINTF",
        "STRPOS",
        "STRFTIME", "TIME", "TYPEOF", "UNIXEPOCH", "UPPER",
        "UUID4_STR", "GEN_RANDOM_UUID", "UUID4", "UUID7_STR", "UUID7", "UUID7_TIMESTAMP_MS",
        "UUID_STR", "UUID_BLOB",
        "BOOLEAN_TO_INT", "INT_TO_BOOLEAN", "VALIDATE_IPADDR",
        "VECTOR", "VECTOR32", "VECTOR32_SPARSE", "VECTOR64", "VECTOR8", "VECTOR1BIT",
        "VECTOR_EXTRACT", "VECTOR_DISTANCE_COS", "VECTOR_DISTANCE_L2",
        "VECTOR_DISTANCE_JACCARD", "VECTOR_DISTANCE_DOT", "VECTOR_CONCAT", "VECTOR_SLICE",
        // Managed full-text index method surface (Ahtola.Core.Search.ManagedFtsFunctions).
        "FTS_MATCH", "FTS_SCORE", "FTS_HIGHLIGHT", "FTS_HIGHLIGHT_LEGACY", "FTS_SNIPPET",
        // SQLite FTS5 auxiliary functions. These require a row from an fts5 virtual table.
        "BM25", "HIGHLIGHT", "SNIPPET",
        // SQLite R-Tree diagnostics.
        "RTREECHECK", "RTREENODE", "RTREEDEPTH",
    };

    private static readonly HashSet<string> WindowOnlyNames = new(StringComparer.Ordinal)
    {
        "ROW_NUMBER", "RANK", "DENSE_RANK", "PERCENT_RANK", "CUME_DIST", "NTILE",
        "LAG", "LEAD", "FIRST_VALUE", "LAST_VALUE", "NTH_VALUE",
    };

    private static readonly HashSet<string> AggregateNames = new(StringComparer.Ordinal)
    {
        "COUNT", "SUM", "TOTAL", "AVG", "MIN", "MAX", "GROUP_CONCAT", "STRING_AGG", "ARRAY_AGG",
        "JSON_GROUP_ARRAY", "JSON_GROUP_OBJECT", "JSONB_GROUP_ARRAY", "JSONB_GROUP_OBJECT",
        "MEDIAN", "MODE", "PERCENTILE", "PERCENTILE_CONT", "PERCENTILE_DISC",
    };

    // Function names whose result can change between invocations even when the underlying
    // table data does not. Statement-scoped subquery memoization must never cache a query
    // that evaluates one of these, and index expressions prohibit them (mirroring SQLite,
    // which does not mark sqlite_version()/sqlite_source_id() with SQLITE_DETERMINISTIC).
    // The date/time family is excluded wholesale because each member accepts the 'now' time
    // string. Maintained alongside Names.
    private static readonly HashSet<string> NonDeterministic = new(StringComparer.Ordinal)
    {
        "RANDOM",
        "RANDOMBLOB",
        "CHANGES",
        "TOTAL_CHANGES",
        "LAST_INSERT_ROWID",
        "IS_AUTOCOMMIT",
        "SQLITE_VERSION",
        "TURSO_VERSION",
        "SQLITE_SOURCE_ID",
        "DATE",
        "DATETIME",
        "TIME",
        "JULIANDAY",
        "STRFTIME",
        "UNIXEPOCH",
        "TIMEDIFF",
        "UUID4_STR",
        "GEN_RANDOM_UUID",
        "UUID4",
        "UUID7_STR",
        "UUID7",
        "UUID7_TIMESTAMP_MS",
        "UUID_STR",
        "UUID_BLOB",
        "FTS_SCORE",
        "BM25",
        "HIGHLIGHT",
        "SNIPPET",
        "RTREECHECK",
    };

    public static bool Contains(string name)
        => Names.Contains(name.ToUpperInvariant());

    // A built-in is deterministic (memoizable) when it resolves to a recognized scalar
    // whose result depends only on its arguments. Aggregates and the non-deterministic set
    // above do not qualify; MIN/MAX are overloaded and also have deterministic scalar forms.
    public static bool IsDeterministic(string name)
    {
        var normalized = name.ToUpperInvariant();
        return Names.Contains(normalized)
            // MIN/MAX resolve to deterministic scalar functions with two or more arguments;
            // their one-argument aggregate overloads are rejected before this lookup.
            && (!AggregateNames.Contains(normalized) || normalized is "MIN" or "MAX")
            && !NonDeterministic.Contains(normalized);
    }

    /// <summary>Window-only names error without an OVER clause; parity tests skip them.</summary>
    public static bool IsWindowOnly(string name)
        => WindowOnlyNames.Contains(name.ToUpperInvariant());

    public static bool IsAggregate(string name)
        => AggregateNames.Contains(name.ToUpperInvariant());

    public static bool IsExposedByFunctionList(string name)
        => name.ToUpperInvariant() is not ("PERCENT_RANK" or "CUME_DIST" or "IS_AUTOCOMMIT");

    public static bool AcceptsCall(string name, int argumentCount, bool star)
    {
        var normalized = name.ToUpperInvariant();
        if (!Names.Contains(normalized))
            return false;
        if (star && normalized == "COUNT")
            return argumentCount == 0;

        var arities = GetArities(normalized);
        if (!arities.Contains(-1))
            return arities.Contains(argumentCount);

        return normalized switch
        {
            "COALESCE" => argumentCount >= 2,
            "CONCAT" => argumentCount >= 1,
            "CONCAT_WS" or "IF" or "IIF" => argumentCount >= 2,
            "MIN" or "MAX" => argumentCount >= 1,
            "JSON_OBJECT" or "JSONB_OBJECT" => (argumentCount & 1) == 0,
            "JSON_INSERT" or "JSONB_INSERT"
                or "JSON_REPLACE" or "JSONB_REPLACE"
                or "JSON_SET" or "JSONB_SET" => argumentCount >= 1 && (argumentCount & 1) != 0,
            "JSON_REMOVE" or "JSONB_REMOVE" => argumentCount >= 1,
            _ => true,
        };
    }

    public static IReadOnlyList<int> GetArities(string name)
    {
        var normalized = name.ToUpperInvariant();
        if (normalized == "COUNT")
            return [0, 1];
        if (normalized == "GROUP_CONCAT")
            return [1, 2];
        if (normalized == "TIME_DATE")
            return [3, 6, 7, 8];
        if (normalized is "UUID7_STR" or "UUID7")
            return [0, 1];
        if (normalized is "LAG" or "LEAD")
            return [1, 2, 3];
        if (normalized is "LIKE" or "SUBSTR" or "SUBSTRING" or "LPAD" or "RPAD")
            return [2, 3];
        if (normalized is "TRIM" or "BTRIM" or "LTRIM" or "RTRIM" or "ROUND" or "LOG"
            or "UNHEX" or "JSON_ARRAY_LENGTH" or "JSON_TYPE" or "JSON_PRETTY" or "RTREECHECK")
        {
            return [1, 2];
        }

        if (normalized is "ROW_NUMBER" or "RANK" or "DENSE_RANK" or "PERCENT_RANK" or "CUME_DIST"
            or "PI" or "RANDOM" or "SQLITE_VERSION" or "TURSO_VERSION" or "SQLITE_SOURCE_ID"
            or "CHANGES" or "TOTAL_CHANGES" or "LAST_INSERT_ROWID" or "UUID4_STR"
            or "IS_AUTOCOMMIT"
            or "GEN_RANDOM_UUID" or "UUID4" or "UUID7_STR" or "UUID7")
        {
            return [0];
        }

        if (normalized is "STRING_AGG" or "JSON_GROUP_OBJECT" or "JSONB_GROUP_OBJECT"
            or "PERCENTILE" or "PERCENTILE_CONT" or "PERCENTILE_DISC" or "NTH_VALUE"
            or "ATAN2" or "POW" or "POWER" or "GCD" or "LCM" or "MOD" or "REPEAT"
            or "GLOB" or "INSTR" or "STRPOS" or "NULLIF" or "IFNULL" or "LIKELIHOOD" or "TIMEDIFF"
            or "JSON_PATCH" or "JSONB_PATCH"
            or "VECTOR_DISTANCE_COS" or "VECTOR_DISTANCE_L2" or "VECTOR_DISTANCE_JACCARD"
            or "VECTOR_DISTANCE_DOT" or "RTREENODE")
        {
            return [2];
        }

        if (normalized is "REPLACE" or "VECTOR_SLICE")
            return [3];

        if (normalized is "HIGHLIGHT" or "FTS_HIGHLIGHT_LEGACY")
            return [4];

        if (normalized is "FTS_SNIPPET" or "SNIPPET")
            return [6];

        if (normalized is "FTS_MATCH" or "FTS_SCORE" or "FTS_HIGHLIGHT" or "BM25")
            return [-1];

        if (normalized is "COALESCE" or "CHAR" or "CHR" or "CONCAT" or "CONCAT_WS" or "IF" or "IIF" or "FORMAT"
            or "PRINTF" or "DATE" or "DATETIME" or "TIME" or "JULIANDAY" or "STRFTIME"
            or "UNIXEPOCH" or "MIN" or "MAX" or "JSON_ARRAY" or "JSONB_ARRAY"
            or "JSON_OBJECT" or "JSONB_OBJECT" or "JSON_EXTRACT" or "JSONB_EXTRACT"
            or "JSON_INSERT" or "JSONB_INSERT" or "JSON_REMOVE" or "JSONB_REMOVE"
            or "JSON_REPLACE" or "JSONB_REPLACE" or "JSON_SET" or "JSONB_SET"
            or "VECTOR_CONCAT")
        {
            return [-1];
        }

        return [1];
    }

    /// <summary>Exposed for evaluator-dispatch parity tests.</summary>
    public static IReadOnlyCollection<string> All => Names;
}
