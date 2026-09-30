using Ahtola.Tests.Sqltest;
using AwesomeAssertions;

namespace Ahtola.Tests;

[Category("CoverageExcluded")]
public class SqltestPhysicalFixtureTests
{
    [TestCase("integrity_check/parity_corrupt_index.sqltest", "Stored index 'idx_b'")]
    [TestCase("integrity_check/parity_corrupt_expression_index.sqltest", "Stored index 'idx_expr'")]
    [TestCase("integrity_check/parity_corrupt_partial_index.sqltest", "Stored index 'idx_partial'")]
    [TestCase("integrity_check/parity_non_unique_index.sqltest", "duplicate non-NULL keys")]
    [TestCase("integrity_check/parity_missing_unique_index.sqltest", "does not match table")]
    [TestCase("integrity_check/parity_freelist_count_mismatch.sqltest", "freelist header declares")]
    [TestCase("integrity_check/parity_freelist_trunk_corrupt.sqltest", "exceeding capacity")]
    [TestCase("integrity_check/parity_overflow_list_length_mismatch.sqltest", "overflow chain ends")]
    public void FixtureConstructionSucceedsBeforeExpectedEngineRejection(
        string relativePath, string diagnostic, bool duringOpen = true)
    {
        var discovered = SqltestCorpus.Cases.First(candidate => candidate.RelativePath == relativePath);
        discovered.Status.Should().Be(SqltestCaseStatus.Runnable);
        var file = SqltestCorpus.LoadFile(relativePath, discovered.FullPath);
        var fixture = file.Databases.Single();
        fixture.Path.Should().NotBeNullOrEmpty();

        var path = SqltestPhysicalFixtures.Materialize(fixture.Path!);
        File.Exists(path).Should().BeTrue();

        var test = file.Tests.Single(candidate => candidate.Name == discovered.TestName);
        var outcome = SqltestManagedRunner.Run(file, test);
        outcome.Matched.Should().BeFalse();
        outcome.Detail.Should().Contain(diagnostic);
        outcome.Detail.Should().Contain(duringOpen
            ? "database failed to open:"
            : "expected success but got error:");
    }

    // Turso "report STRICT type violations from integrity_check": each fixture is a real
    // managed-written STRICT (or generated-column) table with one first-row serial type
    // swapped, and PRAGMA integrity_check/quick_check must report exactly upstream's rows.
    [TestCase("integrity_check/parity_strict_type_violation.sqltest", "non-TEXT value in t.b")]
    [TestCase("integrity_check/parity_strict_gencol_type_violation.sqltest", "non-INT value in t.b")]
    [TestCase("integrity_check/parity_strict_not_null_violation.sqltest", "NULL value in t.b")]
    [TestCase("integrity_check/parity_strict_real_integer_serial.sqltest", "ok")]
    [TestCase("integrity_check/parity_gencol_not_null_violation.sqltest", "NULL value in t.b")]
    public void StoredValueViolationsAreReportedByIntegrityCheck(string relativePath, string expected)
    {
        var discovered = SqltestCorpus.Cases.Where(candidate => candidate.RelativePath == relativePath).ToList();
        discovered.Should().NotBeEmpty();
        discovered.Should().OnlyContain(candidate => candidate.Status == SqltestCaseStatus.Runnable);
        var file = SqltestCorpus.LoadFile(relativePath, discovered[0].FullPath);
        foreach (var candidate in discovered)
        {
            var test = file.Tests.Single(entry => entry.Name == candidate.TestName);
            var outcome = SqltestManagedRunner.Run(file, test);
            outcome.Matched.Should().BeTrue($"{candidate.Id} expects '{expected}': {outcome.Detail}");
        }
    }
}
