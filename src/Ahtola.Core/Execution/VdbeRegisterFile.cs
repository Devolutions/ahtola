namespace Ahtola.Core.Execution;

/// <summary>
/// The interpreter's register file: the scalar <see cref="SqlValue"/> array every opcode reads and writes,
/// plus a parallel slot per register that can hold a <see cref="VdbeRecordValue"/> tuple built by
/// <see cref="MakeRecordInstruction"/>.
/// </summary>
/// <remarks>
/// <para>
/// Keeping the two views in one object is what makes record invalidation total rather than best-effort.
/// The indexer's setter is the only way an opcode writes a scalar, and it always clears the register's
/// record slot, so a register can never appear to hold a stale tuple after a scalar overwrote it. A record
/// is written through <see cref="SetRecord"/>, which parks <see cref="SqlValue.Null"/> in the scalar view
/// so scalar readers — including the public
/// <see cref="ResumableStatement.GetRegister(Register)"/> — observe NULL instead of a fabricated blob.
/// </para>
/// <para>
/// Records are immutable, so copying a slot or snapshotting the file into a savepoint frame is a reference
/// copy that can never alias a later mutation.
/// </para>
/// </remarks>
internal sealed class VdbeRegisterFile
{
    internal VdbeRegisterFile(int registerCount)
    {
        if (registerCount < 0)
            throw new ArgumentOutOfRangeException(nameof(registerCount));

        Scalars = new SqlValue[registerCount];
        Records = new VdbeRecordValue?[registerCount];
    }

    /// <summary>The scalar view, handed to the transaction context for snapshot/restore.</summary>
    internal SqlValue[] Scalars { get; }

    /// <summary>The parallel record view, handed to the transaction context for snapshot/restore.</summary>
    internal VdbeRecordValue?[] Records { get; }

    internal int Length => Scalars.Length;

    /// <summary>Reads or writes the scalar value of a register. Writing clears any record it held.</summary>
    internal SqlValue this[int index]
    {
        get => Scalars[index];
        set
        {
            Scalars[index] = value;
            Records[index] = null;
        }
    }

    /// <summary>Writes a record into a register, leaving its scalar view as NULL.</summary>
    internal void SetRecord(int index, VdbeRecordValue record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Scalars[index] = SqlValue.Null;
        Records[index] = record;
    }

    /// <summary>The record a register holds, or <see langword="null"/> when it holds a scalar.</summary>
    internal VdbeRecordValue? GetRecord(int index) => Records[index];

    /// <summary>Copies one register onto another, preserving whether it holds a scalar or a record.</summary>
    internal void CopySlot(int source, int destination)
    {
        Scalars[destination] = Scalars[source];
        Records[destination] = Records[source];
    }

    /// <summary>Writes a run of scalars into the file, clearing the records they overwrite.</summary>
    internal void CopyFrom(SqlValue[] source, int sourceIndex, int destinationIndex, int count)
    {
        Array.Copy(source, sourceIndex, Scalars, destinationIndex, count);
        Array.Clear(Records, destinationIndex, count);
    }

    /// <summary>Reads a run of scalars out of the file. Registers holding records read as NULL.</summary>
    internal void CopyTo(int sourceIndex, SqlValue[] destination, int destinationIndex, int count)
        => Array.Copy(Scalars, sourceIndex, destination, destinationIndex, count);

    /// <summary>Resets every register to NULL and drops every record.</summary>
    internal void Clear()
    {
        Array.Clear(Scalars);
        Array.Clear(Records);
    }
}
