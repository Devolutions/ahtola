using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public sealed class ManagedEfMigrationHistoryTests
{
    [Test]
    public async Task MigrateCreatesHistoryAndDoesNotReapplyRecordedMigrations()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MigrationHistoryContext>()
            .UseAhtola(connection)
            .Options;
        await using var context = new MigrationHistoryContext(options);

        await context.Database.MigrateAsync();
        await context.Database.MigrateAsync();

        await using var history = connection.CreateCommand();
        history.CommandText =
            "SELECT COUNT(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '20260723220000_CreateHistoryItem';";
        (await history.ExecuteScalarAsync()).Should().Be(1L);

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO \"HistoryItems\" (\"Id\") VALUES (1);";
            await insert.ExecuteNonQueryAsync();
        }

        await using var defaultValue = connection.CreateCommand();
        defaultValue.CommandText = "SELECT \"State\" FROM \"HistoryItems\" WHERE \"Id\" = 1;";
        (await defaultValue.ExecuteScalarAsync()).Should().Be("ready");
    }

    [Test]
    public void GenerateScriptIncludesHistoryBootstrapAndHistoryRow()
    {
        var options = new DbContextOptionsBuilder<MigrationHistoryContext>()
            .UseAhtola("Data Source=:memory:;Local Provider=Managed")
            .Options;
        using var context = new MigrationHistoryContext(options);

        var script = context.GetService<IMigrator>().GenerateScript();

        script.Should().Contain("CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\"")
            .And.Contain("CREATE TABLE \"HistoryItems\"")
            .And.Contain("INSERT INTO \"__EFMigrationsHistory\"");
    }

    [Test]
    public void GenerateIdempotentScriptFailsWithManagedCapabilityError()
    {
        var options = new DbContextOptionsBuilder<MigrationHistoryContext>()
            .UseAhtola("Data Source=:memory:;Local Provider=Managed")
            .Options;
        using var context = new MigrationHistoryContext(options);

        var generate = () => context.GetService<IMigrator>()
            .GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);

        generate.Should().Throw<NotSupportedException>()
            .WithMessage("*does not support idempotent migration scripts*");
    }

    private sealed class MigrationHistoryContext(
        DbContextOptions<MigrationHistoryContext> options) : DbContext(options);

    [DbContext(typeof(MigrationHistoryContext))]
    [Migration("20260723220000_CreateHistoryItem")]
    public sealed class CreateHistoryItemMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
            => migrationBuilder.CreateTable(
                name: "HistoryItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(
                        type: "TEXT",
                        nullable: false,
                        defaultValue: "ready")
                },
                constraints: table => table.PrimaryKey("PK_HistoryItems", item => item.Id));

        protected override void Down(MigrationBuilder migrationBuilder)
            => migrationBuilder.DropTable("HistoryItems");
    }
}
