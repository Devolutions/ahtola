using System.Data;
using AwesomeAssertions;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public sealed class ManagedReaderStreamingSliceTests
{
    [Test]
    public void AhtolaManagedReaderStreamsTypedMaterializedValues()
    {
        using var connection = new AhtolaConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT X'000102' AS blob_value, 'text' AS text_value;";
        using var reader = command.ExecuteReader(CommandBehavior.SequentialAccess);
        Action streamBeforeRead = () => reader.GetStream(0);
        streamBeforeRead.Should().Throw<InvalidOperationException>();
        reader.Read().Should().BeTrue();

        reader.GetBytes(0, 0, null, 0, 0).Should().Be(3);
        var chars = new char[2];
        reader.GetChars(1, 1, chars, 0, 2).Should().Be(2);
        new string(chars).Should().Be("ex");

        using var stream = reader.GetStream(0);
        using var textReader = reader.GetTextReader(1);
        reader.Close();

        stream.ReadByte().Should().Be(0);
        textReader.ReadToEnd().Should().Be("text");
    }

    [Test]
    public void ManagedReaderStreamsTypedMaterializedValues()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT X'0001020304' AS blob_value, 'hello' AS text_value;";
        using var reader = command.ExecuteReader(CommandBehavior.SequentialAccess);
        reader.Read().Should().BeTrue();

        reader.GetBytes(0, 0, null, 0, 0).Should().Be(5);
        var bytes = new byte[] { 9, 9, 9, 9, 9 };
        reader.GetBytes(0, 1, bytes, 1, 3).Should().Be(3);
        bytes.Should().Equal(9, 1, 2, 3, 9);

        reader.GetChars(1, 0, null, 0, 0).Should().Be(5);
        var chars = new[] { '?', '?', '?', '?', '?' };
        reader.GetChars(1, 1, chars, 1, 3).Should().Be(3);
        new string(chars).Should().Be("?ell?");

        using var stream = reader.GetStream(0);
        stream.CanWrite.Should().BeFalse();
        stream.ReadByte().Should().Be(0);

        using var textReader = reader.GetTextReader(1);
        reader.Close();

        stream.ReadByte().Should().Be(1);
        textReader.ReadToEnd().Should().Be("hello");
    }

    [Test]
    public void ManagedReaderStreamingSliceValidatesTypesNullsAndCopyArguments()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT X'000102' AS blob_value, 'text' AS text_value, 42 AS integer_value, NULL AS null_value;";
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();

        reader.GetBytes(0, 3, Array.Empty<byte>(), 0, 0).Should().Be(0);
        reader.GetChars(1, 4, Array.Empty<char>(), 0, 0).Should().Be(0);

        Action negativeByteOffset = () => reader.GetBytes(0, -1, Array.Empty<byte>(), 0, 0);
        negativeByteOffset.Should().Throw<ArgumentOutOfRangeException>();
        Action negativeCharOffset = () => reader.GetChars(1, -1, Array.Empty<char>(), 0, 0);
        negativeCharOffset.Should().Throw<ArgumentOutOfRangeException>();
        Action invalidByteBuffer = () => reader.GetBytes(0, 0, new byte[1], 1, 1);
        invalidByteBuffer.Should().Throw<ArgumentException>();
        Action invalidCharBuffer = () => reader.GetChars(1, 0, new char[1], 1, 1);
        invalidCharBuffer.Should().Throw<ArgumentException>();

        Action bytesFromText = () => reader.GetBytes(1, 0, null, 0, 0);
        bytesFromText.Should().Throw<InvalidCastException>();
        Action charsFromBlob = () => reader.GetChars(0, 0, null, 0, 0);
        charsFromBlob.Should().Throw<InvalidCastException>();
        Action streamFromInteger = () => reader.GetStream(2);
        streamFromInteger.Should().Throw<InvalidCastException>();
        Action textReaderFromBlob = () => reader.GetTextReader(0);
        textReaderFromBlob.Should().Throw<InvalidCastException>();
        Action streamFromNull = () => reader.GetStream(3);
        streamFromNull.Should().Throw<InvalidOperationException>();
        Action textReaderFromNull = () => reader.GetTextReader(3);
        textReaderFromNull.Should().Throw<InvalidOperationException>();
        Action streamFromInvalidOrdinal = () => reader.GetStream(4);
        streamFromInvalidOrdinal.Should().Throw<ArgumentOutOfRangeException>();
    }
}
