using System.Data;
using System.Data.Common;
using System.Globalization;
using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

// P0 correctness validate-first cluster (from the Rust-vs-C# gap analysis). The VDBE
// vertical flagged these four because it could not see whether the higher EmbeddedDatabase
// DML/DDL layer already covers them. Each test runs the violating statement against the
// managed engine AND a real Microsoft.Data.Sqlite oracle and asserts both reject with the
// same SQLite constraint semantics (or, for arithmetic affinity, both coerce text->numeric).
// If a test passes, the higher layer covers it and the item is closed. If it fails, the
// failure surfaces a real P0 defect to fix at the narrowest layer.
[NonParallelizable]
public sealed class ParityCorrectnessValidationTests
{
    // P0-D - Foreign-key enforcement. PRAGMA foreign_keys=ON must reject an orphan child.
    // Highest data-integrity risk if missing: silent referential-integrity loss.
    [Test]
    public void ParityP0D_ForeignKeyEnforcementRejectsOrphanChild()
    {
        var managedPath = CreateDatabasePath("p0d-fk-managed");
        var sqlitePath = CreateDatabasePath("p0d-fk-sqlite");
        try
        {
            using var managed = OpenManaged(managedPath);
            using var sqlite = OpenSqlite(sqlitePath);

            // foreign_keys must be set outside a transaction, in its own autocommit call.
            ExecuteNonQuery(managed, "PRAGMA foreign_keys = ON;");
            ExecuteNonQuery(sqlite, "PRAGMA foreign_keys = ON;");
            ExecuteNonQuery(managed, "CREATE TABLE parent (id INTEGER PRIMARY KEY);");
            ExecuteNonQuery(sqlite, "CREATE TABLE parent (id INTEGER PRIMARY KEY);");
            ExecuteNonQuery(managed, "CREATE TABLE child (id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id));");
            ExecuteNonQuery(sqlite, "CREATE TABLE child (id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id));");

            // Valid parent+child inserts succeed on both.
            ExecuteNonQuery(managed, "INSERT INTO parent (id) VALUES (1);");
            ExecuteNonQuery(sqlite, "INSERT INTO parent (id) VALUES (1);");
            ExecuteNonQuery(managed, "INSERT INTO child (parent_id) VALUES (1);");
            ExecuteNonQuery(sqlite, "INSERT INTO child (parent_id) VALUES (1);");

            // Orphan child must be rejected on both with the same FOREIGN KEY constraint.
            AssertThrowsConstraint(managed, "INSERT INTO child (parent_id) VALUES (999);", "FOREIGN KEY constraint failed");
            AssertThrowsConstraint(sqlite, "INSERT INTO child (parent_id) VALUES (999);", "FOREIGN KEY constraint failed");
        }
        finally
        {
            DeleteDatabase(managedPath);
            DeleteDatabase(sqlitePath);
        }
    }

    // P0-C - NOT NULL enforcement. INSERT of a NULL into a NOT NULL column must error.
    // Silent violations would be data corruption.
    [Test]
    public void ParityP0C_NotNullEnforcementRejectsNullInsert()
    {
        var managedPath = CreateDatabasePath("p0c-notnull-managed");
        var sqlitePath = CreateDatabasePath("p0c-notnull-sqlite");
        try
        {
            using var managed = OpenManaged(managedPath);
            using var sqlite = OpenSqlite(sqlitePath);

            ExecuteNonQuery(managed, "CREATE TABLE t (id INTEGER PRIMARY KEY, x TEXT NOT NULL);");
            ExecuteNonQuery(sqlite, "CREATE TABLE t (id INTEGER PRIMARY KEY, x TEXT NOT NULL);");

            // Valid insert succeeds on both.
            ExecuteNonQuery(managed, "INSERT INTO t (x) VALUES ('ok');");
            ExecuteNonQuery(sqlite, "INSERT INTO t (x) VALUES ('ok');");

            // NULL insert must be rejected on both.
            AssertThrowsConstraint(managed, "INSERT INTO t (x) VALUES (NULL);", "NOT NULL constraint failed");
            AssertThrowsConstraint(sqlite, "INSERT INTO t (x) VALUES (NULL);", "NOT NULL constraint failed");
        }
        finally
        {
            DeleteDatabase(managedPath);
            DeleteDatabase(sqlitePath);
        }
    }

    // P0-B - INSERT OR IGNORE / OR REPLACE conflict handling + auto-rowid allocation.
    [Test]
    public void ParityP0B_InsertOrIgnoreReplaceAndAutoRowidMatchSqlite()
    {
        var managedPath = CreateDatabasePath("p0b-conflict-managed");
        var sqlitePath = CreateDatabasePath("p0b-conflict-sqlite");
        try
        {
            using var managed = OpenManaged(managedPath);
            using var sqlite = OpenSqlite(sqlitePath);

            const string create = "CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE);";
            ExecuteNonQuery(managed, create);
            ExecuteNonQuery(sqlite, create);

            // Seed a row.
            ExecuteNonQuery(managed, "INSERT INTO t (id, name) VALUES (1, 'a');");
            ExecuteNonQuery(sqlite, "INSERT INTO t (id, name) VALUES (1, 'a');");

            // INSERT OR IGNORE on a duplicate unique name -> ignored, row unchanged.
            ExecuteNonQuery(managed, "INSERT OR IGNORE INTO t (id, name) VALUES (1, 'ignored');");
            ExecuteNonQuery(sqlite, "INSERT OR IGNORE INTO t (id, name) VALUES (1, 'ignored');");
            ReadRows(managed).Should().Equal(ReadRows(sqlite));

            // INSERT OR REPLACE on the duplicate PK -> old row deleted, new row inserted.
            ExecuteNonQuery(managed, "INSERT OR REPLACE INTO t (id, name) VALUES (1, 'replaced');");
            ExecuteNonQuery(sqlite, "INSERT OR REPLACE INTO t (id, name) VALUES (1, 'replaced');");
            ReadRows(managed).Should().Equal(ReadRows(sqlite));

            // Auto-rowid: INSERT with NULL id on INTEGER PRIMARY KEY assigns the next rowid.
            ExecuteNonQuery(managed, "INSERT INTO t (id, name) VALUES (NULL, 'b');");
            ExecuteNonQuery(sqlite, "INSERT INTO t (id, name) VALUES (NULL, 'b');");
            ReadRows(managed).Should().Equal(ReadRows(sqlite));
        }
        finally
        {
            DeleteDatabase(managedPath);
            DeleteDatabase(sqlitePath);
        }
    }

    // P0-A - Arithmetic affinity coercion. text+int must coerce text->numeric, not throw.
    // The VDBE ArithmeticInstruction path explicitly does NOT apply affinity; this validates
    // the emitter routes text/blob operands through affinity (or the evaluator fallback) first.
    [Test]
    public void ParityP0A_ArithmeticAffinityCoercesTextToNumeric()
    {
        var managedPath = CreateDatabasePath("p0a-affinity-managed");
        var sqlitePath = CreateDatabasePath("p0a-affinity-sqlite");
        try
        {
            using var managed = OpenManaged(managedPath);
            using var sqlite = OpenSqlite(sqlitePath);

            // '5' + 3 -> SQLite coerces the text '5' to integer 5 and yields 8.
            ReadScalar(managed, "SELECT '5' + 3;").Should().Be(ReadScalar(sqlite, "SELECT '5' + 3;"));
            ReadScalar(managed, "SELECT '5' + 3;").Should().Be(8L);

            // '5' + '3' -> both text operands coerce; SQLite yields 8.
            ReadScalar(managed, "SELECT '5' + '3';").Should().Be(ReadScalar(sqlite, "SELECT '5' + '3';"));
            ReadScalar(managed, "SELECT '5' + '3';").Should().Be(8L);

            // Non-numeric text coerces to 0 under SQLite numeric affinity, so 'abc' + 3 -> 3.
            ReadScalar(managed, "SELECT 'abc' + 3;").Should().Be(ReadScalar(sqlite, "SELECT 'abc' + 3;"));
            ReadScalar(managed, "SELECT 'abc' + 3;").Should().Be(3L);
        }
        finally
        {
            DeleteDatabase(managedPath);
            DeleteDatabase(sqlitePath);
        }
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

    private static void ExecuteNonQuery(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long ReadScalar(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue(because: $"scalar query '{sql}' should return a row");
        return Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
    }

    private static List<(long Id, string Name)> ReadRows(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name FROM t ORDER BY id;";
        var rows = new List<(long, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetInt64(0), reader.GetString(1)));
        return rows;
    }

    private static void AssertThrowsConstraint(DbConnection connection, string sql, string expectedMessageSubstring)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        Exception? caught = null;
        try
        {
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        caught.Should().NotBeNull(because: $"expected '{sql}' to be rejected, but it succeeded");
        caught!.Message.Should().Contain(
            expectedMessageSubstring,
            because: $"both engines must report the same SQLite constraint; engine threw: {caught.Message}");
    }

    private static string CreateDatabasePath(string suffix)
    {
        var directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "parity-correctness-validation");
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
