using Ahtola.Core.Execution;

namespace Ahtola.Core.Compilation;

/// <summary>
/// Lowers a <c>VALUES</c> row list directly onto the resumable state machine: it emits a runnable
/// <see cref="VdbeProgram"/> that streams each explicit row through an unconditional <c>ResultRow</c>,
/// so a source-less <c>VALUES (…), (…)</c> table value constructor executes as real bytecode rather than
/// a precomputed result set handed back by the tree-walking evaluator. It is a direct builder in the same
/// spirit as <see cref="SelectStatementCompiler"/>'s single-row constant projection, generalized to the
/// multi-row case.
/// </summary>
/// <remarks>
/// <para>
/// The emitted program is data-free control flow over a fixed register block. Every row reloads the shared
/// <c>W</c>-register block <c>r[0..W-1]</c> with <see cref="LoadConstantInstruction"/> and emits it with
/// <see cref="ResultRowInstruction"/>; a single terminating <see cref="HaltInstruction"/> ends the stream.
/// Reusing one register block across rows is safe because the interpreter snapshots the registers into a
/// fresh array at each <c>ResultRow</c> (<see cref="ResumableStatement"/>), so a later row's loads never
/// disturb a row already produced. No cursors, sorters, accumulators, or distinct sets are needed:
/// <code>
///   0            LoadConstant r[0]=v(0,0)
///   …            LoadConstant r[W-1]=v(0,W-1)
///                ResultRow r[0..W-1]           (row 0)
///   …            LoadConstant r[0]=v(1,0)      (row 1 reloads the same block)
///                …
///                ResultRow r[0..W-1]
///                …
///                Halt
/// </code>
/// </para>
/// <para>
/// The builder owns only the mechanical row layout and reuses the existing opcode set unchanged; it adds
/// no opcodes because a <c>VALUES</c> row carries no runtime state beyond values already expressible with
/// <c>LoadConstant</c>. It never inspects SQL types, so it composes with the shared result-row machinery:
/// the emitted program (or the <see cref="CompoundTerm"/> from
/// <see cref="BuildTerm(IReadOnlyList{IReadOnlyList{SqlValue}})"/>) can be sequenced
/// by <see cref="CompoundProgramBuilder"/> (as a <c>UNION ALL</c>/<c>UNION</c>/<c>INTERSECT</c>/<c>EXCEPT</c>
/// term, since it emits through plain <c>ResultRow</c>) and gated by <see cref="LimitOffsetProgramBuilder"/>
/// (whose OFFSET/LIMIT counters span every <c>ResultRow</c> the program emits).
/// </para>
/// <para>
/// Value semantics stay with the caller, exactly as the scan, sorted-scan, join, aggregate, and compound
/// builders delegate theirs. Each cell is either a resolved <see cref="SqlValue"/> literal that folds to
/// its value, or a <see cref="ValuesCell.Parameter(ParameterSlot)"/> reference to a late-bound slot. A literal is emitted
/// as <see cref="LoadConstantInstruction"/>; a parameter is emitted as <see cref="LoadParameterInstruction"/>,
/// which reads its value from the <see cref="VdbeParameterBinding"/> supplied at execution time. A program
/// containing parameter cells therefore re-executes with fresh bindings after a
/// <see cref="ResumableStatement.Reset"/>/<see cref="ResumableStatement.Rebind"/> <em>without</em> being
/// rebuilt — the whole point of the late-binding opcode. The parameter-slot width is inferred as the
/// highest slot referenced plus one, matching SQLite's <c>?n</c> numbering, and exposed via
/// <see cref="VdbeProgram.ParameterSlotCount"/>. Resolving each row's expressions/parameters, mapping SQL
/// placeholders to slots, and routing an eligible <c>VALUES</c> statement here belongs to the database
/// layer, which also assigns the generated column names (<c>column1</c>, <c>column2</c>, …) the emitted
/// rows carry no notion of.
/// </para>
/// </remarks>
public static class ValuesProgramBuilder
{
    /// <summary>
    /// Builds a program that streams <paramref name="rows"/> in order, one <c>ResultRow</c> per row.
    /// Requires at least one row, at least one term per row, and every row to have the same number of
    /// terms; the shared width becomes the program's register count.
    /// </summary>
    /// <param name="rows">The explicit rows, each a list of resolved cell values in column order.</param>
    /// <returns>A runnable, cursor-less <see cref="VdbeProgram"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rows"/> is null.</exception>
    /// <exception cref="ArgumentException">The row list is empty, a row is null, a row has no terms, or the
    /// rows are not all the same width.</exception>
    public static VdbeProgram Build(IReadOnlyList<IReadOnlyList<SqlValue>> rows)
        => BuildCells(ToConstantCellRows(rows));

    /// <summary>
    /// Builds a program that streams <paramref name="rows"/> of mixed constant and late-bound-parameter
    /// cells, one <c>ResultRow</c> per row. Constant cells emit <c>LoadConstant</c>; parameter cells emit
    /// <c>LoadParameter</c>, so the program can be re-bound and re-run without recompilation. The
    /// parameter-slot width is the highest slot referenced plus one (zero when no cell is a parameter).
    /// </summary>
    /// <param name="rows">The explicit rows, each a list of cells in column order.</param>
    /// <returns>A runnable, cursor-less <see cref="VdbeProgram"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rows"/> is null.</exception>
    /// <exception cref="ArgumentException">The row list is empty, a row is null, a row has no terms, or the
    /// rows are not all the same width.</exception>
    /// <remarks>This is deliberately a distinct name rather than a <see cref="Build(IReadOnlyList{IReadOnlyList{SqlValue}})"/>
    /// overload: a <c>Build(null)</c> call would otherwise be ambiguous between the constant and cell row
    /// shapes, which both erase to <c>IReadOnlyList&lt;IReadOnlyList&lt;T&gt;&gt;</c>.</remarks>
    public static VdbeProgram BuildCells(IReadOnlyList<IReadOnlyList<ValuesCell>> rows)
    {
        var (instructions, width, parameterSlotCount) = Lower(rows);
        return new VdbeProgram(
            registerCount: width,
            cursorCount: 0,
            instructions,
            parameterSlotCount: parameterSlotCount);
    }

    /// <summary>
    /// Builds a multi-row VALUES-style stream that materializes rows into an
    /// <see cref="OpenEphemeralInstruction"/> table, then Rewind/Next scans it.
    /// Used for paths that own a dedicated cursor (not LimitOffset/compound composition).
    /// </summary>
    public static VdbeProgram BuildEphemeralCells(IReadOnlyList<IReadOnlyList<ValuesCell>> rows)
    {
        var (instructions, width, parameterSlotCount) = LowerEphemeral(rows);
        return new VdbeProgram(
            registerCount: width,
            cursorCount: 1,
            instructions,
            parameterSlotCount: parameterSlotCount);
    }

    /// <summary>
    /// Builds the same program as <see cref="Build(IReadOnlyList{IReadOnlyList{SqlValue}})"/> wrapped as a
    /// <see cref="CompoundTerm"/> with an empty cursor-source list, so a <c>VALUES</c> row list can be
    /// sequenced directly by <see cref="CompoundProgramBuilder"/> (a source-less term iterates no cursors).
    /// </summary>
    public static CompoundTerm BuildTerm(IReadOnlyList<IReadOnlyList<SqlValue>> rows)
        => new(Build(rows), []);

    /// <summary>
    /// Builds the same program as <see cref="BuildCells(IReadOnlyList{IReadOnlyList{ValuesCell}})"/> wrapped
    /// as a <see cref="CompoundTerm"/> with an empty cursor-source list, so a parameterized <c>VALUES</c>
    /// row list composes with <see cref="CompoundProgramBuilder"/>.
    /// </summary>
    public static CompoundTerm BuildTermCells(IReadOnlyList<IReadOnlyList<ValuesCell>> rows)
        => new(BuildCells(rows), []);

    // Wraps a constant-only row list as ValuesCell rows so the constant and parameter paths share one
    // lowering. The row-null and null-list checks are performed here with the exact diagnostics the
    // constant API has always raised, so callers (and the routed VALUES path) see identical messages.
    private static IReadOnlyList<IReadOnlyList<ValuesCell>> ToConstantCellRows(
        IReadOnlyList<IReadOnlyList<SqlValue>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var cellRows = new IReadOnlyList<ValuesCell>[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i]
                ?? throw new ArgumentException($"VALUES row {i} must not be null.", nameof(rows));
            var cells = new ValuesCell[row.Count];
            for (var column = 0; column < row.Count; column++)
                cells[column] = ValuesCell.Constant(row[column]);

            cellRows[i] = cells;
        }

        return cellRows;
    }

    // Validates the rows and lowers them into the LoadConstant/LoadParameter/ResultRow blocks plus the
    // terminating Halt, returning the fixed row width (so the caller can size the register file) and the
    // inferred parameter-slot count (highest slot + 1, or 0 when no cell is a parameter).
    private static (List<VdbeInstruction> Instructions, int Width, int ParameterSlotCount) Lower(
        IReadOnlyList<IReadOnlyList<ValuesCell>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
            throw new ArgumentException("A VALUES program needs at least one row.", nameof(rows));

        var width = -1;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i]
                ?? throw new ArgumentException($"VALUES row {i} must not be null.", nameof(rows));

            if (width < 0)
            {
                width = row.Count;
                if (width == 0)
                    throw new ArgumentException("A VALUES row must have at least one term.", nameof(rows));
            }
            else if (row.Count != width)
            {
                throw new ArgumentException(
                    $"all VALUES must have the same number of terms (row 0 has {width}, row {i} has {row.Count}).",
                    nameof(rows));
            }
        }

        // Every row reuses the register block r[0..W-1]: reload it, then emit it. The interpreter copies
        // the registers at each ResultRow, so overwriting them for the next row cannot mutate an already
        // produced row. One LoadConstant/LoadParameter per term, one ResultRow per row, one final Halt.
        var instructions = new List<VdbeInstruction>(checked(rows.Count * (width + 1) + 1));
        var outputRange = new RegisterRange(new Register(0), width);
        var maxSlot = -1;
        foreach (var row in rows)
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

            instructions.Add(new ResultRowInstruction(outputRange));
        }

        instructions.Add(new HaltInstruction());
        return (instructions, width, maxSlot + 1);
    }

    private static (List<VdbeInstruction> Instructions, int Width, int ParameterSlotCount) LowerEphemeral(
        IReadOnlyList<IReadOnlyList<ValuesCell>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
            throw new ArgumentException("A VALUES program needs at least one row.", nameof(rows));

        var width = -1;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i]
                ?? throw new ArgumentException($"VALUES row {i} must not be null.", nameof(rows));
            if (width < 0)
            {
                width = row.Count;
                if (width == 0)
                    throw new ArgumentException("A VALUES row must have at least one term.", nameof(rows));
            }
            else if (row.Count != width)
            {
                throw new ArgumentException(
                    $"all VALUES must have the same number of terms (row 0 has {width}, row {i} has {row.Count}).",
                    nameof(rows));
            }
        }

        var cursor = new Cursor(0);
        var outputRange = new RegisterRange(new Register(0), width);
        var maxSlot = -1;
        var instructions = new List<VdbeInstruction>(checked(1 + rows.Count * (width + 1) + width + 5));
        instructions.Add(new OpenEphemeralInstruction(cursor, width));
        foreach (var row in rows)
        {
            for (var column = 0; column < width; column++)
            {
                var cell = row[column];
                var destination = new Register(column);
                if (cell.IsParameter)
                {
                    if (cell.Slot.Index > maxSlot)
                        maxSlot = cell.Slot.Index;
                    instructions.Add(new LoadParameterInstruction(destination, cell.Slot));
                }
                else
                {
                    instructions.Add(new LoadConstantInstruction(destination, cell.Value));
                }
            }

            instructions.Add(new EphemeralInsertInstruction(cursor, outputRange));
        }

        var columnStart = instructions.Count + 1;
        var closeIndex = columnStart + width + 2;
        instructions.Add(new RewindCursorInstruction(cursor, new ProgramCounter(closeIndex)));
        for (var column = 0; column < width; column++)
            instructions.Add(new ColumnInstruction(cursor, column, new Register(column)));
        instructions.Add(new ResultRowInstruction(outputRange));
        instructions.Add(new NextInstruction(cursor, new ProgramCounter(columnStart)));
        instructions.Add(new CloseCursorInstruction(cursor));
        instructions.Add(new HaltInstruction());
        return (instructions, width, maxSlot + 1);
    }
}
