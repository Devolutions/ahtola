using System.Collections.ObjectModel;

namespace Ahtola.Core;

/// <summary>SQLite affinity exposed by a managed virtual-table schema.</summary>
public enum ManagedVirtualTableAffinity
{
    Blob,
    Text,
    Numeric,
    Integer,
    Real,
}

/// <summary>Declares one column exposed by a managed virtual table.</summary>
public sealed record ManagedVirtualTableColumn(
    string Name,
    ManagedVirtualTableAffinity Affinity = ManagedVirtualTableAffinity.Blob,
    bool IsHidden = false);

/// <summary>
/// Immutable column shape returned by a managed virtual-table module. Hidden columns remain
/// addressable by predicates but are excluded from <c>SELECT *</c>, matching SQLite virtual tables.
/// </summary>
public sealed class ManagedVirtualTableSchema
{
    private readonly ReadOnlyCollection<ManagedVirtualTableColumn> _columns;

    public ManagedVirtualTableSchema(IEnumerable<ManagedVirtualTableColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var materialized = columns.ToArray();
        if (materialized.Length == 0)
            throw new ArgumentException("A virtual table must declare at least one column.", nameof(columns));
        if (materialized.Any(static column => string.IsNullOrWhiteSpace(column.Name)))
            throw new ArgumentException("A virtual table column name cannot be empty.", nameof(columns));
        if (materialized.Select(static column => column.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != materialized.Length)
            throw new ArgumentException("Virtual table column names must be unique.", nameof(columns));

        _columns = Array.AsReadOnly(materialized);
    }

    public IReadOnlyList<ManagedVirtualTableColumn> Columns => _columns;

    public IReadOnlyList<ManagedVirtualTableColumn> VisibleColumns
        => _columns.Where(static column => !column.IsHidden).ToArray();
}

/// <summary>SQLite-compatible operators passed to <see cref="ManagedVirtualTable.BestIndex"/>.</summary>
public enum ManagedVirtualTableConstraintOperator
{
    Equal,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Match,
    Like,
    Glob,
    IsNull,
    IsNotNull,
    Limit,
    Offset,
}

/// <summary>One planner-visible predicate on a virtual-table column.</summary>
public readonly record struct ManagedVirtualTableConstraint(
    int ColumnIndex,
    ManagedVirtualTableConstraintOperator Operator,
    bool Usable = true);

/// <summary>One planner-visible ORDER BY term.</summary>
public readonly record struct ManagedVirtualTableOrderBy(int ColumnIndex, bool Descending);

/// <summary>
/// Maps a constraint received by <see cref="ManagedVirtualTable.BestIndex"/> to the argument
/// vector supplied to <see cref="ManagedVirtualTableCursor.Filter"/>.
/// </summary>
public readonly record struct ManagedVirtualTableConstraintUsage(int ArgumentIndex, bool Omit = false)
{
    /// <summary>Leaves the constraint for the engine to evaluate after the module scan.</summary>
    public static ManagedVirtualTableConstraintUsage Unused => new(0);
}

/// <summary>Module-selected scan strategy, mirroring Turso's <c>IndexInfo</c>.</summary>
public sealed class ManagedVirtualTablePlan
{
    private readonly ReadOnlyCollection<ManagedVirtualTableConstraintUsage> _constraintUsages;

    public ManagedVirtualTablePlan(
        IEnumerable<ManagedVirtualTableConstraintUsage> constraintUsages,
        int indexNumber = 0,
        string? indexString = null,
        bool orderByConsumed = false,
        double estimatedCost = double.MaxValue,
        long estimatedRows = long.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(constraintUsages);
        if (double.IsNaN(estimatedCost) || estimatedCost < 0)
            throw new ArgumentOutOfRangeException(nameof(estimatedCost));
        if (estimatedRows < 0)
            throw new ArgumentOutOfRangeException(nameof(estimatedRows));

        _constraintUsages = Array.AsReadOnly(constraintUsages.ToArray());
        IndexNumber = indexNumber;
        IndexString = indexString;
        OrderByConsumed = orderByConsumed;
        EstimatedCost = estimatedCost;
        EstimatedRows = estimatedRows;
    }

    public IReadOnlyList<ManagedVirtualTableConstraintUsage> ConstraintUsages => _constraintUsages;
    public int IndexNumber { get; }
    public string? IndexString { get; }
    public bool OrderByConsumed { get; }
    public double EstimatedCost { get; }
    public long EstimatedRows { get; }

    internal void ValidateFor(IReadOnlyList<ManagedVirtualTableConstraint> constraints)
    {
        if (ConstraintUsages.Count != constraints.Count)
        {
            throw new InvalidOperationException(
                $"Virtual-table plan returned {ConstraintUsages.Count} constraint usage entries for {constraints.Count} constraints.");
        }

        var usedArguments = new HashSet<int>();
        foreach (var usage in ConstraintUsages)
        {
            if (usage.ArgumentIndex < 0)
                throw new InvalidOperationException("A virtual-table constraint argument index cannot be negative.");
            if (usage.Omit && usage.ArgumentIndex == 0)
            {
                throw new InvalidOperationException(
                    "A virtual-table plan can omit a constraint only when it receives that constraint's filter argument.");
            }
            if (usage.ArgumentIndex > 0 && !usedArguments.Add(usage.ArgumentIndex))
                throw new InvalidOperationException("A virtual-table plan cannot assign one filter argument to multiple constraints.");
        }
    }
}

/// <summary>Inputs passed to a module while CREATE VIRTUAL TABLE is being executed.</summary>
public sealed record ManagedVirtualTableCreateContext(string TableName, IReadOnlyList<string> Arguments);

/// <summary>
/// Explicit module registration contract. Implementations are instantiated directly and registered through
/// <see cref="ManagedVirtualTableModuleRegistry.Register"/>; the engine never discovers modules by reflection.
/// This keeps the mechanism NativeAOT and trimming safe.
/// </summary>
public abstract class ManagedVirtualTableModule
{
    public abstract string Name { get; }

    public abstract ManagedVirtualTable Create(ManagedVirtualTableCreateContext context);
}

/// <summary>
/// A created virtual-table instance. Implement this type for an eponymous module or a table created with
/// <c>CREATE VIRTUAL TABLE ... USING module</c>.
/// </summary>
public abstract class ManagedVirtualTable
{
    public abstract ManagedVirtualTableSchema Schema { get; }

    public abstract ManagedVirtualTablePlan BestIndex(
        IReadOnlyList<ManagedVirtualTableConstraint> constraints,
        IReadOnlyList<ManagedVirtualTableOrderBy> orderBy);

    public abstract ManagedVirtualTableCursor Open();

    /// <summary>
    /// Receives SQLite's VUpdate argv layout: old rowid, new rowid, then declared column values.
    /// A NULL old rowid denotes INSERT and a NULL new rowid denotes DELETE.
    /// </summary>
    public virtual long? Update(IReadOnlyList<SqlValue> arguments)
        => throw new EmbeddedSqlException("attempt to write a readonly virtual table");

    /// <summary>
    /// Begins one autocommit virtual-table mutation statement. Explicit SQL transactions and
    /// savepoints are not supported for managed virtual-table mutations because the generic ABI
    /// has no reversible module-state contract.
    /// </summary>
    public virtual void Begin() { }
    public virtual void Sync() { }
    public virtual void Commit() { }
    public virtual void Rollback() { }
    public virtual void Rename(string newName) { }
    public virtual void Destroy() { }
}

/// <summary>One open virtual-table scan. Filter positions on the first row, if any.</summary>
public abstract class ManagedVirtualTableCursor : IDisposable
{
    public abstract bool Filter(ManagedVirtualTablePlan plan, IReadOnlyList<SqlValue> arguments);
    public abstract void Next();
    public abstract bool Eof { get; }
    public abstract SqlValue Column(int columnIndex);
    public abstract long RowId { get; }
    public virtual void Dispose() { }
}

/// <summary>
/// Global explicit module registry. Registration is a direct managed call, not assembly scanning, so callers
/// must register shipped modules from statically reachable startup code.
/// </summary>
public static class ManagedVirtualTableModuleRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ManagedVirtualTableModule> Modules =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Register(ManagedVirtualTableModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (string.IsNullOrWhiteSpace(module.Name))
            throw new ArgumentException("A virtual-table module name cannot be empty.", nameof(module));

        lock (Gate)
        {
            if (!Modules.TryAdd(module.Name, module))
                throw new InvalidOperationException($"A managed virtual-table module named '{module.Name}' is already registered.");
        }
    }

    public static bool TryResolve(string name, out ManagedVirtualTableModule module)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (Gate)
            return Modules.TryGetValue(name, out module!);
    }

    public static ManagedVirtualTableModule Resolve(string name)
        => TryResolve(name, out var module)
            ? module
            : throw new EmbeddedSqlException($"no such virtual table module: {name}");
}
