using System.Buffers.Binary;

namespace Ahtola.Core.Storage;

public enum SqliteFileFormatVersion : byte
{
    Legacy = 1,
    Wal = 2,
    Mvcc = 255,
}

public enum SqliteTextEncoding : uint
{
    Unset = 0,
    Utf8 = 1,
    Utf16LittleEndian = 2,
    Utf16BigEndian = 3,
}

public sealed record SqliteDatabaseHeader(
    int PageSize,
    SqliteFileFormatVersion WriteVersion,
    SqliteFileFormatVersion ReadVersion,
    byte ReservedSpace,
    uint ChangeCounter,
    uint DatabaseSizeInPages,
    uint FirstFreelistTrunkPage,
    uint FreelistPageCount,
    uint SchemaCookie,
    uint SchemaFormat,
    int DefaultPageCacheSize,
    uint LargestRootBtreePage,
    SqliteTextEncoding TextEncoding,
    int UserVersion,
    uint IncrementalVacuumEnabled,
    int ApplicationId,
    uint VersionValidFor,
    uint SqliteVersion)
{
    public const int Size = 100;
    public const int DefaultSqliteVersion = 3_047_000;
    public const int MinimumUsableSpace = 480;

    private static readonly byte[] Magic = "SQLite format 3\0"u8.ToArray();

    public int UsableSpace => PageSize - ReservedSpace;

    public static SqliteDatabaseHeader CreateDefault()
        => new(
            SqlitePageSize.Default,
            SqliteFileFormatVersion.Wal,
            SqliteFileFormatVersion.Wal,
            0,
            1,
            0,
            0,
            0,
            0,
            4,
            -2_000,
            0,
            SqliteTextEncoding.Utf8,
            0,
            0,
            0,
            DefaultSqliteVersion,
            DefaultSqliteVersion);

    public static SqliteDatabaseHeader Parse(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
            throw new InvalidDataException("SQLite database header is truncated.");
        if (!source[..Magic.Length].SequenceEqual(Magic))
            throw new InvalidDataException("Database does not contain an SQLite format 3 header.");

        var pageSize = SqlitePageSize.Decode(BinaryPrimitives.ReadUInt16BigEndian(source[16..]));
        var writeVersion = ParseFileFormatVersion(source[18], "write");
        var readVersion = ParseFileFormatVersion(source[19], "read");
        var reservedSpace = source[20];
        ValidateUsableSpace(pageSize, reservedSpace, static message => new InvalidDataException(message));
        if (source[21] != 64 || source[22] != 32 || source[23] != 32)
            throw new InvalidDataException("SQLite payload-fraction fields are invalid.");

        var schemaFormat = ReadUInt32(source, 44);
        var changeCounter = ReadUInt32(source, 24);
        var databaseSizeInPages = ReadUInt32(source, 28);
        var firstFreelistTrunkPage = ReadUInt32(source, 32);
        var freelistPageCount = ReadUInt32(source, 36);
        var schemaCookie = ReadUInt32(source, 40);
        var largestRootBtreePage = ReadUInt32(source, 52);
        var textEncoding = ReadTextEncoding(source);
        var versionValidFor = ReadUInt32(source, 92);
        ValidateSchemaFormat(
            schemaFormat,
            changeCounter,
            databaseSizeInPages,
            firstFreelistTrunkPage,
            freelistPageCount,
            schemaCookie,
            largestRootBtreePage,
            textEncoding,
            versionValidFor,
            static message => new InvalidDataException(message));
        ValidateAutoVacuum(
            largestRootBtreePage,
            ReadUInt32(source, 64),
            databaseSizeInPages,
            static message => new InvalidDataException(message));
        if (source.Slice(72, 20).IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("SQLite database header reserved bytes must be zero.");

        return new SqliteDatabaseHeader(
            pageSize,
            writeVersion,
            readVersion,
            reservedSpace,
            changeCounter,
            databaseSizeInPages,
            firstFreelistTrunkPage,
            freelistPageCount,
            schemaCookie,
            schemaFormat,
            ReadCacheSize(source),
            largestRootBtreePage,
            textEncoding,
            BinaryPrimitives.ReadInt32BigEndian(source[60..]),
            ReadUInt32(source, 64),
            BinaryPrimitives.ReadInt32BigEndian(source[68..]),
            versionValidFor,
            ReadUInt32(source, 96));
    }

    public byte[] ToArray()
    {
        var destination = new byte[Size];
        WriteTo(destination);
        return destination;
    }

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException($"SQLite database header requires {Size} bytes.", nameof(destination));
        ValidateUsableSpace(PageSize, ReservedSpace, static message => new InvalidOperationException(message));
        ValidateFileFormatVersion(WriteVersion, "write");
        ValidateFileFormatVersion(ReadVersion, "read");
        ValidateTextEncoding(TextEncoding);
        ValidateSchemaFormat(
            SchemaFormat,
            ChangeCounter,
            DatabaseSizeInPages,
            FirstFreelistTrunkPage,
            FreelistPageCount,
            SchemaCookie,
            LargestRootBtreePage,
            TextEncoding,
            VersionValidFor,
            static message => new InvalidOperationException(message));
        ValidateAutoVacuum(
            LargestRootBtreePage,
            IncrementalVacuumEnabled,
            DatabaseSizeInPages,
            static message => new InvalidOperationException(message));

        destination[..Size].Clear();
        Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt16BigEndian(destination[16..], SqlitePageSize.Encode(PageSize));
        destination[18] = (byte)WriteVersion;
        destination[19] = (byte)ReadVersion;
        destination[20] = ReservedSpace;
        destination[21] = 64;
        destination[22] = 32;
        destination[23] = 32;
        WriteUInt32(destination, 24, ChangeCounter);
        WriteUInt32(destination, 28, DatabaseSizeInPages);
        WriteUInt32(destination, 32, FirstFreelistTrunkPage);
        WriteUInt32(destination, 36, FreelistPageCount);
        WriteUInt32(destination, 40, SchemaCookie);
        WriteUInt32(destination, 44, SchemaFormat);
        BinaryPrimitives.WriteInt32BigEndian(destination[48..], DefaultPageCacheSize == -2_000 ? 0 : DefaultPageCacheSize);
        WriteUInt32(destination, 52, LargestRootBtreePage);
        BinaryPrimitives.WriteUInt32BigEndian(destination[56..], (uint)TextEncoding);
        BinaryPrimitives.WriteInt32BigEndian(destination[60..], UserVersion);
        WriteUInt32(destination, 64, IncrementalVacuumEnabled);
        BinaryPrimitives.WriteInt32BigEndian(destination[68..], ApplicationId);
        WriteUInt32(destination, 92, VersionValidFor);
        WriteUInt32(destination, 96, SqliteVersion);
    }

    private static SqliteFileFormatVersion ParseFileFormatVersion(byte value, string direction)
    {
        if (value is not (byte)SqliteFileFormatVersion.Legacy
            and not (byte)SqliteFileFormatVersion.Wal
            and not (byte)SqliteFileFormatVersion.Mvcc)
        {
            throw new InvalidDataException($"Unsupported SQLite {direction} file format version {value}.");
        }

        return (SqliteFileFormatVersion)value;
    }

    private static void ValidateFileFormatVersion(SqliteFileFormatVersion value, string direction)
    {
        if (value is not SqliteFileFormatVersion.Legacy
            and not SqliteFileFormatVersion.Wal
            and not SqliteFileFormatVersion.Mvcc)
        {
            throw new InvalidOperationException($"Unsupported SQLite {direction} file format version {(byte)value}.");
        }
    }

    private static SqliteTextEncoding ReadTextEncoding(ReadOnlySpan<byte> source)
    {
        var value = BinaryPrimitives.ReadUInt32BigEndian(source[56..]);
        if (value is > (uint)SqliteTextEncoding.Utf16BigEndian)
            throw new InvalidDataException($"Unsupported SQLite text encoding {value}.");

        return (SqliteTextEncoding)value;
    }

    private static void ValidateTextEncoding(SqliteTextEncoding value)
    {
        if ((uint)value > (uint)SqliteTextEncoding.Utf16BigEndian)
            throw new InvalidOperationException($"Unsupported SQLite text encoding {(uint)value}.");
    }

    private static void ValidateSchemaFormat<TException>(
        uint schemaFormat,
        uint changeCounter,
        uint databaseSizeInPages,
        uint firstFreelistTrunkPage,
        uint freelistPageCount,
        uint schemaCookie,
        uint largestRootBtreePage,
        SqliteTextEncoding textEncoding,
        uint versionValidFor,
        Func<string, TException> createException)
        where TException : Exception
    {
        if (schemaFormat is >= 1 and <= 4)
        {
            // SQLite sets the file-format and text-encoding cookies together in
            // sqlite3StartTable, so a file that has declared a schema format has
            // always also declared its encoding. Format 1-4 with encoding zero is
            // therefore an impossible header, not a pristine database.
            if (textEncoding == SqliteTextEncoding.Unset)
            {
                throw createException(
                    $"SQLite schema format {schemaFormat} requires a declared text encoding, but the header encoding is unset.");
            }

            return;
        }

        // SQLite and Turso use format zero before an empty database has acquired
        // a schema encoding. Freelist or auto-vacuum metadata proves it is not
        // that pristine state.
        if (schemaFormat == 0
            && versionValidFor == changeCounter
            && databaseSizeInPages <= 1
            && textEncoding == SqliteTextEncoding.Unset
            && firstFreelistTrunkPage == 0
            && freelistPageCount == 0
            && schemaCookie == 0
            && largestRootBtreePage == 0)
        {
            return;
        }

        throw createException($"Unsupported SQLite schema format {schemaFormat}.");
    }

    /// <summary>
    /// Rejects auto-vacuum header pairs SQLite can never produce.
    /// </summary>
    /// <remarks>
    /// Turso <c>auto_vacuum_header_fields</c> emits only <c>(0,0)</c> for no
    /// auto-vacuum, <c>(root,0)</c> for full auto-vacuum and <c>(root,1)</c> for
    /// incremental auto-vacuum, so a non-zero incremental-vacuum flag without a
    /// largest-root-btree page is structurally impossible. The largest root page
    /// must also lie inside the database when the size field is authoritative.
    /// </remarks>
    private static void ValidateAutoVacuum<TException>(
        uint largestRootBtreePage,
        uint incrementalVacuumEnabled,
        uint databaseSizeInPages,
        Func<string, TException> createException)
        where TException : Exception
    {
        if (incrementalVacuumEnabled != 0 && largestRootBtreePage == 0)
        {
            throw createException(
                "SQLite incremental-vacuum mode requires a non-zero largest root b-tree page.");
        }

        if (incrementalVacuumEnabled > 1)
        {
            throw createException(
                $"SQLite incremental-vacuum flag {incrementalVacuumEnabled} must be zero or one.");
        }

        if (largestRootBtreePage != 0
            && databaseSizeInPages != 0
            && largestRootBtreePage > databaseSizeInPages)
        {
            throw createException(
                $"SQLite largest root b-tree page {largestRootBtreePage} is outside the {databaseSizeInPages}-page database.");
        }
    }

    private static int ReadCacheSize(ReadOnlySpan<byte> source)
    {
        var value = BinaryPrimitives.ReadInt32BigEndian(source[48..]);
        return value == 0 ? -2_000 : value;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> source, int offset)
        => BinaryPrimitives.ReadUInt32BigEndian(source[offset..]);

    private static void WriteUInt32(Span<byte> destination, int offset, uint value)
        => BinaryPrimitives.WriteUInt32BigEndian(destination[offset..], value);

    private static void ValidateUsableSpace<TException>(
        int pageSize,
        byte reservedSpace,
        Func<string, TException> createException)
        where TException : Exception
    {
        if (pageSize - reservedSpace < MinimumUsableSpace)
        {
            throw createException(
                $"SQLite reserved page space must leave at least {MinimumUsableSpace} usable bytes.");
        }
    }
}
