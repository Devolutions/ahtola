using System.Buffers.Binary;

namespace Ahtola.Core.Storage;

/// <summary>
/// The on-disk layout of a SQLite DELETE-mode rollback journal, factored out of
/// <see cref="SqliteRollbackJournal"/> so other storage adapters can rewrite
/// journal page payloads without duplicating offsets or the checksum algorithm.
/// </summary>
/// <remarks>
/// Journal page records hold the exact on-disk page image, so an encrypted
/// database stores encrypted pages here and the per-record checksum is computed
/// over those encrypted bytes.
/// </remarks>
internal static class SqliteRollbackJournalFormat
{
    /// <summary>Bytes of meaningful header content at offset zero.</summary>
    internal const int HeaderSize = 28;

    /// <summary>The sector size Ahtola writes, and therefore the first record offset it emits.</summary>
    internal const int SectorSize = 512;

    /// <summary>Bytes preceding the page image inside one record (the page number).</summary>
    internal const int RecordPageNumberSize = 4;

    /// <summary>Bytes following the page image inside one record (the checksum).</summary>
    internal const int RecordChecksumSize = 4;

    /// <summary>The finalized-journal magic written once the records are durable.</summary>
    internal static ReadOnlySpan<byte> Magic => [0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7];

    /// <summary>Total bytes occupied by one page record.</summary>
    internal static long GetRecordSize(int pageSize)
        => checked((long)pageSize + RecordPageNumberSize + RecordChecksumSize);

    /// <summary>Whether <paramref name="header"/> begins with the finalized-journal magic.</summary>
    internal static bool HasMagic(ReadOnlySpan<byte> header)
        => header.Length >= Magic.Length && header[..Magic.Length].SequenceEqual(Magic);

    /// <summary>
    /// Reads the record count, checksum nonce, and page size from a journal header
    /// without validating recoverability. Returns false when the header is too
    /// short or declares a page size that cannot be a SQLite page.
    /// </summary>
    internal static bool TryReadLayout(
        ReadOnlySpan<byte> header,
        out uint recordCount,
        out uint checksumNonce,
        out int pageSize,
        out int sectorSize)
    {
        recordCount = 0;
        checksumNonce = 0;
        pageSize = 0;
        sectorSize = 0;
        if (header.Length < HeaderSize)
            return false;

        var encodedSectorSize = BinaryPrimitives.ReadUInt32BigEndian(header[20..]);
        var encodedPageSize = BinaryPrimitives.ReadUInt32BigEndian(header[24..]);
        if (encodedPageSize < SqlitePageSize.Minimum
            || encodedPageSize > SqlitePageSize.Maximum
            || (encodedPageSize & (encodedPageSize - 1)) != 0)
        {
            return false;
        }
        if (encodedSectorSize < 512
            || encodedSectorSize > 65536
            || (encodedSectorSize & (encodedSectorSize - 1)) != 0)
        {
            return false;
        }

        recordCount = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
        checksumNonce = BinaryPrimitives.ReadUInt32BigEndian(header[12..]);
        pageSize = (int)encodedPageSize;
        sectorSize = (int)encodedSectorSize;
        return true;
    }

    /// <summary>The SQLite journal page checksum: nonce plus every 200th byte from the end.</summary>
    internal static uint ComputeChecksum(ReadOnlySpan<byte> page, uint nonce)
    {
        var checksum = nonce;
        for (var index = page.Length - 200; index >= 0; index -= 200)
            checksum = unchecked(checksum + page[index]);
        return checksum;
    }
}
