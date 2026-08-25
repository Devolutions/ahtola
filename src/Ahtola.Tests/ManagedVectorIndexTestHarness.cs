using System.Buffers.Binary;
using System.Globalization;
using Ahtola.Core;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>The vector encodings the managed vector index can be bound to.</summary>
public enum VectorTestEncoding
{
    Float32,
    Float64,
    Float8,
    Float1Bit,
}

/// <summary>The distance metrics the managed vector index can be bound to.</summary>
public enum VectorTestMetric
{
    L2,
    Cosine,
    Dot,
}

/// <summary>
/// Shared harness for the managed vector index suites: deterministic corpora, an indexed table and
/// an un-indexed sibling, and the SQL fragments each encoding and metric needs.
/// </summary>
internal static class ManagedVectorIndexTestHarness
{
    public static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    public static IReadOnlyList<SqlValue[]> Query(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var row = new SqlValue[statement.ColumnCount];
            for (var index = 0; index < row.Length; index++)
                row[index] = statement.GetValue(index);

            rows.Add(row);
        }

        return rows;
    }

    public static IReadOnlyList<long> QueryIntegers(EmbeddedConnection connection, string sql)
        => Query(connection, sql).Select(static row => row[0].AsInteger()).ToArray();

    public static EmbeddedSqlException ShouldThrow(EmbeddedConnection connection, string sql)
    {
        var act = () => Execute(connection, sql);
        return act.Should().Throw<EmbeddedSqlException>().Which;
    }

    /// <summary>The last EXPLAIN QUERY PLAN detail line, which is the access-path row.</summary>
    public static string ExplainDetail(EmbeddedConnection connection, string sql)
    {
        var rows = Query(connection, "EXPLAIN QUERY PLAN " + sql);
        return rows.Count == 0 ? string.Empty : rows[^1][3].AsText();
    }

    public static string CreateDatabasePath(string suite)
    {
        var directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, suite);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():N}.db");
    }

    public static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }

    /// <summary>The SQL constructor that produces one encoding's serialized blob.</summary>
    public static string Constructor(VectorTestEncoding encoding)
        => encoding switch
        {
            VectorTestEncoding.Float32 => "vector32",
            VectorTestEncoding.Float64 => "vector64",
            VectorTestEncoding.Float8 => "vector8",
            VectorTestEncoding.Float1Bit => "vector1bit",
            _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
        };

    /// <summary>The <c>WITH (encoding = …)</c> value for one encoding.</summary>
    public static string EncodingOption(VectorTestEncoding encoding)
        => encoding switch
        {
            VectorTestEncoding.Float32 => "float32",
            VectorTestEncoding.Float64 => "float64",
            VectorTestEncoding.Float8 => "float8",
            VectorTestEncoding.Float1Bit => "float1bit",
            _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
        };

    /// <summary>The <c>WITH (metric = …)</c> value for one metric.</summary>
    public static string MetricOption(VectorTestMetric metric)
        => metric switch
        {
            VectorTestMetric.L2 => "l2",
            VectorTestMetric.Cosine => "cosine",
            VectorTestMetric.Dot => "dot",
            _ => throw new ArgumentOutOfRangeException(nameof(metric)),
        };

    /// <summary>The SQL distance function bound to one metric.</summary>
    public static string DistanceFunction(VectorTestMetric metric)
        => metric switch
        {
            VectorTestMetric.L2 => "vector_distance_l2",
            VectorTestMetric.Cosine => "vector_distance_cos",
            VectorTestMetric.Dot => "vector_distance_dot",
            _ => throw new ArgumentOutOfRangeException(nameof(metric)),
        };

    /// <summary>Renders a vector as the bracketed text every constructor accepts.</summary>
    public static string Literal(IReadOnlyList<double> values)
        => "[" + string.Join(
            ",",
            values.Select(static value => value.ToString("R", CultureInfo.InvariantCulture))) + "]";

    /// <summary>A deterministic corpus of vectors, drawn without any engine involvement.</summary>
    public static double[][] GenerateVectors(int count, int dimensions, int seed, bool binary = false)
    {
        var random = new DeterministicTestRandom((ulong)seed);
        var vectors = new double[count][];
        for (var index = 0; index < count; index++)
        {
            var vector = new double[dimensions];
            for (var component = 0; component < dimensions; component++)
            {
                vector[component] = binary
                    ? random.NextDouble() < 0.5 ? 0.0 : 1.0
                    : Math.Round((random.NextDouble() * 2.0) - 1.0, 4);
            }

            // A zero vector has no direction, so cosine cannot rank it; keeping the corpus away from
            // the origin keeps the oracles comparable across every metric.
            if (vector.All(static value => value == 0.0))
                vector[0] = 1.0;

            vectors[index] = vector;
        }

        return vectors;
    }

    /// <summary>
    /// A deterministic corpus drawn from a handful of tight clusters.
    /// </summary>
    /// <remarks>
    /// Real embedding corpora are clustered, which is the structure an inverted-file index exists to
    /// exploit. Uniform noise in a low dimension is the opposite: no list can be ruled out, so the
    /// certificate reads everything. Both are worth testing, and the suites keep them separate so a
    /// recall assertion and a pruning assertion never get confused for one another.
    /// </remarks>
    public static double[][] GenerateClusteredVectors(
        int count,
        int dimensions,
        int seed,
        int clusters = 16,
        bool binary = false)
    {
        var random = new DeterministicTestRandom((ulong)seed);
        var centers = new double[clusters][];
        for (var cluster = 0; cluster < clusters; cluster++)
        {
            var center = new double[dimensions];
            for (var component = 0; component < dimensions; component++)
                center[component] = binary ? (random.NextDouble() < 0.5 ? 0.0 : 1.0) : Math.Round((random.NextDouble() * 20.0) - 10.0, 4);

            centers[cluster] = center;
        }

        var vectors = new double[count][];
        for (var index = 0; index < count; index++)
        {
            var center = centers[index % clusters];
            var vector = new double[dimensions];
            for (var component = 0; component < dimensions; component++)
            {
                vector[component] = binary
                    ? random.NextDouble() < 0.06 ? 1.0 - center[component] : center[component]
                    : Math.Round(center[component] + ((random.NextDouble() * 0.4) - 0.2), 4);
            }

            if (vector.All(static value => value == 0.0))
                vector[0] = 1.0;

            vectors[index] = vector;
        }

        return vectors;
    }

    /// <summary>
    /// Creates an indexed table and an identically populated un-indexed sibling.
    /// </summary>
    /// <remarks>
    /// The sibling is the first of the two oracles: the same SQL over the same rows, answered by the
    /// engine's ordinary scan and scalar evaluator.
    /// </remarks>
    public static void SeedCorpus(
        EmbeddedConnection connection,
        double[][] vectors,
        VectorTestEncoding encoding,
        VectorTestMetric metric,
        int dimensions,
        int lists = 64,
        int minimumRows = 8,
        string? extraOptions = null)
    {
        var constructor = Constructor(encoding);
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, embedding BLOB);");
        Execute(connection, "CREATE TABLE plain(id INTEGER PRIMARY KEY, embedding BLOB);");
        Execute(
            connection,
            $"CREATE INDEX docs_knn ON docs USING vector (embedding) WITH (metric = '{MetricOption(metric)}', "
            + $"encoding = '{EncodingOption(encoding)}', dims = {dimensions}, lists = {lists}, min_rows = {minimumRows}"
            + (extraOptions is null ? string.Empty : ", " + extraOptions)
            + ");");

        Execute(connection, "BEGIN;");
        for (var index = 0; index < vectors.Length; index++)
        {
            var literal = $"{constructor}('{Literal(vectors[index])}')";
            Execute(connection, $"INSERT INTO docs VALUES ({index + 1}, {literal});");
            Execute(connection, $"INSERT INTO plain VALUES ({index + 1}, {literal});");
        }

        Execute(connection, "COMMIT;");
    }

    /// <summary>Reads one table's stored blobs, keyed by rowid, for the independent oracle.</summary>
    public static Dictionary<long, byte[]> ReadBlobs(EmbeddedConnection connection, string table)
    {
        var blobs = new Dictionary<long, byte[]>();
        foreach (var row in Query(connection, $"SELECT id, embedding FROM {table} ORDER BY id;"))
            blobs[row[0].AsInteger()] = row[1].AsBlob().ToArray();

        return blobs;
    }

    /// <summary>
    /// Extracts the persisted state envelope straight out of a database file.
    /// </summary>
    /// <remarks>
    /// The catalog strips the envelope when it hands <c>sqlite_schema.sql</c> back, so the only way
    /// to assert on the bytes that were actually written is to read them from the file.
    /// </remarks>
    public static string ReadStoredEnvelope(string path)
    {
        // Shared access: some suites read the envelope while the database that produced it is still
        // open, which is safe because the envelope is only ever written by a committed transaction.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var text = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        var start = text.IndexOf("/*ahtola-index-method:", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the catalog must carry a state envelope");
        var end = text.IndexOf("*/", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        return text[start..(end + 2)];
    }
}

/// <summary>
/// A tiny deterministic generator for test corpora.
/// </summary>
/// <remarks>
/// Deliberately not <see cref="Random"/>: its algorithm is an implementation detail that has changed
/// across .NET versions, and a corpus that shifts between framework updates would make a recall
/// assertion mean something different on every run.
/// </remarks>
internal sealed class DeterministicTestRandom(ulong seed)
{
    private ulong _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

    public double NextDouble()
    {
        unchecked
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (z >> 11) * (1.0 / 9007199254740992.0);
        }
    }
}

/// <summary>
/// The second oracle: a brute-force nearest-neighbour search written against the documented Turso
/// blob layouts and distance definitions, sharing no code with the engine.
/// </summary>
/// <remarks>
/// It decodes the stored blobs itself and reproduces each distance with the same accumulation width
/// the format implies — <c>float</c> for float32 sums, <c>double</c> for float64 and float8, exact
/// integer counts for float1bit — so agreeing with it is evidence about the engine rather than a
/// restatement of it.
/// </remarks>
internal static class ManagedVectorTestOracle
{
    /// <summary>The top <paramref name="limit"/> rowids by distance, ties by ascending rowid.</summary>
    public static IReadOnlyList<long> TopK(
        IReadOnlyDictionary<long, byte[]> corpus,
        byte[] query,
        VectorTestMetric metric,
        int limit)
    {
        var scored = corpus
            .Select(entry => (RowId: entry.Key, Distance: Distance(entry.Value, query, metric)))
            .ToList();
        scored.Sort((left, right) =>
        {
            var comparison = left.Distance.CompareTo(right.Distance);
            return comparison != 0 ? comparison : left.RowId.CompareTo(right.RowId);
        });

        return scored.Take(limit).Select(static entry => entry.RowId).ToArray();
    }

    /// <summary>The distance between two serialized vectors, reimplemented from the format.</summary>
    public static double Distance(byte[] left, byte[] right, VectorTestMetric metric)
    {
        var (leftKind, leftValues, leftBits, leftDimensions) = Decode(left);
        var (rightKind, rightValues, rightBits, rightDimensions) = Decode(right);
        leftKind.Should().Be(rightKind);
        leftDimensions.Should().Be(rightDimensions);

        if (leftKind == VectorTestEncoding.Float1Bit)
        {
            var hamming = 0;
            for (var index = 0; index < leftDimensions; index++)
            {
                if (leftBits![index] != rightBits![index])
                    hamming++;
            }

            return metric switch
            {
                VectorTestMetric.Cosine => hamming,
                VectorTestMetric.Dot => -(leftDimensions - (2.0 * hamming)),
                _ => throw new InvalidOperationException("float1bit has no L2 distance"),
            };
        }

        if (leftKind == VectorTestEncoding.Float32)
            return Float32Distance(leftValues!, rightValues!, metric);

        return DoubleDistance(leftValues!, rightValues!, metric);
    }

    /// <summary>float32 accumulates its sums in single precision; the oracle does the same.</summary>
    private static double Float32Distance(double[] left, double[] right, VectorTestMetric metric)
    {
        var dot = 0.0f;
        var dot64 = 0.0;
        var leftNorm = 0.0f;
        var rightNorm = 0.0f;
        var squared = 0.0f;
        for (var index = 0; index < left.Length; index++)
        {
            var a = (float)left[index];
            var b = (float)right[index];
            dot += a * b;
            dot64 += (double)a * b;
            leftNorm += a * a;
            rightNorm += b * b;
            var difference = a - b;
            squared += difference * difference;
        }

        return metric switch
        {
            VectorTestMetric.L2 => Math.Sqrt(squared),
            VectorTestMetric.Cosine => leftNorm == 0.0f || rightNorm == 0.0f
                ? leftNorm == rightNorm ? 0.0 : 1.0
                : 1.0f - (dot / MathF.Sqrt(leftNorm * rightNorm)),
            VectorTestMetric.Dot => -dot64,
            _ => throw new ArgumentOutOfRangeException(nameof(metric)),
        };
    }

    private static double DoubleDistance(double[] left, double[] right, VectorTestMetric metric)
    {
        var dot = 0.0;
        var leftNorm = 0.0;
        var rightNorm = 0.0;
        var squared = 0.0;
        for (var index = 0; index < left.Length; index++)
        {
            var a = left[index];
            var b = right[index];
            dot += a * b;
            leftNorm += a * a;
            rightNorm += b * b;
            var difference = a - b;
            squared += difference * difference;
        }

        return metric switch
        {
            VectorTestMetric.L2 => Math.Sqrt(squared),
            VectorTestMetric.Cosine => leftNorm == 0.0 || rightNorm == 0.0
                ? leftNorm == rightNorm ? 0.0 : 1.0
                : 1.0 - (dot / Math.Sqrt(leftNorm * rightNorm)),
            VectorTestMetric.Dot => -dot,
            _ => throw new ArgumentOutOfRangeException(nameof(metric)),
        };
    }

    /// <summary>Decodes a serialized vector straight from its bytes.</summary>
    private static (VectorTestEncoding Kind, double[]? Values, bool[]? Bits, int Dimensions) Decode(byte[] blob)
    {
        if ((blob.Length & 1) == 0)
        {
            var dimensions = blob.Length / 4;
            var values = new double[dimensions];
            for (var index = 0; index < dimensions; index++)
                values[index] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(index * 4)));

            return (VectorTestEncoding.Float32, values, null, dimensions);
        }

        switch (blob[^1])
        {
            case 1:
                {
                    var dimensions = (blob.Length - 1) / 4;
                    var values = new double[dimensions];
                    for (var index = 0; index < dimensions; index++)
                        values[index] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(index * 4)));

                    return (VectorTestEncoding.Float32, values, null, dimensions);
                }

            case 2:
                {
                    var dimensions = (blob.Length - 1) / 8;
                    var values = new double[dimensions];
                    for (var index = 0; index < dimensions; index++)
                        values[index] = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(blob.AsSpan(index * 8)));

                    return (VectorTestEncoding.Float64, values, null, dimensions);
                }

            case 3:
                {
                    var metadataLength = blob.Length - 1;
                    var trailingBits = blob[metadataLength - 1];
                    var dimensions = (metadataLength * 8) - trailingBits;
                    var bits = new bool[dimensions];
                    for (var index = 0; index < dimensions; index++)
                        bits[index] = ((blob[index / 8] >> (index & 7)) & 1) != 0;

                    return (VectorTestEncoding.Float1Bit, null, bits, dimensions);
                }

            case 4:
                {
                    var metadataLength = blob.Length - 1;
                    var trailingBytes = blob[metadataLength - 1];
                    var dimensions = metadataLength - 10 - trailingBytes;
                    var aligned = ((dimensions + 3) / 4) * 4;
                    var alpha = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(aligned)));
                    var shift = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(aligned + 4)));
                    var values = new double[dimensions];
                    for (var index = 0; index < dimensions; index++)
                        values[index] = ((double)alpha * blob[index]) + shift;

                    return (VectorTestEncoding.Float8, values, null, dimensions);
                }

            default:
                throw new InvalidOperationException($"Unexpected vector type byte {blob[^1]}.");
        }
    }
}
