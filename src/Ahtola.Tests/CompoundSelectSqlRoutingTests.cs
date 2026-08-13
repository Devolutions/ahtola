using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

// Proves that EmbeddedDatabase routes the supported compound-SELECT subset -- same-operator
// UNION ALL and UNION/DISTINCT chains with no ORDER BY/LIMIT/OFFSET -- through the real
// CompoundProgramBuilder bytecode (sequenced term programs plus, for UNION, the DistinctResultRow
// opcode) while keeping the results byte-identical to the tree-walking evaluator. As in the
// aggregate routing tests, EXPLAIN is the ground truth for "was this lowered to bytecode?": a routed
// compound dumps the sequenced opcode stream, while every deliberate fallback shape throws because
// EXPLAIN only describes lowered programs. Fallback tests also assert the evaluator still produces
// the correct value or its exact error.
public class CompoundSelectSqlRoutingTests
{
    [Test]
    public void UnionAllConcatenatesTermsInScanOrderWithDuplicates()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedSingleColumn(connection);

        Column0(ReadRows(connection, "SELECT a FROM t UNION ALL SELECT a FROM u;"))
            .Should().Equal(
                SqlValue.Integer(1),
                SqlValue.Integer(2),
                SqlValue.Integer(2),
                SqlValue.Integer(3),
                SqlValue.Integer(2),
                SqlValue.Integer(4));
    }

    [Test]
    public void UnionDistinctTraversesItsMaterializedSetInKeyOrder()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedSingleColumn(connection);

        Column0(ReadRows(connection, "SELECT a FROM t UNION SELECT a FROM u;"))
            .Should().Equal(
                SqlValue.Integer(1),
                SqlValue.Integer(2),
                SqlValue.Integer(3),
                SqlValue.Integer(4));
    }

    [Test]
    public void UnionDistinctRoutesAndOrdersFullRowsBeforeAnOuterOrderByCanBreakTies()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT);");
        Execute(connection, "CREATE TABLE u(a INTEGER, b TEXT);");
        Execute(connection, "INSERT INTO t VALUES (2, 'z'), (1, 'z');");
        Execute(connection, "INSERT INTO u VALUES (1, 'a'), (3, 'x');");

        var rows = ReadRows(connection, "SELECT a, b FROM t UNION SELECT a, b FROM u;");
        rows.Should().HaveCount(4);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("a"));
        rows[1].Should().Equal(SqlValue.Integer(1), SqlValue.Text("z"));
        rows[2].Should().Equal(SqlValue.Integer(2), SqlValue.Text("z"));
        rows[3].Should().Equal(SqlValue.Integer(3), SqlValue.Text("x"));

        Assert.DoesNotThrow(() => ReadRows(connection, "EXPLAIN SELECT a, b FROM t UNION SELECT a, b FROM u;"));
    }

    [Test]
    public void UnionDistinctDeduplicatesOnTheWholeRowTuple()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedTwoColumn(connection);

        var rows = ReadRows(connection, "SELECT a, b FROM t UNION SELECT a, b FROM u;");

        rows.Should().HaveCount(4);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("x"));
        rows[1].Should().Equal(SqlValue.Integer(2), SqlValue.Text("y"));
        rows[2].Should().Equal(SqlValue.Integer(3), SqlValue.Text("z"));
        rows[3].Should().Equal(SqlValue.Integer(4), SqlValue.Text("w"));
    }

    [Test]
    public void UnionDistinctUsesTheFinalTermRepresentativeForCollatedDuplicates()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE words(value TEXT COLLATE NOCASE);");
        Execute(connection, "INSERT INTO words VALUES ('first');");

        Column0(ReadRows(connection, "SELECT value FROM words UNION SELECT 'FIRST' COLLATE BINARY;"))
            .Should().Equal(SqlValue.Text("FIRST"));
    }

    [Test]
    public void ChainedUnionAllFlattensEveryTermInOrder()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedSingleColumn(connection);

        Column0(ReadRows(connection, "SELECT a FROM t UNION ALL SELECT a FROM u UNION ALL SELECT a FROM t;"))
            .Should().Equal(
                SqlValue.Integer(1),
                SqlValue.Integer(2),
                SqlValue.Integer(2),
                SqlValue.Integer(3),
                SqlValue.Integer(2),
                SqlValue.Integer(4),
                SqlValue.Integer(1),
                SqlValue.Integer(2),
                SqlValue.Integer(2),
                SqlValue.Integer(3));
    }

    [Test]
    public void ChainedUnionDistinctFoldsToASingleDistinctSet()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedSingleColumn(connection);

        // A UNION B UNION C is one flattened distinct set; the routed program must dedup exactly like
        // the evaluator's left-associative fold.
        Column0(ReadRows(connection, "SELECT a FROM t UNION SELECT a FROM u UNION SELECT a FROM t;"))
            .Should().Equal(
                SqlValue.Integer(1),
                SqlValue.Integer(2),
                SqlValue.Integer(3),
                SqlValue.Integer(4));
    }

    [Test]
    public void EmptyTermsBehaveAsIdentityForUnionAll()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedSingleColumn(connection);
        Execute(connection, "CREATE TABLE e(a INTEGER);");

        ReadRows(connection, "SELECT a FROM e UNION ALL SELECT a FROM e;")
            .Should().BeEmpty();

        Column0(ReadRows(connection, "SELECT a FROM e UNION ALL SELECT a FROM t;"))
            .Should().Equal(
                SqlValue.Integer(1),
                SqlValue.Integer(2),
                SqlValue.Integer(2),
                SqlValue.Integer(3));

        Column0(ReadRows(connection, "SELECT a FROM t UNION ALL SELECT a FROM e;"))
            .Should().Equal(
                SqlValue.Integer(1),
                SqlValue.Integer(2),
                SqlValue.Integer(2),
                SqlValue.Integer(3));
    }

    [Test]
    public void EmptyTermsBehaveAsIdentityForUnionDistinct()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedSingleColumn(connection);
        Execute(connection, "CREATE TABLE e(a INTEGER);");

        Column0(ReadRows(connection, "SELECT a FROM e UNION SELECT a FROM t;"))
            .Should().Equal(
                SqlValue.Integer(1),
                SqlValue.Integer(2),
                SqlValue.Integer(3));
    }

    [Test]
    public void LiteralOnlyUnionAllRoutesAndPreservesFirstTermColumnName()
    {
        using var connection = new EmbeddedDatabase().Connect();

        Column0(ReadRows(connection, "SELECT 1 UNION ALL SELECT 2;"))
            .Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));

        // Column labels come from the first term exactly as the evaluator fold reports them, so the
        // routed program stays lowered (EXPLAIN succeeds) even without a table source.
        Opcodes(ReadRows(connection, "EXPLAIN SELECT 1 UNION ALL SELECT 2;"))
            .Should().Contain("ResultRow").And.Contain("Halt");
    }

    [Test]
    public void TypeofTermsRouteThroughUnionAllInSourceOrder()
    {
        using var connection = new EmbeddedDatabase().Connect();

        Column0(ReadRows(connection, "SELECT typeof('first') UNION ALL SELECT typeof(2);"))
            .Should().Equal(SqlValue.Text("text"), SqlValue.Text("integer"));

        var opcodes = Opcodes(ReadRows(
            connection,
            "EXPLAIN SELECT typeof('first') UNION ALL SELECT typeof(2);")).ToList();
        opcodes.Count(opcode => opcode == "Function").Should().Be(2);
        opcodes.Count(opcode => opcode == "ResultRow").Should().Be(2);
    }

    [Test]
    public void InvalidTypeofArityKeepsUnionAllOnTheEvaluator()
    {
        const string query = "SELECT typeof() UNION ALL SELECT typeof('later')";
        using var connection = new EmbeddedDatabase().Connect();

        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, query))!
            .Message.Should().Be("wrong number of arguments to function typeof()");
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + query));
    }

    [Test]
    public void OverriddenTypeofKeepsUnionAllOnTheEvaluator()
    {
        var calls = 0;
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "typeof",
            1,
            values =>
            {
                calls++;
                return SqlValue.Text($"callback:{values[0].AsText()}");
            });
        using var connection = database.Connect();

        Column0(ReadRows(connection, "SELECT typeof('first') UNION ALL SELECT 'second';"))
            .Should().Equal(SqlValue.Text("callback:first"), SqlValue.Text("second"));
        calls.Should().Be(1);
        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT typeof('first') UNION ALL SELECT 'second';"));
    }

    [Test]
    public void CompoundColumnNamesComeFromTheFirstTerm()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedTwoColumn(connection);

        ColumnNames(connection, "SELECT a, b FROM t UNION ALL SELECT a, b FROM u;")
            .Should().Equal("a", "b");
        ColumnNames(connection, "SELECT a, b FROM t UNION SELECT a, b FROM u;")
            .Should().Equal("a", "b");
    }

    [Test]
    public void StarUnionAllRoutesAndReturnsEveryColumn()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedTwoColumn(connection);

        var rows = ReadRows(connection, "SELECT * FROM t UNION ALL SELECT * FROM u;");

        rows.Should().HaveCount(6);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("x"));
        rows[5].Should().Equal(SqlValue.Integer(4), SqlValue.Text("w"));

        // No RowsEqual/collation dependency, so the star-expanded UNION ALL lowers to bytecode.
        Assert.DoesNotThrow(() => ReadRows(connection, "EXPLAIN SELECT * FROM t UNION ALL SELECT * FROM u;"));
    }

    [Test]
    public void ResetReplaysTheCompoundProgramWithAppendedRows()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedSingleColumn(connection);

        using var statement = connection.Prepare("SELECT a FROM t UNION ALL SELECT a FROM u;");

        Drain(statement).Should().Equal(
            SqlValue.Integer(1),
            SqlValue.Integer(2),
            SqlValue.Integer(2),
            SqlValue.Integer(3),
            SqlValue.Integer(2),
            SqlValue.Integer(4));

        Execute(connection, "INSERT INTO u VALUES (5);");

        statement.Reset();
        Drain(statement).Should().Equal(
            SqlValue.Integer(1),
            SqlValue.Integer(2),
            SqlValue.Integer(2),
            SqlValue.Integer(3),
            SqlValue.Integer(2),
            SqlValue.Integer(4),
            SqlValue.Integer(5));
    }

    [Test]
    public void UnionAllExplainEmitsAResultRowPerTermAndNoDistinctSet()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedSingleColumn(connection);

        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN SELECT a FROM t UNION ALL SELECT a FROM u;")).ToList();

        opcodes.Should().Contain("OpenReadCursor").And.Contain("Halt");
        opcodes.Count(opcode => opcode == "ResultRow").Should().Be(2);
        opcodes.Should().NotContain("DistinctResultRow");
    }

    [Test]
    public void UnionDistinctExplainMaterializesAndTraversesOneSortedSet()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedSingleColumn(connection);

        var rows = ReadRows(connection, "EXPLAIN SELECT a FROM t UNION SELECT a FROM u;");
        var opcodes = Opcodes(rows).ToList();

        opcodes.Count(opcode => opcode == "RowSetInsert").Should().Be(2);
        opcodes.Count(opcode => opcode == "ResultRow").Should().Be(1);
        opcodes.Should().NotContain("DistinctResultRow");
        opcodes.Should().Contain("RowSetRewind").And.Contain("RowSetNext");
        Comments(rows).Where(comment => comment.Contains("row set 0")).Should().HaveCount(4);
    }

    [Test]
    public void OrderByFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedSingleColumn(connection);

        Column0(ReadRows(connection, "SELECT a FROM t UNION ALL SELECT a FROM u ORDER BY 1;"))
            .Should().Equal(
                SqlValue.Integer(1),
                SqlValue.Integer(2),
                SqlValue.Integer(2),
                SqlValue.Integer(2),
                SqlValue.Integer(3),
                SqlValue.Integer(4));

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT a FROM t UNION ALL SELECT a FROM u ORDER BY 1;"));
    }

    [Test]
    public void LimitNowLowersToBytecode()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedSingleColumn(connection);

        Column0(ReadRows(connection, "SELECT a FROM t UNION ALL SELECT a FROM u LIMIT 2;"))
            .Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));

        // UNION ALL splices per-term emitters, so every spliced ResultRow shares the same counter.
        Opcodes(ReadRows(connection, "EXPLAIN SELECT a FROM t UNION ALL SELECT a FROM u LIMIT 2;"))
            .Should().ContainInOrder("LimitGate", "ResultRow");
    }

    [Test]
    public void LimitOffsetNowLowersToBytecode()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedSingleColumn(connection);

        Column0(ReadRows(connection, "SELECT a FROM t UNION ALL SELECT a FROM u LIMIT 2 OFFSET 1;"))
            .Should().Equal(SqlValue.Integer(2), SqlValue.Integer(2));

        Opcodes(ReadRows(connection, "EXPLAIN SELECT a FROM t UNION ALL SELECT a FROM u LIMIT 2 OFFSET 1;"))
            .Should().ContainInOrder("OffsetGate", "LimitGate", "ResultRow");
    }

    [Test]
    public void IntersectRoutesToSetOperationBytecode()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedSingleColumn(connection);

        Column0(ReadRows(connection, "SELECT a FROM t INTERSECT SELECT a FROM u;"))
            .Should().Equal(SqlValue.Integer(2));

        var rows = ReadRows(connection, "EXPLAIN SELECT a FROM t INTERSECT SELECT a FROM u;");
        var opcodes = Opcodes(rows).ToList();

        opcodes.Count(opcode => opcode == "RowSetInsert").Should().Be(2);
        opcodes.Count(opcode => opcode == "CompoundResultRow").Should().Be(1);
        opcodes.Should().Contain("RowSetRewind").And.Contain("RowSetNext");
        opcodes.Should().NotContain("ResultRow").And.NotContain("DistinctResultRow");
        Comments(rows).Should().Contain(
            "output=r[2] if new to distinct set 2 and present in all of sets {1}");
    }

    [Test]
    public void ExceptRoutesToSetOperationBytecode()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedSingleColumn(connection);

        Column0(ReadRows(connection, "SELECT a FROM t EXCEPT SELECT a FROM u;"))
            .Should().Equal(SqlValue.Integer(1), SqlValue.Integer(3));

        var rows = ReadRows(connection, "EXPLAIN SELECT a FROM t EXCEPT SELECT a FROM u;");
        var opcodes = Opcodes(rows).ToList();

        opcodes.Count(opcode => opcode == "RowSetInsert").Should().Be(2);
        opcodes.Count(opcode => opcode == "CompoundResultRow").Should().Be(1);
        opcodes.Should().Contain("RowSetRewind").And.Contain("RowSetNext");
        opcodes.Should().NotContain("ResultRow").And.NotContain("DistinctResultRow");
        Comments(rows).Should().Contain(
            "output=r[2] if new to distinct set 2 and absent from all of sets {1}");
    }

    [Test]
    public void MixedOperatorChainFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedSingleColumn(connection);

        // UNION ALL then UNION mixes operators, so the router declines and the evaluator's
        // left-associative fold produces the values.
        Column0(ReadRows(connection, "SELECT a FROM t UNION ALL SELECT a FROM u UNION SELECT a FROM t;"))
            .Should().Equal(
                SqlValue.Integer(1),
                SqlValue.Integer(2),
                SqlValue.Integer(3),
                SqlValue.Integer(4));

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT a FROM t UNION ALL SELECT a FROM u UNION SELECT a FROM t;"));
    }

    [Test]
    public void MismatchedColumnCountRaisesTheEvaluatorError()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedTwoColumn(connection);

        var error = Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "SELECT a FROM t UNION ALL SELECT a, b FROM u;"))!;
        error.Message.Should().Be(
            "SELECTs to the left and right of a compound operator do not have the same number of result columns");

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT a FROM t UNION ALL SELECT a, b FROM u;"));
    }

    [Test]
    public void StarUnionDistinctRoutesWithExpandedMetadata()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedTwoColumn(connection);

        var rows = ReadRows(connection, "SELECT * FROM t UNION SELECT * FROM u;");
        rows.Should().HaveCount(4);
        Assert.DoesNotThrow(
            () => ReadRows(connection, "EXPLAIN SELECT * FROM t UNION SELECT * FROM u;"));
    }

    [Test]
    public void CustomCollationCallbacksDoNotRunBeforeLaterTermErrors()
    {
        var routedCandidate = RunCustomCollationError(
            "SELECT value COLLATE tracking FROM first_rows UNION SELECT abs(value) FROM later_error;");
        var forcedEvaluator = RunCustomCollationError(
            "SELECT value COLLATE tracking FROM first_rows UNION SELECT abs(value) FROM later_error ORDER BY 1;");

        routedCandidate.Error.Should().Be(forcedEvaluator.Error).And.Be("integer overflow");
        routedCandidate.CallbackCount.Should().Be(forcedEvaluator.CallbackCount).And.Be(0);
    }

    private static (string Error, int CallbackCount) RunCustomCollationError(string sql)
    {
        var callbackCount = 0;
        var database = new EmbeddedDatabase();
        database.RegisterCollation(
            "tracking",
            (left, right) =>
            {
                callbackCount++;
                return string.CompareOrdinal(left, right);
            });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE first_rows(value);");
        Execute(connection, "INSERT INTO first_rows VALUES ('same'), ('same');");
        Execute(connection, "CREATE TABLE later_error(value);");
        Execute(connection, "INSERT INTO later_error VALUES (-9223372036854775808);");

        var error = Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, sql))!;

        if (!sql.Contains("ORDER BY", StringComparison.Ordinal))
        {
            Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + sql))!
                .Message.Should().Contain("EXPLAIN is only supported");
        }

        return (error.Message, callbackCount);
    }

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
