using Ahtola.Core.Execution;

namespace Ahtola.Core.Compilation;

/// <summary>
/// Lowers <c>LIMIT</c>/<c>OFFSET</c> onto an already-compiled result-streaming <see cref="VdbeProgram"/>
/// by gating its result-row emissions, so the row count is bounded entirely inside the resumable state
/// machine rather than by trimming a materialized result set in the evaluator. It is a program-to-program
/// transform in the same spirit as <see cref="CompoundProgramBuilder"/>: it relocates the child program's
/// instructions (only jump targets shift, since new registers are appended after the existing ones),
/// seeds one or two counter registers in a prologue, and wraps every <c>ResultRow</c> with an
/// <see cref="OffsetGateInstruction"/> and/or <see cref="LimitGateInstruction"/> that skip and stop the
/// stream. The underlying program keeps executing its own scan/sorter/compound logic; nothing is
/// precomputed and the evaluator is never consulted.
/// </summary>
/// <remarks>
/// <para>
/// Exact semantics, matched to the tree-walking evaluator's <c>ApplyDistinctLimit</c>:
/// </para>
/// <list type="bullet">
///   <item>OFFSET is clamped to non-negative (<c>Math.Max(0, offset)</c>): a negative offset skips
///     nothing. The first <c>offset</c> emitted candidates are discarded before any row is produced.</item>
///   <item>OFFSET is applied before LIMIT: skipped rows are never counted against LIMIT.</item>
///   <item>A non-negative LIMIT emits exactly that many rows; <c>LIMIT 0</c> emits none.</item>
///   <item>A null or negative LIMIT is unbounded and is lowered by simply not emitting a limit gate.</item>
///   <item>When neither gate is needed (offset ≤ 0 and unbounded limit) the program is returned
///     unchanged — a faithful no-op lowering.</item>
/// </list>
/// <para>
/// Composition: because a shared pair of counters gates <em>every</em> row emission in the program, this
/// composes with any result-streaming program — direct table scans, sorted scans, joins, aggregations,
/// constant and source-less scalar-function projections, row-aware aggregates whose
/// <see cref="DistinctGateInstruction"/> has already skipped duplicate candidates, and <c>UNION ALL</c>
/// compounds (whose per-term <c>ResultRow</c>s share the counters, so LIMIT/OFFSET spans the concatenated
/// stream). Programs that fold de-duplication or membership testing into the emit opcode itself
/// (<see cref="DistinctResultRowInstruction"/>, <see cref="CompoundResultRowInstruction"/>,
/// <see cref="GuardedRowInstruction"/> — i.e. <c>DISTINCT</c>, <c>UNION</c>, <c>INTERSECT</c>,
/// <c>EXCEPT</c>) are split into a <see cref="RowGateInstruction"/> carrying those guards followed by a
/// plain <c>ResultRow</c>, so the counters sit between the two and observe only surviving rows. Opcodes
/// that populate a probe set without producing a row (<see cref="RowSetInsertInstruction"/>, and a
/// <see cref="GuardedRowInstruction"/> directed at a <see cref="RowSetDestination"/>) stream past the
/// gates untouched, because they are not part of the caller-visible row stream that LIMIT bounds.
/// </para>
/// <para>
/// The transform owns only control flow; it never inspects row values, so LIMIT/OFFSET semantics stay
/// independent of the SQL types flowing through the gated registers. Wiring this lowering into the SQL
/// pipeline (resolving the LIMIT/OFFSET expressions and routing eligible statements here) belongs to the
/// database layer, exactly as the scan/sorter/compound builders leave their routing to the caller.
/// </para>
/// </remarks>
public static class LimitOffsetProgramBuilder
{
    /// <summary>
    /// Wraps <paramref name="program"/> so it emits at most <paramref name="limit"/> rows after skipping
    /// the first <paramref name="offset"/> rows. Returns the program unchanged when no gating is needed.
    /// </summary>
    /// <param name="program">The result-streaming program to gate.</param>
    /// <param name="offset">The number of leading rows to skip; values ≤ 0 skip nothing.</param>
    /// <param name="limit">The maximum number of rows to emit; <see langword="null"/> or a negative value
    /// is unbounded.</param>
    public static VdbeProgram Apply(VdbeProgram program, long offset, long? limit)
    {
        ArgumentNullException.ThrowIfNull(program);

        // Clamp to the evaluator's semantics: negative offset skips nothing; null/negative limit is
        // unbounded. Only a non-negative limit gates the stream.
        var effectiveOffset = Math.Max(0L, offset);
        var needOffset = effectiveOffset > 0;
        var needLimit = limit is >= 0;

        // Neither gate needed: the program already yields exactly the right rows. Returning it unchanged
        // is a faithful no-op lowering of "no OFFSET, unbounded LIMIT".
        if (!needOffset && !needLimit)
            return program;

        var instructions = program.Instructions;
        var count = instructions.Count;

        // A pre-emit gate counts every candidate that reaches it, which equals the emitted-row count only
        // when nothing between the gate and the emission can suppress the row. Conditional emitters fold the
        // suppression test into the emit opcode itself, so they are split here into a RowGate (the guards)
        // followed by a plain ResultRow (the emission); the counters then sit between the two and see only
        // surviving rows. Reject an already-gated program rather than nest gates.
        var emitterCount = 0;
        foreach (var instruction in instructions)
        {
            switch (instruction)
            {
                case ResultRowInstruction:
                case DistinctResultRowInstruction:
                case CompoundResultRowInstruction:
                    emitterCount++;
                    break;
                case GuardedRowInstruction guarded:
                    if (guarded.Destination is ResultRowDestination)
                        emitterCount++;

                    break;
                case OffsetGateInstruction:
                case LimitGateInstruction:
                    throw new StatementCompilationException(
                        "Program is already LIMIT/OFFSET gated; apply the limit/offset lowering once.");
            }
        }

        if (emitterCount == 0)
        {
            throw new StatementCompilationException(
                "LIMIT/OFFSET lowering requires a program that emits at least one result row.");
        }

        var prologueLength = (needOffset ? 1 : 0) + (needLimit ? 1 : 0);

        // New counter registers are appended after the program's existing registers, so no register index
        // in the copied instructions changes — only jump targets shift.
        var offsetCounter = new Register(program.RegisterCount);
        var limitCounter = new Register(program.RegisterCount + (needOffset ? 1 : 0));
        var registerCount = program.RegisterCount + prologueLength;

        // blockStart[i] is the new address of old instruction i's emitted block: the instructions inserted
        // before it (a RowGate for a conditional emitter, then the offset/limit gates) followed by the
        // instruction itself. Jump targets resolve to the block start, so a branch into an emitter can never
        // bypass its guards or its gates. blockStart[count] is the lowered instruction count.
        var blockStart = new int[count + 1];
        var shift = prologueLength;
        for (var i = 0; i < count; i++)
        {
            blockStart[i] = shift + i;
            shift += LeadLength(instructions[i]);
        }

        blockStart[count] = shift + count;

        // The limit gate's done target is the program's terminating Halt (validated to be the last
        // instruction, hence never an emitter, so its block start is the Halt itself).
        var doneTarget = new ProgramCounter(blockStart[count - 1]);

        var lowered = new List<VdbeInstruction>(blockStart[count]);

        // Prologue: seed the counters. OFFSET first, so the register layout is stable when only one gate
        // is present.
        if (needOffset)
            lowered.Add(new LoadConstantInstruction(offsetCounter, SqlValue.Integer(effectiveOffset)));
        if (needLimit)
            lowered.Add(new LoadConstantInstruction(limitCounter, SqlValue.Integer(limit!.Value)));

        for (var i = 0; i < count; i++)
        {
            var instruction = instructions[i];
            var values = EmittedValues(instruction);
            if (values is null)
            {
                lowered.Add(RemapTargets(instruction, blockStart));
                continue;
            }

            // A candidate rejected by its guards skips the whole block, so it is never charged against
            // OFFSET or LIMIT — exactly how the evaluator de-duplicates before trimming.
            var reject = new ProgramCounter(blockStart[i + 1]);
            var guards = EmitterGuards(instruction);
            if (guards is not null)
                lowered.Add(new RowGateInstruction(values.Value, guards, reject));

            // Gate order matters: OFFSET first (skips to the loop-advance after this block without counting
            // against LIMIT), then LIMIT (stops the stream once the allowance is spent).
            if (needOffset)
                lowered.Add(new OffsetGateInstruction(offsetCounter, reject));
            if (needLimit)
                lowered.Add(new LimitGateInstruction(limitCounter, doneTarget));

            lowered.Add(new ResultRowInstruction(values.Value));
        }

        return new VdbeProgram(
            registerCount,
            program.CursorCount,
            lowered,
            program.SorterCount,
            program.AccumulatorCount,
            program.DistinctSetCount,
            program.ParameterSlotCount,
            windowBufferCount: program.WindowBufferCount);

        int LeadLength(VdbeInstruction instruction) =>
            EmittedValues(instruction) is null
                ? 0
                : (EmitterGuards(instruction) is null ? 0 : 1) + prologueLength;
    }

    // The registers a row-producing instruction emits, or null when the instruction produces no row (and so
    // needs no gating). RowSetInsert and a row-set-directed GuardedRow feed later terms rather than the
    // caller, so they stream past the gates untouched.
    private static RegisterRange? EmittedValues(VdbeInstruction instruction) => instruction switch
    {
        ResultRowInstruction x => x.Values,
        DistinctResultRowInstruction x => x.Values,
        CompoundResultRowInstruction x => x.Values,
        GuardedRowInstruction { Destination: ResultRowDestination } x => x.Values,
        _ => null,
    };

    // The guards folded into a conditional emitter, hoisted so they run before the counters. A plain
    // ResultRow has none, so it is gated directly.
    private static IReadOnlyList<VdbeRowGuard>? EmitterGuards(VdbeInstruction instruction) => instruction switch
    {
        DistinctResultRowInstruction x =>
            [new DistinctRowGuard(x.Equality, x.DistinctSetIndex)],
        CompoundResultRowInstruction x =>
        [
            new MembershipRowGuard(x.Equality, x.MembershipSetIndices, x.Mode),
            new DistinctRowGuard(x.Equality, x.OutputSetIndex),
        ],
        GuardedRowInstruction { Destination: ResultRowDestination } x => x.Guards,
        _ => null,
    };

    /// <summary>
    /// Wraps <paramref name="program"/> so its emitted rows are re-sorted by <paramref name="comparer"/>
    /// after the original stream is fully produced. Used when a window accumulates in PARTITION/ORDER BY
    /// order but the SELECT's top-level ORDER BY is a different key. LIMIT/OFFSET composes onto the
    /// rewritten ResultRows afterwards.
    /// </summary>
    public static VdbeProgram ApplyResultOrder(VdbeProgram program, VdbeRowComparer comparer)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(comparer);

        var original = program.Instructions;
        if (original.Count == 0 || original[^1] is not HaltInstruction)
        {
            throw new StatementCompilationException(
                "Result-order lowering requires a program that ends with Halt.");
        }

        RegisterRange? values = null;
        var emitterCount = 0;
        foreach (var instruction in original)
        {
            switch (instruction)
            {
                case ResultRowInstruction row:
                    if (values is not null && (values.Value.Start.Index != row.Values.Start.Index
                        || values.Value.Count != row.Values.Count))
                    {
                        throw new StatementCompilationException(
                            "Result-order lowering requires every ResultRow to share one register range.");
                    }

                    values = row.Values;
                    emitterCount++;
                    break;
                case DistinctResultRowInstruction:
                case CompoundResultRowInstruction:
                case OffsetGateInstruction:
                case LimitGateInstruction:
                    throw new StatementCompilationException(
                        "Result-order lowering requires a program of plain ResultRows, applied before LIMIT/OFFSET.");
            }
        }

        if (emitterCount == 0 || values is null)
        {
            throw new StatementCompilationException(
                "Result-order lowering requires a program that emits at least one result row.");
        }

        var map = new int[original.Count];
        for (var index = 0; index < original.Count; index++)
            map[index] = index + 1;

        var sorter = new Sorter(program.SorterCount);
        var record = values.Value;
        var lowered = new List<VdbeInstruction>(original.Count + 6)
        {
            new OpenSorterInstruction(sorter, comparer, record.Count),
        };

        for (var index = 0; index < original.Count; index++)
        {
            var instruction = original[index];
            if (instruction is ResultRowInstruction row)
            {
                lowered.Add(new SorterInsertInstruction(sorter, row.Values));
                continue;
            }

            if (instruction is HaltInstruction && index == original.Count - 1)
                continue;

            lowered.Add(RemapTargets(instruction, map));
        }

        var sortIndex = lowered.Count;
        lowered.Add(new SorterSortInstruction(sorter, new ProgramCounter(0)));
        var loop = lowered.Count;
        lowered.Add(new SorterDataInstruction(sorter, record));
        lowered.Add(new ResultRowInstruction(record));
        lowered.Add(new SorterNextInstruction(sorter, new ProgramCounter(loop)));
        var close = lowered.Count;
        lowered.Add(new CloseSorterInstruction(sorter));
        lowered.Add(new HaltInstruction());
        lowered[sortIndex] = new SorterSortInstruction(sorter, new ProgramCounter(close));

        return new VdbeProgram(
            program.RegisterCount,
            program.CursorCount,
            lowered,
            program.SorterCount + 1,
            program.AccumulatorCount,
            program.DistinctSetCount,
            program.ParameterSlotCount,
            program.WorkTableCount,
            program.WindowBufferCount);
    }

    /// <summary>
    /// Applies <see cref="Apply(VdbeProgram, long, long?)"/> to a compiled <see cref="CompoundTerm"/>,
    /// gating its program while preserving its cursor sources unchanged (LIMIT/OFFSET adds no cursors).
    /// This is how a <c>UNION ALL</c> compound composes with LIMIT/OFFSET.
    /// </summary>
    public static CompoundTerm Apply(CompoundTerm term, long offset, long? limit)
    {
        ArgumentNullException.ThrowIfNull(term);
        ArgumentNullException.ThrowIfNull(term.Program);
        var gated = Apply(term.Program, offset, limit);
        return ReferenceEquals(gated, term.Program) ? term : term with { Program = gated };
    }

    // Rebuilds one non-emitting instruction with its jump targets remapped through the block-start table.
    // Registers, cursors, sorters, accumulators, and distinct sets are unchanged because the transform only
    // appends new registers; opcodes without jump targets are returned as-is. The row-producing family is
    // never seen here: every emitter is rewritten by the caller into a gated block.
    private static VdbeInstruction RemapTargets(VdbeInstruction instruction, int[] blockStart)
    {
        ProgramCounter Pc(ProgramCounter counter) => new(blockStart[counter.Offset]);

        return instruction switch
        {
            RewindCursorInstruction x => new RewindCursorInstruction(x.Cursor, Pc(x.EmptyTarget)),
            FilterInstruction x => new FilterInstruction(x.Cursor, x.Predicate, Pc(x.FalseTarget), x.Description),
            FilterRowIdInstruction x => new FilterRowIdInstruction(x.Cursor, x.Predicate, Pc(x.FalseTarget), x.Description),
            FilterRegistersInstruction x => new FilterRegistersInstruction(x.Row, x.Predicate, Pc(x.FalseTarget), x.Description),
            VFilterInstruction x => new VFilterInstruction(x.Cursor, x.Plan, x.Arguments, Pc(x.EmptyTarget)),
            DistinctGateInstruction x => new DistinctGateInstruction(
                x.Values,
                x.Equality,
                x.DistinctSetIndex,
                Pc(x.DuplicateTarget)),
            RowGateInstruction x => new RowGateInstruction(x.Values, x.Guards, Pc(x.RejectTarget)),
            RowSetRewindInstruction x => new RowSetRewindInstruction(
                x.RowSetIndex,
                x.Destination,
                Pc(x.EmptyTarget)),
            RowSetNextInstruction x => new RowSetNextInstruction(
                x.RowSetIndex,
                x.Destination,
                Pc(x.LoopTarget)),
            RowSetTestInstruction x => new RowSetTestInstruction(
                x.RowSetRegister,
                Pc(x.FoundTarget),
                x.ValueRegister,
                x.Batch),
            NextInstruction x => new NextInstruction(x.Cursor, Pc(x.LoopTarget)),
            VNextInstruction x => new VNextInstruction(x.Cursor, Pc(x.LoopTarget)),
            SorterSortInstruction x => new SorterSortInstruction(x.Sorter, Pc(x.EmptyTarget)),
            SorterNextInstruction x => new SorterNextInstruction(x.Sorter, Pc(x.LoopTarget)),
            WindowBufferComputeInstruction x => new WindowBufferComputeInstruction(x.Buffer, Pc(x.EmptyTarget)),
            WindowBufferNextInstruction x => new WindowBufferNextInstruction(x.Buffer, Pc(x.LoopTarget)),
            GotoInstruction x => new GotoInstruction(Pc(x.Target)),
            JumpIfInstruction x => new JumpIfInstruction(x.Register, Pc(x.Target)),
            JumpIfNotTrueInstruction x => new JumpIfNotTrueInstruction(x.Value, Pc(x.FalseTarget)),
            IfPosInstruction x => new IfPosInstruction(x.Register, Pc(x.Target), x.DecrementBy),
            IfNegInstruction x => new IfNegInstruction(x.Register, Pc(x.Target)),
            DecrJumpZeroInstruction x => new DecrJumpZeroInstruction(x.Register, Pc(x.Target)),
            MustBeIntInstruction x => new MustBeIntInstruction(x.Register, x.Target is { } t ? Pc(t) : null),
            GosubInstruction x => new GosubInstruction(Pc(x.Target), x.ReturnRegister),
            SequenceTestInstruction x => new SequenceTestInstruction(x.Cursor, Pc(x.Target), x.ValueRegister),
            NotExistsInstruction x => new NotExistsInstruction(x.Cursor, x.RowIdRegister, Pc(x.JumpTarget), x.Description),
            FoundInstruction x => new FoundInstruction(x.Cursor, x.RowIdRegister, Pc(x.FoundTarget), x.Description),
            SoftNullInstruction
                or MemMaxInstruction
                or AddImmInstruction
                or ZeroOrNullInstruction
                or ReturnInstruction
                or BeginSubrtnInstruction
                or SequenceInstruction
                or HaltIfNullInstruction => instruction,
            SameGroupInstruction x => new SameGroupInstruction(x.CurrentKey, x.SavedKey, x.Comparer, Pc(x.SameGroupTarget)),
            ResetOnceInstruction x => new ResetOnceInstruction(Pc(x.RegionEnd)),
            OnceInstruction x => new OnceInstruction(Pc(x.ReentryTarget)),
            LoadConstantInstruction
                or LoadParameterInstruction
                or CopyInstruction
                or OpenReadCursorInstruction
                or OpenWriteCursorInstruction
                or VOpenInstruction
                or CloseCursorInstruction
                or ColumnInstruction
                or VColumnInstruction
                or RowIdInstruction
                or DeleteInstruction
                or InsertInstruction
                or UpdateInstruction
                or CommitInstruction
                or OpenSorterInstruction
                or SorterInsertInstruction
                or SorterDataInstruction
                or CloseSorterInstruction
                or OpenWindowBufferInstruction
                or WindowBufferInsertInstruction
                or WindowBufferDataInstruction
                or CloseWindowBufferInstruction
                or AggResetInstruction
                or AggStepInstruction
                or AggInverseInstruction
                or AggFinalizeInstruction
                or FunctionInstruction
                or ArithmeticInstruction
                or NumericAffinityInstruction
                or CompareInstruction
                or CastInstruction
                or ProjectRegistersInstruction
                or GroupKeyInstruction
                or RowSetInsertInstruction
                or GuardedRowInstruction
                or YieldInstruction
                or HaltInstruction
                or OpenEphemeralInstruction
                or EphemeralInsertInstruction
                or ColumnRangeInstruction
                or OpenPseudoInstruction
                or TypeCheckInstruction
                or ChangeCountInstruction
                or BlobReadInstruction
                or BlobWriteInstruction
                or BlobLenInstruction => instruction,
            _ => throw new StatementCompilationException(
                $"Cannot lower opcode {instruction.Opcode} into a LIMIT/OFFSET program."),
        };
    }
}
