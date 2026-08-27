using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public class ManagedEfDropCheckConstraintMigrationTests
{
    [Test]
    public async Task ManagedMigrationsDropCheckConstraintsAndPreserveRows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText =
                "CREATE TABLE \"Items\" (\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_Items\" PRIMARY KEY, "
                + "\"Name\" TEXT NOT NULL, CONSTRAINT \"CK_Items_Id\" CHECK (\"Id\" > 0));"
                + "INSERT INTO \"Items\" VALUES (1, 'preserved');";
            await setup.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<DropCheckConstraintMigrationContext>()
            .UseAhtola(connection)
            .Options;
        await using var context = new DropCheckConstraintMigrationContext(options);
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(
        [
            new DropCheckConstraintOperation
            {
                Name = "CK_Items_Id",
                Table = "Items"
            }
        ],
            context.GetService<IDesignTimeModel>().Model,
            MigrationsSqlGenerationOptions.Idempotent);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await context.GetService<IMigrationCommandExecutor>().ExecuteNonQueryAsync(
                commands,
                context.GetService<IRelationalConnection>());
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO \"Items\" VALUES (-1, 'now allowed');";
        await insert.ExecuteNonQueryAsync();

        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT COUNT(*) FROM \"Items\";";
        (await verify.ExecuteScalarAsync()).Should().Be(2L);
    }

    private sealed class DropCheckConstraintMigrationContext(
        DbContextOptions<DropCheckConstraintMigrationContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Item>().ToTable("Items");
    }

    private sealed class Item
    {
        public long Id { get; set; }

        public string Name { get; set; } = "";
    }
}
