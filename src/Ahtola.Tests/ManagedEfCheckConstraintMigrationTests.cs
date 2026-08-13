using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public class ManagedEfCheckConstraintMigrationTests
{
    [Test]
    public async Task EnsureCreatedPersistsCheckConstraints()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<CheckConstraintContext>()
            .UseAhtola(connection)
            .Options;
        await using var context = new CheckConstraintContext(options);

        (await context.Database.EnsureCreatedAsync()).Should().BeTrue();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"sql\" FROM \"sqlite_master\" WHERE \"name\" = 'Items';";

        (await command.ExecuteScalarAsync()).Should().BeOfType<string>()
            .Which.Should().Contain("CONSTRAINT \"CK_Items_Name\" CHECK");
    }

    [Test]
    public void ManagedMigrationsRejectAddedCheckConstraints()
    {
        using var context = CreateContext("Data Source=:memory:;Local Provider=Managed");
        var operation = new AddCheckConstraintOperation
        {
            Name = "CK_Items_Name",
            Table = "Items",
            Sql = "\"Name\" <> ''"
        };

        var generate = () => context.GetService<IMigrationsSqlGenerator>().Generate([operation]);

        generate.Should().Throw<NotSupportedException>()
            .WithMessage("*check constraints*");
    }

    private static CheckConstraintContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<CheckConstraintContext>()
            .UseAhtola(connectionString)
            .Options;

        return new CheckConstraintContext(options);
    }

    private sealed class CheckConstraintContext(DbContextOptions<CheckConstraintContext> options) : DbContext(options)
    {
        public DbSet<CheckConstrainedItem> Items => Set<CheckConstrainedItem>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<CheckConstrainedItem>()
                .ToTable(table => table.HasCheckConstraint("CK_Items_Name", "\"Name\" <> ''"));
    }

    private sealed class CheckConstrainedItem
    {
        public long Id { get; set; }

        public string Name { get; set; } = "";
    }
}
