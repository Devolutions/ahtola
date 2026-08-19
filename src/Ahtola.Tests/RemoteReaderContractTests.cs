using System.Data;
using System.Data.Common;
using System.Text.Json;

using AwesomeAssertions;

namespace Ahtola.Tests;

// Pins the remote reader to the contract the managed reader already implements.
public sealed class RemoteReaderContractTests
{
    [Test]
    public void RemoteReaderFieldTypeUsesDeclaredTypeForNullCell()
    {
        using var reader = CreateReader(
            Column("id", "TEXT"),
            Row(Null()));

        reader.Read().Should().BeTrue();

        // DataTable rejects DBNull as a column type.
        reader.GetFieldType(0).Should().Be(typeof(string));
    }

    [Test]
    public void RemoteReaderFieldTypeIsStableAcrossMixedStorageInOneColumn()
    {
        using var reader = CreateReader(
            Column("payload", "TEXT"),
            Row(Blob([1, 2, 3])),
            Row(Text("plain")));

        reader.Read().Should().BeTrue();
        Type firstRowType = reader.GetFieldType(0);

        reader.Read().Should().BeTrue();
        Type secondRowType = reader.GetFieldType(0);

        // One DataTable column serves the whole result, so a per-row answer makes the second row unloadable.
        secondRowType.Should().Be(firstRowType);
        firstRowType.Should().Be(typeof(string));
    }

    [Test]
    public void RemoteReaderFieldTypeFallsBackToObjectWhenNothingDeclaresATypeAndAllRowsAreNull()
    {
        using var reader = CreateReader(
            Column("computed", declType: null),
            Row(Null()));

        reader.Read().Should().BeTrue();

        reader.GetFieldType(0).Should().Be(typeof(object));
        reader.GetFieldType(0).Should().NotBe(typeof(DBNull));
    }

    [Test]
    public void RemoteReaderReadsBlobGuid()
    {
        var id = new Guid("09b4a80a-cb65-4f23-a388-f2c7af681fec");

        using var reader = CreateReader(
            Column("id", "GUID"),
            Row(Blob(id.ToByteArray())));

        reader.Read().Should().BeTrue();
        reader.GetGuid(0).Should().Be(id);
    }

    [Test]
    public void RemoteReaderReadsTextGuid()
    {
        var id = new Guid("09b4a80a-cb65-4f23-a388-f2c7af681fec");

        using var reader = CreateReader(
            Column("id", "GUID"),
            Row(Text(id.ToString())));

        reader.Read().Should().BeTrue();
        reader.GetGuid(0).Should().Be(id);
    }

    [Test]
    public void RemoteReaderReportsStorageWithoutLeakingTheValueForAnUnparseableGuid()
    {
        using var reader = CreateReader(
            Column("id", "GUID"),
            Row(Text("not-a-guid")));

        reader.Read().Should().BeTrue();

        Action read = () => reader.GetGuid(0);
        read.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("id").And.NotContain("not-a-guid");
    }

    private static AhtolaRemoteDataReader CreateReader(RemoteColumn column, params List<RemoteResponseValue>[] rows)
    {
        var result = new RemoteStatementResult
        {
            Columns = [column],
            Rows = [.. rows],
        };

        return new AhtolaRemoteDataReader(connection: null, [result], CommandBehavior.Default);
    }

    private static RemoteColumn Column(string name, string? declType) => new() { Name = name, DeclType = declType };

    private static List<RemoteResponseValue> Row(RemoteResponseValue value) => [value];

    private static RemoteResponseValue Null() => new() { Type = "null" };

    private static RemoteResponseValue Text(string value) => new()
    {
        Type = "text",
        Value = JsonSerializer.SerializeToElement(value),
    };

    private static RemoteResponseValue Blob(byte[] value) => new()
    {
        Type = "blob",
        Base64 = Convert.ToBase64String(value),
    };
}
