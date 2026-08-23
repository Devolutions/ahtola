using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

/// <summary>
/// Differential expectations taken from Turso's vector implementation at commit 277ddd050.
/// </summary>
public sealed class ManagedVectorFunctionTests
{
    [TestCase("SELECT hex(vector('[1,2]'));", "0000803F00000040")]
    [TestCase("SELECT hex(vector32('[1,2]'));", "0000803F00000040")]
    [TestCase("SELECT hex(vector64('[1,2]'));", "000000000000F03F000000000000004002")]
    [TestCase("SELECT hex(vector32_sparse('[1,0,2]'));", "0000803F0000004000000000020000000300000009")]
    [TestCase("SELECT hex(vector1bit('[1,-1,0,2]'));", "090C03")]
    public void ConstructorsUseTursoSerializedLayouts(string sql, string expected)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        ReadValue(connection, sql).Should().Be(SqlValue.Text(expected));
    }

    [TestCase("SELECT vector_extract(vector32('[1,2.5,-3]'));", "[1,2.5,-3]")]
    [TestCase("SELECT vector_extract(vector64('[1,2.5,-3]'));", "[1,2.5,-3]")]
    [TestCase("SELECT vector_extract(vector32_sparse('[1,0,-3]'));", "[1,0,-3]")]
    [TestCase("SELECT vector_extract(vector1bit('[1,-1,0,2]'));", "[1,-1,-1,1]")]
    [TestCase("SELECT vector_extract(vector8('[4,4]'));", "[4,4]")]
    [TestCase("SELECT vector_extract(vector32(vector64('[1.5,-2.25]')));", "[1.5,-2.25]")]
    [TestCase("SELECT vector_extract(vector('[-1000000000000000000]'));", "[-1000000000000000000]")]
    [TestCase("SELECT vector_extract(vector1bit(vector64('[1e-300]')));", "[1]")]
    public void ExtractAndConversionMatchTurso(string sql, string expected)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        ReadValue(connection, sql).Should().Be(SqlValue.Text(expected));
    }

    [Test]
    public void ConcatAndSlicePreserveVectorEncoding()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        ReadValue(
                connection,
                "SELECT vector_extract(vector_concat(vector32('[1,2]'), vector32('[3,4]')));")
            .Should().Be(SqlValue.Text("[1,2,3,4]"));
        ReadValue(
                connection,
                "SELECT vector_extract(vector_slice(vector64('[1,2,3,4]'), 1, 3));")
            .Should().Be(SqlValue.Text("[2,3]"));
        ReadValue(
                connection,
                "SELECT vector_extract(vector_slice(vector32_sparse('[0,2,0,4]'), 1, 4));")
            .Should().Be(SqlValue.Text("[2,0,4]"));
        ReadValue(
                connection,
                "SELECT vector_extract(vector_slice(vector32('[1,2]'), 2, 2));")
            .Should().Be(SqlValue.Text("[]"));
    }

    [Test]
    public void DistanceFunctionsMatchTursoScalarSemantics()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        ReadReal(connection, "SELECT vector_distance_dot('[1,2]', '[3,4]');")
            .Should().BeApproximately(-11.0, 1e-12);
        ReadReal(connection, "SELECT vector_distance_l2('[1,2]', '[3,4]');")
            .Should().BeApproximately(Math.Sqrt(8.0), 1e-6);
        ReadReal(connection, "SELECT vector_distance_cos('[1,2]', '[3,4]');")
            .Should().BeApproximately(1.0 - 11.0 / Math.Sqrt(125.0), 1e-6);
        ReadReal(connection, "SELECT vector_distance_jaccard('[1,2]', '[3,4]');")
            .Should().BeApproximately(4.0 / 7.0, 1e-6);
        ReadReal(connection, "SELECT vector_distance_cos('[1,2]', '[0,0]');")
            .Should().Be(1.0);
        ReadReal(connection, "SELECT vector_distance_cos(vector64('[1,2]'), vector64('[0,0]'));")
            .Should().Be(1.0);

        ReadReal(
                connection,
                "SELECT vector_distance_l2(vector32_sparse('[1,0,2]'), vector32_sparse('[0,0,2]'));")
            .Should().BeApproximately(1.0, 1e-6);
        ReadReal(
                connection,
                "SELECT vector_distance_cos(vector1bit('[1,-1,1]'), vector1bit('[-1,-1,1]'));")
            .Should().Be(1.0);
        ReadReal(
                connection,
                "SELECT vector_distance_dot(vector1bit('[1,-1,1]'), vector1bit('[-1,-1,1]'));")
            .Should().Be(-1.0);
        ReadReal(
                connection,
                "SELECT vector_distance_l2(vector8('[1,2,3]'), vector8('[1,2,3]'));")
            .Should().BeApproximately(0.0, 1e-12);
    }

    [TestCase("SELECT vector32(NULL);", "Invalid vector type")]
    [TestCase("SELECT vector_extract('[1]');", "Expected blob value")]
    [TestCase("SELECT vector_distance_l2('[1]', '[1,2]');", "Vectors must have the same dimensions")]
    [TestCase("SELECT vector_distance_l2(vector32('[1]'), vector64('[1]'));", "Vectors must be of the same type")]
    [TestCase("SELECT vector_concat(vector32('[1]'), vector64('[1]'));", "Mismatched vector types")]
    [TestCase("SELECT vector_slice('[1,2]', 2, 1);", "start index must not be greater than end index")]
    [TestCase("SELECT vector_slice('[1,2]', 0, 3);", "vector_slice range out of bounds")]
    [TestCase("SELECT vector_slice('[1,2]', 0.0, 1);", "start index must be an integer")]
    [TestCase("SELECT vector_slice(vector1bit('[1,-1]'), 0, 1);", "vector_slice is not supported")]
    [TestCase("SELECT vector_concat(vector8('[1]'), vector8('[2]'));", "vector_concat is not supported")]
    [TestCase("SELECT vector_distance_l2(vector1bit('[1]'), vector1bit('[1]'));", "L2 distance is not supported")]
    public void InvalidTypesDimensionsAndRangesMatchTursoErrors(string sql, string expected)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        var action = () => ReadValue(connection, sql);
        action.Should().Throw<EmbeddedSqlException>().WithMessage($"*{expected}*");
    }

    [TestCase("SELECT vector_extract(X'00');", "unknown vector type: 0")]
    [TestCase("SELECT vector_extract(X'0000');", "f32 dense vector unexpected data length: 2")]
    [TestCase("SELECT vector_extract(X'0000803F020000000200000009');", "index 2 out of range")]
    [TestCase("SELECT vector_extract(X'00FF03');", "trailing bits 255 exceed blob capacity")]
    [TestCase("SELECT vector_extract(X'000004');", "too short")]
    public void MalformedVectorBlobsFailClosed(string sql, string expected)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        var action = () => ReadValue(connection, sql);
        action.Should().Throw<EmbeddedSqlException>().WithMessage($"*{expected}*");
    }

    [TestCase("SELECT vector32('[NaN]');")]
    [TestCase("SELECT vector32('[Infinity]');")]
    [TestCase("SELECT vector32('[1e999]');")]
    [TestCase("SELECT vector64('[-Infinity]');")]
    public void TextVectorsRejectNonFiniteComponents(string sql)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        var action = () => ReadValue(connection, sql);
        action.Should().Throw<EmbeddedSqlException>().WithMessage("*Invalid vector value*");
    }

    [Test]
    public void BlobNonFiniteValuesExtractButNanDistancesBecomeSqlNull()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        ReadValue(connection, "SELECT vector_extract(X'0000C07F0000807F');")
            .Should().Be(SqlValue.Text("[NaN,inf]"));
        ReadValue(connection, "SELECT vector_distance_dot(X'0000C07F', X'0000C07F');")
            .Should().Be(SqlValue.Null);
        ReadReal(
                connection,
                "SELECT vector_distance_jaccard(X'0000C07F0000803F', X'0000004000000040');")
            .Should().BeApproximately(0.25, 1e-6);
        ReadValue(connection, "SELECT vector_extract(vector8(X'0000C07F0000803F'));")
            .Should().Be(SqlValue.Text("[1,1]"));
    }

    [Test]
    public void Float64ToSparseTestsNonZeroBeforeNarrowingLikeTurso()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        ReadValue(connection, "SELECT hex(vector32_sparse(vector64('[1e-300]')));")
            .Should().Be(SqlValue.Text("00000000000000000100000009"));
    }

    [Test]
    public void Float8ConversionAndNonFiniteMetadataMatchTursoArithmetic()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        ReadValue(connection, "SELECT hex(vector8(vector32('[0,-0]')));")
            .Should().Be(SqlValue.Text("000000000000000000000000000204"));
        ReadValue(
                connection,
                "SELECT vector_distance_dot(X'00000000000000000000807F000304', X'00000000000000000000807F000304');")
            .Should().Be(SqlValue.Null);
    }

    [Test]
    public void EmptyVectorsFollowEachUpstreamEncoding()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        ReadValue(connection, "SELECT vector_extract(vector32('[]'));").Should().Be(SqlValue.Text("[]"));
        ReadValue(connection, "SELECT vector_extract(vector64('[]'));").Should().Be(SqlValue.Text("[]"));
        ReadValue(connection, "SELECT vector_extract(vector32_sparse('[]'));").Should().Be(SqlValue.Text("[]"));
        ReadValue(connection, "SELECT vector_distance_l2('[]', '[]');").Should().Be(SqlValue.Real(0.0));
        ReadValue(connection, "SELECT vector_distance_cos('[]', '[]');").Should().Be(SqlValue.Real(0.0));
        ReadValue(connection, "SELECT vector_distance_jaccard('[]', '[]');").Should().Be(SqlValue.Null);
        ReadValue(connection, "SELECT length(vector8('[]'));").Should().Be(SqlValue.Integer(11));

        var oneBit = () => ReadValue(connection, "SELECT vector1bit('[]');");
        oneBit.Should().Throw<EmbeddedSqlException>().WithMessage("*empty vector not supported*");

        var float8RoundTrip = () => ReadValue(connection, "SELECT vector_extract(vector8('[]'));");
        float8RoundTrip.Should().Throw<EmbeddedSqlException>().WithMessage("*for 0 dims*");
    }

    [Test]
    public void PersistedVectorViewResolvesAfterReopen()
    {
        const string path = "persisted-vector-view.db";
        var fileSystem = new InMemoryFileSystem();

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE embeddings(value TEXT);");
            Execute(
                connection,
                "CREATE VIEW decoded_embeddings AS SELECT vector_extract(vector32(value)) AS value FROM embeddings;");
            Execute(connection, "INSERT INTO embeddings VALUES ('[1,2]');");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadValue(reopenedConnection, "SELECT value FROM decoded_embeddings;")
            .Should().Be(SqlValue.Text("[1,2]"));
    }

    private static double ReadReal(EmbeddedConnection connection, string sql)
    {
        var value = ReadValue(connection, sql);
        value.Kind.Should().Be(SqlValueKind.Real);
        return value.AsReal();
    }

    private static SqlValue ReadValue(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0);
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }
}
