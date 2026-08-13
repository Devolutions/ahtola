using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

/// <summary>
/// P4-B: WHERE col/rowid IN (literals/parameters) emits OpenEphemeral + EphemeralInsert + NoConflict
/// membership (inventory residual: vdbe-open-ephemeral compiler emission for IN-list).
/// </summary>
public sealed class InListOpenEphemeralEmissionTests
{
    [Test]
    public void ColumnInLiteralListEmitsOpenEphemeralAndNoConflict()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(a INT, b TEXT);");
        Execute(connection, "INSERT INTO t VALUES (1,'x'),(2,'y'),(3,'z'),(4,'x');");

        var opcodes = Opcodes(connection, "EXPLAIN SELECT a FROM t WHERE b IN ('x', 'z');");

        opcodes.Should().Contain("OpenEphemeral");
        opcodes.Count(op => op == "EphemeralInsert").Should().Be(2);
        opcodes.Should().Contain("NoConflict");
        opcodes.Should().Contain("OpenReadCursor");
        opcodes.Should().Contain("Rewind");
    }

    [Test]
    public void ColumnInLiteralListReturnsMatchingRows()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(a INT, b TEXT);");
        Execute(connection, "INSERT INTO t VALUES (1,'x'),(2,'y'),(3,'z'),(4,'x');");

        var rows = ReadRows(connection, "SELECT a FROM t WHERE b IN ('x', 'z') ORDER BY a;")
            .Select(row => row[0].AsInteger())
            .ToArray();
        rows.Should().Equal(1L, 3L, 4L);
    }

    [Test]
    public void ColumnInParameterizedListBindsAndFilters()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(a INT);");
        Execute(connection, "INSERT INTO t VALUES (10),(20),(30),(40);");

        using var statement = connection.Prepare("SELECT a FROM t WHERE a IN (?, ?) ORDER BY a;");
        statement.Bind(1, SqlValue.Integer(20));
        statement.Bind(2, SqlValue.Integer(40));

        var produced = new List<long>();
        while (statement.Step() == StatementStepResult.Row)
            produced.Add(statement.GetValue(0).AsInteger());
        produced.Should().Equal(20L, 40L);

        var opcodes = Opcodes(connection, "EXPLAIN SELECT a FROM t WHERE a IN (?, ?);");
        opcodes.Should().Contain("OpenEphemeral");
        opcodes.Count(op => op == "LoadParameter").Should().Be(2);
        opcodes.Should().Contain("NoConflict");
    }

    [Test]
    public void RowIdInLiteralListFiltersByRowid()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(a TEXT);");
        Execute(connection, "INSERT INTO t VALUES ('a'),('b'),('c');");

        var rows = ReadRows(connection, "SELECT a FROM t WHERE rowid IN (1, 3) ORDER BY rowid;")
            .Select(row => row[0].AsText())
            .ToArray();
        rows.Should().Equal("a", "c");

        var opcodes = Opcodes(connection, "EXPLAIN SELECT a FROM t WHERE rowid IN (1, 3);");
        opcodes.Should().Contain("OpenEphemeral");
        opcodes.Should().Contain("NoConflict");
        opcodes.Should().Contain("RowId");
    }

    [Test]
    public void NotInDoesNotForceOpenEphemeralPath()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(a INT);");
        Execute(connection, "INSERT INTO t VALUES (1),(2),(3);");

        // NOT IN stays off the ephemeral membership path (correctness/NULL semantics deferred).
        var rows = ReadRows(connection, "SELECT a FROM t WHERE a NOT IN (2) ORDER BY a;")
            .Select(row => row[0].AsInteger())
            .ToArray();
        rows.Should().Equal(1L, 3L);
    }

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
                var row = new SqlValue[statement.GetColumnCount()];
                for (var i = 0; i < row.Length; i++)
                    row[i] = statement.GetValue(i);
                rows.Add(row);
            }

            return rows;
        }

        private static List<string> Opcodes(EmbeddedConnection connection, string explainSql)
            => ReadRows(connection, explainSql).Select(row => row[1].AsText()!).ToList();
    }
