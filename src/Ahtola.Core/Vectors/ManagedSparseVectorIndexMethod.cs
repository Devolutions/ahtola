using Ahtola.Core.Indexing;

namespace Ahtola.Core.Vectors;

/// <summary>
/// Exact sparse Jaccard attachment. Its validated envelope is durable; deterministic component
/// postings are derived from the snapshot-isolated base table.
/// </summary>
internal sealed class ManagedSparseVectorIndexAttachment : ManagedIndexMethodAttachment
{
    private readonly ManagedIndexMethodDefinition _definition;
    private readonly HashSet<long> _unindexableCensus = [];
    private ManagedSparseVectorIndex _index;
    private long _appliedRevision = -1;
    private long _censusRevision = -1;
    private double _observedFraction;
    private long _observedQueries;

    public ManagedSparseVectorIndexAttachment(
        ManagedIndexMethodConfiguration configuration,
        ManagedVectorIndexOptions options)
    {
        Configuration = configuration;
        Options = options;
        _definition = new ManagedIndexMethodDefinition(
            "vector",
            configuration.IndexName,
            [
                new ManagedIndexQueryPattern(ManagedIndexPatternShape.KnnLimit, 2),
                new ManagedIndexQueryPattern(ManagedIndexPatternShape.Knn, 1),
            ],
            backingBtree: true,
            resultsMaterialized: true,
            mvccSupport: ManagedIndexMethodMvccSupport.TransactionalBackingStore,
            storageVersion: ManagedVectorIndexMethod.StateVersion);
        _index = new ManagedSparseVectorIndex(options.Dimensions);
        Planner = new ManagedVectorPlannerAdapter(options);
    }

    public ManagedVectorIndexOptions Options { get; }
    public override ManagedIndexMethodDefinition Definition => _definition;
    public override ManagedIndexMethodConfiguration Configuration { get; }
    public override IManagedIndexMethodPlannerAdapter Planner { get; }
    internal ManagedSparseVectorIndex Index => _index;
    internal long AppliedRevision => _appliedRevision;
    internal bool HasBeenBuilt => _appliedRevision >= 0;
    internal double? ObservedRerankFraction => _observedQueries == 0 ? null : _observedFraction;

    public override ManagedIndexMethodCursor Open(IManagedIndexSource source)
        => new ManagedSparseVectorIndexCursor(this, source);

    public override byte[] SaveState()
        => ManagedSparseVectorIndexState.Encode(Options);

    public override void LoadState(int version, ReadOnlySpan<byte> bytes)
    {
        if (version == 0 && bytes.IsEmpty)
            return;
        if (version != ManagedVectorIndexMethod.StateVersion)
            throw new EmbeddedSqlException($"unsupported managed sparse vector index state version {version}");
        ManagedSparseVectorIndexState.Validate(bytes, Options);
        ResetIndex();
    }

    public override ManagedIndexMethodAttachment Fork()
        => new ManagedSparseVectorIndexAttachment(Configuration, Options);

    internal void RecordSearch(int rerankedRows, int liveRows)
    {
        var fraction = liveRows <= 0 ? 0.0 : Math.Clamp((double)rerankedRows / liveRows, 0.0, 1.0);
        _observedQueries++;
        _observedFraction += (fraction - _observedFraction) / _observedQueries;
    }

    internal ManagedSparseVectorIndex CreateDetachedIndex()
        => new(Options.Dimensions);

    internal void PublishIndex(ManagedSparseVectorIndex index, long revision)
    {
        _index = index;
        _appliedRevision = revision;
    }

    internal void ResetIndex()
    {
        _index = new ManagedSparseVectorIndex(Options.Dimensions);
        _appliedRevision = -1;
        _censusRevision = -1;
        _unindexableCensus.Clear();
        _observedFraction = 0.0;
        _observedQueries = 0;
    }

    internal void MarkApplied(long revision) => _appliedRevision = revision;

    internal int ReconcileUnindexableCensus(IManagedIndexSource source)
    {
        if (_censusRevision == source.Revision)
            return _unindexableCensus.Count;
        if (_censusRevision >= 0 && source.TryGetDelta(_censusRevision) is { } delta)
        {
            foreach (var rowId in delta.ChangedRowIds)
                ClassifyCensusRow(source, rowId);
            _censusRevision = delta.Revision;
            if (_censusRevision == source.Revision)
                return _unindexableCensus.Count;
        }

        _unindexableCensus.Clear();
        for (var position = 0; position < source.RowCount; position++)
        {
            if (!IsIndexable(source.GetRow(position)))
                _unindexableCensus.Add(source.GetRowId(position));
        }
        _censusRevision = source.Revision;
        return _unindexableCensus.Count;
    }

    private void ClassifyCensusRow(IManagedIndexSource source, long rowId)
    {
        if (!source.TryGetPosition(rowId, out var position))
        {
            _unindexableCensus.Remove(rowId);
            return;
        }

        if (IsIndexable(source.GetRow(position)))
            _unindexableCensus.Remove(rowId);
        else
            _unindexableCensus.Add(rowId);
    }

    private bool IsIndexable(SqlValue[] row)
    {
        var columnIndex = Options.ColumnIndex;
        var value = columnIndex >= 0 && columnIndex < row.Length ? row[columnIndex] : SqlValue.Null;
        return SqliteVectorFunctions.TryDecodeSparseVector(value, Options.Dimensions, out var decoded)
            && decoded.IsFinite;
    }
}

/// <summary>One per-statement sparse vector cursor.</summary>
internal sealed class ManagedSparseVectorIndexCursor(
    ManagedSparseVectorIndexAttachment attachment,
    IManagedIndexSource source) : ManagedIndexMethodCursor
{
    private IReadOnlyList<ManagedVectorCandidate> _results = [];
    private int _position = -1;
    private bool _writable;
    private bool _disposed;

    public override void Create()
    {
        attachment.ResetIndex();
        Refresh(force: true);
    }

    public override void Destroy()
    {
        attachment.ResetIndex();
        ResetResults();
    }

    public override void OpenRead() => Refresh(force: false);

    public override void OpenWrite()
    {
        _writable = true;
        Refresh(force: false);
    }

    public override void Insert(ReadOnlySpan<SqlValue> values)
    {
        RequireWritable();
        var (rowId, value) = SplitValues(values);
        attachment.Index.Upsert(rowId, value);
        attachment.MarkApplied(source.Revision);
    }

    public override void Delete(ReadOnlySpan<SqlValue> values)
    {
        RequireWritable();
        var (rowId, _) = SplitValues(values);
        attachment.Index.Remove(rowId);
        attachment.MarkApplied(source.Revision);
    }

    public override bool QueryStart(int patternIndex, ReadOnlySpan<SqlValue> arguments)
    {
        if (patternIndex < 0 || patternIndex >= attachment.Definition.Patterns.Count)
            throw new EmbeddedSqlException($"index method 'vector' has no query pattern {patternIndex}");
        if (arguments.Length == 0)
            throw new EmbeddedSqlException("index method 'vector' requires a query vector");

        Refresh(force: false);
        var options = attachment.Options;
        if (source.RowCount > 0)
            SqliteVectorFunctions.ValidateVectorQueryArgument(arguments[0], options.Encoding, options.Dimensions);
        if (!SqliteVectorFunctions.TryDecodeSparseVector(arguments[0], options.Dimensions, out var query))
            throw new EmbeddedSqlException("Invalid float32_sparse vector");

        var limit = arguments.Length > 1 && arguments[1].Kind == SqlValueKind.Integer
            ? arguments[1].AsInteger()
            : -1;
        var bounded = limit < 0 || limit > int.MaxValue ? int.MaxValue : (int)limit;
        var result = attachment.Index.Search(arguments[0], query, bounded, source, options.ColumnIndex);
        attachment.RecordSearch(result.RerankedRows, source.RowCount);
        _results = result.Rows;
        _position = _results.Count == 0 ? -1 : 0;
        return _position >= 0;
    }

    public override bool QueryNext()
    {
        if (_position < 0)
            return false;
        if (++_position < _results.Count)
            return true;
        _position = -1;
        return false;
    }

    public override SqlValue Column(int index)
    {
        if (_position < 0 || _position >= _results.Count || index != 0)
            return SqlValue.Null;
        var distance = _results[_position].Distance;
        return double.IsNaN(distance) ? SqlValue.Null : SqlValue.Real(distance);
    }

    public override long? RowId()
        => _position >= 0 && _position < _results.Count ? _results[_position].RowId : null;

    public override void Optimize()
    {
        Refresh(force: false);
        PublishRebuild(BuildDetached());
    }

    public override void Rebuild()
        => PublishRebuild(BuildDetached());

    public override ManagedIndexMethodCostEstimate? EstimateCost(in ManagedIndexMethodCostContext context)
    {
        var options = attachment.Options;
        var baseRows = Math.Max(context.BaseTableRows, 0);
        if (baseRows <= 0 || baseRows < options.MinimumRows)
            return null;
        if (attachment.ReconcileUnindexableCensus(source) > 0)
            return null;

        var shape = attachment.Definition.Patterns[context.PatternIndex].Shape;
        var refreshCost = EstimateRefreshCost(baseRows);
        if (context.RetainsUnrankedRows
            || !ManagedIndexPatternShapes.HasLimit(shape)
            || context.Limit is not { } limit)
        {
            return new ManagedIndexMethodCostEstimate(
                Math.Max(baseRows, 1) + refreshCost,
                Math.Max(baseRows, 1),
                Describe(baseRows, baseRows),
                refreshCost);
        }

        var fraction = attachment.ObservedRerankFraction ?? 0.25;
        var reranked = Math.Clamp(Math.Ceiling(baseRows * fraction), 1.0, Math.Max(baseRows, 1));
        var rows = Math.Max(Math.Min(limit, baseRows), 1);
        return new ManagedIndexMethodCostEstimate(
            reranked + rows + refreshCost,
            rows,
            Describe(reranked, baseRows),
            refreshCost);
    }

    private double EstimateRefreshCost(long baseRows)
    {
        if (source.Revision == attachment.AppliedRevision && attachment.HasBeenBuilt)
            return 0.0;
        if (attachment.HasBeenBuilt && source.TryGetDelta(attachment.AppliedRevision) is { } delta)
            return delta.ChangedRowIds.Count;
        return Math.Max(baseRows, 1);
    }

    private static string Describe(double reranked, long baseRows)
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"metric=jaccard encoding=float32_sparse postings=exact scans~{(long)reranked}/{Math.Max(baseRows, 1)} exact=1");

    private void Refresh(bool force)
    {
        if (!force && source.Revision == attachment.AppliedRevision && attachment.HasBeenBuilt)
            return;
        if (!force
            && attachment.HasBeenBuilt
            && source.TryGetDelta(attachment.AppliedRevision) is { } delta)
        {
            ApplyDelta(delta);
            return;
        }
        PublishRebuild(BuildDetached());
    }

    private ManagedSparseVectorIndex BuildDetached()
    {
        var index = attachment.CreateDetachedIndex();
        var columnIndex = attachment.Options.ColumnIndex;
        var rows = new List<(long RowId, int Position)>(source.RowCount);
        for (var position = 0; position < source.RowCount; position++)
            rows.Add((source.GetRowId(position), position));
        rows.Sort(static (left, right) => left.RowId.CompareTo(right.RowId));
        foreach (var (_, position) in rows)
        {
            var row = source.GetRow(position);
            index.Upsert(
                source.GetRowId(position),
                columnIndex >= 0 && columnIndex < row.Length ? row[columnIndex] : SqlValue.Null);
        }
        return index;
    }

    private void ApplyDelta(ManagedIndexSourceDelta delta)
    {
        var columnIndex = attachment.Options.ColumnIndex;
        foreach (var rowId in delta.ChangedRowIds)
        {
            if (source.TryGetPosition(rowId, out var position))
            {
                var row = source.GetRow(position);
                attachment.Index.Upsert(
                    rowId,
                    columnIndex >= 0 && columnIndex < row.Length ? row[columnIndex] : SqlValue.Null);
            }
            else
            {
                attachment.Index.Remove(rowId);
            }
        }
        attachment.MarkApplied(delta.Revision);
    }

    private void PublishRebuild(ManagedSparseVectorIndex index)
    {
        ManagedIndexMethodDiagnostics.RecordStateRebuild();
        var revision = source.Revision;
        attachment.PublishIndex(index, revision);
        source.NotifyRebuilt(revision);
    }

    private void RequireWritable()
    {
        if (!_writable)
            throw new EmbeddedSqlException("index method 'vector' cursor is not open for writing");
    }

    private static (long RowId, SqlValue Value) SplitValues(ReadOnlySpan<SqlValue> values)
    {
        if (values.Length != 2)
        {
            throw new EmbeddedSqlException(
                $"index method 'vector' expects 2 maintenance values but received {values.Length}");
        }
        if (values[^1].Kind != SqlValueKind.Integer)
            throw new EmbeddedSqlException("index method 'vector' requires an integer rowid");
        return (values[^1].AsInteger(), values[0]);
    }

    private void ResetResults()
    {
        _results = [];
        _position = -1;
    }

    public override void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ResetResults();
        _writable = false;
    }
}
