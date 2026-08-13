namespace Ahtola.Core.Storage;

/// <summary>
/// Splits one validated table-leaf page into two compact non-root page images.
/// </summary>
/// <remarks>
/// The returned separator is the maximum rowid in the left image, suitable for
/// a <see cref="SqliteTableInteriorCell"/>. This is only a bounded image codec:
/// it does not allocate pages, install the images, update a parent, or replace a
/// root with an interior page.
/// </remarks>
public sealed class SqliteTableLeafSplit
{
    private readonly byte[] _leftPage;
    private readonly byte[] _rightPage;

    internal SqliteTableLeafSplit(
        ReadOnlySpan<byte> leftPage,
        ReadOnlySpan<byte> rightPage,
        long separatorRowId,
        int leftCellCount,
        int rightCellCount)
    {
        _leftPage = leftPage.ToArray();
        _rightPage = rightPage.ToArray();
        SeparatorRowId = separatorRowId;
        LeftCellCount = leftCellCount;
        RightCellCount = rightCellCount;
    }

    /// <summary>The compact left-page image.</summary>
    public ReadOnlyMemory<byte> LeftPage => _leftPage;

    /// <summary>The compact right-page image.</summary>
    public ReadOnlyMemory<byte> RightPage => _rightPage;

    /// <summary>The maximum rowid in <see cref="LeftPage"/>.</summary>
    public long SeparatorRowId { get; }

    /// <summary>The number of cells in <see cref="LeftPage"/>.</summary>
    public int LeftCellCount { get; }

    /// <summary>The number of cells in <see cref="RightPage"/>.</summary>
    public int RightCellCount { get; }

    /// <summary>
    /// Splits after exactly <paramref name="leftCellCount"/> ordered cells.
    /// Both output pages use the source view's validated page and usable sizes.
    /// </summary>
    public static SqliteTableLeafSplit Create(
        SqliteTableLeafPageView source,
        int leftCellCount)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateSplitBounds(source.Cells.Count, leftCellCount, "table-leaf");

        var left = new SqliteTableLeafPageBuilder(source.PageSize, source.UsableSpace);
        var right = new SqliteTableLeafPageBuilder(source.PageSize, source.UsableSpace);
        for (var index = 0; index < source.Cells.Count; index++)
        {
            var target = index < leftCellCount ? left : right;
            target.Append(source.Cells[index].Cell);
        }

        return new SqliteTableLeafSplit(
            left.Build(),
            right.Build(),
            source.Cells[leftCellCount - 1].Cell.RowId,
            leftCellCount,
            source.Cells.Count - leftCellCount);
    }

    private static void ValidateSplitBounds(
        int cellCount,
        int leftCellCount,
        string pageDescription)
    {
        if (cellCount < 2)
            throw new InvalidOperationException($"SQLite {pageDescription} split requires at least two cells.");
        if (leftCellCount <= 0 || leftCellCount >= cellCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leftCellCount),
                leftCellCount,
                $"SQLite {pageDescription} split must leave at least one cell on each side.");
        }
    }

    internal static void ValidateIndexSplitBounds(
        int cellCount,
        int leftCellCount)
        => ValidateSplitBounds(cellCount, leftCellCount, "index-leaf");
}

/// <summary>
/// Splits one validated index-leaf page into two compact non-root page images.
/// </summary>
/// <remarks>
/// The returned separator is the maximum complete record in the left image,
/// suitable for a <see cref="SqliteIndexInteriorCell"/>. Existing overflow
/// pointers are preserved; the primitive does not allocate, reclaim, or install
/// overflow pages.
/// </remarks>
public sealed class SqliteIndexLeafSplit
{
    private readonly byte[] _leftPage;
    private readonly byte[] _rightPage;
    private readonly byte[] _separatorRecord;

    internal SqliteIndexLeafSplit(
        ReadOnlySpan<byte> leftPage,
        ReadOnlySpan<byte> rightPage,
        ReadOnlySpan<byte> separatorRecord,
        int leftCellCount,
        int rightCellCount)
    {
        _leftPage = leftPage.ToArray();
        _rightPage = rightPage.ToArray();
        _separatorRecord = separatorRecord.ToArray();
        LeftCellCount = leftCellCount;
        RightCellCount = rightCellCount;
    }

    /// <summary>The compact left-page image.</summary>
    public ReadOnlyMemory<byte> LeftPage => _leftPage;

    /// <summary>The compact right-page image.</summary>
    public ReadOnlyMemory<byte> RightPage => _rightPage;

    /// <summary>A copy of the maximum complete record in <see cref="LeftPage"/>.</summary>
    public byte[] GetSeparatorRecord() => _separatorRecord.ToArray();

    /// <summary>The number of cells in <see cref="LeftPage"/>.</summary>
    public int LeftCellCount { get; }

    /// <summary>The number of cells in <see cref="RightPage"/>.</summary>
    public int RightCellCount { get; }

    /// <summary>
    /// Splits after exactly <paramref name="leftCellCount"/> ordered records.
    /// Complete records must have been verified when parsing <paramref name="source"/>.
    /// </summary>
    public static SqliteIndexLeafSplit Create(
        SqliteIndexLeafPageView source,
        int leftCellCount)
    {
        ArgumentNullException.ThrowIfNull(source);
        SqliteTableLeafSplit.ValidateIndexSplitBounds(
            source.Cells.Count,
            leftCellCount);
        if (!source.HasVerifiedRecordOrdering)
        {
            throw new InvalidOperationException(
                "Splitting an index leaf with overflow records requires an overflow reader during parsing.");
        }

        var left = new SqliteIndexLeafPageBuilder(
            source.PageSize,
            source.UsableSpace,
            source.RecordComparer);
        var right = new SqliteIndexLeafPageBuilder(
            source.PageSize,
            source.UsableSpace,
            source.RecordComparer);
        for (var index = 0; index < source.Cells.Count; index++)
        {
            var target = index < leftCellCount ? left : right;
            target.Append(source.Cells[index].Cell, source.GetRecord(index));
        }

        return new SqliteIndexLeafSplit(
            left.Build(),
            right.Build(),
            source.GetRecord(leftCellCount - 1),
            leftCellCount,
            source.Cells.Count - leftCellCount);
    }
}
