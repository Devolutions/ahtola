using System.Data.Common;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace Benchmarks;

/// <summary>
/// Ports the multi-row and multiple-trigger workloads from Turso
/// <c>core/benches/triggers.rs</c> at the repository-pinned revision.
/// </summary>
[BenchmarkCategory("Write")]
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class TursoTriggerBenchmarks : TursoWriteBenchmarkSupport
{
    [Params(1, 4)]
    public int TriggerCount { get; set; }

    protected override void Configure(DbConnection connection)
    {
        Execute(connection, "PRAGMA synchronous=OFF");
        Execute(connection, "CREATE TABLE src(id INTEGER PRIMARY KEY, a TEXT, b INTEGER, c REAL, d TEXT)");
        for (var trigger = 0; trigger < TriggerCount; trigger++)
        {
            Execute(connection, $"CREATE TABLE log{trigger}(src_id INTEGER, value TEXT)");
            Execute(
                connection,
                $"CREATE TRIGGER trg{trigger} AFTER INSERT ON src "
                + $"BEGIN INSERT INTO log{trigger} VALUES (NEW.id, NEW.a); END");
        }
    }

    [BenchmarkCategory("multiple-after-triggers")]
    [Benchmark(Baseline = true, Description = "Microsoft.Data.Sqlite: 500 rows through AFTER triggers")]
    public long NativeAfterTriggers() => InsertTriggeredRows(Native, 500);

    [BenchmarkCategory("multiple-after-triggers")]
    [Benchmark(Description = "Ahtola: 500 rows through AFTER triggers")]
    public long ManagedAfterTriggers() => InsertTriggeredRows(Managed, 500);

    private long InsertTriggeredRows(DbConnection connection, int rows)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO src VALUES ($id, $a, $b, $c, $d)";
        var parameters = Enumerable.Range(0, 5).Select(index =>
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = new[] { "$id", "$a", "$b", "$c", "$d" }[index];
            command.Parameters.Add(parameter);
            return parameter;
        }).ToArray();
        for (var row = 0; row < rows; row++)
        {
            parameters[0].Value = row;
            parameters[1].Value = "text-" + row;
            parameters[2].Value = row;
            parameters[3].Value = row + 0.5;
            parameters[4].Value = "extra-" + row;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return rows + Scalar(connection, "SELECT count(*) FROM log0");
    }
}

/// <summary>
/// Ports Turso's sparse-trigger wide-table case from
/// <c>core/benches/triggers.rs</c>; only <c>NEW.c0</c> is consumed by the trigger.
/// </summary>
[BenchmarkCategory("Write")]
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class TursoTriggerWideTableBenchmarks : TursoWriteBenchmarkSupport
{
    [Params(10, 50)]
    public int ColumnCount { get; set; }

    protected override void Configure(DbConnection connection)
    {
        Execute(connection, "PRAGMA synchronous=OFF");
        var columns = string.Join(", ", Enumerable.Range(0, ColumnCount).Select(index => $"c{index} TEXT"));
        Execute(connection, $"CREATE TABLE wide(id INTEGER PRIMARY KEY, {columns})");
        Execute(connection, "CREATE TABLE audit_wide(src_id INTEGER, first_col TEXT)");
        Execute(
            connection,
            "CREATE TRIGGER trg_wide AFTER INSERT ON wide "
            + "BEGIN INSERT INTO audit_wide VALUES (NEW.id, NEW.c0); END");
    }

    [BenchmarkCategory("wide-sparse-trigger")]
    [Benchmark(Baseline = true, Description = "Microsoft.Data.Sqlite: sparse trigger on wide rows")]
    public long NativeWideTrigger() => InsertWideRows(Native);

    [BenchmarkCategory("wide-sparse-trigger")]
    [Benchmark(Description = "Ahtola: sparse trigger on wide rows")]
    public long ManagedWideTrigger() => InsertWideRows(Managed);

    private long InsertWideRows(DbConnection connection)
    {
        const int Rows = 200;
        var names = Enumerable.Range(0, ColumnCount).Select(index => $"c{index}").ToArray();
        var values = Enumerable.Range(0, ColumnCount).Select(index => $"$c{index}").ToArray();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"INSERT INTO wide(id, {string.Join(", ", names)}) VALUES ($id, {string.Join(", ", values)})";
        var id = command.CreateParameter();
        id.ParameterName = "$id";
        command.Parameters.Add(id);
        var parameters = values.Select(name =>
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            command.Parameters.Add(parameter);
            return parameter;
        }).ToArray();

        for (var row = 0; row < Rows; row++)
        {
            id.Value = row;
            for (var column = 0; column < parameters.Length; column++)
                parameters[column].Value = $"row{row}-column{column}";
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return Rows + Scalar(connection, "SELECT count(*) FROM audit_wide");
    }
}

/// <summary>
/// Ports the BEFORE-trigger validation workload from Turso
/// <c>core/benches/triggers.rs</c>.
/// </summary>
[BenchmarkCategory("Write")]
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class TursoTriggerBeforeBenchmarks : TursoWriteBenchmarkSupport
{
    protected override void Configure(DbConnection connection)
    {
        Execute(connection, "PRAGMA synchronous=OFF");
        Execute(connection, "CREATE TABLE src(id INTEGER PRIMARY KEY, val TEXT, status TEXT)");
        Execute(
            connection,
            "CREATE TRIGGER trg_before BEFORE INSERT ON src "
            + "BEGIN SELECT CASE WHEN NEW.val IS NULL THEN RAISE(ABORT, 'val required') END; END");
    }

    [BenchmarkCategory("before-trigger")]
    [Benchmark(Baseline = true, Description = "Microsoft.Data.Sqlite: BEFORE trigger validation")]
    public int NativeBeforeTrigger() => InsertRows(Native);

    [BenchmarkCategory("before-trigger")]
    [Benchmark(Description = "Ahtola: BEFORE trigger validation")]
    public int ManagedBeforeTrigger() => InsertRows(Managed);

    private static int InsertRows(DbConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO src VALUES ($id, $val, 'active')";
        var id = command.CreateParameter();
        id.ParameterName = "$id";
        command.Parameters.Add(id);
        var value = command.CreateParameter();
        value.ParameterName = "$val";
        command.Parameters.Add(value);
        var affected = 0;
        for (var row = 0; row < 500; row++)
        {
            id.Value = row;
            value.Value = "value-" + row;
            affected += command.ExecuteNonQuery();
        }

        transaction.Commit();
        return affected;
    }
}
