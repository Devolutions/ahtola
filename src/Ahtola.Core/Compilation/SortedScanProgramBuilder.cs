using Ahtola.Core.Execution;

namespace Ahtola.Core.Compilation;

/// <summary>
/// One output column of a sorted scan: either a column read from the scanned row or a
/// folded compile-time constant. Mirrors the SELECT compiler's projection lowering but
/// is expressed in primitives so the builder stays free of AST and SQL semantics.
/// </summary>
public readonly record struct SortedScanColumn
{
    private SortedScanColumn(bool isConstant, int columnIndex, SqlValue constant)
    {
        IsConstant = isConstant;
        ColumnIndex = columnIndex;
        Constant = constant;
    }

    public bool IsConstant { get; }

    /// <summary>The ordinal of the scanned column this output projects (column outputs).</summary>
    public int ColumnIndex { get; }

    /// <summary>The value emitted for a constant output.</summary>
    public SqlValue Constant { get; }

    public static SortedScanColumn ForColumn(int columnIndex)
    {
        if (columnIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(columnIndex));

        return new SortedScanColumn(false, columnIndex, default);
    }

    public static SortedScanColumn ForConstant(SqlValue value) => new(true, 0, value);
}

/// <summary>
/// Lowers a single base-table scan whose result set is ordered by an arbitrary key into
/// a runnable <see cref="VdbeProgram"/>. The program materializes every scanned row into
/// a sorter, orders it with the supplied comparer, and drains the sorted rows to build
/// the projected output — so ORDER BY runs entirely through the resumable state machine
/// rather than the tree-walking evaluator.
/// </summary>
/// <remarks>
/// The builder owns only the program's control flow and register/jump layout. Row-value
/// semantics (predicate evaluation and ORDER BY comparison, including affinity,
/// collation, direction, and NULL ordering) are supplied by the caller through the
/// <see cref="VdbeRowPredicate"/> and <see cref="VdbeRowComparer"/> delegates, exactly as
/// the existing scan and DML compilers delegate their semantics. The emitted program is
/// data-free: the scanned rows are bound at execution time through a
/// <see cref="VdbeCursorSource"/>.
/// <code>
///   0            OpenReadCursor
///   1            OpenSorter
///   2            Rewind        -> sortAddr     (empty table)
///   loopStart    [Filter       -> nextAddr]    (WHERE)
///                Column c0.i -> r[i]           (materialize full row: i in 0..W-1)
///                [RowId        -> r[W]]        (only when carryRowId: trailing rowid slot)
///                SorterInsert  r[0..W-1]       (r[0..W] when carryRowId carries the rowid)
///   nextAddr     Next          -> loopStart
///                CloseCursor
///   sortAddr     SorterSort    -> drainDone     (nothing to drain)
///   drainLoop    SorterData    -> r[0..W-1]
///                Copy/LoadConstant per output register (into r[W..W+P-1])
///                ResultRow r[W..W+P-1]
///                SorterNext    -> drainLoop
///   drainDone    CloseSorter
///                Halt
/// </code>
/// </remarks>
public static class SortedScanProgramBuilder
{
    public static VdbeProgram Build(
        string tableName,
        int tableColumnCount,
        IReadOnlyList<SortedScanColumn> projections,
        VdbeRowComparer comparer,
        VdbeRowPredicate? predicate = null,
        bool carryRowId = false)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(projections);
        ArgumentNullException.ThrowIfNull(comparer);
        if (tableColumnCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(tableColumnCount), "A sorted scan needs at least one column.");
        if (projections.Count == 0)
            throw new ArgumentException("A sorted scan must project at least one output column.", nameof(projections));

        foreach (var projection in projections)
        {
            if (!projection.IsConstant && projection.ColumnIndex >= tableColumnCount)
            {
                throw new ArgumentException(
                    $"Projection reads column {projection.ColumnIndex} of a {tableColumnCount}-column table.",
                    nameof(projections));
            }
        }

        var cursor = new Cursor(0);
        var sorter = new Sorter(0);
        var width = tableColumnCount;
        var outputCount = projections.Count;
        var filterCount = predicate is null ? 0 : 1;

        // When carryRowId is set, an extra trailing register holds the scanned row's rowid
        // (populated by a RowId opcode); the sorter record keeps it for output/shape
        // compatibility while equal keys preserve sorter-insertion order (the stable
        // sorter breaks ties by insertion index = scan order). The staging block grows
        // from W to W+1 columns and the output block shifts past it. WITHOUT-ROWID
        // tables pass carryRowId=false and keep their existing relative tie order.
        var recordWidth = width + (carryRowId ? 1 : 0);

        // Fixed layout so jump targets can be computed up front. The staging block
        // r[0..recordWidth-1] holds the current full row (plus the rowid slot when carried);
        // the output block r[recordWidth..recordWidth+P-1] holds the projected result row.
        var loopStart = 3;
        var columnStart = loopStart + filterCount;
        var rowIdAddr = columnStart + width;
        var sorterInsertAddr = rowIdAddr + (carryRowId ? 1 : 0);
        var nextAddr = sorterInsertAddr + 1;
        var closeReadAddr = nextAddr + 1;
        var sortAddr = closeReadAddr + 1;
        var drainLoop = sortAddr + 1;
        var projectionStart = drainLoop + 1;
        var resultRowAddr = projectionStart + outputCount;
        var sorterNextAddr = resultRowAddr + 1;
        var drainDone = sorterNextAddr + 1;

        var stagingRange = new RegisterRange(new Register(0), recordWidth);
        var outputRange = new RegisterRange(new Register(recordWidth), outputCount);

        var instructions = new List<VdbeInstruction>(drainDone + 2)
        {
            new OpenReadCursorInstruction(cursor, tableName, width),
            new OpenSorterInstruction(sorter, comparer, recordWidth),
            new RewindCursorInstruction(cursor, new ProgramCounter(sortAddr)),
        };

        if (predicate is not null)
        {
            instructions.Add(new FilterInstruction(
                cursor,
                predicate,
                new ProgramCounter(nextAddr),
                $"skip row when WHERE is false, goto {nextAddr}"));
        }

        for (var column = 0; column < width; column++)
            instructions.Add(new ColumnInstruction(cursor, column, new Register(column)));

        if (carryRowId)
            instructions.Add(new RowIdInstruction(cursor, new Register(width)));

        instructions.Add(new SorterInsertInstruction(sorter, stagingRange));
        instructions.Add(new NextInstruction(cursor, new ProgramCounter(loopStart)));
        instructions.Add(new CloseCursorInstruction(cursor));
        instructions.Add(new SorterSortInstruction(sorter, new ProgramCounter(drainDone)));
        instructions.Add(new SorterDataInstruction(sorter, stagingRange));

        for (var output = 0; output < outputCount; output++)
        {
            var projection = projections[output];
            var destination = new Register(recordWidth + output);
            instructions.Add(projection.IsConstant
                ? new LoadConstantInstruction(destination, projection.Constant)
                : new CopyInstruction(new Register(projection.ColumnIndex), destination));
        }

        instructions.Add(new ResultRowInstruction(outputRange));
        instructions.Add(new SorterNextInstruction(sorter, new ProgramCounter(drainLoop)));
        instructions.Add(new CloseSorterInstruction(sorter));
        instructions.Add(new HaltInstruction());

        return new VdbeProgram(
            registerCount: recordWidth + outputCount,
            cursorCount: 1,
            instructions,
            sorterCount: 1);
    }
}
