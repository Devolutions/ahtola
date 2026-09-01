namespace Ahtola.Core.Execution;

/// <summary>
/// The cursor and write bindings a DDL program uses to read and write rows that live in a
/// <see cref="ManagedSchemaStage"/>: the transaction-local <c>sqlite_schema</c> row set, and the rows of a
/// table the same program just created.
/// </summary>
/// <remarks>
/// <para>
/// These are ordinary <see cref="VdbeCursorSource"/>/<see cref="VdbeWriteTarget"/> bindings, so
/// <c>NewRowid</c>, <c>Insert</c>, <c>Rewind</c>/<c>Next</c> and <c>Column</c> behave exactly as they do
/// for any other cursor. What makes them schema bindings is only where their rows live: in the stage, not
/// in the live catalog and not in storage. Discarding the stage discards everything they wrote.
/// </para>
/// <para>
/// Every projection is <em>live</em>. A binding never snapshots the rows it exposes, because the program
/// writes through the same binding it reads through: <c>NewRowid</c> immediately after an <c>Insert</c>
/// has to observe the row that insert added, exactly as a b-tree cursor would.
/// </para>
/// </remarks>
internal static class ManagedSchemaProgramBindings
{
    /// <summary>The <c>sqlite_schema</c> column count, the width of every schema record.</summary>
    public const int SchemaColumnCount = 5;

    /// <summary>The catalog name a schema cursor reports in <c>EXPLAIN</c>.</summary>
    public const string SchemaTableName = "sqlite_schema";

    /// <summary>A cursor source over the stage's schema rows, in schema order.</summary>
    public static VdbeCursorSource CreateSchemaCursorSource(ManagedSchemaStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return new VdbeCursorSource(
            new SchemaRowValueView(stage),
            new SchemaRowIdView(stage),
            () => stage.Rows.Count == 0 ? null : stage.Rows.Rows[^1].RowId);
    }

    /// <summary>
    /// A write target that appends a five-column <c>sqlite_schema</c> record to the stage's row set under
    /// the rowid the program allocated, and removes the row a delete-scan is positioned on.
    /// </summary>
    public static VdbeWriteTarget CreateSchemaWriteTarget(ManagedSchemaStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return new VdbeWriteTarget
        {
            TableName = SchemaTableName,
            RowCount = 0,
            LiveRowCount = () => stage.Rows.Count,
            InsertRecord = (rowId, values) => InsertSchemaRow(stage, rowId, values),
            DeleteRow = position => DeleteSchemaRow(stage, position),
            // A schema row is durable the moment the outer catalog/persist boundary publishes the stage,
            // so there is nothing for a per-cursor commit to flush.
            Commit = static () => null,
        };
    }

    /// <summary>A cursor source over the rows of a stage-resident table, resolved by name on each access.</summary>
    /// <remarks>
    /// The table does not exist when the program is built — <c>ParseSchema</c> creates it partway through —
    /// so the binding resolves it lazily rather than capturing an instance that would still be null.
    /// </remarks>
    public static VdbeCursorSource CreateTableCursorSource(ManagedSchemaStage stage, string tableName)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        return new VdbeCursorSource(
            new TableRowValueView(stage, tableName),
            new TableRowIdView(stage, tableName),
            () =>
            {
                var table = RequireTable(stage, tableName);
                return table.RowIds.Count == 0 ? null : table.RowIds[^1];
            });
    }

    /// <summary>
    /// A write target that appends one register-built row to a stage-resident table, applying the table's
    /// column affinities exactly as the direct <c>CREATE TABLE AS SELECT</c> population did.
    /// </summary>
    public static VdbeWriteTarget CreateTableWriteTarget(ManagedSchemaStage stage, string tableName)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        return new VdbeWriteTarget
        {
            TableName = tableName,
            RowCount = 0,
            LiveRowCount = () => stage.Catalog.Tables.TryGetValue(tableName, out var table) ? table.Rows.Count : 0,
            InsertRecord = (rowId, values) => InsertTableRow(stage, tableName, rowId, values),
            Commit = static () => null,
        };
    }

    /// <summary>
    /// A cursor source over the rows of a table the program scans in order to delete from — the
    /// <c>sqlite_sequence</c> watermark of a dropped AUTOINCREMENT table, or a change-capture version
    /// entry. Every access resolves the staged entry, so the scan follows the private clone the first
    /// delete detaches.
    /// </summary>
    public static VdbeCursorSource CreateMutableTableCursorSource(ManagedSchemaStage stage, string tableName)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        return new VdbeCursorSource(
            new MutableTableRowValueView(stage, tableName),
            new MutableTableRowIdView(stage, tableName),
            () =>
            {
                var table = RequireTable(stage, tableName);
                return table.RowIds.Count == 0 ? null : table.RowIds[^1];
            });
    }

    /// <summary>
    /// A write target that removes the row a scan is positioned on from a stage-resident table, detaching
    /// the table into a private clone first so the deletion is staged like every other schema effect.
    /// </summary>
    public static VdbeWriteTarget CreateMutableTableWriteTarget(ManagedSchemaStage stage, string tableName)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        return new VdbeWriteTarget
        {
            TableName = tableName,
            RowCount = 0,
            LiveRowCount = () => stage.Catalog.Tables.TryGetValue(tableName, out var table) ? table.Rows.Count : 0,
            DeleteRow = position => DeleteTableRow(stage, tableName, position),
            Commit = static () => null,
        };
    }

    /// <summary>
    /// A cursor source over already-materialized rows that honors cancellation between rows.
    /// </summary>
    /// <remarks>
    /// The rows a <c>CREATE TABLE AS SELECT</c> population scans were produced by the query engine, which
    /// already honored the token; this keeps the copy itself cancellable too, exactly as the direct
    /// row-by-row copy it replaces was. The source is value-only: the population loop allocates each
    /// row's key with <c>NewRowid</c> against the target rather than carrying one over.
    /// </remarks>
    public static VdbeCursorSource CreatePopulationCursorSource(
        IReadOnlyList<SqlValue[]> rows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return new VdbeCursorSource(new CancellableRowView(rows, cancellationToken));
    }

    /// <summary>Resolves a table the program expects <c>ParseSchema</c> to have already adopted.</summary>
    internal static EmbeddedTable RequireTable(ManagedSchemaStage stage, string tableName)
        => stage.Catalog.Tables.TryGetValue(tableName, out var table)
            ? table
            : throw new VdbeSchemaExecutionException(
                $"The schema of '{stage.DatabaseName}' has no table '{tableName}'; "
                + "a schema program must adopt a table through ParseSchema before writing to it.");

    private static long InsertSchemaRow(ManagedSchemaStage stage, long rowId, SqlValue[] values)
    {
        if (values.Length != SchemaColumnCount)
        {
            throw new VdbeSchemaExecutionException(
                $"A sqlite_schema record must have {SchemaColumnCount} columns, but the program built {values.Length}.");
        }

        if (rowId <= ManagedSchemaRow.UnassignedRowId)
        {
            throw new VdbeSchemaExecutionException(
                $"A sqlite_schema row cannot be written under rowid {rowId}.");
        }

        var row = new ManagedSchemaRow(
            rowId,
            RequireText(values[0], "type"),
            RequireText(values[1], "name"),
            RequireText(values[2], "tbl_name"),
            RequireRootPage(values[3]),
            values[4].Kind == SqlValueKind.Null ? null : RequireText(values[4], "sql"));
        return stage.Rows.Add(row).RowId;
    }

    /// <summary>
    /// Removes the schema row a delete scan is positioned on. The row is identified by its type and name
    /// rather than by position, so removing it can never take out a neighbour if the scan and the row set
    /// disagree about ordering.
    /// </summary>
    private static void DeleteSchemaRow(ManagedSchemaStage stage, int position)
    {
        if (position < 0 || position >= stage.Rows.Count)
        {
            throw new VdbeSchemaExecutionException(
                $"A sqlite_schema delete addressed row {position}, but the schema holds {stage.Rows.Count} row(s).");
        }

        var row = stage.Rows.Rows[position];
        if (!stage.Rows.Remove(row.Type, row.Name))
        {
            throw new VdbeSchemaExecutionException(
                $"sqlite_schema has no {row.Type} named '{row.Name}' to delete.");
        }
    }

    private static long InsertTableRow(
        ManagedSchemaStage stage,
        string tableName,
        long rowId,
        SqlValue[] values)
    {
        var table = RequireTable(stage, tableName);
        if (values.Length != table.ColumnDefinitions.Length)
        {
            throw new VdbeSchemaExecutionException(
                $"A row written to '{tableName}' has {values.Length} columns but the table declares {table.ColumnDefinitions.Length}.");
        }

        table.ApplyAffinities(values);
        table.Rows.Add(values);
        table.RowIds.Add(rowId);
        return rowId;
    }

    /// <summary>
    /// Removes the row a scan is positioned on from a stage-resident table, after detaching the table so
    /// the removal lands on a clone nobody outside the stage can observe.
    /// </summary>
    private static void DeleteTableRow(ManagedSchemaStage stage, string tableName, int position)
    {
        RequireTable(stage, tableName);
        if (!stage.TryDetachTable(tableName, out var table))
        {
            throw new VdbeSchemaExecutionException(
                $"A delete on '{tableName}' could not detach the table from the schema of '{stage.DatabaseName}'.");
        }

        if (position < 0 || position >= table.Rows.Count)
        {
            throw new VdbeSchemaExecutionException(
                $"A delete on '{tableName}' addressed row {position}, but the table holds {table.Rows.Count} row(s).");
        }

        table.Rows.RemoveAt(position);
        if (position < table.RowIds.Count)
            table.RowIds.RemoveAt(position);
    }

    private static string RequireText(SqlValue value, string column)
        => value.Kind == SqlValueKind.Text
            ? value.AsText()
            : throw new VdbeSchemaExecutionException(
                $"A sqlite_schema record's '{column}' column must be text, but the program built {value.Kind}.");

    private static uint RequireRootPage(SqlValue value)
    {
        if (value.Kind != SqlValueKind.Integer)
        {
            throw new VdbeSchemaExecutionException(
                $"A sqlite_schema record's 'rootpage' column must be an integer, but the program built {value.Kind}.");
        }

        var rootPage = value.AsInteger();
        if (rootPage is < 0 or > uint.MaxValue)
        {
            throw new VdbeSchemaExecutionException(
                $"A sqlite_schema record's rootpage {rootPage} is outside SQLite's page-number range.");
        }

        return (uint)rootPage;
    }

    /// <summary>A read-only list projected from a live source, without copying it.</summary>
    private abstract class LiveView<T> : IReadOnlyList<T>
    {
        public abstract int Count { get; }

        public abstract T this[int index] { get; }

        public IEnumerator<T> GetEnumerator()
        {
            for (var index = 0; index < Count; index++)
                yield return this[index];
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class SchemaRowValueView(ManagedSchemaStage stage) : LiveView<SqlValue[]>
    {
        public override int Count => stage.Rows.Count;

        public override SqlValue[] this[int index] => stage.Rows.Rows[index].ToValues();
    }

    private sealed class SchemaRowIdView(ManagedSchemaStage stage) : LiveView<long>
    {
        public override int Count => stage.Rows.Count;

        public override long this[int index] => stage.Rows.Rows[index].RowId;
    }

    private sealed class TableRowValueView(ManagedSchemaStage stage, string tableName) : LiveView<SqlValue[]>
    {
        public override int Count => Resolve()?.Rows.Count ?? 0;

        public override SqlValue[] this[int index] => RequireTable(stage, tableName).Rows[index];

        // Before ParseSchema adopts the table the cursor is legitimately empty rather than broken: a
        // program may compute over it (NewRowid on an empty table yields 1) before the table exists.
        private EmbeddedTable? Resolve()
            => stage.Catalog.Tables.TryGetValue(tableName, out var table) ? table : null;
    }

    private sealed class TableRowIdView(ManagedSchemaStage stage, string tableName) : LiveView<long>
    {
        private EmbeddedTable? _table;

        public override int Count => Resolve()?.RowIds.Count ?? 0;

        public override long this[int index] => RequireTable().RowIds[index];

        private EmbeddedTable? Resolve()
            => _table ??= stage.Catalog.Tables.TryGetValue(tableName, out var table) ? table : null;

        private EmbeddedTable RequireTable()
            => Resolve() ?? ManagedSchemaProgramBindings.RequireTable(stage, tableName);
    }

    private sealed class MutableTableRowValueView(ManagedSchemaStage stage, string tableName)
        : LiveView<SqlValue[]>
    {
        public override int Count => Resolve()?.Rows.Count ?? 0;

        // Every access resolves the staged entry rather than caching it, because the first delete
        // replaces that entry with the private clone the rest of the scan has to walk.
        public override SqlValue[] this[int index] => RequireTable(stage, tableName).Rows[index];

        private EmbeddedTable? Resolve()
            => stage.Catalog.Tables.TryGetValue(tableName, out var table) ? table : null;
    }

    private sealed class MutableTableRowIdView(ManagedSchemaStage stage, string tableName) : LiveView<long>
    {
        public override int Count => Resolve()?.RowIds.Count ?? 0;

        public override long this[int index] => RequireTable(stage, tableName).RowIds[index];

        private EmbeddedTable? Resolve()
            => stage.Catalog.Tables.TryGetValue(tableName, out var table) ? table : null;
    }

    private sealed class CancellableRowView(IReadOnlyList<SqlValue[]> rows, CancellationToken cancellationToken)
        : LiveView<SqlValue[]>
    {
        public override int Count => rows.Count;

        public override SqlValue[] this[int index]
        {
            get
            {
                cancellationToken.ThrowIfCancellationRequested();
                return rows[index];
            }
        }
    }
}
