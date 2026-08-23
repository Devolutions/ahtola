using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Ahtola.Core;

/// <summary>
/// Pure-managed implementation of Turso's vector scalar functions and serialized vector format.
/// </summary>
/// <remarks>
/// The byte layouts and operation semantics mirror <c>core/vector</c> at Turso commit
/// <c>277ddd050b1243bc19792e845c77f1ccd31896c8</c>.
/// </remarks>
internal static class SqliteVectorFunctions
{
    private enum VectorType
    {
        Float32Dense,
        Float64Dense,
        Float32Sparse,
        Float1Bit,
        Float8,
    }

    private readonly record struct VectorValue(VectorType Type, int Dimensions, byte[] Data);

    internal static SqlValue Vector32(IReadOnlyList<SqlValue> arguments)
    {
        RequireCount(arguments, 1, "vector32 requires exactly one argument");
        return SqlValue.Blob(Serialize(Convert(Parse(arguments[0], VectorType.Float32Dense), VectorType.Float32Dense)));
    }

    internal static SqlValue Vector32Sparse(IReadOnlyList<SqlValue> arguments)
    {
        RequireCount(arguments, 1, "vector32_sparse requires exactly one argument");
        return SqlValue.Blob(Serialize(Convert(Parse(arguments[0], VectorType.Float32Sparse), VectorType.Float32Sparse)));
    }

    internal static SqlValue Vector64(IReadOnlyList<SqlValue> arguments)
    {
        RequireCount(arguments, 1, "vector64 requires exactly one argument");
        return SqlValue.Blob(Serialize(Convert(Parse(arguments[0], VectorType.Float64Dense), VectorType.Float64Dense)));
    }

    internal static SqlValue Vector8(IReadOnlyList<SqlValue> arguments)
    {
        RequireCount(arguments, 1, "vector8 requires exactly one argument");
        return SqlValue.Blob(Serialize(Convert(Parse(arguments[0], VectorType.Float8), VectorType.Float8)));
    }

    internal static SqlValue Vector1Bit(IReadOnlyList<SqlValue> arguments)
    {
        RequireCount(arguments, 1, "vector1bit requires exactly one argument");
        return SqlValue.Blob(Serialize(Convert(Parse(arguments[0], VectorType.Float1Bit), VectorType.Float1Bit)));
    }

    internal static SqlValue Extract(IReadOnlyList<SqlValue> arguments)
    {
        RequireCount(arguments, 1, "vector_extract requires exactly one argument");
        if (arguments[0].Kind != SqlValueKind.Blob)
            throw new EmbeddedSqlException("Expected blob value");

        var blob = arguments[0].AsBlobSpan();
        if (blob.IsEmpty)
            return SqlValue.Text("[]");

        return SqlValue.Text(Format(ParseBlob(blob)));
    }

    internal static SqlValue DistanceCos(IReadOnlyList<SqlValue> arguments)
        => Distance(arguments, "vector_distance_cos requires exactly two arguments", DistanceKind.Cosine);

    internal static SqlValue DistanceL2(IReadOnlyList<SqlValue> arguments)
        => Distance(arguments, "distance_l2 requires exactly two arguments", DistanceKind.L2);

    internal static SqlValue DistanceJaccard(IReadOnlyList<SqlValue> arguments)
        => Distance(arguments, "distance_jaccard requires exactly two arguments", DistanceKind.Jaccard);

    internal static SqlValue DistanceDot(IReadOnlyList<SqlValue> arguments)
        => Distance(arguments, "distance_dot requires exactly two arguments", DistanceKind.Dot);

    internal static SqlValue Concat(IReadOnlyList<SqlValue> arguments)
    {
        RequireCount(arguments, 2, "concat requires exactly two arguments");
        var left = Parse(arguments[0], null);
        var right = Parse(arguments[1], null);
        if (left.Type != right.Type)
            throw new EmbeddedSqlException("Mismatched vector types");

        byte[] data;
        switch (left.Type)
        {
            case VectorType.Float32Dense:
            case VectorType.Float64Dense:
                data = new byte[checked(left.Data.Length + right.Data.Length)];
                left.Data.CopyTo(data, 0);
                right.Data.CopyTo(data, left.Data.Length);
                break;

            case VectorType.Float32Sparse:
                var leftEntries = left.Data.Length / 8;
                var rightEntries = right.Data.Length / 8;
                data = new byte[checked(left.Data.Length + right.Data.Length)];
                left.Data.AsSpan(0, leftEntries * 4).CopyTo(data);
                right.Data.AsSpan(0, rightEntries * 4).CopyTo(data.AsSpan(leftEntries * 4));
                left.Data.AsSpan(leftEntries * 4).CopyTo(data.AsSpan((leftEntries + rightEntries) * 4));
                var rightIndexOffset = (leftEntries * 2 + rightEntries) * 4;
                for (var entry = 0; entry < rightEntries; entry++)
                {
                    var index = checked(ReadSparseIndex(right, entry) + (uint)left.Dimensions);
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        data.AsSpan(rightIndexOffset + entry * 4, 4),
                        index);
                }
                break;

            case VectorType.Float1Bit:
            case VectorType.Float8:
                throw new EmbeddedSqlException("vector_concat is not supported for float1bit/float8 vectors");

            default:
                throw new InvalidOperationException($"Unknown vector type {left.Type}.");
        }

        return SqlValue.Blob(Serialize(new VectorValue(
            left.Type,
            checked(left.Dimensions + right.Dimensions),
            data)));
    }

    internal static SqlValue Slice(IReadOnlyList<SqlValue> arguments)
    {
        RequireCount(arguments, 3, "vector_slice requires exactly three arguments");
        var vector = Parse(arguments[0], null);
        var start = ReadSliceIndex(arguments[1], "start index must be an integer");
        var end = ReadSliceIndex(arguments[2], "end_index must be an integer");
        if (start < 0 || end < 0)
            throw new EmbeddedSqlException("start index and end_index must be non-negative");
        if (start > end)
            throw new EmbeddedSqlException("start index must not be greater than end index");
        if (end > vector.Dimensions)
            throw new EmbeddedSqlException("vector_slice range out of bounds");

        var startIndex = (int)start;
        var endIndex = (int)end;
        VectorValue result;
        switch (vector.Type)
        {
            case VectorType.Float32Dense:
                result = SliceDense(vector, startIndex, endIndex, sizeof(float));
                break;
            case VectorType.Float64Dense:
                result = SliceDense(vector, startIndex, endIndex, sizeof(double));
                break;
            case VectorType.Float32Sparse:
                result = SliceSparse(vector, startIndex, endIndex);
                break;
            case VectorType.Float1Bit:
            case VectorType.Float8:
                throw new EmbeddedSqlException("vector_slice is not supported for float1bit/float8 vectors");
            default:
                throw new InvalidOperationException($"Unknown vector type {vector.Type}.");
        }

        return SqlValue.Blob(Serialize(result));
    }

    private enum DistanceKind
    {
        Cosine,
        L2,
        Jaccard,
        Dot,
    }

    private static SqlValue Distance(
        IReadOnlyList<SqlValue> arguments,
        string countError,
        DistanceKind kind)
    {
        RequireCount(arguments, 2, countError);
        var left = Parse(arguments[0], null);
        var right = Parse(arguments[1], null);
        if (left.Dimensions != right.Dimensions)
            throw new EmbeddedSqlException("Vectors must have the same dimensions");
        if (left.Type != right.Type)
            throw new EmbeddedSqlException("Vectors must be of the same type");

        var value = left.Type switch
        {
            VectorType.Float32Dense => DistanceFloat32(left, right, kind),
            VectorType.Float64Dense => DistanceFloat64(left, right, kind),
            VectorType.Float32Sparse => DistanceSparse(left, right, kind),
            VectorType.Float1Bit => Distance1Bit(left, right, kind),
            VectorType.Float8 => DistanceFloat8(left, right, kind),
            _ => throw new InvalidOperationException($"Unknown vector type {left.Type}."),
        };
        return SqlValue.Real(value);
    }

    private static double DistanceFloat32(VectorValue left, VectorValue right, DistanceKind kind)
    {
        var dot = 0.0f;
        var leftNorm = 0.0f;
        var rightNorm = 0.0f;
        var l2 = 0.0f;
        var min = 0.0f;
        var max = 0.0f;
        var dot64 = 0.0;
        for (var index = 0; index < left.Dimensions; index++)
        {
            var a = ReadFloat(left.Data, index);
            var b = ReadFloat(right.Data, index);
            dot += a * b;
            dot64 += (double)a * b;
            leftNorm += a * a;
            rightNorm += b * b;
            var difference = a - b;
            l2 += difference * difference;
            min += RustMin(a, b);
            max += RustMax(a, b);
        }

        return kind switch
        {
            DistanceKind.Cosine => leftNorm == 0.0f || rightNorm == 0.0f
                ? leftNorm == rightNorm ? 0.0 : 1.0
                : 1.0f - dot / MathF.Sqrt(leftNorm * rightNorm),
            DistanceKind.L2 => Math.Sqrt(l2),
            DistanceKind.Jaccard => max == 0.0f ? double.NaN : 1.0 - min / max,
            DistanceKind.Dot => -dot64,
            _ => throw new InvalidOperationException($"Unknown distance kind {kind}."),
        };
    }

    private static double DistanceFloat64(VectorValue left, VectorValue right, DistanceKind kind)
    {
        var dot = 0.0;
        var leftNorm = 0.0;
        var rightNorm = 0.0;
        var l2 = 0.0;
        var min = 0.0;
        var max = 0.0;
        for (var index = 0; index < left.Dimensions; index++)
        {
            var a = ReadDouble(left.Data, index);
            var b = ReadDouble(right.Data, index);
            dot += a * b;
            leftNorm += a * a;
            rightNorm += b * b;
            var difference = a - b;
            l2 += difference * difference;
            min += RustMin(a, b);
            max += RustMax(a, b);
        }

        return kind switch
        {
            DistanceKind.Cosine => leftNorm == 0.0 || rightNorm == 0.0
                ? leftNorm == rightNorm ? 0.0 : 1.0
                : 1.0 - dot / Math.Sqrt(leftNorm * rightNorm),
            DistanceKind.L2 => Math.Sqrt(l2),
            DistanceKind.Jaccard => max == 0.0 ? double.NaN : 1.0 - min / max,
            DistanceKind.Dot => -dot,
            _ => throw new InvalidOperationException($"Unknown distance kind {kind}."),
        };
    }

    private static double DistanceSparse(VectorValue left, VectorValue right, DistanceKind kind)
    {
        var leftEntries = left.Data.Length / 8;
        var rightEntries = right.Data.Length / 8;
        var leftPosition = 0;
        var rightPosition = 0;
        var dot = 0.0f;
        var dot64 = 0.0;
        var leftNorm = 0.0f;
        var rightNorm = 0.0f;
        var l2 = 0.0f;
        var min = 0.0f;
        var max = 0.0f;

        while (leftPosition < leftEntries && rightPosition < rightEntries)
        {
            var leftIndex = ReadSparseIndex(left, leftPosition);
            var rightIndex = ReadSparseIndex(right, rightPosition);
            if (leftIndex == rightIndex)
            {
                var a = ReadSparseValue(left, leftPosition++);
                var b = ReadSparseValue(right, rightPosition++);
                dot += a * b;
                dot64 += (double)a * b;
                leftNorm += a * a;
                rightNorm += b * b;
                var difference = a - b;
                l2 += difference * difference;
                min += RustMin(a, b);
                max += RustMax(a, b);
            }
            else if (leftIndex < rightIndex)
            {
                AccumulateSparseOnly(ReadSparseValue(left, leftPosition++), ref leftNorm, ref l2, ref min, ref max);
            }
            else
            {
                AccumulateSparseOnly(ReadSparseValue(right, rightPosition++), ref rightNorm, ref l2, ref min, ref max);
            }
        }

        while (leftPosition < leftEntries)
            AccumulateSparseOnly(ReadSparseValue(left, leftPosition++), ref leftNorm, ref l2, ref min, ref max);
        while (rightPosition < rightEntries)
            AccumulateSparseOnly(ReadSparseValue(right, rightPosition++), ref rightNorm, ref l2, ref min, ref max);

        return kind switch
        {
            DistanceKind.Cosine => leftNorm == 0.0f || rightNorm == 0.0f
                ? double.NaN
                : 1.0f - dot / MathF.Sqrt(leftNorm * rightNorm),
            DistanceKind.L2 => Math.Sqrt(l2),
            DistanceKind.Jaccard => max == 0.0f ? double.NaN : 1.0 - min / max,
            DistanceKind.Dot => -dot64,
            _ => throw new InvalidOperationException($"Unknown distance kind {kind}."),
        };
    }

    private static void AccumulateSparseOnly(
        float value,
        ref float norm,
        ref float l2,
        ref float min,
        ref float max)
    {
        norm += value * value;
        l2 += value * value;
        min += RustMin(value, 0.0f);
        max += RustMax(value, 0.0f);
    }

    private static double Distance1Bit(VectorValue left, VectorValue right, DistanceKind kind)
    {
        var hamming = 0;
        var intersection = 0;
        var union = 0;
        for (var index = 0; index < left.Data.Length; index++)
        {
            var a = left.Data[index];
            var b = right.Data[index];
            if (index == left.Data.Length - 1 && left.Dimensions % 8 != 0)
            {
                var semanticBits = (byte)((1 << (left.Dimensions % 8)) - 1);
                a &= semanticBits;
                b &= semanticBits;
            }
            hamming += PopCount((byte)(a ^ b));
            intersection += PopCount((byte)(a & b));
            union += PopCount((byte)(a | b));
        }

        return kind switch
        {
            DistanceKind.Cosine => hamming,
            DistanceKind.L2 => throw new EmbeddedSqlException("L2 distance is not supported for float1bit vectors"),
            DistanceKind.Jaccard => union == 0 ? double.NaN : 1.0 - (double)intersection / union,
            DistanceKind.Dot => -(left.Dimensions - 2.0 * hamming),
            _ => throw new InvalidOperationException($"Unknown distance kind {kind}."),
        };
    }

    private static double DistanceFloat8(VectorValue left, VectorValue right, DistanceKind kind)
    {
        ReadFloat8Metadata(left, out var leftAlpha, out var leftShift);
        ReadFloat8Metadata(right, out var rightAlpha, out var rightShift);
        if (kind is DistanceKind.Cosine or DistanceKind.Dot)
        {
            ulong leftSum = 0;
            ulong rightSum = 0;
            ulong leftSquareSum = 0;
            ulong rightSquareSum = 0;
            ulong integerDot = 0;
            for (var index = 0; index < left.Dimensions; index++)
            {
                var a = (ulong)left.Data[index];
                var b = (ulong)right.Data[index];
                leftSum += a;
                rightSum += b;
                leftSquareSum += a * a;
                rightSquareSum += b * b;
                integerDot += a * b;
            }

            var leftScale = (double)leftAlpha;
            var rightScale = (double)rightAlpha;
            var leftOffset = (double)leftShift;
            var rightOffset = (double)rightShift;
            var dimensions = (double)left.Dimensions;
            var dot = leftScale * rightScale * integerDot
                + leftScale * rightOffset * leftSum
                + rightScale * leftOffset * rightSum
                + leftOffset * rightOffset * dimensions;
            if (kind == DistanceKind.Dot)
                return -dot;

            var leftNorm = leftScale * leftScale * leftSquareSum
                + 2.0 * leftScale * leftOffset * leftSum
                + leftOffset * leftOffset * dimensions;
            var rightNorm = rightScale * rightScale * rightSquareSum
                + 2.0 * rightScale * rightOffset * rightSum
                + rightOffset * rightOffset * dimensions;
            return 1.0 - dot / Math.Sqrt(leftNorm * rightNorm);
        }

        var l2 = 0.0;
        var min = 0.0;
        var max = 0.0;
        for (var index = 0; index < left.Dimensions; index++)
        {
            var a = (double)leftAlpha * left.Data[index] + leftShift;
            var b = (double)rightAlpha * right.Data[index] + rightShift;
            var difference = a - b;
            l2 += difference * difference;
            min += RustMin(a, b);
            max += RustMax(a, b);
        }

        return kind switch
        {
            DistanceKind.L2 => Math.Sqrt(l2),
            DistanceKind.Jaccard => max == 0.0 ? double.NaN : 1.0 - min / max,
            _ => throw new InvalidOperationException($"Unknown distance kind {kind}."),
        };
    }

    private static VectorValue Parse(SqlValue value, VectorType? typeHint)
    {
        return value.Kind switch
        {
            SqlValueKind.Text => ParseText(value.AsText(), typeHint ?? VectorType.Float32Dense),
            SqlValueKind.Blob => ParseBlob(value.AsBlobSpan()),
            _ => throw new EmbeddedSqlException("Invalid vector type"),
        };
    }

    private static VectorValue ParseBlob(ReadOnlySpan<byte> blob)
    {
        VectorType type;
        int dataLength;
        var explicitDimensions = 0;
        if ((blob.Length & 1) == 0)
        {
            type = VectorType.Float32Dense;
            dataLength = blob.Length;
        }
        else
        {
            var typeByte = blob[^1];
            switch (typeByte)
            {
                case 1:
                    type = VectorType.Float32Dense;
                    dataLength = blob.Length - 1;
                    break;
                case 2:
                    type = VectorType.Float64Dense;
                    dataLength = blob.Length - 1;
                    break;
                case 3:
                    type = VectorType.Float1Bit;
                    var oneBitMetadataLength = blob.Length - 1;
                    if (oneBitMetadataLength == 0 || (oneBitMetadataLength & 1) != 0)
                        throw new EmbeddedSqlException("float1bit vector blob length must be even and non-empty");
                    var trailingBits = blob[oneBitMetadataLength - 1];
                    var bitCapacity = checked(oneBitMetadataLength * 8);
                    if (trailingBits > bitCapacity)
                        throw new EmbeddedSqlException($"float1bit vector trailing bits {trailingBits} exceed blob capacity");
                    explicitDimensions = bitCapacity - trailingBits;
                    dataLength = (explicitDimensions + 7) / 8;
                    if (dataLength >= oneBitMetadataLength)
                    {
                        throw new EmbeddedSqlException(
                            $"float1bit vector needs {dataLength} data bytes but blob holds {oneBitMetadataLength}");
                    }

                    break;
                case 4:
                    type = VectorType.Float8;
                    var float8MetadataLength = blob.Length - 1;
                    if (float8MetadataLength < 2 || (float8MetadataLength & 1) != 0)
                        throw new EmbeddedSqlException("float8 vector blob must have even length >= 2 (excluding type byte)");
                    var trailingBytes = blob[float8MetadataLength - 1];
                    var dimensions = float8MetadataLength - 10 - trailingBytes;
                    if (dimensions < 0)
                    {
                        throw new EmbeddedSqlException(
                            $"float8 vector blob of {float8MetadataLength} bytes is too short for {trailingBytes} trailing bytes");
                    }

                    explicitDimensions = dimensions;
                    dataLength = float8MetadataLength - 2;
                    break;
                case 5:
                case 6:
                    throw new EmbeddedSqlException("unsupported vector type from LibSQL");
                case 9:
                    type = VectorType.Float32Sparse;
                    dataLength = blob.Length - 1;
                    break;
                default:
                    throw new EmbeddedSqlException($"unknown vector type: {typeByte}");
            }
        }

        var data = blob[..dataLength].ToArray();
        return Validate(type, explicitDimensions, data);
    }

    private static VectorValue Validate(VectorType type, int explicitDimensions, byte[] data)
    {
        switch (type)
        {
            case VectorType.Float32Dense:
                if (data.Length % sizeof(float) != 0)
                    throw new EmbeddedSqlException($"f32 dense vector unexpected data length: {data.Length}");
                return new VectorValue(type, data.Length / sizeof(float), data);

            case VectorType.Float64Dense:
                if (data.Length % sizeof(double) != 0)
                    throw new EmbeddedSqlException($"f64 dense vector unexpected data length: {data.Length}");
                return new VectorValue(type, data.Length / sizeof(double), data);

            case VectorType.Float32Sparse:
                if (data.Length == 0 || data.Length % 4 != 0 || (data.Length - 4) % 8 != 0)
                    throw new EmbeddedSqlException($"f32 sparse vector unexpected data length: {data.Length}");
                var dimensions = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(data.Length - 4));
                if (dimensions > int.MaxValue)
                    throw new EmbeddedSqlException($"f32 sparse vector dimensions exceed managed limit: {dimensions}");
                var entries = (data.Length - 4) / 8;
                for (var entry = 0; entry < entries; entry++)
                {
                    var index = BinaryPrimitives.ReadUInt32LittleEndian(
                        data.AsSpan(entries * 4 + entry * 4, 4));
                    if (index >= dimensions)
                    {
                        throw new EmbeddedSqlException(
                            $"f32 sparse vector index {index} out of range for {dimensions} dims");
                    }
                }

                Array.Resize(ref data, data.Length - 4);
                return new VectorValue(type, (int)dimensions, data);

            case VectorType.Float1Bit:
                var expectedOneBitLength = (explicitDimensions + 7) / 8;
                if (explicitDimensions == 0 || data.Length != expectedOneBitLength)
                {
                    throw new EmbeddedSqlException(
                        $"f1bit vector data length mismatch: got {data.Length} expected {expectedOneBitLength} for {explicitDimensions} dims");
                }

                return new VectorValue(type, explicitDimensions, data);

            case VectorType.Float8:
                if (data.Length < 8)
                    throw new EmbeddedSqlException($"f8 vector data too short: {data.Length}");
                var expectedFloat8Length = checked(Align4(explicitDimensions) + 8);
                if (explicitDimensions == 0 || data.Length != expectedFloat8Length)
                {
                    throw new EmbeddedSqlException(
                        $"f8 vector data length mismatch: got {data.Length} expected {expectedFloat8Length} for {explicitDimensions} dims");
                }

                return new VectorValue(type, explicitDimensions, data);

            default:
                throw new InvalidOperationException($"Unknown vector type {type}.");
        }
    }

    private static VectorValue ParseText(string text, VectorType type)
    {
        text = text.Trim();
        if (text.Length < 2 || text[0] != '[' || text[^1] != ']')
            throw new EmbeddedSqlException("Invalid vector value");

        var content = text[1..^1];
        if (string.IsNullOrWhiteSpace(content))
        {
            if (type == VectorType.Float1Bit)
                throw new EmbeddedSqlException("empty vector not supported for this type");
            if (type == VectorType.Float8)
                return CreateFloat8([]);
            return new VectorValue(type, 0, []);
        }

        var tokens = content.Split(',');
        return type switch
        {
            VectorType.Float32Dense => CreateDenseFloat32(ParseFloat32(tokens)),
            VectorType.Float64Dense => CreateDenseFloat64(ParseFloat64(tokens)),
            VectorType.Float32Sparse => CreateSparse(ParseFloat32(tokens)),
            VectorType.Float1Bit => Create1Bit(ParseFloat32(tokens)),
            VectorType.Float8 => CreateFloat8(ParseFloat32(tokens)),
            _ => throw new InvalidOperationException($"Unknown vector type {type}."),
        };
    }

    private static float[] ParseFloat32(string[] tokens)
    {
        var values = new float[tokens.Length];
        for (var index = 0; index < tokens.Length; index++)
        {
            if (!float.TryParse(tokens[index].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || !float.IsFinite(value))
            {
                throw new EmbeddedSqlException("Invalid vector value");
            }

            values[index] = value;
        }

        return values;
    }

    private static double[] ParseFloat64(string[] tokens)
    {
        var values = new double[tokens.Length];
        for (var index = 0; index < tokens.Length; index++)
        {
            if (!double.TryParse(tokens[index].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || !double.IsFinite(value))
            {
                throw new EmbeddedSqlException("Invalid vector value");
            }

            values[index] = value;
        }

        return values;
    }

    private static VectorValue Convert(VectorValue vector, VectorType target)
    {
        if (vector.Type == target)
            return vector;

        if (vector.Type == VectorType.Float64Dense)
        {
            if (target == VectorType.Float32Sparse)
                return CreateSparse(ToFloat64(vector));
            if (target == VectorType.Float1Bit)
                return Create1Bit(ToFloat64(vector));
        }

        return target switch
        {
            VectorType.Float32Dense => CreateDenseFloat32(ToFloat32(vector)),
            VectorType.Float64Dense => CreateDenseFloat64(ToFloat64(vector)),
            VectorType.Float32Sparse => CreateSparse(ToFloat32(vector)),
            VectorType.Float1Bit => Create1Bit(ToFloat32(vector)),
            VectorType.Float8 => CreateFloat8ForConversion(ToFloat32(vector)),
            _ => throw new InvalidOperationException($"Unknown vector type {target}."),
        };
    }

    private static float[] ToFloat32(VectorValue vector)
    {
        var values = new float[vector.Dimensions];
        switch (vector.Type)
        {
            case VectorType.Float32Dense:
                for (var index = 0; index < values.Length; index++)
                    values[index] = ReadFloat(vector.Data, index);
                break;
            case VectorType.Float64Dense:
                for (var index = 0; index < values.Length; index++)
                    values[index] = (float)ReadDouble(vector.Data, index);
                break;
            case VectorType.Float32Sparse:
                for (var entry = 0; entry < vector.Data.Length / 8; entry++)
                    values[checked((int)ReadSparseIndex(vector, entry))] = ReadSparseValue(vector, entry);
                break;
            case VectorType.Float1Bit:
                for (var index = 0; index < values.Length; index++)
                    values[index] = IsBitSet(vector.Data, index) ? 1.0f : -1.0f;
                break;
            case VectorType.Float8:
                ReadFloat8Metadata(vector, out var alpha, out var shift);
                for (var index = 0; index < values.Length; index++)
                    values[index] = alpha * vector.Data[index] + shift;
                break;
            default:
                throw new InvalidOperationException($"Unknown vector type {vector.Type}.");
        }

        return values;
    }

    private static double[] ToFloat64(VectorValue vector)
    {
        var values = new double[vector.Dimensions];
        switch (vector.Type)
        {
            case VectorType.Float64Dense:
                for (var index = 0; index < values.Length; index++)
                    values[index] = ReadDouble(vector.Data, index);
                break;
            case VectorType.Float32Dense:
                for (var index = 0; index < values.Length; index++)
                    values[index] = ReadFloat(vector.Data, index);
                break;
            case VectorType.Float32Sparse:
                for (var entry = 0; entry < vector.Data.Length / 8; entry++)
                    values[checked((int)ReadSparseIndex(vector, entry))] = ReadSparseValue(vector, entry);
                break;
            case VectorType.Float1Bit:
                for (var index = 0; index < values.Length; index++)
                    values[index] = IsBitSet(vector.Data, index) ? 1.0 : -1.0;
                break;
            case VectorType.Float8:
                ReadFloat8Metadata(vector, out var alpha, out var shift);
                for (var index = 0; index < values.Length; index++)
                    values[index] = (double)alpha * vector.Data[index] + shift;
                break;
            default:
                throw new InvalidOperationException($"Unknown vector type {vector.Type}.");
        }

        return values;
    }

    private static VectorValue CreateDenseFloat32(float[] values)
    {
        var data = new byte[checked(values.Length * sizeof(float))];
        for (var index = 0; index < values.Length; index++)
            WriteFloat(data, index, values[index]);
        return new VectorValue(VectorType.Float32Dense, values.Length, data);
    }

    private static VectorValue CreateDenseFloat64(double[] values)
    {
        var data = new byte[checked(values.Length * sizeof(double))];
        for (var index = 0; index < values.Length; index++)
            WriteDouble(data, index, values[index]);
        return new VectorValue(VectorType.Float64Dense, values.Length, data);
    }

    private static VectorValue CreateSparse(float[] values)
    {
        var entries = 0;
        foreach (var value in values)
        {
            if (value != 0.0f)
                entries++;
        }

        var data = new byte[checked(entries * 8)];
        var entry = 0;
        for (var index = 0; index < values.Length; index++)
        {
            if (values[index] == 0.0f)
                continue;
            WriteFloat(data, entry, values[index]);
            BinaryPrimitives.WriteUInt32LittleEndian(
                data.AsSpan(entries * 4 + entry * 4, 4),
                (uint)index);
            entry++;
        }

        return new VectorValue(VectorType.Float32Sparse, values.Length, data);
    }

    private static VectorValue CreateSparse(double[] values)
    {
        var entries = 0;
        foreach (var value in values)
        {
            if (value != 0.0)
                entries++;
        }

        var data = new byte[checked(entries * 8)];
        var entry = 0;
        for (var index = 0; index < values.Length; index++)
        {
            if (values[index] == 0.0)
                continue;
            WriteFloat(data, entry, (float)values[index]);
            BinaryPrimitives.WriteUInt32LittleEndian(
                data.AsSpan(entries * 4 + entry * 4, 4),
                (uint)index);
            entry++;
        }

        return new VectorValue(VectorType.Float32Sparse, values.Length, data);
    }

    private static VectorValue Create1Bit(float[] values)
    {
        var data = new byte[(values.Length + 7) / 8];
        for (var index = 0; index < values.Length; index++)
        {
            if (values[index] > 0.0f)
                data[index / 8] |= (byte)(1 << (index & 7));
        }

        return new VectorValue(VectorType.Float1Bit, values.Length, data);
    }

    private static VectorValue Create1Bit(double[] values)
    {
        var data = new byte[(values.Length + 7) / 8];
        for (var index = 0; index < values.Length; index++)
        {
            if (values[index] > 0.0)
                data[index / 8] |= (byte)(1 << (index & 7));
        }

        return new VectorValue(VectorType.Float1Bit, values.Length, data);
    }

    private static VectorValue CreateFloat8(float[] values)
        => CreateFloat8(values, textSemantics: true);

    private static VectorValue CreateFloat8ForConversion(float[] values)
        => CreateFloat8(values, textSemantics: false);

    private static VectorValue CreateFloat8(float[] values, bool textSemantics)
    {
        if (values.Length == 0)
            return new VectorValue(VectorType.Float8, 0, new byte[8]);

        var minimum = float.PositiveInfinity;
        var maximum = float.NegativeInfinity;
        foreach (var value in values)
        {
            if (textSemantics)
            {
                minimum = RustMin(minimum, value);
                maximum = RustMax(maximum, value);
            }
            else
            {
                if (value < minimum)
                    minimum = value;
                if (value > maximum)
                    maximum = value;
            }
        }

        var alpha = (maximum - minimum) / 255.0f;
        var data = new byte[checked(Align4(values.Length) + 8)];
        for (var index = 0; index < values.Length; index++)
        {
            var quantized = alpha == 0.0f
                ? 0
                : QuantizeFloat8((values[index] - minimum) / alpha + 0.5f);
            data[index] = (byte)quantized;
        }

        WriteFloatAtByteOffset(data, Align4(values.Length), alpha);
        WriteFloatAtByteOffset(data, Align4(values.Length) + 4, minimum);
        return new VectorValue(VectorType.Float8, values.Length, data);
    }

    private static byte[] Serialize(VectorValue vector)
    {
        switch (vector.Type)
        {
            case VectorType.Float32Dense:
                return vector.Data.ToArray();

            case VectorType.Float64Dense:
                var float64 = new byte[vector.Data.Length + 1];
                vector.Data.CopyTo(float64, 0);
                float64[^1] = 2;
                return float64;

            case VectorType.Float32Sparse:
                var sparse = new byte[checked(vector.Data.Length + 5)];
                vector.Data.CopyTo(sparse, 0);
                BinaryPrimitives.WriteUInt32LittleEndian(
                    sparse.AsSpan(vector.Data.Length, 4),
                    checked((uint)vector.Dimensions));
                sparse[^1] = 9;
                return sparse;

            case VectorType.Float1Bit:
                var dataSize = (vector.Dimensions + 7) / 8;
                var needsPadding = (dataSize & 1) == 0;
                var oneBit = new byte[checked(dataSize + (needsPadding ? 1 : 0) + 2)];
                vector.Data.AsSpan(0, dataSize).CopyTo(oneBit);
                var trailingOffset = dataSize + (needsPadding ? 1 : 0);
                oneBit[trailingOffset] = checked((byte)((oneBit.Length - 1) * 8 - vector.Dimensions));
                oneBit[^1] = 3;
                return oneBit;

            case VectorType.Float8:
                var trailingBytes = Align4(vector.Dimensions) - vector.Dimensions;
                var float8 = new byte[checked(vector.Data.Length + 3)];
                vector.Data.CopyTo(float8, 0);
                float8[^2] = checked((byte)trailingBytes);
                float8[^1] = 4;
                return float8;

            default:
                throw new InvalidOperationException($"Unknown vector type {vector.Type}.");
        }
    }

    private static string Format(VectorValue vector)
    {
        var output = new StringBuilder();
        output.Append('[');
        for (var index = 0; index < vector.Dimensions; index++)
        {
            if (index != 0)
                output.Append(',');

            switch (vector.Type)
            {
                case VectorType.Float32Dense:
                    output.Append(FormatFloat(ReadFloat(vector.Data, index)));
                    break;
                case VectorType.Float64Dense:
                    output.Append(FormatDouble(ReadDouble(vector.Data, index)));
                    break;
                case VectorType.Float32Sparse:
                    output.Append(FormatFloat(ReadSparseDimension(vector, index)));
                    break;
                case VectorType.Float1Bit:
                    output.Append(IsBitSet(vector.Data, index) ? '1' : "-1");
                    break;
                case VectorType.Float8:
                    ReadFloat8Metadata(vector, out var alpha, out var shift);
                    output.Append(FormatFloat(alpha * vector.Data[index] + shift));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown vector type {vector.Type}.");
            }
        }

        output.Append(']');
        return output.ToString();
    }

    private static VectorValue SliceDense(VectorValue vector, int start, int end, int elementSize)
    {
        var length = checked((end - start) * elementSize);
        return new VectorValue(
            vector.Type,
            end - start,
            vector.Data.AsSpan(checked(start * elementSize), length).ToArray());
    }

    private static VectorValue SliceSparse(VectorValue vector, int start, int end)
    {
        var entryCount = vector.Data.Length / 8;
        var selected = 0;
        for (var entry = 0; entry < entryCount; entry++)
        {
            var index = ReadSparseIndex(vector, entry);
            if (index >= start && index < end)
                selected++;
        }

        var data = new byte[selected * 8];
        var destination = 0;
        for (var entry = 0; entry < entryCount; entry++)
        {
            var index = ReadSparseIndex(vector, entry);
            if (index < start || index >= end)
                continue;
            WriteFloat(data, destination, ReadSparseValue(vector, entry));
            BinaryPrimitives.WriteUInt32LittleEndian(
                data.AsSpan(selected * 4 + destination * 4, 4),
                index - (uint)start);
            destination++;
        }

        return new VectorValue(VectorType.Float32Sparse, end - start, data);
    }

    private static float ReadSparseDimension(VectorValue vector, int dimension)
    {
        var entries = vector.Data.Length / 8;
        var value = 0.0f;
        for (var entry = 0; entry < entries; entry++)
        {
            var index = ReadSparseIndex(vector, entry);
            if (index == dimension)
                value = ReadSparseValue(vector, entry);
        }

        return value;
    }

    private static float ReadSparseValue(VectorValue vector, int entry)
        => ReadFloat(vector.Data, entry);

    private static uint ReadSparseIndex(VectorValue vector, int entry)
    {
        var entries = vector.Data.Length / 8;
        return BinaryPrimitives.ReadUInt32LittleEndian(
            vector.Data.AsSpan(entries * 4 + entry * 4, 4));
    }

    private static void ReadFloat8Metadata(VectorValue vector, out float alpha, out float shift)
    {
        var offset = Align4(vector.Dimensions);
        alpha = ReadFloatAtByteOffset(vector.Data, offset);
        shift = ReadFloatAtByteOffset(vector.Data, offset + 4);
    }

    private static float ReadFloat(byte[] data, int index)
        => ReadFloatAtByteOffset(data, checked(index * sizeof(float)));

    private static float ReadFloatAtByteOffset(byte[] data, int offset)
        => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4)));

    private static double ReadDouble(byte[] data, int index)
        => BitConverter.Int64BitsToDouble(
            BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(checked(index * sizeof(double)), 8)));

    private static void WriteFloat(byte[] data, int index, float value)
        => WriteFloatAtByteOffset(data, checked(index * sizeof(float)), value);

    private static void WriteFloatAtByteOffset(byte[] data, int offset, float value)
        => BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(value));

    private static void WriteDouble(byte[] data, int index, double value)
        => BinaryPrimitives.WriteInt64LittleEndian(
            data.AsSpan(checked(index * sizeof(double)), 8),
            BitConverter.DoubleToInt64Bits(value));

    private static bool IsBitSet(byte[] data, int index)
        => ((data[index / 8] >> (index & 7)) & 1) != 0;

    private static int PopCount(byte value)
    {
        var count = 0;
        while (value != 0)
        {
            value &= (byte)(value - 1);
            count++;
        }

        return count;
    }

    private static int Align4(int value) => checked((value + 3) / 4 * 4);

    private static long ReadSliceIndex(SqlValue value, string error)
        => value.Kind == SqlValueKind.Integer
            ? value.AsInteger()
            : throw new EmbeddedSqlException(error);

    private static string FormatFloat(float value)
        => FormatFloatingPoint(value, value.ToString("R", CultureInfo.InvariantCulture));

    private static string FormatDouble(double value)
        => FormatFloatingPoint(value, value.ToString("R", CultureInfo.InvariantCulture));

    private static string FormatFloatingPoint(double value, string formatted)
    {
        if (double.IsNaN(value))
            return "NaN";
        if (double.IsPositiveInfinity(value))
            return "inf";
        if (double.IsNegativeInfinity(value))
            return "-inf";
        var exponentMarker = formatted.IndexOfAny(['E', 'e']);
        return exponentMarker < 0 ? formatted : ExpandScientificNotation(formatted, exponentMarker);
    }

    private static string ExpandScientificNotation(string formatted, int exponentMarker)
    {
        var negative = formatted[0] == '-';
        var mantissaStart = negative ? 1 : 0;
        var mantissa = formatted[mantissaStart..exponentMarker];
        var exponent = int.Parse(formatted[(exponentMarker + 1)..], CultureInfo.InvariantCulture);
        var decimalPoint = mantissa.IndexOf('.');
        var originalDecimalPosition = decimalPoint < 0 ? mantissa.Length : decimalPoint;
        var digits = decimalPoint < 0 ? mantissa : mantissa.Remove(decimalPoint, 1);
        var decimalPosition = originalDecimalPosition + exponent;

        var output = new StringBuilder(digits.Length + Math.Abs(exponent) + 3);
        if (negative)
            output.Append('-');
        if (decimalPosition <= 0)
        {
            output.Append("0.");
            output.Append('0', -decimalPosition);
            output.Append(digits);
        }
        else if (decimalPosition >= digits.Length)
        {
            output.Append(digits);
            output.Append('0', decimalPosition - digits.Length);
        }
        else
        {
            output.Append(digits.AsSpan(0, decimalPosition));
            output.Append('.');
            output.Append(digits.AsSpan(decimalPosition));
        }

        return output.ToString();
    }

    private static int QuantizeFloat8(float value)
    {
        if (float.IsNaN(value))
            return 0;
        if (value <= 0.0f)
            return 0;
        if (value >= 255.0f)
            return 255;
        return (int)value;
    }

    private static float RustMin(float left, float right)
        => float.IsNaN(left) ? right : float.IsNaN(right) ? left : MathF.Min(left, right);

    private static float RustMax(float left, float right)
        => float.IsNaN(left) ? right : float.IsNaN(right) ? left : MathF.Max(left, right);

    private static double RustMin(double left, double right)
        => double.IsNaN(left) ? right : double.IsNaN(right) ? left : Math.Min(left, right);

    private static double RustMax(double left, double right)
        => double.IsNaN(left) ? right : double.IsNaN(right) ? left : Math.Max(left, right);

    private static void RequireCount(IReadOnlyList<SqlValue> arguments, int count, string error)
    {
        if (arguments.Count != count)
            throw new EmbeddedSqlException(error);
    }
}
