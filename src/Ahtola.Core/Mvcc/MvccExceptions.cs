namespace Ahtola.Core.Mvcc;

/// <summary>
/// First-committer-wins write-write conflict on the MVCC path. Distinct from
/// <see cref="EmbeddedBusyException"/> so callers can choose retry policy.
/// Mirrors Turso <c>LimboError::WriteWriteConflict</c>.
/// </summary>
public sealed class EmbeddedWriteWriteConflictException : EmbeddedSqlException
{
    public EmbeddedWriteWriteConflictException()
        : base("write-write conflict")
    {
    }

    public EmbeddedWriteWriteConflictException(string message)
        : base(message)
    {
    }
}
