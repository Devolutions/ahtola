using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Ahtola;
using Ahtola.Data.Sqlite;
using Ahtola.Data.Sqlite.Browser;

// ADO-only browser trim consumer.
//
// The gate (scripts/Invoke-BrowserTrimAnalysis.ps1) publishes this project with
// SuppressTrimAnalysisWarnings=false and TrimmerSingleWarn=false and requires zero IL2xxx/IL3xxx
// warnings from the entire closure. Every call below exists to root a code path that the trimmer
// would otherwise drop, so the gate actually analyses the reader/schema/aggregate/native-provider
// surfaces rather than an empty program.
//
// This entry point deliberately does not start a Blazor host: Blazor's component activation and
// JS interop dispatch are reflection-based and produce their own upstream IL2xxx warnings, which
// would drown out the signal this gate exists to protect. Runnable browser behaviour is covered by
// samples/BrowserWasmConsumer, which the browser package smoke test actually executes.
var report = new StringBuilder();
report.Append("ado=").Append(RunAdo());
report.Append(";schema=").Append(RunSchemaTable());
report.Append(";field-types=").Append(RunFieldTypes());
report.Append(";tuple-aggregate=").Append(RunTupleAggregate());
report.Append(";adapter=").Append(RunAdapterFill());
report.Append(";native-fails-closed=").Append(RunNativeProviderFailsClosed());
report.Append(";browser-options=").Append(DescribeBrowserOptions());
Console.WriteLine(report.ToString());

static long RunAdo()
{
    using var connection = OpenSeeded();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT SUM(value) FROM probe;";
    return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
}

// Roots DbDataReader.GetSchemaTable on both the facade reader and the inner Ahtola reader.
static int RunSchemaTable()
{
    var columns = 0;
    using (var connection = OpenSeeded())
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, value, label FROM probe;";
        using var reader = command.ExecuteReader();
        DataTable? schema = reader.GetSchemaTable();
        columns += schema?.Rows.Count ?? 0;
        if (schema is not null && schema.Rows.Count > 0)
        {
            // The DataType column carries CLR Type instances; read one back the way
            // DbDataAdapter and DbCommandBuilder do.
            var dataType = (Type)schema.Rows[0][SchemaTableColumn.DataType];
            columns += dataType is null ? 0 : 0;
        }
    }

    using (var connection = new AhtolaConnection("Data Source=:memory:;Mode=Memory"))
    {
        connection.Open();
        using var seed = connection.CreateCommand();
        seed.CommandText = "CREATE TABLE probe(id INTEGER PRIMARY KEY, value INTEGER NOT NULL);"
            + "INSERT INTO probe(value) VALUES (7);";
        seed.ExecuteNonQuery();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, value FROM probe;";
        using var reader = command.ExecuteReader();
        columns += reader.GetSchemaTable()?.Rows.Count ?? 0;
    }

    return columns;
}

// Roots the annotated GetFieldType overrides on every reader in the ADO stack.
static string RunFieldTypes()
{
    using var connection = OpenSeeded();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT id, value, label FROM probe;";
    using DbDataReader reader = command.ExecuteReader();
    var names = new List<string>(reader.FieldCount);
    while (reader.Read())
    {
        for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            names.Add(reader.GetFieldType(ordinal).Name);

        break;
    }

    return string.Join('/', names);
}

// Roots the tuple accumulator path (CoerceAccumulator -> DecodeTuple -> Activator.CreateInstance)
// with the same shape EF Core's ef_avg uses: (decimal sum, ulong count).
static string RunTupleAggregate()
{
    using var connection = OpenSeeded();
    connection.CreateAggregate<decimal, (decimal Sum, ulong Count), decimal?>(
        "probe_avg",
        (Sum: 0m, Count: 0UL),
        static (accumulator, value) => (accumulator.Sum + value, accumulator.Count + 1),
        static accumulator => accumulator.Count == 0 ? null : accumulator.Sum / accumulator.Count);

    using var command = connection.CreateCommand();
    command.CommandText = "SELECT probe_avg(value) FROM probe;";
    return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "null";
}

// DbDataAdapter reads GetSchemaTable's DataType column to type its DataTable columns.
static string RunAdapterFill()
{
    using var connection = OpenSeeded();
    using var adapter = new AhtolaDataAdapter("SELECT id, value, label FROM probe;", connection);
    var table = new DataTable();
    adapter.Fill(table);
    return string.Join('/', table.Columns.Cast<DataColumn>().Select(static column => column.DataType.Name));
}

// Provider=Native must fail closed with the product message and without probing for the companion.
static bool RunNativeProviderFailsClosed()
{
    try
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Mode=Memory;Local Provider=Native");
        connection.Open();
        return false;
    }
    catch (NotSupportedException)
    {
        return true;
    }
}

static string DescribeBrowserOptions()
{
    using var options = new AhtolaBrowserOptions("trim-probe/ado.db");
    return options.SynchronousMode.ToString();
}

static SqliteConnection OpenSeeded()
{
    var connection = new SqliteConnection("Data Source=:memory:;Mode=Memory");
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = """
        CREATE TABLE probe(id INTEGER PRIMARY KEY, value INTEGER NOT NULL, label TEXT);
        INSERT INTO probe(value, label) VALUES (42, 'a'), (84, 'b');
        """;
    command.ExecuteNonQuery();
    return connection;
}
