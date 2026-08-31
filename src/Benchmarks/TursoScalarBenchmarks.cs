using System.Data.Common;
using Ahtola;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.Data.Sqlite;

namespace Benchmarks;

/// <summary>Representative public-SQL equivalents of Turso scalar-function microbenchmarks.</summary>
/// <remarks>
/// Source provenance:
/// <c>turso-src/core/benches/sql_functions/datetime.rs</c>,
/// <c>likeop.rs</c>, <c>numeric.rs</c>, and <c>value.rs</c>.
/// Direct internal Value operations are expressed as prepared scalar SELECTs.
/// </remarks>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class TursoScalarFunctionBenchmarks
{
    private AhtolaConnection _ahtola = null!;
    private SqliteConnection _sqlite = null!;
    private DbCommand _ahtolaCommand = null!;
    private DbCommand _sqliteCommand = null!;

    [Params("DateTime", "DateModifiers", "LikeGlob", "Numeric", "ValueText")]
    public string Area { get; set; } = null!;

    [GlobalSetup]
    public void Setup()
    {
        _ahtola = new AhtolaConnection("Data Source=:memory:");
        _sqlite = new SqliteConnection("Data Source=:memory:");
        _ahtola.Open();
        _sqlite.Open();
        var sql = Area switch
        {
            "DateTime" => """
                SELECT date('2024-07-21'), time('14:30:45.123'),
                       datetime('2024-07-21T14:30:45'), julianday('2024-07-21 14:30:45'),
                       unixepoch('2024-07-21 14:30:45'), strftime('%Y-%m-%d %H:%M:%S', '2024-07-21 14:30:45')
                """,
            "DateModifiers" => """
                SELECT date('2024-07-21', '+5 days'), datetime('1721577045', 'unixepoch'),
                       date('2024-07-21', '+1 month', 'start of month', '+7 days'),
                       timediff('2024-07-25 14:30:45', '2024-07-21 10:15:30')
                """,
            "LikeGlob" => """
                SELECT 'say hello world' LIKE '%h_llo%' ESCAPE '\',
                       '100%' LIKE '100\%' ESCAPE '\',
                       'The quick brown fox jumps over the lazy dog' GLOB '*quick*fox*lazy*',
                       'apple' GLOB '[abc]*'
                """,
            "Numeric" => """
                SELECT CAST('123.456' AS REAL), CAST('9223372036854775807' AS INTEGER),
                       round(3.141592653589793, 8), abs(-123.456),
                       (1000 + 2000), (100.5 * 200.5), (1000 / 10), (12345 << 2)
                """,
            "ValueText" => """
                SELECT lower('THE QUICK BROWN FOX JUMPS OVER THE LAZY DOG'),
                       upper('hello'), length('héllo wörld 你好世界'),
                       trim('xxxhello worldxxx', 'x'), substr('héllo wörld 你好', 1, 10),
                       instr('the quick brown fox', 'fox'), replace('one two one', 'one', 'three'),
                       typeof(123.456), hex('hello')
                """,
            _ => throw new InvalidOperationException(Area),
        };
        _ahtolaCommand = TursoScalarSupport.Prepared(_ahtola, sql);
        _sqliteCommand = TursoScalarSupport.Prepared(_sqlite, sql);
        _ = TursoScalarSupport.ReadAll(_ahtolaCommand);
        _ = TursoScalarSupport.ReadAll(_sqliteCommand);
    }

    [Benchmark, BenchmarkCategory("ScalarFunctions")]
    public int Ahtola() => TursoScalarSupport.ReadAll(_ahtolaCommand);

    [Benchmark(Baseline = true), BenchmarkCategory("ScalarFunctions")]
    public int MicrosoftDataSqlite() => TursoScalarSupport.ReadAll(_sqliteCommand);

    [GlobalCleanup]
    public void Cleanup()
    {
        _ahtolaCommand.Dispose();
        _sqliteCommand.Dispose();
        _ahtola.Dispose();
        _sqlite.Dispose();
    }
}

internal static class TursoScalarSupport
{
    public static DbCommand Prepared(DbConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Prepare();
        return command;
    }

    public static int ReadAll(DbCommand command)
    {
        using var reader = command.ExecuteReader();
        var observed = 17;
        while (reader.Read())
        {
            for (var i = 0; i < reader.FieldCount; i++)
                observed = unchecked((observed * 31) + (reader.IsDBNull(i) ? 0 : reader.GetValue(i).GetHashCode()));
        }
        return observed;
    }
}
