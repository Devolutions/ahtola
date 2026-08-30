using System.Collections.ObjectModel;

namespace Ahtola.Core.Indexing;

/// <summary>
/// How much of the managed MVCC contract an index method can honor.
/// Ports Turso's <c>IndexMethodMvccSupport</c> (turso-src/core/index_method/mod.rs:52-106).
/// </summary>
internal enum ManagedIndexMethodMvccSupport
{
    /// <summary>The method cannot participate in MVCC at all: reads and writes both fail closed.</summary>
    Unsupported = 0,

    /// <summary>Queries are allowed under MVCC but writes fail closed.</summary>
    ReadOnly = 1,

    /// <summary>
    /// All method state lives in storage the engine already keeps transactional, so snapshot,
    /// rollback and savepoint semantics are inherited rather than reimplemented.
    /// </summary>
    TransactionalBackingStore = 2,

    /// <summary>The method owns an external transactional store and coordinates commits itself.</summary>
    ExternalTransactional = 3,
}

/// <summary>The shape of a planner-recognized query a method can serve.</summary>
internal enum ManagedIndexPatternShape
{
    /// <summary>WHERE match(cols…, ?).</summary>
    Match = 0,

    /// <summary>WHERE match(cols…, ?) LIMIT ?.</summary>
    MatchLimit = 1,

    /// <summary>Pinned FTS score-only ORDER BY DESC LIMIT pattern.</summary>
    Score = 2,

    /// <summary>Legacy generic score-order shape retained for compatibility with older adapters.</summary>
    ScoreOrdered = 3,

    /// <summary>Legacy generic limited score-order shape retained for compatibility.</summary>
    ScoreOrderedLimit = 4,

    /// <summary>Reserved for the vector method: ORDER BY distance(col, ?) ASC.</summary>
    Knn = 5,

    /// <summary>Reserved for the vector method: ORDER BY distance(col, ?) ASC LIMIT ?.</summary>
    KnnLimit = 6,

    /// <summary>SELECT score(cols…, ?) … WHERE match(cols…, ?) with no ordering or limit.</summary>
    Combined = 7,

    /// <summary>Combined score/match with an unordered LIMIT.</summary>
    CombinedLimit = 8,

    /// <summary>Combined score/match ordered by score descending.</summary>
    CombinedOrdered = 9,

    /// <summary>Combined score/match ordered by score descending with LIMIT.</summary>
    CombinedOrderedLimit = 10,
}

/// <summary>
/// How a scan plan that ranks only some rows must combine those ranked hits with the base rows the
/// method left unranked. This is a per-method choice, not a global rule: methods disagree on which
/// direction "better" sorts, so a single hard-coded merge order would silently corrupt the other
/// method's results.
/// </summary>
internal enum ManagedIndexUnrankedMergePolicy
{
    /// <summary>
    /// Emit every ranked hit first (in the method's own order), then append the unranked rows in
    /// ascending rowid order. Correct whenever the statement either has no unranked rows to worry
    /// about or truncates before the unranked rows could ever be observed — e.g. the vector method's
    /// KNN shapes, which decline <c>ORDER BY distance ASC</c> pushdown unless the LIMIT is small
    /// enough that unranked rows are provably farther than every kept hit.
    /// </summary>
    Append = 0,

    /// <summary>
    /// Interleave ranked and unranked rows into a single <c>(rank DESC, rowid ASC)</c> order before
    /// any truncation happens. Required whenever the statement's real ordering is by score/rank and
    /// unranked rows are assigned a real, comparable rank (see <see cref="ManagedIndexMethodPatternMatch.UnrankedRank"/>)
    /// rather than being pushed to the back regardless of how they would actually compare — otherwise
    /// a LIMIT can keep the wrong rows whenever ranked and unranked scores can tie or interleave.
    /// </summary>
    MergeByDescendingRank = 1,
}

/// <summary>One column bound to a method index, in declaration order, with optional field options.</summary>
internal sealed record ManagedIndexMethodColumn(
    string Name,
    int ColumnIndex,
    IReadOnlyList<ManagedIndexMethodParameter>? Parameters = null);

/// <summary>One <c>WITH (key = literal)</c> entry captured by the parser.</summary>
internal sealed record ManagedIndexMethodParameter(string Name, SqlValue Value);

/// <summary>
/// The immutable configuration handed to <see cref="ManagedIndexMethod.Attach"/>.
/// Ports Turso's <c>IndexMethodConfiguration</c> (mod.rs:33-43).
/// </summary>
internal sealed class ManagedIndexMethodConfiguration
{
    public ManagedIndexMethodConfiguration(
        string tableName,
        string indexName,
        IReadOnlyList<ManagedIndexMethodColumn> columns,
        IReadOnlyList<ManagedIndexMethodParameter> parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(parameters);

        TableName = tableName;
        IndexName = indexName;
        Columns = Array.AsReadOnly(columns.ToArray());
        Parameters = Array.AsReadOnly(parameters.ToArray());
    }

    public string TableName { get; }

    public string IndexName { get; }

    public IReadOnlyList<ManagedIndexMethodColumn> Columns { get; }

    public IReadOnlyList<ManagedIndexMethodParameter> Parameters { get; }

    /// <summary>Looks up one <c>WITH</c> parameter by ASCII case-insensitive key.</summary>
    public bool TryGetParameter(string name, out SqlValue value)
    {
        foreach (var parameter in Parameters)
        {
            if (string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = parameter.Value;
                return true;
            }
        }

        value = SqlValue.Null;
        return false;
    }
}

/// <summary>One planner-visible query shape declared by a method. Ports Turso's parsed patterns (mod.rs:74).</summary>
internal readonly record struct ManagedIndexQueryPattern(ManagedIndexPatternShape Shape, int ArgumentCount);

/// <summary>
/// The immutable description of an attached method index.
/// Ports Turso's <c>IndexMethodDefinition</c> (mod.rs:65-84).
/// </summary>
internal sealed class ManagedIndexMethodDefinition
{
    public ManagedIndexMethodDefinition(
        string methodName,
        string indexName,
        IReadOnlyList<ManagedIndexQueryPattern> patterns,
        bool backingBtree,
        bool resultsMaterialized,
        ManagedIndexMethodMvccSupport mvccSupport,
        int storageVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        ArgumentNullException.ThrowIfNull(patterns);
        if (storageVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(storageVersion));

        MethodName = methodName;
        IndexName = indexName;
        Patterns = Array.AsReadOnly(patterns.ToArray());
        BackingBtree = backingBtree;
        ResultsMaterialized = resultsMaterialized;
        MvccSupport = mvccSupport;
        StorageVersion = storageVersion;
    }

    public string MethodName { get; }

    public string IndexName { get; }

    /// <summary>Declared most-specific first; the planner matches in this order.</summary>
    public IReadOnlyList<ManagedIndexQueryPattern> Patterns { get; }

    /// <summary>True when the method's durable state is the ordinary index b-tree itself.</summary>
    public bool BackingBtree { get; }

    /// <summary>True when a query materializes its whole result before the first row is read.</summary>
    public bool ResultsMaterialized { get; }

    public ManagedIndexMethodMvccSupport MvccSupport { get; }

    /// <summary>Persisted with the index; a newer value than this build understands fails closed.</summary>
    public int StorageVersion { get; }

    public bool TryFindPattern(ManagedIndexPatternShape shape, out int patternIndex)
    {
        for (var index = 0; index < Patterns.Count; index++)
        {
            if (Patterns[index].Shape == shape)
            {
                patternIndex = index;
                return true;
            }
        }

        patternIndex = -1;
        return false;
    }
}

/// <summary>Cost the planner compares against ordinary scan and b-tree access paths.</summary>
/// <param name="EstimatedCost">Expected work in the same row-read unit the join cost model uses.</param>
/// <param name="EstimatedRows">Rows the access path is expected to produce.</param>
/// <param name="Detail">
/// An optional short, method-owned description of the priced plan (probe counts, mode flags) that
/// EXPLAIN QUERY PLAN appends verbatim. The core never parses it, so a method can surface whatever
/// makes its plan auditable without the planner learning anything about that method.
/// </param>
/// <param name="RefreshCost">
/// The part of <paramref name="EstimatedCost"/> that is one-time reconciliation of derived state
/// with the base rows: the walk, and any training or rebuild, the next use of this index will be
/// forced to perform. It is reported separately because it is a cost of <em>having</em> the index,
/// not of choosing this access path — the same reconciliation is owed to a scalar call evaluated on
/// the plain scan — so the planner amortizes it out of the comparison while still being able to see
/// it, price it, and prefer an already-reconciled index over a cold one.
/// </param>
internal readonly record struct ManagedIndexMethodCostEstimate(
    double EstimatedCost,
    long EstimatedRows,
    string? Detail = null,
    double RefreshCost = 0.0)
{
    /// <summary>
    /// The recurring cost of this access path once its derived state is current, which is what a
    /// full scan is actually being compared against.
    /// </summary>
    public double SteadyStateCost => Math.Max(EstimatedCost - Math.Max(RefreshCost, 0.0), 0.0);
}

/// <summary>
/// One priced access-path candidate, produced without opening a scan or reconciling anything.
/// </summary>
/// <remarks>
/// This is the deferred half of planning. A method is asked what a pattern would cost while its
/// derived state is still whatever it happens to be; nothing is trained, rebuilt, placed or
/// published to answer. Only the candidate the planner actually selects is later opened, and only
/// that one pays for reconciliation. Pricing every candidate by first bringing it up to date is
/// what made EXPLAIN QUERY PLAN rebuild cold indexes and made a table with three method indexes
/// rebuild all three to use one.
/// </remarks>
/// <param name="Attachment">The attachment that produced the price.</param>
/// <param name="PatternIndex">The declared pattern that was priced.</param>
/// <param name="Estimate">What the method says the plan costs.</param>
internal readonly record struct ManagedIndexMethodCostSnapshot(
    ManagedIndexMethodAttachment Attachment,
    int PatternIndex,
    ManagedIndexMethodCostEstimate Estimate);

/// <summary>Inputs a method receives when the planner asks it to price one pattern.</summary>
/// <param name="PatternIndex">Which declared pattern is being priced.</param>
/// <param name="BaseTableRows">Live rows in the base table.</param>
/// <param name="Limit">The literal LIMIT the planner would push down, when there is one.</param>
/// <param name="Arguments">Row-independent query arguments, when the planner can evaluate them.</param>
/// <param name="RetainsUnrankedRows">
/// True when the engine will still emit every base row this pattern does not rank. A method must
/// price that honestly: a plan that produces the whole table cannot be cheaper than the scan it
/// would replace, whatever its ranking costs.
/// </param>
internal readonly record struct ManagedIndexMethodCostContext(
    int PatternIndex,
    long BaseTableRows,
    long? Limit,
    IReadOnlyList<SqlValue> Arguments,
    bool RetainsUnrankedRows = true);

/// <summary>
/// One method-produced result row, in the method-agnostic shape the core execution path consumes:
/// a base-table rowid plus the method's result columns. Column 0 is the method's rank value (a BM25
/// score for FTS, a distance for the vector method); anything beyond it is method defined.
/// </summary>
/// <remarks>
/// This is the contract that keeps <c>GetMethodIndexRows</c> free of method-specific casts. A
/// method never hands the engine its own hit type; it hands back rowids and columns.
/// </remarks>
internal readonly record struct ManagedIndexMethodResultRow(long RowId, SqlValue[] Columns)
{
    public SqlValue Column(int index)
        => Columns is not null && index >= 0 && index < Columns.Length ? Columns[index] : SqlValue.Null;

    /// <summary>Column 0 read as a double, or 0 when the method produced no rank value.</summary>
    public double Rank
        => Column(0) is { Kind: SqlValueKind.Real } real
            ? real.AsReal()
            : Column(0) is { Kind: SqlValueKind.Integer } integer
                ? integer.AsInteger()
                : 0.0;
}

/// <summary>
/// Registration surface for a managed index method. Instances are singletons registered by a direct
/// managed call from a static constructor, never by reflection, so the mechanism stays NativeAOT and
/// trimming safe. Ports Turso's <c>IndexMethod</c> factory (mod.rs:25-31).
/// </summary>
internal abstract class ManagedIndexMethod
{
    public abstract string Name { get; }

    /// <summary>Whether this method accepts field-local <c>WITH</c> options.</summary>
    public virtual bool SupportsColumnParameters => false;

    /// <summary>Validates the configuration and produces the immutable attachment for one index.</summary>
    public abstract ManagedIndexMethodAttachment Attach(ManagedIndexMethodConfiguration configuration);
}

/// <summary>
/// One attached method index: immutable configuration plus the durable state box.
/// Ports Turso's <c>IndexMethodAttachment</c> (mod.rs:47-50).
/// </summary>
internal abstract class ManagedIndexMethodAttachment
{
    public abstract ManagedIndexMethodDefinition Definition { get; }

    public abstract ManagedIndexMethodConfiguration Configuration { get; }

    /// <summary>
    /// The method-specific planner half. The core planner calls only this; it never inspects a
    /// method's SQL surface itself.
    /// </summary>
    public abstract IManagedIndexMethodPlannerAdapter Planner { get; }

    /// <summary>Opens a per-statement, single-threaded cursor. The caller disposes it.</summary>
    public abstract ManagedIndexMethodCursor Open(IManagedIndexSource source);

    /// <summary>
    /// Prices one pattern against a base-row source without reconciling any derived state.
    /// </summary>
    /// <remarks>
    /// The probe cursor is opened and disposed without <see cref="ManagedIndexMethodCursor.OpenRead"/>
    /// or <see cref="ManagedIndexMethodCursor.OpenWrite"/>, which are the only two entry points that
    /// refresh, so this is a read of the method's opinion rather than a use of the method. The
    /// planner calls it once per candidate and opens exactly one of them for real afterwards.
    /// </remarks>
    public ManagedIndexMethodCostSnapshot? EstimateCost(
        IManagedIndexSource source,
        in ManagedIndexMethodCostContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var probe = Open(source);
        return probe.EstimateCost(context) is { } estimate
            ? new ManagedIndexMethodCostSnapshot(this, context.PatternIndex, estimate)
            : null;
    }

    /// <summary>Serializes the versioned, method-owned state envelope persisted with the catalog row.</summary>
    public abstract byte[] SaveState();

    /// <summary>Restores a previously serialized envelope, failing closed on version or shape errors.</summary>
    public abstract void LoadState(int version, ReadOnlySpan<byte> bytes);

    /// <summary>
    /// Produces an independent attachment that shares no mutable state with this one. Catalog
    /// snapshots (transactions, savepoints, DDL rollback) call this so a rolled-back statement
    /// cannot leave method state behind.
    /// </summary>
    public abstract ManagedIndexMethodAttachment Fork();

    /// <summary>
    /// True when either attachment may answer the same scalar/prefilter call without changing its
    /// observable result. The conservative default treats independently configured indexes as
    /// interchangeable when they implement the same method; methods whose configuration changes
    /// scalar semantics override this with a stronger comparison.
    /// </summary>
    public virtual bool HasEquivalentQuerySemantics(ManagedIndexMethodAttachment other)
        => string.Equals(
            Definition.MethodName,
            other.Definition.MethodName,
            StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The base rows a method index derives from. The engine already keeps this snapshot-isolated per
/// transaction and savepoint, so a method that only derives state from it inherits atomicity.
/// </summary>
internal interface IManagedIndexSource
{
    /// <summary>Number of live base rows.</summary>
    int RowCount { get; }

    /// <summary>
    /// A monotonically increasing counter bumped by every base-row mutation. A method compares it
    /// against the revision it last reconciled at, so an unchanged table costs O(1) instead of a
    /// full base-row walk.
    /// </summary>
    long Revision { get; }

    /// <summary>
    /// The rowids mutated between <paramref name="sinceRevision"/> and <see cref="Revision"/>, or
    /// null when the engine cannot prove it observed every mutation in that range. A null result
    /// forces a full rebuild, which the cost model prices explicitly.
    /// </summary>
    ManagedIndexSourceDelta? TryGetDelta(long sinceRevision);

    /// <summary>
    /// Tells the engine that a method just re-derived its whole state from the base rows at
    /// <paramref name="revision"/>, so the mutation journal can drop everything older and clear any
    /// gap it had recorded.
    /// </summary>
    void NotifyRebuilt(long revision);

    /// <summary>The rowid of base row <paramref name="position"/>.</summary>
    long GetRowId(int position);

    /// <summary>
    /// The row array of base row <paramref name="position"/>. Callers may use reference identity as
    /// a change detector: every engine mutation path replaces the array rather than mutating it.
    /// </summary>
    SqlValue[] GetRow(int position);

    /// <summary>Finds the position of a rowid, or false when the row is not live.</summary>
    bool TryGetPosition(long rowId, out int position);
}

/// <summary>A contiguous run of recorded base-row mutations a method can apply incrementally.</summary>
/// <param name="Revision">The base-row revision the delta brings a method up to.</param>
/// <param name="ChangedRowIds">
/// Every rowid touched in the range. The list may contain a rowid more than once and may name a
/// rowid whose value did not actually change: re-deriving an unchanged row is idempotent, so a
/// superset is always safe. What it must never do is omit a rowid that changed.
/// </param>
internal sealed record ManagedIndexSourceDelta(long Revision, IReadOnlyList<long> ChangedRowIds);

/// <summary>
/// One open scan or maintenance handle. Per statement, single threaded, disposed at statement
/// finalize/reset. Ports Turso's <c>IndexMethodCursor</c> (mod.rs:148-243).
/// </summary>
internal abstract class ManagedIndexMethodCursor : IDisposable
{
    /// <summary>Allocates method state for a freshly created index.</summary>
    public abstract void Create();

    /// <summary>Releases all method state for a dropped index.</summary>
    public abstract void Destroy();

    /// <summary>Prepares the cursor for reads.</summary>
    public abstract void OpenRead();

    /// <summary>Prepares the cursor for writes.</summary>
    public abstract void OpenWrite();

    /// <summary>Applies one base-row insert. Values are the index columns in declaration order, rowid last.</summary>
    public abstract void Insert(ReadOnlySpan<SqlValue> values);

    /// <summary>Applies one base-row delete with the same layout as <see cref="Insert"/>.</summary>
    public abstract void Delete(ReadOnlySpan<SqlValue> values);

    /// <summary>
    /// Applies one base-row update. The default replays it as a delete of the old image followed by
    /// an insert of the new one, which is exactly right for a method whose state is keyed by rowid.
    /// </summary>
    public virtual void Update(ReadOnlySpan<SqlValue> oldValues, ReadOnlySpan<SqlValue> newValues)
    {
        Delete(oldValues);
        Insert(newValues);
    }

    /// <summary>Number of result columns <see cref="Column"/> can produce for the current pattern.</summary>
    public virtual int ResultColumnCount => 1;

    /// <summary>Positions the cursor on the first result of <paramref name="patternIndex"/>.</summary>
    public abstract bool QueryStart(int patternIndex, ReadOnlySpan<SqlValue> arguments);

    /// <summary>Advances to the next result.</summary>
    public abstract bool QueryNext();

    /// <summary>Reads one result column. Column 0 is always the method score.</summary>
    public abstract SqlValue Column(int index);

    /// <summary>The base-table rowid of the current result, used to seek back to the base row.</summary>
    public abstract long? RowId();

    /// <summary>
    /// Drains the whole current query into the method-agnostic result-row contract. The core
    /// execution path consumes only this, so it never needs to know which method produced the rows.
    /// </summary>
    public IReadOnlyList<ManagedIndexMethodResultRow> Drain(
        int patternIndex,
        ReadOnlySpan<SqlValue> arguments,
        int? maximumRows = null)
    {
        var rows = new List<ManagedIndexMethodResultRow>();
        var columnCount = Math.Max(ResultColumnCount, 1);
        var more = QueryStart(patternIndex, arguments);
        while (more)
        {
            if (maximumRows is { } cap && rows.Count >= cap)
                break;

            if (RowId() is { } rowId)
            {
                var columns = new SqlValue[columnCount];
                for (var index = 0; index < columnCount; index++)
                    columns[index] = Column(index);

                rows.Add(new ManagedIndexMethodResultRow(rowId, columns));
            }

            more = QueryNext();
        }

        return rows;
    }

    /// <summary>Flushes any in-memory hot state before the enclosing transaction commits.</summary>
    public virtual void PreCommit()
    {
    }

    /// <summary>Compacts method state. Never runs inline in a user DML statement.</summary>
    public virtual void Optimize()
    {
    }

    /// <summary>Discards and rebuilds all method state from the base rows.</summary>
    public virtual void Rebuild()
    {
    }

    /// <summary>Prices one pattern, or returns null to let the planner fall back to a scan.</summary>
    public virtual ManagedIndexMethodCostEstimate? EstimateCost(in ManagedIndexMethodCostContext context) => null;

    public virtual void Dispose()
    {
    }
}

/// <summary>
/// Ports Turso's <c>ensure_mvcc_support</c> (mod.rs:86-106): a method must declare storage that
/// actually honors snapshot isolation before the engine lets it read or write under MVCC.
/// </summary>
internal static class ManagedIndexMethodMvcc
{
    public static void Ensure(ManagedIndexMethodDefinition definition, bool mvccEnabled, bool forWrite)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!mvccEnabled)
            return;

        switch (definition.MvccSupport)
        {
            case ManagedIndexMethodMvccSupport.TransactionalBackingStore:
            case ManagedIndexMethodMvccSupport.ExternalTransactional:
                return;
            case ManagedIndexMethodMvccSupport.ReadOnly when !forWrite:
                return;
            case ManagedIndexMethodMvccSupport.ReadOnly:
                throw new EmbeddedSqlException(
                    $"index method '{definition.MethodName}' is read-only in MVCC");
            default:
                throw new EmbeddedSqlException(
                    $"index method '{definition.MethodName}' does not support MVCC");
        }
    }
}

/// <summary>
/// Global explicit registry. Registration is a direct managed call from a static constructor, not
/// assembly scanning, mirroring <c>ManagedVirtualTableModuleRegistry</c> so NativeAOT and trimming
/// analysis can see every reachable method.
/// </summary>
internal static class ManagedIndexMethodRegistry
{
    private static readonly object Gate = new();

    private static readonly Dictionary<string, ManagedIndexMethod> Methods =
        new(StringComparer.OrdinalIgnoreCase);

    static ManagedIndexMethodRegistry()
    {
        Register(Search.ManagedFtsIndexMethod.Instance);
        Register(Vectors.ManagedVectorIndexMethod.Instance);
    }

    public static void Register(ManagedIndexMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (string.IsNullOrWhiteSpace(method.Name))
            throw new ArgumentException("An index method name cannot be empty.", nameof(method));

        lock (Gate)
        {
            if (!Methods.TryAdd(method.Name, method))
                throw new InvalidOperationException($"A managed index method named '{method.Name}' is already registered.");
        }
    }

    public static bool TryResolve(string name, out ManagedIndexMethod method)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (Gate)
            return Methods.TryGetValue(name, out method!);
    }

    public static ManagedIndexMethod Resolve(string name)
        => TryResolve(name, out var method)
            ? method
            : throw new EmbeddedSqlException($"no such index method: {name}");

    /// <summary>Registered method names, for diagnostics and tests.</summary>
    public static IReadOnlyCollection<string> Names
    {
        get
        {
            lock (Gate)
                return new ReadOnlyCollection<string>(Methods.Keys.ToArray());
        }
    }
}

/// <summary>
/// Canonical <c>WITH (...)</c> rendering for a method index, shared by every method so the catalog
/// round-trip cannot drift between them.
/// </summary>
internal static class ManagedIndexMethodParameterFormatter
{
    /// <summary>Rebuilds the <c>WITH</c> text, or null when the index declared no parameters.</summary>
    public static string? Format(IReadOnlyList<ManagedIndexMethodParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.Count == 0)
            return null;

        var builder = new System.Text.StringBuilder();
        for (var index = 0; index < parameters.Count; index++)
        {
            if (index > 0)
                builder.Append(", ");

            builder.Append(parameters[index].Name).Append(" = ").Append(FormatLiteral(parameters[index].Value));
        }

        return builder.ToString();
    }

    private static string FormatLiteral(SqlValue value)
        => value.Kind switch
        {
            SqlValueKind.Null => "NULL",
            SqlValueKind.Integer => value.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture),
            SqlValueKind.Real => value.AsReal().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            SqlValueKind.Text => "'" + value.AsText().Replace("'", "''", StringComparison.Ordinal) + "'",
            SqlValueKind.Blob => "X'" + Convert.ToHexString(value.AsBlob().Span) + "'",
            _ => throw new EmbeddedSqlException("unsupported index method parameter literal"),
        };
}

/// <summary>
/// Process-wide diagnostic counters that make "planning did not do any work" an assertion rather
/// than an inference.
/// </summary>
/// <remarks>
/// A rebuild is the expensive half of a method index: a full base-row walk plus whatever the method
/// derives from it — posting construction for FTS, k-means and re-placement for the vector method.
/// Counting publications is how a test proves that EXPLAIN QUERY PLAN priced three candidate
/// indexes and rebuilt none of them, which no result set could ever show.
/// </remarks>
internal static class ManagedIndexMethodDiagnostics
{
    private static long _stateRebuilds;

    /// <summary>How many times a method has published a freshly derived state.</summary>
    public static long StateRebuilds => Interlocked.Read(ref _stateRebuilds);

    /// <summary>Called by a method immediately before it publishes a rebuilt structure.</summary>
    public static void RecordStateRebuild() => Interlocked.Increment(ref _stateRebuilds);
}

/// <summary>Engine-wide limits every method shares, so no method can be coaxed into unbounded work.</summary>
internal static class ManagedIndexMethodLimits
{
    /// <summary>Maximum columns one method index may cover (a 32-bit column mask).</summary>
    public const int MaxIndexedColumns = 32;

    /// <summary>Maximum <c>WITH</c> entries accepted by the parser.</summary>
    public const int MaxParameters = 32;

    /// <summary>Maximum serialized method state persisted alongside the catalog row.</summary>
    public const int MaxStateBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Maximum base64 characters accepted for a persisted state envelope. Checked before the
    /// decode allocates, so a hostile catalog row cannot make the loader materialize a huge buffer
    /// only to reject it afterwards. Base64 expands 3 bytes into 4 characters, plus padding.
    /// </summary>
    public const int MaxStateEncodedChars = (((MaxStateBytes + 2) / 3) * 4) + 4;
}
