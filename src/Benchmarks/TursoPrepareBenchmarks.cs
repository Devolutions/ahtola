using System.Data.Common;
using System.Text;
using Ahtola;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.Data.Sqlite;

namespace Benchmarks;

/// <summary>
/// Public ADO.NET proxy for Turso's internal parse/plan/codegen prepare benchmarks.
/// </summary>
/// <remarks>
/// Source provenance:
/// <c>turso-src/core/benches/prepare_benchmark.rs</c> and
/// <c>turso-src/sqlite/parser/benches/parser_benchmark.rs</c>.
/// The parser and internal prepare APIs are intentionally not public, so these
/// benchmarks use <see cref="DbCommand.Prepare"/> and include the provider's
/// public-command overhead.
/// </remarks>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class TursoPrepareShapeBenchmarks
{
    private AhtolaConnection _ahtola = null!;
    private SqliteConnection _sqlite = null!;
    private string _sql = null!;
    private DbCommand _ahtolaCommand = null!;
    private DbCommand _sqliteCommand = null!;
    private bool _ahtolaVariant;
    private bool _sqliteVariant;

    [Params("PointLookup", "ComplexPredicate", "FourWayJoin", "AggregateWindow", "CteCompound")]
    public string Shape { get; set; } = null!;

    [GlobalSetup]
    public void Setup()
    {
        _ahtola = new AhtolaConnection("Data Source=:memory:");
        _sqlite = new SqliteConnection("Data Source=:memory:");
        _ahtola.Open();
        _sqlite.Open();
        TursoPrepareSupport.CreateSchema(_ahtola);
        TursoPrepareSupport.CreateSchema(_sqlite);
        _sql = Shape switch
        {
            "PointLookup" => "SELECT id, name, email FROM users WHERE id = 1",
            "ComplexPredicate" => """
                SELECT id, status, price * quantity AS total FROM orders
                WHERE (status = 'shipped' OR status = 'pending' OR status IN ('paid', 'refunded'))
                  AND price > 10 AND quantity BETWEEN 1 AND 20
                  AND placed_at IS NOT NULL AND placed_at LIKE '2026-%'
                ORDER BY placed_at DESC, id ASC LIMIT 100 OFFSET 20
                """,
            "FourWayJoin" => """
                SELECT u.name, p.name, c.name, o.quantity, o.price
                FROM orders o JOIN users u ON u.id = o.user_id
                JOIN products p ON p.id = o.product_id
                LEFT JOIN categories c ON c.id = p.category_id
                WHERE u.age > 18 AND o.status = 'shipped'
                """,
            "AggregateWindow" => """
                SELECT user_id, sum(price * quantity),
                       row_number() OVER (ORDER BY sum(price * quantity) DESC)
                FROM orders GROUP BY user_id HAVING count(*) > 1
                """,
            "CteCompound" => """
                WITH active AS (SELECT id, name FROM users WHERE age BETWEEN 18 AND 65),
                     totals AS (SELECT user_id, sum(price * quantity) total FROM orders GROUP BY user_id)
                SELECT a.name, t.total FROM active a JOIN totals t ON t.user_id = a.id
                UNION ALL SELECT name, 0 FROM users WHERE age IS NULL
                """,
            _ => throw new InvalidOperationException(Shape),
        };
        _ahtolaCommand = TursoPrepareSupport.Command(_ahtola, _sql);
        _sqliteCommand = TursoPrepareSupport.Command(_sqlite, _sql);
        _ahtolaCommand.Prepare();
        _sqliteCommand.Prepare();
    }

    [Benchmark, BenchmarkCategory("PrepareShape")]
    public int AhtolaPrepare()
    {
        _ahtolaCommand.CommandText = (_ahtolaVariant = !_ahtolaVariant) ? _sql : _sql + " ";
        _ahtolaCommand.Prepare();
        return _ahtolaCommand.Parameters.Count;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("PrepareShape")]
    public int MicrosoftDataSqlitePrepare()
    {
        _sqliteCommand.CommandText = (_sqliteVariant = !_sqliteVariant) ? _sql : _sql + " ";
        _sqliteCommand.Prepare();
        return _sqliteCommand.Parameters.Count;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _ahtolaCommand.Dispose();
        _sqliteCommand.Dispose();
        _ahtola.Dispose();
        _sqlite.Dispose();
    }
}

/// <summary>
/// Parser-proxy scaling coverage for the upstream query and batched-insert parser cases.
/// </summary>
/// <remarks>
/// Source provenance: <c>turso-src/sqlite/parser/benches/parser_benchmark.rs</c>.
/// A lexer-only equivalent is omitted because Ahtola exposes no public lexer API;
/// <see cref="DbCommand.Prepare"/> is the closest supported parser proxy.
/// </remarks>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class TursoPrepareParserProxyBenchmarks
{
    private AhtolaConnection _ahtola = null!;
    private SqliteConnection _sqlite = null!;

    [GlobalSetup]
    public void Setup()
    {
        _ahtola = new AhtolaConnection("Data Source=:memory:");
        _sqlite = new SqliteConnection("Data Source=:memory:");
        _ahtola.Open();
        _sqlite.Open();
        TursoPrepareSupport.Execute(_ahtola, "CREATE TABLE test(id INTEGER, value TEXT)");
        TursoPrepareSupport.Execute(_sqlite, "CREATE TABLE test(id INTEGER, value TEXT)");
    }

    [Benchmark, BenchmarkCategory("ParserProxy")]
    [Arguments("SELECT 1")]
    [Arguments("SELECT first_name, count(1) FROM (SELECT 'Ada' first_name) GROUP BY first_name HAVING count(1) > 0 ORDER BY count(1) LIMIT 1")]
    public int AhtolaQuery(string sql) => TursoPrepareSupport.PrepareOnce(_ahtola, sql);

    [Benchmark(Baseline = true), BenchmarkCategory("ParserProxy")]
    [Arguments("SELECT 1")]
    [Arguments("SELECT first_name, count(1) FROM (SELECT 'Ada' first_name) GROUP BY first_name HAVING count(1) > 0 ORDER BY count(1) LIMIT 1")]
    public int MicrosoftDataSqliteQuery(string sql) => TursoPrepareSupport.PrepareOnce(_sqlite, sql);

    [Benchmark, BenchmarkCategory("ParserProxyInsert")]
    [ArgumentsSource(nameof(InsertStatements))]
    public int AhtolaInsertBatch(string sql) => TursoPrepareSupport.PrepareOnce(_ahtola, sql);

    [Benchmark(Baseline = true), BenchmarkCategory("ParserProxyInsert")]
    [ArgumentsSource(nameof(InsertStatements))]
    public int MicrosoftDataSqliteInsertBatch(string sql) => TursoPrepareSupport.PrepareOnce(_sqlite, sql);

    public IEnumerable<string> InsertStatements()
    {
        yield return TursoPrepareSupport.BuildLiteralInsert(1);
        yield return TursoPrepareSupport.BuildLiteralInsert(10);
        yield return TursoPrepareSupport.BuildLiteralInsert(100);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _ahtola.Dispose();
        _sqlite.Dispose();
    }
}

/// <summary>Prepare scaling for ORM-style multi-row inserts with many parameters.</summary>
/// <remarks>Source provenance: <c>turso-src/core/benches/prepare_params_benchmark.rs</c>.</remarks>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class TursoPrepareParameterBenchmarks
{
    private AhtolaConnection _ahtola = null!;
    private SqliteConnection _sqlite = null!;
    private DbCommand _ahtolaCommand = null!;
    private DbCommand _sqliteCommand = null!;
    private string _sql = null!;
    private bool _ahtolaVariant;
    private bool _sqliteVariant;

    [Params(40, 100, 200)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _ahtola = new AhtolaConnection("Data Source=:memory:");
        _sqlite = new SqliteConnection("Data Source=:memory:");
        _ahtola.Open();
        _sqlite.Open();
        const string ddl = "CREATE TABLE at_refs(source_dbt_doc_id, source_column_id, source_row_id, target_row_id, source_row_number)";
        TursoPrepareSupport.Execute(_ahtola, ddl);
        TursoPrepareSupport.Execute(_sqlite, ddl);
        _sql = TursoPrepareSupport.BuildParameterizedInsert(Rows);
        _ahtolaCommand = TursoPrepareSupport.CommandWithParameters(_ahtola, _sql, Rows * 5);
        _sqliteCommand = TursoPrepareSupport.CommandWithParameters(_sqlite, _sql, Rows * 5);
        _ahtolaCommand.Prepare();
        _sqliteCommand.Prepare();
    }

    [Benchmark, BenchmarkCategory("PrepareParameters")]
    public int AhtolaPrepare()
    {
        _ahtolaCommand.CommandText = (_ahtolaVariant = !_ahtolaVariant) ? _sql : _sql + " ";
        _ahtolaCommand.Prepare();
        return _ahtolaCommand.Parameters.Count;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("PrepareParameters")]
    public int MicrosoftDataSqlitePrepare()
    {
        _sqliteCommand.CommandText = (_sqliteVariant = !_sqliteVariant) ? _sql : _sql + " ";
        _sqliteCommand.Prepare();
        return _sqliteCommand.Parameters.Count;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _ahtolaCommand.Dispose();
        _sqliteCommand.Dispose();
        _ahtola.Dispose();
        _sqlite.Dispose();
    }
}

internal static class TursoPrepareSupport
{
    public static void CreateSchema(DbConnection connection)
    {
        string[] statements =
        [
            "CREATE TABLE users(id INTEGER PRIMARY KEY, name TEXT NOT NULL, email TEXT NOT NULL, age INTEGER, created_at TEXT)",
            "CREATE UNIQUE INDEX users_email ON users(email)",
            "CREATE INDEX users_age ON users(age)",
            "CREATE TABLE products(id INTEGER PRIMARY KEY, sku TEXT NOT NULL, name TEXT NOT NULL, category_id INTEGER, price REAL NOT NULL)",
            "CREATE TABLE categories(id INTEGER PRIMARY KEY, name TEXT NOT NULL, parent_id INTEGER)",
            """
            CREATE TABLE orders(
                id INTEGER PRIMARY KEY,
                user_id INTEGER NOT NULL,
                product_id INTEGER NOT NULL,
                quantity INTEGER NOT NULL,
                price REAL NOT NULL,
                status TEXT NOT NULL,
                placed_at TEXT)
            """,
            "CREATE INDEX orders_user ON orders(user_id)",
            "CREATE INDEX orders_status ON orders(status)",
        ];
        foreach (var statement in statements)
            Execute(connection, statement);
    }

    public static void Execute(DbConnection connection, string sql)
    {
        using var command = Command(connection, sql);
        command.ExecuteNonQuery();
    }

    public static DbCommand Command(DbConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    public static int PrepareOnce(DbConnection connection, string sql)
    {
        using var command = Command(connection, sql);
        command.Prepare();
        return command.Parameters.Count;
    }

    public static DbCommand CommandWithParameters(DbConnection connection, string sql, int count)
    {
        var command = Command(connection, sql);
        for (var i = 1; i <= count; i++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"@p{i}";
            parameter.Value = i;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    public static string BuildParameterizedInsert(int rows)
    {
        var sql = new StringBuilder("INSERT INTO at_refs VALUES ");
        var parameter = 1;
        for (var row = 0; row < rows; row++)
        {
            if (row > 0)
                sql.Append(',');
            sql.Append('(');
            for (var column = 0; column < 5; column++)
            {
                if (column > 0)
                    sql.Append(',');
                sql.Append("@p").Append(parameter++);
            }
            sql.Append(')');
        }
        return sql.ToString();
    }

    public static string BuildLiteralInsert(int rows)
    {
        var sql = new StringBuilder("INSERT INTO test VALUES ");
        for (var i = 0; i < rows; i++)
        {
            if (i > 0)
                sql.Append(',');
            sql.Append('(').Append(i).Append(",'value_").Append(i).Append("')");
        }
        return sql.ToString();
    }
}
