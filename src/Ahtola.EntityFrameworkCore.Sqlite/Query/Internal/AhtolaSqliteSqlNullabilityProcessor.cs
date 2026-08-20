using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Sqlite.Query.Internal;

namespace Ahtola.EntityFrameworkCore.Sqlite.Query.Internal;

public sealed class AhtolaSqliteSqlNullabilityProcessor(
    RelationalParameterBasedSqlProcessorDependencies dependencies,
    RelationalParameterBasedSqlProcessorParameters parameters,
    bool supportsProviderExtensionFunctions = true)
    : SqliteSqlNullabilityProcessor(dependencies, parameters)
{
    protected override SqlExpression VisitCustomSqlExpression(
        SqlExpression sqlExpression,
        bool allowOptimizedExpansion,
        out bool nullable)
    {
        // Regex.IsMatch(...) translates to a RegexpExpression ("X REGEXP Y"), which requires a
        // client-registered 'regexp' SQL function that direct remote Hrana and embedded-replica
        // connections cannot register. Fail here, during translation, rather than emitting SQL
        // the endpoint will reject at execution time.
        if (!supportsProviderExtensionFunctions && sqlExpression is RegexpExpression)
            throw AhtolaRemoteSqlRestrictions.ForRegexp();

        return base.VisitCustomSqlExpression(sqlExpression, allowOptimizedExpansion, out nullable);
    }
    protected override SqlExpression VisitSqlFunction(
        SqlFunctionExpression sqlFunctionExpression,
        bool allowOptimizedExpansion,
        out bool nullable)
    {
        var result = base.VisitSqlFunction(sqlFunctionExpression, allowOptimizedExpansion, out nullable);

        // The base nullability processor wraps a nullable aggregate call (e.g. ef_sum/ef_avg
        // over an empty set) in COALESCE(call, default) for null-propagation, so the restricted
        // check below must look at the wrapped function's name, not "COALESCE" itself.
        if (result is SqlFunctionExpression
            {
                Name: "COALESCE",
                Arguments: [SqlFunctionExpression { IsBuiltIn: true } wrappedFunction, _],
            })
        {
            if (!supportsProviderExtensionFunctions && AhtolaRemoteSqlRestrictions.IsRestrictedFunction(wrappedFunction.Name))
                throw AhtolaRemoteSqlRestrictions.ForFunction(wrappedFunction.Name);

            if (string.Equals(wrappedFunction.Name, "ef_sum", StringComparison.OrdinalIgnoreCase))
            {
                nullable = false;
                return wrappedFunction;
            }

            return result;
        }

        // Decimal arithmetic/comparisons (ef_add/ef_divide/ef_compare/ef_multiply/ef_negate/ef_mod)
        // and decimal aggregates (ef_avg/ef_max/ef_min/ef_sum) all translate to calls to
        // client-registered SQL functions that direct remote Hrana and embedded-replica
        // connections cannot register.
        if (!supportsProviderExtensionFunctions
            && result is SqlFunctionExpression { IsBuiltIn: true, Name: var restrictedName }
            && AhtolaRemoteSqlRestrictions.IsRestrictedFunction(restrictedName))
        {
            throw AhtolaRemoteSqlRestrictions.ForFunction(restrictedName);
        }

        return result;
    }
}
