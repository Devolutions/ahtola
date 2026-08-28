using Ahtola.Core;
using Ahtola.Core.Storage;
using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedSchemaInteriorFileStoreTests
{
    [Test]
    public void OpensExternalSqliteSchemaInteriorWithStaleSeparatorAfterDrop()
    {
        var path = CreateDatabasePath();
        try
        {
            CreateExternalDatabase(path);
            MsData.SqliteConnection.ClearAllPools();
            var separator = ReadSchemaInteriorSeparator(path);

            using (var sqlite = new MsData.SqliteConnection($"Data Source={path}"))
            {
                sqlite.Open();
                using var command = sqlite.CreateCommand();
                command.CommandText = "SELECT name FROM sqlite_schema WHERE rowid = $rowid;";
                command.Parameters.AddWithValue("$rowid", separator.RowId);
                var tableName = Convert.ToString(command.ExecuteScalar());
                tableName.Should().NotBeNullOrEmpty();

                command.Parameters.Clear();
                command.CommandText = $"DROP TABLE \"{tableName}\";";
                command.ExecuteNonQuery();

                command.CommandText = "PRAGMA quick_check;";
                command.ExecuteScalar().Should().Be("ok");
            }

            MsData.SqliteConnection.ClearAllPools();
            AssertSeparatorIsStale(path, separator);

            using var facade = new Ahtola.Data.Sqlite.SqliteConnection(
                $"Data Source={path};Mode=ReadOnly;Pooling=False");
            facade.Open();
            using var select = facade.CreateCommand();
            select.CommandText = "SELECT COUNT(*) FROM sqlite_schema;";
            Convert.ToInt64(select.ExecuteScalar()).Should().Be(9);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void RejectsSchemaInteriorSeparatorBelowChildMaximum()
    {
        var path = CreateDatabasePath();
        try
        {
            CreateExternalDatabase(path);
            MsData.SqliteConnection.ClearAllPools();

            using (var store = SqlitePageStore.Open(PhysicalFileSystem.Instance, path))
            {
                var page = store.ReadPage(1);
                var interior = SqliteTableInteriorPageView.Parse(
                    page,
                    store.Header.UsableSpace,
                    isFirstPage: true);
                interior.Cells.Should().NotBeEmpty();
                page[interior.CellPointers[0] + sizeof(uint)] = 0;
                store.WritePage(1, page);
                store.Flush();
            }

            var exception = Assert.Throws<EmbeddedSqlException>(() =>
            {
                using var facade = new Ahtola.Data.Sqlite.SqliteConnection(
                    $"Data Source={path};Mode=ReadOnly;Pooling=False");
                facade.Open();
            });
            exception.Message.Should().Contain("separator 0 is below maximum rowid");
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    private static void CreateExternalDatabase(string path)
    {
        using var sqlite = new MsData.SqliteConnection($"Data Source={path}");
        sqlite.Open();
        using var command = sqlite.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode=DELETE;
            PRAGMA page_size=512;
            VACUUM;
            """;
        command.ExecuteNonQuery();

        for (var index = 0; index < 10; index++)
        {
            command.CommandText =
                $"CREATE TABLE schema_entry_{index:D3}(id INTEGER PRIMARY KEY, value_{index:D3} TEXT);";
            command.ExecuteNonQuery();
        }
    }

    private static SchemaSeparator ReadSchemaInteriorSeparator(string path)
    {
        using var store = SqlitePageStore.Open(PhysicalFileSystem.Instance, path, readOnly: true);
        var interior = SqliteTableInteriorPageView.Parse(
            store.ReadPage(1),
            store.Header.UsableSpace,
            isFirstPage: true);
        interior.Cells.Should().NotBeEmpty();
        var cell = interior.Cells[interior.Cells.Count / 2].Cell;
        return new SchemaSeparator(cell.LeftChildPage, cell.RowId);
    }

    private static void AssertSeparatorIsStale(string path, SchemaSeparator separator)
    {
        using var store = SqlitePageStore.Open(PhysicalFileSystem.Instance, path, readOnly: true);
        var child = SqliteTableLeafPageView.Parse(
            store.ReadPage(separator.LeftChildPage),
            store.Header.UsableSpace);
        child.Cells.Should().NotBeEmpty();
        child.Cells[^1].Cell.RowId.Should().BeLessThan(separator.RowId);
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "managed-schema-interior-file-store-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"schema-{Guid.NewGuid():N}.db");
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }

    private sealed record SchemaSeparator(uint LeftChildPage, long RowId);
}
