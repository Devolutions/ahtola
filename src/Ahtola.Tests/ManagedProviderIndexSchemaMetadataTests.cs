using System.Data;
using System.Data.Common;
using AwesomeAssertions;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public sealed class ManagedProviderIndexSchemaMetadataTests
{
    [Test]
    public void GetSchemaExposesManagedIndexMetadataAndColumns()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("""
            CREATE TABLE products(id INTEGER PRIMARY KEY, sku TEXT, region TEXT);
            CREATE UNIQUE INDEX ux_products_sku_region ON products(sku, region);
            CREATE INDEX ix_products_region ON products(region);
            """);

        var collections = connection.GetSchema();
        var indexesCollection = collections.Rows.Cast<DataRow>()
            .Single(row => (string)row["CollectionName"] == "Indexes");
        indexesCollection["NumberOfRestrictions"].Should().Be(4);
        indexesCollection["NumberOfIdentifierParts"].Should().Be(4);
        indexesCollection["NumberOfRestrictions"].GetType().Should().Be(typeof(int));

        var indexes = connection.GetSchema("Indexes");
        indexes.TableName.Should().Be("Indexes");
        indexes.Columns.Cast<DataColumn>().Select(column => (column.ColumnName, column.DataType))
            .Should().Equal(
                ("TABLE_CATALOG", typeof(string)),
                ("TABLE_SCHEMA", typeof(string)),
                ("TABLE_NAME", typeof(string)),
                ("INDEX_NAME", typeof(string)),
                ("IS_UNIQUE", typeof(bool)),
                ("ORIGIN", typeof(string)),
                ("IS_PARTIAL", typeof(bool)));

        var uniqueIndex = indexes.Rows.Cast<DataRow>()
            .Single(row => (string)row["INDEX_NAME"] == "ux_products_sku_region");
        uniqueIndex["TABLE_CATALOG"].Should().Be("main");
        uniqueIndex["TABLE_SCHEMA"].Should().Be(DBNull.Value);
        uniqueIndex["TABLE_NAME"].Should().Be("products");
        uniqueIndex["IS_UNIQUE"].Should().Be(true);
        uniqueIndex["IS_UNIQUE"].GetType().Should().Be(typeof(bool));
        uniqueIndex["ORIGIN"].Should().Be("c");
        uniqueIndex["IS_PARTIAL"].Should().Be(false);

        var indexColumns = connection.GetSchema("IndexColumns", [null, null, "products", "ux_products_sku_region"]);
        indexColumns.TableName.Should().Be("IndexColumns");
        indexColumns.Columns.Cast<DataColumn>().Select(column => (column.ColumnName, column.DataType))
            .Should().Equal(
                ("TABLE_CATALOG", typeof(string)),
                ("TABLE_SCHEMA", typeof(string)),
                ("TABLE_NAME", typeof(string)),
                ("INDEX_NAME", typeof(string)),
                ("ORDINAL_POSITION", typeof(int)),
                ("COLUMN_ORDINAL", typeof(int)),
                ("COLUMN_NAME", typeof(string)));
        indexColumns.Rows.Cast<DataRow>()
            .Select(row => ((int)row["ORDINAL_POSITION"], (int)row["COLUMN_ORDINAL"], (string)row["COLUMN_NAME"]))
            .Should().Equal((0, 1, "sku"), (1, 2, "region"));
    }

    [Test]
    public void GetSchemaFiltersManagedIndexMetadataAndRejectsExtraRestrictions()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("""
            CREATE TABLE products(id INTEGER PRIMARY KEY, sku TEXT, region TEXT);
            CREATE UNIQUE INDEX ux_products_sku_region ON products(sku, region);
            CREATE INDEX ix_products_region ON products(region);
            """);

        var filtered = connection.GetSchema("IndexColumns", [null, null, "PRODUCTS", "UX_PRODUCTS_SKU_REGION", "REGION"]);
        filtered.Rows.Cast<DataRow>().Should().ContainSingle();
        filtered.Rows[0]["COLUMN_NAME"].Should().Be("region");
        filtered.Rows[0]["ORDINAL_POSITION"].Should().Be(1);

        Assert.Throws<ArgumentException>(() => connection.GetSchema("Indexes", new string?[5]))!
            .Message.Should().Contain("Indexes");
        Assert.Throws<ArgumentException>(() => connection.GetSchema("IndexColumns", new string?[6]))!
            .Message.Should().Contain("IndexColumns");
    }

    [Test]
    public void GetSchemaRepresentsPartialExpressionIndexTermsWithoutInventingColumns()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery(
            """
            CREATE TABLE products(id INTEGER PRIMARY KEY, sku TEXT, active INTEGER);
            CREATE UNIQUE INDEX ux_products_normalized
                ON products(lower(sku) COLLATE NOCASE DESC)
                WHERE active = 1;
            """);

        var index = connection.GetSchema("Indexes").Rows.Cast<DataRow>()
            .Single(row => (string)row["INDEX_NAME"] == "ux_products_normalized");
        index["IS_UNIQUE"].Should().Be(true);
        index["IS_PARTIAL"].Should().Be(true);

        var term = connection.GetSchema(
                "IndexColumns",
                [null, null, "products", "ux_products_normalized"])
            .Rows.Cast<DataRow>()
            .Should().ContainSingle().Which;
        term["ORDINAL_POSITION"].Should().Be(0);
        term["COLUMN_ORDINAL"].Should().Be(-2);
        term["COLUMN_NAME"].Should().Be(DBNull.Value);
    }

    [Test]
    public void ReaderSchemaPreservesBaseMetadataForManagedAliasedColumns()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("""
            CREATE TABLE products(
                id INTEGER PRIMARY KEY,
                sku TEXT NOT NULL UNIQUE,
                name TEXT NOT NULL
            );
            INSERT INTO products VALUES (1, 'SKU-1', 'Widget');
            """);

        using var reader = connection.ExecuteReader(
            "SELECT id AS product_id, sku AS product_sku, name AS product_name FROM products;");
        reader.Read().Should().BeTrue();

        var schema = reader.GetSchemaTable();
        schema.Rows[0][SchemaTableColumn.BaseColumnName].Should().Be("id");
        schema.Rows[0][SchemaTableColumn.IsKey].Should().Be(true);
        schema.Rows[0][SchemaTableColumn.IsAliased].Should().Be(true);
        schema.Rows[0][SchemaTableColumn.IsExpression].Should().Be(false);
        schema.Rows[0][SchemaTableColumn.AllowDBNull].Should().Be(true);
        schema.Rows[1][SchemaTableColumn.IsUnique].Should().Be(false);
        schema.Rows[1][SchemaTableColumn.AllowDBNull].Should().Be(true);

        var columns = ((DbDataReader)reader).GetColumnSchema();
        columns[0].BaseColumnName.Should().Be("id");
        columns[0].IsKey.Should().BeTrue();
        columns[0].AllowDBNull.Should().BeTrue();
        columns[1].IsUnique.Should().BeFalse();
        columns[1].AllowDBNull.Should().BeTrue();
    }

    [Test]
    public void ReaderSchemaIsAvailableBeforeReadingManagedRows()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("""
            CREATE TABLE products(
                id INTEGER PRIMARY KEY,
                sku TEXT NOT NULL
            );
            INSERT INTO products VALUES (1, 'SKU-1');
            """);

        using var reader = connection.ExecuteReader("SELECT id, sku FROM products;");

        var schema = reader.GetSchemaTable();

        schema.Rows.Cast<DataRow>()
            .Select(row => (
                (string)row[SchemaTableColumn.ColumnName],
                (Type)row[SchemaTableColumn.DataType],
                (int)row[SchemaTableColumn.ProviderType]))
            .Should().Equal(
                ("id", typeof(long), (int)SqliteType.Integer),
                ("sku", typeof(string), (int)SqliteType.Text));
        reader.Read().Should().BeTrue();
    }

    [Test]
    public void ReaderSchemaSamplesTypelessColumnsAfterANullCurrentRow()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("""
            CREATE TABLE values_table(value);
            INSERT INTO values_table VALUES (NULL), ('text');
            """);

        using var reader = connection.ExecuteReader("SELECT value FROM values_table;");
        reader.Read().Should().BeTrue();

        var schema = reader.GetSchemaTable();

        schema.Rows[0][SchemaTableColumn.DataType].Should().Be(typeof(string));
        schema.Rows[0][SchemaTableColumn.ProviderType].Should().Be((int)SqliteType.Text);
    }

    [Test]
    public void SqliteTypeEnumValuesMatchTheSqliteTypeCodesUsedByMicrosoftDataSqlite()
    {
        // Microsoft.Data.Sqlite assigns its SqliteType members the SQLitePCL.raw
        // SQLITE_* constants: Integer=SQLITE_INTEGER(1), Real=SQLITE_FLOAT(2),
        // Text=SQLITE_TEXT(3), Blob=SQLITE_BLOB(4). Matching those values keeps this
        // provider drop-in compatible (schema-table ProviderType and int casts such
        // as PowerShell enum coercion observe the same numbers under both providers).
        ((int)SqliteType.Integer).Should().Be(1);
        ((int)SqliteType.Real).Should().Be(2);
        ((int)SqliteType.Text).Should().Be(3);
        ((int)SqliteType.Blob).Should().Be(4);
    }
}
