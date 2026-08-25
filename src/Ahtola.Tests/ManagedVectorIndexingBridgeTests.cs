using Ahtola.Core;
using AwesomeAssertions;
using static Ahtola.Tests.ManagedVectorIndexTestHarness;

namespace Ahtola.Tests;

/// <summary>
/// Parity between the indexing-facing vector helpers and the SQL functions they delegate to.
/// </summary>
/// <remarks>
/// The index must never re-implement a distance: if <c>DistanceExact</c> and
/// <c>vector_distance_*</c> could disagree by a single bit, an indexed ranking and a scalar ranking
/// could order two rows differently, and the whole exactness argument would collapse.
/// </remarks>
public sealed class ManagedVectorIndexingBridgeTests
{
    private static readonly string[] Literals =
    [
        "[1,2,3,4]",
        "[-1.5,0.25,7.75,-3.5]",
        "[0,0,0,0]",
        "[1e10,-1e10,1e-10,-1e-10]",
    ];

    [TestCase(VectorTestEncoding.Float32, VectorTestMetric.L2)]
    [TestCase(VectorTestEncoding.Float32, VectorTestMetric.Cosine)]
    [TestCase(VectorTestEncoding.Float32, VectorTestMetric.Dot)]
    [TestCase(VectorTestEncoding.Float64, VectorTestMetric.L2)]
    [TestCase(VectorTestEncoding.Float64, VectorTestMetric.Cosine)]
    [TestCase(VectorTestEncoding.Float64, VectorTestMetric.Dot)]
    [TestCase(VectorTestEncoding.Float8, VectorTestMetric.L2)]
    [TestCase(VectorTestEncoding.Float8, VectorTestMetric.Cosine)]
    [TestCase(VectorTestEncoding.Float8, VectorTestMetric.Dot)]
    [TestCase(VectorTestEncoding.Float1Bit, VectorTestMetric.Cosine)]
    [TestCase(VectorTestEncoding.Float1Bit, VectorTestMetric.Dot)]
    public void DistanceExactMatchesTheScalarFunctionBitForBit(VectorTestEncoding encoding, VectorTestMetric metric)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        var constructor = Constructor(encoding);
        var function = DistanceFunction(metric);

        foreach (var left in Literals)
        {
            foreach (var right in Literals)
            {
                var leftValue = Query(connection, $"SELECT {constructor}('{left}');")[0][0];
                var rightValue = Query(connection, $"SELECT {constructor}('{right}');")[0][0];
                var scalar = Query(
                    connection,
                    $"SELECT {function}({constructor}('{left}'), {constructor}('{right}'));")[0][0];

                var bridged = SqliteVectorFunctions.DistanceExact(leftValue, rightValue, Kind(metric));
                var expected = scalar.Kind switch
                {
                    SqlValueKind.Integer => scalar.AsInteger(),
                    SqlValueKind.Real => scalar.AsReal(),
                    _ => double.NaN,
                };

                // A degenerate pair (two zero-norm float8 vectors under cosine) legitimately produces
                // a value SQL renders as NULL; the bridge reports it as NaN, which is what tells the
                // search it has no usable ordering. Bit comparison is reserved for orderable values.
                double.IsNaN(bridged).Should().Be(double.IsNaN(expected), $"{encoding}/{metric} {left} vs {right}");
                if (double.IsNaN(expected))
                    continue;

                BitConverter.DoubleToInt64Bits(bridged)
                    .Should().Be(BitConverter.DoubleToInt64Bits(expected), $"{encoding}/{metric} {left} vs {right}");
            }
        }
    }

    [Test]
    public void DecodingReportsTheEncodingAndDimensionsTheScalarPathSees()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        foreach (var (constructor, expected) in new (string, VectorEncodingKind)[]
                 {
                     ("vector32", VectorEncodingKind.Float32),
                     ("vector64", VectorEncodingKind.Float64),
                     ("vector8", VectorEncodingKind.Float8),
                     ("vector1bit", VectorEncodingKind.Float1Bit),
                 })
        {
            var value = Query(connection, $"SELECT {constructor}('[1,2,3,4]');")[0][0];
            SqliteVectorFunctions.TryDecodeVector(value, out var decoded).Should().BeTrue(constructor);
            decoded.Encoding.Should().Be(expected);
            decoded.Dimensions.Should().Be(4);
            decoded.IsFinite.Should().BeTrue();
        }

        // Sparse vectors are a real Turso encoding, but no dense bound applies to them, so the
        // bridge reports them as undecodable rather than pretending otherwise.
        var sparse = Query(connection, "SELECT vector32_sparse('[1,0,0,4]');")[0][0];
        SqliteVectorFunctions.TryDecodeVector(sparse, out _).Should().BeFalse();
        SqliteVectorFunctions.TryReadVectorEncoding(sparse).Should().Be(VectorEncodingKind.Float32Sparse);
    }

    [Test]
    public void UndecodableValuesAreReportedRatherThanThrown()
    {
        SqliteVectorFunctions.TryDecodeVector(SqlValue.Null, out _).Should().BeFalse();
        SqliteVectorFunctions.TryDecodeVector(SqlValue.Integer(3), out _).Should().BeFalse();
        SqliteVectorFunctions.TryDecodeVector(SqlValue.Text("not a vector"), out _).Should().BeFalse();
        SqliteVectorFunctions.TryDecodeVector(SqlValue.Blob(new byte[] { 0xFF }), out _).Should().BeFalse();

        // A text vector is parsed as float32 dense, exactly as the scalar functions parse it.
        SqliteVectorFunctions.TryDecodeVector(SqlValue.Text("[1,2,3]"), out var decoded).Should().BeTrue();
        decoded.Encoding.Should().Be(VectorEncodingKind.Float32);
        decoded.Dimensions.Should().Be(3);
    }

    [Test]
    public void QueryArgumentValidationReproducesTheScalarErrorsInOrder()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        var parseFailure = () => SqliteVectorFunctions.ValidateVectorQueryArgument(
            SqlValue.Text("nonsense"),
            VectorEncodingKind.Float32,
            4);
        parseFailure.Should().Throw<EmbeddedSqlException>().WithMessage("Invalid vector value");

        var nullFailure = () => SqliteVectorFunctions.ValidateVectorQueryArgument(
            SqlValue.Null,
            VectorEncodingKind.Float32,
            4);
        nullFailure.Should().Throw<EmbeddedSqlException>().WithMessage("Invalid vector type");

        var wrongDimensions = Query(connection, "SELECT vector32('[1,2,3]');")[0][0];
        var dimensionFailure = () => SqliteVectorFunctions.ValidateVectorQueryArgument(
            wrongDimensions,
            VectorEncodingKind.Float32,
            4);
        dimensionFailure.Should().Throw<EmbeddedSqlException>().WithMessage("Vectors must have the same dimensions");

        // Dimensions are checked before types, matching the scalar evaluator's own order.
        var wrongType = Query(connection, "SELECT vector64('[1,2,3,4]');")[0][0];
        var typeFailure = () => SqliteVectorFunctions.ValidateVectorQueryArgument(
            wrongType,
            VectorEncodingKind.Float32,
            4);
        typeFailure.Should().Throw<EmbeddedSqlException>().WithMessage("Vectors must be of the same type");

        var accepted = () => SqliteVectorFunctions.ValidateVectorQueryArgument(
            Query(connection, "SELECT vector32('[1,2,3,4]');")[0][0],
            VectorEncodingKind.Float32,
            4);
        accepted.Should().NotThrow();
    }

    private static VectorDistanceKind Kind(VectorTestMetric metric)
        => metric switch
        {
            VectorTestMetric.L2 => VectorDistanceKind.L2,
            VectorTestMetric.Cosine => VectorDistanceKind.Cosine,
            VectorTestMetric.Dot => VectorDistanceKind.Dot,
            _ => throw new ArgumentOutOfRangeException(nameof(metric)),
        };
}
