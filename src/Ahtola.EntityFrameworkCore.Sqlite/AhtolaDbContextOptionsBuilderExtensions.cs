using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Sqlite.Storage.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using Ahtola;
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
        var mode = ClassifyConnectionMode(connectionString);
        optionsBuilder.UseSqlite(connectionString, sqliteOptionsAction);
        return UseAhtolaServices(optionsBuilder, mode);
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
        var mode = ClassifyConnectionMode(connection.ConnectionString);
        optionsBuilder.UseSqlite(connection, contextOwnsConnection, sqliteOptionsAction);
        return UseAhtolaServices(optionsBuilder, mode);
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
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(AhtolaRemoteSqliteQuerySqlGeneratorFactory))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(AhtolaReplicaSqliteQuerySqlGeneratorFactory))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(AhtolaManagedSqliteQueryableMethodTranslatingExpressionVisitorFactory))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(AhtolaManagedSqliteHistoryRepository))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(AhtolaManagedMigrationCommandExecutor))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(AhtolaManagedSqliteMigrationsSqlGenerator))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(AhtolaSqliteParameterBasedSqlProcessorFactory))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(AhtolaRestrictedSqliteParameterBasedSqlProcessorFactory))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(AhtolaSqliteQueryableMethodTranslatingExpressionVisitorFactory))]
    private static DbContextOptionsBuilder UseAhtolaServices(
        DbContextOptionsBuilder optionsBuilder,
        AhtolaConnectionMode mode)
    {
        var configuredOptions = optionsBuilder
            .ReplaceService<ISqliteRelationalConnection, AhtolaSqliteRelationalConnection>()
            .ReplaceService<IRelationalDatabaseCreator, AhtolaSqliteDatabaseCreator>()
            .ReplaceService<IUpdateSqlGenerator, AhtolaSqliteUpdateSqlGenerator>();

        // Managed-engine-specific service replacements (JSON1 table-valued functions disabled,
        // migrations/history restricted to what the managed engine can execute) apply only to
        // ManagedLocal and EmbeddedReplica: both run queries against a local managed-engine
        // database (a private file, or the local replica copy). NativeLocal and RemoteHrana run
        // against a real SQLite-compatible engine (the native SDK, or the remote/Turso server)
        // and keep the unrestricted, JSON1-enabled services — RemoteHrana additionally layers in
        // the remote-aware translation restrictions (regexp/decimal ef_*/EF_DECIMAL) that neither
        // it nor EmbeddedReplica can register client-side, and leaves migrations/history at EF
        // Core's stock SQLite services rather than the managed-engine-restricted ones.
        return mode switch
        {
            AhtolaConnectionMode.ManagedLocal => configuredOptions
                .ReplaceService<IQuerySqlGeneratorFactory, AhtolaManagedSqliteQuerySqlGeneratorFactory>()
                .ReplaceService<IQueryableMethodTranslatingExpressionVisitorFactory, AhtolaManagedSqliteQueryableMethodTranslatingExpressionVisitorFactory>()
                .ReplaceService<IHistoryRepository, AhtolaManagedSqliteHistoryRepository>()
                .ReplaceService<IMigrationCommandExecutor, AhtolaManagedMigrationCommandExecutor>()
                .ReplaceService<IMigrationsSqlGenerator, AhtolaManagedSqliteMigrationsSqlGenerator>()
                .ReplaceService<IRelationalParameterBasedSqlProcessorFactory, AhtolaSqliteParameterBasedSqlProcessorFactory>(),

            AhtolaConnectionMode.EmbeddedReplica => configuredOptions
                .ReplaceService<IQuerySqlGeneratorFactory, AhtolaReplicaSqliteQuerySqlGeneratorFactory>()
                .ReplaceService<IQueryableMethodTranslatingExpressionVisitorFactory, AhtolaManagedSqliteQueryableMethodTranslatingExpressionVisitorFactory>()
                .ReplaceService<IHistoryRepository, AhtolaManagedSqliteHistoryRepository>()
                .ReplaceService<IMigrationCommandExecutor, AhtolaManagedMigrationCommandExecutor>()
                .ReplaceService<IMigrationsSqlGenerator, AhtolaManagedSqliteMigrationsSqlGenerator>()
                .ReplaceService<IRelationalParameterBasedSqlProcessorFactory, AhtolaRestrictedSqliteParameterBasedSqlProcessorFactory>(),

            AhtolaConnectionMode.RemoteHrana => configuredOptions
                .ReplaceService<IQuerySqlGeneratorFactory, AhtolaRemoteSqliteQuerySqlGeneratorFactory>()
                .ReplaceService<IQueryableMethodTranslatingExpressionVisitorFactory, AhtolaSqliteQueryableMethodTranslatingExpressionVisitorFactory>()
                .ReplaceService<IRelationalParameterBasedSqlProcessorFactory, AhtolaRestrictedSqliteParameterBasedSqlProcessorFactory>(),

            _ /* NativeLocal */ => configuredOptions
                .ReplaceService<IQuerySqlGeneratorFactory, AhtolaSqliteQuerySqlGeneratorFactory>()
                .ReplaceService<IQueryableMethodTranslatingExpressionVisitorFactory, AhtolaSqliteQueryableMethodTranslatingExpressionVisitorFactory>(),
        };
    }

    /// <summary>
    /// Classifies a connection string into the execution mode that determines which EF Core
    /// services <see cref="UseAhtolaServices"/> wires up. Uses the same
    /// <see cref="AhtolaConnectionModeClassifier"/> that the ADO.NET facades use, so a
    /// <c>libsql://</c>/<c>turso://</c>/<c>http(s)://</c>/<c>ws(s)://</c> Data Source (with or
    /// without a Replica Path) is recognized identically here and in
    /// <see cref="Ahtola.Data.Sqlite.SqliteConnection"/>.
    /// </summary>
    private static AhtolaConnectionMode ClassifyConnectionMode(string? connectionString)
    {
        // No connection string (e.g. a context configured to attach one later) has no local
        // file or remote endpoint to classify; preserve the historical default of treating it
        // as a native local connection.
        if (connectionString is null)
            return AhtolaConnectionMode.NativeLocal;

        var connectionOptions = new AhtolaSqliteConnectionStringBuilder(connectionString);
        var endpointMode = AhtolaConnectionModeClassifier.Classify(connectionOptions.DataSource, connectionOptions.ReplicaPath);
        return endpointMode switch
        {
            AhtolaConnectionEndpointMode.RemoteHrana => AhtolaConnectionMode.RemoteHrana,
            AhtolaConnectionEndpointMode.EmbeddedReplica => AhtolaConnectionMode.EmbeddedReplica,
            _ => !connectionOptions.IsLocalProviderConfigured || connectionOptions.LocalProvider == AhtolaLocalProvider.Managed
                ? AhtolaConnectionMode.ManagedLocal
                : AhtolaConnectionMode.NativeLocal,
        };
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
}
