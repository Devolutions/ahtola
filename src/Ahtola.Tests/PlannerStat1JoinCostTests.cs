using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Compilation;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

/// <summary>
/// P3 planner: sqlite_stat1 cardinality drives INNER join nested-loop outer choice and
/// multi-way equijoin hash build-side choice.
/// </summary>
public sealed class PlannerStat1JoinCostTests
{
    [Test]
    public void TwoTableInnerNestedLoopPutsSmallerTableOuterAfterAnalyze()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE big(id INTEGER PRIMARY KEY, b TEXT);");
        Execute(connection, "CREATE TABLE small(id INTEGER PRIMARY KEY, s TEXT);");
        for (var i = 1; i <= 40; i++)
            Execute(connection, $"INSERT INTO big VALUES ({i}, 'b{i}');");
        for (var i = 1; i <= 3; i++)
            Execute(connection, $"INSERT INTO small VALUES ({i}, 's{i}');");
        Execute(connection, "ANALYZE;");

        // Two-table INNER uses nested-loop VDBE (not OpenJoinCursor). Cursor 0 is outer.
        var opens = OpenReadCursors(
            connection,
            "EXPLAIN SELECT big.b, small.s FROM big JOIN small ON big.id = small.id;");
        opens.Should().HaveCount(2);
        opens[0].Should().Be("small"); // smaller estimated side is outer
        opens[1].Should().Be("big");

        var rows = ReadRows(
            connection,
            "SELECT big.b, small.s FROM big JOIN small ON big.id = small.id ORDER BY small.id;");
        rows.Should().HaveCount(3);
        rows[0].Should().Equal(SqlValue.Text("b1"), SqlValue.Text("s1"));
        rows[2].Should().Equal(SqlValue.Text("b3"), SqlValue.Text("s3"));
    }

    [Test]
    public void TwoTableInnerKeepsFromOrderWhenLeftIsAlreadySmaller()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE small(id INTEGER PRIMARY KEY, s TEXT);");
        Execute(connection, "CREATE TABLE big(id INTEGER PRIMARY KEY, b TEXT);");
        for (var i = 1; i <= 3; i++)
            Execute(connection, $"INSERT INTO small VALUES ({i}, 's{i}');");
        for (var i = 1; i <= 40; i++)
            Execute(connection, $"INSERT INTO big VALUES ({i}, 'b{i}');");
        Execute(connection, "ANALYZE;");

        var opens = OpenReadCursors(
            connection,
            "EXPLAIN SELECT small.s, big.b FROM small JOIN big ON small.id = big.id;");
        opens.Should().Equal("small", "big");

        ReadRows(connection, "SELECT COUNT(*) FROM small JOIN big ON small.id = big.id;")
            .Single()[0].Should().Be(SqlValue.Integer(3));
    }

    [Test]
    public void LeftOuterJoinKeepsSqlLeftAsOuterEvenWhenRightIsSmaller()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE big(id INTEGER PRIMARY KEY, b TEXT);");
        Execute(connection, "CREATE TABLE small(id INTEGER PRIMARY KEY, s TEXT);");
        for (var i = 1; i <= 30; i++)
            Execute(connection, $"INSERT INTO big VALUES ({i}, 'b{i}');");
        for (var i = 1; i <= 2; i++)
            Execute(connection, $"INSERT INTO small VALUES ({i}, 's{i}');");
        Execute(connection, "ANALYZE;");

        var opens = OpenReadCursors(
            connection,
            "EXPLAIN SELECT big.b, small.s FROM big LEFT JOIN small ON big.id = small.id;");
        opens.Should().Equal("big", "small");

        var rows = ReadRows(
            connection,
            "SELECT big.b, small.s FROM big LEFT JOIN small ON big.id = small.id ORDER BY big.id;");
        rows.Should().HaveCount(30);
        rows[0][1].Should().Be(SqlValue.Text("s1"));
        rows[2][1].Should().Be(SqlValue.Null);
    }

    [Test]
    public void WithoutAnalyzeTwoTableInnerKeepsFromOrder()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE a(id INTEGER PRIMARY KEY);");
        Execute(connection, "CREATE TABLE b(id INTEGER PRIMARY KEY);");
        Execute(connection, "INSERT INTO a VALUES (1), (2), (3);");
        Execute(connection, "INSERT INTO b VALUES (1);");

        OpenReadCursors(connection, "EXPLAIN SELECT a.id FROM a JOIN b ON a.id = b.id;")
            .Should().Equal("a", "b");
    }

    [Test]
    public void ThreeWayInnerEquijoinHashBuildsSmallerLeftSideAfterAnalyze()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE small(id INTEGER PRIMARY KEY, s TEXT);");
        Execute(connection, "CREATE TABLE big(id INTEGER PRIMARY KEY, b TEXT);");
        Execute(connection, "CREATE TABLE seed(x INTEGER PRIMARY KEY);");
        for (var i = 1; i <= 3; i++)
            Execute(connection, $"INSERT INTO small VALUES ({i}, 's{i}');");
        for (var i = 1; i <= 40; i++)
            Execute(connection, $"INSERT INTO big VALUES ({i}, 'b{i}');");
        for (var i = 1; i <= 3; i++)
            Execute(connection, $"INSERT INTO seed VALUES ({i});");
        Execute(connection, "ANALYZE;");

        // Root is (small ⋈ seed) ⋈ big: left residual ~3, right big=40 → hash-build left.
        var explain = ReadRows(
            connection,
            """
            EXPLAIN SELECT small.s, big.b
            FROM small JOIN seed ON small.id = seed.x JOIN big ON small.id = big.id;
            """);
        var openJoin = explain.Single(row => row[1].AsText() == "OpenJoinCursor");
        // EXPLAIN columns: addr, opcode, p1, p2, p3, p4, comment — p4 is index 5.
        openJoin[5].AsText().Should().Contain("hash-build left");
        // The FROM order is already optimal here, so the cost optimizer keeps it.
        openJoin[5].AsText().Should().Contain("scan order: small, seed, big");

        var rows = ReadRows(
            connection,
            """
            SELECT small.s, big.b
            FROM small JOIN seed ON small.id = seed.x JOIN big ON small.id = big.id
            ORDER BY small.id;
            """);
        rows.Should().HaveCount(3);
        rows[0].Should().Equal(SqlValue.Text("s1"), SqlValue.Text("b1"));
    }

    [Test]
    public void ThreeWayInnerEquijoinReordersToDriveFromTheSmallestTable()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE big(id INTEGER PRIMARY KEY, b TEXT);");
        Execute(connection, "CREATE TABLE small(id INTEGER PRIMARY KEY, s TEXT);");
        Execute(connection, "CREATE TABLE seed(x INTEGER PRIMARY KEY);");
        for (var i = 1; i <= 40; i++)
            Execute(connection, $"INSERT INTO big VALUES ({i}, 'b{i}');");
        for (var i = 1; i <= 3; i++)
            Execute(connection, $"INSERT INTO small VALUES ({i}, 's{i}');");
        Execute(connection, "INSERT INTO seed VALUES (1);");
        Execute(connection, "ANALYZE;");

        // FROM order is big(40), small(3), seed(1). The N-way cost optimizer drives from the
        // one-row table instead, and the resulting root builds its hash over that tiny
        // accumulated left side rather than over the 3-row right input.
        var explain = ReadRows(
            connection,
            """
            EXPLAIN SELECT big.b, small.s
            FROM big JOIN small ON big.id = small.id JOIN seed ON seed.x = big.id;
            """);
        var openJoin = explain.Single(row => row[1].AsText() == "OpenJoinCursor");
        openJoin[5].AsText().Should().Contain("scan order: seed, big, small");
        openJoin[5].AsText().Should().Contain("hash-build left");

        ReadRows(
                connection,
                "SELECT COUNT(*) FROM big JOIN small ON big.id = small.id JOIN seed ON seed.x = big.id;")
            .Single()[0].Should().Be(SqlValue.Integer(1));

        // Projection order still follows the FROM clause even though execution does not.
        var rows = ReadRows(
            connection,
            """
            SELECT big.b, small.s
            FROM big JOIN small ON big.id = small.id JOIN seed ON seed.x = big.id;
            """);
        rows.Should().HaveCount(1);
        rows[0].Should().Equal(SqlValue.Text("b1"), SqlValue.Text("s1"));
    }

    [Test]
    public void ThreeWayLeftOuterDoesNotHashBuildLeftEvenWhenLeftIsSmaller()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE small(id INTEGER PRIMARY KEY, s TEXT);");
        Execute(connection, "CREATE TABLE big(id INTEGER PRIMARY KEY, b TEXT);");
        Execute(connection, "CREATE TABLE seed(x INTEGER PRIMARY KEY);");
        for (var i = 1; i <= 2; i++)
            Execute(connection, $"INSERT INTO small VALUES ({i}, 's{i}');");
        for (var i = 1; i <= 30; i++)
            Execute(connection, $"INSERT INTO big VALUES ({i}, 'b{i}');");
        for (var i = 1; i <= 2; i++)
            Execute(connection, $"INSERT INTO seed VALUES ({i});");
        Execute(connection, "ANALYZE;");

        var explain = ReadRows(
            connection,
            """
            EXPLAIN SELECT small.s, big.b
            FROM small LEFT JOIN seed ON small.id = seed.x LEFT JOIN big ON small.id = big.id;
            """);
        var openJoin = explain.Single(row => row[1].AsText() == "OpenJoinCursor");
        openJoin[5].AsText().Should().NotContain("hash-build left");

        var rows = ReadRows(
            connection,
            """
            SELECT small.s, big.b
            FROM small LEFT JOIN seed ON small.id = seed.x LEFT JOIN big ON small.id = big.id
            ORDER BY small.id;
            """);
        rows.Should().HaveCount(2);
        rows[0][1].Should().Be(SqlValue.Text("b1"));
    }

    [Test]
    public void HashBuildLeftOperatorPlanMatchesHashBuildRightResults()
    {
        var leftRows = new List<SqlValue[]>
        {
            new[] { SqlValue.Integer(1) },
            new[] { SqlValue.Integer(2) },
        };
        var rightRows = new List<SqlValue[]>
        {
            new[] { SqlValue.Integer(2) },
            new[] { SqlValue.Integer(3) },
            new[] { SqlValue.Integer(2) },
        };
        var probe = new VdbeJoinEquiProbe(
            left => left.Values[0].AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture),
            right => right.Values[0].AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture));

        var buildRight = new VdbeJoinOperatorPlan(
            new VdbeJoinScanPlan("l", 1, new VdbeCursorSource(leftRows)),
            new VdbeJoinScanPlan("r", 1, new VdbeCursorSource(rightRows)),
            VdbeJoinKind.Inner,
            condition: null,
            probe,
            hashBuildRight: true);
        var buildLeft = new VdbeJoinOperatorPlan(
            new VdbeJoinScanPlan("l", 1, new VdbeCursorSource(leftRows)),
            new VdbeJoinScanPlan("r", 1, new VdbeCursorSource(rightRows)),
            VdbeJoinKind.Inner,
            condition: null,
            probe,
            hashBuildRight: false);

        static IReadOnlyList<(long L, long R)> Materialize(VdbeJoinOperatorPlan plan)
            => plan.Materialize(maximumRows: null)
                .Select(row => (row.Values[0].AsInteger(), row.Values[1].AsInteger()))
                .OrderBy(t => t.Item1)
                .ThenBy(t => t.Item2)
                .ToList();

        Materialize(buildLeft).Should().Equal(Materialize(buildRight));
        buildLeft.HashBuildRight.Should().BeFalse();
        buildRight.HashBuildRight.Should().BeTrue();
    }

    [Test]
    public void NestedLoopOuterSwapStillProjectsCorrectCombinedColumns()
    {
        var program = JoinProgramBuilder.Build(
            "left_t",
            leftColumnCount: 1,
            "right_t",
            rightColumnCount: 1,
            JoinType.Inner,
            projections: [JoinProjection.ForColumn(0), JoinProjection.ForColumn(1)],
            predicate: row =>
                row[0].Kind == SqlValueKind.Integer
                && row[1].Kind == SqlValueKind.Integer
                && row[0].AsInteger() == row[1].AsInteger(),
            leftIsOuter: false);

        ((OpenReadCursorInstruction)program.Instructions[0]).TableName.Should().Be("right_t");
        ((OpenReadCursorInstruction)program.Instructions[1]).TableName.Should().Be("left_t");

        var leftRows = new List<SqlValue[]> { new[] { SqlValue.Integer(1) }, new[] { SqlValue.Integer(2) } };
        var rightRows = new List<SqlValue[]> { new[] { SqlValue.Integer(2) } };
        using var statement = new ResumableStatement(
            program,
            [new VdbeCursorSource(rightRows), new VdbeCursorSource(leftRows)]);
        var results = new List<(long, long)>();
        while (true)
        {
            var step = statement.StepResumable();
            if (step == ResumableStatementStepResult.Done)
                break;
            step.Should().Be(ResumableStatementStepResult.Row);
            var row = statement.CurrentRow!;
            results.Add((row[0].AsInteger(), row[1].AsInteger()));
        }

        results.Should().Equal([(2L, 2L)]);
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static List<string> OpenReadCursors(EmbeddedConnection connection, string explainSql)
        => ReadRows(connection, explainSql)
            .Where(row => row[1].AsText() == "OpenReadCursor")
            .Select(row => row[5].AsText()) // p4 = table name
            .ToList();

    private static List<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var values = new SqlValue[statement.GetColumnCount()];
            for (var ordinal = 0; ordinal < values.Length; ordinal++)
                values[ordinal] = statement.GetValue(ordinal);
            rows.Add(values);
        }

        return rows;
    }
}
