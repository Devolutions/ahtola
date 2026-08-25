using System.Buffers.Binary;
using System.Text;
using Ahtola.Core.Storage;

namespace Ahtola.Core.Execution;

internal enum VdbeSpillFileKind : byte
{
    SorterRun = 1,
    HashPartition = 2,
    HashBuildOrder = 3,
    HashMatchMap = 4,
}

internal sealed class VdbeTemporaryFile : IDisposable
{
    private readonly IFileSystem _fileSystem;
    private readonly VdbeExecutionMetrics _metrics;
    private bool _disposed;

    private VdbeTemporaryFile(
        IFileSystem fileSystem,
        VdbeExecutionMetrics metrics,
        string path,
        IFile file)
    {
        _fileSystem = fileSystem;
        _metrics = metrics;
        Path = path;
        File = file;
        metrics.SpillFileOpened();
    }

    public string Path { get; }

    public IFile File { get; }

    public static VdbeTemporaryFile Create(VdbeExecutionOptions options, string purpose)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var path = System.IO.Path.Combine(
                options.TemporaryDirectory,
                $"ahtola-{purpose}-{Guid.NewGuid():N}.spill");
            try
            {
                var fileSystem = options.TemporaryFileSystem;
                var file = fileSystem is ITemporaryFileSystem temporaryFileSystem
                    ? temporaryFileSystem.OpenTemporaryFile(path)
                    : fileSystem.OpenFile(path, FileOpenMode.CreateNew);
                return new VdbeTemporaryFile(fileSystem, options.Metrics, path, file);
            }
            catch (IOException) when (options.TemporaryFileSystem.FileExists(path))
            {
            }
        }

        throw new IOException($"Unable to allocate a unique temporary {purpose} file.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Exception? failure = null;
        try
        {
            File.Dispose();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        var deleted = false;
        // Make one best-effort retry so a surfaced transient cleanup error does not also
        // strand its artifact. The first failure is still reported to the statement.
        for (var attempt = 0; attempt < 2 && !deleted; attempt++)
        {
            try
            {
                _fileSystem.DeleteFile(Path);
                deleted = true;
            }
            catch (Exception exception)
            {
                failure = failure is null ? exception : new AggregateException(failure, exception);
            }
        }

        if (deleted)
        {
            _metrics.SpillFileClosed();
            _disposed = true;
        }

        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

internal static class VdbeSpillRecordCodec
{
    private static ReadOnlySpan<byte> Magic => "AHTSPILL"u8;
    private const byte FormatVersion = 1;
    public const int FileHeaderSize = 10;
    private const int RecordLengthSize = 5;

    public static long InitializeFile(
        IFile file,
        VdbeSpillFileKind kind,
        VdbeExecutionMetrics metrics)
    {
        Span<byte> header = stackalloc byte[FileHeaderSize];
        Magic.CopyTo(header);
        header[8] = FormatVersion;
        header[9] = (byte)kind;
        var position = 0L;
        Write(file, ref position, header, metrics);
        return position;
    }

    public static void ValidateFile(
        IFile file,
        VdbeSpillFileKind expectedKind,
        VdbeExecutionMetrics metrics)
    {
        Span<byte> header = stackalloc byte[FileHeaderSize];
        var position = 0L;
        ReadExact(file, ref position, header, metrics);
        if (!header[..8].SequenceEqual(Magic))
            throw new InvalidDataException("Execution spill file has an invalid magic value.");
        if (header[8] != FormatVersion)
            throw new InvalidDataException($"Unsupported execution spill format version {header[8]}.");
        if (header[9] != (byte)expectedKind)
            throw new InvalidDataException($"Execution spill operator kind {header[9]} does not match {expectedKind}.");
    }

    public static long BeginRecord(ref long position)
    {
        var start = position;
        position = checked(position + RecordLengthSize);
        return start;
    }

    public static void CompleteRecord(
        IFile file,
        long recordStart,
        long recordEnd,
        VdbeExecutionMetrics metrics)
    {
        var payloadLength = checked(recordEnd - recordStart - RecordLengthSize);
        if (payloadLength < 0 || payloadLength > uint.MaxValue)
            throw new InvalidDataException("Execution spill record length is invalid.");

        Span<byte> encoded = stackalloc byte[RecordLengthSize];
        var remaining = (uint)payloadLength;
        for (var index = 0; index < RecordLengthSize; index++)
        {
            encoded[index] = (byte)(remaining & 0x7F);
            remaining >>= 7;
            if (index < RecordLengthSize - 1)
                encoded[index] |= 0x80;
        }

        var headerPosition = recordStart;
        Write(file, ref headerPosition, encoded, metrics);
    }

    public static long ReadRecordEnd(
        IFile file,
        ref long position,
        VdbeExecutionMetrics metrics)
    {
        Span<byte> encoded = stackalloc byte[RecordLengthSize];
        ReadExact(file, ref position, encoded, metrics);
        uint length = 0;
        for (var index = 0; index < RecordLengthSize; index++)
        {
            var current = encoded[index];
            var continuation = (current & 0x80) != 0;
            if (continuation != (index < RecordLengthSize - 1))
                throw new InvalidDataException("Execution spill record has a malformed length varint.");
            if (index == RecordLengthSize - 1 && (current & 0x70) != 0)
                throw new InvalidDataException("Execution spill record length exceeds the supported range.");
            length |= (uint)(current & 0x7F) << (index * 7);
        }

        var end = checked(position + length);
        if (end > file.Length)
            throw new EndOfStreamException("Execution spill stream ended inside a record.");
        return end;
    }

    public static void RequireRecordEnd(long position, long recordEnd)
    {
        if (position != recordEnd)
            throw new InvalidDataException("Execution spill record has unexpected trailing or missing data.");
    }

    public static void WriteValues(
        IFile file,
        ref long position,
        IReadOnlyList<SqlValue> values,
        VdbeExecutionMetrics metrics)
    {
        foreach (var value in values)
            WriteValue(file, ref position, value, metrics);
    }

    public static long EstimateEncodedValuesLength(IReadOnlyList<SqlValue> values)
    {
        var total = 0L;
        foreach (var value in values)
        {
            var valueLength = value.Kind switch
            {
                SqlValueKind.Null => 1L,
                SqlValueKind.Integer or SqlValueKind.Real => 1L + sizeof(long),
                SqlValueKind.Text => 1L + sizeof(int) + Encoding.UTF8.GetByteCount(value.AsText()),
                SqlValueKind.Blob => 1L + sizeof(int) + value.AsBlobSpan().Length,
                _ => throw new InvalidOperationException($"Unknown SQL value kind {value.Kind}."),
            };
            total = checked(total + valueLength);
        }
        return total;
    }

    public static long EstimateEncodedStringLength(string value) =>
        checked((long)sizeof(int) + Encoding.UTF8.GetByteCount(value));

    public static SqlValue[] ReadValues(
        IFile file,
        ref long position,
        int count,
        long recordEnd,
        VdbeExecutionMetrics metrics,
        CancellationToken cancellationToken)
    {
        var values = new SqlValue[count];
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            values[index] = ReadValue(file, ref position, recordEnd, metrics);
        }
        return values;
    }

    public static void WriteString(
        IFile file,
        ref long position,
        string value,
        VdbeExecutionMetrics metrics)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteInt32(file, ref position, bytes.Length, metrics);
        Write(file, ref position, bytes, metrics);
    }

    public static string ReadString(
        IFile file,
        ref long position,
        long recordEnd,
        VdbeExecutionMetrics metrics)
    {
        var length = ReadLength(file, ref position, recordEnd, metrics, "text");
        var bytes = new byte[length];
        ReadExact(file, ref position, bytes, metrics);
        return Encoding.UTF8.GetString(bytes);
    }

    public static void WriteInt32(
        IFile file,
        ref long position,
        int value,
        VdbeExecutionMetrics metrics)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        Write(file, ref position, bytes, metrics);
    }

    public static int ReadInt32(IFile file, ref long position, VdbeExecutionMetrics metrics)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        ReadExact(file, ref position, bytes, metrics);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    public static void WriteInt64(
        IFile file,
        ref long position,
        long value,
        VdbeExecutionMetrics metrics)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        Write(file, ref position, bytes, metrics);
    }

    public static long ReadInt64(IFile file, ref long position, VdbeExecutionMetrics metrics)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        ReadExact(file, ref position, bytes, metrics);
        return BinaryPrimitives.ReadInt64LittleEndian(bytes);
    }

    public static void WriteByte(
        IFile file,
        ref long position,
        byte value,
        VdbeExecutionMetrics metrics)
    {
        Span<byte> bytes = stackalloc byte[1] { value };
        Write(file, ref position, bytes, metrics);
    }

    public static byte ReadByte(IFile file, ref long position, VdbeExecutionMetrics metrics)
    {
        Span<byte> bytes = stackalloc byte[1];
        ReadExact(file, ref position, bytes, metrics);
        return bytes[0];
    }

    public static bool ReadBoolean(
        IFile file,
        ref long position,
        VdbeExecutionMetrics metrics,
        string kind) =>
        ReadByte(file, ref position, metrics) switch
        {
            0 => false,
            1 => true,
            var value => throw new InvalidDataException(
                $"Unknown execution spill {kind} marker {value}."),
        };

    private static void WriteValue(
        IFile file,
        ref long position,
        SqlValue value,
        VdbeExecutionMetrics metrics)
    {
        switch (value.Kind)
        {
            case SqlValueKind.Null:
                WriteByte(file, ref position, 0x00, metrics);
                break;
            case SqlValueKind.Integer:
                WriteByte(file, ref position, 0x01, metrics);
                WriteInt64(file, ref position, value.AsInteger(), metrics);
                break;
            case SqlValueKind.Real:
                WriteByte(file, ref position, 0x02, metrics);
                Span<byte> real = stackalloc byte[sizeof(double)];
                BinaryPrimitives.WriteDoubleLittleEndian(real, value.AsReal());
                Write(file, ref position, real, metrics);
                break;
            case SqlValueKind.Text:
                WriteByte(file, ref position, value.IsJson ? (byte)0x83 : (byte)0x03, metrics);
                WriteString(file, ref position, value.AsText(), metrics);
                break;
            case SqlValueKind.Blob:
                WriteByte(file, ref position, 0x04, metrics);
                var blob = value.AsBlobSpan();
                WriteInt32(file, ref position, blob.Length, metrics);
                Write(file, ref position, blob, metrics);
                break;
            default:
                throw new InvalidOperationException($"Unknown SQL value kind {value.Kind}.");
        }
    }

    private static SqlValue ReadValue(
        IFile file,
        ref long position,
        long recordEnd,
        VdbeExecutionMetrics metrics)
    {
        RequireRecordBytes(position, recordEnd, 1, "value tag");
        var kindByte = ReadByte(file, ref position, metrics);
        return kindByte switch
        {
            0x00 => SqlValue.Null,
            0x01 => ReadInteger(file, ref position, recordEnd, metrics),
            0x02 => ReadReal(file, ref position, recordEnd, metrics),
            0x03 => ReadText(file, ref position, recordEnd, metrics, isJson: false),
            0x04 => ReadBlob(file, ref position, recordEnd, metrics),
            0x83 => ReadText(file, ref position, recordEnd, metrics, isJson: true),
            _ => throw new InvalidDataException($"Unknown spilled value tag 0x{kindByte:X2}."),
        };
    }

    private static SqlValue ReadInteger(
        IFile file,
        ref long position,
        long recordEnd,
        VdbeExecutionMetrics metrics)
    {
        RequireRecordBytes(position, recordEnd, sizeof(long), "integer payload");
        return SqlValue.Integer(ReadInt64(file, ref position, metrics));
    }

    private static SqlValue ReadReal(
        IFile file,
        ref long position,
        long recordEnd,
        VdbeExecutionMetrics metrics)
    {
        RequireRecordBytes(position, recordEnd, sizeof(double), "real payload");
        Span<byte> bytes = stackalloc byte[sizeof(double)];
        ReadExact(file, ref position, bytes, metrics);
        return SqlValue.Real(BinaryPrimitives.ReadDoubleLittleEndian(bytes));
    }

    private static SqlValue ReadText(
        IFile file,
        ref long position,
        long recordEnd,
        VdbeExecutionMetrics metrics,
        bool isJson)
    {
        var text = ReadString(file, ref position, recordEnd, metrics);
        return isJson ? SqlValue.JsonText(text) : SqlValue.Text(text);
    }

    private static SqlValue ReadBlob(
        IFile file,
        ref long position,
        long recordEnd,
        VdbeExecutionMetrics metrics)
    {
        var length = ReadLength(file, ref position, recordEnd, metrics, "blob");
        var bytes = new byte[length];
        ReadExact(file, ref position, bytes, metrics);
        return SqlValue.BlobOwned(bytes);
    }

    private static int ReadLength(
        IFile file,
        ref long position,
        long recordEnd,
        VdbeExecutionMetrics metrics,
        string kind)
    {
        RequireRecordBytes(position, recordEnd, sizeof(int), $"{kind} length");
        var length = ReadInt32(file, ref position, metrics);
        if (length < 0)
            throw new InvalidDataException($"Execution spill {kind} length is negative.");
        if (length > recordEnd - position)
        {
            throw new InvalidDataException(
                $"Execution spill {kind} payload crosses its enclosing record boundary.");
        }
        return length;
    }

    private static void RequireRecordBytes(
        long position,
        long recordEnd,
        int byteCount,
        string kind)
    {
        if (position < 0 || recordEnd < position || byteCount > recordEnd - position)
        {
            throw new InvalidDataException(
                $"Execution spill {kind} crosses its enclosing record boundary.");
        }
    }

    private static void Write(
        IFile file,
        ref long position,
        ReadOnlySpan<byte> source,
        VdbeExecutionMetrics metrics)
    {
        file.Write(position, source);
        position = checked(position + source.Length);
        metrics.AddSpillBytesWritten(source.Length);
    }

    private static void ReadExact(
        IFile file,
        ref long position,
        Span<byte> destination,
        VdbeExecutionMetrics metrics)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = file.Read(checked(position + total), destination[total..]);
            if (read <= 0)
                throw new EndOfStreamException("Execution spill stream ended mid-record.");
            total += read;
            metrics.AddSpillBytesRead(read);
        }
        position = checked(position + total);
    }
}
