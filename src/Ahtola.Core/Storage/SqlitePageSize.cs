namespace Ahtola.Core.Storage;

public static class SqlitePageSize
{
    public const int Minimum = 512;
    public const int Maximum = 65_536;
    public const int Default = 4_096;

    public static int Decode(ushort encoded)
    {
        var size = encoded == 1 ? Maximum : encoded;
        if (size < Minimum || size > Maximum || (size & (size - 1)) != 0)
            throw new InvalidDataException($"Invalid SQLite page size {size}.");

        return size;
    }

    public static ushort Encode(int size)
    {
        Validate(size);
        return size == Maximum ? (ushort)1 : checked((ushort)size);
    }

    /// <summary>Throws when <paramref name="size"/> is not a legal SQLite page size.</summary>
    public static void Validate(int size)
    {
        if (size < Minimum || size > Maximum || (size & (size - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(size), size, "SQLite page size must be a power of two between 512 and 65536.");
    }
}
