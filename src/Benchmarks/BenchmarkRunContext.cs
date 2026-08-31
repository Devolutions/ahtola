namespace Benchmarks;

internal static class BenchmarkRunContext
{
    private const string SmokeVariable = "AHTOLA_BENCHMARK_SMOKE";

    public static int ScaleForSmoke(int value, int maximum)
        => string.Equals(
            Environment.GetEnvironmentVariable(SmokeVariable),
            "1",
            StringComparison.Ordinal)
            ? Math.Min(value, maximum)
            : value;
}
