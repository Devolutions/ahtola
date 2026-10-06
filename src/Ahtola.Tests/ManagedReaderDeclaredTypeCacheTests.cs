using System.Data;
using AwesomeAssertions;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// The data reader reuses the declared-type metadata it reads for a source table while the
/// engine reports the same schema for it. These pin that every schema change the pragmas would
/// observe is observed through the reused metadata too.
/// </summary>
public sealed class ManagedReaderDeclaredTypeCacheTests
{
    private static readonly Guid Value = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "ahtola-decltype-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Test]
    public void TempTableShadowingAndDroppingIsObserved()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery($"""
            CREATE TABLE t(id INTEGER PRIMARY KEY, a TEXT);
            INSERT INTO t VALUES (1, '{Value}');
            """);

        ReadFirstValue(connection).Should().Be(Value.ToString());

        connection.ExecuteNonQuery($"""
            CREATE TEMP TABLE t(id INTEGER PRIMARY KEY, a GUID);
            INSERT INTO temp.t VALUES (1, '{Value}');
            """);
        ReadFirstValue(connection).Should().Be(Value);

        connection.ExecuteNonQuery("DROP TABLE temp.t;");
        ReadFirstValue(connection).Should().Be(Value.ToString());
    }

    [Test]
    public void AddedColumnsAndIndexesAreObserved()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("""
            CREATE TABLE t(id INTEGER PRIMARY KEY, a TEXT);
            INSERT INTO t VALUES (1, 'x');
            """);

        ReadSchema(connection).Should().Equal(("id", "INTEGER", true, false), ("a", "TEXT", false, false));

        connection.ExecuteNonQuery($"ALTER TABLE t ADD COLUMN b GUID; UPDATE t SET b = '{Value}';");
        ReadSchema(connection).Should().Equal(
            ("id", "INTEGER", true, false), ("a", "TEXT", false, false), ("b", "GUID", false, false));
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT b FROM t;";
            command.ExecuteScalar().Should().Be(Value);
        }

        connection.ExecuteNonQuery("CREATE UNIQUE INDEX ux_t_a ON t(a);");
        ReadSchema(connection).Should().Equal(
            ("id", "INTEGER", true, false), ("a", "TEXT", false, true), ("b", "GUID", false, false));

        connection.ExecuteNonQuery("DROP INDEX ux_t_a;");
        ReadSchema(connection).Should().Equal(
            ("id", "INTEGER", true, false), ("a", "TEXT", false, false), ("b", "GUID", false, false));
    }

    [Test]
    public void RolledBackSchemaChangesAreObserved()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery($"""
            CREATE TABLE t(id INTEGER PRIMARY KEY, a TEXT);
            INSERT INTO t VALUES (1, '{Value}');
            """);
        ReadFirstValue(connection).Should().Be(Value.ToString());

        using (var transaction = connection.BeginTransaction())
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"""
                    DROP TABLE t;
                    CREATE TABLE t(id INTEGER PRIMARY KEY, a GUID);
                    INSERT INTO t VALUES (1, '{Value}');
                    """;
                command.ExecuteNonQuery();
                command.CommandText = "SELECT a FROM t;";
                command.ExecuteScalar().Should().Be(Value);
            }

            transaction.Rollback();
        }

        ReadFirstValue(connection).Should().Be(Value.ToString());
    }

    [Test]
    public void SchemaChangesFromAnotherConnectionAreObserved()
    {
        var path = Path.Combine(_root, "shared.db");
        using var reader = new SqliteConnection($"Data Source={path}");
        reader.Open();
        reader.ExecuteNonQuery($"""
            CREATE TABLE t(id INTEGER PRIMARY KEY, a TEXT);
            INSERT INTO t VALUES (1, '{Value}');
            """);
        ReadFirstValue(reader).Should().Be(Value.ToString());

        using (var writer = new SqliteConnection($"Data Source={path}"))
        {
            writer.Open();
            writer.ExecuteNonQuery($"""
                DROP TABLE t;
                CREATE TABLE t(id INTEGER PRIMARY KEY, a GUID);
                INSERT INTO t VALUES (1, '{Value}');
                """);
        }

        ReadFirstValue(reader).Should().Be(Value);
    }

    private static object ReadFirstValue(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, a FROM t;";
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        reader.GetDataTypeName(1).Should().Be(reader.GetValue(1) is Guid ? "GUID" : "TEXT");
        return reader.GetValue(1);
    }

    private static List<(string Name, string Type, bool IsKey, bool IsUnique)> ReadSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM t;";
        using var reader = command.ExecuteReader();
        var schema = reader.GetSchemaTable();
        return schema.Rows.Cast<DataRow>()
            .Select(row => (
                (string)row["ColumnName"],
                (string)row["DataTypeName"],
                row["IsKey"] is true,
                row["IsUnique"] is true))
            .ToList();
    }
}
