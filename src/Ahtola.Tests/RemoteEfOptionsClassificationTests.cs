using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Sqlite.Migrations.Internal;
using Ahtola.EntityFrameworkCore.Sqlite.Migrations.Internal;
using Ahtola.EntityFrameworkCore.Sqlite.Query.Internal;
using AhtolaSqliteConnection = Ahtola.Data.Sqlite.SqliteConnection;

namespace Ahtola.Tests;

/// <summary>
/// <c>UseAhtola</c> must classify every supported connection shape (native/managed local,
/// direct remote Hrana including <c>turso://</c>, and embedded replica) into the right EF Core
/// service set, rather than rejecting or silently mishandling the remote/replica cases.
/// </summary>
public sealed class RemoteEfOptionsClassificationTests
{
    [TestCase("Data Source=libsql://database.example;Auth Token=token")]
    [TestCase("Data Source=turso://database.example;Auth Token=token")]
    [TestCase("Data Source=https://database.example/db;Auth Token=token")]
    [TestCase("Data Source=http://database.example/db;Auth Token=token")]
    [TestCase("Data Source=wss://database.example/db;Auth Token=token")]
    [TestCase("Data Source=ws://database.example/db;Auth Token=token")]
    public void UseAhtola_ClassifiesDirectRemoteUrlsAsRemoteHranaServices(string connectionString)
    {
        using var context = CreateContext(connectionString);

        context.GetService<IQuerySqlGeneratorFactory>().Should().BeOfType<AhtolaRemoteSqliteQuerySqlGeneratorFactory>();
        context.GetService<IQueryableMethodTranslatingExpressionVisitorFactory>()
            .Should().BeOfType<AhtolaSqliteQueryableMethodTranslatingExpressionVisitorFactory>();
        context.GetService<IHistoryRepository>().Should().BeOfType<SqliteHistoryRepository>();
        context.GetService<IMigrationsSqlGenerator>().Should().BeOfType<SqliteMigrationsSqlGenerator>();
        context.GetService<IRelationalParameterBasedSqlProcessorFactory>()
            .Should().BeOfType<AhtolaRestrictedSqliteParameterBasedSqlProcessorFactory>();
    }

    [TestCase("Data Source=libsql://database.example;Replica Path=replica.db;Auth Token=token")]
    [TestCase("Data Source=turso://database.example;Replica Path=replica.db;Auth Token=token")]
    public void UseAhtola_ClassifiesEmbeddedReplicaAsReplicaServices(string connectionString)
    {
        using var context = CreateContext(connectionString);

        context.GetService<IQuerySqlGeneratorFactory>().Should().BeOfType<AhtolaReplicaSqliteQuerySqlGeneratorFactory>();
        context.GetService<IQueryableMethodTranslatingExpressionVisitorFactory>()
            .Should().BeOfType<AhtolaManagedSqliteQueryableMethodTranslatingExpressionVisitorFactory>();
        context.GetService<IHistoryRepository>().Should().BeOfType<AhtolaManagedSqliteHistoryRepository>();
        context.GetService<IMigrationsSqlGenerator>().Should().BeOfType<AhtolaManagedSqliteMigrationsSqlGenerator>();
        context.GetService<IRelationalParameterBasedSqlProcessorFactory>()
            .Should().BeOfType<AhtolaRestrictedSqliteParameterBasedSqlProcessorFactory>();
    }

    [Test]
    public void UseAhtola_ClassifiesManagedLocalAsManagedServices()
    {
        using var context = CreateContext("Data Source=:memory:;Local Provider=Managed");

        context.GetService<IQuerySqlGeneratorFactory>().Should().BeOfType<AhtolaManagedSqliteQuerySqlGeneratorFactory>();
        context.GetService<IQueryableMethodTranslatingExpressionVisitorFactory>()
            .Should().BeOfType<AhtolaManagedSqliteQueryableMethodTranslatingExpressionVisitorFactory>();
        context.GetService<IHistoryRepository>().Should().BeOfType<AhtolaManagedSqliteHistoryRepository>();
        context.GetService<IMigrationsSqlGenerator>().Should().BeOfType<AhtolaManagedSqliteMigrationsSqlGenerator>();
        context.GetService<IRelationalParameterBasedSqlProcessorFactory>()
            .Should().BeOfType<AhtolaSqliteParameterBasedSqlProcessorFactory>();
    }

    [TestCase("Data Source=:memory:;Local Provider=Native")]
    [TestCase(null)]
    public void UseAhtola_ClassifiesNativeLocalAsUnrestrictedServices(string? connectionString)
    {
        using var context = CreateContext(connectionString);

        context.GetService<IQuerySqlGeneratorFactory>().Should().BeOfType<AhtolaSqliteQuerySqlGeneratorFactory>();
        context.GetService<IQueryableMethodTranslatingExpressionVisitorFactory>()
            .Should().BeOfType<AhtolaSqliteQueryableMethodTranslatingExpressionVisitorFactory>();
        context.GetService<IHistoryRepository>().Should().BeOfType<SqliteHistoryRepository>();
        context.GetService<IMigrationsSqlGenerator>().Should().BeOfType<SqliteMigrationsSqlGenerator>();
    }

    [Test]
    public void UseAhtola_PropagatesAuthTokenToDirectRemoteRequests()
    {
        using var handler = new ScriptedHranaHandler();
        var priorFactory = AhtolaSqliteConnection.RemoteMessageHandlerFactory;
        AhtolaSqliteConnection.RemoteMessageHandlerFactory = () => handler;
        try
        {
            using var context = CreateContext("Data Source=turso://database.example;Auth Token=super-secret-token");
            _ = context.Widgets.Select(w => w.Id).ToList();

            handler.Authorization.Should().Contain("super-secret-token");
        }
        finally
        {
            AhtolaSqliteConnection.RemoteMessageHandlerFactory = priorFactory;
        }
    }

    private static RemoteWidgetContext CreateContext(string? connectionString)
    {
        var options = new DbContextOptionsBuilder<RemoteWidgetContext>()
            .UseAhtola(connectionString)
            .Options;
        return new RemoteWidgetContext(options);
    }
}
