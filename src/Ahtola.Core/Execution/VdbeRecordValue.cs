namespace Ahtola.Core.Execution;

/// <summary>
/// An ordered tuple held in a VDBE register, produced by <see cref="MakeRecordInstruction"/> and consumed
/// by the record-aware opcodes. It is the managed stand-in for the packed record blob SQLite's
/// <c>MakeRecord</c> builds and Turso models as <c>Value::Record</c>.
/// </summary>
/// <remarks>
/// <para>
/// A record is deliberately <b>not</b> a <see cref="SqlValue"/>. Encoding one as a blob would make it
/// indistinguishable from a user blob — <c>typeof()</c>, comparisons, and parameter round-trips would all
/// lie — and adding a public <see cref="SqlValueKind"/> would leak an interpreter-internal representation
/// into every scalar API. Instead a record lives in a parallel register slot: the scalar view of that
/// register reads as <see cref="SqlValue.Null"/>, and only opcodes that understand records see the tuple.
/// </para>
/// <para>
/// Instances are immutable, so copying a record between registers and snapshotting one into a savepoint
/// frame are both reference copies that can never alias a later mutation.
/// </para>
/// </remarks>
internal sealed class VdbeRecordValue
{
    private readonly SqlValue[] _values;

    /// <summary>Takes ownership of <paramref name="values"/>; callers must not retain the array.</summary>
    internal VdbeRecordValue(SqlValue[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = values;
    }

    /// <summary>Copies <paramref name="values"/> into a new record.</summary>
    public static VdbeRecordValue FromValues(IReadOnlyList<SqlValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var copy = new SqlValue[values.Count];
        for (var index = 0; index < copy.Length; index++)
            copy[index] = values[index];
        return new VdbeRecordValue(copy);
    }

    /// <summary>The number of columns the record carries.</summary>
    public int Count => _values.Length;

    public SqlValue this[int index] => _values[index];

    /// <summary>Copies the record out as a fresh array, so callers cannot mutate the record.</summary>
    public SqlValue[] ToArray() => (SqlValue[])_values.Clone();

    public override string ToString()
        => $"record[{_values.Length}]";
}
