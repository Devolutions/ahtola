using Ahtola;
using BenchmarkDotNet.Attributes;
using MdsConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace Benchmarks;

[MemoryDiagnoser]
[CategoriesColumn]
public class Benchmarks
{
    private MdsConnection _sqlite = null!;
    private AhtolaConnection _ahtola = null!;

    [GlobalSetup]
    public void Setup()
    {
        _sqlite = new MdsConnection("Data Source=:memory:");
        _sqlite.Open();
        _ahtola = new AhtolaConnection("Data Source=:memory:");
        _ahtola.Open();
        CreateTable(_sqlite);
        CreateTable(_ahtola);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _sqlite.Dispose();
        _ahtola.Dispose();
    }

    [BenchmarkCategory("Read", "Smoke")]
    [Benchmark(Baseline = true, Description = "SQLite two-row SELECT")]
    public int SqliteSelect() => Select(_sqlite);

    [BenchmarkCategory("Read", "Smoke")]
    [Benchmark(Description = "Ahtola two-row SELECT")]
    public int AhtolaSelect() => Select(_ahtola);

    private static void CreateTable(System.Data.Common.DbConnection connection)
    {
        using var createTableCommand = connection.CreateCommand();
        createTableCommand.CommandText = "CREATE TABLE t(a, b)";
        createTableCommand.ExecuteNonQuery();

        using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText = @"INSERT INTO t(a, b) VALUES (1, 2), (3, 4);";
        insertCommand.ExecuteNonQuery();
    }

    private static int Select(System.Data.Common.DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM t;";
        using var reader = command.ExecuteReader();
        var sum = 0;
        while (reader.Read())
            sum += reader.GetInt32(0);

        return sum;
    }
}