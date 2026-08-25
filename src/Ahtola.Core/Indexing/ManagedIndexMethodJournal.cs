namespace Ahtola.Core.Indexing;

/// <summary>
/// The engine-side half of revision-aware incremental maintenance: an append-only record of the
/// rowids each DML statement touched, keyed by the base-row revision counter the row store already
/// maintains.
/// </summary>
/// <remarks>
/// <para>
/// A method index derives its state from the base rows, so the only two honest options are to walk
/// every base row on each use — O(N) per statement, which the cost model would then have to charge
/// for — or to know exactly which rows moved. This journal provides the second option.
/// </para>
/// <para>
/// The safety argument is one-sided on purpose. Re-deriving a row that did not change is idempotent,
/// so naming extra rowids is harmless; missing one is not. Every accepted delta therefore has to
/// prove that <em>every</em> revision bump in its range was recorded: a gap (a mutation that reached
/// the row store without reaching this journal) poisons it until the next full rebuild, and a
/// trailing unrecorded bump makes <see cref="TryGetDelta"/> refuse. The fallback is always a correct
/// full rebuild, never a stale answer.
/// </para>
/// <para>
/// The journal is deliberately not copied by <c>EmbeddedTable</c>'s clone/snapshot paths. A catalog
/// snapshot forks its method attachments into empty ones, so the restored attachment rebuilds from
/// the restored rows and no pre-rollback delta can ever be replayed against post-rollback state.
/// </para>
/// </remarks>
internal sealed class ManagedIndexMethodJournal
{
    /// <summary>
    /// Retained entries before the oldest are dropped. A method that fell further behind than this
    /// rebuilds instead, which is the same outcome it would have had without a journal at all.
    /// </summary>
    private const int MaxRetainedEntries = 8192;

    private readonly List<Entry> _entries = [];
    private long _coveredRevision;
    private long _oldestValidRevision;
    private bool _poisoned;

    public ManagedIndexMethodJournal(long revision)
    {
        _coveredRevision = revision;
        _oldestValidRevision = revision;
    }

    /// <summary>Records one reported row mutation at the row store revision it produced.</summary>
    public void Record(long rowId, long revision)
    {
        // More than one revision bump since the last record means a mutation reached the row store
        // without being reported here. We cannot know which rowid it touched, so no delta that
        // spans this point may ever be trusted again until a rebuild re-establishes the baseline.
        if (revision > _coveredRevision + 1)
            _poisoned = true;

        _entries.Add(new Entry(rowId, revision));
        if (revision > _coveredRevision)
            _coveredRevision = revision;

        if (_entries.Count > MaxRetainedEntries)
        {
            var drop = _entries.Count / 2;
            _oldestValidRevision = Math.Max(_oldestValidRevision, _entries[drop - 1].Revision);
            _entries.RemoveRange(0, drop);
        }
    }

    /// <summary>
    /// Re-establishes the baseline after a method rebuilt from the base rows. Entries at or before
    /// <paramref name="revision"/> are dropped and the poison flag clears, because a full rebuild
    /// observed the rows directly and no longer depends on anything that came before.
    /// </summary>
    public void ResetBaseline(long revision)
    {
        _poisoned = false;
        _oldestValidRevision = revision;
        if (revision > _coveredRevision)
            _coveredRevision = revision;

        var keep = 0;
        while (keep < _entries.Count && _entries[keep].Revision <= revision)
            keep++;

        if (keep > 0)
            _entries.RemoveRange(0, keep);
    }

    /// <summary>
    /// The rowids touched between <paramref name="sinceRevision"/> and
    /// <paramref name="currentRevision"/>, or null when that range cannot be proven complete.
    /// </summary>
    public ManagedIndexSourceDelta? TryGetDelta(long sinceRevision, long currentRevision)
    {
        if (_poisoned || sinceRevision < 0 || sinceRevision < _oldestValidRevision)
            return null;
        if (sinceRevision > currentRevision)
            return null;

        // A bump the journal never saw is exactly the case a delta must not paper over.
        if (currentRevision > _coveredRevision)
            return null;
        if (sinceRevision == currentRevision)
            return new ManagedIndexSourceDelta(currentRevision, []);

        var changed = new List<long>();
        foreach (var entry in _entries)
        {
            if (entry.Revision > sinceRevision && entry.Revision <= currentRevision)
                changed.Add(entry.RowId);
        }

        // The first retained entry has to be the very next revision after the caller's baseline,
        // otherwise trimming dropped a change the caller has not applied yet.
        if (changed.Count == 0)
            return null;

        return new ManagedIndexSourceDelta(currentRevision, changed);
    }

    private readonly record struct Entry(long RowId, long Revision);
}
