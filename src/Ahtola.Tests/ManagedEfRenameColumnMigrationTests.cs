using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public sealed class ManagedEfRenameColumnMigrationTests
{
    [Test]
    public async Task ManagedMigrationsRenameColumnsAndUpdateIndexes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            "CREATE TABLE \"Items\" (\"OldName\" TEXT NOT NULL);"
            + "CREATE INDEX \"IX_Items_OldName\" ON \"Items\" (\"OldName\");"
            + "INSERT INTO \"Items\" VALUES ('preserved');");

        var options = new DbContextOptionsBuilder<RenameColumnMigrationContext>()
            .UseAhtola(connection)
            .Options;
        await using var context = new RenameColumnMigrationContext(options);
        var model = context.GetService<IDesignTimeModel>().Model;
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(
        [
            new RenameColumnOperation
            {
                Table = "Items",
                Name = "OldName",
                NewName = "Name"
            }
        ], model);

        foreach (var migrationCommand in commands)
            await ExecuteAsync(connection, migrationCommand.CommandText);

        await using var value = connection.CreateCommand();
        value.CommandText = "SELECT \"Name\" FROM \"Items\";";
        (await value.ExecuteScalarAsync()).Should().Be("preserved");

        await using var index = connection.CreateCommand();
        index.CommandText =
            "SELECT \"sql\" FROM \"sqlite_master\" WHERE \"type\" = 'index' AND \"name\" = 'IX_Items_OldName';";
        (await index.ExecuteScalarAsync()).Should().Be("CREATE INDEX \"IX_Items_OldName\" ON \"Items\" (\"Name\")");
    }

    [Test]
    public void ManagedMigrationsRejectColumnRenamesInCompositePrimaryKeys()
    {
        var options = new DbContextOptionsBuilder<CompositeKeyRenameColumnMigrationContext>()
            .UseAhtola("Data Source=:memory:;Local Provider=Managed")
            .Options;
        using var context = new CompositeKeyRenameColumnMigrationContext(options);
        var model = context.GetService<IDesignTimeModel>().Model;
        var generate = () => context.GetService<IMigrationsSqlGenerator>().Generate(
        [
            new RenameColumnOperation
            {
                Table = "Items",
                Name = "OldName",
                NewName = "Name"
            }
        ], model);

        generate.Should().Throw<NotSupportedException>()
            .WithMessage("*cannot safely rename column*table-constraint*");
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class RenameColumnMigrationContext(
        DbContextOptions<RenameColumnMigrationContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<RenameColumnItem>(entity =>
            {
                entity.ToTable("Items");
                entity.HasKey(item => item.Name);
                entity.HasIndex(item => item.Name).HasDatabaseName("IX_Items_OldName");
            });
        }
    }

    private sealed class RenameColumnItem
    {
        public string Name { get; set; } = "";
    }

    private sealed class CompositeKeyRenameColumnMigrationContext(
        DbContextOptions<CompositeKeyRenameColumnMigrationContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CompositeKeyRenameColumnItem>(entity =>
            {
                entity.ToTable("Items");
                entity.HasKey(item => new { item.Name, item.Kind });
            });
        }
    }

    private sealed class CompositeKeyRenameColumnItem
    {
        public string Name { get; set; } = "";

        public string Kind { get; set; } = "";
    }
}
