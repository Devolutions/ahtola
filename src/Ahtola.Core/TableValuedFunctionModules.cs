using System.Globalization;
using Ahtola.Core.Parsing;

namespace Ahtola.Core;

/// <summary>
/// <c>generate_series(start, stop, step)</c>. The three arguments are the module's hidden
/// columns, so <c>generate_series(1,5)</c> and <c>generate_series WHERE start=1</c> bind the
/// same slots.
/// </summary>
internal sealed class GenerateSeriesModule : TableValuedFunctionModule
{
    public override string Name => "generate_series";

    public override TableValuedFunctionSchema Schema { get; } = new(
        ["value"],
        ["start", "stop", "step"],
        [ColumnAffinity.Integer, ColumnAffinity.Integer, ColumnAffinity.Integer, ColumnAffinity.Integer]);

    public override IReadOnlyList<SqlValue[]> Enumerate(TableValuedFunctionCall call)
    {
        // SQLite's series module produces no rows when the mandatory first argument is
        // absent or NULL, and reports an error for a non-integer bound.
        if (!call.HasArgument(0) || call.Arguments[0].Kind == SqlValueKind.Null)
            return [];

        var start = RequireInteger(call.Arguments[0]);
        var stop = call.HasArgument(1) ? RequireInteger(call.Arguments[1]) : 0xffffffffL;
        var step = call.HasArgument(2) ? RequireInteger(call.Arguments[2]) : 1;

        // A zero step counts by one rather than erroring or looping forever, so
        // generate_series(1,3,0) yields three rows and generate_series(3,1,0) yields none.
        if (step == 0)
            step = 1;

        // The hidden columns report the effective bounds, so an omitted stop or step reads
        // back as the default SQLite applied rather than as NULL.
        return new LazyReadOnlyList<SqlValue[]>(EnumerateRows());

        IEnumerable<SqlValue[]> EnumerateRows()
        {
            SqlValue[] bounds = [SqlValue.Integer(start), SqlValue.Integer(stop), SqlValue.Integer(step)];
            var emitted = 0L;
            if (step > 0)
            {
                for (var current = start;
                     current <= stop && (call.MaximumRows is null || emitted < call.MaximumRows.Value);)
                {
                    call.CheckInterrupt();
                    yield return BuildRow(current, bounds);
                    emitted++;
                    if (current > long.MaxValue - step)
                        yield break;

                    current += step;
                }
            }
            else
            {
                for (var current = start;
                     current >= stop && (call.MaximumRows is null || emitted < call.MaximumRows.Value);)
                {
                    call.CheckInterrupt();
                    yield return BuildRow(current, bounds);
                    emitted++;
                    if (current < long.MinValue - step)
                        yield break;

                    current += step;
                }
            }
        }
    }

    private static SqlValue[] BuildRow(long value, SqlValue[] bounds)
        => [SqlValue.Integer(value), bounds[0], bounds[1], bounds[2]];

    private static long RequireInteger(SqlValue value)
        => value.Kind switch
        {
            SqlValueKind.Integer => value.AsInteger(),
            SqlValueKind.Real => (long)value.AsReal(),
            SqlValueKind.Text when long.TryParse(
                value.AsText(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => throw new EmbeddedSqlException("generate_series() requires integer arguments"),
        };
}

/// <summary>
/// <c>json_each</c> (one level) and <c>json_tree</c> (recursive, pre-order). Both expose
/// SQLite's column set and traversal order; the hidden <c>json</c> and <c>root</c> columns
/// are the two call arguments.
/// </summary>
internal sealed class JsonTraversalModule(bool recursive) : TableValuedFunctionModule
{
    private readonly bool _recursive = recursive;

    public override string Name => _recursive ? "json_tree" : "json_each";

    public override TableValuedFunctionSchema Schema { get; } = new(
        ["key", "value", "type", "atom", "id", "parent", "fullkey", "path"],
        ["json", "root"],
        [
            ColumnAffinity.Blob,
            ColumnAffinity.Blob,
            ColumnAffinity.Text,
            ColumnAffinity.Blob,
            ColumnAffinity.Integer,
            ColumnAffinity.Integer,
            ColumnAffinity.Text,
            ColumnAffinity.Text,
            ColumnAffinity.Blob,
            ColumnAffinity.Text,
        ]);

    public override IReadOnlyList<SqlValue[]> Enumerate(TableValuedFunctionCall call)
    {
        if (!call.HasArgument(0))
            return [];

        var json = call.Arguments[0];
        if (json.Kind == SqlValueKind.Null)
            return [];

        var rootArgument = call.HasArgument(1) ? call.Arguments[1] : SqlValue.Null;
        if (call.HasArgument(1) && rootArgument.Kind == SqlValueKind.Null)
            return [];

        var root = call.HasArgument(1) ? EmbeddedDatabase.RequireJsonRootPath(rootArgument) : "$";
        var rows = EmbeddedDatabase.TraverseJson(json, root, _recursive);
        var result = new List<SqlValue[]>(rows.Count);
        var rootValue = SqlValue.Text(root);
        foreach (var row in rows)
        {
            call.CheckInterrupt();
            result.Add([.. row, json, rootValue]);
        }

        return result;
    }
}

/// <summary>
/// The <c>pragma_*</c> introspection family. Each module forwards to the PRAGMA statement
/// that produces the same result columns, so the function form and the statement form can
/// never disagree.
/// </summary>
internal sealed class PragmaIntrospectionModule(
    string name,
    IReadOnlyList<string> visibleColumns,
    IReadOnlyList<string> hiddenColumns,
    Func<string, ParsedStatement> build) : TableValuedFunctionModule
{
    private readonly Func<string, ParsedStatement> _build = build;

    public override string Name { get; } = name;

    public override TableValuedFunctionSchema Schema { get; } = new(
        visibleColumns,
        hiddenColumns,
        [.. Enumerable.Repeat(ColumnAffinity.Blob, visibleColumns.Count + hiddenColumns.Count)]);

    public override int? SchemaObjectArgumentIndex => 0;

    public override int? SchemaNameArgumentIndex => 1;

    public override IReadOnlyList<SqlValue[]> Enumerate(TableValuedFunctionCall call)
    {
        // SQLite's pragma virtual tables report no rows until the object-name argument is
        // supplied, either positionally or through an `arg = ?` constraint.
        if (!call.HasArgument(0) || call.Arguments[0].Kind == SqlValueKind.Null)
            return [];

        var argument = TableValuedFunctionRows.CoerceToText(call.Arguments[0]);
        if (call.Schema is { } schema
            && !schema.Equals("temp", StringComparison.OrdinalIgnoreCase)
            && (argument.Equals("sqlite_temp_master", StringComparison.OrdinalIgnoreCase)
                || argument.Equals("sqlite_temp_schema", StringComparison.OrdinalIgnoreCase)))
        {
            return [];
        }

        var result = EmbeddedDatabase.ExecuteIntrospectionPragma(_build(argument), call.Context);
        return TableValuedFunctionRows.AppendArguments(result.Rows, call.Arguments, Schema);
    }
}

/// <summary>
/// <c>pragma_table_list</c>. Unlike the rest of the family the argument only filters the
/// listing. The owning connection supplies the catalog set so an unqualified call can
/// enumerate both the main and temporary schemas.
/// </summary>
internal sealed class PragmaTableListModule : TableValuedFunctionModule
{
    public override string Name => "pragma_table_list";

    public override TableValuedFunctionSchema Schema { get; } = new(
        ["schema", "name", "type", "ncol", "wr", "strict"],
        ["arg"],
        [.. Enumerable.Repeat(ColumnAffinity.Blob, 7)]);

    public override IReadOnlyList<SqlValue[]> Enumerate(TableValuedFunctionCall call)
    {
        var filter = call.HasArgument(0) && call.Arguments[0].Kind != SqlValueKind.Null
            ? TableValuedFunctionRows.CoerceToText(call.Arguments[0])
            : null;
        var result = call.Context.ExecuteTableList is { } executeTableList
            ? executeTableList(call.Schema, filter)
            : EmbeddedDatabase.ExecuteIntrospectionPragma(
                new PragmaTableListStatement(call.Schema ?? "main", filter),
                call.Context);

        return TableValuedFunctionRows.AppendArguments(result.Rows, call.Arguments, Schema);
    }
}

/// <summary>
/// <c>pragma_cache_size</c>. The managed engine has no configurable page cache, so it
/// reports SQLite's default of -2000 KiB.
/// </summary>
internal sealed class PragmaCacheSizeModule : TableValuedFunctionModule
{
    private const long DefaultCacheSize = -2000;

    public override string Name => "pragma_cache_size";

    public override TableValuedFunctionSchema Schema { get; } = new(
        ["cache_size"],
        ["schema"],
        [ColumnAffinity.Integer, ColumnAffinity.Blob]);

    public override int? SchemaNameArgumentIndex => 0;

    public override IReadOnlyList<SqlValue[]> Enumerate(TableValuedFunctionCall call)
        => [[SqlValue.Integer(DefaultCacheSize), call.Arguments[0]]];
}

/// <summary>
/// <c>pragma_journal_mode</c>. Reports the current journal mode for the given schema (main
/// when the argument is omitted, matching PRAGMA journal_mode's own unqualified default) -
/// always the live value, reflecting any PRAGMA journal_mode change already applied, never a
/// cached or hardcoded one. The schema name comes from the hidden argument itself
/// (SchemaNameArgumentIndex), evaluated exactly like any other call argument, so a bound
/// parameter (`pragma_journal_mode(?)`) resolves the same as a literal - call.Schema is a
/// different concept entirely (an explicit `schema.pragma_journal_mode(...)` qualifier on
/// the function name, used only for BestIndex routing of literal schema arguments via
/// CollectTableValuedFunctionSchemas) and is left alone. context.Database alone is only the
/// single database this statement's own routing picked, which is wrong whenever the argument
/// names a different schema (a bound parameter can never be seen by that static routing, and
/// even a literal argument only routes correctly when it is the sole schema the statement
/// touches), so this goes through context.DescribeJournalMode - set once per EmbeddedDatabase
/// instance by its owning EmbeddedConnection (EmbeddedDatabase.SetDescribeJournalMode) - which
/// re-resolves the named schema itself, including temp's special "always wal" regardless of
/// the routed instance's own pager state.
/// </summary>
internal sealed class PragmaJournalModeModule : TableValuedFunctionModule
{
    public override string Name => "pragma_journal_mode";

    public override TableValuedFunctionSchema Schema { get; } = new(
        ["journal_mode"],
        ["schema"],
        [ColumnAffinity.Text, ColumnAffinity.Blob]);

    public override int? SchemaNameArgumentIndex => 0;

    public override IReadOnlyList<SqlValue[]> Enumerate(TableValuedFunctionCall call)
    {
        var schema = call.HasArgument(0) && call.Arguments[0].Kind != SqlValueKind.Null
            ? TableValuedFunctionRows.CoerceToText(call.Arguments[0])
            : null;
        var mode = call.Context.DescribeJournalMode?.Invoke(schema)
            ?? DescribeWithoutConnection(call.Context.Database);
        return [[SqlValue.Text(mode), call.Arguments[0]]];
    }

    // Degraded fallback for a context with no connection-aware resolver wired - every real
    // execution path wires one onto each EmbeddedDatabase instance a connection owns
    // (EmbeddedConnection's constructor, ResetTemporaryDatabase, ExecuteAttach), so this only
    // matters for a context built outside that lifecycle. Describes context.Database's own
    // mode directly, correct unless the schema argument actually names a different database
    // than the one this statement was routed to.
    private static string DescribeWithoutConnection(EmbeddedDatabase? database)
    {
        if (database is null)
            return "memory";
        if (database.IsMvccEnabled)
            return "mvcc";
        if (!database.IsFileBacked)
            return "memory";
        return database.GetJournalMode().ToString().ToLowerInvariant();
    }
}

internal sealed class PragmaFunctionListModule : TableValuedFunctionModule
{
    public override string Name => "pragma_function_list";

    public override TableValuedFunctionSchema Schema { get; } = new(
        ["name", "builtin", "type", "enc", "narg", "flags"],
        [],
        [
            ColumnAffinity.Text,
            ColumnAffinity.Integer,
            ColumnAffinity.Text,
            ColumnAffinity.Text,
            ColumnAffinity.Integer,
            ColumnAffinity.Integer,
        ]);

    public override IReadOnlyList<SqlValue[]> Enumerate(TableValuedFunctionCall call)
        => EmbeddedDatabase.BuildPragmaFunctionListRows(call.Context.Database);
}

internal sealed class PragmaModuleListModule : TableValuedFunctionModule
{
    public override string Name => "pragma_module_list";

    public override TableValuedFunctionSchema Schema { get; } = new(
        ["name"],
        [],
        [ColumnAffinity.Text]);

    public override IReadOnlyList<SqlValue[]> Enumerate(TableValuedFunctionCall call)
        => EmbeddedDatabase.BuildPragmaModuleListRows();
}

internal static class TableValuedFunctionRows
{
    public static string CoerceToText(SqlValue value)
        => value.Kind switch
        {
            SqlValueKind.Text => value.AsText(),
            SqlValueKind.Blob => System.Text.Encoding.UTF8.GetString(value.AsBlob().Span),
            SqlValueKind.Integer => value.AsInteger().ToString(CultureInfo.InvariantCulture),
            SqlValueKind.Real => value.AsReal().ToString("R", CultureInfo.InvariantCulture),
            _ => string.Empty,
        };

    /// <summary>
    /// Materializes a module's hidden columns onto every produced row so an
    /// <c>arg = 'x'</c> style predicate still evaluates after the argument was bound.
    /// </summary>
    public static IReadOnlyList<SqlValue[]> AppendArguments(
        IReadOnlyList<SqlValue[]> rows,
        IReadOnlyList<SqlValue> arguments,
        TableValuedFunctionSchema schema)
    {
        var result = new SqlValue[rows.Count][];
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var combined = new SqlValue[schema.AllColumns.Count];
            for (var column = 0; column < combined.Length; column++)
            {
                combined[column] = column < schema.VisibleColumns.Count
                    ? (column < row.Length ? row[column] : SqlValue.Null)
                    : arguments[column - schema.VisibleColumns.Count];
            }

            result[index] = combined;
        }

        return result;
    }
}
