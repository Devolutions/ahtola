using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public class ManagedEfJsonCollectionTranslationTests
{
    [Test]
    public void ManagedProviderRejectsCollectionEnumerationThatRequiresJsonEach()
    {
        using var context = CreateContext("Data Source=:memory:;Local Provider=Managed");

        var translate = () => context.Items.Where(item => item.Ids.Contains(1)).ToQueryString();

        translate.Should().Throw<InvalidOperationException>()
            .WithMessage("*JSON collections*");
    }

    [Test]
    public async Task ManagedProviderExecutesPrimitiveCollectionAnyAndCountWithoutJsonEach()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync();

        context.Items.AddRange(
            new JsonCollectionItem { Id = 1, Ids = [1, 2] },
            new JsonCollectionItem { Id = 2, Ids = [] });
        await context.SaveChangesAsync();

        var anyQuery = context.Items.Where(item => item.Ids.Any());
        anyQuery.ToQueryString()
            .Should()
            .Contain("json_array_length")
            .And.NotContain("json_each");
        (await anyQuery.Select(item => item.Id).ToListAsync()).Should().Equal(1);

        var countQuery = context.Items.OrderBy(item => item.Id).Select(item => item.Ids.Count());
        countQuery.ToQueryString()
            .Should()
            .Contain("json_array_length")
            .And.NotContain("json_each");
        (await countQuery.ToListAsync()).Should().Equal(2, 0);
    }

    [Test]
    public void NativeProviderKeepsJsonEachCollectionPropertyTranslation()
    {
        using var context = CreateContext("Data Source=:memory:;Local Provider=Native");

        context.Items.Where(item => item.Ids.Contains(1))
            .ToQueryString()
            .Should()
            .Contain("json_each");
    }

    private static JsonCollectionContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<JsonCollectionContext>()
            .UseAhtola(connectionString)
            .Options;

        return new JsonCollectionContext(options);
    }

    private static JsonCollectionContext CreateContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<JsonCollectionContext>()
            .UseAhtola(connection)
            .Options;

        return new JsonCollectionContext(options);
    }

    private sealed class JsonCollectionContext(DbContextOptions<JsonCollectionContext> options) : DbContext(options)
    {
        public DbSet<JsonCollectionItem> Items => Set<JsonCollectionItem>();
    }

    private sealed class JsonCollectionItem
    {
        public long Id { get; set; }

        public long[] Ids { get; set; } = [];
    }
}
