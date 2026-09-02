using Ahtola.Core.Execution;

namespace Ahtola.Core.Compilation;

/// <summary>
/// The kind of value an aggregate output column projects.
/// </summary>
public enum AggregateOutputKind
{
    /// <summary>A grouped column, read from the finalized group's saved group key.</summary>
    GroupKey,

    /// <summary>The finalized result of one aggregate accumulator.</summary>
    Aggregate,

    /// <summary>A folded compile-time constant.</summary>
    Constant,
}

/// <summary>
/// One output column of an aggregate result row: a grouped key column, the finalized
/// value of an aggregate, or a folded constant. Mirrors the SELECT compiler's projection
/// lowering but is expressed in primitives so the builder stays free of AST and SQL
/// semantics.
/// </summary>
public readonly record struct AggregateOutput
{
    private AggregateOutput(AggregateOutputKind kind, int index, SqlValue constant)
    {
        Kind = kind;
        Index = index;
        Constant = constant;
    }

    public AggregateOutputKind Kind { get; }

    /// <summary>The group-key ordinal (<see cref="AggregateOutputKind.GroupKey"/>) or the
    /// accumulator ordinal (<see cref="AggregateOutputKind.Aggregate"/>) this output reads.</summary>
    public int Index { get; }

    /// <summary>The value emitted for a constant output.</summary>
    public SqlValue Constant { get; }

    /// <summary>Projects the grouped value of the group-by column at <paramref name="keyIndex"/>.</summary>
    public static AggregateOutput ForGroupKey(int keyIndex)
    {
        if (keyIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(keyIndex));

        return new AggregateOutput(AggregateOutputKind.GroupKey, keyIndex, default);
    }

    /// <summary>Projects the finalized result of the aggregate at <paramref name="accumulatorIndex"/>.</summary>
    public static AggregateOutput ForAggregate(int accumulatorIndex)
    {
        if (accumulatorIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(accumulatorIndex));

        return new AggregateOutput(AggregateOutputKind.Aggregate, accumulatorIndex, default);
    }

    /// <summary>Projects a folded compile-time constant.</summary>
    public static AggregateOutput ForConstant(SqlValue value)
        => new(AggregateOutputKind.Constant, 0, value);
}

/// <summary>
/// One aggregate function of an aggregation: the <see cref="VdbeAggregate"/> supplying the
/// accumulation semantics together with the scanned-row column ordinals that feed its
/// argument tuple. An empty <see cref="ArgumentColumns"/> models a nullary aggregate such
/// as <c>COUNT(*)</c>.
/// </summary>
public sealed record AggregateFunctionSpec(
    VdbeAggregate Aggregate,
    IReadOnlyList<int> ArgumentColumns,
    VdbeRowPredicate? Filter = null,
    int NavigationOffset = 0)
{
    public int Arity => ArgumentColumns.Count;
}

/// <summary>
/// A post-aggregation filter evaluated from a materialized tuple of group-key, aggregate, or
/// constant values. It models <c>HAVING</c>: every accumulator is finalized before the predicate
/// runs, and a false predicate skips only that result row.
/// </summary>
public sealed record AggregateHavingFilter(
    IReadOnlyList<AggregateOutput> Inputs,
    VdbeRowPredicate Predicate,
    string Description);

/// <summary>
/// A row-aware aggregate finalizer whose value controls whether a finalized
/// scalar result or grouped row is emitted.
/// </summary>
public sealed record AggregateFinalizerFilter(
    VdbeAggregate Aggregate,
    VdbeRowPredicate Predicate,
    string Description);

/// <summary>
/// Lowers whole-table (scalar) and <c>GROUP BY</c> aggregations into runnable
/// <see cref="VdbeProgram"/>s built from the aggregate opcode family (<c>AggReset</c>,
/// <c>AggStep</c>, <c>AggFinalize</c>) plus, for grouping, the sorter opcodes and
/// <c>SameGroup</c>/<c>Goto</c> control flow. So aggregation runs entirely through the
/// resumable state machine rather than the tree-walking evaluator.
/// </summary>
/// <remarks>
/// The builder owns only the program's control flow and register/jump layout. Accumulation
/// semantics (<see cref="VdbeAggregate"/>), the group ordering used to make groups
/// contiguous (<see cref="VdbeRowComparer"/>), group equality (<see cref="VdbeGroupComparer"/>),
/// and the WHERE predicate (<see cref="VdbeRowPredicate"/>) are all supplied by the caller,
/// exactly as the scan and sorted-scan builders delegate their semantics. The emitted
/// program is data-free: the scanned rows are bound at execution time through a
/// <see cref="VdbeCursorSource"/>.
/// </remarks>
public static class AggregateProgramBuilder
{
    /// <summary>
    /// Builds a whole-table aggregation with no <c>GROUP BY</c>. The program scans the
    /// table, folds every row into the accumulators, and always emits exactly one result
    /// row — even over an empty table, where each aggregate finalizes its empty-input value
    /// (<c>COUNT</c> → 0, <c>SUM</c> → NULL).
    /// <code>
    ///   0            OpenReadCursor
    ///   1..N         AggReset (one per accumulator)
    ///                Rewind        -> closeAddr        (empty table)
    ///   loopStart    [Filter       -> nextAddr]        (WHERE)
    ///                [Column arg reads] AggStep         (per aggregate)
    ///   nextAddr     Next          -> loopStart
    ///   closeAddr    CloseCursor
    ///                AggFinalize (per accumulator) -> aggOut
    ///                Copy/LoadConstant per output register
    ///                [Copy HAVING inputs; FilterRegisters -> Halt]
    ///                ResultRow
    ///                Halt
    /// </code>
    /// </summary>
    public static VdbeProgram BuildScalar(
        string tableName,
        int tableColumnCount,
        IReadOnlyList<AggregateFunctionSpec> aggregates,
        IReadOnlyList<AggregateOutput> outputs,
        VdbeRowPredicate? predicate = null,
        AggregateHavingFilter? having = null)
    {
        ValidateCommon(tableName, tableColumnCount, aggregates, outputs);
        foreach (var output in outputs)
        {
            if (output.Kind == AggregateOutputKind.GroupKey)
            {
                throw new ArgumentException(
                    "A scalar aggregation has no group key to project; use BuildGrouped.",
                    nameof(outputs));
            }

            ValidateAggregateOutput(output, aggregates.Count, groupKeyCount: 0);
        }

        ValidateHaving(having, aggregates.Count, groupKeyCount: 0);

        var argOffsets = ComputeArgOffsets(aggregates, out var totalArgs);
        var argBase = 0;
        var aggOutBase = totalArgs;
        var outBase = totalArgs + aggregates.Count;
        var havingBase = outBase + outputs.Count;
        var registerCount = havingBase + (having?.Inputs.Count ?? 0);

        var cursor = new Cursor(0);
        var ins = new List<VdbeInstruction>
        {
            new OpenReadCursorInstruction(cursor, tableName, tableColumnCount),
        };

        for (var i = 0; i < aggregates.Count; i++)
            ins.Add(new AggResetInstruction(new Accumulator(i)));

        var rewindIndex = ins.Count;
        ins.Add(new RewindCursorInstruction(cursor, new ProgramCounter(0)));

        var loopStart = ins.Count;
        var filterIndex = -1;
        if (predicate is not null)
        {
            filterIndex = ins.Count;
            ins.Add(new FilterInstruction(cursor, predicate, new ProgramCounter(0), string.Empty));
        }

        EmitCursorSteps(ins, cursor, aggregates, argOffsets, argBase);

        var nextAddr = ins.Count;
        ins.Add(new NextInstruction(cursor, new ProgramCounter(loopStart)));

        var closeAddr = ins.Count;
        ins.Add(new CloseCursorInstruction(cursor));
        EmitFinalizeAndOutput(ins, aggregates, outputs, aggOutBase, outBase, savedKeyBase: 0, having, havingBase);
        ins.Add(new HaltInstruction());

        ins[rewindIndex] = new RewindCursorInstruction(cursor, new ProgramCounter(closeAddr));
        if (filterIndex >= 0)
        {
            ins[filterIndex] = new FilterInstruction(
                cursor,
                predicate!,
                new ProgramCounter(nextAddr),
                $"skip row when WHERE is false, goto {nextAddr}");
        }

        return new VdbeProgram(
            registerCount,
            cursorCount: 1,
            ins,
            sorterCount: 0,
            accumulatorCount: aggregates.Count);
    }

    /// <summary>
    /// Builds the O(1) fast path for <c>SELECT COUNT(*) FROM &lt;source&gt;</c> with no
    /// WHERE/GROUP BY/HAVING/ORDER BY/DISTINCT/LIMIT/OFFSET and no FILTER/OVER on the
    /// COUNT(*). The program opens a read cursor, loads the bound row source's row count
    /// into the single output register, emits exactly one result row, and halts — no scan
    /// loop, no accumulator. The cursor is never iterated, so the row source's indexer is
    /// never touched: a tracking source records no index access. <see cref="HaltInstruction"/>
    /// disposes the open cursor, so no explicit <see cref="CloseCursorInstruction"/> is
    /// emitted. The output register (index 0) is also the single result column.
    /// <code>
    ///   0  OpenReadCursor
    ///   1  RowCount      -> r[0] = c0.rowcount
    ///   2  ResultRow     (r[0])
    ///   3  Halt
    /// </code>
    /// <para>
    /// <paramref name="driveProgress"/>, when non-null, is attached to the <see cref="RowCountInstruction"/>
    /// so the interpreter pumps it once per counted row, keeping a registered progress handler firing at
    /// the same cadence as the scan+accumulator path. Null (the default) keeps the program O(1).
    /// </para>
    /// </summary>
    public static VdbeProgram BuildCountStar(string tableName, int tableColumnCount, Action? driveProgress = null)
    {
        var cursor = new Cursor(0);
        var output = new Register(0);
        var ins = new List<VdbeInstruction>
        {
            new OpenReadCursorInstruction(cursor, tableName, tableColumnCount),
            new RowCountInstruction(cursor, output, driveProgress),
            new ResultRowInstruction(new RegisterRange(output, 1)),
            new HaltInstruction(),
        };

        return new VdbeProgram(
            registerCount: 1,
            cursorCount: 1,
            ins,
            sorterCount: 0,
            accumulatorCount: 0);
    }

    /// <summary>
    /// Builds a <c>GROUP BY</c> aggregation. The program materializes every scanned row into
    /// a sorter ordered by <paramref name="groupOrderComparer"/> so rows of one group are
    /// contiguous, then walks the sorted rows once: it accumulates rows of the current group,
    /// detects each group boundary with <paramref name="groupComparer"/>, and finalizes and
    /// emits one result row per group. An empty table produces no rows.
    /// <code>
    ///   OpenReadCursor / OpenSorter
    ///   Rewind        -> sortAddr                     (empty table)
    ///   loopStart     [Filter] Column* SorterInsert
    ///                 Next -> loopStart / CloseCursor
    ///   sortAddr      SorterSort -> closeAddr          (empty sorter: no groups)
    ///   prime         SorterData; save key; AggReset*; AggStep*
    ///                 SorterNext -> drainLoop
    ///                 Goto       -> finalizeLast        (single-row group)
    ///   drainLoop     SorterData; load current key
    ///                 SameGroup -> sameStep             (still the same group)
    ///                 AggFinalize*; output; [FilterRegisters]; ResultRow; AggReset*; save new key
    ///   sameStep      AggStep*
    ///                 SorterNext -> drainLoop
    ///   finalizeLast  AggFinalize*; output; ResultRow  (last group)
    ///   closeAddr     CloseSorter; Halt
    /// </code>
    /// </summary>
    public static VdbeProgram BuildGrouped(
        string tableName,
        int tableColumnCount,
        IReadOnlyList<int> groupKeyColumns,
        IReadOnlyList<AggregateFunctionSpec> aggregates,
        IReadOnlyList<AggregateOutput> outputs,
        VdbeRowComparer groupOrderComparer,
        VdbeGroupComparer groupComparer,
        VdbeRowPredicate? predicate = null,
        AggregateHavingFilter? having = null)
    {
        ValidateCommon(tableName, tableColumnCount, aggregates, outputs);
        ArgumentNullException.ThrowIfNull(groupKeyColumns);
        ArgumentNullException.ThrowIfNull(groupOrderComparer);
        ArgumentNullException.ThrowIfNull(groupComparer);
        if (groupKeyColumns.Count == 0)
            throw new ArgumentException("A grouped aggregation needs at least one group-key column.", nameof(groupKeyColumns));

        foreach (var column in groupKeyColumns)
        {
            if (column < 0 || column >= tableColumnCount)
            {
                throw new ArgumentException(
                    $"Group-key column {column} is outside the {tableColumnCount}-column table.",
                    nameof(groupKeyColumns));
            }
        }

        foreach (var output in outputs)
            ValidateAggregateOutput(output, aggregates.Count, groupKeyColumns.Count);
        ValidateHaving(having, aggregates.Count, groupKeyColumns.Count);

        var group = groupKeyColumns.Count;
        var argOffsets = ComputeArgOffsets(aggregates, out var totalArgs);
        var stagingBase = 0;
        var savedKeyBase = tableColumnCount;
        var currentKeyBase = tableColumnCount + group;
        var argBase = tableColumnCount + (2 * group);
        var aggOutBase = argBase + totalArgs;
        var outBase = aggOutBase + aggregates.Count;
        var havingBase = outBase + outputs.Count;
        var registerCount = havingBase + (having?.Inputs.Count ?? 0);

        var cursor = new Cursor(0);
        var sorter = new Sorter(0);
        var stagingRange = new RegisterRange(new Register(stagingBase), tableColumnCount);
        var savedKeyRange = new RegisterRange(new Register(savedKeyBase), group);
        var currentKeyRange = new RegisterRange(new Register(currentKeyBase), group);

        var ins = new List<VdbeInstruction>
        {
            new OpenReadCursorInstruction(cursor, tableName, tableColumnCount),
            new OpenSorterInstruction(sorter, groupOrderComparer, tableColumnCount),
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

        for (var column = 0; column < tableColumnCount; column++)
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

        // Prime the first group from the first sorted row.
        ins.Add(new SorterDataInstruction(sorter, stagingRange));
        for (var j = 0; j < group; j++)
            ins.Add(new CopyInstruction(new Register(stagingBase + groupKeyColumns[j]), new Register(savedKeyBase + j)));

        for (var i = 0; i < aggregates.Count; i++)
            ins.Add(new AggResetInstruction(new Accumulator(i)));

        EmitStagingSteps(ins, aggregates, argOffsets, argBase, stagingBase);

        var primeNextIndex = ins.Count;
        ins.Add(new SorterNextInstruction(sorter, new ProgramCounter(0)));
        var primeGotoIndex = ins.Count;
        ins.Add(new GotoInstruction(new ProgramCounter(0)));

        var drainLoop = ins.Count;
        ins.Add(new SorterDataInstruction(sorter, stagingRange));
        for (var j = 0; j < group; j++)
            ins.Add(new CopyInstruction(new Register(stagingBase + groupKeyColumns[j]), new Register(currentKeyBase + j)));

        var sameGroupIndex = ins.Count;
        ins.Add(new SameGroupInstruction(currentKeyRange, savedKeyRange, groupComparer, new ProgramCounter(0)));

        // New group boundary: finalize and emit the previous group, then start a new one.
        EmitFinalizeAndOutput(ins, aggregates, outputs, aggOutBase, outBase, savedKeyBase, having, havingBase);
        for (var i = 0; i < aggregates.Count; i++)
            ins.Add(new AggResetInstruction(new Accumulator(i)));
        for (var j = 0; j < group; j++)
            ins.Add(new CopyInstruction(new Register(currentKeyBase + j), new Register(savedKeyBase + j)));

        var sameStep = ins.Count;
        EmitStagingSteps(ins, aggregates, argOffsets, argBase, stagingBase);
        ins.Add(new SorterNextInstruction(sorter, new ProgramCounter(drainLoop)));

        var finalizeLast = ins.Count;
        EmitFinalizeAndOutput(ins, aggregates, outputs, aggOutBase, outBase, savedKeyBase, having, havingBase);

        var closeAddr = ins.Count;
        ins.Add(new CloseSorterInstruction(sorter));
        ins.Add(new HaltInstruction());

        // Backpatch the forward jumps of the drain phase.
        ins[sortIndex] = new SorterSortInstruction(sorter, new ProgramCounter(closeAddr));
        ins[primeNextIndex] = new SorterNextInstruction(sorter, new ProgramCounter(drainLoop));
        ins[primeGotoIndex] = new GotoInstruction(new ProgramCounter(finalizeLast));
        ins[sameGroupIndex] = new SameGroupInstruction(
            currentKeyRange,
            savedKeyRange,
            groupComparer,
            new ProgramCounter(sameStep));

        return new VdbeProgram(
            registerCount,
            cursorCount: 1,
            ins,
            sorterCount: 1,
            accumulatorCount: aggregates.Count);
    }

    /// <summary>
    /// Builds a scalar aggregate program whose single accumulator collects each
    /// complete filtered source row. Different finalizer descriptors may then
    /// evaluate HAVING and each result expression against that shared row set.
    /// </summary>
    public static VdbeProgram BuildRowScalar(
        string tableName,
        int tableColumnCount,
        VdbeAggregate collector,
        IReadOnlyList<VdbeAggregate> outputs,
        VdbeRowPredicate? predicate = null,
        AggregateFinalizerFilter? having = null,
        VdbeRowEquality? distinctEquality = null)
    {
        ValidateRowPlan(tableName, tableColumnCount, collector, outputs, having);

        var rowBase = 0;
        var outputBase = tableColumnCount;
        var havingRegister = outputBase + outputs.Count;
        var registerCount = havingRegister + (having is null ? 0 : 1);
        var row = new RegisterRange(new Register(rowBase), tableColumnCount);
        var cursor = new Cursor(0);
        var accumulator = new Accumulator(0);
        var ins = new List<VdbeInstruction>
        {
            new OpenReadCursorInstruction(cursor, tableName, tableColumnCount),
            new AggResetInstruction(accumulator),
        };

        var rewindIndex = ins.Count;
        ins.Add(new RewindCursorInstruction(cursor, new ProgramCounter(0)));

        var loopStart = ins.Count;
        var whereIndex = -1;
        if (predicate is not null)
        {
            whereIndex = ins.Count;
            ins.Add(new FilterInstruction(cursor, predicate, new ProgramCounter(0), string.Empty));
        }

        for (var column = 0; column < tableColumnCount; column++)
            ins.Add(new ColumnInstruction(cursor, column, new Register(rowBase + column)));
        ins.Add(new AggStepInstruction(accumulator, collector, row));

        var nextAddress = ins.Count;
        ins.Add(new NextInstruction(cursor, new ProgramCounter(loopStart)));
        var closeAddress = ins.Count;
        ins.Add(new CloseCursorInstruction(cursor));

        var havingFilterIndex = -1;
        if (having is not null)
        {
            ins.Add(new AggFinalizeInstruction(
                accumulator,
                having.Aggregate,
                new Register(havingRegister)));
            havingFilterIndex = ins.Count;
            ins.Add(new FilterRegistersInstruction(
                new RegisterRange(new Register(havingRegister), 1),
                having.Predicate,
                new ProgramCounter(0),
                having.Description));
        }

        for (var index = 0; index < outputs.Count; index++)
        {
            ins.Add(new AggFinalizeInstruction(
                accumulator,
                outputs[index],
                new Register(outputBase + index)));
        }

        var output = new RegisterRange(new Register(outputBase), outputs.Count);
        var distinctGateIndex = -1;
        if (distinctEquality is not null)
        {
            distinctGateIndex = ins.Count;
            ins.Add(new DistinctGateInstruction(
                output,
                distinctEquality,
                DistinctSetIndex: 0,
                DuplicateTarget: new ProgramCounter(0)));
        }

        ins.Add(new ResultRowInstruction(output));
        var haltAddress = ins.Count;
        ins.Add(new HaltInstruction());

        ins[rewindIndex] = new RewindCursorInstruction(cursor, new ProgramCounter(closeAddress));
        if (whereIndex >= 0)
        {
            ins[whereIndex] = new FilterInstruction(
                cursor,
                predicate!,
                new ProgramCounter(nextAddress),
                $"skip row when WHERE is false, goto {nextAddress}");
        }

        if (havingFilterIndex >= 0)
        {
            ins[havingFilterIndex] = new FilterRegistersInstruction(
                new RegisterRange(new Register(havingRegister), 1),
                having!.Predicate,
                new ProgramCounter(haltAddress),
                having.Description);
        }
        if (distinctGateIndex >= 0)
        {
            ins[distinctGateIndex] = new DistinctGateInstruction(
                output,
                distinctEquality!,
                DistinctSetIndex: 0,
                DuplicateTarget: new ProgramCounter(haltAddress));
        }

        return new VdbeProgram(
            registerCount,
            cursorCount: 1,
            ins,
            accumulatorCount: 1,
            distinctSetCount: distinctEquality is null ? 0 : 1);
    }

    /// <summary>
    /// Builds a row-aware GROUP BY program. Computed keys are projected once in
    /// filtered source order and assigned a stable first-seen group id. The first
    /// sorter orders staged rows by <paramref name="groupKeyOrder"/> (ascending key
    /// order, matching SQLite's aggregation sorter, so groups drain in key order);
    /// the second buffers finalized result records so HAVING, ORDER BY, and result
    /// DISTINCT occur after every group has been aggregated. Each output record
    /// carries the group's key between the ORDER BY keys and the HAVING flag, and
    /// <paramref name="outputOrderComparer"/> is expected to break ORDER BY ties on
    /// those key columns so a statement without ORDER BY emits groups in key order.
    /// </summary>
    public static VdbeProgram BuildRowGrouped(
        string tableName,
        int tableColumnCount,
        int groupKeyCount,
        VdbeGroupKeyProjector groupKeyProjector,
        VdbeGroupComparer groupEquality,
        VdbeRowComparer groupKeyOrder,
        VdbeAggregate collector,
        IReadOnlyList<VdbeAggregate> outputs,
        IReadOnlyList<VdbeAggregate> orderKeys,
        VdbeRowComparer outputOrderComparer,
        VdbeRowPredicate? predicate = null,
        AggregateFinalizerFilter? having = null,
        VdbeRowEquality? distinctEquality = null,
        VdbeGroupHasher? groupHasher = null)
    {
        ValidateRowPlan(tableName, tableColumnCount, collector, outputs, having);
        ArgumentNullException.ThrowIfNull(groupKeyProjector);
        ArgumentNullException.ThrowIfNull(groupEquality);
        ArgumentNullException.ThrowIfNull(groupKeyOrder);
        ArgumentNullException.ThrowIfNull(orderKeys);
        ArgumentNullException.ThrowIfNull(outputOrderComparer);
        if (groupKeyCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(groupKeyCount));
        foreach (var orderKey in orderKeys)
            ValidateAggregate(orderKey, nameof(orderKeys));

        // Source sorter record: [group key | row columns | group id]. Staging the key
        // lets the source sorter order by key without re-projecting per comparison.
        var keyBase = 0;
        var rowBase = keyBase + groupKeyCount;
        var groupIdRegister = rowBase + tableColumnCount;
        var sourceRecordCount = groupKeyCount + tableColumnCount + 1;
        var savedGroupIdRegister = sourceRecordCount;
        var savedKeyBase = savedGroupIdRegister + 1;
        var outputRecordBase = savedKeyBase + groupKeyCount;
        var orderBase = outputRecordBase;
        var outputKeyBase = orderBase + orderKeys.Count;
        var havingBase = outputKeyBase + groupKeyCount;
        var outputBase = havingBase + 1;
        var outputRecordCount = orderKeys.Count + groupKeyCount + 1 + outputs.Count;
        var registerCount = outputRecordBase + outputRecordCount;

        var cursor = new Cursor(0);
        var sourceSorter = new Sorter(0);
        var outputSorter = new Sorter(1);
        var accumulator = new Accumulator(0);
        var row = new RegisterRange(new Register(rowBase), tableColumnCount);
        var groupKey = new RegisterRange(new Register(keyBase), groupKeyCount);
        var savedKey = new RegisterRange(new Register(savedKeyBase), groupKeyCount);
        var sourceRecord = new RegisterRange(new Register(keyBase), sourceRecordCount);
        var savedGroup = new RegisterRange(new Register(savedGroupIdRegister), 1);
        var currentGroup = new RegisterRange(new Register(groupIdRegister), 1);
        var outputRecord = new RegisterRange(new Register(outputRecordBase), outputRecordCount);
        var output = new RegisterRange(new Register(outputBase), outputs.Count);
        var emitPredicate = having?.Predicate
            ?? (static values => values[0].AsInteger() != 0);
        var emitDescription = having?.Description ?? "emit finalized group";

        // Equal keys are one group, so a 0 from the key comparer needs no tie-break:
        // the stable sorter keeps scan order inside each group.
        VdbeRowComparer sourceOrder = groupKeyOrder;

        var ins = new List<VdbeInstruction>
        {
            new OpenReadCursorInstruction(cursor, tableName, tableColumnCount),
            new OpenSorterInstruction(sourceSorter, sourceOrder, sourceRecordCount),
            new OpenSorterInstruction(outputSorter, outputOrderComparer, outputRecordCount),
        };

        var rewindIndex = ins.Count;
        ins.Add(new RewindCursorInstruction(cursor, new ProgramCounter(0)));

        var ingestLoop = ins.Count;
        var whereIndex = -1;
        if (predicate is not null)
        {
            whereIndex = ins.Count;
            ins.Add(new FilterInstruction(cursor, predicate, new ProgramCounter(0), string.Empty));
        }

        for (var column = 0; column < tableColumnCount; column++)
            ins.Add(new ColumnInstruction(cursor, column, new Register(rowBase + column)));
        ins.Add(new GroupKeyInstruction(
            row,
            new Register(groupIdRegister),
            groupKeyCount,
            groupKeyProjector,
            groupEquality,
            GroupSetIndex: 0,
            Hasher: groupHasher,
            KeyOutput: groupKey));
        ins.Add(new SorterInsertInstruction(sourceSorter, sourceRecord));

        var nextIngestAddress = ins.Count;
        ins.Add(new NextInstruction(cursor, new ProgramCounter(ingestLoop)));
        var closeCursorAddress = ins.Count;
        ins.Add(new CloseCursorInstruction(cursor));

        var sourceSortIndex = ins.Count;
        ins.Add(new SorterSortInstruction(sourceSorter, new ProgramCounter(0)));
        ins.Add(new SorterDataInstruction(sourceSorter, sourceRecord));
        ins.Add(new CopyInstruction(new Register(groupIdRegister), new Register(savedGroupIdRegister)));
        for (var index = 0; index < groupKeyCount; index++)
        {
            ins.Add(new CopyInstruction(
                new Register(keyBase + index),
                new Register(savedKeyBase + index)));
        }

        ins.Add(new AggResetInstruction(accumulator));
        ins.Add(new AggStepInstruction(accumulator, collector, row));

        var primeNextIndex = ins.Count;
        ins.Add(new SorterNextInstruction(sourceSorter, new ProgramCounter(0)));
        var primeGotoIndex = ins.Count;
        ins.Add(new GotoInstruction(new ProgramCounter(0)));

        var drainLoop = ins.Count;
        ins.Add(new SorterDataInstruction(sourceSorter, sourceRecord));
        var sameGroupIndex = ins.Count;
        ins.Add(new SameGroupInstruction(
            currentGroup,
            savedGroup,
            static (left, right) => left[0].AsInteger() == right[0].AsInteger(),
            new ProgramCounter(0)));

        EmitRowGroupFinalization(
            ins,
            accumulator,
            outputs,
            orderKeys,
            outputSorter,
            outputRecord,
            outputBase,
            orderBase,
            havingBase,
            outputKeyBase,
            savedKey,
            having);
        ins.Add(new AggResetInstruction(accumulator));
        ins.Add(new CopyInstruction(new Register(groupIdRegister), new Register(savedGroupIdRegister)));

        var sameGroupStep = ins.Count;
        ins.Add(new AggStepInstruction(accumulator, collector, row));
        // Track the key of the row just folded into the group so the finalization at
        // the next group boundary can stamp the finished group's key onto its record.
        for (var index = 0; index < groupKeyCount; index++)
        {
            ins.Add(new CopyInstruction(
                new Register(keyBase + index),
                new Register(savedKeyBase + index)));
        }

        ins.Add(new SorterNextInstruction(sourceSorter, new ProgramCounter(drainLoop)));

        var finalizeLast = ins.Count;
        EmitRowGroupFinalization(
            ins,
            accumulator,
            outputs,
            orderKeys,
            outputSorter,
            outputRecord,
            outputBase,
            orderBase,
            havingBase,
            outputKeyBase,
            savedKey,
            having);

        var closeSourceAddress = ins.Count;
        ins.Add(new CloseSorterInstruction(sourceSorter));
        var outputSortIndex = ins.Count;
        ins.Add(new SorterSortInstruction(outputSorter, new ProgramCounter(0)));

        var outputLoop = ins.Count;
        ins.Add(new SorterDataInstruction(outputSorter, outputRecord));
        var havingFilterIndex = ins.Count;
        ins.Add(new FilterRegistersInstruction(
            new RegisterRange(new Register(havingBase), 1),
            emitPredicate,
            new ProgramCounter(0),
            emitDescription));
        var distinctGateIndex = -1;
        if (distinctEquality is not null)
        {
            distinctGateIndex = ins.Count;
            ins.Add(new DistinctGateInstruction(
                output,
                distinctEquality,
                DistinctSetIndex: 1,
                DuplicateTarget: new ProgramCounter(0)));
        }

        ins.Add(new ResultRowInstruction(output));
        var nextOutputAddress = ins.Count;
        ins.Add(new SorterNextInstruction(outputSorter, new ProgramCounter(outputLoop)));

        var closeOutputAddress = ins.Count;
        ins.Add(new CloseSorterInstruction(outputSorter));
        ins.Add(new HaltInstruction());

        ins[rewindIndex] = new RewindCursorInstruction(cursor, new ProgramCounter(closeCursorAddress));
        if (whereIndex >= 0)
        {
            ins[whereIndex] = new FilterInstruction(
                cursor,
                predicate!,
                new ProgramCounter(nextIngestAddress),
                $"skip row when WHERE is false, goto {nextIngestAddress}");
        }

        ins[sourceSortIndex] = new SorterSortInstruction(sourceSorter, new ProgramCounter(closeSourceAddress));
        ins[primeNextIndex] = new SorterNextInstruction(sourceSorter, new ProgramCounter(drainLoop));
        ins[primeGotoIndex] = new GotoInstruction(new ProgramCounter(finalizeLast));
        ins[sameGroupIndex] = new SameGroupInstruction(
            currentGroup,
            savedGroup,
            static (left, right) => left[0].AsInteger() == right[0].AsInteger(),
            new ProgramCounter(sameGroupStep));
        ins[outputSortIndex] = new SorterSortInstruction(outputSorter, new ProgramCounter(closeOutputAddress));
        ins[havingFilterIndex] = new FilterRegistersInstruction(
            new RegisterRange(new Register(havingBase), 1),
            emitPredicate,
            new ProgramCounter(nextOutputAddress),
            emitDescription);
        if (distinctGateIndex >= 0)
        {
            ins[distinctGateIndex] = new DistinctGateInstruction(
                output,
                distinctEquality!,
                DistinctSetIndex: 1,
                DuplicateTarget: new ProgramCounter(nextOutputAddress));
        }

        return new VdbeProgram(
            registerCount,
            cursorCount: 1,
            ins,
            sorterCount: 2,
            accumulatorCount: 1,
            distinctSetCount: distinctEquality is null ? 1 : 2);
    }

    private static void EmitRowGroupFinalization(
        List<VdbeInstruction> ins,
        Accumulator accumulator,
        IReadOnlyList<VdbeAggregate> outputs,
        IReadOnlyList<VdbeAggregate> orderKeys,
        Sorter outputSorter,
        RegisterRange outputRecord,
        int outputBase,
        int orderBase,
        int havingBase,
        int outputKeyBase,
        RegisterRange savedKey,
        AggregateFinalizerFilter? having)
    {
        for (var index = 0; index < outputs.Count; index++)
        {
            ins.Add(new AggFinalizeInstruction(
                accumulator,
                outputs[index],
                new Register(outputBase + index)));
        }

        if (having is null)
            ins.Add(new LoadConstantInstruction(new Register(havingBase), SqlValue.Integer(1)));
        else
            ins.Add(new AggFinalizeInstruction(accumulator, having.Aggregate, new Register(havingBase)));

        for (var index = 0; index < orderKeys.Count; index++)
        {
            ins.Add(new AggFinalizeInstruction(
                accumulator,
                orderKeys[index],
                new Register(orderBase + index)));
        }

        for (var index = 0; index < savedKey.Count; index++)
        {
            ins.Add(new CopyInstruction(
                new Register(savedKey.Start.Index + index),
                new Register(outputKeyBase + index)));
        }

        ins.Add(new SorterInsertInstruction(outputSorter, outputRecord));
    }

    private static void ValidateRowPlan(
        string tableName,
        int tableColumnCount,
        VdbeAggregate collector,
        IReadOnlyList<VdbeAggregate> outputs,
        AggregateFinalizerFilter? having)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(outputs);
        if (tableColumnCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(tableColumnCount));
        if (outputs.Count == 0)
            throw new ArgumentException("An aggregation must project at least one output column.", nameof(outputs));

        ValidateAggregate(collector, nameof(collector));
        foreach (var output in outputs)
            ValidateAggregate(output, nameof(outputs));
        if (having is not null)
        {
            ValidateAggregate(having.Aggregate, nameof(having));
            ArgumentNullException.ThrowIfNull(having.Predicate);
            ArgumentNullException.ThrowIfNull(having.Description);
        }
    }

    private static void ValidateAggregate(VdbeAggregate aggregate, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(aggregate, parameterName);
        if (string.IsNullOrEmpty(aggregate.Name))
            throw new ArgumentException("Aggregate descriptors must have a name.", parameterName);
        ArgumentNullException.ThrowIfNull(aggregate.CreateContext, parameterName);
        ArgumentNullException.ThrowIfNull(aggregate.Accumulate, parameterName);
        ArgumentNullException.ThrowIfNull(aggregate.Finalize, parameterName);
    }

    // Steps every aggregate from the live cursor row: gathers each aggregate's argument
    // columns into its contiguous argument block, then folds the block into its accumulator.
    private static void EmitCursorSteps(
        List<VdbeInstruction> ins,
        Cursor cursor,
        IReadOnlyList<AggregateFunctionSpec> aggregates,
        int[] argOffsets,
        int argBase)
    {
        for (var i = 0; i < aggregates.Count; i++)
        {
            var spec = aggregates[i];
            for (var k = 0; k < spec.Arity; k++)
                ins.Add(new ColumnInstruction(cursor, spec.ArgumentColumns[k], new Register(argBase + argOffsets[i] + k)));

            ins.Add(new AggStepInstruction(
                new Accumulator(i),
                spec.Aggregate,
                new RegisterRange(new Register(argBase + argOffsets[i]), spec.Arity)));
        }
    }

    // Steps every aggregate from the materialized staging row: gathers each aggregate's
    // argument columns out of staging into its argument block, then folds the block in.
    private static void EmitStagingSteps(
        List<VdbeInstruction> ins,
        IReadOnlyList<AggregateFunctionSpec> aggregates,
        int[] argOffsets,
        int argBase,
        int stagingBase)
    {
        for (var i = 0; i < aggregates.Count; i++)
        {
            var spec = aggregates[i];
            for (var k = 0; k < spec.Arity; k++)
                ins.Add(new CopyInstruction(new Register(stagingBase + spec.ArgumentColumns[k]), new Register(argBase + argOffsets[i] + k)));

            ins.Add(new AggStepInstruction(
                new Accumulator(i),
                spec.Aggregate,
                new RegisterRange(new Register(argBase + argOffsets[i]), spec.Arity)));
        }
    }

    // Finalizes every accumulator into its output register, builds the result row into the
    // output block, and emits it. Group-key outputs read the saved (finalizing) group key.
    private static void EmitFinalizeAndOutput(
        List<VdbeInstruction> ins,
        IReadOnlyList<AggregateFunctionSpec> aggregates,
        IReadOnlyList<AggregateOutput> outputs,
        int aggOutBase,
        int outBase,
        int savedKeyBase,
        AggregateHavingFilter? having,
        int havingBase)
    {
        for (var i = 0; i < aggregates.Count; i++)
        {
            ins.Add(new AggFinalizeInstruction(
                new Accumulator(i),
                aggregates[i].Aggregate,
                new Register(aggOutBase + i)));
        }

        for (var o = 0; o < outputs.Count; o++)
        {
            var output = outputs[o];
            var destination = new Register(outBase + o);
            ins.Add(EmitOutput(output, destination, aggOutBase, savedKeyBase));
        }

        if (having is not null)
        {
            for (var input = 0; input < having.Inputs.Count; input++)
            {
                ins.Add(EmitOutput(
                    having.Inputs[input],
                    new Register(havingBase + input),
                    aggOutBase,
                    savedKeyBase));
            }

            var filterAddress = ins.Count;
            ins.Add(new FilterRegistersInstruction(
                new RegisterRange(new Register(havingBase), having.Inputs.Count),
                having.Predicate,
                new ProgramCounter(filterAddress + 2),
                having.Description));
        }

        ins.Add(new ResultRowInstruction(new RegisterRange(new Register(outBase), outputs.Count)));
    }

    private static VdbeInstruction EmitOutput(
        AggregateOutput output,
        Register destination,
        int aggOutBase,
        int savedKeyBase)
    {
        return output.Kind switch
        {
            AggregateOutputKind.GroupKey => new CopyInstruction(new Register(savedKeyBase + output.Index), destination),
            AggregateOutputKind.Aggregate => new CopyInstruction(new Register(aggOutBase + output.Index), destination),
            AggregateOutputKind.Constant => new LoadConstantInstruction(destination, output.Constant),
            _ => throw new ArgumentOutOfRangeException(nameof(output), "Unknown aggregate output kind."),
        };
    }

    private static int[] ComputeArgOffsets(IReadOnlyList<AggregateFunctionSpec> aggregates, out int totalArgs)
    {
        var offsets = new int[aggregates.Count];
        var running = 0;
        for (var i = 0; i < aggregates.Count; i++)
        {
            offsets[i] = running;
            running += aggregates[i].Arity;
        }

        totalArgs = running;
        return offsets;
    }

    private static void ValidateCommon(
        string tableName,
        int tableColumnCount,
        IReadOnlyList<AggregateFunctionSpec> aggregates,
        IReadOnlyList<AggregateOutput> outputs)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(aggregates);
        ArgumentNullException.ThrowIfNull(outputs);
        if (tableColumnCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(tableColumnCount), "An aggregation needs at least one column.");
        if (aggregates.Count == 0)
            throw new ArgumentException("An aggregation must declare at least one aggregate.", nameof(aggregates));
        if (outputs.Count == 0)
            throw new ArgumentException("An aggregation must project at least one output column.", nameof(outputs));

        foreach (var spec in aggregates)
        {
            if (spec is null)
                throw new ArgumentException("Aggregate specifications must not be null.", nameof(aggregates));
            if (spec.Aggregate is null)
                throw new ArgumentException("Aggregate specifications must supply an aggregate.", nameof(aggregates));
            ArgumentNullException.ThrowIfNull(spec.ArgumentColumns);

            foreach (var column in spec.ArgumentColumns)
            {
                if (column < 0 || column >= tableColumnCount)
                {
                    throw new ArgumentException(
                        $"Aggregate argument column {column} is outside the {tableColumnCount}-column table.",
                        nameof(aggregates));
                }
            }
        }
    }

    private static void ValidateAggregateOutput(AggregateOutput output, int aggregateCount, int groupKeyCount)
    {
        switch (output.Kind)
        {
            case AggregateOutputKind.GroupKey when output.Index >= groupKeyCount:
                throw new ArgumentException(
                    $"Output projects group key {output.Index}, but the aggregation groups on {groupKeyCount} columns.",
                    nameof(output));
            case AggregateOutputKind.Aggregate when output.Index >= aggregateCount:
                throw new ArgumentException(
                    $"Output projects aggregate {output.Index}, but the aggregation declares {aggregateCount} aggregates.",
                    nameof(output));
            default:
                break;
        }
    }

    private static void ValidateHaving(AggregateHavingFilter? having, int aggregateCount, int groupKeyCount)
    {
        if (having is null)
            return;

        ArgumentNullException.ThrowIfNull(having.Inputs);
        ArgumentNullException.ThrowIfNull(having.Predicate);
        ArgumentNullException.ThrowIfNull(having.Description);
        foreach (var input in having.Inputs)
            ValidateAggregateOutput(input, aggregateCount, groupKeyCount);
    }
}
