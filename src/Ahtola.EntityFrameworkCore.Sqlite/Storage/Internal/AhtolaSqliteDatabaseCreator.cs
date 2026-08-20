using System.Data;
using System.Linq;
using System.Transactions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Sqlite.Storage.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using Ahtola;
using AhtolaSqliteConnection = Ahtola.Data.Sqlite.SqliteConnection;
using AhtolaSqliteConnectionStringBuilder = Ahtola.Data.Sqlite.SqliteConnectionStringBuilder;
using AhtolaSqliteCacheMode = Ahtola.Data.Sqlite.SqliteCacheMode;
using AhtolaSqliteException = Ahtola.Data.Sqlite.SqliteException;
using AhtolaSqliteOpenMode = Ahtola.Data.Sqlite.SqliteOpenMode;
using AhtolaSqliteRemoteException = Ahtola.Data.Sqlite.SqliteRemoteException;

namespace Ahtola.EntityFrameworkCore.Sqlite.Storage.Internal;

public class AhtolaSqliteDatabaseCreator(
    RelationalDatabaseCreatorDependencies dependencies,
    ISqliteRelationalConnection connection,
    IRawSqlCommandBuilder rawSqlCommandBuilder)
    : RelationalDatabaseCreator(dependencies)
{
    private const int SQLITE_CANTOPEN = 14;

    public override void Create()
    {
        Dependencies.Connection.Open();
        try
        {
            var connectionOptions = new AhtolaSqliteConnectionStringBuilder(connection.ConnectionString);
            if (!RequiresWalPragma(connectionOptions))
                return;

            rawSqlCommandBuilder.Build("PRAGMA journal_mode = 'wal';")
                .ExecuteNonQuery(
                    new RelationalCommandParameterObject(
                        Dependencies.Connection,
                        null,
                        null,
                        null,
                        Dependencies.CommandLogger,
                        CommandSource.Migrations));
        }
        finally
        {
            Dependencies.Connection.Close();
        }
    }

    public override async Task CreateAsync(CancellationToken cancellationToken = default)
    {
        await Dependencies.Connection.OpenAsync(cancellationToken, errorsExpected: false).ConfigureAwait(false);
        try
        {
            var connectionOptions = new AhtolaSqliteConnectionStringBuilder(connection.ConnectionString);
            if (!RequiresWalPragma(connectionOptions))
                return;

            await rawSqlCommandBuilder.Build("PRAGMA journal_mode = 'wal';")
                .ExecuteNonQueryAsync(
                    new RelationalCommandParameterObject(
                        Dependencies.Connection,
                        null,
                        null,
                        null,
                        Dependencies.CommandLogger,
                        CommandSource.Migrations),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await Dependencies.Connection.CloseAsync().ConfigureAwait(false);
        }
    }

    public override bool Exists()
    {
        var connectionOptions = new AhtolaSqliteConnectionStringBuilder(connection.ConnectionString);
        if (IsMemoryDataSource(connectionOptions))
            return true;

        var mode = AhtolaConnectionModeClassifier.Classify(connectionOptions.DataSource, connectionOptions.ReplicaPath);
        if (mode == AhtolaConnectionEndpointMode.EmbeddedReplica)
            return ReplicaExists(connectionOptions);

        if (TryFastPathExists(connectionOptions, mode))
            return true;

        return mode == AhtolaConnectionEndpointMode.RemoteHrana
            ? RemoteExists()
            : LocalOrReplicaReadOnlyProbeExists();
    }

    public override async Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
    {
        var connectionOptions = new AhtolaSqliteConnectionStringBuilder(connection.ConnectionString);
        if (IsMemoryDataSource(connectionOptions))
            return true;

        var mode = AhtolaConnectionModeClassifier.Classify(connectionOptions.DataSource, connectionOptions.ReplicaPath);
        if (mode == AhtolaConnectionEndpointMode.EmbeddedReplica)
            return ReplicaExists(connectionOptions);

        if (TryFastPathExists(connectionOptions, mode))
            return true;

        return mode == AhtolaConnectionEndpointMode.RemoteHrana
            ? await RemoteExistsAsync(cancellationToken).ConfigureAwait(false)
            : await LocalOrReplicaReadOnlyProbeExistsAsync(cancellationToken).ConfigureAwait(false);
    }

    public override bool HasTables()
    {
        var count = (long)rawSqlCommandBuilder
            .Build("SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"type\" = 'table' AND \"rootpage\" IS NOT NULL;")
            .ExecuteScalar(
                new RelationalCommandParameterObject(
                    Dependencies.Connection,
                    null,
                    null,
                    null,
                    Dependencies.CommandLogger,
                    CommandSource.Migrations))!;

        return count != 0;
    }

    public override async Task<bool> HasTablesAsync(CancellationToken cancellationToken = default)
    {
        var count = (long)(await rawSqlCommandBuilder
            .Build("SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"type\" = 'table' AND \"rootpage\" IS NOT NULL;")
            .ExecuteScalarAsync(
                new RelationalCommandParameterObject(
                    Dependencies.Connection,
                    null,
                    null,
                    null,
                    Dependencies.CommandLogger,
                    CommandSource.Migrations),
                cancellationToken)
            .ConfigureAwait(false))!;

        return count != 0;
    }

    public override bool EnsureCreated()
    {
        var connectionOptions = new AhtolaSqliteConnectionStringBuilder(connection.ConnectionString);
        if (!IsEmbeddedReplica(connectionOptions))
            return base.EnsureCreated();

        using var transactionScope = new TransactionScope(
            TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);

        // Unlike the base algorithm (which assumes "the database didn't exist locally" implies
        // "it needs CreateTables()"), an embedded replica's Create() bootstraps a full snapshot
        // from the remote — which may already contain every table the model needs if the remote
        // isn't empty. HasTables() must be checked *after* bootstrapping, not skipped because
        // Exists() (a local-only, no-network check) reported false beforehand, or a non-empty
        // remote gets a duplicate CREATE TABLE against a database that already has the schema.
        var operationsPerformed = false;
        if (!Exists())
        {
            Create();
            operationsPerformed = true;
        }

        if (!HasTables())
        {
            CreateTables();
            operationsPerformed = true;
        }

        RunSeeding(operationsPerformed);
        return operationsPerformed;
    }

    public override async Task<bool> EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        var connectionOptions = new AhtolaSqliteConnectionStringBuilder(connection.ConnectionString);
        if (!IsEmbeddedReplica(connectionOptions))
            return await base.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        using var transactionScope = new TransactionScope(
            TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);

        var operationsPerformed = false;
        if (!await ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            await CreateAsync(cancellationToken).ConfigureAwait(false);
            operationsPerformed = true;
        }

        if (!await HasTablesAsync(cancellationToken).ConfigureAwait(false))
        {
            await CreateTablesAsync(cancellationToken).ConfigureAwait(false);
            operationsPerformed = true;
        }

        await RunSeedingAsync(operationsPerformed, cancellationToken).ConfigureAwait(false);
        return operationsPerformed;
    }

    public override bool EnsureDeleted()
    {
        var connectionOptions = new AhtolaSqliteConnectionStringBuilder(connection.ConnectionString);
        if (!IsEmbeddedReplica(connectionOptions))
            return base.EnsureDeleted();

        // ReplicaExists() throws for an inconsistent local pair (the metadata sidecar present
        // without the database file, or vice versa) so that a routine existence check surfaces
        // the problem loudly rather than silently guessing. But the base EnsureDeleted() calls
        // Exists() *before* Delete() — so going through it here would mean the exact recovery
        // path (EnsureDeleted()) throws the same error it is meant to fix, before ever reaching
        // Delete(). Detect "is there anything local to remove" directly instead, so an
        // inconsistent pair (or any partial artifact set) can always be cleaned up.
        var replicaPath = connectionOptions.ReplicaPath;
        if (string.IsNullOrEmpty(replicaPath) || !HasAnyLocalReplicaArtifact(replicaPath))
            return false;

        Delete();
        return true;
    }

    public override async Task<bool> EnsureDeletedAsync(CancellationToken cancellationToken = default)
    {
        var connectionOptions = new AhtolaSqliteConnectionStringBuilder(connection.ConnectionString);
        if (!IsEmbeddedReplica(connectionOptions))
            return await base.EnsureDeletedAsync(cancellationToken).ConfigureAwait(false);

        var replicaPath = connectionOptions.ReplicaPath;
        if (string.IsNullOrEmpty(replicaPath) || !HasAnyLocalReplicaArtifact(replicaPath))
            return false;

        await DeleteAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private void RunSeeding(bool operationsPerformed)
    {
        var coreOptionsExtension =
            Dependencies.ContextOptions.FindExtension<CoreOptionsExtension>()
            ?? new CoreOptionsExtension();

        var seed = coreOptionsExtension.Seeder;
        if (seed is not null)
        {
            var context = Dependencies.CurrentContext.Context;
            using var transaction = context.Database.BeginTransaction();
            seed(context, operationsPerformed);
            transaction.Commit();
        }
        else if (coreOptionsExtension.AsyncSeeder is not null)
        {
            throw new InvalidOperationException(
                "The context is configured to use an asynchronous seeding method, but EnsureCreated "
                + "is a synchronous operation. Use EnsureCreatedAsync instead, or configure a "
                + "synchronous seeding method.");
        }
    }

    private async Task RunSeedingAsync(bool operationsPerformed, CancellationToken cancellationToken)
    {
        var coreOptionsExtension =
            Dependencies.ContextOptions.FindExtension<CoreOptionsExtension>()
            ?? new CoreOptionsExtension();

        var seedAsync = coreOptionsExtension.AsyncSeeder;
        if (seedAsync is not null)
        {
            var context = Dependencies.CurrentContext.Context;
            var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var _ = transaction.ConfigureAwait(false);
            await seedAsync(context, operationsPerformed, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (coreOptionsExtension.Seeder is not null)
        {
            throw new InvalidOperationException(
                "The context is configured to use a synchronous seeding method, but EnsureCreatedAsync "
                + "is an asynchronous operation. Use EnsureCreated instead, or configure an asynchronous "
                + "seeding method.");
        }
    }

    private static bool IsEmbeddedReplica(AhtolaSqliteConnectionStringBuilder connectionOptions)
        => AhtolaConnectionModeClassifier.Classify(connectionOptions.DataSource, connectionOptions.ReplicaPath)
            == AhtolaConnectionEndpointMode.EmbeddedReplica;

    private static bool HasAnyLocalReplicaArtifact(string replicaPath)
        => ManagedReplicaBootstrapper.GetLocalArtifactPaths(replicaPath).Any(File.Exists);

    public override void Delete()
    {
        var connectionOptions = new AhtolaSqliteConnectionStringBuilder(connection.ConnectionString);
        var mode = AhtolaConnectionModeClassifier.Classify(connectionOptions.DataSource, connectionOptions.ReplicaPath);

        // A direct remote Hrana database is provisioned and owned independently of this
        // process (e.g. via the Turso CLI/API); never treat its URL as a filesystem path or
        // attempt to delete it implicitly.
        if (mode == AhtolaConnectionEndpointMode.RemoteHrana)
            return;

        var dbConnection = Dependencies.Connection.DbConnection;
        var wasOpen = dbConnection.State == ConnectionState.Open;

        if (mode == AhtolaConnectionEndpointMode.EmbeddedReplica)
        {
            DeleteReplicaArtifacts(connectionOptions, wasOpen);
            return;
        }

        var path = wasOpen ? dbConnection.DataSource : ResolveDatabasePath(connectionOptions);
        if (wasOpen)
            Dependencies.Connection.Close();

        if (!path.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            AhtolaSqliteConnection.ClearAllPools();
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
        else if (wasOpen)
        {
            AhtolaSqliteConnection.ClearPool(new AhtolaSqliteConnection(Dependencies.Connection.ConnectionString));
        }

        if (wasOpen)
            Dependencies.Connection.Open();
    }

    public override async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        var connectionOptions = new AhtolaSqliteConnectionStringBuilder(connection.ConnectionString);
        var mode = AhtolaConnectionModeClassifier.Classify(connectionOptions.DataSource, connectionOptions.ReplicaPath);

        if (mode == AhtolaConnectionEndpointMode.RemoteHrana)
            return;

        var dbConnection = Dependencies.Connection.DbConnection;
        var wasOpen = dbConnection.State == ConnectionState.Open;

        if (mode == AhtolaConnectionEndpointMode.EmbeddedReplica)
        {
            await DeleteReplicaArtifactsAsync(connectionOptions, wasOpen, cancellationToken).ConfigureAwait(false);
            return;
        }

        var path = wasOpen ? dbConnection.DataSource : ResolveDatabasePath(connectionOptions);
        if (wasOpen)
            await Dependencies.Connection.CloseAsync().ConfigureAwait(false);

        if (!path.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            AhtolaSqliteConnection.ClearAllPools();
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
        else if (wasOpen)
        {
            AhtolaSqliteConnection.ClearPool(new AhtolaSqliteConnection(Dependencies.Connection.ConnectionString));
        }

        if (wasOpen)
            await Dependencies.Connection.OpenAsync(cancellationToken, errorsExpected: false).ConfigureAwait(false);
    }

    private void DeleteReplicaArtifacts(AhtolaSqliteConnectionStringBuilder connectionOptions, bool wasOpen)
    {
        if (wasOpen)
            Dependencies.Connection.Close();

        var replicaPath = connectionOptions.ReplicaPath;
        if (!string.IsNullOrEmpty(replicaPath))
        {
            AhtolaSqliteConnection.ClearAllPools();
            // Remove the database file and every sidecar a managed replica may have written
            // alongside it (-wal/-shm/-journal, the bootstrap/sync metadata, and the local
            // change journal) — leaving any of these behind causes the next bootstrap to find
            // an inconsistent partial state (see ManagedReplicaBootstrapper's own pair checks).
            foreach (var path in ManagedReplicaBootstrapper.GetLocalArtifactPaths(replicaPath))
                File.Delete(path);
        }

        // Unlike the local (non-replica) Delete()/DeleteAsync() convention below — which
        // reopens the connection to restore the pre-call state when it was open — an embedded
        // replica connection must be left closed here even if it was open before this call.
        // Opening an embedded-replica connection when the local database is absent triggers a
        // full remote bootstrap/download as a side effect, which would either silently
        // re-materialize everything this method just deleted (undoing EnsureDeleted()) or throw
        // after the delete already succeeded (if the remote happens to be unreachable). The
        // caller must explicitly reopen when it actually wants a fresh bootstrap.
    }

    private async Task DeleteReplicaArtifactsAsync(
        AhtolaSqliteConnectionStringBuilder connectionOptions,
        bool wasOpen,
        CancellationToken cancellationToken)
    {
        if (wasOpen)
            await Dependencies.Connection.CloseAsync().ConfigureAwait(false);

        var replicaPath = connectionOptions.ReplicaPath;
        if (!string.IsNullOrEmpty(replicaPath))
        {
            AhtolaSqliteConnection.ClearAllPools();
            foreach (var path in ManagedReplicaBootstrapper.GetLocalArtifactPaths(replicaPath))
                File.Delete(path);
        }

        // See DeleteReplicaArtifacts above: an embedded replica connection must stay closed
        // after deletion, never reopened here, or opening it would immediately re-bootstrap
        // the database this call just deleted.
    }

    /// <summary>Determines local replica existence purely from on-disk state (the database file
    /// and its metadata sidecar) without opening a connection. Opening an embedded-replica
    /// connection when the local database is absent triggers a full remote bootstrap/download as
    /// a side effect, which Exists()/ExistsAsync() must never do.</summary>
    private static bool ReplicaExists(AhtolaSqliteConnectionStringBuilder connectionOptions)
    {
        var replicaPath = connectionOptions.ReplicaPath;
        if (string.IsNullOrEmpty(replicaPath))
            return false;

        return ManagedReplicaBootstrapper.GetLocalState(replicaPath) switch
        {
            ManagedReplicaLocalState.Present => true,
            ManagedReplicaLocalState.Absent => false,
            _ => throw new InvalidOperationException(
                $"The local managed embedded replica state at '{replicaPath}' is inconsistent: " +
                "either the database file or its metadata sidecar exists locally without the " +
                "other. Call EnsureDeleted() to remove the partial local state before continuing."),
        };
    }

    private bool RemoteExists()
    {
        using var probeConnection = connection.CreateReadOnlyConnection();
        try
        {
            probeConnection.Open(errorsExpected: true);
            using var command = probeConnection.DbConnection.CreateCommand();
            command.CommandText = "SELECT 1;";
            command.ExecuteScalar();
            return true;
        }
        catch (AhtolaSqliteException ex) when (IsRemoteNotFound(ex))
        {
            return false;
        }
        finally
        {
            probeConnection.Close();
        }
    }

    private async Task<bool> RemoteExistsAsync(CancellationToken cancellationToken)
    {
        var probeConnection = connection.CreateReadOnlyConnection();
        await using var _ = probeConnection.ConfigureAwait(false);
        try
        {
            await probeConnection.OpenAsync(cancellationToken, errorsExpected: true).ConfigureAwait(false);
            using var command = probeConnection.DbConnection.CreateCommand();
            command.CommandText = "SELECT 1;";
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (AhtolaSqliteException ex) when (IsRemoteNotFound(ex))
        {
            return false;
        }
        finally
        {
            await probeConnection.CloseAsync().ConfigureAwait(false);
        }
    }

    // Only a genuine "the target database does not exist" signal means Exists() should report
    // false: either the transport itself reported HTTP 404, or the server mapped its own
    // not-found condition onto the same SQLITE_CANTOPEN code the local file-based probe already
    // uses for "this database cannot be opened because it isn't there" (see
    // LocalOrReplicaReadOnlyProbeExists). Anything else — auth failures (401/403), transient
    // failures (408/429/5xx, busy), or other protocol/network errors — must propagate so callers
    // do not mistake "the server rejected/couldn't be reached" for "the database doesn't exist".
    private static bool IsRemoteNotFound(AhtolaSqliteException exception)
        => exception is AhtolaSqliteRemoteException remote
            && (remote.HttpStatusCode == System.Net.HttpStatusCode.NotFound
                || remote.SqliteErrorCode == SQLITE_CANTOPEN);

    private bool LocalOrReplicaReadOnlyProbeExists()
    {
        using var probeConnection = connection.CreateReadOnlyConnection();
        try
        {
            probeConnection.Open(errorsExpected: true);
        }
        catch (AhtolaSqliteException ex) when (ex.SqliteErrorCode == SQLITE_CANTOPEN)
        {
            return false;
        }
        finally
        {
            probeConnection.Close();
        }

        return true;
    }

    private async Task<bool> LocalOrReplicaReadOnlyProbeExistsAsync(CancellationToken cancellationToken)
    {
        var probeConnection = connection.CreateReadOnlyConnection();
        await using var _ = probeConnection.ConfigureAwait(false);
        try
        {
            await probeConnection.OpenAsync(cancellationToken, errorsExpected: true).ConfigureAwait(false);
        }
        catch (AhtolaSqliteException ex) when (ex.SqliteErrorCode == SQLITE_CANTOPEN)
        {
            return false;
        }
        finally
        {
            await probeConnection.CloseAsync().ConfigureAwait(false);
        }

        return true;
    }

    private static bool IsMemoryDataSource(AhtolaSqliteConnectionStringBuilder connectionOptions)
        => connectionOptions.DataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || connectionOptions.Mode == AhtolaSqliteOpenMode.Memory;

    private static bool TryFastPathExists(AhtolaSqliteConnectionStringBuilder connectionOptions, AhtolaConnectionEndpointMode mode)
        => mode == AhtolaConnectionEndpointMode.Local && File.Exists(ResolveDatabasePath(connectionOptions));

    // Only an explicitly-configured native local provider benefits from forcing WAL mode
    // here; the managed engine picks its own journaling behavior, and remote/embedded-replica
    // connections have no local file for this process to journal.
    private static bool RequiresWalPragma(AhtolaSqliteConnectionStringBuilder connectionOptions)
        => AhtolaConnectionModeClassifier.Classify(connectionOptions.DataSource, connectionOptions.ReplicaPath)
                == AhtolaConnectionEndpointMode.Local
            && connectionOptions.IsLocalProviderConfigured
            && connectionOptions.LocalProvider == Ahtola.AhtolaLocalProvider.Native;

    private static string ResolveDatabasePath(AhtolaSqliteConnectionStringBuilder connectionOptions)
    {
        var dataSource = connectionOptions.DataSource;
        if (string.IsNullOrEmpty(dataSource) || dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
            return ":memory:";

        if (connectionOptions.Mode == AhtolaSqliteOpenMode.Memory)
        {
            return connectionOptions.Cache == AhtolaSqliteCacheMode.Shared
                ? GetSharedMemoryFile(dataSource)
                : ":memory:";
        }

        if (dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return ResolveUriDatabasePath(dataSource);

        const string dataDirectory = "|DataDirectory|";
        if (dataSource.StartsWith(dataDirectory, StringComparison.OrdinalIgnoreCase))
        {
            var baseDirectory = AppDomain.CurrentDomain.GetData("DataDirectory") as string
                                ?? AppContext.BaseDirectory;
            dataSource = Path.Combine(
                baseDirectory,
                dataSource[dataDirectory.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        return Path.IsPathRooted(dataSource)
            ? dataSource
            : Path.Combine(AppContext.BaseDirectory, dataSource);
    }

    private static string ResolveUriDatabasePath(string dataSource)
    {
        var queryStart = dataSource.IndexOf('?', StringComparison.Ordinal);
        var path = queryStart < 0 ? dataSource[5..] : dataSource[5..queryStart];
        var query = queryStart < 0 ? string.Empty : dataSource[(queryStart + 1)..];
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            if (pieces[0].Equals("mode", StringComparison.OrdinalIgnoreCase)
                && pieces.Length == 2
                && pieces[1].Equals("memory", StringComparison.OrdinalIgnoreCase))
            {
                return ":memory:";
            }
        }

        return Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path);
    }

    private static string GetSharedMemoryFile(string dataSource)
    {
        var sanitized = string.Join(
            "_",
            dataSource.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (sanitized.Length == 0)
            sanitized = Math.Abs(dataSource.GetHashCode(StringComparison.Ordinal)).ToString(System.Globalization.CultureInfo.InvariantCulture);

        return Path.Combine(Path.GetTempPath(), "Ahtola-dotnet-shared-" + sanitized + ".db");
    }
}
