using AwesomeAssertions;
using ManagedSqlite = Ahtola.Data.Sqlite;
using NativeSqlite = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// Pins the primary and extended result codes the <c>Microsoft.Data.Sqlite</c>-compatible
/// facade reports for engine failures against the bundled <c>e_sqlite3</c> through
/// <c>Microsoft.Data.Sqlite</c>. Consumers branch on <c>SqliteErrorCode</c> /
/// <c>SqliteExtendedErrorCode</c> (for example 19/2067 for a UNIQUE violation), so every
/// failure class here must surface the same pair SQLite does, not a generic 1/1.
/// </summary>
[NonParallelizable]
public sealed class SqliteErrorCodeDifferentialTests
{
    public static IEnumerable<TestCaseData> StatementFailures()
    {
        yield return Case("unique-column", "CREATE TABLE t(a UNIQUE); INSERT INTO t VALUES (1);", "INSERT INTO t VALUES (1);", 19, 2067);
        yield return Case("unique-index", "CREATE TABLE t(a); CREATE UNIQUE INDEX t_a ON t(a); INSERT INTO t VALUES (1);", "INSERT INTO t VALUES (1);", 19, 2067);
        yield return Case("unique-update", "CREATE TABLE t(a UNIQUE); INSERT INTO t VALUES (1), (2);", "UPDATE t SET a = 1 WHERE a = 2;", 19, 2067);
        yield return Case("rowid-primary-key", "CREATE TABLE t(id INTEGER PRIMARY KEY); INSERT INTO t VALUES (1);", "INSERT INTO t VALUES (1);", 19, 1555);
        yield return Case("text-primary-key", "CREATE TABLE t(a TEXT PRIMARY KEY); INSERT INTO t VALUES ('x');", "INSERT INTO t VALUES ('x');", 19, 1555);
        yield return Case("without-rowid-primary-key", "CREATE TABLE t(a TEXT PRIMARY KEY, b) WITHOUT ROWID; INSERT INTO t VALUES ('x', 1);", "INSERT INTO t VALUES ('x', 2);", 19, 1555);
        yield return Case("not-null", "CREATE TABLE t(a NOT NULL);", "INSERT INTO t VALUES (NULL);", 19, 1299);
        yield return Case("not-null-update", "CREATE TABLE t(a NOT NULL); INSERT INTO t VALUES (1);", "UPDATE t SET a = NULL;", 19, 1299);
        yield return Case("check-column", "CREATE TABLE t(a CHECK (a > 0));", "INSERT INTO t VALUES (0);", 19, 275);
        yield return Case("check-table", "CREATE TABLE t(a, b, CONSTRAINT positive CHECK (a < b));", "INSERT INTO t VALUES (2, 1);", 19, 275);
        yield return Case(
            "foreign-key-immediate",
            "PRAGMA foreign_keys = ON; CREATE TABLE p(id INTEGER PRIMARY KEY); CREATE TABLE c(pid REFERENCES p(id));",
            "INSERT INTO c VALUES (1);",
            19,
            787);
        yield return Case(
            "foreign-key-parent-delete",
            "PRAGMA foreign_keys = ON; CREATE TABLE p(id INTEGER PRIMARY KEY); CREATE TABLE c(pid REFERENCES p(id)); INSERT INTO p VALUES (1); INSERT INTO c VALUES (1);",
            "DELETE FROM p;",
            19,
            787);
        yield return Case(
            "foreign-key-deferred-commit",
            "PRAGMA foreign_keys = ON; CREATE TABLE p(id INTEGER PRIMARY KEY); CREATE TABLE c(pid REFERENCES p(id) DEFERRABLE INITIALLY DEFERRED);",
            "BEGIN; INSERT INTO c VALUES (1); COMMIT;",
            19,
            787);
        yield return Case(
            "trigger-raise-abort",
            "CREATE TABLE t(a); CREATE TRIGGER tr BEFORE INSERT ON t BEGIN SELECT RAISE(ABORT, 'nope'); END;",
            "INSERT INTO t VALUES (1);",
            19,
            1811);
        yield return Case(
            "trigger-raise-fail",
            "CREATE TABLE t(a); CREATE TRIGGER tr BEFORE INSERT ON t BEGIN SELECT RAISE(FAIL, 'nope'); END;",
            "INSERT INTO t VALUES (1);",
            19,
            1811);
        yield return Case(
            "trigger-raise-rollback",
            "CREATE TABLE t(a); CREATE TRIGGER tr BEFORE INSERT ON t BEGIN SELECT RAISE(ROLLBACK, 'nope'); END;",
            "INSERT INTO t VALUES (1);",
            19,
            1811);
        yield return Case("hidden-rowid", "CREATE TABLE t(a); INSERT INTO t(rowid, a) VALUES (1, 1);", "INSERT INTO t(rowid, a) VALUES (1, 2);", 19, 2579);
        yield return Case("insert-select-duplicate", "CREATE TABLE t(a UNIQUE); CREATE TABLE s(a); INSERT INTO s VALUES (1), (1);", "INSERT INTO t SELECT a FROM s;", 19, 2067);
        yield return Case("or-fail-unique", "CREATE TABLE t(a UNIQUE); INSERT INTO t VALUES (1);", "INSERT OR FAIL INTO t VALUES (1);", 19, 2067);
        yield return Case("or-rollback-primary-key", "CREATE TABLE t(id INTEGER PRIMARY KEY); INSERT INTO t VALUES (1);", "BEGIN; INSERT OR ROLLBACK INTO t VALUES (1);", 19, 1555);
        yield return Case(
            "upsert-update-unique",
            "CREATE TABLE t(id INTEGER PRIMARY KEY, a UNIQUE); INSERT INTO t VALUES (1, 1), (2, 2);",
            "INSERT INTO t VALUES (1, 5) ON CONFLICT(id) DO UPDATE SET a = 2;",
            19,
            2067);
        yield return Case("composite-primary-key", "CREATE TABLE t(a, b, PRIMARY KEY (a, b)); INSERT INTO t VALUES (1, 2);", "INSERT INTO t VALUES (1, 2);", 19, 1555);
        yield return Case("strict-datatype", "CREATE TABLE t(a INTEGER) STRICT;", "INSERT INTO t VALUES ('abc');", 19, 3091);
        yield return Case("rowid-mismatch", "CREATE TABLE t(id INTEGER PRIMARY KEY);", "INSERT INTO t VALUES ('abc');", 20, 20);
        yield return Case("syntax-error", string.Empty, "SELEC 1;", 1, 1);
        yield return Case("no-such-table", string.Empty, "SELECT * FROM missing;", 1, 1);
        yield return Case("no-such-column", "CREATE TABLE t(a);", "SELECT b FROM t;", 1, 1);
        yield return Case("table-exists", "CREATE TABLE t(a);", "CREATE TABLE t(a);", 1, 1);
    }

    [TestCaseSource(nameof(StatementFailures))]
    public void StatementFailureCodesMatchSqlite(string setup, string failing, int expectedPrimary, int expectedExtended)
    {
        var native = CaptureNative(setup, failing);
        var managed = CaptureManaged(setup, failing);

        native.Should().Be((expectedPrimary, expectedExtended), "the case must pin SQLite's own result codes");
        managed.Should().Be(native, "the managed facade must report SQLite's primary and extended result codes");
    }

    [TestCaseSource(nameof(StatementFailures))]
    public void FileBackedStatementFailureCodesMatchSqlite(string setup, string failing, int expectedPrimary, int expectedExtended)
    {
        var nativePath = CreateEmptyPath();
        var managedPath = CreateEmptyPath();
        try
        {
            (int, int) native;
            using (var connection = new NativeSqlite.SqliteConnection($"Data Source={nativePath};Pooling=False"))
            {
                connection.Open();
                if (setup.Length != 0)
                    ExecuteNative(connection, setup);
                native = CaptureNative(connection, failing);
            }

            (int, int) managed;
            using (var connection = new ManagedSqlite.SqliteConnection($"Data Source={managedPath};Pooling=False"))
            {
                connection.Open();
                if (setup.Length != 0)
                    ExecuteManaged(connection, setup);
                managed = CaptureManaged(connection, failing);
            }

            native.Should().Be((expectedPrimary, expectedExtended), "the case must pin SQLite's own result codes");
            managed.Should().Be(native, "the page-backed managed engine must report SQLite's result codes");
        }
        finally
        {
            DeleteDatabase(nativePath);
            DeleteDatabase(managedPath);
        }
    }

    [TestCase("CREATE TABLE t(a UNIQUE); INSERT INTO t VALUES (1);", "INSERT INTO t VALUES (1);", 19, 2067)]
    [TestCase("CREATE TABLE t(id INTEGER PRIMARY KEY); INSERT INTO t VALUES (1);", "INSERT INTO t VALUES (1);", 19, 1555)]
    [TestCase("CREATE TABLE t(a NOT NULL);", "INSERT INTO t VALUES (NULL);", 19, 1299)]
    [TestCase("CREATE TABLE t(a CHECK (a > 0));", "INSERT INTO t VALUES (0);", 19, 275)]
    public void MvccStatementFailureCodesMatchSqlite(string setup, string failing, int expectedPrimary, int expectedExtended)
    {
        var path = CreateEmptyPath();
        try
        {
            using var connection = new ManagedSqlite.SqliteConnection($"Data Source={path};Pooling=False");
            connection.Open();
            ExecuteManaged(connection, "PRAGMA journal_mode = 'mvcc';");
            ExecuteManaged(connection, setup);

            CaptureManaged(connection, failing).Should().Be((expectedPrimary, expectedExtended));
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void UniqueViolationMessageMatchesSqlite()
    {
        const string setup = "CREATE TABLE t(a UNIQUE); INSERT INTO t VALUES (1);";
        const string failing = "INSERT INTO t VALUES (1);";

        var nativeMessage = CaptureNativeMessage(setup, failing);
        var managedMessage = CaptureManagedMessage(setup, failing);

        managedMessage.Should().Be(nativeMessage);
    }

    [Test]
    public void ReadOnlyWriteCodesMatchSqlite()
    {
        var path = CreateSeededFile();
        try
        {
            var native = CaptureNativeFile($"Data Source={path};Mode=ReadOnly;Pooling=False", "INSERT INTO t VALUES (2);");
            var managed = CaptureManagedFile($"Data Source={path};Mode=ReadOnly;Pooling=False", "INSERT INTO t VALUES (2);");

            native.Should().Be((8, 8));
            managed.Should().Be(native);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void BusyWriterCodesMatchSqlite()
    {
        var nativePath = CreateSeededFile();
        var managedPath = CreateSeededFile();
        try
        {
            (int, int) native;
            using (var holder = new NativeSqlite.SqliteConnection($"Data Source={nativePath};Pooling=False;Default Timeout=1"))
            using (var contender = new NativeSqlite.SqliteConnection($"Data Source={nativePath};Pooling=False;Default Timeout=1"))
            {
                holder.Open();
                contender.Open();
                ExecuteNative(holder, "BEGIN IMMEDIATE; INSERT INTO t VALUES (2);");
                native = CaptureNative(contender, "BEGIN IMMEDIATE;");
                ExecuteNative(holder, "ROLLBACK;");
            }

            (int, int) managed;
            using (var holder = new ManagedSqlite.SqliteConnection($"Data Source={managedPath};Pooling=False;Default Timeout=1"))
            using (var contender = new ManagedSqlite.SqliteConnection($"Data Source={managedPath};Pooling=False;Default Timeout=1"))
            {
                holder.Open();
                contender.Open();
                ExecuteManaged(holder, "BEGIN IMMEDIATE; INSERT INTO t VALUES (2);");
                managed = CaptureManaged(contender, "BEGIN IMMEDIATE;");
                ExecuteManaged(holder, "ROLLBACK;");
            }

            native.Should().Be((5, 5));
            managed.Should().Be(native);
        }
        finally
        {
            DeleteDatabase(nativePath);
            DeleteDatabase(managedPath);
        }
    }

    [Test]
    public void GarbageHeaderReportsFileIsNotADatabase()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ahtola-notadb-{Guid.NewGuid():N}.db");
        File.WriteAllBytes(path, Enumerable.Range(0, 4096).Select(i => (byte)(i * 7 + 3)).ToArray());
        try
        {
            var native = CaptureNativeFileWithMessage($"Data Source={path};Pooling=False", "SELECT * FROM sqlite_master;");
            var managed = CaptureManagedFileWithMessage($"Data Source={path};Pooling=False", "SELECT * FROM sqlite_master;");

            native.Codes.Should().Be((26, 26));
            managed.Codes.Should().Be(native.Codes);
            managed.Message.Should().Be(native.Message);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [TestCase("SQLITE_CONSTRAINT_UNIQUE", "UNIQUE constraint failed: t.a", 19, 2067)]
    [TestCase("SQLITE_CONSTRAINT_PRIMARYKEY", "UNIQUE constraint failed: t.id", 19, 1555)]
    [TestCase("SQLITE_CONSTRAINT_NOTNULL", "NOT NULL constraint failed: t.a", 19, 1299)]
    [TestCase("SQLITE_CONSTRAINT_FOREIGNKEY", "FOREIGN KEY constraint failed", 19, 787)]
    [TestCase("SQLITE_CONSTRAINT", "CHECK constraint failed: positive", 19, 275)]
    [TestCase("SQLITE_CONSTRAINT", "constraint failed", 19, 19)]
    [TestCase("SQLITE_BUSY", "database is locked", 5, 5)]
    [TestCase("SQLITE_READONLY", "attempt to write a readonly database", 8, 8)]
    [TestCase("SQLITE_IOERR_SHORT_READ", "disk I/O error", 10, 10)]
    [TestCase("SQLITE_NOTADB", "file is not a database", 26, 26)]
    [TestCase("SQL_PARSE_ERROR", "near \"SELEC\": syntax error", 1, 1)]
    public void RemoteHranaErrorCodesMapToSqliteResultCodes(string remoteCode, string remoteMessage, int primary, int extended)
    {
        var remote = new AhtolaRemoteSqlException(
            $"Remote SQL execution failed: {remoteMessage} ({remoteCode})",
            remoteCode,
            remoteMessage);

        var mapped = ManagedSqlite.SqliteCommand.ToSqliteException(remote);

        (mapped.SqliteErrorCode, mapped.SqliteExtendedErrorCode).Should().Be((primary, extended));
        mapped.Message.Should().StartWith($"SQLite Error {primary}: ");

        var facade = ManagedSqlite.SqliteRemoteExceptionClassifier.From(remote, mapped);
        (facade.SqliteErrorCode, facade.SqliteExtendedErrorCode).Should().Be((primary, extended));
    }

    private static TestCaseData Case(string name, string setup, string failing, int primary, int extended)
        => new TestCaseData(setup, failing, primary, extended).SetArgDisplayNames(name);

    private static string CreateEmptyPath()
        => Path.Combine(Path.GetTempPath(), $"ahtola-errcode-{Guid.NewGuid():N}.db");

    private static (int Primary, int Extended) CaptureNative(string setup, string failing)
    {
        using var connection = new NativeSqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();
        if (setup.Length != 0)
            ExecuteNative(connection, setup);
        return CaptureNative(connection, failing);
    }

    private static (int Primary, int Extended) CaptureNative(NativeSqlite.SqliteConnection connection, string failing)
    {
        try
        {
            ExecuteNative(connection, failing);
        }
        catch (NativeSqlite.SqliteException exception)
        {
            return (exception.SqliteErrorCode, exception.SqliteExtendedErrorCode);
        }

        throw new AssertionException($"SQLite accepted '{failing}'.");
    }

    private static string CaptureNativeMessage(string setup, string failing)
    {
        using var connection = new NativeSqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();
        ExecuteNative(connection, setup);
        try
        {
            ExecuteNative(connection, failing);
        }
        catch (NativeSqlite.SqliteException exception)
        {
            return exception.Message;
        }

        throw new AssertionException($"SQLite accepted '{failing}'.");
    }

    private static (int Primary, int Extended) CaptureManaged(string setup, string failing)
    {
        using var connection = new ManagedSqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();
        if (setup.Length != 0)
            ExecuteManaged(connection, setup);
        return CaptureManaged(connection, failing);
    }

    private static (int Primary, int Extended) CaptureManaged(ManagedSqlite.SqliteConnection connection, string failing)
    {
        try
        {
            ExecuteManaged(connection, failing);
        }
        catch (ManagedSqlite.SqliteException exception)
        {
            return (exception.SqliteErrorCode, exception.SqliteExtendedErrorCode);
        }

        throw new AssertionException($"The managed engine accepted '{failing}'.");
    }

    private static string CaptureManagedMessage(string setup, string failing)
    {
        using var connection = new ManagedSqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();
        ExecuteManaged(connection, setup);
        try
        {
            ExecuteManaged(connection, failing);
        }
        catch (ManagedSqlite.SqliteException exception)
        {
            return exception.Message;
        }

        throw new AssertionException($"The managed engine accepted '{failing}'.");
    }

    private static (int Primary, int Extended) CaptureNativeFile(string connectionString, string failing)
        => CaptureNativeFileWithMessage(connectionString, failing).Codes;

    private static ((int Primary, int Extended) Codes, string Message) CaptureNativeFileWithMessage(string connectionString, string failing)
    {
        try
        {
            using var connection = new NativeSqlite.SqliteConnection(connectionString);
            connection.Open();
            ExecuteNative(connection, failing);
        }
        catch (NativeSqlite.SqliteException exception)
        {
            return ((exception.SqliteErrorCode, exception.SqliteExtendedErrorCode), exception.Message);
        }

        throw new AssertionException($"SQLite accepted '{failing}'.");
    }

    private static (int Primary, int Extended) CaptureManagedFile(string connectionString, string failing)
        => CaptureManagedFileWithMessage(connectionString, failing).Codes;

    private static ((int Primary, int Extended) Codes, string Message) CaptureManagedFileWithMessage(string connectionString, string failing)
    {
        try
        {
            using var connection = new ManagedSqlite.SqliteConnection(connectionString);
            connection.Open();
            ExecuteManaged(connection, failing);
        }
        catch (ManagedSqlite.SqliteException exception)
        {
            return ((exception.SqliteErrorCode, exception.SqliteExtendedErrorCode), exception.Message);
        }

        throw new AssertionException($"The managed engine accepted '{failing}'.");
    }

    private static void ExecuteNative(NativeSqlite.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void ExecuteManaged(ManagedSqlite.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string CreateSeededFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ahtola-errcode-{Guid.NewGuid():N}.db");
        using (var connection = new NativeSqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            ExecuteNative(connection, "CREATE TABLE t(a); INSERT INTO t VALUES (1);");
        }

        return path;
    }

    private static void DeleteDatabase(string path)
    {
        ManagedSqlite.SqliteConnection.ClearAllPools();
        NativeSqlite.SqliteConnection.ClearAllPools();
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
        {
            try
            {
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
            catch (IOException)
            {
            }
        }
    }
}
