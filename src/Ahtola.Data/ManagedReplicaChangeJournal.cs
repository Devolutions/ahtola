using System.Text;
using Ahtola.Core;

namespace Ahtola;

/// <summary>
/// The kind of durable operation captured from a managed embedded replica.
/// </summary>
internal enum ReplicaLocalChangeKind : byte
{
    Row = 1,
    Schema = 2,
}

/// <summary>
/// A committed operation awaiting a future managed-replica push implementation.
/// </summary>
/// <remarks>
/// <c>StatementSequence</c> is the sequence of the journal entry that carries the replayable SQL
/// for this operation's statement. One SQL statement can invoke the update hook for many rows,
/// and only the first of those rows carries the statement text; the rest name it explicitly
/// instead of relying on their position in the journal. <c>0</c> means "no replayable statement
/// is known", which is never pushable: a batch containing such an entry fails closed rather than
/// acknowledging a row that was never transmitted.
/// </remarks>
internal readonly record struct ReplicaLocalChange(
    long Sequence,
    ReplicaLocalChangeKind Kind,
    SqliteChangeOperation Operation,
    string Database,
    string Table,
    long RowId,
    string Sql,
    byte[]? BeforeRecord,
    long StatementSequence = 0)
{
    public static ReplicaLocalChange Row(
        SqliteChangeOperation operation,
        string database,
        string table,
        long rowId,
        byte[]? beforeRecord = null)
        => new(0, ReplicaLocalChangeKind.Row, operation, database, table, rowId, string.Empty, beforeRecord);

    public static ReplicaLocalChange Schema(string sql)
        => new(0, ReplicaLocalChangeKind.Schema, default, string.Empty, string.Empty, 0, sql, null);

    /// <summary>True when this entry carries the SQL that replays its whole statement.</summary>
    public bool CarriesStatementSql => !string.IsNullOrWhiteSpace(Sql);
}

/// <summary>
/// An ordered, bounded view of locally committed replica operations. <see cref="Watermark"/>
/// is the exclusive sequence boundary a successful push may acknowledge.
/// </summary>
internal readonly record struct ReplicaLocalChangeBatch(
    long FirstSequence,
    long Watermark,
    IReadOnlyList<ReplicaLocalChange> Changes);

/// <summary>
/// A total description of a change journal's durable shape, used as an optimistic generation token
/// by publication steps that must release the physical apply lease across network I/O and then
/// prove, on re-acquisition, that the journal they still hold in memory is the journal on disk.
/// </summary>
internal readonly record struct ReplicaJournalGeneration(
    long AssignedSequence,
    long AcknowledgedWatermark,
    int RetainedCount,
    int DiscardedCount);

/// <summary>
/// Replica-private, crash-safe journal. It deliberately lives outside the SQLite file so a
/// remote raw-page replacement never becomes a locally captured mutation.
/// </summary>
internal sealed class ManagedReplicaChangeJournal
{
    internal const string Suffix = ".ahtola-replica-journal";
    internal const string StagingSuffix = Suffix + ".staging.tmp";

    private const ulong Magic = 0x4C_4E_52_4A_4C_4F_54_41; // "ATOLJRNL"
    private const int Version = 8;
    private const int MaxStringBytes = 1024 * 1024;
    private const int MaxBinaryBytes = 16 * 1024 * 1024;

    /// <summary>
    /// The largest gap span a pre-format-7 file may imply. Those formats do not record their
    /// discards, so the inferred set is reconstructed from the distance between the retention base
    /// and the assigned high-water mark — a distance the file itself does not bound. A header
    /// claiming an astronomically high sequence would otherwise drive an unbounded loop and
    /// allocation before any later structural check could reject it, so an implausible span is
    /// treated as corruption up front.
    /// </summary>
    private const long MaxInferredDiscards = 1 << 20;

    private readonly object _gate = new();
    private readonly string _databasePath;
    private readonly string _path;
    private readonly string _stagingPath;
    private readonly List<ReplicaLocalChange> _changes;

    /// <summary>
    /// Every sequence that an explicit, data-loss-acknowledged conflict discard removed and that
    /// has not yet been pruned. This is the durable evidence that a hole in <see cref="_changes"/>
    /// is an <em>intentional</em> discard rather than a lost or corrupted entry: without it the
    /// two are indistinguishable, and an idempotent "the discard already landed" completion could
    /// not be told apart from missing evidence (see
    /// <c>ManagedReplicaConnectionHost.ResolveConflictAsync</c>). Always strictly ascending and
    /// always disjoint from <see cref="_changes"/>.
    /// </summary>
    private readonly List<long> _discarded;
    private long _sequence;
    private long _watermark;

    private ManagedReplicaChangeJournal(
        string databasePath,
        string path,
        string stagingPath,
        long sequence,
        long watermark,
        List<ReplicaLocalChange> changes,
        List<long> discarded)
    {
        _databasePath = databasePath;
        _path = path;
        _stagingPath = stagingPath;
        _sequence = sequence;
        _watermark = watermark;
        _changes = changes;
        _discarded = discarded;
    }

    internal static string GetStagingPath(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        return databasePath + StagingSuffix;
    }

    /// <summary>
    /// Builds a journal file with an exact durable shape, so a test can reproduce a state an
    /// earlier format could reach but this one refuses to create.
    /// </summary>
    /// <remarks>
    /// The only such state that matters is a row separated from the statement that would replay it:
    /// a partial discard. <see cref="DiscardUnacknowledged"/> now refuses to produce it, so it has
    /// to be constructed directly to prove the push path still fails closed when it reads one from
    /// an older file.
    /// </remarks>
    internal static ManagedReplicaChangeJournal OpenForTesting(
        string databasePath,
        long assignedSequence,
        long acknowledgedWatermark,
        IReadOnlyList<ReplicaLocalChange> changes,
        IReadOnlyList<long> discarded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(discarded);

        var journal = new ManagedReplicaChangeJournal(
            databasePath,
            databasePath + Suffix,
            GetStagingPath(databasePath),
            assignedSequence,
            acknowledgedWatermark,
            [.. changes],
            [.. discarded]);
        using (ManagedReplicaJournalLock.AcquireExclusive(databasePath))
            journal.Persist(assignedSequence, acknowledgedWatermark, journal._changes, journal._discarded);
        return journal;
    }

    public static ManagedReplicaChangeJournal Open(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var path = databasePath + Suffix;
        var stagingPath = GetStagingPath(databasePath);

        // Both steps below run under the physical journal lease, for portability rather than
        // tidiness. POSIX unlink succeeds on a file another process still holds open, so an
        // unserialized staging cleanup here would pull the staging file out from under a concurrent
        // Persist and fail its publish; and reading the durable file with a sharing mode that
        // denies delete would make a concurrent Persist's atomic replace fail on Windows. A host
        // that cannot prove the database's physical identity keeps the previous unserialized
        // behavior rather than making Open newly fail closed -- it is strictly no worse off than
        // before, and Open is a read.
        try
        {
            using (ManagedReplicaJournalLock.AcquireExclusive(databasePath))
                return OpenCore(databasePath, path, stagingPath);
        }
        catch (PlatformNotSupportedException)
        {
            return OpenCore(databasePath, path, stagingPath);
        }
    }

    private static ManagedReplicaChangeJournal OpenCore(string databasePath, string path, string stagingPath)
    {
        // A staging file can only ever be a leftover: the durable file is published by an atomic
        // replace, so a staging file that still exists was never adopted and its content is
        // provably not needed. Removing it here (rather than leaving a randomly named,
        // data-bearing artifact behind forever) is the validated startup cleanup.
        DeleteStagingArtifact(stagingPath, throwOnFailure: false);

        var state = ReadDurableState(path);
        return new ManagedReplicaChangeJournal(
            databasePath,
            path,
            stagingPath,
            state.Sequence,
            state.Watermark,
            state.Changes,
            state.Discarded);
    }

    /// <summary>The complete durable shape of one journal file, as validated on read.</summary>
    private readonly record struct DurableJournalState(
        long Sequence,
        long Watermark,
        List<ReplicaLocalChange> Changes,
        List<long> Discarded);

    /// <summary>
    /// Reads and fully validates the durable journal file. Factored out of <see cref="Open"/> so a
    /// mutation holding the physical journal lease can re-read exactly the same validated state
    /// another writer published, instead of persisting from a stale in-memory copy.
    /// </summary>
    private static DurableJournalState ReadDurableState(string path)
    {
        if (!File.Exists(path))
            return new DurableJournalState(0, 1, [], []);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        if (reader.ReadUInt64() != Magic)
            throw new InvalidDataException("Managed replica change journal has an unsupported format.");
        var formatVersion = reader.ReadInt32();
        if (formatVersion is not (1 or 2 or 3 or 4 or 5 or 6 or 7 or Version))
            throw new InvalidDataException("Managed replica change journal has an unsupported format.");

        var sequence = reader.ReadInt64();
        var persistedWatermark = reader.ReadInt64();
        var count = reader.ReadInt32();
        if (sequence < 0
            || sequence == long.MaxValue
            || (formatVersion == 1
                ? persistedWatermark != sequence + 1
                : persistedWatermark < 1 || persistedWatermark > sequence + 1)
            || count < 0)
            throw new InvalidDataException("Managed replica change journal has invalid state.");
        if (count > (stream.Length - stream.Position) / 13)
            throw new InvalidDataException("Managed replica change journal has an invalid entry count.");

        var changes = new List<ReplicaLocalChange>(count);
        long previous = 0;
        for (var i = 0; i < count; i++)
        {
            var change = ReadChange(reader, formatVersion);
            if (change.Sequence <= previous
                || (formatVersion is 2 or 3 or 4 && change.Sequence < persistedWatermark)
                || change.Sequence > sequence)
                throw new InvalidDataException("Managed replica change journal is not ordered.");
            changes.Add(change);
            previous = change.Sequence;
        }

        var discarded = formatVersion >= 7
            ? ReadDiscarded(reader, stream, sequence)
            : InferDiscardedFromGaps(changes, persistedWatermark, sequence);
        if (stream.Position != stream.Length
            || (count != 0 && formatVersion < 6 && previous != sequence))
        {
            // Format 6 relaxed the "last retained entry is the highest ever assigned" invariant:
            // an explicit, data-loss-acknowledged conflict discard may remove entries anywhere in
            // the retained range, including the tail. Format 7 keeps that relaxation but restores
            // exactness by persisting the discarded sequences themselves, so every gap is proven
            // rather than assumed. The assigned high-water mark (`sequence`) still only ever moves
            // forward and is never reused, so monotonicity is preserved; only the *retention* of a
            // given sequence is allowed to end. Formats 1-5 keep the strict check, so a file
            // written by an older build is still validated exactly as it was before.
            throw new InvalidDataException("Managed replica change journal is malformed.");
        }

        ValidateRetentionCompleteness(changes, discarded, persistedWatermark, sequence);
        if (formatVersion < Version)
            InferStatementGroups(changes);
        else
            ValidateStatementGroups(changes);
        var watermark = formatVersion == 1 && changes.Count != 0
            ? changes[0].Sequence
            : persistedWatermark;
        return new DurableJournalState(sequence, watermark, changes, discarded);
    }

    /// <summary>
    /// Runs one durable mutation under the physical journal lease: re-reads the durable file so the
    /// mutation is computed against whatever another instance, alias, or process last published,
    /// then applies <paramref name="mutate"/> and lets it publish atomically.
    /// </summary>
    /// <remarks>
    /// Publication rewrites the whole file, so a mutation computed from a stale in-memory copy
    /// would silently drop every append, acknowledgement, and discard another writer made since
    /// this instance was opened. Re-reading first turns that lost update into an ordinary merge:
    /// appends continue from the durable high-water mark, and every validation the reader performs
    /// (ordering, retention completeness, statement grouping) applies to the state being extended.
    /// </remarks>
    private T MutateDurable<T>(Func<T> mutate)
    {
        using (ManagedReplicaJournalLock.AcquireExclusive(_databasePath))
        {
            lock (_gate)
            {
                var durable = ReadDurableState(_path);
                _sequence = durable.Sequence;
                _watermark = durable.Watermark;
                _changes.Clear();
                _changes.AddRange(durable.Changes);
                _discarded.Clear();
                _discarded.AddRange(durable.Discarded);
                return mutate();
            }
        }
    }

    private void MutateDurable(Action mutate)
        => MutateDurable<object?>(() =>
        {
            mutate();
            return null;
        });

    /// <summary>
    /// Proves the merged entry set this journal is about to publish is still a valid journal:
    /// strictly ascending sequences, never above the assigned high-water mark, never colliding with
    /// a recorded discard, and every statement group intact.
    /// </summary>
    private static void ValidateMergedState(
        long sequence,
        long watermark,
        IReadOnlyList<ReplicaLocalChange> changes,
        IReadOnlyList<long> discarded)
    {
        if (sequence < 0 || sequence == long.MaxValue || watermark < 1 || watermark > sequence + 1)
            throw new InvalidDataException("Managed replica change journal merge produced invalid state.");

        long previous = 0;
        foreach (var change in changes)
        {
            if (change.Sequence <= previous || change.Sequence > sequence)
            {
                throw new InvalidDataException(
                    "Managed replica change journal merge produced a non-monotonic sequence.");
            }

            previous = change.Sequence;
        }

        foreach (var discardedSequence in discarded)
        {
            if (discardedSequence > sequence)
                throw new InvalidDataException("Managed replica change journal merge produced an invalid discard.");
        }

        ValidateStatementGroups(changes);
    }

    /// <summary>
    /// Reconstructs statement grouping for a pre-format-8 file, where the link between a
    /// multi-row statement and its trailing rows was implied by position instead of recorded.
    /// </summary>
    /// <remarks>
    /// The writing rule those formats used is exactly reproduced here: an entry that carries SQL
    /// opens a group and every following entry without SQL belongs to it. A trailing row whose
    /// leader is no longer retained (it was discarded, or acknowledged and pruned) keeps
    /// <c>StatementSequence == 0</c>, which is precisely the state a push must refuse rather than
    /// acknowledge.
    /// </remarks>
    private static void InferStatementGroups(List<ReplicaLocalChange> changes)
    {
        long statement = 0;
        for (var i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            if (change.CarriesStatementSql)
                statement = change.Sequence;
            else if (statement != 0 && i != 0 && changes[i - 1].Sequence + 1 != change.Sequence)
            {
                // A gap between this row and the previous entry means the intervening sequences
                // were discarded, so the run this row belonged to cannot be proven any more.
                statement = 0;
            }

            changes[i] = change with { StatementSequence = change.CarriesStatementSql ? change.Sequence : statement };
        }
    }

    /// <summary>
    /// Validates the recorded statement grouping in isolation: a statement is always named by a
    /// sequence at or before the entry that references it, an entry that carries SQL always names
    /// itself, and <c>0</c> (unknown) is only ever recorded for an entry without SQL.
    /// </summary>
    private static void ValidateStatementGroups(IReadOnlyList<ReplicaLocalChange> changes)
    {
        foreach (var change in changes)
        {
            var valid = change.CarriesStatementSql
                ? change.StatementSequence == change.Sequence
                : change.StatementSequence >= 0 && change.StatementSequence < change.Sequence;
            if (!valid)
            {
                throw new InvalidDataException(
                    "Managed replica change journal records an invalid statement grouping.");
            }
        }
    }

    /// <summary>
    /// Reads the persisted discard record (format 7 and later) and validates it in isolation:
    /// strictly ascending, positive, and never above the assigned high-water mark.
    /// </summary>
    private static List<long> ReadDiscarded(BinaryReader reader, Stream stream, long sequence)
    {
        var discardedCount = reader.ReadInt32();
        if (discardedCount < 0 || discardedCount > (stream.Length - stream.Position) / 8)
            throw new InvalidDataException("Managed replica change journal has an invalid discard count.");

        var discarded = new List<long>(discardedCount);
        long previousDiscarded = 0;
        for (var i = 0; i < discardedCount; i++)
        {
            var discardedSequence = reader.ReadInt64();
            if (discardedSequence <= previousDiscarded || discardedSequence > sequence)
                throw new InvalidDataException("Managed replica change journal discard record is not ordered.");
            discarded.Add(discardedSequence);
            previousDiscarded = discardedSequence;
        }

        return discarded;
    }

    /// <summary>
    /// Reconstructs the discard record for a pre-format-7 file. Formats 1-5 can never contain a
    /// gap (they had no discard operation and their strict tail check rejects one), so this yields
    /// an empty set for them. A format-6 file may contain gaps whose cause was not recorded; the
    /// only interpretation consistent with how the journal is written is that every such gap came
    /// from an explicit discard, so they are adopted as one and become exact on the next persist.
    /// </summary>
    private static List<long> InferDiscardedFromGaps(
        IReadOnlyList<ReplicaLocalChange> changes,
        long watermark,
        long sequence)
    {
        var retentionBase = changes.Count == 0 ? watermark : Math.Min(changes[0].Sequence, watermark);
        var discarded = new List<long>();
        if (retentionBase > sequence)
            return discarded;

        // Bound the reconstruction before walking it: `sequence` comes straight from the file
        // header and, unlike the entry and discard counts, is not constrained by the file's own
        // length. An implausible span is corruption, not a legitimate legacy file.
        var span = sequence - retentionBase + 1;
        if (span - changes.Count > MaxInferredDiscards)
        {
            throw new InvalidDataException(
                "Managed replica change journal implies an implausible number of discarded sequences.");
        }

        var index = 0;
        for (var candidate = retentionBase; candidate <= sequence; candidate++)
        {
            if (index < changes.Count && changes[index].Sequence == candidate)
            {
                index++;
                continue;
            }

            discarded.Add(candidate);
        }

        return discarded;
    }

    /// <summary>
    /// Proves the file is internally complete: from the lowest sequence still represented through
    /// the assigned high-water mark, every sequence is either retained or recorded as discarded,
    /// and no sequence is both. Appends assign contiguous sequences, a discard moves one entry from
    /// retained to discarded, and pruning removes a contiguous prefix from both, so any other shape
    /// means the file lost or duplicated evidence and must fail closed rather than be reinterpreted.
    /// </summary>
    private static void ValidateRetentionCompleteness(
        IReadOnlyList<ReplicaLocalChange> changes,
        IReadOnlyList<long> discarded,
        long watermark,
        long sequence)
    {
        if (changes.Count == 0 && discarded.Count == 0)
            return;

        var retentionBase = watermark;
        if (changes.Count != 0)
            retentionBase = Math.Min(retentionBase, changes[0].Sequence);
        if (discarded.Count != 0)
            retentionBase = Math.Min(retentionBase, discarded[0]);

        var expected = checked(sequence - retentionBase + 1);
        if (expected < 0 || changes.Count + (long)discarded.Count != expected)
        {
            throw new InvalidDataException(
                "Managed replica change journal is missing evidence for at least one assigned sequence.");
        }

        var changeIndex = 0;
        var discardIndex = 0;
        for (var candidate = retentionBase; candidate <= sequence; candidate++)
        {
            var retained = changeIndex < changes.Count && changes[changeIndex].Sequence == candidate;
            var dropped = discardIndex < discarded.Count && discarded[discardIndex] == candidate;
            if (retained == dropped)
            {
                throw new InvalidDataException(
                    "Managed replica change journal records a sequence as both retained and discarded.");
            }

            if (retained)
                changeIndex++;
            else
                discardIndex++;
        }
    }

    /// <summary>
    /// The lowest sequence for which this journal still holds evidence — retained, discarded, or
    /// the acknowledgement watermark itself when nothing is retained.
    /// </summary>
    internal long RetentionBase
    {
        get
        {
            lock (_gate)
            {
                var lowest = _watermark;
                if (_changes.Count != 0)
                    lowest = Math.Min(lowest, _changes[0].Sequence);
                if (_discarded.Count != 0)
                    lowest = Math.Min(lowest, _discarded[0]);
                return lowest;
            }
        }
    }

    /// <summary>
    /// The exclusive sequence boundary a remote server has confirmed. Used as the generation token
    /// that push publication re-validates under the physical apply lease before acknowledging.
    /// </summary>
    internal long AcknowledgedWatermark
    {
        get
        {
            lock (_gate)
                return _watermark;
        }
    }

    /// <summary>The highest sequence ever assigned by this journal. Never reused.</summary>
    internal long AssignedSequence
    {
        get
        {
            lock (_gate)
                return _sequence;
        }
    }

    /// <summary>A snapshot of the durably recorded discarded sequences, in ascending order.</summary>
    internal IReadOnlyList<long> DiscardedSequences
    {
        get
        {
            lock (_gate)
                return _discarded.ToArray();
        }
    }

    /// <summary>
    /// A cheap, total description of this journal's durable shape. Two journals over the same file
    /// describe the same generation only when every field matches, so a push publication can prove
    /// -- while holding the physical apply lease -- that no other process, alias, or thread wrote
    /// the journal while its network round trip was in flight. Persisting from a stale in-memory
    /// journal would otherwise clobber the other writer's appends, acknowledgements, or discards.
    /// </summary>
    internal ReplicaJournalGeneration Generation
    {
        get
        {
            lock (_gate)
                return new ReplicaJournalGeneration(_sequence, _watermark, _changes.Count, _discarded.Count);
        }
    }

    /// <summary>
    /// Whether <paramref name="sequence"/> is durably recorded as explicitly discarded. This is the
    /// only evidence that distinguishes "a discard already landed" from "the entry is missing", and
    /// therefore the only thing that may ever justify retiring a conflict marker whose sequences are
    /// no longer journaled.
    /// </summary>
    internal bool WasDiscarded(long sequence)
    {
        lock (_gate)
            return _discarded.BinarySearch(sequence) >= 0;
    }

    public ReplicaLocalChangeBatch ReadBatch(int maximumChanges)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumChanges);
        lock (_gate)
        {
            var pending = new List<ReplicaLocalChange>();
            foreach (var change in _changes)
            {
                if (change.Sequence >= _watermark)
                    pending.Add(change);
            }

            if (pending.Count == 0)
                return new ReplicaLocalChangeBatch(_watermark, _watermark, []);

            var count = Math.Min(maximumChanges, pending.Count);

            // Never cut a multi-row statement in half. The trailing rows of a statement carry no
            // SQL of their own, so a batch that ended inside one would leave rows that can never
            // be transmitted on their own while the acknowledgement moved past their statement.
            // The limit is therefore a floor, not a ceiling: a statement is always whole.
            while (count < pending.Count
                   && pending[count].StatementSequence != 0
                   && pending[count].StatementSequence == pending[count - 1].StatementSequence)
            {
                count++;
            }

            var batch = pending.GetRange(0, count).ToArray();
            var watermark = batch[^1].Sequence + 1;
            return new ReplicaLocalChangeBatch(batch[0].Sequence, watermark, batch);
        }
    }

    /// <summary>
    /// Proves that acknowledging <paramref name="batch"/> can only ever retire rows a push
    /// actually transmits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every entry must either carry its own replayable SQL, be covered by a statement replayed
    /// inside this very batch, or be covered by a statement that a previous batch already
    /// transmitted and had acknowledged. Anything else — most importantly a row whose statement
    /// was explicitly discarded after a conflict — would be silently retired by the batch
    /// watermark without ever having reached the server.
    /// </para>
    /// <para>
    /// This fails closed on purpose. The orphaned rows stay journaled and can be inspected and
    /// discarded explicitly; nothing about them is guessed or dropped.
    /// </para>
    /// </remarks>
    internal void ValidateBatchIsFullyReplayable(ReplicaLocalChangeBatch batch)
    {
        if (batch.Changes.Count == 0)
            return;

        var replayedInBatch = new HashSet<long>();
        foreach (var change in batch.Changes)
        {
            if (change.CarriesStatementSql)
                replayedInBatch.Add(change.Sequence);
        }

        lock (_gate)
        {
            foreach (var change in batch.Changes)
            {
                if (change.CarriesStatementSql || replayedInBatch.Contains(change.StatementSequence))
                    continue;

                // A statement below this batch's first sequence was transmitted (and confirmed)
                // by an earlier push, unless it was explicitly discarded instead.
                if (change.StatementSequence > 0
                    && change.StatementSequence < batch.FirstSequence
                    && change.StatementSequence < _watermark
                    && _discarded.BinarySearch(change.StatementSequence) < 0)
                {
                    continue;
                }

                throw new InvalidDataException(
                    $"Managed replica change journal entry {change.Sequence} belongs to a statement "
                    + "that is not part of this push and was never transmitted, so acknowledging the "
                    + "batch would discard a local change the remote never received. Inspect and "
                    + "discard the orphaned change(s) explicitly.");
            }
        }
    }

    /// <summary>
    /// Re-reads the exact sequence range a protected push attempt recorded. The range is allowed
    /// to contain holes, but only holes this journal durably recorded as explicit discards: every
    /// sequence in <c>[firstSequence, watermark)</c> must be either still retained or provably
    /// discarded. Anything else means the protected batch is no longer reconstructable and fails
    /// closed rather than being re-pushed as a silently different set.
    /// </summary>
    public ReplicaLocalChangeBatch ReadBatch(long firstSequence, long watermark)
    {
        if (firstSequence <= 0 || watermark <= firstSequence)
            throw new ArgumentOutOfRangeException(nameof(firstSequence));

        lock (_gate)
        {
            var batch = _changes
                .Where(change => change.Sequence >= firstSequence && change.Sequence < watermark)
                .ToArray();
            var discardedInRange = 0;
            foreach (var discarded in _discarded)
            {
                if (discarded >= firstSequence && discarded < watermark)
                    discardedInRange++;
            }

            if (batch.Length + (long)discardedInRange != watermark - firstSequence)
            {
                throw new InvalidDataException(
                    "Managed replica change journal no longer contains the protected push batch.");
            }

            return new ReplicaLocalChangeBatch(firstSequence, watermark, batch);
        }
    }

    public IReadOnlyList<ReplicaLocalChange> ReadAcknowledged(long afterWatermark)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(afterWatermark);
        lock (_gate)
        {
            return _changes
                .Where(change => change.Sequence >= afterWatermark && change.Sequence < _watermark)
                .ToArray();
        }
    }

    /// <summary>
    /// Durably appends locally committed changes, assigning each one the next sequence after the
    /// journal's <em>durable</em> high-water mark.
    /// </summary>
    /// <remarks>
    /// The whole append runs under the physical journal lease and re-reads the durable file first,
    /// so two <see cref="ManagedReplicaChangeJournal"/> instances -- two connections to the same
    /// replica, two aliases of the same file, or two processes -- interleave their appends instead
    /// of overwriting each other's. Sequences are therefore assigned against what is on disk at
    /// publication time, never against a snapshot taken when this instance happened to be opened.
    /// </remarks>
    public void AppendCommitted(IReadOnlyList<ReplicaLocalChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
            return;

        MutateDurable(() =>
        {
            var first = checked(_sequence + 1);
            var assigned = new ReplicaLocalChange[changes.Count];

            // Bind every row to the entry that carries the SQL replaying it, instead of letting
            // that relationship be implied by adjacency. Once recorded, a discard, a prune, or a
            // batch boundary can no longer silently separate a row from the statement that would
            // have transmitted it.
            long statement = 0;
            for (var i = 0; i < changes.Count; i++)
            {
                var change = changes[i];
                var sequence = checked(first + i);
                if (change.CarriesStatementSql)
                    statement = sequence;

                assigned[i] = change with
                {
                    Sequence = sequence,
                    StatementSequence = change.CarriesStatementSql ? sequence : statement,
                };
            }

            var nextSequence = assigned[^1].Sequence;
            var merged = _changes.Concat(assigned).ToArray();
            ValidateMergedState(nextSequence, _watermark, merged, _discarded);
            Persist(nextSequence, _watermark, merged, _discarded);
            ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.JournalAppendPersisted);
            _changes.AddRange(assigned);
            _sequence = nextSequence;
        });
    }

    /// <summary>
    /// Durably discards changes below an exclusive watermark after their enclosing remote
    /// transaction has committed. Failed, cancelled, and conflicting pushes never call this.
    /// </summary>
    public void Acknowledge(long watermark)
    {
        MutateDurable(() =>
        {
            if (watermark < _watermark || watermark > checked(_sequence + 1))
                throw new ArgumentOutOfRangeException(nameof(watermark));

            if (watermark == _watermark)
                return;

            Persist(_sequence, watermark, _changes, _discarded);
            ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.JournalAcknowledgementPersisted);
            _watermark = watermark;
        });
    }

    /// <summary>
    /// Durably removes specific still-pending changes that an application explicitly abandoned
    /// after a push conflict, without ever having pushed them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is deliberately NOT <see cref="Acknowledge"/> and deliberately does not move the
    /// journal's acknowledgement watermark: that watermark's only meaning is "the exclusive
    /// boundary a remote server has confirmed", and conflating a local discard with a remote acknowledgement would
    /// make an audit — or the staleness comparison in
    /// <c>ManagedReplicaBootstrapper.TryUseCurrentLocalStateAsPullBase</c> — unable to tell the
    /// two apart. Discarded sequences simply stop being retained; the assigned high-water mark
    /// stays where it is, so no future change can ever reuse one of them.
    /// </para>
    /// <para>
    /// Each discarded sequence is durably recorded in its own right, so the resulting hole is
    /// provable evidence of an intentional discard rather than an unexplained absence. That is
    /// what lets an interrupted discard be completed idempotently while a genuinely missing or
    /// corrupt entry still fails closed.
    /// </para>
    /// <para>
    /// Every requested sequence must currently be retained and still pending (at or above the
    /// watermark). Anything else fails closed rather than silently discarding a different set.
    /// </para>
    /// </remarks>
    /// <returns>The number of changes removed.</returns>
    public int DiscardUnacknowledged(IReadOnlyList<long> sequences)
    {
        ArgumentNullException.ThrowIfNull(sequences);
        if (sequences.Count == 0)
            return 0;

        return MutateDurable(() =>
        {
            var requested = new HashSet<long>(sequences.Count);
            foreach (var sequence in sequences)
            {
                if (sequence < _watermark)
                {
                    throw new InvalidOperationException(
                        "Managed replica change journal cannot discard an acknowledged change.");
                }
                if (!requested.Add(sequence))
                {
                    throw new ArgumentException(
                        "Managed replica change journal discard requested a duplicate sequence.",
                        nameof(sequences));
                }
            }

            var retained = new List<ReplicaLocalChange>(_changes.Count);
            var discarded = new List<long>(_discarded.Count + requested.Count);
            discarded.AddRange(_discarded);

            // A statement is discarded whole or not at all. Removing the entry that carries the
            // SQL while leaving its trailing rows behind (or the reverse) would strand rows that
            // no push could ever transmit, and the next batch's watermark would retire them
            // silently. Prove group closure before anything is written.
            var requestedStatements = new HashSet<long>();
            foreach (var change in _changes)
            {
                if (change.StatementSequence != 0 && requested.Contains(change.Sequence))
                    requestedStatements.Add(change.StatementSequence);
            }

            foreach (var change in _changes)
            {
                if (change.StatementSequence != 0
                    && requestedStatements.Contains(change.StatementSequence)
                    && !requested.Contains(change.Sequence))
                {
                    throw new InvalidOperationException(
                        "Managed replica change journal cannot discard part of a statement: sequence "
                        + $"{change.Sequence} belongs to statement {change.StatementSequence}, which the "
                        + "discard would only partially remove.");
                }
            }

            var removed = 0;
            foreach (var change in _changes)
            {
                if (requested.Remove(change.Sequence))
                {
                    discarded.Add(change.Sequence);
                    removed++;
                    continue;
                }

                retained.Add(change);
            }

            if (requested.Count != 0)
            {
                throw new InvalidDataException(
                    "Managed replica change journal no longer contains every change requested for discard.");
            }

            discarded.Sort();
            ValidateMergedState(_sequence, _watermark, retained, discarded);
            Persist(_sequence, _watermark, retained, discarded);
            ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.JournalDiscardPersisted);
            _changes.Clear();
            _changes.AddRange(retained);
            _discarded.Clear();
            _discarded.AddRange(discarded);
            return removed;
        });
    }

    public void PruneAcknowledged(long throughWatermark)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(throughWatermark);
        MutateDurable(() =>
        {
            var effectiveWatermark = Math.Min(throughWatermark, _watermark);
            var prunableChanges = _changes.Count != 0 && _changes[0].Sequence < effectiveWatermark;
            var prunableDiscards = _discarded.Count != 0 && _discarded[0] < effectiveWatermark;
            if (!prunableChanges && !prunableDiscards)
                return;

            var retained = _changes.Where(change => change.Sequence >= effectiveWatermark).ToArray();
            var discarded = _discarded.Where(sequence => sequence >= effectiveWatermark).ToArray();
            Persist(_sequence, _watermark, retained, discarded);
            _changes.RemoveAll(change => change.Sequence < effectiveWatermark);
            _discarded.RemoveAll(sequence => sequence < effectiveWatermark);
        });
    }

    private void Persist(
        long sequence,
        long watermark,
        IReadOnlyList<ReplicaLocalChange> changes,
        IReadOnlyList<long> discarded)
    {
        // A deterministic sibling staging name (rather than a random one) means an interrupted
        // persist can never leave an unbounded set of data-bearing leftovers behind: there is at
        // most one, it is part of the replica's declared artifact set, and the next persist or
        // open removes it. Publication of the durable file is still an atomic replace.
        var stagingPath = _stagingPath;
        try
        {
            DeleteStagingArtifact(stagingPath, throwOnFailure: true);
            using (var stream = new FileStream(
                       stagingPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(sequence);
                writer.Write(watermark);
                writer.Write(changes.Count);
                foreach (var change in changes)
                    WriteChange(writer, change);
                writer.Write(discarded.Count);
                foreach (var discardedSequence in discarded)
                    writer.Write(discardedSequence);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
                File.Replace(stagingPath, _path, destinationBackupFileName: null, ignoreMetadataErrors: false);
            else
                File.Move(stagingPath, _path, overwrite: false);
        }
        finally
        {
            if (File.Exists(stagingPath))
                File.Delete(stagingPath);
        }
    }

    private static void DeleteStagingArtifact(string stagingPath, bool throwOnFailure)
    {
        try
        {
            if (File.Exists(stagingPath))
                File.Delete(stagingPath);
        }
        catch (Exception exception) when (!throwOnFailure
                                          && exception is IOException or UnauthorizedAccessException)
        {
            // Opening a journal must never fail because a leftover staging file is momentarily
            // locked: the durable file is authoritative and complete on its own. The next persist
            // deletes the leftover with throwOnFailure, so a genuinely stuck artifact still
            // surfaces before anything depends on writing over it.
        }
    }

    private static void WriteChange(BinaryWriter writer, ReplicaLocalChange change)
    {
        writer.Write(change.Sequence);
        writer.Write((byte)change.Kind);
        writer.Write(change.StatementSequence);
        switch (change.Kind)
        {
            case ReplicaLocalChangeKind.Row:
                writer.Write((int)change.Operation);
                WriteString(writer, change.Database);
                WriteString(writer, change.Table);
                writer.Write(change.RowId);
                WriteString(writer, change.Sql);
                WriteBytes(writer, change.BeforeRecord);
                break;
            case ReplicaLocalChangeKind.Schema:
                WriteString(writer, change.Sql);
                break;
            default:
                throw new InvalidDataException("Managed replica change journal has an unknown change kind.");
        }
    }

    private static ReplicaLocalChange ReadChange(BinaryReader reader, int formatVersion)
    {
        var sequence = reader.ReadInt64();
        var kind = (ReplicaLocalChangeKind)reader.ReadByte();
        var statementSequence = formatVersion >= Version ? reader.ReadInt64() : 0;
        return kind switch
        {
            ReplicaLocalChangeKind.Row => new ReplicaLocalChange(
                sequence,
                kind,
                (SqliteChangeOperation)reader.ReadInt32(),
                ReadString(reader),
                ReadString(reader),
                reader.ReadInt64(),
                formatVersion >= 3 ? ReadString(reader) : string.Empty,
                formatVersion >= 4 ? ReadBytes(reader) : null,
                statementSequence),
            ReplicaLocalChangeKind.Schema => ReplicaLocalChange.Schema(ReadString(reader)) with
            {
                Sequence = sequence,
                StatementSequence = statementSequence,
            },
            _ => throw new InvalidDataException("Managed replica change journal has an unknown change kind."),
        };
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > MaxStringBytes)
            throw new InvalidDataException("Managed replica change journal entry is too large.");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteBytes(BinaryWriter writer, byte[]? value)
    {
        if (value is null)
        {
            writer.Write(-1);
            return;
        }

        if (value.Length > MaxBinaryBytes)
            throw new InvalidDataException("Managed replica change journal binary entry is too large.");
        writer.Write(value.Length);
        writer.Write(value);
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > MaxStringBytes)
            throw new InvalidDataException("Managed replica change journal contains an invalid string.");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException("Managed replica change journal is truncated.");
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Managed replica change journal contains invalid UTF-8.", exception);
        }
    }

    private static byte[]? ReadBytes(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length == -1)
            return null;
        if (length < 0 || length > MaxBinaryBytes)
            throw new InvalidDataException("Managed replica change journal contains an invalid binary entry.");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException("Managed replica change journal is truncated.");
        return bytes;
    }
}
