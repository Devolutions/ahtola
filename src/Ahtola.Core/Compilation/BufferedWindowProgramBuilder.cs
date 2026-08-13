using Ahtola.Core.Execution;

namespace Ahtola.Core.Compilation;

/// <summary>The kind of value a buffered-window result column projects.</summary>
public enum BufferedWindowOutputKind
{
    /// <summary>A pass-through column of the current buffered row.</summary>
    Column,

    /// <summary>One window function's value at the current row.</summary>
    Window,

    /// <summary>A folded compile-time constant.</summary>
    Constant,

    /// <summary>An expression computed from the current row and its window values.</summary>
    Computed,
}

/// <summary>
/// One output column of a buffered-window result row: a pass-through scanned column, one window
/// function's value at that row, a folded constant, or an expression computed from the whole
/// <c>(scanned columns, window values)</c> record. It mirrors the aggregate and running-window output
/// primitives so the builder stays free of AST and SQL semantics: a computed output carries only a
/// <see cref="VdbeScalarFunction"/>, which the caller builds from the evaluator's own expression
/// evaluation.
/// </summary>
public readonly record struct BufferedWindowOutput
{
    private BufferedWindowOutput(
        BufferedWindowOutputKind kind,
        int index,
        SqlValue constant,
        VdbeScalarFunction? function)
    {
        Kind = kind;
        Index = index;
        Constant = constant;
        Function = function;
    }

    public BufferedWindowOutputKind Kind { get; }

    /// <summary>The scanned-column ordinal (<see cref="BufferedWindowOutputKind.Column"/>) or the
    /// window-function ordinal (<see cref="BufferedWindowOutputKind.Window"/>) this output reads.</summary>
    public int Index { get; }

    /// <summary>The value emitted for a constant output.</summary>
    public SqlValue Constant { get; }

    /// <summary>The projection applied to the whole <c>(row, window values)</c> record for a computed
    /// output.</summary>
    public VdbeScalarFunction? Function { get; }

    /// <summary>Projects the current row's value of the scanned column at <paramref name="columnIndex"/>.</summary>
    public static BufferedWindowOutput ForColumn(int columnIndex)
    {
        if (columnIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(columnIndex));

        return new BufferedWindowOutput(BufferedWindowOutputKind.Column, columnIndex, default, null);
    }

    /// <summary>Projects the value of the window function at <paramref name="windowIndex"/>.</summary>
    public static BufferedWindowOutput ForWindow(int windowIndex)
    {
        if (windowIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(windowIndex));

        return new BufferedWindowOutput(BufferedWindowOutputKind.Window, windowIndex, default, null);
    }

    /// <summary>Projects a folded compile-time constant.</summary>
    public static BufferedWindowOutput ForConstant(SqlValue value)
        => new(BufferedWindowOutputKind.Constant, 0, value, null);

    /// <summary>Projects <paramref name="function"/> applied to the full <c>(row, window values)</c>
    /// record, which is how an expression over window results (<c>sum(v) OVER (...) * 2</c>,
    /// <c>CASE WHEN row_number() OVER (...) = 1 THEN ...</c>) is lowered.</summary>
    public static BufferedWindowOutput ForComputed(VdbeScalarFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return new BufferedWindowOutput(BufferedWindowOutputKind.Computed, 0, default, function);
    }
}

/// <summary>
/// Lowers a windowed <c>SELECT</c> over one base table into a runnable <see cref="VdbeProgram"/> built on
/// the buffered-window opcode family. The program scans the table, applies the optional <c>WHERE</c>, and
/// buffers every surviving row into a window buffer in scan order; a single
/// <see cref="WindowBufferComputeInstruction"/> then computes every window function's value for every
/// buffered row; and the drain phase walks the buffer (optionally through a sorter that applies the
/// statement's <c>ORDER BY</c>), projects each output column, and emits one <c>ResultRow</c> per row. So a
/// windowed SELECT runs entirely through the resumable state machine, with no precomputed output.
/// </summary>
/// <remarks>
/// <para>
/// Where <see cref="WindowProgramBuilder"/> models the single streaming frame
/// (<c>ROWS UNBOUNDED PRECEDING TO CURRENT ROW</c>) as an accumulate-then-finalize-per-row fold, this
/// builder makes no assumption about the frame at all: it materializes the partition input and defers
/// every window semantic — partitioning, per-partition ordering, peer groups, ROWS/RANGE/GROUPS frame
/// resolution, <c>EXCLUDE</c>, <c>FILTER</c>, and the ranking/navigation/aggregate function families — to
/// the caller-supplied <see cref="VdbeWindowEvaluator"/>, exactly as the scan family defers predicates to
/// <see cref="VdbeRowPredicate"/> and the sorter family defers ordering to <see cref="VdbeRowComparer"/>.
/// That is what makes forward-looking and peer-relative frames representable: they are not expressible as
/// a streaming fold, so the buffer is the primitive that makes them exact.
/// </para>
/// <para>
/// The builder owns only control flow and register/jump layout. Output ordering is either the buffer's
/// insertion order (scan order, when the statement has no <c>ORDER BY</c>) or the order a supplied
/// <see cref="VdbeRowComparer"/> imposes on the <c>(scanned columns, window values)</c> record, which is
/// how an <c>ORDER BY</c> that reads a window result is lowered. LIMIT/OFFSET is not this builder's
/// concern: the emitted program ends in unconditional <c>ResultRow</c> opcodes, so
/// <see cref="LimitOffsetProgramBuilder"/> composes onto it unchanged.
/// </para>
/// <code>
///   0            OpenReadCursor c0
///   1            OpenWindowBuffer b0                          (W cols, K windows, evaluator)
///   2            [OpenSorter s0]                              (only when ORDER BY is present)
///   3            Rewind        -> computeAddr                 (empty table)
///   ingest       [Filter       -> nextIngest]                 (WHERE)
///                Column c0.i -> r[i]                          (materialize full row: i in 0..W-1)
///                WindowBufferInsert r[0..W-1]
///   nextIngest   Next          -> ingest
///                CloseCursor
///   computeAddr  WindowBufferCompute -> doneAddr              (empty buffer: no rows)
///   gather       [WindowBufferData -> r[0..W+K-1]
///                 SorterInsert  r[0..W+K-1]
///                 WindowBufferNext -> gather
///                 SorterSort    -> doneAddr]
///   drain        SorterData/WindowBufferData -> r[0..W+K-1]
///                Copy/LoadConstant/Function per output register
///                ResultRow
///                SorterNext/WindowBufferNext -> drain
///   doneAddr     [CloseSorter]
///                CloseWindowBuffer
///                Halt
/// </code>
/// </remarks>
public static class BufferedWindowProgramBuilder
{
    /// <summary>
    /// Builds the buffered-window program for a scan of <paramref name="tableName"/>.
    /// </summary>
    /// <param name="tableName">The scanned base table.</param>
    /// <param name="tableColumnCount">The scanned table's column count.</param>
    /// <param name="windowCount">The number of window functions the evaluator computes per row.</param>
    /// <param name="outputs">The projected result columns.</param>
    /// <param name="windowEvaluator">Computes every window value for every buffered row.</param>
    /// <param name="orderComparer">Orders the <c>(row, window values)</c> records for the statement's
    /// ORDER BY, or <see langword="null"/> to emit in scan order.</param>
    /// <param name="predicate">The optional WHERE filter applied during ingest.</param>
    public static VdbeProgram Build(
        string tableName,
        int tableColumnCount,
        int windowCount,
        IReadOnlyList<BufferedWindowOutput> outputs,
        VdbeWindowEvaluator windowEvaluator,
        VdbeRowComparer? orderComparer = null,
        VdbeRowPredicate? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(windowEvaluator);

        if (tableColumnCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(tableColumnCount), "A window scan needs at least one column.");
        if (windowCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(windowCount), "A window scan must declare at least one window function.");
        if (outputs.Count == 0)
            throw new ArgumentException("A window scan must project at least one output column.", nameof(outputs));

        foreach (var output in outputs)
            ValidateOutput(output, tableColumnCount, windowCount);

        var width = tableColumnCount;
        var recordWidth = width + windowCount;

        // Register layout: the (row, window values) record stages at r[0..recordWidth-1] and the projected
        // output block follows it, so a computed output can read the whole record as one argument range.
        var stagingBase = 0;
        var outBase = recordWidth;
        var registerCount = outBase + outputs.Count;

        var cursor = new Cursor(0);
        var buffer = new WindowBuffer(0);
        var sorter = new Sorter(0);
        var rowRange = new RegisterRange(new Register(stagingBase), width);
        var recordRange = new RegisterRange(new Register(stagingBase), recordWidth);
        var ordered = orderComparer is not null;

        var ins = new List<VdbeInstruction>
        {
            new OpenReadCursorInstruction(cursor, tableName, width),
            new OpenWindowBufferInstruction(buffer, width, windowCount, windowEvaluator),
        };

        if (ordered)
            ins.Add(new OpenSorterInstruction(sorter, orderComparer!, recordWidth));

        var rewindIndex = ins.Count;
        ins.Add(new RewindCursorInstruction(cursor, new ProgramCounter(0)));

        var ingest = ins.Count;
        var filterIndex = -1;
        if (predicate is not null)
        {
            filterIndex = ins.Count;
            ins.Add(new FilterInstruction(cursor, predicate, new ProgramCounter(0), string.Empty));
        }

        for (var column = 0; column < width; column++)
            ins.Add(new ColumnInstruction(cursor, column, new Register(stagingBase + column)));

        ins.Add(new WindowBufferInsertInstruction(buffer, rowRange));

        var nextIngest = ins.Count;
        ins.Add(new NextInstruction(cursor, new ProgramCounter(ingest)));
        ins.Add(new CloseCursorInstruction(cursor));

        var computeIndex = ins.Count;
        ins.Add(new WindowBufferComputeInstruction(buffer, new ProgramCounter(0)));

        // Backpatch the ingest-phase jumps now that their targets are known.
        ins[rewindIndex] = new RewindCursorInstruction(cursor, new ProgramCounter(computeIndex));
        if (filterIndex >= 0)
        {
            ins[filterIndex] = new FilterInstruction(
                cursor,
                predicate!,
                new ProgramCounter(nextIngest),
                $"skip row when WHERE is false, goto {nextIngest}");
        }

        var sortIndex = -1;
        if (ordered)
        {
            // Gather phase: move every computed record into the sorter so the statement's ORDER BY —
            // which may read a window result — decides the emission order.
            var gather = ins.Count;
            ins.Add(new WindowBufferDataInstruction(buffer, recordRange));
            ins.Add(new SorterInsertInstruction(sorter, recordRange));
            ins.Add(new WindowBufferNextInstruction(buffer, new ProgramCounter(gather)));
            sortIndex = ins.Count;
            ins.Add(new SorterSortInstruction(sorter, new ProgramCounter(0)));
        }

        var drain = ins.Count;
        ins.Add(ordered
            ? new SorterDataInstruction(sorter, recordRange)
            : new WindowBufferDataInstruction(buffer, recordRange));

        for (var index = 0; index < outputs.Count; index++)
        {
            var output = outputs[index];
            var destination = new Register(outBase + index);
            ins.Add(output.Kind switch
            {
                BufferedWindowOutputKind.Column
                    => new CopyInstruction(new Register(stagingBase + output.Index), destination),
                BufferedWindowOutputKind.Window
                    => new CopyInstruction(new Register(stagingBase + width + output.Index), destination),
                BufferedWindowOutputKind.Constant
                    => new LoadConstantInstruction(destination, output.Constant),
                _ => new FunctionInstruction(destination, output.Function!, recordRange),
            });
        }

        ins.Add(new ResultRowInstruction(new RegisterRange(new Register(outBase), outputs.Count)));
        ins.Add(ordered
            ? new SorterNextInstruction(sorter, new ProgramCounter(drain))
            : new WindowBufferNextInstruction(buffer, new ProgramCounter(drain)));

        var doneAddr = ins.Count;
        if (ordered)
            ins.Add(new CloseSorterInstruction(sorter));
        ins.Add(new CloseWindowBufferInstruction(buffer));
        ins.Add(new HaltInstruction());

        // Backpatch the forward jumps of the drain phase.
        ins[computeIndex] = new WindowBufferComputeInstruction(buffer, new ProgramCounter(doneAddr));
        if (sortIndex >= 0)
            ins[sortIndex] = new SorterSortInstruction(sorter, new ProgramCounter(doneAddr));

        return new VdbeProgram(
            registerCount,
            cursorCount: 1,
            ins,
            sorterCount: ordered ? 1 : 0,
            windowBufferCount: 1);
    }

    private static void ValidateOutput(BufferedWindowOutput output, int tableColumnCount, int windowCount)
    {
        switch (output.Kind)
        {
            case BufferedWindowOutputKind.Column when output.Index >= tableColumnCount:
                throw new ArgumentException(
                    $"Output projects column {output.Index}, but the table has {tableColumnCount} columns.",
                    nameof(output));
            case BufferedWindowOutputKind.Window when output.Index >= windowCount:
                throw new ArgumentException(
                    $"Output projects window {output.Index}, but the scan declares {windowCount} window functions.",
                    nameof(output));
            case BufferedWindowOutputKind.Computed when output.Function is null:
                throw new ArgumentException(
                    "A computed output must supply a projection function.",
                    nameof(output));
            default:
                break;
        }
    }
}
