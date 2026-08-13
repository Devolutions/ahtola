using Ahtola.Core.Execution;

namespace Ahtola.Core.Compilation;

/// <summary>
/// Lowers a bounded, linear recursive common table expression directly onto the resumable state machine:
/// it emits a runnable <see cref="VdbeProgram"/> that seeds a recursive <see cref="WorkTable"/> with the
/// anchor rows and then drains and re-feeds its FIFO frontier through a caller-supplied row or generation
/// transform, so a <c>WITH RECURSIVE cte AS (anchor UNION [ALL] recursive)</c>
/// executes as real, observably looping bytecode rather than a precomputed result set replayed by the
/// tree-walking evaluator. It is the recursion sibling of <see cref="ValuesProgramBuilder"/> (which streams
/// fixed rows) and <see cref="CompoundProgramBuilder"/> (which sequences term streams).
/// </summary>
/// <remarks>
/// <para>
/// The emitted program is data-free control flow over one worktable and a shared <c>W</c>-register block:
/// <code>
///   0            OpenWorkTable wt (W cols, mode, &lt;=MaxRows rows, depth&lt;=MaxDepth)
///   …            LoadConstant r[0..W-1]=seed(0)      (one block per anchor row)
///                SeedWorkTable wt, r[0..W-1]
///                …                                  (remaining anchor rows)
///   loopTop      WorkTableStep wt -> r[0..W-1], goto done if drained
///                ResultRow r[0..W-1]
///                WorkTableExpand[Generation] wt, transform, r[0..W-1]
///                Goto loopTop
///   done         CloseWorkTable wt
///                Halt
/// </code>
/// The recursion — FIFO (breadth-first) ordering, re-feeding descendants, de-duplication, depth bounding,
/// and the total-row cap — is performed by the interpreter's worktable step/expand
/// loop, one generation at a time, not by any single opcode. The anchor generation surfaces first (in seed
/// order), then all of its children, then their children, exactly mirroring the evaluator's level-by-level
/// working-set iteration for a linear recursive term.
/// </para>
/// <para>
/// The builder owns only the mechanical layout and reuses the recursive worktable opcode family unchanged.
/// Value and recursion semantics stay with the caller, exactly as every other direct builder delegates
/// theirs: each seed cell is a literal (<see cref="LoadConstantInstruction"/>) or a late-bound parameter
/// (<see cref="LoadParameterInstruction"/>), so a parameterized anchor re-executes with fresh bindings after
/// a <see cref="ResumableStatement.Reset"/>/<see cref="ResumableStatement.Rebind"/> without recompilation;
/// the recursive term is either a <see cref="VdbeRecursiveTransform"/> over one row or a
/// <see cref="VdbeRecursiveGenerationTransform"/> over the complete frontier; and <c>UNION</c>
/// de-duplication is a caller-supplied <see cref="VdbeRowEquality"/>.
/// Both guards are mandatory and make the recursion safe by construction: <c>maxRows</c> caps
/// total admitted rows (throwing <see cref="RecursiveWorkTableOverflowException"/> on overflow, so an
/// unbounded <c>UNION ALL</c> fails loudly) and <c>maxDepth</c> bounds the recursion depth of the slice.
/// </para>
/// <para>
/// Scope: this builds the well-defined <em>linear</em> recursion (a single recursive transform, matching a
/// recursive term that references the CTE exactly once). Multiple distinct recursive terms with the
/// evaluator's per-term-then-per-row interleaving, and mutual recursion, are out of scope; a caller can
/// still fold several per-row contributions into one transform. Routing SQL to this builder — deciding
/// eligibility, resolving the anchor rows and the recursive term into a transform, and assigning result
/// column names — belongs to the database layer and is intentionally not wired here.
/// </para>
/// </remarks>
public static class RecursiveCteProgramBuilder
{
    /// <summary>
    /// Builds a bounded recursive program with <c>UNION ALL</c> semantics from constant anchor rows: every
    /// admitted row (anchor or descendant) is emitted, in breadth-first order, with no de-duplication.
    /// Termination is guaranteed by the depth and row guards alone.
    /// </summary>
    public static VdbeProgram BuildUnionAll(
        IReadOnlyList<IReadOnlyList<SqlValue>> seedRows,
        VdbeRecursiveTransform transform,
        int maxRows,
        int maxDepth)
        => BuildCells(
            ToConstantCellRows(seedRows),
            RequireRowTransform(transform),
            generationTransform: null,
            WorkTableDedupMode.KeepAll,
            equality: null,
            maxRows,
            maxDepth);

    /// <summary>
    /// Builds a bounded recursive program with <c>UNION</c>/<c>DISTINCT</c> semantics from constant anchor
    /// rows: each distinct row is emitted once, at its first occurrence in breadth-first order.
    /// De-duplication uses <paramref name="rowEquality"/>, so the caller owns the row-equality contract; it
    /// also breaks cycles, so a finite reachable set terminates naturally within the guards.
    /// </summary>
    public static VdbeProgram BuildUnionDistinct(
        IReadOnlyList<IReadOnlyList<SqlValue>> seedRows,
        VdbeRecursiveTransform transform,
        VdbeRowEquality rowEquality,
        int maxRows,
        int maxDepth)
    {
        ArgumentNullException.ThrowIfNull(rowEquality);
        return BuildCells(
            ToConstantCellRows(seedRows),
            RequireRowTransform(transform),
            generationTransform: null,
            WorkTableDedupMode.Distinct,
            rowEquality,
            maxRows,
            maxDepth);
    }

    /// <summary>
    /// Builds a bounded <c>UNION ALL</c> recursive program whose transform runs once per complete
    /// breadth-first generation rather than once per row.
    /// </summary>
    public static VdbeProgram BuildUnionAllGenerations(
        IReadOnlyList<IReadOnlyList<SqlValue>> seedRows,
        VdbeRecursiveGenerationTransform transform,
        int maxRows,
        int maxDepth)
        => BuildCells(
            ToConstantCellRows(seedRows),
            rowTransform: null,
            RequireGenerationTransform(transform),
            WorkTableDedupMode.KeepAll,
            equality: null,
            maxRows,
            maxDepth);

    /// <summary>
    /// Builds a bounded <c>UNION</c> recursive program whose transform runs once per complete
    /// breadth-first generation.
    /// </summary>
    public static VdbeProgram BuildUnionDistinctGenerations(
        IReadOnlyList<IReadOnlyList<SqlValue>> seedRows,
        VdbeRecursiveGenerationTransform transform,
        VdbeRowEquality rowEquality,
        int maxRows,
        int maxDepth)
    {
        ArgumentNullException.ThrowIfNull(rowEquality);
        return BuildCells(
            ToConstantCellRows(seedRows),
            rowTransform: null,
            RequireGenerationTransform(transform),
            WorkTableDedupMode.Distinct,
            rowEquality,
            maxRows,
            maxDepth);
    }

    /// <summary>
    /// Builds the same program as <see cref="BuildUnionAll(IReadOnlyList{IReadOnlyList{SqlValue}}, VdbeRecursiveTransform, int, int)"/>
    /// from anchor rows of mixed constant and late-bound-parameter cells, so a parameterized anchor
    /// (<c>VALUES (?1, 0)</c> as the seed) re-binds and re-runs without recompilation.
    /// </summary>
    public static VdbeProgram BuildUnionAllCells(
        IReadOnlyList<IReadOnlyList<ValuesCell>> seedRows,
        VdbeRecursiveTransform transform,
        int maxRows,
        int maxDepth)
        => BuildCells(
            seedRows,
            RequireRowTransform(transform),
            generationTransform: null,
            WorkTableDedupMode.KeepAll,
            equality: null,
            maxRows,
            maxDepth);

    /// <summary>
    /// Builds the same program as <see cref="BuildUnionDistinct(IReadOnlyList{IReadOnlyList{SqlValue}}, VdbeRecursiveTransform, VdbeRowEquality, int, int)"/>
    /// from anchor rows of mixed constant and late-bound-parameter cells.
    /// </summary>
    public static VdbeProgram BuildUnionDistinctCells(
        IReadOnlyList<IReadOnlyList<ValuesCell>> seedRows,
        VdbeRecursiveTransform transform,
        VdbeRowEquality rowEquality,
        int maxRows,
        int maxDepth)
    {
        ArgumentNullException.ThrowIfNull(rowEquality);
        return BuildCells(
            seedRows,
            RequireRowTransform(transform),
            generationTransform: null,
            WorkTableDedupMode.Distinct,
            rowEquality,
            maxRows,
            maxDepth);
    }

    // Wraps a constant-only anchor row list as ValuesCell rows so the constant and parameter paths share
    // one lowering, raising the same diagnostics for a null list or null row.
    private static IReadOnlyList<IReadOnlyList<ValuesCell>> ToConstantCellRows(
        IReadOnlyList<IReadOnlyList<SqlValue>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var cellRows = new IReadOnlyList<ValuesCell>[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i]
                ?? throw new ArgumentException($"Recursive anchor row {i} must not be null.", nameof(rows));
            var cells = new ValuesCell[row.Count];
            for (var column = 0; column < row.Count; column++)
                cells[column] = ValuesCell.Constant(row[column]);

            cellRows[i] = cells;
        }

        return cellRows;
    }

    private static VdbeProgram BuildCells(
        IReadOnlyList<IReadOnlyList<ValuesCell>> seedRows,
        VdbeRecursiveTransform? rowTransform,
        VdbeRecursiveGenerationTransform? generationTransform,
        WorkTableDedupMode mode,
        VdbeRowEquality? equality,
        int maxRows,
        int maxDepth)
    {
        ArgumentNullException.ThrowIfNull(seedRows);
        if ((rowTransform is null) == (generationTransform is null))
        {
            throw new ArgumentException(
                "A recursive program needs exactly one row or generation transform.");
        }
        if (seedRows.Count == 0)
            throw new ArgumentException("A recursive program needs at least one anchor row.", nameof(seedRows));
        if (maxRows <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxRows), maxRows, "The row guard must be positive.");
        if (maxDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(maxDepth), maxDepth, "The depth guard must be non-negative.");

        var width = -1;
        for (var i = 0; i < seedRows.Count; i++)
        {
            var row = seedRows[i]
                ?? throw new ArgumentException($"Recursive anchor row {i} must not be null.", nameof(seedRows));

            if (width < 0)
            {
                width = row.Count;
                if (width == 0)
                    throw new ArgumentException("A recursive anchor row must have at least one term.", nameof(seedRows));
            }
            else if (row.Count != width)
            {
                throw new ArgumentException(
                    $"all recursive anchor rows must have the same number of terms (row 0 has {width}, row {i} has {row.Count}).",
                    nameof(seedRows));
            }
        }

        var outputRange = new RegisterRange(new Register(0), width);
        var workTable = new WorkTable(0);
        var instructions = new List<VdbeInstruction>(checked(seedRows.Count * (width + 1) + 7))
        {
            new OpenWorkTableInstruction(workTable, width, mode, maxRows, maxDepth, equality),
        };

        // Seed section: each anchor row reloads r[0..W-1] then admits it to the frontier. The interpreter
        // snapshots the row on admission, so reusing the block across seeds cannot disturb a queued row.
        var maxSlot = -1;
        foreach (var row in seedRows)
        {
            for (var column = 0; column < width; column++)
            {
                var cell = row[column];
                var destination = new Register(column);
                if (cell.IsParameter)
                {
                    var slot = cell.Slot;
                    if (slot.Index > maxSlot)
                        maxSlot = slot.Index;

                    instructions.Add(new LoadParameterInstruction(destination, slot));
                }
                else
                {
                    instructions.Add(new LoadConstantInstruction(destination, cell.Value));
                }
            }

            instructions.Add(new SeedWorkTableInstruction(workTable, outputRange));
        }

        // Drain loop: dequeue a frontier row, emit it, expand it one generation deeper, and repeat. The
        // WorkTableStep jumps past the loop to the CloseWorkTable once the frontier is drained.
        var loopTop = instructions.Count;
        var doneTarget = new ProgramCounter(loopTop + 4);
        instructions.Add(new WorkTableStepInstruction(workTable, outputRange, doneTarget));
        instructions.Add(new ResultRowInstruction(outputRange));
        instructions.Add(rowTransform is not null
            ? new WorkTableExpandInstruction(workTable, rowTransform, outputRange)
            : new WorkTableExpandGenerationInstruction(workTable, generationTransform!, outputRange));
        instructions.Add(new GotoInstruction(new ProgramCounter(loopTop)));
        instructions.Add(new CloseWorkTableInstruction(workTable));
        instructions.Add(new HaltInstruction());

        return new VdbeProgram(
            registerCount: width,
            cursorCount: 0,
            instructions,
            parameterSlotCount: maxSlot + 1,
            workTableCount: 1);
    }

    private static VdbeRecursiveTransform RequireRowTransform(VdbeRecursiveTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        return transform;
    }

    private static VdbeRecursiveGenerationTransform RequireGenerationTransform(
        VdbeRecursiveGenerationTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        return transform;
    }
}
