using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using System.Data.Common;
using Ahtola.Core;
using Ahtola.Core.Execution;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

// Test-first foundation for the Seek access-path work (gap-analysis item P1-1: the managed
// engine has no Seek opcodes, so every constrained scan is a full scan + post-filter today).
// Three concerns are covered, each in its own class:
//
//   - SeekInstructionExplainTests: the additive scaffolding (SeekRowid opcode + instruction +
//     VdbeExplain arm). Pure unit, no engine; green as soon as the opcode/instruction exist.
//   - SeekPlanShapeTests: EXPLAIN plan-shape over the direct-embedded engine. The regression
//     guard (a predicate-free scan must still Rewind and must not seek) is active. The
//     opcode-assertion tests are now ACTIVE (Step 2 point-lookup + Step 3 range): they assert
//     the compiler emits SeekRowid / SeekRowidRange for `rowid = N` / `rowid > N` / `rowid BETWEEN`.
//   - SeekParityTests: result correctness vs a Microsoft.Data.Sqlite oracle for the rowid
//     access paths the seek work touches. These are green (a seek must return exactly the rows
//     the scan did). The out-of-order-rowid cases prove correctness does not depend on the
//     RowIds sort invariant (the refinement to the saved seek-optimizer plan: CommitInserts
//     appends in insert order, so RowIds is not globally sorted within a session; SeekRowidRange
//     therefore uses a linear scan, not BinarySearch).

public sealed class SeekInstructionExplainTests
{
    [Test]
    public void DescribeSeekRowidRendersOpcodeAndOperands()
    {
        var instruction = new SeekRowidInstruction(
            new Cursor(2),
            new Register(5),
            new ProgramCounter(9),
            "seek cursor 2 to rowid r[5], goto 9 if not found");

        var (p1, p2, p3, p4, comment) = VdbeExplain.Describe(instruction);

        p1.Should().Be(2);
        p2.Should().Be(9);
        p3.Should().Be(5);
        p4.Should().BeNull();
        comment.Should().Be("seek cursor 2 to rowid r[5], goto 9 if not found");
        instruction.Opcode.Should().Be(VdbeOpcode.SeekRowid);
    }
}

public class SeekPlanShapeTests
{
    [Test]
    public void FullScanWithoutRowidPredicateStillRewindsAndDoesNotSeek()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);");
        Execute(connection, "INSERT INTO t VALUES (1,'a'),(2,'b'),(3,'c');");

        var opcodes = Opcodes(connection, "EXPLAIN SELECT v FROM t;");

        opcodes.Should().Contain("Rewind");
        opcodes.Should().NotContain("SeekRowid");
    }

    [Test]
    public void RowidPointLookupEmitsSeekRowid()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);");
        Execute(connection, "INSERT INTO t VALUES (1,'a'),(2,'b'),(3,'c');");

        var opcodes = Opcodes(connection, "EXPLAIN SELECT v FROM t WHERE rowid = 2;");

        opcodes.Should().Contain("SeekRowid");
    }

    [Test]
    public void IntegerPrimaryKeyAliasPointLookupEmitsSeekRowid()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);");
        Execute(connection, "INSERT INTO t VALUES (1,'a'),(2,'b'),(3,'c');");

        // id is the rowid alias, so WHERE id = N is the same seek path as rowid = N.
        var opcodes = Opcodes(connection, "EXPLAIN SELECT v FROM t WHERE id = 2;");
        opcodes.Should().Contain("SeekRowid");

        ReadRows(connection, "SELECT v FROM t WHERE id = 2;")
            .Select(row => row[0].AsText())
            .Should()
            .Equal("b");
    }

    [Test]
    public void RowidGreaterThanEmitsSeekRowid()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);");
        Execute(connection, "INSERT INTO t VALUES (1,'a'),(2,'b'),(3,'c');");

        var opcodes = Opcodes(connection, "EXPLAIN SELECT v FROM t WHERE rowid > 1;");

        opcodes.Should().Contain("SeekRowidRange");
    }

    [Test]
    public void RowidBetweenRangeEmitsSeekRowidRange()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);");
        Execute(connection, "INSERT INTO t VALUES (1,'a'),(2,'b'),(3,'c');");

        var opcodes = Opcodes(connection, "EXPLAIN SELECT v FROM t WHERE rowid BETWEEN 1 AND 2;");

        opcodes.Should().Contain("SeekRowidRange");
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static IEnumerable<string> Opcodes(EmbeddedConnection connection, string sql)
        => ReadRows(connection, sql).Select(row => row[1].AsText());

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

[NonParallelizable]
public sealed class SeekParityTests
{
    [Test]
    public void RowidPointLookupHitMatchesSqlite()
    {
        AssertSelectMatchesSqlite(
            "rowid-eq-hit",
            "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);",
            "INSERT INTO t VALUES (1,'a'),(2,'b'),(3,'c');",
            "SELECT v FROM t WHERE rowid = 2;",
            new[] { "b" });
    }

    [Test]
    public void RowidPointLookupMissMatchesSqlite()
    {
        AssertSelectMatchesSqlite(
            "rowid-eq-miss",
            "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);",
            "INSERT INTO t VALUES (1,'a'),(2,'b'),(3,'c');",
            "SELECT v FROM t WHERE rowid = 999;",
            Array.Empty<string>());
    }

    [Test]
    public void RowidGreaterThanMatchesSqlite()
    {
        AssertSelectMatchesSqlite(
            "rowid-gt",
            "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);",
            "INSERT INTO t VALUES (1,'a'),(2,'b'),(3,'c');",
            "SELECT v FROM t WHERE rowid > 1 ORDER BY rowid;",
            new[] { "b", "c" });
    }

    [Test]
    public void RowidBetweenRangeMatchesSqlite()
    {
        AssertSelectMatchesSqlite(
            "rowid-between",
            "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);",
            "INSERT INTO t VALUES (1,'a'),(2,'b'),(3,'c');",
            "SELECT v FROM t WHERE rowid BETWEEN 1 AND 2 ORDER BY rowid;",
            new[] { "a", "b" });
    }

    [Test]
    public void RowidAliasPointLookupMatchesSqlite()
    {
        // id is INTEGER PRIMARY KEY, so `id = 2` is a rowid-alias point lookup. This is the
        // shape a SeekRowid emission must recognize (RowidAliasColumnIndex) in Step 3.
        AssertSelectMatchesSqlite(
            "rowid-alias-eq",
            "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);",
            "INSERT INTO t VALUES (1,'a'),(2,'b'),(3,'c');",
            "SELECT v FROM t WHERE id = 2;",
            new[] { "b" });
    }

    [Test]
    public void OutOfOrderRowidInsertPointLookupMatchesSqlite()
    {
        // Explicit out-of-order rowid INSERTs break the RowIds sort invariant within a session
        // (CommitInserts appends in insert order, not rowid order). A point lookup must still
        // find the row regardless of whether RowIds is sorted: this is the correctness guard
        // that justifies the linear-search SeekRowid handler over an assumed-sorted BinarySearch.
        AssertSelectMatchesSqlite(
            "rowid-out-of-order-eq",
            "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);",
            "INSERT INTO t(rowid,v) VALUES (10,'x'),(3,'y'),(7,'z');",
            "SELECT v FROM t WHERE rowid = 7;",
            new[] { "z" });
    }

    [Test]
    public void OutOfOrderRowidInsertOrderedByRowidMatchesSqlite()
    {
        // ORDER BY rowid over an unsorted RowIds projection must still yield rowid-ascending
        // output on both engines; guards that ordering is by rowid value, not cursor position.
        AssertSelectMatchesSqlite(
            "rowid-out-of-order-orderby",
            "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);",
            "INSERT INTO t(rowid,v) VALUES (10,'x'),(3,'y'),(7,'z');",
            "SELECT v FROM t ORDER BY rowid;",
            new[] { "y", "z", "x" });
    }

    // ORDER BY over a non-unique column with out-of-order explicit rowids isolates stable
    // tie-breaking. Both engines scan a rowid table in physical rowid order, so tied keys must
    // produce the same result as SQLite.
    [Test]
    public void OrderByNonUniqueColumnWithOutOfOrderRowidsMatchesSqlite()
    {
        // g='a' ties rowid 10 ('ten') and rowid 3 ('three'), ordered stably as 3 then 10.
        AssertSelectMatchesSqlite(
            "orderby-nonunique-outoforder-asc",
            "CREATE TABLE t(id INTEGER PRIMARY KEY, g TEXT, v TEXT);",
            "INSERT INTO t(rowid,g,v) VALUES (10,'a','ten'),(3,'a','three'),(7,'b','seven');",
            "SELECT v FROM t ORDER BY g;",
            new[] { "three", "ten", "seven" });
    }

    [Test]
    public void OrderByNonUniqueColumnDescWithOutOfOrderRowidsMatchesSqlite()
    {
        // DESC: g='b' ('seven') first, then the g='a' ties remain in rowid order.
        AssertSelectMatchesSqlite(
            "orderby-nonunique-outoforder-desc",
            "CREATE TABLE t(id INTEGER PRIMARY KEY, g TEXT, v TEXT);",
            "INSERT INTO t(rowid,g,v) VALUES (10,'a','ten'),(3,'a','three'),(7,'b','seven');",
            "SELECT v FROM t ORDER BY g DESC;",
            new[] { "seven", "three", "ten" });
    }

    private static void AssertSelectMatchesSqlite(
        string suffix,
        string ddl,
        string insert,
        string select,
        IReadOnlyList<string> expected)
    {
        var managedPath = CreateDatabasePath($"{suffix}-managed");
        var sqlitePath = CreateDatabasePath($"{suffix}-sqlite");
        try
        {
            using var managed = OpenManaged(managedPath);
            using var sqlite = OpenSqlite(sqlitePath);
            ExecuteNonQuery(managed, ddl);
            ExecuteNonQuery(sqlite, ddl);
            ExecuteNonQuery(managed, insert);
            ExecuteNonQuery(sqlite, insert);

            var managedRows = ReadFirstColumn(managed, select);
            var sqliteRows = ReadFirstColumn(sqlite, select);

            // The SQLite oracle (real e_sqlite3) is the ground truth; assert it agrees with the
            // hardcoded expectation first so a both-wrong managed result cannot masquerade as green.
            sqliteRows.Should().Equal(expected, because: $"SQLite oracle must return the expected rows for: {select}");
            managedRows.Should().Equal(sqliteRows, because: $"managed must match SQLite for: {select}");
        }
        finally
        {
            DeleteDatabase(managedPath);
            DeleteDatabase(sqlitePath);
        }
    }

    private static SqliteConnection OpenManaged(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False;Local Provider=Managed");
        connection.Open();
        return connection;
    }

    private static MsData.SqliteConnection OpenSqlite(string path)
    {
        var connection = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    private static void ExecuteNonQuery(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static List<string> ReadFirstColumn(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var values = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            values.Add(reader.GetString(0));
        return values;
    }

    private static string CreateDatabasePath(string suffix)
    {
        var directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "seek-parity");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{suffix}-{Guid.NewGuid():N}.db");
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }
}
