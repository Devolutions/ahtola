using AwesomeAssertions;
using Ahtola.Data.Sqlite;
using Ahtola.Core;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedConnectionPoolingTests
{
    [SetUp]
    public void SetUp() => SqliteConnection.ClearAllPools();

    [TearDown]
    public void TearDown() => SqliteConnection.ClearAllPools();

    [Test]
    public void FileBackedConnectionReusesAResetPhysicalConnection()
    {
        var path = CreateDatabasePath();
        var attachedPath = CreateDatabasePath();
        try
        {
            using var connection = Open(path);
            connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");
            connection.ExecuteNonQuery($"ATTACH DATABASE '{EscapeSqlLiteral(attachedPath)}' AS attached;");
            connection.ExecuteNonQuery("PRAGMA foreign_keys = ON; PRAGMA recursive_triggers = ON;");

            using var prepared = connection.CreateCommand();
            prepared.CommandText = "SELECT 1;";
            prepared.Prepare();

            using var readerCommand = connection.CreateCommand();
            readerCommand.CommandText = "SELECT 2;";
            using var reader = readerCommand.ExecuteReader();
            reader.Read().Should().BeTrue();

            var physicalConnection = connection.ManagedConnection;
            connection.Close();
            connection.Open();
            connection.ManagedConnection.Should().BeSameAs(physicalConnection);
            ReadDatabaseNames(connection).Should().Equal("main");

            var transaction = connection.BeginTransaction();
            connection.ExecuteNonQuery("INSERT INTO data VALUES (42);");
            connection.ExecuteNonQuery("PRAGMA query_only = ON;");

            connection.Close();

            reader.IsClosed.Should().BeTrue();
            transaction.Connection.Should().BeNull();
            connection.Open();

            connection.ManagedConnection.Should().BeSameAs(physicalConnection);
            prepared.ExecuteScalar().Should().Be(1L);
            connection.ExecuteScalar<long>("SELECT COUNT(*) FROM data;").Should().Be(0);
            connection.ExecuteScalar<long>("PRAGMA foreign_keys;").Should().Be(1);
            connection.ExecuteScalar<long>("PRAGMA recursive_triggers;").Should().Be(0);
            connection.ExecuteScalar<long>("PRAGMA query_only;").Should().Be(0);
            connection.ExecuteScalar<long>("SELECT last_insert_rowid();").Should().Be(0);
        }
        finally
        {
            DeleteDatabase(path);
            DeleteDatabase(attachedPath);
        }
    }

    [Test]
    public void PooledHandleRefreshesCatalogAfterAnotherPhysicalHandleCommits()
    {
        var path = CreateDatabasePath();
        try
        {
            using var writer = Open(path);
            using var stale = Open(path);
            var stalePhysical = stale.ManagedConnection;

            stale.Close();
            writer.ExecuteNonQuery("CREATE TABLE data(value INTEGER); INSERT INTO data VALUES (1);");
            writer.Close();

            using var current = Open(path);
            current.ExecuteScalar<long>("SELECT value FROM data;").Should().Be(1);
            stale.Open();
            stale.ManagedConnection.Should().BeSameAs(stalePhysical);
            stale.ExecuteScalar<long>("SELECT value FROM data;").Should().Be(1);
            stale.ExecuteNonQuery("INSERT INTO data VALUES (2);");
            stale.ExecuteScalar<long>("SELECT COUNT(*) FROM data;").Should().Be(2);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void PooledReopenSurvivesSharedMemoryCarrierRemovedByNativeClose()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var seeder = Open(path))
            {
                seeder.ExecuteNonQuery("CREATE TABLE data(value INTEGER); INSERT INTO data VALUES (1);");
            }

            // Stock SQLite removes the -shm lock carrier on the last clean close, and
            // a foreign reader never recreates it. The managed read-only probe refuses
            // to create it by contract, so the pooling catalog refresh must tolerate
            // its absence instead of faulting the reopened pooled connection.
            File.Delete(path + "-shm");

            using var connection = Open(path);
            connection.ExecuteScalar<long>("SELECT value FROM data;").Should().Be(1);
            connection.ExecuteNonQuery("INSERT INTO data VALUES (2);");
            connection.ExecuteScalar<long>("SELECT COUNT(*) FROM data;").Should().Be(2);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void PooledWithoutRowidCatalogReopensAndRollsBackReturnedTransactions()
    {
        var path = CreateDatabasePath();
        try
        {
            using var connection = Open(path);
            connection.ExecuteNonQuery("""
                CREATE TABLE entry(
                    tenant TEXT,
                    sequence INTEGER,
                    value TEXT,
                    computed INTEGER GENERATED ALWAYS AS (sequence * 2) VIRTUAL,
                    PRIMARY KEY(tenant COLLATE NOCASE, sequence DESC),
                    UNIQUE(value)
                ) WITHOUT ROWID;
                CREATE INDEX entry_computed ON entry(computed DESC);
                INSERT INTO entry(tenant, sequence, value) VALUES
                    ('alpha', 1, 'one'),
                    ('Alpha', 2, 'two');
                """);
            var physical = connection.ManagedConnection;
            connection.Close();
            connection.Open();
            connection.ManagedConnection.Should().BeSameAs(physical);
            connection.ExecuteScalar<long>("SELECT computed FROM entry WHERE value = 'two';").Should().Be(4);

            using var transaction = connection.BeginTransaction();
            connection.ExecuteNonQuery("UPDATE entry SET sequence = 9 WHERE value = 'one';");
            connection.Close();
            transaction.Connection.Should().BeNull();

            connection.Open();
            connection.ExecuteScalar<long>("SELECT sequence FROM entry WHERE value = 'one';").Should().Be(1);
            connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_schema WHERE name = 'entry_computed';")
                .Should().Be(1);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void ClearPoolInvalidatesIdleAndRentedGenerations()
    {
        var path = CreateDatabasePath();
        try
        {
            using var idle = Open(path);
            var first = idle.ManagedConnection;
            idle.Close();
            idle.Open();
            idle.ManagedConnection.Should().BeSameAs(first);
            idle.Close();

            SqliteConnection.ClearPool(idle);
            first.Invoking(static connection => connection.Prepare("SELECT 1;"))
                .Should()
                .Throw<ObjectDisposedException>();
            idle.Open();
            var afterIdleClear = idle.ManagedConnection;
            afterIdleClear.Should().NotBeSameAs(first);

            SqliteConnection.ClearPool(idle);
            idle.Close();
            afterIdleClear.Invoking(static connection => connection.Prepare("SELECT 1;"))
                .Should()
                .Throw<ObjectDisposedException>();
            idle.Open();
            idle.ManagedConnection.Should().NotBeSameAs(afterIdleClear);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void ClearAllPoolsInvalidatesEveryFilePool()
    {
        var firstPath = CreateDatabasePath();
        var secondPath = CreateDatabasePath();
        try
        {
            using var first = Open(firstPath);
            using var second = Open(secondPath);
            var firstPhysical = first.ManagedConnection;
            var secondPhysical = second.ManagedConnection;
            first.Close();
            second.Close();

            SqliteConnection.ClearAllPools();
            first.Open();
            second.Open();

            first.ManagedConnection.Should().NotBeSameAs(firstPhysical);
            second.ManagedConnection.Should().NotBeSameAs(secondPhysical);
        }
        finally
        {
            DeleteDatabase(firstPath);
            DeleteDatabase(secondPath);
        }
    }

    [Test]
    public void ClearPoolDoesNotRequireTheDatabaseFileToStillExist()
    {
        var sqlitePath = CreateDatabasePath();
        var ahtolaPath = CreateDatabasePath();
        try
        {
            using (var writer = new SqliteConnection(
                       $"Data Source={sqlitePath};Pooling=False;Local Provider=Managed"))
            {
                writer.Open();
                writer.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");
            }

            using var readOnly = new SqliteConnection(
                $"Data Source={sqlitePath};Mode=ReadOnly;Pooling=True;Local Provider=Managed");
            readOnly.Open();
            readOnly.Close();
            DeleteDatabase(sqlitePath);

            Action clearSqlitePool = () => SqliteConnection.ClearPool(readOnly);
            clearSqlitePool.Should().NotThrow();

            using (var writer = new AhtolaConnection(
                       $"Data Source={ahtolaPath};Pooling=False;Local Provider=Managed"))
            {
                writer.Open();
                writer.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");
            }

            using var ahtolaReadOnly = new AhtolaConnection(
                $"Data Source={ahtolaPath};Mode=ReadOnly;Pooling=True;Local Provider=Managed");
            ahtolaReadOnly.Open();
            ahtolaReadOnly.Close();
            DeleteDatabase(ahtolaPath);

            Action clearAhtolaPool = () => AhtolaConnection.ClearPool(ahtolaReadOnly);
            clearAhtolaPool.Should().NotThrow();
        }
        finally
        {
            DeleteDatabase(sqlitePath);
            DeleteDatabase(ahtolaPath);
        }
    }

    [Test]
    public void MemoryEncryptionAndCallbacksAreNotPooled()
    {
        using (var memory = new SqliteConnection("Data Source=:memory:;Pooling=True;Local Provider=Managed"))
        {
            memory.Open();
            var first = memory.ManagedConnection;
            memory.Close();
            memory.Open();
            memory.ManagedConnection.Should().NotBeSameAs(first);
        }

        var modeMemoryPath = CreateDatabasePath();
        using (var modeMemory = new SqliteConnection(
                   $"Data Source={modeMemoryPath};Mode=Memory;Pooling=True;Local Provider=Managed"))
        {
            modeMemory.Open();
            var first = modeMemory.ManagedConnection;
            modeMemory.Close();
            modeMemory.Open();
            modeMemory.ManagedConnection.Should().NotBeSameAs(first);
            File.Exists(modeMemoryPath).Should().BeFalse();
        }

        var callbackPath = CreateDatabasePath();
        var encryptedPath = CreateDatabasePath();
        try
        {
            using (var callback = new SqliteConnection(
                       $"Data Source={callbackPath};Pooling=True;Local Provider=Managed"))
            {
                callback.CreateFunction("pool_callback", static () => 1L);
                callback.Open();
                var first = callback.ManagedConnection;
                callback.Close();
                callback.Open();
                callback.ManagedConnection.Should().NotBeSameAs(first);
            }

            const string key = "000102030405060708090A0B0C0D0E0F"
                               + "101112131415161718191A1B1C1D1E1F";
            using var encrypted = new SqliteConnection(
                $"Data Source={encryptedPath};Pooling=True;Local Provider=Managed;"
                + $"Encryption Cipher=AES256GCM;Encryption Key={key}");
            encrypted.Open();
            var encryptedFirst = encrypted.ManagedConnection;
            encrypted.Close();
            encrypted.Open();
            encrypted.ManagedConnection.Should().NotBeSameAs(encryptedFirst);
        }
        finally
        {
            DeleteDatabase(callbackPath);
            DeleteDatabase(encryptedPath);
        }
    }

    [Test]
    public void FailedOpenDoesNotPoisonThePool()
    {
        var path = CreateDatabasePath();
        try
        {
            File.WriteAllText(path, "not a sqlite database");
            using var connection = new SqliteConnection(
                $"Data Source={path};Pooling=True;Local Provider=Managed");

            connection.Invoking(static value => value.Open()).Should().Throw<Exception>();

            File.Delete(path);
            connection.Open();
            connection.ExecuteNonQuery("CREATE TABLE recovered(value INTEGER);");
            connection.Close();
            connection.Open();
            connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'recovered';").Should().Be(1);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void ConcurrentRentReturnAndClearKeepsConnectionsUsable()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var setup = Open(path))
                setup.ExecuteNonQuery("CREATE TABLE data(value INTEGER); INSERT INTO data VALUES (1);");

            using var poolIdentity = new SqliteConnection(
                $"Data Source={path};Pooling=True;Local Provider=Managed");
            var workers = Enumerable.Range(0, 8)
                .Select(_ => Task.Run(() =>
                {
                    for (var iteration = 0; iteration < 25; iteration++)
                    {
                        using var connection = Open(path);
                        connection.ExecuteScalar<long>("SELECT value FROM data;").Should().Be(1);
                    }
                }))
                .ToArray();
            var clearer = Task.Run(() =>
            {
                for (var iteration = 0; iteration < 50; iteration++)
                {
                    SqliteConnection.ClearPool(poolIdentity);
                    if ((iteration & 7) == 0)
                        SqliteConnection.ClearAllPools();
                }
            });

            Task.WaitAll([.. workers, clearer]);

            using var final = Open(path);
            final.ExecuteScalar<long>("SELECT value FROM data;").Should().Be(1);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void AhtolaConnectionOptInPoolingResetsRawTransactions()
    {
        var path = CreateDatabasePath();
        try
        {
            using var connection = new AhtolaConnection(
                $"Data Source={path};Pooling=True;Local Provider=Managed");
            connection.Open();
            connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");
            connection.ExecuteNonQuery("BEGIN;");
            connection.ExecuteNonQuery("INSERT INTO data VALUES (1);");
            connection.Close();

            connection.Open();
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM data;";
            count.ExecuteScalar().Should().Be(0L);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void RelativePoolKeyIsRetainedForOpenAndClear()
    {
        var firstDirectory = CreateDatabaseDirectory();
        var secondDirectory = CreateDatabaseDirectory();
        var originalDirectory = Directory.GetCurrentDirectory();
        var relativePath = $"relative-{Guid.NewGuid():N}.db";
        var firstPath = Path.Combine(firstDirectory, relativePath);
        var secondPath = Path.Combine(secondDirectory, relativePath);
        try
        {
            Directory.SetCurrentDirectory(firstDirectory);
            using var connection = new AhtolaConnection(
                $"Data Source={relativePath};Pooling=True;Local Provider=Managed");
            connection.Open();
            connection.ExecuteNonQuery("CREATE TABLE first_database(value INTEGER);");
            connection.Close();

            Directory.SetCurrentDirectory(secondDirectory);
            AhtolaConnection.ClearPool(connection);
            DeleteDatabase(firstPath);

            Directory.SetCurrentDirectory(firstDirectory);
            connection.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'first_database';";
                command.ExecuteScalar().Should().Be(0L);
            }
            connection.Close();

            Directory.SetCurrentDirectory(secondDirectory);
            connection.Open();
            connection.ExecuteNonQuery("CREATE TABLE second_database(value INTEGER);");
            connection.Close();

            File.Exists(secondPath).Should().BeTrue();
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            DeleteDatabase(firstPath);
            DeleteDatabase(secondPath);
            Directory.Delete(firstDirectory, recursive: true);
            Directory.Delete(secondDirectory, recursive: true);
        }
    }

    [TestCase("COMMIT")]
    [TestCase("ROLLBACK")]
    [TestCase("-- transaction completion\nCOMMIT")]
    [TestCase("/* transaction completion */ ROLLBACK")]
    public void RawTransactionControlUnregistersTrackedTransactions(string sql)
    {
        var sqlitePath = CreateDatabasePath();
        var ahtolaPath = CreateDatabasePath();
        try
        {
            using (var connection = Open(sqlitePath))
            {
                using var transaction = connection.BeginTransaction();
                connection.ExecuteNonQuery(sql);
                transaction.Connection.Should().BeNull();

                using var subsequent = connection.BeginTransaction();
                subsequent.Rollback();
                connection.Close();
            }

            using (var connection = new AhtolaConnection(
                       $"Data Source={ahtolaPath};Pooling=True;Local Provider=Managed"))
            {
                connection.Open();
                using var transaction = connection.BeginTransaction();
                connection.ExecuteNonQuery(sql);
                transaction.Connection.Should().BeNull();

                using var subsequent = connection.BeginTransaction();
                subsequent.Rollback();
                connection.Close();
            }
        }
        finally
        {
            DeleteDatabase(sqlitePath);
            DeleteDatabase(ahtolaPath);
        }
    }

    [Test]
    public void RollbackToSavepointKeepsTrackedTransactionsRegistered()
    {
        var sqlitePath = CreateDatabasePath();
        var ahtolaPath = CreateDatabasePath();
        try
        {
            using (var connection = Open(sqlitePath))
            {
                using var transaction = connection.BeginTransaction();
                transaction.Save("point");
                connection.ExecuteNonQuery("ROLLBACK TRANSACTION TO SAVEPOINT point;");
                transaction.Connection.Should().BeSameAs(connection);
                transaction.Rollback();
            }

            using (var connection = new AhtolaConnection(
                       $"Data Source={ahtolaPath};Pooling=True;Local Provider=Managed"))
            {
                connection.Open();
                using var transaction = connection.BeginTransaction();
                connection.ExecuteNonQuery("SAVEPOINT point;");
                connection.ExecuteNonQuery("ROLLBACK TRANSACTION TO SAVEPOINT point;");
                transaction.Connection.Should().BeSameAs(connection);
                transaction.Rollback();
            }
        }
        finally
        {
            DeleteDatabase(sqlitePath);
            DeleteDatabase(ahtolaPath);
        }
    }

    [Test]
    public void ReaderSnapshotsTransactionCompletionBeforeCommandMutation()
    {
        using var connection = new AhtolaConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        using (var transaction = connection.BeginTransaction())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "COMMIT;";
            using var reader = command.ExecuteReader();
            command.CommandText = "SELECT 1;";
            reader.Read().Should().BeFalse();
            transaction.Connection.Should().BeNull();
        }

        using (var transaction = connection.BeginTransaction())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT 1;";
            using var reader = command.ExecuteReader();
            command.CommandText = "COMMIT;";
            reader.Read().Should().BeTrue();
            reader.Read().Should().BeFalse();
            transaction.Connection.Should().BeSameAs(connection);
            transaction.Rollback();
        }
    }

    [Test]
    public void SqliteFacadePoolsEligibleFilesByDefault()
    {
        new SqliteConnectionStringBuilder().Pooling.Should().BeTrue();
        var path = CreateDatabasePath();
        try
        {
            using var connection = new SqliteConnection(
                $"Data Source={path};Local Provider=Managed");
            connection.Open();
            var physical = connection.ManagedConnection;
            connection.Close();
            connection.Open();

            connection.ManagedConnection.Should().BeSameAs(physical);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(
            $"Data Source={path};Pooling=True;Local Provider=Managed");
        connection.Open();
        return connection;
    }

    private static string[] ReadDatabaseNames(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA database_list;";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(1));
        return names.ToArray();
    }

    private static string EscapeSqlLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "managed-pooling-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"pool-{Guid.NewGuid():N}.db");
    }

    private static string CreateDatabaseDirectory()
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "managed-pooling-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }
}
