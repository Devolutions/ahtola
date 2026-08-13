using Ahtola.Core.Execution;

namespace Ahtola.Core.Compilation;

/// <summary>
/// One RETURNING output expression, lowered by <see cref="DmlStatementCompiler"/> into the
/// instructions that compute it into an output register after the row is mutated (INSERT/UPDATE) or
/// before it is deleted (DELETE). It is the compiled-DML analogue of the SELECT compiler's projection
/// descriptors, but expressed as a small expression tree so a RETURNING item can be more than a bare
/// column/rowid/constant: the three leaf kinds
/// (<see cref="Column"/>, <see cref="RowId"/>, <see cref="Constant"/>) plus the
/// <see cref="Arithmetic(ArithmeticOperator, DmlReturningExpression[])"/> node together realize the
/// full arithmetic expression family over the affected row.
/// </summary>
/// <remarks>
/// <para>The tree is composable: an arithmetic node's operands are themselves
/// <see cref="DmlReturningExpression"/>s, so nested arithmetic such as <c>(a + b) * 2</c> lowers to a
/// chain of <see cref="ArithmeticInstruction"/>s over the row's columns/rowid and folded constants. The
/// leaves read the same cursor the mutation opcode wrote (INSERT/UPDATE) or the pre-delete scan row
/// (DELETE), so the projection observes the correct row snapshot; the value, NULL-propagation, and error
/// semantics are owned entirely by <see cref="VdbeArithmetic"/>, keeping the compiled path byte-identical
/// to the arithmetic the SELECT/VALUES routes already emit.</para>
/// <para>The <see cref="Arithmetic(ArithmeticOperator, DmlReturningExpression[])"/> factory validates its
/// shape at construction time — the operator must be defined, the operand count must equal the operator's
/// <see cref="VdbeArithmetic.Arity"/>, and no operand may be null — so a malformed descriptor cannot reach
/// lowering; out-of-range column references are caught later as invalid bytecode by
/// <see cref="VdbeProgram.Validate"/>.</para>
/// </remarks>
public abstract record DmlReturningExpression
{
    private protected DmlReturningExpression()
    {
    }

    /// <summary>Reads column <paramref name="columnIndex"/> of the affected row (post-mutation for
    /// INSERT/UPDATE, pre-delete for DELETE).</summary>
    public static DmlReturningExpression Column(int columnIndex)
    {
        if (columnIndex < 0)
            throw new StatementCompilationException($"RETURNING column index {columnIndex} must be non-negative.");

        return new DmlColumnReturning(columnIndex);
    }

    /// <summary>Reads the affected row's rowid (post-mutation for INSERT/UPDATE, pre-delete for DELETE).</summary>
    public static DmlReturningExpression RowId() => DmlRowIdReturning.Instance;

    /// <summary>Emits a folded compile-time constant, unchanged per affected row.</summary>
    public static DmlReturningExpression Constant(SqlValue value) => new DmlConstantReturning(value);

    /// <summary>Applies <paramref name="op"/> to the results of <paramref name="operands"/>, each itself a
    /// <see cref="DmlReturningExpression"/>. The operand count must equal the operator's
    /// <see cref="VdbeArithmetic.Arity"/> (two for the binary operators, one for the unary sign
    /// operators).</summary>
    /// <exception cref="StatementCompilationException"><paramref name="op"/> is undefined, the operand
    /// count disagrees with the operator's arity, or an operand is null.</exception>
    public static DmlReturningExpression Arithmetic(ArithmeticOperator op, params DmlReturningExpression[] operands)
    {
        ArgumentNullException.ThrowIfNull(operands);
        return Arithmetic(op, (IReadOnlyList<DmlReturningExpression>)operands.ToArray());
    }

    /// <inheritdoc cref="Arithmetic(ArithmeticOperator, DmlReturningExpression[])"/>
    public static DmlReturningExpression Arithmetic(
        ArithmeticOperator op,
        IReadOnlyList<DmlReturningExpression> operands)
    {
        ArgumentNullException.ThrowIfNull(operands);
        if (!Enum.IsDefined(op))
            throw new StatementCompilationException($"RETURNING arithmetic uses an undefined operator {op}.");

        var arity = VdbeArithmetic.Arity(op);
        if (operands.Count != arity)
        {
            throw new StatementCompilationException(
                $"RETURNING arithmetic operator '{VdbeArithmetic.Symbol(op)}' has arity {arity} but was given {operands.Count} operand(s).");
        }

        var copied = new DmlReturningExpression[operands.Count];
        for (var index = 0; index < operands.Count; index++)
        {
            copied[index] = operands[index]
                ?? throw new StatementCompilationException(
                    $"RETURNING arithmetic operator '{VdbeArithmetic.Symbol(op)}' has a null operand at position {index}.");
        }

        return new DmlArithmeticReturning(op, Array.AsReadOnly(copied));
    }
}

/// <summary>A RETURNING leaf that reads a column of the affected row.</summary>
public sealed record DmlColumnReturning(int ColumnIndex) : DmlReturningExpression;

/// <summary>A RETURNING leaf that reads the affected row's rowid.</summary>
public sealed record DmlRowIdReturning : DmlReturningExpression
{
    internal static readonly DmlRowIdReturning Instance = new();
}

/// <summary>A RETURNING leaf that emits a folded compile-time constant.</summary>
public sealed record DmlConstantReturning(SqlValue Value) : DmlReturningExpression;

/// <summary>A RETURNING node that combines its operand expressions with an arithmetic operator.</summary>
public sealed record DmlArithmeticReturning(
    ArithmeticOperator Operator,
    IReadOnlyList<DmlReturningExpression> Operands) : DmlReturningExpression;
