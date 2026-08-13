namespace Ahtola.Core.Storage;

public static class SqliteVarint
{
    public const int MaximumLength = 9;

    public static bool TryRead(ReadOnlySpan<byte> source, out ulong value, out int bytesRead)
    {
        value = 0;
        bytesRead = 0;

        for (var index = 0; index < MaximumLength; index++)
        {
            if (index >= source.Length)
                return false;

            var current = source[index];
            if (index == MaximumLength - 1)
            {
                if ((value >> 48) == 0)
                    return false;

                value = (value << 8) | (ulong)current;
                bytesRead = MaximumLength;
                return true;
            }

            value = (value << 7) | ((ulong)current & 0x7fUL);
            if ((current & 0x80) == 0)
            {
                bytesRead = index + 1;
                return true;
            }
        }

        throw new InvalidOperationException("SQLite varint reader exceeded its maximum length.");
    }

    public static int Write(ulong value, Span<byte> destination)
    {
        var length = GetLength(value);
        if (destination.Length < length)
            throw new ArgumentException($"Destination must contain at least {length} bytes.", nameof(destination));

        if (length == MaximumLength)
        {
            destination[MaximumLength - 1] = (byte)value;
            value >>= 8;

            for (var index = MaximumLength - 2; index >= 0; index--)
            {
                destination[index] = (byte)((value & 0x7f) | 0x80);
                value >>= 7;
            }

            return length;
        }

        for (var index = length - 1; index >= 0; index--)
        {
            destination[index] = (byte)(value & 0x7f);
            value >>= 7;
        }

        for (var index = 0; index < length - 1; index++)
            destination[index] |= 0x80;

        return length;
    }

    public static int GetLength(ulong value)
    {
        if (value <= 0x7f)
            return 1;

        var length = 1;
        while (value > 0x7f)
        {
            value >>= 7;
            length++;
        }

        return length < MaximumLength ? length : MaximumLength;
    }
}
