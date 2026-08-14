using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Sqlite.Storage.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using Ahtola.EntityFrameworkCore.Sqlite.Query.Internal;
using Ahtola.EntityFrameworkCore.Sqlite.Storage.Internal;
using Ahtola.EntityFrameworkCore.Sqlite.Update.Internal;
using Ahtola.EntityFrameworkCore.Sqlite.Migrations.Internal;
using AhtolaSqliteConnection = Ahtola.Data.Sqlite.SqliteConnection;
using AhtolaSqliteConnectionStringBuilder = Ahtola.Data.Sqlite.SqliteConnectionStringBuilder;
using AhtolaLocalProvider = Ahtola.AhtolaLocalProvider;

namespace Microsoft.EntityFrameworkCore;

public static class AhtolaDbContextOptionsBuilderExtensions
{
    #if NET10_0_OR_GREATER
    private const int SupportedEntityFrameworkCoreMajorVersion = 10;
#else
    private const int SupportedEntityFrameworkCoreMajorVersion = 9;
#endif

    public static DbContextOptionsBuilder UseAhtola(
        this DbContextOptionsBuilder optionsBuilder,
        string? connectionString,
        Action<SqliteDbContextOptionsBuilder>? sqliteOptionsAction = null)
    {
        EnsureSupportedEntityFrameworkCoreVersion();
        var usesManagedLocalProvider = UsesManagedLocalProvider(connectionString);
        optionsBuilder.UseSqlite(connectionString, sqliteOptionsAction);
        return UseAhtolaServices(optionsBuilder, usesManagedLocalProvider);
    }

    public static DbContextOptionsBuilder UseAhtola(
        this DbContextOptionsBuilder optionsBuilder,
        AhtolaSqliteConnection connection,
        Action<SqliteDbContextOptionsBuilder>? sqliteOptionsAction = null)
        => UseAhtola(optionsBuilder, connection, contextOwnsConnection: false, sqliteOptionsAction);

    public static DbContextOptionsBuilder UseAhtola(
        this DbContextOptionsBuilder optionsBuilder,
        AhtolaSqliteConnection connection,
        bool contextOwnsConnection,
        Action<SqliteDbContextOptionsBuilder>? sqliteOptionsAction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        EnsureSupportedEntityFrameworkCoreVersion();
        var usesManagedLocalProvider = UsesManagedLocalProvider(connection.ConnectionString);
        optionsBuilder.UseSqlite(connection, contextOwnsConnection, sqliteOptionsAction);
        return UseAhtolaServices(optionsBuilder, usesManagedLocalProvider);
    }

    public static DbContextOptionsBuilder<TContext> UseAhtola<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        string? connectionString,
        Action<SqliteDbContextOptionsBuilder>? sqliteOptionsAction = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseAhtola((DbContextOptionsBuilder)optionsBuilder, connectionString, sqliteOptionsAction);

    public static DbContextOptionsBuilder<TContext> UseAhtola<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        AhtolaSqliteConnection connection,
        Action<SqliteDbContextOptionsBuilder>? sqliteOptionsAction = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseAhtola((DbContextOptionsBuilder)optionsBuilder, connection, sqliteOptionsAction);

    public static DbContextOptionsBuilder<TContext> UseAhtola<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        AhtolaSqliteConnection connection,
        bool contextOwnsConnection,
        Action<SqliteDbContextOptionsBuilder>? sqliteOptionsAction = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseAhtola((DbContextOptionsBuilder)optionsBuilder, connection, contextOwnsConnection, sqliteOptionsAction);

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(AhtolaSqliteRelationalConnection))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(AhtolaSqliteDatabaseCreator))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(AhtolaSqliteQuerySqlGeneratorFactory))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(AhtolaSqliteUpdateSqlGenerator))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(AhtolaManagedSqliteQuerySqlGeneratorFactory))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(AhtolaManagedSqliteQueryableMethodTranslatingExpressionVisitorFactory))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(AhtolaManagedSqliteHistoryRepository))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(AhtolaManagedSqliteMigrationsSqlGenerator))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(AhtolaSqliteParameterBasedSqlProcessorFactory))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(AhtolaSqliteQueryableMethodTranslatingExpressionVisitorFactory))]
    private static DbContextOptionsBuilder UseAhtolaServices(
        DbContextOptionsBuilder optionsBuilder,
        bool usesManagedLocalProvider)
    {
        var configuredOptions = optionsBuilder
            .ReplaceService<ISqliteRelationalConnection, AhtolaSqliteRelationalConnection>()
            .ReplaceService<IRelationalDatabaseCreator, AhtolaSqliteDatabaseCreator>()
            .ReplaceService<IQuerySqlGeneratorFactory, AhtolaSqliteQuerySqlGeneratorFactory>()
            .ReplaceService<IUpdateSqlGenerator, AhtolaSqliteUpdateSqlGenerator>();

        return usesManagedLocalProvider
            ? configuredOptions
                .ReplaceService<IQuerySqlGeneratorFactory, AhtolaManagedSqliteQuerySqlGeneratorFactory>()
                .ReplaceService<IQueryableMethodTranslatingExpressionVisitorFactory, AhtolaManagedSqliteQueryableMethodTranslatingExpressionVisitorFactory>()
                .ReplaceService<IHistoryRepository, AhtolaManagedSqliteHistoryRepository>()
                .ReplaceService<IMigrationsSqlGenerator, AhtolaManagedSqliteMigrationsSqlGenerator>()
                .ReplaceService<IRelationalParameterBasedSqlProcessorFactory, AhtolaSqliteParameterBasedSqlProcessorFactory>()
            : configuredOptions
                .ReplaceService<IQueryableMethodTranslatingExpressionVisitorFactory, AhtolaSqliteQueryableMethodTranslatingExpressionVisitorFactory>();
    }

    private static bool UsesManagedLocalProvider(string? connectionString)
    {
        if (connectionString is null)
            return false;

        var connectionOptions = new AhtolaSqliteConnectionStringBuilder(connectionString);
        if (IsRemoteAhtolaUrl(connectionOptions.DataSource))
        {
            throw new NotSupportedException(
                "UseAhtola supports only local Ahtola databases. Remote URLs require retry and transaction semantics that are not implemented yet; use AhtolaConnection directly for remote ADO.NET access.");
        }

        return !connectionOptions.IsLocalProviderConfigured
            || connectionOptions.LocalProvider == AhtolaLocalProvider.Managed;
    }

    private static void EnsureSupportedEntityFrameworkCoreVersion()
    {
        var loadedVersion = typeof(DbContext).Assembly.GetName().Version;
        if (loadedVersion?.Major != SupportedEntityFrameworkCoreMajorVersion)
        {
            throw new NotSupportedException(
                $"Ahtola.EntityFrameworkCore.Sqlite supports EF Core {SupportedEntityFrameworkCoreMajorVersion}.x, but EF Core {loadedVersion?.ToString() ?? "with an unknown version"} is loaded.");
        }
    }

    private static bool IsRemoteAhtolaUrl(string dataSource)
    {
        return Uri.TryCreate(dataSource, UriKind.Absolute, out var uri)
            && (uri.Scheme.Equals("libsql", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase));
    }
}
