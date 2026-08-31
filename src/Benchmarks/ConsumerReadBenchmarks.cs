using System.Data.Common;
using Ahtola;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using MicrosoftSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace Benchmarks;

/// <summary>
/// Permanent replacement for
/// <c>src/ConsumerBenchmarks/ConsumerReadBenchmarks.cs</c>, preserving its
/// catalog search, metadata inspection, and read-only pin-store workloads in
/// the primary benchmark assembly.
/// </summary>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class ConsumerReadBenchmarks
{
    private const string CatalogCategory = "consumer-catalog-search";
    private const string MetadataCategory = "consumer-metadata";
    private const string PinsCategory = "consumer-pins-open-list";
    private const int PackageCount = 1_000;
    private const int PinCount = 200;

    private AhtolaConnection _ahtolaCatalog = null!;
    private MicrosoftSqliteConnection _sqliteCatalog = null!;
    private AhtolaConnection _ahtolaMetadata = null!;
    private MicrosoftSqliteConnection _sqliteMetadata = null!;
    private string _root = null!;
    private string _pinsPath = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _root = Path.Combine(Path.GetTempPath(), "ahtola-consumer-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _pinsPath = Path.Combine(_root, "pins.db");

        _ahtolaCatalog = OpenAhtolaMemory();
        _sqliteCatalog = OpenSqliteMemory();
        SeedCatalog(_ahtolaCatalog);
        SeedCatalog(_sqliteCatalog);

        _ahtolaMetadata = OpenAhtolaMemory();
        _sqliteMetadata = OpenSqliteMemory();
        SeedMetadata(_ahtolaMetadata);
        SeedMetadata(_sqliteMetadata);
        BuildPinsFixture();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _ahtolaCatalog.Dispose();
        _sqliteCatalog.Dispose();
        _ahtolaMetadata.Dispose();
        _sqliteMetadata.Dispose();
        AhtolaConnection.ClearAllPools();
        MicrosoftSqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [BenchmarkCategory(CatalogCategory)]
    [Benchmark]
    public int AhtolaCatalogSearch()
        => CatalogSearch(_ahtolaCatalog);

    [BenchmarkCategory(CatalogCategory)]
    [Benchmark(Baseline = true)]
    public int SqliteCatalogSearch()
        => CatalogSearch(_sqliteCatalog);

    [BenchmarkCategory(MetadataCategory)]
    [Benchmark]
    public int AhtolaMetadataRead()
        => MetadataRead(_ahtolaMetadata);

    [BenchmarkCategory(MetadataCategory)]
    [Benchmark(Baseline = true)]
    public int SqliteMetadataRead()
        => MetadataRead(_sqliteMetadata);

    [BenchmarkCategory(PinsCategory)]
    [Benchmark]
    public long AhtolaReadOnlyOpenAndListPins()
    {
        using var connection = new AhtolaConnection(
            $"Data Source={_pinsPath};Mode=ReadOnly;Pooling=False;Local Provider=Managed");
        connection.Open();
        return ListPins(connection);
    }

    [BenchmarkCategory(PinsCategory)]
    [Benchmark(Baseline = true)]
    public long SqliteReadOnlyOpenAndListPins()
    {
        using var connection = new MicrosoftSqliteConnection(
            $"Data Source={_pinsPath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        return ListPins(connection);
    }

    private static AhtolaConnection OpenAhtolaMemory()
    {
        var connection = new AhtolaConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        return connection;
    }

    private static MicrosoftSqliteConnection OpenSqliteMemory()
    {
        var connection = new MicrosoftSqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private static int CatalogSearch(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.name, v.version
            FROM packages AS p
            JOIN versions AS v ON p.id = v.package_id
            WHERE p.name LIKE $term OR p.description LIKE $term
            LIMIT 20;
            """;
        var term = command.CreateParameter();
        term.ParameterName = "$term";
        term.Value = "%Tool 4%";
        command.Parameters.Add(term);

        using var reader = command.ExecuteReader();
        var checksum = 0;
        while (reader.Read())
            checksum = unchecked((checksum * 397) ^ reader.GetString(0).Length ^ reader.GetString(1).Length);
        return checksum;
    }

    private static int MetadataRead(DbConnection connection)
    {
        var checksum = 0;
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "SELECT name, type FROM sqlite_schema WHERE type = 'table';";
            using var reader = schema.ExecuteReader();
            while (reader.Read())
                checksum = unchecked((checksum * 397) ^ reader.GetString(0).Length ^ reader.GetString(1).Length);
        }

        using (var columns = connection.CreateCommand())
        {
            columns.CommandText = "PRAGMA table_info(widgets);";
            using var reader = columns.ExecuteReader();
            while (reader.Read())
                checksum = unchecked((checksum * 397) ^ reader.GetString(1).Length);
        }

        return checksum;
    }

    private static long ListPins(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, path, pinned_at FROM pins ORDER BY pinned_at DESC;";
        using var reader = command.ExecuteReader();
        long checksum = 0;
        while (reader.Read())
        {
            checksum = unchecked(
                (checksum * 397)
                ^ reader.GetInt64(0)
                ^ reader.GetString(1).Length
                ^ reader.GetString(2).Length
                ^ reader.GetString(3).Length);
        }

        return checksum;
    }

    private static void SeedCatalog(DbConnection connection)
    {
        Execute(
            connection,
            """
            CREATE TABLE packages(id INTEGER PRIMARY KEY, name TEXT NOT NULL, description TEXT NOT NULL);
            """);
        Execute(
            connection,
            """
            CREATE TABLE versions(
                id INTEGER PRIMARY KEY,
                package_id INTEGER NOT NULL,
                version TEXT NOT NULL,
                FOREIGN KEY(package_id) REFERENCES packages(id));
            """);
        Execute(connection, "CREATE INDEX idx_versions_package_id ON versions(package_id);");

        using var transaction = connection.BeginTransaction();
        using var package = connection.CreateCommand();
        using var version = connection.CreateCommand();
        package.Transaction = transaction;
        version.Transaction = transaction;
        package.CommandText = "INSERT INTO packages(id, name, description) VALUES ($id, $name, $description);";
        version.CommandText = "INSERT INTO versions(package_id, version) VALUES ($id, $version);";
        var packageId = AddParameter(package, "$id");
        var packageName = AddParameter(package, "$name");
        var description = AddParameter(package, "$description");
        var versionId = AddParameter(version, "$id");
        var versionText = AddParameter(version, "$version");
        for (var row = 1; row <= PackageCount; row++)
        {
            packageId.Value = (long)row;
            packageName.Value = $"Contoso.Tool {row}";
            description.Value = $"A deterministic CLI package number {row}.";
            package.ExecuteNonQuery();
            versionId.Value = (long)row;
            versionText.Value = $"1.{row % 20}.0";
            version.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void SeedMetadata(DbConnection connection)
    {
        Execute(
            connection,
            """
            CREATE TABLE widgets(id INTEGER PRIMARY KEY, name TEXT NOT NULL, weight REAL, created_at TEXT);
            """);
        Execute(
            connection,
            "CREATE TABLE gadgets(id INTEGER PRIMARY KEY, widget_id INTEGER, label TEXT);");
    }

    private void BuildPinsFixture()
    {
        using var connection = new MicrosoftSqliteConnection($"Data Source={_pinsPath};Pooling=False");
        connection.Open();
        Execute(
            connection,
            """
            CREATE TABLE pins(
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                path TEXT NOT NULL,
                pinned_at TEXT NOT NULL);
            """);
        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO pins(id, name, path, pinned_at) VALUES ($id, $name, $path, $time);";
        var id = AddParameter(insert, "$id");
        var name = AddParameter(insert, "$name");
        var path = AddParameter(insert, "$path");
        var time = AddParameter(insert, "$time");
        for (var row = 1; row <= PinCount; row++)
        {
            id.Value = (long)row;
            name.Value = $"pin-{row:D3}";
            path.Value = $@"C:\fixture\pins\pin-{row:D3}.lnk";
            time.Value = DateTime.UnixEpoch.AddMinutes(row).ToString("O");
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static DbParameter AddParameter(DbCommand command, string name)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        command.Parameters.Add(parameter);
        return parameter;
    }

    private static void Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
