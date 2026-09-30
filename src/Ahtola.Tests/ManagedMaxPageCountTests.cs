using AwesomeAssertions;
using Ahtola.Core;
using Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// <c>PRAGMA page_count</c> accuracy and <c>PRAGMA max_page_count</c> enforcement, differential
/// against the bundled SQLite where the two engines can run the same script. SQLite semantics
/// covered: the ceiling is per connection and never persisted; setting it clamps to the current
/// size; a write that would grow the database past it fails with <c>SQLITE_FULL</c> ("database or
/// disk is full", result code 13); the failing statement is rolled back, and inside an explicit
/// transaction a single-row write without a statement journal rolls back the whole transaction
/// while any other statement only rolls back itself. MVCC (Turso) only hits the ceiling at
/// checkpoint.
/// </summary>
public sealed class ManagedMaxPageCountTests
{
    private const string Full = "ERR:database or disk is full";
    private const string Rows100x400 =
        "WITH RECURSIVE c(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM c WHERE i < 100) "
        + "INSERT INTO t SELECT zeroblob(400) FROM c";

    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ahtola-max-page-count-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static IEnumerable<TestCaseData> MemoryPageCountScripts()
    {
        yield return new TestCaseData(
            (object)new[]
            {
                "PRAGMA page_count", "PRAGMA page_size=1024", "CREATE TABLE t(a)", "PRAGMA page_count",
                Rows100x400, "PRAGMA page_count", "PRAGMA freelist_count",
                "DELETE FROM t WHERE rowid > 50", "PRAGMA page_count", "PRAGMA freelist_count",
                "VACUUM", "PRAGMA page_count", "PRAGMA freelist_count",
            }).SetName("MemoryPageCount_RowsDeleteVacuum_1024");
        yield return new TestCaseData(
            (object)new[]
            {
                "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)",
                "WITH RECURSIVE c(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM c WHERE i < 2000) "
                    + "INSERT INTO t SELECT i, 'row ' || i FROM c",
                "PRAGMA page_count", "CREATE INDEX ib ON t(b)", "PRAGMA page_count",
                "DROP INDEX ib", "PRAGMA page_count", "PRAGMA freelist_count",
            }).SetName("MemoryPageCount_IndexedTable_4096");
        yield return new TestCaseData(
            (object)new[]
            {
                "PRAGMA page_size=1024", "CREATE TABLE t(a)", "INSERT INTO t VALUES (zeroblob(4000))",
                "PRAGMA page_count", "CREATE TABLE u(a, b)", "CREATE TABLE v(a, b)", "PRAGMA page_count",
            }).SetName("MemoryPageCount_OverflowAndSchema_1024");
    }

    [TestCaseSource(nameof(MemoryPageCountScripts))]
    public void InMemoryPageCountMatchesSqlite(string[] script)
        => RunAhtolaMemory(script).Should().Equal(RunSqlite(script, "Data Source=:memory:"));

    [TestCase("delete")]
    [TestCase("wal")]
    public void FileBackedPageCountMatchesSqlite(string journalMode)
    {
        string[] script =
        [
            $"PRAGMA journal_mode={journalMode}", "CREATE TABLE t(a)", "PRAGMA page_count",
            Rows100x400.Replace("zeroblob(400)", "zeroblob(1500)", StringComparison.Ordinal),
            "PRAGMA page_count", "CREATE INDEX ta ON t(length(a))", "PRAGMA page_count",
        ];

        var ahtola = RunAhtolaFile(script, Path.Combine(_directory, "ahtola.db"));
        var sqlite = RunSqlite(script, $"Data Source={Path.Combine(_directory, "sqlite.db")};Pooling=False");
        ahtola.Should().Equal(sqlite);
        ahtola[1].Should().Be("2");
    }

    [Test]
    public void MaxPageCountGetSetAndClampMatchSqlite()
    {
        string[] script =
        [
            "PRAGMA max_page_count", "PRAGMA max_page_count = 1000", "PRAGMA max_page_count",
            "PRAGMA max_page_count = 0", "PRAGMA max_page_count = -5", "PRAGMA max_page_count",
            "CREATE TABLE t(a)", Rows100x400, "PRAGMA page_count", "PRAGMA max_page_count = 1",
            "PRAGMA max_page_count = 99999999999", "PRAGMA max_page_count",
        ];

        var ahtola = RunAhtolaMemory(script);
        ahtola.Should().Equal(RunSqlite(script, "Data Source=:memory:"));
        ahtola[0].Should().Be("4294967294");
        ahtola[^1].Should().Be("4294967294");
    }

    [Test]
    public void MaxPageCountIsPerConnectionAndNotPersisted()
    {
        var path = Path.Combine(_directory, "limit.db");
        using (var database = EmbeddedDatabase.OpenFile(path))
        using (var first = database.Connect())
        using (var second = database.Connect())
        {
            Execute(first, "CREATE TABLE t(a)");
            Scalar(first, "PRAGMA max_page_count = 2").Should().Be("2");
            Scalar(second, "PRAGMA max_page_count").Should().Be("4294967294");

            // Only the connection that set the ceiling is held to it.
            Execute(second, "INSERT INTO t VALUES (zeroblob(20000))");
            int.Parse(Scalar(first, "PRAGMA page_count")).Should().BeGreaterThan(2);
            Scalar(first, "PRAGMA max_page_count").Should().Be("2");
            Catch(first, "INSERT INTO t VALUES (zeroblob(20000))").Should().Be(Full);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path);
        using var connection = reopened.Connect();
        Scalar(connection, "PRAGMA max_page_count").Should().Be("4294967294");
    }

    [Test]
    public void CorpusDatabaseFullCaseRaisesSqliteFullWithoutPrefix()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "PRAGMA page_size = 1024");
        Execute(connection, "PRAGMA max_page_count = 2");
        Execute(connection, "CREATE TABLE f (x)");

        var failure = Assert.Catch<EmbeddedSqlException>(
            () => Execute(connection, "INSERT INTO f VALUES (zeroblob(4000))"))!;
        failure.Message.Should().Be("database or disk is full");
        failure.SqliteErrorCode.Should().Be(13);
        Scalar(connection, "SELECT count(*) FROM f").Should().Be("0");
        Scalar(connection, "PRAGMA page_count").Should().Be("2");
        Scalar(connection, "PRAGMA integrity_check").Should().Be("ok");
    }

    private static IEnumerable<TestCaseData> TransactionScripts()
    {
        const string Setup = "PRAGMA page_size=1024";
        const string FiftyRows =
            "WITH RECURSIVE c(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM c WHERE i < 50) "
            + "INSERT INTO f SELECT printf('%.*c', 300, 'x') || i FROM c";
        yield return Script(
            "SingleRowInsertRollsBackTheTransaction",
            Setup, "CREATE TABLE f(x)", "PRAGMA max_page_count=4", "BEGIN", "INSERT INTO f VALUES (1)",
            "INSERT INTO f VALUES (zeroblob(8000))", "SELECT count(*) FROM f", "COMMIT", "SELECT count(*) FROM f");
        yield return Script(
            "MultiRowInsertRollsBackOnlyTheStatement",
            Setup, "CREATE TABLE f(x UNIQUE)", "PRAGMA max_page_count=4", "BEGIN", "INSERT INTO f VALUES (1)",
            FiftyRows, "SELECT count(*) FROM f", "COMMIT", "SELECT count(*) FROM f");
        yield return Script(
            "CreateTableRollsBackOnlyTheStatement",
            Setup, "CREATE TABLE f(x)", "PRAGMA max_page_count=3", "BEGIN", "INSERT INTO f VALUES (1)",
            "CREATE TABLE g(y)", "CREATE TABLE h(y)", "SELECT count(*) FROM f", "COMMIT",
            "SELECT count(*) FROM f", "SELECT count(*) FROM sqlite_schema");
        yield return Script(
            "CreateIndexRollsBackOnlyTheStatement",
            Setup, "CREATE TABLE f(x)", "INSERT INTO f VALUES (1)", "PRAGMA max_page_count=2", "BEGIN",
            "INSERT INTO f VALUES (2)", "CREATE INDEX fx ON f(x)", "SELECT count(*) FROM f", "COMMIT",
            "SELECT count(*) FROM f", "SELECT count(*) FROM sqlite_schema");
        yield return Script(
            "MultiRowUpdateRollsBackOnlyTheStatement",
            Setup, "CREATE TABLE f(x)", "INSERT INTO f VALUES (1)", "INSERT INTO f VALUES (2)",
            "PRAGMA max_page_count=2", "BEGIN", "INSERT INTO f VALUES (3)", "UPDATE f SET x = zeroblob(3000)",
            "SELECT count(*), sum(length(x)) FROM f", "COMMIT", "SELECT count(*) FROM f");
        yield return Script(
            "SingleRowidUpdateRollsBackTheTransaction",
            Setup, "CREATE TABLE f(x)", "INSERT INTO f VALUES (1)", "INSERT INTO f VALUES (2)",
            "PRAGMA max_page_count=2", "BEGIN", "INSERT INTO f VALUES (3)",
            "UPDATE f SET x = zeroblob(3000) WHERE rowid = 1", "SELECT count(*), sum(length(x)) FROM f",
            "COMMIT", "SELECT count(*) FROM f");
        yield return Script(
            "AutocommitCreateTableAtTheCeiling",
            "PRAGMA max_page_count=1", "CREATE TABLE t(a)", "PRAGMA max_page_count=2", "CREATE TABLE t(a)",
            "CREATE TABLE t2(b)", "SELECT count(*) FROM sqlite_schema", "PRAGMA page_count");

        static TestCaseData Script(string name, params string[] statements)
            => new TestCaseData((object)statements).SetName("InMemoryTransaction_" + name);
    }

    [TestCaseSource(nameof(TransactionScripts))]
    public void InMemoryDatabaseFullTransactionSemanticsMatchSqlite(string[] script)
    {
        var ahtola = RunAhtolaMemory(script);
        ahtola.Should().Equal(RunSqlite(script, "Data Source=:memory:"));
        ahtola.Should().Contain(Full);
    }

    [TestCase("delete")]
    [TestCase("wal")]
    public void FileBackedDatabaseFullRollsBackAndStaysReopenable(string journalMode)
    {
        var path = Path.Combine(_directory, $"full-{journalMode}.db");
        using (var database = EmbeddedDatabase.OpenFile(path))
        using (var connection = database.Connect())
        {
            Execute(connection, $"PRAGMA journal_mode={journalMode}");
            Execute(connection, "CREATE TABLE t(a)");
            Execute(connection, "INSERT INTO t VALUES (1)");
            Scalar(connection, "PRAGMA page_count").Should().Be("2");
            Scalar(connection, "PRAGMA max_page_count = 3").Should().Be("3");

            // Autocommit INSERT, CREATE INDEX and CREATE TABLE that need to grow past the ceiling.
            Catch(connection, "INSERT INTO t VALUES (zeroblob(20000))").Should().Be(Full);
            Execute(connection, "CREATE TABLE u(a)");
            Catch(connection, "CREATE INDEX ua ON u(a)").Should().Be(Full);
            Catch(connection, "CREATE TABLE v(a)").Should().Be(Full);
            Scalar(connection, "PRAGMA page_count").Should().Be("3");
            Scalar(connection, "SELECT count(*) FROM sqlite_schema").Should().Be("2");

            // Explicit transaction: a multi-row statement is undone alone and COMMIT keeps the rest.
            Execute(connection, "BEGIN");
            Execute(connection, "INSERT INTO t VALUES (2)");
            Catch(
                    connection,
                    "WITH RECURSIVE c(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM c WHERE i < 20) "
                    + "INSERT INTO t SELECT zeroblob(2000) FROM c")
                .Should().Be(Full);
            Execute(connection, "INSERT INTO t VALUES (3)");
            Execute(connection, "COMMIT");

            // A single-row write without a statement journal takes the transaction with it.
            Execute(connection, "BEGIN");
            Execute(connection, "INSERT INTO t VALUES (4)");
            Catch(connection, "INSERT INTO t VALUES (zeroblob(20000))").Should().Be(Full);
            Catch(connection, "COMMIT").Should().Be("ERR:cannot commit - no transaction is active");

            Scalar(connection, "SELECT group_concat(a) FROM t WHERE typeof(a) = 'integer'").Should().Be("1,2,3");
            Scalar(connection, "PRAGMA page_count").Should().Be("3");
            Scalar(connection, "PRAGMA integrity_check").Should().Be("ok");

            // Raising the ceiling lets the same write through.
            Scalar(connection, "PRAGMA max_page_count = 20").Should().Be("20");
            Execute(connection, "INSERT INTO t VALUES (zeroblob(20000))");
        }

        using (var reopened = EmbeddedDatabase.OpenFile(path))
        using (var connection = reopened.Connect())
        {
            Scalar(connection, "PRAGMA integrity_check").Should().Be("ok");
            Scalar(connection, "SELECT count(*) FROM t").Should().Be("4");
        }

        using var sqlite = new SqliteConnection($"Data Source={path};Pooling=False");
        sqlite.Open();
        using var command = sqlite.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        command.ExecuteScalar().Should().Be("ok");
    }

    [Test]
    public void MvccAutocommitWriteIsHeldToTheCeiling()
    {
        // Managed MVCC autocommit writes build their b-tree pages as they commit (nothing waits in
        // the logical log), so, like every other page allocation, they are held to the ceiling.
        var path = Path.Combine(_directory, "mvcc-autocommit.db");
        using (var database = EmbeddedDatabase.OpenFile(path))
        using (var connection = database.Connect())
        {
            Execute(connection, "PRAGMA journal_mode=mvcc");
            Execute(connection, "CREATE TABLE t(x TEXT)");
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
            Scalar(connection, "PRAGMA max_page_count = 5").Should().Be("5");
            Catch(connection, SixtyLargeRows).Should().Be(Full);
            Scalar(connection, "SELECT count(*) FROM t").Should().Be("0");
            Execute(connection, "INSERT INTO t VALUES ('small')");
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
            Scalar(connection, "PRAGMA integrity_check").Should().Be("ok");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path);
        using var reopenedConnection = reopened.Connect();
        Scalar(reopenedConnection, "SELECT count(*) FROM t").Should().Be("1");
        Scalar(reopenedConnection, "PRAGMA integrity_check").Should().Be("ok");
    }

    [Test]
    public void MvccConcurrentCommitIsNotHeldToTheCeiling()
    {
        // Turso: an MVCC commit only appends to the logical log and never consults
        // max_page_count; the ceiling applies when pages are materialized. The managed commit
        // publishes the log frame before it materializes pages, so it must not fail afterwards.
        var path = Path.Combine(_directory, "mvcc-concurrent.db");
        using (var database = EmbeddedDatabase.OpenFile(path))
        using (var connection = database.Connect())
        {
            Execute(connection, "PRAGMA journal_mode=mvcc");
            Execute(connection, "CREATE TABLE t(x TEXT)");
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
            Scalar(connection, "PRAGMA max_page_count = 5").Should().Be("5");
            Execute(connection, "BEGIN CONCURRENT");
            Execute(connection, SixtyLargeRows);
            Execute(connection, "COMMIT");
            Scalar(connection, "SELECT count(*) FROM t").Should().Be("60");
            Scalar(connection, "PRAGMA max_page_count").Should().Be("5");
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
            Scalar(connection, "SELECT count(*) FROM t").Should().Be("60");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path);
        using var reopenedConnection = reopened.Connect();
        Scalar(reopenedConnection, "PRAGMA max_page_count").Should().Be("4294967294");
        Scalar(reopenedConnection, "SELECT count(*) FROM t").Should().Be("60");
        Scalar(reopenedConnection, "PRAGMA integrity_check").Should().Be("ok");
    }

    private const string SixtyLargeRows =
        "WITH RECURSIVE c(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM c WHERE i < 60) "
        + "INSERT INTO t SELECT printf('%.*c', 1800, 'x') FROM c";

    private static List<string> RunAhtolaMemory(IEnumerable<string> script)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        return RunAhtola(connection, script);
    }

    private static List<string> RunAhtolaFile(IEnumerable<string> script, string path)
    {
        using var database = EmbeddedDatabase.OpenFile(path);
        using var connection = database.Connect();
        return RunAhtola(connection, script);
    }

    private static List<string> RunAhtola(EmbeddedConnection connection, IEnumerable<string> script)
    {
        var output = new List<string>();
        foreach (var sql in script)
        {
            try
            {
                using var statement = connection.Prepare(sql);
                while (statement.Step() == StatementStepResult.Row)
                {
                    output.Add(string.Join(
                        ",",
                        Enumerable.Range(0, statement.ColumnCount).Select(index => Show(statement.GetValue(index)))));
                }
            }
            catch (EmbeddedSqlException exception)
            {
                if (exception.Message == "database or disk is full")
                    exception.SqliteErrorCode.Should().Be(13);
                output.Add("ERR:" + exception.Message);
            }
        }

        return output;
    }

    private static List<string> RunSqlite(IEnumerable<string> script, string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        var output = new List<string>();
        foreach (var sql in script)
        {
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    output.Add(string.Join(
                        ",",
                        Enumerable.Range(0, reader.FieldCount).Select(index => Convert.ToString(reader.GetValue(index)))));
                }
            }
            catch (SqliteException exception)
            {
                output.Add("ERR:" + exception.Message
                    .Replace($"SQLite Error {exception.SqliteErrorCode}: '", string.Empty, StringComparison.Ordinal)
                    .TrimEnd('.', '\''));
            }
        }

        return output;
    }

    private static string Show(SqlValue value) => value.Kind switch
    {
        SqlValueKind.Null => string.Empty,
        SqlValueKind.Integer => value.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture),
        SqlValueKind.Text => value.AsText(),
        _ => value.ToString() ?? string.Empty,
    };

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    private static string Scalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        return statement.Step() == StatementStepResult.Row
            ? Show(statement.GetValue(0))
            : throw new InvalidOperationException($"{sql} produced no row.");
    }

    private static string Catch(EmbeddedConnection connection, string sql)
    {
        var failure = Assert.Catch<EmbeddedSqlException>(() => Execute(connection, sql))!;
        if (failure.Message == "database or disk is full")
            failure.SqliteErrorCode.Should().Be(13);
        return "ERR:" + failure.Message;
    }
}
