using System.Globalization;
using System.Text;
using Ahtola.Core.Indexing;

namespace Ahtola.Core.Vectors;

/// <summary>Bounds that keep vector index training, state and search finite.</summary>
internal static class ManagedVectorIndexLimits
{
    /// <summary>Largest indexable dimensionality. The scalar functions keep their own 1 048 576 cap.</summary>
    public const int MaxDimensions = 2048;

    /// <summary>Largest number of inverted lists.</summary>
    public const int MaxLists = 4096;

    /// <summary>Largest training sample.</summary>
    public const int MaxTrainSample = 65536;

    /// <summary>Smallest training sample.</summary>
    public const int MinTrainSample = 256;

    /// <summary>Largest number of Lloyd iterations.</summary>
    public const int MaxIterations = 16;

    /// <summary>Largest persisted centroid payload, checked before any allocation.</summary>
    public const int MaxStateBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Largest number of reranked candidates one search will hold. Beyond this the search has
    /// already read most of the table, so it switches to returning every row rather than truncating.
    /// </summary>
    public const int MaxCandidateRows = 1_000_000;

    /// <summary>
    /// How much wider than the configured probe count the cost model assumes the certificate will
    /// need before any measurement exists. Observed probe counts replace it as soon as one query has
    /// run, so an adversarial data set re-prices itself out of the plan instead of staying cheap.
    /// </summary>
    public const double ColdCertificateFactor = 2.0;
}

/// <summary>Validated <c>WITH (...)</c> options for one vector method index.</summary>
/// <remarks>
/// Every key is validated <em>and</em> consumed: nothing is accepted and then ignored. Keys whose
/// behaviour is not implemented (<c>metric = 'jaccard'</c>, <c>encoding = 'float32_sparse'</c>,
/// <c>exact = 0</c>) are rejected with a message that says so, rather than silently downgraded.
/// </remarks>
internal sealed class ManagedVectorIndexOptions
{
    private static readonly string[] KnownKeys =
    [
        "metric", "dims", "encoding", "lists", "probes", "seed", "iters", "train_sample", "exact", "min_rows",
    ];

    private ManagedVectorIndexOptions(
        string indexName,
        string columnName,
        int columnIndex,
        VectorDistanceKind metric,
        VectorEncodingKind encoding,
        int dimensions,
        int lists,
        int probes,
        long seed,
        int iterations,
        int trainSample,
        bool exact,
        long minimumRows)
    {
        IndexName = indexName;
        ColumnName = columnName;
        ColumnIndex = columnIndex;
        Metric = metric;
        Encoding = encoding;
        Dimensions = dimensions;
        Lists = lists;
        Probes = probes;
        Seed = seed;
        Iterations = iterations;
        TrainSample = trainSample;
        Exact = exact;
        MinimumRows = minimumRows;
    }

    public string IndexName { get; }

    public string ColumnName { get; }

    /// <summary>The base-table column ordinal the index covers.</summary>
    public int ColumnIndex { get; }

    public VectorDistanceKind Metric { get; }

    public VectorEncodingKind Encoding { get; }

    public int Dimensions { get; }

    public int Lists { get; }

    /// <summary>Where the certificate loop starts probing; never a correctness knob.</summary>
    public int Probes { get; }

    public long Seed { get; }

    public int Iterations { get; }

    public int TrainSample { get; }

    /// <summary>Always true in this build; <c>exact = 0</c> is rejected rather than silently accepted.</summary>
    public bool Exact { get; }

    /// <summary>Below this live row count the index declines and the ordinary scan wins.</summary>
    public long MinimumRows { get; }

    /// <summary>The SQL function this index is bound to, used for planner matching.</summary>
    public string DistanceFunctionName => ManagedVectorPlannerAdapter.FunctionFor(Metric);

    /// <summary>
    /// The clustering-space metric. <c>float1bit</c> columns always cluster their raw 0/1 components
    /// so the derived radius is an exact Hamming count, whatever distance the query reports.
    /// </summary>
    public VectorDistanceKind ClusteringMetric
        => Encoding == VectorEncodingKind.Float1Bit ? VectorDistanceKind.L2 : Metric;

    /// <summary>Projects a decoded vector into the clustering space.</summary>
    public bool TryProject(ReadOnlySpan<double> values, out double[] projected)
        => ManagedVectorGeometry.TryProject(values, ClusteringMetric, out projected);

    /// <summary>A stable text fingerprint of everything that affects trained centroids.</summary>
    public string TrainingFingerprint => string.Create(
        CultureInfo.InvariantCulture,
        $"{IndexName}|{Metric}|{Encoding}|{Dimensions}|{Lists}|{Iterations}|{TrainSample}");

    public static ManagedVectorIndexOptions Resolve(ManagedIndexMethodConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Columns.Count != 1)
            throw new EmbeddedSqlException($"index '{configuration.IndexName}' must name exactly one vector column");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in configuration.Parameters)
        {
            if (!KnownKeys.Contains(parameter.Name, StringComparer.OrdinalIgnoreCase))
                throw new EmbeddedSqlException($"unknown vector index parameter: {parameter.Name}");
            if (!seen.Add(parameter.Name))
                throw new EmbeddedSqlException($"duplicate vector index parameter: {parameter.Name}");
        }

        var metric = ParseMetric(configuration.TryGetParameter("metric", out var metricValue)
            ? RequireText("metric", metricValue)
            : "l2");
        var encoding = ParseEncoding(configuration.TryGetParameter("encoding", out var encodingValue)
            ? RequireText("encoding", encodingValue)
            : "float32");

        // The scalar evaluator refuses L2 over float1bit vectors, so an index bound to that pair
        // could only ever serve a query that errors on its first row.
        if (encoding == VectorEncodingKind.Float1Bit && metric == VectorDistanceKind.L2)
            throw new EmbeddedSqlException("L2 distance is not supported for float1bit vectors");

        if (!configuration.TryGetParameter("dims", out _))
        {
            throw new EmbeddedSqlException(
                $"index '{configuration.IndexName}' requires the vector index parameter 'dims'");
        }

        var dimensions = ReadInteger(configuration, "dims", 0);
        if (dimensions is < 1 or > ManagedVectorIndexLimits.MaxDimensions)
        {
            throw new EmbeddedSqlException(
                $"vector index parameter 'dims' must be between 1 and {ManagedVectorIndexLimits.MaxDimensions}");
        }

        var lists = ReadInteger(configuration, "lists", 64);
        if (lists is < 1 or > ManagedVectorIndexLimits.MaxLists)
        {
            throw new EmbeddedSqlException(
                $"vector index parameter 'lists' must be between 1 and {ManagedVectorIndexLimits.MaxLists}");
        }

        var defaultProbes = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(lists)));
        var probes = ReadInteger(configuration, "probes", defaultProbes);
        if (probes < 1)
            throw new EmbeddedSqlException("vector index parameter 'probes' must be at least 1");
        if (probes > lists)
            throw new EmbeddedSqlException("vector index parameter 'probes' must not exceed 'lists'");

        var iterations = ReadInteger(configuration, "iters", 10);
        if (iterations is < 1 or > ManagedVectorIndexLimits.MaxIterations)
        {
            throw new EmbeddedSqlException(
                $"vector index parameter 'iters' must be between 1 and {ManagedVectorIndexLimits.MaxIterations}");
        }

        var trainSample = ReadInteger(configuration, "train_sample", 32768);
        if (trainSample < ManagedVectorIndexLimits.MinTrainSample
            || trainSample > ManagedVectorIndexLimits.MaxTrainSample)
        {
            throw new EmbeddedSqlException(
                $"vector index parameter 'train_sample' must be between {ManagedVectorIndexLimits.MinTrainSample} and {ManagedVectorIndexLimits.MaxTrainSample}");
        }

        var seed = ReadInteger(configuration, "seed", 0);
        var exact = ReadInteger(configuration, "exact", 1);
        if (exact != 1)
        {
            // Approximate mode would let a pushed-down LIMIT return a different row set than the
            // same query without the index. It is not shipped, so it is rejected rather than
            // accepted and quietly treated as exact.
            throw new EmbeddedSqlException(
                "vector index parameter 'exact' must be 1; approximate mode is not implemented");
        }

        var minimumRows = ReadInteger(configuration, "min_rows", 512);
        if (minimumRows < 0)
            throw new EmbeddedSqlException("vector index parameter 'min_rows' must not be negative");

        var stateBytes = checked((long)lists * dimensions * sizeof(float));
        if (stateBytes > ManagedVectorIndexLimits.MaxStateBytes)
        {
            throw new EmbeddedSqlException(
                $"vector index state would exceed {ManagedVectorIndexLimits.MaxStateBytes} bytes; reduce 'lists' or 'dims'");
        }

        return new ManagedVectorIndexOptions(
            configuration.IndexName,
            configuration.Columns[0].Name,
            configuration.Columns[0].ColumnIndex,
            metric,
            encoding,
            (int)dimensions,
            (int)lists,
            (int)probes,
            seed,
            (int)iterations,
            (int)trainSample,
            exact: true,
            minimumRows);
    }

    /// <summary>The canonical <c>WITH (metric = …)</c> spelling of one metric.</summary>
    public static string MetricName(VectorDistanceKind metric)
        => metric switch
        {
            VectorDistanceKind.L2 => "l2",
            VectorDistanceKind.Cosine => "cosine",
            VectorDistanceKind.Dot => "dot",
            VectorDistanceKind.Jaccard => "jaccard",
            _ => throw new InvalidOperationException($"Unknown vector distance kind {metric}."),
        };

    /// <summary>The canonical <c>WITH (encoding = …)</c> spelling of one encoding.</summary>
    public static string EncodingName(VectorEncodingKind encoding)
        => encoding switch
        {
            VectorEncodingKind.Float32 => "float32",
            VectorEncodingKind.Float64 => "float64",
            VectorEncodingKind.Float8 => "float8",
            VectorEncodingKind.Float1Bit => "float1bit",
            VectorEncodingKind.Float32Sparse => "float32_sparse",
            _ => throw new InvalidOperationException($"Unknown vector encoding {encoding}."),
        };

    private static VectorDistanceKind ParseMetric(string text)
        => text.ToLowerInvariant() switch
        {
            "l2" => VectorDistanceKind.L2,
            "cosine" or "cos" => VectorDistanceKind.Cosine,
            "dot" => VectorDistanceKind.Dot,
            "jaccard" => throw new EmbeddedSqlException(
                "vector index metric 'jaccard' requires a sparse index and is not implemented"),
            _ => throw new EmbeddedSqlException($"unknown vector index metric: {text}"),
        };

    private static VectorEncodingKind ParseEncoding(string text)
        => text.ToLowerInvariant() switch
        {
            "float32" or "f32" => VectorEncodingKind.Float32,
            "float64" or "f64" => VectorEncodingKind.Float64,
            "float8" or "f8" => VectorEncodingKind.Float8,
            "float1bit" or "f1bit" => VectorEncodingKind.Float1Bit,
            "float32_sparse" => throw new EmbeddedSqlException(
                "vector index encoding 'float32_sparse' requires a sparse index and is not implemented"),
            _ => throw new EmbeddedSqlException($"unknown vector index encoding: {text}"),
        };

    private static string RequireText(string key, SqlValue value)
        => value.Kind == SqlValueKind.Text
            ? value.AsText()
            : throw new EmbeddedSqlException($"vector index parameter '{key}' requires a text literal");

    private static long ReadInteger(ManagedIndexMethodConfiguration configuration, string key, long fallback)
    {
        if (!configuration.TryGetParameter(key, out var value))
            return fallback;

        return value.Kind switch
        {
            SqlValueKind.Integer => value.AsInteger(),
            _ => throw new EmbeddedSqlException($"vector index parameter '{key}' requires an integer literal"),
        };
    }

    /// <summary>Rebuilds the canonical <c>WITH</c> text so the catalog round-trip is lossless.</summary>
    public static string? FormatParameters(IReadOnlyList<ManagedIndexMethodParameter> parameters)
        => ManagedIndexMethodParameterFormatter.Format(parameters);
}

/// <summary>
/// The pure-managed approximate-nearest-neighbour index method registered as <c>USING vector</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is <em>not</em> a port of Turso's <c>toy_vector_sparse_ivf</c>
/// (turso-src/core/index_method/toy_vector_sparse_ivf.rs), which despite its name is a jaccard-only
/// sparse component inverted index pruned by three unbounded heuristics. What is carried over is the
/// method/attachment/cursor shape from <c>index_method/mod.rs</c>, the materialized-result and
/// transactional-backing-store declarations, and the <c>… ORDER BY distance LIMIT ?</c> query shape
/// in both argument orders. The structure, the training, the state envelope, the cost model and the
/// exactness certificate are Ahtola's. See docs/managed-vector-index.md.
/// </para>
/// <para>
/// The index is IVF-Flat with an exactness certificate: it prunes a list only when a proven
/// inequality says no member of it can enter the top-k, and otherwise probes more lists, degrading
/// to a full scan rather than to a wrong answer.
/// </para>
/// </remarks>
internal sealed class ManagedVectorIndexMethod : ManagedIndexMethod
{
    /// <summary>The persisted state version. A newer value in a file fails closed.</summary>
    public const int StateVersion = 1;

    public static ManagedVectorIndexMethod Instance { get; } = new();

    private ManagedVectorIndexMethod()
    {
    }

    public override string Name => "vector";

    public override ManagedIndexMethodAttachment Attach(ManagedIndexMethodConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new ManagedVectorIndexAttachment(configuration, ManagedVectorIndexOptions.Resolve(configuration));
    }
}

/// <summary>One attached vector index: immutable options, durable centroids, derived postings.</summary>
internal sealed class ManagedVectorIndexAttachment : ManagedIndexMethodAttachment
{
    private readonly ManagedIndexMethodDefinition _definition;
    private readonly HashSet<long> _unindexableCensus = [];
    private ManagedVectorIvfIndex _index;
    private long _appliedRevision = -1;
    private long _censusRevision = -1;
    private double _observedProbes;
    private double _observedFraction;
    private long _observedQueries;

    public ManagedVectorIndexAttachment(
        ManagedIndexMethodConfiguration configuration,
        ManagedVectorIndexOptions options,
        float[]? centroids = null,
        int trainedSampleRows = 0,
        long trainedPopulation = 0)
    {
        Configuration = configuration;
        Options = options;
        _definition = new ManagedIndexMethodDefinition(
            "vector",
            configuration.IndexName,
            [
                // Most specific first: only the limited shape can beat a scan, because a ranking
                // pattern without a limit still has to produce every base row.
                new ManagedIndexQueryPattern(ManagedIndexPatternShape.KnnLimit, 2),
                new ManagedIndexQueryPattern(ManagedIndexPatternShape.Knn, 1),
            ],
            backingBtree: true,
            resultsMaterialized: true,
            // Centroids live in the catalog row and postings are derived from the base rows, both of
            // which the engine already keeps snapshot isolated.
            mvccSupport: ManagedIndexMethodMvccSupport.TransactionalBackingStore,
            storageVersion: ManagedVectorIndexMethod.StateVersion);
        _index = new ManagedVectorIvfIndex(options);
        if (centroids is not null)
            _index.PublishCentroids(centroids, trainedSampleRows, trainedPopulation);

        Planner = new ManagedVectorPlannerAdapter(options);
    }

    public ManagedVectorIndexOptions Options { get; }

    public override ManagedIndexMethodDefinition Definition => _definition;

    public override ManagedIndexMethodConfiguration Configuration { get; }

    public override IManagedIndexMethodPlannerAdapter Planner { get; }

    /// <summary>The live structure. Only cursors mutate it.</summary>
    internal ManagedVectorIvfIndex Index => _index;

    internal long AppliedRevision => _appliedRevision;

    internal bool HasBeenBuilt => _appliedRevision >= 0;

    /// <summary>
    /// The mean fraction of the live table a query has actually had to read and score, or null
    /// before any query ran.
    /// </summary>
    /// <remarks>
    /// This is what keeps the cost model honest. A probe count alone says nothing — one list can
    /// hold most of the table — so the measurement is the reranked row count, normalized by the live
    /// row count so it survives the table growing. When the certificate starts needing everything,
    /// the fraction rises to one and the plan prices itself out instead of advertising pruning it is
    /// not achieving.
    /// </remarks>
    internal double? ObservedRerankFraction => _observedQueries > 0 ? _observedFraction : null;

    /// <summary>The mean number of lists a query has needed, or null before any query ran.</summary>
    internal double? ObservedProbes => _observedQueries > 0 ? _observedProbes : null;

    internal void RecordSearch(int probes, int rerankedRows, int liveRows)
    {
        var boundedProbes = Math.Min(Math.Max(probes, 0), Options.Lists);
        var fraction = liveRows <= 0 ? 0.0 : Math.Clamp((double)rerankedRows / liveRows, 0.0, 1.0);
        _observedQueries++;
        _observedProbes += (boundedProbes - _observedProbes) / _observedQueries;
        _observedFraction += (fraction - _observedFraction) / _observedQueries;
    }

    public override ManagedIndexMethodCursor Open(IManagedIndexSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new ManagedVectorIndexCursor(this, source);
    }

    public override byte[] SaveState()
        => ManagedVectorIndexState.Encode(
            Options,
            _index.Centroids,
            _index.TrainedSampleRows,
            _index.TrainedPopulation);

    public override void LoadState(int version, ReadOnlySpan<byte> bytes)
    {
        // No envelope at all: centroids are an acceleration cache, so a catalog row that never
        // carried one simply trains on first use. That is distinct from an envelope that exists but
        // is empty, which is a truncated write and fails closed.
        if (version == 0 && bytes.Length == 0)
            return;

        if (bytes.Length > ManagedVectorIndexLimits.MaxStateBytes + ManagedVectorIndexState.HeaderSize)
        {
            throw new EmbeddedSqlException(
                $"vector index state would exceed {ManagedVectorIndexLimits.MaxStateBytes} bytes; reduce 'lists' or 'dims'");
        }

        var (centroids, trainedSampleRows, trainedPopulation) = ManagedVectorIndexState.Decode(
            Configuration.IndexName,
            Options,
            version,
            bytes);
        _index = new ManagedVectorIvfIndex(Options);
        _index.PublishCentroids(centroids, trainedSampleRows, trainedPopulation);
        _appliedRevision = -1;
    }

    public override ManagedIndexMethodAttachment Fork()
    {
        // Postings are derived and start empty, so a rolled-back statement leaves nothing behind.
        // Trained centroids travel with the fork: they are the snapshot's own training output, and
        // re-deriving them would make a rollback silently re-cluster the index.
        return new ManagedVectorIndexAttachment(
            Configuration,
            Options,
            _index.IsTrained ? (float[])_index.Centroids.Clone() : null,
            _index.TrainedSampleRows,
            _index.TrainedPopulation);
    }

    /// <summary>
    /// A structure that shares nothing with the published one.
    /// </summary>
    /// <remarks>
    /// Carrying the centroids also carries the population they were trained over, so a rebuild that
    /// reuses them does not look like a freshly trained index and cannot fake away the drift check.
    /// </remarks>
    internal ManagedVectorIvfIndex CreateDetachedIndex(bool carryCentroids)
    {
        var detached = new ManagedVectorIvfIndex(Options);
        if (carryCentroids && _index.IsTrained)
        {
            detached.PublishCentroids(
                (float[])_index.Centroids.Clone(),
                _index.TrainedSampleRows,
                _index.TrainedPopulation);
        }

        return detached;
    }

    /// <summary>Publishes a rebuilt structure atomically once the build has succeeded.</summary>
    internal void PublishIndex(ManagedVectorIvfIndex index, long revision)
    {
        ArgumentNullException.ThrowIfNull(index);
        _index = index;
        _appliedRevision = revision;
    }

    /// <summary>Discards all state, forcing a retrain and rebuild on next use.</summary>
    internal void ResetIndex()
    {
        _index = new ManagedVectorIvfIndex(Options);
        _appliedRevision = -1;
        _censusRevision = -1;
        _unindexableCensus.Clear();
        _observedProbes = 0.0;
        _observedFraction = 0.0;
        _observedQueries = 0;
    }

    internal void MarkApplied(long revision) => _appliedRevision = revision;

    /// <summary>
    /// Brings the unindexable-row census up to date and returns it, without training centroids,
    /// placing postings or publishing anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the planner's correctness gate. A live row whose indexed column is not a valid vector
    /// of the declared encoding and dimensionality makes the scalar form of a KNN query raise the
    /// moment the scan reaches it, so an index that quietly skipped such rows would turn an error
    /// into a result set. The gate therefore has to be answered before an access path is chosen.
    /// </para>
    /// <para>
    /// Answering it from the index's own placement state would mean reconciling — and reconciling a
    /// cold index means k-means over a sample and a full re-placement, which is precisely the work
    /// EXPLAIN QUERY PLAN must not trigger. Classification is a strictly smaller job: decode one
    /// column value per changed row and decide whether it is the declared shape. It rides the same
    /// mutation journal the index does, so the steady-state cost is the rows that changed since the
    /// last statement, and the full walk only runs when the journal cannot prove otherwise.
    /// </para>
    /// </remarks>
    internal int ReconcileUnindexableCensus(IManagedIndexSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
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
        return SqliteVectorFunctions.TryDecodeVector(value, Options.Encoding, Options.Dimensions, out var decoded)
            && decoded.IsFinite;
    }
}

/// <summary>One per-statement vector cursor: single threaded, disposed at statement finalize.</summary>
internal sealed class ManagedVectorIndexCursor(ManagedVectorIndexAttachment attachment, IManagedIndexSource source)
    : ManagedIndexMethodCursor
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
        _results = [];
        _position = -1;
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
        var (rowId, columnValue) = SplitValues(values);
        attachment.Index.Upsert(rowId, columnValue);
        SyncApplied();
        CompactIfNeeded();
    }

    public override void Delete(ReadOnlySpan<SqlValue> values)
    {
        RequireWritable();
        var (rowId, _) = SplitValues(values);
        attachment.Index.Remove(rowId);
        SyncApplied();
        CompactIfNeeded();
    }

    public override bool QueryStart(int patternIndex, ReadOnlySpan<SqlValue> arguments)
    {
        if (patternIndex < 0 || patternIndex >= attachment.Definition.Patterns.Count)
            throw new EmbeddedSqlException($"index method 'vector' has no query pattern {patternIndex}");
        if (arguments.Length == 0)
            throw new EmbeddedSqlException("index method 'vector' requires a query vector");

        Refresh(force: false);
        var options = attachment.Options;
        var queryValue = arguments[0];

        // Raise exactly what the scalar call would raise for this operand pair before any index work
        // happens. The planner only offers this path when every live row already decodes to the
        // declared shape, so the column operand cannot be the one that fails.
        if (source.RowCount > 0)
            SqliteVectorFunctions.ValidateVectorQueryArgument(queryValue, options.Encoding, options.Dimensions);

        if (!SqliteVectorFunctions.TryDecodeVector(queryValue, out var query))
            throw new EmbeddedSqlException("Invalid vector type");

        var limit = arguments.Length > 1 && arguments[1].Kind == SqlValueKind.Integer
            ? arguments[1].AsInteger()
            : -1;
        var bounded = limit < 0 || limit > int.MaxValue ? int.MaxValue : (int)limit;

        var result = attachment.Index.Search(
            queryValue,
            query,
            bounded,
            source,
            options.ColumnIndex,
            options.Probes,
            null);
        attachment.RecordSearch(
            result.ProbedLists == int.MaxValue ? options.Lists : result.ProbedLists,
            result.RerankedRows,
            source.RowCount);
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
        // Compaction and exact radius recomputation only. This never trains centroids itself: the
        // refresh it performs first follows the ordinary drift rule, and the rebuilt structure keeps
        // the same centroids, so every bound it derives can only shrink — and shrinking an upper
        // bound is always safe.
        Refresh(force: false);
        var optimized = attachment.CreateDetachedIndex(carryCentroids: true);
        BuildInto(optimized);
        PublishRebuild(optimized);
    }

    public override void Rebuild()
    {
        // REINDEX: retrain centroids and rebuild postings on a detached structure, publishing only
        // once the whole build succeeded, so a throw leaves the previous index live and queryable.
        var rebuilt = attachment.CreateDetachedIndex(carryCentroids: false);
        Train(rebuilt);
        BuildInto(rebuilt);
        PublishRebuild(rebuilt);
    }

    public override ManagedIndexMethodCostEstimate? EstimateCost(in ManagedIndexMethodCostContext context)
    {
        var options = attachment.Options;
        var baseRows = Math.Max(context.BaseTableRows, 0);
        if (baseRows <= 0 || baseRows < options.MinimumRows)
            return null;

        // A row whose indexed column is not a valid vector of the declared shape makes the scalar
        // form of this query raise an error. Declining hands the statement to the ordinary scan,
        // which raises it in exactly the right order instead of returning rows the scan never would.
        //
        // The census is deliberately not the index's own placement state: pricing runs before any
        // reconciliation, so asking the index would mean rebuilding it to find out. Classification
        // is a decode of the indexed column and nothing else — no centroids, no postings, no
        // publication — and it rides the same mutation journal, so the usual cost is the rows that
        // changed since it last ran.
        if (attachment.ReconcileUnindexableCensus(source) > 0)
            return null;

        var shape = attachment.Definition.Patterns[context.PatternIndex].Shape;
        var refreshCost = EstimateRefreshCost(baseRows);
        if (context.RetainsUnrankedRows
            || !ManagedIndexPatternShapes.HasLimit(shape)
            || context.Limit is not { } limit)
        {
            // The engine will still emit every base row this plan does not rank, so the plan is
            // priced at what it actually produces. Ranking removes nothing, which is exactly why it
            // can never be cheaper than the scan it would replace.
            var allRows = Math.Max(baseRows, 1);
            return new ManagedIndexMethodCostEstimate(
                options.Lists + (allRows * 2.0) + refreshCost,
                allRows,
                Describe(allRows, baseRows),
                refreshCost);
        }

        // Worst case for this data set, not a hoped-for case. The cold estimate assumes the
        // certificate needs twice the configured probes; the measured mean of what queries actually
        // read replaces it whenever it is larger.
        //
        // "Trained" here means trained by the time the plan runs, not trained right now: the
        // reconciliation this estimate already charges for is the thing that trains it. Pricing an
        // unreconciled index as a permanent full read would make it lose every comparison, never be
        // selected, and therefore never be reconciled.
        var coldProbes = Math.Min(options.Lists, options.Probes * ManagedVectorIndexLimits.ColdCertificateFactor);
        var coldRows = Math.Ceiling(baseRows * coldProbes / options.Lists) + attachment.Index.UnboundedRowCount;
        var measuredRows = attachment.ObservedRerankFraction is { } fraction
            ? Math.Ceiling(baseRows * fraction)
            : 0.0;
        var probeRows = WillBeTrained(baseRows)
            ? Math.Min(Math.Max(coldRows, measuredRows), Math.Max(baseRows, 1))
            : Math.Max(baseRows, 1);
        var rows = Math.Max(Math.Min(limit, baseRows), 1);
        var cost = options.Lists + (probeRows * 2.0) + refreshCost + rows;
        return new ManagedIndexMethodCostEstimate(cost, rows, Describe(probeRows, baseRows), refreshCost);
    }

    /// <summary>True when the structure this plan runs against will have usable centroids.</summary>
    /// <remarks>
    /// An index that is already trained stays trained; one that is not will be trained by the
    /// refresh this estimate charges for, provided there are live rows to train from.
    /// </remarks>
    private bool WillBeTrained(long baseRows)
        => attachment.Index.IsTrained || baseRows > 0;

    /// <summary>The method-owned plan description EXPLAIN QUERY PLAN appends.</summary>
    private string Describe(double probeRows, long baseRows)
    {
        var options = attachment.Options;
        var probes = attachment.ObservedProbes is { } measured
            ? (int)Math.Ceiling(measured)
            : (int)Math.Ceiling(Math.Min(options.Lists, options.Probes * ManagedVectorIndexLimits.ColdCertificateFactor));
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"metric={ManagedVectorIndexOptions.MetricName(options.Metric)} encoding={ManagedVectorIndexOptions.EncodingName(options.Encoding)} lists={options.Lists} probes={probes} scans~{(long)probeRows}/{Math.Max(baseRows, 1)} exact=1");
    }

    /// <summary>The reconciliation the next query is forced to perform, priced in row reads.</summary>
    private double EstimateRefreshCost(long baseRows)
    {
        var retrain = attachment.Index.NeedsRetrain(source.RowCount);
        if (!retrain && source.Revision == attachment.AppliedRevision && attachment.Index.IsTrained)
            return 0.0;

        if (!retrain
            && attachment.Index.IsTrained
            && attachment.HasBeenBuilt
            && source.TryGetDelta(attachment.AppliedRevision) is { } delta)
        {
            return delta.ChangedRowIds.Count;
        }

        // A cold structure walks every base row, and an untrained or drifted one additionally runs
        // k-means over its sample. Both are charged rather than hidden.
        var options = attachment.Options;
        var walk = (double)Math.Max(baseRows, 1);
        if (attachment.Index.IsTrained && !retrain)
            return walk;

        var sample = Math.Min(walk, options.TrainSample);
        return walk + (sample * options.Lists * options.Iterations);
    }

    private void Refresh(bool force)
    {
        var retrain = attachment.Index.NeedsRetrain(source.RowCount);
        if (!force && !retrain && source.Revision == attachment.AppliedRevision && attachment.Index.IsTrained)
            return;

        if (!force
            && !retrain
            && attachment.Index.IsTrained
            && attachment.HasBeenBuilt
            && source.TryGetDelta(attachment.AppliedRevision) is { } delta)
        {
            ApplyDelta(delta);
            return;
        }

        // Centroids are carried across an ordinary rebuild so a rolled-back statement does not
        // silently re-cluster; they are re-derived only when the index has never been trained or
        // when the live row count has drifted far enough that they no longer describe the data.
        var rebuilt = attachment.CreateDetachedIndex(carryCentroids: !retrain);
        if (!rebuilt.IsTrained)
            Train(rebuilt);

        BuildInto(rebuilt);
        PublishRebuild(rebuilt);
    }

    private void PublishRebuild(ManagedVectorIvfIndex rebuilt)
    {
        ManagedIndexMethodDiagnostics.RecordStateRebuild();
        var revision = source.Revision;
        attachment.PublishIndex(rebuilt, revision);
        source.NotifyRebuilt(revision);
    }

    private void ApplyDelta(ManagedIndexSourceDelta delta)
    {
        var index = attachment.Index;
        var columnIndex = attachment.Options.ColumnIndex;
        foreach (var rowId in delta.ChangedRowIds)
        {
            if (source.TryGetPosition(rowId, out var position))
            {
                var row = source.GetRow(position);
                index.Upsert(rowId, columnIndex >= 0 && columnIndex < row.Length ? row[columnIndex] : SqlValue.Null);
            }
            else
            {
                index.Remove(rowId);
            }
        }

        attachment.MarkApplied(delta.Revision);
        CompactIfNeeded();
    }

    /// <summary>Assigns every live base row into the structure's lists.</summary>
    private void BuildInto(ManagedVectorIvfIndex index)
    {
        var columnIndex = attachment.Options.ColumnIndex;
        index.ClearPlacements();
        foreach (var (rowId, position) in EnumerateRowIdOrder())
        {
            var row = source.GetRow(position);
            index.Upsert(rowId, columnIndex >= 0 && columnIndex < row.Length ? row[columnIndex] : SqlValue.Null);
        }
    }

    /// <summary>Trains centroids into a detached structure from a deterministic bounded sample.</summary>
    /// <remarks>
    /// Two counts come out of this and they are not interchangeable. The sample is capped by
    /// <c>train_sample</c> and is what k-means saw; the eligible population is how many live rows
    /// could have been sampled, and it is the number the drift rule compares against. The reservoir
    /// is fed during the scan, so only the capped sample is ever retained: a large table costs
    /// <c>O(train_sample × dims)</c> here, not <c>O(rows × dims)</c>.
    /// </remarks>
    private void Train(ManagedVectorIvfIndex index)
    {
        var options = attachment.Options;
        var columnIndex = options.ColumnIndex;
        var random = new ManagedVectorRandom(
            ManagedVectorRandom.DeriveSeed(options.Seed, options.TrainingFingerprint));
        var sampler = new ManagedVectorReservoirSampler(options.TrainSample, random);
        foreach (var (rowId, position) in EnumerateRowIdOrder())
        {
            var row = source.GetRow(position);
            var value = columnIndex >= 0 && columnIndex < row.Length ? row[columnIndex] : SqlValue.Null;
            if (!SqliteVectorFunctions.TryDecodeVector(
                    value,
                    options.Encoding,
                    options.Dimensions,
                    out var decoded)
                || !options.TryProject(decoded.Values, out var projected))
            {
                continue;
            }

            sampler.Offer(rowId, projected);
        }

        var eligiblePopulation = sampler.Seen;
        var samples = sampler.Complete();
        var centroids = ManagedVectorTraining.Train(
            samples,
            options.Lists,
            options.Dimensions,
            options.Iterations,
            random);
        index.PublishCentroids(centroids, samples.Count, eligiblePopulation);
    }

    /// <summary>
    /// Base rows in rowid-ascending order.
    /// </summary>
    /// <remarks>
    /// Training walks rowids, not storage positions, so neither the insertion order nor a later
    /// update that moved a row can change which rows the sample draws or how the centroids come out.
    /// </remarks>
    private List<(long RowId, int Position)> EnumerateRowIdOrder()
    {
        var rows = new List<(long RowId, int Position)>(source.RowCount);
        for (var position = 0; position < source.RowCount; position++)
            rows.Add((source.GetRowId(position), position));

        rows.Sort(static (left, right) => left.RowId.CompareTo(right.RowId));
        return rows;
    }

    private void SyncApplied() => attachment.MarkApplied(source.Revision);

    private void CompactIfNeeded()
    {
        if (attachment.Index.NeedsCompaction)
            attachment.Index.Compact();
    }

    private void RequireWritable()
    {
        if (!_writable)
            throw new EmbeddedSqlException("index method 'vector' cursor is not open for writing");
    }

    private (long RowId, SqlValue ColumnValue) SplitValues(ReadOnlySpan<SqlValue> values)
    {
        if (values.Length != 2)
        {
            throw new EmbeddedSqlException(
                $"index method 'vector' expects 2 maintenance values but received {values.Length}");
        }

        var rowIdValue = values[^1];
        if (rowIdValue.Kind != SqlValueKind.Integer)
            throw new EmbeddedSqlException("index method 'vector' requires an integer rowid");

        return (rowIdValue.AsInteger(), values[0]);
    }

    public override void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _results = [];
        _position = -1;
        _writable = false;
    }
}
