using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

// Coverage for the managed engine's bytecode lowering of the bounded INSERT/UPDATE/DELETE
// subset. EXPLAIN dumps assert the emitted program shape (real cursor/mutation opcodes and
// jump layout); the behavioural cases assert the compiled path stays byte-for-byte identical
// to the tree-walking evaluator for predicates, rows-affected, RETURNING, last_insert_rowid,
// and constraint atomicity, and that unsupported clauses still fall back to the evaluator.
public class CompiledDmlExecutionTests
{
    [Test]
    public void ExplainDumpsInsertProgram()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        var rows = ReadRows(connection, "EXPLAIN INSERT INTO t VALUES (1), (2);");
        Opcodes(rows).Should().Equal(
            "OpenWriteCursor", "Rewind", "Insert", "Next", "Commit", "CloseCursor", "Halt");

        // OpenWriteCursor names the table and reports its column count.
        rows[0][2].Should().Be(SqlValue.Integer(0));
        rows[0][4].Should().Be(SqlValue.Integer(1));
        rows[0][5].Should().Be(SqlValue.Text("t"));
        rows[0][6].Should().Be(SqlValue.Text("open write cursor 0 on t (1 cols)"));

        // Rewind jumps past the loop to Commit when there is nothing to mutate.
        rows[1][3].Should().Be(SqlValue.Integer(4));

        // Next loops back to the mutation (address 2).
        rows[3][3].Should().Be(SqlValue.Integer(2));
    }

    [Test]
    public void ExplainDumpsInsertProgramWithReturning()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, name TEXT);");

        var rows = ReadRows(connection, "EXPLAIN INSERT INTO t VALUES (1, 'a') RETURNING id, name;");
        Opcodes(rows).Should().Equal(
            "OpenWriteCursor", "Rewind", "Insert", "Next", "OpenReadCursor", "Rewind",
            "Column", "Column", "ResultRow", "Next", "CloseCursor", "Commit", "CloseCursor", "Halt");

        // RETURNING reads the source-ordered affected-row buffer through cursor 1.
        rows[6][6].Should().Be(SqlValue.Text("r[0]=c1.col[0]"));
        rows[7][6].Should().Be(SqlValue.Text("r[1]=c1.col[1]"));
        rows[8][6].Should().Be(SqlValue.Text("output=r[0..1]"));
    }

    [Test]
    public void ExplainDumpsInsertReturningRowid()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        var rows = ReadRows(connection, "EXPLAIN INSERT INTO t VALUES (1) RETURNING rowid;");
        Opcodes(rows).Should().Equal(
            "OpenWriteCursor", "Rewind", "Insert", "Next", "OpenReadCursor", "Rewind",
            "RowId", "ResultRow", "Next", "CloseCursor", "Commit", "CloseCursor", "Halt");

        rows[6][6].Should().Be(SqlValue.Text("r[0]=c1.rowid"));
    }

    [Test]
    public void ExplainDumpsUpdateProgramWithFilter()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        var rows = ReadRows(connection, "EXPLAIN UPDATE t SET value = 9 WHERE value > 1;");
        Opcodes(rows).Should().Equal(
            "OpenWriteCursor", "Rewind", "Filter", "Update", "Next", "Commit", "CloseCursor", "Halt");

        // Filter falls through to the mutation when true and jumps to Next when false.
        rows[2][3].Should().Be(SqlValue.Integer(4));

        // Next loops back to the Filter at the top of the body (address 2).
        rows[4][3].Should().Be(SqlValue.Integer(2));
    }

    [Test]
    public void ExplainDumpsUpdateProgramWithoutFilter()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        var rows = ReadRows(connection, "EXPLAIN UPDATE t SET value = 9;");
        Opcodes(rows).Should().Equal(
            "OpenWriteCursor", "Rewind", "Update", "Next", "Commit", "CloseCursor", "Halt");
    }

    [Test]
    public void ExplainDumpsDeleteProgramWithFilter()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        var rows = ReadRows(connection, "EXPLAIN DELETE FROM t WHERE value > 1;");
        Opcodes(rows).Should().Equal(
            "OpenWriteCursor", "Rewind", "Filter", "Delete", "Next", "Commit", "CloseCursor", "Halt");
    }

    [Test]
    public void ExplainDumpsDeleteAllProgram()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        var rows = ReadRows(connection, "EXPLAIN DELETE FROM t;");
        Opcodes(rows).Should().Equal(
            "OpenWriteCursor", "Rewind", "Delete", "Next", "Commit", "CloseCursor", "Halt");
    }

    [Test]
    public void ExplainDumpsDeleteReturningProgram()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, name TEXT);");

        var rows = ReadRows(connection, "EXPLAIN DELETE FROM t WHERE id > 1 RETURNING *;");
        Opcodes(rows).Should().Equal(
            "OpenWriteCursor", "Rewind", "Filter", "Delete", "Next", "OpenReadCursor", "Rewind",
            "Column", "Column", "ResultRow", "Next", "CloseCursor", "Commit", "CloseCursor", "Halt");
    }

    [Test]
    public void ExplainStillRejectsPredicatesOutsideTheScanSubset()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        // A WHERE that embeds a subquery cannot run against a single scanned row, so the
        // whole statement falls back to the evaluator and EXPLAIN reports nothing.
        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN UPDATE t SET value = 1 WHERE value IN (SELECT 1);"));

        // A subquery needs more than the scanned row and its rowid, so it remains evaluator-backed.
        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN DELETE FROM t WHERE rowid IN (SELECT 1);"));
    }

    [Test]
    public void InsertLoweredProgramPersistsRowsAndReportsMetadata()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, name TEXT);");

        using (var statement = connection.Prepare("INSERT INTO t VALUES (1, 'ada'), (2, 'grace');"))
        {
            statement.Step().Should().Be(StatementStepResult.Done);
            statement.RowsAffected.Should().Be(2);
        }

        connection.LastInsertRowId.Should().Be(2);

        var rows = ReadRows(connection, "SELECT id, name FROM t;");
        rows.Should().HaveCount(2);
        rows[0][0].Should().Be(SqlValue.Integer(1));
        rows[0][1].Should().Be(SqlValue.Text("ada"));
        rows[1][0].Should().Be(SqlValue.Integer(2));
        rows[1][1].Should().Be(SqlValue.Text("grace"));
    }

    [Test]
    public void UpdateLoweredProgramAppliesPredicateAndReportsRowsAffected()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30);");

        using (var statement = connection.Prepare("UPDATE t SET value = value + 1 WHERE id >= 2;"))
        {
            statement.Step().Should().Be(StatementStepResult.Done);
            statement.RowsAffected.Should().Be(2);
        }

        var rows = ReadRows(connection, "SELECT value FROM t;");
        rows.Select(row => row[0]).Should().Equal(
            SqlValue.Integer(10), SqlValue.Integer(21), SqlValue.Integer(31));
    }

    [Test]
    public void DeleteLoweredProgramAppliesPredicateAndReportsRowsAffected()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2), (3), (4);");

        using (var statement = connection.Prepare("DELETE FROM t WHERE id = 2 OR id = 4;"))
        {
            statement.Step().Should().Be(StatementStepResult.Done);
            statement.RowsAffected.Should().Be(2);
        }

        var rows = ReadRows(connection, "SELECT id FROM t;");
        rows.Select(row => row[0]).Should().Equal(SqlValue.Integer(1), SqlValue.Integer(3));
    }

    [Test]
    public void DeleteWithoutPredicateRemovesEveryRow()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2), (3);");

        using (var statement = connection.Prepare("DELETE FROM t;"))
        {
            statement.Step().Should().Be(StatementStepResult.Done);
            statement.RowsAffected.Should().Be(3);
        }

        ReadRows(connection, "SELECT id FROM t;").Should().BeEmpty();
    }

    [Test]
    public void InsertReturningReadsTheWrittenRow()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, name TEXT);");

        var rows = ReadRows(connection, "INSERT INTO t VALUES (5, 'ada'), (6, 'grace') RETURNING name, id;");
        rows.Should().HaveCount(2);
        rows[0][0].Should().Be(SqlValue.Text("ada"));
        rows[0][1].Should().Be(SqlValue.Integer(5));
        rows[1][0].Should().Be(SqlValue.Text("grace"));
        rows[1][1].Should().Be(SqlValue.Integer(6));
    }

    [Test]
    public void InsertReturningRowidObservesAllocatedRowid()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(name TEXT);");

        var rows = ReadRows(connection, "INSERT INTO t VALUES ('x'), ('y') RETURNING rowid, name;");
        rows.Should().HaveCount(2);
        rows[0][0].Should().Be(SqlValue.Integer(1));
        rows[0][1].Should().Be(SqlValue.Text("x"));
        rows[1][0].Should().Be(SqlValue.Integer(2));
        rows[1][1].Should().Be(SqlValue.Text("y"));
    }

    [Test]
    public void UpdateReturningReadsNewValues()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 10), (2, 20);");

        var rows = ReadRows(connection, "UPDATE t SET value = value + 5 WHERE id = 2 RETURNING id, value;");
        rows.Should().ContainSingle();
        rows[0][0].Should().Be(SqlValue.Integer(2));
        rows[0][1].Should().Be(SqlValue.Integer(25));
    }

    [Test]
    public void DeleteReturningReadsRemovedRows()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30);");

        var rows = ReadRows(connection, "DELETE FROM t WHERE value >= 20 RETURNING id, value;");
        rows.Should().HaveCount(2);
        rows[0][0].Should().Be(SqlValue.Integer(2));
        rows[0][1].Should().Be(SqlValue.Integer(20));
        rows[1][0].Should().Be(SqlValue.Integer(3));
        rows[1][1].Should().Be(SqlValue.Integer(30));

        ReadRows(connection, "SELECT id FROM t;").Select(row => row[0]).Should().Equal(SqlValue.Integer(1));
    }

    [Test]
    public void ConstraintViolationRollsBackTheWholeStatement()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT);");
        Execute(connection, "INSERT INTO t VALUES (1, 'ada');");

        // The second value row conflicts with the existing rowid, so the whole INSERT fails
        // and neither value row is persisted, even though the first row was already built.
        using (var statement = connection.Prepare("INSERT INTO t VALUES (2, 'grace'), (1, 'dup') RETURNING id;"))
        {
            Assert.Throws<EmbeddedSqlException>(() => statement.Step());
        }

        var rows = ReadRows(connection, "SELECT id, name FROM t;");
        rows.Should().ContainSingle();
        rows[0][0].Should().Be(SqlValue.Integer(1));
        rows[0][1].Should().Be(SqlValue.Text("ada"));
    }

    [Test]
    public void LastInsertRowidIsUnchangedByUpdateAndDelete()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (7, 10);");
        connection.LastInsertRowId.Should().Be(7);

        Execute(connection, "UPDATE t SET value = 11 WHERE id = 7;");
        connection.LastInsertRowId.Should().Be(7);

        Execute(connection, "DELETE FROM t WHERE id = 7;");
        connection.LastInsertRowId.Should().Be(7);
    }

    [Test]
    public void EvaluatorHandlesPredicatesOutsideTheScanSubset()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30);");

        // A subquery predicate keeps the DELETE on the evaluator but must still succeed.
        using (var statement = connection.Prepare("DELETE FROM t WHERE value IN (SELECT 20 UNION SELECT 30);"))
        {
            statement.Step().Should().Be(StatementStepResult.Done);
            statement.RowsAffected.Should().Be(2);
        }

        ReadRows(connection, "SELECT id FROM t;").Select(row => row[0]).Should().Equal(SqlValue.Integer(1));
    }

    [Test]
    public void EvaluatorHandlesReturningExpressionsOutsideTheSubset()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, value INTEGER);");

        // A projected expression (not a bare column) is not lowered but still returns via
        // the evaluator.
        var rows = ReadRows(connection, "INSERT INTO t VALUES (1, 10) RETURNING value * 2 AS doubled;");
        rows.Should().ContainSingle();
        rows[0][0].Should().Be(SqlValue.Integer(20));
    }

    [Test]
    public void CompiledInsertSupportsResetAndReplaysTheProgram()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(name TEXT);");

        using var statement = connection.Prepare("INSERT INTO t VALUES ('a') RETURNING rowid, name;");
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(1));
        statement.GetValue(1).Should().Be(SqlValue.Text("a"));
        statement.Step().Should().Be(StatementStepResult.Done);

        statement.Reset();
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(2));
        statement.Step().Should().Be(StatementStepResult.Done);

        ReadRows(connection, "SELECT rowid FROM t;").Select(row => row[0])
            .Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));
    }

    private static IEnumerable<string> Opcodes(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[1].AsText());

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
}
