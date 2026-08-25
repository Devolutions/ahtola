using System.Buffers.Binary;

namespace Ahtola.Core.Vectors;

/// <summary>
/// The versioned state envelope for a vector method index: configuration plus trained centroids.
/// </summary>
/// <remarks>
/// <para>
/// The envelope rides in the trailing comment of the index's <c>sqlite_schema.sql</c> text, so it is
/// written, rolled back and recovered by the same pager/WAL transaction as the rest of the catalog.
/// It carries only the centroids: assignments, postings and radii are derived from the base rows on
/// demand, which is what makes rollback, savepoint, VACUUM and crash recovery correct by inheritance
/// rather than by a second durability path.
/// </para>
/// <para>
/// Every check below runs before the centroid array is allocated. A hostile or corrupt catalog row
/// therefore cannot make the loader materialize a large buffer and reject it afterwards.
/// </para>
/// </remarks>
internal static class ManagedVectorIndexState
{
    /// <summary>'A' 'V' 'I' 'X', little-endian.</summary>
    public const uint Magic = 0x5849_5641;

    /// <summary>
    /// The fixed byte layout of the header, declared once so a writer and a reader cannot drift.
    /// </summary>
    /// <remarks>
    /// Every field is at a named offset with an explicit width, and <see cref="HeaderSize"/> is the
    /// end of the last field rather than a hand-maintained round number. A previous revision wrote
    /// the fingerprint as an eight-byte value into a four-byte slot at the end of the header, which
    /// silently zeroed the first centroid component on every save; the layout below makes that class
    /// of overlap a compile-time-visible arithmetic identity instead of an invisible one.
    /// </remarks>
    public const int MagicOffset = 0;

    public const int VersionOffset = MagicOffset + sizeof(uint);

    public const int MetricOffset = VersionOffset + sizeof(ushort);

    public const int EncodingOffset = MetricOffset + sizeof(byte);

    public const int DimensionsOffset = EncodingOffset + sizeof(byte);

    public const int ListsOffset = DimensionsOffset + sizeof(int);

    public const int IterationsOffset = ListsOffset + sizeof(int);

    public const int TrainSampleOffset = IterationsOffset + sizeof(int);

    public const int SeedOffset = TrainSampleOffset + sizeof(int);

    /// <summary>How many rows the k-means sample actually held.</summary>
    public const int TrainedSampleOffset = SeedOffset + sizeof(long);

    public const int ExactOffset = TrainedSampleOffset + sizeof(int);

    /// <summary>Three bytes reserved so <c>probes</c> stays four-byte aligned.</summary>
    public const int ReservedOffset = ExactOffset + sizeof(byte);

    public const int ProbesOffset = ReservedOffset + 3;

    /// <summary>A four-byte FNV-1a fold of the centroid payload.</summary>
    public const int FingerprintOffset = ProbesOffset + sizeof(int);

    /// <summary>
    /// The eligible live-row population the sample was drawn from, which is what drift is measured
    /// against. It is distinct from <see cref="TrainedSampleOffset"/>, which is capped by
    /// <c>train_sample</c> and therefore says nothing about how large the table was.
    /// </summary>
    public const int TrainedPopulationOffset = FingerprintOffset + sizeof(uint);

    /// <summary>Fixed header size preceding the centroid payload.</summary>
    public const int HeaderSize = TrainedPopulationOffset + sizeof(long);

    /// <summary>Serializes configuration and centroids into the persisted envelope.</summary>
    public static byte[] Encode(
        ManagedVectorIndexOptions options,
        float[] centroids,
        int trainedSampleRows,
        long trainedPopulation)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(centroids);

        var payloadBytes = checked(centroids.Length * sizeof(float));
        var buffer = new byte[checked(HeaderSize + payloadBytes)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(MagicOffset), Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.AsSpan(VersionOffset),
            (ushort)ManagedVectorIndexMethod.StateVersion);
        buffer[MetricOffset] = (byte)options.Metric;
        buffer[EncodingOffset] = (byte)options.Encoding;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(DimensionsOffset), options.Dimensions);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(ListsOffset), options.Lists);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(IterationsOffset), options.Iterations);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(TrainSampleOffset), options.TrainSample);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(SeedOffset), options.Seed);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(TrainedSampleOffset), trainedSampleRows);
        buffer[ExactOffset] = options.Exact ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(ProbesOffset), options.Probes);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(TrainedPopulationOffset), trainedPopulation);
        for (var index = 0; index < centroids.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                buffer.AsSpan(HeaderSize + (index * sizeof(float))),
                BitConverter.SingleToInt32Bits(centroids[index]));
        }

        // Written last, four bytes wide, into a four-byte field that ends exactly where the header
        // ends minus the population field: the payload is never touched by this write.
        var fingerprint = Fingerprint(buffer.AsSpan(HeaderSize));
        BinaryPrimitives.WriteUInt32LittleEndian(
            buffer.AsSpan(FingerprintOffset),
            (uint)(fingerprint & 0xFFFF_FFFFUL));
        return buffer;
    }

    /// <summary>
    /// Validates and decodes an envelope, failing closed on every mismatch.
    /// </summary>
    /// <returns>
    /// The restored centroids, the sample size they were trained over, and the eligible live-row
    /// population that sample was drawn from.
    /// </returns>
    public static (float[] Centroids, int TrainedSampleRows, long TrainedPopulation) Decode(
        string indexName,
        ManagedVectorIndexOptions options,
        int version,
        ReadOnlySpan<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (version <= 0)
            throw new EmbeddedSqlException($"malformed managed index '{indexName}': invalid state version");
        if (version > ManagedVectorIndexMethod.StateVersion)
        {
            throw new EmbeddedSqlException(
                $"index '{indexName}' was written by a newer managed index method (v{version})");
        }

        if (bytes.Length == 0)
            throw new EmbeddedSqlException($"malformed managed index '{indexName}': empty state");
        if (bytes.Length < HeaderSize)
            throw new EmbeddedSqlException($"malformed managed index '{indexName}': truncated state");
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[MagicOffset..]) != Magic)
            throw new EmbeddedSqlException($"malformed managed index '{indexName}': truncated state");

        var storedVersion = BinaryPrimitives.ReadUInt16LittleEndian(bytes[VersionOffset..]);
        if (storedVersion != version)
            throw new EmbeddedSqlException($"malformed managed index '{indexName}': state version mismatch");

        RequireField(indexName, "metric", bytes[MetricOffset] == (byte)options.Metric);
        RequireField(indexName, "encoding", bytes[EncodingOffset] == (byte)options.Encoding);
        RequireField(
            indexName,
            "dims",
            BinaryPrimitives.ReadInt32LittleEndian(bytes[DimensionsOffset..]) == options.Dimensions);
        RequireField(indexName, "lists", BinaryPrimitives.ReadInt32LittleEndian(bytes[ListsOffset..]) == options.Lists);
        RequireField(
            indexName,
            "iters",
            BinaryPrimitives.ReadInt32LittleEndian(bytes[IterationsOffset..]) == options.Iterations);
        RequireField(
            indexName,
            "train_sample",
            BinaryPrimitives.ReadInt32LittleEndian(bytes[TrainSampleOffset..]) == options.TrainSample);
        RequireField(indexName, "seed", BinaryPrimitives.ReadInt64LittleEndian(bytes[SeedOffset..]) == options.Seed);
        RequireField(indexName, "exact", bytes[ExactOffset] == (options.Exact ? 1 : 0));
        RequireField(indexName, "probes", BinaryPrimitives.ReadInt32LittleEndian(bytes[ProbesOffset..]) == options.Probes);

        var trainedSampleRows = BinaryPrimitives.ReadInt32LittleEndian(bytes[TrainedSampleOffset..]);
        if (trainedSampleRows < 0)
            throw new EmbeddedSqlException($"malformed managed index '{indexName}': invalid trained row count");

        // The population is what the drift rule compares live rows against, so a value that cannot
        // have produced the recorded sample is corruption rather than a number to clamp.
        var trainedPopulation = BinaryPrimitives.ReadInt64LittleEndian(bytes[TrainedPopulationOffset..]);
        if (trainedPopulation < 0 || trainedPopulation < trainedSampleRows)
            throw new EmbeddedSqlException($"malformed managed index '{indexName}': invalid trained row count");

        // The payload length is checked against the declared shape before anything is allocated, so
        // an oversized or short envelope is rejected rather than partially materialized.
        var expected = checked((long)options.Lists * options.Dimensions * sizeof(float));
        if (bytes.Length - HeaderSize != expected)
            throw new EmbeddedSqlException($"malformed managed index '{indexName}': centroid payload length mismatch");

        var payload = bytes[HeaderSize..];
        var storedFingerprint = BinaryPrimitives.ReadUInt32LittleEndian(bytes[FingerprintOffset..]);
        if ((uint)(Fingerprint(payload) & 0xFFFF_FFFFUL) != storedFingerprint)
            throw new EmbeddedSqlException($"malformed managed index '{indexName}': centroid checksum mismatch");

        var centroids = new float[options.Lists * options.Dimensions];
        for (var index = 0; index < centroids.Length; index++)
        {
            var value = BitConverter.Int32BitsToSingle(
                BinaryPrimitives.ReadInt32LittleEndian(payload[(index * sizeof(float))..]));

            // A non-finite centroid would poison every geometric bound derived from it, so it is a
            // corruption signal rather than a value to be clamped.
            if (!float.IsFinite(value))
                throw new EmbeddedSqlException($"malformed managed index '{indexName}': non-finite centroid");

            centroids[index] = value;
        }

        return (centroids, trainedSampleRows, trainedPopulation);
    }

    private static void RequireField(string indexName, string field, bool matches)
    {
        if (!matches)
        {
            throw new EmbeddedSqlException(
                $"malformed managed index '{indexName}': state {field} does not match the index definition");
        }
    }

    /// <summary>FNV-1a over the centroid payload; deterministic and endian independent.</summary>
    private static ulong Fingerprint(ReadOnlySpan<byte> payload)
    {
        var hash = 0xCBF29CE484222325UL;
        foreach (var value in payload)
        {
            hash ^= value;
            hash *= 0x100000001B3UL;
        }

        return hash;
    }
}
