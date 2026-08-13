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
