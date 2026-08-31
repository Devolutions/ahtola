using System.Data.Common;
using Ahtola;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.Data.Sqlite;

namespace Benchmarks;

/// <summary>Steady-state public reader equivalents for Turso's VDBE record-recycling paths.</summary>
/// <remarks>
/// Source provenance: <c>turso-src/core/benches/record_recycling.rs</c>.
/// MVCC-only index seeking is omitted because Microsoft.Data.Sqlite has no
/// compatible MVCC journal mode; the shared sorter and aggregate shapes retain
/// a meaningful SQLite baseline.
/// </remarks>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class TursoRecordRecyclingBenchmarks
{
    private const int RowCount = 2_048;
    private AhtolaConnection _ahtola = null!;
    private SqliteConnection _sqlite = null!;
    private DbCommand _ahtolaCommand = null!;
    private DbCommand _sqliteCommand = null!;

    [Params("SorterRecordRoundTrip", "GroupConcatText", "MaxTextReplacement", "LastValueBlobCapture")]
    public string Shape { get; set; } = null!;

    [GlobalSetup]
    public void Setup()
    {
        _ahtola = new AhtolaConnection("Data Source=:memory:");
        _sqlite = new SqliteConnection("Data Source=:memory:");
        _ahtola.Open();
        _sqlite.Open();
        Seed(_ahtola);
        Seed(_sqlite);
        var sql = Shape switch
        {
            "SorterRecordRoundTrip" => "SELECT txt, payload FROM rows ORDER BY sort_key DESC",
            "GroupConcatText" => "SELECT group_concat(txt, '|') FROM rows",
            "MaxTextReplacement" => "SELECT max(txt) FROM rows",
            "LastValueBlobCapture" => "SELECT last_value(payload) OVER (ORDER BY id) FROM rows",
            _ => throw new InvalidOperationException(Shape),
        };
        _ahtolaCommand = TursoScalarSupport.Prepared(_ahtola, sql);
        _sqliteCommand = TursoScalarSupport.Prepared(_sqlite, sql);
        _ = TursoScalarSupport.ReadAll(_ahtolaCommand);
        _ = TursoScalarSupport.ReadAll(_sqliteCommand);
    }

    [Benchmark, BenchmarkCategory("RecordRecycling")]
    public int Ahtola() => TursoScalarSupport.ReadAll(_ahtolaCommand);

    [Benchmark(Baseline = true), BenchmarkCategory("RecordRecycling")]
    public int MicrosoftDataSqlite() => TursoScalarSupport.ReadAll(_sqliteCommand);

    [GlobalCleanup]
    public void Cleanup()
    {
        _ahtolaCommand.Dispose();
        _sqliteCommand.Dispose();
        _ahtola.Dispose();
        _sqlite.Dispose();
    }

    private static void Seed(DbConnection connection)
    {
        TursoPrepareSupport.Execute(connection, """
            CREATE TABLE rows(id INTEGER PRIMARY KEY, sort_key INTEGER NOT NULL, txt TEXT NOT NULL, payload BLOB NOT NULL)
            """);
        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO rows(id, sort_key, txt, payload) VALUES (@id, @sort, @txt, zeroblob(128))";
        AddParameter(insert, "@id");
        AddParameter(insert, "@sort");
        AddParameter(insert, "@txt");
        insert.Prepare();
        for (var i = 0; i < RowCount; i++)
        {
            insert.Parameters[0].Value = i;
            insert.Parameters[1].Value = (i * 1_103) % RowCount;
            insert.Parameters[2].Value = $"value-{i:D6}-{new string('x', 96)}";
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void AddParameter(DbCommand command, string name)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        command.Parameters.Add(parameter);
    }
}
