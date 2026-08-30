using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Ahtola.Core.Indexing;

namespace Ahtola.Core.Search;

/// <summary>
/// The pure-managed full-text index method registered as <c>USING fts</c>.
/// </summary>
/// <remarks>
/// Aligned with Turso's <c>fts</c> index method (turso-src/core/index_method/fts.rs) on SQL surface,
/// <c>WITH</c> keys, query patterns and merge thresholds, but implemented natively over managed
/// postings rather than Tantivy. See docs/managed-index-methods.md for the full divergence list.
/// </remarks>
internal sealed class ManagedFtsIndexMethod : ManagedIndexMethod
{
    /// <summary>The persisted state version. A newer value in a file fails closed.</summary>
    public const int StateVersion = 2;

    public static ManagedFtsIndexMethod Instance { get; } = new();

    private ManagedFtsIndexMethod()
    {
    }

    public override string Name => "fts";

    public override bool SupportsColumnParameters => true;

    public override ManagedIndexMethodAttachment Attach(ManagedIndexMethodConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new ManagedFtsIndexAttachment(configuration, ManagedFtsIndexOptions.Resolve(configuration));
    }
}

/// <summary>Validated <c>WITH (...)</c> options for one FTS method index.</summary>
internal sealed class ManagedFtsIndexOptions
{
    private static readonly string[] KnownKeys =
    [
        "tokenizer", "weights", "min_gram", "max_gram", "columnsize", "detail",
    ];

    private static readonly string[] KnownColumnKeys =
    [
        "tokenizer", "min_gram", "max_gram",
    ];

    private ManagedFtsIndexOptions(
        ManagedFtsTokenizerOptions tokenizer,
        ManagedFtsTokenizerOptions[] columnTokenizers,
        double[] weights,
        bool columnSize,
        ManagedFtsDetailLevel detail)
    {
        Tokenizer = tokenizer;
        ColumnTokenizers = columnTokenizers;
        Weights = weights;
        ColumnSize = columnSize;
        Detail = detail;
    }

    public ManagedFtsTokenizerOptions Tokenizer { get; }

    /// <summary>Tokenizer assigned to each field in declaration order.</summary>
    public IReadOnlyList<ManagedFtsTokenizerOptions> ColumnTokenizers { get; }

    /// <summary>Per-column BM25 weights, defaulting to 1.0.</summary>
    public double[] Weights { get; }

    /// <summary>Whether per-column token lengths participate in BM25 normalization.</summary>
    public bool ColumnSize { get; }

    /// <summary>Posting detail level: <c>full</c> keeps positions, <c>columns</c> and <c>none</c> do not.</summary>
    public ManagedFtsDetailLevel Detail { get; }

    public static ManagedFtsIndexOptions Resolve(ManagedIndexMethodConfiguration configuration)
    {
        if (configuration.Columns.Count == 0)
            throw new EmbeddedSqlException($"index '{configuration.IndexName}' must name at least one fts column");
        if (configuration.Columns.Count > ManagedIndexMethodLimits.MaxIndexedColumns)
        {
            throw new EmbeddedSqlException(
                $"index '{configuration.IndexName}' exceeds the {ManagedIndexMethodLimits.MaxIndexedColumns} column limit for index methods");
        }
        var columnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in configuration.Columns)
        {
            if (!columnNames.Add(column.Name))
                throw new EmbeddedSqlException($"duplicate fts index column: {column.Name}");
        }

        // Every WITH key must be recognized and consumed; Turso asserts the same invariant in
        // fts_with_keys_all_validated_and_consumed (fts.rs:1640-1649). A duplicate key is rejected
        // too, because silently taking the first or the last would make the DDL text ambiguous.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in configuration.Parameters)
        {
            if (!KnownKeys.Contains(parameter.Name, StringComparer.OrdinalIgnoreCase))
                throw new EmbeddedSqlException($"unknown fts index parameter: {parameter.Name}");
            if (!seen.Add(parameter.Name))
                throw new EmbeddedSqlException($"duplicate fts index parameter: {parameter.Name}");
        }

        var tokenizer = ResolveTokenizer(
            configuration.Parameters,
            "fts index",
            ManagedFtsTokenizerOptions.Default);
        var columnTokenizers = new ManagedFtsTokenizerOptions[configuration.Columns.Count];
        for (var column = 0; column < configuration.Columns.Count; column++)
        {
            var field = configuration.Columns[column];
            columnTokenizers[column] = ResolveColumnTokenizer(field, tokenizer);
        }

        var weights = new double[configuration.Columns.Count];
        Array.Fill(weights, 1.0);
        if (configuration.TryGetParameter("weights", out var weightsValue))
            ParseWeights(configuration, RequireText("weights", weightsValue), weights);

        var columnSizeValue = ReadInteger(configuration, "columnsize", 1);
        if (columnSizeValue is not (0 or 1))
            throw new EmbeddedSqlException("fts index parameter 'columnsize' must be 0 or 1");

        var detail = ManagedFtsDetailLevel.Full;
        if (configuration.TryGetParameter("detail", out var detailValue))
            detail = ManagedFtsSearchIndex.ParseDetail(RequireText("detail", detailValue));

        return new ManagedFtsIndexOptions(
            tokenizer,
            columnTokenizers,
            weights,
            columnSizeValue != 0,
            detail);
    }

    private static ManagedFtsTokenizerOptions ResolveColumnTokenizer(
        ManagedIndexMethodColumn column,
        ManagedFtsTokenizerOptions fallback)
    {
        var parameters = column.Parameters ?? [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in parameters)
        {
            if (!KnownColumnKeys.Contains(parameter.Name, StringComparer.OrdinalIgnoreCase))
            {
                throw new EmbeddedSqlException(
                    $"unknown fts index parameter for column '{column.Name}': {parameter.Name}");
            }
            if (!seen.Add(parameter.Name))
            {
                throw new EmbeddedSqlException(
                    $"duplicate fts index parameter for column '{column.Name}': {parameter.Name}");
            }
        }

        return ResolveTokenizer(parameters, $"fts column '{column.Name}'", fallback);
    }

    private static ManagedFtsTokenizerOptions ResolveTokenizer(
        IReadOnlyList<ManagedIndexMethodParameter> parameters,
        string scope,
        ManagedFtsTokenizerOptions fallback)
    {
        var kind = TryGetParameter(parameters, "tokenizer", out var tokenizerValue)
            ? ManagedFtsTokenizerOptions.ParseKind(RequireText("tokenizer", tokenizerValue))
            : fallback.Kind;
        var hasMinGram = TryGetParameter(parameters, "min_gram", out var minValue);
        var hasMaxGram = TryGetParameter(parameters, "max_gram", out var maxValue);
        var isGramTokenizer = kind is ManagedFtsTokenizerKind.Ngram or ManagedFtsTokenizerKind.Trigram;

        if ((hasMinGram || hasMaxGram) && !isGramTokenizer)
        {
            throw new EmbeddedSqlException(
                $"{scope} parameters 'min_gram'/'max_gram' require the 'ngram' or 'trigram' tokenizer, not '{ManagedFtsTokenizerOptions.FormatKind(kind)}'");
        }
        if ((hasMinGram || hasMaxGram) && kind is ManagedFtsTokenizerKind.Trigram)
        {
            throw new EmbeddedSqlException(
                "fts 'trigram' tokenizer has a fixed gram size; use tokenizer = 'ngram' to configure min_gram/max_gram");
        }

        var inheritedWindow = kind == fallback.Kind && fallback.IsGramTokenizer;
        var defaultMin = inheritedWindow ? fallback.MinGram : 2;
        var defaultMax = inheritedWindow ? fallback.MaxGram : 3;
        var minGram = hasMinGram ? ReadInteger("min_gram", minValue) : defaultMin;
        var maxGram = hasMaxGram ? ReadInteger("max_gram", maxValue) : defaultMax;
        if (isGramTokenizer)
            ManagedFtsTokenization.ValidateGramBounds(minGram, maxGram);

        return new ManagedFtsTokenizerOptions(kind, minGram, maxGram);
    }

    private static bool TryGetParameter(
        IReadOnlyList<ManagedIndexMethodParameter> parameters,
        string name,
        out SqlValue value)
    {
        foreach (var parameter in parameters)
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

    private static void ParseWeights(
        ManagedIndexMethodConfiguration configuration,
        string specification,
        double[] weights)
    {
        var assigned = new HashSet<int>();
        foreach (var entry in specification.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
                throw new EmbeddedSqlException($"invalid fts weights entry: {entry}");

            var column = entry[..separator].Trim();
            var text = entry[(separator + 1)..].Trim();
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var weight)
                || float.IsNaN(weight)
                || float.IsInfinity(weight)
                || weight < 0.0f)
            {
                throw new EmbeddedSqlException($"invalid fts column weight: {text}");
            }

            var position = -1;
            for (var index = 0; index < configuration.Columns.Count; index++)
            {
                if (string.Equals(configuration.Columns[index].Name, column, StringComparison.OrdinalIgnoreCase))
                {
                    position = index;
                    break;
                }
            }

            if (position < 0)
                throw new EmbeddedSqlException($"no such fts column: {column}");
            if (!assigned.Add(position))
                throw new EmbeddedSqlException($"duplicate fts column weight: {column}");

            weights[position] = weight;
        }
    }

    private static string RequireText(string key, SqlValue value)
        => value.Kind == SqlValueKind.Text
            ? value.AsText()
            : throw new EmbeddedSqlException($"fts index parameter '{key}' requires a text literal");

    private static long ReadInteger(ManagedIndexMethodConfiguration configuration, string key, long fallback)
    {
        if (!configuration.TryGetParameter(key, out var value))
            return fallback;

        return value.Kind switch
        {
            SqlValueKind.Integer => value.AsInteger(),
            _ => throw new EmbeddedSqlException($"fts index parameter '{key}' requires an integer literal"),
        };
    }

    private static int ReadInteger(string key, SqlValue value)
    {
        if (value.Kind != SqlValueKind.Integer
            || value.AsInteger() is < int.MinValue or > int.MaxValue)
        {
            throw new EmbeddedSqlException($"fts index parameter '{key}' requires an integer literal");
        }

        return (int)value.AsInteger();
    }

    /// <summary>Rebuilds the canonical <c>WITH</c> text so the catalog round-trip is lossless.</summary>
    public static string? FormatParameters(IReadOnlyList<ManagedIndexMethodParameter> parameters)
        => ManagedIndexMethodParameterFormatter.Format(parameters);
}

/// <summary>One attached FTS method index: immutable configuration plus the derived posting state.</summary>
internal sealed class ManagedFtsIndexAttachment : ManagedIndexMethodAttachment
{
    private readonly ManagedIndexMethodDefinition _definition;
    private ManagedFtsSearchIndex _index;

    /// <summary>
    /// The base-row revision the postings were last reconciled against, or -1 when the postings
    /// have never been built. Reconciliation is skipped outright when the revision has not moved,
    /// which is what keeps a repeated query off the O(base rows) path.
    /// </summary>
    private long _appliedRevision = -1;

    public ManagedFtsIndexAttachment(
        ManagedIndexMethodConfiguration configuration,
        ManagedFtsIndexOptions options)
    {
        Configuration = configuration;
        Options = options;
        _definition = new ManagedIndexMethodDefinition(
            "fts",
            configuration.IndexName,
            [
                // Declared most specific first, matching Turso's FTS_PATTERN_* ordering (fts.rs:1908-1914).
                new ManagedIndexQueryPattern(ManagedIndexPatternShape.Score, 2),
                new ManagedIndexQueryPattern(ManagedIndexPatternShape.CombinedOrderedLimit, 2),
                new ManagedIndexQueryPattern(ManagedIndexPatternShape.CombinedOrdered, 1),
                new ManagedIndexQueryPattern(ManagedIndexPatternShape.CombinedLimit, 2),
                new ManagedIndexQueryPattern(ManagedIndexPatternShape.Combined, 1),
                new ManagedIndexQueryPattern(ManagedIndexPatternShape.MatchLimit, 2),
                new ManagedIndexQueryPattern(ManagedIndexPatternShape.Match, 1),
            ],
            backingBtree: true,
            // Unordered MATCH/combined shapes are exposed as a cursor stream. DML callers therefore
            // collect stable rowids before applying writes, matching Turso's safety contract.
            resultsMaterialized: false,
            // Every byte of durable state is either the ordinary index b-tree the file store already
            // writes or the catalog row itself, both of which the engine keeps snapshot-isolated.
            mvccSupport: ManagedIndexMethodMvccSupport.TransactionalBackingStore,
            storageVersion: ManagedFtsIndexMethod.StateVersion);
        _index = CreateIndex();
    }

    public ManagedFtsIndexOptions Options { get; }

    public override ManagedIndexMethodDefinition Definition => _definition;

    public override ManagedIndexMethodConfiguration Configuration { get; }

    public override IManagedIndexMethodPlannerAdapter Planner => ManagedFtsPlannerAdapter.Instance;

    /// <summary>The live posting state. Only cursors mutate it.</summary>
    internal ManagedFtsSearchIndex Index => _index;

    public override ManagedIndexMethodCursor Open(IManagedIndexSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new ManagedFtsIndexCursor(this, source);
    }

    public override byte[] SaveState()
    {
        // A stable header, not a snapshot of derived counters: the postings themselves are rebuilt
        // from the ordinary index b-tree and the base rows, so the persisted catalog text stays
        // byte-identical across commits that do not change the schema.
        var columns = Configuration.Columns.Count;
        var buffer = new byte[
            StateHeaderSize
            + (columns * sizeof(double))
            + (columns * ColumnTokenizerStateSize)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, columns);
        buffer[sizeof(int)] = (byte)Options.Tokenizer.Kind;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(sizeof(int) + sizeof(byte)), Options.Tokenizer.MinGram);
        BinaryPrimitives.WriteInt32LittleEndian(
            buffer.AsSpan(sizeof(int) + sizeof(byte) + sizeof(int)),
            Options.Tokenizer.MaxGram);
        buffer[sizeof(int) + sizeof(byte) + (2 * sizeof(int))] = (byte)Options.Detail;
        buffer[sizeof(int) + (2 * sizeof(byte)) + (2 * sizeof(int))] = Options.ColumnSize ? (byte)1 : (byte)0;
        for (var column = 0; column < columns; column++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(
                buffer.AsSpan(StateHeaderSize + (column * sizeof(double))),
                Options.Weights[column]);
        }
        var tokenizerOffset = StateHeaderSize + (columns * sizeof(double));
        for (var column = 0; column < columns; column++)
        {
            var tokenizer = Options.ColumnTokenizers[column];
            buffer[tokenizerOffset] = (byte)tokenizer.Kind;
            BinaryPrimitives.WriteInt32LittleEndian(
                buffer.AsSpan(tokenizerOffset + sizeof(byte)),
                tokenizer.MinGram);
            BinaryPrimitives.WriteInt32LittleEndian(
                buffer.AsSpan(tokenizerOffset + sizeof(byte) + sizeof(int)),
                tokenizer.MaxGram);
            tokenizerOffset += ColumnTokenizerStateSize;
        }

        return buffer;
    }

    /// <summary>Global header followed by weights and one tokenizer tuple per field.</summary>
    private const int StateHeaderSize = sizeof(int) + sizeof(byte) + (2 * sizeof(int)) + sizeof(byte) + sizeof(byte);
    private const int ColumnTokenizerStateSize = sizeof(byte) + (2 * sizeof(int));

    public override void LoadState(int version, ReadOnlySpan<byte> bytes)
    {
        // No envelope at all: the postings are derived state, so a catalog row that never carried
        // one simply rebuilds from the base rows. This is the documented "missing state rebuilds
        // silently" path and is distinct from an envelope that exists but is empty.
        if (version == 0 && bytes.Length == 0)
            return;

        if (version <= 0)
            throw new EmbeddedSqlException($"malformed managed index '{Configuration.IndexName}': invalid state version");
        if (version > ManagedFtsIndexMethod.StateVersion)
        {
            throw new EmbeddedSqlException(
                $"index '{Configuration.IndexName}' was written by a newer managed index method (v{version})");
        }

        // An envelope that exists must be complete: a zero-length payload is a truncated write, not
        // a legitimate "no state" marker, and silently accepting it would hide catalog corruption.
        if (bytes.Length == 0)
            throw new EmbeddedSqlException($"malformed managed index '{Configuration.IndexName}': empty state");

        var columns = Configuration.Columns.Count;
        var legacyExpected = StateHeaderSize + (columns * sizeof(double));
        var expected = version == 1
            ? legacyExpected
            : legacyExpected + (columns * ColumnTokenizerStateSize);
        if (bytes.Length < StateHeaderSize)
            throw new EmbeddedSqlException($"malformed managed index '{Configuration.IndexName}': truncated state");

        var storedColumns = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (storedColumns != columns || bytes.Length != expected)
        {
            throw new EmbeddedSqlException(
                $"malformed managed index '{Configuration.IndexName}': state declares {storedColumns} columns but the index has {columns}");
        }

        var storedKind = bytes[sizeof(int)];
        if (!Enum.IsDefined(typeof(ManagedFtsTokenizerKind), (int)storedKind))
            throw new EmbeddedSqlException($"malformed managed index '{Configuration.IndexName}': unknown tokenizer");
        if ((ManagedFtsTokenizerKind)storedKind != Options.Tokenizer.Kind)
        {
            throw new EmbeddedSqlException(
                $"malformed managed index '{Configuration.IndexName}': state tokenizer does not match the index definition");
        }

        var storedMinGram = BinaryPrimitives.ReadInt32LittleEndian(bytes[(sizeof(int) + sizeof(byte))..]);
        var storedMaxGram = BinaryPrimitives.ReadInt32LittleEndian(bytes[(sizeof(int) + sizeof(byte) + sizeof(int))..]);
        if (storedMinGram != Options.Tokenizer.MinGram || storedMaxGram != Options.Tokenizer.MaxGram)
        {
            throw new EmbeddedSqlException(
                $"malformed managed index '{Configuration.IndexName}': state gram bounds do not match the index definition");
        }

        if (Options.Tokenizer.IsGramTokenizer)
            ManagedFtsTokenization.ValidateGramBounds(storedMinGram, storedMaxGram);

        var storedDetail = bytes[sizeof(int) + sizeof(byte) + (2 * sizeof(int))];
        if (!Enum.IsDefined(typeof(ManagedFtsDetailLevel), (int)storedDetail))
            throw new EmbeddedSqlException($"malformed managed index '{Configuration.IndexName}': unknown detail level");
        if ((ManagedFtsDetailLevel)storedDetail != Options.Detail)
        {
            throw new EmbeddedSqlException(
                $"malformed managed index '{Configuration.IndexName}': state detail level does not match the index definition");
        }

        var storedColumnSize = bytes[sizeof(int) + (2 * sizeof(byte)) + (2 * sizeof(int))];
        if (storedColumnSize is not (0 or 1))
            throw new EmbeddedSqlException($"malformed managed index '{Configuration.IndexName}': invalid columnsize flag");
        if ((storedColumnSize != 0) != Options.ColumnSize)
        {
            throw new EmbeddedSqlException(
                $"malformed managed index '{Configuration.IndexName}': state columnsize does not match the index definition");
        }

        for (var column = 0; column < columns; column++)
        {
            var weight = BinaryPrimitives.ReadDoubleLittleEndian(
                bytes[(StateHeaderSize + (column * sizeof(double)))..]);
            if (double.IsNaN(weight) || double.IsInfinity(weight) || weight < 0.0)
                throw new EmbeddedSqlException($"malformed managed index '{Configuration.IndexName}': invalid column weight");
            if (Math.Abs(weight - Options.Weights[column]) > double.Epsilon)
            {
                throw new EmbeddedSqlException(
                    $"malformed managed index '{Configuration.IndexName}': state weights do not match the index definition");
            }
        }

        if (version >= 2)
        {
            var tokenizerOffset = legacyExpected;
            for (var column = 0; column < columns; column++)
            {
                var kind = bytes[tokenizerOffset];
                if (!Enum.IsDefined(typeof(ManagedFtsTokenizerKind), (int)kind))
                {
                    throw new EmbeddedSqlException(
                        $"malformed managed index '{Configuration.IndexName}': unknown column tokenizer");
                }

                var minGram = BinaryPrimitives.ReadInt32LittleEndian(bytes[(tokenizerOffset + sizeof(byte))..]);
                var maxGram = BinaryPrimitives.ReadInt32LittleEndian(
                    bytes[(tokenizerOffset + sizeof(byte) + sizeof(int))..]);
                var configured = Options.ColumnTokenizers[column];
                if ((ManagedFtsTokenizerKind)kind != configured.Kind
                    || minGram != configured.MinGram
                    || maxGram != configured.MaxGram)
                {
                    throw new EmbeddedSqlException(
                        $"malformed managed index '{Configuration.IndexName}': state column tokenizer does not match the index definition");
                }

                if (configured.IsGramTokenizer)
                    ManagedFtsTokenization.ValidateGramBounds(minGram, maxGram);
                tokenizerOffset += ColumnTokenizerStateSize;
            }
        }
    }

    public override ManagedIndexMethodAttachment Fork()
    {
        // The postings are derived state: a fork starts empty and rebuilds from the snapshot's base
        // rows on first use, so a rolled-back statement can never leave method state behind.
        return new ManagedFtsIndexAttachment(Configuration, Options);
    }

    internal ManagedFtsSearchIndex CreateIndex()
        => new(
            Configuration.Columns.Count,
            Options.ColumnTokenizers,
            Options.Weights,
            Options.Detail,
            Options.ColumnSize)
        {
            ColumnIndexResolver = ResolveColumnIndex,
        };

    /// <summary>
    /// Publishes a rebuilt posting set atomically. REINDEX and OPTIMIZE build into a detached index
    /// and call this only after the build succeeded, so a failure part-way through leaves the
    /// previously published state intact and still queryable.
    /// </summary>
    internal void PublishIndex(ManagedFtsSearchIndex index, long revision)
    {
        ArgumentNullException.ThrowIfNull(index);
        _index = index;
        _appliedRevision = revision;
    }

    /// <summary>Discards all derived state, forcing a rebuild on next use (DROP INDEX, Destroy).</summary>
    internal void ResetIndex()
    {
        _index = CreateIndex();
        _appliedRevision = -1;
    }

    internal long AppliedRevision => _appliedRevision;

    internal void MarkApplied(long revision) => _appliedRevision = revision;

    internal bool HasBeenBuilt => _appliedRevision >= 0;

    internal int? ResolveColumnIndex(string name)
    {
        for (var index = 0; index < Configuration.Columns.Count; index++)
        {
            if (string.Equals(Configuration.Columns[index].Name, name, StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return null;
    }

    internal bool IsIndexedColumn(string name) => ResolveColumnIndex(name) is not null;

    /// <summary>Parses a query against this index's tokenizer and column set.</summary>
    internal ManagedFtsNode ParseQuery(string query)
        => ManagedFtsQueryLanguage.ParseMethod(
            query,
            Configuration.Columns.Select(static column => column.Name).ToArray(),
            Options.ColumnTokenizers);

    public override bool HasEquivalentQuerySemantics(ManagedIndexMethodAttachment other)
    {
        if (other is not ManagedFtsIndexAttachment fts
            || Options.Detail != fts.Options.Detail
            || Options.ColumnSize != fts.Options.ColumnSize
            || Configuration.Columns.Count != fts.Configuration.Columns.Count)
        {
            return false;
        }

        for (var left = 0; left < Configuration.Columns.Count; left++)
        {
            var name = Configuration.Columns[left].Name;
            var right = fts.ResolveColumnIndex(name);
            if (right is null
                || Options.ColumnTokenizers[left] != fts.Options.ColumnTokenizers[right.Value]
                || Options.Weights[left] != fts.Options.Weights[right.Value])
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// One per-statement FTS cursor. Single threaded and disposed at statement finalize or reset; it
/// holds no cross-connection cache, so the engine's pager snapshot is the only read state.
/// </summary>
internal sealed class ManagedFtsIndexCursor(ManagedFtsIndexAttachment attachment, IManagedIndexSource source)
    : ManagedIndexMethodCursor
{
    private IReadOnlyList<ManagedFtsHit> _hits = [];
    private IEnumerator<ManagedFtsHit>? _stream;
    private ManagedFtsHit? _streamCurrent;
    private int _position = -1;
    private int _currentPattern;
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
        _hits = [];
        _stream?.Dispose();
        _stream = null;
        _streamCurrent = null;
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
        var (rowId, columns) = SplitValues(values);
        attachment.Index.Upsert(rowId, columns, columns);
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

    /// <summary>
    /// Records that the postings now reflect the current base-row revision. Explicit maintenance
    /// and incremental replay both end here, so the next statement can prove it has nothing to do.
    /// </summary>
    private void SyncApplied() => attachment.MarkApplied(source.Revision);

    public override bool QueryStart(int patternIndex, ReadOnlySpan<SqlValue> arguments)
    {
        if (patternIndex < 0 || patternIndex >= attachment.Definition.Patterns.Count)
            throw new EmbeddedSqlException($"index method 'fts' has no query pattern {patternIndex}");
        if (arguments.Length == 0)
            throw new EmbeddedSqlException("index method 'fts' requires a query argument");

        Refresh(force: false);
        _currentPattern = patternIndex;
        var queryText = ManagedFtsPlannerAdapter.RequireQueryText(arguments[0]);
        int? limit = null;
        if (arguments.Length > 1 && arguments[1].Kind == SqlValueKind.Integer)
        {
            var requested = arguments[1].AsInteger();
            limit = requested < 0 ? null : (int)Math.Min(requested, int.MaxValue);
        }

        _stream?.Dispose();
        _stream = null;
        _streamCurrent = null;
        var shape = attachment.Definition.Patterns[_currentPattern].Shape;
        var query = attachment.ParseQuery(queryText);
        if (shape is ManagedIndexPatternShape.Match
            or ManagedIndexPatternShape.MatchLimit
            or ManagedIndexPatternShape.Combined
            or ManagedIndexPatternShape.CombinedLimit)
        {
            _hits = [];
            _position = -1;
            _stream = attachment.Index.SearchUnordered(
                    query,
                    attachment.Options.Weights,
                    includeScores: shape is ManagedIndexPatternShape.Combined
                        or ManagedIndexPatternShape.CombinedLimit,
                    limit)
                .GetEnumerator();
            if (_stream.MoveNext())
            {
                _streamCurrent = _stream.Current;
                return true;
            }

            Reset();
            return false;
        }

        _hits = attachment.Index.Search(query, attachment.Options.Weights, limit);
        _position = _hits.Count == 0 ? -1 : 0;
        return _position >= 0;
    }

    public override bool QueryNext()
    {
        if (_stream is not null)
        {
            if (_stream.MoveNext())
            {
                _streamCurrent = _stream.Current;
                return true;
            }

            return Reset();
        }

        if (_position < 0)
            return false;

        return ++_position < _hits.Count || Reset();
    }

    private bool Reset()
    {
        _stream?.Dispose();
        _stream = null;
        _streamCurrent = null;
        _position = -1;
        return false;
    }

    public override SqlValue Column(int index)
    {
        var hit = CurrentHit();
        if (hit is null)
            return SqlValue.Null;

        if (index != 0)
            return SqlValue.Null;

        return attachment.Definition.Patterns.Count > 0
            && attachment.Definition.Patterns[_currentPattern].Shape
                is ManagedIndexPatternShape.Match or ManagedIndexPatternShape.MatchLimit
            ? SqlValue.Integer(1)
            : SqlValue.Real(hit.Value.Score);
    }

    public override long? RowId()
        => CurrentHit()?.RowId;

    private ManagedFtsHit? CurrentHit()
        => _stream is not null
            ? _streamCurrent
            : _position >= 0 && _position < _hits.Count
                ? _hits[_position]
                : null;

    public override void Optimize()
    {
        // Build the compacted posting set on a detached index and publish it only once the build
        // succeeded, exactly like Rebuild. Compaction is a reclaim of superseded postings, so a
        // failure part-way through must leave the previously published state whole rather than
        // partially purged.
        Refresh(force: false);
        var optimized = attachment.CreateIndex();
        BuildInto(optimized);
        PublishRebuild(optimized);
    }

    public override void Rebuild()
    {
        // Build into a detached index and publish only on success: a throw part-way through a
        // REINDEX must leave the previously published postings intact rather than half-erased.
        var rebuilt = attachment.CreateIndex();
        BuildInto(rebuilt);
        PublishRebuild(rebuilt);
        CompactIfNeeded();
    }

    public override ManagedIndexMethodCostEstimate? EstimateCost(in ManagedIndexMethodCostContext context)
    {
        var baseRows = Math.Max(context.BaseTableRows, 0);

        // Pricing happens before reconciliation, so the corpus size the plan will actually run
        // against is the one it will have afterwards. A stale or cold index would otherwise report a
        // one-document corpus and price a LIMIT plan as if it could only ever return a single row.
        var reconciled = source.Revision == attachment.AppliedRevision;
        var documents = Math.Max(
            reconciled ? attachment.Index.DocumentCount : Math.Max(baseRows, attachment.Index.DocumentCount),
            1);
        var shape = attachment.Definition.Patterns[context.PatternIndex].Shape;
        var estimatedRows = ManagedIndexPatternShapes.HasLimit(shape) && context.Limit is { } limit
            ? limit <= 0 ? 0 : Math.Max(Math.Min(limit, documents), 1)
            : Math.Max(documents / 100, 1);

        // A ranking-only pattern never removes a row: every base row must still be produced, with
        // the non-matching ones ranked last. Pricing it as if only the hits came back is what made
        // an ORDER BY-only plan look cheaper than the scan it is strictly more expensive than.
        if (ManagedIndexPatternShapes.IsRankingOnly(shape))
        {
            estimatedRows = ManagedIndexPatternShapes.HasLimit(shape) && context.Limit is { } capped
                ? capped <= 0 ? 0 : Math.Max(Math.Min(capped, Math.Max(baseRows, 1)), 1)
                : Math.Max(baseRows, 1);
        }

        // A method scan costs one posting walk per query term plus one base-row seek per hit; a full
        // table scan costs one row read per base row. Both are expressed in the same row-read unit
        // the join cost model uses.
        var termCost = Math.Log2(documents + 1);
        var refreshCost = EstimateRefreshCost(baseRows);
        var cost = (estimatedRows * 2.0) + termCost + refreshCost;
        return new ManagedIndexMethodCostEstimate(cost, estimatedRows, Detail: null, RefreshCost: refreshCost);
    }

    /// <summary>
    /// The reconciliation the very next query will be forced to perform, priced in the same
    /// row-read unit as everything else. Charging zero here is what let a stale index advertise a
    /// cheap plan and then walk every base row before returning its first result.
    /// </summary>
    private double EstimateRefreshCost(long baseRows)
    {
        if (source.Revision == attachment.AppliedRevision)
            return 0.0;

        if (attachment.HasBeenBuilt && source.TryGetDelta(attachment.AppliedRevision) is { } delta)
            return delta.ChangedRowIds.Count;

        return Math.Max(baseRows, 1);
    }

    /// <summary>
    /// Reconciles the derived postings with the base rows.
    /// </summary>
    /// <remarks>
    /// The engine bumps a revision counter on every base-row mutation and records the rowids it
    /// touched, so an unchanged table costs O(1) and a small DML costs O(changed rows). The full
    /// base-row walk only runs when the journal cannot prove it saw every mutation since the last
    /// reconciliation — after a rollback restored a forked attachment, for example — and
    /// <see cref="EstimateCost"/> prices that case explicitly instead of hiding it.
    /// </remarks>
    private void Refresh(bool force)
    {
        if (!force && source.Revision == attachment.AppliedRevision)
            return;

        if (!force
            && attachment.HasBeenBuilt
            && source.TryGetDelta(attachment.AppliedRevision) is { } delta)
        {
            ApplyDelta(delta);
            return;
        }

        var rebuilt = attachment.CreateIndex();
        BuildInto(rebuilt);
        PublishRebuild(rebuilt);
        CompactIfNeeded();
    }

    /// <summary>
    /// Swaps a freshly built posting set in and tells the engine the mutation journal can drop
    /// everything older. Publication is the last step, so a build that threw leaves the previously
    /// published state live and queryable.
    /// </summary>
    private void PublishRebuild(ManagedFtsSearchIndex rebuilt)
    {
        ManagedIndexMethodDiagnostics.RecordStateRebuild();
        var revision = source.Revision;
        attachment.PublishIndex(rebuilt, revision);
        source.NotifyRebuilt(revision);
    }

    private void ApplyDelta(ManagedIndexSourceDelta delta)
    {
        var index = attachment.Index;
        var columnCount = attachment.Configuration.Columns.Count;
        foreach (var rowId in delta.ChangedRowIds)
        {
            if (source.TryGetPosition(rowId, out var position))
            {
                var row = source.GetRow(position);
                index.Upsert(rowId, row, ProjectColumns(row, columnCount));
            }
            else
            {
                index.Remove(rowId);
            }
        }

        attachment.MarkApplied(delta.Revision);
        CompactIfNeeded();
    }

    private void BuildInto(ManagedFtsSearchIndex index)
    {
        var columnCount = attachment.Configuration.Columns.Count;
        for (var position = 0; position < source.RowCount; position++)
        {
            var row = source.GetRow(position);
            index.Upsert(source.GetRowId(position), row, ProjectColumns(row, columnCount));
        }
    }

    private SqlValue[] ProjectColumns(SqlValue[] row, int columnCount)
    {
        var buffer = new SqlValue[columnCount];
        for (var column = 0; column < columnCount; column++)
        {
            var columnIndex = attachment.Configuration.Columns[column].ColumnIndex;
            buffer[column] = columnIndex >= 0 && columnIndex < row.Length ? row[columnIndex] : SqlValue.Null;
        }

        return buffer;
    }

    private void CompactIfNeeded()
    {
        // Compaction above the synchronous bound is deferred to an explicit optimize, mirroring
        // Turso's merge policy (fts.rs:73-91).
        if (attachment.Index.NeedsCompaction
            && attachment.Index.DocumentCount <= ManagedFtsSearchIndex.MaxSynchronousCompactionDocuments)
        {
            attachment.Index.Compact();
        }
    }

    private void RequireWritable()
    {
        if (!_writable)
            throw new EmbeddedSqlException("index method 'fts' cursor is not open for writing");
    }

    private (long RowId, SqlValue[] Columns) SplitValues(ReadOnlySpan<SqlValue> values)
    {
        var columnCount = attachment.Configuration.Columns.Count;
        if (values.Length != columnCount + 1)
        {
            throw new EmbeddedSqlException(
                $"index method 'fts' expects {columnCount + 1} maintenance values but received {values.Length}");
        }

        var rowIdValue = values[^1];
        if (rowIdValue.Kind != SqlValueKind.Integer)
            throw new EmbeddedSqlException("index method 'fts' requires an integer rowid");

        return (rowIdValue.AsInteger(), values[..columnCount].ToArray());
    }

    public override void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _hits = [];
        _stream?.Dispose();
        _stream = null;
        _streamCurrent = null;
        _position = -1;
        _writable = false;
    }
}
