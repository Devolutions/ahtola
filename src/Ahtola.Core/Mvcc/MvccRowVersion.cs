namespace Ahtola.Core.Mvcc;

/// <summary>
/// Begin/end marker on a version — either an in-flight transaction id or a
/// committed timestamp (Turso <c>TxTimestampOrID</c>).
/// </summary>
internal readonly record struct MvccStamp(bool IsTimestamp, ulong Value)
{
    public static MvccStamp FromTxId(MvccTxId id) => new(IsTimestamp: false, id.Value);

    public static MvccStamp FromTimestamp(ulong timestamp) => new(IsTimestamp: true, timestamp);

    public override string ToString()
        => IsTimestamp ? $"ts:{Value}" : $"tx:{Value}";
}

/// <summary>
/// One version of a row (Turso <c>RowVersion</c>). <see cref="Begin"/> is when
/// the version becomes visible; <see cref="End"/> is when it is deleted/superseded.
/// Null end means still live.
/// </summary>
internal sealed class MvccRowVersion
{
    internal MvccRowVersion(
        ulong versionId,
        MvccStamp? begin,
        MvccStamp? end,
        SqlValue[] cells,
        bool isTombstone = false)
    {
        VersionId = versionId;
        Begin = begin;
        End = end;
        Cells = cells;
        IsTombstone = isTombstone;
    }

    internal ulong VersionId { get; }

    internal MvccStamp? Begin { get; set; }

    internal MvccStamp? End { get; set; }

    /// <summary>Column values for this version (empty for pure tombstones).</summary>
    internal SqlValue[] Cells { get; }

    /// <summary>True when this version only marks a base-row delete (no payload).</summary>
    internal bool IsTombstone { get; }

    internal MvccRowVersion Clone()
        => new(VersionId, Begin, End, (SqlValue[])Cells.Clone(), IsTombstone);
}
