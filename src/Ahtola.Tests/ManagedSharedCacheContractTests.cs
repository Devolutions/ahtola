using System.Data;
using AwesomeAssertions;
using Ahtola.Data.Sqlite;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedSharedCacheContractTests
{
    private const string ReadUncommittedNotSupportedMessage =
        "PRAGMA read_uncommitted and IsolationLevel.ReadUncommitted are not supported for managed shared-memory databases because the managed engine preserves transaction isolation and does not expose dirty reads.";

    private const string HooksNotSupportedMessage =
            "Managed shared-memory databases do not support update, commit, rollback, authorizer, trace, or progress callbacks because the managed catalog is shared across connections.";

    [SetUp]
    public void SetUp() => SqliteConnection.ClearAllPools();

    [TearDown]
    public void TearDown() => SqliteConnection.ClearAllPools();

    [Test]
    public void NamedMemorySharesCatalogUntilTheLastLogicalConnectionCloses()
    {
        var connectionString = CreateConnectionString();
        using var first = new SqliteConnection(connectionString);
        using var second = new SqliteConnection(connectionString);
        first.Open();
        var firstPhysicalConnection = first.ManagedConnection;
        first.ExecuteNonQuery("CREATE TABLE data(value INTEGER); INSERT INTO data VALUES (42);");

        second.Open();
        second.ExecuteScalar<long>("SELECT value FROM data;").Should().Be(42);

        first.Close();
        SqliteConnection.ClearAllPools();
        second.ExecuteScalar<long>("SELECT value FROM data;").Should().Be(42);

        first.Open();
        first.ManagedConnection.Should().NotBeSameAs(firstPhysicalConnection);
        first.ExecuteScalar<long>("SELECT value FROM data;").Should().Be(42);

        first.Close();
        second.Close();

        using var replacement = new SqliteConnection(connectionString);
        replacement.Open();
        CountTable(replacement, "data").Should().Be(0);
    }

    [Test]
    public void AhtolaAndSqliteConnectionsShareTheSameNamedMemoryCatalog()
    {
        var name = "managed-cross-surface-" + Guid.NewGuid().ToString("N");
        var connectionString =
            $"Data Source={name};Mode=Memory;Cache=Shared;Pooling=False;Local Provider=Managed";
        using var connection = new AhtolaConnection(connectionString);
        using var sqlite = new SqliteConnection(connectionString);

        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");
        connection.ExecuteNonQuery("INSERT INTO data VALUES (7);");

        sqlite.Open();
        sqlite.ExecuteScalar<long>("SELECT value FROM data;").Should().Be(7);

        connection.Close();
        sqlite.ExecuteScalar<long>("SELECT value FROM data;").Should().Be(7);
    }

    [Test]
    public void PoolingTrueIsFacadeCompatibilityOnlyForSharedMemory()
    {
        var name = "managed-shared-pooling-" + Guid.NewGuid().ToString("N");
        var connectionString =
            $"Data Source={name};Mode=Memory;Cache=Shared;Pooling=True;Local Provider=Managed";
        using (var sqlite = new SqliteConnection(connectionString))
        {
            sqlite.Open();
            sqlite.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");
        }

        using (var replacement = new SqliteConnection(connectionString))
        {
            replacement.Open();
            CountTable(replacement, "data").Should().Be(0);
        }

        using var connection = new AhtolaConnection(connectionString);
        connection.Invoking(static connection => connection.Open())
            .Should().Throw<NotSupportedException>()
            .WithMessage("Pooling=True is supported only for unencrypted managed local file databases.");
    }

    [Test]
    public void PrivateNamedMemoryConnectionsRemainIsolated()
    {
        var name = "managed-private-memory-" + Guid.NewGuid().ToString("N");
        var connectionString =
            $"Data Source={name};Mode=Memory;Cache=Private;Pooling=True;Local Provider=Managed";
        using var first = new SqliteConnection(connectionString);
        using var second = new SqliteConnection(connectionString);
        first.Open();
        second.Open();

        first.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");

        CountTable(first, "data").Should().Be(1);
        CountTable(second, "data").Should().Be(0);
    }

    [Test]
    public void SharedMemoryNamesAreCaseSensitive()
    {
        var name = "managed-case-sensitive-" + Guid.NewGuid().ToString("N");
        using var lower = new SqliteConnection(
            $"Data Source={name};Mode=Memory;Cache=Shared;Local Provider=Managed");
        using var upper = new SqliteConnection(
            $"Data Source={name.ToUpperInvariant()};Mode=Memory;Cache=Shared;Local Provider=Managed");
        lower.Open();
        upper.Open();

        lower.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");

        CountTable(lower, "data").Should().Be(1);
        CountTable(upper, "data").Should().Be(0);
    }

    [Test]
    public async Task ConcurrentConnectionsSerializeSharedCatalogMutations()
    {
        var connectionString = CreateConnectionString();
        using var anchor = new SqliteConnection(connectionString);
        anchor.Open();
        anchor.ExecuteNonQuery("CREATE TABLE data(value INTEGER PRIMARY KEY);");

        var workers = Enumerable.Range(0, 4).Select(worker => Task.Run(async () =>
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            for (var index = 0; index < 25; index++)
            {
                await ExecuteNonQueryAsync(
                    connection,
                    $"INSERT INTO data VALUES ({worker * 25 + index});");
            }
        }));

        await Task.WhenAll(workers);

        anchor.ExecuteScalar<long>("SELECT COUNT(*) FROM data;").Should().Be(100);
    }

    [Test]
    public void SharedMemoryPreservesIsolationAndRejectsDirtyReadRequests()
    {
        var connectionString = CreateConnectionString();
        using var writer = new SqliteConnection(connectionString);
        using var reader = new SqliteConnection(connectionString);
        writer.Open();
        reader.Open();
        writer.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");

        using var transaction = writer.BeginTransaction();
        writer.ExecuteNonQuery("INSERT INTO data VALUES (1);");
        reader.ExecuteScalar<long>("SELECT COUNT(*) FROM data;").Should().Be(0);

        reader.Invoking(static connection => connection.ExecuteNonQuery("PRAGMA read_uncommitted = 1;"))
            .Should()
            .Throw<NotSupportedException>()
            .WithMessage(ReadUncommittedNotSupportedMessage);
        reader.ExecuteNonQuery("PRAGMA read_uncommitted = 0;");
        reader.ExecuteScalar<long>("PRAGMA read_uncommitted;").Should().Be(0);
        reader.Invoking(static connection => connection.BeginTransaction(IsolationLevel.ReadUncommitted))
            .Should()
            .Throw<NotSupportedException>()
            .WithMessage(ReadUncommittedNotSupportedMessage);
        reader.Invoking(static connection => connection.BeginTransaction(IsolationLevel.ReadUncommitted, deferred: false))
            .Should()
            .Throw<NotSupportedException>()
            .WithMessage(ReadUncommittedNotSupportedMessage);
        reader.Transaction.Should().BeNull();

        transaction.Commit();
        reader.ExecuteScalar<long>("SELECT COUNT(*) FROM data;").Should().Be(1);
    }

    [Test]
    public void AhtolaConnectionRejectsDirtyReadRequestsForSharedMemory()
    {
        using var connection = new AhtolaConnection(CreateConnectionString());
        connection.Open();

        connection.Invoking(static value => value.ExecuteNonQuery("PRAGMA read_uncommitted(ON);"))
            .Should()
            .Throw<NotSupportedException>()
            .WithMessage(ReadUncommittedNotSupportedMessage);
        ReadAhtolaInt64(connection, "PRAGMA read_uncommitted;").Should().Be(0);
        connection.Invoking(static value => value.BeginTransaction(IsolationLevel.ReadUncommitted))
            .Should()
            .Throw<NotSupportedException>()
            .WithMessage(ReadUncommittedNotSupportedMessage);
    }

    [TestCase("0x1")]
    [TestCase("1.0")]
    [TestCase("2e0")]
    [TestCase("1e999")]
    [TestCase("+1")]
    [TestCase("257")]
    [TestCase("0x101")]
    [TestCase("'1'")]
    public void ManagedSharedMemoryRejectsNumericDirtyReadPragmaForms(string value)
    {
        var connectionString = CreateConnectionString();
        using var sqlite = new SqliteConnection(connectionString);
        using var connection = new AhtolaConnection(connectionString);
        sqlite.Open();
        connection.Open();

        sqlite.Invoking(connection => connection.ExecuteNonQuery($"PRAGMA read_uncommitted = {value};"))
            .Should()
            .Throw<NotSupportedException>()
            .WithMessage(ReadUncommittedNotSupportedMessage);
        connection.Invoking(connection => connection.ExecuteNonQuery($"PRAGMA read_uncommitted = {value};"))
            .Should()
            .Throw<NotSupportedException>()
            .WithMessage(ReadUncommittedNotSupportedMessage);
    }

    [TestCase("-1")]
    [TestCase("-0x1")]
    [TestCase("256")]
    [TestCase("0x100")]
    [TestCase("2147483648")]
    [TestCase("0xffffffff")]
    [TestCase("'+1'")]
    public void ManagedSharedMemoryAcceptsNumericPragmaFormsThatSQLiteTreatsAsDisabled(string value)
    {
        var connectionString = CreateConnectionString();
        using var sqlite = new SqliteConnection(connectionString);
        using var connection = new AhtolaConnection(connectionString);
        sqlite.Open();
        connection.Open();

        sqlite.ExecuteNonQuery($"PRAGMA read_uncommitted = {value};");
        connection.ExecuteNonQuery($"PRAGMA read_uncommitted = {value};");

        sqlite.ExecuteScalar<long>("PRAGMA read_uncommitted;").Should().Be(0);
        ReadAhtolaInt64(connection, "PRAGMA read_uncommitted;").Should().Be(0);
    }

    [Test]
    public void ManagedSharedMemoryRejectsEncryption()
    {
        var key = Convert.ToHexString(new byte[32]);
        var connectionString = CreateConnectionString()
                               + $";Encryption Cipher=Aes256Gcm;Encryption Key={key}";
        using var sqlite = new SqliteConnection(connectionString);
        using var connection = new AhtolaConnection(connectionString);

        sqlite.Invoking(static connection => connection.Open())
            .Should()
            .Throw<NotSupportedException>()
            .WithMessage("Encryption is supported only for file-backed databases when Local Provider=Managed.");
        connection.Invoking(static connection => connection.Open())
            .Should()
            .Throw<NotSupportedException>()
            .WithMessage("Encryption is supported only for file-backed databases when Local Provider=Managed.");
    }

    // File-backed Cache=Shared is accepted: the managed engine cannot emulate SQLite
    // shared-cache semantics (cross-connection dirty reads, table locks) for file
    // databases, so it opens each connection as an ordinary private file connection
    // with strictly stronger isolation instead of rejecting the keyword outright.
    [TestCase("Data Source={0};Cache=Shared")]
    [TestCase("Data Source={0};Mode=ReadWriteCreate;Cache=Shared")]
    public void ManagedProviderOpensFileBackedSharedCacheAsPrivateFileConnection(string options)
    {
        var path = Path.Combine(Path.GetTempPath(), "Ahtola-shared-cache-" + Guid.NewGuid().ToString("N") + ".db")
            .Replace('\\', '/');
        try
        {
            var connectionString = string.Format(options + ";Local Provider=Managed", path);
            using var sqlite = new SqliteConnection(connectionString);
            using var connection = new AhtolaConnection(connectionString);
            using var second = new SqliteConnection(connectionString);

            sqlite.Open();
            sqlite.ExecuteNonQuery("CREATE TABLE data(value INTEGER); INSERT INTO data VALUES (42);");

            // Both facades and every parallel open see one durable file, each through
            // its own private connection.
            connection.Open();
            ReadAhtolaInt64(connection, "SELECT value FROM data;").Should().Be(42);
            second.Open();
            second.ExecuteScalar<long>("SELECT value FROM data;").Should().Be(42);

            // Private connections keep connection-local callbacks available.
            second.CreateFunction("local_answer", static () => 42L);
            second.ExecuteScalar<long>("SELECT local_answer();").Should().Be(42);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(path);
        }
    }

    // PSSqlite's default connection string is 'Data Source=:memory:;Cache=Shared'. Real
    // SQLite gives each open of an anonymous :memory: database its own private cache
    // unless Mode=Memory routes the open through the shared-cache URI form, so the
    // managed provider must accept these shapes and behave exactly like a plain
    // private :memory: connection.
    [TestCase("Data Source=:memory:;Cache=Shared")]
    [TestCase("Mode=Memory;Cache=Shared")]
    public void AnonymousSharedCacheMemoryOpensAsPrivateInMemory(string options)
    {
        var connectionString = options + ";Local Provider=Managed";
        using var first = new SqliteConnection(connectionString);
        using var second = new SqliteConnection(connectionString);
        using var connection = new AhtolaConnection(connectionString);

        first.Open();
        second.Open();
        connection.Open();

        first.ExecuteNonQuery("CREATE TABLE data(value INTEGER); INSERT INTO data VALUES (42);");

        first.ExecuteScalar<long>("SELECT value FROM data;").Should().Be(42);
        CountTable(second, "data").Should().Be(0);
        CountAhtolaTable(connection, "data").Should().Be(0);

        // Anonymous shared caches keep a private catalog, so connection-local callbacks
        // remain available, unlike on named shared-memory databases.
        first.CreateFunction("local_answer", static () => 42L);
        first.ExecuteScalar<long>("SELECT local_answer();").Should().Be(42);

        first.Close();
        first.Open();
        CountTable(first, "data").Should().Be(0);
    }

    // Microsoft.Data.Sqlite rewrites Mode=Memory + Cache=Shared into the shared-cache
    // URI form (file:NAME?mode=memory&cache=shared), which turns a literal ':memory:'
    // Data Source into a named in-memory database shared by every connection in the
    // process. The managed provider mirrors that routing.
    [Test]
    public void ColonMemoryWithMemoryModeAndSharedCacheSharesOneCatalog()
    {
        const string connectionString =
            "Data Source=:memory:;Mode=Memory;Cache=Shared;Pooling=False;Local Provider=Managed";
        using var first = new SqliteConnection(connectionString);
        using var second = new SqliteConnection(connectionString);
        using var connection = new AhtolaConnection(connectionString);
        first.Open();
        second.Open();
        connection.Open();

        first.ExecuteNonQuery("CREATE TABLE data(value INTEGER); INSERT INTO data VALUES (7);");

        second.ExecuteScalar<long>("SELECT value FROM data;").Should().Be(7);
        ReadAhtolaInt64(connection, "SELECT value FROM data;").Should().Be(7);

        first.Close();
        second.Close();
        connection.Close();

        using var replacement = new SqliteConnection(connectionString);
        replacement.Open();
        CountTable(replacement, "data").Should().Be(0);
    }

    // Oracle that pins the SQLite semantics the managed acceptance above mirrors.
    [TestCase("Data Source=:memory:;Cache=Shared", false)]
    [TestCase("Mode=Memory;Cache=Shared", false)]
    [TestCase("Data Source=:memory:;Mode=Memory;Cache=Shared", true)]
    public void NativeSqliteRoutesAnonymousSharedCacheMemoryLikeTheManagedProvider(
        string options,
        bool shared)
    {
        using var first = new MsData.SqliteConnection(options);
        using var second = new MsData.SqliteConnection(options);
        first.Open();
        second.Open();

        using (var create = first.CreateCommand())
        {
            create.CommandText = "CREATE TABLE data(value INTEGER);";
            create.ExecuteNonQuery();
        }

        using var probe = second.CreateCommand();
        probe.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'data';";
        Convert.ToInt64(probe.ExecuteScalar()).Should().Be(shared ? 1 : 0);
    }

    [Test]
        public void ManagedSharedMemorySharesCatalogScopedCallbacksAcrossConnections()
    {
            var connectionString = CreateConnectionString();
            using var first = new SqliteConnection(connectionString);
            // EF-style: register before Open, then open additional leases.
            first.CreateFunction("local_value", static () => 41L);
            first.CreateCollation("local_collation", StringComparer.OrdinalIgnoreCase.Compare);
            first.CreateAggregate<long, long>(
                "local_sum",
                seed: 0L,
                (acc, value) => acc + value,
                isDeterministic: true);
            first.Open();
            first.ExecuteNonQuery("CREATE TABLE data(name TEXT COLLATE local_collation, value INTEGER);");
            first.ExecuteNonQuery("INSERT INTO data VALUES ('Alpha', 1), ('beta', 2);");
            first.ExecuteScalar<long>("SELECT local_value();").Should().Be(41);
            first.ExecuteScalar<long>("SELECT local_sum(value) FROM data;").Should().Be(3);
            first.ExecuteScalar<long>("SELECT COUNT(*) FROM data WHERE name = 'alpha';").Should().Be(1);

            using var second = new SqliteConnection(connectionString);
            // Second EF connection registers the same catalog-scoped callbacks and still sees data.
            second.CreateFunction("local_value", static () => 41L);
            second.CreateCollation("local_collation", StringComparer.OrdinalIgnoreCase.Compare);
            second.CreateAggregate<long, long>(
                "local_sum",
                seed: 0L,
                (acc, value) => acc + value,
                isDeterministic: true);
            second.Open();
            second.ExecuteScalar<long>("SELECT local_value();").Should().Be(41);
            second.ExecuteScalar<long>("SELECT local_sum(value) FROM data;").Should().Be(3);
            second.ExecuteScalar<long>("SELECT COUNT(*) FROM data WHERE name = 'ALPHA';").Should().Be(1);
        }

        [Test]
        public void ManagedSharedMemoryRejectsHooks()
        {
            using var connection = new SqliteConnection(CreateConnectionString());
            connection.Open();
            connection.Invoking(static c => c.SetUpdateHook(_ => { }))
                .Should()
                .Throw<NotSupportedException>()
                .WithMessage(HooksNotSupportedMessage);
        }

    [Test]
    public async Task CanceledOpenDoesNotAcquireSharedMemoryLifetime()
    {
        var connectionString = CreateConnectionString();
        using var canceled = new SqliteConnection(connectionString);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await canceled.Awaiting(connection => connection.OpenAsync(cancellation.Token))
            .Should()
            .ThrowAsync<OperationCanceledException>();
        canceled.State.Should().Be(ConnectionState.Closed);

        using (var writer = new SqliteConnection(connectionString))
        {
            await writer.OpenAsync();
            await ExecuteNonQueryAsync(writer, "CREATE TABLE data(value INTEGER);");
        }

        using var replacement = new SqliteConnection(connectionString);
        replacement.Open();
        CountTable(replacement, "data").Should().Be(0);
    }

    private static string CreateConnectionString()
    {
        var name = "managed-shared-memory-" + Guid.NewGuid().ToString("N");
        return $"Data Source={name};Mode=Memory;Cache=Shared;Pooling=False;Local Provider=Managed";
    }

    private static long CountTable(SqliteConnection connection, string name)
        => connection.ExecuteScalar<long>(
            $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{name}';");

    private static long CountAhtolaTable(AhtolaConnection connection, string name)
        => ReadAhtolaInt64(
            connection,
            $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{name}';");

    private static long ReadAhtolaInt64(AhtolaConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void DeleteDatabaseFiles(string path)
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            try
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task<int> ExecuteNonQueryAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync();
    }
}
