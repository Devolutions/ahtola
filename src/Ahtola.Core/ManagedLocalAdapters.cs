using Ahtola.Core.Storage;

namespace Ahtola.Core;

public enum ManagedResultValueKind
{
    Null,
    Integer,
    Real,
    Text,
    Blob,
}

public readonly struct ManagedResultValue
{
    private readonly SqlValue _value;

    public ManagedResultValue(SqlValue value)
    {
        _value = value;
    }

    public ManagedResultValueKind Kind => _value.Kind switch
    {
        SqlValueKind.Null => ManagedResultValueKind.Null,
        SqlValueKind.Integer => ManagedResultValueKind.Integer,
        SqlValueKind.Real => ManagedResultValueKind.Real,
        SqlValueKind.Text => ManagedResultValueKind.Text,
        SqlValueKind.Blob => ManagedResultValueKind.Blob,
        _ => throw new InvalidOperationException($"Unknown SQL value kind {_value.Kind}."),
    };

    public long AsInteger() => _value.AsInteger();

    public double AsReal() => _value.AsReal();

    public string AsText() => _value.AsText();

    public ReadOnlyMemory<byte> AsBlob() => _value.AsBlob();
}

public readonly record struct ManagedResultColumn(string Name);

public readonly struct ManagedResultRow
{
    private readonly IManagedStatementAdapter _statement;

    internal ManagedResultRow(IManagedStatementAdapter statement)
    {
        _statement = statement;
    }

    public ManagedResultValue GetValue(int ordinal) => _statement.GetResultValue(ordinal);
}

public readonly struct ManagedResultMetadata
{
    private readonly IManagedStatementAdapter _statement;

    internal ManagedResultMetadata(IManagedStatementAdapter statement)
    {
        _statement = statement;
    }

    public int ColumnCount => _statement.GetResultColumnCount();

    public ManagedResultColumn GetColumn(int ordinal) => _statement.GetResultColumn(ordinal);
}

public readonly record struct ManagedParameter(int Index, string? Name);

public readonly struct ManagedParameterMetadata
{
    private readonly IManagedStatementAdapter _statement;

    internal ManagedParameterMetadata(IManagedStatementAdapter statement)
    {
        _statement = statement;
    }

    public int Count => _statement.ParameterCount;

    public ManagedParameter GetParameter(int index) => new(index, _statement.GetParameterName(index));

    public int GetParameterIndex(string name) => _statement.GetParameterIndex(name);
}

public enum ManagedSnapshotFailure
{
    DestinationNotEmpty,
    DestinationBusy,
    UnsupportedSchemaObject,
    RowidNotAccessible,
    ColumnCountMismatch,
    PhysicalFileIdentityUnavailable,
    SourceBusy,
}

public sealed class ManagedSnapshotException : Exception
{
    public ManagedSnapshotException(ManagedSnapshotFailure failure, string? objectName = null)
        : base($"Managed snapshot failed: {failure}.")
    {
        Failure = failure;
        ObjectName = objectName;
    }

    public ManagedSnapshotFailure Failure { get; }

    public string? ObjectName { get; }
}

public interface IManagedDatabaseAdapter : IDisposable
{
    IManagedConnectionAdapter Connect();

    IManagedConnectionAdapter Connection { get; }
}

public interface IManagedConnectionAdapter : IDisposable
{
    IManagedStatementAdapter Prepare(string sql);

    bool HasAttachedDatabases => true;

    /// <summary>
    /// How long contended transaction-lock acquisitions wait before reporting busy,
    /// mirroring <c>sqlite3_busy_timeout</c>. Adapters without a managed transaction
    /// lock ignore the value.
    /// </summary>
    TimeSpan BusyTimeout
    {
        get => TimeSpan.Zero;
        set { }
    }

    void ResetForPooling()
        => throw new NotSupportedException("This managed connection adapter does not support pooling.");

    IManagedIncrementalBlobAdapter OpenBlob(
        string databaseName,
        string tableName,
        string columnName,
        long rowId,
        bool readOnly = false)
        => throw new NotSupportedException("Managed incremental blob I/O is not supported by this connection adapter.");

    void RegisterScalarFunction(string name, int arity, Func<IReadOnlyList<SqlValue>, SqlValue> function);

    int UnregisterScalarFunctions(string name);

    void RegisterAggregateFunction(
        string name,
        int arity,
        SqlValue seed,
        Func<SqlValue, IReadOnlyList<SqlValue>, SqlValue> step,
        Func<SqlValue, SqlValue> finalize);

    int UnregisterAggregateFunctions(string name);

    void RegisterCollation(string name, Func<string, string, int> compare);

    bool UnregisterCollation(string name);

    void CopySnapshotTo(IManagedConnectionAdapter destination)
        => throw new NotSupportedException("Managed snapshot copying is not supported by this connection adapter.");

    void CopySnapshotTo(
        IManagedConnectionAdapter destination,
        string destinationName,
        string sourceName)
        => throw new NotSupportedException("Named managed snapshot copying is not supported by this connection adapter.");

    void ApplySnapshotPragmaHeader(int schemaVersion, int userVersion, int applicationId)
        => throw new NotSupportedException("Managed snapshot PRAGMA metadata is not supported by this connection adapter.");

    /// <summary>
    /// The update, commit, rollback, authorizer, trace and progress callbacks registered on this
    /// connection. Adapters that cannot deliver them throw so a caller never silently loses a hook.
    /// </summary>
    ManagedConnectionHooks Hooks
        => throw new NotSupportedException("Managed SQL hooks are not supported by this connection adapter.");
}

public interface IManagedStatementAdapter : IDisposable
{
    int ParameterCount { get; }

    ManagedParameterMetadata ParameterMetadata => new(this);

    int RowsAffected { get; }

    void Bind(int index, SqlValue value);

    int GetParameterIndex(string name);

    StatementStepResult Step();

    StatementStepResult Step(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = Step();
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    bool HasRows();

    void Reset();

    void ClearBindings();

    SqlValue GetValue(int ordinal);

    string GetColumnName(int ordinal);

    int GetColumnCount();

    ManagedResultValue GetResultValue(int ordinal) => new(GetValue(ordinal));

    ManagedResultColumn GetResultColumn(int ordinal) => new(GetColumnName(ordinal));

    int GetResultColumnCount() => GetColumnCount();

    ManagedResultRow CurrentRow => new(this);

    ManagedResultMetadata ResultMetadata => new(this);

    string? GetParameterName(int index);
}

public sealed class ManagedDatabaseAdapter : IManagedDatabaseAdapter
{
    private readonly object _gate = new();
    private EmbeddedDatabase? _databaseOwner;
    private ManagedConnectionAdapter? _connection;
    private bool _disposed;

    private ManagedDatabaseAdapter(EmbeddedDatabase databaseOwner)
    {
        _databaseOwner = databaseOwner;
    }

    private ManagedDatabaseAdapter(ManagedConnectionAdapter connection)
    {
        _connection = connection;
    }

    private ManagedDatabaseAdapter(
        ManagedConnectionAdapter connection,
        EmbeddedDatabase databaseOwner)
    {
        _connection = connection;
        _databaseOwner = databaseOwner;
    }

    public static ManagedDatabaseAdapter Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return string.Equals(path, ":memory:", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(path)
            ? new ManagedDatabaseAdapter(new EmbeddedDatabase())
            : new ManagedDatabaseAdapter(EmbeddedDatabase.OpenFile(path));
    }

    public static ManagedDatabaseAdapter OpenFile(
        string path,
        IFileSystem fileSystem,
        bool readOnly = false,
        bool foreignReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(fileSystem);
        return new ManagedDatabaseAdapter(EmbeddedDatabase.OpenFile(path, fileSystem, readOnly, foreignReadOnly));
    }

    public static ManagedDatabaseAdapter FromConnection(EmbeddedConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new ManagedDatabaseAdapter(ManagedConnectionAdapter.Wrap(connection));
    }

    public static ManagedDatabaseAdapter FromConnection(
        EmbeddedConnection connection,
        EmbeddedDatabase? owner)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return owner is null
            ? FromConnection(connection)
            : new ManagedDatabaseAdapter(ManagedConnectionAdapter.Wrap(connection), owner);
    }

    public IManagedConnectionAdapter Connect()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_connection is not null)
                return _connection;

            var database = _databaseOwner
                ?? throw new InvalidOperationException("The managed database cannot create another connection.");
            return _connection = new ManagedConnectionAdapter(database.Connect());
        }
    }

    public IManagedConnectionAdapter Connection
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _connection
                    ?? throw new InvalidOperationException("The managed database has not been connected.");
            }
        }
    }

    public void Dispose()
    {
        ManagedConnectionAdapter? connection;
        EmbeddedDatabase? ownedDatabase;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            connection = _connection;
            _connection = null;
            ownedDatabase = _databaseOwner;
            _databaseOwner = null;
        }

        try
        {
            connection?.Dispose();
        }
        finally
        {
            ownedDatabase?.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

public sealed class ManagedConnectionAdapter : IManagedConnectionAdapter
{
    private readonly object _gate = new();
    private EmbeddedConnection? _connection;

    internal ManagedConnectionAdapter(EmbeddedConnection connection)
    {
        _connection = connection;
    }

    public static ManagedConnectionAdapter Wrap(EmbeddedConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new ManagedConnectionAdapter(connection);
    }

    public bool HasAttachedDatabases => GetConnection().HasAttachedDatabases;

    public ManagedConnectionHooks Hooks => GetConnection().Hooks;

    public TimeSpan BusyTimeout
    {
        get => GetConnection().BusyTimeout;
        set => GetConnection().BusyTimeout = value;
    }

    public IManagedStatementAdapter Prepare(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return ManagedStatementAdapter.FromPreparedStatement(this, sql, GetConnection().Prepare(sql));
    }

    public void ResetForPooling()
    {
        GetConnection().ResetForPooling();
    }

    public IManagedIncrementalBlobAdapter OpenBlob(
        string databaseName,
        string tableName,
        string columnName,
        long rowId,
        bool readOnly = false)
    {
        return ManagedIncrementalBlobAdapter.Open(this, databaseName, tableName, columnName, rowId, readOnly);
    }

    public void RegisterScalarFunction(string name, int arity, Func<IReadOnlyList<SqlValue>, SqlValue> function)
    {
        GetConnection().RegisterScalarFunction(name, arity, function);
    }

    public int UnregisterScalarFunctions(string name)
    {
        return GetConnection().UnregisterScalarFunctions(name);
    }

    public void RegisterAggregateFunction(
        string name,
        int arity,
        SqlValue seed,
        Func<SqlValue, IReadOnlyList<SqlValue>, SqlValue> step,
        Func<SqlValue, SqlValue> finalize)
    {
        GetConnection().RegisterAggregateFunction(name, arity, seed, step, finalize);
    }

    public int UnregisterAggregateFunctions(string name)
    {
        return GetConnection().UnregisterAggregateFunctions(name);
    }

    public void RegisterCollation(string name, Func<string, string, int> compare)
    {
        GetConnection().RegisterCollation(name, compare);
    }

    public bool UnregisterCollation(string name)
    {
        return GetConnection().UnregisterCollation(name);
    }

    public void CopySnapshotTo(IManagedConnectionAdapter destination)
        => CopySnapshotTo(destination, "main", "main");

    public void CopySnapshotTo(
        IManagedConnectionAdapter destination,
        string destinationName,
        string sourceName)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(destinationName);
        ArgumentNullException.ThrowIfNull(sourceName);
        if (destination is not ManagedConnectionAdapter managedDestination)
            throw new ArgumentException("Managed snapshots require a managed destination adapter.", nameof(destination));

        var sourceConnection = GetConnection();
        var destinationConnection = managedDestination.GetConnection();
        if (sourceConnection.ReferencesSameDatabase(sourceName, destinationConnection, destinationName))
            throw new EmbeddedSqlException("source and destination must be distinct");
        if (sourceConnection.CannotProveDistinctSnapshotFiles(
                sourceName,
                destinationConnection,
                destinationName))
        {
            throw new ManagedSnapshotException(
                ManagedSnapshotFailure.PhysicalFileIdentityUnavailable);
        }
        if (destinationConnection.HasActiveTransaction)
            throw new ManagedSnapshotException(ManagedSnapshotFailure.DestinationBusy);
        var sourceTransactionActive = sourceConnection.HasActiveTransaction;
        if (sourceTransactionActive
            && !sourceName.Equals("main", StringComparison.OrdinalIgnoreCase))
        {
            throw new ManagedSnapshotException(ManagedSnapshotFailure.SourceBusy);
        }

        ManagedConnectionAdapter? sourceSnapshot = null;
        EmbeddedDatabase? sourceSnapshotOwner = null;
        ManagedConnectionAdapter? destinationSnapshot = null;
        try
        {
            ManagedConnectionAdapter snapshotSource;
            if (sourceTransactionActive)
            {
                snapshotSource = this;
            }
            else
            {
                var snapshot = sourceConnection.OpenSnapshotConnection(sourceName);
                sourceSnapshotOwner = snapshot.Owner;
                snapshotSource = sourceSnapshot = Wrap(snapshot.Connection);
            }

            destinationSnapshot = Wrap(destinationConnection.OpenDatabaseConnection(destinationName));
            ManagedSnapshot.Copy(snapshotSource, destinationSnapshot, sourceTransactionActive);
        }
        finally
        {
            destinationSnapshot?.Dispose();
            sourceSnapshot?.Dispose();
            sourceSnapshotOwner?.Dispose();
        }
    }

    public void ApplySnapshotPragmaHeader(int schemaVersion, int userVersion, int applicationId)
    {
        GetConnection().ApplySnapshotPragmaHeader(schemaVersion, userVersion, applicationId);
    }

    public void Dispose()
    {
        EmbeddedConnection? connection;
        lock (_gate)
        {
            connection = _connection;
            _connection = null;
        }

        connection?.Dispose();
    }

    internal EmbeddedStatement PrepareEmbeddedStatement(string sql)
    {
        return GetConnection().Prepare(sql);
    }

    internal IDisposable OpenBlobMutationLease(string databaseName, string tableName, long rowId)
    {
        return GetConnection().OpenBlobMutationLease(databaseName, tableName, rowId);
    }

    internal long GetBlobMutationGeneration(string databaseName, string tableName, long rowId)
    {
        return GetConnection().GetBlobMutationGeneration(databaseName, tableName, rowId);
    }

    internal bool HasUpdateTrigger(string databaseName, string tableName)
    {
        return GetConnection().HasUpdateTrigger(databaseName, tableName);
    }

    private EmbeddedConnection GetConnection()
    {
        lock (_gate)
        {
            return _connection ?? throw new ObjectDisposedException(nameof(ManagedConnectionAdapter));
        }
    }
}

public sealed class ManagedStatementAdapter : IManagedStatementAdapter
{
    private readonly object _gate = new();
    private readonly ManagedConnectionAdapter _connection;
    private readonly string _sql;
    private EmbeddedStatement? _statement;
    private bool _hasCurrentRow;
    private bool _clearBindingsPending;

    private ManagedStatementAdapter(
        ManagedConnectionAdapter connection,
        string sql,
        EmbeddedStatement statement)
    {
        _connection = connection;
        _sql = sql;
        _statement = statement;
    }

    public static ManagedStatementAdapter FromPreparedStatement(
        ManagedConnectionAdapter connection,
        string sql,
        EmbeddedStatement statement)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(statement);
        return new ManagedStatementAdapter(connection, sql, statement);
    }

    public int ParameterCount => GetStatement().ParameterCount;

    public ManagedParameterMetadata ParameterMetadata => new(this);

    public int RowsAffected => GetStatement().RowsAffected;

    public void Bind(int index, SqlValue value)
    {
        GetStatement().Bind(index, value);
    }

    public int GetParameterIndex(string name)
    {
        return GetStatement().GetParameterIndex(name);
    }

    public StatementStepResult Step()
        => Step(CancellationToken.None);

    public StatementStepResult Step(CancellationToken cancellationToken)
    {
        try
        {
            var result = GetStatement().Step(cancellationToken);
            lock (_gate)
                _hasCurrentRow = result == StatementStepResult.Row;
            return result;
        }
        catch
        {
            lock (_gate)
                _hasCurrentRow = false;
            throw;
        }
    }

    public bool HasRows()
    {
        return GetStatement().HasRows();
    }

    public void Reset()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_clearBindingsPending)
            {
                ReplaceStatementWithoutBindings();
                return;
            }
        }

        GetStatement().Reset();
        lock (_gate)
            _hasCurrentRow = false;
    }

    public void ClearBindings()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_hasCurrentRow)
            {
                // A stepped row remains readable until Reset replaces the statement without its bindings.
                _clearBindingsPending = true;
                return;
            }

            ReplaceStatementWithoutBindings();
            _clearBindingsPending = true;
        }
    }

    public SqlValue GetValue(int ordinal)
    {
        return GetStatement().GetValue(ordinal);
    }

    public ManagedResultValue GetResultValue(int ordinal)
    {
        return new(GetStatement().GetValue(ordinal));
    }

    public string GetColumnName(int ordinal)
    {
        return GetStatement().GetColumnName(ordinal);
    }

    public ManagedResultColumn GetResultColumn(int ordinal)
    {
        return new(GetStatement().GetColumnName(ordinal));
    }

    public int GetColumnCount()
    {
        return GetStatement().GetColumnCount();
    }

    public int GetResultColumnCount()
    {
        return GetStatement().GetColumnCount();
    }

    public string? GetParameterName(int index)
    {
        return GetStatement().GetParameterName(index);
    }

    public void Dispose()
    {
        EmbeddedStatement? statement;
        lock (_gate)
        {
            statement = _statement;
            _statement = null;
            _hasCurrentRow = false;
            _clearBindingsPending = false;
        }

        statement?.Dispose();
    }

    private void ReplaceStatementWithoutBindings()
    {
        var replacement = _connection.PrepareEmbeddedStatement(_sql);
        EmbeddedStatement? previous = null;
        try
        {
            ThrowIfDisposed();
            previous = _statement;
            _statement = replacement;
            _hasCurrentRow = false;
            _clearBindingsPending = false;
        }
        catch
        {
            replacement.Dispose();
            throw;
        }

        previous!.Dispose();
    }

    private EmbeddedStatement GetStatement()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _statement!;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_statement is null, this);
    }
}

internal static class ManagedSnapshot
{
    private static readonly string[] RowidNames = ["rowid", "_rowid_", "oid"];

    public static void Copy(
        IManagedConnectionAdapter source,
        IManagedConnectionAdapter destination,
        bool sourceTransactionActive = false)
    {
        var sourceTransactionStarted = false;
        var destinationTransactionStarted = false;
        var destinationForeignKeysDisabled = false;
        try
        {
            if (!sourceTransactionActive)
            {
                Execute(source, "BEGIN;");
                sourceTransactionStarted = true;
            }

            var schema = ReadSchema(source);
            var sourceHasSqliteSequence = HasSqliteSequence(source);
            var sourceHasSqliteStat1 = HasSqliteStat1(source);
            var pragmaHeader = ReadPragmaHeader(source);
            if (ForeignKeysEnabled(destination))
            {
                Execute(destination, "PRAGMA foreign_keys = OFF;");
                destinationForeignKeysDisabled = !ForeignKeysEnabled(destination);
            }

            try
            {
                Execute(destination, "BEGIN;");
                destinationTransactionStarted = true;
                ClearSchema(destination);
                if (sourceHasSqliteSequence)
                    EnsureSqliteSequence(destination);
                else
                    ClearSqliteSequence(destination);
                var tables = schema.Where(entry => entry.Type == "table").ToArray();
                foreach (var entry in tables.Where(
                             entry => !EmbeddedDatabase.IsAutoIncrementSequenceBackingTable(entry.Name)))
                    Execute(destination, entry.Sql);

                foreach (var table in tables.Where(
                             entry => !EmbeddedDatabase.IsAutoIncrementSequenceBackingTable(entry.Name)))
                    CopyRows(source, destination, table);
                if (sourceHasSqliteSequence)
                    CopySqliteSequence(source, destination);
                foreach (var table in tables.Where(
                             entry => EmbeddedDatabase.IsAutoIncrementSequenceBackingTable(entry.Name)))
                {
                    Execute(destination, "DELETE FROM " + QuoteIdentifier(table.Name) + ";");
                    CopyRows(source, destination, table);
                }
                if (sourceHasSqliteStat1)
                {
                    // sqlite_stat1 is internal and therefore has no replayable CREATE TABLE entry.
                    // Recreate it through ANALYZE, then preserve the source's exact persisted rows.
                    Execute(destination, "ANALYZE;");
                    CopySqliteStat1(source, destination);
                }

                foreach (var entry in schema.Where(entry => entry.Type is "index" or "view" or "trigger"))
                    Execute(destination, entry.Sql);

                destination.ApplySnapshotPragmaHeader(
                    pragmaHeader.SchemaVersion,
                    pragmaHeader.UserVersion,
                    pragmaHeader.ApplicationId);
                Execute(destination, "COMMIT;");
                destinationTransactionStarted = false;
            }
            catch (EmbeddedPostCommitMaintenanceException)
            {
                throw;
            }
            catch
            {
                if (destinationTransactionStarted)
                {
                    Execute(destination, "ROLLBACK;");
                    destinationTransactionStarted = false;
                }

                throw;
            }
        }
        finally
        {
            if (destinationForeignKeysDisabled && !destinationTransactionStarted)
                Execute(destination, "PRAGMA foreign_keys = ON;");
            if (sourceTransactionStarted)
                Execute(source, "ROLLBACK;");
        }
    }

    private static void ClearSchema(IManagedConnectionAdapter destination)
    {
        if (HasSqliteStat1(destination))
            Execute(destination, "DROP TABLE sqlite_stat1;");

        var schema = ReadSchema(destination);
        foreach (var type in new[] { "trigger", "view", "index", "table" })
        {
            foreach (var entry in schema.Where(entry => entry.Type == type))
            {
                if (type == "table" && EmbeddedDatabase.IsAutoIncrementSequenceBackingTable(entry.Name))
                    continue;
                Execute(destination, "DROP " + type.ToUpperInvariant() + " " + QuoteIdentifier(entry.Name) + ";");
            }
        }
    }

    private static List<SchemaEntry> ReadSchema(IManagedConnectionAdapter source)
    {
        using var statement = source.Prepare(
            "SELECT type, name, sql FROM sqlite_master WHERE sql IS NOT NULL;");
        var schema = new List<SchemaEntry>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var type = statement.GetValue(0).AsText();
            var name = statement.GetValue(1).AsText();
            if (name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
                continue;
            if (type is not ("table" or "index" or "view" or "trigger"))
            {
                throw new ManagedSnapshotException(
                    ManagedSnapshotFailure.UnsupportedSchemaObject,
                    type);
            }

            var sql = statement.GetValue(2).AsText();
            schema.Add(new SchemaEntry(type, name, sql, HasWithoutRowidClause(sql)));
        }

        return schema;
    }

    private static bool HasSqliteSequence(IManagedConnectionAdapter connection)
    {
        using var statement = connection.Prepare(
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'sqlite_sequence';");
        if (statement.Step() != StatementStepResult.Row)
            throw new InvalidOperationException("sqlite_master did not return a sqlite_sequence count.");
        return statement.GetValue(0).AsInteger() != 0;
    }

    private static bool HasSqliteStat1(IManagedConnectionAdapter connection)
    {
        using var statement = connection.Prepare(
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'sqlite_stat1';");
        if (statement.Step() != StatementStepResult.Row)
            throw new InvalidOperationException("sqlite_master did not return a sqlite_stat1 count.");
        return statement.GetValue(0).AsInteger() != 0;
    }

    private static void EnsureSqliteSequence(IManagedConnectionAdapter destination)
    {
        if (HasSqliteSequence(destination))
            return;

        Execute(
            destination,
            "CREATE TABLE __ahtola_snapshot_sequence_seed(id INTEGER PRIMARY KEY AUTOINCREMENT);");
        Execute(destination, "DROP TABLE __ahtola_snapshot_sequence_seed;");
    }

    private static void ClearSqliteSequence(IManagedConnectionAdapter destination)
    {
        if (HasSqliteSequence(destination))
            Execute(destination, "DELETE FROM sqlite_sequence;");
    }

    private static void CopySqliteSequence(
        IManagedConnectionAdapter source,
        IManagedConnectionAdapter destination)
    {
        Execute(destination, "DELETE FROM sqlite_sequence;");
        using var select = source.Prepare(
            "SELECT rowid, name, seq FROM sqlite_sequence ORDER BY rowid;");
        while (select.Step() == StatementStepResult.Row)
        {
            using var insert = destination.Prepare(
                "INSERT INTO sqlite_sequence(rowid, name, seq) VALUES ($p0, $p1, $p2);");
            insert.Bind(1, select.GetValue(0));
            insert.Bind(2, select.GetValue(1));
            insert.Bind(3, select.GetValue(2));
            Execute(insert);
        }
    }

    private static void CopySqliteStat1(
        IManagedConnectionAdapter source,
        IManagedConnectionAdapter destination)
    {
        Execute(destination, "DELETE FROM sqlite_stat1;");
        using var select = source.Prepare(
            "SELECT rowid, tbl, idx, stat FROM sqlite_stat1 ORDER BY rowid;");
        while (select.Step() == StatementStepResult.Row)
        {
            using var insert = destination.Prepare(
                "INSERT INTO sqlite_stat1(rowid, tbl, idx, stat) VALUES ($p0, $p1, $p2, $p3);");
            for (var index = 0; index < 4; index++)
                insert.Bind(index + 1, select.GetValue(index));
            Execute(insert);
        }
    }

    private static SnapshotPragmaHeader ReadPragmaHeader(IManagedConnectionAdapter source)
        => new(
            ReadPragmaInteger(source, "schema_version"),
            ReadPragmaInteger(source, "user_version"),
            ReadPragmaInteger(source, "application_id"));

    private static int ReadPragmaInteger(IManagedConnectionAdapter source, string name)
    {
        using var statement = source.Prepare("PRAGMA " + name + ";");
        if (statement.Step() != StatementStepResult.Row)
            throw new InvalidOperationException($"PRAGMA {name} did not return a value.");

        return checked((int)statement.GetValue(0).AsInteger());
    }

    private static void CopyRows(
        IManagedConnectionAdapter source,
        IManagedConnectionAdapter destination,
        SchemaEntry table)
    {
        var columnNames = ReadColumnNames(source, table.Name);
        var selectColumnNames = columnNames.ToArray();
        if (!table.IsWithoutRowid)
        {
            var rowidName = GetRowidName(columnNames);
            if (rowidName is null)
            {
                throw new ManagedSnapshotException(
                    ManagedSnapshotFailure.RowidNotAccessible,
                    table.Name);
            }

            selectColumnNames = [rowidName, .. selectColumnNames];
        }

        using var select = source.Prepare(
            "SELECT " + string.Join(", ", selectColumnNames.Select(QuoteIdentifier))
            + " FROM " + QuoteIdentifier(table.Name) + ";");

        var parameterNames = Enumerable.Range(0, selectColumnNames.Length)
            .Select(index => "$p" + index)
            .ToArray();
        var insertSql = "INSERT INTO " + QuoteIdentifier(table.Name)
                        + " (" + string.Join(", ", selectColumnNames.Select(QuoteIdentifier)) + ") VALUES ("
                        + string.Join(", ", parameterNames) + ");";
        while (select.Step() == StatementStepResult.Row)
        {
            if (select.GetColumnCount() != selectColumnNames.Length)
            {
                throw new ManagedSnapshotException(
                    ManagedSnapshotFailure.ColumnCountMismatch,
                    table.Name);
            }

            using var insert = destination.Prepare(insertSql);
            for (var index = 0; index < parameterNames.Length; index++)
                insert.Bind(index + 1, select.GetValue(index));
            Execute(insert);
        }
    }

    private static List<string> ReadColumnNames(IManagedConnectionAdapter source, string tableName)
    {
        using var statement = source.Prepare("PRAGMA table_info(" + QuoteIdentifier(tableName) + ");");
        var names = new List<string>();
        while (statement.Step() == StatementStepResult.Row)
            names.Add(statement.GetValue(1).AsText());
        return names;
    }

    private static string? GetRowidName(IReadOnlyList<string> columnNames)
    {
        foreach (var rowidName in RowidNames)
        {
            if (!columnNames.Contains(rowidName, StringComparer.OrdinalIgnoreCase))
                return rowidName;
        }

        return null;
    }

    private static bool HasWithoutRowidClause(string sql)
    {
        string? previousWord = null;
        for (var index = 0; index < sql.Length;)
        {
            switch (sql[index])
            {
                case '\'':
                case '"':
                    index = SkipQuoted(sql, index, sql[index]);
                    continue;
                case '[':
                    index = SkipBracketedIdentifier(sql, index);
                    continue;
                case '-' when index + 1 < sql.Length && sql[index + 1] == '-':
                    index = SkipLineComment(sql, index + 2);
                    continue;
                case '/' when index + 1 < sql.Length && sql[index + 1] == '*':
                    index = SkipBlockComment(sql, index + 2);
                    continue;
            }

            if (!char.IsLetter(sql[index]))
            {
                index++;
                continue;
            }

            var wordStart = index++;
            while (index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] == '_'))
                index++;

            var word = sql[wordStart..index];
            if (string.Equals(previousWord, "WITHOUT", StringComparison.OrdinalIgnoreCase)
                && string.Equals(word, "ROWID", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            previousWord = word;
        }

        return false;
    }

    private static int SkipQuoted(string sql, int index, char quote)
    {
        index++;
        while (index < sql.Length)
        {
            if (sql[index++] != quote)
                continue;
            if (index >= sql.Length || sql[index] != quote)
                break;
            index++;
        }

        return index;
    }

    private static int SkipBracketedIdentifier(string sql, int index)
    {
        index++;
        while (index < sql.Length && sql[index++] != ']')
        {
        }

        return index;
    }

    private static int SkipLineComment(string sql, int index)
    {
        while (index < sql.Length && sql[index] is not '\r' and not '\n')
            index++;

        return index;
    }

    private static int SkipBlockComment(string sql, int index)
    {
        while (index + 1 < sql.Length && (sql[index] != '*' || sql[index + 1] != '/'))
            index++;

        return Math.Min(index + 2, sql.Length);
    }

    private static void Execute(IManagedConnectionAdapter connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        Execute(statement);
    }

    private static void Execute(IManagedStatementAdapter statement)
    {
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    private static bool ForeignKeysEnabled(IManagedConnectionAdapter connection)
    {
        using var statement = connection.Prepare("PRAGMA foreign_keys;");
        if (statement.Step() != StatementStepResult.Row)
            throw new InvalidOperationException("PRAGMA foreign_keys did not return a value.");

        return statement.GetValue(0).AsInteger() != 0;
    }

    private static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private sealed record SchemaEntry(string Type, string Name, string Sql, bool IsWithoutRowid);

    private readonly record struct SnapshotPragmaHeader(
        int SchemaVersion,
        int UserVersion,
        int ApplicationId);
}
