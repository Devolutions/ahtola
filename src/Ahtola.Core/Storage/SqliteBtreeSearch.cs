namespace Ahtola.Core.Storage;

/// <summary>The lower-bound result from a search of cells ordered by one SQLite key.</summary>
public readonly record struct SqliteBtreeSearchResult(int Index, bool IsExact);

/// <summary>The child selected by an interior-page lower-bound search.</summary>
public readonly record struct SqliteBtreeChildSearchResult(
    int ChildIndex,
    uint ChildPage,
    bool IsSeparatorKey);
