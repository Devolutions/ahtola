using Ahtola.Core.Execution;

namespace Ahtola.Core.Compilation;

internal enum ScanAccessKind
{
    Default,
    MultiIndexOr,
    MultiIndexAnd,
}

/// <summary>
/// Describes a single base table that a <see cref="SelectStatement"/> can scan
/// directly. The caller supplies the live row list plus a column resolver so the
/// compiler owns the scan structure while SQL semantics stay in the evaluator.
/// </summary>
/// <param name="TableName">The catalog name of the scanned table.</param>
/// <param name="Qualifier">The alias (or table name) used to qualify columns.</param>
/// <param name="Columns">The table's columns in declaration order.</param>
/// <param name="Rows">The live rows the emitted cursor iterates.</param>
/// <param name="ResolveColumnIndex">
/// Maps a (possibly qualified) column reference to its ordinal, or <c>null</c> when
/// the reference does not name a column of this table.
/// </param>
/// <param name="RowIds">The optional hidden rowids aligned with <paramref name="Rows"/>.</param>
/// <param name="IndexName">The selected logical index, when the rows are in index order.</param>
/// <param name="ColumnDefinitions">The immutable column metadata aligned with <paramref name="Columns"/>.</param>
/// <param name="QualifiedColumnDefinitions">Column metadata keyed by qualified SQL name.</param>
/// <param name="IndexSeek">
/// Optional equality prefix for SEARCH plans: emit SeekGE/IdxGE on these table-column
/// ordinals instead of Rewind, then residual WHERE Filter.
/// </param>
/// <param name="AccessKind">The explicit multi-index shape used by EXPLAIN formatting.</param>
internal sealed record ScanTarget(
    string TableName,
    string Qualifier,
    string[] Columns,
    IReadOnlyList<SqlValue[]> Rows,
    Func<string, int?> ResolveColumnIndex,
    IReadOnlyList<long>? RowIds = null,
    string? IndexName = null,
    IReadOnlyList<EmbeddedColumn?>? ColumnDefinitions = null,
    IReadOnlyDictionary<string, EmbeddedColumn>? QualifiedColumnDefinitions = null,
    IndexSeekPrefix? IndexSeek = null,
    ScanAccessKind AccessKind = ScanAccessKind.Default)
{
    public bool HasRowId => RowIds is not null;

    /// <summary>
    /// Materializes this scan's cursor source. Table B-trees rewind to their smallest rowid; index
    /// scans retain their index-key order instead.
    /// </summary>
    public VdbeCursorSource CreateCursorSource()
    {
        if (RowIds is null || IndexName is not null || RowIds.Count < 2)
            return new VdbeCursorSource(Rows, RowIds);

        var rowOrder = Enumerable.Range(0, RowIds.Count).ToArray();
        Array.Sort(rowOrder, (left, right) => RowIds[left].CompareTo(RowIds[right]));

        var rows = new SqlValue[Rows.Count][];
        var rowIds = new long[RowIds.Count];
        for (var outputIndex = 0; outputIndex < rowOrder.Length; outputIndex++)
        {
            var sourceIndex = rowOrder[outputIndex];
            rows[outputIndex] = Rows[sourceIndex];
            rowIds[outputIndex] = RowIds[sourceIndex];
        }

        return new VdbeCursorSource(rows, rowIds);
    }
}

/// <summary>
/// A lowered <see cref="SelectStatement"/>: the emitted <see cref="VdbeProgram"/>
/// together with the live row sources its cursors iterate at execution time.
/// </summary>
internal sealed record CompiledSelect(
    VdbeProgram Program,
    IReadOnlyList<VdbeCursorSource> CursorSources,
    IReadOnlyList<int>? ParameterIndices = null);

/// <summary>
/// Equality prefix for a managed index SEARCH: table-column ordinals aligned with
/// literal/parameter bounds, consumed by <see cref="SelectStatementCompiler"/> to emit
/// <c>SeekGE</c>/<c>IdxGE</c> before the residual WHERE filter.
/// </summary>
internal sealed record IndexSeekPrefix(
    IReadOnlyList<int> KeyColumns,
    IReadOnlyList<Expression> Bounds);
