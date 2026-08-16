namespace Ahtola.Core.Spatial;

/// <summary>
/// Inclusive N-dimensional bounds used by the managed R-Tree. Coordinates are supplied as
/// <c>min0, max0, min1, max1, ...</c>.
/// </summary>
internal sealed class ManagedRTreeBounds
{
    private readonly double[] _coordinates;

    public ManagedRTreeBounds(params double[] coordinates)
    {
        ArgumentNullException.ThrowIfNull(coordinates);
        if (coordinates.Length == 0 || coordinates.Length % 2 != 0)
            throw new ArgumentException("R-Tree bounds require a min/max pair for every dimension.", nameof(coordinates));

        _coordinates = [.. coordinates];
        for (var dimension = 0; dimension < Dimensions; dimension++)
        {
            var minimum = Minimum(dimension);
            var maximum = Maximum(dimension);
            if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum > maximum)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(coordinates),
                    "R-Tree coordinates must be finite and every minimum must be no greater than its maximum.");
            }
        }
    }

    public int Dimensions => _coordinates.Length / 2;

    public double Minimum(int dimension) => _coordinates[dimension * 2];

    public double Maximum(int dimension) => _coordinates[(dimension * 2) + 1];

    public bool Intersects(ManagedRTreeBounds other)
    {
        EnsureCompatible(other);
        for (var dimension = 0; dimension < Dimensions; dimension++)
        {
            if (Maximum(dimension) < other.Minimum(dimension)
                || other.Maximum(dimension) < Minimum(dimension))
            {
                return false;
            }
        }

        return true;
    }

    public bool Contains(ManagedRTreeBounds other)
    {
        EnsureCompatible(other);
        for (var dimension = 0; dimension < Dimensions; dimension++)
        {
            if (Minimum(dimension) > other.Minimum(dimension)
                || Maximum(dimension) < other.Maximum(dimension))
            {
                return false;
            }
        }

        return true;
    }

    public ManagedRTreeBounds Union(ManagedRTreeBounds other)
    {
        EnsureCompatible(other);
        var union = new double[_coordinates.Length];
        for (var dimension = 0; dimension < Dimensions; dimension++)
        {
            union[dimension * 2] = Math.Min(Minimum(dimension), other.Minimum(dimension));
            union[(dimension * 2) + 1] = Math.Max(Maximum(dimension), other.Maximum(dimension));
        }

        return new ManagedRTreeBounds(union);
    }

    public double HyperVolume
    {
        get
        {
            var volume = 1d;
            for (var dimension = 0; dimension < Dimensions; dimension++)
            {
                var extent = Maximum(dimension) - Minimum(dimension);
                if (!double.IsFinite(extent) || extent > double.MaxValue / volume)
                    return double.MaxValue;

                volume *= extent;
            }

            return volume;
        }
    }

    private void EnsureCompatible(ManagedRTreeBounds other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Dimensions != other.Dimensions)
            throw new ArgumentException("R-Tree bounds must have the same dimension count.", nameof(other));
    }
}

/// <summary>
/// A deterministic in-memory R-Tree for reusable module filtering. The virtual-table layer owns
/// persistence and transaction boundaries; this structure only manages spatial entries.
/// </summary>
internal sealed class ManagedRTreeIndex
{
    private const int MaximumEntries = 8;
    private const int MinimumEntries = MaximumEntries / 2;

    private Node? _root;
    private readonly Dictionary<long, ManagedRTreeBounds> _entries = [];

    public int Count => _entries.Count;

    public void Upsert(long rowId, ManagedRTreeBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        Remove(rowId);

        var entry = new Entry(rowId, bounds);
        if (_root is null)
        {
            _root = Node.Leaf(entry);
        }
        else
        {
            var sibling = Insert(_root, entry);
            if (sibling is not null)
                _root = Node.Parent(_root, sibling);
        }

        _entries.Add(rowId, bounds);
    }

    public bool Remove(long rowId)
    {
        if (!_entries.Remove(rowId, out var bounds))
            return false;

        Remove(_root!, rowId, bounds);
        if (_root is { IsLeaf: false, Children.Count: 1 })
            _root = _root.Children[0];
        if (_root is { Count: 0 })
            _root = null;

        return true;
    }

    public IReadOnlyList<long> SearchIntersecting(ManagedRTreeBounds bounds)
        => Search(bounds, static (candidate, query) => candidate.Intersects(query));

    public IReadOnlyList<long> SearchContaining(ManagedRTreeBounds bounds)
        => Search(bounds, static (candidate, query) => candidate.Contains(query));

    private IReadOnlyList<long> Search(
        ManagedRTreeBounds bounds,
        Func<ManagedRTreeBounds, ManagedRTreeBounds, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        var matches = new List<long>();
        Search(_root, bounds, predicate, matches);
        matches.Sort();
        return matches;
    }

    private static void Search(
        Node? node,
        ManagedRTreeBounds bounds,
        Func<ManagedRTreeBounds, ManagedRTreeBounds, bool> predicate,
        List<long> matches)
    {
        if (node is null || node.Bounds is null || !node.Bounds.Intersects(bounds))
            return;

        if (node.IsLeaf)
        {
            foreach (var entry in node.Entries)
            {
                if (predicate(entry.Bounds, bounds))
                    matches.Add(entry.RowId);
            }

            return;
        }

        foreach (var child in node.Children)
            Search(child, bounds, predicate, matches);
    }

    private static bool Remove(Node node, long rowId, ManagedRTreeBounds bounds)
    {
        if (node.IsLeaf)
        {
            var index = node.Entries.FindIndex(entry => entry.RowId == rowId);
            if (index < 0)
                return false;

            node.Entries.RemoveAt(index);
            node.RefreshBounds();
            return true;
        }

        foreach (var child in node.Children.ToArray())
        {
            if (child.Bounds is null || !child.Bounds.Intersects(bounds) || !Remove(child, rowId, bounds))
                continue;

            if (child.Count == 0)
                node.Children.Remove(child);
            node.RefreshBounds();
            return true;
        }

        return false;
    }

    private static Node? Insert(Node node, Entry entry)
    {
        if (node.IsLeaf)
        {
            node.Entries.Add(entry);
        }
        else
        {
            var child = SelectChild(node.Children, entry.Bounds);
            var sibling = Insert(child, entry);
            if (sibling is not null)
                node.Children.Add(sibling);
        }

        node.RefreshBounds();
        return node.Count > MaximumEntries ? Split(node) : null;
    }

    private static Node SelectChild(IReadOnlyList<Node> children, ManagedRTreeBounds bounds)
        => children
            .OrderBy(child => Enlargement(child.Bounds!, bounds))
            .ThenBy(child => child.Bounds!.HyperVolume)
            .ThenBy(child => child.FirstRowId)
            .First();

    private static double Enlargement(ManagedRTreeBounds bounds, ManagedRTreeBounds entry)
        => bounds.Union(entry).HyperVolume - bounds.HyperVolume;

    private static Node Split(Node node)
    {
        var items = node.IsLeaf
            ? node.Entries.Select(static entry => new SplitItem(entry, null)).ToList()
            : node.Children.Select(static child => new SplitItem(null, child)).ToList();
        var first = new Node(node.IsLeaf);
        var second = new Node(node.IsLeaf);
        SeedGroups(items, first, second);

        while (items.Count > 0)
        {
            if (first.Count + items.Count == MinimumEntries)
            {
                AddRange(first, items);
                break;
            }

            if (second.Count + items.Count == MinimumEntries)
            {
                AddRange(second, items);
                break;
            }

            var item = items
                .OrderByDescending(candidate => Math.Abs(
                    Enlargement(first.Bounds!, candidate.Bounds) - Enlargement(second.Bounds!, candidate.Bounds)))
                .ThenBy(candidate => candidate.FirstRowId)
                .First();
            items.Remove(item);
            AddToBestGroup(item, first, second);
        }

        node.ReplaceWith(first);
        return second;
    }

    private static void SeedGroups(List<SplitItem> items, Node first, Node second)
    {
        SplitItem? firstSeed = null;
        SplitItem? secondSeed = null;
        var greatestWaste = double.NegativeInfinity;
        for (var left = 0; left < items.Count; left++)
        {
            for (var right = left + 1; right < items.Count; right++)
            {
                var waste = items[left].Bounds.Union(items[right].Bounds).HyperVolume
                    - items[left].Bounds.HyperVolume
                    - items[right].Bounds.HyperVolume;
                if (waste > greatestWaste)
                {
                    greatestWaste = waste;
                    firstSeed = items[left];
                    secondSeed = items[right];
                }
            }
        }

        Add(first, firstSeed!);
        Add(second, secondSeed!);
        items.Remove(firstSeed!);
        items.Remove(secondSeed!);
    }

    private static void AddToBestGroup(SplitItem item, Node first, Node second)
    {
        var firstEnlargement = Enlargement(first.Bounds!, item.Bounds);
        var secondEnlargement = Enlargement(second.Bounds!, item.Bounds);
        if (firstEnlargement < secondEnlargement
            || (firstEnlargement == secondEnlargement
                && (first.Bounds!.HyperVolume < second.Bounds!.HyperVolume
                    || (first.Bounds.HyperVolume == second.Bounds.HyperVolume && first.Count <= second.Count))))
        {
            Add(first, item);
        }
        else
        {
            Add(second, item);
        }
    }

    private static void AddRange(Node target, List<SplitItem> items)
    {
        foreach (var item in items)
            Add(target, item);
        items.Clear();
    }

    private static void Add(Node node, SplitItem item)
    {
        if (node.IsLeaf)
            node.Entries.Add(item.Entry!);
        else
            node.Children.Add(item.Child!);
        node.RefreshBounds();
    }

    private sealed class Node(bool isLeaf)
    {
        public bool IsLeaf { get; } = isLeaf;
        public List<Entry> Entries { get; } = [];
        public List<Node> Children { get; } = [];
        public ManagedRTreeBounds? Bounds { get; private set; }
        public int Count => IsLeaf ? Entries.Count : Children.Count;
        public long FirstRowId => IsLeaf
            ? Entries.Min(static entry => entry.RowId)
            : Children.Min(static child => child.FirstRowId);

        public static Node Leaf(Entry entry)
        {
            var node = new Node(isLeaf: true);
            node.Entries.Add(entry);
            node.RefreshBounds();
            return node;
        }

        public static Node Parent(Node left, Node right)
        {
            var node = new Node(isLeaf: false);
            node.Children.Add(left);
            node.Children.Add(right);
            node.RefreshBounds();
            return node;
        }

        public void ReplaceWith(Node source)
        {
            Entries.Clear();
            Children.Clear();
            Entries.AddRange(source.Entries);
            Children.AddRange(source.Children);
            RefreshBounds();
        }

        public void RefreshBounds()
        {
            if (Count == 0)
            {
                Bounds = null;
                return;
            }

            var bounds = IsLeaf
                ? Entries.Select(static entry => entry.Bounds)
                : Children.Select(static child => child.Bounds!);
            Bounds = bounds.Aggregate(static (left, right) => left.Union(right));
        }
    }

    private sealed record Entry(long RowId, ManagedRTreeBounds Bounds);

    private sealed record SplitItem(Entry? Entry, Node? Child)
    {
        public ManagedRTreeBounds Bounds => Entry?.Bounds ?? Child!.Bounds!;
        public long FirstRowId => Entry?.RowId ?? Child!.FirstRowId;
    }
}
