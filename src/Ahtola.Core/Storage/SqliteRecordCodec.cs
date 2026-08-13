using System.Buffers.Binary;
using System.Text;

namespace Ahtola.Core.Storage;

public static class SqliteRecordCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16LittleEndian = new(false, false, true);
    private static readonly UnicodeEncoding StrictUtf16BigEndian = new(true, false, true);

    public static byte[] Encode(IReadOnlyList<SqlValue> values, SqliteTextEncoding textEncoding = SqliteTextEncoding.Utf8)
    {
        ArgumentNullException.ThrowIfNull(values);
        var encoding = GetTextEncoding(textEncoding);

        var serialTypes = new ulong[values.Count];
        var body = new List<byte>();
        var serialTypeBytes = 0;

        for (var index = 0; index < values.Count; index++)
        {
            serialTypes[index] = WriteValueBody(values[index], body, encoding);
            serialTypeBytes += SqliteVarint.GetLength(serialTypes[index]);
        }

        var headerSize = serialTypeBytes + 1;
        while (true)
        {
            var calculated = serialTypeBytes + SqliteVarint.GetLength((ulong)headerSize);
            if (calculated == headerSize)
                break;

            headerSize = calculated;
        }

        var record = new List<byte>(headerSize + body.Count);
        WriteVarint(record, (ulong)headerSize);
        foreach (var serialType in serialTypes)
            WriteVarint(record, serialType);

        if (record.Count != headerSize)
            throw new InvalidOperationException("SQLite record header size calculation is inconsistent.");

        record.AddRange(body);
        return record.ToArray();
    }

    public static SqlValue[] Decode(ReadOnlySpan<byte> record, SqliteTextEncoding textEncoding = SqliteTextEncoding.Utf8)
    {
        var encoding = GetTextEncoding(textEncoding);
        if (!SqliteVarint.TryRead(record, out var headerSizeValue, out var headerSizeLength))
            throw new InvalidDataException("SQLite record header size is invalid.");
        if (headerSizeValue > int.MaxValue)
            throw new InvalidDataException("SQLite record header size exceeds supported managed buffer length.");

        var headerSize = (int)headerSizeValue;
        if (headerSize < headerSizeLength || headerSize > record.Length)
            throw new InvalidDataException("SQLite record header extends outside its payload.");

        var serialTypes = new List<ulong>();
        var headerPosition = headerSizeLength;
        while (headerPosition < headerSize)
        {
            if (!SqliteVarint.TryRead(record[headerPosition..headerSize], out var serialType, out var serialTypeLength))
                throw new InvalidDataException("SQLite record serial type is invalid.");

            serialTypes.Add(serialType);
            headerPosition += serialTypeLength;
        }

        var values = new SqlValue[serialTypes.Count];
        var bodyPosition = headerSize;
        for (var index = 0; index < serialTypes.Count; index++)
            values[index] = ReadValue(record, ref bodyPosition, serialTypes[index], encoding);

        if (bodyPosition != record.Length)
            throw new InvalidDataException("SQLite record contains trailing bytes.");

        return values;
    }

    private static ulong WriteValueBody(SqlValue value, List<byte> body, Encoding textEncoding)
    {
        switch (value.Kind)
        {
            case SqlValueKind.Null:
                return 0;
            case SqlValueKind.Integer:
                return WriteInteger(value.AsInteger(), body);
            case SqlValueKind.Real:
                WriteInt64(body, BitConverter.DoubleToInt64Bits(value.AsReal()));
                return 7;
            case SqlValueKind.Text:
                {
                    var bytes = textEncoding.GetBytes(value.AsText());
                    body.AddRange(bytes);
                    return checked((ulong)bytes.Length * 2 + 13);
                }
            case SqlValueKind.Blob:
                {
                                var bytes = value.AsBlobSpan();
                                body.AddRange(bytes);
                    return checked((ulong)bytes.Length * 2 + 12);
                }
            default:
                throw new InvalidOperationException($"Unknown SQL value kind {value.Kind}.");
        }
    }

    private static ulong WriteInteger(long value, List<byte> body)
    {
        if (value == 0)
            return 8;
        if (value == 1)
            return 9;
        if (value is >= sbyte.MinValue and <= sbyte.MaxValue)
        {
            body.Add(unchecked((byte)value));
            return 1;
        }
        if (value is >= short.MinValue and <= short.MaxValue)
        {
            WriteIntegerBytes(body, value, 2);
            return 2;
        }
        if (value is >= -8_388_608 and <= 8_388_607)
        {
            WriteIntegerBytes(body, value, 3);
            return 3;
        }
        if (value is >= int.MinValue and <= int.MaxValue)
        {
            WriteIntegerBytes(body, value, 4);
            return 4;
        }
        if (value is >= -140_737_488_355_328 and <= 140_737_488_355_327)
        {
            WriteIntegerBytes(body, value, 6);
            return 5;
        }

        WriteIntegerBytes(body, value, 8);
        return 6;
    }

    private static SqlValue ReadValue(ReadOnlySpan<byte> record, ref int bodyPosition, ulong serialType, Encoding textEncoding)
    {
        switch (serialType)
        {
            case 0:
                return SqlValue.Null;
            case 1:
                return SqlValue.Integer(ReadSignedInteger(record, ref bodyPosition, 1));
            case 2:
                return SqlValue.Integer(ReadSignedInteger(record, ref bodyPosition, 2));
            case 3:
                return SqlValue.Integer(ReadSignedInteger(record, ref bodyPosition, 3));
            case 4:
                return SqlValue.Integer(ReadSignedInteger(record, ref bodyPosition, 4));
            case 5:
                return SqlValue.Integer(ReadSignedInteger(record, ref bodyPosition, 6));
            case 6:
                return SqlValue.Integer(ReadSignedInteger(record, ref bodyPosition, 8));
            case 7:
                return SqlValue.Real(BitConverter.Int64BitsToDouble(ReadSignedInteger(record, ref bodyPosition, 8)));
            case 8:
                return SqlValue.Integer(0);
            case 9:
                return SqlValue.Integer(1);
            case 10 or 11:
                throw new InvalidDataException($"SQLite record uses reserved serial type {serialType}.");
            default:
                return ReadTextOrBlob(record, ref bodyPosition, serialType, textEncoding);
        }
    }

    private static SqlValue ReadTextOrBlob(ReadOnlySpan<byte> record, ref int bodyPosition, ulong serialType, Encoding textEncoding)
    {
        var length = serialType % 2 == 0
            ? (serialType - 12) / 2
            : (serialType - 13) / 2;
        if (length > int.MaxValue)
            throw new InvalidDataException("SQLite record value exceeds supported managed buffer length.");
        if (bodyPosition > record.Length - (int)length)
            throw new InvalidDataException("SQLite record value extends outside its payload.");

        var value = record.Slice(bodyPosition, (int)length);
        bodyPosition += (int)length;

        if (serialType % 2 == 0)
            return SqlValue.Blob(value);

        try
        {
            return SqlValue.Text(textEncoding.GetString(value));
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("SQLite record contains invalid text.", exception);
        }
    }

    private static long ReadSignedInteger(ReadOnlySpan<byte> record, ref int bodyPosition, int byteCount)
    {
        if (bodyPosition > record.Length - byteCount)
            throw new InvalidDataException("SQLite record integer extends outside its payload.");

        long value = 0;
        for (var index = 0; index < byteCount; index++)
            value = (value << 8) | record[bodyPosition + index];

        bodyPosition += byteCount;
        var shift = (sizeof(long) - byteCount) * 8;
        return (value << shift) >> shift;
    }

    private static void WriteIntegerBytes(List<byte> body, long value, int byteCount)
    {
        for (var index = byteCount - 1; index >= 0; index--)
            body.Add((byte)(value >> (index * 8)));
    }

    private static void WriteInt64(List<byte> body, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            body.AddRange(bytes);
    }

    private static void WriteVarint(List<byte> destination, ulong value)
    {
        Span<byte> bytes = stackalloc byte[SqliteVarint.MaximumLength];
        var length = SqliteVarint.Write(value, bytes);
        for (var index = 0; index < length; index++)
            destination.Add(bytes[index]);
    }

    private static Encoding GetTextEncoding(SqliteTextEncoding textEncoding)
    {
        return textEncoding switch
        {
            SqliteTextEncoding.Unset or SqliteTextEncoding.Utf8 => StrictUtf8,
            SqliteTextEncoding.Utf16LittleEndian => StrictUtf16LittleEndian,
            SqliteTextEncoding.Utf16BigEndian => StrictUtf16BigEndian,
            _ => throw new ArgumentOutOfRangeException(nameof(textEncoding), textEncoding, "Unsupported SQLite text encoding."),
        };
    }
}
