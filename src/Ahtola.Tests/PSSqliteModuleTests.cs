using System.Collections;
using System.Collections.Specialized;
using System.Data;
using AwesomeAssertions;
using Ahtola.Data.Sqlite;
using Ahtola.PSSqlite;

namespace Ahtola.Tests;

/// <summary>
/// Smoke coverage for the Ahtola-backed clone of the synedgy.PSSqlite C# module.
/// Exercises library services directly (not the PowerShell host).
/// </summary>
public sealed class PSSqliteModuleTests
{
    [Test]
    public void ConnectionFactory_Creates_Openable_Memory_Connection()
    {
        using var connection = ConnectionFactory.Create("Data Source=:memory:");
        connection.Open();
        connection.State.Should().Be(ConnectionState.Open);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1;";
        Convert.ToInt64(command.ExecuteScalar()).Should().Be(1);
    }

    [Test]
    public void QueryExecutor_Returns_DataTable_And_Parameters()
    {
        using var connection = ConnectionFactory.Create("Data Source=:memory:");
        connection.Open();

        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT); INSERT INTO t(name) VALUES ('a'), ('b');";
            setup.ExecuteNonQuery();
        }

        var table = QueryExecutor.Execute(
            connection,
            "SELECT id, name FROM t WHERE name = $name ORDER BY id;",
            new Hashtable { ["$name"] = "b" },
            new QueryOptions { OutputFormat = "DataTable" }) as DataTable;

        table.Should().NotBeNull();
        table!.Rows.Count.Should().Be(1);
        table.Rows[0]["name"].Should().Be("b");
    }

    [Test]
    public void QueryExecutor_Uses_Transaction_And_Leaves_Caller_Connection_Open()
    {
        using var connection = ConnectionFactory.Create("Data Source=:memory:");
        connection.Open();

        QueryExecutor.Execute(
            connection,
            "CREATE TABLE values_table(id INTEGER PRIMARY KEY, value TEXT);",
            parameters: null,
            new QueryOptions { OutputFormat = "NonQuery" }).Should().Be(0);
        connection.State.Should().Be(ConnectionState.Open);

        using (var transaction = connection.BeginTransaction())
        {
            QueryExecutor.Execute(
                connection,
                "INSERT INTO values_table(id, value) VALUES ($id, $value);",
                new Hashtable { ["$id"] = 1, ["$value"] = "rolled-back" },
                new QueryOptions { OutputFormat = "NonQuery", Transaction = transaction }).Should().Be(1);
            transaction.Rollback();
        }

        QueryExecutor.Execute(
            connection,
            "SELECT COUNT(*) FROM values_table;",
            parameters: null,
            new QueryOptions { OutputFormat = "Scalar" }).Should().Be(0L);
        connection.State.Should().Be(ConnectionState.Open);
    }

    [Test]
    public void Initialize_And_Crud_RoundTrip_From_Yaml_Config()
    {
        var root = Path.Combine(Path.GetTempPath(), "ahtola-pssqlite-" + Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(root, "db");
        var configPath = Path.Combine(root, "Pester.PSSqliteConfig.yml");
        Directory.CreateDirectory(dbPath);

        try
        {
            File.WriteAllText(configPath, $"""
                DatabasePath: '{dbPath.Replace('\\', '/')}'
                DatabaseFile: 'Pester.sqlite'
                Version: '1.2.3'
                Schema:
                  Tables:
                    Cars:
                      Columns:
                        Id:
                          Type: INTEGER
                          PrimaryKey: true
                          AllowNull: false
                        Make:
                          Type: TEXT
                        Colour:
                          Type: TEXT
                        Year:
                          Type: INTEGER
                """);

            var config = SQLiteDBConfig.Load(configPath);
            config.Schema.Should().NotBeNull();
            config.Schema!.ValidateDefinition();

            DatabaseInitializer.Initialize(config, DBMigrationMode.CREATE, force: false);

            var dbFile = Path.Combine(config.DatabasePath!, config.DatabaseFile!);
            File.Exists(dbFile).Should().BeTrue();

            var compare = DatabaseVersion.Compare(config, "1.2.3");
            compare.IsDeployed.Should().BeTrue();
            compare.CurrentVersion.Should().Be("1.2.3");
            compare.ExpectedVersion.Should().Be("1.2.3");

            using var connection = ConnectionFactory.Create(config.ConnectionString!);
            connection.Open();

            var inserted = CrudSqlBuilder.ExecuteInsert(
                config,
                "Cars",
                new Hashtable
                {
                    ["Id"] = 1,
                    ["Make"] = "DeLorean",
                    ["Colour"] = "Silver",
                    ["Year"] = 1981
                },
                connection,
                warning: _ => { });

            inserted.Should().NotBeNull();

            var selected = CrudSqlBuilder.ExecuteSelect(
                config,
                "Cars",
                new Hashtable { ["Make"] = "DeLorean" },
                connection,
                outputFormat: "OrderedDictionary",
                caseSensitive: false,
                warning: _ => { });

            selected.Should().NotBeNull();
            var rows = ((IEnumerable)selected!).Cast<object>().ToArray();
            rows.Length.Should().Be(1);
            var row = (OrderedDictionary)rows[0];
            row["Make"].Should().Be("DeLorean");
            Convert.ToInt64(row["Year"]).Should().Be(1981);

            CrudSqlBuilder.ExecuteUpdate(
                config,
                "Cars",
                new Hashtable { ["Colour"] = "Stainless" },
                new Hashtable { ["Id"] = 1 },
                connection,
                caseSensitive: false,
                warning: _ => { });

            CrudSqlBuilder.ExecuteUpdate(
                config,
                "Cars",
                new Hashtable { ["Id"] = 1, ["Year"] = 1982 },
                new Hashtable { ["Id"] = 1 },
                connection,
                caseSensitive: false,
                onConflict: "UPSERT",
                warning: _ => { });

            CrudSqlBuilder.ExecuteUpdate(
                config,
                "Cars",
                new Hashtable { ["Colour"] = null },
                new Hashtable { ["Id"] = 1 },
                connection,
                caseSensitive: false,
                warning: _ => { });

            var updated = CrudSqlBuilder.ExecuteSelect(
                config,
                "Cars",
                new Hashtable { ["Id"] = 1 },
                connection,
                outputFormat: "OrderedDictionary",
                caseSensitive: false,
                warning: _ => { });
            var updatedRow = ((IEnumerable)updated!).Cast<OrderedDictionary>().Single();
            updatedRow["Year"].Should().Be(1982L);
            updatedRow["Colour"].Should().BeNull();

            var metadata = MetadataStore.Get(config.ConnectionString!, new[] { "version" });
            metadata.Should().NotBeNull();
            metadata!["version"].Should().Be("1.2.3");

            CrudSqlBuilder.ExecuteDelete(
                config,
                "Cars",
                new Hashtable { ["Id"] = 1 },
                connection,
                caseSensitive: false,
                warning: _ => { });

            var afterDelete = CrudSqlBuilder.ExecuteSelect(
                config,
                "Cars",
                null,
                connection,
                outputFormat: "DataTable",
                caseSensitive: false,
                warning: _ => { }) as DataTable;

            afterDelete.Should().NotBeNull();
            afterDelete!.Rows.Count.Should().Be(0);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public void QueryExecutor_Opens_And_Retains_Caller_Owned_Connection()
    {
        using var connection = ConnectionFactory.Create("Data Source=:memory:");

        var result = QueryExecutor.Execute(
            connection,
            "SELECT 17;",
            parameters: null,
            new QueryOptions { OutputFormat = "Scalar" });

        Convert.ToInt64(result).Should().Be(17);
        connection.State.Should().Be(ConnectionState.Open);
    }
}
