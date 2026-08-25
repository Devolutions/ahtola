using System.Diagnostics;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Exercises the real attribution rules in <c>scripts/Invoke-TrimAnalysisGate.ps1</c> through its
/// <c>-ClassifyOnly</c> seam, so the gate's own parser is regression-tested rather than
/// reimplemented here.
/// </summary>
/// <remarks>
/// The decisive case is a warning raised at a <em>consumer</em> source location whose payload
/// names an Ahtola member (an IL2091 on the consumer's own call into an annotated Ahtola generic).
/// Classifying on the file prefix alone and returning early would file that under "upstream" and
/// let a real Ahtola trim hole through the gate.
/// </remarks>
public sealed class TrimAnalysisGateAttributionTests
{
    private const string ConsumerIl2091NamingAhtola =
        @"C:\src\consumer\Program.cs(42,13): warning IL2091: 'T' generic argument does not satisfy "
        + "'DynamicallyAccessedMemberTypes.PublicProperties' in "
        + "'Ahtola.Data.Sqlite.SqliteConnection.CreateAggregate<TAccumulate>(String, TAccumulate)'. "
        + "The generic parameter of the consumer method does not have matching annotations.";

    private const string ConsumerIl2091NamingDevolutionsAhtola =
        @"/home/runner/work/app/src/Program.cs(7,5): warning IL2026: Using member "
        + "'Devolutions.Ahtola.Data.Sqlite.SqliteConnection.EnableExtensions(Boolean)' which has "
        + "'RequiresUnreferencedCodeAttribute' can break functionality when trimming.";

    private const string AhtolaSourceWarning =
        @"D:\ci\src\Ahtola.Data\AhtolaSchemaCollections.cs(812,9): warning IL2111: Method "
        + "'System.Type.TypeInitializer.get' with parameters or return value with "
        + "`DynamicallyAccessedMembersAttribute` is accessed via reflection.";

    private const string IlLinkFormAhtolaMember =
        "ILLink : Trim analysis warning IL2111: Ahtola.AhtolaSchemaCollections.CreateReaderSchemaTable(): "
        + "Method 'System.Type.TypeInitializer.get' with parameters or return value with "
        + "`DynamicallyAccessedMembersAttribute` is accessed via reflection.";

    private const string UpstreamOnly =
        @"C:\src\consumer\Program.cs(3,1): warning IL2026: Using member "
        + "'Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.Include<T>(IQueryable<T>, String)' "
        + "which has 'RequiresUnreferencedCodeAttribute' can break functionality when trimming.";

    private const string UpstreamIlLinkForm =
        "ILLink : Trim analysis warning IL2026: Microsoft.EntityFrameworkCore.Query.QueryCompiler.Execute(): "
        + "Using member which has 'RequiresUnreferencedCodeAttribute'.";

    /// <summary>
    /// A checkout directory that merely contains "ahtola" must not be treated as evidence.
    /// </summary>
    private const string UpstreamInAhtolaCheckoutDirectory =
        @"D:\dev\ahtola-checkout\samples\AdoTrimConsumer\Program.cs(9,1): warning IL2026: Using member "
        + "'System.Data.DataTable.Load(IDataReader)' which has 'RequiresUnreferencedCodeAttribute'.";

    [Test]
    public void ClassifiesConsumerSiteWarningsThatNameAhtolaAsAhtola()
    {
        var ahtola = Classify(
            ConsumerIl2091NamingAhtola,
            ConsumerIl2091NamingDevolutionsAhtola,
            AhtolaSourceWarning,
            IlLinkFormAhtolaMember,
            UpstreamOnly,
            UpstreamIlLinkForm,
            UpstreamInAhtolaCheckoutDirectory);

        ahtola.Should().Contain(line => line.Contains("IL2091", StringComparison.Ordinal),
            "an IL2091 raised at a consumer source site that names an Ahtola member is ours");
        ahtola.Should().Contain(line => line.Contains("Devolutions.Ahtola.Data.Sqlite", StringComparison.Ordinal));
        ahtola.Should().Contain(line => line.Contains("AhtolaSchemaCollections.cs", StringComparison.Ordinal));
        ahtola.Should().Contain(line => line.Contains("ILLink : Trim analysis warning IL2111", StringComparison.Ordinal));

        ahtola.Should().NotContain(line => line.Contains("EntityFrameworkQueryableExtensions", StringComparison.Ordinal));
        ahtola.Should().NotContain(line => line.Contains("QueryCompiler", StringComparison.Ordinal));
        ahtola.Should().NotContain(line => line.Contains("DataTable.Load", StringComparison.Ordinal),
            "a checkout path containing 'ahtola' is not evidence");
        ahtola.Count.Should().Be(4);
    }

    [Test]
    public void DoesNotEarlyReturnOnAConsumerSourcePath()
    {
        // Exactly the regression: same payload, one raised in Ahtola source and one at a consumer
        // source site. Both are ours.
        var ahtola = Classify(ConsumerIl2091NamingAhtola, UpstreamOnly);

        ahtola.Count.Should().Be(1);
        ahtola[0].Should().Contain("IL2091");
    }

    [Test]
    public void TreatsAWarninglessLogAsClean()
        => Classify("  Determining projects to restore...", "Build succeeded.").Should().BeEmpty();

    private static List<string> Classify(params string[] lines)
    {
        var repositoryRoot = FindRepositoryRoot();
        var gate = Path.Combine(repositoryRoot, "scripts", "Invoke-TrimAnalysisGate.ps1");
        File.Exists(gate).Should().BeTrue($"'{gate}' must exist");

        var shell = ResolvePowerShell();
        if (shell is null)
            Assert.Ignore("pwsh is not available on this machine; the gate is a PowerShell 7 script.");

        var logDirectory = Path.Combine(
            repositoryRoot,
            "artifacts",
            "test-results",
            "trim-attribution",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logDirectory);
        var log = Path.Combine(logDirectory, "publish.log");
        try
        {
            File.WriteAllLines(log, lines);

            var start = new ProcessStartInfo(shell!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = repositoryRoot,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(gate);
            start.ArgumentList.Add("-ClassifyOnly");
            start.ArgumentList.Add(log);

            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            process.ExitCode.Should().Be(0, $"the gate must classify cleanly.\nstdout:\n{stdout}\nstderr:\n{stderr}");

            return [.. stdout
                .Split('\n')
                .Select(static line => line.Trim())
                .Where(static line => line.StartsWith("[Ahtola]", StringComparison.Ordinal))
                .Select(static line => line["[Ahtola]".Length..].Trim())];
        }
        finally
        {
            Directory.Delete(logDirectory, recursive: true);
        }
    }

    private static string? ResolvePowerShell()
    {
        foreach (var candidate in new[] { "pwsh", "pwsh.exe" })
        {
            try
            {
                using var probe = Process.Start(new ProcessStartInfo(candidate)
                {
                    ArgumentList = { "-NoProfile", "-Command", "exit 0" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                probe!.WaitForExit();
                return candidate;
            }
            catch (Exception)
            {
                // Try the next candidate.
            }
        }

        return null;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ahtola.slnx")))
            directory = directory.Parent;

        directory.Should().NotBeNull("the test run must sit inside the repository");
        return directory!.FullName;
    }
}
