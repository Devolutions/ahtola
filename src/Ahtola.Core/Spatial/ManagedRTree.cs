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
            if (double.IsNaN(minimum) || double.IsNaN(maximum) || minimum > maximum)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(coordinates),
                    "R-Tree coordinates must not be NaN and every minimum must be no greater than its maximum.");
            }
        }
    }

    public int Dimensions => _coordinates.Length / 2;

    public int CoordinateCount => _coordinates.Length;

    public double Minimum(int dimension) => _coordinates[dimension * 2];

    public double Maximum(int dimension) => _coordinates[(dimension * 2) + 1];

    public double Coordinate(int index) => _coordinates[index];

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

    public bool ValueEquals(ManagedRTreeBounds other)
        => Dimensions == other.Dimensions
            && Enumerable.Range(0, CoordinateCount).All(index =>
                Coordinate(index) == other.Coordinate(index));

    private void EnsureCompatible(ManagedRTreeBounds other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Dimensions != other.Dimensions)
            throw new ArgumentException("R-Tree bounds must have the same dimension count.", nameof(other));
    }
}

internal readonly record struct ManagedRTreeSearchConstraint(
    int CoordinateIndex,
    ManagedVirtualTableConstraintOperator Operator,
    double Value);

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

    internal int LastSearchVisitedNodes { get; private set; }

    public void Upsert(long rowId, ManagedRTreeBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        if (_entries.Remove(rowId))
            RebuildTree();

        InsertNew(rowId, bounds);
    }

    public bool Remove(long rowId)
    {
        if (!_entries.Remove(rowId))
            return false;

        // Rebuilding is a deterministic CondenseTree: every surviving leaf is reinserted and no
        // underfull interior node or stale bounding rectangle can survive a delete.
        RebuildTree();
        return true;
    }

    public bool TryGet(long rowId, out ManagedRTreeBounds bounds)
        => _entries.TryGetValue(rowId, out bounds!);

    public IReadOnlyList<long> SearchIntersecting(ManagedRTreeBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        var constraints = new ManagedRTreeSearchConstraint[bounds.CoordinateCount];
        for (var dimension = 0; dimension < bounds.Dimensions; dimension++)
        {
            constraints[dimension * 2] = new(
                dimension * 2,
                ManagedVirtualTableConstraintOperator.LessThanOrEqual,
                bounds.Maximum(dimension));
            constraints[(dimension * 2) + 1] = new(
                (dimension * 2) + 1,
                ManagedVirtualTableConstraintOperator.GreaterThanOrEqual,
                bounds.Minimum(dimension));
        }
        return Search(constraints);
    }

    public IReadOnlyList<long> SearchContaining(ManagedRTreeBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        var constraints = new ManagedRTreeSearchConstraint[bounds.CoordinateCount];
        for (var dimension = 0; dimension < bounds.Dimensions; dimension++)
        {
            constraints[dimension * 2] = new(
                dimension * 2,
                ManagedVirtualTableConstraintOperator.LessThanOrEqual,
                bounds.Minimum(dimension));
            constraints[(dimension * 2) + 1] = new(
                (dimension * 2) + 1,
                ManagedVirtualTableConstraintOperator.GreaterThanOrEqual,
                bounds.Maximum(dimension));
        }
        return Search(constraints);
    }

    public IReadOnlyList<long> Search(IReadOnlyList<ManagedRTreeSearchConstraint> constraints)
    {
        ArgumentNullException.ThrowIfNull(constraints);
        LastSearchVisitedNodes = 0;
        if (_root is null)
            return [];

        foreach (var constraint in constraints)
        {
            if (constraint.CoordinateIndex < 0
                || constraint.CoordinateIndex >= _root.Bounds!.CoordinateCount)
            {
                throw new ArgumentOutOfRangeException(nameof(constraints));
            }
        }

        var matches = new List<long>();
        Search(_root, constraints, matches);
        matches.Sort();
        return matches;
    }

    public IReadOnlyList<KeyValuePair<long, ManagedRTreeBounds>> Snapshot()
        => _entries.OrderBy(static entry => entry.Key).ToArray();

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (_root is null)
        {
            if (_entries.Count != 0)
                problems.Add("R-Tree dictionary contains entries but the tree is empty");
            return problems;
        }

        var seen = new Dictionary<long, ManagedRTreeBounds>();
        ValidateNode(_root, isRoot: true, expectedDimensions: _root.Bounds!.Dimensions, seen, problems);
        if (seen.Count != _entries.Count)
            problems.Add($"R-Tree tree/dictionary entry counts differ ({seen.Count} != {_entries.Count})");

        foreach (var (rowId, bounds) in _entries)
        {
            if (!seen.TryGetValue(rowId, out var treeBounds))
                problems.Add($"R-Tree dictionary rowid {rowId} is missing from the tree");
            else if (!bounds.ValueEquals(treeBounds))
                problems.Add($"R-Tree dictionary rowid {rowId} has different tree bounds");
        }

        return problems;
    }

    private void InsertNew(long rowId, ManagedRTreeBounds bounds)
    {
        var entry = new Entry(rowId, bounds);
        if (_root is null)
        {
            _root = Node.Leaf(entry);
        }
        else
        {
            if (_root.Bounds!.Dimensions != bounds.Dimensions)
                throw new ArgumentException("R-Tree entries must use one dimension count.", nameof(bounds));

            var sibling = Insert(_root, entry);
            if (sibling is not null)
                _root = Node.Parent(_root, sibling);
        }

        _entries.Add(rowId, bounds);
    }

    private void RebuildTree()
    {
        var entries = _entries.OrderBy(static entry => entry.Key).ToArray();
        _root = null;
        _entries.Clear();
        foreach (var (rowId, bounds) in entries)
            InsertNew(rowId, bounds);
    }

    private void Search(
        Node node,
        IReadOnlyList<ManagedRTreeSearchConstraint> constraints,
        List<long> matches)
    {
        LastSearchVisitedNodes++;
        if (node.Bounds is null || !CouldContainMatch(node.Bounds, constraints))
            return;

        if (node.IsLeaf)
        {
            foreach (var entry in node.Entries)
            {
                if (constraints.All(constraint => Compare(
                        entry.Bounds.Coordinate(constraint.CoordinateIndex),
                        constraint.Operator,
                        constraint.Value)))
                {
                    matches.Add(entry.RowId);
                }
            }
            return;
        }

        foreach (var child in node.Children)
            Search(child, constraints, matches);
    }

    private static bool CouldContainMatch(
        ManagedRTreeBounds bounds,
        IReadOnlyList<ManagedRTreeSearchConstraint> constraints)
    {
        foreach (var constraint in constraints)
        {
            var dimension = constraint.CoordinateIndex / 2;
            var lowerEnvelope = bounds.Minimum(dimension);
            var upperEnvelope = bounds.Maximum(dimension);
            var possible = constraint.Operator switch
            {
                ManagedVirtualTableConstraintOperator.Equal or ManagedVirtualTableConstraintOperator.Is
                    => lowerEnvelope <= constraint.Value && upperEnvelope >= constraint.Value,
                ManagedVirtualTableConstraintOperator.NotEqual or ManagedVirtualTableConstraintOperator.IsNot
                    => true,
                ManagedVirtualTableConstraintOperator.LessThan => lowerEnvelope < constraint.Value,
                ManagedVirtualTableConstraintOperator.LessThanOrEqual => lowerEnvelope <= constraint.Value,
                ManagedVirtualTableConstraintOperator.GreaterThan => upperEnvelope > constraint.Value,
                ManagedVirtualTableConstraintOperator.GreaterThanOrEqual => upperEnvelope >= constraint.Value,
                _ => true,
            };
            if (!possible)
                return false;
        }

        return true;
    }

    private static bool Compare(
        double left,
        ManagedVirtualTableConstraintOperator operation,
        double right)
        => operation switch
        {
            ManagedVirtualTableConstraintOperator.Equal or ManagedVirtualTableConstraintOperator.Is => left == right,
            ManagedVirtualTableConstraintOperator.NotEqual or ManagedVirtualTableConstraintOperator.IsNot => left != right,
            ManagedVirtualTableConstraintOperator.GreaterThan => left > right,
            ManagedVirtualTableConstraintOperator.GreaterThanOrEqual => left >= right,
            ManagedVirtualTableConstraintOperator.LessThan => left < right,
            ManagedVirtualTableConstraintOperator.LessThanOrEqual => left <= right,
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

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
    {
        var enlarged = bounds.Union(entry).HyperVolume;
        var existing = bounds.HyperVolume;
        if (enlarged == existing)
            return 0;
        if (enlarged == double.MaxValue)
            return existing == double.MaxValue ? 0 : double.MaxValue;
        return enlarged - existing;
    }

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
                .OrderByDescending(candidate => Difference(
                    Enlargement(first.Bounds!, candidate.Bounds),
                    Enlargement(second.Bounds!, candidate.Bounds)))
                .ThenBy(candidate => candidate.FirstRowId)
                .First();
            items.Remove(item);
            AddToBestGroup(item, first, second);
        }

        node.ReplaceWith(first);
        return second;
    }

    private static double Difference(double left, double right)
        => left == right ? 0 : left == double.MaxValue || right == double.MaxValue
            ? double.MaxValue
            : Math.Abs(left - right);

    private static void SeedGroups(List<SplitItem> items, Node first, Node second)
    {
        SplitItem? firstSeed = null;
        SplitItem? secondSeed = null;
        var greatestWaste = double.NegativeInfinity;
        for (var left = 0; left < items.Count; left++)
        {
            for (var right = left + 1; right < items.Count; right++)
            {
                var union = items[left].Bounds.Union(items[right].Bounds).HyperVolume;
                var waste = union == double.MaxValue
                    ? double.MaxValue
                    : union - items[left].Bounds.HyperVolume - items[right].Bounds.HyperVolume;
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

    private static void ValidateNode(
        Node node,
        bool isRoot,
        int expectedDimensions,
        Dictionary<long, ManagedRTreeBounds> seen,
        List<string> problems)
    {
        if (node.Count == 0)
            problems.Add("R-Tree contains an empty node");
        if (!isRoot && node.Count < MinimumEntries)
            problems.Add($"R-Tree contains an underfull node with {node.Count} entries");
        if (node.Count > MaximumEntries)
            problems.Add($"R-Tree contains an overfull node with {node.Count} entries");
        if (node.Bounds is null || node.Bounds.Dimensions != expectedDimensions)
            problems.Add("R-Tree node has missing or inconsistent bounds");

        if (node.IsLeaf)
        {
            foreach (var entry in node.Entries)
            {
                if (entry.Bounds.Dimensions != expectedDimensions)
                    problems.Add($"R-Tree rowid {entry.RowId} has an inconsistent dimension count");
                if (!seen.TryAdd(entry.RowId, entry.Bounds))
                    problems.Add($"R-Tree rowid {entry.RowId} appears more than once");
            }
        }
        else
        {
            foreach (var child in node.Children)
                ValidateNode(child, isRoot: false, expectedDimensions, seen, problems);
        }

        if (node.Count > 0)
        {
            var expected = node.IsLeaf
                ? node.Entries.Select(static entry => entry.Bounds).Aggregate(static (left, right) => left.Union(right))
                : node.Children.Select(static child => child.Bounds!).Aggregate(static (left, right) => left.Union(right));
            if (node.Bounds is null || !node.Bounds.ValueEquals(expected))
                problems.Add("R-Tree node bounding rectangle is stale");
        }
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
