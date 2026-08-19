using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;

namespace Ahtola.Tests;

public sealed class AhtolaParameterBindingRegressionTests
{
    public static IEnumerable<TestCaseData> EnumValues()
    {
        yield return new TestCaseData(SByteEnum.Minimum, (long)sbyte.MinValue);
        yield return new TestCaseData(ByteEnum.Maximum, (long)byte.MaxValue);
        yield return new TestCaseData(Int16Enum.Minimum, (long)short.MinValue);
        yield return new TestCaseData(UInt16Enum.Maximum, (long)ushort.MaxValue);
        yield return new TestCaseData(Int32Enum.Minimum, (long)int.MinValue);
        yield return new TestCaseData(UInt32Enum.Maximum, (long)uint.MaxValue);
        yield return new TestCaseData(Int64Enum.Minimum, long.MinValue);
        yield return new TestCaseData(UInt64Enum.Maximum, -1L);
    }

    [TestCaseSource(nameof(EnumValues))]
    public void EnumParametersUseTheirUnderlyingSqliteIntegerBitPattern(object value, long expected)
    {
        var parameter = new AhtolaParameter(value);

        parameter.ToValue().ValueType.Should().Be(AhtolaValueType.Integer);
        parameter.ToValue().IntValue.Should().Be(expected);
        parameter.ToSqlValue().AsInteger().Should().Be(expected);
    }

    [Test]
    public void ManagedBindingUsesReferencedLexerSlotsAndIgnoresSurplusParameters()
    {
        using var connection = new AhtolaConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT @named, @named, ?2, ?, ':ignored'
            -- $ignored
            /* ?9 */
            """;
        command.Parameters.Add(new AhtolaParameter("$named", 11L));
        command.Parameters.Add(new AhtolaParameter("?2", 22L));
        command.Parameters.Add(new AhtolaParameter(33L));
        command.Parameters.Add(new AhtolaParameter("$unused", new object()));

        using var reader = command.ExecuteReader();

        reader.Read().Should().BeTrue();
        reader.GetInt64(0).Should().Be(11L);
        reader.GetInt64(1).Should().Be(11L);
        reader.GetInt64(2).Should().Be(22L);
        reader.GetInt64(3).Should().Be(33L);
        reader.GetString(4).Should().Be(":ignored");
    }

    [Test]
    public void ManagedBindingReportsEachReferencedMissingSlotWithoutRequiringNumberedHoles()
    {
        using var connection = new AhtolaConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        using (var numbered = connection.CreateCommand())
        {
            numbered.CommandText = "SELECT ?5;";
            numbered.Parameters.Add(new AhtolaParameter("?5", 5L));
            numbered.ExecuteScalar().Should().Be(5L);
        }

        using var missing = connection.CreateCommand();
        missing.CommandText = "SELECT @named, ?2, ?;";
        missing.Parameters.Add(new AhtolaParameter("@named", 1L));

        Assert.Throws<InvalidOperationException>(() => missing.ExecuteScalar())!
            .Message.Should().Be("Missing value for parameter ?2.");
    }

    [Test]
    public async Task RemoteStatementFiltersParametersBeforeHranaSerialization()
    {
        using var handler = new CapturingHandler(ExecuteSuccess());
        using var client = new AhtolaRemoteClient(
            new HttpClient(handler),
            new Uri("https://example.test"),
            authToken: null);
        var parameters = CreateMixedParameters();

        await client.ExecuteAsync(
            "SELECT @named, @named, ?2, ?, ':ignored' /* ?9 */",
            parameters,
            wantRows: true,
            commandTimeout: 30,
            closeAfter: false,
            CancellationToken.None);

        var statement = handler.Requests.Should().ContainSingle().Which
            .GetProperty("requests")[0]
            .GetProperty("stmt");
        statement.GetProperty("named_args").EnumerateArray()
            .Select(static argument => argument.GetProperty("name").GetString())
            .Should().Equal("@named", "?2");
        statement.GetProperty("args").EnumerateArray()
            .Select(static argument => argument.GetProperty("value").GetString())
            .Should().Equal("33");
    }

    [Test]
    public async Task RemoteBatchFiltersParametersAndRejectsMissingSlotsBeforeSending()
    {
        using var handler = new CapturingHandler(BatchSuccess());
        using var client = new AhtolaRemoteClient(
            new HttpClient(handler),
            new Uri("https://example.test"),
            authToken: null);
        var command = new AhtolaBatchCommand("SELECT :named, ?2, ?")
        {
            Parameters =
            {
                new AhtolaParameter("@named", 11L),
                new AhtolaParameter("?2", 22L),
                new AhtolaParameter(33L),
                new AhtolaParameter("$unused", new object()),
            },
        };

        await client.ExecuteBatchAsync(
            [command],
            commandTimeout: 30,
            wantRows: true,
            closeAfter: false,
            CancellationToken.None);

        var statement = handler.Requests.Should().ContainSingle().Which
            .GetProperty("requests")[0]
            .GetProperty("batch")
            .GetProperty("steps")[0]
            .GetProperty("stmt");
        statement.GetProperty("named_args").EnumerateArray()
            .Select(static argument => argument.GetProperty("name").GetString())
            .Should().Equal(":named", "?2");
        statement.GetProperty("args").GetArrayLength().Should().Be(1);

        var missing = new AhtolaBatchCommand("SELECT :missing");
        var exception = Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ExecuteBatchAsync(
                [missing],
                commandTimeout: 30,
                wantRows: true,
                closeAfter: false,
                CancellationToken.None));
        exception!.Message.Should().Be("Missing value for parameter :missing.");
        handler.Requests.Should().ContainSingle();
    }

    private static AhtolaParameterCollection CreateMixedParameters()
    {
        var parameters = new AhtolaParameterCollection
        {
            new AhtolaParameter("$named", 11L),
            new AhtolaParameter("?2", 22L),
            new AhtolaParameter(33L),
            new AhtolaParameter("$unused", new object()),
        };
        return parameters;
    }

    private static string ExecuteSuccess()
        => """{"baton":"next","results":[{"type":"ok","response":{"type":"execute","result":{"cols":[],"rows":[],"affected_row_count":0}}}]}""";

    private static string BatchSuccess()
        => """{"baton":"next","results":[{"type":"ok","response":{"type":"batch","result":{"step_results":[{"cols":[],"rows":[],"affected_row_count":0}],"step_errors":[null]}}}]}""";

    private sealed class CapturingHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);

        public List<JsonElement> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(cancellationToken));
            Requests.Add(document.RootElement.Clone());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json"),
            };
        }
    }

    private enum SByteEnum : sbyte { Minimum = sbyte.MinValue }
    private enum ByteEnum : byte { Maximum = byte.MaxValue }
    private enum Int16Enum : short { Minimum = short.MinValue }
    private enum UInt16Enum : ushort { Maximum = ushort.MaxValue }
    private enum Int32Enum : int { Minimum = int.MinValue }
    private enum UInt32Enum : uint { Maximum = uint.MaxValue }
    private enum Int64Enum : long { Minimum = long.MinValue }
    private enum UInt64Enum : ulong { Maximum = ulong.MaxValue }
}
