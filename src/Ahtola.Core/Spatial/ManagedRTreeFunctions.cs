using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Ahtola.Core.Spatial;

internal static class ManagedRTreeFunctions
{
    public static SqlValue Depth(IReadOnlyList<SqlValue> arguments)
    {
        RequireCount("rtreedepth", arguments, 1);
        if (arguments[0].Kind != SqlValueKind.Blob)
            throw new EmbeddedSqlException("Invalid argument to rtreedepth()");

        var blob = arguments[0].AsBlob().Span;
        if (blob.Length < 2)
            throw new EmbeddedSqlException("Invalid argument to rtreedepth()");
        return SqlValue.Integer(BinaryPrimitives.ReadUInt16BigEndian(blob));
    }

    public static SqlValue Node(IReadOnlyList<SqlValue> arguments)
    {
        RequireCount("rtreenode", arguments, 2);
        var dimensions = ToSqliteInt32(arguments[0]);
        if (dimensions is < 1 or > 5 || arguments[1].Kind != SqlValueKind.Blob)
            return SqlValue.Null;

        var blob = arguments[1].AsBlob().Span;
        if (blob.Length < 4)
            return SqlValue.Null;
        var cellCount = BinaryPrimitives.ReadUInt16BigEndian(blob[2..]);
        var cellSize = 8 + (dimensions * 8);
        if (cellCount > (blob.Length - 4) / cellSize)
            return SqlValue.Null;
        if (cellCount == 0)
            return SqlValue.Text("not an error");

        var builder = new StringBuilder();
        var offset = 4;
        for (var cell = 0; cell < cellCount; cell++)
        {
            if (cell != 0)
                builder.Append(' ');
            builder.Append('{');
            builder.Append(BinaryPrimitives.ReadInt64BigEndian(blob[offset..]).ToString(CultureInfo.InvariantCulture));
            offset += sizeof(long);
            for (var coordinate = 0; coordinate < dimensions * 2; coordinate++)
            {
                var bits = BinaryPrimitives.ReadInt32BigEndian(blob[offset..]);
                offset += sizeof(int);
                builder.Append(' ');
                builder.Append(FormatCoordinate(BitConverter.Int32BitsToSingle(bits)));
            }
            builder.Append('}');
        }

        return SqlValue.Text(builder.ToString());
    }

    private static string FormatCoordinate(float value)
    {
        if (float.IsPositiveInfinity(value))
            return "Inf";
        if (float.IsNegativeInfinity(value))
            return "-Inf";
        return value.ToString("G6", CultureInfo.InvariantCulture).Replace('E', 'e');
    }

    private static int ToSqliteInt32(SqlValue value)
    {
        var numeric = EmbeddedDatabase.ApplySqliteNumericAffinity(value);
        var integer = numeric.Kind == SqlValueKind.Integer
            ? numeric.AsInteger()
            : ToSqliteInt64(numeric.AsReal());
        return unchecked((int)integer);
    }

    private static long ToSqliteInt64(double value)
    {
        if (double.IsNaN(value))
            return 0;
        if (value >= long.MaxValue)
            return long.MaxValue;
        if (value <= long.MinValue)
            return long.MinValue;
        return (long)Math.Truncate(value);
    }

    private static void RequireCount(
        string function,
        IReadOnlyList<SqlValue> arguments,
        int expected)
    {
        if (arguments.Count != expected)
            throw new EmbeddedSqlException($"wrong number of arguments to function {function}()");
    }
}
