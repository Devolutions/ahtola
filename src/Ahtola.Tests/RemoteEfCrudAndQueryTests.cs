using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using AhtolaSqliteConnection = Ahtola.Data.Sqlite.SqliteConnection;

namespace Ahtola.Tests;

/// <summary>
/// CRUD/SaveChanges and query translation must work end to end over a direct remote Hrana
/// connection: INSERT ... RETURNING (and the last_insert_rowid()/changes() fallback shape when
/// RETURNING is disabled for a table), simple filter/order/limit queries, and JSON1 translation
/// for primitive collections.
/// </summary>
public sealed class RemoteEfCrudAndQueryTests
{
    private const string ConnectionString = "Data Source=turso://database.example;Auth Token=token";

    [SetUp]
    public void InstallHandler() => _priorFactory = AhtolaSqliteConnection.RemoteMessageHandlerFactory;

    [TearDown]
    public void RestoreHandler() => AhtolaSqliteConnection.RemoteMessageHandlerFactory = _priorFactory;

    private Func<HttpMessageHandler?>? _priorFactory;

    [Test]
    public void SaveChanges_Insert_UsesReturningClauseAndPopulatesGeneratedKey()
    {
        using var handler = new ScriptedHranaHandler();
        using var context = CreateContext(handler);

        var widget = new RemoteWidget { Name = "Widget A", Price = 9.99m, Quantity = 3 };
        context.Widgets.Add(widget);
        var affected = context.SaveChanges();

        affected.Should().Be(1);
        widget.Id.Should().Be(1);
        handler.SqlLog.Should().ContainSingle();
        handler.SqlLog[0].Should().Contain("INSERT INTO").And.Contain("RETURNING");
    }

    [Test]
    public void SaveChanges_MultipleInserts_SendsABatchAndAssignsDistinctKeys()
    {
        using var handler = new ScriptedHranaHandler();
        using var context = CreateContext(handler);

        var first = new RemoteWidget { Name = "Widget A", Price = 9.99m };
        var second = new RemoteWidget { Name = "Widget B", Price = 19.99m };
        context.Widgets.AddRange(first, second);
        var affected = context.SaveChanges();

        affected.Should().Be(2);
        first.Id.Should().NotBe(second.Id);
        first.Id.Should().BeGreaterThan(0);
        second.Id.Should().BeGreaterThan(0);
    }

    [Test]
    public void SaveChanges_Update_SendsUpdateReturningAndReportsRecordsAffected()
    {
        using var handler = new ScriptedHranaHandler();
        using var context = CreateContext(handler);

        var widget = new RemoteWidget { Id = 7, Name = "Widget A", Price = 9.99m };
        context.Attach(widget);
        widget.Name = "Widget A (renamed)";
        var affected = context.SaveChanges();

        affected.Should().Be(1);
        handler.SqlLog.Should().ContainSingle();
        handler.SqlLog[0].Should().Contain("UPDATE").And.Contain("\"Widgets\"");
    }

    [Test]
    public void SaveChanges_Delete_SendsDeleteAndReportsRecordsAffected()
    {
        using var handler = new ScriptedHranaHandler();
        using var context = CreateContext(handler);

        var widget = new RemoteWidget { Id = 7, Name = "Widget A" };
        context.Attach(widget);
        context.Remove(widget);
        var affected = context.SaveChanges();

        affected.Should().Be(1);
        handler.SqlLog.Should().ContainSingle();
        handler.SqlLog[0].Should().Contain("DELETE FROM").And.Contain("\"Widgets\"");
    }

    [Test]
    public void SaveChanges_Insert_WithoutReturningClause_UsesLastInsertRowIdAndChangesShape()
    {
        using var handler = new ScriptedHranaHandler();
        AhtolaSqliteConnection.RemoteMessageHandlerFactory = () => handler;
        var options = new DbContextOptionsBuilder<NoReturningWidgetContext>()
            .UseAhtola(ConnectionString)
            .Options;
        using var context = new NoReturningWidgetContext(options);

        var widget = new RemoteWidget { Name = "Widget A", Price = 9.99m };
        context.Widgets.Add(widget);
        var affected = context.SaveChanges();

        affected.Should().Be(1);
        widget.Id.Should().BeGreaterThan(0);
        handler.SqlLog.Should().Contain(sql => sql.Contains("last_insert_rowid()", StringComparison.OrdinalIgnoreCase));
        handler.SqlLog.Should().Contain(sql => sql.Contains("changes()", StringComparison.OrdinalIgnoreCase));
        handler.SqlLog.Should().NotContain(sql => sql.Contains("RETURNING", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void Query_WhereOrderByTake_TranslatesToExpectedSqlShapeAndMaterializesScriptedRows()
    {
        using var handler = new ScriptedHranaHandler();
        using var context = CreateContext(handler);
        handler.At(
            0,
            ScriptedHranaHandler.Rows(
                ["Name"],
                [[ScriptedHranaHandler.Text("Widget A")], [ScriptedHranaHandler.Text("Widget B")]]));

        var names = context.Widgets
            .Where(w => w.Quantity > 0)
            .OrderBy(w => w.Name)
            .Take(5)
            .Select(w => w.Name)
            .ToList();

        names.Should().Equal("Widget A", "Widget B");
        handler.SqlLog.Should().ContainSingle();
        var sql = handler.SqlLog[0];
        sql.Should().Contain("WHERE").And.Contain("ORDER BY").And.Contain("LIMIT");
    }

    [Test]
    public void Query_JsonPrimitiveCollectionContains_TranslatesThroughJsonEach()
    {
        using var handler = new ScriptedHranaHandler();
        using var context = CreateContext(handler);

        var query = () => context.Widgets.Where(w => w.Tags.Contains("clearance")).Select(w => w.Id).ToList();

        query.Should().NotThrow();
        handler.SqlLog.Should().ContainSingle(sql => sql.Contains("json_each", StringComparison.OrdinalIgnoreCase));
    }

    private static RemoteWidgetContext CreateContext(ScriptedHranaHandler handler)
    {
        AhtolaSqliteConnection.RemoteMessageHandlerFactory = () => handler;
        var options = new DbContextOptionsBuilder<RemoteWidgetContext>()
            .UseAhtola(ConnectionString)
            .Options;
        return new RemoteWidgetContext(options);
    }

    private sealed class NoReturningWidgetContext(DbContextOptions<NoReturningWidgetContext> options) : DbContext(options)
    {
        public DbSet<RemoteWidget> Widgets => Set<RemoteWidget>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<RemoteWidget>().ToTable(table => table.UseSqlReturningClause(false));
    }
}
