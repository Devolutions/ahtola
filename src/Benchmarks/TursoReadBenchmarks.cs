using System.Data.Common;
using Ahtola;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using MicrosoftSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace Benchmarks;

/// <summary>
/// Read-path comparisons adapted from
/// <c>turso-src/core/benches/count_benchmark.rs</c> and
/// <c>turso-src/core/benches/select_star_benchmark.rs</c>.
/// </summary>
/// <remarks>
/// The original million-row and 100k-row fixtures are intentionally reduced to
/// practical defaults for the permanent suite. Both providers receive the same
/// deterministic rows and each method returns a checksum of consumed values.
/// </remarks>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class TursoReadBenchmarks
{
    private const string CountFilteredCategory = "read-count-filtered";
    private const string CountTextCategory = "read-count-text";
    private const string CountGroupByCategory = "read-count-group-by";
    private const string SelectStarCategory = "read-select-star";

    private AhtolaConnection _ahtola = null!;
    private MicrosoftSqliteConnection _sqlite = null!;

    /// <summary>Rows in the deterministic read fixture.</summary>
    [Params(1_000)]
    public int RowCount { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _ahtola = new AhtolaConnection("Data Source=:memory:;Local Provider=Managed");
        _ahtola.Open();
        Seed(_ahtola, RowCount);

        _sqlite = new MicrosoftSqliteConnection("Data Source=:memory:");
        _sqlite.Open();
        Seed(_sqlite, RowCount);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _ahtola.Dispose();
        _sqlite.Dispose();
        AhtolaConnection.ClearAllPools();
        MicrosoftSqliteConnection.ClearAllPools();
    }

    [BenchmarkCategory(CountFilteredCategory)]
    [Benchmark]
    public long AhtolaCountFiltered()
        => ReadIntegralRows(_ahtola, "SELECT COUNT(*) FROM read_rows WHERE g <= 'group-07';");

    [BenchmarkCategory(CountFilteredCategory)]
    [Benchmark(Baseline = true)]
    public long SqliteCountFiltered()
        => ReadIntegralRows(_sqlite, "SELECT COUNT(*) FROM read_rows WHERE g <= 'group-07';");

    [BenchmarkCategory(CountTextCategory)]
    [Benchmark]
    public long AhtolaCountText()
        => ReadIntegralRows(_ahtola, "SELECT COUNT(g) FROM read_rows;");

    [BenchmarkCategory(CountTextCategory)]
    [Benchmark(Baseline = true)]
    public long SqliteCountText()
        => ReadIntegralRows(_sqlite, "SELECT COUNT(g) FROM read_rows;");

    [BenchmarkCategory(CountGroupByCategory)]
    [Benchmark]
    public long AhtolaCountGroupBy()
        => ReadGroupedCounts(_ahtola);

    [BenchmarkCategory(CountGroupByCategory)]
    [Benchmark(Baseline = true)]
    public long SqliteCountGroupBy()
        => ReadGroupedCounts(_sqlite);

    [BenchmarkCategory(SelectStarCategory)]
    [Benchmark]
    public long AhtolaSelectStar()
        => ReadAllColumns(_ahtola);

    [BenchmarkCategory(SelectStarCategory)]
    [Benchmark(Baseline = true)]
    public long SqliteSelectStar()
        => ReadAllColumns(_sqlite);

    private static void Seed(DbConnection connection, int rowCount)
    {
        Execute(
            connection,
            """
            CREATE TABLE read_rows(
                id INTEGER PRIMARY KEY,
                g TEXT NOT NULL,
                payload TEXT NOT NULL,
                v INTEGER NOT NULL,
                c4 INTEGER NOT NULL,
                c5 TEXT NOT NULL,
                c6 INTEGER NOT NULL,
                c7 TEXT NOT NULL,
                c8 INTEGER NOT NULL,
                c9 TEXT NOT NULL);
            """);
        Execute(connection, "CREATE INDEX idx_read_rows_g ON read_rows(g);");

        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO read_rows(id, g, payload, v, c4, c5, c6, c7, c8, c9)
            VALUES ($id, $g, $payload, $v, $c4, $c5, $c6, $c7, $c8, $c9);
            """;
        var parameters = Enumerable.Range(0, 10)
            .Select(index =>
            {
                var parameter = insert.CreateParameter();
                parameter.ParameterName = index switch
                {
                    0 => "$id",
                    1 => "$g",
                    2 => "$payload",
                    3 => "$v",
                    _ => "$c" + index,
                };
                insert.Parameters.Add(parameter);
                return parameter;
            })
            .ToArray();

        for (var row = 1; row <= rowCount; row++)
        {
            parameters[0].Value = (long)row;
            parameters[1].Value = $"group-{row % 16:D2}";
            parameters[2].Value = $"row-payload-{row:D6}";
            parameters[3].Value = (long)row;
            parameters[4].Value = (long)(row * 5);
            parameters[5].Value = $"v{row % 97:D2}-5";
            parameters[6].Value = (long)(row * 7);
            parameters[7].Value = $"v{row % 97:D2}-7";
            parameters[8].Value = (long)(row * 9);
            parameters[9].Value = $"v{row % 97:D2}-9";
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static long ReadIntegralRows(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        long checksum = 0;
        while (reader.Read())
            checksum = unchecked((checksum * 397) ^ Convert.ToInt64(reader.GetValue(0)));
        return checksum;
    }

    private static long ReadGroupedCounts(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT g, COUNT(*) FROM read_rows GROUP BY g;";
        using var reader = command.ExecuteReader();
        long checksum = 0;
        while (reader.Read())
            checksum = unchecked((checksum * 397) ^ reader.GetString(0).Length ^ reader.GetInt64(1));
        return checksum;
    }

    private static long ReadAllColumns(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM read_rows;";
        using var reader = command.ExecuteReader();
        long checksum = 0;
        while (reader.Read())
        {
            for (var column = 0; column < reader.FieldCount; column++)
            {
                var value = reader.GetValue(column);
                checksum = unchecked((checksum * 397) ^ (value is string text ? text.Length : Convert.ToInt64(value)));
            }
        }

        return checksum;
    }

    private static void Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
