using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Ahtola.Data.Sqlite;

namespace Ahtola.PSSqlite;

public enum DBMigrationMode
{
    INCREMENTAL,
    CREATE,
    OVERWRITE
}

public enum SqliteOrdering
{
    ASC,
    DESC,
    NONE
}

public enum SqliteConstraintType
{
    Index,
    ForeignKey,
    PrimaryKey,
    Check
}

public enum SqliteTableOption
{
    WithoutRowId,
    Strict
}

internal static class DefinitionReader
{
    public static bool Contains(IDictionary definition, string key)
    {
        return Find(definition, key, out _);
    }

    public static object? Get(IDictionary definition, string key)
    {
        return Find(definition, key, out var value) ? value : null;
    }

    public static bool Find(IDictionary definition, string key, out object? value)
    {
        foreach (DictionaryEntry entry in definition)
        {
            if (string.Equals(Convert.ToString(entry.Key, CultureInfo.InvariantCulture), key, StringComparison.OrdinalIgnoreCase))
            {
                value = entry.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    public static Dictionary<string, object?> ToDictionary(object? value)
    {
        if (value is not IDictionary source)
        {
            throw new ArgumentException("The definition must be a dictionary.");
        }

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in source)
        {
            var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture)
                ?? throw new ArgumentException("Definition keys cannot be null.");
            result[key] = Normalize(entry.Value);
        }

        return result;
    }

    public static object? Normalize(object? value)
    {
        if (value is IDictionary dictionary)
        {
            return ToDictionary(dictionary);
        }

        if (value is IEnumerable enumerable and not string)
        {
            var list = new List<object?>();
            foreach (var item in enumerable)
            {
                list.Add(Normalize(item));
            }

            return list;
        }

        return value;
    }

    public static IReadOnlyList<object?> ToList(object? value)
    {
        if (value is null)
        {
            return Array.Empty<object?>();
        }

        if (value is IEnumerable enumerable and not string)
        {
            var result = new List<object?>();
            foreach (var item in enumerable)
            {
                result.Add(Normalize(item));
            }

            return result;
        }

        return new[] { Normalize(value) };
    }

    public static string? AsString(object? value)
    {
        return value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    public static bool AsBool(object? value, bool defaultValue = false)
    {
        if (value is null)
        {
            return defaultValue;
        }

        if (value is bool boolean)
        {
            return boolean;
        }

        return bool.TryParse(AsString(value), out var parsed) ? parsed : defaultValue;
    }

    public static T? AsEnum<T>(object? value, T? defaultValue = null)
        where T : struct
    {
        if (value is T typed)
        {
            return typed;
        }

        return Enum.TryParse<T>(AsString(value), true, out var parsed) ? parsed : defaultValue;
    }

    public static IReadOnlyList<string> AsStrings(object? value)
    {
        return ToList(value)
            .Select(AsString)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToArray();
    }
}

internal static class SqlIdentifier
{
    public static string Quote(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("SQLite identifiers cannot be empty.");
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public static string QuoteQualified(string value)
    {
        return string.Join(
            ".",
            value.Split('.').Select(part => Quote(part.Trim())));
    }
}

public class SQLiteConstraint
{
    public SqliteConstraintType ConstraintType { get; set; }

    public SQLiteConstraint()
    {
    }

    public SQLiteConstraint(string constraintType)
    {
        ConstraintType = ParseConstraintType(constraintType);
    }

    public SQLiteConstraint(SqliteConstraintType constraintType)
    {
        ConstraintType = constraintType;
    }

    private static SqliteConstraintType ParseConstraintType(string value)
    {
        return Enum.TryParse<SqliteConstraintType>(value, true, out var result)
            ? result
            : throw new ArgumentException($"Unknown SQLite constraint type '{value}'.");
    }
}

public sealed class SqliteIndexConstraint : SQLiteConstraint
{
    public string? Name { get; set; }
    public string? Table { get; set; }
    public bool Unique { get; set; }
    public bool IfNotExists { get; set; } = true;
    public string? SchemaName { get; set; }
    public string[] Columns { get; set; } = Array.Empty<string>();
    public string? Where { get; set; }

    public SqliteIndexConstraint()
    {
        ConstraintType = SqliteConstraintType.Index;
    }

    public SqliteIndexConstraint(IDictionary definition)
        : this()
    {
        Name = DefinitionReader.AsString(DefinitionReader.Get(definition, "Name"));
        Unique = DefinitionReader.AsBool(DefinitionReader.Get(definition, "Unique"));
        IfNotExists = DefinitionReader.AsBool(DefinitionReader.Get(definition, "IfNotExists"), true);
        SchemaName = DefinitionReader.AsString(DefinitionReader.Get(definition, "SchemaName"));
        Table = DefinitionReader.AsString(DefinitionReader.Get(definition, "Table"));
        Columns = DefinitionReader.AsStrings(DefinitionReader.Get(definition, "Columns")).ToArray();
        Where = DefinitionReader.AsString(DefinitionReader.Get(definition, "Where"));
        ValidateDefinition();
    }

    public void ValidateDefinition()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("Name is required for an index.");
        }

        if (string.IsNullOrWhiteSpace(Table))
        {
            throw new ArgumentException("The table name is required.");
        }

        if (Columns.Length == 0)
        {
            throw new ArgumentException("At least one column is required for the index.");
        }
    }

    public string CreateString()
    {
        ValidateDefinition();
        var builder = new StringBuilder("CREATE");
        if (Unique)
        {
            builder.Append(" UNIQUE");
        }

        builder.Append(" INDEX ");
        if (IfNotExists)
        {
            builder.Append("IF NOT EXISTS ");
        }

        if (!string.IsNullOrWhiteSpace(SchemaName))
        {
            builder.Append(SqlIdentifier.Quote(SchemaName!)).Append('.');
        }

        builder.Append(SqlIdentifier.Quote(Name!))
            .Append(" ON ")
            .Append(SqlIdentifier.QuoteQualified(Table!))
            .Append(" (")
            .Append(string.Join(", ", Columns.Select(SqlIdentifier.Quote)))
            .Append(')');

        if (!string.IsNullOrWhiteSpace(Where))
        {
            builder.Append(" WHERE ").Append(Where);
        }

        return builder.AppendLine(";").ToString();
    }

    public override string ToString() => CreateString();
}

public sealed class SqliteForeignKeyTableConstraint : SQLiteConstraint
{
    public string? Name { get; set; }
    public string? Table { get; set; }
    public string[] Columns { get; set; } = Array.Empty<string>();
    public string? ForeignTable { get; set; }
    public string[] ForeignColumns { get; set; } = Array.Empty<string>();
    public string? OnUpdate { get; set; }
    public string? OnDelete { get; set; }
    public string Match { get; set; } = "NONE";

    public SqliteForeignKeyTableConstraint()
    {
        ConstraintType = SqliteConstraintType.ForeignKey;
    }

    public SqliteForeignKeyTableConstraint(IDictionary definition)
        : this()
    {
        Name = DefinitionReader.AsString(DefinitionReader.Get(definition, "Name"));
        Table = DefinitionReader.AsString(DefinitionReader.Get(definition, "Table"));
        Columns = DefinitionReader.AsStrings(DefinitionReader.Get(definition, "Columns")).ToArray();
        ForeignTable = DefinitionReader.AsString(DefinitionReader.Get(definition, "ForeignTable"));
        ForeignColumns = DefinitionReader.AsStrings(DefinitionReader.Get(definition, "ForeignColumns")).ToArray();
        OnUpdate = DefinitionReader.AsString(DefinitionReader.Get(definition, "OnUpdate"));
        OnDelete = DefinitionReader.AsString(DefinitionReader.Get(definition, "OnDelete"));
        Match = DefinitionReader.AsString(DefinitionReader.Get(definition, "Match")) ?? "NONE";
        ValidateDefinition();
    }

    public void ValidateDefinition()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("Name is required for foreign key constraint.");
        }

        if (string.IsNullOrWhiteSpace(Table))
        {
            throw new ArgumentException("Table is required for foreign key constraint.");
        }

        if (string.IsNullOrWhiteSpace(ForeignTable))
        {
            throw new ArgumentException("ForeignTable is required for foreign key constraint.");
        }

        if (Columns.Length == 0)
        {
            throw new ArgumentException("At least one column is required for foreign key constraint.");
        }

        if (ForeignColumns.Length == 0)
        {
            throw new ArgumentException("At least one foreign column is required for foreign key constraint.");
        }
    }

    public override string ToString()
    {
        ValidateDefinition();
        var builder = new StringBuilder()
            .Append("CONSTRAINT ")
            .Append(SqlIdentifier.Quote(Name!))
            .Append(" FOREIGN KEY (")
            .Append(string.Join(", ", Columns.Select(SqlIdentifier.Quote)))
            .Append(") REFERENCES ")
            .Append(SqlIdentifier.QuoteQualified(ForeignTable!))
            .Append(" (")
            .Append(string.Join(", ", ForeignColumns.Select(SqlIdentifier.Quote)))
            .Append(')');

        if (!string.IsNullOrWhiteSpace(OnUpdate))
        {
            builder.Append(" ON UPDATE ").Append(OnUpdate!.ToUpperInvariant());
        }

        if (!string.IsNullOrWhiteSpace(OnDelete))
        {
            builder.Append(" ON DELETE ").Append(OnDelete!.ToUpperInvariant());
        }

        if (!string.Equals(Match, "NONE", StringComparison.OrdinalIgnoreCase))
        {
            builder.Append(" MATCH ").Append(Match.ToUpperInvariant());
        }

        return builder.Append(';').ToString();
    }
}

public sealed class SqlitePrimaryKeyTableConstraint : SQLiteConstraint
{
    public string? Name { get; set; }
    public string[] Columns { get; set; } = Array.Empty<string>();
    public string ConflictClause { get; set; } = "NONE";

    public SqlitePrimaryKeyTableConstraint()
    {
        ConstraintType = SqliteConstraintType.PrimaryKey;
    }

    public SqlitePrimaryKeyTableConstraint(IDictionary definition)
        : this()
    {
        Name = DefinitionReader.AsString(DefinitionReader.Get(definition, "Name"));
        Columns = DefinitionReader.AsStrings(DefinitionReader.Get(definition, "Columns")).ToArray();
        ConflictClause = DefinitionReader.AsString(DefinitionReader.Get(definition, "ConflictClause")) ?? "NONE";
    }

    public override string ToString()
    {
        if (Columns.Length == 0)
        {
            throw new ArgumentException("At least one column is required for a primary key constraint.");
        }

        var builder = new StringBuilder("CONSTRAINT ");
        if (!string.IsNullOrWhiteSpace(Name))
        {
            builder.Append(SqlIdentifier.Quote(Name!)).Append(' ');
        }

        builder.Append("PRIMARY KEY (")
            .Append(string.Join(", ", Columns.Select(SqlIdentifier.Quote)))
            .Append(')');

        if (!string.IsNullOrWhiteSpace(ConflictClause) &&
            !string.Equals(ConflictClause, "NONE", StringComparison.OrdinalIgnoreCase))
        {
            builder.Append(" ON CONFLICT ").Append(ConflictClause.ToUpperInvariant());
        }

        return builder.ToString();
    }
}

public sealed class SqliteCheckTableConstraint : SQLiteConstraint
{
    public string? Name { get; set; }
    public string? TableName { get; set; }
    public string? ColumnName { get; set; }
    public string? CheckExpression { get; set; }

    public SqliteCheckTableConstraint()
    {
        ConstraintType = SqliteConstraintType.Check;
    }

    public SqliteCheckTableConstraint(IDictionary definition)
        : this()
    {
        Name = DefinitionReader.AsString(DefinitionReader.Get(definition, "Name"));
        TableName = DefinitionReader.AsString(DefinitionReader.Get(definition, "TableName"))
            ?? DefinitionReader.AsString(DefinitionReader.Get(definition, "Table"));
        ColumnName = DefinitionReader.AsString(DefinitionReader.Get(definition, "ColumnName"));
        CheckExpression = DefinitionReader.AsString(DefinitionReader.Get(definition, "CheckExpression"));
        ValidateConstraint();
    }

    public void ValidateConstraint()
    {
        if (string.IsNullOrWhiteSpace(TableName))
        {
            throw new ArgumentException("TableName is required for CHECK constraints.");
        }

        if (string.IsNullOrWhiteSpace(CheckExpression))
        {
            throw new ArgumentException("CheckExpression is required for CHECK constraints.");
        }
    }

    public override string ToString()
    {
        ValidateConstraint();
        var builder = new StringBuilder("CONSTRAINT ");
        if (!string.IsNullOrWhiteSpace(Name))
        {
            builder.Append(SqlIdentifier.Quote(Name!)).Append(' ');
        }

        return builder.Append("CHECK (").Append(CheckExpression).Append(')').ToString();
    }
}

public sealed class SQLiteColumn
{
    public string? Name { get; set; }
    public SqliteType Type { get; set; }
    public bool PrimaryKey { get; set; }
    public SqliteOrdering? PrimaryKeyOrder { get; set; }
    public bool AutoIncrement { get; set; }
    public bool AllowNull { get; set; } = true;
    public bool Unique { get; set; }
    public string? UniqueConflictClause { get; set; }
    public string? CheckExpression { get; set; }
    public object? DefaultValue { get; set; }
    public string? Collation { get; set; }
    public bool Indexed { get; set; }
    public string? References { get; set; }

    public SQLiteColumn()
    {
    }

    public SQLiteColumn(IDictionary definition)
    {
        Name = DefinitionReader.AsString(DefinitionReader.Get(definition, "Name"));
        var typeText = DefinitionReader.AsString(DefinitionReader.Get(definition, "Type"));
        if (!Enum.TryParse<SqliteType>(typeText, true, out var type))
        {
            throw new ArgumentException($"Column '{Name}' has an invalid SQLite type '{typeText}'.");
        }

        Type = type;
        PrimaryKey = DefinitionReader.AsBool(DefinitionReader.Get(definition, "PrimaryKey"));
        PrimaryKeyOrder = DefinitionReader.AsEnum<SqliteOrdering>(
            DefinitionReader.Get(definition, "PrimaryKeyOrder"),
            SqliteOrdering.NONE);
        AutoIncrement = DefinitionReader.AsBool(DefinitionReader.Get(definition, "AutoIncrement"));
        AllowNull = DefinitionReader.AsBool(DefinitionReader.Get(definition, "AllowNull"), true);
        Unique = DefinitionReader.AsBool(DefinitionReader.Get(definition, "Unique"));
        UniqueConflictClause = DefinitionReader.AsString(DefinitionReader.Get(definition, "UniqueConflictClause"));
        CheckExpression = DefinitionReader.AsString(DefinitionReader.Get(definition, "CheckExpression"));
        DefaultValue = DefinitionReader.Get(definition, "DefaultValue");
        Collation = DefinitionReader.AsString(DefinitionReader.Get(definition, "Collation"));
        Indexed = DefinitionReader.AsBool(DefinitionReader.Get(definition, "Indexed"));
        References = DefinitionReader.AsString(DefinitionReader.Get(definition, "References"));
    }

    public void ValidateDefinition()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("Column Name is required.");
        }
    }

    public override string ToString()
    {
        ValidateDefinition();
        var builder = new StringBuilder()
            .Append(SqlIdentifier.Quote(Name!))
            .Append(' ')
            .Append(Type.ToString().ToUpperInvariant());

        if (PrimaryKey)
        {
            builder.Append(" PRIMARY KEY");
            if (PrimaryKeyOrder is not null && PrimaryKeyOrder != SqliteOrdering.NONE)
            {
                builder.Append(' ').Append(PrimaryKeyOrder.Value.ToString().ToUpperInvariant());
            }

            if (Type == SqliteType.Integer && (AutoIncrement || PrimaryKey))
            {
                builder.Append(" AUTOINCREMENT");
            }
        }
        else if (!AllowNull)
        {
            builder.Append(" NOT NULL");
        }
        else if (Unique)
        {
            builder.Append(" UNIQUE");
            if (!string.IsNullOrWhiteSpace(UniqueConflictClause))
            {
                builder.Append(" ON CONFLICT ").Append(UniqueConflictClause!.ToUpperInvariant());
            }
        }
        else if (!string.IsNullOrWhiteSpace(CheckExpression))
        {
            builder.Append(" CHECK (").Append(CheckExpression).Append(')');
        }
        else if (DefaultValue is not null)
        {
            builder.Append(" DEFAULT ").Append(SqlLiteral(DefaultValue));
        }
        else if (!string.IsNullOrWhiteSpace(Collation))
        {
            builder.Append(" COLLATE ").Append(Collation);
        }
        else if (!string.IsNullOrWhiteSpace(References))
        {
            builder.Append(" REFERENCES ").Append(References);
        }

        return builder.ToString();
    }

    private static string SqlLiteral(object value)
    {
        if (value is bool boolean)
        {
            return boolean ? "1" : "0";
        }

        if (value is byte or short or int or long or float or double or decimal)
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture)!;
        }

        return "'" + Convert.ToString(value, CultureInfo.InvariantCulture)!.Replace("'", "''") + "'";
    }
}

public sealed class SqliteTable
{
    public string? Name { get; set; }
    public string? Schema { get; set; }
    public bool IfNotExists { get; set; } = true;
    public bool Temporary { get; set; }
    public bool Strict { get; set; }
    public List<SQLiteColumn> Columns { get; } = new();
    public List<SQLiteConstraint> Constraints { get; } = new();
    public List<SqliteTableOption> Options { get; } = new();

    public SqliteTable()
    {
    }

    public SqliteTable(IDictionary definition)
    {
        Name = DefinitionReader.AsString(DefinitionReader.Get(definition, "Name"));
        Schema = DefinitionReader.AsString(DefinitionReader.Get(definition, "Schema"));
        IfNotExists = DefinitionReader.AsBool(DefinitionReader.Get(definition, "IfNotExists"), true);
        Temporary = DefinitionReader.AsBool(DefinitionReader.Get(definition, "Temporary"));
        Strict = DefinitionReader.AsBool(DefinitionReader.Get(definition, "Strict"));

        if (DefinitionReader.Get(definition, "Columns") is IDictionary columns)
        {
            foreach (DictionaryEntry entry in columns)
            {
                var columnDefinition = entry.Value is IDictionary dictionary
                    ? DefinitionReader.ToDictionary(dictionary)
                    : new Dictionary<string, object?>();
                columnDefinition["Name"] = Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
                Columns.Add(new SQLiteColumn(columnDefinition));
            }
        }

        foreach (var constraintValue in DefinitionReader.ToList(DefinitionReader.Get(definition, "Constraints")))
        {
            if (constraintValue is not IDictionary constraint)
            {
                continue;
            }

            var constraintDefinition = DefinitionReader.ToDictionary(constraint);
            constraintDefinition["Table"] = Name;
            var type = DefinitionReader.AsString(DefinitionReader.Get(constraint, "Type"));
            switch (type?.ToUpperInvariant())
            {
                case "FOREIGNKEY":
                    Constraints.Add(new SqliteForeignKeyTableConstraint(constraintDefinition));
                    break;
                case "CHECK":
                    Constraints.Add(new SqliteCheckTableConstraint(constraintDefinition));
                    break;
                case "PRIMARYKEY":
                    Constraints.Add(new SqlitePrimaryKeyTableConstraint(constraintDefinition));
                    break;
                case "INDEX":
                    Constraints.Add(new SqliteIndexConstraint(constraintDefinition));
                    break;
                default:
                    throw new ArgumentException($"Unknown constraint type '{type}' for table '{Name}'.");
            }
        }

        foreach (var option in DefinitionReader.AsStrings(DefinitionReader.Get(definition, "Options")))
        {
            if (Enum.TryParse<SqliteTableOption>(option, true, out var parsed) && !Options.Contains(parsed))
            {
                Options.Add(parsed);
            }
        }

        if (Strict && !Options.Contains(SqliteTableOption.Strict))
        {
            Options.Add(SqliteTableOption.Strict);
        }
    }

    public void ValidateDefinition()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("Table Name is required.");
        }

        if (Columns.Count == 0)
        {
            throw new ArgumentException("At least one column is required in the table definition.");
        }

        foreach (var column in Columns)
        {
            column.ValidateDefinition();
        }
    }

    public string CreateString()
    {
        ValidateDefinition();
        var builder = new StringBuilder("CREATE");
        if (Temporary)
        {
            builder.Append(" TEMPORARY");
        }

        builder.Append(" TABLE");
        if (IfNotExists)
        {
            builder.Append(" IF NOT EXISTS");
        }

        builder.Append(' ');
        if (!string.IsNullOrWhiteSpace(Schema))
        {
            builder.Append(SqlIdentifier.Quote(Schema!)).Append('.');
        }

        builder.Append(SqlIdentifier.Quote(Name!)).AppendLine(" (");
        var definitions = Columns.Select(column => "    " + column).ToList();
        definitions.AddRange(
            Constraints
                .Where(constraint => constraint is not SqliteIndexConstraint)
                .Select(constraint => "    " + constraint));
        builder.AppendLine(string.Join(",\n", definitions));
        builder.Append(')');

        foreach (var option in Options)
        {
            builder.Append(' ').Append(option == SqliteTableOption.WithoutRowId ? "WITHOUT ROWID" : "STRICT");
        }

        return builder.AppendLine(";").ToString();
    }
}

public sealed class SqliteViewColumn
{
    public string? Name { get; set; }
    public string? Table { get; set; }
    public string? Column { get; set; }
    public string? Expression { get; set; }

    public SqliteViewColumn()
    {
    }

    public SqliteViewColumn(IDictionary definition)
    {
        Name = DefinitionReader.AsString(DefinitionReader.Get(definition, "Name"));
        Expression = DefinitionReader.AsString(DefinitionReader.Get(definition, "Expression"));

        if (DefinitionReader.Get(definition, "Source") is IDictionary source)
        {
            Table = DefinitionReader.AsString(DefinitionReader.Get(source, "Table"));
            Column = DefinitionReader.AsString(DefinitionReader.Get(source, "Column"));
            Expression ??= DefinitionReader.AsString(DefinitionReader.Get(source, "Expression"));
        }
        else
        {
            Table = DefinitionReader.AsString(DefinitionReader.Get(definition, "Table"));
            Column = DefinitionReader.AsString(DefinitionReader.Get(definition, "Column"));
        }

        ValidateDefinition();
    }

    public void ValidateDefinition()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("View column Name is required.");
        }
    }

    public bool HasSelectExpression()
    {
        return !string.IsNullOrWhiteSpace(Expression) || !string.IsNullOrWhiteSpace(Column);
    }

    public string GetSelectExpression()
    {
        if (!string.IsNullOrWhiteSpace(Expression))
        {
            return Expression!;
        }

        if (!string.IsNullOrWhiteSpace(Column))
        {
            return string.IsNullOrWhiteSpace(Table)
                ? SqlIdentifier.Quote(Column!)
                : $"{SqlIdentifier.Quote(Table!)}.{SqlIdentifier.Quote(Column!)}";
        }

        throw new ArgumentException($"View column '{Name}' must define Expression or Source.Column.");
    }

    public string ToSelectString()
    {
        return $"{GetSelectExpression()} AS {SqlIdentifier.Quote(Name!)}";
    }
}

public sealed class SqliteView
{
    public string? Name { get; set; }
    public string? Schema { get; set; }
    public bool IfNotExists { get; set; } = true;
    public List<SqliteViewColumn> Columns { get; } = new();
    public bool Distinct { get; set; }
    public object? From { get; set; }
    public List<object?> Joins { get; } = new();
    public object? Where { get; set; }
    public object? Having { get; set; }
    public List<object?> GroupBy { get; } = new();
    public List<object?> OrderBy { get; } = new();
    public string? Sql { get; set; }

    public SqliteView()
    {
    }

    public SqliteView(IDictionary definition)
    {
        Name = DefinitionReader.AsString(DefinitionReader.Get(definition, "Name"));
        Schema = DefinitionReader.AsString(DefinitionReader.Get(definition, "Schema"));
        IfNotExists = DefinitionReader.AsBool(DefinitionReader.Get(definition, "IfNotExists"), true);

        var select = DefinitionReader.Get(definition, "Select") as IDictionary;
        var columns = DefinitionReader.Get(definition, "Columns") ?? (select is null ? null : DefinitionReader.Get(select, "Columns"));
        AddColumns(columns);

        var distinct = DefinitionReader.Get(definition, "Distinct") ?? (select is null ? null : DefinitionReader.Get(select, "Distinct"));
        Distinct = DefinitionReader.AsBool(distinct);
        From = DefinitionReader.Get(definition, "From");
        Joins.AddRange(DefinitionReader.ToList(DefinitionReader.Get(definition, "Joins")));
        Where = DefinitionReader.Get(definition, "Where");
        Having = DefinitionReader.Get(definition, "Having");

        var groupBy = DefinitionReader.Get(definition, "GroupBy");
        if (groupBy is IDictionary groupDefinition && DefinitionReader.Contains(groupDefinition, "Columns"))
        {
            GroupBy.AddRange(DefinitionReader.ToList(DefinitionReader.Get(groupDefinition, "Columns")));
        }
        else
        {
            GroupBy.AddRange(DefinitionReader.ToList(groupBy));
        }

        OrderBy.AddRange(DefinitionReader.ToList(DefinitionReader.Get(definition, "OrderBy")));
        Sql = DefinitionReader.AsString(DefinitionReader.Get(definition, "Sql"));
        ValidateDefinition();
    }

    private void AddColumns(object? columnDefinitions)
    {
        if (columnDefinitions is null)
        {
            return;
        }

        if (columnDefinitions is not IDictionary columns)
        {
            throw new ArgumentException($"Columns for view '{Name}' must be defined as a mapping.");
        }

        foreach (DictionaryEntry entry in columns)
        {
            var column = entry.Value is IDictionary definition
                ? DefinitionReader.ToDictionary(definition)
                : new Dictionary<string, object?>();
            column["Name"] = Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
            Columns.Add(new SqliteViewColumn(column));
        }
    }

    public void ValidateDefinition()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("View Name is required.");
        }

        if (Columns.Count == 0)
        {
            throw new ArgumentException($"At least one output column is required for view '{Name}'.");
        }

        foreach (var column in Columns)
        {
            column.ValidateDefinition();
        }

        if (!string.IsNullOrWhiteSpace(Sql))
        {
            if (From is not null || Joins.Count > 0 || Where is not null || GroupBy.Count > 0 || Having is not null || OrderBy.Count > 0 || Distinct)
            {
                throw new ArgumentException($"View '{Name}' cannot define both Sql and structured select members.");
            }

            return;
        }

        if (From is null)
        {
            throw new ArgumentException($"Structured view '{Name}' must define From.");
        }

        foreach (var column in Columns)
        {
            if (!column.HasSelectExpression())
            {
                throw new ArgumentException($"Structured view '{Name}' column '{column.Name}' must define Expression or Source.Column.");
            }
        }
    }

    public string CreateString()
    {
        ValidateDefinition();
        var builder = new StringBuilder("CREATE VIEW ");
        if (IfNotExists)
        {
            builder.Append("IF NOT EXISTS ");
        }

        if (!string.IsNullOrWhiteSpace(Schema))
        {
            builder.Append(SqlIdentifier.Quote(Schema!)).Append('.');
        }

        builder.Append(SqlIdentifier.Quote(Name!))
            .Append(" (")
            .Append(string.Join(", ", Columns.Select(column => SqlIdentifier.Quote(column.Name!))))
            .AppendLine(") AS");

        if (!string.IsNullOrWhiteSpace(Sql))
        {
            var rawSql = Sql!.Trim();
            builder.AppendLine(rawSql.EndsWith(";", StringComparison.Ordinal) ? rawSql : rawSql + ";");
        }
        else
        {
            builder.AppendLine(BuildSelectStatement() + ";");
        }

        return builder.ToString();
    }

    private string BuildSelectStatement()
    {
        var builder = new StringBuilder("SELECT ");
        if (Distinct)
        {
            builder.Append("DISTINCT ");
        }

        builder.AppendLine(string.Join(", ", Columns.Select(FormatViewColumn)));
        builder.Append("FROM ").Append(FormatSource(From));

        foreach (var join in Joins)
        {
            builder.AppendLine().Append(FormatJoin(join));
        }

        if (Where is not null)
        {
            builder.AppendLine().Append("WHERE ").Append(FormatCondition(Where));
        }

        if (GroupBy.Count > 0)
        {
            builder.AppendLine().Append("GROUP BY ")
                .Append(string.Join(", ", GroupBy.Select(FormatReference)));
        }

        if (Having is not null)
        {
            builder.AppendLine().Append("HAVING ").Append(FormatCondition(Having));
        }

        if (OrderBy.Count > 0)
        {
            builder.AppendLine().Append("ORDER BY ")
                .Append(string.Join(", ", OrderBy.Select(FormatOrderBy)));
        }

        return builder.ToString().TrimEnd();
    }

    private string FormatViewColumn(SqliteViewColumn column)
    {
        return !string.IsNullOrWhiteSpace(column.Expression)
            ? $"{column.Expression} AS {SqlIdentifier.Quote(column.Name!)}"
            : $"{FormatReference(new Dictionary<string, object?> { ["Table"] = column.Table, ["Column"] = column.Column })} AS {SqlIdentifier.Quote(column.Name!)}";
    }

    private string FormatSource(object? definition)
    {
        if (definition is string source)
        {
            return source;
        }

        if (definition is not IDictionary dictionary)
        {
            throw new ArgumentException($"Invalid source definition for view '{Name}'.");
        }

        var table = DefinitionReader.AsString(DefinitionReader.Get(dictionary, "Table"))
            ?? DefinitionReader.AsString(DefinitionReader.Get(dictionary, "Name"));
        if (string.IsNullOrWhiteSpace(table))
        {
            throw new ArgumentException($"View '{Name}' source definitions must include Table or Name.");
        }

        var alias = DefinitionReader.AsString(DefinitionReader.Get(dictionary, "Alias"));
        return string.IsNullOrWhiteSpace(alias)
            ? SqlIdentifier.QuoteQualified(table)
            : $"{SqlIdentifier.QuoteQualified(table)} AS {SqlIdentifier.Quote(alias)}";
    }

    private string FormatReference(object? reference)
    {
        if (reference is string text)
        {
            return text;
        }

        if (reference is not IDictionary dictionary)
        {
            throw new ArgumentException($"Invalid reference definition for view '{Name}'.");
        }

        var expression = DefinitionReader.AsString(DefinitionReader.Get(dictionary, "Expression"));
        if (!string.IsNullOrWhiteSpace(expression))
        {
            return expression!;
        }

        var column = DefinitionReader.AsString(DefinitionReader.Get(dictionary, "Column"))
            ?? DefinitionReader.AsString(DefinitionReader.Get(dictionary, "Name"));
        if (string.IsNullOrWhiteSpace(column))
        {
            throw new ArgumentException($"View '{Name}' references must include Column or Expression.");
        }

        var table = DefinitionReader.AsString(DefinitionReader.Get(dictionary, "Table"))
            ?? DefinitionReader.AsString(DefinitionReader.Get(dictionary, "Alias"));
        return string.IsNullOrWhiteSpace(table)
            ? SqlIdentifier.Quote(column)
            : $"{ResolveReferencedTable(table!)}.{SqlIdentifier.Quote(column)}";
    }

    private string ResolveReferencedTable(string table)
    {
        foreach (var source in new[] { From }.Concat(Joins))
        {
            if (source is not IDictionary dictionary)
            {
                continue;
            }

            var sourceName = DefinitionReader.AsString(DefinitionReader.Get(dictionary, "Table"))
                ?? DefinitionReader.AsString(DefinitionReader.Get(dictionary, "Name"));
            var alias = DefinitionReader.AsString(DefinitionReader.Get(dictionary, "Alias"));
            if (!string.IsNullOrWhiteSpace(alias) &&
                (string.Equals(table, alias, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(table, sourceName, StringComparison.OrdinalIgnoreCase)))
            {
                return SqlIdentifier.Quote(alias!);
            }

            if (string.Equals(table, sourceName, StringComparison.OrdinalIgnoreCase))
            {
                return SqlIdentifier.QuoteQualified(sourceName!);
            }
        }

        return SqlIdentifier.QuoteQualified(table);
    }

    private string FormatOperand(object? operand)
    {
        if (operand is IDictionary dictionary)
        {
            if (DefinitionReader.Contains(dictionary, "Value"))
            {
                return ConvertSqlLiteral(DefinitionReader.Get(dictionary, "Value"));
            }

            return FormatReference(dictionary);
        }

        if (operand is IEnumerable enumerable and not string)
        {
            var values = new List<string>();
            foreach (var value in enumerable)
            {
                values.Add(ConvertSqlLiteral(value));
            }

            return "(" + string.Join(", ", values) + ")";
        }

        return ConvertSqlLiteral(operand);
    }

    private static string ConvertSqlLiteral(object? value)
    {
        if (value is null)
        {
            return "NULL";
        }

        if (value is bool boolean)
        {
            return boolean ? "1" : "0";
        }

        if (value is DateTime dateTime)
        {
            return "'" + dateTime.ToString("O", CultureInfo.InvariantCulture) + "'";
        }

        if (value is byte or short or int or long or float or double or decimal)
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture)!;
        }

        return "'" + Convert.ToString(value, CultureInfo.InvariantCulture)!.Replace("'", "''") + "'";
    }

    private string FormatCondition(object? condition)
    {
        if (condition is string text)
        {
            return text;
        }

        if (condition is not IDictionary dictionary)
        {
            throw new ArgumentException($"Invalid condition definition for view '{Name}'.");
        }

        if (DefinitionReader.Contains(dictionary, "All"))
        {
            var conditions = DefinitionReader.ToList(DefinitionReader.Get(dictionary, "All"))
                .Select(FormatCondition)
                .ToArray();
            if (conditions.Length == 0)
            {
                throw new ArgumentException($"Condition group 'All' for view '{Name}' must contain a condition.");
            }

            return "(" + string.Join(" AND ", conditions) + ")";
        }

        if (DefinitionReader.Contains(dictionary, "Any"))
        {
            var conditions = DefinitionReader.ToList(DefinitionReader.Get(dictionary, "Any"))
                .Select(FormatCondition)
                .ToArray();
            if (conditions.Length == 0)
            {
                throw new ArgumentException($"Condition group 'Any' for view '{Name}' must contain a condition.");
            }

            return "(" + string.Join(" OR ", conditions) + ")";
        }

        if (!DefinitionReader.Contains(dictionary, "Left") || !DefinitionReader.Contains(dictionary, "Operator"))
        {
            throw new ArgumentException($"View '{Name}' conditions must define Left and Operator.");
        }

        var left = FormatReference(DefinitionReader.Get(dictionary, "Left"));
        var op = (DefinitionReader.AsString(DefinitionReader.Get(dictionary, "Operator")) ?? string.Empty).ToUpperInvariant();
        if (DefinitionReader.Contains(dictionary, "Right"))
        {
            return $"{left} {op} {FormatOperand(DefinitionReader.Get(dictionary, "Right"))}";
        }

        if (op is "IS NULL" or "IS NOT NULL")
        {
            return $"{left} {op}";
        }

        throw new ArgumentException($"View '{Name}' condition '{op}' requires Right.");
    }

    private string FormatJoin(object? joinDefinition)
    {
        if (joinDefinition is not IDictionary dictionary)
        {
            throw new ArgumentException($"Invalid join definition for view '{Name}'.");
        }

        var joinType = DefinitionReader.AsString(DefinitionReader.Get(dictionary, "Type")) ?? "INNER";
        var source = new Dictionary<string, object?>();
        var table = DefinitionReader.Get(dictionary, "Table") ?? DefinitionReader.Get(dictionary, "Name");
        source["Table"] = table;
        source["Alias"] = DefinitionReader.Get(dictionary, "Alias");
        if (!DefinitionReader.Contains(dictionary, "On"))
        {
            throw new ArgumentException($"Join definitions for view '{Name}' must define On.");
        }

        return $"{joinType.ToUpperInvariant()} JOIN {FormatSource(source)} ON {FormatCondition(DefinitionReader.Get(dictionary, "On"))}";
    }

    private string FormatOrderBy(object? definition)
    {
        if (definition is string text)
        {
            return text;
        }

        if (definition is not IDictionary dictionary)
        {
            throw new ArgumentException($"Invalid OrderBy definition for view '{Name}'.");
        }

        var direction = DefinitionReader.AsString(DefinitionReader.Get(dictionary, "Direction")) ?? "ASC";
        return $"{FormatReference(dictionary)} {direction.ToUpperInvariant()}";
    }
}

public sealed class SqliteDBSchema
{
    public List<SqliteTable> Tables { get; } = new();
    public List<SqliteView> Views { get; } = new();
    public List<SqliteIndexConstraint> Indexes { get; } = new();

    public SqliteDBSchema()
    {
    }

    public SqliteDBSchema(IDictionary definition)
    {
        if (DefinitionReader.Get(definition, "Tables") is IDictionary tables)
        {
            foreach (DictionaryEntry entry in tables)
            {
                var table = entry.Value is IDictionary tableDefinition
                    ? DefinitionReader.ToDictionary(tableDefinition)
                    : new Dictionary<string, object?>();
                table["Name"] = Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
                Tables.Add(new SqliteTable(table));
            }
        }

        if (DefinitionReader.Get(definition, "Views") is IDictionary views)
        {
            foreach (DictionaryEntry entry in views)
            {
                var view = entry.Value is IDictionary viewDefinition
                    ? DefinitionReader.ToDictionary(viewDefinition)
                    : new Dictionary<string, object?>();
                view["Name"] = Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
                Views.Add(new SqliteView(view));
            }
        }

        if (DefinitionReader.Get(definition, "Indexes") is IDictionary indexes)
        {
            foreach (DictionaryEntry entry in indexes)
            {
                var index = entry.Value is IDictionary indexDefinition
                    ? DefinitionReader.ToDictionary(indexDefinition)
                    : new Dictionary<string, object?>();
                index["Name"] = Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
                Indexes.Add(new SqliteIndexConstraint(index));
            }
        }
    }

    public void ValidateDefinition()
    {
        if (Tables.Count == 0 && Views.Count == 0)
        {
            throw new ArgumentException("At least one table or view is required in the schema.");
        }

        foreach (var table in Tables)
        {
            table.ValidateDefinition();
        }

        foreach (var view in Views)
        {
            view.ValidateDefinition();
        }

        var duplicateNames = Tables.Select(table => table.Name!)
            .Concat(Views.Select(view => view.Name!))
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateNames.Length > 0)
        {
            throw new ArgumentException("Table and view names must be unique. Duplicate names: " + string.Join(", ", duplicateNames));
        }

        foreach (var index in Indexes)
        {
            index.ValidateDefinition();
        }
    }

    public SqliteTable? GetTable(string name)
    {
        return Tables.FirstOrDefault(table => string.Equals(table.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public SqliteView? GetView(string name)
    {
        return Views.FirstOrDefault(view => string.Equals(view.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public object? GetSelectable(string name)
    {
        return (object?)GetTable(name) ?? GetView(name);
    }

    public string GetSchemaSDL()
    {
        ValidateDefinition();
        var builder = new StringBuilder();
        foreach (var table in Tables)
        {
            builder.AppendLine(table.CreateString());
        }

        foreach (var view in Views)
        {
            builder.AppendLine(view.CreateString());
        }

        foreach (var index in Indexes)
        {
            builder.AppendLine(index.CreateString());
        }

        foreach (var table in Tables)
        {
            foreach (var index in table.Constraints.OfType<SqliteIndexConstraint>())
            {
                builder.AppendLine(index.CreateString());
            }
        }

        return builder.ToString();
    }
}
