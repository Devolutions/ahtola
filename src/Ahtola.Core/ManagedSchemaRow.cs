using Ahtola.Core.Indexing;

namespace Ahtola.Core;

/// <summary>
/// One <c>sqlite_schema</c> row in its on-disk column order (<c>type, name, tbl_name, rootpage, sql</c>)
/// plus the rowid that orders it inside the schema b-tree.
/// </summary>
/// <remarks>
/// <para>
/// This is the single row shape shared by the three producers/consumers that used to model schema rows
/// independently: <see cref="EmbeddedFileStore"/> when it reads or rebuilds the schema b-tree,
/// <c>sqlite_schema</c>/<c>sqlite_master</c> query reads, and the transaction-local schema execution
/// context a DDL program runs against.
/// </para>
/// <para>
/// <see cref="RootPage"/> keeps SQLite's unsigned 32-bit page-number domain. A row staged by a DDL
/// program may carry a <em>logical</em> root reserved from <see cref="ManagedSchemaRootPlan"/> instead of
/// a physical page; see that type for the adapter invariant that keeps a logical root out of durable
/// storage.
/// </para>
/// </remarks>
internal sealed record ManagedSchemaRow(
    long RowId,
    string Type,
    string Name,
    string TableName,
    uint RootPage,
    string? Sql)
{
    /// <summary>The rowid a row carries before it has been placed in an ordered set.</summary>
    public const long UnassignedRowId = 0;

    public const string TableType = "table";
    public const string IndexType = "index";
    public const string ViewType = "view";
    public const string TriggerType = "trigger";

    /// <summary>The <c>sqlite_schema</c> column names, in on-disk order.</summary>
    public static string[] CreateColumnNames() => ["type", "name", "tbl_name", "rootpage", "sql"];

    /// <summary>Creates a row whose rowid is assigned later by <see cref="ManagedSchemaRowSet"/>.</summary>
    public ManagedSchemaRow(string type, string name, string tableName, uint rootPage, string? sql)
        : this(UnassignedRowId, type, name, tableName, rootPage, sql)
    {
    }

    public bool HasRowId => RowId > UnassignedRowId;

    public bool IsTable => string.Equals(Type, TableType, StringComparison.Ordinal);

    public bool IsIndex => string.Equals(Type, IndexType, StringComparison.Ordinal);

    public bool IsView => string.Equals(Type, ViewType, StringComparison.Ordinal);

    public bool IsTrigger => string.Equals(Type, TriggerType, StringComparison.Ordinal);

    /// <summary>
    /// Whether this row describes a virtual table. SQLite stores a virtual table as a <c>table</c> row
    /// with rootpage 0, which is exactly how the managed file store round-trips one.
    /// </summary>
    public bool IsVirtualTable => IsTable && RootPage == 0;

    /// <summary>Whether this row describes an implicit (UNIQUE/PRIMARY KEY) constraint index.</summary>
    public bool IsImplicitIndex => IsIndex && Sql is null;

    /// <summary>Projects the row into the five <c>sqlite_schema</c> column values.</summary>
    public SqlValue[] ToValues() =>
    [
        SqlValue.Text(Type),
        SqlValue.Text(Name),
        SqlValue.Text(TableName),
        SqlValue.Integer(RootPage),
        Sql is null ? SqlValue.Null : SqlValue.Text(Sql),
    ];
}

/// <summary>
/// Selects which SQL text a schema row carries for an object whose stored form differs from the form a
/// <c>sqlite_schema</c> query exposes.
/// </summary>
/// <remarks>
/// <para>
/// Method indexes and virtual tables both differ: their persisted text appends a versioned state envelope
/// that the file store must round-trip, while a query over <c>sqlite_schema</c> shows the plain
/// declaration. Making the choice an explicit parameter keeps both projections in one factory instead of
/// two divergent row builders.
/// </para>
/// <para>
/// A transaction-local schema stage carries a virtual table's state in the live definition it holds, not
/// in the row, so it projects the declaration; only a row that has to stand on its own in storage needs
/// the envelope.
/// </para>
/// </remarks>
internal enum ManagedSchemaSqlForm
{
    /// <summary>The declaration as written, without any storage-only envelope.</summary>
    Declared,

    /// <summary>The exact text the managed file store writes into the schema b-tree.</summary>
    Persisted,
}

/// <summary>
/// Builds <see cref="ManagedSchemaRow"/> values from catalog objects. This is the one place that decides a
/// row's <c>type</c>, <c>name</c>, <c>tbl_name</c> and <c>sql</c>, so the file store and
/// <c>sqlite_schema</c> reads cannot drift apart on row shape.
/// </summary>
/// <remarks>
/// Row <em>ordering</em> stays with each producer: the file store writes rowid order derived from its own
/// persistence order, while a query read materializes catalog order. <see cref="ManagedSchemaRowSet"/>
/// carries whichever order its producer chose.
/// </remarks>
internal static class ManagedSchemaRowFactory
{
    public static ManagedSchemaRow ForTable(string name, EmbeddedTable table, uint rootPage)
        => new(
            ManagedSchemaRow.TableType,
            name,
            name,
            rootPage,
            table.Sql ?? EmbeddedDatabase.BuildCreateTableSql(name, table));

    public static ManagedSchemaRow ForVirtualTable(
        EmbeddedDatabase.VirtualTableDefinition definition,
        ManagedSchemaSqlForm form = ManagedSchemaSqlForm.Persisted)
        => new(
            ManagedSchemaRow.TableType,
            definition.Name,
            definition.Name,
            0,
            form == ManagedSchemaSqlForm.Persisted
                ? ManagedVirtualTableSchemaSql.Build(definition)
                : ManagedVirtualTableSchemaSql.BuildDeclaration(
                    definition.Name,
                    definition.ModuleName,
                    definition.Arguments));

    public static ManagedSchemaRow ForIndex(
        string tableName,
        EmbeddedTable table,
        EmbeddedIndex index,
        uint rootPage,
        ManagedSchemaSqlForm form)
        => new(
            ManagedSchemaRow.IndexType,
            index.Name,
            tableName,
            rootPage,
            index.Origin == EmbeddedIndexOrigin.Explicit
                ? BuildIndexSql(tableName, table, index, form)
                : null);

    public static ManagedSchemaRow ForView(ViewDefinition view)
        => new(ManagedSchemaRow.ViewType, view.Name, view.Name, 0, view.Sql);

    public static ManagedSchemaRow ForTrigger(TriggerDefinition trigger)
        => new(ManagedSchemaRow.TriggerType, trigger.Name, trigger.TableName, 0, trigger.Sql);

    private static string BuildIndexSql(
        string tableName,
        EmbeddedTable table,
        EmbeddedIndex index,
        ManagedSchemaSqlForm form)
    {
        if (form == ManagedSchemaSqlForm.Persisted && index.IsMethodIndex)
            return ManagedIndexMethodSemantics.BuildPersistedSql(tableName, table, index);

        return index.Sql ?? IndexSqlFormatter.BuildCreateIndexSql(tableName, index);
    }
}

/// <summary>
/// Raised when a schema row set is asked to do something its invariants forbid — add a duplicate object
/// name, replace or remove a row that is not present, or publish a row that still carries a logical root.
/// </summary>
internal sealed class ManagedSchemaRowException : EmbeddedSqlException
{
    public ManagedSchemaRowException(string message) : base(message)
    {
    }
}

/// <summary>
/// An ordered, namespace-and-name-keyed set of <c>sqlite_schema</c> rows. Order is the rowid order the schema b-tree
/// stores, so enumerating the set reproduces the exact row sequence a <c>sqlite_schema</c> scan yields.
/// </summary>
/// <remarks>
/// <para>
/// Mutations preserve position: <see cref="Add"/> appends with the next rowid, <see cref="Replace"/> keeps
/// a row's slot and rowid, and <see cref="Remove(string)"/> closes the gap without renumbering surviving rows.
/// That mirrors SQLite, where deleting a schema row frees its rowid while every other row keeps its own.
/// </para>
/// <para>
/// The set is transaction-local state. Nothing here writes to a pager, a header, or a live catalog;
/// publication remains the existing catalog/persist boundary's job.
/// </para>
/// </remarks>
internal sealed class ManagedSchemaRowSet
{
    private readonly List<ManagedSchemaRow> _rows;
    private readonly Dictionary<string, int> _positionsByIdentity;
    private long _nextRowId;

    public ManagedSchemaRowSet()
    {
        _rows = [];
        _positionsByIdentity = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _nextRowId = 1;
    }

    private ManagedSchemaRowSet(ManagedSchemaRowSet source)
    {
        _rows = [.. source._rows];
        _positionsByIdentity = new Dictionary<string, int>(source._positionsByIdentity, StringComparer.OrdinalIgnoreCase);
        _nextRowId = source._nextRowId;
    }

    /// <summary>The rows in schema b-tree order.</summary>
    public IReadOnlyList<ManagedSchemaRow> Rows => _rows;

    public int Count => _rows.Count;

    /// <summary>The rowid the next <see cref="Add"/> assigns.</summary>
    public long NextRowId => _nextRowId;

    /// <summary>
    /// Builds a set from rows already in schema order, adopting each row's stored rowid when it has one
    /// and assigning the next free rowid when it does not.
    /// </summary>
    public static ManagedSchemaRowSet FromOrderedRows(IEnumerable<ManagedSchemaRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var set = new ManagedSchemaRowSet();
        foreach (var row in rows)
            set.Add(row);
        return set;
    }

    /// <summary>Creates an independent copy; mutating the copy cannot affect the original.</summary>
    public ManagedSchemaRowSet Clone() => new(this);

    public bool Contains(string name)
        => _positionsByIdentity.ContainsKey(ObjectIdentity(name))
            || _positionsByIdentity.ContainsKey(TriggerIdentity(name));

    public bool TryGet(string name, out ManagedSchemaRow row)
    {
        if (_positionsByIdentity.TryGetValue(ObjectIdentity(name), out var position)
            || _positionsByIdentity.TryGetValue(TriggerIdentity(name), out position))
        {
            row = _rows[position];
            return true;
        }

        row = null!;
        return false;
    }

    /// <summary>
    /// Appends <paramref name="row"/>, assigning the next rowid when it does not already carry one.
    /// A row that carries a rowid keeps it, which is how a set rebuilt from the schema b-tree preserves
    /// the rowids SQLite stored.
    /// </summary>
    public ManagedSchemaRow Add(ManagedSchemaRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        ValidateRow(row);
        var identity = Identity(row);
        if (_positionsByIdentity.ContainsKey(identity))
        {
            throw new ManagedSchemaRowException(
                $"sqlite_schema already contains an object named '{row.Name}'.");
        }

        var placed = row.HasRowId ? row : row with { RowId = _nextRowId };
        if (placed.RowId < _nextRowId && _rows.Count > 0)
        {
            throw new ManagedSchemaRowException(
                $"sqlite_schema row '{placed.Name}' has rowid {placed.RowId}, which does not follow the previous row.");
        }

        _positionsByIdentity.Add(identity, _rows.Count);
        _rows.Add(placed);
        _nextRowId = placed.RowId + 1;
        return placed;
    }

    /// <summary>
    /// Replaces the row named <paramref name="row"/>.<see cref="ManagedSchemaRow.Name"/> in place, keeping
    /// its slot and rowid. Renaming an object is <see cref="Rename"/>, not a replace.
    /// </summary>
    public ManagedSchemaRow Replace(ManagedSchemaRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        ValidateRow(row);
        if (!_positionsByIdentity.TryGetValue(Identity(row), out var position))
        {
            throw new ManagedSchemaRowException(
                $"sqlite_schema has no object named '{row.Name}' to replace.");
        }

        var replaced = row with { RowId = _rows[position].RowId };
        _rows[position] = replaced;
        return replaced;
    }

    /// <summary>
    /// Renames the row named <paramref name="from"/> to <paramref name="to"/>, keeping its slot and rowid,
    /// and rewrites the <c>tbl_name</c> of every row that pointed at the old name.
    /// </summary>
    public void Rename(string from, string to)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        if (!_positionsByIdentity.TryGetValue(ObjectIdentity(from), out var position))
            throw new ManagedSchemaRowException($"sqlite_schema has no object named '{from}' to rename.");
        if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase)
            && _positionsByIdentity.ContainsKey(ObjectIdentity(to)))
            throw new ManagedSchemaRowException($"sqlite_schema already contains an object named '{to}'.");

        for (var index = 0; index < _rows.Count; index++)
        {
            var row = _rows[index];
            var renamedName = index == position ? to : row.Name;
            var renamedTable = string.Equals(row.TableName, from, StringComparison.OrdinalIgnoreCase)
                ? to
                : row.TableName;
            if (ReferenceEquals(renamedName, row.Name) && ReferenceEquals(renamedTable, row.TableName))
                continue;

            _rows[index] = row with { Name = renamedName, TableName = renamedTable };
        }

        _positionsByIdentity.Remove(ObjectIdentity(from));
        _positionsByIdentity[ObjectIdentity(to)] = position;
    }

    /// <summary>Removes the named row, closing its slot without renumbering the survivors.</summary>
    public bool Remove(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var identity = ObjectIdentity(name);
        if (!_positionsByIdentity.TryGetValue(identity, out var position))
        {
            identity = TriggerIdentity(name);
            if (!_positionsByIdentity.TryGetValue(identity, out position))
                return false;
        }

        return RemoveAt(identity, position);
    }

    public bool Remove(string type, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var identity = Identity(type, name);
        if (!_positionsByIdentity.TryGetValue(identity, out var position))
            return false;

        return RemoveAt(identity, position);
    }

    private bool RemoveAt(string identity, int position)
    {
        _rows.RemoveAt(position);
        _positionsByIdentity.Remove(identity);
        foreach (var key in _positionsByIdentity.Keys.ToArray())
        {
            if (_positionsByIdentity[key] > position)
                _positionsByIdentity[key]--;
        }

        // SQLite's NewRowid hands out one past the largest rowid the b-tree still holds, so deleting the
        // last schema row frees its rowid for reuse. Recomputing here keeps the set in step with the
        // cursor binding a program allocates rowids through, which reads the same largest-row answer.
        _nextRowId = _rows.Count == 0 ? 1 : _rows[^1].RowId + 1;
        return true;
    }

    /// <summary>
    /// Fails when any row still carries a root reserved from <paramref name="plan"/>. The outer persist
    /// adapter calls this before writing, so a logical root can never reach the schema b-tree.
    /// </summary>
    public void ValidateNoLogicalRoots(ManagedSchemaRootPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        foreach (var row in _rows)
        {
            if (plan.IsLogicalRoot(row.RootPage))
            {
                throw new ManagedSchemaRowException(
                    $"sqlite_schema row '{row.Name}' still carries logical root {row.RootPage}; "
                    + "logical roots must be mapped to physical roots before publication.");
            }
        }
    }

    private static void ValidateRow(ManagedSchemaRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Name))
            throw new ManagedSchemaRowException("A sqlite_schema row must have a name.");
        if (string.IsNullOrWhiteSpace(row.TableName))
            throw new ManagedSchemaRowException($"sqlite_schema row '{row.Name}' must have a tbl_name.");
        if (row.Type is not (ManagedSchemaRow.TableType
            or ManagedSchemaRow.IndexType
            or ManagedSchemaRow.ViewType
            or ManagedSchemaRow.TriggerType))
        {
            throw new ManagedSchemaRowException(
                $"sqlite_schema row '{row.Name}' has unsupported type '{row.Type}'.");
        }
        if (row.RootPage == 1)
        {
            throw new ManagedSchemaRowException(
                $"sqlite_schema row '{row.Name}' cannot use page 1, which holds sqlite_schema itself.");
        }
        if ((row.IsView || row.IsTrigger) && row.RootPage != 0)
        {
            throw new ManagedSchemaRowException(
                $"sqlite_schema {row.Type} '{row.Name}' must have rootpage 0.");
        }
        if (row.IsIndex && row.RootPage == 0)
            throw new ManagedSchemaRowException($"sqlite_schema index '{row.Name}' must have a rootpage.");
    }

    private static string Identity(ManagedSchemaRow row) => Identity(row.Type, row.Name);

    private static string Identity(string type, string name)
        => string.Equals(type, ManagedSchemaRow.TriggerType, StringComparison.Ordinal)
            ? TriggerIdentity(name)
            : ObjectIdentity(name);

    private static string ObjectIdentity(string name) => "object\0" + name;

    private static string TriggerIdentity(string name) => "trigger\0" + name;
}
