using System.Buffers.Binary;
using System.Text;

namespace Ahtola.Core.Storage;

public static class SqliteRecordCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16LittleEndian = new(false, false, true);
    private static readonly UnicodeEncoding StrictUtf16BigEndian = new(true, false, true);

    /// <summary>Encodes <paramref name="values"/> as one SQLite record payload.</summary>
    /// <remarks>
    /// Sizes are computed first so the payload is written once into an
    /// exactly-sized array. Building the body through growing <c>List&lt;byte&gt;</c>
    /// buffers produced several times the record's own size in garbage, which
    /// dominated allocations during a full catalog rebuild.
    /// </remarks>
    public static byte[] Encode(IReadOnlyList<SqlValue> values, SqliteTextEncoding textEncoding = SqliteTextEncoding.Utf8)
    {
        ArgumentNullException.ThrowIfNull(values);
        var encoding = GetTextEncoding(textEncoding);

        var serialTypes = new ulong[values.Count];
        var serialTypeBytes = 0;
        var bodyLength = 0;

        for (var index = 0; index < values.Count; index++)
        {
            var serialType = GetSerialType(values[index], encoding);
            serialTypes[index] = serialType;
            serialTypeBytes += SqliteVarint.GetLength(serialType);
            bodyLength = checked(bodyLength + GetBodyLength(serialType));
        }

        var headerSize = serialTypeBytes + 1;
        while (true)
        {
            var calculated = serialTypeBytes + SqliteVarint.GetLength((ulong)headerSize);
            if (calculated == headerSize)
                break;

            headerSize = calculated;
        }

        var record = new byte[checked(headerSize + bodyLength)];
        var position = SqliteVarint.Write((ulong)headerSize, record);
        foreach (var serialType in serialTypes)
            position += SqliteVarint.Write(serialType, record.AsSpan(position));

        if (position != headerSize)
            throw new InvalidOperationException("SQLite record header size calculation is inconsistent.");

        for (var index = 0; index < values.Count; index++)
            position += WriteValueBody(values[index], serialTypes[index], record.AsSpan(position), encoding);

        if (position != record.Length)
            throw new InvalidOperationException("SQLite record body size calculation is inconsistent.");

        return record;
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

    /// <summary>The SQLite serial type that will represent <paramref name="value"/>.</summary>
    private static ulong GetSerialType(SqlValue value, Encoding textEncoding)
        => value.Kind switch
        {
            SqlValueKind.Null => 0,
            SqlValueKind.Integer => GetIntegerSerialType(value.AsInteger()),
            SqlValueKind.Real => 7,
            SqlValueKind.Text => checked((ulong)textEncoding.GetByteCount(value.AsText()) * 2 + 13),
            SqlValueKind.Blob => checked((ulong)value.AsBlobSpan().Length * 2 + 12),
            _ => throw new InvalidOperationException($"Unknown SQL value kind {value.Kind}."),
        };

    /// <summary>The body byte count implied by <paramref name="serialType"/>.</summary>
    private static int GetBodyLength(ulong serialType)
        => serialType switch
        {
            0 or 8 or 9 => 0,
            1 => 1,
            2 => 2,
            3 => 3,
            4 => 4,
            5 => 6,
            6 or 7 => 8,
            10 or 11 => throw new InvalidOperationException($"SQLite record uses reserved serial type {serialType}."),
            _ => checked((int)((serialType - (serialType % 2 == 0 ? 12UL : 13UL)) / 2)),
        };

    private static ulong GetIntegerSerialType(long value)
        => value switch
        {
            0 => 8,
            1 => 9,
            >= sbyte.MinValue and <= sbyte.MaxValue => 1,
            >= short.MinValue and <= short.MaxValue => 2,
            >= -8_388_608 and <= 8_388_607 => 3,
            >= int.MinValue and <= int.MaxValue => 4,
            >= -140_737_488_355_328 and <= 140_737_488_355_327 => 5,
            _ => 6,
        };

    /// <summary>Writes one value's body bytes and returns how many were written.</summary>
    private static int WriteValueBody(
        SqlValue value,
        ulong serialType,
        Span<byte> destination,
        Encoding textEncoding)
    {
        switch (serialType)
        {
            case 0 or 8 or 9:
                return 0;
            case 1 or 2 or 3 or 4 or 5 or 6:
                {
                    var byteCount = GetBodyLength(serialType);
                    WriteIntegerBytes(destination, value.AsInteger(), byteCount);
                    return byteCount;
                }
            case 7:
                BinaryPrimitives.WriteInt64BigEndian(destination, BitConverter.DoubleToInt64Bits(value.AsReal()));
                return sizeof(long);
            default:
                {
                    if (serialType % 2 == 1)
                        return textEncoding.GetBytes(value.AsText(), destination);

                    var blob = value.AsBlobSpan();
                    blob.CopyTo(destination);
                    return blob.Length;
                }
        }
    }

    private static void WriteIntegerBytes(Span<byte> destination, long value, int byteCount)
    {
        for (var index = 0; index < byteCount; index++)
            destination[index] = (byte)(value >> ((byteCount - 1 - index) * 8));
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
