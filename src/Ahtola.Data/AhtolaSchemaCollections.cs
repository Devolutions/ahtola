using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using Ahtola.Core;

namespace Ahtola;

/// <summary>
/// Builds the ADO.NET schema surfaces shared by <see cref="AhtolaConnection"/> and the
/// <c>Ahtola.Data.Sqlite</c> facade.
/// </summary>
/// <remarks>
/// Every collection is derived from ordinary SQL executed on the owning connection rather
/// than from a local catalog handle, so managed local, native local, embedded replica and
/// remote Hrana connections all report the catalog they are actually attached to. A mode
/// that cannot answer a query surfaces the engine's own error instead of an empty table
/// that would read as "no objects exist".
/// </remarks>
internal static class AhtolaSchemaCollections
{
    internal const string Tables = "Tables";
    internal const string Columns = "Columns";
    internal const string Indexes = "Indexes";
    internal const string IndexColumns = "IndexColumns";

    /// <summary>
    /// SQLite's keyword list, which is what a caller needs in order to decide whether an
    /// identifier has to be quoted. Verified against <c>Microsoft.Data.Sqlite</c> as a set by
    /// <c>ManagedSchemaSqliteDifferentialTests</c> rather than maintained by hand.
    /// </summary>
    private static readonly string[] ReservedWordList =
    [
        "ABORT", "ACTION", "ADD", "AFTER", "ALL", "ALTER", "ALWAYS", "ANALYZE",
        "AND", "AS", "ASC", "ATTACH", "AUTOINCREMENT", "BEFORE", "BEGIN", "BETWEEN",
        "BY", "CASCADE", "CASE", "CAST", "CHECK", "COLLATE", "COLUMN", "COMMIT",
        "CONFLICT", "CONSTRAINT", "CREATE", "CROSS", "CURRENT", "CURRENT_DATE",
        "CURRENT_TIME", "CURRENT_TIMESTAMP", "DATABASE", "DEFAULT", "DEFERRABLE",
        "DEFERRED", "DELETE", "DESC", "DETACH", "DISTINCT", "DO", "DROP", "EACH",
        "ELSE", "END", "ESCAPE", "EXCEPT", "EXCLUDE", "EXCLUSIVE", "EXISTS",
        "EXPLAIN", "FAIL", "FILTER", "FIRST", "FOLLOWING", "FOR", "FOREIGN", "FROM",
        "FULL", "GENERATED", "GLOB", "GROUP", "GROUPS", "HAVING", "IF", "IGNORE",
        "IMMEDIATE", "IN", "INDEX", "INDEXED", "INITIALLY", "INNER", "INSERT",
        "INSTEAD", "INTERSECT", "INTO", "IS", "ISNULL", "JOIN", "KEY", "LAST",
        "LEFT", "LIKE", "LIMIT", "MATCH", "MATERIALIZED", "NATURAL", "NO", "NOT",
        "NOTHING", "NOTNULL", "NULL", "NULLS", "OF", "OFFSET", "ON", "OR", "ORDER",
        "OTHERS", "OUTER", "OVER", "PARTITION", "PLAN", "PRAGMA", "PRECEDING",
        "PRIMARY", "QUERY", "RAISE", "RANGE", "RECURSIVE", "REFERENCES", "REGEXP",
        "REINDEX", "RELEASE", "RENAME", "REPLACE", "RESTRICT", "RETURNING", "RIGHT",
        "ROLLBACK", "ROW", "ROWS", "SAVEPOINT", "SELECT", "SET", "TABLE", "TEMP",
        "TEMPORARY", "THEN", "TIES", "TO", "TRANSACTION", "TRIGGER", "UNBOUNDED",
        "UNION", "UNIQUE", "UPDATE", "USING", "VACUUM", "VALUES", "VIEW", "VIRTUAL",
        "WHEN", "WHERE", "WINDOW", "WITH", "WITHOUT",
    ];

    internal static string UnknownCollectionMessage(string collectionName)
        => $"Unknown collection: {collectionName}.";

    internal static string TooManyRestrictionsMessage(string collectionName)
        => $"Too many restrictions specified for collection {collectionName}.";

    internal static DataTable GetSchema(
        DbConnection connection,
        string collectionName,
        string?[]? restrictionValues)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(collectionName);

        if (string.Equals(collectionName, DbMetaDataCollectionNames.MetaDataCollections, StringComparison.OrdinalIgnoreCase))
        {
            ValidateRestrictions(collectionName, restrictionValues, 0);
            return CreateMetaDataCollectionsTable();
        }

        if (string.Equals(collectionName, DbMetaDataCollectionNames.ReservedWords, StringComparison.OrdinalIgnoreCase))
        {
            ValidateRestrictions(collectionName, restrictionValues, 0);
            var table = new DataTable(DbMetaDataCollectionNames.ReservedWords);
            table.Columns.Add(DbMetaDataColumnNames.ReservedWord, typeof(string));
            foreach (var word in ReservedWordList)
                table.Rows.Add(word);

            return table;
        }

        if (string.Equals(collectionName, Tables, StringComparison.OrdinalIgnoreCase))
            return GetTablesSchema(connection, collectionName, restrictionValues);

        if (string.Equals(collectionName, Columns, StringComparison.OrdinalIgnoreCase))
            return GetColumnsSchema(connection, collectionName, restrictionValues);

        if (string.Equals(collectionName, Indexes, StringComparison.OrdinalIgnoreCase))
            return GetIndexesSchema(connection, collectionName, restrictionValues);

        if (string.Equals(collectionName, IndexColumns, StringComparison.OrdinalIgnoreCase))
            return GetIndexColumnsSchema(connection, collectionName, restrictionValues);

        throw new ArgumentException(UnknownCollectionMessage(collectionName));
    }

    private static DataTable CreateMetaDataCollectionsTable()
    {
        var table = new DataTable(DbMetaDataCollectionNames.MetaDataCollections);
        table.Columns.Add(DbMetaDataColumnNames.CollectionName, typeof(string));
        table.Columns.Add(DbMetaDataColumnNames.NumberOfRestrictions, typeof(int));
        table.Columns.Add(DbMetaDataColumnNames.NumberOfIdentifierParts, typeof(int));
        table.Rows.Add(DbMetaDataCollectionNames.MetaDataCollections, 0, 0);
        table.Rows.Add(DbMetaDataCollectionNames.ReservedWords, 0, 0);
        table.Rows.Add(Tables, 4, 4);
        table.Rows.Add(Columns, 4, 4);
        table.Rows.Add(Indexes, 4, 4);
        table.Rows.Add(IndexColumns, 5, 4);
        return table;
    }

    private static DataTable GetTablesSchema(
        DbConnection connection,
        string collectionName,
        string?[]? restrictionValues)
    {
        EnsureOpen(connection);
        ValidateRestrictions(collectionName, restrictionValues, 4);
        var table = new DataTable(Tables);
        table.Columns.Add("TABLE_CATALOG", typeof(string));
        table.Columns.Add("TABLE_SCHEMA", typeof(string));
        table.Columns.Add("TABLE_NAME", typeof(string));
        table.Columns.Add("TABLE_TYPE", typeof(string));

        if (!MatchesCatalogAndSchemaRestrictions(restrictionValues))
            return table;

        var tableNameRestriction = GetRestriction(restrictionValues, 2);
        var tableTypeRestriction = GetRestriction(restrictionValues, 3);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, type, sql FROM sqlite_master WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%'"
                              + (tableNameRestriction is null ? "" : " AND name COLLATE NOCASE = $table")
                              + " ORDER BY name;";
        if (tableNameRestriction is not null)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$table";
            parameter.Value = tableNameRestriction;
            command.Parameters.Add(parameter);
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var type = reader.GetString(1).Equals("view", StringComparison.OrdinalIgnoreCase) ? "VIEW" : "BASE TABLE";
            if (tableTypeRestriction is not null
                && !string.Equals(type, tableTypeRestriction, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tableName = GetDeclaredSchemaObjectName(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2));
            table.Rows.Add("main", DBNull.Value, tableName, type);
        }

        return table;
    }

    private static DataTable GetColumnsSchema(
        DbConnection connection,
        string collectionName,
        string?[]? restrictionValues)
    {
        EnsureOpen(connection);
        ValidateRestrictions(collectionName, restrictionValues, 4);
        var table = new DataTable(Columns);
        table.Columns.Add("TABLE_CATALOG", typeof(string));
        table.Columns.Add("TABLE_SCHEMA", typeof(string));
        table.Columns.Add("TABLE_NAME", typeof(string));
        table.Columns.Add("COLUMN_NAME", typeof(string));
        table.Columns.Add("ORDINAL_POSITION", typeof(int));
        table.Columns.Add("COLUMN_DEFAULT", typeof(string));
        table.Columns.Add("IS_NULLABLE", typeof(bool));
        table.Columns.Add("DATA_TYPE", typeof(string));

        if (!MatchesCatalogAndSchemaRestrictions(restrictionValues))
            return table;

        var tableNameRestriction = GetRestriction(restrictionValues, 2);
        var columnNameRestriction = GetRestriction(restrictionValues, 3);
        var tableNames = tableNameRestriction is null
            ? GetUserTableNames(connection)
            : [tableNameRestriction];
        foreach (var tableName in tableNames)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var columnName = reader.GetString(1);
                if (columnNameRestriction is not null
                    && !string.Equals(columnNameRestriction, columnName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                table.Rows.Add(
                    "main",
                    DBNull.Value,
                    tableName,
                    columnName,
                    reader.GetInt32(0),
                    reader.IsDBNull(4) ? DBNull.Value : reader.GetValue(4),
                    reader.GetInt64(3) == 0,
                    reader.GetString(2));
            }
        }

        return table;
    }

    private static DataTable GetIndexesSchema(
        DbConnection connection,
        string collectionName,
        string?[]? restrictionValues)
    {
        EnsureOpen(connection);
        ValidateRestrictions(collectionName, restrictionValues, 4);
        var table = new DataTable(Indexes);
        table.Columns.Add("TABLE_CATALOG", typeof(string));
        table.Columns.Add("TABLE_SCHEMA", typeof(string));
        table.Columns.Add("TABLE_NAME", typeof(string));
        table.Columns.Add("INDEX_NAME", typeof(string));
        table.Columns.Add("IS_UNIQUE", typeof(bool));
        table.Columns.Add("ORIGIN", typeof(string));
        table.Columns.Add("IS_PARTIAL", typeof(bool));

        if (!MatchesCatalogAndSchemaRestrictions(restrictionValues))
            return table;

        var tableNameRestriction = GetRestriction(restrictionValues, 2);
        var indexNameRestriction = GetRestriction(restrictionValues, 3);

        foreach (var tableName in GetUserTableNames(connection))
        {
            if (tableNameRestriction is not null
                && !string.Equals(tableName, tableNameRestriction, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA index_list({QuoteIdentifier(tableName)});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var indexName = reader.GetString(1);
                if (indexNameRestriction is not null
                    && !string.Equals(indexName, indexNameRestriction, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                table.Rows.Add(
                    "main",
                    DBNull.Value,
                    tableName,
                    indexName,
                    reader.GetInt64(2) != 0,
                    reader.GetString(3),
                    reader.GetInt64(4) != 0);
            }
        }

        return table;
    }

    private static DataTable GetIndexColumnsSchema(
        DbConnection connection,
        string collectionName,
        string?[]? restrictionValues)
    {
        EnsureOpen(connection);
        ValidateRestrictions(collectionName, restrictionValues, 5);
        var table = new DataTable(IndexColumns);
        table.Columns.Add("TABLE_CATALOG", typeof(string));
        table.Columns.Add("TABLE_SCHEMA", typeof(string));
        table.Columns.Add("TABLE_NAME", typeof(string));
        table.Columns.Add("INDEX_NAME", typeof(string));
        table.Columns.Add("ORDINAL_POSITION", typeof(int));
        table.Columns.Add("COLUMN_ORDINAL", typeof(int));
        table.Columns.Add("COLUMN_NAME", typeof(string));

        if (!MatchesCatalogAndSchemaRestrictions(restrictionValues))
            return table;

        var tableNameRestriction = GetRestriction(restrictionValues, 2);
        var indexNameRestriction = GetRestriction(restrictionValues, 3);
        var columnNameRestriction = GetRestriction(restrictionValues, 4);

        foreach (var tableName in GetUserTableNames(connection))
        {
            if (tableNameRestriction is not null
                && !string.Equals(tableName, tableNameRestriction, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var indexName in GetIndexNames(connection, tableName))
            {
                if (indexNameRestriction is not null
                    && !string.Equals(indexName, indexNameRestriction, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var columns = connection.CreateCommand();
                columns.CommandText = $"PRAGMA index_info({QuoteIdentifier(indexName)});";
                using var columnReader = columns.ExecuteReader();
                while (columnReader.Read())
                {
                    var columnName = columnReader.IsDBNull(2)
                        ? null
                        : columnReader.GetString(2);
                    if (columnNameRestriction is not null
                        && (columnName is null
                            || !string.Equals(
                                columnName,
                                columnNameRestriction,
                                StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    table.Rows.Add(
                        "main",
                        DBNull.Value,
                        tableName,
                        indexName,
                        columnReader.GetInt32(0),
                        columnReader.GetInt32(1),
                        columnName is null ? DBNull.Value : columnName);
                }
            }
        }

        return table;
    }

    internal static List<string> GetUserTableNames(DbConnection connection)
    {
        var tables = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            tables.Add(reader.GetString(0));

        return tables;
    }

    private static List<string> GetIndexNames(DbConnection connection, string tableName)
    {
        var indexes = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_list({QuoteIdentifier(tableName)});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            indexes.Add(reader.GetString(1));

        return indexes;
    }

    internal static void EnsureOpen(DbConnection connection)
    {
        if (connection.State != ConnectionState.Open)
            throw new InvalidOperationException("The connection is not open.");
    }

    internal static void ValidateRestrictions(string collectionName, string?[]? restrictionValues, int maxRestrictions)
    {
        if (restrictionValues is not null && restrictionValues.Length > maxRestrictions)
            throw new ArgumentException(TooManyRestrictionsMessage(collectionName));
    }

    private static string? GetRestriction(string?[]? restrictionValues, int index)
        => restrictionValues is not null && restrictionValues.Length > index && !string.IsNullOrEmpty(restrictionValues[index])
            ? restrictionValues[index]
            : null;

    private static bool MatchesCatalogAndSchemaRestrictions(string?[]? restrictionValues)
    {
        var catalog = GetRestriction(restrictionValues, 0);
        var schema = GetRestriction(restrictionValues, 1);
        return (catalog is null || string.Equals(catalog, "main", StringComparison.OrdinalIgnoreCase))
               && schema is null;
    }

    internal static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    internal static string GetDeclaredSchemaObjectName(string storedName, string type, string? createSql)
    {
        if (string.IsNullOrWhiteSpace(createSql))
            return storedName;

        var index = 0;
        if (!TryReadKeyword(createSql, ref index, "CREATE"))
            return storedName;

        _ = TryReadKeyword(createSql, ref index, "TEMP")
            || TryReadKeyword(createSql, ref index, "TEMPORARY");

        var expectedType = type.Equals("view", StringComparison.OrdinalIgnoreCase) ? "VIEW" : "TABLE";
        if (!TryReadKeyword(createSql, ref index, expectedType))
            return storedName;

        var beforeIf = index;
        if (TryReadKeyword(createSql, ref index, "IF"))
        {
            if (!TryReadKeyword(createSql, ref index, "NOT")
                || !TryReadKeyword(createSql, ref index, "EXISTS"))
            {
                index = beforeIf;
            }
        }

        return TryReadSchemaObjectName(createSql, ref index, out var objectName)
            ? objectName
            : storedName;
    }

    private static bool TryReadKeyword(string sql, ref int index, string keyword)
    {
        SkipSqlWhitespace(sql, ref index);
        if (sql.Length - index < keyword.Length
            || !sql.AsSpan(index, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var end = index + keyword.Length;
        if (end < sql.Length && IsSqlIdentifierPart(sql[end]))
            return false;

        index = end;
        return true;
    }

    private static bool TryReadSchemaObjectName(string sql, ref int index, [NotNullWhen(true)] out string? objectName)
    {
        objectName = null;
        do
        {
            if (!TryReadIdentifier(sql, ref index, out var part))
                return objectName is not null;

            objectName = part;
            SkipSqlWhitespace(sql, ref index);
            if (index >= sql.Length || sql[index] != '.')
                return true;

            index++;
        }
        while (true);
    }

    private static bool TryReadIdentifier(string sql, ref int index, [NotNullWhen(true)] out string? identifier)
    {
        identifier = null;
        SkipSqlWhitespace(sql, ref index);
        if (index >= sql.Length)
            return false;

        var quote = sql[index];
        if (quote is '"' or '\'' or '`')
        {
            index++;
            var start = index;
            var builder = new StringBuilder();
            while (index < sql.Length)
            {
                if (sql[index] == quote)
                {
                    if (index + 1 < sql.Length && sql[index + 1] == quote)
                    {
                        builder.Append(sql.AsSpan(start, index - start));
                        builder.Append(quote);
                        index += 2;
                        start = index;
                        continue;
                    }

                    builder.Append(sql.AsSpan(start, index - start));
                    index++;
                    identifier = builder.ToString();
                    return true;
                }

                index++;
            }

            return false;
        }

        if (quote == '[')
        {
            index++;
            var start = index;
            while (index < sql.Length && sql[index] != ']')
                index++;
            if (index >= sql.Length)
                return false;

            identifier = sql[start..index];
            index++;
            return true;
        }

        var tokenStart = index;
        while (index < sql.Length && IsSqlIdentifierPart(sql[index]))
            index++;

        if (index == tokenStart)
            return false;

        identifier = sql[tokenStart..index];
        return true;
    }

    private static void SkipSqlWhitespace(string sql, ref int index)
    {
        while (index < sql.Length && char.IsWhiteSpace(sql[index]))
            index++;
    }

    private static bool IsSqlIdentifierPart(char value)
        => char.IsLetterOrDigit(value) || value == '_' || value == '$';

    internal static string UnquoteIdentifier(string identifier)
    {
        var trimmed = identifier.Trim();
        if (trimmed.Length < 2)
            return trimmed;

        return (trimmed[0], trimmed[^1]) switch
        {
            ('"', '"') => trimmed[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal),
            ('[', ']') => trimmed[1..^1],
            ('`', '`') => trimmed[1..^1].Replace("``", "`", StringComparison.Ordinal),
            _ => trimmed
        };
    }

    /// <summary>
    /// Builds the <see cref="DbDataReader.GetSchemaTable"/> result for a reader over
    /// <paramref name="commandText"/>. The column set matches the facade reader so that
    /// <see cref="DbCommandBuilder"/> behaves identically on both ADO.NET surfaces.
    /// </summary>
    internal static DataTable BuildReaderSchemaTable(
        DbConnection? connection,
        string? commandText,
        int fieldCount,
        Func<int, string> getName,
        Func<int, Type> getFieldType)
    {
        var schema = CreateReaderSchemaTable();
        var hasSource = TryGetSelectSource(commandText, fieldCount, getName, out var tableName, out var selections);
        var tableColumns = hasSource && connection is not null
            ? GetTableColumns(connection, tableName)
            : new Dictionary<string, ReaderSchemaColumn>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < fieldCount; i++)
        {
            var columnName = getName(i);
            var selection = i < selections.Count ? selections[i] : columnName;
            var baseColumnName = ResolveBaseColumnName(selection, columnName, tableColumns);
            ReaderSchemaColumn? info = baseColumnName is not null
                                       && tableColumns.TryGetValue(baseColumnName, out var resolved)
                ? resolved
                : null;

            var declaredType = info?.TypeName ?? string.Empty;
            var dataType = info is not null
                ? GetClrTypeFromDeclaredType(declaredType, getFieldType(i))
                : getFieldType(i);
            var dataTypeName = info is not null
                ? StripTypeLength(declaredType)
                : GetDeclaredTypeFromClrType(dataType);
            var isAliased = info is null
                            || !string.Equals(info.Name, columnName, StringComparison.Ordinal);

            var row = schema.NewRow();
            row[SchemaTableColumn.ColumnName] = columnName;
            row[SchemaTableColumn.ColumnOrdinal] = i;
            row[SchemaTableColumn.ColumnSize] = -1;
            row[SchemaTableColumn.NumericPrecision] = DBNull.Value;
            row[SchemaTableColumn.NumericScale] = DBNull.Value;
            row[SchemaTableColumn.IsUnique] = info is not null ? !isAliased && info.IsUnique : DBNull.Value;
            row[SchemaTableColumn.IsKey] = info is not null ? info.IsKey : DBNull.Value;
            row["BaseServerName"] = "";
            row["BaseCatalogName"] = info is not null ? "main" : DBNull.Value;
            row[SchemaTableColumn.BaseColumnName] = info is not null ? info.Name : DBNull.Value;
            row[SchemaTableColumn.BaseSchemaName] = DBNull.Value;
            row[SchemaTableColumn.BaseTableName] = info is not null ? tableName : DBNull.Value;
            row[SchemaTableColumn.DataType] = dataType;
            row["DataTypeName"] = dataTypeName;
            row[SchemaTableColumn.AllowDBNull] = info is not null ? isAliased || info.AllowNull : DBNull.Value;
            row[SchemaTableColumn.IsAliased] = isAliased;
            row[SchemaTableColumn.IsExpression] = info is null;
            row[SchemaTableOptionalColumn.IsAutoIncrement] = info is not null ? false : DBNull.Value;
            row[SchemaTableColumn.IsLong] = DBNull.Value;
            row[SchemaTableColumn.ProviderType] = GetProviderType(dataType);
            schema.Rows.Add(row);
        }

        return schema;
    }

    /// <summary>
    /// Resolves the declared SQLite type of every projected column, or <c>null</c> for a column
    /// that does not map to a stored column. A local result set carries no declared types, so a
    /// reader that is not positioned on a row can only answer type questions from the catalog.
    /// </summary>
    internal static string?[] GetDeclaredColumnTypes(
        DbConnection? connection,
        string? commandText,
        int fieldCount,
        Func<int, string> getName)
    {
        var declared = new string?[fieldCount];
        if (!TryGetSelectSource(commandText, fieldCount, getName, out var tableName, out var selections)
            || connection is null)
        {
            return declared;
        }

        var tableColumns = GetTableColumns(connection, tableName);
        for (var i = 0; i < fieldCount; i++)
        {
            var columnName = getName(i);
            var selection = i < selections.Count ? selections[i] : columnName;
            var baseColumnName = ResolveBaseColumnName(selection, columnName, tableColumns);
            if (baseColumnName is not null && tableColumns.TryGetValue(baseColumnName, out var info))
                declared[i] = info.TypeName;
        }

        return declared;
    }

    /// <summary>
    /// Maps a declared SQLite type to its CLR type using SQLite's affinity rules.
    /// </summary>
    internal static Type GetClrTypeFromDeclaredTypeName(string declaredType, Type fallback)
        => GetClrTypeFromDeclaredType(declaredType, fallback);

    private static DataTable CreateReaderSchemaTable()
    {
        var schema = new DataTable("SchemaTable");
        schema.Columns.Add(SchemaTableColumn.ColumnName, typeof(string));
        schema.Columns.Add(SchemaTableColumn.ColumnOrdinal, typeof(int));
        schema.Columns.Add(SchemaTableColumn.ColumnSize, typeof(int));
        schema.Columns.Add(SchemaTableColumn.NumericPrecision, typeof(short));
        schema.Columns.Add(SchemaTableColumn.NumericScale, typeof(short));
        schema.Columns.Add(SchemaTableColumn.IsUnique, typeof(bool));
        schema.Columns.Add(SchemaTableColumn.IsKey, typeof(bool));
        schema.Columns.Add("BaseServerName", typeof(string));
        schema.Columns.Add("BaseCatalogName", typeof(string));
        schema.Columns.Add(SchemaTableColumn.BaseColumnName, typeof(string));
        schema.Columns.Add(SchemaTableColumn.BaseSchemaName, typeof(string));
        schema.Columns.Add(SchemaTableColumn.BaseTableName, typeof(string));
        schema.Columns.Add(SchemaTableColumn.DataType, typeof(Type));
        schema.Columns.Add("DataTypeName", typeof(string));
        schema.Columns.Add(SchemaTableColumn.AllowDBNull, typeof(bool));
        schema.Columns.Add(SchemaTableColumn.IsAliased, typeof(bool));
        schema.Columns.Add(SchemaTableColumn.IsExpression, typeof(bool));
        schema.Columns.Add(SchemaTableOptionalColumn.IsAutoIncrement, typeof(bool));
        schema.Columns.Add(SchemaTableColumn.IsLong, typeof(bool));
        schema.Columns.Add(SchemaTableColumn.ProviderType, typeof(int));
        return schema;
    }

    private static bool TryGetSelectSource(
        string? commandText,
        int fieldCount,
        Func<int, string> getName,
        out string tableName,
        out List<string> selections)
    {
        tableName = string.Empty;
        selections = [];
        if (string.IsNullOrWhiteSpace(commandText))
            return false;

        var match = Regex.Match(
            commandText,
            @"^\s*SELECT\s+(?<select>.*?)\s+FROM\s+(?<table>""(?:[^""]|"""")+""|\[[^\]]+\]|`[^`]+`|[\w]+)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
            return false;

        tableName = UnquoteIdentifier(match.Groups["table"].Value);
        selections = SplitSelectList(match.Groups["select"].Value);
        if (selections.Count == 1 && selections[0] == "*")
            selections = Enumerable.Range(0, fieldCount).Select(getName).ToList();

        return true;
    }

    private static List<string> SplitSelectList(string selectList)
    {
        var selections = new List<string>();
        var start = 0;
        var quote = false;
        for (var i = 0; i < selectList.Length; i++)
        {
            if (selectList[i] == '\'')
                quote = !quote;
            else if (!quote && selectList[i] == ',')
            {
                selections.Add(selectList[start..i].Trim());
                start = i + 1;
            }
        }

        selections.Add(selectList[start..].Trim());
        return selections;
    }

    private static Dictionary<string, ReaderSchemaColumn> GetTableColumns(DbConnection connection, string tableName)
    {
        // Only dispatch to the managed fast path when the connection is actually backed by a
        // managed adapter. AhtolaConnection/SqliteConnection implement IManagedSchemaConnection
        // unconditionally (native local and remote connections included), so checking the
        // interface alone would route those connections into ManagedSchemaConnection, which has
        // no adapter to return. Falling through to the PRAGMA-based path below keeps native and
        // remote connections on their normal provider-neutral schema lookup.
        if (connection is IManagedSchemaConnection { ManagedSchemaConnection: { } managedConnection })
            return GetManagedTableColumns(managedConnection, tableName);

        var columns = new Dictionary<string, ReaderSchemaColumn>(StringComparer.OrdinalIgnoreCase);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(1);
                columns[name] = new ReaderSchemaColumn(
                    name,
                    reader.GetString(2),
                    reader.GetInt64(3) == 0,
                    reader.GetInt64(5) != 0,
                    false);
            }
        }

        if (columns.Count == 0)
            return columns;

        foreach (var indexName in GetUniqueSingleColumnIndexNames(connection, tableName))
        {
            using var infoCommand = connection.CreateCommand();
            infoCommand.CommandText = $"PRAGMA index_info({QuoteIdentifier(indexName)});";
            using var indexInfo = infoCommand.ExecuteReader();
            var indexedColumns = new List<string>();
            while (indexInfo.Read())
            {
                if (!indexInfo.IsDBNull(2))
                    indexedColumns.Add(indexInfo.GetString(2));
            }

            if (indexedColumns.Count == 1 && columns.TryGetValue(indexedColumns[0], out var column))
                columns[indexedColumns[0]] = column with { IsUnique = true };
        }

        return columns;
    }

    private static Dictionary<string, ReaderSchemaColumn> GetManagedTableColumns(
        IManagedConnectionAdapter connection,
        string tableName)
    {
        var columns = new Dictionary<string, ReaderSchemaColumn>(StringComparer.OrdinalIgnoreCase);
        using (var statement = connection.Prepare(
                   BuildManagedPragma(tableName, "table_info")))
        {
            while (statement.Step() == StatementStepResult.Row)
            {
                var name = statement.GetValue(1).AsText();
                columns[name] = new ReaderSchemaColumn(
                    name,
                    statement.GetValue(2).AsText(),
                    statement.GetValue(3).AsInteger() == 0,
                    statement.GetValue(5).AsInteger() != 0,
                    false);
            }
        }

        if (columns.Count == 0)
            return columns;

        using var indexes = connection.Prepare(
            BuildManagedPragma(tableName, "index_list"));
        while (indexes.Step() == StatementStepResult.Row)
        {
            if (indexes.GetValue(2).AsInteger() == 0
                || indexes.GetValue(4).AsInteger() != 0)
            {
                continue;
            }

            var indexName = indexes.GetValue(1).AsText();
            using var info = connection.Prepare(
                BuildManagedPragma(
                    QualifyPragmaObject(tableName, indexName),
                    "index_info"));
            string? indexedColumn = null;
            var indexedColumnCount = 0;
            while (info.Step() == StatementStepResult.Row)
            {
                if (info.GetValue(2).Kind != SqlValueKind.Null)
                {
                    indexedColumn = info.GetValue(2).AsText();
                    indexedColumnCount++;
                }
            }

            if (indexedColumnCount == 1
                && indexedColumn is not null
                && columns.TryGetValue(indexedColumn, out var column))
            {
                columns[indexedColumn] = column with { IsUnique = true };
            }
        }

        return columns;
    }

    private static string BuildManagedPragma(string objectName, string pragma)
    {
        var separator = objectName.IndexOf('.');
        return separator < 0
            ? $"PRAGMA {pragma}({QuoteIdentifier(objectName)});"
            : $"PRAGMA {QuoteIdentifier(objectName[..separator])}.{pragma}"
              + $"({QuoteIdentifier(objectName[(separator + 1)..])});";
    }

    private static string QualifyPragmaObject(string tableName, string objectName)
    {
        var separator = tableName.IndexOf('.');
        return separator < 0
            ? objectName
            : tableName[..separator] + "." + objectName;
    }

    private static List<string> GetUniqueSingleColumnIndexNames(DbConnection connection, string tableName)
    {
        var indexes = new List<string>();
        using var indexCommand = connection.CreateCommand();
        indexCommand.CommandText = $"PRAGMA index_list({QuoteIdentifier(tableName)});";
        using var reader = indexCommand.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetInt64(2) == 0 || reader.GetInt64(4) != 0)
                continue;

            indexes.Add(reader.GetString(1));
        }

        return indexes;
    }

    private static string? ResolveBaseColumnName(
        string selection,
        string columnName,
        Dictionary<string, ReaderSchemaColumn> tableColumns)
    {
        var withoutAlias = Regex.Replace(selection, @"\s+AS\s+.*$", "", RegexOptions.IgnoreCase).Trim();
        var candidate = UnquoteIdentifier(withoutAlias);
        if (tableColumns.ContainsKey(candidate))
            return candidate;
        if (selection.Length != withoutAlias.Length)
            return null;

        return tableColumns.ContainsKey(columnName) && !Regex.IsMatch(selection, @"[+\-*/()]")
            ? columnName
            : null;
    }

    private static string StripTypeLength(string typeName)
    {
        var index = typeName.IndexOf('(', StringComparison.Ordinal);
        return index < 0 ? typeName : typeName[..index];
    }

    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
    private static Type GetClrTypeFromDeclaredType(
        string typeName,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)] Type fallback)
    {
        var normalized = StripTypeLength(typeName).Trim();
        if (normalized.Equals("GUID", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("UNIQUEIDENTIFIER", StringComparison.OrdinalIgnoreCase))
        {
            return typeof(Guid);
        }

        normalized = normalized.ToUpperInvariant();
        if (normalized.Length == 0)
            return fallback;
        if (normalized.Contains("INT", StringComparison.Ordinal))
            return typeof(long);
        if (normalized.Contains("CHAR", StringComparison.Ordinal)
            || normalized.Contains("CLOB", StringComparison.Ordinal)
            || normalized.Contains("TEXT", StringComparison.Ordinal))
        {
            return typeof(string);
        }

        if (normalized.Contains("REAL", StringComparison.Ordinal)
            || normalized.Contains("FLOA", StringComparison.Ordinal)
            || normalized.Contains("DOUB", StringComparison.Ordinal))
        {
            return typeof(double);
        }

        if (normalized.Contains("BLOB", StringComparison.Ordinal))
            return typeof(byte[]);

        return typeof(string);
    }

    private static string GetDeclaredTypeFromClrType(Type type)
    {
        if (type == typeof(long) || type == typeof(int) || type == typeof(bool))
            return "INTEGER";
        if (type == typeof(double) || type == typeof(float))
            return "REAL";
        if (type == typeof(string) || type == typeof(Guid))
            return "TEXT";

        return "BLOB";
    }

    private static int GetProviderType(Type type)
    {
        if (type == typeof(long) || type == typeof(int) || type == typeof(bool))
            return 0;
        if (type == typeof(double) || type == typeof(float))
            return 1;
        if (type == typeof(string) || type == typeof(Guid))
            return 2;

        return 3;
    }

    private sealed record ReaderSchemaColumn(
        string Name,
        string TypeName,
        bool AllowNull,
        bool IsKey,
        bool IsUnique);
}
