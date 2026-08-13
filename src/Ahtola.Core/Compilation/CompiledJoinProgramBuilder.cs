using Ahtola.Core.Execution;

namespace Ahtola.Core.Compilation;

/// <summary>Builds SELECT pipelines over a materializing <see cref="VdbeJoinPlan"/> cursor.</summary>
internal static class CompiledJoinProgramBuilder
{
    public static VdbeProgram BuildProjection(
        VdbeJoinPlan plan,
        int outputColumnCount,
        VdbeRowTransform projection,
        VdbeRowComparer? orderComparer,
        VdbeRowEquality? distinctEquality,
        long offset,
        long? limit)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(projection);
        if (outputColumnCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(outputColumnCount));
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit is <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit));

        var cursor = new Cursor(0);
        var recordWidth = plan.RecordColumnCount;
        var recordRange = new RegisterRange(new Register(0), recordWidth);
        var outputRange = new RegisterRange(new Register(recordWidth), outputColumnCount);
        var nextRegister = checked(recordWidth + outputColumnCount);
        Register? offsetCounter = null;
        Register? limitCounter = null;
        var instructions = new List<VdbeInstruction>();

        if (offset > 0)
        {
            offsetCounter = new Register(nextRegister++);
            instructions.Add(new LoadConstantInstruction(offsetCounter.Value, SqlValue.Integer(offset)));
        }

        if (limit is { } maximum)
        {
            limitCounter = new Register(nextRegister++);
            instructions.Add(new LoadConstantInstruction(limitCounter.Value, SqlValue.Integer(maximum)));
        }

        instructions.Add(new OpenJoinCursorInstruction(cursor, plan));

        var needsProjectionBuffer = distinctEquality is not null || offsetCounter is not null || limitCounter is not null;
        var sorterCount = (orderComparer is null ? 0 : 1) + (needsProjectionBuffer ? 1 : 0);
        var orderSorter = orderComparer is null ? (Sorter?)null : new Sorter(0);
        var projectionSorter = needsProjectionBuffer
            ? new Sorter(orderSorter is null ? 0 : 1)
            : (Sorter?)null;

        if (orderSorter is { } ordered)
            instructions.Add(new OpenSorterInstruction(ordered, orderComparer!, recordWidth));
        if (projectionSorter is { } projected)
            instructions.Add(new OpenSorterInstruction(projected, StableIdentityComparer, outputColumnCount));

        var rewindIndex = instructions.Count;
        instructions.Add(new RewindCursorInstruction(cursor, new ProgramCounter(0)));
        var ingestLoop = instructions.Count;
        EmitCursorRecordRead(instructions, cursor, recordWidth);

        if (orderSorter is { } order)
        {
            instructions.Add(new SorterInsertInstruction(order, recordRange));
        }
        else
        {
            EmitProjection(instructions, recordRange, outputRange, projection);
            if (projectionSorter is { } projectionBuffer)
                instructions.Add(new SorterInsertInstruction(projectionBuffer, outputRange));
            else
                instructions.Add(new ResultRowInstruction(outputRange));
        }

        instructions.Add(new NextInstruction(cursor, new ProgramCounter(ingestLoop)));
        var closeCursorIndex = instructions.Count;
        instructions.Add(new CloseCursorInstruction(cursor));

        if (orderSorter is { } orderedSorter)
        {
            var sortIndex = instructions.Count;
            instructions.Add(new SorterSortInstruction(orderedSorter, new ProgramCounter(0)));
            var orderedDrainLoop = instructions.Count;
            instructions.Add(new SorterDataInstruction(orderedSorter, recordRange));
            EmitProjection(instructions, recordRange, outputRange, projection);
            if (projectionSorter is { } projectionBuffer)
                instructions.Add(new SorterInsertInstruction(projectionBuffer, outputRange));
            else
                instructions.Add(new ResultRowInstruction(outputRange));
            instructions.Add(new SorterNextInstruction(orderedSorter, new ProgramCounter(orderedDrainLoop)));
            var closeOrderIndex = instructions.Count;
            instructions.Add(new CloseSorterInstruction(orderedSorter));

            instructions[rewindIndex] = new RewindCursorInstruction(cursor, new ProgramCounter(closeCursorIndex));
            instructions[sortIndex] = new SorterSortInstruction(
                orderedSorter,
                new ProgramCounter(closeOrderIndex));
        }
        else
        {
            instructions[rewindIndex] = new RewindCursorInstruction(cursor, new ProgramCounter(closeCursorIndex));
        }

        if (projectionSorter is { } buffered)
        {
            EmitBufferedResults(
                instructions,
                buffered,
                outputRange,
                distinctEquality,
                offsetCounter,
                limitCounter);
        }
        else
        {
            instructions.Add(new HaltInstruction());
        }

        return new VdbeProgram(
            registerCount: nextRegister,
            cursorCount: 1,
            instructions,
            sorterCount,
            distinctSetCount: distinctEquality is null ? 0 : 1);
    }

    public static VdbeProgram BindJoinCursor(VdbeProgram program, VdbeJoinPlan plan)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(plan);
        var instructions = program.Instructions.ToArray();
        if (instructions.Length == 0
            || instructions[0] is not OpenReadCursorInstruction
            {
                Cursor.Index: 0,
                ColumnCount: var columnCount,
            }
            || columnCount != plan.RecordColumnCount)
        {
            throw new ArgumentException(
                "The source program must open cursor 0 with the join record width as its first instruction.",
                nameof(program));
        }

        instructions[0] = new OpenJoinCursorInstruction(new Cursor(0), plan);
        return new VdbeProgram(
            program.RegisterCount,
            program.CursorCount,
            instructions,
            program.SorterCount,
            program.AccumulatorCount,
            program.DistinctSetCount,
            program.ParameterSlotCount,
            program.WorkTableCount);
    }

    private static void EmitCursorRecordRead(
        List<VdbeInstruction> instructions,
        Cursor cursor,
        int recordWidth)
    {
        for (var column = 0; column < recordWidth; column++)
            instructions.Add(new ColumnInstruction(cursor, column, new Register(column)));
    }

    private static void EmitProjection(
        List<VdbeInstruction> instructions,
        RegisterRange input,
        RegisterRange output,
        VdbeRowTransform projection)
    {
        instructions.Add(new ProjectRegistersInstruction(
            input,
            output,
            projection,
            $"{FormatRange(output)}=project joined row {FormatRange(input)}"));
    }

    private static void EmitBufferedResults(
        List<VdbeInstruction> instructions,
        Sorter sorter,
        RegisterRange output,
        VdbeRowEquality? distinctEquality,
        Register? offsetCounter,
        Register? limitCounter)
    {
        var sortIndex = instructions.Count;
        instructions.Add(new SorterSortInstruction(sorter, new ProgramCounter(0)));
        var drainLoop = instructions.Count;
        instructions.Add(new SorterDataInstruction(sorter, output));

        var distinctIndex = -1;
        if (distinctEquality is not null)
        {
            distinctIndex = instructions.Count;
            instructions.Add(new DistinctFilterInstruction(
                output,
                distinctEquality,
                DistinctSetIndex: 0,
                new ProgramCounter(0)));
        }

        var offsetIndex = -1;
        if (offsetCounter is { } offset)
        {
            offsetIndex = instructions.Count;
            instructions.Add(new OffsetGateInstruction(offset, new ProgramCounter(0)));
        }

        var limitIndex = -1;
        if (limitCounter is { } limit)
        {
            limitIndex = instructions.Count;
            instructions.Add(new LimitGateInstruction(limit, new ProgramCounter(0)));
        }

        instructions.Add(new ResultRowInstruction(output));
        var nextIndex = instructions.Count;
        instructions.Add(new SorterNextInstruction(sorter, new ProgramCounter(drainLoop)));
        var closeIndex = instructions.Count;
        instructions.Add(new CloseSorterInstruction(sorter));
        instructions.Add(new HaltInstruction());

        instructions[sortIndex] = new SorterSortInstruction(sorter, new ProgramCounter(closeIndex));
        if (distinctIndex >= 0)
        {
            instructions[distinctIndex] = new DistinctFilterInstruction(
                output,
                distinctEquality!,
                DistinctSetIndex: 0,
                new ProgramCounter(nextIndex));
        }

        if (offsetIndex >= 0)
            instructions[offsetIndex] = new OffsetGateInstruction(offsetCounter!.Value, new ProgramCounter(nextIndex));
        if (limitIndex >= 0)
            instructions[limitIndex] = new LimitGateInstruction(limitCounter!.Value, new ProgramCounter(closeIndex));
    }

    private static int StableIdentityComparer(SqlValue[] left, SqlValue[] right) => 0;

    private static string FormatRange(RegisterRange range)
        => range.Count == 1
            ? $"r[{range.Start.Index}]"
            : $"r[{range.Start.Index}..{range.Start.Index + range.Count - 1}]";
}
