global using Ahtola.Core.Parsing;

namespace Ahtola.Core.Parsing;

internal abstract record ParsedStatement;

internal abstract record QueryStatement : ParsedStatement;

internal sealed record CreateTableStatement(
    string Name,
    IReadOnlyList<EmbeddedColumn> Columns,
    bool IfNotExists,
    bool WithoutRowid = false,
    IReadOnlyList<TablePrimaryKeyColumn>? PrimaryKeyColumns = null,
    IReadOnlyList<TableUniqueConstraint>? UniqueConstraints = null,
    IReadOnlyList<CheckConstraint>? CheckConstraints = null,
    InsertConflictAlgorithm? PrimaryKeyConflictAlgorithm = null,
    string? PrimaryKeyConstraintName = null,
    int? PrimaryKeyDeclarationOrder = null,
    IReadOnlyList<ForeignKeyDefinition>? TableForeignKeys = null,
    bool Strict = false,
    IReadOnlyList<SqlValue[]>? InitialRows = null,
    string? Sql = null) : ParsedStatement;

/// <summary>
/// A module-backed catalog table. Module arguments deliberately retain SQL text rather than
/// being evaluated expressions: SQLite passes the CREATE VIRTUAL TABLE argument list to the module
/// at connect/create time, not once per scanned row.
/// </summary>
internal sealed record CreateVirtualTableStatement(
    string Name,
    string ModuleName,
    IReadOnlyList<string> Arguments,
    bool IfNotExists) : ParsedStatement;

internal sealed record CreateTableAsSelectStatement(
    string Name,
    QueryStatement Query,
    bool IfNotExists,
    bool Temporary) : ParsedStatement;

internal sealed record DropTableStatement(string Name, bool IfExists) : ParsedStatement;

internal sealed record CreateIndexStatement(
    string Name,
    string TableName,
    IReadOnlyList<IndexedColumnDefinition> Columns,
    bool Unique,
    bool IfNotExists,
    Expression? Where = null,
    string? WhereSql = null,
    string? Sql = null,
    string? Method = null,
    IReadOnlyList<Indexing.ManagedIndexMethodParameter>? MethodParameters = null) : ParsedStatement
{
    /// <summary>True for <c>CREATE INDEX … USING method (…)</c>.</summary>
    public bool IsMethodIndex => Method is not null;
}

internal sealed record DropIndexStatement(string Name, bool IfExists) : ParsedStatement;

internal sealed record IndexedColumnDefinition(
    string? Name,
    string? Collation,
    bool Descending,
    Expression? Expression = null,
    string? ExpressionSql = null)
{
    public bool IsExpression => Expression is not null;
}

internal sealed record CreateViewStatement(
    string Name,
    IReadOnlyList<string>? Columns,
    QueryStatement Query,
    string Sql,
    bool IfNotExists,
    bool Temporary = false) : ParsedStatement;

internal sealed record DropViewStatement(string Name, bool IfExists) : ParsedStatement;

internal enum TriggerEvent
{
    Insert,
    Update,
    Delete,
}

internal enum TriggerTiming
{
    Before,
    After,
    InsteadOf,
}

internal sealed record CreateTriggerStatement(
    string Name,
    TriggerTiming Timing,
    TriggerEvent Event,
    IReadOnlyList<string>? UpdateOfColumns,
    string TableName,
    Expression? When,
    IReadOnlyList<ParsedStatement> Body,
    string Sql,
    bool IfNotExists,
    bool Temporary = false,
    // Set when the trigger lives in a different schema than the table it watches, which
    // SQLite only allows for temp triggers. The owning connection validates the target.
    string? TargetSchema = null) : ParsedStatement;

internal sealed record DropTriggerStatement(string Name, bool IfExists) : ParsedStatement;

internal sealed record ViewDefinition(
    string Name,
    IReadOnlyList<string>? Columns,
    QueryStatement Query,
    string Sql);

internal sealed record TriggerDefinition(
    string Name,
    TriggerTiming Timing,
    TriggerEvent Event,
    IReadOnlyList<string>? UpdateOfColumns,
    string TableName,
    Expression? When,
    IReadOnlyList<ParsedStatement> Body,
    string Sql,
    long DeclarationOrder,
    // Non-null when the watched table lives in a different schema than the trigger. Only a
    // temp trigger can do this, and its body statements are routed by the owning connection.
    string? TargetSchema = null,
    // True when the trigger lives in the temp schema, which makes it connection private and
    // lets its body reach objects in other schemas.
    bool Temporary = false);

// A parser-only separator retains whether a dot was SQL syntax rather than part of a
// quoted identifier. Catalog object names remain ordinary strings after connection routing.
internal static class ManagedSchemaName
{
    private const char Separator = '\u001f';

    public static string Create(string schema, string name) => schema + Separator + name;

    public static bool TrySplit(string value, out string schema, out string name)
    {
        var separator = value.IndexOf(Separator);
        if (separator < 0)
        {
            schema = string.Empty;
            name = value;
            return false;
        }

        schema = value[..separator];
        name = value[(separator + 1)..];
        return true;
    }

    public static string Display(string value)
        => TrySplit(value, out var schema, out var name) ? schema + "." + name : value;
}

internal sealed record AlterTableAddColumnStatement(string TableName, EmbeddedColumn Column, string? ColumnSql = null) : ParsedStatement;

internal sealed record AlterTableRenameStatement(string TableName, string NewName) : ParsedStatement;

/// <summary>
/// <paramref name="QuoteNewName"/> mirrors SQLite's <c>bQuote</c>: when the replacement name
/// was written quoted in the ALTER statement, every rewritten reference in dependent schema
/// SQL is emitted quoted as well.
/// </summary>
internal sealed record AlterTableRenameColumnStatement(
    string TableName,
    string ColumnName,
    string NewName,
    bool QuoteNewName = false) : ParsedStatement;

internal sealed record AlterTableAlterColumnStatement(
    string TableName,
    string ColumnName,
    EmbeddedColumn Column) : ParsedStatement;

internal sealed record AlterTableDropColumnStatement(string TableName, string ColumnName) : ParsedStatement;

internal sealed record InsertStatement(
    string TableName,
    string[]? Columns,
    IReadOnlyList<Expression[]> Rows,
    QueryStatement? Source = null,
    IReadOnlyList<Projection>? Returning = null,
    UpsertClause? Upsert = null,
    InsertConflictAlgorithm? ConflictAlgorithm = null) : ParsedStatement;

internal enum InsertConflictAlgorithm
{
    Rollback,
    Abort,
    Fail,
    Ignore,
    Replace,
}

internal sealed record UpsertTargetColumn(
    string? Name,
    string? Collation,
    bool Descending = false,
    Expression? Expression = null,
    string? ExpressionSql = null,
    string? Qualifier = null,
    string? Schema = null)
{
    public bool IsExpression => Expression is not null;
}

internal abstract record UpsertAction;

internal sealed record DoNothingUpsertAction : UpsertAction;

internal sealed record DoUpdateUpsertAction(
    IReadOnlyList<ColumnAssignment> Assignments,
    Expression? Where) : UpsertAction;

internal sealed record UpsertClause(
    IReadOnlyList<UpsertTargetColumn> Target,
    UpsertAction Action,
    Expression? TargetWhere = null,
    string? TargetWhereSql = null,
    UpsertClause? Next = null)
{
    public IEnumerable<UpsertClause> Clauses()
    {
        for (UpsertClause? clause = this; clause is not null; clause = clause.Next)
            yield return clause;
    }
}

internal sealed record UpdateStatement(
    string TableName,
    IReadOnlyList<ColumnAssignment> Assignments,
    Expression? Where,
    IReadOnlyList<Projection>? Returning = null,
    IReadOnlyList<OrderByTerm>? OrderBy = null,
    Expression? Limit = null,
    Expression? Offset = null,
    string? Alias = null,
    TableSource? From = null,
    InsertConflictAlgorithm? ConflictAlgorithm = null,
    TableIndexDirective? IndexDirective = null) : ParsedStatement
{
    public IReadOnlyList<OrderByTerm> EffectiveOrderBy => OrderBy ?? [];

    /// <summary>
    /// The name target columns are qualified by inside SET, WHERE, and the FROM join.
    /// An alias replaces the table name entirely, matching SQLite.
    /// </summary>
    public string TargetQualifier => Alias ?? ManagedSchemaName.Display(TableName);
}

internal sealed record DeleteStatement(
    string TableName,
    Expression? Where,
    IReadOnlyList<Projection>? Returning = null,
    IReadOnlyList<OrderByTerm>? OrderBy = null,
    Expression? Limit = null,
    Expression? Offset = null,
    string? Alias = null,
    TableIndexDirective? IndexDirective = null) : ParsedStatement
{
    public IReadOnlyList<OrderByTerm> EffectiveOrderBy => OrderBy ?? [];

    /// <summary>The name target columns are qualified by inside WHERE.</summary>
    public string TargetQualifier => Alias ?? ManagedSchemaName.Display(TableName);
}

internal sealed record PragmaTableInfoStatement(string TableName) : ParsedStatement;

internal sealed record PragmaTableXInfoStatement(string TableName) : ParsedStatement;

internal sealed record PragmaIndexListStatement(string TableName) : ParsedStatement;

internal sealed record PragmaIndexInfoStatement(string IndexName) : ParsedStatement;

internal sealed record PragmaIndexXInfoStatement(string IndexName) : ParsedStatement;

internal sealed record PragmaForeignKeyListStatement(string? TableName) : ParsedStatement;

internal sealed record PragmaForeignKeyCheckStatement(
    string? TableName,
    string? Schema = null) : ParsedStatement;

/// <summary>
/// <c>PRAGMA integrity_check</c> and <c>PRAGMA quick_check</c>. The optional
/// argument is either a maximum error count or a single table to restrict the
/// check to, matching SQLite.
/// </summary>
internal sealed record PragmaIntegrityCheckStatement(
    bool Quick,
    int? MaxErrors,
    string? TableName,
    string? Schema = null) : ParsedStatement;

internal sealed record PragmaTableListStatement(string? Schema = null, string? Filter = null) : ParsedStatement;

internal sealed record PragmaDatabaseListStatement(string? Schema = null) : ParsedStatement;

internal sealed record PragmaEncodingStatement(string? Schema = null) : ParsedStatement;

internal sealed record PragmaQueryOnlyStatement(bool? Enabled, string? Schema = null) : ParsedStatement;

internal sealed record PragmaForeignKeysStatement(bool? Enabled, string? Schema = null) : ParsedStatement;

internal sealed record PragmaDeferForeignKeysStatement(bool? Enabled, string? Schema = null) : ParsedStatement;

internal sealed record PragmaRecursiveTriggersStatement(bool? Enabled, string? Schema = null) : ParsedStatement;

/// <summary>
/// Turso's connection-scoped public change-data-capture configuration. A null
/// value queries the active configuration; otherwise it is <c>mode[,table]</c>.
/// </summary>
internal sealed record PragmaCaptureDataChangesConnectionStatement(
    string? Value,
    string? Schema = null) : ParsedStatement;

internal enum PragmaHeaderIntegerKind
{
    SchemaVersion,
    UserVersion,
    ApplicationId,
}

internal sealed record PragmaHeaderIntegerStatement(
    PragmaHeaderIntegerKind Kind,
    int? Value,
    string? Schema = null) : ParsedStatement;

internal sealed record PragmaJournalModeStatement(string? Mode, string? Schema = null) : ParsedStatement;

internal sealed record PragmaPageSizeStatement(int? Value, string? Schema = null) : ParsedStatement;

internal sealed record PragmaCacheSizeStatement(long? Value, string? Schema = null) : ParsedStatement;

internal sealed record PragmaCacheSpillStatement(bool? Enabled, string? Schema = null) : ParsedStatement;

internal sealed record PragmaMaxPageCountStatement(long? Value, string? Schema = null) : ParsedStatement;

internal sealed record PragmaIgnoreCheckConstraintsStatement(bool? Enabled, string? Schema = null) : ParsedStatement;

internal sealed record PragmaRequireWhereStatement(bool? Enabled, string? Schema = null) : ParsedStatement;

internal sealed record PragmaTempStoreStatement(int? Value, string? Schema = null) : ParsedStatement;

internal sealed record PragmaWalCheckpointStatement(string? Mode, string? Schema = null) : ParsedStatement;

internal sealed record PragmaBusyTimeoutStatement(long? Value, string? Schema = null) : ParsedStatement;

internal sealed record PragmaSynchronousStatement(string? Value, string? Schema = null) : ParsedStatement;

internal sealed record PragmaLockingModeStatement(string? Value, string? Schema = null) : ParsedStatement;

internal sealed record PragmaAutoVacuumStatement(string? Value, string? Schema = null) : ParsedStatement;

internal sealed record PragmaDataSyncRetryStatement(bool? Enabled, string? Schema = null) : ParsedStatement;

internal sealed record PragmaFullColumnNamesStatement(bool? Enabled, string? Schema = null) : ParsedStatement;

internal sealed record PragmaShortColumnNamesStatement(bool? Enabled, string? Schema = null) : ParsedStatement;

internal sealed record PragmaMvccCheckpointThresholdStatement(long? Value, string? Schema = null) : ParsedStatement;

internal sealed record PragmaMvccGcThresholdStatement(long? Value, string? Schema = null) : ParsedStatement;

internal sealed record PragmaListTypesStatement(string? Schema = null) : ParsedStatement;

internal sealed record PragmaFunctionListStatement(string? Schema = null) : ParsedStatement;

internal sealed record PragmaModuleListStatement(string? Schema = null) : ParsedStatement;

/// <summary>
/// An unrecognized pragma: SQLite silently ignores unknown pragmas (Turso's
/// translate/pragma.rs falls through without emitting anything), so the engine executes
/// these as a no-op instead of rejecting them at prepare time.
/// </summary>
internal sealed record PragmaNoOpStatement(string Name, string? Schema = null) : ParsedStatement;

internal sealed record PragmaPageCountStatement(string? Schema = null) : ParsedStatement;

internal sealed record PragmaFreelistCountStatement(string? Schema = null) : ParsedStatement;

internal sealed record AnalyzeStatement(string? Target) : ParsedStatement;

internal enum ReindexTargetKind
{
    Automatic,
    Collation,
}

internal sealed record ReindexStatement(
    string? Target,
    ReindexTargetKind TargetKind = ReindexTargetKind.Automatic) : ParsedStatement;

internal sealed record VacuumStatement(string? Schema, Expression? Into) : ParsedStatement;

internal sealed record AttachDatabaseStatement(
    Expression Path,
    string Alias,
    Expression? Key) : ParsedStatement;

internal sealed record DetachDatabaseStatement(string Alias) : ParsedStatement;

internal sealed record ExplainStatement(ParsedStatement Inner) : ParsedStatement;

internal sealed record ExplainQueryPlanStatement(ParsedStatement Inner) : ParsedStatement;

internal sealed record SelectStatement(
    bool Distinct,
    IReadOnlyList<Projection> Projections,
    TableSource? Source,
    Expression? Where,
    IReadOnlyList<Expression> GroupBy,
    Expression? Having,
    IReadOnlyList<NamedWindowDefinition> NamedWindows,
    IReadOnlyList<OrderByTerm> OrderBy,
    Expression? Limit,
    Expression? Offset) : QueryStatement;

// A VALUES(...) row-set expression. It is a first-class query term so it can appear
// at the top level, inside FROM/JOIN as a derived table, as a scalar/IN/EXISTS
// subquery, as a compound-select term, and as the body of a common table expression.
// SQLite names its columns "column1".."columnN".
internal sealed record ValuesClause(
    IReadOnlyList<IReadOnlyList<Expression>> Rows) : QueryStatement;

internal sealed record CompoundSelectStatement(
    IReadOnlyList<QueryStatement> Terms,
    IReadOnlyList<CompoundOperator> Operators,
    IReadOnlyList<OrderByTerm> OrderBy,
    Expression? Limit,
    Expression? Offset) : QueryStatement;

internal sealed record WithSelectStatement(
    IReadOnlyList<CommonTableExpression> CommonTableExpressions,
    QueryStatement Query) : QueryStatement;

internal sealed record WithDmlStatement(
    IReadOnlyList<CommonTableExpression> CommonTableExpressions,
    ParsedStatement Dml) : ParsedStatement;

internal enum CteMaterializationHint
{
    Unspecified,
    Materialized,
    NotMaterialized,
}

internal sealed record CommonTableExpression(
    string Name,
    IReadOnlyList<string>? Columns,
    QueryStatement Query,
    CteMaterializationHint MaterializationHint);

/// <summary>The locking behavior requested by <c>BEGIN</c>.</summary>
internal enum TransactionMode
{
    /// <summary>Take the write lock lazily, at the first write.</summary>
    Deferred,

    /// <summary>Use Turso's MVCC-only concurrent transaction mode.</summary>
    Concurrent,

    /// <summary>Take the write lock at <c>BEGIN</c>.</summary>
    Immediate,

    /// <summary>Take the exclusive lock at <c>BEGIN</c>.</summary>
    Exclusive,
}

internal sealed record BeginStatement(
    TransactionMode Mode = TransactionMode.Deferred,
    string? Name = null) : ParsedStatement;

internal sealed record CommitStatement(string? Name = null) : ParsedStatement;

internal sealed record RollbackStatement(string? Name = null) : ParsedStatement;

internal sealed record SavepointStatement(string Name) : ParsedStatement;

internal sealed record ReleaseSavepointStatement(string Name) : ParsedStatement;

internal sealed record RollbackToSavepointStatement(string Name) : ParsedStatement;

internal abstract record TableSource;

internal sealed record NamedTableSource(
    string Name,
    string? Alias = null,
    TableIndexDirective? IndexDirective = null,
    bool IsSchemaQualified = false) : TableSource;

internal abstract record TableIndexDirective;

internal sealed record IndexedByDirective(string IndexName) : TableIndexDirective;

internal sealed record NotIndexedDirective : TableIndexDirective;

/// <summary>
/// A FROM-clause row source produced by calling a named module rather than by looking a
/// table up in the catalog: <c>FROM pragma_table_info('t')</c>, <c>FROM json_each(:doc)</c>,
/// <c>FROM generate_series(1, 10)</c>.
/// <para>
/// This is the only table-valued call site in the grammar. Resolution, column shape and row
/// production all live behind <c>TableValuedFunctionRegistry</c>, so a real virtual-table
/// module implementation attaches by registering a module rather than by extending the
/// parser or the planner.
/// </para>
/// </summary>
internal sealed record TableValuedFunctionSource(
    string Name,
    IReadOnlyList<Expression> Arguments,
    string? Alias = null,
    string? Schema = null) : TableSource;

internal sealed record DerivedTableSource(QueryStatement Query, string? Alias) : TableSource;

internal sealed record JoinTableSource(
    TableSource Left,
    TableSource Right,
    Expression? Condition,
    JoinKind Kind,
    IReadOnlyList<string>? UsingColumns = null,
    bool Natural = false) : TableSource;

internal enum JoinKind
{
    Inner,
    Left,
    Right,
    Full,

    /// <summary>
    /// Internal-only join produced by <c>EmbeddedDatabase.RewriteCorrelatedSubqueries</c>. It
    /// keeps one copy of a left row when at least one right row satisfies the condition, and
    /// projects only the left row shape. Turso spells this <c>JoinType::Semi</c>
    /// (turso-src/core/translate/optimizer/unnest.rs:259-265). No SQL surface produces it: the
    /// parser never emits it, so it can only appear after the correlated-subquery rewrite.
    /// </summary>
    Semi,

    /// <summary>
    /// Internal-only join produced by <c>EmbeddedDatabase.RewriteCorrelatedSubqueries</c>. It
    /// keeps a left row when <em>no</em> right row satisfies the condition, and projects only
    /// the left row shape. Turso spells this <c>JoinType::Anti</c>
    /// (turso-src/core/translate/optimizer/unnest.rs:259-265).
    /// </summary>
    Anti,
}

internal static class JoinKindExtensions
{
    /// <summary>
    /// True for the internal semi/anti joins, whose output row shape is exactly the left
    /// side's: the right side only decides whether a left row survives, so its columns are
    /// never visible to the enclosing query (they stay in scope for the join condition only).
    /// </summary>
    public static bool ProducesLeftShapeOnly(this JoinKind kind)
        => kind is JoinKind.Semi or JoinKind.Anti;
}

internal enum CompoundOperator
{
    Union,
    UnionAll,
    Intersect,
    Except,
}

internal sealed record Projection(
    Expression Expression,
    string? Alias,
    // SQLite names a result column that has no alias after the exact source text of the
    // expression that produced it (so `a + 1`, `count(*)`, `'text'`), including the
    // parentheses of a parenthesized expression. The parser captures the expression span;
    // when it is absent (rewritten projections), callers fall back to structural names.
    string? SourceText = null);

internal enum NullPlacement
{
    Default,
    First,
    Last,
}

internal sealed record OrderByTerm(
    Expression Expression,
    bool Descending,
    NullPlacement NullPlacement = NullPlacement.Default,
    long? Ordinal = null);

internal sealed record WindowSpecification(
    string? BaseWindowName,
    IReadOnlyList<Expression> PartitionBy,
    IReadOnlyList<OrderByTerm> OrderBy,
    WindowFrame? Frame,
    bool IsNamedReference = false);

internal sealed record NamedWindowDefinition(string Name, WindowSpecification Specification);

internal enum WindowFrameMode
{
    Rows,
    Range,
    Groups,
}

internal enum FrameBoundKind
{
    UnboundedPreceding,
    Preceding,
    CurrentRow,
    Following,
    UnboundedFollowing,
}

internal sealed record FrameBound(FrameBoundKind Kind, Expression? Offset);

internal enum FrameExclusion
{
    NoOthers,
    CurrentRow,
    Group,
    Ties,
}

internal sealed record WindowFrame(
    WindowFrameMode Mode,
    FrameBound Start,
    FrameBound End,
    FrameExclusion Exclusion = FrameExclusion.NoOthers);

internal sealed record ColumnAssignment(
    string Column,
    Expression Value,
    int ValueIndex = 0,
    int ValueCount = 1,
    bool IsRowAssignment = false);

internal sealed record EmbeddedColumn(
    string Name,
    string? DeclaredType,
    bool PrimaryKey,
    bool NotNull,
    bool Unique,
    SqlValue? DefaultValue,
    bool PrimaryKeyDescending = false,
    Expression? GenerationExpression = null,
    bool GeneratedStored = false,
    string? GenerationSql = null,
    string? Collation = null,
    ForeignKeyDefinition? ForeignKey = null,
    IReadOnlyList<CheckConstraint>? Checks = null,
    Expression? DefaultExpression = null,
    string? DefaultSql = null,
    InsertConflictAlgorithm? PrimaryKeyConflictAlgorithm = null,
    InsertConflictAlgorithm? NotNullConflictAlgorithm = null,
    InsertConflictAlgorithm? UniqueConflictAlgorithm = null,
    string? PrimaryKeyConstraintName = null,
    string? NotNullConstraintName = null,
    string? UniqueConstraintName = null,
    string? DefaultConstraintName = null,
    string? CollationConstraintName = null,
    string? GenerationConstraintName = null,
    string? NullConstraintName = null,
    bool ExplicitNull = false,
    bool GenerationAlways = false,
    bool AutoIncrement = false,
    IReadOnlyList<ForeignKeyDefinition>? AdditionalForeignKeys = null,
    int? PrimaryKeyDeclarationOrder = null,
    int? UniqueDeclarationOrder = null,
    bool StrictAny = false,
    bool GenerationVirtualSpelled = false)
{
    // A column is generated when it carries a computed AS (...) expression. Generated
    // columns are materialized at write time; VIRTUAL and STORED differ only in whether
    // the value may be persisted (STORED) or must be recomputed (VIRTUAL).
    public bool IsGenerated => GenerationExpression is not null;

    public IReadOnlyList<CheckConstraint> CheckConstraints { get; } =
        Array.AsReadOnly((Checks ?? []).ToArray());

    public IReadOnlyList<ForeignKeyDefinition> ForeignKeyConstraints { get; } =
        Array.AsReadOnly(
            (ForeignKey is null
                ? AdditionalForeignKeys ?? []
                : new[] { ForeignKey }.Concat(AdditionalForeignKeys ?? []))
            .ToArray());

    public bool HasDefault => DefaultValue.HasValue || DefaultExpression is not null;

    /// <summary>
    /// Rebuilds the column with different constraint clauses. <see cref="CheckConstraints"/> and
    /// <see cref="ForeignKeyConstraints"/> are property initializers, which a record <c>with</c>
    /// copy does not re-run, so editing <see cref="Checks"/>, <see cref="ForeignKey"/>, or
    /// <see cref="AdditionalForeignKeys"/> through <c>with</c> would leave those projections
    /// stale. Constraint edits go through this explicit rebuild instead.
    /// </summary>
    public EmbeddedColumn WithConstraints(
        IReadOnlyList<CheckConstraint>? checks,
        ForeignKeyDefinition? foreignKey,
        IReadOnlyList<ForeignKeyDefinition>? additionalForeignKeys)
        => new(
            Name,
            DeclaredType,
            PrimaryKey,
            NotNull,
            Unique,
            DefaultValue,
            PrimaryKeyDescending,
            GenerationExpression,
            GeneratedStored,
            GenerationSql,
            Collation,
            foreignKey,
            checks,
            DefaultExpression,
            DefaultSql,
            PrimaryKeyConflictAlgorithm,
            NotNullConflictAlgorithm,
            UniqueConflictAlgorithm,
            PrimaryKeyConstraintName,
            NotNullConstraintName,
            UniqueConstraintName,
            DefaultConstraintName,
            CollationConstraintName,
            GenerationConstraintName,
            NullConstraintName,
            ExplicitNull,
            GenerationAlways,
            AutoIncrement,
            additionalForeignKeys,
            PrimaryKeyDeclarationOrder,
            UniqueDeclarationOrder,
            StrictAny,
            GenerationVirtualSpelled);
}

// A column participating in a table-level PRIMARY KEY(...) clause, preserving the
// declared collation and ASC/DESC direction so its physical-key descriptor does not
// lose SQLite's comparison semantics.
internal sealed record TablePrimaryKeyColumn(
    string Name,
    bool Descending,
    string? Collation = null,
    bool AutoIncrement = false);

internal sealed record TableUniqueConstraint(
    string? Name,
    IReadOnlyList<TablePrimaryKeyColumn> Columns,
    InsertConflictAlgorithm? ConflictAlgorithm = null,
    int DeclarationOrder = int.MaxValue);

internal sealed record CheckConstraint(
    string? Name,
    Expression Expression,
    string Sql,
    InsertConflictAlgorithm? ConflictAlgorithm = null);

internal enum ForeignKeyAction
{
    NoAction,
    Restrict,
    SetNull,
    SetDefault,
    Cascade,
}

internal enum ForeignKeyDeferral
{
    NotDeferrable,
    InitiallyImmediate,
    InitiallyDeferred,
}

internal sealed record ForeignKeyDefinition(
    IReadOnlyList<string> ChildColumns,
    string ParentTable,
    IReadOnlyList<string> ParentColumns,
    ForeignKeyAction OnDelete = ForeignKeyAction.NoAction,
    ForeignKeyAction OnUpdate = ForeignKeyAction.NoAction,
    string? Match = null,
    ForeignKeyDeferral Deferral = ForeignKeyDeferral.NotDeferrable,
    string? ConstraintName = null);

internal sealed record EmbeddedIndexColumn(
    string Name,
    int ColumnIndex,
    string? Collation,
    bool Descending,
    Expression? Expression = null,
    string? ExpressionSql = null)
{
    public bool IsExpression => Expression is not null;
}

internal enum EmbeddedIndexOrigin
{
    Explicit,
    UniqueConstraint,
    PrimaryKey,
}

internal sealed record EmbeddedIndex(
    string Name,
    bool Unique,
    IReadOnlyList<EmbeddedIndexColumn> Columns,
    EmbeddedIndexOrigin Origin = EmbeddedIndexOrigin.Explicit,
    InsertConflictAlgorithm? ConflictAlgorithm = null,
    Expression? Where = null,
    string? WhereSql = null,
    int? ConstraintOrdinal = null,
    string? Sql = null,
    string? Method = null,
    IReadOnlyList<Indexing.ManagedIndexMethodParameter>? MethodParameters = null)
{
    public bool IsPartial => Where is not null;

    /// <summary>True when this index is served by a registered managed index method.</summary>
    public bool IsMethodIndex => Method is not null;
}

internal abstract record Expression;

internal sealed record LiteralExpression(SqlValue Value) : Expression;

internal enum CurrentTimeKind
{
    Date,
    Time,
    Timestamp,
}

internal sealed record CurrentTimeExpression(CurrentTimeKind Kind) : Expression;

internal sealed record ParameterExpression(int Index) : Expression;

internal enum RaiseAction
{
    Ignore,
    Rollback,
    Abort,
    Fail,
}

/// <summary>
/// A <c>RAISE(...)</c> call inside a trigger body. <see cref="Message"/> is an arbitrary
/// expression (SQLite allows any expression, e.g. <c>RAISE(ABORT, 'bad: ' || NEW.a)</c>),
/// or <c>null</c> for <c>RAISE(IGNORE)</c> and for the <c>RAISE('msg')</c> shorthand whose
/// message is a plain string literal still represented as a <see cref="LiteralExpression"/>.
/// </summary>
internal sealed record RaiseExpression(RaiseAction Action, Expression? Message) : Expression;

internal sealed record RowValueExpression(IReadOnlyList<Expression> Values) : Expression;

/// <summary>
/// A column reference. <see cref="BooleanKeyword"/> is set when the reference came from a bare,
/// unquoted <c>TRUE</c>/<c>FALSE</c> keyword: SQLite parses those as ordinary identifiers and only
/// rewrites them into the integer literals 1/0 when no column of that name is in scope.
/// </summary>
internal sealed record ColumnExpression(
    string Name,
    string? Qualifier = null,
    string? UnqualifiedName = null,
    bool? BooleanKeyword = null,
    string? Schema = null) : Expression;

internal sealed record FunctionExpression(
    string Name,
    IReadOnlyList<Expression> Arguments,
    bool CountStar,
    bool Distinct = false,
    Expression? Filter = null,
    WindowSpecification? Window = null,
    IReadOnlyList<OrderByTerm>? AggregateOrderBy = null,
    bool OrderedSet = false) : Expression;

internal sealed record ScalarSubqueryExpression(QueryStatement Query) : Expression;

internal sealed record ExistsExpression(QueryStatement Query, bool Negated) : Expression;

internal sealed record CollationExpression(Expression Expression, string Name) : Expression;

internal sealed record CastExpression(Expression Expression, string TypeName) : Expression;

internal sealed record CaseExpression(Expression? Operand, IReadOnlyList<CaseClause> Clauses, Expression? Else) : Expression;

internal sealed record CaseClause(Expression When, Expression Then);

internal sealed record LikeExpression(Expression Value, Expression Pattern, Expression? Escape, bool Negated) : Expression;

internal sealed record InExpression(Expression Value, IReadOnlyList<Expression> Values, bool Negated) : Expression;

internal sealed record InSubqueryExpression(Expression Value, QueryStatement Query, bool Negated) : Expression;

internal sealed record BetweenExpression(Expression Value, Expression Lower, Expression Upper, bool Negated) : Expression;

internal sealed record UnaryExpression(UnaryOperator Operator, Expression Operand) : Expression;

internal sealed record StarExpression : Expression;

internal sealed record QualifiedStarExpression(string Qualifier) : Expression;

internal sealed record GlobExpression(Expression Value, Expression Pattern, bool Negated) : Expression;

internal sealed record BinaryExpression(Expression Left, BinaryOperator Operator, Expression Right) : Expression;

internal enum BinaryOperator
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,
    BitwiseAnd,
    BitwiseOr,
    ShiftLeft,
    ShiftRight,
    Concatenate,
    JsonArrow,
    JsonArrowText,
    And,
    Or,
    Is,
    IsNot,
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
}

internal enum UnaryOperator
{
    Not,
    Plus,
    Negate,
    BitwiseNot,
}
