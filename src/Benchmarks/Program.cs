using BenchmarkDotNet.Running;
using Benchmarks;

if (args.Length > 0 && args[0] == HotspotQuickRunner.Switch)
{
    HotspotQuickRunner.Run(args);
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

/// <summary>Entry-point anchor for <see cref="BenchmarkSwitcher.FromAssembly"/>.</summary>
public partial class Program;
