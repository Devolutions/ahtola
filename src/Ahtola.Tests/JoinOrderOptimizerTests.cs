using System.Globalization;
using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Compilation.JoinOrdering;

namespace Ahtola.Tests;

/// <summary>
/// Coverage for the cost-based N-way join-order stage in
/// <c>EmbeddedDatabase.JoinOrderRewrites.cs</c> and its enumerator/cost model under
/// <c>Ahtola.Core/Compilation/JoinOrdering</c>.
/// <para>
/// Correctness assertions are differential against Microsoft.Data.Sqlite so a reorder that
/// changed an answer — row set, NULL semantics, or <c>SELECT *</c> column order — fails even if
/// the managed engine is self-consistent. Plan-shape assertions read the scan order and
/// build-side text the join cursor publishes to <c>EXPLAIN</c>, and the
/// <see cref="EmbeddedDatabase.JoinOrderDiagnostics"/> counters prove which enumerator ran (and
/// that a barrier shape declined instead of silently reordering).
/// </para>
/// </summary>
public sealed class JoinOrderOptimizerTests
{
    /// <summary>Chain of four INNER equijoins listed largest-first in the FROM clause.</summary>
    private const string ChainSetup =
        """
        CREATE TABLE t1(id INTEGER PRIMARY KEY, v TEXT);
        CREATE TABLE t2(id INTEGER PRIMARY KEY, v TEXT);
        CREATE TABLE t3(id INTEGER PRIMARY KEY, v TEXT);
        CREATE TABLE t4(id INTEGER PRIMARY KEY, v TEXT);
        """;

    private const string ChainQuery =
        """
        SELECT t1.v, t2.v, t3.v, t4.v
        FROM t1 JOIN t2 ON t1.id = t2.id JOIN t3 ON t2.id = t3.id JOIN t4 ON t3.id = t4.id
        ORDER BY t1.id;
        """;

    // ------------------------------------------------------------------------------------
    // Order selection.
    // ------------------------------------------------------------------------------------

    [Test]
    public void FourTableChainDrivesFromTheSmallestTable()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, ChainSetup + Fill("t1", 60) + Fill("t2", 40) + Fill("t3", 20) + Fill("t4", 2));
        Execute(connection, "ANALYZE;");

        JoinDescription(connection, "EXPLAIN " + ChainQuery)
            .Should().Contain("scan order: t4, t3, t2, t1");
        database.JoinOrderDiagnostics.SegmentsReordered.Should().Be(1);
        database.JoinOrderDiagnostics.DynamicProgrammingPlans.Should().Be(1);

        Rows(connection, ChainQuery).Should().HaveCount(2);
    }

    [Test]
    public void ReorderedChainMatchesSqliteAndAManuallyOrderedQuery()
    {
        var setup = ChainSetup + Fill("t1", 60) + Fill("t2", 40) + Fill("t3", 20) + Fill("t4", 2) + "ANALYZE;";
        AssertMatchesSqlite(setup, ChainQuery);

        // The same answer written in the physical order the optimizer chose.
        const string manual =
            """
            SELECT t1.v, t2.v, t3.v, t4.v
            FROM t4 JOIN t3 ON t3.id = t4.id JOIN t2 ON t2.id = t3.id JOIN t1 ON t1.id = t2.id
            ORDER BY t1.id;
            """;
        Flatten(RunManaged(setup, ChainQuery)).Should().Equal(Flatten(RunManaged(setup, manual)));
    }

    [Test]
    public void TwelveTableSegmentUsesTheSubsetDynamicProgram()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, ChainScript(12, descendingSizes: true));
        Execute(connection, "ANALYZE;");

        var query = ChainSelect(12);
        JoinDescription(connection, "EXPLAIN " + query).Should().Contain("scan order: t12,");
        database.JoinOrderDiagnostics.DynamicProgrammingPlans.Should().Be(1);
        database.JoinOrderDiagnostics.GreedyPlans.Should().Be(0);
        Rows(connection, query).Should().HaveCount(6);
    }

    [Test]
    public void SegmentAboveTheDynamicProgrammingCapFallsBackToGreedy()
    {
        JoinOrderEnumerator.DynamicProgrammingMemberCap.Should().Be(12);

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, ChainScript(13, descendingSizes: true));
        Execute(connection, "ANALYZE;");

        var query = ChainSelect(13);
        var description = JoinDescription(connection, "EXPLAIN " + query);
        database.JoinOrderDiagnostics.GreedyPlans.Should().Be(1);
        database.JoinOrderDiagnostics.DynamicProgrammingPlans.Should().Be(0);

        // Greedy still produces a genuine reorder, not a decline back to FROM order.
        description.Should().Contain("scan order: t13,");
        Rows(connection, query).Should().HaveCount(6);
    }

    [Test]
    public void WithoutAnalyzeTheFromOrderIsKeptVerbatim()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, ChainSetup + Fill("t1", 60) + Fill("t2", 40) + Fill("t3", 20) + Fill("t4", 2));

        JoinDescription(connection, "EXPLAIN " + ChainQuery)
            .Should().Contain("scan order: t1, t2, t3, t4");
        database.JoinOrderDiagnostics.SegmentsReordered.Should().Be(0);
        database.JoinOrderDiagnostics.DynamicProgrammingPlans.Should().Be(0);
    }

    [Test]
    public void PartialStatisticsStillKeepTheFromOrder()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, ChainSetup + Fill("t1", 60) + Fill("t2", 40) + Fill("t3", 20) + Fill("t4", 2));
        // ANALYZE on one table only: t2/t3/t4 have no sqlite_stat1 row, so the segment declines.
        Execute(connection, "ANALYZE t1;");

        JoinDescription(connection, "EXPLAIN " + ChainQuery)
            .Should().Contain("scan order: t1, t2, t3, t4");
        database.JoinOrderDiagnostics.SegmentsReordered.Should().Be(0);
    }

    [Test]
    public void TiedCostsAlwaysPickTheSameOrderAcrossRepeatedCompilations()
    {
        var setup = ChainSetup + Fill("t1", 12) + Fill("t2", 12) + Fill("t3", 12) + Fill("t4", 12) + "ANALYZE;";
        var descriptions = new HashSet<string>(StringComparer.Ordinal);
        for (var attempt = 0; attempt < 25; attempt++)
        {
            using var database = new EmbeddedDatabase();
            using var connection = database.Connect();
            Execute(connection, setup);
            descriptions.Add(JoinDescription(connection, "EXPLAIN " + ChainQuery));
        }

        descriptions.Should().HaveCount(1);
        // Every order costs the same, so the lexicographic tie-break keeps the FROM order.
        descriptions.Single().Should().Contain("scan order: t1, t2, t3, t4");
    }

    // ------------------------------------------------------------------------------------
    // Barriers.
    // ------------------------------------------------------------------------------------

    [Test]
    public void LeftJoinBarrierIsNeverCrossedAndKeepsNullExtension()
    {
        var setup =
            $"""
            CREATE TABLE big(id INTEGER PRIMARY KEY, v TEXT);
            CREATE TABLE small(id INTEGER PRIMARY KEY, v TEXT);
            CREATE TABLE opt(id INTEGER PRIMARY KEY, v TEXT);
            CREATE TABLE tiny(id INTEGER PRIMARY KEY, v TEXT);
            {Series("big", 60, "b")}
            {Series("small", 6, "s")}
            INSERT INTO opt VALUES (1, 'o1');
            INSERT INTO tiny VALUES (1, 'y1'), (2, 'y2');
            ANALYZE;
            """;
        const string query =
            """
            SELECT big.v, small.v, opt.v, tiny.v
            FROM big JOIN small ON big.id = small.id
                     LEFT JOIN opt ON opt.id = small.id
                     JOIN tiny ON tiny.id = big.id
            ORDER BY big.id;
            """;

        AssertMatchesSqlite(setup, query);

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, setup);
        var description = JoinDescription(connection, "EXPLAIN " + query);
        var order = ScanOrder(description);

        // big/small/opt form one frozen LEFT-join unit: their three positions stay contiguous,
        // and `opt` never moves ahead of the tables it is outer-joined onto.
        var positions = new[] { order.IndexOf("big"), order.IndexOf("small"), order.IndexOf("opt") };
        Array.Sort(positions);
        positions[2].Should().Be(positions[0] + 2);
        order.IndexOf("opt").Should().BeGreaterThan(order.IndexOf("small"));

        // The unmatched right rows are still NULL-extended, i.e. the barrier kept its semantics.
        Rows(connection, query).Count(row => row[2].Kind == SqlValueKind.Null).Should().Be(1);
    }

    [Test]
    public void NestedInnerSegmentInsideABarrierIsStillOptimized()
    {
        var setup =
            $"""
            CREATE TABLE x(id INTEGER PRIMARY KEY, v TEXT);
            CREATE TABLE y(id INTEGER PRIMARY KEY, v TEXT);
            CREATE TABLE z(id INTEGER PRIMARY KEY, v TEXT);
            CREATE TABLE w(id INTEGER PRIMARY KEY, v TEXT);
            {Series("x", 50, "x")}
            {Series("y", 20, "y")}
            INSERT INTO z VALUES (1, 'z1'), (2, 'z2');
            INSERT INTO w VALUES (1, 'w1');
            ANALYZE;
            """;
        const string query =
            """
            SELECT x.v, y.v, z.v, w.v
            FROM x JOIN y ON x.id = y.id JOIN z ON y.id = z.id LEFT JOIN w ON w.id = z.id
            ORDER BY x.id;
            """;

        AssertMatchesSqlite(setup, query);

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, setup);
        var order = ScanOrder(JoinDescription(connection, "EXPLAIN " + query));

        // The x/y/z segment is the LEFT join's frozen left input, yet it is still reordered.
        order[0].Should().Be("z");
        order[3].Should().Be("w");
        database.JoinOrderDiagnostics.SegmentsReordered.Should().Be(1);
    }

    [Test]
    public void NaturalJoinPairIsFrozen()
    {
        var setup =
            $"""
            CREATE TABLE n1(id INTEGER PRIMARY KEY, tag TEXT);
            CREATE TABLE n2(id INTEGER PRIMARY KEY, note TEXT);
            CREATE TABLE n3(id INTEGER PRIMARY KEY, extra TEXT);
            {Series("n1", 40, "a")}
            {Series("n2", 30, "b")}
            INSERT INTO n3 VALUES (1, 'c1');
            ANALYZE;
            """;
        const string query =
            """
            SELECT n1.tag, n2.note, n3.extra
            FROM n1 NATURAL JOIN n2 JOIN n3 ON n3.id = n1.id
            ORDER BY n1.id;
            """;

        AssertMatchesSqlite(setup, query);

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, setup);
        var order = ScanOrder(JoinDescription(connection, "EXPLAIN " + query));
        order.IndexOf("n1").Should().Be(order.IndexOf("n2") - 1);
    }

    [Test]
    public void UsingJoinPairIsFrozen()
    {
        var setup =
            $"""
            CREATE TABLE u1(id INTEGER PRIMARY KEY, tag TEXT);
            CREATE TABLE u2(id INTEGER PRIMARY KEY, note TEXT);
            CREATE TABLE u3(k INTEGER PRIMARY KEY, extra TEXT);
            {Series("u1", 40, "a")}
            {Series("u2", 30, "b")}
            INSERT INTO u3 VALUES (1, 'c1');
            ANALYZE;
            """;
        const string query =
            """
            SELECT u1.tag, u2.note, u3.extra
            FROM u1 JOIN u2 USING (id) JOIN u3 ON u3.k = u1.id
            ORDER BY u1.id;
            """;

        AssertMatchesSqlite(setup, query);

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, setup);
        var order = ScanOrder(JoinDescription(connection, "EXPLAIN " + query));
        order.IndexOf("u1").Should().Be(order.IndexOf("u2") - 1);
    }

    [Test]
    public void SemiJoinShapesDeclineTheReorderEntirely()
    {
        var setup =
            $"""
            CREATE TABLE parents(id INTEGER PRIMARY KEY, v TEXT);
            CREATE TABLE kids(id INTEGER PRIMARY KEY, parent INTEGER);
            {Series("parents", 30, "p")}
            {Pairs("kids", 5)}
            ANALYZE;
            """;
        const string query =
            """
            SELECT p.v FROM parents AS p
            WHERE EXISTS (SELECT 1 FROM kids AS k WHERE k.parent = p.id)
            ORDER BY p.id;
            """;

        AssertMatchesSqlite(setup, query);

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, setup);
        Rows(connection, query).Should().HaveCount(5);
        database.JoinOrderDiagnostics.SegmentsReordered.Should().Be(0);
    }

    // ------------------------------------------------------------------------------------
    // Cross joins, self joins, NULL keys.
    // ------------------------------------------------------------------------------------

    [Test]
    public void CommaCrossJoinSegmentReordersAndKeepsTheSameRowMultiset()
    {
        var setup =
            $"""
            CREATE TABLE c1(a INTEGER);
            CREATE TABLE c2(b INTEGER);
            CREATE TABLE c3(c INTEGER);
            {Singles("c1", 20)}
            {Singles("c2", 4)}
            {Singles("c3", 2)}
            ANALYZE;
            """;
        const string query = "SELECT a, b, c FROM c1, c2, c3 ORDER BY a, b, c;";

        AssertMatchesSqlite(setup, query);

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, setup);
        Rows(connection, query).Should().HaveCount(160);
        var order = ScanOrder(JoinDescription(connection, "EXPLAIN " + query));
        // No equality key exists anywhere, so every step is a nested-loop cross. The optimizer
        // still moves the 20-row table out of the driving position and onto the last step.
        order.Should().NotEqual(["c1", "c2", "c3"]);
        order[2].Should().Be("c1");
        database.JoinOrderDiagnostics.SegmentsReordered.Should().BeGreaterThan(0);
    }

    [Test]
    public void SelfJoinAliasesStayDistinctMembers()
    {
        var setup =
            $"""
            CREATE TABLE nodes(id INTEGER PRIMARY KEY, parent INTEGER, v TEXT);
            {Nodes(30)}
            CREATE TABLE roots(id INTEGER PRIMARY KEY);
            INSERT INTO roots VALUES (2);
            ANALYZE;
            """;
        const string query =
            """
            SELECT child.v, parent.v, roots.id
            FROM nodes AS child JOIN nodes AS parent ON child.parent = parent.id
                 JOIN roots ON roots.id = child.id
            ORDER BY child.id;
            """;

        AssertMatchesSqlite(setup, query);

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, setup);
        Rows(connection, query).Should().HaveCount(1);
        // Two members resolve to the same base table, so the scan order names it twice.
        ScanOrder(JoinDescription(connection, "EXPLAIN " + query)).Should().HaveCount(3);
    }

    [Test]
    public void NullJoinKeysStillNeverMatchAfterReordering()
    {
        var setup =
            $"""
            CREATE TABLE l(id INTEGER PRIMARY KEY, k INTEGER);
            CREATE TABLE m(k INTEGER, v TEXT);
            CREATE TABLE s(v2 TEXT, k INTEGER);
            {NullableKeys("l", 45)}
            {Series("m", 30, "m")}
            INSERT INTO m VALUES (NULL, 'mnull');
            INSERT INTO s VALUES ('s1', 1), ('s2', NULL), ('s3', 2);
            ANALYZE;
            """;
        const string query =
            """
            SELECT l.id, m.v, s.v2
            FROM l JOIN m ON l.k = m.k JOIN s ON s.k = m.k
            ORDER BY l.id, m.v, s.v2;
            """;

        AssertMatchesSqlite(setup, query);
    }

    // ------------------------------------------------------------------------------------
    // Projection order.
    // ------------------------------------------------------------------------------------

    [Test]
    public void StarProjectionKeepsFromClauseColumnOrderAfterReorder()
    {
        var setup = ProjectionSetup();
        const string query =
            "SELECT * FROM wide JOIN mid ON wide.id = mid.id JOIN narrow ON narrow.id = mid.id ORDER BY wide.id;";

        AssertMatchesSqlite(setup, query);

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, setup);
        ColumnNames(connection, query).Should().Equal("id", "w1", "w2", "id", "m1", "id", "n1");

        var reordered = ScanOrder(JoinDescription(connection, "EXPLAIN " + query));
        reordered[0].Should().Be("narrow");
        database.JoinOrderDiagnostics.SegmentsReordered.Should().BeGreaterThan(0);

        var row = Rows(connection, query).First();
        row[0].Should().Be(SqlValue.Integer(1));
        row[1].Should().Be(SqlValue.Text("w1-1"));
        row[2].Should().Be(SqlValue.Text("w2-1"));
        row[4].Should().Be(SqlValue.Text("m1-1"));
        row[6].Should().Be(SqlValue.Text("n1-1"));
    }

    [Test]
    public void QualifiedStarProjectionsKeepFromClauseColumnOrderAfterReorder()
    {
        var setup = ProjectionSetup();
        const string query =
            """
            SELECT wide.*, narrow.*, mid.m1
            FROM wide JOIN mid ON wide.id = mid.id JOIN narrow ON narrow.id = mid.id
            ORDER BY wide.id;
            """;

        AssertMatchesSqlite(setup, query);
    }

    [Test]
    public void UnqualifiedProjectionsResolveToTheSameColumnsAfterReorder()
    {
        var setup = ProjectionSetup();
        const string query =
            """
            SELECT w1, m1, n1
            FROM wide JOIN mid ON wide.id = mid.id JOIN narrow ON narrow.id = mid.id
            ORDER BY w1;
            """;

        AssertMatchesSqlite(setup, query);
    }

    // ------------------------------------------------------------------------------------
    // Interaction with the rest of the pipeline.
    // ------------------------------------------------------------------------------------

    [Test]
    public void AggregateOverAReorderedJoinMatchesSqlite()
    {
        var setup = ChainSetup + Fill("t1", 60) + Fill("t2", 40) + Fill("t3", 20) + Fill("t4", 2) + "ANALYZE;";
        AssertMatchesSqlite(setup, "SELECT COUNT(*), MIN(t1.v), MAX(t4.v) FROM t1 JOIN t2 ON t1.id = t2.id JOIN t3 ON t2.id = t3.id JOIN t4 ON t3.id = t4.id;");
        AssertMatchesSqlite(
            setup,
            """
            SELECT t4.v, COUNT(*)
            FROM t1 JOIN t2 ON t1.id = t2.id JOIN t3 ON t2.id = t3.id JOIN t4 ON t3.id = t4.id
            GROUP BY t4.v
            ORDER BY t4.v;
            """);
    }

    [Test]
    public void OrderByLimitOffsetAndDistinctOverAReorderedJoinMatchSqlite()
    {
        var setup = ChainSetup + Fill("t1", 60) + Fill("t2", 40) + Fill("t3", 20) + Fill("t4", 6) + "ANALYZE;";
        AssertMatchesSqlite(
            setup,
            """
            SELECT t1.v, t4.v
            FROM t1 JOIN t2 ON t1.id = t2.id JOIN t3 ON t2.id = t3.id JOIN t4 ON t3.id = t4.id
            ORDER BY t1.id DESC LIMIT 3 OFFSET 1;
            """);
        AssertMatchesSqlite(
            setup,
            """
            SELECT DISTINCT t3.v
            FROM t1 JOIN t2 ON t1.id = t2.id JOIN t3 ON t2.id = t3.id JOIN t4 ON t3.id = t4.id
            ORDER BY t3.v;
            """);
    }

    /// <summary>
    /// Reordering permutes the physical value slots of a joined row, and
    /// <c>RemapJoinOrderOutputColumns</c> re-points the projection metadata onto that permuted
    /// layout while keeping FROM list order. The DISTINCT equality's per-column collations are
    /// derived from the same metadata, so they have to be resolved against the <em>reordered</em>
    /// FROM tree: resolving a remapped index against the original tree lands on a different
    /// table's column and reports its collation instead, silently downgrading a NOCASE/RTRIM
    /// column to BINARY and emitting rows that are duplicates under the declared collation.
    /// </summary>
    [Test]
    public void DistinctStarOverAReorderedJoinKeepsDeclaredColumnCollations()
    {
        // 'Dup' and 'DUP' are one value under NOCASE, so `SELECT DISTINCT *` must collapse them.
        var setup = CollationSetup("NOCASE", "Dup", "DUP");
        const string query =
            "SELECT DISTINCT * FROM big, mid, small WHERE big.k = mid.k AND mid.k = small.k";

        // Which of the two equal spellings survives is plan-dependent in both engines, so the
        // differential assertion counts the distinct rows rather than reading them back.
        AssertMatchesSqlite(setup, $"SELECT COUNT(*) FROM ({query});");

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, setup);

        // `big` is by far the largest member, so it is moved off the driving position and its
        // columns end up at physical slots other than their FROM-order ones.
        ScanOrder(JoinDescription(connection, "EXPLAIN " + query))[0].Should().NotBe("big");
        database.JoinOrderDiagnostics.SegmentsReordered.Should().BeGreaterThan(0);

        var rows = Rows(connection, query);
        rows.Should().HaveCount(2);
        rows.Select(row => row[0].AsInteger()).Order().Should().Equal(0, 1);
        foreach (var row in rows)
        {
            row[1].AsText().Should().BeOneOf("Dup", "DUP");
            // FROM-order projection is unaffected by the reorder.
            row[3].AsText().Should().Be("m");
            row[5].AsText().Should().Be("s");
        }

        ColumnNames(connection, query).Should().Equal("k", "c", "k", "m", "k", "s");
    }

    [Test]
    public void DistinctStarOverAReorderedJoinKeepsRtrimCollation()
    {
        // 'Pad' and 'Pad  ' are one value under RTRIM.
        var setup = CollationSetup("RTRIM", "Pad", "Pad  ");
        const string query =
            "SELECT DISTINCT * FROM big, mid, small WHERE big.k = mid.k AND mid.k = small.k";

        AssertMatchesSqlite(setup, $"SELECT COUNT(*) FROM ({query});");

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, setup);
        Rows(connection, query).Should().HaveCount(2);
        database.JoinOrderDiagnostics.SegmentsReordered.Should().BeGreaterThan(0);
    }

    [Test]
    public void DistinctWithExplicitCollateOverAReorderedJoinMatchesSqlite()
    {
        // An explicit COLLATE is resolved from the expression, not from a physical slot, so it
        // must keep working — including when it overrides a column's declared collation.
        var setup = CollationSetup("BINARY", "Dup", "DUP");

        AssertMatchesSqlite(
            setup,
            """
            SELECT COUNT(*) FROM (
                SELECT DISTINCT big.c COLLATE NOCASE, mid.m, small.s
                FROM big, mid, small WHERE big.k = mid.k AND mid.k = small.k);
            """);

        // Without the override the two spellings are distinct under the declared BINARY.
        AssertMatchesSqlite(
            setup,
            """
            SELECT COUNT(*) FROM (
                SELECT DISTINCT * FROM big, mid, small WHERE big.k = mid.k AND mid.k = small.k);
            """);

        // The declared collation of a NOCASE column is not silently strengthened either: an
        // explicit BINARY override still separates the spellings.
        var nocase = CollationSetup("NOCASE", "Dup", "DUP");
        AssertMatchesSqlite(
            nocase,
            """
            SELECT COUNT(*) FROM (
                SELECT DISTINCT big.c COLLATE BINARY, mid.m, small.s
                FROM big, mid, small WHERE big.k = mid.k AND mid.k = small.k);
            """);
    }

    /// <summary>
    /// Three unrelated-width tables whose sizes force the cost model to move <c>big</c> — the
    /// table carrying the collated column — out of its FROM position.
    /// </summary>
    private static string CollationSetup(string collation, string first, string second)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"CREATE TABLE big(k INTEGER, c TEXT COLLATE {collation});");
        builder.Append("CREATE TABLE mid(k INTEGER, m TEXT);");
        builder.Append("CREATE TABLE small(k INTEGER, s TEXT);");
        for (var index = 0; index < 40; index++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"INSERT INTO big VALUES ({index % 2}, '{first}');");
            builder.Append(CultureInfo.InvariantCulture, $"INSERT INTO big VALUES ({index % 2}, '{second}');");
        }

        for (var index = 0; index < 20; index++)
            builder.Append(CultureInfo.InvariantCulture, $"INSERT INTO mid VALUES ({index % 2}, 'm');");

        builder.Append("INSERT INTO small VALUES (0,'s'),(1,'s');");
        builder.Append("ANALYZE;");
        return builder.ToString();
    }

    [Test]
    public void WindowFunctionOverAJoinStillMatchesSqlite()
    {
        var setup = ChainSetup + Fill("t1", 30) + Fill("t2", 20) + Fill("t3", 10) + Fill("t4", 4) + "ANALYZE;";
        AssertMatchesSqlite(
            setup,
            """
            SELECT t1.v, ROW_NUMBER() OVER (ORDER BY t1.id) AS rn
            FROM t1 JOIN t2 ON t1.id = t2.id JOIN t3 ON t2.id = t3.id JOIN t4 ON t3.id = t4.id
            ORDER BY rn;
            """);
    }

    [Test]
    public void LocalWhereFiltersArePushedAndResultsAreUnchanged()
    {
        var setup = ChainSetup + Fill("t1", 60) + Fill("t2", 40) + Fill("t3", 20) + Fill("t4", 8) + "ANALYZE;";
        const string query =
            """
            SELECT t1.v, t4.v
            FROM t1 JOIN t2 ON t1.id = t2.id JOIN t3 ON t2.id = t3.id JOIN t4 ON t3.id = t4.id
            WHERE t1.id > 2 AND t4.v = 't4-5'
            ORDER BY t1.id;
            """;

        AssertMatchesSqlite(setup, query);

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, setup);
        Rows(connection, query).Should().HaveCount(1);
        database.JoinOrderDiagnostics.PushedWhereTerms.Should().BeGreaterThan(0);
    }

    [Test]
    public void ParameterizedJoinsStillBindAfterReordering()
    {
        var setup = ChainSetup + Fill("t1", 60) + Fill("t2", 40) + Fill("t3", 20) + Fill("t4", 8) + "ANALYZE;";
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, setup);

        using var statement = connection.Prepare(
            """
            SELECT t1.v FROM t1 JOIN t2 ON t1.id = t2.id JOIN t3 ON t2.id = t3.id JOIN t4 ON t3.id = t4.id
            WHERE t1.id = ?
            ORDER BY t1.id;
            """);
        statement.Bind(1, SqlValue.Integer(3));
        var values = new List<string>();
        while (statement.Step() == StatementStepResult.Row)
            values.Add(statement.GetValue(0).AsText());
        values.Should().Equal("t1-3");
    }

    // ------------------------------------------------------------------------------------
    // Enumerator and cost model, exercised directly.
    // ------------------------------------------------------------------------------------

    [Test]
    public void DynamicProgrammingFindsTheBruteForceOptimum()
    {
        var random = new Random(20260823);
        for (var members = 2; members <= 7; members++)
        {
            for (var trial = 0; trial < 12; trial++)
            {
                var segment = RandomSegment(random, members);
                var plan = JoinOrderEnumerator.Compute(segment);
                plan.Should().NotBeNull();

                var bruteForce = double.PositiveInfinity;
                foreach (var permutation in Permutations(Enumerable.Range(0, members).ToArray()))
                {
                    var cost = JoinOrderEnumerator.EvaluateOrder(segment, permutation).Cost;
                    if (cost < bruteForce)
                        bruteForce = cost;
                }

                plan!.Cost.Should().BeApproximately(bruteForce, 1e-6);
                plan.MemberOrder.Should().BeEquivalentTo(Enumerable.Range(0, members));
            }
        }
    }

    [Test]
    public void GreedyFallbackProducesAValidOrderNoWorseThanTheFromOrder()
    {
        var random = new Random(4711);
        for (var members = 13; members <= 16; members++)
        {
            var segment = RandomSegment(random, members);
            var plan = JoinOrderEnumerator.Compute(segment);
            plan.Should().NotBeNull();
            plan!.UsedDynamicProgramming.Should().BeFalse();
            plan.MemberOrder.Should().BeEquivalentTo(Enumerable.Range(0, members));

            var identity = JoinOrderEnumerator.EvaluateOrder(
                segment,
                Enumerable.Range(0, members).ToArray());
            plan.Cost.Should().BeLessThanOrEqualTo(identity.Cost + 1e-9);
        }
    }

    [Test]
    public void EqualCostMembersKeepTheOriginalOrder()
    {
        var members = Enumerable.Range(0, 6)
            .Select(index => new JoinSegmentMember(index, RowCount: 100.0, ColumnWidth: 1))
            .ToArray();
        var plan = JoinOrderEnumerator.Compute(new JoinSegment(members, []));

        plan.Should().NotBeNull();
        plan!.MemberOrder.Should().Equal(0, 1, 2, 3, 4, 5);
        plan.IsIdentityOrder.Should().BeTrue();
    }

    [Test]
    public void SegmentsOutsideTheEnumerableRangeAreDeclined()
    {
        JoinOrderEnumerator.Compute(
                new JoinSegment([new JoinSegmentMember(0, 10.0, 1)], []))
            .Should().BeNull();

        var tooMany = Enumerable.Range(0, JoinOrderEnumerator.MaximumMembers + 1)
            .Select(index => new JoinSegmentMember(index, 10.0, 1))
            .ToArray();
        JoinOrderEnumerator.Compute(new JoinSegment(tooMany, [])).Should().BeNull();
    }

    [Test]
    public void ScanCostMatchesThePortedTursoFormula()
    {
        // cost.rs:120-135 estimate_scan_cost with rows_per_table_page = 50, cpu_cost_per_row = 0.003.
        JoinCostModel.EstimateFullScanCost(500.0, scanCount: 1.0)
            .Should().BeApproximately((500.0 / 50.0) + (500.0 * 0.003), 1e-12);

        // Fewer rows than one page still costs one page of IO.
        JoinCostModel.EstimateFullScanCost(10.0, scanCount: 1.0)
            .Should().BeApproximately(1.0 + (10.0 * 0.003), 1e-12);

        // Repeated scans pay cache_reuse_factor = 0.2 for every scan after the first.
        JoinCostModel.EstimateFullScanCost(500.0, scanCount: 3.0)
            .Should().BeApproximately(10.0 + (2.0 * 10.0 * 0.2) + (3.0 * 500.0 * 0.003), 1e-12);
    }

    [Test]
    public void HashJoinCostMatchesThePortedTursoFormula()
    {
        // access_method.rs:1200-1235 estimate_hash_join_cost without the spill term:
        // build * (hash_cpu_cost + hash_insert_cost) + probe * (hash_cpu_cost + hash_lookup_cost).
        JoinCostModel.EstimateHashJoinCost(100.0, 250.0, probeMultiplier: 1.0)
            .Should().BeApproximately((100.0 * 0.003) + (250.0 * 0.004), 1e-12);
        JoinCostModel.EstimateHashJoinCost(100.0, 250.0, probeMultiplier: 2.0)
            .Should().BeApproximately((100.0 * 0.003) + (250.0 * 0.004 * 2.0), 1e-12);
    }

    [Test]
    public void IndexSeekCostMatchesThePinnedTursoFormula()
    {
        // cost.rs:171-236. Index rows/page = 50 * (3 columns + rowid) / (1 key + rowid) = 100.
        // depth=2, four seeks=8; two rows/seek pays one cached leaf page sequence (1.6);
        // non-covering table lookups=0.16; CPU=4*0.01 + 8*0.003; index bonus=0.5.
        JoinCostModel.EstimateIndexSeekCost(
                baseRowCount: 5000.0,
                indexColumnCount: 1,
                tableColumnCount: 3,
                hasRowIdAlias: false,
                covering: false,
                inputCardinality: 4.0,
                rowsPerSeek: 2.0)
            .Should().BeApproximately(8.0 + 1.6 + 0.16 + 0.064 - 0.5, 1e-12);
    }

    [Test]
    public void ManagedIndexViewBuildCostAccountsForTheOneTimeSort()
    {
        const double rows = 20_000.0;
        JoinCostModel.EstimateManagedIndexViewBuildCost(rows).Should().BeApproximately(
            rows * Math.Log2(rows) * JoinCostParams.SortCpuPerRow
                + rows * JoinCostParams.CpuCostPerRow,
            1e-9);
    }

    [Test]
    public void UniqueFullKeyCandidateCompetesAsAnExecutableSeekShape()
    {
        var candidate = new JoinIndexCandidate(
            "right_k",
            [new JoinIndexColumn(0, "BINARY", Descending: false)],
            [12.0],
            Unique: true,
            Covering: false,
            TableColumnCount: 2,
            HasRowIdAlias: false,
            Forced: true);
        var segment = new JoinSegment(
        [
            new JoinSegmentMember(0, 3.0, 1),
            new JoinSegmentMember(1, 500.0, 2, [candidate]),
        ],
        [
            new JoinPredicateTerm(
                TableMask: 0b11,
                IsEquality: true,
                EqualityLeftMask: 0b01,
                EqualityRightMask: 0b10,
                EqualityLeftMatchRows: 1.0,
                EqualityRightMatchRows: 12.0,
                Selectivity: JoinCostParams.SelectivityEqualityIndexed,
                EqualityLeftColumnOrdinal: 0,
                EqualityRightColumnOrdinal: 0,
                EqualityCollation: "BINARY",
                EqualitySeekCollation: "BINARY"),
        ]);

        var plan = JoinOrderEnumerator.EvaluateOrder(segment, [0, 1]);
        plan.StepShapes[1].Should().Be(JoinStepShape.IndexSeekRight);
        plan.IndexAccesses[1].Should().NotBeNull();
        plan.IndexAccesses[1]!.RowsPerSeek.Should().Be(1.0);
    }

    [Test]
    public void UnhintedSeekDoesNotHideManagedIndexReconstructionCost()
    {
        var candidate = new JoinIndexCandidate(
            "right_k",
            [new JoinIndexColumn(0, "BINARY", Descending: false)],
            [1.0],
            Unique: true,
            Covering: false,
            TableColumnCount: 2,
            HasRowIdAlias: false);
        var segment = new JoinSegment(
        [
            new JoinSegmentMember(0, 4.0, 1),
            new JoinSegmentMember(1, 20_000.0, 2, [candidate]),
        ],
        [
            new JoinPredicateTerm(
                TableMask: 0b11,
                IsEquality: true,
                EqualityLeftMask: 0b01,
                EqualityRightMask: 0b10,
                EqualityLeftMatchRows: 1.0,
                EqualityRightMatchRows: 1.0,
                Selectivity: JoinCostParams.SelectivityEqualityIndexed,
                EqualityLeftColumnOrdinal: 0,
                EqualityRightColumnOrdinal: 0,
                EqualityCollation: "BINARY"),
        ]);

        JoinOrderEnumerator.EvaluateOrder(segment, [0, 1]).StepShapes[1]
            .Should().NotBe(JoinStepShape.IndexSeekRight);
    }

    [Test]
    public void ForcedIndexWaitsForItsOuterBindingMember()
    {
        var forced = new JoinIndexCandidate(
            "b_k",
            [new JoinIndexColumn(0, "BINARY", Descending: false)],
            [1.0],
            Unique: false,
            Covering: false,
            TableColumnCount: 2,
            HasRowIdAlias: false,
            Forced: true);
        var segment = new JoinSegment(
        [
            new JoinSegmentMember(0, 1_000.0, 2),
            new JoinSegmentMember(1, 1_000.0, 2, [forced]),
            new JoinSegmentMember(2, 1.0, 1),
        ],
        [
            new JoinPredicateTerm(
                0b011,
                true,
                0b001,
                0b010,
                1.0,
                1.0,
                JoinCostParams.SelectivityEqualityIndexed,
                EqualityLeftColumnOrdinal: 0,
                EqualityRightColumnOrdinal: 0,
                EqualityCollation: "BINARY",
                EqualitySeekCollation: "BINARY"),
            new JoinPredicateTerm(
                0b110,
                true,
                0b100,
                0b010,
                1.0,
                1.0,
                JoinCostParams.SelectivityEqualityIndexed,
                EqualityLeftColumnOrdinal: 0,
                EqualityRightColumnOrdinal: 1,
                EqualityCollation: "BINARY",
                EqualitySeekCollation: "BINARY"),
        ]);

        var plan = JoinOrderEnumerator.Compute(segment);
        plan.Should().NotBeNull();
        var forcedStep = Array.IndexOf(plan!.MemberOrder, 1);
        forcedStep.Should().BeGreaterThan(Array.IndexOf(plan.MemberOrder, 0));
        plan.StepShapes[forcedStep].Should().Be(JoinStepShape.IndexSeekRight);
    }

    [Test]
    public void HashBuildSideIsChosenByCostNotByPosition()
    {
        // A small accumulated left against a large right prefers building the left, because the
        // build side is the one buffered in memory.
        var buildLeft = JoinCostModel.EstimateStepCost(JoinStepShape.HashBuildLeft, 3.0, 4000.0, 3.0);
        var buildRight = JoinCostModel.EstimateStepCost(JoinStepShape.HashBuildRight, 3.0, 4000.0, 3.0);
        buildLeft.Should().BeLessThan(buildRight);

        // The reverse holds when the right input is the small one.
        JoinCostModel.EstimateStepCost(JoinStepShape.HashBuildRight, 4000.0, 3.0, 4000.0)
            .Should().BeLessThan(JoinCostModel.EstimateStepCost(JoinStepShape.HashBuildLeft, 4000.0, 3.0, 4000.0));

        // A cross step has no key, so it costs the full comparison product.
        JoinCostModel.EstimateStepCost(JoinStepShape.NestedLoop, 1000.0, 1000.0, 1000.0)
            .Should().BeGreaterThan(JoinCostModel.EstimateStepCost(JoinStepShape.HashBuildRight, 1000.0, 1000.0, 1000.0));
    }

    [Test]
    public void RowsAfterStepClampsToAtLeastOneRow()
    {
        JoinCostModel.RowsAfterStep(10.0, 2.0, 1.0).Should().BeApproximately(20.0, 1e-12);
        JoinCostModel.RowsAfterStep(10.0, 0.001, 0.1).Should().Be(1.0);
    }

    // ------------------------------------------------------------------------------------
    // Helpers.
    // ------------------------------------------------------------------------------------

    private static JoinSegment RandomSegment(Random random, int members)
    {
        var segmentMembers = new JoinSegmentMember[members];
        for (var index = 0; index < members; index++)
        {
            segmentMembers[index] = new JoinSegmentMember(
                index,
                random.Next(1, 5000),
                ColumnWidth: 2,
                [
                    new JoinIndexCandidate(
                        $"i{index}_k",
                        [new JoinIndexColumn(0, "BINARY", Descending: false)],
                        [random.Next(1, 8)],
                        Unique: false,
                        Covering: false,
                        TableColumnCount: 2,
                        HasRowIdAlias: false),
                ]);
        }

        // A connected chain plus one extra random edge, so both equality and residual terms occur.
        var terms = new List<JoinPredicateTerm>();
        for (var index = 1; index < members; index++)
        {
            var left = 1UL << (index - 1);
            var right = 1UL << index;
            terms.Add(new JoinPredicateTerm(
                left | right,
                IsEquality: true,
                left,
                right,
                EqualityLeftMatchRows: random.Next(1, 6),
                EqualityRightMatchRows: random.Next(1, 6),
                Selectivity: JoinCostParams.SelectivityEqualityUnindexed,
                EqualityLeftColumnOrdinal: 0,
                EqualityRightColumnOrdinal: 0,
                EqualityCollation: "BINARY"));
        }

        if (members >= 3)
        {
            var a = random.Next(members);
            var b = random.Next(members);
            if (a != b)
            {
                terms.Add(new JoinPredicateTerm(
                    (1UL << a) | (1UL << b),
                    IsEquality: false,
                    EqualityLeftMask: 0,
                    EqualityRightMask: 0,
                    EqualityLeftMatchRows: 0,
                    EqualityRightMatchRows: 0,
                    JoinCostParams.SelectivityRange));
            }
        }

        return new JoinSegment(segmentMembers, terms);
    }

    private static IEnumerable<int[]> Permutations(int[] values)
    {
        if (values.Length <= 1)
        {
            yield return values;
            yield break;
        }

        for (var index = 0; index < values.Length; index++)
        {
            var head = values[index];
            var rest = values.Where((_, position) => position != index).ToArray();
            foreach (var tail in Permutations(rest))
                yield return new[] { head }.Concat(tail).ToArray();
        }
    }

    private static string ProjectionSetup() =>
        $"""
        CREATE TABLE wide(id INTEGER PRIMARY KEY, w1 TEXT, w2 TEXT);
        CREATE TABLE mid(id INTEGER PRIMARY KEY, m1 TEXT);
        CREATE TABLE narrow(id INTEGER PRIMARY KEY, n1 TEXT);
        {Wide(50)}
        {Series2("mid", 20, "m1")}
        {Series2("narrow", 2, "n1")}
        ANALYZE;
        """;

    private static string Fill(string table, int rows)
        => Series(table, rows, table + "-") + "\n";

    /// <summary>
    /// Emits an explicit VALUES list rather than <c>generate_series</c>, which the SQLite build
    /// the differential assertions run against does not ship.
    /// </summary>
    private static string Series(string table, int rows, string prefix)
    {
        var values = Enumerable.Range(1, rows)
            .Select(value => string.Create(CultureInfo.InvariantCulture, $"({value}, '{prefix}{value}')"));
        return $"INSERT INTO {table} VALUES {string.Join(", ", values)};";
    }

    private static string Series2(string table, int rows, string prefix)
    {
        var values = Enumerable.Range(1, rows)
            .Select(value => string.Create(CultureInfo.InvariantCulture, $"({value}, '{prefix}-{value}')"));
        return $"INSERT INTO {table} VALUES {string.Join(", ", values)};";
    }

    private static string Singles(string table, int rows)
    {
        var values = Enumerable.Range(1, rows)
            .Select(value => string.Create(CultureInfo.InvariantCulture, $"({value})"));
        return $"INSERT INTO {table} VALUES {string.Join(", ", values)};";
    }

    private static string Pairs(string table, int rows)
    {
        var values = Enumerable.Range(1, rows)
            .Select(value => string.Create(CultureInfo.InvariantCulture, $"({value}, {value})"));
        return $"INSERT INTO {table} VALUES {string.Join(", ", values)};";
    }

    private static string Nodes(int rows)
    {
        var values = Enumerable.Range(1, rows)
            .Select(value => string.Create(CultureInfo.InvariantCulture, $"({value}, {value - 1}, 'n{value}')"));
        return $"INSERT INTO nodes VALUES {string.Join(", ", values)};";
    }

    private static string NullableKeys(string table, int rows)
    {
        var values = Enumerable.Range(1, rows)
            .Select(value => value % 3 == 0
                ? string.Create(CultureInfo.InvariantCulture, $"({value}, NULL)")
                : string.Create(CultureInfo.InvariantCulture, $"({value}, {value})"));
        return $"INSERT INTO {table} VALUES {string.Join(", ", values)};";
    }

    private static string Wide(int rows)
    {
        var values = Enumerable.Range(1, rows)
            .Select(value => string.Create(
                CultureInfo.InvariantCulture,
                $"({value}, 'w1-{value}', 'w2-{value}')"));
        return $"INSERT INTO wide VALUES {string.Join(", ", values)};";
    }

    private static string ChainScript(int tables, bool descendingSizes)
    {
        var script = new System.Text.StringBuilder();
        for (var index = 1; index <= tables; index++)
            script.Append(CultureInfo.InvariantCulture, $"CREATE TABLE t{index}(id INTEGER PRIMARY KEY, v TEXT);\n");
        for (var index = 1; index <= tables; index++)
        {
            var rows = descendingSizes ? (tables - index + 1) * 6 : index * 6;
            script.Append(Fill($"t{index}", rows));
        }

        return script.ToString();
    }

    private static string ChainSelect(int tables)
    {
        var script = new System.Text.StringBuilder("SELECT t1.v FROM t1");
        for (var index = 2; index <= tables; index++)
            script.Append(CultureInfo.InvariantCulture, $" JOIN t{index} ON t{index - 1}.id = t{index}.id");
        script.Append(" ORDER BY t1.id;");
        return script.ToString();
    }

    private static string JoinDescription(EmbeddedConnection connection, string explainSql)
        => Rows(connection, explainSql)
            .Where(row => row[1].AsText() == "OpenJoinCursor")
            .Select(row => row[5].AsText())
            .Last();

    private static List<string> ScanOrder(string description)
    {
        var start = description.IndexOf("scan order: ", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        start += "scan order: ".Length;
        var end = description.IndexOf(']', start);
        return description[start..end].Split(", ").ToList();
    }

    private static List<SqlValue[]> Rows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            rows.Add(Enumerable.Range(0, statement.GetColumnCount())
                .Select(statement.GetValue)
                .ToArray());
        }

        return rows;
    }

    private static string[] ColumnNames(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        return Enumerable.Range(0, statement.GetColumnCount()).Select(statement.GetColumnName).ToArray();
    }

    private static List<string> Flatten(QueryOutput output)
        => output.Rows.Select(row => string.Join("\u001f", row)).ToList();

    private static void AssertMatchesSqlite(string setup, string query)
    {
        var managed = RunManaged(setup, query);
        var sqlite = RunSqlite(setup, query);
        managed.Columns.Should().Equal(sqlite.Columns, $"of query {query}");
        managed.Rows.Should().HaveCount(sqlite.Rows.Count, $"of query {query}");
        for (var index = 0; index < sqlite.Rows.Count; index++)
            managed.Rows[index].Should().Equal(sqlite.Rows[index], $"of row {index} of query {query}");
    }

    private static QueryOutput RunManaged(string setup, string query)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, setup);
        using var statement = connection.Prepare(query);
        var columns = Enumerable.Range(0, statement.GetColumnCount())
            .Select(statement.GetColumnName)
            .ToArray();
        var rows = new List<string[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            rows.Add(Enumerable.Range(0, statement.GetColumnCount())
                .Select(index => Normalize(statement.GetValue(index)))
                .ToArray());
        }

        return new QueryOutput(columns, rows);
    }

    private static QueryOutput RunSqlite(string setup, string query)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var setupCommand = connection.CreateCommand())
        {
            setupCommand.CommandText = setup;
            setupCommand.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = query;
        using var reader = command.ExecuteReader();
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<string[]>();
        while (reader.Read())
        {
            rows.Add(Enumerable.Range(0, reader.FieldCount)
                .Select(index => Normalize(reader.GetValue(index)))
                .ToArray());
        }

        return new QueryOutput(columns, rows);
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
            {
                while (statement.Step() == StatementStepResult.Row)
                {
                }
            }
        }
    }

    private static string Normalize(SqlValue value)
        => value.Kind switch
        {
            SqlValueKind.Null => "N:",
            SqlValueKind.Integer => $"I:{value.AsInteger()}",
            SqlValueKind.Real => $"R:{value.AsReal():R}",
            SqlValueKind.Text => $"T:{value.AsText()}",
            SqlValueKind.Blob => $"B:{Convert.ToBase64String(value.AsBlob().Span)}",
            _ => throw new InvalidOperationException($"Unknown managed value kind {value.Kind}."),
        };

    private static string Normalize(object value)
        => value switch
        {
            DBNull => "N:",
            long integer => $"I:{integer}",
            double real => $"R:{real:R}",
            string text => $"T:{text}",
            byte[] blob => $"B:{Convert.ToBase64String(blob)}",
            _ => throw new InvalidOperationException($"Unknown SQLite value type {value.GetType().Name}."),
        };

    private sealed record QueryOutput(string[] Columns, IReadOnlyList<string[]> Rows);
}
