using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

// Proves that EmbeddedDatabase routes the supported top-level VALUES subset -- a source-less row list
// whose cells are literals or bare parameters -- through ValuesProgramBuilder bytecode (LoadConstant or
// LoadParameter per cell, one ResultRow per row, a terminating Halt, no cursors) while keeping the
// produced rows and generated column metadata byte-identical to the tree-walking evaluator. As with the
// other SQL routing suites, EXPLAIN is the ground truth for "was this lowered to bytecode?": a routed
// VALUES dumps its opcode stream, while every deliberate fallback shape (computed cells, a VALUES used as
// a derived table or a compound term) throws because EXPLAIN only describes lowered programs. Fallback
// tests also assert the evaluator still produces the correct value or its exact error, and the
// unequal-width case pins the builder's validation onto the evaluator's diagnostic.
public class TopLevelValuesSqlRoutingTests
{
    [Test]
    public void SingleRowLiteralValuesRoutesToBytecode()
    {
        using var connection = new EmbeddedDatabase().Connect();

        var rows = ReadRows(connection, "VALUES (1, 'a', 3.5)");
        rows.Should().HaveCount(1);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("a"), SqlValue.Real(3.5));

        // A source-less literal VALUES lowers to the cursor-less LoadConstant/ResultRow/Halt shape.
        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN VALUES (1, 'a', 3.5)")).ToList();
        opcodes.Count(opcode => opcode == "LoadConstant").Should().Be(3);
        opcodes.Count(opcode => opcode == "ResultRow").Should().Be(1);
        opcodes.Count(opcode => opcode == "Halt").Should().Be(1);
        opcodes.Should().NotContain("OpenReadCursor").And.NotContain("Rewind");
    }

    [Test]
        public void MultiRowLiteralValuesEmitsOpenEphemeralScanInOrder()
    {
        using var connection = new EmbeddedDatabase().Connect();

        Column0(ReadRows(connection, "VALUES (10), (20), (30)"))
            .Should().Equal(SqlValue.Integer(10), SqlValue.Integer(20), SqlValue.Integer(30));

            // Multi-row VALUES lower through BuildEphemeralCells: OpenEphemeral + EphemeralInsert per
            // row, then a single Rewind/Column/ResultRow/Next scan (not one ResultRow per source row).
        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN VALUES (1, 2), (3, 4), (5, 6)")).ToList();
            opcodes.Should().Contain("OpenEphemeral");
            opcodes.Count(opcode => opcode == "EphemeralInsert").Should().Be(3);
            opcodes.Count(opcode => opcode == "LoadConstant").Should().Be(6);
            opcodes.Count(opcode => opcode == "ResultRow").Should().Be(1);
            opcodes.Should().Contain("Rewind").And.Contain("Next");
            opcodes.Count(opcode => opcode == "Halt").Should().Be(1);
        }

    [Test]
    public void ResetReplaysTheRoutedValuesProgram()
    {
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare("VALUES (1), (2), (3)");

        Drain(statement).Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2), SqlValue.Integer(3));

        // A source-less VALUES has no external state, so re-running the emitted program after Reset
        // replays the identical rows -- the reused register block survives the reset intact.
        statement.Reset();
        Drain(statement).Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2), SqlValue.Integer(3));
    }

    [Test]
    public void PreservesEveryLiteralValueKindAcrossTheRoutedProgram()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // Every SQL literal kind must survive the LoadConstant/ResultRow round trip unchanged:
        // a max-value integer, a real, unicode text, a blob (X'..'), and NULL.
        var row = ReadRows(connection, "VALUES (9223372036854775807, 2.5, 'π', X'0102FF', NULL)").Single();

        row[0].Should().Be(SqlValue.Integer(long.MaxValue));
        row[1].Should().Be(SqlValue.Real(2.5));
        row[2].Should().Be(SqlValue.Text("π"));
        row[3].Kind.Should().Be(SqlValueKind.Blob);
        row[3].AsBlob().ToArray().Should().Equal(0x01, 0x02, 0xFF);
        row[4].Kind.Should().Be(SqlValueKind.Null);

        // All cells are literals, so the whole statement lowers to bytecode.
        Assert.DoesNotThrow(
            () => ReadRows(connection, "EXPLAIN VALUES (9223372036854775807, 2.5, 'π', X'0102FF', NULL)"));
    }

    [Test]
    public void GeneratedColumnNamesAreColumn1ToColumnN()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // The routed program carries no column notion; the database layer names them column1..columnN
        // through DescribeValues, exactly as the evaluator does, for both single- and multi-row forms.
        ColumnNames(connection, "VALUES (1, 2, 3)").Should().Equal("column1", "column2", "column3");
        ColumnNames(connection, "VALUES (1, 2), (3, 4)").Should().Equal("column1", "column2");
    }

    [Test]
    public void ParameterizedValuesRouteToLateBoundBytecode()
    {
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare("VALUES (?1, ?2), (?2, ?1)");
        statement.Bind(1, SqlValue.Integer(7));
        statement.Bind(2, SqlValue.Text("z"));

        // The routed program resolves the bound parameters per execution and produces the rows.
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(7));
        statement.GetValue(1).Should().Be(SqlValue.Text("z"));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Text("z"));
        statement.GetValue(1).Should().Be(SqlValue.Integer(7));
        statement.Step().Should().Be(StatementStepResult.Done);

        // Parameter cells lower to LoadParameter rather than baking their bound values as constants.
        using var explain = connection.Prepare("EXPLAIN VALUES (?1, ?2)");
        explain.Bind(1, SqlValue.Integer(7));
        explain.Bind(2, SqlValue.Text("z"));
        var opcodes = new List<string>();
        while (explain.Step() == StatementStepResult.Row)
            opcodes.Add(explain.GetValue(1).AsText());

        opcodes.Count(opcode => opcode == "LoadParameter").Should().Be(2);
        opcodes.Count(opcode => opcode == "ResultRow").Should().Be(1);
        opcodes.Count(opcode => opcode == "Halt").Should().Be(1);
        opcodes.Should().NotContain("LoadConstant").And.NotContain("OpenReadCursor").And.NotContain("Rewind");
    }

    [Test]
    public void ComputedCellsFallBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // Arithmetic is a computed expression, evaluated by the tree walker, so the value is correct
        // but the statement is not lowered.
        ReadRows(connection, "VALUES (1 + 1, 2 * 3)").Single()
            .Should().Equal(SqlValue.Integer(2), SqlValue.Integer(6));
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN VALUES (1 + 1, 2 * 3)"));

        // A negated numeric literal parses as 0 - n (a computed expression), so it also stays on the
        // evaluator rather than being baked as a constant.
        ReadRows(connection, "VALUES (-1)").Single().Should().Equal(SqlValue.Integer(-1));
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN VALUES (-1)"));
    }

    [Test]
    public void ValuesAsDerivedTableStaysOnEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // A VALUES used as a derived table is reached through the scan pipeline, never the top-level
        // router, so it produces the correct rows on the evaluator and the statement is not lowered.
        var rows = ReadRows(connection, "SELECT column1, column2 FROM (VALUES (1, 10), (2, 20))");
        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(10));
        rows[1].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(20));

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT column1, column2 FROM (VALUES (1, 10), (2, 20))"));
    }

    [Test]
    public void ValuesAsCompoundTermRoutes()
    {
        using var connection = new EmbeddedDatabase().Connect();

        Column0(ReadRows(connection, "SELECT 1 AS x UNION ALL VALUES (2)"))
            .Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));

        Assert.DoesNotThrow(
            () => ReadRows(connection, "EXPLAIN SELECT 1 AS x UNION ALL VALUES (2)"));
    }

    [Test]
    public void TopLevelAndBareDerivedValuesRoute()
    {
        using var connection = new EmbeddedDatabase().Connect();

        Assert.DoesNotThrow(() => ReadRows(connection, "EXPLAIN VALUES (1, 2)"));
        Assert.DoesNotThrow(() => ReadRows(connection, "EXPLAIN SELECT * FROM (VALUES (1, 2))"));
    }

    [Test]
    public void UnequalRowWidthRaisesTheExactEvaluatorDiagnostic()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // The builder's width validation is mapped onto the evaluator's exact message, whether the
        // later row is narrower or wider than the first, and on both the execution and EXPLAIN paths.
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "VALUES (1, 2), (3)"))!
            .Message.Should().Be("all VALUES must have the same number of terms");
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "VALUES (1), (2, 3)"))!
            .Message.Should().Be("all VALUES must have the same number of terms");
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN VALUES (1, 2), (3)"))!
            .Message.Should().Be("all VALUES must have the same number of terms");
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
