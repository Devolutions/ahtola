using System.Data;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Sqlite.Storage.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using AhtolaSqliteConnection = Ahtola.Data.Sqlite.SqliteConnection;
using AhtolaSqliteConnectionStringBuilder = Ahtola.Data.Sqlite.SqliteConnectionStringBuilder;
using AhtolaSqliteCacheMode = Ahtola.Data.Sqlite.SqliteCacheMode;
using AhtolaSqliteException = Ahtola.Data.Sqlite.SqliteException;
using AhtolaSqliteOpenMode = Ahtola.Data.Sqlite.SqliteOpenMode;

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
            if (!connectionOptions.IsLocalProviderConfigured
                || connectionOptions.LocalProvider == AhtolaLocalProvider.Managed)
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

    public override bool Exists()
    {
        var connectionOptions = new AhtolaSqliteConnectionStringBuilder(connection.ConnectionString);
        if (connectionOptions.DataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || connectionOptions.Mode == AhtolaSqliteOpenMode.Memory)
        {
            return true;
        }

        if ((!connectionOptions.IsLocalProviderConfigured
                || connectionOptions.LocalProvider == Ahtola.AhtolaLocalProvider.Managed)
            && !IsRemoteAhtolaUrl(connectionOptions.DataSource)
            && File.Exists(ResolveDatabasePath(connectionOptions)))
        {
            return true;
        }

        using var readOnlyConnection = connection.CreateReadOnlyConnection();
        try
        {
            readOnlyConnection.Open(errorsExpected: true);
        }
        catch (AhtolaSqliteException ex) when (ex.SqliteErrorCode == SQLITE_CANTOPEN)
        {
            return false;
        }
        finally
        {
            readOnlyConnection.Close();
        }

        return true;
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

    public override void Delete()
    {
        var dbConnection = Dependencies.Connection.DbConnection;
        var wasOpen = dbConnection.State == ConnectionState.Open;
        var connectionOptions = new AhtolaSqliteConnectionStringBuilder(connection.ConnectionString);
        var path = wasOpen
            ? dbConnection.DataSource
            : ResolveDatabasePath(connectionOptions);

        if (IsRemoteAhtolaUrl(connectionOptions.DataSource))
            return;

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

    private static bool IsRemoteAhtolaUrl(string dataSource)
        => Uri.TryCreate(dataSource, UriKind.Absolute, out var uri)
            && (uri.Scheme.Equals("libsql", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase));

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
