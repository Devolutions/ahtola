using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

/// <summary>
/// Round-trip coverage for <see cref="SqliteRecordCodec"/>, which now sizes a
/// record up front and writes it once into an exactly-sized buffer instead of
/// accumulating it through growing lists.
/// </summary>
public sealed class SqliteRecordCodecRoundTripTests
{
    [TestCase(SqliteTextEncoding.Utf8)]
    [TestCase(SqliteTextEncoding.Utf16LittleEndian)]
    [TestCase(SqliteTextEncoding.Utf16BigEndian)]
    public void EveryStorageClassRoundTripsThroughTheEncodedRecord(SqliteTextEncoding textEncoding)
    {
        SqlValue[] values =
        [
            SqlValue.Null,
            SqlValue.Integer(0),
            SqlValue.Integer(1),
            SqlValue.Integer(-1),
            SqlValue.Integer(sbyte.MinValue),
            SqlValue.Integer(sbyte.MaxValue),
            SqlValue.Integer(short.MinValue),
            SqlValue.Integer(short.MaxValue),
            SqlValue.Integer(-8_388_608),
            SqlValue.Integer(8_388_607),
            SqlValue.Integer(int.MinValue),
            SqlValue.Integer(int.MaxValue),
            SqlValue.Integer(-140_737_488_355_328),
            SqlValue.Integer(140_737_488_355_327),
            SqlValue.Integer(long.MinValue),
            SqlValue.Integer(long.MaxValue),
            SqlValue.Real(0d),
            SqlValue.Real(-1.5d),
            SqlValue.Real(double.MaxValue),
            SqlValue.Text(string.Empty),
            SqlValue.Text("ascii"),
            SqlValue.Text("héllo wörld — ünïcode"),
            SqlValue.Text("emoji \U0001F600 surrogate pair"),
            SqlValue.Blob([]),
            SqlValue.Blob([0x00, 0x7F, 0x80, 0xFF]),
        ];

        var record = SqliteRecordCodec.Encode(values, textEncoding);
        var decoded = SqliteRecordCodec.Decode(record, textEncoding);

        decoded.Should().HaveCount(values.Length);
        for (var index = 0; index < values.Length; index++)
        {
            decoded[index].Kind.Should().Be(values[index].Kind, $"value {index} keeps its storage class");
            switch (values[index].Kind)
            {
                case SqlValueKind.Integer:
                    decoded[index].AsInteger().Should().Be(values[index].AsInteger());
                    break;
                case SqlValueKind.Real:
                    decoded[index].AsReal().Should().Be(values[index].AsReal());
                    break;
                case SqlValueKind.Text:
                    decoded[index].AsText().Should().Be(values[index].AsText());
                    break;
                case SqlValueKind.Blob:
                    decoded[index].AsBlob().ToArray().Should().Equal(values[index].AsBlob().ToArray());
                    break;
            }
        }
    }

    [Test]
    public void EncodedRecordHasNoTrailingOrMissingBytes()
    {
        // A record whose header size varint itself grows: 130 columns needs a
        // two-byte header length, which is where an off-by-one would surface.
        var values = Enumerable.Range(0, 130).Select(index => SqlValue.Integer(index)).ToArray();
        var record = SqliteRecordCodec.Encode(values);

        SqliteRecordCodec.Decode(record).Should().HaveCount(values.Length);
        Assert.Throws<InvalidDataException>(() => SqliteRecordCodec.Decode(record.Concat<byte>([0x00]).ToArray()));
    }

    [Test]
    public void LargeTextAndBlobPayloadsRoundTripExactly()
    {
        var text = string.Concat(Enumerable.Repeat("payload-", 4096));
        var blob = new byte[32768];
        for (var index = 0; index < blob.Length; index++)
            blob[index] = unchecked((byte)(index * 31));

        var record = SqliteRecordCodec.Encode([SqlValue.Text(text), SqlValue.Blob(blob)]);
        var decoded = SqliteRecordCodec.Decode(record);

        decoded[0].AsText().Should().Be(text);
        decoded[1].AsBlob().ToArray().Should().Equal(blob);
    }

    [TestCase("BINARY")]
    [TestCase("NOCASE")]
    [TestCase("RTRIM")]
    public void TextComparisonMatchesTheEncodedByteOrder(string collation)
    {
        var comparer = new SqliteIndexRecordComparer(
            SqliteTextEncoding.Utf8,
            [new SqliteIndexComparisonTerm(SqliteKeySortOrder.Ascending, SqliteKeyCollation.FromName(collation))]);

        // Mixed ASCII and non-ASCII: the ASCII fast path and the encoded-byte
        // path must agree with each other and stay a total order.
        string[] samples =
        [
            "", "a", "A", "aa", "ab", "b", "z", "Z", "a ", "a  ",
            "héllo", "hello", "zz", "\U0001F600", "\uFFFDx", "ünïcode",
        ];

        foreach (var left in samples)
        {
            foreach (var right in samples)
            {
                var forward = Math.Sign(Compare(comparer, left, right));
                var reverse = Math.Sign(Compare(comparer, right, left));
                forward.Should().Be(-reverse, $"'{left}' vs '{right}' must be antisymmetric under {collation}");
            }
        }
    }

    private static int Compare(SqliteIndexRecordComparer comparer, string left, string right)
        => comparer.Compare(
            SqliteRecordCodec.Encode([SqlValue.Text(left)]),
            SqliteRecordCodec.Encode([SqlValue.Text(right)]));
}
