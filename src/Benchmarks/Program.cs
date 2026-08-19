using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

/// <summary>Entry-point anchor for <see cref="BenchmarkSwitcher.FromAssembly"/>.</summary>
public partial class Program;
