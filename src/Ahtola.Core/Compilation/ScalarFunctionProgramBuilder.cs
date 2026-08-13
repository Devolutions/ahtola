using Ahtola.Core.Execution;

namespace Ahtola.Core.Compilation;

/// <summary>
/// Lowers scalar-function evaluation directly onto the resumable state machine, emitting runnable
/// <see cref="VdbeProgram"/>s whose <see cref="FunctionInstruction"/>s invoke caller-supplied
/// <see cref="VdbeScalarFunction"/> delegates. It is the scalar-expression companion to
/// <see cref="ValuesProgramBuilder"/> (constant/parameter rows) and the scan lowering in
/// <see cref="SelectStatementCompiler"/> (a real cursor loop): a function call over a projection executes
/// as bytecode that folds a materialized argument register block into one destination value, rather than
/// the tree-walking evaluator computing the whole expression outside the VDBE.
/// </summary>
/// <remarks>
/// <para>
/// The builder owns only the mechanical register layout and control flow; it never inspects SQL types.
/// Argument counting, NULL propagation, and the result kind live entirely in the <see cref="VdbeScalarFunction"/>
/// delegate, exactly as the scan, join, aggregate, and compound builders delegate their value semantics.
/// Two source shapes are supported and each demonstrates a distinct composition:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="BuildOverValues"/> loads a source-less row of
///   <see cref="ValuesCell"/> arguments (baked <c>LoadConstant</c> literals mixed with late-bound
///   <c>LoadParameter</c> slots) and applies the function to them — composing scalar functions with
///   <c>VALUES</c> and parameters, so the program re-runs with fresh bindings after a
///   <see cref="ResumableStatement.Reset"/>/<see cref="ResumableStatement.Rebind"/> without recompilation.</description></item>
///   <item><description><see cref="BuildOverScan"/> opens a read cursor, reads argument (and optional
///   passthrough) columns from each scanned row, applies the function, and emits the projection — composing
///   scalar functions with a base-table scan through the same cursor loop the SELECT compiler emits.</description></item>
/// </list>
/// <para>
/// The emitted programs are direct VDBE bytecode: they contain a real <see cref="FunctionInstruction"/>
/// that the interpreter executes by invoking the delegate over a snapshot of the argument registers, not a
/// façade that defers to the evaluator. Wiring a parsed SQL function call to a resolved
/// <see cref="VdbeScalarFunction"/> (builtin lookup, user-function registration, argument expression
/// lowering, and routing) remains the database layer's job; this builder stops at the runnable program.
/// </para>
/// </remarks>
public static class ScalarFunctionProgramBuilder
{
    /// <summary>
    /// Builds a source-less program that, for a single row of argument cells, applies
    /// <paramref name="function"/> to them and emits its result as a one-column result row. Constant cells
    /// emit <c>LoadConstant</c>; parameter cells emit <c>LoadParameter</c>, so a program with parameter
    /// arguments re-executes with fresh bindings after a reset/rebind. The parameter-slot width is the
    /// highest slot referenced plus one (zero when no cell is a parameter).
    /// </summary>
    /// <param name="function">The scalar function to apply. When it declares a fixed
    /// <see cref="VdbeScalarFunction.Arity"/>, the argument count must equal it.</param>
    /// <param name="arguments">The argument cells in argument order.</param>
    /// <returns>A runnable, cursor-less <see cref="VdbeProgram"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="function"/> or <paramref name="arguments"/>
    /// is null, or a cell list entry is null.</exception>
    /// <exception cref="ArgumentException">The argument count does not match a fixed-arity function.</exception>
    public static VdbeProgram BuildOverValues(
        VdbeScalarFunction function,
        IReadOnlyList<ValuesCell> arguments)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(arguments);
        RequireArity(function, arguments.Count);

        var argumentCount = arguments.Count;

        // Layout: arguments occupy r[0..argumentCount-1]; the function writes its result into the register
        // just past them, which the single-column ResultRow then emits.
        var resultRegister = new Register(argumentCount);
        var argumentRange = new RegisterRange(new Register(0), argumentCount);

        var instructions = new List<VdbeInstruction>(argumentCount + 3);
        var maxSlot = -1;
        for (var column = 0; column < argumentCount; column++)
        {
            var cell = arguments[column];
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

        instructions.Add(new FunctionInstruction(resultRegister, function, argumentRange));
        instructions.Add(new ResultRowInstruction(new RegisterRange(resultRegister, 1)));
        instructions.Add(new HaltInstruction());

        return new VdbeProgram(
            registerCount: argumentCount + 1,
            cursorCount: 0,
            instructions,
            parameterSlotCount: maxSlot + 1);
    }

    /// <summary>
    /// Builds the same program as <see cref="BuildOverValues"/> wrapped as a <see cref="CompoundTerm"/> with
    /// an empty cursor-source list, so a scalar-function projection over constants/parameters can be
    /// sequenced directly by <see cref="CompoundProgramBuilder"/> (a source-less term iterates no cursors).
    /// </summary>
    public static CompoundTerm BuildOverValuesTerm(
        VdbeScalarFunction function,
        IReadOnlyList<ValuesCell> arguments)
        => new(BuildOverValues(function, arguments), []);

    /// <summary>
    /// Builds a program that scans <paramref name="rows"/> and, for each row, applies
    /// <paramref name="function"/> to the row's <paramref name="argumentColumns"/>, emitting the optional
    /// <paramref name="passthroughColumns"/> followed by the function result as the projection. It is a real
    /// cursor loop (<c>OpenReadCursor</c>, <c>Rewind</c>, <c>Column</c>…, <c>Function</c>, <c>ResultRow</c>,
    /// <c>Next</c>, <c>CloseCursor</c>, <c>Halt</c>), so it composes scalar functions with a base-table scan.
    /// </summary>
    /// <param name="function">The scalar function to apply per row. When it declares a fixed
    /// <see cref="VdbeScalarFunction.Arity"/>, the argument-column count must equal it.</param>
    /// <param name="tableName">The catalog name of the scanned table, surfaced for EXPLAIN.</param>
    /// <param name="columnCount">The number of columns the scanned cursor exposes.</param>
    /// <param name="argumentColumns">The column ordinals whose values feed the function, in argument order.</param>
    /// <param name="rows">The live rows the emitted cursor iterates.</param>
    /// <param name="passthroughColumns">Optional column ordinals emitted verbatim before the function
    /// result, e.g. a key column carried alongside a computed value.</param>
    /// <param name="predicate">An optional per-row filter evaluated before the function arguments are read.</param>
    /// <returns>A <see cref="CompoundTerm"/> pairing the program with its single cursor's row source.</returns>
    /// <exception cref="ArgumentNullException">A required reference argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="tableName"/> is empty, <paramref name="columnCount"/>
    /// is not positive, a referenced column is out of range, or the argument-column count does not match a
    /// fixed-arity function.</exception>
    public static CompoundTerm BuildOverScan(
        VdbeScalarFunction function,
        string tableName,
        int columnCount,
        IReadOnlyList<int> argumentColumns,
        IReadOnlyList<SqlValue[]> rows,
        IReadOnlyList<int>? passthroughColumns = null,
        VdbeRowPredicate? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(argumentColumns);
        ArgumentNullException.ThrowIfNull(rows);
        if (tableName.Length == 0)
            throw new ArgumentException("A scanned table needs a name.", nameof(tableName));
        if (columnCount <= 0)
            throw new ArgumentException("A scanned table needs a positive column count.", nameof(columnCount));

        RequireArity(function, argumentColumns.Count);
        var passthrough = passthroughColumns ?? [];
        RequireColumnsInRange(passthrough, columnCount, nameof(passthroughColumns));
        RequireColumnsInRange(argumentColumns, columnCount, nameof(argumentColumns));

        var passthroughCount = passthrough.Count;
        var argumentCount = argumentColumns.Count;

        // Layout: passthrough columns fill the output prefix r[0..passthroughCount-1]; the function result
        // occupies r[passthroughCount] (the final output column); the arguments are read into the scratch
        // block r[passthroughCount+1 .. passthroughCount+argumentCount], which sits outside both the result
        // register and the emitted output range, so writing the result never overlaps an argument read.
        var resultRegister = new Register(passthroughCount);
        var argumentStart = new Register(passthroughCount + 1);
        var argumentRange = new RegisterRange(argumentStart, argumentCount);
        var outputRange = new RegisterRange(new Register(0), passthroughCount + 1);
        var registerCount = passthroughCount + 1 + argumentCount;

        var cursor = new Cursor(0);
        const int loopStart = 2;
        var filterCount = predicate is null ? 0 : 1;
        var bodyLength = passthroughCount + argumentCount + 2; // column reads + function + result row
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

        for (var j = 0; j < passthroughCount; j++)
            instructions.Add(new ColumnInstruction(cursor, passthrough[j], new Register(j)));

        for (var i = 0; i < argumentCount; i++)
            instructions.Add(new ColumnInstruction(cursor, argumentColumns[i], new Register(passthroughCount + 1 + i)));

        instructions.Add(new FunctionInstruction(resultRegister, function, argumentRange));
        instructions.Add(new ResultRowInstruction(outputRange));
        instructions.Add(new NextInstruction(cursor, new ProgramCounter(loopStart)));
        instructions.Add(new CloseCursorInstruction(cursor));
        instructions.Add(new HaltInstruction());

        var program = new VdbeProgram(registerCount, cursorCount: 1, instructions);
        return new CompoundTerm(program, [new VdbeCursorSource(rows)]);
    }

    private static void RequireArity(VdbeScalarFunction function, int argumentCount)
    {
        if (function.Arity is { } arity && arity != argumentCount)
        {
            throw new ArgumentException(
                $"Scalar function '{function.Name}' has arity {arity} but was given {argumentCount} argument(s).");
        }
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
