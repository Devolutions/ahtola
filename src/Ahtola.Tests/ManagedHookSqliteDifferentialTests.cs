using AwesomeAssertions;
using SQLitePCL;
using Ahtola.Core;
using Ahtola.Data.Sqlite;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// Runs the load-bearing hook scenarios against both the managed engine and real SQLite
/// (e_sqlite3 through SQLitePCLRaw) and asserts they agree.
/// </summary>
/// <remarks>
/// The managed hook semantics were derived from measurements of real SQLite rather than from the
/// documentation, and several of them are surprising -- REPLACE conflict resolution does not report
/// its implicit delete, WITHOUT ROWID tables report nothing at all, and an unfiltered
/// <c>DELETE FROM t</c> reports nothing because of the truncate optimization. Re-measuring here
/// keeps the managed engine pinned to observed behavior instead of a snapshot of it in a comment.
/// The one intentional divergence is asserted explicitly so it cannot drift unnoticed.
/// </remarks>
public sealed class ManagedHookSqliteDifferentialTests
{
    [OneTimeSetUp]
    public void InitializeSqlite() => Batteries_V2.Init();

    [Test]
    [TestCase("INSERT INTO t VALUES (1, 'one'), (2, 'two')")]
    [TestCase("INSERT INTO t VALUES (1, 'one'); UPDATE t SET b = 'ONE' WHERE a = 1")]
    [TestCase("INSERT INTO t VALUES (1, 'one'); DELETE FROM t WHERE a = 1")]
    [TestCase("INSERT INTO t VALUES (1, 'one'); UPDATE t SET a = 9 WHERE a = 1")]
    [TestCase("INSERT INTO t VALUES (1, 'one'); INSERT OR REPLACE INTO t VALUES (1, 'again')")]
    [TestCase("INSERT INTO t VALUES (1, 'one'); INSERT INTO t SELECT a + 10, b FROM t")]
    public void RowChangeNotificationsMatchRealSqlite(string script)
    {
        const string schema = "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)";
        RunManaged(schema, script).Should().Equal(RunSqlite(schema, script));
    }

    [Test]
    public void UniqueIndexReplaceConflictMatchesRealSqlite()
    {
        const string schema = "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT UNIQUE)";
        const string script = "INSERT INTO t VALUES (1, 'one'); INSERT OR REPLACE INTO t VALUES (2, 'one')";
        RunManaged(schema, script).Should().Equal(RunSqlite(schema, script));
    }

    [Test]
    public void AutoIncrementSequenceMaintenanceMatchesRealSqlite()
    {
        const string schema = "CREATE TABLE t(a INTEGER PRIMARY KEY AUTOINCREMENT, b TEXT)";
        const string script = "INSERT INTO t(b) VALUES ('one'); INSERT INTO t(b) VALUES ('two')";
        RunManaged(schema, script).Should().Equal(RunSqlite(schema, script));
    }

    [Test]
    public void WithoutRowidTablesMatchRealSqlite()
    {
        const string schema = "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT) WITHOUT ROWID";
        const string script = "INSERT INTO t VALUES (1, 'one'); UPDATE t SET b = 'ONE'; DELETE FROM t WHERE a = 1";
        RunManaged(schema, script).Should().BeEmpty();
        RunSqlite(schema, script).Should().BeEmpty();
    }

    [Test]
    public void CreateTableAsSelectMatchesRealSqlite()
    {
        const string schema = "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)";
        const string script = "INSERT INTO t VALUES (1, 'one'); CREATE TABLE copy AS SELECT * FROM t";
        RunManaged(schema, script).Should().Equal(RunSqlite(schema, script));
    }

    [Test]
    public void UnfilteredDeleteIsTheOneDocumentedDivergenceFromRealSqlite()
    {
        const string schema = "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)";
        const string script = "INSERT INTO t VALUES (1, 'one'), (2, 'two'); DELETE FROM t";

        // Real SQLite replaces an unqualified DELETE with a truncate that visits no rows, so no
        // row change is reported. The managed engine has no truncate path, and dropping the
        // notifications to imitate one would leave a change-tracking consumer silently stale.
        RunSqlite(schema, script)
            .Should()
            .Equal("18 main.t 1", "18 main.t 2");
        RunManaged(schema, script)
            .Should()
            .Equal("18 main.t 1", "18 main.t 2", "9 main.t 1", "9 main.t 2");
    }

    [Test]
    public void CommitHookVetoBehavesLikeRealSqlite()
    {
        var managed = VetoManaged();
        var native = VetoSqlite();

        managed.Should().Be(native);
        managed.Should().Be("commit|rollback errorCode=19 rows=1 autocommit=True");
    }

    [Test]
    [TestCase("BEGIN; ROLLBACK")]
    [TestCase("BEGIN; SELECT count(*) FROM t; ROLLBACK")]
    [TestCase("BEGIN; INSERT INTO t VALUES (2, 'two'); ROLLBACK")]
    public void RollbackNotificationsMatchRealSqlite(string script)
    {
        RunManagedTransactionLog(script).Should().Equal(RunSqliteTransactionLog(script));
    }

    [Test]
    public void AFailedAutocommitMutationRollsBackLikeRealSqlite()
    {
        RunManagedTransactionLog("INSERT INTO t VALUES (1, 'duplicate')", expectFailure: true)
            .Should()
            .Equal(RunSqliteTransactionLog("INSERT INTO t VALUES (1, 'duplicate')", expectFailure: true))
            .And
            .Equal("rollback");
    }

    [Test]
    public void AFailedStatementInsideATransactionDoesNotRollBackLikeRealSqlite()
    {
        const string script = "BEGIN; INSERT INTO t VALUES (1, 'duplicate')";
        RunManagedTransactionLog(script, expectFailure: true)
            .Should()
            .Equal(RunSqliteTransactionLog(script, expectFailure: true))
            .And
            .BeEmpty();
    }

    private static List<string> RunManaged(string schema, string script)
    {
        var log = new List<string>();
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed;Pooling=False");
        connection.Open();
        ExecuteScript(connection, schema);
        connection.SetUpdateHook(change =>
            log.Add($"{(int)change.Operation} {change.Database}.{change.Table} {change.RowId}"));
        ExecuteScript(connection, script);
        return log;
    }

    private static List<string> RunSqlite(string schema, string script)
    {
        var log = new List<string>();
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        ExecuteScript(connection, schema);
        var hook = new delegate_update((_, operation, database, table, rowId) =>
            log.Add($"{operation} {database.utf8_to_string()}.{table.utf8_to_string()} {rowId}"));
        raw.sqlite3_update_hook(connection.Handle!, hook, null);
        ExecuteScript(connection, script);
        raw.sqlite3_update_hook(connection.Handle!, (delegate_update?)null, null);
        GC.KeepAlive(hook);
        return log;
    }

    private static string VetoManaged()
    {
        var log = new List<string>();
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed;Pooling=False");
        connection.Open();
        ExecuteScript(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT); INSERT INTO t VALUES (1, 'one')");
        connection.SetCommitHook(() =>
        {
            log.Add("commit");
            return false;
        });
        connection.SetRollbackHook(() => log.Add("rollback"));

        var error = Assert.Throws<SqliteException>(() => ExecuteScript(connection, "INSERT INTO t VALUES (2, 'two')"));
        var hooks = string.Join('|', log);
        connection.SetCommitHook(null);
        connection.SetRollbackHook(null);

        using var count = connection.CreateCommand();
        count.CommandText = "SELECT count(*) FROM t";
        var rows = Convert.ToInt64(count.ExecuteScalar());
        var autocommit = !IsInExplicitTransaction(connection);
        return $"{hooks} errorCode={error!.SqliteErrorCode} rows={rows} autocommit={autocommit}";
    }

    private static string VetoSqlite()
    {
        var log = new List<string>();
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        ExecuteScript(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT); INSERT INTO t VALUES (1, 'one')");
        var commit = new delegate_commit(_ =>
        {
            log.Add("commit");
            return 1;
        });
        var rollback = new delegate_rollback(_ => log.Add("rollback"));
        raw.sqlite3_commit_hook(connection.Handle!, commit, null);
        raw.sqlite3_rollback_hook(connection.Handle!, rollback, null);

        var error = Assert.Throws<MsData.SqliteException>(
            () => ExecuteScript(connection, "INSERT INTO t VALUES (2, 'two')"));
        var hooks = string.Join('|', log);
        raw.sqlite3_commit_hook(connection.Handle!, null, null);
        raw.sqlite3_rollback_hook(connection.Handle!, null, null);

        using var count = connection.CreateCommand();
        count.CommandText = "SELECT count(*) FROM t";
        var rows = Convert.ToInt64(count.ExecuteScalar());
        var autocommit = raw.sqlite3_get_autocommit(connection.Handle!) != 0;
        GC.KeepAlive(commit);
        GC.KeepAlive(rollback);
        return $"{hooks} errorCode={error!.SqliteErrorCode} rows={rows} autocommit={autocommit}";
    }

    private static List<string> RunManagedTransactionLog(string script, bool expectFailure = false)
    {
        var log = new List<string>();
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed;Pooling=False");
        connection.Open();
        ExecuteScript(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT); INSERT INTO t VALUES (1, 'one')");
        connection.SetCommitHook(() =>
        {
            log.Add("commit");
            return true;
        });
        connection.SetRollbackHook(() => log.Add("rollback"));
        RunScriptAllowingFailure(sql => ExecuteScript(connection, sql), script, expectFailure);
        connection.SetCommitHook(null);
        connection.SetRollbackHook(null);
        return log;
    }

    private static List<string> RunSqliteTransactionLog(string script, bool expectFailure = false)
    {
        var log = new List<string>();
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        ExecuteScript(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT); INSERT INTO t VALUES (1, 'one')");
        var commit = new delegate_commit(_ =>
        {
            log.Add("commit");
            return 0;
        });
        var rollback = new delegate_rollback(_ => log.Add("rollback"));
        raw.sqlite3_commit_hook(connection.Handle!, commit, null);
        raw.sqlite3_rollback_hook(connection.Handle!, rollback, null);
        RunScriptAllowingFailure(sql => ExecuteScript(connection, sql), script, expectFailure);
        raw.sqlite3_commit_hook(connection.Handle!, null, null);
        raw.sqlite3_rollback_hook(connection.Handle!, null, null);
        GC.KeepAlive(commit);
        GC.KeepAlive(rollback);
        return log;
    }

    private static void RunScriptAllowingFailure(Action<string> execute, string script, bool expectFailure)
    {
        var statements = script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < statements.Length; index++)
        {
            var isLast = index == statements.Length - 1;
            if (expectFailure && isLast)
            {
                Assert.Throws(Is.InstanceOf<Exception>(), () => execute(statements[index]));
                continue;
            }

            execute(statements[index]);
        }
    }

    private static bool IsInExplicitTransaction(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "BEGIN";
            command.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            return true;
        }

        using var rollback = connection.CreateCommand();
        rollback.CommandText = "ROLLBACK";
        rollback.ExecuteNonQuery();
        return false;
    }

    private static void ExecuteScript(System.Data.Common.DbConnection connection, string script)
    {
        foreach (var statement in script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }
    }
}
