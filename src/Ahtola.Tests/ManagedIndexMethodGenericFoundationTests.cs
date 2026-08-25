using Ahtola.Core;
using Ahtola.Core.Indexing;
using Ahtola.Core.Parsing;
using AwesomeAssertions;
using static Ahtola.Tests.ManagedIndexMethodTestHarness;

namespace Ahtola.Tests;

/// <summary>
/// Vector-readiness probe for the managed index-method foundation.
/// </summary>
/// <remarks>
/// <para>
/// A minimal method that has nothing to do with full-text search registers KNN and KNN+LIMIT
/// patterns over a blob column, recognizes <c>ORDER BY vector_distance_l2(col, ?) ASC</c> through its
/// own planner adapter, and is planned and executed by the core engine end to end. If any part of
/// the core still assumed FTS — an <c>fts_match</c>/<c>fts_score</c> name, a text-only query
/// argument, or a cast to an FTS attachment or hit type — this suite would not compile or would
/// return the wrong rows.
/// </para>
/// <para>
/// This is a test double on purpose. It proves the abstraction is genuinely method generic; it is
/// not the start of a vector index, and no vector indexing work begins here.
/// </para>
/// </remarks>
public sealed class ManagedIndexMethodGenericFoundationTests
{
    [Test]
    public void ANonFtsMethodPlansAndExecutesAKnnLimitPattern()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedPoints(connection);

        ExplainDetail(connection, KnnQuery(500, 3))
            .Should().Contain("USING INDEX METHOD probe")
            .And.Contain("pattern=KnnLimit");

        var nearest = QueryIntegers(connection, KnnQuery(500, 3));
        nearest.Should().HaveCount(3);
        nearest[0].Should().Be(500);
        nearest.Should().BeEquivalentTo(new long[] { 499, 500, 501 });
    }

    [Test]
    public void ANonFtsMethodStillProducesEveryRowWhenRankingWithoutALimit()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedPoints(connection);

        // KNN is a ranking pattern: it may reorder but must never drop rows.
        var total = QueryIntegers(connection, "SELECT count(*) FROM points;")[0];
        QueryIntegers(
                connection,
                "SELECT id FROM points ORDER BY vector_distance_l2(embedding, vector32('[500]')) ASC;")
            .Should().HaveCount((int)total);
    }

    [Test]
    public void ANonFtsMethodDeclaresItsOwnPatternsAndIsRegisteredWithoutReflection()
    {
        ProbeIndexMethod.EnsureRegistered();

        ManagedIndexMethodRegistry.Names.Should().Contain("probe");
        var attachment = ManagedIndexMethodRegistry.Resolve("probe").Attach(
            new ManagedIndexMethodConfiguration(
                "points",
                "points_knn",
                [new ManagedIndexMethodColumn("embedding", 1)],
                []));

        attachment.Definition.Patterns.Select(static pattern => pattern.Shape)
            .Should().Equal(ManagedIndexPatternShape.KnnLimit, ManagedIndexPatternShape.Knn);
        attachment.Planner.OwnedFunctionNames.Should().Equal("VECTOR_DISTANCE_L2");
    }

    [Test]
    public void ANonFtsMethodDeclinesWhenItsFunctionIsShadowed()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedPoints(connection);

        ExplainDetail(connection, KnnQuery(500, 3)).Should().Contain("USING INDEX METHOD probe");

        connection.RegisterScalarFunction("vector_distance_l2", -1, static _ => SqlValue.Real(0.0));

        ExplainDetail(connection, KnnQuery(500, 3)).Should().NotContain("INDEX METHOD");
    }

    [Test]
    public void ANonFtsMethodReceivesABlobArgumentUnchanged()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedPoints(connection);

        // The pushed-down argument is a vector blob, not text: the generic execution path has to
        // hand the method the value it was given rather than assuming an FTS-style query string.
        ProbeIndexCursor.LastArgumentKind = null;
        QueryIntegers(connection, KnnQuery(10, 1)).Should().Equal(10);
        ProbeIndexCursor.LastArgumentKind.Should().Be(SqlValueKind.Blob);
    }

    [Test]
    public void ANonFtsMethodPreservesScalarErrorSemanticsBeforeExecuting()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedPoints(connection);

        ShouldThrow(
                connection,
                "SELECT id FROM points ORDER BY vector_distance_l2(embedding, 'not a vector') ASC LIMIT 3;")
            .Message.Should().Contain("vector");
    }

    [Test]
    public void TheCorePlannerAndExecutorContainNoFtsSpecificCode()
    {
        // Structural guard for the generic foundation: every FTS cast and every fts_* function name
        // must live in the FTS implementation, never in the shared planner/execution path. A future
        // change that reintroduces one here fails this test before it can shape the abstraction.
        var repositoryRoot = FindRepositoryRoot();
        var generic = Path.Combine(repositoryRoot, "src", "Ahtola.Core", "EmbeddedDatabase.IndexMethods.cs");
        File.Exists(generic).Should().BeTrue(generic);

        var offenders = new List<string>();
        var lines = File.ReadAllLines(generic);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var code = line.TrimStart();

            // Comments may name FTS as an example; code may not.
            if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("///", StringComparison.Ordinal))
                continue;

            if (line.Contains("ManagedFts", StringComparison.Ordinal))
                offenders.Add($"{index + 1}: {code}");
        }

        offenders.Should().BeEmpty();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ahtola.slnx")))
            directory = directory.Parent;

        directory.Should().NotBeNull("the test run must sit inside the repository");
        return directory!.FullName;
    }

    private static string KnnQuery(int target, int limit)
        => $"SELECT id FROM points ORDER BY vector_distance_l2(embedding, vector32('[{target}]')) ASC LIMIT {limit};";

    private static string ExplainDetail(EmbeddedConnection connection, string sql)
    {
        var rows = Query(connection, "EXPLAIN QUERY PLAN " + sql);
        return rows.Count == 0 ? string.Empty : rows[^1][3].AsText();
    }

    private static void SeedPoints(EmbeddedConnection connection)
    {
        ProbeIndexMethod.EnsureRegistered();
        Execute(connection, "CREATE TABLE points(id INTEGER PRIMARY KEY, embedding BLOB);");
        Execute(connection, "CREATE INDEX points_knn ON points USING probe (embedding);");
        Execute(connection, "BEGIN;");
        for (var id = 1; id <= 1000; id++)
            Execute(connection, $"INSERT INTO points VALUES ({id}, vector32('[{id}]'));");

        Execute(connection, "COMMIT;");
    }
}

/// <summary>
/// A deliberately trivial nearest-neighbour method over one vector column, used only to prove the
/// foundation is method generic. Registration is a direct managed call, never reflection, so it stays
/// NativeAOT and trimming safe like the shipped FTS method.
/// </summary>
internal sealed class ProbeIndexMethod : ManagedIndexMethod
{
    private static readonly object Gate = new();
    private static bool _registered;

    public static ProbeIndexMethod Instance { get; } = new();

    private ProbeIndexMethod()
    {
    }

    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (_registered)
                return;

            if (!ManagedIndexMethodRegistry.TryResolve("probe", out _))
                ManagedIndexMethodRegistry.Register(Instance);

            _registered = true;
        }
    }

    public override string Name => "probe";

    public override ManagedIndexMethodAttachment Attach(ManagedIndexMethodConfiguration configuration)
    {
        if (configuration.Columns.Count != 1)
            throw new EmbeddedSqlException($"index '{configuration.IndexName}' must name exactly one probe column");
        if (configuration.Parameters.Count != 0)
            throw new EmbeddedSqlException($"index '{configuration.IndexName}' accepts no WITH parameters");

        return new ProbeIndexAttachment(configuration);
    }
}

internal sealed class ProbeIndexAttachment : ManagedIndexMethodAttachment
{
    private readonly ManagedIndexMethodDefinition _definition;

    public ProbeIndexAttachment(ManagedIndexMethodConfiguration configuration)
    {
        Configuration = configuration;
        _definition = new ManagedIndexMethodDefinition(
            "probe",
            configuration.IndexName,
            [
                new ManagedIndexQueryPattern(ManagedIndexPatternShape.KnnLimit, 2),
                new ManagedIndexQueryPattern(ManagedIndexPatternShape.Knn, 1),
            ],
            backingBtree: true,
            resultsMaterialized: true,
            ManagedIndexMethodMvccSupport.TransactionalBackingStore,
            storageVersion: 1);
    }

    public override ManagedIndexMethodDefinition Definition => _definition;

    public override ManagedIndexMethodConfiguration Configuration { get; }

    public override IManagedIndexMethodPlannerAdapter Planner => ProbePlannerAdapter.Instance;

    public override ManagedIndexMethodCursor Open(IManagedIndexSource source) => new ProbeIndexCursor(this, source);

    public override byte[] SaveState() => [1];

    public override void LoadState(int version, ReadOnlySpan<byte> bytes)
    {
        if (version > 1)
            throw new EmbeddedSqlException($"index '{Configuration.IndexName}' was written by a newer method");
    }

    public override ManagedIndexMethodAttachment Fork() => new ProbeIndexAttachment(Configuration);
}

internal sealed class ProbeIndexCursor(ProbeIndexAttachment attachment, IManagedIndexSource source)
    : ManagedIndexMethodCursor
{
    private readonly List<(long RowId, double Distance)> _results = [];
    private int _position = -1;

    /// <summary>The kind of the last pushed-down argument, so a test can prove it was not coerced.</summary>
    internal static SqlValueKind? LastArgumentKind { get; set; }

    public override void Create()
    {
    }

    public override void Destroy() => _results.Clear();

    public override void OpenRead()
    {
    }

    public override void OpenWrite()
    {
    }

    public override void Insert(ReadOnlySpan<SqlValue> values)
    {
    }

    public override void Delete(ReadOnlySpan<SqlValue> values)
    {
    }

    public override bool QueryStart(int patternIndex, ReadOnlySpan<SqlValue> arguments)
    {
        if (arguments.Length == 0)
            throw new EmbeddedSqlException("index method 'probe' requires a target vector");

        LastArgumentKind = arguments[0].Kind;
        var target = arguments[0];
        var limit = arguments.Length > 1 && arguments[1].Kind == SqlValueKind.Integer
            ? (int)arguments[1].AsInteger()
            : int.MaxValue;

        var columnIndex = attachment.Configuration.Columns[0].ColumnIndex;
        _results.Clear();
        for (var position = 0; position < source.RowCount; position++)
        {
            var row = source.GetRow(position);
            if (columnIndex >= row.Length || row[columnIndex].Kind != SqlValueKind.Blob)
                continue;

            var distance = SqliteVectorFunctions.DistanceL2([row[columnIndex], target]);
            if (distance.Kind is not (SqlValueKind.Real or SqlValueKind.Integer))
                continue;

            _results.Add((
                source.GetRowId(position),
                distance.Kind == SqlValueKind.Real ? distance.AsReal() : distance.AsInteger()));
        }

        _results.Sort(static (left, right)
            => left.Distance.Equals(right.Distance)
                ? left.RowId.CompareTo(right.RowId)
                : left.Distance.CompareTo(right.Distance));

        if (limit >= 0 && _results.Count > limit)
            _results.RemoveRange(limit, _results.Count - limit);

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
        => _position >= 0 && _position < _results.Count && index == 0
            ? SqlValue.Real(_results[_position].Distance)
            : SqlValue.Null;

    public override long? RowId()
        => _position >= 0 && _position < _results.Count ? _results[_position].RowId : null;

    public override ManagedIndexMethodCostEstimate? EstimateCost(in ManagedIndexMethodCostContext context)
    {
        var shape = attachment.Definition.Patterns[context.PatternIndex].Shape;
        var baseRows = Math.Max(context.BaseTableRows, 1);
        var rows = ManagedIndexPatternShapes.HasLimit(shape) && context.Limit is { } limit
            ? Math.Max(Math.Min(limit, baseRows), 1)
            : baseRows;
        return new ManagedIndexMethodCostEstimate(rows * 2.0, rows);
    }
}

/// <summary>Recognizes <c>ORDER BY vector_distance_l2(col, ?) ASC [LIMIT n]</c>.</summary>
internal sealed class ProbePlannerAdapter : IManagedIndexMethodPlannerAdapter
{
    public const string DistanceFunction = "VECTOR_DISTANCE_L2";

    private static readonly string[] Owned = [DistanceFunction];

    public static ProbePlannerAdapter Instance { get; } = new();

    private ProbePlannerAdapter()
    {
    }

    public IReadOnlyList<string> OwnedFunctionNames => Owned;

    public bool TryMatch(ManagedIndexMethodPlannerContext context, out ManagedIndexMethodPatternMatch match)
    {
        match = null!;
        if (context.IsShadowedFunction(DistanceFunction))
            return false;
        if (context.OrderBy is not { Count: > 0 })
            return false;

        var term = context.OrderBy[0];
        if (term.Descending
            || term.Expression is not FunctionExpression function
            || !string.Equals(function.Name, DistanceFunction, StringComparison.OrdinalIgnoreCase)
            || function.Arguments.Count != 2
            || function.Window is not null
            || function.Filter is not null
            || function.Distinct)
        {
            return false;
        }

        if (function.Arguments[0] is not ColumnExpression { BooleanKeyword: null } column)
            return false;
        if (column.Qualifier is { } qualifier
            && !string.Equals(qualifier, context.Qualifier, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!string.Equals(
                column.UnqualifiedName ?? column.Name,
                context.Columns[0].Name,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!context.IsHoistableArgument(function.Arguments[1]))
            return false;

        var shape = context.Limit is not null && context.OrderBy is { Count: 1 }
            ? ManagedIndexPatternShape.KnnLimit
            : ManagedIndexPatternShape.Knn;
        match = new ManagedIndexMethodPatternMatch(
            shape,
            function.Arguments[1],
            FiltersRows: false,
            ValidateArgument: static value =>
            {
                // Reproduce the scalar type error before the plan runs, so choosing the index can
                // never turn an error into a row set.
                if (value.Kind is not (SqlValueKind.Null or SqlValueKind.Blob))
                    throw new EmbeddedSqlException("vector_distance_l2 requires vector arguments");
            });
        return true;
    }
}
