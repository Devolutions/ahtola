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
/// <see cref="Enumerate"/> is the whole execution contract. Nothing outside a module
/// implementation knows any function name.
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
        })
        {
            modules.Add(module.Name, module);
        }

        return modules;
    }
}
