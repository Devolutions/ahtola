using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

// Covers both managed join lowerings: the direct two-table INNER/LEFT nested loop and the
// materializing join cursor used for N-way/RIGHT/FULL/USING/NATURAL and downstream SELECT phases.
// EXPLAIN is the routing ground truth; deliberately unsafe callback/error-order shapes remain
// evaluator-owned and therefore have no bytecode dump.
public class JoinSqlRoutingTests
{
    [Test]
    public void InnerJoinOnEqualityMatchesEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);

        var rows = ReadRows(
            connection,
            "SELECT u.id, u.name, o.amount FROM users u INNER JOIN orders o ON u.id = o.user_id;");

        rows.Should().HaveCount(3);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("ada"), SqlValue.Integer(10));
        rows[1].Should().Equal(SqlValue.Integer(1), SqlValue.Text("ada"), SqlValue.Integer(20));
        rows[2].Should().Equal(SqlValue.Integer(2), SqlValue.Text("bo"), SqlValue.Integer(30));
    }

    [Test]
    public void InnerJoinStarExpandsLeftThenRightColumns()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);

        var rows = ReadRows(
            connection,
            "SELECT * FROM users u JOIN orders o ON u.id = o.user_id;");

        ColumnNames(connection, "SELECT * FROM users u JOIN orders o ON u.id = o.user_id;")
            .Should().Equal("id", "name", "id", "user_id", "amount");
        rows.Should().HaveCount(3);
        rows[0].Should().Equal(
            SqlValue.Integer(1), SqlValue.Text("ada"), SqlValue.Integer(100), SqlValue.Integer(1), SqlValue.Integer(10));
        rows[2].Should().Equal(
            SqlValue.Integer(2), SqlValue.Text("bo"), SqlValue.Integer(102), SqlValue.Integer(2), SqlValue.Integer(30));
    }

    [Test]
    public void QualifiedStarProjectsOnlyThatSourcesColumns()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);

        ColumnNames(connection, "SELECT o.* FROM users u JOIN orders o ON u.id = o.user_id;")
            .Should().Equal("id", "user_id", "amount");

        var rows = ReadRows(connection, "SELECT o.* FROM users u JOIN orders o ON u.id = o.user_id;");
        rows.Should().HaveCount(3);
        rows[0].Should().Equal(SqlValue.Integer(100), SqlValue.Integer(1), SqlValue.Integer(10));
        rows[1].Should().Equal(SqlValue.Integer(101), SqlValue.Integer(1), SqlValue.Integer(20));
        rows[2].Should().Equal(SqlValue.Integer(102), SqlValue.Integer(2), SqlValue.Integer(30));
    }

    [Test]
    public void AliasedAndConstantProjectionsRouteThroughTheJoin()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);

        ColumnNames(
            connection,
            "SELECT u.name AS who, o.amount AS cost FROM users u JOIN orders o ON u.id = o.user_id;")
            .Should().Equal("who", "cost");

        var rows = ReadRows(
            connection,
            "SELECT u.name AS who, o.amount AS cost, 7 FROM users u JOIN orders o ON u.id = o.user_id;");
        rows.Should().HaveCount(3);
        rows[0].Should().Equal(SqlValue.Text("ada"), SqlValue.Integer(10), SqlValue.Integer(7));

        // The whole statement (including the folded constant) went through the join program.
        Opcodes(ReadRows(
                connection,
                "EXPLAIN SELECT u.name AS who, o.amount AS cost, 7 FROM users u JOIN orders o ON u.id = o.user_id;"))
            .Should().Contain("FilterRegisters").And.Contain("LoadConstant");
    }

    [Test]
    public void InnerJoinWhereFoldsIntoThePerPairPredicate()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);

        var rows = ReadRows(
            connection,
            "SELECT u.name, o.amount FROM users u JOIN orders o ON u.id = o.user_id WHERE o.amount > 15;");

        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Text("ada"), SqlValue.Integer(20));
        rows[1].Should().Equal(SqlValue.Text("bo"), SqlValue.Integer(30));

        // A single FilterRegisters gates both the ON condition and the folded WHERE.
        Opcodes(ReadRows(
                connection,
                "EXPLAIN SELECT u.name, o.amount FROM users u JOIN orders o ON u.id = o.user_id WHERE o.amount > 15;"))
            .Count(opcode => opcode == "FilterRegisters").Should().Be(1);
    }

    [Test]
    public void CommaJoinProducesTheCrossProduct()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE a(x INTEGER);");
        Execute(connection, "CREATE TABLE b(y INTEGER);");
        Execute(connection, "INSERT INTO a VALUES (1), (2);");
        Execute(connection, "INSERT INTO b VALUES (10), (20);");

        var rows = ReadRows(connection, "SELECT a.x, b.y FROM a, b;");

        rows.Should().HaveCount(4);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(10));
        rows[1].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(20));
        rows[2].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(10));
        rows[3].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(20));
    }

    [Test]
    public void CrossJoinWithoutPredicateEmitsNoFilterRegisters()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE a(x INTEGER);");
        Execute(connection, "CREATE TABLE b(y INTEGER);");

        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN SELECT a.x, b.y FROM a CROSS JOIN b;")).ToList();

        opcodes.Should().Contain("OpenReadCursor")
            .And.Contain("Rewind")
            .And.Contain("Column")
            .And.Contain("ResultRow")
            .And.Contain("Next")
            .And.Contain("CloseCursor")
            .And.Contain("Halt");
        opcodes.Should().NotContain("FilterRegisters");
        opcodes.Should().NotContain("JumpIf");
    }

    [Test]
    public void NullJoinKeysNeverMatchInInnerJoin()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE l(k INTEGER, tag TEXT);");
        Execute(connection, "CREATE TABLE r(k INTEGER, tag TEXT);");
        Execute(connection, "INSERT INTO l VALUES (1, 'l1'), (NULL, 'lnull');");
        Execute(connection, "INSERT INTO r VALUES (1, 'r1'), (NULL, 'rnull');");

        var rows = ReadRows(connection, "SELECT l.tag, r.tag FROM l JOIN r ON l.k = r.k;");

        // NULL = NULL is not true, so only the (1,1) pair survives.
        rows.Should().ContainSingle();
        rows[0].Should().Equal(SqlValue.Text("l1"), SqlValue.Text("r1"));
    }

    [Test]
    public void NocaseCollationInOnConditionRoutesAndMatchesEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE l(name TEXT);");
        Execute(connection, "CREATE TABLE r(name TEXT);");
        Execute(connection, "INSERT INTO l VALUES ('Ada'), ('Bo');");
        Execute(connection, "INSERT INTO r VALUES ('ADA'), ('zoe');");

        var rows = ReadRows(
            connection,
            "SELECT l.name, r.name FROM l JOIN r ON l.name = r.name COLLATE NOCASE;");

        rows.Should().ContainSingle();
        rows[0].Should().Equal(SqlValue.Text("Ada"), SqlValue.Text("ADA"));

        Opcodes(ReadRows(
                connection,
                "EXPLAIN SELECT l.name, r.name FROM l JOIN r ON l.name = r.name COLLATE NOCASE;"))
            .Should().Contain("FilterRegisters");
    }

    [Test]
    public void LeftOuterJoinNullExtendsUnmatchedLeftRows()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);
        // A third user with no orders exercises the null-extension branch.
        Execute(connection, "INSERT INTO users VALUES (3, 'cy');");

        var rows = ReadRows(
            connection,
            "SELECT u.name, o.amount FROM users u LEFT JOIN orders o ON u.id = o.user_id;");

        rows.Should().HaveCount(4);
        rows[0].Should().Equal(SqlValue.Text("ada"), SqlValue.Integer(10));
        rows[1].Should().Equal(SqlValue.Text("ada"), SqlValue.Integer(20));
        rows[2].Should().Equal(SqlValue.Text("bo"), SqlValue.Integer(30));
        rows[3].Should().Equal(SqlValue.Text("cy"), SqlValue.Null);
    }

    [Test]
    public void LeftOuterJoinNullExtendsEveryRowWhenRightIsEmpty()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE users(id INTEGER, name TEXT);");
        Execute(connection, "CREATE TABLE orders(id INTEGER, user_id INTEGER, amount INTEGER);");
        Execute(connection, "INSERT INTO users VALUES (1, 'ada'), (2, 'bo');");

        var rows = ReadRows(
            connection,
            "SELECT u.name, o.amount FROM users u LEFT JOIN orders o ON u.id = o.user_id;");

        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Text("ada"), SqlValue.Null);
        rows[1].Should().Equal(SqlValue.Text("bo"), SqlValue.Null);
    }

    [Test]
    public void LeftOuterJoinWithEmptyLeftYieldsNoRows()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE users(id INTEGER, name TEXT);");
        Execute(connection, "CREATE TABLE orders(id INTEGER, user_id INTEGER, amount INTEGER);");
        Execute(connection, "INSERT INTO orders VALUES (100, 1, 10);");

        ReadRows(connection, "SELECT u.name, o.amount FROM users u LEFT JOIN orders o ON u.id = o.user_id;")
            .Should().BeEmpty();
    }

    [Test]
    public void InnerJoinExplainEmitsTheNestedLoopProgram()
    {
        using var connection = new EmbeddedDatabase().Connect();
        // Single-column tables keep the combined-column reads to one per side so the exact
        // opcode sequence stays readable.
        Execute(connection, "CREATE TABLE p(a INTEGER);");
        Execute(connection, "CREATE TABLE q(b INTEGER);");

        var opcodes = Opcodes(ReadRows(
            connection,
            "EXPLAIN SELECT p.a, q.b FROM p JOIN q ON p.a = q.b;")).ToList();

        opcodes.Should().Equal(
            "OpenReadCursor",
            "OpenReadCursor",
            "Rewind",
            "Rewind",
            "Column",
            "Column",
            "FilterRegisters",
            "Copy",
            "Copy",
            "ResultRow",
            "Next",
            "Next",
            "CloseCursor",
            "CloseCursor",
            "Halt");
    }

    [Test]
    public void LeftOuterJoinExplainEmitsMatchFlagAndJumpIf()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);

        var opcodes = Opcodes(ReadRows(
            connection,
            "EXPLAIN SELECT u.id, o.amount FROM users u LEFT JOIN orders o ON u.id = o.user_id;")).ToList();

        opcodes.Should().Contain("FilterRegisters")
            .And.Contain("JumpIf")
            .And.Contain("LoadConstant")
            .And.Contain("ResultRow");

        // The LEFT OUTER shape projects two result rows: the matched pair and the null-extension.
        opcodes.Count(opcode => opcode == "ResultRow").Should().Be(2);

        Comments(ReadRows(
                connection,
                "EXPLAIN SELECT u.id, o.amount FROM users u LEFT JOIN orders o ON u.id = o.user_id;"))
            .Should().Contain(comment => comment.StartsWith("goto ") && comment.Contains(" if r["));
    }

    [Test]
    public void JoinStatementResetReplayReflectsAppendedRows()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);

        using var statement = connection.Prepare(
            "SELECT u.name, o.amount FROM users u JOIN orders o ON u.id = o.user_id;");

        DrainPairs(statement).Should().Equal(
            (SqlValue.Text("ada"), SqlValue.Integer(10)),
            (SqlValue.Text("ada"), SqlValue.Integer(20)),
            (SqlValue.Text("bo"), SqlValue.Integer(30)));

        Execute(connection, "INSERT INTO orders VALUES (103, 2, 40);");

        statement.Reset();
        DrainPairs(statement).Should().Equal(
            (SqlValue.Text("ada"), SqlValue.Integer(10)),
            (SqlValue.Text("ada"), SqlValue.Integer(20)),
            (SqlValue.Text("bo"), SqlValue.Integer(30)),
            (SqlValue.Text("bo"), SqlValue.Integer(40)));
    }

    [Test]
    public void RightJoinRoutesThroughMaterializingJoinCursor()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);
        Execute(connection, "INSERT INTO orders VALUES (200, 9, 99);");

        // The orphan order surfaces with NULL user columns.
        var rows = ReadRows(
            connection,
            "SELECT u.name, o.amount FROM users u RIGHT JOIN orders o ON u.id = o.user_id;");
        rows.Should().HaveCount(4);
        rows[3].Should().Equal(SqlValue.Null, SqlValue.Integer(99));

        Opcodes(ReadRows(
                connection,
                "EXPLAIN SELECT u.name, o.amount FROM users u RIGHT JOIN orders o ON u.id = o.user_id;"))
            .Should().Contain("OpenJoinCursor");
    }

    [Test]
    public void FullJoinRoutesThroughMaterializingJoinCursor()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);

        var rows = ReadRows(
            connection,
            "SELECT u.name, o.amount FROM users u FULL JOIN orders o ON u.id = o.user_id;");
        rows.Should().HaveCount(3);

        Opcodes(ReadRows(
                connection,
                "EXPLAIN SELECT u.name, o.amount FROM users u FULL JOIN orders o ON u.id = o.user_id;"))
            .Should().Contain("OpenJoinCursor");
    }

    [Test]
    public void UsingJoinStaysOnTheEvaluatorWithCoalescedColumns()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE l(id INTEGER, tag TEXT);");
        Execute(connection, "CREATE TABLE r(id INTEGER, note TEXT);");
        Execute(connection, "INSERT INTO l VALUES (1, 'a'), (2, 'b');");
        Execute(connection, "INSERT INTO r VALUES (1, 'x'), (3, 'y');");

        // USING coalesces the join column into a single output.
        var rows = ReadRows(connection, "SELECT id, tag, note FROM l JOIN r USING (id);");
        rows.Should().ContainSingle();
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("a"), SqlValue.Text("x"));

        // The compiled join builder concatenates both source rows verbatim, so it cannot represent
        // a coalesced USING column and declines. The evaluator owns the shape instead.
        AssertEvaluatorOwned(connection, "SELECT id, tag, note FROM l JOIN r USING (id);");
    }

    [Test]
    public void NaturalJoinStaysOnTheEvaluatorWithCoalescedColumns()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE l(id INTEGER, tag TEXT);");
        Execute(connection, "CREATE TABLE r(id INTEGER, note TEXT);");
        Execute(connection, "INSERT INTO l VALUES (1, 'a'), (2, 'b');");
        Execute(connection, "INSERT INTO r VALUES (2, 'x'), (3, 'y');");

        var rows = ReadRows(connection, "SELECT id, tag, note FROM l NATURAL JOIN r;");
        rows.Should().ContainSingle();
        rows[0].Should().Equal(SqlValue.Integer(2), SqlValue.Text("b"), SqlValue.Text("x"));

        AssertEvaluatorOwned(connection, "SELECT id, tag, note FROM l NATURAL JOIN r;");
    }

    [Test]
    public void MultiTableUsingAndNaturalJoinsShareCoalescedHashKeys()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE a(id INTEGER, av TEXT);");
        Execute(connection, "CREATE TABLE b(id INTEGER, bv TEXT);");
        Execute(connection, "CREATE TABLE c(id INTEGER, cv TEXT);");
        Execute(connection, "INSERT INTO a VALUES (1, 'a1'), (2, 'a2'), (3, 'a3');");
        Execute(connection, "INSERT INTO b VALUES (1, 'b1'), (2, 'b2'), (4, 'b4');");
        Execute(connection, "INSERT INTO c VALUES (1, 'c1'), (2, 'c2'), (5, 'c5');");

        var usingRows = ReadRows(
            connection,
            "SELECT a.av, b.bv, c.cv FROM a JOIN b USING(id) JOIN c USING(id);");
        usingRows.Should().HaveCount(2);
        usingRows[0].Should().Equal(SqlValue.Text("a1"), SqlValue.Text("b1"), SqlValue.Text("c1"));
        usingRows[1].Should().Equal(SqlValue.Text("a2"), SqlValue.Text("b2"), SqlValue.Text("c2"));
        ReadRows(connection, "SELECT a.av, b.bv, c.cv FROM a NATURAL JOIN b NATURAL JOIN c;")
            .Should().HaveCount(2);
    }

    [Test]
    public void ConstantFalseInnerJoinShortCircuitsAnOuterJoinInput()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE a(id INTEGER);");
        Execute(connection, "CREATE TABLE b(id INTEGER);");
        Execute(connection, "CREATE TABLE c(id INTEGER);");
        Execute(connection, "INSERT INTO a VALUES (1), (2);");
        Execute(connection, "INSERT INTO b VALUES (1), (2);");
        Execute(connection, "INSERT INTO c VALUES (1), (2);");

        ReadRows(connection, "SELECT * FROM a LEFT JOIN b ON 1 JOIN c ON 0;")
            .Should().BeEmpty();
    }

    [Test]
    public void ThreeTableJoinRoutesThroughMaterializingJoinCursor()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE a(id INTEGER, av TEXT);");
        Execute(connection, "CREATE TABLE b(id INTEGER, bv TEXT);");
        Execute(connection, "CREATE TABLE c(id INTEGER, cv TEXT);");
        Execute(connection, "INSERT INTO a VALUES (1, 'a1');");
        Execute(connection, "INSERT INTO b VALUES (1, 'b1');");
        Execute(connection, "INSERT INTO c VALUES (1, 'c1');");

        var rows = ReadRows(
            connection,
            "SELECT a.av, b.bv, c.cv FROM a JOIN b ON a.id = b.id JOIN c ON b.id = c.id;");
        rows.Should().ContainSingle();
        rows[0].Should().Equal(SqlValue.Text("a1"), SqlValue.Text("b1"), SqlValue.Text("c1"));

        Opcodes(ReadRows(
                connection,
                "EXPLAIN SELECT a.av, b.bv, c.cv FROM a JOIN b ON a.id = b.id JOIN c ON b.id = c.id;"))
            .Should().Contain("OpenJoinCursor");
    }

    [Test]
    public void GroupByOverJoinRoutesThroughAggregateOpcodes()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);

        var rows = ReadRows(
            connection,
            "SELECT u.name, count(*) FROM users u JOIN orders o ON u.id = o.user_id GROUP BY u.name;");
        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Text("ada"), SqlValue.Integer(2));
        rows[1].Should().Equal(SqlValue.Text("bo"), SqlValue.Integer(1));

        Opcodes(ReadRows(
                connection,
                "EXPLAIN SELECT u.name, count(*) FROM users u JOIN orders o ON u.id = o.user_id GROUP BY u.name;"))
            .Should().Contain("OpenJoinCursor").And.Contain("AggStep").And.Contain("AggFinalize");
    }

    [Test]
    public void OrderByOverJoinRoutesThroughSorterOpcodes()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);

        var rows = ReadRows(
            connection,
            "SELECT o.amount FROM users u JOIN orders o ON u.id = o.user_id ORDER BY o.amount DESC;");
        rows.Select(row => row[0])
            .Should().Equal(SqlValue.Integer(30), SqlValue.Integer(20), SqlValue.Integer(10));

        Opcodes(ReadRows(
                connection,
                "EXPLAIN SELECT o.amount FROM users u JOIN orders o ON u.id = o.user_id ORDER BY o.amount DESC;"))
            .Should().Contain("OpenJoinCursor").And.Contain("OpenSorter");
    }

    [Test]
    public void DistinctOverJoinRoutesThroughDistinctFilter()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);

        var rows = ReadRows(
            connection,
            "SELECT DISTINCT u.name FROM users u JOIN orders o ON u.id = o.user_id;");
        rows.Select(row => row[0]).Should().Equal(SqlValue.Text("ada"), SqlValue.Text("bo"));

        Opcodes(ReadRows(
                connection,
                "EXPLAIN SELECT DISTINCT u.name FROM users u JOIN orders o ON u.id = o.user_id;"))
            .Should().Contain("OpenJoinCursor").And.Contain("DistinctFilter");
    }

    [Test]
    public void DirectInnerEquiJoinWhereLimitOffsetRoutesAndMatchesEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);

        var routed = ReadRows(
            connection,
            "SELECT u.id AS user_id, o.amount AS cost, 7 AS marker FROM users u JOIN orders o ON u.id = o.user_id WHERE o.amount >= 10 LIMIT 2 OFFSET 1;");
        routed.Select(row => (row[0], row[1], row[2])).Should().Equal(
            (SqlValue.Integer(1), SqlValue.Integer(20), SqlValue.Integer(7)),
            (SqlValue.Integer(2), SqlValue.Integer(30), SqlValue.Integer(7)));
        ColumnNames(
                connection,
                "SELECT u.id AS user_id, o.amount AS cost, 7 AS marker FROM users u JOIN orders o ON u.id = o.user_id WHERE o.amount >= 10 LIMIT 2 OFFSET 1;")
            .Should().Equal("user_id", "cost", "marker");

        // The computed equivalent uses the materializing join cursor and is the differential
        // oracle for the legacy direct nested-loop row stream.
        var evaluated = ReadRows(
            connection,
            "SELECT u.id + 0 AS user_id, o.amount AS cost, 7 AS marker FROM users u JOIN orders o ON u.id = o.user_id WHERE o.amount >= 10 LIMIT 2 OFFSET 1;");
        routed.Select(row => (row[0], row[1], row[2]))
            .Should().Equal(evaluated.Select(row => (row[0], row[1], row[2])));

        var opcodes = Opcodes(ReadRows(
            connection,
            "EXPLAIN SELECT u.id AS user_id, o.amount AS cost, 7 AS marker FROM users u JOIN orders o ON u.id = o.user_id WHERE o.amount >= 10 LIMIT 2 OFFSET 1;"));
        opcodes.Should().Contain("FilterRegisters")
            .And.Contain("OffsetGate")
            .And.Contain("LimitGate")
            .And.NotContain("JumpIf");
        Opcodes(ReadRows(
                connection,
                "EXPLAIN SELECT u.id + 0 AS user_id, o.amount AS cost, 7 AS marker FROM users u JOIN orders o ON u.id = o.user_id WHERE o.amount >= 10 LIMIT 2 OFFSET 1;"))
            .Should().Contain("OpenJoinCursor").And.Contain("ProjectRegisters");
    }

    [Test]
    public void BoundedInnerJoinPreservesCollationNullAndParameterResetSemantics()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE l(key TEXT, label TEXT);");
        Execute(connection, "CREATE TABLE r(key TEXT, amount INTEGER);");
        Execute(connection, "INSERT INTO l VALUES ('Ada', 'first'), (NULL, 'null-key'), ('Bo', 'third');");
        Execute(connection, "INSERT INTO r VALUES ('ADA', 10), (NULL, 99), ('BO', 20);");

        using var statement = connection.Prepare(
            "SELECT l.label AS label, r.amount AS amount FROM l JOIN r ON l.key = r.key COLLATE NOCASE WHERE r.amount >= ? LIMIT ? OFFSET ?;");
        statement.Bind(1, SqlValue.Integer(0));
        statement.Bind(2, SqlValue.Integer(1));
        statement.Bind(3, SqlValue.Integer(1));
        DrainRows(statement).Select(row => (row[0], row[1]))
            .Should().Equal((SqlValue.Text("third"), SqlValue.Integer(20)));

        statement.Reset();
        statement.Bind(1, SqlValue.Integer(0));
        statement.Bind(2, SqlValue.Integer(2));
        statement.Bind(3, SqlValue.Integer(0));
        DrainRows(statement).Select(row => (row[0], row[1]))
            .Should().Equal(
                (SqlValue.Text("first"), SqlValue.Integer(10)),
                (SqlValue.Text("third"), SqlValue.Integer(20)));

        // NULL = NULL remains unknown, not true: the NULL-key pair never enters either routed result.
        Opcodes(ReadRows(
            connection,
            "EXPLAIN SELECT l.label, r.amount FROM l JOIN r ON l.key = r.key COLLATE NOCASE WHERE r.amount >= 0 LIMIT 2;"))
            .Should().Contain("FilterRegisters").And.Contain("LimitGate");
    }

    [Test]
    public void BoundedLeftEquiJoinRoutesAndMatchesEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedLeftJoinCases(connection);

        const string routedSql =
            "SELECT l.label AS left_label, r.amount AS right_amount, 7 AS marker FROM l LEFT JOIN r ON l.key = r.key COLLATE NOCASE LIMIT 6 OFFSET 0;";
        var routed = ReadRows(connection, routedSql);
        var evaluated = ReadRows(
            connection,
            "SELECT l.label AS left_label, r.amount AS right_amount, 7 AS marker FROM l LEFT JOIN r ON l.key = r.key COLLATE NOCASE WHERE 1 LIMIT 6 OFFSET 0;");

        routed.Select(row => (row[0], row[1], row[2])).Should().Equal(
            evaluated.Select(row => (row[0], row[1], row[2])));
        routed.Select(row => (row[0], row[1], row[2])).Should().Equal(
            (SqlValue.Text("first"), SqlValue.Integer(10), SqlValue.Integer(7)),
            (SqlValue.Text("first"), SqlValue.Integer(11), SqlValue.Integer(7)),
            (SqlValue.Text("null-key"), SqlValue.Null, SqlValue.Integer(7)),
            (SqlValue.Text("third"), SqlValue.Integer(20), SqlValue.Integer(7)),
            (SqlValue.Text("unmatched"), SqlValue.Null, SqlValue.Integer(7)),
            (SqlValue.Text("affinity"), SqlValue.Integer(40), SqlValue.Integer(7)));
        ColumnNames(connection, routedSql).Should().Equal("left_label", "right_amount", "marker");

        var trimmed = ReadRows(
            connection,
            "SELECT l.label, r.amount FROM l LEFT JOIN r ON l.key = r.key COLLATE NOCASE LIMIT 4 OFFSET 1;");
        trimmed.Select(row => (row[0], row[1])).Should().Equal(
            (SqlValue.Text("first"), SqlValue.Integer(11)),
            (SqlValue.Text("null-key"), SqlValue.Null),
            (SqlValue.Text("third"), SqlValue.Integer(20)),
            (SqlValue.Text("unmatched"), SqlValue.Null));

        var opcodes = Opcodes(ReadRows(
            connection,
            "EXPLAIN SELECT l.label, r.amount FROM l LEFT JOIN r ON l.key = r.key COLLATE NOCASE LIMIT 4 OFFSET 1;")).ToList();
        opcodes.Should().Contain("FilterRegisters")
            .And.Contain("JumpIf")
            .And.Contain("OffsetGate")
            .And.Contain("LimitGate");
        opcodes.Count(opcode => opcode == "ResultRow").Should().Be(2);
    }

    [Test]
    public void BoundedLeftEquiJoinPreservesParameterResetAndMetadataSemantics()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedLeftJoinCases(connection);

        using var routed = connection.Prepare(
            "SELECT l.label AS left_label, r.amount AS right_amount FROM l LEFT JOIN r ON l.key = r.key COLLATE NOCASE WHERE r.amount >= ? LIMIT ? OFFSET ?;");
        using var evaluated = connection.Prepare(
            "SELECT l.label || '' AS left_label, r.amount AS right_amount FROM l LEFT JOIN r ON l.key = r.key COLLATE NOCASE WHERE r.amount >= ? LIMIT ? OFFSET ?;");

        routed.GetColumnCount().Should().Be(2);
        routed.GetColumnName(0).Should().Be("left_label");
        routed.GetColumnName(1).Should().Be("right_amount");

        routed.Bind(1, SqlValue.Integer(0));
        routed.Bind(2, SqlValue.Integer(2));
        routed.Bind(3, SqlValue.Integer(1));
        evaluated.Bind(1, SqlValue.Integer(0));
        evaluated.Bind(2, SqlValue.Integer(2));
        evaluated.Bind(3, SqlValue.Integer(1));
        var first = DrainRows(routed);
        first.Select(row => (row[0], row[1])).Should().Equal(
            (SqlValue.Text("first"), SqlValue.Integer(11)),
            (SqlValue.Text("third"), SqlValue.Integer(20)));
        first.Select(row => (row[0], row[1])).Should().Equal(
            DrainRows(evaluated).Select(row => (row[0], row[1])));

        routed.Reset();
        evaluated.Reset();
        routed.Bind(1, SqlValue.Integer(15));
        routed.Bind(2, SqlValue.Integer(3));
        routed.Bind(3, SqlValue.Integer(0));
        evaluated.Bind(1, SqlValue.Integer(15));
        evaluated.Bind(2, SqlValue.Integer(3));
        evaluated.Bind(3, SqlValue.Integer(0));
        var second = DrainRows(routed);
        second.Select(row => (row[0], row[1])).Should().Equal(
            (SqlValue.Text("third"), SqlValue.Integer(20)),
            (SqlValue.Text("affinity"), SqlValue.Integer(40)));
        second.Select(row => (row[0], row[1])).Should().Equal(
            DrainRows(evaluated).Select(row => (row[0], row[1])));

        routed.Reset();
        evaluated.Reset();
        routed.Bind(1, SqlValue.Null);
        routed.Bind(2, SqlValue.Integer(3));
        routed.Bind(3, SqlValue.Integer(0));
        evaluated.Bind(1, SqlValue.Null);
        evaluated.Bind(2, SqlValue.Integer(3));
        evaluated.Bind(3, SqlValue.Integer(0));
        DrainRows(routed).Should().BeEmpty();
        DrainRows(evaluated).Should().BeEmpty();

        var explain = ReadRows(
            connection,
            "EXPLAIN SELECT l.label AS left_label, r.amount AS right_amount FROM l LEFT JOIN r ON l.key = r.key COLLATE NOCASE WHERE r.amount >= 0 LIMIT 2 OFFSET 1;");
        Opcodes(explain).Count(opcode => opcode == "FilterRegisters").Should().Be(3);
        Comments(explain).Should().Contain(comment => comment.StartsWith("skip result when post-join WHERE is false"));
    }

    [Test]
    public void BoundedCrossJoinWithDirectWhereRoutesAndMatchesEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE a(x INTEGER);");
        Execute(connection, "CREATE TABLE b(y INTEGER);");
        Execute(connection, "INSERT INTO a VALUES (1), (2);");
        Execute(connection, "INSERT INTO b VALUES (10), (20), (30);");

        const string routedSql =
            "SELECT a.x AS left_value, b.y AS right_value, 7 AS marker FROM a CROSS JOIN b WHERE a.x < b.y LIMIT 3 OFFSET 1;";
        var routed = ReadRows(connection, routedSql);
        routed.Select(row => (row[0], row[1], row[2])).Should().Equal(
            (SqlValue.Integer(1), SqlValue.Integer(20), SqlValue.Integer(7)),
            (SqlValue.Integer(1), SqlValue.Integer(30), SqlValue.Integer(7)),
            (SqlValue.Integer(2), SqlValue.Integer(10), SqlValue.Integer(7)));
        ColumnNames(connection, routedSql).Should().Equal("left_value", "right_value", "marker");

        // The computed projection uses the materializing join cursor and supplies a behavioral
        // oracle for the direct-column nested-loop stream.
        var evaluated = ReadRows(
            connection,
            "SELECT a.x + 0 AS left_value, b.y AS right_value, 7 AS marker FROM a CROSS JOIN b WHERE a.x < b.y LIMIT 3 OFFSET 1;");
        routed.Select(row => (row[0], row[1], row[2]))
            .Should().Equal(evaluated.Select(row => (row[0], row[1], row[2])));

        var opcodes = Opcodes(ReadRows(connection, $"EXPLAIN {routedSql}")).ToList();
        opcodes.Should().Contain("FilterRegisters")
            .And.Contain("OffsetGate")
            .And.Contain("LimitGate")
            .And.NotContain("JumpIf");
        Opcodes(ReadRows(
                connection,
                "EXPLAIN SELECT a.x + 0 AS left_value, b.y AS right_value, 7 AS marker FROM a CROSS JOIN b WHERE a.x < b.y LIMIT 3 OFFSET 1;"))
            .Should().Contain("OpenJoinCursor").And.Contain("ProjectRegisters");
    }

    [Test]
    public void BoundedCommaJoinRoutesThroughTheCrossJoinPipeline()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE a(x INTEGER);");
        Execute(connection, "CREATE TABLE b(y INTEGER);");
        Execute(connection, "INSERT INTO a VALUES (1), (2);");
        Execute(connection, "INSERT INTO b VALUES (10), (20);");

        var rows = ReadRows(connection, "SELECT a.x, b.y FROM a, b WHERE a.x >= 2 LIMIT 2;");

        rows.Select(row => (row[0], row[1])).Should().Equal(
            (SqlValue.Integer(2), SqlValue.Integer(10)),
            (SqlValue.Integer(2), SqlValue.Integer(20)));
        Opcodes(ReadRows(connection, "EXPLAIN SELECT a.x, b.y FROM a, b WHERE a.x >= 2 LIMIT 2;"))
            .Should().Contain("FilterRegisters").And.Contain("LimitGate").And.NotContain("JumpIf");
    }

    [Test]
    public void BoundedComputedPredicatesRouteThroughVdbe()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);
        Execute(connection, "INSERT INTO users VALUES (3, 'cy');");

        ReadRows(
                connection,
                "SELECT u.name, o.amount FROM users u JOIN orders o ON u.id + 0 = o.user_id LIMIT 1;")
            .Should().ContainSingle();
        ReadRows(
                connection,
                "SELECT u.name, o.amount FROM users u JOIN orders o ON u.id = o.user_id WHERE o.amount + 0 > 10 LIMIT 1;")
            .Should().ContainSingle();
        ReadRows(
                connection,
                "SELECT u.name, o.amount FROM users u LEFT JOIN orders o ON u.id = o.user_id WHERE o.amount + 0 > 10 LIMIT 1;")
            .Should().ContainSingle();
        // Deterministic arithmetic operands are evaluated inside the compiled join. The source
        // may consume the bound directly, so a separate LimitGate is not required.
        // Registered callbacks remain on the evaluator path.
        foreach (var sql in new[]
        {
            "EXPLAIN SELECT u.name, o.amount FROM users u JOIN orders o ON u.id + 0 = o.user_id LIMIT 1;",
            "EXPLAIN SELECT u.name, o.amount FROM users u JOIN orders o ON u.id = o.user_id WHERE o.amount + 0 > 10 LIMIT 1;",
            "EXPLAIN SELECT u.name, o.amount FROM users u LEFT JOIN orders o ON u.id = o.user_id WHERE o.amount + 0 > 10 LIMIT 1;",
        })
        {
            Opcodes(ReadRows(connection, sql)).Should().Contain("OpenJoinCursor");
        }
    }

    [Test]
    public void BoundedJoinInvalidColumnAndLimitKeepEvaluatorErrors()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);

        Assert.Throws<EmbeddedSqlException>(() => ReadRows(
                connection,
                "SELECT u.name FROM users u JOIN orders o ON u.id = o.user_id WHERE o.missing = 1 LIMIT 1;"))!
            .Message.Should().Be("no such column: o.missing");
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(
                connection,
                "SELECT u.name FROM users u JOIN orders o ON u.id = o.user_id LIMIT 'x';"))!
            .Message.Should().Be("datatype mismatch");

        Assert.Throws<EmbeddedSqlException>(() => ReadRows(
            connection,
            "EXPLAIN SELECT u.name FROM users u JOIN orders o ON u.id = o.user_id WHERE o.missing = 1 LIMIT 1;"));

        SeedLeftJoinCases(connection);
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(
                connection,
                "SELECT l.label FROM l LEFT JOIN r ON l.missing = r.key LIMIT 1;"))!
            .Message.Should().Be("no such column: l.missing");
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(
                connection,
                "SELECT l.label FROM l LEFT JOIN r ON l.key = r.key LIMIT 'x';"))!
            .Message.Should().Be("datatype mismatch");
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(
                connection,
                "SELECT l.label FROM l LEFT JOIN r ON l.key = r.key WHERE r.missing IS NULL LIMIT 1;"))!
            .Message.Should().Be("no such column: r.missing");
        // Column resolution happens at prepare time, before the LIMIT datatype is
        // checked, so a bad column wins even when LIMIT is also invalid (sqlite3 parity).
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(
                connection,
                "SELECT l.label FROM l LEFT JOIN r ON l.key = r.key WHERE r.missing IS NULL LIMIT 'x';"))!
            .Message.Should().Be("no such column: r.missing");
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(
            connection,
            "EXPLAIN SELECT l.label FROM l LEFT JOIN r ON l.key = r.key WHERE r.missing IS NULL LIMIT 1;"));
    }

    [Test]
    public void BoundedLeftOuterJoinWhereRoutesAfterNullExtension()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);
        Execute(connection, "INSERT INTO users VALUES (3, 'cy');");

        const string routedSql =
            "SELECT u.name AS user_name, o.amount AS order_amount FROM users u LEFT JOIN orders o ON u.id = o.user_id WHERE o.amount IS NULL LIMIT 5 OFFSET 0;";
        var routed = ReadRows(
            connection,
            routedSql);
        routed.Should().ContainSingle();
        routed[0].Should().Equal(SqlValue.Text("cy"), SqlValue.Null);
        ColumnNames(connection, routedSql).Should().Equal("user_name", "order_amount");

        // The computed projection uses the materializing join cursor as the differential oracle.
        var evaluated = ReadRows(
            connection,
            "SELECT u.name || '' AS user_name, o.amount AS order_amount FROM users u LEFT JOIN orders o ON u.id = o.user_id WHERE o.amount IS NULL LIMIT 5 OFFSET 0;");
        routed.Select(row => (row[0], row[1]))
            .Should().Equal(evaluated.Select(row => (row[0], row[1])));

        var explain = ReadRows(
            connection,
            "EXPLAIN SELECT u.name AS user_name, o.amount AS order_amount FROM users u LEFT JOIN orders o ON u.id = o.user_id WHERE o.amount IS NULL LIMIT 5 OFFSET 0;");
        Opcodes(explain).Should().Contain("OpenJoinCursor")
            .And.Contain("ProjectRegisters")
            .And.Contain("LimitGate")
            .And.NotContain("JumpIf");
    }

    [Test]
    public void UnboundedLeftOuterJoinWhereRoutesAfterNullExtension()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);
        Execute(connection, "INSERT INTO users VALUES (3, 'cy');");

        var rows = ReadRows(
            connection,
            "SELECT u.name, o.amount FROM users u LEFT JOIN orders o ON u.id = o.user_id WHERE o.amount IS NULL;");
        rows.Should().ContainSingle();
        rows[0].Should().Equal(SqlValue.Text("cy"), SqlValue.Null);

        Opcodes(ReadRows(
                connection,
                "EXPLAIN SELECT u.name, o.amount FROM users u LEFT JOIN orders o ON u.id = o.user_id WHERE o.amount IS NULL;"))
            .Should().Contain("OpenJoinCursor");
    }

    [Test]
    public void ComputedExpressionProjectionRoutesThroughProjectRegisters()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);

        // The materializing cursor completes ON/WHERE before ProjectRegisters computes each result.
        var rows = ReadRows(
            connection,
            "SELECT o.amount + 1 FROM users u JOIN orders o ON u.id = o.user_id;");
        rows.Select(row => row[0])
            .Should().Equal(SqlValue.Integer(11), SqlValue.Integer(21), SqlValue.Integer(31));

        Opcodes(ReadRows(
                connection,
                "EXPLAIN SELECT o.amount + 1 FROM users u JOIN orders o ON u.id = o.user_id;"))
            .Should().Contain("OpenJoinCursor").And.Contain("ProjectRegisters");
    }

    [Test]
    public void MissingQualifiedStarTableStillRaisesEvaluatorError()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);

        // A qualifier that names neither side declines so the evaluator raises its exact error.
        var error = Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "SELECT z.* FROM users u JOIN orders o ON u.id = o.user_id;"))!;
        error.Message.Should().Be("no such table: z");

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT z.* FROM users u JOIN orders o ON u.id = o.user_id;"));
    }

    [Test]
    public void ParenthesizedJoinClauseParsesAndExecutes()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);

        var rows = ReadRows(
            connection,
            "SELECT u.name, o.id, o.amount FROM users u INNER JOIN (orders o INNER JOIN users u2 ON o.user_id = u2.id) ON u.id = o.user_id ORDER BY o.id;");

        rows.Should().HaveCount(3);
        rows[0].Should().Equal(SqlValue.Text("ada"), SqlValue.Integer(100), SqlValue.Integer(10));
        rows[1].Should().Equal(SqlValue.Text("ada"), SqlValue.Integer(101), SqlValue.Integer(20));
        rows[2].Should().Equal(SqlValue.Text("bo"), SqlValue.Integer(102), SqlValue.Integer(30));
    }

    [Test]
    public void ParenthesizedJoinClausePreservesLeftJoinGrouping()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);
        Execute(connection, "INSERT INTO users VALUES (3, 'cy');");

        // The parenthesized group only contains order 102 (joined to u2 'bo'), so the LEFT JOIN
        // must NULL-extend ada and cy rather than filtering them like a flattened inner join would.
        var rows = ReadRows(
            connection,
            "SELECT u.name, o.id FROM users u LEFT JOIN (orders o INNER JOIN users u2 ON o.user_id = u2.id AND u2.name = 'bo') ON u.id = o.user_id ORDER BY u.name;");

        rows.Should().HaveCount(3);
        rows[0].Should().Equal(SqlValue.Text("ada"), SqlValue.Null);
        rows[1].Should().Equal(SqlValue.Text("bo"), SqlValue.Integer(102));
        rows[2].Should().Equal(SqlValue.Text("cy"), SqlValue.Null);
    }

    [Test]
    public void NestedParenthesizedJoinChainMatchesNorthwindInvoicesShape()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);
        Execute(connection, "CREATE TABLE items(id INTEGER, order_id INTEGER, sku TEXT);");
        Execute(connection, "INSERT INTO items VALUES (1, 100, 'x'), (2, 100, 'y'), (3, 102, 'z');");

        // Mirrors the classic Northwind "Invoices" view: a JOIN (b JOIN (c JOIN d ON ...) ON ...) ON ...
        var rows = ReadRows(
            connection,
            "SELECT o.id, u.name, i.sku FROM orders o INNER JOIN (users u INNER JOIN (items i INNER JOIN orders o2 ON i.order_id = o2.id) ON u.id = o2.user_id) ON o.id = i.order_id ORDER BY i.sku;");

        rows.Should().HaveCount(3);
        rows[0].Should().Equal(SqlValue.Integer(100), SqlValue.Text("ada"), SqlValue.Text("x"));
        rows[1].Should().Equal(SqlValue.Integer(100), SqlValue.Text("ada"), SqlValue.Text("y"));
        rows[2].Should().Equal(SqlValue.Integer(102), SqlValue.Text("bo"), SqlValue.Text("z"));
    }

    private static void SeedOrders(EmbeddedConnection connection)
    {
        Execute(connection, "CREATE TABLE users(id INTEGER, name TEXT);");
        Execute(connection, "CREATE TABLE orders(id INTEGER, user_id INTEGER, amount INTEGER);");
        Execute(connection, "INSERT INTO users VALUES (1, 'ada'), (2, 'bo');");
        Execute(connection, "INSERT INTO orders VALUES (100, 1, 10), (101, 1, 20), (102, 2, 30);");
    }

    private static void SeedLeftJoinCases(EmbeddedConnection connection)
    {
        Execute(connection, "CREATE TABLE l(key INTEGER, label TEXT);");
        Execute(connection, "CREATE TABLE r(key INTEGER, amount INTEGER);");
        Execute(connection, "INSERT INTO l VALUES ('Ada', 'first'), (NULL, 'null-key'), ('Bo', 'third'), ('Cy', 'unmatched'), ('001', 'affinity');");
        Execute(connection, "INSERT INTO r VALUES ('ADA', 10), ('ada', 11), (NULL, 99), ('BO', 20), ('Zoe', 30), (1, 40);");
    }

    private static List<(SqlValue, SqlValue)> DrainPairs(EmbeddedStatement statement)
    {
        var rows = new List<(SqlValue, SqlValue)>();
        while (statement.Step() == StatementStepResult.Row)
            rows.Add((statement.GetValue(0), statement.GetValue(1)));

        return rows;
    }

    private static List<SqlValue[]> DrainRows(EmbeddedStatement statement)
    {
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var row = new SqlValue[statement.GetColumnCount()];
            for (var ordinal = 0; ordinal < row.Length; ordinal++)
                row[ordinal] = statement.GetValue(ordinal);

            rows.Add(row);
        }

        return rows;
    }

    private static void AssertEvaluatorOwned(EmbeddedConnection connection, string sql)
    {
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + sql))!
            .Message.Should().Contain("EXPLAIN is only supported");
        ReadRows(connection, "EXPLAIN QUERY PLAN " + sql)[0][3]
            .Should().Be(SqlValue.Text("MANAGED EVALUATOR FALLBACK"));
    }

    private static IEnumerable<string> Opcodes(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[1].AsText());

    private static IEnumerable<string> Comments(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[6].AsText());

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

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

    private static string[] ColumnNames(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var names = new string[statement.GetColumnCount()];
        for (var ordinal = 0; ordinal < names.Length; ordinal++)
            names[ordinal] = statement.GetColumnName(ordinal);

        return names;
    }
}
