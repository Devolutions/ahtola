using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ahtola;
using Ahtola.Data.Sqlite;

var databasePath = Path.Combine(AppContext.BaseDirectory, $"managed-package-{Guid.NewGuid():N}.db");
using var connection = new SqliteConnection(
    $"Data Source={databasePath};Pooling=True;Local Provider=Managed");
try
{
    VerifyPublicCapabilityContract();
    connection.Open();

    using var command = connection.CreateCommand();
    command.CommandText = "SELECT 1";

    if (command.ExecuteScalar() is not 1L)
        throw new InvalidOperationException("The managed Ahtola package consumer returned an unexpected result.");

    connection.Close();
    connection.Open();
    if (command.ExecuteScalar() is not 1L)
        throw new InvalidOperationException("The managed Ahtola package pool returned an unexpected result.");

    SqliteConnection.ClearPool(connection);
    connection.Close();
    SqliteConnection.ClearAllPools();

    var options = new DbContextOptionsBuilder<ConsumerContext>()
        .UseAhtola(connection)
        .Options;
    using (var context = new ConsumerContext(options))
    {
        context.Database.EnsureCreated();
        context.Records.Add(new ConsumerRecord { Id = 1, Value = "packaged" });
        context.SaveChanges();

        if (context.Records.Single().Value != "packaged")
            throw new InvalidOperationException("The packaged Ahtola EF Core provider returned an unexpected result.");
    }

    var expectedEfMajor =
    #if NET10_0_OR_GREATER
            10;
    #else
            9;
    #endif
        if (typeof(DbContext).Assembly.GetName().Version?.Major != expectedEfMajor)
            throw new InvalidOperationException($"The managed Ahtola package consumer must run against EF Core {expectedEfMajor}.x.");

    var remoteOptions = new DbContextOptionsBuilder<ConsumerContext>()
        .UseAhtola("Data Source=libsql://example-org.Ahtola.io;Auth Token=package-test-token")
        .Options;
    using (var remoteContext = new ConsumerContext(remoteOptions))
    {
        if (remoteContext.Database.GetDbConnection() is not SqliteConnection remoteConnection
            || remoteConnection.Capabilities.Mode != AhtolaConnectionMode.RemoteHrana)
        {
            throw new InvalidOperationException(
                "The packaged UseAhtola provider did not retain the configured remote endpoint mode.");
        }
    }

    const string encryptionKey = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    const string encryptionKey128 = "000102030405060708090A0B0C0D0E0F";
    var encryptedPath = Path.Combine(Path.GetTempPath(), $"Ahtola-managed-package-{Guid.NewGuid():N}.db");
    try
    {
        var encryptedConnectionString =
            $"Data Source={encryptedPath};Local Provider=Managed;Encryption Cipher=AES256GCM;Encryption Key={encryptionKey}";
        using (var encrypted = new SqliteConnection(encryptedConnectionString))
        {
            encrypted.Open();
            encrypted.ExecuteNonQuery("CREATE TABLE encrypted_data(value TEXT); INSERT INTO encrypted_data VALUES ('package');");
        }

        using (var reopened = new SqliteConnection(encryptedConnectionString))
        {
            reopened.Open();
            if (reopened.ExecuteScalar<string>("SELECT value FROM encrypted_data;") != "package")
                throw new InvalidOperationException("The managed package did not reopen its encrypted database.");
        }

        using var unsupported = new SqliteConnection(
            $"Data Source={encryptedPath};Local Provider=Managed;Encryption Cipher=chacha20poly1305;Encryption Key={encryptionKey}");
        try
        {
            unsupported.Open();
            throw new InvalidOperationException("The managed package accepted a cipher with no on-disk cipher id.");
        }
        catch (NotSupportedException exception) when (
            exception.Message.Contains("cipher IDs 1 through 8", StringComparison.Ordinal))
        {
        }
    }
    finally
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            File.Delete(encryptedPath + suffix);
    }

    // Every Turso format version 0 cipher must round-trip from the packed
    // package, including under NativeAOT and trimming: the AEGIS ciphers are
    // implemented in managed code, so this is where a reflection-free, ILC-safe
    // implementation proves itself outside the test host.
    foreach (var (cipherName, cipherKey, expectedReservedBytes, expectedCipherId) in new[]
             {
                 ("AES128GCM", encryptionKey128, (byte)28, (byte)1),
                 ("AES256GCM", encryptionKey, (byte)28, (byte)2),
                 ("AEGIS256", encryptionKey, (byte)48, (byte)3),
                 ("AEGIS256X2", encryptionKey, (byte)48, (byte)4),
                 ("AEGIS256X4", encryptionKey, (byte)48, (byte)5),
                 ("AEGIS128L", encryptionKey128, (byte)32, (byte)6),
                 ("AEGIS128X2", encryptionKey128, (byte)32, (byte)7),
                 ("AEGIS128X4", encryptionKey128, (byte)32, (byte)8),
             })
    {
        var cipherPath = Path.Combine(Path.GetTempPath(), $"Ahtola-managed-package-{cipherName}-{Guid.NewGuid():N}.db");
        try
        {
            var connectionString =
                $"Data Source={cipherPath};Local Provider=Managed;Encryption Cipher={cipherName};Encryption Key={cipherKey}";
            using (var cipherConnection = new SqliteConnection(connectionString))
            {
                cipherConnection.Open();
                cipherConnection.ExecuteNonQuery("CREATE TABLE payload(value TEXT); INSERT INTO payload VALUES ('aegis');");
            }

            var header = new byte[21];
            using (var stream = File.OpenRead(cipherPath))
                stream.ReadExactly(header);

            if (System.Text.Encoding.ASCII.GetString(header, 0, 5) != "AHTLA" || header[5] != 0)
                throw new InvalidOperationException($"{cipherName} did not produce an AHTLA format version 0 header.");
            if (header[6] != expectedCipherId)
                throw new InvalidOperationException($"{cipherName} wrote cipher id {header[6]}, expected {expectedCipherId}.");
            if (header[20] != expectedReservedBytes)
                throw new InvalidOperationException($"{cipherName} reserved {header[20]} bytes, expected {expectedReservedBytes}.");

            using (var reopened = new SqliteConnection(connectionString))
            {
                reopened.Open();
                if (reopened.ExecuteScalar<string>("SELECT value FROM payload;") != "aegis")
                    throw new InvalidOperationException($"The managed package did not reopen its {cipherName} database.");
            }
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
                File.Delete(cipherPath + suffix);
        }
    }


    if (AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
            string.Equals(assembly.GetName().Name, "Turso.Raw", StringComparison.Ordinal)))
    {
        throw new InvalidOperationException("The managed Ahtola package consumer must not load Turso.Raw.");
    }

    EnsureNoNativeCompanionWasRestored();
    VerifyTrimSensitiveSurfaces();
    await VerifyEntityFrameworkIntegrationAsync(connection);
    VerifySourceFreeReplicaOptions();
    await VerifyOptionalCloudRuntimeAsync();

    Console.WriteLine(
        $"Managed package consumer succeeded on {AppContext.TargetFrameworkName} with EF Core {typeof(DbContext).Assembly.GetName().Version}.");
}
finally
{
    connection.Close();
    SqliteConnection.ClearAllPools();
    DeleteDatabase(databasePath);
}
// Trim/AOT-sensitive ADO surfaces. These are here so the packed consumer's trimmed and NativeAOT
// publishes actually root them: the schema table, the annotated GetFieldType contract, the tuple
// accumulator (Activator.CreateInstance over a ValueTuple), and the fail-closed native provider.
static void VerifyTrimSensitiveSurfaces()
{
    using var connection = new SqliteConnection("Data Source=:memory:;Mode=Memory");
    connection.Open();

    using (var seed = connection.CreateCommand())
    {
        seed.CommandText =
            "CREATE TABLE probe(id INTEGER PRIMARY KEY, value INTEGER NOT NULL, label TEXT);"
            + "INSERT INTO probe(value, label) VALUES (10, 'a'), (20, 'b'), (30, 'c');";
        seed.ExecuteNonQuery();
    }

    using (var command = connection.CreateCommand())
    {
        command.CommandText = "SELECT id, value, label FROM probe;";
        using DbDataReader reader = command.ExecuteReader();

        DataTable schema = reader.GetSchemaTable()
            ?? throw new InvalidOperationException("The managed package reader produced no schema table.");
        if (schema.Rows.Count != 3)
            throw new InvalidOperationException("The managed package schema table lost a column.");
        foreach (DataRow row in schema.Rows)
        {
            if (row[SchemaTableColumn.DataType] is not Type)
                throw new InvalidOperationException("The managed package schema table lost its CLR types.");
        }

        if (!reader.Read() || reader.GetFieldType(0) != typeof(long) || reader.GetFieldType(2) != typeof(string))
            throw new InvalidOperationException("The managed package reader reported unexpected field types.");
    }

    // Same accumulator shape EF Core's ef_avg uses: (decimal sum, ulong count). The accumulator
    // round-trips through the engine as text, so the tuple is reconstructed on every step.
    connection.CreateAggregate<decimal, (decimal Sum, ulong Count), decimal?>(
        "probe_avg",
        (Sum: 0m, Count: 0UL),
        static (accumulator, value) => (accumulator.Sum + value, accumulator.Count + 1),
        static accumulator => accumulator.Count == 0 ? null : accumulator.Sum / accumulator.Count);

    using (var command = connection.CreateCommand())
    {
        command.CommandText = "SELECT probe_avg(value) FROM probe;";
        if (Convert.ToDecimal(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 20m)
            throw new InvalidOperationException("The managed package tuple aggregate returned an unexpected result.");
    }

    // No native companion is shipped, and nothing probes for one by assembly name.
    try
    {
        using var native = new SqliteConnection("Data Source=:memory:;Mode=Memory;Local Provider=Native");
        native.Open();
        throw new InvalidOperationException("Local Provider=Native must fail closed without the native companion.");
    }
    catch (NotSupportedException)
    {
    }
}

static async Task VerifyEntityFrameworkIntegrationAsync(SqliteConnection connection)
{
    var options = new DbContextOptionsBuilder<ManagedPackageContext>()
        .UseAhtola(connection)
        .Options;

    await using var context = new ManagedPackageContext(options);
    await context.Database.EnsureCreatedAsync();
    context.Records.Add(new ManagedPackageRecord { Value = "entity-framework" });
    await context.SaveChangesAsync();

    var value = await context.Records.SingleAsync(record => record.Value == "entity-framework");
    if (value.Value != "entity-framework")
        throw new InvalidOperationException("The managed Entity Framework package consumer returned an unexpected result.");
}

static void EnsureNoNativeCompanionWasRestored()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        var assetsPath = Path.Combine(directory.FullName, "obj", "project.assets.json");
        if (!File.Exists(assetsPath))
            continue;

        using var assetsStream = File.OpenRead(assetsPath);
        using var assets = JsonDocument.Parse(assetsStream);
        var nativePackage = assets.RootElement
            .GetProperty("libraries")
            .EnumerateObject()
            .Select(library => library.Name)
            .FirstOrDefault(IsNativeCompanionPackage);
        if (nativePackage is not null)
        {
            throw new InvalidOperationException(
                $"The managed Ahtola package consumer must not restore native companion package {nativePackage}.");
        }

        return;
    }

    throw new FileNotFoundException("Could not locate the managed consumer restore graph.");
}

static void VerifySourceFreeReplicaOptions()
{
    var options = new AhtolaReplicaOptions(
        "replica.db",
        new Uri("https://example.Ahtola.io"),
        authToken: null)
    {
        LongPollTimeout = TimeSpan.FromSeconds(15),
        PartialBootstrap = AhtolaPartialBootstrapOptions.Prefix(64 * 1024),
        PushOperationsThreshold = 1000,
        PullBytesThreshold = 1024 * 1024,
    };
    using var replica = AhtolaConnection.CreateReplica(options);
    if (!replica.Capabilities.SupportsSync)
        throw new InvalidOperationException("The managed package did not expose managed embedded replica synchronization.");
}

static async Task VerifyOptionalCloudRuntimeAsync()
{
    if (!string.Equals(
            Environment.GetEnvironmentVariable("AHTOLA_RUN_CLOUD_SMOKE"),
            "1",
            StringComparison.Ordinal))
    {
        return;
    }

    var remoteUrl = Environment.GetEnvironmentVariable("TURSO_REMOTE_URL");
    var authToken = Environment.GetEnvironmentVariable("TURSO_AUTH_TOKEN");
    if (string.IsNullOrWhiteSpace(remoteUrl) || string.IsNullOrWhiteSpace(authToken))
    {
        throw new InvalidOperationException(
            "The opt-in cloud smoke requires TURSO_REMOTE_URL and TURSO_AUTH_TOKEN.");
    }

    var replicaPath = Path.GetFullPath($"managed-package-cloud-replica-{Guid.NewGuid():N}.db");
    var tableName = $"ahtola_package_smoke_{Guid.NewGuid():N}";
    var stage = "direct remote query";
    try
    {
        var directConnectionString = new AhtolaConnectionStringBuilder
        {
            DataSource = remoteUrl,
            AuthToken = authToken,
            ReadYourWrites = false,
        }.ConnectionString;

        using (var direct = new AhtolaConnection(directConnectionString))
        {
            await direct.OpenAsync(CancellationToken.None);
            if (await ExecuteOneAsync(direct) != 1)
                throw new InvalidOperationException("Direct remote query returned an unexpected result.");
        }

        var replicaOptions = new AhtolaReplicaOptions(
            replicaPath,
            new Uri(remoteUrl, UriKind.Absolute),
            authToken,
            bootstrapIfEmpty: true);
        using (var replica = AhtolaConnection.CreateReplica(replicaOptions))
        {
            stage = "embedded replica open";
            await replica.OpenAsync(CancellationToken.None);
            stage = "embedded replica query";
            if (await ExecuteOneAsync(replica) != 1)
                throw new InvalidOperationException("Embedded replica query returned an unexpected result.");

            replica.ExecuteNonQuery($"CREATE TABLE {tableName}(value INTEGER NOT NULL);");
            replica.ExecuteNonQuery($"INSERT INTO {tableName} VALUES (1);");
            stage = "embedded replica sync";
            _ = await replica.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
        }

        stage = "remote cleanup";
        using (var cleanup = new AhtolaConnection(directConnectionString))
        {
            await cleanup.OpenAsync(CancellationToken.None);
            cleanup.ExecuteNonQuery($"DROP TABLE {tableName};");
        }

        Console.WriteLine("Opt-in cloud smoke succeeded.");
    }
    catch (Exception exception)
    {
        // Do not surface remote URLs, bearer tokens, or server response bodies in CI logs.
        throw new InvalidOperationException(
        $"The opt-in cloud smoke failed during {stage} ({exception.GetType().Name}).");
    }
    finally
    {
        DeleteReplicaFiles(replicaPath);
    }
}

static async Task<long> ExecuteOneAsync(AhtolaConnection connection)
{
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT 1;";
    return Convert.ToInt64(
        await command.ExecuteScalarAsync(CancellationToken.None),
        System.Globalization.CultureInfo.InvariantCulture);
}

static void DeleteReplicaFiles(string path)
{
    foreach (var suffix in new[]
             {
                 string.Empty,
                 "-wal",
                 "-shm",
                 "-journal",
                 ".ahtola-replica-meta",
                 ".ahtola-replica-journal",
             })
    {
        File.Delete(path + suffix);
    }
}

static void VerifyPublicCapabilityContract()
{
    using var ahtolaManaged = new AhtolaConnection("Data Source=:memory:;Local Provider=Managed");
    AssertCapabilities(
        ahtolaManaged.Capabilities,
        ahtolaManaged.CanCreateBatch,
        AhtolaConnectionFacade.AhtolaData,
        AhtolaConnectionMode.ManagedLocal,
        [true, true, true, true, false, false, false, false, false, false, true, true, false]);

    using var ahtolaNative = new AhtolaConnection("Data Source=:memory:;Local Provider=Native");
    AssertCapabilities(
        ahtolaNative.Capabilities,
        ahtolaNative.CanCreateBatch,
        AhtolaConnectionFacade.AhtolaData,
        AhtolaConnectionMode.NativeLocal,
        [true, true, true, true, false, false, false, false, false, false, true, false, false]);

    using var ahtolaRemote = new AhtolaConnection("Data Source=https://example.Ahtola.io");
    AssertCapabilities(
        ahtolaRemote.Capabilities,
        ahtolaRemote.CanCreateBatch,
        AhtolaConnectionFacade.AhtolaData,
        AhtolaConnectionMode.RemoteHrana,
        [true, true, true, true, false, false, false, false, false, false, false, false, false]);

    using var ahtolaReplica = new AhtolaConnection(
        "Data Source=https://example.Ahtola.io;Replica Path=replica.db");
    AssertCapabilities(
        ahtolaReplica.Capabilities,
        ahtolaReplica.CanCreateBatch,
        AhtolaConnectionFacade.AhtolaData,
        AhtolaConnectionMode.EmbeddedReplica,
        [true, true, true, true, false, false, false, false, false, false, false, false, true]);

    using var sqliteManaged = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
    AssertCapabilities(
        sqliteManaged.Capabilities,
        sqliteManaged.CanCreateBatch,
        AhtolaConnectionFacade.Sqlite,
        AhtolaConnectionMode.ManagedLocal,
        [true, true, true, true, true, true, true, true, true, false, true, true, false]);

    using var sqliteNative = new SqliteConnection("Data Source=:memory:;Local Provider=Native");
    AssertCapabilities(
        sqliteNative.Capabilities,
        sqliteNative.CanCreateBatch,
        AhtolaConnectionFacade.Sqlite,
        AhtolaConnectionMode.NativeLocal,
        [true, true, true, true, true, true, true, true, true, true, true, false, false]);

    using var sqliteRemote = new SqliteConnection("Data Source=https://example.Ahtola.io");
    AssertCapabilities(
        sqliteRemote.Capabilities,
        sqliteRemote.CanCreateBatch,
        AhtolaConnectionFacade.Sqlite,
        AhtolaConnectionMode.RemoteHrana,
        [true, true, true, true, false, false, false, false, false, false, false, false, false]);

    using var sqliteReplica = new SqliteConnection(
        "Data Source=https://example.Ahtola.io;Replica Path=replica.db");
    AssertCapabilities(
        sqliteReplica.Capabilities,
        sqliteReplica.CanCreateBatch,
        AhtolaConnectionFacade.Sqlite,
        AhtolaConnectionMode.EmbeddedReplica,
        [true, true, true, true, false, false, false, false, false, false, false, false, true]);
}

static void AssertCapabilities(
    AhtolaConnectionCapabilities capabilities,
    bool canCreateBatch,
    AhtolaConnectionFacade facade,
    AhtolaConnectionMode mode,
    bool[] expected)
{
    var actual = new[]
    {
        capabilities.CanCreateBatch,
        capabilities.SupportsAsyncOperations,
        capabilities.SupportsTransactions,
        capabilities.SupportsSavepoints,
        capabilities.SupportsBackup,
        capabilities.SupportsIncrementalBlob,
        capabilities.SupportsUserDefinedFunctions,
        capabilities.SupportsUserDefinedAggregates,
        capabilities.SupportsCustomCollations,
        capabilities.SupportsExtensions,
        capabilities.SupportsAttach,
        capabilities.SupportsPooling,
        capabilities.SupportsSync,
    };
    if (capabilities.Facade != facade
        || capabilities.Mode != mode
        || canCreateBatch != capabilities.CanCreateBatch
        || !actual.SequenceEqual(expected))
    {
        throw new InvalidOperationException(
            $"Unexpected packaged capability contract for {facade}/{mode}.");
    }
}

static bool IsNativeCompanionPackage(string packageIdentity)
    => packageIdentity.StartsWith("Turso.Raw/", StringComparison.OrdinalIgnoreCase) ||
       packageIdentity.StartsWith("Turso.Data.Native/", StringComparison.OrdinalIgnoreCase) ||
       packageIdentity.StartsWith("Turso.Data.Sync/", StringComparison.OrdinalIgnoreCase) ||
       packageIdentity.StartsWith("Turso.Data.Sqlite.Native/", StringComparison.OrdinalIgnoreCase) ||
       packageIdentity.StartsWith("Turso.Data.Sqlite.NativeAot", StringComparison.OrdinalIgnoreCase) ||
       packageIdentity.StartsWith("Turso.Data.Sqlite.Sync/", StringComparison.OrdinalIgnoreCase);

static void DeleteDatabase(string path)
{
    foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
    {
        var candidate = path + suffix;
        if (File.Exists(candidate))
            File.Delete(candidate);
    }
}

sealed class ConsumerContext(DbContextOptions<ConsumerContext> options) : DbContext(options)
{
    public DbSet<ConsumerRecord> Records => Set<ConsumerRecord>();
}

sealed class ConsumerRecord
{
    public int Id { get; set; }

    public required string Value { get; set; }
}

sealed class ManagedPackageContext(DbContextOptions<ManagedPackageContext> options) : DbContext(options)
{
    public DbSet<ManagedPackageRecord> Records => Set<ManagedPackageRecord>();
}

sealed class ManagedPackageRecord
{
    public int Id { get; init; }

    public required string Value { get; init; }
}
