using Ahtola.Core.Parsing;

namespace Ahtola.Core;

/// <summary>
/// Declared shape of a table-valued function. Visible columns are what <c>SELECT *</c>
/// expands to; hidden columns carry the call arguments and are addressable by name but
/// never expanded, exactly as SQLite's virtual-table <c>HIDDEN</c> columns behave.
/// </summary>
internal sealed record TableValuedFunctionSchema(
    IReadOnlyList<string> VisibleColumns,
    IReadOnlyList<string> HiddenColumns,
    IReadOnlyList<ColumnAffinity> Affinities)
{
    public IReadOnlyList<string> AllColumns { get; } =
        [.. VisibleColumns, .. HiddenColumns];

    public ColumnAffinity AffinityAt(int index)
        => index < Affinities.Count ? Affinities[index] : ColumnAffinity.Blob;
}

/// <summary>
/// One invocation of a table-valued function. <see cref="Arguments"/> is already
/// evaluated and padded to the module's hidden-column count with <see cref="SqlValue.Null"/>
/// for arguments the caller omitted.
/// </summary>
internal sealed record TableValuedFunctionCall(
    IReadOnlyList<SqlValue> Arguments,
    IReadOnlyList<bool> ArgumentSupplied,
    string? Schema,
    long? MaximumRows,
    EmbeddedDatabase.QueryContext Context)
{
    public bool HasArgument(int index)
        => index < ArgumentSupplied.Count && ArgumentSupplied[index];

    /// <summary>
    /// Aborts a row loop once the caller has cancelled or interrupted the statement.
    /// </summary>
    /// <remarks>
    /// Modules generate rows in unbounded loops — <c>generate_series</c> with an omitted
    /// stop runs to 0xffffffff — so a module that never polls cannot be stopped at all.
    /// Every row loop must call this. It is deliberately the single place the interrupt
    /// mechanism is named, so adopting a different one stays a one-line change here
    /// rather than an edit to every module.
    /// </remarks>
    public void CheckInterrupt()
        => Context.CheckInterrupt();
}

/// <summary>
/// A FROM-clause row source addressed by name rather than by catalog lookup.
/// <para>
/// This is the single seam that a real virtual-table module implementation attaches to:
/// the parser turns <c>name(args)</c> into a <see cref="TableValuedFunctionSource"/>,
/// <see cref="TableValuedFunctionRegistry"/> resolves the name, <see cref="Schema"/>
/// answers every planner question about the source's columns, and
/// <see cref="TableValuedFunctionVirtualTable"/> adapts enumeration to the standard managed
/// virtual-table planner and cursor contract. Nothing outside a module implementation knows
/// any function name.
/// </para>
/// </summary>
internal abstract class TableValuedFunctionModule
{
    public abstract string Name { get; }

    public abstract TableValuedFunctionSchema Schema { get; }

    /// <summary>Positional arguments the caller may pass, in hidden-column order.</summary>
    public virtual int MaximumArgumentCount => Schema.HiddenColumns.Count;

    public virtual int MinimumArgumentCount => 0;

    /// <summary>
    /// Index of the argument that names a schema object, when the module has one. The
    /// connection-level router uses it to send the call to the schema that owns the object,
    /// so a temp shadow wins over a main table of the same name.
    /// </summary>
    public virtual int? SchemaObjectArgumentIndex => null;

    /// <summary>
    /// Index of the argument that names a database schema, when the module has one, so
    /// <c>pragma_table_info('t', 'aux')</c> is routed to the attached database.
    /// </summary>
    public virtual int? SchemaNameArgumentIndex => null;

    public abstract IReadOnlyList<SqlValue[]> Enumerate(TableValuedFunctionCall call);
}

/// <summary>
/// Adapts built-in table-valued functions to the same planner/cursor contract as catalog virtual
/// tables. Parenthesized call arguments are represented as equality constraints on hidden columns.
/// </summary>
internal sealed class TableValuedFunctionVirtualTable(
    TableValuedFunctionModule module,
    string? schemaName,
    EmbeddedDatabase.QueryContext context) : ManagedVirtualTable
{
    private const string PlanPrefix = "tvf:";
    private readonly TableValuedFunctionModule _module =
        module ?? throw new ArgumentNullException(nameof(module));
    private readonly string? _schemaName = schemaName;
    private readonly EmbeddedDatabase.QueryContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    public override ManagedVirtualTableSchema Schema { get; } = new(
        module.Schema.AllColumns.Select((name, index) => new ManagedVirtualTableColumn(
            name,
            module.Schema.AffinityAt(index) switch
            {
                ColumnAffinity.Text => ManagedVirtualTableAffinity.Text,
                ColumnAffinity.Numeric => ManagedVirtualTableAffinity.Numeric,
                ColumnAffinity.Integer => ManagedVirtualTableAffinity.Integer,
                ColumnAffinity.Real => ManagedVirtualTableAffinity.Real,
                _ => ManagedVirtualTableAffinity.Blob,
            },
            IsHidden: index >= module.Schema.VisibleColumns.Count)));

    public override ManagedVirtualTablePlan BestIndex(
        IReadOnlyList<ManagedVirtualTableConstraint> constraints,
        IReadOnlyList<ManagedVirtualTableOrderBy> orderBy)
    {
        var usages = new ManagedVirtualTableConstraintUsage[constraints.Count];
        var argumentMappings = new List<string>();
        var argumentIndex = 0;
        for (var index = 0; index < constraints.Count; index++)
        {
            var constraint = constraints[index];
            if (!constraint.Usable)
                continue;

            var hiddenIndex = constraint.ColumnIndex - _module.Schema.VisibleColumns.Count;
            if (constraint.Operator == ManagedVirtualTableConstraintOperator.Equal
                && hiddenIndex >= 0
                && hiddenIndex < _module.Schema.HiddenColumns.Count)
            {
                usages[index] = new ManagedVirtualTableConstraintUsage(++argumentIndex);
                argumentMappings.Add($"h{hiddenIndex}");
            }
            else if (constraint.Operator == ManagedVirtualTableConstraintOperator.Limit)
            {
                usages[index] = new ManagedVirtualTableConstraintUsage(++argumentIndex);
                argumentMappings.Add("l");
            }
            else if (constraint.Operator == ManagedVirtualTableConstraintOperator.Offset)
            {
                usages[index] = new ManagedVirtualTableConstraintUsage(++argumentIndex);
                argumentMappings.Add("o");
            }
        }

        var estimatedRows = _module.Name.Equals("generate_series", StringComparison.OrdinalIgnoreCase)
            ? 1000L
            : 100L;
        return new ManagedVirtualTablePlan(
            usages,
            indexNumber: argumentMappings.Count == 0 ? 0 : 1,
            indexString: PlanPrefix + string.Join(',', argumentMappings),
            estimatedCost: estimatedRows,
            estimatedRows: estimatedRows);
    }

    public override ManagedVirtualTableCursor Open()
        => new Cursor(_module, _schemaName, _context);

    private sealed class Cursor(
        TableValuedFunctionModule module,
        string? schemaName,
        EmbeddedDatabase.QueryContext context) : ManagedVirtualTableCursor
    {
        private IReadOnlyList<SqlValue[]>? _rows;
        private IEnumerator<SqlValue[]>? _enumerator;
        private SqlValue[]? _current;
        private long _rowId;

        public override bool Filter(ManagedVirtualTablePlan plan, IReadOnlyList<SqlValue> arguments)
        {
            DisposeEnumeration();
            var values = new SqlValue[module.Schema.HiddenColumns.Count];
            Array.Fill(values, SqlValue.Null);
            var supplied = new bool[values.Length];
            long? limit = null;
            var offset = 0L;
            var mappings = (plan.IndexString ?? string.Empty).StartsWith(PlanPrefix, StringComparison.Ordinal)
                ? plan.IndexString![PlanPrefix.Length..].Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];
            if (mappings.Length != arguments.Count)
                throw new InvalidOperationException("A table-valued function plan has an invalid argument mapping.");

            for (var index = 0; index < mappings.Length; index++)
            {
                var mapping = mappings[index];
                if (mapping == "l")
                {
                    limit = arguments[index].Kind == SqlValueKind.Integer
                        && arguments[index].AsInteger() >= 0
                            ? arguments[index].AsInteger()
                            : null;
                    continue;
                }
                if (mapping == "o")
                {
                    offset = arguments[index].Kind == SqlValueKind.Integer
                        ? Math.Max(0, arguments[index].AsInteger())
                        : 0;
                    continue;
                }
                if (mapping.Length < 2
                    || mapping[0] != 'h'
                    || !int.TryParse(
                        mapping.AsSpan(1),
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var hiddenIndex)
                    || (uint)hiddenIndex >= (uint)values.Length)
                {
                    throw new InvalidOperationException("A table-valued function plan has an invalid hidden-column mapping.");
                }

                values[hiddenIndex] = arguments[index];
                supplied[hiddenIndex] = true;
            }

            _rows = module.Enumerate(new TableValuedFunctionCall(
                values,
                supplied,
                schemaName,
                limit is { } bounded
                    ? bounded > long.MaxValue - offset ? long.MaxValue : bounded + offset
                    : null,
                context));
            _enumerator = _rows.GetEnumerator();
            _rowId = 0;
            MoveNext();
            return !Eof;
        }

        public override void Next() => MoveNext();

        public override bool Eof => _current is null;

        public override SqlValue Column(int index)
        {
            var row = _current ?? throw new InvalidOperationException("Table-valued function cursor is not positioned.");
            if ((uint)index >= (uint)row.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            return row[index];
        }

        public override long RowId => Eof
            ? throw new InvalidOperationException("Table-valued function cursor is not positioned.")
            : _rowId;

        public override void Dispose() => DisposeEnumeration();

        private void MoveNext()
        {
            if (_enumerator is null)
            {
                _current = null;
                return;
            }

            if (_enumerator.MoveNext())
            {
                _current = _enumerator.Current;
                _rowId++;
                return;
            }

            DisposeEnumeration();
        }

        private void DisposeEnumeration()
        {
            try
            {
                _enumerator?.Dispose();
            }
            finally
            {
                _enumerator = null;
                _current = null;
                if (_rows is IDisposable disposable)
                    disposable.Dispose();
                _rows = null;
            }
        }
    }
}

/// <summary>
/// Name-to-module resolution for FROM-clause table-valued functions. Registration is the
/// only place a built-in name appears; adding a virtual-table module means adding an entry
/// here rather than teaching the parser or the planner about another name.
/// </summary>
internal static class TableValuedFunctionRegistry
{
    private static readonly Dictionary<string, TableValuedFunctionModule> Modules =
        Create();

    public static bool TryResolve(string name, out TableValuedFunctionModule module)
        => Modules.TryGetValue(name, out module!);

    public static bool IsRegistered(string name) => Modules.ContainsKey(name);

    public static IReadOnlyCollection<string> AllNames => Modules.Keys;

    public static TableValuedFunctionModule Resolve(string name)
        => TryResolve(name, out var module)
            ? module
            : throw new EmbeddedSqlException(UnsupportedMessage(name));

    public static string UnsupportedMessage(string name)
        => $"Managed table-valued source '{name}' is not supported: "
            + "no module registration, planner, or execution contract is available.";

    private static Dictionary<string, TableValuedFunctionModule> Create()
    {
        var modules = new Dictionary<string, TableValuedFunctionModule>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in new TableValuedFunctionModule[]
        {
            new GenerateSeriesModule(),
            new JsonTraversalModule(recursive: false),
            new JsonTraversalModule(recursive: true),
            new PragmaIntrospectionModule(
                "pragma_table_info",
                ["cid", "name", "type", "notnull", "dflt_value", "pk"],
                ["arg", "schema"],
                static argument => new PragmaTableInfoStatement(argument)),
            new PragmaIntrospectionModule(
                "pragma_table_xinfo",
                ["cid", "name", "type", "notnull", "dflt_value", "pk", "hidden"],
                ["arg", "schema"],
                static argument => new PragmaTableXInfoStatement(argument)),
            new PragmaIntrospectionModule(
                "pragma_index_list",
                ["seq", "name", "unique", "origin", "partial"],
                ["arg", "schema"],
                static argument => new PragmaIndexListStatement(argument)),
            new PragmaIntrospectionModule(
                "pragma_index_info",
                ["seqno", "cid", "name"],
                ["arg", "schema"],
                static argument => new PragmaIndexInfoStatement(argument)),
            new PragmaIntrospectionModule(
                "pragma_index_xinfo",
                ["seqno", "cid", "name", "desc", "coll", "key"],
                ["arg", "schema"],
                static argument => new PragmaIndexXInfoStatement(argument)),
            new PragmaIntrospectionModule(
                "pragma_foreign_key_list",
                ["id", "seq", "table", "from", "to", "on_update", "on_delete", "match"],
                ["arg", "schema"],
                static argument => new PragmaForeignKeyListStatement(argument)),
            new PragmaTableListModule(),
            new PragmaCacheSizeModule(),
            new PragmaFunctionListModule(),
            new PragmaModuleListModule(),
        })
        {
            modules.Add(module.Name, module);
        }

        return modules;
    }
}
