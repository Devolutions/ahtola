using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace Ahtola.Core.Storage;

/// <summary>The native byte order used by SQLite's transient WAL-index.</summary>
public enum SqliteWalIndexByteOrder
{
    LittleEndian,
    BigEndian,
}

/// <summary>
/// Optional shared-memory capability required before a filesystem can participate
/// in SQLite-compatible WAL-index coordination.
/// </summary>
/// <remarks>
/// This capability is deliberately separate from <see cref="IFileSystem"/> until
/// a production implementation provides mapped, cross-process-visible memory on
/// every supported physical platform. The physical pager consumes
/// <see cref="PhysicalFileSystem"/> mappings for Stage 1 WAL-index publication
/// under Stage 0 ownership; Stages 2–6 still attach reader/writer protocols.
/// </remarks>
public interface ISqliteWalSharedMemoryFileSystem
{
    /// <summary>Opens a mapped SQLite WAL shared-memory region.</summary>
    ISqliteWalSharedMemoryMapping OpenSharedMemory(
        string path,
        FileOpenMode mode,
        bool readOnly = false);
}

/// <summary>
/// A cross-process-visible mapping of SQLite's transient WAL shared-memory file.
/// </summary>
/// <remarks>
/// A mapping alone does not establish a coherent WAL snapshot. Future pager code
/// must acquire the SQLite role locks and use <see cref="MemoryBarrier"/> between
/// duplicate header publication before trusting or changing this memory.
/// </remarks>
public interface ISqliteWalSharedMemoryMapping : IDisposable
{
    /// <summary>The current mapped length in bytes.</summary>
    long Length { get; }

    /// <summary>Whether this mapping rejects writes.</summary>
    bool IsReadOnly { get; }

    /// <summary>Copies bytes from the mapping at an absolute offset.</summary>
    void Read(long position, Span<byte> destination);

    /// <summary>Copies bytes to the mapping at an absolute offset.</summary>
    void Write(long position, ReadOnlySpan<byte> source);

    /// <summary>
    /// Publishes prior shared-memory writes before a dependent reader or writer
    /// observes the next WAL-index state.
    /// </summary>
    void MemoryBarrier();
}

/// <summary>
/// Provides the immutable identity of the physical file backing a mapped SQLite
/// WAL shared-memory region.
/// </summary>
internal interface ISqliteWalSharedMemoryCarrierIdentity
{
    SqliteWalSharedMemoryCarrierIdentity CarrierIdentity { get; }
}

/// <summary>
/// Duplicates handles for the exact file that backs a physical shared-memory
/// mapping, rather than reopening its path.
/// </summary>
internal interface ISqliteWalSharedMemoryLockCarrier : ISqliteWalSharedMemoryCarrierIdentity
{
    bool PreventsCarrierReplacement { get; }

    SafeFileHandle DuplicateLockCarrierHandle();
}

/// <summary>Defines the fixed layout of SQLite's 32 KiB WAL-index blocks.</summary>
public static class SqliteWalIndexLayout
{
    /// <summary>Size of one SQLite WAL-index block in bytes.</summary>
    public const int BlockSize = 32 * 1024;

    /// <summary>Bytes occupied by both headers and checkpoint information.</summary>
    public const int HeaderRegionSize = 136;

    /// <summary>Frames indexed by the first block after its header region.</summary>
    public const int FirstBlockFrameCapacity = 4_062;

    /// <summary>Frames indexed by every block after the first.</summary>
    public const int SubsequentBlockFrameCapacity = 4_096;

    /// <summary>Hash slots in every WAL-index block.</summary>
    public const int HashSlotCount = 8_192;

    /// <summary>Returns the zero-based WAL-index block containing a frame.</summary>
    public static int GetBlockIndex(uint frameNumber)
    {
        if (frameNumber == 0)
            throw new ArgumentOutOfRangeException(nameof(frameNumber), "SQLite WAL frame numbers start at one.");

        if (frameNumber <= FirstBlockFrameCapacity)
            return 0;

        return checked((int)((frameNumber - FirstBlockFrameCapacity - 1) / SubsequentBlockFrameCapacity) + 1);
    }

    /// <summary>Returns the byte offset of a frame's page-number slot.</summary>
    public static long GetPageNumberOffset(uint frameNumber)
    {
        var blockIndex = GetBlockIndex(frameNumber);
        var slotIndex = blockIndex == 0
            ? frameNumber - 1
            : (frameNumber - FirstBlockFrameCapacity - 1) % SubsequentBlockFrameCapacity;
        var blockOffset = checked((long)blockIndex * BlockSize);
        var pageNumberOffset = blockIndex == 0
            ? HeaderRegionSize
            : 0;

        return checked(blockOffset + pageNumberOffset + slotIndex * sizeof(uint));
    }

    /// <summary>Returns the byte offset of a block's zero-based hash slot.</summary>
    public static long GetHashSlotOffset(int blockIndex, int hashSlotIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blockIndex);
        if ((uint)hashSlotIndex >= HashSlotCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(hashSlotIndex),
                $"SQLite WAL-index hash slot must be between zero and {HashSlotCount - 1}.");
        }

        var pageNumberBytes = blockIndex == 0
            ? FirstBlockFrameCapacity * sizeof(uint)
            : SubsequentBlockFrameCapacity * sizeof(uint);
        var blockOffset = checked((long)blockIndex * BlockSize);
        var pageNumberOffset = blockIndex == 0
            ? HeaderRegionSize
            : 0;

        return checked(blockOffset + pageNumberOffset + pageNumberBytes + hashSlotIndex * sizeof(ushort));
    }

    /// <summary>Returns the number of allocated blocks needed to index a frame.</summary>
    public static int GetRequiredBlockCount(uint maximumFrame)
    {
        if (maximumFrame <= FirstBlockFrameCapacity)
            return 1;

        var remainingFrames = maximumFrame - FirstBlockFrameCapacity;
        return checked((int)((remainingFrames + SubsequentBlockFrameCapacity - 1) / SubsequentBlockFrameCapacity) + 1);
    }
}

/// <summary>
/// The validated 48-byte SQLite WAL-index header stored twice at the start of
/// the transient <c>-shm</c> region.
/// </summary>
public sealed record SqliteWalIndexHeader
{
    /// <summary>Size in bytes of one WAL-index header copy.</summary>
    public const int Size = 48;

    /// <summary>The only WAL-index version understood by current SQLite.</summary>
    public const uint CurrentFormatVersion = 3_007_000;

    private SqliteWalIndexHeader(
        uint changeCounter,
        SqliteWalChecksumByteOrder walChecksumByteOrder,
        int pageSize,
        uint maximumFrame,
        uint databasePageCount,
        uint frameChecksum1,
        uint frameChecksum2,
        uint salt1,
        uint salt2,
        uint checksum1,
        uint checksum2)
    {
        ChangeCounter = changeCounter;
        WalChecksumByteOrder = walChecksumByteOrder;
        PageSize = pageSize;
        MaximumFrame = maximumFrame;
        DatabasePageCount = databasePageCount;
        FrameChecksum1 = frameChecksum1;
        FrameChecksum2 = frameChecksum2;
        Salt1 = salt1;
        Salt2 = salt2;
        Checksum1 = checksum1;
        Checksum2 = checksum2;
    }

    /// <summary>The native byte order of the current SQLite WAL-index host.</summary>
    public static SqliteWalIndexByteOrder NativeByteOrder { get; }
        = BitConverter.IsLittleEndian
            ? SqliteWalIndexByteOrder.LittleEndian
            : SqliteWalIndexByteOrder.BigEndian;

    /// <summary>The WAL-index format version.</summary>
    public static uint FormatVersion => CurrentFormatVersion;

    /// <summary>The transaction-change counter published by the writer.</summary>
    public uint ChangeCounter { get; }

    /// <summary>The byte order used by the associated WAL rolling checksums.</summary>
    public SqliteWalChecksumByteOrder WalChecksumByteOrder { get; }

    /// <summary>The database page size represented by this WAL-index.</summary>
    public int PageSize { get; }

    /// <summary>The last valid, committed frame published by the writer.</summary>
    public uint MaximumFrame { get; }

    /// <summary>The committed database size in pages.</summary>
    public uint DatabasePageCount { get; }

    /// <summary>The first checksum word of <see cref="MaximumFrame"/>.</summary>
    public uint FrameChecksum1 { get; }

    /// <summary>The second checksum word of <see cref="MaximumFrame"/>.</summary>
    public uint FrameChecksum2 { get; }

    /// <summary>The first WAL salt copied verbatim from the WAL header.</summary>
    public uint Salt1 { get; }

    /// <summary>The second WAL salt copied verbatim from the WAL header.</summary>
    public uint Salt2 { get; }

    /// <summary>The first checksum word over the preceding header bytes.</summary>
    public uint Checksum1 { get; }

    /// <summary>The second checksum word over the preceding header bytes.</summary>
    public uint Checksum2 { get; }

    /// <summary>
    /// Parses a header written by a SQLite process on this host architecture.
    /// </summary>
    public static SqliteWalIndexHeader Parse(ReadOnlySpan<byte> source)
        => Parse(source, NativeByteOrder);

    /// <summary>
    /// Parses one exact WAL-index header using its host-native byte order.
    /// </summary>
    /// <remarks>
    /// This overload exists for format validation. A live SQLite WAL-index must
    /// only be consumed on a host with the matching native byte order.
    /// </remarks>
    public static SqliteWalIndexHeader Parse(
        ReadOnlySpan<byte> source,
        SqliteWalIndexByteOrder nativeByteOrder)
    {
        RequireExactLength(source.Length, Size, "SQLite WAL-index header");
        ValidateByteOrder(nativeByteOrder);

        var version = ReadUInt32(source, nativeByteOrder);
        if (version != CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"Unsupported SQLite WAL-index format version {version}; expected {CurrentFormatVersion}.");
        }
        if (ReadUInt32(source[4..], nativeByteOrder) != 0)
            throw new InvalidDataException("SQLite WAL-index header padding must be zero.");
        if (source[12] != 1)
            throw new InvalidDataException("SQLite WAL-index header is not initialized.");

        var walChecksumByteOrder = source[13] switch
        {
            0 => SqliteWalChecksumByteOrder.LittleEndian,
            1 => SqliteWalChecksumByteOrder.BigEndian,
            var value => throw new InvalidDataException(
                $"SQLite WAL-index header has invalid big-endian checksum flag {value}."),
        };
        var pageSize = DecodePageSize(ReadUInt16(source[14..], nativeByteOrder));
        var checksum = SqliteWalChecksum.Calculate(
            source[..40],
            ToWalChecksumByteOrder(nativeByteOrder));
        var checksum1 = ReadUInt32(source[40..], nativeByteOrder);
        var checksum2 = ReadUInt32(source[44..], nativeByteOrder);
        if (checksum != (checksum1, checksum2))
            throw new InvalidDataException("SQLite WAL-index header checksum does not match its contents.");

        var maximumFrame = ReadUInt32(source[16..], nativeByteOrder);
        var databasePageCount = ReadUInt32(source[20..], nativeByteOrder);
        if (maximumFrame != 0 && databasePageCount == 0)
        {
            throw new InvalidDataException(
                "SQLite WAL-index header publishes frames without a committed database page count.");
        }

        return new SqliteWalIndexHeader(
            ReadUInt32(source[8..], nativeByteOrder),
            walChecksumByteOrder,
            pageSize,
            maximumFrame,
            databasePageCount,
            ReadUInt32(source[24..], nativeByteOrder),
            ReadUInt32(source[28..], nativeByteOrder),
            BinaryPrimitives.ReadUInt32BigEndian(source[32..]),
            BinaryPrimitives.ReadUInt32BigEndian(source[36..]),
            checksum1,
            checksum2);
    }

    /// <summary>Serializes this header to a new exact-length native-endian buffer.</summary>
    public byte[] ToArray()
    {
        var destination = new byte[Size];
        WriteTo(destination);
        return destination;
    }

    /// <summary>
    /// Serializes this header to an exact-length native-endian destination.
    /// </summary>
    public void WriteTo(Span<byte> destination)
    {
        RequireExactLength(destination.Length, Size, "SQLite WAL-index header destination");
        if (MaximumFrame != 0 && DatabasePageCount == 0)
        {
            throw new InvalidOperationException(
                "SQLite WAL-index header cannot publish frames without a committed database page count.");
        }

        var encodedPageSize = PageSize == SqlitePageSize.Maximum
            ? (ushort)1
            : checked((ushort)PageSize);
        if (DecodePageSize(encodedPageSize) != PageSize)
            throw new InvalidOperationException("SQLite WAL-index header has an invalid page size.");

        WriteUInt32(destination, NativeByteOrder, CurrentFormatVersion);
        WriteUInt32(destination[4..], NativeByteOrder, value: 0);
        WriteUInt32(destination[8..], NativeByteOrder, ChangeCounter);
        destination[12] = 1;
        destination[13] = WalChecksumByteOrder switch
        {
            SqliteWalChecksumByteOrder.LittleEndian => 0,
            SqliteWalChecksumByteOrder.BigEndian => 1,
            _ => throw new InvalidOperationException("SQLite WAL-index header has an unsupported checksum byte order."),
        };
        WriteUInt16(
            destination[14..],
            NativeByteOrder,
            encodedPageSize);
        WriteUInt32(destination[16..], NativeByteOrder, MaximumFrame);
        WriteUInt32(destination[20..], NativeByteOrder, DatabasePageCount);
        WriteUInt32(destination[24..], NativeByteOrder, FrameChecksum1);
        WriteUInt32(destination[28..], NativeByteOrder, FrameChecksum2);
        BinaryPrimitives.WriteUInt32BigEndian(destination[32..], Salt1);
        BinaryPrimitives.WriteUInt32BigEndian(destination[36..], Salt2);

        var checksum = SqliteWalChecksum.Calculate(
            destination[..40],
            ToWalChecksumByteOrder(NativeByteOrder));
        if (checksum != (Checksum1, Checksum2))
            throw new InvalidOperationException("SQLite WAL-index header has stale checksum fields.");

        WriteUInt32(destination[40..], NativeByteOrder, Checksum1);
        WriteUInt32(destination[44..], NativeByteOrder, Checksum2);
    }

    /// <summary>
    /// Creates the next header after a writer has durably appended and committed
    /// frames to the associated WAL.
    /// </summary>
    public SqliteWalIndexHeader WithCommittedFrames(
        uint maximumFrame,
        uint databasePageCount,
        uint frameChecksum1,
        uint frameChecksum2)
    {
        if (maximumFrame <= MaximumFrame)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumFrame),
                maximumFrame,
                "A SQLite WAL writer must publish a committed frame beyond the prior boundary.");
        }
        if (databasePageCount == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(databasePageCount),
                "A SQLite WAL commit must publish a nonzero database page count.");
        }

        return Create(
            changeCounter: unchecked(ChangeCounter + 1),
            maximumFrame,
            databasePageCount,
            frameChecksum1,
            frameChecksum2);
    }

    /// <summary>
    /// Creates the empty-WAL header used only after a checkpointer holds the
    /// writer and every read-mark lock, and has durably installed the main store.
    /// </summary>
    public SqliteWalIndexHeader WithRestartedWal(uint databasePageCount)
        => WithRestartedWal(databasePageCount, Salt1, Salt2);

    internal SqliteWalIndexHeader WithRestartedWal(
        uint databasePageCount,
        uint salt1,
        uint salt2)
    {
        if (databasePageCount == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(databasePageCount),
                "A restarted SQLite WAL must retain a nonzero main-database page count.");
        }

        return SqliteWalIndexHeader.Create(
            changeCounter: unchecked(ChangeCounter + 1),
            WalChecksumByteOrder,
            PageSize,
            maximumFrame: 0,
            databasePageCount,
            frameChecksum1: 0,
            frameChecksum2: 0,
            salt1,
            salt2);
    }

    private SqliteWalIndexHeader Create(
        uint changeCounter,
        uint maximumFrame,
        uint databasePageCount,
        uint frameChecksum1,
        uint frameChecksum2)
        => Create(
            changeCounter,
            WalChecksumByteOrder,
            PageSize,
            maximumFrame,
            databasePageCount,
            frameChecksum1,
            frameChecksum2,
            Salt1,
            Salt2);

    internal static SqliteWalIndexHeader Create(
        uint changeCounter,
        SqliteWalChecksumByteOrder walChecksumByteOrder,
        int pageSize,
        uint maximumFrame,
        uint databasePageCount,
        uint frameChecksum1,
        uint frameChecksum2,
        uint salt1,
        uint salt2)
    {
        Span<byte> bytes = stackalloc byte[Size];
        WriteUInt32(bytes, NativeByteOrder, CurrentFormatVersion);
        WriteUInt32(bytes[4..], NativeByteOrder, value: 0);
        WriteUInt32(bytes[8..], NativeByteOrder, changeCounter);
        bytes[12] = 1;
        bytes[13] = walChecksumByteOrder == SqliteWalChecksumByteOrder.BigEndian ? (byte)1 : (byte)0;
        WriteUInt16(
            bytes[14..],
            NativeByteOrder,
            pageSize == SqlitePageSize.Maximum ? (ushort)1 : checked((ushort)pageSize));
        WriteUInt32(bytes[16..], NativeByteOrder, maximumFrame);
        WriteUInt32(bytes[20..], NativeByteOrder, databasePageCount);
        WriteUInt32(bytes[24..], NativeByteOrder, frameChecksum1);
        WriteUInt32(bytes[28..], NativeByteOrder, frameChecksum2);
        BinaryPrimitives.WriteUInt32BigEndian(bytes[32..], salt1);
        BinaryPrimitives.WriteUInt32BigEndian(bytes[36..], salt2);
        var checksum = SqliteWalChecksum.Calculate(
            bytes[..40],
            ToWalChecksumByteOrder(NativeByteOrder));
        WriteUInt32(bytes[40..], NativeByteOrder, checksum.First);
        WriteUInt32(bytes[44..], NativeByteOrder, checksum.Second);
        return Parse(bytes);
    }

    private static int DecodePageSize(ushort encodedPageSize)
    {
        if (encodedPageSize == 1)
            return SqlitePageSize.Maximum;
        if (encodedPageSize < SqlitePageSize.Minimum
            || encodedPageSize > 32 * 1024
            || (encodedPageSize & (encodedPageSize - 1)) != 0)
        {
            throw new InvalidDataException(
                "SQLite WAL-index page size must be 1 for 65536 bytes or a power of two from 512 through 32768 bytes.");
        }

        return encodedPageSize;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> source, SqliteWalIndexByteOrder byteOrder)
        => byteOrder switch
        {
            SqliteWalIndexByteOrder.LittleEndian => BinaryPrimitives.ReadUInt32LittleEndian(source),
            SqliteWalIndexByteOrder.BigEndian => BinaryPrimitives.ReadUInt32BigEndian(source),
            _ => throw new ArgumentOutOfRangeException(nameof(byteOrder), byteOrder, "Unsupported SQLite WAL-index byte order."),
        };

    private static ushort ReadUInt16(ReadOnlySpan<byte> source, SqliteWalIndexByteOrder byteOrder)
        => byteOrder switch
        {
            SqliteWalIndexByteOrder.LittleEndian => BinaryPrimitives.ReadUInt16LittleEndian(source),
            SqliteWalIndexByteOrder.BigEndian => BinaryPrimitives.ReadUInt16BigEndian(source),
            _ => throw new ArgumentOutOfRangeException(nameof(byteOrder), byteOrder, "Unsupported SQLite WAL-index byte order."),
        };

    private static void WriteUInt32(
        Span<byte> destination,
        SqliteWalIndexByteOrder byteOrder,
        uint value)
    {
        switch (byteOrder)
        {
            case SqliteWalIndexByteOrder.LittleEndian:
                BinaryPrimitives.WriteUInt32LittleEndian(destination, value);
                return;
            case SqliteWalIndexByteOrder.BigEndian:
                BinaryPrimitives.WriteUInt32BigEndian(destination, value);
                return;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(byteOrder),
                    byteOrder,
                    "Unsupported SQLite WAL-index byte order.");
        }
    }

    private static void WriteUInt16(
        Span<byte> destination,
        SqliteWalIndexByteOrder byteOrder,
        ushort value)
    {
        switch (byteOrder)
        {
            case SqliteWalIndexByteOrder.LittleEndian:
                BinaryPrimitives.WriteUInt16LittleEndian(destination, value);
                return;
            case SqliteWalIndexByteOrder.BigEndian:
                BinaryPrimitives.WriteUInt16BigEndian(destination, value);
                return;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(byteOrder),
                    byteOrder,
                    "Unsupported SQLite WAL-index byte order.");
        }
    }

    private static SqliteWalChecksumByteOrder ToWalChecksumByteOrder(SqliteWalIndexByteOrder byteOrder)
        => byteOrder == SqliteWalIndexByteOrder.LittleEndian
            ? SqliteWalChecksumByteOrder.LittleEndian
            : SqliteWalChecksumByteOrder.BigEndian;

    private static void ValidateByteOrder(SqliteWalIndexByteOrder byteOrder)
    {
        if (byteOrder is not SqliteWalIndexByteOrder.LittleEndian
            and not SqliteWalIndexByteOrder.BigEndian)
        {
            throw new ArgumentOutOfRangeException(
                nameof(byteOrder),
                byteOrder,
                "Unsupported SQLite WAL-index byte order.");
        }
    }

    private static void RequireExactLength(int actualLength, int expectedLength, string structure)
    {
        if (actualLength != expectedLength)
        {
            throw new InvalidDataException(
                $"{structure} must be exactly {expectedLength} bytes; found {actualLength} bytes.");
        }
    }
}

/// <summary>The checkpoint fields following SQLite's duplicated WAL-index headers.</summary>
public sealed record SqliteWalIndexCheckpointInfo(
    uint BackfilledFrameCount,
    uint ReadMark0,
    uint ReadMark1,
    uint ReadMark2,
    uint ReadMark3,
    uint ReadMark4,
    uint BackfillAttemptedFrameCount)
{
    /// <summary>Size in bytes of the checkpoint information and lock area.</summary>
    public const int Size = 40;

    /// <summary>Number of SQLite WAL read-mark slots.</summary>
    public const int ReadMarkCount = 5;

    /// <summary>Value SQLite uses for an unclaimed read-mark slot.</summary>
    public const uint ReadMarkNotUsed = uint.MaxValue;

    /// <summary>Offset of SQLite's eight lock bytes within the complete header region.</summary>
    public const int LockOffset = 120;

    /// <summary>Returns the read-mark value for a SQLite reader slot.</summary>
    public uint GetReadMark(int readMarkIndex)
        => readMarkIndex switch
        {
            0 => ReadMark0,
            1 => ReadMark1,
            2 => ReadMark2,
            3 => ReadMark3,
            4 => ReadMark4,
            _ => throw new ArgumentOutOfRangeException(
                nameof(readMarkIndex),
                readMarkIndex,
                $"SQLite WAL read-mark index must be between zero and {ReadMarkCount - 1}."),
        };

    internal static SqliteWalIndexCheckpointInfo Parse(
        ReadOnlySpan<byte> source,
        uint maximumFrame,
        SqliteWalIndexByteOrder nativeByteOrder)
    {
        if (source.Length != Size)
        {
            throw new InvalidDataException(
                $"SQLite WAL-index checkpoint information must be exactly {Size} bytes; found {source.Length} bytes.");
        }

        var backfilledFrameCount = ReadUInt32(source, nativeByteOrder);
        var backfillAttemptedFrameCount = ReadUInt32(source[32..], nativeByteOrder);
        if (backfilledFrameCount > maximumFrame || backfillAttemptedFrameCount > maximumFrame)
        {
            throw new InvalidDataException(
                "SQLite WAL-index checkpoint information refers to frames beyond the committed WAL boundary.");
        }

        return new SqliteWalIndexCheckpointInfo(
            backfilledFrameCount,
            ReadUInt32(source[4..], nativeByteOrder),
            ReadUInt32(source[8..], nativeByteOrder),
            ReadUInt32(source[12..], nativeByteOrder),
            ReadUInt32(source[16..], nativeByteOrder),
            ReadUInt32(source[20..], nativeByteOrder),
            backfillAttemptedFrameCount);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> source, SqliteWalIndexByteOrder byteOrder)
        => byteOrder == SqliteWalIndexByteOrder.LittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(source)
            : BinaryPrimitives.ReadUInt32BigEndian(source);
}

/// <summary>
/// A validated, internally consistent snapshot of SQLite's 136-byte WAL-index
/// header region.
/// </summary>
/// <remarks>
/// The parser requires both header copies to be valid and identical. A mismatch
/// may be a writer's in-progress second-copy/first-copy publication, a stale
/// mapping, or corruption. Without a mapped retry protocol and shared-memory
/// barrier, accepting either copy could expose an uncommitted frame.
/// </remarks>
public sealed record SqliteWalIndexHeaderRegion(
    SqliteWalIndexHeader Header,
    SqliteWalIndexCheckpointInfo CheckpointInfo)
{
    /// <summary>Parses the header region from a SQLite WAL-index snapshot.</summary>
    public static SqliteWalIndexHeaderRegion Parse(ReadOnlySpan<byte> source)
        => Parse(source, SqliteWalIndexHeader.NativeByteOrder);

    /// <summary>Parses the header region using an explicit native byte order.</summary>
    public static SqliteWalIndexHeaderRegion Parse(
        ReadOnlySpan<byte> source,
        SqliteWalIndexByteOrder nativeByteOrder)
    {
        if (source.Length < SqliteWalIndexLayout.HeaderRegionSize)
        {
            throw new InvalidDataException(
                $"SQLite WAL-index header region must contain at least {SqliteWalIndexLayout.HeaderRegionSize} bytes; found {source.Length} bytes.");
        }

        var firstHeader = SqliteWalIndexHeader.Parse(source[..SqliteWalIndexHeader.Size], nativeByteOrder);
        var secondHeader = SqliteWalIndexHeader.Parse(
            source.Slice(SqliteWalIndexHeader.Size, SqliteWalIndexHeader.Size),
            nativeByteOrder);
        if (firstHeader != secondHeader)
        {
            throw new InvalidDataException(
                "SQLite WAL-index header copies differ; refusing an in-progress, stale, or corrupt publication.");
        }

        var checkpointInfo = SqliteWalIndexCheckpointInfo.Parse(
            source.Slice(SqliteWalIndexHeader.Size * 2, SqliteWalIndexCheckpointInfo.Size),
            firstHeader.MaximumFrame,
            nativeByteOrder);
        return new SqliteWalIndexHeaderRegion(firstHeader, checkpointInfo);
    }
}

/// <summary>
/// Reads and publishes SQLite WAL-index headers and resolves page numbers through
/// the transient native-endian hash tables.
/// </summary>
/// <remarks>
/// This is deliberately detached from <see cref="SqlitePager"/>. Callers must
/// provide any SQLite role lock required for their operation; the instance lock
/// only serializes operations issued through this instance. A valid result is
/// authenticated against the WAL file and never authorizes pager behavior.
/// </remarks>
public sealed class SqliteWalIndexSharedMemory
{
    private const int StableHeaderReadAttempts = 8;
    private const uint HashMultiplier = 383;

    private readonly object _gate = new();
    private readonly ISqliteWalSharedMemoryMapping _mapping;

    /// <summary>Creates an accessor over an already mapped SQLite <c>-shm</c> region.</summary>
    public SqliteWalIndexSharedMemory(ISqliteWalSharedMemoryMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        _mapping = mapping;
    }

    internal SqliteWalSharedMemoryCarrierIdentity? CarrierIdentity
        => (_mapping as ISqliteWalSharedMemoryCarrierIdentity)?.CarrierIdentity;

    internal ISqliteWalSharedMemoryLockCarrier? LockCarrier
        => _mapping as ISqliteWalSharedMemoryLockCarrier;

    /// <summary>
    /// Reads a stable dual-header snapshot and validates it against the WAL.
    /// </summary>
    public SqliteWalIndexHeaderRegion ReadValidatedHeader(SqliteWalFile wal)
    {
        ArgumentNullException.ThrowIfNull(wal);
        lock (_gate)
        {
            var region = ReadStableHeaderRegion();
            ValidateHeaderAgainstWal(region.Header, wal);
            return region;
        }
    }

    /// <summary>
    /// Reads a stable, checksum-valid WAL-index header region without trusting it
    /// as a WAL snapshot. Recovery uses this only while it owns the recovery and
    /// all reader locks, before truncating an uncommitted tail.
    /// </summary>
    public SqliteWalIndexHeaderRegion ReadStableHeader()
    {
        lock (_gate)
            return ReadStableHeaderRegion();
    }

    /// <summary>
    /// Reads either checksum-valid WAL-index header copy for recovery only. A
    /// caller must first own writer, recovery, checkpoint, and every read mark;
    /// normal snapshot paths must use <see cref="ReadValidatedHeader"/>.
    /// </summary>
    public SqliteWalIndexHeader ReadRecoverableHeader()
    {
        lock (_gate)
        {
            EnsureMappedBlocks(blockCount: 1);
            Span<byte> first = stackalloc byte[SqliteWalIndexHeader.Size];
            Span<byte> second = stackalloc byte[SqliteWalIndexHeader.Size];
            _mapping.Read(position: 0, first);
            _mapping.MemoryBarrier();
            _mapping.Read(SqliteWalIndexHeader.Size, second);

            SqliteWalIndexHeader? firstHeader = null;
            SqliteWalIndexHeader? secondHeader = null;
            try
            {
                firstHeader = SqliteWalIndexHeader.Parse(first);
            }
            catch (InvalidDataException)
            {
            }
            try
            {
                secondHeader = SqliteWalIndexHeader.Parse(second);
            }
            catch (InvalidDataException)
            {
            }

            if (firstHeader is null && secondHeader is null)
            {
                throw new InvalidDataException(
                    "Neither SQLite WAL-index header copy is valid enough to recover the WAL index.");
            }
            if (firstHeader is not null && secondHeader is not null)
            {
                if (firstHeader == secondHeader)
                    return firstHeader;
                throw new InvalidDataException(
                    "SQLite WAL-index header copies disagree; recovery cannot select trustworthy tail evidence.");
            }

            return firstHeader ?? secondHeader!;
        }
    }

    /// <summary>
    /// Publishes a validated WAL-index header using SQLite's second-copy,
    /// barrier, first-copy ordering.
    /// </summary>
    public void PublishHeader(SqliteWalIndexHeader header, SqliteWalFile wal)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(wal);

        lock (_gate)
        {
            if (_mapping.IsReadOnly)
                throw new InvalidOperationException("Cannot publish a SQLite WAL-index header through a read-only mapping.");

            EnsureMappedBlocks(SqliteWalIndexLayout.GetRequiredBlockCount(header.MaximumFrame));
            ValidateHeaderAgainstWal(header, wal);

            var bytes = header.ToArray();
            _mapping.Write(SqliteWalIndexHeader.Size, bytes);
            _mapping.MemoryBarrier();
            _mapping.Write(position: 0, bytes);
        }
    }

    /// <summary>
    /// Publishes one nonzero WAL read mark while its caller holds that mark's
    /// exclusive SQLite byte-range lock.
    /// </summary>
    /// <remarks>
    /// Read mark zero is a placeholder for database-only readers and must never
    /// be written. This method deliberately does not acquire a role lock: the
    /// caller owns the cross-process protocol and must downgrade to a shared
    /// lock before exposing the selected boundary to a reader.
    /// </remarks>
    public void PublishReadMark(int readMarkIndex, uint maximumFrame)
    {
        if (readMarkIndex <= 0 || readMarkIndex >= SqliteWalIndexCheckpointInfo.ReadMarkCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(readMarkIndex),
                readMarkIndex,
                $"SQLite WAL writable read-mark indexes must be between one and {SqliteWalIndexCheckpointInfo.ReadMarkCount - 1}.");
        }
        if (maximumFrame == 0 || maximumFrame == SqliteWalIndexCheckpointInfo.ReadMarkNotUsed)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumFrame),
                maximumFrame,
                "SQLite WAL read marks must name a nonzero committed frame.");
        }

        lock (_gate)
        {
            if (_mapping.IsReadOnly)
                throw new InvalidOperationException("Cannot publish a SQLite WAL read mark through a read-only mapping.");

            EnsureMappedBlocks(blockCount: 1);
            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            WriteUInt32(bytes, maximumFrame);
            _mapping.Write(
                SqliteWalIndexHeader.Size * 2L + sizeof(uint) + readMarkIndex * sizeof(uint),
                bytes);
            _mapping.MemoryBarrier();
        }
    }

    /// <summary>
    /// Publishes the page-number and hash entries for frames that were already
    /// durably appended, then makes their committed boundary visible by publishing
    /// the supplied header. The caller must own <c>WAL_WRITE_LOCK</c>.
    /// </summary>
    public void PublishCommittedFrames(
        SqliteWalIndexHeader priorHeader,
        IReadOnlyList<SqliteWalFrame> frames,
        SqliteWalIndexHeader committedHeader,
        SqliteWalFile wal)
    {
        ArgumentNullException.ThrowIfNull(priorHeader);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(committedHeader);
        ArgumentNullException.ThrowIfNull(wal);
        if (frames.Count == 0)
            throw new ArgumentException("A SQLite WAL commit must contain at least one frame.", nameof(frames));
        if (priorHeader.PageSize != committedHeader.PageSize
            || priorHeader.WalChecksumByteOrder != committedHeader.WalChecksumByteOrder
            || priorHeader.Salt1 != committedHeader.Salt1
            || priorHeader.Salt2 != committedHeader.Salt2)
        {
            throw new InvalidOperationException(
                "A SQLite WAL commit cannot change its page size, checksum order, or WAL incarnation.");
        }
        if (committedHeader.MaximumFrame != checked(priorHeader.MaximumFrame + (uint)frames.Count))
        {
            throw new InvalidOperationException(
                "SQLite WAL frame publication must advance the committed boundary by exactly the appended frame count.");
        }

        lock (_gate)
        {
            if (_mapping.IsReadOnly)
                throw new InvalidOperationException("Cannot publish SQLite WAL frames through a read-only mapping.");

            EnsureWritableBlocks(SqliteWalIndexLayout.GetRequiredBlockCount(committedHeader.MaximumFrame));
            var recovery = wal.ScanRecovery();
            if (recovery.StopReason != SqliteWalRecoveryStopReason.EndOfFile
                || recovery.LastCommittedFrameNumber != committedHeader.MaximumFrame
                || recovery.LastCommittedDatabaseSizeInPages != committedHeader.DatabasePageCount)
            {
                throw new InvalidDataException(
                    "The SQLite WAL does not contain exactly the committed frame boundary being published.");
            }

            // One chain walk for the published run: ReadFrame revalidates the
            // whole prefix per call, making per-frame verification O(P^2).
            var expectedFrames = wal.ReadFrameRange(
                checked(priorHeader.MaximumFrame + 1U),
                committedHeader.MaximumFrame);
            for (var index = 0; index < frames.Count; index++)
            {
                var frameNumber = checked(priorHeader.MaximumFrame + (uint)index + 1);
                var frame = frames[index]
                    ?? throw new ArgumentException("SQLite WAL frame collections cannot contain null frames.", nameof(frames));
                var expected = expectedFrames[index];
                if (frame.Header != expected.Header || !frame.PageData.AsSpan().SequenceEqual(expected.PageData))
                {
                    throw new InvalidDataException(
                        $"SQLite WAL frame {frameNumber} changed before its WAL-index entry could be published.");
                }

                PublishFrameIndex(frameNumber, frame.Header.PageNumber);
            }

            // The frame/hash arrays must be globally visible before either header
            // copy exposes their new committed boundary.
            _mapping.MemoryBarrier();
            PublishHeader(committedHeader, wal);
        }
    }

    /// <summary>
    /// Confirms the selected checkpoint boundary still describes the live WAL
    /// incarnation (SHM salts plus the durable on-disk WAL header). Returns
    /// <see langword="false"/> when a concurrent writer has wrapped or reset the
    /// WAL — the SQLite <c>walRestartLog</c> race fixed in 3.51.3.
    /// </summary>
    /// <remarks>
    /// Call this after exclusive ownership of read-mark 0 (or immediately before
    /// install/backfill publication) and soft-skip the checkpoint when it fails.
    /// <paramref name="liveRegion"/> always receives the latest stable SHM view.
    /// </remarks>
    public bool TryConfirmCheckpointIncarnation(
        SqliteWalIndexHeader selectedHeader,
        SqliteWalFile wal,
        out SqliteWalIndexHeaderRegion liveRegion)
    {
        ArgumentNullException.ThrowIfNull(selectedHeader);
        ArgumentNullException.ThrowIfNull(wal);
        lock (_gate)
        {
            liveRegion = ReadStableHeaderRegion();
            if (!HasMatchingWalIncarnation(selectedHeader, liveRegion.Header))
                return false;

            var durableWalHeader = wal.ReadDurableHeader();
            return HasMatchingWalFileIncarnation(selectedHeader, durableWalHeader);
        }
    }

    /// <summary>
    /// Records how far the active checkpointer attempted to backfill. Callers
    /// must own <c>WAL_CKPT_LOCK</c>, provide an independently authenticated
    /// selected boundary, and must not move the value backwards.
    /// </summary>
    public void PublishBackfillAttemptedFrameCount(
        SqliteWalIndexHeader selectedHeader,
        uint attemptedFrameCount,
        SqliteWalFile wal)
    {
        ArgumentNullException.ThrowIfNull(selectedHeader);
        ArgumentNullException.ThrowIfNull(wal);
        lock (_gate)
        {
            var region = ReadCheckpointPublicationHeaderRegion(selectedHeader);
            ValidateCheckpointPublicationHeader(selectedHeader, region.Header, wal);
            if (attemptedFrameCount < region.CheckpointInfo.BackfilledFrameCount
                || attemptedFrameCount < region.CheckpointInfo.BackfillAttemptedFrameCount
                || attemptedFrameCount > selectedHeader.MaximumFrame)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(attemptedFrameCount),
                    "SQLite WAL backfill-attempt progress must be monotonic and within the selected committed boundary.");
            }

            WriteUInt32(
                SqliteWalIndexHeader.Size * 2L + 32,
                attemptedFrameCount);
            _mapping.MemoryBarrier();
        }
    }

    /// <summary>
    /// Publishes durable main-store backfill progress. The caller must own
    /// <c>WAL_CKPT_LOCK</c> and must call this only after flushing the installed
    /// database pages to durable storage. The selected boundary must have been
    /// independently authenticated before copying pages.
    /// </summary>
    public void PublishBackfilledFrameCount(
        SqliteWalIndexHeader selectedHeader,
        uint backfilledFrameCount,
        SqliteWalFile wal)
    {
        ArgumentNullException.ThrowIfNull(selectedHeader);
        ArgumentNullException.ThrowIfNull(wal);
        lock (_gate)
        {
            var region = ReadCheckpointPublicationHeaderRegion(selectedHeader);
            ValidateCheckpointPublicationHeader(selectedHeader, region.Header, wal);
            if (backfilledFrameCount < region.CheckpointInfo.BackfilledFrameCount
                || backfilledFrameCount > region.CheckpointInfo.BackfillAttemptedFrameCount
                || backfilledFrameCount > selectedHeader.MaximumFrame)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(backfilledFrameCount),
                    "SQLite WAL durable backfill progress must be monotonic and no later than the selected attempted boundary.");
            }

            WriteUInt32(SqliteWalIndexHeader.Size * 2L, backfilledFrameCount);
            _mapping.MemoryBarrier();
        }
    }

    /// <summary>
    /// Clears frame lookup state and checkpoint accounting after a fully exclusive
    /// restart or truncate. The caller must own writer, checkpoint, and all
    /// read-mark locks, and must already have durably reset the WAL.
    /// </summary>
    public void ResetAfterDurableRestart(SqliteWalIndexHeader restartedHeader)
    {
        ArgumentNullException.ThrowIfNull(restartedHeader);
        if (restartedHeader.MaximumFrame != 0)
        {
            throw new ArgumentException(
                "A SQLite WAL restart header must publish no committed frames.",
                nameof(restartedHeader));
        }

        lock (_gate)
        {
            if (_mapping.IsReadOnly)
                throw new InvalidOperationException("Cannot reset SQLite WAL-index state through a read-only mapping.");

            EnsureWritableBlocks(blockCount: 1);
            ClearFrameIndex();
            PublishResetCheckpointInfo();
            _mapping.MemoryBarrier();
            PublishHeaderWithoutWalValidation(restartedHeader);
        }
    }

    /// <summary>
    /// Rebuilds every frame/hash entry and both header copies from a clean,
    /// independently scanned WAL. Callers must hold writer, recovery, checkpoint,
    /// and all read-mark locks before invoking this crash-recovery operation.
    /// </summary>
    public void RebuildFromWal(SqliteWalFile wal, uint mainDatabasePageCount)
    {
        ArgumentNullException.ThrowIfNull(wal);
        if (mainDatabasePageCount == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mainDatabasePageCount),
                "SQLite WAL-index recovery requires a nonzero main-database page count.");
        }

        lock (_gate)
        {
            if (_mapping.IsReadOnly)
                throw new InvalidOperationException("Cannot rebuild SQLite WAL-index state through a read-only mapping.");

            var recovery = wal.ScanRecovery();
            if (recovery.StopReason != SqliteWalRecoveryStopReason.EndOfFile
                || recovery.LastValidFrameNumber != recovery.LastCommittedFrameNumber
                || recovery.LastCommittedFrameNumber > uint.MaxValue)
            {
                throw new InvalidDataException(
                    "SQLite WAL-index recovery requires a complete WAL ending at its last committed frame.");
            }

            var maximumFrame = checked((uint)recovery.LastCommittedFrameNumber);
            // Stage 5: recovery always advances iChange so peer caches cannot keep
            // serving pre-recovery pages against a rebuilt index.
            var changeCounter = ResolveRecoveryChangeCounter();
            var header = maximumFrame == 0
                ? SqliteWalIndexHeader.Create(
                    changeCounter,
                    wal.Header.ChecksumByteOrder,
                    wal.PageSize,
                    maximumFrame: 0,
                    mainDatabasePageCount,
                    frameChecksum1: 0,
                    frameChecksum2: 0,
                    wal.Header.Salt1,
                    wal.Header.Salt2)
                : CreateHeaderFromCommittedWal(
                    wal,
                    maximumFrame,
                    recovery.LastCommittedDatabaseSizeInPages,
                    changeCounter);

            EnsureWritableBlocks(SqliteWalIndexLayout.GetRequiredBlockCount(maximumFrame));
            ClearFrameIndex();
            PublishResetCheckpointInfo();
            if (maximumFrame != 0)
            {
                var frameNumber = 0U;
                foreach (var frame in wal.ReadFrameRange(1, maximumFrame))
                    PublishFrameIndex(++frameNumber, frame.Header.PageNumber);
            }

            _mapping.MemoryBarrier();
            PublishHeaderWithoutWalValidation(header);
        }
    }

    private uint ResolveRecoveryChangeCounter()
    {
        try
        {
            EnsureMappedBlocks(blockCount: 1);
            var prior = ReadStableHeaderRegion().Header.ChangeCounter;
            return unchecked(prior + 1);
        }
        catch (InvalidDataException)
        {
            return 1;
        }
        catch (ArgumentOutOfRangeException)
        {
            // Zero-length carriers grow during EnsureWritableBlocks; first recovery
            // still starts at iChange=1.
            return 1;
        }
    }

    /// <summary>
    /// Resolves the newest frame for <paramref name="pageNumber"/> within the
    /// currently validated committed WAL boundary, or returns <see langword="null"/>.
    /// </summary>
    public uint? FindFrame(SqliteWalFile wal, uint pageNumber)
    {
        ArgumentNullException.ThrowIfNull(wal);
        if (pageNumber == 0)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), "SQLite database page numbers start at one.");

        lock (_gate)
        {
            for (var attempt = 0; attempt < StableHeaderReadAttempts; attempt++)
            {
                var region = ReadStableHeaderRegion();
                ValidateHeaderAgainstWal(region.Header, wal);
                var frameNumber = FindFrame(region.Header, pageNumber);
                if (frameNumber is { } frame)
                    ValidateMatchedFrame(wal, region.Header, frame, pageNumber);

                var confirmation = ReadStableHeaderRegion();
                if (region.Header == confirmation.Header)
                    return frameNumber;
            }
        }

        throw new InvalidDataException(
            "SQLite WAL-index header changed while resolving a page number; refusing a stale lookup.");
    }

    /// <summary>
    /// Reads a stable dual-copy SHM header region without authenticating it
    /// against a WAL file. Used after an incarnation race when the open WAL
    /// handle may still cache the pre-wrap header.
    /// </summary>
    public SqliteWalIndexHeaderRegion ReadStableHeaderRegion()
    {
        EnsureMappedBlocks(blockCount: 1);
        InvalidDataException? failure = null;
        for (var attempt = 0; attempt < StableHeaderReadAttempts; attempt++)
        {
            try
            {
                Span<byte> source = stackalloc byte[SqliteWalIndexLayout.HeaderRegionSize];
                ReadHeaderRegion(source);
                return SqliteWalIndexHeaderRegion.Parse(source);
            }
            catch (InvalidDataException exception)
            {
                failure = exception;
            }
        }

        throw new InvalidDataException(
            $"SQLite WAL-index header remained malformed or torn after {StableHeaderReadAttempts} stable-read attempts.",
            failure);
    }

    private SqliteWalIndexHeaderRegion ReadCheckpointPublicationHeaderRegion(
        SqliteWalIndexHeader selectedHeader)
    {
        EnsureMappedBlocks(blockCount: 1);
        InvalidDataException? failure = null;
        for (var attempt = 0; attempt < StableHeaderReadAttempts; attempt++)
        {
            try
            {
                Span<byte> source = stackalloc byte[SqliteWalIndexLayout.HeaderRegionSize];
                ReadHeaderRegion(source);
                var firstHeader = SqliteWalIndexHeader.Parse(source[..SqliteWalIndexHeader.Size]);
                var secondHeader = SqliteWalIndexHeader.Parse(
                    source.Slice(SqliteWalIndexHeader.Size, SqliteWalIndexHeader.Size));
                if (firstHeader == secondHeader)
                    return SqliteWalIndexHeaderRegion.Parse(source);

                if (firstHeader.MaximumFrame == secondHeader.MaximumFrame
                    || !HasMatchingWalIncarnation(selectedHeader, firstHeader)
                    || !HasMatchingWalIncarnation(selectedHeader, secondHeader)
                    || firstHeader.MaximumFrame < selectedHeader.MaximumFrame
                    || secondHeader.MaximumFrame < selectedHeader.MaximumFrame)
                {
                    throw new InvalidDataException(
                        "SQLite WAL-index header copies changed incompatibly while checkpoint progress was being published.");
                }

                var currentHeader = firstHeader.MaximumFrame > secondHeader.MaximumFrame
                    ? firstHeader
                    : secondHeader;
                var checkpointInfo = SqliteWalIndexCheckpointInfo.Parse(
                    source.Slice(SqliteWalIndexHeader.Size * 2, SqliteWalIndexCheckpointInfo.Size),
                    currentHeader.MaximumFrame,
                    SqliteWalIndexHeader.NativeByteOrder);
                return new SqliteWalIndexHeaderRegion(currentHeader, checkpointInfo);
            }
            catch (InvalidDataException exception)
            {
                failure = exception;
            }
        }

        throw new InvalidDataException(
            $"SQLite WAL-index header remained malformed, torn, or incompatible after {StableHeaderReadAttempts} checkpoint-publication attempts.",
            failure);
    }

    private void ReadHeaderRegion(Span<byte> destination)
    {
        _mapping.Read(position: 0, destination[..SqliteWalIndexHeader.Size]);
        _mapping.MemoryBarrier();
        _mapping.Read(
            SqliteWalIndexHeader.Size,
            destination.Slice(SqliteWalIndexHeader.Size, SqliteWalIndexHeader.Size));
        _mapping.Read(
            SqliteWalIndexHeader.Size * 2,
            destination[(SqliteWalIndexHeader.Size * 2)..]);
    }

    private uint? FindFrame(SqliteWalIndexHeader header, uint pageNumber)
    {
        if (header.MaximumFrame == 0)
            return null;

        var blockIndex = SqliteWalIndexLayout.GetBlockIndex(header.MaximumFrame);
        EnsureMappedBlocks(checked(blockIndex + 1));
        for (; blockIndex >= 0; blockIndex--)
        {
            var frameZero = GetBlockFrameZero(blockIndex);
            var frameCapacity = GetBlockFrameCapacity(blockIndex);
            var hashSlot = (int)(unchecked(pageNumber * HashMultiplier)
                                 & (SqliteWalIndexLayout.HashSlotCount - 1));
            uint? result = null;

            for (var probe = 0; probe < SqliteWalIndexLayout.HashSlotCount; probe++)
            {
                var hashValue = ReadUInt16(
                    SqliteWalIndexLayout.GetHashSlotOffset(blockIndex, hashSlot));
                if (hashValue == 0)
                    break;
                if (hashValue > frameCapacity)
                {
                    throw new InvalidDataException(
                        $"SQLite WAL-index hash slot {hashSlot} in block {blockIndex} refers to page-number slot {hashValue}, outside the block.");
                }

                var frameNumber = checked(frameZero + hashValue);
                if (frameNumber <= header.MaximumFrame)
                {
                    var indexedPageNumber = ReadUInt32(
                        SqliteWalIndexLayout.GetPageNumberOffset(frameNumber));
                    if (indexedPageNumber == 0)
                    {
                        throw new InvalidDataException(
                            $"SQLite WAL-index page-number slot for frame {frameNumber} is zero within the committed boundary.");
                    }
                    if (indexedPageNumber == pageNumber
                        && (result is null || frameNumber > result.Value))
                    {
                        result = frameNumber;
                    }
                }

                hashSlot = (hashSlot + 1) & (SqliteWalIndexLayout.HashSlotCount - 1);
            }

            if (result is { })
                return result;
        }

        return null;
    }

    private void EnsureMappedBlocks(int blockCount)
    {
        if (blockCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(blockCount), "SQLite WAL-index block count must be positive.");

        var requiredLength = checked((long)blockCount * SqliteWalIndexLayout.BlockSize);
        if (_mapping.Length < requiredLength)
        {
            throw new InvalidDataException(
                $"SQLite WAL-index mapping is {_mapping.Length} bytes but requires at least {requiredLength} bytes.");
        }
    }

    private static SqliteWalIndexHeader CreateHeaderFromCommittedWal(
        SqliteWalFile wal,
        uint maximumFrame,
        uint databasePageCount,
        uint changeCounter = 1)
    {
        var committedFrame = wal.ReadFrame(maximumFrame).Header;
        if (!committedFrame.IsCommit || committedFrame.DatabaseSizeInPages != databasePageCount)
        {
            throw new InvalidDataException(
                "SQLite WAL-index recovery found a non-commit frame at its recovered committed boundary.");
        }

        return SqliteWalIndexHeader.Create(
            changeCounter,
            wal.Header.ChecksumByteOrder,
            wal.PageSize,
            maximumFrame,
            databasePageCount,
            committedFrame.Checksum1,
            committedFrame.Checksum2,
            wal.Header.Salt1,
            wal.Header.Salt2);
    }

    private void EnsureWritableBlocks(int blockCount)
    {
        if (blockCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(blockCount), "SQLite WAL-index block count must be positive.");

        // Grow first: a freshly created lock-carrier -shm is zero-length until the
        // pager (or SQLite) publishes the first WAL-index region.
        var requiredLength = checked((long)blockCount * SqliteWalIndexLayout.BlockSize);
        if (_mapping.Length < requiredLength)
        {
            _mapping.Write(requiredLength - 1, stackalloc byte[1]);
            if (_mapping.Length < requiredLength)
            {
                throw new InvalidDataException(
                    $"SQLite WAL-index mapping did not grow to its required {requiredLength} bytes.");
            }
        }

        EnsureMappedBlocks(blockCount);
    }

    private void PublishFrameIndex(uint frameNumber, uint pageNumber)
    {
        var blockIndex = SqliteWalIndexLayout.GetBlockIndex(frameNumber);
        var frameZero = GetBlockFrameZero(blockIndex);
        var frameSlot = checked(frameNumber - frameZero);
        var frameCapacity = GetBlockFrameCapacity(blockIndex);
        if (frameSlot == 0 || frameSlot > frameCapacity)
            throw new InvalidOperationException("SQLite WAL frame resolved outside its WAL-index block.");

        WriteUInt32(SqliteWalIndexLayout.GetPageNumberOffset(frameNumber), pageNumber);
        var hashSlot = (int)(unchecked(pageNumber * HashMultiplier)
                             & (SqliteWalIndexLayout.HashSlotCount - 1));
        for (var probe = 0; probe < SqliteWalIndexLayout.HashSlotCount; probe++)
        {
            if (ReadUInt16(SqliteWalIndexLayout.GetHashSlotOffset(blockIndex, hashSlot)) == 0)
            {
                WriteUInt16(
                    SqliteWalIndexLayout.GetHashSlotOffset(blockIndex, hashSlot),
                    checked((ushort)frameSlot));
                return;
            }

            hashSlot = (hashSlot + 1) & (SqliteWalIndexLayout.HashSlotCount - 1);
        }

        throw new InvalidDataException(
            $"SQLite WAL-index hash table for block {blockIndex} has no free slot for page {pageNumber}.");
    }

    private void ClearFrameIndex()
    {
        var length = _mapping.Length;
        if (length <= SqliteWalIndexLayout.HeaderRegionSize)
            return;

        var cleared = new byte[Math.Min(SqliteWalIndexLayout.BlockSize, checked((int)(length - SqliteWalIndexLayout.HeaderRegionSize)))];
        for (var position = (long)SqliteWalIndexLayout.HeaderRegionSize; position < length;)
        {
            var count = (int)Math.Min(cleared.Length, length - position);
            _mapping.Write(position, cleared.AsSpan(0, count));
            position += count;
        }
    }

    private void PublishResetCheckpointInfo()
    {
        Span<byte> readMarks = stackalloc byte[24];
        readMarks.Clear();
        WriteUInt32(readMarks, 0);
        WriteUInt32(readMarks.Slice(sizeof(uint), sizeof(uint)), 0);
        for (var index = 1; index < SqliteWalIndexCheckpointInfo.ReadMarkCount; index++)
        {
            WriteUInt32(
                readMarks.Slice((index + 1) * sizeof(uint), sizeof(uint)),
                SqliteWalIndexCheckpointInfo.ReadMarkNotUsed);
        }
        _mapping.Write(SqliteWalIndexHeader.Size * 2L, readMarks);

        Span<byte> tail = stackalloc byte[8];
        tail.Clear();
        _mapping.Write(SqliteWalIndexHeader.Size * 2L + 32, tail);
    }

    private void PublishHeaderWithoutWalValidation(SqliteWalIndexHeader header)
    {
        EnsureWritableBlocks(SqliteWalIndexLayout.GetRequiredBlockCount(header.MaximumFrame));
        var bytes = header.ToArray();
        _mapping.Write(SqliteWalIndexHeader.Size, bytes);
        _mapping.MemoryBarrier();
        _mapping.Write(position: 0, bytes);
    }

    private static void ValidateHeaderAgainstWal(SqliteWalIndexHeader header, SqliteWalFile wal)
    {
        var walHeader = wal.Header;
        if (header.PageSize != walHeader.PageSize)
            throw new InvalidDataException("SQLite WAL-index page size does not match the WAL header.");
        if (header.WalChecksumByteOrder != walHeader.ChecksumByteOrder)
            throw new InvalidDataException("SQLite WAL-index checksum byte order does not match the WAL header.");
        if (header.Salt1 != walHeader.Salt1 || header.Salt2 != walHeader.Salt2)
            throw new InvalidDataException("SQLite WAL-index salts do not match the WAL header.");

        var recovery = wal.ScanRecovery();
        if (recovery.StopReason != SqliteWalRecoveryStopReason.EndOfFile)
        {
            throw new InvalidDataException(
                $"SQLite WAL contains a {recovery.StopReason} tail; refusing to trust its WAL-index.");
        }
        if (recovery.LastValidFrameNumber != recovery.LastCommittedFrameNumber)
        {
            throw new InvalidDataException(
                "SQLite WAL contains valid frames after its last commit; refusing to trust an uncommitted tail.");
        }
        if (recovery.LastCommittedFrameNumber != header.MaximumFrame)
        {
            throw new InvalidDataException(
                "SQLite WAL-index committed-frame boundary does not match the independently validated WAL.");
        }

        if (header.MaximumFrame == 0)
            return;

        if (recovery.LastCommittedDatabaseSizeInPages != header.DatabasePageCount)
        {
            throw new InvalidDataException(
                "SQLite WAL-index database page count does not match the independently validated WAL.");
        }

        var committedFrame = wal.ReadFrame(header.MaximumFrame);
        if (!committedFrame.Header.IsCommit)
            throw new InvalidDataException("SQLite WAL-index maximum frame is not a WAL commit frame.");
        if (committedFrame.Header.DatabaseSizeInPages != header.DatabasePageCount)
        {
            throw new InvalidDataException(
                "SQLite WAL-index database page count does not match its maximum WAL frame.");
        }
        if (committedFrame.Header.Checksum1 != header.FrameChecksum1
            || committedFrame.Header.Checksum2 != header.FrameChecksum2)
        {
            throw new InvalidDataException(
                "SQLite WAL-index frame checksum does not match its maximum WAL frame.");
        }
    }

    private static void ValidateMatchedFrame(
        SqliteWalFile wal,
        SqliteWalIndexHeader header,
        uint frameNumber,
        uint pageNumber)
    {
        var frame = wal.ReadFrame(frameNumber);
        if (frame.Header.PageNumber != pageNumber)
        {
            throw new InvalidDataException(
                $"SQLite WAL-index frame {frameNumber} maps page {pageNumber} but the WAL frame stores page {frame.Header.PageNumber}.");
        }

        if (frame.Header.Salt1 != header.Salt1 || frame.Header.Salt2 != header.Salt2)
            throw new InvalidDataException("SQLite WAL-index lookup frame salts do not match its header.");
    }

    private static void ValidateCheckpointPublicationHeader(
        SqliteWalIndexHeader selectedHeader,
        SqliteWalIndexHeader currentHeader,
        SqliteWalFile wal)
    {
        if (!HasMatchingWalIncarnation(selectedHeader, currentHeader))
            throw new SqliteWalIncarnationChangedException();
        if (currentHeader.MaximumFrame < selectedHeader.MaximumFrame)
        {
            throw new InvalidDataException(
                "SQLite WAL committed boundary moved backwards while checkpoint progress was being published.");
        }

        // Prefer the durable on-disk header so a peer wrap is visible even when
        // this connection still caches the pre-wrap WAL header.
        var durableWalHeader = wal.ReadDurableHeader();
        if (!HasMatchingWalFileIncarnation(selectedHeader, durableWalHeader))
            throw new SqliteWalIncarnationChangedException();

        var recovery = wal.ScanRecovery();
        if (recovery.LastCommittedFrameNumber < selectedHeader.MaximumFrame
            || recovery.LastCommittedFrameNumber < currentHeader.MaximumFrame)
        {
            throw new InvalidDataException(
                "SQLite WAL no longer authenticates the selected checkpoint boundary or current index header.");
        }
        if (selectedHeader.MaximumFrame == 0)
            return;

        var selectedFrame = wal.ReadFrame(selectedHeader.MaximumFrame).Header;
        if (!selectedFrame.IsCommit
            || selectedFrame.DatabaseSizeInPages != selectedHeader.DatabasePageCount
            || selectedFrame.Checksum1 != selectedHeader.FrameChecksum1
            || selectedFrame.Checksum2 != selectedHeader.FrameChecksum2)
        {
            throw new InvalidDataException(
                "SQLite WAL selected checkpoint boundary is not its authenticated commit frame.");
        }
    }

    private static bool HasMatchingWalIncarnation(
        SqliteWalIndexHeader expectedHeader,
        SqliteWalIndexHeader actualHeader)
        => expectedHeader.PageSize == actualHeader.PageSize
           && expectedHeader.WalChecksumByteOrder == actualHeader.WalChecksumByteOrder
           && expectedHeader.Salt1 == actualHeader.Salt1
           && expectedHeader.Salt2 == actualHeader.Salt2;

    private static bool HasMatchingWalFileIncarnation(
        SqliteWalIndexHeader expectedHeader,
        SqliteWalHeader walHeader)
        => expectedHeader.PageSize == walHeader.PageSize
           && expectedHeader.WalChecksumByteOrder == walHeader.ChecksumByteOrder
           && expectedHeader.Salt1 == walHeader.Salt1
           && expectedHeader.Salt2 == walHeader.Salt2;

    private uint ReadUInt32(long position)
    {
        Span<byte> source = stackalloc byte[sizeof(uint)];
        _mapping.Read(position, source);
        return SqliteWalIndexHeader.NativeByteOrder == SqliteWalIndexByteOrder.LittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(source)
            : BinaryPrimitives.ReadUInt32BigEndian(source);
    }

    private ushort ReadUInt16(long position)
    {
        Span<byte> source = stackalloc byte[sizeof(ushort)];
        _mapping.Read(position, source);
        return SqliteWalIndexHeader.NativeByteOrder == SqliteWalIndexByteOrder.LittleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(source)
            : BinaryPrimitives.ReadUInt16BigEndian(source);
    }

    private static void WriteUInt32(Span<byte> destination, uint value)
    {
        if (SqliteWalIndexHeader.NativeByteOrder == SqliteWalIndexByteOrder.LittleEndian)
            BinaryPrimitives.WriteUInt32LittleEndian(destination, value);
        else
            BinaryPrimitives.WriteUInt32BigEndian(destination, value);
    }

    private void WriteUInt32(long position, uint value)
    {
        Span<byte> destination = stackalloc byte[sizeof(uint)];
        WriteUInt32(destination, value);
        _mapping.Write(position, destination);
    }

    private void WriteUInt16(long position, ushort value)
    {
        Span<byte> destination = stackalloc byte[sizeof(ushort)];
        if (SqliteWalIndexHeader.NativeByteOrder == SqliteWalIndexByteOrder.LittleEndian)
            BinaryPrimitives.WriteUInt16LittleEndian(destination, value);
        else
            BinaryPrimitives.WriteUInt16BigEndian(destination, value);
        _mapping.Write(position, destination);
    }

    private static uint GetBlockFrameZero(int blockIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blockIndex);
        if (blockIndex == 0)
            return 0;

        return checked(
            (uint)SqliteWalIndexLayout.FirstBlockFrameCapacity
            + checked((uint)(blockIndex - 1) * SqliteWalIndexLayout.SubsequentBlockFrameCapacity));
    }

    private static ushort GetBlockFrameCapacity(int blockIndex)
        => checked((ushort)(blockIndex == 0
            ? SqliteWalIndexLayout.FirstBlockFrameCapacity
            : SqliteWalIndexLayout.SubsequentBlockFrameCapacity));
}

/// <summary>
/// Raised when a concurrent peer wraps or resets the WAL while this connection
/// is publishing checkpoint progress. Callers should soft-skip the checkpoint
/// rather than advancing <c>nBackfill</c> or faulting the pager.
/// </summary>
/// <remarks>
/// Mirrors the SQLite 3.51.3 salt re-check after exclusive ownership of
/// <c>WAL_READ_LOCK(0)</c> (check-in <c>7168988acbec2d8d</c>): when salts diverge,
/// the checkpointer abandons the backfill instead of writing a stale frame count.
/// </remarks>
public sealed class SqliteWalIncarnationChangedException : IOException
{
    public SqliteWalIncarnationChangedException()
        : base("SQLite WAL changed incarnation while checkpoint progress was being published.")
    {
    }

    public SqliteWalIncarnationChangedException(string message)
        : base(message)
    {
    }

    public SqliteWalIncarnationChangedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
