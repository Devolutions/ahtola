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
    public async Task ManagedMigrationsRenameTablesWithForeignKeyAndTriggerDependencies()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            """
            PRAGMA foreign_keys = ON;
            CREATE TABLE "Parents" ("Id" INTEGER NOT NULL CONSTRAINT "PK_Parents" PRIMARY KEY);
            CREATE TABLE "Children" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Children" PRIMARY KEY,
                "ParentId" INTEGER NOT NULL,
                CONSTRAINT "FK_Children_Parents" FOREIGN KEY ("ParentId") REFERENCES "Parents" ("Id")
            );
            CREATE TABLE "Audit" ("ParentId" INTEGER NOT NULL);
            CREATE TRIGGER "TR_Parents_Insert" AFTER INSERT ON "Parents"
            BEGIN
                INSERT INTO "Audit" VALUES (NEW."Id");
            END;
            INSERT INTO "Parents" VALUES (1);
            INSERT INTO "Children" VALUES (1, 1);
            """);

        var options = new DbContextOptionsBuilder<DependentRenameTableMigrationContext>()
            .UseAhtola(connection)
            .Options;
        await using var context = new DependentRenameTableMigrationContext(options);
        var model = context.GetService<IDesignTimeModel>().Model;

        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(
            [new RenameTableOperation { Name = "Parents", NewName = "RenamedParents" }],
            model);
        foreach (var command in commands)
            await ExecuteAsync(connection, command.CommandText);

        await ExecuteAsync(connection, "INSERT INTO \"RenamedParents\" VALUES (2);");
        await ExecuteAsync(connection, "INSERT INTO \"Children\" VALUES (2, 2);");

        await using var audit = connection.CreateCommand();
        audit.CommandText = "SELECT group_concat(\"ParentId\", ',') FROM \"Audit\";";
        (await audit.ExecuteScalarAsync()).Should().Be("1,2");

        await using var foreignKey = connection.CreateCommand();
        foreignKey.CommandText = "SELECT \"table\" FROM pragma_foreign_key_list('Children');";
        (await foreignKey.ExecuteScalarAsync()).Should().Be("RenamedParents");
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
