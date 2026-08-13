using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

/// <summary>
/// P4-A: compiler emits SeekGE/IdxGE for SEARCH plans with leading equality on a usable index
/// (inventory residual: vdbe-seek-op-family-partial compiler emission).
/// </summary>
public sealed class IndexSeekEmissionTests
{
    [Test]
    public void EqualitySearchOnSecondaryIndexEmitsIdxGE()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(a INT, b TEXT);");
        Execute(connection, "CREATE INDEX idx_b ON t(b);");
        Execute(connection, "INSERT INTO t VALUES (1,'x'),(2,'y'),(3,'x'),(4,'z');");

        var opcodes = Opcodes(connection, "EXPLAIN SELECT a FROM t WHERE b = 'x';");

        opcodes.Should().Contain(op => op == "SeekGE" || op == "IdxGE");
        opcodes.Should().NotContain("Rewind");
        opcodes.Should().Contain("Filter");
    }

    [Test]
    public void EqualitySearchReturnsMatchingRows()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(a INT, b TEXT);");
        Execute(connection, "CREATE INDEX idx_b ON t(b);");
        Execute(connection, "INSERT INTO t VALUES (1,'x'),(2,'y'),(3,'x'),(4,'z');");

        var rows = ReadRows(connection, "SELECT a FROM t WHERE b = 'x' ORDER BY a;")
            .Select(row => row[0].AsInteger())
            .ToArray();
        rows.Should().Equal(1L, 3L);
    }

    [Test]
    public void MultiColumnEqualityPrefixEmitsSeek()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(a INT, b INT, c TEXT);");
        Execute(connection, "CREATE INDEX idx_ab ON t(a, b);");
        Execute(connection, "INSERT INTO t VALUES (1,10,'p'),(1,20,'q'),(2,10,'r');");

        var opcodes = Opcodes(connection, "EXPLAIN SELECT c FROM t WHERE a = 1 AND b = 20;");
        opcodes.Should().Contain(op => op == "SeekGE" || op == "IdxGE");

        var rows = ReadRows(connection, "SELECT c FROM t WHERE a = 1 AND b = 20;")
            .Select(row => row[0].AsText())
            .ToArray();
        rows.Should().Equal("q");
    }

    [Test]
    public void FullScanWithoutEqualityStillRewinds()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(a INT, b TEXT);");
        Execute(connection, "CREATE INDEX idx_b ON t(b);");
        Execute(connection, "INSERT INTO t VALUES (1,'x');");

        var opcodes = Opcodes(connection, "EXPLAIN SELECT a FROM t;");
        opcodes.Should().Contain("Rewind");
        opcodes.Should().NotContain("SeekGE");
        opcodes.Should().NotContain("IdxGE");
    }

    [Test]
    public void SeekKeyWithKeyColumnsFindsNonLeadingIndexColumn()
    {
        var ordered = new List<SqlValue[]>
        {
            new[] { SqlValue.Integer(1), SqlValue.Text("x") },
            new[] { SqlValue.Integer(3), SqlValue.Text("x") },
            new[] { SqlValue.Integer(2), SqlValue.Text("y") },
        };
        var source = new VdbeCursorSource(ordered, new List<long> { 1, 3, 2 });

        VdbeInstruction[] instructions =
        [
            new OpenReadCursorInstruction(new Cursor(0), ColumnCount: 2),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("y")),
            new SeekKeyInstruction(
                new Cursor(0),
                new RegisterRange(new Register(0), 1),
                VdbeKeySeekOperator.GreaterThanOrEqual,
                EqOnly: false,
                IsIndex: true,
                new ProgramCounter(6),
                "idxge b",
                KeyColumns: [1]),
            new ColumnInstruction(new Cursor(0), ColumnIndex: 0, new Register(1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new GotoInstruction(new ProgramCounter(8)),
            new LoadConstantInstruction(new Register(1), SqlValue.Integer(-1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 1, instructions);
        program.Instructions[2].Opcode.Should().Be(VdbeOpcode.IdxGE);
        using var statement = new ResumableStatement(program, cursorSources: [source]);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsInteger().Should().Be(2);
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static IEnumerable<string> Opcodes(EmbeddedConnection connection, string sql)
        => ReadRows(connection, sql).Select(row => row[1].AsText()!);

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
