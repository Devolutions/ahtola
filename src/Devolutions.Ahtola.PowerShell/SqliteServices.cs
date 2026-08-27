using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Text.RegularExpressions;
using Ahtola;
using Ahtola.Data.Sqlite;

namespace Ahtola.PSSqlite;

internal static class StringExpansion
{
    private static readonly Regex PowerShellEnvironmentVariable = new(
        @"\$env:(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Expand(string value)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value);
        expanded = PowerShellEnvironmentVariable.Replace(
            expanded,
            match => Environment.GetEnvironmentVariable(match.Groups["name"].Value) ?? string.Empty);
        return expanded;
    }
}

internal static class TursoCloudConnectionFactory
{
    internal const string RemoteUrlEnvironmentVariable = "TURSO_REMOTE_URL";
    internal const string AuthTokenEnvironmentVariable = "TURSO_AUTH_TOKEN";

    public static AhtolaCloudConnection Create(
        string? remoteUrl,
        SecureString? authToken,
        string? replicaPath,
        bool useTursoEnvironment,
        int syncInterval,
        TimeSpan? longPollTimeout = null,
        long? pushOperationsThreshold = null,
        long? pullBytesThreshold = null,
        AhtolaPartialBootstrapOptions? partialBootstrap = null,
        SecureString? remoteEncryptionKey = null,
        AhtolaRemoteEncryptionCipher? remoteEncryptionCipher = null)
    {
        var endpoint = ResolveEndpoint(remoteUrl, useTursoEnvironment);
        var token = ResolveAuthToken(authToken, useTursoEnvironment);
        var encryptionKey = ResolveSecret(remoteEncryptionKey, nameof(remoteEncryptionKey));
        string? resolvedReplicaPath = null;
        try
        {
            AhtolaConnection connection;
            if (string.IsNullOrWhiteSpace(replicaPath))
            {
                var builder = new AhtolaConnectionStringBuilder
                {
                    DataSource = endpoint.AbsoluteUri,
                };
                if (!string.IsNullOrWhiteSpace(token))
                    builder.AuthToken = token;

                connection = new AhtolaConnection(builder.ConnectionString);
            }
            else
            {
                resolvedReplicaPath = Path.GetFullPath(StringExpansion.Expand(replicaPath));
                var options = new AhtolaReplicaOptions(resolvedReplicaPath, endpoint, token)
                {
                    SyncInterval = syncInterval,
                    LongPollTimeout = longPollTimeout,
                    PushOperationsThreshold = pushOperationsThreshold,
                    PullBytesThreshold = pullBytesThreshold,
                    PartialBootstrap = partialBootstrap,
                    RemoteEncryption = encryptionKey is null
                        ? null
                        : new AhtolaRemoteEncryptionOptions(
                            encryptionKey,
                            remoteEncryptionCipher
                            ?? throw new ArgumentException(
                                "RemoteEncryptionCipher is required with RemoteEncryptionKey.",
                                nameof(remoteEncryptionCipher))),
                };
                connection = AhtolaConnection.CreateReplica(options);
            }

            try
            {
                connection.Open();
                return new AhtolaCloudConnection(connection, endpoint, resolvedReplicaPath);
            }
            catch
            {
                try
                {
                    connection.Dispose();
                }
                catch
                {
                    // Preserve the original provider exception and its replica metadata.
                }
                throw;
            }
        }
        finally
        {
            token = string.Empty;
            encryptionKey = string.Empty;
        }
    }

    private static Uri ResolveEndpoint(string? remoteUrl, bool useTursoEnvironment)
    {
        var value = string.IsNullOrWhiteSpace(remoteUrl)
            ? useTursoEnvironment
                ? Environment.GetEnvironmentVariable(RemoteUrlEnvironmentVariable)
                : null
            : StringExpansion.Expand(remoteUrl);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(useTursoEnvironment
                ? "TursoUrl must be supplied or TURSO_REMOTE_URL must be set when UseTursoEnvironment is specified."
                : "TursoUrl is required unless UseTursoEnvironment is specified.");
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint)
            || string.IsNullOrWhiteSpace(endpoint.Host)
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment)
            || !(endpoint.Scheme.Equals("libsql", StringComparison.OrdinalIgnoreCase)
                 || endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                 || endpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "TursoUrl must be an absolute libsql, HTTPS, or HTTP URL without user information, a query string, or a fragment.");
        }

        return endpoint;
    }

    private static string? ResolveAuthToken(SecureString? authToken, bool useTursoEnvironment)
    {
        if (authToken is not null)
        {
            var value = new NetworkCredential(string.Empty, authToken).Password;
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("AuthToken cannot be empty.");
            return value;
        }

        return useTursoEnvironment
            ? Environment.GetEnvironmentVariable(AuthTokenEnvironmentVariable)
            : null;
    }

    private static string? ResolveSecret(SecureString? secret, string parameterName)
    {
        if (secret is null)
            return null;

        var value = new NetworkCredential(string.Empty, secret).Password;
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("The secure value cannot be empty.", parameterName);
        return value;
    }
}

public sealed class SQLiteDBConfig
{
    public string? DatabasePath { get; set; }
    public string? DatabaseFile { get; set; }
    public string? ConnectionString { get; set; }
    public string Version { get; set; } = "0";
    public string DBVersion
    {
        get => Version;
        set => Version = value;
    }

    public SqliteDBSchema? Schema { get; set; }

    public SQLiteDBConfig()
    {
    }

    public SQLiteDBConfig(string databasePath, string databaseFile)
    {
        DatabasePath = GetAbsolutePath(StringExpansion.Expand(databasePath));
        DatabaseFile = StringExpansion.Expand(databaseFile);
        ConnectionString = BuildConnectionString(DatabasePath, DatabaseFile);
    }

    public SQLiteDBConfig(string stringInfo)
    {
        if (stringInfo.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
        {
            ConnectionString = stringInfo;
        }
        else
        {
            throw new ArgumentException($"Connection strings must start with 'Data Source='. Received: {stringInfo}");
        }
    }

    public SQLiteDBConfig(IDictionary definition)
    {
        SetObjectProperties(definition);
    }

    public string GetDatabaseSDL()
    {
        if (Schema is null)
        {
            throw new InvalidOperationException("Schema is not defined in the database configuration.");
        }

        return Schema.GetSchemaSDL();
    }

    public bool DatabaseExists()
    {
        if (IsMemoryConnection(ConnectionString))
        {
            return true;
        }

        var path = GetDatabaseFilePath();
        return path is not null && File.Exists(path);
    }

    public void RemoveDatabase()
    {
        if (IsMemoryConnection(ConnectionString))
        {
            return;
        }

        var path = GetDatabaseFilePath();
        if (path is null)
        {
            return;
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void CreateDatabase(bool force = false, bool skipSchemaUpdate = false)
    {
        if (DatabaseExists() && !force)
        {
            throw new InvalidOperationException("Database already exists. Use Force to overwrite.");
        }

        if (DatabaseExists() && force)
        {
            RemoveDatabase();
        }

        if (!IsMemoryConnection(ConnectionString) && !string.IsNullOrWhiteSpace(DatabasePath))
        {
            Directory.CreateDirectory(DatabasePath!);
        }

        if (!skipSchemaUpdate)
        {
            UpdateDatabaseSchema();
        }
    }

    public void UpdateDatabaseSchema()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException("A SQLite connection string is not configured.");
        }

        try
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = GetDatabaseSDL();
            command.ExecuteNonQuery();

            command.CommandText = "CREATE TABLE IF NOT EXISTS _metadata (key TEXT PRIMARY KEY, value TEXT);";
            command.ExecuteNonQuery();

            command.CommandText = "INSERT OR REPLACE INTO _metadata (key, value) VALUES ('version', $version);";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$version", Version);
            command.ExecuteNonQuery();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("Failed to update database: " + exception.Message, exception);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    public string? GetDatabaseFilePath()
    {
        if (!string.IsNullOrWhiteSpace(DatabasePath) && !string.IsNullOrWhiteSpace(DatabaseFile))
        {
            return Path.Combine(DatabasePath!, DatabaseFile!);
        }

        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return null;
        }

        var builder = new SqliteConnectionStringBuilder(ConnectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource) ||
            string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return GetAbsolutePath(builder.DataSource);
    }

    private void SetObjectProperties(IDictionary definition, Func<string, string>? expander = null)
    {
        expander ??= StringExpansion.Expand;

        if (DefinitionReader.Contains(definition, "DatabasePath"))
        {
            DatabasePath = GetAbsolutePath(expander(DefinitionReader.AsString(DefinitionReader.Get(definition, "DatabasePath")) ?? string.Empty));
        }

        if (DefinitionReader.Contains(definition, "DatabaseFile"))
        {
            DatabaseFile = expander(DefinitionReader.AsString(DefinitionReader.Get(definition, "DatabaseFile")) ?? string.Empty);
        }

        if (DefinitionReader.Contains(definition, "ConnectionString"))
        {
            ConnectionString = expander(DefinitionReader.AsString(DefinitionReader.Get(definition, "ConnectionString")) ?? string.Empty);
        }
        else if (!string.IsNullOrWhiteSpace(DatabasePath) && !string.IsNullOrWhiteSpace(DatabaseFile))
        {
            ConnectionString = BuildConnectionString(DatabasePath, DatabaseFile);
        }
        else
        {
            throw new ArgumentException("DatabasePath and DatabaseFile must be set to construct a valid connection string.");
        }

        if (DefinitionReader.Contains(definition, "Version"))
        {
            Version = DefinitionReader.AsString(DefinitionReader.Get(definition, "Version")) ?? "0";
        }

        if (DefinitionReader.Get(definition, "Schema") is IDictionary schema)
        {
            Schema = new SqliteDBSchema(schema);
        }
    }

    private static string BuildConnectionString(string path, string file)
    {
        return new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(path, file)
        }.ToString();
    }

    private static bool IsMemoryConnection(string? connectionString)
    {
        return !string.IsNullOrWhiteSpace(connectionString) &&
            connectionString.IndexOf(":memory:", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    internal static string GetAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Directory.GetCurrentDirectory();
        }

        return Path.GetFullPath(path);
    }
}

public sealed class DBVersionComparisonResult
{
    public string? CurrentVersion { get; set; }
    public string? ExpectedVersion { get; set; }
    public bool IsDeployed { get; set; }
    public string direction { get; set; } = "!=";
    public List<string> Reasons { get; } = new();

    public override string ToString()
    {
        return $"{CurrentVersion} {direction} {ExpectedVersion}";
    }
}

public static class DatabaseVersion
{
    public static DBVersionComparisonResult Compare(SQLiteDBConfig config, string? expectedVersion = null)
    {
        expectedVersion ??= config.Version;
        var result = new DBVersionComparisonResult
        {
            ExpectedVersion = expectedVersion
        };

        if (!config.DatabaseExists())
        {
            result.Reasons.Add("Database does not exist.");
            return result;
        }

        try
        {
            var metadata = MetadataStore.Get(config.ConnectionString!, new[] { "version" });
            if (metadata is null)
            {
                result.Reasons.Add("Metadata table not found.");
                return result;
            }

            if (!metadata.Contains("version") || string.IsNullOrEmpty(metadata["version"]?.ToString()))
            {
                result.Reasons.Add("Database version is not set in the metadata.");
                return result;
            }

            result.CurrentVersion = metadata["version"]?.ToString();
            result.IsDeployed = true;
            if (string.IsNullOrEmpty(expectedVersion))
            {
                result.ExpectedVersion = null;
                result.direction = ">";
                return result;
            }

            var comparison = CompareVersions(result.CurrentVersion!, expectedVersion);
            result.direction = comparison switch
            {
                < 0 => "<",
                > 0 => ">",
                _ => "=="
            };
            result.Reasons.Add($"Database version comparison: {result.CurrentVersion} {result.direction} {expectedVersion}.");
            return result;
        }
        catch (Exception exception)
        {
            result.Reasons.Add("Failed to retrieve metadata from the database: " + exception.Message);
            return result;
        }
    }

    private static int CompareVersions(string current, string expected)
    {
        if (int.TryParse(current, NumberStyles.Integer, CultureInfo.InvariantCulture, out var currentInt) &&
            int.TryParse(expected, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expectedInt))
        {
            return currentInt.CompareTo(expectedInt);
        }

        var currentNumeric = current.Split(new[] { '-' }, 2, StringSplitOptions.None)[0];
        var expectedNumeric = expected.Split(new[] { '-' }, 2, StringSplitOptions.None)[0];
        if (Version.TryParse(currentNumeric, out var currentVersion) &&
            Version.TryParse(expectedNumeric, out var expectedVersion))
        {
            return currentVersion.CompareTo(expectedVersion);
        }

        return string.Compare(current, expected, StringComparison.OrdinalIgnoreCase);
    }
}

public static class ConnectionFactory
{
    public static SqliteConnection Create(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("ConnectionString is required.");
        }

        return new SqliteConnection(connectionString);
    }

    public static SqliteConnection Create(string databasePath, string databaseFile)
    {
        var path = SQLiteDBConfig.GetAbsolutePath(StringExpansion.Expand(databasePath));
        Directory.CreateDirectory(path);
        return Create(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(path, StringExpansion.Expand(databaseFile))
        }.ToString());
    }
}

public sealed class QueryOptions
{
    public string OutputFormat { get; set; } = "PSCustomObject";
    public int CommandTimeout { get; set; } = 30;
    public string? TableName { get; set; }
    public DbTransaction? Transaction { get; set; }
}

public static class QueryExecutor
{
    public static object? Execute(
        DbConnection connection,
        string commandText,
        IDictionary? parameters,
        QueryOptions? options = null)
    {
        options ??= new QueryOptions();
        if (connection.State != ConnectionState.Open)
        {
            connection.Open();
        }

        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = options.CommandTimeout;
        if (options.Transaction is not null)
        {
            command.Transaction = options.Transaction;
        }

        AddParameters(command, parameters);
        var outputFormat = options.OutputFormat.ToUpperInvariant();
        if (outputFormat == "SCALAR")
        {
            return command.ExecuteScalar();
        }

        if (outputFormat == "NONQUERY")
        {
            return command.ExecuteNonQuery();
        }

        using var readerForTable = command.ExecuteReader();
        if (outputFormat == "DATASET")
        {
            return ToDataSet(readerForTable, options.TableName);
        }

        var dataTable = new DataTable();
        dataTable.Load(readerForTable);
        if (!string.IsNullOrWhiteSpace(options.TableName))
        {
            dataTable.TableName = options.TableName;
        }

        if (outputFormat is "DATAREADER" or "DETACHEDDATAREADER")
        {
            // A command-owned reader cannot outlive this method safely, so this is a snapshot.
            return dataTable.CreateDataReader();
        }

        return ConvertResult(dataTable, outputFormat);
    }

    public static DataTable ExecuteDataTable(
        DbConnection connection,
        string commandText,
        IDictionary? parameters = null,
        DbTransaction? transaction = null)
    {
        return (DataTable)Execute(
            connection,
            commandText,
            parameters,
            new QueryOptions
            {
                OutputFormat = "DataTable",
                Transaction = transaction
            })!;
    }

    private static void AddParameters(DbCommand command, IDictionary? parameters)
    {
        if (parameters is null)
        {
            return;
        }

        foreach (DictionaryEntry entry in parameters)
        {
            var parameter = command.CreateParameter();
            var name = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty;
            parameter.ParameterName = name.StartsWith("@", StringComparison.Ordinal) ||
                                      name.StartsWith("$", StringComparison.Ordinal) ||
                                      name.StartsWith(":", StringComparison.Ordinal)
                ? name
                : "@" + name;
            parameter.Value = entry.Value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }

    public static object ConvertResult(DataTable table, string outputFormat)
    {
        return outputFormat.ToUpperInvariant() switch
        {
            "DATATABLE" => table,
            "ORDEREDDICTIONARY" => ToOrderedDictionaries(table),
            "PSCUSTOMOBJECT" => ToPowerShellObjects(table),
            _ => table
        };
    }

    private static DataSet ToDataSet(IDataReader reader, string? tableName)
    {
        var dataSet = new DataSet();
        var resultSet = 0;
        do
        {
            var table = new DataTable();
            table.Load(reader);
            table.TableName = string.IsNullOrWhiteSpace(tableName)
                ? $"Result{resultSet}"
                : resultSet == 0
                    ? tableName
                    : $"{tableName}{resultSet}";
            dataSet.Tables.Add(table);
            resultSet++;
        }
        while (!reader.IsClosed && reader.NextResult());

        return dataSet;
    }

    private static IEnumerable ToOrderedDictionaries(DataTable table)
    {
        var results = new List<OrderedDictionary>();
        foreach (DataRow row in table.Rows)
        {
            var result = new OrderedDictionary();
            foreach (DataColumn column in table.Columns)
            {
                result[column.ColumnName] = row[column] is DBNull ? null : row[column];
            }

            results.Add(result);
        }

        return results;
    }

    private static IEnumerable ToPowerShellObjects(DataTable table)
    {
        // Isolate SMA type refs in a nested type so missing System.Management.Automation
        // fails at the call boundary (catchable) rather than during JIT of this method.
        try
        {
            return PowerShellObjectMaterializer.Materialize(table);
        }
        catch (Exception ex) when (
            ex is FileNotFoundException
            or FileLoadException
            or TypeLoadException
            or MissingMethodException
            or NullReferenceException)
        {
            return ToOrderedDictionaries(table);
        }
    }

    /// <summary>
    /// Nested so SMA dependencies are only resolved when materializing PSCustomObject rows.
    /// </summary>
    private static class PowerShellObjectMaterializer
    {
        public static IEnumerable Materialize(DataTable table)
        {
            var results = new List<object>();
            foreach (DataRow row in table.Rows)
            {
                var psObject = System.Management.Automation.PSObject.AsPSObject(new object());
                if (psObject?.Properties is null)
                {
                    // PowerShellStandard reference facade — not a real host SMA.
                    throw new TypeLoadException("System.Management.Automation.PSObject.Properties is unavailable.");
                }

                foreach (DataColumn column in table.Columns)
                {
                    psObject.Properties.Add(new System.Management.Automation.PSNoteProperty(
                        column.ColumnName,
                        row[column] is DBNull ? null : row[column]));
                }

                if (!string.IsNullOrWhiteSpace(table.TableName))
                {
                    psObject.TypeNames.Insert(0, table.TableName);
                }

                results.Add(psObject);
            }

            return results;
        }
    }
}

public static class MetadataStore
{
    public static OrderedDictionary? Get(string connectionString, IReadOnlyCollection<string> keys)
    {
        using var connection = ConnectionFactory.Create(connectionString);
        return Get(connection, keys);
    }

    public static OrderedDictionary? Get(SqliteConnection connection, IReadOnlyCollection<string> keys)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(keys);

        if (connection.State != ConnectionState.Open)
        {
            connection.Open();
        }

        using var tableCheck = connection.CreateCommand();
        tableCheck.CommandText = "SELECT name FROM sqlite_schema WHERE name = $name COLLATE NOCASE";
        tableCheck.Parameters.AddWithValue("$name", "_metadata");
        if (tableCheck.ExecuteScalar() is null)
        {
            return null;
        }

        using var command = connection.CreateCommand();
        var parameters = new Dictionary<string, object?>();
        if (keys.Count == 0 || keys.Contains("*"))
        {
            command.CommandText = "SELECT key, value FROM _metadata;";
        }
        else
        {
            var placeholders = new List<string>();
            var index = 0;
            foreach (var key in keys)
            {
                var name = "$key" + index++;
                placeholders.Add(name);
                parameters[name] = key;
            }

            command.CommandText = $"SELECT key, value FROM _metadata WHERE key IN ({string.Join(", ", placeholders)});";
        }

        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Key, parameter.Value ?? DBNull.Value);
        }

        using var reader = command.ExecuteReader();
        var result = new OrderedDictionary();
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetValue(1);
        }

        return result;
    }
}

public sealed class SqliteFilter
{
    public string PredicateSql { get; }
    public IDictionary Parameters { get; }

    public SqliteFilter(string predicateSql, IDictionary parameters)
    {
        PredicateSql = predicateSql;
        Parameters = parameters;
    }
}

public static class CrudSqlBuilder
{
    public static SqliteFilter BuildFilter(
        IDictionary? clauses,
        IReadOnlyCollection<string> columns,
        bool caseSensitive,
        Action<string>? warning = null)
    {
        var predicates = new List<string> { "1=1" };
        var parameters = new OrderedDictionary();
        var index = 0;

        if (clauses is null)
        {
            return new SqliteFilter(string.Join(" AND ", predicates), parameters);
        }

        foreach (DictionaryEntry entry in clauses)
        {
            var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty;
            if (TryFindColumn(columns, key, out var column))
            {
                if (entry.Value is null)
                {
                    continue;
                }

                var parameterName = "$clause" + index++;
                var expression = SqlIdentifier.Quote(column!);
                if (!caseSensitive)
                {
                    expression += " COLLATE NOCASE";
                }

                if (entry.Value is string text && text.IndexOf('*') >= 0)
                {
                    predicates.Add($"{expression} LIKE {parameterName}");
                    parameters[parameterName] = text.Replace("*", "%");
                }
                else
                {
                    predicates.Add($"{expression} = {parameterName}");
                    parameters[parameterName] = entry.Value;
                }

                continue;
            }

            if (TryFindRangeColumn(columns, key, "Before", out column))
            {
                var parameterName = "$clause" + index++;
                predicates.Add($"{SqlIdentifier.Quote(column!)} < {parameterName}");
                if (entry.Value is not null)
                {
                    parameters[parameterName] = entry.Value;
                }

                continue;
            }

            if (TryFindRangeColumn(columns, key, "After", out column))
            {
                var parameterName = "$clause" + index++;
                predicates.Add($"{SqlIdentifier.Quote(column!)} > {parameterName}");
                if (entry.Value is not null)
                {
                    parameters[parameterName] = entry.Value;
                }

                continue;
            }

            warning?.Invoke($"Column '{key}' is not a valid column.");
        }

        return new SqliteFilter(string.Join(" AND ", predicates), parameters);
    }

    public static object? ExecuteSelect(
        SQLiteDBConfig config,
        string tableName,
        IDictionary? clauses,
        SqliteConnection connection,
        string outputFormat,
        bool caseSensitive,
        SqliteTransaction? transaction = null,
        Action<string>? warning = null)
    {
        var selectable = config.Schema?.GetSelectable(tableName)
            ?? throw new ArgumentException($"Table or view '{tableName}' does not exist in the database schema.");
        var columns = selectable is SqliteTable table
            ? table.Columns.Select(column => column.Name!).ToArray()
            : ((SqliteView)selectable).Columns.Select(column => column.Name!).ToArray();
        var filter = BuildFilter(clauses, columns, caseSensitive, warning);
        var commandText = $"SELECT * FROM {SqlIdentifier.QuoteQualified(tableName)} WHERE {filter.PredicateSql};";
        var result = QueryExecutor.Execute(
            connection,
            commandText,
            filter.Parameters,
            new QueryOptions
            {
                OutputFormat = outputFormat,
                TableName = tableName,
                Transaction = transaction
            });

        if (result is IEnumerable enumerable &&
            result is not DataTable &&
            result is not DataSet &&
            result is not string)
        {
            return enumerable;
        }

        return result;
    }

    public static object? ExecuteInsert(
        SQLiteDBConfig config,
        string tableName,
        IDictionary rowData,
        SqliteConnection connection,
        SqliteTransaction? transaction = null,
        Action<string>? warning = null)
    {
        var table = config.Schema?.GetTable(tableName)
            ?? throw new ArgumentException($"Table '{tableName}' does not exist in the database schema.");
        var values = GetKnownValues(rowData, table.Columns.Select(column => column.Name!).ToArray(), warning);
        if (values.Count == 0)
        {
            throw new ArgumentException($"No valid row data was provided for table '{tableName}'.");
        }

        var names = values.Keys.Cast<string>().ToArray();
        var parameterNames = names.Select((_, index) => "$value" + index).ToArray();
        var parameters = new OrderedDictionary();
        for (var index = 0; index < names.Length; index++)
        {
            parameters[parameterNames[index]] = values[names[index]];
        }

        var commandText = $"INSERT INTO {SqlIdentifier.QuoteQualified(tableName)} ({string.Join(", ", names.Select(SqlIdentifier.Quote))}) VALUES ({string.Join(", ", parameterNames)} ) RETURNING *;";
        return QueryExecutor.Execute(
            connection,
            commandText,
            parameters,
            new QueryOptions
            {
                OutputFormat = "PSCustomObject",
                TableName = tableName,
                Transaction = transaction
            });
    }

    public static int ExecuteUpdate(
        SQLiteDBConfig config,
        string tableName,
        IDictionary rowData,
        IDictionary? clauses,
        SqliteConnection connection,
        bool caseSensitive,
        string onConflict = "UPDATE",
        SqliteTransaction? transaction = null,
        Action<string>? warning = null)
    {
        var table = config.Schema?.GetTable(tableName)
            ?? throw new ArgumentException($"Table '{tableName}' does not exist in the database schema.");
        var values = GetKnownValues(rowData, table.Columns.Select(column => column.Name!).ToArray(), warning);
        if (values.Count == 0)
        {
            throw new ArgumentException($"No valid row data was provided for table '{tableName}'.");
        }

        if (string.Equals(onConflict, "UPSERT", StringComparison.OrdinalIgnoreCase))
        {
            return ExecuteUpsert(table, tableName, values, clauses, connection, transaction, warning);
        }

        var parameters = new OrderedDictionary();
        var assignments = new List<string>();
        var index = 0;
        foreach (DictionaryEntry value in values)
        {
            var parameterName = "$value" + index++;
            assignments.Add($"{SqlIdentifier.Quote(Convert.ToString(value.Key, CultureInfo.InvariantCulture) ?? string.Empty)} = {parameterName}");
            parameters[parameterName] = value.Value;
        }

        var filter = BuildFilter(clauses, table.Columns.Select(column => column.Name!).ToArray(), caseSensitive, warning);
        foreach (DictionaryEntry parameter in filter.Parameters)
        {
            parameters[parameter.Key] = parameter.Value;
        }

        var commandText = $"UPDATE {SqlIdentifier.QuoteQualified(tableName)} SET {string.Join(", ", assignments)} WHERE {filter.PredicateSql};";
        return Convert.ToInt32(QueryExecutor.Execute(
            connection,
            commandText,
            parameters,
            new QueryOptions
            {
                OutputFormat = "NonQuery",
                Transaction = transaction
            }));
    }

    private static int ExecuteUpsert(
        SqliteTable table,
        string tableName,
        OrderedDictionary values,
        IDictionary? clauses,
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Action<string>? warning)
    {
        var primaryKeyColumns = table.Columns
            .Where(column => column.PrimaryKey)
            .Select(column => column.Name!)
            .Concat(table.Constraints.OfType<SqlitePrimaryKeyTableConstraint>().SelectMany(constraint => constraint.Columns))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (primaryKeyColumns.Length == 0)
        {
            throw new ArgumentException($"Table '{tableName}' does not define a primary key required for UPSERT.");
        }

        var insertValues = new OrderedDictionary();
        foreach (DictionaryEntry value in values)
        {
            insertValues[value.Key] = value.Value;
        }

        if (clauses is not null)
        {
            foreach (DictionaryEntry clause in GetKnownValues(
                         clauses,
                         table.Columns.Select(column => column.Name!).ToArray(),
                         warning))
            {
                if (!insertValues.Contains(clause.Key))
                {
                    insertValues[clause.Key] = clause.Value;
                }
            }
        }

        foreach (var primaryKeyColumn in primaryKeyColumns)
        {
            if (!insertValues.Contains(primaryKeyColumn))
            {
                throw new ArgumentException(
                    $"UPSERT requires a value for primary key column '{primaryKeyColumn}' in RowData or ClauseData.");
            }
        }

        var names = insertValues.Keys.Cast<string>().ToArray();
        var parameterNames = names.Select((_, index) => "$value" + index).ToArray();
        var parameters = new OrderedDictionary();
        for (var index = 0; index < names.Length; index++)
        {
            parameters[parameterNames[index]] = insertValues[names[index]];
        }

        var updates = names
            .Where(name => !primaryKeyColumns.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Select((name, index) => $"{SqlIdentifier.Quote(name)} = excluded.{SqlIdentifier.Quote(name)}")
            .ToArray();
        var conflictAction = updates.Length == 0
            ? "DO NOTHING"
            : "DO UPDATE SET " + string.Join(", ", updates);
        var commandText =
            $"INSERT INTO {SqlIdentifier.QuoteQualified(tableName)} ({string.Join(", ", names.Select(SqlIdentifier.Quote))}) " +
            $"VALUES ({string.Join(", ", parameterNames)}) ON CONFLICT ({string.Join(", ", primaryKeyColumns.Select(SqlIdentifier.Quote))}) {conflictAction};";
        return Convert.ToInt32(QueryExecutor.Execute(
            connection,
            commandText,
            parameters,
            new QueryOptions
            {
                OutputFormat = "NonQuery",
                Transaction = transaction
            }));
    }

    public static int ExecuteDelete(
        SQLiteDBConfig config,
        string tableName,
        IDictionary? clauses,
        SqliteConnection connection,
        bool caseSensitive,
        SqliteTransaction? transaction = null,
        Action<string>? warning = null)
    {
        var table = config.Schema?.GetTable(tableName)
            ?? throw new ArgumentException($"Table '{tableName}' does not exist in the database schema.");
        var filter = BuildFilter(clauses, table.Columns.Select(column => column.Name!).ToArray(), caseSensitive, warning);
        return Convert.ToInt32(QueryExecutor.Execute(
            connection,
            $"DELETE FROM {SqlIdentifier.QuoteQualified(tableName)} WHERE {filter.PredicateSql};",
            filter.Parameters,
            new QueryOptions
            {
                OutputFormat = "NonQuery",
                Transaction = transaction
            }));
    }

    private static OrderedDictionary GetKnownValues(
        IDictionary values,
        IReadOnlyCollection<string> columns,
        Action<string>? warning)
    {
        var result = new OrderedDictionary();
        foreach (DictionaryEntry entry in values)
        {
            var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty;
            if (!TryFindColumn(columns, key, out var column))
            {
                warning?.Invoke($"Column '{key}' is not a valid column.");
                continue;
            }

            result[column!] = entry.Value;
        }

        return result;
    }

    private static bool TryFindColumn(IReadOnlyCollection<string> columns, string key, out string? column)
    {
        column = columns.FirstOrDefault(value => string.Equals(value, key, StringComparison.OrdinalIgnoreCase));
        return column is not null;
    }

    private static bool TryFindRangeColumn(
        IReadOnlyCollection<string> columns,
        string key,
        string suffix,
        out string? column)
    {
        column = null;
        if (!key.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = key.Substring(0, key.Length - suffix.Length);
        return TryFindColumn(columns, candidate, out column);
    }
}

public static class DatabaseInitializer
{
    public static void Initialize(
        SQLiteDBConfig config,
        DBMigrationMode migrationMode = DBMigrationMode.INCREMENTAL,
        bool force = false)
    {
        if (force)
        {
            migrationMode = DBMigrationMode.OVERWRITE;
        }

        var shouldUpdate = false;
        if (!config.DatabaseExists())
        {
            if (migrationMode == DBMigrationMode.CREATE || migrationMode == DBMigrationMode.INCREMENTAL || migrationMode == DBMigrationMode.OVERWRITE)
            {
                config.CreateDatabase(force: false);
                return;
            }
        }
        else
        {
            var comparison = DatabaseVersion.Compare(config);
            shouldUpdate = comparison.direction is "<" or "!=";
        }

        switch (migrationMode)
        {
            case DBMigrationMode.INCREMENTAL:
                if (shouldUpdate)
                {
                    config.UpdateDatabaseSchema();
                }
                break;
            case DBMigrationMode.CREATE:
                break;
            case DBMigrationMode.OVERWRITE:
                if (force || shouldUpdate)
                {
                    config.RemoveDatabase();
                    config.CreateDatabase();
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(migrationMode), migrationMode, "Unsupported migration mode.");
        }
    }
}
