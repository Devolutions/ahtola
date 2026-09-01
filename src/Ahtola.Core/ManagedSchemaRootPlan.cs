using Ahtola.Core.Execution;

namespace Ahtola.Core;

/// <summary>The kind of b-tree a <see cref="ManagedSchemaRootReservation"/> stands for.</summary>
internal enum ManagedSchemaRootKind
{
    Table,
    Index,
}

/// <summary>One root a DDL program reserved, in reservation order.</summary>
internal sealed record ManagedSchemaRootReservation(uint RootPage, ManagedSchemaRootKind Kind);

/// <summary>
/// The root-page allocation and reclamation intents one DDL program accumulated, held transaction-locally
/// until the outer catalog/persist boundary publishes or discards them.
/// </summary>
/// <remarks>
/// <para><b>Adapter invariant — logical roots are not durable.</b></para>
/// <para>
/// Ahtola's managed persistence writes a database by full rewrite: <c>EmbeddedFileStore.Persist</c> builds
/// a fresh page allocation for the whole catalog on every commit and therefore assigns every table and
/// index root at commit time. It has no API to reserve a physical root ahead of that rewrite, so a DDL
/// program running before the commit genuinely cannot know the page its new b-tree will occupy.
/// </para>
/// <para>
/// Rather than invent a page number and call it a root — which would be a durable-looking value that is
/// wrong the moment the rewrite runs — <see cref="Reserve"/> hands out a <em>logical</em> root: a
/// transaction-local identifier drawn from a band above every root and page the context knows about, and
/// recorded here so it stays recognizable. A logical root is a placeholder for "the root the eventual
/// commit will assign", nothing more. It is never a page number, is never read from or written to, and is
/// never compared against the committed page count.
/// </para>
/// <para>The invariant every consumer must honor:</para>
/// <list type="number">
/// <item>A logical root is valid only inside the transaction that reserved it.</item>
/// <item>Publication must map every logical root to the physical root the rewrite assigned, through
/// <see cref="MapToPhysicalRoot"/> (normally via <see cref="ManagedSchemaStage.MapLogicalRoots"/>), which
/// retires the identifier as it records the mapping.
/// <see cref="ManagedSchemaRowSet.ValidateNoLogicalRoots"/> fails closed when one would escape.</item>
/// <item>Discarding the stage discards the reservation; nothing outside needs to be undone, because no
/// storage was touched.</item>
/// </list>
/// <para>
/// <see cref="MarkCleared"/> and <see cref="MarkDestroyed"/> are likewise intents, not actions: the full
/// rewrite simply stops emitting a dropped object, so its pages are reclaimed by not being written. The
/// plan records the intent so a later phase can verify the rewrite retired exactly the expected trees.
/// </para>
/// </remarks>
internal sealed class ManagedSchemaRootPlan
{
    private readonly List<ManagedSchemaRootReservation> _reservations = [];
    private readonly HashSet<uint> _logicalRoots = [];
    private readonly HashSet<uint> _baselineLogicalRoots = [];
    private readonly HashSet<uint> _reservedLogicalRoots = [];
    private readonly Dictionary<uint, uint> _publishedRoots = [];
    private readonly List<uint> _clearedRoots = [];
    private readonly List<uint> _destroyedRoots = [];
    private uint _nextLogicalRoot;
    private uint _baselineHighWater;

    /// <param name="firstLogicalRoot">
    /// The first identifier <see cref="Reserve"/> may hand out. The owner passes a value strictly greater
    /// than every physical root and page the bound database currently uses, so a logical root can never be
    /// confused with an existing object's root inside this transaction.
    /// </param>
    public ManagedSchemaRootPlan(uint firstLogicalRoot)
    {
        if (firstLogicalRoot < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(firstLogicalRoot),
                firstLogicalRoot,
                "Pages 0 and 1 can never be an allocatable b-tree root.");
        }

        _nextLogicalRoot = firstLogicalRoot;
        _baselineHighWater = firstLogicalRoot;
    }

    /// <summary>The roots reserved by this program run, in reservation order.</summary>
    public IReadOnlyList<ManagedSchemaRootReservation> Reservations => _reservations;

    /// <summary>The roots <c>ClearBtree</c> asked to empty, in order.</summary>
    public IReadOnlyList<uint> ClearedRoots => _clearedRoots;

    /// <summary>The roots <c>Destroy</c> asked to retire, in order.</summary>
    public IReadOnlyList<uint> DestroyedRoots => _destroyedRoots;

    /// <summary>
    /// The physical root publication assigned to each logical root, keyed by the retired logical
    /// identifier. It is the record of what <see cref="MapToPhysicalRoot"/> resolved, so a later phase can
    /// check the commit placed exactly the trees this program described.
    /// </summary>
    public IReadOnlyDictionary<uint, uint> PublishedRoots => _publishedRoots;

    public bool HasStagedChanges
        => _reservations.Count > 0 || _clearedRoots.Count > 0 || _destroyedRoots.Count > 0;

    /// <summary>Whether <paramref name="rootPage"/> is a placeholder this plan handed out and has not
    /// yet retired through <see cref="MapToPhysicalRoot"/>.</summary>
    public bool IsLogicalRoot(uint rootPage) => _logicalRoots.Contains(rootPage);

    /// <summary>
    /// Hands out a logical root for an object that already exists but whose storage has no physical root —
    /// every object of an in-memory database, where SQLite would have a real b-tree page and Ahtola has
    /// none. Unlike <see cref="Reserve"/> this records no reservation, because the program did not create
    /// the object, and it survives <see cref="Reset"/> so a re-run keeps addressing the same objects.
    /// </summary>
    public uint AssignBaselineRoot(ManagedSchemaRootKind kind)
    {
        _ = kind;
        var root = TakeNextLogicalRoot();
        _baselineLogicalRoots.Add(root);
        _baselineHighWater = _nextLogicalRoot;
        return root;
    }

    /// <summary>Reserves the next logical root for a new b-tree of <paramref name="kind"/>.</summary>
    public uint Reserve(ManagedSchemaRootKind kind)
    {
        var root = TakeNextLogicalRoot();
        _reservedLogicalRoots.Add(root);
        _reservations.Add(new ManagedSchemaRootReservation(root, kind));
        return root;
    }

    /// <summary>Records that the contents under <paramref name="rootPage"/> are to be emptied.</summary>
    public void MarkCleared(uint rootPage)
    {
        RequireAllocatableRoot(rootPage, "ClearBtree");
        _clearedRoots.Add(rootPage);
    }

    /// <summary>
    /// Records that <paramref name="rootPage"/> is to be retired. Reclaiming a root this program had itself
    /// reserved simply cancels the reservation, exactly as an uncommitted allocation should behave.
    /// </summary>
    /// <remarks>
    /// Only a root from <see cref="Reserve"/> is cancellable. A baseline logical root stands for a tree
    /// that already exists, so destroying it is a real reclamation intent the commit has to honor — it is
    /// recorded, not silently forgotten.
    /// </remarks>
    public void MarkDestroyed(uint rootPage)
    {
        RequireAllocatableRoot(rootPage, "Destroy");
        if (_reservedLogicalRoots.Remove(rootPage))
        {
            _logicalRoots.Remove(rootPage);
            _reservations.RemoveAll(reservation => reservation.RootPage == rootPage);
            return;
        }

        _destroyedRoots.Add(rootPage);
    }

    /// <summary>
    /// Retires <paramref name="logicalRoot"/> in favor of the physical root the commit assigned.
    /// </summary>
    /// <remarks>
    /// Retiring the identifier rather than relying on its numeric value is what makes
    /// <see cref="IsLogicalRoot"/> exact: a page number the commit assigns may coincide with an identifier
    /// this plan handed out, so "is it logical" can only be answered by membership, never by range.
    /// </remarks>
    public void MapToPhysicalRoot(uint logicalRoot, uint physicalRoot)
    {
        if (!_logicalRoots.Contains(logicalRoot))
        {
            throw new VdbeSchemaExecutionException(
                $"Root {logicalRoot} is not an outstanding logical root of this schema program.");
        }

        RequireAllocatableRoot(physicalRoot, "Publication");
        if (physicalRoot != logicalRoot && _logicalRoots.Contains(physicalRoot))
        {
            throw new VdbeSchemaExecutionException(
                $"Publication cannot map logical root {logicalRoot} onto {physicalRoot}, "
                + "which is still an outstanding logical root of this schema program.");
        }

        _logicalRoots.Remove(logicalRoot);
        _reservedLogicalRoots.Remove(logicalRoot);
        _publishedRoots[logicalRoot] = physicalRoot;
    }

    /// <summary>
    /// Discards every intent this program run staged, keeping the baseline roots handed out by
    /// <see cref="AssignBaselineRoot"/> so a re-run still recognizes the objects that already existed.
    /// </summary>
    public void Reset()
    {
        _logicalRoots.Clear();
        foreach (var root in _baselineLogicalRoots)
            _logicalRoots.Add(root);

        _reservedLogicalRoots.Clear();
        _publishedRoots.Clear();
        _reservations.Clear();
        _clearedRoots.Clear();
        _destroyedRoots.Clear();
        _nextLogicalRoot = _baselineHighWater;
    }

    private uint TakeNextLogicalRoot()
    {
        if (_nextLogicalRoot == uint.MaxValue)
        {
            throw new VdbeSchemaExecutionException(
                "The schema root plan exhausted its logical root identifiers.");
        }

        var root = _nextLogicalRoot++;
        _logicalRoots.Add(root);
        return root;
    }

    private static void RequireAllocatableRoot(uint rootPage, string opcodeName)
    {
        if (rootPage < 2)
        {
            throw new VdbeSchemaExecutionException(
                $"{opcodeName} addresses root page {rootPage}, which is not an allocatable b-tree root.");
        }
    }
}
