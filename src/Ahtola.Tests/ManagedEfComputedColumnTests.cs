using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public class ManagedEfComputedColumnTests
{
    [Test]
    public async Task ManagedProviderPersistsAndRefreshesVirtualComputedColumns()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ComputedColumnContext>()
            .UseAhtola(connection)
            .Options;
        await using var context = new ComputedColumnContext(options);

        var createScript = context.Database.GenerateCreateScript();
        createScript.Should().Contain("AS (length(\"Name\"))");
        createScript.Should().NotContain("STORED");
        (await context.Database.EnsureCreatedAsync()).Should().BeTrue();

        var item = new ComputedColumnItem { Name = "Ada" };
        context.Items.Add(item);
        await context.SaveChangesAsync();
        item.NameLength.Should().Be(3);

        item.Name = "Grace";
        await context.SaveChangesAsync();
        item.NameLength.Should().Be(5);

        context.ChangeTracker.Clear();
        (await context.Items.SingleAsync()).NameLength.Should().Be(5);
    }

    private sealed class ComputedColumnContext(
        DbContextOptions<ComputedColumnContext> options) : DbContext(options)
    {
        public DbSet<ComputedColumnItem> Items => Set<ComputedColumnItem>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<ComputedColumnItem>()
                .Property(item => item.NameLength)
                .HasComputedColumnSql("length(\"Name\")", stored: false);
    }

    private sealed class ComputedColumnItem
    {
        public long Id { get; set; }

        public string Name { get; set; } = "";

        public int? NameLength { get; set; }
    }
}
