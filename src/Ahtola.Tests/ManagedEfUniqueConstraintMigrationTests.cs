using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
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
    public async Task ManagedMigrationsAddAndDropStandaloneUniqueConstraints()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE "Items" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Items" PRIMARY KEY,
                "Email" TEXT NOT NULL COLLATE NOCASE,
                "Name" TEXT NOT NULL DEFAULT 'unknown'
            );
            CREATE INDEX "IX_Items_Name" ON "Items" ("Name");
            INSERT INTO "Items" VALUES (1, 'first@example.test', 'first');
            """);

        var options = new DbContextOptionsBuilder<AlternateKeyContext>()
            .UseAhtola(connection)
            .Options;
        await using var context = new AlternateKeyContext(options);
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var model = context.GetService<IDesignTimeModel>().Model;

        var addCommands = generator.Generate(
        [
            new AddUniqueConstraintOperation
            {
                Name = "AK_Items_Email",
                Table = "Items",
                Columns = ["Email"]
            }
        ], model, MigrationsSqlGenerationOptions.Idempotent);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await context.GetService<IMigrationCommandExecutor>().ExecuteNonQueryAsync(
                addCommands,
                context.GetService<IRelationalConnection>());
        }

        var duplicate = () => ExecuteAsync(
            connection,
            "INSERT INTO \"Items\" VALUES (2, 'FIRST@EXAMPLE.TEST', 'duplicate');");
        await duplicate.Should().ThrowAsync<Exception>();

        var dropOptions = new DbContextOptionsBuilder<NoAlternateKeyContext>()
            .UseAhtola(connection)
            .Options;
        await using var dropContext = new NoAlternateKeyContext(dropOptions);
        var dropCommands = dropContext.GetService<IMigrationsSqlGenerator>().Generate(
        [
            new DropUniqueConstraintOperation
            {
                Name = "AK_Items_Email",
                Table = "Items"
            }
        ],
            dropContext.GetService<IDesignTimeModel>().Model,
            MigrationsSqlGenerationOptions.Idempotent);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await dropContext.GetService<IMigrationCommandExecutor>().ExecuteNonQueryAsync(
                dropCommands,
                dropContext.GetService<IRelationalConnection>());
        }

        await ExecuteAsync(
            connection,
            "INSERT INTO \"Items\" VALUES (2, 'FIRST@EXAMPLE.TEST', 'second');");

        await using var verify = connection.CreateCommand();
        verify.CommandText =
            "SELECT \"Name\" || ':' || \"Email\" FROM \"Items\" ORDER BY \"Id\";";
        await using var reader = await verify.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("first:first@example.test");
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("second:FIRST@EXAMPLE.TEST");
    }

    private sealed class AlternateKeyContext(DbContextOptions<AlternateKeyContext> options) : DbContext(options)
    {
        public DbSet<AlternateKeyItem> Items => Set<AlternateKeyItem>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AlternateKeyItem>(entity =>
            {
                entity.ToTable("Items");
                entity.HasAlternateKey(item => item.Email).HasName("AK_Items_Email");
                entity.HasIndex(item => item.Name).HasDatabaseName("IX_Items_Name");
                entity.Property(item => item.Email).UseCollation("NOCASE");
                entity.Property(item => item.Name).HasDefaultValue("unknown");
            });
        }
    }

    private sealed class NoAlternateKeyContext(DbContextOptions<NoAlternateKeyContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AlternateKeyItem>(entity =>
            {
                entity.ToTable("Items");
                entity.HasIndex(item => item.Name).HasDatabaseName("IX_Items_Name");
                entity.Property(item => item.Email).UseCollation("NOCASE");
                entity.Property(item => item.Name).HasDefaultValue("unknown");
            });
        }
    }

    private sealed class AlternateKeyItem
    {
        public long Id { get; set; }

        public string Email { get; set; } = "";

        public string Name { get; set; } = "";
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        IReadOnlyList<MigrationCommand> commands)
    {
        foreach (var command in commands)
            await ExecuteAsync(connection, command.CommandText);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
