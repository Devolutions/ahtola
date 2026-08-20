using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using AhtolaSqliteConnection = Ahtola.Data.Sqlite.SqliteConnection;

namespace Ahtola.Tests;

/// <summary>
/// Explicit transactions, optimistic-concurrency detection, and error propagation must all work
/// through the EF Core facade over a direct remote Hrana connection. No automatic
/// <c>EnableRetryOnFailure</c>-style execution strategy is registered for remote Ahtola
/// connections (see docs/dotnet-packages.md): a transient remote failure propagates to the
/// caller as-is rather than being silently retried, since EF's execution-strategy retry
/// semantics interact unsafely with connection-scoped user transactions unless carefully
/// implemented, which is future work.
/// </summary>
public sealed class RemoteEfTransactionTests
{
    private const string ConnectionString = "Data Source=turso://database.example;Auth Token=token";

    [SetUp]
    public void InstallHandler() => _priorFactory = AhtolaSqliteConnection.RemoteMessageHandlerFactory;

    [TearDown]
    public void RestoreHandler() => AhtolaSqliteConnection.RemoteMessageHandlerFactory = _priorFactory;

    private Func<HttpMessageHandler?>? _priorFactory;

    [Test]
    public void ExplicitTransaction_CommitsSuccessfully()
    {
        using var handler = new ScriptedHranaHandler();
        using var context = CreateContext(handler);

        using var transaction = context.Database.BeginTransaction();
        var widget = new RemoteWidget { Name = "Widget A", Price = 9.99m };
        context.Widgets.Add(widget);
        context.SaveChanges();
        transaction.Commit();

        widget.Id.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task ExplicitTransaction_CommitsSuccessfullyAsync()
    {
        using var handler = new ScriptedHranaHandler();
        using var context = CreateContext(handler);

        await using var transaction = await context.Database.BeginTransactionAsync();
        var widget = new RemoteWidget { Name = "Widget A", Price = 9.99m };
        context.Widgets.Add(widget);
        await context.SaveChangesAsync();
        await transaction.CommitAsync();

        widget.Id.Should().BeGreaterThan(0);
    }

    [Test]
    public void ExplicitTransaction_RollbackLeavesTheConnectionUsableAfterward()
    {
        using var handler = new ScriptedHranaHandler();
        using var context = CreateContext(handler);

        using (var transaction = context.Database.BeginTransaction())
        {
            var widget = new RemoteWidget { Name = "Widget A", Price = 9.99m };
            context.Widgets.Add(widget);
            context.SaveChanges();
            transaction.Rollback();
        }

        var again = () => context.Widgets.Select(w => w.Id).ToList();
        again.Should().NotThrow();
    }

    [Test]
    public void SaveChanges_Update_WhenServerReportsZeroRowsAffected_ThrowsConcurrencyException()
    {
        // Simulates another writer having already changed or removed the row: the server
        // reports 0 rows affected for an update expected to match exactly one row.
        using var handler = new ScriptedHranaHandler();
        handler.At(0, ScriptedHranaHandler.Ok(affectedRowCount: 0));
        using var context = CreateContext(handler);

        var widget = new RemoteWidget { Id = 7, Name = "Widget A" };
        context.Attach(widget);
        widget.Name = "Widget A (renamed)";

        var save = () => context.SaveChanges();

        save.Should().Throw<DbUpdateConcurrencyException>();
    }

    [Test]
    public void SaveChanges_Delete_WhenServerReportsZeroRowsAffected_ThrowsConcurrencyException()
    {
        using var handler = new ScriptedHranaHandler();
        handler.At(0, ScriptedHranaHandler.Ok(affectedRowCount: 0));
        using var context = CreateContext(handler);

        var widget = new RemoteWidget { Id = 7, Name = "Widget A" };
        context.Attach(widget);
        context.Remove(widget);

        var save = () => context.SaveChanges();

        save.Should().Throw<DbUpdateConcurrencyException>();
    }

    [Test]
    public void TransientRemoteFailure_PropagatesDirectly_WithoutAutomaticRetry()
    {
        using var handler = new ScriptedHranaHandler();
        handler.ErrorAt(0, "database is locked", code: "SQLITE_BUSY");
        using var context = CreateContext(handler);

        var query = () => context.Widgets.Select(w => w.Id).ToList();

        query.Should().Throw<Exception>();
        handler.RequestCount.Should().Be(1, "no automatic retry is performed for direct remote connections");
    }

    [Test]
    public async Task ScriptedHandler_SkipsDestructiveStep_WhenItsGuardConditionReferencesAFailedEarlierStep()
    {
        // Exercises the test infrastructure itself (ScriptedHranaHandler), not EF: a batch step
        // guarded by a condition referencing an earlier step's outcome must never execute when
        // that earlier step failed. Without correct condition evaluation, a destructive statement
        // chained after a failed guard could run anyway, silently corrupting data instead of the
        // batch aborting as Hrana's semantics require — this is exactly what every other RemoteEf
        // test implicitly depends on (e.g. SqliteHistoryRepository's lock-acquisition batches).
        using var handler = new ScriptedHranaHandler();
        handler.ErrorAt(1, "forced failure for this test");
        using var httpClient = new HttpClient(handler);
        using var client = new AhtolaRemoteClient(httpClient, new Uri("https://database.example"), authToken: null);

        var commands = new[]
        {
            new AhtolaBatchCommand("INSERT INTO t VALUES (1)"),
            new AhtolaBatchCommand("UPDATE t SET value = 2 WHERE value = 1"),
            new AhtolaBatchCommand("DROP TABLE t")
            {
                RemoteCondition = AhtolaRemoteBatchCondition.StepSucceeded(1),
            },
        };

        var execute = async () => await client.ExecuteBatchAsync(
            commands,
            commandTimeout: 30,
            wantRows: false,
            closeAfter: true,
            CancellationToken.None);

        // The client surfaces the failed step 1 as an exception (real Hrana client semantics),
        // but by the time it does, the fake server has already fully computed the batch
        // response — so the SQL log is the reliable place to prove the guarded step never ran.
        await execute.Should().ThrowAsync<Exception>();
        handler.SqlLog.Should().ContainInOrder("INSERT INTO t VALUES (1)", "UPDATE t SET value = 2 WHERE value = 1");
        handler.SqlLog.Should().NotContain(
            "DROP TABLE t",
            "the guard condition (step 1 succeeded) was never satisfied, so the destructive step must not run");
    }

    private static RemoteWidgetContext CreateContext(ScriptedHranaHandler handler)
    {
        AhtolaSqliteConnection.RemoteMessageHandlerFactory = () => handler;
        var options = new DbContextOptionsBuilder<RemoteWidgetContext>()
            .UseAhtola(ConnectionString)
            .Options;
        return new RemoteWidgetContext(options);
    }
}
