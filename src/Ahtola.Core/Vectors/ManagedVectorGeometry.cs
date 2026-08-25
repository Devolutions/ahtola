namespace Ahtola.Core.Vectors;

/// <summary>
/// The metric-specific geometry an IVF index needs: how a vector is projected into the clustering
/// space, how a member's distance to its centroid is measured, and — the part that makes the index
/// exact — how far away every member of an unprobed list is <em>provably</em> guaranteed to be.
/// </summary>
/// <remarks>
/// <para>
/// Every bound below is a lower bound on the value <see cref="SqliteVectorFunctions.DistanceExact"/>
/// would report for any member of a list, derived from a real-arithmetic inequality and then widened
/// by a floating-point slack. The certificate the search loop checks is
/// <c>bound(list) &gt; kth-best-reported-distance</c>: when it holds, no member of that list can
/// enter the top-k, so skipping the list cannot change the answer. When it does not hold the list is
/// probed. The loop therefore degrades to a full scan rather than to a wrong answer.
/// </para>
/// <para>
/// The inequalities used, per metric:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>l2</c>: the triangle inequality, <c>‖q − v‖ ≥ ‖q − c‖ − ‖c − v‖ ≥ d(q,c) − radius</c>.
/// </description></item>
/// <item><description>
/// <c>cosine</c>: the angle between unit vectors is a metric, so
/// <c>θ(q,v) ≥ θ(q,c) − θ(c,v) ≥ θ(q,c) − radius</c>, and <c>1 − cos θ</c> is monotone increasing on
/// <c>[0, π]</c>, so the angular bound transfers directly to the reported cosine distance.
/// </description></item>
/// <item><description>
/// <c>dot</c> (reported negated): <c>q·v = q·c + q·(v − c) ≤ q·c + ‖q‖·radius</c> by
/// Cauchy–Schwarz, so <c>−q·v ≥ −(q·c + ‖q‖·radius)</c>.
/// </description></item>
/// <item><description>
/// <c>float1bit</c>: the reported cosine distance <em>is</em> the Hamming distance and the reported
/// dot distance is <c>2·hamming − dims</c>; Hamming is a metric over bit strings, so the same
/// triangle inequality applies with exact integer arithmetic and zero slack.
/// </description></item>
/// </list>
/// <para>
/// This is not a port of Turso's <c>toy_vector_sparse_ivf</c>, whose <c>delta</c>/<c>scan_portion</c>/
/// <c>scan_order</c> knobs prune heuristically with no recall bound at all. See
/// docs/managed-vector-index.md.
/// </para>
/// </remarks>
internal static class ManagedVectorGeometry
{
    /// <summary>A list bound that permits no pruning, used whenever an inequality cannot be proven.</summary>
    public const double Unprovable = double.NegativeInfinity;

    /// <summary>
    /// Projects a decoded vector into the space centroids live in.
    /// </summary>
    /// <remarks>
    /// <c>cosine</c> clusters unit-normalized vectors so that Euclidean proximity in the clustering
    /// space is monotone in angle; every other metric clusters the raw components. A cosine vector
    /// with zero norm has no direction at all and is reported as unprojectable, which routes it to
    /// the always-probed bucket instead of into a list whose radius it would silently invalidate.
    /// </remarks>
    public static bool TryProject(ReadOnlySpan<double> values, VectorDistanceKind metric, out double[] projected)
    {
        projected = [];
        foreach (var value in values)
        {
            if (!double.IsFinite(value))
                return false;
        }

        if (metric != VectorDistanceKind.Cosine)
        {
            projected = values.ToArray();
            return true;
        }

        return TryNormalize(values, out projected);
    }

    /// <summary>Scales a vector to unit length, or reports that it has no direction.</summary>
    public static bool TryNormalize(ReadOnlySpan<double> values, out double[] unit)
    {
        unit = [];
        var norm = Norm(values);
        if (!double.IsFinite(norm) || norm <= 0.0)
            return false;

        var scaled = new double[values.Length];
        for (var index = 0; index < scaled.Length; index++)
            scaled[index] = values[index] / norm;

        unit = scaled;
        return true;
    }

    /// <summary>Euclidean distance in the clustering space; the assignment and radius measure.</summary>
    public static double ClusterDistance(ReadOnlySpan<double> left, ReadOnlySpan<double> right)
        => Math.Sqrt(ClusterDistanceSquared(left, right));

    /// <summary>Squared Euclidean distance, used where only ordering matters.</summary>
    public static double ClusterDistanceSquared(ReadOnlySpan<double> left, ReadOnlySpan<double> right)
    {
        var total = 0.0;
        for (var index = 0; index < left.Length; index++)
        {
            var difference = left[index] - right[index];
            total += difference * difference;
        }

        return total;
    }

    /// <summary>Euclidean norm.</summary>
    public static double Norm(ReadOnlySpan<double> values) => Math.Sqrt(DotProduct(values, values));

    /// <summary>Inner product.</summary>
    public static double DotProduct(ReadOnlySpan<double> left, ReadOnlySpan<double> right)
    {
        var total = 0.0;
        for (var index = 0; index < left.Length; index++)
            total += left[index] * right[index];

        return total;
    }

    /// <summary>The angle in radians between two unit vectors.</summary>
    public static double UnitAngle(ReadOnlySpan<double> left, ReadOnlySpan<double> right)
        => Math.Acos(Math.Clamp(DotProduct(left, right), -1.0, 1.0));

    /// <summary>The 0/1 bit pattern of a decoded <c>float1bit</c> vector.</summary>
    public static bool[] ToBits(ReadOnlySpan<double> values)
    {
        var bits = new bool[values.Length];
        for (var index = 0; index < values.Length; index++)
            bits[index] = values[index] != 0.0;

        return bits;
    }

    /// <summary>The bit pattern a <c>float1bit</c> centroid represents, thresholded at one half.</summary>
    public static bool[] BinarizeCentroid(ReadOnlySpan<double> centroid)
    {
        var bits = new bool[centroid.Length];
        for (var index = 0; index < centroid.Length; index++)
            bits[index] = centroid[index] >= 0.5;

        return bits;
    }

    /// <summary>Hamming distance between two equal-length bit patterns.</summary>
    public static int Hamming(ReadOnlySpan<bool> left, ReadOnlySpan<bool> right)
    {
        var distance = 0;
        for (var index = 0; index < left.Length; index++)
        {
            if (left[index] != right[index])
                distance++;
        }

        return distance;
    }

    /// <summary>
    /// The relative floating-point slack that covers the difference between the exact real distance
    /// and the value the scalar evaluator reports.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>float32</c> vectors accumulate their sums in <c>float</c> (see <c>DistanceFloat32</c>), so
    /// a <c>dims</c>-term sum carries a relative error near <c>dims · 2⁻²⁴</c>. The value returned
    /// here is <c>(dims + 8) · 2⁻²²</c>, roughly eight times that worst case, so an accumulation
    /// pattern this analysis did not anticipate still cannot make the bound unsafe.
    /// </para>
    /// <para>
    /// <c>float64</c> and <c>float8</c> reach the reported value through <c>double</c> arithmetic, so
    /// their slack is the same expression at <c>2⁻⁴⁶</c>. <c>float1bit</c> distances are exact
    /// integer counts and take no slack at all.
    /// </para>
    /// </remarks>
    public static double RelativeSlack(VectorEncodingKind encoding, int dimensions)
        => encoding switch
        {
            VectorEncodingKind.Float1Bit => 0.0,
            VectorEncodingKind.Float32 => (dimensions + 8) * Math.ScaleB(1.0, -22),
            _ => (dimensions + 8) * Math.ScaleB(1.0, -46),
        };

    /// <summary>
    /// The absolute floor added to every slack, covering underflow of squared components to zero.
    /// </summary>
    /// <remarks>
    /// A <c>float32</c> square underflows below roughly 1e-22 per component; summed over the
    /// dimension cap and square rooted that is under 1e-20, so 1e-18 is a safe floor. The
    /// <c>double</c> equivalent is many orders of magnitude smaller.
    /// </remarks>
    public static double AbsoluteSlack(VectorEncodingKind encoding)
        => encoding switch
        {
            VectorEncodingKind.Float1Bit => 0.0,
            VectorEncodingKind.Float32 => 1e-18,
            _ => 1e-70,
        };

    /// <summary>
    /// Reproduces the squared-norm accumulator <c>vector_distance_cos</c> builds for one vector, in
    /// the accumulation width the encoding implies, and reports whether the cosine it feeds is in a
    /// range where the reported value still tracks the real one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the part of the geometry that cannot be reasoned about in <see cref="double"/>. The
    /// scalar evaluator sums <c>float</c> squares for a <c>float32</c> column
    /// (<c>DistanceFloat32</c>), so a vector of 1e-24 components has a squared norm of 1e-48 per
    /// term, which is below the smallest subnormal <c>float</c> and accumulates to exactly zero. The
    /// evaluator then takes its documented degenerate branch and reports 0 or 1 — a value with no
    /// relationship to the angle at all. A vector of 1e20 components overflows the same accumulator
    /// to infinity and collapses the reported distance to 1. Every angular bound derived from the
    /// widened <c>double</c> components would be wrong for such a row, so it is routed to the
    /// always-probed bucket instead of into a list.
    /// </para>
    /// <para>
    /// The usable window is deliberately conservative: it also requires that the product of two
    /// usable squared norms neither overflows nor underflows, because the reported value divides by
    /// the square root of that product. Over-reporting degeneracy costs probes and never costs
    /// recall, so the window is set by powers of two well inside the format rather than by the last
    /// representable value.
    /// </para>
    /// </remarks>
    public static bool IsCosineScalarUsable(ReadOnlySpan<double> values, VectorEncodingKind encoding)
    {
        // float1bit cosine is an exact integer Hamming count; there is no norm and no rounding.
        if (encoding == VectorEncodingKind.Float1Bit)
            return true;

        if (encoding == VectorEncodingKind.Float32)
        {
            var accumulated = 0.0f;
            foreach (var value in values)
            {
                var component = (float)value;
                if (!float.IsFinite(component))
                    return false;

                accumulated += component * component;
            }

            return float.IsFinite(accumulated)
                && accumulated >= Float32MinimumUsableSquaredNorm
                && accumulated <= Float32MaximumUsableSquaredNorm;
        }

        var total = 0.0;
        foreach (var value in values)
        {
            if (!double.IsFinite(value))
                return false;

            total += value * value;
        }

        return double.IsFinite(total)
            && total >= DoubleMinimumUsableSquaredNorm
            && total <= DoubleMaximumUsableSquaredNorm;
    }

    /// <summary>2⁻⁶³: the square of two such norms is still the smallest normal <c>float</c>.</summary>
    private const float Float32MinimumUsableSquaredNorm = 1.0842021724855044E-19f;

    /// <summary>2⁶³: the square of two such norms is two binades below <c>float.MaxValue</c>.</summary>
    private const float Float32MaximumUsableSquaredNorm = 9.2233720368547758E+18f;

    /// <summary>2⁻⁵¹¹, the <c>double</c> equivalent of the <c>float32</c> floor.</summary>
    private const double DoubleMinimumUsableSquaredNorm = 1.4916681462400413E-154;

    /// <summary>2⁵¹¹, the <c>double</c> equivalent of the <c>float32</c> ceiling.</summary>
    private const double DoubleMaximumUsableSquaredNorm = 6.7039039649712985E+153;
}

/// <summary>
/// The per-query, per-list bound calculator. Built once per search from the query vector, then asked
/// for one provable lower bound per list.
/// </summary>
internal sealed class ManagedVectorBoundCalculator
{
    private readonly VectorDistanceKind _metric;
    private readonly VectorEncodingKind _encoding;
    private readonly double[] _projected;
    private readonly bool[] _queryBits;
    private readonly double _queryNorm;
    private readonly double _relativeSlack;
    private readonly double _absoluteSlack;
    private readonly int _dimensions;

    public ManagedVectorBoundCalculator(
        in DecodedVector query,
        VectorDistanceKind metric,
        VectorDistanceKind clusteringMetric,
        VectorEncodingKind encoding)
    {
        _metric = metric;
        _encoding = encoding;
        _dimensions = query.Dimensions;
        _relativeSlack = ManagedVectorGeometry.RelativeSlack(encoding, query.Dimensions);
        _absoluteSlack = ManagedVectorGeometry.AbsoluteSlack(encoding);
        _queryBits = encoding == VectorEncodingKind.Float1Bit
            ? ManagedVectorGeometry.ToBits(query.Values)
            : [];

        // A query the geometry cannot place (a non-finite component, or a zero-norm vector under
        // cosine clustering) yields no usable inequality at all, so every list stays unprunable and
        // the search degrades to a full scan.
        var prunable = ManagedVectorGeometry.TryProject(query.Values, clusteringMetric, out var projected);
        _projected = projected;
        _queryNorm = 0.0;
        if (prunable && metric == VectorDistanceKind.Dot && encoding != VectorEncodingKind.Float1Bit)
        {
            _queryNorm = ManagedVectorGeometry.Norm(query.Values);
            prunable = double.IsFinite(_queryNorm);
        }

        // A query whose scalar-arithmetic norm underflows, overflows or is not finite makes
        // vector_distance_cos report its degenerate 0/1 constant for every row, which no angular
        // inequality describes. Nothing can be pruned for such a query: the search reads everything
        // and the ordinary comparison decides, exactly as the scan would.
        if (prunable
            && metric == VectorDistanceKind.Cosine
            && !ManagedVectorGeometry.IsCosineScalarUsable(query.Values, encoding))
        {
            prunable = false;
        }

        CanPrune = prunable;
    }

    /// <summary>False when no list can ever be pruned for this query.</summary>
    public bool CanPrune { get; }

    /// <summary>
    /// A provable lower bound on the reported distance of every member of a list, already widened by
    /// the floating-point slack. <see cref="ManagedVectorGeometry.Unprovable"/> when no inequality
    /// applies, which forces the list to be probed.
    /// </summary>
    /// <param name="centroid">
    /// The list centroid in the clustering space — unit length for cosine, raw components otherwise.
    /// </param>
    /// <param name="centroidBits">The binarized centroid, for <c>float1bit</c> columns only.</param>
    /// <param name="radius">
    /// An upper bound on the clustering-space distance (an angle for cosine, a Hamming count for
    /// <c>float1bit</c>) from the centroid to its farthest member.
    /// </param>
    public double LowerBound(ReadOnlySpan<double> centroid, bool[] centroidBits, double radius)
    {
        if (!CanPrune || !double.IsFinite(radius) || radius < 0.0)
            return ManagedVectorGeometry.Unprovable;

        if (_encoding == VectorEncodingKind.Float1Bit)
        {
            if (_queryBits.Length != centroidBits.Length)
                return ManagedVectorGeometry.Unprovable;

            // Exact integer Hamming geometry: no slack is needed or applied.
            var hamming = ManagedVectorGeometry.Hamming(_queryBits, centroidBits);
            var separation = Math.Max(0.0, hamming - radius);
            return _metric switch
            {
                VectorDistanceKind.Cosine => separation,
                VectorDistanceKind.Dot => (2.0 * separation) - _dimensions,
                _ => ManagedVectorGeometry.Unprovable,
            };
        }

        switch (_metric)
        {
            case VectorDistanceKind.L2:
                {
                    var centroidDistance = ManagedVectorGeometry.ClusterDistance(_projected, centroid);
                    if (!double.IsFinite(centroidDistance))
                        return ManagedVectorGeometry.Unprovable;

                    var bound = Math.Max(0.0, centroidDistance - radius);
                    var magnitude = centroidDistance + radius;
                    return bound - (magnitude * _relativeSlack) - _absoluteSlack;
                }

            case VectorDistanceKind.Cosine:
                {
                    // Both operands are unit vectors in the clustering space, so the inner product is
                    // the cosine of the angle between them.
                    var angle = ManagedVectorGeometry.UnitAngle(_projected, centroid);
                    if (!double.IsFinite(angle))
                        return ManagedVectorGeometry.Unprovable;

                    var separation = Math.Max(0.0, angle - radius);
                    var bound = 1.0 - Math.Cos(separation);

                    // Cosine distance lives in [0, 2] and the reported form subtracts a near-one ratio
                    // from one, so its error is absolute rather than relative; the magnitude proxy is
                    // the whole range and the relative term is doubled to cover the two accumulated
                    // norms as well as the inner product.
                    return bound - (4.0 * _relativeSlack) - _absoluteSlack;
                }

            case VectorDistanceKind.Dot:
                {
                    var inner = ManagedVectorGeometry.DotProduct(_projected, centroid);
                    if (!double.IsFinite(inner))
                        return ManagedVectorGeometry.Unprovable;

                    var reach = _queryNorm * radius;
                    var bound = -(inner + reach);
                    if (!double.IsFinite(bound))
                        return ManagedVectorGeometry.Unprovable;

                    var magnitude = Math.Abs(inner) + reach;
                    return bound - (magnitude * _relativeSlack) - _absoluteSlack;
                }

            default:
                return ManagedVectorGeometry.Unprovable;
        }
    }
}
