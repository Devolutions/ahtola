using Microsoft.EntityFrameworkCore.Query;

namespace Ahtola.EntityFrameworkCore.Sqlite.Query.Internal;

public sealed class AhtolaSqliteQuerySqlGeneratorFactory(QuerySqlGeneratorDependencies dependencies) : IQuerySqlGeneratorFactory
{
    public QuerySqlGenerator Create()
        => new AhtolaSqliteQuerySqlGenerator(dependencies);
}
public sealed class AhtolaManagedSqliteQuerySqlGeneratorFactory(
    QuerySqlGeneratorDependencies dependencies) : IQuerySqlGeneratorFactory
{
    public QuerySqlGenerator Create()
        => new AhtolaSqliteQuerySqlGenerator(dependencies, areJsonEachFunctionsSupported: false);
}

/// <summary>
/// Used for direct remote Hrana connections: standard SQLite/Turso SQL and JSON1 remain
/// enabled (the remote endpoint is a real SQLite-compatible server), but constructs requiring
/// a client-registered function/collation (regexp, decimal arithmetic/aggregates, decimal
/// ordering) are rejected with a precise diagnostic instead of reaching the server.
/// </summary>
public sealed class AhtolaRemoteSqliteQuerySqlGeneratorFactory(
    QuerySqlGeneratorDependencies dependencies) : IQuerySqlGeneratorFactory
{
    public QuerySqlGenerator Create()
        => new AhtolaSqliteQuerySqlGenerator(dependencies, areJsonEachFunctionsSupported: true, supportsProviderExtensionFunctions: false);
}

/// <summary>
/// Used for embedded-replica connections: JSON1 table-valued functions are disabled (queries
/// run against the local managed replica, same as <see cref="AhtolaManagedSqliteQuerySqlGeneratorFactory"/>),
/// and constructs requiring a client-registered function/collation are rejected the same way
/// as <see cref="AhtolaRemoteSqliteQuerySqlGeneratorFactory"/> (the replica connection cannot
/// register them either).
/// </summary>
public sealed class AhtolaReplicaSqliteQuerySqlGeneratorFactory(
    QuerySqlGeneratorDependencies dependencies) : IQuerySqlGeneratorFactory
{
    public QuerySqlGenerator Create()
        => new AhtolaSqliteQuerySqlGenerator(dependencies, areJsonEachFunctionsSupported: false, supportsProviderExtensionFunctions: false);
}
