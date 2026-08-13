using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// End-to-end dual-cursor SQL routing under <c>PRAGMA journal_mode=mvcc</c> +
/// <c>BEGIN CONCURRENT</c>: peer uncommitted writes stay invisible, own writes
/// are visible, post-commit SI, and same-row WW conflicts surface on the SQL path.
/// </summary>
public sealed class MvccSelectDualCursorRoutingTests
{
    [Test]
    public void PeerUncommittedInsertIsInvisibleUntilCommit()
    {
        using var db = new RoutingFileDatabase();
        using var writer = db.Connect();
        using var reader = db.Connect();

        writer.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        ReadValue(reader, "PRAGMA journal_mode;").Should().Be("mvcc");

        writer.ExecuteNonQuery("BEGIN CONCURRENT;");
        writer.ExecuteNonQuery("INSERT INTO t VALUES (42);");

        reader.ExecuteNonQuery("BEGIN CONCURRENT;");
        Convert.ToInt64(Scalar(reader, "SELECT COUNT(*) FROM t WHERE v = 42;")).Should().Be(0L);

        writer.ExecuteNonQuery("COMMIT;");

        // Reader snapshot began before the commit — SI keeps the insert dark.
        Convert.ToInt64(Scalar(reader, "SELECT COUNT(*) FROM t WHERE v = 42;")).Should().Be(0L);
        reader.ExecuteNonQuery("COMMIT;");

        Convert.ToInt64(Scalar(reader, "SELECT COUNT(*) FROM t WHERE v = 42;")).Should().Be(1L);
    }

    [Test]
    public void WriterSeesOwnUncommittedInsertViaSelect()
    {
        using var db = new RoutingFileDatabase();
        using var connection = db.Connect();

        connection.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        connection.ExecuteNonQuery("BEGIN CONCURRENT;");
        connection.ExecuteNonQuery("INSERT INTO t VALUES (7);");
        Convert.ToInt64(Scalar(connection, "SELECT v FROM t WHERE v = 7;")).Should().Be(7L);
        connection.ExecuteNonQuery("COMMIT;");
    }

    [Test]
    public void PeerUncommittedDeleteKeepsBaseVisibleToSibling()
    {
        using var db = new RoutingFileDatabase();
        using var seeder = db.Connect();
        seeder.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        seeder.ExecuteNonQuery("INSERT INTO t VALUES (1);");

        using var deleter = db.Connect();
        using var reader = db.Connect();
        deleter.ExecuteNonQuery("BEGIN CONCURRENT;");
        reader.ExecuteNonQuery("BEGIN CONCURRENT;");

        deleter.ExecuteNonQuery("DELETE FROM t WHERE v = 1;");
        Convert.ToInt64(Scalar(deleter, "SELECT COUNT(*) FROM t WHERE v = 1;")).Should().Be(0L);
        Convert.ToInt64(Scalar(reader, "SELECT COUNT(*) FROM t WHERE v = 1;")).Should().Be(1L);

        deleter.ExecuteNonQuery("COMMIT;");
        // Reader began before delete commit — still sees the base row under SI.
        Convert.ToInt64(Scalar(reader, "SELECT COUNT(*) FROM t WHERE v = 1;")).Should().Be(1L);
        reader.ExecuteNonQuery("COMMIT;");

        Convert.ToInt64(Scalar(reader, "SELECT COUNT(*) FROM t WHERE v = 1;")).Should().Be(0L);
    }

    [Test]
    public void ConcurrentUpdateOfSameBaseRowRaisesWriteWriteConflict()
    {
        using var db = new RoutingFileDatabase();
        using var seeder = db.Connect();
        seeder.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        seeder.ExecuteNonQuery("INSERT INTO t VALUES (1);");

        using var a = db.Connect();
        using var b = db.Connect();
        a.ExecuteNonQuery("BEGIN CONCURRENT;");
        b.ExecuteNonQuery("BEGIN CONCURRENT;");
        a.ExecuteNonQuery("UPDATE t SET v = 10 WHERE v = 1;");

        var error = Capture(() => b.ExecuteNonQuery("UPDATE t SET v = 20 WHERE v = 1;"));
        error.Should().NotBeNull();
        error!.Message.Should().ContainEquivalentOf("write-write conflict");
        b.ExecuteNonQuery("ROLLBACK;");
        a.ExecuteNonQuery("COMMIT;");

        Convert.ToInt64(Scalar(a, "SELECT v FROM t;")).Should().Be(10L);
    }

    [Test]
    public void ConcurrentDeleteOfSameBaseRowRaisesWriteWriteConflict()
    {
        using var db = new RoutingFileDatabase();
        using var seeder = db.Connect();
        seeder.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        seeder.ExecuteNonQuery("INSERT INTO t VALUES (3);");

        using var a = db.Connect();
        using var b = db.Connect();
        a.ExecuteNonQuery("BEGIN CONCURRENT;");
        b.ExecuteNonQuery("BEGIN CONCURRENT;");
        a.ExecuteNonQuery("DELETE FROM t WHERE v = 3;");

        var error = Capture(() => b.ExecuteNonQuery("DELETE FROM t WHERE v = 3;"));
        error.Should().NotBeNull();
        error!.Message.Should().ContainEquivalentOf("write-write conflict");
        b.ExecuteNonQuery("ROLLBACK;");
        a.ExecuteNonQuery("COMMIT;");
    }

    [Test]
    public void PostCommitSelectSeesPeerInsertAndUpdate()
    {
        using var db = new RoutingFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();
        a.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");

        a.ExecuteNonQuery("BEGIN CONCURRENT;");
        a.ExecuteNonQuery("INSERT INTO t VALUES (100);");
        a.ExecuteNonQuery("COMMIT;");

        b.ExecuteNonQuery("BEGIN CONCURRENT;");
        b.ExecuteNonQuery("UPDATE t SET v = 200 WHERE v = 100;");
        b.ExecuteNonQuery("COMMIT;");

        Convert.ToInt64(Scalar(a, "SELECT v FROM t;")).Should().Be(200L);
    }

    [TestCase("CREATE TABLE other(value INTEGER);")]
    [TestCase("CREATE TABLE other AS SELECT v FROM t;")]
    [TestCase("DROP TABLE t;")]
    [TestCase("ALTER TABLE t ADD COLUMN other INTEGER;")]
    [TestCase("CREATE INDEX ix_t_v ON t(v);")]
    [TestCase("CREATE VIEW t_view AS SELECT v FROM t;")]
    [TestCase("CREATE TRIGGER t_insert AFTER INSERT ON t BEGIN SELECT new.v; END;")]
    public void ConcurrentSchemaChangesFailClosed(string sql)
    {
        using var db = new RoutingFileDatabase();
        using var connection = db.Connect();

        connection.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        connection.ExecuteNonQuery("BEGIN CONCURRENT;");

        var error = Capture(() => connection.ExecuteNonQuery(sql));
        error.Should().NotBeNull();
        error!.Message.Should().ContainEquivalentOf("schema changes");

        connection.ExecuteNonQuery("INSERT INTO t VALUES (1);");
        connection.ExecuteNonQuery("COMMIT;");
        Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM t;")).Should().Be(1L);
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static string ReadValue(SqliteConnection connection, string sql)
        => Convert.ToString(Scalar(connection, sql)) ?? string.Empty;

    private static Exception? Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception) when (exception is SqliteException or EmbeddedSqlException)
        {
            return exception;
        }
    }

    private sealed class RoutingFileDatabase : IDisposable
    {
        private readonly List<SqliteConnection> _connections = [];

        public RoutingFileDatabase()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"Ahtola-mvcc-routing-{Guid.NewGuid():N}.db");

            using var seed = new SqliteConnection($"Data Source={Path};Local Provider=Managed");
            seed.Open();
            seed.ExecuteNonQuery("CREATE TABLE t(v INTEGER);");
        }

        public string Path { get; }

        public SqliteConnection Connect()
        {
            var connection = new SqliteConnection($"Data Source={Path};Local Provider=Managed;Default Timeout=1");
            connection.Open();
            _connections.Add(connection);
            return connection;
        }

        public void Dispose()
        {
            foreach (var connection in _connections)
                connection.Dispose();

            foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
            {
                var candidate = Path + suffix;
                if (!File.Exists(candidate))
                    continue;

                try
                {
                    File.Delete(candidate);
                }
                catch
                {
                    // best-effort cleanup
                }
            }
        }
    }
}
