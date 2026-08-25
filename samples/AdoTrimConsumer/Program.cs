using System.Data;
using System.Data.Common;
using System.Globalization;
using Ahtola;
using Ahtola.Data.Sqlite;

// Runnable ADO-only trim/AOT probe. Every check below exercises a surface whose trim contract was
// non-trivial to get right, so a regression shows up either as an IL2xxx/IL3xxx warning during
// publish or as a failure here after trimming / NativeAOT compilation.
try
{
    VerifySchemaTableAndFieldTypes();
    VerifyTupleAccumulatorAggregate();
    VerifyAdapterFill();
    VerifyNativeProviderFailsClosed();
    Console.WriteLine("PASS: ado-trim-consumer");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FAIL: ado-trim-consumer: {exception}");
    return 1;
}

static void VerifySchemaTableAndFieldTypes()
{
    // The facade reader and the inner Ahtola reader build their schema tables through different
    // code paths, so both are exercised.
    using (var connection = OpenSeeded())
    using (var command = connection.CreateCommand())
    {
        command.CommandText = "SELECT id, value, label FROM probe;";
        using DbDataReader reader = command.ExecuteReader();
        AssertSchema(reader.GetSchemaTable());

        if (!reader.Read())
            throw new InvalidOperationException("The reader produced no rows.");
        if (reader.GetFieldType(0) != typeof(long) || reader.GetFieldType(2) != typeof(string))
            throw new InvalidOperationException("GetFieldType reported unexpected CLR types.");
    }

    using (var connection = new AhtolaConnection("Data Source=:memory:;Mode=Memory"))
    {
        connection.Open();
        foreach (var statement in new[]
                 {
                     "CREATE TABLE probe(id INTEGER PRIMARY KEY, value INTEGER NOT NULL, label TEXT);",
                     "INSERT INTO probe(value, label) VALUES (10, 'a');",
                 })
        {
            using var seed = connection.CreateCommand();
            seed.CommandText = statement;
            seed.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, value, label FROM probe;";
        using var reader = command.ExecuteReader();
        AssertSchema(reader.GetSchemaTable());
    }

    static void AssertSchema(DataTable? schema)
    {
        if (schema is null || schema.Rows.Count != 3)
            throw new InvalidOperationException("The schema table lost a column.");

        // The declared column type is part of the ADO.NET metadata contract, not just the values.
        if (schema.Columns[SchemaTableColumn.DataType]?.DataType != typeof(Type))
            throw new InvalidOperationException("The schema table's DataType column is not typed System.Type.");

        foreach (DataRow row in schema.Rows)
        {
            if (row[SchemaTableColumn.DataType] is not Type)
                throw new InvalidOperationException("The schema table lost its CLR Type values.");
        }
    }
}

// Reconstructing a ValueTuple accumulator goes through Activator.CreateInstance, which only works
// after trimming/AOT because CreateAggregate's TAccumulate is annotated with
// [DynamicallyAccessedMembers(PublicConstructors)].
static void VerifyTupleAccumulatorAggregate()
{
    using var connection = OpenSeeded();
    connection.CreateAggregate<decimal, (decimal Sum, ulong Count), decimal?>(
        "probe_avg",
        (Sum: 0m, Count: 0UL),
        static (accumulator, value) => (accumulator.Sum + value, accumulator.Count + 1),
        static accumulator => accumulator.Count == 0 ? null : accumulator.Sum / accumulator.Count);

    using var command = connection.CreateCommand();
    command.CommandText = "SELECT probe_avg(value) FROM probe;";
    var average = Convert.ToDecimal(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    if (average != 20m)
        throw new InvalidOperationException($"The tuple accumulator aggregate returned {average}, expected 20.");
}

// DbDataAdapter types its DataTable columns from the schema table's DataType values.
static void VerifyAdapterFill()
{
    using var connection = OpenSeeded();
    using var adapter = new AhtolaDataAdapter("SELECT id, value, label FROM probe;", connection);
    var table = new DataTable();
    adapter.Fill(table);

    var types = table.Columns.Cast<DataColumn>().Select(static column => column.DataType).ToArray();
    if (types.Length != 3 || types[0] != typeof(long) || types[2] != typeof(string))
        throw new InvalidOperationException("The adapter fill produced unexpected column types.");
    if (table.Rows.Count != 3)
        throw new InvalidOperationException("The adapter fill produced unexpected row count.");
}

static void VerifyNativeProviderFailsClosed()
{
    try
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Mode=Memory;Local Provider=Native");
        connection.Open();
    }
    catch (NotSupportedException)
    {
        return;
    }

    throw new InvalidOperationException("Local Provider=Native must fail closed without a registered factory.");
}

static SqliteConnection OpenSeeded()
{
    var connection = new SqliteConnection("Data Source=:memory:;Mode=Memory");
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = """
        CREATE TABLE probe(id INTEGER PRIMARY KEY, value INTEGER NOT NULL, label TEXT);
        INSERT INTO probe(value, label) VALUES (10, 'a'), (20, 'b'), (30, 'c');
        """;
    command.ExecuteNonQuery();
    return connection;
}
