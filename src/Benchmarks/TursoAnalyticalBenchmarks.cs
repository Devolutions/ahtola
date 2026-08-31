using System.Data.Common;
using Ahtola;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using MicrosoftSqlite = Microsoft.Data.Sqlite;

namespace Benchmarks;

/// <summary>
/// Compact analytical coverage inspired by Turso's graph-query and TPC-H benches.
/// Fixture construction is intentionally outside the measured query lifecycle.
/// </summary>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class TursoAnalyticalBenchmarks
{
    private AhtolaConnection? _ahtola;
    private MicrosoftSqlite.SqliteConnection? _sqlite;

    [Params(10_000)]
    public int OrderCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _ahtola = new AhtolaConnection("Data Source=:memory:");
        _sqlite = new MicrosoftSqlite.SqliteConnection("Data Source=:memory:");
        _ahtola.Open();
        _sqlite.Open();
        TursoMemoryBenchmarkSupport.Execute(_ahtola, "PRAGMA cache_size=-65536;");
        TursoMemoryBenchmarkSupport.Execute(_sqlite, "PRAGMA cache_size=-65536;");
        var effectiveOrderCount = BenchmarkRunContext.ScaleForSmoke(OrderCount, 1_000);
        BuildFixture(_ahtola, effectiveOrderCount);
        BuildFixture(_sqlite, effectiveOrderCount);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _ahtola?.Dispose();
        _sqlite?.Dispose();
    }

    [BenchmarkCategory("analytical-aggregation")]
    [Benchmark(Baseline = true, Description = "SQLite grouped aggregation")]
    public long SqliteAggregation() => TursoMemoryBenchmarkSupport.Consume(
        _sqlite!,
        "SELECT region, status, count(*), sum(amount), avg(discount) FROM orders GROUP BY region, status;");

    [BenchmarkCategory("analytical-aggregation")]
    [Benchmark(Description = "Ahtola grouped aggregation")]
    public long AhtolaAggregation() => TursoMemoryBenchmarkSupport.Consume(
        _ahtola!,
        "SELECT region, status, count(*), sum(amount), avg(discount) FROM orders GROUP BY region, status;");

    [BenchmarkCategory("analytical-join")]
    [Benchmark(Baseline = true, Description = "SQLite dimension join")]
    public long SqliteJoin() => TursoMemoryBenchmarkSupport.Consume(
        _sqlite!,
        """
        SELECT c.segment, count(*), sum(o.amount)
        FROM customers AS c JOIN orders AS o ON o.customer_id = c.id
        WHERE o.amount >= 250
        GROUP BY c.segment;
        """);

    [BenchmarkCategory("analytical-join")]
    [Benchmark(Description = "Ahtola dimension join")]
    public long AhtolaJoin() => TursoMemoryBenchmarkSupport.Consume(
        _ahtola!,
        """
        SELECT c.segment, count(*), sum(o.amount)
        FROM customers AS c JOIN orders AS o ON o.customer_id = c.id
        WHERE o.amount >= 250
        GROUP BY c.segment;
        """);

    [BenchmarkCategory("analytical-graph-shape")]
    [Benchmark(Baseline = true, Description = "SQLite OR/IN graph-query shape")]
    public long SqliteGraphShape() => TursoMemoryBenchmarkSupport.Consume(
        _sqlite!,
        """
        SELECT customer_id, count(*)
        FROM orders
        WHERE (status = 'open' AND region IN ('americas', 'emea'))
           OR (discount >= 0.20 AND amount > 700)
        GROUP BY customer_id;
        """);

    [BenchmarkCategory("analytical-graph-shape")]
    [Benchmark(Description = "Ahtola OR/IN graph-query shape")]
    public long AhtolaGraphShape() => TursoMemoryBenchmarkSupport.Consume(
        _ahtola!,
        """
        SELECT customer_id, count(*)
        FROM orders
        WHERE (status = 'open' AND region IN ('americas', 'emea'))
           OR (discount >= 0.20 AND amount > 700)
        GROUP BY customer_id;
        """);

    private static void BuildFixture(DbConnection connection, int orderCount)
    {
        TursoMemoryBenchmarkSupport.Execute(connection,
            "CREATE TABLE customers(id INTEGER PRIMARY KEY, segment TEXT NOT NULL);");
        TursoMemoryBenchmarkSupport.Execute(connection,
            """
            CREATE TABLE orders(
                id INTEGER PRIMARY KEY,
                customer_id INTEGER NOT NULL,
                region TEXT NOT NULL,
                status TEXT NOT NULL,
                amount REAL NOT NULL,
                discount REAL NOT NULL);
            """);
        TursoMemoryBenchmarkSupport.Execute(connection,
            "CREATE INDEX orders_customer ON orders(customer_id);");
        TursoMemoryBenchmarkSupport.Execute(connection,
            "CREATE INDEX orders_region_status ON orders(region, status);");

        using (var transaction = connection.BeginTransaction())
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO customers(id, segment) VALUES ($id, $segment);";
            var id = TursoMemoryBenchmarkSupport.AddParameter(insert, "$id");
            var segment = TursoMemoryBenchmarkSupport.AddParameter(insert, "$segment");
            for (var i = 1; i <= 256; i++)
            {
                id.Value = i;
                segment.Value = (i % 4) switch { 0 => "enterprise", 1 => "consumer", 2 => "public", _ => "startup" };
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        using (var transaction = connection.BeginTransaction())
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO orders(id, customer_id, region, status, amount, discount) "
                + "VALUES ($id, $customer, $region, $status, $amount, $discount);";
            var id = TursoMemoryBenchmarkSupport.AddParameter(insert, "$id");
            var customer = TursoMemoryBenchmarkSupport.AddParameter(insert, "$customer");
            var region = TursoMemoryBenchmarkSupport.AddParameter(insert, "$region");
            var status = TursoMemoryBenchmarkSupport.AddParameter(insert, "$status");
            var amount = TursoMemoryBenchmarkSupport.AddParameter(insert, "$amount");
            var discount = TursoMemoryBenchmarkSupport.AddParameter(insert, "$discount");
            var regions = new[] { "americas", "emea", "apac" };
            var statuses = new[] { "open", "paid", "shipped", "returned" };
            for (var i = 1; i <= orderCount; i++)
            {
                id.Value = i;
                customer.Value = 1 + ((i * 37) % 256);
                region.Value = regions[(i * 7) % regions.Length];
                status.Value = statuses[(i * 11) % statuses.Length];
                amount.Value = ((i * 7919) % 100_000) / 100d;
                discount.Value = ((i * 17) % 31) / 100d;
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }
}
