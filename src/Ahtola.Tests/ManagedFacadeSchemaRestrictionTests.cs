using AwesomeAssertions;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public sealed class ManagedFacadeSchemaRestrictionTests
{
    [Test]
    public void ManagedFacadeHonorsCatalogSchemaAndTableTypeSchemaRestrictions()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        typeof(SqliteConnection)
            .GetField("_database", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(connection)
            .Should()
            .BeNull();
        connection.ExecuteNonQuery("""
            CREATE TABLE products(id INTEGER PRIMARY KEY, name TEXT);
            CREATE VIEW product_names AS SELECT name FROM products;
            CREATE INDEX ix_products_name ON products(name);
            """);

        var baseTables = connection.GetSchema("Tables", ["main", null, null, "BASE TABLE"]);
        baseTables.Rows.Cast<System.Data.DataRow>().Select(row => (string)row["TABLE_NAME"])
            .Should().Equal("products");

        var views = connection.GetSchema("Tables", ["main", null, null, "VIEW"]);
        views.Rows.Cast<System.Data.DataRow>().Select(row => (string)row["TABLE_NAME"])
            .Should().Equal("product_names");

        connection.GetSchema("Tables", ["other", null, null, null]).Rows.Count.Should().Be(0);
        connection.GetSchema("Tables", [null, "dbo", null, null]).Rows.Count.Should().Be(0);
        connection.GetSchema("Columns", ["other", null, "products", "name"]).Rows.Count.Should().Be(0);
        connection.GetSchema("Indexes", ["other", null, "products", "ix_products_name"]).Rows.Count.Should().Be(0);
        connection.GetSchema("IndexColumns", ["other", null, "products", "ix_products_name", "name"]).Rows.Count.Should().Be(0);
    }
}
