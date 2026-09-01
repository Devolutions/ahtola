using Ahtola.Core.Execution;

namespace Ahtola.Core.Compilation;

/// <summary>The frame kind a window's rows are drawn from. Only <see cref="Rows"/> is
/// representable by <see cref="WindowProgramBuilder"/>; <see cref="Range"/> and
/// <see cref="Groups"/> are declared so the builder can reject them explicitly rather than
/// silently producing peer-inclusive results it does not model.</summary>
public enum WindowFrameMode
{
    /// <summary>Physical <c>ROWS</c> framing: each row is an independent frame position,
    /// so ties in the ORDER BY key are not grouped into peers.</summary>
    Rows,

    /// <summary>Logical <c>RANGE</c> framing (peer-inclusive). Not modeled.</summary>
    Range,

    /// <summary>Peer-group <c>GROUPS</c> framing. Not modeled.</summary>
    Groups,
}

/// <summary>One boundary of a window frame. Mirrors the five SQL frame bounds so an
/// unsupported bound can be named and rejected instead of misinterpreted.</summary>
public enum WindowBound
{
    UnboundedPreceding,
    Preceding,
    CurrentRow,
    Following,
    UnboundedFollowing,
}

/// <summary>
/// The frame a window function is evaluated over. <see cref="WindowProgramBuilder"/> models the running
/// prefix (<see cref="Running"/>) and the exact current row (<see cref="CurrentRow"/>). Other frames are
/// rejected because they need retained departing rows or forward-looking input that this streaming builder
/// does not model.
/// </summary>
/// <remarks>
/// This is a VDBE-lowering primitive, distinct from the evaluator's SQL-level frame AST: it exists so the
/// builder's caller can state the frame it lowered and the builder can honestly reject frames it does not
/// implement (RANGE/GROUPS framing, bounded or forward-looking ROWS bounds, EXCLUDE clauses). A window's
/// ORDER BY and PARTITION BY are supplied separately through the comparer delegates.
/// </remarks>
public readonly record struct WindowFrameSpec(WindowFrameMode Mode, WindowBound Start, WindowBound End)
{
    /// <summary>A running frame: <c>ROWS UNBOUNDED PRECEDING TO CURRENT ROW</c>.</summary>
    public static WindowFrameSpec Running => new(WindowFrameMode.Rows, WindowBound.UnboundedPreceding, WindowBound.CurrentRow);

    /// <summary>A one-row moving frame: <c>ROWS CURRENT ROW TO CURRENT ROW</c>.</summary>
    public static WindowFrameSpec CurrentRow => new(WindowFrameMode.Rows, WindowBound.CurrentRow, WindowBound.CurrentRow);

    /// <summary>Whether this frame is the running-rows frame.</summary>
    public bool IsRunning => Mode == WindowFrameMode.Rows
        && Start == WindowBound.UnboundedPreceding
        && End == WindowBound.CurrentRow;

    /// <summary>Whether this frame contains exactly the current physical row.</summary>
    public bool IsCurrentRow => Mode == WindowFrameMode.Rows
        && Start == WindowBound.CurrentRow
        && End == WindowBound.CurrentRow;

    /// <summary>Whether this frame can be lowered by the streaming builder.</summary>
    public bool IsSupported => IsRunning || IsCurrentRow;
}

/// <summary>The kind of value a window result column projects.</summary>
public enum WindowOutputKind
{
    /// <summary>A pass-through column of the current (sorted) row, e.g. a partition or order column.</summary>
    Column,

    /// <summary>The finalized value of one window function at the current row.</summary>
    Window,

    /// <summary>A folded compile-time constant.</summary>
    Constant,
}

/// <summary>
/// One output column of a window result row: a pass-through scanned column of the current row, the
/// finalized value of one window function at that row, or a folded constant. Mirrors the aggregate and
/// sorted-scan output primitives so the builder stays free of AST and SQL semantics.
/// </summary>
public readonly record struct WindowOutput
{
    private WindowOutput(WindowOutputKind kind, int index, SqlValue constant)
    {
        Kind = kind;
        Index = index;
        Constant = constant;
    }

    public WindowOutputKind Kind { get; }

    /// <summary>The scanned-column ordinal (<see cref="WindowOutputKind.Column"/>) or the window-function
    /// ordinal (<see cref="WindowOutputKind.Window"/>) this output reads.</summary>
    public int Index { get; }

    /// <summary>The value emitted for a constant output.</summary>
    public SqlValue Constant { get; }

    /// <summary>Projects the current row's value of the scanned column at <paramref name="columnIndex"/>.</summary>
    public static WindowOutput ForColumn(int columnIndex)
    {
        if (columnIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(columnIndex));

        return new WindowOutput(WindowOutputKind.Column, columnIndex, default);
    }

    /// <summary>Projects the finalized value of the window function at <paramref name="windowIndex"/>.</summary>
    public static WindowOutput ForWindow(int windowIndex)
    {
        if (windowIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(windowIndex));

        return new WindowOutput(WindowOutputKind.Window, windowIndex, default);
    }

    /// <summary>Projects a folded compile-time constant.</summary>
    public static WindowOutput ForConstant(SqlValue value) => new(WindowOutputKind.Constant, 0, value);
}

/// <summary>
/// Lowers a partitioned streaming aggregate window into a runnable <see cref="VdbeProgram"/> built from the
/// sorter and aggregate opcode families. The program materializes every scanned row into a sorter ordered
/// by <c>(PARTITION BY keys, ORDER BY keys)</c> so each partition is a contiguous, in-order run, then walks
/// the sorted rows once: it resets the accumulators at each partition boundary, folds the current row into
/// them, finalizes them, and emits one result row per input row. So a supported window
/// (<c>func(...) OVER (PARTITION BY ... ORDER BY ...)</c>) runs entirely through the resumable state machine
/// rather than the tree-walking evaluator, with no precomputed output.
/// </summary>
/// <remarks>
/// <para>
/// The builder owns only the program's control flow and register/jump layout. Every SQL semantic is supplied
/// by the caller through the same delegate contracts the aggregate and sorted-scan builders use: the
/// per-function accumulation semantics (<see cref="VdbeAggregate"/>), the <c>(partition, order)</c> ordering
/// that makes partitions contiguous and rows within a partition window-ordered (<see cref="VdbeRowComparer"/>),
/// the partition-key equality used to detect partition boundaries (<see cref="VdbeGroupComparer"/>), and the
/// optional WHERE predicate (<see cref="VdbeRowPredicate"/>). The emitted program is data-free: the scanned
/// rows are bound at execution time through a <see cref="VdbeCursorSource"/>.
/// </para>
/// <para>
/// The running frame (<see cref="WindowFrameSpec.Running"/>) folds every partition row up to and including
/// the current row, restarting per partition. <c>row_number()</c> is expressed as a running
/// <c>count(*)</c> (a nullary window function), and running <c>sum</c>/<c>count</c>/<c>avg</c>/<c>min</c>/<c>max</c>
/// follow from the corresponding accumulators. Because the finalize step runs once per row against the
/// still-open accumulator, each window function's <see cref="VdbeAggregate.Finalize"/> must be side-effect
/// free (as the standard aggregates are). The exact-current-row frame steps, finalizes, emits, and then
/// applies <c>AggInverse</c> for that same row, so its aggregates must supply
/// <see cref="VdbeAggregate.Inverse"/>. Any other frame is rejected.
/// </para>
/// <code>
///   0            OpenReadCursor
///   1            OpenSorter                                  (comparer orders by partition then order keys)
///   2            Rewind        -> sortAddr                   (empty table)
///   loopStart    [Filter       -> nextIngest]               (WHERE)
///                Column c0.i -> r[i]                         (materialize full row: i in 0..W-1)
///                SorterInsert  r[0..W-1]
///   nextIngest   Next          -> loopStart
///                CloseCursor
///   sortAddr     SorterSort    -> doneAddr                   (empty sorter: no rows)
///   prime        SorterData    -> r[0..W-1]
///                [Copy partition keys -> savedKey]           (when PARTITION BY present)
///                AggReset (per window)
///                Goto          -> emit
///   drainLoop    SorterData    -> r[0..W-1]
///                [Copy partition keys -> currentKey
///                 SameGroup currentKey==savedKey -> emit     (same partition: keep accumulating)
///                 AggReset (per window)                      (new partition: restart)
///                 Copy currentKey -> savedKey]
///   emit         [Copy args] AggStep; AggFinalize -> aggOut  (per window)
///                Copy/LoadConstant per output register
///                ResultRow
///                [AggInverse]                               (exact-current-row frame only)
///                SorterNext    -> drainLoop
///   doneAddr     CloseSorter
///                Halt
/// </code>
/// </remarks>
public static class WindowProgramBuilder
{
    public static VdbeProgram Build(
        string tableName,
        int tableColumnCount,
        IReadOnlyList<int> partitionColumns,
        IReadOnlyList<AggregateFunctionSpec> windows,
        IReadOnlyList<WindowOutput> outputs,
        VdbeRowComparer orderComparer,
        VdbeGroupComparer? partitionComparer = null,
        VdbeRowPredicate? predicate = null,
        WindowFrameSpec? frame = null)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(partitionColumns);
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(orderComparer);

        var effectiveFrame = frame ?? WindowFrameSpec.Running;
        if (!effectiveFrame.IsSupported)
        {
            throw new ArgumentException(
                $"WindowProgramBuilder only models ROWS UNBOUNDED PRECEDING TO CURRENT ROW and ROWS CURRENT ROW TO CURRENT ROW; " +
                $"frame ({effectiveFrame.Mode}, {effectiveFrame.Start}, {effectiveFrame.End}) is not representable.",
                nameof(frame));
        }

        if (tableColumnCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(tableColumnCount), "A window scan needs at least one column.");
        if (windows.Count == 0)
            throw new ArgumentException("A window scan must declare at least one window function.", nameof(windows));
        if (outputs.Count == 0)
            throw new ArgumentException("A window scan must project at least one output column.", nameof(outputs));

        foreach (var spec in windows)
        {
            if (spec is null)
                throw new ArgumentException("Window function specifications must not be null.", nameof(windows));
            if (spec.Aggregate is null)
                throw new ArgumentException("Window function specifications must supply an aggregate.", nameof(windows));
            ArgumentNullException.ThrowIfNull(spec.ArgumentColumns);
            if (effectiveFrame.IsCurrentRow && spec.Aggregate.Inverse is null)
            {
                throw new ArgumentException(
                    "ROWS CURRENT ROW TO CURRENT ROW requires every window aggregate to supply an inverse delegate.",
                    nameof(windows));
            }

            foreach (var column in spec.ArgumentColumns)
            {
                if (column < 0 || column >= tableColumnCount)
                {
                    throw new ArgumentException(
                        $"Window argument column {column} is outside the {tableColumnCount}-column table.",
                        nameof(windows));
                }
            }
        }

        foreach (var column in partitionColumns)
        {
            if (column < 0 || column >= tableColumnCount)
            {
                throw new ArgumentException(
                    $"Partition column {column} is outside the {tableColumnCount}-column table.",
                    nameof(partitionColumns));
            }
        }

        if (partitionColumns.Count > 0 && partitionComparer is null)
        {
            throw new ArgumentException(
                "A partitioned window needs a partition comparer to detect partition boundaries.",
                nameof(partitionComparer));
        }

        foreach (var output in outputs)
            ValidateOutput(output, tableColumnCount, windows.Count);

        return BuildProgram(
            tableName,
            tableColumnCount,
            partitionColumns,
            windows,
            outputs,
            orderComparer,
            partitionComparer,
            predicate,
            effectiveFrame);
    }

    private static VdbeProgram BuildProgram(
        string tableName,
        int tableColumnCount,
        IReadOnlyList<int> partitionColumns,
        IReadOnlyList<AggregateFunctionSpec> windows,
        IReadOnlyList<WindowOutput> outputs,
        VdbeRowComparer orderComparer,
        VdbeGroupComparer? partitionComparer,
        VdbeRowPredicate? predicate,
        WindowFrameSpec frame)
    {
        var width = tableColumnCount;
        var partition = partitionColumns.Count;
        var argOffsets = ComputeArgOffsets(windows, out var totalArgs);

        // Register layout mirrors the grouped-aggregate builder: the full sorted row stages at r[0..W-1],
        // followed by the saved and current partition keys, the per-function argument blocks, the finalized
        // window values, and finally the projected output block.
        var stagingBase = 0;
        var savedKeyBase = width;
        var currentKeyBase = width + partition;
        var argBase = width + (2 * partition);
        var aggOutBase = argBase + totalArgs;
        var outBase = aggOutBase + windows.Count;
        var registerCount = outBase + outputs.Count;

        var cursor = new Cursor(0);
        var sorter = new Sorter(0);
        var stagingRange = new RegisterRange(new Register(stagingBase), width);
        var savedKeyRange = new RegisterRange(new Register(savedKeyBase), partition);
        var currentKeyRange = new RegisterRange(new Register(currentKeyBase), partition);

        var ins = new List<VdbeInstruction>
        {
            new OpenReadCursorInstruction(cursor, tableName, width),
            new OpenSorterInstruction(sorter, orderComparer, width),
        };

        var rewindIndex = ins.Count;
        ins.Add(new RewindCursorInstruction(cursor, new ProgramCounter(0)));

        var loopStart = ins.Count;
        var filterIndex = -1;
        if (predicate is not null)
        {
            filterIndex = ins.Count;
            ins.Add(new FilterInstruction(cursor, predicate, new ProgramCounter(0), string.Empty));
        }

        for (var column = 0; column < width; column++)
            ins.Add(new ColumnInstruction(cursor, column, new Register(stagingBase + column)));

        ins.Add(new SorterInsertInstruction(sorter, stagingRange));

        var nextIngestAddr = ins.Count;
        ins.Add(new NextInstruction(cursor, new ProgramCounter(loopStart)));
        ins.Add(new CloseCursorInstruction(cursor));

        var sortIndex = ins.Count;
        ins.Add(new SorterSortInstruction(sorter, new ProgramCounter(0)));

        // Backpatch the ingest-phase jumps now that their targets are known.
        ins[rewindIndex] = new RewindCursorInstruction(cursor, new ProgramCounter(sortIndex));
        if (filterIndex >= 0)
        {
            ins[filterIndex] = new FilterInstruction(
                cursor,
                predicate!,
                new ProgramCounter(nextIngestAddr),
                $"skip row when WHERE is false, goto {nextIngestAddr}");
        }

        // Prime the first partition from the first sorted row, then jump into the shared emit block so the
        // first row also produces a result.
        ins.Add(new SorterDataInstruction(sorter, stagingRange));
        for (var j = 0; j < partition; j++)
            ins.Add(new CopyInstruction(new Register(stagingBase + partitionColumns[j]), new Register(savedKeyBase + j)));

        for (var i = 0; i < windows.Count; i++)
            ins.Add(new AggResetInstruction(new Accumulator(i)));

        var primeGotoIndex = ins.Count;
        ins.Add(new GotoInstruction(new ProgramCounter(0)));

        var drainLoop = ins.Count;
        ins.Add(new SorterDataInstruction(sorter, stagingRange));

        var sameGroupIndex = -1;
        if (partition > 0)
        {
            for (var j = 0; j < partition; j++)
                ins.Add(new CopyInstruction(new Register(stagingBase + partitionColumns[j]), new Register(currentKeyBase + j)));

            sameGroupIndex = ins.Count;
            ins.Add(new SameGroupInstruction(currentKeyRange, savedKeyRange, partitionComparer!, new ProgramCounter(0)));

            // New partition boundary: restart the accumulators and adopt the new key, then fall into emit.
            for (var i = 0; i < windows.Count; i++)
                ins.Add(new AggResetInstruction(new Accumulator(i)));
            for (var j = 0; j < partition; j++)
                ins.Add(new CopyInstruction(new Register(currentKeyBase + j), new Register(savedKeyBase + j)));
        }

        var emit = ins.Count;
        EmitWindowSteps(ins, windows, argOffsets, argBase, stagingBase);
        for (var i = 0; i < windows.Count; i++)
        {
            ins.Add(new AggFinalizeInstruction(
                new Accumulator(i),
                windows[i].Aggregate,
                new Register(aggOutBase + i)));
        }

        for (var o = 0; o < outputs.Count; o++)
        {
            var output = outputs[o];
            var destination = new Register(outBase + o);
            ins.Add(output.Kind switch
            {
                WindowOutputKind.Column => new CopyInstruction(new Register(stagingBase + output.Index), destination),
                WindowOutputKind.Window => new CopyInstruction(new Register(aggOutBase + output.Index), destination),
                _ => new LoadConstantInstruction(destination, output.Constant),
            });
        }

        ins.Add(new ResultRowInstruction(new RegisterRange(new Register(outBase), outputs.Count)));
        if (frame.IsCurrentRow)
        {
            for (var i = 0; i < windows.Count; i++)
            {
                ins.Add(new AggInverseInstruction(
                    new Accumulator(i),
                    windows[i].Aggregate,
                    new RegisterRange(new Register(argBase + argOffsets[i]), windows[i].Arity)));
            }
        }

        ins.Add(new SorterNextInstruction(sorter, new ProgramCounter(drainLoop)));

        var doneAddr = ins.Count;
        ins.Add(new CloseSorterInstruction(sorter));
        ins.Add(new HaltInstruction());

        // Backpatch the forward jumps of the drain phase.
        ins[sortIndex] = new SorterSortInstruction(sorter, new ProgramCounter(doneAddr));
        ins[primeGotoIndex] = new GotoInstruction(new ProgramCounter(emit));
        if (sameGroupIndex >= 0)
        {
            ins[sameGroupIndex] = new SameGroupInstruction(
                currentKeyRange,
                savedKeyRange,
                partitionComparer!,
                new ProgramCounter(emit));
        }

        return new VdbeProgram(
            registerCount,
            cursorCount: 1,
            ins,
            sorterCount: 1,
            accumulatorCount: windows.Count);
    }

    // Steps every window function from the materialized staging row: gathers each function's argument
    // columns out of staging into its argument block, then folds the block into its accumulator. A nullary
    // function such as the count(*) behind row_number() steps a zero-width range.
    private static void EmitWindowSteps(
        List<VdbeInstruction> ins,
        IReadOnlyList<AggregateFunctionSpec> windows,
        int[] argOffsets,
        int argBase,
        int stagingBase)
    {
        for (var i = 0; i < windows.Count; i++)
        {
            var spec = windows[i];
            for (var k = 0; k < spec.Arity; k++)
                ins.Add(new CopyInstruction(new Register(stagingBase + spec.ArgumentColumns[k]), new Register(argBase + argOffsets[i] + k)));

            ins.Add(new AggStepInstruction(
                new Accumulator(i),
                spec.Aggregate,
                new RegisterRange(new Register(argBase + argOffsets[i]), spec.Arity)));
        }
    }

    private static int[] ComputeArgOffsets(IReadOnlyList<AggregateFunctionSpec> windows, out int totalArgs)
    {
        var offsets = new int[windows.Count];
        var running = 0;
        for (var i = 0; i < windows.Count; i++)
        {
            offsets[i] = running;
            running += windows[i].Arity;
        }

        totalArgs = running;
        return offsets;
    }

    private static void ValidateOutput(WindowOutput output, int tableColumnCount, int windowCount)
    {
        switch (output.Kind)
        {
            case WindowOutputKind.Column when output.Index >= tableColumnCount:
                throw new ArgumentException(
                    $"Output projects column {output.Index}, but the table has {tableColumnCount} columns.",
                    nameof(output));
            case WindowOutputKind.Window when output.Index >= windowCount:
                throw new ArgumentException(
                    $"Output projects window {output.Index}, but the scan declares {windowCount} window functions.",
                    nameof(output));
            default:
                break;
        }
    }
}
