using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// Proves that an eligible top-level VALUES statement compiles its lowered VDBE program (and its
// SQL-index-to-slot map) at most once per prepared statement, then reuses that immutable program across
// every Reset/rebind instead of recompiling on each execution -- the fix for the reviewed per-reset
// recompilation of parameterized VALUES programs. The observable seam is EmbeddedStatement.CompiledValuesProgram
// (the cached immutable program, reference-stable across resets) plus ValuesProgramCompilationCount (which
// reaches one and never grows). The suite also confirms the fix preserves prepared-statement semantics:
// repeated binds/resets for every value kind (blobs, NULLs), named and duplicate-numbered placeholders
// collapsing to one slot, clear/rebind to fresh values, disposal releasing the cache, SQLite/Turso NULL
// defaults for unbound parameters, unequal-width diagnostics with their exact timing, and that fallback
// shapes (a computed cell) are never cached.
public class ParameterizedValuesProgramCacheTests
{
    [Test]
    public void ParameterizedValuesCompilesOnceAndReusesTheProgramAcrossResets()
    {
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare("VALUES (?)");

        statement.Bind(1, SqlValue.Integer(0));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(0));
        statement.Step().Should().Be(StatementStepResult.Done);

        // The first execution resolves and caches the lowering exactly once.
        var program = statement.CompiledValuesProgram;
        program.Should().NotBeNull();
        statement.ValuesProgramCompilationCount.Should().Be(1);

        // Every subsequent Reset/rebind re-runs the identical immutable program: a fresh compile would
        // produce a distinct VdbeProgram instance and bump the counter, so reference-stability plus a
        // frozen counter is direct proof of reuse rather than recompilation.
        for (var iteration = 1; iteration <= 5; iteration++)
        {
            statement.Reset();
            statement.Bind(1, SqlValue.Integer(iteration));
            statement.Step().Should().Be(StatementStepResult.Row);
            statement.GetValue(0).Should().Be(SqlValue.Integer(iteration));
            statement.Step().Should().Be(StatementStepResult.Done);

            statement.CompiledValuesProgram.Should().BeSameAs(program);
            statement.ValuesProgramCompilationCount.Should().Be(1);
        }
    }

    [Test]
    public void ConstantOnlyValuesAlsoCachesAndReusesItsProgram()
    {
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare("VALUES (1), (2)");

        DrainIntegers(statement).Should().Equal(1, 2);
        var program = statement.CompiledValuesProgram;
        program.Should().NotBeNull();
        program!.ParameterSlotCount.Should().Be(0);
        statement.ValuesProgramCompilationCount.Should().Be(1);

        statement.Reset();
        DrainIntegers(statement).Should().Equal(1, 2);
        statement.CompiledValuesProgram.Should().BeSameAs(program);
        statement.ValuesProgramCompilationCount.Should().Be(1);
    }

    [Test]
    public void RepeatedBlobBindsAcrossResetsReuseTheProgramAndDoNotShareState()
    {
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare("VALUES (?)");

        statement.Bind(1, SqlValue.Blob(new byte[] { 0x01, 0x02, 0xFF }));
        statement.Step().Should().Be(StatementStepResult.Row);
        var firstSnapshot = statement.GetValue(0).AsBlob().ToArray();
        firstSnapshot.Should().Equal(0x01, 0x02, 0xFF);
        statement.Step().Should().Be(StatementStepResult.Done);
        var program = statement.CompiledValuesProgram;
        program.Should().NotBeNull();

        // Rebind the same slot to a different blob and re-run: the routed value is the freshly bound blob,
        // the earlier captured bytes are untouched (no aliased binding buffer bleeds between executions),
        // and the program instance is the same one compiled on the first execution.
        statement.Reset();
        statement.Bind(1, SqlValue.Blob(new byte[] { 0xAA, 0xBB }));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).AsBlob().ToArray().Should().Equal(0xAA, 0xBB);
        statement.Step().Should().Be(StatementStepResult.Done);

        firstSnapshot.Should().Equal(0x01, 0x02, 0xFF);
        statement.CompiledValuesProgram.Should().BeSameAs(program);
        statement.ValuesProgramCompilationCount.Should().Be(1);
    }

    [Test]
    public void ClearAndRebindToNullAndBackReusesTheProgram()
    {
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare("VALUES (?)");

        statement.Bind(1, SqlValue.Integer(7));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(7));
        statement.Step().Should().Be(StatementStepResult.Done);
        var program = statement.CompiledValuesProgram;
        program.Should().NotBeNull();

        // "Clear" the slot to NULL (a real routed value, not a missing binding), then rebind it to a fresh
        // value; both executions replay the cached program.
        statement.Reset();
        statement.Bind(1, SqlValue.Null);
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Kind.Should().Be(SqlValueKind.Null);
        statement.Step().Should().Be(StatementStepResult.Done);
        statement.CompiledValuesProgram.Should().BeSameAs(program);

        statement.Reset();
        statement.Bind(1, SqlValue.Text("back"));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Text("back"));
        statement.Step().Should().Be(StatementStepResult.Done);

        statement.CompiledValuesProgram.Should().BeSameAs(program);
        statement.ValuesProgramCompilationCount.Should().Be(1);
    }

    [Test]
    public void NamedDuplicatePlaceholderReusesOneSlotAndOneProgramAcrossResets()
    {
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare("VALUES (:a, :b, :a)");

        statement.Bind(":a", SqlValue.Text("x")).Should().BeTrue();
        statement.Bind(":b", SqlValue.Text("y")).Should().BeTrue();
        RowText(statement).Should().Equal("x", "y", "x");
        var program = statement.CompiledValuesProgram;
        program.Should().NotBeNull();

        // :a collapses to a single slot, so rebinding it once feeds both of its cells on the reused program.
        statement.Reset();
        statement.Bind(":a", SqlValue.Text("p")).Should().BeTrue();
        statement.Bind(":b", SqlValue.Text("q")).Should().BeTrue();
        RowText(statement).Should().Equal("p", "q", "p");

        statement.CompiledValuesProgram.Should().BeSameAs(program);
        statement.ValuesProgramCompilationCount.Should().Be(1);
    }

    [Test]
    public void DuplicateNumberedPlaceholderReusesOneSlotAndOneProgramAcrossResets()
    {
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare("VALUES (?1, ?1)");

        statement.Bind(1, SqlValue.Integer(5));
        RowIntegers(statement).Should().Equal(5, 5);
        var program = statement.CompiledValuesProgram;
        program.Should().NotBeNull();
        program!.ParameterSlotCount.Should().Be(1);

        statement.Reset();
        statement.Bind(1, SqlValue.Integer(8));
        RowIntegers(statement).Should().Equal(8, 8);

        statement.CompiledValuesProgram.Should().BeSameAs(program);
        statement.ValuesProgramCompilationCount.Should().Be(1);
    }

    [Test]
    public void MultiRowParameterizedValuesReusesTheProgramAcrossResets()
    {
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare("VALUES (?), (?), (?)");

        statement.Bind(1, SqlValue.Integer(10));
        statement.Bind(2, SqlValue.Integer(20));
        statement.Bind(3, SqlValue.Integer(30));
        DrainIntegers(statement).Should().Equal(10, 20, 30);
        var program = statement.CompiledValuesProgram;
        program.Should().NotBeNull();

        statement.Reset();
        statement.Bind(1, SqlValue.Integer(40));
        statement.Bind(2, SqlValue.Integer(50));
        statement.Bind(3, SqlValue.Integer(60));
        DrainIntegers(statement).Should().Equal(40, 50, 60);

        statement.CompiledValuesProgram.Should().BeSameAs(program);
        statement.ValuesProgramCompilationCount.Should().Be(1);
    }

    [Test]
    public void ColumnMetadataStaysStableAcrossReusedExecutions()
    {
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare("VALUES (?, ?, ?)");

        statement.Bind(1, SqlValue.Integer(1));
        statement.Bind(2, SqlValue.Integer(2));
        statement.Bind(3, SqlValue.Integer(3));
        statement.Step().Should().Be(StatementStepResult.Row);
        ColumnNames(statement).Should().Equal("column1", "column2", "column3");
        statement.Step().Should().Be(StatementStepResult.Done);

        statement.Reset();
        statement.Bind(1, SqlValue.Integer(4));
        statement.Bind(2, SqlValue.Integer(5));
        statement.Bind(3, SqlValue.Integer(6));
        statement.Step().Should().Be(StatementStepResult.Row);
        ColumnNames(statement).Should().Equal("column1", "column2", "column3");
        statement.GetValue(0).Should().Be(SqlValue.Integer(4));
    }

    [Test]
    public void ComputedCellFallsBackToEvaluatorAndIsNeverCached()
    {
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare("VALUES (?, 1 + 1)");

        statement.Bind(1, SqlValue.Integer(9));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(9));
        statement.GetValue(1).Should().Be(SqlValue.Integer(2));
        statement.Step().Should().Be(StatementStepResult.Done);

        // A computed cell disqualifies the whole statement from lowering, so nothing is cached.
        statement.CompiledValuesProgram.Should().BeNull();
        statement.ValuesProgramCompilationCount.Should().Be(0);

        // The ineligible decision persists across resets without ever attempting to cache a program.
        statement.Reset();
        statement.Bind(1, SqlValue.Integer(11));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(11));
        statement.CompiledValuesProgram.Should().BeNull();
        statement.ValuesProgramCompilationCount.Should().Be(0);
    }

    [Test]
    public void UnboundParameterDefaultsToNullAndTheLoweringIsCached()
    {
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare("VALUES (?, ?)");

        statement.Bind(1, SqlValue.Integer(1));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(1));
        statement.GetValue(1).Kind.Should().Be(SqlValueKind.Null);
        statement.Step().Should().Be(StatementStepResult.Done);
        statement.CompiledValuesProgram.Should().NotBeNull();
        statement.ValuesProgramCompilationCount.Should().Be(1);

        statement.Reset();
        statement.Bind(2, SqlValue.Integer(2));
        RowIntegers(statement).Should().Equal(1, 2);
        statement.CompiledValuesProgram.Should().NotBeNull();
        statement.ValuesProgramCompilationCount.Should().Be(1);
    }

    [Test]
    public void UnequalWidthValuesThrowsEachExecutionAndIsNeverCached()
    {
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare("VALUES (?, ?), (?)");

        statement.Bind(1, SqlValue.Integer(1));
        statement.Bind(2, SqlValue.Integer(2));
        statement.Bind(3, SqlValue.Integer(3));

        // The width diagnostic surfaces at execution time exactly as the evaluator raises it, and because the
        // lowering never resolves cleanly it recurs on every execution rather than being cached.
        Assert.Throws<EmbeddedSqlException>(() => statement.Step())!
            .Message.Should().Be("all VALUES must have the same number of terms");
        statement.CompiledValuesProgram.Should().BeNull();
        statement.ValuesProgramCompilationCount.Should().Be(0);

        statement.Reset();
        Assert.Throws<EmbeddedSqlException>(() => statement.Step())!
            .Message.Should().Be("all VALUES must have the same number of terms");
        statement.CompiledValuesProgram.Should().BeNull();
        statement.ValuesProgramCompilationCount.Should().Be(0);
    }

    [Test]
    public void DisposeReleasesTheCachedProgramAndBlocksFurtherUse()
    {
        using var connection = new EmbeddedDatabase().Connect();
        var statement = connection.Prepare("VALUES (?)");

        statement.Bind(1, SqlValue.Integer(1));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.CompiledValuesProgram.Should().NotBeNull();

        statement.Dispose();

        // Dispose releases the cached lowering (so its program is collectable) and rejects any further use.
        statement.CompiledValuesProgram.Should().BeNull();
        Assert.Throws<ObjectDisposedException>(() => statement.Step());
        Assert.Throws<ObjectDisposedException>(() => statement.Reset());
    }

    [Test]
    public void SeparatePreparedStatementsForTheSameSqlOwnIndependentCaches()
    {
        using var connection = new EmbeddedDatabase().Connect();
        using var first = connection.Prepare("VALUES (?)");
        using var second = connection.Prepare("VALUES (?)");

        first.Bind(1, SqlValue.Integer(1));
        first.Step().Should().Be(StatementStepResult.Row);
        first.Step().Should().Be(StatementStepResult.Done);

        second.Bind(1, SqlValue.Integer(2));
        second.Step().Should().Be(StatementStepResult.Row);
        second.Step().Should().Be(StatementStepResult.Done);

        // The cache is per prepared statement: two statements over identical SQL each compile their own
        // program, so their cached instances are distinct.
        first.CompiledValuesProgram.Should().NotBeNull();
        second.CompiledValuesProgram.Should().NotBeNull();
        first.CompiledValuesProgram.Should().NotBeSameAs(second.CompiledValuesProgram);
        first.ValuesProgramCompilationCount.Should().Be(1);
        second.ValuesProgramCompilationCount.Should().Be(1);
    }

    private static List<long> DrainIntegers(EmbeddedStatement statement)
    {
        var values = new List<long>();
        while (statement.Step() == StatementStepResult.Row)
            values.Add(statement.GetValue(0).AsInteger());

        return values;
    }

    private static List<long> RowIntegers(EmbeddedStatement statement)
    {
        statement.Step().Should().Be(StatementStepResult.Row);
        var values = new List<long>();
        for (var ordinal = 0; ordinal < statement.GetColumnCount(); ordinal++)
            values.Add(statement.GetValue(ordinal).AsInteger());

        statement.Step().Should().Be(StatementStepResult.Done);
        return values;
    }

    private static List<string> RowText(EmbeddedStatement statement)
    {
        statement.Step().Should().Be(StatementStepResult.Row);
        var values = new List<string>();
        for (var ordinal = 0; ordinal < statement.GetColumnCount(); ordinal++)
            values.Add(statement.GetValue(ordinal).AsText());

        statement.Step().Should().Be(StatementStepResult.Done);
        return values;
    }

    private static List<string> ColumnNames(EmbeddedStatement statement)
    {
        var names = new List<string>();
        for (var ordinal = 0; ordinal < statement.GetColumnCount(); ordinal++)
            names.Add(statement.GetColumnName(ordinal));

        return names;
    }
}
