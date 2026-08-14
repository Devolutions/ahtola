namespace Ahtola;

/// <summary>
/// Describes the local replica operation that the remote server rejected.
/// </summary>
public enum AhtolaReplicaConflictKind
{
    /// <summary>
    /// The remote response could not be associated with a replayed local operation.
    /// </summary>
    Unknown,

    /// <summary>
    /// A replayed row insert, update, or delete conflicted.
    /// </summary>
    RowWrite,

    /// <summary>
    /// A replayed schema-changing statement conflicted.
    /// </summary>
    SchemaChange,
}

/// <summary>
/// Indicates that the server rejected a managed replica push because its state conflicts with
/// locally committed journal changes. The journal is retained so the application can resolve
/// the conflict explicitly; synchronization never rebases changes automatically.
/// </summary>
public sealed class AhtolaReplicaConflictException : AhtolaException
{
    /// <summary>
    /// Initializes a conflict exception reported by the remote server.
    /// </summary>
    public AhtolaReplicaConflictException(
        string message,
        string? remoteErrorCode = null,
        AhtolaReplicaConflictKind conflictKind = AhtolaReplicaConflictKind.Unknown,
        long? localChangeSequence = null)
        : base(message)
    {
        RemoteErrorCode = remoteErrorCode;
        ConflictKind = conflictKind;
        LocalChangeSequence = localChangeSequence;
    }

    /// <summary>
    /// Gets the optional Hrana error code reported by the server.
    /// </summary>
    public string? RemoteErrorCode { get; }

    /// <summary>
    /// Gets the kind of local operation associated with the conflicting replay step.
    /// </summary>
    public AhtolaReplicaConflictKind ConflictKind { get; }

    /// <summary>
    /// Gets the durable local journal sequence associated with the conflicting replay step,
    /// when the server reported a step-specific batch error.
    /// </summary>
    public long? LocalChangeSequence { get; }
}
