using System.Data;
using System.Data.Common;

namespace Ahtola.Data.Sqlite;

public class SqliteTransaction : DbTransaction
{
    private SqliteConnection? _connection;
    private readonly IsolationLevel _isolationLevel;
    private bool _completed;
    private bool _externalRollback;

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
            await transaction
                .ExecuteAsync(GetBeginSql(transaction._isolationLevel, deferred), cancellationToken)
                .ConfigureAwait(false);
            return transaction;
        }
        catch
        {
            transaction.Complete();
            throw;
        }
    }

    public override IsolationLevel IsolationLevel => _isolationLevel;

    public override bool SupportsSavepoints => true;

    protected override DbConnection? DbConnection => Connection;

    public new virtual SqliteConnection? Connection => _connection;

    internal bool IsCompleted => _completed;

    internal bool WasRolledBackExternally => _externalRollback;

    public override void Commit()
    {
        ThrowIfCompleted();
        if (_externalRollback)
            throw new InvalidOperationException(Properties.Resources.TransactionCompleted);

        Execute("COMMIT;");
        Complete();
    }

    public override async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfCompleted();
        if (_externalRollback)
            throw new InvalidOperationException(Properties.Resources.TransactionCompleted);

        await ExecuteAsync("COMMIT;", cancellationToken).ConfigureAwait(false);
        Complete();
    }

    public override void Rollback()
    {
        ThrowIfCompleted();
        try
        {
            if (!_externalRollback)
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
                await ExecuteAsync("ROLLBACK;", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Complete();
        }
    }

    public override void Save(string savepointName)
    {
        ArgumentNullException.ThrowIfNull(savepointName);
        ThrowIfCompleted();
        Execute("SAVEPOINT " + QuoteIdentifier(savepointName) + ";");
    }

    public override async Task SaveAsync(string savepointName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(savepointName);
        ThrowIfCompleted();
        await ExecuteAsync(
                "SAVEPOINT " + QuoteIdentifier(savepointName) + ";",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public override void Rollback(string savepointName)
    {
        ArgumentNullException.ThrowIfNull(savepointName);
        ThrowIfCompleted();
        Execute("ROLLBACK TO SAVEPOINT " + QuoteIdentifier(savepointName) + ";");
    }

    public override async Task RollbackAsync(string savepointName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(savepointName);
        ThrowIfCompleted();
        await ExecuteAsync(
                "ROLLBACK TO SAVEPOINT " + QuoteIdentifier(savepointName) + ";",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public override void Release(string savepointName)
    {
        ArgumentNullException.ThrowIfNull(savepointName);
        ThrowIfCompleted();
        Execute("RELEASE SAVEPOINT " + QuoteIdentifier(savepointName) + ";");
    }

    public override async Task ReleaseAsync(string savepointName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(savepointName);
        ThrowIfCompleted();
        await ExecuteAsync(
                "RELEASE SAVEPOINT " + QuoteIdentifier(savepointName) + ";",
                cancellationToken)
            .ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_completed && _connection is { State: ConnectionState.Open })
            Rollback();
        else if (disposing && _connection is not null && ReferenceEquals(_connection.Transaction, this))
            _connection.Transaction = null;

        base.Dispose(disposing);
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
