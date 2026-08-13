using System.Globalization;
using BenchmarkDotNet.Attributes;
using MdsConnection = Microsoft.Data.Sqlite.SqliteConnection;
using AhtolaManagedConnection = Ahtola.Data.Sqlite.SqliteConnection;

namespace ConsumerBenchmarks;

/// <summary>
/// Read-heavy consumer benchmarks modeled on real-world consumer query shapes
/// (a winget-style package search tool ("pinget") and a metadata/schema
/// inspection tool ("synedgy")). These benchmarks exist to guard against
/// *regressions* in the managed Ahtola engine's read path, not to claim
/// parity with the native engine or with Microsoft.Data.Sqlite. See the
/// README in this folder for interpretation guidance.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ConsumerReadBenchmarks
{
    private const string CatalogSearchCategory = "CatalogSearch";
    private const string MetadataSelectCategory = "MetadataSelect";
    private const string PinsReadOnlyOpenAndListCategory = "PinsReadOnlyOpenAndList";

    private const int PackageCount = 1000;

    private string _tempDirectory = null!;
    private string _catalogDbPath = null!;
    private string _pinsDbPath = null!;

    // Winget catalog search (in-memory, shared connections reused across invocations).
    private AhtolaManagedConnection _ahtolaCatalogConnection = null!;
    private MdsConnection _mdsCatalogConnection = null!;

    // Metadata SELECT (in-memory, small schema).
    private AhtolaManagedConnection _ahtolaMetadataConnection = null!;
    private MdsConnection _mdsMetadataConnection = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"Ahtola-consumer-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _catalogDbPath = Path.Combine(_tempDirectory, "catalog.db");
        _pinsDbPath = Path.Combine(_tempDirectory, "pins.db");

        // Winget catalog search setup (in-memory).
        _ahtolaCatalogConnection = new AhtolaManagedConnection("Data Source=:memory:;Local Provider=Managed");
        _ahtolaCatalogConnection.Open();
        SeedCatalog(_ahtolaCatalogConnection);

        _mdsCatalogConnection = new MdsConnection("Data Source=:memory:");
        _mdsCatalogConnection.Open();
        SeedCatalog(_mdsCatalogConnection);

        // Metadata SELECT setup (in-memory, small schema).
        _ahtolaMetadataConnection = new AhtolaManagedConnection("Data Source=:memory:;Local Provider=Managed");
        _ahtolaMetadataConnection.Open();
        SeedMetadataSchema(_ahtolaMetadataConnection);

        _mdsMetadataConnection = new MdsConnection("Data Source=:memory:");
        _mdsMetadataConnection.Open();
        SeedMetadataSchema(_mdsMetadataConnection);

        // Pin store list / read-only open setup (pre-built file DB for both engines).
        BuildPinsDatabase();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _ahtolaCatalogConnection?.Dispose();
        _mdsCatalogConnection?.Dispose();
        _ahtolaMetadataConnection?.Dispose();
        _mdsMetadataConnection?.Dispose();

        AhtolaManagedConnection.ClearAllPools();
        MdsConnection.ClearAllPools();

        if (Directory.Exists(_tempDirectory))
        {
            try
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; file locks can linger briefly after Dispose on some platforms.
            }
        }
    }

    // ---------------------------------------------------------------
    // Shape 1: Winget catalog search (JOIN + LIKE + LIMIT).
    // ---------------------------------------------------------------

    [BenchmarkCategory(CatalogSearchCategory)]
    [Benchmark(Description = "CatalogSearch: Ahtola Managed")]
    public int CatalogSearch_AhtolaManaged() => RunCatalogSearch(_ahtolaCatalogConnection);

    [BenchmarkCategory(CatalogSearchCategory)]
    [Benchmark(Baseline = true, Description = "CatalogSearch: Microsoft.Data.Sqlite")]
    public int CatalogSearch_MicrosoftDataSqlite() => RunCatalogSearch(_mdsCatalogConnection);

    // ---------------------------------------------------------------
    // Shape 2: Metadata SELECT (sqlite_schema + table_info-style read).
    // ---------------------------------------------------------------

    [BenchmarkCategory(MetadataSelectCategory)]
    [Benchmark(Description = "MetadataSelect: Ahtola Managed")]
    public int MetadataSelect_AhtolaManaged() => RunMetadataSelect(_ahtolaMetadataConnection);

    [BenchmarkCategory(MetadataSelectCategory)]
    [Benchmark(Baseline = true, Description = "MetadataSelect: Microsoft.Data.Sqlite")]
    public int MetadataSelect_MicrosoftDataSqlite() => RunMetadataSelect(_mdsMetadataConnection);

    // ---------------------------------------------------------------
    // Shape 3: Pin store list / read-only open.
    // ---------------------------------------------------------------

    [BenchmarkCategory(PinsReadOnlyOpenAndListCategory)]
    [Benchmark(Description = "PinsReadOnlyOpenAndList: Ahtola Managed")]
    public int PinsReadOnlyOpenAndList_AhtolaManaged()
    {
        using var connection = new AhtolaManagedConnection(
            $"Data Source={_pinsDbPath};Mode=ReadOnly;Pooling=False;Local Provider=Managed");
        connection.Open();
        return RunPinsList(connection);
    }

    [BenchmarkCategory(PinsReadOnlyOpenAndListCategory)]
    [Benchmark(Baseline = true, Description = "PinsReadOnlyOpenAndList: Microsoft.Data.Sqlite")]
    public int PinsReadOnlyOpenAndList_MicrosoftDataSqlite()
    {
        using var connection = new MdsConnection($"Data Source={_pinsDbPath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        return RunPinsList(connection);
    }

    // ---------------------------------------------------------------
    // Shared query bodies (kept identical across engines).
    // ---------------------------------------------------------------

    private static int RunCatalogSearch(System.Data.Common.DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.name, v.version
            FROM packages p
            JOIN versions v ON p.id = v.package_id
            WHERE p.name LIKE @term OR p.description LIKE @term
            LIMIT 20;
            """;
        var term = command.CreateParameter();
        term.ParameterName = "@term";
        term.Value = "%Tool 4%";
        command.Parameters.Add(term);

        using var reader = command.ExecuteReader();
        var rowCount = 0;
        while (reader.Read())
            rowCount++;

        return rowCount;
    }

    private static int RunMetadataSelect(System.Data.Common.DbConnection connection)
    {
        var tableCount = 0;
        using (var schemaCommand = connection.CreateCommand())
        {
            schemaCommand.CommandText = "SELECT name, type FROM sqlite_schema WHERE type = 'table';";
            using var reader = schemaCommand.ExecuteReader();
            while (reader.Read())
                tableCount++;
        }

        var columnCount = 0;
        using (var pragmaCommand = connection.CreateCommand())
        {
            pragmaCommand.CommandText = "PRAGMA table_info(widgets);";
            using var reader = pragmaCommand.ExecuteReader();
            while (reader.Read())
                columnCount++;
        }

        return tableCount + columnCount;
    }

    private static int RunPinsList(System.Data.Common.DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, path, pinned_at FROM pins ORDER BY pinned_at DESC;";
        using var reader = command.ExecuteReader();
        var rowCount = 0;
        while (reader.Read())
            rowCount++;

        return rowCount;
    }

    // ---------------------------------------------------------------
    // Seeding helpers.
    // ---------------------------------------------------------------

    private static void SeedCatalog(System.Data.Common.DbConnection connection)
    {
        using (var create = connection.CreateCommand())
        {
            create.CommandText =
                """
                CREATE TABLE packages(
                    id INTEGER PRIMARY KEY,
                    name TEXT NOT NULL,
                    description TEXT NOT NULL
                );
                CREATE TABLE versions(
                    id INTEGER PRIMARY KEY,
                    package_id INTEGER NOT NULL,
                    version TEXT NOT NULL,
                    FOREIGN KEY (package_id) REFERENCES packages(id)
                );
                CREATE INDEX idx_versions_package_id ON versions(package_id);
                """;
            create.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        using (var insertPackage = connection.CreateCommand())
        using (var insertVersion = connection.CreateCommand())
        {
            insertPackage.Transaction = transaction;
            insertPackage.CommandText = "INSERT INTO packages(id, name, description) VALUES (@id, @name, @description);";
            var packageId = insertPackage.CreateParameter();
            packageId.ParameterName = "@id";
            var packageName = insertPackage.CreateParameter();
            packageName.ParameterName = "@name";
            var packageDescription = insertPackage.CreateParameter();
            packageDescription.ParameterName = "@description";
            insertPackage.Parameters.Add(packageId);
            insertPackage.Parameters.Add(packageName);
            insertPackage.Parameters.Add(packageDescription);

            insertVersion.Transaction = transaction;
            insertVersion.CommandText = "INSERT INTO versions(package_id, version) VALUES (@packageId, @version);";
            var versionPackageId = insertVersion.CreateParameter();
            versionPackageId.ParameterName = "@packageId";
            var versionValue = insertVersion.CreateParameter();
            versionValue.ParameterName = "@version";
            insertVersion.Parameters.Add(versionPackageId);
            insertVersion.Parameters.Add(versionValue);

            for (var i = 1; i <= PackageCount; i++)
            {
                packageId.Value = i;
                packageName.Value = $"Contoso.Tool {i}".ToString(CultureInfo.InvariantCulture);
                packageDescription.Value = $"A sample CLI tool package number {i} used for benchmarking search queries.";
                insertPackage.ExecuteNonQuery();

                versionPackageId.Value = i;
                versionValue.Value = $"1.{i % 20}.0";
                insertVersion.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    private static void SeedMetadataSchema(System.Data.Common.DbConnection connection)
    {
        using var create = connection.CreateCommand();
        create.CommandText =
            """
            CREATE TABLE widgets(
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                weight REAL,
                created_at TEXT
            );
            CREATE TABLE gadgets(
                id INTEGER PRIMARY KEY,
                widget_id INTEGER,
                label TEXT
            );
            """;
        create.ExecuteNonQuery();
    }

    private void BuildPinsDatabase()
    {
        var connectionString = $"Data Source={_pinsDbPath};Pooling=False;Local Provider=Managed";
        using var connection = new AhtolaManagedConnection(connectionString);
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText =
                """
                CREATE TABLE pins(
                    id INTEGER PRIMARY KEY,
                    name TEXT NOT NULL,
                    path TEXT NOT NULL,
                    pinned_at TEXT NOT NULL
                );
                """;
            create.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO pins(id, name, path, pinned_at) VALUES (@id, @name, @path, @pinnedAt);";
            var id = insert.CreateParameter();
            id.ParameterName = "@id";
            var name = insert.CreateParameter();
            name.ParameterName = "@name";
            var path = insert.CreateParameter();
            path.ParameterName = "@path";
            var pinnedAt = insert.CreateParameter();
            pinnedAt.ParameterName = "@pinnedAt";
            insert.Parameters.Add(id);
            insert.Parameters.Add(name);
            insert.Parameters.Add(path);
            insert.Parameters.Add(pinnedAt);

            for (var i = 1; i <= 200; i++)
            {
                id.Value = i;
                name.Value = $"pin-{i}";
                path.Value = $"C:\\Users\\example\\pins\\pin-{i}.lnk";
                pinnedAt.Value = DateTime.UnixEpoch.AddMinutes(i).ToString("O");
                insert.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }
}
