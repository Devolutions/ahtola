using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Ahtola.Tests;

public class ManagedEfDropCheckConstraintMigrationTests
{
    [Test]
    public void ManagedMigrationsRejectDroppedCheckConstraintsBeforeSqlGeneration()
    {
        var options = new DbContextOptionsBuilder<DropCheckConstraintMigrationContext>()
            .UseAhtola("Data Source=:memory:;Local Provider=Managed")
            .Options;
        using var context = new DropCheckConstraintMigrationContext(options);
        var generate = () => context.GetService<IMigrationsSqlGenerator>().Generate(
        [
            new DropCheckConstraintOperation
            {
                Name = "CK_Items_Id",
                Table = "Items"
            }
        ]);

        generate.Should().Throw<NotSupportedException>()
            .WithMessage("*dropping check constraints*");
    }

    private sealed class DropCheckConstraintMigrationContext(
        DbContextOptions<DropCheckConstraintMigrationContext> options) : DbContext(options);
}
