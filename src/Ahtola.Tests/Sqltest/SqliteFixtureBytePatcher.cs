namespace Ahtola.Tests.Sqltest;

/// <summary>
/// Byte-level patch helpers used only to build the <c>integrity_check/parity_*</c> corruption
/// fixtures for the conformance harness. Mirrors the <c>parse_sqlite_varint</c>,
/// <c>sqlite_serial_type_payload_len</c>, and <c>patch_*</c> routines in
/// <c>turso-src/testing/sqltest/src/generator/mod.rs</c>: every offset and page-header field
/// here is the real on-disk SQLite b-tree page layout (page header at the page start — flags,
/// 3..4 cell count, 5..6 cell content area start, 7 fragmented-byte count, 8.. cell pointer
/// array — record header varint + serial-type array, as documented alongside
/// <c>Ahtola.Core/Storage/SqliteCellPointerArray.cs</c> and <c>SqliteRecordCodec.cs</c>), so
/// the fixtures these produce are genuinely corrupted bytes, not synthetic stand-ins.
/// </summary>
internal static class SqliteFixtureBytePatcher
{
    /// <summary>
    /// Reads the on-disk page size from the 100-byte file header (offset 16, big-endian
    /// 16-bit, with the SQLite special case that a raw value of 1 means 65536).
    /// </summary>
    public static int ReadDbHeaderPageSize(byte[] bytes)
    {
        if (bytes.Length < 18)
            throw new InvalidOperationException("fixture is too small to contain a database header");
        var raw = (bytes[16] << 8) | bytes[17];
        return raw == 1 ? 65536 : raw;
    }

    /// <summary>
    /// Finds the root page of a named object by walking the raw bytes of the schema table
    /// (always page 1) directly, instead of going through SQL. Ahtola's <c>sqlite_schema</c>
    /// projection always reports <c>rootpage 0</c> (a documented stub for the in-memory read
    /// path — see <c>EmbeddedDatabase.EnumerateCatalogSchemaRows</c>), so the only way to learn
    /// the real physical root page a freshly-built fixture uses is to read it from the actual
    /// on-disk schema-table record, exactly as Ahtola's own pager must when it plans a query
    /// against that object.
    /// </summary>
    public static int FindObjectRootPage(byte[] bytes, int pageSize, string objectName)
    {
        // Page 1's b-tree page header follows the 100-byte file header.
        const int SchemaPageHeaderOffset = 100;
        if (bytes.Length < SchemaPageHeaderOffset + 8)
            throw new InvalidOperationException("fixture is too small to contain a schema page");

        var pageType = bytes[SchemaPageHeaderOffset];
        if (pageType != 0x0D)
        {
            throw new InvalidOperationException(
                $"expected the schema (page 1) to be a table-leaf page (0x0D), got 0x{pageType:X2}; " +
                "this harness only walks a single-leaf schema page, which every small fixture produces");
        }

        var cellCount = (bytes[SchemaPageHeaderOffset + 3] << 8) | bytes[SchemaPageHeaderOffset + 4];
        var ptrArrayStart = SchemaPageHeaderOffset + 8;
        for (var i = 0; i < cellCount; i++)
        {
            // Cell pointers on page 1 are relative to the start of the file (offset 0), not
            // to the 100-byte header, because SQLite's page-relative addressing for page 1
            // is defined relative to the page itself, which begins at file offset 0.
            var cellStart = ReadCellPointer(bytes, ptrArrayStart + (i * 2));

            var (_, payloadVarintLength) = ParseVarint(bytes, cellStart);
            var (_, rowidVarintLength) = ParseVarint(bytes, cellStart + payloadVarintLength);
            var payloadStart = cellStart + payloadVarintLength + rowidVarintLength;

            var (headerSize, headerSizeVarintLength) = ParseVarint(bytes, payloadStart);
            var serialsStart = payloadStart + headerSizeVarintLength;

            // sqlite_schema columns, in order: type, name, tbl_name, rootpage, sql.
            var serialTypes = new long[5];
            var cursor = serialsStart;
            for (var column = 0; column < serialTypes.Length; column++)
            {
                var (serialType, length) = ParseVarint(bytes, cursor);
                serialTypes[column] = serialType;
                cursor += length;
            }

            var dataStart = payloadStart + (int)headerSize;
            string? nameValue = null;
            long rootPageValue = 0;
            var dataOffset = dataStart;
            for (var column = 0; column < serialTypes.Length; column++)
            {
                var length = SerialTypePayloadLength(serialTypes[column]);
                if (length < 0)
                    throw new InvalidOperationException($"unsupported serial type {serialTypes[column]} in schema row");

                if (column == 1)
                    nameValue = DecodeText(bytes, dataOffset, length);
                else if (column == 3)
                    rootPageValue = DecodeInteger(serialTypes[column], bytes, dataOffset);

                dataOffset += length;
            }

            if (string.Equals(nameValue, objectName, StringComparison.Ordinal))
                return checked((int)rootPageValue);
        }

        throw new InvalidOperationException($"schema object '{objectName}' not found on the raw schema page");
    }

    private static string DecodeText(byte[] bytes, int offset, int length)
        => System.Text.Encoding.UTF8.GetString(bytes, offset, length);

    /// <summary>Decodes a SQLite record's INTEGER-family serial type (0,1,2,3,4,5,6,8,9) to a <see cref="long"/>.</summary>
    private static long DecodeInteger(long serialType, byte[] bytes, int offset)
        => serialType switch
        {
            0 => 0,
            8 => 0,
            9 => 1,
            1 => unchecked((sbyte)bytes[offset]),
            2 => unchecked((short)((bytes[offset] << 8) | bytes[offset + 1])),
            3 => unchecked((bytes[offset] << 16) | (bytes[offset + 1] << 8) | bytes[offset + 2])
                is var value24 && (value24 & 0x800000) != 0 ? value24 | unchecked((int)0xFF000000) : value24,
            4 => (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3],
            5 => DecodeBigEndianSigned(bytes, offset, 6),
            6 => DecodeBigEndianSigned(bytes, offset, 8),
            _ => throw new InvalidOperationException($"serial type {serialType} is not an integer-family type"),
        };

    private static long DecodeBigEndianSigned(byte[] bytes, int offset, int length)
    {
        long value = unchecked((sbyte)bytes[offset]);
        for (var i = 1; i < length; i++)
            value = (value << 8) | bytes[offset + i];
        return value;
    }

    /// <summary>Decodes a SQLite variant-length integer at <paramref name="offset"/>.</summary>
    public static (long Value, int Length) ParseVarint(byte[] bytes, int offset)
    {
        if ((uint)offset >= (uint)bytes.Length)
            throw new ArgumentOutOfRangeException(nameof(offset), "varint offset out of bounds");

        var start = offset;
        long value = 0;
        for (var i = 0; i < 8; i++)
        {
            var b = bytes[offset];
            offset++;
            value = (value << 7) | (uint)(b & 0x7f);
            if ((b & 0x80) == 0)
                return (value, offset - start);
            if (offset >= bytes.Length)
                throw new InvalidOperationException("truncated varint");
            if (i == 7)
            {
                var b9 = bytes[offset];
                value = (value << 8) | b9;
                return (value, offset + 1 - start);
            }
        }

        throw new InvalidOperationException("invalid varint encoding");
    }

    /// <summary>Payload byte length for a SQLite record serial type, or -1 if unsupported.</summary>
    public static int SerialTypePayloadLength(long serialType)
        => serialType switch
        {
            0 or 8 or 9 => 0,
            1 => 1,
            2 => 2,
            3 => 3,
            4 => 4,
            5 => 6,
            6 or 7 => 8,
            >= 12 when serialType % 2 == 0 => (int)((serialType - 12) / 2),
            >= 13 when serialType % 2 == 1 => (int)((serialType - 13) / 2),
            _ => -1,
        };

    private static int ReadPageHeaderCellCount(byte[] bytes, int pageStart)
        => (bytes[pageStart + 3] << 8) | bytes[pageStart + 4];

    private static int ReadCellPointer(byte[] bytes, int offset)
        => (bytes[offset] << 8) | bytes[offset + 1];

    private static void WriteUInt16BigEndian(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)(value >> 8);
        bytes[offset + 1] = (byte)value;
    }

    /// <summary>
    /// Validates a computed page offset before any byte access, with a diagnostic message
    /// that names every value that went into the computation — a plain
    /// <see cref="IndexOutOfRangeException"/> from a raw array access gives no clue whether
    /// the root page, page size, or on-disk file length was the culprit.
    /// </summary>
    private static void ValidatePageBounds(byte[] bytes, int pageSize, int rootPage, int pageStart, int minimumLength)
    {
        if (pageStart < 0 || bytes.Length < pageStart + minimumLength)
        {
            throw new InvalidOperationException(
                $"fixture too small to patch root page {rootPage}: file is {bytes.Length} byte(s), " +
                $"page_size={pageSize}, computed page start={pageStart}, needs at least {minimumLength} " +
                "byte(s) from that offset. This usually means the on-disk file has fewer pages than the " +
                "schema's rootpage implies (a checkpoint/flush did not happen before the file was read).");
        }
    }

    /// <summary>
    /// Drops the last cell from a b-tree page (mirrors <c>patch_drop_last_cell_from_btree_page</c>):
    /// decrements the cell count and, if the removed cell owned the content-area start,
    /// moves the content area to the smallest still-referenced cell so the page stays
    /// structurally consistent apart from the missing entry.
    /// </summary>
    public static void DropLastCellFromBtreePage(byte[] bytes, int pageSize, int rootPage)
    {
        var pageStart = (rootPage - 1) * pageSize;
        ValidatePageBounds(bytes, pageSize, rootPage, pageStart, 8);

        var cellCount = ReadPageHeaderCellCount(bytes, pageStart);
        if (cellCount < 2)
            throw new InvalidOperationException($"cannot drop index cell from page {rootPage}: only {cellCount} cells");

        var ptrArrayStart = pageStart + 8;
        var droppedPtrOffset = ptrArrayStart + ((cellCount - 1) * 2);
        var droppedCellStart = ReadCellPointer(bytes, droppedPtrOffset);

        WriteUInt16BigEndian(bytes, pageStart + 3, cellCount - 1);

        var oldContentArea = ReadCellPointer(bytes, pageStart + 5);
        if (oldContentArea == droppedCellStart)
        {
            var minPointer = int.MaxValue;
            for (var i = 0; i < cellCount - 1; i++)
                minPointer = Math.Min(minPointer, ReadCellPointer(bytes, ptrArrayStart + (i * 2)));

            if (minPointer == int.MaxValue)
                throw new InvalidOperationException("failed to compute updated content area");
            WriteUInt16BigEndian(bytes, pageStart + 5, minPointer);
        }
    }

    /// <summary>
    /// Sets the second column of the table row's first cell to an 8-bit integer value
    /// (mirrors <c>patch_set_second_table_column_i8_in_first_row</c>).
    /// </summary>
    public static void SetSecondTableColumnI8InFirstRow(byte[] bytes, int pageSize, int rootPage, sbyte newValue)
    {
        var pageStart = (rootPage - 1) * pageSize;
        ValidatePageBounds(bytes, pageSize, rootPage, pageStart, 8);
        if (ReadPageHeaderCellCount(bytes, pageStart) < 1)
            throw new InvalidOperationException($"cannot patch table row on page {rootPage}: no cells");

        var ptrArrayStart = pageStart + 8;
        var cellStart = pageStart + ReadCellPointer(bytes, ptrArrayStart);

        var (_, payloadVarintLength) = ParseVarint(bytes, cellStart);
        var (_, rowidVarintLength) = ParseVarint(bytes, cellStart + payloadVarintLength);
        var payloadStart = cellStart + payloadVarintLength + rowidVarintLength;

        var (headerSize, headerSizeVarintLength) = ParseVarint(bytes, payloadStart);
        if (payloadStart + headerSize > bytes.Length)
            throw new InvalidOperationException($"record header out of bounds on page {rootPage}");

        var serialsStart = payloadStart + headerSizeVarintLength;
        var (firstSerialType, firstSerialLength) = ParseVarint(bytes, serialsStart);
        var (secondSerialType, _) = ParseVarint(bytes, serialsStart + firstSerialLength);

        var dataStart = payloadStart + (int)headerSize;
        var firstDataLength = SerialTypePayloadLength(firstSerialType);
        var secondDataLength = SerialTypePayloadLength(secondSerialType);
        if (firstDataLength < 0)
            throw new InvalidOperationException($"unsupported first-column serial type {firstSerialType}");
        if (secondDataLength != 1)
            throw new InvalidOperationException(
                $"expected second-column i8 payload, got serial type {secondSerialType}");

        var secondDataOffset = dataStart + firstDataLength;
        if (secondDataOffset >= bytes.Length)
            throw new InvalidOperationException($"second-column payload offset out of bounds on page {rootPage}");
        bytes[secondDataOffset] = unchecked((byte)newValue);
    }

    /// <summary>
    /// Sets the second column of the table row's first cell to NULL (mirrors
    /// <c>patch_set_second_table_column_to_null_in_first_row</c>). Expects the column to hold
    /// an empty TEXT value (serial type 13, zero body bytes) so swapping in NULL (serial type
    /// 0, also zero body bytes) does not disturb the record's payload layout.
    /// </summary>
    public static void SetSecondTableColumnToNullInFirstRow(byte[] bytes, int pageSize, int rootPage)
    {
        var pageStart = (rootPage - 1) * pageSize;
        ValidatePageBounds(bytes, pageSize, rootPage, pageStart, 8);
        if (ReadPageHeaderCellCount(bytes, pageStart) < 1)
            throw new InvalidOperationException($"cannot patch table row on page {rootPage}: no cells");

        var ptrArrayStart = pageStart + 8;
        var cellStart = pageStart + ReadCellPointer(bytes, ptrArrayStart);
        if (cellStart >= bytes.Length)
            throw new InvalidOperationException($"cell pointer out of bounds on page {rootPage}");

        var (_, payloadVarintLength) = ParseVarint(bytes, cellStart);
        var (_, rowidVarintLength) = ParseVarint(bytes, cellStart + payloadVarintLength);
        var payloadStart = cellStart + payloadVarintLength + rowidVarintLength;
        if (payloadStart >= bytes.Length)
            throw new InvalidOperationException($"payload start out of bounds on page {rootPage}");

        var (headerSize, headerSizeVarintLength) = ParseVarint(bytes, payloadStart);
        var headerEnd = payloadStart + (int)headerSize;
        if (headerEnd > bytes.Length)
            throw new InvalidOperationException($"record header out of bounds on page {rootPage}");

        var serialsStart = payloadStart + headerSizeVarintLength;
        var (_, firstSerialLength) = ParseVarint(bytes, serialsStart);
        var secondSerialOffset = serialsStart + firstSerialLength;
        if (secondSerialOffset >= headerEnd)
            throw new InvalidOperationException($"record does not contain a second column on page {rootPage}");

        if (bytes[secondSerialOffset] != 13)
            throw new InvalidOperationException(
                $"unexpected serial type {bytes[secondSerialOffset]} for fixture row");
        bytes[secondSerialOffset] = 0;
    }

    /// <summary>
    /// Sets the first column of the table row's first cell to NULL (mirrors
    /// <c>patch_set_first_table_column_to_null_in_first_row</c>). Expects serial type 8
    /// (integer zero, zero body bytes) so the swap to NULL (also zero body bytes) is clean.
    /// </summary>
    public static void SetFirstTableColumnToNullInFirstRow(byte[] bytes, int pageSize, int rootPage)
    {
        var pageStart = (rootPage - 1) * pageSize;
        ValidatePageBounds(bytes, pageSize, rootPage, pageStart, 8);
        if (ReadPageHeaderCellCount(bytes, pageStart) < 1)
            throw new InvalidOperationException($"cannot patch table row on page {rootPage}: no cells");

        var ptrArrayStart = pageStart + 8;
        var cellStart = pageStart + ReadCellPointer(bytes, ptrArrayStart);
        if (cellStart >= bytes.Length)
            throw new InvalidOperationException($"cell pointer out of bounds on page {rootPage}");

        var (_, payloadVarintLength) = ParseVarint(bytes, cellStart);
        var (_, rowidVarintLength) = ParseVarint(bytes, cellStart + payloadVarintLength);
        var payloadStart = cellStart + payloadVarintLength + rowidVarintLength;
        if (payloadStart >= bytes.Length)
            throw new InvalidOperationException($"payload start out of bounds on page {rootPage}");

        var (_, headerSizeVarintLength) = ParseVarint(bytes, payloadStart);
        var firstSerialOffset = payloadStart + headerSizeVarintLength;

        if (bytes[firstSerialOffset] != 8)
            throw new InvalidOperationException(
                $"unexpected serial type {bytes[firstSerialOffset]} for fixture row (expected 8 = integer zero)");
        bytes[firstSerialOffset] = 0;
    }

    /// <summary>
    /// Scans an index page's cells for one whose first (and only, for a single-column index)
    /// key equals <paramref name="from"/> and rewrites it to <paramref name="to"/> (mirrors
    /// <c>patch_change_first_index_key_i8</c>). Expects an 8-bit integer key (serial type 1).
    /// </summary>
    public static void ChangeFirstIndexKeyI8(byte[] bytes, int pageSize, int rootPage, sbyte from, sbyte to)
    {
        var pageStart = (rootPage - 1) * pageSize;
        ValidatePageBounds(bytes, pageSize, rootPage, pageStart, 8);

        var cellCount = ReadPageHeaderCellCount(bytes, pageStart);
        if (cellCount < 1)
            throw new InvalidOperationException($"cannot patch index entry on page {rootPage}: no cells");

        var ptrArrayStart = pageStart + 8;
        for (var i = 0; i < cellCount; i++)
        {
            var cellStart = pageStart + ReadCellPointer(bytes, ptrArrayStart + (i * 2));

            var (_, payloadVarintLength) = ParseVarint(bytes, cellStart);
            var payloadStart = cellStart + payloadVarintLength;
            var (headerSize, headerSizeVarintLength) = ParseVarint(bytes, payloadStart);
            var serialsStart = payloadStart + headerSizeVarintLength;
            var (firstSerialType, _) = ParseVarint(bytes, serialsStart);
            if (firstSerialType != 1)
                throw new InvalidOperationException(
                    $"expected first index key serial type 1, got {firstSerialType}");

            var dataStart = payloadStart + (int)headerSize;
            if (dataStart >= bytes.Length)
                throw new InvalidOperationException($"index key payload offset out of bounds on page {rootPage}");
            if (bytes[dataStart] == unchecked((byte)from))
            {
                bytes[dataStart] = unchecked((byte)to);
                return;
            }
        }

        throw new InvalidOperationException($"failed to find index key value {from} to patch on page {rootPage}");
    }

    /// <summary>Reads the database header's freelist trunk page and count fields (offsets 32/36).</summary>
    public static (int TrunkPage, int Count) ReadDbHeaderFreelistFields(byte[] bytes)
    {
        if (bytes.Length < 40)
            throw new InvalidOperationException("fixture is too small to contain database header freelist fields");
        var trunk = (bytes[32] << 24) | (bytes[33] << 16) | (bytes[34] << 8) | bytes[35];
        var count = (bytes[36] << 24) | (bytes[37] << 16) | (bytes[38] << 8) | bytes[39];
        return (trunk, count);
    }

    /// <summary>Overwrites the header's freelist-page count field (offset 36) directly.</summary>
    public static void SetDbHeaderFreelistCount(byte[] bytes, int newCount)
    {
        if (bytes.Length < 40)
            throw new InvalidOperationException("fixture is too small to patch freelist count");
        bytes[36] = (byte)(newCount >> 24);
        bytes[37] = (byte)(newCount >> 16);
        bytes[38] = (byte)(newCount >> 8);
        bytes[39] = (byte)newCount;
    }

    /// <summary>
    /// Inflates a freelist trunk page's leaf-pointer count field beyond what the page can
    /// physically hold (mirrors <c>patch_set_freelist_trunk_leaf_count</c>).
    /// </summary>
    public static void SetFreelistTrunkLeafCount(byte[] bytes, int pageSize, int trunkPage)
    {
        if (trunkPage <= 0)
            throw new InvalidOperationException("cannot patch freelist trunk page 0");
        var pageStart = (trunkPage - 1) * pageSize;
        if (bytes.Length < pageStart + 8)
            throw new InvalidOperationException($"fixture too small to patch freelist trunk page {trunkPage}");

        var maxPointers = (pageSize - 8) / 4;
        var newCount = maxPointers + 1;
        bytes[pageStart + 4] = (byte)(newCount >> 24);
        bytes[pageStart + 5] = (byte)(newCount >> 16);
        bytes[pageStart + 6] = (byte)(newCount >> 8);
        bytes[pageStart + 7] = (byte)newCount;
    }

    /// <summary>Local-payload size when a record's payload overflows (mirrors the SQLite formula).</summary>
    private static int? PayloadOverflowLocalWithPointer(int payloadSize, int usableSize)
    {
        var maxLocal = usableSize - 35;
        if (payloadSize <= maxLocal)
            return null;

        var minLocal = (((usableSize - 12) * 32) / 255) - 23;
        var overflowPagePayload = usableSize - 4;
        if (overflowPagePayload <= 0)
            return null;

        var local = minLocal + ((payloadSize - minLocal) % overflowPagePayload);
        if (local > maxLocal)
            local = minLocal;
        return local + 4;
    }

    /// <summary>
    /// Truncates the overflow chain of the table's first row by following its stored first
    /// overflow-page pointer and zeroing that page's own next-page pointer (mirrors
    /// <c>patch_truncate_first_overflow_chain_for_first_table_row</c>), leaving the record's
    /// declared payload size larger than what the (now-shorter) overflow chain actually holds.
    /// </summary>
    public static void TruncateFirstOverflowChainForFirstTableRow(byte[] bytes, int pageSize, int tableRootPage)
    {
        var pageStart = (tableRootPage - 1) * pageSize;
        ValidatePageBounds(bytes, pageSize, tableRootPage, pageStart, 8);

        var pageFlags = bytes[pageStart];
        if (pageFlags != 13)
            throw new InvalidOperationException(
                $"expected table-leaf root page for overflow fixture, got flags={pageFlags}");

        if (ReadPageHeaderCellCount(bytes, pageStart) < 1)
            throw new InvalidOperationException($"cannot patch overflow chain on page {tableRootPage}: no cells");

        var ptrArrayStart = pageStart + 8;
        var cellStart = pageStart + ReadCellPointer(bytes, ptrArrayStart);

        var (payloadSizeLong, payloadVarintLength) = ParseVarint(bytes, cellStart);
        var (_, rowidVarintLength) = ParseVarint(bytes, cellStart + payloadVarintLength);
        var payloadSize = checked((int)payloadSizeLong);

        var localWithPointer = PayloadOverflowLocalWithPointer(payloadSize, pageSize)
            ?? throw new InvalidOperationException($"row payload does not overflow for page {tableRootPage}");
        if (localWithPointer < 4)
            throw new InvalidOperationException("invalid local payload/pointer size while patching overflow fixture");

        var firstOverflowPtrOffset = cellStart + payloadVarintLength + rowidVarintLength + localWithPointer - 4;
        if (firstOverflowPtrOffset + 4 > bytes.Length)
            throw new InvalidOperationException($"overflow pointer offset out of bounds on page {tableRootPage}");

        var firstOverflowPage =
            (bytes[firstOverflowPtrOffset] << 24)
            | (bytes[firstOverflowPtrOffset + 1] << 16)
            | (bytes[firstOverflowPtrOffset + 2] << 8)
            | bytes[firstOverflowPtrOffset + 3];
        if (firstOverflowPage <= 0)
            throw new InvalidOperationException("fixture row has no overflow chain");

        var overflowStart = (firstOverflowPage - 1) * pageSize;
        if (overflowStart + 4 > bytes.Length)
            throw new InvalidOperationException($"first overflow page {firstOverflowPage} is out of range");

        var nextOverflow =
            (bytes[overflowStart] << 24)
            | (bytes[overflowStart + 1] << 16)
            | (bytes[overflowStart + 2] << 8)
            | bytes[overflowStart + 3];
        if (nextOverflow == 0)
            throw new InvalidOperationException("fixture payload must span at least two overflow pages");

        bytes[overflowStart] = 0;
        bytes[overflowStart + 1] = 0;
        bytes[overflowStart + 2] = 0;
        bytes[overflowStart + 3] = 0;
    }
}
