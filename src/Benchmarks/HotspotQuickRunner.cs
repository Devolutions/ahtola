using System.Diagnostics;
using System.Reflection;
using BenchmarkDotNet.Attributes;

namespace Benchmarks;

/// <summary>
/// In-process stopwatch loop over <see cref="DotNetHotspotBenchmarks"/> for the
/// optimize-measure cycle: every case runs for a fixed time budget on both providers and
/// prints the Ahtola/SQLite ratio. BenchmarkDotNet remains the source of record; this is a
/// development aid (<c>dotnet run -c Release -- --hotspots-quick [filter] [milliseconds] [Sqlite|Ahtola]</c>).
/// </summary>
internal static class HotspotQuickRunner
{
    public const string Switch = "--hotspots-quick";

    public static void Run(string[] arguments)
    {
        var filter = arguments.Length > 1 ? arguments[1] : string.Empty;
        var budget = TimeSpan.FromMilliseconds(arguments.Length > 2 ? int.Parse(arguments[2]) : 400);
        var engines = arguments.Length > 3
            ? [Enum.Parse<DotNetHotspotBenchmarks.Provider>(arguments[3], ignoreCase: true)]
            : Enum.GetValues<DotNetHotspotBenchmarks.Provider>();
        var methods = typeof(DotNetHotspotBenchmarks)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.GetCustomAttribute<BenchmarkAttribute>() is not null)
            .Where(method => method.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var results = new Dictionary<(string Method, DotNetHotspotBenchmarks.Provider Engine), (double Mean, double Allocated)>();
        foreach (var engine in engines)
        {
            var benchmarks = new DotNetHotspotBenchmarks { Engine = engine };
            benchmarks.GlobalSetup();
            try
            {
                foreach (var method in methods)
                {
                    var result = Measure(benchmarks, method, budget);
                    results[(method.Name, engine)] = result;
                    Console.Error.WriteLine($"  {engine,-6} {method.Name,-28} {FormatTime(result.Mean),12}");
                }
            }
            finally
            {
                benchmarks.GlobalCleanup();
            }
        }

        if (engines.Length == 1)
            return;

        Console.WriteLine($"{"Case",-28} {"SQLite",12} {"Ahtola",12} {"Ratio",8} {"Alloc SQLite",14} {"Alloc Ahtola",14}");
        foreach (var method in methods)
        {
            var sqlite = results[(method.Name, DotNetHotspotBenchmarks.Provider.Sqlite)];
            var ahtola = results[(method.Name, DotNetHotspotBenchmarks.Provider.Ahtola)];
            Console.WriteLine(
                $"{method.Name,-28} {FormatTime(sqlite.Mean),12} {FormatTime(ahtola.Mean),12} {ahtola.Mean / sqlite.Mean,8:F2} {FormatBytes(sqlite.Allocated),14} {FormatBytes(ahtola.Allocated),14}");
        }
    }

    private static (double Mean, double Allocated) Measure(DotNetHotspotBenchmarks benchmarks, MethodInfo method, TimeSpan budget)
    {
        var attribute = method.GetCustomAttribute<BenchmarkAttribute>()!;
        var operations = Math.Max(1, attribute.OperationsPerInvoke);
        var iterationSetup = typeof(DotNetHotspotBenchmarks)
            .GetMethods()
            .FirstOrDefault(candidate => candidate.GetCustomAttribute<IterationSetupAttribute>() is { } setup
                                         && setup.Targets.Contains(method.Name));
        Action invoke = method.ReturnType == typeof(int)
            ? CreateInvoker(method.CreateDelegate<Func<int>>(benchmarks))
            : method.ReturnType == typeof(long)
                ? CreateInvoker(method.CreateDelegate<Func<long>>(benchmarks))
                : throw new NotSupportedException($"{method.Name} returns {method.ReturnType}.");

        // Warm up the JIT, statement caches and the page cache.
        var warmup = Stopwatch.StartNew();
        while (warmup.Elapsed < budget / 4)
        {
            iterationSetup?.Invoke(benchmarks, null);
            invoke();
        }

        var samples = new List<double>();
        long allocated = 0;
        long invocations = 0;
        var total = Stopwatch.StartNew();
        while (total.Elapsed < budget || samples.Count < 3)
        {
            iterationSetup?.Invoke(benchmarks, null);
            var before = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            invoke();
            samples.Add(Stopwatch.GetElapsedTime(start).TotalNanoseconds / operations);
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            invocations++;
        }

        samples.Sort();
        // Median resists GC and checkpoint outliers better than the mean in a short loop.
        return (samples[samples.Count / 2], (double)allocated / invocations / operations);
    }

    private static Action CreateInvoker<T>(Func<T> benchmark)
        => () => GC.KeepAlive(benchmark());

    private static string FormatTime(double nanoseconds)
        => nanoseconds switch
        {
            >= 1_000_000 => $"{nanoseconds / 1_000_000:F2} ms",
            >= 1_000 => $"{nanoseconds / 1_000:F2} us",
            _ => $"{nanoseconds:F0} ns",
        };

    private static string FormatBytes(double bytes)
        => bytes switch
        {
            >= 1024 * 1024 => $"{bytes / (1024 * 1024):F1} MB",
            >= 1024 => $"{bytes / 1024:F1} KB",
            _ => $"{bytes:F0} B",
        };
}
