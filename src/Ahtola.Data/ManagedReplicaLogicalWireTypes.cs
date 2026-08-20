namespace Ahtola;

/// <summary>
/// The sync protocol a remote database advertises for incremental pulls. This is a
/// persisted capability flag detected once per response, distinct from the per-response
/// <c>stream_kind</c>/<c>apply_mode</c> a specific pull-updates reply carries.
/// </summary>
internal enum RemotePullProtocol
{
    /// <summary>The client has not yet learned the remote's protocol.</summary>
    Unknown = 0,

    /// <summary>The remote supports only raw page streams.</summary>
    Pages = 1,

    /// <summary>The remote supports the MVCC logical pull protocol.</summary>
    MvccLogical = 2,
}

/// <summary>Logical operation kind decoded from the server's MVCC logical log.</summary>
internal enum ManagedReplicaLogicalOpType : byte
{
    Unspecified = 0,
    UpsertRow = 1,
    DeleteRow = 2,
    Schema = 3,
    UpdateHeader = 4,
}

/// <summary>Schema action represented by a logical schema operation.</summary>
internal enum ManagedReplicaLogicalSchemaAction : byte
{
    Unspecified = 0,
    Create = 1,
    Drop = 2,
    Refresh = 3,
    Alter = 4,
}

/// <summary>Type of schema object affected by a logical schema operation.</summary>
internal enum ManagedReplicaLogicalSchemaKind : byte
{
    Unspecified = 0,
    Table = 1,
    Index = 2,
    Trigger = 3,
    View = 4,
}

/// <summary>
/// One logical operation decoded from a portable MVCC logical-log frame. Only the fields
/// required by <see cref="OpType"/> are populated.
/// </summary>
internal sealed record ManagedReplicaLogicalOp(
    ManagedReplicaLogicalOpType OpType,
    string TableName,
    long RowId,
    byte[] Record,
    string Sql,
    int? UserVersion,
    int? ApplicationId,
    ManagedReplicaLogicalSchemaAction? SchemaAction,
    ManagedReplicaLogicalSchemaKind? SchemaKind,
    string SchemaName,
    ulong StableTableId)
{
    internal static ManagedReplicaLogicalOp Empty(ManagedReplicaLogicalOpType opType) => new(
        opType,
        TableName: string.Empty,
        RowId: 0,
        Record: [],
        Sql: string.Empty,
        UserVersion: null,
        ApplicationId: null,
        SchemaAction: null,
        SchemaKind: null,
        SchemaName: string.Empty,
        StableTableId: 0);
}

/// <summary>One committed MVCC transaction decoded from a portable MVCC logical-log frame.</summary>
internal sealed record ManagedReplicaLogicalTxn(
    ulong EndOffset,
    ulong CommitTs,
    IReadOnlyList<ManagedReplicaLogicalOp> Ops,
    string OriginClientId);

/// <summary>
/// One <c>MvccLogicalLogRangeProto</c> range describing a byte span of the server's logical
/// log, carried in tag 7 of a pull-updates response header.
/// </summary>
internal readonly record struct ManagedReplicaLogicalLogRange(
    ulong Generation,
    ulong StartOffset,
    ulong EndOffset,
    bool StartsWithHeader,
    byte[]? CrcSeed);
