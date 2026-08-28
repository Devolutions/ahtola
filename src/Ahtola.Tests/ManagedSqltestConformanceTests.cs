using System.Text;
using AwesomeAssertions;
using Ahtola.Tests.Sqltest;

namespace Ahtola.Tests;

/// <summary>
/// Runs the repository's <c>sqlite-sqltests</c> conformance corpus against the managed
/// engine. Every case in the corpus is discovered; cases the managed engine cannot
/// currently satisfy are listed in <c>Conformance/managed-sqltest-expected-failures.txt</c>
/// so both coverage and known gaps stay visible instead of being silently omitted.
/// </summary>
[Category("CoverageExcluded")]
public class ManagedSqltestConformanceTests
{
    public static IEnumerable<TestCaseData> CorpusFiles()
    {
        foreach (var relativePath in SqltestCorpus.Cases
                     .Select(static discovered => discovered.RelativePath)
                     .Distinct(StringComparer.Ordinal))
        {
            yield return new TestCaseData(relativePath).SetName($"ManagedSqltestFile({relativePath})");
        }
    }

    [Test]
    [TestCaseSource(nameof(CorpusFiles))]
    public void ManagedSqltestFileMatchesTheCorpus(string relativePath)
    {
        var discovered = SqltestCorpus.Cases
            .Where(candidate => candidate.RelativePath == relativePath)
            .ToList();
        var runnable = discovered.Where(static candidate => candidate.Status == SqltestCaseStatus.Runnable).ToList();
        if (runnable.Count == 0)
        {
            Assert.Ignore(
                $"{discovered[0].Status}: {discovered[0].Reason ?? "every case in this file is skipped by the corpus"}");
        }

        var file = SqltestCorpus.LoadFile(relativePath, runnable[0].FullPath);
        var problems = new List<string>();
        foreach (var candidate in runnable)
        {
            var test = file.Tests.Single(entry => entry.Name == candidate.TestName);
            var outcome = SqltestManagedRunner.Run(file, test);
            var isExpectedFailure = SqltestCorpus.ExpectedFailures.ContainsKey(candidate.Id);

            if (outcome.Matched && isExpectedFailure)
            {
                problems.Add(
                    $"{candidate.Id} now passes; remove it from {Path.GetFileName(SqltestCorpus.ExpectedFailuresSourcePath)}");
            }
            else if (!outcome.Matched && !isExpectedFailure)
            {
                problems.Add($"{candidate.Id} failed: {outcome.Detail}");
            }
        }

        if (problems.Count != 0)
            Assert.Fail(string.Join(Environment.NewLine, problems));
    }

    [Test]
    [Explicit("Regenerates Conformance/managed-sqltest-expected-failures.txt from current engine behavior.")]
    public void RegenerateExpectedFailures()
    {
        var failures = new List<string>();
        foreach (var discovered in SqltestCorpus.Cases)
        {
            if (discovered.Status != SqltestCaseStatus.Runnable)
                continue;

            var file = SqltestCorpus.LoadFile(discovered.RelativePath, discovered.FullPath);
            var test = file.Tests.Single(candidate => candidate.Name == discovered.TestName);
            SqltestOutcome outcome;
            try
            {
                outcome = SqltestManagedRunner.Run(file, test);
            }
            catch (Exception exception)
            {
                outcome = new SqltestOutcome(false, exception.Message);
            }

            if (!outcome.Matched)
                failures.Add($"{discovered.Id} | {Summarize(outcome.Detail)}");
        }

        var contents = new StringBuilder()
            .AppendLine("# Cases in sqlite/conformance/sqlite-sqltests that the managed engine does not yet satisfy.")
            .AppendLine("# Format: <relative path>::<test name> | <summary of the observed difference>")
            .AppendLine("# Regenerate with:")
            .AppendLine("#   dotnet test src/Ahtola.Tests/Ahtola.Tests.csproj \\")
            .AppendLine("#     --filter FullyQualifiedName~RegenerateExpectedFailures -- NUnit.Explicit=true")
            .AppendLine();
        foreach (var failure in failures)
            contents.AppendLine(failure);

        File.WriteAllText(SqltestCorpus.ExpectedFailuresSourcePath, contents.ToString());
        TestContext.Out.WriteLine(
            $"wrote {failures.Count} expected failures to {SqltestCorpus.ExpectedFailuresSourcePath}");
    }

    private static string Summarize(string detail)
    {
        var firstLine = detail.ReplaceLineEndings("\n").Split('\n')[0].Replace('|', '/').Trim();
        var summary = firstLine.Length <= 200 ? firstLine : firstLine[..200];
        var escaped = new StringBuilder(summary.Length);
        foreach (var character in summary)
        {
            if (!char.IsControl(character))
            {
                escaped.Append(character);
            }
            else if (character == '\0')
            {
                escaped.Append("\\0");
            }
            else
            {
                escaped.Append($"\\u{(int)character:X4}");
            }
        }

        return escaped.ToString();
    }

    [Test]
    public void EveryExpectedFailureRefersToADiscoveredRunnableCase()
    {
        var runnable = SqltestCorpus.Cases
            .Where(static discovered => discovered.Status == SqltestCaseStatus.Runnable)
            .Select(static discovered => discovered.Id)
            .ToHashSet(StringComparer.Ordinal);

        SqltestCorpus.ExpectedFailures.Keys
            .Where(id => !runnable.Contains(id))
            .Should()
            .BeEmpty("stale expected-failure entries hide corpus coverage changes");
    }

    [Test]
    public void HarnessExclusionsAreReviewableAndReferToUnsupportedCases()
    {
        var unsupported = SqltestCorpus.Cases
            .Where(static discovered => discovered.Status == SqltestCaseStatus.UnsupportedHarness)
            .ToDictionary(static discovered => discovered.Id, StringComparer.Ordinal);

        SqltestCorpus.HarnessExclusions.Should().ContainSingle(
            "in-process exclusions must stay exceptional and individually reviewed");
        foreach (var (id, reason) in SqltestCorpus.HarnessExclusions)
        {
            unsupported.Should().ContainKey(id, "stale exclusions silently shrink conformance coverage");
            reason.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Test]
    public void ExpectedFailureBaselineContainsNoRawControlCharacters()
    {
        File.ReadLines(SqltestCorpus.ExpectedFailuresSourcePath)
            .SelectMany(static line => line)
            .Where(char.IsControl)
            .Should()
            .BeEmpty("control characters make the text baseline impossible to review reliably");
    }

    [Test]
    public void ManagedConformanceCoverageIsReported()
    {
        var cases = SqltestCorpus.Cases;
        var runnable = cases.Count(static discovered => discovered.Status == SqltestCaseStatus.Runnable);
        var expectedFailures = SqltestCorpus.ExpectedFailures.Count;
        var passing = runnable - expectedFailures;

        var summary = new StringBuilder()
            .AppendLine($"corpus root:          {SqltestCorpus.CorpusRoot}")
            .AppendLine($"discovered cases:     {cases.Count}")
            .AppendLine($"runnable cases:       {runnable}")
            .AppendLine($"expected failures:    {expectedFailures}")
            .AppendLine($"passing cases:        {passing}")
            .AppendLine($"skipped by corpus:    {cases.Count(static discovered => discovered.Status == SqltestCaseStatus.SkippedByCorpus)}")
            .AppendLine($"unsupported harness:  {cases.Count(static discovered => discovered.Status == SqltestCaseStatus.UnsupportedHarness)}");
        TestContext.Out.Write(summary.ToString());

        // The corpus is discovered rather than enumerated here, so these floors guard
        // against a discovery or routing regression silently shrinking managed coverage.
        cases.Count.Should().BeGreaterThan(7000);
        runnable.Should().BeGreaterThan(5000);
        passing.Should().BeGreaterThan(4000);
    }
}
