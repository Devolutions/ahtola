using AwesomeAssertions;
using Ahtola.Core.Spatial;

namespace Ahtola.Tests;

public sealed class ManagedRTreeIndexTests
{
    [Test]
    public void BoundsAreInclusiveAndDimensionSafe()
    {
        var bounds = new ManagedRTreeBounds(0, 5, 10, 20);

        bounds.Intersects(new ManagedRTreeBounds(5, 8, 12, 15)).Should().BeTrue();
        bounds.Contains(new ManagedRTreeBounds(1, 4, 11, 19)).Should().BeTrue();
        bounds.Invoking(value => value.Intersects(new ManagedRTreeBounds(0, 1)))
            .Should().Throw<ArgumentException>();
    }

    [Test]
    public void IndexSplitsSearchesAndRemovesEntries()
    {
        var index = new ManagedRTreeIndex();
        for (var id = 1; id <= 24; id++)
            index.Upsert(id, new ManagedRTreeBounds(id, id + 1, id * 10, (id * 10) + 1));

        index.SearchIntersecting(new ManagedRTreeBounds(8.5, 11.5, 75, 115))
            .Should().Equal(8, 9, 10, 11);
        index.SearchContaining(new ManagedRTreeBounds(9.2, 9.8, 90.2, 90.8))
            .Should().Equal(9);

        index.Upsert(9, new ManagedRTreeBounds(100, 101, 100, 101));
        index.SearchIntersecting(new ManagedRTreeBounds(8.5, 11.5, 75, 115))
            .Should().Equal(8, 10, 11);
        index.Remove(10).Should().BeTrue();
        index.Remove(10).Should().BeFalse();
        index.Count.Should().Be(23);
    }

    [Test]
    public void IndexSplitsFiniteCoordinatesWithAnUnrepresentableHyperVolume()
    {
        var index = new ManagedRTreeIndex();
        for (var id = 1; id <= 9; id++)
            index.Upsert(id, new ManagedRTreeBounds(-1e308, 1e308));

        index.SearchIntersecting(new ManagedRTreeBounds(0, 1)).Should().Equal(1, 2, 3, 4, 5, 6, 7, 8, 9);
    }

    [Test]
    public void RandomizedQueriesAndCondenseMatchBruteForce()
    {
        const int seed = 0x5A17;
        var random = new Random(seed);
        var index = new ManagedRTreeIndex();
        var expected = new Dictionary<long, ManagedRTreeBounds>();
        for (var id = 1L; id <= 500; id++)
        {
            var x = random.NextDouble() * 2_000 - 1_000;
            var y = random.NextDouble() * 2_000 - 1_000;
            var bounds = new ManagedRTreeBounds(
                x,
                x + random.NextDouble() * 25,
                y,
                y + random.NextDouble() * 25);
            expected.Add(id, bounds);
            index.Upsert(id, bounds);
        }

        for (var queryIndex = 0; queryIndex < 200; queryIndex++)
        {
            var x = random.NextDouble() * 2_000 - 1_000;
            var y = random.NextDouble() * 2_000 - 1_000;
            var query = new ManagedRTreeBounds(x, x + 40, y, y + 40);
            var bruteForce = expected
                .Where(entry => entry.Value.Intersects(query))
                .Select(static entry => entry.Key)
                .Order()
                .ToArray();
            index.SearchIntersecting(query).Should().Equal(bruteForce);
        }

        foreach (var rowId in expected.Keys.Where(static rowId => rowId % 3 == 0).ToArray())
        {
            index.Remove(rowId).Should().BeTrue();
            expected.Remove(rowId);
        }

        foreach (var rowId in expected.Keys.Where(static rowId => rowId % 7 == 0).ToArray())
        {
            var x = random.NextDouble() * 2_000 - 1_000;
            var y = random.NextDouble() * 2_000 - 1_000;
            var bounds = new ManagedRTreeBounds(x, x + 10, y, y + 10);
            expected[rowId] = bounds;
            index.Upsert(rowId, bounds);
        }

        index.Validate().Should().BeEmpty();
        for (var queryIndex = 0; queryIndex < 200; queryIndex++)
        {
            var x = random.NextDouble() * 2_000 - 1_000;
            var y = random.NextDouble() * 2_000 - 1_000;
            var query = new ManagedRTreeBounds(x, x + 40, y, y + 40);
            var bruteForce = expected
                .Where(entry => entry.Value.Intersects(query))
                .Select(static entry => entry.Key)
                .Order()
                .ToArray();
            index.SearchIntersecting(query).Should().Equal(bruteForce);
        }

        index.SearchIntersecting(new ManagedRTreeBounds(10_000, 10_001, 10_000, 10_001))
            .Should().BeEmpty();
        index.LastSearchVisitedNodes.Should().Be(1);
        index.Snapshot().Select(static entry => entry.Key).Should().Equal(expected.Keys.Order());
    }

    [Test]
    public void DeleteAndUpdateWorkScalesWithoutRebuildingEverySurvivor()
    {
        var small = MeasureDeleteInsertions(512);
        var large = MeasureDeleteInsertions(1_024);

        small.Should().BeGreaterThan(0);
        large.Should().BeLessThan(small * 3,
            "doubling the tree must not quadruple deletion work as a full rebuild per row would");

        var index = CreateIndex(1_024);
        var before = index.TreeInsertionCount;
        index.Upsert(512, new ManagedRTreeBounds(-10, -9, -8, -7));

        index.RemoveOperationCount.Should().Be(1);
        index.TreeInsertionCount.Should().BeLessThan(before + index.Count,
            "one update must condense a path rather than reinsert the complete tree");
        index.Validate().Should().BeEmpty();
    }

    private static long MeasureDeleteInsertions(int count)
    {
        var index = CreateIndex(count);
        var before = index.TreeInsertionCount;
        for (var rowId = 1; rowId <= count; rowId += 2)
            index.Remove(rowId).Should().BeTrue();

        index.RemoveOperationCount.Should().Be(count / 2);
        index.Validate().Should().BeEmpty();
        return index.TreeInsertionCount - before;
    }

    private static ManagedRTreeIndex CreateIndex(int count)
    {
        var index = new ManagedRTreeIndex();
        for (var rowId = 1; rowId <= count; rowId++)
        {
            var x = rowId % 64;
            var y = rowId / 64;
            index.Upsert(rowId, new ManagedRTreeBounds(x, x + 0.5, y, y + 0.5));
        }
        return index;
    }
}
