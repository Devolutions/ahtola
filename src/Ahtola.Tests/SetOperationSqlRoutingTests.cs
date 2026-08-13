using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

// Proves that EmbeddedDatabase routes the supported same-operator INTERSECT and EXCEPT compound-SELECT
// subset through the real CompoundProgramBuilder bytecode (source-ordered row-set capture followed by
// first-set membership iteration) while keeping results byte-identical to the tree-walking
// evaluator's ApplyIntersect/ApplyExcept fold. EXPLAIN is the ground truth for "was this lowered to
// bytecode?": a routed compound dumps the sequenced opcode stream, while every deliberate fallback shape
// throws because EXPLAIN only describes lowered programs. Fallback tests also assert the evaluator still
// produces the correct value or its exact error. This complements CompoundSelectSqlRoutingTests, which
// covers the UNION ALL / UNION-DISTINCT routes.
public class SetOperationSqlRoutingTests
{
    // ---- INTERSECT routes -------------------------------------------------------------------------

    [Test]
    public void IntersectTraversesTheMaterializedSetInKeyOrder()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedSingleColumn(connection);

        // t = {1,2,2,3}; u = {2,4}. The only shared row is {2}.
        Column0(ReadRows(connection, "SELECT a FROM t INTERSECT SELECT a FROM u;"))
            .Should().Equal(SqlValue.Integer(2));
    }

    [Test]
    public void IntersectAndExceptRouteAndOrderSurvivingRowsByTheirFullTuple()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE TABLE u(a INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (3), (1), (2);");
        Execute(connection, "INSERT INTO u VALUES (1), (3);");

        Column0(ReadRows(connection, "SELECT a FROM t INTERSECT SELECT a FROM u;"))
            .Should().Equal(SqlValue.Integer(1), SqlValue.Integer(3));
        Column0(ReadRows(connection, "SELECT a FROM t EXCEPT SELECT a FROM u;"))
            .Should().Equal(SqlValue.Integer(2));

        Execute(connection, "DELETE FROM u;");
        Execute(connection, "INSERT INTO u VALUES (2);");
        Column0(ReadRows(connection, "SELECT a FROM t EXCEPT SELECT a FROM u;"))
            .Should().Equal(SqlValue.Integer(1), SqlValue.Integer(3));
    }

    [Test]
    public void IntersectDeduplicatesOnTheWholeRowTuple()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedTwoColumn(connection);

        // t = {(1,x),(2,y),(2,y),(3,z)}; u = {(2,y),(4,w)}. Whole-tuple intersect => {(2,y)}.
        var rows = ReadRows(connection, "SELECT a, b FROM t INTERSECT SELECT a, b FROM u;");

        rows.Should().HaveCount(1);
        rows[0].Should().Equal(SqlValue.Integer(2), SqlValue.Text("y"));
    }

    [Test]
    public void ChainedIntersectFlattensToPresentInAllProbeSets()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedSingleColumn(connection);
        Execute(connection, "CREATE TABLE v(a INTEGER);");
        Execute(connection, "INSERT INTO v VALUES (2), (3), (5);");

        // t INTERSECT u INTERSECT v: distinct t rows present in both u and v, in first-term order => {2}.
        Column0(ReadRows(connection, "SELECT a FROM t INTERSECT SELECT a FROM u INTERSECT SELECT a FROM v;"))
            .Should().Equal(SqlValue.Integer(2));
    }

    [Test]
    public void IntersectTreatsNullsAsEqual()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedNullableSingleColumn(connection);

        // tn = {NULL,1,2}; un = {NULL,2,3}. NULL==NULL for set ops, so distinct shared rows are {NULL,2}.
        var rows = ReadRows(connection, "SELECT a FROM tn INTERSECT SELECT a FROM un;");

        rows.Should().HaveCount(2);
        rows[0][0].Kind.Should().Be(SqlValueKind.Null);
        rows[1][0].Should().Be(SqlValue.Integer(2));
    }

    // ---- EXCEPT routes ----------------------------------------------------------------------------

    [Test]
    public void ExceptKeepsDistinctFirstTermRowsAbsentFromSecond()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedSingleColumn(connection);

        // t = {1,2,2,3}; u = {2,4}. Distinct t rows not in u => {1,3}.
        Column0(ReadRows(connection, "SELECT a FROM t EXCEPT SELECT a FROM u;"))
            .Should().Equal(SqlValue.Integer(1), SqlValue.Integer(3));
    }

    [Test]
    public void ExceptDeduplicatesOnTheWholeRowTuple()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedTwoColumn(connection);

        // t rows not present in u, de-duplicated on the whole tuple => {(1,x),(3,z)}.
        var rows = ReadRows(connection, "SELECT a, b FROM t EXCEPT SELECT a, b FROM u;");

        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("x"));
        rows[1].Should().Equal(SqlValue.Integer(3), SqlValue.Text("z"));
    }

    [Test]
    public void ChainedExceptFlattensToAbsentFromAllProbeSets()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedSingleColumn(connection);
        Execute(connection, "CREATE TABLE v(a INTEGER);");
        Execute(connection, "INSERT INTO v VALUES (2), (3), (5);");

        // t EXCEPT u EXCEPT v == t minus (u UNION v): the only surviving row is {1}.
        Column0(ReadRows(connection, "SELECT a FROM t EXCEPT SELECT a FROM u EXCEPT SELECT a FROM v;"))
            .Should().Equal(SqlValue.Integer(1));
    }

    [Test]
    public void ExceptTreatsNullsAsEqual()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedNullableSingleColumn(connection);

        // tn = {NULL,1,2}; un = {NULL,2,3}. NULL and 2 are removed, leaving {1}.
        Column0(ReadRows(connection, "SELECT a FROM tn EXCEPT SELECT a FROM un;"))
            .Should().Equal(SqlValue.Integer(1));
    }

    // ---- Output metadata / lifecycle --------------------------------------------------------------

    [Test]
    public void CompoundColumnNamesComeFromTheFirstTerm()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedTwoColumn(connection);

        ColumnNames(connection, "SELECT a AS x, b FROM t INTERSECT SELECT a, b FROM u;")
            .Should().Equal("x", "b");
        ColumnNames(connection, "SELECT a AS x, b FROM t EXCEPT SELECT a, b FROM u;")
            .Should().Equal("x", "b");
    }

    [Test]
    public void ResetReplaysTheIntersectProgramWithAppendedRows()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedSingleColumn(connection);

        using var statement = connection.Prepare("SELECT a FROM t INTERSECT SELECT a FROM u;");

        Drain(statement).Should().Equal(SqlValue.Integer(2));

        // Adding 3 to u makes t's distinct 3 newly present in the probe set; Reset must recompile/replay.
        Execute(connection, "INSERT INTO u VALUES (3);");

        statement.Reset();
        Drain(statement).Should().Equal(SqlValue.Integer(2), SqlValue.Integer(3));
    }

    [Test]
    public void ParameterizedIntersectRoutesAndReExecutesOnReset()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedSingleColumn(connection);

        using var statement = connection.Prepare("SELECT a FROM t WHERE a >= ?1 INTERSECT SELECT a FROM u;");

        statement.Bind(1, SqlValue.Integer(1));
        Drain(statement).Should().Equal(SqlValue.Integer(2));

        // Rebinding the parameter must re-filter the primary term: a >= 3 yields {3}, which is absent
        // from u, so the intersect is empty.
        statement.Reset();
        statement.Bind(1, SqlValue.Integer(3));
        Drain(statement).Should().BeEmpty();
    }

    // ---- EXPLAIN ground truth ---------------------------------------------------------------------

    [Test]
    public void IntersectExplainEmitsRowSetInsertThenCompoundResultRow()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedSingleColumn(connection);

        var rows = ReadRows(connection, "EXPLAIN SELECT a FROM t INTERSECT SELECT a FROM u;");
        var opcodes = Opcodes(rows).ToList();

        // Both terms materialize in source order before the first set is iterated for membership output.
        opcodes.Count(opcode => opcode == "RowSetInsert").Should().Be(2);
        opcodes.Count(opcode => opcode == "CompoundResultRow").Should().Be(1);
        opcodes.Should().NotContain("ResultRow").And.NotContain("DistinctResultRow");
        opcodes.Should().Contain("RowSetRewind").And.Contain("RowSetNext");
        opcodes.Should().Contain("OpenReadCursor").And.Contain("Halt");

        Comments(rows).Should().Contain("insert r[0] into row set 0");
        Comments(rows).Should().Contain(
            "output=r[2] if new to distinct set 2 and present in all of sets {1}");
        P4(rows).Should().Contain("sets {1}");
    }

    [Test]
    public void ExceptExplainEmitsAbsentFromAllCompoundResultRow()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedSingleColumn(connection);

        var rows = ReadRows(connection, "EXPLAIN SELECT a FROM t EXCEPT SELECT a FROM u;");
        var opcodes = Opcodes(rows).ToList();

        opcodes.Count(opcode => opcode == "RowSetInsert").Should().Be(2);
        opcodes.Count(opcode => opcode == "CompoundResultRow").Should().Be(1);
        opcodes.Should().NotContain("ResultRow").And.NotContain("DistinctResultRow");

        Comments(rows).Should().Contain(
            "output=r[2] if new to distinct set 2 and absent from all of sets {1}");
    }

    [Test]
    public void ChainedIntersectExplainEmitsOneProbeSetPerNonPrimaryTerm()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedSingleColumn(connection);
        Execute(connection, "CREATE TABLE v(a INTEGER);");
        Execute(connection, "INSERT INTO v VALUES (2), (3), (5);");

        var rows = ReadRows(
            connection,
            "EXPLAIN SELECT a FROM t INTERSECT SELECT a FROM u INTERSECT SELECT a FROM v;");
        var opcodes = Opcodes(rows).ToList();

        opcodes.Count(opcode => opcode == "RowSetInsert").Should().Be(3);
        opcodes.Count(opcode => opcode == "CompoundResultRow").Should().Be(1);
        opcodes.Count(opcode => opcode == "OpenReadCursor").Should().Be(3);

        Comments(rows).Should().Contain(
            "output=r[3] if new to distinct set 3 and present in all of sets {1,2}");
        P4(rows).Should().Contain("sets {1,2}");
    }

    // ---- Deliberate fallbacks (kept on the evaluator) ---------------------------------------------

    [Test]
    public void IntersectWithOrderByFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedSingleColumn(connection);

        Column0(ReadRows(connection, "SELECT a FROM t INTERSECT SELECT a FROM u ORDER BY 1;"))
            .Should().Equal(SqlValue.Integer(2));

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT a FROM t INTERSECT SELECT a FROM u ORDER BY 1;"));
    }

    [Test]
    public void ExceptWithLimitNowLowersToBytecode()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedSingleColumn(connection);

        Column0(ReadRows(connection, "SELECT a FROM t EXCEPT SELECT a FROM u LIMIT 1;"))
            .Should().Equal(SqlValue.Integer(1));

        // The compound program now composes with the limit/offset counters: the row-set probe runs
        // as a RowGate ahead of them so a suppressed candidate never consumes the budget.
        Opcodes(ReadRows(connection, "EXPLAIN SELECT a FROM t EXCEPT SELECT a FROM u LIMIT 1;"))
            .Should().ContainInOrder("RowGate", "LimitGate", "ResultRow");
    }

    [Test]
    public void IntersectWithOffsetNowLowersToBytecode()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedSingleColumn(connection);

        Column0(ReadRows(connection, "SELECT a FROM t EXCEPT SELECT a FROM u LIMIT 1 OFFSET 1;"))
            .Should().Equal(SqlValue.Integer(3));

        Opcodes(ReadRows(connection, "EXPLAIN SELECT a FROM t EXCEPT SELECT a FROM u LIMIT 1 OFFSET 1;"))
            .Should().ContainInOrder("RowGate", "OffsetGate", "LimitGate", "ResultRow");
    }

    [Test]
    public void MixedIntersectExceptChainFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedSingleColumn(connection);
        Execute(connection, "CREATE TABLE v(a INTEGER);");
        Execute(connection, "INSERT INTO v VALUES (2), (3), (5);");

        // (t INTERSECT u) EXCEPT v mixes operators, so the router declines and the evaluator's
        // left-associative fold produces the value: (t INTERSECT u)={2}, EXCEPT v ({2,3,5}) => {}.
        ReadRows(connection, "SELECT a FROM t INTERSECT SELECT a FROM u EXCEPT SELECT a FROM v;")
            .Should().BeEmpty();

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(
                connection,
                "EXPLAIN SELECT a FROM t INTERSECT SELECT a FROM u EXCEPT SELECT a FROM v;"));
    }

    [Test]
    public void MixedUnionIntersectChainFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedSingleColumn(connection);

        // (t UNION ALL u) INTERSECT u: evaluator fold => distinct {1,2,3,4} present in u ({2,4}) => {2,4}.
        Column0(ReadRows(connection, "SELECT a FROM t UNION ALL SELECT a FROM u INTERSECT SELECT a FROM u;"))
            .Should().Equal(SqlValue.Integer(2), SqlValue.Integer(4));

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(
                connection,
                "EXPLAIN SELECT a FROM t UNION ALL SELECT a FROM u INTERSECT SELECT a FROM u;"));
    }

    [Test]
    public void StarIntersectRoutesWithExpandedMetadata()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedTwoColumn(connection);

        ReadRows(connection, "SELECT * FROM t INTERSECT SELECT * FROM u;").Should().ContainSingle();
        Assert.DoesNotThrow(
            () => ReadRows(connection, "EXPLAIN SELECT * FROM t INTERSECT SELECT * FROM u;"));
    }

    [Test]
    public void StarExceptRoutesWithExpandedMetadata()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedTwoColumn(connection);

        ReadRows(connection, "SELECT * FROM t EXCEPT SELECT * FROM u;").Should().HaveCount(2);
        Assert.DoesNotThrow(
            () => ReadRows(connection, "EXPLAIN SELECT * FROM t EXCEPT SELECT * FROM u;"));
    }

    [Test]
    public void DistinctTermIntersectRetainsInnerDeduplication()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedSingleColumn(connection);

        Column0(ReadRows(connection, "SELECT DISTINCT a FROM t INTERSECT SELECT a FROM u;"))
            .Should().Equal(SqlValue.Integer(2));

        Opcodes(ReadRows(connection, "EXPLAIN SELECT DISTINCT a FROM t INTERSECT SELECT a FROM u;"))
            .Should().Contain("GuardedRow");
    }

    [TestCase("INTERSECT")]
    [TestCase("EXCEPT")]
    public void ErrorCapableTermsPreserveEvaluatorSourceOrder(string compoundOperator)
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE first_error(value);");
        Execute(connection, "INSERT INTO first_error VALUES (-9223372036854775808);");
        Execute(connection, "CREATE TABLE second_error(value);");
        Execute(connection, "INSERT INTO second_error VALUES ('x');");
        var query =
            $"SELECT abs(value) FROM first_error {compoundOperator} SELECT instr(value) FROM second_error;";
        var forcedEvaluator =
            $"SELECT abs(value) FROM first_error {compoundOperator} SELECT instr(value) FROM second_error ORDER BY 1;";

        var actual = Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, query))!;
        var expected = Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, forcedEvaluator))!;

        actual.Message.Should().Be(expected.Message).And.Be("integer overflow");
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + query))!
            .Message.Should().Contain("EXPLAIN is only supported");
    }

    [TestCase("INTERSECT")]
    [TestCase("EXCEPT")]
    public void ComputedArithmeticTermsRoute(string compoundOperator)
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE arithmetic_left(value);");
        Execute(connection, "INSERT INTO arithmetic_left VALUES ('10');");
        Execute(connection, "CREATE TABLE arithmetic_right(value);");
        Execute(connection, "INSERT INTO arithmetic_right VALUES ('10');");
        var query =
            $"SELECT value + 1 FROM arithmetic_left {compoundOperator} SELECT value + 1 FROM arithmetic_right;";
        var forcedEvaluator =
            $"SELECT value + 1 FROM arithmetic_left {compoundOperator} SELECT value + 1 FROM arithmetic_right ORDER BY 1;";

        var actual = ReadRows(connection, query);
        var expected = ReadRows(connection, forcedEvaluator);

        actual.Should().HaveCount(expected.Count);
        for (var index = 0; index < actual.Count; index++)
            actual[index].Should().Equal(expected[index]);
        Assert.DoesNotThrow(() => ReadRows(connection, "EXPLAIN " + query));
    }

    [Test]
    public void MismatchedColumnCountIntersectRaisesTheEvaluatorError()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedTwoColumn(connection);

        var error = Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "SELECT a, b FROM t INTERSECT SELECT a FROM u;"))!;
        error.Message.Should().Be(
            "SELECTs to the left and right of a compound operator do not have the same number of result columns");

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT a, b FROM t INTERSECT SELECT a FROM u;"));
    }

    [Test]
    public void MismatchedColumnCountExceptRaisesTheEvaluatorError()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedTwoColumn(connection);

        var error = Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "SELECT a, b FROM t EXCEPT SELECT a FROM u;"))!;
        error.Message.Should().Be(
            "SELECTs to the left and right of a compound operator do not have the same number of result columns");
    }

    [Test]
    public void ExplicitCollateProjectionIntersectRoutesWithFirstTermCollation()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(a TEXT);");
        Execute(connection, "INSERT INTO t VALUES ('X'), ('y');");
        Execute(connection, "CREATE TABLE u(a TEXT);");
        Execute(connection, "INSERT INTO u VALUES ('x'), ('Y');");

        // The projection emitter treats COLLATE as a value-preserving wrapper, while the compound
        // equality delegate derives NOCASE from the first term. 'X'~'x' and 'y'~'Y' therefore match,
        // yielding {'X','y'} in NOCASE key order; under BINARY the result would be empty.
        Column0(ReadRows(connection, "SELECT a COLLATE NOCASE FROM t INTERSECT SELECT a FROM u;"))
            .Should().Equal(SqlValue.Text("X"), SqlValue.Text("y"));

        Opcodes(ReadRows(
                connection,
                "EXPLAIN SELECT a COLLATE NOCASE FROM t INTERSECT SELECT a FROM u;"))
            .Should().Contain("RowSetInsert").And.Contain("CompoundResultRow");
    }

    // ---- Seeding / helpers ------------------------------------------------------------------------

    private static void SeedSingleColumn(EmbeddedConnection connection)
    {
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2), (2), (3);");
        Execute(connection, "CREATE TABLE u(a INTEGER);");
        Execute(connection, "INSERT INTO u VALUES (2), (4);");
    }

    private static void SeedTwoColumn(EmbeddedConnection connection)
    {
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT);");
        Execute(connection, "INSERT INTO t VALUES (1, 'x'), (2, 'y'), (2, 'y'), (3, 'z');");
        Execute(connection, "CREATE TABLE u(a INTEGER, b TEXT);");
        Execute(connection, "INSERT INTO u VALUES (2, 'y'), (4, 'w');");
    }

    private static void SeedNullableSingleColumn(EmbeddedConnection connection)
    {
        Execute(connection, "CREATE TABLE tn(a INTEGER);");
        Execute(connection, "INSERT INTO tn VALUES (NULL), (1), (2);");
        Execute(connection, "CREATE TABLE un(a INTEGER);");
        Execute(connection, "INSERT INTO un VALUES (NULL), (2), (3);");
    }

    private static List<SqlValue> Drain(EmbeddedStatement statement)
    {
        var values = new List<SqlValue>();
        while (statement.Step() == StatementStepResult.Row)
            values.Add(statement.GetValue(0));

        return values;
    }

    private static IEnumerable<SqlValue> Column0(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[0]);

    private static IEnumerable<string> Opcodes(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[1].AsText());

    private static IEnumerable<string> Comments(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[6].AsText());

    private static IEnumerable<string> P4(IEnumerable<SqlValue[]> rows)
        => rows.Where(row => row[5].Kind != SqlValueKind.Null).Select(row => row[5].AsText());

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
