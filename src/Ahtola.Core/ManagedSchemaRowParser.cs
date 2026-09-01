using Ahtola.Core.Indexing;
using Ahtola.Core.Parsing;

namespace Ahtola.Core;

/// <summary>
/// An index reconstructed from a <c>sqlite_schema</c> row, together with the method-index state envelope
/// that was appended to its stored SQL (version 0 and an empty payload when the row carries none).
/// </summary>
internal sealed record ParsedManagedSchemaIndex(EmbeddedIndex Index, int StateVersion, byte[] State)
{
    /// <summary>Loads the parsed state into the index's method attachment, when it has one.</summary>
    public void RestoreMethodState(string tableName, EmbeddedTable table)
    {
        if (!Index.IsMethodIndex)
            return;

        ManagedIndexMethodSemantics
            .GetAttachment(tableName, table, Index)
            .LoadState(StateVersion, State);
    }
}

/// <summary>
/// Why a <c>sqlite_schema</c> row is being turned back into a definition.
/// </summary>
/// <remarks>
/// The difference is persistence-only validation. <see cref="Load"/> reads a row that has already been
/// written to storage, so a definition that cannot survive a reopen — one carrying bind parameters,
/// managed callbacks or unregistered collations — is a corrupt row and must be rejected. <see cref="Reparse"/>
/// re-reads a row the current statement just wrote into a transaction-local stage, where those
/// dependencies are still legal: an in-memory database may hold them forever, and a file-backed one
/// rejects them at persist time, exactly as it did before the statement was lowered to bytecode.
/// </remarks>
internal enum ManagedSchemaAdoptionMode
{
    /// <summary>The row came from storage.</summary>
    Load,

    /// <summary>The row came from the schema stage the running program is writing to.</summary>
    Reparse,
}

/// <summary>
/// Reconstructs catalog objects from <c>sqlite_schema</c> rows.
/// </summary>
/// <remarks>
/// <para>
/// This is the single reader shared by the two callers that turn stored schema SQL back into definitions:
/// <see cref="EmbeddedFileStore"/> when it reopens a database, and the <c>ParseSchema</c> opcode when a DDL
/// program republishes rows it just wrote. Keeping one implementation is the point — two catalog readers
/// would drift, and a row that reopens cleanly but reparses differently (or vice versa) is exactly the bug
/// class this prevents.
/// </para>
/// <para>
/// The parser is deliberately storage-free: it turns SQL text plus row metadata into definitions and
/// validates them, but never reads a page, never populates table rows, and never touches a live catalog.
/// Row loading and b-tree validation stay in the file store, where the pager lives; catalog adoption stays
/// with the caller, so it can be made atomic across a whole set of rows.
/// </para>
/// </remarks>
internal static class ManagedSchemaRowParser
{
    /// <summary>
    /// Rebuilds the table a <c>table</c> row declares. The returned table carries no rows; the caller
    /// supplies them from storage (reopen) or from the table it is replacing (reparse).
    /// </summary>
    public static EmbeddedTable ParseTable(ManagedSchemaRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        RequireType(row, ManagedSchemaRow.TableType);
        var sql = RequireSql(row);
        var statement = SqlParser.Parse(sql, SqlParameterMap.Parse(sql));
        if (statement is not CreateTableStatement create)
            throw new EmbeddedSqlException($"Stored schema for table '{row.Name}' is not a CREATE TABLE statement.");
        if (!string.Equals(create.Name, row.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new EmbeddedSqlException(
                $"Stored schema entry for table '{row.Name}' does not match its CREATE TABLE name.");
        }

        var table = new EmbeddedTable(
            row.Name,
            create.Columns,
            create.WithoutRowid,
            create.PrimaryKeyColumns,
            create.UniqueConstraints,
            create.CheckConstraints,
            create.PrimaryKeyConflictAlgorithm,
            create.PrimaryKeyConstraintName,
            create.PrimaryKeyDeclarationOrder,
            create.TableForeignKeys,
            create.Strict);
        table.Sql = create.Sql;
        // A CREATE TABLE AS SELECT stores its schema in SQLite's compact rendering, and an ALTER that
        // regenerates the text has to keep rendering it that way. The flag is not in the row, so it is
        // recovered structurally: the table is compact exactly when the compact rendering reproduces the
        // stored text byte for byte.
        table.SchemaSqlCompact = create.Sql is { } storedSql
            && string.Equals(
                EmbeddedDatabase.BuildCreateTableSql(row.Name, MarkCompact(table)),
                storedSql,
                StringComparison.Ordinal);
        return table;
    }

    private static EmbeddedTable MarkCompact(EmbeddedTable table)
    {
        table.SchemaSqlCompact = true;
        return table;
    }

    /// <summary>Rebuilds and instantiates the virtual table a rootpage-0 <c>table</c> row declares.</summary>
    public static EmbeddedDatabase.VirtualTableDefinition ParseVirtualTable(ManagedSchemaRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        RequireType(row, ManagedSchemaRow.TableType);
        var (declaration, payload) = ManagedVirtualTableSchemaSql.Parse(RequireSql(row));
        if (!string.Equals(declaration.Name, row.Name, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(row.Name, row.TableName, StringComparison.OrdinalIgnoreCase))
        {
            throw new EmbeddedSqlException(
                $"Stored virtual table '{row.Name}' does not match its sqlite_schema metadata.");
        }

        var virtualTable = ManagedVirtualTableModuleRegistry.Resolve(declaration.ModuleName).Create(
            new ManagedVirtualTableCreateContext(declaration.Name, declaration.Arguments),
            payload);
        ArgumentNullException.ThrowIfNull(virtualTable);
        return new EmbeddedDatabase.VirtualTableDefinition(
            declaration.Name,
            declaration.ModuleName,
            declaration.Arguments.ToArray(),
            payload,
            virtualTable);
    }

    /// <summary>
    /// Rebuilds the explicit index an <c>index</c> row declares against <paramref name="table"/>.
    /// Implicit constraint indexes carry no SQL and are matched against the table's own constraint indexes
    /// by the caller instead of being reconstructed here.
    /// </summary>
    public static ParsedManagedSchemaIndex ParseIndex(ManagedSchemaRow row, EmbeddedTable table)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(table);
        RequireType(row, ManagedSchemaRow.IndexType);
        if (row.Sql is null)
        {
            throw new EmbeddedSqlException(
                $"Stored implicit index '{row.Name}' has no SQL text to parse.");
        }

        // A method index carries its versioned state envelope in a trailing SQL comment. The envelope is
        // only stripped once the candidate declaration has been parsed and proven to be a USING-method
        // index, so an ordinary index whose own SQL text happens to end in a similar comment round-trips
        // untouched. A newer or malformed envelope fails closed instead of silently loading as empty.
        ManagedIndexMethodStateSql.TrySplit(
            row.Sql,
            ManagedIndexMethodSemantics.IsMethodIndexDeclaration,
            out var declarationSql,
            out var stateVersion,
            out var state);
        var statement = SqlParser.Parse(declarationSql, SqlParameterMap.Parse(declarationSql));
        if (statement is not CreateIndexStatement create)
            throw new EmbeddedSqlException($"Stored schema for index '{row.Name}' is not a CREATE INDEX statement.");
        if (!string.Equals(create.Name, row.Name, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(create.TableName, row.TableName, StringComparison.OrdinalIgnoreCase))
        {
            throw new EmbeddedSqlException(
                $"Stored schema entry for index '{row.Name}' does not match its sqlite_schema name or table.");
        }

        return new ParsedManagedSchemaIndex(
            EmbeddedIndexFactory.Create(row.TableName, table, create),
            stateVersion,
            state);
    }

    /// <summary>Rebuilds and validates the view a <c>view</c> row declares.</summary>
    public static ViewDefinition ParseView(
        ManagedSchemaRow row,
        ManagedSchemaAdoptionMode mode = ManagedSchemaAdoptionMode.Load)
    {
        ArgumentNullException.ThrowIfNull(row);
        RequireType(row, ManagedSchemaRow.ViewType);
        var sql = RequireSql(row);
        if (SqlParser.Parse(sql, SqlParameterMap.Parse(sql)) is not CreateViewStatement view)
            throw new EmbeddedSqlException($"Stored schema entry '{row.Name}' is not a CREATE VIEW statement.");

        ValidateView(row, view, mode);
        return new ViewDefinition(view.Name, view.Columns, view.Query, view.Sql);
    }

    /// <summary>Rebuilds and validates the trigger a <c>trigger</c> row declares.</summary>
    /// <param name="row">The <c>trigger</c> row to rebuild.</param>
    /// <param name="tables">The tables the trigger's target is resolved against.</param>
    /// <param name="views">The views an <c>INSTEAD OF</c> trigger's target is resolved against.</param>
    /// <param name="declarationOrder">The firing order the rebuilt trigger keeps.</param>
    /// <param name="mode">Whether the row came from storage or from the running program's stage.</param>
    /// <param name="targetSchema">
    /// The schema owning the table the trigger watches, when that is not the schema the trigger lives in.
    /// Only a TEMP trigger can reach across schemas, its target is not in <paramref name="tables"/>, and
    /// the stored SQL alone cannot say which schema supplied it — which is why upstream's
    /// <c>ParseSchema</c> carries the same fact as <c>trigger_target_database_id</c>.
    /// </param>
    /// <param name="temporary">Whether the trigger lives in the connection-private temp schema.</param>
    public static TriggerDefinition ParseTrigger(
        ManagedSchemaRow row,
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        IReadOnlyDictionary<string, ViewDefinition> views,
        long declarationOrder,
        ManagedSchemaAdoptionMode mode = ManagedSchemaAdoptionMode.Load,
        string? targetSchema = null,
        bool temporary = false)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(views);
        RequireType(row, ManagedSchemaRow.TriggerType);
        var sql = RequireSql(row);
        if (SqlParser.Parse(sql, SqlParameterMap.Parse(sql)) is not CreateTriggerStatement trigger)
            throw new EmbeddedSqlException($"Stored schema entry '{row.Name}' is not a CREATE TRIGGER statement.");

        ValidateTrigger(row, trigger, tables, views, mode, targetSchema);
        return new TriggerDefinition(
            trigger.Name,
            trigger.Timing,
            trigger.Event,
            trigger.UpdateOfColumns,
            LocalTableName(trigger.TableName),
            trigger.When,
            trigger.Body,
            trigger.Sql,
            declarationOrder,
            targetSchema,
            temporary);
    }

    /// <summary>
    /// SQLite keeps ON-clause schema qualifiers verbatim in stored trigger SQL
    /// (<c>CREATE TRIGGER ... ON main.t ...</c>), so a reparsed target may be qualified while the catalog
    /// keys are local.
    /// </summary>
    public static string LocalTableName(string name)
        => ManagedSchemaName.TrySplit(name, out _, out var local) ? local : name;

    internal static void ValidateView(
        ManagedSchemaRow row,
        CreateViewStatement view,
        ManagedSchemaAdoptionMode mode = ManagedSchemaAdoptionMode.Load)
    {
        if (!string.Equals(view.Name, row.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new EmbeddedSqlException(
                $"Stored schema entry for view '{row.Name}' does not match its CREATE VIEW name.");
        }

        if (mode == ManagedSchemaAdoptionMode.Load)
            EmbeddedFileStore.ValidateRuntimeIndependentQuery("view", row.Name, view.Query);
    }

    internal static void ValidateTrigger(
        ManagedSchemaRow row,
        CreateTriggerStatement trigger,
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        IReadOnlyDictionary<string, ViewDefinition> views,
        ManagedSchemaAdoptionMode mode = ManagedSchemaAdoptionMode.Load,
        string? targetSchema = null)
    {
        var targetName = LocalTableName(trigger.TableName);
        if (!string.Equals(trigger.Name, row.Name, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(targetName, row.TableName, StringComparison.OrdinalIgnoreCase))
        {
            throw new EmbeddedSqlException(
                $"Stored schema entry for trigger '{row.Name}' does not match its CREATE TRIGGER definition.");
        }

        // A temp trigger whose target lives in another schema watches a table this catalog cannot see;
        // the owning connection resolved and validated it before the statement was routed here.
        if (targetSchema is not null)
            return;

        var targetExists = trigger.Timing == TriggerTiming.InsteadOf
            ? views.ContainsKey(targetName)
            : tables.ContainsKey(targetName);
        if (!targetExists)
        {
            throw new EmbeddedSqlException(
                $"Stored trigger '{row.Name}' references missing target '{trigger.TableName}'.");
        }

        if (mode == ManagedSchemaAdoptionMode.Reparse)
            return;

        EmbeddedFileStore.ValidateRuntimeIndependentTriggerBody(row.Name, trigger.When, trigger.Body);
        EmbeddedFileStore.ValidateTriggerCollationDependencies(
            row.Name,
            new TriggerDefinition(
                trigger.Name,
                trigger.Timing,
                trigger.Event,
                trigger.UpdateOfColumns,
                targetName,
                trigger.When,
                trigger.Body,
                trigger.Sql,
                DeclarationOrder: 0),
            tables,
            views);
    }

    private static void RequireType(ManagedSchemaRow row, string expected)
    {
        if (!string.Equals(row.Type, expected, StringComparison.Ordinal))
        {
            throw new EmbeddedSqlException(
                $"Stored schema entry '{row.Name}' has type '{row.Type}' where '{expected}' was expected.");
        }
    }

    private static string RequireSql(ManagedSchemaRow row)
        => row.Sql ?? throw new EmbeddedSqlException($"Stored schema entry '{row.Name}' is missing SQL text.");
}
