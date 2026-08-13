using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Ahtola.Tests;

public class ManagedEfDescendingIndexMigrationTests
{
    [TestCase(true)]
    [TestCase(false)]
    public void ManagedMigrationsGenerateDescendingIndexEncoding(bool useEmptySortOrders)
    {
        var options = new DbContextOptionsBuilder<DescendingIndexMigrationContext>()
            .UseAhtola("Data Source=:memory:;Local Provider=Managed")
            .Options;
        using var context = new DescendingIndexMigrationContext(options);
        var createTable = new CreateTableOperation { Name = "Items" };
        createTable.Columns.Add(new AddColumnOperation
        {
            Table = "Items",
            Name = "Rank",
            ClrType = typeof(int),
            ColumnType = "INTEGER",
            IsNullable = false
        });
        var createIndex = new CreateIndexOperation
        {
            Name = "IX_Items_Rank",
            Table = "Items",
            Columns = ["Rank"],
            IsDescending = useEmptySortOrders ? [] : [true]
        };

        var commands = context.GetService<IMigrationsSqlGenerator>().Generate([createTable, createIndex]);

        commands.Select(command => command.CommandText)
            .Should()
            .Contain(command => command.Contains(
                "CREATE INDEX \"IX_Items_Rank\" ON \"Items\" (\"Rank\" DESC)",
                StringComparison.Ordinal));
    }

    private sealed class DescendingIndexMigrationContext(
        DbContextOptions<DescendingIndexMigrationContext> options) : DbContext(options);
}
