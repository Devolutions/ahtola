using System.Data.Common;
using IsolationLevel = System.Data.IsolationLevel;

namespace Ahtola;

public class AhtolaTransaction : DbTransaction
{
    private AhtolaConnection? _connection;
    private readonly IsolationLevel _isolationLevel;
    private readonly bool _supportsSavepoints;
    private IDisposable? _managedReplicaOperation;
    private bool _completed;

    public AhtolaTransaction(AhtolaConnection connection, IsolationLevel isolationLevel)
        : this(connection, isolationLevel, beginTransaction: true)
    {
    }

    private AhtolaTransaction(
        AhtolaConnection connection,
        IsolationLevel isolationLevel,
        bool beginTransaction)
    {
        _connection = connection;
        _isolationLevel = NormalizeIsolationLevel(isolationLevel);
        _supportsSavepoints = connection.Capabilities.SupportsSavepoints;

        if (_isolationLevel == IsolationLevel.ReadUncommitted)
            connection.ReadUncommitted = true;

        try
        {
            if (beginTransaction)
            {
                if (connection.IsRemote)
                    connection.BeginRemoteTransaction(_isolationLevel);
                else
                    connection.ExecuteNonQuery("BEGIN");
            }

            _managedReplicaOperation = connection.EnterManagedReplicaTransaction();
        }
        catch
        {
            _managedReplicaOperation?.Dispose();
            _managedReplicaOperation = null;
            throw;
        }
    }

    internal static async ValueTask<AhtolaTransaction> CreateAsync(
        AhtolaConnection connection,
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken)
    {
        var transaction = new AhtolaTransaction(
            connection,
            isolationLevel,
            beginTransaction: false);
        try
        {
            if (connection.IsRemote)
            {
                await connection
                    .BeginRemoteTransactionAsync(transaction._isolationLevel, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await transaction
                    .ExecuteNonQueryAsync("BEGIN", cancellationToken)
                    .ConfigureAwait(false);
            }

            transaction._managedReplicaOperation = connection.EnterManagedReplicaTransaction();
            return transaction;
        }
        catch
        {
            transaction.CompleteTransaction();
            throw;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!_completed)
        {
            if (_connection is null || _connection.State == System.Data.ConnectionState.Closed)
                CompleteTransaction();
            else
                Rollback();
        }

        base.Dispose(disposing);
    }

    public override IsolationLevel IsolationLevel => _isolationLevel;

    public override bool SupportsSavepoints => _supportsSavepoints;

    internal bool IsCompleted => _completed;

    internal void MarkCompletedExternally()
    {
        if (!_completed)
            CompleteTransaction();
    }

    protected override DbConnection? DbConnection => _connection;

    public override void Commit()
    {
        ThrowIfCompleted();
        var connection = GetConnection();
        if (connection.IsRemote)
        {
            try
            {
                connection.CommitRemoteTransaction();
            }
            catch (AhtolaRemoteSqlException)
            {
                throw;
            }
            catch
            {
                CompleteTransaction();
                throw;
            }

            CompleteTransaction();
            connection.CloseRemoteSessionIfStateless();
            return;
        }
        else
        {
            connection.ExecuteNonQuery("COMMIT;");
            CompleteTransaction();
        }
    }

    public override void Rollback()
    {
        ThrowIfCompleted();
        var connection = GetConnection();
        if (connection.IsRemote)
        {
            try
            {
                connection.RollbackRemoteTransaction();
            }
            finally
            {
                CompleteTransaction();
            }

            connection.CloseRemoteSessionIfStateless();
            return;
        }

        try
        {
            connection.ExecuteNonQuery("ROLLBACK;");
        }
        finally
        {
            CompleteTransaction();
        }
    }

    public override async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfCompleted();
        var connection = GetConnection();
        if (connection.IsRemote)
        {
            try
            {
                await connection.CommitRemoteTransactionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (AhtolaRemoteSqlException)
            {
                throw;
            }
            catch
            {
                CompleteTransaction();
                throw;
            }

            CompleteTransaction();
            connection.CloseRemoteSessionIfStateless();
            return;
        }

        await ExecuteNonQueryAsync("COMMIT;", cancellationToken).ConfigureAwait(false);
        CompleteTransaction();
    }

    public override async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfCompleted();
        var connection = GetConnection();
        if (connection.IsRemote)
        {
            try
            {
                await connection.RollbackRemoteTransactionAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CompleteTransaction();
            }

            connection.CloseRemoteSessionIfStateless();
            return;
        }

        try
        {
            await ExecuteNonQueryAsync("ROLLBACK;", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CompleteTransaction();
        }
    }

    public override void Save(string savepointName)
    {
        ArgumentNullException.ThrowIfNull(savepointName);
        ThrowIfCompleted();
        GetConnection().ExecuteNonQuery("SAVEPOINT " + QuoteIdentifier(savepointName) + ";");
    }

    public override Task SaveAsync(string savepointName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(savepointName);
        ThrowIfCompleted();
        return ExecuteNonQueryAsync(
            "SAVEPOINT " + QuoteIdentifier(savepointName) + ";",
            cancellationToken);
    }

    public override void Rollback(string savepointName)
    {
        ArgumentNullException.ThrowIfNull(savepointName);
        ThrowIfCompleted();
        GetConnection().ExecuteNonQuery("ROLLBACK TO SAVEPOINT " + QuoteIdentifier(savepointName) + ";");
    }

    public override Task RollbackAsync(string savepointName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(savepointName);
        ThrowIfCompleted();
        return ExecuteNonQueryAsync(
            "ROLLBACK TO SAVEPOINT " + QuoteIdentifier(savepointName) + ";",
            cancellationToken);
    }

    public override void Release(string savepointName)
    {
        ArgumentNullException.ThrowIfNull(savepointName);
        ThrowIfCompleted();
        GetConnection().ExecuteNonQuery("RELEASE SAVEPOINT " + QuoteIdentifier(savepointName) + ";");
    }

    public override Task ReleaseAsync(string savepointName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(savepointName);
        ThrowIfCompleted();
        return ExecuteNonQueryAsync(
            "RELEASE SAVEPOINT " + QuoteIdentifier(savepointName) + ";",
            cancellationToken);
    }

    private void CompleteTransaction()
    {
        var connection = _connection;
        if (connection is null)
            return;
        if (_isolationLevel == IsolationLevel.ReadUncommitted)
            connection.ReadUncommitted = false;
        _completed = true;
        _connection = null;
        connection.TransactionCompleted(this);
        Interlocked.Exchange(ref _managedReplicaOperation, null)?.Dispose();
    }

    private void ThrowIfCompleted()
    {
        if (_completed)
            throw new InvalidOperationException("This transaction has already completed.");
    }

    private AhtolaConnection GetConnection()
        => _connection ?? throw new InvalidOperationException("This transaction has already completed.");

    private async Task ExecuteNonQueryAsync(string sql, CancellationToken cancellationToken)
    {
        await using var command = GetConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = this;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static IsolationLevel NormalizeIsolationLevel(IsolationLevel isolationLevel)
    {
        return isolationLevel switch
        {
            IsolationLevel.Unspecified => IsolationLevel.Serializable,
            IsolationLevel.Serializable => IsolationLevel.Serializable,
            IsolationLevel.ReadCommitted => IsolationLevel.Serializable,

            // Serializable is strictly stronger, so upgrading honours the request.
            IsolationLevel.RepeatableRead => IsolationLevel.Serializable,
            IsolationLevel.ReadUncommitted => IsolationLevel.ReadUncommitted,
            _ => throw new NotSupportedException($"Isolation level {isolationLevel} is not supported.")
        };
    }
}
