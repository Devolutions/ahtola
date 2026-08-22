using System.Data;
using System.Data.Common;
using System.Net.Http;
using Ahtola;

namespace Ahtola.Data.Sqlite;

public class SqliteTransaction : DbTransaction
{
    private SqliteConnection? _connection;
    private readonly IsolationLevel _isolationLevel;
    private bool _completed;
    private bool _externalRollback;
    private AhtolaTransaction? _ahtolaTransaction;

    internal SqliteTransaction(SqliteConnection connection, IsolationLevel isolationLevel, bool deferred)
        : this(connection, isolationLevel, deferred, beginTransaction: true)
    {
    }

    private SqliteTransaction(
        SqliteConnection connection,
        IsolationLevel isolationLevel,
        bool deferred,
        bool beginTransaction)
    {
        _connection = connection;
        _isolationLevel = NormalizeIsolationLevel(connection, isolationLevel, deferred);

        if (connection.AhtolaConnection is { } ahtolaConnection)
        {
            if (beginTransaction)
            {
                try
                {
                    _ahtolaTransaction = (AhtolaTransaction)ahtolaConnection.BeginTransaction(_isolationLevel);
                }
                catch (Exception ex) when (ex is AhtolaException or HttpRequestException)
                {
                    throw MapRemoteException(connection, ex);
                }
            }
            return;
        }

        if (_isolationLevel == IsolationLevel.ReadUncommitted)
            connection.ReadUncommitted = true;

        if (beginTransaction)
            Execute(GetBeginSql(_isolationLevel, deferred));
    }

    internal static async ValueTask<SqliteTransaction> CreateAsync(
        SqliteConnection connection,
        IsolationLevel isolationLevel,
        bool deferred,
        CancellationToken cancellationToken)
    {
        var transaction = new SqliteTransaction(
            connection,
            isolationLevel,
            deferred,
            beginTransaction: false);
        try
        {
            if (connection.AhtolaConnection is { } ahtolaConnection)
            {
                transaction._ahtolaTransaction = (AhtolaTransaction)await ahtolaConnection
                    .BeginTransactionAsync(transaction._isolationLevel, cancellationToken)
                    .ConfigureAwait(false);
                return transaction;
            }

            await transaction
                .ExecuteAsync(GetBeginSql(transaction._isolationLevel, deferred), cancellationToken)
                .ConfigureAwait(false);
            return transaction;
        }
        catch (Exception ex) when (ex is AhtolaException or HttpRequestException)
        {
            transaction.Complete();
            throw MapRemoteException(connection, ex);
        }
        catch
        {
            transaction.Complete();
            throw;
        }
    }

    public override IsolationLevel IsolationLevel => _isolationLevel;

    public override bool SupportsSavepoints => _ahtolaTransaction?.SupportsSavepoints ?? true;

    protected override DbConnection? DbConnection => Connection;

    public new virtual SqliteConnection? Connection => _connection;

    internal bool IsCompleted => _completed;

    internal bool WasRolledBackExternally => _externalRollback;

    internal AhtolaTransaction? AhtolaTransaction => _ahtolaTransaction;

    private static Exception MapRemoteException(SqliteConnection connection, Exception exception)
    {
        var mapped = SqliteCommand.ToSqliteException(exception);
        return connection.Mode == AhtolaConnectionMode.RemoteHrana
            ? SqliteRemoteExceptionClassifier.From(exception, mapped)
            : mapped;
    }

    private void RunAhtola(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is AhtolaException or HttpRequestException)
        {
            _connection?.ObserveRemoteInvalidation();
            throw MapRemoteException(_connection!, ex);
        }
    }

    private async Task RunAhtolaAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AhtolaException or HttpRequestException)
        {
            _connection?.ObserveRemoteInvalidation();
            throw MapRemoteException(_connection!, ex);
        }
    }

    public override void Commit()
    {
        ThrowIfSynchronousBrowserOperation("CommitAsync");
        ThrowIfCompleted();
        if (_externalRollback)
            throw new InvalidOperationException(Properties.Resources.TransactionCompleted);

        if (_ahtolaTransaction is not null)
        {
            try
            {
                RunAhtola(_ahtolaTransaction.Commit);
            }
            catch
            {
                if (_ahtolaTransaction?.IsCompleted == true)
                    Complete();
                throw;
            }
        }
        else
            Execute("COMMIT;");
        Complete();
    }

    public override async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfCompleted();
        if (_externalRollback)
            throw new InvalidOperationException(Properties.Resources.TransactionCompleted);

        if (_ahtolaTransaction is not null)
        {
            try
            {
                await RunAhtolaAsync(() => _ahtolaTransaction.CommitAsync(cancellationToken)).ConfigureAwait(false);
            }
            catch
            {
                if (_ahtolaTransaction?.IsCompleted == true)
                    Complete();
                throw;
            }
        }
        else
            await ExecuteAsync("COMMIT;", cancellationToken).ConfigureAwait(false);
        Complete();
    }

    public override void Rollback()
    {
        ThrowIfSynchronousBrowserOperation("RollbackAsync");
        ThrowIfCompleted();
        try
        {
            if (!_externalRollback)
                if (_ahtolaTransaction is not null)
                    RunAhtola(_ahtolaTransaction.Rollback);
                else
                    Execute("ROLLBACK;");
        }
        finally
        {
            Complete();
        }
    }

    public override async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfCompleted();
        try
        {
            if (!_externalRollback)
                if (_ahtolaTransaction is not null)
                    await RunAhtolaAsync(() => _ahtolaTransaction.RollbackAsync(cancellationToken)).ConfigureAwait(false);
                else
                    await ExecuteAsync("ROLLBACK;", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Complete();
        }
    }

    public override void Save(string savepointName)
    {
        ThrowIfSynchronousBrowserOperation("SaveAsync");
        ArgumentNullException.ThrowIfNull(savepointName);
        ThrowIfCompleted();
        if (_ahtolaTransaction is not null)
            RunAhtola(() => _ahtolaTransaction.Save(savepointName));
        else
            Execute("SAVEPOINT " + QuoteIdentifier(savepointName) + ";");
    }

    public override async Task SaveAsync(string savepointName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(savepointName);
        ThrowIfCompleted();
        if (_ahtolaTransaction is not null)
            await RunAhtolaAsync(() => _ahtolaTransaction.SaveAsync(savepointName, cancellationToken)).ConfigureAwait(false);
        else
            await ExecuteAsync(
                    "SAVEPOINT " + QuoteIdentifier(savepointName) + ";",
                    cancellationToken)
                .ConfigureAwait(false);
    }

    public override void Rollback(string savepointName)
    {
        ThrowIfSynchronousBrowserOperation("RollbackAsync");
        ArgumentNullException.ThrowIfNull(savepointName);
        ThrowIfCompleted();
        if (_ahtolaTransaction is not null)
            RunAhtola(() => _ahtolaTransaction.Rollback(savepointName));
        else
            Execute("ROLLBACK TO SAVEPOINT " + QuoteIdentifier(savepointName) + ";");
    }

    public override async Task RollbackAsync(string savepointName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(savepointName);
        ThrowIfCompleted();
        if (_ahtolaTransaction is not null)
            await RunAhtolaAsync(() => _ahtolaTransaction.RollbackAsync(savepointName, cancellationToken)).ConfigureAwait(false);
        else
            await ExecuteAsync(
                    "ROLLBACK TO SAVEPOINT " + QuoteIdentifier(savepointName) + ";",
                    cancellationToken)
                .ConfigureAwait(false);
    }

    public override void Release(string savepointName)
    {
        ThrowIfSynchronousBrowserOperation("ReleaseAsync");
        ArgumentNullException.ThrowIfNull(savepointName);
        ThrowIfCompleted();
        if (_ahtolaTransaction is not null)
            RunAhtola(() => _ahtolaTransaction.Release(savepointName));
        else
            Execute("RELEASE SAVEPOINT " + QuoteIdentifier(savepointName) + ";");
    }

    public override async Task ReleaseAsync(string savepointName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(savepointName);
        ThrowIfCompleted();
        if (_ahtolaTransaction is not null)
            await RunAhtolaAsync(() => _ahtolaTransaction.ReleaseAsync(savepointName, cancellationToken)).ConfigureAwait(false);
        else
            await ExecuteAsync(
                    "RELEASE SAVEPOINT " + QuoteIdentifier(savepointName) + ";",
                    cancellationToken)
                .ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_completed && _connection?.RequiresAsyncExecution == true)
        {
            throw new PlatformNotSupportedException(
                "Synchronous transaction disposal is not supported by the browser database source. "
                + "Use DisposeAsync or RollbackAsync.");
        }

        if (disposing && !_completed && _ahtolaTransaction is not null)
            Complete();
        else if (disposing && !_completed && _connection is { State: ConnectionState.Open })
            Rollback();
        else if (disposing && _connection is not null && ReferenceEquals(_connection.Transaction, this))
            _connection.Transaction = null;

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_connection?.RequiresAsyncExecution != true)
        {
            await base.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (!_completed)
        {
            if (_connection is { State: ConnectionState.Open })
                await RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            else
                Complete();
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    internal void MarkCompletedExternally(bool rolledBack)
    {
        if (rolledBack)
            _externalRollback = true;

        Complete();
    }

    private void Complete()
    {
        var connection = _connection;
        if (connection is null)
        {
            _completed = true;
            return;
        }

        connection.Transaction = null;
        _ahtolaTransaction?.Dispose();
        _ahtolaTransaction = null;
        if (_isolationLevel == IsolationLevel.ReadUncommitted)
            connection.ReadUncommitted = false;

        _completed = true;
        _connection = null;
    }

    private void ThrowIfCompleted()
    {
        if (_completed || _connection is null || _connection.State != ConnectionState.Open)
            throw new InvalidOperationException(Properties.Resources.TransactionCompleted);
    }

    private void ThrowIfSynchronousBrowserOperation(string asyncAlternative)
    {
        if (_connection?.RequiresAsyncExecution == true)
        {
            throw new PlatformNotSupportedException(
                "Synchronous transaction operations are not supported by the browser database source. "
                + $"Use {asyncAlternative}.");
        }
    }

    private static IsolationLevel NormalizeIsolationLevel(SqliteConnection connection, IsolationLevel isolationLevel, bool deferred)
    {
        if (isolationLevel == IsolationLevel.ReadUncommitted && connection.IsManagedSharedMemory)
            throw new NotSupportedException(Properties.Resources.ManagedSharedCacheReadUncommittedNotSupported);

        if ((isolationLevel == IsolationLevel.ReadUncommitted && (!connection.IsSharedCache || !deferred))
            || isolationLevel == IsolationLevel.ReadCommitted
            || isolationLevel == IsolationLevel.RepeatableRead
            || isolationLevel == IsolationLevel.Unspecified)
        {
            return IsolationLevel.Serializable;
        }

        if (isolationLevel == IsolationLevel.Serializable || isolationLevel == IsolationLevel.ReadUncommitted)
            return isolationLevel;

        throw new ArgumentException(Properties.Resources.InvalidIsolationLevel(isolationLevel));
    }

    private static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string GetBeginSql(IsolationLevel isolationLevel, bool deferred)
        => isolationLevel == IsolationLevel.Serializable && !deferred
            ? "BEGIN IMMEDIATE;"
            : "BEGIN;";

    private void Execute(string sql)
    {
        var connection = _connection;
        if (connection is null)
            throw new InvalidOperationException(Properties.Resources.TransactionCompleted);

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = this;
        command.ExecuteNonQuery();
    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        var connection = _connection;
        if (connection is null)
            throw new InvalidOperationException(Properties.Resources.TransactionCompleted);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = this;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

}
