using System.Buffers.Binary;
using System.Text;
using Ahtola.Core.Storage;

namespace Ahtola;

/// <summary>
/// A bounded, standalone decoder for Turso's network "lml3" MVCC logical-log wire format
/// (<c>MvccLogicalLogMetadataProto.format == "lml3"</c>). This mirrors the byte layout Turso's
/// server streams verbatim from its persisted logical log (56-byte header, chained-CRC32C
/// transaction frames, an optional portable-changes extension block, and SQLite-varint-framed
/// recovery operations) but is intentionally independent of Ahtola's own local
/// <c>Ahtola.Core.Mvcc.MvccLogicalLog</c> (v4) writer/reader: the two serve different purposes
/// (durable local storage vs. a bounded network decode) and must not share implementation.
/// </summary>
/// <remarks>
/// The decoder validates the entire supplied body before any caller can observe partial
/// results: every range/frame/op is structurally validated (magic numbers, lengths, chained
/// CRC32C, UTF-8, protobuf bounds) up front, and the full <see cref="ManagedReplicaLogicalTxn"/>
/// sequence is only returned once the whole body decodes cleanly with zero trailing bytes.
/// </remarks>
internal static class ManagedReplicaLml3Decoder
{
    internal const string ExpectedFormat = "lml3";

    private const uint LogMagic = 0x4C4D4C32; // "LML2" in LE (lml3 = log format version 3)
    private const byte LogVersion = 3;
    private const int LogHeaderSize = 56;
    private const int LogHeaderSaltStart = 8;
    private const int LogHeaderSaltEnd = 16;
    private const int LogHeaderReservedStart = 16;
    private const int LogHeaderCrcStart = 52;

    private const uint FrameMagic = 0x5854564D; // "MVTX"
    private const uint ExtFrameMagic = 0x5845564D; // "MVEX"
    private const uint EndMagic = 0x4554564D; // "MVTE"
    private const int TxHeaderSize = 24; // frame_magic(4) + payload_size(8) + op_count(4) + commit_ts(8)
    private const int TxExtHeaderSize = 40; // TxHeaderSize + extension_size(8) + extension_record_count(4) + frame_flags(4)
    private const int TxTrailerSize = 8; // crc32c(4) + end_magic(4)
    private const uint TxFlagHasExtensionBlock = 1u << 0;

    private const int ExtensionRecordHeaderSize = 8; // type(u16) + flags(u16) + len(u32)
    private const ushort ExtensionTypePortableChanges = 1;

    private const byte OpUpsertTable = 0;
    private const byte OpDeleteTable = 1;
    private const byte OpUpsertIndex = 2;
    private const byte OpDeleteIndex = 3;
    private const byte OpUpdateHeader = 4;
    private const byte OpFlagPortableExtension = 1 << 1;

    private const long SqliteSchemaTableId = -1;
    private const ulong DeleteExtIdentityRecordField = 1;
    private const ulong DeleteExtPkRecordField = 2;

    private const ulong PortableTxFieldStringTable = 12;
    private const ulong PortableTxFieldObjectMap = 13;
    private const ulong PortableTxFieldMeta = 14;
    private const ulong PortableObjectFieldMvTableId = 1;
    private const ulong PortableObjectFieldNameRef = 2;
    private const ulong PortableMetaFieldKeyRef = 1;
    private const ulong PortableMetaFieldValueRef = 2;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Decodes every transaction carried by <paramref name="ranges"/> against the concatenated
    /// response <paramref name="body"/> bytes. Ranges are processed in order; each range's byte
    /// length (<c>EndOffset - StartOffset</c>) determines how many bytes of <paramref name="body"/>
    /// it consumes, starting immediately after the previous range's bytes.
    /// </summary>
    public static IReadOnlyList<ManagedReplicaLogicalTxn> Decode(
        IReadOnlyList<ManagedReplicaLogicalLogRange> ranges,
        byte[] body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        ArgumentNullException.ThrowIfNull(body);
        if (ranges.Count == 0)
            throw new InvalidDataException("The MVCC logical-log stream has no ranges.");

        var transactions = new List<ManagedReplicaLogicalTxn>();
        var bodyOffset = 0;
        uint? runningCrc = null;
        (ulong Generation, ulong EndOffset)? previousRangeBoundary = null;

        foreach (var range in ranges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (previousRangeBoundary != (range.Generation, range.StartOffset))
                runningCrc = null;

            if (range.EndOffset < range.StartOffset)
            {
                throw new InvalidDataException(
                    $"The MVCC logical-log range {range.StartOffset}..{range.EndOffset} is invalid.");
            }

            var rangeLengthUlong = range.EndOffset - range.StartOffset;
            if (rangeLengthUlong > int.MaxValue)
                throw new InvalidDataException("An MVCC logical-log range length overflows the supported buffer size.");

            var rangeLength = (int)rangeLengthUlong;
            var rangeEnd = checked(bodyOffset + rangeLength);
            if (rangeEnd > body.Length)
            {
                throw new InvalidDataException(
                    $"The MVCC logical-log body is shorter than advertised range {range.StartOffset}..{range.EndOffset}.");
            }

            var rangeBody = body.AsSpan(bodyOffset, rangeLength);
            bodyOffset = rangeEnd;

            var pos = 0;
            if (range.StartsWithHeader)
            {
                runningCrc = ValidateLogHeader(rangeBody);
                pos = LogHeaderSize;
            }
            else if (range.CrcSeed is { } seed)
            {
                runningCrc = DecodeCrcSeed(seed);
            }
            else if (runningCrc is null)
            {
                throw new InvalidDataException(
                    $"The MVCC logical-log range {range.StartOffset}..{range.EndOffset} is missing a CRC seed.");
            }

            while (pos < rangeBody.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pos = DecodeFrame(rangeBody, pos, ref runningCrc, transactions, cancellationToken);
            }

            previousRangeBoundary = (range.Generation, range.EndOffset);
        }

        if (bodyOffset != body.Length)
        {
            throw new InvalidDataException(
                "The MVCC logical-log body has trailing bytes after its advertised ranges.");
        }

        return transactions;
    }

    private static int DecodeFrame(
        ReadOnlySpan<byte> rangeBody,
        int pos,
        ref uint? runningCrc,
        List<ManagedReplicaLogicalTxn> transactions,
        CancellationToken cancellationToken)
    {
        var frameStart = pos;
        if (rangeBody.Length - pos < TxHeaderSize + TxTrailerSize)
            throw new InvalidDataException("A MVCC logical-log transaction frame is truncated.");

        var frameMagic = ReadUInt32Le(rangeBody, pos);
        var hasExtensionHeader = frameMagic == ExtFrameMagic;
        if (frameMagic != FrameMagic && !hasExtensionHeader)
        {
            throw new InvalidDataException(
                $"An MVCC logical-log transaction frame has an invalid magic at range offset {frameStart}.");
        }

        var headerSize = hasExtensionHeader ? TxExtHeaderSize : TxHeaderSize;
        if (hasExtensionHeader && rangeBody.Length - pos < TxExtHeaderSize + TxTrailerSize)
            throw new InvalidDataException("A MVCC logical-log transaction frame is truncated.");

        var payloadSizeUlong = ReadUInt64Le(rangeBody, pos + 4);
        if (payloadSizeUlong > int.MaxValue)
            throw new InvalidDataException("An MVCC logical-log recovery payload size overflows the supported buffer size.");
        var payloadSize = (int)payloadSizeUlong;
        var opCount = ReadUInt32Le(rangeBody, pos + 12);

        var extensionSize = 0;
        var extensionRecordCount = 0u;
        if (hasExtensionHeader)
        {
            var extensionSizeUlong = ReadUInt64Le(rangeBody, pos + 24);
            if (extensionSizeUlong > int.MaxValue)
                throw new InvalidDataException("An MVCC logical-log extension size overflows the supported buffer size.");
            extensionSize = (int)extensionSizeUlong;
            extensionRecordCount = ReadUInt32Le(rangeBody, pos + 32);
            var frameFlags = ReadUInt32Le(rangeBody, pos + 36);
            if ((frameFlags & ~TxFlagHasExtensionBlock) != 0)
                throw new InvalidDataException($"An MVCC logical-log transaction has unsupported flags {frameFlags:x}.");
            if (extensionSize == 0 && extensionRecordCount != 0)
            {
                throw new InvalidDataException(
                    "An MVCC logical-log frame has an extension record count without an extension block.");
            }
            if (extensionSize > 0 && (frameFlags & TxFlagHasExtensionBlock) == 0)
            {
                throw new InvalidDataException(
                    "An MVCC logical-log frame has an extension block without the extension flag.");
            }
        }

        var extensionStart = checked(pos + headerSize);
        var recoveryStart = checked(extensionStart + extensionSize);
        var trailerStart = checked(recoveryStart + payloadSize);
        var frameEnd = checked(trailerStart + TxTrailerSize);
        if (frameEnd > rangeBody.Length)
            throw new InvalidDataException("A MVCC logical-log transaction frame is truncated.");

        if (ReadUInt32Le(rangeBody, trailerStart + 4) != EndMagic)
            throw new InvalidDataException("A MVCC logical-log transaction has an invalid trailer magic.");

        if (runningCrc is { } previousCrc)
        {
            var expectedCrc = Lml3Crc32C.Append(previousCrc, rangeBody[frameStart..trailerStart]);
            var storedCrc = ReadUInt32Le(rangeBody, trailerStart);
            if (expectedCrc != storedCrc)
            {
                throw new InvalidDataException(
                    $"An MVCC logical-log transaction checksum mismatch was detected at range offset {frameStart}.");
            }

            runningCrc = storedCrc;
        }

        if (extensionSize == 0)
            return frameEnd;

        var extensionBlock = rangeBody[extensionStart..recoveryStart];
        var recoveryPayload = rangeBody[recoveryStart..trailerStart];
        var portablePayload = FindExtensionPayload(extensionBlock, extensionRecordCount, ExtensionTypePortableChanges, cancellationToken);
        if (portablePayload.Length == 0)
            return frameEnd;

        DecodePortableFrame(portablePayload, recoveryPayload, opCount, transactions);
        return frameEnd;
    }

    private static void DecodePortableFrame(
        byte[] portablePayload,
        ReadOnlySpan<byte> recoveryPayload,
        uint opCount,
        List<ManagedReplicaLogicalTxn> transactions)
    {
        var offset = 0;
        while (offset < portablePayload.Length)
        {
            var portableTxn = ReadDelimitedPortableTxn(portablePayload, ref offset);
            var txn = DecodeRecoveryOpsToLogicalTxn(portableTxn, recoveryPayload, opCount);
            if (txn.Ops.Count != 0)
                transactions.Add(txn);
        }
    }

    private static ManagedReplicaLogicalTxn DecodeRecoveryOpsToLogicalTxn(
        PortableLogicalTxn portableTxn,
        ReadOnlySpan<byte> recoveryPayload,
        uint opCount)
    {
        var objectNames = portableTxn.ObjectNames();
        var originClientId = portableTxn.OriginClientId();

        var headerOps = new List<ManagedReplicaLogicalOp>();
        var schemaDeltas = new SortedDictionary<long, SchemaRowDelta>();
        var rowOps = new List<ManagedReplicaLogicalOp>();

        var cursor = 0;
        for (var i = 0; i < opCount; i++)
        {
            if (recoveryPayload.Length - cursor < 6)
                throw new InvalidDataException("A MVCC logical-log recovery op is truncated.");

            var tag = recoveryPayload[cursor];
            var flags = recoveryPayload[cursor + 1];
            var tableId = (long)BinaryPrimitives.ReadInt32LittleEndian(recoveryPayload[(cursor + 2)..(cursor + 6)]);
            cursor += 6;

            var payloadLen = ReadSqliteVarintAsInt(recoveryPayload, ref cursor, "op payload length");
            var payloadEnd = checked(cursor + payloadLen);
            if (payloadEnd > recoveryPayload.Length)
                throw new InvalidDataException("A MVCC logical-log op payload is truncated.");
            var payload = recoveryPayload[cursor..payloadEnd];
            cursor = payloadEnd;

            ReadOnlySpan<byte> portableExtension = [];
            if ((flags & OpFlagPortableExtension) != 0)
            {
                var extensionLen = ReadSqliteVarintAsInt(recoveryPayload, ref cursor, "op extension length");
                var extensionEnd = checked(cursor + extensionLen);
                if (extensionEnd > recoveryPayload.Length)
                    throw new InvalidDataException("A MVCC logical-log op extension is truncated.");
                portableExtension = recoveryPayload[cursor..extensionEnd];
                cursor = extensionEnd;
            }

            switch (tag)
            {
                case OpUpsertTable:
                    {
                        var payloadCursor = 0;
                        var rowId = ReadSqliteVarintAsRowId(payload, ref payloadCursor);
                        var record = payload[payloadCursor..].ToArray();
                        if (tableId == SqliteSchemaTableId)
                        {
                            GetOrAddDelta(schemaDeltas, rowId).New = DecodeSchemaRow(record);
                        }
                        else if (objectNames.TryGetValue(tableId, out var tableName))
                        {
                            rowOps.Add(new ManagedReplicaLogicalOp(
                                ManagedReplicaLogicalOpType.UpsertRow,
                                tableName,
                                rowId,
                                record,
                                Sql: string.Empty,
                                UserVersion: null,
                                ApplicationId: null,
                                SchemaAction: null,
                                SchemaKind: null,
                                SchemaName: string.Empty,
                                StableTableId: 0));
                        }

                        break;
                    }
                case OpDeleteTable:
                    {
                        var payloadCursor = 0;
                        var rowId = ReadSqliteVarintAsRowId(payload, ref payloadCursor);
                        if (tableId == SqliteSchemaTableId)
                        {
                            var identityRecord = DecodeDeleteExtensionRecord(portableExtension, DeleteExtIdentityRecordField);
                            if (identityRecord.Length == 0)
                            {
                                throw new InvalidDataException(
                                    "An MVCC sqlite_schema delete is missing its portable identity record.");
                            }

                            GetOrAddDelta(schemaDeltas, rowId).Old = DecodeSchemaRow(identityRecord);
                        }
                        else if (objectNames.TryGetValue(tableId, out var tableName))
                        {
                            var primaryKeyRecord = DecodeDeleteExtensionRecord(portableExtension, DeleteExtPkRecordField);
                            rowOps.Add(new ManagedReplicaLogicalOp(
                                ManagedReplicaLogicalOpType.DeleteRow,
                                tableName,
                                rowId,
                                primaryKeyRecord,
                                Sql: string.Empty,
                                UserVersion: null,
                                ApplicationId: null,
                                SchemaAction: null,
                                SchemaKind: null,
                                SchemaName: string.Empty,
                                StableTableId: 0));
                        }

                        break;
                    }
                case OpUpsertIndex:
                case OpDeleteIndex:
                    // Index recovery ops are not replayed at the logical layer.
                    break;
                case OpUpdateHeader:
                    headerOps.Add(DecodeUpdateHeaderOp(payload));
                    break;
                default:
                    throw new InvalidDataException($"An MVCC logical-log recovery op has an unknown tag {tag}.");
            }
        }

        if (cursor != recoveryPayload.Length)
            throw new InvalidDataException("An MVCC logical-log recovery payload has trailing bytes.");

        var ops = new List<ManagedReplicaLogicalOp>(headerOps);
        AppendSchemaOps(schemaDeltas, ops);
        ops.AddRange(rowOps);

        return new ManagedReplicaLogicalTxn(portableTxn.EndOffset, portableTxn.CommitTs, ops, originClientId);
    }

    private static SchemaRowDelta GetOrAddDelta(SortedDictionary<long, SchemaRowDelta> deltas, long rowId)
    {
        if (!deltas.TryGetValue(rowId, out var delta))
        {
            delta = new SchemaRowDelta();
            deltas[rowId] = delta;
        }

        return delta;
    }

    private static void AppendSchemaOps(SortedDictionary<long, SchemaRowDelta> deltas, List<ManagedReplicaLogicalOp> ops)
    {
        foreach (var delta in deltas.Values)
        {
            if (delta.Old is { } old && delta.New is { } updated)
                ops.Add(SchemaLogicalOp(updated, ManagedReplicaLogicalSchemaAction.Refresh));
            else if (delta.New is { } created)
                ops.Add(SchemaLogicalOp(created, ManagedReplicaLogicalSchemaAction.Create));
            else if (delta.Old is { } dropped)
                ops.Add(SchemaLogicalOp(dropped, ManagedReplicaLogicalSchemaAction.Drop));
        }
    }

    private static ManagedReplicaLogicalOp SchemaLogicalOp(DecodedSchemaRow row, ManagedReplicaLogicalSchemaAction action)
        => new(
            ManagedReplicaLogicalOpType.Schema,
            TableName: string.Empty,
            RowId: 0,
            Record: [],
            Sql: action == ManagedReplicaLogicalSchemaAction.Drop ? string.Empty : row.Sql,
            UserVersion: null,
            ApplicationId: null,
            SchemaAction: action,
            SchemaKind: SchemaKindFromRowType(row.RowType),
            SchemaName: row.Name,
            StableTableId: 0);

    private static ManagedReplicaLogicalSchemaKind SchemaKindFromRowType(string rowType)
    {
        if (rowType.Equals("table", StringComparison.OrdinalIgnoreCase))
            return ManagedReplicaLogicalSchemaKind.Table;
        if (rowType.Equals("index", StringComparison.OrdinalIgnoreCase))
            return ManagedReplicaLogicalSchemaKind.Index;
        if (rowType.Equals("trigger", StringComparison.OrdinalIgnoreCase))
            return ManagedReplicaLogicalSchemaKind.Trigger;
        if (rowType.Equals("view", StringComparison.OrdinalIgnoreCase))
            return ManagedReplicaLogicalSchemaKind.View;

        throw new InvalidDataException($"An MVCC logical-log schema row has an unsupported object type '{rowType}'.");
    }

    private readonly record struct DecodedSchemaRow(string RowType, string Name, string Sql);

    private sealed class SchemaRowDelta
    {
        public DecodedSchemaRow? Old { get; set; }

        public DecodedSchemaRow? New { get; set; }
    }

    private static DecodedSchemaRow DecodeSchemaRow(byte[] record)
    {
        var values = SqliteRecordCodec.Decode(record);
        if (values.Length < 5)
        {
            throw new InvalidDataException(
                $"A sqlite_schema record must have at least 5 columns, got {values.Length}.");
        }

        return new DecodedSchemaRow(
            SchemaTextValue(values[0], "type"),
            SchemaTextValue(values[1], "name"),
            SchemaTextValue(values[4], "sql"));
    }

    private static string SchemaTextValue(Core.SqlValue value, string field)
        => value.Kind switch
        {
            Core.SqlValueKind.Text => value.AsText(),
            Core.SqlValueKind.Null => string.Empty,
            _ => throw new InvalidDataException($"sqlite_schema.{field} must be text."),
        };

    private static ManagedReplicaLogicalOp DecodeUpdateHeaderOp(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 72 || !payload[..16].SequenceEqual("SQLite format 3\0"u8))
            throw new InvalidDataException("An MVCC UPDATE_HEADER payload is invalid.");

        return new ManagedReplicaLogicalOp(
            ManagedReplicaLogicalOpType.UpdateHeader,
            TableName: string.Empty,
            RowId: 0,
            Record: [],
            Sql: string.Empty,
            // SQLite's user_version/application_id header fields are logically signed int32
            // (PRAGMA user_version/application_id both accept and round-trip negative values),
            // even though they occupy 4 raw big-endian bytes in the on-disk header.
            UserVersion: BinaryPrimitives.ReadInt32BigEndian(payload[60..64]),
            ApplicationId: BinaryPrimitives.ReadInt32BigEndian(payload[68..72]),
            SchemaAction: null,
            SchemaKind: null,
            SchemaName: string.Empty,
            StableTableId: 0);
    }

    private static byte[] DecodeDeleteExtensionRecord(ReadOnlySpan<byte> extension, ulong recordField)
    {
        var cursor = 0;
        byte[] record = [];
        while (cursor < extension.Length)
        {
            var key = ReadProtoVarint(extension, ref cursor, "delete extension");
            var field = key >> 3;
            var wireType = key & 7;
            if (field == recordField && wireType == 2)
            {
                var length = ReadProtoLength(extension, ref cursor, "delete extension record");
                var end = checked(cursor + length);
                if (end > extension.Length)
                    throw new InvalidDataException("A MVCC delete record is truncated.");
                record = extension[cursor..end].ToArray();
                cursor = end;
            }
            else
            {
                SkipProtoField(extension, ref cursor, wireType);
            }
        }

        return record;
    }

    // --- Portable protobuf message decode (PortableLogicalTxn / PortableObjectMap / PortableMeta) ---

    private sealed record PortableLogicalTxn(
        ulong EndOffset,
        ulong CommitTs,
        IReadOnlyList<string> StringTable,
        IReadOnlyList<(long MvTableId, ulong NameRef)> ObjectMap,
        IReadOnlyList<(ulong KeyRef, ulong ValueRef)> Meta)
    {
        public Dictionary<long, string> ObjectNames()
        {
            var names = new Dictionary<long, string>();
            foreach (var (mvTableId, nameRef) in ObjectMap)
                names[mvTableId] = ResolveString(nameRef, "portable object map");
            return names;
        }

        public string OriginClientId()
        {
            foreach (var (keyRef, valueRef) in Meta)
            {
                if (ResolveString(keyRef, "portable metadata key") == "client")
                    return ResolveString(valueRef, "portable metadata value");
            }

            return string.Empty;
        }

        private string ResolveString(ulong index, string context)
        {
            if (index > int.MaxValue || (int)index >= StringTable.Count)
                throw new InvalidDataException($"{context} references a missing string {index}.");
            return StringTable[(int)index];
        }
    }

    /// <summary>
    /// Reads one length-delimited <c>PortableLogicalTxn</c> message, advancing <paramref name="offset"/>
    /// past the varint length prefix and the message body.
    /// </summary>
    private static PortableLogicalTxn ReadDelimitedPortableTxn(byte[] buffer, ref int offset)
    {
        var cursor = offset;
        var length = ReadProtoLength(buffer, ref cursor, "portable logical transaction");
        var end = checked(cursor + length);
        if (end > buffer.Length)
            throw new InvalidDataException("A portable MVCC logical-log transaction message is truncated.");

        var message = buffer.AsSpan(cursor, length);
        offset = end;

        ulong? endOffset = null;
        ulong? commitTs = null;
        var stringTable = new List<string>();
        var objectMap = new List<(long, ulong)>();
        var meta = new List<(ulong, ulong)>();

        var fieldCursor = 0;
        while (fieldCursor < message.Length)
        {
            var key = ReadProtoVarint(message, ref fieldCursor, "portable transaction");
            var field = key >> 3;
            var wireType = key & 7;
            switch (field)
            {
                case 1 when wireType == 0:
                    endOffset = ReadProtoVarint(message, ref fieldCursor, "portable transaction end_offset");
                    break;
                case 2 when wireType == 0:
                    commitTs = ReadProtoVarint(message, ref fieldCursor, "portable transaction commit_ts");
                    break;
                case PortableTxFieldStringTable when wireType == 2:
                    {
                        var bytes = ReadProtoLengthDelimited(message, ref fieldCursor, "portable string table entry");
                        stringTable.Add(DecodeStrictUtf8(bytes));
                        break;
                    }
                case PortableTxFieldObjectMap when wireType == 2:
                    {
                        var bytes = ReadProtoLengthDelimited(message, ref fieldCursor, "portable object map entry");
                        objectMap.Add(ReadPortableObjectMap(bytes));
                        break;
                    }
                case PortableTxFieldMeta when wireType == 2:
                    {
                        var bytes = ReadProtoLengthDelimited(message, ref fieldCursor, "portable metadata entry");
                        meta.Add(ReadPortableMeta(bytes));
                        break;
                    }
                default:
                    SkipProtoField(message, ref fieldCursor, wireType);
                    break;
            }
        }

        return new PortableLogicalTxn(
            endOffset ?? 0,
            commitTs ?? 0,
            stringTable,
            objectMap,
            meta);
    }

    private static (long MvTableId, ulong NameRef) ReadPortableObjectMap(byte[] bytes)
    {
        long? mvTableId = null;
        ulong? nameRef = null;
        var cursor = 0;
        while (cursor < bytes.Length)
        {
            var key = ReadProtoVarint(bytes, ref cursor, "portable object map");
            var field = key >> 3;
            var wireType = key & 7;
            if (field == PortableObjectFieldMvTableId && wireType == 0)
                mvTableId = ZigZagDecode(ReadProtoVarint(bytes, ref cursor, "portable object map mv_table_id"));
            else if (field == PortableObjectFieldNameRef && wireType == 0)
                nameRef = ReadProtoVarint(bytes, ref cursor, "portable object map name_ref");
            else
                SkipProtoField(bytes, ref cursor, wireType);
        }

        return (mvTableId ?? 0, nameRef ?? 0);
    }

    private static (ulong KeyRef, ulong ValueRef) ReadPortableMeta(byte[] bytes)
    {
        ulong? keyRef = null;
        ulong? valueRef = null;
        var cursor = 0;
        while (cursor < bytes.Length)
        {
            var key = ReadProtoVarint(bytes, ref cursor, "portable metadata");
            var field = key >> 3;
            var wireType = key & 7;
            if (field == PortableMetaFieldKeyRef && wireType == 0)
                keyRef = ReadProtoVarint(bytes, ref cursor, "portable metadata key_ref");
            else if (field == PortableMetaFieldValueRef && wireType == 0)
                valueRef = ReadProtoVarint(bytes, ref cursor, "portable metadata value_ref");
            else
                SkipProtoField(bytes, ref cursor, wireType);
        }

        return (keyRef ?? 0, valueRef ?? 0);
    }

    private static long ZigZagDecode(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);

    // --- Extension record scanning ---

    private static byte[] FindExtensionPayload(
        ReadOnlySpan<byte> extensionBlock, uint recordCount, ushort wantedType, CancellationToken cancellationToken)
    {
        // Each record consumes at least its 8-byte header (a zero-length payload is legal), so no
        // more records than that can possibly fit; reject an absurd declared count up front
        // instead of looping toward discovering it, bounding this independent of recordCount.
        var maxPossibleRecords = extensionBlock.Length / ExtensionRecordHeaderSize;
        if (recordCount > (uint)maxPossibleRecords)
        {
            throw new InvalidDataException(
                "An MVCC logical-log extension block declares more records than it can possibly contain.");
        }

        var offset = 0;
        var payload = new List<byte>();
        for (var i = 0; i < recordCount; i++)
        {
            if ((i & 0x3FF) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            var headerEnd = checked(offset + ExtensionRecordHeaderSize);
            if (headerEnd > extensionBlock.Length)
                throw new InvalidDataException("An MVCC logical-log extension record header is truncated.");

            var extensionType = ReadUInt16Le(extensionBlock, offset);
            var extensionFlags = ReadUInt16Le(extensionBlock, offset + 2);
            if (extensionFlags != 0)
            {
                throw new InvalidDataException(
                    $"An MVCC logical-log extension has unsupported flags for type {extensionType}: {extensionFlags:x}.");
            }

            // Widen before narrowing: a value >= 0x80000000 must fail closed with
            // InvalidDataException, not silently become a negative int that could make
            // payloadEnd regress behind payloadStart (non-progress) or underflow into an
            // OverflowException/ArgumentException instead of the intended failure mode.
            var extensionLenRaw = ReadUInt32Le(extensionBlock, offset + 4);
            if (extensionLenRaw > int.MaxValue)
            {
                throw new InvalidDataException(
                    "An MVCC logical-log extension record length overflows the supported buffer size.");
            }

            var extensionLen = (int)extensionLenRaw;
            var payloadStart = headerEnd;
            var payloadEnd = checked(payloadStart + extensionLen);
            if (payloadEnd > extensionBlock.Length)
                throw new InvalidDataException("An MVCC logical-log extension record payload is truncated.");

            if (extensionType == wantedType)
                payload.AddRange(extensionBlock[payloadStart..payloadEnd].ToArray());

            // payloadEnd == headerEnd + (a validated non-negative length) > offset always holds,
            // so every iteration strictly advances offset and the loop is bounded by
            // extensionBlock.Length regardless of what recordCount claims.
            offset = payloadEnd;
        }

        if (offset != extensionBlock.Length)
            throw new InvalidDataException("An MVCC logical-log extension block has trailing bytes.");

        return payload.ToArray();
    }

    // --- Header + CRC seed ---

    private static uint ValidateLogHeader(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < LogHeaderSize)
            throw new InvalidDataException("An MVCC logical-log header is truncated.");
        if (ReadUInt32Le(buf, 0) != LogMagic)
            throw new InvalidDataException("An MVCC logical-log header has an invalid magic.");
        if (buf[4] != LogVersion)
            throw new InvalidDataException($"An MVCC logical-log header has an unsupported version {buf[4]}.");
        if ((buf[5] & 0b1111_1110) != 0)
            throw new InvalidDataException("An MVCC logical-log header has invalid flags.");

        var hdrLen = ReadUInt16Le(buf, 6);
        if (hdrLen != LogHeaderSize)
            throw new InvalidDataException($"An MVCC logical-log header has an invalid length {hdrLen}.");

        var storedCrc = ReadUInt32Le(buf, LogHeaderCrcStart);
        Span<byte> crcBuf = stackalloc byte[LogHeaderSize];
        buf[..LogHeaderSize].CopyTo(crcBuf);
        crcBuf[LogHeaderCrcStart..LogHeaderSize].Clear();
        if (Lml3Crc32C.Compute(crcBuf) != storedCrc)
            throw new InvalidDataException("An MVCC logical-log header checksum mismatch was detected.");

        for (var i = LogHeaderReservedStart; i < LogHeaderCrcStart; i++)
        {
            if (buf[i] != 0)
                throw new InvalidDataException("An MVCC logical-log header has non-zero reserved bytes.");
        }

        var salt = BinaryPrimitives.ReadUInt64LittleEndian(buf[LogHeaderSaltStart..LogHeaderSaltEnd]);
        Span<byte> saltBytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(saltBytes, salt);
        return Lml3Crc32C.Compute(saltBytes);
    }

    private static uint DecodeCrcSeed(byte[] seed)
    {
        if (seed.Length != 4)
            throw new InvalidDataException($"An MVCC logical-log CRC seed has an invalid length {seed.Length}.");
        return BinaryPrimitives.ReadUInt32LittleEndian(seed);
    }

    // --- Primitive readers ---

    private static uint ReadUInt32Le(ReadOnlySpan<byte> buf, int offset)
    {
        if (offset + 4 > buf.Length)
            throw new InvalidDataException("A MVCC logical-log frame is truncated.");
        return BinaryPrimitives.ReadUInt32LittleEndian(buf[offset..]);
    }

    private static ushort ReadUInt16Le(ReadOnlySpan<byte> buf, int offset)
    {
        if (offset + 2 > buf.Length)
            throw new InvalidDataException("A MVCC logical-log frame is truncated.");
        return BinaryPrimitives.ReadUInt16LittleEndian(buf[offset..]);
    }

    private static ulong ReadUInt64Le(ReadOnlySpan<byte> buf, int offset)
    {
        if (offset + 8 > buf.Length)
            throw new InvalidDataException("A MVCC logical-log frame is truncated.");
        return BinaryPrimitives.ReadUInt64LittleEndian(buf[offset..]);
    }

    private static int ReadSqliteVarintAsInt(ReadOnlySpan<byte> buf, ref int cursor, string context)
    {
        if (!SqliteVarint.TryRead(buf[cursor..], out var value, out var bytesRead))
            throw new InvalidDataException($"An MVCC logical-log {context} is invalid.");
        if (value > int.MaxValue)
            throw new InvalidDataException($"An MVCC logical-log {context} overflows the supported buffer size.");
        cursor += bytesRead;
        return (int)value;
    }

    private static long ReadSqliteVarintAsRowId(ReadOnlySpan<byte> buf, ref int cursor)
    {
        if (!SqliteVarint.TryRead(buf[cursor..], out var value, out var bytesRead))
            throw new InvalidDataException("An MVCC logical-log rowid is invalid.");
        cursor += bytesRead;
        return unchecked((long)value);
    }

    /// <summary>Reads a standard protobuf-style base-128 varint (little-endian 7-bit groups).</summary>
    private static ulong ReadProtoVarint(ReadOnlySpan<byte> buf, ref int cursor, string context)
    {
        ulong value = 0;
        var shift = 0;
        while (cursor < buf.Length)
        {
            var b = buf[cursor];
            cursor++;
            value |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0)
                return value;
            shift += 7;
            if (shift >= 64)
                throw new InvalidDataException($"An MVCC logical-log {context} varint overflows 64 bits.");
        }

        throw new InvalidDataException($"An MVCC logical-log {context} varint is truncated.");
    }

    private static int ReadProtoLength(ReadOnlySpan<byte> buf, ref int cursor, string context)
    {
        var length = ReadProtoVarint(buf, ref cursor, context);
        if (length > int.MaxValue)
            throw new InvalidDataException($"An MVCC logical-log {context} length overflows the supported buffer size.");
        return (int)length;
    }

    private static byte[] ReadProtoLengthDelimited(ReadOnlySpan<byte> buf, ref int cursor, string context)
    {
        var length = ReadProtoLength(buf, ref cursor, context);
        var end = checked(cursor + length);
        if (end > buf.Length)
            throw new InvalidDataException($"An MVCC logical-log {context} is truncated.");
        var bytes = buf[cursor..end].ToArray();
        cursor = end;
        return bytes;
    }

    private static void SkipProtoField(ReadOnlySpan<byte> buf, ref int cursor, ulong wireType)
    {
        switch (wireType)
        {
            case 0:
                ReadProtoVarint(buf, ref cursor, "unknown field");
                break;
            case 1:
                if (cursor + 8 > buf.Length)
                    throw new InvalidDataException("An MVCC logical-log unknown 64-bit field is truncated.");
                cursor += 8;
                break;
            case 2:
                _ = ReadProtoLengthDelimited(buf, ref cursor, "unknown field");
                break;
            case 5:
                if (cursor + 4 > buf.Length)
                    throw new InvalidDataException("An MVCC logical-log unknown 32-bit field is truncated.");
                cursor += 4;
                break;
            default:
                throw new InvalidDataException($"An MVCC logical-log message has an unsupported wire type {wireType}.");
        }
    }

    private static string DecodeStrictUtf8(byte[] bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("An MVCC logical-log portable string is not valid UTF-8.", exception);
        }
    }
}

/// <summary>Standalone CRC32C (Castagnoli) implementation supporting chained continuation.</summary>
internal static class Lml3Crc32C
{
    private static readonly uint[] Table = CreateTable();

    /// <summary>Computes a fresh CRC32C checksum of <paramref name="data"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => Append(0, data);

    /// <summary>
    /// Continues a CRC32C computation from a previously finalized checksum, matching Rust's
    /// <c>crc32c::crc32c_append</c> (a fresh computation is equivalent to <c>Append(0, data)</c>).
    /// </summary>
    public static uint Append(uint previousCrc, ReadOnlySpan<byte> data)
    {
        var crc = previousCrc ^ 0xFFFFFFFFu;
        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] CreateTable()
    {
        const uint poly = 0x82F63B78u; // reflected Castagnoli
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var crc = i;
            for (var j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
            table[i] = crc;
        }

        return table;
    }
}
