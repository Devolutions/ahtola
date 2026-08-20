using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Sqlite.Internal;
using Microsoft.EntityFrameworkCore.Sqlite.Query.Internal;

namespace Ahtola.EntityFrameworkCore.Sqlite.Query.Internal;

public sealed class AhtolaSqliteQuerySqlGenerator(
    QuerySqlGeneratorDependencies dependencies,
    bool areJsonEachFunctionsSupported = true,
    bool supportsProviderExtensionFunctions = true) : SqliteQuerySqlGenerator(dependencies)
{
    private string? _unaliasedDmlTargetTableAlias;
    private readonly bool _areJsonEachFunctionsSupported = areJsonEachFunctionsSupported;
    private readonly bool _supportsProviderExtensionFunctions = supportsProviderExtensionFunctions;

    protected override Expression VisitDelete(DeleteExpression deleteExpression)
    {
        var previousAlias = _unaliasedDmlTargetTableAlias;
        _unaliasedDmlTargetTableAlias = deleteExpression.Table.Alias;
        try
        {
            return base.VisitDelete(deleteExpression);
        }
        finally
        {
            _unaliasedDmlTargetTableAlias = previousAlias;
        }
    }

    protected override Expression VisitUpdate(UpdateExpression updateExpression)
    {
        var previousAlias = _unaliasedDmlTargetTableAlias;
        _unaliasedDmlTargetTableAlias = updateExpression.SelectExpression.Tables.Count == 1
            ? updateExpression.Table.Alias
            : null;
        try
        {
            return base.VisitUpdate(updateExpression);
        }
        finally
        {
            _unaliasedDmlTargetTableAlias = previousAlias;
        }
    }

    protected override Expression VisitColumn(ColumnExpression columnExpression)
    {
        if (columnExpression.TableAlias == _unaliasedDmlTargetTableAlias)
        {
            Sql.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(columnExpression.Name));
            return columnExpression;
        }

        return base.VisitColumn(columnExpression);
    }

    protected override Expression VisitTable(TableExpression tableExpression)
    {
        if (tableExpression.Alias == _unaliasedDmlTargetTableAlias)
        {
            Sql.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(tableExpression.Name, tableExpression.Schema));
            return tableExpression;
        }

        return base.VisitTable(tableExpression);
    }

    protected override Expression VisitExtension(Expression extensionExpression)
    {
        if (!_areJsonEachFunctionsSupported && extensionExpression is JsonEachExpression)
            throw new InvalidOperationException(SqliteStrings.QueryingIntoJsonCollectionsNotSupported("3.38.0"));

        return base.VisitExtension(extensionExpression);
    }

    protected override Expression VisitOrdering(OrderingExpression orderingExpression)
    {
        if (ShouldUseDecimalCollation(orderingExpression.Expression))
        {
            if (!_supportsProviderExtensionFunctions)
                throw AhtolaRemoteSqlRestrictions.ForDecimalOrdering();

            var collatedExpression = new CollateExpression(orderingExpression.Expression, "EF_DECIMAL");
            return base.VisitOrdering(new OrderingExpression(collatedExpression, orderingExpression.IsAscending));
        }

        return base.VisitOrdering(orderingExpression);
    }

    private static bool ShouldUseDecimalCollation(SqlExpression expression)
    {
        if (expression is CollateExpression)
            return false;

        return IsDecimalType(expression.Type)
               || (expression.TypeMapping is not null && IsDecimalType(expression.TypeMapping.ClrType));
    }

    private static bool IsDecimalType(Type type)
        => (Nullable.GetUnderlyingType(type) ?? type) == typeof(decimal);
}
