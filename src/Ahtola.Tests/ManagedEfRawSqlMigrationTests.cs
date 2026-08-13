using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public class ManagedEfRawSqlMigrationTests
{
    [Test]
    public async Task MigrateRejectsRawSqlBeforeApplicationSchemaMutation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<RawSqlMigrationContext>()
            .UseAhtola(connection)
            .Options;
        await using var context = new RawSqlMigrationContext(options);

        var migrate = async () => await context.Database.MigrateAsync();

        await migrate.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*raw SQL migration operations*");

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"type\" = 'table' AND \"name\" = 'Items';";

        (await command.ExecuteScalarAsync()).Should().Be(0L);
    }

    private sealed class RawSqlMigrationContext(DbContextOptions<RawSqlMigrationContext> options) : DbContext(options);

    [DbContext(typeof(RawSqlMigrationContext))]
    [Migration("20260722210000_RejectRawSql")]
    public sealed class RejectRawSqlMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Items",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_Items", item => item.Id));
            migrationBuilder.Sql("INSERT INTO \"Items\" (\"Id\") VALUES (1);");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
            => migrationBuilder.DropTable("Items");
    }
}
