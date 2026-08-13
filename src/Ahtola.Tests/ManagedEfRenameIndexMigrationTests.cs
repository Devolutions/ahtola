using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public class ManagedEfRenameIndexMigrationTests
{
    [Test]
    public async Task ManagedMigrationsRejectIndexRenamesBeforeSchemaMutation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<RenameIndexMigrationContext>()
            .UseAhtola(connection)
            .Options;
        await using var context = new RenameIndexMigrationContext(options);

        var createTable = new CreateTableOperation { Name = "Items" };
        createTable.Columns.Add(new AddColumnOperation
        {
            Table = "Items",
            Name = "Id",
            ClrType = typeof(long),
            ColumnType = "INTEGER",
            IsNullable = false
        });
        var renameIndex = new RenameIndexOperation
        {
            Name = "IX_Items_Id",
            Table = "Items",
            NewName = "IX_Items_Renamed"
        };
        var generate = () => context.GetService<IMigrationsSqlGenerator>()
            .Generate([createTable, renameIndex]);

        generate.Should().Throw<NotSupportedException>()
            .WithMessage("*only when the target model contains*");

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"type\" = 'table' AND \"name\" = 'Items';";

        (await command.ExecuteScalarAsync()).Should().Be(0L);
    }

    [Test]
    public async Task ManagedMigrationsRenameIndexesFromTheTargetModel()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText =
                "CREATE TABLE \"Items\" (\"Id\" INTEGER NOT NULL PRIMARY KEY, \"Rank\" INTEGER NOT NULL);"
                + "CREATE INDEX \"IX_Items_Rank\" ON \"Items\" (\"Rank\");";
            await setup.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<RenameIndexMigrationContext>()
            .UseAhtola(connection)
            .Options;
        await using var context = new RenameIndexMigrationContext(options);
        var model = context.GetService<IDesignTimeModel>().Model;
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(
        [
            new RenameIndexOperation
            {
                Name = "IX_Items_Rank",
                Table = "Items",
                NewName = "IX_Items_Renamed"
            }
        ], model);

        string.Concat(commands.Select(command => command.CommandText))
            .Should().Contain("DROP INDEX \"IX_Items_Rank\"")
            .And.Contain("CREATE INDEX \"IX_Items_Renamed\"");
        foreach (var migrationCommand in commands)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = migrationCommand.CommandText;
            await command.ExecuteNonQueryAsync();
        }

        await using var verify = connection.CreateCommand();
        verify.CommandText =
            "SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"type\" = 'index' AND \"name\" = 'IX_Items_Renamed';";
        (await verify.ExecuteScalarAsync()).Should().Be(1L);
    }

    private sealed class RenameIndexMigrationContext(
        DbContextOptions<RenameIndexMigrationContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<RenameIndexItem>(entity =>
            {
                entity.ToTable("Items");
                entity.HasKey(item => item.Id);
                entity.HasIndex(item => item.Rank).HasDatabaseName("IX_Items_Renamed");
            });
        }
    }

    private sealed class RenameIndexItem
    {
        public long Id { get; set; }

        public int Rank { get; set; }
    }
}
