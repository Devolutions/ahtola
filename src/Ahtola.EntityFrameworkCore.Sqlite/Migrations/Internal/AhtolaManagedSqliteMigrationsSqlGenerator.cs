using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Sqlite.Metadata.Internal;
using System.Text;

namespace Ahtola.EntityFrameworkCore.Sqlite.Migrations.Internal;

public sealed class AhtolaManagedSqliteMigrationsSqlGenerator(
    MigrationsSqlGeneratorDependencies dependencies,
    IRelationalAnnotationProvider migrationsAnnotations)
    : SqliteMigrationsSqlGenerator(dependencies, migrationsAnnotations)
{
    private bool _idempotent;
    private HashSet<(string Table, string? Schema)> _rebuildTables = [];
    private (string Table, string? Schema)? _pendingRebuildCopy;

    public override IReadOnlyList<MigrationCommand> Generate(
        IReadOnlyList<MigrationOperation> operations,
        IModel? model = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
    {
        var plannedOperations = ManagedSqliteTableRebuildPlanner.Plan(operations, model);
        _idempotent = options.HasFlag(MigrationsSqlGenerationOptions.Idempotent);
        if (_idempotent)
        {
            ManagedSqliteTableRebuildPlanner.ValidateIdempotent(
                plannedOperations,
                options.HasFlag(MigrationsSqlGenerationOptions.Script));
        }
        _rebuildTables = ManagedSqliteTableRebuildPlanner.GetRebuildTables(plannedOperations);
        try
        {
            if (options.HasFlag(MigrationsSqlGenerationOptions.Script) && _rebuildTables.Count != 0)
            {
                throw new NotSupportedException(
                    "The managed local provider cannot generate a standalone script for a table rebuild because arbitrary live trigger definitions can only be captured and restored by the managed migration executor.");
            }

            var commands = base.Generate(plannedOperations, model, options);
            if (_pendingRebuildCopy is { } pending)
            {
                throw new InvalidOperationException(
                    $"The managed SQLite rebuild for table '{pending.Table}' did not generate its expected copy operation.");
            }

            return commands;
        }
        finally
        {
            _idempotent = false;
            _rebuildTables = [];
            _pendingRebuildCopy = null;
        }
    }

    protected override void ColumnDefinition(
        string? schema,
        string table,
        string name,
        ColumnOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        var autoincrement = operation.FindAnnotation(SqliteAnnotationNames.Autoincrement);
        var legacyAutoincrement = operation.FindAnnotation(SqliteAnnotationNames.LegacyAutoincrement);
        operation.RemoveAnnotation(SqliteAnnotationNames.Autoincrement);
        operation.RemoveAnnotation(SqliteAnnotationNames.LegacyAutoincrement);

        try
        {
            base.ColumnDefinition(schema, table, name, operation, model, builder);
        }
        finally
        {
            if (autoincrement is not null)
                operation.SetAnnotation(autoincrement.Name, autoincrement.Value);

            if (legacyAutoincrement is not null)
                operation.SetAnnotation(legacyAutoincrement.Name, legacyAutoincrement.Value);
        }
    }

    protected override void Generate(
        CreateTableOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true)
    {
        if (operation.Name.StartsWith("ef_temp_", StringComparison.Ordinal))
        {
            var rebuild = (operation.Name["ef_temp_".Length..], operation.Schema);
            if (_rebuildTables.Contains(rebuild))
            {
                if (_pendingRebuildCopy is not null)
                    throw new InvalidOperationException("Managed SQLite rebuild copy operations overlapped.");

                _pendingRebuildCopy = rebuild;
            }
        }

        if (!_idempotent && operation.Schema is null)
        {
            base.Generate(operation, model, builder, terminate);
            return;
        }

        builder
            .Append(_idempotent ? "CREATE TABLE IF NOT EXISTS " : "CREATE TABLE ")
            .Append(DelimitIdentifier(operation.Name, operation.Schema))
            .AppendLine(" (");

        using (builder.Indent())
        {
            CreateTableColumns(operation, model, builder);
            CreateTableConstraints(operation, model, builder);
            builder.AppendLine();
        }

        builder.Append(")");
        if (terminate)
        {
            builder.AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);
            EndStatement(builder);
        }
    }

    protected override void Generate(
        CreateIndexOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true)
    {
        if (!_idempotent && operation.Schema is null)
        {
            base.Generate(operation, model, builder, terminate);
            return;
        }

        builder.Append("CREATE ");
        if (operation.IsUnique)
            builder.Append("UNIQUE ");

        IndexTraits(operation, model, builder);
        builder
            .Append(_idempotent ? "INDEX IF NOT EXISTS " : "INDEX ")
            .Append(DelimitIdentifier(operation.Name, operation.Schema))
            .Append(" ON ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table))
            .Append(" (");
        GenerateIndexColumnList(operation, model, builder);
        builder.Append(")");
        IndexOptions(operation, model, builder);

        if (terminate)
        {
            builder.AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);
            EndStatement(builder);
        }
    }

    protected override void Generate(
        DropIndexOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true)
    {
        if (!_idempotent && operation.Schema is null)
        {
            base.Generate(operation, model, builder, terminate);
            return;
        }

        builder
            .Append(_idempotent ? "DROP INDEX IF EXISTS " : "DROP INDEX ")
            .Append(DelimitIdentifier(operation.Name, operation.Schema));
        if (terminate)
        {
            builder.AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);
            EndStatement(builder);
        }
    }

    protected override void Generate(
        DropTableOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true)
    {
        if (_rebuildTables.Contains((operation.Name, operation.Schema)))
        {
            builder
                .Append("-- AHTOLA_REBUILD:")
                .Append(operation.Schema is null ? "-" : EncodeMarkerValue(operation.Schema))
                .Append(":")
                .Append(EncodeMarkerValue(operation.Name))
                .AppendLine();
        }

        if (!_idempotent && operation.Schema is null)
        {
            base.Generate(operation, model, builder, terminate);
            return;
        }

        builder
            .Append(_idempotent ? "DROP TABLE IF EXISTS " : "DROP TABLE ")
            .Append(DelimitIdentifier(operation.Name, operation.Schema));
        if (terminate)
        {
            builder.AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);
            EndStatement(builder);
        }
    }

    private static string EncodeMarkerValue(string value)
        => Convert.ToHexString(Encoding.UTF8.GetBytes(value));

    private string DelimitIdentifier(string name, string? schema)
        => schema is null
            ? Dependencies.SqlGenerationHelper.DelimitIdentifier(name)
            : Dependencies.SqlGenerationHelper.DelimitIdentifier(schema)
                + "."
                + Dependencies.SqlGenerationHelper.DelimitIdentifier(name);

    protected override void Generate(
        RenameTableOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        if (operation.NewName is null || operation.NewName == operation.Name)
            return;

        builder
            .Append("ALTER TABLE ")
            .Append(DelimitIdentifier(operation.Name, operation.Schema))
            .Append(" RENAME TO ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.NewName))
            .AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);
        EndStatement(builder);
    }

    protected override void Generate(
        RenameColumnOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        builder
            .Append("ALTER TABLE ")
            .Append(DelimitIdentifier(operation.Table, operation.Schema))
            .Append(" RENAME COLUMN ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
            .Append(" TO ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.NewName))
            .AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);
        EndStatement(builder);
    }

    protected override void Generate(
        SqlOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        var originalSql = operation.Sql;
        var rebuild = _pendingRebuildCopy;
        if (rebuild is { } pending)
        {
            var unqualifiedTemporary =
                Dependencies.SqlGenerationHelper.DelimitIdentifier("ef_temp_" + pending.Table);
            var unqualifiedTable = Dependencies.SqlGenerationHelper.DelimitIdentifier(pending.Table);
            if (!operation.Sql.Contains("INSERT INTO " + unqualifiedTemporary, StringComparison.Ordinal)
                || !operation.Sql.Contains("FROM " + unqualifiedTable, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The managed SQLite rebuild for table '{pending.Table}' generated an unexpected copy operation.");
            }

            if (pending.Schema is not null)
            {
                operation.Sql = operation.Sql
                    .Replace(
                        "INSERT INTO " + unqualifiedTemporary,
                        "INSERT INTO " + DelimitIdentifier("ef_temp_" + pending.Table, pending.Schema),
                        StringComparison.Ordinal)
                    .Replace(
                        "FROM " + unqualifiedTable,
                        "FROM " + DelimitIdentifier(pending.Table, pending.Schema),
                        StringComparison.Ordinal);
            }
        }

        try
        {
            base.Generate(operation, model, builder);
        }
        finally
        {
            operation.Sql = originalSql;
            if (rebuild is not null)
                _pendingRebuildCopy = null;
        }
    }

    protected override void ForeignKeyConstraint(
        AddForeignKeyOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
        => base.ForeignKeyConstraint(operation, model, builder);
}

internal static class ManagedSqliteTableRebuildPlanner
{
    public static HashSet<(string Table, string? Schema)> GetRebuildTables(
        IReadOnlyList<MigrationOperation> operations)
    {
        var rebuilds = new HashSet<(string Table, string? Schema)>();
        var createdTables = new HashSet<(string Table, string? Schema)>();

        foreach (var operation in operations)
        {
            switch (operation)
            {
                case CreateTableOperation create:
                    createdTables.Add((create.Name, create.Schema));
                    break;

                case AddPrimaryKeyOperation primaryKey:
                    rebuilds.Add((primaryKey.Table, primaryKey.Schema));
                    break;
                case AddUniqueConstraintOperation unique:
                    rebuilds.Add((unique.Table, unique.Schema));
                    break;
                case AddCheckConstraintOperation check:
                    rebuilds.Add((check.Table, check.Schema));
                    break;
                case AlterTableOperation table:
                    rebuilds.Add((table.Name, table.Schema));
                    break;
                case DropCheckConstraintOperation check:
                    rebuilds.Add((check.Table, check.Schema));
                    break;
                case DropForeignKeyOperation foreignKey:
                    rebuilds.Add((foreignKey.Table, foreignKey.Schema));
                    break;
                case DropPrimaryKeyOperation primaryKey:
                    rebuilds.Add((primaryKey.Table, primaryKey.Schema));
                    break;
                case DropUniqueConstraintOperation unique:
                    rebuilds.Add((unique.Table, unique.Schema));
                    break;
                case DropColumnOperation column:
                    rebuilds.Add((column.Table, column.Schema));
                    break;
                case AlterColumnOperation column:
                    rebuilds.Add((column.Table, column.Schema));
                    break;
                case AddColumnOperation column when column.Comment is not null:
                    rebuilds.Add((column.Table, column.Schema));
                    break;
                case AddForeignKeyOperation foreignKey
                    when !createdTables.Contains((foreignKey.Table, foreignKey.Schema)):
                    rebuilds.Add((foreignKey.Table, foreignKey.Schema));
                    break;

                case RenameTableOperation rename:
                    if (rebuilds.Remove((rename.Name, rename.Schema)))
                    {
                        rebuilds.Add(
                            (rename.NewName ?? rename.Name, rename.NewSchema ?? rename.Schema));
                    }
                    break;
            }
        }

        return rebuilds;
    }

    public static IReadOnlyList<MigrationOperation> Plan(
        IReadOnlyList<MigrationOperation> operations,
        IModel? model)
    {
        var planned = new List<MigrationOperation>(operations.Count);
        foreach (var operation in operations)
        {
            switch (operation)
            {
                case CreateTableOperation createTable:
                    foreach (var column in createTable.Columns)
                        ValidateComputedColumn(column);
                    foreach (var check in createTable.CheckConstraints)
                        ValidateExpression(check.Sql, "check constraint", check.Name, createTable.Name);
                    break;

                case AddColumnOperation column:
                    ValidateComputedColumn(column);
                    if (column.ComputedColumnSql is not null && column.IsStored is true)
                        ValidateRebuildTable(column.Table, column.Schema, model);
                    break;

                case AlterColumnOperation column:
                    ValidateComputedColumn(column);
                    ValidateRebuildTable(column.Table, column.Schema, model);
                    break;

                case CreateIndexOperation index:
                    if (index.Filter is not null)
                        ValidateExpression(index.Filter, "filtered index", index.Name, index.Table);
                    break;

                case RenameIndexOperation rename:
                    ValidateRenameIndex(rename, model);
                    break;

                case AddCheckConstraintOperation check:
                    ValidateExpression(check.Sql, "check constraint", check.Name, check.Table);
                    ValidateRebuildTable(check.Table, check.Schema, model);
                    break;

                case AddUniqueConstraintOperation unique:
                    ValidateRebuildTable(unique.Table, unique.Schema, model);
                    break;

                case DropCheckConstraintOperation check:
                    ValidateRebuildTable(check.Table, check.Schema, model);
                    break;

                case DropUniqueConstraintOperation unique:
                    ValidateRebuildTable(unique.Table, unique.Schema, model);
                    break;

                case AddPrimaryKeyOperation primaryKey:
                    ValidateRebuildTable(primaryKey.Table, primaryKey.Schema, model);
                    break;

                case DropPrimaryKeyOperation primaryKey:
                    ValidateRebuildTable(primaryKey.Table, primaryKey.Schema, model);
                    break;

                case AddForeignKeyOperation foreignKey:
                    if (!operations.OfType<CreateTableOperation>().Any(
                            table => table.Name == foreignKey.Table
                                && table.Schema == foreignKey.Schema))
                    {
                        ValidateRebuildTable(foreignKey.Table, foreignKey.Schema, model);
                    }
                    break;

                case DropForeignKeyOperation foreignKey:
                    ValidateRebuildTable(foreignKey.Table, foreignKey.Schema, model);
                    break;

                case DropColumnOperation column:
                    ValidateRebuildTable(column.Table, column.Schema, model);
                    break;

                case AlterTableOperation table:
                    ValidateRebuildTable(table.Name, table.Schema, model);
                    break;

                case RenameTableOperation rename
                    when rename.NewSchema is not null && rename.NewSchema != rename.Schema:
                    throw new NotSupportedException(
                        $"The managed local provider cannot move table '{rename.Name}' between attached schemas.");
            }

            if (operation is AddColumnOperation
                {
                    ComputedColumnSql: not null,
                    IsStored: true
                } storedColumn)
            {
                planned.Add(
                    new DropColumnOperation
                    {
                        Name = storedColumn.Name,
                        Table = storedColumn.Table,
                        Schema = storedColumn.Schema
                    });
            }

            planned.Add(operation);
        }

        return planned;
    }

    public static void ValidateIdempotent(
        IReadOnlyList<MigrationOperation> operations,
        bool standaloneScript)
    {
        foreach (var operation in operations)
        {
            if (operation is RenameTableOperation
                or RenameColumnOperation
                or InsertDataOperation
                or UpdateDataOperation
                or DeleteDataOperation
                or SqlOperation
                || operation is AddColumnOperation
                {
                    ComputedColumnSql: null
                }
                || operation is AddColumnOperation
                {
                    IsStored: not true
                })
            {
                throw new NotSupportedException(
                    $"The managed local provider cannot generate an honest idempotent script for '{operation.GetType().Name}' because SQLite cannot conditionally execute that operation.");
            }

            if (standaloneScript
                && operation is not CreateTableOperation
                && operation is not CreateIndexOperation
                && operation is not EnsureSchemaOperation)
            {
                throw new NotSupportedException(
                    $"The managed local provider cannot generate an honest standalone idempotent script for '{operation.GetType().Name}' because SQLite has no procedural __EFMigrationsHistory guard.");
            }
        }
    }

    private static void ValidateRebuildTable(string table, string? schema, IModel? model)
    {
        var relationalTable = model?.GetRelationalModel().FindTable(table, schema);
        if (relationalTable is null)
        {
            throw new InvalidOperationException(
                $"Rebuilding table '{table}' requires the target relational model so columns, indexes, foreign keys, defaults, generated expressions, and collations can be preserved.");
        }

        foreach (var column in relationalTable.Columns)
        {
            if (column.ComputedColumnSql is { } expression)
                ValidateExpression(expression, "computed column", column.Name, table);
        }

        foreach (var check in relationalTable.CheckConstraints)
            ValidateExpression(check.Sql, "check constraint", check.Name ?? "<unnamed>", table);

        foreach (var index in relationalTable.Indexes)
        {
            if (index.Filter is { } filter)
                ValidateExpression(filter, "filtered index", index.Name, table);
        }
    }

    private static void ValidateComputedColumn(ColumnOperation operation)
    {
        if (operation.ComputedColumnSql is { } expression)
            ValidateExpression(expression, "computed column", operation.Name, operation.Table);
    }

    private static void ValidateRenameIndex(RenameIndexOperation operation, IModel? model)
    {
        var targetIndex = operation.Table is null
            ? null
            : model?.GetRelationalModel()
                .FindTable(operation.Table, operation.Schema)?
                .Indexes.FirstOrDefault(index => index.Name == operation.NewName);
        if (targetIndex is null)
        {
            throw new NotSupportedException(
                $"The managed local provider can rename index '{operation.Name}' on '{operation.Table}' only when the target model contains '{operation.NewName}'.");
        }

        if (targetIndex.Filter is { } filter)
            ValidateExpression(filter, "filtered index", targetIndex.Name, operation.Table!);
    }

    private static void ValidateExpression(string expression, string kind, string name, string table)
    {
        if (string.IsNullOrWhiteSpace(expression)
            || ContainsUnsafeSql(expression))
        {
            throw new NotSupportedException(
                $"The managed local provider cannot use expression '{expression}' for {kind} '{name}' on '{table}'.");
        }
    }

    private static bool ContainsUnsafeSql(string sql)
    {
        for (var index = 0; index < sql.Length;)
        {
            var value = sql[index];
            if (value == '\0' || value == ';')
                return true;

            if (value is '\'' or '"' or '`' or '[')
            {
                if (!SkipQuoted(sql, ref index, value))
                    return true;

                continue;
            }

            if ((value == '-'
                    && index + 1 < sql.Length
                    && sql[index + 1] == '-')
                || (value == '/'
                    && index + 1 < sql.Length
                    && sql[index + 1] == '*'))
            {
                return true;
            }

            if (IsIdentifierCharacter(value))
            {
                var start = index++;
                while (index < sql.Length && IsIdentifierCharacter(sql[index]))
                    index++;

                var token = sql.AsSpan(start, index - start);
                if (token.Equals("SELECT", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("OVER", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("PRAGMA", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("ATTACH", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("DETACH", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                continue;
            }

            index++;
        }

        return false;
    }

    private static bool SkipQuoted(string sql, ref int index, char opening)
    {
        var closing = opening == '[' ? ']' : opening;
        index++;
        while (index < sql.Length)
        {
            if (sql[index] != closing)
            {
                index++;
                continue;
            }

            index++;
            if (index < sql.Length && sql[index] == closing && opening != '[')
            {
                index++;
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool IsIdentifierCharacter(char value)
        => char.IsLetterOrDigit(value) || value is '_' or '$';
}
