using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public sealed class ManagedEfDropColumnMigrationTests
{
    [Test]
    public async Task ManagedMigrationsDropColumnsAndPreserveRows()
    {
        await using var connection = new SqliteConnection(
            "Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE "Items" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Items" PRIMARY KEY,
                "Obsolete" TEXT,
                "Name" TEXT NOT NULL
            );
            INSERT INTO "Items" VALUES (1, 'remove', 'preserved');
            """);

        var options = new DbContextOptionsBuilder<DropColumnMigrationContext>()
            .UseAhtola(connection)
            .Options;
        await using var context = new DropColumnMigrationContext(options);
        var model = context.GetService<IDesignTimeModel>().Model;
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(
        [
            new DropColumnOperation
            {
                Table = "Items",
                Name = "Obsolete",
            }
        ], model);

        commands.Should().ContainSingle();
        commands[0].CommandText.Should().Contain("DROP COLUMN");
        foreach (var command in commands)
            await ExecuteAsync(connection, command.CommandText);

        await using var value = connection.CreateCommand();
        value.CommandText = "SELECT \"Id\", \"Name\" FROM \"Items\";";
        await using var reader = await value.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt64(0).Should().Be(1);
        reader.GetString(1).Should().Be("preserved");
        (await reader.ReadAsync()).Should().BeFalse();
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class DropColumnMigrationContext(
        DbContextOptions<DropColumnMigrationContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<DropColumnItem>(entity =>
            {
                entity.ToTable("Items");
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Name).IsRequired();
            });
        }
    }

    private sealed class DropColumnItem
    {
        public long Id { get; set; }

        public string Name { get; set; } = "";
    }
}
