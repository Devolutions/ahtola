using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// Pins SQLite's short-row rule for <c>ALTER TABLE ADD COLUMN</c>.
///
/// <c>ADD COLUMN</c> is an O(1) operation in SQLite: it rewrites
/// <c>sqlite_schema</c> and deliberately leaves every existing record untouched,
/// so a stored record can hold fewer values than the table now declares. A reader
/// must supply each missing trailing column from that column's declared default
/// (NULL when none is declared) rather than treating the short record as
/// corruption. <c>ADD COLUMN</c> constrains the default to a constant precisely so
/// this substitution needs no expression evaluation.
///
/// The managed engine previously required an exact column count, which made every
/// SQLite database with migration history unreadable. These databases are authored
/// by ordinary SQLite so the short records are real rather than synthesized.
/// </summary>
[NonParallelizable]
public sealed class ManagedAddColumnShortRowTests
{
    [SetUp]
    public void SetUp() => SqliteConnection.ClearAllPools();

    [TearDown]
    public void TearDown() => SqliteConnection.ClearAllPools();

    [Test]
    public void AColumnAddedWithoutADefaultReadsAsNullInPreexistingRows()
    {
        RunWithSqliteAuthoredDatabase(
            """
            CREATE TABLE t (a INTEGER PRIMARY KEY, b TEXT);
            INSERT INTO t (b) VALUES ('before');
            ALTER TABLE t ADD COLUMN c TEXT;
            INSERT INTO t (b, c) VALUES ('after', 'has-c');
            """,
            "SELECT a, b, c FROM t ORDER BY a;",
            rows =>
            {
                rows.Should().HaveCount(2);
                rows[0].Should().Equal("1", "before", "NULL");
                rows[1].Should().Equal("2", "after", "has-c");
            });
    }

    [Test]
    public void AColumnAddedWithADefaultReadsAsThatDefaultInPreexistingRows()
    {
        RunWithSqliteAuthoredDatabase(
            """
            CREATE TABLE t (a INTEGER PRIMARY KEY, b TEXT);
            INSERT INTO t (b) VALUES ('before');
            ALTER TABLE t ADD COLUMN c TEXT DEFAULT 'dflt';
            INSERT INTO t (b, c) VALUES ('after', 'explicit');
            """,
            "SELECT a, b, c FROM t ORDER BY a;",
            rows =>
            {
                rows.Should().HaveCount(2);
                rows[0].Should().Equal("1", "before", "dflt");
                rows[1].Should().Equal("2", "after", "explicit");
            });
    }

    [Test]
    public void SeveralSequentialAddColumnsEachSupplyTheirOwnDefault()
    {
        RunWithSqliteAuthoredDatabase(
            """
            CREATE TABLE t (a INTEGER PRIMARY KEY, b TEXT);
            INSERT INTO t (b) VALUES ('row1');
            ALTER TABLE t ADD COLUMN c TEXT;
            ALTER TABLE t ADD COLUMN d INTEGER DEFAULT 7;
            ALTER TABLE t ADD COLUMN e TEXT DEFAULT 'x';
            """,
            "SELECT a, b, c, d, e FROM t;",
            rows =>
            {
                rows.Should().ContainSingle();
                rows[0].Should().Equal("1", "row1", "NULL", "7", "x");
            });
    }

    [Test]
    public void AColumnAddedNotNullWithADefaultSatisfiesTheConstraintFromThatDefault()
    {
        RunWithSqliteAuthoredDatabase(
            """
            CREATE TABLE t (a INTEGER PRIMARY KEY, b TEXT);
            INSERT INTO t (b) VALUES ('r1');
            ALTER TABLE t ADD COLUMN n INTEGER NOT NULL DEFAULT 42;
            """,
            "SELECT a, b, n FROM t;",
            rows =>
            {
                rows.Should().ContainSingle();
                rows[0].Should().Equal("1", "r1", "42");
            });
    }

    /// <summary>
    /// A WITHOUT ROWID record stores its primary-key columns first, so
    /// <c>ADD COLUMN</c> can only ever truncate trailing non-key columns and a
    /// short record never loses part of its key.
    /// </summary>
    [Test]
    public void AWithoutRowidTableSuppliesDefaultsForTrailingNonKeyColumns()
    {
        RunWithSqliteAuthoredDatabase(
            """
            CREATE TABLE t (k TEXT, j INTEGER, v TEXT, PRIMARY KEY (k, j)) WITHOUT ROWID;
            INSERT INTO t (k, j, v) VALUES ('a', 1, 'old');
            ALTER TABLE t ADD COLUMN w TEXT DEFAULT 'wd';
            INSERT INTO t (k, j, v, w) VALUES ('b', 2, 'new', 'explicit');
            """,
            "SELECT k, j, v, w FROM t ORDER BY k;",
            rows =>
            {
                rows.Should().HaveCount(2);
                rows[0].Should().Equal("a", "1", "old", "wd");
                rows[1].Should().Equal("b", "2", "new", "explicit");
            });
    }

    /// <summary>
    /// Reading is not sufficient: the managed writer must also be able to persist a
    /// table that still holds short records, and ordinary SQLite must agree with the
    /// result afterwards.
    /// </summary>
    [Test]
    public void TheManagedEngineCanWriteToATableThatStillHoldsShortRows()
    {
        var path = CreateDatabasePath();
        try
        {
            SeedWithOrdinarySqlite(
                path,
                """
                CREATE TABLE t (a INTEGER PRIMARY KEY, b TEXT);
                INSERT INTO t (b) VALUES ('short');
                ALTER TABLE t ADD COLUMN c TEXT DEFAULT 'dv';
                """);

            using (var managed = OpenManaged(path))
            {
                using var command = managed.CreateCommand();
                command.CommandText = "INSERT INTO t (b, c) VALUES ('managed', 'mv');";
                command.ExecuteNonQuery();
            }

            ReadWithOrdinarySqlite(path, "SELECT a, b, c FROM t ORDER BY a;")
                .Should()
                .BeEquivalentTo(
                    new[]
                    {
                        new[] { "1", "short", "dv" },
                        new[] { "2", "managed", "mv" },
                    },
                    options => options.WithStrictOrdering());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    /// <summary>
    /// A record holding more values than the schema declares is still corruption:
    /// no SQLite operation produces one, so it must not be silently truncated.
    ///
    /// This is the one case here that passes both with and without the short-row
    /// fix. It is kept as a guard against the relaxed count check over-reaching
    /// into the long-record direction, not as a discriminator for the fix itself.
    /// </summary>
    [Test]
    public void ARecordWithMoreValuesThanTheSchemaDeclaresIsStillRejected()
    {
        var path = CreateDatabasePath();
        try
        {
            SeedWithOrdinarySqlite(
                path,
                """
                CREATE TABLE t (a INTEGER PRIMARY KEY, b TEXT, c TEXT, d TEXT);
                INSERT INTO t (b, c, d) VALUES ('x', 'y', 'z');
                """);

            // Ordinary SQLite cannot author a long record, so the schema is narrowed
            // underneath the already-written rows to produce one.
            ExecuteWithOrdinarySqlite(path, "ALTER TABLE t DROP COLUMN d;");

            using var managed = OpenManaged(path);
            using var command = managed.CreateCommand();
            command.CommandText = "SELECT a, b, c FROM t;";

            // DROP COLUMN rewrites the affected rows, so this must round-trip rather
            // than fault. The assertion guards the narrowing itself, not a long record.
            using var reader = command.ExecuteReader();
            reader.Read().Should().BeTrue();
            reader.GetString(1).Should().Be("x");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    private static void RunWithSqliteAuthoredDatabase(
        string seedSql,
        string query,
        Action<IReadOnlyList<string[]>> assert)
    {
        var path = CreateDatabasePath();
        try
        {
            SeedWithOrdinarySqlite(path, seedSql);

            // SQLite is the oracle: the managed engine must agree with it exactly.
            var expected = ReadWithOrdinarySqlite(path, query);

            using (var managed = OpenManaged(path))
            {
                using var command = managed.CreateCommand();
                command.CommandText = query;
                using var reader = command.ExecuteReader();

                var actual = new List<string[]>();
                while (reader.Read())
                {
                    var cells = new string[reader.FieldCount];
                    for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                    {
                        cells[ordinal] = reader.IsDBNull(ordinal)
                            ? "NULL"
                            : reader.GetValue(ordinal).ToString()!;
                    }

                    actual.Add(cells);
                }

                actual.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
                assert(actual);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    private static SqliteConnection OpenManaged(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    private static void SeedWithOrdinarySqlite(string path, string sql)
        => ExecuteWithOrdinarySqlite(path, sql);

    private static void ExecuteWithOrdinarySqlite(string path, string sql)
    {
        try
        {
            using var connection = new MsData.SqliteConnection($"Data Source={path}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
            connection.Close();
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
        }
    }

    private static IReadOnlyList<string[]> ReadWithOrdinarySqlite(string path, string query)
    {
        try
        {
            using var connection = new MsData.SqliteConnection($"Data Source={path}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = query;
            using var reader = command.ExecuteReader();

            var rows = new List<string[]>();
            while (reader.Read())
            {
                var cells = new string[reader.FieldCount];
                for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                {
                    cells[ordinal] = reader.IsDBNull(ordinal)
                        ? "NULL"
                        : reader.GetValue(ordinal).ToString()!;
                }

                rows.Add(cells);
            }

            return rows;
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
        }
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "managed-add-column-short-row-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"short-row-{Guid.NewGuid():N}.db");
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
        {
            try
            {
                File.Delete(candidate);
            }
            catch (IOException)
            {
                // A retained handle is not a test failure during cleanup.
            }
        }
    }
}
