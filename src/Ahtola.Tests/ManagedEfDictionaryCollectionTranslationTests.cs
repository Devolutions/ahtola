using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// A dictionary's <c>Keys</c>/<c>Values</c> view is a nested type generic over <em>two</em>
/// parameters (<c>Dictionary&lt;TKey, TValue&gt;.KeyCollection</c>) whose base type is
/// <see cref="object"/> and which is not itself <c>IEnumerable&lt;T&gt;</c>. Resolving the element
/// type by walking base types and counting generic arguments therefore fails with
/// <c>"is not an enumerable type"</c> and the query stops translating entirely.
/// </summary>
/// <remarks>
/// <c>EF.Parameter</c> is what forces the collection to be translated as a single primitive
/// collection instead of being expanded into one parameter per element, and that is the path which
/// resolves the element type. The managed provider then refuses <c>json_each</c> exactly as it
/// does for a <c>List&lt;T&gt;</c>; the native provider translates it. Either outcome proves the
/// element type resolved — the regression is the <c>"is not an enumerable type"</c> failure that
/// happens before that decision is ever reached.
/// </remarks>
public class ManagedEfDictionaryCollectionTranslationTests
{
    private const string NotEnumerable = "is not an enumerable type";

    [Test]
    public void DictionaryKeysContainsResolvesItsElementTypeOnTheManagedProvider()
    {
        using var context = CreateContext("Data Source=:memory:;Local Provider=Managed");
        var wanted = new Dictionary<string, int>(StringComparer.Ordinal) { ["ada"] = 1, ["cyd"] = 3 };

        var translate = () => context.People
            .Where(person => EF.Parameter(wanted.Keys).Contains(person.Name))
            .ToQueryString();

        // Same outcome a List<string> gets: the element type resolved, and only the managed
        // provider's json_each policy stops it.
        translate.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().NotContain(NotEnumerable);
        translate.Should().Throw<InvalidOperationException>()
            .WithMessage("*JSON collections*");
    }

    [Test]
    public void DictionaryValuesResolveTheirElementTypeOnTheManagedProvider()
    {
        using var context = CreateContext("Data Source=:memory:;Local Provider=Managed");
        var wanted = new Dictionary<string, long>(StringComparer.Ordinal) { ["a"] = 2L, ["b"] = 3L };

        var translate = () => context.People
            .Where(person => EF.Parameter(wanted.Values).Any(value => value == person.Id))
            .ToQueryString();

        translate.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().NotContain(NotEnumerable);
        translate.Should().Throw<InvalidOperationException>()
            .WithMessage("*JSON collections*");
    }

    [Test]
    public void SortedDictionaryKeysResolveTheirElementTypeOnTheManagedProvider()
    {
        using var context = CreateContext("Data Source=:memory:;Local Provider=Managed");
        var wanted = new SortedDictionary<string, int>(StringComparer.Ordinal) { ["bob"] = 1 };

        var translate = () => context.People
            .Where(person => EF.Parameter(wanted.Keys).Contains(person.Name))
            .ToQueryString();

        translate.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().NotContain(NotEnumerable);
    }

    [Test]
    public void DictionaryKeysContainsTranslatesToJsonEachOnTheNativeProvider()
    {
        using var context = CreateContext("Data Source=:memory:;Local Provider=Native");
        var wanted = new Dictionary<string, int>(StringComparer.Ordinal) { ["ada"] = 1, ["cyd"] = 3 };

        context.People
            .Where(person => EF.Parameter(wanted.Keys).Contains(person.Name))
            .ToQueryString()
            .Should().Contain("json_each");
    }

    [Test]
    public void DictionaryValuesTranslateToJsonEachOnTheNativeProvider()
    {
        using var context = CreateContext("Data Source=:memory:;Local Provider=Native");
        var wanted = new Dictionary<string, long>(StringComparer.Ordinal) { ["a"] = 2L, ["b"] = 3L };

        context.People
            .Where(person => EF.Parameter(wanted.Values).Any(value => value == person.Id))
            .ToQueryString()
            .Should().Contain("json_each");
    }

    /// <summary>
    /// The shapes that already worked must keep working: an array, a list, a set and an
    /// interface-typed sequence all resolve through the single-generic-argument rule.
    /// </summary>
    [Test]
    public void ArrayListSetAndInterfaceCollectionsStillResolveTheirElementType()
    {
        using var context = CreateContext("Data Source=:memory:;Local Provider=Native");

        var array = new[] { "ada" };
        var list = new List<string> { "bob" };
        var set = new HashSet<string>(StringComparer.Ordinal) { "cyd" };
        IReadOnlyList<long> ids = [1L, 2L];

        context.People.Where(p => EF.Parameter(array).Contains(p.Name)).ToQueryString()
            .Should().Contain("json_each");
        context.People.Where(p => EF.Parameter(list).Contains(p.Name)).ToQueryString()
            .Should().Contain("json_each");
        context.People.Where(p => EF.Parameter(set).Contains(p.Name)).ToQueryString()
            .Should().Contain("json_each");
        context.People.Where(p => EF.Parameter(ids).Contains(p.Id)).ToQueryString()
            .Should().Contain("json_each");
    }

    /// <summary>
    /// End-to-end proof against real rows. EF Core 9 and 10 differ in how a captured collection is
    /// parameterized — 10 expands it into one parameter per element, 9 routes it through
    /// <c>json_each</c>, which the managed provider refuses — so the assertion accepts either the
    /// filtered rows or that documented refusal. What is never acceptable on any version is the
    /// element type failing to resolve.
    /// </summary>
    [Test]
    public async Task DictionaryKeysAndValuesFilterRealRows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync();

        context.People.AddRange(
            new Person { Id = 1, Name = "ada" },
            new Person { Id = 2, Name = "bob" },
            new Person { Id = 3, Name = "cyd" });
        await context.SaveChangesAsync();

        var byName = new Dictionary<string, int>(StringComparer.Ordinal) { ["ada"] = 1, ["cyd"] = 3 };
        await AssertFiltersOrIsRefusedForJsonAsync(
            () => context.People
                .Where(person => byName.Keys.Contains(person.Name))
                .OrderBy(person => person.Id)
                .Select(person => person.Id)
                .ToListAsync(),
            [1L, 3L]);

        var byId = new Dictionary<string, long>(StringComparer.Ordinal) { ["x"] = 2L, ["y"] = 3L };
        await AssertFiltersOrIsRefusedForJsonAsync(
            () => context.People
                .Where(person => byId.Values.Contains(person.Id))
                .OrderBy(person => person.Id)
                .Select(person => person.Id)
                .ToListAsync(),
            [2L, 3L]);
    }

    private static async Task AssertFiltersOrIsRefusedForJsonAsync(
        Func<Task<List<long>>> query,
        long[] expected)
    {
        try
        {
            (await query()).Should().Equal(expected);
        }
        catch (InvalidOperationException exception)
        {
            exception.Message.Should().NotContain(NotEnumerable);
            exception.Message.Should().Contain(
                "JSON collections",
                "the only acceptable refusal is the managed provider's documented json_each policy");
        }
    }

    private static DictionaryContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<DictionaryContext>()
            .UseAhtola(connectionString)
            .Options;

        return new DictionaryContext(options);
    }

    private static DictionaryContext CreateContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<DictionaryContext>()
            .UseAhtola(connection)
            .Options;

        return new DictionaryContext(options);
    }

    private sealed class DictionaryContext(DbContextOptions<DictionaryContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();
    }

    private sealed class Person
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}
