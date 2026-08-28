using AwesomeAssertions;
using Ahtola.Core.Storage;
using Ahtola.Tests.Oracle;

namespace Ahtola.Tests;

/// <summary>
/// Bounded reference-model checks corresponding to Turso's randomized
/// <c>core/storage/page_cache.rs::test_page_cache_fuzz</c>.
/// </summary>
public sealed class ModelBasedStorageTests
{
    private const int OperationCount = 256;

    [TestCase(17)]
    [TestCase(0x5eed)]
    public void PagerReadCacheMatchesIndependentLruModelAfterEveryOperation(int defaultSeed)
    {
        var stream = StableTestSeed.Create((ulong)defaultSeed)
            .Derive($"pager-read-cache-{defaultSeed:x}");
        var trace = ReplayTrace.Create(TestContext.CurrentContext.Test.Name, stream);

        OracleFailureArtifacts.Run(trace, () =>
        {
            const int capacity = 4;
            var actual = new SqlitePagerReadCache(capacity);
            var model = new ReferenceReadCache(capacity);

            for (var step = 0; step < OperationCount; step++)
            {
                var pageNumber = checked((uint)(stream.Random.NextInt32(7) + 1));
                var generation = stream.Random.NextInt32(5);
                switch (stream.Random.NextInt32(100))
                {
                    case < 43:
                        {
                            var page = stream.Random.NextBytes(24);
                            Log(trace, step, $"ADD page={pageNumber} generation={generation} bytes={Convert.ToHexString(page)}");
                            actual.Add(pageNumber, generation, page);
                            model.Add(pageNumber, generation, page);
                            break;
                        }
                    case < 76:
                        {
                            Log(trace, step, $"GET page={pageNumber} generation={generation}");
                            var actualFound = actual.TryGetValue(pageNumber, generation, out var actualPage);
                            var expectedFound = model.TryGetValue(pageNumber, generation, out var expectedPage);
                            actualFound.Should().Be(expectedFound, because: Diagnostics(trace, step));
                            if (expectedFound)
                                actualPage.Should().Equal(expectedPage, because: Diagnostics(trace, step));
                            break;
                        }
                    case < 92:
                        Log(trace, step, $"REMOVE page={pageNumber}");
                        actual.Remove(pageNumber);
                        model.Remove(pageNumber);
                        break;
                    default:
                        Log(trace, step, "CLEAR");
                        actual.Clear();
                        model.Clear();
                        break;
                }

                AssertEquivalent(actual, model, step, trace);
            }
        });
    }

    private static void AssertEquivalent(
        SqlitePagerReadCache actual,
        ReferenceReadCache model,
        int step,
        ReplayTrace trace)
    {
        actual.Capacity.Should().Be(model.Capacity, because: Diagnostics(trace, step));
        actual.Count.Should().Be(model.Count, because: Diagnostics(trace, step));
        actual.Count.Should().BeLessThanOrEqualTo(actual.Capacity, because: Diagnostics(trace, step));

        // Touch the same keys in the same order on both caches. Count equality plus
        // finding every modeled key proves exact membership without inspecting internals.
        var keys = model.PageNumbers.Order().ToArray();
        foreach (var pageNumber in keys)
        {
            var generation = model.Generation(pageNumber);
            var expectedFound = model.TryGetValue(pageNumber, generation, out var expectedPage);
            var actualFound = actual.TryGetValue(pageNumber, generation, out var actualPage);
            expectedFound.Should().BeTrue(because: Diagnostics(trace, step));
            actualFound.Should().BeTrue(because: Diagnostics(trace, step));
            actualPage.Should().Equal(expectedPage, because: Diagnostics(trace, step));
        }

        actual.Count.Should().Be(model.Count, because: Diagnostics(trace, step));
    }

    private static void Log(ReplayTrace trace, int step, string operation)
        => trace.Add(
            $"-- cache step={step}: {operation}",
            comparison: "independent bounded LRU model",
            action: "pager-cache");

    private static string Diagnostics(ReplayTrace trace, int step)
        => $"{trace.SeedDiagnostics}; step={step}{Environment.NewLine}{trace.ToSql()}";

    private sealed class ReferenceReadCache(int capacity)
    {
        private readonly Dictionary<uint, CacheEntry> _entries = [];
        private readonly List<uint> _leastToMostRecent = [];

        internal int Capacity { get; } = capacity;

        internal int Count => _entries.Count;

        internal IEnumerable<uint> PageNumbers => _entries.Keys;

        internal long Generation(uint pageNumber) => _entries[pageNumber].Generation;

        internal void Add(uint pageNumber, long generation, byte[] page)
        {
            Remove(pageNumber);
            if (_entries.Count == Capacity)
            {
                var evicted = _leastToMostRecent[0];
                _leastToMostRecent.RemoveAt(0);
                _entries.Remove(evicted);
            }

            _entries.Add(pageNumber, new CacheEntry(generation, page.ToArray()));
            _leastToMostRecent.Add(pageNumber);
        }

        internal bool TryGetValue(uint pageNumber, long generation, out byte[] page)
        {
            if (!_entries.TryGetValue(pageNumber, out var entry))
            {
                page = null!;
                return false;
            }

            if (entry.Generation != generation)
            {
                Remove(pageNumber);
                page = null!;
                return false;
            }

            _leastToMostRecent.Remove(pageNumber);
            _leastToMostRecent.Add(pageNumber);
            page = entry.Page.ToArray();
            return true;
        }

        internal void Remove(uint pageNumber)
        {
            if (!_entries.Remove(pageNumber))
                return;
            _leastToMostRecent.Remove(pageNumber);
        }

        internal void Clear()
        {
            _entries.Clear();
            _leastToMostRecent.Clear();
        }

        private sealed record CacheEntry(long Generation, byte[] Page);
    }
}
