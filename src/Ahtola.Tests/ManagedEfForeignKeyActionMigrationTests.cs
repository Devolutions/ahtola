using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public sealed class ManagedEfForeignKeyActionMigrationTests
{
    [Test]
    public async Task ManagedMigrationsGenerateCompositeTableForeignKeys()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<MigrationContext>()
            .UseAhtola(connection)
            .Options;
        await using var context = new MigrationContext(options);

        var parent = CreateTable("Parents", "FirstId", "SecondId");
        var child = CreateTable("Children", "ParentFirstId", "ParentSecondId");
        child.ForeignKeys.Add(new AddForeignKeyOperation
        {
            Name = "FK_Children_Parents_ParentFirstId_ParentSecondId",
            Table = "Children",
            Columns = ["ParentFirstId", "ParentSecondId"],
            PrincipalTable = "Parents",
            PrincipalColumns = ["FirstId", "SecondId"],
        });

        var commands = context.GetService<IMigrationsSqlGenerator>().Generate([parent, child]);
        var sql = string.Concat(commands.Select(command => command.CommandText));
        sql.Should().Contain(
            "FOREIGN KEY (\"ParentFirstId\", \"ParentSecondId\") "
                + "REFERENCES \"Parents\" (\"FirstId\", \"SecondId\")");
        foreach (var migrationCommand in commands)
        {
            await using var execute = connection.CreateCommand();
            execute.CommandText = migrationCommand.CommandText;
            await execute.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';";
        (await command.ExecuteScalarAsync()).Should().Be(2L);
    }

    private static CreateTableOperation CreateTable(string name, params string[] columns)
    {
        var table = new CreateTableOperation { Name = name };
        foreach (var column in columns)
        {
            table.Columns.Add(new AddColumnOperation
            {
                Table = name,
                Name = column,
                ClrType = typeof(long),
                ColumnType = "INTEGER",
                IsNullable = false,
            });
        }

        return table;
    }

    private sealed class MigrationContext(DbContextOptions<MigrationContext> options) : DbContext(options);
}
