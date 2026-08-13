using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Ahtola.Core.Storage;

/// <summary>The byte order used by SQLite's WAL rolling-checksum algorithm.</summary>
public enum SqliteWalChecksumByteOrder
{
    LittleEndian,
    BigEndian,
}

/// <summary>Why recovery scanning stopped at the reported boundary.</summary>
public enum SqliteWalRecoveryStopReason
{
    EndOfFile,
    PartialFrame,
    InvalidFrame,
}

/// <summary>
/// Implements SQLite's rolling WAL checksum over a sequence of eight-byte
/// chunks.
/// </summary>
public static class SqliteWalChecksum
{
    /// <summary>
    /// Calculates a WAL checksum, continuing from <paramref name="first"/> and
    /// <paramref name="second"/>.
    /// </summary>
    public static (uint First, uint Second) Calculate(
        ReadOnlySpan<byte> source,
        SqliteWalChecksumByteOrder byteOrder,
        uint first = 0,
        uint second = 0)
    {
        if (source.Length % 8 != 0)
            throw new ArgumentException("SQLite WAL checksum input must be a multiple of eight bytes.", nameof(source));
        if (byteOrder is not SqliteWalChecksumByteOrder.LittleEndian
            and not SqliteWalChecksumByteOrder.BigEndian)
        {
            throw new ArgumentOutOfRangeException(
                nameof(byteOrder),
                byteOrder,
                "Unsupported SQLite WAL checksum byte order.");
        }

        var firstChecksum = first;
        var secondChecksum = second;
        for (var offset = 0; offset < source.Length; offset += 8)
        {
            var firstWord = ReadWord(source.Slice(offset, 4), byteOrder);
            var secondWord = ReadWord(source.Slice(offset + 4, 4), byteOrder);
            firstChecksum = unchecked(firstChecksum + firstWord + secondChecksum);
            secondChecksum = unchecked(secondChecksum + secondWord + firstChecksum);
        }

        return (firstChecksum, secondChecksum);
    }

    private static uint ReadWord(ReadOnlySpan<byte> source, SqliteWalChecksumByteOrder byteOrder)
        => byteOrder switch
        {
            SqliteWalChecksumByteOrder.LittleEndian => BinaryPrimitives.ReadUInt32LittleEndian(source),
            SqliteWalChecksumByteOrder.BigEndian => BinaryPrimitives.ReadUInt32BigEndian(source),
            _ => throw new ArgumentOutOfRangeException(nameof(byteOrder), byteOrder, "Unsupported SQLite WAL checksum byte order."),
        };
}

/// <summary>
/// The validated 32-byte SQLite WAL header. Header fields are stored
/// big-endian; the magic number selects the byte order of checksum words.
/// </summary>
public sealed class SqliteWalHeader
{
    /// <summary>Size of a SQLite WAL header in bytes.</summary>
    public const int Size = 32;

    /// <summary>Magic value selecting little-endian checksum words.</summary>
    public const uint LittleEndianChecksumMagic = 0x377F0682;

    /// <summary>Magic value selecting big-endian checksum words.</summary>
    public const uint BigEndianChecksumMagic = 0x377F0683;

    /// <summary>The only WAL file format version supported by SQLite.</summary>
    public const uint CurrentFormatVersion = 3_007_000;

    private SqliteWalHeader(
        int pageSize,
        uint checkpointSequence,
        uint salt1,
        uint salt2,
        SqliteWalChecksumByteOrder checksumByteOrder,
        uint checksum1,
        uint checksum2)
    {
        PageSize = pageSize;
        CheckpointSequence = checkpointSequence;
        Salt1 = salt1;
        Salt2 = salt2;
        ChecksumByteOrder = checksumByteOrder;
        Checksum1 = checksum1;
        Checksum2 = checksum2;
    }

    /// <summary>The WAL magic number corresponding to <see cref="ChecksumByteOrder"/>.</summary>
    public uint Magic => ChecksumByteOrder == SqliteWalChecksumByteOrder.BigEndian
        ? BigEndianChecksumMagic
        : LittleEndianChecksumMagic;

    /// <summary>The SQLite WAL format version.</summary>
    public static uint FormatVersion => CurrentFormatVersion;

    /// <summary>The database page size encoded by this WAL.</summary>
    public int PageSize { get; }

    /// <summary>The checkpoint sequence number.</summary>
    public uint CheckpointSequence { get; }

    /// <summary>The first WAL salt.</summary>
    public uint Salt1 { get; }

    /// <summary>The second WAL salt.</summary>
    public uint Salt2 { get; }

    /// <summary>The checksum byte order selected by <see cref="Magic"/>.</summary>
    public SqliteWalChecksumByteOrder ChecksumByteOrder { get; }

    /// <summary>The first rolling checksum word.</summary>
    public uint Checksum1 { get; }

    /// <summary>The second rolling checksum word.</summary>
    public uint Checksum2 { get; }

    /// <summary>Creates a checksummed SQLite WAL header.</summary>
    public static SqliteWalHeader Create(
        int pageSize,
        uint salt1,
        uint salt2,
        uint checkpointSequence = 0,
        SqliteWalChecksumByteOrder checksumByteOrder = SqliteWalChecksumByteOrder.LittleEndian)
    {
        ValidatePageSize(pageSize, static message => new ArgumentOutOfRangeException(nameof(pageSize), message));
        ValidateChecksumByteOrder(checksumByteOrder);

        Span<byte> prefix = stackalloc byte[Size - 8];
        WritePrefix(prefix, pageSize, checkpointSequence, salt1, salt2, checksumByteOrder);
        var (First, Second) = SqliteWalChecksum.Calculate(prefix, checksumByteOrder);
        return new SqliteWalHeader(
            pageSize,
            checkpointSequence,
            salt1,
            salt2,
            checksumByteOrder,
            First,
            Second);
    }

    internal SqliteWalHeader Restart(uint salt2)
        => Create(
            PageSize,
            unchecked(Salt1 + 1),
            salt2,
            unchecked(CheckpointSequence + 1),
            ChecksumByteOrder);

    /// <summary>Parses and validates exactly one SQLite WAL header.</summary>
    public static SqliteWalHeader Parse(ReadOnlySpan<byte> source)
    {
        RequireExactLength(source.Length, Size, "SQLite WAL header");

        var magic = BinaryPrimitives.ReadUInt32BigEndian(source);
        var checksumByteOrder = magic switch
        {
            LittleEndianChecksumMagic => SqliteWalChecksumByteOrder.LittleEndian,
            BigEndianChecksumMagic => SqliteWalChecksumByteOrder.BigEndian,
            _ => throw new InvalidDataException($"Unsupported SQLite WAL magic value 0x{magic:X8}."),
        };

        var formatVersion = BinaryPrimitives.ReadUInt32BigEndian(source[4..]);
        if (formatVersion != CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"Unsupported SQLite WAL format version {formatVersion}; expected {CurrentFormatVersion}.");
        }

        var pageSize = DecodePageSize(BinaryPrimitives.ReadUInt32BigEndian(source[8..]));
        var checksum = SqliteWalChecksum.Calculate(source[..(Size - 8)], checksumByteOrder);
        var checksum1 = BinaryPrimitives.ReadUInt32BigEndian(source[24..]);
        var checksum2 = BinaryPrimitives.ReadUInt32BigEndian(source[28..]);
        if (checksum != (checksum1, checksum2))
            throw new InvalidDataException("SQLite WAL header checksum does not match its contents.");

        return new SqliteWalHeader(
            pageSize,
            BinaryPrimitives.ReadUInt32BigEndian(source[12..]),
            BinaryPrimitives.ReadUInt32BigEndian(source[16..]),
            BinaryPrimitives.ReadUInt32BigEndian(source[20..]),
            checksumByteOrder,
            checksum1,
            checksum2);
    }

    /// <summary>Serializes this header to a new exact-length buffer.</summary>
    public byte[] ToArray()
    {
        var destination = new byte[Size];
        WriteTo(destination);
        return destination;
    }

    /// <summary>Serializes this header to an exact 32-byte destination.</summary>
    public void WriteTo(Span<byte> destination)
    {
        RequireExactLength(destination.Length, Size, "SQLite WAL header destination");
        ValidatePageSize(PageSize, static message => new InvalidOperationException(message));
        ValidateChecksumByteOrder(ChecksumByteOrder);

        WritePrefix(destination[..(Size - 8)], PageSize, CheckpointSequence, Salt1, Salt2, ChecksumByteOrder);
        var checksum = SqliteWalChecksum.Calculate(destination[..(Size - 8)], ChecksumByteOrder);
        if (checksum != (Checksum1, Checksum2))
            throw new InvalidOperationException("SQLite WAL header has stale checksum fields.");

        BinaryPrimitives.WriteUInt32BigEndian(destination[24..], Checksum1);
        BinaryPrimitives.WriteUInt32BigEndian(destination[28..], Checksum2);
    }

    private static void WritePrefix(
        Span<byte> destination,
        int pageSize,
        uint checkpointSequence,
        uint salt1,
        uint salt2,
        SqliteWalChecksumByteOrder checksumByteOrder)
    {
        BinaryPrimitives.WriteUInt32BigEndian(
            destination,
            checksumByteOrder == SqliteWalChecksumByteOrder.BigEndian
                ? BigEndianChecksumMagic
                : LittleEndianChecksumMagic);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], CurrentFormatVersion);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..], checked((uint)pageSize));
        BinaryPrimitives.WriteUInt32BigEndian(destination[12..], checkpointSequence);
        BinaryPrimitives.WriteUInt32BigEndian(destination[16..], salt1);
        BinaryPrimitives.WriteUInt32BigEndian(destination[20..], salt2);
    }

    private static int DecodePageSize(uint encodedPageSize)
    {
        if (encodedPageSize == 0)
            throw new InvalidDataException("SQLite WAL page size zero is not a persistent on-disk page size.");
        if (encodedPageSize > int.MaxValue)
            throw new InvalidDataException($"Invalid SQLite WAL page size {encodedPageSize}.");

        var pageSize = (int)encodedPageSize;
        ValidatePageSize(pageSize, static message => new InvalidDataException(message));
        return pageSize;
    }

    private static void ValidatePageSize<TException>(int pageSize, Func<string, TException> createException)
        where TException : Exception
    {
        if (pageSize < SqlitePageSize.Minimum
            || pageSize > SqlitePageSize.Maximum
            || (pageSize & (pageSize - 1)) != 0)
        {
            throw createException(
                $"SQLite WAL page size must be a power of two between {SqlitePageSize.Minimum} and {SqlitePageSize.Maximum} bytes.");
        }
    }

    private static void ValidateChecksumByteOrder(SqliteWalChecksumByteOrder checksumByteOrder)
    {
        if (checksumByteOrder is not SqliteWalChecksumByteOrder.LittleEndian
            and not SqliteWalChecksumByteOrder.BigEndian)
        {
            throw new ArgumentOutOfRangeException(
                nameof(checksumByteOrder),
                checksumByteOrder,
                "Unsupported SQLite WAL checksum byte order.");
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

/// <summary>The 24-byte, big-endian header preceding one SQLite WAL page image.</summary>
public readonly record struct SqliteWalFrameHeader(
    uint PageNumber,
    uint DatabaseSizeInPages,
    uint Salt1,
    uint Salt2,
    uint Checksum1,
    uint Checksum2)
{
    /// <summary>Size of a SQLite WAL frame header in bytes.</summary>
    public const int Size = 24;

    /// <summary>Whether this frame commits its enclosing transaction.</summary>
    public bool IsCommit => DatabaseSizeInPages != 0;

    /// <summary>Parses exactly one frame header and rejects page zero.</summary>
    public static SqliteWalFrameHeader Parse(ReadOnlySpan<byte> source)
    {
        if (source.Length != Size)
        {
            throw new InvalidDataException(
                $"SQLite WAL frame header must be exactly {Size} bytes; found {source.Length} bytes.");
        }

        var pageNumber = BinaryPrimitives.ReadUInt32BigEndian(source);
        if (pageNumber == 0)
            throw new InvalidDataException("SQLite WAL frame page number must be non-zero.");
        return new SqliteWalFrameHeader(
            pageNumber,
            BinaryPrimitives.ReadUInt32BigEndian(source[4..]),
            BinaryPrimitives.ReadUInt32BigEndian(source[8..]),
            BinaryPrimitives.ReadUInt32BigEndian(source[12..]),
            BinaryPrimitives.ReadUInt32BigEndian(source[16..]),
            BinaryPrimitives.ReadUInt32BigEndian(source[20..]));
    }

    /// <summary>Serializes this frame header to an exact 24-byte destination.</summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length != Size)
        {
            throw new ArgumentException(
                $"SQLite WAL frame header destination must be exactly {Size} bytes.",
                nameof(destination));
        }
        if (PageNumber == 0)
            throw new InvalidOperationException("SQLite WAL frame page number must be non-zero.");
        BinaryPrimitives.WriteUInt32BigEndian(destination, PageNumber);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], DatabaseSizeInPages);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..], Salt1);
        BinaryPrimitives.WriteUInt32BigEndian(destination[12..], Salt2);
        BinaryPrimitives.WriteUInt32BigEndian(destination[16..], Checksum1);
        BinaryPrimitives.WriteUInt32BigEndian(destination[20..], Checksum2);
    }
}

/// <summary>A validated SQLite WAL frame and its page image.</summary>
public sealed class SqliteWalFrame
{
    internal SqliteWalFrame(SqliteWalFrameHeader header, byte[] pageData)
    {
        Header = header;
        PageData = pageData;
    }

    /// <summary>The validated frame header.</summary>
    public SqliteWalFrameHeader Header { get; }

    /// <summary>A copy of the page image stored by the frame.</summary>
    public byte[] PageData { get; }
}

/// <summary>
/// The deterministic WAL state that can be safely recovered without consulting
/// shared-memory index state.
/// </summary>
/// <remarks>
/// A <see cref="SqlitePager"/> can also report a durable checkpoint marker after
/// it has reclaimed a fully checkpointed WAL. Such a marker has no physical WAL
/// frame: <see cref="LastValidFrameNumber"/> and
/// <see cref="LastCommittedByteLength"/> identify the empty WAL, while
/// <see cref="LastCommittedFrameNumber"/> is one to show that the current main
/// database file is a durably checkpointed committed view. Results returned
/// directly by <see cref="SqliteWalFile.ScanRecovery"/> always describe physical
/// WAL frames only.
/// </remarks>
public sealed record SqliteWalRecoveryInfo(
    long LastValidFrameNumber,
    long LastCommittedFrameNumber,
    uint LastCommittedDatabaseSizeInPages,
    long LastCommittedByteLength,
    SqliteWalRecoveryStopReason StopReason)
{
    /// <summary>Whether every byte following the WAL header belonged to a valid frame.</summary>
    public bool ReachedEndOfFile => StopReason == SqliteWalRecoveryStopReason.EndOfFile;

    /// <summary>
    /// Whether this represents a pager-verified checkpointed main store rather
    /// than a physical WAL commit frame.
    /// </summary>
    public bool IsDurablyCheckpointedMainStore
        => LastValidFrameNumber == 0
           && LastCommittedFrameNumber == 1
           && LastCommittedDatabaseSizeInPages != 0
           && LastCommittedByteLength == SqliteWalHeader.Size
           && ReachedEndOfFile;
}

/// <summary>
/// A minimal, single-writer SQLite WAL file codec over <see cref="IFileSystem"/>.
/// It validates checksums and salts but intentionally does not provide SQLite
/// locking, shared-memory coordination, checkpointing, or transaction orchestration.
/// </summary>
public sealed class SqliteWalFile : IDisposable
{
    // Ahtola's pager uses this durable sequence marker to distinguish an empty WAL
    // produced by a completed checkpoint from a newly-created empty WAL. Ordinary
    // restarts still use SQLite/Turso's incrementing checkpoint sequence.
    private const uint PagerCheckpointedRecoverySequence = 0xA5C3_5A3C;

    private readonly IFile _file;
        private readonly IPageCodec? _pageCodec;
        private readonly bool _ownsPageCodec;
        private SqliteWalHeader _header;
        private bool _hasCheckpointedRecoveryMarker;
        private bool _truncatedAfterCheckpoint;
        private bool _disposed;

        private SqliteWalFile(
            IFile file,
            SqliteWalHeader header,
            IPageCodec? pageCodec,
            bool ownsPageCodec,
            bool hasCheckpointedRecoveryMarker = false)
        {
            _file = file;
            _header = header;
            _pageCodec = pageCodec;
            _ownsPageCodec = ownsPageCodec;
            _hasCheckpointedRecoveryMarker = hasCheckpointedRecoveryMarker;
        }

    /// <summary>The validated WAL header.</summary>
    public SqliteWalHeader Header
    {
        get
        {
            ThrowIfDisposed();
            return _header;
        }
    }

    /// <summary>
    /// Reads the on-disk WAL header without mutating this instance's cached
    /// header. Used by checkpoint paths that must observe a peer wrap/reset.
    /// </summary>
    public SqliteWalHeader ReadDurableHeader()
    {
        ThrowIfDisposed();
        if (_truncatedAfterCheckpoint && _file.Length == 0)
            return _header;
        if (_file.Length < SqliteWalHeader.Size)
        {
            throw new InvalidDataException(
                "File is too small to contain a SQLite WAL header.");
        }

        Span<byte> headerBytes = stackalloc byte[SqliteWalHeader.Size];
        if (_file.Read(0, headerBytes) != headerBytes.Length)
            throw new InvalidDataException("Failed to read the complete SQLite WAL header.");
        return SqliteWalHeader.Parse(headerBytes);
    }

    /// <summary>The fixed database page size used by every WAL frame.</summary>
    public int PageSize
    {
        get
        {
            ThrowIfDisposed();
            return Header.PageSize;
        }
    }

    /// <summary>The byte size of one frame header and page image.</summary>
    public long FrameSize
    {
        get
        {
            ThrowIfDisposed();
            return checked((long)SqliteWalFrameHeader.Size + Header.PageSize);
        }
    }

    /// <summary>The current physical file length, including any incomplete tail.</summary>
    public long Length
    {
        get
        {
            ThrowIfDisposed();
            return _file.Length;
        }
    }

    /// <summary>Whether this WAL was opened read-only.</summary>
    public bool IsReadOnly => _file.IsReadOnly;

    internal bool HasCheckpointedRecoveryMarker
    {
        get
        {
            ThrowIfDisposed();
            return _hasCheckpointedRecoveryMarker
                || _header.CheckpointSequence == PagerCheckpointedRecoverySequence;
        }
    }

    /// <summary>Creates a new WAL file containing only <paramref name="header"/>.</summary>
    public static SqliteWalFile Create(
        IFileSystem fileSystem,
        string path,
        SqliteWalHeader header,
            AhtolaEncryptionOptions? encryption = null,
            IPageCodec? pageCodec = null)
        {
            ArgumentNullException.ThrowIfNull(fileSystem);
            ArgumentException.ThrowIfNullOrEmpty(path);
            ArgumentNullException.ThrowIfNull(header);

            var boundCodec = PageCodecSupport.Bind(encryption, pageCodec, header.PageSize, out var ownsCodec);
            var file = fileSystem.OpenFile(path, FileOpenMode.CreateNew);
            try
            {
                file.Write(0, header.ToArray());
                if (file.Length != SqliteWalHeader.Size)
                    throw new InvalidDataException("Writing the SQLite WAL header produced an invalid file length.");

                file.FlushToDisk();
                return new SqliteWalFile(file, header, boundCodec, ownsCodec);
            }
            catch
            {
                try
                {
                    file.Dispose();
                }
                catch
                {
                }

                try
                {
                    PageCodecSupport.DisposeOwned(boundCodec, ownsCodec);
                }
                catch
                {
                }

                try
                {
                    fileSystem.DeleteFile(path);
                }
                catch
                {
                }

                throw;
            }
        }

    /// <summary>
    /// Opens an existing WAL after validating its header. A partial or corrupt
    /// frame tail is left for <see cref="ScanRecovery"/> to diagnose.
    /// </summary>
    public static SqliteWalFile Open(
        IFileSystem fileSystem,
        string path,
        bool readOnly = false,
        AhtolaEncryptionOptions? encryption = null,
            SqliteWalHeader? truncatedHeader = null,
            IPageCodec? pageCodec = null)
        {
            ArgumentNullException.ThrowIfNull(fileSystem);
            ArgumentException.ThrowIfNullOrEmpty(path);
            PageCodecSupport.RejectCombinedTransforms(encryption, pageCodec);

            var file = fileSystem.OpenFile(path, FileOpenMode.OpenExisting, readOnly);
            IPageCodec? boundCodec = null;
            var ownsCodec = false;
            try
            {
                if (file.Length == 0 && truncatedHeader is not null)
                {
                    boundCodec = PageCodecSupport.Bind(
                        encryption,
                        pageCodec,
                        truncatedHeader.PageSize,
                        out ownsCodec);
                    return new SqliteWalFile(file, truncatedHeader, boundCodec, ownsCodec)
                    {
                        _truncatedAfterCheckpoint = true,
                    };
                }
                if (file.Length < SqliteWalHeader.Size)
                    throw new InvalidDataException("File is too small to contain a SQLite WAL header.");

                Span<byte> headerBytes = stackalloc byte[SqliteWalHeader.Size];
                if (file.Read(0, headerBytes) != headerBytes.Length)
                    throw new InvalidDataException("Failed to read the complete SQLite WAL header.");

                var header = SqliteWalHeader.Parse(headerBytes);
                boundCodec = PageCodecSupport.Bind(encryption, pageCodec, header.PageSize, out ownsCodec);
                return new SqliteWalFile(file, header, boundCodec, ownsCodec);
            }
            catch
            {
                file.Dispose();
                PageCodecSupport.DisposeOwned(boundCodec, ownsCodec);
                throw;
            }
        }

    /// <summary>
    /// Appends a checksummed page frame. A non-zero
    /// <paramref name="databaseSizeInPages"/> marks this frame as committed.
    /// </summary>
    public long AppendFrame(uint pageNumber, ReadOnlySpan<byte> pageData, uint databaseSizeInPages = 0)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        if (pageNumber == 0)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), pageNumber, "SQLite WAL page numbers are 1-based.");
        if (pageData.Length != Header.PageSize)
        {
            throw new ArgumentException(
                $"SQLite WAL page data must be exactly {Header.PageSize} bytes.",
                nameof(pageData));
        }

        MaterializeHeaderAfterCheckpointTruncate();
        var scan = ScanCore();
        if (scan.Info.StopReason != SqliteWalRecoveryStopReason.EndOfFile)
        {
            throw new InvalidDataException(
                "Cannot append to a SQLite WAL with a partial or invalid frame tail; recover it first.");
        }

        var frameNumber = checked(scan.Info.LastValidFrameNumber + 1);
        var offset = FrameOffset(frameNumber);
        if (_file.Length != offset)
            throw new InvalidDataException("SQLite WAL length changed while preparing an append.");

        var frame = new byte[checked((int)FrameSize)];
        var frameHeader = new SqliteWalFrameHeader(
            pageNumber,
            databaseSizeInPages,
            Header.Salt1,
            Header.Salt2,
            0,
            0);
        frameHeader.WriteTo(frame.AsSpan(0, SqliteWalFrameHeader.Size));
                if (_pageCodec is null)
            pageData.CopyTo(frame.AsSpan(SqliteWalFrameHeader.Size));
        else
                    PageCodecSupport.Encode(
                        _pageCodec,
                        PageLocation.Wal,
                        pageNumber,
                        pageData,
                        frame.AsSpan(SqliteWalFrameHeader.Size));

        var (First, Second) = SqliteWalChecksum.Calculate(
            frame.AsSpan(0, 8),
            Header.ChecksumByteOrder,
            scan.LastChecksum.First,
            scan.LastChecksum.Second);
        var checksum = SqliteWalChecksum.Calculate(
            frame.AsSpan(SqliteWalFrameHeader.Size),
            Header.ChecksumByteOrder,
            First,
            Second);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(16, 4), checksum.First);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(20, 4), checksum.Second);

        AppendAtEnd(offset, frame);
        return frameNumber;
    }

    /// <summary>
    /// Reads one frame after validating every checksum in its chain from the WAL
    /// header through that frame.
    /// </summary>
    public SqliteWalFrame ReadFrame(long frameNumber)
    {
        ThrowIfDisposed();
        if (frameNumber < 1)
            throw new ArgumentOutOfRangeException(nameof(frameNumber), frameNumber, "SQLite WAL frame numbers are 1-based.");

        var fullFrameCount = CompleteFrameCount(_file.Length);
        if (frameNumber > fullFrameCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameNumber),
                frameNumber,
                $"Frame number is out of range for {fullFrameCount} complete SQLite WAL frame(s).");
        }

        var previousChecksum = (Header.Checksum1, Header.Checksum2);
        for (var currentFrameNumber = 1L; currentFrameNumber <= frameNumber; currentFrameNumber++)
        {
            var frame = ReadFrameBytes(FrameOffset(currentFrameNumber));
            var frameHeader = ValidateFrame(frame, previousChecksum, out var checksum);
            if (currentFrameNumber == frameNumber)
            {
                var onDiskPageData = frame.AsSpan(SqliteWalFrameHeader.Size);
                                byte[] pageData;
                                if (_pageCodec is null)
                                {
                                    pageData = onDiskPageData.ToArray();
                                }
                                else
                                {
                                    pageData = new byte[onDiskPageData.Length];
                                    PageCodecSupport.Decode(
                                        _pageCodec,
                                        PageLocation.Wal,
                                        frameHeader.PageNumber,
                                        onDiskPageData,
                                        pageData);
                                }

                                return new SqliteWalFrame(frameHeader, pageData);
            }

            previousChecksum = checksum;
        }

        throw new InvalidOperationException("SQLite WAL frame traversal ended unexpectedly.");
    }

    /// <summary>
    /// Scans valid frames in order and reports the boundary after the most recent
    /// valid commit. Corrupt or incomplete tails are not treated as committed.
    /// </summary>
    public SqliteWalRecoveryInfo ScanRecovery()
    {
        ThrowIfDisposed();
        return ScanCore().Info;
    }

    /// <summary>
    /// Truncates and durably flushes every byte after the last valid committed
    /// frame, then returns the scan result used to choose the boundary.
    /// </summary>
    public SqliteWalRecoveryInfo RecoverToLastCommittedFrame()
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();

        var scan = ScanCore();
        if (_truncatedAfterCheckpoint && _file.Length == 0)
            return scan.Info;

        var targetLength = scan.Info.LastCommittedByteLength;
        if (_file.Length != targetLength)
        {
            _file.SetLength(targetLength);
            if (_file.Length != targetLength)
                throw new InvalidDataException("SQLite WAL recovery truncation did not reach its requested boundary.");

            _file.FlushToDisk();
        }

        return scan.Info;
    }

    /// <summary>Flushes WAL bytes and metadata to durable storage.</summary>
    public void Flush()
    {
        ThrowIfDisposed();
        if (!IsReadOnly)
            _file.FlushToDisk();
    }

    /// <summary>
    /// Reclaims every committed frame after the caller has durably installed the
    /// same committed view in the main database file.
    /// </summary>
    /// <remarks>
    /// This is intentionally pager-only: the caller must hold the exclusive
    /// checkpoint lock and must not call it until main-store durability has
    /// succeeded. It refuses a partial, corrupt, or uncommitted tail rather than
    /// discarding recovery evidence.
    /// </remarks>
    internal void ResetAfterDurableCheckpoint(bool publishCheckpointedRecoveryMarker)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();

        var scan = ScanCore();
        if (scan.Info.StopReason != SqliteWalRecoveryStopReason.EndOfFile
            || scan.Info.LastValidFrameNumber != scan.Info.LastCommittedFrameNumber)
        {
            throw new InvalidDataException(
                "Cannot reset a SQLite WAL with a partial, corrupt, or uncommitted frame tail.");
        }

        if (scan.Info.LastCommittedFrameNumber == 0)
            return;

        _file.SetLength(SqliteWalHeader.Size);
        if (_file.Length != SqliteWalHeader.Size)
            throw new InvalidDataException("SQLite WAL reset did not reach its header boundary.");

        var salt2 = CreateRandomSalt();
        var replacementHeader = publishCheckpointedRecoveryMarker
            ? SqliteWalHeader.Create(
                _header.PageSize,
                unchecked(_header.Salt1 + 1),
                salt2,
                PagerCheckpointedRecoverySequence,
                _header.ChecksumByteOrder)
            : _header.Restart(salt2);
        _file.Write(0, replacementHeader.ToArray());
        _header = replacementHeader;
        _hasCheckpointedRecoveryMarker = publishCheckpointedRecoveryMarker;

        _file.FlushToDisk();
    }

    /// <summary>
    /// Truncates the WAL to zero bytes after a caller has durably checkpointed its
    /// complete committed view and excluded writers and every read mark.
    /// </summary>
    /// <remarks>
    /// A retained coordinator can begin a later write: it recreates this file's
    /// validated WAL header before appending its first frame. A separate opener
    /// must wait for that writer, as SQLite's normal WAL protocol does.
    /// </remarks>
    internal void TruncateAfterDurableCheckpoint()
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();

        var scan = ScanCore();
        if (scan.Info.StopReason != SqliteWalRecoveryStopReason.EndOfFile
            || scan.Info.LastValidFrameNumber != scan.Info.LastCommittedFrameNumber)
        {
            throw new InvalidDataException(
                "Cannot truncate a SQLite WAL with a partial, corrupt, or uncommitted frame tail.");
        }

        _file.SetLength(0);
        if (_file.Length != 0)
            throw new InvalidDataException("SQLite WAL truncation did not reach zero bytes.");

        _file.FlushToDisk();
        _truncatedAfterCheckpoint = true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _file.Dispose();
                PageCodecSupport.DisposeOwned(_pageCodec, _ownsPageCodec);
    }

    private ScanState ScanCore()
    {
        var length = _file.Length;
        if (_truncatedAfterCheckpoint && length == 0)
        {
            return CreateScanState(
                lastValidFrameNumber: 0,
                lastCommittedFrameNumber: 0,
                lastCommittedDatabaseSizeInPages: 0,
                SqliteWalRecoveryStopReason.EndOfFile,
                (Header.Checksum1, Header.Checksum2));
        }

        // Peer engine materialised a real WAL header into a file we opened as
        // truncated (zero-length -wal under a live stock SQLite hold). Drop the
        // synthetic header and parse the on-disk one before scanning frames.
        if (_truncatedAfterCheckpoint && length >= SqliteWalHeader.Size)
        {
            var headerBytes = new byte[SqliteWalHeader.Size];
            if (_file.Read(0, headerBytes) == headerBytes.Length)
            {
                _header = SqliteWalHeader.Parse(headerBytes);
                _truncatedAfterCheckpoint = false;
            }
        }

        var fullFrameCount = CompleteFrameCount(length);
        var hasPartialFrame = (length - SqliteWalHeader.Size) % FrameSize != 0;
        var previousChecksum = (Header.Checksum1, Header.Checksum2);
        var lastValidFrameNumber = 0L;
        var lastCommittedFrameNumber = 0L;
        var lastCommittedDatabaseSizeInPages = 0U;

        for (var frameNumber = 1L; frameNumber <= fullFrameCount; frameNumber++)
        {
            var frame = new byte[checked((int)FrameSize)];
            if (_file.Read(FrameOffset(frameNumber), frame) != frame.Length)
            {
                return CreateScanState(
                    lastValidFrameNumber,
                    lastCommittedFrameNumber,
                    lastCommittedDatabaseSizeInPages,
                    SqliteWalRecoveryStopReason.PartialFrame,
                    previousChecksum);
            }

            SqliteWalFrameHeader frameHeader;
            (uint First, uint Second) checksum;
            try
            {
                frameHeader = ValidateFrame(frame, previousChecksum, out checksum);
            }
            catch (InvalidDataException)
            {
                return CreateScanState(
                    lastValidFrameNumber,
                    lastCommittedFrameNumber,
                    lastCommittedDatabaseSizeInPages,
                    SqliteWalRecoveryStopReason.InvalidFrame,
                    previousChecksum);
            }

            lastValidFrameNumber = frameNumber;
            previousChecksum = checksum;
            if (frameHeader.IsCommit)
            {
                lastCommittedFrameNumber = frameNumber;
                lastCommittedDatabaseSizeInPages = frameHeader.DatabaseSizeInPages;
            }
        }

        return CreateScanState(
            lastValidFrameNumber,
            lastCommittedFrameNumber,
            lastCommittedDatabaseSizeInPages,
            hasPartialFrame ? SqliteWalRecoveryStopReason.PartialFrame : SqliteWalRecoveryStopReason.EndOfFile,
            previousChecksum);
    }

    private ScanState CreateScanState(
        long lastValidFrameNumber,
        long lastCommittedFrameNumber,
        uint lastCommittedDatabaseSizeInPages,
        SqliteWalRecoveryStopReason stopReason,
        (uint First, uint Second) lastChecksum)
        => new(
            new SqliteWalRecoveryInfo(
                lastValidFrameNumber,
                lastCommittedFrameNumber,
                lastCommittedDatabaseSizeInPages,
                LengthForFrameCount(lastCommittedFrameNumber),
                stopReason),
            lastChecksum);

    private SqliteWalFrameHeader ValidateFrame(
        ReadOnlySpan<byte> frame,
        (uint First, uint Second) previousChecksum,
        out (uint First, uint Second) checksum)
    {
        if (frame.Length != FrameSize)
            throw new InvalidDataException("SQLite WAL frame has an unexpected length.");

        var frameHeader = SqliteWalFrameHeader.Parse(frame[..SqliteWalFrameHeader.Size]);
        if (frameHeader.Salt1 != Header.Salt1 || frameHeader.Salt2 != Header.Salt2)
            throw new InvalidDataException("SQLite WAL frame salts do not match the WAL header.");

        var (First, Second) = SqliteWalChecksum.Calculate(
            frame[..8],
            Header.ChecksumByteOrder,
            previousChecksum.First,
            previousChecksum.Second);
        checksum = SqliteWalChecksum.Calculate(
            frame[SqliteWalFrameHeader.Size..],
            Header.ChecksumByteOrder,
            First,
            Second);
        if (checksum != (frameHeader.Checksum1, frameHeader.Checksum2))
            throw new InvalidDataException("SQLite WAL frame checksum does not match its contents.");

        return frameHeader;
    }

    private void AppendAtEnd(long offset, ReadOnlySpan<byte> frame)
    {
        var expectedLength = checked(offset + frame.Length);
        try
        {
            _file.Write(offset, frame);
            if (_file.Length != expectedLength)
                throw new InvalidDataException("Appending a SQLite WAL frame produced an invalid file length.");
        }
        catch (Exception appendException)
        {
            try
            {
                if (_file.Length != offset)
                    _file.SetLength(offset);
            }
            catch (Exception rollbackException)
            {
                throw new InvalidDataException(
                    "Appending a SQLite WAL frame failed and the original length could not be restored.",
                    new AggregateException(appendException, rollbackException));
            }

            throw;
        }
    }

    private void MaterializeHeaderAfterCheckpointTruncate()
    {
        if (!_truncatedAfterCheckpoint)
            return;
        if (_file.Length != 0)
        {
            throw new InvalidDataException(
                "SQLite WAL changed after a checkpoint truncate; refusing to overwrite an unexpected artifact.");
        }

        _file.Write(position: 0, Header.ToArray());
        if (_file.Length != SqliteWalHeader.Size)
            throw new InvalidDataException("Recreating a truncated SQLite WAL header produced an invalid file length.");

        _truncatedAfterCheckpoint = false;
    }

    private byte[] ReadFrameBytes(long offset)
    {
        var frame = new byte[checked((int)FrameSize)];
        var read = _file.Read(offset, frame);
        if (read != frame.Length)
        {
            throw new InvalidDataException(
                $"Short read on SQLite WAL frame: expected {frame.Length} bytes, got {read} bytes.");
        }

        return frame;
    }

    private long CompleteFrameCount(long length)
    {
        if (length < SqliteWalHeader.Size)
            throw new InvalidDataException("SQLite WAL is shorter than its header.");

        return (length - SqliteWalHeader.Size) / FrameSize;
    }

    private long FrameOffset(long frameNumber)
    {
        if (frameNumber < 1)
            throw new ArgumentOutOfRangeException(nameof(frameNumber), frameNumber, "SQLite WAL frame numbers are 1-based.");

        return checked(SqliteWalHeader.Size + checked((frameNumber - 1) * FrameSize));
    }

    private long LengthForFrameCount(long frameCount)
    {
        if (frameCount < 0)
            throw new ArgumentOutOfRangeException(nameof(frameCount), frameCount, "SQLite WAL frame count cannot be negative.");

        return checked(SqliteWalHeader.Size + checked(frameCount * FrameSize));
    }

    private void ThrowIfReadOnly()
    {
        if (IsReadOnly)
            throw new InvalidOperationException("The SQLite WAL was opened read-only.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static uint CreateRandomSalt()
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        RandomNumberGenerator.Fill(bytes);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    private readonly record struct ScanState(
        SqliteWalRecoveryInfo Info,
        (uint First, uint Second) LastChecksum);
}
