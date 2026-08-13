using System.Data;
using System.Data.Common;
using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

// P1-22 / P1-23 validate-first cluster (from the Rust-vs-C# gap analysis). The VDBE
// vertical flagged triggers (P1-22) and ALTER/DROP DDL (P1-23) as missing opcodes, but
// noted they may be covered at the higher EmbeddedDatabase DML/DDL layer (mirroring the
// P0 cluster). Each test exercises the common, contract-clean shapes both engines must
// support and asserts the managed engine matches a real Microsoft.Data.Sqlite oracle.
// If a test passes, the higher layer covers the item and it is closed (downgrade to P3).
// If it fails, the failure surfaces a real gap to fix at the narrowest layer. Known
// deliberate limitations (INSTEAD OF triggers with LIMIT/RETURNING; ALTER TABLE RENAME
// rewriting table-qualified CHECK expressions) are intentionally out of scope.
[NonParallelizable]
public sealed class ParityHigherLayerValidationTests
{
    // P1-22 - AFTER INSERT/UPDATE/DELETE row triggers fire with NEW/OLD correlation names.
    [Test]
    public void ParityP1_22_AfterInsertUpdateDeleteTriggersMatchSqlite()
    {
        var managedPath = CreateDatabasePath("p1_22-triggers-managed");
        var sqlitePath = CreateDatabasePath("p1_22-triggers-sqlite");
        try
        {
            using var managed = OpenManaged(managedPath);
            using var sqlite = OpenSqlite(sqlitePath);

            const string createAccounts = "CREATE TABLE accounts (id INTEGER PRIMARY KEY, balance INTEGER NOT NULL DEFAULT 0);";
            const string createAudit = "CREATE TABLE audit (id INTEGER PRIMARY KEY, action TEXT, amount INTEGER);";
            ExecuteNonQuery(managed, createAccounts);
            ExecuteNonQuery(sqlite, createAccounts);
            ExecuteNonQuery(managed, createAudit);
            ExecuteNonQuery(sqlite, createAudit);

            const string afterInsert = "CREATE TRIGGER trg_after_insert AFTER INSERT ON accounts FOR EACH ROW BEGIN INSERT INTO audit (action, amount) VALUES ('insert', NEW.balance); END;";
            const string afterUpdate = "CREATE TRIGGER trg_after_update AFTER UPDATE ON accounts FOR EACH ROW BEGIN INSERT INTO audit (action, amount) VALUES ('update', NEW.balance); END;";
            const string afterDelete = "CREATE TRIGGER trg_after_delete AFTER DELETE ON accounts FOR EACH ROW BEGIN INSERT INTO audit (action, amount) VALUES ('delete', OLD.balance); END;";
            foreach (var sql in new[] { afterInsert, afterUpdate, afterDelete })
            {
                ExecuteNonQuery(managed, sql);
                ExecuteNonQuery(sqlite, sql);
            }

            ExecuteNonQuery(managed, "INSERT INTO accounts (balance) VALUES (100);");
            ExecuteNonQuery(sqlite, "INSERT INTO accounts (balance) VALUES (100);");
            ExecuteNonQuery(managed, "INSERT INTO accounts (balance) VALUES (50);");
            ExecuteNonQuery(sqlite, "INSERT INTO accounts (balance) VALUES (50);");
            ExecuteNonQuery(managed, "UPDATE accounts SET balance = balance + 25 WHERE id = 1;");
            ExecuteNonQuery(sqlite, "UPDATE accounts SET balance = balance + 25 WHERE id = 1;");
            ExecuteNonQuery(managed, "DELETE FROM accounts WHERE id = 2;");
            ExecuteNonQuery(sqlite, "DELETE FROM accounts WHERE id = 2;");

            ReadAll(managed, "SELECT action, amount FROM audit ORDER BY id;")
                .Should().BeEquivalentTo(ReadAll(sqlite, "SELECT action, amount FROM audit ORDER BY id;"), o => o.WithStrictOrdering());
            ReadAll(managed, "SELECT id, balance FROM accounts ORDER BY id;")
                .Should().BeEquivalentTo(ReadAll(sqlite, "SELECT id, balance FROM accounts ORDER BY id;"), o => o.WithStrictOrdering());
        }
        finally
        {
            DeleteDatabase(managedPath);
            DeleteDatabase(sqlitePath);
        }
    }

    // P1-22 - BEFORE UPDATE trigger observes OLD and NEW correlation names. Kept
    // function-free: file-backed trigger bodies that call a builtin scalar function
    // (e.g. UPPER) are deliberately rejected by the managed persistence layer (see the
    // separate P1-22 boundary finding in plan.md), so this cluster validates the
    // common function-free BEFORE trigger shape that both engines support.
    [Test]
    public void ParityP1_22_BeforeUpdateTriggerCapturesOldAndNewMatchSqlite()
    {
        var managedPath = CreateDatabasePath("p1_22-before-managed");
        var sqlitePath = CreateDatabasePath("p1_22-before-sqlite");
        try
        {
            using var managed = OpenManaged(managedPath);
            using var sqlite = OpenSqlite(sqlitePath);

            ExecuteNonQuery(managed, "CREATE TABLE t (id INTEGER PRIMARY KEY, v INTEGER);");
            ExecuteNonQuery(sqlite, "CREATE TABLE t (id INTEGER PRIMARY KEY, v INTEGER);");
            ExecuteNonQuery(managed, "CREATE TABLE audit (id INTEGER PRIMARY KEY, old_v INTEGER, new_v INTEGER);");
            ExecuteNonQuery(sqlite, "CREATE TABLE audit (id INTEGER PRIMARY KEY, old_v INTEGER, new_v INTEGER);");

            const string before = "CREATE TRIGGER trg_before_update BEFORE UPDATE ON t FOR EACH ROW BEGIN INSERT INTO audit (old_v, new_v) VALUES (OLD.v, NEW.v); END;";
            ExecuteNonQuery(managed, before);
            ExecuteNonQuery(sqlite, before);

            ExecuteNonQuery(managed, "INSERT INTO t (v) VALUES (10);");
            ExecuteNonQuery(sqlite, "INSERT INTO t (v) VALUES (10);");
            ExecuteNonQuery(managed, "INSERT INTO t (v) VALUES (20);");
            ExecuteNonQuery(sqlite, "INSERT INTO t (v) VALUES (20);");
            ExecuteNonQuery(managed, "UPDATE t SET v = v + 5;");
            ExecuteNonQuery(sqlite, "UPDATE t SET v = v + 5;");

            ReadAll(managed, "SELECT old_v, new_v FROM audit ORDER BY id;")
                .Should().BeEquivalentTo(ReadAll(sqlite, "SELECT old_v, new_v FROM audit ORDER BY id;"), o => o.WithStrictOrdering());
            ReadAll(managed, "SELECT id, v FROM t ORDER BY id;")
                .Should().BeEquivalentTo(ReadAll(sqlite, "SELECT id, v FROM t ORDER BY id;"), o => o.WithStrictOrdering());
        }
        finally
        {
            DeleteDatabase(managedPath);
            DeleteDatabase(sqlitePath);
        }
    }

    // P1-23 - ALTER TABLE ADD/RENAME/DROP COLUMN preserve data and default-fill new columns.
    [Test]
    public void ParityP1_23_AlterTableAddRenameDropColumnMatchSqlite()
    {
        var managedPath = CreateDatabasePath("p1_23-columns-managed");
        var sqlitePath = CreateDatabasePath("p1_23-columns-sqlite");
        try
        {
            using var managed = OpenManaged(managedPath);
            using var sqlite = OpenSqlite(sqlitePath);

            ExecuteNonQuery(managed, "CREATE TABLE t (id INTEGER PRIMARY KEY, a TEXT);");
            ExecuteNonQuery(sqlite, "CREATE TABLE t (id INTEGER PRIMARY KEY, a TEXT);");
            ExecuteNonQuery(managed, "INSERT INTO t (a) VALUES ('first');");
            ExecuteNonQuery(sqlite, "INSERT INTO t (a) VALUES ('first');");
            ExecuteNonQuery(managed, "INSERT INTO t (a) VALUES ('second');");
            ExecuteNonQuery(sqlite, "INSERT INTO t (a) VALUES ('second');");

            ExecuteNonQuery(managed, "ALTER TABLE t ADD COLUMN b TEXT DEFAULT 'x';");
            ExecuteNonQuery(sqlite, "ALTER TABLE t ADD COLUMN b TEXT DEFAULT 'x';");
            ExecuteNonQuery(managed, "INSERT INTO t (a) VALUES ('third');");
            ExecuteNonQuery(sqlite, "INSERT INTO t (a) VALUES ('third');");

            ColumnNames(managed, "t").Should().Equal(ColumnNames(sqlite, "t"));
            ReadAll(managed, "SELECT id, a, b FROM t ORDER BY id;")
                .Should().BeEquivalentTo(ReadAll(sqlite, "SELECT id, a, b FROM t ORDER BY id;"), o => o.WithStrictOrdering());

            ExecuteNonQuery(managed, "ALTER TABLE t RENAME COLUMN a TO alpha;");
            ExecuteNonQuery(sqlite, "ALTER TABLE t RENAME COLUMN a TO alpha;");
            ColumnNames(managed, "t").Should().Equal(ColumnNames(sqlite, "t"));
            ReadAll(managed, "SELECT id, alpha, b FROM t ORDER BY id;")
                .Should().BeEquivalentTo(ReadAll(sqlite, "SELECT id, alpha, b FROM t ORDER BY id;"), o => o.WithStrictOrdering());

            ExecuteNonQuery(managed, "ALTER TABLE t DROP COLUMN b;");
            ExecuteNonQuery(sqlite, "ALTER TABLE t DROP COLUMN b;");
            ColumnNames(managed, "t").Should().Equal(ColumnNames(sqlite, "t"));
            ReadAll(managed, "SELECT id, alpha FROM t ORDER BY id;")
                .Should().BeEquivalentTo(ReadAll(sqlite, "SELECT id, alpha FROM t ORDER BY id;"), o => o.WithStrictOrdering());
        }
        finally
        {
            DeleteDatabase(managedPath);
            DeleteDatabase(sqlitePath);
        }
    }

    // P1-23 - ALTER TABLE RENAME TO and DROP TABLE update the schema catalog.
    [Test]
    public void ParityP1_23_RenameTableAndDropTableMatchSqlite()
    {
        var managedPath = CreateDatabasePath("p1_23-table-managed");
        var sqlitePath = CreateDatabasePath("p1_23-table-sqlite");
        try
        {
            using var managed = OpenManaged(managedPath);
            using var sqlite = OpenSqlite(sqlitePath);

            ExecuteNonQuery(managed, "CREATE TABLE t (id INTEGER PRIMARY KEY, v INTEGER);");
            ExecuteNonQuery(sqlite, "CREATE TABLE t (id INTEGER PRIMARY KEY, v INTEGER);");
            ExecuteNonQuery(managed, "INSERT INTO t (v) VALUES (10);");
            ExecuteNonQuery(sqlite, "INSERT INTO t (v) VALUES (10);");
            ExecuteNonQuery(managed, "INSERT INTO t (v) VALUES (20);");
            ExecuteNonQuery(sqlite, "INSERT INTO t (v) VALUES (20);");

            ExecuteNonQuery(managed, "ALTER TABLE t RENAME TO t_renamed;");
            ExecuteNonQuery(sqlite, "ALTER TABLE t RENAME TO t_renamed;");
            UserTables(managed).Should().Equal(UserTables(sqlite));
            ReadAll(managed, "SELECT id, v FROM t_renamed ORDER BY id;")
                .Should().BeEquivalentTo(ReadAll(sqlite, "SELECT id, v FROM t_renamed ORDER BY id;"), o => o.WithStrictOrdering());

            ExecuteNonQuery(managed, "DROP TABLE t_renamed;");
            ExecuteNonQuery(sqlite, "DROP TABLE t_renamed;");
            UserTables(managed).Should().Equal(UserTables(sqlite));
            UserTables(managed).Should().BeEmpty();
        }
        finally
        {
            DeleteDatabase(managedPath);
            DeleteDatabase(sqlitePath);
        }
    }

    // P1-22 escape hatch - in-memory (non-file) trigger bodies may call builtin scalar
    // functions: the file-backed rejection (see ManagedDocumentedBoundaryTests.
    // FileBackedTriggerWithBuiltinFunctionIsRejected / Readme.md:695) is specific to
    // EmbeddedFileStore's reopen constraint; an in-memory database never reopens, so a
    // function-bearing trigger is allowed and matches MDS.
    [Test]
    public void ParityP1_22_InMemoryTriggerWithBuiltinFunctionMatchesSqlite()
    {
        using var managed = OpenManagedMemory();
        using var sqlite = OpenSqliteMemory();

        ExecuteNonQuery(managed, "CREATE TABLE t (id INTEGER PRIMARY KEY, b TEXT);");
        ExecuteNonQuery(sqlite, "CREATE TABLE t (id INTEGER PRIMARY KEY, b TEXT);");
        ExecuteNonQuery(managed, "CREATE TABLE audit (id INTEGER PRIMARY KEY, upper_name TEXT);");
        ExecuteNonQuery(sqlite, "CREATE TABLE audit (id INTEGER PRIMARY KEY, upper_name TEXT);");

        const string trigger = "CREATE TRIGGER trg AFTER INSERT ON t FOR EACH ROW BEGIN INSERT INTO audit (upper_name) VALUES (UPPER(NEW.b)); END;";
        ExecuteNonQuery(managed, trigger);
        ExecuteNonQuery(sqlite, trigger);

        ExecuteNonQuery(managed, "INSERT INTO t (b) VALUES ('bob');");
        ExecuteNonQuery(sqlite, "INSERT INTO t (b) VALUES ('bob');");

        ReadAll(managed, "SELECT upper_name FROM audit ORDER BY id;")
            .Should().BeEquivalentTo(ReadAll(sqlite, "SELECT upper_name FROM audit ORDER BY id;"), o => o.WithStrictOrdering());
    }

    private static SqliteConnection OpenManaged(string path)
    {
        // Pooling=False so disposing hands the file back to other engines cleanly; the
        // managed pool otherwise retains the SQLite lock-byte ownership by design.
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

    private static SqliteConnection OpenManagedMemory()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        return connection;
    }

    private static MsData.SqliteConnection OpenSqliteMemory()
    {
        var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private static void ExecuteNonQuery(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    // Reads every row of a result set as a normalized object?[] so managed and oracle
    // values compare regardless of whether the provider boxes an integer as int or long
    // (both normalize to long) or a real as float/double (both normalize to double).
    private static List<object?[]> ReadAll(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var values = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                values[i] = Normalize(reader.GetValue(i));
            rows.Add(values);
        }

        return rows;
    }

    private static object? Normalize(object? value)
    {
        if (value is null || value == DBNull.Value)
            return null;

        return value switch
        {
            long l => l,
            int i => (long)i,
            short s => (long)s,
            byte b => (long)b,
            ulong ul => (long)ul,
            uint ui => (long)ui,
            ushort us => (long)us,
            double d => d,
            float f => (double)f,
            decimal dec => (double)dec,
            string s => s,
            byte[] arr => arr,
            _ => value,
        };
    }

    private static List<string> ColumnNames(DbConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(1)); // PRAGMA table_info columns: cid, name, type, notnull, dflt_value, pk.
        return names;
    }

    private static List<string> UserTables(DbConnection connection)
    {
        return ReadAllStrings(connection, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;");
    }

    private static List<string> ReadAllStrings(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
        return rows;
    }

    private static string CreateDatabasePath(string suffix)
    {
        var directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "parity-higher-layer-validation");
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
