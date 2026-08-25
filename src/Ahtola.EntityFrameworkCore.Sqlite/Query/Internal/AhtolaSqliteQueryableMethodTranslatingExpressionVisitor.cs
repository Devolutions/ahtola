// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Sqlite.Internal;
using Microsoft.EntityFrameworkCore.Sqlite.Query.Internal;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ahtola.EntityFrameworkCore.Sqlite.Query.Internal;

/// <summary>
///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
///     the same compatibility standards as public APIs. It may be changed or removed without notice in
///     any release. You should only use it directly in your code with extreme caution and knowing that
///     doing so can result in application failures when updating to a new Entity Framework Core release.
/// </summary>
public sealed class AhtolaSqliteQueryableMethodTranslatingExpressionVisitor : RelationalQueryableMethodTranslatingExpressionVisitor
{
    private readonly IRelationalTypeMappingSource _typeMappingSource;
    private readonly SqliteSqlExpressionFactory _sqlExpressionFactory;
    private readonly SqlAliasManager _sqlAliasManager;
    private readonly bool _areJsonEachFunctionsSupported;

    /// <summary>
    ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///     any release. You should only use it directly in your code with extreme caution and knowing that
    ///     doing so can result in application failures when updating to a new Entity Framework Core release.
    /// </summary>
    [EntityFrameworkInternal] // https://www.sqlite.org/json1.html#jeach
    public const string JsonEachKeyColumnName = "key";

    /// <summary>
    ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///     any release. You should only use it directly in your code with extreme caution and knowing that
    ///     doing so can result in application failures when updating to a new Entity Framework Core release.
    /// </summary>
    [EntityFrameworkInternal] // https://www.sqlite.org/json1.html#jeach
    public const string JsonEachValueColumnName = "value";

    /// <summary>
    ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///     any release. You should only use it directly in your code with extreme caution and knowing that
    ///     doing so can result in application failures when updating to a new Entity Framework Core release.
    /// </summary>
    public AhtolaSqliteQueryableMethodTranslatingExpressionVisitor(
        QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
        RelationalQueryableMethodTranslatingExpressionVisitorDependencies relationalDependencies,
        RelationalQueryCompilationContext queryCompilationContext,
        bool areJsonEachFunctionsSupported)
        : base(dependencies, relationalDependencies, queryCompilationContext)
    {
        _typeMappingSource = relationalDependencies.TypeMappingSource;
        _sqlExpressionFactory = (SqliteSqlExpressionFactory)relationalDependencies.SqlExpressionFactory;
        _sqlAliasManager = queryCompilationContext.SqlAliasManager;

        _areJsonEachFunctionsSupported = areJsonEachFunctionsSupported;
    }

    /// <summary>
    ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///     any release. You should only use it directly in your code with extreme caution and knowing that
    ///     doing so can result in application failures when updating to a new Entity Framework Core release.
    /// </summary>
    private AhtolaSqliteQueryableMethodTranslatingExpressionVisitor(
        AhtolaSqliteQueryableMethodTranslatingExpressionVisitor parentVisitor)
        : base(parentVisitor)
    {
        _typeMappingSource = parentVisitor._typeMappingSource;
        _sqlExpressionFactory = parentVisitor._sqlExpressionFactory;
        _sqlAliasManager = parentVisitor._sqlAliasManager;

        _areJsonEachFunctionsSupported = parentVisitor._areJsonEachFunctionsSupported;
    }

    /// <summary>
    ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///     any release. You should only use it directly in your code with extreme caution and knowing that
    ///     doing so can result in application failures when updating to a new Entity Framework Core release.
    /// </summary>
    protected override QueryableMethodTranslatingExpressionVisitor CreateSubqueryVisitor()
        => new AhtolaSqliteQueryableMethodTranslatingExpressionVisitor(this);

    /// <summary>
    ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///     any release. You should only use it directly in your code with extreme caution and knowing that
    ///     doing so can result in application failures when updating to a new Entity Framework Core release.
    /// </summary>
    protected override ShapedQueryExpression? TranslateAny(ShapedQueryExpression source, LambdaExpression? predicate)
    {
        // Simplify x.Array.Any() => json_array_length(x.Array) > 0 instead of WHERE EXISTS (SELECT 1 FROM json_each(x.Array))
        if (predicate is null
            && source.QueryExpression is SelectExpression
            {
                Tables: [TableValuedFunctionExpression { Name: "json_each", Schema: null, IsBuiltIn: true, Arguments: [var array] }],
                Predicate: null,
                GroupBy: [],
                Having: null,
                IsDistinct: false,
                Limit: null,
                Offset: null
            })
        {
            var translation =
                _sqlExpressionFactory.GreaterThan(
                    _sqlExpressionFactory.Function(
                        "json_array_length",
                        new[] { array },
                        nullable: true,
                        argumentsPropagateNullability: new[] { true },
                        typeof(int)),
                    _sqlExpressionFactory.Constant(0));

#pragma warning disable EF1001
            return source.UpdateQueryExpression(new SelectExpression(translation, _sqlAliasManager));
#pragma warning restore EF1001
        }

        return base.TranslateAny(source, predicate);
    }

    /// <summary>
    ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///     any release. You should only use it directly in your code with extreme caution and knowing that
    ///     doing so can result in application failures when updating to a new Entity Framework Core release.
    /// </summary>
    protected override ShapedQueryExpression? TranslateOrderBy(
        ShapedQueryExpression source,
        LambdaExpression keySelector,
        bool ascending)
    {
        var translation = base.TranslateOrderBy(source, keySelector, ascending);
        if (translation == null)
        {
            return null;
        }

        var orderingExpression = ((SelectExpression)translation.QueryExpression).Orderings.Last();
        var orderingExpressionType = GetProviderType(orderingExpression.Expression);
        if (orderingExpressionType == typeof(DateTimeOffset)
            || orderingExpressionType == typeof(TimeSpan)
            || orderingExpressionType == typeof(ulong))
        {
            throw new NotSupportedException(
                SqliteStrings.OrderByNotSupported(orderingExpressionType.ShortDisplayName()));
        }

        return translation;
    }

    /// <summary>
    ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///     any release. You should only use it directly in your code with extreme caution and knowing that
    ///     doing so can result in application failures when updating to a new Entity Framework Core release.
    /// </summary>
    protected override ShapedQueryExpression? TranslateThenBy(
        ShapedQueryExpression source,
        LambdaExpression keySelector,
        bool ascending)
    {
        var translation = base.TranslateThenBy(source, keySelector, ascending);
        if (translation == null)
        {
            return null;
        }

        var orderingExpression = ((SelectExpression)translation.QueryExpression).Orderings.Last();
        var orderingExpressionType = GetProviderType(orderingExpression.Expression);
        if (orderingExpressionType == typeof(DateTimeOffset)
            || orderingExpressionType == typeof(TimeSpan)
            || orderingExpressionType == typeof(ulong))
        {
            throw new NotSupportedException(
                SqliteStrings.OrderByNotSupported(orderingExpressionType.ShortDisplayName()));
        }

        return translation;
    }

    /// <summary>
    ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///     any release. You should only use it directly in your code with extreme caution and knowing that
    ///     doing so can result in application failures when updating to a new Entity Framework Core release.
    /// </summary>
    protected override ShapedQueryExpression? TranslateCount(ShapedQueryExpression source, LambdaExpression? predicate)
    {
        // Simplify x.Array.Count() => json_array_length(x.Array) instead of SELECT COUNT(*) FROM json_each(x.Array)
        if (predicate is null
            && source.QueryExpression is SelectExpression
            {
                Tables: [TableValuedFunctionExpression { Name: "json_each", Schema: null, IsBuiltIn: true, Arguments: [var array] }],
                Predicate: null,
                GroupBy: [],
                Having: null,
                IsDistinct: false,
                Limit: null,
                Offset: null
            })
        {
            var translation = _sqlExpressionFactory.Function(
                "json_array_length",
                new[] { array },
                nullable: true,
                argumentsPropagateNullability: new[] { true },
                typeof(int));

#pragma warning disable EF1001
            return source.UpdateQueryExpression(new SelectExpression(translation, _sqlAliasManager));
#pragma warning restore EF1001
        }

        return base.TranslateCount(source, predicate);
    }

    /// <summary>
    ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///     any release. You should only use it directly in your code with extreme caution and knowing that
    ///     doing so can result in application failures when updating to a new Entity Framework Core release.
    /// </summary>
    protected override ShapedQueryExpression? TranslatePrimitiveCollection(
        SqlExpression sqlExpression,
        IProperty? property,
        string tableAlias)
    {
        var elementTypeMapping = (RelationalTypeMapping?)sqlExpression.TypeMapping?.ElementTypeMapping;
        var elementClrType = GetSequenceType(sqlExpression.Type);
        var jsonEachExpression = new JsonEachExpression(tableAlias, sqlExpression);

        // If this is a collection property, get the element's nullability out of metadata. Otherwise, this is a parameter property, in
        // which case we only have the CLR type (note that we cannot produce different SQLs based on the nullability of an *element* in
        // a parameter collection - our caching mechanism only supports varying by the nullability of the parameter itself (i.e. the
        // collection).
        var isElementNullable = property?.GetElementType()!.IsNullable;

        var keyColumnTypeMapping = _typeMappingSource.FindMapping(typeof(int))!;

#pragma warning disable EF1001 // Internal EF Core API usage.
        var selectExpression = new SelectExpression(
            [jsonEachExpression],
            new ColumnExpression(
                JsonEachValueColumnName,
                tableAlias,
                UnwrapNullableType(elementClrType),
                elementTypeMapping,
                isElementNullable ?? IsNullableType(elementClrType)),
            identifier:
            [
                (new ColumnExpression(JsonEachKeyColumnName, tableAlias, typeof(int), keyColumnTypeMapping, nullable: false),
                    keyColumnTypeMapping.Comparer)
            ],
            _sqlAliasManager);
#pragma warning restore EF1001 // Internal EF Core API usage.

        // If we have a collection column, we know the type mapping at this point (as opposed to parameters, whose type mapping will get
        // inferred later based on usage in SqliteInferredTypeMappingApplier); we should be able to apply any SQL logic needed to convert
        // the JSON value out to its relational counterpart (e.g. datetime() for timestamps, see ApplyJsonSqlConversion).
        //
        // However, doing it here would interfere with pattern matching in e.g. TranslateElementAtOrDefault, where we specifically check
        // for a bare column being projected out of the table - if the user composed any operators over the collection, it's no longer
        // possible to apply a specialized translation via the -> operator. We could add a way to recognize the special conversions we
        // compose on top, but instead of going into that complexity, we'll just apply the SQL conversion later, in
        // SqliteInferredTypeMappingApplier, as if we had a parameter collection.

        // Append an ordering for the json_each 'key' column.
        selectExpression.AppendOrdering(
            new OrderingExpression(
                selectExpression.CreateColumnExpression(
                    jsonEachExpression,
                    JsonEachKeyColumnName,
                    typeof(int),
                    typeMapping: _typeMappingSource.FindMapping(typeof(int)),
                    columnNullable: false),
                ascending: true));

        var nullableElementClrType = MakeNullable(elementClrType);
        Expression shaperExpression = new ProjectionBindingExpression(
            selectExpression, new ProjectionMember(), nullableElementClrType);

        if (elementClrType != nullableElementClrType)
        {
            Debug.Assert(
                nullableElementClrType == shaperExpression.Type,
                "expression.Type must be nullable of targetType");

            shaperExpression = Expression.Convert(shaperExpression, elementClrType);
        }

        return new ShapedQueryExpression(selectExpression, shaperExpression);
    }

    /// <summary>
    ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///     any release. You should only use it directly in your code with extreme caution and knowing that
    ///     doing so can result in application failures when updating to a new Entity Framework Core release.
    /// </summary>
    protected override ShapedQueryExpression TransformJsonQueryToTable(JsonQueryExpression jsonQueryExpression)
    {
        if (!_areJsonEachFunctionsSupported)
            throw new InvalidOperationException(SqliteStrings.QueryingIntoJsonCollectionsNotSupported("3.38.0"));

    #if NET10_0_OR_GREATER
            var structuralType = jsonQueryExpression.StructuralType;
    #else
            var structuralType = (ITypeBase)jsonQueryExpression.EntityType;
    #endif
            var textTypeMapping = _typeMappingSource.FindMapping(typeof(string));

            // TODO: Refactor this out
            // Calculate the table alias for the json_each expression based on the last named path segment
            // (or the JSON column name if there are none)
            var lastNamedPathSegment = jsonQueryExpression.Path.LastOrDefault(ps => ps.PropertyName is not null);
            var tableAlias = _sqlAliasManager.GenerateTableAlias(lastNamedPathSegment.PropertyName ?? jsonQueryExpression.JsonColumn.Name);

            // Handling a non-primitive JSON array is complicated on SQLite; unlike SQL Server OPENJSON and PostgreSQL jsonb_to_recordset,
            // SQLite's json_each can only project elements of the array, and not properties within those elements. For example:
            // SELECT value FROM json_each('[{"a":1,"b":"foo"}, {"a":2,"b":"bar"}]')
            // This will return two rows, each with a string column representing an array element (i.e. {"a":1,"b":"foo"}). To decompose that
            // into a and b columns, a further extraction is needed:
            // SELECT value ->> 'a' AS a, value ->> 'b' AS b FROM json_each('[{"a":1,"b":"foo"}, {"a":2,"b":"bar"}]')

            // We therefore generate a minimal subquery projecting out all the properties and navigations, wrapped by a SelectExpression
            // containing that:
            // SELECT ...
            // FROM (SELECT value ->> 'a' AS a, value ->> 'b' AS b FROM json_each(<JSON column>, <path>)) AS j
            // WHERE j.a = 8;

            // Unfortunately, while the subquery projects the entity, our EntityProjectionExpression currently supports only bare
            // ColumnExpression (the above requires JsonScalarExpression). So we hack as if the subquery projects an anonymous type instead,
            // with a member for each JSON property that needs to be projected. We then wrap it with a SelectExpression the projects a proper
            // EntityProjectionExpression.

            var jsonEachExpression = new JsonEachExpression(tableAlias, jsonQueryExpression.JsonColumn, jsonQueryExpression.Path);

    #pragma warning disable EF1001 // Internal EF Core API usage.
            var selectExpression = CreateSelect(
                jsonQueryExpression,
                jsonEachExpression,
                JsonEachKeyColumnName,
                typeof(int),
                _typeMappingSource.FindMapping(typeof(int))!);
    #pragma warning restore EF1001 // Internal EF Core API usage.

            selectExpression.AppendOrdering(
                new OrderingExpression(
                    selectExpression.CreateColumnExpression(
                        jsonEachExpression,
                        JsonEachKeyColumnName,
                        typeof(int),
                        typeMapping: _typeMappingSource.FindMapping(typeof(int)),
                        columnNullable: false),
                    ascending: true));

            var propertyJsonScalarExpression = new Dictionary<ProjectionMember, Expression>();

            var jsonColumn = selectExpression.CreateColumnExpression(
                jsonEachExpression, JsonEachValueColumnName, typeof(string), _typeMappingSource.FindMapping(typeof(string))); // TODO: nullable?

            // First step: build a SelectExpression that will execute json_each and project all properties and navigations out, e.g.
            // (SELECT value ->> 'a' AS a, value ->> 'b' AS b FROM json_each(c."JsonColumn", '$.Something.SomeCollection')

            // We're only interested in properties which actually exist in the JSON, filter out uninteresting synthetic keys
            foreach (var property in structuralType.GetPropertiesInHierarchy())
            {
                if (property.GetJsonPropertyName() is string jsonPropertyName)
                {
                    // HACK: currently the only way to project multiple values from a SelectExpression is to simulate a Select out to an anonymous
                    // type; this requires the MethodInfos of the anonymous type properties, from which the projection alias gets taken.
                    // So we create fake members to hold the JSON property name for the alias.
                    var projectionMember = new ProjectionMember().Append(new FakeMemberInfo(jsonPropertyName));

                    propertyJsonScalarExpression[projectionMember] = new JsonScalarExpression(
                        jsonColumn,
                        new[] { new PathSegment(property.GetJsonPropertyName()!) },
                        UnwrapNullableType(property.ClrType),
                        property.GetRelationalTypeMapping(),
                        property.IsNullable);
                }
            }

            if (structuralType is IEntityType entityType)
            {
                foreach (var navigation in entityType.GetNavigationsInHierarchy()
                             .Where(
                                 n => n.ForeignKey.IsOwnership
                                     && n.TargetEntityType.IsMappedToJson()
                                     && n.ForeignKey.PrincipalToDependent == n))
                {
                    var jsonNavigationName = navigation.TargetEntityType.GetJsonPropertyName();
                    Debug.Assert(jsonNavigationName is not null, "Invalid navigation found on JSON-mapped entity");

                    var projectionMember = new ProjectionMember().Append(new FakeMemberInfo(jsonNavigationName));

                    propertyJsonScalarExpression[projectionMember] = new JsonScalarExpression(
                        jsonColumn,
                        new[] { new PathSegment(jsonNavigationName) },
                        typeof(string),
                        textTypeMapping,
                        !navigation.ForeignKey.IsRequiredDependent);
                }
            }

    #if NET10_0_OR_GREATER
            foreach (var complexProperty in structuralType.GetComplexProperties())
            {
                var jsonNavigationName = complexProperty.ComplexType.GetJsonPropertyName();
                Debug.Assert(jsonNavigationName is not null, "Invalid complex property found on JSON-mapped structural type");

                var projectionMember = new ProjectionMember().Append(new FakeMemberInfo(jsonNavigationName));

                propertyJsonScalarExpression[projectionMember] = new JsonScalarExpression(
                    jsonColumn,
                    new[] { new PathSegment(jsonNavigationName) },
                    typeof(string),
                    textTypeMapping,
                    jsonQueryExpression.IsNullable || complexProperty.IsNullable);
            }
    #endif

            selectExpression.ReplaceProjection(propertyJsonScalarExpression);

            // Second step: push the above SelectExpression down to a subquery, and project an entity projection from the outer
            // SelectExpression, i.e.
            // SELECT "t"."a", "t"."b"
            // FROM (SELECT value ->> 'a' ... FROM json_each(...))

            selectExpression.PushdownIntoSubquery();
            var subquery = selectExpression.Tables[0];

    #pragma warning disable EF1001 // Internal EF Core API usage.
            var newOuterSelectExpression = CreateSelect(
                jsonQueryExpression,
                subquery,
                JsonEachKeyColumnName,
                typeof(int),
                _typeMappingSource.FindMapping(typeof(int))!);
    #pragma warning restore EF1001 // Internal EF Core API usage.

            newOuterSelectExpression.AppendOrdering(
                new OrderingExpression(
                    selectExpression.CreateColumnExpression(
                        subquery,
                        JsonEachKeyColumnName,
                        typeof(int),
                        typeMapping: _typeMappingSource.FindMapping(typeof(int)),
                        columnNullable: false),
                    ascending: true));

            return new ShapedQueryExpression(
                newOuterSelectExpression,
                new RelationalStructuralTypeShaperExpression(
    #if NET10_0_OR_GREATER
                    jsonQueryExpression.StructuralType,
    #else
                    jsonQueryExpression.EntityType,
    #endif
                    new ProjectionBindingExpression(
                        newOuterSelectExpression,
                        new ProjectionMember(),
                        typeof(ValueBuffer)),
                    false));
        }

    /// <summary>
    ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///     any release. You should only use it directly in your code with extreme caution and knowing that
    ///     doing so can result in application failures when updating to a new Entity Framework Core release.
    /// </summary>
    protected override ShapedQueryExpression? TranslateElementAtOrDefault(
        ShapedQueryExpression source,
        Expression index,
        bool returnDefault)
    {
        if (!returnDefault
            && source.QueryExpression is SelectExpression
            {
                Tables:
                [
                    TableValuedFunctionExpression
                {
                    Name: "json_each", Schema: null, IsBuiltIn: true, Arguments: [var jsonArrayColumn]
                } jsonEachExpression
                ],
                Predicate: null,
                GroupBy: [],
                Having: null,
                IsDistinct: false,
                Orderings: [{ Expression: ColumnExpression { Name: JsonEachKeyColumnName } orderingColumn, IsAscending: true }],
                Limit: null,
                Offset: null
            } selectExpression
            && orderingColumn.TableAlias == jsonEachExpression.Alias
            && TranslateExpression(index) is { } translatedIndex)
        {
            // Index on JSON array

            // Extract the column projected out of the source, and simplify the subquery to a simple JsonScalarExpression
            var shaperExpression = source.ShaperExpression;
            if (shaperExpression is UnaryExpression { NodeType: ExpressionType.Convert } unaryExpression
                && IsNullableType(unaryExpression.Operand.Type)
                && UnwrapNullableType(unaryExpression.Operand.Type) == unaryExpression.Type)
            {
                shaperExpression = unaryExpression.Operand;
            }

            if (shaperExpression is ProjectionBindingExpression projectionBindingExpression
                && selectExpression.GetProjection(projectionBindingExpression) is ColumnExpression projectionColumn)
            {
                SqlExpression translation = new JsonScalarExpression(
                    jsonArrayColumn,
                    new[] { new PathSegment(translatedIndex) },
                    projectionColumn.Type,
                    projectionColumn.TypeMapping,
                    projectionColumn.IsNullable);

                // If we have a type mapping (i.e. translating over a column rather than a parameter), apply any necessary server-side
                // conversions.
                if (projectionColumn.TypeMapping is not null)
                {
                    translation = ApplyJsonSqlConversion(
                        translation, _sqlExpressionFactory, projectionColumn.TypeMapping, projectionColumn.IsNullable);
                }

#pragma warning disable EF1001
                return source.UpdateQueryExpression(new SelectExpression(translation, _sqlAliasManager));
#pragma warning restore EF1001
            }
        }

        return base.TranslateElementAtOrDefault(source, index, returnDefault);
    }

    /// <summary>
    ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///     any release. You should only use it directly in your code with extreme caution and knowing that
    ///     doing so can result in application failures when updating to a new Entity Framework Core release.
    /// </summary>
    protected override bool IsNaturallyOrdered(SelectExpression selectExpression)
    {
        return selectExpression is
        {
            Tables: [var mainTable, ..],
            Orderings:
                [
                {
                    Expression: ColumnExpression { Name: JsonEachKeyColumnName } orderingColumn,
                    IsAscending: true
                }
                ]
        }
            && orderingColumn.TableAlias == mainTable.Alias
            && IsJsonEachKeyColumn(selectExpression, orderingColumn);

        bool IsJsonEachKeyColumn(SelectExpression selectExpression, ColumnExpression orderingColumn)
            => selectExpression.Tables.FirstOrDefault(t => t.Alias == orderingColumn.TableAlias)?.UnwrapJoin() is TableExpressionBase table
                && (table is JsonEachExpression
                    || (table is SelectExpression subquery
                        && subquery.Projection.FirstOrDefault(p => p.Alias == JsonEachKeyColumnName)?.Expression is ColumnExpression
                            projectedColumn
                        && IsJsonEachKeyColumn(subquery, projectedColumn)));
    }

    private static Type GetProviderType(SqlExpression expression)
        => expression.TypeMapping?.Converter?.ProviderClrType
            ?? expression.TypeMapping?.ClrType
            ?? expression.Type;

    /// <summary>
    /// Resolves the element type of an enumerable CLR type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Type.GetInterfaces"/> would answer this directly, but it requires
    /// <see cref="DynamicallyAccessedMemberTypes.Interfaces"/> on its receiver and the receiver
    /// here is <c>SqlExpression.Type</c>, whose getter declares no annotation — the requirement
    /// cannot be propagated any further up, because the next frame is EF Core's own
    /// <c>TranslatePrimitiveCollection</c> override signature. Reflecting anyway would leave a
    /// permanent IL2072 in shipped source, so the shapes are resolved structurally instead.
    /// </para>
    /// <para>
    /// The generic-argument count alone is not enough: <c>Dictionary&lt;TKey,
    /// TValue&gt;.KeyCollection</c> and <c>ValueCollection</c> are generic over <em>two</em>
    /// parameters and neither they nor any of their base types is <c>IEnumerable&lt;T&gt;</c>, so
    /// they are recognized explicitly. Without that, <c>dictionary.Keys.Contains(...)</c> and
    /// <c>dictionary.Values.Contains(...)</c> silently fail to translate.
    /// </para>
    /// </remarks>
    private static Type GetSequenceType(Type type)
    {
        if (type.IsArray)
            return type.GetElementType()!;

        for (var current = type; current is not null; current = current.BaseType)
        {
            if (!current.IsGenericType)
                continue;

            var arguments = current.GetGenericArguments();
            var definition = current.GetGenericTypeDefinition();
            if (DictionaryKeyCollections.Contains(definition))
                return arguments[0];
            if (DictionaryValueCollections.Contains(definition))
                return arguments[1];
            if (arguments.Length == 1 && typeof(IEnumerable).IsAssignableFrom(current))
                return arguments[0];
        }

        throw new InvalidOperationException($"Type '{type}' is not an enumerable type.");
    }

    /// <summary>
    /// Key-view types whose element is the dictionary's first generic argument. Every other
    /// dictionary in the BCL exposes its keys as <c>ICollection&lt;TKey&gt;</c> or
    /// <c>IEnumerable&lt;TKey&gt;</c>, which the single-argument rule already covers.
    /// </summary>
    private static readonly HashSet<Type> DictionaryKeyCollections =
    [
        typeof(Dictionary<,>.KeyCollection),
        typeof(SortedDictionary<,>.KeyCollection),
    ];

    /// <summary>
    /// Value-view types whose element is the dictionary's second generic argument.
    /// </summary>
    private static readonly HashSet<Type> DictionaryValueCollections =
    [
        typeof(Dictionary<,>.ValueCollection),
        typeof(SortedDictionary<,>.ValueCollection),
    ];

    private static Type UnwrapNullableType(Type type)
        => Nullable.GetUnderlyingType(type) ?? type;

    private static bool IsNullableType(Type type)
        => !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;

    /// <summary>
    /// Nullable counterparts of every value type the Ahtola SQLite type-mapping source can produce
    /// for a primitive-collection element. Reifying them lets the projection keep its nullable shape
    /// without <c>typeof(Nullable&lt;&gt;).MakeGenericType(...)</c>, which requires dynamic code
    /// (IL3050) and is unavailable under NativeAOT.
    /// </summary>
    private static readonly Dictionary<Type, Type> NullableElementTypes = new()
    {
        [typeof(bool)] = typeof(bool?),
        [typeof(byte)] = typeof(byte?),
        [typeof(sbyte)] = typeof(sbyte?),
        [typeof(char)] = typeof(char?),
        [typeof(short)] = typeof(short?),
        [typeof(ushort)] = typeof(ushort?),
        [typeof(int)] = typeof(int?),
        [typeof(uint)] = typeof(uint?),
        [typeof(long)] = typeof(long?),
        [typeof(ulong)] = typeof(ulong?),
        [typeof(float)] = typeof(float?),
        [typeof(double)] = typeof(double?),
        [typeof(decimal)] = typeof(decimal?),
        [typeof(Guid)] = typeof(Guid?),
        [typeof(DateTime)] = typeof(DateTime?),
        [typeof(DateTimeOffset)] = typeof(DateTimeOffset?),
        [typeof(TimeSpan)] = typeof(TimeSpan?),
        [typeof(DateOnly)] = typeof(DateOnly?),
        [typeof(TimeOnly)] = typeof(TimeOnly?),
    };

    /// <summary>
    /// Returns the nullable form of <paramref name="type"/>. Reference types and types that are
    /// already <see cref="Nullable{T}"/> are returned unchanged. A value type outside the reified
    /// map above (an enum, or a user struct with a custom value converter) keeps its non-nullable
    /// form: the shaper then reads the element directly instead of reading a nullable and converting
    /// back, which is the same observable result for a collection whose elements are non-nullable by
    /// construction, and it avoids constructing a generic instantiation at run time.
    /// </summary>
    private static Type MakeNullable(Type type)
        => IsNullableType(type)
            ? type
            : NullableElementTypes.TryGetValue(type, out var nullable)
                ? nullable
                : type;

    /// <summary>
    ///     Wraps the given expression with any SQL logic necessary to convert a value coming out of a JSON document into the relational value
    ///     represented by the given type mapping.
    /// </summary>
    /// <remarks>
    ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///     any release. You should only use it directly in your code with extreme caution and knowing that
    ///     doing so can result in application failures when updating to a new Entity Framework Core release.
    /// </remarks>
    [EntityFrameworkInternal]
    public static SqlExpression ApplyJsonSqlConversion(
        SqlExpression expression,
        SqliteSqlExpressionFactory sqlExpressionFactory,
        RelationalTypeMapping typeMapping,
        bool isNullable)
        => typeMapping switch
        {
            // In general unhex returns NULL whenever the decoding fails.
            // In this case, we assume that the decoding cannot fail, because we
            // rely on the user to correctly model the database schema and
            // contents. Under this assumption, `expression` can only be a valid
            // hex string or NULL, hence unhex simply propagates the nullability
            // from its argument.

            ByteArrayTypeMapping
                => sqlExpressionFactory.Function(
                    "unhex",
                    new[] { expression },
                    nullable: true,
                    argumentsPropagateNullability: new[] { true },
                    typeof(byte[]),
                    typeMapping),

            _ => expression
        };

    private sealed class FakeMemberInfo(string name) : MemberInfo
    {
        public override string Name { get; } = name;

        public override object[] GetCustomAttributes(bool inherit)
            => throw new NotSupportedException();

        public override object[] GetCustomAttributes(Type attributeType, bool inherit)
            => throw new NotSupportedException();

        public override bool IsDefined(Type attributeType, bool inherit)
            => throw new NotSupportedException();

        public override Type? DeclaringType
            => throw new NotSupportedException();

        public override MemberTypes MemberType
            => throw new NotSupportedException();

        public override Type? ReflectedType
            => throw new NotSupportedException();
    }
}
