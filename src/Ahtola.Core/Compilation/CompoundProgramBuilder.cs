using Ahtola.Core.Execution;

namespace Ahtola.Core.Compilation;

/// <summary>
/// One term of a compound SELECT: a compiled child <see cref="VdbeProgram"/> together with the live
/// row sources its read cursors iterate at execution time. A term is any program that streams its
/// result through <c>ResultRow</c> (a constant projection, a table scan, a sorted scan, a join, or an
/// aggregation) — <see cref="CompoundProgramBuilder"/> sequences these streams without knowing how
/// each term was built, exactly as the tree-walking evaluator sequences its per-term result sets.
/// </summary>
/// <param name="Program">The compiled child program.</param>
/// <param name="CursorSources">
/// The child's read-cursor row sources, one per cursor, in cursor-index order. Its length must equal
/// <see cref="VdbeProgram.CursorCount"/>; a term with no cursors (e.g. a constant projection) supplies
/// an empty list.
/// </param>
public sealed record CompoundTerm(VdbeProgram Program, IReadOnlyList<VdbeCursorSource> CursorSources);

/// <summary>
/// Sequences the result streams of two or more compiled child programs into one runnable
/// <see cref="VdbeProgram"/>, lowering compound SELECT execution — <c>UNION ALL</c>,
/// <c>UNION</c>/<c>DISTINCT</c>, <c>INTERSECT</c>, and <c>EXCEPT</c> — onto the resumable state machine
/// rather than a tree-walking evaluator or an AST-only wrapper. <c>UNION</c> variants run each term to
/// exhaustion in order, emitting its rows, then fall through to the next; set operations run every term
/// in source order into row sets, then iterate the first set against the remaining membership sets.
/// </summary>
/// <remarks>
/// The builder owns only the mechanical splice: it relocates each term's registers, cursors, sorters,
/// accumulators, distinct sets, parameter slots, and jump targets into disjoint ranges, drops every
/// non-final term's trailing <c>Halt</c> so control falls through to the next term, concatenates the
/// terms' cursor sources, and validates that every term projects the same number of result columns.
/// <para>
/// Row-value semantics stay with the caller, exactly as the scan, join, sorted-scan, and aggregate
/// builders delegate theirs: <c>UNION</c>/<c>DISTINCT</c> de-duplication is driven by a caller-supplied
/// <see cref="VdbeRowEquality"/> so the emitted program matches the evaluator's row-equality contract
/// (NULL==NULL together with affinity- and collation-aware comparison) rather than re-deriving it here.
/// </para>
/// <para>
/// <c>UNION ALL</c> preserves internal grouping, de-duplication, and membership state by relocating
/// each term's sets intact. <see cref="BuildUnionDistinct"/> appends its outer distinct guard after
/// each term's existing output guards, retaining nested semantics.
/// </para>
/// </remarks>
public static class CompoundProgramBuilder
{
    /// <summary>
    /// Sequences <paramref name="terms"/> with <c>UNION ALL</c> semantics: every row of every term is
    /// emitted, in term order, with no de-duplication. Any internal de-duplication a term performs is
    /// preserved. Requires at least two terms, all projecting the same number of columns.
    /// </summary>
    public static CompoundTerm BuildUnionAll(IReadOnlyList<CompoundTerm> terms)
        => Build(terms, distinctEquality: null);

    /// <summary>
    /// Sequences <paramref name="terms"/> with <c>UNION</c>/<c>DISTINCT</c> semantics. De-duplication
    /// uses <paramref name="rowEquality"/>, so the caller owns the exact row-equality contract. When
    /// <paramref name="outputComparer"/> is supplied, the distinct rows are materialized and emitted in
    /// key order, matching SQLite's temporary B-tree traversal; otherwise they retain arrival order for
    /// generic callers that do not provide SQL ordering semantics. Requires at least two terms, all
    /// projecting the same number of columns.
    /// </summary>
    public static CompoundTerm BuildUnionDistinct(
        IReadOnlyList<CompoundTerm> terms,
        VdbeRowEquality rowEquality,
        VdbeRowComparer? outputComparer = null)
    {
        ArgumentNullException.ThrowIfNull(rowEquality);
        return outputComparer is null
            ? Build(terms, rowEquality)
            : BuildSetOperation(terms, rowEquality, mode: null, outputComparer);
    }

    /// <summary>
    /// Combines <paramref name="terms"/> with <c>INTERSECT</c> semantics. Membership and de-duplication
    /// use <paramref name="rowEquality"/>, so the caller owns the exact row-equality contract. When
    /// <paramref name="outputComparer"/> is supplied, surviving rows are emitted in temporary B-tree key
    /// order; otherwise they retain first-term order. Requires at least two terms, all projecting the same
    /// number of columns.
    /// </summary>
    public static CompoundTerm BuildIntersect(
        IReadOnlyList<CompoundTerm> terms,
        VdbeRowEquality rowEquality,
        VdbeRowComparer? outputComparer = null)
    {
        ArgumentNullException.ThrowIfNull(rowEquality);
        return BuildSetOperation(terms, rowEquality, CompoundMembershipMode.PresentInAll, outputComparer);
    }

    /// <summary>
    /// Combines <paramref name="terms"/> with left-associative <c>EXCEPT</c> semantics. Membership and
    /// de-duplication use <paramref name="rowEquality"/>, so the caller owns the exact row-equality
    /// contract. When <paramref name="outputComparer"/> is supplied, surviving rows are emitted in
    /// temporary B-tree key order; otherwise they retain first-term order. Requires at least two terms,
    /// all projecting the same number of columns.
    /// </summary>
    public static CompoundTerm BuildExcept(
        IReadOnlyList<CompoundTerm> terms,
        VdbeRowEquality rowEquality,
        VdbeRowComparer? outputComparer = null)
    {
        ArgumentNullException.ThrowIfNull(rowEquality);
        return BuildSetOperation(terms, rowEquality, CompoundMembershipMode.AbsentFromAll, outputComparer);
    }

    private static CompoundTerm Build(IReadOnlyList<CompoundTerm> terms, VdbeRowEquality? distinctEquality)
    {
        ArgumentNullException.ThrowIfNull(terms);
        if (terms.Count < 2)
            throw new ArgumentException("A compound select needs at least two terms.", nameof(terms));

        var count = terms.Count;
        var columnCount = -1;
        for (var i = 0; i < count; i++)
        {
            var term = terms[i]
                ?? throw new ArgumentException($"Compound term {i} must not be null.", nameof(terms));
            ArgumentNullException.ThrowIfNull(term.Program);
            ArgumentNullException.ThrowIfNull(term.CursorSources);
            if (term.CursorSources.Count != term.Program.CursorCount)
            {
                throw new ArgumentException(
                    $"Compound term {i} supplies {term.CursorSources.Count} cursor sources for a {term.Program.CursorCount}-cursor program.",
                    nameof(terms));
            }

            var termColumns = ResultColumnCount(term.Program, i);
            if (columnCount < 0)
                columnCount = termColumns;
            else if (columnCount != termColumns)
            {
                throw new ArgumentException(
                    $"SELECTs to the left and right of a compound operator do not have the same number of result columns ({columnCount} vs {termColumns}).",
                    nameof(terms));
            }
        }

        // Lay out each term's resources in disjoint ranges. Registers, cursors, sorters, accumulators,
        // and distinct sets are relocated by the running totals of the preceding terms; jump targets by
        // the running count of emitted instructions. Every term except the last drops its trailing Halt
        // so control falls through to the next term's first instruction.
        var registerBase = new int[count];
        var cursorBase = new int[count];
        var sorterBase = new int[count];
        var accumulatorBase = new int[count];
        var distinctBase = new int[count];
        var instructionBase = new int[count];
        var parameterSlotBase = new int[count];
        var hashTableBase = new int[count];

        var totalRegisters = 0;
        var totalCursors = 0;
        var totalSorters = 0;
        var totalAccumulators = 0;
        var totalDistinctSets = 0;
        var totalInstructions = 0;
        var totalParameterSlots = 0;
        var totalHashTables = 0;
        for (var i = 0; i < count; i++)
        {
            var program = terms[i].Program;
            registerBase[i] = totalRegisters;
            cursorBase[i] = totalCursors;
            sorterBase[i] = totalSorters;
            accumulatorBase[i] = totalAccumulators;
            distinctBase[i] = totalDistinctSets;
            instructionBase[i] = totalInstructions;
            parameterSlotBase[i] = totalParameterSlots;
            hashTableBase[i] = totalHashTables;

            totalRegisters += program.RegisterCount;
            totalCursors += program.CursorCount;
            totalSorters += program.SorterCount;
            totalAccumulators += program.AccumulatorCount;
            totalDistinctSets += program.DistinctSetCount;
            totalInstructions += KeptInstructionCount(program, isLast: i == count - 1)
                + (i == count - 1 ? 0 : program.AccumulatorCount);
            totalParameterSlots += program.ParameterSlotCount;
            totalHashTables += program.HashTableCount;
        }

        // The outer distinct set (for BuildUnionDistinct) is allocated after every term's own sets; the
        // pre-flight validation guarantees terms carry none, so this is set 0 in practice.
        var outerDistinctSet = totalDistinctSets;
        var combinedDistinctSets = distinctEquality is null ? totalDistinctSets : totalDistinctSets + 1;

        var instructions = new List<VdbeInstruction>(totalInstructions);
        var cursorSources = new List<VdbeCursorSource>(totalCursors);
        for (var i = 0; i < count; i++)
        {
            var term = terms[i];
            var program = term.Program;
            var kept = KeptInstructionCount(program, isLast: i == count - 1);
            for (var j = 0; j < kept; j++)
            {
                instructions.Add(Relocate(
                    program.Instructions[j],
                    registerBase[i],
                    cursorBase[i],
                    sorterBase[i],
                    accumulatorBase[i],
                    distinctBase[i],
                    instructionBase[i],
                    parameterSlotBase[i],
                    hashTableBase[i],
                    distinctEquality,
                    outerDistinctSet));
            }
            if (i != count - 1)
            {
                for (var accumulator = 0; accumulator < program.AccumulatorCount; accumulator++)
                {
                    instructions.Add(new AggResetInstruction(
                        new Accumulator(accumulatorBase[i] + accumulator)));
                }
            }

            cursorSources.AddRange(term.CursorSources);
        }

        var combined = new VdbeProgram(
            totalRegisters,
            totalCursors,
            instructions,
            totalSorters,
            totalAccumulators,
            combinedDistinctSets,
            totalParameterSlots,
            hashTableCount: totalHashTables);
        return new CompoundTerm(combined, cursorSources);
    }

    // Lowers a homogeneous set-operation chain without reordering its inputs. Every term runs in SQL
    // source order, then the output pass traverses the materialized distinct set. UNION captures every
    // term into one set; INTERSECT and EXCEPT capture their term sets separately for membership tests.
    private static CompoundTerm BuildSetOperation(
        IReadOnlyList<CompoundTerm> terms,
        VdbeRowEquality rowEquality,
        CompoundMembershipMode? mode,
        VdbeRowComparer? outputComparer)
    {
        ArgumentNullException.ThrowIfNull(terms);
        if (terms.Count < 2)
            throw new ArgumentException("A compound select needs at least two terms.", nameof(terms));

        var count = terms.Count;
        var columnCount = -1;
        for (var i = 0; i < count; i++)
        {
            var term = terms[i]
                ?? throw new ArgumentException($"Compound term {i} must not be null.", nameof(terms));
            ArgumentNullException.ThrowIfNull(term.Program);
            ArgumentNullException.ThrowIfNull(term.CursorSources);
            if (term.CursorSources.Count != term.Program.CursorCount)
            {
                throw new ArgumentException(
                    $"Compound term {i} supplies {term.CursorSources.Count} cursor sources for a {term.Program.CursorCount}-cursor program.",
                    nameof(terms));
            }

            var termColumns = ResultColumnCount(term.Program, i);
            if (columnCount < 0)
                columnCount = termColumns;
            else if (columnCount != termColumns)
            {
                throw new ArgumentException(
                    $"SELECTs to the left and right of a compound operator do not have the same number of result columns ({columnCount} vs {termColumns}).",
                    nameof(terms));
            }
        }

        // Each term retains its own resources, including nested row sets, in source order.
        var registerBase = new int[count];
        var cursorBase = new int[count];
        var sorterBase = new int[count];
        var accumulatorBase = new int[count];
        var distinctBase = new int[count];
        var instructionBase = new int[count];
        var parameterSlotBase = new int[count];
        var hashTableBase = new int[count];

        var totalRegisters = 0;
        var totalCursors = 0;
        var totalSorters = 0;
        var totalAccumulators = 0;
        var totalDistinctSets = 0;
        var totalInstructions = 0;
        var totalParameterSlots = 0;
        var totalHashTables = 0;
        for (var termIndex = 0; termIndex < count; termIndex++)
        {
            var program = terms[termIndex].Program;
            registerBase[termIndex] = totalRegisters;
            cursorBase[termIndex] = totalCursors;
            sorterBase[termIndex] = totalSorters;
            accumulatorBase[termIndex] = totalAccumulators;
            distinctBase[termIndex] = totalDistinctSets;
            instructionBase[termIndex] = totalInstructions;
            parameterSlotBase[termIndex] = totalParameterSlots;
            hashTableBase[termIndex] = totalHashTables;

            totalRegisters += program.RegisterCount;
            totalCursors += program.CursorCount;
            totalSorters += program.SorterCount;
            totalAccumulators += program.AccumulatorCount;
            totalDistinctSets += program.DistinctSetCount;
            totalInstructions += KeptInstructionCount(program, isLast: false)
                + program.AccumulatorCount;
            totalParameterSlots += program.ParameterSlotCount;
            totalHashTables += program.HashTableCount;
        }

        var capturesSingleSet = mode is null;
        var captureSets = new int[count];
        for (var termIndex = 0; termIndex < count; termIndex++)
            captureSets[termIndex] = totalDistinctSets + (capturesSingleSet ? 0 : termIndex);
        var outputSet = capturesSingleSet
            ? captureSets[0]
            : totalDistinctSets + count;
        var combinedDistinctSets = outputSet + 1;

        var output = new RegisterRange(new Register(totalRegisters), columnCount);
        var rewindAddress = totalInstructions;
        var resultAddress = rewindAddress + 1;
        var haltAddress = rewindAddress + 3;
        var instructions = new List<VdbeInstruction>(haltAddress + 1);
        var cursorSources = new List<VdbeCursorSource>(totalCursors);
        for (var termIndex = 0; termIndex < count; termIndex++)
        {
            var term = terms[termIndex];
            var program = term.Program;
            var kept = KeptInstructionCount(program, isLast: false);
            for (var j = 0; j < kept; j++)
            {
                instructions.Add(RelocateSetOperation(
                    program.Instructions[j],
                    registerBase[termIndex],
                    cursorBase[termIndex],
                    sorterBase[termIndex],
                    accumulatorBase[termIndex],
                    distinctBase[termIndex],
                    instructionBase[termIndex],
                    parameterSlotBase[termIndex],
                    hashTableBase[termIndex],
                    rowEquality,
                    captureSets[termIndex]));
            }
            for (var accumulator = 0; accumulator < program.AccumulatorCount; accumulator++)
            {
                instructions.Add(new AggResetInstruction(
                    new Accumulator(accumulatorBase[termIndex] + accumulator)));
            }

            cursorSources.AddRange(term.CursorSources);
        }

        var membershipSets = captureSets.Skip(1).ToArray();
        instructions.Add(new RowSetRewindInstruction(
            captureSets[0],
            output,
            new ProgramCounter(haltAddress),
            outputComparer));
        instructions.Add(mode is { } membershipMode
            ? new CompoundResultRowInstruction(
                output,
                rowEquality,
                outputSet,
                membershipSets,
                membershipMode)
            : new ResultRowInstruction(output));
        instructions.Add(new RowSetNextInstruction(
            captureSets[0],
            output,
            new ProgramCounter(resultAddress)));
        instructions.Add(new HaltInstruction());

        var combinedSetOp = new VdbeProgram(
            totalRegisters + columnCount,
            totalCursors,
            instructions,
            totalSorters,
            totalAccumulators,
            combinedDistinctSets,
            totalParameterSlots,
            hashTableCount: totalHashTables);
        return new CompoundTerm(combinedSetOp, cursorSources);
    }

    // A non-final term drops its trailing Halt so execution falls through to the next term; the final
    // term keeps its Halt as the combined program's terminator.
    private static int KeptInstructionCount(VdbeProgram program, bool isLast)
        => isLast ? program.Instructions.Count : program.Instructions.Count - 1;

    // The number of result columns a term projects, verifying every result-row emission in the term is
    // the same width. Both plain and distinct result rows count so a distinct sub-term is measurable.
    private static int ResultColumnCount(VdbeProgram program, int termIndex)
    {
        int? width = null;
        foreach (var instruction in program.Instructions)
        {
            var emitted = instruction switch
            {
                ResultRowInstruction result => (int?)result.Values.Count,
                DistinctResultRowInstruction distinct => distinct.Values.Count,
                CompoundResultRowInstruction compound => compound.Values.Count,
                GuardedRowInstruction { Destination: ResultRowDestination } guarded => guarded.Values.Count,
                _ => null,
            };

            if (emitted is not int columns)
                continue;

            if (width is null)
                width = columns;
            else if (width != columns)
            {
                throw new ArgumentException(
                    $"Compound term {termIndex} emits result rows of differing widths ({width} vs {columns}).",
                    nameof(program));
            }
        }

        return width
            ?? throw new ArgumentException(
                $"Compound term {termIndex} does not emit any result rows.",
                nameof(program));
    }

    // Rebuilds one instruction with its resources shifted into the term's disjoint ranges. An outer
    // distinct guard is appended after any nested output guards rather than replacing them.
    private static VdbeInstruction Relocate(
        VdbeInstruction instruction,
        int registerBase,
        int cursorBase,
        int sorterBase,
        int accumulatorBase,
        int distinctBase,
        int instructionBase,
        int parameterSlotBase,
        int hashTableBase,
        VdbeRowEquality? distinctEquality,
        int outerDistinctSet)
    {
        if (RelocateStructural(
                instruction,
                registerBase,
                cursorBase,
                sorterBase,
                accumulatorBase,
                distinctBase,
                instructionBase,
                parameterSlotBase,
                hashTableBase)
            is { } structural)
        {
            return structural;
        }

        Register Reg(Register register) => new(register.Index + registerBase);
        RegisterRange Range(RegisterRange range) => new(Reg(range.Start), range.Count);
        ProgramCounter Pc(ProgramCounter counter) => new(counter.Offset + instructionBase);

        return instruction switch
        {
            GroupKeyInstruction x => new GroupKeyInstruction(
                Range(x.Row),
                Reg(x.Destination),
                x.KeyCount,
                x.Projector,
                x.Equality,
                x.GroupSetIndex + distinctBase,
                x.Hasher,
                x.KeyOutput is { } keyOutput ? Range(keyOutput) : null),
            DistinctGateInstruction x => new DistinctGateInstruction(
                Range(x.Values),
                x.Equality,
                x.DistinctSetIndex + distinctBase,
                Pc(x.DuplicateTarget)),
            RowSetInsertInstruction x => new RowSetInsertInstruction(Range(x.Values), x.Equality, x.RowSetIndex + distinctBase),
            DistinctResultRowInstruction x when distinctEquality is null
                => new DistinctResultRowInstruction(
                    Range(x.Values),
                    x.Equality,
                    x.DistinctSetIndex + distinctBase),
            DistinctResultRowInstruction x => new GuardedRowInstruction(
                Range(x.Values),
                [
                    new DistinctRowGuard(x.Equality, x.DistinctSetIndex + distinctBase),
                    new DistinctRowGuard(distinctEquality, outerDistinctSet),
                ],
                new ResultRowDestination()),
            CompoundResultRowInstruction x when distinctEquality is null
                => new CompoundResultRowInstruction(
                    Range(x.Values),
                    x.Equality,
                    x.OutputSetIndex + distinctBase,
                    RelocateSetIndices(x.MembershipSetIndices, distinctBase),
                    x.Mode),
            CompoundResultRowInstruction x => new GuardedRowInstruction(
                Range(x.Values),
                [
                    new MembershipRowGuard(
                        x.Equality,
                        RelocateSetIndices(x.MembershipSetIndices, distinctBase),
                        x.Mode),
                    new DistinctRowGuard(x.Equality, x.OutputSetIndex + distinctBase),
                    new DistinctRowGuard(distinctEquality, outerDistinctSet),
                ],
                new ResultRowDestination()),
            GuardedRowInstruction { Destination: ResultRowDestination } x
                => new GuardedRowInstruction(
                    Range(x.Values),
                    AppendDistinctGuard(
                        RelocateGuards(x.Guards, distinctBase),
                        distinctEquality,
                        outerDistinctSet),
                    new ResultRowDestination()),
            ResultRowInstruction x => distinctEquality is null
                ? new ResultRowInstruction(Range(x.Values))
                : new DistinctResultRowInstruction(Range(x.Values), distinctEquality, outerDistinctSet),
            _ => throw new StatementCompilationException(
                $"Cannot sequence unsupported opcode {instruction.Opcode} into a compound program."),
        };
    }

    // Rebuilds one instruction of a set-operation term. Every result-producing opcode is redirected into
    // the term's capture set while retaining any nested distinct or membership guards.
    private static VdbeInstruction RelocateSetOperation(
        VdbeInstruction instruction,
        int registerBase,
        int cursorBase,
        int sorterBase,
        int accumulatorBase,
        int distinctBase,
        int instructionBase,
        int parameterSlotBase,
        int hashTableBase,
        VdbeRowEquality equality,
        int captureSet)
    {
        if (RelocateStructural(
                instruction,
                registerBase,
                cursorBase,
                sorterBase,
                accumulatorBase,
                distinctBase,
                instructionBase,
                parameterSlotBase,
                hashTableBase)
            is { } structural)
        {
            return structural;
        }

        Register Reg(Register register) => new(register.Index + registerBase);
        RegisterRange Range(RegisterRange range) => new(Reg(range.Start), range.Count);
        ProgramCounter Pc(ProgramCounter counter) => new(counter.Offset + instructionBase);

        return instruction switch
        {
            GroupKeyInstruction x => new GroupKeyInstruction(
                Range(x.Row),
                Reg(x.Destination),
                x.KeyCount,
                x.Projector,
                x.Equality,
                x.GroupSetIndex + distinctBase,
                x.Hasher,
                x.KeyOutput is { } keyOutput ? Range(keyOutput) : null),
            DistinctGateInstruction x => new DistinctGateInstruction(
                Range(x.Values),
                x.Equality,
                x.DistinctSetIndex + distinctBase,
                Pc(x.DuplicateTarget)),
            ResultRowInstruction x
                => new RowSetInsertInstruction(Range(x.Values), equality, captureSet),
            DistinctResultRowInstruction x => new GuardedRowInstruction(
                Range(x.Values),
                [new DistinctRowGuard(x.Equality, x.DistinctSetIndex + distinctBase)],
                new RowSetDestination(equality, captureSet)),
            CompoundResultRowInstruction x => new GuardedRowInstruction(
                Range(x.Values),
                [
                    new MembershipRowGuard(
                        x.Equality,
                        RelocateSetIndices(x.MembershipSetIndices, distinctBase),
                        x.Mode),
                    new DistinctRowGuard(x.Equality, x.OutputSetIndex + distinctBase),
                ],
                new RowSetDestination(equality, captureSet)),
            GuardedRowInstruction { Destination: ResultRowDestination } x
                => new GuardedRowInstruction(
                    Range(x.Values),
                    RelocateGuards(x.Guards, distinctBase),
                    new RowSetDestination(equality, captureSet)),
            _ => throw new StatementCompilationException(
                $"Cannot sequence unsupported opcode {instruction.Opcode} into a set-operation term."),
        };
    }

    // Relocates every non-output opcode, returning null for group/set/output instructions so each
    // caller can preserve, de-duplicate, or capture it according to the outer compound semantics.
    private static VdbeInstruction? RelocateStructural(
        VdbeInstruction instruction,
        int registerBase,
        int cursorBase,
        int sorterBase,
        int accumulatorBase,
        int distinctBase,
        int instructionBase,
        int parameterSlotBase,
        int hashTableBase)
    {
        Register Reg(Register register) => new(register.Index + registerBase);
        Cursor Cur(Cursor cursor) => new(cursor.Index + cursorBase);
        Sorter Sort(Sorter sorter) => new(sorter.Index + sorterBase);
        Accumulator Acc(Accumulator accumulator) => new(accumulator.Index + accumulatorBase);
        ProgramCounter Pc(ProgramCounter counter) => new(counter.Offset + instructionBase);
        RegisterRange Range(RegisterRange range) => new(Reg(range.Start), range.Count);
        ParameterSlot Slot(ParameterSlot slot) => new(slot.Index + parameterSlotBase);

        return instruction switch
        {
            LoadConstantInstruction x => new LoadConstantInstruction(Reg(x.Destination), x.Value),
            LoadParameterInstruction x => new LoadParameterInstruction(Reg(x.Destination), Slot(x.Slot)),
            CopyInstruction x => new CopyInstruction(Reg(x.Source), Reg(x.Destination)),
            FunctionInstruction x => new FunctionInstruction(Reg(x.Destination), x.Function, Range(x.Arguments)),
            ArithmeticInstruction x => new ArithmeticInstruction(Reg(x.Destination), x.Operator, Range(x.Operands)),
            NumericAffinityInstruction x => new NumericAffinityInstruction(Reg(x.Value), x.Affinity),
            CompareInstruction x => new CompareInstruction(
                Reg(x.Destination),
                x.Operator,
                Reg(x.Left),
                Reg(x.Right),
                x.LeftAffinity,
                x.RightAffinity,
                x.Collation),
            CastInstruction x => new CastInstruction(Reg(x.Value), x.TypeName),
            OpenReadCursorInstruction x => new OpenReadCursorInstruction(Cur(x.Cursor), x.TableName, x.ColumnCount),
            OpenWriteCursorInstruction x => new OpenWriteCursorInstruction(Cur(x.Cursor), x.TableName, x.ColumnCount),
            CloseCursorInstruction x => new CloseCursorInstruction(Cur(x.Cursor)),
            OpenSorterInstruction x => new OpenSorterInstruction(Sort(x.Sorter), x.Comparer, x.ColumnCount, x.BufferRowCapacity),
            SorterInsertInstruction x => new SorterInsertInstruction(Sort(x.Sorter), Range(x.Record)),
            SorterSortInstruction x => new SorterSortInstruction(Sort(x.Sorter), Pc(x.EmptyTarget)),
            SorterDataInstruction x => new SorterDataInstruction(Sort(x.Sorter), Range(x.Destination)),
            SorterNextInstruction x => new SorterNextInstruction(Sort(x.Sorter), Pc(x.LoopTarget)),
            CloseSorterInstruction x => new CloseSorterInstruction(Sort(x.Sorter)),
            GotoInstruction x => new GotoInstruction(Pc(x.Target)),
            JumpIfInstruction x => new JumpIfInstruction(Reg(x.Register), Pc(x.Target)),
            JumpIfNotTrueInstruction x => new JumpIfNotTrueInstruction(Reg(x.Value), Pc(x.FalseTarget)),
            IfPosInstruction x => new IfPosInstruction(Reg(x.Register), Pc(x.Target), x.DecrementBy),
            IfNegInstruction x => new IfNegInstruction(Reg(x.Register), Pc(x.Target)),
            DecrJumpZeroInstruction x => new DecrJumpZeroInstruction(Reg(x.Register), Pc(x.Target)),
            MustBeIntInstruction x => new MustBeIntInstruction(Reg(x.Register), x.Target is { } t ? Pc(t) : null),
            SoftNullInstruction x => new SoftNullInstruction(Reg(x.Register)),
            MemMaxInstruction x => new MemMaxInstruction(Reg(x.Destination), Reg(x.Source)),
            AddImmInstruction x => new AddImmInstruction(Reg(x.Register), x.Value),
            ZeroOrNullInstruction x => new ZeroOrNullInstruction(Reg(x.Left), Reg(x.Right), Reg(x.Destination)),
            GosubInstruction x => new GosubInstruction(Pc(x.Target), Reg(x.ReturnRegister)),
            ReturnInstruction x => new ReturnInstruction(Reg(x.ReturnRegister), x.CanFallThrough),
            BeginSubrtnInstruction x => new BeginSubrtnInstruction(Reg(x.Destination), x.DestinationEnd is { } e ? Reg(e) : null),
            SequenceInstruction x => new SequenceInstruction(Cur(x.Cursor), Reg(x.Destination)),
            SequenceTestInstruction x => new SequenceTestInstruction(Cur(x.Cursor), Pc(x.Target), Reg(x.ValueRegister)),
            HaltIfNullInstruction x => new HaltIfNullInstruction(Reg(x.Target), x.ErrorCode, x.Description),
            NotExistsInstruction x => new NotExistsInstruction(Cur(x.Cursor), Reg(x.RowIdRegister), Pc(x.JumpTarget), x.Description),
            FoundInstruction x => new FoundInstruction(Cur(x.Cursor), Reg(x.RowIdRegister), Pc(x.FoundTarget), x.Description),
            OpenPseudoInstruction x => new OpenPseudoInstruction(Cur(x.Cursor), Range(x.Content)),
            BlobReadInstruction x => new BlobReadInstruction(
                Cur(x.Cursor),
                x.ColumnIndex,
                Reg(x.Offset),
                Reg(x.Amount),
                Reg(x.Destination)),
            BlobWriteInstruction x => new BlobWriteInstruction(
                Cur(x.Cursor),
                x.ColumnIndex,
                Reg(x.Offset),
                Reg(x.Source),
                Reg(x.Destination)),
            BlobLenInstruction x => new BlobLenInstruction(Cur(x.Cursor), x.ColumnIndex, Reg(x.Destination)),
            AggResetInstruction x => new AggResetInstruction(Acc(x.Accumulator)),
            AggStepInstruction x => new AggStepInstruction(Acc(x.Accumulator), x.Aggregate, Range(x.Arguments)),
            AggInverseInstruction x => new AggInverseInstruction(Acc(x.Accumulator), x.Aggregate, Range(x.Arguments)),
            AggFinalizeInstruction x => new AggFinalizeInstruction(Acc(x.Accumulator), x.Aggregate, Reg(x.Destination)),
            AggValueInstruction x => new AggValueInstruction(Acc(x.Accumulator), x.Aggregate, Reg(x.Destination)),
            SameGroupInstruction x => new SameGroupInstruction(Range(x.CurrentKey), Range(x.SavedKey), x.Comparer, Pc(x.SameGroupTarget)),
            RowSetInsertInstruction x => new RowSetInsertInstruction(
                Range(x.Values),
                x.Equality,
                x.RowSetIndex + distinctBase),
            RowSetRewindInstruction x => new RowSetRewindInstruction(
                x.RowSetIndex + distinctBase,
                Range(x.Destination),
                Pc(x.EmptyTarget),
                x.Comparer),
            RowSetNextInstruction x => new RowSetNextInstruction(
                x.RowSetIndex + distinctBase,
                Range(x.Destination),
                Pc(x.LoopTarget)),
            GuardedRowInstruction { Destination: RowSetDestination destination } x
                => new GuardedRowInstruction(
                    Range(x.Values),
                    RelocateGuards(x.Guards, distinctBase),
                    new RowSetDestination(
                        destination.Equality,
                        destination.RowSetIndex + distinctBase)),
            RewindCursorInstruction x => new RewindCursorInstruction(Cur(x.Cursor), Pc(x.EmptyTarget)),
            OpenAutoindexInstruction x => new OpenAutoindexInstruction(Cur(x.Cursor), x.ColumnCount),
            OpenDupInstruction x => new OpenDupInstruction(Cur(x.NewCursor), Cur(x.OriginalCursor)),
            ResetSorterInstruction x => new ResetSorterInstruction(Cur(x.Cursor)),
            ColumnHasFieldInstruction x => new ColumnHasFieldInstruction(Cur(x.Cursor), x.Column, Pc(x.Target)),
            DeferredSeekInstruction x => new DeferredSeekInstruction(Cur(x.IndexCursor), Cur(x.TableCursor)),
            SeekEndInstruction x => new SeekEndInstruction(Cur(x.Cursor)),
            BloomFilterInstruction x => new BloomFilterInstruction(Cur(x.Cursor), Range(x.Key), Pc(x.NotPresentTarget)),
            BloomFilterAddInstruction x => new BloomFilterAddInstruction(Cur(x.Cursor), Range(x.Key)),
            HashBuildInstruction x => new HashBuildInstruction(
                Cur(x.Cursor),
                Range(x.Key),
                x.HashTableId + hashTableBase,
                x.MemoryBudget,
                x.Collations,
                x.Payload is { } payload ? Range(payload) : null,
                x.TrackMatched),
            HashDistinctInstruction x => new HashDistinctInstruction(
                Range(x.Key),
                x.Collations,
                x.HashTableId + hashTableBase,
                Pc(x.DuplicateTarget)),
            HashBuildFinalizeInstruction x => new HashBuildFinalizeInstruction(x.HashTableId + hashTableBase),
            HashProbeInstruction x => new HashProbeInstruction(
                x.HashTableId + hashTableBase,
                Range(x.Key),
                Reg(x.Destination),
                x.PayloadDestination is { } payloadDest ? Range(payloadDest) : null,
                Pc(x.NotFoundTarget)),
            HashNextInstruction x => new HashNextInstruction(
                x.HashTableId + hashTableBase,
                Reg(x.Destination),
                x.PayloadDestination is { } payloadDest ? Range(payloadDest) : null,
                Pc(x.ExhaustedTarget)),
            HashCloseInstruction x => new HashCloseInstruction(x.HashTableId + hashTableBase),
            HashClearInstruction x => new HashClearInstruction(x.HashTableId + hashTableBase),
            HashMarkMatchedInstruction x => new HashMarkMatchedInstruction(x.HashTableId + hashTableBase),
            HashResetMatchedInstruction x => new HashResetMatchedInstruction(x.HashTableId + hashTableBase),
            HashScanUnmatchedInstruction x => new HashScanUnmatchedInstruction(
                x.HashTableId + hashTableBase,
                Reg(x.Destination),
                x.PayloadDestination is { } payloadDest ? Range(payloadDest) : null,
                Pc(x.ExhaustedTarget)),
            HashNextUnmatchedInstruction x => new HashNextUnmatchedInstruction(
                x.HashTableId + hashTableBase,
                Reg(x.Destination),
                x.PayloadDestination is { } payloadDest ? Range(payloadDest) : null,
                Pc(x.ExhaustedTarget)),
            ColumnInstruction x => new ColumnInstruction(Cur(x.Cursor), x.ColumnIndex, Reg(x.Destination)),
            RowIdInstruction x => new RowIdInstruction(Cur(x.Cursor), Reg(x.Destination)),
            DeleteInstruction x => new DeleteInstruction(Cur(x.Cursor)),
            InsertInstruction x => new InsertInstruction(
                Cur(x.Cursor),
                x.Flags,
                x.Record is { } record ? Reg(record) : null,
                x.RowId is { } rowId ? Reg(rowId) : null,
                x.TableName),
            UpdateInstruction x => new UpdateInstruction(Cur(x.Cursor)),
            CommitInstruction x => new CommitInstruction(Cur(x.Cursor)),
            FilterInstruction x => new FilterInstruction(Cur(x.Cursor), x.Predicate, Pc(x.FalseTarget), x.Description),
            FilterRowIdInstruction x => new FilterRowIdInstruction(Cur(x.Cursor), x.Predicate, Pc(x.FalseTarget), x.Description),
            FilterRegistersInstruction x => new FilterRegistersInstruction(Range(x.Row), x.Predicate, Pc(x.FalseTarget), x.Description),
            NextInstruction x => new NextInstruction(Cur(x.Cursor), Pc(x.LoopTarget)),
            YieldInstruction => instruction,
            HaltInstruction => instruction,
            ResultRowInstruction => null,
            GroupKeyInstruction => null,
            DistinctResultRowInstruction => null,
            DistinctGateInstruction => null,
            CompoundResultRowInstruction => null,
            GuardedRowInstruction => null,
            _ => throw new StatementCompilationException(
                $"Cannot sequence unsupported opcode {instruction.Opcode} into a compound program."),
        };
    }

    private static IReadOnlyList<VdbeRowGuard> RelocateGuards(
        IReadOnlyList<VdbeRowGuard> guards,
        int distinctBase)
    {
        var relocated = new VdbeRowGuard[guards.Count];
        for (var index = 0; index < guards.Count; index++)
        {
            relocated[index] = guards[index] switch
            {
                DistinctRowGuard distinct
                    => new DistinctRowGuard(distinct.Equality, distinct.RowSetIndex + distinctBase),
                MembershipRowGuard membership
                    => new MembershipRowGuard(
                        membership.Equality,
                        RelocateSetIndices(membership.RowSetIndices, distinctBase),
                        membership.Mode),
                _ => throw new StatementCompilationException(
                    $"Cannot relocate unsupported row guard {guards[index].GetType().Name}."),
            };
        }

        return relocated;
    }

    private static IReadOnlyList<VdbeRowGuard> AppendDistinctGuard(
        IReadOnlyList<VdbeRowGuard> guards,
        VdbeRowEquality? equality,
        int rowSetIndex)
    {
        if (equality is null)
            return guards;

        var appended = new VdbeRowGuard[guards.Count + 1];
        for (var index = 0; index < guards.Count; index++)
            appended[index] = guards[index];
        appended[^1] = new DistinctRowGuard(equality, rowSetIndex);
        return appended;
    }

    private static int[] RelocateSetIndices(IReadOnlyList<int> indices, int distinctBase)
    {
        var relocated = new int[indices.Count];
        for (var i = 0; i < indices.Count; i++)
            relocated[i] = indices[i] + distinctBase;

        return relocated;
    }
}
