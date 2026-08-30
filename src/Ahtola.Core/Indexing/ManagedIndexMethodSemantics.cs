using Ahtola.Core.Indexing;
using Ahtola.Core.Parsing;

namespace Ahtola.Core;

/// <summary>
/// Catalog-level validation and attachment for <c>CREATE INDEX … USING method</c>.
/// </summary>
/// <remarks>
/// Mirrors the shapes Turso rejects in <c>Index::from_sql</c>
/// (turso-src/core/schema.rs:5847-5853) and the resolution it performs at schema load
/// (schema.rs:5854-5864).
/// </remarks>
internal static class ManagedIndexMethodSemantics
{
    /// <summary>Validates one method index against its base table, failing closed on every gap.</summary>
    public static void ValidateDefinition(string tableName, EmbeddedTable table, EmbeddedIndex index)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(index);
        if (index.Method is null)
            return;

        if (index.Unique)
            throw new EmbeddedSqlException($"index '{index.Name}' cannot be UNIQUE and use an index method");
        if (index.IsPartial)
            throw new EmbeddedSqlException($"index '{index.Name}' cannot be partial and use an index method");
        if (table.WithoutRowid)
        {
            throw new EmbeddedSqlException(
                $"index '{index.Name}' cannot use an index method on WITHOUT ROWID table '{tableName}'");
        }
        if (index.Origin != EmbeddedIndexOrigin.Explicit)
            throw new EmbeddedSqlException($"index '{index.Name}' cannot use an index method for a table constraint");
        if (ManagedIndexMethodNames.IsReserved(index.Name))
            throw new EmbeddedSqlException($"object name reserved for internal use: {index.Name}");

        foreach (var column in index.Columns)
        {
            if (column.IsExpression)
                throw new EmbeddedSqlException($"index '{index.Name}' cannot index an expression with an index method");
            if (column.Descending)
                throw new EmbeddedSqlException($"index '{index.Name}' cannot use DESC with an index method");
            if (column.Collation is not null)
                throw new EmbeddedSqlException($"index '{index.Name}' cannot use COLLATE with an index method");
        }

        // Attaching validates the method name, the column shape and every WITH key.
        _ = CreateAttachment(tableName, table, index);
    }

    /// <summary>Resolves the registered method and produces a fresh, immutable attachment.</summary>
    public static ManagedIndexMethodAttachment CreateAttachment(
        string tableName,
        EmbeddedTable table,
        EmbeddedIndex index)
    {
        var method = ManagedIndexMethodRegistry.Resolve(
            index.Method ?? throw new InvalidOperationException("Index is not a method index."));
        if (!method.SupportsColumnParameters
            && index.Columns.Any(static column => column.MethodParameters is { Count: > 0 }))
        {
            throw new EmbeddedSqlException(
                $"index method '{method.Name}' does not support per-column WITH parameters");
        }
        var columns = new ManagedIndexMethodColumn[index.Columns.Count];
        for (var position = 0; position < index.Columns.Count; position++)
        {
            var column = index.Columns[position];
            columns[position] = new ManagedIndexMethodColumn(
                table.Columns[column.ColumnIndex],
                column.ColumnIndex,
                column.MethodParameters);
        }

        return method.Attach(new ManagedIndexMethodConfiguration(
            tableName,
            index.Name,
            columns,
            index.MethodParameters ?? []));
    }

    /// <summary>
    /// Returns the live attachment for a method index, creating it on first use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cache lives on the table, which the catalog already snapshots per transaction and
    /// savepoint, so a rolled-back statement discards its method state with the rest of the catalog.
    /// </para>
    /// <para>
    /// An attachment is only cached once it is fully constructed <em>and</em> its persisted state
    /// envelope has loaded. Caching before the envelope is validated would leave a half-initialized
    /// attachment behind after a malformed catalog row throws, and every later statement would then
    /// answer from it instead of failing. The cache is also keyed by the index definition identity,
    /// so dropping <c>docs_fts</c> and recreating it under the same name with different <c>WITH</c>
    /// options can never resurrect the previous options.
    /// </para>
    /// </remarks>
    public static ManagedIndexMethodAttachment GetAttachment(
        string tableName,
        EmbeddedTable table,
        EmbeddedIndex index)
    {
        if (table.TryGetMethodAttachment(index, out var attachment))
            return attachment;

        attachment = CreateAttachment(tableName, table, index);
        if (index.Sql is { } sql
            && ManagedIndexMethodStateSql.TrySplit(sql, IsMethodIndexDeclaration, out _, out var version, out var state))
        {
            attachment.LoadState(version, state);
        }

        // Publish only after the attachment is complete: a throw above leaves no cache entry, so
        // the next statement retries from a clean slate instead of reusing partial state.
        table.PublishMethodAttachment(index, attachment);
        return attachment;
    }

    /// <summary>
    /// True when a stored SQL text parses as <c>CREATE INDEX … USING method</c>. Used to gate state
    /// envelope decoding so an ordinary index whose comment merely resembles an envelope is not
    /// mangled.
    /// </summary>
    internal static bool IsMethodIndexDeclaration(string declarationSql)
    {
        try
        {
            return SqlParser.Parse(declarationSql, SqlParameterMap.Parse(declarationSql))
                is CreateIndexStatement { Method: not null };
        }
        catch (EmbeddedSqlException)
        {
            return false;
        }
    }

    /// <summary>Opens a per-statement cursor over the table's live rows.</summary>
    public static ManagedIndexMethodCursor OpenCursor(
        string tableName,
        EmbeddedTable table,
        EmbeddedIndex index)
        => GetAttachment(tableName, table, index).Open(new EmbeddedTableIndexSource(table));

    /// <summary>Emits the CREATE INDEX text plus the versioned state envelope for the catalog row.</summary>
    public static string BuildPersistedSql(string tableName, EmbeddedTable table, EmbeddedIndex index)
    {
        var declaration = IndexSqlFormatter.BuildCreateIndexSql(tableName, index);
        if (index.Method is null)
            return declaration;

        var attachment = GetAttachment(tableName, table, index);
        return ManagedIndexMethodStateSql.Append(
            declaration,
            attachment.Definition.StorageVersion,
            attachment.SaveState());
    }

    /// <summary>
    /// Drops cached method state for one index (DROP INDEX, REINDEX, a failed CREATE INDEX). The
    /// method's <c>Destroy</c> hook runs first so it can release anything it owns outside the
    /// attachment before the attachment itself is forgotten.
    /// </summary>
    public static void Forget(EmbeddedTable table, string indexName, bool destroy = false)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (destroy && table.TryGetMethodAttachmentByName(indexName, out var attachment))
        {
            using var cursor = attachment.Open(new EmbeddedTableIndexSource(table));
            cursor.Destroy();
        }

        table.ForgetMethodAttachment(indexName);
    }
}

/// <summary>
/// Adapts an <see cref="EmbeddedTable"/>'s live rows to <see cref="IManagedIndexSource"/>, including
/// the revision counter and mutation journal that make incremental maintenance possible.
/// </summary>
/// <remarks>
/// Row value arrays are replaced rather than mutated by every engine DML path, and every mutation
/// bumps <c>RowStore.Revision</c>, so a method can tell an unchanged table from a changed one in
/// O(1) and can apply a small change in O(changed rows).
/// </remarks>
internal sealed class EmbeddedTableIndexSource(EmbeddedTable table) : IManagedIndexSource
{
    private Dictionary<long, int>? _positions;
    private long _positionsRevision = -1;

    public int RowCount => table.Rows.Count;

    public long Revision => table.Rows.Revision;

    public ManagedIndexSourceDelta? TryGetDelta(long sinceRevision)
        => table.TryGetMethodIndexDelta(sinceRevision);

    public void NotifyRebuilt(long revision) => table.ResetMethodIndexJournalBaseline(revision);

    public long GetRowId(int position)
        => position < table.RowIds.Count ? table.RowIds[position] : position + 1L;

    public SqlValue[] GetRow(int position) => table.Rows[position];

    public bool TryGetPosition(long rowId, out int position)
    {
        if (_positions is null || _positionsRevision != table.Rows.Revision)
        {
            _positions = new Dictionary<long, int>(table.Rows.Count);
            for (var index = 0; index < table.Rows.Count; index++)
                _positions[GetRowId(index)] = index;

            _positionsRevision = table.Rows.Revision;
        }

        return _positions.TryGetValue(rowId, out position);
    }
}
