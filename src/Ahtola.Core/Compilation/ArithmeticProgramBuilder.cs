using Ahtola.Core.Execution;

namespace Ahtola.Core.Compilation;

/// <summary>
/// Lowers arithmetic expressions directly onto the resumable state machine, emitting runnable
/// <see cref="VdbeProgram"/>s whose <see cref="ArithmeticInstruction"/>s compute a result from register
/// operands. It is the arithmetic companion to <see cref="ScalarFunctionProgramBuilder"/> (function calls),
/// <see cref="ValuesProgramBuilder"/> (constant/parameter rows), and the scan lowering in
/// <see cref="SelectStatementCompiler"/> (a real cursor loop): an arithmetic expression executes as bytecode
/// that folds a materialized operand register block into one destination value, rather than the
/// tree-walking evaluator computing the whole expression outside the VDBE.
/// </summary>
/// <remarks>
/// <para>
/// The builder owns only the mechanical register layout and control flow; it never re-derives value
/// semantics. NULL propagation, integer/real typing, overflow, division/modulo by zero, and operand type
/// errors all live in <see cref="VdbeArithmetic.Evaluate"/>, exactly as the scan, join, aggregate, and
/// compound builders delegate their value semantics. Two source shapes are supported, each demonstrating a
/// distinct composition:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="BuildOverValues"/> loads a row of <see cref="ValuesCell"/> operands
///   (baked <c>LoadConstant</c> literals mixed with late-bound <c>LoadParameter</c> slots) and applies the
///   operator to them — composing arithmetic with <c>VALUES</c> and parameters, so the program re-runs with
///   fresh bindings after a <see cref="ResumableStatement.Reset"/>/<see cref="ResumableStatement.Rebind"/>
///   without recompilation.</description></item>
///   <item><description><see cref="BuildOverScan"/> opens a read cursor, reads operand (and optional
///   passthrough) columns from each scanned row, applies the operator, and emits the projection — composing
///   arithmetic with a base-table scan through the same cursor loop the SELECT compiler emits.</description></item>
/// </list>
/// <para>
/// The emitted programs are direct VDBE bytecode: they contain a real <see cref="ArithmeticInstruction"/>
/// the interpreter executes by evaluating the operator over a snapshot of the operand registers, not a
/// façade that defers to the evaluator. Wiring a parsed SQL arithmetic expression to these opcodes (operand
/// expression lowering, affinity/coercion, and routing) remains the database layer's job; this builder
/// stops at the runnable program.
/// </para>
/// </remarks>
public static class ArithmeticProgramBuilder
{
    /// <summary>
    /// Describes one output column for an arithmetic scan. Exactly one projection must be
    /// <see cref="ArithmeticResult"/>; every other projection reads one source column.
    /// </summary>
    public readonly record struct ScanProjection(bool IsArithmeticResult, int ColumnIndex)
    {
        public static ScanProjection ForColumn(int columnIndex) => new(false, columnIndex);

        public static ScanProjection ArithmeticResult() => new(true, 0);
    }

    /// <summary>
    /// Builds a source-less program that, for a single row of operand cells, applies
    /// <paramref name="op"/> to them and emits its result as a one-column result row. Constant cells emit
    /// <c>LoadConstant</c>; parameter cells emit <c>LoadParameter</c>, so a program with parameter operands
    /// re-executes with fresh bindings after a reset/rebind. The parameter-slot width is the highest slot
    /// referenced plus one (zero when no cell is a parameter).
    /// </summary>
    /// <param name="op">The arithmetic operator to apply. The operand count must equal its
    /// <see cref="VdbeArithmetic.Arity"/> (two for binary operators, one for the unary sign operators).</param>
    /// <param name="operands">The operand cells in operand order.</param>
    /// <returns>A runnable, cursor-less <see cref="VdbeProgram"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operands"/> is null.</exception>
    /// <exception cref="ArgumentException">The operand count does not match the operator's arity.</exception>
    public static VdbeProgram BuildOverValues(ArithmeticOperator op, IReadOnlyList<ValuesCell> operands)
    {
        ArgumentNullException.ThrowIfNull(operands);
        var arity = RequireArity(op, operands.Count);

        // Layout: operands occupy r[0..arity-1]; the operator writes its result into the register just past
        // them, which the single-column ResultRow then emits.
        var resultRegister = new Register(arity);
        var operandRange = new RegisterRange(new Register(0), arity);

        var instructions = new List<VdbeInstruction>(arity + 3);
        var maxSlot = -1;
        for (var index = 0; index < arity; index++)
        {
            var cell = operands[index];
            var destination = new Register(index);
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

        instructions.Add(new ArithmeticInstruction(resultRegister, op, operandRange));
        instructions.Add(new ResultRowInstruction(new RegisterRange(resultRegister, 1)));
        instructions.Add(new HaltInstruction());

        return new VdbeProgram(
            registerCount: arity + 1,
            cursorCount: 0,
            instructions,
            parameterSlotCount: maxSlot + 1);
    }

    /// <summary>
    /// Builds the same program as <see cref="BuildOverValues"/> wrapped as a <see cref="CompoundTerm"/> with
    /// an empty cursor-source list, so an arithmetic projection over constants/parameters can be sequenced
    /// directly by <see cref="CompoundProgramBuilder"/> (a source-less term iterates no cursors).
    /// </summary>
    public static CompoundTerm BuildOverValuesTerm(ArithmeticOperator op, IReadOnlyList<ValuesCell> operands)
        => new(BuildOverValues(op, operands), []);

    /// <summary>
    /// Builds a program that scans <paramref name="rows"/> and, for each row, applies <paramref name="op"/>
    /// to the row's <paramref name="operandColumns"/>, emitting the optional
    /// <paramref name="passthroughColumns"/> followed by the arithmetic result as the projection. It is a
    /// real cursor loop (<c>OpenReadCursor</c>, <c>Rewind</c>, <c>Column</c>…, <c>Arithmetic</c>,
    /// <c>ResultRow</c>, <c>Next</c>, <c>CloseCursor</c>, <c>Halt</c>), so it composes arithmetic with a
    /// base-table scan.
    /// </summary>
    /// <param name="op">The operator to apply per row. The operand-column count must equal its
    /// <see cref="VdbeArithmetic.Arity"/>.</param>
    /// <param name="tableName">The catalog name of the scanned table, surfaced for EXPLAIN.</param>
    /// <param name="columnCount">The number of columns the scanned cursor exposes.</param>
    /// <param name="operandColumns">The column ordinals whose values feed the operator, in operand order.</param>
    /// <param name="rows">The live rows the emitted cursor iterates.</param>
    /// <param name="passthroughColumns">Optional column ordinals emitted verbatim before the arithmetic
    /// result, e.g. a key column carried alongside a computed value.</param>
    /// <returns>A <see cref="CompoundTerm"/> pairing the program with its single cursor's row source.</returns>
    /// <exception cref="ArgumentNullException">A required reference argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="tableName"/> is empty, <paramref name="columnCount"/>
    /// is not positive, a referenced column is out of range, or the operand-column count does not match the
    /// operator's arity.</exception>
    public static CompoundTerm BuildOverScan(
        ArithmeticOperator op,
        string tableName,
        int columnCount,
        IReadOnlyList<int> operandColumns,
        IReadOnlyList<SqlValue[]> rows,
        IReadOnlyList<int>? passthroughColumns = null)
    {
        var passthrough = passthroughColumns ?? [];
        var projections = new ScanProjection[passthrough.Count + 1];
        for (var index = 0; index < passthrough.Count; index++)
            projections[index] = ScanProjection.ForColumn(passthrough[index]);
        projections[^1] = ScanProjection.ArithmeticResult();

        return BuildOverScanWithProjectionOrder(
            op,
            tableName,
            columnCount,
            operandColumns,
            rows,
            projections);
    }

    /// <summary>
    /// Builds an arithmetic scan whose direct-column projections and arithmetic result occur
    /// in the supplied output order, optionally filtering each source row before any
    /// projection expression runs. The projection list must contain exactly one arithmetic
    /// result and otherwise only source-column reads.
    /// </summary>
    public static CompoundTerm BuildOverScanWithProjectionOrder(
        ArithmeticOperator op,
        string tableName,
        int columnCount,
        IReadOnlyList<int> operandColumns,
        IReadOnlyList<SqlValue[]> rows,
        IReadOnlyList<ScanProjection> projections,
        VdbeRowPredicate? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(operandColumns);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(projections);
        if (tableName.Length == 0)
            throw new ArgumentException("A scanned table needs a name.", nameof(tableName));
        if (columnCount <= 0)
            throw new ArgumentException("A scanned table needs a positive column count.", nameof(columnCount));
        if (projections.Count == 0)
            throw new ArgumentException("An arithmetic scan needs at least one output projection.", nameof(projections));

        var arity = RequireArity(op, operandColumns.Count);
        RequireColumnsInRange(operandColumns, columnCount, nameof(operandColumns));

        var resultIndex = -1;
        for (var index = 0; index < projections.Count; index++)
        {
            if (projections[index].IsArithmeticResult)
            {
                if (resultIndex >= 0)
                    throw new ArgumentException("An arithmetic scan needs exactly one arithmetic result.", nameof(projections));

                resultIndex = index;
            }
            else
            {
                RequireColumnsInRange([projections[index].ColumnIndex], columnCount, nameof(projections));
            }
        }

        if (resultIndex < 0)
            throw new ArgumentException("An arithmetic scan needs exactly one arithmetic result.", nameof(projections));

        // Output registers mirror the requested projection order. Operand reads occupy a
        // scratch block after that output range, so computing the arithmetic result cannot
        // overwrite an operand even when the result appears between direct projections.
        var resultRegister = new Register(resultIndex);
        var operandStart = new Register(projections.Count);
        var operandRange = new RegisterRange(operandStart, arity);
        var outputRange = new RegisterRange(new Register(0), projections.Count);
        var registerCount = projections.Count + arity;

        var cursor = new Cursor(0);
        const int loopStart = 2;
        var filterCount = predicate is null ? 0 : 1;
        var directColumnCount = projections.Count(projection => !projection.IsArithmeticResult);
        var bodyLength = directColumnCount + arity + 2; // column reads + arithmetic + result row
        var nextAddr = loopStart + filterCount + bodyLength;
        var closeAddr = nextAddr + 1;

        var instructions = new List<VdbeInstruction>(closeAddr + 2)
        {
            new OpenReadCursorInstruction(cursor, tableName, columnCount),
            new RewindCursorInstruction(cursor, new ProgramCounter(closeAddr)),
        };

        if (predicate is not null)
        {
            instructions.Add(new FilterInstruction(
                cursor,
                predicate,
                new ProgramCounter(nextAddr),
                $"skip row when WHERE is false, goto {nextAddr}"));
        }

        for (var index = 0; index < projections.Count; index++)
        {
            var projection = projections[index];
            if (!projection.IsArithmeticResult)
                instructions.Add(new ColumnInstruction(cursor, projection.ColumnIndex, new Register(index)));
        }

        for (var i = 0; i < arity; i++)
            instructions.Add(new ColumnInstruction(cursor, operandColumns[i], new Register(projections.Count + i)));

        instructions.Add(new ArithmeticInstruction(resultRegister, op, operandRange));
        instructions.Add(new ResultRowInstruction(outputRange));
        instructions.Add(new NextInstruction(cursor, new ProgramCounter(loopStart)));
        instructions.Add(new CloseCursorInstruction(cursor));
        instructions.Add(new HaltInstruction());

        var program = new VdbeProgram(registerCount, cursorCount: 1, instructions);
        return new CompoundTerm(program, [new VdbeCursorSource(rows)]);
    }

    private static int RequireArity(ArithmeticOperator op, int operandCount)
    {
        var arity = VdbeArithmetic.Arity(op);
        if (operandCount != arity)
        {
            throw new ArgumentException(
                $"Arithmetic operator '{VdbeArithmetic.Symbol(op)}' has arity {arity} but was given {operandCount} operand(s).",
                nameof(operandCount));
        }

        return arity;
    }

    private static void RequireColumnsInRange(IReadOnlyList<int> columns, int columnCount, string parameterName)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            if (column < 0 || column >= columnCount)
            {
                throw new ArgumentException(
                    $"Column {column} is outside the scanned table's {columnCount} column(s).",
                    parameterName);
            }
        }
    }
}
