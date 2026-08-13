using System.Globalization;

namespace Ahtola.Core.Execution;

/// <summary>
/// Raised when a <see cref="VdbeParameterBinding"/> cannot be assembled or applied because its slot
/// assignments are inconsistent with the program's parameter space: a slot bound twice (duplicate), a
/// slot outside the declared range (invalid), a slot left unbound (missing), or a binding whose width
/// does not match the program it is applied to.
/// </summary>
public sealed class VdbeParameterBindingException : InvalidOperationException
{
    public VdbeParameterBindingException(string message) : base(message)
    {
    }
}

/// <summary>
/// The immutable carrier of the late-bound parameter values a <see cref="ResumableStatement"/> reads
/// through <see cref="LoadParameterInstruction"/>. It is the binding half of the late-binding mechanism:
/// a program declares <see cref="VdbeProgram.ParameterSlotCount"/> dense slots <c>0..N-1</c>, and a
/// binding supplies exactly one value for each of them, so re-executing the same program with a different
/// binding rebinds every parameter without recompilation.
/// </summary>
/// <remarks>
/// <para>
/// A binding owns a private, fully populated value array and never exposes it, so it cannot be a mutable
/// caller array whose later writes would leak into an in-flight or already-completed execution. Values are
/// <see cref="SqlValue"/>s, which are themselves immutable (text is an immutable string; a blob is copied
/// on construction), so a binding is a genuinely frozen snapshot of the parameters at the moment it was
/// built.
/// </para>
/// <para>
/// Assembly is validated up front. The <see cref="Builder"/> rejects binding the same slot twice
/// (duplicate) and binding a slot outside <c>0..N-1</c> (invalid) as each assignment is made, and
/// <see cref="Builder.Build"/> rejects any slot left unbound (missing) — so a missing parameter is a hard
/// error at bind time rather than a silent NULL discovered mid-execution. <see cref="FromValues(System.Collections.Generic.IReadOnlyList{SqlValue})"/>
/// offers the same guarantee positionally, where supplying one value per slot makes duplicate and missing
/// assignments impossible by construction.
/// </para>
/// </remarks>
public sealed class VdbeParameterBinding
{
    private readonly SqlValue[] _values;

    private VdbeParameterBinding(SqlValue[] values) => _values = values;

    /// <summary>A binding for a program that declares no parameter slots.</summary>
    public static VdbeParameterBinding Empty { get; } = new([]);

    /// <summary>The number of slots this binding supplies, i.e. the
    /// <see cref="VdbeProgram.ParameterSlotCount"/> it satisfies.</summary>
    public int Count => _values.Length;

    /// <summary>Reads the bound value of <paramref name="slot"/>.</summary>
    /// <exception cref="VdbeParameterBindingException"><paramref name="slot"/> is outside the binding's
    /// slot range.</exception>
    public SqlValue Get(ParameterSlot slot)
    {
        if (slot.Index >= _values.Length)
        {
            throw new VdbeParameterBindingException(
                $"Parameter slot {slot.Index} is out of range for a binding with {_values.Length} slots.");
        }

        return _values[slot.Index];
    }

    /// <summary>Reads the bound value of the slot with the given index.</summary>
    public SqlValue this[int slotIndex] => Get(new ParameterSlot(slotIndex));

    /// <summary>
    /// Builds a binding positionally, taking one value per slot in slot order. Every slot is assigned
    /// exactly once, so no missing/duplicate/invalid slot is possible; the values are copied into private
    /// storage.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is null.</exception>
    public static VdbeParameterBinding FromValues(IReadOnlyList<SqlValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var copy = new SqlValue[values.Count];
        for (var index = 0; index < values.Count; index++)
            copy[index] = values[index];

        return new VdbeParameterBinding(copy);
    }

    /// <summary>Builds a binding positionally from an explicit value list, one value per slot in order.</summary>
    public static VdbeParameterBinding FromValues(params SqlValue[] values)
        => FromValues((IReadOnlyList<SqlValue>)(values ?? throw new ArgumentNullException(nameof(values))));

    /// <summary>Opens a <see cref="Builder"/> for a binding of <paramref name="slotCount"/> slots. Each
    /// slot must be bound exactly once before <see cref="Builder.Build"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slotCount"/> is negative.</exception>
    public static Builder CreateBuilder(int slotCount) => new(slotCount);

    /// <summary>
    /// Assembles a <see cref="VdbeParameterBinding"/> for a fixed number of slots by binding each slot
    /// exactly once. Binding a slot twice, or a slot outside the declared range, or leaving a slot unbound,
    /// throws a <see cref="VdbeParameterBindingException"/>.
    /// </summary>
    public sealed class Builder
    {
        private readonly SqlValue[] _values;
        private readonly bool[] _bound;
        private bool _built;

        internal Builder(int slotCount)
        {
            if (slotCount < 0)
                throw new ArgumentOutOfRangeException(nameof(slotCount));

            _values = new SqlValue[slotCount];
            _bound = new bool[slotCount];
        }

        /// <summary>Binds <paramref name="slot"/> to <paramref name="value"/>.</summary>
        /// <exception cref="VdbeParameterBindingException"><paramref name="slot"/> is out of range
        /// (invalid) or has already been bound (duplicate).</exception>
        public Builder Bind(ParameterSlot slot, SqlValue value)
        {
            ThrowIfBuilt();
            if (slot.Index >= _values.Length)
            {
                throw new VdbeParameterBindingException(
                    $"Cannot bind parameter slot {slot.Index}: the binding declares {_values.Length} slots (0..{_values.Length - 1}).");
            }

            if (_bound[slot.Index])
            {
                throw new VdbeParameterBindingException(
                    $"Parameter slot {slot.Index} is bound more than once.");
            }

            _values[slot.Index] = value;
            _bound[slot.Index] = true;
            return this;
        }

        /// <summary>Binds the slot with the given index to <paramref name="value"/>.</summary>
        public Builder Bind(int slotIndex, SqlValue value) => Bind(new ParameterSlot(slotIndex), value);

        /// <summary>Finalizes the binding, requiring every slot to have been bound.</summary>
        /// <exception cref="VdbeParameterBindingException">One or more slots were left unbound (missing).</exception>
        public VdbeParameterBinding Build()
        {
            ThrowIfBuilt();
            var missing = new List<int>();
            for (var index = 0; index < _bound.Length; index++)
            {
                if (!_bound[index])
                    missing.Add(index);
            }

            if (missing.Count > 0)
            {
                var slots = string.Join(", ", missing.Select(index => index.ToString(CultureInfo.InvariantCulture)));
                throw new VdbeParameterBindingException(
                    $"Parameter slot{(missing.Count == 1 ? string.Empty : "s")} {slots} left unbound.");
            }

            _built = true;

            // Hand the completed snapshot to the binding by copy so a reused builder reference can never
            // mutate an already-published binding.
            return new VdbeParameterBinding((SqlValue[])_values.Clone());
        }

        private void ThrowIfBuilt()
        {
            if (_built)
                throw new VdbeParameterBindingException("This binding builder has already been built.");
        }
    }
}
