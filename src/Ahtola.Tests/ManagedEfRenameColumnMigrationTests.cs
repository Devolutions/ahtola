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
    public async Task ManagedMigrationsRenameColumnsWithConstraintAndTriggerDependencies()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE "Items" (
                "OldName" TEXT NOT NULL,
                "Kind" TEXT NOT NULL,
                "Mirror" TEXT AS ("OldName" || ':' || "Kind"),
                CONSTRAINT "PK_Items" PRIMARY KEY ("OldName", "Kind"),
                CONSTRAINT "CK_Items_OldName" CHECK ("OldName" <> '')
            );
            CREATE INDEX "IX_Items_OldName" ON "Items" ("OldName") WHERE "OldName" <> 'skip';
            CREATE TABLE "Audit" ("Value" TEXT NOT NULL);
            CREATE TRIGGER "TR_Items_OldName" AFTER UPDATE OF "OldName" ON "Items"
            BEGIN
                INSERT INTO "Audit" VALUES (NEW."OldName");
            END;
            INSERT INTO "Items" ("OldName", "Kind") VALUES ('before', 'kind');
            """);

        var options = new DbContextOptionsBuilder<CompositeKeyRenameColumnMigrationContext>()
            .UseAhtola(connection)
            .Options;
        await using var context = new CompositeKeyRenameColumnMigrationContext(options);
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
        foreach (var command in commands)
            await ExecuteAsync(connection, command.CommandText);

        await ExecuteAsync(
            connection,
            "UPDATE \"Items\" SET \"Name\" = 'after' WHERE \"Name\" = 'before';");

        await using var verify = connection.CreateCommand();
        verify.CommandText =
            "SELECT \"Name\" || ':' || \"Kind\" || ':' || \"Mirror\" FROM \"Items\";";
        (await verify.ExecuteScalarAsync()).Should().Be("after:kind:after:kind");

        await using var audit = connection.CreateCommand();
        audit.CommandText = "SELECT \"Value\" FROM \"Audit\";";
        (await audit.ExecuteScalarAsync()).Should().Be("after");

        await using var schema = connection.CreateCommand();
        schema.CommandText =
            "SELECT group_concat(\"sql\", char(10)) FROM \"sqlite_master\" "
            + "WHERE \"name\" IN ('Items', 'IX_Items_OldName', 'TR_Items_OldName');";
        var sql = (string)(await schema.ExecuteScalarAsync())!;
        sql.Should().NotContain("\"OldName\"").And.Contain("\"Name\"");
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
