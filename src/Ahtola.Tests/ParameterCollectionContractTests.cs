using System.Data.Common;
using Ahtola.Data.Sqlite;
using AwesomeAssertions;

namespace Ahtola.Tests;

public sealed class ParameterCollectionContractTests
{
    [Test]
    public void AhtolaCollectionSupportsLookupCopyInsertionAndRemoval()
    {
        var collection = new AhtolaParameterCollection();
        var first = new AhtolaParameter("@first", 1L);
        var third = new AhtolaParameter("@third", 3L);

        collection.Add(first).Should().Be(0);
        collection.Add(2L).Should().Be(1);
        collection.AddRange(new object[] { third, 4L });
        var fifth = collection.AddWithValue("@fifth", 5L);
        collection.Insert(1, new AhtolaParameter("@inserted", 10L));

        collection.Count.Should().Be(6);
        collection.SyncRoot.Should().NotBeNull();
        collection.Contains(first).Should().BeTrue();
        collection.Contains(2L).Should().BeTrue();
        collection.Contains("@FIRST").Should().BeFalse();
        collection.Contains("@first").Should().BeTrue();
        collection.IndexOf(first).Should().Be(0);
        collection.IndexOf(2L).Should().Be(2);
        collection.IndexOf("@third").Should().Be(3);

        var copied = new AhtolaParameter[collection.Count + 1];
        collection.CopyTo(copied, 1);
        copied[1].Should().BeSameAs(first);
        copied[^1].Should().BeSameAs(fifth);
        collection.Cast<AhtolaParameter>().Should().HaveCount(6);

        collection.Remove(2L);
        collection.RemoveAt("@inserted");
        collection.RemoveAt(collection.IndexOf(fifth));
        collection.Cast<AhtolaParameter>().Select(static parameter => parameter.Value)
            .Should().Equal(1L, 3L, 4L);

        collection.Clear();
        collection.Count.Should().Be(0);
    }

    [Test]
    public void AhtolaCollectionRejectsMissingAndWrongProviderParameters()
    {
        DbParameterCollection collection = new AhtolaParameterCollection
        {
            new AhtolaParameter("@value", 1L),
        };

        collection[0] = new AhtolaParameter("@replacement", 2L);
        collection["@replacement"] = new AhtolaParameter("@renamed", 3L);
        collection[0].Value.Should().Be(3L);

        Assert.Throws<ArgumentException>(() => collection[0] = new SqliteParameter("@wrong", 4L));
        Assert.Throws<ArgumentException>(() => collection["@missing"] = new AhtolaParameter("@value", 5L));
        Assert.Throws<ArgumentException>(() => collection.Remove("@missing-value"));
        Assert.Throws<ArgumentException>(() => collection.RemoveAt("@missing"));
        Assert.Throws<ArgumentException>(() => _ = collection["@missing"]);
        Assert.Throws<ArgumentNullException>(() => collection.CopyTo(null!, 0));
    }

    [Test]
    public void SqliteCollectionSupportsTypedOverloadsAndCaseInsensitiveLookup()
    {
        var collection = new SqliteParameterCollection();
        var id = collection.Add("$id", SqliteType.Integer);
        var payload = collection.Add("$payload", SqliteType.Blob, 128);
        var name = collection.AddWithValue("$name", "ahtola");

        collection.AddRange(new object[] { new SqliteParameter("$rank", 4L), 5L });
        collection.Insert(1, new SqliteParameter("$inserted", 10L));

        collection.Count.Should().Be(6);
        collection.SyncRoot.Should().NotBeNull();
        id.SqliteType.Should().Be(SqliteType.Integer);
        payload.SqliteType.Should().Be(SqliteType.Blob);
        payload.Size.Should().Be(128);
        collection["$NAME"].Should().BeSameAs(name);
        collection.Contains("$ID").Should().BeTrue();
        collection.Contains(id).Should().BeTrue();
        collection.Contains(5L).Should().BeTrue();
        collection.IndexOf("$PAYLOAD").Should().Be(2);
        collection.IndexOf(4L).Should().Be(4);

        var copied = new SqliteParameter[collection.Count];
        collection.CopyTo(copied, 0);
        copied.Should().Equal(collection.Cast<SqliteParameter>());

        collection.Remove(5L);
        collection.RemoveAt("$INSERTED");
        collection.RemoveAt(collection.IndexOf("$rank"));
        collection.Cast<SqliteParameter>().Select(static parameter => parameter.ParameterName)
            .Should().Equal("$id", "$payload", "$name");
    }

    [Test]
    public void SqliteCollectionRejectsMissingAndWrongProviderParameters()
    {
        DbParameterCollection collection = new SqliteParameterCollection
        {
            new SqliteParameter("$value", 1L),
        };

        collection[0] = new SqliteParameter("$replacement", 2L);
        collection["$REPLACEMENT"] = new SqliteParameter("$renamed", 3L);
        collection[0].Value.Should().Be(3L);

        Assert.Throws<ArgumentException>(() => collection[0] = new AhtolaParameter("@wrong", 4L));
        Assert.Throws<ArgumentException>(() => collection["$missing"] = new SqliteParameter("$value", 5L));
        Assert.Throws<ArgumentException>(() => collection.Remove("$missing-value"));
        Assert.Throws<ArgumentException>(() => collection.RemoveAt("$missing"));
        Assert.Throws<ArgumentException>(() => _ = collection["$missing"]);
        Assert.Throws<ArgumentNullException>(() => collection.AddRange(null!));
        Assert.Throws<ArgumentNullException>(() => collection.CopyTo(null!, 0));
    }
}
