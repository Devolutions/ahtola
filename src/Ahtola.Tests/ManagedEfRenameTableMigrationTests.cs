using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public sealed class ManagedEfRenameTableMigrationTests
{
    [Test]
    public async Task ManagedMigrationsRenameTablesAndPreserveRows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<RenameTableMigrationContext>()
            .UseAhtola(connection)
            .Options;
        await using var context = new RenameTableMigrationContext(options);
        var model = context.GetService<IDesignTimeModel>().Model;

        await ExecuteAsync(
            connection,
            "CREATE TABLE \"Parents\" (\"Id\" INTEGER NOT NULL); INSERT INTO \"Parents\" VALUES (7);");

        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(
            [new RenameTableOperation { Name = "Parents", NewName = "RenamedParents" }],
            model);
        foreach (var migrationCommand in commands)
            await ExecuteAsync(connection, migrationCommand.CommandText);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT \"Id\" FROM \"RenamedParents\";";

        (await command.ExecuteScalarAsync()).Should().Be(7L);
    }

    [Test]
    public void ManagedMigrationsRejectTableRenamesWithForeignKeyDependencies()
    {
        var options = new DbContextOptionsBuilder<DependentRenameTableMigrationContext>()
            .UseAhtola("Data Source=:memory:;Local Provider=Managed")
            .Options;
        using var context = new DependentRenameTableMigrationContext(options);
        var model = context.GetService<IDesignTimeModel>().Model;

        var generate = () => context.GetService<IMigrationsSqlGenerator>().Generate(
            [new RenameTableOperation { Name = "Parents", NewName = "RenamedParents" }],
            model);

        generate.Should().Throw<NotSupportedException>()
            .WithMessage("*cannot safely rename table*foreign key or trigger dependencies*");
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class RenameTableMigrationContext(
        DbContextOptions<RenameTableMigrationContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<RenameTableParent>().ToTable("RenamedParents");
    }

    private sealed class DependentRenameTableMigrationContext(
        DbContextOptions<DependentRenameTableMigrationContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<RenameTableParent>().ToTable("RenamedParents");
            modelBuilder.Entity<RenameTableChild>(entity =>
            {
                entity.ToTable("Children");
                entity.HasOne<RenameTableParent>()
                    .WithMany()
                    .HasForeignKey(child => child.ParentId)
                    .OnDelete(DeleteBehavior.NoAction);
            });
        }
    }

    private sealed class RenameTableParent
    {
        public long Id { get; set; }
    }

    private sealed class RenameTableChild
    {
        public long Id { get; set; }

        public long ParentId { get; set; }
    }
}
