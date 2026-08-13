namespace Ahtola.Core.Execution;

/// <summary>
/// The arithmetic operation an <see cref="ArithmeticInstruction"/> applies to its register operands. Each
/// operator has a fixed arity — binary operators consume two operand registers and unary operators consume
/// one — which the program
/// validator pins against the instruction's operand range so an arity error can never reach execution.
/// </summary>
public enum ArithmeticOperator
{
    /// <summary>Addition (<c>a + b</c>).</summary>
    Add,

    /// <summary>Subtraction (<c>a - b</c>).</summary>
    Subtract,

    /// <summary>Multiplication (<c>a * b</c>).</summary>
    Multiply,

    /// <summary>Division (<c>a / b</c>). Integer operands divide with truncation toward zero; a zero
    /// divisor yields NULL rather than raising.</summary>
    Divide,

    /// <summary>Remainder (<c>a % b</c>). Computed on the integer truncations of its operands; a zero
    /// divisor yields NULL.</summary>
    Modulo,

    /// <summary>Bitwise AND (<c>a &amp; b</c>) over integer-coerced operands.</summary>
    BitwiseAnd,

    /// <summary>Bitwise OR (<c>a | b</c>) over integer-coerced operands.</summary>
    BitwiseOr,

    /// <summary>Signed left shift (<c>a &lt;&lt; b</c>) with SQLite's saturated shift count.</summary>
    ShiftLeft,

    /// <summary>Signed right shift (<c>a &gt;&gt; b</c>) with SQLite's saturated shift count.</summary>
    ShiftRight,

    /// <summary>Bitwise complement (<c>~a</c>) over an integer-coerced operand.</summary>
    BitwiseNot,

    /// <summary>Unary negation (<c>-a</c>), the arithmetic complement of <see cref="Identity"/>.</summary>
    Negate,

    /// <summary>Unary plus (<c>+a</c>): a storage-class-preserving no-op.</summary>
    Identity,
}

/// <summary>
/// Raised by <see cref="VdbeArithmetic.Evaluate"/> to signal an operand type incompatible with the selected
/// arithmetic operator.
/// It is the arithmetic sibling of <see cref="VdbeFunctionException"/>: the interpreter does not catch it,
/// so a failing <see cref="ArithmeticInstruction"/> propagates the exception out of the step with the
/// destination register left untouched, and a caller may catch this single shape to distinguish an operand
/// type error from a runtime fault. The family deliberately does <em>not</em> apply SQL numeric affinity to
/// text/blob operands; a compiler routing SQL arithmetic through these opcodes must materialize numeric
/// operands (or a coercion step) first.
/// </summary>
public sealed class VdbeArithmeticException : InvalidOperationException
{
    public VdbeArithmeticException(string message)
        : base(message)
    {
    }

    public VdbeArithmeticException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Evaluates the <see cref="ArithmeticOperator"/> family over already-materialized <see cref="SqlValue"/>
/// operands. It is the leaf value semantics an <see cref="ArithmeticInstruction"/> executes with, the
/// arithmetic analogue of a <see cref="VdbeScalarFunction"/> delegate: a pure mapping from an operand tuple
/// to one result value with no register, cursor, or program state. The interpreter snapshots the operand
/// registers before calling <see cref="Evaluate"/> and writes the result only on success, so a throwing
/// evaluation never publishes a half-computed value and a destination register may overlap an operand.
/// </summary>
/// <remarks>
/// <para>The result semantics mirror the tree-walking evaluator's numeric operators (and, for
/// <see cref="ArithmeticOperator.Modulo"/>, SQLite's <c>%</c>) exactly for numeric operands, but the
/// operators are computed directly here rather than by calling that evaluator:</para>
/// <list type="bullet">
///   <item><description><b>NULL</b> — any NULL operand yields NULL, short-circuiting before the remaining
///   operands' types are inspected.</description></item>
///   <item><description><b>Integer vs. real</b> — two integer operands compute in <see cref="long"/>; a real
///   operand promotes the operation to <see cref="double"/>.</description></item>
///   <item><description><b>Overflow</b> — an integer <see cref="ArithmeticOperator.Add"/>/<see cref="ArithmeticOperator.Subtract"/>/
///   <see cref="ArithmeticOperator.Multiply"/>/<see cref="ArithmeticOperator.Negate"/> that overflows <see cref="long"/> falls back to the real
///   result rather than wrapping or raising.</description></item>
///   <item><description><b>Division / modulo by zero</b> — a zero divisor yields NULL (never a raised
///   divide-by-zero); integer <c>long.MinValue / -1</c> yields the real magnitude and <c>x % -1</c> yields
///   zero, both avoiding the sole two's-complement overflow.</description></item>
///   <item><description><b>Type errors</b> — numeric operators require numbers, bitwise operators require
///   integers, and identity accepts every storage class; the family applies no SQL affinity coercion.</description></item>
/// </list>
/// </remarks>
public static class VdbeArithmetic
{
    /// <summary>The number of operand registers an <paramref name="op"/> consumes: two for the binary
    /// operators, one for unary operators.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="op"/> is not a defined operator.</exception>
    public static int Arity(ArithmeticOperator op) => op switch
    {
        ArithmeticOperator.Add
            or ArithmeticOperator.Subtract
            or ArithmeticOperator.Multiply
            or ArithmeticOperator.Divide
            or ArithmeticOperator.Modulo
            or ArithmeticOperator.BitwiseAnd
            or ArithmeticOperator.BitwiseOr
            or ArithmeticOperator.ShiftLeft
            or ArithmeticOperator.ShiftRight => 2,
        ArithmeticOperator.Negate
            or ArithmeticOperator.Identity
            or ArithmeticOperator.BitwiseNot => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unknown arithmetic operator."),
    };

    /// <summary>The infix/prefix symbol for <paramref name="op"/> surfaced by <c>EXPLAIN</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="op"/> is not a defined operator.</exception>
    public static string Symbol(ArithmeticOperator op) => op switch
    {
        ArithmeticOperator.Add => "+",
        ArithmeticOperator.Subtract => "-",
        ArithmeticOperator.Multiply => "*",
        ArithmeticOperator.Divide => "/",
        ArithmeticOperator.Modulo => "%",
        ArithmeticOperator.BitwiseAnd => "&",
        ArithmeticOperator.BitwiseOr => "|",
        ArithmeticOperator.ShiftLeft => "<<",
        ArithmeticOperator.ShiftRight => ">>",
        ArithmeticOperator.BitwiseNot => "~",
        ArithmeticOperator.Negate => "-",
        ArithmeticOperator.Identity => "+",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unknown arithmetic operator."),
    };

    /// <summary>
    /// Applies <paramref name="op"/> to <paramref name="operands"/> (in operand order) and returns the
    /// single result value.
    /// </summary>
    /// <param name="op">The arithmetic operation to apply.</param>
    /// <param name="operands">The operand tuple, whose length must equal <see cref="Arity"/> of
    /// <paramref name="op"/>. The interpreter passes a private snapshot, so the method may read it freely.</param>
    /// <returns>The computed value, preserving the operand storage class for
    /// <see cref="ArithmeticOperator.Identity"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operands"/> is null.</exception>
    /// <exception cref="VdbeArithmeticException">The operand count disagrees with the operator's arity, or a
    /// non-NULL operand is incompatible with the selected operator.</exception>
    public static SqlValue Evaluate(ArithmeticOperator op, SqlValue[] operands)
    {
        ArgumentNullException.ThrowIfNull(operands);
        var arity = Arity(op);
        if (operands.Length != arity)
        {
            throw new VdbeArithmeticException(
                $"Arithmetic operator '{Symbol(op)}' expects {arity} operand(s) but received {operands.Length}.");
        }

        // NULL propagates: any NULL operand yields NULL, short-circuiting before any other operand's type is
        // inspected (so '5 % NULL' and 'NULL % x'00'' are both NULL, not type errors).
        foreach (var operand in operands)
        {
            if (operand.Kind == SqlValueKind.Null)
                return SqlValue.Null;
        }

        return op switch
        {
            ArithmeticOperator.Add or ArithmeticOperator.Subtract or ArithmeticOperator.Multiply
                => Additive(op, operands[0], operands[1]),
            ArithmeticOperator.Divide => Divide(operands[0], operands[1]),
            ArithmeticOperator.Modulo => Modulo(operands[0], operands[1]),
            ArithmeticOperator.BitwiseAnd => Bitwise(operands[0], operands[1], and: true),
            ArithmeticOperator.BitwiseOr => Bitwise(operands[0], operands[1], and: false),
            ArithmeticOperator.ShiftLeft => Shift(operands[0], operands[1], left: true),
            ArithmeticOperator.ShiftRight => Shift(operands[0], operands[1], left: false),
            ArithmeticOperator.BitwiseNot => BitwiseNot(operands[0]),
            ArithmeticOperator.Negate => Negate(operands[0]),
            ArithmeticOperator.Identity => Identity(operands[0]),
            _ => throw new VdbeArithmeticException($"Unknown arithmetic operator {op}."),
        };
    }

    private static SqlValue Additive(ArithmeticOperator op, SqlValue left, SqlValue right)
    {
        RequireNumeric(left, op);
        RequireNumeric(right, op);
        if (left.Kind == SqlValueKind.Integer && right.Kind == SqlValueKind.Integer)
        {
            var a = left.AsInteger();
            var b = right.AsInteger();
            try
            {
                return SqlValue.Integer(op switch
                {
                    ArithmeticOperator.Add => checked(a + b),
                    ArithmeticOperator.Subtract => checked(a - b),
                    ArithmeticOperator.Multiply => checked(a * b),
                    _ => throw new VdbeArithmeticException($"Operator '{Symbol(op)}' is not additive."),
                });
            }
            catch (OverflowException)
            {
                // Integer overflow promotes to a real result rather than wrapping or raising.
                double x = a;
                double y = b;
                return SqlValue.Real(op switch
                {
                    ArithmeticOperator.Add => x + y,
                    ArithmeticOperator.Subtract => x - y,
                    ArithmeticOperator.Multiply => x * y,
                    _ => throw new VdbeArithmeticException($"Operator '{Symbol(op)}' is not additive."),
                });
            }
        }

        var l = ToReal(left);
        var r = ToReal(right);
        return SqlValue.Real(op switch
        {
            ArithmeticOperator.Add => l + r,
            ArithmeticOperator.Subtract => l - r,
            ArithmeticOperator.Multiply => l * r,
            _ => throw new VdbeArithmeticException($"Operator '{Symbol(op)}' is not additive."),
        });
    }

    private static SqlValue Divide(SqlValue left, SqlValue right)
    {
        RequireNumeric(left, ArithmeticOperator.Divide);
        RequireNumeric(right, ArithmeticOperator.Divide);
        if (left.Kind == SqlValueKind.Integer && right.Kind == SqlValueKind.Integer)
        {
            var dividend = left.AsInteger();
            var divisor = right.AsInteger();
            if (divisor == 0)
                return SqlValue.Null;
            // long.MinValue / -1 is the sole overflowing integer division; return its real magnitude.
            if (dividend == long.MinValue && divisor == -1)
                return SqlValue.Real(-(double)long.MinValue);

            return SqlValue.Integer(dividend / divisor);
        }

        var realDivisor = ToReal(right);
        if (realDivisor == 0.0)
            return SqlValue.Null;

        return SqlValue.Real(ToReal(left) / realDivisor);
    }

    private static SqlValue Modulo(SqlValue left, SqlValue right)
    {
        RequireNumeric(left, ArithmeticOperator.Modulo);
        RequireNumeric(right, ArithmeticOperator.Modulo);
        if (left.Kind == SqlValueKind.Integer && right.Kind == SqlValueKind.Integer)
        {
            var dividend = left.AsInteger();
            var divisor = right.AsInteger();
            if (divisor == 0)
                return SqlValue.Null;
            // x % -1 is mathematically zero; special-casing it avoids the long.MinValue % -1 overflow.
            if (divisor == -1)
                return SqlValue.Integer(0);

            return SqlValue.Integer(dividend % divisor);
        }

        // Real operands take the remainder of their integer truncations, mirroring SQLite's '%', and the
        // result is itself a real.
        var iA = ToInt64Truncating(ToReal(left));
        var iB = ToInt64Truncating(ToReal(right));
        if (iB == 0)
            return SqlValue.Null;
        if (iB == -1)
            return SqlValue.Real(0.0);

        return SqlValue.Real(iA % iB);
    }

    private static SqlValue Negate(SqlValue value)
    {
        RequireNumeric(value, ArithmeticOperator.Negate);
        if (value.Kind == SqlValueKind.Integer)
        {
            var x = value.AsInteger();
            try
            {
                return SqlValue.Integer(checked(0L - x));
            }
            catch (OverflowException)
            {
                // Negating long.MinValue overflows; promote to the real magnitude.
                return SqlValue.Real(0.0 - (double)x);
            }
        }

        return SqlValue.Real(0.0 - value.AsReal());
    }

    private static SqlValue Identity(SqlValue value)
    {
        return value;
    }

    private static SqlValue Bitwise(SqlValue left, SqlValue right, bool and)
    {
        var a = RequireInteger(left, and ? ArithmeticOperator.BitwiseAnd : ArithmeticOperator.BitwiseOr);
        var b = RequireInteger(right, and ? ArithmeticOperator.BitwiseAnd : ArithmeticOperator.BitwiseOr);
        return SqlValue.Integer(and ? a & b : a | b);
    }

    private static SqlValue BitwiseNot(SqlValue value)
        => SqlValue.Integer(~RequireInteger(value, ArithmeticOperator.BitwiseNot));

    private static SqlValue Shift(SqlValue value, SqlValue count, bool left)
    {
        var integer = RequireInteger(value, left ? ArithmeticOperator.ShiftLeft : ArithmeticOperator.ShiftRight);
        var shift = RequireInteger(count, left ? ArithmeticOperator.ShiftLeft : ArithmeticOperator.ShiftRight);
        if (shift < 0)
        {
            var reversed = shift == long.MinValue ? 64L : -shift;
            return SqlValue.Integer(left
                ? ShiftRight(integer, reversed)
                : ShiftLeft(integer, reversed));
        }

        return SqlValue.Integer(left ? ShiftLeft(integer, shift) : ShiftRight(integer, shift));
    }

    private static long ShiftLeft(long value, long count)
        => count >= 64 ? 0 : unchecked(value << (int)count);

    private static long ShiftRight(long value, long count)
        => count >= 64 ? value < 0 ? -1 : 0 : value >> (int)count;

    private static void RequireNumeric(SqlValue value, ArithmeticOperator op)
    {
        if (value.Kind is not (SqlValueKind.Integer or SqlValueKind.Real))
        {
            throw new VdbeArithmeticException(
                $"Arithmetic operator '{Symbol(op)}' requires a numeric operand but received a {value.Kind} value.");
        }
    }

    private static long RequireInteger(SqlValue value, ArithmeticOperator op)
    {
        if (value.Kind != SqlValueKind.Integer)
        {
            throw new VdbeArithmeticException(
                $"Arithmetic operator '{Symbol(op)}' requires an integer operand but received a {value.Kind} value.");
        }

        return value.AsInteger();
    }

    private static double ToReal(SqlValue value) => value.Kind switch
    {
        SqlValueKind.Integer => value.AsInteger(),
        SqlValueKind.Real => value.AsReal(),
        _ => throw new VdbeArithmeticException($"Cannot interpret a {value.Kind} value as a number."),
    };

    private static long ToInt64Truncating(double value)
    {
        if (double.IsNaN(value))
            return 0;

        var truncated = Math.Truncate(value);
        if (truncated >= long.MaxValue)
            return long.MaxValue;
        if (truncated <= long.MinValue)
            return long.MinValue;

        return (long)truncated;
    }
}
