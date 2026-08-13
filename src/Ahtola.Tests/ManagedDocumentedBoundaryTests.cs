using System.Data;
using System.Reflection;
using AwesomeAssertions;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// Guards the "Managed engine scope" boundaries published in
/// <c>README.md</c>. If one of these surfaces becomes supported,
/// this suite fails so the documentation is corrected in the same change rather
/// than drifting into an overstated compatibility claim.
/// </summary>
public sealed class ManagedDocumentedBoundaryTests
{
    // The remaining pragma cluster (synchronous, locking_mode, busy_timeout,
    // wal_checkpoint, wal_autocheckpoint, auto_vacuum, max_page_count, temp_store,
    // mmap_size) is now accepted: busy_timeout / wal_checkpoint / max_page_count /
    // temp_store, synchronous, locking_mode, auto_vacuum, function_list, and
    // module_list are executed; the remainder follow SQLite's silent no-op behavior.

    private static readonly string[] UnsupportedStatements =
    [
        "SELECT * FROM fts5vocab('t', 'row')",
        "CREATE VIRTUAL TABLE vt USING fts5(x)",
    ];

    [Test]
    public void BeginConcurrentRequiresMvccAndSucceedsWhenEnabled()
    {
        using var connection = Open();
        Assert.Throws<SqliteException>(() => Execute(connection, "BEGIN CONCURRENT"));

        Execute(connection, "PRAGMA journal_mode=mvcc;");
        Execute(connection, "BEGIN CONCURRENT");
        Execute(connection, "COMMIT");
    }

    /// <summary>
    /// P5-D: auto_vacuum / incremental_vacuum stay silent no-ops (Turso v0.7.2 also rejects
    /// Incremental auto-vacuum). Must not throw and must not claim ptrmap reclaim.
    /// </summary>
    [Test]
    [TestCase("PRAGMA auto_vacuum")]
    [TestCase("PRAGMA auto_vacuum=NONE")]
    [TestCase("PRAGMA auto_vacuum=FULL")]
    [TestCase("PRAGMA auto_vacuum=INCREMENTAL")]
    [TestCase("PRAGMA incremental_vacuum")]
    [TestCase("PRAGMA incremental_vacuum(10)")]
    public void AutoVacuumFamilyIsAcceptedNoOp(string sql)
    {
        using var connection = Open();
        Execute(connection, "INSERT INTO t VALUES (1, 'a');");
        Execute(connection, sql);
        ExecuteScalarLong(connection, "SELECT COUNT(*) FROM t;").Should().Be(1L);
    }

    /// <summary>
    /// P5-C: cache_spill round-trips as a surface flag; managed cache is clean-page only so
    /// there is no dirty-page spill counterpart (inventory storage-no-page-cache-spill).
    /// </summary>
    [Test]
    public void CacheSpillPragmaRoundTripsWithoutError()
    {
        using var connection = Open();
        Execute(connection, "PRAGMA cache_spill=OFF;");
        Execute(connection, "PRAGMA cache_spill=ON;");
        Execute(connection, "PRAGMA cache_size=-2000;");
        Execute(connection, "INSERT INTO t VALUES (42, 'x');");
        ExecuteScalarLong(connection, "SELECT a FROM t;").Should().Be(42L);
    }

    [Test]
    [TestCaseSource(nameof(UnsupportedStatements))]
    public void ADocumentedUnsupportedStatementIsRejected(string sql)
    {
        using var connection = Open();
        Assert.Throws<SqliteException>(() => Execute(connection, sql));
    }

    [Test]
    public void RawHandleInteropRemainsUnavailable()
    {
        using var connection = Open();
        object? handle = connection.Handle;
        handle.Should().BeNull();
    }

    [Test]
    public void ServerVersionMirrorsSqliteVersion()
    {
        using var connection = Open();
        connection.ServerVersion.Should().Be("3.50.4");
    }

    [Test]
    [TestCase("CreateModule")]
    public void NoModuleSurfaceIsPublished(string fragment)
    {
        typeof(SqliteConnection)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Select(member => member.Name)
            .Should()
            .NotContain(name => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Update, commit and rollback hooks, the authorizer, tracing and the progress handler moved
    /// out of the "Not implemented" list in <c>README.md</c>, so this asserts the
    /// documented direction of that scope change: the surface must stay published. Behavior lives
    /// in <c>ManagedHookAndAuthorizerTests</c> and <c>ManagedHookSqliteDifferentialTests</c>.
    /// </summary>
    [Test]
    [TestCase("SetUpdateHook")]
    [TestCase("SetCommitHook")]
    [TestCase("SetRollbackHook")]
    [TestCase("SetAuthorizer")]
    [TestCase("SetTraceHandler")]
    [TestCase("SetProgressHandler")]
    public void TheDocumentedHookSurfaceIsPublished(string member)
    {
        typeof(SqliteConnection)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Select(candidate => candidate.Name)
            .Should()
            .Contain(member);
    }

    [Test]
    public void OnlyTheDocumentedSchemaCollectionsAreDefined()
    {
        using var connection = Open();

        connection.GetSchema("MetaDataCollections").Rows.Cast<DataRow>()
            .Select(row => (string)row["CollectionName"])
            .Should().Equal(
                "MetaDataCollections",
                "ReservedWords",
                "Tables",
                "Columns",
                "Indexes",
                "IndexColumns");

        foreach (var undefined in new[] { "DataSourceInformation", "DataTypes", "Restrictions", "ForeignKeys", "Views" })
        {
            Assert.Throws<ArgumentException>(() => connection.GetSchema(undefined))!
                .Message.Should().Be($"Unknown collection: {undefined}.");
        }
    }

    [Test]
    public void TheCommandBuilderRefusesSelectsItCannotRoundTrip()
    {
        using var connection = Open();

        // Documented limit: single-table selects exposing a key column only. A join and a keyless
        // table are the two shapes callers hit first, so both have to fail loudly at command
        // generation rather than silently producing a statement that updates nothing.
        using var join = new AhtolaDataAdapter("SELECT t.a, s.b FROM t JOIN s ON t.a = s.a", connection);
        using var joinBuilder = new AhtolaCommandBuilder(join);
        Assert.Throws<InvalidOperationException>(() => joinBuilder.GetUpdateCommand());

        using var keyless = new AhtolaDataAdapter("SELECT a, b FROM t", connection);
        using var keylessBuilder = new AhtolaCommandBuilder(keyless);
        Assert.Throws<InvalidOperationException>(() => keylessBuilder.GetUpdateCommand());
    }

    [Test]
    public void TheAdapterDoesNotBatchRowUpdates()
    {
        using var adapter = new AhtolaDataAdapter();

        adapter.UpdateBatchSize.Should().Be(1);
        Assert.Throws<NotSupportedException>(() => adapter.UpdateBatchSize = 10);
    }

    // File-backed trigger definitions may reference the engine's built-in functions: the
    // stored CREATE SQL re-resolves to the same implementations after reopen, so the body
    // keeps working. (Application-registered callbacks remain rejected; see
    // ManagedSchemaDefinitionRecoverySafetyTests.)
    [Test]
    public void FileBackedTriggerWithBuiltinFunctionPersistsAcrossReopen()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var connection = OpenFile(path))
            {
                ExecuteNonQuery(connection, "CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT)");
                ExecuteNonQuery(connection, "CREATE TABLE audit (id INTEGER PRIMARY KEY, upper_name TEXT)");
                ExecuteNonQuery(connection, "CREATE TRIGGER trg AFTER INSERT ON t FOR EACH ROW BEGIN INSERT INTO audit (upper_name) VALUES (UPPER(NEW.name)); END");
                ExecuteNonQuery(connection, "INSERT INTO t (name) VALUES ('before')");
            }

            using (var reopened = OpenFile(path))
            {
                ExecuteNonQuery(reopened, "INSERT INTO t (name) VALUES ('after')");
                using var command = reopened.CreateCommand();
                command.CommandText = "SELECT upper_name FROM audit ORDER BY id";
                using var reader = command.ExecuteReader();
                var values = new List<string>();
                while (reader.Read())
                    values.Add(reader.GetString(0));
                values.Should().Equal("BEFORE", "AFTER");
            }
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    // Readme.md "Known divergences from SQLite" documents that a double-quoted token in a
    // value context is resolved strictly as a column identifier: an unresolved name throws
    // `no such column` rather than falling back to a string literal the way stock SQLite
    // (SQLITE_DQS, the default, including e_sqlite3) does. Single-quoted literals are the
    // portable form. This pins the strict behavior so a future DQS-style fallback cannot
    // silently slip in without updating the documentation.
    [Test]
    public void DoubleQuotedTokenInValueContextIsResolvedAsColumnNotStringLiteral()
    {
        using var connection = Open();
        Execute(connection, "INSERT INTO t VALUES (1, 'characters')");

        // (1) A double-quoted real column name resolves to the column value (strict
        //     identifier), not to the literal token. A DQS string-literal fallback would
        //     return the text "a" here instead of the stored integer 1.
        ReadOne(connection, "SELECT \"a\" FROM t").Should().Be("1");

        // (2) A double-quoted token that is NOT a column throws `no such column`, matching
        //     strict identifier resolution. Stock SQLite with SQLITE_DQS (the default,
        //     including e_sqlite3) would fall back to the string literal 'characters' and
        //     return a row. This pins the documented divergence.
        var error = Assert.Throws<SqliteException>(() =>
            ReadOne(connection, "SELECT \"characters\" FROM t"));

        error!.Message.Should().Contain("no such column: characters");
    }

    private static string? ReadOne(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return "<NO ROWS>";
        return reader.IsDBNull(0) ? "<NULL>" : reader.GetValue(0)?.ToString();
    }

    private static SqliteConnection Open()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT)");
        Execute(connection, "CREATE TABLE s(a INTEGER, b TEXT)");
        return connection;
    }

    private static SqliteConnection OpenFile(string path)
    {
        // Pooling=False hands the file lock back on dispose so the test can delete it; the
        // explicit Local Provider=Managed pins the managed file store whose reopen
        // constraint is the boundary under test.
        var connection = new SqliteConnection($"Data Source={path};Pooling=False;Local Provider=Managed");
        connection.Open();
        return connection;
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "managed-boundary");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"file-trigger-{Guid.NewGuid():N}.db");
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

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
        }
    }

    private static long ExecuteScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return Convert.ToInt64(value);
    }
}
