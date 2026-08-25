namespace Ahtola.Core.Vectors;

/// <summary>One sampled training row: its rowid and its projected clustering-space vector.</summary>
internal readonly record struct ManagedVectorTrainingSample(long RowId, double[] Values);

/// <summary>
/// Algorithm R reservoir sampling, fed one row at a time so a training scan never materializes more
/// than <c>train_sample</c> projected vectors.
/// </summary>
/// <remarks>
/// <para>
/// Retention is the point: a projected vector is <c>dimensions</c> doubles, so buffering every
/// eligible row before sampling made training cost <c>O(rows × dims)</c> memory on a table whose
/// sample is capped at a few thousand rows. Offering rows as they are decoded keeps the working set
/// at <c>O(train_sample × dims)</c> regardless of table size.
/// </para>
/// <para>
/// The eligible population is deliberately <em>not</em> the reservoir size: <see cref="Seen"/>
/// counts every row that could have been sampled, which is the number the drift rule compares
/// against. Conflating the two makes any table larger than the cap look like it is permanently
/// growing.
/// </para>
/// <para>
/// The draw sequence is identical to sampling a fully materialized list: the same rows are offered,
/// in the same rowid-ascending order, and the generator is consulted exactly once per row past the
/// reservoir's capacity. Trained centroids are therefore byte-identical to the buffered form.
/// </para>
/// </remarks>
internal sealed class ManagedVectorReservoirSampler
{
    private readonly List<ManagedVectorTrainingSample> _reservoir;
    private readonly int _capacity;
    private readonly ManagedVectorRandom _random;
    private long _seen;

    public ManagedVectorReservoirSampler(int capacity, ManagedVectorRandom random)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _capacity = capacity;
        _random = random;
        _reservoir = new List<ManagedVectorTrainingSample>(Math.Min(capacity, 1024));
    }

    /// <summary>Eligible rows offered so far — the population, not the retained sample.</summary>
    public long Seen => _seen;

    /// <summary>Rows currently retained; never more than the configured capacity.</summary>
    public int RetainedCount => _reservoir.Count;

    public void Offer(long rowId, double[] values)
    {
        ArgumentNullException.ThrowIfNull(values);

        _seen++;
        if (_reservoir.Count < _capacity)
        {
            _reservoir.Add(new ManagedVectorTrainingSample(rowId, values));
            return;
        }

        var slot = _random.NextBounded(_seen);
        if (slot < _capacity)
            _reservoir[(int)slot] = new ManagedVectorTrainingSample(rowId, values);
    }

    /// <summary>The drawn sample, restored to rowid-ascending order.</summary>
    /// <remarks>
    /// A reservoir filled out of rowid order after replacements would make the k-means++ tie rule
    /// depend on replacement history; sorting restores the canonical order.
    /// </remarks>
    public List<ManagedVectorTrainingSample> Complete()
    {
        _reservoir.Sort(static (left, right) => left.RowId.CompareTo(right.RowId));
        return _reservoir;
    }
}

/// <summary>
/// Deterministic k-means training for the vector index.
/// </summary>
/// <remarks>
/// <para>
/// Every source of nondeterminism is removed on purpose, because the trained centroids are persisted
/// in the catalog row: the sample is drawn in rowid-ascending order (not storage order, which an
/// update can permute), the generator is <see cref="ManagedVectorRandom"/> seeded from the declared
/// seed and the index configuration, accumulation runs in <c>double</c> over a fixed index order, and
/// every tie — nearest centroid, farthest point, empty-cluster reseed — resolves to the lowest index.
/// The same rows with the same <c>WITH</c> options therefore produce byte-identical state on x64,
/// ARM64, NativeAOT and WebAssembly.
/// </para>
/// <para>
/// The clustering space is supplied by <see cref="ManagedVectorGeometry.TryProject"/>: raw components
/// for <c>l2</c>/<c>dot</c>/<c>float1bit</c>, unit-normalized components for <c>cosine</c> so that
/// Euclidean proximity in the clustering space is monotone in angle.
/// </para>
/// </remarks>
internal static class ManagedVectorTraining
{
    /// <summary>How often long-running loops give the engine a chance to observe an interrupt.</summary>
    public const int InterruptPollInterval = 64;

    /// <summary>
    /// Draws a bounded, deterministic sample of projected vectors in rowid-ascending order.
    /// </summary>
    /// <remarks>
    /// Algorithm R reservoir sampling: the first <paramref name="capacity"/> eligible rows fill the
    /// reservoir, and each later row replaces a uniformly chosen slot with probability
    /// <c>capacity / seen</c>. The walk order is rowid ascending, so neither the insertion order nor
    /// the physical row layout can change which rows are drawn.
    /// </remarks>
    public static List<ManagedVectorTrainingSample> Sample(
        IReadOnlyList<(long RowId, double[] Values)> rowsInRowIdOrder,
        int capacity,
        ManagedVectorRandom random)
    {
        ArgumentNullException.ThrowIfNull(rowsInRowIdOrder);
        ArgumentNullException.ThrowIfNull(random);

        var sampler = new ManagedVectorReservoirSampler(capacity, random);
        foreach (var (rowId, values) in rowsInRowIdOrder)
            sampler.Offer(rowId, values);

        return sampler.Complete();
    }

    /// <summary>
    /// Trains <paramref name="lists"/> centroids over <paramref name="samples"/> and returns them as
    /// a flat <c>lists × dimensions</c> float32 array, the durable centroid payload.
    /// </summary>
    /// <remarks>
    /// k-means++ seeding followed by exactly <paramref name="iterations"/> Lloyd passes. Centroids
    /// are narrowed to float32 because they only steer the search: every distance that reaches a
    /// result is recomputed from the base row through the scalar evaluator, so centroid precision
    /// affects how many lists get probed and never affects which rows come back.
    /// </remarks>
    public static float[] Train(
        IReadOnlyList<ManagedVectorTrainingSample> samples,
        int lists,
        int dimensions,
        int iterations,
        ManagedVectorRandom random,
        Action? checkInterrupt = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(random);
        if (lists <= 0)
            throw new ArgumentOutOfRangeException(nameof(lists));
        if (dimensions <= 0)
            throw new ArgumentOutOfRangeException(nameof(dimensions));

        var centroids = new double[lists][];
        for (var list = 0; list < lists; list++)
            centroids[list] = new double[dimensions];

        if (samples.Count == 0)
            return Flatten(centroids, lists, dimensions);

        SeedPlusPlus(samples, centroids, lists, dimensions, random, checkInterrupt);

        var assignments = new int[samples.Count];
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var moved = Assign(samples, centroids, assignments, checkInterrupt);
            Recentre(samples, centroids, assignments, lists, dimensions, checkInterrupt);
            if (!moved && iteration > 0)
                break;
        }

        return Flatten(centroids, lists, dimensions);
    }

    /// <summary>k-means++ seeding with deterministic D² sampling and lowest-index tie resolution.</summary>
    private static void SeedPlusPlus(
        IReadOnlyList<ManagedVectorTrainingSample> samples,
        double[][] centroids,
        int lists,
        int dimensions,
        ManagedVectorRandom random,
        Action? checkInterrupt)
    {
        var first = (int)random.NextBounded(samples.Count);
        Array.Copy(samples[first].Values, centroids[0], dimensions);

        var nearest = new double[samples.Count];
        for (var index = 0; index < samples.Count; index++)
            nearest[index] = ManagedVectorGeometry.ClusterDistanceSquared(samples[index].Values, centroids[0]);

        for (var list = 1; list < lists; list++)
        {
            checkInterrupt?.Invoke();
            var total = 0.0;
            foreach (var value in nearest)
                total += value;

            int chosen;
            if (!(total > 0.0) || !double.IsFinite(total))
            {
                // Every remaining point coincides with an existing centre: spread the leftovers over
                // distinct sample rows so empty lists do not all collapse onto sample 0.
                chosen = Math.Min(list, samples.Count - 1);
            }
            else
            {
                var target = random.NextDouble() * total;
                var running = 0.0;
                chosen = samples.Count - 1;
                for (var index = 0; index < samples.Count; index++)
                {
                    running += nearest[index];
                    if (running > target)
                    {
                        chosen = index;
                        break;
                    }
                }
            }

            Array.Copy(samples[chosen].Values, centroids[list], dimensions);
            for (var index = 0; index < samples.Count; index++)
            {
                var candidate = ManagedVectorGeometry.ClusterDistanceSquared(samples[index].Values, centroids[list]);
                if (candidate < nearest[index])
                    nearest[index] = candidate;
            }
        }
    }

    /// <summary>Assigns every sample to its nearest centroid, breaking ties toward the lowest list.</summary>
    private static bool Assign(
        IReadOnlyList<ManagedVectorTrainingSample> samples,
        double[][] centroids,
        int[] assignments,
        Action? checkInterrupt)
    {
        var moved = false;
        for (var index = 0; index < samples.Count; index++)
        {
            if (index % InterruptPollInterval == 0)
                checkInterrupt?.Invoke();

            var best = NearestCentroid(samples[index].Values, centroids);
            if (assignments[index] != best)
            {
                assignments[index] = best;
                moved = true;
            }
        }

        return moved;
    }

    /// <summary>The index of the nearest centroid, lowest index on a tie.</summary>
    public static int NearestCentroid(ReadOnlySpan<double> values, double[][] centroids)
    {
        var best = 0;
        var bestDistance = double.PositiveInfinity;
        for (var list = 0; list < centroids.Length; list++)
        {
            var distance = ManagedVectorGeometry.ClusterDistanceSquared(values, centroids[list]);

            // Strictly-less keeps the lowest list on a tie, which is what makes assignment stable
            // across platforms whose loops may otherwise evaluate in a different order.
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = list;
            }
        }

        return best;
    }

    /// <summary>Recomputes each centroid as the mean of its members, reseeding empty lists.</summary>
    private static void Recentre(
        IReadOnlyList<ManagedVectorTrainingSample> samples,
        double[][] centroids,
        int[] assignments,
        int lists,
        int dimensions,
        Action? checkInterrupt)
    {
        var sums = new double[lists][];
        var counts = new int[lists];
        for (var list = 0; list < lists; list++)
            sums[list] = new double[dimensions];

        // Accumulate in ascending sample order so the summation order — and therefore the rounding —
        // is fixed for a given sample set.
        for (var index = 0; index < samples.Count; index++)
        {
            if (index % InterruptPollInterval == 0)
                checkInterrupt?.Invoke();

            var list = assignments[index];
            var values = samples[index].Values;
            var accumulator = sums[list];
            for (var component = 0; component < dimensions; component++)
                accumulator[component] += values[component];

            counts[list]++;
        }

        for (var list = 0; list < lists; list++)
        {
            if (counts[list] > 0)
            {
                var accumulator = sums[list];
                var centroid = centroids[list];
                for (var component = 0; component < dimensions; component++)
                    centroid[component] = accumulator[component] / counts[list];

                continue;
            }

            ReseedEmptyCluster(samples, centroids, assignments, list, dimensions);
        }
    }

    /// <summary>
    /// Moves an empty centroid onto the sample that is farthest from its own centre, with the lowest
    /// sample index winning a tie.
    /// </summary>
    private static void ReseedEmptyCluster(
        IReadOnlyList<ManagedVectorTrainingSample> samples,
        double[][] centroids,
        int[] assignments,
        int list,
        int dimensions)
    {
        var farthest = -1;
        var farthestDistance = -1.0;
        for (var index = 0; index < samples.Count; index++)
        {
            var owner = assignments[index];
            var distance = ManagedVectorGeometry.ClusterDistanceSquared(samples[index].Values, centroids[owner]);
            if (distance > farthestDistance)
            {
                farthestDistance = distance;
                farthest = index;
            }
        }

        if (farthest < 0)
            return;

        Array.Copy(samples[farthest].Values, centroids[list], dimensions);
        assignments[farthest] = list;
    }

    private static float[] Flatten(double[][] centroids, int lists, int dimensions)
    {
        var payload = new float[checked(lists * dimensions)];
        for (var list = 0; list < lists; list++)
        {
            var centroid = centroids[list];
            for (var component = 0; component < dimensions; component++)
            {
                var value = (float)centroid[component];

                // A centroid that is not finite would poison every bound derived from it and would
                // be rejected on reload; collapsing it to zero here keeps training total.
                payload[(list * dimensions) + component] = float.IsFinite(value) ? value : 0.0f;
            }
        }

        return payload;
    }
}
