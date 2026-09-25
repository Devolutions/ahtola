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

    internal static void RejectTypedColumns(
        CreateTableStatement statement,
        IReadOnlyDictionary<string, ParsedStatement> types)
    {
        foreach (var column in statement.Columns)
            RejectTypedColumn(column, types);
    }

    internal static void RejectTypedColumn(
        EmbeddedColumn column,
        IReadOnlyDictionary<string, ParsedStatement> types)
    {
        if (column.DeclaredType is not { } declaredType)
            return;

        var token = new SqlLexer(declaredType).Current;
        if (token.Kind == TokenKind.Identifier && types.ContainsKey(token.Text))
        {
            throw new EmbeddedSqlException(
                $"Columns of custom type '{declaredType}' are not yet supported by the managed engine.");
        }
    }

    internal static EmbeddedTable CreateBackingTable()
        => new(
            TableName,
            [
                new EmbeddedColumn("name", "TEXT", true, false, false, null),
                new EmbeddedColumn("sql", "TEXT", false, false, false, null),
            ]);
}
