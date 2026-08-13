using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public class ManagedEfUniqueConstraintMigrationTests
{
    [Test]
    public async Task EnsureCreatedPersistsAlternateKeysAsUniqueConstraints()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AlternateKeyContext>()
            .UseAhtola(connection)
            .Options;
        await using var context = new AlternateKeyContext(options);

        (await context.Database.EnsureCreatedAsync()).Should().BeTrue();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"sql\" FROM \"sqlite_master\" WHERE \"name\" = 'Items';";

        (await command.ExecuteScalarAsync()).Should().BeOfType<string>()
            .Which.Should().Contain("CONSTRAINT \"AK_Items_Email\" UNIQUE");
    }

    [Test]
    public void ManagedMigrationsRejectStandaloneUniqueConstraintOperationsBeforeSqlGeneration()
    {
        var options = new DbContextOptionsBuilder<AlternateKeyContext>()
            .UseAhtola("Data Source=:memory:;Local Provider=Managed")
            .Options;
        using var context = new AlternateKeyContext(options);
        var generator = context.GetService<IMigrationsSqlGenerator>();

        var add = () => generator.Generate(
        [
            new AddUniqueConstraintOperation
            {
                Name = "AK_Items_Email",
                Table = "Items",
                Columns = ["Email"]
            }
        ]);
        var drop = () => generator.Generate(
        [
            new DropUniqueConstraintOperation
            {
                Name = "AK_Items_Email",
                Table = "Items"
            }
        ]);

        add.Should().Throw<NotSupportedException>().WithMessage("*unique constraints*");
        drop.Should().Throw<NotSupportedException>().WithMessage("*unique constraints*");
    }

    private sealed class AlternateKeyContext(DbContextOptions<AlternateKeyContext> options) : DbContext(options)
    {
        public DbSet<AlternateKeyItem> Items => Set<AlternateKeyItem>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<AlternateKeyItem>()
                .HasAlternateKey(item => item.Email);
    }

    private sealed class AlternateKeyItem
    {
        public long Id { get; set; }

        public string Email { get; set; } = "";
    }
}
