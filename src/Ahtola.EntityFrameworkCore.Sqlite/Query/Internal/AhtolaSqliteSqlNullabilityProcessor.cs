using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Sqlite.Query.Internal;

namespace Ahtola.EntityFrameworkCore.Sqlite.Query.Internal;

public sealed class AhtolaSqliteSqlNullabilityProcessor(
    RelationalParameterBasedSqlProcessorDependencies dependencies,
    RelationalParameterBasedSqlProcessorParameters parameters)
    : SqliteSqlNullabilityProcessor(dependencies, parameters)
{
    protected override SqlExpression VisitSqlFunction(
        SqlFunctionExpression sqlFunctionExpression,
        bool allowOptimizedExpansion,
        out bool nullable)
    {
        var result = base.VisitSqlFunction(sqlFunctionExpression, allowOptimizedExpansion, out nullable);
        if (result is SqlFunctionExpression
            {
                Name: "COALESCE",
                Arguments:
                [
                    SqlFunctionExpression
                {
                    IsBuiltIn: true,
                    Name: var name
                } function,
                    _
                ]
            }
            && string.Equals(name, "ef_sum", StringComparison.OrdinalIgnoreCase))
        {
            nullable = false;
            return function;
        }

        return result;
    }
}
