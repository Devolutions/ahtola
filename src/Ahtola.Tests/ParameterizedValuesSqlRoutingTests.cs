using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

// Proves that EmbeddedDatabase now routes the parameterized top-level VALUES subset -- a source-less
// VALUES row list whose every cell is either a literal or a bare bind parameter (SQL ?, ?NNN, or a named
// :/@/$ placeholder) -- through the real ValuesProgramBuilder cell path: each constant cell emits
// LoadConstant, each parameter cell emits LoadParameter reading a late-bound VdbeParameterBinding, one
// ResultRow per row, a terminating Halt, no cursors. As with the sibling routing suites, EXPLAIN is the
// ground truth for "was this lowered to bytecode?": a routed parameterized VALUES dumps its opcode stream
// (including LoadParameter), while every deliberate fallback shape (a computed cell anywhere in the row, a
// VALUES used as a derived table or a compound term) still throws because EXPLAIN only describes lowered
// programs. Because the program reads its parameters late, its opcode shape is independent of the bound
// values (no baked constant), so the same compiled program rebinds and re-runs without recompilation --
// the observable signature of late binding. Duplicate placeholders (a repeated ?NNN number or named
// identity) collapse to one slot and therefore one value, preserving SQLite parameter identity. Generated
// column1..columnN metadata and unequal-width diagnostics remain unchanged, while unset parameters use
// SQLite/Turso's NULL value.
public class ParameterizedValuesSqlRoutingTests
{
    [Test]
    public void PositionalParametersRouteToBytecodeAndBind()
    {
        using var connection = new EmbeddedDatabase().Connect();

        using var statement = connection.Prepare("VALUES (?, ?)");
        statement.Bind(1, SqlValue.Integer(7));
        statement.Bind(2, SqlValue.Text("z"));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(7));
        statement.GetValue(1).Should().Be(SqlValue.Text("z"));
        statement.Step().Should().Be(StatementStepResult.Done);

        // Each anonymous ? cell lowers to a LoadParameter (not a baked LoadConstant); the program is
        // cursor-less with one ResultRow and a terminating Halt.
        var opcodes = Opcodes(ExplainBound(connection, "EXPLAIN VALUES (?, ?)", SqlValue.Null, SqlValue.Null));
        opcodes.Count(opcode => opcode == "LoadParameter").Should().Be(2);
        opcodes.Count(opcode => opcode == "ResultRow").Should().Be(1);
        opcodes.Count(opcode => opcode == "Halt").Should().Be(1);
        opcodes.Should().NotContain("LoadConstant").And.NotContain("OpenReadCursor").And.NotContain("Rewind");
    }

    [Test]
    public void NumberedParametersMapToDenseSlotsByFirstAppearanceAndPreserveOrder()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // The row order is ?2 then ?1: the routed cell reads the value bound to that SQL index regardless
        // of the numbering, so the emitted row is ('b', 'a').
        using var statement = connection.Prepare("VALUES (?2, ?1)");
        statement.Bind(1, SqlValue.Text("a"));
        statement.Bind(2, SqlValue.Text("b"));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Text("b"));
        statement.GetValue(1).Should().Be(SqlValue.Text("a"));
        statement.Step().Should().Be(StatementStepResult.Done);

        // Slots are assigned densely in first-appearance (row-major) order: ?2 -> slot 0, ?1 -> slot 1.
        var comments = Comments(ExplainBound(connection, "EXPLAIN VALUES (?2, ?1)", SqlValue.Null, SqlValue.Null));
        comments.Should().Contain("r[0]=param[0]").And.Contain("r[1]=param[1]");
    }

    [Test]
    public void NamedParametersRouteBindByNameAndShareDuplicateIdentity()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // :a appears twice, :b once. Duplicate named identity collapses to one slot, so both :a cells read
        // the same bound value; the routed row is ('x', 'y', 'x').
        using var statement = connection.Prepare("VALUES (:a, :b, :a)");
        statement.Bind(":a", SqlValue.Text("x")).Should().BeTrue();
        statement.Bind(":b", SqlValue.Text("y")).Should().BeTrue();
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Text("x"));
        statement.GetValue(1).Should().Be(SqlValue.Text("y"));
        statement.GetValue(2).Should().Be(SqlValue.Text("x"));
        statement.Step().Should().Be(StatementStepResult.Done);

        // :a -> slot 0 (reused by the third cell), :b -> slot 1: three LoadParameters over two slots.
        var rows = ExplainNamed(connection, "EXPLAIN VALUES (:a, :b, :a)", (":a", SqlValue.Null), (":b", SqlValue.Null));
        Opcodes(rows).Count(opcode => opcode == "LoadParameter").Should().Be(3);
        Comments(rows).Should().Equal("r[0]=param[0]", "r[1]=param[1]", "r[2]=param[0]", "output=r[0..2]", "halt");
    }

    [Test]
    public void DuplicateNumberedParameterSharesOneSlot()
    {
        using var connection = new EmbeddedDatabase().Connect();

        using var statement = connection.Prepare("VALUES (?1, ?1)");
        statement.Bind(1, SqlValue.Integer(5));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(5));
        statement.GetValue(1).Should().Be(SqlValue.Integer(5));
        statement.Step().Should().Be(StatementStepResult.Done);

        // Both ?1 cells reference the identical slot 0, so there is one distinct parameter, two loads.
        var comments = Comments(ExplainBound(connection, "EXPLAIN VALUES (?1, ?1)", SqlValue.Null));
        comments.Where(comment => comment.StartsWith("r[")).Should().Equal("r[0]=param[0]", "r[1]=param[0]");
    }

    [Test]
    public void MixedConstantAndParameterCellsRoute()
    {
        using var connection = new EmbeddedDatabase().Connect();

        using var statement = connection.Prepare("VALUES (1, ?, 'z')");
        statement.Bind(1, SqlValue.Integer(42));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(1));
        statement.GetValue(1).Should().Be(SqlValue.Integer(42));
        statement.GetValue(2).Should().Be(SqlValue.Text("z"));
        statement.Step().Should().Be(StatementStepResult.Done);

        // The literal cells bake to LoadConstant while the ? cell defers to LoadParameter, all in one row.
        var opcodes = Opcodes(ExplainBound(connection, "EXPLAIN VALUES (1, ?, 'z')", SqlValue.Null));
        opcodes.Count(opcode => opcode == "LoadConstant").Should().Be(2);
        opcodes.Count(opcode => opcode == "LoadParameter").Should().Be(1);
        opcodes.Count(opcode => opcode == "ResultRow").Should().Be(1);
    }

    [Test]
    public void NullAndBlobBoundParametersRoundTripThroughBytecode()
    {
        using var connection = new EmbeddedDatabase().Connect();

        var blob = new byte[] { 0x01, 0x02, 0xFF };
        using var statement = connection.Prepare("VALUES (?, ?)");
        statement.Bind(1, SqlValue.Null);
        statement.Bind(2, SqlValue.Blob(blob));
        statement.Step().Should().Be(StatementStepResult.Row);

        // A bound NULL is a real routed value, and a bound blob survives the LoadParameter/ResultRow trip.
        statement.GetValue(0).Kind.Should().Be(SqlValueKind.Null);
        statement.GetValue(1).Kind.Should().Be(SqlValueKind.Blob);
        statement.GetValue(1).AsBlob().ToArray().Should().Equal(0x01, 0x02, 0xFF);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void RebindAcrossExecutionsReplaysRoutedProgramWithNewValues()
    {
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare("VALUES (?)");

        statement.Bind(1, SqlValue.Integer(7));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(7));
        statement.Step().Should().Be(StatementStepResult.Done);

        // Reset then rebind the same slot to a new value: the routed parameterized program re-runs and
        // yields the freshly bound row, because the parameter was never baked into the program.
        statement.Reset();
        statement.Bind(1, SqlValue.Integer(8));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(8));
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void RoutedProgramShapeIsIndependentOfBoundValues()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // The observable signature of late binding: the emitted opcode stream for a parameterized VALUES is
        // byte-identical no matter what value would be bound, because the value is read at run time rather
        // than compiled in. A constant VALUES, by contrast, bakes its value into the LoadConstant p4.
        var withSeven = Dump(ExplainBound(connection, "EXPLAIN VALUES (?)", SqlValue.Integer(7)));
        var withOther = Dump(ExplainBound(connection, "EXPLAIN VALUES (?)", SqlValue.Integer(999)));

        withSeven.Should().Equal(withOther);
        withSeven.Should().ContainMatch("*param[0]*").And.NotContainMatch("*=7*").And.NotContainMatch("*=999*");
    }

    [Test]
    public void GeneratedColumnNamesArePreservedForParameterizedValues()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // Metadata is available before binding and matches the evaluator's column1..columnN naming for both
        // single- and multi-row parameterized forms.
        using (var single = connection.Prepare("VALUES (?, ?, ?)"))
        {
            single.GetColumnCount().Should().Be(3);
            new[] { single.GetColumnName(0), single.GetColumnName(1), single.GetColumnName(2) }
                .Should().Equal("column1", "column2", "column3");
        }

        using var multi = connection.Prepare("VALUES (?, ?), (?, ?)");
        multi.GetColumnCount().Should().Be(2);
        new[] { multi.GetColumnName(0), multi.GetColumnName(1) }.Should().Equal("column1", "column2");
    }

    [Test]
        public void MultiRowParameterizedValuesEmitsOpenEphemeralScan()
    {
        using var connection = new EmbeddedDatabase().Connect();

        using var statement = connection.Prepare("VALUES (?), (?), (?)");
        statement.Bind(1, SqlValue.Integer(10));
        statement.Bind(2, SqlValue.Integer(20));
        statement.Bind(3, SqlValue.Integer(30));
        var produced = new List<SqlValue>();
        while (statement.Step() == StatementStepResult.Row)
            produced.Add(statement.GetValue(0));

        produced.Should().Equal(SqlValue.Integer(10), SqlValue.Integer(20), SqlValue.Integer(30));

            // Multi-row parameterized VALUES use BuildEphemeralCells (OpenEphemeral + inserts + scan).
        var opcodes = Opcodes(ExplainBound(
            connection, "EXPLAIN VALUES (?), (?), (?)", SqlValue.Null, SqlValue.Null, SqlValue.Null));
            opcodes.Should().Contain("OpenEphemeral");
            opcodes.Count(opcode => opcode == "EphemeralInsert").Should().Be(3);
            opcodes.Count(opcode => opcode == "LoadParameter").Should().Be(3);
            opcodes.Count(opcode => opcode == "ResultRow").Should().Be(1);
            opcodes.Should().Contain("Rewind").And.Contain("Next");
            opcodes.Count(opcode => opcode == "Halt").Should().Be(1);
        }

    [Test]
    public void ComputedCellMixedWithParameterFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // A single computed cell disqualifies the whole row: the evaluator resolves the parameter and the
        // arithmetic, so the value is correct but the statement is not lowered.
        using (var statement = connection.Prepare("VALUES (?, 1 + 1)"))
        {
            statement.Bind(1, SqlValue.Integer(9));
            statement.Step().Should().Be(StatementStepResult.Row);
            statement.GetValue(0).Should().Be(SqlValue.Integer(9));
            statement.GetValue(1).Should().Be(SqlValue.Integer(2));
            statement.Step().Should().Be(StatementStepResult.Done);
        }

        using var explain = connection.Prepare("EXPLAIN VALUES (?, 1 + 1)");
        explain.Bind(1, SqlValue.Null);
        Assert.Throws<EmbeddedSqlException>(() => explain.Step())!
            .Message.Should().Contain("EXPLAIN is only supported");
    }

    [Test]
    public void ParameterInDerivedTableStaysOnEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // A parameterized VALUES used as a derived table is reached through the scan pipeline, never the
        // top-level router, so it produces the correct row on the evaluator and is not lowered.
        using (var statement = connection.Prepare("SELECT column1 FROM (VALUES (?))"))
        {
            statement.Bind(1, SqlValue.Integer(9));
            statement.Step().Should().Be(StatementStepResult.Row);
            statement.GetValue(0).Should().Be(SqlValue.Integer(9));
            statement.Step().Should().Be(StatementStepResult.Done);
        }

        using var explain = connection.Prepare("EXPLAIN SELECT column1 FROM (VALUES (?))");
        explain.Bind(1, SqlValue.Null);
        Assert.Throws<EmbeddedSqlException>(() => explain.Step())!
            .Message.Should().Contain("EXPLAIN is only supported");
    }

    [Test]
    public void ParameterInCompoundTermRoutes()
    {
        using var connection = new EmbeddedDatabase().Connect();

        using (var statement = connection.Prepare("SELECT 1 AS x UNION ALL VALUES (?)"))
        {
            statement.Bind(1, SqlValue.Integer(2));
            var produced = new List<SqlValue>();
            while (statement.Step() == StatementStepResult.Row)
                produced.Add(statement.GetValue(0));

            produced.Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));
        }

        using var explain = connection.Prepare("EXPLAIN SELECT 1 AS x UNION ALL VALUES (?)");
        explain.Bind(1, SqlValue.Null);
        explain.Step().Should().Be(StatementStepResult.Row);
    }

    [Test]
    public void UnboundPositionalParameterDefaultsToNull()
    {
        using var connection = new EmbeddedDatabase().Connect();

        using var statement = connection.Prepare("VALUES (?, ?)");
        statement.Bind(1, SqlValue.Integer(1));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(1));
        statement.GetValue(1).Kind.Should().Be(SqlValueKind.Null);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void UnboundNamedParameterDefaultsToNull()
    {
        using var connection = new EmbeddedDatabase().Connect();

        using var statement = connection.Prepare("VALUES (:a, :b)");
        statement.Bind(":a", SqlValue.Integer(1)).Should().BeTrue();
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(1));
        statement.GetValue(1).Kind.Should().Be(SqlValueKind.Null);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void UnequalWidthParameterizedValuesRaisesEvaluatorDiagnostic()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // The builder's width validation is mapped onto the evaluator's exact message even when every cell
        // is a parameter, on both the execution and EXPLAIN paths.
        using (var statement = connection.Prepare("VALUES (?, ?), (?)"))
        {
            statement.Bind(1, SqlValue.Integer(1));
            statement.Bind(2, SqlValue.Integer(2));
            statement.Bind(3, SqlValue.Integer(3));
            Assert.Throws<EmbeddedSqlException>(() => statement.Step())!
                .Message.Should().Be("all VALUES must have the same number of terms");
        }

        using var explain = connection.Prepare("EXPLAIN VALUES (?, ?), (?)");
        explain.Bind(1, SqlValue.Null);
        explain.Bind(2, SqlValue.Null);
        explain.Bind(3, SqlValue.Null);
        Assert.Throws<EmbeddedSqlException>(() => explain.Step())!
            .Message.Should().Be("all VALUES must have the same number of terms");
    }

    private static IEnumerable<string> Opcodes(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[1].AsText());

    private static IEnumerable<string> Comments(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[6].AsText());

    private static List<string> Dump(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => $"{row[1].AsText()}|{row[6].AsText()}").ToList();

    // Prepares an EXPLAIN statement, binds the given values positionally (EXPLAIN still requires every
    // parameter bound before it can describe the program), and reads its opcode rows.
    private static List<SqlValue[]> ExplainBound(EmbeddedConnection connection, string sql, params SqlValue[] positional)
    {
        using var statement = connection.Prepare(sql);
        for (var index = 0; index < positional.Length; index++)
            statement.Bind(index + 1, positional[index]);

        return DrainRows(statement);
    }

    private static List<SqlValue[]> ExplainNamed(
        EmbeddedConnection connection, string sql, params (string Name, SqlValue Value)[] bindings)
    {
        using var statement = connection.Prepare(sql);
        foreach (var (name, value) in bindings)
            statement.Bind(name, value).Should().BeTrue();

        return DrainRows(statement);
    }

    private static List<SqlValue[]> DrainRows(EmbeddedStatement statement)
    {
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
