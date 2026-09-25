using Ahtola.Core.Parsing;

namespace Ahtola.Core;

internal static class ManagedTypeRegistry
{
    internal const string TableName = "__turso_internal_types";

    internal static bool MetadataMatches(
        IReadOnlyDictionary<string, EmbeddedTable> left,
        IReadOnlyDictionary<string, EmbeddedTable> right)
    {
        var hasLeft = left.TryGetValue(TableName, out var leftTable);
        var hasRight = right.TryGetValue(TableName, out var rightTable);
        if (hasLeft != hasRight)
            return false;
        if (!hasLeft)
            return true;
        if (leftTable!.Rows.Count != rightTable!.Rows.Count)
            return false;
        for (var index = 0; index < leftTable.Rows.Count; index++)
        {
            if (!leftTable.Rows[index].SequenceEqual(rightTable.Rows[index]))
                return false;
        }
        return true;
    }

    internal static IReadOnlyDictionary<string, ParsedStatement> Load(
        IReadOnlyDictionary<string, EmbeddedTable> tables)
    {
        var types = new Dictionary<string, ParsedStatement>(StringComparer.OrdinalIgnoreCase);
        if (!tables.TryGetValue(TableName, out var backing))
            return types;

        if (backing.Columns.Length != 2
            || !backing.Columns[0].Equals("name", StringComparison.OrdinalIgnoreCase)
            || !backing.Columns[1].Equals("sql", StringComparison.OrdinalIgnoreCase)
            || !backing.ColumnDefinitions[0].PrimaryKey
            || !string.Equals(backing.ColumnDefinitions[0].DeclaredType, "TEXT", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(backing.ColumnDefinitions[1].DeclaredType, "TEXT", StringComparison.OrdinalIgnoreCase))
        {
            throw new EmbeddedSqlException("Invalid internal type registry schema.");
        }

        foreach (var row in backing.Rows)
        {
            if (row.Length != 2
                || row[0].Kind != SqlValueKind.Text
                || row[1].Kind != SqlValueKind.Text)
            {
                throw new EmbeddedSqlException("Invalid internal type registry row.");
            }

            var sql = row[1].AsText();
            var parsed = SqlParser.Parse(sql, SqlParameterMap.Parse(sql));
            if (parsed is CreateDomainStatement domainDefinition)
                ValidateDomain(domainDefinition);
            var name = parsed switch
            {
                CreateTypeStatement type => type.Name,
                CreateDomainStatement domain => domain.Name,
                _ => throw new EmbeddedSqlException("Invalid internal type definition."),
            };
            var baseType = parsed switch
            {
                CreateTypeStatement type => type.BaseType,
                CreateDomainStatement domain => domain.BaseType,
                _ => throw new EmbeddedSqlException("Invalid internal type definition."),
            };
            if (!baseType.Equals("INTEGER", StringComparison.OrdinalIgnoreCase)
                || SqlParameterMap.Parse(sql).Count != 0
                || !name.Equals(row[0].AsText(), StringComparison.OrdinalIgnoreCase)
                || !types.TryAdd(name, parsed))
            {
                throw new EmbeddedSqlException("Invalid or duplicate internal type definition.");
            }
        }

        return types;
    }

    internal static IReadOnlyList<EmbeddedColumn> ResolveColumns(
        CreateTableStatement statement,
        IReadOnlyDictionary<string, ParsedStatement> types)
    {
        var columns = new EmbeddedColumn[statement.Columns.Count];
        for (var index = 0; index < columns.Length; index++)
        {
            var column = statement.Columns[index];
            var definition = FindDefinition(column, types);
            if (definition is null)
            {
                columns[index] = column;
                continue;
            }

            if (!statement.Strict)
                throw new EmbeddedSqlException($"custom type columns require STRICT tables: {statement.Name}.{column.Name}");
            if (column.IsGenerated || column.PrimaryKey || column.ExplicitNull)
                throw new EmbeddedSqlException($"Custom type column {statement.Name}.{column.Name} cannot be generated, a primary key, or explicitly NULL.");
            columns[index] = definition switch
            {
                CreateDomainStatement domain => column with { Domain = domain },
                CreateTypeStatement identity => column with { IdentityType = identity },
                _ => throw new EmbeddedSqlException($"Unsupported custom type '{column.DeclaredType}'."),
            };
        }
        return columns;
    }

    internal static bool ContainsDomain(EmbeddedTable table)
        => table.ColumnDefinitions.Any(static column => column.Domain is not null);

    internal static bool ContainsCustomType(EmbeddedTable table)
        => table.ColumnDefinitions.Any(static column =>
            column.Domain is not null || column.IdentityType is not null);

    internal static void RejectTypedColumn(
        EmbeddedColumn column,
        IReadOnlyDictionary<string, ParsedStatement> types)
    {
        if (FindDefinition(column, types) is not null)
        {
            throw new EmbeddedSqlException(
                $"Columns of custom type '{column.DeclaredType}' are not yet supported by the managed engine.");
        }
    }

    private static ParsedStatement? FindDefinition(
        EmbeddedColumn column,
        IReadOnlyDictionary<string, ParsedStatement> types)
    {
        if (column.DeclaredType is not { } declaredType)
            return null;
        var lexer = new SqlLexer(declaredType);
        if (lexer.Current.Kind != TokenKind.Identifier
            || !types.TryGetValue(lexer.Current.Text, out var definition))
            return null;
        lexer.Next();
        if (lexer.Current.Kind != TokenKind.End)
            throw new EmbeddedSqlException($"Parameterized custom type '{declaredType}' is not supported.");
        return definition;
    }

    internal static Expression RewriteDomainCheck(Expression expression, string columnName)
        => expression switch
        {
            ColumnExpression { Qualifier: null, Name: var name }
                when name.Equals("value", StringComparison.OrdinalIgnoreCase)
                => new ColumnExpression(columnName),
            LiteralExpression literal => literal,
            UnaryExpression unary => unary with
            {
                Operand = RewriteDomainCheck(unary.Operand, columnName),
            },
            BinaryExpression binary => binary with
            {
                Left = RewriteDomainCheck(binary.Left, columnName),
                Right = RewriteDomainCheck(binary.Right, columnName),
            },
            _ => throw new EmbeddedSqlException("Unsupported expression in domain CHECK constraint."),
        };

    internal static void ValidateDomain(CreateDomainStatement domain)
    {
        foreach (var check in domain.Checks)
            _ = RewriteDomainCheck(check.Expression, "value");
        if (domain.Default is not null && !IsConstantDefault(domain.Default))
            throw new EmbeddedSqlException("Only constant INTEGER domain DEFAULT expressions are supported.");
    }

    private static bool IsConstantDefault(Expression expression)
        => expression switch
        {
            LiteralExpression { Value.Kind: SqlValueKind.Integer } => true,
            UnaryExpression { Operand: var operand } => IsConstantDefault(operand),
            _ => false,
        };

    internal static EmbeddedTable CreateBackingTable()
        => new(
            TableName,
            [
                new EmbeddedColumn("name", "TEXT", true, false, false, null),
                new EmbeddedColumn("sql", "TEXT", false, false, false, null),
            ]);
}
