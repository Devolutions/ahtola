using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using AhtolaSqliteConnection = Ahtola.Data.Sqlite.SqliteConnection;

namespace Ahtola.Tests;

/// <summary>
/// Direct remote Hrana and embedded-replica connections cannot register the client-side
/// 'regexp'/'ef_*' functions or the 'EF_DECIMAL' collation that the SQLite EF Core provider
/// normally relies on for <c>Regex.IsMatch</c>, decimal arithmetic/aggregates, and decimal
/// ordering (see <see cref="Ahtola.EntityFrameworkCore.Sqlite.Query.Internal.AhtolaRemoteSqlRestrictions"/>).
/// Those translations must fail with a precise <see cref="NotSupportedException"/> during query
/// translation — before any SQL is sent to the endpoint — rather than reaching the server and
/// failing late with an opaque "no such function"/"no such collation sequence" error. Unrelated
/// decimal parameter/storage usage (simple projections, filters, equality) must keep working.
/// </summary>
public sealed class RemoteEfTranslationRestrictionTests
{
    [Test]
    public void RegexIsMatch_ThrowsNotSupportedException_BeforeSendingAnySql()
    {
        using var handler = new ScriptedHranaHandler();
        using var context = CreateContext(handler);

        var query = () => context.Widgets.Where(w => Regex.IsMatch(w.Name, "^A")).ToList();

        query.Should().Throw<NotSupportedException>().Which.Message.Should().Contain("regexp");
        handler.SqlLog.Should().BeEmpty("translation must fail before any request reaches the endpoint");
    }

    [Test]
    public void DecimalAddition_ThrowsNotSupportedException_BeforeSendingAnySql()
    {
        using var handler = new ScriptedHranaHandler();
        using var context = CreateContext(handler);

        var query = () => context.Widgets.Where(w => w.Price + 1m > 10m).ToList();

        query.Should().Throw<NotSupportedException>().Which.Message.Should().Contain("decimal");
        handler.SqlLog.Should().BeEmpty();
    }

    [Test]
    public void DecimalSubtraction_ThrowsNotSupportedException_BeforeSendingAnySql()
    {
        using var handler = new ScriptedHranaHandler();
        using var context = CreateContext(handler);

        var query = () => context.Widgets.Where(w => w.Price - 1m > 10m).ToList();

        query.Should().Throw<NotSupportedException>();
        handler.SqlLog.Should().BeEmpty();
    }

    [Test]
    public void DecimalMultiplication_ThrowsNotSupportedException_BeforeSendingAnySql()
    {
        using var handler = new ScriptedHranaHandler();
        using var context = CreateContext(handler);

        var query = () => context.Widgets.Where(w => w.Price * 2m > 10m).ToList();

        query.Should().Throw<NotSupportedException>();
        handler.SqlLog.Should().BeEmpty();
    }

    [Test]
    public void DecimalDivision_ThrowsNotSupportedException_BeforeSendingAnySql()
    {
        using var handler = new ScriptedHranaHandler();
        using var context = CreateContext(handler);

        var query = () => context.Widgets.Where(w => w.Price / 2m > 10m).ToList();

        query.Should().Throw<NotSupportedException>();
        handler.SqlLog.Should().BeEmpty();
    }

    [Test]
    public void DecimalModulo_ThrowsNotSupportedException_BeforeSendingAnySql()
    {
        using var handler = new ScriptedHranaHandler();
        using var context = CreateContext(handler);

        var query = () => context.Widgets.Where(w => w.Price % 2m == 0m).ToList();

        query.Should().Throw<NotSupportedException>();
        handler.SqlLog.Should().BeEmpty();
    }

    [Test]
    public void DecimalNegate_ThrowsNotSupportedException_BeforeSendingAnySql()
    {
        using var handler = new ScriptedHranaHandler();
        using var context = CreateContext(handler);

        var query = () => context.Widgets.Where(w => -w.Price < 0m).ToList();

        query.Should().Throw<NotSupportedException>();
        handler.SqlLog.Should().BeEmpty();
    }

    [Test]
    public void DecimalOrderingComparison_ThrowsNotSupportedException_BeforeSendingAnySql()
    {
        using var handler = new ScriptedHranaHandler();
        using var context = CreateContext(handler);

        var query = () => context.Widgets.Where(w => w.Price > 10m).ToList();

        query.Should().Throw<NotSupportedException>();
        handler.SqlLog.Should().BeEmpty();
    }

    [Test]
    public void DecimalSum_ThrowsNotSupportedException_BeforeSendingAnySql()
    {
        using var handler = new ScriptedHranaHandler();
        using var context = CreateContext(handler);

        var query = () => context.Widgets.Sum(w => w.Price);

        query.Should().Throw<NotSupportedException>().Which.Message.Should().Contain("ef_sum");
        handler.SqlLog.Should().BeEmpty();
    }

    [Test]
    public void DecimalAverage_ThrowsNotSupportedException_BeforeSendingAnySql()
    {
        using var handler = new ScriptedHranaHandler();
        using var context = CreateContext(handler);

        var query = () => context.Widgets.Average(w => w.Price);

        query.Should().Throw<NotSupportedException>().Which.Message.Should().Contain("ef_avg");
        handler.SqlLog.Should().BeEmpty();
    }

    [Test]
    public void DecimalMax_ThrowsNotSupportedException_BeforeSendingAnySql()
    {
        using var handler = new ScriptedHranaHandler();
        using var context = CreateContext(handler);

        var query = () => context.Widgets.Max(w => w.Price);

        // EF Core 10 translates decimal Max via the client-registered 'ef_max' function (which
        // our restriction guard rejects); EF Core 9's own SQLite provider rejects decimal Max
        // natively before translation ever reaches that function. Either way this must throw
        // NotSupportedException before any SQL is sent.
        query.Should().Throw<NotSupportedException>();
        handler.SqlLog.Should().BeEmpty();
    }

    [Test]
    public void DecimalMin_ThrowsNotSupportedException_BeforeSendingAnySql()
    {
        using var handler = new ScriptedHranaHandler();
        using var context = CreateContext(handler);

        var query = () => context.Widgets.Min(w => w.Price);

        // See DecimalMax_ThrowsNotSupportedException_BeforeSendingAnySql: EF Core 9's SQLite
        // provider rejects decimal Min natively (pre-dating the 'ef_min' function).
        query.Should().Throw<NotSupportedException>();
        handler.SqlLog.Should().BeEmpty();
    }

    [Test]
    public void OrderByDecimal_ThrowsNotSupportedException_BeforeSendingAnySql()
    {
        using var handler = new ScriptedHranaHandler();
        using var context = CreateContext(handler);

        var query = () => context.Widgets.OrderBy(w => w.Price).Select(w => w.Id).ToList();

        query.Should().Throw<NotSupportedException>().Which.Message.Should().Contain("EF_DECIMAL");
        handler.SqlLog.Should().BeEmpty();
    }

    [Test]
    public void OrderByNonDecimalColumn_IsUnaffectedByTheDecimalOrderingRestriction()
    {
        using var handler = new ScriptedHranaHandler();
        using var context = CreateContext(handler);

        var query = () => context.Widgets.OrderBy(w => w.Name).Select(w => w.Id).ToList();

        query.Should().NotThrow();
        handler.SqlLog.Should().ContainSingle(sql => sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void UnrelatedDecimalFilterAndProjection_AreNotRejected()
    {
        // Equality/inequality on a decimal column, and simply reading/writing decimal values,
        // do not require any client-registered function or collation — only arithmetic,
        // ordering comparisons, and aggregates do. These must keep working over a remote
        // connection.
        using var handler = new ScriptedHranaHandler();
        using var context = CreateContext(handler);

        var equalityQuery = () => context.Widgets.Where(w => w.Price == 9.99m).Select(w => w.Id).ToList();
        var projectionQuery = () => context.Widgets.Select(w => w.Price).ToList();

        equalityQuery.Should().NotThrow();
        projectionQuery.Should().NotThrow();
    }

    [Test]
    public void JsonPrimitiveCollectionCount_RemainsTranslatedOverRemote()
    {
        // JSON1 stays enabled for direct remote connections (the endpoint is a real
        // SQLite-compatible server); only the client-side function/collation constructs above
        // are restricted. Count() over a JSON1 primitive collection translates to
        // json_array_length(...) rather than a json_each(...) table-valued function.
        using var handler = new ScriptedHranaHandler();
        using var context = CreateContext(handler);

        var query = () => context.Widgets.Select(w => w.Tags.Count()).ToList();

        query.Should().NotThrow();
        handler.SqlLog.Should().ContainSingle(sql => sql.Contains("json_array_length", StringComparison.OrdinalIgnoreCase));
    }

    private static RemoteWidgetContext CreateContext(ScriptedHranaHandler handler)
    {
        AhtolaSqliteConnection.RemoteMessageHandlerFactory = () => handler;

        var options = new DbContextOptionsBuilder<RemoteWidgetContext>()
            .UseAhtola("Data Source=turso://database.example;Auth Token=token")
            .Options;
        return new RemoteWidgetContext(options);
    }

    [SetUp]
    public void InstallHandler()
    {
        _priorFactory = AhtolaSqliteConnection.RemoteMessageHandlerFactory;
    }

    [TearDown]
    public void RestoreHandler()
    {
        AhtolaSqliteConnection.RemoteMessageHandlerFactory = _priorFactory;
    }

    private Func<HttpMessageHandler?>? _priorFactory;
}
