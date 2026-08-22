using System.Data;
using System.Text;
using Ahtola.Core;

namespace Ahtola.Data.Sqlite;

public class SqliteBlob : Stream
{
    private readonly MemoryStream? _stream;
    private IManagedIncrementalBlobAdapter? _managedBlob;
    private readonly SqliteConnection? _connection;
    private readonly string? _databaseName;
    private readonly string? _tableName;
    private readonly string? _columnName;
    private readonly long _rowId;
    private readonly bool _readOnly;
    private long _position;
    private bool _disposed;

    public SqliteBlob(SqliteConnection connection, string tableName, string columnName, long rowid, bool readOnly = false)
        : this(connection, "main", tableName, columnName, rowid, readOnly)
    {
    }

    public SqliteBlob(
        SqliteConnection connection,
        string databaseName,
        string tableName,
        string columnName,
        long rowid,
        bool readOnly = false)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(databaseName);
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(columnName);
        if (connection.State != ConnectionState.Open)
            throw new InvalidOperationException(Properties.Resources.SqlBlobRequiresOpenConnection);
        if (!connection.Capabilities.SupportsIncrementalBlob)
            throw new NotSupportedException("Incremental blob I/O is not supported by this connection.");
        if (connection.RequiresAsyncExecution)
        {
            throw new PlatformNotSupportedException(
                "Synchronous incremental blob opening is not supported by browser-managed databases. "
                + "Use SqliteConnection.OpenBlobAsync.");
        }

        _connection = connection;
        _databaseName = databaseName;
        _tableName = tableName;
        _columnName = columnName;
        _rowId = rowid;
        _readOnly = readOnly;
        if (connection.IsManagedConnection)
        {
            try
            {
                _managedBlob = connection.ManagedConnection.OpenBlob(databaseName, tableName, columnName, rowid, readOnly);
            }
            catch (ManagedBlobException exception)
            {
                throw ToSqliteException(exception);
            }
            catch (EmbeddedSqlException exception)
            {
                throw SqliteCommand.ToSqliteException(exception);
            }

            connection.ManagedBlobOpened(this);
            return;
        }

        _stream = new MemoryStream(GetBlobValue(connection, databaseName, tableName, columnName, rowid), writable: true);
    }

    private SqliteBlob(
        SqliteConnection connection,
        string databaseName,
        string tableName,
        string columnName,
        long rowid,
        bool readOnly,
        IManagedIncrementalBlobAdapter managedBlob)
    {
        _connection = connection;
        _databaseName = databaseName;
        _tableName = tableName;
        _columnName = columnName;
        _rowId = rowid;
        _readOnly = readOnly;
        _managedBlob = managedBlob;
        connection.ManagedBlobOpened(this);
    }

    internal SqliteBlob(byte[] value)
    {
        _stream = new MemoryStream(value.ToArray(), writable: false);
        _readOnly = true;
    }

    public override bool CanRead => !_disposed && (_managedBlob is not null || _stream?.CanRead == true);

    public override bool CanSeek => !_disposed && (_managedBlob is not null || _stream?.CanSeek == true);

    public override bool CanWrite => !_disposed && !_readOnly && (_managedBlob is not null || _stream?.CanWrite == true);

    public override long Length
    {
        get
        {
            if (_managedBlob is not null)
                return ExecuteManaged(static blob => blob.Length);

            return GetStream().Length;
        }
    }

    public override long Position
    {
        get => _managedBlob is not null ? GetManagedPosition() : GetStream().Position;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), value, message: null);

            if (_managedBlob is not null)
            {
                ThrowIfDisposed();
                _position = value;
            }
            else
            {
                GetStream().Position = value;
            }
        }
    }

    public override void Flush()
    {
        if (_managedBlob is not null)
        {
            ThrowIfDisposed();
            ThrowIfBrowserSyncOperation("flushing");
            return;
        }

        Persist();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (_managedBlob is not null)
            return Task.CompletedTask;

        return GetStream().FlushAsync(cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBuffer(buffer, offset, count);
        if (_managedBlob is not null)
        {
            ThrowIfDisposed();
            ThrowIfBrowserSyncOperation("reading");
            var read = ExecuteManaged(blob => blob.Read(GetManagedPosition(), buffer.AsSpan(offset, count)));
            _position += read;
            return read;
        }

        return GetStream().Read(buffer, offset, count);
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (_managedBlob is null)
            return await GetStream().ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

        try
        {
            var read = await _managedBlob
                .ReadAsync(GetManagedPosition(), buffer, cancellationToken)
                .ConfigureAwait(false);
            _position += read;
            return read;
        }
        catch (ManagedBlobException exception)
        {
            throw ToSqliteException(exception);
        }
        catch (EmbeddedSqlException exception)
        {
            throw SqliteCommand.ToSqliteException(exception);
        }
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ValidateBuffer(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        if (_managedBlob is not null)
        {
            var managedPosition = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(GetManagedPosition() + offset),
                SeekOrigin.End => checked(Length + offset),
                _ => throw new ArgumentException(Properties.Resources.InvalidEnumValue(typeof(SeekOrigin), origin), nameof(origin))
            };
            if (managedPosition < 0)
                throw new IOException(Properties.Resources.SeekBeforeBegin);

            _position = managedPosition;
            return managedPosition;
        }

        var stream = GetStream();
        var position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => stream.Position + offset,
            SeekOrigin.End => stream.Length + offset,
            _ => throw new ArgumentException(Properties.Resources.InvalidEnumValue(typeof(SeekOrigin), origin), nameof(origin))
        };
        if (position < 0)
            throw new IOException(Properties.Resources.SeekBeforeBegin);

        stream.Position = position;
        return position;
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException(Properties.Resources.ResizeNotSupported);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ThrowIfDisposed();
        if (_readOnly)
            throw new NotSupportedException(Properties.Resources.WriteNotSupported);

        ValidateBuffer(buffer, offset, count);
        if (_managedBlob is not null)
            ThrowIfBrowserSyncOperation("writing");
        if (count == 0)
            return;

        if (_managedBlob is not null)
        {
            if (_connection?.HasOpenReader == true)
                throw new SqliteException(Properties.Resources.SqliteNativeError(5, "database is locked"), 5);
            if (_connection?.IsReadOnly == true)
                throw new SqliteException(Properties.Resources.SqliteNativeError(8, "attempt to write a readonly database"), 8);
            if (GetManagedPosition() > Length || count > Length - GetManagedPosition())
                throw new NotSupportedException(Properties.Resources.ResizeNotSupported);

            ExecuteManaged(blob =>
            {
                blob.Write(GetManagedPosition(), buffer.AsSpan(offset, count));
                return 0;
            });
            _position += count;
            return;
        }

        var stream = GetStream();
        if (stream.Position + count > stream.Length)
            throw new NotSupportedException(Properties.Resources.ResizeNotSupported);

        stream.Write(buffer, offset, count);
        Persist();
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (_readOnly)
            throw new NotSupportedException(Properties.Resources.WriteNotSupported);
        if (buffer.IsEmpty)
            return;

        if (_managedBlob is not null)
        {
            if (_connection?.HasOpenReader == true)
                throw new SqliteException(Properties.Resources.SqliteNativeError(5, "database is locked"), 5);
            if (_connection?.IsReadOnly == true)
                throw new SqliteException(Properties.Resources.SqliteNativeError(8, "attempt to write a readonly database"), 8);
            if (GetManagedPosition() > Length || buffer.Length > Length - GetManagedPosition())
                throw new NotSupportedException(Properties.Resources.ResizeNotSupported);

            try
            {
                await _managedBlob
                    .WriteAsync(GetManagedPosition(), buffer, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ManagedBlobException exception)
            {
                throw ToSqliteException(exception);
            }
            catch (EmbeddedSqlException exception)
            {
                throw SqliteCommand.ToSqliteException(exception);
            }

            _position += buffer.Length;
            return;
        }

        var stream = GetStream();
        if (stream.Position + buffer.Length > stream.Length)
            throw new NotSupportedException(Properties.Resources.ResizeNotSupported);

        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Persist();
    }

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ValidateBuffer(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            if (_managedBlob is not null)
                ThrowIfBrowserSyncOperation("disposal");
            try
            {
                _managedBlob?.Dispose();
                _stream?.Dispose();
            }
            finally
            {
                _connection?.ManagedBlobClosed(this);
                _disposed = true;
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        Exception? disposalError = null;
        try
        {
            var managedBlob = Interlocked.Exchange(ref _managedBlob, null);
            if (managedBlob is not null)
                await managedBlob.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            disposalError = exception;
        }
        finally
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        if (disposalError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(disposalError).Throw();
    }

    internal void CloseFromConnection() => Dispose();

    internal ValueTask CloseFromConnectionAsync() => DisposeAsync();

    internal static ValueTask<SqliteBlob> OpenAsync(
        SqliteConnection connection,
        string databaseName,
        string tableName,
        string columnName,
        long rowid,
        bool readOnly,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(databaseName);
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(columnName);
        cancellationToken.ThrowIfCancellationRequested();
        if (connection.State != ConnectionState.Open)
            throw new InvalidOperationException(Properties.Resources.SqlBlobRequiresOpenConnection);
        if (!connection.Capabilities.SupportsIncrementalBlob)
            throw new NotSupportedException("Incremental blob I/O is not supported by this connection.");

        if (!connection.IsManagedConnection)
        {
            return ValueTask.FromResult(
                new SqliteBlob(connection, databaseName, tableName, columnName, rowid, readOnly));
        }

        try
        {
            var adapter = connection.ManagedConnection.OpenBlob(
                databaseName,
                tableName,
                columnName,
                rowid,
                readOnly);
            return ValueTask.FromResult(
                new SqliteBlob(
                    connection,
                    databaseName,
                    tableName,
                    columnName,
                    rowid,
                    readOnly,
                    adapter));
        }
        catch (ManagedBlobException exception)
        {
            throw ToSqliteException(exception);
        }
        catch (EmbeddedSqlException exception)
        {
            throw SqliteCommand.ToSqliteException(exception);
        }
    }

    private MemoryStream GetStream()
    {
        ThrowIfDisposed();
        return _stream ?? throw new NotSupportedException("Incremental blob I/O is not yet supported by the Ahtola SQLite-compatible provider.");
    }

    private long GetManagedPosition()
    {
        ThrowIfDisposed();
        return _position;
    }

    private T ExecuteManaged<T>(Func<IManagedIncrementalBlobAdapter, T> operation)
    {
        ThrowIfDisposed();
        try
        {
            return operation(_managedBlob ?? throw new InvalidOperationException("The managed blob adapter is unavailable."));
        }
        catch (ManagedBlobException exception)
        {
            throw ToSqliteException(exception);
        }
        catch (EmbeddedSqlException exception)
        {
            throw SqliteCommand.ToSqliteException(exception);
        }
    }

    private void Persist()
    {
        if (_connection is null
            || _databaseName is null
            || _tableName is null
            || _columnName is null
            || _readOnly)
            return;

        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE " + QualifyTable(_databaseName, _tableName)
            + " SET " + QuoteIdentifier(_columnName) + " = $value WHERE rowid = $rowid;";
        command.Parameters.Add("$value", SqliteType.Blob).Value = GetStream().ToArray();
        command.Parameters.Add("$rowid", SqliteType.Integer).Value = _rowId;
        command.ExecuteNonQuery();
    }

    private static byte[] GetBlobValue(
        SqliteConnection connection,
        string databaseName,
        string tableName,
        string columnName,
        long rowId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + QuoteIdentifier(columnName)
            + " FROM " + QualifyTable(databaseName, tableName) + " WHERE rowid = $rowid;";
        command.Parameters.Add("$rowid", SqliteType.Integer).Value = rowId;
        var value = command.ExecuteScalar();
        return value switch
        {
            byte[] bytes => bytes.ToArray(),
            string text => Encoding.UTF8.GetBytes(text),
            null or DBNull => throw new SqliteException(Properties.Resources.SqliteNativeError(1, "no such rowid: " + rowId), 1),
            _ => Encoding.UTF8.GetBytes(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)
        };
    }

    private static void ValidateBuffer(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, message: null);
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, message: null);
        if (offset > buffer.Length || count > buffer.Length - offset)
            throw new ArgumentException(Properties.Resources.InvalidOffsetAndCount);
    }

    private static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string QualifyTable(string databaseName, string tableName)
        => QuoteIdentifier(databaseName) + "." + QuoteIdentifier(tableName);

    private static SqliteException ToSqliteException(ManagedBlobException exception)
        => new(
            Properties.Resources.SqliteNativeError(exception.ErrorCode, exception.Message),
            exception.ErrorCode);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void ThrowIfBrowserSyncOperation(string operation)
    {
        if (_connection?.RequiresAsyncExecution == true)
        {
            throw new PlatformNotSupportedException(
                $"Synchronous incremental blob {operation} is not supported by browser-managed databases. "
                + "Use the corresponding asynchronous Stream API.");
        }
    }
}
