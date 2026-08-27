using System.Buffers.Binary;
using Ahtola.Core.Vectors;

namespace Ahtola.Core;

/// <summary>The serialized vector encodings an index method may bind a column to.</summary>
/// <remarks>
/// The numeric values are the Turso type bytes carried by a serialized vector blob
/// (turso-src/core/vector/vector_types.rs), so the enum doubles as the persisted state discriminant.
/// </remarks>
internal enum VectorEncodingKind
{
    Float32 = 1,
    Float64 = 2,
    Float1Bit = 3,
    Float8 = 4,
    Float32Sparse = 9,
}

/// <summary>The distance functions an index method may bind an index to.</summary>
internal enum VectorDistanceKind
{
    L2 = 0,
    Cosine = 1,
    Dot = 2,
    Jaccard = 3,
}

/// <summary>
/// One decoded vector in the shape an index method reasons about: the encoding it came from, its
/// dimensionality, and its component values widened to <see cref="double"/>.
/// </summary>
/// <remarks>
/// The widened components are used only for the geometric bounds an index uses to prune lists.
/// Every distance that reaches a query result is produced by
/// <see cref="SqliteVectorFunctions.DistanceExact"/>, which is the scalar evaluator's own code path,
/// so an indexed answer and a scalar answer are bit identical by construction rather than by
/// re-implementation.
/// </remarks>
internal readonly record struct DecodedVector(VectorEncodingKind Encoding, int Dimensions, double[] Values)
{
    /// <summary>True when every component is finite, which every bound below assumes.</summary>
    public bool IsFinite
    {
        get
        {
            foreach (var value in Values)
            {
                if (!double.IsFinite(value))
                    return false;
            }

            return true;
        }
    }
}

/// <summary>A validated sparse float32 vector without a dense expansion.</summary>
internal readonly record struct DecodedSparseVector(int Dimensions, int[] Indices, float[] Values)
{
    public bool IsFinite
    {
        get
        {
            foreach (var value in Values)
            {
                if (!float.IsFinite(value))
                    return false;
            }

            return true;
        }
    }

    public bool IsNonNegative
    {
        get
        {
            foreach (var value in Values)
            {
                if (value < 0.0f)
                    return false;
            }

            return true;
        }
    }
}

/// <summary>
/// The indexing-facing half of the vector scalar functions.
/// </summary>
/// <remarks>
/// Distance and error members delegate to the scalar implementation. The sparse structural decoder
/// additionally validates the serialized layout in place so a declared index dimension bounds every
/// allocation before bytes are copied.
/// </remarks>
internal static partial class SqliteVectorFunctions
{
    /// <summary>Decodes a value the way <c>vector_distance_*</c> would, or reports why it cannot.</summary>
    /// <remarks>
    /// A failure is returned rather than thrown because the index classifies rows in bulk; the
    /// error text a query must raise is produced by re-running the real scalar call, never by
    /// re-formatting this message.
    /// </remarks>
    internal static bool TryDecodeVector(SqlValue value, out DecodedVector decoded)
        => TryDecodeVector(value, expectedEncoding: null, expectedDimensions: null, out decoded);

    /// <summary>
    /// Decodes a value and, when the caller already knows the shape the column is bound to, proves
    /// that shape before anything proportional to the vector is allocated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The unconstrained overload above must materialize whatever the blob declares, because it has
    /// no expectation to check against. An index does: every row it looks at is either the declared
    /// encoding and dimensionality or it is unindexable. Deciding that from the parsed header — a
    /// type byte and a length — costs nothing, whereas widening first and comparing afterwards
    /// allocates eight bytes per declared component for a row that was always going to be rejected.
    /// A million-component blob in a four-dimensional index is the case this exists for.
    /// </para>
    /// <para>
    /// The managed dimension cap is enforced here as well, so no decode this path performs can be
    /// larger than the cap regardless of what the caller expected.
    /// </para>
    /// </remarks>
    internal static bool TryDecodeVector(
        SqlValue value,
        VectorEncodingKind? expectedEncoding,
        int? expectedDimensions,
        out DecodedVector decoded)
    {
        decoded = default;
        if (expectedDimensions is { } requested
            && (requested < 0 || requested > MaximumManagedDimensions))
        {
            return false;
        }

        VectorValue parsed;
        try
        {
            parsed = Parse(value, null);
        }
        catch (EmbeddedSqlException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        var encoding = ToEncodingKind(parsed.Type);
        if (encoding is null)
            return false;
        if (expectedEncoding is { } wanted && encoding.Value != wanted)
            return false;
        if (expectedDimensions is { } dimensions && parsed.Dimensions != dimensions)
            return false;

        // Belt and braces: Parse already refuses an oversized blob, but an index must never be the
        // reason a single row turns into a multi-megabyte widening.
        if (parsed.Dimensions < 0 || parsed.Dimensions > MaximumManagedDimensions)
            return false;

        var values = new double[parsed.Dimensions];
        switch (parsed.Type)
        {
            case VectorType.Float32Dense:
                for (var index = 0; index < parsed.Dimensions; index++)
                    values[index] = ReadFloat(parsed.Data, index);
                break;

            case VectorType.Float64Dense:
                for (var index = 0; index < parsed.Dimensions; index++)
                    values[index] = ReadDouble(parsed.Data, index);
                break;

            case VectorType.Float1Bit:
                for (var index = 0; index < parsed.Dimensions; index++)
                    values[index] = IsBitSet(parsed.Data, index) ? 1.0 : 0.0;
                break;

            case VectorType.Float8:
                ReadFloat8Metadata(parsed, out var alpha, out var shift);
                for (var index = 0; index < parsed.Dimensions; index++)
                    values[index] = ((double)alpha * parsed.Data[index]) + shift;
                break;

            default:
                // Sparse vectors decode to a component list whose dense expansion is not what the
                // scalar distances walk; an index method must reject the encoding instead.
                return false;
        }

        decoded = new DecodedVector(encoding.Value, parsed.Dimensions, values);
        return true;
    }

    /// <summary>
    /// Decodes the sparse on-wire form after proving its complete shape and entry count.
    /// </summary>
    /// <remarks>
    /// This intentionally does not route through <c>Parse</c>: an index knows its declared
    /// dimensionality, so it can reject a hostile blob before copying it. Strictly increasing,
    /// in-range component indexes also bound the two result arrays by <paramref name="expectedDimensions"/>.
    /// </remarks>
    internal static bool TryDecodeSparseVector(
        SqlValue value,
        int expectedDimensions,
        out DecodedSparseVector decoded)
    {
        decoded = default;
        if (value.Kind != SqlValueKind.Blob
            || expectedDimensions < 1
            || expectedDimensions > ManagedVectorIndexLimits.MaxDimensions)
        {
            return false;
        }

        var blob = value.AsBlobSpan();
        if (blob.Length > MaximumManagedBlobBytes || blob.Length < 5 || blob[^1] != 9)
            return false;

        var dataLength = blob.Length - 1;
        if ((dataLength & 3) != 0 || (dataLength - sizeof(uint)) % 8 != 0)
            return false;

        var dimensions = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(dataLength - sizeof(uint), sizeof(uint)));
        if (dimensions != (uint)expectedDimensions)
            return false;

        var entries = (dataLength - sizeof(uint)) / 8;
        if (entries > expectedDimensions)
            return false;

        var indices = new int[entries];
        var values = new float[entries];
        uint previous = 0;
        for (var entry = 0; entry < entries; entry++)
        {
            var component = BinaryPrimitives.ReadUInt32LittleEndian(
                blob.Slice((entries * sizeof(float)) + (entry * sizeof(uint)), sizeof(uint)));
            if (component >= dimensions || (entry != 0 && component <= previous))
                return false;

            indices[entry] = (int)component;
            values[entry] = BitConverter.Int32BitsToSingle(
                BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(entry * sizeof(float), sizeof(float))));
            previous = component;
        }

        decoded = new DecodedSparseVector(expectedDimensions, indices, values);
        return true;
    }

    /// <summary>The encoding of a value, or null when it is not a vector this build can index.</summary>
    internal static VectorEncodingKind? TryReadVectorEncoding(SqlValue value)
    {
        try
        {
            return ToEncodingKind(Parse(value, null).Type);
        }
        catch (EmbeddedSqlException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The distance the scalar evaluator would produce for <c>vector_distance_*(left, right)</c>.
    /// </summary>
    /// <remarks>
    /// This is the same call the SQL function makes, so an index rank and a scalar rank agree to the
    /// bit — including the float32 accumulation order, the zero-norm cosine rule and the
    /// float1bit-specific definitions.
    /// </remarks>
    internal static double DistanceExact(SqlValue left, SqlValue right, VectorDistanceKind kind)
    {
        var result = Distance([left, right], "vector distance requires exactly two arguments", ToDistanceKind(kind));
        return result.Kind switch
        {
            SqlValueKind.Integer => result.AsInteger(),
            SqlValueKind.Real => result.AsReal(),

            // A distance that is not a number surfaces as SQL NULL (a degenerate cosine over two
            // zero-norm vectors, for example). NaN is the value that carries "this pair has no
            // usable ordering" back to the caller, which abandons pruning rather than guessing.
            _ => double.NaN,
        };
    }

    /// <summary>
    /// Raises exactly the error <c>vector_distance_*</c> would raise for a query argument evaluated
    /// against a column that is known to hold <paramref name="encoding"/> vectors of
    /// <paramref name="dimensions"/> components.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scalar function parses both operands, then compares dimensions, then compares types
    /// (<c>Distance</c> above). An index is only ever planned when every live row already decodes to
    /// the declared encoding and dimensionality, so the column operand can never be the operand that
    /// fails; the first failure is therefore always one of the three checks reproduced here, in the
    /// same order, for either argument order.
    /// </para>
    /// <para>
    /// Callers must invoke this only when the base table has at least one row: an empty scan
    /// evaluates no call at all and so raises nothing.
    /// </para>
    /// </remarks>
    internal static void ValidateVectorQueryArgument(SqlValue query, VectorEncodingKind encoding, int dimensions)
    {
        // Parse first, exactly like the scalar path: a malformed operand outranks any later check.
        var parsed = Parse(query, null);
        if (parsed.Dimensions != dimensions)
            throw new EmbeddedSqlException("Vectors must have the same dimensions");
        if (ToEncodingKind(parsed.Type) != encoding)
            throw new EmbeddedSqlException("Vectors must be of the same type");
    }

    private static VectorEncodingKind? ToEncodingKind(VectorType type)
        => type switch
        {
            VectorType.Float32Dense => VectorEncodingKind.Float32,
            VectorType.Float64Dense => VectorEncodingKind.Float64,
            VectorType.Float1Bit => VectorEncodingKind.Float1Bit,
            VectorType.Float8 => VectorEncodingKind.Float8,
            VectorType.Float32Sparse => VectorEncodingKind.Float32Sparse,
            _ => null,
        };

    private static DistanceKind ToDistanceKind(VectorDistanceKind kind)
        => kind switch
        {
            VectorDistanceKind.L2 => DistanceKind.L2,
            VectorDistanceKind.Cosine => DistanceKind.Cosine,
            VectorDistanceKind.Dot => DistanceKind.Dot,
            VectorDistanceKind.Jaccard => DistanceKind.Jaccard,
            _ => throw new InvalidOperationException($"Unknown vector distance kind {kind}."),
        };
}
