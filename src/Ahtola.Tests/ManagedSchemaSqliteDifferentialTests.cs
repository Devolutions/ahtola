using System.Data;
using System.Data.Common;
using AwesomeAssertions;
using Ahtola.Data.Sqlite;
using Mds = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// Re-checks the schema and disconnected-ADO.NET surface against <c>Microsoft.Data.Sqlite</c> at
/// test time, so the claims behind <see cref="AhtolaDataAdapter"/> and <c>GetSchema</c> stay
/// measured rather than remembered. Every assertion here compares against a value read from the
/// reference provider in the same test run; nothing is transcribed from the ADO.NET
/// specification, because each shape this port needed contradicted at least one reasonable
/// reading of it.
/// </summary>
public sealed class ManagedSchemaSqliteDifferentialTests
{
    private const string CreateSql = "CREATE TABLE person(id INTEGER PRIMARY KEY, name TEXT NOT NULL, score INTEGER)";
    private const string InsertSql = "INSERT INTO person(id, name, score) VALUES (1,'ada',10),(2,'grace',20),(3,'alan',30)";
    private const string SelectSql = "SELECT id, name, score FROM person ORDER BY id";

    /// <summary>
    /// A minimal adapter over the reference provider. <see cref="DbDataAdapter"/> has no abstract
    /// members, so this is the whole thing: it isolates <c>System.Data</c>'s own behavior from
    /// anything this port adds.
    /// </summary>
    private sealed class ReferenceAdapter : DbDataAdapter
    {
        internal ReferenceAdapter(DbConnection connection)
        {
            var command = connection.CreateCommand();
            command.CommandText = SelectSql;
            SelectCommand = command;
        }
    }

    [Test]
    public void ReservedWordsMatchesTheReferenceProviderExactly()
    {
        // A caller uses this list to decide which identifiers need quoting, so a short list
        // produces invalid SQL rather than a cosmetic difference.
        ManagedWords().Should().BeEquivalentTo(Reference());

        static IEnumerable<string> ManagedWords()
        {
            using var connection = OpenAhtola(seed: false);
            return Words(connection.GetSchema("ReservedWords"));
        }

        static IEnumerable<string> Reference()
        {
            using var connection = OpenReference(seed: false);
            return Words(connection.GetSchema("ReservedWords"));
        }

        static List<string> Words(DataTable table)
            => table.Rows.Cast<DataRow>().Select(row => (string)row["ReservedWord"]).ToList();
    }

    [Test]
    public void MetaDataCollectionsUsesTheReferenceColumnShape()
    {
        using var connection = OpenAhtola(seed: false);
        using var reference = OpenReference(seed: false);

        Describe(connection.GetSchema("MetaDataCollections")).Should().Equal(Describe(reference.GetSchema("MetaDataCollections")));

        // The row set is deliberately a superset: the reference provider defines only the two
        // constant collections, while this port also answers the four catalog collections.
        Collections(reference.GetSchema("MetaDataCollections"))
            .Should().Equal("MetaDataCollections", "ReservedWords");
        Collections(connection.GetSchema("MetaDataCollections"))
            .Should().Contain(["MetaDataCollections", "ReservedWords", "Tables", "Columns", "Indexes", "IndexColumns"]);

        foreach (var superset in new[] { "Tables", "Columns", "Indexes", "IndexColumns" })
            Assert.Throws<ArgumentException>(() => reference.GetSchema(superset));

        static List<string> Describe(DataTable table)
            => table.Columns.Cast<DataColumn>().Select(column => $"{column.ColumnName}:{column.DataType.Name}").ToList();

        static List<string> Collections(DataTable table)
            => table.Rows.Cast<DataRow>().Select(row => (string)row["CollectionName"]).ToList();
    }

    [Test]
    public void ReaderTypeMetadataBeforeTheFirstReadMatchesTheReferenceProvider()
    {
        // DbDataAdapter maps the result schema before fetching a row. Answering typeof(object)
        // here silently produces an untyped DataTable; throwing here breaks Fill outright.
        using var connection = OpenAhtola();
        using var reference = OpenReference();

        Probe(connection).Should().Equal(Probe(reference));

        static List<string> Probe(DbConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = SelectSql;
            using var reader = command.ExecuteReader();
            return Enumerable.Range(0, reader.FieldCount)
                .Select(i => $"{reader.GetName(i)}:{reader.GetFieldType(i).Name}:{reader.GetDataTypeName(i)}")
                .ToList();
        }
    }

    [Test]
    public void ReaderSchemaTableMatchesTheReferenceProviderOnTheColumnsTheBuilderReads()
    {
        using var connection = OpenAhtola();
        using var reference = OpenReference();

        // IsKey is what AhtolaCommandBuilder uses to build the WHERE clause, and AllowDBNull and
        // ColumnSize are what it uses to bind parameters, so those four have to agree.
        Probe(connection).Should().Equal(Probe(reference));

        static List<string> Probe(DbConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = SelectSql;
            using var reader = command.ExecuteReader();
            var schema = reader.GetSchemaTable()!;
            return schema.Rows.Cast<DataRow>()
                .Select(row => string.Join(
                    "|",
                    row["ColumnName"],
                    row["DataType"],
                    row["ColumnSize"],
                    row["IsKey"],
                    row["IsUnique"],
                    row["AllowDBNull"],
                    row["BaseTableName"]))
                .ToList();
        }
    }

    [Test]
    public void AdapterFillProducesTheReferenceColumnTypes()
    {
        using var connection = OpenAhtola();
        using var reference = OpenReference();

        Probe(connection, connection => new AhtolaDataAdapter(SelectSql, connection))
            .Should().Equal(Probe(reference, connection => new ReferenceAdapter(connection)));

        static List<string> Probe(DbConnection connection, Func<DbConnection, DbDataAdapter> factory)
        {
            using var adapter = factory(connection);
            var dataSet = new DataSet();
            adapter.Fill(dataSet, "person");
            return dataSet.Tables["person"]!.Columns.Cast<DataColumn>()
                .Select(column => $"{column.ColumnName}:{column.DataType.Name}:{column.AllowDBNull}")
                .ToList();
        }
    }

    [Test]
    public void FillSchemaInfersTheSameKeyAndNullabilityAsTheReferenceProvider()
    {
        using var connection = OpenAhtola();
        using var reference = OpenReference();

        // This is the assertion that corrected an assumption: a rowid-alias INTEGER PRIMARY KEY
        // does not become a DataTable primary key, because SQLite publishes IsUnique=False for it
        // and System.Data declines the inference. Matching the reference provider is preferred
        // over inventing uniqueness metadata the engine cannot support.
        Probe(connection, connection => new AhtolaDataAdapter(SelectSql, connection))
            .Should().Equal(Probe(reference, connection => new ReferenceAdapter(connection)));

        static List<string> Probe(DbConnection connection, Func<DbConnection, DbDataAdapter> factory)
        {
            using var adapter = factory(connection);
            var dataSet = new DataSet();
            adapter.FillSchema(dataSet, SchemaType.Source, "person");
            var table = dataSet.Tables["person"]!;
            var described = table.Columns.Cast<DataColumn>()
                .Select(column => $"{column.ColumnName}:{column.DataType.Name}:null={column.AllowDBNull}:ro={column.ReadOnly}:max={column.MaxLength}")
                .ToList();
            described.Insert(0, $"primaryKeyLength={table.PrimaryKey.Length}");
            return described;
        }
    }

    private static AhtolaConnection OpenAhtola(bool seed = true)
    {
        var connection = new AhtolaConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        if (seed)
            Seed(connection);

        return connection;
    }

    private static Mds.SqliteConnection OpenReference(bool seed = true)
    {
        var connection = new Mds.SqliteConnection("Data Source=:memory:");
        connection.Open();
        if (seed)
            Seed(connection);

        return connection;
    }

    private static void Seed(DbConnection connection)
    {
        foreach (var sql in new[] { CreateSql, InsertSql })
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
}
