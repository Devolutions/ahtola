using AwesomeAssertions;
using Ahtola.Data.Sqlite;
using ManagedSqliteConnection = Ahtola.Data.Sqlite.SqliteConnection;
using NativeSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedUpsertPersistenceTests
{
    [SetUp]
    public void SetUp() => ManagedSqliteConnection.ClearAllPools();

    [TearDown]
    public void TearDown() => ManagedSqliteConnection.ClearAllPools();

    [Test]
    public void RichTargetInferenceSurvivesReopenPageMigrationBackupAndPoolReuse()
    {
        var sourcePath = CreateDatabasePath();
        var destinationPath = CreateDatabasePath();
        try
        {
            using (var source = OpenManaged(sourcePath, pooling: false))
            {
                source.ExecuteNonQuery(
                    """
                    CREATE TABLE items(
                        id INTEGER PRIMARY KEY,
                        code TEXT,
                        active INTEGER,
                        value INTEGER,
                        normalized TEXT GENERATED ALWAYS AS (lower(code)) VIRTUAL
                    ) STRICT;
                    CREATE UNIQUE INDEX items_active_code
                    ON items(lower(code) COLLATE NOCASE DESC)
                    WHERE active = 1;
                    INSERT INTO items(id, code, active, value) VALUES (1, 'item', 1, 1);
                    """);
                UpsertAndAssert(source, 2, "ITEM", 2);
            }

            using (var reopened = OpenManaged(sourcePath, pooling: false))
            {
                UpsertAndAssert(reopened, 3, "Item", 3);
                reopened.ExecuteScalar<string>("PRAGMA journal_mode = DELETE;").Should().Be("delete");
                reopened.ExecuteNonQuery("PRAGMA page_size = 1024; VACUUM;");
                reopened.ExecuteScalar<long>("PRAGMA page_size;").Should().Be(1024);
                UpsertAndAssert(reopened, 4, "ITEM", 4);
            }

            using (var migrated = OpenManaged(sourcePath, pooling: false))
            using (var destination = OpenManaged(destinationPath, pooling: false))
            {
                migrated.ExecuteScalar<long>("PRAGMA page_size;").Should().Be(1024);
                UpsertAndAssert(migrated, 5, "item", 5);
                migrated.BackupDatabase(destination);
                UpsertAndAssert(destination, 6, "ITEM", 6);
            }

            using var pooled = OpenManaged(destinationPath, pooling: true);
            var physical = pooled.ManagedConnection;
            UpsertAndAssert(pooled, 7, "Item", 7);
            pooled.Close();
            pooled.Open();
            pooled.ManagedConnection.Should().BeSameAs(physical);
            UpsertAndAssert(pooled, 8, "ITEM", 8);
            pooled.ExecuteScalar<string>(
                    "SELECT sql FROM sqlite_schema WHERE name = 'items_active_code';")
                .Should().Contain("lower(code) COLLATE NOCASE DESC")
                .And.Contain("WHERE active = 1");
        }
        finally
        {
            ManagedSqliteConnection.ClearAllPools();
            DeleteDatabase(sourcePath);
            DeleteDatabase(destinationPath);
        }
    }

    [Test]
    public void ReopenRejectsApplicationDefinedExpressionIndexesBeforeUse()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var native = new NativeSqliteConnection($"Data Source={path};Pooling=False"))
            {
                native.Open();
                native.CreateFunction<string?, string?>(
                    "custom_key",
                    static value => value?.ToUpperInvariant(),
                    isDeterministic: true);
                using var command = native.CreateCommand();
                command.CommandText =
                    "CREATE TABLE items(code TEXT);"
                    + "CREATE UNIQUE INDEX items_custom_key ON items(custom_key(code));"
                    + "INSERT INTO items VALUES ('one');";
                command.ExecuteNonQuery();
            }

            using var managed = new ManagedSqliteConnection(
                $"Data Source={path};Pooling=False;Local Provider=Managed");
            managed.Invoking(static connection => connection.Open())
                .Should().Throw<Exception>()
                .WithMessage("*non-deterministic functions are prohibited in index expressions*");
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void ReopenPreservesNewestMatchingIndexInferencePrecedence()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var connection = OpenManaged(path, pooling: false))
            {
                connection.ExecuteNonQuery(
                    """
                    CREATE TABLE items(code TEXT, active INTEGER, value INTEGER);
                    CREATE UNIQUE INDEX z_older_binary
                    ON items((code || '') COLLATE BINARY)
                    WHERE active = 1;
                    CREATE UNIQUE INDEX a_newer_nocase
                    ON items((code || '') COLLATE NOCASE)
                    WHERE active = 1;
                    INSERT INTO items VALUES ('item', 1, 1);
                    INSERT INTO items VALUES ('ITEM', 1, 2)
                    ON CONFLICT(code || '') WHERE active = 1
                    DO UPDATE SET value = excluded.value;
                    """);
                connection.ExecuteScalar<long>("SELECT value FROM items;").Should().Be(2);
            }

            using var reopened = OpenManaged(path, pooling: false);
            reopened.ExecuteNonQuery(
                """
                INSERT INTO items VALUES ('ITEM', 1, 3)
                ON CONFLICT(code || '') WHERE active = 1
                DO UPDATE SET value = excluded.value;
                """);
            reopened.ExecuteScalar<long>("SELECT value FROM items;").Should().Be(3);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void ReopenAcceptsValidatedLegacyRedundantImplicitAutoindex()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var native = new NativeSqliteConnection($"Data Source={path};Pooling=False"))
            {
                native.Open();
                using var command = native.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE items(key TEXT, other TEXT, PRIMARY KEY(key), UNIQUE(other));
                    CREATE UNIQUE INDEX legacy_redundant ON items(key);
                    INSERT INTO items VALUES ('one', 'other');
                    PRAGMA writable_schema = ON;
                    UPDATE sqlite_schema
                    SET name = 'sqlite_autoindex_items_3'
                    WHERE name = 'sqlite_autoindex_items_2';
                    UPDATE sqlite_schema
                    SET name = 'sqlite_autoindex_items_2', sql = NULL
                    WHERE name = 'legacy_redundant';
                    UPDATE sqlite_schema
                    SET sql = 'CREATE TABLE items(key TEXT, other TEXT, PRIMARY KEY(key), UNIQUE(key), UNIQUE(other))'
                    WHERE type = 'table' AND name = 'items';
                    PRAGMA writable_schema = OFF;
                    """;
                command.ExecuteNonQuery();
            }

            using var managed = OpenManaged(path, pooling: false);
            managed.ExecuteScalar<string>("SELECT key FROM items;").Should().Be("one");
            managed.ExecuteNonQuery(
                "INSERT INTO items VALUES ('one', 'new') ON CONFLICT(key) DO NOTHING;");
            managed.ExecuteScalar<long>("SELECT COUNT(*) FROM items;").Should().Be(1);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void BoundedCommitPersistsRecreatedIndexInferenceOrder()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var connection = OpenManaged(path, pooling: false))
            {
                connection.ExecuteNonQuery(
                    """
                    CREATE TABLE items(code TEXT, value INTEGER);
                    CREATE UNIQUE INDEX z_binary ON items(code COLLATE BINARY);
                    CREATE UNIQUE INDEX a_nocase ON items(code COLLATE NOCASE);
                    CREATE TABLE mutations(id INTEGER PRIMARY KEY);
                    INSERT INTO items VALUES ('item', 1);
                    BEGIN;
                    DROP INDEX z_binary;
                    CREATE UNIQUE INDEX z_binary ON items(code COLLATE BINARY);
                    INSERT INTO mutations VALUES (1);
                    COMMIT;
                    """);
                connection.Invoking(value => value.ExecuteNonQuery(
                        """
                        INSERT INTO items VALUES ('ITEM', 2)
                        ON CONFLICT(code) DO UPDATE SET value = excluded.value;
                        """))
                    .Should().Throw<SqliteException>().WithMessage("*UNIQUE constraint failed*");
            }

            using var reopened = OpenManaged(path, pooling: false);
            reopened.Invoking(value => value.ExecuteNonQuery(
                    """
                    INSERT INTO items VALUES ('ITEM', 3)
                    ON CONFLICT(code) DO UPDATE SET value = excluded.value;
                    """))
                .Should().Throw<SqliteException>().WithMessage("*UNIQUE constraint failed*");
            reopened.ExecuteScalar<long>("SELECT value FROM items;").Should().Be(1);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void NativeSchemaRowOrderDoesNotForceSchemaRewrite()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var setup = OpenManaged(path, pooling: false))
            {
                setup.ExecuteNonQuery(
                    """
                    CREATE TABLE z(id INTEGER PRIMARY KEY, value TEXT);
                    CREATE TABLE a(id INTEGER PRIMARY KEY, value TEXT);
                    INSERT INTO z VALUES (1, 'one');
                    """);
            }

            using (var native = new NativeSqliteConnection($"Data Source={path};Pooling=False"))
            {
                native.Open();
                using var command = native.CreateCommand();
                command.CommandText =
                    """
                    PRAGMA writable_schema = ON;
                    UPDATE sqlite_schema SET rowid = -1 WHERE type = 'table' AND name = 'a';
                    UPDATE sqlite_schema SET rowid = -2 WHERE type = 'table' AND name = 'z';
                    UPDATE sqlite_schema SET rowid = 1 WHERE type = 'table' AND name = 'z';
                    UPDATE sqlite_schema SET rowid = 2 WHERE type = 'table' AND name = 'a';
                    PRAGMA writable_schema = OFF;
                    """;
                command.ExecuteNonQuery();
            }

            using (var managed = OpenManaged(path, pooling: false))
            {
                var schemaVersion = managed.ExecuteScalar<long>("PRAGMA schema_version;");
                managed.ExecuteNonQuery("INSERT INTO z VALUES (2, 'two');");
                managed.ExecuteScalar<long>("PRAGMA schema_version;").Should().Be(schemaVersion);
            }

            using var reopened = OpenManaged(path, pooling: false);
            reopened.ExecuteScalar<long>("SELECT COUNT(*) FROM z;").Should().Be(2);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    private static void UpsertAndAssert(
        ManagedSqliteConnection connection,
        long candidateId,
        string code,
        long value)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO items(id, code, active, value) VALUES ($id, $code, 1, $value)
            ON CONFLICT(lower(code) COLLATE nocase ASC)
            WHERE active = 1
            DO UPDATE SET code = excluded.code, value = excluded.value
            RETURNING id, code, normalized, value;
            """;
        command.Parameters.AddWithValue("$id", candidateId);
        command.Parameters.AddWithValue("$code", code);
        command.Parameters.AddWithValue("$value", value);
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        reader.GetInt64(0).Should().Be(1);
        reader.GetString(1).Should().Be(code);
        reader.GetString(2).Should().Be("item");
        reader.GetInt64(3).Should().Be(value);
        reader.Read().Should().BeFalse();
    }

    private static ManagedSqliteConnection OpenManaged(string path, bool pooling)
    {
        var connection = new ManagedSqliteConnection(
            $"Data Source={path};Pooling={pooling};Local Provider=Managed");
        connection.Open();
        return connection;
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "managed-upsert-persistence-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"upsert-{Guid.NewGuid():N}.db");
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-journal" })
        {
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }
}
