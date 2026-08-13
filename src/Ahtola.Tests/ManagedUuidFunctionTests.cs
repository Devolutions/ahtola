using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

public class ManagedUuidFunctionTests
{
    [Test]
    public void UuidTextAndBlobConversionsUseCanonicalRfc4122Bytes()
    {
        const string uuid = "01945ca0-3189-76c0-9a8f-caf310fc8b8e";

        Scalar($"uuid_str(x'01945CA0318976C09A8FCAF310FC8B8E')").Should().Be(SqlValue.Text(uuid));
        Scalar($"hex(uuid_blob('{uuid}'))").Should().Be(SqlValue.Text("01945CA0318976C09A8FCAF310FC8B8E"));
        Scalar($"uuid_str(uuid_blob('{uuid}'))").Should().Be(SqlValue.Text(uuid));
    }

    [Test]
    public void UuidGenerationProducesVersionedRfc4122Values()
    {
        Scalar("uuid4_str()").AsText().Should().MatchRegex(
            "^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$");
        Scalar("gen_random_uuid()").AsText().Should().MatchRegex(
            "^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$");

        var uuid4 = Scalar("uuid4()");
        uuid4.Kind.Should().Be(SqlValueKind.Blob);
        uuid4.AsBlob().Length.Should().Be(16);
        (uuid4.AsBlob().Span[6] >> 4).Should().Be(4);
        (uuid4.AsBlob().Span[8] & 0xc0).Should().Be(0x80);

        Scalar("uuid7_str(1736720789)").AsText().Should().MatchRegex(
            "^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$");
        var uuid7 = Scalar("uuid7(1736720789)");
        uuid7.Kind.Should().Be(SqlValueKind.Blob);
        (uuid7.AsBlob().Span[6] >> 4).Should().Be(7);
        (uuid7.AsBlob().Span[8] & 0xc0).Should().Be(0x80);
    }

    [Test]
    public void Uuid7TimestampExtractionUsesTheLeadingSixRfc4122Bytes()
    {
        Scalar("uuid7_timestamp_ms('01945ca0-3189-76c0-9a8f-caf310fc8b8e')")
            .Should().Be(SqlValue.Integer(1_736_720_789_897));
        Scalar("uuid7_timestamp_ms(x'01945CA0318976C09A8FCAF310FC8B8E')")
            .Should().Be(SqlValue.Integer(1_736_720_789_897));
        Scalar("uuid7_timestamp_ms(uuid7(1736720789))")
            .Should().Be(SqlValue.Integer(1_736_720_789_000));
        Scalar("uuid7_timestamp_ms(uuid7_str('1736720789'))")
            .Should().Be(SqlValue.Integer(1_736_720_789_000));
    }

    [Test]
    public void UuidConversionsAndTimestampExtractionReturnNullForInvalidInputs()
    {
        Scalar("uuid_str(NULL)").Should().Be(SqlValue.Null);
        Scalar("uuid_str(x'0011')").Should().Be(SqlValue.Null);
        Scalar("uuid_str('01945ca0-3189-76c0-9a8f-caf310fc8b8e')").Should().Be(SqlValue.Null);
        Scalar("uuid_blob(NULL)").Should().Be(SqlValue.Null);
        Scalar("uuid_blob('not-a-uuid')").Should().Be(SqlValue.Null);
        Scalar("uuid_blob(123)").Should().Be(SqlValue.Null);
        Scalar("uuid7('1736720789')").Should().Be(SqlValue.Null);
        Scalar("uuid7_timestamp_ms(NULL)").Should().Be(SqlValue.Null);
        Scalar("uuid7_timestamp_ms(x'0011')").Should().Be(SqlValue.Null);
        Scalar("uuid7_timestamp_ms('not-a-uuid')").Should().Be(SqlValue.Null);
    }

    [Test]
    public void UuidArityErrorsAndPercentileSupportAreExplicit()
    {
        AssertError("uuid_str()", "wrong number of arguments to function uuid_str()");
        AssertError("uuid_blob()", "wrong number of arguments to function uuid_blob()");
        AssertError("uuid7_timestamp_ms()", "wrong number of arguments to function uuid7_timestamp_ms()");
        AssertError("uuid7_str('0')", "Invalid timestamp");
        AssertError("uuid7_str(x'00')", "invalid arguments to function uuid7_str()");
        Scalar("percentile(1, 50)").Should().Be(SqlValue.Real(1));
    }

    private static SqlValue Scalar(string expression)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare("SELECT " + expression + ";");
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0);
    }

    private static void AssertError(string expression, string message)
    {
        var exception = Assert.Throws<EmbeddedSqlException>(() => Scalar(expression));
        exception!.Message.Should().Be(message);
    }
}
