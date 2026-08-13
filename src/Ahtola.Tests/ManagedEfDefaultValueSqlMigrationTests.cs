using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public class ManagedEfDefaultValueSqlMigrationTests
{
    [Test]
    public async Task ManagedMigrationsApplyLiteralDefaultsToNewAndExistingRows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<DefaultValueSqlContext>()
            .UseAhtola(connection)
            .Options;
        await using var context = new DefaultValueSqlContext(options);
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var createTable = new CreateTableOperation { Name = "Defaults" };
        createTable.Columns.Add(
            new AddColumnOperation
            {
                Table = "Defaults",
                Name = "Id",
                ClrType = typeof(long),
                ColumnType = "INTEGER",
                IsNullable = false
            });
        createTable.Columns.Add(
            new AddColumnOperation
            {
                Table = "Defaults",
                Name = "State",
                ClrType = typeof(string),
                ColumnType = "TEXT",
                IsNullable = false,
                DefaultValue = "ready"
            });

        await ExecuteAsync(connection, generator.Generate([createTable]));
        await ExecuteAsync(connection, "INSERT INTO \"Defaults\" (\"Id\") VALUES (1);");
        await ExecuteAsync(
            connection,
            generator.Generate(
            [
                new AddColumnOperation
                {
                    Table = "Defaults",
                    Name = "Priority",
                    ClrType = typeof(long),
                    ColumnType = "INTEGER",
                    IsNullable = false,
                    DefaultValue = 7L
                }
            ]));

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"State\" || ':' || \"Priority\" FROM \"Defaults\" WHERE \"Id\" = 1;";
        (await command.ExecuteScalarAsync()).Should().Be("ready:7");
    }

    [Test]
    public async Task EnsureCreatedPersistsDefaultValueSql()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<DefaultValueSqlContext>()
            .UseAhtola(connection)
            .Options;
        await using var context = new DefaultValueSqlContext(options);

        (await context.Database.EnsureCreatedAsync()).Should().BeTrue();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"sql\" FROM \"sqlite_master\" WHERE \"name\" = 'Items';";

        (await command.ExecuteScalarAsync()).Should().BeOfType<string>()
            .Which.Should().Contain("DEFAULT (CURRENT_TIMESTAMP)");
    }

    private sealed class DefaultValueSqlContext(DbContextOptions<DefaultValueSqlContext> options) : DbContext(options)
    {
        public DbSet<DefaultedItem> Items => Set<DefaultedItem>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<DefaultedItem>()
                .Property(item => item.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP");
    }

    private sealed class DefaultedItem
    {
        public long Id { get; set; }

        public DateTime CreatedAt { get; set; }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        IReadOnlyList<MigrationCommand> migrationCommands)
    {
        foreach (var migrationCommand in migrationCommands)
            await ExecuteAsync(connection, migrationCommand.CommandText);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
