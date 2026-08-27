using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
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
    public async Task ManagedMigrationsAddCheckConstraintsAndPreserveSchemaObjects()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE "Items" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Items" PRIMARY KEY,
                "Name" TEXT NOT NULL COLLATE NOCASE DEFAULT 'unknown'
            );
            CREATE INDEX "IX_Items_Name" ON "Items" ("Name");
            CREATE TABLE "Audit" ("Value" TEXT NOT NULL);
            CREATE TRIGGER "TR_Items_Update" AFTER UPDATE ON "Items"
            BEGIN
                INSERT INTO "Audit" VALUES (NEW."Name");
            END;
            INSERT INTO "Items" VALUES (1, 'preserved');
            """);

        await using var context = CreateContext(connection);
        var operation = new AddCheckConstraintOperation
        {
            Name = "CK_Items_Name",
            Table = "Items",
            Sql = "\"Name\" <> ''"
        };

        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(
            [operation],
            context.GetService<IDesignTimeModel>().Model,
            MigrationsSqlGenerationOptions.Idempotent);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await context.GetService<IMigrationCommandExecutor>().ExecuteNonQueryAsync(
                commands,
                context.GetService<IRelationalConnection>());
        }

        await using var schema = connection.CreateCommand();
        schema.CommandText = "SELECT \"sql\" FROM \"sqlite_master\" WHERE \"name\" = 'Items';";
        (await schema.ExecuteScalarAsync()).Should().BeOfType<string>()
            .Which.Should().Contain("CK_Items_Name")
            .And.Contain("COLLATE NOCASE")
            .And.Contain("DEFAULT 'unknown'");

        await using var index = connection.CreateCommand();
        index.CommandText = "SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"name\" = 'IX_Items_Name';";
        (await index.ExecuteScalarAsync()).Should().Be(1L);

        await using var preserved = connection.CreateCommand();
        preserved.CommandText = "SELECT \"Name\" FROM \"Items\" WHERE \"Id\" = 1;";
        (await preserved.ExecuteScalarAsync()).Should().Be("preserved");

        await ExecuteAsync(connection, "UPDATE \"Items\" SET \"Name\" = 'updated' WHERE \"Id\" = 1;");
        await using var audit = connection.CreateCommand();
        audit.CommandText = "SELECT \"Value\" FROM \"Audit\";";
        (await audit.ExecuteScalarAsync()).Should().Be("updated");

        var invalid = () => ExecuteAsync(connection, "INSERT INTO \"Items\" (\"Id\", \"Name\") VALUES (2, '');");
        await invalid.Should().ThrowAsync<Exception>();
    }

    private static CheckConstraintContext CreateContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<CheckConstraintContext>()
            .UseAhtola(connection)
            .Options;

        return new CheckConstraintContext(options);
    }

    private sealed class CheckConstraintContext(DbContextOptions<CheckConstraintContext> options) : DbContext(options)
    {
        public DbSet<CheckConstrainedItem> Items => Set<CheckConstrainedItem>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CheckConstrainedItem>(entity =>
            {
                entity.ToTable("Items", table =>
                    table.HasCheckConstraint("CK_Items_Name", "\"Name\" <> ''"));
                entity.HasIndex(item => item.Name).HasDatabaseName("IX_Items_Name");
                entity.Property(item => item.Name)
                    .UseCollation("NOCASE")
                    .HasDefaultValue("unknown");
            });
        }
    }

    private sealed class CheckConstrainedItem
    {
        public long Id { get; set; }

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
