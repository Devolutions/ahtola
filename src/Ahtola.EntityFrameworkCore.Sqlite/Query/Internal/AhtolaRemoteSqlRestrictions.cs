namespace Ahtola.EntityFrameworkCore.Sqlite.Query.Internal;

/// <summary>
/// Shared messaging and detection for SQL constructs that require a client-registered SQLite
/// function, aggregate, or collation (<c>regexp</c>, the decimal <c>ef_*</c> helper functions
/// and aggregates, and the <c>EF_DECIMAL</c> collation). Those registrations are only possible
/// for local Ahtola connections; direct remote Hrana and embedded-replica connections cannot
/// register them (see <see cref="Storage.Internal.AhtolaSqliteRelationalConnection"/>), so
/// translating one of these constructs for those connection modes must fail fast, during query
/// translation, with a precise diagnostic — not late, as an opaque "no such function" error
/// once the generated SQL reaches the server.
/// </summary>
internal static class AhtolaRemoteSqlRestrictions
{
    private static readonly HashSet<string> RestrictedFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "regexp",
        "ef_mod",
        "ef_add",
        "ef_divide",
        "ef_compare",
        "ef_multiply",
        "ef_negate",
        "ef_avg",
        "ef_max",
        "ef_min",
        "ef_sum",
    };

    public static bool IsRestrictedFunction(string name) => RestrictedFunctionNames.Contains(name);

    public static NotSupportedException ForFunction(string name) => new(
        $"The SQL function '{name}' requires a client-registered function that is only available for "
        + "local Ahtola connections. Regex.IsMatch(...), decimal arithmetic, and decimal aggregates "
        + "(Sum/Average/Max/Min) are not supported for direct remote Hrana or embedded replica connections; "
        + "use a local Ahtola connection (Local Provider=Managed or Native) instead.");

    public static NotSupportedException ForRegexp() => new(
        "Regex.IsMatch(...) translates to a client-registered 'regexp' SQL function that is only available "
        + "for local Ahtola connections. It is not supported for direct remote Hrana or embedded replica "
        + "connections; use a local Ahtola connection (Local Provider=Managed or Native) instead.");

    public static NotSupportedException ForDecimalOrdering() => new(
        "Ordering by a decimal value requires the client-registered 'EF_DECIMAL' collation, which is only "
        + "available for local Ahtola connections. It is not supported for direct remote Hrana or embedded "
        + "replica connections; use a local Ahtola connection (Local Provider=Managed or Native) instead, or "
        + "order by a projected surrogate value.");
}
