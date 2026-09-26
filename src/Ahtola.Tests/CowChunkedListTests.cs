using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

/// <summary>
/// <see cref="CowChunkedList{T}"/> against a <see cref="List{T}"/> oracle, including clones that
/// share chunks and are then mutated on either side.
/// </summary>
public sealed class CowChunkedListTests
{
    private const int ChunkSize = CowChunkedList<int>.ChunkSize;

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void RandomOperationsAcrossChunkBoundariesMatchListAndNeverLeakIntoClones(int seed)
    {
        var random = new Random(seed);
        var lists = new List<(CowChunkedList<int> Subject, List<int> Oracle)> { (new(), []) };
        for (var step = 0; step < 6_000; step++)
        {
            var (subject, oracle) = lists[random.Next(lists.Count)];
            switch (random.Next(10))
            {
                case 0 or 1 or 2:
                    var appended = random.Next();
                    subject.Add(appended);
                    oracle.Add(appended);
                    break;
                case 3:
                    var inserted = random.Next();
                    var insertAt = random.Next(oracle.Count + 1);
                    subject.Insert(insertAt, inserted);
                    oracle.Insert(insertAt, inserted);
                    break;
                case 4 when oracle.Count > 0:
                    var removeAt = random.Next(oracle.Count);
                    subject.RemoveAt(removeAt);
                    oracle.RemoveAt(removeAt);
                    break;
                case 5 when oracle.Count > 0:
                    var replaced = random.Next(oracle.Count);
                    subject[replaced] = -step;
                    oracle[replaced] = -step;
                    break;
                case 6 when lists.Count < 6:
                    var clone = new CowChunkedList<int>();
                    clone.ShareFrom(subject);
                    lists.Add((clone, [.. oracle]));
                    break;
                case 7 when random.Next(40) == 0:
                    subject.Clear();
                    oracle.Clear();
                    break;
                case 8:
                    // Keep lists spanning several chunks.
                    for (var index = 0; index < ChunkSize / 2; index++)
                    {
                        subject.Add(index);
                        oracle.Add(index);
                    }

                    break;
            }

            if (step % 500 == 0)
            {
                foreach (var (candidate, expected) in lists)
                    AssertSame(candidate, expected);
            }
        }

        foreach (var (candidate, expected) in lists)
            AssertSame(candidate, expected);
    }

    [Test]
    public void InsertAndRemoveAtExactChunkEdges()
    {
        var subject = new CowChunkedList<int>();
        var oracle = new List<int>();
        for (var index = 0; index < ChunkSize * 2; index++)
        {
            subject.Add(index);
            oracle.Add(index);
        }

        var shared = new CowChunkedList<int>();
        shared.ShareFrom(subject);
        var sharedOracle = oracle.ToList();

        foreach (var position in new[] { 0, ChunkSize - 1, ChunkSize, ChunkSize * 2 - 1, ChunkSize * 2 })
        {
            subject.Insert(position, -position);
            oracle.Insert(position, -position);
            AssertSame(subject, oracle);
        }

        foreach (var position in new[] { ChunkSize * 2, ChunkSize, ChunkSize - 1, 0 })
        {
            subject.RemoveAt(position);
            oracle.RemoveAt(position);
            AssertSame(subject, oracle);
        }

        while (oracle.Count > ChunkSize)
        {
            subject.RemoveAt(oracle.Count - 1);
            oracle.RemoveAt(oracle.Count - 1);
        }

        subject.Add(7);
        oracle.Add(7);
        AssertSame(subject, oracle);
        AssertSame(shared, sharedOracle);
    }

    [Test]
    public void EnumerationThrowsWhenTheListChangesUnderIt()
    {
        var subject = new CowChunkedList<int> { 1, 2, 3 };

        var enumerate = () =>
        {
            foreach (var value in subject)
                subject.Add(value);
        };

        enumerate.Should().Throw<InvalidOperationException>();
    }

    private static void AssertSame(CowChunkedList<int> subject, List<int> oracle)
    {
        subject.Count.Should().Be(oracle.Count);
        subject.Should().Equal(oracle);
        subject.ToArray().Should().Equal(oracle);
        if (oracle.Count > 0)
        {
            subject[oracle.Count - 1].Should().Be(oracle[^1]);
            subject.IndexOf(oracle[^1]).Should().Be(oracle.IndexOf(oracle[^1]));
        }
    }
}
