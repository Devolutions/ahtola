using Ahtola.Core.Parsing;

namespace Ahtola.Core;

public interface IManagedIncrementalBlobAdapter : IDisposable, IAsyncDisposable
{
    long Length { get; }

    int Read(long offset, Span<byte> destination);

    ValueTask<int> ReadAsync(
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = Read(offset, destination.Span);
        return ValueTask.FromResult(result);
    }

    void Write(long offset, ReadOnlySpan<byte> source);

    ValueTask WriteAsync(
        long offset,
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(offset, source.Span);
        return ValueTask.CompletedTask;
    }

    ValueTask IAsyncDisposable.DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class ManagedBlobException : Exception
{
    public ManagedBlobException(int errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public int ErrorCode { get; }
}

internal interface IManagedIncrementalBlobReadSource : IDisposable
{
    int Length { get; }

    int Read(int offset, Span<byte> destination);
}

internal sealed class SqlValueIncrementalBlobReadSource : IManagedIncrementalBlobReadSource
{
    private SqlValue _value;
    private bool _disposed;

    public SqlValueIncrementalBlobReadSource(SqlValue value)
    {
        if (value.Kind != SqlValueKind.Blob)
            throw new ArgumentException("Incremental blob sources require a BLOB value.", nameof(value));

        _value = value;
        Length = value.AsBlobSpan().Length;
    }

    public int Length { get; }

    public int Read(int offset, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var source = _value.AsBlobSpan();
        if (offset >= source.Length || destination.IsEmpty)
            return 0;

        var bytesRead = Math.Min(destination.Length, source.Length - offset);
        source.Slice(offset, bytesRead).CopyTo(destination);
        return bytesRead;
    }

    public void Dispose()
    {
        _disposed = true;
        _value = default;
    }
}

internal sealed class ManagedIncrementalBlobAdapter : IManagedIncrementalBlobAdapter
{
    private const int SqliteError = 1;
    private const int SqliteAbort = 4;

    private readonly object _gate = new();
    private readonly ManagedConnectionAdapter _connection;
    private readonly string _databaseName;
    private readonly string _tableName;
    private readonly string _columnName;
    private readonly long _rowId;
    private readonly bool _readOnly;
    private readonly IManagedIncrementalBlobReadSource? _readSource;
    private byte[] _value;
    private SqlValue[] _rowSnapshot;
    private readonly IDisposable _mutationLease;
    private long _mutationGeneration;
    private bool _disposed;

    private ManagedIncrementalBlobAdapter(
        ManagedConnectionAdapter connection,
        string databaseName,
        string tableName,
        string columnName,
        long rowId,
        bool readOnly,
        BlobSnapshot snapshot,
        IDisposable mutationLease,
        long mutationGeneration,
        IManagedIncrementalBlobReadSource? readSource = null)
    {
        _connection = connection;
        _databaseName = databaseName;
        _tableName = tableName;
        _columnName = columnName;
        _rowId = rowId;
        _readOnly = readOnly;
        _readSource = readSource;
        _value = snapshot.Value;
        _rowSnapshot = snapshot.Row;
        _mutationLease = mutationLease;
        _mutationGeneration = mutationGeneration;
    }

    public static IManagedIncrementalBlobAdapter Open(
        ManagedConnectionAdapter connection,
        string databaseName,
        string tableName,
        string columnName,
        long rowId,
        bool readOnly)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(databaseName);
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(columnName);

        if (readOnly
            && connection.TryOpenPageNativeBlobReadSource(
                databaseName,
                tableName,
                columnName,
                rowId,
                out var readSource,
                out var pageMutationLease,
                out var mutationGeneration))
        {
            try
            {
                return new ManagedIncrementalBlobAdapter(
                    connection,
                    databaseName,
                    tableName,
                    columnName,
                    rowId,
                    readOnly: true,
                    new BlobSnapshot([], []),
                    pageMutationLease,
                    mutationGeneration,
                    readSource);
            }
            catch
            {
                pageMutationLease.Dispose();
                readSource.Dispose();
                throw;
            }
        }

        EnsureTable(connection, databaseName, tableName);
        var mutationLease = connection.OpenBlobMutationLease(databaseName, tableName, rowId);
        try
        {
            var generationBeforeRead = connection.GetBlobMutationGeneration(databaseName, tableName, rowId);
            var snapshot = ReadSnapshot(connection, databaseName, tableName, columnName, rowId, missingIsAbort: false);
            var generationAfterRead = connection.GetBlobMutationGeneration(databaseName, tableName, rowId);
            if (generationBeforeRead != generationAfterRead)
                throw Aborted();

            return new ManagedIncrementalBlobAdapter(
                connection,
                databaseName,
                tableName,
                columnName,
                rowId,
                readOnly,
                snapshot,
                mutationLease,
                generationAfterRead);
        }
        catch
        {
            mutationLease.Dispose();
            throw;
        }
    }

    public long Length
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                EnsureCurrent();
                return _readSource?.Length ?? _value.Length;
            }
        }
    }

    public int Read(long offset, Span<byte> destination)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset));

            EnsureCurrent();
            var length = _readSource?.Length ?? _value.Length;
            if (offset >= length)
                return 0;

            if (_readSource is not null)
                return _readSource.Read((int)offset, destination);

            var count = Math.Min(destination.Length, _value.Length - (int)offset);
            _value.AsSpan((int)offset, count).CopyTo(destination);
            return count;
        }
    }

    public void Write(long offset, ReadOnlySpan<byte> source)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_readOnly)
                throw new NotSupportedException("Writing is not supported for a read-only blob.");
            if (offset < 0 || offset > _value.Length || source.Length > _value.Length - offset)
                throw new NotSupportedException("Resizing is not supported.");

            if (source.IsEmpty)
                return;

            EnsureCurrent();
            if (_connection.HasUpdateTrigger(_databaseName, _tableName))
            {
                throw new ManagedBlobException(
                    SqliteError,
                    "cannot write to an incremental blob on a table with UPDATE triggers");
            }

            var updated = _value.ToArray();
            source.CopyTo(updated.AsSpan((int)offset, source.Length));

            using var statement = _connection.Prepare(
                "UPDATE " + QualifyTable(_databaseName, _tableName)
                + " SET " + QuoteIdentifier(_columnName) + " = $value"
                + " WHERE rowid = $rowid AND " + QuoteIdentifier(_columnName) + " = $expected;");
            statement.Bind(statement.GetParameterIndex("$value"), SqlValue.Blob(updated));
            statement.Bind(statement.GetParameterIndex("$rowid"), SqlValue.Integer(_rowId));
            statement.Bind(statement.GetParameterIndex("$expected"), SqlValue.Blob(_value));
            if (statement.Step() != StatementStepResult.Done)
                throw new InvalidOperationException("An incremental blob update unexpectedly returned a row.");
            if (statement.RowsAffected != 1)
                throw Aborted();

            var snapshot = ReadSnapshot(
                _connection,
                _databaseName,
                _tableName,
                _columnName,
                _rowId,
                missingIsAbort: true);
            _value = snapshot.Value;
            _rowSnapshot = snapshot.Row;
            _mutationGeneration = _connection.GetBlobMutationGeneration(_databaseName, _tableName, _rowId);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _value = [];
            try
            {
                _readSource?.Dispose();
            }
            finally
            {
                _mutationLease.Dispose();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void EnsureCurrent()
    {
        var generationBeforeRead = _connection.GetBlobMutationGeneration(_databaseName, _tableName, _rowId);
        if (_readSource is not null)
        {
            if (generationBeforeRead != _mutationGeneration)
                throw Aborted();

            return;
        }

        var snapshot = ReadSnapshot(
            _connection,
            _databaseName,
            _tableName,
            _columnName,
            _rowId,
            missingIsAbort: true);
        var generationAfterRead = _connection.GetBlobMutationGeneration(_databaseName, _tableName, _rowId);
        if (generationBeforeRead != generationAfterRead
            || generationAfterRead != _mutationGeneration
            || !RowsEqual(snapshot.Row, _rowSnapshot))
        {
            throw Aborted();
        }
    }

    private static BlobSnapshot ReadSnapshot(
        IManagedConnectionAdapter connection,
        string databaseName,
        string tableName,
        string columnName,
        long rowId,
        bool missingIsAbort)
    {
        using var statement = connection.Prepare(
            "SELECT * FROM " + QualifyTable(databaseName, tableName)
            + " WHERE rowid = $rowid;");
        statement.Bind(statement.GetParameterIndex("$rowid"), SqlValue.Integer(rowId));
        if (statement.Step() != StatementStepResult.Row)
        {
            if (missingIsAbort)
                throw Aborted();

            throw new ManagedBlobException(SqliteError, $"no such rowid: {rowId}");
        }

        var columnIndex = FindColumnIndex(statement, columnName);
        var value = statement.GetValue(columnIndex);
        if (value.Kind != SqlValueKind.Blob)
            throw missingIsAbort
                ? Aborted()
                : new ManagedBlobException(SqliteError, "cannot open a non-BLOB value as an incremental blob");

        var row = new SqlValue[statement.GetColumnCount()];
        for (var index = 0; index < row.Length; index++)
            row[index] = CloneValue(statement.GetValue(index));

        return new BlobSnapshot(value.AsBlob().ToArray(), row);
    }

    private static void EnsureTable(
        IManagedConnectionAdapter connection,
        string databaseName,
        string tableName)
    {
        using var statement = connection.Prepare(
            "SELECT type, name, sql FROM " + QualifyTable(databaseName, "sqlite_master")
            + " WHERE sql IS NOT NULL;");
        while (statement.Step() == StatementStepResult.Row)
        {
            var name = statement.GetValue(1);
            if (name.Kind != SqlValueKind.Text
                || !string.Equals(name.AsText(), tableName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var type = statement.GetValue(0);
            if (type.Kind != SqlValueKind.Text)
                throw new InvalidOperationException("sqlite_master returned a non-text object type.");

            var objectType = type.AsText();
            if (string.Equals(objectType, "table", StringComparison.OrdinalIgnoreCase))
            {
                var sql = statement.GetValue(2);
                if (sql.Kind != SqlValueKind.Text)
                    throw new InvalidOperationException("sqlite_master returned non-text table SQL.");
                var parsed = SqlParser.Parse(sql.AsText(), SqlParameterMap.Parse(sql.AsText()));
                if (parsed is not CreateTableStatement createTable)
                    throw new InvalidOperationException("sqlite_master returned table SQL that is not CREATE TABLE.");
                if (createTable.WithoutRowid)
                    throw new ManagedBlobException(SqliteError, $"cannot open table without rowid: {tableName}");

                return;
            }

            throw new ManagedBlobException(
                SqliteError,
                $"cannot open an incremental blob on {objectType} {tableName}");
        }

        throw new ManagedBlobException(SqliteError, $"no such table: {tableName}");
    }

    private static ManagedBlobException Aborted()
        => new(SqliteAbort, "query aborted");

    private static int FindColumnIndex(IManagedStatementAdapter statement, string columnName)
    {
        for (var index = 0; index < statement.GetColumnCount(); index++)
        {
            if (string.Equals(statement.GetColumnName(index), columnName, StringComparison.OrdinalIgnoreCase))
                return index;
        }

        throw new ManagedBlobException(SqliteError, $"no such column: {columnName}");
    }

    private static bool RowsEqual(ReadOnlySpan<SqlValue> left, ReadOnlySpan<SqlValue> right)
        => left.SequenceEqual(right);

    private static SqlValue CloneValue(SqlValue value)
    {
        return value.Kind switch
        {
            SqlValueKind.Null => SqlValue.Null,
            SqlValueKind.Integer => SqlValue.Integer(value.AsInteger()),
            SqlValueKind.Real => SqlValue.Real(value.AsReal()),
            SqlValueKind.Text => SqlValue.Text(value.AsText()),
            SqlValueKind.Blob => SqlValue.Blob(value.AsBlob().Span),
            _ => throw new InvalidOperationException($"Unknown SQL value kind {value.Kind}."),
        };
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string QualifyTable(string databaseName, string tableName)
        => QuoteIdentifier(databaseName) + "." + QuoteIdentifier(tableName);

    private sealed record BlobSnapshot(byte[] Value, SqlValue[] Row);
}
