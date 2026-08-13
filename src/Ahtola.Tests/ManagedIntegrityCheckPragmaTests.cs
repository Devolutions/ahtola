using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// Differential coverage for <c>PRAGMA integrity_check</c> and
/// <c>PRAGMA quick_check</c>. Healthy shapes run against an in-memory managed
/// database and an in-memory SQLite database simultaneously. Corrupt shapes are
/// produced by real SQLite through <c>writable_schema</c>, then read back by
/// both engines from independent copies of the same file, because the managed
/// pager holds exclusive main-file ownership.
/// </summary>
public sealed class ManagedIntegrityCheckPragmaTests
{
    [Test]
    [TestCase("PRAGMA integrity_check")]
    [TestCase("PRAGMA quick_check")]
    [TestCase("PRAGMA integrity_check(10)")]
    [TestCase("PRAGMA quick_check(3)")]
    [TestCase("PRAGMA main.integrity_check")]
    [TestCase("PRAGMA integrity_check(t)")]
    [TestCase("PRAGMA integrity_check('t')")]
    [TestCase("PRAGMA main.quick_check(u)")]
    public void AHealthyDatabaseMatchesSqlite(string query)
    {
        string[] setup =
        [
            "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT NOT NULL, c INT CHECK(c > 0))",
            "CREATE UNIQUE INDEX ix_b ON t(b)",
            "CREATE TABLE u(v TEXT)",
            "INSERT INTO t VALUES(1,'x',5),(2,'y',6)",
            "INSERT INTO u VALUES('p'),(NULL)",
        ];

        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        foreach (var statement in setup)
        {
            Execute(managed, statement);
            Execute(sqlite, statement);
        }

        AssertColumnNamesMatch(managed, sqlite, query);
        AssertQueriesMatch(managed, sqlite, query);
    }

    [Test]
    public void RepeatedNullKeysDoNotViolateAUniqueIndex()
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        foreach (var statement in new[]
                 {
                     "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)",
                     "CREATE UNIQUE INDEX ix_b ON t(b)",
                     "INSERT INTO t VALUES(1,NULL),(2,NULL)",
                 })
        {
            Execute(managed, statement);
            Execute(sqlite, statement);
        }

        AssertQueriesMatch(managed, sqlite, "PRAGMA integrity_check");
    }

    [Test]
    public void AnAttachedDatabaseIsCheckedThroughItsOwnSchema()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Ahtola-integrity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var mainPath = Path.Combine(directory, "main.db");
            var attachedPath = Path.Combine(directory, "aux.db");
            var attach = "ATTACH DATABASE '"
                + attachedPath.Replace("'", "''", StringComparison.Ordinal)
                + "' AS aux";
            using var managedDatabase = EmbeddedDatabase.OpenFile(mainPath, PhysicalFileSystem.Instance);
            using var managed = managedDatabase.Connect();
            using var sqlite = OpenSqlite(Path.Combine(directory, "sqlite-main.db"));
            foreach (var statement in new[]
                     {
                         "CREATE TABLE t(a INTEGER PRIMARY KEY)",
                         attach,
                         "CREATE TABLE aux.side(v TEXT NOT NULL)",
                         "INSERT INTO aux.side VALUES('p')",
                     })
            {
                Execute(managed, statement);
                Execute(sqlite, statement == attach
                    ? "ATTACH DATABASE '"
                      + Path.Combine(directory, "sqlite-aux.db").Replace("'", "''", StringComparison.Ordinal)
                      + "' AS aux"
                    : statement);
            }

            AssertQueriesMatch(managed, sqlite, "PRAGMA aux.integrity_check");
            AssertQueriesMatch(managed, sqlite, "PRAGMA aux.quick_check(side)");

            // SQLite's pragma grammar takes a single token, so a schema-qualified
            // restriction is a syntax error rather than a cross-database check.
            Assert.Throws<EmbeddedSqlException>(
                () => Execute(managed, "PRAGMA integrity_check(aux.side)"));
            Assert.Throws<MsData.SqliteException>(
                () => Execute(sqlite, "PRAGMA integrity_check(aux.side)"));

            var managedError = Assert.Throws<EmbeddedSqlException>(
                () => Execute(managed, "PRAGMA aux.integrity_check(t)"));
            managedError!.Message.Should().Contain("no such table");
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void AnUnknownRestrictedTableFailsLikeSqlite()
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        using var sqlite = OpenSqlite();
        Execute(managed, "CREATE TABLE t(a)");
        Execute(sqlite, "CREATE TABLE t(a)");

        var managedError = Assert.Throws<EmbeddedSqlException>(
            () => Execute(managed, "PRAGMA integrity_check(nosuchtable)"));
        var sqliteError = Assert.Throws<MsData.SqliteException>(
            () => Execute(sqlite, "PRAGMA integrity_check(nosuchtable)"));
        managedError!.Message.Should().Contain("no such table: nosuchtable");
        sqliteError!.Message.Should().Contain(managedError.Message);
    }

    [Test]
    [TestCase("PRAGMA integrity_check")]
    [TestCase("PRAGMA quick_check")]
    [TestCase("PRAGMA integrity_check(1)")]
    public void DeclaredConstraintViolationsMatchSqlite(string query)
    {
        AssertCorruptFileMatchesSqlite(
            [
                "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT, c INT)",
                "INSERT INTO t VALUES(1,'x',5),(9,NULL,-4)",
            ],
            [
                "UPDATE sqlite_schema SET sql="
                + "'CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT NOT NULL, c INT CHECK(c > 0))' WHERE name='t'",
            ],
            query);
    }

    [Test]
    [TestCase("PRAGMA integrity_check")]
    [TestCase("PRAGMA quick_check")]
    public void AStoredNonUniqueIndexEntryFailsToOpenInsteadOfBeingReported(string query)
    {
        // SQLite opens this database and reports "non-unique entry in index ix_b".
        // The managed file store validates stored index records while loading, so
        // it refuses the database outright instead. Assert both behaviors so the
        // divergence stays explicit.
        WithCorruptDatabase(
            [
                "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)",
                "CREATE INDEX ix_b ON t(b)",
                "INSERT INTO t VALUES(1,'x'),(2,'y'),(3,'x')",
            ],
            ["UPDATE sqlite_schema SET sql='CREATE UNIQUE INDEX ix_b ON t(b)' WHERE name='ix_b'"],
            (managedPath, sqlitePath) =>
            {
                var managedError = Assert.Throws<EmbeddedSqlException>(() =>
                {
                    using var database = EmbeddedDatabase.OpenFile(managedPath, PhysicalFileSystem.Instance);
                    using var managed = database.Connect();
                    ReadRows(managed, query);
                });
                managedError!.Message.Should().Contain("duplicate non-NULL keys");

                using var sqlite = OpenSqlite(sqlitePath);
                var sqliteRows = ReadRows(sqlite, query);
                sqliteRows.Should().HaveCount(1);
                sqliteRows[0][0].Should().Be(
                    query.Contains("quick", StringComparison.Ordinal) ? "ok" : "non-unique entry in index ix_b");
            });
    }

    [Test]
    [TestCase("PRAGMA integrity_check(u)", true)]
    // SQLite walks its schema hash table, so problems from independent tables
    // have no defined relative order; compare them as a set.
    [TestCase("PRAGMA integrity_check", false)]
    public void ARestrictedCheckOnlyReportsItsTableLikeSqlite(string query, bool ordered)
    {
        AssertCorruptFileMatchesSqlite(
            [
                "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)",
                "CREATE TABLE u(v TEXT)",
                "INSERT INTO t VALUES(1,NULL)",
                "INSERT INTO u VALUES(NULL)",
            ],
            [
                "UPDATE sqlite_schema SET sql='CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT NOT NULL)' WHERE name='t'",
                "UPDATE sqlite_schema SET sql='CREATE TABLE u(v TEXT NOT NULL)' WHERE name='u'",
            ],
            query,
            ordered);
    }

    // Builds one corrupt SQLite file, then compares what each engine reports for
    // independent copies of it. Managed pagers own the main file exclusively, so
    // the two readers must not share a path.
    private static void AssertCorruptFileMatchesSqlite(
        IReadOnlyList<string> setup,
        IReadOnlyList<string> schemaRewrites,
        string query,
        bool ordered = true)
        => WithCorruptDatabase(
            setup,
            schemaRewrites,
            (managedPath, sqlitePath) =>
            {
                IReadOnlyList<SqlValue[]> managedRows;
                using (var database = EmbeddedDatabase.OpenFile(managedPath, PhysicalFileSystem.Instance))
                using (var managed = database.Connect())
                {
                    managedRows = ReadRows(managed, query);
                }

                IReadOnlyList<object?[]> sqliteRows;
                using (var sqlite = OpenSqlite(sqlitePath))
                {
                    sqliteRows = ReadRows(sqlite, query);
                }

                AssertRowsMatch(managedRows, sqliteRows, ordered);
            });

    private static void WithCorruptDatabase(
        IReadOnlyList<string> setup,
        IReadOnlyList<string> schemaRewrites,
        Action<string, string> assert)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Ahtola-integrity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source.db");
            using (var writer = OpenSqlite(source))
            {
                foreach (var statement in setup)
                    Execute(writer, statement);

                Execute(writer, "PRAGMA writable_schema=ON");
                foreach (var statement in schemaRewrites)
                    Execute(writer, statement);

                Execute(writer, "PRAGMA writable_schema=RESET");
            }

            MsData.SqliteConnection.ClearAllPools();

            var managedPath = Path.Combine(directory, "managed.db");
            var sqlitePath = Path.Combine(directory, "sqlite.db");
            File.Copy(source, managedPath);
            File.Copy(source, sqlitePath);

            assert(managedPath, sqlitePath);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertColumnNamesMatch(
        EmbeddedConnection managed,
        MsData.SqliteConnection sqlite,
        string query)
    {
        using var statement = managed.Prepare(query);
        var managedNames = new string[statement.GetColumnCount()];
        for (var column = 0; column < managedNames.Length; column++)
            managedNames[column] = statement.GetColumnName(column);

        using var command = sqlite.CreateCommand();
        command.CommandText = query;
        using var reader = command.ExecuteReader();
        var sqliteNames = new string[reader.FieldCount];
        for (var column = 0; column < sqliteNames.Length; column++)
            sqliteNames[column] = reader.GetName(column);

        managedNames.Should().Equal(sqliteNames);
    }

    private static void AssertQueriesMatch(
        EmbeddedConnection managed,
        MsData.SqliteConnection sqlite,
        string query)
        => AssertRowsMatch(ReadRows(managed, query), ReadRows(sqlite, query));

    private static void AssertRowsMatch(
        IReadOnlyList<SqlValue[]> managedRows,
        IReadOnlyList<object?[]> sqliteRows,
        bool ordered = true)
    {
        managedRows.Should().HaveCount(
            sqliteRows.Count,
            "managed rows {0} should match SQLite rows {1}",
            FormatRows(managedRows),
            FormatRows(sqliteRows));

        var managedText = managedRows.Select(Describe).ToArray();
        var sqliteText = sqliteRows
            .Select(row => string.Join('\u001f', row.Select(value => value?.ToString() ?? "\u0000")))
            .ToArray();
        if (ordered)
            managedText.Should().Equal(sqliteText);
        else
            managedText.Should().BeEquivalentTo(sqliteText);

        foreach (var row in managedRows)
        {
            foreach (var value in row)
                value.Kind.Should().BeOneOf(SqlValueKind.Text, SqlValueKind.Null);
        }
    }

    private static string Describe(SqlValue[] row)
        => string.Join(
            '\u001f',
            row.Select(value => value.Kind == SqlValueKind.Null ? "\u0000" : value.AsText()));

    private static MsData.SqliteConnection OpenSqlite(string path = ":memory:")
    {
        var connection = new MsData.SqliteConnection($"Data Source={path}");
        connection.Open();
        return connection;
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    private static void Execute(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var values = new SqlValue[statement.GetColumnCount()];
            for (var column = 0; column < values.Length; column++)
                values[column] = statement.GetValue(column);
            rows.Add(values);
        }

        return rows;
    }

    private static IReadOnlyList<object?[]> ReadRows(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var values = new object?[reader.FieldCount];
            for (var column = 0; column < values.Length; column++)
                values[column] = reader.IsDBNull(column) ? null : reader.GetValue(column);
            rows.Add(values);
        }

        return rows;
    }

    private static string FormatRows<T>(IReadOnlyList<T[]> rows)
        => string.Join(
            "; ",
            rows.Select(row => "[" + string.Join(", ", row.Select(value => value?.ToString() ?? "NULL")) + "]"));
}
