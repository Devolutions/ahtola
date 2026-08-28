using Ahtola.Data.Sqlite;
using Ahtola.Tests.Infrastructure;
using AwesomeAssertions;

namespace Ahtola.Tests;

public sealed class LogicalDatabaseFingerprintTests
{
    [Test]
    public void TypedGoldenVectorIsStableAndExcludesSqliteInternalObjects()
    {
        using var connection = Open(":memory:");
        Execute(
            connection,
            """
            CREATE TABLE typed(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                nil,
                integer_value,
                real_value,
                text_value,
                blob_value);
            INSERT INTO typed(nil, integer_value, real_value, text_value, blob_value)
            VALUES(NULL, -7, 1.5, 'héllo', X'00FF');
            """);

        var fingerprint = LogicalDatabaseFingerprint.Compute(connection);
        TestContext.Out.WriteLine($"logical fingerprint: {fingerprint}");

        fingerprint.SchemaObjects.Should().Be(1, "sqlite_sequence is an internal SQLite object");
        fingerprint.Tables.Should().Be(1);
        fingerprint.Rows.Should().Be(1);
        fingerprint.Sha256.Should().Be("7a0f724474204a7cd7e26c2f18d7bba7623da6f378eb63161b1fd5b87b6f2b86");
    }

    [Test]
    public void EqualLogicalStatesWithDifferentPhysicalHistoriesHaveTheSameFingerprint()
    {
        var root = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "logical-database-fingerprint",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var firstPath = Path.Combine(root, "first.db");
        var secondPath = Path.Combine(root, "second.db");

        try
        {
            using var first = Open(firstPath);
            Execute(
                first,
                """
                PRAGMA page_size=512;
                VACUUM;
                CREATE TABLE items(id INTEGER PRIMARY KEY, label TEXT, payload BLOB);
                CREATE INDEX items_label ON items(label);
                INSERT INTO items VALUES(3, 'three', X'03');
                INSERT INTO items VALUES(1, 'one', X'01');
                INSERT INTO items VALUES(2, 'discarded', X'FF');
                DELETE FROM items WHERE id = 2;
                INSERT INTO items VALUES(2, 'two', X'02');
                """);

            using var second = Open(secondPath);
            Execute(
                second,
                """
                PRAGMA page_size=4096;
                VACUUM;
                CREATE TABLE items(id INTEGER PRIMARY KEY, label TEXT, payload BLOB);
                CREATE INDEX items_label ON items(label);
                INSERT INTO items VALUES(1, 'one', X'01');
                INSERT INTO items VALUES(2, 'two', X'02');
                INSERT INTO items VALUES(3, 'three', X'03');
                """);

            var firstFingerprint = LogicalDatabaseFingerprint.Compute(first);
            var secondFingerprint = LogicalDatabaseFingerprint.Compute(second);

            firstFingerprint.Should().Be(
                secondFingerprint,
                $"logical fingerprints should ignore page layout and mutation history; first={firstFingerprint}, second={secondFingerprint}");

            Execute(second, "UPDATE items SET label = 'changed' WHERE id = 2;");
            var changedFingerprint = LogicalDatabaseFingerprint.Compute(second);
            changedFingerprint.Sha256.Should().NotBe(
                firstFingerprint.Sha256,
                $"one typed value changed; before={firstFingerprint}, after={changedFingerprint}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static SqliteConnection Open(string dataSource)
    {
        var connection = new SqliteConnection(
            $"Data Source={dataSource};Local Provider=Managed;Pooling=False");
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
