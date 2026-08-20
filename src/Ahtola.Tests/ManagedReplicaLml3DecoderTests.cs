using AwesomeAssertions;

namespace Ahtola.Tests;

public sealed class ManagedReplicaLml3DecoderTests
{
    [Test]
    public void DecodesASingleUpsertRowFromAMinimalFrame()
    {
        var salt = 0xAAAABBBBCCCCDDDDUL;
        var header = Lml3TestBuilder.BuildHeader(salt);
        var crc = Lml3TestBuilder.HeaderSeedCrc(salt);

        var record = SqliteRecordCodecTestHelper.Encode("hello");
        var op = Lml3TestBuilder.BuildRecoveryOp(
            tag: 0, // UPSERT_TABLE
            flags: 0,
            tableId: -2,
            payload: Lml3TestBuilder.UpsertTablePayload(rowId: 7, record));

        var portableTxn = Lml3TestBuilder.BuildPortableLogicalTxn(
            endOffset: 200,
            commitTs: 55,
            strings: ["items"],
            objectMap: [(-2, 0)]);
        var extensionRecord = Lml3TestBuilder.BuildExtensionRecord(
            Lml3TestBuilder.PortableChangesExtensionType,
            Lml3TestBuilder.Delimited(portableTxn));

        var frame = Lml3TestBuilder.BuildFrame(ref crc, op, opCount: 1, extensionBlock: extensionRecord);
        var body = header.Concat(frame).ToArray();

        var ranges = new[]
        {
            new ManagedReplicaLogicalLogRange(1, 0, (ulong)body.Length, StartsWithHeader: true, CrcSeed: null),
        };

        var txns = ManagedReplicaLml3Decoder.Decode(ranges, body, CancellationToken.None);
        txns.Should().ContainSingle();
        txns[0].CommitTs.Should().Be(55);
        txns[0].EndOffset.Should().Be(200);
        txns[0].Ops.Should().ContainSingle();
        var decodedOp = txns[0].Ops[0];
        decodedOp.OpType.Should().Be(ManagedReplicaLogicalOpType.UpsertRow);
        decodedOp.TableName.Should().Be("items");
        decodedOp.RowId.Should().Be(7);
        decodedOp.Record.Should().Equal(record);
    }

    [Test]
    public void FramesWithoutAnExtensionBlockYieldNoOperationsButStillAdvanceCrc()
    {
        var salt = 1UL;
        var header = Lml3TestBuilder.BuildHeader(salt);
        var crc = Lml3TestBuilder.HeaderSeedCrc(salt);

        var op = Lml3TestBuilder.BuildRecoveryOp(0, 0, -2, Lml3TestBuilder.UpsertTablePayload(1, [0x00]));
        var compactFrame = Lml3TestBuilder.BuildFrame(ref crc, op, opCount: 1); // no extension block => compact frame

        var portableTxn = Lml3TestBuilder.BuildPortableLogicalTxn(1, 1, ["t"], [(-2, 0)]);
        var extRecord = Lml3TestBuilder.BuildExtensionRecord(1, Lml3TestBuilder.Delimited(portableTxn));
        var op2 = Lml3TestBuilder.BuildRecoveryOp(0, 0, -2, Lml3TestBuilder.UpsertTablePayload(2, [0x00]));
        var extendedFrame = Lml3TestBuilder.BuildFrame(ref crc, op2, opCount: 1, extensionBlock: extRecord);

        var body = header.Concat(compactFrame).Concat(extendedFrame).ToArray();
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, (ulong)body.Length, true, null) };

        var txns = ManagedReplicaLml3Decoder.Decode(ranges, body, CancellationToken.None);
        txns.Should().ContainSingle("the compact frame carries no portable payload and must be skipped");
        txns[0].Ops[0].RowId.Should().Be(2);
    }

    [Test]
    public void UnknownWireLevelTableIdSilentlySkipsTheRowOp()
    {
        var salt = 2UL;
        var header = Lml3TestBuilder.BuildHeader(salt);
        var crc = Lml3TestBuilder.HeaderSeedCrc(salt);

        // table_id -5 is not present in the object map below.
        var op = Lml3TestBuilder.BuildRecoveryOp(0, 0, -5, Lml3TestBuilder.UpsertTablePayload(1, [0x00]));
        var portableTxn = Lml3TestBuilder.BuildPortableLogicalTxn(1, 1, ["known"], [(-2, 0)]);
        var extRecord = Lml3TestBuilder.BuildExtensionRecord(1, Lml3TestBuilder.Delimited(portableTxn));
        var frame = Lml3TestBuilder.BuildFrame(ref crc, op, opCount: 1, extensionBlock: extRecord);

        var body = header.Concat(frame).ToArray();
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, (ulong)body.Length, true, null) };

        var txns = ManagedReplicaLml3Decoder.Decode(ranges, body, CancellationToken.None);
        txns.Should().BeEmpty("the op referenced an unmapped table id and must be dropped, not errored");
    }

    [Test]
    public void DecodesSchemaCreateFromASqliteSchemaUpsert()
    {
        var salt = 3UL;
        var header = Lml3TestBuilder.BuildHeader(salt);
        var crc = Lml3TestBuilder.HeaderSeedCrc(salt);

        var schemaRecord = Lml3TestBuilder.SchemaRecord("table", "widgets", 5, "CREATE TABLE widgets(id INTEGER PRIMARY KEY)");
        var op = Lml3TestBuilder.BuildRecoveryOp(0, 0, -1, Lml3TestBuilder.UpsertTablePayload(rowId: 1, schemaRecord));
        var portableTxn = Lml3TestBuilder.BuildPortableLogicalTxn(1, 1);
        var extRecord = Lml3TestBuilder.BuildExtensionRecord(1, Lml3TestBuilder.Delimited(portableTxn));
        var frame = Lml3TestBuilder.BuildFrame(ref crc, op, 1, extRecord);

        var body = header.Concat(frame).ToArray();
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, (ulong)body.Length, true, null) };
        var txns = ManagedReplicaLml3Decoder.Decode(ranges, body, CancellationToken.None);

        txns.Should().ContainSingle();
        var op0 = txns[0].Ops.Should().ContainSingle().Subject;
        op0.OpType.Should().Be(ManagedReplicaLogicalOpType.Schema);
        op0.SchemaAction.Should().Be(ManagedReplicaLogicalSchemaAction.Create);
        op0.SchemaKind.Should().Be(ManagedReplicaLogicalSchemaKind.Table);
        op0.SchemaName.Should().Be("widgets");
        op0.Sql.Should().Be("CREATE TABLE widgets(id INTEGER PRIMARY KEY)");
    }

    [Test]
    public void DecodesSchemaRefreshWhenTheSameRowidHasAnOldAndNewImage()
    {
        var salt = 4UL;
        var header = Lml3TestBuilder.BuildHeader(salt);
        var crc = Lml3TestBuilder.HeaderSeedCrc(salt);

        var oldRecord = Lml3TestBuilder.SchemaRecord("table", "widgets", 5, "CREATE TABLE widgets(id INTEGER PRIMARY KEY)");
        var newRecord = Lml3TestBuilder.SchemaRecord("table", "widgets", 5, "CREATE TABLE widgets(id INTEGER PRIMARY KEY, note TEXT)");
        var deleteExtension = Lml3TestBuilder.DeleteExtension(1, oldRecord); // identity record field = 1
        var deleteOp = Lml3TestBuilder.BuildRecoveryOp(1, 2 /* PORTABLE_EXTENSION flag */, -1, Lml3TestBuilder.DeleteTablePayload(1), deleteExtension);
        var upsertOp = Lml3TestBuilder.BuildRecoveryOp(0, 0, -1, Lml3TestBuilder.UpsertTablePayload(1, newRecord));
        var recoveryPayload = deleteOp.Concat(upsertOp).ToArray();

        var portableTxn = Lml3TestBuilder.BuildPortableLogicalTxn(1, 1);
        var extRecord = Lml3TestBuilder.BuildExtensionRecord(1, Lml3TestBuilder.Delimited(portableTxn));
        var frame = Lml3TestBuilder.BuildFrame(ref crc, recoveryPayload, opCount: 2, extensionBlock: extRecord);

        var body = header.Concat(frame).ToArray();
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, (ulong)body.Length, true, null) };
        var txns = ManagedReplicaLml3Decoder.Decode(ranges, body, CancellationToken.None);

        var op0 = txns.Should().ContainSingle().Subject.Ops.Should().ContainSingle().Subject;
        op0.SchemaAction.Should().Be(ManagedReplicaLogicalSchemaAction.Refresh);
        op0.Sql.Should().Be("CREATE TABLE widgets(id INTEGER PRIMARY KEY, note TEXT)");
    }

    [Test]
    public void DecodesSchemaDropFromASqliteSchemaDeleteWithoutAMatchingInsert()
    {
        var salt = 5UL;
        var header = Lml3TestBuilder.BuildHeader(salt);
        var crc = Lml3TestBuilder.HeaderSeedCrc(salt);

        var oldRecord = Lml3TestBuilder.SchemaRecord("table", "widgets", 5, "CREATE TABLE widgets(id INTEGER PRIMARY KEY)");
        var deleteExtension = Lml3TestBuilder.DeleteExtension(1, oldRecord);
        var deleteOp = Lml3TestBuilder.BuildRecoveryOp(1, 2, -1, Lml3TestBuilder.DeleteTablePayload(1), deleteExtension);

        var portableTxn = Lml3TestBuilder.BuildPortableLogicalTxn(1, 1);
        var extRecord = Lml3TestBuilder.BuildExtensionRecord(1, Lml3TestBuilder.Delimited(portableTxn));
        var frame = Lml3TestBuilder.BuildFrame(ref crc, deleteOp, 1, extRecord);

        var body = header.Concat(frame).ToArray();
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, (ulong)body.Length, true, null) };
        var txns = ManagedReplicaLml3Decoder.Decode(ranges, body, CancellationToken.None);

        var op0 = txns.Should().ContainSingle().Subject.Ops.Should().ContainSingle().Subject;
        op0.SchemaAction.Should().Be(ManagedReplicaLogicalSchemaAction.Drop);
        op0.Sql.Should().BeEmpty();
        op0.SchemaName.Should().Be("widgets");
    }

    [Test]
    public void DeleteRowWithoutAPortableExtensionCarriesNoKeyRecord()
    {
        var salt = 6UL;
        var header = Lml3TestBuilder.BuildHeader(salt);
        var crc = Lml3TestBuilder.HeaderSeedCrc(salt);

        var op = Lml3TestBuilder.BuildRecoveryOp(1, 0, -2, Lml3TestBuilder.DeleteTablePayload(9)); // no extension
        var portableTxn = Lml3TestBuilder.BuildPortableLogicalTxn(1, 1, ["t"], [(-2, 0)]);
        var extRecord = Lml3TestBuilder.BuildExtensionRecord(1, Lml3TestBuilder.Delimited(portableTxn));
        var frame = Lml3TestBuilder.BuildFrame(ref crc, op, 1, extRecord);

        var body = header.Concat(frame).ToArray();
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, (ulong)body.Length, true, null) };
        var txns = ManagedReplicaLml3Decoder.Decode(ranges, body, CancellationToken.None);

        var op0 = txns.Should().ContainSingle().Subject.Ops.Should().ContainSingle().Subject;
        op0.OpType.Should().Be(ManagedReplicaLogicalOpType.DeleteRow);
        op0.RowId.Should().Be(9);
        op0.Record.Should().BeEmpty();
    }

    [Test]
    public void DeleteRowWithAPkExtensionCarriesTheProjectedKeyRecord()
    {
        var salt = 7UL;
        var header = Lml3TestBuilder.BuildHeader(salt);
        var crc = Lml3TestBuilder.HeaderSeedCrc(salt);

        var pkRecord = SqliteRecordCodecTestHelper.Encode("pk-value");
        var extension = Lml3TestBuilder.DeleteExtension(2, pkRecord); // PK record field = 2
        var op = Lml3TestBuilder.BuildRecoveryOp(1, 2, -2, Lml3TestBuilder.DeleteTablePayload(9), extension);
        var portableTxn = Lml3TestBuilder.BuildPortableLogicalTxn(1, 1, ["t"], [(-2, 0)]);
        var extRecord = Lml3TestBuilder.BuildExtensionRecord(1, Lml3TestBuilder.Delimited(portableTxn));
        var frame = Lml3TestBuilder.BuildFrame(ref crc, op, 1, extRecord);

        var body = header.Concat(frame).ToArray();
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, (ulong)body.Length, true, null) };
        var txns = ManagedReplicaLml3Decoder.Decode(ranges, body, CancellationToken.None);

        var op0 = txns.Should().ContainSingle().Subject.Ops.Should().ContainSingle().Subject;
        op0.Record.Should().Equal(pkRecord);
    }

    [Test]
    public void IndexRecoveryOpsAreSkippedEntirely()
    {
        var salt = 8UL;
        var header = Lml3TestBuilder.BuildHeader(salt);
        var crc = Lml3TestBuilder.HeaderSeedCrc(salt);

        var upsertIndex = Lml3TestBuilder.BuildRecoveryOp(2, 0, -3, [0x01, 0x02]);
        var deleteIndex = Lml3TestBuilder.BuildRecoveryOp(3, 0, -3, [0x01]);
        var recoveryPayload = upsertIndex.Concat(deleteIndex).ToArray();
        var portableTxn = Lml3TestBuilder.BuildPortableLogicalTxn(1, 1);
        var extRecord = Lml3TestBuilder.BuildExtensionRecord(1, Lml3TestBuilder.Delimited(portableTxn));
        var frame = Lml3TestBuilder.BuildFrame(ref crc, recoveryPayload, 2, extRecord);

        var body = header.Concat(frame).ToArray();
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, (ulong)body.Length, true, null) };
        var txns = ManagedReplicaLml3Decoder.Decode(ranges, body, CancellationToken.None);
        txns.Should().BeEmpty();
    }

    [Test]
    public void DecodesAnUpdateHeaderOp()
    {
        var salt = 9UL;
        var header = Lml3TestBuilder.BuildHeader(salt);
        var crc = Lml3TestBuilder.HeaderSeedCrc(salt);

        var payload = Lml3TestBuilder.UpdateHeaderPayload(userVersion: 7, applicationId: 99);
        var op = Lml3TestBuilder.BuildRecoveryOp(4, 0, -1, payload);
        var portableTxn = Lml3TestBuilder.BuildPortableLogicalTxn(1, 1);
        var extRecord = Lml3TestBuilder.BuildExtensionRecord(1, Lml3TestBuilder.Delimited(portableTxn));
        var frame = Lml3TestBuilder.BuildFrame(ref crc, op, 1, extRecord);

        var body = header.Concat(frame).ToArray();
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, (ulong)body.Length, true, null) };
        var txns = ManagedReplicaLml3Decoder.Decode(ranges, body, CancellationToken.None);

        var op0 = txns.Should().ContainSingle().Subject.Ops.Should().ContainSingle().Subject;
        op0.OpType.Should().Be(ManagedReplicaLogicalOpType.UpdateHeader);
        op0.UserVersion.Should().Be(7);
        op0.ApplicationId.Should().Be(99);
    }

    [Test]
    public void DecodesNegativeUserVersionAndApplicationIdAsSignedInt32()
    {
        // SQLite's user_version/application_id are signed int32 values that round-trip negative
        // numbers through PRAGMA; the wire bytes must be read as a two's-complement signed value
        // (BinaryPrimitives.ReadInt32BigEndian), not as an unsigned value that would corrupt a
        // negative number into a huge positive one.
        var salt = 58UL;
        var header = Lml3TestBuilder.BuildHeader(salt);
        var crc = Lml3TestBuilder.HeaderSeedCrc(salt);

        var payload = Lml3TestBuilder.UpdateHeaderPayload(userVersion: -1, applicationId: int.MinValue);
        var op = Lml3TestBuilder.BuildRecoveryOp(4, 0, -1, payload);
        var portableTxn = Lml3TestBuilder.BuildPortableLogicalTxn(1, 1);
        var extRecord = Lml3TestBuilder.BuildExtensionRecord(1, Lml3TestBuilder.Delimited(portableTxn));
        var frame = Lml3TestBuilder.BuildFrame(ref crc, op, 1, extRecord);

        var body = header.Concat(frame).ToArray();
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, (ulong)body.Length, true, null) };
        var txns = ManagedReplicaLml3Decoder.Decode(ranges, body, CancellationToken.None);

        var op0 = txns.Should().ContainSingle().Subject.Ops.Should().ContainSingle().Subject;
        op0.UserVersion.Should().Be(-1);
        op0.ApplicationId.Should().Be(int.MinValue);
    }

    [Test]
    public void UnknownRecoveryOpTagIsRejected()
    {
        var salt = 10UL;
        var header = Lml3TestBuilder.BuildHeader(salt);
        var crc = Lml3TestBuilder.HeaderSeedCrc(salt);
        var op = Lml3TestBuilder.BuildRecoveryOp(99, 0, -1, [0x00]);
        var frame = Lml3TestBuilder.BuildFrame(ref crc, op, 1, Lml3TestBuilder.BuildExtensionRecord(1, Lml3TestBuilder.Delimited(Lml3TestBuilder.BuildPortableLogicalTxn(1, 1))));

        var body = header.Concat(frame).ToArray();
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, (ulong)body.Length, true, null) };
        Assert.Throws<InvalidDataException>(() => ManagedReplicaLml3Decoder.Decode(ranges, body, CancellationToken.None));
    }

    [Test]
    public void MultiplePortableTransactionsInOneFrameAreAllDecoded()
    {
        var salt = 11UL;
        var header = Lml3TestBuilder.BuildHeader(salt);
        var crc = Lml3TestBuilder.HeaderSeedCrc(salt);

        var op1 = Lml3TestBuilder.BuildRecoveryOp(0, 0, -2, Lml3TestBuilder.UpsertTablePayload(1, [0x00]));
        var op2 = Lml3TestBuilder.BuildRecoveryOp(0, 0, -2, Lml3TestBuilder.UpsertTablePayload(2, [0x00]));
        var recoveryPayload = op1.Concat(op2).ToArray();

        var txnA = Lml3TestBuilder.BuildPortableLogicalTxn(10, 100, ["t"], [(-2, 0)]);
        var txnB = Lml3TestBuilder.BuildPortableLogicalTxn(20, 200, ["t"], [(-2, 0)]);
        var portablePayload = Lml3TestBuilder.Delimited(txnA).Concat(Lml3TestBuilder.Delimited(txnB)).ToArray();
        var extRecord = Lml3TestBuilder.BuildExtensionRecord(1, portablePayload);
        var frame = Lml3TestBuilder.BuildFrame(ref crc, recoveryPayload, 2, extRecord);

        var body = header.Concat(frame).ToArray();
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, (ulong)body.Length, true, null) };
        var txns = ManagedReplicaLml3Decoder.Decode(ranges, body, CancellationToken.None);

        txns.Should().HaveCount(2);
        txns[0].CommitTs.Should().Be(100);
        txns[1].CommitTs.Should().Be(200);
        // Both portable txns share the SAME recovery payload/op_count (matching upstream), so both
        // decode all ops in that frame.
        txns[0].Ops.Select(o => o.RowId).Should().Equal(1, 2);
        txns[1].Ops.Select(o => o.RowId).Should().Equal(1, 2);
    }

    [Test]
    public void UnknownExtensionRecordTypeIsIgnored()
    {
        var salt = 12UL;
        var header = Lml3TestBuilder.BuildHeader(salt);
        var crc = Lml3TestBuilder.HeaderSeedCrc(salt);
        var op = Lml3TestBuilder.BuildRecoveryOp(0, 0, -2, Lml3TestBuilder.UpsertTablePayload(1, [0x00]));

        var unknownRecord = Lml3TestBuilder.BuildExtensionRecord(99, [1, 2, 3]);
        var portableTxn = Lml3TestBuilder.BuildPortableLogicalTxn(1, 1, ["t"], [(-2, 0)]);
        var knownRecord = Lml3TestBuilder.BuildExtensionRecord(1, Lml3TestBuilder.Delimited(portableTxn));
        var extensionBlock = unknownRecord.Concat(knownRecord).ToArray();

        var frame = Lml3TestBuilder.BuildFrameWithExtensionRecordCount(ref crc, op, 1, extensionBlock, extensionRecordCount: 2);

        var body = header.Concat(frame).ToArray();
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, (ulong)body.Length, true, null) };
        var txns = ManagedReplicaLml3Decoder.Decode(ranges, body, CancellationToken.None);
        txns.Should().ContainSingle();
    }

    [Test]
    public void UnknownFieldsInThePortableTransactionMessageAreSkipped()
    {
        var salt = 13UL;
        var header = Lml3TestBuilder.BuildHeader(salt);
        var crc = Lml3TestBuilder.HeaderSeedCrc(salt);
        var op = Lml3TestBuilder.BuildRecoveryOp(0, 0, -2, Lml3TestBuilder.UpsertTablePayload(1, [0x00]));

        var txnBytes = Lml3TestBuilder.BuildPortableLogicalTxn(1, 1, ["t"], [(-2, 0)]).ToList();
        // Append an unknown field (tag 15, varint wire type) that a forward-compatible decoder must skip.
        txnBytes.Add((byte)((15 << 3) | 0));
        txnBytes.Add(7);
        var extRecord = Lml3TestBuilder.BuildExtensionRecord(1, Lml3TestBuilder.Delimited(txnBytes.ToArray()));
        var frame = Lml3TestBuilder.BuildFrame(ref crc, op, 1, extRecord);

        var body = header.Concat(frame).ToArray();
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, (ulong)body.Length, true, null) };
        var txns = ManagedReplicaLml3Decoder.Decode(ranges, body, CancellationToken.None);
        txns.Should().ContainSingle();
    }

    // --- Malformed input rejection ---

    [Test]
    public void RejectsAnInvalidHeaderMagic()
    {
        var header = Lml3TestBuilder.BuildHeader(1);
        header[0] = 0xFF;
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, (ulong)header.Length, true, null) };
        Assert.Throws<InvalidDataException>(() => ManagedReplicaLml3Decoder.Decode(ranges, header, CancellationToken.None))
            .Message.Should().Contain("magic");
    }

    [Test]
    public void RejectsAnUnsupportedHeaderVersion()
    {
        var header = Lml3TestBuilder.BuildHeader(1, versionOverride: 2);
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, (ulong)header.Length, true, null) };
        Assert.Throws<InvalidDataException>(() => ManagedReplicaLml3Decoder.Decode(ranges, header, CancellationToken.None))
            .Message.Should().Contain("version");
    }

    [Test]
    public void RejectsNonZeroReservedFlagBits()
    {
        var header = Lml3TestBuilder.BuildHeader(1, flagsOverride: 0b10);
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, (ulong)header.Length, true, null) };
        Assert.Throws<InvalidDataException>(() => ManagedReplicaLml3Decoder.Decode(ranges, header, CancellationToken.None))
            .Message.Should().Contain("flags");
    }

    [Test]
    public void RejectsAnInvalidHeaderLength()
    {
        var header = Lml3TestBuilder.BuildHeader(1, hdrLenOverride: 40);
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, (ulong)header.Length, true, null) };
        Assert.Throws<InvalidDataException>(() => ManagedReplicaLml3Decoder.Decode(ranges, header, CancellationToken.None))
            .Message.Should().Contain("length");
    }

    [Test]
    public void RejectsNonZeroReservedBytes()
    {
        var header = Lml3TestBuilder.BuildHeader(1, corruptReserved: true);
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, (ulong)header.Length, true, null) };
        Assert.Throws<InvalidDataException>(() => ManagedReplicaLml3Decoder.Decode(ranges, header, CancellationToken.None))
            .Message.Should().Contain("reserved");
    }

    [Test]
    public void RejectsAHeaderChecksumMismatch()
    {
        var header = Lml3TestBuilder.BuildHeader(1);
        header[52] ^= 0xFF; // corrupt one CRC byte without touching anything else
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, (ulong)header.Length, true, null) };
        Assert.Throws<InvalidDataException>(() => ManagedReplicaLml3Decoder.Decode(ranges, header, CancellationToken.None))
            .Message.Should().Contain("checksum");
    }

    [Test]
    public void RejectsAnInvalidFrameMagic()
    {
        var salt = 20UL;
        var header = Lml3TestBuilder.BuildHeader(salt);
        var crc = Lml3TestBuilder.HeaderSeedCrc(salt);
        var frame = Lml3TestBuilder.BuildFrame(ref crc, [], 0);
        frame[0] = 0xFF;
        var body = header.Concat(frame).ToArray();
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, (ulong)body.Length, true, null) };
        Assert.Throws<InvalidDataException>(() => ManagedReplicaLml3Decoder.Decode(ranges, body, CancellationToken.None))
            .Message.Should().Contain("magic");
    }

    [Test]
    public void RejectsAnInvalidTrailerMagic()
    {
        var salt = 21UL;
        var header = Lml3TestBuilder.BuildHeader(salt);
        var crc = Lml3TestBuilder.HeaderSeedCrc(salt);
        var frame = Lml3TestBuilder.BuildFrame(ref crc, [], 0, corruptTrailerMagic: 0xDEADBEEF);
        var body = header.Concat(frame).ToArray();
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, (ulong)body.Length, true, null) };
        Assert.Throws<InvalidDataException>(() => ManagedReplicaLml3Decoder.Decode(ranges, body, CancellationToken.None))
            .Message.Should().Contain("trailer");
    }

    [Test]
    public void RejectsATransactionChecksumMismatch()
    {
        var salt = 22UL;
        var header = Lml3TestBuilder.BuildHeader(salt);
        var crc = Lml3TestBuilder.HeaderSeedCrc(salt);
        var frame = Lml3TestBuilder.BuildFrame(ref crc, [], 0, corruptTrailerCrc: 0x12345678);
        var body = header.Concat(frame).ToArray();
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, (ulong)body.Length, true, null) };
        Assert.Throws<InvalidDataException>(() => ManagedReplicaLml3Decoder.Decode(ranges, body, CancellationToken.None))
            .Message.Should().Contain("checksum");
    }

    [Test]
    public void RejectsATruncatedFrame()
    {
        var salt = 23UL;
        var header = Lml3TestBuilder.BuildHeader(salt);
        var crc = Lml3TestBuilder.HeaderSeedCrc(salt);
        var frame = Lml3TestBuilder.BuildFrame(ref crc, [], 0);
        var body = header.Concat(frame[..^2]).ToArray(); // chop off the last 2 bytes of the trailer
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, (ulong)body.Length, true, null) };
        Assert.Throws<InvalidDataException>(() => ManagedReplicaLml3Decoder.Decode(ranges, body, CancellationToken.None))
            .Message.Should().Contain("truncated");
    }

    [Test]
    public void RejectsUnsupportedFrameFlags()
    {
        var salt = 24UL;
        var header = Lml3TestBuilder.BuildHeader(salt);
        var crc = Lml3TestBuilder.HeaderSeedCrc(salt);
        var extRecord = Lml3TestBuilder.BuildExtensionRecord(1, Lml3TestBuilder.Delimited(Lml3TestBuilder.BuildPortableLogicalTxn(1, 1)));
        var frame = Lml3TestBuilder.BuildFrame(ref crc, [], 0, extensionBlock: extRecord, frameFlagsOverride: 0xFF);
        var body = header.Concat(frame).ToArray();
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, (ulong)body.Length, true, null) };
        Assert.Throws<InvalidDataException>(() => ManagedReplicaLml3Decoder.Decode(ranges, body, CancellationToken.None))
            .Message.Should().Contain("flags");
    }

    [Test]
    public void RejectsTrailingBytesAfterAdvertisedRanges()
    {
        var salt = 25UL;
        var header = Lml3TestBuilder.BuildHeader(salt);
        var crc = Lml3TestBuilder.HeaderSeedCrc(salt);
        var frame = Lml3TestBuilder.BuildFrame(ref crc, [], 0);
        var body = header.Concat(frame).Append((byte)0x00).ToArray(); // extra trailing byte
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, (ulong)(body.Length - 1), true, null) };
        Assert.Throws<InvalidDataException>(() => ManagedReplicaLml3Decoder.Decode(ranges, body, CancellationToken.None))
            .Message.Should().Contain("trailing");
    }

    [Test]
    public void RejectsARangeShorterThanTheAdvertisedBody()
    {
        var salt = 26UL;
        var header = Lml3TestBuilder.BuildHeader(salt);
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, (ulong)header.Length + 100, true, null) };
        Assert.Throws<InvalidDataException>(() => ManagedReplicaLml3Decoder.Decode(ranges, header, CancellationToken.None))
            .Message.Should().Contain("shorter");
    }

    [Test]
    public void RejectsAnEmptyRangeList()
    {
        Assert.Throws<InvalidDataException>(() => ManagedReplicaLml3Decoder.Decode([], [], CancellationToken.None))
            .Message.Should().Contain("no ranges");
    }

    [Test]
    public void ContiguousRangesContinueTheCrcChainWithoutANewSeed()
    {
        var salt = 30UL;
        var header = Lml3TestBuilder.BuildHeader(salt);
        var crc = Lml3TestBuilder.HeaderSeedCrc(salt);
        var op1 = Lml3TestBuilder.BuildRecoveryOp(0, 0, -2, Lml3TestBuilder.UpsertTablePayload(1, [0x00]));
        var portableTxn1 = Lml3TestBuilder.BuildPortableLogicalTxn(1, 1, ["t"], [(-2, 0)]);
        var frame1 = Lml3TestBuilder.BuildFrame(ref crc, op1, 1, Lml3TestBuilder.BuildExtensionRecord(1, Lml3TestBuilder.Delimited(portableTxn1)));

        var op2 = Lml3TestBuilder.BuildRecoveryOp(0, 0, -2, Lml3TestBuilder.UpsertTablePayload(2, [0x00]));
        var portableTxn2 = Lml3TestBuilder.BuildPortableLogicalTxn(2, 2, ["t"], [(-2, 0)]);
        var frame2 = Lml3TestBuilder.BuildFrame(ref crc, op2, 1, Lml3TestBuilder.BuildExtensionRecord(1, Lml3TestBuilder.Delimited(portableTxn2)));

        var range1Bytes = header.Concat(frame1).ToArray();
        var range2Bytes = frame2;
        var body = range1Bytes.Concat(range2Bytes).ToArray();

        var ranges = new[]
        {
            new ManagedReplicaLogicalLogRange(1, 0, (ulong)range1Bytes.Length, true, null),
            new ManagedReplicaLogicalLogRange(1, (ulong)range1Bytes.Length, (ulong)(range1Bytes.Length + range2Bytes.Length), false, null),
        };

        var txns = ManagedReplicaLml3Decoder.Decode(ranges, body, CancellationToken.None);
        txns.Should().HaveCount(2);
    }

    [Test]
    public void NonContiguousRangeWithoutACrcSeedIsRejected()
    {
        var salt = 31UL;
        var header = Lml3TestBuilder.BuildHeader(salt);
        var crc = Lml3TestBuilder.HeaderSeedCrc(salt);
        var frame1 = Lml3TestBuilder.BuildFrame(ref crc, [], 0);
        var frame2 = Lml3TestBuilder.BuildFrame(ref crc, [], 0);

        var range1Bytes = header.Concat(frame1).ToArray();
        var body = range1Bytes.Concat(frame2).ToArray();

        // Generation gap (100 -> 200) breaks continuity with no crc_seed for the new range.
        var ranges = new[]
        {
            new ManagedReplicaLogicalLogRange(1, 0, (ulong)range1Bytes.Length, true, null),
            new ManagedReplicaLogicalLogRange(2, 500, (ulong)(500 + frame2.Length), false, null),
        };

        Assert.Throws<InvalidDataException>(() => ManagedReplicaLml3Decoder.Decode(ranges, body, CancellationToken.None))
            .Message.Should().Contain("CRC seed");
    }

    [Test]
    public void ANonContiguousRangeWithAnExplicitCrcSeedContinuesCorrectly()
    {
        var salt = 32UL;
        var header = Lml3TestBuilder.BuildHeader(salt);
        var crc = Lml3TestBuilder.HeaderSeedCrc(salt);
        var op1 = Lml3TestBuilder.BuildRecoveryOp(0, 0, -2, Lml3TestBuilder.UpsertTablePayload(1, [0x00]));
        var portableTxn1 = Lml3TestBuilder.BuildPortableLogicalTxn(1, 1, ["t"], [(-2, 0)]);
        var frame1 = Lml3TestBuilder.BuildFrame(ref crc, op1, 1, Lml3TestBuilder.BuildExtensionRecord(1, Lml3TestBuilder.Delimited(portableTxn1)));
        var range1Bytes = header.Concat(frame1).ToArray();

        // Seed the second (non-contiguous, e.g. server-truncated) range explicitly with the CRC as of
        // the end of the first range.
        var seedBytes = BitConverter.GetBytes(crc);
        var op2 = Lml3TestBuilder.BuildRecoveryOp(0, 0, -2, Lml3TestBuilder.UpsertTablePayload(2, [0x00]));
        var portableTxn2 = Lml3TestBuilder.BuildPortableLogicalTxn(2, 2, ["t"], [(-2, 0)]);
        var frame2 = Lml3TestBuilder.BuildFrame(ref crc, op2, 1, Lml3TestBuilder.BuildExtensionRecord(1, Lml3TestBuilder.Delimited(portableTxn2)));

        var body = range1Bytes.Concat(frame2).ToArray();
        var ranges = new[]
        {
            new ManagedReplicaLogicalLogRange(1, 0, (ulong)range1Bytes.Length, true, null),
            new ManagedReplicaLogicalLogRange(9, 999, (ulong)(999 + frame2.Length), false, seedBytes),
        };

        var txns = ManagedReplicaLml3Decoder.Decode(ranges, body, CancellationToken.None);
        txns.Should().HaveCount(2);
    }

    [Test]
    public void AnEmptyLogicalResponseWithAZeroLengthRangeDecodesToNoTransactions()
    {
        var salt = 40UL;
        var header = Lml3TestBuilder.BuildHeader(salt);
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, (ulong)header.Length, true, null) };
        var txns = ManagedReplicaLml3Decoder.Decode(ranges, header, CancellationToken.None);
        txns.Should().BeEmpty();
    }

    [Test]
    public void CancellationIsHonoredBeforeDecodingBegins()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, 0, true, null) };
        Assert.Throws<OperationCanceledException>(() => ManagedReplicaLml3Decoder.Decode(ranges, [], cts.Token));
    }

    [Test]
    public void ExtensionRecordLengthOfExactlyOverflowThresholdIsRejected()
    {
        // 0x80000000: the smallest uint whose (int) cast would be negative if narrowed without a
        // guard, which could make payloadEnd regress behind payloadStart (breaking forward
        // progress) or throw the wrong exception type from a checked cast.
        AssertMalformedExtensionRecordLengthThrows(0x80000000);
    }

    [Test]
    public void ExtensionRecordLengthNearUInt32MaxIsRejected()
    {
        AssertMalformedExtensionRecordLengthThrows(0xfffffff8);
    }

    private static void AssertMalformedExtensionRecordLengthThrows(uint declaredLength)
    {
        var salt = 55UL;
        var header = Lml3TestBuilder.BuildHeader(salt);
        var crc = Lml3TestBuilder.HeaderSeedCrc(salt);
        var op = Lml3TestBuilder.BuildRecoveryOp(0, 0, -2, Lml3TestBuilder.UpsertTablePayload(1, [0x00]));

        // One hand-built extension record header whose length field lies about how much data
        // follows (no actual payload bytes are present at all).
        var extensionBlock = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(extensionBlock.AsSpan(0), 1); // type
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(extensionBlock.AsSpan(2), 0); // flags
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(extensionBlock.AsSpan(4), declaredLength);

        var frame = Lml3TestBuilder.BuildFrameWithExtensionRecordCount(
            ref crc, op, 1, extensionBlock, extensionRecordCount: 1);

        var body = header.Concat(frame).ToArray();
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, (ulong)body.Length, true, null) };
        Assert.Throws<InvalidDataException>(() => ManagedReplicaLml3Decoder.Decode(ranges, body, CancellationToken.None));
    }

    [Test]
    public void HugeDeclaredExtensionRecordCountIsRejectedBeforeIterating()
    {
        var salt = 56UL;
        var header = Lml3TestBuilder.BuildHeader(salt);
        var crc = Lml3TestBuilder.HeaderSeedCrc(salt);
        var op = Lml3TestBuilder.BuildRecoveryOp(0, 0, -2, Lml3TestBuilder.UpsertTablePayload(1, [0x00]));

        // A tiny extension block cannot possibly contain anywhere near this many 8-byte-minimum
        // records; the decoder must reject the declared count up front rather than attempt to
        // iterate towards discovering the mismatch.
        var extensionBlock = new byte[8];
        var frame = Lml3TestBuilder.BuildFrameWithExtensionRecordCount(
            ref crc, op, 1, extensionBlock, extensionRecordCount: 0xfffffff8);

        var body = header.Concat(frame).ToArray();
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, (ulong)body.Length, true, null) };
        Assert.Throws<InvalidDataException>(() => ManagedReplicaLml3Decoder.Decode(ranges, body, CancellationToken.None));
    }

    [Test]
    public void CancellationIsHonoredWhileScanningALargeExtensionBlock()
    {
        var salt = 57UL;
        var header = Lml3TestBuilder.BuildHeader(salt);
        var crc = Lml3TestBuilder.HeaderSeedCrc(salt);
        var op = Lml3TestBuilder.BuildRecoveryOp(0, 0, -2, Lml3TestBuilder.UpsertTablePayload(1, [0x00]));

        // Many zero-payload (8-byte header only) extension records: individually cheap and legal,
        // but numerous enough that an uncancellable scan would be a real DoS concern for an
        // attacker-controlled recordCount. A pre-cancelled token must still surface
        // OperationCanceledException (not run to completion, and not throw some other exception
        // type first).
        const int recordCount = 5000;
        var extensionBlock = new byte[recordCount * 8];
        for (var i = 0; i < recordCount; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(extensionBlock.AsSpan(i * 8), 1);

        var frame = Lml3TestBuilder.BuildFrameWithExtensionRecordCount(
            ref crc, op, 1, extensionBlock, extensionRecordCount: recordCount);

        var body = header.Concat(frame).ToArray();
        var ranges = new[] { new ManagedReplicaLogicalLogRange(1, 0, (ulong)body.Length, true, null) };

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => ManagedReplicaLml3Decoder.Decode(ranges, body, cts.Token));
    }

    [Test]
    public void Crc32CMatchesTheCanonicalCheckValue()
    {
        // The standard CRC32C (Castagnoli) check value: CRC32C(ASCII "123456789") == 0xE3069283.
        // This asserts the PRODUCTION Lml3Crc32C directly against an externally-defined constant,
        // independent of the separate Lml3Crc32CForTests implementation the test builder uses to
        // construct fixtures (a bug shared by both would not be caught by frame round-trip tests
        // alone).
        var bytes = System.Text.Encoding.ASCII.GetBytes("123456789");
        Lml3Crc32C.Compute(bytes).Should().Be(0xE3069283u);
    }

    [Test]
    public void Crc32CChainedAppendMatchesASingleComputeOverTheConcatenation()
    {
        var part1 = System.Text.Encoding.ASCII.GetBytes("123456789");
        var part2 = System.Text.Encoding.ASCII.GetBytes("the quick brown fox");
        var whole = part1.Concat(part2).ToArray();

        var chained = Lml3Crc32C.Append(Lml3Crc32C.Compute(part1), part2);
        var direct = Lml3Crc32C.Compute(whole);

        chained.Should().Be(direct);
    }
}

internal static class SqliteRecordCodecTestHelper
{
    public static byte[] Encode(string text) => Core.Storage.SqliteRecordCodec.Encode([Core.SqlValue.Text(text)]);

    public static byte[] EncodeRow(params Core.SqlValue[] values) => Core.Storage.SqliteRecordCodec.Encode(values);
}
