using Ahtola.Core.Execution;

namespace Ahtola.Core.Compilation;

/// <summary>
/// One cell of a <c>VALUES</c> row for <see cref="ValuesProgramBuilder"/>: either a compile-time constant
/// or a reference to a late-bound parameter slot. It is the input that lets the builder emit a program
/// mixing <see cref="LoadConstantInstruction"/> (baked literals) and <see cref="LoadParameterInstruction"/>
/// (deferred parameters), so a <c>VALUES (1, ?0)</c> constructor can re-execute with different bindings
/// without recompilation while its literal cells stay baked.
/// </summary>
/// <remarks>
/// A cell carries no SQL semantics: a constant already holds a resolved <see cref="SqlValue"/>, and a
/// parameter cell holds only a <see cref="ParameterSlot"/> index. Resolving an expression to a literal, or
/// mapping a SQL placeholder (<c>?n</c>, <c>:name</c>, …) to a slot, remains the caller's job — exactly as
/// the builder delegates every other value decision.
/// </remarks>
public readonly struct ValuesCell : IEquatable<ValuesCell>
{
    private readonly SqlValue _value;
    private readonly ParameterSlot _slot;

    private ValuesCell(bool isParameter, SqlValue value, ParameterSlot slot)
    {
        IsParameter = isParameter;
        _value = value;
        _slot = slot;
    }

    /// <summary>Whether this cell defers to a late-bound parameter slot rather than a baked constant.</summary>
    public bool IsParameter { get; }

    /// <summary>A constant cell holding a resolved value, emitted as <c>LoadConstant</c>.</summary>
    public static ValuesCell Constant(SqlValue value) => new(false, value, default);

    /// <summary>A parameter cell referencing <paramref name="slot"/>, emitted as <c>LoadParameter</c>.</summary>
    public static ValuesCell Parameter(ParameterSlot slot) => new(true, default, slot);

    /// <summary>A parameter cell referencing the slot with the given index.</summary>
    public static ValuesCell Parameter(int slotIndex) => Parameter(new ParameterSlot(slotIndex));

    /// <summary>The constant value of a constant cell.</summary>
    /// <exception cref="InvalidOperationException">This cell is a parameter cell.</exception>
    public SqlValue Value => IsParameter
        ? throw new InvalidOperationException("A parameter cell has no constant value.")
        : _value;

    /// <summary>The parameter slot of a parameter cell.</summary>
    /// <exception cref="InvalidOperationException">This cell is a constant cell.</exception>
    public ParameterSlot Slot => IsParameter
        ? _slot
        : throw new InvalidOperationException("A constant cell has no parameter slot.");

    public bool Equals(ValuesCell other)
        => IsParameter == other.IsParameter
            && (IsParameter ? _slot == other._slot : _value.Equals(other._value));

    public override bool Equals(object? obj) => obj is ValuesCell cell && Equals(cell);

    public override int GetHashCode()
        => IsParameter ? HashCode.Combine(true, _slot) : HashCode.Combine(false, _value);

    public static bool operator ==(ValuesCell left, ValuesCell right) => left.Equals(right);

    public static bool operator !=(ValuesCell left, ValuesCell right) => !left.Equals(right);
}
