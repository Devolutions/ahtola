using Microsoft.EntityFrameworkCore.Query;

namespace Ahtola.EntityFrameworkCore.Sqlite.Query.Internal;

public sealed class AhtolaSqliteParameterBasedSqlProcessorFactory(
    RelationalParameterBasedSqlProcessorDependencies dependencies) : IRelationalParameterBasedSqlProcessorFactory
{
    public RelationalParameterBasedSqlProcessor Create(RelationalParameterBasedSqlProcessorParameters parameters)
        => new AhtolaSqliteParameterBasedSqlProcessor(dependencies, parameters);
}
/// <summary>
/// Used for direct remote Hrana and embedded-replica connections, neither of which can
/// register the client-side functions/collation that regexp, decimal arithmetic/aggregates,
/// and decimal ordering translate to; see <see cref="AhtolaRemoteSqlRestrictions"/>.
/// </summary>
public sealed class AhtolaRestrictedSqliteParameterBasedSqlProcessorFactory(
    RelationalParameterBasedSqlProcessorDependencies dependencies) : IRelationalParameterBasedSqlProcessorFactory
{
    public RelationalParameterBasedSqlProcessor Create(RelationalParameterBasedSqlProcessorParameters parameters)
        => new AhtolaSqliteParameterBasedSqlProcessor(dependencies, parameters, supportsProviderExtensionFunctions: false);
}
