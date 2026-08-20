using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Sqlite.Query.Internal;

namespace Ahtola.EntityFrameworkCore.Sqlite.Query.Internal;

public sealed class AhtolaSqliteParameterBasedSqlProcessor(
    RelationalParameterBasedSqlProcessorDependencies dependencies,
    RelationalParameterBasedSqlProcessorParameters parameters,
    bool supportsProviderExtensionFunctions = true)
    : SqliteParameterBasedSqlProcessor(dependencies, parameters)
{
#if NET10_0_OR_GREATER
    protected override Expression ProcessSqlNullability(
        Expression queryExpression,
        ParametersCacheDecorator parametersDecorator)
        => new AhtolaSqliteSqlNullabilityProcessor(Dependencies, Parameters, supportsProviderExtensionFunctions)
            .Process(queryExpression, parametersDecorator);
#else
    protected override Expression ProcessSqlNullability(
        Expression queryExpression,
        IReadOnlyDictionary<string, object?> parametersValues,
        out bool canCache)
        => new AhtolaSqliteSqlNullabilityProcessor(Dependencies, Parameters, supportsProviderExtensionFunctions)
            .Process(queryExpression, parametersValues, out canCache);
#endif
}
