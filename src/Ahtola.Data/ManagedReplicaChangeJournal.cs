using System.Text;
using Ahtola.Core;

namespace Ahtola;

/// <summary>
/// The kind of durable operation captured from a managed embedded replica.
/// </summary>
internal enum ReplicaLocalChangeKind : byte
{
    Row = 1,
    Schema = 2,
}

/// <summary>
/// A committed operation awaiting a future managed-replica push implementation.
/// </summary>
internal readonly record struct ReplicaLocalChange(
    long Sequence,
    ReplicaLocalChangeKind Kind,
    SqliteChangeOperation Operation,
    string Database,
    string Table,
    long RowId,
    string Sql,
    byte[]? BeforeRecord)
{
    public static ReplicaLocalChange Row(
        SqliteChangeOperation operation,
        string database,
        string table,
        long rowId,
        byte[]? beforeRecord = null)
        => new(0, ReplicaLocalChangeKind.Row, operation, database, table, rowId, string.Empty, beforeRecord);

    public static ReplicaLocalChange Schema(string sql)
        => new(0, ReplicaLocalChangeKind.Schema, default, string.Empty, string.Empty, 0, sql, null);
}

/// <summary>
/// An ordered, bounded view of locally committed replica operations. <see cref="Watermark"/>
/// is the exclusive sequence boundary a successful push may acknowledge.
/// </summary>
internal readonly record struct ReplicaLocalChangeBatch(
    long FirstSequence,
    long Watermark,
    IReadOnlyList<ReplicaLocalChange> Changes);

/// <summary>
/// Replica-private, crash-safe journal. It deliberately lives outside the SQLite file so a
/// remote raw-page replacement never becomes a locally captured mutation.
/// </summary>
internal sealed class ManagedReplicaChangeJournal
{
    internal const string Suffix = ".ahtola-replica-journal";

    private const ulong Magic = 0x4C_4E_52_4A_4C_4F_54_41; // "ATOLJRNL"
    private const int Version = 5;
    private const int MaxStringBytes = 1024 * 1024;
    private const int MaxBinaryBytes = 16 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly List<ReplicaLocalChange> _changes;
    private long _sequence;
    private long _watermark;

    private ManagedReplicaChangeJournal(
        string path,
        long sequence,
        long watermark,
        List<ReplicaLocalChange> changes)
    {
        _path = path;
        _sequence = sequence;
        _watermark = watermark;
        _changes = changes;
    }

    public static ManagedReplicaChangeJournal Open(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var path = databasePath + Suffix;
        if (!File.Exists(path))
            return new ManagedReplicaChangeJournal(path, 0, 1, []);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        if (reader.ReadUInt64() != Magic)
            throw new InvalidDataException("Managed replica change journal has an unsupported format.");
        var formatVersion = reader.ReadInt32();
        if (formatVersion is not (1 or 2 or 3 or 4 or Version))
            throw new InvalidDataException("Managed replica change journal has an unsupported format.");

        var sequence = reader.ReadInt64();
        var persistedWatermark = reader.ReadInt64();
        var count = reader.ReadInt32();
        if (sequence < 0
            || (formatVersion == 1
                ? persistedWatermark != checked(sequence + 1)
                : persistedWatermark < 1 || persistedWatermark > checked(sequence + 1))
            || count < 0)
            throw new InvalidDataException("Managed replica change journal has invalid state.");
        if (count > (stream.Length - stream.Position) / 13)
            throw new InvalidDataException("Managed replica change journal has an invalid entry count.");

        var changes = new List<ReplicaLocalChange>(count);
        long previous = 0;
        for (var i = 0; i < count; i++)
        {
            var change = ReadChange(reader, formatVersion);
            if (change.Sequence <= previous
                || (formatVersion is 2 or 3 or 4 && change.Sequence < persistedWatermark)
                || change.Sequence > sequence)
                throw new InvalidDataException("Managed replica change journal is not ordered.");
            changes.Add(change);
            previous = change.Sequence;
        }

        if (stream.Position != stream.Length || (count != 0 && previous != sequence))
            throw new InvalidDataException("Managed replica change journal is malformed.");
        var watermark = formatVersion == 1 && changes.Count != 0
            ? changes[0].Sequence
            : persistedWatermark;
        return new ManagedReplicaChangeJournal(path, sequence, watermark, changes);
    }

    internal long RetentionBase
    {
        get
        {
            lock (_gate)
                return _changes.Count == 0 ? _watermark : Math.Min(_changes[0].Sequence, _watermark);
        }
    }

    public ReplicaLocalChangeBatch ReadBatch(int maximumChanges)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumChanges);
        lock (_gate)
        {
            var pending = _changes.Where(change => change.Sequence >= _watermark);
            var count = Math.Min(maximumChanges, _changes.Count(change => change.Sequence >= _watermark));
            if (count == 0)
                return new ReplicaLocalChangeBatch(_watermark, _watermark, []);

            var batch = pending.Take(count).ToArray();
            var watermark = batch[^1].Sequence + 1;
            return new ReplicaLocalChangeBatch(batch[0].Sequence, watermark, batch);
        }
    }

    public ReplicaLocalChangeBatch ReadBatch(long firstSequence, long watermark)
    {
        if (firstSequence <= 0 || watermark <= firstSequence)
            throw new ArgumentOutOfRangeException(nameof(firstSequence));

        lock (_gate)
        {
            var batch = _changes
                .Where(change => change.Sequence >= firstSequence && change.Sequence < watermark)
                .ToArray();
            if (batch.Length == 0
                || batch[0].Sequence != firstSequence
                || batch[^1].Sequence != watermark - 1
                || batch.Length != watermark - firstSequence)
            {
                throw new InvalidDataException(
                    "Managed replica change journal no longer contains the protected push batch.");
            }

            return new ReplicaLocalChangeBatch(firstSequence, watermark, batch);
        }
    }

    public IReadOnlyList<ReplicaLocalChange> ReadAcknowledged(long afterWatermark)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(afterWatermark);
        lock (_gate)
        {
            return _changes
                .Where(change => change.Sequence >= afterWatermark && change.Sequence < _watermark)
                .ToArray();
        }
    }

    public void AppendCommitted(IReadOnlyList<ReplicaLocalChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
            return;

        lock (_gate)
        {
            var first = checked(_sequence + 1);
            var assigned = new ReplicaLocalChange[changes.Count];
            for (var i = 0; i < changes.Count; i++)
            {
                var change = changes[i];
                assigned[i] = change with { Sequence = checked(first + i) };
            }

            var nextSequence = assigned[^1].Sequence;
            Persist(nextSequence, _watermark, _changes.Concat(assigned).ToArray());
            ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.JournalAppendPersisted);
            _changes.AddRange(assigned);
            _sequence = nextSequence;
        }
    }

    /// <summary>
    /// Durably discards changes below an exclusive watermark after their enclosing remote
    /// transaction has committed. Failed, cancelled, and conflicting pushes never call this.
    /// </summary>
    public void Acknowledge(long watermark)
    {
        lock (_gate)
        {
            if (watermark < _watermark || watermark > checked(_sequence + 1))
                throw new ArgumentOutOfRangeException(nameof(watermark));

            if (watermark == _watermark)
                return;

            Persist(_sequence, watermark, _changes);
            ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.JournalAcknowledgementPersisted);
            _watermark = watermark;
        }
    }

    public void PruneAcknowledged(long throughWatermark)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(throughWatermark);
        lock (_gate)
        {
            var effectiveWatermark = Math.Min(throughWatermark, _watermark);
            if (_changes.Count == 0 || _changes[0].Sequence >= effectiveWatermark)
                return;

            var retained = _changes.Where(change => change.Sequence >= effectiveWatermark).ToArray();
            Persist(_sequence, _watermark, retained);
            _changes.RemoveAll(change => change.Sequence < effectiveWatermark);
        }
    }

    private void Persist(long sequence, long watermark, IReadOnlyList<ReplicaLocalChange> changes)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        var stagingPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       stagingPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(sequence);
                writer.Write(watermark);
                writer.Write(changes.Count);
                foreach (var change in changes)
                    WriteChange(writer, change);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
                File.Replace(stagingPath, _path, destinationBackupFileName: null, ignoreMetadataErrors: false);
            else
                File.Move(stagingPath, _path, overwrite: false);
        }
        finally
        {
            if (File.Exists(stagingPath))
                File.Delete(stagingPath);
        }
    }

    private static void WriteChange(BinaryWriter writer, ReplicaLocalChange change)
    {
        writer.Write(change.Sequence);
        writer.Write((byte)change.Kind);
        switch (change.Kind)
        {
            case ReplicaLocalChangeKind.Row:
                writer.Write((int)change.Operation);
                WriteString(writer, change.Database);
                WriteString(writer, change.Table);
                writer.Write(change.RowId);
                WriteString(writer, change.Sql);
                WriteBytes(writer, change.BeforeRecord);
                break;
            case ReplicaLocalChangeKind.Schema:
                WriteString(writer, change.Sql);
                break;
            default:
                throw new InvalidDataException("Managed replica change journal has an unknown change kind.");
        }
    }

    private static ReplicaLocalChange ReadChange(BinaryReader reader, int formatVersion)
    {
        var sequence = reader.ReadInt64();
        var kind = (ReplicaLocalChangeKind)reader.ReadByte();
        return kind switch
        {
            ReplicaLocalChangeKind.Row => new ReplicaLocalChange(
                sequence,
                kind,
                (SqliteChangeOperation)reader.ReadInt32(),
                ReadString(reader),
                ReadString(reader),
                reader.ReadInt64(),
                formatVersion >= 3 ? ReadString(reader) : string.Empty,
                formatVersion >= 4 ? ReadBytes(reader) : null),
            ReplicaLocalChangeKind.Schema => ReplicaLocalChange.Schema(ReadString(reader)) with { Sequence = sequence },
            _ => throw new InvalidDataException("Managed replica change journal has an unknown change kind."),
        };
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > MaxStringBytes)
            throw new InvalidDataException("Managed replica change journal entry is too large.");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteBytes(BinaryWriter writer, byte[]? value)
    {
        if (value is null)
        {
            writer.Write(-1);
            return;
        }

        if (value.Length > MaxBinaryBytes)
            throw new InvalidDataException("Managed replica change journal binary entry is too large.");
        writer.Write(value.Length);
        writer.Write(value);
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > MaxStringBytes)
            throw new InvalidDataException("Managed replica change journal contains an invalid string.");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException("Managed replica change journal is truncated.");
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Managed replica change journal contains invalid UTF-8.", exception);
        }
    }

    private static byte[]? ReadBytes(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length == -1)
            return null;
        if (length < 0 || length > MaxBinaryBytes)
            throw new InvalidDataException("Managed replica change journal contains an invalid binary entry.");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException("Managed replica change journal is truncated.");
        return bytes;
    }
}
